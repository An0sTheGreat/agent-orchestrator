using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using NAudio;
using NAudio.Dsp;
using NAudio.Wave;

namespace AgentOrchestrator;

internal enum CallState { Idle, Listening, Thinking, Speaking, Paused }
internal enum CallEvent { Start, End, Sent, Speak, Done, Interrupt, Mute, Unmute }

// Call mode (conversation only): listen (the dictation capture + VAD, the mic open for the whole call) → each phrase is sent as a
// message in the call's chat (the Chat conversation shown, when it has messages; otherwise a fresh "Call · <date, time>") through the normal send path with
// "voice": true → the reply's sentences are spoken with the main voice as they stream → listening again. The chat holds the
// transcript as it goes; EndCall leaves it, with a toast that opens it. The mic is not listened to while the model speaks.
public partial class MainWindow
{
    private CallState _callNow;
    private bool _callMuted;
    private string _callTitle = "";
    private WaveInEvent? _callMic;
    private BlockingCollection<float[]>? _callQueue;     // capture → the feeding thread
    private BlockingCollection<string>? _callSentences;  // the reply's sentences → the player (null: nothing to speak)
    private CancellationTokenSource? _callSpeech;
    private int _callQueued;                              // the reply's sentences already handed to the player
    private long _callLevelsAt;
    // --ui-smoke: replaces RunTurn for a phrase (no llama-server); it runs the turn through StartChatTurn and FinishChatTurn.
    private Action<string>? _callTurnStub;
    // A call in a Chatterbox voice makes its chat model and the voice ready together (PrepareCallAsync): the first phrase waits
    // for the model, then for the warm-up (the two never share the GPU at once). _callKeepsVoice: the voice fits beside the model,
    // so the call's turns keep the engine (FreeGpuForChat); _callStandIn: it does not, so the call reads with the Kokoro stand-in.
    // The call's model stays loaded between its turns whatever Keep models loaded says (EndCall stops it when that is off).
    private Task _callModelReady = Task.CompletedTask, _callVoiceReady = Task.CompletedTask;
    private bool _callKeepsVoice, _callStandIn, _callPreparing;
    private System.Diagnostics.Stopwatch _callTurnClock = new();
    private TimeSpan? _callFirstToken;
    // --ui-smoke: the model load, the warm-up, the free GPU memory and the model's estimate (bytes, name, context) the call sees
    private Func<Task>? _callLoadStub, _callWarmStub;
    private Func<long?>? _callFreeStub;
    private Func<(long Bytes, string Model, long Context)?>? _callSizeStub;
    private Func<bool>? _callServesStub; // whether a loaded server runs VOICE's own GGUF
    // A call continuing a chat: the chat's own worker summarizes it first (CallCatchUp); the first phrase and VOICE's model load
    // wait for it. --ui-smoke: _callSummaryStub (worker, request) → the engine's output lines, in place of the process.
    private Task _callSummaryReady = Task.CompletedTask;
    private bool _callCatchingUp;
    private Func<string, string, Task<List<string>>>? _callSummaryStub;
    private const int CallSummaryMin = 6; // fewer messages since the last summary: VOICE gets them as they are


    internal event Action<CallState>? CallStateChanged;
    internal event Action<float[], bool>? CallLevels; // 24 band energies 0..1; true: the model's voice, false: the microphone

    internal bool InCall => _callNow != CallState.Idle;
    internal CallState CallNow => _callNow;

    // The state machine, pure: muted holds through thinking and speaking, so a reply ends in Paused rather than Listening.
    internal static CallState CallNext(CallState s, CallEvent e, bool muted) => (s, e) switch
    {
        (_, CallEvent.End) => CallState.Idle,
        (CallState.Idle, CallEvent.Start) => muted ? CallState.Paused : CallState.Listening,
        (CallState.Idle, _) => CallState.Idle,
        (_, CallEvent.Sent) => CallState.Thinking,
        (CallState.Thinking, CallEvent.Speak) => CallState.Speaking,
        (CallState.Thinking or CallState.Speaking, CallEvent.Done or CallEvent.Interrupt) => muted ? CallState.Paused : CallState.Listening,
        (CallState.Listening, CallEvent.Mute) => CallState.Paused,
        (CallState.Paused, CallEvent.Unmute) => CallState.Listening,
        _ => s,
    };

    private void CallFire(CallEvent e)
    {
        var next = CallNext(_callNow, e, _callMuted);
        if (next == _callNow)
            return;
        _callNow = next;
        CallStateChanged?.Invoke(next);
    }

    internal bool CallReady(out string reason)
    {
        var hear = _dictationTranscribe is not null || SpeechModels.Ready;
        var speak = _speechTest || TtsModels.Ready;
        reason = hear && speak ? "" : "Download the speech-to-text and voice models in Settings > Voice";
        return reason.Length == 0;
    }

    internal void StartCall()
    {
        if (InCall)
            return;
        if (!CallReady(out var reason))
        {
            Toast(reason);
            return;
        }
        StopDictation();
        StopSpeaking();
        var queue = new BlockingCollection<float[]>();
        void Feed(float[] samples) // the capture thread
        {
            try { queue.Add(samples); }
            catch (InvalidOperationException) { } // ended: the device's last buffers
        }
        if (_dictationFeed is null)
        {
            try { _callMic = OpenMicrophone(Feed); }
            catch (MmException exception)
            {
                Log($"Call: the microphone did not open: {exception.Result}");
                Toast(WaveInEvent.DeviceCount == 0 ? "No microphone found"
                    : "The microphone could not be opened: check Windows Settings > Privacy & security > Microphone", "#FF667A");
                return;
            }
        }
        else
            _dictationFeed(Feed);
        _callQueue = queue;
        var continued = NavChat.IsChecked == true && _chatHistory.Count > 0 && !ChatTurnShown; // the chat shown goes on as the call
        if (!continued)
            NewChat_Click(this, new RoutedEventArgs());
        _callTitle = continued ? "" : $"Call · {Now():d MMM, HH:mm}"; // "": the continued chat keeps its title
        _callMuted = false;
        Player.Played = samples => CallLevelsOf(samples, Tts.Rate, speaking: true);
        _ = Task.Run(() => CallFeeding(queue));
        CallFire(CallEvent.Start);
        _callSummaryReady = continued && _chatHistory.Count - (_chat.Summary is null ? 0 : _chat.SummaryCovers) >= CallSummaryMin ? CallCatchUp() : Task.CompletedTask;
        if (CallVoice() is { Chatterbox: not null } voice)
        {
            var model = new TaskCompletionSource();
            (_callModelReady, _callPreparing) = (model.Task, true);
            var ready = _callVoiceReady = PrepareCallAsync(voice, model);
            ShowCallState(_callNow);
            _ = ready.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (_callVoiceReady != ready)
                    return;
                _callPreparing = false;
                ShowCallState(_callNow);
            }));
        }
    }

    // The call's worker: VOICE (on LEAD's model until it has its own).
    private RoleCard? CallCard() => CardFor(LauncherLogic.Voice) ?? CardFor("planner");

    // The chat's worker (its model still loaded) summarizes the chat as /compact does, in a quiet engine process of its own; the
    // summary is stored as the chat's, so the call's turns send it and their own messages. A failure leaves the history as it is.
    private async Task CallCatchUp()
    {
        var (c, role) = (_chat, ChatTarget()?.Role ?? "planner");
        var opened = c.Opened;
        var request = LauncherLogic.BuildChatRequest(role, c.History.Append(new LauncherLogic.ChatMessage("user", "/compact")), summary: c.Summary ?? "", summaryCovers: c.SummaryCovers);
        _callCatchingUp = true;
        ShowCallState(_callNow);
        try
        {
            var output = await (_callSummaryStub?.Invoke(role, request) ?? CallSummaryRun(request));
            if (output.Select(LauncherLogic.TryParseEvent).LastOrDefault(e => e is { } x && Str(x, "e") == "history_compacted") is { } done
                && Str(done, "summary") is { Length: > 0 } summary && c.Opened == opened)
                (c.Summary, c.SummaryCovers) = (summary, (int)Num(done, "covers"));
            else
                Log("Call: the chat could not be summarized; the call sends its history as it is");
        }
        catch (Exception x) when (x is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Log($"Call: the chat could not be summarized ({x.Message}); the call sends its history as it is");
        }
        finally
        {
            _callCatchingUp = false;
            ShowCallState(_callNow);
        }
    }

    // One engine process for the summary (as LoadRoleAsync's): its output lines, logged but for the events.
    private async Task<List<string>> CallSummaryRun(string request)
    {
        if (_workflowRoot is null || !RuntimeReady())
            return [];
        var file = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-summary-{Guid.NewGuid():N}.json");
        var prepared = LauncherLogic.PrepareChatFolder(SelectedProjectPath(), _workflowRoot, CurrentWorkers(), out _);
        try
        {
            File.WriteAllText(file, request);
            var info = LauncherLogic.BuildChatStartInfo(LauncherLogic.FindPython(), _workflowRoot, prepared.Path, file);
            ApplyRuntime(info);
            using var process = new System.Diagnostics.Process { StartInfo = info };
            var output = new ConcurrentQueue<string>();
            process.OutputDataReceived += (_, a) => { if (a.Data is not null) output.Enqueue(a.Data); };
            process.ErrorDataReceived += (_, a) => { if (a.Data is not null) output.Enqueue(a.Data); };
            Log($"Call: summarizing the chat in {prepared.Path}");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            foreach (var line in output.Where(l => !l.StartsWith(LauncherLogic.EventPrefix, StringComparison.Ordinal)))
                Log(line);
            return output.ToList();
        }
        finally
        {
            try { File.Delete(file); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
            if (prepared.IsTemp)
                try { Directory.Delete(prepared.Path, true); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        }
    }

    private VoiceChoice CallVoice() => VoiceChoice.Find(_settings.Tts.Voice, _settings.CustomVoices);

    private long? CallFreeGpu() => _callFreeStub is { } stub ? stub() : Chatterbox.FreeGpuBytes();

    // The fit check (a toast once when the voice does not fit beside the model), the call's model loaded when it is not (Load
    // models' path), then the voice warmed up. VOICE on LEAD's model: any loaded model is LEAD's. VOICE with its own: it is
    // loaded unless a server runs its GGUF; with Several models "never" that load replaces the loaded model, whose memory the
    // check cannot count before, so the check waits for the load and measures the room then.
    private async Task PrepareCallAsync(VoiceChoice voice, TaskCompletionSource modelReady)
    {
        var cb = voice.Chatterbox!;
        try
        {
            var card = CallCard();
            var size = card is null ? null : await CallModelSize(card);
            var loaded = ModelsLoaded && (card is not { Role: LauncherLogic.Voice, FollowsMain: false } || (_callServesStub?.Invoke() ?? LauncherLogic.ServesModel(_llamaStateFile, CallGguf(card))));
            var replaces = ModelsLoaded && !loaded && _settings.MultiModel == "never";
            async Task Fit(bool modelLoaded)
            {
                var free = _callFreeStub is { } stub ? stub() : await Task.Run(() => Chatterbox.FreeGpu.Get(wait: true));
                _callKeepsVoice = true;
                if (size is { Remote: false } sized && Chatterbox.CallRoom(free, Chatterbox.Loaded, modelLoaded, sized.Bytes) is { } room && room < Chatterbox.NeedBytes)
                {
                    (_callKeepsVoice, _callStandIn) = (false, true);
                    var text = Chatterbox.CallWontFitText(cb.Name, cb.Fallback, sized.Model, sized.Context, room);
                    Log("Call: " + text);
                    Toast(text, "#F7C873");
                }
            }
            if (!replaces)
                await Fit(loaded);
            if (InCall && !loaded && card is not null && size is not { Remote: true })
            {
                await _callSummaryReady; // the chat's model summarizes first: VOICE's load may replace it
                if (InCall)
                    await (_callLoadStub?.Invoke() ?? LoadRoleAsync(card.Role, card.Title));
            }
            if (replaces && InCall)
                await Fit(true);
        }
        finally { modelReady.TrySetResult(); }
        if (InCall && _callKeepsVoice)
            await (_callWarmStub?.Invoke() ?? Chatterbox.WarmUpAsync());
    }

    // The worker's resolved GGUF, or null (remote, unreadable settings).
    private string? CallGguf(RoleCard card)
    {
        try { return WebState(card) is { Remote: null } state ? LauncherLogic.ResolveGguf(state.Load, state.Folder, state.Hub) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NullReferenceException or InvalidOperationException) { return null; }
    }

    // The worker's GPU estimate as its settings show it (VramEstimate), its model's name and context; null: unknown.
    private async Task<(bool Remote, long Bytes, string Model, long Context)?> CallModelSize(RoleCard card)
    {
        if (_callSizeStub is { } stub)
            return stub() is { } s ? (false, s.Bytes, s.Model, s.Context) : null;
        WebWorkerState state;
        try { state = WebState(card); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NullReferenceException or InvalidOperationException) { return null; }
        if (state.Remote is not null)
            return (true, 0, "", 0);
        var gguf = LauncherLogic.ResolveGguf(state.Load, state.Folder, state.Hub);
        var (meta, files, head) = await Task.Run(() => VramEstimate.Read(gguf));
        var options = ModelOptions.Prefill(state.Current, ModelOptions.Defaults(meta, state.Load?["model_path"] is not null, state.Inherited))
            .ToDictionary(p => p.Key, p => p.Value.Trim());
        if (VramEstimate.Estimate(meta, files.Model, files.Vision, options, head) is not { } estimate)
            return null;
        var context = long.TryParse(options.GetValueOrDefault("max_seq_length", ""), out var c) && c > 0 ? c : Math.Min(ModelOptions.ModelContext(meta) ?? 4096, ModelOptions.ContextCap);
        return (false, estimate.Total, Path.GetFileNameWithoutExtension(gguf) ?? card.Title, context);
    }

    // The feeding thread: one DictationSession per listening spell (a new one when one ends on its own after silence); samples
    // are dropped unless listening, and the spell in progress is dropped with them.
    private void CallFeeding(BlockingCollection<float[]> queue)
    {
        Func<float[], string> transcribe;
        if (_dictationTranscribe is { } fake)
            transcribe = fake;
        else
        {
            var recognizer = new Lazy<Func<float[], string>>(Speech.Recognizer);
            _ = Task.Run(() => recognizer.Value); // loads while the user starts talking
            transcribe = samples => recognizer.Value(samples);
        }
        DictationSession? session = null;
        void Drop() { var s = session; session = null; s?.Dispose(); }
        try
        {
            foreach (var samples in queue.GetConsumingEnumerable())
            {
                // read off the UI thread: a stale read costs one 50 ms buffer. Interrupt by talking listens over the speech too.
                var over = _callNow == CallState.Speaking && _settings.Tts.CallBargeIn;
                if (_callNow != CallState.Listening && !over)
                {
                    Drop();
                    continue;
                }
                if (!over) // the voice's own levels show while it speaks
                    CallLevelsOf(samples, Speech.Rate, speaking: false);
                if (session is null)
                {
                    var made = session = _dictationTranscribe is null ? new DictationSession(transcribe) : new DictationSession(transcribe, vad: false);
                    made.Text += phrase => Dispatcher.BeginInvoke(() => CallPhrase(phrase));
                    made.Ended += _ => { if (session == made) session = null; }; // on this thread, inside Feed
                    made.Heard += () => Dispatcher.BeginInvoke(() => { if (_callNow == CallState.Speaking && _settings.Tts.CallBargeIn) InterruptCall(); });
                }
                session.Feed(samples);
            }
        }
        catch (Exception exception) // the recognizer failed to load or decode
        {
            Dispatcher.BeginInvoke(() =>
            {
                Log($"Call failed: {exception.Message}");
                Toast("The call failed: " + exception.Message, "#FF667A");
                if (_callQueue == queue)
                    EndCall();
            });
        }
        finally
        {
            try { Drop(); }
            catch (Exception) { } // a recognizer that failed fails again on the flush: already reported
        }
    }

    private void CallLevelsOf(float[] samples, int rate, bool speaking)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _callLevelsAt) < 16 || samples.Length == 0) // at most ~60 a second
            return;
        Interlocked.Exchange(ref _callLevelsAt, now);
        var bands = CallBands(samples, rate);
        Dispatcher.BeginInvoke(() => CallLevels?.Invoke(bands, speaking));
    }

    // A phrase heard: it goes into the call's chat as the user's message (a busy chat drops it, with a toast).
    private void CallPhrase(string phrase)
    {
        phrase = phrase.Trim();
        if (_callNow != CallState.Listening || phrase.Length == 0)
            return;
        if (_chatBusy)
        {
            Toast("Wait for the reply to finish", "#F7C873");
            return;
        }
        CallFire(CallEvent.Sent);
        Forget(CallWhenReady(phrase));
    }

    // A phrase waits for the call's model, then for its voice's warm-up ("Getting the voice ready…"), and a voice that left the
    // GPU since (idle, room) is warmed again first: Chatterbox never loads while the model generates, as they contend on the
    // GPU. Ended or interrupted meanwhile, it is not sent.
    private async Task CallWhenReady(string phrase)
    {
        await _callSummaryReady;
        await _callModelReady;
        await _callVoiceReady;
        if (_callNow == CallState.Thinking && _callKeepsVoice && CallVoice().Chatterbox is not null)
            await (_callWarmStub?.Invoke() ?? Chatterbox.WarmUpAsync()); // nothing when it is loaded
        if (_callNow != CallState.Thinking)
            return;
        if (_callTurnStub is { } stub)
            stub(phrase);
        else
            await CallTurn(phrase);
    }

    private async Task CallTurn(string phrase)
    {
        await RunTurn(_chat, phrase, CallCard()!.Role, role => ChatRequest(role, phrase), LauncherLogic.BuildChatStartInfo, "Call");
        if (_callNow == CallState.Thinking && _callSentences is null) // refused before it started (a model loading, no runtime)
            CallFire(CallEvent.Done);
    }

    // The end of StartTurn: a turn in the Chat view during a call (a phrase, or a message typed into the call's chat) is spoken.
    private void CallTurnStarted(Conversation c)
    {
        if (!InCall || c != _chat)
            return;
        _turnTitle = _callTitle; // no model title on call turns: the chat is named for the call
        CallFire(CallEvent.Sent);
        var sentences = _callSentences = new BlockingCollection<string>();
        var cancel = _callSpeech = new CancellationTokenSource();
        _callQueued = 0;
        (_callTurnClock, _callFirstToken) = (System.Diagnostics.Stopwatch.StartNew(), null);
        Forget(CallSpeak(sentences, cancel));
    }

    private async Task CallSpeak(BlockingCollection<string> sentences, CancellationTokenSource cancel)
    {
        var chosen = CallVoice();
        var voice = _callStandIn ? chosen.Kokoro : chosen; // a voice that does not fit: the stand-in, without a try each sentence
        string? fell = null;
        TimeSpan? firstAudio = null;
        void OnFell(string note) => fell = note;
        // Speaking once the output reads the first sound, not when a sentence is handed over (the voice may still be making it)
        void Audible() => Dispatcher.BeginInvoke(() =>
        {
            firstAudio = _callTurnClock.Elapsed;
            if (_callSentences == sentences)
                CallFire(CallEvent.Speak);
        });
        SpeechPlayer.FellBack += OnFell;
        try
        {
            await Player.SpeakStream(sentences.GetConsumingEnumerable(cancel.Token), voice, _settings.Tts, cancel.Token, Audible, line => Log("Call: " + line));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Log($"Call: speaking failed: {exception.Message}");
            Toast(exception is InvalidOperationException ? exception.Message : "Speaking failed: " + exception.Message, "#FF667A");
        }
        finally { SpeechPlayer.FellBack -= OnFell; }
        // which engine and voice read it (each reply tries Chatterbox again: a fallback lasts one reply)
        Log((voice.Chatterbox is { } cb && fell is null ? $"Call: the reply was read by Chatterbox in {cb.Name}"
            : $"Call: the reply was read by Kokoro in {voice.Kokoro.Name}{(_callStandIn ? $" (the stand-in for {chosen.Chatterbox?.Name})" : "")}{(fell is null ? "" : " — " + fell)}")
            + (firstAudio is { } heard ? $"; first audio {heard.TotalSeconds:0.0} s after the turn started" : "; no audio"));
        if (_callSentences == sentences) // not interrupted, ended or replaced by a newer turn meanwhile
        {
            _callSentences = null;
            CallFire(CallEvent.Done);
        }
    }

    // The reply so far (AppendChatDelta): each sentence goes to the player once the next one has started.
    private void CallDelta(string answer)
    {
        if (_turn == _chat && _callSentences is not null)
            _callFirstToken ??= _callTurnClock.Elapsed;
        CallHandOver(answer, final: false);
    }

    // FinishChatTurn: the rest of the reply (null: none, failed or stopped) and the end of the sentences.
    private void CallTurnFinished(Conversation c, string? answer)
    {
        if (c != _chat || _callSentences is not { } sentences)
            return;
        Log($"Call: the reply's text took {_callTurnClock.Elapsed.TotalSeconds:0.0} s, first token after {(_callFirstToken is { } first ? $"{first.TotalSeconds:0.0} s" : "none")}"
            + (_reply is { } r && (r.Pending ?? r.Last) is { } stats ? $" ({stats})" : ""));
        if (answer is not null)
            CallHandOver(answer, final: true);
        sentences.CompleteAdding();
    }

    // ponytail: re-splits the whole reply on every delta (quadratic in its length); spoken replies are a few sentences.
    private void CallHandOver(string answer, bool final)
    {
        if (_callSentences is not { IsAddingCompleted: false } sentences || _turn != _chat)
            return;
        var all = SpeechText.Sentences(SpeechText.ForSpeech(answer, _settings.Tts));
        for (var ready = final ? all.Count : all.Count - 1; _callQueued < ready; _callQueued++)
            sentences.Add(all[_callQueued]);
    }

    // Speech stops (and a reply still streaming is not spoken) and listening starts.
    internal void InterruptCall()
    {
        if (_callNow is not (CallState.Speaking or CallState.Thinking))
            return;
        Log($"Call: interrupted while {_callNow}");
        StopCallSpeech();
        CallFire(CallEvent.Interrupt);
    }

    internal void ToggleCallMute()
    {
        if (!InCall)
            return;
        _callMuted = !_callMuted;
        CallFire(_callMuted ? CallEvent.Mute : CallEvent.Unmute);
    }

    private void StopCallSpeech()
    {
        _callSentences = null;
        _callSpeech?.Cancel();
        _speechPlayer?.Stop();
    }

    // Idempotent: the mic released, speech stopped; the chat stays (saved as it went), with a toast that opens it.
    internal void EndCall()
    {
        if (!InCall)
            return;
        _callMic?.Dispose();
        _callMic = null;
        _callQueue?.CompleteAdding();
        _callQueue = null;
        StopCallSpeech();
        if (_speechPlayer is { } player)
            player.Played = null;
        (_callMuted, _callKeepsVoice, _callStandIn, _callPreparing) = (false, false, false, false);
        (_callModelReady, _callVoiceReady, _callSummaryReady) = (Task.CompletedTask, Task.CompletedTask, Task.CompletedTask);
        CallFire(CallEvent.End);
        if (!_settings.ChatKeepLoaded && !ChatModelsInUse) // kept loaded through the call; a turn still running stops it when it finishes
            StopLocalServer();
        if (_chatConv is { } saved && saved.Title == _callTitle)
            Toast($"Call saved to {_callTitle}", click: () => OpenChatConversation(saved.Id));
        else
            Toast("Call ended");
    }

    // 24 log-spaced band energies (80 Hz to 8 kHz) of the newest 1024 samples (zero-padded), 0..1 over -60..0 dB.
    internal static float[] CallBands(float[] samples, int rate)
    {
        const int m = 10, n = 1 << m, count = 24;
        var data = new Complex[n];
        var from = Math.Max(0, samples.Length - n);
        for (var i = 0; i < n && from + i < samples.Length; i++)
            data[i].X = (float)(samples[from + i] * FastFourierTransform.HannWindow(i, n));
        FastFourierTransform.FFT(true, m, data);
        var bands = new float[count];
        for (var b = 0; b < count; b++)
        {
            double lo = 80 * Math.Pow(100, (double)b / count), hi = 80 * Math.Pow(100, (double)(b + 1) / count);
            int first = Math.Clamp((int)(lo * n / rate), 1, n / 2 - 1), last = Math.Clamp((int)Math.Ceiling(hi * n / rate), first + 1, n / 2);
            double peak = 0;
            for (var k = first; k < last; k++)
                peak = Math.Max(peak, Math.Sqrt(data[k].X * data[k].X + data[k].Y * data[k].Y));
            var db = 20 * Math.Log10(peak * 4 + 1e-9); // NAudio's FFT scales by 1/n; a full-scale sine under the Hann window peaks near 1/4
            bands[b] = (float)Math.Clamp((db + 60) / 60, 0, 1);
        }
        return bands;
    }

    // --self-test: the state machine and the bands.
    internal static void CallSelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Call self-test failed: " + what);
        }
        var s = CallState.Idle;
        foreach (var (e, want) in new[] { (CallEvent.Sent, CallState.Idle), (CallEvent.Start, CallState.Listening), (CallEvent.Sent, CallState.Thinking),
                     (CallEvent.Speak, CallState.Speaking), (CallEvent.Speak, CallState.Speaking), (CallEvent.Done, CallState.Listening),
                     (CallEvent.Mute, CallState.Paused), (CallEvent.Speak, CallState.Paused), (CallEvent.Unmute, CallState.Listening),
                     (CallEvent.Sent, CallState.Thinking), (CallEvent.Interrupt, CallState.Listening), (CallEvent.Done, CallState.Listening),
                     (CallEvent.End, CallState.Idle), (CallEvent.Unmute, CallState.Idle) })
        {
            var next = CallNext(s, e, muted: false);
            Check(next == want, $"{s} + {e} → {next}, not {want}");
            s = next;
        }
        Check(CallNext(CallState.Speaking, CallEvent.Done, muted: true) == CallState.Paused && CallNext(CallState.Thinking, CallEvent.Interrupt, muted: true) == CallState.Paused
            && CallNext(CallState.Paused, CallEvent.Sent, muted: true) == CallState.Thinking && CallNext(CallState.Idle, CallEvent.Start, muted: true) == CallState.Paused,
            "muted: a reply ends in Paused; a typed message is still sent");
        var sine = Enumerable.Range(0, 2048).Select(i => (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / Speech.Rate))).ToArray();
        var bands = CallBands(sine, Speech.Rate);
        var top = Array.IndexOf(bands, bands.Max());
        double lo = 80 * Math.Pow(100, top / 24.0), hi = 80 * Math.Pow(100, (top + 1) / 24.0);
        Check(bands.Length == 24 && bands.Max() > 0.8 && lo <= 1100 && hi >= 900 && bands[0] < 0.5 && bands[^1] < 0.5,
            $"a 1 kHz sine: band {top} ({lo:0}-{hi:0} Hz) at {bands.Max():0.00}, ends {bands[0]:0.00}/{bands[^1]:0.00}");
        Check(CallBands(new float[512], Speech.Rate).All(b => b == 0) && CallBands([], Tts.Rate).All(b => b == 0), "silence: all bands zero");
    }

    // --ui-smoke: stub capture, recognizer, LLM and voice (never the real mic, GPU, network or a model): start → a phrase → a user
    // message in the new "Call · …" chat → the reply's sentences spoken → listening again; Interrupt during speech → listening;
    // muted → no phrase taken; End → the chat kept with both sides, the toast, the mic released. A call in a chat with messages
    // goes on in it: a short one as it is, a longer one summarized by its own worker before VOICE's model loads.
    private async Task<bool> CallSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: call: {what}"); return false; }
        var (player, test) = (_speechPlayer, _speechTest);
        var spoken = new List<string>();
        var states = new List<CallState>();
        var levels = new HashSet<bool>();
        void OnState(CallState s) => states.Add(s);
        void OnLevels(float[] b, bool speaking) { if (b.Length == 24) levels.Add(speaking); }
        var (loud, playedAtTwo, stateAtOne, generating, overlap, stops) = (0, -1, CallState.Idle, false, false, 0);
        float[] Stub(string text, VoiceChoice voice, float speed, CancellationToken ct)
        {
            lock (spoken)
                spoken.Add(text);
            if (text == "One fine day.")
            {
                Thread.Sleep(200); // the voice still making the first sentence: no sound yet, so not Speaking
                stateAtOne = _callNow;
            }
            if (text == "Two birds sang.") // made while the first sentence plays (2 s of it, ~100 ms at the fake output's pace)
                playedAtTwo = Volatile.Read(ref loud);
            if (text.StartsWith("Slow"))
                ct.WaitHandle.WaitOne(10_000); // speaking until interrupted
            return Enumerable.Repeat(0.3f, text == "One fine day." ? Tts.Rate * 2 : Tts.Rate / 2).ToArray();
        }
        string[] Spoken() { lock (spoken) return spoken.ToArray(); }
        async Task<bool> Until(Func<bool> done)
        {
            for (var i = 0; i < 100 && !done(); i++)
                await Task.Delay(40);
            return done();
        }
        Action<float[]>? feed = null;
        var reply = "";
        var (modelLoaded, free, order, savedVoice) = (false, (long?)(12L << 30), new List<string>(), _settings.Tts.Voice);
        string? cbWhy = null, lastRequest = null;
        bool? follows = null;
        string Ev(string json) => LauncherLogic.EventPrefix + json;
        void Phrase() // off the UI thread, as the microphone: a loud second, then quiet that ends the phrase
        {
            for (var i = 0; i < 10; i++)
                feed!(Enumerable.Repeat(0.5f, 1600).ToArray());
            for (var i = 0; i < 8; i++)
                feed!(new float[1600]);
        }
        _dictationFeed = f => feed = f;
        _dictationTranscribe = _ => "Hello there";
        _speechPlayer = new SpeechPlayer(Stub, () => new Tts.FakeOutput());
        _speechTest = true;
        _callTurnStub = text => // as CallTurn: RunTurn on the call's worker (VOICE), which frees the GPU for the chat model first
        {
            generating = true;
            lastRequest = ChatRequest(CallCard()!.Role, text);
            StartTurn(_chat, text, CallCard()!.Role);
            FreeGpuForChat();
            foreach (var word in reply.Split(' '))
                _lines.Enqueue(Ev($$"""{"e":"delta","role":"voice","kind":"text","text":"{{word}} "}"""));
            DrainLines();
            _lines.Enqueue(Ev($$"""{"e":"chat_reply","role":"voice","text":"{{reply}}"}"""));
            DrainLines();
            generating = false;
            FinishChatTurn(0, false, null);
        };
        var keepLoaded = _settings.ChatKeepLoaded;
        _settings.ChatKeepLoaded = false; // a call keeps its model between turns all the same, and stops it at its end
        _stopServerStub = () => stops++;
        CallStateChanged += OnState;
        CallLevels += OnLevels;
        try
        {
            if (!CallReady(out var why))
                return Fail("not ready with the stubs: " + why);
            StartCall();
            if (!InCall || CallNow != CallState.Listening || feed is null || _chatHistory.Count != 0)
                return Fail($"start: {CallNow}, feed {feed is not null}, {_chatHistory.Count} messages");
            var levelsTap = _speechPlayer.Played;
            _speechPlayer.Played = s => { Interlocked.Add(ref loud, s.Count(f => f != 0)); levelsTap?.Invoke(s); };
            reply = "One fine day. Two birds sang. Three?";
            await Task.Run(Phrase);
            if (!await Until(() => Spoken().Length >= 3 && CallNow == CallState.Listening))
                return Fail($"the reply was not spoken, or listening did not resume: {CallNow}, spoken [{string.Join("|", Spoken())}]");
            if (Spoken() is not ["One fine day.", "Two birds sang.", "Three?"] || _chatHistory is not [{ Role: "user", Content: "Hello there" }, { Role: "assistant" }]
                || _chatConv?.Title?.StartsWith("Call · ") != true || !states.Contains(CallState.Thinking) || !states.Contains(CallState.Speaking) || !levels.SetEquals([true, false]))
                return Fail($"spoken [{string.Join("|", Spoken())}], {_chatHistory.Count} messages, title {_chatConv?.Title}, states {string.Join(",", states)}, levels {string.Join(",", levels)}");
            if (stateAtOne != CallState.Thinking || playedAtTwo is < 0 or >= Tts.Rate * 2 || stops != 0)
                return Fail($"while the first sentence was made the state was {stateAtOne}, not Thinking; or the second was made after the first had played "
                    + $"({playedAtTwo} of {Tts.Rate * 2} samples); or the model was stopped between call turns ({stops} stops)");
            // Interrupt while speaking: the rest is not spoken, listening at once
            reply = "Quick one. Slow sentence here. Never said.";
            await Task.Run(Phrase);
            if (!await Until(() => CallNow == CallState.Speaking && Spoken().Contains("Slow sentence here.")))
                return Fail($"the second reply did not start speaking: {CallNow}");
            InterruptCall();
            await Task.Delay(300);
            if (CallNow != CallState.Listening || Spoken().Contains("Never said."))
                return Fail($"interrupt: {CallNow}, spoken [{string.Join("|", Spoken())}]");
            // muted: no phrase taken
            ToggleCallMute();
            var count = _chatHistory.Count;
            await Task.Run(Phrase);
            await Task.Delay(500);
            if (CallNow != CallState.Paused || _chatHistory.Count != count)
                return Fail($"muted: {CallNow}, {_chatHistory.Count - count} messages taken");
            ToggleCallMute();
            var id = _chatConv!.Id;
            if (stops != 0)
                return Fail($"the model was stopped during the call ({stops} stops)");
            EndCall();
            if (stops != 1)
                return Fail($"Keep models loaded off: the call's end stopped the model {stops} times, not once");
            var kept = _store.Get(id);
            if (InCall || CallNow != CallState.Idle || _callQueue is not null || _callMic is not null || _speechPlayer.Played is not null
                || kept?.Messages.Count != 4 || kept.Messages.Count(m => m.Role == "user") != 2 || ToastsOf($"Call saved to {kept.Title}").Count == 0)
                return Fail($"end: {CallNow}, {kept?.Messages.Count} saved messages, toast {ToastsOf($"Call saved to {kept?.Title}").Count}");
            if (CallCard()?.Role != LauncherLogic.Voice)
                return Fail($"the call's worker is {CallCard()?.Role}, not VOICE");

            // A Chatterbox voice, no model loaded: StartCall loads the call's model, then warms Chatterbox; Chatterbox reads the reply
            _settings.Tts.Voice = VoiceChoice.ChatterboxDefault;
            _speechPlayer = new SpeechPlayer(Stub, () => new Tts.FakeOutput(), new ChatterboxSeam(() => cbWhy, (text, _, _) =>
            {
                lock (spoken)
                    spoken.Add("cb:" + text);
                return new float[Tts.Rate / 2];
            }, () => overlap |= generating)); // a warm-up while the model generates: they would contend on the GPU
            _modelsLoadedStub = () => modelLoaded;
            _callLoadStub = async () => { lock (order) order.Add("load"); await Task.Delay(50); modelLoaded = true; };
            _callWarmStub = () => { lock (order) order.Add("warm"); overlap |= generating; return Task.CompletedTask; };
            _callFreeStub = () => free;
            _callSizeStub = () => (4L << 30, "Voice-4B", 8192);
            string Order() { lock (order) return string.Join(",", order); }
            async Task<string?> Reply(string text, string heard)
            {
                reply = text;
                await Task.Run(Phrase);
                return await Until(() => Spoken().Contains(heard) && CallNow == CallState.Listening) ? null : $"\"{text}\" not read as {heard}: {CallNow}, spoken [{string.Join("|", Spoken())}]";
            }
            NewChat_Click(this, new RoutedEventArgs()); // a fresh call chat (the one shown, with messages, would go on as the call)
            StartCall();
            if (!await Until(() => !_callPreparing) || Order() != "load,warm" || _callStandIn)
                return Fail($"Chatterbox voice, no model loaded: prepared {!_callPreparing}, order {Order()}, stand-in {_callStandIn}");
            if (await Reply("Chatterbox reads this.", "cb:Chatterbox reads this.") is var notRead && notRead is not null || Spoken().Contains("Chatterbox reads this."))
                return Fail("the first reply was not read by Chatterbox: " + notRead);
            // a later turn with the model loaded and 1.5 GB free keeps the engine (the rule outside calls unloads under 2 GB)
            free = 3L << 29;
            var unloaded = false;
            _freeGpuChatterbox = (() => true, () => unloaded = true); // the engine loaded (the smoke's process has shut the real one down)
            if (await Reply("Still here.", "cb:Still here.") is { } keptWhy)
                return Fail("the later turn: " + keptWhy);
            if (SpinWait.SpinUntil(() => unloaded, 600))
                return Fail("a later turn with the model loaded and 1.5 GB free unloaded Chatterbox");
            // a fallback in one reply does not stick to the next
            cbWhy = "busy";
            if (await Reply("Fallback here.", "Fallback here.") is { } fellWhy)
                return Fail("the fallback reply: " + fellWhy);
            cbWhy = null;
            if (await Reply("Back again.", "cb:Back again.") is { } backWhy)
                return Fail("the fallback stuck to the next reply: " + backWhy);
            if (overlap)
                return Fail("Chatterbox was warmed while the call's model generated");
            EndCall();
            _freeGpuChatterbox = null;

            // doesn't fit beside the model: the toast, the stand-in for every reply, no warm-up
            (modelLoaded, free) = (false, 2L << 30);
            lock (order) order.Clear();
            NewChat_Click(this, new RoutedEventArgs());
            StartCall();
            var cb = CallVoice().Chatterbox!;
            var wontFit = Chatterbox.CallWontFitText(cb.Name, cb.Fallback, "Voice-4B", 8192, Chatterbox.CallRoom(2L << 30, false, false, 4L << 30)!.Value);
            if (!await Until(() => !_callPreparing) || !_callStandIn || ToastsOf(wontFit).Count == 0 || Order() != "load")
                return Fail($"doesn't fit: stand-in {_callStandIn}, toast {ToastsOf(wontFit).Count}, order {Order()}");
            if (await Reply("Stand in.", "Stand in.") is var standWhy && standWhy is not null || Spoken().Contains("cb:Stand in."))
                return Fail("the stand-in did not read the reply: " + standWhy);
            EndCall();

            // VOICE with its own model while another (LEAD's) is loaded: VOICE's own model is loaded for the call, unless a
            // server already runs it
            var voiceCard = CardFor(LauncherLogic.Voice)!;
            follows = voiceCard.FollowsMain;
            voiceCard.FollowsMain = false;
            (modelLoaded, free) = (true, 12L << 30);
            foreach (var (served, want) in new[] { (true, "warm"), (false, "load,warm") })
            {
                lock (order) order.Clear();
                _callServesStub = () => served;
                NewChat_Click(this, new RoutedEventArgs());
                StartCall();
                if (!await Until(() => !_callPreparing) || Order() != want || _callStandIn)
                    return Fail($"VOICE's own model is loaded for the call (served {served}): order {Order()}, not {want}; stand-in {_callStandIn}");
                EndCall();
            }

            // A call in a chat with messages goes on in it: no new chat, its title kept, "Call ended". Short (4 messages): VOICE
            // gets them as they are. Longer: the chat's worker summarizes it ("Catching up on the chat…") before VOICE's model
            // loads, and the call's first request sends the summary and the call's own message.
            (_callServesStub, modelLoaded) = (null, false);
            string? summaryRequest = null, summarizedBy = null;
            _callSummaryStub = async (role, request) =>
            {
                (summaryRequest, summarizedBy) = (request, role);
                await Task.Delay(150);
                lock (order) order.Add("summary");
                return [Ev("""{"e":"history_compacted","role":"x","summary":"Earlier: birds.","covers":6,"before":90,"after":9}""")];
            };
            OpenChatConversation(id);
            var (title, worker) = (_chatConv?.Title, ChatTarget()?.Role);
            void ClearEnded() { foreach (var t in ToastsOf("Call ended").Concat(ToastsOf($"Call saved to {title}"))) ToastStack.Children.Remove(t); } // a toast replaces its twin
            bool Ended() => ToastsOf("Call ended").Count == 1 && ToastsOf($"Call saved to {title}").Count == 0;
            ClearEnded();
            lock (order) order.Clear();
            StartCall();
            if (!await Until(() => !_callPreparing) || Order() != "load,warm" || _chatConv?.Id != id || _chatHistory.Count != 4)
                return Fail($"a call in a short chat: order {Order()}, chat {_chatConv?.Id} (not {id}), {_chatHistory.Count} messages");
            if (await Reply("Short one.", "cb:Short one.") is { } shortWhy)
                return Fail("a call in a short chat: " + shortWhy);
            EndCall();
            if (_store.Get(id) is not { Messages.Count: 6 } shortChat || shortChat.Title != title || !Ended() || summaryRequest is not null
                || System.Text.Json.Nodes.JsonNode.Parse(lastRequest!)?["summary"]?.GetValue<string>() != "")
                return Fail($"a call in a short chat: {_store.Get(id)?.Messages.Count} saved messages, title {_store.Get(id)?.Title} (not {title}), "
                    + $"\"Call ended\" {Ended()}, summarized {summaryRequest is not null}");
            modelLoaded = false;
            ClearEnded();
            lock (order) order.Clear();
            StartCall();
            if (CallStateText.Text != "Catching up on the chat…")
                return Fail($"summarizing the chat shows \"{CallStateText.Text}\"");
            if (!await Until(() => !_callPreparing) || Order() != "summary,load,warm" || summarizedBy != worker || worker == LauncherLogic.Voice)
                return Fail($"a call in a longer chat: order {Order()}, summarized by {summarizedBy} (the chat's worker is {worker})");
            if (await Reply("Long one.", "cb:Long one.") is { } longWhy)
                return Fail("a call in a longer chat: " + longWhy);
            var (asked, sent) = (System.Text.Json.Nodes.JsonNode.Parse(summaryRequest!)!, System.Text.Json.Nodes.JsonNode.Parse(lastRequest!)!);
            if (asked["messages"]!.AsArray() is not { Count: 7 } askedMessages || askedMessages[^1]!["content"]!.GetValue<string>() != "/compact" || asked["worker"]!.GetValue<string>() != worker
                || sent["summary"]!.GetValue<string>() != "Earlier: birds." || sent["summary_covers"]!.GetValue<int>() != 6 || sent["messages"]!.AsArray().Count != 1 || sent["worker"]!.GetValue<string>() != LauncherLogic.Voice)
                return Fail($"a call in a longer chat: the summary asked {asked.ToJsonString()}; the first call request {sent.ToJsonString()}");
            EndCall();
            if (_store.Get(id) is not { Messages.Count: 8, Summary: "Earlier: birds.", SummaryCovers: 6 } longChat || longChat.Title != title || !Ended())
                return Fail($"a call in a longer chat: {_store.Get(id)?.Messages.Count} saved messages, summary {_store.Get(id)?.Summary}, title {_store.Get(id)?.Title}");
            return true;
        }
        finally
        {
            EndCall();
            (_settings.ChatKeepLoaded, _stopServerStub) = (keepLoaded, null);
            _freeGpuChatterbox = null;
            _settings.Tts.Voice = savedVoice;
            if (follows is { } f)
                CardFor(LauncherLogic.Voice)!.FollowsMain = f;
            (_modelsLoadedStub, _callLoadStub, _callWarmStub, _callFreeStub, _callSizeStub, _callServesStub, _callSummaryStub) = (null, null, null, null, null, null, null);
            CallStateChanged -= OnState;
            CallLevels -= OnLevels;
            (_dictationFeed, _dictationTranscribe, _callTurnStub, _speechPlayer, _speechTest) = (null, null, null, player, test);
            NewChat_Click(this, new RoutedEventArgs());
        }
    }
}

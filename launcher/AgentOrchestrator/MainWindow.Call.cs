using System.Collections.Concurrent;
using System.Windows;
using NAudio;
using NAudio.Dsp;
using NAudio.Wave;

namespace AgentOrchestrator;

internal enum CallState { Idle, Listening, Thinking, Speaking, Paused }
internal enum CallEvent { Start, End, Sent, Speak, Done, Interrupt, Mute, Unmute }

// Call mode (conversation only): listen (the dictation capture + VAD, the mic open for the whole call) → each phrase is sent as a
// message in the call's own chat ("Call · <date, time>", a fresh one made at the start) through the normal send path with
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
        NewChat_Click(this, new RoutedEventArgs());
        _callTitle = $"Call · {Now():d MMM, HH:mm}";
        _callMuted = false;
        Player.Played = samples => CallLevelsOf(samples, Tts.Rate, speaking: true);
        _ = Task.Run(() => CallFeeding(queue));
        CallFire(CallEvent.Start);
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
        if (_callTurnStub is { } stub)
            stub(phrase);
        else
            Forget(CallTurn(phrase));
    }

    private async Task CallTurn(string phrase)
    {
        await RunTurn(_chat, phrase, ChatTarget()!.Role, role => ChatRequest(role, phrase), LauncherLogic.BuildChatStartInfo, "Call");
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
        Forget(CallSpeak(sentences, cancel));
    }

    private async Task CallSpeak(BlockingCollection<string> sentences, CancellationTokenSource cancel)
    {
        try
        {
            await Player.SpeakStream(sentences.GetConsumingEnumerable(cancel.Token), VoiceChoice.Find(_settings.Tts.Voice, _settings.CustomVoices),
                _settings.Tts, cancel.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Log($"Call: speaking failed: {exception.Message}");
            Toast(exception is InvalidOperationException ? exception.Message : "Speaking failed: " + exception.Message, "#FF667A");
        }
        if (_callSentences == sentences) // not interrupted, ended or replaced by a newer turn meanwhile
        {
            _callSentences = null;
            CallFire(CallEvent.Done);
        }
    }

    // The reply so far (AppendChatDelta): each sentence goes to the player once the next one has started.
    private void CallDelta(string answer) => CallHandOver(answer, final: false);

    // FinishChatTurn: the rest of the reply (null: none, failed or stopped) and the end of the sentences.
    private void CallTurnFinished(Conversation c, string? answer)
    {
        if (c != _chat || _callSentences is not { } sentences)
            return;
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
        {
            sentences.Add(all[_callQueued]);
            CallFire(CallEvent.Speak);
        }
    }

    // Speech stops (and a reply still streaming is not spoken) and listening starts.
    internal void InterruptCall()
    {
        if (_callNow is not (CallState.Speaking or CallState.Thinking))
            return;
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
        _callMuted = false;
        CallFire(CallEvent.End);
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
    // muted → no phrase taken; End → the chat kept with both sides, the toast, the mic released.
    private async Task<bool> CallSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: call: {what}"); return false; }
        var (player, test) = (_speechPlayer, _speechTest);
        var spoken = new List<string>();
        var states = new List<CallState>();
        var levels = new HashSet<bool>();
        void OnState(CallState s) => states.Add(s);
        void OnLevels(float[] b, bool speaking) { if (b.Length == 24) levels.Add(speaking); }
        float[] Stub(string text, VoiceChoice voice, float speed, CancellationToken ct)
        {
            lock (spoken)
                spoken.Add(text);
            if (text.StartsWith("Slow"))
                ct.WaitHandle.WaitOne(10_000); // speaking until interrupted
            return Enumerable.Repeat(0.3f, Tts.Rate / 2).ToArray();
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
        _callTurnStub = text =>
        {
            StartChatTurn(text);
            foreach (var word in reply.Split(' '))
                _lines.Enqueue(Ev($$"""{"e":"delta","role":"planner","kind":"text","text":"{{word}} "}"""));
            DrainLines();
            _lines.Enqueue(Ev($$"""{"e":"chat_reply","role":"planner","text":"{{reply}}"}"""));
            DrainLines();
            FinishChatTurn(0, false, null);
        };
        CallStateChanged += OnState;
        CallLevels += OnLevels;
        try
        {
            if (!CallReady(out var why))
                return Fail("not ready with the stubs: " + why);
            StartCall();
            if (!InCall || CallNow != CallState.Listening || feed is null || _chatHistory.Count != 0)
                return Fail($"start: {CallNow}, feed {feed is not null}, {_chatHistory.Count} messages");
            reply = "One fine day. Two birds sang. Three?";
            await Task.Run(Phrase);
            if (!await Until(() => Spoken().Length >= 3 && CallNow == CallState.Listening))
                return Fail($"the reply was not spoken, or listening did not resume: {CallNow}, spoken [{string.Join("|", Spoken())}]");
            if (Spoken() is not ["One fine day.", "Two birds sang.", "Three?"] || _chatHistory is not [{ Role: "user", Content: "Hello there" }, { Role: "assistant" }]
                || _chatConv?.Title?.StartsWith("Call · ") != true || !states.Contains(CallState.Thinking) || !states.Contains(CallState.Speaking) || !levels.SetEquals([true, false]))
                return Fail($"spoken [{string.Join("|", Spoken())}], {_chatHistory.Count} messages, title {_chatConv?.Title}, states {string.Join(",", states)}, levels {string.Join(",", levels)}");
            // Interrupt while speaking: the rest is not spoken, listening at once
            reply = "Slow sentence here. Never said.";
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
            EndCall();
            var kept = _store.Get(id);
            if (InCall || CallNow != CallState.Idle || _callQueue is not null || _callMic is not null || _speechPlayer.Played is not null
                || kept?.Messages.Count != 4 || kept.Messages.Count(m => m.Role == "user") != 2 || ToastsOf($"Call saved to {kept.Title}").Count == 0)
                return Fail($"end: {CallNow}, {kept?.Messages.Count} saved messages, toast {ToastsOf($"Call saved to {kept?.Title}").Count}");
            return true;
        }
        finally
        {
            EndCall();
            CallStateChanged -= OnState;
            CallLevels -= OnLevels;
            (_dictationFeed, _dictationTranscribe, _callTurnStub, _speechPlayer, _speechTest) = (null, null, null, player, test);
            NewChat_Click(this, new RoutedEventArgs());
        }
    }
}

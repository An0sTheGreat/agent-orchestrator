using System.Buffers.Binary;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator;

// The phone app's dictation (MainWindow.WebApp.cs routes /api/dictate): the phone records and posts raw PCM (16-bit little-endian,
// mono, 16 kHz); the PC transcribes it with the shared recognizer through a DictationSession, off the UI thread, and answers
// with the phrases joined by Speech.Insert's rules. Nothing is kept: the audio lives only for the request.
public partial class MainWindow
{
    private const int WebDictateMax = 10 * 60 * Speech.Rate * 2; // 10 minutes: 19,200,000 bytes
    private const string WebDictateNotReady = "Download the speech model on the PC (Settings > Voice)";

    // The smoke's stand-in for Speech.Recognizer(), so a phrase comes back without the models (null: the real recognizer).
    private Func<float[], string>? _webTranscribe;

    private (int, object) WebDictateState() => (200, new { ready = _webTranscribe is not null || SpeechModels.Ready });

    private async Task<(int, object)> WebDictate(byte[] body)
    {
        if (body.Length > WebDictateMax)
            return (413, new { error = "Too long (10 minutes at most)" });
        if (body.Length == 0 || body.Length % 2 != 0)
            return (400, new { error = "Expected 16-bit PCM, mono, 16 kHz" });
        var stand = _webTranscribe;
        if (stand is null && !SpeechModels.Ready)
            return (409, new { error = WebDictateNotReady });
        return await Task.Run<(int, object)>(() =>
        {
            Func<float[], string> transcribe;
            try { transcribe = stand ?? Speech.Recognizer(); }
            catch (InvalidOperationException) { return (409, new { error = WebDictateNotReady }); } // the models went since the check
            try { return (200, new { text = WebTranscribe(transcribe, body) }); }
            catch (Exception exception) { return (500, new { error = "Transcription failed: " + exception.Message }); }
        });
    }

    // Fed in 100 ms chunks, as the desktop's microphone feeds it. A session ends on its own after 2 s of silence or 10 s with
    // no speech; a recording that goes on after that (the user tapped stop later) continues in a fresh session.
    private static string WebTranscribe(Func<float[], string> transcribe, byte[] pcm)
    {
        const int chunk = Speech.Rate / 10;
        var text = "";
        var ended = false;
        DictationSession? session = null;
        void Say(string phrase) => text = Speech.Insert(text, text.Length, phrase).text;
        try
        {
            for (var at = 0; at < pcm.Length; at += chunk * 2)
            {
                if (session is null)
                {
                    session = new DictationSession(transcribe);
                    session.Text += Say;
                    session.Ended += _ => ended = true;
                }
                var samples = new float[Math.Min(chunk * 2, pcm.Length - at) / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(at + 2 * i)) / 32768f;
                session.Feed(samples);
                if (ended)
                {
                    session.Dispose();
                    (session, ended) = (null, false);
                }
            }
            session?.Stop(); // the phrase in progress
        }
        finally { session?.Dispose(); }
        return text;
    }

    // /api/dictate (WebAppSmoke, while on), on the smoke's data folder, where the speech models are absent.
    private async Task<bool> WebVoiceSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: voice: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        async Task<(int Status, JsonNode? Body)> Send(byte[]? pcm, string? login = "smoke@example.com")
        {
            using var request = new HttpRequestMessage(pcm is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}/api/dictate");
            if (pcm is not null)
                request.Content = new ByteArrayContent(pcm) { Headers = { ContentType = new("application/octet-stream") } };
            if (login is not null)
                request.Headers.Add("Tailscale-User-Login", login);
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            try { return ((int)response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null); }
            catch (System.Text.Json.JsonException) { return ((int)response.StatusCode, null); }
        }
        // `loud` seconds of a -6 dB square-ish level (speech to the energy gate) then `quiet` seconds of silence.
        static byte[] Pcm(double loud, double quiet)
        {
            var bytes = new byte[2 * (int)((loud + quiet) * Speech.Rate)];
            for (var i = 0; i < (int)(loud * Speech.Rate); i++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * i), (short)(i % 2 == 0 ? 16384 : -16384));
            return bytes;
        }
        try
        {
            if ((await Send(null, null)).Status != 403 || (await Send([0, 0], null)).Status != 403)
                return Fail("no owner is not 403");
            if (await Send(null) is not { Status: 200, Body: { } state } || state["ready"]?.GetValueKind() != System.Text.Json.JsonValueKind.False)
                return Fail($"GET: {(await Send(null)).Body}");
            if ((await Send([1, 2, 3])).Status != 400 || (await Send([])).Status != 400)
                return Fail("an odd-length or empty body is not 400");
            if ((await Send(new byte[WebDictateMax + 2])).Status != 413)
                return Fail("over 10 minutes is not 413");
            if (await Send(Pcm(0, 0.5)) is not { Status: 409, Body: { } absent } || absent["error"]?.ToString() != WebDictateNotReady)
                return Fail($"no models: {(await Send(Pcm(0, 0.5))).Body}");

            _webTranscribe = _ => "hello";
            if (await Send(null) is not { Body: { } ready } || ready["ready"]?.GetValue<bool>() != true)
                return Fail("GET with a recognizer is not ready");
            if (await Send(Pcm(1, 0.6)) is not { Status: 200, Body: { } said } || said["text"]?.ToString() != "Hello")
                return Fail($"a phrase: {(await Send(Pcm(1, 0.6))).Body}");
            // a pause past a session's 2 s end, then more speech: both phrases, joined
            if (await Send([.. Pcm(1, 2.5), .. Pcm(1, 0.2)]) is not { Status: 200, Body: { } two } || two["text"]?.ToString() != "Hello hello")
                return Fail("two phrases across a session's end");
            return true;
        }
        finally { _webTranscribe = null; }
    }

    // Replies read aloud on the phone (routes /api/voice, /api/speak/plan, /api/speak): the phone asks for the reply's sentences
    // (SpeechText on the PC), then for each sentence's 24 kHz 16-bit WAV (Kokoro, Tts.Synthesize), playing sentence n while it
    // fetches n+1. Voice settings are the PC's own (AppSettings.Tts); Read replies aloud is the phone's own (its localStorage),
    // and downloads and blends stay on the PC.
    private const string WebSpeakNotReady = "Download the voice model on the PC (Settings > Voice)";
    internal const int WebSentenceMax = 600;

    // The smoke's stand-in for Tts.Synthesize, so audio comes back without the models (null: the real engine).
    private Func<string, VoiceChoice, float, CancellationToken, float[]>? _webSynthesize;

    // Cancelled when the app closes (OnClosing, before the web server stops and Tts.Shutdown). A sentence being made also stops
    // when a newer one is asked for: HttpListener does not say when the phone aborts, and the phone asks one sentence at a time
    // and asks nothing more after a stop, so the newest request is the one wanted.
    // ponytail: one reading across all the owner's phones; a second phone reading at once cuts the first's sentences
    private readonly CancellationTokenSource _speechClosing = new();
    private CancellationTokenSource? _webSpeaking;

    private (int, object) WebVoiceState() => (200, new
    {
        ready = _webSynthesize is not null || TtsModels.Ready,
        voices = Tts.Voices.Select(v => new { name = v.Name, language = v.Language, accent = v.Accent, gender = v.Gender, custom = false })
            .Concat(_settings.CustomVoices.Select(c => new { name = c.Name, language = "", accent = "", gender = "", custom = true })),
        settings = new { voice = _settings.Tts.Voice, speed = _settings.Tts.Speed },
    });

    // {voice?, speed?}: each one given is checked, then both are saved together; the answer is GET's.
    private (int, object) WebVoiceSet(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return (400, new { error = "Expected a JSON object" });
        string? voice = null;
        double? speed = null;
        if (body.TryGetProperty("voice", out var v))
        {
            voice = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (voice is null || !Tts.Voices.Any(x => x.Name == voice) && !_settings.CustomVoices.Any(c => c.Name == voice))
                return (400, new { error = "No such voice" });
        }
        if (body.TryGetProperty("speed", out var sp))
        {
            if (sp.ValueKind != JsonValueKind.Number || sp.GetDouble() is not (>= 0.5 and <= 2.0))
                return (400, new { error = "Speed is 0.5 to 2" });
            speed = Math.Round(sp.GetDouble(), 2);
        }
        if (voice is not null) _settings.Tts.Voice = voice;
        if (speed is { } x) _settings.Tts.Speed = x;
        _settings.Save();
        return WebVoiceState();
    }

    // {text}: the reply as the sentences to ask /api/speak for, each at most WebSentenceMax characters (the PC's reading rules,
    // SpeechText.Sentences, Pieces). Longer than MaxChars x 2 once prepared for speech (not as markdown): 413.
    private async Task<(int, object)> WebSpeakPlan(JsonElement body)
    {
        var text = WebField(body, "text");
        if (string.IsNullOrWhiteSpace(text))
            return (400, new { error = "Nothing to read" });
        var settings = await Dispatcher.InvokeAsync(() => _settings.Tts);
        return await Task.Run<(int, object)>(() =>
        {
            var said = SpeechText.ForSpeech(text, settings);
            var max = 2L * settings.MaxChars;
            if (string.IsNullOrWhiteSpace(said))
                return (400, new { error = "Nothing to read" });
            if (said.Length > max)
                return (413, new { error = $"Too long to read aloud ({max:N0} characters at most)" });
            return (200, new { sentences = SpeechText.Pieces(SpeechText.Sentences(said), WebSentenceMax) });
        });
    }

    // {text, voice?, worker?}: one sentence of the plan (at most WebSentenceMax characters, else 413), read as it is with the
    // body's voice, else the worker's voice (TtsSettings.WorkerVoices), else the PC's voice, at the PC's speed.
    private async Task<(int, object)> WebSpeak(JsonElement body)
    {
        var text = WebField(body, "text").Trim();
        // worker: a role id, or the name a reply carries ("Lead", a custom worker's title), mapped to its role on the UI thread
        var askedWorker = WebField(body, "worker");
        var (settings, customs, role) = await Dispatcher.InvokeAsync(() => (_settings.Tts, _settings.CustomVoices.ToList(),
            _cards.FirstOrDefault(c => c.Role == askedWorker || ChatName(c, c.Role).Equals(askedWorker, StringComparison.OrdinalIgnoreCase))?.Role ?? askedWorker));
        if (text.Length == 0)
            return (400, new { error = "Nothing to read" });
        if (text.Length > WebSentenceMax)
            return (413, new { error = $"One sentence at a time ({WebSentenceMax} characters at most)" });
        var stand = _webSynthesize;
        if (stand is null && !TtsModels.Ready)
            return (409, new { error = WebSpeakNotReady });
        var name = WebField(body, "voice") is { Length: > 0 } asked ? asked
            : settings.WorkerVoices.GetValueOrDefault(role) is { Length: > 0 } worker ? worker
            : settings.Voice;
        var choice = VoiceChoice.Find(name, customs);
        var speak = stand ?? Tts.Synthesize;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_speechClosing.Token);
        try { Interlocked.Exchange(ref _webSpeaking, cancel)?.Cancel(); } // the older sentence, if still being made
        catch (ObjectDisposedException) { } // it ended meanwhile
        try
        {
            return await Task.Run<(int, object)>(() =>
            {
                try { return (200, new WebServer.Response(200, "audio/wav", Wav(speak(text, choice, (float)settings.Speed, cancel.Token)))); }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { return (503, new { error = "Stopped" }); }
                catch (InvalidOperationException) when (stand is null) { return (409, new { error = WebSpeakNotReady }); } // the model went since the check
                catch (Exception exception) { return (500, new { error = "Speech failed: " + exception.Message }); }
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _webSpeaking, null, cancel);
            cancel.Dispose();
        }
    }

    // Samples (-1..1, clipped) to a mono 16-bit PCM WAV file.
    internal static byte[] Wav(float[] samples, int rate = Tts.Rate)
    {
        var wav = new byte[44 + samples.Length * 2];
        var span = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);       // the fmt chunk's size
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);        // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);        // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], rate * 2); // bytes a second
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);        // bytes a frame
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);       // bits a sample
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var s = Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + 2 * i)..], (short)(s < 0 ? s * 32768 : s * 32767));
        }
        return wav;
    }

    // Wav's bytes, header and samples (full scale both ways, clipped beyond). Run at the start of WebSpeakSmoke.
    internal static void WebSpeakSelfTest()
    {
        var wav = Wav([0, 1, -1, 0.5f, 2, -2], 24000);
        var expected = Convert.FromHexString("52494646" + "30000000" + "57415645" + "666D7420" + "10000000" + "0100" + "0100" + "C05D0000" + "80BB0000" +
            "0200" + "1000" + "64617461" + "0C000000" + "0000" + "FF7F" + "0080" + "FF3F" + "FF7F" + "0080");
        if (!wav.SequenceEqual(expected))
            throw new InvalidOperationException("WebSpeak self-test failed: the WAV is " + Convert.ToHexString(wav));
    }

    // /api/voice, /api/speak/plan and /api/speak (WebAppSmoke, while on), on the smoke's data folder, where the voice model is absent.
    private async Task<bool> WebSpeakSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: speak: {what}"); return false; }
        try { WebSpeakSelfTest(); }
        catch (InvalidOperationException exception) { return Fail(exception.Message); }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        async Task<(int Status, string Type, byte[] Body)> Send(string path, object? json, CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}{path}");
            if (json is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
            request.Headers.Add("Tailscale-User-Login", "smoke@example.com");
            using var response = await http.SendAsync(request, ct);
            return ((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType ?? "", await response.Content.ReadAsByteArrayAsync(ct));
        }
        static JsonNode? Json(byte[] body) { try { return JsonNode.Parse(body); } catch (JsonException) { return null; } }
        List<string>? Plan(byte[] body) => Json(body)?["sentences"]?.AsArray().Select(n => n!.ToString()).ToList();
        var (voice, speed, autoRead) = (_settings.Tts.Voice, _settings.Tts.Speed, _settings.Tts.AutoRead);
        try
        {
            if (await Send("/api/voice", null) is not { Status: 200 } got || Json(got.Body) is not { } state || state["ready"]?.GetValue<bool>() != false ||
                state["voices"]?.AsArray() is not { } voices || voices.Count != Tts.Voices.Count + _settings.CustomVoices.Count ||
                voices[0]?["name"]?.ToString() != Tts.Voices[0].Name || voices[0]?["custom"]?.GetValue<bool>() != false || voices[0]?["gender"] is null ||
                state["settings"]?["voice"]?.ToString() != voice || state["settings"]?["autoRead"] is not null || state["settings"]?["speed"] is null)
                return Fail("GET's shape");
            // Read replies aloud is the phone's own: an autoRead in the body leaves the PC's AutoRead as it was
            if (await Send("/api/voice", new { voice = "bm_george", speed = 1.25, autoRead = !autoRead }) is not { Status: 200 } set ||
                Json(set.Body)?["settings"]?["voice"]?.ToString() != "bm_george" || AppSettings.Load().Tts is not { Voice: "bm_george", Speed: 1.25 } saved || saved.AutoRead != autoRead)
                return Fail("POST does not persist the voice and speed, or wrote the PC's AutoRead");
            if ((await Send("/api/voice", new { voice = "nobody" })).Status != 400 || (await Send("/api/voice", new { speed = 9 })).Status != 400 ||
                AppSettings.Load().Tts.Voice != "bm_george")
                return Fail("a bad voice or speed is not 400, or was saved");

            // the plan: the reply prepared for speech, in sentences of at most WebSentenceMax characters
            if (await Send("/api/speak/plan", new { text = "Hello **there**. Second one! It is 2026" }) is not { Status: 200 } plan ||
                Plan(plan.Body) is not ["Hello there.", "Second one!", "It is two thousand twenty six."])
                return Fail("the plan of a short reply");
            var longOne = string.Join(" ", Enumerable.Repeat("word", 400)); // one 2,000-character sentence
            if (await Send("/api/speak/plan", new { text = longOne }) is not { Status: 200 } cut || Plan(cut.Body) is not { Count: 4 } pieces ||
                pieces.Any(p => p.Length > WebSentenceMax) || string.Join(" ", pieces) != longOne + ".")
                return Fail("a long sentence is not cut into pieces");
            if ((await Send("/api/speak/plan", new { text = "  " })).Status != 400 || (await Send("/api/speak/plan", new { })).Status != 400)
                return Fail("an empty plan is not 400");
            var max = 2 * _settings.Tts.MaxChars;
            if ((await Send("/api/speak/plan", new { text = new string('a', max + 1) })).Status != 413)
                return Fail("over MaxChars x 2 is not 413");
            if (await Send("/api/speak/plan", new { text = "```\n" + new string('x', max + 100) + "\n```\nShort." }) is not { Status: 200 } code ||
                Plan(code.Body) is not ["Code skipped.", "Short."])
                return Fail("the limit counts the markdown, not the text read");

            if (await Send("/api/speak", new { text = "Hello." }) is not { Status: 409 } absent || Json(absent.Body)?["error"]?.ToString() != WebSpeakNotReady)
                return Fail("no model is not 409");

            var heard = new List<(string Text, string Voice, float Speed)>();
            _webSynthesize = (text, choice, s, _) => { lock (heard) heard.Add((text, choice.Name, s)); return new float[2400]; }; // 0.1 s
            if (await Send("/api/speak", new { text = "Hello there." }) is not { Status: 200, Type: "audio/wav" } wav || wav.Body.Length != 44 + 4800 ||
                Encoding.ASCII.GetString(wav.Body, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav.Body, 8, 4) != "WAVE" ||
                BinaryPrimitives.ReadInt32LittleEndian(wav.Body.AsSpan(4)) != 36 + 4800 || BinaryPrimitives.ReadInt32LittleEndian(wav.Body.AsSpan(24)) != Tts.Rate ||
                BinaryPrimitives.ReadInt16LittleEndian(wav.Body.AsSpan(34)) != 16 || BinaryPrimitives.ReadInt32LittleEndian(wav.Body.AsSpan(40)) != 4800)
                return Fail("a WAV with the right header and length");
            if (heard is not [{ Text: "Hello there.", Voice: "bm_george", Speed: 1.25f }])
                return Fail($"the sentence as given, the PC's voice and speed: {string.Join("; ", heard)}");
            _settings.Tts.WorkerVoices["reviewer"] = "af_bella";
            try
            {
                if ((await Send("/api/speak", new { text = "Hi.", worker = "reviewer" })).Status != 200 ||
                    (await Send("/api/speak", new { text = "Hi.", voice = "am_adam", worker = "reviewer" })).Status != 200 ||
                    !heard.Skip(1).Select(x => x.Voice).SequenceEqual(["af_bella", "am_adam"]))
                    return Fail($"the worker's voice, then the body's: {string.Join("; ", heard)}");
            }
            finally { _settings.Tts.WorkerVoices.Remove("reviewer"); }
            if ((await Send("/api/speak", new { text = "  " })).Status != 400 || (await Send("/api/speak", new { })).Status != 400)
                return Fail("empty is not 400");
            if ((await Send("/api/speak", new { text = new string('a', WebSentenceMax + 1) })).Status != 413 ||
                (await Send("/api/speak", new { text = new string('a', WebSentenceMax) })).Status != 200)
                return Fail("over WebSentenceMax is not 413, or WebSentenceMax is refused");

            // cancellation: the stand-in makes one sentence at a time, as the engine does; a request the phone aborted mid-sentence
            // stops being made when the next is asked for, so the next is not stuck behind it
            using var engine = new SemaphoreSlim(1);
            var cancelled = false;
            _webSynthesize = (text, _, _, ct) =>
            {
                engine.Wait(ct);
                try
                {
                    if (text == "Slow.")
                    {
                        ct.WaitHandle.WaitOne(20_000);
                        cancelled = ct.IsCancellationRequested;
                        ct.ThrowIfCancellationRequested();
                    }
                    return new float[2400];
                }
                finally { engine.Release(); }
            };
            using (var abort = new CancellationTokenSource(300))
                try { await Send("/api/speak", new { text = "Slow." }, abort.Token); return Fail("the slow sentence was not aborted"); }
                catch (OperationCanceledException) { }
            var started = DateTime.UtcNow;
            if ((await Send("/api/speak", new { text = "Next." })).Status != 200 || (DateTime.UtcNow - started).TotalSeconds > 3 || !cancelled)
                return Fail($"the next sentence waited behind an aborted one ({(DateTime.UtcNow - started).TotalSeconds:0.0} s, cancelled {cancelled})");
            return true;
        }
        finally
        {
            _webSynthesize = null;
            (_settings.Tts.Voice, _settings.Tts.Speed, _settings.Tts.AutoRead) = (voice, speed, autoRead);
            _settings.Save();
        }
    }
}

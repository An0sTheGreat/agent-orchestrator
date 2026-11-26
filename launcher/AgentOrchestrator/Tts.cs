using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using NAudio.Wave;
using SherpaOnnx;

namespace AgentOrchestrator;

// Kokoro-82M v1.0 (multi-language, int8) for sherpa-onnx in <DataRoot>\speech, downloaded only from Settings > Voice. Checked
// 2026-11-26: Hugging Face csukuangfj/kokoro-int8-multi-lang-v1_0 at revision 57adb37fc0e8d4bd430cfc1b8f8c2294945b9af2
// (https://huggingface.co/<repo>/resolve/<revision>/<file>), by HubDownloader into speech\kokoro-int8-multi-lang-v1_0\.
// Only the files sherpa-onnx 2.0.3 reads: it no longer uses espeak-ng (espeak-ng-data and dict\ are ignored), the language
// comes from the lexicons, and words missing from them are skipped (SpeechText turns digits into words). The zh rule FSTs
// are left out: they would rewrite every number and date into Chinese, under English voices too. The fp32 model (326 MB) is
// not used: int8 is a third of the size.
internal static class TtsModels
{
    private const string Repo = "csukuangfj/kokoro-int8-multi-lang-v1_0", Revision = "57adb37fc0e8d4bd430cfc1b8f8c2294945b9af2";
    private static readonly HubDownloadFile[] Kokoro =
    [
        new("model.int8.onnx", 114_202_993), new("voices.bin", 28_200_960), new("tokens.txt", 687),
        new("lexicon-us-en.txt", 5_956_885), new("lexicon-zh.txt", 2_365_182),
    ];

    public static string Folder => Path.Combine(SpeechModels.Folder, Repo.Split('/')[1]);
    public static string ModelFile(string name) => Path.Combine(Folder, name);

    // Every file present with its expected size: a half-finished download (.incomplete, or a short file) never counts.
    public static bool Ready => Kokoro.All(f => new FileInfo(ModelFile(f.Name)) is { Exists: true } info && info.Length == f.Size);
    public static long DownloadBytes => Kokoro.Sum(f => f.Size);

    public static async Task DownloadAsync(IProgress<(long done, long total)> progress, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }; // HubDownloader follows the hub's redirects itself
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
        Directory.CreateDirectory(SpeechModels.Folder);
        await new HubDownloader(http, SpeechModels.Folder, HubLayout.Flat).DownloadAsync(Repo, Revision, Kokoro,
            new Relay<HubProgress>(p => progress.Report((p.DoneBytes, DownloadBytes))), ct);
        if (!Ready)
            throw new InvalidDataException("The voice model files are incomplete after the download");
    }
}

// A built-in Kokoro voice: its name (the key saved in settings, e.g. "af_heart"), sherpa-onnx speaker id, and how it sounds.
internal sealed record VoiceInfo(string Name, int Id, string Language, string Accent, string Gender);

// What to read with: a built-in voice by name (Mix null), or a custom blend (built-in name -> weight; Name is the blend's).
internal sealed record VoiceChoice(string Name, IReadOnlyDictionary<string, double>? Mix = null)
{
    // A saved voice name (TtsSettings.Voice, WorkerVoices) to a choice: a built-in first, then a saved blend; an unknown name
    // stays as it is, and Tts.Weights turns it into the default voice with a note.
    public static VoiceChoice Find(string name, IEnumerable<CustomVoice> customs) =>
        Tts.Voices.Any(v => v.Name == name) || customs.FirstOrDefault(c => c.Name == name) is not { } custom
            ? new(name)
            : new(name, custom.Mix);
}

internal static class Tts
{
    public const int Rate = 24000;
    public const string Default = "af_heart";
    private const int StyleFloats = 510 * 256; // one voice in voices.bin: 510 token lengths x 256 floats (float32, little-endian)

    // sherpa-onnx's speaker ids (scripts/kokoro/v1.0/generate_voices_bin.py id2speaker): the index is the id.
    private static readonly string[] Names =
    [
        "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah",
        "af_sky", "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
        "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george", "bm_lewis", "ef_dora", "em_alex",
        "ff_siwis", "hf_alpha", "hf_beta", "hm_omega", "hm_psi", "if_sara", "im_nicola", "jf_alpha", "jf_gongitsune", "jf_nezumi",
        "jf_tebukuro", "jm_kumo", "pf_dora", "pm_alex", "pm_santa", "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao", "zf_xiaoyi",
        "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang", "em_santa",
    ];

    // The voice's language and accent from its first letter, its gender from the second. sherpa-onnx 2.x reads only the
    // English and Chinese lexicons, so the other languages' voices read English text with their accent.
    public static readonly IReadOnlyList<VoiceInfo> Voices = Names.Select((name, id) =>
    {
        var (language, accent) = name[0] switch
        {
            'a' => ("English", "American"), 'b' => ("English", "British"), 'e' => ("Spanish", ""), 'f' => ("French", ""),
            'h' => ("Hindi", ""), 'i' => ("Italian", ""), 'j' => ("Japanese", ""), 'p' => ("Portuguese", "Brazilian"),
            _ => ("Chinese", "Mandarin"),
        };
        return new VoiceInfo(name, id, language, accent, name[1] == 'f' ? "Female" : "Male");
    }).ToList();

    private static readonly object Gate = new();
    private static OfflineTts? _builtIn, _blend;
    private static string _blendKey = "";
    private static bool _shutdown;

    // The weights a choice reads with, summing to 1. A built-in is itself at 1; a blend's weights are normalised (zero and
    // negative ones dropped). A voice that no longer exists, or a blend naming one, reads with the default voice and a note.
    public static (Dictionary<string, double> weights, string? note) Weights(VoiceChoice voice)
    {
        bool Known(string name) => Voices.Any(v => v.Name == name);
        var mix = voice.Mix?.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new() { [voice.Name] = 1 };
        if (mix.Count == 0 || !mix.Keys.All(Known))
            return (new() { [Default] = 1 }, $"The voice \"{voice.Name}\" is not available, so the default voice reads instead");
        var sum = mix.Values.Sum();
        return (mix.ToDictionary(kv => kv.Key, kv => kv.Value / sum), null);
    }

    // The text read aloud: 24 kHz mono samples. One synthesis at a time (2 threads, CPU); the engine is made on first use and
    // a blend's on first use of that blend (sherpa-onnx takes the voice count from the model, so a blend cannot be appended to
    // voices.bin: it goes into slot 0 of a copy, speech\kokoro-blend.bin, read by a second engine). ct stops a synthesis
    // between its chunks (OperationCanceledException). Throws InvalidOperationException when the model is not downloaded.
    public static float[] Synthesize(string text, VoiceChoice voice, float speed, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var (weights, _) = Weights(voice);
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown, typeof(Tts));
            if (!TtsModels.Ready)
                throw new InvalidOperationException("Download the voice model in Settings > Voice");
            OfflineTts engine;
            int sid;
            if (weights.Count == 1)
            {
                engine = _builtIn ??= Engine(TtsModels.ModelFile("voices.bin"));
                sid = Voices.First(v => v.Name == weights.Keys.First()).Id;
            }
            else
            {
                var key = string.Join(",", weights.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value:R}"));
                if (_blend is null || key != _blendKey)
                {
                    _blend?.Dispose();
                    _blend = null;
                    var file = Path.Combine(SpeechModels.Folder, "kokoro-blend.bin");
                    File.WriteAllBytes(file, BlendFile(File.ReadAllBytes(TtsModels.ModelFile("voices.bin")), weights));
                    _blend = Engine(file);
                    _blendKey = key;
                }
                engine = _blend;
                sid = 0;
            }
            int Continue(IntPtr samples, int n) => ct.IsCancellationRequested ? 0 : 1;
            var audio = engine.GenerateWithCallback(text, speed, sid, Continue);
            try
            {
                ct.ThrowIfCancellationRequested();
                return audio.Samples;
            }
            finally { audio.Dispose(); } // has Dispose without IDisposable
        }
    }

    // voices.bin with slot 0 replaced by the weighted sum of the named voices' styles (the way Kokoro mixes voices).
    internal static byte[] BlendFile(byte[] voices, IReadOnlyDictionary<string, double> weights)
    {
        var all = MemoryMarshal.Cast<byte, float>(voices);
        var blend = new float[StyleFloats];
        foreach (var (name, weight) in weights)
        {
            var style = all.Slice(Voices.First(v => v.Name == name).Id * StyleFloats, StyleFloats);
            for (var i = 0; i < StyleFloats; i++)
                blend[i] += (float)(style[i] * weight);
        }
        var output = (byte[])voices.Clone();
        MemoryMarshal.AsBytes(blend.AsSpan()).CopyTo(output);
        return output;
    }

    private static OfflineTts Engine(string voices)
    {
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = TtsModels.ModelFile("model.int8.onnx");
        config.Model.Kokoro.Voices = voices;
        config.Model.Kokoro.Tokens = TtsModels.ModelFile("tokens.txt");
        config.Model.Kokoro.Lexicon = TtsModels.ModelFile("lexicon-us-en.txt") + "," + TtsModels.ModelFile("lexicon-zh.txt");
        config.Model.NumThreads = 2;
        config.Model.Provider = "cpu";
        return new OfflineTts(config);
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            _builtIn?.Dispose();
            _blend?.Dispose();
            (_builtIn, _blend, _shutdown) = (null, null, true);
        }
    }

    // --speech-say "<text>" <out.wav> [voice | name:weight,name:weight]: the text through ForSpeech, Sentences and Synthesize
    // into a 24 kHz float WAV, in a test data folder (AGENT_ORCHESTRATOR_DATA; the model is downloaded there when missing).
    // Its length and peak on stderr; 0 when it is not silent.
    public static int SayFile(string text, string wav, string voice)
    {
        if (!TtsModels.Ready && !Speech.TestDownload("--speech-say", TtsModels.DownloadAsync))
            return 2;
        var choice = voice.Contains(':')
            ? new VoiceChoice("blend", voice.Split(',').Select(p => p.Split(':')).ToDictionary(p => p[0], p => double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))
            : new VoiceChoice(voice);
        var started = DateTime.UtcNow;
        var samples = SpeechText.Sentences(SpeechText.ForSpeech(text, new TtsSettings())).SelectMany(s => Synthesize(s, choice, 1f)).ToArray();
        using (var writer = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)))
            writer.WriteSamples(samples, 0, samples.Length);
        var peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        Console.Error.WriteLine($"{voice}: {samples.Length / (double)Rate:0.00} s, peak {peak:0.000}, made in {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
        return peak > 0.05 ? 0 : 1;
    }

    // --self-test: the text rules, the sentence splitter, blend weights and the blend file, Ready, and Synthesize without the
    // model; SpeechPlayer with a stub engine and a fake output (no audio device).
    public static void TtsSelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Tts self-test failed: " + what);
        }
        var s = new TtsSettings();
        string Say(string md, TtsSettings? with = null) => SpeechText.ForSpeech(md, with ?? s);
        var said = Say("Intro:\n```cs\nvar x = 1;\n```\nMiddle.\n```\nmore\n```");
        Check(said == "Intro:\nCode skipped.\nMiddle.", $"code blocks gave [{said}]");
        Check(Say("```\nhello there\n```", new TtsSettings { SkipCode = false }) == "hello there.", "code read when SkipCode is off");
        Check(Say("See [the docs](https://x.io/a) or https://y.io/b now") == "See the docs or now.", $"links gave [{Say("See [the docs](https://x.io/a) or https://y.io/b now")}]");
        Check(Say("## Setup steps\nRun it") == "Setup steps.\nRun it.", "a heading is a sentence");
        Check(Say("## Setup\nRun it", new TtsSettings { ReadHeadings = false }) == "Run it.", "headings off");
        Check(Say("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\nAfter") == "A table with two rows.\nAfter.", $"table gave [{Say("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |")}]");
        Check(Say("| a | b |\n|---|---|\n| x | y |", new TtsSettings { TableSummary = false }) == "a, b.\nx, y.", "table rows read");
        Check(Say("Steps:\n- first thing\n- second thing!\n1. third") == "Steps:\nfirst thing.\nsecond thing!\nthird.", $"lists gave [{Say("Steps:\n- first thing\n- second thing!\n1. third")}]");
        Check(Say("Done 🎉 **great** work ✅") == "Done great work.", $"emoji gave [{Say("Done 🎉 **great** work ✅")}]");
        Check(Say("<think>hmm</think>Answer") == "Answer.", "thinking dropped");
        Check(Say("It costs 9,000 or 3.5 and 50%; v1.2.3") == "It costs nine thousand or three point five and fifty percent; v one point two point three.",
            $"numbers gave [{Say("It costs 9,000 or 3.5 and 50%; v1.2.3")}]");
        Check(Say("Line one\ncontinues here") == "Line one continues here.", "a wrapped paragraph is one piece");
        Check(Say("") == "" && Say("```\nonly code\n```") == "Code skipped.", "empty and code-only replies");
        Check(Say("use `x` and `" + new string('a', 50) + "`") == "use x and.", "long inline code dropped");

        var sentences = SpeechText.Sentences("Use e.g. this one. Pi is 3.5 today! Really? Mr. Smith said \"go.\" End");
        Check(sentences is ["Use e.g. this one.", "Pi is 3.5 today!", "Really?", "Mr. Smith said \"go.\"", "End"],
            $"sentences gave [{string.Join(" | ", sentences)}]");
        Check(SpeechText.Sentences("a.\nb") is ["a.", "b"] && SpeechText.Sentences("  \n ").Count == 0, "line ends split");

        Check(Voices.Count == 54 && Voices[3] is { Name: Default, Language: "English", Accent: "American", Gender: "Female" }
            && Voices[53] is { Name: "em_santa", Language: "Spanish", Gender: "Male" }, "the voice catalog");
        var (w, note) = Weights(new VoiceChoice("mine", new Dictionary<string, double> { ["af_bella"] = 3, ["bm_george"] = 1, ["af_sky"] = 0 }));
        Check(note is null && w.Count == 2 && Math.Abs(w["af_bella"] - 0.75) < 1e-9 && Math.Abs(w["bm_george"] - 0.25) < 1e-9, "blend weights normalised");
        (w, note) = Weights(new VoiceChoice("old", new Dictionary<string, double> { ["af_bella"] = 1, ["gone_voice"] = 1 }));
        Check(note is not null && w.Keys.Single() == Default, "a blend naming a missing voice falls back to the default");
        Check(Weights(new VoiceChoice("bm_lewis")).weights["bm_lewis"] == 1 && Weights(new VoiceChoice("nope")).note is not null, "a built-in, an unknown name");
        var customs = new List<CustomVoice> { new() { Name = "Calm", Mix = { ["af_bella"] = 1, ["af_sky"] = 1 } } };
        Check(VoiceChoice.Find("Calm", customs).Mix is { Count: 2 } && VoiceChoice.Find("af_sky", customs).Mix is null, "Find");

        var voices = new float[54 * StyleFloats];
        for (var v = 0; v < 54; v++)
            voices.AsSpan(v * StyleFloats, StyleFloats).Fill(v);
        var file = MemoryMarshal.Cast<byte, float>(BlendFile(MemoryMarshal.AsBytes(voices.AsSpan()).ToArray(),
            new Dictionary<string, double> { ["af_bella"] = 0.5, ["bm_george"] = 0.5 })); // ids 2 and 26
        Check(file.Length == voices.Length && file[0] == 14 && file[StyleFloats - 1] == 14 && file[StyleFloats] == 1 && file[^1] == 53,
            "the blend goes into slot 0 and the rest is unchanged");

        // without the model: not Ready, Synthesize says so
        var dataBefore = Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA");
        var area = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-tts-test-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", area);
            Check(!TtsModels.Ready && TtsModels.DownloadBytes == 150_726_707, "nothing downloaded");
            Directory.CreateDirectory(TtsModels.Folder);
            File.WriteAllBytes(TtsModels.ModelFile("tokens.txt"), new byte[687]);
            File.WriteAllBytes(TtsModels.ModelFile("model.int8.onnx") + ".incomplete", new byte[10]);
            Check(!TtsModels.Ready, "a half-finished download counts as ready");
            var threw = false;
            try { Synthesize("hello", new VoiceChoice(Default), 1f); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "Synthesize without the model");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", dataBefore);
            try { Directory.Delete(area, recursive: true); } catch (Exception) { }
        }

        // the player: every sentence reaches the output in order with its pause, Speaking goes true then false, a second Speak
        // ends the first, Stop stops at once
        var made = new List<string>();
        float[] Stub(string text, VoiceChoice voice, float speed, CancellationToken ct)
        {
            lock (made)
                made.Add(text);
            return Enumerable.Repeat(0.5f, Rate / 10).ToArray(); // 100 ms
        }
        var output = new FakeOutput();
        using var player = new SpeechPlayer(Stub, () => output);
        var changes = new List<bool>();
        player.SpeakingChanged += changes.Add;
        var quiet = new TtsSettings { SentencePauseMs = 50, Volume = 0.5 };
        player.Speak(["One.", "Two.", "Three."], new VoiceChoice(Default), quiet, CancellationToken.None).Wait(10_000);
        Thread.Sleep(50); // the fake output's last read is counted
        Check(made is ["One.", "Two.", "Three."] && !player.Speaking && changes is [true, false], $"spoke [{string.Join(",", made)}], changes [{string.Join(",", changes)}]");
        Check(output.Loud == 3 * Rate / 10 && output.Peak == 0.25f, $"played {output.Loud} loud samples at peak {output.Peak}");
        made.Clear();
        var first = player.Speak(Enumerable.Range(0, 200).Select(i => $"s{i}."), new VoiceChoice(Default), quiet, CancellationToken.None);
        Thread.Sleep(100);
        var second = player.Speak(["Other."], new VoiceChoice(Default), quiet, CancellationToken.None);
        Check(first.Wait(2000) && second.Wait(5000) && made.Count < 200 && made[^1] == "Other.", $"a second Speak ended the first after {made.Count} sentences");
        var third = player.Speak(Enumerable.Range(0, 200).Select(i => $"t{i}."), new VoiceChoice(Default), quiet, CancellationToken.None);
        Thread.Sleep(100);
        var stopped = DateTime.UtcNow;
        player.Stop();
        Check(third.Wait(1000) && !player.Speaking && (DateTime.UtcNow - stopped).TotalMilliseconds < 500, "Stop stops");
    }

    // An output that plays at 20x speed on its own thread: it reads the player's samples and keeps the loud ones' count and peak.
    private sealed class FakeOutput : IWavePlayer
    {
        private IWaveProvider? _source;
        private volatile bool _playing;
        public int Loud;
        public float Peak;
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState => _playing ? PlaybackState.Playing : PlaybackState.Stopped;
        public WaveFormat OutputWaveFormat => _source!.WaveFormat;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider waveProvider) => _source = waveProvider;
        public void Play()
        {
            _playing = true;
            new Thread(() =>
            {
                var buffer = new byte[Rate / 100 * 4]; // 10 ms of float samples
                while (_playing)
                {
                    var read = _source!.Read(buffer, 0, buffer.Length);
                    foreach (var f in MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, read)))
                        if (f != 0)
                            (Loud, Peak) = (Loud + 1, Math.Max(Peak, Math.Abs(f)));
                    Thread.Sleep(1); // ponytail: about 20x real time; the player only needs it faster than real
                }
            }) { IsBackground = true }.Start();
        }
        public void Stop()
        {
            _playing = false;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }
        public void Pause() => _playing = false;
        public void Dispose() => _playing = false;
    }
}

// Reads sentences aloud, one Speak at a time: a new Speak or Stop ends the one playing (the output stops at once) and the
// output device is released when a Speak ends. Sentence n+1 is synthesised while sentence n plays; each is followed by
// SentencePauseMs of silence and scaled by Volume. Speaking and SpeakingChanged change on a background thread.
internal sealed class SpeechPlayer : IDisposable
{
    private readonly Func<string, VoiceChoice, float, CancellationToken, float[]> _synthesize;
    private readonly Func<TtsSettings, IWavePlayer> _output;
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private Task _running = Task.CompletedTask;
    private bool _speaking;

    public event Action<bool>? SpeakingChanged;
    public bool Speaking => _speaking;

    public SpeechPlayer() : this(Tts.Synthesize, s => new WaveOutEvent { DeviceNumber = OutputDevice(s.Device), DesiredLatency = 200 }) { }

    // The seam for tests: a stub engine and an output that is not an audio device.
    internal SpeechPlayer(Func<string, VoiceChoice, float, CancellationToken, float[]> synthesize, Func<IWavePlayer> output)
        : this(synthesize, _ => output()) { }

    private SpeechPlayer(Func<string, VoiceChoice, float, CancellationToken, float[]> synthesize, Func<TtsSettings, IWavePlayer> output) =>
        (_synthesize, _output) = (synthesize, output);

    // The speakers Windows lists (WinMM names), in device order; TtsSettings.Device saves one by name.
    public static List<string> Speakers() =>
        Enumerable.Range(0, WaveOut.DeviceCount).Select(i => WaveOut.GetCapabilities(i).ProductName).ToList();

    // The saved speaker to its device number; -1 = the Windows default (""). A saved speaker that is not connected throws, so
    // the caller can say so.
    private static int OutputDevice(string name) =>
        name.Length == 0 ? -1
        : Speakers().IndexOf(name) is var i and >= 0 ? i
        : throw new InvalidOperationException($"The speaker \"{name}\" is not connected");

    // Completes when the sentences are read, or when Stop, a newer Speak or ct ends it (no exception then). Engine and device
    // errors (the model not downloaded, the speaker missing) fault the task.
    public Task Speak(IEnumerable<string> sentences, VoiceChoice voice, TtsSettings settings, CancellationToken ct)
    {
        Task previous;
        CancellationTokenSource cancel;
        lock (_gate)
        {
            _current?.Cancel();
            previous = _running;
            cancel = _current = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _running = Task.Run(async () =>
            {
                try { await previous; } catch (Exception) { } // its own caller saw its error
                await Run(sentences.ToList(), voice, settings, cancel.Token);
            });
            return _running;
        }
    }

    public void Stop()
    {
        lock (_gate)
            _current?.Cancel();
    }

    private async Task Run(List<string> sentences, VoiceChoice voice, TtsSettings settings, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || sentences.Count == 0)
            return;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Tts.Rate, 1);
        var buffer = new BufferedWaveProvider(format) { BufferDuration = TimeSpan.FromMinutes(10), ReadFully = true };
        using var output = _output(settings);
        output.Init(buffer);
        using var stopOutput = ct.Register(output.Stop); // Stop silences at once, mid-synthesis too
        SetSpeaking(true);
        try
        {
            output.Play();
            var pause = new float[Tts.Rate * Math.Max(0, settings.SentencePauseMs) / 1000];
            var volume = (float)settings.Volume;
            var previous = TimeSpan.Zero; // the audio of the sentence added last
            foreach (var sentence in sentences)
            {
                var samples = _synthesize(sentence, voice, (float)settings.Speed, ct);
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = Math.Clamp(samples[i] * volume, -1f, 1f);
                ct.ThrowIfCancellationRequested();
                buffer.AddSamples(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), 0, samples.Length * 4);
                buffer.AddSamples(MemoryMarshal.AsBytes(pause.AsSpan()).ToArray(), 0, pause.Length * 4);
                previous = TimeSpan.FromSeconds((samples.Length + pause.Length) / (double)Tts.Rate);
                // the next sentence is made while this one plays: wait until only this one is left in the buffer
                while (buffer.BufferedDuration > previous)
                    await Task.Delay(20, ct);
            }
            while (buffer.BufferedBytes > 0)
                await Task.Delay(20, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            output.Stop();
            SetSpeaking(false);
        }
    }

    private void SetSpeaking(bool speaking)
    {
        if (_speaking == speaking)
            return;
        _speaking = speaking;
        SpeakingChanged?.Invoke(speaking);
    }

    public void Dispose()
    {
        Stop();
        try { _running.Wait(2000); } catch (Exception) { }
    }
}

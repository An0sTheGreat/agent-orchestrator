using System.IO;
using System.Net.Http;
using SherpaOnnx;

namespace AgentOrchestrator;

// The speech models in <DataRoot>\speech, downloaded only from Settings > Voice (nothing downloads on its own). Checked 2026-11-26:
//  - Parakeet TDT 0.6B v2 int8 (English), Hugging Face csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8 at revision
//    84b478d541cdbf0c296a7ab5f1e20a6f8f998114: encoder.int8.onnx 652,282,309 B, decoder.int8.onnx 7,257,753 B,
//    joiner.int8.onnx 1,739,080 B, tokens.txt 9,384 B (https://huggingface.co/<repo>/resolve/<revision>/<file>), fetched by
//    HubDownloader (resumable .incomplete files, renamed when the size matches) into speech\<repo name>\.
//  - Silero VAD, the sherpa-onnx release https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx,
//    643,854 B, into speech\silero_vad.onnx (through .incomplete, renamed when the size matches).
internal static class SpeechModels
{
    private const string Repo = "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", Revision = "84b478d541cdbf0c296a7ab5f1e20a6f8f998114";
    private const string VadUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx";
    internal const long VadSize = 643_854;
    private static readonly HubDownloadFile[] Parakeet =
        [new("encoder.int8.onnx", 652_282_309), new("decoder.int8.onnx", 7_257_753), new("joiner.int8.onnx", 1_739_080), new("tokens.txt", 9_384)];

    public static string Folder => Path.Combine(AppSettings.DataRoot, "speech");
    public static string ModelFile(string name) => Path.Combine(Folder, Repo.Split('/')[1], name);
    public static string VadFile => Path.Combine(Folder, "silero_vad.onnx");

    private static IEnumerable<(string Path, long Size)> Files => Parakeet.Select(f => (ModelFile(f.Name), f.Size)).Append((VadFile, VadSize));

    // Every file present with its expected size: a half-finished download (.incomplete, or a short file) never counts.
    public static bool Ready => Files.All(f => new FileInfo(f.Path) is { Exists: true } info && info.Length == f.Size);
    public static long DownloadBytes => Files.Sum(f => f.Size);

    // ponytail: silero (0.6 MB) is one plain GET without resume or retry; the 650 MB Parakeet files get HubDownloader's.
    public static async Task DownloadAsync(IProgress<(long done, long total)> progress, CancellationToken ct)
    {
        var total = DownloadBytes;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }; // HubDownloader follows the hub's redirects itself
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
        Directory.CreateDirectory(Folder);
        if (new FileInfo(VadFile) is not { Exists: true, Length: VadSize })
        {
            using var github = new HttpClient { Timeout = TimeSpan.FromMinutes(2) }; // GitHub's release asset redirects to its CDN
            github.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
            var bytes = await github.GetByteArrayAsync(VadUrl, ct);
            if (bytes.Length != VadSize)
                throw new InvalidDataException($"silero_vad.onnx is {bytes.Length} bytes, expected {VadSize}");
            await File.WriteAllBytesAsync(VadFile + ".incomplete", bytes, ct);
            File.Move(VadFile + ".incomplete", VadFile, overwrite: true);
        }
        progress.Report((VadSize, total));
        await new HubDownloader(http, Folder, HubLayout.Flat).DownloadAsync(Repo, Revision, Parakeet, new Relay(p => progress.Report((VadSize + p.DoneBytes, total))), ct);
        if (!Ready)
            throw new InvalidDataException("The speech model files are incomplete after the download");
    }

    // Reports on the downloader's thread; the caller's Progress<T> moves it to the UI.
    private sealed class Relay(Action<HubProgress> report) : IProgress<HubProgress> { public void Report(HubProgress value) => report(value); }
}

internal static class Speech
{
    public const int Rate = 16000;
    private static readonly object Gate = new();
    private static OfflineRecognizer? _recognizer;

    // The shared Parakeet recognizer: created once on first use (2 threads, CPU), disposed by Shutdown when the app exits.
    // Calls are serialised (one decode at a time, so dictation never takes more than its 2 threads). Throws when not Ready.
    public static Func<float[], string> Recognizer()
    {
        lock (Gate)
        {
            if (_recognizer is null)
            {
                if (!SpeechModels.Ready)
                    throw new InvalidOperationException("Download the speech model in Settings > Voice");
                var config = new OfflineRecognizerConfig();
                config.ModelConfig.Transducer.Encoder = SpeechModels.ModelFile("encoder.int8.onnx");
                config.ModelConfig.Transducer.Decoder = SpeechModels.ModelFile("decoder.int8.onnx");
                config.ModelConfig.Transducer.Joiner = SpeechModels.ModelFile("joiner.int8.onnx");
                config.ModelConfig.Tokens = SpeechModels.ModelFile("tokens.txt");
                config.ModelConfig.ModelType = "nemo_transducer";
                config.ModelConfig.NumThreads = 2;
                config.ModelConfig.Provider = "cpu";
                _recognizer = new OfflineRecognizer(config);
            }
        }
        return samples =>
        {
            lock (Gate)
            {
                if (_recognizer is not { } recognizer)
                    return "";
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(Rate, samples);
                recognizer.Decode(stream);
                return stream.Result.Text.Trim();
            }
        };
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    // Down: each output sample is the mean of the input it covers (a box filter, so little aliasing); up: linear interpolation.
    public static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate)
    {
        if (fromRate == toRate)
            return input.ToArray();
        var output = new float[(int)((long)input.Length * toRate / fromRate)];
        var step = (double)fromRate / toRate;
        for (var i = 0; i < output.Length; i++)
        {
            if (step > 1)
            {
                int start = (int)(i * step), end = Math.Min(input.Length, Math.Max(start + 1, (int)((i + 1) * step)));
                double sum = 0;
                for (var j = start; j < end; j++)
                    sum += input[j];
                output[i] = (float)(sum / (end - start));
            }
            else
            {
                var at = i * step;
                var j = (int)at;
                var next = Math.Min(j + 1, input.Length - 1);
                output[i] = (float)(input[j] + (input[next] - input[j]) * (at - j));
            }
        }
        return output;
    }

    // RMS on a dB scale: -60 dB (or silence) = 0, full scale = 1.
    public static float LevelOf(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return 0;
        double sum = 0;
        foreach (var s in samples)
            sum += s * s;
        var rms = Math.Sqrt(sum / samples.Length);
        return rms <= 0 ? 0 : (float)Math.Clamp((20 * Math.Log10(rms) + 60) / 60, 0, 1);
    }

    // The phrase at the caret: a space before and after when needed (never two), a capital at the start of the box or after a
    // sentence end. The caret lands just after the phrase.
    public static (string text, int caret) Insert(string box, int caret, string phrase)
    {
        phrase = phrase.Trim();
        caret = Math.Clamp(caret, 0, box.Length);
        if (phrase.Length == 0)
            return (box, caret);
        string before = box[..caret], after = box[caret..];
        var lead = before.TrimEnd();
        if ((lead.Length == 0 || lead[^1] is '.' or '!' or '?' || before.EndsWith('\n')) && char.IsLower(phrase[0]))
            phrase = char.ToUpperInvariant(phrase[0]) + phrase[1..];
        static bool Punct(char c) => c is '.' or ',' or '!' or '?' or ';' or ':' or ')';
        if (before.Length > 0 && !char.IsWhiteSpace(before[^1]) && !Punct(phrase[0]))
            phrase = " " + phrase;
        var tail = after.Length > 0 && !char.IsWhiteSpace(after[0]) && !Punct(after[0]) ? " " : "";
        return (before + phrase + tail + after, before.Length + phrase.Length);
    }

    // --self-test: the pure helpers, and a DictationSession fed 10 s of silence ending with "nothing heard" (fake transcribe;
    // the real Silero VAD when the models are downloaded in this data folder, else the energy gate; it says which on stderr).
    public static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Speech self-test failed: " + what);
        }
        var sine = new float[48000];
        for (var i = 0; i < sine.Length; i++)
            sine[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 48000.0);
        var down = Resample(sine, 48000, Rate);
        Check(down.Length == 16000, $"48k to 16k gave {down.Length} samples");
        var crossings = 0;
        for (var i = 1; i < down.Length; i++)
            if (down[i - 1] < 0 != down[i] < 0)
                crossings++;
        Check(Math.Abs(crossings - 2000) <= 4, $"a 1 kHz sine resampled crosses zero {crossings} times in 1 s");
        Check(Resample(new float[] { 0, 1 }, 8000, Rate) is [0, 0.5f, 1, 1], "8k to 16k interpolates");
        Check(LevelOf(new float[1600]) == 0, "silence's level");
        Check(LevelOf(Enumerable.Repeat(1f, 1600).ToArray()) == 1, "full scale's level");
        Check(LevelOf(Enumerable.Repeat(0.0005f, 1600).ToArray()) == 0, "below -60 dB's level");

        Check(Insert("", 0, "hello.") == ("Hello.", 6), "into an empty box");
        Check(Insert("Hi", 2, "there") == ("Hi there", 8), "after a word");
        Check(Insert("end. ", 5, "next") == ("end. Next", 9), "after a sentence");
        Check(Insert("end.", 4, "next") == ("end. Next", 9), "after a sentence, no space");
        Check(Insert("Hello world", 5, "big") == ("Hello big world", 9), "mid-text keeps the rest");
        Check(Insert("Hello world", 6, "big") == ("Hello big world", 9), "before a word");
        Check(Insert("Hi", 2, ", you") == ("Hi, you", 7), "punctuation needs no space");
        Check(Insert("Hi", 2, "  ") == ("Hi", 2), "an empty phrase changes nothing");

        var real = SpeechModels.Ready;
        Console.Error.WriteLine($"Speech self-test: DictationSession with {(real ? "the Silero VAD" : "the energy gate (models not downloaded)")}");
        using var session = new DictationSession(_ => "never");
        var ended = new List<string>();
        var texts = new List<string>();
        var levels = 0;
        session.Ended += ended.Add;
        session.Text += texts.Add;
        session.Level += _ => levels++;
        var chunk = new float[1600]; // 100 ms
        for (var i = 0; i < 99; i++)
            session.Feed(chunk);
        Check(ended.Count == 0, "ended before 10 s");
        for (var i = 0; i < 5; i++)
            session.Feed(chunk);
        Check(ended is ["nothing heard"], $"10 s of silence ended with [{string.Join(",", ended)}]");
        Check(texts.Count == 0 && levels >= 100, $"silence gave {texts.Count} phrases, {levels} levels");

        // the energy gate: a loud second then 2 s of silence is one phrase, then "silence"
        using var gated = new DictationSession(samples => $"{samples.Length / Rate}s", vad: false);
        ended.Clear();
        gated.Ended += ended.Add;
        gated.Text += texts.Add;
        for (var i = 0; i < 10; i++)
            gated.Feed(Enumerable.Repeat(0.5f, 1600).ToArray());
        for (var i = 0; i < 25; i++)
            gated.Feed(chunk);
        Check(texts.Count == 1 && ended is ["silence"], $"speech then silence gave [{string.Join(",", texts)}] and [{string.Join(",", ended)}]");

        // Ready only with every file at its size
        var dataBefore = Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA");
        var area = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-speech-test-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", area);
            Check(!SpeechModels.Ready && SpeechModels.DownloadBytes == 661_932_380, "nothing downloaded");
            Directory.CreateDirectory(Path.GetDirectoryName(SpeechModels.ModelFile("x"))!);
            File.WriteAllBytes(SpeechModels.VadFile, new byte[SpeechModels.VadSize]);
            File.WriteAllBytes(SpeechModels.ModelFile("tokens.txt"), new byte[9_384]);
            File.WriteAllBytes(SpeechModels.ModelFile("encoder.int8.onnx") + ".incomplete", new byte[10]);
            Check(!SpeechModels.Ready, "a half-finished download counts as ready");
            var threw = false;
            try { Recognizer(); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "Recognizer() without the models");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", dataBefore);
            try { Directory.Delete(area, recursive: true); } catch (Exception) { }
        }
    }
}

// One dictation: 16 kHz samples in, a Level per Feed, a Text per spoken phrase (each VAD segment transcribed as it ends), and
// Ended once on its own: "silence" after 2 s without speech once speech was heard, "nothing heard" after 10 s with none.
// Stop() transcribes the phrase in progress and raises no Ended. Events fire on the feeding thread.
internal sealed class DictationSession : IDisposable
{
    private const int Window = 512, EndSilence = 2 * Speech.Rate, NothingHeard = 10 * Speech.Rate;
    private const float GateLevel = 0.4f; // the energy gate: about -36 dB RMS counts as speech
    private readonly Func<float[], string> _transcribe;
    private readonly VoiceActivityDetector? _vad;
    private readonly object _gate = new();
    private readonly List<float> _pending = [];  // samples not yet a whole window
    private readonly List<float> _phrase = [];   // the energy gate's phrase in progress
    private long _total, _silence;
    private bool _heard, _done;

    public event Action<float>? Level;
    public event Action<string>? Text;
    public event Action<string>? Ended;

    public DictationSession(Func<float[], string> transcribe) : this(transcribe, SpeechModels.Ready) { }

    // vad false (or the models missing): the energy gate stands in for Silero (the self-test, without the models).
    internal DictationSession(Func<float[], string> transcribe, bool vad)
    {
        _transcribe = transcribe;
        if (!vad)
            return;
        var config = new VadModelConfig();
        config.SileroVad.Model = SpeechModels.VadFile;
        config.SileroVad.MinSilenceDuration = 0.5f; // a pause this long ends a phrase
        config.SileroVad.MaxSpeechDuration = 15f;   // a longer phrase is split (default 5 s cuts words)
        config.SileroVad.WindowSize = Window;
        config.NumThreads = 1;
        _vad = new VoiceActivityDetector(config, 30);
    }

    public void Feed(float[] samples16k)
    {
        lock (_gate)
        {
            if (_done)
                return;
            Level?.Invoke(Speech.LevelOf(samples16k));
            _pending.AddRange(samples16k);
            var whole = _pending.Count / Window * Window;
            for (var at = 0; at < whole && !_done; at += Window)
            {
                var window = _pending.GetRange(at, Window).ToArray();
                bool speech;
                if (_vad is not null)
                {
                    _vad.AcceptWaveform(window);
                    speech = _vad.IsSpeechDetected();
                    Drain();
                }
                else
                {
                    speech = Speech.LevelOf(window) >= GateLevel;
                    if (speech)
                        _phrase.AddRange(window);
                    else if (_phrase.Count > 0 && _silence + Window >= Speech.Rate / 2)
                        Emit();
                }
                _total += Window;
                _heard |= speech;
                _silence = speech ? 0 : _silence + Window;
                if (_heard && _silence >= EndSilence)
                    End("silence");
                else if (!_heard && _total >= NothingHeard)
                    End("nothing heard");
            }
            _pending.RemoveRange(0, Math.Min(whole, _pending.Count));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_done)
                Flush();
            _done = true;
        }
    }

    private void End(string reason)
    {
        Flush();
        _done = true;
        Ended?.Invoke(reason);
    }

    private void Flush()
    {
        if (_vad is not null)
        {
            _vad.Flush();
            Drain();
        }
        else
        {
            if (_phrase.Count > 0) // mid-phrase: its last samples too
            {
                _phrase.AddRange(_pending);
                Emit();
            }
        }
        _pending.Clear();
    }

    private void Drain()
    {
        while (!_vad!.IsEmpty())
        {
            var samples = _vad.Front().Samples;
            _vad.Pop();
            Say(samples);
        }
    }

    private void Emit()
    {
        var samples = _phrase.ToArray();
        _phrase.Clear();
        Say(samples);
    }

    private void Say(float[] samples)
    {
        if (_transcribe(samples) is { Length: > 0 } text)
            Text?.Invoke(text);
    }

    public void Dispose()
    {
        Stop();
        _vad?.Dispose();
    }
}

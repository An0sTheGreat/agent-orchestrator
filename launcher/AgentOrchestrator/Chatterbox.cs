using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;

namespace AgentOrchestrator;

// Chatterbox Turbo (Resemble AI, 350M, MIT, English) in <DataRoot>\chatterbox, installed only from Settings > Voice.
//
// Runtime, decided 2026-11-26 on this PC (RTX 5080 16 GB, driver 617.42): the official ONNX export run in-process with ONNX
// Runtime's DirectML provider, not a Python sidecar.
//  - ONNX: Hugging Face ResembleAI/chatterbox-turbo-ONNX at revision d21799bd0354adb85e348b8a0442a8405110a2cf, the fp16
//    graphs (speech_encoder, embed_tokens, language_model, conditional_decoder; each .onnx beside its .onnx_data) and
//    tokenizer.json (GPT-2 byte-level BPE + 19 paralinguistic tags such as [laugh]), 1,662 MB, by HubDownloader into
//    chatterbox\chatterbox-turbo-ONNX\. The loop follows the model card: the clip through speech_encoder, the text through
//    embed_tokens, language_model autoregressive with its KV cache (repetition penalty 1.2) until token 6562, then
//    conditional_decoder to 24 kHz samples.
//  - ONNX Runtime 1.24.4 with DirectML (the last DirectML build; DirectML runs on any DX12 GPU, Blackwell included, with no
//    CUDA/cuDNN install): onnxruntime.dll from nuget.org Microsoft.ML.OnnxRuntime.DirectML 1.24.4 (12 MB package) and
//    DirectML.dll from Microsoft.AI.DirectML 1.15.4 (a 202 MB package for an 18 MB dll; the package is deleted after), into
//    chatterbox\runtime\. The app references only Microsoft.ML.OnnxRuntime.Managed 1.24.4 (no natives) and points its
//    "onnxruntime" at that file by full path, so it never meets sherpa-onnx's own onnxruntime.dll (Kokoro, Parakeet).
//  - Why not the Python sidecar: chatterbox-tts 0.1.7 pins torch 2.6.0 (no CUDA 12.8 build, so no sm_120 for the 5080)
//    below Python 3.14 and transformers 5.2 / gradio 6.8 / a git-only resemble-perth on top; FindPython() finds 3.14 here.
//    Its install would be ~6 GB (torch cu128 ~3 GB) against ~1.7 GB, and it needs a second process to supervise. The CUDA
//    provider was not taken either: it needs cuDNN 9 + cuBLAS (~1.5 GB more).
// Install on disk: ~1.7 GB (models 1,662 MB + runtime 36 MB + the default voice); the download is ~1.9 GB (Kokoro's 150 MB
// too when it is missing: the default voice is Kokoro af_heart reading a reference paragraph, chatterbox\voices\default.wav).
// Turbo takes no exaggeration or cfg (the torch Turbo ignores them as well); Temperature is used (top-p 0.95).
internal sealed class ChatterboxVoice
{
    public string Name { get; set; } = "";
    public string Clip { get; set; } = ""; // a 24 kHz mono WAV, a file name under chatterbox\voices\ (or a full path)
    public double Exaggeration { get; set; } = 0.5;
    public double Cfg { get; set; } = 0.5;
    public double Temperature { get; set; } = 0.8;
    public string Fallback { get; set; } = Tts.Default; // the Kokoro voice that reads when Chatterbox cannot

    public static ChatterboxVoice Default => new() { Name = "Chatterbox default", Clip = ChatterboxRuntime.DefaultClipName };
}

internal static class ChatterboxRuntime
{
    private const string Repo = "ResembleAI/chatterbox-turbo-ONNX", Revision = "d21799bd0354adb85e348b8a0442a8405110a2cf";
    private const string OrtVersion = "1.24.4", DirectMlVersion = "1.15.4";
    internal const string DefaultClipName = "default.wav";
    private static readonly HubDownloadFile[] Models =
    [
        new("tokenizer.json", 3_562_272),
        new("onnx/speech_encoder_fp16.onnx", 1_189_888), new("onnx/speech_encoder_fp16.onnx_data", 522_307_136),
        new("onnx/embed_tokens_fp16.onnx", 1_754), new("onnx/embed_tokens_fp16.onnx_data", 116_406_272),
        new("onnx/language_model_fp16.onnx", 209_456), new("onnx/language_model_fp16.onnx_data", 634_862_406),
        new("onnx/conditional_decoder_fp16.onnx", 2_104_016), new("onnx/conditional_decoder_fp16.onnx_data", 384_019_328),
    ];
    // (package, version, entry in the package, file in runtime\, its size)
    private static readonly (string Package, string Version, string Entry, string File, long Size)[] Natives =
    [
        ("microsoft.ml.onnxruntime.directml", OrtVersion, "runtimes/win-x64/native/onnxruntime.dll", "onnxruntime.dll", 17_328_152),
        ("microsoft.ai.directml", DirectMlVersion, "bin/x64-win/DirectML.dll", "DirectML.dll", 18_527_776),
    ];
    private const long PackageBytes = 12_458_649 + 202_292_617;

    public static string Folder => Path.Combine(AppSettings.DataRoot, "chatterbox");
    public static string ModelFile(string name) => Path.Combine(Folder, Repo.Split('/')[1], name.Replace('/', Path.DirectorySeparatorChar));
    public static string RuntimeFile(string name) => Path.Combine(Folder, "runtime", name);
    public static string VoicesFolder => Path.Combine(Folder, "voices");
    public static string ClipPath(ChatterboxVoice voice) => Path.IsPathRooted(voice.Clip) ? voice.Clip : Path.Combine(VoicesFolder, voice.Clip);

    // Every file with its expected size, and the default voice: a half-finished install never counts.
    public static bool Ready =>
        Models.All(f => Size(ModelFile(f.Name)) == f.Size) && Natives.All(n => Size(RuntimeFile(n.File)) == n.Size)
        && Size(Path.Combine(VoicesFolder, DefaultClipName)) > 44;
    public static long InstallBytes => Models.Sum(f => f.Size) + Natives.Sum(n => n.Size); // on disk
    public static long DownloadBytes => Models.Sum(f => f.Size) + PackageBytes + (TtsModels.Ready ? 0 : TtsModels.DownloadBytes);
    private static long Size(string path) => new FileInfo(path) is { Exists: true } info ? info.Length : -1;

    // The runtime, the models (resumable), Kokoro when missing, then the default voice. Progress over DownloadBytes.
    public static async Task InstallAsync(IProgress<(long done, long total)> progress, CancellationToken ct)
    {
        var total = DownloadBytes;
        long done = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(RuntimeFile("x"))!);
        using (var nuget = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        {
            nuget.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
            foreach (var (package, version, entry, file, size) in Natives)
            {
                if (Size(RuntimeFile(file)) == size)
                    continue;
                // ponytail: the package is one plain GET without resume (202 MB at most); the models get HubDownloader's
                var nupkg = RuntimeFile(package + ".nupkg.incomplete");
                using (var response = await nuget.GetAsync($"https://api.nuget.org/v3-flatcontainer/{package}/{version}/{package}.{version}.nupkg", HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var body = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = File.Create(nupkg);
                    var buffer = new byte[1 << 20];
                    for (int n; (n = await body.ReadAsync(buffer, ct)) > 0;)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                        progress.Report((done += n, total));
                    }
                }
                using (var zip = ZipFile.OpenRead(nupkg))
                    (zip.GetEntry(entry) ?? throw new InvalidDataException($"{package} has no {entry}")).ExtractToFile(RuntimeFile(file) + ".incomplete", overwrite: true);
                File.Delete(nupkg);
                if (Size(RuntimeFile(file) + ".incomplete") != size)
                    throw new InvalidDataException($"{file} from {package} {version} is not the expected size");
                File.Move(RuntimeFile(file) + ".incomplete", RuntimeFile(file), overwrite: true);
            }
        }
        done = PackageBytes;
        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }) // HubDownloader follows the hub's redirects itself
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
            var start = done;
            await new HubDownloader(http, Folder, HubLayout.Flat).DownloadAsync(Repo, Revision, Models,
                new Relay<HubProgress>(p => progress.Report((start + p.DoneBytes, total))), ct);
        }
        done = PackageBytes + Models.Sum(f => f.Size);
        if (!TtsModels.Ready)
        {
            var start = done;
            await TtsModels.DownloadAsync(new Relay<(long done, long total)>(p => progress.Report((start + p.done, total))), ct);
        }
        var clip = Path.Combine(VoicesFolder, DefaultClipName);
        if (Size(clip) <= 44)
        {
            Directory.CreateDirectory(VoicesFolder);
            var samples = await Task.Run(() => SpeechText.Sentences(DefaultReference).SelectMany(s => Tts.Synthesize(s, new VoiceChoice(Tts.Default), 1f, ct)).ToArray(), ct);
            Chatterbox.WriteWav(clip + ".incomplete", samples);
            File.Move(clip + ".incomplete", clip, overwrite: true);
        }
        if (!Ready)
            throw new InvalidDataException("The Chatterbox files are incomplete after the install");
    }

    // About ten seconds of af_heart: the default voice's reference.
    private const string DefaultReference = "Hello there! I'm really glad you stopped by today. Let me walk you through what I found, " +
        "and then we can decide together what to do next. Honestly, I think you're going to like it.";

    // Unloads the engine and deletes the folder. The runtime's dlls stay loaded until the app exits once used this run, so
    // they may be left behind (Ready is false without the models either way); the next Uninstall or Install deals with them.
    public static void Uninstall()
    {
        Chatterbox.Unload();
        foreach (var dir in new[] { Path.Combine(Folder, Repo.Split('/')[1]), VoicesFolder, Path.GetDirectoryName(RuntimeFile("x"))! })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

internal static class Chatterbox
{
    public const int Rate = 24000;
    private const long StartSpeech = 6561, StopSpeech = 6562, Silence = 4299;
    private const int MaxTokens = 1000; // 25 tokens a second: 40 s, past any one sentence
    // GPU memory the engine takes once loaded (fp16 weights 1.66 GB + DirectML working memory); measured on the 5080 (G4).
    internal const long NeedBytes = 3L << 30;
    internal static TimeSpan IdleAfter = TimeSpan.FromMinutes(10);

    private static readonly object Gate = new();
    private static readonly IdleSlot<Engine> Slot = new(() => IdleAfter, Gate);
    private static readonly Dictionary<string, Conditioning> Conds = []; // clip path + write time -> its encoder outputs
    private static volatile bool _shutdown;
    private static string? _failure; // the last load error: no retry until Unload (Install, Uninstall)

    // Whether a Chatterbox voice can read now; else the reason (the caller reads with the voice's Kokoro stand-in).
    public static bool CanRunNow(out string reason)
    {
        reason = !ChatterboxRuntime.Ready ? "Chatterbox is not installed"
            : _failure is { } failure ? "Chatterbox could not start: " + failure
            : WontFit(Slot.Loaded ? null : FreeGpuBytes(), Slot.Loaded) ?? "";
        return reason.Length == 0;
    }

    // null when the engine fits: already loaded, the free memory unknown (no NVIDIA tools: DirectML tries, errors fall back),
    // or at least NeedBytes free.
    internal static string? WontFit(long? freeBytes, bool loaded) =>
        loaded || freeBytes is null || freeBytes >= NeedBytes ? null
        : $"Only {freeBytes / (double)(1L << 30):0.0} GB of GPU memory is free and Chatterbox needs about {NeedBytes >> 30} GB";

    // The first GPU's free memory (nvidia-smi), or null.
    internal static long? FreeGpuBytes()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            var output = process?.StandardOutput.ReadToEndAsync();
            return process is not null && process.WaitForExit(5000) &&
                long.TryParse(output!.GetAwaiter().GetResult().Split('\n')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) ? mib << 20 : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    // The text read in the voice: 24 kHz mono samples. One synthesis at a time; the engine loads on first use (GPU through
    // DirectML, else CPU) and unloads after IdleAfter unused. ct ends it within a model step (OperationCanceledException).
    // Throws when not installed or the engine fails; the caller falls back to Kokoro.
    public static float[] Synthesize(string text, ChatterboxVoice voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        while (!Monitor.TryEnter(Gate, 50))
            ct.ThrowIfCancellationRequested();
        try
        {
            ObjectDisposedException.ThrowIf(_shutdown, typeof(Chatterbox));
            if (!ChatterboxRuntime.Ready)
                throw new InvalidOperationException("Install Chatterbox in Settings > Voice");
            Engine engine;
            try { engine = Slot.Get(() => new Engine()); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _failure = e.Message;
                throw;
            }
            var clip = ChatterboxRuntime.ClipPath(voice);
            var key = clip + "|" + File.GetLastWriteTimeUtc(clip).Ticks;
            if (!Conds.TryGetValue(key, out var cond) || cond.Owner != engine)
            {
                cond?.Free(); // made by an engine since unloaded
                Conds[key] = cond = engine.Condition(LoadClip(clip), ct);
            }
            try { return engine.Generate(text, cond, (float)voice.Temperature, ct); }
            finally { Slot.Touch(); }
        }
        finally { Monitor.Exit(Gate); }
    }

    // Frees the GPU now (before a model load that needs the room, Uninstall); the next Synthesize loads again.
    public static void Unload()
    {
        lock (Gate)
        {
            Slot.Release();
            ClearConds();
            _failure = null;
        }
    }

    // Closing: a synthesis in progress stops at its next step; the engine is freed if it ends within a second.
    public static void Shutdown()
    {
        _shutdown = true;
        if (!Monitor.TryEnter(Gate, 1000))
            return;
        try { Slot.Release(); ClearConds(); }
        finally { Monitor.Exit(Gate); }
    }

    // A reference clip as 24 kHz mono (the first channel), at most its first 20 s.
    internal static float[] LoadClip(string path)
    {
        using var reader = new AudioFileReader(path);
        var (rate, channels) = (reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        var all = new List<float>();
        var buffer = new float[rate * channels];
        for (int read; (read = reader.Read(buffer, 0, buffer.Length)) > 0 && all.Count < rate * 20;)
            for (var i = 0; i < read; i += channels)
                all.Add(buffer[i]);
        var mono = all.Take(rate * 20).ToArray();
        return rate == Rate ? mono : Speech.Resample(mono, rate, Rate);
    }

    internal static void WriteWav(string path, float[] samples)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }

    // The speech encoder's outputs for one clip, kept so a voice's clip is encoded once per load.
    private sealed record Conditioning(Engine Owner, OrtValue[] Values) // cond embeds, prompt tokens, speaker embedding, speaker features
    {
        public void Free() { foreach (var v in Values) v.Dispose(); }
    }

    private static void ClearConds()
    {
        foreach (var c in Conds.Values)
            c.Free();
        Conds.Clear();
    }

    // The four sessions. ponytail: the KV cache goes through host memory each step (no IOBinding); bind it if steps get slow.
    private sealed class Engine : IDisposable
    {
        private readonly InferenceSession _encoder, _embed, _lm, _decoder;
        private readonly Gpt2Tokenizer _tokenizer;
        public readonly bool Gpu;

        public Engine()
        {
            UseRuntime();
            _tokenizer = new Gpt2Tokenizer(File.ReadAllText(ChatterboxRuntime.ModelFile("tokenizer.json")));
            SessionOptions Options(bool gpu)
            {
                var options = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                if (gpu)
                    options.AppendExecutionProvider_DML(0);
                return options;
            }
            InferenceSession Open(string name, bool gpu)
            {
                using var options = Options(gpu);
                return new InferenceSession(ChatterboxRuntime.ModelFile($"onnx/{name}_fp16.onnx"), options);
            }
            try { (_encoder, _embed, _lm, _decoder, Gpu) = (Open("speech_encoder", true), Open("embed_tokens", true), Open("language_model", true), Open("conditional_decoder", true), true); }
            catch (Exception e) when (e is OnnxRuntimeException or EntryPointNotFoundException) // no DirectML device: the CPU, slowly
            {
                (_encoder, _embed, _lm, _decoder, Gpu) = (Open("speech_encoder", false), Open("embed_tokens", false), Open("language_model", false), Open("conditional_decoder", false), false);
            }
        }

        public Conditioning Condition(float[] clip, CancellationToken ct)
        {
            using var audio = Tensor(_encoder, "audio_values", clip, [1, clip.Length]);
            return new(this, Run(_encoder, [("audio_values", audio)], ct));
        }

        public float[] Generate(string text, Conditioning cond, float temperature, CancellationToken ct)
        {
            var ids = _tokenizer.Encode(text);
            var generated = new List<long> { StartSpeech };
            var past = _lm.InputMetadata.Where(kv => kv.Key.StartsWith("past_key_values")).ToDictionary(kv => kv.Key,
                kv => OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, kv.Value.ElementDataType, [1, 16, 0, 64]));
            var random = new Random();
            long seq = 0;
            try
            {
                for (var step = 0; step < MaxTokens; step++)
                {
                    using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
                    var embedded = Run(_embed, [("input_ids", idsValue)], ct)[0];
                    if (step == 0)
                    {
                        var joined = Concat(cond.Values[0], embedded);
                        embedded.Dispose();
                        embedded = joined;
                    }
                    var length = embedded.GetTensorTypeAndShape().Shape[1];
                    var positions = Enumerable.Range(0, (int)length).Select(i => seq + i).ToArray();
                    seq += length;
                    using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, (int)seq).ToArray(), [1, seq]);
                    using var position = OrtValue.CreateTensorValueFromMemory(positions, [1, length]);
                    var inputs = new List<(string, OrtValue)> { ("inputs_embeds", embedded), ("attention_mask", mask), ("position_ids", position) };
                    inputs.AddRange(past.Select(kv => (kv.Key, kv.Value)));
                    var outputs = Run(_lm, inputs, ct);
                    embedded.Dispose();
                    var names = _lm.OutputNames;
                    for (var i = 1; i < outputs.Length; i++)
                    {
                        var name = names[i].Replace("present", "past_key_values");
                        past[name].Dispose();
                        past[name] = outputs[i];
                    }
                    var next = Sample(Floats(outputs[0], lastRow: true), generated, temperature, random);
                    outputs[0].Dispose();
                    generated.Add(next);
                    if (next == StopSpeech)
                        break;
                    ids = [next];
                }
            }
            finally { foreach (var v in past.Values) v.Dispose(); }
            var prompt = cond.Values[1].GetTensorDataAsSpan<long>().ToArray();
            var speech = prompt.Concat(generated.Skip(1).Where(t => t != StopSpeech)).Concat([Silence, Silence, Silence]).ToArray();
            using var tokens = OrtValue.CreateTensorValueFromMemory(speech, [1, speech.Length]);
            var wav = Run(_decoder, [("speech_tokens", tokens), ("speaker_embeddings", cond.Values[2]), ("speaker_features", cond.Values[3])], ct);
            try { return Floats(wav[0], lastRow: false); }
            finally { foreach (var v in wav) v.Dispose(); }
        }

        // Runs with only the inputs the graph has; ct terminates a run in progress.
        private static OrtValue[] Run(InferenceSession session, List<(string Name, OrtValue Value)> inputs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var options = new RunOptions();
            using var stop = ct.Register(() => options.Terminate = true);
            var used = inputs.Where(i => session.InputMetadata.ContainsKey(i.Name)).ToList();
            try
            {
                // not disposed: its Dispose would free the values, which the caller owns now
                return session.Run(options, used.Select(i => i.Name).ToList(), used.Select(i => i.Value).ToList(), session.OutputNames).ToArray();
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        }

        public void Dispose()
        {
            foreach (var s in new[] { _encoder, _embed, _lm, _decoder })
                s.Dispose();
        }
    }

    // Temperature, repetition penalty 1.2 (the model card's) and top-p 0.95 over the last position's logits.
    internal static long Sample(float[] logits, IEnumerable<long> generated, float temperature, Random random)
    {
        foreach (var t in generated.Distinct())
            logits[t] = logits[t] < 0 ? logits[t] * 1.2f : logits[t] / 1.2f;
        if (temperature <= 0.01f)
            return Array.IndexOf(logits, logits.Max());
        var max = logits.Max();
        var order = Enumerable.Range(0, logits.Length).OrderByDescending(i => logits[i]).Take(1000).ToArray();
        var p = order.Select(i => Math.Exp((logits[i] - max) / temperature)).ToArray();
        var sum = p.Sum();
        double kept = 0;
        var n = 0;
        while (n < p.Length && kept < 0.95 * sum)
            kept += p[n++];
        var pick = random.NextDouble() * kept;
        for (var i = 0; i < n; i++)
            if ((pick -= p[i]) <= 0)
                return order[i];
        return order[n - 1];
    }

    // A tensor of the input's own element type (float16 or float32) from floats.
    private static OrtValue Tensor(InferenceSession session, string input, float[] data, long[] shape)
    {
        var type = session.InputMetadata[input].ElementDataType;
        if (type == TensorElementType.Float)
            return OrtValue.CreateTensorValueFromMemory(data, shape);
        var value = OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, type, shape);
        var halves = MemoryMarshal.Cast<byte, Half>(value.GetTensorMutableRawData());
        for (var i = 0; i < data.Length; i++)
            halves[i] = (Half)data[i];
        return value;
    }

    // A float16 or float32 tensor as floats: all of it, or the last row of [1, seq, n].
    private static float[] Floats(OrtValue value, bool lastRow)
    {
        var info = value.GetTensorTypeAndShape();
        var raw = value.GetTensorMutableRawData();
        var width = lastRow ? (int)info.Shape[^1] : (int)info.ElementCount;
        var start = (int)info.ElementCount - width;
        if (info.ElementDataType == TensorElementType.Float)
            return MemoryMarshal.Cast<byte, float>(raw).Slice(start, width).ToArray();
        var halves = MemoryMarshal.Cast<byte, Half>(raw).Slice(start, width);
        var floats = new float[width];
        for (var i = 0; i < width; i++)
            floats[i] = (float)halves[i];
        return floats;
    }

    // [1, a, d] + [1, b, d] -> [1, a+b, d], same element type.
    private static OrtValue Concat(OrtValue first, OrtValue second)
    {
        var (a, b) = (first.GetTensorTypeAndShape(), second.GetTensorTypeAndShape());
        var joined = OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, a.ElementDataType, [1, a.Shape[1] + b.Shape[1], a.Shape[2]]);
        var into = joined.GetTensorMutableRawData();
        var head = first.GetTensorMutableRawData();
        head.CopyTo(into);
        second.GetTensorMutableRawData().CopyTo(into[head.Length..]);
        return joined;
    }

    // "onnxruntime" for Microsoft.ML.OnnxRuntime is chatterbox\runtime\onnxruntime.dll by full path (DirectML.dll beside it
    // loaded first), never sherpa-onnx's own onnxruntime.dll that Kokoro and Parakeet use. Set once per process.
    private static bool _resolver;
    private static void UseRuntime()
    {
        if (_resolver)
            return;
        NativeLibrary.Load(ChatterboxRuntime.RuntimeFile("DirectML.dll"));
        var ort = NativeLibrary.Load(ChatterboxRuntime.RuntimeFile("onnxruntime.dll"));
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) => name == "onnxruntime" ? ort : IntPtr.Zero);
        _resolver = true;
    }

    // --chatterbox-say "<text>" <out.wav> [clip.wav]: install into a test data folder (AGENT_ORCHESTRATOR_DATA) when missing,
    // then the text sentence by sentence (default voice, or the clip) into a WAV; timings, real-time factor, GPU memory and
    // peak on stderr; then a long sentence cancelled after 1.5 s must stop within a second. 0 when not silent and it stopped.
    public static int SayFile(string text, string wav, string? clip)
    {
        if (!ChatterboxRuntime.Ready)
        {
            var started = Stopwatch.StartNew();
            if (!Speech.TestDownload("--chatterbox-say", ChatterboxRuntime.InstallAsync))
                return 2;
            Console.Error.WriteLine($"installed {DirSize(ChatterboxRuntime.Folder) >> 20} MB in {started.Elapsed.TotalSeconds:0} s");
        }
        var voice = clip is null ? ChatterboxVoice.Default : new ChatterboxVoice { Name = "clip", Clip = Path.GetFullPath(clip) };
        var before = FreeGpuBytes();
        if (!CanRunNow(out var reason))
        {
            Console.Error.WriteLine("cannot run: " + reason);
            return 1;
        }
        var clock = Stopwatch.StartNew();
        var all = new List<float>();
        double first = 0;
        foreach (var sentence in SpeechText.Sentences(SpeechText.ForSpeech(text, new TtsSettings())))
        {
            all.AddRange(Synthesize(sentence, voice, CancellationToken.None));
            if (first == 0)
                first = clock.Elapsed.TotalSeconds;
        }
        var made = clock.Elapsed.TotalSeconds;
        var after = FreeGpuBytes();
        var samples = all.ToArray();
        WriteWav(wav, samples);
        var peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        var seconds = samples.Length / (double)Rate;
        Console.Error.WriteLine($"{voice.Name}: {(Slot.Value?.Gpu == true ? "GPU (DirectML)" : "CPU")}, {seconds:0.00} s of audio, first sentence in {first:0.00} s (incl. load), all in {made:0.00} s, " +
            $"RTF {made / Math.Max(seconds, 0.01):0.00}, peak {peak:0.000}, GPU memory taken {((before ?? 0) - (after ?? 0)) >> 20} MB");
        clock.Restart();
        Synthesize("Warm run.", voice, CancellationToken.None);
        Console.Error.WriteLine($"a short sentence warm: {clock.Elapsed.TotalSeconds:0.00} s");
        using var cancel = new CancellationTokenSource(1500);
        var stopwatch = Stopwatch.StartNew();
        var stopped = false;
        try { Synthesize(string.Join(" ", Enumerable.Repeat("This is a very long sentence that keeps going and going", 8)) + ".", voice, cancel.Token); }
        catch (OperationCanceledException) { stopped = true; }
        var late = stopwatch.Elapsed.TotalSeconds - 1.5;
        Console.Error.WriteLine($"cancel: {(stopped ? "stopped" : "did not stop")} {late:0.00} s after the request");
        Shutdown();
        return peak > 0.05 && stopped && late < 1 ? 0 : 1;
    }

    private static long DirSize(string dir) => new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    // --self-test: the tokenizer, sampling, the VRAM decision, the idle unload, the clip and WAV helpers, Ready.
    public static void ChatterboxSelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Chatterbox self-test failed: " + what);
        }
        // a tiny GPT-2 tokenizer: byte-level (space = Ġ), merges by rank, tags whole, two end tokens appended
        var tokenizer = new Gpt2Tokenizer("""
            {"added_tokens":[{"id":9,"content":"<|endoftext|>"},{"id":10,"content":"[laugh]"}],
             "model":{"vocab":{"h":0,"e":1,"he":2,"Ġ":3,"Ġhe":4,"!":5,"y":6,"Ġh":7},"merges":[["Ġ","h"],["h","e"],["Ġh","e"]]}}
            """);
        var ids = tokenizer.Encode("he he[laugh]!");
        Check(ids is [2, 4, 10, 5, 9, 9], $"tokens [{string.Join(",", ids)}]");
        Check(tokenizer.Encode("hey") is [2, 6, 9, 9], "a word without a full merge");

        // sampling: greedy at temperature 0, the repetition penalty, a token outside top-p never picked
        Check(Sample([1f, 5f, 2f], [], 0f, new Random(1)) == 1, "greedy");
        Check(Sample([1f, 2.2f, 2f], [1], 0f, new Random(1)) == 2, "repetition penalty");
        var random = new Random(7);
        Check(Enumerable.Range(0, 200).All(_ => Sample([10f, 10f, -20f], [], 0.8f, random) != 2), "top-p");

        // the VRAM decision: loaded or unknown fits; less than NeedBytes free does not
        Check(WontFit(null, false) is null && WontFit(1L << 30, true) is null && WontFit(NeedBytes, false) is null, "fits");
        Check(WontFit((2L << 30) + (1L << 29), false) is { } why && why.Contains("2.5 GB") && why.Contains("3 GB"), "won't fit");

        // the idle unload: made on first Get, the same one while used, freed after the idle time, made again after
        var made = 0;
        var timeout = TimeSpan.FromMilliseconds(400);
        var slot = new IdleSlot<FakeEngine>(() => timeout, new object());
        var one = slot.Get(() => new FakeEngine(++made));
        Thread.Sleep(250);
        slot.Touch();
        Thread.Sleep(250);
        Check(slot.Loaded && !one.Disposed && slot.Get(() => new FakeEngine(++made)) == one, "used within the idle time stays");
        Check(SpinWait.SpinUntil(() => one.Disposed, 2000) && !slot.Loaded, "unloaded after the idle time");
        var two = slot.Get(() => new FakeEngine(++made));
        Check(made == 2 && two != one, "made again");
        slot.Release();
        Check(two.Disposed && !slot.Loaded, "Release frees");

        // clip and WAV helpers: a 48 kHz stereo file comes back as 24 kHz mono of its first channel, at most 20 s
        var area = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-chatterbox-test-{Guid.NewGuid():N}");
        var dataBefore = Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA");
        try
        {
            Directory.CreateDirectory(area);
            var stereo = Path.Combine(area, "in.wav");
            using (var writer = new WaveFileWriter(stereo, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
                for (var i = 0; i < 48000 * 22; i++)
                    writer.WriteSamples([0.5f, -0.5f], 0, 2);
            var clip = LoadClip(stereo);
            Check(Math.Abs(clip.Length - Rate * 20) < 50 && clip.Skip(100).Take(1000).All(f => f > 0.4f), $"clip of {clip.Length} samples");
            var round = Path.Combine(area, "out.wav");
            WriteWav(round, [0.25f, -0.25f]);
            Check(LoadClip(round) is [0.25f, -0.25f], "WAV round trip");

            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", area);
            Check(!ChatterboxRuntime.Ready && !CanRunNow(out var reason) && reason.Contains("not installed"), "nothing installed");
            Directory.CreateDirectory(Path.GetDirectoryName(ChatterboxRuntime.ModelFile("onnx/x"))!);
            File.WriteAllBytes(ChatterboxRuntime.ModelFile("tokenizer.json"), new byte[3_562_272]);
            File.WriteAllBytes(ChatterboxRuntime.ModelFile("onnx/language_model_fp16.onnx_data") + ".incomplete", new byte[10]);
            Check(!ChatterboxRuntime.Ready && ChatterboxRuntime.InstallBytes > 1_600_000_000, "a half-finished install counts as ready");
            var threw = false;
            try { Synthesize("hello", ChatterboxVoice.Default, CancellationToken.None); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "Synthesize without the install");
            ChatterboxRuntime.Uninstall();
            Check(!Directory.Exists(Path.GetDirectoryName(ChatterboxRuntime.ModelFile("tokenizer.json"))), "Uninstall deletes the models");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", dataBefore);
            try { Directory.Delete(area, recursive: true); } catch (Exception) { }
        }
    }

    private sealed class FakeEngine(int n) : IDisposable
    {
        public readonly int N = n;
        public volatile bool Disposed;
        public void Dispose() => Disposed = true;
    }
}

// One disposable thing made on demand and disposed after it has gone unused for the idle time (or on Release). _gate is the
// owner's lock (held during each use), so an idle unload never lands mid-use.
internal sealed class IdleSlot<T>(Func<TimeSpan> idleAfter, object _gate) where T : class, IDisposable
{
    private T? _value;
    private Timer? _timer;
    private DateTime _due;

    public bool Loaded => _value is not null;
    public T? Value => _value;

    public T Get(Func<T> make)
    {
        lock (_gate)
        {
            _value ??= make();
            Arm();
            return _value;
        }
    }

    public void Touch()
    {
        lock (_gate)
            if (_value is not null)
                Arm();
    }

    public void Release()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _value?.Dispose();
            _value = null;
        }
    }

    private void Arm()
    {
        var after = idleAfter();
        _due = DateTime.UtcNow + after;
        _timer ??= new Timer(_ => Expire());
        _timer.Change(after, Timeout.InfiniteTimeSpan);
    }

    private void Expire()
    {
        lock (_gate)
            if (_value is not null && DateTime.UtcNow >= _due) // not used while this waited for the lock
                Release();
    }
}

// GPT-2 byte-level BPE (tokenizer.json): the added tokens ([laugh], <|endoftext|>) kept whole, the GPT-2 split, merges by
// rank; the post-processor's two <|endoftext|> appended, as the model card's AutoTokenizer call does.
internal sealed class Gpt2Tokenizer
{
    private static readonly Regex Split = new(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+", RegexOptions.Compiled);
    private static readonly char[] ByteChar = ByteChars();
    private readonly Dictionary<string, int> _vocab = [];
    private readonly Dictionary<string, int> _ranks = []; // "a b" -> rank
    private readonly Dictionary<string, int> _added = [];
    private readonly Regex? _addedSplit;
    private readonly int _end;

    public Gpt2Tokenizer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var model = doc.RootElement.GetProperty("model");
        foreach (var p in model.GetProperty("vocab").EnumerateObject())
            _vocab[p.Name] = p.Value.GetInt32();
        var rank = 0;
        foreach (var m in model.GetProperty("merges").EnumerateArray())
            _ranks[m.ValueKind == JsonValueKind.Array ? m[0].GetString() + " " + m[1].GetString() : m.GetString()!] = rank++;
        foreach (var a in doc.RootElement.GetProperty("added_tokens").EnumerateArray())
            _added[a.GetProperty("content").GetString()!] = a.GetProperty("id").GetInt32();
        _end = _added["<|endoftext|>"];
        if (_added.Count > 0)
            _addedSplit = new Regex("(" + string.Join("|", _added.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + ")");
    }

    public long[] Encode(string text)
    {
        var ids = new List<long>();
        foreach (var part in _addedSplit?.Split(text) ?? [text])
        {
            if (_added.TryGetValue(part, out var tag))
            {
                ids.Add(tag);
                continue;
            }
            foreach (Match word in Split.Matches(part))
                ids.AddRange(Bpe(new string(Encoding.UTF8.GetBytes(word.Value).Select(b => ByteChar[b]).ToArray())).Select(t => (long)_vocab[t]));
        }
        ids.Add(_end);
        ids.Add(_end);
        return ids.ToArray();
    }

    private List<string> Bpe(string word)
    {
        var parts = word.Select(c => c.ToString()).ToList();
        while (parts.Count > 1)
        {
            var (best, at) = (int.MaxValue, -1);
            for (var i = 0; i < parts.Count - 1; i++)
                if (_ranks.TryGetValue(parts[i] + " " + parts[i + 1], out var r) && r < best)
                    (best, at) = (r, i);
            if (at < 0)
                break;
            parts[at] += parts[at + 1];
            parts.RemoveAt(at + 1);
        }
        return parts;
    }

    // GPT-2's bytes_to_unicode: printable bytes stand for themselves, the rest map to 256 and up.
    private static char[] ByteChars()
    {
        var map = new char[256];
        var next = 256;
        for (var b = 0; b < 256; b++)
            map[b] = b is >= '!' and <= '~' or >= 0xA1 and <= 0xAC or >= 0xAE and <= 0xFF ? (char)b : (char)next++;
        return map;
    }
}

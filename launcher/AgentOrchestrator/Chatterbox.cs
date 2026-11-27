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
// Runtime's DirectML provider, not a Python sidecar. The first real run (G4) put only the decoder on DirectML (Engine says why).
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
//    Its install would be ~6 GB (torch cu128 ~3 GB), and it needs a second process to supervise.
//  - CUDA first (G4, 2026-11-26): the CUDA 12 build of the same ONNX Runtime (Gpu.Windows nupkg) with cudart 12.9, cuBLAS 12.9,
//    cuFFT 11.4 and cuDNN 9.28 from NVIDIA's pip wheels into runtime\cuda\ (+2.5 GB; sm_120 runs through PTX JIT). All four
//    graphs on CUDA, the KV cache kept on the GPU: warm RTF 0.41, 11.7 ms a token, 3.2 GB of VRAM. DirectML+CPU is the fallback.
// Install on disk: ~1.7 GB (models 1,662 MB + runtime 36 MB + the default voice); the download is ~1.9 GB (Kokoro's 150 MB
// too when it is missing: the default voice is Kokoro af_heart reading a reference paragraph, chatterbox\voices\default.wav).
// With an NVIDIA GPU (LauncherLogic.HasNvidiaGpu) the CUDA set comes too: +2.4 GB on disk from a +1.6 GB download, so ~4.1 GB
// on disk from ~3.5 GB; without one it is never downloaded (DirectML+CPU only).
// Integrity = exact sizes + immutable pinned URLs over TLS: a pinned Hugging Face revision, versioned nuget.org packages and
// content-addressed PyPI wheel paths, each file checked against its expected size; no hashes beyond what those hosts guarantee.
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
    // Each native: the package (a nupkg or an NVIDIA pip wheel, both zips), its entry, the file in runtime\, its size. runtime\
    // holds the DirectML build (the fallback); runtime\cuda\ the CUDA 12 build of the same ONNX Runtime and the CUDA libraries
    // its CUDA provider imports (cudart, cuBLAS/cuBLASLt, cuFFT, cuDNN 9 with the sub-libraries it loads by name).
    private sealed record Native(string Package, string Entry, string File, long Size);
    private static string Nuget(string id, string version) => $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg";
    private const string Wheels = "https://files.pythonhosted.org/packages/";
    private static readonly string OrtDml = Nuget("microsoft.ml.onnxruntime.directml", OrtVersion), Dml = Nuget("microsoft.ai.directml", DirectMlVersion),
        OrtCuda = Nuget("microsoft.ml.onnxruntime.gpu.windows", OrtVersion),
        Cudart = Wheels + "59/df/e7c3a360be4f7b93cee39271b792669baeb3846c58a4df6dfcf187a7ffab/nvidia_cuda_runtime_cu12-12.9.79-py3-none-win_amd64.whl",
        Cublas = Wheels + "45/a1/a17fade6567c57452cfc8f967a40d1035bb9301db52f27808167fbb2be2f/nvidia_cublas_cu12-12.9.1.4-py3-none-win_amd64.whl",
        Cufft = Wheels + "20/ee/29955203338515b940bd4f60ffdbc073428f25ef9bfbce44c9a066aedc5c/nvidia_cufft_cu12-11.4.1.4-py3-none-win_amd64.whl",
        Cudnn = Wheels + "c7/bd/1d6709d2223e99f04079f16005f8ae6e11a14ee16b5eab6833686cf72fe1/nvidia_cudnn_cu12-9.28.1.4-py3-none-win_amd64.whl";
    private static readonly Dictionary<string, long> PackageSize = new()
    {
        [OrtDml] = 12_458_649, [Dml] = 202_292_617, [OrtCuda] = 172_417_405,
        [Cudart] = 3_591_604, [Cublas] = 553_153_899, [Cufft] = 200_067_309, [Cudnn] = 696_300_805,
    };
    private static readonly Native[] Natives =
    [
        new(OrtDml, "runtimes/win-x64/native/onnxruntime.dll", "onnxruntime.dll", 17_328_152),
        new(Dml, "bin/x64-win/DirectML.dll", "DirectML.dll", 18_527_776),
        // the CUDA build in its load order (Chatterbox.UseRuntime)
        new(Cudart, "nvidia/cuda_runtime/bin/cudart64_12.dll", @"cuda\cudart64_12.dll", 583_680),
        new(Cublas, "nvidia/cublas/bin/cublasLt64_12.dll", @"cuda\cublasLt64_12.dll", 668_669_952),
        new(Cublas, "nvidia/cublas/bin/cublas64_12.dll", @"cuda\cublas64_12.dll", 102_518_272),
        new(Cufft, "nvidia/cufft/bin/cufft64_11.dll", @"cuda\cufft64_11.dll", 287_136_768),
        new(Cudnn, "nvidia/cudnn/bin/cudnn_graph64_9.dll", @"cuda\cudnn_graph64_9.dll", 99_939_952),
        new(Cudnn, "nvidia/cudnn/bin/cudnn_engines_precompiled64_9.dll", @"cuda\cudnn_engines_precompiled64_9.dll", 522_326_640),
        new(Cudnn, "nvidia/cudnn/bin/cudnn_engines_runtime_compiled64_9.dll", @"cuda\cudnn_engines_runtime_compiled64_9.dll", 43_971_184),
        new(Cudnn, "nvidia/cudnn/bin/cudnn_engines_tensor_ir64_9.dll", @"cuda\cudnn_engines_tensor_ir64_9.dll", 156_784),
        new(Cudnn, "nvidia/cudnn/bin/cudnn_heuristic64_9.dll", @"cuda\cudnn_heuristic64_9.dll", 94_795_376),
        new(Cudnn, "nvidia/cudnn/bin/cudnn_ops64_9.dll", @"cuda\cudnn_ops64_9.dll", 90_087_024),
        new(Cudnn, "nvidia/cudnn/bin/cudnn_cnn64_9.dll", @"cuda\cudnn_cnn64_9.dll", 2_872_944),
        new(Cudnn, "nvidia/cudnn/bin/cudnn_adv64_9.dll", @"cuda\cudnn_adv64_9.dll", 229_649_008),
        new(Cudnn, "nvidia/cudnn/bin/cudnn_ext64_9.dll", @"cuda\cudnn_ext64_9.dll", 130_160),
        new(Cudnn, "nvidia/cudnn/bin/cudnn64_9.dll", @"cuda\cudnn64_9.dll", 272_496),
        new(OrtCuda, "runtimes/win-x64/native/onnxruntime_providers_shared.dll", @"cuda\onnxruntime_providers_shared.dll", 22_040),
        new(OrtCuda, "runtimes/win-x64/native/onnxruntime.dll", @"cuda\onnxruntime.dll", 14_430_752),
        new(OrtCuda, "runtimes/win-x64/native/onnxruntime_providers_cuda.dll", @"cuda\onnxruntime_providers_cuda.dll", 275_606_552),
    ];
    internal static IEnumerable<string> CudaFiles => Natives.Where(n => n.File.StartsWith(@"cuda\")).Select(n => RuntimeFile(n.File));
    // The natives this PC gets: the CUDA set only with an NVIDIA GPU (~1.6 GB of downloads nothing else could use).
    private static readonly bool Nvidia = LauncherLogic.HasNvidiaGpu();
    private static IEnumerable<Native> Needed(bool nvidia) => Natives.Where(n => nvidia || !n.File.StartsWith(@"cuda\"));
    internal static IEnumerable<string> NativeFiles(bool nvidia) => Needed(nvidia).Select(n => n.File);
    private static long PackageBytes => Needed(Nvidia).Select(n => n.Package).Distinct().Sum(p => PackageSize[p]);

    public static string Folder => Path.Combine(AppSettings.DataRoot, "chatterbox");
    public static string ModelFile(string name) => Path.Combine(Folder, Repo.Split('/')[1], name.Replace('/', Path.DirectorySeparatorChar));
    public static string RuntimeFile(string name) => Path.Combine(Folder, "runtime", name);
    public static string VoicesFolder => Path.Combine(Folder, "voices");
    public static string ClipPath(ChatterboxVoice voice) => Path.IsPathRooted(voice.Clip) ? voice.Clip : Path.Combine(VoicesFolder, voice.Clip);

    // Every file with its expected size, and the default voice: a half-finished install never counts.
    public static bool Ready =>
        Models.All(f => Size(ModelFile(f.Name)) == f.Size) && Needed(Nvidia).All(n => Size(RuntimeFile(n.File)) == n.Size)
        && Size(Path.Combine(VoicesFolder, DefaultClipName)) > 44;
    public static long InstallBytes => Models.Sum(f => f.Size) + Needed(Nvidia).Sum(n => n.Size); // on disk
    public static long DownloadBytes => Models.Sum(f => f.Size) + PackageBytes + (TtsModels.Ready ? 0 : TtsModels.Find(TtsModels.Chosen).DownloadBytes);
    private static long Size(string path) => new FileInfo(path) is { Exists: true } info ? info.Length : -1;

    // The runtime, the models (resumable), Kokoro when missing, then the default voice. Progress over DownloadBytes.
    public static async Task InstallAsync(IProgress<(long done, long total)> progress, CancellationToken ct)
    {
        var total = DownloadBytes;
        long done = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(RuntimeFile("x"))!);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Folder))!);
        if (drive.AvailableFreeSpace < DownloadBytes + InstallBytes) // a package and what it unpacks are on disk at once
            throw new IOException($"Chatterbox needs about {(DownloadBytes + InstallBytes) / (double)(1L << 30):0.0} GB free on {drive.Name} " +
                $"while it installs; {drive.AvailableFreeSpace / (double)(1L << 30):0.0} GB is");
        using (var nuget = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        {
            nuget.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
            foreach (var group in Needed(Nvidia).GroupBy(n => n.Package))
            {
                if (group.All(n => Size(RuntimeFile(n.File)) == n.Size))
                {
                    progress.Report((done += PackageSize[group.Key], total));
                    continue;
                }
                // ponytail: each package is one plain GET without resume (696 MB at most); the models get HubDownloader's
                var package = RuntimeFile(Path.GetFileName(group.Key) + ".incomplete");
                try
                {
                using (var response = await nuget.GetAsync(group.Key, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var body = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = File.Create(package);
                    var buffer = new byte[1 << 20];
                    for (int n; (n = await body.ReadAsync(buffer, ct)) > 0;)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                        progress.Report((done += n, total));
                    }
                }
                {
                    using var zip = ZipFile.OpenRead(package);
                    foreach (var (_, entry, file, size) in group)
                    {
                        var path = RuntimeFile(file);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        (zip.GetEntry(entry) ?? throw new InvalidDataException($"{group.Key} has no {entry}")).ExtractToFile(path + ".incomplete", overwrite: true);
                        if (Size(path + ".incomplete") != size)
                            throw new InvalidDataException($"{file} from {group.Key} is not the expected size");
                        File.Move(path + ".incomplete", path, overwrite: true);
                    }
                }
                }
                finally { File.Delete(package); } // cancelled or failed too: never a partial package left behind
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
            await TtsModels.Find(TtsModels.Chosen).DownloadAsync(new Relay<(long done, long total)>(p => progress.Report((start + p.done, total))), ct);
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
    internal const string DefaultReference = "Hello there! I'm really glad you stopped by today. Let me walk you through what I found, " +
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
    // GPU memory the engine takes once loaded (CUDA, all four graphs: 3,178 MB measured on the 5080, with margin).
    internal const long NeedBytes = 7L << 29;
    internal static TimeSpan IdleAfter = TimeSpan.FromMinutes(10);

    private static readonly object Gate = new();
    private static readonly IdleSlot<Engine> Slot = new(() => IdleAfter, Gate);
    private static readonly Dictionary<string, Conditioning> Conds = []; // clip path + write time -> its encoder outputs
    private static volatile bool _shutdown;
    private static string? _failure; // the last load error: no retry until it expires, Retry or Unload (Install, Uninstall)
    private static DateTime _failedAt;
    internal static TimeSpan FailureFor = TimeSpan.FromSeconds(60);
    // The load error while it holds (the Settings > Voice row shows it with Retry), else null.
    public static string? Failure => _failure is { } f && DateTime.UtcNow - _failedAt < FailureFor ? f : null;
    public static void Retry() => _failure = null;
    private static CancellationTokenSource _stop = new(); // Unload cancels it: the synthesis or load in progress ends at its next step
    internal static Func<string, CancellationToken, float[]>? Stub; // --ui-smoke: an engine that opens nothing and reads with this

    // While a chat model loads, Chatterbox does not load (reads go to the Kokoro stand-in): while loading() holds, at most until Until.
    private sealed record Hold(Func<bool> Loading, DateTime Until);
    private static volatile Hold _hold = new(() => false, DateTime.MinValue);
    public static void HoldWhile(Func<bool> loading, TimeSpan max) => _hold = new(loading, DateTime.UtcNow + max);
    internal static bool Holds(Func<bool> loading, DateTime until, DateTime now) => now < until && loading();
    private static bool Held => _hold is var h && Holds(h.Loading, h.Until, DateTime.UtcNow);
    private const string HeldReason = "Chatterbox waits for the chat model to load";

    // A sentence's speech-token budget: ~25 tokens a second of audio at ~15 characters a second, three times over, at least 100.
    // A sentence that misses its stop token ends there (its tail trimmed), not 40 s later.
    internal static int SpeechTokenCap(string text) => Math.Clamp(text.Length * 25 * 3 / 15, 100, MaxTokens);
    // The last read on this thread: speech tokens made and whether the cap ended it (SpeechPlayer's per-sentence log).
    [ThreadStatic] internal static (int Tokens, bool Capped)? LastRead;

    private static int _tokens; // speech tokens made and the language model's time on them (--chatterbox-say)
    private static double _lmSeconds;

    // Where the loaded engine runs ("CUDA", ...), or null when not loaded: the Settings > Voice status line.
    public static string? Provider => Slot.Value?.Provider;
    public static bool Loaded => Slot.Loaded;

    // Whether a Chatterbox voice can read now; else the reason (the caller reads with the voice's Kokoro stand-in).
    public static bool CanRunNow(out string reason)
    {
        reason = !ChatterboxRuntime.Ready ? "Chatterbox is not installed"
            : Failure is { } failure ? "Chatterbox could not start: " + failure
            : Held ? HeldReason
            : WontFit(Slot.Loaded ? null : FreeGpuBytes(), Slot.Loaded) ?? "";
        return reason.Length == 0;
    }

    // null when the engine fits: already loaded, the free memory unknown (no NVIDIA tools: DirectML tries, errors fall back),
    // or at least NeedBytes free.
    internal static string? WontFit(long? freeBytes, bool loaded) =>
        loaded || freeBytes is null || freeBytes >= NeedBytes ? null
        : $"Only {freeBytes / (double)(1L << 30):0.0} GB of GPU memory is free and Chatterbox needs about {NeedBytes / (double)(1L << 30):0.#} GB";

    // Before a chat engine starts: the engine leaves the GPU when no chat model is loaded yet (the load is about to take the
    // room) or the free memory is under 2 GB (unknown free memory: stays).
    internal static bool ShouldUnloadForChat(bool loaded, bool modelsLoaded, long? freeVram) =>
        loaded && (!modelsLoaded || freeVram < 2L << 30);

    // A call in a Chatterbox voice that fits beside its chat model (CallRoom at its start) keeps the engine for its own turns:
    // no hold and no unload while no model is loaded (the load fits beside it), and with the model loaded only when the free
    // memory falls under CallHeadroom (unknown: stays).
    internal const long CallHeadroom = 1L << 29;
    internal static bool KeepForCall(bool inCall, bool fits, bool modelsLoaded, long? freeVram) =>
        inCall && fits && (!modelsLoaded || !(freeVram < CallHeadroom));

    // The GPU memory Chatterbox would have once the chat model is loaded too (null: unknown, it tries): the free memory now,
    // less the model's estimate when it is not loaded yet, plus the engine's own when it is loaded.
    internal static long? CallRoom(long? freeVram, bool loaded, bool modelsLoaded, long modelBytes) =>
        freeVram - (modelsLoaded ? 0 : modelBytes) + (loaded ? NeedBytes : 0);

    // The toast at the start of a call whose Chatterbox voice does not fit beside its chat model.
    internal static string CallWontFitText(string voice, string standIn, string model, long context, long room) =>
        $"{voice} needs ~{NeedBytes / (double)(1L << 30):0.#} GB of GPU memory; with {model} at {context.ToString("N0", CultureInfo.InvariantCulture)} tokens only " +
        $"{Math.Max(0, room) / (double)(1L << 30):0.0} GB is free — the call uses its stand-in voice {standIn}. Lower the context length or pick a Kokoro voice for calls.";

    // The first GPU's free memory (nvidia-smi, read at most every 3 s), or null. The UI thread never waits on nvidia-smi: it
    // gets the last value (null before the first read) while a background read refreshes it.
    internal static long? FreeGpuBytes() => FreeGpu.Get(wait: System.Windows.Application.Current?.Dispatcher.CheckAccess() != true);
    internal static readonly TtlCache<long?> FreeGpu = new(ReadFreeGpuBytes, TimeSpan.FromSeconds(3));

    private static long? ReadFreeGpuBytes()
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
    // Throws when not installed, held, unloaded mid-read (Unload) or the engine fails; the caller falls back to Kokoro.
    public static float[] Synthesize(string text, ChatterboxVoice voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var stop = _stop.Token;
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        try { return SynthesizeLocked(text, voice, both.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Chatterbox made room for the chat model");
        }
    }

    private static float[] SynthesizeLocked(string text, ChatterboxVoice voice, CancellationToken ct)
    {
        while (!Monitor.TryEnter(Gate, 50))
            ct.ThrowIfCancellationRequested();
        try
        {
            ObjectDisposedException.ThrowIf(_shutdown, typeof(Chatterbox));
            if (Stub is { } stub)
            {
                Load(ct);
                try { return stub(text, ct); }
                finally { Slot.Touch(); }
            }
            if (!ChatterboxRuntime.Ready)
                throw new InvalidOperationException("Install Chatterbox in Settings > Voice");
            var engine = Load(ct);
            var clip = ChatterboxRuntime.ClipPath(voice);
            var key = clip + "|" + File.GetLastWriteTimeUtc(clip).Ticks;
            if (!Conds.TryGetValue(key, out var cond) || cond.Owner != engine)
            {
                cond?.Free(); // made by an engine since unloaded
                foreach (var old in Conds.Where(kv => kv.Key.StartsWith(clip + "|") && kv.Key != key).ToList()) // the clip rewritten (preview.wav)
                {
                    old.Value.Free();
                    Conds.Remove(old.Key);
                }
                Conds[key] = cond = engine.Condition(LoadClip(clip), ct);
            }
            try { return engine.Generate(text, cond, (float)voice.Temperature, ct); }
            finally { Slot.Touch(); }
        }
        finally { Monitor.Exit(Gate); }
    }

    // The engine, loaded when it is not (under Gate; not while held); a load error is kept for CanRunNow (no retry until Unload).
    private static Engine Load(CancellationToken ct)
    {
        if (!Slot.Loaded && Held)
            throw new InvalidOperationException(HeldReason);
        try { return Slot.Get(() => new Engine(ct)); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            (_failure, _failedAt) = (e.Message, DateTime.UtcNow);
            throw;
        }
    }

    // Starts loading the engine in the background, so a read's first sentence does not wait the ~20 s cold load: called when a
    // read in a Chatterbox voice starts (SpeechPlayer, /api/speak/plan), and when one is chosen. Nothing when it is loaded or
    // cannot run now; a load error is kept as Synthesize keeps it (the read falls back to Kokoro).
    public static Task WarmUpAsync() => Loaded || !ChatterboxRuntime.Ready ? Task.CompletedTask : Task.Run(() =>
    {
        if (!CanRunNow(out _))
            return;
        var stop = _stop.Token;
        lock (Gate)
            if (!_shutdown)
                try { Load(stop); }
                catch (Exception) { } // kept in _failure
    });

    // Frees the GPU (before a model load that needs the room, Uninstall); the next Synthesize loads again. The synthesis or load
    // in progress is cancelled first, so the wait for Gate is one model step or session open; FreeGpuForChat runs it off the UI thread.
    public static void Unload()
    {
        Interlocked.Exchange(ref _stop, new()).Cancel(); // not disposed: a synthesis may still hold its token
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
        public readonly string Provider; // for the status line: "CUDA", "decoder on DirectML, the rest on the CPU", "CPU"
        private readonly OrtMemoryInfo _cache; // where the language model's KV cache lives between steps: the GPU on CUDA

        // ct (Unload, the read's own) is checked before each session opens, so a cold load stops within one open.
        public Engine(CancellationToken ct)
        {
          try
          {
            if (Stub is not null)
            {
                (_encoder, _embed, _lm, _decoder, _tokenizer, _cache, Provider) = (null!, null!, null!, null!, null!, null!, "stub");
                return;
            }
            UseRuntime();
            _tokenizer = new Gpt2Tokenizer(File.ReadAllText(ChatterboxRuntime.ModelFile("tokenizer.json")));
            SessionOptions Options(bool gpu)
            {
                var options = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                if (gpu && Cuda)
                {
                    // cuDNN's default exhaustive algorithm search reruns for every new sequence length: ~7 GB taken and a cancel
                    // waiting 37 s on it in G4. The heuristic pick and an arena that grows only as asked.
                    using var cuda = new OrtCUDAProviderOptions();
                    cuda.UpdateOptions(new Dictionary<string, string> { ["device_id"] = "0", ["cudnn_conv_algo_search"] = "HEURISTIC", ["arena_extend_strategy"] = "kSameAsRequested" });
                    options.AppendExecutionProvider_CUDA(cuda);
                }
                else if (gpu)
                    options.AppendExecutionProvider_DML(0);
                return options;
            }
            if (Cuda)
            {
                // all four graphs on CUDA; a CUDA failure here (no device, out of memory) is the load error: Kokoro reads
                (_encoder, _embed, _lm, _decoder) = (Open("speech_encoder", true), Open("embed_tokens", true), Open("language_model", true), Open("conditional_decoder", true));
                (Gpu, Provider) = (true, "CUDA");
                _cache = new OrtMemoryInfo(OrtMemoryInfo.allocatorCUDA, OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
                return;
            }
            _cache = OrtMemoryInfo.DefaultInstance;
            InferenceSession Open(string name, bool gpu)
            {
                ct.ThrowIfCancellationRequested();
                using var options = Options(gpu);
                return new InferenceSession(ChatterboxRuntime.ModelFile($"onnx/{name}_fp16.onnx"), options);
            }
            // Only the decoder runs on the GPU: DirectML 1.24.4 on the 5080 (G4) rejects the speech encoder's MultiHeadAttention and
            // embed_tokens' Slice for a lone speech token ("The parameter is incorrect"), and its GroupQueryAttention with a past
            // computes wrong tokens (no stop token, 290 for five words), so the language model runs on the CPU (~42 ms a token).
            (_encoder, _embed, _lm) = (Open("speech_encoder", false), Open("embed_tokens", false), Open("language_model", false));
            try { (_decoder, Gpu, Provider) = (Open("conditional_decoder", true), true, "decoder on DirectML, the rest on the CPU"); }
            catch (Exception e) when (e is OnnxRuntimeException or EntryPointNotFoundException) // no DirectML device: the CPU
            {
                (_decoder, Gpu, Provider) = (Open("conditional_decoder", false), false, "CPU");
            }
          }
          catch
          {
            Dispose(); // the sessions already open (a CUDA out of memory on the third): their GPU memory goes now
            throw;
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
            var clock = Stopwatch.StartNew();
            long seq = 0;
            try
            {
                var cap = SpeechTokenCap(text);
                for (var step = 0; step < cap; step++)
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
                    OrtValue[] outputs;
                    try { outputs = RunLm(inputs, ct); }
                    finally { embedded.Dispose(); }
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
            (_tokens, _lmSeconds) = (_tokens + generated.Count - 1, _lmSeconds + clock.Elapsed.TotalSeconds);
            LastRead = (generated.Count - 1, generated[^1] != StopSpeech);
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

        // The language model through an IOBinding: the logits come back to host memory, the present KV cache stays in _cache
        // (on CUDA the GPU), so the next step binds it as its past without a copy.
        private OrtValue[] RunLm(List<(string Name, OrtValue Value)> inputs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var options = new RunOptions();
            using var stop = ct.Register(() => options.Terminate = true);
            using var binding = _lm.CreateIoBinding();
            foreach (var (name, value) in inputs.Where(i => _lm.InputMetadata.ContainsKey(i.Name)))
                binding.BindInput(name, value);
            var names = _lm.OutputNames;
            binding.BindOutputToDevice(names[0], OrtMemoryInfo.DefaultInstance);
            foreach (var name in names.Skip(1))
                binding.BindOutputToDevice(name, _cache);
            try
            {
                _lm.RunWithBinding(options, binding);
                return binding.GetOutputValues().ToArray(); // not disposed: the caller owns the values
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        }

        public void Dispose()
        {
            foreach (var s in new[] { _encoder, _embed, _lm, _decoder })
                s?.Dispose();
            if (_cache is not null && _cache != OrtMemoryInfo.DefaultInstance)
                _cache.Dispose();
        }
    }

    // Temperature, repetition penalty 1.2 (the model card's) and top-p 0.95 over the last position's logits.
    internal static long Sample(float[] logits, IEnumerable<long> generated, float temperature, Random random)
    {
        if (logits.Any(float.IsNaN))
            throw new InvalidOperationException("The model produced invalid output");
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
    // The CUDA build (runtime\cuda\) when the NVIDIA driver and every CUDA file load (each by full path, so the by-name loads
    // inside find them loaded), else the DirectML build. One build per process: the managed binding binds its natives once.
    private static bool _resolver;
    internal static bool Cuda { get; private set; }
    private static void UseRuntime()
    {
        if (_resolver)
            return;
        // sherpa-onnx-c-api.dll (Kokoro, Parakeet) imports "onnxruntime.dll" by name, and Windows binds a by-name import to any
        // loaded module of that name: ours, loaded first, would give sherpa ONNX Runtime 1.24.4 where it asks for 1.28's API
        // (null, then an access violation). Loaded here first, through the same probing as its DllImport, it binds to its own
        // onnxruntime.dll beside it; ours below comes by full path, a second module, and nothing else imports it by name.
        NativeLibrary.TryLoad("sherpa-onnx-c-api", typeof(SherpaOnnx.VersionInfo).Assembly, null, out _);
        // the CUDA provider is not preloaded: its DllMain fails (1114) before onnxruntime.dll hosts it; onnxruntime loads it from beside itself
        Cuda = NativeLibrary.TryLoad("nvcuda.dll", out _) && File.Exists(ChatterboxRuntime.RuntimeFile(@"cuda\onnxruntime_providers_cuda.dll"))
            && ChatterboxRuntime.CudaFiles.Where(f => !f.EndsWith("onnxruntime_providers_cuda.dll")).All(f => NativeLibrary.TryLoad(f, out _));
        if (!Cuda)
            NativeLibrary.Load(ChatterboxRuntime.RuntimeFile("DirectML.dll"));
        var ort = NativeLibrary.Load(ChatterboxRuntime.RuntimeFile(Cuda ? @"cuda\onnxruntime.dll" : "onnxruntime.dll"));
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
        var before = FreeGpu.Get(wait: true); // this runs on the UI thread, where FreeGpuBytes does not wait
        if (!CanRunNow(out var reason))
        {
            Console.Error.WriteLine("cannot run: " + reason);
            return 1;
        }
        var clock = Stopwatch.StartNew();
        var all = new List<float>();
        double first = 0, lm = 0;
        var sane = true;
        (_tokens, _lmSeconds) = (0, 0);
        foreach (var sentence in SpeechText.Sentences(SpeechText.ForSpeech(text, new TtsSettings())))
        {
            var made1 = Synthesize(sentence, voice, CancellationToken.None);
            all.AddRange(made1);
            if (first == 0)
                first = clock.Elapsed.TotalSeconds;
            // a looping language model (no stop token) makes far too much audio: 0.03-0.25 s a character is speech
            var perChar = made1.Length / (double)Rate / sentence.Length;
            sane &= perChar is >= 0.03 and <= 0.25;
            Console.Error.WriteLine($"  {made1.Length / (double)Rate:0.00} s for {sentence.Length} characters ({perChar:0.000} s each): {sentence}");
        }
        var made = clock.Elapsed.TotalSeconds;
        lm = _lmSeconds;
        var after = FreeGpu.Get(wait: true);
        var samples = all.ToArray();
        WriteWav(wav, samples);
        var peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        var seconds = samples.Length / (double)Rate;
        Console.Error.WriteLine($"{voice.Name}: {Provider}, {seconds:0.00} s of audio, first sentence in {first:0.00} s (incl. load), all in {made:0.00} s, " +
            $"RTF {made / Math.Max(seconds, 0.01):0.00}, {_tokens} tokens at {lm * 1000 / Math.Max(_tokens, 1):0.0} ms each, peak {peak:0.000}, GPU memory taken {((before ?? 0) - (after ?? 0)) >> 20} MB");
        // warm: the same text again, the engine and the voice's conditioning loaded
        clock.Restart();
        double warmFirst = 0, warmSeconds = 0;
        foreach (var sentence in SpeechText.Sentences(SpeechText.ForSpeech(text, new TtsSettings())))
        {
            warmSeconds += Synthesize(sentence, voice, CancellationToken.None).Length / (double)Rate;
            if (warmFirst == 0)
                warmFirst = clock.Elapsed.TotalSeconds;
        }
        Console.Error.WriteLine($"warm: first sentence in {warmFirst:0.00} s, RTF {clock.Elapsed.TotalSeconds / Math.Max(warmSeconds, 0.01):0.00}");
        using var cancel = new CancellationTokenSource(1500);
        var stopwatch = Stopwatch.StartNew();
        var stopped = false;
        try { Synthesize(string.Join(" ", Enumerable.Repeat("This is a very long sentence that keeps going and going", 8)) + ".", voice, cancel.Token); }
        catch (OperationCanceledException) { stopped = true; }
        var late = stopwatch.Elapsed.TotalSeconds - 1.5;
        Console.Error.WriteLine($"cancel: {(stopped ? "stopped" : "did not stop")} {late:0.00} s after the request");
        Shutdown();
        if (!sane)
            Console.Error.WriteLine("a sentence's audio length is outside 0.03-0.25 s a character");
        return peak > 0.05 && stopped && late < 1 && sane ? 0 : 1;
    }

    // --speech-order-check <out.wav> cb|kokoro: the two ONNX Runtimes in one process. Installs Chatterbox (with Kokoro) and
    // Parakeet into a test data folder when missing and exits (the install reads with Kokoro). Installed: cb is Chatterbox one
    // sentence, Kokoro one sentence into out.wav, Parakeet transcribing it; kokoro is the reverse (Parakeet on the default
    // voice's clip). 0 when every step ran; the onnxruntime modules loaded on stderr.
    public static int SpeechOrderCheck(string wav, string first)
    {
        if (!ChatterboxRuntime.Ready || !SpeechModels.Ready)
            return (ChatterboxRuntime.Ready || Speech.TestDownload("--speech-order-check", ChatterboxRuntime.InstallAsync))
                && (SpeechModels.Ready || Speech.TestDownload("--speech-order-check", SpeechModels.DownloadAsync)) ? 0 : 2;
        void Cb() => Console.Error.WriteLine($"chatterbox ({Provider}): {Synthesize("Hello there, this is Chatterbox reading.", ChatterboxVoice.Default, CancellationToken.None).Length} samples");
        void Kokoro()
        {
            var samples = Tts.Synthesize("The quick brown fox jumps over the lazy dog.", new VoiceChoice(Tts.Default), 1f);
            WriteWav(wav, samples);
            Console.Error.WriteLine($"kokoro: {samples.Length} samples");
        }
        var code = 0;
        void Stt(string file) => code |= Speech.TranscribeFile(file);
        if (first == "cb") { Cb(); Kokoro(); Stt(wav); }
        else { Stt(Path.Combine(ChatterboxRuntime.VoicesFolder, ChatterboxRuntime.DefaultClipName)); Kokoro(); Cb(); }
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            if (m.ModuleName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine("loaded: " + m.FileName);
        Shutdown();
        return code;
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
        // the speech-token cap: at least 100, ~5 tokens a character (77 characters: 385), never past MaxTokens
        Check(SpeechTokenCap("Hi.") == 100 && SpeechTokenCap(new string('a', 77)) == 385 && SpeechTokenCap(new string('a', 5000)) == MaxTokens,
            $"the speech-token cap: {SpeechTokenCap("Hi.")}, {SpeechTokenCap(new string('a', 77))}, {SpeechTokenCap(new string('a', 5000))}");
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
        var invalid = false;
        try { Sample([float.NaN, 1f], [], 0.8f, random); } catch (InvalidOperationException e) { invalid = e.Message.Contains("invalid output"); }
        Check(invalid, "NaN logits are an engine error");

        // the VRAM decision: loaded or unknown fits; less than NeedBytes free does not
        Check(WontFit(null, false) is null && WontFit(1L << 30, true) is null && WontFit(NeedBytes, false) is null, "fits");
        Check(WontFit(1L << 29, false) is { } why && why.Contains("0.5 GB") && why.Contains("about 3.5 GB"), "won't fit");

        // the unload before a chat load: only when loaded, and no model yet or under 2 GB free
        Check(Chatterbox.ShouldUnloadForChat(true, false, null) && Chatterbox.ShouldUnloadForChat(true, true, 1L << 30)
            && !Chatterbox.ShouldUnloadForChat(true, true, 3L << 30) && !Chatterbox.ShouldUnloadForChat(true, true, null)
            && !Chatterbox.ShouldUnloadForChat(false, false, 0), "unload for chat");
        // a call that fits keeps the engine: no model yet, or loaded with at least CallHeadroom free (unknown: kept)
        Check(KeepForCall(true, true, false, null) && KeepForCall(true, true, true, 1L << 30) && KeepForCall(true, true, true, null)
            && !KeepForCall(true, true, true, 1L << 28) && !KeepForCall(true, false, true, 8L << 30) && !KeepForCall(false, true, true, 8L << 30), "keep for a call");
        Check(CallRoom(null, false, false, 8L << 30) is null && CallRoom(14L << 30, false, false, 12L << 30) == 2L << 30
            && CallRoom(1L << 30, true, true, 12L << 30) == (1L << 30) + NeedBytes && CallRoom(5L << 30, false, true, 12L << 30) == 5L << 30, "the room beside the model");
        Check(CallWontFitText("GPT_Ember", "af_heart", "Model-9B-Q5_K_M", 120000, 2L << 30) == "GPT_Ember needs ~3.5 GB of GPU memory; with Model-9B-Q5_K_M at 120,000 tokens "
            + "only 2.0 GB is free — the call uses its stand-in voice af_heart. Lower the context length or pick a Kokoro voice for calls.", "the fit message");

        // the hold while a chat model loads: while loading() holds and only until its time is up
        var now = DateTime.UtcNow;
        Check(Holds(() => true, now.AddSeconds(1), now) && !Holds(() => false, now.AddSeconds(1), now)
            && !Holds(() => true, now, now) && !Holds(() => true, now.AddSeconds(-1), now), "the hold");

        // nvidia-smi's cache: one read within the ttl; stale, a no-wait Get returns the last value at once and refreshes behind
        var reads = 0;
        var cache = new TtlCache<int>(() => { Thread.Sleep(300); return ++reads; }, TimeSpan.FromMilliseconds(600));
        Check(cache.Get(true) == 1 && cache.Get(true) == 1 && cache.Get(false) == 1 && reads == 1, "a fresh value is not read again");
        Thread.Sleep(700);
        var quick = Stopwatch.StartNew();
        Check(cache.Get(false) == 1 && quick.ElapsedMilliseconds < 100, $"a stale no-wait Get waited {quick.ElapsedMilliseconds} ms");
        Check(SpinWait.SpinUntil(() => cache.Get(false) == 2, 2000) && cache.Get(true) == 2 && reads == 2, "refreshed in the background, once");

        // the CUDA set (~1.6 GB of downloads) only on a PC with an NVIDIA GPU; the DirectML build everywhere
        var plain = ChatterboxRuntime.NativeFiles(false).ToList();
        var nvidia = ChatterboxRuntime.NativeFiles(true).ToList();
        Check(plain is ["onnxruntime.dll", "DirectML.dll"] && nvidia.Contains(@"cuda\cudart64_12.dll") && nvidia.Contains(@"cuda\onnxruntime.dll")
            && plain.All(nvidia.Contains), $"native files: {string.Join(", ", plain)}");

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

// The last value read, kept for ttl. A stale one is read again: now (wait), or else in the background with the last value
// returned meanwhile. The read runs outside the lock, so a no-wait Get never waits on one in progress.
internal sealed class TtlCache<T>(Func<T> read, TimeSpan ttl)
{
    private readonly object _gate = new();
    private T _value = default!;
    private DateTime _at = DateTime.MinValue;
    private Task? _refresh;

    public T Get(bool wait)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _at < ttl)
                return _value;
            if (!wait)
            {
                if (_refresh is not { IsCompleted: false })
                    _refresh = Task.Run(Refresh);
                return _value;
            }
        }
        return Refresh();
    }

    private T Refresh()
    {
        var value = read();
        lock (_gate)
            (_value, _at) = (value, DateTime.UtcNow);
        return value;
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

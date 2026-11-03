using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// A rough GPU memory estimate for a GGUF model and its load options, as the worker's settings show it: the weights on the
// GPU, the KV cache for the context, the compute buffers and the vision projector. Sizes are bytes.
internal static class VramEstimate
{
    public sealed record Result(long Weights, long Kv, long Compute, long Vision)
    {
        public long Total => Weights + Kv + Compute + Vision;
    }

    // Bytes per KV cache element for each cache_type_kv (the quantized types store 32 values per block).
    private static double KvBytes(string type) => type switch
    {
        "f32" => 4, "q8_0" => 34 / 32.0, "q4_0" or "iq4_nl" => 18 / 32.0, "q4_1" => 20 / 32.0, "q5_0" => 22 / 32.0, "q5_1" => 24 / 32.0,
        _ => 2, // f16, bf16, the default
    };

    // ponytail: CUDA context and scratch not tied to the model, a fixed guess; measure per GPU if it matters.
    private const long Overhead = 400L << 20;

    // meta: the GGUF header values (LauncherLogic.ReadGgufMetadata). files: the model's size (every split part) and its
    // vision projector's (0: none). options: the shown option values (ModelOptions keys, "" = default). Null when the header
    // lacks the layer count.
    public static Result? Estimate(IReadOnlyDictionary<string, string>? meta, long modelBytes, long visionBytes, IReadOnlyDictionary<string, string> options)
    {
        if (meta?.GetValueOrDefault("general.architecture") is not { } arch || Num(meta, arch + ".block_count") is not { } layersD || layersD < 1)
            return null;
        var layers = (int)layersD;
        long Option(string key, long fallback) => long.TryParse(options.GetValueOrDefault(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        var context = Option("max_seq_length", 0);
        if (context <= 0)
            context = Math.Min(ModelOptions.ModelContext(meta) ?? 4096, ModelOptions.ContextCap);
        var gpuLayers = Option("gpu_layers", -1);
        var onGpu = gpuLayers < 0 || gpuLayers >= layers ? 1.0 : (double)gpuLayers / layers;

        // Weights: the file's share for the GPU layers, less the experts of the MoE layers kept on the CPU.
        // ponytail: experts taken as 85% of an MoE layer; the real split is in the tensor list, not the header.
        var weights = modelBytes * onGpu;
        if (Num(meta, arch + ".expert_count") is > 0)
            weights -= modelBytes * 0.85 * Math.Min(Option("n_cpu_moe", 0), layers) / layers;

        // KV cache: per token, each attention layer's KV heads times the key and value widths. Hybrid models (a list of
        // per-layer KV head counts with zeros, or full_attention_interval) cache only their attention layers; sliding-window
        // layers (sliding_window_pattern) only the window plus a micro-batch, at their own widths.
        // ponytail: layers that share another layer's KV (Gemma 4 E-series) are counted as their own.
        var ubatch = Option("n_ubatch", 512);
        var heads = List(meta, arch + ".attention.head_count").DefaultIfEmpty(0).Max();
        var embd = Num(meta, arch + ".embedding_length") ?? 0;
        var keyWidth = Num(meta, arch + ".attention.key_length") ?? (heads > 0 ? embd / heads : 0);
        var valueWidth = Num(meta, arch + ".attention.value_length") ?? keyWidth;
        var kvHeads = List(meta, arch + ".attention.head_count_kv");
        var perLayer = kvHeads.Count == layers ? kvHeads : Enumerable.Repeat(kvHeads.DefaultIfEmpty(heads).First(), layers).ToList();
        if (kvHeads.Count != layers && Num(meta, arch + ".full_attention_interval") is >= 1 and var interval)
            perLayer = perLayer.Select((h, i) => (i + 1) % (int)interval == 0 ? h : 0).ToList();
        var window = Num(meta, arch + ".attention.sliding_window") ?? 0;
        var pattern = List(meta, arch + ".attention.sliding_window_pattern");
        var swaKey = Num(meta, arch + ".attention.key_length_swa") ?? keyWidth;
        var swaValue = Num(meta, arch + ".attention.value_length_swa") ?? valueWidth;
        var elements = perLayer.Select((h, i) => window > 0 && pattern.Count == layers && pattern[i] != 0
            ? h * (swaKey + swaValue) * Math.Min(context, window + ubatch)
            : h * (keyWidth + valueWidth) * context).Sum();
        var kv = elements * KvBytes(options.GetValueOrDefault("cache_type_kv", "")) * onGpu;

        // Compute: the output logits for one micro-batch dominate (vocabulary x 4 bytes per token), plus the hidden states.
        var vocab = Num(meta, "tokenizer.ggml.tokens.count") ?? 32000;
        var compute = ubatch * (vocab + embd * 8) * 4 + Overhead;

        var vision = options.GetValueOrDefault("disable_vision") == "on" || options.GetValueOrDefault("mmproj_offload") != "on" ? 0 : visionBytes; // on the CPU unless Vision on GPU
        return new Result((long)Math.Max(0, weights), (long)kv, (long)compute, vision);
    }

    private static double? Num(IReadOnlyDictionary<string, string> meta, string key) =>
        double.TryParse(meta.GetValueOrDefault(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // A value that may be one number or a comma-separated per-layer list.
    private static List<double> List(IReadOnlyDictionary<string, string> meta, string key) =>
        meta.GetValueOrDefault(key, "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList();

    private static readonly Regex SplitPart = new(@"-\d{5}-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase);

    // The model's bytes (all parts of a split model) and the vision projector beside it (0: none).
    public static (long Model, long Vision) FileSizes(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            var model = SplitPart.Match(name) is { Success: true } m
                ? Directory.EnumerateFiles(dir, SplitPart.Replace(name, "") + "-*-of-" + m.Groups[1].Value + ".gguf").Sum(f => new FileInfo(f).Length)
                : new FileInfo(path).Length;
            var vision = LauncherLogic.VisionProjector(path) is { } projector ? new FileInfo(projector).Length : 0;
            return (model, vision);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return (0, 0); }
    }

    public static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    internal static void SelfTest()
    {
        // A Llama-style 32-layer model with 8 KV heads of width 128: 128 KiB of f16 KV per token.
        var meta = new Dictionary<string, string>
        {
            ["general.architecture"] = "llama", ["llama.block_count"] = "32", ["llama.embedding_length"] = "4096", ["llama.attention.head_count"] = "32",
            ["llama.attention.head_count_kv"] = "8", ["llama.context_length"] = "131072", ["tokenizer.ggml.tokens.count"] = "128000",
        };
        var o = new Dictionary<string, string> { ["max_seq_length"] = "8192" };
        var r = Estimate(meta, 5L << 30, 0, o)!;
        if (r.Weights != 5L << 30 || r.Kv != 8192L * 32 * 8 * 256 * 2 || r.Compute != 512L * (128000 + 4096 * 8) * 4 + Overhead || r.Vision != 0)
            throw new InvalidOperationException("VramEstimate dense");
        o["cache_type_kv"] = "q8_0";
        o["gpu_layers"] = "16";
        o["disable_vision"] = "on";
        var half = Estimate(meta, 5L << 30, 1L << 30, o)!;
        if (half.Weights != 5L << 29 || half.Kv != (long)(r.Kv / 2 * (34 / 32.0) / 2) || half.Vision != 0 || Estimate(meta, 1, 1L << 30, new Dictionary<string, string> { ["mmproj_offload"] = "on" })!.Vision != 1L << 30 ||
            Estimate(meta, 1, 1L << 30, new Dictionary<string, string>())!.Vision != 0) // on the CPU by default
            throw new InvalidOperationException("VramEstimate options");
        // Empty context: the model's own, capped at ContextCap.
        if (Estimate(meta, 0, 0, new Dictionary<string, string>())!.Kv != 32768L * 32 * 8 * 256 * 2)
            throw new InvalidOperationException("VramEstimate context");
        // Hybrid: KV only on every 4th layer; a per-layer list with zeros works the same.
        meta["llama.full_attention_interval"] = "4";
        var hybrid = Estimate(meta, 0, 0, new Dictionary<string, string> { ["max_seq_length"] = "8192" })!;
        meta.Remove("llama.full_attention_interval");
        meta["llama.attention.head_count_kv"] = string.Join(",", Enumerable.Range(0, 32).Select(i => (i + 1) % 4 == 0 ? "8" : "0"));
        if (hybrid.Kv != r.Kv / 4 || Estimate(meta, 0, 0, new Dictionary<string, string> { ["max_seq_length"] = "8192" })!.Kv != r.Kv / 4)
            throw new InvalidOperationException("VramEstimate hybrid");
        // Sliding window: pattern layers hold window + micro-batch tokens at the SWA widths.
        var swa = new Dictionary<string, string>
        {
            ["general.architecture"] = "g", ["g.block_count"] = "2", ["g.attention.head_count"] = "8", ["g.attention.head_count_kv"] = "2",
            ["g.attention.key_length"] = "512", ["g.attention.value_length"] = "512", ["g.attention.key_length_swa"] = "256", ["g.attention.value_length_swa"] = "256",
            ["g.attention.sliding_window"] = "512", ["g.attention.sliding_window_pattern"] = "1,0",
        };
        if (Estimate(swa, 0, 0, new Dictionary<string, string> { ["max_seq_length"] = "8192" })!.Kv != (2 * 512 * 1024 + 2 * 1024 * 8192) * 2)
            throw new InvalidOperationException("VramEstimate swa");
        // MoE: experts of CPU layers leave the GPU.
        meta["llama.expert_count"] = "128";
        if (Estimate(meta, 10L << 30, 0, new Dictionary<string, string> { ["n_cpu_moe"] = "16" })!.Weights != (long)((10L << 30) * (1 - 0.85 / 2)))
            throw new InvalidOperationException("VramEstimate moe");
        if (Estimate(new Dictionary<string, string> { ["general.architecture"] = "x" }, 1, 0, o) is not null || Gb(3L << 29) != "1.5 GB")
            throw new InvalidOperationException("VramEstimate missing");
    }
}

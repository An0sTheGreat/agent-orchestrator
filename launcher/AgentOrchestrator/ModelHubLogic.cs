using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// The Models Hub's UI-free logic: Hugging Face search URLs and paging, the /api/models listing, ranking and a typo-tolerant
// search over the fetched rows, the GGUF variants of one repo, and whether a variant fits this machine. Facts in
// docs/model-hub-findings.md. No network here.
internal static class HubLogic
{
    public sealed record HubSort(string Id, string Label);

    public static readonly IReadOnlyList<HubSort> Sorts =
    [
        new("latest", "Latest"), new("newest", "Newest"), new("trending", "Trending"),
        new("downloads", "Most downloads"), new("updated", "Recently updated"), new("likes", "Most likes"),
    ];

    public sealed record HubModel(string Id, string Owner, string Name, long Downloads, long Likes, DateTimeOffset? LastModified,
        DateTimeOffset? CreatedAt, double? TrendingScore, long? ParamCount, string? PipelineTag, bool Gated, bool IsGguf, bool IsDiffusion);

    public sealed record HubFile(string Name, long Size);

    public sealed record HubVariant(string Label, string Quant, long SizeBytes, long DownloadBytes, IReadOnlyList<HubFile> Files, IReadOnlyList<HubFile> Companions);

    public sealed record HubVariants(string Sha, bool HasVision, IReadOnlyList<HubVariant> Variants);

    public enum Fit { Unknown, Fits, Marginal, Partial, Oom }

    // What a model makes. Text includes vision and other multimodal chat.
    [Flags]
    public enum HubKind { Text = 1, Audio = 2, Image = 4, Video = 8 }

    // A model maker (family) for the brand filter and row icons. IconKey: a file stem in Assets\Vendors, or null.
    public sealed record HubBrand(string Key, string Label, string? IconKey, HubKind Kinds);

    // ---- JSON helpers: every field may be missing or null ----

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static double? Dbl(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static DateTimeOffset? Date(JsonElement e, string p) =>
        Str(e, p) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static readonly HashSet<string> DiffusionPipelines = new(StringComparer.OrdinalIgnoreCase)
    {
        "text-to-image", "image-to-image", "text-to-video", "image-to-video", "video-to-video", "unconditional-image-generation", "text-to-3d", "image-to-3d",
    };

    // ---- search URL and paging ----

    private static string SortField(string sortId) => sortId switch
    {
        "latest" or "newest" => "createdAt",
        "trending" => "trendingScore",
        "downloads" => "downloads",
        "updated" => "lastModified",
        "likes" => "likes",
        _ => throw new ArgumentException("Unknown sort: " + sortId, nameof(sortId)),
    };

    private static readonly string[] Expand = ["downloads", "likes", "lastModified", "createdAt", "trendingScore", "pipeline_tag", "gated", "tags", "library_name", "gguf"];

    // "latest" asks HF for the newest; the UI may add a trendingScore page, and Rank re-orders the union.
    public static Uri SearchUrl(string query, string sortId, int limit = 100, string? pipelineTag = null)
    {
        var q = new List<string> { "filter=gguf" };
        if (!string.IsNullOrWhiteSpace(pipelineTag))
            q.Add("pipeline_tag=" + Uri.EscapeDataString(pipelineTag.Trim()));
        if (query.Trim() is { Length: > 0 } search)
            q.Add("search=" + Uri.EscapeDataString(search));
        q.Add("sort=" + SortField(sortId));
        q.Add("direction=-1");
        q.Add("limit=" + limit.ToString(CultureInfo.InvariantCulture));
        q.AddRange(Expand.Select(x => "expand[]=" + x));
        return new Uri("https://huggingface.co/api/models?" + string.Join("&", q));
    }

    public static Uri? NextPage(string? linkHeader) =>
        linkHeader is not null && Regex.Match(linkHeader, @"<([^>]+)>\s*;\s*rel=""?next""?", RegexOptions.IgnoreCase) is { Success: true } m
        && Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var u) ? u : null;

    // ---- /api/models listing ----

    public static List<HubModel> ParseModels(string json)
    {
        var list = new List<HubModel>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || Str(e, "id") is not { Length: > 0 } id)
                continue;
            var slash = id.IndexOf('/');
            var tags = e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            var hasGguf = e.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object;
            var gated = e.TryGetProperty("gated", out var g) && (g.ValueKind == JsonValueKind.True || g.ValueKind == JsonValueKind.String && g.GetString() != "false");
            var pipeline = Str(e, "pipeline_tag");
            list.Add(new HubModel(id, slash < 0 ? "" : id[..slash], id[(slash + 1)..],
                Long(e, "downloads") ?? 0, Long(e, "likes") ?? 0, Date(e, "lastModified"), Date(e, "createdAt"), Dbl(e, "trendingScore"),
                hasGguf ? Long(gguf, "total") : null, pipeline, gated,
                hasGguf || tags.Contains("gguf", StringComparer.OrdinalIgnoreCase) || id.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase),
                string.Equals(Str(e, "library_name"), "diffusers", StringComparison.OrdinalIgnoreCase) || pipeline is not null && DiffusionPipelines.Contains(pipeline)));
        }
        return list;
    }

    // ---- ranking ----

    // "latest": newest that are also trending first, (trendingScore+2)/(ageDays+2)^1.6; with a query, fuzzy match quality
    // comes before that. Other sorts keep HF's order. Diffusion repos stay: FilterKinds decides what is shown.
    public static List<HubModel> Rank(IEnumerable<HubModel> models, string query, string sortId, DateTimeOffset now)
    {
        var list = models.ToList();
        if (sortId != "latest")
            return list;
        double Latest(HubModel m)
        {
            var age = m.CreatedAt is { } c ? Math.Max(0, (now - c).TotalDays) : 3650;
            return ((m.TrendingScore ?? 0) + 2) / Math.Pow(age + 2, 1.6);
        }
        return string.IsNullOrWhiteSpace(query)
            ? list.OrderByDescending(Latest).ToList()
            : list.OrderByDescending(m => FuzzyScore(query, m.Id)).ThenByDescending(Latest).ToList();
    }

    // ---- model kinds ----

    private static readonly Regex VideoHint = new(@"(?<![a-z])(?:wan-?\d|ltx|mochi|cogvideo|hunyuan-?video|skyreels|step-?video|[ti]2v(?![a-z]))", RegexOptions.IgnoreCase);
    private static readonly Regex ImageHint = new(@"(?<![a-z])(?:flux|stable-?diffusion|sdxl|sd-?3(?!\d)|hidream|qwen-?image|z-image|chroma(?![a-z])|lumina|pix-?art|kolors|t2i(?![a-z]))", RegexOptions.IgnoreCase);
    private static readonly Regex AudioHint = new(@"tts(?![a-z])|(?<![a-z])(?:tts|asr(?![a-z])|whisper|kokoro|orpheus|parakeet|vibevoice|chatterbox|cosyvoice|bark(?![a-z]))", RegexOptions.IgnoreCase);

    // From pipeline_tag; without one, from the name (and diffusers -> Image); nothing known -> Text. "*-text-to-text"
    // (image/video/audio chat) is Text.
    public static HubKind KindOf(HubModel m)
    {
        var tag = (m.PipelineTag ?? "").Trim().ToLowerInvariant();
        if (tag.EndsWith("-text-to-text", StringComparison.Ordinal))
            return HubKind.Text;
        if (tag is "text-to-speech" or "automatic-speech-recognition" or "text-to-audio" or "voice-activity-detection" || tag.StartsWith("audio-", StringComparison.Ordinal))
            return HubKind.Audio;
        if (tag is "text-to-video" or "image-to-video" || tag.StartsWith("video-", StringComparison.Ordinal))
            return HubKind.Video;
        if (tag is "text-to-image" or "image-to-image" or "unconditional-image-generation" or "image-to-3d" or "text-to-3d")
            return HubKind.Image;
        if (tag.Length > 0)
            return HubKind.Text;
        if (VideoHint.IsMatch(m.Name))
            return HubKind.Video;
        if (ImageHint.IsMatch(m.Name) || m.IsDiffusion)
            return HubKind.Image;
        return AudioHint.IsMatch(m.Name) ? HubKind.Audio : HubKind.Text;
    }

    // The pipeline_tag values to ask HF for (one request each) to list these kinds.
    public static string[] PipelineTagsFor(HubKind kinds) =>
    [
        .. kinds.HasFlag(HubKind.Text) ? new[] { "text-generation", "image-text-to-text", "any-to-any" } : [],
        .. kinds.HasFlag(HubKind.Audio) ? new[] { "text-to-speech", "automatic-speech-recognition", "text-to-audio" } : [],
        .. kinds.HasFlag(HubKind.Image) ? new[] { "text-to-image", "image-to-image" } : [],
        .. kinds.HasFlag(HubKind.Video) ? new[] { "text-to-video", "image-to-video" } : [],
    ];

    // No kinds selected = no filter.
    public static List<HubModel> FilterKinds(IEnumerable<HubModel> models, HubKind kinds) =>
        kinds == 0 ? models.ToList() : models.Where(m => (KindOf(m) & kinds) != 0).ToList();

    // ---- brands ----

    private sealed record BrandDef(HubBrand Brand, Regex Name, string Query, string[] Owners);

    private const HubKind Tx = HubKind.Text, Au = HubKind.Audio, Im = HubKind.Image, Vi = HubKind.Video;

    // name: a regex matched in the repo name after a non-letter; owners: HF accounts that are this maker (the fallback).
    private static BrandDef Def(string key, string label, string? icon, HubKind kinds, string name, string query, params string[] owners) =>
        new(new HubBrand(key, label, icon, kinds), new Regex("(?<![a-z])(?:" + name + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), query, owners);

    private static readonly BrandDef[] BrandDefs =
    [
        // text, vision and multimodal chat
        Def("deepseek", "DeepSeek", "deepseek-color", Tx, "deepseek", "deepseek", "deepseek-ai"),
        Def("qwen", "Qwen", "qwen-color", Tx | Au | Im, "qwen|qwq|qvq", "qwen", "Qwen"),
        Def("llama", "Meta Llama", "meta-color", Tx, @"(?:code)?llama(?!file|[.-]?cpp)", "llama", "meta-llama", "facebook"),
        Def("mistral", "Mistral", "mistral-color", Tx | Au, "mistral|mixtral|ministral|codestral|devstral|magistral|pixtral|voxtral", "mistral", "mistralai"),
        Def("gemma", "Google Gemma", "google-color", Tx, "(?:code|pali|med|shield|t5|embedding)?gemma", "gemma", "google"),
        Def("phi", "Microsoft Phi", "microsoft-color", Tx, @"phi-?\d", "phi", "microsoft"),
        Def("nemotron", "NVIDIA Nemotron", "nvidia-color", Tx, "nemotron", "nemotron", "nvidia"),
        Def("minimax", "MiniMax", "minimax-color", Tx, "minimax", "minimax", "MiniMaxAI"),
        Def("glm", "Z.ai GLM", "zai", Tx, "(?:chat)?glm", "glm", "zai-org", "THUDM", "z-ai"),
        Def("kimi", "Moonshot Kimi", "moonshot", Tx, "kimi|moonlight", "kimi", "moonshotai"),
        Def("gpt-oss", "OpenAI gpt-oss", "openai", Tx, "gpt-oss", "gpt-oss", "openai"),
        Def("cohere", "Cohere Command", "cohere-color", Tx, "command-?[ra](?![a-z])|c4ai|aya(?![a-z])", "command", "CohereLabs", "CohereForAI", "Cohere"),
        Def("granite", "IBM Granite", "ibm", Tx | Au, "granite", "granite", "ibm-granite", "ibm"),
        Def("liquid", "Liquid LFM", "liquid", Tx | Au, "lfm", "lfm", "LiquidAI"),
        Def("stepfun", "StepFun Step", "stepfun-color", Tx | Au | Im | Vi, @"step-?\d|step-?(?:audio|video)|step1x", "step", "stepfun-ai"),
        Def("seed", "ByteDance Seed", "bytedance-color", Tx, "seed-?(?:oss|coder|x)|ui-tars", "seed", "ByteDance-Seed", "ByteDance"),
        Def("hunyuan", "Tencent Hunyuan", "tencent-color", Tx | Im | Vi, "hunyuan", "hunyuan", "tencent"),
        Def("ernie", "Baidu ERNIE", "baidu-color", Tx, "ernie(?![a-z])", "ernie", "baidu"),
        Def("mimo", "Xiaomi MiMo", "xiaomimimo", Tx | Au, "mimo(?![a-z])", "mimo", "XiaomiMiMo", "xiaomi"),
        Def("olmo", "AllenAI OLMo", "ai2-color", Tx, "olmo|molmo|tulu", "olmo", "allenai"),
        Def("smollm", "Hugging Face SmolLM", "huggingface-color", Tx, "smol(?:lm|vlm)", "smollm", "HuggingFaceTB", "HuggingFaceH4"),
        Def("grok", "xAI Grok", "xai", Tx, "grok", "grok", "xai-org"),
        Def("yi", "01.AI Yi", "zeroone", Tx, @"yi(?=[-\d])", "yi", "01-ai"),
        Def("hermes", "Nous Hermes", "nousresearch", Tx, "hermes", "hermes", "NousResearch"),
        Def("dolphin", "Dolphin", "dolphin", Tx, "dolphin", "dolphin", "cognitivecomputations", "dphn"),
        Def("arcee", "Arcee", "arcee-color", Tx, "arcee", "arcee", "arcee-ai"),
        Def("cogito", "Deep Cogito", "deepcogito-color", Tx, "cogito", "cogito", "deepcogito"),
        Def("longcat", "Meituan LongCat", "longcat-color", Tx, "longcat", "longcat", "meituan-longcat", "meituan"),
        Def("ling", "inclusionAI Ling", "ling-color", Tx, @"(?:ling|ring|ming)-(?:\d|flash|mini|lite|plus|omni)", "ling", "inclusionAI"),
        Def("kat", "Kwaipilot KAT", "kwaipilot", Tx, @"kat-(?:dev|coder|v\d)", "kat", "Kwaipilot"),
        Def("rnj", "Essential AI Rnj", "essentialai-color", Tx, @"rnj-?\d", "rnj", "EssentialAI"),
        Def("solar", "Upstage Solar", "upstage-color", Tx, "solar(?![a-z])", "solar", "upstage"),
        Def("jamba", "AI21 Jamba", "ai21", Tx, "jamba", "jamba", "ai21labs"),
        Def("openchat", "OpenChat", "openchat-color", Tx, "openchat", "openchat", "openchat"),
        Def("internlm", "InternLM", null, Tx, "intern(?:lm|vl|-?s1)", "internlm", "internlm", "OpenGVLab"),
        Def("falcon", "TII Falcon", null, Tx, "falcon", "falcon", "tiiuae"),
        Def("exaone", "LG EXAONE", null, Tx, "exaone", "exaone", "LGAI-EXAONE"),
        Def("minicpm", "OpenBMB MiniCPM", null, Tx, "minicpm", "minicpm", "openbmb"),
        Def("baichuan", "Baichuan", null, Tx, "baichuan", "baichuan", "baichuan-inc"),
        Def("llava", "LLaVA", null, Tx, "llava", "llava", "llava-hf", "liuhaotian"),
        Def("apriel", "ServiceNow Apriel", null, Tx, "apriel", "apriel", "ServiceNow-AI"),
        Def("skywork", "Skywork", null, Tx, "skywork", "skywork", "Skywork"),
        Def("moondream", "Moondream", null, Tx, "moondream", "moondream", "vikhyatk", "moondream"),
        // speech
        Def("whisper", "OpenAI Whisper", "openai", Au, "(?:distil-?|faster-)?whisper", "whisper"),
        Def("parakeet", "NVIDIA Parakeet", "nvidia-color", Au, "parakeet|canary-", "parakeet"),
        Def("vibevoice", "Microsoft VibeVoice", "microsoft-color", Au, "vibevoice", "vibevoice"),
        Def("kokoro", "Kokoro", null, Au, "kokoro", "kokoro", "hexgrad"),
        Def("orpheus", "Orpheus", null, Au, "orpheus", "orpheus", "canopylabs"),
        Def("outetts", "OuteTTS", null, Au, "outetts", "outetts", "OuteAI"),
        Def("dia", "Nari Dia", null, Au, @"dia-\d", "dia", "nari-labs"),
        Def("csm", "Sesame CSM", null, Au, @"csm-\d", "csm", "sesame"),
        Def("chatterbox", "Resemble Chatterbox", null, Au, "chatterbox", "chatterbox", "ResembleAI"),
        Def("f5-tts", "F5-TTS", null, Au, "f5-?tts", "f5-tts", "SWivid"),
        Def("spark-tts", "Spark-TTS", null, Au, "spark-?tts", "spark-tts", "SparkAudio"),
        Def("fish-speech", "Fish Audio", null, Au, "fish-?speech|openaudio", "fish-speech", "fishaudio"),
        Def("cosyvoice", "FunAudioLLM CosyVoice", null, Au, "cosyvoice|sensevoice", "cosyvoice", "FunAudioLLM"),
        Def("index-tts", "IndexTTS", null, Au, "index-?tts", "indextts", "IndexTeam"),
        Def("moshi", "Kyutai Moshi", null, Au, "moshi", "moshi", "kyutai"),
        Def("bark", "Suno Bark", null, Au, "bark(?![a-z])", "bark", "suno"),
        Def("higgs", "Boson Higgs Audio", null, Au, "higgs-?audio", "higgs-audio", "bosonai"),
        Def("piper", "Piper", null, Au, "piper(?![a-z])", "piper", "rhasspy"),
        // image
        Def("flux", "Black Forest Labs FLUX", null, Im, "flux", "flux", "black-forest-labs"),
        Def("stability", "Stability AI", null, Im | Au | Vi, @"stable-?(?:diffusion|audio|video|cascade)|sdxl|sd-?3(?!\d)", "stable-diffusion", "stabilityai"),
        Def("hidream", "HiDream", null, Im, "hidream", "hidream", "HiDream-ai"),
        Def("chroma", "Chroma", null, Im, "chroma(?![a-z])", "chroma", "lodestones"),
        Def("sana", "NVIDIA Sana", "nvidia-color", Im, "sana(?![a-z])", "sana", "Efficient-Large-Model"),
        Def("z-image", "Alibaba Z-Image", "alibaba-color", Im, "z-image", "z-image", "Tongyi-MAI"),
        Def("omnigen", "OmniGen", null, Im, "omnigen", "omnigen", "Shitao", "OmniGen2"),
        Def("lumina", "Lumina", null, Im, "lumina(?![a-z])", "lumina", "Alpha-VLLM"),
        Def("pixart", "PixArt", null, Im, "pix-?art", "pixart", "PixArt-alpha"),
        Def("kolors", "Kwai Kolors", null, Im, "kolors", "kolors", "Kwai-Kolors"),
        Def("kandinsky", "Kandinsky", null, Im | Vi, "kandinsky", "kandinsky", "kandinskylab", "ai-forever"),
        // video
        Def("wan", "Wan", "alibaba-color", Vi | Im, @"wan-?\d", "wan", "Wan-AI"),
        Def("ltx", "Lightricks LTX", null, Vi, "ltx", "ltx", "Lightricks"),
        Def("mochi", "Genmo Mochi", null, Vi, "mochi", "mochi", "genmo"),
        Def("cogvideo", "Zhipu CogVideoX", "zhipu-color", Vi | Im, "cog(?:videox?|view)", "cogvideox"),
        Def("skyreels", "SkyReels", null, Vi, "skyreels", "skyreels"),
    ];

    public static readonly IReadOnlyList<HubBrand> Brands = BrandDefs.Select(d => d.Brand).ToArray();

    // Add, not TryAdd: an owner listed twice fails the type initializer, so the self-test catches it.
    private static readonly Dictionary<string, HubBrand> OwnerBrands = BrandDefs
        .SelectMany(d => d.Owners.Select(o => (o, d.Brand))).ToDictionary(x => x.o, x => x.Brand, StringComparer.OrdinalIgnoreCase);

    // The maker named in the repo name (the earliest match, the longer one on a tie: "Huihui-Qwen3.8" is Qwen,
    // "DeepSeek-R1-Distill-Qwen" DeepSeek), else the owner's; null = Other. ponytail: one regex per brand per id.
    public static HubBrand? BrandOf(string modelId)
    {
        var slash = modelId.IndexOf('/');
        var name = modelId[(slash + 1)..];
        HubBrand? best = null;
        int at = int.MaxValue, length = 0;
        foreach (var d in BrandDefs)
            if (d.Name.Match(name) is { Success: true } m && (m.Index < at || m.Index == at && m.Length > length))
                (best, at, length) = (d.Brand, m.Index, m.Length);
        return best ?? (slash > 0 && OwnerBrands.TryGetValue(modelId[..slash], out var owner) ? owner : null);
    }

    public static List<HubBrand> BrandsFor(HubKind kinds) =>
        Brands.Where(b => (b.Kinds & kinds) != 0).OrderBy(b => b.Label, StringComparer.OrdinalIgnoreCase).ToList();

    // Empty set = no filter; "other" keeps the models with no known brand.
    public static List<HubModel> FilterBrands(IEnumerable<HubModel> models, ISet<string> brandKeys) =>
        brandKeys.Count == 0 ? models.ToList()
        : models.Where(m => BrandOf(m.Id) is { } b ? brandKeys.Contains(b.Key) : brandKeys.Contains("other")).ToList();

    // The search word per brand to send HF when the brand filter is on and the query is empty.
    public static IEnumerable<string> BrandQueries(IEnumerable<HubBrand> selected) =>
        selected.Select(b => BrandDefs.First(d => d.Brand.Key == b.Key).Query);

    // ---- fuzzy search ----

    public static string Normalize(string s) =>
        new(s.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '.' or '-' or '/')).ToArray());

    // The trimmed query, then letters and digits joined across a separator ("qwen 3.8" -> "qwen3.8"), then split apart
    // ("qwen3.8" -> "qwen 3.8"). Distinct, at most 3.
    public static IEnumerable<string> QueryVariants(string query)
    {
        var q = query.Trim();
        return new[] { q, Regex.Replace(q, @"(\p{L})[\s_\-]+(\d)", "$1$2"), Regex.Replace(q, @"(\p{L})(\d)", "$1 $2") }.Distinct().Take(3);
    }

    // 1 when every query token is a substring of the normalized id; a token within a small edit distance of some window of
    // the id scores less (0.7 at one edit, 0.5 at two); any token that misses gives 0. The best of QueryVariants counts,
    // so "qwn 3.8" matches as "qwn38".
    public static double FuzzyScore(string query, string modelId)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 1;
        var id = Normalize(modelId);
        return QueryVariants(query).Max(v => TokensScore(v, id));
    }

    private static double TokensScore(string query, string id)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0)
            return 1;
        double sum = 0;
        foreach (var t in tokens)
        {
            if (id.Contains(t, StringComparison.Ordinal))
            {
                sum += 1;
                continue;
            }
            var tolerance = Tolerance(t.Length);
            var d = tolerance == 0 ? int.MaxValue : WindowDistance(t, id, tolerance);
            if (d > tolerance)
                return 0;
            sum += 0.7 - 0.2 * (d - 1);
        }
        return sum / tokens.Count;
    }

    private static int Tolerance(int length) => length >= 7 ? 2 : length >= 4 ? 1 : 0;

    // ponytail: every window of length len±tolerance through a full DP, O(id * tolerance * len^2); fine for a few hundred rows.
    private static int WindowDistance(string token, string id, int tolerance)
    {
        var best = int.MaxValue;
        for (var len = Math.Max(1, token.Length - tolerance); len <= Math.Min(id.Length, token.Length + tolerance); len++)
            for (var start = 0; start + len <= id.Length; start++)
                best = Math.Min(best, Osa(token, id.Substring(start, len)));
        return best;
    }

    // Damerau-Levenshtein, optimal string alignment form (adjacent swaps count as one edit).
    private static int Osa(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    public static List<HubModel> FuzzyFilter(IEnumerable<HubModel> models, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return models.ToList();
        return models.Select(m => (m, s: FuzzyScore(query, m.Id))).Where(x => x.s > 0).OrderByDescending(x => x.s).Select(x => x.m).ToList();
    }

    // ---- GGUF variants of one repo (/api/models/{repo}?blobs=true) ----

    private static readonly Regex QuantRx = new(
        @"(?<![a-z0-9])(?:UD-)?(?:IQ\d\w*|Q\d(?:_K)?(?:_[A-Z0-9]+)*|MXFP\d\w*|NVFP\d\w*|BF16|F16|F32|TQ\d\w*)(?![a-z0-9])", RegexOptions.IgnoreCase);
    private static readonly Regex ShardRx = new(@"-\d{5}-of-\d{5}$", RegexOptions.IgnoreCase);
    private static readonly Regex F16Rx = new(@"(?<!B)F16", RegexOptions.IgnoreCase);

    private static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);

    public static HubVariants ParseVariants(string modelInfoJson)
    {
        using var doc = JsonDocument.Parse(modelInfoJson);
        var root = doc.RootElement;
        var files = new List<HubFile>();
        if (root.TryGetProperty("siblings", out var sibs) && sibs.ValueKind == JsonValueKind.Array)
            foreach (var s in sibs.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Object && Str(s, "rfilename") is { } n && n.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                    files.Add(new HubFile(n, Long(s, "size") ?? 0));

        var mmproj = files.Where(f => Has(f.Name, "mmproj")).ToList();
        var chosen = mmproj.FirstOrDefault(f => F16Rx.IsMatch(Path.GetFileName(f.Name)))
            ?? mmproj.FirstOrDefault(f => Has(Path.GetFileName(f.Name), "BF16")) ?? mmproj.FirstOrDefault();
        IReadOnlyList<HubFile> companions = chosen is null ? [] : [chosen];
        var companionBytes = companions.Sum(f => f.Size);

        var variants = files
            .Where(f => !Has(f.Name, "mmproj") && !Has(f.Name, "imatrix") && !Has(f.Name, "mtp") && !Has(f.Name, "draft"))
            .GroupBy(f => (Dir: f.Name[..Math.Max(0, f.Name.LastIndexOf('/'))], Stem: ShardRx.Replace(Path.GetFileNameWithoutExtension(f.Name), "")))
            .Select(g =>
            {
                var shards = g.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
                var size = shards.Sum(f => f.Size);
                var quant = QuantRx.Matches(g.Key.Stem) is { Count: > 0 } ms ? ms[^1].Value : g.Key.Stem;
                return (g.Key, Variant: new HubVariant(quant, quant, size, size + companionBytes, shards, companions));
            })
            .ToList();
        // Two groups with one quant (e.g. "x-Q4_K_M" and "x-128k-Q4_K_M") are labelled by their path instead.
        var dupes = variants.GroupBy(v => v.Variant.Quant, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = variants
            .Select(v => dupes.Contains(v.Variant.Quant) ? v.Variant with { Label = (v.Key.Dir.Length > 0 ? v.Key.Dir + "/" : "") + v.Key.Stem } : v.Variant)
            .OrderByDescending(v => v.SizeBytes)
            .ToList();
        return new HubVariants(Str(root, "sha") ?? "", mmproj.Count > 0, result);
    }

    // ---- fit to this machine ----

    private const double GiB = 1024.0 * 1024 * 1024;

    // required = GiB*1.15 + 1 GiB. GPU budget: the card less 3% or 0.5 GiB, whichever is more. Marginal: fits only the
    // whole card. Partial: spills into half the RAM. No GPU: compared with half the RAM.
    public static Fit FitOf(long downloadBytes, double? vramGiB, double? ramGiB)
    {
        var required = downloadBytes / GiB * 1.15 + 1;
        if (vramGiB is { } vram && vram > 0)
        {
            var budget = vram - Math.Max(0.03 * vram, 0.5);
            if (required <= budget)
                return Fit.Fits;
            if (required <= vram)
                return Fit.Marginal;
            return ramGiB is { } r && required <= budget + 0.5 * r ? Fit.Partial : Fit.Oom;
        }
        if (ramGiB is { } ram)
            return required <= 0.5 * ram ? Fit.Partial : Fit.Oom;
        return Fit.Unknown;
    }

    public static string FitTip(Fit f) => f switch
    {
        Fit.Fits => "Fits in GPU memory",
        Fit.Marginal => "Fits only by using the whole GPU; may be tight",
        Fit.Partial => "Spills into system RAM; runs slower",
        Fit.Oom => "Too large for this machine",
        _ => "Hardware unknown",
    };

    // Downloaded first, then Fits, Marginal, Partial (largest first in each), Unknown (largest first), then Oom smallest first.
    public static List<HubVariant> OrderByFit(IEnumerable<HubVariant> v, double? vramGiB, double? ramGiB, Func<HubVariant, bool>? isDownloaded = null)
    {
        int Class(HubVariant x) => isDownloaded?.Invoke(x) == true ? 0 : FitOf(x.DownloadBytes, vramGiB, ramGiB) switch
        {
            Fit.Fits => 1, Fit.Marginal => 2, Fit.Partial => 3, Fit.Unknown => 4, _ => 5,
        };
        return v.Select(x => (x, r: Class(x))).OrderBy(p => p.r).ThenBy(p => p.r == 5 ? p.x.DownloadBytes : -p.x.DownloadBytes).Select(p => p.x).ToList();
    }

    // "Only show models that fit": the smallest quant guessed as ParamCount * 0.4 bytes.
    public static bool ModelFits(HubModel m, double? vramGiB, double? ramGiB) =>
        m.ParamCount is { } p && FitOf((long)(p * 0.4), vramGiB, ramGiB) is Fit.Fits or Fit.Marginal or Fit.Partial;

    // The smallest weights' bytes (split parts summed; mmproj, imatrix, mtp and draft files are not weights), null: none.
    public static long? SmallestWeights(HubVariants v) => v.Variants.Count == 0 ? null : v.Variants.Min(x => x.SizeBytes);

    // Drops repos known to have no GGUF weights (smallest: id -> SmallestWeights; a repo not in it is unknown and stays) and,
    // with fitsGpu on and the VRAM known, those whose smallest weights are larger than the whole GPU.
    public static List<HubModel> FilterFetched(IEnumerable<HubModel> rows, IReadOnlyDictionary<string, long?> smallest, bool fitsGpu, double? vramGiB) =>
        rows.Where(m => !smallest.TryGetValue(m.Id, out var bytes) || bytes is { } b && !(fitsGpu && vramGiB is > 0 && b > vramGiB * GiB)).ToList();

    // ---- display ----

    private static string Scaled(double value, string[] units, double step)
    {
        var i = 0;
        while (Math.Round(value, 1) >= step && i < units.Length - 1)
        {
            value /= step;
            i++;
        }
        value = Math.Round(value, 1);
        return (value < 10 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture)) + units[i];
    }

    // Decimal units like Hugging Face: "7.5 GB", "71 GB".
    public static string FormatSize(long bytes) => bytes < 1000 ? bytes + " B" : Scaled(bytes, [" B", " KB", " MB", " GB", " TB"], 1000);

    public static string FormatCount(long n) => n < 1000 ? n.ToString(CultureInfo.InvariantCulture) : Scaled(n, ["", "K", "M", "B"], 1000);

    // A download's speed: "12.3 MB/s" (decimal, like FormatSize); "" before the first measure.
    public static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond > 0 ? (bytesPerSecond / 1e6).ToString(bytesPerSecond < 1e7 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB/s" : "";

    // The time left at this speed: "45s", "12m", "1h 05m"; "" when unknown (no speed yet).
    public static string FormatEta(long remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || remainingBytes < 0)
            return "";
        var s = (long)Math.Ceiling(remainingBytes / bytesPerSecond);
        return s < 60 ? s + "s" : s < 3600 ? (s + 59) / 60 + "m" : $"{s / 3600}h {s % 3600 / 60:00}m";
    }

    public static string FormatAge(DateTimeOffset? t, DateTimeOffset now)
    {
        if (t is not { } time)
            return "";
        var age = now - time;
        return age.TotalHours < 1 ? "just now"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
            : age.TotalDays < 30 ? $"{(int)age.TotalDays}d ago"
            : age.TotalDays < 365 ? $"{(int)(age.TotalDays / 30)}mo ago"
            : $"{(int)(age.TotalDays / 365)}y ago";
    }

    // ---- README ----

    // HTML elements whose tags are stripped (text kept). Only known names, so placeholders like <model> or <s> stay;
    // source only with attributes (a "<source>" placeholder stays).
    private const string HtmlTags = "a|abbr|article|aside|audio|b|big|blockquote|br|caption|center|cite|code|colgroup|col|dd|del|details|div|dl|dt|em|" +
        "figcaption|figure|font|footer|h[1-6]|header|hr|iframe|img|ins|i|kbd|li|mark|nav|ol|picture|pre|p|section|small|source(?=\\s)|span|strike|" +
        "strong|sub|summary|sup|table|tbody|td|tfoot|thead|th|tr|tt|ul|u|video";

    // Elements that end a line where they open or close.
    private static readonly HashSet<string> HtmlBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "article", "aside", "blockquote", "caption", "center", "dd", "details", "div", "dl", "dt", "figcaption", "figure", "footer",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "nav", "ol", "p", "pre", "section", "summary", "table", "tbody", "td",
        "tfoot", "th", "thead", "tr", "ul",
    };

    // A code span (kept as is), a comment, a script/style element, a whole <a> element, or one known tag. Leftmost wins, so a
    // tag inside a code span is never touched. An autolink (<https://x>) or a comparison (a < b) matches nothing.
    private static readonly Regex HtmlInlineRx = new(
        @"(?<code>(?<bt>`+)(?:(?!\n[ \t]*\n)[\s\S])*?(?<!`)\k<bt>(?!`))|<!--[\s\S]*?(?:-->|\z)|<(?<raw>script|style)\b[\s\S]*?(?:</\k<raw>\s*>|\z)" +
        @"|<a\b(?<attrs>[^<>]*)>(?<inner>[\s\S]*?)</a\s*>|<(?<close>/)?(?<tag>" + HtmlTags + @")(?![a-z0-9-])(?<attrs>[^<>]*)>",
        RegexOptions.IgnoreCase);

    // Marks a line an HTML tag was taken from: its indentation goes (so it does not turn into a code block) and, left blank, it
    // merges with the blank lines around it.
    private const char Stripped = '\u0001';

    private static string? Attr(string attrs, string name) =>
        Regex.Match(attrs, @"(?<![\w-])" + name + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+))", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Groups["v"].Value.Trim() : null;

    // Inline HTML as Markdown: <a href> -> [text](href), <img alt src> -> ![alt](src) (no alt or src: dropped), <br> and block
    // tags -> a line break, any other known tag -> nothing; comments, script and style dropped.
    private static string StripHtml(string text) => HtmlInlineRx.Replace(text, m =>
    {
        if (m.Groups["code"].Success)
            return m.Value;
        if (m.Groups["inner"].Success)
        {
            var inner = StripHtml(m.Groups["inner"].Value).Replace(Stripped.ToString(), "").Trim();
            return Stripped + (Attr(m.Groups["attrs"].Value, "href") is { Length: > 0 } href && inner.Length > 0 ? $"[{inner}]({href})" : inner) + Stripped;
        }
        if (!m.Groups["tag"].Success)
            return Stripped.ToString();
        var tag = m.Groups["tag"].Value;
        var attrs = m.Groups["attrs"].Value;
        if (tag.Equals("img", StringComparison.OrdinalIgnoreCase))
            return Attr(attrs, "src") is { Length: > 0 } src && Attr(attrs, "alt") is { Length: > 0 } alt ? $"{Stripped}![{alt}]({src}){Stripped}" : Stripped.ToString();
        return tag.Equals("br", StringComparison.OrdinalIgnoreCase) || HtmlBlocks.Contains(tag) ? $"{Stripped}\n{Stripped}" : Stripped.ToString();
    });

    // Text outside code fences: HTML stripped, touched lines unindented, runs of blank lines merged into one.
    private static IEnumerable<string> Prose(IEnumerable<string> lines)
    {
        var blank = false;
        foreach (var raw in StripHtml(string.Join("\n", lines)).Split('\n'))
        {
            var line = raw.TrimStart().StartsWith(Stripped) ? raw.Replace(Stripped.ToString(), "").TrimStart() : raw.Replace(Stripped.ToString(), "");
            if (string.IsNullOrWhiteSpace(line))
            {
                if (blank)
                    continue;
                line = "";
            }
            blank = line.Length == 0;
            yield return line;
        }
    }

    // The README as our Markdown renderer can show it: no YAML front matter, inline HTML turned into Markdown or plain text
    // (StripHtml), code fences untouched. ponytail: regexes, not an HTML parser; nested markup in odd shapes may leave tags.
    public static string CleanReadme(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var at = 0;
        if (lines[0].TrimEnd() == "---" && Array.FindIndex(lines, 1, l => l.TrimEnd() is "---" or "...") is var end and > 0)
            at = end + 1;
        var kept = new List<string>();
        var prose = new List<string>();
        void Flush()
        {
            if (prose.Count > 0)
                kept.AddRange(Prose(prose));
            prose.Clear();
        }
        var fence = false;
        for (; at < lines.Length; at++)
        {
            var line = lines[at];
            var t = line.Trim();
            var marker = t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal);
            if (!fence && !marker)
            {
                prose.Add(line);
                continue;
            }
            Flush();
            kept.Add(line);
            if (marker)
                fence = !fence;
        }
        Flush();
        return string.Join("\n", kept).Trim('\n');
    }

    // ---- self-test (no network) ----

    internal static void HubLogicSelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Hub self-test failed: " + what);
        }

        // 1. sorts
        Check(string.Join(",", Sorts.Select(s => s.Id)) == "latest,newest,trending,downloads,updated,likes", "sort ids and order");
        Check(Sorts[3].Label == "Most downloads" && Sorts[4].Label == "Recently updated", "sort labels");

        // 2. listing
        var models = ParseModels("""
            [
              {"id":"unsloth/Qwen3.8-27B-GGUF","downloads":61000,"likes":1400,"lastModified":"2026-11-01T00:00:00.000Z",
               "createdAt":"2026-10-20T00:00:00.000Z","trendingScore":120,"gguf":{"total":27000000000,"architecture":"qwen3"},
               "pipeline_tag":"text-generation","gated":false,"tags":["gguf"],"library_name":"transformers"},
              {"id":"someone/model-GGUF","gated":"manual","downloads":null},
              {"id":"art/flux-gguf","library_name":"diffusers","tags":["gguf"]},
              {"id":"art/pics","pipeline_tag":"text-to-image","tags":["gguf"]},
              {"id":"plain","tags":["text-generation"]},
              {"downloads":5}
            ]
            """);
        Check(models.Count == 5, "listing skips rows without an id");
        var q = models[0];
        Check(q.Owner == "unsloth" && q.Name == "Qwen3.8-27B-GGUF" && q.Downloads == 61000 && q.Likes == 1400, "listing basics");
        Check(q.ParamCount == 27000000000 && q.TrendingScore == 120 && q.PipelineTag == "text-generation" && !q.Gated && q.IsGguf && !q.IsDiffusion, "listing fields");
        Check(q.CreatedAt == new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero) && q.LastModified!.Value.Day == 1, "listing dates");
        Check(models[1].Gated && models[1].IsGguf && models[1].Downloads == 0 && models[1].ParamCount is null && models[1].CreatedAt is null, "missing fields, -GGUF name, gated string");
        Check(models[2].IsDiffusion && models[3].IsDiffusion && !models[4].IsGguf && models[4].Owner == "", "diffusion and non-gguf rows");
        Check(ParseModels("{}").Count == 0, "non-array listing");

        // 3. search URL and paging
        var url = SearchUrl("  qwen 3.8 ", "trending").AbsoluteUri;
        Check(url.StartsWith("https://huggingface.co/api/models?", StringComparison.Ordinal) && url.Contains("filter=gguf") && url.Contains("search=qwen%203.8")
              && url.Contains("sort=trendingScore") && url.Contains("direction=-1") && url.Contains("limit=100"), "search URL: " + url);
        Check(Regex.Matches(url, "expand").Count == 10 && url.Contains("library_name") && url.Contains("trendingScore&"), "search URL expands: " + url);
        Check(!SearchUrl(" ", "likes", 50).AbsoluteUri.Contains("search=") && SearchUrl("", "likes", 50).AbsoluteUri.Contains("limit=50"), "blank query omitted");
        foreach (var (id, field) in new[] { ("latest", "createdAt"), ("newest", "createdAt"), ("trending", "trendingScore"), ("downloads", "downloads"), ("updated", "lastModified"), ("likes", "likes") })
            Check(SearchUrl("x", id).AbsoluteUri.Contains("sort=" + field + "&"), "sort mapping " + id);
        Check(NextPage("<https://huggingface.co/api/models?cursor=abc&limit=100>; rel=\"next\"")?.AbsoluteUri == "https://huggingface.co/api/models?cursor=abc&limit=100", "Link next");
        Check(NextPage("<https://h/a?p=1>; rel=\"prev\", <https://h/a?p=3>; rel=\"next\"")?.AbsoluteUri == "https://h/a?p=3", "Link next among several");
        Check(NextPage(null) is null && NextPage("") is null && NextPage("<https://h/a>; rel=\"last\"") is null, "no next link");

        // 4. ranking
        var now = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        HubModel M(string id, double? trend, int? daysOld, bool diffusion = false) =>
            new(id, "", id, 0, 0, null, daysOld is { } d ? now.AddDays(-d) : null, trend, null, null, false, true, diffusion);
        var pool = new[] { M("old", 50, null), M("fresh", 0, 0), M("hot", 100, 10), M("pic", 1000, 0, true) };
        Check(string.Join(",", Rank(pool, "", "latest", now).Select(m => m.Id)) == "pic,hot,fresh,old", "latest ranking keeps diffusion");
        Check(string.Join(",", Rank(pool, "", "likes", now).Select(m => m.Id)) == "old,fresh,hot,pic", "other sorts keep HF order");
        Check(string.Join(",", Rank(pool, "fresh", "latest", now).Select(m => m.Id)) == "fresh,pic,hot,old", "latest with a query puts matches first");

        // 4b. kinds
        HubModel K(string id, string? tag, bool diffusion = false) =>
            new(id, id.Contains('/') ? id[..id.IndexOf('/')] : "", id[(id.IndexOf('/') + 1)..], 0, 0, null, null, null, null, tag, false, true, diffusion);
        foreach (var (id, tag, diffusion, want) in new (string, string?, bool, HubKind)[]
        {
            ("unsloth/Qwen3.8-27B-GGUF", "text-generation", false, HubKind.Text), ("x/VL-GGUF", "image-text-to-text", false, HubKind.Text),
            ("x/Omni-GGUF", "any-to-any", false, HubKind.Text), ("x/Vid-chat", "video-text-to-text", false, HubKind.Text), ("x/emb", "feature-extraction", false, HubKind.Text),
            ("x/plain-GGUF", null, false, HubKind.Text), ("x/flux-named-but-tagged", "text-generation", false, HubKind.Text),
            ("black-forest-labs/FLUX.1-dev-gguf", "text-to-image", true, HubKind.Image), ("city96/FLUX.1-dev-gguf", null, false, HubKind.Image),
            ("x/sdxl-turbo-gguf", null, false, HubKind.Image), ("x/edit", "image-to-image", false, HubKind.Image), ("art/pics", null, true, HubKind.Image),
            ("ggml-org/whisper-large-v3-GGUF", null, false, HubKind.Audio), ("x/asr", "automatic-speech-recognition", false, HubKind.Audio),
            ("hexgrad/Kokoro-82M-gguf", null, false, HubKind.Audio), ("x/OuteTTS-1.0-GGUF", null, false, HubKind.Audio), ("x/say", "text-to-speech", false, HubKind.Audio),
            ("x/cls", "audio-classification", false, HubKind.Audio),
            ("city96/Wan2.1-T2V-14B-gguf", null, true, HubKind.Video), ("x/LTX-Video-gguf", null, false, HubKind.Video), ("x/t2v", "text-to-video", false, HubKind.Video),
            ("x/i2v", "image-to-video", false, HubKind.Video), ("x/v2v", "video-to-video", false, HubKind.Video),
        })
            Check(KindOf(K(id, tag, diffusion)) == want, $"KindOf {id} [{tag}] = {KindOf(K(id, tag, diffusion))}");
        var kindPool = new[] { K("a/Qwen3-GGUF", "text-generation"), K("b/FLUX.1-gguf", null, true), K("c/whisper-gguf", null), K("d/Wan2.2-gguf", null, true) };
        Check(string.Join(",", FilterKinds(kindPool, HubKind.Text).Select(m => m.Owner)) == "a", "FilterKinds Text hides diffusion");
        Check(string.Join(",", FilterKinds(kindPool, HubKind.Audio | HubKind.Video).Select(m => m.Owner)) == "c,d", "FilterKinds Audio|Video");
        Check(FilterKinds(kindPool, 0).Count == 4, "FilterKinds none selected = no filter");
        Check(PipelineTagsFor(HubKind.Text).Contains("text-generation") && PipelineTagsFor(HubKind.Text).Contains("image-text-to-text") && !PipelineTagsFor(HubKind.Text).Contains("text-to-image"), "tags for Text");
        var avTags = PipelineTagsFor(HubKind.Audio | HubKind.Video);
        Check(avTags.Contains("text-to-speech") && avTags.Contains("automatic-speech-recognition") && avTags.Contains("text-to-video") && !avTags.Contains("text-generation") && !avTags.Contains("text-to-image"), "tags for Audio|Video");
        Check(PipelineTagsFor(0).Length == 0 && PipelineTagsFor(HubKind.Image).SequenceEqual(["text-to-image", "image-to-image"]), "tags for none and Image");
        var tagUrl = SearchUrl("", "trending", 100, "text-to-image").AbsoluteUri;
        Check(tagUrl.Contains("filter=gguf&pipeline_tag=text-to-image&sort=trendingScore"), "search URL pipeline_tag: " + tagUrl);
        Check(!SearchUrl("x", "likes").AbsoluteUri.Contains("pipeline_tag=") && !SearchUrl("x", "likes", 100, " ").AbsoluteUri.Contains("pipeline_tag="), "no pipeline_tag by default");

        // 4c. brands
        string? BrandKey(string id) => BrandOf(id)?.Key;
        foreach (var (id, want) in new (string, string?)[]
        {
            ("huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF", "qwen"), ("ISTA-DASLab/DeepSeek-V4.1-Flash-1017-GSQ-RCO-GGUF", "deepseek"),
            ("SC117/Qwen3.8-Flash-Next-80B-A3B-GGUF", "qwen"), ("LiquidAI/LFM3-1.7B-GGUF", "liquid"), ("unsloth/Qwen3.8-27B-GGUF", "qwen"),
            ("bartowski/DeepSeek-R1-Distill-Qwen-7B-GGUF", "deepseek"), ("bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", "llama"),
            ("TheBloke/CodeLlama-7B-GGUF", "llama"), ("unsloth/gemma-3-27b-it-GGUF", "gemma"), ("x/medgemma-4b-GGUF", "gemma"),
            ("microsoft/Phi-4-mini-instruct-gguf", "phi"), ("bartowski/phi3-medium", "phi"), ("unsloth/Mistral-Small-3.2-24B-GGUF", "mistral"),
            ("x/Devstral-Small-GGUF", "mistral"), ("nvidia/Llama-3_3-Nemotron-Super-49B-GGUF", "llama"), ("x/Nemotron-H-8B-GGUF", "nemotron"),
            ("unsloth/MiniMax-M2-GGUF", "minimax"), ("unsloth/GLM-4.6-GGUF", "glm"), ("unsloth/Kimi-K2-Instruct-GGUF", "kimi"),
            ("ggml-org/gpt-oss-20b-GGUF", "gpt-oss"), ("ggml-org/whisper-large-v3-GGUF", "whisper"), ("x/c4ai-command-r-plus-GGUF", "cohere"),
            ("ibm-granite/granite-4.0-h-small-GGUF", "granite"), ("x/Step-3.5-Flash-GGUF", "stepfun"), ("x/Seed-OSS-36B-GGUF", "seed"),
            ("x/Hunyuan-A13B-GGUF", "hunyuan"), ("x/ERNIE-4.5-21B-GGUF", "ernie"), ("x/MiMo-7B-RL-GGUF", "mimo"), ("x/OLMo-2-13B-GGUF", "olmo"),
            ("x/SmolLM3-3B-GGUF", "smollm"), ("black-forest-labs/FLUX.1-dev-gguf", "flux"), ("x/stable-diffusion-3.5-large-gguf", "stability"),
            ("Lightricks/LTX-Video-gguf", "ltx"), ("city96/Wan2.1-T2V-14B-gguf", "wan"), ("x/Kokoro-82M-gguf", "kokoro"), ("x/orpheus-3b-0.1-ft-GGUF", "orpheus"),
            ("x/parakeet-tdt-0.6b-v3-GGUF", "parakeet"),
            // owner fallback
            ("deepseek-ai/Janus-Pro-7B", "deepseek"), ("meta-llama/xyz", "llama"), ("LiquidAI/foo-GGUF", "liquid"), ("QWEN/thing", "qwen"),
            // unknown, not a brand, false-positive guards
            ("acme/foo-bar-GGUF", null), ("unsloth/foo-GGUF", null), ("bartowski/llamafile-x", null), ("x/Swan-7B", null), ("x/stepwise-7b", null),
            ("x/Bernie-7B", null), ("x/sapphire-3", null), ("nofolder", null), ("", null), ("/x", null),
        })
            Check(BrandKey(id) == want, $"BrandOf {id} = {BrandKey(id) ?? "null"}");
        Check(Brands.Count >= 60 && Brands.Select(b => b.Key).Distinct().Count() == Brands.Count && Brands.All(b => b.Key != "other" && b.Kinds != 0 && b.Label.Length > 0), "brand catalog keys and kinds");
        foreach (var kind in new[] { HubKind.Text, HubKind.Audio, HubKind.Image, HubKind.Video })
            Check(BrandsFor(kind).Count >= 5, "brands for " + kind);
        // Stems listed from Assets\Vendors; RemoteApiSelfTest also loads every IconKey from the app's resources.
        string[] vendorStems = ["ai2-color", "ai21", "aionlabs-color", "alibaba-color", "anthropic", "arcee-color", "aws", "baidu-color", "bytedance-color",
            "cerebras-color", "cohere-color", "deepcogito-color", "deepinfra-color", "deepseek-color", "dolphin", "essentialai-color", "fireworks-color",
            "google-color", "groq", "huggingface-color", "ibm", "inception", "inflection", "kwaipilot", "ling-color", "liquid", "longcat-color", "meta-color",
            "microsoft-color", "minimax-color", "mistral-color", "moonshot", "morph-color", "nousresearch", "nvidia-color", "openai", "openchat-color",
            "openrouter-color", "perplexity-color", "poolside-color", "qwen-color", "relace", "stepfun-color", "tencent-color", "together-color",
            "upstage-color", "xai", "xiaomimimo", "zai", "zeroone", "zhipu-color"];
        var badIcons = Brands.Where(b => b.IconKey is { } k && !vendorStems.Contains(k)).Select(b => b.IconKey).ToList();
        Check(badIcons.Count == 0, "brand icons not in Assets\\Vendors: " + string.Join(", ", badIcons));
        var audioBrands = BrandsFor(HubKind.Audio);
        Check(audioBrands.Any(b => b.Key == "whisper") && audioBrands.Any(b => b.Key == "kokoro") && audioBrands.Any(b => b.Key == "qwen") && audioBrands.All(b => b.Key != "llama"), "BrandsFor Audio");
        Check(audioBrands.Select(b => b.Label).SequenceEqual(audioBrands.Select(b => b.Label).OrderBy(l => l, StringComparer.OrdinalIgnoreCase)), "BrandsFor sorted by label");
        var textAudio = BrandsFor(HubKind.Text | HubKind.Audio);
        Check(textAudio.Any(b => b.Key == "llama") && textAudio.Any(b => b.Key == "whisper") && textAudio.All(b => b.Key != "flux"), "BrandsFor Text|Audio");
        Check(BrandsFor(0).Count == 0, "BrandsFor none");
        var brandPool = new[] { K("unsloth/Qwen3.8-27B-GGUF", null), K("acme/foo-GGUF", null), K("LiquidAI/LFM3-1.7B-GGUF", null), K("x/DeepSeek-V4-GGUF", null) };
        Check(FilterBrands(brandPool, new HashSet<string>()).Count == 4, "FilterBrands empty = no filter");
        Check(string.Join(",", FilterBrands(brandPool, new HashSet<string> { "qwen", "liquid" }).Select(m => m.Owner)) == "unsloth,LiquidAI", "FilterBrands keys");
        Check(string.Join(",", FilterBrands(brandPool, new HashSet<string> { "other" }).Select(m => m.Owner)) == "acme", "FilterBrands other");
        Check(string.Join(",", FilterBrands(brandPool, new HashSet<string> { "deepseek", "other" }).Select(m => m.Owner)) == "acme,x", "FilterBrands brand + other");
        Check(string.Join(",", BrandQueries(Brands.Where(b => b.Key is "deepseek" or "qwen" or "liquid"))) == "deepseek,qwen,lfm", "BrandQueries");
        Check(Brands.All(b => BrandQueries([b]).Single() is { Length: > 0 } w && w == w.ToLowerInvariant()), "every brand has a lower-case query");

        // 5. fuzzy search
        Check(Normalize("Un sloth/Qwen3.8_27B-GGUF") == "unslothqwen3827bgguf", "Normalize");
        Check(string.Join("|", QueryVariants(" qwen 3.8 ")) == "qwen 3.8|qwen3.8", "variants join");
        Check(string.Join("|", QueryVariants("qwen3.8")) == "qwen3.8|qwen 3.8", "variants split");
        Check(string.Join("|", QueryVariants("a 1b2")) == "a 1b2|a1b2|a 1b 2", "variants at most 3");
        const string qwen = "unsloth/Qwen3.8-27B-GGUF";
        Check(FuzzyScore("qwen 3.8", qwen) == 1, "qwen 3.8 matches exactly");
        Check(FuzzyScore("qwn 3.8", qwen) is > 0 and < 1, "qwn 3.8 typo matches");
        Check(FuzzyScore("qwenn", qwen) is > 0 and < 1, "qwenn typo matches");
        Check(FuzzyScore("llama 3 8b", "meta-llama/Llama-3-8B-GGUF") == 1, "llama 3 8b matches");
        Check(FuzzyScore("gemma", qwen) == 0, "gemma does not match Qwen");
        Check(FuzzyScore("qwn", qwen) == 0, "short tokens get no typo tolerance");
        Check(FuzzyScore("unslothqwn38", qwen) is > 0 and < 1 && FuzzyScore("unslthqwn38", qwen) is > 0 and < 1 && FuzzyScore("unslthqwn387", qwen) == 0, "7+ tolerates two edits, not three");
        var fz = new[] { M("bartowski/qwn-x", 0, 0), M(qwen, 0, 0), M("google/gemma-3", 0, 0), M("other/Qwen3.8-GGUF", 0, 0) };
        Check(string.Join(",", FuzzyFilter(fz, "qwen 3.8").Select(m => m.Id)) == qwen + ",other/Qwen3.8-GGUF", "FuzzyFilter keeps matches in order");
        Check(string.Join(",", FuzzyFilter(fz, "qwenn 3.8").Select(m => m.Id)) == qwen + ",other/Qwen3.8-GGUF", "FuzzyFilter typo keeps stable order");
        Check(FuzzyFilter(fz, "  ").Count == 4, "empty query keeps all");

        // 6. variants: shards summed and ordered, mmproj F16 chosen as companion, imatrix/mtp/draft skipped
        var vs = ParseVariants("""
            {"id":"unsloth/Qwen3.8-27B-GGUF","sha":"abc123","siblings":[
              {"rfilename":"README.md","size":1000},
              {"rfilename":"Qwen3.8-27B-Q4_K_M.gguf","size":16000000000},
              {"rfilename":"BF16/Qwen3.8-27B-BF16-00002-of-00002.gguf","size":24000000000},
              {"rfilename":"BF16/Qwen3.8-27B-BF16-00001-of-00002.gguf","size":30000000000},
              {"rfilename":"Qwen3.8-27B-UD-Q2_K_XL.gguf","size":10000000000},
              {"rfilename":"Qwen3.8-27B-weird.gguf","size":1000000000},
              {"rfilename":"mmproj-BF16.gguf","size":900000000},
              {"rfilename":"mmproj-F16.gguf","size":880000000},
              {"rfilename":"mmproj-F32.gguf","size":1700000000},
              {"rfilename":"imatrix_unsloth.gguf","size":5000000},
              {"rfilename":"Qwen3.8-27B-MTP-Q8_0.gguf","size":500000000},
              {"rfilename":"draft/Qwen3.8-0.6B-Q8_0.gguf","size":600000000},
              {"rfilename":"Qwen3.8-27B-IQ4_XS.gguf"}
            ]}
            """);
        Check(vs.Sha == "abc123" && vs.HasVision, "variants sha and vision");
        Check(string.Join(",", vs.Variants.Select(v => v.Label)) == "BF16,Q4_K_M,UD-Q2_K_XL,Qwen3.8-27B-weird,IQ4_XS", "variant labels and order: " + string.Join(",", vs.Variants.Select(v => v.Label)));
        var bf16 = vs.Variants[0];
        Check(bf16.SizeBytes == 54000000000 && bf16.DownloadBytes == 54880000000, "shard sizes summed");
        Check(string.Join(",", bf16.Files.Select(f => f.Name)) == "BF16/Qwen3.8-27B-BF16-00001-of-00002.gguf,BF16/Qwen3.8-27B-BF16-00002-of-00002.gguf", "shards in order");
        Check(vs.Variants.All(v => v.Companions.Count == 1 && v.Companions[0].Name == "mmproj-F16.gguf"), "F16 mmproj is every variant's companion");
        var dup = ParseVariants("""{"siblings":[{"rfilename":"a-Q8_0.gguf","size":10},{"rfilename":"b/a-Q8_0.gguf","size":20},{"rfilename":"mmproj-x-BF16.gguf","size":0}]}""");
        Check(dup.Sha == "" && dup.Variants[0].Companions[0].Name == "mmproj-x-BF16.gguf", "BF16 mmproj when no F16");
        Check(string.Join(",", dup.Variants.Select(v => v.Label + "=" + v.Quant)) == "b/a-Q8_0=Q8_0,a-Q8_0=Q8_0", "duplicate quants labelled by path");
        Check(!ParseVariants("""{"siblings":[{"rfilename":"x-Q4_0.gguf","size":1}]}""").HasVision, "no mmproj, no vision");

        // 7. fit classes at their boundaries
        static long Req(double required) => (long)((required - 1) / 1.15 * GiB);
        Check(FitOf(Req(23.27), 24, 64) == Fit.Fits && FitOf(Req(23.29), 24, 64) == Fit.Marginal, "Fits/Marginal at 97% of 24 GiB");
        Check(FitOf(Req(7.49), 8, 64) == Fit.Fits && FitOf(Req(7.51), 8, 64) == Fit.Marginal, "0.5 GiB reserve on small cards");
        Check(FitOf(Req(23.99), 24, 64) == Fit.Marginal && FitOf(Req(24.01), 24, 64) == Fit.Partial, "Marginal/Partial at the whole card");
        Check(FitOf(Req(55.27), 24, 64) == Fit.Partial && FitOf(Req(55.29), 24, 64) == Fit.Oom, "Partial/Oom at budget + half the RAM");
        Check(FitOf(Req(24.01), 24, null) == Fit.Oom, "GPU, RAM unknown");
        Check(FitOf(Req(15.99), null, 32) == Fit.Partial && FitOf(Req(16.01), 0, 32) == Fit.Oom, "no GPU: half the RAM");
        Check(FitOf(1, null, null) == Fit.Unknown && FitTip(Fit.Oom).Length > 0 && FitTip(Fit.Unknown) != FitTip(Fit.Fits), "unknown hardware and tips");
        HubVariant V(string label, double required) => new(label, label, Req(required), Req(required), [], []);
        var order = OrderByFit([V("oomBig", 90), V("fitsSmall", 5), V("partial", 30), V("oomSmall", 60), V("fitsBig", 20), V("marginal", 23.5), V("got", 100)], 24, 64, v => v.Label == "got");
        Check(string.Join(",", order.Select(v => v.Label)) == "got,fitsBig,fitsSmall,marginal,partial,oomSmall,oomBig", "OrderByFit: " + string.Join(",", order.Select(v => v.Label)));
        Check(ModelFits(q, 24, 64) && !ModelFits(q, 4, 8) && !ModelFits(models[1], 80, 512), "ModelFits from params*0.4");

        // 8. fetched sizes: the smallest weights (shards summed, mmproj/imatrix only: none); no-GGUF repos and, with Fits my
        // GPU on, repos larger than the whole card are dropped; unknown ones stay
        Check(SmallestWeights(dup) == 10, "SmallestWeights");
        var shardOnly = ParseVariants("""{"siblings":[{"rfilename":"x-Q8_0-00001-of-00002.gguf","size":3},{"rfilename":"x-Q8_0-00002-of-00002.gguf","size":4},{"rfilename":"mmproj-F16.gguf","size":1}]}""");
        Check(SmallestWeights(shardOnly) == 7, "SmallestWeights sums split parts, skips mmproj");
        Check(SmallestWeights(ParseVariants("""{"siblings":[{"rfilename":"mmproj-F16.gguf","size":5},{"rfilename":"imatrix.gguf","size":5},{"rfilename":"README.md","size":5}]}""")) is null, "mmproj/imatrix only: no weights");
        var fetchPool = new[] { M("big", 0, 0), M("small", 0, 0), M("none", 0, 0), M("unknown", 0, 0), M("edge", 0, 0) };
        var sizes = new Dictionary<string, long?> { ["big"] = (long)(8.5 * GiB), ["small"] = 3L << 30, ["none"] = null, ["edge"] = 8L << 30 };
        string Ids(List<HubModel> l) => string.Join(",", l.Select(m => m.Id));
        Check(Ids(FilterFetched(fetchPool, sizes, false, 8)) == "big,small,unknown,edge", "FilterFetched off: only no-GGUF dropped");
        Check(Ids(FilterFetched(fetchPool, sizes, true, 8)) == "small,unknown,edge", "FilterFetched Fits my GPU: larger than the card dropped, equal kept");
        Check(Ids(FilterFetched(fetchPool, sizes, true, null)) == "big,small,unknown,edge" && Ids(FilterFetched(fetchPool, sizes, true, 0)) == "big,small,unknown,edge", "FilterFetched VRAM unknown: no fit filter");

        // display
        Check(FormatSize(7_500_000_000) == "7.5 GB" && FormatSize(71_000_000_000) == "71 GB" && FormatSize(880_000_000) == "880 MB" && FormatSize(9_960_000_000) == "10 GB", "FormatSize");
        Check(FormatCount(1400) == "1.4K" && FormatCount(6_100_000) == "6.1M" && FormatCount(999) == "999" && FormatCount(27_300) == "27K", "FormatCount");
        Check(FormatAge(now.AddDays(-1), now) == "1d ago" && FormatAge(now.AddDays(-60), now) == "2mo ago" && FormatAge(now.AddDays(-27), now) == "27d ago"
              && FormatAge(null, now) == "" && FormatAge(now.AddHours(-3), now) == "3h ago", "FormatAge");
        Check(FormatSpeed(12_340_000) == "12 MB/s" && FormatSpeed(2_350_000) == "2.4 MB/s" && FormatSpeed(0) == "", "FormatSpeed: " + FormatSpeed(2_350_000));
        Check(FormatEta(45_000_000, 1_000_000) == "45s" && FormatEta(61_000_000, 1_000_000) == "2m" && FormatEta(3_900_000_000, 1_000_000) == "1h 05m"
              && FormatEta(10, 0) == "" && FormatEta(0, 5) == "0s", "FormatEta: " + FormatEta(3_900_000_000, 1_000_000));

        // README
        void Readme(string md, string want, string what) => Check(CleanReadme(md) == want, $"CleanReadme {what}: [{CleanReadme(md)}]");
        Readme("---\r\nlicense: mit\r\ntags: [gguf]\r\n---\r\n# Title\r\n\r\nBody", "# Title\n\nBody", "front matter");
        Readme("---\nno end", "---\nno end", "unclosed front matter");
        Readme("# A\n\n---\n\nB", "# A\n\n---\n\nB", "a rule is not front matter");
        Readme("<div align=\"center\">\n<img src=\"a.png\">\n</div>\n\n# T", "# T", "HTML block, image without alt dropped");
        Readme("A\n\n<p align=\"center\">centred <b>x</b>\nmore</p>\n\nB", "A\n\ncentred x\nmore\n\nB", "block tag keeps its text");
        Readme("<p align=center>\n  Welcome to the model\n</p>\n\nNext", "  Welcome to the model\n\nNext", "p align=center paragraph");
        Readme("A\n\n<img src=\"x.png\" />\n\nB", "A\n\nB", "tags-only line");
        Readme("<!-- note\n\nstill -->\nText", "Text", "comment over a blank line");
        Readme("<!-- one line -->\nText", "Text", "one-line comment");
        Readme("a <!-- c --> b", "a  b", "inline comment");
        Readme("```html\n<div>x</div>\n```", "```html\n<div>x</div>\n```", "HTML in a fence");
        Readme("See <b>bold</b> here\n<https://x.y>", "See bold here\n<https://x.y>", "inline tags and autolinks");
        Readme("<small><font color=\"red\">ABSOLUTE FIRE:</font> 640 ARC-C ...</small>", "ABSOLUTE FIRE: 640 ARC-C ...", "small and font");
        Readme("<font color=\"red\">NEW - Qwen 3.8 ...</font>", "NEW - Qwen 3.8 ...", "font");
        Readme("# Model\n\n<small><font color=\"red\">ABSOLUTE FIRE:</font> 640 ARC-C ...</small>\n\n```\n<small>keep</small> <font color=\"red\">x</font>\n```\n\nEnd",
            "# Model\n\nABSOLUTE FIRE: 640 ARC-C ...\n\n```\n<small>keep</small> <font color=\"red\">x</font>\n```\n\nEnd", "fence with <small> untouched");
        Readme("Use `<small>x</small>` and ``<b>`y`</b>`` then <i>it</i>", "Use `<small>x</small>` and ``<b>`y`</b>`` then it", "code spans untouched");
        Readme("~~~\n  <span>a</span>\n\n\n~~~", "~~~\n  <span>a</span>\n\n\n~~~", "tilde fence and its blank lines untouched");
        Readme("x < 5 and y > 3; <model> and <s>[INST] and <source>", "x < 5 and y > 3; <model> and <s>[INST] and <source>", "comparisons and placeholders kept");
        Readme("Go <a href=\"https://x.y/z\" target=\"_blank\">to <b>site</b></a>.", "Go [to site](https://x.y/z).", "a -> Markdown link");
        Readme("<a name=\"top\"></a>\n# T", "# T", "anchor without text");
        Readme("<a href='https://h'><img src=\"b.svg\" alt=\"badge\"></a>", "[![badge](b.svg)](https://h)", "linked badge");
        Readme("<img alt=\"Logo\" width=200 src=logo.png>", "![Logo](logo.png)", "img with alt and src");
        Readme("<img\n  src=\"x.png\"\n  alt=\"Two lines\">", "![Two lines](x.png)", "img over lines");
        Readme("<div align=\"center\">\n    <img src=\"x.png\" alt=\"X\">\n</div>", "![X](x.png)", "indented tag not a code block");
        Readme("a<br>b<br/>c<BR />d", "a\nb\nc\nd", "br breaks lines");
        Readme("<details><summary>More</summary>\n\nHidden text\n</details>", "More\n\nHidden text", "details and summary keep text");
        Readme("<table><tr><th>Q</th><th>Score</th></tr><tr><td>Q4</td><td>9</td></tr></table>", "Q\n\nScore\n\nQ4\n\n9", "table cells on their own lines");
        Readme("<style>.x{color:red}</style>Text<script>alert(1)</script>", "Text", "style and script dropped");
        Readme("H<sub>2</sub>O and x<sup>2</sup>, <mark>hi</mark> <em>e</em> <strong>s</strong> <u>u</u> <span class=\"k\">k</span> <center>c</center>",
            "H2O and x2, hi e s u k \nc", "inline presentational tags");
        Readme("  - item <b>bold</b>", "  - item bold", "list indentation kept when the line starts with text");
    }
}

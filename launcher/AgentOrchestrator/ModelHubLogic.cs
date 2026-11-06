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
    public static Uri SearchUrl(string query, string sortId, int limit = 100)
    {
        var q = new List<string> { "filter=gguf" };
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

    // Drops diffusion repos. "latest": newest that are also trending first, (trendingScore+2)/(ageDays+2)^1.6; with a query,
    // fuzzy match quality comes before that. Other sorts keep HF's order.
    public static List<HubModel> Rank(IEnumerable<HubModel> models, string query, string sortId, DateTimeOffset now)
    {
        var list = models.Where(m => !m.IsDiffusion).ToList();
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

    // A line opening a raw HTML block: a comment, a block-level tag, or nothing but tags (an autolink like <https://x> is not a tag).
    private static readonly Regex HtmlBlockRx = new(
        @"^\s*(<!--|</?(address|article|aside|blockquote|center|details|div|dl|figure|footer|h[1-6]|header|hr|li|nav|ol|p|picture|section|summary|table|tbody|td|th|thead|tr|ul)(\s|/?>|$)|(</?[a-z][a-z0-9-]*(\s[^>]*)?/?>\s*)+$)",
        RegexOptions.IgnoreCase);

    // The README as our Markdown renderer can show it: no YAML front matter, no raw HTML blocks (from the opening line through
    // the next blank line, a comment through its end), code fences untouched. ponytail: CommonMark's HTML block rules, roughly.
    public static string CleanReadme(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var at = 0;
        if (lines[0].TrimEnd() == "---" && Array.FindIndex(lines, 1, l => l.TrimEnd() is "---" or "...") is var end and > 0)
            at = end + 1;
        var kept = new List<string>();
        bool fence = false, html = false, comment = false;
        for (; at < lines.Length; at++)
        {
            var line = lines[at];
            var t = line.Trim();
            if (comment)
            {
                comment = !line.Contains("-->", StringComparison.Ordinal);
                continue;
            }
            if (html && t.Length > 0)
                continue;
            if (html)
            {
                html = false;
                if (kept.Count == 0 || kept[^1].Trim().Length == 0)
                    continue;
            }
            else if (!fence && HtmlBlockRx.IsMatch(line))
            {
                if (t.StartsWith("<!--", StringComparison.Ordinal))
                    comment = !t[4..].Contains("-->", StringComparison.Ordinal);
                else
                    html = true;
                continue;
            }
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
                fence = !fence;
            kept.Add(line);
        }
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
        Check(string.Join(",", Rank(pool, "", "latest", now).Select(m => m.Id)) == "hot,fresh,old", "latest ranking");
        Check(string.Join(",", Rank(pool, "", "likes", now).Select(m => m.Id)) == "old,fresh,hot", "other sorts keep HF order, drop diffusion");
        Check(string.Join(",", Rank(pool, "fresh", "latest", now).Select(m => m.Id)) == "fresh,hot,old", "latest with a query puts matches first");

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

        // display
        Check(FormatSize(7_500_000_000) == "7.5 GB" && FormatSize(71_000_000_000) == "71 GB" && FormatSize(880_000_000) == "880 MB" && FormatSize(9_960_000_000) == "10 GB", "FormatSize");
        Check(FormatCount(1400) == "1.4K" && FormatCount(6_100_000) == "6.1M" && FormatCount(999) == "999" && FormatCount(27_300) == "27K", "FormatCount");
        Check(FormatAge(now.AddDays(-1), now) == "1d ago" && FormatAge(now.AddDays(-60), now) == "2mo ago" && FormatAge(now.AddDays(-27), now) == "27d ago"
              && FormatAge(null, now) == "" && FormatAge(now.AddHours(-3), now) == "3h ago", "FormatAge");

        // README
        void Readme(string md, string want, string what) => Check(CleanReadme(md) == want, $"CleanReadme {what}: [{CleanReadme(md)}]");
        Readme("---\r\nlicense: mit\r\ntags: [gguf]\r\n---\r\n# Title\r\n\r\nBody", "# Title\n\nBody", "front matter");
        Readme("---\nno end", "---\nno end", "unclosed front matter");
        Readme("# A\n\n---\n\nB", "# A\n\n---\n\nB", "a rule is not front matter");
        Readme("<div align=\"center\">\n<img src=\"a.png\">\n</div>\n\n# T", "# T", "HTML block");
        Readme("A\n\n<p align=\"center\">centred <b>x</b>\nmore</p>\n\nB", "A\n\nB", "block tag with text");
        Readme("A\n\n<img src=\"x.png\" />\n\nB", "A\n\nB", "tags-only line");
        Readme("<!-- note\n\nstill -->\nText", "Text", "comment over a blank line");
        Readme("<!-- one line -->\nText", "Text", "one-line comment");
        Readme("```html\n<div>x</div>\n```", "```html\n<div>x</div>\n```", "HTML in a fence");
        Readme("See <b>bold</b> here\n<https://x.y>", "See <b>bold</b> here\n<https://x.y>", "inline tags and autolinks");
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator;

// The Remote API screen's data: the OpenAI-compatible services it offers, their model lists, and OpenRouter's per-provider
// prices and speeds for one model.
internal static class RemoteCatalog
{
    public sealed record Service(string Id, string Name, string BaseUrl, string KeyEnv, string KeyUrl, string KeySteps, bool ModelsNeedKey, bool HasProviders);

    // ContextLength 0 = unknown; prices $/M from OpenRouter's "pricing", null = unknown (variable, unlisted, another service).
    public sealed record ModelEntry(string Id, string Name, long ContextLength, decimal? InputPerM = null, decimal? OutputPerM = null);

    public sealed record ProviderRow(string Name, string Tag, string Quantization, decimal? InputPerM, decimal? OutputPerM, decimal? CacheReadPerM,
        double? LatencySeconds, double? ThroughputTps, double? Uptime, bool Vision, long ContextLength, string Privacy = "");

    // How a provider treats prompts, as OpenRouter's own provider page shows it (its API does not carry this): Private = no training and no
    // retention; Logs = may retain prompts but does not train; Trains = may train on them; "" = not known.
    public const string Private = "private", Logs = "logs", Trains = "trains";

    public static string PrivacyTip(string privacy) => privacy switch
    {
        Private => "Private: this provider does not train on prompts and does not retain prompt data.",
        Logs => "Logs: this provider may retain prompts, but does not use them for training.",
        Trains => "Trains: this provider may use prompts for training.",
        _ => "",
    };

    // slug -> level from the data policies embedded in OpenRouter's /{model}/providers page (quotes escaped inside its script data).
    public static Dictionary<string, string> ParsePrivacy(string page)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(page.Replace("\\\"", "\""),
            "\"slug\":\"([^\"]+)\"[^{}]{0,300}\"dataPolicy\":\\{[^{}]*?\"training\":(true|false)[^{}]*?\"retainsPrompts\":(true|false)"))
            found[m.Groups[1].Value] = m.Groups[2].Value == "true" ? Trains : m.Groups[3].Value == "true" ? Logs : Private;
        return found;
    }

    // tag -> takes images, for the endpoints of `variant` (the page also lists other variants, ":batch" and the like) on the same page.
    // The API's endpoints carry no such flag, but an endpoint's "features" there does when it differs from the model: relace/fp4 serves
    // the image-input GLM-5.3 Flash text-only, and OpenRouter's router drops it for a request with an image. The engine sends images
    // as base64 data URLs, so supports_base64_image_input decides. An endpoint without the flag takes what the model takes.
    public static Dictionary<string, bool> ParseImageInput(string page, string variant)
    {
        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var text = page.Replace("\\\"", "\"");
        const string anchor = "\"model_variant_slug\":\"";
        var starts = new List<int>();
        for (var at = text.IndexOf(anchor, StringComparison.Ordinal); at >= 0; at = text.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal))
            starts.Add(at + anchor.Length);
        for (var i = 0; i < starts.Count; i++)
        {
            var chunk = text[starts[i]..(i + 1 < starts.Count ? starts[i + 1] : text.Length)]; // one endpoint object, from its variant on
            if (!chunk.StartsWith(variant + "\"", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.Match(chunk, "\"provider_slug\":\"([^\"]+)\"") is not { Success: true } slug)
                continue;
            var features = chunk.IndexOf("\"features\":{", StringComparison.Ordinal);
            if (features >= 0 && System.Text.RegularExpressions.Regex.Match(chunk.Substring(features, Math.Min(800, chunk.Length - features)),
                    "\"supports_base64_image_input\":(true|false)") is { Success: true } image)
                found[slug.Groups[1].Value] = image.Groups[1].Value == "true";
        }
        return found;
    }

    // The endpoint tag is "<provider slug>/<quantization>" (or just the slug): privacy by slug, image input by the whole tag.
    public static List<ProviderRow> WithPrivacy(List<ProviderRow> rows, Dictionary<string, string> privacy, Dictionary<string, bool>? images = null) =>
        privacy.Count == 0 && images is not { Count: > 0 } ? rows : [.. rows.Select(r =>
            (privacy.TryGetValue(r.Tag.Split('/')[0], out var p) ? r with { Privacy = p } : r) is var row && images?.TryGetValue(r.Tag, out var v) == true ? row with { Vision = v } : row)];

    // ModelsNeedKey: what an unauthenticated GET {BaseUrl}/models answered on 2026-11-05 (200 => false; 401/403, or Gemini's 404 => true).
    public static IReadOnlyList<Service> Services { get; } =
    [
        S("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY", "https://openrouter.ai/settings/keys", "openrouter.ai", false, true),
        S("openai", "OpenAI", "https://api.openai.com/v1", "OPENAI_API_KEY", "https://platform.openai.com/api-keys", "platform.openai.com", true),
        S("groq", "Groq", "https://api.groq.com/openai/v1", "GROQ_API_KEY", "https://console.groq.com/keys", "console.groq.com", true),
        S("together", "Together", "https://api.together.xyz/v1", "TOGETHER_API_KEY", "https://api.together.ai/settings/api-keys", "together.ai", true),
        S("fireworks", "Fireworks", "https://api.fireworks.ai/inference/v1", "FIREWORKS_API_KEY", "https://app.fireworks.ai/settings/users/api-keys", "fireworks.ai", true),
        S("deepinfra", "DeepInfra", "https://api.deepinfra.com/v1/openai", "DEEPINFRA_API_KEY", "https://deepinfra.com/dash/api_keys", "deepinfra.com", false),
        S("mistral", "Mistral", "https://api.mistral.ai/v1", "MISTRAL_API_KEY", "https://console.mistral.ai/api-keys", "console.mistral.ai", true),
        S("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "DEEPSEEK_API_KEY", "https://platform.deepseek.com/api_keys", "platform.deepseek.com", true),
        S("xai", "xAI", "https://api.x.ai/v1", "XAI_API_KEY", "https://console.x.ai/", "console.x.ai", true),
        S("cerebras", "Cerebras", "https://api.cerebras.ai/v1", "CEREBRAS_API_KEY", "https://cloud.cerebras.ai/", "cloud.cerebras.ai", true),
        S("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_API_KEY", "https://aistudio.google.com/app/apikey", "aistudio.google.com", true),
        S("nvidia", "NVIDIA NIM", "https://integrate.api.nvidia.com/v1", "NVIDIA_API_KEY", "https://build.nvidia.com/settings/api-keys", "build.nvidia.com", false),
        new("custom", "Custom URL…", "", "", "", "", true, false),
    ];

    private static Service S(string id, string name, string baseUrl, string keyEnv, string keyUrl, string site, bool modelsNeedKey, bool hasProviders = false) =>
        new(id, name, baseUrl, keyEnv, keyUrl,
            $"Sign in or sign up at {site}, open the API keys page, then create a new key and copy it. " +
            $"Paste the key into the API key box on the Remote API page. (Or set the environment variable {keyEnv} yourself, then restart Agent Orchestrator.)",
            modelsNeedKey, hasProviders);

    // The service whose host the address uses (scheme, path, trailing slash and case ignored); null = a custom URL.
    public static Service? ServiceFor(string baseUrl) =>
        Uri.TryCreate(baseUrl.Contains("://") ? baseUrl.Trim() : "https://" + baseUrl.Trim(), UriKind.Absolute, out var uri)
            ? Services.FirstOrDefault(s => s.BaseUrl != "" && new Uri(s.BaseUrl).Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
            : null;

    // {"data":[{id, name?, context_length?}]} (OpenAI, OpenRouter) or a bare array of the same (Together); sorted by id.
    public static List<ModelEntry> ParseModels(string json) =>
        Reply(json, "The service's reply is not a model list (expected {\"data\":[...]} or a JSON array).", root =>
    {
        var list = root as JsonArray ?? (root as JsonObject)?["data"] as JsonArray ?? throw new FormatException();
        var models = new List<ModelEntry>();
        foreach (var m in list.OfType<JsonObject>())
        {
            if (Str(m["id"]) is not { Length: > 0 } id)
                continue;
            if (id.StartsWith("models/", StringComparison.Ordinal)) // Gemini
                id = id["models/".Length..];
            var context = m["context_length"] ?? m["context_window"] ?? m["max_model_len"] ?? (m["metadata"] as JsonObject)?["context_length"]; // the last: DeepInfra
            var pricing = m["pricing"] as JsonObject;
            models.Add(new ModelEntry(id, Str(m["name"]) is { Length: > 0 } name ? name : id, (long)(Num(context) ?? 0),
                PerMillion(pricing?["prompt"]), PerMillion(pricing?["completion"])));
        }
        models.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
        return models;
    });

    // JsonNode reads objects lazily, so a reply it parsed can still throw (ArgumentException on a duplicate key) while
    // read: anything but cancellation becomes the one "not a ... list" error the page shows.
    private static T Reply<T>(string json, string notList, Func<JsonNode?, T> read)
    {
        try { return read(JsonNode.Parse(json)); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new InvalidOperationException(notList, e); }
    }

    public static async Task<List<ModelEntry>> FetchModelsAsync(HttpClient http, string baseUrl, string? apiKey, CancellationToken ct) =>
        ParseModels(await GetAsync(http, baseUrl.TrimEnd('/') + "/models", apiKey, ct).ConfigureAwait(false));

    // OpenRouter's GET /models/{id}/endpoints: one row per provider, cheapest input first (unknown prices last).
    public static List<ProviderRow> ParseProviders(string json) => Reply(json, "OpenRouter's reply is not a provider list.", root =>
    {
        var data = (root as JsonObject)?["data"] as JsonObject;
        var endpoints = data?["endpoints"] as JsonArray ?? throw new FormatException();
        // supports_image_reference is false even for image-input models (gpt-4o-mini), so the model's input modalities count too.
        var modelVision = (data!["architecture"] as JsonObject)?["input_modalities"] is JsonArray inputs && inputs.Any(i => Str(i) == "image");
        var rows = endpoints.OfType<JsonObject>().Select(e =>
        {
            var pricing = e["pricing"] as JsonObject;
            return new ProviderRow(Str(e["provider_name"]) ?? "", Str(e["tag"]) ?? "", Str(e["quantization"]) ?? "",
                PerMillion(pricing?["prompt"]), PerMillion(pricing?["completion"]), PerMillion(pricing?["input_cache_read"]),
                Num((e["latency_last_30m"] as JsonObject)?["p50"] ?? e["latency_last_30m"]) / 1000, // milliseconds, per OpenRouter's API reference
                Num((e["throughput_last_30m"] as JsonObject)?["p50"] ?? e["throughput_last_30m"]),
                Num(e["uptime_last_30m"]),
                modelVision || (e["supports_image_reference"] as JsonValue)?.TryGetValue<bool>(out var image) == true && image,
                (long)(Num(e["context_length"]) ?? 0));
        }).ToList();
        return (List<ProviderRow>)[.. rows.OrderBy(r => r.InputPerM is null).ThenBy(r => r.InputPerM).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    });

    // Public without a key; latency and throughput come back null unless a key is sent.
    public static async Task<List<ProviderRow>> FetchProvidersAsync(HttpClient http, string modelId, CancellationToken ct, string? apiKey = null)
    {
        var page = FetchPrivacyAsync(http, modelId.Trim(), ct);
        var rows = ParseProviders(await GetAsync(http, $"https://openrouter.ai/api/v1/models/{modelId.Trim()}/endpoints", apiKey, ct).ConfigureAwait(false));
        var (privacy, images) = await page.ConfigureAwait(false);
        return WithPrivacy(rows, privacy, images);
    }

    // Best effort: the shields and per-provider image input are niceties, so a page that is down or reshaped leaves the rows as the API
    // gave them. No key goes to the page.
    private static async Task<(Dictionary<string, string> Privacy, Dictionary<string, bool> Images)> FetchPrivacyAsync(HttpClient http, string modelId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            // A variant (":free") has a page of its own, with its own providers; ":floor" and the like may not: then the base model's.
            foreach (var id in modelId.Contains(':') ? new[] { modelId, modelId.Split(':')[0] } : [modelId])
            {
                using var response = await http.GetAsync($"https://openrouter.ai/{id}/providers", timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (ParsePrivacy(text) is { Count: > 0 } found)
                    return (found, ParseImageInput(text, id));
            }
            return ([], []);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return ([], []);
        }
    }

    private static async Task<string> GetAsync(HttpClient http, string url, string? apiKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new HttpRequestException(string.IsNullOrWhiteSpace(apiKey)
                    ? $"{url} needs an API key ({(int)response.StatusCode})."
                    : $"{url} refused the API key ({(int)response.StatusCode}); check that it is correct and active.", null, response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"{url} did not answer within 15 seconds.");
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? n) =>
        n is not JsonValue v ? null
        : v.TryGetValue<double>(out var d) ? d
        : Str(v) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;

    // OpenRouter prices are USD per token as strings ("0.0000000285"); negative ("-1": priced per request) = unknown.
    private static decimal? PerMillion(JsonNode? n)
    {
        decimal perToken;
        if (Str(n) is { } s)
        {
            if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out perToken))
                return null;
        }
        else if (n is not JsonValue v || !v.TryGetValue(out perToken))
            return null;
        return perToken < 0 ? null : perToken * 1_000_000m;
    }

    // --self-test: canned replies trimmed from the live services (2026-11-05), and the fetcher through a fake handler.
    internal static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"RemoteCatalog self-test failed: {what}");
        }

        var openAi = ParseModels("{\"object\":\"list\",\"data\":[{\"id\":\"tencent/hy4\",\"name\":\"Tencent: Hy4\",\"context_length\":262144}," +
            "{\"id\":\"Alpha/b\",\"context_window\":8192},{\"name\":\"no id\"},{\"id\":\"c\",\"max_model_len\":32768.0}," +
            "{\"id\":\"ByteDance/Seed-2.0-pro\",\"metadata\":{\"context_length\":256000}}]}");
        Check(openAi.Select(m => m.Id).SequenceEqual(["Alpha/b", "ByteDance/Seed-2.0-pro", "c", "tencent/hy4"]), "OpenAI-style ids or order");
        Check(openAi[3] == new ModelEntry("tencent/hy4", "Tencent: Hy4", 262144) && openAi[0] == new ModelEntry("Alpha/b", "Alpha/b", 8192) &&
            openAi[2].ContextLength == 32768 && openAi[1].ContextLength == 256000, "OpenAI-style names or context lengths");
        var bare = ParseModels("[{\"id\":\"meta-llama/Llama-3.3-70B-Instruct-Turbo\",\"display_name\":\"Llama\",\"context_length\":131072},{\"id\":\"\"}]");
        Check(bare is [{ Id: "meta-llama/Llama-3.3-70B-Instruct-Turbo", ContextLength: 131072 }], "bare array");
        var gemini = ParseModels("{\"object\":\"list\",\"data\":[{\"id\":\"models/gemini-2.0-flash\",\"object\":\"model\",\"owned_by\":\"google\"}]}");
        Check(gemini is [{ Id: "gemini-2.0-flash", Name: "gemini-2.0-flash", ContextLength: 0 }], "Gemini models/ prefix");
        Check(openAi.All(m => m.InputPerM is null && m.OutputPerM is null) && bare[0].InputPerM is null && gemini[0].OutputPerM is null, "no pricing: no prices");
        // OpenRouter's GET /models (trimmed, 2026-11-05): USD per token as strings; "-1" = variable (the auto router).
        var priced = ParseModels("{\"data\":[{\"id\":\"google/gemini-omni-1.2-flash\",\"name\":\"Google: Gemini Omni 1.2 Flash\",\"context_length\":131072," +
            "\"pricing\":{\"prompt\":\"0.0000015\",\"completion\":\"0.000009\",\"web_search\":\"0.014\"}}," +
            "{\"id\":\"openrouter/auto\",\"name\":\"Auto Router\",\"context_length\":2000000,\"pricing\":{\"prompt\":\"-1\",\"completion\":\"-1\"}}," +
            "{\"id\":\"stealth/crouton-alpha\",\"name\":\"Crouton Alpha\",\"context_length\":1048576,\"pricing\":{\"prompt\":\"0\",\"completion\":\"0\"}}," +
            "{\"id\":\"x/relace\",\"pricing\":{\"prompt\":\"0.0000000285\",\"completion\":\"junk\"}},{\"id\":\"x/half\",\"pricing\":{\"completion\":\"0.0000004\"}}]}");
        Check(priced is [{ Id: "google/gemini-omni-1.2-flash", InputPerM: 1.5m, OutputPerM: 9m, ContextLength: 131072 },
            { Id: "openrouter/auto", InputPerM: null, OutputPerM: null }, { Id: "stealth/crouton-alpha", InputPerM: 0m, OutputPerM: 0m },
            { Id: "x/half", InputPerM: null, OutputPerM: 0.4m }, { Id: "x/relace", InputPerM: 0.0285m, OutputPerM: null }],
            "OpenRouter model prices: " + string.Join(", ", priced));
        // The probe: .NET 8's JsonNode parses a duplicate key and throws only when the object is read (so the replies below reach the reader).
        var duplicate = JsonNode.Parse("{\"a\":1,\"a\":2}")!.AsObject();
        try { _ = duplicate["a"]; Check(false, "probe: JsonNode read a duplicate key"); }
        catch (ArgumentException) { }
        string[] junkReplies = ["", "<html>502</html>", "{\"error\":{\"code\":404}}", "null", "42", "{\"data\":5}", "{\"data\":{\"endpoints\":\"x\"}}",
            "{\"data\":[],\"data\":[]}", "{\"data\":[{\"id\":\"a\",\"id\":\"b\"}]}", "{\"data\":{\"endpoints\":[],\"endpoints\":[]}}",
            "{\"data\":{\"endpoints\":[{\"tag\":\"a\",\"tag\":\"b\"}]}}", "{\"data\":{\"architecture\":{\"x\":1,\"x\":2},\"endpoints\":[]}}"];
        foreach (var junk in junkReplies) // each is neither list; anything but the InvalidOperationException fails the self-test
        {
            try { ParseModels(junk); Check(false, $"junk accepted as models: {junk}"); }
            catch (InvalidOperationException e) when (e.Message.Contains("not a model list")) { }
            try { ParseProviders(junk); Check(false, $"junk accepted as providers: {junk}"); }
            catch (InvalidOperationException e) when (e.Message.Contains("not a provider list")) { }
        }

        var providers = ParseProviders("{\"data\":{\"id\":\"z-ai/glm-5.3-flash\",\"architecture\":{\"input_modalities\":[\"text\"]},\"endpoints\":[" +
            "{\"provider_name\":\"OpenInference\",\"tag\":\"open-inference/fp4\",\"quantization\":\"fp4\",\"context_length\":1048576," +
            "\"pricing\":{\"prompt\":\"0.00000003\",\"completion\":\"0.00000022\",\"input_cache_read\":\"0.00000002\",\"discount\":0}," +
            "\"uptime_last_30m\":100,\"supports_image_reference\":false,\"latency_last_30m\":{\"p50\":450,\"p75\":600,\"p90\":800,\"p99\":1200},\"throughput_last_30m\":{\"p50\":85.5,\"p75\":70,\"p90\":60,\"p99\":40}}," +
            "{\"provider_name\":\"Mystery\",\"tag\":\"mystery\",\"quantization\":\"unknown\",\"context_length\":65536,\"pricing\":{\"prompt\":\"-1\"}," +
            "\"uptime_last_30m\":null,\"supports_image_reference\":true,\"latency_last_30m\":null,\"throughput_last_30m\":null}," +
            "{\"provider_name\":\"Relace\",\"tag\":\"relace/fp4\",\"quantization\":\"fp4\",\"context_length\":1048576," +
            "\"pricing\":{\"prompt\":\"0.0000000285\",\"completion\":\"0.0000000923\",\"input_cache_read\":\"0.0000000285\",\"discount\":0}," +
            "\"uptime_last_30m\":93.33333333333333,\"supports_image_reference\":false,\"latency_last_30m\":null,\"throughput_last_30m\":null}," +
            "{\"provider_name\":\"GMICloud\",\"tag\":\"gmicloud/fp8\",\"quantization\":\"fp8\",\"context_length\":202752," +
            "\"pricing\":{\"prompt\":\"0.00000006\",\"completion\":\"0.00000044\",\"discount\":0.5},\"uptime_last_30m\":100,\"latency_last_30m\":null,\"throughput_last_30m\":null}]}}");
        Check(providers.Select(p => p.Tag).SequenceEqual(["relace/fp4", "open-inference/fp4", "gmicloud/fp8", "mystery"]), "provider order: " + string.Join(",", providers.Select(p => p.Tag)));
        Check(providers[0] == new ProviderRow("Relace", "relace/fp4", "fp4", 0.0285m, 0.0923m, 0.0285m, null, null, 93.33333333333333, false, 1048576), $"Relace row: {providers[0]}");
        Check(providers[1].InputPerM == 0.03m && providers[1].OutputPerM == 0.22m && providers[1].CacheReadPerM == 0.02m &&
            providers[1].LatencySeconds == 0.45 && providers[1].ThroughputTps == 85.5 && providers[1].Uptime == 100, $"OpenInference row: {providers[1]}");
        Check(providers[2].InputPerM == 0.06m && providers[2].CacheReadPerM is null, $"GMICloud row: {providers[2]}");
        Check(providers[3] is { InputPerM: null, OutputPerM: null, Uptime: null, Vision: true, ContextLength: 65536 }, $"Mystery row: {providers[3]}");
        Check(ParseProviders("{\"data\":{\"architecture\":{\"input_modalities\":[\"text\",\"image\"]},\"endpoints\":[{\"tag\":\"openai\",\"supports_image_reference\":false}]}}")
            is [{ Vision: true, Tag: "openai", InputPerM: null }], "an image-input model is Vision");
        try { ParseProviders("{\"error\":{\"message\":\"No endpoints found\"}}"); Check(false, "a non-provider reply parsed"); }
        catch (InvalidOperationException e) when (e.Message.Contains("not a provider list")) { }
        var shields = ParsePrivacy(PrivacySample);
        Check(shields.Count == 3 && shields["gmicloud"] == Logs && shields["deepinfra"] == Private && shields["novita"] == Trains, $"privacy levels: {string.Join(",", shields)}");
        Check(ParsePrivacy("").Count == 0 && ParsePrivacy("<html>no data</html>").Count == 0 && PrivacyTip(Private).StartsWith("Private:") && PrivacyTip("") == "", "privacy of nothing");
        // Per-provider image input: relace says no, together has no flag (the model's), sail says no here but yes in the :batch variant.
        var images = ParseImageInput(ImageSample, "z-ai/glm-5.3-flash");
        Check(images.Count == 2 && !images["relace/fp4"] && !images["sail-research/fp4"], $"image input: {string.Join(",", images)}");
        Check(ParseImageInput(ImageSample, "z-ai/glm-5.3-flash:batch") is { Count: 1 } batch && batch["sail-research/fp4"], "image input of the :batch variant");
        var visionRows = WithPrivacy(ParseProviders("{\"data\":{\"architecture\":{\"input_modalities\":[\"text\",\"image\"]},\"endpoints\":[" +
            "{\"tag\":\"relace/fp4\",\"pricing\":{\"prompt\":\"0.00000001\"}},{\"tag\":\"together\",\"pricing\":{\"prompt\":\"0.00000002\"}}]}}"), [], images);
        Check(visionRows is [{ Tag: "relace/fp4", Vision: false }, { Tag: "together", Vision: true }], $"vision merged: {string.Join(",", visionRows.Select(r => $"{r.Tag}={r.Vision}"))}");

        Check(ServiceFor("https://openrouter.ai/api/v1/")?.Id == "openrouter" && ServiceFor("HTTP://OpenRouter.AI/api/v1")?.Id == "openrouter" &&
            ServiceFor("api.groq.com/openai/v1")?.Id == "groq", "ServiceFor a known service");
        Check(ServiceFor("http://127.0.0.1:8888/v1") is null && ServiceFor("https://my.host/v1") is null && ServiceFor("") is null && ServiceFor("not a url") is null, "ServiceFor a custom URL");
        Check(Services[0].Id == "openrouter" && Services[0].HasProviders && !Services[0].ModelsNeedKey &&
            Services[^1] is { Id: "custom", Name: "Custom URL…", BaseUrl: "", KeyEnv: "", HasProviders: false } &&
            Services.Select(s => s.Id).Distinct().Count() == Services.Count && Services.Count(s => s.HasProviders) == 1, "Services ids, first or last");
        foreach (var s in Services.SkipLast(1))
            Check(System.Text.RegularExpressions.Regex.IsMatch(s.KeyEnv, "^[A-Z][A-Z0-9_]*$") && s.KeyUrl.StartsWith("https://") && s.KeySteps.Contains(s.KeyEnv) &&
                Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var u) && u.Scheme == "https" && !s.BaseUrl.EndsWith('/'), $"service {s.Id}");

        Task.Run(async () =>
        {
            var fake = new FakeHandler();
            using var http = new HttpClient(fake);
            fake.Reply = (HttpStatusCode.OK, "{\"data\":[{\"id\":\"b\"},{\"id\":\"a\",\"context_length\":4096}]}");
            var models = await FetchModelsAsync(http, "https://api.example.test/v1/", null, CancellationToken.None);
            Check(models is [{ Id: "a", ContextLength: 4096 }, { Id: "b" }] && fake.Seen?.RequestUri?.ToString() == "https://api.example.test/v1/models" &&
                fake.Seen.Headers.Authorization is null && fake.Seen.Headers.Accept.ToString() == "application/json", "fetch without a key");
            await FetchModelsAsync(http, "https://api.example.test/v1", " sk-test ", CancellationToken.None);
            Check(fake.Seen?.Headers.Authorization?.ToString() == "Bearer sk-test", "fetch sends the key");
            fake.Reply = (HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Missing API key\"}}");
            try { await FetchModelsAsync(http, "https://api.example.test/v1", "", CancellationToken.None); Check(false, "401 did not throw"); }
            catch (HttpRequestException e) when (e.Message.Contains("needs an API key") && e.StatusCode == HttpStatusCode.Unauthorized) { }
            Check(fake.Seen?.Headers.Authorization is null, "an empty key was sent");
            fake.Reply = (HttpStatusCode.OK, "<html>login</html>");
            try { await FetchModelsAsync(http, "https://api.example.test/v1", null, CancellationToken.None); Check(false, "HTML parsed as models"); }
            catch (InvalidOperationException e) when (e.Message.Contains("not a model list")) { }
            fake.Reply = (HttpStatusCode.OK, "{\"data\":{\"endpoints\":[]}}");
            fake.All.Clear();
            Check((await FetchProvidersAsync(http, "z-ai/glm-5.3-flash", CancellationToken.None)).Count == 0 &&
                fake.All.Any(r => r.RequestUri?.ToString() == "https://openrouter.ai/api/v1/models/z-ai/glm-5.3-flash/endpoints" && r.Headers.Authorization is null), "provider fetch");
            // With a key: the endpoints get it, the page (privacy shields) never does; the page's answer is merged by provider slug.
            fake.Reply = (HttpStatusCode.OK, "{\"data\":{\"endpoints\":[{\"provider_name\":\"GMICloud\",\"tag\":\"gmicloud/fp8\"},{\"provider_name\":\"Other\",\"tag\":\"other\"}]}}");
            fake.Page = (HttpStatusCode.OK, PrivacySample);
            fake.All.Clear();
            var shown = await FetchProvidersAsync(http, "deepseek/v3:floor", CancellationToken.None, "sk-test");
            Check(fake.All.Any(r => r.RequestUri?.ToString() == "https://openrouter.ai/deepseek/v3:floor/providers" && r.Headers.Authorization is null) &&
                fake.All.Count(r => r.Headers.Authorization is not null) == 1, "privacy page fetch (the variant's own page first)");
            Check(shown.Select(r => r.Privacy).SequenceEqual([Logs, ""]), $"privacy merged: {string.Join(",", shown.Select(r => $"{r.Tag}={r.Privacy}"))}");
            // A variant whose page carries nothing falls back to the base model's page.
            fake.PageOnly = "https://openrouter.ai/deepseek/v3/providers";
            fake.All.Clear();
            Check((await FetchProvidersAsync(http, "deepseek/v3:floor", CancellationToken.None)).Select(r => r.Privacy).SequenceEqual([Logs, ""]) &&
                fake.All.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/providers", StringComparison.Ordinal)) == 2, "privacy falls back to the base model's page");
            fake.PageOnly = null;
            fake.Page = (HttpStatusCode.InternalServerError, "");
            Check((await FetchProvidersAsync(http, "deepseek/v3", CancellationToken.None)).Count == 2, "rows still shown when the page is down");
        }).GetAwaiter().GetResult();
    }

    // Two providers' data policies as the page embeds them (quotes escaped): gmicloud retains, deepinfra keeps nothing, novita trains.
    private const string PrivacySample =
        "x{\\\"displayName\\\":\\\"GMICloud\\\",\\\"slug\\\":\\\"gmicloud\\\",\\\"baseUrl\\\":\\\"https://api.gmi.test\\\",\\\"dataPolicy\\\":{\\\"training\\\":false,\\\"trainingOpenRouter\\\":false,\\\"retainsPrompts\\\":true,\\\"canPublish\\\":false},\\\"headquarters\\\":\\\"US\\\"}" +
        "{\\\"displayName\\\":\\\"DeepInfra\\\",\\\"slug\\\":\\\"deepinfra\\\",\\\"baseUrl\\\":\\\"https://x.test\\\",\\\"dataPolicy\\\":{\\\"training\\\":false,\\\"trainingOpenRouter\\\":false,\\\"retainsPrompts\\\":false}}" +
        "{\\\"slug\\\":\\\"novita\\\",\\\"baseUrl\\\":\\\"\\\",\\\"dataPolicy\\\":{\\\"training\\\":true,\\\"trainingOpenRouter\\\":false,\\\"retainsPrompts\\\":true}}";

    // Endpoint objects as the page embeds them (quotes escaped), trimmed to the keys ParseImageInput reads.
    private const string ImageSample =
        "{\\\"model_variant_slug\\\":\\\"z-ai/glm-5.3-flash\\\",\\\"provider_slug\\\":\\\"relace/fp4\\\",\\\"features\\\":{\\\"supports_base64_image_input\\\":false,\\\"supports_image_urls\\\":false}}" +
        "{\\\"model_variant_slug\\\":\\\"z-ai/glm-5.3-flash\\\",\\\"provider_slug\\\":\\\"together\\\",\\\"features\\\":{\\\"supports_tool_choice\\\":{\\\"literal_none\\\":true}}}" +
        "{\\\"model_variant_slug\\\":\\\"z-ai/glm-5.3-flash\\\",\\\"provider_slug\\\":\\\"sail-research/fp4\\\",\\\"features\\\":{\\\"supports_multipart\\\":true,\\\"supports_base64_image_input\\\":false}}" +
        "{\\\"model_variant_slug\\\":\\\"z-ai/glm-5.3-flash:batch\\\",\\\"provider_slug\\\":\\\"sail-research/fp4\\\",\\\"features\\\":{\\\"supports_base64_image_input\\\":true}}";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body) Reply, Page = (HttpStatusCode.NotFound, ""); // Page answers the /providers web page
        public HttpRequestMessage? Seen;
        public string? PageOnly; // when set, only this page answers; any other /providers page has nothing in it
        public readonly List<HttpRequestMessage> All = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (All)
            {
                Seen = request;
                All.Add(request);
            }
            var (status, body) = request.RequestUri?.AbsolutePath.EndsWith("/providers", StringComparison.Ordinal) != true ? Reply
                : PageOnly is null || request.RequestUri.ToString() == PageOnly ? Page : (HttpStatusCode.OK, "<html>nothing</html>");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}

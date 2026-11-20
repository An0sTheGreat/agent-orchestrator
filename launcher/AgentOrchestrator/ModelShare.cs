using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// Sharing this PC's models with another PC (docs/superpowers/specs/2026-11-19-share-models-design.md): the pure parts.
// llama-server binds 127.0.0.1:ChatPort; ImageShare (ImagePort), which proxies /v1/ to it, binds the Tailscale IPv4 on the
// Tailscale network, or 127.0.0.1 behind one Cloudflare quick tunnel on the Cloudflare one. Keys live in ApiKeys, never in settings.json: OwnKeyEnv on the primary PC,
// KeyEnv on the PC that connects to it.
internal static class ModelShare
{
    public const int ChatPort = 8781, ImagePort = 8782;
    public const string OwnKeyEnv = "AO_SHARE_OWN_KEY", KeyEnv = "AO_SHARE_KEY";
    public const string Scheme = "ao-share://";
    public static string Folder => Path.Combine(AppSettings.DataRoot, "share");
    public static string IniPath => Path.Combine(Folder, "models.ini");

    public const string Tailscale = "tailscale", Cloudflare = "cloudflare"; // AppSettings.ShareNetwork

    // Tailscale's CGNAT range, 100.64.0.0/10.
    public static bool IsTailscale(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [100, var b, _, _] && (b & 0xC0) == 64;

    // The first Tailscale IPv4 among `addresses` (default: the addresses of the network interfaces that are up); null = none.
    public static IPAddress? TailscaleIp(IEnumerable<IPAddress>? addresses = null) =>
        (addresses ?? NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))).FirstOrDefault(IsTailscale);

    // The network sharing uses: the user's pick, else Tailscale when this PC has a Tailscale address, else Cloudflare.
    public static string Network(string picked, IEnumerable<IPAddress>? addresses = null) =>
        picked is Tailscale or Cloudflare ? picked : TailscaleIp(addresses) is null ? Cloudflare : Tailscale;

    // A host from a connection code that is a Tailscale IPv4, written canonically (no port, no leading zeros).
    public static bool IsTailscaleHost(string host) => IPAddress.TryParse(host, out var ip) && ip.ToString() == host && IsTailscale(ip);

    // The host as the other PC calls it: a Tailscale IP straight to ImageShare over plain HTTP (Tailscale encrypts the link),
    // a <name>.trycloudflare.com tunnel over HTTPS.
    public static string ChatUrl(string host) => ImagesUrl(host) + "/v1";
    public static string ImagesUrl(string host) => IsTailscaleHost(host) ? $"http://{host}:{ImagePort}" : $"https://{host}";

    // 32 random bytes, base64url without padding (43 characters).
    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Code(string host, string key) => $"{Scheme}{host}?key={key}";

    // ao-share://<name>.trycloudflare.com?key=<base64url> or ao-share://<Tailscale IPv4>?key=<base64url>; anything else is null
    // (no other IP or host, no port: the key only ever goes to a Cloudflare quick tunnel or a Tailscale address).
    public static (string Host, string Key)? ParseCode(string? code) =>
        Regex.Match(code?.Trim() ?? "", @"^ao-share://([A-Za-z0-9-]+\.trycloudflare\.com|[0-9.]{7,15})\?key=([A-Za-z0-9_-]+)$") is { Success: true } m &&
        (m.Groups[1].Value.EndsWith(".trycloudflare.com", StringComparison.Ordinal) || IsTailscaleHost(m.Groups[1].Value))
            ? (m.Groups[1].Value, m.Groups[2].Value) : null;

    // Load: this PC's own load settings for the model as llama-server preset keys (LoadOptions), so the router loads it as
    // this PC would (a MoE model's experts on the CPU, its context, its threads) instead of everything on the GPU.
    public sealed record SharedModel(string Id, string Model, string? Mmproj, IReadOnlyList<(string Key, string Value)>? Load = null);

    // auto_load keys -> llama-server long option names, as orchestrator.py llama_args passes them.
    private static readonly (string Key, string[] Names, Func<string, bool> Keep)[] LoadKeys =
    [
        ("max_seq_length", ["ctx-size"], v => int.TryParse(v, out var n) && n > 0),
        ("gpu_layers", ["n-gpu-layers"], v => int.TryParse(v, out var n) && n >= 0), // -1 (all) is llama-server's own default
        ("cache_type_kv", ["cache-type-k", "cache-type-v"], v => true),
        ("n_batch", ["batch-size"], v => int.TryParse(v, out var n) && n > 0),
        ("n_ubatch", ["ubatch-size"], v => int.TryParse(v, out var n) && n > 0),
        ("n_cpu_moe", ["n-cpu-moe"], v => int.TryParse(v, out var n) && n > 0),
        ("n_parallel", ["parallel"], v => int.TryParse(v, out var n) && n > 0),
        ("ctx_checkpoints", ["ctx-checkpoints"], v => int.TryParse(v, out var n) && n >= 0),
        ("cache_ram", ["cache-ram"], v => int.TryParse(v, out _)),
        ("load_mode", ["load-mode"], v => v is "none" or "mmap" or "mlock" or "mmap+mlock" or "dio"),
        ("tensor_split", ["tensor-split"], v => true),
        ("reasoning_budget", ["reasoning-budget"], v => int.TryParse(v, out var n) && n >= 0),
        ("speculative_type", ["spec-type"], v => v == "none" || v.StartsWith("draft-", StringComparison.Ordinal)), // the app's off/auto/mtp need the model: left out
    ];

    // The preset keys for saved load options (auto_load key -> value, a list comma-joined) and llama_extra_args: -t N and
    // --long value pairs become keys (they win, as in llama_args); anything else (short flags, bare switches) is skipped.
    public static List<(string Key, string Value)> LoadOptions(IReadOnlyDictionary<string, string> options, IReadOnlyList<string> extraArgs)
    {
        var ini = new List<(string Key, string Value)>();
        foreach (var (key, names, keep) in LoadKeys)
            if (options.GetValueOrDefault(key)?.Trim() is { Length: > 0 } value && keep(value))
                ini.AddRange(names.Select(n => (n, value)));
        for (var i = 0; i + 1 < extraArgs.Count; i++)
            if ((extraArgs[i] == "-t" ? "threads" : extraArgs[i].StartsWith("--", StringComparison.Ordinal) && extraArgs[i].Length > 2 ? extraArgs[i][2..] : null) is { } name &&
                (!extraArgs[i + 1].StartsWith('-') || double.TryParse(extraArgs[i + 1], out _)))
            {
                ini.RemoveAll(o => o.Key == name);
                ini.Add((name, extraArgs[++i]));
            }
        return ini;
    }

    // This PC's saved load options for a shared model as the Model Settings editors show them (auto_load keys, non-empty):
    // its own entry in overrides (POST /share/load, OverridesPath), else the auto_load of a worker in worker-config.json whose
    // gguf_file is that file (or whose model_path names it), else a model preset saved for that model, else none.
    public static Dictionary<string, string> SavedOptions(string file, string id, JsonNode? workerConfig, IEnumerable<ModelPreset> presets,
        IReadOnlyDictionary<string, Dictionary<string, string>>? overrides = null)
    {
        static Dictionary<string, string> Kept(IEnumerable<KeyValuePair<string, string>> options) =>
            options.Where(p => IsLoadKey(p.Key) && p.Value.Trim().Length > 0).ToDictionary();
        static IEnumerable<JsonObject> AutoLoads(JsonNode? node) => node switch
        {
            JsonObject o => (o["auto_load"] is JsonObject a ? [a] : Enumerable.Empty<JsonObject>()).Concat(o.Where(p => p.Key != "auto_load").SelectMany(p => AutoLoads(p.Value))),
            JsonArray list => list.SelectMany(AutoLoads),
            _ => [],
        };
        static string Text(JsonNode? v) => v is JsonArray list ? string.Join(",", list.Select(x => x?.ToString())) : v?.ToString() ?? "";
        if (overrides?.GetValueOrDefault(id) is { } own)
            return Kept(own);
        var name = Path.GetFileNameWithoutExtension(file);
        bool Same(string? path) => path is { Length: > 0 } && (path.Equals(file, StringComparison.OrdinalIgnoreCase) || path.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (AutoLoads(workerConfig).FirstOrDefault(a => Same(Text(a["gguf_file"])) || Same(Text(a["model_path"]))) is { } load)
            return Kept(ModelOptions.Read(new JsonObject { ["auto_load"] = load.DeepClone() }));
        return presets.FirstOrDefault(p => IsFor(p, id, file)) is { } preset ? Kept(preset.Options) : [];
    }

    private static bool IsLoadKey(string key) => ModelOptions.All.Any(o => o.Key == key && o.Target == "auto_load");

    // SavedOptions as llama-server preset keys (LoadOptions): a list's ", " joined with "," and llama_extra_args split as the
    // editor splits it ("quotes" keep a value with spaces whole; a JSON list, as older presets kept it, is read as one).
    public static List<(string Key, string Value)> SavedLoad(string file, string id, JsonNode? workerConfig, IEnumerable<ModelPreset> presets,
        IReadOnlyDictionary<string, Dictionary<string, string>>? overrides = null)
    {
        var options = SavedOptions(file, id, workerConfig, presets, overrides);
        var args = options.GetValueOrDefault("llama_extra_args")?.Trim() ?? "";
        return LoadOptions(options.ToDictionary(p => p.Key, p => Regex.Replace(p.Value.Trim(), @"\s*,\s*", ",")),
            args.StartsWith('[') ? JsonSerializer.Deserialize<List<string>>(args) ?? []
            : [.. Regex.Matches(args, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)]);
    }

    public static string OverridesPath => Path.Combine(Folder, "overrides.json");

    // <data>\share\overrides.json: shared model id -> its load options as saved from another PC; empty when missing or unreadable.
    public static Dictionary<string, Dictionary<string, string>> ReadOverrides()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(OverridesPath)) ?? []; }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private sealed record LoadReply(Dictionary<string, string> Options);

    // GET /share/load?model=<id>: {"options":{key:value}}, the model's SavedOptions.
    public static string LoadJson(IReadOnlyDictionary<string, string> options) => JsonSerializer.Serialize(new LoadReply(options.ToDictionary()), Web);
    public static Dictionary<string, string> ParseLoad(string json) => JsonSerializer.Deserialize<LoadReply>(json, Web)?.Options ?? [];

    // POST /share/load {"model":id,"options":{key:value}}: the options ("" removes one) over the model's saved ones (saved: its
    // SavedOptions, null = not a shared model) become its entry in overrides. 400 for a bad body, a key that is not a load option
    // or a bad value, 404 for an unknown model; overrides changes only on 200 {"ok":true}.
    public static ImageShare.Response SaveLoad(string body, Dictionary<string, Dictionary<string, string>> overrides, Func<string, Dictionary<string, string>?> saved)
    {
        JsonObject? asked;
        try { asked = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { asked = null; }
        if (asked?["model"] is not JsonValue model || !model.TryGetValue<string>(out var id) || asked["options"] is not JsonObject given)
            return ImageShare.Error(400, "The body is not {\"model\",\"options\"} JSON");
        var changes = new Dictionary<string, string>();
        foreach (var (key, value) in given)
        {
            if (!IsLoadKey(key))
                return ImageShare.Error(400, $"Not a load option: {key}");
            if (value is not JsonValue v || !v.TryGetValue<string>(out var text))
                return ImageShare.Error(400, $"{key}: the value is not a string");
            changes[key] = text;
        }
        if (saved(id) is not { } options)
            return ImageShare.Error(404, $"No such shared model: {id}");
        if (ModelOptions.Apply(new JsonObject { ["auto_load"] = new JsonObject { ["model_path"] = id } }, changes, includeLoad: true) is { Count: > 0 } errors)
            return ImageShare.Error(400, string.Join(" ", errors.Select(e => $"{e.Key}: {e.Value}")));
        foreach (var (key, text) in changes)
            if (text.Trim().Length == 0)
                options.Remove(key);
            else
                options[key] = text;
        overrides[id] = options;
        return new(200, """{"ok":true}""");
    }

    // A model preset saved for this shared model: by its id or its file name.
    private static bool IsFor(ModelPreset p, string id, string file) =>
        p.Model.Equals(id, StringComparison.OrdinalIgnoreCase) || p.Model.Equals(Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private sealed record PresetList(List<ModelPreset> Presets);

    // GET /share/presets: {"presets":[{"name","model","options","systemPrompt"}]}, the presets of the shared models (an any-model
    // preset too), each model named by its id in /v1/models, with request options only (the primary PC loads with its own).
    public static string PresetsJson(IEnumerable<ModelPreset> presets, IReadOnlyList<SharedModel> models) =>
        JsonSerializer.Serialize(new PresetList([.. presets
            .Select(p => (Preset: p, Id: p.Model.Length == 0 ? "" : models.FirstOrDefault(m => IsFor(p, m.Id, m.Model))?.Id))
            .Where(x => x.Id is not null)
            .Select(x => new ModelPreset
            {
                Name = x.Preset.Name, Model = x.Id!, SystemPrompt = x.Preset.SystemPrompt,
                Options = x.Preset.Options.Where(o => ModelOptions.All.Any(k => k.Key == o.Key && k.Target != "auto_load")).ToDictionary(),
            })]), Web);

    public static List<ModelPreset> ParsePresets(string json) => JsonSerializer.Deserialize<PresetList>(json, Web)?.Presets ?? [];

    // The primary PC's presets into this PC's, named "<name> (Primary PC)": one of the same name and model is replaced. The count copied.
    public static int MergePresets(List<ModelPreset> mine, IEnumerable<ModelPreset?> copied)
    {
        var count = 0;
        foreach (var p in copied)
        {
            if (p is not { Name.Length: > 0 })
                continue;
            var named = new ModelPreset { Name = $"{p.Name} (Primary PC)", Model = p.Model ?? "", Options = p.Options ?? [], SystemPrompt = p.SystemPrompt ?? "" };
            var same = mine.FindIndex(m => m.Name.Equals(named.Name, StringComparison.OrdinalIgnoreCase) && m.Model.Equals(named.Model, StringComparison.OrdinalIgnoreCase));
            if (same >= 0)
                mine[same] = named;
            else
                mine.Add(named);
            count++;
        }
        return count;
    }

    // llama-server's --models-preset file: one section per model, named by the id the other PC picks it by.
    public static string Ini(IEnumerable<SharedModel> models)
    {
        var text = new StringBuilder("version = 1\n");
        foreach (var m in models)
        {
            text.Append($"\n[{m.Id}]\nmodel = {m.Model}\n");
            if (m.Mmproj is { Length: > 0 } mmproj)
                text.Append($"mmproj = {mmproj}\n");
            foreach (var (key, value) in m.Load ?? [])
                text.Append($"{key} = {value}\n");
        }
        return text.ToString();
    }

    // The local chat GGUFs (FindLocalGgufs skips mmproj files): image models (no tokenizer) left out, each with its vision
    // projector, named as the model picker names them (MainWindow.FetchModelChoicesAsync).
    public static List<SharedModel> LocalModels(string modelsFolder, string hubCache, JsonNode? workerConfig, IEnumerable<ModelPreset> presets,
        IReadOnlyDictionary<string, Dictionary<string, string>>? overrides = null) =>
        [.. LauncherLogic.FindLocalGgufs(modelsFolder, hubCache)
            .Where(g => LauncherLogic.ReadGgufMetadata(g.File)?.ContainsKey("tokenizer.ggml.model") == true)
            .Select(g => new SharedModel(DisplayId(g), g.File, LauncherLogic.VisionProjector(g.File), SavedLoad(g.File, DisplayId(g), workerConfig, presets, overrides)))
            .DistinctBy(m => m.Id)];

    // The model picker's label for a local GGUF: "<repo name>  ·  <quant>" for a Hugging Face cache file, the file name otherwise.
    public static string DisplayId(LauncherLogic.LocalGguf g) =>
        g.Repo.Length > 0 ? $"{g.Repo.Split('/')[^1]}  ·  {(g.Variant.Length > 0 ? g.Variant : Path.GetFileName(g.File))}" : Path.GetFileNameWithoutExtension(g.File);

    public static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("ModelShare: " + what);
        }
        static IPAddress A(string s) => IPAddress.Parse(s);
        Check(TailscaleIp([A("192.168.1.5"), A("fd7a:115c:a1e0::1"), A("100.64.0.1"), A("100.100.1.2")])?.ToString() == "100.64.0.1", "the first Tailscale address");
        Check(TailscaleIp([A("100.127.255.254")]) is not null, "the top of 100.64.0.0/10");
        Check(TailscaleIp([A("192.168.1.5"), A("100.128.0.1"), A("100.63.255.255"), A("10.0.0.1")]) is null, "addresses outside 100.64.0.0/10");
        Check(Network("", [A("192.168.1.5"), A("100.80.211.78")]) == Tailscale && Network("", [A("192.168.1.5")]) == Cloudflare && Network("", []) == Cloudflare,
            "the default network with and without a Tailscale address");
        Check(Network(Cloudflare, [A("100.80.211.78")]) == Cloudflare && Network(Tailscale, []) == Tailscale, "the picked network wins");

        var key = NewKey();
        Check(key.Length == 43 && Regex.IsMatch(key, "^[A-Za-z0-9_-]+$") && key != NewKey(), "a new key");
        Check(ParseCode(" " + Code("seasonal-deck-sf.trycloudflare.com", key) + " ") == ("seasonal-deck-sf.trycloudflare.com", key), "the connection code round trip");
        Check(ParseCode("ao-share://abc-def.trycloudflare.com?key=K") == ("abc-def.trycloudflare.com", "K"), "a short key");
        Check(ParseCode("ao-share://100.101.102.103?key=K") == ("100.101.102.103", "K") && ParseCode(Code("100.64.0.1", key)) == ("100.64.0.1", key), "a Tailscale code");
        foreach (var bad in new[] { $"ao-share://100.128.0.1?key={key}", $"ao-share://100.63.255.255?key={key}", $"ao-share://192.168.1.5?key={key}",
                     $"ao-share://100.101.102.103:8782?key={key}", $"ao-share://100.064.0.1?key={key}", $"ao-share://100.64.1?key={key}", $"http://100.64.0.1?key={key}", $"ao-share://evil.com?key={key}", $"ao-share://x.trycloudflare.com.evil.com?key={key}",
                     $"ao-share://x.trycloudflare.com:443?key={key}", $"ao-share://x.y.trycloudflare.com?key={key}", $"ao-share://trycloudflare.com?key={key}",
                     $"https://x.trycloudflare.com?key={key}", "ao-share://x.trycloudflare.com", "ao-share://x.trycloudflare.com?key=", "ao-share://x.trycloudflare.com?key=a b", "", null })
            Check(ParseCode(bad) is null, $"a bad code was accepted: {bad}");

        Check(ChatUrl("a-b.trycloudflare.com") == "https://a-b.trycloudflare.com/v1" && ImagesUrl("a-b.trycloudflare.com") == "https://a-b.trycloudflare.com", "the tunnel URLs");
        Check(ChatUrl("100.101.102.103") == "http://100.101.102.103:8782/v1" && ImagesUrl("100.101.102.103") == "http://100.101.102.103:8782", "the Tailscale URLs");
        Check(Ini([new("Qwen3-GGUF  ·  Q4_K_M", @"C:\hub\qwen3-Q4_K_M.gguf", null), new("gemma-vision", @"D:\models\gemma.gguf", @"D:\models\mmproj-gemma.gguf")]) ==
            "version = 1\n\n[Qwen3-GGUF  ·  Q4_K_M]\nmodel = C:\\hub\\qwen3-Q4_K_M.gguf\n\n[gemma-vision]\nmodel = D:\\models\\gemma.gguf\nmmproj = D:\\models\\mmproj-gemma.gguf\n",
            "the models.ini contents");
        // a MoE model loads as this PC loads it: experts on the CPU, its context, KV cache type and threads
        var moe = SavedLoad(@"W:\m\Big-MoE.gguf", "Big-MoE", JsonNode.Parse("""
            {"roles":{"lead":{"auto_load":{"model_path":"Other","gguf_file":"W:\\m\\other.gguf","n_cpu_moe":5}}},
             "custom_workers":[{"auto_load":{"model_path":"Big-MoE","gguf_file":"W:\\m\\Big-MoE.gguf","max_seq_length":65536,"cache_type_kv":"q8_0",
               "n_cpu_moe":39,"gpu_layers":-1,"speculative_type":"off","tensor_split":[3,1],"llama_extra_args":["--flash-attn","on","-t","8","--no-mmap"]}}]}
            """), []);
        Check(Ini([new("Big-MoE", @"W:\m\Big-MoE.gguf", null, moe)]) ==
            "version = 1\n\n[Big-MoE]\nmodel = W:\\m\\Big-MoE.gguf\nctx-size = 65536\ncache-type-k = q8_0\ncache-type-v = q8_0\nn-cpu-moe = 39\n" +
            "tensor-split = 3,1\nflash-attn = on\nthreads = 8\n", "a shared model's own load options: " + string.Join("; ", moe));
        Check(SavedLoad(@"D:\x\Q.gguf", "Q  ·  Q4", null, [new() { Model = "Q  ·  Q4", Options = { ["n_cpu_moe"] = "12", ["llama_extra_args"] = "--threads 6" } }])
            .SequenceEqual([("n-cpu-moe", "12"), ("threads", "6")]) && SavedLoad(@"D:\x\Q.gguf", "Q", null, []).Count == 0, "load options from a model preset, and none");
        // /share/load: the options over the saved ones become this PC's own for that model, and SavedLoad takes them before the worker's
        var overrides = new Dictionary<string, Dictionary<string, string>>();
        var workers = JsonNode.Parse("""{"custom_workers":[{"auto_load":{"model_path":"Big-MoE","gguf_file":"W:\\m\\Big-MoE.gguf","n_cpu_moe":39,"max_seq_length":65536}}]}""");
        Dictionary<string, string>? Saved(string id) => id == "Big-MoE" ? SavedOptions(@"W:\m\Big-MoE.gguf", id, workers, [], overrides) : null;
        Check(LoadJson(Saved("Big-MoE")!) == """{"options":{"max_seq_length":"65536","n_cpu_moe":"39"}}""", "GET /share/load: " + LoadJson(Saved("Big-MoE")!));
        foreach (var (bad, status) in new[] { ("""{"model":"Big-MoE","options":{"temperature":"0.5"}}""", 400), ("""{"model":"Big-MoE","options":{"n_cpu_moe":"x"}}""", 400),
                     ("""{"model":"Big-MoE","options":{"n_cpu_moe":20}}""", 400), ("""{"model":"Other","options":{"n_cpu_moe":"20"}}""", 404), ("nope", 400), ("""{"model":"Big-MoE"}""", 400) })
            Check(SaveLoad(bad, overrides, Saved).Status == status && overrides.Count == 0, $"POST /share/load refused {bad} with {SaveLoad(bad, overrides, Saved).Status}, not {status}");
        var saved = SaveLoad("""{"model":"Big-MoE","options":{"n_cpu_moe":"20","cache_type_kv":"q8_0","max_seq_length":""}}""", overrides, Saved);
        Check(saved is { Status: 200, Json: """{"ok":true}""" }, "POST /share/load: " + saved.Json);
        var reloaded = SavedLoad(@"W:\m\Big-MoE.gguf", "Big-MoE", workers, [], overrides);
        Check(reloaded.SequenceEqual([("cache-type-k", "q8_0"), ("cache-type-v", "q8_0"), ("n-cpu-moe", "20")]), "the saved load options come first: " + string.Join("; ", reloaded));
        Check(ParseLoad(LoadJson(Saved("Big-MoE")!)) is { Count: 2 } back && back["n_cpu_moe"] == "20" && back["cache_type_kv"] == "q8_0", "the load JSON round trip");
        // /share/presets: shared models' presets (by id or file name) and any-model ones, named by the shared id, request options only
        SharedModel[] shared = [new("Qwen3-GGUF  ·  Q4_K_M", @"C:\hub\qwen3-Q4_K_M.gguf", null), new("gemma", @"D:\m\gemma.gguf", null)];
        var sent = ParsePresets(PresetsJson([
            new() { Name = "Coder", Model = "qwen3-Q4_K_M", Options = { ["temperature"] = "0.2", ["n_cpu_moe"] = "30", ["max_seq_length"] = "65536" }, SystemPrompt = "Be terse" },
            new() { Name = "Any", Options = { ["top_p"] = "0.9" } },
            new() { Name = "Other", Model = "Llama-70B", Options = { ["temperature"] = "1" } },
            new() { Name = "G", Model = "GEMMA" },
        ], shared));
        Check(sent.Select(p => $"{p.Name}|{p.Model}").SequenceEqual(["Coder|Qwen3-GGUF  ·  Q4_K_M", "Any|", "G|gemma"]), "the shared presets: " + string.Join("; ", sent.Select(p => p.Name + "|" + p.Model)));
        Check(sent[0].Options.Count == 1 && sent[0].Options["temperature"] == "0.2" && sent[0].SystemPrompt == "Be terse" && sent[1].Options["top_p"] == "0.9",
            "the shared presets' request options only: " + string.Join("; ", sent[0].Options));
        Check(PresetsJson([new() { Name = "Any" }], []) == """{"presets":[{"name":"Any","options":{},"systemPrompt":"","model":""}]}""", "the presets JSON: " + PresetsJson([new() { Name = "Any" }], []));
        List<ModelPreset> mine = [new() { Name = "Coder (Primary PC)", Model = "Qwen3-GGUF  ·  Q4_K_M", SystemPrompt = "old" }, new() { Name = "Coder", Model = "Qwen3-GGUF  ·  Q4_K_M" },
            new() { Name = "Coder (Primary PC)", Model = "gemma" }];
        Check(MergePresets(mine, sent) == 3 && mine.Select(p => $"{p.Name}|{p.Model}|{p.SystemPrompt}").SequenceEqual(["Coder (Primary PC)|Qwen3-GGUF  ·  Q4_K_M|Be terse",
            "Coder|Qwen3-GGUF  ·  Q4_K_M|", "Coder (Primary PC)|gemma|", "Any (Primary PC)||", "G (Primary PC)|gemma|"]),
            "the merge (rename, replace the same name and model, keep the others): " + string.Join("; ", mine.Select(p => p.Name + "|" + p.Model)));
        Check(DisplayId(new("unsloth/Qwen3-GGUF", "Q4_K_M", @"C:\x\q.gguf")) == "Qwen3-GGUF  ·  Q4_K_M" && DisplayId(new("", "", @"D:\m\My Model.gguf")) == "My Model",
            "display ids");
    }
}

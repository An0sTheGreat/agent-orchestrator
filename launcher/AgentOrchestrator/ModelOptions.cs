using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentOrchestrator;

// The per-worker Model settings window: one table drives its fields and their mapping into roles.<role> (or a custom
// worker's entry) in config.json. Load options are Unsloth Studio's LoadRequest; the Unsloth-only sampling keys go in
// sampling.extra_body, as config.json does with top_k.
internal static class ModelOptions
{
    public enum Kind { Int, Number, Text, Multiline, Choice, Bool, NumberList, IntList, StopList, ArgList }

    // Target: "auto_load", "sampling", "sampling.extra_body" or "role" (the role object itself). Default: what the window
    // fills in when the key is absent and the model's GGUF says nothing (null: left empty).
    public sealed record Option(string Key, string Target, Kind Kind, string Help, double Min = double.MinValue, double Max = double.MaxValue, string[]? Choices = null,
        string? Default = null)
    {
        public string Section => Target switch { "auto_load" => "Load", "role" => "Request", _ => "Sampling" };
        public string[] Values => Kind == Kind.Bool ? ["on", "off"] : Choices ?? [];
        public string Label => Labels.TryGetValue(Key, out var label) ? label : Key;
    }

    // What the window calls each option; the config key stays in the tooltip.
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["gguf_variant"] = "Quantization", ["max_seq_length"] = "Context length", ["cache_type_kv"] = "KV cache type", ["n_parallel"] = "Parallel slots",
        ["n_batch"] = "Batch size", ["n_ubatch"] = "Micro-batch size", ["ctx_checkpoints"] = "Context checkpoints", ["cache_ram"] = "Prompt cache (MiB)",
        ["load_mode"] = "Memory mapping", ["gpu_memory_mode"] = "GPU memory mode", ["gpu_layers"] = "GPU layers", ["n_cpu_moe"] = "MoE layers on CPU",
        ["tensor_split"] = "Tensor split", ["gpu_ids"] = "GPU IDs", ["tensor_parallel"] = "Tensor parallel", ["speculative_type"] = "Speculative decoding",
        ["spec_draft_n_max"] = "Draft tokens", ["spec_draft_cache_type"] = "Draft KV cache type", ["reasoning_budget"] = "Reasoning budget",
        ["reasoning_budget_message"] = "Budget message", ["disable_vision"] = "Disable vision", ["mmproj_offload"] = "Vision on GPU", ["cpu_fallback"] = "CPU fallback",
        ["chat_template_override"] = "Chat template override", ["llama_extra_args"] = "Extra llama-server arguments", ["temperature"] = "Temperature",
        ["top_p"] = "Top P", ["top_k"] = "Top K", ["min_p"] = "Min P", ["repetition_penalty"] = "Repetition penalty", ["presence_penalty"] = "Presence penalty",
        ["frequency_penalty"] = "Frequency penalty", ["max_tokens"] = "Max tokens", ["seed"] = "Seed", ["stop"] = "Stop strings",
        ["enable_thinking"] = "Thinking", ["reasoning_effort"] = "Reasoning effort", ["timeout_seconds"] = "Timeout (seconds)",
    };

    private static readonly string[] CacheTypes = ["f16", "bf16", "q8_0", "q4_0", "q4_1", "q5_0", "q5_1", "iq4_nl", "f32"];
    private const double Big = int.MaxValue;

    public static readonly Option[] All =
    [
        new("gguf_variant", "auto_load", Kind.Text, "Quantization of a Hugging Face GGUF repo, e.g. Q4_K_M"),
        new("max_seq_length", "auto_load", Kind.Int, "Context length; 0 = auto", 0, 1048576, Default: "32768"),
        new("cache_type_kv", "auto_load", Kind.Choice, "KV cache precision", Choices: CacheTypes, Default: "f16"),
        new("n_parallel", "auto_load", Kind.Int, "Parallel request slots, 1 to 64", 1, 64, Default: "1"),
        new("n_batch", "auto_load", Kind.Int, "Logical batch size", 1, 65536, Default: "2048"),
        new("n_ubatch", "auto_load", Kind.Int, "Physical batch size", 1, 65536, Default: "512"),
        new("ctx_checkpoints", "auto_load", Kind.Int, "Context checkpoints, 0 to 256", 0, 256, Default: "32"),
        new("cache_ram", "auto_load", Kind.Int, "Prompt cache in MiB; -1 = unlimited", -1, Big, Default: "8192"),
        new("load_mode", "auto_load", Kind.Choice, "How the weights are mapped into memory", Choices: ["auto", "none", "mmap", "mlock", "mmap+mlock", "dio"]),
        // ponytail: no prefilled default where the server's own default isn't verified: writing a guess would override it.
        new("gpu_memory_mode", "auto_load", Kind.Choice, "manual uses the GPU layer count", Choices: ["auto", "manual"]),
        new("gpu_layers", "auto_load", Kind.Int, "Layers on the GPU; -1 = fit", -1, Big),
        new("n_cpu_moe", "auto_load", Kind.Int, "MoE expert layers kept on the CPU", 0, Big, Default: "0"),
        new("tensor_split", "auto_load", Kind.NumberList, "Share per GPU, comma separated, e.g. 3,1"),
        new("gpu_ids", "auto_load", Kind.IntList, "GPUs to use, comma separated, e.g. 0,1"),
        new("tensor_parallel", "auto_load", Kind.Bool, "Split each layer across the GPUs"),
        new("speculative_type", "auto_load", Kind.Choice, "Speculative decoding", Choices: ["auto", "mtp", "dspark", "dflash", "ngram", "mtp+ngram", "off"]),
        new("spec_draft_n_max", "auto_load", Kind.Int, "Draft tokens per step, 1 to 16", 1, 16),
        new("spec_draft_cache_type", "auto_load", Kind.Choice, "Draft KV cache precision", Choices: CacheTypes),
        new("reasoning_budget", "auto_load", Kind.Int, "Thinking tokens; -1 = unlimited, 0 = off", -1, Big, Default: "-1"),
        new("reasoning_budget_message", "auto_load", Kind.Text, "Inserted when the budget runs out"),
        new("disable_vision", "auto_load", Kind.Bool, "Skip the vision projector"),
        new("mmproj_offload", "auto_load", Kind.Bool, "Run the vision projector on the GPU: faster images, about 1 GB more VRAM"),
        new("cpu_fallback", "auto_load", Kind.Bool, "Load on the CPU when the GPU is short of memory"),
        new("chat_template_override", "auto_load", Kind.Multiline, "Jinja chat template used instead of the model's own"),
        new("llama_extra_args", "auto_load", Kind.ArgList, "Extra llama-server arguments: one per line, or space separated"),
        // Sampling defaults are llama-server's own, so writing one changes nothing.
        new("temperature", "sampling", Kind.Number, "0 to 2", 0, 2, Default: "0.8"),
        new("top_p", "sampling", Kind.Number, "0 to 1", 0, 1, Default: "0.95"),
        new("top_k", "sampling.extra_body", Kind.Int, "-1 to 100", -1, 100, Default: "40"),
        new("min_p", "sampling.extra_body", Kind.Number, "0 to 1", 0, 1, Default: "0.05"),
        new("repetition_penalty", "sampling.extra_body", Kind.Number, "1 to 2", 1, 2, Default: "1.0"),
        new("presence_penalty", "sampling", Kind.Number, "-2 to 2", -2, 2, Default: "0"),
        new("frequency_penalty", "sampling", Kind.Number, "-2 to 2", -2, 2, Default: "0"),
        new("max_tokens", "sampling", Kind.Int, "Longest reply, in tokens; empty = no limit", 1, 1048576),
        new("seed", "sampling", Kind.Int, "Fixed seed for repeatable replies; -1 = random", -1, uint.MaxValue),
        new("stop", "sampling", Kind.StopList, "Stop strings: one per line, or comma separated"),
        new("enable_thinking", "sampling", Kind.Bool, "Reason before answering; off suits workers that edit files; empty = the model's own default"),
        new("reasoning_effort", "sampling", Kind.Choice, "For models that take an effort level", Choices: ["none", "minimal", "low", "medium", "high", "max", "xhigh"]),
        new("timeout_seconds", "role", Kind.Int, "Seconds to wait for a reply, 1 to 86400", 1, 86400),
    ];

    private static JsonObject? Container(JsonObject role, string target, bool create)
    {
        var node = role;
        foreach (var part in target == "role" ? [] : target.Split('.'))
        {
            if (node[part] is not JsonObject next)
            {
                if (!create)
                    return null;
                node[part] = next = new JsonObject();
            }
            node = next;
        }
        return node;
    }

    // Each option's current value as the window shows it ("" = key absent).
    public static Dictionary<string, string> Read(JsonObject role) => All.ToDictionary(o => o.Key, o =>
    {
        static string Plain(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";
        return Container(role, o.Target, false)?[o.Key] switch
        {
            null => "",
            JsonArray a => string.Join(o.Kind is Kind.StopList or Kind.ArgList ? "\n" : ", ", a.Select(Plain)),
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "on" : "off",
            var n => Plain(n),
        };
    });

    // The model's context window from its GGUF header values (<arch>.context_length), or null.
    public static long? ModelContext(IReadOnlyDictionary<string, string>? meta) =>
        meta?.GetValueOrDefault("general.architecture") is { } arch && meta.GetValueOrDefault(arch + ".context_length") is { } n &&
        long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    public const long ContextCap = 32768;

    // What each option shows while its key is absent: the model's own value (meta: its GGUF header values, null = unknown),
    // else the option's static default. Context length: the model's, capped at ContextCap. load false (no model to load):
    // the load options stay empty, as Apply would refuse them. inherited (a custom worker: what it inherits from the
    // Implementer, LauncherLogic.InheritedOptions) wins over both. Thinking is never filled: a template with a thinking
    // switch does not say which way the model defaults.
    public static Dictionary<string, string> Defaults(IReadOnlyDictionary<string, string>? meta, bool load, IReadOnlyDictionary<string, string>? inherited = null)
    {
        var defaults = All.Where(o => o.Default is not null && (load || o.Target != "auto_load")).ToDictionary(o => o.Key, o => o.Default!);
        if (load && ModelContext(meta) is { } context)
            defaults["max_seq_length"] = Math.Min(context, ContextCap).ToString(CultureInfo.InvariantCulture);
        foreach (var (gguf, key) in new[] { ("temp", "temperature"), ("top_p", "top_p"), ("top_k", "top_k"), ("min_p", "min_p"), ("penalty_repeat", "repetition_penalty") })
            if (meta?.GetValueOrDefault("general.sampling." + gguf) is { } value && Parse(All.First(o => o.Key == key), value).Error is null)
                defaults[key] = value;
        foreach (var o in All)
            if (inherited?.GetValueOrDefault(o.Key) is { Length: > 0 } value && (load || o.Target != "auto_load"))
                defaults[o.Key] = value;
        return defaults;
    }

    // What the window shows: each configured value, else its default ("" for none).
    public static Dictionary<string, string> Prefill(IReadOnlyDictionary<string, string> configured, IReadOnlyDictionary<string, string> defaults) =>
        All.ToDictionary(o => o.Key, o => configured.GetValueOrDefault(o.Key, "") is { Length: > 0 } value ? value : defaults.GetValueOrDefault(o.Key, ""));

    private static string Range(Option o) => o.Max >= Big
        ? $"{o.Min.ToString(CultureInfo.InvariantCulture)} or more"
        : $"{o.Min.ToString(CultureInfo.InvariantCulture)} to {o.Max.ToString(CultureInfo.InvariantCulture)}";

    // The JSON for a non-empty field (null: a list with no items, i.e. remove), or an error message.
    private static (JsonNode? Value, string? Error) Parse(Option o, string text)
    {
        var t = text.Trim();
        bool Whole(string s, out long v) => long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v) && v >= o.Min && v <= o.Max;
        bool Real(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && v >= o.Min && v <= o.Max;
        JsonNode? List(IEnumerable<JsonNode?> items) => items.ToArray() is { Length: > 0 } a ? new JsonArray(a) : null;
        string[] Commas() => t.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        switch (o.Kind)
        {
            case Kind.Int:
                return Whole(t, out var l) ? (JsonValue.Create(l), null) : (null, $"A whole number, {Range(o)}.");
            case Kind.Number:
                return Real(t, out var d) ? (JsonValue.Create(d), null) : (null, $"A number, {Range(o)}.");
            case Kind.Text:
                return (JsonValue.Create(t), null);
            case Kind.Multiline:
                return (JsonValue.Create(text), null);
            case Kind.Choice or Kind.Bool:
                if (!o.Values.Contains(t))
                    return (null, $"One of {string.Join(", ", o.Values)}.");
                return (o.Kind == Kind.Bool ? JsonValue.Create(t == "on") : JsonValue.Create(t), null);
            case Kind.NumberList:
                return Commas().All(s => Real(s, out _))
                    ? (List(Commas().Select(s => (JsonNode?)JsonValue.Create(double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)))), null)
                    : (null, "Numbers separated by commas, e.g. 3,1.");
            case Kind.IntList:
                return Commas().All(s => Whole(s, out var g) && g >= 0)
                    ? (List(Commas().Select(s => (JsonNode?)JsonValue.Create(long.Parse(s, CultureInfo.InvariantCulture)))), null)
                    : (null, "Whole numbers separated by commas, e.g. 0,1.");
            case Kind.StopList:
                var stops = text.Contains('\n') ? text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries) : Commas();
                return (List(stops.Select(s => (JsonNode?)JsonValue.Create(s))), null);
            default: // ArgList: one token per line, else split on spaces
                var args = text.Contains('\n')
                    ? text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    : t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return (List(args.Select(s => (JsonNode?)JsonValue.Create(s))), null);
        }
    }

    // Applies the fields to a role object: a field present but empty removes its key, a field absent leaves it, keys
    // not in the table stay. Every field is checked first; on any error (key -> message, "" for the role as a whole)
    // nothing changes. includeLoad false (a remote role) skips the auto_load options.
    public static Dictionary<string, string> Apply(JsonObject role, IDictionary<string, string> fields, bool includeLoad)
    {
        var errors = new Dictionary<string, string>();
        var changes = new List<(Option Option, JsonNode? Value)>();
        foreach (var o in All)
        {
            if (!fields.TryGetValue(o.Key, out var text) || !includeLoad && o.Target == "auto_load")
                continue;
            if (text.Trim().Length == 0)
            {
                changes.Add((o, null));
                continue;
            }
            var (value, error) = Parse(o, text);
            if (error is not null)
                errors[o.Key] = error;
            else
                changes.Add((o, value));
        }
        if (changes.Any(c => c.Value is not null && c.Option.Target == "auto_load") && role["auto_load"]?["model_path"] is null)
            errors[""] = "This worker has no model to load (auto_load.model_path in config.json), so load options cannot be set. Pick a model first or leave them empty.";
        if (errors.Count > 0)
            return errors;
        foreach (var (o, value) in changes)
            if (value is null)
                Container(role, o.Target, false)?.Remove(o.Key);
            else
                Container(role, o.Target, true)![o.Key] = value;
        return errors;
    }

    // A custom worker's own options (non-empty only), with the six fields older versions kept separately folded in.
    // Whatever it leaves out, the worker inherits from the Implementer.
    public static Dictionary<string, string> Effective(WorkerSetting w)
    {
        var options = new Dictionary<string, string>();
        foreach (var (key, value) in new[] { ("temperature", w.Temperature), ("max_tokens", w.MaxTokens), ("repetition_penalty", w.RepetitionPenalty),
                     ("reasoning_budget", w.ReasoningBudget), ("max_seq_length", w.ContextLength) })
            if (value.Trim().Length > 0)
                options[key] = value.Trim();
        if (w.Thinking is "On" or "Off")
            options["enable_thinking"] = w.Thinking.ToLowerInvariant();
        foreach (var (key, value) in w.ModelOptions)
            if (value.Trim().Length > 0)
                options[key] = value;
            else
                options.Remove(key);
        return options;
    }

    public static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Model settings: " + what);
        }
        static JsonObject Role() => JsonNode.Parse("""
            {"display_name":"x","custom":1,"auto_load":{"model_path":"m","gguf_variant":"Q4","n_batch":512},
             "sampling":{"temperature":0.3,"top_p":0.9,"extra_body":{"top_k":40,"other":1}}}
            """)!.AsObject();
        string J(JsonNode? n) => n?.ToJsonString() ?? "absent";

        var role = Role();
        var errors = Apply(role, new Dictionary<string, string>
        {
            ["temperature"] = "0.7", ["top_p"] = "", ["top_k"] = "25", ["min_p"] = "0.05", ["n_batch"] = " ", ["tensor_split"] = "3, 1.5", ["gpu_ids"] = "0,1",
            ["stop"] = "a, b", ["llama_extra_args"] = "--foo 1  --bar", ["tensor_parallel"] = "on", ["timeout_seconds"] = "90", ["cache_type_kv"] = "q8_0",
        }, includeLoad: true);
        var s = role["sampling"]!;
        var load = role["auto_load"]!;
        Check(errors.Count == 0, "valid fields were refused: " + string.Join(" ", errors));
        Check(J(s["temperature"]) == "0.7" && s["top_p"] is null && J(s["extra_body"]) == """{"top_k":25,"other":1,"min_p":0.05}""" && s["min_p"] is null, "sampling / extra_body: " + J(s));
        Check(load["n_batch"] is null && (string?)load["gguf_variant"] == "Q4" && (string?)load["model_path"] == "m", "empty removes, absent untouched: " + J(load));
        Check(J(load["tensor_split"]) == "[3,1.5]" && J(load["gpu_ids"]) == "[0,1]" && J(s["stop"]) == """["a","b"]""" &&
              J(load["llama_extra_args"]) == """["--foo","1","--bar"]""" && J(load["tensor_parallel"]) == "true" && (string?)load["cache_type_kv"] == "q8_0", "lists and choices: " + J(role));
        Check(J(role["timeout_seconds"]) == "90" && (string?)role["display_name"] == "x" && J(role["custom"]) == "1", "role keys: " + J(role));
        var shown = Read(role);
        Check(shown["tensor_split"] == "3, 1.5" && shown["stop"] == "a\nb" && shown["tensor_parallel"] == "on" && shown["top_k"] == "25" && shown["top_p"] == "", "read back: " + string.Join(" ", shown));

        role = Role();
        Apply(role, new Dictionary<string, string> { ["stop"] = "x\ny, z\n", ["llama_extra_args"] = "--chat-template-file C:\\a b.jinja\n-fa\n" }, true);
        Check(J(role["sampling"]!["stop"]) == """["x","y, z"]""" && J(role["auto_load"]!["llama_extra_args"]) == """["--chat-template-file C:\\a b.jinja","-fa"]""", "line lists: " + J(role));

        role = Role();
        var before = J(role);
        errors = Apply(role, new Dictionary<string, string>
            { ["top_p"] = "0.5", ["temperature"] = "5", ["gpu_ids"] = "0,x", ["cache_type_kv"] = "q9", ["n_parallel"] = "0", ["enable_thinking"] = "maybe", ["max_seq_length"] = "1.5" }, true);
        Check(errors.Keys.Order().SequenceEqual(["cache_type_kv", "enable_thinking", "gpu_ids", "max_seq_length", "n_parallel", "temperature"]), "range errors: " + string.Join(" ", errors.Keys));
        Check(J(role) == before, "an error changed the role: " + J(role));

        var noModel = JsonNode.Parse("""{"sampling":{}}""")!.AsObject();
        Check(Apply(noModel, new Dictionary<string, string> { ["max_seq_length"] = "4096", ["temperature"] = "0.5" }, true).ContainsKey("") && J(noModel) == """{"sampling":{}}""",
            "load options without auto_load.model_path must be refused: " + J(noModel));
        Check(Apply(noModel, new Dictionary<string, string> { ["max_seq_length"] = "", ["n_batch"] = "64" }, false).Count == 0 && J(noModel) == """{"sampling":{}}""",
            "empty load fields, and load fields of a remote role, must not create auto_load: " + J(noModel));

        // Defaults: configured > the model's GGUF values > static; the context capped; unknown metadata -> static only.
        var unknown = Defaults(null, load: true);
        Check(unknown["max_seq_length"] == "32768" && unknown["n_batch"] == "2048" && unknown["temperature"] == "0.8" && unknown["top_k"] == "40" && unknown["min_p"] == "0.05" &&
              !unknown.ContainsKey("enable_thinking") && !unknown.ContainsKey("max_tokens") &&
              !unknown.ContainsKey("gguf_variant") && !unknown.ContainsKey("seed") && !unknown.ContainsKey("timeout_seconds"), "static defaults: " + string.Join(" ", unknown));
        Check(!Defaults(null, load: false).ContainsKey("n_batch") && Defaults(null, false)["top_p"] == "0.95", "no model: load options stay empty");
        var meta = new Dictionary<string, string>
        {
            ["general.architecture"] = "qwen3", ["qwen3.context_length"] = "262144", ["llama.context_length"] = "4096", ["general.sampling.temp"] = "0.7",
            ["general.sampling.top_k"] = "40", ["general.sampling.min_p"] = "5", ["tokenizer.chat_template"] = "{% if enable_thinking %}<think>{% endif %}",
        };
        var derived = Defaults(meta, load: true);
        Check(ModelContext(meta) == 262144 && derived["max_seq_length"] == "32768" && derived["temperature"] == "0.7" && derived["top_k"] == "40" &&
              derived["min_p"] == "0.05" && !derived.ContainsKey("enable_thinking") && derived["n_batch"] == "2048", "model defaults: " + string.Join(" ", derived));
        // A custom worker: the Implementer's values over the model's; its load values only with a model to load.
        var inherited = new Dictionary<string, string> { ["temperature"] = "0.33", ["n_batch"] = "64", ["top_p"] = "" };
        var custom = Defaults(meta, load: true, inherited);
        Check(custom["temperature"] == "0.33" && custom["n_batch"] == "64" && custom["top_p"] == "0.95" && custom["top_k"] == "40" &&
              !Defaults(meta, load: false, inherited).ContainsKey("n_batch"), "inherited defaults: " + string.Join(" ", custom));
        meta["qwen3.context_length"] = "8192";
        meta["tokenizer.chat_template"] = "plain";
        derived = Defaults(meta, load: true);
        Check(derived["max_seq_length"] == "8192" && !derived.ContainsKey("enable_thinking"), "small context, plain template: " + string.Join(" ", derived));
        var prefill = Prefill(new Dictionary<string, string> { ["temperature"] = "0.3", ["top_p"] = "", ["max_seq_length"] = "16384" }, derived);
        Check(prefill["temperature"] == "0.3" && prefill["top_p"] == "0.95" && prefill["top_k"] == "40" && prefill["max_seq_length"] == "16384" && prefill["seed"] == "" &&
              prefill.Count == All.Length, "prefill: " + string.Join(" ", prefill));

        var worker = new WorkerSetting { Temperature = "0.4", Thinking = "Off", ContextLength = "8192", ModelOptions = { ["top_k"] = "5", ["max_seq_length"] = "" } };
        var copy = worker.Clone();
        copy.ModelOptions["top_k"] = "6";
        Check(string.Join(",", Effective(worker).OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")) == "enable_thinking=off,temperature=0.4,top_k=5",
            "custom worker options: " + string.Join(",", Effective(worker)));
    }
}

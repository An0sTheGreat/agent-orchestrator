using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentOrchestrator;

// The per-worker Model settings window: one table drives its fields and their mapping into roles.<role> (or a custom
// worker's entry) in config.json. Load options are the engine's auto_load keys for llama-server; the extra sampling keys go in
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
        ["enable_thinking"] = "Thinking", ["reasoning_effort"] = "Reasoning effort", ["timeout_seconds"] = "Timeout (seconds)", ["max_tool_call_chars"] = "Max tool call (characters)",
    };

    // What each option does, for its hover tip in the worker settings (about 20 words; the tip adds the default shown).
    public static readonly Dictionary<string, string> Descriptions = new()
    {
        ["gguf_variant"] = "The quantization of the model file. Lower bit counts use less memory and run faster, at some loss in quality.",
        ["max_seq_length"] = "How many tokens the model can hold at once: the conversation, files and tool results. Larger needs more VRAM.",
        ["cache_type_kv"] = "Precision of the memory that holds the conversation. q8_0 halves its size with little loss; q4 saves more, costs quality.",
        ["n_parallel"] = "How many requests the server answers at the same time. Each slot shares the context, so keep 1 for one chat.",
        ["n_batch"] = "How many prompt tokens are read per step. Larger reads long prompts faster but needs more memory while reading.",
        ["n_ubatch"] = "The batch actually sent to the GPU at once. Raising it speeds prompt reading on big GPUs, using more VRAM.",
        ["ctx_checkpoints"] = "Saved points in the context that let the server reuse earlier work when a conversation branches or is edited.",
        ["cache_ram"] = "System memory, in MiB, for cached prompts, so a repeated beginning of a conversation is not read again.",
        ["load_mode"] = "How the model file is loaded into memory: mapped from disk, locked in RAM, or read directly. Auto usually suits.",
        ["gpu_memory_mode"] = "Auto lets the server decide how much of the model goes on the GPU; manual uses the GPU layer count below.",
        ["gpu_layers"] = "How many of the model's layers run on the GPU; the rest run on the CPU, much slower. -1 fits as many as possible.",
        ["n_cpu_moe"] = "For mixture-of-experts models: expert layers kept on the CPU, freeing VRAM for context at some cost in speed.",
        ["tensor_split"] = "With several GPUs: how much of the model each one holds, as shares, for example 3,1 for a three-to-one split.",
        ["gpu_ids"] = "Which GPUs the model may use, by number. Empty uses every GPU the server can see.",
        ["tensor_parallel"] = "With several GPUs: splits every layer across them instead of giving each GPU whole layers. Can be faster on fast links.",
        ["speculative_type"] = "Drafts several tokens ahead and checks them in one step, so replies come out faster with the same result.",
        ["spec_draft_n_max"] = "How many tokens speculative decoding drafts each step. More can be faster when drafts are often right.",
        ["spec_draft_cache_type"] = "Precision of the draft model's conversation memory. Lower precision saves VRAM with little effect on the drafts.",
        ["reasoning_budget"] = "The most tokens a thinking model may spend reasoning before it must answer. -1 is unlimited, 0 turns thinking off.",
        ["reasoning_budget_message"] = "Text inserted when the thinking budget runs out, to tell the model to stop reasoning and answer now.",
        ["disable_vision"] = "Skips loading the vision projector, saving VRAM. The model can then no longer see images or screenshots.",
        ["mmproj_offload"] = "Runs the vision projector on the GPU: images are read faster, at about 1 GB more VRAM.",
        ["cpu_fallback"] = "Loads the model on the CPU when the GPU lacks memory, instead of failing. Works, but replies are much slower.",
        ["chat_template_override"] = "A Jinja chat template used instead of the one in the model file. For models whose own template is broken.",
        ["llama_extra_args"] = "Extra command-line arguments for llama-server, for options this page does not list. One per line or space separated.",
        ["temperature"] = "Randomness of the reply. Lower is focused and repeatable, higher is more varied and creative; coding suits low values.",
        ["top_p"] = "Samples only from the most likely tokens whose chances add up to this share. Lower values make replies more focused.",
        ["top_k"] = "Samples only from this many of the most likely next tokens. Lower is more focused; -1 or 0 turns it off.",
        ["min_p"] = "Drops tokens less likely than this share of the top token's chance, trimming unlikely words while keeping variety.",
        ["repetition_penalty"] = "Makes tokens already used less likely, against loops and repeated phrases. 1.0 is off; small raises help most.",
        ["presence_penalty"] = "Discourages any token that has already appeared, nudging the model toward new topics and words. 0 is off.",
        ["frequency_penalty"] = "Discourages tokens in proportion to how often they appeared, against repeating the same words. 0 is off.",
        ["max_tokens"] = "The longest reply allowed, in tokens, thinking included. Empty leaves it to the server and the context left.",
        ["seed"] = "A fixed random seed makes the same prompt give the same reply. -1 picks a new seed each time.",
        ["stop"] = "Text that ends the reply as soon as the model writes it. Usually empty; the model's template already sets these.",
        ["enable_thinking"] = "Lets a thinking model reason before answering. Better answers, slower replies; empty uses the model's own default.",
        ["reasoning_effort"] = "How hard a model that takes an effort level thinks: low is quick, high is thorough and slower.",
        ["timeout_seconds"] = "How long the orchestrator waits for this worker's reply before it gives up, in seconds.",
        ["max_tool_call_chars"] = "The longest tool call (such as a file write) the model may send, in characters. Past it the call is cut off and the model is told to send the work in smaller pieces. Stops a model looping inside one call. About 60000 suits a small model; empty or 0 = no limit.",
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
        new("max_tool_call_chars", "role", Kind.Int, "Longest tool call, in characters; empty or 0 = no limit", 0, Big),
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
            ["stop"] = "a, b", ["llama_extra_args"] = "--foo 1  --bar", ["tensor_parallel"] = "on", ["timeout_seconds"] = "90", ["max_tool_call_chars"] = "60000", ["cache_type_kv"] = "q8_0",
        }, includeLoad: true);
        var s = role["sampling"]!;
        var load = role["auto_load"]!;
        Check(errors.Count == 0, "valid fields were refused: " + string.Join(" ", errors));
        Check(J(s["temperature"]) == "0.7" && s["top_p"] is null && J(s["extra_body"]) == """{"top_k":25,"other":1,"min_p":0.05}""" && s["min_p"] is null, "sampling / extra_body: " + J(s));
        Check(load["n_batch"] is null && (string?)load["gguf_variant"] == "Q4" && (string?)load["model_path"] == "m", "empty removes, absent untouched: " + J(load));
        Check(J(load["tensor_split"]) == "[3,1.5]" && J(load["gpu_ids"]) == "[0,1]" && J(s["stop"]) == """["a","b"]""" &&
              J(load["llama_extra_args"]) == """["--foo","1","--bar"]""" && J(load["tensor_parallel"]) == "true" && (string?)load["cache_type_kv"] == "q8_0", "lists and choices: " + J(role));
        Check(J(role["max_tool_call_chars"]) == "60000", "the tool call limit is a role key: " + J(role));
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

using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

internal static class LauncherLogic
{
    public static readonly string[] ProjectFiles =
    [
        "PROJECT.md", "STATE.md", "TASK.md", "RESULT.md", "REVIEW.md", "ISSUES.md", "config.json"
    ];

    public static string? FindWorkflowRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "orchestrator.py")) &&
                    File.Exists(Path.Combine(directory.FullName, "config.json")))
                    return directory.FullName;
            }
        }

        return null;
    }

    public static ProcessStartInfo BuildStartInfo(string python, string workflowRoot, string projectRoot, string apiKey, bool once)
    {
        var info = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = workflowRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(Path.Combine(workflowRoot, "orchestrator.py"));
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(projectRoot);
        info.ArgumentList.Add("--non-interactive");
        info.ArgumentList.Add("--events");
        if (once)
            info.ArgumentList.Add("--once");
        info.Environment["UNSLOTH_API_KEY"] = apiKey;
        info.Environment["PYTHONUNBUFFERED"] = "1";
        return info;
    }

    public sealed record ModelInfo(string Name, string Weights, string Quant);

    // Display-only parsing of a model id or GGUF path; ggufVariant (from config.json) wins when present.
    public static ModelInfo DescribeModel(string id, string? ggufVariant = null)
    {
        var tail = id.Contains('\\') ? Path.GetFileNameWithoutExtension(id) : id.Split('/')[^1].Split(':')[0];
        var quant = ggufVariant;
        if (string.IsNullOrWhiteSpace(quant))
        {
            var q = Regex.Match(id, @"(?<![A-Za-z0-9])((?:UD-)?I?Q\d(?:_[A-Z0-9]+)*)(?![A-Za-z0-9])");
            quant = q.Success ? q.Groups[1].Value : "";
        }
        var name = Regex.Replace(tail, @"[-_ ]GGUF$", "", RegexOptions.IgnoreCase);
        if (quant.Length > 0)
            name = Regex.Replace(name, @"\s*[｜|]?\s*" + Regex.Escape(quant) + "$", "");
        var w = Regex.Match(name, @"(?<![A-Za-z0-9.])(\d+(?:\.\d+)?)B(?:[-_ ]A(\d+(?:\.\d+)?)B)?(?![A-Za-z])");
        var weights = !w.Success ? "—" : w.Groups[2].Success ? $"{w.Groups[1].Value}B · {w.Groups[2].Value}B active" : $"{w.Groups[1].Value}B";
        return new ModelInfo(name.Trim(), weights, quant.Length == 0 ? "—" : quant);
    }

    // "Task X": the active task in TASK.md, else the most recently passed task in STATE.md.
    public static string? CurrentTaskId(string projectRoot)
    {
        try
        {
            var task = Path.Combine(projectRoot, "TASK.md");
            if (File.Exists(task))
            {
                var m = Regex.Match(File.ReadAllText(task), @"^# (.+)$", RegexOptions.Multiline);
                if (m.Success && m.Groups[1].Value.Trim() != "Active task")
                    return m.Groups[1].Value.Trim();
            }
            var state = Path.Combine(projectRoot, "STATE.md");
            if (File.Exists(state))
            {
                var passed = Regex.Matches(File.ReadAllText(state), @"^## Passed: (.+)$", RegexOptions.Multiline);
                if (passed.Count > 0)
                    return passed[^1].Groups[1].Value.Trim();
            }
        }
        catch (IOException) { }
        return null;
    }

    public static string PrimaryLabel(string projectRoot) =>
        CurrentTaskId(projectRoot) is { } id ? $"Resume Project — {id}" : "Start Task";

    public const string EventPrefix = "@@AO ";

    // Structured events from orchestrator.py --events; null for ordinary log lines.
    public static JsonElement? TryParseEvent(string line)
    {
        if (!line.StartsWith(EventPrefix, StringComparison.Ordinal))
            return null;
        try { return JsonDocument.Parse(line[EventPrefix.Length..]).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    public static string[] MissingProjectFiles(string projectRoot) =>
        ProjectFiles.Where(name => !File.Exists(Path.Combine(projectRoot, name))).ToArray();

    public static (string Key, string Path)? FindApiKeyFile(string workflowRoot, string executableDirectory)
    {
        foreach (var path in new[]
        {
            Path.Combine(executableDirectory, "api_key.txt"),
            Path.Combine(workflowRoot, "api_key.txt"),
        }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;
            var lines = File.ReadAllLines(path);
            if (lines.Length != 1 || string.IsNullOrWhiteSpace(lines[0]))
                throw new InvalidDataException($"{path} must contain exactly one nonblank line.");
            return (lines[0].Trim(), path);
        }
        return null;
    }

    public static string[] InitializeProject(
        string workflowRoot, string projectRoot, string? customProjectFile = null, string? customConfigFile = null)
    {
        Directory.CreateDirectory(projectRoot);
        var copied = new List<string>();
        foreach (var name in MissingProjectFiles(projectRoot))
        {
            var source = name switch
            {
                "PROJECT.md" when customProjectFile is not null => customProjectFile,
                "config.json" when customConfigFile is not null => customConfigFile,
                _ => Path.Combine(workflowRoot, name),
            };
            File.Copy(source, Path.Combine(projectRoot, name));
            copied.Add(name);
        }
        return copied.ToArray();
    }

    public static void ValidateConfigFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("roles", out var roles) ||
            roles.ValueKind != JsonValueKind.Object ||
            new[] { "planner", "implementer", "reviewer" }.Any(role => !roles.TryGetProperty(role, out _)))
            throw new InvalidDataException("Custom config.json must define planner, implementer, and reviewer roles.");
    }

    public static string FindPython()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "python.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Python was not found in PATH. Install Python 3.11 or newer and enable Add Python to PATH.");
    }

    // Generation parameters of a built-in role, as the text shown in the settings editor ("" = key absent).
    public sealed record RoleSettings(string Temperature, string MaxTokens, string RepetitionPenalty, string ReasoningBudget, string ContextLength);

    public static RoleSettings ReadRoleSettings(string configPath, string role)
    {
        var settings = JsonNode.Parse(File.ReadAllText(configPath))!["roles"]![role]!;
        static string Text(JsonNode? node) => node is JsonValue ? node.ToJsonString() : "";
        return new RoleSettings(
            Text(settings["sampling"]?["temperature"]), Text(settings["sampling"]?["max_tokens"]),
            Text(settings["sampling"]?["extra_body"]?["repetition_penalty"]),
            Text(settings["auto_load"]?["reasoning_budget"]), Text(settings["auto_load"]?["max_seq_length"]));
    }

    // Validates every field, then writes; returns an error message (nothing written) or null on success.
    // An empty repetition penalty removes the key; the other empty fields leave their key as it is.
    public static string? WriteRoleSettings(string configPath, string role, RoleSettings values)
    {
        static bool Number(string text, double min, double max, out double result) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result) && result >= min && result <= max;
        static bool Whole(string text, long min, long max, out long result) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= min && result <= max;

        double temperature = 0, penalty = 0;
        long maxTokens = 0, budget = 0, context = 0;
        var temperatureText = values.Temperature.Trim();
        var maxTokensText = values.MaxTokens.Trim();
        var penaltyText = values.RepetitionPenalty.Trim();
        var budgetText = values.ReasoningBudget.Trim();
        var contextText = values.ContextLength.Trim();
        if (temperatureText.Length > 0 && !Number(temperatureText, 0, 2, out temperature))
            return "Temperature must be a number from 0 to 2.";
        if (maxTokensText.Length > 0 && !Whole(maxTokensText, 1, 262144, out maxTokens))
            return "Max tokens must be a whole number from 1 to 262144.";
        if (penaltyText.Length > 0 && !Number(penaltyText, 1, 2, out penalty))
            return "Repetition penalty must be a number from 1.0 to 2.0 (or empty).";
        if (budgetText.Length > 0 && !Whole(budgetText, 0, int.MaxValue, out budget))
            return "Reasoning budget must be a whole number, 0 or more.";
        if (contextText.Length > 0 && !Whole(contextText, 512, 262144, out context))
            return "Context length must be a whole number from 512 to 262144.";

        var root = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        var settings = root["roles"]![role]!.AsObject();
        var sampling = (settings["sampling"] ??= new JsonObject()).AsObject();
        var load = settings["auto_load"] is JsonObject { } existing && existing["model_path"] is not null ? existing : null;
        if ((budgetText.Length > 0 || contextText.Length > 0) && load is null)
            return "This role has no auto_load model_path in config.json, so reasoning budget and context length cannot be set. Assign a model first or leave those fields empty.";
        if (temperatureText.Length > 0)
            sampling["temperature"] = temperature;
        if (maxTokensText.Length > 0)
            sampling["max_tokens"] = maxTokens;
        if (penaltyText.Length == 0)
            sampling["extra_body"]?.AsObject().Remove("repetition_penalty");
        else
            (sampling["extra_body"] ??= new JsonObject()).AsObject()["repetition_penalty"] = penalty;
        if (budgetText.Length > 0)
            load!["reasoning_budget"] = budget;
        if (contextText.Length > 0)
            load!["max_seq_length"] = context;
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return null;
    }

    // True when the node, or any ancestor, is a button. A Run inside a TextBlock is not a Visual and
    // VisualTreeHelper.GetParent throws for it, so non-visuals (and visuals not yet in a visual tree) use the logical tree.
    public static bool IsInsideButton(System.Windows.DependencyObject? source)
    {
        for (; source is not null; source = (source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(source) : null) ?? System.Windows.LogicalTreeHelper.GetParent(source))
            if (source is System.Windows.Controls.Primitives.ButtonBase)
                return true;
        return false;
    }

    // The orchestrator is killed without cleanup, so leave a note on how the next run continues.
    public static string RecordStop(string projectRoot, DateTime when, string activity)
    {
        string? id = null, phase = null;
        var round = 0;
        try
        {
            using var checkpoint = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, ".ao_checkpoint.json")));
            var data = checkpoint.RootElement;
            id = data.GetProperty("task").GetProperty("id").ToString();
            phase = data.GetProperty("phase").GetString();
            round = data.GetProperty("round").GetInt32();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                              or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            id = null;
        }

        var next = string.IsNullOrWhiteSpace(id)
            ? "No task was in progress; the next run plans a new task."
            : $"The next run resumes task {id} ({phase}, review round {round + 1}).";
        var handoff = Path.Combine(projectRoot, "HANDOFF.md");
        var existing = File.Exists(handoff) ? File.ReadAllText(handoff) : "";
        var lead = existing.Length == 0 ? "" : existing.EndsWith('\n') ? "\n" : "\n\n";
        File.AppendAllText(handoff, $"{lead}## Stopped by user\n" +
            $"- Time: {when.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\n" +
            $"- Activity: {(string.IsNullOrWhiteSpace(activity) ? "unknown" : activity)}\n- {next}\n");
        return $"Handoff saved: {next}";
    }

    public static void SelfTest()
    {
        var root = FindWorkflowRoot() ?? throw new InvalidOperationException("Workflow root discovery failed.");
        var info = BuildStartInfo("python.exe", root, @"Y:\Example Project", "test-key", true);
        if (!info.ArgumentList.Contains("--once") || !info.ArgumentList.Contains("--non-interactive"))
            throw new InvalidOperationException("Argument construction failed.");
        if (info.Environment["UNSLOTH_API_KEY"] != "test-key")
            throw new InvalidOperationException("API key environment handling failed.");

        var gguf = DescribeModel(@"Y:\.llm_models\Qwen3.5 9B｜Q8_0.gguf");
        if (gguf.Name != "Qwen3.5 9B" || gguf.Weights != "9B" || gguf.Quant != "Q8_0")
            throw new InvalidOperationException($"Model description failed: {gguf}");
        var moe = DescribeModel("unsloth/Qwen-AgentWorld-35B-A3B-GGUF", "UD-Q2_K_XL");
        if (moe.Weights != "35B · 3B active" || moe.Quant != "UD-Q2_K_XL" || moe.Name != "Qwen-AgentWorld-35B-A3B")
            throw new InvalidOperationException($"MoE model description failed: {moe}");
        if (TryParseEvent("@@AO {\"e\":\"usage\",\"role\":\"planner\"}")?.GetProperty("role").GetString() != "planner" ||
            TryParseEvent("plain log line") is not null)
            throw new InvalidOperationException("Event parsing failed.");

        var testArea = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-{Guid.NewGuid():N}");
        var testProject = Path.Combine(testArea, "project");
        try
        {
            Directory.CreateDirectory(testArea);
            var customProject = Path.Combine(testArea, "custom-project.md");
            var customConfig = Path.Combine(testArea, "custom-config.json");
            File.WriteAllText(customProject, "custom-project");
            File.WriteAllText(customConfig, "{\"roles\":{\"planner\":{},\"implementer\":{},\"reviewer\":{}}}");
            File.WriteAllText(Path.Combine(testArea, "api_key.txt"), "test-api-key\n");
            var detectedKey = FindApiKeyFile(testArea, Path.Combine(testArea, "elsewhere"));
            if (detectedKey?.Key != "test-api-key")
                throw new InvalidOperationException("API key file detection failed.");
            ValidateConfigFile(customConfig);
            var roleConfig = Path.Combine(testArea, "role-config.json");
            File.WriteAllText(roleConfig, "{\"keep\":1,\"roles\":{\"planner\":{\"display_name\":\"P\",\"sampling\":{\"temperature\":0.6,\"top_p\":0.9,\"extra_body\":{\"repetition_penalty\":1.1,\"top_k\":20}},\"auto_load\":{\"model_path\":\"m\"}},\"reviewer\":{\"sampling\":{\"temperature\":0.2}}}}");
            var before = ReadRoleSettings(roleConfig, "planner");
            if (before != new RoleSettings("0.6", "", "1.1", "", ""))
                throw new InvalidOperationException($"Role settings read failed: {before}");
            if (WriteRoleSettings(roleConfig, "planner", new RoleSettings("0.8", "4096", "", "0", "32768")) is not null)
                throw new InvalidOperationException("Valid role settings were rejected.");
            var written = JsonNode.Parse(File.ReadAllText(roleConfig))!;
            var planner = written["roles"]!["planner"]!;
            if ((double)planner["sampling"]!["temperature"]! != 0.8 || (int)planner["sampling"]!["max_tokens"]! != 4096 ||
                (int)planner["auto_load"]!["reasoning_budget"]! != 0 || (int)planner["auto_load"]!["max_seq_length"]! != 32768)
                throw new InvalidOperationException("Role settings were not written.");
            var extra = planner["sampling"]!["extra_body"]!.AsObject();
            if (extra.ContainsKey("repetition_penalty") || (int)extra["top_k"]! != 20)
                throw new InvalidOperationException("Empty repetition penalty should remove only that key.");
            if ((int)written["keep"]! != 1 || (double)planner["sampling"]!["top_p"]! != 0.9 ||
                (string)planner["auto_load"]!["model_path"]! != "m" || (string)planner["display_name"]! != "P" ||
                (double)written["roles"]!["reviewer"]!["sampling"]!["temperature"]! != 0.2)
                throw new InvalidOperationException("Role settings changed unrelated keys.");
            if (WriteRoleSettings(roleConfig, "reviewer", new RoleSettings("", "", "1.3", "", "")) is not null ||
                (double)JsonNode.Parse(File.ReadAllText(roleConfig))!["roles"]!["reviewer"]!["sampling"]!["extra_body"]!["repetition_penalty"]! != 1.3)
                throw new InvalidOperationException("A missing extra_body was not created.");
            var untouched = File.ReadAllText(roleConfig);
            if (WriteRoleSettings(roleConfig, "planner", new RoleSettings("3", "", "", "", "")) is null ||
                WriteRoleSettings(roleConfig, "planner", new RoleSettings("", "", "", "", "100")) is null ||
                WriteRoleSettings(roleConfig, "planner", new RoleSettings("1", "10", "abc", "", "")) is null ||
                File.ReadAllText(roleConfig) != untouched)
                throw new InvalidOperationException("Invalid role settings were accepted or partially written.");
            var noLoad = JsonNode.Parse(File.ReadAllText(roleConfig))!["roles"]!["reviewer"]!;
            if (noLoad["auto_load"] is not null || WriteRoleSettings(roleConfig, "reviewer", new RoleSettings("0.5", "", "", "", "")) is not null ||
                JsonNode.Parse(File.ReadAllText(roleConfig))!["roles"]!["reviewer"]!["auto_load"] is not null)
                throw new InvalidOperationException("A role without auto_load must not gain one for sampling-only changes.");
            untouched = File.ReadAllText(roleConfig);
            if (WriteRoleSettings(roleConfig, "reviewer", new RoleSettings("", "", "", "", "4096")) is null ||
                WriteRoleSettings(roleConfig, "reviewer", new RoleSettings("0.9", "", "", "0", "")) is null ||
                File.ReadAllText(roleConfig) != untouched)
                throw new InvalidOperationException("Context length or reasoning budget without auto_load model_path was accepted.");
            var run = new System.Windows.Documents.Run("12 tokens");
            var tokens = new System.Windows.Controls.TextBlock();
            tokens.Inlines.Add(run);
            var inner = new System.Windows.Controls.TextBlock();
            var innerRun = new System.Windows.Documents.Run("Settings");
            inner.Inlines.Add(innerRun);
            var settingsButton = new System.Windows.Controls.Button { Content = inner };
            if (IsInsideButton(run) || IsInsideButton(tokens) || IsInsideButton(null) || !IsInsideButton(innerRun) || !IsInsideButton(inner) || !IsInsideButton(settingsButton))
                throw new InvalidOperationException("Button ancestor detection failed.");
            var copied = InitializeProject(root, testProject, customProject, customConfig);
            if (copied.Length != ProjectFiles.Length || MissingProjectFiles(testProject).Length != 0)
                throw new InvalidOperationException("Project initialization failed.");
            if (File.ReadAllText(Path.Combine(testProject, "PROJECT.md")) != "custom-project")
                throw new InvalidOperationException("Custom PROJECT.md was not copied.");
            File.WriteAllText(Path.Combine(testProject, "PROJECT.md"), "preserve-me");
            InitializeProject(root, testProject);
            if (File.ReadAllText(Path.Combine(testProject, "PROJECT.md")) != "preserve-me")
                throw new InvalidOperationException("Project initialization overwrote an existing file.");
            if (PrimaryLabel(testProject) != "Start Task")
                throw new InvalidOperationException("Fresh project should offer Start Task.");
            File.WriteAllText(Path.Combine(testProject, "STATE.md"), "# Verified state\n\n## Passed: PHASE-0\n- x\n");
            if (PrimaryLabel(testProject) != "Resume Project — PHASE-0")
                throw new InvalidOperationException("Passed task should offer Resume.");
            File.WriteAllText(Path.Combine(testProject, "TASK.md"), "# PHASE-1\n\n## Outcome\nx\n");
            if (PrimaryLabel(testProject) != "Resume Project — PHASE-1")
                throw new InvalidOperationException("Active task should win over passed task.");

            var stopProject = Path.Combine(testArea, "stop");
            Directory.CreateDirectory(stopProject);
            var handoffFile = Path.Combine(stopProject, "HANDOFF.md");
            var checkpointFile = Path.Combine(stopProject, ".ao_checkpoint.json");
            File.WriteAllText(handoffFile, "# Old handoff\nkeep-me\n");
            File.WriteAllText(checkpointFile, "{\"version\":1,\"task\":{\"id\":\"PHASE-7\",\"outcome\":\"x\"},\"round\":2,\"phase\":\"reviewing\"}");
            RecordStop(stopProject, new DateTime(2026, 1, 2, 3, 4, 5), "Reviewing");
            var stopText = File.ReadAllText(handoffFile);
            if (!stopText.StartsWith("# Old handoff\nkeep-me\n") || !stopText.Contains("## Stopped by user") ||
                !stopText.Contains("- Time: 2026-01-02T03:04:05") || !stopText.Contains("- Activity: Reviewing") ||
                !stopText.Contains("- The next run resumes task PHASE-7 (reviewing, review round 3)."))
                throw new InvalidOperationException($"Stop note with a checkpoint failed: {stopText}");
            File.Delete(checkpointFile);
            File.Delete(handoffFile);
            RecordStop(stopProject, DateTime.Now, "");
            stopText = File.ReadAllText(handoffFile);
            if (!stopText.StartsWith("## Stopped by user") || !stopText.Contains("- Activity: unknown") ||
                !stopText.Contains("- No task was in progress; the next run plans a new task."))
                throw new InvalidOperationException($"Stop note without a checkpoint failed: {stopText}");
            File.WriteAllText(checkpointFile, "{not json");
            RecordStop(stopProject, DateTime.Now, "x");
            if (!File.ReadAllText(handoffFile).EndsWith("- No task was in progress; the next run plans a new task.\n"))
                throw new InvalidOperationException("Stop note with a corrupt checkpoint failed.");
        }
        finally
        {
            if (Directory.Exists(testArea))
                Directory.Delete(testArea, recursive: true);
        }
    }
}

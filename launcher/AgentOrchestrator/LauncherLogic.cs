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
        var info = NewStartInfo(python, workflowRoot, apiKey);
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(projectRoot);
        info.ArgumentList.Add("--non-interactive");
        info.ArgumentList.Add("--events");
        if (once)
            info.ArgumentList.Add("--once");
        return info;
    }

    // ---- chat: one orchestrator process per user message, fed the whole conversation through a request file

    public sealed record ChatMessage(string Role, string Content);

    // mode "single" talks to one worker (a built-in role id or a custom worker's Id); "multi" runs Planner -> Implementer -> Reviewer.
    public static string BuildChatRequest(string mode, string worker, IEnumerable<ChatMessage> messages) =>
        JsonSerializer.Serialize(new
        {
            mode = mode == "multi" ? "multi" : "single",
            worker,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static ProcessStartInfo BuildChatStartInfo(string python, string workflowRoot, string folder, string apiKey, string requestFile)
    {
        var info = NewStartInfo(python, workflowRoot, apiKey);
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(folder);
        info.ArgumentList.Add("--chat");
        info.ArgumentList.Add(requestFile);
        info.ArgumentList.Add("--events");
        info.ArgumentList.Add("--non-interactive");
        return info;
    }

    // True when configPath is the workflow root's config.json: the template new projects are initialized from.
    public static bool IsTemplateConfig(string configPath, string? workflowRoot) =>
        workflowRoot is not null && configPath.Length > 0 && string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(Path.Combine(workflowRoot, "config.json")), StringComparison.OrdinalIgnoreCase);

    // The folder a chat runs in. An initialized project is used as is. Otherwise the template config is copied to a
    // fresh temp folder (the template itself is never written) and gets the custom workers; the caller deletes it
    // when the chat ends. IsTemp says whether the folder was created here (and so is the caller's to delete); a real
    // project folder is never IsTemp. Returns "Title: reason" for each custom worker left out.
    public sealed record ChatFolder(string Path, bool IsTemp);

    public static ChatFolder PrepareChatFolder(string selectedProject, string workflowRoot, IEnumerable<WorkerSetting> workers, out string[] skipped)
    {
        skipped = [];
        var projectConfig = Path.Combine(selectedProject, "config.json");
        if (selectedProject.Length > 0 && File.Exists(projectConfig) && !IsTemplateConfig(projectConfig, workflowRoot))
            return new ChatFolder(selectedProject, false);
        var folder = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-chat-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, "config.json");
            File.Copy(Path.Combine(workflowRoot, "config.json"), copy);
            skipped = WriteCustomWorkers(copy, workers);
            return new ChatFolder(folder, true);
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static ProcessStartInfo NewStartInfo(string python, string workflowRoot, string apiKey)
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

    /// <summary>Moves an existing PROJECT.md aside (never overwriting a prior backup) and returns the backup's file name.</summary>
    public static string BackupProjectMd(string projectRoot)
    {
        var source = Path.Combine(projectRoot, "PROJECT.md");
        var backup = "PROJECT.md.bak";
        if (File.Exists(Path.Combine(projectRoot, backup)))
            backup = $"PROJECT.md.bak-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        for (var n = 2; File.Exists(Path.Combine(projectRoot, backup)); n++)
            backup = $"PROJECT.md.bak-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{n}";
        File.Move(source, Path.Combine(projectRoot, backup));
        return backup;
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
    public sealed record RoleSettings(string Temperature, string MaxTokens, string RepetitionPenalty, string ReasoningBudget, string ContextLength, string Thinking = "Auto");

    // sampling.enable_thinking: true = On, false = Off, key absent = Auto.
    public static readonly string[] ThinkingChoices = ["Auto", "On", "Off"];

    public static RoleSettings ReadRoleSettings(string configPath, string role)
    {
        var settings = JsonNode.Parse(File.ReadAllText(configPath))!["roles"]![role]!;
        static string Text(JsonNode? node) => node is JsonValue ? node.ToJsonString() : "";
        return new RoleSettings(
            Text(settings["sampling"]?["temperature"]), Text(settings["sampling"]?["max_tokens"]),
            Text(settings["sampling"]?["extra_body"]?["repetition_penalty"]),
            Text(settings["auto_load"]?["reasoning_budget"]), Text(settings["auto_load"]?["max_seq_length"]),
            settings["sampling"]?["enable_thinking"] is JsonValue flag && flag.TryGetValue<bool>(out var on) ? (on ? "On" : "Off") : "Auto");
    }

    // The field rules shared by the built-in and custom Settings dialogs; null when every field is acceptable.
    public static string? ValidateRoleSettings(RoleSettings values)
    {
        static bool Number(string text, double min, double max, out double result) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result) && result >= min && result <= max;
        static bool Whole(string text, long min, long max, out long result) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= min && result <= max;

        if (!ThinkingChoices.Contains(values.Thinking))
            return "Thinking must be Auto, On or Off.";
        var temperatureText = values.Temperature.Trim();
        var maxTokensText = values.MaxTokens.Trim();
        var penaltyText = values.RepetitionPenalty.Trim();
        var budgetText = values.ReasoningBudget.Trim();
        var contextText = values.ContextLength.Trim();
        if (temperatureText.Length > 0 && !Number(temperatureText, 0, 2, out _))
            return "Temperature must be a number from 0 to 2.";
        if (maxTokensText.Length > 0 && !Whole(maxTokensText, 1, 262144, out _))
            return "Max tokens must be a whole number from 1 to 262144.";
        if (penaltyText.Length > 0 && !Number(penaltyText, 1, 2, out _))
            return "Repetition penalty must be a number from 1.0 to 2.0 (or empty).";
        if (budgetText.Length > 0 && !Whole(budgetText, 0, int.MaxValue, out _))
            return "Reasoning budget must be a whole number, 0 or more.";
        if (contextText.Length > 0 && !Whole(contextText, 512, 262144, out _))
            return "Context length must be a whole number from 512 to 262144.";
        return null;
    }

    // Validates every field, then writes; returns an error message (nothing written) or null on success.
    // An empty repetition penalty removes the key; the other empty fields leave their key as it is.
    public static string? WriteRoleSettings(string configPath, string role, RoleSettings values)
    {
        if (ValidateRoleSettings(values) is { } problem)
            return problem;
        double temperature = 0, penalty = 0;
        long maxTokens = 0, budget = 0, context = 0;
        var temperatureText = values.Temperature.Trim();
        var maxTokensText = values.MaxTokens.Trim();
        var penaltyText = values.RepetitionPenalty.Trim();
        var budgetText = values.ReasoningBudget.Trim();
        var contextText = values.ContextLength.Trim();
        _ = double.TryParse(temperatureText, NumberStyles.Float, CultureInfo.InvariantCulture, out temperature);
        _ = long.TryParse(maxTokensText, NumberStyles.None, CultureInfo.InvariantCulture, out maxTokens);
        _ = double.TryParse(penaltyText, NumberStyles.Float, CultureInfo.InvariantCulture, out penalty);
        _ = long.TryParse(budgetText, NumberStyles.None, CultureInfo.InvariantCulture, out budget);
        _ = long.TryParse(contextText, NumberStyles.None, CultureInfo.InvariantCulture, out context);

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
        if (values.Thinking == "Auto")
            sampling.Remove("enable_thinking");
        else
            sampling["enable_thinking"] = values.Thinking == "On";
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

    public static readonly string[] BuiltInRoles = ["planner", "implementer", "reviewer"];

    // Copies the worker list and, when the config exists, each built-in role's complete roles.<role> object.
    public static WorkerPreset CapturePreset(string name, string configPath, IEnumerable<WorkerSetting> workers)
    {
        var preset = new WorkerPreset
        {
            Name = name,
            Workers = workers.Select(w => w.Clone()).ToList(),
        };
        if (File.Exists(configPath) && JsonNode.Parse(File.ReadAllText(configPath))?["roles"] is JsonObject roles)
            foreach (var role in BuiltInRoles)
                if (roles[role] is JsonObject stored)
                    preset.Roles[role] = stored.ToJsonString();
        return preset;
    }

    // Replaces only the stored roles.<role> objects; every other key and role in the config stays.
    // Returns false when there is no config to write to.
    public static bool ApplyPresetRoles(string configPath, WorkerPreset preset)
    {
        if (!File.Exists(configPath))
            return false;
        if (preset.Roles.Count == 0)
            return true;
        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("config.json is not a JSON object.");
        var roles = (root["roles"] ??= new JsonObject()).AsObject();
        foreach (var (role, json) in preset.Roles.Where(r => BuiltInRoles.Contains(r.Key)))
            roles[role] = JsonNode.Parse(json);
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return true;
    }

    public static readonly string[] ReservedTitles = ["PLANNER", "IMPLEMENTER", "REVIEWER"];

    // Why a custom worker cannot have this title (reserved, empty or already used by another worker), or null.
    public static string? TitleProblem(string title, string selfId, IEnumerable<WorkerSetting> workers)
    {
        title = title.Trim();
        if (title.Length == 0)
            return "Enter a title.";
        if (ReservedTitles.Contains(title, StringComparer.OrdinalIgnoreCase))
            return $"\"{title}\" is reserved for a built-in worker. Choose another title.";
        if (workers.Any(w => w.Id != selfId && string.Equals(w.Title.Trim(), title, StringComparison.OrdinalIgnoreCase)))
            return $"Another worker is already titled \"{title}\".";
        return null;
    }

    // Why the orchestrator would ignore this custom worker (it needs a model, a system prompt, and for a
    // Specialist a description), or null. Shared by the Settings dialog and CustomProblems.
    public static string? CustomWorkerProblem(WorkerSetting w)
    {
        if (string.IsNullOrWhiteSpace(w.ModelPath))
            return string.IsNullOrWhiteSpace(w.ModelId) ? "no model assigned (Change model)" : "pick the model again (Change model)";
        if (string.IsNullOrWhiteSpace(w.SystemPrompt))
            return "the system prompt is empty (Settings)";
        if (w.Kind != "reviewer" && string.IsNullOrWhiteSpace(w.Description))
            return "a Specialist needs a description (Settings)";
        return null;
    }

    // The orchestrator's acceptance rules for every worker in order (null = accepted or not a custom worker):
    // the title may not be empty, reserved, or used by an earlier accepted worker, and the project's
    // implementer needs a base_url (custom workers copy it). Used by the writer and by the card warnings.
    public static string?[] CustomProblems(IReadOnlyList<WorkerSetting> workers, bool implementerHasBaseUrl)
    {
        var problems = new string?[workers.Count];
        var used = new HashSet<string>(ReservedTitles, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < workers.Count; i++)
        {
            var w = workers[i];
            if (!w.Custom)
                continue;
            var title = w.Title.Trim();
            problems[i] = !implementerHasBaseUrl ? "the project's implementer has no base_url in config.json" :
                title.Length == 0 ? "the title is empty — rename it" :
                ReservedTitles.Contains(title, StringComparer.OrdinalIgnoreCase) ? $"title '{title}' is reserved for a built-in worker — rename it" :
                used.Contains(title) ? $"title '{title}' is already used — rename it" :
                CustomWorkerProblem(w);
            if (problems[i] is null)
                used.Add(title);
        }
        return problems;
    }

    public static bool ImplementerHasBaseUrl(string configPath)
    {
        try { return !string.IsNullOrWhiteSpace((JsonNode.Parse(File.ReadAllText(configPath)))?["roles"]?["implementer"]?["base_url"]?.ToString()); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    // Rewrites the top-level custom_workers array (every other key stays). Each worker copies the Implementer's
    // connection settings, auto_load defaults and sampling, then applies its own model and settings.
    // Workers the orchestrator would ignore are not written; returns "Title: reason" for each.
    // A missing implementer just means nothing to copy.
    public static string[] WriteCustomWorkers(string configPath, IEnumerable<WorkerSetting> workers)
    {
        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("config.json is not a JSON object.");
        var implementer = root["roles"]?["implementer"] as JsonObject;
        var skipped = new List<string>();
        var array = new JsonArray();
        var all = workers.ToList();
        var problems = CustomProblems(all, !string.IsNullOrWhiteSpace(implementer?["base_url"]?.ToString()));
        for (var i = 0; i < all.Count; i++)
        {
            var w = all[i];
            if (!w.Custom)
                continue;
            if (problems[i] is { } problem)
            {
                skipped.Add($"{w.Title.Trim()}: {problem}");
                continue;
            }
            var entry = new JsonObject
            {
                ["id"] = w.Id, ["title"] = w.Title.Trim(), ["kind"] = w.Kind == "reviewer" ? "reviewer" : "specialist",
                ["description"] = w.Description, ["system_prompt"] = w.SystemPrompt,
            };
            foreach (var key in new[] { "base_url", "management_url", "api_key_env", "api_key" })
                if (implementer?[key] is { } value)
                    entry[key] = value.DeepClone();
            entry["expected_model_contains"] = w.ModelExpected ?? w.ModelId ?? "";
            entry["display_name"] = w.ModelId ?? w.ModelExpected ?? "";
            if (implementer?["timeout_seconds"] is { } timeout)
                entry["timeout_seconds"] = timeout.DeepClone();

            var load = implementer?["auto_load"]?.DeepClone() as JsonObject ?? new JsonObject();
            load["model_path"] = w.ModelPath;
            if (string.IsNullOrEmpty(w.ModelVariant))
                load.Remove("gguf_variant");
            else
                load["gguf_variant"] = w.ModelVariant;
            if (long.TryParse(w.ReasoningBudget.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var budget))
                load["reasoning_budget"] = budget;
            if (long.TryParse(w.ContextLength.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var context))
                load["max_seq_length"] = context;
            entry["auto_load"] = load;

            var sampling = implementer?["sampling"]?.DeepClone() as JsonObject ?? new JsonObject();
            sampling.Remove("enable_thinking");
            if (double.TryParse(w.Temperature.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
                sampling["temperature"] = temperature;
            if (long.TryParse(w.MaxTokens.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var maxTokens))
                sampling["max_tokens"] = maxTokens;
            if (double.TryParse(w.RepetitionPenalty.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var penalty))
                (sampling["extra_body"] ??= new JsonObject()).AsObject()["repetition_penalty"] = penalty;
            if (w.Thinking is "On" or "Off")
                sampling["enable_thinking"] = w.Thinking == "On";
            entry["sampling"] = sampling;
            array.Add(entry);
        }
        root["custom_workers"] = array;
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return skipped.ToArray();
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

    // TASK TIME / TURN TIME: m:ss, h:mm:ss from an hour, "—" before the clock has ever started.
    public static string FormatElapsed(TimeSpan? elapsed) =>
        elapsed is not { } t ? "—" : t.TotalHours >= 1 ? $@"{(int)t.TotalHours}:{t:mm\:ss}" : t.ToString(@"m\:ss");

    public static string FormatElapsed(System.Diagnostics.Stopwatch watch) =>
        FormatElapsed(watch.IsRunning || watch.Elapsed > TimeSpan.Zero ? watch.Elapsed : null);

    public static void SelfTest()
    {
        if (FormatElapsed((TimeSpan?)null) != "—" || FormatElapsed(TimeSpan.FromSeconds(65)) != "1:05" ||
            FormatElapsed(TimeSpan.FromSeconds(3725)) != "1:02:05" || FormatElapsed(TimeSpan.FromHours(25)) != "25:00:00")
            throw new InvalidOperationException("Elapsed time formatting failed.");
        var root = FindWorkflowRoot() ?? throw new InvalidOperationException("Workflow root discovery failed.");
        var info = BuildStartInfo("python.exe", root, @"Y:\Example Project", "test-key", true);
        if (!info.ArgumentList.Contains("--once") || !info.ArgumentList.Contains("--non-interactive"))
            throw new InvalidOperationException("Argument construction failed.");
        if (info.Environment["UNSLOTH_API_KEY"] != "test-key")
            throw new InvalidOperationException("API key environment handling failed.");

        var chatInfo = BuildChatStartInfo("python.exe", root, @"Y:\Example Project", "test-key", @"Y:\req.json");
        var chatArgs = chatInfo.ArgumentList;
        if (chatArgs.IndexOf("--chat") < 0 || chatArgs[chatArgs.IndexOf("--chat") + 1] != @"Y:\req.json" || !chatArgs.Contains("--events") ||
            !chatArgs.Contains("--non-interactive") || chatArgs[chatArgs.IndexOf("--project") + 1] != @"Y:\Example Project" || chatArgs.Contains("--once") ||
            chatInfo.Environment["UNSLOTH_API_KEY"] != "test-key" || chatInfo.Environment["PYTHONUNBUFFERED"] != "1")
            throw new InvalidOperationException("Chat start info failed.");
        var chat = new[] { new ChatMessage("user", "hi \"there\"\nline2 é"), new ChatMessage("assistant", "hello"), new ChatMessage("user", "again") };
        var single = JsonNode.Parse(BuildChatRequest("single", "implementer", chat))!;
        var customId = Guid.NewGuid().ToString("N");
        var multi = JsonNode.Parse(BuildChatRequest("multi", customId, chat.Take(1)))!;
        var sm = single["messages"]!.AsArray();
        if ((string)single["mode"]! != "single" || (string)single["worker"]! != "implementer" || sm.Count != 3 ||
            (string)sm[0]!["role"]! != "user" || (string)sm[0]!["content"]! != "hi \"there\"\nline2 é" || (string)sm[1]!["role"]! != "assistant" ||
            (string)sm[2]!["content"]! != "again" || (string)multi["mode"]! != "multi" || (string)multi["worker"]! != customId || multi["messages"]!.AsArray().Count != 1 ||
            (string)JsonNode.Parse(BuildChatRequest("bogus", "planner", chat))!["mode"]! != "single")
            throw new InvalidOperationException("Chat request failed.");

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
            var rootDir = Path.Combine(testArea, "root");
            var rootConfig = Path.Combine(rootDir, "config.json");
            Directory.CreateDirectory(rootDir);
            File.WriteAllText(rootConfig, "{\"keep\":1,\"roles\":{\"implementer\":{\"base_url\":\"http://h/v1\"}}}");
            var rootBytes = File.ReadAllBytes(rootConfig);
            var chatWorkers = new List<WorkerSetting> { Ok("c1", "Chat Docs") };
            var chatPrepared = PrepareChatFolder(testArea, rootDir, chatWorkers, out _);
            var chatFolder = chatPrepared.Path;
            try
            {
                var chatCopy = JsonNode.Parse(File.ReadAllText(Path.Combine(chatFolder, "config.json")))!;
                if (!chatPrepared.IsTemp || chatFolder == rootDir || chatFolder == testArea || (int)chatCopy["keep"]! != 1 || chatCopy["custom_workers"]?.AsArray().Count != 1 ||
                    !File.ReadAllBytes(rootConfig).SequenceEqual(rootBytes) || !IsTemplateConfig(rootConfig.ToUpperInvariant(), rootDir))
                    throw new InvalidOperationException("Chat folder without a project must be a temp copy of the template.");
            }
            finally { Directory.Delete(chatFolder, true); }
            File.WriteAllText(Path.Combine(testArea, "config.json"), "{}");
            if (PrepareChatFolder(testArea, rootDir, chatWorkers, out _) is not { Path: var initialized, IsTemp: false } || initialized != testArea)
                throw new InvalidOperationException("Chat folder should be the initialized project.");
            File.Delete(Path.Combine(testArea, "config.json"));
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
            // Thinking: On/Off write a bool, Auto removes the key; other sampling keys and roles are untouched.
            string Sampling(string role) => JsonNode.Parse(File.ReadAllText(roleConfig))!["roles"]![role]!["sampling"]!.ToJsonString();
            var plannerBefore = Sampling("planner");
            var reviewerBefore = Sampling("reviewer");
            JsonNode? Flag() => JsonNode.Parse(File.ReadAllText(roleConfig))!["roles"]!["reviewer"]!["sampling"]!["enable_thinking"];
            var thinkingOk = WriteRoleSettings(roleConfig, "reviewer", new RoleSettings("", "", "", "", "", "On")) is null &&
                Flag()?.GetValue<bool>() == true && ReadRoleSettings(roleConfig, "reviewer").Thinking == "On" &&
                WriteRoleSettings(roleConfig, "reviewer", new RoleSettings("", "", "", "", "", "Off")) is null &&
                Flag()?.GetValue<bool>() == false && ReadRoleSettings(roleConfig, "reviewer").Thinking == "Off" &&
                Sampling("planner") == plannerBefore && Sampling("reviewer").Contains("\"repetition_penalty\"") == reviewerBefore.Contains("\"repetition_penalty\"") &&
                WriteRoleSettings(roleConfig, "reviewer", new RoleSettings("", "", "", "", "", "Auto")) is null &&
                Flag() is null && !Sampling("reviewer").Contains("enable_thinking") && ReadRoleSettings(roleConfig, "reviewer").Thinking == "Auto" &&
                Sampling("reviewer") == reviewerBefore &&
                WriteRoleSettings(roleConfig, "reviewer", new RoleSettings("", "", "", "", "", "Maybe")) is not null;
            if (!thinkingOk)
                throw new InvalidOperationException("Thinking setting was not written, read back or removed correctly.");
            // Presets: capture from config A, apply to config B; B keeps its own project keys.
            var configA = Path.Combine(testArea, "preset-a.json");
            var configB = Path.Combine(testArea, "preset-b.json");
            File.WriteAllText(configA, "{\"validation_commands\":[\"a\"],\"roles\":{\"planner\":{\"display_name\":\"PA\",\"sampling\":{\"temperature\":0.1},\"auto_load\":{\"model_path\":\"ma\"}},\"implementer\":{\"display_name\":\"IA\"},\"reviewer\":{\"display_name\":\"RA\"}}}");
            File.WriteAllText(configB, "{\"validation_commands\":[\"b\"],\"roles\":{\"planner\":{\"display_name\":\"PB\"},\"implementer\":{\"display_name\":\"IB\"},\"reviewer\":{\"display_name\":\"RB\"},\"extra\":{\"x\":1}}}");
            var workersA = new[] { new WorkerSetting { Id = "planner", Title = "PLANNER" }, new WorkerSetting { Id = "c1", Title = "MINE", Custom = true, ModelId = "m", ModelVariant = "Q4" } };
            var preset = CapturePreset("p", configA, workersA);
            if (preset.Workers.Count != 2 || preset.Workers[1].Title != "MINE" || preset.Workers[1].ModelVariant != "Q4" || preset.Roles.Count != 3)
                throw new InvalidOperationException("Preset capture failed.");
            var bBefore = File.ReadAllText(configB);
            if (CapturePreset("none", Path.Combine(testArea, "missing.json"), workersA).Roles.Count != 0 ||
                !ApplyPresetRoles(configB, new WorkerPreset { Name = "none" }) || File.ReadAllText(configB) != bBefore ||
                ApplyPresetRoles(Path.Combine(testArea, "missing.json"), preset))
                throw new InvalidOperationException("A preset without roles must leave the config untouched.");
            if (!ApplyPresetRoles(configB, preset))
                throw new InvalidOperationException("Preset roles were not applied.");
            var rolesA = JsonNode.Parse(File.ReadAllText(configA))!["roles"]!;
            var presetApplied = JsonNode.Parse(File.ReadAllText(configB))!;
            if (BuiltInRoles.Any(r => presetApplied["roles"]![r]!.ToJsonString() != rolesA[r]!.ToJsonString()) ||
                presetApplied["validation_commands"]![0]!.ToString() != "b" || (int)presetApplied["roles"]!["extra"]!["x"]! != 1)
                throw new InvalidOperationException("Applying a preset must replace the three roles and nothing else.");
            // Custom workers: contract of the written custom_workers array.
            var cwConfig = Path.Combine(testArea, "cw.json");
            File.WriteAllText(cwConfig, "{\"keep\":1,\"custom_workers\":[{\"id\":\"old\"}],\"roles\":{\"implementer\":{\"base_url\":\"http://h/v1\",\"management_url\":\"http://h\",\"api_key_env\":\"K\",\"api_key\":\"sk\",\"timeout_seconds\":600,\"auto_load\":{\"model_path\":\"impl\",\"gguf_variant\":\"Q8_0\",\"max_seq_length\":32768,\"n_parallel\":1},\"sampling\":{\"temperature\":0.4,\"top_p\":0.8,\"max_tokens\":8192,\"enable_thinking\":true,\"extra_body\":{\"top_k\":20,\"repetition_penalty\":1.05}}}}}");
            var rolesBefore = JsonNode.Parse(File.ReadAllText(cwConfig))!["roles"]!.ToJsonString();
            var cwWorkers = new[]
            {
                new WorkerSetting { Id = "planner", Title = "PLANNER" },
                new WorkerSetting { Id = "s1", Title = " Tester ", Custom = true, Kind = "specialist", Description = "Runs tests", SystemPrompt = "You test.\nBe brief.",
                    ModelId = "org/Model-GGUF", ModelVariant = "Q4_K_M", ModelPath = "org/Model-GGUF", ModelExpected = "Model-GGUF",
                    Temperature = "0.2", MaxTokens = "2048", RepetitionPenalty = "1.2", ReasoningBudget = "0", ContextLength = "16384", Thinking = "Off" },
                new WorkerSetting { Id = "r1", Title = "Security", Custom = true, Kind = "reviewer", SystemPrompt = "You review.", ModelId = "local.gguf", ModelPath = "C:\\m\\local.gguf", ModelExpected = "local.gguf" },
                new WorkerSetting { Id = "n1", Title = "NoModel", Custom = true, Kind = "reviewer", SystemPrompt = "p" },
                new WorkerSetting { Id = "n2", Title = "NoPrompt", Custom = true, ModelPath = "m", ModelId = "m", Description = "d" },
                new WorkerSetting { Id = "n3", Title = "NoDesc", Custom = true, Kind = "specialist", ModelPath = "m", ModelId = "m", SystemPrompt = "p" },
                new WorkerSetting { Id = "n4", Title = "OldSaved", Custom = true, Kind = "reviewer", ModelId = "org/Old", SystemPrompt = "p" },
            };
            var skippedTitles = WriteCustomWorkers(cwConfig, cwWorkers);
            var cw = JsonNode.Parse(File.ReadAllText(cwConfig))!;
            var cwArray = cw["custom_workers"]!.AsArray();
            var spec = cwArray[0]!;
            var rev = cwArray[1]!;
            var specSampling = spec["sampling"]!;
            var specLoad = spec["auto_load"]!;
            var revSampling = rev["sampling"]!;
            if (skippedTitles.Length != 4 || !skippedTitles[0].StartsWith("NoModel: no model") || !skippedTitles[1].StartsWith("NoPrompt: ") || !skippedTitles[2].StartsWith("NoDesc: ") || !skippedTitles[3].Contains("pick the model again") || cwArray.Count != 2 || (int)cw["keep"]! != 1 || cw["roles"]!.ToJsonString() != rolesBefore)
                throw new InvalidOperationException("Custom workers: skipping, array size or untouched keys failed.");
            if ((string)spec["id"]! != "s1" || (string)spec["title"]! != "Tester" || (string)spec["kind"]! != "specialist" || (string)spec["description"]! != "Runs tests" ||
                (string)spec["system_prompt"]! != "You test.\nBe brief." || (string)spec["base_url"]! != "http://h/v1" || (string)spec["management_url"]! != "http://h" ||
                (string)spec["api_key_env"]! != "K" || (string)spec["api_key"]! != "sk" || (int)spec["timeout_seconds"]! != 600 || (string)spec["expected_model_contains"]! != "Model-GGUF" || (string)spec["display_name"]! != "org/Model-GGUF")
                throw new InvalidOperationException("Custom workers: specialist fields do not match the contract.");
            if ((string)specLoad["model_path"]! != "org/Model-GGUF" || (string)specLoad["gguf_variant"]! != "Q4_K_M" || (int)specLoad["reasoning_budget"]! != 0 ||
                (int)specLoad["max_seq_length"]! != 16384 || (int)specLoad["n_parallel"]! != 1)
                throw new InvalidOperationException("Custom workers: specialist model or auto_load failed.");
            if ((double)specSampling["temperature"]! != 0.2 || (int)specSampling["max_tokens"]! != 2048 || specSampling["enable_thinking"]!.GetValue<bool>() ||
                (double)specSampling["extra_body"]!["repetition_penalty"]! != 1.2 || (int)specSampling["extra_body"]!["top_k"]! != 20 || (double)specSampling["top_p"]! != 0.8)
                throw new InvalidOperationException("Custom workers: specialist sampling failed.");
            if ((string)rev["kind"]! != "reviewer" || (string)rev["auto_load"]!["model_path"]! != "C:\\m\\local.gguf" || rev["auto_load"]!["gguf_variant"] is not null ||
                (string)rev["expected_model_contains"]! != "local.gguf" || revSampling["enable_thinking"] is not null || (double)revSampling["temperature"]! != 0.4)
                throw new InvalidOperationException("Custom workers: reviewer fields failed.");
            WriteCustomWorkers(cwConfig, cwWorkers.Take(1));
            if (JsonNode.Parse(File.ReadAllText(cwConfig))!["custom_workers"]!.AsArray().Count != 0)
                throw new InvalidOperationException("Custom workers: rewriting must replace the previous array.");
            // Title rules and the implementer's base_url, as the orchestrator applies them.
            WorkerSetting Ok(string id, string title) => new() { Id = id, Title = title, Custom = true, Kind = "reviewer", SystemPrompt = "p", ModelId = "m", ModelPath = "m" };
            var titled = new[] { Ok("t1", "Worker 2"), Ok("t2", "WORKER 2"), Ok("t3", "Reviewer"), Ok("t4", " planner "), Ok("t5", "Fine") };
            var titledSkipped = WriteCustomWorkers(cwConfig, titled);
            var titledWritten = JsonNode.Parse(File.ReadAllText(cwConfig))!["custom_workers"]!.AsArray();
            if (titledSkipped.Length != 3 || !titledSkipped[0].Contains("already used") || !titledSkipped[1].Contains("reserved") || !titledSkipped[2].Contains("reserved") ||
                titledWritten.Count != 2 || (string)titledWritten[0]!["id"]! != "t1" || (string)titledWritten[1]!["id"]! != "t5")
                throw new InvalidOperationException("Custom workers: duplicate and reserved titles must not be written.");
            var noUrlConfig = Path.Combine(testArea, "nourl.json");
            File.WriteAllText(noUrlConfig, "{\"roles\":{\"implementer\":{}}}");
            if (ImplementerHasBaseUrl(noUrlConfig) || !ImplementerHasBaseUrl(cwConfig) || WriteCustomWorkers(noUrlConfig, [Ok("u1", "Docs")]) is not [var noUrl] || !noUrl.Contains("base_url") ||
                JsonNode.Parse(File.ReadAllText(noUrlConfig))!["custom_workers"]!.AsArray().Count != 0)
                throw new InvalidOperationException("Custom workers: a project without an implementer base_url must report and write nothing.");
            var peers = new[] { new WorkerSetting { Id = "a", Title = "Tester" }, new WorkerSetting { Id = "b", Title = "Other" } };
            if (TitleProblem("reviewer", "a", peers) is null || TitleProblem(" Planner ", "a", peers) is null || TitleProblem("IMPLEMENTER", "a", peers) is null ||
                TitleProblem("TESTER", "b", peers) is null || TitleProblem("  ", "a", peers) is null || TitleProblem("tester", "a", peers) is not null || TitleProblem("Docs", "a", peers) is not null)
                throw new InvalidOperationException("Custom worker title validation failed.");
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

            // Existing PROJECT.md: keep-existing still creates the rest; replace backs up and never clobbers a backup.
            var mdProject = Path.Combine(testArea, "md-existing");
            Directory.CreateDirectory(mdProject);
            File.WriteAllText(Path.Combine(mdProject, "PROJECT.md"), "user-one");
            var keptCopied = InitializeProject(root, mdProject, customProject);
            if (File.ReadAllText(Path.Combine(mdProject, "PROJECT.md")) != "user-one" || keptCopied.Contains("PROJECT.md") ||
                keptCopied.Length != ProjectFiles.Length - 1 || MissingProjectFiles(mdProject).Length != 0)
                throw new InvalidOperationException("Keep-existing PROJECT.md initialization failed.");
            var backupOne = BackupProjectMd(mdProject);
            InitializeProject(root, mdProject, customProject);
            if (backupOne != "PROJECT.md.bak" || File.ReadAllText(Path.Combine(mdProject, backupOne)) != "user-one" ||
                File.ReadAllText(Path.Combine(mdProject, "PROJECT.md")) != "custom-project")
                throw new InvalidOperationException("Replacing PROJECT.md did not back up and install the new file.");
            File.WriteAllText(Path.Combine(mdProject, "PROJECT.md"), "user-two");
            var backupTwo = BackupProjectMd(mdProject);
            InitializeProject(root, mdProject);
            if (backupTwo == backupOne || File.ReadAllText(Path.Combine(mdProject, backupOne)) != "user-one" ||
                File.ReadAllText(Path.Combine(mdProject, backupTwo)) != "user-two" ||
                File.ReadAllText(Path.Combine(mdProject, "PROJECT.md")) != File.ReadAllText(Path.Combine(root, "PROJECT.md")))
                throw new InvalidOperationException("A second PROJECT.md replacement overwrote the first backup.");
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

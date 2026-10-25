using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
        }
        finally
        {
            if (Directory.Exists(testArea))
                Directory.Delete(testArea, recursive: true);
        }
    }
}

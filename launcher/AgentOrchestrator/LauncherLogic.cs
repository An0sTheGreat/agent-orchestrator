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

    // requestFile: a Code request (BuildCodeRequest) giving the run a Code turn's tools; the project stays folders[0].
    public static ProcessStartInfo BuildStartInfo(string python, string workflowRoot, string projectRoot, string apiKey, bool once, string requestFile = "")
    {
        var info = NewStartInfo(python, workflowRoot, apiKey);
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(projectRoot);
        info.ArgumentList.Add("--non-interactive");
        info.ArgumentList.Add("--events");
        if (once)
            info.ArgumentList.Add("--once");
        if (requestFile.Length > 0)
        {
            info.ArgumentList.Add("--request");
            info.ArgumentList.Add(requestFile);
        }
        return info;
    }

    // ---- chat: one orchestrator process per user message, fed the whole conversation through a request file

    // Time: when it was sent / completed (null in old saved chats). Images: the user's photos, or the reply's browser
    // screenshots, shown as thumbnails. Neither is sent to the engine.
    public sealed record ChatMessage(string Role, string Content, DateTimeOffset? Time = null, IReadOnlyList<string>? Images = null);

    // The stats under a model call's output: "Planner · 42.1 tok/s · 1,234 generated · 5,678 prompt" (no tok/s when unknown).
    // The chat status line while llama-server works and nothing is on screen yet: reading the prompt, then writing (a quiet
    // call such as /compact's summary shows nothing until it is done).
    public static string LlamaProgressLine(string name, long prompt, long processed, long decoded) => decoded == 0
        ? $"{name} is reading the conversation: {processed:N0} / {prompt:N0} tokens…"
        : $"{name} is writing: {decoded:N0} tokens so far…";

    public static string StatsLine(string name, double tps, long generated, long prompt)
    {
        var parts = new List<string>();
        if (name.Length > 0)
            parts.Add(name);
        if (tps > 0)
            parts.Add(tps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " tok/s");
        parts.Add(generated.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " generated");
        parts.Add(prompt.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " prompt");
        return string.Join(" · ", parts);
    }

    // While a call streams: "Planner · 37.5 tok/s · 1,204 tokens" (streamed chunks, about one token each).
    public static string LiveLine(string name, double tps, long tokens)
    {
        var parts = new List<string>();
        if (name.Length > 0)
            parts.Add(name);
        if (tps > 0)
            parts.Add(tps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " tok/s");
        parts.Add(tokens.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " tokens");
        return string.Join(" · ", parts);
    }

    public static string TotalsLine(long turn, long task) => $"Turn: {ContextSnapshot.Tokens(turn)} tokens · Task: {ContextSnapshot.Tokens(task)} tokens";

    // "just now", "3 minutes ago", "2 hours ago", "yesterday", then a date, as the footer under a message shows it.
    public static string RelativeTime(DateTimeOffset time, DateTimeOffset now)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return age.Minutes == 1 ? "1 minute ago" : $"{age.Minutes} minutes ago";
        if (age < TimeSpan.FromHours(24))
            return age.Hours == 1 ? "1 hour ago" : $"{age.Hours} hours ago";
        var days = (now.ToLocalTime().Date - time.ToLocalTime().Date).Days;
        if (days == 1)
            return "yesterday";
        var local = time.ToLocalTime();
        return local.ToString(local.Year == now.ToLocalTime().Year ? "MMM d" : "MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    // The composer's switches, sent with each Chat and Code message: web search, utilities, and the skill folders turned on.
    // Images: photo paths attached to this message (the engine sends them with it only).
    // Vision: the message box's switch; off, the engine shows no image to the model (orchestrator.vision_problem).
    // Emoji: Settings > Appearance's emoji in replies (none, less, standard, more), an instruction the engine adds.
    public sealed record Extras(bool Search, bool Tools, IReadOnlyList<string> Skills, IReadOnlyList<string>? Images = null, bool Vision = true,
        string Emoji = "standard");

    public sealed record Skill(string Name, string Description, string Folder, bool BuiltIn = false);

    // Each folder under root holding a SKILL.md, by name; name and description come from its front matter (name defaults to the folder's).
    // bundledRoot (the app's own skills, <workflow root>\skills) adds its skills as BuiltIn, except a folder name root
    // also has: the user's copy overrides the bundled one.
    public static List<Skill> ListSkills(string root, string? bundledRoot = null)
    {
        var skills = new List<Skill>();
        foreach (var (folder, builtIn) in Folders(root, false).Concat(Folders(bundledRoot, true)))
        {
            if (builtIn && skills.Any(s => string.Equals(Path.GetFileName(s.Folder), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase)))
                continue;
            string text;
            try { text = File.ReadAllText(Path.Combine(folder, "SKILL.md")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var front = Regex.Match(text, @"\A---\s*\n(.*?)\n---", RegexOptions.Singleline);
            string Field(string key) => front.Success && Regex.Match(front.Groups[1].Value, $@"(?m)^{key}\s*:\s*(.+?)\s*$") is { Success: true } m
                ? m.Groups[1].Value.Trim('\'', '"') : "";
            skills.Add(new(Field("name") is { Length: > 0 } name ? name : Path.GetFileName(folder), Field("description"), folder, builtIn));
        }
        return skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        static IEnumerable<(string, bool)> Folders(string? root, bool builtIn) =>
            root is not null && Directory.Exists(root) ? Directory.GetDirectories(root).Select(f => (f, builtIn)) : [];
    }

    // A model that thinks before answering. With modelsFolder given (null = skip the file check), the model file's own
    // tokenizer.chat_template decides when it can be found and read; otherwise a role whose auto_load sets a non-zero
    // reasoning_budget, or a known reasoning family by name (Qwen3/3.5, QwQ, DeepSeek-R1 and its distills, gpt-oss,
    // Phi-4 reasoning, Magistral, GLM-4.5+ / Z1).
    // ponytail: name list, extend it as reasoning families appear.
    public static bool IsThinkingModel(string modelPathOrName, JsonObject? autoLoad, string? modelsFolder = null, string? hubCache = null) =>
        (modelsFolder is null ? null : ThinkingFromTemplate(ReadGgufChatTemplate(ResolveGguf(autoLoad, modelsFolder, hubCache ?? HubCache())))) ??
        (autoLoad?["reasoning_budget"] is JsonValue budget && double.TryParse(budget.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0 ||
        Regex.IsMatch(modelPathOrName, @"qwen3|qwq|deepseek-r1|r1-distill|gpt-oss|phi-4.*reasoning|magistral|glm-(4\.[5-9]|z1)|thinking|reasoning", RegexOptions.IgnoreCase));

    // A thinking model's template has a thinking switch, starts the reply with <think> in its generation prompt, or uses
    // gpt-oss's / Magistral's reasoning markers. "<think>" or reasoning_content elsewhere is not enough: non-thinking
    // models (Qwen3 Instruct 2507) mention them only to strip earlier reasoning from the history.
    internal static bool? ThinkingFromTemplate(string? template)
    {
        if (template is null)
            return null;
        var prompt = template.LastIndexOf("add_generation_prompt", StringComparison.Ordinal);
        return new[] { "enable_thinking", "<|channel|>analysis", "[THINK]" }.Any(m => template.Contains(m, StringComparison.Ordinal)) ||
            prompt >= 0 && template.IndexOf("<think>", prompt, StringComparison.Ordinal) >= 0;
    }

    // The role's GGUF file: auto_load.gguf_file when it exists, else the local scan (the Change picker's) for the repo in
    // auto_load.model_path, preferring auto_load.gguf_variant. Null when none is found.
    public static string? ResolveGguf(JsonObject? autoLoad, string modelsFolder, string hubCache)
    {
        if (autoLoad is null)
            return null;
        if (autoLoad["gguf_file"]?.ToString() is { Length: > 0 } file && File.Exists(file))
            return file;
        if (autoLoad["model_path"]?.ToString() is not { Length: > 0 } repo)
            return null;
        var variant = autoLoad["gguf_variant"]?.ToString() ?? "";
        try
        {
            var mine = FindLocalGgufs(modelsFolder, hubCache).Where(g => g.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)).ToList();
            return (mine.FirstOrDefault(g => variant.Length > 0 && g.Variant.Equals(variant, StringComparison.OrdinalIgnoreCase)) ?? mine.FirstOrDefault())?.File;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly Dictionary<string, Dictionary<string, string>?> GgufTemplateCache = new();

    // The GGUF metadata value tokenizer.chat_template; null on any malformed data, I/O error, a missing key or a null path.
    public static string? ReadGgufChatTemplate(string? path) => ReadGgufMetadata(path)?.GetValueOrDefault("tokenizer.chat_template");

    // The GGUF header values the launcher uses (general.architecture, tokenizer.chat_template, <arch>.context_length,
    // general.sampling.*), as text, read by streaming the header (v2/v3, little-endian). Null on any malformed data, I/O
    // error or a null path. Cached per path, size and modification time.
    public static Dictionary<string, string>? ReadGgufMetadata(string? path)
    {
        if (path is null)
            return null;
        try
        {
            var info = new FileInfo(path);
            var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (GgufTemplateCache)
                if (GgufTemplateCache.TryGetValue(key, out var hit))
                    return hit;
            var meta = ScanGgufMetadata(path);
            lock (GgufTemplateCache)
                GgufTemplateCache[key] = meta;
            return meta;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    internal static int GgufTemplateCacheCount { get { lock (GgufTemplateCache) return GgufTemplateCache.Count; } }

    // The <arch>.* values VramEstimate uses.
    private static readonly string[] VramKeys = [".block_count", ".embedding_length", ".attention.head_count", ".attention.head_count_kv",
        ".attention.key_length", ".attention.value_length", ".full_attention_interval", ".expert_count", ".attention.sliding_window",
        ".attention.sliding_window_pattern", ".attention.key_length_swa", ".attention.value_length_swa"];

    // A malformed or unknown entry after the header ends the scan with what was read so far.
    private static Dictionary<string, string>? ScanGgufMetadata(string path)
    {
        const ulong MaxString = 64 << 20;
        Dictionary<string, string>? meta = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var r = new BinaryReader(fs, System.Text.Encoding.UTF8);
            if (r.ReadUInt32() != 0x46554747u || r.ReadUInt32() is not (2 or 3))
                return null;
            r.ReadUInt64();
            var count = r.ReadUInt64();
            if (count > 1_000_000)
                return null;
            // a string's text, or null (bytes skipped) when !keep; throws InvalidDataException on an absurd length
            string? Str(bool keep)
            {
                var len = r.ReadUInt64();
                if (len > MaxString || (long)len > fs.Length - fs.Position)
                    throw new InvalidDataException();
                if (keep)
                    return System.Text.Encoding.UTF8.GetString(r.ReadBytes((int)len));
                fs.Seek((long)len, SeekOrigin.Current);
                return null;
            }
            void Skip(uint type)
            {
                switch (type)
                {
                    case 0 or 1 or 7: fs.Seek(1, SeekOrigin.Current); break;
                    case 2 or 3: fs.Seek(2, SeekOrigin.Current); break;
                    case 4 or 5 or 6: fs.Seek(4, SeekOrigin.Current); break;
                    case 10 or 11 or 12: fs.Seek(8, SeekOrigin.Current); break;
                    case 8: Str(false); break;
                    case 9:
                        var elem = r.ReadUInt32();
                        var n = r.ReadUInt64();
                        if (n > (ulong)fs.Length || elem == 9) // nested arrays are not used by llama.cpp
                            throw new InvalidDataException();
                        for (ulong i = 0; i < n; i++)
                            Skip(elem);
                        break;
                    default: throw new InvalidDataException();
                }
            }
            meta = new Dictionary<string, string>();
            for (ulong i = 0; i < count; i++)
            {
                var name = Str(true)!;
                var type = r.ReadUInt32();
                var wanted = name is "general.architecture" or "tokenizer.chat_template" or "general.file_type" or "tokenizer.ggml.model" || name.EndsWith(".context_length", StringComparison.Ordinal) ||
                    name.StartsWith("general.sampling.", StringComparison.Ordinal) || VramKeys.Any(k => name.EndsWith(k, StringComparison.Ordinal));
                // Arrays: the vocabulary's size as "<name>.count"; a per-layer integer or bool list (hybrid models'
                // KV heads, a sliding-window pattern) as "a,b,c", bools as 1 and 0.
                if (type == 9 && (wanted || name == "tokenizer.ggml.tokens"))
                {
                    var elem = r.ReadUInt32();
                    var n = r.ReadUInt64();
                    if (n > (ulong)fs.Length)
                        throw new InvalidDataException();
                    if (name == "tokenizer.ggml.tokens")
                        meta[name + ".count"] = n.ToString(CultureInfo.InvariantCulture);
                    var ints = elem is 4 or 5 or 7 && n <= 4096 ? new List<string>() : null;
                    for (ulong j = 0; j < n; j++)
                        if (ints is null)
                            Skip(elem);
                        else
                            ints.Add(elem switch { 4 => r.ReadUInt32().ToString(CultureInfo.InvariantCulture), 5 => r.ReadInt32().ToString(CultureInfo.InvariantCulture), _ => r.ReadByte() != 0 ? "1" : "0" });
                    if (ints is not null && wanted)
                        meta[name] = string.Join(",", ints);
                    continue;
                }
                string? value = !wanted ? null : type switch
                {
                    8 => Str(true),
                    4 => r.ReadUInt32().ToString(CultureInfo.InvariantCulture),
                    5 => r.ReadInt32().ToString(CultureInfo.InvariantCulture),
                    10 => r.ReadUInt64().ToString(CultureInfo.InvariantCulture),
                    11 => r.ReadInt64().ToString(CultureInfo.InvariantCulture),
                    6 => r.ReadSingle().ToString(CultureInfo.InvariantCulture),
                    12 => r.ReadDouble().ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (value is not null)
                    meta[name] = value;
                else
                    Skip(type);
            }
            return meta;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return meta; }
    }

    // worker: the one worker that answers (a built-in role id or a custom worker's Id); it may delegate parts to the others.
    // title: ask the answering model for a short conversation title after the reply (a new conversation's first message).
    // effort: thinking effort for every model call of the turn ("" = the model's default; off, low, medium, high, max).
    // summary: the engine's stored summary of the first summaryCovers messages, which are then left out ("": none).
    public static string BuildChatRequest(string worker, IEnumerable<ChatMessage> messages, Extras? extras = null, bool title = false,
        string effort = "", string summary = "", int summaryCovers = 0) =>
        JsonSerializer.Serialize(new
        {
            worker,
            messages = messages.Skip(summary.Length > 0 ? summaryCovers : 0).Select(m => new { role = m.Role, content = m.Content }),
            search = extras?.Search ?? false,
            tools = extras?.Tools ?? false,
            skills = extras?.Skills ?? [],
            images = extras?.Images ?? [],
            vision = extras?.Vision ?? true,
            emoji = extras?.Emoji ?? "standard",
            title,
            effort,
            summary,
            summary_covers = summary.Length > 0 ? summaryCovers : 0,
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

    // ---- code: like chat, but the worker gets file tools over folders[0] (the project, always write) and the extra folders

    public sealed record CodeFolder(string Path, string Access); // Access: "read" | "write"

    // action "chat" (messages end with the user's) or "write_project" (the conversation so far, possibly empty).
    public static string BuildCodeRequest(string action, string worker, IEnumerable<ChatMessage> messages, string projectFolder, IEnumerable<CodeFolder> extraFolders,
        Extras? extras = null, bool title = false, string effort = "", string summary = "", int summaryCovers = 0,
        string engine = "", string session = "", string piHome = "", bool sandboxNetwork = false, string permissions = "safe") =>
        JsonSerializer.Serialize(new
        {
            action = action == "write_project" ? "write_project" : "chat",
            engine = engine == "pi" ? "pi" : "",
            sandbox = new { network = sandboxNetwork, home = SandboxHome }, // run_command's AppContainer (sandbox.py)
            permissions = permissions is "ask" or "all" ? permissions : "safe", // when run_command asks first (orchestrator.command_runner)
            session, // the conversation id: the Pi engine keeps one Pi session per conversation
            pi_home = piHome,
            worker,
            messages = messages.Skip(summary.Length > 0 ? summaryCovers : 0).Select(m => new { role = m.Role, content = m.Content }),
            summary,
            summary_covers = summary.Length > 0 ? summaryCovers : 0,
            folders = extraFolders.Select(f => new { path = f.Path, access = f.Access == "write" ? "write" : "read" })
                .Prepend(new { path = projectFolder, access = "write" }),
            search = extras?.Search ?? false,
            tools = extras?.Tools ?? false,
            skills = extras?.Skills ?? [],
            images = extras?.Images ?? [],
            vision = extras?.Vision ?? true,
            emoji = extras?.Emoji ?? "standard",
            title,
            effort,
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static ProcessStartInfo BuildCodeStartInfo(string python, string workflowRoot, string configFolder, string apiKey, string requestFile)
    {
        var info = NewStartInfo(python, workflowRoot, apiKey);
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(configFolder);
        info.ArgumentList.Add("--events");
        info.ArgumentList.Add("--code");
        info.ArgumentList.Add(requestFile);
        return info;
    }

    // The legacy one-conversation-per-folder file (<data>\conversations\<hash>.json); ConversationStore migrates it.
    public sealed class CodeConversation
    {
        public string Folder { get; set; } = "";
        public List<CodeFolder> Folders { get; set; } = new();
        public string Worker { get; set; } = "implementer";
        public List<ChatMessage> Messages { get; set; } = new();
        public DateTime Updated { get; set; }
    }

    // The legacy file's name for a folder.
    public static string ConversationPath(string folder, string? dataRoot = null)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(NormalizeFolder(folder).ToLowerInvariant())));
        return Path.Combine(dataRoot ?? AppSettings.DataRoot, "conversations", hash[..16].ToLowerInvariant() + ".json");
    }

    private static string NormalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    // True when folder is the workflow root, inside it, or contains it: such a folder must never be writable by a Code turn.
    public static bool OverlapsWorkflowRoot(string folder, string? workflowRoot)
    {
        if (workflowRoot is null || folder.Length == 0)
            return false;
        var (a, b) = (NormalizeFolder(folder), NormalizeFolder(workflowRoot));
        static bool Within(string inner, string outer) => inner.StartsWith(outer.EndsWith(Path.DirectorySeparatorChar) ? outer : outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || Within(a, b) || Within(b, a);
    }

    // True when configPath is the workflow root's config.json: the template new projects are initialized from.
    public static bool IsTemplateConfig(string configPath, string? workflowRoot) =>
        workflowRoot is not null && configPath.Length > 0 && string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(Path.Combine(workflowRoot, "config.json")), StringComparison.OrdinalIgnoreCase);

    // The launcher's own worker setup: <data>\worker-config.json, made from the template when missing. Presets, workers
    // and role settings work in it with no project; new projects and project-less chats start from it.
    public static string EnsureWorkerConfig(string workflowRoot, string? dataRoot = null)
    {
        dataRoot ??= AppSettings.DataRoot;
        var path = Path.Combine(dataRoot, "worker-config.json");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(dataRoot);
            File.Copy(Path.Combine(workflowRoot, "config.json"), path);
        }
        return path;
    }

    // The config worker changes go to: the project's config.json when it has one (never the template), else the worker config.
    public static string ActiveConfigPath(string project, string? workflowRoot, string? dataRoot = null)
    {
        var projectConfig = project.Length > 0 ? Path.Combine(project, "config.json") : "";
        if (projectConfig.Length > 0 && File.Exists(projectConfig) && !IsTemplateConfig(projectConfig, workflowRoot))
            return projectConfig;
        return workflowRoot is null ? "" : EnsureWorkerConfig(workflowRoot, dataRoot);
    }

    // The folder a chat runs in. An initialized project is used as is. Otherwise the worker config is copied to a
    // fresh temp folder (the template itself is never written) and gets the custom workers; the caller deletes it
    // when the chat ends. IsTemp says whether the folder was created here (and so is the caller's to delete); a real
    // project folder is never IsTemp. Returns "Title: reason" for each custom worker left out.
    public sealed record ChatFolder(string Path, bool IsTemp);

    public static ChatFolder PrepareChatFolder(string selectedProject, string workflowRoot, IEnumerable<WorkerSetting> workers, out string[] skipped, string? dataRoot = null)
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
            File.Copy(EnsureWorkerConfig(workflowRoot, dataRoot), copy);
            skipped = WriteCustomWorkers(copy, workers, Path.Combine(workflowRoot, "config.json"));
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

    // ---- local runtime: llama.cpp's llama-server instead of Unsloth Studio

    public const string LlamaRuntime = "llamacpp";
    // Recent releases, newest first: llama.cpp publishes its builds (b1234) as prereleases, and "latest" can be a tag
    // without any binaries.
    public const string LlamaReleaseUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20";
    public static string LlamaFolder => Path.Combine(AppSettings.DataRoot, "llama.cpp");
    public static string LlamaServerPath => Path.Combine(LlamaFolder, "llama-server.exe");
    // The engine keeps one llama-server and records it here: {"pid", "port", "signature", "model_file", "log"}.
    public static string LlamaStateFile => Path.Combine(Path.GetTempPath(), "agent-orchestrator-llama.json");

    private static readonly string[] RuntimeVariables = ["AGENT_ORCHESTRATOR_BACKEND", "AGENT_ORCHESTRATOR_LLAMA_SERVER", "AGENT_ORCHESTRATOR_GGUF_DIRS"];

    // Tells an orchestrator process which runtime to use. Unsloth: none of the variables (even ones the launcher inherited).
    public static void ApplyRuntimeEnv(ProcessStartInfo info, string runtime, string llamaServer, string? modelsFolder)
    {
        foreach (var name in RuntimeVariables)
            info.Environment.Remove(name);
        if (runtime != LlamaRuntime)
            return;
        info.Environment["AGENT_ORCHESTRATOR_BACKEND"] = LlamaRuntime;
        info.Environment["AGENT_ORCHESTRATOR_LLAMA_SERVER"] = llamaServer;
        if (!string.IsNullOrWhiteSpace(modelsFolder))
            info.Environment["AGENT_ORCHESTRATOR_GGUF_DIRS"] = modelsFolder;
    }

    // ---- Pi (pi.dev), the optional Code engine: installed with npm into <data>\pi, pinned to one tested version.
    public const string PiPackage = "@earendil-works/pi-coding-agent";
    public const string PiVersion = "1.4.0";
    public static string PiFolder => Path.Combine(AppSettings.DataRoot, "pi");
    public static string PiCli => Path.Combine(PiFolder, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
    public static string PiHome => Path.Combine(PiFolder, "home"); // Pi's per-turn config and the conversations' sessions
    public static string SandboxHome => Path.Combine(AppSettings.DataRoot, "sandbox"); // run_command's grant records and temp folders

    // The installed Pi's version (its package.json), or null when Pi is not installed.
    public static string? InstalledPiVersion()
    {
        try
        {
            var package = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(PiCli)!)!, "package.json");
            return File.Exists(PiCli) ? JsonNode.Parse(File.ReadAllText(package))?["version"]?.ToString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // "Version 1.1.0 (abc1234)": the csproj Version and the commit the SDK appends to the informational version.
    public static string VersionText(string? informational = null)
    {
        informational ??= typeof(LauncherLogic).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        var parts = informational.Split('+', 2);
        return $"Version {parts[0]}" + (parts.Length == 2 && parts[1].Length >= 7 ? $" ({parts[1][..7]})" : "");
    }

    // "v22.19.0" or newer (Pi's minimum Node.js).
    public static bool NodeVersionOk(string version) =>
        Version.TryParse(version.TrimStart('v').Split('-')[0], out var v) && v >= new Version(22, 19);

    // PATH after an install: the machine then user registry entries (expanded), then this process's other entries, without
    // duplicates (case-insensitive, a trailing \ ignored).
    public static string MergePath(string? current, string? machine, string? user)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return string.Join(';', $"{machine};{user};{current}".Split(';').Select(p => Environment.ExpandEnvironmentVariables(p.Trim()))
            .Where(p => p.Length > 0 && seen.Add(p.TrimEnd('\\'))));
    }

    // Points an orchestrator process at the installed Pi (the engine's AGENT_ORCHESTRATOR_PI); never an inherited value.
    public static void ApplyPiEnv(ProcessStartInfo info, string piCli)
    {
        info.Environment.Remove("AGENT_ORCHESTRATOR_PI");
        if (File.Exists(piCli))
            info.Environment["AGENT_ORCHESTRATOR_PI"] = piCli;
    }

    public sealed record LlamaAssets(string Tag, string Flavour, List<string> Urls);

    // The newest release that has a Windows x64 build for this machine: with an NVIDIA GPU the highest CUDA version the driver
    // supports (from nvidia-smi's "CUDA Version: X.Y"; unreadable: 12.x before 13.x) that has its cudart runtime zip, else
    // Vulkan, else CPU. get fetches a URL's text (the GitHub API; tests pass sample JSON).
    public static async Task<LlamaAssets> FetchLlamaAssetsAsync(Func<string, Task<string>> get, bool nvidia, string? nvidiaSmi = null)
    {
        using var doc = JsonDocument.Parse(await get(LlamaReleaseUrl));
        var releases = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var release in releases.Where(r => !(r.TryGetProperty("draft", out var draft) && draft.GetBoolean())))
        {
            try { return PickLlamaAssets(release.GetRawText(), nvidia, nvidiaSmi); }
            catch (InvalidOperationException) { } // a release without Windows binaries: try the next one
        }
        throw new InvalidOperationException("No recent llama.cpp release has a Windows x64 build.");
    }

    public static LlamaAssets PickLlamaAssets(string releaseJson, bool nvidia, string? nvidiaSmi = null)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var assets = doc.RootElement.GetProperty("assets").EnumerateArray().ToDictionary(
            a => a.GetProperty("name").GetString() ?? "", a => a.GetProperty("browser_download_url").GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        static string Cudart(string version) => $"cudart-llama-bin-win-cuda-{version}-x64.zip";
        var driver = Regex.Match(nvidiaSmi ?? "", @"CUDA Version:\s*(\d+\.\d+)") is { Success: true } d ? Version.Parse(d.Groups[1].Value) : null;
        if (nvidia && assets.Keys.Select(n => Regex.Match(n, $@"^llama-{Regex.Escape(tag)}-bin-win-cuda-(\d+(?:\.\d+)+)-x64\.zip$", RegexOptions.IgnoreCase))
                .Where(m => m.Success && assets.ContainsKey(Cudart(m.Groups[1].Value)) && (driver is null || Version.Parse(m.Groups[1].Value) <= driver))
                .OrderByDescending(m => driver is null && Version.Parse(m.Groups[1].Value).Major == 12).ThenByDescending(m => Version.Parse(m.Groups[1].Value))
                .FirstOrDefault() is { } cuda)
            return new(tag, $"CUDA {cuda.Groups[1].Value}", [assets[cuda.Value], assets[Cudart(cuda.Groups[1].Value)]]);
        foreach (var (kind, flavour) in new[] { ("vulkan", "Vulkan"), ("cpu", "CPU") })
            if (assets.TryGetValue($"llama-{tag}-bin-win-{kind}-x64.zip", out var url))
                return new(tag, flavour, [url]);
        throw new InvalidOperationException($"llama.cpp release {tag} has no Windows x64 build.");
    }

    // An NVIDIA driver installs nvidia-smi.exe in System32 (older ones elsewhere on PATH).
    public static bool HasNvidiaGpu() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Prepend(Environment.SystemDirectory)
            .Any(d => d.Trim().Length > 0 && File.Exists(Path.Combine(d.Trim().Trim('"'), "nvidia-smi.exe")));

    // nvidia-smi's text (it names the highest CUDA version the driver supports), or null when it cannot be run.
    public static string? ReadNvidiaSmi()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            var output = process?.StandardOutput.ReadToEndAsync();
            return process is not null && process.WaitForExit(5000) ? output!.GetAwaiter().GetResult() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    // The first GPU's total memory in bytes, or null: nvidia-smi, else llama-server's own device list (Vulkan on an Intel or
    // AMD GPU, where an integrated GPU reports the shared memory it may use).
    public static long? GpuMemoryBytes() => NvidiaMemoryBytes() ?? LlamaDeviceMemoryBytes(LlamaServerPath);

    // llama-server --list-devices: "  Vulkan0: Intel(R) Arc(TM) 140V GPU (16GB) (18390 MiB, 17000 MiB free)"; the first device.
    public static long? ParseDeviceMemory(string listing) =>
        Regex.Match(listing, @"\((\d+) MiB, \d+ MiB free\)") is { Success: true } m && long.TryParse(m.Groups[1].Value, out var mib) ? mib << 20 : null;

    private static long? LlamaDeviceMemoryBytes(string server)
    {
        if (!File.Exists(server))
            return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(server, "--list-devices")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return ParseDeviceMemory(output.GetAwaiter().GetResult() + "\n" + errors.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private static long? NvidiaMemoryBytes()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            var output = process?.StandardOutput.ReadToEndAsync();
            return process is not null && process.WaitForExit(5000) &&
                long.TryParse(output!.GetAwaiter().GetResult().Split('\n')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) ? mib << 20 : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    // Removes the folders an interrupted install left beside target. If target itself is missing (the swap died between the
    // two moves) the newest "old" folder is the install: it moves back instead of being deleted.
    public static void CleanLlamaLeftovers(string target)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(target));
        if (parent is null || !Directory.Exists(parent))
            return;
        var name = Path.GetFileName(target);
        void Delete(string dir)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var dir in Directory.GetDirectories(parent, $"{name}.new-*"))
            Delete(dir);
        var olds = Directory.GetDirectories(parent, $"{name}.old-*").OrderByDescending(Directory.GetLastWriteTimeUtc).ToList();
        if (!Directory.Exists(target) && olds.Count > 0)
        {
            try { Directory.Move(olds[0], target); olds.RemoveAt(0); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // keep them all
        }
        foreach (var dir in olds)
            Delete(dir);
    }

    // Unpacks the zips into a fresh folder beside target, every file flattened to its root (a build that nests
    // llama-server.exe still works), checks llama-server.exe is there and writes version.txt, then swaps it in: the old
    // folder is moved aside and deleted only once the new one is in place, and moved back if that fails.
    // Entry names are cut to their last segment ("..\x.dll" is x.dll) and must still land inside the new folder.
    // moveDirectory is a test seam for a swap that fails.
    public static void InstallLlamaServer(IEnumerable<string> zips, string target, string version, Action<string, string>? moveDirectory = null)
    {
        moveDirectory ??= Directory.Move;
        CleanLlamaLeftovers(target);
        var fresh = $"{target}.new-{Guid.NewGuid():N}";
        var old = $"{target}.old-{Guid.NewGuid():N}";
        var installed = false;
        try
        {
            Directory.CreateDirectory(fresh);
            foreach (var zip in zips)
                using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
                    foreach (var entry in archive.Entries)
                    {
                        var name = entry.FullName[(entry.FullName.LastIndexOfAny(['/', '\\']) + 1)..];
                        var destination = Path.GetFullPath(Path.Combine(fresh, name));
                        if (name.Length > 0 && destination.StartsWith(Path.GetFullPath(fresh) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, destination, overwrite: true);
                    }
            if (!File.Exists(Path.Combine(fresh, "llama-server.exe")))
                throw new InvalidOperationException("The download has no llama-server.exe.");
            File.WriteAllText(Path.Combine(fresh, "version.txt"), version);
            if (Directory.Exists(target))
                moveDirectory(target, old);
            try { moveDirectory(fresh, target); }
            catch
            {
                if (Directory.Exists(old) && !Directory.Exists(target))
                    moveDirectory(old, target);
                throw;
            }
            installed = true;
        }
        finally
        {
            foreach (var dir in installed ? new[] { fresh, old } : [fresh]) // a failed swap never deletes the old install
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // The pid in the engine's state file; null when the file is missing or unreadable.
    public static int? ReadLlamaPid(string stateFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(stateFile));
            return doc.RootElement.GetProperty("pid").GetInt32();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
                                       InvalidOperationException or FormatException) { return null; }
    }

    // The live llama-server the state file names, or null. A pid that is gone or now belongs to another program is never ours.
    public static Process? RecordedLlamaServer(string stateFile)
    {
        if (ReadLlamaPid(stateFile) is not { } pid)
            return null;
        try
        {
            var process = Process.GetProcessById(pid);
            if (IsLlamaServer(process))
                return process;
            process.Dispose();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { } // not running
        return null;
    }

    private static bool IsLlamaServer(Process process) => string.Equals(process.ProcessName, "llama-server", StringComparison.OrdinalIgnoreCase);

    // Kills the recorded llama-server (its whole tree) and deletes the state file; the file stays if the kill failed.
    // True when a server was stopped.
    public static bool StopLlamaServer(string stateFile)
    {
        var stopped = false;
        if (RecordedLlamaServer(stateFile) is { } server)
            using (server)
            {
                try { server.Kill(entireProcessTree: true); server.WaitForExit(10000); stopped = true; } // its files stay locked until it exits
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
            }
        try { File.Delete(stateFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return stopped;
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

    // Complete: STATE.md's last "## " section is the orchestrator's "## Project complete" (planning a new task removes
    // it) and no task is checkpointed.
    public static bool IsProjectComplete(string projectRoot)
    {
        try
        {
            var state = Path.Combine(projectRoot, "STATE.md");
            if (File.Exists(Path.Combine(projectRoot, ".ao_checkpoint.json")) || !File.Exists(state))
                return false;
            var headings = Regex.Matches(File.ReadAllText(state), @"^## .*$", RegexOptions.Multiline);
            return headings.Count > 0 && headings[^1].Value.TrimEnd() == "## Project complete";
        }
        catch (IOException) { return false; }
    }

    public static string PrimaryLabel(string projectRoot) =>
        IsProjectComplete(projectRoot) ? "Restart Project" : CurrentTaskId(projectRoot) is { } id ? $"Resume Project — {id}" : "Start Task";

    // Restart Project keeps these names in the project folder and sends everything else to the Recycle Bin.
    public static readonly string[] RestartKeeps = ["PROJECT.md", ".git"];

    public static string[] RestartTargets(string projectRoot) =>
        Directory.GetFileSystemEntries(projectRoot)
            .Where(path => !RestartKeeps.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    // Why Restart must not empty this folder, or null: a drive root, the template folder (or a folder holding it),
    // or a folder without PROJECT.md.
    public static string? RestartRefusal(string projectRoot, string? workflowRoot)
    {
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(project) ?? ""), project, StringComparison.OrdinalIgnoreCase))
            return $"{project} is a drive root";
        // Network and removable drives have no Recycle Bin: the shell would delete permanently.
        if (!project.StartsWith(@"\\") && new DriveInfo(Path.GetPathRoot(project)!).DriveType != DriveType.Fixed)
            return $"{project} is not on a local fixed drive, so its files could not go to the Recycle Bin";
        if (project.StartsWith(@"\\"))
            return $"{project} is a network path, so its files could not go to the Recycle Bin";
        if (workflowRoot is not null)
        {
            var template = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workflowRoot));
            if (string.Equals(template, project, StringComparison.OrdinalIgnoreCase) ||
                template.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return $"{project} holds the Agent Orchestrator template, not a project";
        }
        return File.Exists(Path.Combine(project, "PROJECT.md")) ? null : $"{project} has no PROJECT.md";
    }

    // Recycles every restart target in turn and stops at the first one that fails or is still there afterwards
    // (items recycled before it stay in the Recycle Bin). Returns that path and the reason, or null when all went.
    public static (string Path, string Error)? RecycleForRestart(string projectRoot, Action<string> recycle)
    {
        foreach (var path in RestartTargets(projectRoot))
        {
            try { recycle(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { return (path, e.Message); }
            if (File.Exists(path) || Directory.Exists(path))
                return (path, "it is still there after recycling");
        }
        return null;
    }

    public static void Recycle(string path)
    {
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

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

    // A role on a remote OpenAI-compatible API: config.json gives its model id and no Unsloth management_url, so the
    // orchestrator neither loads nor monitors it. The key itself stays in the environment variable KeyEnv.
    public sealed record RemoteRole(string BaseUrl, string Model, string KeyEnv, string Timeout);

    // The Unsloth keys a switch to Remote moves into roles.<role>.local_settings, and a switch back to Local restores.
    private static readonly string[] LocalKeys = ["base_url", "management_url", "api_key_env", "api_key", "expected_model_contains", "auto_load", "display_name", "timeout_seconds"];

    public static ModelInfo RemoteModelInfo(string model) => new(model, "—", "Remote");

    public static bool IsRemote(JsonNode? role) => role?["management_url"] is null && role?["model"] is JsonValue;

    // Whether a run talks to the local Unsloth server (needs its key, can poll its monitor): some role or custom
    // worker in the config has a management_url. With llama.cpp as the local runtime a role that also has auto_load is served
    // by llama-server; one without auto_load expects a model already loaded in Unsloth, so it still counts.
    public static bool UsesUnsloth(JsonElement config, bool llamaRuntime = false)
    {
        bool Has(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("management_url", out _) &&
                                   !(llamaRuntime && e.TryGetProperty("auto_load", out _));
        return config.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Object && roles.EnumerateObject().Any(r => Has(r.Value)) ||
               config.TryGetProperty("custom_workers", out var workers) && workers.ValueKind == JsonValueKind.Array && workers.EnumerateArray().Any(Has);
    }

    public static RemoteRole? ReadRemoteRole(string configPath, string role)
    {
        var settings = JsonNode.Parse(File.ReadAllText(configPath))!["roles"]![role];
        return IsRemote(settings)
            ? new RemoteRole((string?)settings!["base_url"] ?? "", (string?)settings["model"] ?? "", (string?)settings["api_key_env"] ?? "", settings["timeout_seconds"]?.ToJsonString() ?? "")
            : null;
    }

    public static string? ValidateRemoteRole(RemoteRole remote)
    {
        if (!Uri.TryCreate(remote.BaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "Base URL must be an http:// or https:// address, e.g. https://openrouter.ai/api/v1.";
        if (remote.Model.Trim().Length == 0)
            return "Enter the model id the API expects, e.g. openai/gpt-4o-mini.";
        if (!Regex.IsMatch(remote.KeyEnv.Trim(), "^[A-Za-z_][A-Za-z0-9_]*$"))
            return "API key variable must be an environment variable name, e.g. OPENAI_API_KEY (not the key itself).";
        if (remote.KeyEnv.Trim().Equals("UNSLOTH_API_KEY", StringComparison.OrdinalIgnoreCase))
            return "UNSLOTH_API_KEY holds the local Unsloth key; use the remote API's own variable, e.g. OPENAI_API_KEY.";
        if (remote.Timeout.Trim().Length > 0 && !(long.TryParse(remote.Timeout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t is >= 1 and <= 86400))
            return "Timeout must be a whole number of seconds from 1 to 86400 (or empty).";
        return null;
    }

    // remote: switch the role to (or edit) Remote mode; null: switch it back to the local Unsloth flow, restoring the
    // saved local_settings, else the template's (templatePath, only read) values for this role. Error message or null.
    public static string? WriteRemoteRole(string configPath, string role, RemoteRole? remote, string templatePath)
    {
        if (remote is not null && ValidateRemoteRole(remote) is { } problem)
            return problem;
        var root = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        var settings = root["roles"]![role]!.AsObject();
        if (remote is not null)
        {
            if (!IsRemote(settings))
            {
                var saved = new JsonObject();
                foreach (var key in LocalKeys)
                    if (settings[key] is { } value)
                        saved[key] = value.DeepClone();
                settings["local_settings"] = saved;
            }
            foreach (var key in new[] { "management_url", "auto_load", "expected_model_contains", "api_key" })
                settings.Remove(key);
            settings["base_url"] = remote.BaseUrl.Trim().TrimEnd('/');
            settings["model"] = remote.Model.Trim();
            settings["display_name"] = remote.Model.Trim();
            settings["api_key_env"] = remote.KeyEnv.Trim();
            if (long.TryParse(remote.Timeout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var timeout))
                settings["timeout_seconds"] = timeout;
        }
        else if (IsRemote(settings))
        {
            var source = settings["local_settings"] as JsonObject
                ?? JsonNode.Parse(File.ReadAllText(templatePath))?["roles"]?[role] as JsonObject
                ?? throw new InvalidOperationException($"No local settings to restore for {role}.");
            settings.Remove("model");
            foreach (var key in LocalKeys)
                if (source[key] is { } value)
                    settings[key] = value.DeepClone();
                else
                    settings.Remove(key);
            settings.Remove("local_settings");
        }
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return null;
    }

    // A GGUF the llama.cpp runtime can load. Repo "org/name" for a Hugging Face cache file (the engine finds it by repo and
    // variant), "" for a file in the models folder (loaded by its path, auto_load.gguf_file). Variant: the quant, or "".
    public sealed record LocalGguf(string Repo, string Variant, string File);

    // The engine's Hugging Face cache (orchestrator.py gguf_file): HF_HUB_CACHE, else HF_HOME\hub, else ~\.cache\huggingface\hub.
    public static string HubCache() =>
        Environment.GetEnvironmentVariable("HF_HUB_CACHE") is { Length: > 0 } hub ? hub
        : Environment.GetEnvironmentVariable("HF_HOME") is { Length: > 0 } home ? Path.Combine(home, "hub")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "hub");

    private static readonly Regex SplitPart = new(@"-(\d{5})-of-\d{5}\.gguf$", RegexOptions.IgnoreCase);
    private static readonly Regex QuantTail = new(@"[-_. ]((?:UD-)?(?:I?Q\d\w*|TQ\d\w*|BF16|F16|F32|MXFP4\w*))$", RegexOptions.IgnoreCase);

    // llama.cpp's general.file_type values (llama_ftype) as quantization names; "" for one it does not name.
    private static readonly string[] FileTypes = ["F32", "F16", "Q4_0", "Q4_1", "", "", "", "Q8_0", "Q5_0", "Q5_1", "Q2_K", "Q3_K_S", "Q3_K_M", "Q3_K_L",
        "Q4_K_S", "Q4_K_M", "Q5_K_S", "Q5_K_M", "Q6_K", "IQ2_XXS", "IQ2_XS", "Q2_K_S", "IQ3_XS", "IQ3_XXS", "IQ1_S", "IQ4_NL", "IQ3_S", "IQ3_M",
        "IQ2_S", "IQ2_M", "IQ4_XS", "IQ1_M", "BF16", "", "", "", "TQ1_0", "TQ2_0", "MXFP4"];

    public static string QuantOfFileType(string? fileType) =>
        int.TryParse(fileType, out var t) && t >= 0 && t < FileTypes.Length ? FileTypes[t] : "";

    // Whether a model can see images, as orchestrator.vision_problem decides: not with Disable vision on, nor (llama.cpp
    // runtime) when its GGUF has no vision projector beside it. A remote role, an unknown model or a file not found: yes.
    public static bool CanSee(JsonObject? autoLoad, bool llamaRuntime, string modelsFolder, string hubCache)
    {
        if (autoLoad is null)
            return true;
        if ((autoLoad["disable_vision"]?.ToString() ?? "").ToLowerInvariant() is "on" or "true" or "1")
            return false;
        return !llamaRuntime || ResolveGguf(autoLoad, modelsFolder, hubCache) is not { } file || VramEstimate.FileSizes(file).Vision > 0;
    }

    // orchestrator.vision_projector: the smallest *mmproj*.gguf beside the model; in a folder holding other models too, only
    // one whose name carries this model's name (another model's projector makes llama-server exit). Null without.
    public static string? VisionProjector(string modelFile)
    {
        static string Squash(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
        static string Stem(string file) => Squash(Regex.Replace(SplitPart.Replace(Path.GetFileName(file), ".gguf")[..^5], @"[-_. ]+(?:UD-)?(?:I?Q\d\w*|BF16|F16|F32|MXFP4\w*)$", "", RegexOptions.IgnoreCase));
        try
        {
            var files = Directory.EnumerateFiles(Path.GetDirectoryName(modelFile)!, "*.gguf").ToList();
            var found = files.Where(f => Path.GetFileName(f).Contains("mmproj", StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Except(found).Select(Stem).Distinct().Count() > 1)
            {
                var own = Stem(modelFile);
                found = found.Where(f => own.Length > 0 && Squash(Path.GetFileName(f)).Contains(own)).ToList();
            }
            return found.OrderBy(f => new FileInfo(f).Length).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // The quantization a GGUF's file name ends with ("model-Q4_K_M.gguf": Q4_K_M), or "".
    public static string QuantOf(string file) =>
        QuantTail.Match(SplitPart.Replace(Path.GetFileName(file), ".gguf")[..^5]) is { Success: true } m ? m.Groups[1].Value : "";

    // Every loadable GGUF the engine's lookup would find: the models folder (empty = none) and each cached repo's snapshots.
    // Vision projectors (mmproj) and later parts of a split model are skipped, as the engine does.
    public static List<LocalGguf> FindLocalGgufs(string modelsFolder, string hubCache)
    {
        var found = new List<LocalGguf>();
        IEnumerable<string> Files(string folder) => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Contains("mmproj", StringComparison.OrdinalIgnoreCase) && FirstPart(Path.GetFileName(f)))
                .Order(StringComparer.OrdinalIgnoreCase)
            : [];
        static bool FirstPart(string name) => SplitPart.Match(name) is not { Success: true } part || part.Groups[1].Value == "00001";
        static string Variant(string file) => QuantOf(file);
        if (modelsFolder.Length > 0)
            found.AddRange(Files(modelsFolder).Select(f => new LocalGguf("", Variant(f), f)));
        if (Directory.Exists(hubCache))
            foreach (var repoDir in Directory.EnumerateDirectories(hubCache, "models--*").Order(StringComparer.OrdinalIgnoreCase))
            {
                var parts = Path.GetFileName(repoDir)["models--".Length..].Split("--", 2);
                if (parts.Length != 2)
                    continue;
                var snapshots = Path.Combine(repoDir, "snapshots");
                // the same file in several snapshot revisions is one choice
                found.AddRange(Files(snapshots).DistinctBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .Select(f => new LocalGguf($"{parts[0]}/{parts[1]}", Variant(f), f)));
            }
        return found;
    }

    public static readonly string[] BuiltInRoles = ["planner", "implementer", "reviewer"];

    // roles.compactor (orchestrator.py's compactor role): absent or {"same_as": "planner"} summarizes on MAIN's model.
    public const string Compactor = "compactor";
    public static bool CompactorHasOwn(JsonElement roles) =>
        roles.TryGetProperty(Compactor, out var c) && c.ValueKind == JsonValueKind.Object && !c.TryGetProperty("same_as", out _);
    public static bool CompactorHasOwn(JsonObject? roles) => roles?[Compactor] is JsonObject c && c["same_as"] is null;

    // Copies the worker list and, when the config exists, each built-in role's complete roles.<role> object.
    public static WorkerPreset CapturePreset(string name, string configPath, IEnumerable<WorkerSetting> workers)
    {
        var preset = new WorkerPreset
        {
            Name = name,
            Workers = workers.Select(w => w.Clone()).ToList(),
        };
        if (File.Exists(configPath) && JsonNode.Parse(File.ReadAllText(configPath)) is JsonObject root)
        {
            if (root["roles"] is JsonObject roles)
                foreach (var role in BuiltInRoles.Append(Compactor))
                    if (roles[role] is JsonObject stored)
                        preset.Roles[role] = stored.ToJsonString();
            preset.TaskLimits = root["task_limits"] is JsonObject limits ? limits.ToJsonString() : "{}";
        }
        return preset;
    }

    // Replaces only the stored roles.<role> objects and task_limits ("{}" removes it; null leaves it);
    // every other key and role in the config stays. Returns false when there is no config to write to.
    public static bool ApplyPresetRoles(string configPath, WorkerPreset preset)
    {
        if (!File.Exists(configPath))
            return false;
        if (preset.Roles.Count == 0 && preset.TaskLimits is null)
            return true;
        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("config.json is not a JSON object.");
        if (preset.Roles.Count > 0)
        {
            var roles = (root["roles"] ??= new JsonObject()).AsObject();
            foreach (var (role, json) in preset.Roles.Where(r => BuiltInRoles.Contains(r.Key) || r.Key == Compactor))
                roles[role] = JsonNode.Parse(json);
            if (!preset.Roles.ContainsKey(Compactor)) // a preset from before COMPACTOR: compaction on MAIN's model
                roles[Compactor] = new JsonObject { ["same_as"] = "planner" };
        }
        if (preset.TaskLimits is { } stored)
        {
            if (JsonNode.Parse(stored) is JsonObject { Count: > 0 } limits)
                root["task_limits"] = limits;
            else
                root.Remove("task_limits");
        }
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return true;
    }

    // Implementer/reviewer not in addedRoles run as the planner: "same_as": "planner", their other keys kept (custom workers
    // still copy the implementer's connection). An added role loses same_as and, when that leaves it with no model, gets a
    // copy of the planner's settings. Writes only when something changed. keepOwn (a project's config): a not-added role
    // named there with a model of its own (HasOwnModel) is left as it is; returns those roles.
    public static string[] WriteRoleFallbacks(string configPath, IEnumerable<string> addedRoles, IEnumerable<string>? keepOwn = null)
    {
        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("config.json is not a JSON object.");
        var roles = (root["roles"] ??= new JsonObject()).AsObject();
        var before = roles.ToJsonString();
        var kept = new List<string>();
        foreach (var name in BuiltInRoles.Skip(1))
        {
            var role = roles[name] as JsonObject;
            if (!addedRoles.Contains(name) && keepOwn?.Contains(name) == true && HasOwnModel(role, roles["planner"]))
                kept.Add(name);
            else if (!addedRoles.Contains(name))
                (role ?? (JsonObject)(roles[name] = new JsonObject()))["same_as"] = "planner";
            else
            {
                role?.Remove("same_as");
                if (role is not null && (role.ContainsKey("base_url") || role.ContainsKey("model")))
                    continue;
                var copy = roles["planner"]?.DeepClone() as JsonObject ?? new JsonObject();
                foreach (var (key, value) in role?.ToList() ?? [])
                    copy[key] = value?.DeepClone(); // the role's own keys (a chat prompt) stay
                roles[name] = copy;
            }
        }
        if (roles.ToJsonString() != before)
            File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        return [.. kept];
    }

    // The role keys that make a model a role's own: which model, where, and how it is loaded, sampled and waited on.
    private static readonly string[] OwnModelKeys = ["base_url", "model", "auto_load", "sampling", "timeout_seconds"];

    // Whether a built-in role runs a model of its own rather than the planner's: no same_as, and one of OwnModelKeys set
    // (not empty) to something other than the planner's value.
    public static bool HasOwnModel(JsonNode? role, JsonNode? planner) =>
        role is JsonObject o && o["same_as"] is null &&
        OwnModelKeys.Any(k => o[k] is { } value && value is not JsonObject { Count: 0 } && !JsonNode.DeepEquals(value, planner?[k]));

    // The implementer/reviewer in configPath with a model of their own (HasOwnModel); null when it cannot be read.
    public static List<string>? RolesWithOwnModel(string configPath)
    {
        try
        {
            var roles = JsonNode.Parse(File.ReadAllText(configPath))?["roles"];
            return BuiltInRoles.Skip(1).Where(r => HasOwnModel(roles?[r], roles?["planner"])).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // What identifies a built-in role's model (remote model id, or the local load path, variant and file); "" when none.
    public static string ModelKey(JsonNode? role) =>
        role is null ? "" : $"{role["model"]}|{role["auto_load"]?["model_path"]}|{role["auto_load"]?["gguf_variant"]}|{role["auto_load"]?["gguf_file"]}";

    // The orchestrator's task_limits.max_allowed_files when the key is absent.
    public const int DefaultMaxFiles = 5;

    // task_limits in the project's config.json as field text ("" = key absent, so the orchestrator's default).
    public static (string Files, string Work) ReadTaskLimits(string configPath)
    {
        var limits = JsonNode.Parse(File.ReadAllText(configPath))?["task_limits"] as JsonObject;
        static string Text(JsonNode? node) => node is JsonValue ? node.ToJsonString() : "";
        return (Text(limits?["max_allowed_files"]), Text(limits?["max_required_work"]));
    }

    // Both fields must be empty or a whole number of 1 or more; returns an error (nothing written) or null.
    // An empty field removes its key, an emptied task_limits is removed, and every other key stays.
    public static string? WriteTaskLimits(string configPath, string files, string work)
    {
        var values = new[] { ("max_allowed_files", files.Trim()), ("max_required_work", work.Trim()) };
        if (values.Any(v => v.Item2.Length > 0 && !(int.TryParse(v.Item2, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)))
            return "Task limits must be whole numbers, 1 or more (or empty for the default).";
        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("config.json is not a JSON object.");
        if (root["task_limits"] is not JsonObject limits)
            root["task_limits"] = limits = new JsonObject();
        foreach (var (key, text) in values)
            if (text.Length == 0)
                limits.Remove(key);
            else
                limits[key] = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        if (limits.Count == 0)
            root.Remove("task_limits");
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return null;
    }

    // Each built-in role's auto_load.max_seq_length, when set.
    public static List<(string Name, long Context)> RoleContexts(string configPath)
    {
        var roles = JsonNode.Parse(File.ReadAllText(configPath))?["roles"] as JsonObject;
        var contexts = new List<(string, long)>();
        foreach (var role in BuiltInRoles)
            if (roles?[role]?["auto_load"]?["max_seq_length"] is JsonValue value && value.TryGetValue<long>(out var context))
                contexts.Add((char.ToUpperInvariant(role[0]) + role[1..], context));
        return contexts;
    }

    // A non-blocking hint: a task's files (~4,000 tokens each) plus ~8,000 tokens of prompt may not fit a role's context.
    public static string? TaskContextWarning(int files, IEnumerable<(string Name, long Context)> contexts)
    {
        var tight = contexts.Where(c => files * 4000L + 8000 > c.Context).Select(c => $"{c.Name}'s {Math.Round(c.Context / 1024.0)}k").ToList();
        return tight.Count == 0 ? null : $"{files} files per task may not fit {string.Join(" or ", tight)} context";
    }

    public static readonly string[] ReservedTitles = ["PLANNER", "IMPLEMENTER", "REVIEWER", "MAIN", "LEAD", "COMPACTOR"];

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

    // Why the orchestrator would ignore this custom worker (it needs a model), or null. Shared by the Settings dialog and
    // CustomProblems. An empty description or system prompt is fine: the kind's default is written instead.
    public static string? CustomWorkerProblem(WorkerSetting w)
    {
        if (string.IsNullOrWhiteSpace(w.ModelPath))
            return string.IsNullOrWhiteSpace(w.ModelId) ? "no model assigned (Change model)" : "pick the model again (Change model)";
        return null;
    }

    // A custom worker's description and system prompt when it leaves them empty, by kind ("reviewer", else a Specialist).
    // The Models window shows them prefilled.
    public static string DefaultDescription(string kind) => kind == "reviewer"
        ? "A second reviewer: checks each task's changes for bugs, missed requirements and risky edits."
        : "A general implementer: writes and edits code for the task it is given.";

    public static string DefaultSystemPrompt(string kind) => kind == "reviewer"
        ? "You are a code reviewer. Check the change against the task: correctness, missed requirements, broken behaviour and risky "
          + "edits. Report concrete problems with where they are; do not ask for changes the task did not call for."
        : "You are an implementer. Make the change the task asks for, completely and correctly, following the project's existing "
          + "conventions. Keep the change focused, and say briefly what you changed.";

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

    // The implementer's local Unsloth settings: the role itself, or when it is Remote the local_settings it saved,
    // else the template's (templatePath, only read). Null when none is known.
    private static JsonObject? LocalImplementer(JsonObject? implementer, string? templatePath)
    {
        if (!IsRemote(implementer))
            return implementer;
        if (implementer!["local_settings"] is JsonObject saved)
            return saved;
        var template = templatePath is not null && File.Exists(templatePath)
            ? JsonNode.Parse(File.ReadAllText(templatePath))?["roles"]?["implementer"] as JsonObject : null;
        return IsRemote(template) ? template!["local_settings"] as JsonObject : template;
    }

    // What a custom worker copies from the Implementer before its own options: the local timeout_seconds and auto_load
    // (see LocalImplementer) and the sampling, but enable_thinking.
    private static void Inherit(JsonObject entry, JsonObject? implementer, JsonObject? local)
    {
        if (local?["timeout_seconds"] is { } timeout)
            entry["timeout_seconds"] = timeout.DeepClone();
        entry["auto_load"] = local?["auto_load"]?.DeepClone() as JsonObject ?? new JsonObject();
        var sampling = implementer?["sampling"]?.DeepClone() as JsonObject ?? new JsonObject();
        sampling.Remove("enable_thinking");
        entry["sampling"] = sampling;
    }

    // The option values a custom worker inherits for what it leaves empty (as ModelOptions.Read shows them; gguf_variant
    // follows the worker's own model). None when the config cannot be read.
    public static Dictionary<string, string> InheritedOptions(string configPath, string? templatePath)
    {
        try
        {
            var implementer = JsonNode.Parse(File.ReadAllText(configPath))?["roles"]?["implementer"] as JsonObject;
            var entry = new JsonObject();
            Inherit(entry, implementer, LocalImplementer(implementer, templatePath));
            var options = ModelOptions.Read(entry);
            options.Remove("gguf_variant");
            return options;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }

    // Rewrites the top-level custom_workers array (every other key stays). Each worker copies the Implementer's
    // local connection settings and auto_load defaults (see LocalImplementer) and its sampling, then applies its own
    // model and settings.
    // Workers the orchestrator would ignore are not written; returns "Title: reason" for each.
    // A missing implementer just means nothing to copy.
    public static string[] WriteCustomWorkers(string configPath, IEnumerable<WorkerSetting> workers, string? templatePath = null)
    {
        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("config.json is not a JSON object.");
        var implementer = root["roles"]?["implementer"] as JsonObject;
        var local = LocalImplementer(implementer, templatePath);
        var skipped = new List<string>();
        var array = new JsonArray();
        var all = workers.ToList();
        var problems = CustomProblems(all, !string.IsNullOrWhiteSpace(local?["base_url"]?.ToString()));
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
            var kind = w.Kind == "reviewer" ? "reviewer" : "specialist";
            var entry = new JsonObject
            {
                ["id"] = w.Id, ["title"] = w.Title.Trim(), ["kind"] = kind,
                ["description"] = string.IsNullOrWhiteSpace(w.Description) ? DefaultDescription(kind) : w.Description,
                ["system_prompt"] = string.IsNullOrWhiteSpace(w.SystemPrompt) ? DefaultSystemPrompt(kind) : w.SystemPrompt,
            };
            foreach (var key in new[] { "base_url", "management_url", "api_key_env", "api_key" })
                if (local?[key] is { } value)
                    entry[key] = value.DeepClone();
            entry["expected_model_contains"] = w.ModelExpected ?? w.ModelId ?? "";
            entry["display_name"] = w.ModelId ?? w.ModelExpected ?? "";
            Inherit(entry, implementer, local);
            var load = (JsonObject)entry["auto_load"]!;
            load["model_path"] = w.ModelPath;
            if (string.IsNullOrEmpty(w.ModelVariant))
                load.Remove("gguf_variant");
            else
                load["gguf_variant"] = w.ModelVariant;
            if (string.IsNullOrEmpty(w.GgufFile))
                load.Remove("gguf_file");
            else
                load["gguf_file"] = w.GgufFile;
            ModelOptions.Apply(entry, ModelOptions.Effective(w), includeLoad: true); // the worker's own values; the rest stay the Implementer's
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

    // Dragging the title bar of a maximized window restores it to its pre-maximize size (restore; fallback only when
    // that is empty) with the cursor at the same fraction of the bar's width and the same height within the bar, kept
    // on the cursor's monitor work area but never leaving the cursor outside the restored title bar. All in DIPs.
    public static System.Windows.Rect DragRestoreBounds(System.Windows.Point cursor, System.Windows.Point inBar, double barWidth,
        double barHeight, System.Windows.Rect restore, System.Windows.Rect workArea, System.Windows.Size fallback)
    {
        var size = restore.IsEmpty ? fallback : restore.Size;
        var left = cursor.X - size.Width * Math.Clamp(inBar.X / barWidth, 0, 1);
        left = Math.Max(Math.Min(left, workArea.Right - size.Width), workArea.Left);
        left = Math.Clamp(left, cursor.X - size.Width, cursor.X);
        var top = Math.Max(cursor.Y - Math.Clamp(inBar.Y, 0, barHeight), workArea.Top);
        top = Math.Clamp(top, cursor.Y - barHeight, cursor.Y);
        return new System.Windows.Rect(left, top, size.Width, size.Height);
    }

    // The local runtime: asset picking (no network: the fetch is a lambda), the child environment, the state file, install.
    private static void LlamaSelfTest(string root)
    {
        // The GPU meter's maximum without nvidia-smi: llama-server's device list (a name with parentheses of its own).
        if (ParseDeviceMemory("Available devices:\n  Vulkan0: Intel(R) Arc(TM) 140V GPU (16GB) (18390 MiB, 17000 MiB free)\n") != 18390L << 20 ||
            ParseDeviceMemory("Available devices:\n  CUDA0: NVIDIA GeForce RTX 5080 (16275 MiB, 14985 MiB free)") != 16275L << 20 ||
            ParseDeviceMemory("Available devices:\n") is not null)
            throw new InvalidOperationException("llama-server --list-devices was not read.");
        static string Release(params string[] names) => JsonSerializer.Serialize(new
        {
            tag_name = "b6100",
            assets = names.Select(n => new { name = n, browser_download_url = "https://dl/" + n }),
        });
        string[] all =
        [
            "llama-b6100-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-12.4-x64.zip",
            "llama-b6100-bin-win-cuda-13.1-x64.zip", "cudart-llama-bin-win-cuda-13.1-x64.zip",
            "llama-b6100-bin-win-cuda-13.1-arm64.zip", "llama-b6100-bin-ubuntu-x64.zip",
            "llama-b6100-bin-win-vulkan-x64.zip", "llama-b6100-bin-win-cpu-x64.zip",
        ];
        string? asked = null;
        static string Smi(string version) => $"| NVIDIA-SMI 570.00   Driver Version: 570.00   CUDA Version: {version} |";
        // The release list: a binary-less "latest" tag and a draft come first and are skipped for the newest build.
        var list = $"[{{\"tag_name\":\"v0.8.0\",\"assets\":[{{\"name\":\"nightly-tag.txt\",\"browser_download_url\":\"https://dl/n\"}}]}}," +
            $"{JsonSerializer.Serialize(new { tag_name = "b9999", draft = true, assets = new[] { new { name = "llama-b9999-bin-win-cpu-x64.zip", browser_download_url = "https://dl/draft" } } })}," +
            $"{Release(all)}]";
        var cuda = FetchLlamaAssetsAsync(url => { asked = url; return Task.FromResult(list); }, nvidia: true, Smi("13.1")).GetAwaiter().GetResult();
        try
        {
            FetchLlamaAssetsAsync(_ => Task.FromResult("[{\"tag_name\":\"v0.8.0\",\"assets\":[]}]"), nvidia: false).GetAwaiter().GetResult();
            throw new InvalidOperationException("A release list without Windows builds must be refused.");
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("No recent llama.cpp release")) { }
        if (asked != LlamaReleaseUrl || cuda.Tag != "b6100" || cuda.Flavour != "CUDA 13.1" ||
            !cuda.Urls.SequenceEqual(["https://dl/llama-b6100-bin-win-cuda-13.1-x64.zip", "https://dl/cudart-llama-bin-win-cuda-13.1-x64.zip"]))
            throw new InvalidOperationException($"CUDA asset picking failed: {cuda.Flavour} {string.Join(", ", cuda.Urls)}");
        var noCudart = PickLlamaAssets(Release(all.Where(n => n != "cudart-llama-bin-win-cuda-13.1-x64.zip").ToArray()), nvidia: true, Smi("13.1"));
        var vulkan = PickLlamaAssets(Release(all), nvidia: false);
        var cpu = PickLlamaAssets(Release(all.Where(n => !n.Contains("vulkan")).ToArray()), nvidia: false);
        var nvidiaNoCuda = PickLlamaAssets(Release(all.Where(n => !n.Contains("cuda")).ToArray()), nvidia: true, Smi("13.1"));
        // The driver's CUDA version caps the build; with no readable version 12.x is preferred over 13.x.
        string[] withThirteenZero = [.. all, "llama-b6100-bin-win-cuda-13.0-x64.zip", "cudart-llama-bin-win-cuda-13.0-x64.zip"];
        string Flavour(string[] names, string? smi) => PickLlamaAssets(Release(names), nvidia: true, smi).Flavour;
        if (Flavour(all, Smi("12.8")) != "CUDA 12.4" || Flavour(withThirteenZero, Smi("13.0")) != "CUDA 13.0" || Flavour(all, Smi("13.2")) != "CUDA 13.1" ||
            Flavour(all, null) != "CUDA 12.4" || Flavour(all, "no driver here") != "CUDA 12.4" || Flavour(all, Smi("11.8")) != "Vulkan")
            throw new InvalidOperationException("CUDA picking must follow the driver's CUDA version.");
        if (noCudart.Flavour != "CUDA 12.4" || vulkan.Flavour != "Vulkan" || vulkan.Urls is not ["https://dl/llama-b6100-bin-win-vulkan-x64.zip"] ||
            cpu.Flavour != "CPU" || cpu.Urls is not ["https://dl/llama-b6100-bin-win-cpu-x64.zip"] || nvidiaNoCuda.Flavour != "Vulkan")
            throw new InvalidOperationException("Vulkan / CPU asset fallback failed.");
        try
        {
            PickLlamaAssets(Release("llama-b6100-bin-ubuntu-x64.zip", "llama-b6100-bin-win-cuda-13.1-arm64.zip"), nvidia: true);
            throw new InvalidOperationException("A release without a Windows x64 build must be refused.");
        }
        catch (InvalidOperationException e) when (e.Message.Contains("no Windows x64 build")) { }

        var local = BuildChatStartInfo("python.exe", root, @"Y:\p", "", @"Y:\req.json");
        local.Environment["AGENT_ORCHESTRATOR_GGUF_DIRS"] = @"Y:\stale"; // inherited from the launcher's own environment
        ApplyRuntimeEnv(local, LlamaRuntime, @"Y:\data\llama.cpp\llama-server.exe", @"Y:\models");
        var unsloth = BuildStartInfo("python.exe", root, @"Y:\p", "k", true);
        unsloth.Environment["AGENT_ORCHESTRATOR_BACKEND"] = "llamacpp";
        ApplyRuntimeEnv(unsloth, "unsloth", @"Y:\data\llama.cpp\llama-server.exe", @"Y:\models");
        var noFolder = BuildCodeStartInfo("python.exe", root, @"Y:\p", "", @"Y:\code.json");
        noFolder.Environment["AGENT_ORCHESTRATOR_GGUF_DIRS"] = @"Y:\stale";
        ApplyRuntimeEnv(noFolder, LlamaRuntime, @"Y:\s.exe", "");
        if (local.Environment["AGENT_ORCHESTRATOR_BACKEND"] != "llamacpp" || local.Environment["AGENT_ORCHESTRATOR_LLAMA_SERVER"] != @"Y:\data\llama.cpp\llama-server.exe" ||
            local.Environment["AGENT_ORCHESTRATOR_GGUF_DIRS"] != @"Y:\models" || RuntimeVariables.Any(unsloth.Environment.ContainsKey) ||
            unsloth.Environment["UNSLOTH_API_KEY"] != "k" || noFolder.Environment.ContainsKey("AGENT_ORCHESTRATOR_GGUF_DIRS") ||
            noFolder.Environment["AGENT_ORCHESTRATOR_LLAMA_SERVER"] != @"Y:\s.exe")
            throw new InvalidOperationException("Runtime environment failed.");

        var area = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-llama-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(area);
            var state = Path.Combine(area, "state.json");
            if (ReadLlamaPid(state) is not null)
                throw new InvalidOperationException("A missing state file must read as no server.");
            File.WriteAllText(state, """{"pid": 4242, "port": 8899, "signature": "s", "model_file": "m.gguf", "log": "l.txt"}""");
            if (ReadLlamaPid(state) != 4242)
                throw new InvalidOperationException("State file parsing failed.");
            foreach (var corrupt in new[] { "{\"pid\": 42", "{\"port\": 8899}", "{\"pid\": \"x\"}", "[]", "" })
            {
                File.WriteAllText(state, corrupt);
                if (ReadLlamaPid(state) is not null)
                    throw new InvalidOperationException($"A corrupt state file must read as no server: {corrupt}");
            }

            // Install: nested files are flattened; a download without llama-server.exe leaves the old install untouched.
            string Zip(string name, params string[] entries)
            {
                var path = Path.Combine(area, name);
                using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
                foreach (var entry in entries)
                    using (var writer = new StreamWriter(archive.CreateEntry(entry).Open()))
                        writer.Write(entry);
                return path;
            }
            var target = Path.Combine(area, "llama.cpp");
            InstallLlamaServer([Zip("a.zip", "build/bin/llama-server.exe", "build/bin/ggml.dll"), Zip("b.zip", "cudart64_12.dll")], target, "b6100\nCUDA 12.4");
            if (!File.Exists(Path.Combine(target, "llama-server.exe")) || !File.Exists(Path.Combine(target, "cudart64_12.dll")) ||
                File.ReadAllText(Path.Combine(target, "version.txt")) != "b6100\nCUDA 12.4" || Directory.GetDirectories(area).Length != 1)
                throw new InvalidOperationException("llama-server install failed.");
            try
            {
                InstallLlamaServer([Zip("c.zip", "readme.txt")], target, "b6200\nCPU");
                throw new InvalidOperationException("An install without llama-server.exe must fail.");
            }
            catch (InvalidOperationException e) when (e.Message.Contains("no llama-server.exe")) { }
            if (File.ReadAllText(Path.Combine(target, "version.txt")) != "b6100\nCUDA 12.4" || Directory.GetDirectories(area).Length != 1)
                throw new InvalidOperationException("A failed install must leave the old one in place.");

            // Zip entries cannot climb out of the install folder or name an absolute path: only the file name counts.
            var hostile = Path.Combine(area, "hostile");
            InstallLlamaServer([Zip("evil.zip", "llama-server.exe", @"..\evil.dll", @"C:\x\y.dll", "../up.dll", "dir/", "x/..")], hostile, "b6100\nCPU");
            if (!File.Exists(Path.Combine(hostile, "evil.dll")) || !File.Exists(Path.Combine(hostile, "y.dll")) || !File.Exists(Path.Combine(hostile, "up.dll")) ||
                File.Exists(Path.Combine(area, "evil.dll")) || File.Exists(Path.Combine(area, "up.dll")) || Directory.GetFiles(hostile).Length != 5)
                throw new InvalidOperationException("A zip entry with a path must extract into the install folder by file name only.");

            // Leftovers of an interrupted install: .new-* is deleted; .old-* is deleted too, unless the install folder is
            // missing, when the newest one is restored.
            var parts = Path.Combine(area, "parts");
            foreach (var (suffix, age) in new[] { (".new-a", 0), (".old-b", 2), (".old-c", 1) })
            {
                Directory.CreateDirectory(parts + suffix);
                File.WriteAllText(Path.Combine(parts + suffix, "id.txt"), suffix);
                Directory.SetLastWriteTimeUtc(parts + suffix, DateTime.UtcNow.AddHours(-age));
            }
            CleanLlamaLeftovers(parts);
            int Leftovers() => Directory.GetDirectories(area, "parts.*-*").Length;
            if (File.ReadAllText(Path.Combine(parts, "id.txt")) != ".old-c" || Leftovers() != 0)
                throw new InvalidOperationException("A missing install folder must be restored from the newest .old-* leftover.");
            Directory.CreateDirectory(parts + ".old-d");
            Directory.CreateDirectory(parts + ".new-e");
            CleanLlamaLeftovers(parts);
            if (File.ReadAllText(Path.Combine(parts, "id.txt")) != ".old-c" || Leftovers() != 0)
                throw new InvalidOperationException("Leftovers beside an install folder must be deleted.");

            // A swap that fails puts the old install back and removes the new one.
            var before = Directory.GetDirectories(area).Order().ToArray();
            try
            {
                InstallLlamaServer([Zip("d.zip", "llama-server.exe")], target, "b6300\nCPU",
                    (from, to) => { if (from.Contains(".new-")) throw new IOException("locked"); Directory.Move(from, to); });
                throw new InvalidOperationException("A failed swap must fail the install.");
            }
            catch (IOException e) when (e.Message == "locked") { }
            if (File.ReadAllText(Path.Combine(target, "version.txt")) != "b6100\nCUDA 12.4" || !Directory.GetDirectories(area).Order().SequenceEqual(before))
                throw new InvalidOperationException("A failed swap must roll back to the old install and leave no stray folders.");
        }
        finally { Directory.Delete(area, recursive: true); }
    }

    public static void SelfTest()
    {
        ModelOptions.SelfTest();
        ContextSnapshot.SelfTest();
        var work = new System.Windows.Rect(0, 0, 1920, 1040);
        var restored = new System.Windows.Rect(300, 200, 1000, 600);
        var fallbackSize = new System.Windows.Size(1400, 860);
        System.Windows.Rect Drag(double x, double y, double inX, double inY, System.Windows.Rect restore, System.Windows.Rect area)
        {
            var r = DragRestoreBounds(new(x, y), new(inX, inY), 1920, 32, restore, area, fallbackSize);
            if (x < r.Left || x > r.Right || y < r.Top || y > r.Top + 32)
                throw new InvalidOperationException($"Drag restore left the cursor ({x},{y}) outside the title bar: {r}");
            return r;
        }
        foreach (var x in new[] { 5.0, 960, 1900 }) // left, centre, right of the bar: same fraction of the restored width
        {
            var r = Drag(x, 10, x, 10, restored, work);
            if (r.Size != restored.Size || Math.Abs((x - r.Left) / r.Width - x / 1920) > 1e-9 || r.Top != 0 || r.Left < 0 || r.Right > 1920)
                throw new InvalidOperationException($"Drag restore at x={x} failed: {r}");
        }
        if (Drag(50, 10, 1900, 10, restored, work).Left != 0 || // would hang off the left edge: clamped onto the work area
            Drag(-960, 10, 960, 10, restored, new(-1920, 0, 1920, 1040)).Left != -1460 || // monitor left of the primary
            Drag(960, 45, 960, 10, restored, new(0, 40, 1920, 1000)).Top != 40 || // taskbar on top
            Drag(960, -3, 960, 2, restored, work).Top != -3 || // cursor above the work area: stays in the bar
            Drag(960, 10, 960, 10, new(0, 0, 2500, 900), work).Left != 0 || // wider than the screen
            Drag(960, 10, 960, 10, System.Windows.Rect.Empty, work).Size != fallbackSize)
            throw new InvalidOperationException("Drag restore clamping or fallback failed.");

        if (FormatElapsed((TimeSpan?)null) != "—" || FormatElapsed(TimeSpan.FromSeconds(65)) != "1:05" ||
            FormatElapsed(TimeSpan.FromSeconds(3725)) != "1:02:05" || FormatElapsed(TimeSpan.FromHours(25)) != "25:00:00")
            throw new InvalidOperationException("Elapsed time formatting failed.");
        var root = FindWorkflowRoot() ?? throw new InvalidOperationException("Workflow root discovery failed.");
        var info = BuildStartInfo("python.exe", root, @"Y:\Example Project", "test-key", true);
        if (!info.ArgumentList.Contains("--once") || !info.ArgumentList.Contains("--non-interactive") || info.ArgumentList.Contains("--request"))
            throw new InvalidOperationException("Argument construction failed.");
        var runArgs = BuildStartInfo("python.exe", root, @"Y:\Example Project", "k", false, @"Y:\run.json").ArgumentList;
        if (runArgs.IndexOf("--request") < 0 || runArgs[runArgs.IndexOf("--request") + 1] != @"Y:\run.json")
            throw new InvalidOperationException("A project run must pass its Code request.");
        if (info.Environment["UNSLOTH_API_KEY"] != "test-key")
            throw new InvalidOperationException("API key environment handling failed.");

        var chatInfo = BuildChatStartInfo("python.exe", root, @"Y:\Example Project", "test-key", @"Y:\req.json");
        var chatArgs = chatInfo.ArgumentList;
        if (chatArgs.IndexOf("--chat") < 0 || chatArgs[chatArgs.IndexOf("--chat") + 1] != @"Y:\req.json" || !chatArgs.Contains("--events") ||
            !chatArgs.Contains("--non-interactive") || chatArgs[chatArgs.IndexOf("--project") + 1] != @"Y:\Example Project" || chatArgs.Contains("--once") ||
            chatInfo.Environment["UNSLOTH_API_KEY"] != "test-key" || chatInfo.Environment["PYTHONUNBUFFERED"] != "1")
            throw new InvalidOperationException("Chat start info failed.");
        var chat = new[] { new ChatMessage("user", "hi \"there\"\nline2 é"), new ChatMessage("assistant", "hello"), new ChatMessage("user", "again") };
        var timed = new[] { new ChatMessage("user", "hi", DateTimeOffset.Now) };
        if (BuildChatRequest("implementer", timed).Contains("time", StringComparison.OrdinalIgnoreCase) ||
            BuildCodeRequest("chat", "implementer", timed, @"Y:\P", []).Contains("time", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A message time reached the engine request.");
        if (LlamaProgressLine("Lead", 4415, 0, 0) != "Lead is reading the conversation: 0 / 4,415 tokens…" ||
            LlamaProgressLine("Lead", 4415, 4415, 1234) != "Lead is writing: 1,234 tokens so far…")
            throw new InvalidOperationException("The llama progress line failed.");
        if (StatsLine("Planner", 42.14, 1234, 5678) !="Planner · 42.1 tok/s · 1,234 generated · 5,678 prompt" || StatsLine("", 0, 5, 7) != "5 generated · 7 prompt" ||
            TotalsLine(12345, 45600) != "Turn: 12.3k tokens · Task: 45.6k tokens" || TotalsLine(950, 1_200_000) != "Turn: 950 tokens · Task: 1.2M tokens" ||
            LiveLine("Planner", 37.5, 1204) != "Planner · 37.5 tok/s · 1,204 tokens" || LiveLine("Planner", 0, 1) != "Planner · 1 tokens")
            throw new InvalidOperationException("Stats formatting failed.");
        var at = new DateTimeOffset(2026, 6, 15, 12, 0, 0, DateTimeOffset.Now.Offset);
        if (RelativeTime(at.AddSeconds(-30), at) != "just now" || RelativeTime(at.AddSeconds(5), at) != "just now" ||
            RelativeTime(at.AddMinutes(-1), at) != "1 minute ago" || RelativeTime(at.AddMinutes(-3), at) != "3 minutes ago" ||
            RelativeTime(at.AddHours(-1), at) != "1 hour ago" || RelativeTime(at.AddHours(-2), at) != "2 hours ago" ||
            RelativeTime(at.AddHours(-23), at) != "23 hours ago" || RelativeTime(at.AddHours(-30), at) != "yesterday" ||
            RelativeTime(at.AddDays(-3), at) != "Jun 12" || RelativeTime(at.AddYears(-1), at) != "Jun 15, 2025")
            throw new InvalidOperationException("Relative time formatting failed.");
        var single = JsonNode.Parse(BuildChatRequest("implementer", chat))!;
        var customId = Guid.NewGuid().ToString("N");
        var custom = JsonNode.Parse(BuildChatRequest(customId, chat.Take(1)))!;
        var sm = single["messages"]!.AsArray();
        if (single["mode"] is not null || (string)single["worker"]! != "implementer" || sm.Count != 3 ||
            (string)sm[0]!["role"]! != "user" || (string)sm[0]!["content"]! != "hi \"there\"\nline2 é" || (string)sm[1]!["role"]! != "assistant" ||
            (string)sm[2]!["content"]! != "again" || (string)custom["worker"]! != customId || custom["messages"]!.AsArray().Count != 1)
            throw new InvalidOperationException("Chat request failed.");

        var codeInfo = BuildCodeStartInfo("python.exe", root, @"Y:\Example Project", "test-key", @"Y:\code.json");
        var codeArgs = codeInfo.ArgumentList;
        if (codeArgs.IndexOf("--code") < 0 || codeArgs[codeArgs.IndexOf("--code") + 1] != @"Y:\code.json" || !codeArgs.Contains("--events") ||
            codeArgs[codeArgs.IndexOf("--project") + 1] != @"Y:\Example Project" || codeArgs.Contains("--chat") || codeInfo.Environment["UNSLOTH_API_KEY"] != "test-key")
            throw new InvalidOperationException("Code start info failed.");
        var code = JsonNode.Parse(BuildCodeRequest("chat", "implementer", chat, @"Y:\app", [new(@"Y:\lib", "read"), new(@"Y:\shared", "write"), new(@"Y:\odd", "bogus")]))!;
        var codeFolders = code["folders"]!.AsArray();
        if ((string)code["action"]! != "chat" || (string)code["worker"]! != "implementer" || code["messages"]!.AsArray().Count != 3 ||
            (string)code["messages"]![0]!["content"]! != "hi \"there\"\nline2 é" || codeFolders.Count != 4 ||
            (string)codeFolders[0]!["path"]! != @"Y:\app" || (string)codeFolders[0]!["access"]! != "write" ||
            (string)codeFolders[1]!["path"]! != @"Y:\lib" || (string)codeFolders[1]!["access"]! != "read" ||
            (string)codeFolders[2]!["access"]! != "write" || (string)codeFolders[3]!["access"]! != "read" ||
            (string)JsonNode.Parse(BuildCodeRequest("write_project", "planner", [], @"Y:\app", []))!["action"]! != "write_project" ||
            JsonNode.Parse(BuildCodeRequest("bogus", "planner", [], @"Y:\app", []))!["folders"]!.AsArray().Count != 1)
            throw new InvalidOperationException("Code request failed.");
        var piRequest = JsonNode.Parse(BuildCodeRequest("chat", "planner", chat, @"Y:\app", [], engine: "pi", session: "c1", piHome: @"Y:\data\pi\home"))!;
        if ((string)code["engine"]! != "" || (string)piRequest["engine"]! != "pi" || (string)piRequest["session"]! != "c1" ||
            (string)piRequest["pi_home"]! != @"Y:\data\pi\home" || (string)JsonNode.Parse(BuildCodeRequest("chat", "planner", chat, @"Y:\app", [], engine: "x"))!["engine"]! != "")
            throw new InvalidOperationException("Code engine request failed.");
        var sandboxed = JsonNode.Parse(BuildCodeRequest("chat", "planner", chat, @"Y:\app", [], sandboxNetwork: true))!;
        if ((bool)code["sandbox"]!["network"]! || !(bool)sandboxed["sandbox"]!["network"]! || (string)sandboxed["sandbox"]!["home"]! != SandboxHome)
            throw new InvalidOperationException("Sandbox request failed.");
        if (VersionText("1.1.0+0123456789abcdef") != "Version 1.1.0 (0123456)" || VersionText("1.1.0") != "Version 1.1.0" ||
            !VersionText().StartsWith("Version 1.1.0"))
            throw new InvalidOperationException("The version text failed.");
        if (!NodeVersionOk("v22.19.0") || !NodeVersionOk("v24.14.1") || !NodeVersionOk("v23.0.0-nightly") || NodeVersionOk("v22.18.9") ||
            NodeVersionOk("v20.11.1") || NodeVersionOk("") || NodeVersionOk("node"))
            throw new InvalidOperationException("The Node.js version check failed.");
        if (MergePath(@"C:\app;C:\Windows\", @"C:\Windows;C:\Program Files\nodejs\", @"%SystemRoot%\x;;") !=
            @"C:\Windows;C:\Program Files\nodejs\;" + Environment.ExpandEnvironmentVariables(@"%SystemRoot%\x") + @";C:\app")
            throw new InvalidOperationException("The PATH merge failed.");
        var withExtras = JsonNode.Parse(BuildChatRequest("implementer", chat, new(true, false, [@"Y:\skills\a"])))!;
        var codeExtras = JsonNode.Parse(BuildCodeRequest("chat", "implementer", chat, @"Y:\app", [], new(false, true, [])))!;
        if ((bool)single["search"]! || (bool)single["tools"]! || single["skills"]!.AsArray().Count != 0 || !(bool)withExtras["search"]! ||
            (bool)withExtras["tools"]! || (string)withExtras["skills"]![0]! != @"Y:\skills\a" || (bool)codeExtras["search"]! || !(bool)codeExtras["tools"]! ||
            (bool)code["tools"]!)
            throw new InvalidOperationException("Request extras failed.");
        if ((bool)single["title"]! || (bool)code["title"]! || !(bool)JsonNode.Parse(BuildChatRequest("implementer", chat, title: true))!["title"]! ||
            !(bool)JsonNode.Parse(BuildCodeRequest("chat", "implementer", chat, @"Y:\app", [], title: true))!["title"]!)
            throw new InvalidOperationException("The first-turn title flag failed.");
        if ((string)single["effort"]! != "" || (string)JsonNode.Parse(BuildChatRequest("x", chat, effort: "high"))!["effort"]! != "high" ||
            (string)JsonNode.Parse(BuildCodeRequest("chat", "implementer", chat, @"Y:\app", [], effort: "off"))!["effort"]! != "off")
            throw new InvalidOperationException("The effort field failed.");
        if (code["mode"] is not null) // the engine runs every Code chat turn as the lead; there is no mode to send
            throw new InvalidOperationException("The Code request still sends a mode.");
        // A stored summary stands in for the messages it covers; none by default.
        foreach (var summed in new[] { BuildChatRequest("implementer", chat, summary: "S", summaryCovers: 2),
                     BuildCodeRequest("chat", "implementer", chat, @"Y:\app", [], summary: "S", summaryCovers: 2) }.Select(r => JsonNode.Parse(r)!))
            if ((string)summed["summary"]! != "S" || (int)summed["summary_covers"]! != 2 || summed["messages"]!.AsArray() is not [{ } only] ||
                (string)only["content"]! != "again")
                throw new InvalidOperationException("The conversation summary failed.");
        if ((string)single["summary"]! != "" || (int)single["summary_covers"]! != 0 || (string)code["summary"]! != "" || (int)code["summary_covers"]! != 0)
            throw new InvalidOperationException("A request without a summary carried one.");
        foreach (var (name, budget, thinks) in new (string, int?, bool)[]
        {
            ("unsloth/Qwen3.5-4B-GGUF", null, true), ("Qwen3-8B", null, true), ("QwQ-32B", null, true), ("deepseek-ai/DeepSeek-R1-0528", null, true),
            ("DeepSeek-R1-Distill-Llama-8B", null, true), ("openai/gpt-oss-20b", null, true), ("Phi-4-mini-reasoning", null, true),
            ("Magistral-Small-2509", null, true), ("GLM-4.5-Air", null, true), ("GLM-Z1-9B", null, true), ("Kimi-K2-Thinking", null, true),
            ("unsloth/Qwen2.5-Coder-3B-Instruct-GGUF", null, false), ("unsloth/Phi-4-mini-instruct-GGUF", null, false), ("GLM-4-9B", null, false),
            ("Qwen2.5-Coder-3B-Instruct", 0, false), ("Qwen2.5-Coder-3B-Instruct", 4096, true), ("Llama-3.1-8B", -1, true), ("Qwen3-8B", 0, true),
        })
            if (IsThinkingModel(name, budget is null ? new JsonObject { ["model_path"] = name } : new JsonObject { ["reasoning_budget"] = budget }) != thinks ||
                budget is null && IsThinkingModel(name, null) != thinks)
                throw new InvalidOperationException($"IsThinkingModel({name}, {budget}) should be {thinks}.");
        // Chat-template detection from the GGUF file itself, with synthetic GGUFs.
        var tplArea = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-tpl-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tplArea);
            string Gguf(string file, string? template, bool truncate = false, bool badMagic = false)
            {
                var p = Path.Combine(tplArea, file);
                using (var fs = File.Create(p))
                using (var w = new BinaryWriter(fs, System.Text.Encoding.UTF8))
                {
                    void S(string v) { w.Write((ulong)System.Text.Encoding.UTF8.GetByteCount(v)); w.Write(System.Text.Encoding.UTF8.GetBytes(v)); }
                    w.Write(badMagic ? 0x12345678u : 0x46554747u); w.Write(3u); w.Write(0UL); w.Write(template is null ? 3UL : 4UL);
                    S("general.name"); w.Write(8u); S("Synthetic");
                    S("general.file_type"); w.Write(4u); w.Write(15u);
                    S("tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write(3UL); S("<s>"); S("hello"); S("</s>");
                    if (template is not null) { S("tokenizer.chat_template"); w.Write(8u); S(template); }
                }
                if (truncate)
                    using (var fs = new FileStream(p, FileMode.Open)) fs.SetLength(60);
                return p;
            }
            JsonObject Load(string file, string name) => new() { ["gguf_file"] = file, ["model_path"] = name };
            var think = Gguf("think.gguf", "{% if enable_thinking %}<think>{% endif %}");
            var plain = Gguf("plain.gguf", "{{ messages }} plain chatml");
            var cut = Gguf("cut.gguf", "<think>", truncate: true);
            var bad = Gguf("bad.gguf", "<think>", badMagic: true);
            var noTpl = Gguf("none.gguf", null);
            if (ReadGgufChatTemplate(think) != "{% if enable_thinking %}<think>{% endif %}" || ReadGgufChatTemplate(noTpl) is not null ||
                ReadGgufChatTemplate(cut) is not null || ReadGgufChatTemplate(bad) is not null || ReadGgufChatTemplate(Path.Combine(tplArea, "missing.gguf")) is not null)
                throw new InvalidOperationException("GGUF chat template read failed.");
            if (!IsThinkingModel("Some-Agent-35B", Load(think, "Some-Agent-35B"), "") || IsThinkingModel("qwen3-fake", Load(plain, "qwen3-fake"), "") ||
                !IsThinkingModel("qwen3-fake", Load(cut, "qwen3-fake"), "") || IsThinkingModel("Some-Agent-35B", Load(cut, "Some-Agent-35B"), "") ||
                !IsThinkingModel("Qwen3-X", Load(bad, "Qwen3-X"), "") || IsThinkingModel("Some-Agent-35B", Load(think, "Some-Agent-35B")))
                throw new InvalidOperationException("Template-driven IsThinkingModel failed.");
            foreach (var marker in new[] { "enable_thinking", "<|channel|>analysis", "[THINK]", "{% if add_generation_prompt %}<|im_start|>assistant\n<think>\n{% endif %}" })
                if (!IsThinkingModel("Plain", Load(Gguf("m.gguf", "a " + marker + " b"), "Plain"), ""))
                    throw new InvalidOperationException($"Marker {marker} not detected.");
            // Qwen3 Instruct 2507: <think> and reasoning_content only to strip earlier reasoning; the reply starts plainly.
            const string instruct = "{% if '</think>' in content %}{% set reasoning_content = content.split('</think>')[0] %}{% endif %}" +
                "{% if add_generation_prompt %}<|im_start|>assistant\n{% endif %}";
            if (IsThinkingModel("qwen3-4b-instruct-2507", Load(Gguf("instruct.gguf", instruct), "qwen3-4b-instruct-2507"), ""))
                throw new InvalidOperationException("A template that only strips <think> from history must not count as thinking.");
            // HF cache layout resolved from model_path + gguf_variant
            var hubFile = Path.Combine(tplArea, "hub", "models--org--Agent-GGUF", "snapshots", "x");
            Directory.CreateDirectory(hubFile);
            File.Copy(think, Path.Combine(hubFile, "Agent-Q4_K_M.gguf"));
            File.Copy(plain, Path.Combine(hubFile, "Agent-Q8_0.gguf"));
            var hub = Path.Combine(tplArea, "hub");
            var viaHub = new JsonObject { ["model_path"] = "org/Agent-GGUF", ["gguf_variant"] = "Q4_K_M" };
            if (Path.GetFileName(ResolveGguf(viaHub, "", hub)) != "Agent-Q4_K_M.gguf" || !IsThinkingModel("org/Agent-GGUF", viaHub, "", hub) ||
                IsThinkingModel("org/Agent-GGUF", new JsonObject { ["model_path"] = "org/Agent-GGUF", ["gguf_variant"] = "Q8_0" }, "", hub) ||
                ResolveGguf(new JsonObject { ["model_path"] = "org/Other" }, "", hub) is not null)
                throw new InvalidOperationException("GGUF resolution failed.");
            // The header values the Model settings window derives its defaults from.
            var metaFile = Path.Combine(tplArea, "meta.gguf");
            using (var fs = File.Create(metaFile))
            using (var w = new BinaryWriter(fs, System.Text.Encoding.UTF8))
            {
                void S(string v) { w.Write((ulong)System.Text.Encoding.UTF8.GetByteCount(v)); w.Write(System.Text.Encoding.UTF8.GetBytes(v)); }
                w.Write(0x46554747u); w.Write(3u); w.Write(0UL); w.Write(10UL);
                S("general.architecture"); w.Write(8u); S("qwen3");
                S("general.sampling.temp"); w.Write(6u); w.Write(0.7f);
                S("tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write(2UL); S("a"); S("b");
                S("qwen3.attention.head_count_kv"); w.Write(9u); w.Write(4u); w.Write(2UL); w.Write(0u); w.Write(8u);
                S("general.sampling.top_k"); w.Write(5u); w.Write(40);
                S("qwen3.context_length"); w.Write(4u); w.Write(262144u);
                S("general.sampling.flag"); w.Write(7u); w.Write((byte)1);
                S("tokenizer.chat_template"); w.Write(8u); S("{{ x }}");
                S("general.future"); w.Write(99u); // an unknown type: the scan stops, keeping what it read
                S("general.sampling.min_p"); w.Write(6u); w.Write(0.1f);
            }
            var read = ReadGgufMetadata(metaFile);
            if (read is null || read.Count != 7 || read["tokenizer.ggml.tokens.count"] != "2" || read["qwen3.attention.head_count_kv"] != "0,8" || read["general.architecture"] != "qwen3" || read["general.sampling.temp"] != "0.7" || read["general.sampling.top_k"] != "40" ||
                read["qwen3.context_length"] != "262144" || read["tokenizer.chat_template"] != "{{ x }}" || ReadGgufMetadata(bad) is not null ||
                ModelOptions.Defaults(read, load: true) is not { } fromFile || fromFile["max_seq_length"] != "32768" || fromFile["temperature"] != "0.7")
                throw new InvalidOperationException("GGUF metadata read failed: " + string.Join(" ", read ?? new()));
            if (GgufTemplateCacheCount == 0 || ReadGgufChatTemplate(think) is null)
                throw new InvalidOperationException("GGUF template cache failed.");
        }
        finally { Directory.Delete(tplArea, recursive: true); }
        var skillArea = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-skills-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(skillArea, "zeta"));
            File.WriteAllText(Path.Combine(skillArea, "zeta", "SKILL.md"), "---\nname: \"Alpha\"\ndescription: Does A\n---\nbody");
            Directory.CreateDirectory(Path.Combine(skillArea, "beta"));
            File.WriteAllText(Path.Combine(skillArea, "beta", "SKILL.md"), "no front matter");
            Directory.CreateDirectory(Path.Combine(skillArea, "empty")); // no SKILL.md: not a skill
            if (ListSkills(skillArea) is not [{ Name: "Alpha", Description: "Does A" } a, { Name: "beta", Description: "" }] ||
                Path.GetFileName(a.Folder) != "zeta" || ListSkills(Path.Combine(skillArea, "missing")).Count != 0)
                throw new InvalidOperationException("Skill listing failed.");
            // Bundled skills join the user's as BuiltIn; a user folder of the same name (any case) overrides the bundled one.
            var bundled = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-bundled-{Guid.NewGuid():N}");
            try
            {
                foreach (var (folder, text) in new[] { ("ZETA", "---\nname: Bundled zeta\n---"), ("gamma", "---\nname: gamma\ndescription: Built in\n---") })
                {
                    Directory.CreateDirectory(Path.Combine(bundled, folder));
                    File.WriteAllText(Path.Combine(bundled, folder, "SKILL.md"), text);
                }
                if (ListSkills(skillArea, bundled) is not [{ Name: "Alpha", BuiltIn: false }, { Name: "beta", BuiltIn: false }, { Name: "gamma", Description: "Built in", BuiltIn: true } g] ||
                    !g.Folder.StartsWith(bundled) || ListSkills(Path.Combine(skillArea, "missing"), bundled) is not [{ Name: "Bundled zeta", BuiltIn: true }, { Name: "gamma" }])
                    throw new InvalidOperationException("Bundled skill merge failed.");
            }
            finally { Directory.Delete(bundled, recursive: true); }
        }
        finally { Directory.Delete(skillArea, recursive: true); }
        var dataArea = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-conv-{Guid.NewGuid():N}");
        try
        {
            var convPath = ConversationPath(@"Y:\Programs\My_App", dataArea);
            var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(@"y:\programs\my_app")))[..16].ToLowerInvariant();
            if (convPath != Path.Combine(dataArea, "conversations", expectedHash + ".json") || ConversationPath(@"y:\programs\my_app", dataArea) != convPath)
                throw new InvalidOperationException("Conversation path failed.");
            if (ConversationPath(@"Y:\Programs\My_App\", dataArea) != convPath || ConversationPath(@"Y:\Programs\My_App\.", dataArea) != convPath)
                throw new InvalidOperationException("A trailing separator must not change the conversation file.");
            var wf = @"Y:\Programs\Agent Setup";
            if (!OverlapsWorkflowRoot(wf, wf) || !OverlapsWorkflowRoot(@"y:\programs\agent setup\", wf) || !OverlapsWorkflowRoot(@"Y:\Programs/Agent Setup/templates", wf) ||
                !OverlapsWorkflowRoot(@"Y:\Programs", wf) || !OverlapsWorkflowRoot(@"Y:\", wf) || OverlapsWorkflowRoot(@"Y:\Programs\Agent Setup2", wf) ||
                OverlapsWorkflowRoot(@"Y:\Programs\My_App", wf) || OverlapsWorkflowRoot(@"Y:\Programs\My_App", null))
                throw new InvalidOperationException("Workflow root overlap failed.");
        }
        finally
        {
            if (Directory.Exists(dataArea))
                Directory.Delete(dataArea, recursive: true);
        }

        LlamaSelfTest(root);

        // Local GGUFs: the models folder and the HF cache layout; mmproj and later split parts skipped, one entry per file name.
        var ggufArea = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-gguf-{Guid.NewGuid():N}");
        try
        {
            void Touch(params string[] parts)
            {
                var file = Path.Combine([ggufArea, .. parts]);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "");
            }
            Touch("models", "sub", "My-Model-Q8_0.gguf");
            Touch("models", "mmproj-F16.gguf");
            Touch("hub", "models--unsloth--Qwen3-4B-GGUF", "snapshots", "r1", "Qwen3-4B-UD-Q4_K_XL.gguf");
            Touch("hub", "models--unsloth--Qwen3-4B-GGUF", "snapshots", "r2", "Qwen3-4B-UD-Q4_K_XL.gguf");
            Touch("hub", "models--unsloth--Qwen3-4B-GGUF", "snapshots", "r1", "mmproj-BF16.gguf");
            Touch("hub", "models--org--Big-GGUF", "snapshots", "r1", "Q4_K_M", "Big-Q4_K_M-00001-of-00002.gguf");
            Touch("hub", "models--org--Big-GGUF", "snapshots", "r1", "Q4_K_M", "Big-Q4_K_M-00002-of-00002.gguf");
            Touch("hub", "models--org--Big-GGUF", "blobs", "x.gguf");
            var ggufs = FindLocalGgufs(Path.Combine(ggufArea, "models"), Path.Combine(ggufArea, "hub"));
            var shown = string.Join("|", ggufs.Select(g => $"{g.Repo}:{g.Variant}:{Path.GetFileName(g.File)}"));
            if (shown != ":Q8_0:My-Model-Q8_0.gguf|org/Big-GGUF:Q4_K_M:Big-Q4_K_M-00001-of-00002.gguf|unsloth/Qwen3-4B-GGUF:UD-Q4_K_XL:Qwen3-4B-UD-Q4_K_XL.gguf" ||
                !Path.IsPathRooted(ggufs[0].File) || FindLocalGgufs("", Path.Combine(ggufArea, "none")).Count != 0)
                throw new InvalidOperationException($"Local GGUF scan failed: {shown}");
        }
        finally
        {
            if (Directory.Exists(ggufArea))
                Directory.Delete(ggufArea, recursive: true);
        }

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
            var rootDir = Path.Combine(testArea, "root");
            var rootConfig = Path.Combine(rootDir, "config.json");
            Directory.CreateDirectory(rootDir);
            File.WriteAllText(rootConfig, "{\"keep\":1,\"roles\":{\"implementer\":{\"base_url\":\"http://h/v1\"}}}");
            var rootBytes = File.ReadAllBytes(rootConfig);
            var chatWorkers = new List<WorkerSetting> { Ok("c1", "Chat Docs") };
            // The worker config: made from the template once, then edited on its own; the template is never written.
            var dataDir = Path.Combine(testArea, "data");
            var workerConfig = Path.Combine(dataDir, "worker-config.json");
            if (ActiveConfigPath("", rootDir, dataDir) != workerConfig || ActiveConfigPath(rootDir, rootDir, dataDir) != workerConfig ||
                ActiveConfigPath(testArea, rootDir, dataDir) != workerConfig || !File.ReadAllBytes(workerConfig).SequenceEqual(rootBytes))
                throw new InvalidOperationException("Without a project config the active config must be a copy of the template in the data folder.");
            File.WriteAllText(workerConfig, "{\"keep\":2,\"roles\":{\"implementer\":{\"base_url\":\"http://h/v1\"}}}");
            if (EnsureWorkerConfig(rootDir, dataDir) != workerConfig || !File.ReadAllText(workerConfig).Contains("\"keep\":2"))
                throw new InvalidOperationException("An existing worker config must be kept.");
            var chatPrepared = PrepareChatFolder(testArea, rootDir, chatWorkers, out _, dataDir);
            var chatFolder = chatPrepared.Path;
            try
            {
                var chatCopy = JsonNode.Parse(File.ReadAllText(Path.Combine(chatFolder, "config.json")))!;
                if (!chatPrepared.IsTemp || chatFolder == rootDir || chatFolder == testArea || (int)chatCopy["keep"]! != 2 || chatCopy["custom_workers"]?.AsArray().Count != 1 ||
                    !File.ReadAllBytes(rootConfig).SequenceEqual(rootBytes) || !IsTemplateConfig(rootConfig.ToUpperInvariant(), rootDir) ||
                    !File.ReadAllText(workerConfig).Contains("\"keep\":2"))
                    throw new InvalidOperationException("Chat folder without a project must be a temp copy of the worker config.");
            }
            finally { Directory.Delete(chatFolder, true); }
            File.WriteAllText(Path.Combine(testArea, "config.json"), "{}");
            if (PrepareChatFolder(testArea, rootDir, chatWorkers, out _, dataDir) is not { Path: var initialized, IsTemp: false } || initialized != testArea ||
                ActiveConfigPath(testArea, rootDir, dataDir) != Path.Combine(testArea, "config.json"))
                throw new InvalidOperationException("Chat folder and active config should be the initialized project's.");
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
            // Remote API mode: save moves the Unsloth keys aside, Local puts them back (or the template's, when none were saved).
            var remoteConfig = Path.Combine(testArea, "remote-config.json");
            var remoteTemplate = Path.Combine(testArea, "remote-template.json");
            const string localPlanner = "{\"base_url\":\"http://127.0.0.1:8888/v1\",\"management_url\":\"http://127.0.0.1:8888\",\"api_key_env\":\"UNSLOTH_API_KEY\",\"expected_model_contains\":\"Q\",\"timeout_seconds\":600,\"auto_load\":{\"model_path\":\"u/Q\"},\"display_name\":\"u/Q\",\"sampling\":{\"temperature\":0.6}}";
            File.WriteAllText(remoteConfig, $"{{\"keep\":1,\"roles\":{{\"planner\":{localPlanner}}}}}");
            File.WriteAllText(remoteTemplate, "{\"roles\":{\"planner\":{\"base_url\":\"http://t/v1\",\"management_url\":\"http://t\",\"api_key_env\":\"UNSLOTH_API_KEY\",\"auto_load\":{\"model_path\":\"t/M\"}}}}");
            var templateBytes = File.ReadAllBytes(remoteTemplate);
            JsonNode Planner() => JsonNode.Parse(File.ReadAllText(remoteConfig))!["roles"]!["planner"]!;
            var remote = new RemoteRole("https://openrouter.ai/api/v1/", "vendor/model-x", "OPENAI_API_KEY", "120");
            if (ReadRemoteRole(remoteConfig, "planner") is not null || WriteRemoteRole(remoteConfig, "planner", remote, remoteTemplate) is not null)
                throw new InvalidOperationException("Remote role: a local role read as remote, or the remote save failed.");
            var saved = Planner();
            if (saved["management_url"] is not null || saved["auto_load"] is not null || saved["expected_model_contains"] is not null ||
                (string?)saved["base_url"] != "https://openrouter.ai/api/v1" || (string?)saved["model"] != "vendor/model-x" ||
                (string?)saved["api_key_env"] != "OPENAI_API_KEY" || (int?)saved["timeout_seconds"] != 120 || (double?)saved["sampling"]?["temperature"] != 0.6 ||
                ReadRemoteRole(remoteConfig, "planner") != new RemoteRole("https://openrouter.ai/api/v1", "vendor/model-x", "OPENAI_API_KEY", "120") ||
                RemoteModelInfo("vendor/model-x") != new ModelInfo("vendor/model-x", "—", "Remote"))
                throw new InvalidOperationException($"Remote role saved wrongly: {saved.ToJsonString()}");
            if (WriteRemoteRole(remoteConfig, "planner", remote with { Model = "other/m" }, remoteTemplate) is not null ||
                WriteRemoteRole(remoteConfig, "planner", null, remoteTemplate) is not null ||
                !JsonNode.DeepEquals(Planner(), JsonNode.Parse(localPlanner)) || (int?)JsonNode.Parse(File.ReadAllText(remoteConfig))!["keep"] != 1)
                throw new InvalidOperationException($"Remote role: Local did not restore the role: {Planner().ToJsonString()}");
            foreach (var bad in new[] { remote with { BaseUrl = "openrouter.ai" }, remote with { Model = " " }, remote with { KeyEnv = "sk-abc 123" }, remote with { Timeout = "0" } })
                if (WriteRemoteRole(remoteConfig, "planner", bad, remoteTemplate) is null)
                    throw new InvalidOperationException($"Remote role: invalid input was accepted: {bad}");
            File.WriteAllText(remoteConfig, "{\"roles\":{\"planner\":{\"base_url\":\"https://r/v1\",\"model\":\"r/m\",\"api_key_env\":\"K\",\"sampling\":{}}}}");
            if (WriteRemoteRole(remoteConfig, "planner", null, remoteTemplate) is not null || (string?)Planner()["management_url"] != "http://t" ||
                (string?)Planner()["auto_load"]?["model_path"] != "t/M" || Planner()["model"] is not null || !File.ReadAllBytes(remoteTemplate).SequenceEqual(templateBytes))
                throw new InvalidOperationException($"Remote role: Local without saved settings should use the template's: {Planner().ToJsonString()}");
            // A literal api_key is moved aside with the Unsloth keys, and the local Unsloth key variable is refused.
            var keyedPlanner = localPlanner.Replace("\"api_key_env\":\"UNSLOTH_API_KEY\",", "\"api_key_env\":\"UNSLOTH_API_KEY\",\"api_key\":\"sk-local\",");
            File.WriteAllText(remoteConfig, $"{{\"roles\":{{\"planner\":{keyedPlanner}}}}}");
            if (WriteRemoteRole(remoteConfig, "planner", remote, remoteTemplate) is not null || Planner()["api_key"] is not null ||
                (string?)Planner()["local_settings"]?["api_key"] != "sk-local" || WriteRemoteRole(remoteConfig, "planner", null, remoteTemplate) is not null ||
                !JsonNode.DeepEquals(Planner(), JsonNode.Parse(keyedPlanner)))
                throw new InvalidOperationException($"Remote role: api_key was not moved aside and restored: {Planner().ToJsonString()}");
            if (ValidateRemoteRole(remote with { KeyEnv = "UNSLOTH_API_KEY" }) is null || ValidateRemoteRole(remote with { KeyEnv = " unsloth_api_key " }) is null ||
                WriteRemoteRole(remoteConfig, "planner", remote with { KeyEnv = "UNSLOTH_API_KEY" }, remoteTemplate) is null || Planner()["model"] is not null)
                throw new InvalidOperationException("Remote role: UNSLOTH_API_KEY must not be accepted as a remote key variable.");
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
            // Role fallbacks: a role not added gets same_as (its keys kept); an added one loses it, and an empty one copies the planner.
            var fallbacks = Path.Combine(testArea, "fallbacks.json");
            File.WriteAllText(fallbacks, "{\"keep\":1,\"roles\":{\"planner\":{\"base_url\":\"u\",\"auto_load\":{\"model_path\":\"P\"}},\"implementer\":{\"base_url\":\"u\",\"display_name\":\"I\",\"auto_load\":{\"model_path\":\"I\"}},\"reviewer\":{\"same_as\":\"planner\",\"chat_system_prompt\":\"x\"}}}");
            WriteRoleFallbacks(fallbacks, ["reviewer"]);
            var fb = JsonNode.Parse(File.ReadAllText(fallbacks))!;
            if ((string?)fb["roles"]!["implementer"]!["same_as"] != "planner" || (string?)fb["roles"]!["implementer"]!["display_name"] != "I" ||
                fb["roles"]!["reviewer"]!["same_as"] is not null || (string?)fb["roles"]!["reviewer"]!["chat_system_prompt"] != "x" ||
                ModelKey(fb["roles"]!["reviewer"]) != ModelKey(fb["roles"]!["planner"]) || (string?)fb["roles"]!["reviewer"]!["base_url"] != "u" || (int)fb["keep"]! != 1)
                throw new InvalidOperationException("Role fallbacks: a not-added role must get same_as, an added empty one the planner's settings.");
            WriteRoleFallbacks(fallbacks, ["implementer", "reviewer"]);
            fb = JsonNode.Parse(File.ReadAllText(fallbacks))!;
            if (fb["roles"]!["implementer"]!["same_as"] is not null || ModelKey(fb["roles"]!["implementer"]) == ModelKey(fb["roles"]!["planner"]))
                throw new InvalidOperationException("Role fallbacks: an added role with a model must keep its own settings.");
            File.WriteAllText(fallbacks, "{\"roles\":{\"planner\":{\"base_url\":\"u\"}}}");
            WriteRoleFallbacks(fallbacks, []);
            fb = JsonNode.Parse(File.ReadAllText(fallbacks))!;
            if ((string?)fb["roles"]!["implementer"]!["same_as"] != "planner" || (string?)fb["roles"]!["reviewer"]!["same_as"] != "planner")
                throw new InvalidOperationException("Role fallbacks: a missing role must be written as same_as.");
            // Own model: a role keeps its own unless its model and model options equal the planner's (an absent or empty key is the planner's).
            var ownPlanner = JsonNode.Parse("{\"base_url\":\"u\",\"model\":\"m\",\"sampling\":{\"temperature\":0.5}}");
            if (HasOwnModel(JsonNode.Parse("{\"base_url\":\"u\",\"model\":\"m\",\"sampling\":{\"temperature\":0.5},\"display_name\":\"I\"}"), ownPlanner) ||
                HasOwnModel(JsonNode.Parse("{\"display_name\":\"I\",\"auto_load\":{}}"), ownPlanner) ||
                HasOwnModel(JsonNode.Parse("{\"base_url\":\"r\",\"same_as\":\"planner\"}"), ownPlanner) || HasOwnModel(null, ownPlanner) ||
                !HasOwnModel(JsonNode.Parse("{\"base_url\":\"r\",\"model\":\"m\"}"), ownPlanner) ||
                !HasOwnModel(JsonNode.Parse("{\"model\":\"m\",\"sampling\":{\"temperature\":0.2}}"), ownPlanner) ||
                !HasOwnModel(JsonNode.Parse("{\"auto_load\":{\"model_path\":\"m\"}}"), ownPlanner))
                throw new InvalidOperationException("Own model: only a role whose model or model options differ from the planner's has its own.");
            // A project's not-added role with its own model is kept (and reported) when keepOwn names it; the rest get same_as.
            File.WriteAllText(fallbacks, "{\"roles\":{\"planner\":{\"model\":\"m\"},\"implementer\":{\"model\":\"x\"},\"reviewer\":{\"model\":\"m\"}}}");
            var keptOwn = WriteRoleFallbacks(fallbacks, [], ["reviewer"]);
            fb = JsonNode.Parse(File.ReadAllText(fallbacks))!;
            if (keptOwn.Length != 0 || (string?)fb["roles"]!["implementer"]!["same_as"] != "planner" || (string?)fb["roles"]!["reviewer"]!["same_as"] != "planner")
                throw new InvalidOperationException("Role fallbacks: a role keepOwn does not name, or one without its own model, must get same_as.");
            File.WriteAllText(fallbacks, "{\"roles\":{\"planner\":{\"model\":\"m\"},\"implementer\":{\"model\":\"x\"},\"reviewer\":{\"model\":\"m\"}}}");
            keptOwn = WriteRoleFallbacks(fallbacks, [], ["implementer", "reviewer"]);
            fb = JsonNode.Parse(File.ReadAllText(fallbacks))!;
            if (keptOwn is not ["implementer"] || fb["roles"]!["implementer"]!["same_as"] is not null || (string?)fb["roles"]!["reviewer"]!["same_as"] != "planner")
                throw new InvalidOperationException("Role fallbacks: a project's role with its own model must be left as it is.");
            // Migration reads the roles with their own model; an unreadable config gives null (nothing migrates).
            File.WriteAllText(fallbacks, "{\"roles\":{\"planner\":{\"model\":\"m\"},\"implementer\":{\"model\":\"m\",\"sampling\":{\"top_p\":0.9}},\"reviewer\":{\"model\":\"m\"}}}");
            var badConfig = Path.Combine(testArea, "bad-config.json");
            File.WriteAllText(badConfig, "{not json");
            if (RolesWithOwnModel(fallbacks) is not ["implementer"] || RolesWithOwnModel(badConfig) is not null || RolesWithOwnModel("") is not null ||
                RolesWithOwnModel(Path.Combine(testArea, "missing.json")) is not null)
                throw new InvalidOperationException("Migration must find the roles with their own model and nothing in an unreadable config.");
            // Settings that exist but cannot be read are never saved over.
            var dataBefore = Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA");
            try
            {
                Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", testArea);
                var settingsFile = Path.Combine(testArea, "settings.json");
                File.WriteAllText(settingsFile, "{broken");
                var unread = AppSettings.Load();
                unread.Save();
                if (!unread.LoadFailed || File.ReadAllText(settingsFile) != "{broken")
                    throw new InvalidOperationException("Settings that failed to load must not be saved over.");
                // Settings from before WorkflowMode: tri when the Implementer was added, else dual; the old list is not written back.
                string Mode(string json) { File.WriteAllText(settingsFile, json); var s = AppSettings.Load(); s.Save(); return s.WorkflowMode + "|" + AppSettings.Load().WorkflowMode; }
                if (Mode("""{"AddedRoles":["implementer"]}""") != "tri|tri" || Mode("""{"AddedRoles":["reviewer"]}""") != "dual|dual" || Mode("{}") != "dual|dual" ||
                    Mode("""{"WorkflowMode":"tri"}""") != "tri|tri" || File.ReadAllText(settingsFile).Contains("AddedRoles") ||
                    string.Join(",", AppSettings.Load().AddedRoles) != "implementer,reviewer")
                    throw new InvalidOperationException("The workflow mode was not migrated from the added roles.");
            }
            finally { Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", dataBefore); }
            // Task limits: read and write; an empty field removes its key, then the empty task_limits; other keys stay.
            var limitsConfig = Path.Combine(testArea, "limits.json");
            File.WriteAllText(limitsConfig, "{\"keep\":1,\"roles\":{\"implementer\":{\"auto_load\":{\"max_seq_length\":16384}},\"reviewer\":{\"auto_load\":{\"max_seq_length\":32768}}}}");
            if (ReadTaskLimits(limitsConfig) != ("", "") || WriteTaskLimits(limitsConfig, "6", " 10 ") is not null || ReadTaskLimits(limitsConfig) != ("6", "10"))
                throw new InvalidOperationException("Task limits were not written or read back.");
            var limitsBefore = File.ReadAllText(limitsConfig);
            if (new[] { "0", "-2", "1.5", "x", "99999999999" }.Any(bad => WriteTaskLimits(limitsConfig, bad, "") is null || WriteTaskLimits(limitsConfig, "", bad) is null) ||
                File.ReadAllText(limitsConfig) != limitsBefore)
                throw new InvalidOperationException("Invalid task limits were accepted or partially written.");
            if (WriteTaskLimits(limitsConfig, "", "8") is not null || ReadTaskLimits(limitsConfig) != ("", "8") || WriteTaskLimits(limitsConfig, " ", "") is not null ||
                JsonNode.Parse(File.ReadAllText(limitsConfig)) is not JsonObject cleared || cleared.ContainsKey("task_limits") || (int)cleared["keep"]! != 1 ||
                (int)cleared["roles"]!["reviewer"]!["auto_load"]!["max_seq_length"]! != 32768)
                throw new InvalidOperationException("Empty task limits must remove their keys, then task_limits, and nothing else.");
            // Presets carry task_limits: "C++" sets 6/10, one saved without task_limits restores the defaults, an older preset leaves them.
            WriteTaskLimits(configA, "6", "10");
            var cpp = CapturePreset("C++", configA, workersA);
            var plain = CapturePreset("plain", limitsConfig, workersA);
            var old = new WorkerPreset { Name = "old", Roles = cpp.Roles };
            if (cpp.TaskLimits is null || plain.TaskLimits != "{}" || CapturePreset("none", Path.Combine(testArea, "missing.json"), workersA).TaskLimits is not null ||
                !ApplyPresetRoles(configB, cpp) || ReadTaskLimits(configB) != ("6", "10") || JsonNode.Parse(File.ReadAllText(configB))!["validation_commands"]![0]!.ToString() != "b" ||
                !ApplyPresetRoles(configB, old) || ReadTaskLimits(configB) != ("6", "10") ||
                !ApplyPresetRoles(configB, plain) || JsonNode.Parse(File.ReadAllText(configB))!.AsObject().ContainsKey("task_limits"))
                throw new InvalidOperationException("Presets must capture and apply task_limits.");
            // Context warning: files x 4,000 + 8,000 tokens against each role's max_seq_length.
            var contexts = RoleContexts(limitsConfig);
            if (contexts.Count != 2 || contexts[0] != ("Implementer", 16384) || contexts[1] != ("Reviewer", 32768) ||
                TaskContextWarning(2, contexts) is not null || // 16,000 fits 16k
                TaskContextWarning(3, contexts) != "3 files per task may not fit Implementer's 16k context" ||
                TaskContextWarning(6, contexts) != "6 files per task may not fit Implementer's 16k context" || // 32,000 fits 32k
                TaskContextWarning(7, contexts) != "7 files per task may not fit Implementer's 16k or Reviewer's 32k context" ||
                TaskContextWarning(50, []) is not null || RoleContexts(configA).Count != 0)
                throw new InvalidOperationException("Task limits context warning thresholds failed.");
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
            if (skippedTitles.Length != 2 || !skippedTitles[0].StartsWith("NoModel: no model") || !skippedTitles[1].Contains("pick the model again") || cwArray.Count != 4 || (int)cw["keep"]! != 1 || cw["roles"]!.ToJsonString() != rolesBefore)
                throw new InvalidOperationException("Custom workers: skipping, array size or untouched keys failed.");
            // An empty system prompt or description is written as the kind's default.
            if ((string)cwArray[2]!["system_prompt"]! != DefaultSystemPrompt("specialist") || (string)cwArray[2]!["description"]! != "d" ||
                (string)cwArray[3]!["description"]! != DefaultDescription("specialist") || (string)cwArray[3]!["system_prompt"]! != "p" ||
                (string)rev["description"]! != DefaultDescription("reviewer"))
                throw new InvalidOperationException("Custom workers: empty fields must take the kind's defaults.");
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
            // A Remote implementer: custom workers keep the local connection (its saved local_settings, else the template's).
            File.WriteAllText(cwConfig, $"{{\"roles\":{rolesBefore}}}");
            var remoteImpl = new RemoteRole("https://remote.example/v1", "vendor/m", "OPENAI_API_KEY", "30");
            var cwTemplate = Path.Combine(testArea, "cw-template.json");
            File.WriteAllText(cwTemplate, "{\"roles\":{\"implementer\":{\"base_url\":\"http://t/v1\",\"management_url\":\"http://t\",\"api_key_env\":\"UNSLOTH_API_KEY\",\"timeout_seconds\":9,\"auto_load\":{\"model_path\":\"t\",\"n_batch\":7}}}}");
            JsonNode Worker() => JsonNode.Parse(File.ReadAllText(cwConfig))!["custom_workers"]![0]!;
            if (WriteRemoteRole(cwConfig, "implementer", remoteImpl, cwTemplate) is not null || WriteCustomWorkers(cwConfig, [Ok("v1", "Docs")], cwTemplate).Length != 0 ||
                (string?)Worker()["base_url"] != "http://h/v1" || (string?)Worker()["management_url"] != "http://h" || (string?)Worker()["api_key_env"] != "K" ||
                (string?)Worker()["api_key"] != "sk" || (int?)Worker()["timeout_seconds"] != 600 || (int?)Worker()["auto_load"]?["n_parallel"] != 1)
                throw new InvalidOperationException($"Custom workers: a Remote implementer's connection was copied: {Worker().ToJsonString()}");
            File.WriteAllText(cwConfig, "{\"roles\":{\"implementer\":{\"base_url\":\"https://remote.example/v1\",\"model\":\"vendor/m\",\"api_key_env\":\"OPENAI_API_KEY\",\"sampling\":{}}}}");
            if (WriteCustomWorkers(cwConfig, [Ok("v1", "Docs")], cwTemplate).Length != 0 || (string?)Worker()["base_url"] != "http://t/v1" ||
                (string?)Worker()["management_url"] != "http://t" || (string?)Worker()["api_key_env"] != "UNSLOTH_API_KEY" || (int?)Worker()["auto_load"]?["n_batch"] != 7 ||
                WriteCustomWorkers(cwConfig, [Ok("v1", "Docs")]) is not [var noLocal] || !noLocal.Contains("base_url"))
                throw new InvalidOperationException($"Custom workers: a Remote implementer without saved settings should use the template's: {Worker().ToJsonString()}");
            // Every role remote and no custom worker: no Unsloth key needed and no monitor to poll.
            static bool Unsloth(string json, bool llama = false) { using var doc = JsonDocument.Parse(json); return UsesUnsloth(doc.RootElement, llama); }
            if (Unsloth("{\"roles\":{\"reviewer\":{\"management_url\":\"http://h\",\"auto_load\":{\"model_path\":\"m\"}}}}", llama: true) ||
                !Unsloth("{\"roles\":{\"reviewer\":{\"management_url\":\"http://h\"}}}", llama: true) ||
                !Unsloth("{\"roles\":{\"reviewer\":{\"management_url\":\"http://h\",\"auto_load\":{}}}}"))
                throw new InvalidOperationException("UsesUnsloth: a role with management_url but no auto_load still needs Unsloth on llama.cpp.");
            if (Unsloth("{\"roles\":{\"planner\":{\"model\":\"m\"}},\"custom_workers\":[]}") || Unsloth("{}") ||
                !Unsloth("{\"roles\":{\"planner\":{\"model\":\"m\"},\"reviewer\":{\"management_url\":\"http://h\"}}}") ||
                !Unsloth("{\"roles\":{\"planner\":{\"model\":\"m\"}},\"custom_workers\":[{\"management_url\":\"http://h\"}]}"))
                throw new InvalidOperationException("UsesUnsloth: wrong answer for remote-only or mixed configs.");
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
            File.WriteAllText(Path.Combine(testProject, "STATE.md"), "# Verified state\n\n## Passed: PHASE-1\n- x\n\n## Project complete\r\n- Summary: done\n");
            if (PrimaryLabel(testProject) != "Restart Project")
                throw new InvalidOperationException("A complete project should offer Restart Project.");
            File.WriteAllText(Path.Combine(testProject, "TASK.md"), "# PHASE-2\n\n## Outcome\nx\n");
            File.WriteAllText(Path.Combine(testProject, ".ao_checkpoint.json"), "{}");
            if (PrimaryLabel(testProject) != "Resume Project — PHASE-2")
                throw new InvalidOperationException("A task in progress after completion should offer Resume.");
            File.Delete(Path.Combine(testProject, ".ao_checkpoint.json"));
            File.AppendAllText(Path.Combine(testProject, "STATE.md"), "\n## Passed: PHASE-2\n- x\n");
            if (PrimaryLabel(testProject) != "Resume Project — PHASE-2")
                throw new InvalidOperationException("A task passed after completion should offer Resume.");
            if (RestartRefusal(Path.GetPathRoot(root)!, null) is null || RestartRefusal(root, root) is null || RestartRefusal(Path.GetDirectoryName(root)!, root) is null ||
                RestartRefusal(testArea, root) is null || RestartRefusal(testProject, root) is not null)
                throw new InvalidOperationException("Restart refusal failed.");

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

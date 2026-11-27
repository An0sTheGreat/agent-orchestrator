using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator;

// The phone app's Code screen (MainWindow.WebApp.cs routes /api/code/... and /api/fs/dirs): a Code conversation's snapshot,
// a new one for a folder, its folder, Start, Once, PROJECT.md, extra folders, its files (read only, sandboxed to its folders),
// the saved replies' changes and diffs, and the run log. Writes go through the desktop's own calls; never a dialog: where the
// desktop would ask or refuse, the phone gets a 409.
public partial class MainWindow
{
    private const int WebCodeMaxBytes = 2_000_000; // the editor's EditorMaxBytes
    private const string WebBusy = "Wait for the reply or run to finish";

    // The last `todo` tool call's items per conversation (DrainLines feeds it): the model passes its whole list each time.
    // ponytail: in memory only; saved messages keep no tool rows, so the list is gone after a restart.
    private readonly Dictionary<string, List<(string Text, string State)>> _webTodos = [];

    private void KeepWebTodo(string? conversationId, JsonElement ev)
    {
        if (conversationId is null || Str(ev, "e") != "tool" || Str(ev, "name") != "todo")
            return;
        try
        {
            using var args = JsonDocument.Parse(Str(ev, "args"));
            if (args.RootElement.ValueKind != JsonValueKind.Object || !args.RootElement.TryGetProperty("items", out var items))
                return;
            var list = items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Select(i => i.ToString()).ToList() : [items.ToString()];
            _webTodos[conversationId] = list.Select(item => item.TrimStart() is var t && t.Length >= 3 && t[0] == '[' && t[2] == ']'
                ? (t[3..].Trim(), char.ToLowerInvariant(t[1]) switch { 'x' => "done", '-' or '~' or '>' => "doing", _ => "" }) : (item.Trim(), "")).ToList();
        }
        catch (JsonException) { }
    }

    private static (int, object) WebCodeError(string error) => (409, new { error });

    private bool WorkerEditsPending => _shownWorker is { } shown && _cards.Contains(shown) && _workerDirty();

    // A full path the phone sent for a folder, made full; null with the 400 when it is not one that exists.
    private static (string? Folder, (int, object) Refusal) WebFolderArg(string path)
    {
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            return (null, (400, new { error = "Send the folder's full path" }));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return LauncherLogic.FolderUsable(full) ? (full, default) : (null, (400, new { error = $"Folder not found: {full}" }));
    }

    // /api/code/new, and /api/code/{id}[/action]
    private (int, object) WebCode(string method, string rest, IReadOnlyDictionary<string, string> query, JsonElement body)
    {
        if (method == "POST" && WebPromptOpen())
            return (409, new { error = "DialogOpen" });
        if (rest == "new")
            return method == "POST" ? WebCodeNew(body) : WebNotFound;
        var slash = rest.IndexOf('/');
        var (id, action) = slash < 0 ? (rest, "") : (rest[..slash], rest[(slash + 1)..]);
        if (_store.Get(id) is not { } conv || Kind(conv.Kind) != "code")
            return (404, new { error = "No such conversation" });
        return (method, action) switch
        {
            ("GET", "") => (200, WebCodeSnapshot(conv)),
            ("GET", "files") => WebCodeFiles(conv, query.GetValueOrDefault("dir") ?? ""),
            ("GET", "file") => WebCodeFile(conv, query.GetValueOrDefault("path") ?? ""),
            ("GET", "changes") => (200, new { files = WebCodeChanges(conv).Select(c => new { path = c.Path, added = c.Change.Added, removed = c.Change.Removed, msg = c.Msg }).ToList() }),
            ("GET", "diff") => WebCodeDiff(conv, query.GetValueOrDefault("path") ?? "", query.GetValueOrDefault("msg")),
            ("GET", "runlog") => (200, new { text = ReadRunLogTail(_store.RunLogPath(id)) ?? "" }),
            ("POST", "folder") => WebCodeFolder(conv, WebField(body, "path")),
            ("POST", "start") => WebCodeStart(conv, body.ValueKind == JsonValueKind.Object && body.TryGetProperty("restart", out var r) && r.ValueKind == JsonValueKind.True),
            ("POST", "once") => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? WebCodeOnce(conv, on.GetBoolean()) : (400, new { error = "Send on: true or false" }),
            ("POST", "projectmd/template") => WebCodeTemplate(conv),
            ("POST", "projectmd") => WebCodeProjectMd(conv, body),
            ("POST", "folders/add") => WebCodeAddFolder(conv, WebField(body, "path"), WebField(body, "access")),
            ("POST", "folders/remove") => WebCodeRemoveFolder(conv, WebField(body, "path")),
            _ => WebNotFound,
        };
    }

    // NewCodeConversation(folder, group) with the folder the phone sent, validated as the desktop: full, usable, not the app's own.
    private (int, object) WebCodeNew(JsonElement body)
    {
        var (folder, refusal) = WebFolderArg(WebField(body, "folder"));
        if (folder is null)
            return refusal;
        if (LauncherLogic.OverlapsWorkflowRoot(folder, _workflowRoot))
            return WebCodeError(WorkflowRootRefusal);
        var group = WebField(body, "project");
        if (group.Length > 0 && _store.View().Groups.All(g => g.Id != group))
            return (400, new { error = "No such project" });
        if (_chatBusy && _turn.IsCode)
            return WebCodeError(WebBusy);
        if (WorkerEditsPending)
            return WebCodeError("Save or discard the worker settings on the PC first");
        NewCodeConversation(folder, group.Length > 0 ? group : null);
        return _codeConv is { } c && SameFolder(c.Folder, folder) ? (200, new { id = c.Id }) : WebCodeError("Could not start the conversation");
    }

    // OpenCodeConversation without its dialogs: the desktop's refusals as 409s; null once it is the open one.
    private (int, object)? WebOpenCode(ConversationStore.Conversation conv)
    {
        if (_codeConv?.Id == conv.Id)
            return null;
        if (_chatBusy && _turn.IsCode)
            return WebCodeError(WebBusy);
        if (conv.Folder.Length > 0 && !Path.IsPathFullyQualified(conv.Folder))
            return WebCodeError("This chat has no folder, so it can't be opened");
        if (WorkerEditsPending)
            return WebCodeError("Save or discard the worker settings on the PC first");
        OpenCodeConversation(conv.Id);
        return _codeConv?.Id == conv.Id ? null : WebCodeError("Could not open the conversation");
    }

    private List<LauncherLogic.CodeFolder> WebCodeFoldersOf(ConversationStore.Conversation conv) =>
        _codeConv?.Id == conv.Id ? CodeExtraFolders : _codeParked is { } p && p.Conv?.Id == conv.Id ? p.Folders : conv.Folders;

    private object WebCodeSnapshot(ConversationStore.Conversation conv)
    {
        var folder = conv.Folder;
        var usable = folder.Length > 0 && LauncherLogic.FolderUsable(folder);
        var hasMd = usable && File.Exists(Path.Combine(folder, "PROJECT.md"));
        var busy = RunningId == conv.Id;
        var running = busy && _process is not null;
        object Clock(System.Diagnostics.Stopwatch w) => new { seconds = running ? (int)w.Elapsed.TotalSeconds : 0, running = running && w.IsRunning };
        // the live transcript while this conversation's run writes it, else its saved .run.txt
        var activity = _runLogId == conv.Id ? _transcript.ToString(Math.Max(0, _transcript.Length - 40_000), Math.Min(_transcript.Length, 40_000))
            : ReadRunLogTail(_store.RunLogPath(conv.Id), 40_000) ?? "";
        return new
        {
            id = conv.Id, title = ConversationStore.TitleOf(conv), folder,
            folders = WebCodeFoldersOf(conv).Select(f => new { path = f.Path, access = f.Access }).ToList(),
            hasProjectMd = hasMd, startLabel = usable ? LauncherLogic.PrimaryLabel(folder) : "Start project",
            canStart = !_chatBusy && _process is null && _workflowRoot is not null && hasMd && !LauncherLogic.OverlapsWorkflowRoot(folder, _workflowRoot) &&
                       !WorkerEditsPending && RuntimeRefusal is null,
            running, busy, taskId = usable ? LauncherLogic.CurrentTaskId(folder) : null,
            status = busy || _codeConv?.Id == conv.Id ? StatusText.Text : "Ready",
            taskTime = Clock(_taskWatch), turnTime = Clock(_turnWatch), once = _settings.Once,
            todo = _webTodos.GetValueOrDefault(conv.Id)?.Select(t => new { text = t.Text, state = t.State }).ToList(),
            activity,
        };
    }

    // UseCodeFolder on the conversation, opened first.
    private (int, object) WebCodeFolder(ConversationStore.Conversation conv, string path)
    {
        var (folder, refusal) = WebFolderArg(path);
        if (folder is null)
            return refusal;
        if (LauncherLogic.OverlapsWorkflowRoot(folder, _workflowRoot))
            return WebCodeError(WorkflowRootRefusal);
        if (CodeBusy)
            return WebCodeError(WebBusy);
        if (WebOpenCode(conv) is { } refused)
            return refused;
        UseCodeFolder(folder);
        return SameFolder(conv.Folder, folder) ? (200, WebCodeSnapshot(conv)) : WebCodeError("Could not change the folder");
    }

    // StartCodeProject's guards as 409s with its texts; a complete project needs restart: true, then Start_Click's RestartProject runs past
    // its confirm (after Start_Click's own RuntimeReady, so a missing llama-server recycles nothing).
    private (int, object) WebCodeStart(ConversationStore.Conversation conv, bool restart)
    {
        var folder = conv.Folder;
        if (_chatBusy || _process is not null)
            return WebCodeError(WebBusy);
        if (_workflowRoot is null)
            return WebCodeError("The Agent Orchestrator folder was not found");
        if (folder.Length == 0 || !Directory.Exists(folder))
            return WebCodeError("Choose a folder first");
        if (LauncherLogic.OverlapsWorkflowRoot(folder, _workflowRoot))
            return WebCodeError(WorkflowRootRefusal);
        if (!File.Exists(Path.Combine(folder, "PROJECT.md")))
            return WebCodeError("Project not started: add a PROJECT.md to this folder (create it with a model, Add PROJECT.md… or Start from template)");
        if (WorkerEditsPending) // Start_Click's SaveWorkerEditsFirst would ask
            return WebCodeError("Save or discard the worker settings on the PC first");
        if (LauncherLogic.IsProjectComplete(folder))
        {
            if (!restart)
                return (409, new { error = "The project is complete: restart it? Everything but PROJECT.md goes to the Recycle Bin.", restart = true });
            if (LauncherLogic.RestartRefusal(folder, _workflowRoot) is { } reason)
                return WebCodeError($"Restart refused: {reason}");
        }
        if (WebOpenCode(conv) is { } refused)
            return refused;
        var confirm = _restartConfirm;
        _restartConfirm = _ => true; // the phone confirmed on its side; Start_Click reaches RestartProject before its first await
        try { StartCodeProject(); }
        finally { _restartConfirm = confirm; }
        return _process is not null ? (200, WebCodeSnapshot(conv)) : WebCodeError("PROJECT.md and the workflow files are ready, but the run was not started");
    }

    private (int, object) WebCodeOnce(ConversationStore.Conversation conv, bool on)
    {
        _settings.Once = on;
        _settings.Save();
        return (200, WebCodeSnapshot(conv));
    }

    // StartProjectMdFromTemplate's copy, without opening the file on the PC.
    private (int, object) WebCodeTemplate(ConversationStore.Conversation conv)
    {
        var folder = conv.Folder;
        if (_chatBusy || _process is not null)
            return WebCodeError(WebBusy);
        if (_workflowRoot is null)
            return WebCodeError("The Agent Orchestrator folder was not found");
        if (folder.Length == 0 || !Directory.Exists(folder))
            return WebCodeError("Choose a folder first");
        if (LauncherLogic.OverlapsWorkflowRoot(folder, _workflowRoot))
            return WebCodeError(WorkflowRootRefusal);
        var target = Path.Combine(folder, "PROJECT.md");
        try
        {
            if (!File.Exists(target))
            {
                File.Copy(Path.Combine(_workflowRoot, "PROJECT.md"), target);
                Log($"PROJECT.md template copied into {folder}");
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            Log($"Could not copy the PROJECT.md template: {x.Message}");
            return WebCodeError("Could not copy the PROJECT.md template");
        }
        RefreshCodeFolder();
        RefreshPrimary();
        return (200, WebCodeSnapshot(conv));
    }

    // Writes the folder's PROJECT.md (the one file the phone edits); answers as GET file does.
    private (int, object) WebCodeProjectMd(ConversationStore.Conversation conv, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
            return (400, new { error = "Send text" });
        var text = t.GetString()!;
        if (Encoding.UTF8.GetByteCount(text) > WebCodeMaxBytes)
            return (413, new { error = "Too large" });
        var folder = conv.Folder;
        if (folder.Length == 0 || !Directory.Exists(folder))
            return WebCodeError("Choose a folder first");
        if (LauncherLogic.OverlapsWorkflowRoot(folder, _workflowRoot))
            return WebCodeError(WorkflowRootRefusal);
        var target = Path.Combine(folder, "PROJECT.md");
        if (WebSandboxed(conv, target) is null)
            return (403, new { error = "Outside this conversation's folders" });
        try { File.WriteAllText(target, text); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return WebCodeError($"Could not save PROJECT.md: {x.Message}"); }
        RefreshCodeFolder();
        RefreshPrimary();
        return WebCodeFile(conv, target);
    }

    // AddCodeFolder_Click past its picker (the phone sends the path), with its refusals. No access sent: Full edit, as on the
    // PC, but the Agent Orchestrator folder Read only; an explicit access is still honoured.
    private (int, object) WebCodeAddFolder(ConversationStore.Conversation conv, string path, string access)
    {
        if (access is not ("" or "read" or "write"))
            return (400, new { error = "Send access: read or write" });
        var (full, refusal) = WebFolderArg(path);
        if (full is null)
            return refusal;
        if (access == "")
            access = LauncherLogic.OverlapsWorkflowRoot(full, _workflowRoot) ? "read" : "write";
        var project = conv.Folder;
        if (!LauncherLogic.FolderUsable(project))
            return WebCodeError("Choose a folder first");
        if (_chatBusy || _process is not null)
            return WebCodeError(WebBusy);
        if (LauncherLogic.OverlapsWorkflowRoot(project, _workflowRoot))
            return WebCodeError(WorkflowRootRefusal);
        if (SamePath(full, project))
            return WebCodeError("That is the project folder; the worker can already edit it");
        if (WebCodeFoldersOf(conv).Any(f => SamePath(f.Path, full)))
            return WebCodeError("That folder is already added");
        if (access == "write" && LauncherLogic.OverlapsWorkflowRoot(full, _workflowRoot))
            return WebCodeError("The Agent Orchestrator folder can only be added Read only");
        if (WebOpenCode(conv) is { } refused)
            return refused;
        if (!EnsureCodeConversation(project))
            return WebCodeError("Could not save the conversation");
        CodeExtraFolders.Add(new LauncherLogic.CodeFolder(full, access));
        RefreshCodeFolders();
        SaveCodeConversation();
        return (200, WebCodeSnapshot(conv));
    }

    private (int, object) WebCodeRemoveFolder(ConversationStore.Conversation conv, string path)
    {
        if (path.Length == 0)
            return (400, new { error = "Send the folder's path" });
        if (_chatBusy || _process is not null)
            return WebCodeError(WebBusy);
        if (WebCodeFoldersOf(conv).FirstOrDefault(f => SamePath(f.Path, path)) is not { } folder)
            return (404, new { error = "No such folder" });
        if (WebOpenCode(conv) is { } refused)
            return refused;
        RemoveCodeFolder(CodeExtraFolders.First(f => SamePath(f.Path, folder.Path)));
        return (200, WebCodeSnapshot(conv));
    }

    // A path the phone may read for this conversation: full, inside its folder or one of its extra folders (the Agent Orchestrator
    // folder only through an extra folder, as Read only), no `..`, and no link (symlink, junction) on the way; null otherwise. Other
    // reparse points pass: OneDrive's files on demand and deduplicated files are ordinary files.
    private string? WebSandboxed(ConversationStore.Conversation conv, string path)
    {
        if (path.Length == 0 || path.Split('\\', '/').Contains(".."))
            return null;
        string full;
        try { full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(conv.Folder, path)); }
        catch (Exception x) when (x is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        static bool Within(string inner, string root) =>
            (Path.TrimEndingDirectorySeparator(inner) + Path.DirectorySeparatorChar).StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var extras = WebCodeFoldersOf(conv).Select(f => f.Path).Where(p => p.Length > 0 && Path.IsPathFullyQualified(p)).Select(p => Path.GetFullPath(p)).ToList();
        var roots = (conv.Folder.Length > 0 && Path.IsPathFullyQualified(conv.Folder) ? extras.Prepend(Path.GetFullPath(conv.Folder)) : extras).ToList();
        if (roots.FirstOrDefault(r => Within(full, r)) is not { } root)
            return null;
        if (_workflowRoot is not null && Within(full, _workflowRoot) && !extras.Any(e => Within(full, e)))
            return null;
        // every existing step from the root down: a junction or symlink could lead anywhere
        for (var step = full; step.Length >= Path.TrimEndingDirectorySeparator(root).Length; step = Path.GetDirectoryName(step) ?? "")
        {
            try
            {
                if ((Directory.Exists(step) ? new DirectoryInfo(step).LinkTarget : File.Exists(step) ? new FileInfo(step).LinkTarget : null) is not null)
                    return null;
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return null; }
            if (Path.GetDirectoryName(step) is null)
                break;
        }
        return full;
    }

    private (int, object) WebCodeFiles(ConversationStore.Conversation conv, string dir)
    {
        if (WebSandboxed(conv, dir.Length == 0 ? conv.Folder : dir) is not { } full)
            return (403, new { error = "Outside this conversation's folders" });
        if (!Directory.Exists(full))
            return (404, new { error = "No such folder" });
        try
        {
            var info = new DirectoryInfo(full);
            var dirs = info.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => new { name = d.Name, path = d.FullName, dir = true, size = (long?)null });
            var files = info.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => new { name = f.Name, path = f.FullName, dir = false, size = (long?)f.Length });
            return (200, new { dir = full, entries = dirs.Concat(files).ToList() });
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return (403, new { error = x.Message }); }
    }

    // The editor's rules (ShowEditorFile): at most 2,000,000 bytes, and a NUL in the first 8000 characters is not text.
    private (int, object) WebCodeFile(ConversationStore.Conversation conv, string path)
    {
        if (WebSandboxed(conv, path) is not { } full)
            return (403, new { error = "Outside this conversation's folders" });
        if (!File.Exists(full))
            return (404, new { error = "No such file" });
        try
        {
            var size = new FileInfo(full).Length;
            if (size > WebCodeMaxBytes)
                return (413, new { error = "Too large to show" });
            var text = File.ReadAllText(full);
            if (text.AsSpan(0, Math.Min(text.Length, 8000)).Contains('\0'))
                return (415, new { error = "Not a text file" });
            return (200, new { path = full, text, language = LanguageOf(full), size });
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return (403, new { error = x.Message }); }
    }

    // Every file a saved reply changed, newest reply first: its path as the file chips resolve it, and the reply's index.
    private List<(string Path, LauncherLogic.FileChange Change, int Msg)> WebCodeChanges(ConversationStore.Conversation conv) =>
        conv.Messages.Select((m, i) => (m, i)).Reverse().Where(x => x.m.Changes is not null)
            .SelectMany(x => x.m.Changes!.Files.Select(f => (ChipPath(f.Path, x.m.Changes.Folders), f, x.i))).ToList();

    private (int, object) WebCodeDiff(ConversationStore.Conversation conv, string path, string? msg)
    {
        if (!int.TryParse(msg, out var index) || WebCodeChanges(conv).FirstOrDefault(c => c.Msg == index && c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is not { Path: not null } change)
            return (404, new { error = "No such change" });
        if (change.Change.Edits is not { } edits)
            return (404, new { error = "This change was too large to keep" });
        if (WebSandboxed(conv, change.Path) is not { } full)
            return (403, new { error = "Outside this conversation's folders" });
        string text;
        try { text = File.Exists(full) && new FileInfo(full).Length <= WebCodeMaxBytes ? File.ReadAllText(full) : ""; }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return (403, new { error = x.Message }); }
        var (shown, kinds, numbers) = DiffView(text, edits);
        var lines = kinds.Length == 0 ? [] : shown.Split('\n');
        return (200, new { path = full, lines = lines.Select((l, i) => new { kind = kinds[i].ToString(), num = numbers[i], text = l }).ToList() });
    }

    // The phone's folder browser: directory names only; the drives when no path is given. Hidden, system and unreadable ones are left out.
    private static (int, object) WebFsDirs(string? path)
    {
        static bool Readable(DirectoryInfo d)
        {
            try { using var e = d.EnumerateFileSystemInfos().GetEnumerator(); e.MoveNext(); return true; }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        }
        if (string.IsNullOrEmpty(path))
            return (200, new { path = "", parent = (string?)null, dirs = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new { name = d.Name, path = d.Name }).ToList() });
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) is var full && !Directory.Exists(full))
            return (404, new { error = "No such folder" });
        try
        {
            var dirs = new DirectoryInfo(full).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && Readable(d))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => new { name = d.Name, path = d.FullName }).ToList();
            return (200, new { path = full, parent = Directory.GetParent(full)?.FullName ?? "", dirs });
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return (403, new { error = x.Message }); }
    }

    private sealed record WebSmokeReply(int Status, JsonNode? Body);

    // The Code routes (WebAppSmoke, while on), on temp folders and the smoke's data folder; no Python run is ever started.
    private async Task<bool> WebCodeSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: code: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<WebSmokeReply> Send(string path, object? json = null)
        {
            using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
                { Content = json is null ? null : new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json") };
            request.Headers.Add("Tailscale-User-Login", "smoke@example.com");
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            try { return new((int)response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null); }
            catch (JsonException) { return new((int)response.StatusCode, null); }
        }
        static string Q(string s) => Uri.EscapeDataString(s);

        var temp = Path.Combine(Path.GetTempPath(), $"ao-webcode-{Guid.NewGuid():N}");
        string proj = Path.Combine(temp, "proj"), extra = Path.Combine(temp, "extra"), outside = Path.Combine(temp, "outside");
        string? id = null;
        var once = _settings.Once;
        try
        {
            foreach (var d in new[] { Path.Combine(proj, "src"), extra, outside })
                Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(proj, "a.txt"), "alpha\n");
            File.WriteAllBytes(Path.Combine(proj, "bin.dat"), [1, 0, 2, 0]);
            File.WriteAllText(Path.Combine(extra, "e.txt"), "extra");
            File.WriteAllText(Path.Combine(outside, "o.txt"), "secret");

            if ((await Send("/api/code/new", new { folder = "not\\full" })).Status != 400 || _workflowRoot is null ||
                (await Send("/api/code/new", new { folder = _workflowRoot })) is not { Status: 409 } root || root.Body?["error"]?.ToString() != WorkflowRootRefusal)
                return Fail("new: a bad path is not 400 or the workflow root not 409");
            if (await Send("/api/code/new", new { folder = proj }) is not { Status: 200, Body: { } made } || made["id"]?.ToString() is not { Length: > 0 } madeId)
                return Fail("new: no id for the temp project");
            id = madeId;
            var code = $"/api/code/{id}";
            if (await Send(code) is not { Status: 200, Body: { } snap } || snap["hasProjectMd"]?.GetValue<bool>() != false || snap["folder"]?.ToString() != proj ||
                snap["folders"]!.AsArray().Count != 0 || snap["canStart"]?.GetValue<bool>() != false || snap["running"]?.GetValue<bool>() != false ||
                snap["todo"] is not null || snap["taskTime"]?["seconds"]?.GetValue<int>() != 0 || snap["startLabel"] is null || snap["activity"] is null)
                return Fail($"snapshot: {(await Send(code)).Body}");
            if (await Send(code + "/start", new { }) is not { Status: 409 } start ||
                start.Body?["error"]?.ToString() != "Project not started: add a PROJECT.md to this folder (create it with a model, Add PROJECT.md… or Start from template)" || _process is not null)
                return Fail($"start without PROJECT.md: {(await Send(code + "/start", new { })).Body}");
            if (await Send(code + "/projectmd/template", new { }) is not { Status: 200 } tpl || tpl.Body?["hasProjectMd"]?.GetValue<bool>() != true)
                return Fail("template");
            if (await Send(code + "/projectmd", new { text = "# Hello" }) is not { Status: 200 } md || File.ReadAllText(Path.Combine(proj, "PROJECT.md")) != "# Hello" || md.Body?["text"]?.ToString() != "# Hello")
                return Fail("projectmd");
            if (await Send(code + "/files") is not { Status: 200, Body: { } list } || list["entries"]!.AsArray() is var entries && (entries[0]?["name"]?.ToString() != "src" ||
                entries[0]?["dir"]?.GetValue<bool>() != true || !entries.Any(e => e?["name"]?.ToString() == "a.txt")))
                return Fail($"files: {(await Send(code + "/files")).Body}");
            var link = Path.Combine(proj, "link"); // a junction out of the project, as anyone can make one without admin rights
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                mklink.WaitForExit();
            if (!File.Exists(Path.Combine(link, "o.txt")) || (await Send(code + "/file?path=" + Q(Path.Combine(link, "o.txt")))).Status != 403 || (await Send(code + "/files?dir=" + Q(link))).Status != 403)
                return Fail("a junction out of the project was not refused");
            if (await Send(code + "/file?path=" + Q(Path.Combine(proj, "a.txt"))) is not { Status: 200 } read || read.Body?["text"]?.ToString() != "alpha\n" || read.Body?["language"]?.ToString() != "txt")
                return Fail("file");
            var outsideFile = Path.Combine(outside, "o.txt");
            if ((await Send(code + "/file?path=" + Q(outsideFile))).Status is not (403 or 404) || (await Send(code + "/file?path=" + Q(proj + "\\..\\outside\\o.txt"))).Status == 200 ||
                (await Send(code + "/files?dir=" + Q(outside))).Status == 200 || (await Send(code + "/file?path=" + Q(Path.Combine(proj, "bin.dat")))).Status != 415)
                return Fail("file: outside, .. or a binary file was not refused");
            var extraFile = Q(Path.Combine(extra, "e.txt"));
            if (await Send(code + "/folders/add", new { path = extra, access = "read" }) is not { Status: 200 } add || add.Body?["folders"]?[0]?["access"]?.ToString() != "read" ||
                (await Send(code + "/file?path=" + extraFile)).Status != 200 || (await Send(code + "/folders/add", new { path = extra, access = "read" })).Status != 409)
                return Fail("folders/add");
            if ((await Send(code + "/folders/remove", new { path = "" })).Status != 400)
                return Fail("folders/remove with no path is not 400");
            if (await Send(code + "/folders/remove", new { path = extra }) is not { Status: 200 } remove || remove.Body?["folders"]!.AsArray().Count != 0 || (await Send(code + "/file?path=" + extraFile)).Status != 403)
                return Fail("folders/remove");
            // no access sent (the phone no longer asks): Full edit, as on the PC
            if (await Send(code + "/folders/add", new { path = extra }) is not { Status: 200 } plain || plain.Body?["folders"]?[0]?["access"]?.ToString() != "write" ||
                (await Send(code + "/folders/remove", new { path = extra })).Status != 200)
                return Fail("folders/add without access is not Full edit");
            if (await Send("/api/fs/dirs?path=" + Q(temp)) is not { Status: 200 } dirs || !dirs.Body!["dirs"]!.AsArray().Any(d => d?["name"]?.ToString() == "proj") ||
                dirs.Body["parent"]?.ToString() != Path.GetDirectoryName(temp) || (await Send("/api/fs/dirs")).Status != 200)
                return Fail("fs/dirs");

            var conv = _store.Get(id)!;
            conv.Messages.Add(new("user", "write a.txt"));
            conv.Messages.Add(new("assistant", "Done.", Changes: new(1, [new("a.txt", 1, 0, [new("", "alpha\n")])], [proj])));
            _store.Save(conv);
            var aPath = Path.Combine(proj, "a.txt");
            if (await Send(code + "/changes") is not { Status: 200, Body: { } changes } || changes["files"]?[0] is not { } first || first["path"]?.ToString() != aPath ||
                first["added"]?.GetValue<int>() != 1 || first["msg"]?.GetValue<int>() != conv.Messages.Count - 1)
                return Fail($"changes: {(await Send(code + "/changes")).Body}");
            if (await Send($"{code}/diff?path={Q(aPath)}&msg={conv.Messages.Count - 1}") is not { Status: 200, Body: { } diff } || diff["lines"]?[0]?["kind"]?.ToString() != "+" ||
                diff["lines"]?[0]?["text"]?.ToString() != "alpha" || (await Send($"{code}/diff?path={Q(aPath)}&msg=0")).Status != 404)
                return Fail($"diff: {(await Send($"{code}/diff?path={Q(aPath)}&msg={conv.Messages.Count - 1}")).Body}");
            if (await Send(code + "/runlog") is not { Status: 200 } log || log.Body?["text"] is null)
                return Fail("runlog");
            if (await Send(code + "/once", new { on = !once }) is not { Status: 200 } o || _settings.Once == once || AppSettings.Load().Once == once || o.Body?["once"]?.GetValue<bool>() != !once ||
                (await Send(code + "/once", new { on = "yes" })).Status != 400)
                return Fail("once");
            KeepWebTodo(id, JsonDocument.Parse("""{"e":"tool","name":"todo","args":"{\"items\":[\"[x] plan\",\"[-] build\",\"[ ] ship\"]}"}""").RootElement);
            if (await Send(code) is not { Status: 200, Body: { } withTodo } || withTodo["todo"] is not JsonArray todo || todo.Count != 3 ||
                todo[0]?["state"]?.ToString() != "done" || todo[1]?["state"]?.ToString() != "doing" || todo[2]?["text"]?.ToString() != "ship" || todo[2]?["state"]?.ToString() != "")
                return Fail($"todo: {(await Send(code)).Body?["todo"]}");
            if ((await Send("/api/code/nope")).Status != 404 || (await Send("/api/code/nope/files")).Status != 404)
                return Fail("an unknown conversation is not 404");

            // a complete project restarted from the phone while llama-server is being updated: Start_Click refuses before anything is recycled
            File.WriteAllText(Path.Combine(proj, "STATE.md"), "# State\n\n## Project complete\n");
            var (download, recycle) = (_llamaDownload, _restartRecycle);
            var recycled = new List<string>();
            using var updating = _llamaDownload = new CancellationTokenSource();
            _restartRecycle = recycled.Add;
            try
            {
                if (await Send(code + "/start", new { }) is not { Status: 409 } ask || ask.Body?["restart"]?.GetValue<bool>() != true)
                    return Fail("start on a complete project does not ask to restart");
                if ((await Send(code + "/start", new { restart = true })).Status != 409 || recycled.Count != 0 || _process is not null || !File.Exists(Path.Combine(proj, "a.txt")))
                    return Fail($"restart with llama-server not ready recycled {recycled.Count} items");
            }
            finally { (_llamaDownload, _restartRecycle) = (download, recycle); }
            return true;
        }
        finally
        {
            _settings.Once = once;
            _settings.Save();
            _webTodos.Remove(id ?? "");
            if (id is not null)
            {
                if (_codeConv?.Id == id)
                    CloseCodeConversation();
                _store.Delete(id);
            }
            try { Directory.Delete(temp, recursive: true); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        }
    }
}

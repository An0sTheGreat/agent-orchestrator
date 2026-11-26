using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace AgentOrchestrator;

// The phone app's chat management, Skills and Usage (MainWindow.WebApp.cs routes them; all run on the UI thread). Store writes
// go through StoreChange as the sidebar's do, never its dialogs; skills are written as NewSkill writes them, without its
// dialog or editor; usage is read from _stats.
public partial class MainWindow
{
    private const int WebSkillCap = 256 * 1024; // SKILL.md, both ways

    private static readonly (int, object) NoConversation = (404, new { error = "No such conversation" }), NoProject = (404, new { error = "No such project" });

    private bool WebHasProject(string id) => _store.View().Groups.Any(g => g.Id == id);

    // A store write through the sidebar's StoreChange; 409 when the store refused it (read-only).
    private (int, object) WebStore(Func<bool> change)
    {
        var done = false;
        StoreChange(() => done = change());
        return done ? (200, new { ok = true }) : (409, new { error = "The PC couldn't save the change" });
    }

    private static bool? WebBool(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    // A blank title goes back to the derived one (the store's Rename).
    private (int, object) WebRenameChat(string id, JsonElement body) =>
        _store.Get(id) is null ? NoConversation : WebStore(() => _store.Rename(id, WebField(body, "title")));

    private (int, object) WebArchiveChat(string id, JsonElement body) =>
        _store.Get(id) is null ? NoConversation
        : WebBool(body, "archived") is not { } archived ? (400, new { error = "archived must be true or false" })
        : WebStore(() => _store.Archive(id, archived));

    // project: a project's id, or null (or absent) for out of its project. Archived chats keep their place, as on the sidebar.
    private (int, object) WebMoveChat(string id, JsonElement body)
    {
        if (_store.Get(id) is not { } chat)
            return NoConversation;
        string? project = null;
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("project", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            if (p.ValueKind != JsonValueKind.String)
                return (400, new { error = "project must be a project's id or null" });
            project = p.GetString()!;
        }
        if (chat.Archived)
            return (409, new { error = "Unarchive it first" });
        if (project is not null && !WebHasProject(project))
            return NoProject;
        return WebStore(() => _store.MoveToGroup(id, project));
    }

    private (int, object) WebNewProject(JsonElement body)
    {
        var (name, kind) = (WebField(body, "name").Trim(), WebField(body, "kind"));
        if (name.Length == 0)
            return (400, new { error = "Name the project" });
        if (kind is not ("chat" or "code"))
            return (400, new { error = "kind must be chat or code" });
        string? id = null;
        StoreChange(() => id = _store.CreateGroup(name, kind));
        return id is null ? (409, new { error = "The PC couldn't save the change" }) : (200, new { id });
    }

    private (int, object) WebRenameProject(string id, JsonElement body) =>
        !WebHasProject(id) ? NoProject
        : WebField(body, "name").Trim() is not { Length: > 0 } name ? (400, new { error = "Name the project" })
        : WebStore(() => _store.RenameGroup(id, name));

    // Its chats are kept and move to Recents (the store's DeleteGroup).
    private (int, object) WebDeleteProject(string id) => !WebHasProject(id) ? NoProject : WebStore(() => _store.DeleteGroup(id));

    // ---- Skills

    private object WebSkillJson(LauncherLogic.Skill s) =>
        new { key = Path.GetFileName(s.Folder), name = s.Name, description = s.Description, builtIn = s.BuiltIn, on = HasSkill(s) };

    // Keys are folder names, matched ignoring case as AllSkills' override does.
    private LauncherLogic.Skill? WebFindSkill(string? key) =>
        string.IsNullOrEmpty(key) ? null : AllSkills().FirstOrDefault(s => string.Equals(Path.GetFileName(s.Folder), key, StringComparison.OrdinalIgnoreCase));

    private void RefreshSkillsPageIfShown()
    {
        if (SkillsView.Visibility == Visibility.Visible)
            RenderSkillsPage();
    }

    // The user's first (OrderBy is stable: each group stays by name).
    private (int, object) WebSkills() => (200, new { skills = AllSkills().OrderBy(s => s.BuiltIn).Select(WebSkillJson).ToList() });

    private (int, object) WebSkill(string? key)
    {
        if (WebFindSkill(key) is not { } skill)
            return (404, new { error = "No such skill" });
        var file = Path.Combine(skill.Folder, "SKILL.md");
        try
        {
            if (new FileInfo(file).Length > WebSkillCap)
                return (413, new { error = "Its SKILL.md is over 256 KB: open it on the PC" });
            var text = File.ReadAllText(file);
            return (200, new { key = Path.GetFileName(skill.Folder), name = skill.Name, description = skill.Description, builtIn = skill.BuiltIn, on = HasSkill(skill), text });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (409, new { error = $"Couldn't read it: {e.Message}" });
        }
    }

    private (int, object) WebSetSkill(JsonElement body)
    {
        if (WebFindSkill(WebField(body, "key")) is not { } skill)
            return (404, new { error = "No such skill" });
        if (WebBool(body, "on") is not { } on)
            return (400, new { error = "on must be true or false" });
        SetSkill(Path.GetFileName(skill.Folder), on, skill.BuiltIn);
        RefreshSkillsPageIfShown();
        return (200, new { ok = true });
    }

    // NewSkill without its dialog and editor: the same checks and messages, the same SKILL.md (the phone's description in it), on.
    private (int, object) WebNewSkill(JsonElement body)
    {
        var name = WebField(body, "name").Trim();
        var description = string.Join(' ', WebField(body, "description").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) // Windows drops a trailing dot
            return (400, new { error = "A skill name can't contain \\ / : * ? \" < > | or end with a dot" });
        var folder = Path.Combine(AppSettings.SkillsRoot, name);
        if (Directory.Exists(folder))
            return (400, new { error = "A skill with that name already exists" });
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"),
                $"---\nname: {name}\ndescription: {(description.Length > 0 ? description : "When to use this skill, in one sentence.")}\n---\n\n# {name}\n\n" +
                "Step-by-step instructions the model follows when it uses this skill.\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (409, new { error = $"Could not create the skill: {e.Message}" });
        }
        SetSkill(name, true);
        RefreshSkillsPageIfShown();
        return (200, new { key = name });
    }

    // The user's own skills only, written to SkillsRoot\<key>\SKILL.md and nowhere else.
    private (int, object) WebSaveSkill(JsonElement body)
    {
        if (WebFindSkill(WebField(body, "key")) is not { } skill)
            return (404, new { error = "No such skill" });
        if (skill.BuiltIn)
            return (403, new { error = "Built-in skills can't be edited" });
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
            return (400, new { error = "text is missing" });
        var text = t.GetString()!;
        if (Encoding.UTF8.GetByteCount(text) > WebSkillCap)
            return (413, new { error = "SKILL.md can be at most 256 KB" });
        var root = Path.GetFullPath(AppSettings.SkillsRoot);
        var folder = Path.GetFullPath(skill.Folder);
        var file = Path.GetFullPath(Path.Combine(folder, "SKILL.md"));
        if (!string.Equals(Path.GetDirectoryName(folder), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (403, new { error = "Built-in skills can't be edited" });
        try { File.WriteAllText(file, text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (409, new { error = $"Couldn't save it: {e.Message}" });
        }
        RefreshSkillsPageIfShown(); // its name or description may have changed
        return (200, new { ok = true });
    }

    // ---- Usage

    // This PC's own lifetime; the rest every connected PC's (Combined), as the profile shows them.
    private (int, object) WebUsage()
    {
        var all = _stats.Combined();
        var today = DateTime.Today;
        return (200, new
        {
            lifetime = _stats.Lifetime, combined = all.Lifetime, longestTask = all.LongestTaskSeconds,
            peak = all.Peak, longestStreak = all.LongestStreak, currentStreak = all.CurrentStreak(today),
            days = Enumerable.Range(-6, 7).Select(i => UsageStats.Key(today.AddDays(i))).Select(day => new { day, tokens = all.Daily.GetValueOrDefault(day) }).ToList(),
            pcs = _stats.Others.Select(o => new { name = o.Key, lifetime = o.Value.Lifetime }).Prepend(new { name = Environment.MachineName, lifetime = _stats.Lifetime }).ToList(),
        });
    }

    // The routes above (WebAppSmoke, while on), on the smoke's own data folder and a stand-in workflow root with one built-in
    // skill: rename persists and blank resets; archive and back; a chat project made, a chat moved in (shown under it, with its
    // kind) and out; an archived chat refused; a project renamed and deleted (its chats back to Recents); unknown ids 404;
    // skills made (listed and on), refused (bad name, exists), read, saved (the file changed), a built-in's save refused,
    // switched off; usage's shape.
    private async Task<bool> WebManageSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: manage: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<(int Status, JsonNode? Body)> Send(string path, string? json = null)
        {
            using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
                { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("Tailscale-User-Login", "smoke@example.com");
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
        }
        async Task<JsonNode> Chats() => (await Send("/api/conversations")).Body!;
        static bool In(JsonNode? list, string id) => list!.AsArray().Any(i => i!["id"]!.ToString() == id);
        JsonNode? Project(JsonNode all, string id) => all["projects"]!.AsArray().FirstOrDefault(g => g!["id"]!.ToString() == id);

        if (_store.Create("", "chat") is not { } a || _store.Create("", "chat") is not { } b)
            return Fail("no chats to manage");
        var one = "/api/conversation/";
        var workflow = _workflowRoot;
        try
        {
            var r = await Send(one + a.Id + "/rename", """{"title":"Phone title"}""");
            if (r.Status != 200 || r.Body?["ok"]?.GetValue<bool>() != true ||
                (await Chats())["recents"]!.AsArray().FirstOrDefault(i => i!["id"]!.ToString() == a.Id)?["title"]?.ToString() != "Phone title")
                return Fail($"rename: {r.Status} {r.Body}");
            if ((await Send(one + a.Id + "/rename", """{"title":"  "}""")).Status != 200 || _store.Get(a.Id)?.Title is not null)
                return Fail("a blank rename did not reset the title");

            if ((await Send(one + a.Id + "/archive", """{"archived":true}""")).Status != 200 || !In((await Chats())["archived"], a.Id) || In((await Chats())["recents"], a.Id))
                return Fail("archive");
            if ((await Send(one + a.Id + "/archive", """{"archived":false}""")).Status != 200 || In((await Chats())["archived"], a.Id) || !In((await Chats())["recents"], a.Id))
                return Fail("unarchive");
            if ((await Send(one + a.Id + "/archive", "{}")).Status != 400)
                return Fail("archive without archived was not 400");

            if ((await Send("/api/projects", """{"name":" ","kind":"chat"}""")).Status != 400 || (await Send("/api/projects", """{"name":"P","kind":"other"}""")).Status != 400)
                return Fail("a blank project name or a bad kind was not 400");
            var p = await Send("/api/projects", """{"name":"Phone project","kind":"chat"}""");
            if (p.Status != 200 || p.Body?["id"]?.ToString() is not { Length: > 0 } project)
                return Fail($"new project: {p.Status} {p.Body}");
            if ((await Send(one + a.Id + "/move", $$"""{"project":"{{project}}"}""")).Status != 200 ||
                Project(await Chats(), project) is not { } group || group["kind"]?.ToString() != "chat" || group["name"]?.ToString() != "Phone project" || !In(group["items"], a.Id) ||
                In((await Chats())["recents"], a.Id))
                return Fail($"move in: {await Chats()}");
            if ((await Send(one + a.Id + "/move", """{"project":null}""")).Status != 200 || !In((await Chats())["recents"], a.Id) || In(Project(await Chats(), project)!["items"], a.Id))
                return Fail("move out");
            _store.Archive(b.Id, true);
            var m = await Send(one + b.Id + "/move", $$"""{"project":"{{project}}"}""");
            if (m.Status != 409 || m.Body?["error"]?.ToString() != "Unarchive it first" || _store.GroupOf(b.Id) is not null)
                return Fail($"move of an archived chat: {m.Status} {m.Body}");
            if ((await Send(one + a.Id + "/move", $$"""{"project":"{{project}}"}""")).Status != 200 ||
                (await Send($"/api/project/{project}/rename", """{"name":"Renamed"}""")).Status != 200 || Project(await Chats(), project)?["name"]?.ToString() != "Renamed")
                return Fail("project rename");
            if ((await Send($"/api/project/{project}/delete", "{}")).Status != 200 || Project(await Chats(), project) is not null || !In((await Chats())["recents"], a.Id))
                return Fail("project delete");

            if ((await Send(one + "nope/rename", """{"title":"x"}""")).Status != 404 || (await Send(one + "nope/archive", """{"archived":true}""")).Status != 404 ||
                (await Send(one + "nope/move", "{}")).Status != 404 || (await Send(one + a.Id + "/move", """{"project":"nope"}""")).Status != 404 ||
                (await Send("/api/project/nope/rename", """{"name":"x"}""")).Status != 404 || (await Send("/api/project/nope/delete", "{}")).Status != 404 ||
                (await Send(one + "rename", "{}")).Status != 404 || (await Send("/api/project/delete", "{}")).Status != 404)
                return Fail("an unknown chat or project was not 404");

            // Skills: a stand-in workflow root holds the one built-in.
            _workflowRoot = Path.Combine(AppSettings.DataRoot, "smoke-workflow");
            var builtIn = Path.Combine(_workflowRoot, "skills", "builtin-smoke", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(builtIn)!);
            File.WriteAllText(builtIn, "---\nname: builtin-smoke\ndescription: Ships with the app.\n---\n");
            var n = await Send("/api/skills/new", """{"name":"phone-skill","description":"Made on the phone."}""");
            var mine = Path.Combine(AppSettings.SkillsRoot, "phone-skill", "SKILL.md");
            if (n.Status != 200 || n.Body?["key"]?.ToString() != "phone-skill" || !File.ReadAllText(mine).Contains("description: Made on the phone.") ||
                (await Send("/api/skills")).Body?["skills"]?.AsArray() is not [{ } first, { } second] ||
                first["key"]?.ToString() != "phone-skill" || first["on"]?.GetValue<bool>() != true || first["builtIn"]?.GetValue<bool>() != false ||
                second["key"]?.ToString() != "builtin-smoke" || second["builtIn"]?.GetValue<bool>() != true || second["on"]?.GetValue<bool>() != true)
                return Fail($"new skill: {n.Status} {n.Body} {(await Send("/api/skills")).Body}");
            var bad = await Send("/api/skills/new", """{"name":"a/b","description":""}""");
            var exists = await Send("/api/skills/new", """{"name":"PHONE-SKILL","description":""}""");
            if (bad.Status != 400 || bad.Body?["error"]?.ToString() != "A skill name can't contain \\ / : * ? \" < > | or end with a dot" ||
                exists.Status != 400 || exists.Body?["error"]?.ToString() != "A skill with that name already exists")
                return Fail($"a bad or taken skill name: {bad.Status} {bad.Body}, {exists.Status} {exists.Body}");
            var read = await Send("/api/skill?key=PHONE-SKILL");
            if (read.Status != 200 || read.Body?["key"]?.ToString() != "phone-skill" || read.Body["text"]?.ToString() != File.ReadAllText(mine) ||
                (await Send("/api/skill?key=nope")).Status != 404)
                return Fail($"skill read: {read.Status} {read.Body}");
            if ((await Send("/api/skills/save", """{"key":"phone-skill","text":"---\nname: phone-skill\n---\nEdited.\n"}""")).Status != 200 ||
                File.ReadAllText(mine) != "---\nname: phone-skill\n---\nEdited.\n")
                return Fail("skill save");
            if ((await Send("/api/skills/save", """{"key":"builtin-smoke","text":"x"}""")).Status != 403 || !File.ReadAllText(builtIn).Contains("Ships with the app.") ||
                (await Send("/api/skills/save", JsonSerializer.Serialize(new { key = "phone-skill", text = new string('x', WebSkillCap + 1) }))).Status != 413)
                return Fail("a built-in's save or an oversized save was not refused");
            if ((await Send("/api/skills/set", """{"key":"phone-skill","on":false}""")).Status != 200 ||
                (await Send("/api/skills")).Body?["skills"]?.AsArray().First(s => s!["key"]!.ToString() == "phone-skill")?["on"]?.GetValue<bool>() != false ||
                AppSettings.Load().Skills.Contains("phone-skill", StringComparer.OrdinalIgnoreCase))
                return Fail("skill switched off");

            _stats.Add(500, DateTime.Now);
            var u = await Send("/api/usage");
            if (u.Status != 200 || u.Body is not { } usage || usage["days"]?.AsArray() is not { Count: 7 } days || days[6]!["day"]?.ToString() != UsageStats.Key(DateTime.Today) ||
                days[6]!["tokens"]!.GetValue<long>() < 500 || usage["lifetime"]!.GetValue<long>() != _stats.Lifetime || usage["combined"] is null ||
                usage["longestTask"] is null || usage["peak"] is null || usage["pcs"]?[0]?["name"]?.ToString() != Environment.MachineName)
                return Fail($"usage: {u.Status} {u.Body}");
            return true;
        }
        finally
        {
            _workflowRoot = workflow;
            _store.Delete(a.Id);
            _store.Delete(b.Id);
        }
    }
}

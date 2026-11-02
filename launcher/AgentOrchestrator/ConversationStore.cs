using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator;

// Saved conversations (Code, and the Chat view's: Kind "chat", no folder) by id (<data>\conversations\<id>.json) and the sidebar's order of them (<data>\sidebar.json):
// ungrouped chats and named groups ("projects"), each an ordered id list, newest at the top. An archived chat keeps its
// place in its list (unarchiving puts it back there); the read model shows it apart. Several chats may share a folder.
// ponytail: no locking, one instance owned by the UI thread; a second writer (another launcher) would lose updates.
internal sealed class ConversationStore
{
    public sealed class Conversation
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; } // null: derived by TitleOf; set by Rename or a model-written title (first turn)
        public string Kind { get; set; } = "code"; // "code" or "chat"
        public string Folder { get; set; } = ""; // "" for a chat
        public List<LauncherLogic.CodeFolder> Folders { get; set; } = new();
        public string Worker { get; set; } = "implementer";
        public List<LauncherLogic.ChatMessage> Messages { get; set; } = new();
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public bool Archived { get; set; }
        public long? Tokens { get; set; } // tokens used by all turns of this conversation (the footer's Task total); absent in older files
        public ContextSnapshot? Context { get; set; } // the last turn's context usage; absent in older files
        public Dictionary<string, ContextSnapshot>? Contexts { get; set; } // Code: each worker's (by role, the lead's too); absent in older files
        public string? Summary { get; set; } // the engine's summary of Messages[..SummaryCovers] (history_compacted); null: none yet
        public int SummaryCovers { get; set; }
    }

    private sealed class Group
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Collapsed { get; set; }
        public List<string> Items { get; set; } = new();
        public string Kind { get; set; } = ""; // the view it was made in ("chat" or "code"); "" in older files
    }

    private sealed class Index
    {
        public List<Group> Groups { get; set; } = new();
        public List<string> Items { get; set; } = new();
        public bool ArchivedCollapsed { get; set; } = true;
    }

    public sealed record Entry(string Id, string Title, string Folder, DateTime Updated, string Kind);
    public sealed record GroupView(string Id, string Name, bool Collapsed, IReadOnlyList<Entry> Items, string Kind = "");
    public sealed record SidebarView(IReadOnlyList<GroupView> Groups, IReadOnlyList<Entry> Ungrouped, IReadOnlyList<Entry> Archived, bool ArchivedCollapsed);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly Regex IdName = new("^[0-9a-f]{32}$"), LegacyName = new("^[0-9a-f]{16}$");

    private readonly string _dir, _indexPath;
    private readonly Dictionary<string, Conversation> _conversations = new();
    private Index _index = new();

    public ConversationStore(string dataRoot)
    {
        _dir = Path.Combine(dataRoot, "conversations");
        _indexPath = Path.Combine(dataRoot, "sidebar.json");
    }

    // Why the last Load could not read everything (a locked file, access denied); null when it did. The store is then
    // read-only for the session: it shows what it read, every change returns false, and sidebar.json is never rewritten
    // from a partial read.
    public string? LoadError { get; private set; }
    public bool ReadOnly => LoadError is not null;

    // Reads everything, migrating legacy one-per-folder files first. Corrupt files are skipped, index ids without a
    // conversation dropped, and conversations missing from the index appended to the ungrouped list (newest first).
    public void Load()
    {
        _conversations.Clear();
        _index = new();
        LoadError = "the conversations folder could not be read"; // a Load that throws leaves the store read-only
        string? error = null;
        Directory.CreateDirectory(_dir);
        foreach (var path in Directory.GetFiles(_dir, "*.json").Where(p => LegacyName.IsMatch(Path.GetFileNameWithoutExtension(p))))
            error ??= Migrate(path);
        foreach (var path in Directory.GetFiles(_dir, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                if (IdName.IsMatch(id) && Read<Conversation>(path) is { } c)
                    _conversations[id] = Clean(c, id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error ??= e.Message; }
        }

        Index? read = null;
        try { read = Read<Index>(_indexPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error ??= e.Message; }
        var index = read ?? new();
        var seen = new HashSet<string>();
        List<string> Keep(List<string>? ids) => (ids ?? []).Where(id => id is not null && _conversations.ContainsKey(id) && seen.Add(id)).ToList();
        index.Groups = (index.Groups ?? []).Where(g => g is not null).ToList();
        foreach (var g in index.Groups)
        {
            g.Id = string.IsNullOrEmpty(g.Id) ? NewId() : g.Id;
            g.Name ??= "";
            g.Items = Keep(g.Items);
        }
        index.Groups = index.Groups.DistinctBy(g => g.Id).ToList();
        index.Items = Keep(index.Items);
        index.Items.AddRange(_conversations.Values.Where(c => !seen.Contains(c.Id)).OrderByDescending(c => c.Updated).Select(c => c.Id));
        _index = index;
        LoadError = error;
        if (!ReadOnly)
            SaveIndex();
    }

    // Null when the store is read-only.
    public Conversation? Create(string folder, string kind = "code")
    {
        if (ReadOnly)
            return null;
        var now = DateTime.Now;
        var c = new Conversation { Id = NewId(), Kind = kind, Folder = folder, Created = now, Updated = now };
        _conversations[c.Id] = c;
        Write(PathOf(c.Id), c);
        _index.Items.Insert(0, c.Id);
        SaveIndex();
        return c;
    }

    // False (nothing written) when the conversation was deleted meanwhile: a late save never brings it back.
    public bool Save(Conversation conversation)
    {
        if (ReadOnly || !_conversations.ContainsKey(conversation.Id))
            return false;
        conversation.Updated = DateTime.Now;
        _conversations[conversation.Id] = conversation;
        Write(PathOf(conversation.Id), conversation);
        return true;
    }

    public Conversation? Get(string id) => _conversations.GetValueOrDefault(id);

    // In a group or archived: the user organized it, so it is kept even when empty.
    public bool IsOrganized(string id) => Get(id) is { Archived: true } || _index.Groups.Any(g => g.Items.Contains(id));

    // A blank title goes back to the derived one.
    public bool Rename(string id, string? title) => Change(id, c => c.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim());

    public bool Archive(string id, bool archived) => Change(id, c => c.Archived = archived);

    public bool Delete(string id)
    {
        if (ReadOnly || !_conversations.Remove(id))
            return false;
        File.Delete(PathOf(id));
        ListOf(id)?.Remove(id);
        SaveIndex();
        return true;
    }

    // Within its list, past chats of the other archived state (so a visible chat swaps with its visible neighbour).
    public bool MoveUp(string id) => Move(id, -1);
    public bool MoveDown(string id) => Move(id, 1);

    // To the top of the group's list, or of the ungrouped list when groupId is null.
    public bool MoveToGroup(string id, string? groupId) => MoveTo(id, groupId, null);

    // Into the group's list (the ungrouped one when groupId is null): at the top when at is null, else taking at's place
    // (a drop on a row: in the same list it lands after at when it came from above, so both directions move it).
    public bool MoveTo(string id, string? groupId, string? at)
    {
        var target = groupId is null ? _index.Items : FindGroup(groupId)?.Items;
        if (ReadOnly || target is null || id == at || (at is not null && !target.Contains(at)) || ListOf(id) is not { } from)
            return false;
        var after = from == target && at is not null && from.IndexOf(id) < from.IndexOf(at);
        from.Remove(id);
        target.Insert(at is null ? 0 : target.IndexOf(at) + (after ? 1 : 0), id);
        SaveIndex();
        return true;
    }

    // Null when the store is read-only.
    // kind: the view it is made in ("chat" or "code"); an empty project shows only there.
    public string? CreateGroup(string name, string kind = "")
    {
        if (ReadOnly)
            return null;
        var g = new Group { Id = NewId(), Name = name.Trim(), Kind = kind };
        _index.Groups.Insert(0, g);
        SaveIndex();
        return g.Id;
    }

    public bool RenameGroup(string groupId, string name) => ChangeGroup(groupId, g => g.Name = name.Trim());
    public bool SetGroupCollapsed(string groupId, bool collapsed) => ChangeGroup(groupId, g => g.Collapsed = collapsed);

    // The group's chats (archived ones too) move, in order, to the top of the ungrouped list; none is deleted.
    public bool DeleteGroup(string groupId)
    {
        if (ReadOnly || FindGroup(groupId) is not { } g)
            return false;
        _index.Groups.Remove(g);
        _index.Items.InsertRange(0, g.Items);
        SaveIndex();
        return true;
    }

    // The group takes at's place (a drop on another project's header).
    public bool MoveGroupTo(string groupId, string at)
    {
        var (from, to) = (_index.Groups.FindIndex(g => g.Id == groupId), _index.Groups.FindIndex(g => g.Id == at));
        if (ReadOnly || from < 0 || to < 0 || from == to)
            return false;
        var g = _index.Groups[from];
        _index.Groups.RemoveAt(from);
        _index.Groups.Insert(to, g);
        SaveIndex();
        return true;
    }

    public bool MoveGroupUp(string groupId) => MoveGroup(groupId, -1);
    public bool MoveGroupDown(string groupId) => MoveGroup(groupId, 1);

    public bool SetArchivedCollapsed(bool collapsed)
    {
        if (ReadOnly)
            return false;
        _index.ArchivedCollapsed = collapsed;
        SaveIndex();
        return true;
    }

    // Groups in order with their non-archived chats, the ungrouped non-archived chats, then every archived chat
    // (groups' first, in list order).
    public SidebarView View()
    {
        Entry ToEntry(string id) => new(id, TitleOf(_conversations[id]), _conversations[id].Folder, _conversations[id].Updated, _conversations[id].Kind);
        List<Entry> Pick(IEnumerable<string> ids, bool archived) => ids.Where(id => _conversations[id].Archived == archived).Select(ToEntry).ToList();
        return new(
            _index.Groups.Select(g => new GroupView(g.Id, g.Name, g.Collapsed, Pick(g.Items, false), g.Kind)).ToList(),
            Pick(_index.Items, false),
            Pick(_index.Groups.SelectMany(g => g.Items).Concat(_index.Items), true),
            _index.ArchivedCollapsed);
    }

    // The Rename title, else the first user message (whitespace collapsed, about 40 chars), else the folder's leaf name.
    public static string TitleOf(Conversation c)
    {
        if (c.Title is { } title)
            return title;
        var first = c.Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
        var text = string.Join(' ', first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 0)
            return text.Length <= 40 ? text : text[..(char.IsHighSurrogate(text[39]) ? 39 : 40)].TrimEnd() + "…";
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(c.Folder));
        return leaf.Length > 0 ? leaf : c.Folder.Length > 0 ? c.Folder : "New conversation";
    }

    private bool Change(string id, Action<Conversation> change)
    {
        if (ReadOnly || Get(id) is not { } c)
            return false;
        change(c);
        Write(PathOf(id), c);
        return true;
    }

    private bool ChangeGroup(string groupId, Action<Group> change)
    {
        if (ReadOnly || FindGroup(groupId) is not { } g)
            return false;
        change(g);
        SaveIndex();
        return true;
    }

    private bool Move(string id, int step)
    {
        if (ReadOnly || ListOf(id) is not { } list)
            return false;
        var i = list.IndexOf(id);
        for (var j = i + step; j >= 0 && j < list.Count; j += step)
        {
            if (_conversations[list[j]].Archived != _conversations[id].Archived)
                continue;
            list.RemoveAt(i);
            list.Insert(j, id);
            SaveIndex();
            return true;
        }
        return false;
    }

    private bool MoveGroup(string groupId, int step)
    {
        var i = _index.Groups.FindIndex(g => g.Id == groupId);
        if (ReadOnly || i < 0 || i + step < 0 || i + step >= _index.Groups.Count)
            return false;
        (_index.Groups[i], _index.Groups[i + step]) = (_index.Groups[i + step], _index.Groups[i]);
        SaveIndex();
        return true;
    }

    private Group? FindGroup(string groupId) => _index.Groups.Find(g => g.Id == groupId);

    private List<string>? ListOf(string id) =>
        _index.Items.Contains(id) ? _index.Items : _index.Groups.Find(g => g.Items.Contains(id))?.Items;

    // A legacy <hash>.json becomes an id-based conversation, then is renamed .migrated so it is converted once.
    // A corrupt one is left alone. An IO error (locked, access denied) is returned, and a converted copy already written
    // is removed again, so the next Load retries without a duplicate.
    // ponytail: a crash between the two steps, or a failed removal, migrates it twice (a duplicate chat).
    private string? Migrate(string path)
    {
        string? written = null;
        try
        {
            if (Read<LauncherLogic.CodeConversation>(path) is not { } old)
                return null;
            var c = Clean(new Conversation
            {
                Folder = old.Folder, Folders = old.Folders, Worker = old.Worker, Messages = old.Messages, Created = old.Updated, Updated = old.Updated,
            }, NewId());
            written = PathOf(c.Id);
            Write(written, c);
            File.Move(path, Path.ChangeExtension(path, ".migrated"), overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (written is not null)
                try { File.Delete(written); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            return e.Message;
        }
    }

    // Like LauncherLogic.LoadConversation: hand-edited or partial files get empty lists and lose invalid entries.
    private static Conversation Clean(Conversation c, string id)
    {
        c.Id = id; // the file name is the id
        c.Title = string.IsNullOrWhiteSpace(c.Title) ? null : c.Title;
        c.Kind = c.Kind == "chat" ? "chat" : "code";
        c.Folder ??= "";
        c.Worker ??= "implementer";
        c.Messages = (c.Messages ?? []).Where(m => m is { Role: "user" or "assistant", Content: not null }).ToList();
        c.Folders = (c.Folders ?? []).Where(f => f is { Path.Length: > 0 }).Select(f => f.Access == "write" ? f : f with { Access = "read" }).ToList();
        if (string.IsNullOrWhiteSpace(c.Summary) || c.SummaryCovers < 0 || c.SummaryCovers > c.Messages.Count)
            (c.Summary, c.SummaryCovers) = (null, 0); // a summary that does not fit the messages: the engine summarizes afresh
        return c;
    }

    // Null for a missing or corrupt file. An IO error (locked, access denied) is thrown: the file is there but unread.
    private static T? Read<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException or NotSupportedException) { return null; }
    }

    // Atomic: a crash leaves the old file or the new one, never half of one.
    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }

    private void SaveIndex() => Write(_indexPath, _index);
    private string PathOf(string id) => Path.Combine(_dir, id + ".json");
    private static string NewId() => Guid.NewGuid().ToString("N");

    public static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("ConversationStore self-test failed: " + what);
        }
        static string Ids(IEnumerable<Entry> entries) => string.Join(",", entries.Select(e => e.Id));
        static string Of(params Conversation[] cs) => string.Join(",", cs.Select(c => c.Id));

        var root = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-store-{Guid.NewGuid():N}");
        var conversations = Path.Combine(root, "conversations");
        try
        {
            var s = new ConversationStore(root);
            s.Load();
            Check(s.View() is { Groups.Count: 0, Ungrouped.Count: 0, Archived.Count: 0 }, "empty store");
            ConversationStore Reload()
            {
                var fresh = new ConversationStore(root);
                fresh.Load();
                return fresh;
            }

            // Round trip; two chats on one folder; newest at the top; derived titles.
            var a = s.Create(@"C:\Work\Alpha")!;
            var b = s.Create(@"C:\Work\Alpha")!;
            var c = s.Create(@"C:\Work\Gamma\")!;
            Check(a.Id != b.Id && IdName.IsMatch(a.Id) && File.Exists(Path.Combine(conversations, a.Id + ".json")), "create");
            a.Messages.Add(new("user", "  Fix   the\nlogin bug please, it fails whenever the password has a space ", new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2))));
            a.Messages.Add(new("assistant", "Done."));
            a.Folders.Add(new(@"C:\Ref", "read"));
            a.Worker = "reviewer";
            Check(s.Save(a), "save");
            s = Reload();
            var la = s.Get(a.Id);
            Check(la is not null && la.Folder == a.Folder && la.Worker == "reviewer" && la.Created == a.Created &&
                  la.Messages.SequenceEqual(a.Messages) && la.Folders.SequenceEqual(a.Folders), "round trip");
            Check(la!.Messages[0].Time == new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2)) && la.Messages[1].Time is null, "message time saved and loaded");
            Check(Ids(s.View().Ungrouped) == Of(c, b, a), "newest first");
            Check(TitleOf(la!) == "Fix the login bug please, it fails whene…" && TitleOf(s.Get(b.Id)!) == "Alpha" &&
                  TitleOf(s.Get(c.Id)!) == "Gamma" && TitleOf(new Conversation()) == "New conversation", "derived titles");
            var lb = s.Get(b.Id)!;
            Check(s.Rename(b.Id, " My chat ") && s.Save(lb), "rename");
            lb.Messages.Add(new("user", "later message"));
            s.Save(lb);
            s = Reload();
            Check(s.View().Ungrouped[1].Title == "My chat", "rename sticks");

            // Ordering within a list, at its bounds, persisted.
            Check(!s.MoveUp(c.Id) && !s.MoveDown(a.Id) && !s.MoveUp("missing"), "move at bounds");
            Check(s.MoveDown(c.Id) && Ids(s.View().Ungrouped) == Of(b, c, a), "move down");
            Check(s.MoveUp(a.Id) && Ids(s.View().Ungrouped) == Of(b, a, c), "move up");
            s = Reload();
            Check(Ids(s.View().Ungrouped) == Of(b, a, c), "order persists");
            // Drag: takes the target's place from either direction; onto itself or a target in another list is refused.
            Check(s.MoveTo(b.Id, null, c.Id) && Ids(s.View().Ungrouped) == Of(a, c, b), "drag down");
            Check(s.MoveTo(b.Id, null, a.Id) && Ids(s.View().Ungrouped) == Of(b, a, c), "drag up");
            Check(!s.MoveTo(b.Id, null, b.Id) && !s.MoveTo(b.Id, "missing", null) && !s.MoveTo("missing", null, null), "drag refused");

            // Groups: create (top), move at bounds, move chats in, rename, collapse, delete (chats survive).
            var g1 = s.CreateGroup("Project One")!;
            var g2 = s.CreateGroup("Two")!;
            Check(string.Join(",", s.View().Groups.Select(g => g.Id)) == $"{g2},{g1}", "new group on top");
            Check(s.MoveGroupTo(g2, g1) && string.Join(",", s.View().Groups.Select(g => g.Id)) == $"{g1},{g2}" && s.MoveGroupTo(g2, g1) &&
                  !s.MoveGroupTo(g2, g2) && !s.MoveGroupTo(g2, "missing"), "group drag");
            Check(!s.MoveGroupUp(g2) && s.MoveGroupDown(g2) && !s.MoveGroupDown(g2) && !s.MoveGroupUp("missing"), "group moves");
            Check(s.MoveToGroup(a.Id, g1) && s.MoveTo(c.Id, g1, a.Id) && Ids(s.View().Groups.Single(g => g.Id == g1).Items) == Of(c, a) &&
                  !s.MoveTo(b.Id, g1, b.Id) && !s.MoveTo(b.Id, null, a.Id) && !s.MoveToGroup(b.Id, "missing"), "move to group");
            Check(s.RenameGroup(g1, "Renamed") && s.SetGroupCollapsed(g1, true), "group rename/collapse");
            s = Reload();
            var v = s.View();
            Check(v.Groups[0] is { Name: "Renamed", Collapsed: true } grp && grp.Id == g1 && Ids(grp.Items) == Of(c, a) &&
                  Ids(v.Ungrouped) == Of(b), "groups persist");
            Check(s.MoveDown(c.Id) && Ids(s.View().Groups[0].Items) == Of(a, c), "move within group");
            Check(s.DeleteGroup(g1) && !s.DeleteGroup(g1), "delete group");
            v = s.View();
            Check(v.Groups.Count == 1 && Ids(v.Ungrouped) == Of(a, c, b) && s.Get(a.Id) is not null && s.Get(c.Id) is not null &&
                  File.Exists(Path.Combine(conversations, c.Id + ".json")), "deleted group's chats survive");

            // Archive: hidden from its list, moves skip it, unarchive puts it back; archived in a group too.
            Check(s.Archive(c.Id, true) && Ids(s.View().Ungrouped) == Of(a, b) && Ids(s.View().Archived) == Of(c), "archive");
            Check(s.MoveDown(a.Id) && Ids(s.View().Ungrouped) == Of(b, a), "move skips archived");
            Check(s.MoveToGroup(b.Id, g2) && s.Archive(b.Id, true), "archive in group");
            s.SetArchivedCollapsed(true);
            s = Reload();
            v = s.View();
            Check(Ids(v.Archived) == Of(b, c) && v.Groups[0].Items.Count == 0 && v.ArchivedCollapsed, "archived persists");
            Check(s.Archive(c.Id, false) && Ids(s.View().Ungrouped) == Of(c, a) && Ids(s.View().Archived) == Of(b), "unarchive");

            // Delete removes the file and the entry; a late Save does not resurrect it.
            Check(s.Delete(a.Id) && !s.Delete(a.Id) && s.Get(a.Id) is null && !File.Exists(Path.Combine(conversations, a.Id + ".json")), "delete");
            Check(!s.Save(a) && !File.Exists(Path.Combine(conversations, a.Id + ".json")) && Ids(s.View().Ungrouped) == Of(c), "no resurrection");

            // A Chat-view chat: kind and no folder round-trip; an unknown kind loads as code, and an old file's mode is ignored.
            var chat = s.Create("", "chat")!;
            chat.Messages.Add(new("user", "plain question"));
            s.Save(chat);
            File.WriteAllText(Path.Combine(conversations, new string('d', 32) + ".json"), """{"kind":"bogus","mode":"multi","folder":"C:\\K"}""");
            s = Reload();
            Check(s.Get(chat.Id) is { Kind: "chat", Folder: "" } && s.View().Ungrouped[0] is { Kind: "chat", Title: "plain question" } &&
                  s.Get(new string('d', 32)) is { Kind: "code" } && !File.ReadAllText(Path.Combine(conversations, chat.Id + ".json")).Contains("\"mode\""), "chat kind");
            s.Delete(chat.Id);
            s.Delete(new string('d', 32));

            // Corrupt index: every conversation survives, ungrouped.
            File.WriteAllText(Path.Combine(root, "sidebar.json"), "{ not json");
            s = Reload();
            v = s.View();
            Check(v.Groups.Count == 0 && Ids(v.Ungrouped) == Of(c) && Ids(v.Archived) == Of(b), "corrupt index tolerated");
            // Hand-edited index: unknown ids, duplicates, null lists and a nameless group.
            File.WriteAllText(Path.Combine(root, "sidebar.json"),
                $$"""{"groups":[{"id":"x","name":null,"items":["{{new string('0', 32)}}","{{b.Id}}","{{b.Id}}",null]},null],"items":null}""");
            s = Reload();
            v = s.View();
            Check(v.Groups is [{ Id: "x", Name: "" }] && Ids(v.Archived) == Of(b) && Ids(v.Ungrouped) == Of(c), "index cleaned");
            // Corrupt conversation file: skipped, its index entry dropped, no throw.
            File.WriteAllText(Path.Combine(conversations, c.Id + ".json"), "]]");
            s = Reload();
            Check(s.Get(c.Id) is null && s.View().Ungrouped.Count == 0 && s.Get(b.Id) is not null, "corrupt conversation tolerated");
            // Hand-edited conversation: null lists load empty, invalid messages and folders are dropped, unknown access is read.
            var edited = new string('e', 32);
            File.WriteAllText(Path.Combine(conversations, edited + ".json"), """
                {"messages":[null,{"role":"system","content":"x"},{"role":"user"},{"role":"user","content":"ok"}],
                 "folders":[null,{"path":""},{"path":"Y:\\lib","access":"bogus"},{"path":"Y:\\w","access":"write"}],"worker":null,"folder":null,
                 "summary":"S","summaryCovers":1}
                """);
            File.WriteAllText(Path.Combine(conversations, c.Id + ".json"), """{"messages":null,"folders":null,"summary":"S","summaryCovers":1}""");
            s = Reload();
            Check(s.Get(edited) is { Folder: "", Worker: "implementer", Summary: "S", SummaryCovers: 1 } e && e.Messages.SequenceEqual([new LauncherLogic.ChatMessage("user", "ok")]) &&
                  e.Folders.SequenceEqual([new LauncherLogic.CodeFolder(@"Y:\lib", "read"), new LauncherLogic.CodeFolder(@"Y:\w", "write")]) &&
                  s.Get(c.Id) is { Messages.Count: 0, Folders.Count: 0, Summary: null, SummaryCovers: 0 }, "hand-edited conversation cleaned");
            s.Delete(edited);
            s.Delete(c.Id);

            // Legacy migration: converted once, folder and messages kept, file renamed .migrated.
            var legacyPath = LauncherLogic.ConversationPath(@"C:\Work\Legacy", root);
            File.WriteAllText(legacyPath, """{"folder":"C:\\Work\\Legacy","folders":[{"path":"C:\\Ref","access":"write"}],"worker":"planner","messages":[{"role":"user","content":"old question"},{"role":"assistant","content":"old answer"}],"updated":"2026-01-02T03:04:05"}""");
            s = Reload();
            var migrated = s.View().Ungrouped;
            Check(migrated.Count == 1 && s.Get(migrated[0].Id) is { Folder: @"C:\Work\Legacy", Worker: "planner", Messages.Count: 2 } m &&
                  m.Folders.SequenceEqual([new LauncherLogic.CodeFolder(@"C:\Ref", "write")]) && migrated[0].Title == "old question" &&
                  !File.Exists(legacyPath) && File.Exists(Path.ChangeExtension(legacyPath, ".migrated")), "legacy migration");
            s = Reload();
            Check(s.View().Ungrouped.Count == 1 && Ids(s.View().Ungrouped) == Ids(migrated), "migrated once");

            // A file that cannot be read (locked) is not a corrupt one: the store turns read-only, every change is refused,
            // and sidebar.json is never rewritten, so a grouped chat keeps its place once the file can be read again.
            var kept = s.Create(@"C:\Work\Kept")!;
            var keptGroup2 = s.CreateGroup("Kept 2")!;
            var keptGroup = s.CreateGroup("Kept")!;
            Check(s.MoveToGroup(kept.Id, keptGroup) && !s.ReadOnly, "lock setup");
            var indexPath = Path.Combine(root, "sidebar.json");
            var indexBytes = File.ReadAllBytes(indexPath);
            bool IndexUnchanged() => File.ReadAllBytes(indexPath).SequenceEqual(indexBytes);
            using (new FileStream(Path.Combine(conversations, kept.Id + ".json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                s = Reload();
                Check(s.ReadOnly && s.Get(kept.Id) is null && s.Create(@"C:\Work\X") is null && s.CreateGroup("X") is null && !s.DeleteGroup(keptGroup) &&
                      !s.Save(s.Get(migrated[0].Id)!) && !s.Archive(migrated[0].Id, true) && !s.SetArchivedCollapsed(false) &&
                      !s.MoveTo(migrated[0].Id, keptGroup, null) && !s.MoveGroupTo(keptGroup, keptGroup2), "locked conversation: read-only");
            }
            s = Reload();
            Check(IndexUnchanged() && !s.ReadOnly && Ids(s.View().Groups.Single(g => g.Id == keptGroup).Items) == kept.Id, "locked conversation: group kept");
            indexBytes = File.ReadAllBytes(indexPath);
            using (new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                s = Reload();
                Check(s.ReadOnly && s.Get(kept.Id) is not null && s.Create(@"C:\Work\X") is null, "locked index: read-only");
            }
            Check(IndexUnchanged(), "locked index: unchanged");

            // A locked legacy file: unreadable leaves nothing behind; readable but not renamable removes its converted copy.
            // Once unlocked it is migrated exactly once.
            var legacy2 = LauncherLogic.ConversationPath(@"C:\Work\Legacy2", root);
            File.WriteAllText(legacy2, """{"folder":"C:\\Work\\Legacy2","messages":[{"role":"user","content":"second legacy"}]}""");
            var files = Directory.GetFiles(conversations, "*.json").Length;
            foreach (var share in new[] { FileShare.None, FileShare.Read })
                using (new FileStream(legacy2, FileMode.Open, FileAccess.Read, share))
                {
                    s = Reload();
                    Check(s.ReadOnly && s.Create(@"C:\Work\X") is null && Directory.GetFiles(conversations, "*.json").Length == files, $"locked legacy ({share}): read-only, no copy");
                }
            Check(IndexUnchanged() && s.View().Groups.Any(g => g.Id == keptGroup), "locked legacy: groups kept");
            s = Reload();
            Check(!s.ReadOnly && !File.Exists(legacy2) && Directory.GetFiles(conversations, "*.json").Length == files &&
                  s.View().Ungrouped.Count(e => e.Title == "second legacy") == 1, "locked legacy: migrated once after unlock");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

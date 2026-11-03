using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace AgentOrchestrator;

// Remote access (docs/remote-access.md): the glue between RemoteServer and the window. Every action runs on the UI thread
// through the desktop's own paths: open the conversation, put the text in its composer and SendOrQueue; Stop is StopWork;
// an answer resolves the open Choose popup as its button would. Settings (the switch, the password) and the tunnel call
// StartRemoteServer / StopRemoteServer and read RemotePort; RemoteStateChanged fires when either changes it.
public partial class MainWindow
{
    private RemoteServer? _remote;
    internal int? RemotePort => _remote?.Port; // the bound port; null while stopped
    internal event Action? RemoteStateChanged;
    private bool _dialogsQuiet; // --ui-smoke: popups open without taking the focus

    // The open Choose popup (null: none) and the conversation that was replying when it opened (null: none).
    private sealed record RemotePrompt(string? Conversation, string Title, string Message, (string Label, string Value)[] Options, Action<string> Resolve);
    private RemotePrompt? _remotePrompt;

    // Idempotent. False without a password or a free port.
    internal bool StartRemoteServer()
    {
        if (_remote is not null)
            return true;
        if (string.IsNullOrEmpty(_settings.RemotePasswordHash))
            return false;
        var server = new RemoteServer(() => _settings.RemotePasswordHash, (method, path, body) => Dispatcher.InvokeAsync(() => RemoteAct(method, path, body)).Task);
        if (!server.Start(_settings.RemotePort))
        {
            Log($"Remote access: no free port from {_settings.RemotePort}.");
            return false;
        }
        _remote = server;
        Log($"Remote access: listening on http://127.0.0.1:{server.Port}/");
        RemoteStateChanged?.Invoke();
        return true;
    }

    // Idempotent; signs every device out.
    internal void StopRemoteServer()
    {
        if (_remote is null)
            return;
        _remote.Stop();
        _remote = null;
        RemoteStateChanged?.Invoke();
    }

    private (int, object) RemoteAct(string method, string path, JsonElement body)
    {
        string Field(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        const string one = "/api/conversation/";
        return (method, path) switch
        {
            ("GET", "/api/conversations") => (200, new { conversations = RemoteList() }),
            ("GET", _) when path.StartsWith(one) => RemoteConversation(path[one.Length..]),
            ("POST", "/api/send") => RemoteSend(Field("id"), Field("text")),
            ("POST", "/api/stop") => Field("id") == RunningId && RunningId is not null
                ? Ok(() => StopWork_Click(this, new RoutedEventArgs())) : (409, new { error = "Nothing is running there." }),
            ("POST", "/api/answer") => _remotePrompt is { } p && (p.Conversation is null || p.Conversation == Field("id")) && p.Options.Any(o => o.Value == Field("choice"))
                ? Ok(() => p.Resolve(Field("choice"))) : (409, new { error = "That question is no longer open." }),
            _ => (404, new { error = "Not found" }),
        };

        static (int, object) Ok(Action act)
        {
            act();
            return (200, new { ok = true });
        }
    }

    private IEnumerable<object> RemoteList()
    {
        var (view, running) = (_store.View(), RunningId);
        return view.Groups.SelectMany(g => g.Items).Concat(view.Ungrouped).OrderByDescending(e => e.Updated)
            .Select(e => new { id = e.Id, title = e.Title, kind = Kind(e.Kind), updated = e.Updated, busy = e.Id == running }).ToList();
    }

    private static string Kind(string kind) => kind == "chat" ? "chat" : "code";

    private (int, object) RemoteConversation(string id)
    {
        if (_store.Get(id) is not { } saved)
            return (404, new { error = "No such conversation" });
        var busy = id == RunningId;
        var live = busy && _chatBusy; // a reply streams in it: the turn's own view (the store holds a placeholder reply)
        var messages = live ? _turn.History.Concat(_pendingUser is { } user ? TurnMessages(user) : Enumerable.Empty<LauncherLogic.ChatMessage>()).ToList() : saved.Messages;
        var queued = id == _chatConv?.Id ? _chatQueue.Count : id == _codeConv?.Id ? _codeQueue.Count : 0;
        var prompt = _remotePrompt is { } p && (p.Conversation is null || p.Conversation == id)
            ? new { title = p.Title, message = p.Message, choices = p.Options.Select(o => new { label = o.Label, value = o.Value }).ToList() } : null;
        var reply = live && _reply is { } r
            ? new { text = r.Final ?? r.AnswerText.ToString(), status = r.Status.Visibility == Visibility.Visible ? r.Status.Text : "" } : null;
        return (200, new
        {
            id, title = ConversationStore.TitleOf(saved), kind = Kind(saved.Kind), busy, queued, prompt, reply,
            messages = messages.Select(m => new { role = m.Role, text = m.Content }).ToList(),
        });
    }

    // The desktop's own send: the conversation opens, the text goes through its composer (the desktop's draft and attached
    // files are set aside meanwhile, so they never go with it), and SendOrQueue sends it or queues it behind a reply.
    private (int, object) RemoteSend(string id, string text)
    {
        if (text.Trim().Length == 0)
            return (400, new { error = "Empty message" });
        Conversation c;
        if (id.Length == 0)
        {
            NewChat_Click(this, new RoutedEventArgs());
            c = _chat;
        }
        else
        {
            if (_store.Get(id) is not { } saved)
                return (404, new { error = "No such conversation" });
            if (saved.Kind == "chat")
                OpenChatConversation(id);
            else
                OpenCodeConversation(id);
            c = saved.Kind == "chat" ? _chat : _code;
            if ((c.IsCode ? _codeConv : _chatConv)?.Id != id)
                return (409, new { error = "The app cannot open that conversation now (a reply or run is using the models)." });
        }
        var (draft, files) = (c.Input.Text, AttachmentsOf(c).ToList());
        AttachmentsOf(c).Clear();
        c.Input.Text = text;
        SendOrQueue(c);
        var sent = c.Input.Text != text; // sent or queued: the composer took it
        c.Input.Text = draft;
        if (AttachmentsOf(c).Count == 0)
            AttachmentsOf(c).AddRange(files);
        RefreshAttachments(c);
        return sent ? (200, new { id = (c.IsCode ? _codeConv : _chatConv)?.Id, queued = QueueOf(c).Count > 0 })
            : (409, new { error = "The app did not send it; see the desktop for why." });
    }

    // --ui-smoke: the server on a free port with a known password; without the cookie 401, login, the list, a message to a
    // new chat answered by a fake engine (a stand-in orchestrator.py) and read back, and a request popup answered remotely.
    private async Task<bool> RemoteSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: remote: {what}"); return false; }
        var (hash, port, root, key, runtime, choice, notify) = (_settings.RemotePasswordHash, _settings.RemotePort, _workflowRoot, ApiKeyBox.Password, _settings.Runtime, _codeChoice, _notifyHook);
        var engine = Path.Combine(AppSettings.DataRoot, "remote-engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(engine, "orchestrator.py"), "import json, time\n" +
            "def ev(**e): print('@@AO ' + json.dumps(e), flush=True)\n" +
            "ev(e='delta', role='planner', kind='text', text='Remote ')\ntime.sleep(1.5)\nev(e='chat_reply', role='planner', text='Remote reply')\n");
        File.WriteAllText(Path.Combine(engine, "config.json"), """{"roles": {}}""");
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
        string? cookie = null, setCookie = null;
        try
        {
            (_workflowRoot, ApiKeyBox.Password, _settings.Runtime, _codeChoice, _notifyHook) = (engine, "smoke-key", "unsloth", null, (_, _) => { });
            _settings.RemotePasswordHash = RemoteAuth.HashPassword("smoke password");
            _settings.RemotePort = Random.Shared.Next(20000, 60000);
            if (!StartRemoteServer() || RemotePort is not { } p)
                return Fail("no server");
            async Task<(int Status, JsonNode? Json)> Call(HttpMethod method, string path, object? body = null, bool signed = true)
            {
                using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{p}{path}");
                if (body is not null)
                    request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                if (signed && cookie is not null)
                    request.Headers.Add("Cookie", cookie); // a Secure cookie: no browser would send it over plain http
                using var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                if (response.Headers.TryGetValues("Set-Cookie", out var set))
                    setCookie = set.First();
                return ((int)response.StatusCode, text.StartsWith('{') ? JsonNode.Parse(text) : null);
            }
            if ((await Call(HttpMethod.Get, "/api/conversations")).Status != 401)
                return Fail("a request without the cookie was not refused");
            if ((await Call(HttpMethod.Post, "/api/login", new { password = "wrong" })).Status != 401 || setCookie is not null)
                return Fail("a wrong password signed in");
            if ((await Call(HttpMethod.Post, "/api/login", new { password = "smoke password" })).Status != 200 || setCookie is null || !setCookie.Contains("Secure"))
                return Fail($"login: {setCookie}");
            cookie = setCookie.Split(';')[0];
            using (var page = await http.GetAsync($"http://127.0.0.1:{p}/"))
                if (!(await page.Content.ReadAsStringAsync()).Contains("/api/login") || page.Headers.CacheControl?.NoStore != true ||
                    !page.Headers.TryGetValues("X-Frame-Options", out _))
                    return Fail("the page");
            if ((await Call(HttpMethod.Get, "/api/conversations")).Json?["conversations"] is not JsonArray)
                return Fail("list");
            var sent = await Call(HttpMethod.Post, "/api/send", new { text = "Hello from the phone" });
            if (sent.Json?["id"]?.GetValue<string>() is not { } id)
                return Fail($"send: {sent.Status} {sent.Json}");
            var first = (await Call(HttpMethod.Get, "/api/conversation/" + id)).Json;
            if (first?["busy"]?.GetValue<bool>() != true || first["reply"] is null || first["messages"]?.AsArray().LastOrDefault()?["text"]?.GetValue<string>() != "Hello from the phone")
                return Fail($"in progress: {first}");
            JsonNode? done = null;
            for (var i = 0; i < 100 && done?["busy"]?.GetValue<bool>() != false; i++)
            {
                await Task.Delay(200);
                done = (await Call(HttpMethod.Get, "/api/conversation/" + id)).Json;
            }
            if (done?["messages"]?.AsArray().LastOrDefault() is not { } last || last["role"]?.GetValue<string>() != "assistant" ||
                last["text"]?.GetValue<string>() != "Remote reply" || done["reply"] is not null || done["kind"]?.GetValue<string>() != "chat")
                return Fail($"reply: {done}");
            if ((await Call(HttpMethod.Get, "/api/conversations")).Json?["conversations"]?.AsArray().Any(c => c?["id"]?.GetValue<string>() == id) != true)
                return Fail("the new chat is not listed");
            // A request popup: shown in the conversation with its choices; a choice it does not offer is refused; the answer closes it.
            var asked = Dispatcher.InvokeAsync(() => Choose("Allow this command?", "The model wants to run: dir", ("Allow once", "allow"), ("Deny", "deny")));
            JsonNode? prompt = null;
            for (var i = 0; i < 50 && prompt is null; i++)
            {
                await Task.Delay(100);
                prompt = (await Call(HttpMethod.Get, "/api/conversation/" + id)).Json?["prompt"];
            }
            if (prompt?["choices"]?.AsArray().Select(c => c?["value"]?.GetValue<string>()).SequenceEqual(["allow", "deny"]) != true)
                return Fail($"prompt: {prompt}");
            if ((await Call(HttpMethod.Post, "/api/answer", new { id, choice = "maybe" })).Status != 409 ||
                (await Call(HttpMethod.Post, "/api/answer", new { id, choice = "deny" })).Status != 200)
                return Fail("answer");
            if (await Task.WhenAny(asked.Task, Task.Delay(5000)) != asked.Task || asked.Result != "deny" || _remotePrompt is not null)
                return Fail("the popup did not take the answer");
            if ((await Call(HttpMethod.Get, "/api/conversation/" + id, signed: false)).Status != 401)
                return Fail("a conversation was read without the cookie");
            return true;
        }
        finally
        {
            StopRemoteServer();
            (_settings.RemotePasswordHash, _settings.RemotePort, _workflowRoot, ApiKeyBox.Password, _settings.Runtime, _codeChoice, _notifyHook) =
                (hash, port, root, key, runtime, choice, notify);
        }
    }
}

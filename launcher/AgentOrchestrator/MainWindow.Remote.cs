using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

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

    // The open remote-answerable Choose popup (null: none), the conversation it is shown in, and a nonce an answer must
    // carry, so an answer meant for an earlier popup never resolves this one.
    private sealed record RemotePrompt(string Conversation, string Nonce, string Title, string Message, (string Label, string Value)[] Options, Window Dialog, Action<string> Resolve);
    private RemotePrompt? _remotePrompt;

    // The remote prompt for conversation `id`, while nothing is open on top of it.
    private RemotePrompt? PromptFor(string id) => _remotePrompt is { } p && p.Conversation == id && !Disabled(p.Dialog) ? p : null;

    // A popup the remote page cannot answer is open at the PC: a remote action now would run inside its dialog loop.
    private bool DesktopDialogOpen() => _remotePrompt is { } p ? Disabled(p.Dialog)
        : System.Windows.Interop.ComponentDispatcher.IsThreadModal || Disabled(this);

    // A modal on top disables its owner (WPF's ShowDialog every other window of the thread).
    private static bool Disabled(Window window) => new System.Windows.Interop.WindowInteropHelper(window).Handle is var h && h != 0 && !IsWindowEnabled(h);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

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
            ("POST", "/api/stop") => Field("id") != RunningId || RunningId is null ? (409, new { error = "Nothing is running there." })
                : DesktopDialogOpen() ? DialogOpen : Ok(() => StopWork_Click(this, new RoutedEventArgs())),
            ("POST", "/api/answer") => PromptFor(Field("id")) is { } p && p.Nonce == Field("nonce") && p.Options.Any(o => o.Value == Field("choice"))
                ? Ok(() => p.Resolve(Field("choice"))) : (409, new { error = "That request was already answered." }),
            _ => (404, new { error = "Not found" }),
        };

        static (int, object) Ok(Action act)
        {
            act();
            return (200, new { ok = true });
        }
    }

    private static readonly (int, object) DialogOpen = (409, new { error = "The app is showing a dialog on the PC." });

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
        var prompt = PromptFor(id) is { } p
            ? new { title = p.Title, message = p.Message, nonce = p.Nonce, choices = p.Options.Select(o => new { label = o.Label, value = o.Value }).ToList() } : null;
        var reply = live && _reply is { } r
            ? new { text = r.Final ?? r.AnswerText.ToString(), status = r.Status.Visibility == Visibility.Visible ? r.Status.Text : "" } : null;
        // The Code view's run output (a project run's thinking, tool calls and results) as its plain transcript, last 40k
        // characters: it lives only in that view, not in the saved messages.
        var text = id == _codeConv?.Id ? _transcript.ToString().Trim() : "";
        var activity = text.Length == 0 ? null : new
        {
            text = text.Length > 40_000 ? "…" + text[^40_000..] : text,
            running = _process is not null,
        };
        return (200, new
        {
            id, title = ConversationStore.TitleOf(saved), kind = Kind(saved.Kind), busy, queued, prompt, reply, activity,
            messages = messages.Select(m => new { role = m.Role, text = m.Content }).ToList(),
        });
    }

    // The desktop's own send: the conversation opens, the text goes through its composer (the desktop's draft and attached
    // files are set aside meanwhile, so they never go with it), and SendOrQueue sends it or queues it behind a reply.
    private (int, object) RemoteSend(string id, string text)
    {
        if (text.Trim().Length == 0)
            return (400, new { error = "Empty message" });
        if (text.TrimStart().StartsWith('/')) // a slash command changes settings (RunLocalCommand): those stay at the PC
            return (400, new { error = "Slash commands only work at the PC." });
        if (DesktopDialogOpen())
            return DialogOpen;
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
    // new chat answered by a fake engine (a stand-in orchestrator.py) and read back, a PC-only popup that stays hidden and
    // blocks send and stop, a slash command refused, a LAN client refused, and a request popup bound to one conversation
    // answered remotely with its nonce (not from another conversation, nor with a stale nonce).
    private async Task<bool> RemoteSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: remote: {what}"); return false; }
        static async Task<bool> Until(Func<bool> done)
        {
            for (var waited = 0; waited < 5000 && !done(); waited += 50)
                await Task.Delay(50);
            return done();
        }
        var (hash, port, root, project, choice, notify) = (_settings.RemotePasswordHash, _settings.RemotePort, _workflowRoot, _projectPath, _codeChoice, _notifyHook);
        var engine = Path.Combine(AppSettings.DataRoot, "remote-engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(engine, "orchestrator.py"), "import json, time\n" +
            "def ev(**e): print('@@AO ' + json.dumps(e), flush=True)\n" +
            "ev(e='delta', role='planner', kind='text', text='Remote ')\ntime.sleep(3)\nev(e='chat_reply', role='planner', text='Remote reply')\n");
        File.WriteAllText(Path.Combine(engine, "config.json"), """{"roles": {}}""");
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
        string? cookie = null, setCookie = null;
        try
        {
            // no project: with the root swapped, the real template would pass for a project's config and be written
            (_workflowRoot, _projectPath, _codeChoice, _notifyHook) = (engine, "", null, (_, _) => { });
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
            // A PC-only popup (Folder access) while that conversation replies: not shown nor answerable there, and send and
            // stop wait for it (they would run inside its dialog loop).
            var folder = Dispatcher.InvokeAsync(() => Choose("Folder access", "What may the worker do in C:\\elsewhere?", ("Read only", "read"), ("Full edit access", "write")));
            DialogShell? FolderDialog() => Application.Current.Windows.OfType<DialogShell>().FirstOrDefault(d => d.Title == "Folder access");
            if (!await Until(() => FolderDialog() is not null) || RunningId != id)
                return Fail("the PC-only popup did not open while the reply ran");
            for (var i = 0; i < 3; i++)
            {
                if ((await Call(HttpMethod.Get, "/api/conversation/" + id)).Json?["prompt"] is { } exposed)
                    return Fail($"non-remote dialog exposed: {exposed}");
                await Task.Delay(100);
            }
            if ((await Call(HttpMethod.Post, "/api/answer", new { id, choice = "write", nonce = "" })).Status != 409)
                return Fail("non-remote dialog answered");
            var blockedSend = await Call(HttpMethod.Post, "/api/send", new { id, text = "during the dialog" });
            var blockedStop = await Call(HttpMethod.Post, "/api/stop", new { id });
            if (blockedSend.Status != 409 || blockedStop.Status != 409 || blockedStop.Json?["error"]?.GetValue<string>()?.Contains("dialog") != true)
                return Fail($"send or stop under a PC dialog: {blockedSend.Status} {blockedStop.Status} {blockedStop.Json}");
            FolderDialog()!.Close();
            if (await Task.WhenAny(folder.Task, Task.Delay(5000)) != folder.Task || folder.Result is not null)
                return Fail("the PC-only popup did not close");
            async Task<JsonNode?> Done(string conversation)
            {
                JsonNode? state = null;
                for (var i = 0; i < 100 && state?["busy"]?.GetValue<bool>() != false; i++)
                {
                    await Task.Delay(200);
                    state = (await Call(HttpMethod.Get, "/api/conversation/" + conversation)).Json;
                }
                return state;
            }
            var done = await Done(id);
            if (done?["messages"]?.AsArray().LastOrDefault() is not { } last || last["role"]?.GetValue<string>() != "assistant" ||
                last["text"]?.GetValue<string>() != "Remote reply" || done["reply"] is not null || done["kind"]?.GetValue<string>() != "chat")
                return Fail($"reply: {done}");
            if ((await Call(HttpMethod.Get, "/api/conversations")).Json?["conversations"]?.AsArray().Any(c => c?["id"]?.GetValue<string>() == id) != true)
                return Fail("the new chat is not listed");
            // The Code view's run output goes with its conversation only (a project run's steps are not saved messages).
            if (_store.Create(Path.GetTempPath(), "code") is { } code)
            {
                var (shown, transcript) = (_codeConv, _transcript.ToString());
                (_codeConv, _) = (code, _transcript.Clear().Append("[PROJECT RUN] Working through PROJECT.md\nread_file STATE.md\n=> ok\n"));
                try
                {
                    var run = (await Call(HttpMethod.Get, "/api/conversation/" + code.Id)).Json?["activity"]?["text"]?.GetValue<string>();
                    if (run?.Contains("read_file STATE.md") != true || done["activity"] is not null ||
                        (await Call(HttpMethod.Get, "/api/conversation/" + id)).Json?["activity"] is not null)
                        return Fail($"run output: {run}");
                }
                finally
                {
                    (_codeConv, _) = (shown, _transcript.Clear().Append(transcript));
                    _store.Delete(code.Id);
                }
            }
            else
                return Fail("no code conversation for the run output check");
            if ((await Call(HttpMethod.Post, "/api/send", new { id, text = "  /ponytail ultra" })) is not { Status: 400, Json: { } slash } ||
                slash["error"]?.GetValue<string>()?.Contains("Slash") != true)
                return Fail("a slash command was not refused");
            // A LAN client naming the loopback host (http.sys routes on Host): refused. Our own LAN address is not loopback.
            if (System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()).FirstOrDefault(a =>
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a)) is { } lan)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{lan}:{p}/");
                request.Headers.Host = $"127.0.0.1:{p}";
                int status;
                try { status = (int)(await http.SendAsync(request)).StatusCode; }
                catch (HttpRequestException) { status = 0; } // refused before HTTP: not served either
                if (status == 200)
                    return Fail($"a LAN client ({lan}) was served: {status}");
            }
            var second = await Call(HttpMethod.Post, "/api/send", new { text = "A second chat" });
            if (second.Json?["id"]?.GetValue<string>() is not { } other || other == id || (await Done(other))?["busy"]?.GetValue<bool>() != false)
                return Fail($"second chat: {second.Status} {second.Json}");
            // A request popup bound to the first chat: shown there (not in the second) with its choices and nonce; a choice it
            // does not offer, an answer from the second chat and a stale nonce are refused; the answer closes it.
            var asked = Dispatcher.InvokeAsync(() => Choose(remote: id, "Allow this command?", "The model wants to run: dir", ("Allow once", "allow"), ("Deny", "deny")));
            JsonNode? prompt = null;
            for (var i = 0; i < 50 && prompt is null; i++)
            {
                await Task.Delay(100);
                prompt = (await Call(HttpMethod.Get, "/api/conversation/" + id)).Json?["prompt"];
            }
            if (prompt?["choices"]?.AsArray().Select(c => c?["value"]?.GetValue<string>()).SequenceEqual(["allow", "deny"]) != true ||
                prompt["nonce"]?.GetValue<string>() is not { Length: > 0 } nonce)
                return Fail($"prompt: {prompt}");
            if ((await Call(HttpMethod.Get, "/api/conversation/" + other)).Json?["prompt"] is { } elsewhere)
                return Fail($"a prompt for one conversation shows in another: {elsewhere}");
            if ((await Call(HttpMethod.Post, "/api/answer", new { id = other, choice = "deny", nonce })).Status != 409)
                return Fail("answered from another conversation");
            if ((await Call(HttpMethod.Post, "/api/answer", new { id, choice = "maybe", nonce })).Status != 409)
                return Fail("a choice it does not offer");
            if ((await Call(HttpMethod.Post, "/api/answer", new { id, choice = "deny", nonce = "0123456789ABCDEF0123456789ABCDEF" })).Status != 409)
                return Fail("a stale nonce answered the popup");
            if ((await Call(HttpMethod.Post, "/api/answer", new { id, choice = "deny", nonce })).Status != 200)
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
            (_settings.RemotePasswordHash, _settings.RemotePort, _workflowRoot, _projectPath, _codeChoice, _notifyHook) =
                (hash, port, root, project, choice, notify);
        }
    }

    // Settings > General > Remote access: on starts the server, then the tunnel on its port (asking to download cloudflared
    // first when it is missing); off stops the tunnel, then the server. At startup a missing cloudflared shows Failed instead.
    private RemoteTunnel _tunnel = null!; // InitRemote; --ui-smoke swaps in tunnels on a fake cloudflared
    private int? _remoteDownload; // cloudflared's download, in percent
    private string? _remoteFailure; // why the switch did not turn on, until it is clicked again
    private const string CloudflaredUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    private void InitRemote()
    {
        UseTunnel(new RemoteTunnel(RemoteTunnel.DefaultExe));
        RemoteStateChanged += RefreshRemote;
        if (_settings.RemoteAccess)
        {
            if (StartRemoteServer())
                _tunnel.Start(RemotePort!.Value);
            else
                _remoteFailure = "the server did not start (no password, or no free port)";
        }
        RefreshRemote();
    }

    private void UseTunnel(RemoteTunnel tunnel)
    {
        _tunnel = tunnel;
        tunnel.Changed += () => Dispatcher.InvokeAsync(RefreshRemote);
    }

    private async void RemoteToggle_Click(object sender, RoutedEventArgs e)
    {
        _remoteFailure = null;
        if (RemoteToggle.IsChecked != true)
        {
            _tunnel.Stop();
            StopRemoteServer();
            SaveRemoteAccess(false);
            return;
        }
        if (string.IsNullOrEmpty(_settings.RemotePasswordHash) || (!File.Exists(_tunnel.Exe) && !await DownloadCloudflared()))
        {
            SaveRemoteAccess(false); // the password line says why; a failed download, the state line
            return;
        }
        if (!StartRemoteServer())
        {
            _remoteFailure = $"no free port from {_settings.RemotePort}";
            SaveRemoteAccess(false);
            return;
        }
        _tunnel.Start(RemotePort!.Value);
        SaveRemoteAccess(true);
    }

    private void SaveRemoteAccess(bool on)
    {
        _settings.RemoteAccess = on;
        _settings.Save();
        RefreshRemote();
    }

    // Asks, then downloads to a temporary name beside it (progress in the state line) and moves it into place when complete.
    private async Task<bool> DownloadCloudflared()
    {
        if (Choose("Download cloudflared?", "Download cloudflared (about 60 MB) from github.com/cloudflare/cloudflared?", ("Download", "download")) != "download")
            return false;
        var temp = _tunnel.Exe + ".download";
        (_remoteDownload, RemoteToggle.IsEnabled) = (0, false);
        RefreshRemote();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.GetAsync(CloudflaredUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                for (int read; (read = await source.ReadAsync(buffer)) > 0;)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0 && (int)(done * 100 / total) != _remoteDownload)
                    {
                        _remoteDownload = (int)(done * 100 / total);
                        RefreshRemote();
                    }
                }
            }
            if (!RemoteTunnel.SignedBy(temp, "Cloudflare"))
            {
                File.Delete(temp);
                _remoteFailure = "the download is not signed by Cloudflare";
                Log($"Remote access: {_remoteFailure}");
                return false;
            }
            File.Move(temp, _tunnel.Exe, overwrite: true);
            Log($"Remote access: downloaded cloudflared to {_tunnel.Exe}");
            return true;
        }
        catch (Exception x) when (x is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            _remoteFailure = "could not download cloudflared: " + x.Message;
            Log($"Remote access: {_remoteFailure}");
            try { File.Delete(temp); } catch (Exception y) when (y is IOException or UnauthorizedAccessException) { }
            return false;
        }
        finally
        {
            (_remoteDownload, RemoteToggle.IsEnabled) = (null, true);
        }
    }

    // A new hash signs every device out (RemoteServer reads it on each request).
    private void RemotePassword_Click(object sender, RoutedEventArgs e)
    {
        if (RemotePasswordBox.Password.Length == 0)
        {
            Toast("Type a password first", "#F7C873");
            return;
        }
        _settings.RemotePasswordHash = RemoteAuth.HashPassword(RemotePasswordBox.Password);
        _settings.Save();
        RemotePasswordBox.Clear();
        Toast("Password set");
        RefreshRemote();
    }

    private void RemoteCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard(RemoteAddressBox.Text, "Address copied", "No address yet");

    private void RefreshRemote()
    {
        RemoteToggle.IsChecked = _settings.RemoteAccess || _remoteDownload is not null;
        RemotePasswordState.Text = string.IsNullOrEmpty(_settings.RemotePasswordHash)
            ? "No password yet: remote access cannot turn on without one." : "Password set. Setting a new one signs every device out.";
        RemoteStateText.Text = _remoteDownload is { } percent ? $"Downloading {percent}%" : _remoteFailure is { } failure ? "Failed: " + failure : _tunnel.State switch
        {
            RemoteTunnel.Status.Starting => "Starting…",
            RemoteTunnel.Status.Online => "Online",
            RemoteTunnel.Status.Failed => "Failed: " + _tunnel.Reason,
            _ => "Off",
        };
        var address = _tunnel.State == RemoteTunnel.Status.Online ? _tunnel.Url : null;
        RemoteOnlinePanel.Visibility = address is null ? Visibility.Collapsed : Visibility.Visible;
        if (address is not null && RemoteAddressBox.Text != address)
        {
            RemoteAddressBox.Text = address;
            RemoteQrImage.Source = QrCode(address);
        }
    }

    // ~8 px a module, black on the white quiet zone; the Image scales it to 180 px with NearestNeighbor, so it stays crisp.
    private static BitmapImage QrCode(string text)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(PngByteQRCodeHelper.GetQRCode(text, QRCodeGenerator.ECCLevel.M, 8));
        image.EndInit();
        image.Freeze();
        return image;
    }

    // --ui-smoke: no password refuses the switch; a declined download turns it back off; a fake cloudflared (a .cmd that
    // prints an address, then waits) goes Starting, Online with the address and its QR code, and off kills it by pid; one
    // that exits at once ends Failed with its last line. The clipboard is not checked: it is the user's, in use meanwhile.
    private async Task<bool> RemoteTunnelSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: remote tunnel: {what}"); return false; }
        static async Task<bool> Until(Func<bool> done)
        {
            for (var waited = 0; waited < 10000 && !done(); waited += 50)
                await Task.Delay(50);
            return done();
        }
        void Click(bool on)
        {
            RemoteToggle.IsChecked = on;
            RemoteToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        var (hash, port, access, choice, tunnel) = (_settings.RemotePasswordHash, _settings.RemotePort, _settings.RemoteAccess, _codeChoice, _tunnel);
        var fakes = Path.Combine(AppSettings.DataRoot, "fake-cloudflared");
        Directory.CreateDirectory(fakes);
        const string address = "https://smoke-test-tunnel.trycloudflare.com";
        var online = Path.Combine(fakes, "online.cmd");
        File.WriteAllText(online, "@echo off\r\necho INF Requesting new quick Tunnel on trycloudflare.com...\r\n" +
            "echo INF See https://developers.cloudflare.com/cloudflare-one/ 1>&2\r\n" +
            $"echo INF ^|  {address}  ^| 1>&2\r\nping -n 120 127.0.0.1 >nul\r\n");
        var exits = Path.Combine(fakes, "exits.cmd");
        File.WriteAllText(exits, "@echo off\r\necho ERR failed to reach the edge 1>&2\r\nexit /b 3\r\n");
        try
        {
            _settings.RemotePort = Random.Shared.Next(20000, 60000);
            _settings.RemotePasswordHash = null;
            Click(true);
            if (RemoteToggle.IsChecked == true || _settings.RemoteAccess || RemotePort is not null || !RemotePasswordState.Text.Contains("cannot turn on"))
                return Fail("it turned on without a password");

            _settings.RemotePasswordHash = RemoteAuth.HashPassword("smoke password");
            UseTunnel(new RemoteTunnel(Path.Combine(fakes, "missing.exe")));
            string? asked = null;
            _codeChoice = title => { asked = title; return null; };
            Click(true);
            if (asked != "Download cloudflared?" || RemoteToggle.IsChecked == true || _settings.RemoteAccess || RemotePort is not null)
                return Fail($"declining the download: asked {asked}, on {RemoteToggle.IsChecked}");

            var fake = new RemoteTunnel(online);
            var states = new List<RemoteTunnel.Status>();
            fake.Changed += () => { lock (states) states.Add(fake.State); };
            UseTunnel(fake);
            Click(true);
            if (!await Until(() => RemoteAddressBox.Text == address && RemoteStateText.Text == "Online"))
                return Fail($"not online: {fake.State} {fake.Reason} / {RemoteStateText.Text}");
            lock (states)
                if (states.FirstOrDefault() != RemoteTunnel.Status.Starting || fake.Url != address)
                    return Fail($"states {string.Join(",", states)}, url {fake.Url}");
            if (RemoteQrImage.Source is not BitmapSource { PixelWidth: > 100 } || RemoteOnlinePanel.Visibility != Visibility.Visible ||
                !_settings.RemoteAccess || RemotePort is null || fake.Pid is not { } pid)
                return Fail("the address, its QR code or the server");
            Click(false);
            if (fake.State != RemoteTunnel.Status.Off || RemotePort is not null || _settings.RemoteAccess ||
                !await Until(() => { try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.HasExited; } catch (ArgumentException) { return true; } }) ||
                !await Until(() => RemoteStateText.Text == "Off" && RemoteOnlinePanel.Visibility != Visibility.Visible))
                return Fail($"off: {fake.State}, pid {pid}, {RemoteStateText.Text}");

            var failing = new RemoteTunnel(exits);
            UseTunnel(failing);
            Click(true);
            if (!await Until(() => failing.State == RemoteTunnel.Status.Failed && RemoteStateText.Text.StartsWith("Failed: ERR failed to reach the edge")))
                return Fail($"an exiting cloudflared: {failing.State} {failing.Reason} / {RemoteStateText.Text}");
            Click(false);
            if (failing.State != RemoteTunnel.Status.Off)
                return Fail("an exiting cloudflared did not stop");
            return true;
        }
        finally
        {
            _tunnel.Stop();
            StopRemoteServer();
            (_settings.RemotePasswordHash, _settings.RemotePort, _settings.RemoteAccess, _codeChoice, _tunnel) = (hash, port, access, choice, tunnel);
            RefreshRemote();
        }
    }
}

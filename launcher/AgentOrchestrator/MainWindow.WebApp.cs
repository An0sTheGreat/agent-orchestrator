using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgentOrchestrator;

// The phone app over Tailscale: WebServer on 127.0.0.1, published at https://<this PC's MagicDNS name>/ by `tailscale serve`.
// Settings > General's switch keeps WebAppOn, the user's choice: it stays on while Tailscale is not ready (the state line says
// why) and the app starts once it is, at the next launch or toggle. It starts at launch when on; Quit (OnClosing) stops it and
// turns Serve off. The owner's login and the tailnet's suffix come from `tailscale status`, read at start and every 5 minutes.
public partial class MainWindow
{
    private WebServer? _webApp;
    private Tailscale.State? _webTailnet; // the status the running server answers by
    private string? _webFailure; // why it is not running although on, until the switch is clicked again
    private bool _webStarting;
    private readonly DispatcherTimer _webTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    // --webapp-smoke swaps both, so tests never touch Tailscale: the status read, and Serve (a port: publish it; null: off).
    internal Func<Task<Tailscale.State?>> _webStatus = Tailscale.StatusAsync;
    internal Func<int?, CancellationToken, Task<(bool Ok, string Output)>> _webServe = ServeOnTailnet;
    // /api/pcs asks each of the owner's other PCs for its /api/hello (null: no answer); --webapp-smoke swaps it, never the network.
    internal Func<string, CancellationToken, Task<JsonNode?>> _webProbe = ProbeHello;
    private static readonly HttpClient WebProbeHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static async Task<JsonNode?> ProbeHello(string dnsName, CancellationToken ct)
    {
        try { return JsonNode.Parse(await WebProbeHttp.GetStringAsync($"https://{dnsName}/api/hello", ct)) as JsonObject; }
        catch (Exception x) when (x is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }
    private string? _webFailureUrl; // the link in tailscale's own message, shown as "Enable Serve for your tailnet"
    // A failed start is retried while the switch is on: every _webRetryEvery for _webRetryFastFor, then every _webRetrySlow
    // (tailscaled may still be starting at logon). --webapp-smoke shortens the first.
    internal TimeSpan _webRetryEvery = TimeSpan.FromSeconds(30), _webRetryFastFor = TimeSpan.FromMinutes(5), _webRetrySlow = TimeSpan.FromMinutes(5);
    private readonly DispatcherTimer _webRetry = new();
    private DateTime _webRetrySince;

    // The live events (GET /api/conversation/{id}/events): the engine's events from DrainLines, `saved` from FinishChatTurn, and
    // `state` whenever busy, queued or the prompt of a watched conversation changes (polled: one place sees all those changes).
    private readonly WebEvents _webEvents = new();
    private readonly Dictionary<string, string> _webStates = []; // the last state event per watched conversation
    private readonly DispatcherTimer _webStatePoll = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private void PublishWebEvent(string? conversationId, string json)
    {
        if (_webApp is not null && conversationId is not null)
            _webEvents.Publish(conversationId, json);
    }

    private static string WebState(bool busy, int queued, object? prompt) => JsonSerializer.Serialize(new { e = "state", busy, queued, prompt });

    private void PollWebStates()
    {
        var watched = _webEvents.Conversations();
        foreach (var gone in _webStates.Keys.Except(watched).ToList())
            _webStates.Remove(gone);
        foreach (var id in watched)
            if (ConversationNow(id) is (_, var busy, _, var queued, var prompt, _) && WebState(busy, queued, prompt) is var state && _webStates.GetValueOrDefault(id) != state)
                PublishWebEvent(id, _webStates[id] = state);
    }

    // On the UI thread, so the hello's reply and the deltas after it never overlap nor leave a gap (DrainLines publishes there too).
    private (int, object) WebEventsOpen(string id)
    {
        if (ConversationNow(id) is not (_, var busy, _, var queued, var prompt, var reply))
            return (404, new { error = "No such conversation" });
        _webStates.TryAdd(id, WebState(busy, queued, prompt));
        var life = new CancellationTokenSource();
        var picture = _webPicture is { } p && p.Id == id ? new { width = p.Width, height = p.Height, text = p.Text, progress = p.Progress, frame = p.Frame } : null;
        return (200, new WebServer.EventStream(JsonSerializer.Serialize(new { e = "hello", busy, queued, prompt, reply, picture }), _webEvents.Subscribe(id, life.Token), life));
    }

    private static JsonElement WebBody(byte[] body)
    {
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    internal string? WebAppAddress => _webApp is not null && _webTailnet is { } t ? $"https://{t.DnsName}/" : null; // null while off

    private static async Task<(bool Ok, string Output)> ServeOnTailnet(int? port, CancellationToken ct)
    {
        if (port is { } p)
            return await Tailscale.ServeAsync(p, ct);
        await Tailscale.ServeOffAsync(ct);
        return (true, "");
    }

    private void ArmWebRetry()
    {
        if (_webRetry.IsEnabled)
            return;
        (_webRetrySince, _webRetry.Interval) = (DateTime.UtcNow, _webRetryEvery);
        _webRetry.Start();
    }

    private async void WebRetry_Tick(object? sender, EventArgs e)
    {
        if (!_settings.WebAppOn || _webApp is not null)
        {
            _webRetry.Stop();
            return;
        }
        if (_webStarting)
            return;
        if (DateTime.UtcNow - _webRetrySince > _webRetryFastFor)
            _webRetry.Interval = _webRetrySlow;
        await StartWebAppAsync();
    }

    private void InitWebApp()
    {
        _webRetry.Tick += WebRetry_Tick;
        _webStatePoll.Tick += (_, _) => PollWebStates();
        _webTimer.Tick += async (_, _) =>
        {
            if (await _webStatus() is { } status && _webApp is not null)
                _webTailnet = status; // a status that failed to read keeps the last one
            RefreshWebApp();
        };
        RefreshWebApp();
    }

    // --webapp-dev <port> (dev only, App.xaml.cs): the phone app on http://127.0.0.1:<port>/ for a browser on this PC, every loopback
    // request taken as the owner's (WebServer.DevLogin), Tailscale faked (dev@example.com, this PC "home" and an online "laptop" on
    // tail0000.ts.net; never the network, never `tailscale serve`, no setting saved), against the temp data folder App set, seeded
    // here: a project with two chats (one with a markdown reply and a picture), two recents and one archived. False when the port is taken.
    internal bool StartWebAppDev(int port)
    {
        _webTailnet = Tailscale.ParseStatus("""
            {"BackendState":"Running","Self":{"UserID":1,"HostName":"home","DNSName":"home.tail0000.ts.net."},"CertDomains":["home.tail0000.ts.net"],
             "CurrentTailnet":{"MagicDNSSuffix":"tail0000.ts.net"},"User":{"1":{"LoginName":"dev@example.com"}},
             "Peer":{"k1":{"UserID":1,"HostName":"laptop","OS":"windows","DNSName":"laptop.tail0000.ts.net.","Online":true}}}
            """);
        _webProbe = (dns, _) => Task.FromResult(dns == "laptop.tail0000.ts.net" ? JsonNode.Parse("""{"pc":"LAPTOP"}""") : null);
        _webServe = (_, _) => Task.FromResult((true, "")); // a close never turns the real Serve off

        var images = Path.Combine(AppSettings.DataRoot, "images");
        Directory.CreateDirectory(images);
        var picture = Path.Combine(images, "sunset.png");
        var pixels = new byte[160 * 120 * 4];
        for (var i = 0; i < pixels.Length; i += 4) // a vertical gradient, blue to orange (Bgr32)
            (pixels[i], pixels[i + 1], pixels[i + 2]) = ((byte)(200 - i / 4 / 160), (byte)(80 + i / 4 / 160 / 2), (byte)(60 + i / 4 / 160 * 3 / 2));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, 160 * 4)));
        using (var file = File.Create(picture))
            encoder.Save(file);
        var now = DateTimeOffset.Now;
        string Seed(string folder, string kind, params LauncherLogic.ChatMessage[] messages)
        {
            var c = _store.Create(folder, kind)!;
            c.Messages.AddRange(messages);
            _store.Save(c);
            return c.Id;
        }
        var project = _store.CreateGroup("weather-dashboard")!;
        _store.MoveToGroup(Seed("", "chat", new("user", "Plan a weather dashboard", now.AddMinutes(-9)),
            new("assistant", "## The plan\n\nTwo parts, **today** and a `7-day` chart:\n\n1. Fetch the forecast\n2. Draw the chart\n\n| Part | Agent |\n| --- | --- |\n| Fetch | Implementer |\n| Review | Reviewer |\n\n> Keep it dark by default.\n\nA first sketch:",
                now.AddMinutes(-8), [picture], "24 tok/s")), project);
        _store.MoveToGroup(Seed(@"C:\dev\weather-dashboard", "code", new("user", "Add the 7-day chart", now.AddMinutes(-6)), new("assistant", "Done: chart.js draws 7 points.", now.AddMinutes(-5))), project);
        Seed("", "chat", new("user", "Reviewer keeps failing chart.js", now.AddHours(-20)), new("assistant", "It expects 7 points; the data has 6.", now.AddHours(-20)));
        Seed("", "chat", new("user", "Theme tokens for Graphite", now.AddDays(-2)), new("assistant", "`#1E1E1E` window, `#303030` cards.", now.AddDays(-2)));
        _store.Archive(Seed("", "chat", new("user", "Docs: remote access page", now.AddDays(-8)), new("assistant", "Drafted.", now.AddDays(-8))), true);

        var server = new WebServer(() => _webTailnet?.Login, () => _webTailnet?.OwnerOrigins, WebApi) { DevLogin = _webTailnet?.Login };
        if (!server.Start(port) || server.Port != port)
        {
            server.Stop();
            Console.Error.WriteLine($"webapp-dev: port {port} is taken");
            return false;
        }
        _webApp = server;
        _webStatePoll.Start();
        Console.Error.WriteLine($"webapp-dev: http://127.0.0.1:{port}/ (data {AppSettings.DataRoot})");
        return true;
    }

    // Only the app's normal start (App.xaml.cs sets LaunchStartsWebApp there) turns the phone app on at launch: the test and tool
    // modes use the real profile and must never run `tailscale serve`.
    internal bool LaunchStartsWebApp;

    internal void StartWebAppAtLaunch()
    {
        if (LaunchStartsWebApp && _settings.WebAppOn)
            _ = StartWebAppAsync();
    }

    private Task<bool>? _webStart; // the start in flight, which Stop waits for

    // Idempotent. False when Tailscale is not ready, no port is free or Serve refused; _webFailure says which.
    internal Task<bool> StartWebAppAsync() => _webStarting ? _webStart! : _webStart = StartWebAppCore();

    private async Task<bool> StartWebAppCore()
    {
        if (_webApp is not null || _webStarting)
            return _webApp is not null;
        (_webStarting, _webFailure, _webFailureUrl) = (true, null, null);
        var retry = false;
        RefreshWebApp();
        try
        {
            var status = await _webStatus();
            if (Tailscale.WebAppProblem(status, Tailscale.Exe is not null) is { } why)
            {
                _webFailure = why;
                retry = true; // not ready yet (tailscaled may still be starting); a refusal by tailscale serve is not retried
                return false;
            }
            _webTailnet = status;
            var server = new WebServer(() => _webTailnet?.Login, () => _webTailnet?.OwnerOrigins, WebApi);
            if (!server.Start(_settings.WebAppPort))
            {
                _webFailure = $"no free port from {_settings.WebAppPort}";
                return false;
            }
            var (served, output) = await _webServe(server.Port, CancellationToken.None);
            if (!served)
            {
                server.Stop();
                _webFailure = Tailscale.ServeFailure(output) is { Length: > 0 } line ? $"tailscale serve: {line}" : "tailscale serve did not publish it";
                _webFailureUrl = Tailscale.ServeEnableUrl(output);
                return false;
            }
            _webApp = server;
            _webRetry.Stop();
            _webTimer.Start();
            _webStatePoll.Start();
            Log($"Phone app: {WebAppAddress} serves http://127.0.0.1:{server.Port}/");
            return true;
        }
        finally
        {
            if (_webFailure is not null)
            {
                Log($"Phone app: {_webFailure}");
                if (retry && _settings.WebAppOn)
                    ArmWebRetry();
                else
                    _webRetry.Stop(); // a refusal after a not-ready failure ends the retries too
            }
            _webStarting = false;
            RefreshWebApp();
        }
    }

    // Idempotent. Serve goes off on a pool thread, so Quit can wait for it on the UI thread.
    // A start still in flight finishes first, so Serve's route is never left behind by a Quit mid-start.
    internal async Task StopWebAppAsync()
    {
        _webRetry.Stop();
        if (_webStarting && _webStart is { } start)
            await start;
        _webRetry.Stop(); // a failed start re-arms it
        _webTimer.Stop();
        _webStatePoll.Stop();
        _webStates.Clear();
        if (_webApp is null)
            return;
        _webApp.Stop(); // ends the open event streams, which unsubscribes them
        _webApp = null;
        _webStarting = true; // keeps the switch disabled until Serve is off, so an on can not overlap it
        RefreshWebApp();
        var serve = _webServe;
        try { await Task.Run(() => serve(null, CancellationToken.None)); }
        finally { _webStarting = false; RefreshWebApp(); }
    }

    // Quit: waits for Stop on the UI thread for at most `cap`, still pumping messages (a start in flight resumes on it).
    private void WaitForWebAppStop(TimeSpan cap)
    {
        var stop = StopWebAppAsync();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(cap, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher);
        _ = stop.ContinueWith(_ => Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        if (!stop.IsCompleted)
            Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static readonly (int, object) WebNotFound = (404, new { error = "Not found" });

    // The phone app's API (WebServer has checked the caller). What reads the window's state runs on the UI thread.
    private async Task<(int Status, object Body)> WebApi(string method, string path, IReadOnlyDictionary<string, string> query, byte[] body, string contentType, string? fileName)
    {
        const string one = "/api/conversation/";
        return (method, path) switch
        {
            ("GET", "/api/hello") => (200, WebHello()),
            ("GET", "/api/pcs") => (200, await WebPcs()),
            ("GET", "/api/conversations") => await Dispatcher.InvokeAsync(WebConversations),
            ("GET", one + "events") => WebNotFound, // no id: the events route below would slice past it
            ("GET", _) when path.StartsWith(one) && path.EndsWith("/events") => await Dispatcher.InvokeAsync(() => WebEventsOpen(path[one.Length..^"/events".Length])),
            ("GET", one + "picture") => WebNotFound, // no id, as with events
            ("GET", _) when path.StartsWith(one) && path.EndsWith("/picture") => await Dispatcher.InvokeAsync(() => WebPicturePreview(path[one.Length..^"/picture".Length])),
            ("POST", "/api/answer") => await Dispatcher.InvokeAsync(() => RemoteAct(method, path, WebBody(body))),
            ("POST", "/api/stop") => WebDialog(await Dispatcher.InvokeAsync(() => WebPromptOpen() ? DialogOpen : RemoteAct(method, path, WebBody(body)))),
            ("POST", "/api/send") => WebDialog(await Dispatcher.InvokeAsync(() => WebPromptOpen() ? DialogOpen : WebSend(WebBody(body)))),
            ("POST", "/api/upload") => WebUpload(fileName, body),
            ("GET", "/api/composer") => await await Dispatcher.InvokeAsync(async () => WebComposer(await WebSharedImageModels())),
            ("POST", "/api/composer") => await await Dispatcher.InvokeAsync(async () => WebComposerSet(WebBody(body), await WebSharedImageModels())),
            ("GET", _) when path.StartsWith(one) => await Dispatcher.InvokeAsync(() => WebConversation(path[one.Length..])),
            ("GET", "/api/file") => WebFile(query.GetValueOrDefault("path")),
            _ => WebNotFound,
        };
    }

    // Send and stop wait for any PC popup, one tied to a conversation too (the sandbox's command approval, which the phone may answer):
    // either would otherwise run inside its dialog loop. RemoteAct (remote.html) keeps DesktopDialogOpen alone.
    private bool WebPromptOpen() => _remotePrompt is not null || DesktopDialogOpen();

    // Send and stop while a PC dialog is open: the phone app's own error word, which its page explains.
    private static (int, object) WebDialog((int, object) result) => ReferenceEquals(result.Item2, DialogOpen.Item2) ? (409, new { error = "DialogOpen" }) : result;

    private static string WebField(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string WebPasted => Path.Combine(AppSettings.DataRoot, "pasted");

    // A photo or file from the phone, saved into pasted\ as <unique id>-<its name, path and unsafe characters dropped>; one
    // the composer would refuse is deleted again and refused with Attach's reason.
    private static (int, object) WebUpload(string? fileName, byte[] body)
    {
        var name = string.Concat(Path.GetFileName(Uri.UnescapeDataString(fileName ?? "")).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim(' ', '.');
        name = name.Length == 0 ? "upload" : name[Math.Max(0, name.Length - 100)..]; // the end keeps the extension
        Directory.CreateDirectory(WebPasted);
        var path = Path.Combine(WebPasted, $"{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, body);
        if (AttachRefusal([], path) is not { } why)
            return (200, new { path });
        File.Delete(path);
        return (400, new { error = why });
    }

    // RemoteSend with the phone's uploads: an attachment that is not a file in pasted\ is refused, never read off the PC.
    private (int, object) WebSend(JsonElement body)
    {
        var folder = Path.GetFullPath(WebPasted) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("attachments", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                var path = item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
                if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) is var full && (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)))
                    return (400, new { error = "Only files sent from the phone can be attached" });
                files.Add(full);
            }
        return RemoteSend(WebField(body, "id"), WebField(body, "text"), files);
    }

    // The Chat composer's options as the desktop shows them: the toggles, the effort sent (and the levels the target takes),
    // its target worker and the ones it may pick, the skills, and the chat's image model, size and steps: the Image Gen chip's model
    // choices and pick (model: a choice's id; name: what the chip shows), its aspect ratios (Custom first) and pick, its width and
    // height boxes (0 = empty: the ratio's recommended), its lock, and the size the request carries ("" while no model is ready).
    private (int, object) WebComposer(List<ImageShare.Model>? shared)
    {
        var target = ChatTarget();
        var size = ChatImageReady && ImageGen.ChatRequest(_settings.ChatImage, _settings.ImageKeepBoth) is { } r ? $"{r.Width}×{r.Height}" : "";
        var c = _settings.ChatImage;
        ScanImageFolder();
        return (200, new
        {
            search = _settings.UseSearch, tools = _settings.UseTools, vision = _settings.UseVision,
            effort = EffortFor(target), efforts = EffortLevels(target), worker = target?.Role ?? "",
            workers = ListedWorkers(_chat).Select(k => new { role = k.Role, name = ChatName(k, k.Role) }).ToList(),
            skills = AllSkills().Select(s => new { name = Path.GetFileName(s.Folder), on = HasSkill(s), description = s.Description }).ToList(),
            imageGen = new
            {
                model = ChatImageKey(), name = ImageGen.ChatModel(c) is var (_, model) ? model : "",
                models = ChatImageChoices(shared).Select(m => new { id = m.Key, name = m.Name, note = m.Note, downloaded = m.Downloaded, primary = m.Primary }).ToList(),
                aspect = c.Aspect, aspects = ImageGen.Presets.Select(p => new { name = p.Name, ratio = p.Ratio.Length > 0 ? p.Ratio : "Custom" }).ToList(),
                width = c.CustomWidth, height = c.CustomHeight, unlocked = c.SizeUnlocked, size, steps = _settings.ChatImageSteps,
            },
        });
    }

    // The primary PC's image models for the composer while connected: the list fetched last for this host, else fetched (5 s); null when none.
    private async Task<List<ImageShare.Model>?> WebSharedImageModels()
    {
        var host = _settings.ShareHost;
        if (host.Length == 0)
            return null;
        if (_imageShared is var (fetched, models) && fetched == host)
            return models;
        try { return await FetchSharedImageModels(host, 5); }
        catch (Exception x) when (x is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException) { return null; }
    }

    // The chip's size controls' rules without the controls (ImageSizeControls: Commit, AspectChanged, Read): the boxes round to the
    // model's multiple (unlocked: as typed, 64-4096), with a ratio and locked the height follows the width (else the width the height),
    // a pair off the ratio is Custom, and Width/Height are the boxes over the ratio's recommended size (Custom: the ratio before, else Square).
    private void WriteChatImageSize(string aspect, bool unlocked, int typedWidth, int typedHeight, bool heightTyped)
    {
        var c = _settings.ChatImage;
        var (multiple, step) = (ChatImageMultiple(), unlocked ? 1 : ChatImageMultiple());
        int cw = typedWidth > 0 ? ImageGen.Pick(typedWidth, 0, step) : 0, ch = typedHeight > 0 ? ImageGen.Pick(typedHeight, 0, step) : 0;
        var custom = aspect == "Custom";
        if (!custom && !unlocked)
        {
            if (heightTyped && ch > 0) // the field that was edited leads (the chip's Commit(width: false)): the width follows the height
                cw = ImageGen.Linked(ch, aspect, false, multiple);
            else if (cw > 0)
                ch = ImageGen.Linked(cw, aspect, true, multiple);
            else if (ch > 0)
                cw = ImageGen.Linked(ch, aspect, false, multiple);
            custom = cw > 0 && !ImageGen.Matches(cw, ch, aspect, multiple);
        }
        var ratio = custom ? (IsChatImageRatio(c.Aspect) ? c.Aspect : "1:1") : aspect;
        var (rw, rh) = ImageGen.Size(ratio, ImageGen.RecommendedLongSide, multiple);
        int w = ImageGen.Pick(cw, rw, step), h = ImageGen.Pick(ch, rh, step);
        (c.Aspect, c.SizeUnlocked, c.Width, c.Height, c.CustomWidth, c.CustomHeight) = (custom ? "Custom" : aspect, unlocked, w, h, custom || cw > 0 ? w : 0, custom || ch > 0 ? h : 0);
    }

    // An aspect the chip's list offers: a preset's ratio, or its flip (the swap button)
    private static bool IsChatImageRatio(string aspect) => ImageGen.Presets.Any(p => p.Ratio.Length > 0 && (p.Ratio == aspect || ImageGen.Flip(p.Ratio) == aspect));

    // Writes what the body names, as the desktop's own controls do (an unknown worker, effort, skill, image model or aspect changes
    // nothing: 400), saves, and refreshes the desktop's chips and toggles. imageGen.model: one of the chip's choices (the pick itself
    // changes nothing); aspect ("Custom", a ratio or its flip), width, height (0 = empty) and unlocked: the chip's size, missing ones as they are.
    private (int, object) WebComposerSet(JsonElement body, List<ImageShare.Model>? shared)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return (400, new { error = "Expected a JSON object" });
        bool? Flag(string name) => body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        var worker = body.TryGetProperty("worker", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
        var effort = body.TryGetProperty("effort", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var skills = AllSkills();
        var toggles = body.TryGetProperty("skills", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(t => (Skill: skills.FirstOrDefault(k => Path.GetFileName(k.Folder).Equals(WebField(t, "name"), StringComparison.OrdinalIgnoreCase)),
                On: t.ValueKind == JsonValueKind.Object && t.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True)).ToList() : [];
        if (worker is not null && ListedWorkers(_chat).All(k => k.Role != worker) || effort is not null && Efforts.All(x => x.Value != effort) || toggles.Any(t => t.Skill is null))
            return (400, new { error = "No such worker, effort or skill" });
        var image = body.TryGetProperty("imageGen", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        int? Number(string name) => image.ValueKind == JsonValueKind.Object && image.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        var (modelKey, aspect) = (image.ValueKind == JsonValueKind.Object ? WebField(image, "model") : "", image.ValueKind == JsonValueKind.Object ? WebField(image, "aspect") : "");
        ChatImageChoice? pick = null;
        if (modelKey.Length > 0 && !modelKey.Equals(ChatImageKey(), StringComparison.OrdinalIgnoreCase))
        {
            ScanImageFolder();
            if ((pick = ChatImageChoices(shared).FirstOrDefault(m => m.Key.Equals(modelKey, StringComparison.OrdinalIgnoreCase))) is null)
                return (400, new { error = "No such image model" });
        }
        if (aspect.Length > 0 && aspect != "Custom" && !IsChatImageRatio(aspect))
            return (400, new { error = "No such aspect ratio" });
        (_settings.UseSearch, _settings.UseTools, _settings.UseVision) = (Flag("search") ?? _settings.UseSearch, Flag("tools") ?? _settings.UseTools, Flag("vision") ?? _settings.UseVision);
        (_settings.ChatWorker, _settings.ChatEffort) = (worker ?? _settings.ChatWorker, effort ?? _settings.ChatEffort);
        foreach (var (skill, on) in toggles)
        {
            var (list, key) = (skill!.BuiltIn ? _settings.SkillsOff : _settings.Skills, Path.GetFileName(skill.Folder));
            list.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (on != skill.BuiltIn) // as SetSkill: a user skill on, or a bundled one off
                list.Add(key);
        }
        if (Number("steps") is { } steps)
            _settings.ChatImageSteps = steps > 0 ? Math.Clamp(steps, 1, 100) : 0; // as the chip's box: 0 is the model's own
        if (pick is not null)
            PickChatImage(pick); // before the size: a typed size rounds to the new model's multiple
        var unlocked = image.ValueKind == JsonValueKind.Object && image.TryGetProperty("unlocked", out var u) && u.ValueKind is JsonValueKind.True or JsonValueKind.False ? u.GetBoolean() : (bool?)null;
        var c = _settings.ChatImage;
        if (aspect.Length > 0 || unlocked is not null || Number("width") is not null || Number("height") is not null)
        {
            // the box the body edited leads: a height alone, or a pair where only the height differs from what is stored (a phone echoing both boxes)
            var (bodyWidth, bodyHeight) = (Number("width"), Number("height"));
            var heightTyped = bodyHeight is not null && (bodyWidth is null || bodyWidth == c.CustomWidth && bodyHeight != c.CustomHeight);
            WriteChatImageSize(aspect.Length > 0 ? aspect : c.Aspect == "Custom" || IsChatImageRatio(c.Aspect) ? c.Aspect : "1:1", unlocked ?? c.SizeUnlocked,
                bodyWidth ?? c.CustomWidth, bodyHeight ?? c.CustomHeight, heightTyped);
        }
        _settings.Save();
        RefreshExtras();
        RefreshChatTarget();
        return WebComposer(shared);
    }

    private object WebHello() => new
    {
        pc = Environment.MachineName, dnsName = _webTailnet?.DnsName ?? "", user = _webTailnet?.Login ?? "",
        version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "", shell = WebServer.Shell,
    };

    // This PC first, then the owner's other PCs on the tailnet, each asked for its /api/hello at once: one that answers is online.
    // ponytail: the peers are those of the last status read (at start and every 5 minutes); a PC added since shows up at the next.
    private async Task<object> WebPcs()
    {
        var (tailnet, probe) = (_webTailnet, _webProbe);
        var peers = tailnet?.MyWindowsPcs.Where(p => p.DnsName.Length > 0).ToList() ?? [];
        var answers = await Task.WhenAll(peers.Select(p => probe(p.DnsName, CancellationToken.None)));
        return peers.Select((p, i) => new { pc = (answers[i] as JsonObject)?["pc"]?.ToString() ?? p.Name, dnsName = p.DnsName, online = answers[i] is JsonObject, self = false })
            .Prepend(new { pc = Environment.MachineName, dnsName = tailnet?.DnsName ?? "", online = true, self = true }).ToList();
    }

    private (int, object) WebConversations()
    {
        var (view, running) = (_store.View(), RunningId);
        var item = (ConversationStore.Entry e) => new { id = e.Id, title = e.Title, kind = Kind(e.Kind), updated = e.Updated, busy = e.Id == running, folder = e.Folder, worker = WebWorker(_store.Get(e.Id)) };
        return (200, new
        {
            projects = view.Groups.Select(g => new { id = g.Id, name = g.Name, items = g.Items.Select(item).ToList() }).ToList(),
            recents = view.Ungrouped.Select(item).ToList(),
            archived = view.Archived.Select(item).ToList(),
        });
    }

    // The worker a conversation sends to, named as the desktop names it ("Lead" for the planner); "" when its card is unknown.
    private string WebWorker(ConversationStore.Conversation? c) => c is null ? "" : ChatName(CardFor(c.Worker), c.Worker);

    // A reply's worker: the saved stats line starts with it ("Lead · 81.1 tok/s · ..."); null when the line has none ("12 tok/s", "5 generated · 7 prompt").
    internal static string? StatsWorker(string? stats) =>
        stats?.IndexOf(" · ", StringComparison.Ordinal) is > 0 and var i && !char.IsDigit(stats[0]) ? stats[..i] : null;

    private (int, object) WebConversation(string id)
    {
        if (ConversationNow(id) is not (var saved, var busy, var messages, var queued, var prompt, var reply))
            return (404, new { error = "No such conversation" });
        // the open view's measure while it is shown (a turn updates it before saving), else the saved one
        var context = (id == _chatConv?.Id ? _chatContext : id == _codeConv?.Id ? _codeContext : null) ?? saved.Context;
        return (200, new
        {
            id, title = ConversationStore.TitleOf(saved), kind = Kind(saved.Kind), worker = WebWorker(saved), busy, queued, prompt, reply,
            context = context is { } c ? new { nCtx = c.NCtx, used = c.Used, fraction = c.Fraction, parts = c.Parts } : null,
            messages = messages.Select(m => new
            {
                role = m.Role, text = m.Content, time = m.Time, stats = m.Stats, worker = m.Role == "assistant" ? StatsWorker(m.Stats) : null,
                images = (m.Images ?? []).Select(i => "/api/file?path=" + Uri.EscapeDataString(i)).ToList(),
            }).ToList(),
        });
    }

    // A picture of the app's own (generated, pasted or a screenshot) by its full path; anything else is 404.
    private static (int, object) WebFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
            return WebNotFound;
        var full = Path.GetFullPath(path);
        var type = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null,
        };
        var inside = new[] { "images", "pasted", "screenshots" }.Any(folder =>
            full.StartsWith(Path.GetFullPath(Path.Combine(AppSettings.DataRoot, folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (type is null || !inside)
            return WebNotFound;
        try { return (200, new WebServer.Response(200, type, File.ReadAllBytes(full))); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return WebNotFound; }
    }

    private async void WebAppToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.WebAppOn = WebAppToggle.IsChecked == true;
        _settings.Save();
        (_webFailure, _webFailureUrl) = (null, null);
        if (_settings.WebAppOn)
            await StartWebAppAsync();
        else
            await StopWebAppAsync();
        RefreshWebApp();
    }

    private void WebAppFixLink_Click(object sender, RoutedEventArgs e)
    {
        if (_webFailureUrl is { } url)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception x) when (x is System.ComponentModel.Win32Exception or InvalidOperationException) { Log($"Phone app: could not open {url}"); }
    }

    private void WebAppCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard(WebAppAddressBox.Text, "Address copied", "No address yet");

    private void RefreshWebApp()
    {
        (WebAppToggle.IsChecked, WebAppToggle.IsEnabled) = (_settings.WebAppOn, !_webStarting);
        WebAppStateText.Text = _webStarting ? "Starting…" : _webApp is not null ? "On" : _webFailure is { } why ? "Not running: " + why : "Off";
        WebAppFixText.Visibility = !_webStarting && _webApp is null && _webFailureUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        var address = WebAppAddress;
        WebAppOnlinePanel.Visibility = address is null ? Visibility.Collapsed : Visibility.Visible;
        if (address is not null && WebAppAddressBox.Text != address)
        {
            WebAppAddressBox.Text = address;
            WebAppQrImage.Source = QrCode(address);
        }
    }

    // --webapp-smoke and --ui-smoke, Tailscale faked (smoke@example.com on tail0000.ts.net; Serve recorded, never run): not
    // ready (no HTTPS certificates), the switch stays on with the reason and nothing runs; ready, on starts the server on a free
    // loopback port, publishes that port, saves WebAppOn and shows the address and its QR code; the owner gets the page, anyone
    // else 403, and a same-tailnet origin reaches the API; off stops the server, turns Serve off and saves WebAppOn off.
    internal async Task<bool> WebAppSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: {what}"); return false; }
        static async Task<bool> Until(Func<bool> done)
        {
            for (var waited = 0; waited < 5000 && !done(); waited += 50)
                await Task.Delay(50);
            return done();
        }
        void Click(bool on)
        {
            WebAppToggle.IsChecked = on;
            WebAppToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        const string tailnet = """
            {"BackendState":"Running","Self":{"UserID":1,"HostName":"home","DNSName":"home.tail0000.ts.net."},
             "CurrentTailnet":{"MagicDNSSuffix":"tail0000.ts.net"},"User":{"1":{"LoginName":"smoke@example.com"}}%CERTS%,
             "Peer":{"k1":{"UserID":1,"HostName":"laptop","OS":"windows","DNSName":"laptop.tail0000.ts.net.","Online":true},
                     "k2":{"UserID":1,"HostName":"desk","OS":"windows","DNSName":"desk.tail0000.ts.net.","Online":true},
                     "k3":{"UserID":2,"HostName":"theirs","DNSName":"theirs.tail0000.ts.net.","Online":true}}}
            """;
        var (status, serve, on, port) = (_webStatus, _webServe, _settings.WebAppOn, _settings.WebAppPort);
        var served = new System.Collections.Concurrent.ConcurrentQueue<int?>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<int> Get(string path, string? login, string method = "GET", string? origin = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{_webApp?.Port}{path}");
            if (login is not null)
                request.Headers.Add("Tailscale-User-Login", login);
            if (origin is not null)
                request.Headers.Add("Origin", origin);
            using var response = await http.SendAsync(request);
            return (int)response.StatusCode;
        }
        try
        {
            _webServe = (p, _) => { served.Enqueue(p); return Task.FromResult((true, "")); };
            _settings.WebAppPort = Random.Shared.Next(20000, 60000);
            _webStatus = () => Task.FromResult<Tailscale.State?>(Tailscale.ParseStatus(tailnet.Replace("%CERTS%", "")));
            Click(true);
            if (!await Until(() => !_webStarting) || _webApp is not null || !served.IsEmpty || !_settings.WebAppOn || WebAppToggle.IsChecked != true ||
                WebAppStateText.Text != "Not running: Turn on HTTPS certificates for your tailnet in the Tailscale admin console (DNS page)" || WebAppAddress is not null)
                return Fail($"not ready: running {_webApp is not null}, served {served.Count}, on {_settings.WebAppOn}, state {WebAppStateText.Text}");
            Click(false);
            if (_settings.WebAppOn || !served.IsEmpty || WebAppStateText.Text != "Off")
                return Fail($"off while not ready: {WebAppStateText.Text}");

            _webStatus = () => Task.FromResult<Tailscale.State?>(Tailscale.ParseStatus(tailnet.Replace("%CERTS%", ""","CertDomains":["home.tail0000.ts.net"]""")));
            Click(true);
            if (!await Until(() => !_webStarting) || _webApp?.Port is not { } bound || bound < _settings.WebAppPort || !served.SequenceEqual([bound]) ||
                !_settings.WebAppOn || !AppSettings.Load().WebAppOn || WebAppStateText.Text != "On")
                return Fail($"on: port {_webApp?.Port}, served [{string.Join(",", served)}], on {_settings.WebAppOn}, state {WebAppStateText.Text}");
            if (WebAppAddress != "https://home.tail0000.ts.net/" || WebAppAddressBox.Text != WebAppAddress || WebAppOnlinePanel.Visibility != Visibility.Visible ||
                WebAppQrImage.Source is not BitmapSource { PixelWidth: > 100 })
                return Fail($"the address {WebAppAddress}, its box or its QR code");
            if (await Get("/", "smoke@example.com") != 200 || await Get("/", null) != 403 || await Get("/", "other@example.com") != 403 ||
                await Get("/api/none", "smoke@example.com", "POST", "https://home.tail0000.ts.net") != 404 ||
                await Get("/api/none", "smoke@example.com", "POST", "https://evil.example") != 403)
                return Fail("the owner, others, and the tailnet's origins");
            if (!await WebApiSmoke(bound) || !await WebEventsSmoke(bound) || !await WebSendSmoke(bound))
                return false;

            // the server stops with a stream open: its subscription goes
            if (_store.Create("", "chat") is not { } idle)
                return Fail("no chat for the open stream");
            _store.Save(idle);
            var lingering = _webEvents.Count; // a stream an earlier check closed goes only at its next keepalive write (up to 15 s)
            using var open = await SseClient.Open(bound, $"/api/conversation/{idle.Id}/events");
            if (!open.Head.StartsWith("HTTP/1.1 200") || _webEvents.Count != lingering + 1)
                return Fail($"a stream before the stop: {open.Head.Split('\r')[0]}, {_webEvents.Count} subscribers");
            Click(false);
            _store.Delete(idle.Id);
            if (!await Until(() => _webEvents.Count == 0))
                return Fail($"the server stopped with {_webEvents.Count} subscribers left");
            if (!await Until(() => served.Count == 2 && !_webStarting) || _webApp is not null || !served.SequenceEqual([bound, null]) || _settings.WebAppOn || AppSettings.Load().WebAppOn ||
                WebAppStateText.Text != "Off" || WebAppOnlinePanel.Visibility == Visibility.Visible || WebAppAddress is not null)
                return Fail($"off: served [{string.Join(",", served)}], on {_settings.WebAppOn}, state {WebAppStateText.Text}");
            try
            {
                using var gone = await http.GetAsync($"http://127.0.0.1:{bound}/");
                return Fail($"the server still answers ({(int)gone.StatusCode})");
            }
            catch (HttpRequestException) { }

            // Quit mid-start: Stop waits for the start in flight, then turns Serve off after it was turned on.
            var calls = new System.Collections.Concurrent.ConcurrentQueue<int?>();
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _webServe = async (p, _) => { calls.Enqueue(p); if (p is not null) await gate.Task; return (true, ""); };
            var start = StartWebAppAsync();
            if (!await Until(() => calls.Count == 1) || !_webStarting)
                return Fail($"mid-start: Serve on not reached, calls [{string.Join(",", calls)}]");
            var stop = StopWebAppAsync();
            await Task.Delay(200);
            if (stop.IsCompleted || calls.Count != 1)
                return Fail($"mid-start: Stop did not wait for the start, calls [{string.Join(",", calls)}]");
            gate.SetResult(true);
            if (!await Until(() => stop.IsCompleted) || !await start || !calls.ToArray().Select(c => c is null).SequenceEqual([false, true]) || _webApp is not null)
                return Fail($"mid-start: calls [{string.Join(",", calls)}], running {_webApp is not null}");
            var midPort = calls.First()!.Value;
            try
            {
                using var gone = await http.GetAsync($"http://127.0.0.1:{midPort}/");
                return Fail($"mid-start: the server still answers ({(int)gone.StatusCode})");
            }
            catch (HttpRequestException) { }

            // Launch: only the normal start (LaunchStartsWebApp, which App sets) turns the phone app on, never a test or tool mode.
            _webServe = (p, _) => { served.Enqueue(p); return Task.FromResult((true, "")); };
            served.Clear();
            (_settings.WebAppOn, LaunchStartsWebApp) = (true, false);
            StartWebAppAtLaunch();
            await Task.Delay(300);
            if (_webApp is not null || _webStarting || !served.IsEmpty)
                return Fail($"launch: a test window started the phone app (running {_webApp is not null}, served {served.Count})");
            LaunchStartsWebApp = true;
            StartWebAppAtLaunch();
            if (!await Until(() => _webApp is not null))
                return Fail("launch: the normal start did not start the phone app");
            await StopWebAppAsync();

            // Serve refused with tailscale's own message: the state line shows its first line and a link to enable Serve.
            const string notEnabled = "Serve is not enabled on your tailnet.\nTo enable, visit:\n\n\thttps://login.tailscale.com/f/serve?node=n123abc\n";
            var refusals = 0;
            _webServe = (p, _) => { refusals++; return Task.FromResult((false, notEnabled)); };
            _webRetryEvery = TimeSpan.FromMilliseconds(100);
            _settings.WebAppOn = true; // a refusal by tailscale serve itself is not retried: the message and link stay
            if (await StartWebAppAsync() || WebAppStateText.Text != "Not running: tailscale serve: Serve is not enabled on your tailnet." ||
                WebAppFixText.Visibility != Visibility.Visible || _webFailureUrl != "https://login.tailscale.com/f/serve?node=n123abc")
                return Fail($"serve refused: state {WebAppStateText.Text}, link {WebAppFixText.Visibility} {_webFailureUrl}");
            await Task.Delay(500);
            if (refusals != 1 || WebAppFixText.Visibility != Visibility.Visible || !WebAppToggle.IsEnabled || _webStarting)
                return Fail($"serve refused: retried ({refusals} calls), link {WebAppFixText.Visibility}, switch enabled {WebAppToggle.IsEnabled}");
            Click(false);
            if (WebAppFixText.Visibility == Visibility.Visible || _webFailureUrl is not null || WebAppStateText.Text != "Off")
                return Fail($"serve refused, switched off: state {WebAppStateText.Text}, link {WebAppFixText.Visibility}");
            _settings.WebAppOn = false;

            // Not ready at first (the retry timer runs), then ready and refused by tailscale serve: no more retries.
            refusals = 0;
            var reads = 0;
            _webStatus = () => Task.FromResult(reads++ == 0 ? null : Tailscale.ParseStatus(tailnet.Replace("%CERTS%", ""","CertDomains":["home.tail0000.ts.net"]""")));
            _settings.WebAppOn = true;
            _ = StartWebAppAsync();
            await Until(() => refusals >= 1 && !_webStarting); // under load the retry is late: wait for it, then for a few more intervals to see it does not repeat
            await Task.Delay(400);
            if (refusals != 1 || _webRetry.IsEnabled || _webStarting || WebAppFixText.Visibility != Visibility.Visible)
                return Fail($"not ready then refused: {refusals} serve calls, retry timer {_webRetry.IsEnabled}, link {WebAppFixText.Visibility}");
            Click(false);

            // A failed start is retried while the switch is on: tailscaled still starting (status null at first), then ready.
            _webServe = (p, _) => { served.Enqueue(p); return Task.FromResult((true, "")); };
            var statusCalls = 0;
            _webStatus = () => Task.FromResult(statusCalls++ == 0 ? null : Tailscale.ParseStatus(tailnet.Replace("%CERTS%", ""","CertDomains":["home.tail0000.ts.net"]""")));
            _webRetryEvery = TimeSpan.FromMilliseconds(100);
            (_settings.WebAppOn, LaunchStartsWebApp) = (true, true);
            StartWebAppAtLaunch();
            if (!await Until(() => _webApp is not null) || statusCalls != 2 || WebAppStateText.Text != "On")
                return Fail($"retry: running {_webApp is not null}, status calls {statusCalls}, state {WebAppStateText.Text}");
            // switched off while failing: no more tries
            await StopWebAppAsync();
            statusCalls = 0;
            _webStatus = () => { statusCalls++; return Task.FromResult<Tailscale.State?>(null); };
            _webRetry.Stop();
            _ = StartWebAppAsync();
            await Until(() => statusCalls > 1);
            _settings.WebAppOn = false;
            await StopWebAppAsync();
            var seen = statusCalls;
            await Task.Delay(400);
            if (statusCalls != seen || _webRetry.IsEnabled)
                return Fail($"retry: kept trying after the switch went off ({seen} -> {statusCalls})");
            return true;
        }
        finally
        {
            await StopWebAppAsync();
            (_webStatus, _webServe, _settings.WebAppOn, _settings.WebAppPort, _webFailure) = (status, serve, on, port, null);
            _settings.Save();
            RefreshWebApp();
        }
    }

    // An events stream read off a raw socket, so the framing is checked as sent: chunked, one `data:` frame or keepalive per chunk.
    private sealed class SseClient : IDisposable
    {
        private readonly System.Net.Sockets.TcpClient _tcp = new();
        private Stream _stream = Stream.Null;
        public string Head = "";
        public int KeepAlives;
        public static readonly TimeSpan Within = TimeSpan.FromSeconds(10); // every read's deadline: keepalives never extend it

        // `post`: a POST instead, whose Content-Length promises more than `post`, all it sends (WebApiSmoke: a body never read).
        public static async Task<SseClient> Open(int port, string path, string? login = "smoke@example.com", string? site = null, string? post = null)
        {
            var c = new SseClient();
            await c._tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
            c._stream = c._tcp.GetStream();
            var request = $"{(post is null ? "GET" : "POST")} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" + (login is null ? "" : $"Tailscale-User-Login: {login}\r\n") +
                (site is null ? "" : $"Sec-Fetch-Site: {site}\r\n") + (post is null ? "" : $"Content-Type: application/json\r\nContent-Length: {post.Length + 1000}\r\n") + "\r\n" + post;
            await c._stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
            using var deadline = new CancellationTokenSource(Within);
            for (string line; (line = await c.Line(deadline.Token)).Length > 0;)
                c.Head += line + "\r\n";
            return c;
        }

        private async Task<string> Line(CancellationToken deadline)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (await _stream.ReadAsync(one, deadline) == 1 && one[0] != '\n')
                bytes.Add(one[0]);
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
        }

        // The next event by the deadline (keepalives counted and skipped); throws when a chunk is not exactly one frame.
        public async Task<JsonNode> Next(CancellationToken deadline)
        {
            while (true)
            {
                var size = Convert.ToInt32(await Line(deadline), 16);
                var data = new byte[size + 2];
                await _stream.ReadExactlyAsync(data, deadline);
                var text = System.Text.Encoding.UTF8.GetString(data, 0, size);
                if (text == WebServer.PingFrame)
                {
                    KeepAlives++;
                    continue;
                }
                if (!text.StartsWith("data: ") || !text.EndsWith("\n\n") || text[..^2].Contains('\n'))
                    throw new InvalidDataException($"not one frame: {text}");
                return JsonNode.Parse(text[6..^2])!;
            }
        }

        public void Dispose() => _tcp.Dispose();
    }

    // Live events (WebAppSmoke, while on), a chat turn on a stand-in engine whose deltas wait for gate files: the stream is
    // text/event-stream, chunked, one frame per chunk, keepalives while idle; hello, the deltas in order, saved; a PC popup opened
    // during the turn shows as the state's prompt, send waits for it and /api/answer with its nonce resolves it; a stream that
    // reconnects mid-turn gets the reply so far in its hello and the rest as deltas, once, and after a tool call that reply so far
    // holds both sections; the gates refuse it; a client that leaves is unsubscribed. A missing event fails by its read's deadline.
    private async Task<bool> WebEventsSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: events: {what}"); return false; }
        static async Task<bool> Until(Func<bool> done)
        {
            for (var waited = 0; waited < 5000 && !done(); waited += 50)
                await Task.Delay(50);
            return done();
        }
        var (root, project, choice, notify, quiet, keepAlive) = (_workflowRoot, _projectPath, _codeChoice, _notifyHook, _dialogsQuiet, _webApp!.KeepAlive);
        var engine = Path.Combine(AppSettings.DataRoot, "webapp-engine");
        Directory.CreateDirectory(engine);
        // it gives up on a gate after 60 s, so a run that died early never leaves it waiting
        File.WriteAllText(Path.Combine(engine, "orchestrator.py"), "import json, os, sys, time\n" +
            "def ev(**e): print('@@AO ' + json.dumps(e), flush=True)\n" +
            "def wait(name):\n    end = time.time() + 60\n" +
            "    while not os.path.exists(os.path.join(os.path.dirname(os.path.abspath(__file__)), name)):\n" +
            "        if time.time() > end: sys.exit(1)\n        time.sleep(0.05)\n" +
            "wait('go0')\nev(e='delta', role='planner', kind='text', text='One ')\nwait('go1')\nev(e='delta', role='planner', kind='text', text='two ')\n" +
            "ev(e='tool', role='planner', name='read_file', args='{}', result='OK')\nev(e='delta', role='planner', kind='text', text='three')\n" +
            "wait('go2')\nev(e='delta', role='planner', kind='text', text=' four')\nev(e='chat_reply', role='planner', text='three four')\n");
        File.WriteAllText(Path.Combine(engine, "config.json"), """{"roles": {}}""");
        void Go(string gate) => File.WriteAllText(Path.Combine(engine, gate), "");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("Tailscale-User-Login", "smoke@example.com");
        var streams = new List<SseClient>();
        async Task<SseClient> Open(string id) { var s = await SseClient.Open(port, $"/api/conversation/{id}/events"); streams.Add(s); return s; }
        // events up to the first that `last` accepts, all by one deadline: a missing event fails the check (keepalives never extend it)
        static async Task<List<JsonNode>> ReadTo(SseClient s, string what, Func<JsonNode, bool> last)
        {
            var got = new List<JsonNode>();
            using var deadline = new CancellationTokenSource(SseClient.Within);
            try
            {
                do got.Add(await s.Next(deadline.Token)); while (!last(got[^1]));
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"no {what} within {SseClient.Within.TotalSeconds} s, got [{string.Join(" ", got.Select(n => n.ToJsonString()))}]");
            }
            return got;
        }
        static async Task<JsonNode> One(SseClient s, string what) => (await ReadTo(s, what, _ => true))[0];
        static string E(JsonNode n) => n["e"]?.ToString() ?? "";
        static string Deltas(IEnumerable<JsonNode> events) => string.Concat(events.Where(n => E(n) == "delta").Select(n => n["text"]?.ToString()));
        try
        {
            (_workflowRoot, _projectPath, _codeChoice, _notifyHook, _dialogsQuiet, _webApp.KeepAlive) = (engine, "", null, (_, _) => { }, true, TimeSpan.FromMilliseconds(200));
            var placeholder = !File.Exists(LauncherLogic.LlamaServerPath); // a turn needs the runtime there; the stand-in engine never runs it
            if (placeholder)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LauncherLogic.LlamaServerPath)!);
                File.WriteAllBytes(LauncherLogic.LlamaServerPath, []);
            }
            var started = RemoteSend("", "Count to three");
            if (placeholder)
                File.Delete(LauncherLogic.LlamaServerPath);
            if (started is not (200, _) || RunningId is not { } id)
                return Fail($"the turn did not start: {started.Item1} {JsonSerializer.Serialize(started.Item2)}, running {RunningId}, busy {_chatBusy}");
            var a = await Open(id);
            if (!a.Head.StartsWith("HTTP/1.1 200") || !a.Head.Contains("Content-Type: text/event-stream", StringComparison.OrdinalIgnoreCase) ||
                !a.Head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
                return Fail($"the stream's head: {a.Head}");
            if (await One(a, "hello") is not { } hello || E(hello) != "hello" || hello["busy"]?.GetValue<bool>() != true || hello["reply"]?["text"]?.ToString() != "")
                return Fail("the hello");
            await Task.Delay(500); // idle: keepalives
            Go("go0");
            var first = await ReadTo(a, "first delta", n => E(n) == "delta");
            if (a.KeepAlives == 0)
                return Fail("no keepalive while idle");

            // a PC popup during the turn: mirrored as the state's prompt; send waits for it; the phone's answer resolves it
            var asked = Dispatcher.InvokeAsync(() => Choose("Folder access", "What may the worker do in C:\\elsewhere?", ("Read only", "read"), ("Full edit access", "write")));
            var shown = (await ReadTo(a, "state with the prompt", n => E(n) == "state" && n["prompt"] is not null))[^1]["prompt"]!;
            if (shown["title"]?.ToString() != "Folder access" || shown["nonce"]?.ToString() is not { Length: > 0 } nonce)
                return Fail($"the prompt: {shown}");
            if (RemoteAct("POST", "/api/send", WebBody(System.Text.Encoding.UTF8.GetBytes($$"""{"id":"{{id}}","text":"during the popup"}"""))) is not (409, _))
                return Fail("send ran while the popup was open");
            async Task<int> Answer(string body)
            {
                using var answer = await http.PostAsync($"http://127.0.0.1:{port}/api/answer", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                return (int)answer.StatusCode;
            }
            if (await Answer($$"""{"id":"{{id}}","choice":"write","nonce":"0123"}""") != 409 || await Answer($$"""{"id":"{{id}}","choice":"write","nonce":"{{nonce}}"}""") != 200 ||
                await Task.WhenAny(asked.Task, Task.Delay(5000)) != asked.Task || asked.Result != "write")
                return Fail("the phone's answer did not resolve the popup");
            await ReadTo(a, "state without the prompt", n => E(n) == "state" && n["prompt"] is null);

            // the picture card during the turn: start, progress, preview, done and end on the stream; its latest preview by GET while
            // it is open, 404 after; a stream opened meanwhile gets it in its hello
            byte[] dot = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");
            async Task<(int Status, string? Type, byte[] Body)> Picture()
            {
                using var got = await http.GetAsync($"http://127.0.0.1:{port}/api/conversation/{id}/picture?frame=1");
                return ((int)got.StatusCode, got.Content.Headers.ContentType?.MediaType, await got.Content.ReadAsByteArrayAsync());
            }
            ShowPendingPicture(1024, 1536);
            PendingPictureProgress("Step 2/8…", 0.25);
            ShowImagePreview(dot);
            var preview = await Picture();
            if (preview.Status != 200 || preview.Type != "image/png" || !preview.Body.SequenceEqual(dot))
                return Fail($"the picture's preview while open: {preview.Status} {preview.Type}, {preview.Body.Length} bytes");
            var midway = await Open(id);
            var withPicture = (await One(midway, "hello with the picture"))["picture"];
            midway.Dispose();
            if (withPicture?["width"]?.GetValue<int>() != 1024 || withPicture["height"]?.GetValue<int>() != 1536 || withPicture["frame"]?.GetValue<int>() != 1)
                return Fail($"the hello's picture: {withPicture?.ToJsonString() ?? "null"}");
            var saved = Path.Combine(ImageGen.ImagesFolder, "smoke-picture.png");
            PendingPictureDone(saved);
            RemovePendingPicture();
            var pictures = (await ReadTo(a, "the picture's end", n => E(n) == "picture" && n["state"]?.ToString() == "end")).Where(n => E(n) == "picture").ToList();
            string Said(JsonNode n) => n["state"]?.ToString() switch
            {
                "start" => $"start {n["width"]}x{n["height"]}",
                "progress" => $"progress {n["text"]} {n["progress"]}",
                "preview" => $"preview {n["frame"]}",
                "done" => $"done {(n["path"]?.ToString() == saved ? "path" : n["path"])}",
                var state => state ?? "",
            };
            if (string.Join(", ", pictures.Select(Said)) != "start 1024x1536, progress Step 2/8 0.25, preview 1, done path, end")
                return Fail($"the picture's events: {string.Join(" ", pictures.Select(n => n.ToJsonString()))}");
            if (await Picture() is { Status: not 404 } after)
                return Fail($"the picture's preview after the end: {after.Status}");
            if (!await Until(() => _webEvents.Count == 1))
                return Fail($"the stream opened for the picture's hello is still subscribed ({_webEvents.Count})");

            // one that leaves mid-turn is unsubscribed; one that reconnects gets the reply so far, then the rest, once
            var b = await Open(id);
            await One(b, "hello on the second stream");
            if (_webEvents.Count != 2)
                return Fail($"{_webEvents.Count} subscribers with two streams");
            b.Dispose();
            if (!await Until(() => _webEvents.Count == 1))
                return Fail($"a client that left is still subscribed ({_webEvents.Count})");
            var sofar = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/conversation/{id}"))?["reply"]?["text"]?.ToString();
            var c = await Open(id);
            var again = await One(c, "hello on the reconnect");
            Go("go1");
            var second = await ReadTo(a, "second section's delta", n => E(n) == "delta" && n["text"]?.ToString() == "three");
            // a reconnect after a tool call started a second section: the reply so far holds both sections, once
            var sections = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/conversation/{id}"))?["reply"]?["text"]?.ToString();
            var d = await Open(id);
            var twoSections = await One(d, "hello after the tool call");
            Go("go2");
            var rest = await ReadTo(a, "saved on the first stream", n => E(n) == "saved");
            var resumed = await ReadTo(c, "saved on the reconnect", n => E(n) == "saved");
            var late = await ReadTo(d, "saved after the tool call", n => E(n) == "saved");
            var all = first.Concat(second).Concat(rest).ToList();
            if (Deltas(all) != "One two three four" || !second.Any(n => E(n) == "tool") || !rest.Any(n => E(n) == "chat_reply"))
                return Fail($"stream A: {string.Join(" ", all.Select(n => n.ToJsonString()))}");
            if (sofar != "One " || again["reply"]?["text"]?.ToString() != sofar || sofar + Deltas(resumed) != "One two three four")
                return Fail($"the reconnect: so far {sofar}, hello {again.ToJsonString()}, then {Deltas(resumed)}");
            if (sections != "One two\n\nthree" || twoSections["reply"]?["text"]?.ToString() != sections || Deltas(late) != " four")
                return Fail($"the reconnect after a tool call: so far {sections}, hello {twoSections.ToJsonString()}, then {Deltas(late)}");
            if (_store.Get(id)?.Messages.LastOrDefault()?.Content != "One two\n\nthree four")
                return Fail($"saved before the reply was: {_store.Get(id)?.Messages.LastOrDefault()?.Content}");

            // the gates: no owner, another account, a cross-site page
            foreach (var (login, site) in new[] { ((string?)null, (string?)null), ("other@example.com", null), ("smoke@example.com", "cross-site") })
            {
                using var refused = await SseClient.Open(port, $"/api/conversation/{id}/events", login, site);
                if (!refused.Head.StartsWith("HTTP/1.1 403"))
                    return Fail($"{login} {site}: {refused.Head.Split('\r')[0]}");
            }
            if ((await http.GetAsync($"http://127.0.0.1:{port}/api/conversation/none/events")).StatusCode != System.Net.HttpStatusCode.NotFound)
                return Fail("an unknown conversation's events");
            using (var noId = await http.GetAsync($"http://127.0.0.1:{port}/api/conversation/events"))
                if (noId.StatusCode != System.Net.HttpStatusCode.NotFound || JsonNode.Parse(await noId.Content.ReadAsStringAsync())?["error"]?.ToString() != "Not found")
                    return Fail($"events without an id: {(int)noId.StatusCode}");
            foreach (var s in streams)
                s.Dispose();
            if (!await Until(() => _webEvents.Count == 0))
                return Fail($"{_webEvents.Count} subscribers after the streams closed");
            return true;
        }
        catch (Exception x) when (x is IOException or InvalidDataException or OperationCanceledException or TimeoutException or FormatException or HttpRequestException)
        {
            return Fail(x.Message);
        }
        finally
        {
            foreach (var s in streams)
                s.Dispose();
            // after a failed check: close its popup and let the turn end, or closing the window would ask (unseen) and hang the run
            foreach (var popup in OwnedWindows.OfType<DialogShell>().ToList())
                popup.Close();
            foreach (var gate in new[] { "go0", "go1", "go2" })
                Go(gate);
            await Until(() => !_chatBusy);
            (_workflowRoot, _projectPath, _codeChoice, _notifyHook, _dialogsQuiet) = (root, project, choice, notify, quiet);
            if (_webApp is not null)
                _webApp.KeepAlive = keepAlive;
        }
    }

    // Sending (WebAppSmoke, while on), the window hidden, then minimized, and never shown by a route: a PNG uploaded into
    // pasted\ under a unique safe name and sent with "describe this" to a new chat on a stand-in engine (the reply in the stream,
    // the user message holding the picture); a second send leaves the minimized window minimized; 26 MB is 413 with its message;
    // a file type Attach refuses is 400 and deleted; an attachment outside pasted\ and an empty message are 400; /compact goes and another slash command is 400; stop routes;
    // the composer is read and written (saved, the desktop toggles following); send while a PC dialog is open is 409.
    private async Task<bool> WebSendSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: send: {what}"); return false; }
        static async Task<bool> Until(Func<bool> done)
        {
            for (var waited = 0; waited < 5000 && !done(); waited += 50)
                await Task.Delay(50);
            return done();
        }
        var (root, project, choice, notify, quiet, search, visible, activated) = (_workflowRoot, _projectPath, _codeChoice, _notifyHook, _dialogsQuiet, _settings.UseSearch, IsVisible, ShowActivated);
        var (shareHost, sharedFetch, chatImage) = (_settings.ShareHost, _imageSharedFetch, JsonSerializer.Serialize(_settings.ChatImage));
        var engine = Path.Combine(AppSettings.DataRoot, "webapp-send-engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(engine, "orchestrator.py"), "import json, os, sys, time\n" +
            "def ev(**e): print('@@AO ' + json.dumps(e), flush=True)\n" +
            "end = time.time() + 60\n" +
            "while not os.path.exists(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'go')):\n" +
            "    if time.time() > end: sys.exit(1)\n    time.sleep(0.05)\n" +
            "ev(e='delta', role='planner', kind='text', text='A red dot')\nev(e='chat_reply', role='planner', text='A red dot')\n");
        File.WriteAllText(Path.Combine(engine, "config.json"), """{"roles": {}}""");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("Tailscale-User-Login", "smoke@example.com");
        async Task<(int Status, JsonNode? Body)> Post(string path, HttpContent content, string? name = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}") { Content = content };
            if (name is not null)
                request.Headers.Add("X-File-Name", name);
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            try { return ((int)response.StatusCode, JsonNode.Parse(text)); }
            catch (JsonException) { return ((int)response.StatusCode, null); }
        }
        Task<(int Status, JsonNode? Body)> PostJson(string path, object body) => Post(path, new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));
        var placeholder = !File.Exists(LauncherLogic.LlamaServerPath); // a turn needs the runtime there; the stand-in engine never runs it
        string? created = null;
        try
        {
            (_workflowRoot, _projectPath, _codeChoice, _notifyHook, _dialogsQuiet) = (engine, "", null, (_, _) => { }, true);
            if (placeholder)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LauncherLogic.LlamaServerPath)!);
                File.WriteAllBytes(LauncherLogic.LlamaServerPath, []);
            }
            Hide();
            var pasted = Path.Combine(AppSettings.DataRoot, "pasted");
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");
            var up = await Post("/api/upload", new ByteArrayContent(png), Uri.EscapeDataString(@"..\phone dot.png"));
            if (up is not (200, { } u) || u["path"]?.ToString() is not { } photo || Path.GetDirectoryName(photo) != pasted || !Path.GetFileName(photo).EndsWith("-phone dot.png") ||
                !File.ReadAllBytes(photo).SequenceEqual(png))
                return Fail($"upload: {up.Status} {up.Body?.ToJsonString()}");
            var sent = await PostJson("/api/send", new { id = "", text = "describe this", attachments = new[] { photo } });
            if (sent is not (200, { } s) || s["id"]?.ToString() is not { Length: > 0 } id || _store.Get(id) is null)
                return Fail($"send with the photo: {sent.Status} {sent.Body?.ToJsonString()}");
            created = id;
            using (var stream = await SseClient.Open(port, $"/api/conversation/{id}/events"))
            {
                File.WriteAllText(Path.Combine(engine, "go"), "");
                var events = new List<JsonNode>();
                using var deadline = new CancellationTokenSource(SseClient.Within);
                while (events.LastOrDefault()?["e"]?.ToString() != "saved")
                    events.Add(await stream.Next(deadline.Token));
                if (!events.Any(n => n["e"]?.ToString() == "delta" && n["text"]?.ToString() == "A red dot"))
                    return Fail($"the reply's stream: {string.Join(" ", events.Select(n => n.ToJsonString()))}");
            }
            if (!await Until(() => !_chatBusy) || _store.Get(id)?.Messages is not { Count: 2 } saved || saved[0].Role != "user" || !saved[0].Content.StartsWith("describe this") ||
                saved[0].Images?.SequenceEqual([photo]) != true || saved[1].Content != "A red dot")
                return Fail($"the saved turn: {JsonSerializer.Serialize(_store.Get(id)?.Messages)}");
            if (IsVisible)
                return Fail("a send showed the hidden window");

            (ShowActivated, WindowState) = (false, WindowState.Minimized);
            Show();
            if (await PostJson("/api/send", new { id, text = "and again" }) is not (200, _) || !await Until(() => !_chatBusy && _store.Get(id)?.Messages.Count == 4) ||
                WindowState != WindowState.Minimized)
                return Fail($"a send to the minimized window: {_store.Get(id)?.Messages.Count} messages, {WindowState}");

            var big = await Post("/api/upload", new ByteArrayContent(new byte[26 << 20]), "big.png");
            if (big is not (413, { } b) || b["error"]?.ToString() != "The file is over 25 MB")
                return Fail($"26 MB: {big.Status} {big.Body?.ToJsonString()}");
            var blob = await Post("/api/upload", new ByteArrayContent([0, 1, 2, 0, 255, 0, 7]), "blob.bin");
            if (blob is not (400, { } r) || r["error"]?.ToString()?.Contains("isn't a text file or photo") != true || Directory.GetFiles(pasted).Length != 1)
                return Fail($"a refused file type: {blob.Status} {blob.Body?.ToJsonString()}, {Directory.GetFiles(pasted).Length} files in pasted");
            var outside = Path.Combine(AppSettings.DataRoot, "outside.png");
            File.WriteAllBytes(outside, png);
            foreach (var bad in new[] { outside, Path.Combine(pasted, "..", "outside.png"), Path.Combine(pasted, "none.png"), @"pasted\" + Path.GetFileName(photo) })
                if (await PostJson("/api/send", new { id, text = "with a PC file", attachments = new[] { bad } }) is not (400, _))
                    return Fail($"{bad} was attached");
            if (await PostJson("/api/send", new { id, text = "  " }) is not (400, _) || _store.Get(id)?.Messages.Count != 4)
                return Fail("an empty message, or a refused one, was sent");
            // /compact summarizes this conversation and changes no setting: it goes; another slash command stays at the PC
            if (await PostJson("/api/send", new { id, text = "/compact keep the colours" }) is not (200, _) || !await Until(() => !_chatBusy))
                return Fail("/compact was not sent");
            if (await PostJson("/api/send", new { id, text = "/ponytail ultra" }) is not (400, { } slash) || slash["error"]?.ToString() != "Slash commands only work at the PC.")
                return Fail("/ponytail ultra was not refused");
            if (await PostJson("/api/stop", new { id }) is not (409, { } idle) || idle["error"]?.ToString() != "Nothing is running there.")
                return Fail("stop while idle");

            _settings.UseSearch = true;
            RefreshExtras();
            var composer = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/composer"));
            if (composer?["search"]?.GetValue<bool>() != true || composer["worker"]?.ToString() != ChatTarget()?.Role || composer["workers"]?.AsArray().Any(w => w?["role"]?.ToString() == "planner") != true ||
                composer["skills"] is not JsonArray || composer["imageGen"]?["steps"] is null || composer["effort"] is null)
                return Fail($"the composer: {composer?.ToJsonString()}");
            if (await PostJson("/api/composer", new { search = false }) is not (200, _) || _settings.UseSearch || AppSettings.Load().UseSearch ||
                ChatSearchToggle.IsChecked != false || CodeSearchToggle.IsChecked != false)
                return Fail($"composer search off: setting {_settings.UseSearch}, toggle {ChatSearchToggle.IsChecked}");
            if (await PostJson("/api/composer", new { worker = "nobody" }) is not (400, _) || await PostJson("/api/composer", new { effort = "loud" }) is not (400, _))
                return Fail("an unknown worker or effort was taken");

            // the chat's image model and size: the chip's choices, picked as the chip does; the primary PC's from the swapped fetch
            _settings.ChatImage.RecipeId = ImageGen.Recipes[0].Id;
            var image = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/composer"))?["imageGen"];
            if (image?["model"]?.ToString() != ImageGen.Recipes[0].Id || image["models"]?.AsArray().Any(m => m?["id"]?.ToString() == ImageGen.Recipes[0].Id && m["primary"]?.GetValue<bool>() == false) != true ||
                image["aspects"]?.AsArray().Count != ImageGen.Presets.Length)
                return Fail($"the composer's image models: {image?.ToJsonString()}");
            var other = image["models"]!.AsArray().Select(m => m!["id"]!.ToString()).FirstOrDefault(m => m != ImageGen.Recipes[0].Id && ImageGen.Recipes.Any(r => r.Id == m));
            if (other is null)
                return Fail("no other recipe listed");
            var multiple = ImageGen.Recipe(other).Multiple;
            if (await PostJson("/api/composer", new { imageGen = new { model = other, aspect = "16:9" } }) is not (200, _) || _settings.ChatImage is not { LocalModel: "", Aspect: "16:9" } chosen ||
                chosen.RecipeId != other || AppSettings.Load().ChatImage.RecipeId != other ||
                ImageGen.ChatRequest(chosen, false) is not { } wide || (wide.Width, wide.Height) != ImageGen.Size("16:9", ImageGen.RecommendedLongSide, multiple) ||
                ChatImageGenToggle.ToolTip as string != $"Image Gen: {ImageGen.Recipe(other).Name}, {wide.Width} × {wide.Height}")
                return Fail($"picking {other} at 16:9: {JsonSerializer.Serialize(_settings.ChatImage)}, tip {ChatImageGenToggle.ToolTip}");
            if (await PostJson("/api/composer", new { imageGen = new { width = 1000 } }) is not (200, _) || ImageGen.ChatRequest(_settings.ChatImage, false) is not { } typed ||
                (typed.Width, typed.Height) != (ImageGen.Pick(1000, 0, multiple), ImageGen.Linked(ImageGen.Pick(1000, 0, multiple), "16:9", true, multiple)) || _settings.ChatImage.Aspect != "16:9")
                return Fail($"a typed width at 16:9: {JsonSerializer.Serialize(_settings.ChatImage)}");
            // an edited height leads, the width follows (the chip's Commit(width: false)), alone or beside the echoed width box
            var h720 = ImageGen.Pick(720, 0, multiple);
            if (await PostJson("/api/composer", new { imageGen = new { height = 720 } }) is not (200, _) || _settings.ChatImage is not { Aspect: "16:9" } byHeight ||
                (byHeight.CustomHeight, byHeight.CustomWidth) != (h720, ImageGen.Linked(h720, "16:9", false, multiple)))
                return Fail($"a typed height at 16:9: {JsonSerializer.Serialize(_settings.ChatImage)}");
            var h540 = ImageGen.Pick(540, 0, multiple);
            if (await PostJson("/api/composer", new { imageGen = new { width = _settings.ChatImage.CustomWidth, height = 540 } }) is not (200, _) || _settings.ChatImage is not { Aspect: "16:9" } both ||
                (both.CustomHeight, both.CustomWidth) != (h540, ImageGen.Linked(h540, "16:9", false, multiple)))
                return Fail($"both boxes, the height edited: {JsonSerializer.Serialize(_settings.ChatImage)}");
            if (await PostJson("/api/composer", new { imageGen = new { model = "no-such-model" } }) is not (400, _) || _settings.ChatImage.RecipeId != other)
                return Fail("an unknown image model was taken");
            (_settings.ShareHost, _imageSharedFetch) = ("primary.test", (_, _) => Task.FromResult(new List<ImageShare.Model> { new(ImageGen.Recipes[0].Id, "Shared", "", false, 16, 8, 1, "") }));
            if (await PostJson("/api/composer", new { imageGen = new { model = "primary:" + ImageGen.Recipes[0].Id } }) is not (200, { } onPrimary) ||
                _settings.ChatImage.SharedModel != ImageGen.Recipes[0].Id || onPrimary["imageGen"]?["models"]?.AsArray().Any(m => m?["primary"]?.GetValue<bool>() == true) != true)
                return Fail($"the primary PC's model: {JsonSerializer.Serialize(_settings.ChatImage)}");
            if (WindowState != WindowState.Minimized)
                return Fail($"the window is {WindowState} after the routes");

            var asked = Dispatcher.InvokeAsync(() => Choose("Folder access", "What may the worker do in C:\\elsewhere?", ("Read only", "read"), ("Full edit access", "write")));
            if (!await Until(() => _remotePrompt is not null))
                return Fail("the popup did not open");
            var during = await PostJson("/api/send", new { id, text = "during the popup" });
            _remotePrompt!.Resolve("read");
            await Task.WhenAny(asked.Task, Task.Delay(5000));
            if (during is not (409, { } d) || d["error"]?.ToString() != "DialogOpen")
                return Fail($"send while a dialog was open: {during.Status} {during.Body?.ToJsonString()}");

            // a PC popup while no turn runs: any conversation shows it, and the phone's answer with its nonce resolves it
            var noTurn = Dispatcher.InvokeAsync(() => Choose("Folder access", "What may the worker do in C:\\elsewhere?", ("Read only", "read"), ("Full edit access", "write")));
            if (!await Until(() => _remotePrompt is not null))
                return Fail("the popup outside a turn did not open");
            var idlePrompt = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/conversation/{id}"))?["prompt"];
            if (idlePrompt?["title"]?.ToString() != "Folder access" || idlePrompt["nonce"]?.ToString() is not { Length: > 0 } idleNonce)
                return Fail($"the popup outside a turn is not the conversation's prompt: {idlePrompt?.ToJsonString()}");
            if (await PostJson("/api/answer", new { id, choice = "write", nonce = idleNonce }) is not (200, _) ||
                await Task.WhenAny(noTurn.Task, Task.Delay(5000)) != noTurn.Task || noTurn.Result != "write")
                return Fail("the phone's answer did not resolve the popup outside a turn");

            // a popup tied to the conversation (as the sandbox's command approval): send and stop still wait for it
            var bound = Dispatcher.InvokeAsync(() => Choose(remote: id, "Run this command?", "git status", ("Allow", "allow"), ("Deny", "deny")));
            if (!await Until(() => _remotePrompt is not null))
                return Fail("the bound popup did not open");
            var (boundSend, boundStop) = (await PostJson("/api/send", new { id, text = "during the bound popup" }), await PostJson("/api/stop", new { id }));
            _remotePrompt!.Resolve("deny");
            await Task.WhenAny(bound.Task, Task.Delay(5000));
            if (boundSend is not (409, { } bs) || bs["error"]?.ToString() != "DialogOpen" || boundStop is not (409, { } bt) || bt["error"]?.ToString() != "DialogOpen")
                return Fail($"send and stop while a bound popup was open: {boundSend.Status} {boundSend.Body?.ToJsonString()}, {boundStop.Status} {boundStop.Body?.ToJsonString()}");
            return true;
        }
        catch (Exception x) when (x is IOException or InvalidDataException or OperationCanceledException or FormatException or HttpRequestException)
        {
            return Fail(x.Message);
        }
        finally
        {
            foreach (var popup in OwnedWindows.OfType<DialogShell>().ToList())
                popup.Close();
            File.WriteAllText(Path.Combine(engine, "go"), "");
            await Until(() => !_chatBusy);
            if (placeholder)
                File.Delete(LauncherLogic.LlamaServerPath);
            if (created is not null)
                _store.Delete(created);
            (_workflowRoot, _projectPath, _codeChoice, _notifyHook, _dialogsQuiet, _settings.UseSearch) = (root, project, choice, notify, quiet, search);
            (_settings.ShareHost, _imageSharedFetch, _imageShared, _settings.ChatImage) = (shareHost, sharedFetch, null, JsonSerializer.Deserialize<ImageSettings>(chatImage)!);
            _settings.Save();
            RefreshExtras();
            WindowState = WindowState.Normal;
            if (visible)
                Show();
            else
                Hide();
            ShowActivated = activated;
        }
    }

    // The read API over the running server's socket (WebAppSmoke, while on): every route refuses a request without the owner's
    // login; this PC and the owner's peers, probed in parallel (the faked probe); a stored chat with an assistant picture is listed
    // under recents and read back with its picture's URL, which returns the bytes; nothing outside the data folder's images,
    // pasted and screenshots pictures is served; the security headers, no-store on /api only, and CORS for one tailnet label only.
    private async Task<bool> WebApiSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: api: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<(int Status, System.Net.Http.Headers.HttpResponseHeaders Headers, string Type, byte[] Body)> Fetch(string path, string? login = "smoke@example.com", string? origin = null,
            HttpMethod? method = null, HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"http://127.0.0.1:{port}{path}") { Content = content };
            if (login is not null)
                request.Headers.Add("Tailscale-User-Login", login);
            if (origin is not null)
                request.Headers.Add("Origin", origin);
            using var response = await http.SendAsync(request);
            return ((int)response.StatusCode, response.Headers, response.Content.Headers.ContentType?.MediaType ?? "", await response.Content.ReadAsByteArrayAsync());
        }
        async Task<JsonNode?> Json(string path) => await Fetch(path) is { Status: 200 } r ? JsonNode.Parse(r.Body) : null;
        static string File(string path) => "/api/file?path=" + Uri.EscapeDataString(path);

        var (probe, root) = (_webProbe, AppSettings.DataRoot);
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(images + "-x");
        var picture = Path.Combine(images, "smoke fox.png");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        System.IO.File.WriteAllBytes(picture, png);
        // pictures beside the served folders and in a look-alike folder, and a non-picture inside one: none is served
        System.IO.File.WriteAllBytes(Path.Combine(root, "secret.png"), png);
        System.IO.File.WriteAllBytes(Path.Combine(images + "-x", "a.png"), png);
        System.IO.File.WriteAllText(Path.Combine(images, "notes.txt"), "text");
        if (_store.Create("", "chat") is not { } chat)
            return Fail("no stored chat");
        try
        {
            chat.Messages.AddRange([new("user", "Paint a fox", DateTimeOffset.Now), new("assistant", "Here it is", DateTimeOffset.Now, [picture], "12 tok/s"),
                new("assistant", "Done", DateTimeOffset.Now, null, "Lead · 81.1 tok/s · 5 generated"), new("assistant", "Again", DateTimeOffset.Now, null, "5 generated · 7 prompt")]);
            chat.Worker = "planner";
            chat.Context = new ContextSnapshot { NCtx = 1000, Prompt = 200, Completion = 50, Parts = new() { ["system"] = 100 } };
            _store.Save(chat);

            foreach (var route in new[] { "/api/hello", "/api/pcs", "/api/conversations", "/api/conversation/" + chat.Id, File(picture), "/" })
                if ((await Fetch(route, null)).Status != 403 || (await Fetch(route, "other@example.com")).Status != 403)
                    return Fail($"{route} without the owner's login was not refused");
            foreach (var login in new[] { null, "other@example.com" })
                if (await Fetch("/api/send", login, method: HttpMethod.Post, content: new StringContent("""{"id":"","text":"hi"}""", System.Text.Encoding.UTF8, "application/json")) is not { Status: 403 } foreign ||
                    System.Text.Encoding.UTF8.GetString(foreign.Body) != """{"error":"SignedOut"}""")
                    return Fail($"{login}'s POST with a body was not refused as signed out");
            // its body is never read: a request whose body never arrives is still answered (SseClient's head deadline)
            try
            {
                using var unread = await SseClient.Open(port, "/api/send", "other@example.com", post: "{");
                if (!unread.Head.StartsWith("HTTP/1.1 403"))
                    return Fail($"a non-owner's unfinished POST: {unread.Head.Split('\r')[0]}");
            }
            catch (OperationCanceledException) { return Fail("a non-owner's body was read before the identity check"); }

            if (await Json("/api/hello") is not { } hello || $"{hello["pc"]}|{hello["dnsName"]}|{hello["user"]}" != $"{Environment.MachineName}|home.tail0000.ts.net|smoke@example.com" ||
                hello["version"] is null || $"{hello["shell"]}" != WebServer.Shell || $"{hello["shell"]}" == $"{hello["version"]}")
                return Fail($"hello: {await Json("/api/hello")}");

            // the owner's two other PCs are asked at once (neither answers before both were asked); another account's never
            var probed = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var bothAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _webProbe = async (dns, _) =>
            {
                probed.Enqueue(dns);
                if (probed.Count == 2)
                    bothAsked.TrySetResult();
                return await Task.WhenAny(bothAsked.Task, Task.Delay(3000)) == bothAsked.Task && dns == "laptop.tail0000.ts.net" ? JsonNode.Parse("""{"pc":"LAPTOP"}""") : null;
            };
            var pcs = string.Join(",", (await Json("/api/pcs"))?.AsArray().Select(n => $"{n?["pc"]}|{n?["dnsName"]}|{n?["online"]}|{n?["self"]}") ?? []);
            if (pcs != $"{Environment.MachineName}|home.tail0000.ts.net|true|true,LAPTOP|laptop.tail0000.ts.net|true|false,desk|desk.tail0000.ts.net|false|false" || probed.Count != 2)
                return Fail($"pcs: {pcs}, probed [{string.Join(",", probed)}]");

            var listed = (await Json("/api/conversations"))?["recents"]?.AsArray().FirstOrDefault(c => c?["id"]?.ToString() == chat.Id);
            if ($"{listed?["title"]}|{listed?["kind"]}|{listed?["busy"]}|{listed?["folder"]}|{listed?["worker"]}" != "Paint a fox|chat|false||Lead")
                return Fail($"conversations: {await Json("/api/conversations")}");

            var one = await Json("/api/conversation/" + chat.Id);
            var messages = one?["messages"]?.AsArray();
            if (one is null || $"{one["title"]}|{one["kind"]}|{one["busy"]}|{one["queued"]}|{one["worker"]}" != "Paint a fox|chat|false|0|Lead" || messages?.Count != 4 ||
                $"{messages[0]?["worker"]}|{messages[1]?["worker"]}|{messages[2]?["worker"]}|{messages[3]?["worker"]}" != "||Lead|" ||
                $"{messages[0]?["role"]}|{messages[0]?["text"]}" != "user|Paint a fox" ||
                $"{messages[1]?["role"]}|{messages[1]?["text"]}|{messages[1]?["stats"]}" != "assistant|Here it is|12 tok/s" || messages[1]?["time"] is null ||
                messages[1]?["images"]?.AsArray().Select(i => i?.ToString()).SequenceEqual([File(picture)]) != true ||
                $"{one["context"]?["nCtx"]}|{one["context"]?["used"]}|{one["context"]?["fraction"]}|{one["context"]?["parts"]?["system"]}" != "1000|250|0.25|100")
                return Fail($"conversation: {one}");
            if ((await Fetch("/api/conversation/none")).Status != 404)
                return Fail("an unknown conversation was not 404");

            if (await Fetch(File(picture)) is not { Status: 200, Type: "image/png" } served || !served.Body.SequenceEqual(png))
                return Fail("the picture's URL did not return its bytes");
            foreach (var path in new[] { @"C:\Windows\win.ini", Path.Combine(images, "..", "secret.png"), Path.Combine(images + "-x", "a.png"),
                Path.Combine(images, "notes.txt"), Path.Combine(images, "none.png"), @"images\smoke fox.png", "" })
                if ((await Fetch(File(path))).Status != 404)
                    return Fail($"{path} was served");

            // over the wire: the security headers everywhere, no-store on /api only, CORS for the owner's PCs only
            foreach (var route in new[] { "/", "/api/hello" })
                if (await Fetch(route) is not { Status: 200 } r || !r.Headers.TryGetValues("Content-Security-Policy", out var csp) || !csp.Single().Contains("default-src 'self'") ||
                    !r.Headers.TryGetValues("X-Frame-Options", out var frame) || frame.Single() != "DENY" ||
                    !r.Headers.TryGetValues("X-Content-Type-Options", out var sniff) || sniff.Single() != "nosniff" || (r.Headers.CacheControl?.NoStore == true) != route.StartsWith("/api/"))
                    return Fail($"{route}: the security or cache headers");
            var cors = await Fetch("/api/hello", origin: "https://laptop.tail0000.ts.net");
            if (!cors.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed) ||
                allowed.Single() != "https://laptop.tail0000.ts.net" || !cors.Headers.Vary.Contains("Origin"))
                return Fail("a tailnet PC's origin was not allowed");
            // the owner's laptop: its preflight is answered with its origin allowed and its POST reaches the API (an unknown route's 404)
            if (await Fetch("/api/hello", origin: "https://laptop.tail0000.ts.net", method: HttpMethod.Options) is not { Status: 204 } preflight ||
                !preflight.Headers.TryGetValues("Access-Control-Allow-Origin", out var preAllowed) || preAllowed.Single() != "https://laptop.tail0000.ts.net" ||
                (await Fetch("/api/none", origin: "https://laptop.tail0000.ts.net", method: HttpMethod.Post)).Status != 404)
                return Fail("the owner's laptop: its preflight or its POST");
            // another account's device on the same tailnet (theirs) is refused like any other site: no allow header, its POST 403
            foreach (var origin in new[] { "https://theirs.tail0000.ts.net", "https://evil.example", "https://a.b.tail0000.ts.net", "http://laptop.tail0000.ts.net",
                "https://laptop.tail0000.ts.net.evil.example" })
                if ((await Fetch("/api/hello", origin: origin)).Headers.Contains("Access-Control-Allow-Origin") ||
                    (await Fetch("/api/hello", origin: origin, method: HttpMethod.Options)).Headers.Contains("Access-Control-Allow-Origin") ||
                    (await Fetch("/api/none", origin: origin, method: HttpMethod.Post)).Status != 403)
                    return Fail($"{origin} was allowed");

            // the home-screen app: the manifest (its type and fields) and each of its icons, the page's links to them; sw.js revalidated
            // on every load (no-cache, so an update reaches the phone; 404 until the service worker exists)
            var manifest = await Fetch("/manifest.webmanifest");
            var app = manifest is { Status: 200, Type: "application/manifest+json" } ? JsonNode.Parse(manifest.Body) : null;
            var icons = app?["icons"]?.AsArray().Select(i => $"{i?["src"]}|{i?["sizes"]}|{i?["type"]}|{i?["purpose"]}").ToList() ?? [];
            if ($"{app?["name"]}|{app?["short_name"]}|{app?["start_url"]}|{app?["scope"]}|{app?["display"]}|{app?["background_color"]}|{app?["theme_color"]}" !=
                "Agent Orchestrator|Orchestrator|/|/|standalone|#1E1E1E|#1E1E1E" ||
                string.Join(",", icons) != "/icons/icon-192.png|192x192|image/png|any,/icons/icon-512.png|512x512|image/png|any,/icons/icon-maskable-512.png|512x512|image/png|maskable")
                return Fail($"the manifest: {manifest.Status} {manifest.Type} {System.Text.Encoding.UTF8.GetString(manifest.Body)}");
            foreach (var (icon, size) in new[] { ("/icons/icon-192.png", 192), ("/icons/icon-512.png", 512), ("/icons/icon-maskable-512.png", 512), ("/icons/apple-touch-icon.png", 180) })
                if (await Fetch(icon) is not { Status: 200, Type: "image/png" } iconFile || iconFile.Body.Length < 24 || !iconFile.Body.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ||
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(iconFile.Body.AsSpan(16)) != size) // IHDR's width
                    return Fail($"{icon} is not a {size} px PNG");
            var page = System.Text.Encoding.UTF8.GetString((await Fetch("/")).Body);
            if (!page.Contains("""<link rel="manifest" href="manifest.webmanifest">""") || !page.Contains("""<meta name="theme-color" content="#1E1E1E">""") ||
                !page.Contains("""<link rel="apple-touch-icon" href="icons/apple-touch-icon.png">"""))
                return Fail("the page's manifest, theme colour or apple-touch-icon");
            var sw = await Fetch("/sw.js");
            if (sw.Status != 200 || !sw.Type.StartsWith("text/javascript") || sw.Headers.CacheControl?.NoCache != true)
                return Fail($"sw.js: {sw.Status} {sw.Type} {sw.Headers.CacheControl}");
            // an upload from another of the owner's PCs names its file in X-File-Name: the preflight allows it
            if (!(await Fetch("/api/upload", origin: "https://laptop.tail0000.ts.net", method: HttpMethod.Options)).Headers.TryGetValues("Access-Control-Allow-Headers", out var allowHeaders) ||
                !allowHeaders.Single().Split(',').Select(x => x.Trim()).Contains("X-File-Name", StringComparer.OrdinalIgnoreCase))
                return Fail("the preflight does not allow X-File-Name");
            return true;
        }
        finally
        {
            _webProbe = probe;
            _store.Delete(chat.Id);
        }
    }
}

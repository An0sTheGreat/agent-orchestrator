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
        _webTimer.Tick += async (_, _) =>
        {
            if (await _webStatus() is { } status && _webApp is not null)
                _webTailnet = status; // a status that failed to read keeps the last one
            RefreshWebApp();
        };
        RefreshWebApp();
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
            if (Tailscale.WebAppProblem(status) is { } why)
            {
                _webFailure = why;
                retry = true; // not ready yet (tailscaled may still be starting); a refusal by tailscale serve is not retried
                return false;
            }
            _webTailnet = status;
            var server = new WebServer(() => _webTailnet?.Login, () => _webTailnet?.MagicDnsSuffix, WebApi);
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
        if (_webApp is null)
            return;
        _webApp.Stop();
        _webApp = null;
        RefreshWebApp();
        var serve = _webServe;
        await Task.Run(() => serve(null, CancellationToken.None));
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
    private async Task<(int Status, object Body)> WebApi(string method, string path, IReadOnlyDictionary<string, string> query, byte[] body, string contentType)
    {
        const string one = "/api/conversation/";
        return (method, path) switch
        {
            ("GET", "/api/hello") => (200, WebHello()),
            ("GET", "/api/pcs") => (200, await WebPcs()),
            ("GET", "/api/conversations") => await Dispatcher.InvokeAsync(WebConversations),
            ("GET", _) when path.StartsWith(one) => await Dispatcher.InvokeAsync(() => WebConversation(path[one.Length..])),
            ("GET", "/api/file") => WebFile(query.GetValueOrDefault("path")),
            _ => WebNotFound,
        };
    }

    private object WebHello() => new
    {
        pc = Environment.MachineName, dnsName = _webTailnet?.DnsName ?? "", user = _webTailnet?.Login ?? "",
        version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "",
    };

    // This PC first, then the owner's other PCs on the tailnet, each asked for its /api/hello at once: one that answers is online.
    // ponytail: the peers are those of the last status read (at start and every 5 minutes); a PC added since shows up at the next.
    private async Task<object> WebPcs()
    {
        var (tailnet, probe) = (_webTailnet, _webProbe);
        var peers = tailnet?.Mine.Where(p => p.DnsName.Length > 0).ToList() ?? [];
        var answers = await Task.WhenAll(peers.Select(p => probe(p.DnsName, CancellationToken.None)));
        return peers.Select((p, i) => new { pc = (answers[i] as JsonObject)?["pc"]?.ToString() ?? p.Name, dnsName = p.DnsName, online = answers[i] is JsonObject, self = false })
            .Prepend(new { pc = Environment.MachineName, dnsName = tailnet?.DnsName ?? "", online = true, self = true }).ToList();
    }

    private (int, object) WebConversations()
    {
        var (view, running) = (_store.View(), RunningId);
        var item = (ConversationStore.Entry e) => new { id = e.Id, title = e.Title, kind = Kind(e.Kind), updated = e.Updated, busy = e.Id == running, folder = e.Folder };
        return (200, new
        {
            projects = view.Groups.Select(g => new { id = g.Id, name = g.Name, items = g.Items.Select(item).ToList() }).ToList(),
            recents = view.Ungrouped.Select(item).ToList(),
            archived = view.Archived.Select(item).ToList(),
        });
    }

    private (int, object) WebConversation(string id)
    {
        if (ConversationNow(id) is not (var saved, var busy, var messages, var queued, var prompt, var reply))
            return (404, new { error = "No such conversation" });
        // the open view's measure while it is shown (a turn updates it before saving), else the saved one
        var context = (id == _chatConv?.Id ? _chatContext : id == _codeConv?.Id ? _codeContext : null) ?? saved.Context;
        return (200, new
        {
            id, title = ConversationStore.TitleOf(saved), kind = Kind(saved.Kind), busy, queued, prompt, reply,
            context = context is { } c ? new { nCtx = c.NCtx, used = c.Used, fraction = c.Fraction, parts = c.Parts } : null,
            messages = messages.Select(m => new
            {
                role = m.Role, text = m.Content, time = m.Time, stats = m.Stats,
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
             "Peer":{"k1":{"UserID":1,"HostName":"laptop","DNSName":"laptop.tail0000.ts.net.","Online":true},
                     "k2":{"UserID":1,"HostName":"desk","DNSName":"desk.tail0000.ts.net.","Online":true},
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
            if (!await WebApiSmoke(bound))
                return false;

            Click(false);
            if (!await Until(() => served.Count == 2) || _webApp is not null || !served.SequenceEqual([bound, null]) || _settings.WebAppOn || AppSettings.Load().WebAppOn ||
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
            await Task.Delay(500);
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

    // The read API over the running server's socket (WebAppSmoke, while on): every route refuses a request without the owner's
    // login; this PC and the owner's peers, probed in parallel (the faked probe); a stored chat with an assistant picture is listed
    // under recents and read back with its picture's URL, which returns the bytes; nothing outside the data folder's images,
    // pasted and screenshots pictures is served; the security headers, no-store on /api only, and CORS for one tailnet label only.
    private async Task<bool> WebApiSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: api: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<(int Status, System.Net.Http.Headers.HttpResponseHeaders Headers, string Type, byte[] Body)> Fetch(string path, string? login = "smoke@example.com", string? origin = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
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
            chat.Messages.AddRange([new("user", "Paint a fox", DateTimeOffset.Now), new("assistant", "Here it is", DateTimeOffset.Now, [picture], "12 tok/s")]);
            chat.Context = new ContextSnapshot { NCtx = 1000, Prompt = 200, Completion = 50, Parts = new() { ["system"] = 100 } };
            _store.Save(chat);

            foreach (var route in new[] { "/api/hello", "/api/pcs", "/api/conversations", "/api/conversation/" + chat.Id, File(picture), "/" })
                if ((await Fetch(route, null)).Status != 403 || (await Fetch(route, "other@example.com")).Status != 403)
                    return Fail($"{route} without the owner's login was not refused");

            if (await Json("/api/hello") is not { } hello || $"{hello["pc"]}|{hello["dnsName"]}|{hello["user"]}" != $"{Environment.MachineName}|home.tail0000.ts.net|smoke@example.com" ||
                hello["version"] is null)
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
            if ($"{listed?["title"]}|{listed?["kind"]}|{listed?["busy"]}|{listed?["folder"]}" != "Paint a fox|chat|false|")
                return Fail($"conversations: {await Json("/api/conversations")}");

            var one = await Json("/api/conversation/" + chat.Id);
            var messages = one?["messages"]?.AsArray();
            if (one is null || $"{one["title"]}|{one["kind"]}|{one["busy"]}|{one["queued"]}" != "Paint a fox|chat|false|0" || messages?.Count != 2 ||
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

            // over the wire: the security headers everywhere, no-store on /api only, CORS for https://<one label>.<tailnet> only
            foreach (var route in new[] { "/", "/api/hello" })
                if (await Fetch(route) is not { Status: 200 } r || !r.Headers.TryGetValues("Content-Security-Policy", out var csp) || !csp.Single().Contains("default-src 'self'") ||
                    !r.Headers.TryGetValues("X-Frame-Options", out var frame) || frame.Single() != "DENY" ||
                    !r.Headers.TryGetValues("X-Content-Type-Options", out var sniff) || sniff.Single() != "nosniff" || (r.Headers.CacheControl?.NoStore == true) != route.StartsWith("/api/"))
                    return Fail($"{route}: the security or cache headers");
            var cors = await Fetch("/api/hello", origin: "https://laptop.tail0000.ts.net");
            if (!cors.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed) ||
                allowed.Single() != "https://laptop.tail0000.ts.net" || !cors.Headers.Vary.Contains("Origin"))
                return Fail("a tailnet PC's origin was not allowed");
            foreach (var origin in new[] { "https://evil.example", "https://a.b.tail0000.ts.net", "http://laptop.tail0000.ts.net", "https://laptop.tail0000.ts.net.evil.example" })
                if ((await Fetch("/api/hello", origin: origin)).Headers.Contains("Access-Control-Allow-Origin"))
                    return Fail($"{origin} was allowed");
            return true;
        }
        finally
        {
            _webProbe = probe;
            _store.Delete(chat.Id);
        }
    }
}

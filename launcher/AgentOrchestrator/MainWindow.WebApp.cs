using System.Net.Http;
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
    internal Func<int?, CancellationToken, Task<bool>> _webServe = ServeOnTailnet;

    internal string? WebAppAddress => _webApp is not null && _webTailnet is { } t ? $"https://{t.DnsName}/" : null; // null while off

    private static async Task<bool> ServeOnTailnet(int? port, CancellationToken ct)
    {
        if (port is { } p)
            return await Tailscale.ServeAsync(p, ct);
        await Tailscale.ServeOffAsync(ct);
        return true;
    }

    private void InitWebApp()
    {
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
        (_webStarting, _webFailure) = (true, null);
        RefreshWebApp();
        try
        {
            var status = await _webStatus();
            if (Tailscale.WebAppProblem(status) is { } why)
            {
                _webFailure = why;
                return false;
            }
            _webTailnet = status;
            var server = new WebServer(() => _webTailnet?.Login, () => _webTailnet?.MagicDnsSuffix, WebApi);
            if (!server.Start(_settings.WebAppPort))
            {
                _webFailure = $"no free port from {_settings.WebAppPort}";
                return false;
            }
            if (!await _webServe(server.Port, CancellationToken.None))
            {
                server.Stop();
                _webFailure = "tailscale serve did not publish it";
                return false;
            }
            _webApp = server;
            _webTimer.Start();
            Log($"Phone app: {WebAppAddress} serves http://127.0.0.1:{server.Port}/");
            return true;
        }
        finally
        {
            if (_webFailure is not null)
                Log($"Phone app: {_webFailure}");
            _webStarting = false;
            RefreshWebApp();
        }
    }

    // Idempotent. Serve goes off on a pool thread, so Quit can wait for it on the UI thread.
    // A start still in flight finishes first, so Serve's route is never left behind by a Quit mid-start.
    internal async Task StopWebAppAsync()
    {
        if (_webStarting && _webStart is { } start)
            await start;
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

    // No routes yet: the API comes with the app's pages.
    private Task<(int Status, object Body)> WebApi(string method, string path, IReadOnlyDictionary<string, string> query, byte[] body, string contentType) =>
        Task.FromResult<(int, object)>((404, new { error = "Not found" }));

    private async void WebAppToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.WebAppOn = WebAppToggle.IsChecked == true;
        _settings.Save();
        _webFailure = null;
        if (_settings.WebAppOn)
            await StartWebAppAsync();
        else
            await StopWebAppAsync();
        RefreshWebApp();
    }

    private void WebAppCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard(WebAppAddressBox.Text, "Address copied", "No address yet");

    private void RefreshWebApp()
    {
        (WebAppToggle.IsChecked, WebAppToggle.IsEnabled) = (_settings.WebAppOn, !_webStarting);
        WebAppStateText.Text = _webStarting ? "Starting…" : _webApp is not null ? "On" : _webFailure is { } why ? "Not running: " + why : "Off";
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
             "CurrentTailnet":{"MagicDNSSuffix":"tail0000.ts.net"},"User":{"1":{"LoginName":"smoke@example.com"}}%CERTS%}
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
            _webServe = (p, _) => { served.Enqueue(p); return Task.FromResult(true); };
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
            _webServe = async (p, _) => { calls.Enqueue(p); if (p is not null) await gate.Task; return true; };
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
            _webServe = (p, _) => { served.Enqueue(p); return Task.FromResult(true); };
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
}

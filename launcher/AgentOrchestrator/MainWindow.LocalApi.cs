using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;

namespace AgentOrchestrator;

// Settings > Models > Local API for my apps (LocalApi.cs): an OpenAI-compatible server on 127.0.0.1:AppSettings.LocalApiPort for
// this PC's own programs, behind a bearer key (ApiKeys under LocalApi.KeyName: read from the store, never left in the environment).
public partial class MainWindow
{
    private LocalApi.Server? _localApi;
    private string _localApiState = "Off";

    internal string? LocalApiKey() => ApiKeys.Get(LocalApi.KeyName);

    private void InitLocalApi()
    {
        if (_settings.LocalApi)
            StartLocalApi();
        else
            RefreshLocalApi();
    }

    private void LocalApiToggle_Click(object sender, RoutedEventArgs e)
    {
        if (LocalApiToggle.IsChecked == true)
            StartLocalApi();
        else
        {
            StopLocalApi();
            _settings.LocalApi = false;
            _settings.Save();
            RefreshLocalApi();
        }
    }

    // The switch stays on when the port is taken (the state says why), so the next start tries again.
    private void StartLocalApi()
    {
        StopLocalApi();
        if (LocalApiKey() is not { Length: > 0 })
            NewLocalApiKey();
        var port = _settings.LocalApiPort is > 0 and < 65536 ? _settings.LocalApiPort : LocalApi.DefaultPort;
        var server = new LocalApi.Server(port, LocalApiKey, LocalApiRoute);
        if (server.Start())
        {
            _localApi = server;
            _localApiState = $"Listening on http://127.0.0.1:{port}/v1";
        }
        else
            _localApiState = $"Port {port} is in use by another program";
        _settings.LocalApi = true;
        _settings.Save();
        RefreshLocalApi();
    }

    // Idempotent.
    private void StopLocalApi()
    {
        _localApi?.Dispose();
        _localApi = null;
        _localApiState = "Off";
    }

    private void RefreshLocalApi()
    {
        LocalApiToggle.IsChecked = _settings.LocalApi;
        LocalApiStateText.Text = _localApiState;
        LocalApiKeyBox.Text = _localApi is not null ? LocalApiKey() ?? "" : "";
    }

    // ApiKeys.Save also sets the variable; this key stays out of the environment (the workers' processes inherit it).
    private static void NewLocalApiKey()
    {
        ApiKeys.Save(LocalApi.KeyName, "Default", ModelShare.NewKey());
        Environment.SetEnvironmentVariable(LocalApi.KeyName, null);
    }

    private void LocalApiCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard(LocalApiKeyBox.Text, "API key copied", "No API key yet");

    // The key is read on every request: the old one stops working at once.
    private void LocalApiNewKey_Click(object sender, RoutedEventArgs e)
    {
        NewLocalApiKey();
        RefreshLocalApi();
        Toast("New key: your apps need the new one");
    }

    private async Task LocalApiRoute(HttpListenerContext ctx)
    {
        var (method, path) = (ctx.Request.HttpMethod, ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "");
        if (method == "GET" && path == "/v1/models")
        {
            var data = await Dispatcher.InvokeAsync(() =>
            {
                ScanImageFolder();
                return _cards.Select(c => new { id = c.Role, @object = "model", owned_by = "agent-orchestrator", name = c.Title, kind = "chat" })
                    .Concat(ImageShare.Models(_imgLocal).Select(m => new { id = m.Id, @object = "model", owned_by = "agent-orchestrator", name = m.Name, kind = "image" }))
                    .ToList();
            });
            await LocalApiReply(ctx, 200, JsonSerializer.SerializeToUtf8Bytes(new { @object = "list", data }));
            return;
        }
        await LocalApiReply(ctx, 404, LocalApi.Error("Unknown route"));
    }

    private static async Task LocalApiReply(HttpListenerContext ctx, int status, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
    }

    // --webapp-smoke: the switch starts and stops the server on a free port with its key; /v1/models needs the key; New key replaces it;
    // a port in use is reported; the key never reaches the environment.
    internal async Task<bool> LocalApiSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: local api: {what}"); return false; }
        void Click(bool on)
        {
            LocalApiToggle.IsChecked = on;
            LocalApiToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        static int FreePort()
        {
            using var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        var (on, port) = (_settings.LocalApi, _settings.LocalApiPort);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        async Task<(int, string)> Get(int at, string? key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{at}/v1/models");
            if (key is not null)
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var response = await http.SendAsync(request);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        HttpListener? blocker = null;
        try
        {
            var p = _settings.LocalApiPort = FreePort();
            Click(true);
            var key = LocalApiKeyBox.Text;
            if (_localApi is null || LocalApiStateText.Text != $"Listening on http://127.0.0.1:{p}/v1" || key.Length != 43 || !AppSettings.Load().LocalApi ||
                Environment.GetEnvironmentVariable(LocalApi.KeyName) is not null || key != LocalApiKey())
                return Fail($"on: running {_localApi is not null}, state {LocalApiStateText.Text}, key length {key.Length}, env {Environment.GetEnvironmentVariable(LocalApi.KeyName) is not null}");
            if ((await Get(p, null)).Item1 != 401 || (await Get(p, "wrong")).Item1 != 401)
                return Fail("/v1/models without the key");
            var (status, body) = await Get(p, key);
            if (status != 200)
                return Fail($"/v1/models with the key: {status}");
            using (var doc = JsonDocument.Parse(body))
            {
                var chat = doc.RootElement.GetProperty("data").EnumerateArray().Where(m => m.GetProperty("kind").GetString() == "chat").Select(m => m.GetProperty("id").GetString()).ToList();
                if (!_cards.Select(c => c.Role).All(chat.Contains) || chat.Count != _cards.Count)
                    return Fail($"the workers listed: {string.Join(",", chat)}");
            }
            LocalApiNewKey_Click(this, new RoutedEventArgs());
            var fresh = LocalApiKeyBox.Text;
            if (fresh == key || fresh.Length != 43 || (await Get(p, key)).Item1 != 401 || (await Get(p, fresh)).Item1 != 200)
                return Fail("New key");

            Click(false);
            if (_localApi is not null || LocalApiStateText.Text != "Off" || AppSettings.Load().LocalApi)
                return Fail($"off: {LocalApiStateText.Text}");
            try { await Get(p, fresh); return Fail("still answering when off"); } catch (HttpRequestException) { }

            var taken = _settings.LocalApiPort = FreePort();
            blocker = new HttpListener();
            blocker.Prefixes.Add($"http://127.0.0.1:{taken}/");
            blocker.Start();
            Click(true);
            if (_localApi is not null || LocalApiStateText.Text != $"Port {taken} is in use by another program")
                return Fail($"port in use: {LocalApiStateText.Text}");
            Click(false);
            if (LocalApiStateText.Text != "Off")
                return Fail($"off after in use: {LocalApiStateText.Text}");
            return true;
        }
        finally
        {
            blocker?.Close();
            StopLocalApi();
            (_settings.LocalApi, _settings.LocalApiPort) = (on, port);
            _settings.Save();
            RefreshLocalApi();
        }
    }
}

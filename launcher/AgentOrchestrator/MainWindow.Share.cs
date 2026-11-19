using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrchestrator;

// Settings > Models > Share models with my other PCs (ModelShare.cs, docs/superpowers/specs/2026-11-19-share-models-design.md).
// Primary PC: the switch starts llama-server in router mode on 127.0.0.1:ModelShare.ChatPort (the share key from ApiKeys
// under ModelShare.OwnKeyEnv), with <data>\share\models.ini written from the local chat GGUFs on each start, and the ImageShare
// server on ModelShare.ImagePort (which proxies /v1/ to llama-server). On the Tailscale network (AppSettings.ShareNetwork,
// ModelShare.Network) ImageShare binds this PC's Tailscale IPv4 and the code carries that fixed address; on the Cloudflare one
// it binds 127.0.0.1 behind one Cloudflare quick tunnel (asking to download cloudflared first, as Remote access does).
// llama-server restarts after a backoff (5 s doubling to 1 min) when it exits while on, the tunnel likewise (RemoteTunnel);
// all stop by pid when the switch goes off or the app closes. Other PC: Connect checks a connection code against
// ModelShare.ChatUrl's /models and keeps its host
// (AppSettings.ShareHost) and key (ApiKeys, ModelShare.KeyEnv); the Remote API page then lists "Primary PC".
public partial class MainWindow
{
    private Process? _shareProcess;
    private ImageShare.Server? _imageShare; // the images half, on ModelShare.ImagePort; it keeps running across llama-server restarts
    private int _shareGeneration, _shareFailures; // a new generation for every launch and stop: stale exits and restarts are ignored
    private readonly RemoteTunnel _shareTunnel = new(RemoteTunnel.DefaultExe, "cloudflared-share.pid");
    private string _shareState = "Off"; // off: why; on: llama-server's state ("" = running)
    private IPAddress? _shareIp; // sharing on Tailscale: the address ImageShare binds; null = on the Cloudflare tunnel
    private const string ShareLabel = "Primary PC"; // the name of the key Connect saves under ModelShare.KeyEnv

    private void InitShare()
    {
        _shareTunnel.Changed += () => Dispatcher.InvokeAsync(RefreshShare);
        RemoteCatalog.ShareHost = _settings.ShareHost;
        ShareNote(_settings.ShareHost.Length > 0 ? $"Connected to {_settings.ShareHost}" : "");
        if (_settings.ShareModels)
            StartShare();
        else
            RefreshShare();
    }

    private async void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ShareToggle.IsChecked != true)
        {
            StopShare();
            _shareState = "Off";
            SaveShare(false);
            return;
        }
        await TurnOnShare();
    }

    // The network the next start uses: the user's pick, else Tailscale when this PC has a Tailscale address now.
    private string ShareNetworkNow() => ModelShare.Network(_settings.ShareNetwork);

    // The Cloudflare network asks to download cloudflared first; Tailscale needs nothing more.
    private async Task TurnOnShare()
    {
        if (ShareNetworkNow() == ModelShare.Cloudflare)
        {
            _shareState = "Off: sharing needs cloudflared";
            if (!File.Exists(_shareTunnel.Exe) && !await DownloadCloudflared(_shareTunnel.Exe,
                    percent => { (_shareState, ShareToggle.IsEnabled) = (percent is { } p ? $"Downloading cloudflared {p}%" : _shareState, percent is null); RefreshShare(); },
                    failure => { _shareState = "Failed: " + failure; Log($"Share models: {failure}"); }))
            {
                SaveShare(false);
                return;
            }
        }
        StartShare();
    }

    // The user's pick is kept; while sharing, a different network restarts it.
    private async void ShareNetwork_Click(object sender, RoutedEventArgs e)
    {
        var picked = (string)((FrameworkElement)sender).Tag;
        var running = _shareIp is null ? ModelShare.Cloudflare : ModelShare.Tailscale;
        _settings.ShareNetwork = picked;
        _settings.Save();
        if (!_settings.ShareModels || running == picked)
        {
            RefreshShare();
            return;
        }
        StopShare();
        await TurnOnShare();
    }

    // At startup a missing cloudflared shows the tunnel Failed instead of asking.
    private void StartShare()
    {
        if (!File.Exists(LauncherLogic.LlamaServerPath))
        {
            _shareState = "llama-server is not installed: download it under Model runtime";
            SaveShare(false);
            return;
        }
        _shareIp = null;
        if (ShareNetworkNow() == ModelShare.Tailscale && (_shareIp = ModelShare.TailscaleIp()) is null)
        {
            _shareState = "Tailscale is not connected: sign in to Tailscale, or choose Cloudflare";
            SaveShare(false);
            return;
        }
        if (!ApiKeys.Has(ModelShare.OwnKeyEnv))
            ApiKeys.Save(ModelShare.OwnKeyEnv, "Default", ModelShare.NewKey());
        _shareFailures = 0;
        SaveShare(true);
        LaunchShare(++_shareGeneration);
    }

    private async void LaunchShare(int generation)
    {
        _shareState = "Starting llama-server…";
        RefreshShare();
        var models = await Task.Run(() => ModelShare.LocalModels(_settings.ModelsFolder, LauncherLogic.HubCache()));
        if (generation != _shareGeneration)
            return;
        var info = new ProcessStartInfo(LauncherLogic.LlamaServerPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--host", "127.0.0.1", "--port", $"{ModelShare.ChatPort}", "--api-key", Environment.GetEnvironmentVariable(ModelShare.OwnKeyEnv) ?? "",
                     "--models-preset", ModelShare.IniPath, "--models-max", "1" })
            info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            Directory.CreateDirectory(ModelShare.Folder);
            File.WriteAllText(ModelShare.IniPath, ModelShare.Ini(models));
            StartImageShare(_shareIp ?? IPAddress.Loopback, ModelShare.ImagePort);
            process.Start();
            if (_shareIp is null)
                _shareTunnel.Start(ModelShare.ImagePort); // idempotent: it keeps running (and its address) across llama-server restarts
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or SocketException)
        {
            process.Dispose();
            StopShare();
            _shareState = "Failed: " + x.Message;
            SaveShare(false);
            return;
        }
        var started = DateTime.Now;
        _shareProcess = process;
        Log($"Share models: llama-server (pid {process.Id}) on 127.0.0.1:{ModelShare.ChatPort}, {models.Count} models, " +
            (_shareIp is { } ip ? $"on Tailscale at {ip}:{ModelShare.ImagePort}" : "through a Cloudflare tunnel"));
        _shareState = "";
        RefreshShare();
        process.Exited += (_, _) => Dispatcher.InvokeAsync(async () =>
        {
            if (generation != _shareGeneration)
                return;
            var code = process.ExitCode;
            (_shareProcess, _shareFailures) = (null, DateTime.Now - started > TimeSpan.FromMinutes(1) ? 0 : _shareFailures);
            var wait = RemoteTunnel.Backoff(_shareFailures++);
            var next = ++_shareGeneration;
            _shareState = $"llama-server exited with code {code} (restarting in {wait.TotalSeconds:0} s)";
            RefreshShare();
            await Task.Delay(wait);
            if (next == _shareGeneration && _settings.ShareModels)
                LaunchShare(next);
        });
    }

    // Idempotent: the tunnel and the server stop by their own pids, and any pending restart is dropped.
    private void StopShare()
    {
        _shareTunnel.Stop();
        _imageShare?.Dispose();
        _imageShare = null;
        _shareGeneration++;
        if (_shareProcess is not { } process)
            return;
        _shareProcess = null;
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (Exception x) when (x is InvalidOperationException or Win32Exception) { } // already gone
    }

    // Idempotent; the key is read on every request, so New key signs other PCs out at once. Also --image-share-loopback (App.xaml.cs).
    internal void StartImageShare(IPAddress ip, int port) =>
        _imageShare ??= new ImageShare.Server(ip, port, ModelShare.ChatPort, () => Environment.GetEnvironmentVariable(ModelShare.OwnKeyEnv) ?? "", ServeImageShareAsync)
            { Progress = () => _shareProgress };

    // On the server's thread: the model list scans the folder here; a generation runs on the UI thread like the page's own.
    private async Task<ImageShare.Response> ServeImageShareAsync(ImageShare.Request request, CancellationToken ct)
    {
        if (request.Path == ImageShare.UnloadPath)
        {
            await UnloadSharedChatModelsAsync(ct);
            return new(200, "{}");
        }
        if (request.Path == ImageShare.ModelsPath)
            return new(200, ImageShare.ModelsJson(ImageShare.Models(ImageGen.ScanFolder(await Dispatcher.InvokeAsync(() => ImageModelsFolder)).Models)));
        ImageSettings? asked;
        try { asked = JsonSerializer.Deserialize<ImageSettings>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { asked = null; }
        return asked is null ? ImageShare.Error(400, "The body is not image settings JSON") : await await Dispatcher.InvokeAsync(() => GenerateForShareAsync(asked, ct));
    }

    // The shared llama-server's loaded chat models, unloaded (its router's POST /models/unload); best effort: a failure leaves
    // them and the generation's own Low VRAM retry and memory message follow.
    private static async Task UnloadSharedChatModelsAsync(CancellationToken ct)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ModelShare.ChatPort}"), Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", Environment.GetEnvironmentVariable(ModelShare.OwnKeyEnv) ?? "");
            using var list = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("/models", ct));
            foreach (var model in list.RootElement.GetProperty("data").EnumerateArray())
                if (model.TryGetProperty("status", out var status) && status.TryGetProperty("value", out var value) && value.GetString() is "loaded" or "loading")
                    (await http.PostAsync("/models/unload", System.Net.Http.Json.JsonContent.Create(new { model = model.GetProperty("id").GetString() }), ct)).Dispose();
        }
        catch (Exception x) when (x is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException) { }
    }

    // The generation for another PC: its latest {"progress","status"} line (ImageShare.Server.Progress reads it off the UI thread).
    private volatile string? _shareProgress;

    // One generation for the other PC through the Images page's job (409 while one runs), in a temp folder that is deleted after.
    private async Task<ImageShare.Response> GenerateForShareAsync(ImageSettings asked, CancellationToken ct)
    {
        ScanImageFolder();
        if (ImagesView.Children.Count == 0)
            BuildImagesPage();
        if (ImageBusy)
            return ImageShare.Error(409, "Busy generating another picture");
        if (ImageShare.Resolve(asked.RecipeId, _imgLocal) is not var (recipe, local))
            return ImageShare.Error(400, $"No such model: {asked.RecipeId}");
        var run = ImageShare.Run(asked, recipe, local);
        if (run.Prompt.Length == 0)
            return ImageShare.Error(400, "The prompt is empty");
        var folder = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-share-{Guid.NewGuid():N}");
        ImageShare.Response? reply = null;
        try
        {
            await RunImageJob(async jobCt =>
            {
                using var stop = ct.Register(CancelImageGen); // sharing turned off meanwhile
                _shareProgress = null;
                var fraction = 0.0;
                _imgStatusTap = (text, progress) => _shareProgress = ImageShare.ProgressJson(fraction = progress ?? fraction, text);
                ImageStatus("Generating for another PC…", indeterminate: true);
                await UnloadSharedChatModelsAsync(jobCt); // the GPU for the picture: the next chat request loads its model again
                var pictures = new List<ImageShare.Picture>();
                foreach (var (written, _, seed) in (await GenerateLocalAsync(run, folder, null, jobCt)).Where(f => File.Exists(f.Written)))
                    pictures.Add(new(Convert.ToBase64String(await File.ReadAllBytesAsync(written, jobCt)), seed));
                reply = new(200, ImageShare.PicturesJson(pictures));
                ImageStatus($"Done for another PC · seed {run.Seed}", 1);
            });
        }
        finally
        {
            _imgStatusTap = null;
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        }
        return reply ?? ImageShare.Error(500, _imgStatus.Text); // RunImageJob showed the failure: "Failed: …" or "Cancelled"
    }

    private void SaveShare(bool on)
    {
        _settings.ShareModels = on;
        _settings.Save();
        RefreshShare();
    }

    private void RefreshShare()
    {
        ShareToggle.IsChecked = _settings.ShareModels || !ShareToggle.IsEnabled; // on while cloudflared downloads
        ShareNetworkPanel.IsEnabled = ShareToggle.IsEnabled;
        var onTailscale = _settings.ShareModels ? _shareIp is not null : ShareNetworkNow() == ModelShare.Tailscale;
        (ShareTailscaleRadio.IsChecked, ShareCloudflareRadio.IsChecked) = (onTailscale, !onTailscale);
        var tailscale = _settings.ShareModels && _shareIp is { } at && _imageShare is not null ? at.ToString() : null;
        var tunnel = tailscale is not null
            ? $"Online on Tailscale at {tailscale}, a fixed address: the connection code stays the same when sharing restarts. " +
              "The first time, Windows may ask to allow \"Agent Orchestrator\" through the firewall: allow it"
            : _shareIp is not null ? "" : _shareTunnel.State switch
        {
            RemoteTunnel.Status.Starting => "Tunnel starting…",
            RemoteTunnel.Status.Online => "Online. The address changes each time sharing starts: then paste the new connection code on your other PCs",
            RemoteTunnel.Status.Failed => "Tunnel failed: " + _shareTunnel.Reason,
            _ => "",
        };
        ShareStateText.Text = _settings.ShareModels ? string.Join(" · ", new[] { tunnel, _shareState }.Where(t => t.Length > 0)) : _shareState;
        var key = Environment.GetEnvironmentVariable(ModelShare.OwnKeyEnv);
        var host = !_settings.ShareModels || key is not { Length: > 0 } ? null
            : tailscale ?? (_shareIp is null && _shareTunnel is { State: RemoteTunnel.Status.Online, Url: { } url } ? new Uri(url).Host : null);
        ShareCodePanel.Visibility = host is null ? Visibility.Collapsed : Visibility.Visible;
        ShareCodeBox.Text = host is null ? "" : ModelShare.Code(host, key!);
    }

    private void ShareCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard(ShareCodeBox.Text, "Connection code copied", "No connection code yet");

    // A new key signs the other PCs out: the server restarts with it.
    private void ShareNewKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeys.Save(ModelShare.OwnKeyEnv, "Default", ModelShare.NewKey());
        if (_settings.ShareModels)
        {
            StopShare();
            _shareFailures = 0;
            LaunchShare(++_shareGeneration);
        }
        RefreshShare();
        Toast("New key: other PCs need the new connection code");
    }

    private void ShareNote(string text, bool error = false)
    {
        ShareConnectState.Text = text;
        if (error)
            ShareConnectState.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A));
        else
            ShareConnectState.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }

    private async void ShareConnect_Click(object sender, RoutedEventArgs e)
    {
        if (ModelShare.ParseCode(ShareCodeInput.Text) is not var (host, key))
        {
            ShareNote("That is not a connection code: copy it from Settings > Models on the primary PC (it starts with ao-share://).", error: true);
            return;
        }
        ShareConnectButton.IsEnabled = false;
        ShareNote($"Connecting to {host}…");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var models = await RemoteCatalog.FetchModelsAsync(http, ModelShare.ChatUrl(host), key, CancellationToken.None);
            ApiKeys.Save(ModelShare.KeyEnv, ShareLabel, key);
            _settings.ShareHost = RemoteCatalog.ShareHost = host;
            _settings.Save();
            ShareCodeInput.Clear();
            ShareNote($"Connected to {host}: {models.Count} models");
        }
        catch (Exception x) when (x is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            ShareNote($"Could not connect to {host}: " + (x is TaskCanceledException
                ? "no answer in 10 s. Is the primary PC on and sharing its models? " + (ModelShare.IsTailscaleHost(host)
                    ? "Are both PCs signed in to Tailscale, and is Agent Orchestrator allowed through the primary PC's firewall?"
                    : "Its address changes each time sharing starts.") : x.Message), error: true);
        }
        finally
        {
            ShareConnectButton.IsEnabled = true;
        }
    }

    private void ShareDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (ApiKeys.Labels(ModelShare.KeyEnv).Contains(ShareLabel))
            ApiKeys.Remove(ModelShare.KeyEnv, ShareLabel);
        _settings.ShareHost = RemoteCatalog.ShareHost = "";
        _settings.Save();
        ShareNote("Not connected");
    }

    // --ui-smoke: the section is on the Settings Models page, and a pasted bad code shows the error and connects nothing.
    private bool ShareSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: share models: {what}"); return false; }
        if (SettingsPageOf(ShareToggle) is null)
            return Fail("the Share section is not in Settings > Models");
        if (SettingsPageOf(ShareTailscaleRadio) is null || SettingsPageOf(ShareCloudflareRadio) is null || ShareTailscaleRadio.IsChecked == ShareCloudflareRadio.IsChecked)
            return Fail($"the network choice: Tailscale {ShareTailscaleRadio.IsChecked}, Cloudflare {ShareCloudflareRadio.IsChecked}");
        var host = _settings.ShareHost;
        ShareCodeInput.Text = "ao-share://x.trycloudflare.com.evil.com?key=nope";
        ShareConnectButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (!ShareConnectState.Text.StartsWith("That is not a connection code") || _settings.ShareHost != host || ApiKeys.Has(ModelShare.KeyEnv))
            return Fail($"a bad code: {ShareConnectState.Text}");
        ShareCodeInput.Clear();
        ShareNote("");
        // the Images page offers the primary PC only while connected
        if (ImagesView.Children.Count == 0)
            BuildImagesPage();
        var useShared = SmokeFind<Button>(ImagesView, b => b.Tag as string == "ImagesUseShared");
        _settings.ShareHost = "smoke.trycloudflare.com";
        ShowImageSource();
        var connected = useShared?.Visibility;
        _settings.ShareHost = "";
        ShowImageSource();
        _settings.ShareHost = host;
        if (connected != Visibility.Visible || useShared!.Visibility != Visibility.Collapsed)
            return Fail($"\"Use the primary PC…\" is {connected} connected and {useShared?.Visibility} not");
        ShowImageSource();
        return true;
    }

    private static StackPanel? SettingsPageOf(DependencyObject element)
    {
        for (var at = element; at is not null; at = VisualTreeHelper.GetParent(at) ?? LogicalTreeHelper.GetParent(at))
            if (at is StackPanel { Tag: "Models" } panel)
                return panel;
        return null;
    }
}

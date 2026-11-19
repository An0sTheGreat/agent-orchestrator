using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrchestrator;

// Settings > Models > Share models with my other PCs (ModelShare.cs, docs/superpowers/specs/2026-11-19-share-models-design.md).
// Primary PC: the switch starts llama-server in router mode on this PC's Tailscale address (ModelShare.ChatPort, the share
// key from ApiKeys under ModelShare.OwnKeyEnv), with <data>\share\models.ini written from the local chat GGUFs on each
// start; it restarts after a backoff (5 s doubling to 1 min) when it exits while on, and stops by pid when the switch goes
// off or the app closes. Other PC: Connect checks a connection code against the primary PC's /v1/models and keeps its
// address (AppSettings.ShareHost) and key (ApiKeys, ModelShare.KeyEnv); the Remote API page then lists "Primary PC".
public partial class MainWindow
{
    private Process? _shareProcess;
    private int _shareGeneration, _shareFailures; // a new generation for every launch and stop: stale exits and restarts are ignored
    private string? _shareIp;
    private string _shareState = "Off";
    private const string ShareLabel = "Primary PC"; // the name of the key Connect saves under ModelShare.KeyEnv

    private void InitShare()
    {
        RemoteCatalog.ShareHost = _settings.ShareHost;
        ShareNote(_settings.ShareHost.Length > 0 ? $"Connected to {_settings.ShareHost}" : "");
        if (_settings.ShareModels)
            StartShare();
        else
            RefreshShare();
    }

    private void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ShareToggle.IsChecked == true)
            StartShare();
        else
        {
            StopShare();
            _shareState = "Off";
            SaveShare(false);
        }
    }

    private void StartShare()
    {
        if (ModelShare.TailscaleIp() is not { } ip)
        {
            _shareState = "Tailscale is not running: install it and sign in";
            SaveShare(false);
            return;
        }
        if (!File.Exists(LauncherLogic.LlamaServerPath))
        {
            _shareState = "llama-server is not installed: download it under Model runtime";
            SaveShare(false);
            return;
        }
        if (!ApiKeys.Has(ModelShare.OwnKeyEnv))
            ApiKeys.Save(ModelShare.OwnKeyEnv, "Default", ModelShare.NewKey());
        (_shareIp, _shareFailures) = (ip.ToString(), 0);
        SaveShare(true);
        LaunchShare(++_shareGeneration);
    }

    private async void LaunchShare(int generation)
    {
        _shareState = "Starting…";
        RefreshShare();
        var models = await Task.Run(() => ModelShare.LocalModels(_settings.ModelsFolder, LauncherLogic.HubCache()));
        if (generation != _shareGeneration)
            return;
        var info = new ProcessStartInfo(LauncherLogic.LlamaServerPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--host", _shareIp!, "--port", $"{ModelShare.ChatPort}", "--api-key", Environment.GetEnvironmentVariable(ModelShare.OwnKeyEnv) ?? "",
                     "--models-preset", ModelShare.IniPath, "--models-max", "1" })
            info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            Directory.CreateDirectory(ModelShare.Folder);
            File.WriteAllText(ModelShare.IniPath, ModelShare.Ini(models));
            process.Start();
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            StopShare();
            _shareState = "Failed: " + x.Message;
            SaveShare(false);
            return;
        }
        var started = DateTime.Now;
        _shareProcess = process;
        Log($"Share models: llama-server (pid {process.Id}) on {_shareIp}:{ModelShare.ChatPort}, {models.Count} models");
        _shareState = $"Sharing on {_shareIp} · chat port {ModelShare.ChatPort} · Windows may ask to allow llama-server through the firewall the first time";
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

    // Idempotent: the server stops by its own pid, and any pending restart is dropped.
    private void StopShare()
    {
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

    private void SaveShare(bool on)
    {
        _settings.ShareModels = on;
        _settings.Save();
        RefreshShare();
    }

    private void RefreshShare()
    {
        ShareToggle.IsChecked = _settings.ShareModels;
        ShareStateText.Text = _shareState;
        var key = Environment.GetEnvironmentVariable(ModelShare.OwnKeyEnv);
        var on = _settings.ShareModels && _shareIp is not null && key is { Length: > 0 };
        ShareCodePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ShareCodeBox.Text = on ? ModelShare.Code(_shareIp!, key!) : "";
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
        if (ModelShare.ParseCode(ShareCodeInput.Text) is not var (ip, key))
        {
            ShareNote("That is not a connection code: copy it from Settings > Models on the primary PC (it starts with ao-share://).", error: true);
            return;
        }
        ShareConnectButton.IsEnabled = false;
        ShareNote($"Connecting to {ip}…");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var models = await RemoteCatalog.FetchModelsAsync(http, $"http://{ip}:{ModelShare.ChatPort}/v1", key, CancellationToken.None);
            ApiKeys.Save(ModelShare.KeyEnv, ShareLabel, key);
            _settings.ShareHost = RemoteCatalog.ShareHost = ip;
            _settings.Save();
            ShareCodeInput.Clear();
            ShareNote($"Connected to {ip}: {models.Count} models");
        }
        catch (Exception x) when (x is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            ShareNote($"Could not connect to {ip}: " + (x is TaskCanceledException
                ? "no answer in 10 s. Is the primary PC on, sharing its models, and signed in to Tailscale?" : x.Message), error: true);
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
        var host = _settings.ShareHost;
        ShareCodeInput.Text = "http://100.64.0.1?key=nope";
        ShareConnectButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (!ShareConnectState.Text.StartsWith("That is not a connection code") || _settings.ShareHost != host || ApiKeys.Has(ModelShare.KeyEnv))
            return Fail($"a bad code: {ShareConnectState.Text}");
        ShareCodeInput.Clear();
        ShareNote("");
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

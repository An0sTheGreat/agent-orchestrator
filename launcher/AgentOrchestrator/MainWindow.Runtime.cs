using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AgentOrchestrator;

// Settings > System > Model runtime: llama.cpp's llama-server (downloaded into <data>\llama.cpp), which the
// engine starts itself and records in LauncherLogic.LlamaStateFile; the launcher stops it where it unloads chat models.
public partial class MainWindow
{
    private CancellationTokenSource? _llamaDownload; // the download in progress; cancelled when the app closes
    private string _llamaStateFile = LauncherLogic.LlamaStateFile; // --ui-smoke points it at its own file

    private void InitRuntime()
    {
        try { LauncherLogic.CleanLlamaLeftovers(LauncherLogic.LlamaFolder); } // an install that died half-way
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        ShowModelsFolder();
        RefreshLlamaStatus();
        InitPi();
        ShowWorkflowMode();
        InitSandbox();
        AppVersionText.Text = LauncherLogic.VersionText();
    }

    // Why a run can't start yet: llama-server has not been downloaded, or is being updated; null when it can.
    private string? RuntimeRefusal => _llamaDownload is not null ? "llama-server is being updated"
        : File.Exists(LauncherLogic.LlamaServerPath) ? null : "Download llama-server in Settings first";

    // False, with a toast, when RuntimeRefusal has a reason.
    private bool RuntimeReady()
    {
        if (RuntimeRefusal is not { } reason)
            return true;
        Toast(reason, "#F7C873");
        return false;
    }

    private void ApplyRuntime(ProcessStartInfo info)
    {
        LauncherLogic.ApplyRuntimeEnv(info, LauncherLogic.LlamaServerPath, _settings.ModelsFolder);
        LauncherLogic.ApplyPiEnv(info, LauncherLogic.PiCli);
        info.Environment["AGENT_ORCHESTRATOR_PROMPTS"] = Path.Combine(AppSettings.DataRoot, "prompts"); // the edited prompts (orchestrator.prompt_text)
        if (_settings.Ponytail is "lite" or "full" or "ultra") // orchestrator.ponytail_block
            info.Environment["AGENT_ORCHESTRATOR_PONYTAIL"] = _settings.Ponytail;
        else
            info.Environment.Remove("AGENT_ORCHESTRATOR_PONYTAIL");
        info.Environment["AGENT_ORCHESTRATOR_MULTI_MODEL"] = _settings.MultiModel;
    }

    // Settings > Models > Several models at once shows the saved choice; a click saves it (the engine reads it at its start).
    private void RefreshMultiModel()
    {
        foreach (var radio in MultiModelOptions.Children.OfType<RadioButton>())
            radio.IsChecked = (string)radio.Tag == _settings.MultiModel;
    }

    private void MultiModel_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string mode } || mode == _settings.MultiModel)
            return;
        _settings.MultiModel = mode;
        _settings.Save();
    }

    // Kills every server the engine recorded (only pids that are still a llama-server).
    private Action? _stopServerStub; // --ui-smoke (CallSmoke): counts the stops instead
    private void StopLocalServer()
    {
        if (_stopServerStub is { } stub)
        {
            stub();
            return;
        }
        _llamaPorts.Clear();
        if (LauncherLogic.StopLlamaServer(_llamaStateFile))
            Log("Stopped the local llama-server.");
    }

    private void RefreshLlamaStatus()
    {
        var installed = File.Exists(LauncherLogic.LlamaServerPath);
        string[] version;
        try { version = File.ReadAllLines(Path.Combine(LauncherLogic.LlamaFolder, "version.txt")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { version = []; }
        LlamaStatus.Text = !installed ? "llama-server not installed"
            : version.Length >= 2 ? $"llama-server {version[0]} installed ({version[1]})" : "llama-server installed";
        LlamaDownloadButton.Content = installed ? "Update llama-server" : "Download llama-server";
    }

    // Latest release from GitHub, progress in the status line. Refused while the models are in use or the server runs
    // (its files are locked). Any failure is a log line and a toast; the previous install stays.
    private async void LlamaDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_llamaDownload is not null)
            return;
        if (_process is not null || _chatBusy)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            return;
        }
        if (LauncherLogic.RecordedLlamaServer(_llamaStateFile) is { } running)
        {
            running.Dispose();
            Toast("llama-server is running; turn Keep models loaded off to stop it first", "#F7C873");
            return;
        }
        using var cancel = _llamaDownload = new CancellationTokenSource();
        LlamaDownloadButton.IsEnabled = false;
        var zips = new List<string>();
        try
        {
            // ponytail: no overall timeout (a CUDA build is hundreds of MB); a stalled transfer waits until the app closes.
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
            LlamaStatus.Text = "Looking up the latest llama.cpp release…";
            var nvidia = await Task.Run(() => LauncherLogic.HasNvidiaGpu() ? LauncherLogic.ReadNvidiaSmi() ?? "" : null); // "" = a GPU whose driver version is unreadable
            var assets = await LauncherLogic.FetchLlamaAssetsAsync(url => http.GetStringAsync(url, cancel.Token), nvidia is not null, nvidia);
            foreach (var url in assets.Urls)
            {
                var zip = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-llama-{Guid.NewGuid():N}.zip");
                zips.Add(zip);
                await DownloadAsync(http, url, zip, Path.GetFileName(url), cancel.Token);
            }
            LlamaStatus.Text = "Installing…";
            await Task.Run(() => LauncherLogic.InstallLlamaServer(zips, LauncherLogic.LlamaFolder, $"{assets.Tag}\n{assets.Flavour}"));
            Log($"llama-server {assets.Tag} ({assets.Flavour}) installed in {LauncherLogic.LlamaFolder}");
            Toast($"llama-server {assets.Tag} installed");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } // the app is closing
        catch (Exception exception)
        {
            Log($"llama-server download failed: {exception.Message}");
            Toast("llama-server download failed", "#FF667A");
        }
        finally
        {
            foreach (var zip in zips)
                try { File.Delete(zip); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
            _llamaDownload = null;
            LlamaDownloadButton.IsEnabled = true;
            RefreshLlamaStatus();
        }
    }

    // Streams url to file; the copy loop resumes on the UI thread between reads, so the status line updates directly.
    private async Task DownloadAsync(HttpClient http, string url, string file, string name, CancellationToken token, TextBlock? status = null)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = File.Create(file);
        var buffer = new byte[1 << 16];
        long done = 0, shown = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            done += read;
            if (done - shown < 1 << 20)
                continue;
            shown = done;
            (status ?? LlamaStatus).Text = total > 0 ? $"Downloading {name}: {done * 100 / total}% of {total / (1 << 20)} MB" : $"Downloading {name}: {done / (1 << 20)} MB";
        }
    }

    // Settings > System > Model runtime: the Models folders, each with Remove, and Add models folder… (AppSettings.ModelsFolder
    // holds them joined by ';').
    private void ModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        var folders = LauncherLogic.ModelFolders(_settings.ModelsFolder);
        var dialog = new OpenFolderDialog { Title = "Folder with GGUF models" };
        if (folders.LastOrDefault(Directory.Exists) is { } last)
            dialog.InitialDirectory = last;
        if (dialog.ShowDialog(this) != true)
            return;
        SetModelFolders([.. folders, dialog.FolderName]);
    }

    private void SetModelFolders(IEnumerable<string> folders)
    {
        _settings.ModelsFolder = LauncherLogic.JoinModelFolders(folders);
        _settings.Save();
        _localModelsVersion++;
        ShowModelsFolder();
    }

    private void ShowModelsFolder()
    {
        ModelsFoldersList.Children.Clear();
        var folders = LauncherLogic.ModelFolders(_settings.ModelsFolder);
        if (folders.Length == 0)
            ModelsFoldersList.Children.Add(new TextBlock { Text = "No folder added", Margin = new Thickness(0, 4, 0, 4), Opacity = 0.7 });
        foreach (var folder in folders)
        {
            var remove = new Button { Content = "Remove", Padding = new Thickness(11, 4, 11, 4), Tag = "ModelsFolderRemove", ToolTip = "Stop looking for models in this folder (nothing is deleted)" };
            remove.Click += (_, _) => SetModelFolders(LauncherLogic.ModelFolders(_settings.ModelsFolder).Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase)));
            DockPanel.SetDock(remove, Dock.Right);
            var text = new TextBlock { Text = folder, ToolTip = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0),
                Opacity = Directory.Exists(folder) ? 1 : 0.5 };
            ModelsFoldersList.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 6), Children = { remove, text } });
        }
    }
}

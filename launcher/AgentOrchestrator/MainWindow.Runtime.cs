using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;

namespace AgentOrchestrator;

// Options > Model runtime: Unsloth Studio's API, or llama.cpp's llama-server (downloaded into <data>\llama.cpp) that the
// engine starts itself and records in LauncherLogic.LlamaStateFile; the launcher stops it where it unloads chat models.
public partial class MainWindow
{
    private CancellationTokenSource? _llamaDownload; // the download in progress; cancelled when the app closes
    private string _llamaStateFile = LauncherLogic.LlamaStateFile; // --ui-smoke points it at its own file

    private bool LocalRuntime => _settings.Runtime == LauncherLogic.LlamaRuntime;

    private void InitRuntime()
    {
        _settings.Runtime = LocalRuntime ? LauncherLogic.LlamaRuntime : "unsloth";
        RuntimeLlamaRadio.IsChecked = LocalRuntime;
        RuntimeUnslothRadio.IsChecked = !LocalRuntime;
        ShowModelsFolder();
        RefreshLlamaStatus();
    }

    // Refused while a reply or run is using the models. Leaving the local runtime stops its server (frees the GPU for Unsloth).
    private void Runtime_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        var runtime = RuntimeLlamaRadio.IsChecked == true ? LauncherLogic.LlamaRuntime : "unsloth";
        if (runtime == _settings.Runtime)
            return;
        if (_process is not null || _chatBusy)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            (LocalRuntime ? RuntimeLlamaRadio : RuntimeUnslothRadio).IsChecked = true; // re-enters here with nothing to change
            return;
        }
        StopLocalServer();
        _settings.Runtime = runtime;
        _settings.Save();
    }

    // False, with a toast, when the local runtime is chosen but llama-server has not been downloaded.
    private bool RuntimeReady()
    {
        if (!LocalRuntime || File.Exists(LauncherLogic.LlamaServerPath))
            return true;
        Toast("Download llama-server in Options first", "#F7C873");
        return false;
    }

    private void ApplyRuntime(ProcessStartInfo info) =>
        LauncherLogic.ApplyRuntimeEnv(info, _settings.Runtime, LauncherLogic.LlamaServerPath, _settings.ModelsFolder);

    // Kills the server the engine recorded (only if that pid is still a llama-server). The Unsloth runtime never has one.
    private void StopLocalServer()
    {
        if (LocalRuntime && LauncherLogic.StopLlamaServer(_llamaStateFile))
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
            var assets = await LauncherLogic.FetchLlamaAssetsAsync(url => http.GetStringAsync(url, cancel.Token), LauncherLogic.HasNvidiaGpu());
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
    private async Task DownloadAsync(HttpClient http, string url, string file, string name, CancellationToken token)
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
            LlamaStatus.Text = total > 0 ? $"Downloading {name}: {done * 100 / total}% of {total / (1 << 20)} MB" : $"Downloading {name}: {done / (1 << 20)} MB";
        }
    }

    private void ModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Folder with GGUF models" };
        if (Directory.Exists(_settings.ModelsFolder))
            dialog.InitialDirectory = _settings.ModelsFolder;
        if (dialog.ShowDialog(this) != true)
            return;
        _settings.ModelsFolder = dialog.FolderName;
        _settings.Save();
        ShowModelsFolder();
    }

    private void ModelsFolderClear_Click(object sender, RoutedEventArgs e)
    {
        _settings.ModelsFolder = "";
        _settings.Save();
        ShowModelsFolder();
    }

    private void ShowModelsFolder()
    {
        ModelsFolderText.Text = _settings.ModelsFolder.Length > 0 ? _settings.ModelsFolder : "No folder chosen";
        ModelsFolderText.ToolTip = _settings.ModelsFolder.Length > 0 ? _settings.ModelsFolder : null;
        ModelsFolderClear.Visibility = _settings.ModelsFolder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

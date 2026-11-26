using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AgentOrchestrator;

// Settings > Voice: the speech and voice models' downloads (SpeechModels, TtsModels, only when Download is pressed) and the microphone dictation
// listens to (AppSettings.Microphone, a Windows device name; "" = the Windows default).
public partial class MainWindow
{
    private CancellationTokenSource? _speechDownload; // the downloads in progress; Cancel, or the app closing, stops them
    private readonly Dictionary<TtsModel, CancellationTokenSource?> _ttsDownloads = new();
    private readonly Dictionary<TtsModel, (RadioButton Pick, TextBlock Status, Button Download, Button Delete)> _ttsRows = new();

    private void InitVoice()
    {
        BuildTtsModels();
        BuildChatterbox();
        RefreshVoiceStatus();
        FillMicrophones();
        BuildSpeakOut();
    }

    private void RefreshVoiceStatus()
    {
        var ready = SpeechModels.Ready;
        VoiceStatus.Text = ready ? "Parakeet TDT 0.6B ready" : $"Speech model not downloaded · {SpeechModels.DownloadBytes >> 20} MB";
        VoiceDownloadButton.Content = "Download";
        VoiceDownloadButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        var (active, chosen) = (TtsModels.Active, TtsModels.Find(_settings.Tts.Model));
        foreach (var (model, row) in _ttsRows)
        {
            if (_ttsDownloads.GetValueOrDefault(model) is not null)
                continue; // downloading: its row shows the progress and Cancel
            ready = model.Ready;
            row.Status.Text = ready ? (model == active ? "Ready · reading" : "Downloaded")
                : model != chosen || !active.Ready ? "Not downloaded"
                : model == TtsModels.Full ? "Full quality not downloaded — Download for clearer voices"
                : $"{model.Label} not downloaded — {active.Label} reads meanwhile";
            row.Download.Content = "Download";
            row.Download.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            row.Delete.Visibility = ready && model != chosen ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshChatterbox();
        RefreshSpeakOut();
    }

    private CancellationTokenSource? _chatterboxInstall;
    private TextBlock _chatterboxStatus = null!;
    private Button _chatterboxInstallButton = null!, _chatterboxRemoveButton = null!;

    // The Chatterbox Turbo row after the Kokoro rows: status, Install/Cancel (never on its own) and Uninstall.
    private void BuildChatterbox()
    {
        _chatterboxStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0) };
        _chatterboxStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _chatterboxInstallButton = new Button { Content = "Install", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(_chatterboxInstallButton, "Install Chatterbox");
        _chatterboxRemoveButton = new Button { Content = "Uninstall", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(_chatterboxRemoveButton, "Uninstall Chatterbox");
        _chatterboxInstallButton.Click += (_, _) => ModelDownload("Chatterbox voice engine", _chatterboxInstallButton, _chatterboxStatus, ChatterboxRuntime.InstallAsync,
            ChatterboxRuntime.Folder, () => _chatterboxInstall, c => _chatterboxInstall = c);
        _chatterboxRemoveButton.Click += async (_, _) =>
        {
            if (Choose("Remove Chatterbox", $"Remove Chatterbox ({ChatterboxRuntime.InstallBytes / (double)(1L << 30):0.0} GB)?", ("Remove", "remove")) is null)
                return;
            StopSpeaking();
            await Task.Run(ChatterboxRuntime.Uninstall); // unloads first; deleting gigabytes stays off the UI thread
            Log("Chatterbox removed");
            RefreshVoiceStatus();
        };
        var label = new TextBlock { Text = "Chatterbox Turbo (lively voices, GPU)", MinWidth = 170, VerticalAlignment = VerticalAlignment.Center };
        TtsModelsPanel.Children.Add(new DockPanel { Margin = new Thickness(0, 10, 0, 0),
            Children = { Docked(_chatterboxRemoveButton, Dock.Right), Docked(_chatterboxInstallButton, Dock.Right), Docked(label, Dock.Left), _chatterboxStatus } });
    }

    private void RefreshChatterbox()
    {
        if (_chatterboxInstall is not null)
            return; // installing: the row shows the progress and Cancel
        var ready = ChatterboxRuntime.Ready;
        _chatterboxStatus.Text = ready ? "Installed · " + (Chatterbox.Provider is { } where ? $"runs on {where}" : "loads when a voice reads")
            : $"Not installed · {ChatterboxRuntime.DownloadBytes / (double)(1L << 30):0.0} GB download";
        _chatterboxInstallButton.Content = "Install";
        _chatterboxInstallButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        _chatterboxRemoveButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }

    // The Kokoro builds, each a row: the choice (TtsSettings.Model), its status, Download/Cancel, and Delete when downloaded and
    // not chosen. Built once: a running download keeps writing into its row's status.
    private void BuildTtsModels()
    {
        TtsModels.Chosen = _settings.Tts.Model;
        foreach (var model in TtsModels.All)
        {
            var pick = new RadioButton { Content = $"{model.Label} · {model.DownloadBytes >> 20} MB", GroupName = "TtsModel", MinWidth = 170,
                IsChecked = model == TtsModels.Find(_settings.Tts.Model), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(pick, model.Label);
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0) };
            status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var download = new Button { Content = "Download", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetName(download, $"Download {model.Label}");
            var delete = new Button { Content = "Delete", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetName(delete, $"Delete {model.Label}");
            pick.Checked += (_, _) => ChooseTtsModel(model);
            download.Click += (_, _) => ModelDownload($"{model.Label.ToLowerInvariant()} voice model", download, status, model.DownloadAsync, model.Folder,
                () => _ttsDownloads.GetValueOrDefault(model), c => _ttsDownloads[model] = c);
            delete.Click += (_, _) => DeleteTtsModel(model);
            TtsModelsPanel.Children.Add(new DockPanel { Margin = new Thickness(0, 10, 0, 0),
                Children = { Docked(delete, Dock.Right), Docked(download, Dock.Right), Docked(pick, Dock.Left), status } });
            _ttsRows[model] = (pick, status, download, delete);
        }
    }

    // Reading stops first; the engines reload for the new build on the next sentence (Tts.Synthesize). Nothing is read on its own.
    private void ChooseTtsModel(TtsModel model)
    {
        if (_settings.Tts.Model == model.Key)
            return;
        StopSpeaking();
        _settings.Tts.Model = TtsModels.Chosen = model.Key;
        _settings.Save();
        RefreshVoiceStatus();
    }

    // Frees the disk after asking; the build reading now (Compact, while Full is chosen but not downloaded) says nothing reads after.
    private void DeleteTtsModel(TtsModel model)
    {
        var after = model == TtsModels.Active ? $" Nothing reads aloud until {TtsModels.Find(_settings.Tts.Model).Label} is downloaded." : "";
        if (Choose("Delete voice model", $"Delete the {model.Label} voice model ({model.DownloadBytes >> 20} MB)?{after}", ("Delete", "delete")) is null)
            return;
        StopSpeaking();
        Tts.Release();
        try
        {
            Directory.Delete(model.Folder, recursive: true);
            Log($"The {model.Label} voice model deleted from {model.Folder}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Deleting the {model.Label} voice model failed: {exception.Message}");
            Toast("Could not delete the voice model: " + exception.Message, "#FF667A");
        }
        RefreshVoiceStatus();
    }

    private void VoiceDownload_Click(object sender, RoutedEventArgs e) =>
        ModelDownload("speech model", VoiceDownloadButton, VoiceStatus, SpeechModels.DownloadAsync, SpeechModels.Folder,
            () => _speechDownload, c => _speechDownload = c);

    // Download, or Cancel while downloading. A failure is a log line and a toast; the files already fetched stay (the next
    // Download resumes), and a half-finished one never counts as ready.
    private async void ModelDownload(string what, Button button, TextBlock status, Func<IProgress<(long done, long total)>, CancellationToken, Task> download,
        string folder, Func<CancellationTokenSource?> running, Action<CancellationTokenSource?> setRunning)
    {
        if (running() is { } current)
        {
            current.Cancel();
            return;
        }
        using var cancel = new CancellationTokenSource();
        setRunning(cancel);
        button.Content = "Cancel";
        var shownMb = -1L;
        var progress = new Progress<(long done, long total)>(p =>
        {
            if (p.done >> 20 == shownMb || running() != cancel)
                return;
            shownMb = p.done >> 20;
            status.Text = $"Downloading the {what}: {shownMb} of {p.total >> 20} MB ({p.done * 100 / p.total}%)";
        });
        try
        {
            status.Text = $"Downloading the {what}…";
            await download(progress, cancel.Token);
            Log($"The {what} downloaded into {folder}");
            Toast(char.ToUpper(what[0]) + what[1..] + " ready");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } // Cancel, or the app is closing
        catch (Exception exception)
        {
            Log($"The {what} download failed: {exception.Message}");
            Toast(char.ToUpper(what[0]) + what[1..] + " download failed", "#FF667A");
        }
        finally
        {
            setRunning(null);
            RefreshVoiceStatus();
        }
    }

    // "Windows default" first, then the microphones connected now; a saved one that is unplugged stays listed (and chosen) so
    // the choice is not lost, while dictation falls back to the default. Filled again each time the list opens.
    private void FillMicrophones()
    {
        List<string> names;
        try { names = Speech.Microphones(); }
        catch (NAudio.MmException) { names = []; } // no audio service
        if (_settings.Microphone.Length > 0 && !names.Contains(_settings.Microphone))
            names.Add(_settings.Microphone);
        MicrophoneBox.Items.Clear();
        MicrophoneBox.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = "" });
        foreach (var name in names)
            MicrophoneBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        MicrophoneBox.SelectedItem = MicrophoneBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == _settings.Microphone);
    }

    private void Microphone_DropDownOpened(object sender, EventArgs e) => FillMicrophones();

    private void Microphone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MicrophoneBox.SelectedItem is not ComboBoxItem { Tag: string name } || name == _settings.Microphone)
            return;
        _settings.Microphone = name;
        _settings.Save();
    }
}

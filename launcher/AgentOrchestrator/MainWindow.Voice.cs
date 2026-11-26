using System.Windows;
using System.Windows.Controls;

namespace AgentOrchestrator;

// Settings > Voice: the speech and voice models' downloads (SpeechModels, TtsModels, only when Download is pressed) and the microphone dictation
// listens to (AppSettings.Microphone, a Windows device name; "" = the Windows default).
public partial class MainWindow
{
    private CancellationTokenSource? _speechDownload, _ttsDownload; // the downloads in progress; Cancel, or the app closing, stops them

    private void InitVoice()
    {
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
        ready = TtsModels.Ready;
        TtsStatus.Text = ready ? "Kokoro 82M ready" : $"Voice model not downloaded · {TtsModels.DownloadBytes >> 20} MB";
        TtsDownloadButton.Content = "Download";
        TtsDownloadButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        RefreshSpeakOut();
    }

    private void VoiceDownload_Click(object sender, RoutedEventArgs e) =>
        ModelDownload("speech model", VoiceDownloadButton, VoiceStatus, SpeechModels.DownloadAsync, SpeechModels.Folder,
            () => _speechDownload, c => _speechDownload = c);

    private void TtsDownload_Click(object sender, RoutedEventArgs e) =>
        ModelDownload("voice model", TtsDownloadButton, TtsStatus, TtsModels.DownloadAsync, TtsModels.Folder,
            () => _ttsDownload, c => _ttsDownload = c);

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

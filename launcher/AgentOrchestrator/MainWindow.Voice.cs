using System.Windows;
using System.Windows.Controls;

namespace AgentOrchestrator;

// Settings > Voice: the speech model's download (SpeechModels, only when Download is pressed) and the microphone dictation
// listens to (AppSettings.Microphone, a Windows device name; "" = the Windows default).
public partial class MainWindow
{
    private CancellationTokenSource? _speechDownload; // the download in progress; Cancel, or the app closing, stops it

    private void InitVoice()
    {
        RefreshVoiceStatus();
        FillMicrophones();
    }

    private void RefreshVoiceStatus()
    {
        var ready = SpeechModels.Ready;
        VoiceStatus.Text = ready ? "Parakeet TDT 0.6B ready" : $"Speech model not downloaded · {SpeechModels.DownloadBytes >> 20} MB";
        VoiceDownloadButton.Content = "Download";
        VoiceDownloadButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }

    // Download, or Cancel while downloading. A failure is a log line and a toast; the files already fetched stay (the next
    // Download resumes), and a half-finished one never counts as ready.
    private async void VoiceDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_speechDownload is { } running)
        {
            running.Cancel();
            return;
        }
        using var cancel = _speechDownload = new CancellationTokenSource();
        VoiceDownloadButton.Content = "Cancel";
        var shownMb = -1L;
        var progress = new Progress<(long done, long total)>(p =>
        {
            if (p.done >> 20 == shownMb || _speechDownload != cancel)
                return;
            shownMb = p.done >> 20;
            VoiceStatus.Text = $"Downloading the speech model: {shownMb} of {p.total >> 20} MB ({p.done * 100 / p.total}%)";
        });
        try
        {
            VoiceStatus.Text = "Downloading the speech model…";
            await SpeechModels.DownloadAsync(progress, cancel.Token);
            Log($"Speech model downloaded into {SpeechModels.Folder}");
            Toast("Speech model ready");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } // Cancel, or the app is closing
        catch (Exception exception)
        {
            Log($"Speech model download failed: {exception.Message}");
            Toast("Speech model download failed", "#FF667A");
        }
        finally
        {
            _speechDownload = null;
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

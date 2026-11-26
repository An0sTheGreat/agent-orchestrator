using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio;
using NAudio.Wave;

namespace AgentOrchestrator;

// Dictation in the Chat and Code boxes: the mic (or Ctrl+Space held in the box) captures the microphone (NAudio, the one picked in
// Settings > Voice) into a DictationSession; while listening the box's text is hidden under a level meter drawn in its place, and
// each spoken phrase goes in at the caret (Speech.Insert). Dictation never sends: Enter or Send while listening only stops it and
// shows the text; the next press sends. It stops on the mic, Esc in the box, Enter or Send, switching view or conversation
// (ShowView), the window hiding to the tray or closing, focus leaving the box during hold-to-talk, or the session's own
// auto-stop; the phrase in progress is then transcribed (the mic turns, "working") and lands in the box, unless another
// conversation shows there by then (dropped, with a toast: the composer has no per-conversation draft to keep it in).
public partial class MainWindow
{
    private DictationSession? _dictation;  // listening (null: idle, or only the last phrase still transcribing)
    private TextBox? _dictationBox;
    private WaveInEvent? _dictationMic;
    private BlockingCollection<float[]>? _dictationQueue; // capture → the feeding thread (a slow phrase never stalls the device)
    private bool _dictationHold;           // Ctrl+Space started it: its release stops it
    private readonly DispatcherTimer _dictationFrame = new() { Interval = TimeSpan.FromMilliseconds(33) }; // the meter, ~30 fps
    // --ui-smoke seams: _dictationFeed replaces the microphone (it receives the feed to push 16 kHz samples into), and
    // _dictationTranscribe the recognizer (the energy gate then stands in for the VAD, and the model need not be downloaded).
    private Action<Action<float[]>>? _dictationFeed;
    private Func<float[], string>? _dictationTranscribe;

    private bool Dictating => _dictation is not null;

    private (Button Mic, DictationMeter Meter) DictationOf(TextBox box) => box == CodeInput ? (CodeMic, CodeMeter) : (ChatMic, ChatMeter);

    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        var box = sender == CodeMic ? CodeInput : ChatInput;
        if (Dictating && _dictationBox == box)
            StopDictation();
        else
            StartDictation(box);
        box.Focus();
    }

    // Esc stops; Ctrl+Space pressed starts (hold-to-talk), its release (ChatInput_PreviewKeyUp) stops. True when handled.
    private bool DictationKey(TextBox box, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Dictating)
        {
            StopDictation();
            return e.Handled = true;
        }
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.Control)
            return false;
        if (!e.IsRepeat && !Dictating)
        {
            StartDictation(box);
            _dictationHold = Dictating;
        }
        return e.Handled = true;
    }

    private void ChatInput_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_dictationHold && e.Key is Key.Space or Key.LeftCtrl or Key.RightCtrl)
            StopDictation();
    }

    // Hold-to-talk whose key-up will never reach the box: focus left it, or the window was deactivated.
    private void DictationHold_Lost(object sender, EventArgs e)
    {
        if (_dictationHold)
            StopDictation();
    }

    // Enter or Send while listening: the text is hidden under the meter, so the press only stops and shows it. True when it did.
    private bool RevealDictation()
    {
        if (!Dictating)
            return false;
        StopDictation();
        return true;
    }

    private void StartDictation(TextBox box)
    {
        StopDictation(); // the other box's
        if (_dictationTranscribe is null && !SpeechModels.Ready)
        {
            Toast("Download the speech model in Settings > Voice");
            OpenSettings(VoiceDownloadButton);
            return;
        }
        var (mic, meter) = DictationOf(box);
        Func<float[], string> transcribe;
        DictationSession session;
        if (_dictationTranscribe is { } fake)
            (transcribe, session) = (fake, new DictationSession(fake, vad: false));
        else
        {
            var recognizer = new Lazy<Func<float[], string>>(Speech.Recognizer); // loads in the background: the UI never waits for 650 MB
            _ = Task.Run(() => recognizer.Value);
            transcribe = samples => recognizer.Value(samples);
            session = new DictationSession(transcribe);
        }
        var queue = new BlockingCollection<float[]>();
        void Feed(float[] samples) // on the capture thread: the meter follows it even while the feeding thread transcribes
        {
            var level = Speech.LevelOf(samples);
            Dispatcher.BeginInvoke(() => meter.Push(level));
            try { queue.Add(samples); }
            catch (InvalidOperationException) { } // stopped: the device's last buffers
        }
        if (_dictationFeed is null)
        {
            try { _dictationMic = OpenMicrophone(Feed); }
            catch (MmException exception)
            {
                session.Dispose();
                Log($"Dictation: the microphone did not open: {exception.Result}");
                Toast(WaveInEvent.DeviceCount == 0 ? "No microphone found"
                    : "The microphone could not be opened: check Windows Settings > Privacy & security > Microphone", "#FF667A");
                return;
            }
        }
        else
            _dictationFeed(Feed);
        (_dictation, _dictationBox, _dictationQueue) = (session, box, queue);
        var (conv, opened, stored) = (ConvOf(box), ConvOf(box).Opened, box == CodeInput ? _codeConv : _chatConv);
        session.Text += phrase => Dispatcher.BeginInvoke(() =>
        {
            if (ConvOf(box) != conv || conv.Opened != opened) // parked, opened or cleared since: this box is another conversation's now
            {
                Toast($"Dictation for {(stored is null ? "the new chat" : ConversationStore.TitleOf(stored))} was discarded", "#F7C873");
                return;
            }
            var (text, caret) = Speech.Insert(box.Text, box.CaretIndex, phrase);
            box.Text = text;
            box.CaretIndex = caret;
        });
        session.Ended += reason => Dispatcher.BeginInvoke(() =>
        {
            if (reason == "nothing heard")
                Toast("Nothing heard — check the microphone in Settings > Voice", "#F7C873");
            if (_dictation == session)
                StopDictation();
        });
        // The feeding thread: until StopDictation completes the queue, then the phrase in progress (Stop) and the mic back to idle.
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var samples in queue.GetConsumingEnumerable())
                    session.Feed(samples);
                session.Stop();
            }
            catch (Exception exception) // the recognizer failed to load or decode
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Log($"Dictation failed: {exception.Message}");
                    Toast("Dictation failed: " + exception.Message, "#FF667A");
                    if (_dictation == session)
                        StopDictation();
                });
            }
            finally
            {
                try { session.Dispose(); }
                catch (Exception) { } // a recognizer that failed fails again on the flush: already reported
                Dispatcher.BeginInvoke(() =>
                {
                    if ((string)mic.Tag == "busy")
                        mic.Tag = "";
                });
            }
        });
        mic.Tag = "on";
        box.Opacity = 0;       // the text stays, hidden under the meter
        box.IsReadOnly = true; // keys stay in the box (Esc, Enter, Ctrl+Space) without typing into hidden text
        meter.Start();
        meter.Visibility = Visibility.Visible;
        _dictationFrame.Tick -= DictationFrame;
        _dictationFrame.Tick += DictationFrame;
        _dictationFrame.Start();
        RefreshChatInput(); // the placeholder hides
    }

    private void DictationFrame(object? sender, EventArgs e)
    {
        if (_dictationBox is { } box)
            DictationOf(box).Meter.Frame();
    }

    // Idempotent. The microphone is released here; the queued audio and the phrase in progress finish on the feeding thread.
    private void StopDictation()
    {
        if (_dictation is null || _dictationBox is not { } box)
            return;
        var (mic, meter) = DictationOf(box);
        _dictationMic?.Dispose(); // stops recording and closes the device
        _dictationMic = null;
        _dictationQueue!.CompleteAdding();
        (_dictation, _dictationBox, _dictationQueue, _dictationHold) = (null, null, null, false);
        _dictationFrame.Stop();
        meter.Visibility = Visibility.Collapsed;
        box.Opacity = 1;
        box.IsReadOnly = false;
        mic.Tag = "busy";
        RefreshChatInput();
    }

    // 16 kHz mono 16-bit (the Windows default device converts any rate itself); a picked device that refuses it records at 48 or
    // 44.1 kHz, resampled. Buffers of 50 ms: the meter moves about 20 times a second. Throws MmException when nothing opens.
    private WaveInEvent OpenMicrophone(Action<float[]> feed)
    {
        var device = Speech.DeviceNumber(_settings.Microphone) ?? -1; // -1: WAVE_MAPPER, the Windows default
        int[] rates = [Speech.Rate, 48000, 44100];
        foreach (var rate in rates)
        {
            var mic = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(rate, 16, 1), BufferMilliseconds = 50 };
            mic.DataAvailable += (_, a) =>
            {
                var samples = new float[a.BytesRecorded / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BitConverter.ToInt16(a.Buffer, 2 * i) / 32768f;
                feed(rate == Speech.Rate ? samples : Speech.Resample(samples, rate, Speech.Rate));
            };
            mic.RecordingStopped += (_, a) =>
            {
                if (a.Exception is { } exception) // unplugged, or access withdrawn while listening
                    Dispatcher.BeginInvoke(() =>
                    {
                        Log($"Dictation: the microphone stopped: {exception.Message}");
                        Toast("The microphone stopped", "#FF667A");
                        if (_dictationMic == mic)
                            StopDictation();
                    });
            };
            try
            {
                mic.StartRecording();
                return mic;
            }
            catch (MmException exception) when (exception.Result == MmResult.WaveBadFormat && rate != rates[^1])
            {
                mic.Dispose();
            }
            catch
            {
                mic.Dispose();
                throw;
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    // --ui-smoke: the mic in both composers; a fake capture and recognizer: the meter covers the box (its text hidden, kept), a
    // phrase lands at the caret by Insert's rules, Esc stops (meter gone, text back); Enter while listening only stops (nothing
    // sent); hiding to the tray stops; a late phrase after a chat switch is dropped; Send while listening stops; without the
    // speech model the mic says where to download it. Never the real microphone.
    private async Task<bool> DictationSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: dictation: {what}"); return false; }
        var busy = _chatBusy;
        try
        {
            foreach (var (mic, box, stop) in new[] { (ChatMic, ChatInput, ChatStop), (CodeMic, CodeInput, CodeStop) })
                if (mic.Parent != box.Parent || Grid.GetColumn(mic) != Grid.GetColumn(stop) - 1 || mic.Visibility != Visibility.Visible)
                    return Fail($"{mic.Name} is not beside the box, left of Stop");
            if (!SpeechModels.Ready) // the smoke's own data folder: nothing downloaded
            {
                Mic_Click(ChatMic, new RoutedEventArgs());
                var voiceShown = SettingsPages.Children.OfType<FrameworkElement>().Any(p => (string)p.Tag == "Voice" && p.Visibility == Visibility.Visible);
                _settingsWindow?.Close();
                if (Dictating || ToastsOf("Download the speech model in Settings > Voice").Count == 0 || !voiceShown)
                    return Fail("without the model: no toast, or Settings > Voice not shown");
            }
            Action<float[]>? feed = null;
            _dictationFeed = f => feed = f;
            _dictationTranscribe = _ => "big";
            ShowView(chat: true);
            ChatInput.Text = "Hello world";
            ChatInput.CaretIndex = 5;
            Mic_Click(ChatMic, new RoutedEventArgs());
            await Task.Delay(200);
            if (!Dictating || feed is null || ChatMeter.Visibility != Visibility.Visible || Grid.GetColumn(ChatMeter) != Grid.GetColumn(ChatInput) || ChatMeter.Parent != ChatInput.Parent
                || ChatInput.Opacity != 0 || ChatInput.Text != "Hello world" || (string)ChatMic.Tag != "on" || ChatHint.Visibility == Visibility.Visible)
                return Fail("listening: the meter is not over the box, or the text is not hidden");
            await Task.Run(() => // off the UI thread, as the microphone: a loud second, then 0.6 s of quiet ends the phrase
            {
                for (var i = 0; i < 10; i++)
                    feed(Enumerable.Repeat(0.5f, 1600).ToArray());
                for (var i = 0; i < 6; i++)
                    feed(new float[1600]);
            });
            await Task.Delay(400);
            if (ChatInput.Text != "Hello big world" || ChatInput.CaretIndex != 9 || ChatMeter.Levels.Max() <= 0)
                return Fail($"the phrase: \"{ChatInput.Text}\" caret {ChatInput.CaretIndex}");
            ChatInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Task.Delay(300);
            if (Dictating || ChatMeter.Visibility == Visibility.Visible || ChatInput.Opacity != 1 || ChatInput.IsReadOnly || ChatInput.Text != "Hello big world" || (string)ChatMic.Tag != "")
                return Fail("Esc did not stop, or the text did not come back");
            // Enter while listening sends nothing (busy: a send would queue it): it only stops and shows the text
            _chatBusy = true;
            var queued = _chatQueue.Count;
            Mic_Click(ChatMic, new RoutedEventArgs());
            ChatInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            if (Dictating || ChatInput.Opacity != 1 || ChatInput.Text != "Hello big world" || _chatQueue.Count != queued)
                return Fail($"Enter while listening: dictating {Dictating}, \"{ChatInput.Text}\", {_chatQueue.Count - queued} queued");
            _chatBusy = busy;
            // a phrase still transcribing when another chat opens is dropped, not put in that chat's box
            ChatInput.Clear();
            Mic_Click(ChatMic, new RoutedEventArgs());
            for (var i = 0; i < 10; i++)
                feed(Enumerable.Repeat(0.5f, 1600).ToArray()); // mid-phrase: it is transcribed when dictation stops
            NewChat_Click(this, new RoutedEventArgs());
            for (var wait = 0; wait < 20 && !ToastStack.Children.OfType<Border>().Any(b => b.Child is StackPanel { Children: [_, TextBlock t] } && t.Text.EndsWith("was discarded")); wait++)
                await Task.Delay(100);
            if (ChatInput.Text.Length > 0 || !ToastStack.Children.OfType<Border>().Any(b => b.Child is StackPanel { Children: [_, TextBlock t] } && t.Text.EndsWith("was discarded")))
                return Fail($"a late phrase after a chat switch: \"{ChatInput.Text}\", or no toast");
            // Send while listening stops it (busy: Send queues, and an empty box queues nothing)
            ShowView();
            CodeInput.Clear();
            _chatBusy = true;
            Mic_Click(CodeMic, new RoutedEventArgs());
            if (!Dictating || CodeMeter.Visibility != Visibility.Visible)
                return Fail("Code's mic did not start");
            SendOrQueue(_code);
            if (Dictating || CodeMeter.Visibility == Visibility.Visible || CodeInput.Opacity != 1)
                return Fail("Send did not stop dictation");
            return true;
        }
        finally
        {
            StopDictation();
            (_dictationFeed, _dictationTranscribe, _chatBusy) = (null, null, busy);
            ChatInput.Clear();
            CodeInput.Clear();
            ShowView(chat: true);
        }
    }

    // --ui-smoke, last of all (it hides and shows the window, which later checks must not inherit): hiding to the tray releases
    // the microphone (a stand-in tray: test modes have none, and HideToTray closes without one).
    private Task<bool> DictationTraySmoke()
    {
        var (feed, transcribe) = (_dictationFeed, _dictationTranscribe);
        _dictationFeed = _ => { };
        _dictationTranscribe = _ => "";
        ShowView(chat: true);
        try
        {
            Mic_Click(ChatMic, new RoutedEventArgs());
            if (!Dictating)
            {
                Console.Error.WriteLine("ui-smoke: dictation: the mic did not start for the tray check");
                return Task.FromResult(false);
            }
            var activated = ShowActivated;
            _tray = new System.Windows.Forms.NotifyIcon();
            try { HideToTray(quiet: true); }
            finally
            {
                _tray.Dispose();
                _tray = null;
                ShowActivated = false; // back without taking the foreground
                Show();
                ShowActivated = activated;
            }
            if (Dictating)
            {
                Console.Error.WriteLine("ui-smoke: dictation: HideToTray left the microphone open");
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        finally
        {
            StopDictation();
            (_dictationFeed, _dictationTranscribe) = (feed, transcribe);
            ChatInput.Clear();
        }
    }
}

// The level meter drawn in the box's place while listening: a soft red dot and the elapsed time at the left, a centred row of
// thin rounded bars in the accent (newest on the right, scrolling left, eased toward the level), "Listening… Esc to stop" muted
// at the right when there is room. The phone's input pill draws the same (web/js/dictate.js draw()).
internal sealed class DictationMeter : FrameworkElement
{
    private const int Bars = 28;
    private const double BarWidth = 3, Gap = 3;
    private static readonly Brush Dot = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B)));
    internal readonly float[] Levels = new float[Bars]; // the last levels pushed, newest last
    private readonly float[] _shown = new float[Bars];  // eased toward Levels each frame
    private readonly Stopwatch _clock = new();

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(DictationMeter),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(DictationMeter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    public void Start()
    {
        Array.Clear(Levels);
        Array.Clear(_shown);
        _clock.Restart();
        InvalidateVisual();
    }

    public void Push(float level)
    {
        Array.Copy(Levels, 1, Levels, 0, Bars - 1);
        Levels[^1] = level;
    }

    public void Frame()
    {
        for (var i = 0; i < Bars; i++)
            _shown[i] += (Levels[i] - _shown[i]) * 0.3f;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext g)
    {
        double w = ActualWidth, mid = ActualHeight / 2, ms = _clock.Elapsed.TotalMilliseconds;
        g.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        g.PushOpacity(0.65 + 0.35 * Math.Sin(ms / 320));
        g.DrawEllipse(Dot, null, new Point(10, mid), 3.5, 3.5);
        g.Pop();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        FormattedText Text(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 12, TextBrush, dpi);
        var seconds = (int)(ms / 1000);
        var time = Text($"{seconds / 60}:{seconds % 60:00}");
        g.DrawText(time, new Point(19, mid - time.Height / 2));
        const double span = Bars * BarWidth + (Bars - 1) * Gap;
        double x0 = Math.Max(58, (w - span) / 2), tallest = Math.Clamp(ActualHeight - 10, 4, 26);
        for (var i = 0; i < Bars; i++)
        {
            var height = Math.Max(3, _shown[i] * tallest);
            g.DrawRoundedRectangle(BarBrush, null, new Rect(x0 + i * (BarWidth + Gap), mid - height / 2, BarWidth, height), 1.5, 1.5);
        }
        var label = Text("Listening… Esc to stop");
        if (w - (x0 + span) > label.Width + 20)
            g.DrawText(label, new Point(w - label.Width - 4, mid - label.Height / 2));
    }
}

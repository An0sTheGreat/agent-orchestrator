using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NAudio.Wave;

namespace AgentOrchestrator;

// A Chatterbox voice's reference clip: 24 kHz mono, the chosen 5–20 s with the silence at its ends trimmed, loudness-normalised;
// and the name rules (unique case-insensitively across Kokoro built-ins, blends and Chatterbox voices, "Default" built in).
internal static class ClipTools
{
    public const int Rate = Chatterbox.Rate;
    public const double MinSeconds = 5, MaxSeconds = 20;
    private const int Window = Rate / 20;  // 50 ms
    private const double Sound = 0.01;     // a window with an RMS of -40 dBFS or more has sound in it
    private const double Target = 0.1;     // -20 dBFS RMS after normalising

    // Any audio file NAudio reads (WAV, MP3, M4A, FLAC: Media Foundation) as 24 kHz mono, the channels averaged. Throws when unreadable.
    public static float[] Load(string path)
    {
        using var reader = new AudioFileReader(path);
        var (rate, channels) = (reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        var mono = new List<float>();
        var buffer = new float[rate * channels];
        for (int read; (read = reader.Read(buffer, 0, buffer.Length)) > 0 && mono.Count < rate * 600;) // ponytail: the first 10 minutes; trim a longer file first
            for (var i = 0; i + channels <= read; i += channels)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += buffer[i + c];
                mono.Add(sum / channels);
            }
        return Speech.Resample(CollectionsMarshal.AsSpan(mono), rate, Rate);
    }

    public static string? TooShort(int samples, string what) =>
        samples < MinSeconds * Rate ? $"{what} is {samples / (double)Rate:0.0} s: a voice needs at least {MinSeconds:0} s" : null;

    // The selection [start, start + length) of 24 kHz audio (at most 20 s) as a voice clip, or why it can't be one.
    public static (float[] clip, string? problem) Prepare(float[] audio, double start, double length)
    {
        var from = Math.Clamp((int)(start * Rate), 0, audio.Length);
        var selection = audio.AsSpan(from, Math.Clamp((int)(Math.Min(length, MaxSeconds) * Rate), 0, audio.Length - from));
        if (TooShort(selection.Length, "The selection") is { } shortSelection)
            return ([], shortSelection);
        if (SoundShare(selection) < 0.25)
            return ([], "The selection is nearly silent: choose a stretch where someone speaks");
        var clip = TrimSilence(selection);
        if (TooShort(clip.Length, "The selection without the silence at its ends") is { } shortClip)
            return ([], shortClip);
        return (Normalize(clip), null);
    }

    // Without the silent windows at either end (0.1 s kept on each side); empty when nothing has sound.
    public static float[] TrimSilence(ReadOnlySpan<float> s)
    {
        int first = -1, last = -1;
        for (var w = 0; w * Window < s.Length; w++)
            if (Rms(s.Slice(w * Window, Math.Min(Window, s.Length - w * Window))) >= Sound)
                (first, last) = (first < 0 ? w : first, w);
        if (first < 0)
            return [];
        return s[Math.Max(0, first * Window - Rate / 10)..Math.Min(s.Length, (last + 1) * Window + Rate / 10)].ToArray();
    }

    // The share of 50 ms windows with sound.
    public static double SoundShare(ReadOnlySpan<float> s)
    {
        int windows = 0, loud = 0;
        for (var at = 0; at < s.Length; at += Window, windows++)
            if (Rms(s.Slice(at, Math.Min(Window, s.Length - at))) >= Sound)
                loud++;
        return windows == 0 ? 0 : loud / (double)windows;
    }

    // To -20 dBFS RMS, the peak held under 0.95.
    public static float[] Normalize(float[] s)
    {
        var (rms, peak) = (Rms(s), s.Length == 0 ? 0 : s.Max(Math.Abs));
        if (rms <= 0)
            return s;
        var gain = (float)Math.Min(Target / rms, 0.95 / peak);
        return s.Select(x => x * gain).ToArray();
    }

    internal static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var x in s)
            sum += x * x;
        return s.IsEmpty ? 0 : Math.Sqrt(sum / s.Length);
    }

    // Why name can't be a new Chatterbox voice's, or null. taken: the names in use (Kokoro built-ins, blends, the other Chatterbox voices).
    public static string? NameProblem(string name, IEnumerable<string> taken) =>
        name.Length == 0 ? "Name the voice"
        : name.Length > 40 ? "Keep the name to 40 characters"
        : name.Equals("Default", StringComparison.OrdinalIgnoreCase) || taken.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)) ? $"A voice is already called \"{name}\""
        : null;

    // The loudest sample in each of columns slices: the waveform.
    public static float[] Peaks(float[] s, int columns) =>
        Enumerable.Range(0, columns).Select(c =>
        {
            int from = (int)((long)s.Length * c / columns), to = Math.Max(from + 1, (int)((long)s.Length * (c + 1) / columns));
            return from >= s.Length ? 0 : s.AsSpan(from, Math.Min(to, s.Length) - from).ToArray().Max(Math.Abs);
        }).ToArray();

    // --self-test
    public static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Clip self-test failed: " + what);
        }
        static float[] Tone(double seconds, float amp) => Enumerable.Range(0, (int)(seconds * Rate)).Select(i => amp * MathF.Sin(i * 0.05f)).ToArray();
        static float[] Quiet(double seconds) => new float[(int)(seconds * Rate)];
        var (clip, problem) = Prepare([.. Quiet(2), .. Tone(8, 0.3f), .. Quiet(2)], 0, 20);
        Check(problem is null, "a clip with sound: " + problem);
        Check(Math.Abs(clip.Length - 8.2 * Rate) <= 2 * Window, $"the silence at the ends trimmed: {clip.Length / (double)Rate:0.00} s");
        Check(Math.Abs(Rms(clip) - Target) < 0.005, $"normalised to -20 dBFS: {Rms(clip):0.000}");
        var long20 = Prepare(Tone(30, 0.3f), 3, 25);
        Check(long20.problem is null && long20.clip.Length <= MaxSeconds * Rate, "at most 20 s");
        Check(Prepare(Tone(4.9, 0.3f), 0, 20).problem?.Contains("at least 5 s") == true, "shorter than 5 s refused");
        Check(Prepare([.. Quiet(1), .. Tone(4, 0.3f), .. Quiet(1)], 0, 20).problem?.Contains("without the silence") == true, "5 s only with its silence refused");
        Check(Prepare(Tone(8, 0.001f), 0, 20).problem?.Contains("silent") == true, "near-silent refused");
        Check(Prepare([.. Tone(8, 0.3f), .. Quiet(10)], 10, 8).problem?.Contains("silent") == true, "the selection, not the whole audio");
        var spiky = Tone(6, 0.05f);
        spiky[100] = 1;
        Check(Normalize(spiky).Max(Math.Abs) <= 0.951f, "the peak held under 0.95");
        Check(Speech.Resample(new float[48000], 48000, Rate).Length == Rate && Speech.Resample(new float[44100], 44100, Rate).Length == Rate, "to 24 kHz");
        Check(Peaks([0, 0.5f, -1, 0.2f], 2) is [0.5f, 1], "peaks");
        string[] taken = ["af_heart", "Calm blend", "Narrator"];
        Check(NameProblem("", taken) is not null && NameProblem("narrator", taken) is not null && NameProblem("AF_HEART", taken) is not null
            && NameProblem("default", taken) is not null && NameProblem(new string('x', 41), taken) is not null, "names refused");
        Check(NameProblem("Narrator two", taken) is null, "a new name");
    }
}

// Settings > Voice > Speech output > Chatterbox voices (AppSettings.ChatterboxVoices, clips in chatterbox\voices\), shown once
// Chatterbox is installed: the list (Default and the saved ones: ▶, Rename, Edit, Delete) and Add a voice from an audio file,
// the microphone (the dictation capture, up to 20 s) or a Kokoro voice/blend reading a reference paragraph; then the selection
// over a waveform, a name, the sliders, the Kokoro stand-in, ▶ Preview, Save (ClipTools.Prepare). Chosen as "cb:" + Name.
public partial class MainWindow
{
    private Func<string, ChatterboxVoice, CancellationToken, float[]> _cbSynth = Chatterbox.Synthesize; // --ui-smoke: stubs
    private Func<string, VoiceChoice, float, CancellationToken, float[]> _cbKokoro = Tts.Synthesize;
    private Func<Action<float[]>, IDisposable>? _cbMic;  // null: the dictation microphone (16 kHz)
    private Func<IWavePlayer>? _cbOutput;                // null: the chosen speaker
    private bool _cbTest;                                // --ui-smoke: shown without the install
    private SpeechPlayer? _cbPlayer;                     // the preview playing
    private StackPanel? _cbSection, _cbList;
    private TextBlock? _cbHint;
    private StackPanel _cbEditor = null!;
    private TextBlock _cbStatus = null!;
    private TextBox _cbName = null!, _cbTestText = null!;
    private Slider _cbStart = null!, _cbLength = null!, _cbTemp = null!, _cbExag = null!, _cbCfg = null!;
    private ComboBox _cbFallback = null!, _cbKokoroPick = null!;
    private Canvas _cbWave = null!;
    private Rectangle _cbSelection = null!;
    private ProgressBar _cbLevel = null!;
    private Button _cbRecord = null!;
    private float[]? _cbDraft;      // the source audio at 24 kHz
    private string? _cbEditing;     // the saved voice being edited: Save replaces it
    private IDisposable? _cbRecording;
    private List<float>? _cbRecorded;
    private const double WaveWidth = 440, WaveHeight = 56;

    private IEnumerable<string> ChatterboxNames() => _settings.ChatterboxVoices.Select(v => v.Name).Prepend("Default");

    private void RefreshChatterboxVoices()
    {
        if (_cbSection is null)
            return;
        var ready = ChatterboxRuntime.Ready || _cbTest;
        _cbSection.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        _cbHint!.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildChatterboxVoices(StackPanel p)
    {
        p.Children.Add(Heading("Chatterbox voices"));
        p.Children.Add(_cbHint = Note("Install Chatterbox above to make lively voices from a short recording. Until then a Chatterbox voice reads with its Kokoro stand-in."));
        var s = _cbSection = new StackPanel();
        p.Children.Add(s);
        s.Children.Add(Hint("A voice from 5 to 20 seconds of someone speaking clearly. Chatterbox reads with it; its Kokoro stand-in reads when Chatterbox can't."));
        s.Children.Add(Hint("Use voices you have the right to copy (your own, with permission, public domain)"));
        s.Children.Add(_cbList = new StackPanel { Margin = new Thickness(0, 8, 0, 0) });

        s.Children.Add(new TextBlock { Text = "Add a voice", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        var file = new Button { Content = "From a file…", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 8, 0) };
        file.Click += async (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a recording of the voice",
                Filter = "Audio (*.wav;*.mp3;*.m4a;*.flac)|*.wav;*.mp3;*.m4a;*.flac|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true)
                await LoadChatterboxFile(dialog.FileName);
        };
        _cbRecord = new Button { Content = "Record", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(_cbRecord, "Record from the microphone");
        _cbRecord.Click += (_, _) => ToggleChatterboxRecording();
        _cbKokoroPick = KokoroPicker(Tts.Default, "Kokoro voice to copy");
        var fromKokoro = new Button { Content = "From this voice", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(8, 0, 0, 0) };
        fromKokoro.Click += async (_, _) => await ChatterboxFromKokoro();
        s.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { file, _cbRecord, _cbKokoroPick, fromKokoro } });
        _cbLevel = new ProgressBar { Maximum = 1, Height = 6, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_cbLevel, "Microphone level");
        s.Children.Add(_cbLevel);
        s.Children.Add(_cbStatus = Note(""));

        // the editor: shown once there is a recording
        var e = _cbEditor = new StackPanel { Visibility = Visibility.Collapsed };
        s.Children.Add(e);
        _cbWave = new Canvas { Width = WaveWidth, Height = WaveHeight, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        _cbSelection = new Rectangle { Height = WaveHeight, Opacity = 0.25 };
        _cbSelection.SetResourceReference(Shape.FillProperty, "Accent");
        e.Children.Add(_cbWave);
        FrameworkElement Slide(double min, double max, double step, double value, Func<double, string> show, string name, out Slider slider, Action<double>? changed = null)
        {
            var row = SliderWith(min, max, step, value, show, changed ?? (_ => { }), name);
            slider = (Slider)((DockPanel)row).Children[1];
            return row;
        }
        e.Children.Add(Row("Start", Slide(0, 1, 0.1, 0, v => $"{v:0.0} s", "Start", out _cbStart, _ => ShowSelection())));
        e.Children.Add(Row("Length", Slide(ClipTools.MinSeconds, ClipTools.MaxSeconds, 0.5, 10, v => $"{v:0.0} s", "Length", out _cbLength, _ => ShowSelection())));
        var playSelection = new Button { Content = "▶ The selection", Padding = new Thickness(11, 6, 11, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        playSelection.Click += async (_, _) =>
        {
            if (_cbDraft is not { } audio)
                return;
            var from = Math.Min((int)(_cbStart.Value * ClipTools.Rate), audio.Length);
            var part = audio[from..Math.Min(audio.Length, from + (int)(_cbLength.Value * ClipTools.Rate))];
            await PlayChatterbox((_, _) => part, ["the selection"]);
        };
        e.Children.Add(playSelection);
        _cbName = new TextBox { Width = 240, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_cbName, "Chatterbox voice name");
        e.Children.Add(Row("Name", _cbName));
        e.Children.Add(Row("Temperature", Slide(0.1, 1.5, 0.05, 0.8, v => $"{v:0.00}", "Temperature", out _cbTemp)));
        e.Children.Add(Row("Exaggeration (not used by Turbo yet)", Slide(0.25, 2, 0.05, 0.5, v => $"{v:0.00}", "Exaggeration", out _cbExag)));
        e.Children.Add(Row("Cfg / pace (not used by Turbo yet)", Slide(0, 1, 0.05, 0.5, v => $"{v:0.00}", "Cfg", out _cbCfg)));
        _cbFallback = KokoroPicker(Tts.Default, "Kokoro stand-in");
        e.Children.Add(Row("Kokoro stand-in (reads when Chatterbox can't)", _cbFallback));
        _cbTestText = new TextBox { Margin = new Thickness(0, 0, 8, 0), MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_cbTestText, "Test sentence");
        var preview = new Button { Content = "▶ Preview", Padding = new Thickness(11, 6, 11, 6) };
        preview.Click += async (_, _) => await PreviewDraft();
        e.Children.Add(new DockPanel { Margin = new Thickness(0, 14, 0, 0), Children = { Docked(preview, Dock.Right), _cbTestText } });
        var save = new Button { Content = "Save", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 8, 0) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => SaveChatterboxVoice();
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(11, 6, 11, 6) };
        cancel.Click += (_, _) => CloseChatterboxEditor();
        e.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { save, cancel } });
        FillChatterboxVoices();
        RefreshChatterboxVoices();
    }

    // The Kokoro voices and the saved blends.
    private ComboBox KokoroPicker(string chosen, string name)
    {
        var box = new ComboBox { Width = 220 };
        AutomationProperties.SetName(box, name);
        foreach (var c in _settings.CustomVoices)
            box.Items.Add(new ComboBoxItem { Content = c.Name + " (custom)", Tag = c.Name });
        foreach (var v in Tts.Voices)
            box.Items.Add(new ComboBoxItem { Content = $"{VoiceLabel(v.Name)} · {v.Language}", Tag = v.Name });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == chosen) ?? box.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == Tts.Default);
        return box;
    }

    private void FillChatterboxVoices()
    {
        if (_cbList is null)
            return;
        _cbList.Children.Clear();
        foreach (var voice in _settings.ChatterboxVoices.Prepend<ChatterboxVoice?>(null))
        {
            var name = voice?.Name ?? "Default";
            var v = voice ?? ChatterboxVoice.Default;
            var play = GlyphButton(PlayGlyph, $"Play {name}");
            play.Click += async (_, _) => await PlayChatterboxVoice(v, $"Hello, I'm {name}. This is how I sound.");
            var label = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var detail = new TextBlock { Text = voice is null ? "  built in" : $"  temperature {voice.Temperature:0.00} · stand-in {VoiceLabel(voice.Fallback)}",
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Tag = name };
            if (voice is not null)
            {
                var (rename, edit, delete) = (SmallButton("Rename"), SmallButton("Edit"), SmallButton("Delete"));
                rename.Click += (_, _) =>
                {
                    var box = new TextBox { Text = name, Width = 200 };
                    row.Children.Remove(label);
                    row.Children.Insert(row.Children.Count - 1, box);
                    box.Focus();
                    box.SelectAll();
                    var done = false;
                    void Commit(bool keep)
                    {
                        if (done)
                            return;
                        done = true;
                        if (keep && box.Text.Trim() != name && RenameChatterboxVoice(name, box.Text.Trim()) is null)
                            return; // the lists were refilled
                        FillChatterboxVoices();
                    }
                    box.KeyDown += (_, k) => { if (k.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape) Commit(k.Key == System.Windows.Input.Key.Enter); };
                    box.LostFocus += (_, _) => Commit(true);
                };
                edit.Click += (_, _) => EditChatterboxVoice(voice);
                delete.Click += (_, _) => DeleteChatterboxVoice(name);
                row.Children.Add(Docked(delete, Dock.Right));
                row.Children.Add(Docked(edit, Dock.Right));
                row.Children.Add(Docked(rename, Dock.Right));
            }
            row.Children.Add(Docked(play, Dock.Left));
            row.Children.Add(Docked(label, Dock.Left));
            row.Children.Add(detail);
            _cbList.Children.Add(row);
        }
    }

    private void Say(string text, bool problem)
    {
        _cbStatus.Text = text;
        if (problem)
            Toast(text, "#F7C873");
    }

    // ---- the sources: each makes a draft (24 kHz) or says why not

    private async Task<string?> LoadChatterboxFile(string path)
    {
        float[] audio;
        try { audio = await Task.Run(() => ClipTools.Load(path)); }
        catch (Exception e) when (e is not OutOfMemoryException) // NAudio/Media Foundation throw many kinds for a file they can't read
        {
            var why = $"Couldn't read {System.IO.Path.GetFileName(path)}: {e.Message}";
            Say(why, true);
            return why;
        }
        return SetDraft(audio, System.IO.Path.GetFileNameWithoutExtension(path));
    }

    private void ToggleChatterboxRecording()
    {
        if (_cbRecording is not null)
        {
            StopChatterboxRecording();
            return;
        }
        var recorded = _cbRecorded = new List<float>();
        void Feed(float[] samples) // 16 kHz, on the capture thread
        {
            bool full;
            lock (recorded)
            {
                recorded.AddRange(samples);
                full = recorded.Count >= Speech.Rate * ClipTools.MaxSeconds;
            }
            var level = Speech.LevelOf(samples);
            Dispatcher.BeginInvoke(() =>
            {
                _cbLevel.Value = level;
                if (full && _cbRecorded == recorded)
                    StopChatterboxRecording();
            });
        }
        try { _cbRecording = _cbMic is { } mic ? mic(Feed) : OpenMicrophone(Feed); }
        catch (NAudio.MmException e)
        {
            Say("The microphone didn't open: " + e.Message, true);
            return;
        }
        _cbRecord.Content = "Stop";
        _cbLevel.Visibility = Visibility.Visible;
        Say("Recording: speak for 5 to 20 seconds, then Stop", false);
    }

    // keep: the recording becomes the draft (Stop, or 20 s reached); else it is dropped (the editor closing).
    private void StopChatterboxRecording(bool keep = true)
    {
        if (_cbRecording is not { } recording)
            return;
        _cbRecording = null;
        recording.Dispose();
        _cbRecord.Content = "Record";
        _cbLevel.Visibility = Visibility.Collapsed;
        if (!keep)
            return;
        float[] samples;
        lock (_cbRecorded!)
            samples = _cbRecorded.Take((int)(Speech.Rate * ClipTools.MaxSeconds)).ToArray();
        SetDraft(Speech.Resample(samples, Speech.Rate, ClipTools.Rate), "My voice");
    }

    private async Task ChatterboxFromKokoro()
    {
        if (_cbKokoroPick.SelectedItem is not ComboBoxItem { Tag: string name })
            return;
        Say("Making the clip…", false);
        var choice = VoiceChoice.Find(name, _settings.CustomVoices);
        var make = _cbKokoro;
        try
        {
            var samples = await Task.Run(() => SpeechText.Sentences(ChatterboxRuntime.DefaultReference).SelectMany(s => make(s, choice, 1f, CancellationToken.None)).ToArray());
            SetDraft(samples, VoiceLabel(name).Split(' ')[0] + " copy"); // Tts.Rate is 24 kHz
        }
        catch (Exception e) // the Kokoro engine's errors: shown, never the app's error box
        {
            Say("Couldn't make the clip: " + e.Message, true);
        }
    }

    // Opens the editor on the audio; a source under 5 s is refused here (null when shown).
    private string? SetDraft(float[] audio, string suggested)
    {
        if (ClipTools.TooShort(audio.Length, "The recording") is { } why)
        {
            Say(why, true);
            return why;
        }
        _cbDraft = audio;
        var seconds = audio.Length / (double)ClipTools.Rate;
        _cbLength.Maximum = Math.Min(ClipTools.MaxSeconds, seconds);
        _cbLength.Value = Math.Min(10, _cbLength.Maximum);
        _cbStart.Maximum = Math.Max(0.1, seconds - ClipTools.MinSeconds);
        _cbStart.Value = 0;
        if (_cbEditing is null)
            _cbName.Text = suggested;
        DrawWave(audio);
        ShowSelection();
        _cbEditor.Visibility = Visibility.Visible;
        Say($"{seconds:0.0} s of audio: choose up to 20 s where only this voice speaks", false);
        return null;
    }

    private void DrawWave(float[] audio)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var peaks = ClipTools.Peaks(audio, (int)WaveWidth / 2);
            for (var i = 0; i < peaks.Length; i++)
            {
                var h = Math.Max(0.5, peaks[i] * WaveHeight / 2);
                g.BeginFigure(new Point(i * 2 + 1, WaveHeight / 2 - h), false, false);
                g.LineTo(new Point(i * 2 + 1, WaveHeight / 2 + h), true, false);
            }
        }
        geometry.Freeze();
        var wave = new System.Windows.Shapes.Path { Data = geometry, StrokeThickness = 1.2 };
        wave.SetResourceReference(Shape.StrokeProperty, "Muted");
        _cbWave.Children.Clear();
        _cbWave.Children.Add(_cbSelection);
        _cbWave.Children.Add(wave);
    }

    private void ShowSelection()
    {
        if (_cbDraft is not { Length: > 0 } audio)
            return;
        var seconds = audio.Length / (double)ClipTools.Rate;
        var start = Math.Min(_cbStart.Value, seconds);
        Canvas.SetLeft(_cbSelection, start / seconds * WaveWidth);
        _cbSelection.Width = Math.Max(1, Math.Min(_cbLength.Value, seconds - start) / seconds * WaveWidth);
    }

    private void EditChatterboxVoice(ChatterboxVoice voice)
    {
        float[] clip;
        try { clip = Chatterbox.LoadClip(ChatterboxRuntime.ClipPath(voice)); }
        catch (Exception e) when (e is not OutOfMemoryException) // missing, or not a WAV NAudio reads
        {
            Say($"Couldn't read {voice.Name}'s clip: {e.Message}", true);
            return;
        }
        _cbEditing = voice.Name;
        _cbName.Text = voice.Name;
        (_cbTemp.Value, _cbExag.Value, _cbCfg.Value) = (voice.Temperature, voice.Exaggeration, voice.Cfg);
        _cbFallback.SelectedItem = _cbFallback.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == voice.Fallback) ?? _cbFallback.SelectedItem;
        if (SetDraft(clip, voice.Name) is not null)
            _cbEditing = null;
    }

    private void CloseChatterboxEditor()
    {
        StopChatterboxRecording(keep: false);
        (_cbDraft, _cbEditing, _cbName.Text, _cbStatus.Text) = (null, null, "", "");
        _cbEditor.Visibility = Visibility.Collapsed;
    }

    // ---- playing: one preview at a time, through SpeechPlayer (the chosen speaker and volume)

    private async Task PlayChatterbox(Func<string, CancellationToken, float[]> make, IEnumerable<string> sentences)
    {
        StopSpeaking();
        var device = _settings.Tts.Device;
        var player = _cbPlayer = new SpeechPlayer((sentence, _, _, ct) => make(sentence, ct),
            _cbOutput ?? (() => new WaveOutEvent { DeviceNumber = SpeechPlayer.Speakers().IndexOf(device), DesiredLatency = 200 })); // -1: the Windows default
        try { await player.Speak(sentences, new VoiceChoice("chatterbox"), _settings.Tts, CancellationToken.None); }
        catch (Exception e) // engine or device errors: a toast, never the app's error box
        {
            Log($"Chatterbox preview failed: {e.Message}");
            Toast("Playing failed: " + e.Message, "#FF667A");
        }
        finally
        {
            player.Dispose();
            if (_cbPlayer == player)
                _cbPlayer = null;
        }
    }

    private Task PlayChatterboxVoice(ChatterboxVoice voice, string text)
    {
        if (!_cbTest && !Chatterbox.CanRunNow(out var reason))
        {
            Toast(reason, "#F7C873");
            return Task.CompletedTask;
        }
        var synth = _cbSynth;
        return PlayChatterbox((sentence, ct) => synth(sentence, voice, ct), SpeechText.Sentences(text));
    }

    private async Task PreviewDraft()
    {
        if (_cbDraft is null)
            return;
        var (clip, problem) = ClipTools.Prepare(_cbDraft, _cbStart.Value, _cbLength.Value);
        if (problem is not null)
        {
            Say(problem, true);
            return;
        }
        Directory.CreateDirectory(ChatterboxRuntime.VoicesFolder);
        Chatterbox.WriteWav(System.IO.Path.Combine(ChatterboxRuntime.VoicesFolder, "preview.wav"), clip); // overwritten by the next preview
        var voice = new ChatterboxVoice { Name = _cbName.Text.Trim(), Clip = "preview.wav", Temperature = _cbTemp.Value };
        await PlayChatterboxVoice(voice, _cbTestText.Text.Length > 0 ? _cbTestText.Text : "Hello! This is how the new voice sounds.");
    }

    // ---- saving, renaming, deleting: null when done, else the reason (also shown)

    private IEnumerable<string> TakenNames(string? except) =>
        Tts.Voices.Select(v => v.Name).Concat(_settings.CustomVoices.Select(c => c.Name)).Concat(_settings.ChatterboxVoices.Where(c => c.Name != except).Select(c => c.Name));

    private string? SaveChatterboxVoice()
    {
        var name = _cbName.Text.Trim();
        var problem = _cbDraft is null ? "Choose a recording first" : ClipTools.NameProblem(name, TakenNames(_cbEditing));
        float[] clip = [];
        if (problem is null)
            (clip, problem) = ClipTools.Prepare(_cbDraft!, _cbStart.Value, _cbLength.Value);
        if (problem is not null)
        {
            Say(problem, true);
            return problem;
        }
        Directory.CreateDirectory(ChatterboxRuntime.VoicesFolder);
        var file = $"{Guid.NewGuid():N}.wav";
        Chatterbox.WriteWav(System.IO.Path.Combine(ChatterboxRuntime.VoicesFolder, file), clip);
        var voice = new ChatterboxVoice { Name = name, Clip = file, Temperature = _cbTemp.Value, Exaggeration = _cbExag.Value, Cfg = _cbCfg.Value,
            Fallback = _cbFallback.SelectedItem is ComboBoxItem { Tag: string fallback } ? fallback : Tts.Default };
        var at = _settings.ChatterboxVoices.FindIndex(c => c.Name == _cbEditing);
        if (at >= 0)
        {
            DeleteClip(_settings.ChatterboxVoices[at]);
            _settings.ChatterboxVoices[at] = voice;
            Repoint("cb:" + _cbEditing, "cb:" + name);
        }
        else
            _settings.ChatterboxVoices.Add(voice);
        _settings.Save();
        CloseChatterboxEditor();
        FillVoicePickers();
        Toast($"Voice \"{name}\" saved");
        return null;
    }

    private string? RenameChatterboxVoice(string from, string to)
    {
        if (ClipTools.NameProblem(to, TakenNames(from)) is { } problem)
        {
            Toast(problem, "#F7C873");
            return problem;
        }
        _settings.ChatterboxVoices.First(c => c.Name == from).Name = to;
        Repoint("cb:" + from, "cb:" + to);
        _settings.Save();
        FillVoicePickers();
        return null;
    }

    private void DeleteChatterboxVoice(string name)
    {
        if (Choose("Delete Chatterbox voice", $"Delete \"{name}\" and its clip?", ("Delete", "delete")) is null)
            return;
        foreach (var voice in _settings.ChatterboxVoices.Where(c => c.Name == name).ToList())
        {
            DeleteClip(voice);
            _settings.ChatterboxVoices.Remove(voice);
        }
        Repoint("cb:" + name, null);
        _settings.Save();
        FillVoicePickers();
    }

    private static void DeleteClip(ChatterboxVoice voice)
    {
        if (System.IO.Path.IsPathRooted(voice.Clip) || voice.Clip == ChatterboxRuntime.DefaultClipName)
            return; // only clips this library wrote
        try { File.Delete(ChatterboxRuntime.ClipPath(voice)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // in use: left behind, unused
    }
}

// --ui-smoke: stub engines, microphone and output (never real audio, a microphone or the GPU), WAVs written to the smoke's data
// folder. The section shows when installed; a 3 s file, an unreadable file and a near-silent selection are refused with a
// reason and save nothing; a file saves a 24 kHz mono 5-20 s clip, listed and in every voice picker marked Chatterbox; a
// built-in's name is refused (and a blend may not take a Chatterbox voice's); ▶ reads with the voice; Kokoro and the
// microphone make drafts; rename moves the choice; delete (confirmed) removes the clip and puts the default back.
public partial class MainWindow
{
    private async Task<bool> ChatterboxVoicesSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: chatterbox voices: {what}"); return false; }
        var (synth, kokoro, mic, output, test, choice) = (_cbSynth, _cbKokoro, _cbMic, _cbOutput, _cbTest, _codeChoice);
        var savedTts = System.Text.Json.JsonSerializer.Serialize(_settings.Tts);
        var savedVoices = _settings.ChatterboxVoices.ToList();
        var said = new List<string>();
        static float[] Tone(double seconds, float amp, int rate) => Enumerable.Range(0, (int)(seconds * rate)).Select(i => amp * MathF.Sin(i * 0.07f)).ToArray();
        static void Wav(string path, double seconds, float amp)
        {
            var mono = Tone(seconds, amp, 44100);
            using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
            var stereo = mono.SelectMany(x => new[] { x, x }).ToArray();
            writer.WriteSamples(stereo, 0, stereo.Length);
        }
        _cbSynth = (text, voice, ct) => { lock (said) said.Add($"{voice.Name}|{text}"); return new float[Chatterbox.Rate / 2]; };
        _cbKokoro = (text, voice, speed, ct) => Tone(text.Length * 0.07, 0.3f, Tts.Rate);
        _cbMic = feed => { feed(Tone(6, 0.3f, Speech.Rate)); return new CancellationTokenSource(); };
        _cbOutput = () => new Tts.FakeOutput();
        _cbTest = true;
        try
        {
            RefreshSpeakOut();
            if (_cbSection!.Visibility != Visibility.Visible || _cbHint!.Visibility == Visibility.Visible)
                return Fail("the section is not shown with Chatterbox installed");
            var dir = AppSettings.DataRoot;
            Directory.CreateDirectory(dir);
            string good = System.IO.Path.Combine(dir, "smoke-voice.wav"), shortWav = System.IO.Path.Combine(dir, "smoke-short.wav"),
                silent = System.IO.Path.Combine(dir, "smoke-silent.wav"), junk = System.IO.Path.Combine(dir, "smoke-junk.wav");
            Wav(good, 8, 0.3f);
            Wav(shortWav, 3, 0.3f);
            Wav(silent, 8, 0.0005f);
            File.WriteAllText(junk, "not audio");
            var count = _settings.ChatterboxVoices.Count;
            var tooShort = await LoadChatterboxFile(shortWav);
            if (tooShort is null || !tooShort.Contains("at least 5 s") || _cbEditor.Visibility == Visibility.Visible)
                return Fail($"a 3 s file was not refused: {tooShort}");
            var unreadable = await LoadChatterboxFile(junk);
            if (unreadable is null || !unreadable.StartsWith("Couldn't read"))
                return Fail($"an unreadable file was not refused: {unreadable}");
            if (await LoadChatterboxFile(silent) is { } silentLoad)
                return Fail($"an 8 s quiet file did not open: {silentLoad}");
            _cbName.Text = "Smoke quiet";
            var quiet = SaveChatterboxVoice();
            if (quiet is null || !quiet.Contains("silent") || _settings.ChatterboxVoices.Count != count)
                return Fail($"a near-silent selection was not refused: {quiet}");
            CloseChatterboxEditor();
            var goodLoad = await LoadChatterboxFile(good);
            if (goodLoad is not null || _cbEditor.Visibility != Visibility.Visible || _cbName.Text != "smoke-voice" || _cbWave.Children.Count != 2)
                return Fail($"an 8 s file did not open in the editor: {goodLoad}");
            _cbName.Text = "AF_Heart";
            if (SaveChatterboxVoice() is null)
                return Fail("a built-in voice's name was accepted");
            (_cbName.Text, _cbTemp.Value) = ("Smoke voice", 0.6);
            if (SaveChatterboxVoice() is { } saveProblem)
                return Fail("save: " + saveProblem);
            var voice = _settings.ChatterboxVoices.FirstOrDefault(c => c.Name == "Smoke voice");
            if (voice is null || Math.Abs(voice.Temperature - 0.6) > 1e-6 || !File.Exists(ChatterboxRuntime.ClipPath(voice)) || _cbEditor.Visibility == Visibility.Visible)
                return Fail("the voice was not saved with its clip");
            using (var reader = new WaveFileReader(ChatterboxRuntime.ClipPath(voice)))
                if (reader.WaveFormat.SampleRate != 24000 || reader.WaveFormat.Channels != 1 || reader.TotalTime.TotalSeconds is < 5 or > 20)
                    return Fail($"the clip is {reader.WaveFormat} for {reader.TotalTime.TotalSeconds:0.0} s");
            bool Listed(string tag) => _voicePickers.Count > 1 && _voicePickers.All(b => b.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == tag && ((string)i.Content).EndsWith("(Chatterbox)")));
            bool InList(string name) => _cbList!.Children.OfType<FrameworkElement>().Any(r => r.Tag as string == name);
            // the engine toggle and the view switch: Chatterbox lists only "cb:" voices in the main picker and shows only its group; Kokoro the reverse
            var mainPicker = _voicePickers[0];
            bool OnlyCb(bool cb) => mainPicker.Items.Count > 0 && mainPicker.Items.OfType<ComboBoxItem>().All(i => ((string)i.Tag).StartsWith("cb:") == cb);
            if ((string)EngineKokoro.Content != "Kokoro" || (string)EngineChatterbox.Content != "Chatterbox")
                return Fail("the engine toggle is not Kokoro | Chatterbox");
            EngineChatterbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (_settings.Tts.Engine != "chatterbox" || !_settings.Tts.Voice.StartsWith("cb:") || !OnlyCb(true) || ChatterboxSettings.Visibility != Visibility.Visible
                || KokoroSettings.Visibility == Visibility.Visible || AppSettings.Load().Tts.Engine != "chatterbox")
                return Fail($"picking Chatterbox: engine {_settings.Tts.Engine}, voice {_settings.Tts.Voice}");
            VoiceViewKokoro.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (KokoroSettings.Visibility != Visibility.Visible || ChatterboxSettings.Visibility == Visibility.Visible || _settings.Tts.Engine != "chatterbox")
                return Fail("the view switch did not show only the Kokoro group");
            EngineKokoro.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (_settings.Tts.Engine != "kokoro" || !OnlyCb(false))
                return Fail("picking Kokoro still lists Chatterbox voices");
            if (System.Text.Json.JsonSerializer.Deserialize<TtsSettings>("{\"Voice\":\"cb:Narrator\"}")!.Engine != "chatterbox"
                || System.Text.Json.JsonSerializer.Deserialize<TtsSettings>("{\"Voice\":\"bm_george\"}")!.Engine != "kokoro")
                return Fail("a saved voice without Engine does not give its engine");
            SetEngine("chatterbox"); // the main picker lists the Chatterbox voices below
            if (!Listed("cb:Smoke voice") || !Listed("cb:Default") || !InList("Smoke voice") || !InList("Default") || AppSettings.Load().ChatterboxVoices.All(c => c.Name != "Smoke voice"))
                return Fail("the voice is not listed, in every picker, or saved in the settings");
            if (SaveBlend("smoke VOICE", new Dictionary<string, double> { ["af_bella"] = 0.5, ["af_sky"] = 0.5 }, null) is null)
                return Fail("a blend took a Chatterbox voice's name");
            await PlayChatterboxVoice(voice, "Hello there.");
            if (!said.Contains("Smoke voice|Hello there."))
                return Fail($"▶ did not read with the voice: [{string.Join(" / ", said)}]");
            // the other sources
            _cbKokoroPick.SelectedItem = _cbKokoroPick.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "bm_george");
            await ChatterboxFromKokoro();
            if (_cbDraft is not { Length: >= 5 * 24000 } || _cbEditor.Visibility != Visibility.Visible)
                return Fail("a Kokoro voice made no draft");
            CloseChatterboxEditor();
            ToggleChatterboxRecording();
            await Task.Delay(100);
            ToggleChatterboxRecording();
            if (_cbDraft is not { Length: >= 5 * 24000 } || (string)_cbRecord.Content != "Record")
                return Fail("the microphone made no draft");
            CloseChatterboxEditor();
            // rename: the choice follows; delete: the clip goes and the default comes back
            (_settings.Tts.Voice, _settings.Tts.WorkerVoices["planner"]) = ("cb:Smoke voice", "cb:Smoke voice");
            if (RenameChatterboxVoice("Smoke voice", "default") is null)
                return Fail("renamed onto Default");
            if (RenameChatterboxVoice("Smoke voice", "Smoke renamed") is not null || Listed("cb:Smoke voice") || !Listed("cb:Smoke renamed") || !InList("Smoke renamed")
                || _settings.Tts.Voice != "cb:Smoke renamed" || _settings.Tts.WorkerVoices["planner"] != "cb:Smoke renamed" || AppSettings.Load().ChatterboxVoices.All(c => c.Name != "Smoke renamed"))
                return Fail("rename");
            var clip = ChatterboxRuntime.ClipPath(voice);
            _codeChoice = _ => "delete";
            DeleteChatterboxVoice("Smoke renamed");
            if (Listed("cb:Smoke renamed") || InList("Smoke renamed") || File.Exists(clip) || _settings.Tts.Voice != VoiceChoice.ChatterboxDefault || _settings.Tts.WorkerVoices.ContainsKey("planner")
                || AppSettings.Load().ChatterboxVoices.Any(c => c.Name == "Smoke renamed"))
                return Fail("delete");
            return true;
        }
        finally
        {
            CloseChatterboxEditor();
            StopSpeaking();
            (_cbSynth, _cbKokoro, _cbMic, _cbOutput, _cbTest, _codeChoice) = (synth, kokoro, mic, output, test, choice);
            _settings.Tts = System.Text.Json.JsonSerializer.Deserialize<TtsSettings>(savedTts)!;
            _settings.ChatterboxVoices = savedVoices;
            _settings.Save();
            if (Directory.Exists(ChatterboxRuntime.Folder))
                Directory.Delete(ChatterboxRuntime.Folder, recursive: true);
            RefreshSpeakOut();
            FillVoicePickers();
        }
    }
}

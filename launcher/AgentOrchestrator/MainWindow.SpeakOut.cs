using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AgentOrchestrator;

// Speech out on the desktop: the speak button on each Chat/Code reply (it turns into Stop while that reply reads; one player, so a
// second reply's speak ends the first), Read replies aloud (a finished reply in the conversation shown, with its worker's voice),
// and Settings > Voice > Speech output (AppSettings.Tts and CustomVoices): the voice, speed/volume/pause, a test box, the speaker,
// the reading rules, a voice per worker, the voice library with previews, and custom blends. Speech stops on Stop, a new reply,
// switching view or conversation (ShowView), the window hiding to the tray or closing; the player releases the device then.
public partial class MainWindow
{
    private SpeechPlayer? _speechPlayer; // made on first use; --ui-smoke puts one with a stub engine and a fake output here
    private bool _speechTest;            // --ui-smoke: the stub needs no downloaded model
    private Button? _speakingButton;     // the reply reading now (its button shows Stop); null: idle or a settings preview
    private int _readGeneration;         // each Read's number: only the newest one puts its button back when it ends
    private readonly List<ComboBox> _voicePickers = new(); // the main voice and the per-worker pickers (filled by FillVoicePickers)
    private StackPanel? _voiceLibrary, _savedVoices, _blendRows;
    private TextBox? _blendName;
    private string? _blendEditing; // the saved voice the editor was loaded from (Edit): Save replaces it
    private const string SpeakGlyph = "", StopGlyph = "", PlayGlyph = "";

    private SpeechPlayer Player => _speechPlayer ??= new SpeechPlayer();

    // ---- the speak button and reading

    private Button SpeakButton(ChatReply reply)
    {
        var button = GlyphButton(SpeakGlyph, "Read aloud");
        button.Tag = "speak";
        button.Click += (_, _) =>
        {
            if (_speakingButton == button)
                StopSpeaking();
            else
                Read(button, reply.Final ?? reply.AnswerText.ToString(), VoiceFor(reply.Role));
        };
        return button;
    }

    private Button GlyphButton(string glyph, string tip)
    {
        var text = new TextBlock { Text = glyph, FontSize = 12 };
        text.SetResourceReference(StyleProperty, "RailGlyph");
        var button = new Button { Content = text, Width = 24, Height = 24, Padding = new Thickness(0), ToolTip = tip, Cursor = Cursors.Hand };
        button.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(button, tip);
        return button;
    }

    private static void ShowSpeaking(Button button, bool on)
    {
        ((TextBlock)button.Content).Text = on ? StopGlyph : SpeakGlyph;
        button.Tag = on ? "stop" : "speak";
        button.ToolTip = on ? "Stop reading" : "Read aloud";
    }

    // Read replies aloud: a reply that finished in the conversation shown (FinishChatTurn), with its worker's voice.
    private void ReadFinished(ChatReply reply)
    {
        if (_settings.Tts.AutoRead && !InCall) // a call speaks its replies itself
            Read(reply.Speak, reply.Final ?? reply.AnswerText.ToString(), VoiceFor(reply.Role));
    }

    // The worker's own voice when set, else the main one.
    private VoiceChoice VoiceFor(string role) =>
        VoiceChoice.Find(_settings.Tts.WorkerVoices.TryGetValue(role, out var v) && v.Length > 0 ? v : _settings.Tts.Voice, _settings.CustomVoices);

    // Reads markdown aloud (the reading rules, then sentence by sentence); button, when a reply's, shows Stop meanwhile. Longer than
    // MaxChars asks first. A missing model, a missing speaker or an engine error is a toast and back to idle.
    private async void Read(Button? button, string markdown, VoiceChoice voice)
    {
        if (!_speechTest && !TtsModels.Ready)
        {
            Toast("Download the voice model in Settings > Voice");
            return;
        }
        var s = _settings.Tts;
        string text;
        try { text = SpeechText.ForSpeech(markdown, s); }
        catch (Exception exception) // async void: an escape would be the app's error box
        {
            Log($"Reading aloud failed: {exception.Message}");
            Toast("Reading aloud failed: " + exception.Message, "#FF667A");
            return;
        }
        if (text.Length == 0)
        {
            Toast("Nothing to read");
            return;
        }
        if (text.Length > s.MaxChars && Choose("Read aloud", $"Read all {text.Length:N0} characters?", ("Read all", "all")) is null)
            return;
        if (Tts.Weights(voice).note is { } note)
            Toast(note, "#F7C873");
        StopSpeaking();
        var generation = ++_readGeneration;
        _speakingButton = button;
        if (button is not null)
            ShowSpeaking(button, true);
        try { await Player.Speak(SpeechText.Sentences(text), voice, s, CancellationToken.None); }
        catch (Exception exception)
        {
            Log($"Reading aloud failed: {exception.Message}");
            Toast(exception is InvalidOperationException ? exception.Message : "Reading aloud failed: " + exception.Message, "#FF667A");
        }
        if (generation == _readGeneration) // not taken over by a newer read meanwhile (the same reply's again too)
        {
            _speakingButton = null;
            if (button is not null)
                ShowSpeaking(button, false);
        }
    }

    // Idempotent: the player stops (the device is released when its Speak ends) and the button goes back to speak.
    // A call's reply is not stopped here (switching views, a new reply...): only the call's own interrupt or end stops it.
    private void StopSpeaking()
    {
        if (_callSentences is null)
            _speechPlayer?.Stop();
        _cbPlayer?.Stop(); // a Chatterbox voice's preview
        if (_speakingButton is { } button)
            ShowSpeaking(button, false);
        _speakingButton = null;
    }

    // ---- Settings > Voice > Speech output

    private void RefreshSpeakOut()
    {
        var ready = TtsModels.Ready || _speechTest;
        SpeakOutPanel.IsEnabled = ready;
        SpeakOutPanel.Opacity = ready ? 1 : 0.5;
        SpeakOutHint.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        RefreshChatterboxVoices();
    }

    private void BuildSpeakOut()
    {
        var p = SpeakOutPanel;
        p.Children.Clear();
        _voicePickers.Clear();
        var t = _settings.Tts;
        void Save() => _settings.Save();

        p.Children.Add(Row("Read replies aloud", Toggle(t.AutoRead, on => { t.AutoRead = on; Save(); })));
        p.Children.Add(Note("Each finished reply is read with its worker's voice. A new reply, Stop, or switching conversation ends it."));
        var main = VoicePicker(false);
        main.SelectionChanged += (_, _) =>
        {
            if (main.SelectedItem is ComboBoxItem { Tag: string name } && name != t.Voice)
                t.Voice = name;
            Save();
        };
        p.Children.Add(Row("Voice", main));
        p.Children.Add(Row("Speed", SliderWith(0.5, 2.0, 0.05, t.Speed, v => $"{v:0.00}×", v => { t.Speed = v; Save(); }, "Speed")));
        p.Children.Add(Row("Volume", SliderWith(0, 1, 0.05, t.Volume, v => $"{v * 100:0}%", v => { t.Volume = v; Save(); }, "Volume")));
        p.Children.Add(Row("Pause between sentences", SliderWith(0, 1000, 50, t.SentencePauseMs, v => $"{v:0} ms", v => { t.SentencePauseMs = (int)v; Save(); }, "Pause between sentences")));
        var test = new TextBox { Text = "", Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 30 };
        AutomationProperties.SetName(test, "Type something to hear it");
        var play = new Button { Content = "Play", Padding = new Thickness(11, 6, 11, 6) };
        play.Click += (_, _) => Read(null, test.Text.Length > 0 ? test.Text : "Type something to hear it.", VoiceChoice.Find(t.Voice, _settings.CustomVoices));
        p.Children.Add(new DockPanel { Margin = new Thickness(0, 14, 0, 0), Children = { Docked(play, Dock.Right), test } });
        var device = new ComboBox { Width = 240 };
        AutomationProperties.SetName(device, "Speaker");
        void FillDevices()
        {
            List<string> names;
            try { names = SpeechPlayer.Speakers(); }
            catch (NAudio.MmException) { names = []; }
            if (t.Device.Length > 0 && !names.Contains(t.Device))
                names.Add(t.Device); // unplugged: kept so the choice is not lost (reading says it is not connected)
            device.Items.Clear();
            device.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = "" });
            foreach (var name in names)
                device.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            device.SelectedItem = device.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == t.Device);
        }
        FillDevices();
        device.DropDownOpened += (_, _) => FillDevices();
        device.SelectionChanged += (_, _) =>
        {
            if (device.SelectedItem is ComboBoxItem { Tag: string name } && name != t.Device)
            {
                t.Device = name;
                Save();
            }
        };
        p.Children.Add(Row("Speaker", device));
        p.Children.Add(Row("Interrupt by talking (use headphones)", Toggle(t.CallBargeIn, on => { t.CallBargeIn = on; Save(); })));
        p.Children.Add(Note("In a call the microphone stays on while the reply is spoken, and your voice stops it. Off: Space, Esc or a click on the call's circle interrupts."));

        p.Children.Add(Heading("What gets read"));
        p.Children.Add(Row("Skip code blocks", Toggle(t.SkipCode, on => { t.SkipCode = on; Save(); })));
        p.Children.Add(Row("Read headings", Toggle(t.ReadHeadings, on => { t.ReadHeadings = on; Save(); })));
        p.Children.Add(Row("Read tables as \"a table with N rows\"", Toggle(t.TableSummary, on => { t.TableSummary = on; Save(); })));
        p.Children.Add(Row("Read the thinking", Toggle(t.ReadThinking, on => { t.ReadThinking = on; Save(); })));
        var max = new TextBox { Text = t.MaxChars.ToString(), Width = 90, TextAlignment = TextAlignment.Right };
        AutomationProperties.SetName(max, "Ask before reading more than");
        max.LostFocus += (_, _) =>
        {
            if (int.TryParse(max.Text.Replace(",", ""), out var n) && n >= 100)
                t.MaxChars = n;
            max.Text = t.MaxChars.ToString();
            Save();
        };
        p.Children.Add(Row("Ask before reading more than (characters)", max));

        p.Children.Add(Heading("Voice per worker"));
        p.Children.Add(Note("A worker can read with its own voice; the others use the voice above."));
        foreach (var card in _cards.Where(k => !k.Hidden))
        {
            var role = card.Role;
            var picker = VoicePicker(true, role);
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not ComboBoxItem { Tag: string name })
                    return;
                if (name.Length == 0)
                    t.WorkerVoices.Remove(role);
                else
                    t.WorkerVoices[role] = name;
                Save();
            };
            p.Children.Add(Row(ChatName(card, role), picker));
        }

        p.Children.Add(Heading("Custom voices"));
        p.Children.Add(Note("Blend two to four voices: each slider sets how much of that voice is in the mix. Hear it, name it and save it."));
        _blendRows = new StackPanel();
        _blendName = new TextBox { Width = 200, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 30 };
        AutomationProperties.SetName(_blendName, "Custom voice name");
        var add = new Button { Content = "Add a voice", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => { if (_blendRows.Children.Count < 4) AddBlendRow(Tts.Voices[0].Name, 0.5); };
        var hear = new Button { Content = "▶ Play", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 8, 0) };
        hear.Click += (_, _) => Read(null, "This is how the blend sounds.", new VoiceChoice(_blendName.Text.Length > 0 ? _blendName.Text : "blend", BlendMix()));
        var save = new Button { Content = "Save", Padding = new Thickness(11, 6, 11, 6) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) =>
        {
            if (SaveBlend(_blendName.Text.Trim(), BlendMix(), _blendEditing) is null)
                Toast("Custom voice saved");
        };
        p.Children.Add(_blendRows);
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { add, _blendName, hear, save } });
        ResetBlendEditor();
        _savedVoices = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        p.Children.Add(_savedVoices);

        BuildChatterboxVoices(p);

        p.Children.Add(Heading("Voice library"));
        var search = new TextBox { Margin = new Thickness(0, 10, 0, 0), MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(search, "Search voices");
        _voiceLibrary = new StackPanel();
        search.TextChanged += (_, _) => FillLibrary(search.Text.Trim());
        p.Children.Add(search);
        p.Children.Add(_voiceLibrary);
        FillLibrary("");
        FillVoicePickers();
        RefreshSpeakOut();
    }


    // Every built-in voice by language and accent, with gender; ▶ previews it, Use makes it the main voice.
    private void FillLibrary(string filter)
    {
        _voiceLibrary!.Children.Clear();
        foreach (var group in Tts.Voices.Where(v => filter.Length == 0 || $"{v.Name} {v.Language} {v.Accent} {v.Gender}".Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(v => v.Accent.Length > 0 ? $"{v.Language} · {v.Accent}" : v.Language))
        {
            var header = new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) };
            _voiceLibrary.Children.Add(header);
            foreach (var voice in group)
            {
                var preview = GlyphButton(PlayGlyph, $"Preview {voice.Name}");
                preview.Click += (_, _) => Read(null, $"Hello, I'm {VoiceLabel(voice.Name)}. This is how I sound.", new VoiceChoice(voice.Name));
                var use = new Button { Content = "Use", Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(6, 0, 0, 0) };
                use.SetResourceReference(StyleProperty, "GhostButton");
                use.Click += (_, _) => { _settings.Tts.Voice = voice.Name; _settings.Save(); FillVoicePickers(); };
                var gender = new TextBlock { Text = voice.Gender, Width = 70, VerticalAlignment = VerticalAlignment.Center };
                gender.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                _voiceLibrary.Children.Add(new DockPanel
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    Children = { Docked(use, Dock.Right), Docked(preview, Dock.Right), Docked(gender, Dock.Right),
                        new TextBlock { Text = VoiceLabel(voice.Name), VerticalAlignment = VerticalAlignment.Center } },
                });
            }
        }
        if (_voiceLibrary.Children.Count == 0)
            _voiceLibrary.Children.Add(Note("No voice matches."));
    }

    // "af_heart" → "Heart (af_heart)"; a custom voice keeps its name.
    private static string VoiceLabel(string name) =>
        Tts.Voices.Any(v => v.Name == name) ? $"{char.ToUpper(name[3])}{name[4..]} ({name})" : name;

    private ComboBox VoicePicker(bool worker, string role = "")
    {
        var box = new ComboBox { Width = 240, Tag = worker ? role : null };
        AutomationProperties.SetName(box, worker ? $"Voice for {role}" : "Voice");
        _voicePickers.Add(box);
        return box;
    }

    // The pickers list the saved custom voices first, then every built-in; a worker's starts with "Same as the main voice".
    private void FillVoicePickers()
    {
        var t = _settings.Tts;
        foreach (var box in _voicePickers)
        {
            var role = box.Tag as string;
            var chosen = role is null ? t.Voice : t.WorkerVoices.GetValueOrDefault(role, "");
            box.Items.Clear();
            if (role is not null)
                box.Items.Add(new ComboBoxItem { Content = "Same as the main voice", Tag = "" });
            foreach (var c in _settings.CustomVoices)
                box.Items.Add(new ComboBoxItem { Content = c.Name + " (custom)", Tag = c.Name });
            foreach (var name in ChatterboxNames())
                box.Items.Add(new ComboBoxItem { Content = name + " (Chatterbox)", Tag = "cb:" + name });
            foreach (var v in Tts.Voices)
                box.Items.Add(new ComboBoxItem { Content = $"{VoiceLabel(v.Name)} · {v.Language}", Tag = v.Name });
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == chosen) ?? box.Items[0];
        }
        FillSavedVoices();
        FillChatterboxVoices();
    }

    private void FillSavedVoices()
    {
        if (_savedVoices is null)
            return;
        _savedVoices.Children.Clear();
        foreach (var custom in _settings.CustomVoices)
        {
            var name = custom.Name;
            var play = GlyphButton(PlayGlyph, $"Play {name}");
            play.Click += (_, _) => Read(null, $"Hello, I'm {name}.", new VoiceChoice(name, custom.Mix));
            var label = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var mix = new TextBlock { Text = "  " + string.Join(" + ", Tts.Weights(new VoiceChoice(name, custom.Mix)).weights.Select(kv => $"{kv.Key} {kv.Value * 100:0}%")),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            mix.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var rename = SmallButton("Rename");
            var edit = SmallButton("Edit");
            var delete = SmallButton("Delete");
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
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
                    if (keep && box.Text.Trim() != name && RenameVoice(name, box.Text.Trim()) is null)
                        return; // the lists were refilled
                    FillSavedVoices();
                }
                box.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Escape) Commit(e.Key == Key.Enter); };
                box.LostFocus += (_, _) => Commit(true);
            };
            edit.Click += (_, _) =>
            {
                _blendRows!.Children.Clear();
                foreach (var (voice, weight) in custom.Mix)
                    AddBlendRow(voice, weight);
                _blendName!.Text = name;
                _blendEditing = name;
            };
            delete.Click += (_, _) => DeleteVoice(name);
            row.Children.Add(Docked(delete, Dock.Right));
            row.Children.Add(Docked(edit, Dock.Right));
            row.Children.Add(Docked(rename, Dock.Right));
            row.Children.Add(Docked(play, Dock.Left));
            row.Children.Add(Docked(label, Dock.Left));
            row.Children.Add(mix);
            _savedVoices.Children.Add(row);
        }
    }

    private Button SmallButton(string text)
    {
        var b = new Button { Content = text, Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(6, 0, 0, 0) };
        b.SetResourceReference(StyleProperty, "GhostButton");
        return b;
    }

    private void ResetBlendEditor()
    {
        _blendRows!.Children.Clear();
        AddBlendRow("af_heart", 0.5);
        AddBlendRow("bm_george", 0.5);
        _blendName!.Text = "";
        _blendEditing = null;
    }

    // One voice of the blend: which built-in, its weight (0–1), and × (while more than two).
    private void AddBlendRow(string voice, double weight)
    {
        var box = new ComboBox { Width = 220 };
        AutomationProperties.SetName(box, "Blend voice");
        foreach (var v in Tts.Voices)
            box.Items.Add(new ComboBoxItem { Content = $"{VoiceLabel(v.Name)} · {v.Language}", Tag = v.Name });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == voice) ?? box.Items[0];
        var slider = SliderWith(0, 1, 0.05, Math.Clamp(weight, 0, 1), v => $"{v * 100:0}", _ => { }, "Weight");
        var remove = GlyphButton("", "Remove from the blend");
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Tag = (box, (Slider)((DockPanel)slider).Children[1]) };
        remove.Click += (_, _) => { if (_blendRows!.Children.Count > 2) _blendRows.Children.Remove(row); };
        row.Children.Add(Docked(remove, Dock.Right));
        row.Children.Add(Docked(box, Dock.Left));
        row.Children.Add(slider);
        _blendRows!.Children.Add(row);
    }

    private Dictionary<string, double> BlendMix()
    {
        var mix = new Dictionary<string, double>();
        foreach (var (box, slider) in _blendRows!.Children.OfType<DockPanel>().Select(r => ((ComboBox, Slider))r.Tag))
            if (box.SelectedItem is ComboBoxItem { Tag: string name })
                mix[name] = mix.GetValueOrDefault(name) + slider.Value;
        return mix;
    }

    // Saves a blend (replacing: the saved voice it was edited from). Null when saved, else the reason, also toasted: no name, a
    // built-in's name (it would read as that voice), another custom's name, or fewer than two voices with weight.
    private string? SaveBlend(string name, Dictionary<string, double> mix, string? replacing)
    {
        string? problem = name.Length == 0 ? "Name the custom voice"
            : Tts.Voices.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? $"\"{name}\" is a built-in voice's name: choose another"
            : _settings.CustomVoices.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && c.Name != replacing) ? $"A custom voice is already called \"{name}\""
            : ChatterboxNames().Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)) ? $"A Chatterbox voice is already called \"{name}\""
            : mix.Count(kv => kv.Value > 0) is < 2 or > 4 ? "Blend two to four voices, each with some weight"
            : null;
        if (problem is not null)
        {
            Toast(problem, "#F7C873");
            return problem;
        }
        var voice = new CustomVoice { Name = name, Mix = mix.Where(kv => kv.Value > 0).ToDictionary() };
        var at = _settings.CustomVoices.FindIndex(c => c.Name == replacing);
        if (at >= 0)
        {
            _settings.CustomVoices[at] = voice;
            Repoint(replacing!, name);
        }
        else
            _settings.CustomVoices.Add(voice);
        _settings.Save();
        if (_blendRows is not null)
            ResetBlendEditor();
        FillVoicePickers();
        return null;
    }

    private string? RenameVoice(string from, string to)
    {
        var custom = _settings.CustomVoices.First(c => c.Name == from);
        return SaveBlend(to, custom.Mix, from);
    }

    private void DeleteVoice(string name)
    {
        if (Choose("Delete custom voice", $"Delete \"{name}\"?", ("Delete", "delete")) is null)
            return;
        _settings.CustomVoices.RemoveAll(c => c.Name == name);
        Repoint(name, null);
        _settings.Save();
        FillVoicePickers();
    }

    // A renamed voice stays chosen where it was; a deleted one goes back to the default (main) or to the main voice (a worker).
    private void Repoint(string from, string? to)
    {
        var t = _settings.Tts;
        if (t.Voice == from)
            t.Voice = to ?? Tts.Default;
        foreach (var role in t.WorkerVoices.Where(kv => kv.Value == from).Select(kv => kv.Key).ToList())
            if (to is null)
                t.WorkerVoices.Remove(role);
            else
                t.WorkerVoices[role] = to;
    }

    // ---- small builders (the Settings pages' look: a label left, the control right, muted notes)

    private static DockPanel Row(string label, FrameworkElement control) =>
        new() { Margin = new Thickness(0, 14, 0, 0), Children = { Docked(control, Dock.Right), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };

    private static T Docked<T>(T element, Dock dock) where T : UIElement
    {
        DockPanel.SetDock(element, dock);
        if (element is FrameworkElement { VerticalAlignment: VerticalAlignment.Stretch } f)
            f.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return note;
    }

    private TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 26, 0, 0) };
        heading.FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont");
        return heading;
    }

    private ToggleButton Toggle(bool on, Action<bool> changed)
    {
        var toggle = new ToggleButton { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        toggle.SetResourceReference(StyleProperty, "ToggleSwitch");
        toggle.Click += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    // A slider with its value shown at the right; changed runs on each move (the value rounded to the step).
    private static FrameworkElement SliderWith(double min, double max, double step, double value, Func<double, string> show, Action<double> changed, string name)
    {
        var label = new TextBlock { Width = 60, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = show(value) };
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Value = value, Width = 200, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, name);
        slider.ValueChanged += (_, e) =>
        {
            var v = Math.Round(e.NewValue / step) * step;
            label.Text = show(v);
            changed(v);
        };
        return new DockPanel { Children = { Docked(label, Dock.Right), slider } };
    }
}

// --ui-smoke: a stub engine and a fake output (never real audio). The speak button is on each reply and turns into Stop; a second
// reply's speak stops the first; Stop (the button, ShowView) stops; Read replies aloud reads a finished reply only when on, with
// its worker's voice; over MaxChars asks first; a blend saves, renames and deletes and shows in the voice pickers; a built-in's
// name is refused; the settings persist (AppSettings.Load); the voice model choice (Full quality / Compact) is Ready per build and switches.
public partial class MainWindow
{
    private async Task<bool> SpeakOutSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: speak out: {what}"); return false; }
        if (!_chatterboxStatus.Text.StartsWith("Not installed") || _chatterboxInstallButton.Visibility != Visibility.Visible || (string)_chatterboxInstallButton.Content != "Install"
            || _chatterboxRemoveButton.Visibility == Visibility.Visible)
            return Fail($"the Chatterbox row is not 'Not installed' with Install: '{_chatterboxStatus.Text}'");
        var (player, test, choice) = (_speechPlayer, _speechTest, _codeChoice);
        var saved = System.Text.Json.JsonSerializer.Serialize(_settings.Tts);
        var savedVoices = _settings.CustomVoices.ToList();
        var spoken = new List<string>();
        float[] Stub(string text, VoiceChoice voice, float speed, CancellationToken ct)
        {
            lock (spoken)
                spoken.Add($"{voice.Name}|{text}");
            return new float[Tts.Rate / 2]; // half a second: the fake output plays it in about 25 ms
        }
        int Count() { lock (spoken) return spoken.Count; }
        async Task<bool> Until(Func<bool> done)
        {
            for (var i = 0; i < 50 && !done(); i++)
                await Task.Delay(40);
            return done();
        }
        _speechPlayer = new SpeechPlayer(Stub, () => new Tts.FakeOutput());
        _speechTest = true;
        try
        {
            NewChat_Click(this, new RoutedEventArgs());
            ShowView(chat: true);
            var longText = string.Join(" ", Enumerable.Range(0, 300).Select(i => $"Sentence {i} here."));
            var (turn, current, replies) = (_turn, _reply, new List<ChatReply>());
            _turn = _chat;
            foreach (var final in new[] { longText, "Reply two. It is short." })
            {
                var made = AddReply("planner");
                (made.Final, made.Footer.Visibility) = (final, Visibility.Visible);
                replies.Add(made);
            }
            (_turn, _reply) = (turn, current);
            if (replies.Count != 2 || replies.Any(r => r.Speak.Parent != r.Footer || (string)r.Speak.Tag != "speak"))
                return Fail($"no speak button on each reply's footer: {replies.Count} replies, tags {string.Join(",", replies.Select(r => r.Speak?.Tag))}, in footer {string.Join(",", replies.Select(r => r.Speak?.Parent == r.Footer))}");
            var (a, b) = (replies[0].Speak, replies[1].Speak);
            _settings.Tts.MaxChars = 100_000;
            a.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if ((string)a.Tag != "stop" || !await Until(() => Count() > 2))
                return Fail("speak did not turn into Stop, or nothing was read");
            b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if ((string)a.Tag != "speak" || (string)b.Tag != "stop")
                return Fail("a second reply's speak did not take over the button");
            if (!await Until(() => (string)b.Tag == "speak") || Count() >= 300 || !spoken[^1].EndsWith("It is short."))
                return Fail($"the second reply did not stop the first ({Count()} read, last {spoken[^1]})");
            spoken.Clear();
            a.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => Count() > 1);
            a.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // Stop
            var stoppedAt = Count();
            await Task.Delay(300);
            if ((string)a.Tag != "speak" || _speechPlayer.Speaking || Count() > stoppedAt + 1)
                return Fail("Stop did not stop");
            a.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ShowView(chat: false);
            await Task.Delay(200);
            if ((string)a.Tag != "speak" || _speechPlayer.Speaking)
                return Fail("switching view did not stop");
            ShowView(chat: true);
            // Read replies aloud: off reads nothing; on reads with the worker's voice
            spoken.Clear();
            _settings.Tts.AutoRead = false;
            ReadFinished(replies[1]);
            await Task.Delay(200);
            if (Count() != 0)
                return Fail("auto-read spoke while off");
            (_settings.Tts.AutoRead, _settings.Tts.WorkerVoices["planner"]) = (true, "bm_george");
            ReadFinished(replies[1]);
            if (!await Until(() => (string)b.Tag == "speak" && Count() == 2) || !spoken.All(s => s.StartsWith("bm_george|")))
                return Fail($"auto-read on: [{string.Join(" / ", spoken)}]");
            // over MaxChars asks once; Cancel reads nothing
            spoken.Clear();
            var asked = 0;
            _codeChoice = title => { asked++; return null; };
            _settings.Tts.MaxChars = 10;
            a.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(200);
            if (asked != 1 || Count() != 0 || (string)a.Tag != "speak")
                return Fail($"over MaxChars: asked {asked}, read {Count()}");
            // custom voices: a built-in's name refused; save, rename (the choice follows), delete (back to the default)
            _codeChoice = _ => "delete";
            var mix = new Dictionary<string, double> { ["af_bella"] = 0.5, ["af_sky"] = 0.5 };
            if (SaveBlend("AF_Heart", mix, null) is null || _settings.CustomVoices.Any(c => c.Name == "AF_Heart"))
                return Fail("a built-in voice's name was accepted");
            bool Listed(string name) => _voicePickers.Count > 1 && _voicePickers.All(p => p.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == name));
            if (SaveBlend("Smoke calm", mix, null) is not null || !Listed("Smoke calm"))
                return Fail("a blend did not save into the pickers");
            _settings.Tts.Voice = "Smoke calm";
            if (RenameVoice("Smoke calm", "Smoke soft") is not null || Listed("Smoke calm") || !Listed("Smoke soft") || _settings.Tts.Voice != "Smoke soft")
                return Fail("rename");
            if (AppSettings.Load() is not { } loaded || loaded.CustomVoices.All(c => c.Name != "Smoke soft") || loaded.Tts.Voice != "Smoke soft" || !loaded.Tts.AutoRead)
                return Fail("the settings did not persist");
            DeleteVoice("Smoke soft");
            if (Listed("Smoke soft") || _settings.CustomVoices.Any(c => c.Name == "Smoke soft") || _settings.Tts.Voice != Tts.Default
                || AppSettings.Load().CustomVoices.Any(c => c.Name == "Smoke soft"))
                return Fail("delete");

            // the voice model (stub files of the exact sizes in the smoke's data folder): Full by default; an int8-only install reads
            // on Compact and says Full is not downloaded; each build Ready on its own; switching persists, stops a reading and reads
            // nothing on its own; Delete is offered only for a downloaded build not chosen, and frees it
            static void Fake(TtsModel m)
            {
                Directory.CreateDirectory(m.Folder);
                foreach (var f in m.Files)
                    using (var file = File.Create(m.File(f.Name)))
                        file.SetLength(f.Size);
            }
            var (full, compact) = (_ttsRows[TtsModels.Full], _ttsRows[TtsModels.Compact]);
            static bool Shown(UIElement e) => e.Visibility == Visibility.Visible;
            if (_settings.Tts.Model != "full" || TtsModels.Ready || TtsModels.Active != TtsModels.Full || full.Pick.IsChecked != true || !Shown(full.Download) || !Shown(compact.Download))
                return Fail($"the voice model does not start at Full quality, nothing downloaded: {_settings.Tts.Model}, {TtsModels.Active.Key}");
            Fake(TtsModels.Compact);
            RefreshVoiceStatus();
            if (TtsModels.Active != TtsModels.Compact || !TtsModels.Ready || TtsModels.Full.Ready || !full.Status.Text.StartsWith("Full quality not downloaded")
                || !Shown(full.Download) || Shown(full.Delete) || Shown(compact.Download) || !Shown(compact.Delete) || compact.Status.Text != "Ready · reading")
                return Fail($"an int8-only install: reads with {TtsModels.Active.Key}, Full says [{full.Status.Text}], Compact [{compact.Status.Text}]");
            Fake(TtsModels.Full);
            RefreshVoiceStatus();
            if (TtsModels.Active != TtsModels.Full || full.Status.Text != "Ready · reading" || Shown(full.Download) || Shown(full.Delete) || !Shown(compact.Delete) || compact.Status.Text != "Downloaded")
                return Fail($"both downloaded: reads with {TtsModels.Active.Key}, Full [{full.Status.Text}], Compact [{compact.Status.Text}]");
            _settings.Tts.MaxChars = 100_000;
            a.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => Count() > 1);
            compact.Pick.IsChecked = true;
            var switchedAt = Count();
            await Task.Delay(300);
            if (_settings.Tts.Model != "compact" || TtsModels.Chosen != "compact" || TtsModels.Active != TtsModels.Compact || AppSettings.Load().Tts.Model != "compact")
                return Fail($"switching to Compact: chose {_settings.Tts.Model}, reads with {TtsModels.Active.Key}, saved {AppSettings.Load().Tts.Model}");
            if (_speechPlayer.Speaking || (string)a.Tag != "speak" || Count() > switchedAt + 1)
                return Fail($"switching the model did not stop the reading, or read on its own ({Count() - switchedAt} after)");
            if (!Shown(full.Delete) || Shown(compact.Delete))
                return Fail("Delete after switching to Compact");
            DeleteTtsModel(TtsModels.Full);
            if (TtsModels.Full.Ready || Directory.Exists(TtsModels.Full.Folder) || TtsModels.Active != TtsModels.Compact || !Shown(full.Download) || Shown(full.Delete))
                return Fail("deleting Full quality");
            return true;
        }
        finally
        {
            StopSpeaking();
            _speechPlayer.Dispose();
            (_speechPlayer, _speechTest, _codeChoice) = (player, test, choice);
            _settings.Tts = System.Text.Json.JsonSerializer.Deserialize<TtsSettings>(saved)!;
            _settings.CustomVoices = savedVoices;
            _settings.Save();
            foreach (var model in TtsModels.All)
                if (Directory.Exists(model.Folder))
                    Directory.Delete(model.Folder, recursive: true);
            _ttsRows[TtsModels.Find(_settings.Tts.Model)].Pick.IsChecked = true; // its handler sees the restored setting: nothing saved
            TtsModels.Chosen = _settings.Tts.Model;
            RefreshVoiceStatus();
            FillVoicePickers();
            NewChat_Click(this, new RoutedEventArgs());
        }
    }
}

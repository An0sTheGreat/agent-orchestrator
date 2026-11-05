using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrchestrator;

// The selected worker tab's model settings (MainWindow.WorkerTabs.cs hosts them inline). Model, load, sampling and request
// options (ModelOptions); a built-in role's remote API fields and optional chat system prompt; a custom worker's name, kind,
// description, system prompt and removal. Built-in roles write config.json on Save; custom workers keep their options in the
// launcher's settings and inherit what they leave empty from the Implementer.
public partial class MainWindow
{
    // Shows the Chat prompt section in the Chat view and the fixed workflow prompt section in the Code view.
    private void ApplyPromptView(Panel? sections = null)
    {
        var chat = NavChat.IsChecked == true;
        foreach (var section in (sections ?? WorkerSettingsHost.Child as Panel) is { } root ? LogicalTreeHelper.GetChildren(root).OfType<FrameworkElement>().SelectMany(Flatten) : [])
            if (section.Tag is "ChatPromptSection" or "WorkflowPromptSection")
                section.Visibility = (section.Tag as string == "ChatPromptSection") == chat ? Visibility.Visible : Visibility.Collapsed;
        static IEnumerable<FrameworkElement> Flatten(FrameworkElement e) =>
            LogicalTreeHelper.GetChildren(e).OfType<FrameworkElement>().SelectMany(Flatten).Prepend(e);
    }

    // A built-in worker's editable prompts (prompts/<file>, overridden in <data>\prompts\<file>; orchestrator.prompt_text).
    private static (string Label, string File)[] RolePromptFiles(string role) => role switch
    {
        "planner" => [("Code", "lead.txt"), ("Code with Pi", "lead_pi.txt"), ("Start project", "lead_project.txt"), ("Step-by-step pipeline", "planner.txt")],
        "implementer" => [("Delegated tasks", "sub_implementer.txt"), ("Step-by-step pipeline", "implementer.txt")],
        "reviewer" => [("Delegated reviews", "sub_reviewer.txt"), ("Step-by-step pipeline", "reviewer.txt")],
        "compactor" => [("Summaries", "compactor.txt")],
        _ => [],
    };

    private static string PromptOverridePath(string file) => Path.Combine(AppSettings.DataRoot, "prompts", file);

    private string DefaultPromptText(string file)
    {
        try { return _workflowRoot is null ? "" : File.ReadAllText(Path.Combine(_workflowRoot, "prompts", file)).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static string? OverrideText(string file)
    {
        try { return File.Exists(PromptOverridePath(file)) && File.ReadAllText(PromptOverridePath(file)).Trim() is { Length: > 0 } text ? text : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // One prompt: its label (", edited" while it differs from the default) with the edit and reset buttons at the right, over
    // the box's top-right corner; the text is read-only until edit unlocks it.
    private FrameworkElement PromptEditor(string label, string file, List<(string File, TextBox Box)> edits)
    {
        var fallback = DefaultPromptText(file);
        var box = new TextBox
        {
            Text = OverrideText(file) ?? fallback, Tag = "Prompt:" + file, Height = 180, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 6, 8, 6),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsReadOnly = true,
            VerticalContentAlignment = VerticalAlignment.Top, FontFamily = AppFonts.Mono,
        };
        box.SetResourceReference(ForegroundProperty, "Muted");
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, ToolTip = $"prompts/{file}" };
        void Title() => title.Text = label + (box.Text.Trim() == fallback ? "" : "  ·  edited");
        Button Icon(string glyph, string tag, string tip)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, Style = (Style)FindResource("RailGlyph"), FontSize = 13 }, Tag = tag, ToolTip = tip,
                Style = (Style)FindResource("GhostButton"), Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }
        var editButton = Icon("\uE70F", "EditPrompt", "Edit this prompt");
        var reset = Icon("\uE72C", "ResetPrompt", "Restore the default prompt");
        void Lock(bool locked)
        {
            box.IsReadOnly = locked;
            box.SetResourceReference(ForegroundProperty, locked ? "Muted" : "TextPrimary");
        }
        editButton.Click += (_, _) =>
        {
            Lock(!box.IsReadOnly);
            if (!box.IsReadOnly)
                box.Focus();
        };
        reset.Click += (_, _) =>
        {
            box.Text = fallback;
            Lock(true);
        };
        box.TextChanged += (_, _) => Title();
        Title();
        edits.Add((file, box));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { editButton, reset } };
        DockPanel.SetDock(buttons, Dock.Right);
        // the buttons sit over the box's top-right corner, on the title's line, so they never cover its text
        return new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { new DockPanel { Children = { buttons, title } }, box } };
    }

    // Writes each edited prompt to <data>\prompts (one back at its default removes its file); false when a write failed.
    private bool SavePromptOverrides(IEnumerable<(string File, TextBox Box)> edits)
    {
        try
        {
            foreach (var (file, box) in edits)
            {
                var target = PromptOverridePath(file);
                if (box.Text.Trim() == DefaultPromptText(file))
                {
                    if (File.Exists(target))
                        File.Delete(target);
                }
                else if (box.Text.Trim() != OverrideText(file))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, box.Text.Trim() + "\n");
                    Log($"Prompt saved: {target}");
                }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not save a prompt: {e.Message}");
            return false;
        }
    }

    private static void WriteJson(string path, JsonNode root) => File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));

    // The settings body, or null when a built-in role has no readable config.json. saved: after a successful Save; close:
    // closeLabel's button, Remove worker, or a config.json that could not be written. IsDirty: a field differs from what the
    // body was built with. Save: the Save button; true when it wrote the edits. The radio buttons share fixed group names,
    // so host one body at a time.
    private (FrameworkElement Body, Func<bool> IsDirty, Func<bool> Save)? ModelSettingsBody(RoleCard card, Action saved, Action close, string closeLabel)
    {
        var path = card.IsCustom ? "" : ActiveConfigPath();
        JsonObject? role = null;
        LauncherLogic.RemoteRole? remoteNow = null;
        var followsMain = false; // COMPACTOR on MAIN's model: MAIN's settings show, and Save gives it its own only when they change
        if (!card.IsCustom)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                var roles = JsonNode.Parse(File.ReadAllText(path))!["roles"]!.AsObject();
                followsMain = card.Role == LauncherLogic.Compactor && !LauncherLogic.CompactorHasOwn(roles);
                role = roles[followsMain ? "planner" : card.Role]!.AsObject();
                remoteNow = LauncherLogic.ReadRemoteRole(path, followsMain ? "planner" : card.Role);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
            {
                Log($"Could not read config.json: {exception.Message}");
                return null;
            }
        }
        var current = role is null ? ModelOptions.Effective(card.Config) : ModelOptions.Read(role);
        // Absent keys show their default (ModelOptions.Defaults): the model's own (its GGUF header, read off the UI thread by
        // Derive) or the static one; for a custom worker the Implementer's value first; for a remote role none. auto: the
        // fields showing a default, with that value; while one still shows it, a new model read, model pick or mode switch
        // swaps in the new default.
        var w0 = card.Config;
        JsonObject? LoadOf(ModelChoice? c) => c is not null ? new JsonObject { ["model_path"] = c.LoadPath, ["gguf_variant"] = c.Variant, ["gguf_file"] = c.File }
            : role is not null ? role["auto_load"] as JsonObject
            : w0.ModelPath is { } p ? new JsonObject { ["model_path"] = p, ["gguf_variant"] = w0.ModelVariant, ["gguf_file"] = w0.GgufFile } : null;
        var inherited = card.IsCustom ? LauncherLogic.InheritedOptions(ActiveConfigPath(), Path.Combine(_workflowRoot ?? "", "config.json")) : null;
        var startLoad = LoadOf(null);
        var hasModel = startLoad?["model_path"] is not null;
        Dictionary<string, string>? meta = null;
        (long Model, long Vision) files = (0, 0);
        RadioButton[]? modes = null;
        var remoteMode = () => modes is null ? remoteNow is not null : modes[1].IsChecked == true;
        Dictionary<string, string> Base() => remoteMode() ? new() : ModelOptions.Defaults(meta, hasModel, inherited);
        var shownAtStart = ModelOptions.Prefill(current, Base());
        var auto = ModelOptions.All.Where(o => current.GetValueOrDefault(o.Key, "").Trim().Length == 0).ToDictionary(o => o.Key, o => shownAtStart[o.Key]);
        var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A));
        var emptyMeans = card.IsCustom ? "Empty = the Implementer's value." : "Empty = the default (the key is removed).";

        TextBlock Muted(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 10.5, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }
        TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12 };
        TextBlock ErrorLine() => new() { Foreground = red, FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        TextBox Box(string text, string tag, double height = 30) => new()
        {
            Text = text, Tag = tag, Height = height, Margin = new Thickness(0, 4, 0, 0),
            Padding = height > 30 ? new Thickness(8, 6, 8, 6) : new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = height > 30 ? VerticalAlignment.Top : VerticalAlignment.Center, AcceptsReturn = height > 30,
            TextWrapping = height > 30 ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // The groups of related settings, one column or two (SettingsColumns), added in SettingsGroups' order once built: the
        // Model group beside the worker's own (HeadGroups) on top, then a line, then the option groups.
        var head = new SettingsColumns();
        var sections = new SettingsColumns();
        var divider = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 18) };
        divider.SetResourceReference(Border.BackgroundProperty, "Border");
        var body = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Children = { head, divider, sections } };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var blocks = new Dictionary<string, StackPanel>();
        // A group's header in small capitals; its note shows below it, or (showHelp false: an option group's) on hover.
        StackPanel Section(string name, string help, bool showHelp = true)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
            var header = new TextBlock { Text = name.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), ToolTip = help };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            panel.Children.Add(header);
            if (showHelp)
                panel.Children.Add(Muted(help));
            return blocks[name] = panel;
        }

        var editors = new Dictionary<string, Control>();
        var fieldErrors = new Dictionary<string, TextBlock>();
        var hints = new Dictionary<string, TextBlock>();
        string Value(Control c) => c is ComboBox b ? b.SelectedItem is string s && s != "(default)" ? s : "" : ((TextBox)c).Text;
        void SetValue(Control c, string v)
        {
            if (c is not ComboBox b)
                ((TextBox)c).Text = v;
            else
            {
                if (v.Length > 0 && !b.Items.Contains(v))
                    b.Items.Add(v);
                b.SelectedItem = v.Length > 0 ? v : "(default)";
            }
        }
        void ContextHint(Dictionary<string, string>? meta)
        {
            if (hints.TryGetValue("max_seq_length", out var contextHint)) // built with the Model fields
                contextHint.Text = ModelOptions.All.First(o => o.Key == "max_seq_length").Help +
                (ModelOptions.ModelContext(meta) is { } max ? $". Model maximum: {max.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}" : "");
            SliderRange(meta);
        }
        var rows = new Dictionary<string, StackPanel>();
        var sliderRanges = new Dictionary<string, Action<long>>();
        // Context length: Auto (0) to the model's maximum, in 1,024-token steps. GPU layers: 0 to every layer (empty or -1
        // show as all). The box and the slider follow each other; SliderRange sets the range from the model.
        FrameworkElement SliderFor(string key, TextBox box)
        {
            var context = key == "max_seq_length";
            var slider = new Slider { Minimum = 0, Maximum = context ? 131072 : 100, TickFrequency = context ? 1024 : 1, IsSnapToTickEnabled = true,
                SmallChange = context ? 1024 : 1, LargeChange = context ? 8192 : 4, Tag = key + "_slider", Margin = new Thickness(0, 6, 0, 0) };
            var max = Muted("");
            DockPanel.SetDock(max, Dock.Right);
            var ends = new DockPanel { Children = { max, Muted(context ? "Auto" : "0") } };
            var syncing = false;
            void FromBox()
            {
                syncing = true;
                slider.Value = long.TryParse(box.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0
                    ? v : context ? 0 : slider.Maximum;
                syncing = false;
            }
            slider.ValueChanged += (_, _) =>
            {
                if (!syncing)
                    box.Text = ((long)slider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            };
            box.TextChanged += (_, _) =>
            {
                if (!syncing)
                    FromBox();
            };
            sliderRanges[key] = top =>
            {
                syncing = true;
                slider.Maximum = Math.Max(1, top);
                max.Text = top.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                syncing = false;
                FromBox();
            };
            sliderRanges[key](context ? 131072 : 100);
            return new StackPanel { Children = { slider, ends } };
        }
        // The sliders' ranges from the model's GGUF header: its context window, its layer count (+1: the output layer).
        void SliderRange(Dictionary<string, string>? meta)
        {
            sliderRanges.GetValueOrDefault("max_seq_length")?.Invoke(ModelOptions.ModelContext(meta) ?? 131072);
            var layers = meta?.GetValueOrDefault("general.architecture") is { } arch && long.TryParse(meta.GetValueOrDefault(arch + ".block_count"), out var n) ? n + 1 : 100;
            sliderRanges.GetValueOrDefault("gpu_layers")?.Invoke(layers);
        }
        StackPanel Fields(string group)
        {
            var fields = new StackPanel();
            foreach (var o in ModelOptions.All.Where(o => SettingsGroupOf(o.Key) == group))
            {
                var wide = o.Kind is ModelOptions.Kind.Multiline or ModelOptions.Kind.StopList or ModelOptions.Kind.ArgList;
                var value = shownAtStart[o.Key];
                Control editor;
                if (o.Values.Length > 0)
                {
                    var combo = new ComboBox { Tag = o.Key, Margin = new Thickness(0, 4, 0, 0) };
                    combo.Items.Add("(default)");
                    foreach (var v in o.Values)
                        combo.Items.Add(v);
                    if (value.Length > 0 && !o.Values.Contains(value))
                        combo.Items.Add(value); // as found in config.json; Save names the accepted values
                    combo.SelectedItem = value.Length > 0 ? value : "(default)";
                    editor = combo;
                }
                else
                {
                    var box = Box(value, o.Key, wide ? 76 : 30);
                    if (o.Kind is ModelOptions.Kind.Multiline or ModelOptions.Kind.ArgList)
                        box.FontFamily = AppFonts.Mono;
                    editor = box;
                }
                editors[o.Key] = editor;
                // The hover tip: what the option does, the default the field shows when empty (it follows the model), its range
                // or format, and the config key. Built when it opens, so the default is the current one.
                string Tip()
                {
                    var shown = Base().GetValueOrDefault(o.Key, "") is { Length: > 0 } d ? d : o.Default ?? "";
                    return $"{ModelOptions.Descriptions.GetValueOrDefault(o.Key, o.Help)}\n\nDefault: {(shown.Length > 0 ? shown : "none (the server or model decides)")}\n{o.Help}  ·  {o.Key}";
                }
                TextBlock TipBlock() => new() { Text = Tip(), MaxWidth = 340, TextWrapping = TextWrapping.Wrap };
                hints[o.Key] = Muted(o.Help);
                if (wide)
                {
                    fields.Children.Add(rows[o.Key] = new StackPanel
                    {
                        Margin = new Thickness(0, 12, 0, 0), Children = { Label(o.Label), hints[o.Key], editor, (fieldErrors[o.Key] = ErrorLine()) }, ToolTip = TipBlock(),
                    });
                    rows[o.Key].ToolTipOpening += (_, _) => rows[o.Key].ToolTip = TipBlock();
                    continue;
                }
                // Label left, a compact control right; the help shows on hover.
                (editor.Width, editor.Margin, editor.HorizontalAlignment) = (o.Kind == ModelOptions.Kind.Text ? 170 : 120, new Thickness(12, 0, 0, 0), HorizontalAlignment.Right);
                var name = Label(o.Label);
                name.VerticalAlignment = VerticalAlignment.Center;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                var line = new DockPanel();
                DockPanel.SetDock(editor, Dock.Right);
                line.Children.Add(editor);
                line.Children.Add(name);
                hints[o.Key].Visibility = Visibility.Collapsed;
                var row = rows[o.Key] = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { line }, ToolTip = TipBlock() };
                row.ToolTipOpening += (_, _) => row.ToolTip = TipBlock();
                if (o.Key is "max_seq_length" or "gpu_layers" && editor is TextBox sliderBox)
                    row.Children.Add(SliderFor(o.Key, sliderBox));
                row.Children.Add(hints[o.Key]);
                row.Children.Add(fieldErrors[o.Key] = ErrorLine());
                fields.Children.Add(row);
            }
            return fields;
        }

        // Custom workers: what the card's menu and the old Settings dialog offered.
        TextBox? nameBox = null, description = null, prompt = null;
        RadioButton[]? kinds = null;
        if (card.IsCustom)
        {
            var w = card.Config;
            var panel = Section("Worker", "This custom worker's name, kind and standing instructions.");
            nameBox = Box(card.Title, "Name");
            nameBox.MaxLength = 32;
            kinds = new[] { ("specialist", "Specialist"), ("reviewer", "Extra reviewer") }.Select(k => new RadioButton
            {
                Content = k.Item2, Tag = k.Item1, GroupName = "WorkerKind", IsChecked = (w.Kind == "reviewer" ? "reviewer" : "specialist") == k.Item1, Margin = new Thickness(0, 0, 18, 0),
            }).ToArray();
            var kindHelp = Muted("");
            void Explain() => kindHelp.Text = kinds[0].IsChecked == true
                ? "Specialist: the Planner can assign it tasks, and it edits files like the Implementer."
                : "Extra reviewer: reviews every task alongside the Reviewer; its FAIL sends the task back.";
            // Empty fields show the kind's defaults; switching kind swaps a default (or empty) field for the other kind's.
            string KindOf(RadioButton r) => r.Tag as string == "reviewer" ? "reviewer" : "specialist";
            var startKind = w.Kind == "reviewer" ? "reviewer" : "specialist";
            description = Box(string.IsNullOrWhiteSpace(w.Description) ? LauncherLogic.DefaultDescription(startKind) : w.Description, "Description", 58);
            prompt = Box(string.IsNullOrWhiteSpace(w.SystemPrompt) ? LauncherLogic.DefaultSystemPrompt(startKind) : w.SystemPrompt, "SystemPrompt", 170);
            foreach (var k in kinds)
                k.Checked += (_, _) =>
                {
                    Explain();
                    foreach (var (box, of) in new (TextBox, Func<string, string>)[] { (description, LauncherLogic.DefaultDescription), (prompt, LauncherLogic.DefaultSystemPrompt) })
                        if (string.IsNullOrWhiteSpace(box.Text) || kinds.Any(r => box.Text == of(KindOf(r))))
                            box.Text = of(KindOf(k));
                };
            Explain();
            prompt.FontFamily = AppFonts.Mono;
            var remove = new Button { Content = "Remove worker", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
            remove.Click += (_, _) =>
            {
                if (AskDialog("Remove worker", $"Remove {card.Title}? Its settings are deleted.", "Remove") is null)
                    return;
                HoldWorkerTab(() =>
                {
                    RemoveWorker(card); // first: the Models window asks before closing only while its worker exists
                    close();
                });
                Log($"{card.Title}: removed");
            };
            foreach (var part in new UIElement[]
                     {
                         new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { Label("Name"), nameBox, (fieldErrors["Name"] = ErrorLine()) } },
                         new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { Label("Kind"), new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { kinds[0], kinds[1] } }, kindHelp } },
                         new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { Label("Description"), Muted("Shown to the Planner: it assigns Specialist tasks based on this, so say what the worker is for."), description, (fieldErrors["Description"] = ErrorLine()) } },
                         new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { Label("System prompt"), Muted("The worker's standing instructions."), prompt, (fieldErrors["SystemPrompt"] = ErrorLine()) } },
                         remove,
                     })
                panel.Children.Add(part);
        }

        // Built-in roles: Local (llama.cpp) or a remote OpenAI-compatible API.
        TextBox[]? remoteBoxes = null;
        TextBox? chatPrompt = null;
        var promptEdits = new List<(string File, TextBox Box)>(); // the editable workflow prompts (PromptEditor)
        StackPanel? remotePanel = null, source = null;
        if (!card.IsCustom)
        {
            // The Chat view: an optional prompt of the user's. The Code view: the role's fixed workflow prompts, read only
            // (the multi-agent workflow depends on them). ApplyPromptView shows the one for the view on screen.
            chatPrompt = Box(followsMain ? "" : (string?)role!["chat_system_prompt"] ?? "", "ChatSystemPrompt", 120);
            chatPrompt.FontFamily = AppFonts.Mono;
            if (card.Role != LauncherLogic.Compactor) // COMPACTOR never chats
            {
                var chat = Section("System prompt (Chat)", "Optional: added to this worker's instructions in the Chat view (roles." + card.Role + ".chat_system_prompt). Empty = none.");
                chat.Tag = "ChatPromptSection";
                chat.Children.Add(chatPrompt);
            }
            var workflow = Section("System prompt (workflow)", card.Role == LauncherLogic.Compactor
                ? "The instructions COMPACTOR follows when it summarizes a conversation or an agent's steps."
                : "The instructions this worker follows in Code conversations and project runs.");
            workflow.Tag = card.Role == LauncherLogic.Compactor ? "PromptSection" : "WorkflowPromptSection"; // COMPACTOR's shows in both views
            // Collapsed until its header is clicked (the prompts are long); the choice is kept (PromptsExpanded).
            var promptBody = new StackPanel { Tag = "PromptBody", Visibility = _settings.PromptsExpanded ? Visibility.Visible : Visibility.Collapsed };
            promptBody.Children.Add(Muted("The edit button unlocks a prompt and the reset button restores the default; Save keeps the change, for every project."));
            foreach (var (label, file) in RolePromptFiles(card.Role))
                promptBody.Children.Add(PromptEditor(label, file, promptEdits));
            var promptHeader = (TextBlock)workflow.Children[0];
            var promptTitle = promptHeader.Text;
            void ShowPrompts() => promptHeader.Text = (promptBody.Visibility == Visibility.Visible ? "▾  " : "▸  ") + promptTitle;
            (promptHeader.Cursor, promptHeader.Tag) = (Cursors.Hand, "PromptToggle");
            promptHeader.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                promptBody.Visibility = promptBody.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                _settings.PromptsExpanded = promptBody.Visibility == Visibility.Visible;
                _settings.Save();
                ShowPrompts();
            };
            ShowPrompts();
            workflow.Children.Add(promptBody);
            var panel = source = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { Label("Model source"), Muted("Local loads the model on this computer; Remote API sends requests to an OpenAI-compatible endpoint.") } };
            modes = new[] { "Local", "Remote API" }.Select((text, i) => new RadioButton
            {
                Content = text, GroupName = "ModelSource", IsChecked = (remoteNow is not null) == (i == 1), Margin = new Thickness(0, 0, 18, 0),
            }).ToArray();
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { modes[0], modes[1] } });
            var remoteFields = new (string Label, string Hint, string Value)[]
            {
                ("Base URL", "e.g. https://openrouter.ai/api/v1", remoteNow?.BaseUrl ?? ""),
                ("Model id", "as the API names it", remoteNow?.Model ?? ""),
                ("API key variable", "environment variable holding the key", remoteNow?.KeyEnv ?? "OPENAI_API_KEY"),
                ("Provider (optional)", "OpenRouter only: use these providers, in this order, with no fallback. Names as OpenRouter lists them, comma-separated; empty lets OpenRouter choose.", remoteNow?.Provider ?? ""),
            };
            remoteBoxes = remoteFields.Select(f => Box(f.Value, f.Label)).ToArray();
            remotePanel = new StackPanel();
            for (var i = 0; i < remoteFields.Length; i++)
                remotePanel.Children.Add(new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { Label(remoteFields[i].Label), Muted(remoteFields[i].Hint), remoteBoxes[i] } });
            remotePanel.Children.Add(fieldErrors["Remote"] = ErrorLine());
            var note = Muted("The launcher never stores the key: set the variable in your environment before starting it. Remote roles are not loaded, unloaded or monitored.");
            note.Margin = new Thickness(0, 8, 0, 0);
            remotePanel.Children.Add(note);
            panel.Children.Add(remotePanel);
        }

        // The option groups (Model's fields join the picker below); a remote role hides the load options, and the groups
        // holding nothing else.
        var loadOnly = new List<StackPanel>();
        foreach (var (name, help) in SettingsGroups.Where(g => g.Help is not null))
            if (Fields(name) is { Children.Count: > 0 } fields)
            {
                Section(name, $"{help} {emptyMeans}", showHelp: false).Children.Add(fields);
                if (ModelOptions.All.Where(o => SettingsGroupOf(o.Key) == name).All(o => o.Target == "auto_load"))
                    loadOnly.Add(blocks[name]);
            }
        ContextHint(null);

        // Swaps the current defaults into the fields still showing the previous ones (a user's edit stays); returns their keys.
        List<string> Rederive()
        {
            var fresh = Base();
            var changed = new List<string>();
            foreach (var (key, shown) in auto.ToList())
                if (key != "gguf_variant" && Value(editors[key]) == shown)
                {
                    SetValue(editors[key], auto[key] = fresh.GetValueOrDefault(key, ""));
                    changed.Add(key);
                }
            return changed;
        }
        // Reads the model's GGUF header off the UI thread, then rederives; only the latest read applies. The values derived at
        // open are part of the clean state, not changes.
        var derivation = 0;
        List<string?> clean = [];
        async void Derive(JsonObject? load, bool atOpen = false)
        {
            var mine = ++derivation;
            var (folder, hub, copy) = (_settings.ModelsFolder ?? "", LauncherLogic.HubCache(), load?.DeepClone().AsObject());
            var (read, sizes) = await Task.Run(() =>
            {
                var gguf = LauncherLogic.ResolveGguf(copy, folder, hub);
                return (LauncherLogic.ReadGgufMetadata(gguf), gguf is null ? (0L, 0L) : VramEstimate.FileSizes(gguf));
            });
            if (mine != derivation)
                return;
            meta = read;
            files = sizes;
            var keys = editors.Keys.ToList();
            foreach (var key in Rederive())
                if (atOpen)
                    clean[keys.IndexOf(key)] = StateOf(editors[key]);
            ContextHint(meta);
            Vram();
        }

        // The VRAM meter (VramEstimate): the model's GGUF and the shown options, against the GPU's memory; it follows every edit.
        var vramBlock = new StackPanel { Tag = "VramEstimate", Margin = new Thickness(0, 12, 0, 0) };
        void Vram()
        {
            vramBlock.Children.Clear();
            var estimate = remoteMode() ? null : VramEstimate.Estimate(meta, files.Model, files.Vision, editors.ToDictionary(p => p.Key, p => Value(p.Value).Trim()));
            vramBlock.Visibility = estimate is null ? Visibility.Collapsed : Visibility.Visible;
            if (estimate is null)
                return;
            var gpu = _gpuMemory is { IsCompletedSuccessfully: true, Result: > 0 } t ? t.Result : (long?)null;
            // Total: the whole model wherever it runs (every layer counted, as if all were on the GPU).
            var all = editors.ToDictionary(p => p.Key, p => p.Key is "gpu_layers" or "n_cpu_moe" ? "" : Value(p.Value).Trim());
            var whole = VramEstimate.Estimate(meta, files.Model, files.Vision, all)!.Total;
            vramBlock.Children.Add(Label("Estimated memory usage"));
            DockPanel Pill(string name, string value, Brush? color = null)
            {
                var text = new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.SemiBold };
                if (color is null)
                    text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                else
                    text.Foreground = color;
                var pill = new Border { Child = text, Padding = new Thickness(10, 3, 10, 3), CornerRadius = new CornerRadius(11) };
                pill.SetResourceReference(Border.BackgroundProperty, "Hover");
                DockPanel.SetDock(pill, Dock.Right);
                var label = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                return new DockPanel { Margin = new Thickness(0, 8, 0, 0), Children = { pill, label } };
            }
            vramBlock.Children.Add(Pill("GPU", VramEstimate.Gb(estimate.Total) + (gpu is { } g ? " / " + VramEstimate.Gb(g) : ""),
                gpu is { } g2 ? (Brush)new BrushConverter().ConvertFromString(RingColor((double)estimate.Total / g2))! : null));
            vramBlock.Children.Add(Pill("Total", VramEstimate.Gb(Math.Max(whole, estimate.Total))));
            var parts = new (string Name, long Bytes, string Color)[]
            {
                ("Weights", estimate.Weights, "#4C8DFF"), ("KV cache", estimate.Kv, "#8B7CFF"), ("Compute", estimate.Compute, "#E8A33D"), ("Vision", estimate.Vision, "#2BB3A3"),
            };
            var bar = new Grid { Height = 8 };
            foreach (var (_, bytes, color) in parts.Append(("", Math.Max(0, (gpu ?? 0) - estimate.Total), "")).Where(p => p.Item2 > 0))
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bytes, GridUnitType.Star) });
                var segment = new System.Windows.Shapes.Rectangle { Fill = color.Length == 0 ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(color)! };
                Grid.SetColumn(segment, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(segment);
            }
            var track = new Border { CornerRadius = new CornerRadius(4), Child = bar, ClipToBounds = true, Margin = new Thickness(0, 6, 0, 6) };
            track.SetResourceReference(Border.BackgroundProperty, "Hover");
            vramBlock.Children.Add(track);
            var legend = new WrapPanel();
            foreach (var (name, bytes, color) in parts.Where(p => p.Bytes > 0))
                legend.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 2),
                    Children =
                    {
                        new System.Windows.Shapes.Rectangle { Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                            Fill = (Brush)new BrushConverter().ConvertFromString(color)! },
                        Muted($"{name} {VramEstimate.Gb(bytes)}"),
                    },
                });
            vramBlock.Children.Add(legend);
            var over = gpu is { } g3 ? estimate.Total - g3 : 0;
            var note = Muted(over > 0
                ? $"About {VramEstimate.Gb(over)} over this GPU: lower the context length, pick a smaller KV cache type, or keep layers or MoE experts on the CPU."
                : "An estimate from the model file and these settings; the real use can differ by a few hundred MB.");
            if (over > 0)
                note.Foreground = red;
            note.Margin = new Thickness(0, 4, 0, 0);
            vramBlock.Children.Add(note);
        }

        // The model list loads in the background the first time it opens (never at startup); the body stays usable meanwhile.
        var modelBox = new ComboBox { Tag = "Model", Margin = new Thickness(0, 6, 0, 0) };
        modelBox.Items.Add(new ComboBoxItem { Content = card.ModelName });
        modelBox.SelectedIndex = 0;
        var modelStatus = Muted("Open the list for the installed models.");
        modelStatus.Margin = new Thickness(0, 4, 0, 0);
        // Top: the picker, quantization, source, memory, context length with its slider, then the Advanced switch.
        var top = Section("Model", "The model this worker runs, its quantization and where it runs.");
        var modelFields = Fields("Model");
        // The quantization comes with the model (a pick sets it), so it is shown, not edited: "Quantization - Q4_K_M".
        if (editors["gguf_variant"] is TextBox variantBox)
        {
            var quantLine = new TextBlock { FontSize = 12, Tag = "QuantizationText" };
            void ShowQuant()
            {
                quantLine.Inlines.Clear();
                quantLine.Inlines.Add(new System.Windows.Documents.Run("Quantization - ") { FontWeight = FontWeights.SemiBold });
                quantLine.Inlines.Add(new System.Windows.Documents.Run(variantBox.Text.Trim() is { Length: > 0 } q ? q : "—"));
            }
            variantBox.TextChanged += (_, _) => ShowQuant();
            ShowQuant();
            rows["gguf_variant"].Children.Clear();
            rows["gguf_variant"].Children.Add(quantLine);
            rows["gguf_variant"].Children.Add(fieldErrors["gguf_variant"]);
        }
        modelFields.Children.Remove(rows["max_seq_length"]);
        ContextHint(null);
        var advanced = new ToggleButton { Style = (Style)FindResource("ToggleSwitch"), IsChecked = _settings.ModelAdvanced, Tag = "AdvancedSettings", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(advanced, Dock.Right);
        var advancedLabel = Label("Advanced settings");
        advancedLabel.VerticalAlignment = VerticalAlignment.Center;
        var advancedRow = new DockPanel { Margin = new Thickness(0, 16, 0, 0), Children = { advanced, advancedLabel },
            ToolTip = "Memory, speed, output and server options" };
        // COMPACTOR: whether it follows LEAD's model, and the way back to it.
        StackPanel? follow = null;
        if (card.Role == LauncherLogic.Compactor)
        {
            follow = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Tag = "CompactorSource" };
            if (followsMain)
                follow.Children.Add(Muted("Same as LEAD: compaction runs on LEAD's model and follows its changes. Pick a model (or change a setting) and Save to give COMPACTOR its own."));
            else
            {
                follow.Children.Add(Muted("COMPACTOR has its own model: summaries load it, then LEAD's model loads again."));
                var back = new Button { Content = "Use LEAD's model", Tag = "UseMainModel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4),
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
                back.Click += (_, _) =>
                {
                    if (WriteConfigs(WorkerConfigTargets(), target =>
                        {
                            var root = JsonNode.Parse(File.ReadAllText(target))!.AsObject();
                            ((root["roles"] ??= new JsonObject()).AsObject())[LauncherLogic.Compactor] = new JsonObject { ["same_as"] = "planner" };
                            WriteJson(target, root);
                            return null;
                        }) is { } failed)
                    {
                        Toast($"{card.Title}: not changed ({failed})", "#FF667A");
                        return;
                    }
                    Toast($"{card.Title}: same as LEAD");
                    HoldWorkerTab(() =>
                    {
                        PrefillCards();
                        RefreshWorkerTab(force: true);
                    });
                };
                follow.Children.Add(back);
            }
        }
        foreach (var part in new UIElement?[] { new StackPanel { Margin = new Thickness(0, 6, 0, 0), Children = { modelBox, modelStatus } }, follow, modelFields, source, vramBlock,
                     rows["max_seq_length"], advancedRow })
            if (part is not null)
                top.Children.Add(part);
        foreach (var (name, _) in SettingsGroups)
            if (blocks.TryGetValue(name, out var block))
                (HeadGroups.Contains(name) ? head : sections).Children.Add(block);
        ApplyPromptView(body);
        // The picker (MainWindow.ModelPicker.cs) opens instead of the box's own list; the box shows the current model or the
        // pick. The list loads the first time it opens (never at startup); the body stays usable meanwhile.
        ModelChoice? initial = null;
        var requested = false;
        var picker = _modelPickerTest = new ModelPicker(modelBox, card.ModelName, choice =>
        {
            var old = modelBox.SelectedItem;
            var item = choice is null ? new ComboBoxItem { Content = card.ModelName } : new ComboBoxItem { Content = choice.Label, Tag = choice };
            modelBox.Items.Add(item);
            modelBox.SelectedItem = item;
            modelBox.Items.Remove(old);
        });
        void OpenPicker()
        {
            if (!modelBox.IsEnabled)
                return;
            picker.Open(modelBox.SelectedItem is ComboBoxItem { Tag: ModelChoice shown } ? shown : initial, initial);
            if (!requested)
                LoadModels();
            requested = true;
        }
        // Opened on the release: opened on the press, the release would count as a click outside and close it. A click
        // while it is open closes it (the press already did, StaysOpen being false).
        modelBox.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true; // not the box's own list
        modelBox.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!picker.JustClosed)
                OpenPicker();
        };
        modelBox.PreviewKeyDown += (_, e) =>
        {
            if ((e.Key == Key.System ? e.SystemKey : e.Key) is Key.F4 or Key.Down or Key.Up or Key.Enter or Key.Space)
            {
                e.Handled = true;
                OpenPicker();
            }
        };
        // A model brings its own quantization: the Load section's gguf_variant follows the pick; the current model (or no
        // pick) restores what it showed.
        modelBox.SelectionChanged += (_, _) =>
        {
            var picked = Picked();
            if (editors["gguf_variant"] is TextBox variant)
                variant.Text = picked is null ? shownAtStart["gguf_variant"] : picked.Variant ?? "";
            hasModel = picked is not null || startLoad?["model_path"] is not null;
            Derive(LoadOf(picked));
        };
        async void LoadModels()
        {
            modelStatus.Text = "Loading models…";
            picker.SetEntries(null, "Loading models…");
            try
            {
                var choices = await FetchModelChoicesAsync();
                var (folder, hub) = (_settings.ModelsFolder ?? "", LauncherLogic.HubCache());
                var entries = await Task.Run(() => DescribeChoices(choices, folder, hub));
                initial = choices.FirstOrDefault(choice => LauncherLogic.DescribeModel(choice.Display, choice.Variant) is var d
                    && card.ModelName == d.Name && (card.Quant == d.Quant || d.Quant == "—"));
                modelStatus.Text = choices.Count == 0 ? "No installed text models found." : "GGUF files on this computer.";
                picker.SetEntries(entries, choices.Count == 0 ? modelStatus.Text : "");
                if (picker.IsOpen)
                    picker.Open(modelBox.SelectedItem is ComboBoxItem { Tag: ModelChoice shown } ? shown : initial, initial);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                modelStatus.Text = $"Could not list models ({exception.Message}); the current model is kept.";
                modelStatus.Foreground = red;
                picker.SetEntries([], modelStatus.Text);
                requested = false; // the next open tries again
            }
        }

        void SyncMode()
        {
            var remote = remoteMode();
            remotePanel?.SetValue(VisibilityProperty, remote ? Visibility.Visible : Visibility.Collapsed);
            foreach (var hidden in ModelOptions.All.Where(o => o.Target == "auto_load").Select(o => rows[o.Key]).Concat(loadOnly))
                hidden.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            modelBox.IsEnabled = !remote;
            if (advanced.IsChecked != true) // the Advanced switch shows the load and output groups; Sampling and the prompts always show
                foreach (var name in AdvancedGroups)
                    if (blocks.TryGetValue(name, out var block))
                        block.Visibility = Visibility.Collapsed;
        }
        advanced.Click += (_, _) =>
        {
            _settings.ModelAdvanced = advanced.IsChecked == true;
            _settings.Save();
            foreach (var name in AdvancedGroups)
                if (blocks.TryGetValue(name, out var block))
                    block.Visibility = Visibility.Visible;
            SyncMode();
        };
        foreach (var mode in modes ?? [])
            mode.Checked += (_, _) =>
            {
                SyncMode();
                Rederive(); // a remote role shows no local defaults
            };
        SyncMode();

        var error = ErrorLine();
        error.Margin = new Thickness(0, 0, 0, 8);
        var save = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 4, 16, 4) };
        var cancel = new Button { Content = closeLabel, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        // Option presets: the option fields (not the model or its quantization) and the system prompt when it is set and not
        // the worker kind's default, global across workers. Applying one only fills the fields; Save still writes them.
        var promptBox = prompt ?? chatPrompt!;
        string DefaultPrompt() => kinds is null ? "" : LauncherLogic.DefaultSystemPrompt(kinds[1].IsChecked == true ? "reviewer" : "specialist");
        var presets = new Button
        {
            Content = "Presets  ▾", Tag = "ModelPresets", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Save these options as a preset, or fill them from one",
        };
        // Presets are the model's: the menu (built on each click) lists those of the model in the picker now
        string ShownModel() => (modelBox.SelectedItem as ComboBoxItem)?.Content as string ?? card.ModelName;
        bool Mine(ModelPreset p) => p.Model.Length == 0 || p.Model.Equals(ShownModel(), StringComparison.OrdinalIgnoreCase);
        presets.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = presets, Placement = PlacementMode.Top };
            MenuItem Item(string header, Action act)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => act();
                return item;
            }
            menu.Items.Add(Item("Save as preset…", () =>
            {
                if (SidebarAsk("Save preset", "Name this set of model options.", "Save", "")?.Trim() is not { Length: > 0 } name)
                    return;
                var same = _settings.ModelPresets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Model.Equals(ShownModel(), StringComparison.OrdinalIgnoreCase));
                if (same >= 0 && SidebarAsk("Replace preset", $"Replace the preset {name}?", "Replace") is null)
                    return;
                var preset = new ModelPreset
                {
                    Name = name, Model = ShownModel(), Options = editors.Where(p => p.Key != "gguf_variant" && Value(p.Value).Trim().Length > 0).ToDictionary(p => p.Key, p => Value(p.Value)),
                    SystemPrompt = promptBox.Text.Trim().Length > 0 && promptBox.Text != DefaultPrompt() ? promptBox.Text : "",
                };
                if (same >= 0)
                    _settings.ModelPresets[same] = preset;
                else
                    _settings.ModelPresets.Add(preset);
                _settings.Save();
                Toast($"Preset {name} saved");
            }));
            if (_settings.ModelPresets.Any(Mine))
            {
                menu.Items.Add(new Separator());
                var delete = new MenuItem { Header = "Delete preset" };
                foreach (var preset in _settings.ModelPresets.Where(Mine).ToList())
                {
                    menu.Items.Add(Item(preset.Model.Length == 0 ? $"{preset.Name}  (any model)" : preset.Name, () =>
                    {
                        // values only; load options only with a model to load (Save would refuse them)
                        var load = hasModel && !remoteMode();
                        foreach (var (key, value) in preset.Options)
                            if (value.Trim().Length > 0 && editors.TryGetValue(key, out var editor) && (load || ModelOptions.All.First(o => o.Key == key).Target != "auto_load"))
                                SetValue(editor, value);
                        if (preset.SystemPrompt.Trim().Length > 0)
                            promptBox.Text = preset.SystemPrompt;
                    }));
                    delete.Items.Add(Item(preset.Name, () =>
                    {
                        _settings.ModelPresets.Remove(preset);
                        _settings.Save();
                    }));
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(delete);
            }
            presets.ContextMenu = menu;
            menu.IsOpen = true;
        };
        var footer = new StackPanel
        {
            Margin = new Thickness(0, 14, 0, 0),
            Children = { error, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { presets, cancel, save } } },
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Margin = new Thickness(0, 14, 14, 14);
        foreach (var part in new FrameworkElement[] { body, footer }) // read-only while a run uses the models
            part.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(RoleCard.Editable)) { Source = card });
        cancel.Click += (_, _) => close();

        // Shows the errors under their fields (the rest above the buttons); true when there were any.
        bool ShowErrors(Dictionary<string, string> errors)
        {
            foreach (var line in fieldErrors.Values)
                line.Visibility = Visibility.Collapsed;
            foreach (var (key, message) in errors)
                if (fieldErrors.TryGetValue(key, out var line))
                    (line.Text, line.Visibility) = (message, Visibility.Visible);
            var general = errors.Where(p => !fieldErrors.ContainsKey(p.Key)).Select(p => p.Value).ToList();
            error.Text = general.Count > 0 ? string.Join(" ", general) : "Some fields need a fix: see the notes under them.";
            error.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return errors.Count > 0;
        }

        save.Click += (_, _) => Save();
        bool Save()
        {
            var fields = editors.ToDictionary(p => p.Key, p => Value(p.Value));
            var choice = Picked();
            if (!card.IsCustom && !string.Equals(ActiveConfigPath(), path, StringComparison.OrdinalIgnoreCase))
            {
                // built for another project's config (the project changed behind the tab): never write its values there
                Log($"{card.Title}: not saved, the project's config.json changed since the tab was opened ({path}); the tab now shows the current one");
                Toast($"{card.Title}: not saved, the project changed", "#FF667A");
                close();
                return false;
            }
            if (card.IsCustom)
            {
                var w = card.Config;
                var probe = new JsonObject();
                if ((choice?.LoadPath ?? w.ModelPath) is { } modelPath)
                    probe["auto_load"] = new JsonObject { ["model_path"] = modelPath };
                var errors = ModelOptions.Apply(probe, fields, includeLoad: true);
                var kind = (string)kinds!.First(r => r.IsChecked == true).Tag;
                var title = nameBox!.Text.Trim();
                if (title != card.Title && LauncherLogic.TitleProblem(title, card.Role, CurrentWorkers()) is { } titleProblem)
                    errors["Name"] = titleProblem;
                if (ShowErrors(errors))
                    return false;
                if (choice is not null)
                    AssignModel(card, choice);
                w.Kind = kind;
                w.Description = description!.Text.Trim();
                w.SystemPrompt = prompt!.Text;
                var inherits = Base(); // a value equal to what it would inherit is not stored, so the Implementer's changes still reach it
                w.ModelOptions = fields.Where(p => p.Value.Trim().Length > 0 && p.Value != inherits.GetValueOrDefault(p.Key, "")).ToDictionary();
                (w.Temperature, w.MaxTokens, w.RepetitionPenalty, w.ReasoningBudget, w.ContextLength, w.Thinking) = ("", "", "", "", "", "Auto"); // now in ModelOptions
                if (title != card.Title)
                    card.Title = title;
                SaveWorkers();
                SyncCustomWorkers();
                ContextLengthSaved(card.Role, fields.GetValueOrDefault("max_seq_length"));
                Log($"{card.Title}: model settings saved");
                Toast($"{card.Title}: settings saved");
                saved();
                return true;
            }

            if (!SavePromptOverrides(promptEdits))
            {
                ShowErrors(new() { [""] = "A prompt could not be saved: see the log." });
                return false;
            }
            // COMPACTOR on MAIN's model keeps following it until a model or option of its own is saved.
            if (followsMain && choice is null && editors.Values.Concat<Control?>(remoteBoxes ?? []).Concat(modes ?? []).Select(StateOf)
                    .SequenceEqual(clean.Take(editors.Count).Concat(clean.Skip(editors.Count + 4).Take((remoteBoxes?.Length ?? 0) + (modes?.Length ?? 0)))))
            {
                Toast($"{card.Title}: settings saved");
                HoldWorkerTab(saved);
                return true;
            }
            var remote = remoteMode() ? new LauncherLogic.RemoteRole(remoteBoxes![0].Text, remoteBoxes[1].Text, remoteBoxes[2].Text, "", remoteBoxes[3].Text) : null;
            var check = (JsonObject)role!.DeepClone(); // validate everything before any file is touched
            if (choice is not null && remote is null)
                SetRoleModel(check, choice);
            var problems = ModelOptions.Apply(check, fields, includeLoad: remote is null);
            if (remote is not null && LauncherLogic.ValidateRemoteRole(remote) is { } remoteProblem)
                problems["Remote"] = remoteProblem;
            if (ShowErrors(problems))
                return false;
            string? problem;
            try
            {
                // path first (the window's config), then the worker config when path is a project's
                problem = WriteConfigs(WorkerConfigTargets().Where(t => t != path).Prepend(path).ToList(), target =>
                {
                    if ((remote is not null || remoteNow is not null) &&
                        LauncherLogic.WriteRemoteRole(target, card.Role, remote, Path.Combine(_workflowRoot ?? "", "config.json")) is { } failed)
                        return failed;
                    var root = JsonNode.Parse(File.ReadAllText(target))!.AsObject();
                    if (card.Role == LauncherLogic.Compactor && !LauncherLogic.CompactorHasOwn(root["roles"] as JsonObject)) // its own from here on: MAIN's settings to start
                    {
                        var copy = (JsonObject)root["roles"]!["planner"]!.DeepClone();
                        copy.Remove("chat_system_prompt");
                        root["roles"]![LauncherLogic.Compactor] = copy;
                    }
                    var settings = root["roles"]![card.Role]!.AsObject();
                    if (choice is not null && remote is null)
                        SetRoleModel(settings, choice);
                    if (ModelOptions.Apply(settings, fields, includeLoad: remote is null) is { Count: > 0 } refused)
                        return string.Join(" ", refused.Values);
                    if (chatPrompt!.Text.Trim().Length > 0)
                        settings["chat_system_prompt"] = chatPrompt.Text;
                    else
                        settings.Remove("chat_system_prompt");
                    WriteJson(target, root);
                    return null;
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
            {
                Log($"Could not update config.json: {exception.Message}");
                SetStatus("Could not update config.json", "#FF667A");
                close();
                return false;
            }
            if (problem is not null)
            {
                ShowErrors(new() { [""] = problem });
                return false;
            }
            if (choice is not null && remote is null)
            {
                card.ModelId = choice.Display;
                card.ModelVariant = choice.Variant;
                card.SetModel(LauncherLogic.DescribeModel(choice.Display, choice.Variant));
                SaveWorkers();
            }
            if (remote is null)
                ContextLengthSaved(card.Role, fields.GetValueOrDefault("max_seq_length"));
            Log($"{card.Title}: model settings saved{(remote is null ? "" : $" (remote API {remote.BaseUrl.Trim()}, model {remote.Model.Trim()})")}");
            if (remote is not null && Environment.GetEnvironmentVariable(remote.KeyEnv.Trim()) is null)
                Toast($"{card.Title}: set {remote.KeyEnv.Trim()} in your environment, then restart the launcher", "#F7C873");
            else
                Toast($"{card.Title}: settings saved");
            HoldWorkerTab(() =>
            {
                PrefillCards();
                saved();
            });
            return true;
        }
        ModelChoice? Picked() => modelBox.SelectedItem is ComboBoxItem { Tag: ModelChoice c } && c != initial ? c : null;
        static string? StateOf(Control? c) => c switch { TextBox t => t.Text, ComboBox b => b.SelectedItem as string, RadioButton r => r.IsChecked.ToString(), _ => "" };
        List<string?> State() => editors.Values.Concat(new Control?[] { nameBox, description, prompt, chatPrompt }).Concat(remoteBoxes ?? []).Concat(modes ?? []).Concat(kinds ?? [])
            .Concat(promptEdits.Select(p => p.Box))
            .Select(StateOf).Append((Picked() is not null).ToString()).ToList();
        clean = State();
        foreach (var editor in editors.Values)
            if (editor is TextBox box)
                box.TextChanged += (_, _) => Vram();
            else
                ((ComboBox)editor).SelectionChanged += (_, _) => Vram();
        foreach (var mode in modes ?? [])
            mode.Checked += (_, _) => Vram();
        _gpuMemory ??= Task.Run(LauncherLogic.GpuMemoryBytes);
        _gpuMemory.ContinueWith(_ => Vram(), TaskScheduler.FromCurrentSynchronizationContext());
        Derive(startLoad, atOpen: true);
        return (new DockPanel { Children = { footer, scroll } }, () => !State().SequenceEqual(clean), Save);
    }

    private static Task<long?>? _gpuMemory; // the first GPU's total memory (nvidia-smi), read once

    // The settings body's groups in display order (one column, or two read left to right); Help: an option group's note (null:
    // built by ModelSettingsBody itself). An option whose key SettingsGroupKeys does not name goes in Other.
    // Grouped: Memory, Speed, Output and Server show with the Advanced switch (AdvancedGroups).
    private static readonly (string Name, string? Help)[] SettingsGroups =
    [
        ("Model", null), ("Sampling", "Sent with every request; top_k, min_p and repetition_penalty go in extra_body."),
        ("Memory", "Load options: apply at the next model load."), ("Speed", "Load options: apply at the next model load."),
        ("Output", "The reasoning budget, its message and the chat template apply at the next model load; the rest is sent with every request."),
        ("Server", "Load options: apply at the next model load."), ("Request", "How long the orchestrator waits for this worker."), ("Other", "Other options."),
        ("System prompt (Chat)", null), ("System prompt (workflow)", null), ("Worker", null),
    ];
    private static readonly string[] HeadGroups = ["Model", "Worker", "System prompt (Chat)", "System prompt (workflow)"];
    private static readonly string[] AdvancedGroups = ["Memory", "Speed", "Output", "Server", "Request", "Other"];
    private static readonly Dictionary<string, string[]> SettingsGroupKeys = new()
    {
        ["Model"] = ["gguf_variant", "max_seq_length"],
        ["Sampling"] = ["temperature", "top_p", "top_k", "min_p", "repetition_penalty", "presence_penalty", "frequency_penalty", "seed", "enable_thinking", "reasoning_effort"],
        ["Memory"] = ["cache_type_kv", "gpu_memory_mode", "gpu_layers", "n_cpu_moe", "disable_vision", "mmproj_offload", "ctx_checkpoints", "cache_ram", "cpu_fallback"],
        ["Speed"] = ["speculative_type", "spec_draft_n_max", "spec_draft_cache_type", "n_parallel", "n_batch", "n_ubatch", "tensor_parallel", "tensor_split", "gpu_ids"],
        ["Output"] = ["max_tokens", "stop", "reasoning_budget", "reasoning_budget_message", "chat_template_override"],
        ["Server"] = ["load_mode", "llama_extra_args"],
        ["Request"] = ["timeout_seconds"],
    };
    private static string SettingsGroupOf(string key) => SettingsGroupKeys.FirstOrDefault(g => g.Value.Contains(key)).Key ?? "Other";

    // --ui-smoke: the target chips' "Options…" opens the worker details column (expanding the rail) on the target's tab: Single
    // chat's target, the Code target, Multi's Planner. Save writes the worker and keeps the tab; leaving a tab with an unsaved
    // change asks: no answer stays, Discard switches and writes nothing. Absent keys show their defaults (a remote role none);
    // option presets save the fields and the system prompt, apply them (fields only, nothing written) and delete; Revert drops
    // what a preset filled. config: the active config.json, holding roles.implementer.
    private async Task<bool> ModelConfigSmoke(string config, Action<string, FrameworkElement?> snap)
    {
        static Task Idle() => Task.Delay(400);
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: worker tab options: {what}"); return false; }
        // The chip's dropdown: "Options…", a separator, then the listed workers ("*": the checked target).
        static List<string> Dropdown(Button chip)
        {
            SmokeClick(chip);
            var items = chip.ContextMenu.Items.OfType<MenuItem>().ToList();
            chip.ContextMenu.IsOpen = false;
            return items.Select(i => (string)i.Header + (i.IsChecked ? "*" : "")).ToList();
        }
        // Options… from a collapsed rail: the worker whose tab the column then shows.
        async Task<RoleCard?> Open(Button chip)
        {
            SetWorkerRail(true);
            SmokeClick(chip);
            chip.ContextMenu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            chip.ContextMenu.IsOpen = false;
            await Idle();
            return DetailsPanel.IsVisible && !_settings.DetailsCollapsed ? _shownWorker : null;
        }
        const string chipTip = "Choose the worker to message, or open model Options";
        const string leadTip = "The lead (the Planner) answers and hands parts to your other workers; Options opens its model settings";
        var saved = (_settings.ChatWorker, _settings.ModelsFolder, _codeChoice);
        var models = Path.Combine(AppSettings.DataRoot, "smoke-models-config");
        Directory.CreateDirectory(models);
        _settings.ModelsFolder = models; // a disk scan
        var asked = new List<string>();
        string? answer = null;
        _codeChoice = title => { asked.Add(title); return answer; };
        var (planner, implementer, reviewer) = (CardFor("planner")!, CardFor("implementer")!, CardFor("reviewer")!);
        try
        {
            // Chat: the dropdown lists every shown worker (MAIN, the added roles, the custom workers), the target checked;
            // Options… opens the target's tab. Code: Options… and the lead (with its model), opening the lead's (the Planner's) tab.
            _settings.ChatWorker = "reviewer";
            RefreshChatTarget();
            if (Dropdown(ChatTargetChip) is not ["Options…", "Lead", "Implementer", "Reviewer*", ..] chatDrop || chatDrop.Count != 1 + _cards.Count(k => !k.Hidden && k.Role != LauncherLogic.Compactor) ||
                ChatTargetChip.ToolTip as string != chipTip)
                return Fail("Single chat dropdown");
            if (await Open(ChatTargetChip) != reviewer || !reviewer.IsSelected)
                return Fail("Single chat Options…");
            if (Dropdown(CodeTargetChip) is not ["Options…", "Lead*"] || (string)CodeTargetChip.Content != "Lead  ▾" ||
                await Open(CodeTargetChip) != planner || CodeTargetChip.ToolTip as string != leadTip)
                return Fail("Code Options…");
            SelectWorker(reviewer);
            if (asked.Count > 0 || ChatTargetChip.Cursor != Cursors.Hand || await Open(CodeTargetChip) != planner)
                return Fail("Code Options… from another tab");

            // Save keeps the Implementer's tab.
            SmokeClickTab(implementer);
            await Idle();
            SmokeField("temperature").Text = "0.33";
            SmokeClick(SmokeButton("Save"));
            await Idle();
            if (_shownWorker != implementer || (double?)JsonNode.Parse(File.ReadAllText(config))!["roles"]!["implementer"]!["sampling"]?["temperature"] != 0.33)
                return Fail("Save");

            // An unsaved change: another tab asks; no answer stays, Discard switches and writes nothing.
            var bytes = File.ReadAllBytes(config);
            SmokeField("temperature").Text = "0.77";
            SmokeClickTab(reviewer);
            await Idle();
            if (asked is not ["Unsaved changes"] || _shownWorker != implementer || !implementer.IsSelected || reviewer.IsSelected || SmokeField("temperature").Text != "0.77")
                return Fail("unsaved change: no answer");
            answer = "";
            SmokeClickTab(reviewer);
            await Idle();
            if (asked.Count != 2 || _shownWorker != reviewer || !reviewer.IsSelected || implementer.IsSelected || !File.ReadAllBytes(config).SequenceEqual(bytes))
                return Fail("unsaved change: Discard");

            // Defaults fill the absent keys; a remote role shows none (providers may reject them), Local brings them back.
            RadioButton Source(string label) => SmokeFind<RadioButton>(WorkerSettingsHost, r => r.Content as string == label)!;
            if (SmokeField("temperature").Text != "0.8" || SmokeField("top_p").Text != "0.95" || _workerDirty())
                return Fail("defaults");
            Source("Remote API").IsChecked = true;
            var remoteShown = SmokeField("temperature").Text + SmokeField("top_p").Text;
            Source("Local").IsChecked = true;
            if (remoteShown != "" || SmokeField("temperature").Text != "0.8")
                return Fail("remote defaults");

            // Option presets: save on the Reviewer (with its chat system prompt), apply on the Implementer, delete.
            void Preset(string header, string? sub = null)
            {
                var button = SmokeFind<Button>(WorkerSettingsHost, b => b.Tag as string == "ModelPresets")!;
                SmokeClick(button);
                var item = button.ContextMenu.Items.OfType<MenuItem>().First(i => (string)i.Header == header);
                (sub is null ? item : item.Items.OfType<MenuItem>().First(i => (string)i.Header == sub)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                button.ContextMenu.IsOpen = false;
            }
            SmokeField("temperature").Text = "0.21";
            SmokeField("top_k").Text = "7";
            SmokeField("ChatSystemPrompt").Text = "Smoke prompt";
            answer = "Smoke options";
            Preset("Save as preset…");
            if (_settings.ModelPresets.SingleOrDefault(p => p.Name == "Smoke options") is not { } stored || stored.Options.GetValueOrDefault("temperature") != "0.21" ||
                stored.Options.ContainsKey("gguf_variant") || stored.Options.ContainsKey("seed") || stored.SystemPrompt != "Smoke prompt" ||
                stored.Model != reviewer.ModelName)
                return Fail("save a preset");
            // As if saved on a worker with a model, or by an older version: a load value and an empty one; neither is applied
            // to the Implementer, which has no model to load.
            (stored.Options["n_batch"], stored.Options["top_p"]) = ("64", "");
            answer = "";
            SmokeClickTab(implementer); // discards the Reviewer's change
            await Idle();
            if (asked[^1] != "Unsaved changes" || _shownWorker != implementer || SmokeField("temperature").Text != "0.33" || SmokeField("ChatSystemPrompt").Text != "")
                return Fail("discard the Reviewer's change");
            // presets are the model's: another model's is not listed; the Implementer's model's is
            stored.Model = "Some other model";
            var listed = SmokeFind<Button>(WorkerSettingsHost, b => b.Tag as string == "ModelPresets")!;
            SmokeClick(listed);
            var others = listed.ContextMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header == "Smoke options");
            listed.ContextMenu.IsOpen = false;
            if (others)
                return Fail("another model's preset is listed");
            stored.Model = implementer.ModelName;
            Preset("Smoke options");
            if (SmokeField("temperature").Text != "0.21" || SmokeField("top_k").Text != "7" || SmokeField("n_batch").Text != "" || SmokeField("top_p").Text != "0.95" ||
                SmokeField("ChatSystemPrompt").Text != "Smoke prompt" || !File.ReadAllBytes(config).SequenceEqual(bytes))
                return Fail("apply a preset");
            Preset("Delete preset", "Smoke options");
            if (_settings.ModelPresets.Any(p => p.Name == "Smoke options"))
                return Fail("delete a preset");
            SmokeClick(SmokeButton("Revert"));
            await Idle();
            if (SmokeField("temperature").Text != "0.33" || SmokeField("ChatSystemPrompt").Text != "" || _workerDirty() || !File.ReadAllBytes(config).SequenceEqual(bytes))
                return Fail("Revert a preset");

            // An unsaved edit: switching the project asks, no answer keeps the project and the edit; closing the window asks,
            // no answer cancels the close; starting a run or a turn asks to save first, Cancel starts nothing.
            var project = _projectPath;
            var workerConfig = LauncherLogic.EnsureWorkerConfig(_workflowRoot!);
            var workerBytes = File.ReadAllBytes(workerConfig);
            (SmokeField("temperature").Text, answer) = ("0.66", null);
            ProjectPath = "";
            if (asked[^1] != "Unsaved changes" || _projectPath != project || SmokeField("temperature").Text != "0.66" || !_workerDirty())
                return Fail("a project switch with an unsaved edit");
            var closing = new System.ComponentModel.CancelEventArgs();
            OnClosing(this, closing);
            if (!closing.Cancel || asked[^1] != "Unsaved changes" || !_workerDirty())
                return Fail("closing with an unsaved edit");
            if (SaveWorkerEditsFirst() || asked[^1] != "Save changes" || !_workerDirty() || !File.ReadAllBytes(config).SequenceEqual(bytes))
                return Fail("a start with an unsaved edit: Cancel");
            // A project changed behind the tab: Save writes neither config and shows the current project's settings.
            _projectPath = "";
            answer = "save";
            if (SaveWorkerEditsFirst() || _workerDirty() || !File.ReadAllBytes(config).SequenceEqual(bytes) || !File.ReadAllBytes(workerConfig).SequenceEqual(workerBytes))
                return Fail("Save after the project changed");
            _projectPath = project;
            RefreshWorkerTab(force: true);
            // Save first: written, then the start goes on (the same value as typed differently: later checks expect 0.33).
            SmokeField("temperature").Text = "0.330";
            if (!_workerDirty() ||!SaveWorkerEditsFirst() || _workerDirty() || (double?)JsonNode.Parse(File.ReadAllText(config))!["roles"]!["implementer"]!["sampling"]?["temperature"] != 0.33)
                return Fail("a start with an unsaved edit: Save");
            return true;
        }
        finally
        {
            RefreshWorkerTab(force: true); // a check that failed with a change pending
            (_settings.ChatWorker, _settings.ModelsFolder, _codeChoice) = saved;
            RefreshChatTarget();
            Directory.Delete(models, recursive: true);
        }
    }

    // --ui-smoke: the Code header puts the Context card directly left of the Conversation / Run output toggle; while a turn
    // runs, its status sits in the top row just left of the stop square and the bottom hint steps aside; idle shows the hint.
    private async Task<bool> UiTweaksSmoke(Action<string, FrameworkElement?> snap)
    {
        var (wasChat, turn) = (NavChat.IsChecked == true, _turn);
        try
        {
            ShowView(chat: false);
            await Task.Delay(400);
            var header = (DockPanel)RunClocks.Parent; // no Conversation / Run output toggle: a run's output is in the conversation
            if (CodeContextRing.Parent != header || header.Children.IndexOf(CodeContextRing) != header.Children.IndexOf(RunClocks) + 1)
                return false;
            _chatBusy = true;
            _turn = _code;
            RefreshChatInput();
            await Task.Delay(400);
            var top = (Grid)CodeStop.Parent;
            if (CodeBusyText.Parent != top || CodeBusyText.Visibility != Visibility.Visible || !CodeBusyText.Text.StartsWith("Replying…") || CodeStop.Visibility != Visibility.Visible ||
                Grid.GetColumn(CodeBusyText) != Grid.GetColumn(CodeStop) - 1 || Grid.GetRow(CodeBusyText) != 0 ||
                CodeBusyText.TranslatePoint(new Point(CodeBusyText.ActualWidth, 0), top).X > CodeStop.TranslatePoint(new Point(0, 0), top).X ||
                (CodeInput.Text.Length == 0) != (CodeHint.Visibility == Visibility.Visible) || !CodeHint.Text.StartsWith("Enter to queue") ||
                ChatBusyText.Text.Contains("Replying")) // the other conversation is only locked
                return false;
            snap("36-ui-tweaks", null);
            _chatBusy = false;
            RefreshChatInput();
            return CodeBusyText.Visibility == Visibility.Collapsed && CodeHint.Visibility == Visibility.Visible && CodeHint.Text.StartsWith("Enter to send");
        }
        finally
        {
            (_chatBusy, _turn) = (false, turn);
            RefreshChatInput();
            ShowView(chat: wasChat);
        }
    }

    // --ui-smoke helpers for the worker tabs and the inline settings body (WorkerSettingsHost).
    private static IEnumerable<DependencyObject> SmokeVisuals(DependencyObject d) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(d)).Select(i => VisualTreeHelper.GetChild(d, i)).SelectMany(c => SmokeVisuals(c).Prepend(c));
    private static T? SmokeFind<T>(DependencyObject d, Func<T, bool> match) where T : DependencyObject =>
        d is T t && match(t) ? t : LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().Select(c => SmokeFind(c, match)).FirstOrDefault(r => r is not null);
    private static void SmokeClick(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private TextBox SmokeField(string key) => SmokeFind<TextBox>(WorkerSettingsHost, t => t.Tag as string == key)!;
    private Button SmokeButton(string label) => SmokeFind<Button>(WorkerSettingsHost, b => b.Content as string == label)!;
    private FrameworkElement? SmokeTab(RoleCard card)
    {
        WorkerTabs.UpdateLayout();
        return WorkerTabs.ItemContainerGenerator.ContainerFromItem(card) as FrameworkElement;
    }
    private void SmokeClickTab(RoleCard card) => SmokeClick(SmokeVisuals(SmokeTab(card)!).OfType<Button>().First(b => b.Tag as string == "WorkerTab"));

    // --ui-smoke: the rail card's gear opens the column on its worker's tab; one tab per shown worker, three fit the default
    // column, a narrower one scrolls the strip (the wheel too); a tab shows title, model, tok/s and task · turn tokens. The
    // Planner's tab: its model list loads on the first open (the GGUFs on disk, llama.cpp runtime), a model's defaults never
    // replace an edit, Save writes the role and keeps the tab, Revert and an invalid value write nothing, and the chat system
    // prompt is saved and removed when blank. config: the active config.json, holding roles.planner.
    // --ui-smoke: COMPACTOR's tab (always shown, Same as MAIN, never a chat target), the Model group beside the prompts in a
    // wide column, a prompt edited, saved and reset (<data>\prompts), and "+ New preset" applying the default workers.
    private async Task<bool> CompactorSmoke(string config, Action<string, FrameworkElement?> snap)
    {
        static Task Idle() => Task.Delay(400);
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: compactor: {what}"); return false; }
        var (planner, compactor) = (CardFor("planner")!, CardFor(LauncherLogic.Compactor));
        var (chatWorker, ask, width, expanded) = (_settings.ChatWorker, _codeChoice, DetailsColumn.Width, _settings.PromptsExpanded);
        _settings.PromptsExpanded = false;
        _codeChoice = _ => null;
        var overridden = PromptOverridePath("compactor.txt");
        try
        {
            if (compactor is null || compactor.Hidden || !compactor.FollowsMain || compactor.ModelName != planner.ModelName || compactor.Title != "COMPACTOR")
                return Fail("the card");
            _settings.ChatWorker = LauncherLogic.Compactor;
            if (ListedWorkers(_chat).Contains(compactor) || ChatTarget() != planner ||
                BuildAddWorkerMenu(PresetsButton).Items.OfType<MenuItem>().Any(i => i.Tag as string == LauncherLogic.Compactor))
                return Fail("COMPACTOR is listed to chat with or to add");
            _settings.ChatWorker = chatWorker;

            if (Math.Abs(DetailsColumn.MinWidth - DetailsMin()) > 0.5 || DetailsMin() < Math.Min(260, 1) || DetailsColumn.ActualWidth < DetailsMin() - 0.5)
                return Fail($"the column's least width: {DetailsColumn.MinWidth} / {DetailsMin()}");
            DetailsColumn.Width = new GridLength(820); // two columns: Model beside the prompts, the options below the line
            SmokeClickTab(compactor);
            await Idle();
            if ((double)Resources["WorkerTabWidth"] != WorkerTabMax || WorkerTabsScroll.ScrollableWidth > 1) // capped: all four fit
                return Fail($"the cards' width: {Resources["WorkerTabWidth"]}");
            TextBlock? Header(string text) => SmokeFind<TextBlock>(WorkerSettingsHost, t => t.Text.EndsWith(text) && t.IsVisible);
            if (Header("MODEL") is not { } model || Header("SYSTEM PROMPT (WORKFLOW)") is not { } prompts || Header("SAMPLING") is not { } sampling)
                return Fail("the groups");
            // The prompts start collapsed; their header opens them, and the choice is kept.
            if (SmokeFind<StackPanel>(WorkerSettingsHost, p => p.Tag as string == "PromptBody") is not { IsVisible: false } || !prompts.Text.StartsWith("▸"))
                return Fail("the prompts start collapsed");
            prompts.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonUpEvent });
            await Idle();
            if (SmokeFind<StackPanel>(WorkerSettingsHost, p => p.Tag as string == "PromptBody") is not { IsVisible: true } || !prompts.Text.StartsWith("▾") || !_settings.PromptsExpanded)
                return Fail("the prompts open");

            // Vision: the switch goes in the request; a model that cannot see (Disable vision, or no mmproj beside its GGUF)
            // greys it with the reason as its tip.
            var sight = Path.Combine(AppSettings.DataRoot, "smoke-vision");
            Directory.CreateDirectory(sight);
            var blind = Path.Combine(sight, "Blind-Q4_K_M.gguf");
            File.WriteAllText(blind, "x");
            JsonObject Load() => new() { ["model_path"] = "Blind", ["gguf_file"] = blind };
            if (LauncherLogic.CanSee(new JsonObject { ["disable_vision"] = "on" }, "", "") ||
                LauncherLogic.CanSee(Load(), "", "") || !LauncherLogic.CanSee(null, "", ""))
                return Fail("CanSee");
            File.WriteAllText(Path.Combine(sight, "mmproj-F16.gguf"), "x");
            if (!LauncherLogic.CanSee(Load(), "", ""))
                return Fail("CanSee with a projector");
            // a flat models folder: only a projector named for the model counts
            var flat = Path.Combine(AppSettings.DataRoot, "smoke-flat");
            Directory.CreateDirectory(flat);
            foreach (var name in new[] { "Qwen3.5-9B-Q8_0.gguf", "gemma-4-E4B-it-Q4_K_M.gguf", "mmproj-gemma-4-E4B-it-F16.gguf" })
                File.WriteAllText(Path.Combine(flat, name), "x");
            if (LauncherLogic.VisionProjector(Path.Combine(flat, "Qwen3.5-9B-Q8_0.gguf")) is not null ||
                LauncherLogic.VisionProjector(Path.Combine(flat, "gemma-4-E4B-it-Q4_K_M.gguf")) != Path.Combine(flat, "mmproj-gemma-4-E4B-it-F16.gguf"))
                return Fail("another model's projector");
            // Quantization from the name (a space before it too), else from general.file_type; a GGUF with no tokenizer (a
            // diffusion model) is Image generation.
            void Gguf(string file, params (string Key, object Value)[] kv)
            {
                using var w = new BinaryWriter(File.Create(file));
                void Str(string s) { var b = System.Text.Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
                w.Write(0x46554747u); w.Write(3u); w.Write(0UL); w.Write((ulong)kv.Length);
                foreach (var (key, value) in kv)
                {
                    Str(key);
                    if (value is string text) { w.Write(8u); Str(text); }
                    else { w.Write(4u); w.Write((uint)(int)value); }
                }
            }
            var kinds = Path.Combine(AppSettings.DataRoot, "smoke-kinds");
            Directory.CreateDirectory(kinds);
            var (talker, painter) = (Path.Combine(kinds, "Talker Uncnr NEO.gguf"), Path.Combine(kinds, "MOSS-Image-SFT-Q8_0.gguf"));
            Gguf(talker, ("general.architecture", "qwen3"), ("general.file_type", 15), ("tokenizer.ggml.model", "gpt2"));
            Gguf(painter, ("general.architecture", "moss_image"));
            var described = DescribeChoices([new("t", "t", "t", null, "t", talker), new("p", "p", "p", null, "p", painter)], "", "");
            if (LauncherLogic.QuantOf("Qwen3 Coder 30B IQ2_M.gguf") != "IQ2_M" || LauncherLogic.QuantOfFileType("32") != "BF16" ||
                described is not [{ Quant: "Q4_K_M", Group: "Text only" }, { Quant: "Q8_0", Group: "Image generation" }])
                return Fail($"quant and kind: {string.Join(", ", described.Select(d => $"{d.Quant} {d.Group}"))}");
            CodeVisionToggle.IsChecked = false;
            ExtraToggle_Click(CodeVisionToggle, new RoutedEventArgs());
            if (_settings.UseVision || ChatVisionToggle.IsChecked != false || JsonNode.Parse(LauncherLogic.BuildChatRequest("planner", [], CurrentExtras()))?["vision"]?.GetValue<bool>() != false)
                return Fail("the Vision switch");
            CodeVisionToggle.IsChecked = true;
            ExtraToggle_Click(CodeVisionToggle, new RoutedEventArgs());
            if (!_settings.UseVision || ChatVisionToggle.IsChecked != true)
                return Fail("the Vision switch back on");
            // MAIN on a model with no projector: Code's switch greys with the tip
            var mainLoad = JsonNode.Parse(File.ReadAllText(config))!["roles"]!["planner"]!["auto_load"]?.ToJsonString();
            File.Delete(Path.Combine(sight, "mmproj-F16.gguf"));
            WriteConfigs(WorkerConfigTargets(), target =>
            {
                var root = JsonNode.Parse(File.ReadAllText(target))!.AsObject();
                root["roles"]!["planner"]!["auto_load"] = Load();
                WriteJson(target, root);
                return null;
            });
            RefreshVision();
            for (var i = 0; i < 50 && CodeVisionToggle.IsEnabled; i++)
                await Task.Delay(100);
            var greyed = !CodeVisionToggle.IsEnabled && CodeVisionToggle.ToolTip as string == "Vision is not supported for the selected model(s).";
            snap("vision-greyed", null);
            WriteConfigs(WorkerConfigTargets(), target =>
            {
                var root = JsonNode.Parse(File.ReadAllText(target))!.AsObject();
                if (mainLoad is null)
                    root["roles"]!["planner"]!.AsObject().Remove("auto_load");
                else
                    root["roles"]!["planner"]!["auto_load"] = JsonNode.Parse(mainLoad);
                WriteJson(target, root);
                return null;
            });
            RefreshVision();
            if (!greyed)
                return Fail("a model that cannot see greys the switch");
            var (m, p, s) = (model.TranslatePoint(new Point(), WorkerSettingsHost), prompts.TranslatePoint(new Point(), WorkerSettingsHost),
                sampling.TranslatePoint(new Point(), WorkerSettingsHost));
            if (Math.Abs(m.Y - p.Y) > 2 || p.X <= m.X + 100 || s.Y <= m.Y + 100)
                return Fail($"the layout: model {m}, prompts {p}, sampling {s}");
            if (SmokeFind<TextBlock>(WorkerSettingsHost, t => t.Text.StartsWith("Same as LEAD:") && t.IsVisible) is null || SmokeButton("Remove") is not null ||
                SmokeFind<TextBox>(WorkerSettingsHost, t => t.Tag as string == "ChatSystemPrompt") is not null)
                return Fail("Same as LEAD, no chat prompt");
            TextBox Box() => SmokeFind<TextBox>(WorkerSettingsHost, t => t.Tag as string == "Prompt:compactor.txt")!;
            Button Icon(string tag) => SmokeFind<Button>(WorkerSettingsHost, b => b.Tag as string == tag)!;
            var original = DefaultPromptText("compactor.txt");
            if (original.Length == 0 || Box().Text != original || !Box().IsReadOnly)
                return Fail("the compactor prompt");
            // the buttons sit above the box: they never cover its text
            if (Icon("EditPrompt").TranslatePoint(new Point(0, Icon("EditPrompt").ActualHeight), WorkerSettingsHost).Y > Box().TranslatePoint(new Point(), WorkerSettingsHost).Y + 1)
                return Fail("the buttons cover the prompt");
            snap("worker-settings-wide", null);

            SmokeClick(Icon("EditPrompt"));
            if (Box().IsReadOnly)
                return Fail("edit unlocks");
            Box().Text = "Summarize in haiku.";
            SmokeClick(SmokeButton("Save"));
            await Idle();
            var configNow = JsonNode.Parse(File.ReadAllText(config))!["roles"] as JsonObject;
            if (!File.Exists(overridden) || File.ReadAllText(overridden).Trim() != "Summarize in haiku." || LauncherLogic.CompactorHasOwn(configNow) ||
                Box().Text != "Summarize in haiku." || !Box().IsReadOnly)
                return Fail("a saved prompt");
            SmokeClick(Icon("ResetPrompt"));
            if (Box().Text != original)
                return Fail("reset");
            SmokeClick(SmokeButton("Save"));
            await Idle();
            if (File.Exists(overridden))
                return Fail("a reset prompt keeps its file");

            // "+ New preset": the default workers (no custom ones, the workflow mode kept), the template's settings, COMPACTOR on LEAD.
            if (BuildPresetsMenu().Items.OfType<MenuItem>().FirstOrDefault()?.Header as string != "+ New preset…")
                return Fail("the menu item");
            SavePreset("smoke-before");
            var mode = _settings.WorkflowMode;
            NewPreset("smoke-fresh");
            await Idle();
            var template = JsonNode.Parse(File.ReadAllText(Path.Combine(_workflowRoot!, "config.json")))!["roles"]!;
            var now = JsonNode.Parse(File.ReadAllText(config))!["roles"]!;
            if (_settings.Presets.FirstOrDefault(x => x.Name == "smoke-fresh") is not { } fresh || fresh.Workers.Any(w => w.Custom) || _settings.WorkflowMode != mode ||
                _cards.Any(c => c.IsCustom) || !JsonNode.DeepEquals(now["planner"]?["auto_load"], template["planner"]?["auto_load"]) ||
                (string?)now[LauncherLogic.Compactor]?["same_as"] != "planner")
                return Fail("the fresh preset");
            ApplyPreset(_settings.Presets.First(x => x.Name == "smoke-before"));
            _settings.Presets.RemoveAll(x => x.Name.StartsWith("smoke-", StringComparison.Ordinal));
            _settings.Save();
            return true;
        }
        finally
        {
            (_settings.ChatWorker, _codeChoice, DetailsColumn.Width, _settings.PromptsExpanded) = (chatWorker, ask, width, expanded);
            if (File.Exists(overridden))
                File.Delete(overridden);
        }
    }

    private async Task<bool> ModelSettingsSmoke(RoleCard planner, string config, Action<string, FrameworkElement?> snap)
    {
        static Task Idle() => Task.Delay(400);
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: worker tabs: {what}"); return false; }
        static async Task<bool> Until(Func<bool> done)
        {
            for (var i = 0; i < 50 && !done(); i++)
                await Task.Delay(100);
            return done();
        }
        ComboBox Model() => SmokeFind<ComboBox>(WorkerSettingsHost, c => c.Tag as string == "Model")!;
        JsonNode Role() => JsonNode.Parse(File.ReadAllText(config))!["roles"]!["planner"]!;

        var models = Path.Combine(AppSettings.DataRoot, "smoke-models-window");
        var fake = Path.Combine(models, "Fake-Model-Q4_K_M.gguf");
        Directory.CreateDirectory(models);
        File.WriteAllText(fake, "");
        var ask = _codeChoice;
        _settings.ModelsFolder = models;
        var asked = 0;
        _codeChoice = _ => { asked++; return null; }; // nothing below leaves a tab with unsaved edits
        try
        {
            // The rail card's gear: the column opens on that worker's tab.
            var implementer = CardFor("implementer")!;
            SetWorkerRail(true);
            await Idle();
            RailDots.UpdateLayout();
            ShowRailCard((FrameworkElement)VisualTreeHelper.GetChild(RailDots.ItemContainerGenerator.ContainerFromItem(implementer), 0));
            await Idle();
            if (SmokeVisuals(RailCard).OfType<Button>().FirstOrDefault(b => b.Tag as string == "ModelSettingsGear") is not { } gear)
                return Fail("hover card gear");
            SmokeClick(gear);
            await Idle();
            if (RailPopup.IsOpen || !DetailsPanel.IsVisible || _settings.DetailsCollapsed || _shownWorker != implementer || !implementer.IsSelected)
                return Fail("the gear opens the tab");

            // One tab per shown worker; three fit the default column.
            var width = (double)Resources["WorkerTabWidth"];
            if (_cards.Any(c => SmokeTab(c) is not { } t || t.IsVisible != (!c.Hidden || c.IsSelected)) || Math.Abs(width - Math.Clamp(WorkerTabsScroll.ActualWidth / 3 - 6, 88, WorkerTabMax)) > 0.5 ||
                3 * (width + 6) > WorkerTabsScroll.ActualWidth + 1 || (_cards.Count(c => !c.Hidden) <= 3 && WorkerTabsScroll.ScrollableWidth > 1) ||
                Math.Abs(SmokeTab(planner)!.ActualWidth - width - 6) > 0.5)
                return Fail("tabs at the default width");
            var texts = SmokeVisuals(SmokeTab(planner)!).OfType<TextBlock>().Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text).ToList();
            if (!texts.Contains(planner.Title) || !texts.Contains(planner.ModelName) || !texts.Contains(planner.TokensPerSec) ||
                !texts.Contains($"{planner.TaskTokensShort} task · {planner.TurnTotalShort} turn"))
                return Fail("tab text: " + string.Join(" | ", texts));
            snap("tabs-default", null);

            // A narrow column: the tabs keep their minimum width and the strip scrolls, also with the mouse wheel.
            DetailsColumn.MinWidth = 260; // the tab strip at the narrowest column (a small window's)
            DetailsColumn.Width = new GridLength(260);
            await Idle();
            if (WorkerTabsScroll.ScrollableWidth <= 0 || (double)Resources["WorkerTabWidth"] != 88)
                return Fail("a narrow column scrolls");
            WorkerTabsScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = PreviewMouseWheelEvent });
            await Idle();
            if (WorkerTabsScroll.HorizontalOffset <= 0)
                return Fail("the wheel scrolls the strip");
            snap("tabs-narrow", null);
            SetWorkerRail(false); // back to the saved width
            await Idle();

            // Selecting a tab shows that worker's settings; the model list is not loaded until it opens.
            SmokeClickTab(planner);
            await Idle();
            if (_shownWorker != planner || !planner.IsSelected || implementer.IsSelected || _settings.SelectedWorker != "planner" ||
                SmokeFind<TextBox>(WorkerSettingsHost, t => t.Tag as string == "ChatSystemPrompt") is null ||
                Model().Items.Count != 1 || Model().Items.OfType<ComboBoxItem>().Any(i => i.Tag is not null))
                return Fail("the Planner's tab");
            // A click on the box opens the picker (not the box's own list), which lists the models once loaded.
            async Task<PickerEntry?> FakeItem()
            {
                var box = Model();
                box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonDownEvent });
                if (_modelPickerTest is { IsOpen: true } || box.IsDropDownOpen)
                    return null; // the press opens nothing: its release would close what it opened
                box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent });
                await Until(() => _modelPickerTest is { IsOpen: true } p && p.Rows.Any(r => r.Tag is PickerEntry));
                var entry = _modelPickerTest?.Rows.Select(r => r.Tag as PickerEntry).FirstOrDefault(e => e?.Choice.File == fake);
                _modelPickerTest?.Close();
                return entry;
            }

            // The model's defaults arrive off the UI thread and never replace an edit; re-selecting "(current)" restores the
            // fields (no model_path: the load options empty again), so Save is not refused.
            if (await FakeItem() is not { } fakeItem || !Model().IsEnabled)
                return Fail("the model list");
            // A press anywhere in the window closes it at once (StaysOpen alone missed presses while the search box held the
            // mouse, so it stayed open, and a rebuilt tab left it at the window's corner)
            await Task.Delay(450); // past JustClosed, so the release opens it again
            Model().RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent });
            await Until(() => _modelPickerTest is { IsOpen: true });
            RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseDownEvent });
            if (_modelPickerTest is not { IsOpen: false })
                return Fail("a press outside the model picker");
            snap("tabs-settings", null);
            SmokeField("top_k").Text = "33";
            _modelPickerTest!.Choose(fakeItem); // an unreadable GGUF: the static defaults
            if (!await Until(() => SmokeField("max_seq_length").Text == "32768") || SmokeField("n_batch").Text != "2048" || SmokeField("seed").Text != "" ||
                SmokeField("max_tokens").Text != "" || SmokeField("top_k").Text != "33" || Model().SelectedItem is not ComboBoxItem { Tag: ModelChoice })
                return Fail("a model's defaults");
            _modelPickerTest.Choose(null); // "(current)": the model the worker had
            if (!await Until(() => SmokeField("max_seq_length").Text == "") || SmokeField("n_batch").Text != "")
                return Fail("the current model");
            var body = WorkerSettingsHost.Child;
            SmokeClick(SmokeButton("Save"));
            await Idle();
            if (_shownWorker != planner || WorkerSettingsHost.Child == body || _workerDirty() || Role()["auto_load"] is not null || SmokeField("top_k").Text != "33")
                return Fail("Save keeps the tab");

            // Save: a model from the list and a temperature.
            if (await FakeItem() is not { } fakeAgain)
                return Fail("the model list after Save");
            _modelPickerTest!.Choose(fakeAgain);
            if (!await Until(() => SmokeField("max_seq_length").Text == "32768"))
                return Fail("a model's defaults after Save");
            SmokeField("temperature").Text = "0.45";
            SmokeClick(SmokeButton("Save"));
            await Idle();
            var load = Role();
            if (_shownWorker != planner || (double?)load["sampling"]?["temperature"] != 0.45 || (string?)load["auto_load"]?["model_path"] != "Fake-Model-Q4_K_M" ||
                (int?)load["auto_load"]?["max_seq_length"] != 32768 || (double?)load["sampling"]?["top_p"] != 0.95 ||
                (string?)load["auto_load"]?["gguf_variant"] != "Q4_K_M" || (string?)load["auto_load"]?["gguf_file"] != fake || planner.ModelName != LauncherLogic.DescribeModel("Fake-Model-Q4_K_M", "Q4_K_M").Name)
                return Fail("Save a model");
            // The quantization shows as text, not a box; the picker searches fuzzily, groups (Text only, then Vision capable;
            // image generation listed but not pickable), sorts each group by name, quant or size, and Enter picks.
            string? QuantText() => SmokeFind<TextBlock>(WorkerSettingsHost, t => t.Tag as string == "QuantizationText") is { } q
                ? string.Concat(q.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) : null;
            if (QuantText() != "Quantization - Q4_K_M" ||
                SmokeFind<TextBox>(WorkerSettingsHost, t => t.Tag as string == "gguf_variant") is not null)
                return Fail($"the quantization line: '{QuantText()}'");
            if (!FuzzyMatch("Qwen3.5-4B Q4_K_M Text only", "qw 4b q4") || FuzzyMatch("Gemma", "qx") || QuantKey("Q4_K_M").Bits != 4 ||
                QuantKey("UD-IQ3_XXS").Bits != 3 || QuantKey("BF16").Bits != 16 || QuantKey("—").Bits != double.MaxValue)
                return Fail("fuzzy search and quant order");
            {
                var picks = new List<ModelChoice?>();
                var test = new ModelPicker(Model(), "Current one", picks.Add);
                static ModelChoice C(string n) => new(n, n, n, null, n);
                var vision = C("v");
                test.SetEntries([new(C("b"), "Beta", "Q4_K_M", 9L << 30, PickerGroups[0]), new(C("a"), "Alpha", "Q8_0", 3L << 30, PickerGroups[0]),
                    new(vision, "Avis", "Q6_K", 5L << 30, PickerGroups[1]), new(C("i"), "Flux", "Q4_0", 1L << 30, PickerGroups[2])], "");
                test.Open(null, null);
                await Idle();
                snap("model-picker", test.Card);
                string Names() => string.Join(",", test.Rows.Select(r => (r.Tag as PickerEntry)?.Name ?? "current"));
                var byName = Names();
                test.SortBy("Quant");
                var byQuant = Names();
                test.SortBy("Size");
                test.SortBy("Size"); // again: largest first
                var bySizeDown = Names();
                test.Search.Text = "avs";
                var searched = Names();
                test.Key(Key.Enter);
                if (byName != "current,Alpha,Beta,Avis" || byQuant != "current,Beta,Alpha,Avis" || bySizeDown != "current,Beta,Alpha,Avis" ||
                    searched != "Avis" || picks is not [{ } picked] || picked != vision || test.IsOpen)
                    return Fail($"the picker: {byName} / {byQuant} / {bySizeDown} / {searched}");
            }

            // Revert drops an edit; an invalid value shows the error under the field; neither writes anything.
            var bytes = File.ReadAllBytes(config);
            SmokeField("temperature").Text = "0.9";
            if (!_workerDirty())
                return Fail("an edit is unsaved");
            SmokeClick(SmokeButton("Revert"));
            await Idle();
            if (SmokeField("temperature").Text != "0.45" || _workerDirty() || !File.ReadAllBytes(config).SequenceEqual(bytes))
                return Fail("Revert");
            SmokeField("temperature").Text = "5";
            SmokeField("top_p").Text = "0.5";
            SmokeClick(SmokeButton("Save"));
            await Idle();
            if (SmokeFind<TextBlock>(WorkerSettingsHost, t => t.Visibility == Visibility.Visible && t.Text.StartsWith("A number, 0 to 2")) is null ||
                !File.ReadAllBytes(config).SequenceEqual(bytes))
                return Fail("an invalid value");
            SmokeClick(SmokeButton("Revert"));
            await Idle();

            // The chat system prompt: saved to roles.planner.chat_system_prompt, removed when blank.
            SmokeField("ChatSystemPrompt").Text = "Answer briefly.";
            SmokeClick(SmokeButton("Save"));
            await Idle();
            if ((string?)Role()["chat_system_prompt"] != "Answer briefly." || SmokeField("ChatSystemPrompt").Text != "Answer briefly.")
                return Fail("the chat system prompt");
            SmokeField("ChatSystemPrompt").Text = "  ";
            SmokeClick(SmokeButton("Save"));
            await Idle();
            if (Role().AsObject().ContainsKey("chat_system_prompt") || SmokeField("ChatSystemPrompt").Text != "")
                return Fail("a blank chat system prompt");
            return asked == 0 || Fail("asked");
        }
        finally
        {
            (_settings.ModelsFolder, _codeChoice) = ("", ask);
            RefreshWorkerTab(force: true);
            Directory.Delete(models, recursive: true);
        }
    }
}

// The settings body's groups: one column, or two from TwoColumnsAt wide, each group whole in the shorter column, in order.
// Re-flows on every measure (a width or visibility change), so the editors, their state and focus stay put.
internal sealed class SettingsColumns : Panel
{
    public const double TwoColumnsAt = 620;
    private const double ColumnGap = 28, RowGap = 26;
    private readonly List<(int Column, double Y)> _slots = [];
    private bool _two;

    protected override Size MeasureOverride(Size available)
    {
        _two = !double.IsInfinity(available.Width) && available.Width >= TwoColumnsAt;
        var width = _two ? (available.Width - ColumnGap) / 2 : available.Width;
        var heights = new double[_two ? 2 : 1];
        var widest = 0.0;
        _slots.Clear();
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            var column = _two && heights[1] < heights[0] ? 1 : 0;
            var y = heights[column] > 0 ? heights[column] + RowGap : 0;
            _slots.Add((column, y));
            if (child.Visibility != Visibility.Collapsed)
                (heights[column], widest) = (y + child.DesiredSize.Height, Math.Max(widest, child.DesiredSize.Width));
        }
        return new Size(double.IsInfinity(available.Width) ? widest : available.Width, heights.Max());
    }

    protected override Size ArrangeOverride(Size final)
    {
        var width = _two ? (final.Width - ColumnGap) / 2 : final.Width;
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(_slots[i].Column * (width + ColumnGap), _slots[i].Y, width, InternalChildren[i].DesiredSize.Height));
        return final;
    }
}

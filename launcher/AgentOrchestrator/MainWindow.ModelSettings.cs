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

// The worker card's gear: the Model settings window. Model, load, sampling and request options (ModelOptions); a built-in
// role's remote API fields; a custom worker's name, kind, description, system prompt and removal. Built-in roles write
// config.json on Save; custom workers keep their options in the launcher's settings and inherit what they leave empty
// from the Implementer. Cancel, × and Esc discard.
public partial class MainWindow
{
    private void ModelSettings_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RoleCard card)
            OpenModelSettings(card);
    }

    private void OpenModelSettings(RoleCard card)
    {
        if (_process is not null || !card.Editable || BuildModelSettings(card) is not { } window)
            return;
        if (_settingsShow is { } show)
            show(window);
        else
            window.ShowDialog();
    }

    // The composer's target chip: the Models window, a list of the workers (name, model, activity dot) beside the selected
    // one's model settings. Save keeps the window open; switching worker or closing with unsaved changes asks first.
    private DialogShell? OpenModelConfig(IReadOnlyList<RoleCard> cards)
    {
        if (_process is not null || cards.Count == 0 || cards.Any(c => !c.Editable))
            return null;
        var host = new Border();
        var list = new StackPanel { Width = 200, Margin = new Thickness(0, 0, 18, 0) };
        DockPanel.SetDock(list, Dock.Left);
        var window = new DialogShell(this, "Models", new DockPanel { Children = { list, host } }, large: true);
        RoleCard? selected = null;
        var dirty = () => false;
        var rows = new Dictionary<RoleCard, ToggleButton>();
        bool Show(RoleCard card)
        {
            var shown = host.Child;
            host.Child = null; // the radio groups are shared by name: never two bodies at once
            if (ModelSettingsBody(card, () => Show(card), window.Close, "Close") is not { } built)
            {
                host.Child = shown;
                return false;
            }
            (host.Child, dirty, selected) = (built.Body, built.IsDirty, card);
            foreach (var (c, row) in rows)
                row.IsChecked = c == card;
            return true;
        }
        foreach (var card in cards)
        {
            var name = new TextBlock { Text = ChatName(card, card.Role), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            if (card.IsCustom)
                name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(RoleCard.Title)) { Source = card });
            var model = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            model.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(RoleCard.ModelName)) { Source = card });
            model.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var dot = new ContentPresenter { Content = card, ContentTemplate = (DataTemplate)FindResource("ActivityDot"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Right);
            var row = rows[card] = new ToggleButton { Content = new DockPanel { Children = { dot, new StackPanel { Children = { name, model } } } }, Tag = card.Role, Padding = new Thickness(14, 7, 6, 7) };
            row.SetResourceReference(StyleProperty, "NavToggle");
            row.Click += (_, _) =>
            {
                if (card != selected && (!dirty() || SidebarAsk("Unsaved changes", $"Discard the unsaved changes to {ChatName(selected, selected?.Role ?? "")}?", "Discard") is not null))
                    Show(card);
                row.IsChecked = card == selected;
            };
            list.Children.Add(row);
        }
        window.Closing += (_, e) => e.Cancel = selected is not null && _cards.Contains(selected) && dirty() &&
            SidebarAsk("Unsaved changes", $"Discard the unsaved changes to {ChatName(selected, selected.Role)}?", "Discard") is null;
        window.Closed += (_, _) => host.Child = null;
        if (!Show(cards[0]))
            return null;
        if (_settingsShow is { } show)
            show(window);
        else
            window.ShowDialog();
        return window;
    }

    private static void WriteJson(string path, JsonNode root) => File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));

    // The window, or null (status set) when a built-in role has no readable config.json.
    private DialogShell? BuildModelSettings(RoleCard card)
    {
        DialogShell? dialog = null;
        if (ModelSettingsBody(card, () => dialog?.Close(), () => dialog?.Close(), "Cancel") is not { } built)
            return null;
        return dialog = new DialogShell(this, card.Title, built.Body, large: true);
    }

    // The window's contents, or null (status set) when a built-in role has no readable config.json. saved: after a successful
    // Save; close: Cancel (labelled closeLabel), Remove worker, or a config.json that could not be written. IsDirty: a field
    // differs from what the body was built with. The radio buttons share fixed group names, so host one body at a time.
    private (FrameworkElement Body, Func<bool> IsDirty)? ModelSettingsBody(RoleCard card, Action saved, Action close, string closeLabel)
    {
        var path = card.IsCustom ? "" : ActiveConfigPath();
        JsonObject? role = null;
        LauncherLogic.RemoteRole? remoteNow = null;
        if (!card.IsCustom)
        {
            if (!File.Exists(path))
            {
                SetStatus("No config.json to change", "#F7C873");
                return null;
            }
            try
            {
                role = JsonNode.Parse(File.ReadAllText(path))!["roles"]![card.Role]!.AsObject();
                remoteNow = LauncherLogic.ReadRemoteRole(path, card.Role);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
            {
                Log($"Could not read config.json: {exception.Message}");
                SetStatus("Could not read config.json", "#FF667A");
                return null;
            }
        }
        var current = role is null ? ModelOptions.Effective(card.Config) : ModelOptions.Read(role);
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

        // Left: the section names; right: the sections, scrolling.
        var sections = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var scroll = new ScrollViewer { Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var nav = new StackPanel { Width = 150, Margin = new Thickness(0, 0, 18, 0) };
        (StackPanel Panel, Button Link) Section(string name, string help)
        {
            var panel = new StackPanel { Margin = new Thickness(0, sections.Children.Count == 0 ? 0 : 26, 0, 0) };
            panel.Children.Add(new TextBlock { Text = name, FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 14, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(Muted(help));
            sections.Children.Add(panel);
            var link = new Button
            {
                Content = name, Style = (Style)FindResource("SidebarRow"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6),
            };
            link.Click += (_, _) => scroll.ScrollToVerticalOffset(panel.TranslatePoint(new Point(0, 0), sections).Y);
            nav.Children.Add(link);
            return (panel, link);
        }

        var editors = new Dictionary<string, Control>();
        var fieldErrors = new Dictionary<string, TextBlock>();
        Grid Fields(string section)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            int row = -1, col = 2;
            foreach (var o in ModelOptions.All.Where(o => o.Section == section))
            {
                var wide = o.Kind is ModelOptions.Kind.Multiline or ModelOptions.Kind.StopList or ModelOptions.Kind.ArgList;
                if (wide || col == 2)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    (row, col) = (row + 1, 0);
                }
                var value = current.GetValueOrDefault(o.Key, "");
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
                        box.FontFamily = new FontFamily("Consolas");
                    editor = box;
                }
                editors[o.Key] = editor;
                var cell = new StackPanel
                {
                    Margin = new Thickness(col == 0 ? 0 : 9, 12, col == 0 && !wide ? 9 : 0, 0),
                    Children = { Label(o.Label), Muted(o.Help), editor, (fieldErrors[o.Key] = ErrorLine()) }, ToolTip = o.Key,
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, col);
                if (wide)
                    Grid.SetColumnSpan(cell, 2);
                grid.Children.Add(cell);
                col = wide ? 2 : col + 1;
            }
            return grid;
        }

        // Custom workers: what the card's menu and the old Settings dialog offered.
        TextBox? nameBox = null, description = null, prompt = null;
        RadioButton[]? kinds = null;
        if (card.IsCustom)
        {
            var w = card.Config;
            var (panel, _) = Section("Worker", "This custom worker's name, kind and standing instructions.");
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
            foreach (var k in kinds)
                k.Checked += (_, _) => Explain();
            Explain();
            description = Box(w.Description, "Description", 58);
            prompt = Box(w.SystemPrompt, "SystemPrompt", 170);
            prompt.FontFamily = new FontFamily("Consolas");
            var remove = new Button { Content = "Remove worker", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
            remove.Click += (_, _) =>
            {
                if (AskDialog("Remove worker", $"Remove {card.Title}? Its settings are deleted.", "Remove") is null)
                    return;
                RemoveWorker(card); // first: the Models window asks before closing only while its worker exists
                close();
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

        // Built-in roles: Local (Unsloth / llama.cpp) or a remote OpenAI-compatible API.
        RadioButton[]? modes = null;
        TextBox[]? remoteBoxes = null;
        StackPanel? remotePanel = null;
        if (!card.IsCustom)
        {
            var (panel, _) = Section("Model source", "Local loads the model on this computer; Remote API sends requests to an OpenAI-compatible endpoint.");
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

        var (loadPanel, loadLink) = Section("Load", $"Unsloth Studio load options. Load options apply at the next model load. {emptyMeans}");
        loadPanel.Children.Add(Fields("Load"));
        var (samplingPanel, _) = Section("Sampling", $"Sent with every request; top_k, min_p and repetition_penalty go in extra_body (Unsloth only). {emptyMeans}");
        samplingPanel.Children.Add(Fields("Sampling"));
        var (requestPanel, _) = Section("Request", $"How long the orchestrator waits for this worker. {emptyMeans}");
        requestPanel.Children.Add(Fields("Request"));

        // The model list loads in the background; the window stays usable meanwhile.
        var modelBox = new ComboBox { Tag = "Model", IsEnabled = false, Width = 420 };
        modelBox.Items.Add(new ComboBoxItem { Content = "Loading models…" });
        modelBox.SelectedIndex = 0;
        var modelStatus = Muted("Loading models…");
        modelStatus.VerticalAlignment = VerticalAlignment.Center;
        modelStatus.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(modelBox, Dock.Left);
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 16), Children = { Label("Model"), new DockPanel { Margin = new Thickness(0, 6, 0, 0), Children = { modelBox, modelStatus } } } };
        ModelChoice? initial = null;
        var remoteMode = () => modes?[1].IsChecked == true;
        var listed = false;
        async void LoadModels()
        {
            try
            {
                var choices = await FetchModelChoicesAsync();
                modelBox.Items.Clear();
                ComboBoxItem? selected = null;
                foreach (var choice in choices)
                {
                    var described = LauncherLogic.DescribeModel(choice.Display, choice.Variant);
                    var item = new ComboBoxItem { Content = choice.Label, Tag = choice };
                    modelBox.Items.Add(item);
                    if (selected is null && card.ModelName == described.Name && (card.Quant == described.Quant || described.Quant == "—"))
                        (selected, initial) = (item, choice);
                }
                if (selected is null)
                    modelBox.Items.Insert(0, selected = new ComboBoxItem { Content = $"{card.ModelName}  (current)" });
                modelBox.SelectedItem = selected;
                listed = true;
                modelBox.IsEnabled = !remoteMode();
                modelStatus.Text = choices.Count == 0 ? "No installed text models found." : LocalRuntime ? "GGUF files on this computer (llama.cpp runtime)." : "Models installed in Unsloth.";
                // A model brings its own quantization: the Load section's gguf_variant follows the pick.
                modelBox.SelectionChanged += (_, _) =>
                {
                    if (modelBox.SelectedItem is ComboBoxItem { Tag: ModelChoice picked } && editors["gguf_variant"] is TextBox variant)
                        variant.Text = picked.Variant ?? "";
                };
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                modelBox.Items.Clear();
                modelBox.Items.Add(new ComboBoxItem { Content = card.ModelName });
                modelBox.SelectedIndex = 0;
                modelStatus.Text = $"Could not list models ({exception.Message}); the current model is kept.";
                modelStatus.Foreground = red;
            }
        }

        void SyncMode()
        {
            var remote = remoteMode();
            remotePanel?.SetValue(VisibilityProperty, remote ? Visibility.Visible : Visibility.Collapsed);
            loadPanel.Visibility = loadLink.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            modelBox.IsEnabled = listed && !remote;
        }
        foreach (var mode in modes ?? [])
            mode.Checked += (_, _) => SyncMode();
        SyncMode();

        var error = ErrorLine();
        error.Margin = new Thickness(0, 0, 0, 8);
        var save = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        var cancel = new Button { Content = closeLabel, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var footer = new StackPanel
        {
            Margin = new Thickness(0, 14, 0, 0),
            Children = { error, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } } },
        };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(nav, Dock.Left);
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

        save.Click += (_, _) =>
        {
            var fields = editors.ToDictionary(p => p.Key, p => p.Value is ComboBox c ? c.SelectedItem is string s && s != "(default)" ? s : "" : ((TextBox)p.Value).Text);
            var choice = Picked();
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
                if (string.IsNullOrWhiteSpace(prompt!.Text))
                    errors["SystemPrompt"] = "Enter a system prompt: the orchestrator ignores a worker without one.";
                else if (kind != "reviewer" && string.IsNullOrWhiteSpace(description!.Text))
                    errors["Description"] = "Enter a description: the Planner assigns Specialist tasks based on it.";
                if (ShowErrors(errors))
                    return;
                if (choice is not null)
                    AssignModel(card, choice);
                w.Kind = kind;
                w.Description = description!.Text.Trim();
                w.SystemPrompt = prompt.Text;
                w.ModelOptions = fields.Where(p => p.Value.Trim().Length > 0).ToDictionary();
                (w.Temperature, w.MaxTokens, w.RepetitionPenalty, w.ReasoningBudget, w.ContextLength, w.Thinking) = ("", "", "", "", "", "Auto"); // now in ModelOptions
                if (title != card.Title)
                    card.Title = title;
                SaveWorkers();
                SyncCustomWorkers();
                Log($"{card.Title}: model settings saved");
                Toast($"{card.Title}: settings saved");
                saved();
                return;
            }

            var remote = remoteMode() ? new LauncherLogic.RemoteRole(remoteBoxes![0].Text, remoteBoxes[1].Text, remoteBoxes[2].Text, "") : null;
            var check = (JsonObject)role!.DeepClone(); // validate everything before any file is touched
            if (choice is not null && remote is null)
                SetRoleModel(check, choice);
            var problems = ModelOptions.Apply(check, fields, includeLoad: remote is null);
            if (remote is not null && LauncherLogic.ValidateRemoteRole(remote) is { } remoteProblem)
                problems["Remote"] = remoteProblem;
            if (ShowErrors(problems))
                return;
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
                    var settings = root["roles"]![card.Role]!.AsObject();
                    if (choice is not null && remote is null)
                        SetRoleModel(settings, choice);
                    if (ModelOptions.Apply(settings, fields, includeLoad: remote is null) is { Count: > 0 } refused)
                        return string.Join(" ", refused.Values);
                    WriteJson(target, root);
                    return null;
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
            {
                Log($"Could not update config.json: {exception.Message}");
                SetStatus("Could not update config.json", "#FF667A");
                close();
                return;
            }
            if (problem is not null)
            {
                ShowErrors(new() { [""] = problem });
                return;
            }
            if (choice is not null && remote is null)
            {
                card.ModelId = choice.Display;
                card.ModelVariant = choice.Variant;
                card.SetModel(LauncherLogic.DescribeModel(choice.Display, choice.Variant));
                SaveWorkers();
            }
            Log($"{card.Title}: model settings saved{(remote is null ? "" : $" (remote API {remote.BaseUrl.Trim()}, model {remote.Model.Trim()})")}");
            if (remote is not null && Environment.GetEnvironmentVariable(remote.KeyEnv.Trim()) is null)
                Toast($"{card.Title}: set {remote.KeyEnv.Trim()} in your environment, then restart the launcher", "#F7C873");
            else
                Toast($"{card.Title}: settings saved");
            PrefillCards();
            saved();
        };
        LoadModels();
        ModelChoice? Picked() => modelBox.SelectedItem is ComboBoxItem { Tag: ModelChoice c } && c != initial ? c : null;
        string State() => string.Join("\u0001", editors.Values.Concat(new Control?[] { nameBox, description, prompt }).Concat(remoteBoxes ?? []).Concat(modes ?? []).Concat(kinds ?? [])
            .Select(c => c switch { TextBox t => t.Text, ComboBox b => b.SelectedItem as string, RadioButton r => r.IsChecked.ToString(), _ => "" })) + (Picked() is not null);
        var clean = State();
        return (new DockPanel { Children = { top, footer, nav, scroll } }, () => State() != clean);
    }

    // --ui-smoke: the target chips open the Models window (borderless, large, owned) listing Single's target, Multi's three
    // workers, or the Code target; Save writes the worker and keeps the window open; switching worker with an unsaved change
    // asks, and Discard writes nothing. config: the active config.json, holding roles.implementer.
    private async Task<bool> ModelConfigSmoke(string config, Action<string, FrameworkElement?> snap)
    {
        static Task Idle() => Task.Delay(400);
        static IEnumerable<DependencyObject> Tree(DependencyObject d) =>
            LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().SelectMany(c => Tree(c).Prepend(c));
        static void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        static List<ToggleButton> Rows(Window w) => Tree(w).OfType<ToggleButton>().Where(t => t.GetType() == typeof(ToggleButton)).ToList();
        static string Listed(Window w) => string.Join(",", Rows(w).Select(r => (string)r.Tag + (r.IsChecked == true ? "*" : "")));
        static TextBox Field(Window w, string key) => Tree(w).OfType<TextBox>().First(t => t.Tag as string == key);
        async Task<DialogShell?> Open(Button chip)
        {
            Click(chip);
            await Idle();
            return OwnedWindows.OfType<DialogShell>().LastOrDefault(w => w.IsVisible && w.Title == "Models");
        }
        var saved = (_settings.ChatMode, _settings.ChatWorker, _settings.CodeWorker, _settings.Runtime, _settings.ModelsFolder, _codeChoice, _settings.Theme);
        var models = Path.Combine(AppSettings.DataRoot, "smoke-models-config");
        Directory.CreateDirectory(models);
        (_settings.Runtime, _settings.ModelsFolder) = (LauncherLogic.LlamaRuntime, models); // a disk scan: never a real Unsloth server
        var asked = new List<string>();
        string? answer = null;
        _codeChoice = title => { asked.Add(title); return answer; };
        try
        {
            // Single: the target; Code: the Code target. Closing with nothing changed asks nothing.
            (_settings.ChatMode, _settings.ChatWorker, _settings.CodeWorker) = ("single", "reviewer", "planner");
            RefreshChatTarget();
            if (await Open(ChatTargetChip) is not { } single || single.WindowStyle != WindowStyle.None || single.Owner != this || single.Width < 600 ||
                Listed(single) != "reviewer*" || ChatTargetChip.ToolTip as string != "Configure model")
                return false;
            single.Close();
            if (single.IsVisible || await Open(CodeTargetChip) is not { } code || Listed(code) != "planner*" || CodeTargetChip.ToolTip as string != "Configure model")
                return false;
            code.Close();

            // Multi: the three workers, the Planner first.
            _settings.ChatMode = "multi";
            RefreshChatTarget();
            if (code.IsVisible || asked.Count > 0 || ChatTargetChip.Cursor != Cursors.Hand || await Open(ChatTargetChip) is not { } multi ||
                Listed(multi) != "planner*,implementer,reviewer")
                return false;
            snap("33-model-config", multi);
            _settings.Theme = "Parchment";
            ApplyTheme();
            await Idle();
            snap("33-model-config-parchment", multi);
            _settings.Theme = saved.Theme;
            ApplyTheme();

            // Save keeps the window open on the Implementer.
            Click(Rows(multi)[1]);
            await Idle();
            Field(multi, "temperature").Text = "0.33";
            Click(Tree(multi).OfType<Button>().First(b => b.Content as string == "Save"));
            await Idle();
            if (!multi.IsVisible || Listed(multi) != "planner,implementer*,reviewer" ||
                (double?)JsonNode.Parse(File.ReadAllText(config))!["roles"]!["implementer"]!["sampling"]?["temperature"] != 0.33)
                return false;

            // An unsaved change: switching asks; no answer stays, Discard switches and writes nothing; closing then asks nothing.
            var bytes = File.ReadAllBytes(config);
            Field(multi, "temperature").Text = "0.77";
            Click(Rows(multi)[2]);
            await Idle();
            if (asked.Count != 1 || Listed(multi) != "planner,implementer*,reviewer" || Field(multi, "temperature").Text != "0.77")
                return false;
            answer = "";
            Click(Rows(multi)[2]);
            await Idle();
            if (asked.Count != 2 || Listed(multi) != "planner,implementer,reviewer*")
                return false;
            multi.Close();
            return !multi.IsVisible && asked.Count == 2 && File.ReadAllBytes(config).SequenceEqual(bytes);
        }
        finally
        {
            answer = ""; // a check that failed with a change pending still closes the window
            foreach (var open in OwnedWindows.OfType<DialogShell>().Where(w => w.Title == "Models").ToList())
                open.Close();
            (_settings.ChatMode, _settings.ChatWorker, _settings.CodeWorker, _settings.Runtime, _settings.ModelsFolder, _codeChoice, _settings.Theme) = saved;
            ApplyTheme();
            RefreshChatTarget();
            Directory.Delete(models, recursive: true);
        }
    }

    // --ui-smoke: the cards have a gear and no Settings button or right-click menu; the gear opens this window (borderless,
    // large, owned), its model list has the GGUFs on disk (llama.cpp runtime), Save writes the role, Cancel and an invalid
    // value write nothing. config: the active config.json, holding roles.planner.
    private async Task<bool> ModelSettingsSmoke(RoleCard planner, string config, Action<string, FrameworkElement?> snap)
    {
        static Task Idle() => Task.Delay(400);
        static IEnumerable<DependencyObject> Visuals(DependencyObject d) =>
            Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(d)).Select(i => VisualTreeHelper.GetChild(d, i)).SelectMany(c => Visuals(c).Prepend(c));
        static T? Find<T>(DependencyObject d, Func<T, bool> match) where T : DependencyObject =>
            d is T t && match(t) ? t : LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().Select(c => Find(c, match)).FirstOrDefault(r => r is not null);
        static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        FrameworkElement? Container()
        {
            CardsList.UpdateLayout();
            return CardsList.ItemContainerGenerator.ContainerFromItem(planner) as FrameworkElement;
        }
        Button? Gear() => Container() is { } c ? Visuals(c).OfType<Button>().FirstOrDefault(b => b.Tag as string == "ModelSettingsGear") : null;

        // Compact card: a gear, and a right-click is no longer handled.
        _settings.Compact = true;
        ApplyCompact();
        await Idle();
        var root = Container() is { } compact ? Visuals(compact).OfType<FrameworkElement>().FirstOrDefault(e => e.Tag as string == "CardRoot") : null;
        var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = MouseRightButtonUpEvent };
        root?.RaiseEvent(rightClick);
        var compactGear = Gear();
        _settings.Compact = false;
        ApplyCompact();
        await Idle();
        if (root is null || rightClick.Handled || root.ContextMenu is not null || compactGear is null)
            return false;
        // Standard card: the gear, and no Settings button (Change stays).
        if (Container() is not { } standard || Gear() is not { } gear || Visuals(standard).OfType<Button>().Any(b => b.Content as string == "Settings") ||
            !Visuals(standard).OfType<Button>().Any(b => b.Content as string == "Change"))
            return false;

        var models = Path.Combine(AppSettings.DataRoot, "smoke-models-window");
        var fake = Path.Combine(models, "Fake-Model-Q4_K_M.gguf");
        Directory.CreateDirectory(models);
        File.WriteAllText(fake, "");
        var runtime = _settings.Runtime;
        (_settings.Runtime, _settings.ModelsFolder) = (LauncherLogic.LlamaRuntime, models);
        try
        {
            async Task<DialogShell?> Open()
            {
                Click(gear);
                await Idle();
                var window = OwnedWindows.OfType<DialogShell>().LastOrDefault(w => w.IsVisible && w.Title == planner.Title);
                for (var i = 0; window is not null && i < 50 && Find<ComboBox>(window, c => c.Tag as string == "Model")!.Items.OfType<ComboBoxItem>().All(item => item.Tag is null); i++)
                    await Task.Delay(100);
                return window;
            }
            ComboBox Model(DialogShell w) => Find<ComboBox>(w, c => c.Tag as string == "Model")!;
            TextBox Field(DialogShell w, string key) => Find<TextBox>(w, t => t.Tag as string == key)!;
            Button Labelled(DialogShell w, string label) => Find<Button>(w, b => b.Content as string == label)!;
            var bytes = File.ReadAllBytes(config);

            // Save: a model from the list and a temperature.
            if (await Open() is not { } window || window.WindowStyle != WindowStyle.None || window.Owner != this || window.Width < 600 ||
                Model(window).Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as ModelChoice)?.File == fake) is not { } fakeItem || !Model(window).IsEnabled)
                return false;
            snap("27-model-settings", window);
            Model(window).SelectedItem = fakeItem;
            Field(window, "temperature").Text = "0.45";
            Click(Labelled(window, "Save"));
            await Idle();
            var load = JsonNode.Parse(File.ReadAllText(config))!["roles"]!["planner"]!;
            if (window.IsVisible || (double?)load["sampling"]?["temperature"] != 0.45 || (string?)load["auto_load"]?["model_path"] != "Fake-Model-Q4_K_M" ||
                (string?)load["auto_load"]?["gguf_variant"] != "Q4_K_M" || (string?)load["auto_load"]?["gguf_file"] != fake || planner.ModelName != LauncherLogic.DescribeModel("Fake-Model-Q4_K_M", "Q4_K_M").Name)
                return false;

            // Cancel discards; an invalid value keeps the window open with the error under the field, and writes nothing.
            bytes = File.ReadAllBytes(config);
            if (await Open() is not { } cancelled)
                return false;
            Field(cancelled, "temperature").Text = "0.9";
            Click(Labelled(cancelled, "Cancel"));
            await Idle();
            if (cancelled.IsVisible || !File.ReadAllBytes(config).SequenceEqual(bytes) || await Open() is not { } invalid)
                return false;
            Field(invalid, "temperature").Text = "5";
            Field(invalid, "top_p").Text = "0.5";
            Click(Labelled(invalid, "Save"));
            await Idle();
            var shown = invalid.IsVisible && Find<TextBlock>(invalid, t => t.Visibility == Visibility.Visible && t.Text.StartsWith("A number, 0 to 2")) is not null;
            invalid.Close();
            return shown && File.ReadAllBytes(config).SequenceEqual(bytes);
        }
        finally
        {
            (_settings.Runtime, _settings.ModelsFolder) = (runtime, "");
            Directory.Delete(models, recursive: true);
        }
    }
}

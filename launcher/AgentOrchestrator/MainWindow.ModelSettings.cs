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
    // A built-in role's fixed prompts from the workflow folder: the project run's, then the Code conversation's.
    private string WorkflowPrompt(string role)
    {
        string Read(string file)
        {
            try { return _workflowRoot is null ? "" : File.ReadAllText(Path.Combine(_workflowRoot, "prompts", file)).Trim(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
        }
        var code = role == "implementer" ? "code.txt" : $"code_{role}.txt";
        return $"PROJECT RUN (prompts/{role}.txt)\n\n{Read(role + ".txt")}\n\n\nCODE CONVERSATION (prompts/{code})\n\n{Read(code)}";
    }

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
        if (!card.IsCustom)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                role = JsonNode.Parse(File.ReadAllText(path))!["roles"]![card.Role]!.AsObject();
                remoteNow = LauncherLogic.ReadRemoteRole(path, card.Role);
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

        // The groups of related settings, one column or two (SettingsColumns), added in SettingsGroups' order once built.
        var sections = new SettingsColumns { Margin = new Thickness(0, 0, 14, 0) };
        var scroll = new ScrollViewer { Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var blocks = new Dictionary<string, StackPanel>();
        StackPanel Section(string name, string help)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = name, FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 14, FontWeight = FontWeights.SemiBold });
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
        void ContextHint(Dictionary<string, string>? meta) => hints["max_seq_length"].Text = ModelOptions.All.First(o => o.Key == "max_seq_length").Help +
            (ModelOptions.ModelContext(meta) is { } max ? $". Model maximum: {max.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}" : "");
        var rows = new Dictionary<string, StackPanel>();
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
                        box.FontFamily = new FontFamily("Consolas");
                    editor = box;
                }
                editors[o.Key] = editor;
                fields.Children.Add(rows[o.Key] = new StackPanel
                {
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { Label(o.Label), (hints[o.Key] = Muted(o.Help)), editor, (fieldErrors[o.Key] = ErrorLine()) },
                    ToolTip = o.Default is null ? o.Key : $"{o.Key}; default {o.Default}",
                });
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
            prompt.FontFamily = new FontFamily("Consolas");
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

        // Built-in roles: Local (Unsloth / llama.cpp) or a remote OpenAI-compatible API.
        TextBox[]? remoteBoxes = null;
        TextBox? chatPrompt = null;
        StackPanel? remotePanel = null, source = null;
        if (!card.IsCustom)
        {
            // The Chat view: an optional prompt of the user's. The Code view: the role's fixed workflow prompts, read only
            // (the multi-agent workflow depends on them). ApplyPromptView shows the one for the view on screen.
            var chat = Section("System prompt (Chat)", "Optional: added to this worker's instructions in the Chat view (roles." + card.Role + ".chat_system_prompt). Empty = none.");
            chat.Tag = "ChatPromptSection";
            chat.Children.Add(chatPrompt = Box((string?)role!["chat_system_prompt"] ?? "", "ChatSystemPrompt", 120));
            chatPrompt.FontFamily = new FontFamily("Consolas");
            var workflow = Section("System prompt (workflow)", "Fixed: the instructions this role follows in project runs and Code conversations. Not editable.");
            workflow.Tag = "WorkflowPromptSection";
            var fixedPrompt = Box(WorkflowPrompt(card.Role), "WorkflowPrompt", 220);
            (fixedPrompt.FontFamily, fixedPrompt.IsReadOnly, fixedPrompt.TextWrapping, fixedPrompt.VerticalScrollBarVisibility) =
                (new FontFamily("Consolas"), true, TextWrapping.Wrap, ScrollBarVisibility.Auto);
            fixedPrompt.SetResourceReference(ForegroundProperty, "Muted");
            workflow.Children.Add(fixedPrompt);
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
            if (card.Role != "planner") // an added Implementer / Reviewer: removing it hands its work back to MAIN
            {
                var name = ChatName(card, card.Role);
                var removeRole = new Button { Content = "Remove", Tag = "RemoveRole", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0),
                    ToolTip = $"MAIN covers the {name} again; these settings are kept for when you add it back" };
                removeRole.Click += (_, _) =>
                {
                    if (AskDialog("Remove worker", $"Remove the {name}? MAIN (the planner) takes over its work.", "Remove") is null)
                        return;
                    HoldWorkerTab(() =>
                    {
                        RemoveBuiltInRole(card.Role);
                        close();
                    });
                    Log($"{name}: removed; MAIN covers it");
                };
                panel.Children.Add(removeRole);
            }
        }

        // The option groups (Model's fields join the picker below); a remote role hides the load options, and the groups
        // holding nothing else.
        var loadOnly = new List<StackPanel>();
        foreach (var (name, help) in SettingsGroups.Where(g => g.Help is not null))
            if (Fields(name) is { Children.Count: > 0 } fields)
            {
                Section(name, $"{help} {emptyMeans}").Children.Add(fields);
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
            var head = new DockPanel();
            var total = new TextBlock { Text = VramEstimate.Gb(estimate.Total) + (gpu is { } g ? " / " + VramEstimate.Gb(g) : ""), FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = gpu is { } g2 ? (Brush)new BrushConverter().ConvertFromString(RingColor((double)estimate.Total / g2))! : (Brush)FindResource("TextPrimary") };
            DockPanel.SetDock(total, Dock.Right);
            head.Children.Add(total);
            head.Children.Add(Label("VRAM estimate"));
            vramBlock.Children.Add(head);
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
        var top = Section("Model", "The model this worker runs, its quantization and where it runs.");
        foreach (var part in new UIElement?[] { new StackPanel { Margin = new Thickness(0, 6, 0, 0), Children = { modelBox, modelStatus } }, Fields("Model"), vramBlock, source })
            if (part is not null)
                top.Children.Add(part);
        foreach (var (name, _) in SettingsGroups)
            if (blocks.TryGetValue(name, out var block))
                sections.Children.Add(block);
        ApplyPromptView(sections);
        ModelChoice? initial = null;
        var requested = false;
        modelBox.DropDownOpened += (_, _) =>
        {
            if (!requested)
                LoadModels();
            requested = true;
        };
        async void LoadModels()
        {
            modelStatus.Text = "Loading models…";
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
                modelStatus.Text = choices.Count == 0 ? "No installed text models found." : LocalRuntime ? "GGUF files on this computer (llama.cpp runtime)." : "Models installed in Unsloth.";
                // A model brings its own quantization: the Load section's gguf_variant follows the pick. "(current)" (no
                // ModelChoice) restores what the current model showed.
                modelBox.SelectionChanged += (_, _) =>
                {
                    var picked = (modelBox.SelectedItem as ComboBoxItem)?.Tag as ModelChoice;
                    if (editors["gguf_variant"] is TextBox variant)
                        variant.Text = picked is null ? shownAtStart["gguf_variant"] : picked.Variant ?? "";
                    hasModel = picked is not null || startLoad?["model_path"] is not null;
                    Derive(LoadOf(picked));
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
            foreach (var hidden in ModelOptions.All.Where(o => o.Target == "auto_load").Select(o => rows[o.Key]).Concat(loadOnly))
                hidden.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            modelBox.IsEnabled = !remote;
        }
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
                var same = _settings.ModelPresets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (same >= 0 && SidebarAsk("Replace preset", $"Replace the preset {name}?", "Replace") is null)
                    return;
                var preset = new ModelPreset
                {
                    Name = name, Options = editors.Where(p => p.Key != "gguf_variant" && Value(p.Value).Trim().Length > 0).ToDictionary(p => p.Key, p => Value(p.Value)),
                    SystemPrompt = promptBox.Text.Trim().Length > 0 && promptBox.Text != DefaultPrompt() ? promptBox.Text : "",
                };
                if (same >= 0)
                    _settings.ModelPresets[same] = preset;
                else
                    _settings.ModelPresets.Add(preset);
                _settings.Save();
                Toast($"Preset {name} saved");
            }));
            if (_settings.ModelPresets.Count > 0)
            {
                menu.Items.Add(new Separator());
                var delete = new MenuItem { Header = "Delete preset" };
                foreach (var preset in _settings.ModelPresets.ToList())
                {
                    menu.Items.Add(Item(preset.Name, () =>
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
        foreach (var part in new FrameworkElement[] { sections, footer }) // read-only while a run uses the models
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

            var remote = remoteMode() ? new LauncherLogic.RemoteRole(remoteBoxes![0].Text, remoteBoxes[1].Text, remoteBoxes[2].Text, "") : null;
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
    private static readonly (string Name, string? Help)[] SettingsGroups =
    [
        ("Model", null), ("Context & memory", "Load options: apply at the next model load."), ("Batching", "Load options: apply at the next model load."),
        ("GPU", "Load options: apply at the next model load."), ("Speculative decoding", "Load options: apply at the next model load."),
        ("Reasoning", "The budget applies at the next model load; thinking and effort are sent with every request."),
        ("Sampling", "Sent with every request; top_k, min_p and repetition_penalty go in extra_body (Unsloth only)."),
        ("Output", "Sent with every request."), ("Request", "How long the orchestrator waits for this worker."),
        ("Advanced", "Load options: apply at the next model load."), ("Other", "Other options."),
        ("System prompt (Chat)", null), ("System prompt (workflow)", null), ("Worker", null),
    ];
    private static readonly Dictionary<string, string[]> SettingsGroupKeys = new()
    {
        ["Model"] = ["gguf_variant"],
        ["Context & memory"] = ["max_seq_length", "cache_type_kv", "n_parallel", "ctx_checkpoints", "cache_ram", "load_mode"],
        ["Batching"] = ["n_batch", "n_ubatch"],
        ["GPU"] = ["gpu_memory_mode", "gpu_layers", "n_cpu_moe", "tensor_split", "gpu_ids", "tensor_parallel", "cpu_fallback", "disable_vision"],
        ["Speculative decoding"] = ["speculative_type", "spec_draft_n_max", "spec_draft_cache_type"],
        ["Reasoning"] = ["reasoning_budget", "reasoning_budget_message", "enable_thinking", "reasoning_effort"],
        ["Sampling"] = ["temperature", "top_p", "top_k", "min_p", "repetition_penalty", "presence_penalty", "frequency_penalty", "seed"],
        ["Output"] = ["max_tokens", "stop"],
        ["Request"] = ["timeout_seconds"],
        ["Advanced"] = ["chat_template_override", "llama_extra_args"],
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
        var saved = (_settings.ChatWorker, _settings.Runtime, _settings.ModelsFolder, _codeChoice);
        var models = Path.Combine(AppSettings.DataRoot, "smoke-models-config");
        Directory.CreateDirectory(models);
        (_settings.Runtime, _settings.ModelsFolder) = (LauncherLogic.LlamaRuntime, models); // a disk scan: never a real Unsloth server
        var asked = new List<string>();
        string? answer = null;
        _codeChoice = title => { asked.Add(title); return answer; };
        var (planner, implementer, reviewer) = (CardFor("planner")!, CardFor("implementer")!, CardFor("reviewer")!);
        try
        {
            // Chat: the dropdown lists every shown worker (MAIN, the added roles, the custom workers), the target checked;
            // Options… opens the target's tab. Code: Options… alone, opening the lead's (the Planner's) tab.
            _settings.ChatWorker = "reviewer";
            RefreshChatTarget();
            if (Dropdown(ChatTargetChip) is not ["Options…", "Main", "Implementer", "Reviewer*", ..] chatDrop || chatDrop.Count != 1 + _cards.Count(k => !k.Hidden) ||
                ChatTargetChip.ToolTip as string != chipTip)
                return Fail("Single chat dropdown");
            if (await Open(ChatTargetChip) != reviewer || !reviewer.IsSelected)
                return Fail("Single chat Options…");
            if (Dropdown(CodeTargetChip) is not ["Options…"] || !((string)CodeTargetChip.Content).StartsWith("Lead: ") ||
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
                stored.Options.ContainsKey("gguf_variant") || stored.Options.ContainsKey("seed") || stored.SystemPrompt != "Smoke prompt")
                return Fail("save a preset");
            // As if saved on a worker with a model, or by an older version: a load value and an empty one; neither is applied
            // to the Implementer, which has no model to load.
            (stored.Options["n_batch"], stored.Options["top_p"]) = ("64", "");
            answer = "";
            SmokeClickTab(implementer); // discards the Reviewer's change
            await Idle();
            if (asked[^1] != "Unsaved changes" || _shownWorker != implementer || SmokeField("temperature").Text != "0.33" || SmokeField("ChatSystemPrompt").Text != "")
                return Fail("discard the Reviewer's change");
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
            (_settings.ChatWorker, _settings.Runtime, _settings.ModelsFolder, _codeChoice) = saved;
            RefreshChatTarget();
            Directory.Delete(models, recursive: true);
        }
    }

    // --ui-smoke: the Code header puts the context ring directly left of the Conversation / Run output toggle; while a turn
    // runs, its status sits in the top row just left of the stop square and the bottom hint steps aside; idle shows the hint.
    private async Task<bool> UiTweaksSmoke(Action<string, FrameworkElement?> snap)
    {
        var (wasChat, turn) = (NavChat.IsChecked == true, _turn);
        try
        {
            ShowView(chat: false);
            ShowCodeRun(false);
            await Task.Delay(400);
            var toggleBox = (Border)((StackPanel)CodeConversationToggle.Parent).Parent;
            var header = (DockPanel)toggleBox.Parent;
            if (CodeContextRing.Parent != header || header.Children.IndexOf(CodeContextRing) != header.Children.IndexOf(toggleBox) + 1 ||
                CodeContextRing.TranslatePoint(new Point(CodeContextRing.ActualWidth, 0), header).X > toggleBox.TranslatePoint(new Point(0, 0), header).X)
                return false;
            _chatBusy = true;
            _turn = _code;
            RefreshChatInput();
            await Task.Delay(400);
            var top = (Grid)CodeStop.Parent;
            if (CodeBusyText.Parent != top || CodeBusyText.Visibility != Visibility.Visible || CodeBusyText.Text != "Replying…" || CodeStop.Visibility != Visibility.Visible ||
                Grid.GetColumn(CodeBusyText) != Grid.GetColumn(CodeStop) - 1 || Grid.GetRow(CodeBusyText) != 0 ||
                CodeBusyText.TranslatePoint(new Point(CodeBusyText.ActualWidth, 0), top).X > CodeStop.TranslatePoint(new Point(0, 0), top).X ||
                CodeHint.Visibility == Visibility.Visible || CodeHint.Text.Contains("Replying") ||
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
            ShowCodeRun(true);
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
        var (runtime, ask) = (_settings.Runtime, _codeChoice);
        (_settings.Runtime, _settings.ModelsFolder) = (LauncherLogic.LlamaRuntime, models);
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
            if (_cards.Any(c => SmokeTab(c) is not { } t || t.IsVisible != (!c.Hidden || c.IsSelected)) || Math.Abs(width - Math.Max(88, WorkerTabsScroll.ActualWidth / 3 - 6)) > 0.5 ||
                3 * (width + 6) > WorkerTabsScroll.ActualWidth + 1 || (_cards.Count(c => !c.Hidden) <= 3 && WorkerTabsScroll.ScrollableWidth > 1) ||
                Math.Abs(SmokeTab(planner)!.ActualWidth - width - 6) > 0.5)
                return Fail("tabs at the default width");
            var texts = SmokeVisuals(SmokeTab(planner)!).OfType<TextBlock>().Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text).ToList();
            if (!texts.Contains(planner.Title) || !texts.Contains(planner.ModelName) || !texts.Contains(planner.TokensPerSec) ||
                !texts.Contains($"{planner.TaskTokensShort} task · {planner.TurnTotalShort} turn"))
                return Fail("tab text: " + string.Join(" | ", texts));
            snap("tabs-default", null);

            // A narrow column: the tabs keep their minimum width and the strip scrolls, also with the mouse wheel.
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
            async Task<ComboBoxItem?> FakeItem()
            {
                var box = Model();
                box.IsDropDownOpen = true;
                box.IsDropDownOpen = false;
                await Until(() => box.Items.OfType<ComboBoxItem>().Any(i => i.Tag is not null));
                return box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as ModelChoice)?.File == fake);
            }

            // The model's defaults arrive off the UI thread and never replace an edit; re-selecting "(current)" restores the
            // fields (no model_path: the load options empty again), so Save is not refused.
            if (await FakeItem() is not { } fakeItem || !Model().IsEnabled)
                return Fail("the model list");
            snap("tabs-settings", null);
            SmokeField("top_k").Text = "33";
            Model().SelectedItem = fakeItem; // an unreadable GGUF: the static defaults
            if (!await Until(() => SmokeField("max_seq_length").Text == "32768") || SmokeField("n_batch").Text != "2048" || SmokeField("seed").Text != "" ||
                SmokeField("max_tokens").Text != "" || SmokeField("top_k").Text != "33" ||
                Model().Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is null) is not { } currentItem)
                return Fail("a model's defaults");
            Model().SelectedItem = currentItem;
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
            Model().SelectedItem = fakeAgain;
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
            (_settings.Runtime, _settings.ModelsFolder, _codeChoice) = (runtime, "", ask);
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

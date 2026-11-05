using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgentOrchestrator;

// The Remote API page (the sidebar's Remote API), in the centre column in place of Code and Chat: an OpenAI-compatible
// service, its models (RemoteCatalog), for OpenRouter the providers to use in order, and Apply to a worker. The key stays
// in the environment variable the page names; the app never stores it.
public partial class MainWindow
{
    private HttpClient _remoteHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan }; // RemoteCatalog caps each request at 15 s; smokes put a fake handler here
    private Action<string> _openExternal = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); // smokes record the address instead
    private DialogShell? _remoteHelp;
    private (string Service, string BaseUrl, string Model)? _remoteLast; // the page's last choice, this session only
    private CancellationTokenSource? _remoteModelsCts, _remoteProvidersCts;
    private Task _remoteModelsTask = Task.CompletedTask, _remoteProvidersTask = Task.CompletedTask; // smokes await them

    // The providers table: 0 = the Provider column, which takes the width left over (at least 130).
    private static readonly double[] ProviderColumns = [34, 0, 64, 76, 76, 82, 68, 56, 64, 52];
    private static readonly string?[] HeadTips = [null, null, "Quantization", "Input, USD per million tokens", "Output, USD per million tokens",
        "Cached input read, USD per million tokens", "Median latency in seconds, last 30 minutes", "Median throughput in tokens per second, last 30 minutes",
        "Uptime in %, last 30 minutes", "Takes images"];

    // role: the worker Apply to starts on; preset: its current remote settings (service, model, provider order).
    private void OpenRemoteApi(string? role = null, LauncherLogic.RemoteRole? preset = null)
    {
        CloseRemoteApi();
        var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A));
        var custom = RemoteCatalog.Services[^1];
        TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 16, 0, 0) };
        TextBox Box(string tag) => new()
        {
            Tag = tag, Height = 30, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center,
        };
        void Note(TextBlock t, string text, bool error = false)
        {
            t.Text = text;
            t.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (error)
                t.Foreground = red;
            else
                t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
        Grid Columns(UIElement left, UIElement right, double leftWidth = 240)
        {
            var g = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(leftWidth) }, new ColumnDefinition { Width = new GridLength(16) }, new ColumnDefinition() } };
            Grid.SetColumn(right, 2);
            g.Children.Add(left);
            g.Children.Add(right);
            return g;
        }

        // ---- service, base URL, key variable
        var service = new ComboBox { Tag = "RemoteService", Margin = new Thickness(0, 4, 0, 0) };
        foreach (var s in RemoteCatalog.Services)
            service.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s });
        var baseUrl = Box("RemoteBaseUrl");
        var keyEnv = Box("RemoteKeyEnv");
        var keyStatus = new TextBlock { Tag = "RemoteKeyStatus", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 7) }; // level with the box beside it
        RemoteCatalog.Service Selected() => (RemoteCatalog.Service)((ComboBoxItem)service.SelectedItem).Tag;
        string? Key() => keyEnv.Text.Trim() is { Length: > 0 } env && Environment.GetEnvironmentVariable(env) is { Length: > 0 } key ? key : null;

        // ---- models
        var models = new List<RemoteCatalog.ModelEntry>();
        var search = Box("RemoteSearch");
        var searchHint = RemoteMuted("Search models");
        searchHint.IsHitTestVisible = false;
        searchHint.VerticalAlignment = VerticalAlignment.Center;
        searchHint.Margin = new Thickness(12, 4, 0, 0);
        search.TextChanged += (_, _) => searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var list = new StackPanel { Tag = "RemoteModels" };
        var listBox = new Border
        {
            Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = list, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        listBox.SetResourceReference(Border.BackgroundProperty, "Card");
        listBox.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        listBox.SetResourceReference(Border.CornerRadiusProperty, "SmallRadius");
        var modelsNote = RemoteMuted("");
        modelsNote.Tag = "RemoteModelsNote";
        modelsNote.Margin = new Thickness(2, 6, 0, 0);
        var modelId = Box("RemoteModelId");

        // ---- providers (OpenRouter)
        var rows = new List<RemoteCatalog.ProviderRow>();
        var order = (preset?.Provider ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var table = new StackPanel { Tag = "RemoteProviderRows", MinWidth = ProviderColumns.Sum() + 130 };
        var tableScroll = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 8, 0, 0) };
        tableScroll.SizeChanged += (_, e) => table.Width = Math.Max(table.MinWidth, e.NewSize.Width); // the whole column; narrower scrolls
        var providersNote = RemoteMuted("");
        providersNote.Margin = new Thickness(0, 6, 0, 0);
        var keyNote = RemoteMuted("Latency and throughput show only with an OpenRouter key in the environment.");
        keyNote.Margin = new Thickness(0, 2, 0, 0);
        var orderText = new TextBlock { Tag = "RemoteOrder", TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var clearOrder = new Button { Content = "Clear", Tag = "RemoteClearOrder", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(clearOrder, Dock.Right);
        var visionOnly = new ToggleButton { Style = (Style)FindResource("ToggleSwitch"), Tag = "RemoteVisionOnly", VerticalAlignment = VerticalAlignment.Center };
        var visionLabel = RemoteMuted("Vision only");
        visionLabel.VerticalAlignment = VerticalAlignment.Center;
        visionLabel.Margin = new Thickness(0, 0, 8, 0);
        var providersHead = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var visionPair = new StackPanel { Orientation = Orientation.Horizontal, Children = { visionLabel, visionOnly } };
        DockPanel.SetDock(visionPair, Dock.Right);
        providersHead.Children.Add(visionPair);
        providersHead.Children.Add(new TextBlock { Text = "Providers", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var providers = new StackPanel
        {
            Tag = "RemoteProviders",
            Children =
            {
                providersHead,
                RemoteMuted("Tick providers to use only those, in the order ticked (no fallbacks). Nothing ticked: OpenRouter picks any provider."),
                keyNote, providersNote,
                tableScroll,
                new DockPanel { Margin = new Thickness(0, 8, 0, 0), Children = { clearOrder, orderText } },
            },
        };

        // ---- apply
        var target = new ComboBox { Tag = "RemoteTarget", Margin = new Thickness(0, 4, 0, 0) };
        foreach (var card in _cards)
            target.Items.Add(new ComboBoxItem { Content = ChatName(card, card.Role), Tag = card });
        target.SelectedItem = target.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((RoleCard)i.Tag).Role == role) ?? target.Items.OfType<ComboBoxItem>().FirstOrDefault();
        var apply = new Button { Content = "Apply", Tag = "RemoteApply", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(18, 4, 18, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        var errorLine = new TextBlock { Tag = "RemoteError", Foreground = red, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var keyHelp = new Button { Content = "Key help", Tag = "RemoteKeyHelp", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(keyHelp, Dock.Right);

        void KeyStatus()
        {
            var env = keyEnv.Text.Trim();
            var set = Key() is not null;
            keyStatus.Text = env.Length == 0 ? "No variable: requests go without a key." : set ? $"✓ {env} is set in your environment." : $"{env} is not set — see Key help.";
            if (set || env.Length == 0)
                keyStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            else
                keyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0xC8, 0x73));
            keyNote.Visibility = set ? Visibility.Collapsed : Visibility.Visible;
        }

        void RenderModels()
        {
            list.Children.Clear();
            var q = search.Text.Trim();
            var shown = models.Where(m => q.Length == 0 || m.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var m in shown.Take(200)) // ponytail: a 200-row cap instead of a virtualized list; search narrows the rest
            {
                var context = RemoteMuted(RemoteContext(m.ContextLength));
                context.VerticalAlignment = VerticalAlignment.Center;
                context.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(context, Dock.Right);
                var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = m.Id };
                name.Inlines.Add(new Run(m.Name));
                if (m.Name != m.Id)
                {
                    var id = new Run("   " + m.Id) { FontSize = 11 };
                    id.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                    name.Inlines.Add(id);
                }
                var row = new ToggleButton
                {
                    Content = new DockPanel { Children = { context, name } }, Tag = m, IsChecked = m.Id == modelId.Text.Trim(),
                    Padding = new Thickness(14, 5, 10, 5), Margin = new Thickness(0, 0, 0, 1),
                };
                row.SetResourceReference(StyleProperty, "NavToggle");
                row.Click += (_, _) => modelId.Text = m.Id; // its TextChanged marks the row and loads the providers
                list.Children.Add(row);
            }
            listBox.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (models.Count > 0)
                Note(modelsNote, shown.Count > 200 ? $"Showing 200 of {shown.Count} models: search to narrow." : q.Length > 0 ? $"{shown.Count} of {models.Count} models" : $"{models.Count} models");
        }

        void LoadModels()
        {
            _remoteModelsCts?.Cancel();
            var cts = _remoteModelsCts = new CancellationTokenSource();
            models = [];
            RenderModels();
            var s = Selected();
            var url = baseUrl.Text.Trim();
            var key = Key();
            _remoteLast = (s.Id, url, modelId.Text.Trim());
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                Note(modelsNote, "Enter the service's base URL (such as http://127.0.0.1:8080/v1) to list its models.");
                return;
            }
            if (s.ModelsNeedKey && s != custom && key is null)
            {
                Note(modelsNote, $"{s.Name} lists its models only with an API key: set {s.KeyEnv} (see Key help), or type the model id below.");
                return;
            }
            Note(modelsNote, "Loading models…");
            _remoteModelsTask = Fetch(cts.Token);
            async Task Fetch(CancellationToken ct)
            {
                try
                {
                    await Task.Delay(300, ct);
                    var got = await RemoteCatalog.FetchModelsAsync(_remoteHttp, url, key, ct);
                    if (ct.IsCancellationRequested)
                        return;
                    models = got;
                    if (models.Count == 0)
                        Note(modelsNote, "The service lists no models: type the model id below.");
                    RenderModels();
                }
                catch (OperationCanceledException) { }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException or FormatException)
                {
                    if (!ct.IsCancellationRequested) // a custom URL is tried without a key too (local servers need none)
                        Note(modelsNote, e.Message + (key is null && s == custom ? " If it needs a key, name its variable above." : ""), error: true);
                }
            }
        }

        void RenderProviders()
        {
            table.Children.Clear();
            orderText.Text = order.Count == 0 ? "Any provider." : "Only, in this order: " + string.Join(" → ", order.Select(tag => rows.FirstOrDefault(r => r.Tag == tag)?.Name is { Length: > 0 } n ? $"{n} ({tag})" : tag));
            clearOrder.Visibility = order.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (rows.Count == 0)
                return;
            Grid Line(params UIElement[] cells)
            {
                var g = new Grid { MinHeight = 28 };
                for (var i = 0; i < ProviderColumns.Length; i++)
                {
                    g.ColumnDefinitions.Add(ProviderColumns[i] > 0 ? new ColumnDefinition { Width = new GridLength(ProviderColumns[i]) } : new ColumnDefinition { MinWidth = 130 });
                    Grid.SetColumn(cells[i], i);
                    g.Children.Add(cells[i]);
                }
                return g;
            }
            TextBlock Cell(string text, bool number = true)
            {
                var t = new TextBlock
                {
                    Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                    HorizontalAlignment = number ? HorizontalAlignment.Right : HorizontalAlignment.Left, FontSize = number ? 11.5 : 12.5,
                };
                if (number)
                    t.FontFamily = AppFonts.Mono;
                return t;
            }
            var heads = new[] { "", "Provider", "Quant.", "In $/M", "Out $/M", "Cache $/M", "Latency", "tok/s", "Uptime", "Vision" }
                .Select((h, i) =>
                {
                    var t = RemoteMuted(h);
                    t.ToolTip = HeadTips[i];
                    t.TextWrapping = TextWrapping.NoWrap;
                    t.FontWeight = FontWeights.SemiBold;
                    t.VerticalAlignment = VerticalAlignment.Bottom;
                    t.Margin = new Thickness(0, 0, 12, 4);
                    t.TextAlignment = i >= 3 ? TextAlignment.Right : TextAlignment.Left;
                    return (UIElement)t;
                }).ToArray();
            var header = Line(heads);
            var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 2) };
            rule.SetResourceReference(Border.BackgroundProperty, "Border");
            table.Children.Add(header);
            table.Children.Add(rule);
            foreach (var r in rows.Where(r => visionOnly.IsChecked != true || r.Vision))
            {
                var position = order.IndexOf(r.Tag);
                var tick = new ToggleButton
                {
                    Style = (Style)FindResource("SegToggle"), Width = 22, Height = 22, Padding = new Thickness(0), FontSize = 11, Tag = "tick:" + r.Tag,
                    IsChecked = position >= 0, Content = position >= 0 ? (position + 1).ToString(CultureInfo.InvariantCulture) : "",
                    ToolTip = position >= 0 ? $"Provider {position + 1} of {order.Count}: click to remove" : "Use this provider (in the order ticked)",
                };
                tick.Click += (_, _) =>
                {
                    if (tick.IsChecked == true)
                        order.Add(r.Tag);
                    else
                        order.Remove(r.Tag);
                    RenderProviders();
                };
                var box = new Border { Child = tick, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                box.SetResourceReference(Border.BorderBrushProperty, "Border");
                box.SetResourceReference(Border.BackgroundProperty, "Card");
                box.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
                var name = Cell(r.Name.Length > 0 ? r.Name : r.Tag, number: false);
                name.ToolTip = r.Tag;
                var line = Line(box, name, Cell(r.Quantization is { Length: > 0 } and not "unknown" ? r.Quantization : "—", number: false),
                    Cell(RemotePrice(r.InputPerM)), Cell(RemotePrice(r.OutputPerM)), Cell(RemotePrice(r.CacheReadPerM)),
                    Cell(RemoteFigure(r.LatencySeconds, "0.00")), Cell(RemoteFigure(r.ThroughputTps, "0")), Cell(RemoteFigure(r.Uptime, "0.#")),
                    Cell(r.Vision ? "✓" : "", number: false));
                line.Tag = r;
                table.Children.Add(line);
            }
        }

        void LoadProviders()
        {
            _remoteProvidersCts?.Cancel();
            var cts = _remoteProvidersCts = new CancellationTokenSource();
            rows = [];
            RenderProviders();
            var id = modelId.Text.Trim();
            providers.Visibility = Selected().HasProviders && id.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (providers.Visibility != Visibility.Visible)
                return;
            Note(providersNote, "Loading providers…");
            var key = Key();
            _remoteProvidersTask = Fetch(cts.Token);
            async Task Fetch(CancellationToken ct)
            {
                try
                {
                    await Task.Delay(300, ct);
                    var got = await RemoteCatalog.FetchProvidersAsync(_remoteHttp, id, ct, key);
                    if (ct.IsCancellationRequested)
                        return;
                    rows = got;
                    Note(providersNote, rows.Count == 0 ? "OpenRouter lists no providers for this model id." : "");
                    RenderProviders();
                }
                catch (OperationCanceledException) { }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException or FormatException)
                {
                    if (!ct.IsCancellationRequested)
                        Note(providersNote, e.Message, error: true);
                }
            }
        }

        void Apply()
        {
            Note(errorLine, "");
            if (CodeBusy)
            {
                Toast("Wait for the reply or run to finish", "#F7C873");
                return;
            }
            if (!DiscardWorkerEdits())
                return;
            if (target.SelectedItem is not ComboBoxItem { Tag: RoleCard card })
            {
                Note(errorLine, "There is no worker to apply it to.", error: true);
                return;
            }
            var remote = new LauncherLogic.RemoteRole(baseUrl.Text.Trim().TrimEnd('/'), modelId.Text.Trim(), keyEnv.Text.Trim(), "", Selected().HasProviders ? string.Join(", ", order) : "");
            if (LauncherLogic.ValidateRemoteRole(remote) is { } problem)
            {
                Note(errorLine, problem, error: true);
                return;
            }
            var name = ChatName(card, card.Role);
            if (card.IsCustom)
            {
                SetCustomRemote(card, remote);
                SaveWorkers();
                SyncCustomWorkers();
            }
            else
            {
                var targets = WorkerConfigTargets();
                if (targets.Length == 0)
                {
                    Note(errorLine, "There is no config.json to write to yet.", error: true);
                    return;
                }
                string? failed;
                try
                {
                    failed = WriteConfigs(targets, t =>
                    {
                        if (card.Role == LauncherLogic.Compactor) // following MAIN: its own roles.compactor first, then remote
                        {
                            var root = JsonNode.Parse(File.ReadAllText(t))!.AsObject();
                            if (!LauncherLogic.CompactorHasOwn(root["roles"] as JsonObject))
                            {
                                GiveCompactorOwn(root);
                                WriteJson(t, root);
                            }
                        }
                        return LauncherLogic.WriteRemoteRole(t, card.Role, remote, Path.Combine(_workflowRoot ?? "", "config.json"));
                    });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
                {
                    Log($"Could not update config.json: {exception.Message}");
                    SetStatus("Could not update config.json", "#FF667A");
                    Note(errorLine, $"Could not update config.json: {exception.Message}", error: true);
                    return;
                }
                if (failed is not null)
                {
                    Note(errorLine, failed, error: true);
                    return;
                }
            }
            Log($"{name}: remote API {remote.BaseUrl}, model {remote.Model}{(remote.Provider.Length > 0 ? $", providers {remote.Provider}" : "")}");
            if (Environment.GetEnvironmentVariable(remote.KeyEnv) is not { Length: > 0 })
                Toast($"Applied to {name}: set {remote.KeyEnv} in your environment, then restart the launcher", "#F7C873");
            else
                Toast($"Applied to {name}");
            HoldWorkerTab(() =>
            {
                PrefillCards();
                RefreshWorkerTab(force: true);
            });
        }

        // ---- the starting values, then the handlers
        var start = preset is not null ? RemoteCatalog.ServiceFor(preset.BaseUrl) ?? custom
            : _remoteLast is { } last ? RemoteCatalog.Services.FirstOrDefault(s => s.Id == last.Service) ?? RemoteCatalog.Services[0]
            : RemoteCatalog.Services[0];
        service.SelectedItem = service.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, start));
        baseUrl.Text = preset?.BaseUrl ?? (start == custom && _remoteLast is { } l ? l.BaseUrl : start.BaseUrl);
        baseUrl.IsReadOnly = start != custom;
        keyEnv.Text = preset?.KeyEnv is { Length: > 0 } presetKey ? presetKey : start.KeyEnv;
        modelId.Text = preset?.Model ?? (_remoteLast is { } m && m.Service == start.Id ? m.Model : "");
        KeyStatus();

        service.SelectionChanged += (_, _) =>
        {
            var s = Selected();
            baseUrl.IsReadOnly = s != custom;
            order.Clear(); // another service's model and providers mean nothing here
            modelId.Text = "";
            if (s != custom)
            {
                baseUrl.Text = s.BaseUrl;
                keyEnv.Text = s.KeyEnv;
            }
            LoadModels();
            LoadProviders();
        };
        baseUrl.TextChanged += (_, _) => LoadModels();
        keyEnv.TextChanged += (_, _) =>
        {
            KeyStatus();
            LoadModels();
        };
        search.TextChanged += (_, _) => RenderModels();
        modelId.TextChanged += (_, _) =>
        {
            foreach (var row in list.Children.OfType<ToggleButton>())
                row.IsChecked = ((RemoteCatalog.ModelEntry)row.Tag).Id == modelId.Text.Trim();
            _remoteLast = (Selected().Id, baseUrl.Text.Trim(), modelId.Text.Trim());
            LoadProviders();
        };
        visionOnly.Click += (_, _) => RenderProviders();
        clearOrder.Click += (_, _) =>
        {
            order.Clear();
            RenderProviders();
        };
        apply.Click += (_, _) => Apply();
        keyHelp.Click += (_, _) => ShowRemoteHelp(Selected().Id);

        // ---- the page
        var title = new TextBlock { Text = "Remote API", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 22, FontWeight = FontWeights.SemiBold };
        var intro = RemoteMuted("Run a worker on an OpenAI-compatible service instead of this computer. The API key stays in an environment variable: the app never stores it.");
        intro.FontSize = 12;
        intro.Margin = new Thickness(0, 4, 0, 0);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 22, 0, 0) };
        divider.SetResourceReference(Border.BackgroundProperty, "Border");
        var searchBox = new Grid { Width = 260, HorizontalAlignment = HorizontalAlignment.Right, Children = { search, searchHint } };
        DockPanel.SetDock(searchBox, Dock.Right);
        var modelHead = new TextBlock { Text = "Model", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var page = new StackPanel
        {
            MaxWidth = 860, Margin = new Thickness(36, 28, 36, 36),
            Children =
            {
                new DockPanel { Children = { keyHelp, new StackPanel { Children = { title, intro } } } },
                Columns(new StackPanel { Children = { Label("Service"), service } }, new StackPanel { Children = { Label("Base URL"), baseUrl } }),
                Columns(new StackPanel { Children = { Label("API key variable"), keyEnv } }, new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { keyStatus } }),
                new DockPanel { Margin = new Thickness(0, 18, 0, 0), Children = { searchBox, modelHead } },
                listBox, modelsNote,
                Label("Model id"), RemoteMuted("What the worker's config gets: pick one above or type it."), modelId,
                providers,
                divider,
                Label("Apply to"),
                Columns(target, apply),
                errorLine,
            },
        };
        RemoteApiView.Children.Clear();
        RemoteApiView.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Tag = "RemoteApiPage" });
        Reveal(CodeView, false);
        Reveal(ChatView, false);
        Reveal(RemoteApiView, true);
        LoadModels();
        LoadProviders();
        if (!_settings.RemoteHelpShown)
        {
            _settings.RemoteHelpShown = true;
            _settings.Save();
            ShowRemoteHelp(start.Id);
        }
    }

    // A custom worker on a remote model; its local model is kept (the first time) for Switch to Local.
    private static void SetCustomRemote(RoleCard card, LauncherLogic.RemoteRole remote)
    {
        if (card.Config.Remote is null)
            (card.Config.LocalModelId, card.Config.LocalModelVariant) = (card.ModelId, card.ModelVariant);
        card.Config.Remote = remote;
        card.ModelId = remote.Model;
        card.ModelVariant = null;
        card.SetModel(LauncherLogic.RemoteModelInfo(remote.Model));
    }

    private void CloseRemoteApi()
    {
        _remoteModelsCts?.Cancel();
        _remoteProvidersCts?.Cancel();
        Reveal(RemoteApiView, false);
    }

    // "Create an API key": per service, its key page and the steps; the service shown on the page first. Modeless.
    private void ShowRemoteHelp(string first)
    {
        if (_remoteHelp is { } open)
        {
            if (!_dialogsQuiet)
                open.Activate();
            return;
        }
        var sections = new StackPanel
        {
            Margin = new Thickness(0, 0, 16, 0), // clear of the scroll bar
            Children = { RemoteMuted("The app never stores a key: it reads the environment variable named on the Remote API page. Where to get one:") },
        };
        foreach (var s in RemoteCatalog.Services.Where(s => s.KeyUrl.Length > 0).OrderBy(s => s.Id != first))
        {
            var link = new Hyperlink(new Run(s.KeyUrl)) { Tag = "link:" + s.Id };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentLight");
            link.Click += (_, _) => _openExternal(s.KeyUrl);
            var steps = RemoteMuted(s.KeySteps);
            steps.Margin = new Thickness(0, 2, 0, 0);
            sections.Children.Add(new StackPanel
            {
                Tag = "help:" + s.Id, Margin = new Thickness(0, 14, 0, 0),
                Children = { new TextBlock { Text = s.Name, FontWeight = FontWeights.SemiBold }, new TextBlock { Inlines = { link }, Margin = new Thickness(0, 2, 0, 0) }, steps },
            });
        }
        var ok = new Button { Content = "Got it", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), IsDefault = true };
        DockPanel.SetDock(ok, Dock.Bottom);
        var body = new DockPanel { Width = 540, Children = { ok, new ScrollViewer { Content = sections, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } };
        var dialog = _remoteHelp = new DialogShell(this, "Create an API key", body) { ShowActivated = !_dialogsQuiet };
        ok.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => _remoteHelp = null;
        dialog.Show();
    }

    private static TextBlock RemoteMuted(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    // USD per million tokens; "—" unknown.
    private static string RemotePrice(decimal? perMillion) => perMillion is { } v ? "$" + v.ToString("0.00##", CultureInfo.InvariantCulture) : "—";

    private static string RemoteFigure(double? value, string format) => value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "—";

    // A context length in tokens: 262K, 1.05M; "" unknown.
    private static string RemoteContext(long tokens) =>
        tokens <= 0 ? "" : tokens >= 1_000_000 ? (tokens / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M" : (tokens / 1e3).ToString("0", CultureInfo.InvariantCulture) + "K";

    internal static void RemoteApiSelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException($"Remote API self-test failed: {what}");
        }
        Check(RemotePrice(0.0285m) == "$0.0285" && RemotePrice(0.03m) == "$0.03" && RemotePrice(1.5m) == "$1.50" && RemotePrice(0m) == "$0.00" &&
            RemotePrice(12.345678m) == "$12.3457" && RemotePrice(null) == "—", "price format");
        Check(RemoteContext(262144) == "262K" && RemoteContext(1048576) == "1.05M" && RemoteContext(2_000_000) == "2M" && RemoteContext(8192) == "8K" && RemoteContext(0) == "",
            "context length");
        Check(RemoteFigure(0.45, "0.00") == "0.45" && RemoteFigure(93.33333, "0.#") == "93.3" && RemoteFigure(85.5, "0") == "86" && RemoteFigure(null, "0") == "—", "figures");
    }

    // --ui-smoke: the sidebar's Remote API opens the page (the first time with the key help) in place of Code and Chat,
    // lists the (fake) service's models, filters them, shows a model's providers, writes the ticked order on Apply (to a
    // built-in role, COMPACTOR and a custom worker), refuses bad input and a busy Apply, and New chat leaves it.
    private async Task<bool> RemoteApiSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: remote api: {what}"); return false; }
        var (http, open, shown, last) = (_remoteHttp, _openExternal, _settings.RemoteHelpShown, _remoteLast);
        var fake = new RemoteApiFakeHandler();
        var opened = new List<string>();
        _remoteHttp = new HttpClient(fake);
        _openExternal = opened.Add;
        var targets = WorkerConfigTargets();
        var backup = targets.Select(File.ReadAllText).ToArray();
        var worker = new RoleCard("smoke-remote", "SMOKE REMOTE", custom: true);
        const string Unset = "AO_SMOKE_UNSET_KEY", OpenRouter = "https://openrouter.ai/api/v1", Glm = "z-ai/glm-5.3-flash";
        T Find<T>(string tag) where T : FrameworkElement => SmokeFind<T>(RemoteApiView, e => e.Tag as string == tag)!;
        Task Loaded() => Task.WhenAll(_remoteModelsTask, _remoteProvidersTask);
        void Pick(string tag, Func<object, bool> match)
        {
            var box = Find<ComboBox>(tag);
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => match(i.Tag));
        }
        void Service(string id) => Pick("RemoteService", s => ((RemoteCatalog.Service)s).Id == id);
        void Target(string role) => Pick("RemoteTarget", c => ((RoleCard)c).Role == role);
        void Toggle(ToggleButton t)
        {
            t.IsChecked = t.IsChecked != true;
            SmokeClick(t);
        }
        ToggleButton? Tick(string tag) => SmokeFind<ToggleButton>(RemoteApiView, t => t.Tag as string == "tick:" + tag);
        List<string> Rows() => [.. Find<StackPanel>("RemoteProviderRows").Children.OfType<Grid>().Where(g => g.Tag is RemoteCatalog.ProviderRow)
            .Select(g => string.Join("|", g.Children.OfType<TextBlock>().Select(t => t.Text)))];
        string OrderText() => Find<TextBlock>("RemoteOrder").Text;
        void Apply() => SmokeClick(Find<Button>("RemoteApply"));
        void Reset()
        {
            for (var i = 0; i < targets.Length; i++)
                File.WriteAllText(targets[i], backup[i]);
        }
        bool Unchanged() => targets.Select(File.ReadAllText).SequenceEqual(backup);
        string? Wrote(string role, LauncherLogic.RemoteRole want) => targets.Select(t => (t, LauncherLogic.ReadRemoteRole(t, role)))
            .Where(p => p.Item2 is null || p.Item2 with { Timeout = "" } != want).Select(p => $"{p.t}: {p.Item2}").FirstOrDefault();
        try
        {
            if (targets.Length == 0)
                return Fail("no config to apply to");
            _settings.RemoteHelpShown = false;
            if (SmokeFind<Button>(SidebarList, b => b.Tag as string == "+Remote API") is not { } nav)
                return Fail("no Remote API in the sidebar");
            nav.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.WhenAll(_remoteModelsTask, _remoteProvidersTask);
            await Task.Delay(400); // the views' fades
            if (RemoteApiView.Visibility != Visibility.Visible || CodeView.Visibility == Visibility.Visible || ChatView.Visibility == Visibility.Visible)
                return Fail($"page {RemoteApiView.Visibility}, code {CodeView.Visibility}, chat {ChatView.Visibility}");
            if (_remoteHelp is null || !_settings.RemoteHelpShown || !AppSettings.Load().RemoteHelpShown || SmokeFind<StackPanel>(_remoteHelp, p => p.Tag as string == "help:openrouter") is null)
                return Fail("the first-use key help did not show (or was not saved as shown)");
            _remoteHelp.Close();
            if (SmokeFind<StackPanel>(RemoteApiView, p => p.Tag as string == "RemoteModels") is not { Children.Count: 3 } ||
                !fake.Seen.Contains("https://openrouter.ai/api/v1/models"))
                return Fail($"the model list: {string.Join(", ", fake.Seen)}");

            // The key help shows once by itself; Key help reopens it with the selected service first, and its links open outside.
            OpenRemoteApi();
            await Loaded();
            if (_remoteHelp is not null)
                return Fail("the key help showed a second time");
            Service("groq");
            SmokeClick(Find<Button>("RemoteKeyHelp"));
            if (_remoteHelp is null || SmokeFind<StackPanel>(_remoteHelp, p => (p.Tag as string)?.StartsWith("help:", StringComparison.Ordinal) == true)?.Tag as string != "help:groq")
                return Fail("Key help did not open on the selected service (Groq)");
            SmokeFind<System.Windows.Documents.Hyperlink>(_remoteHelp, h => h.Tag as string == "link:groq")?.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
            if (!opened.SequenceEqual([RemoteCatalog.Services.First(s => s.Id == "groq").KeyUrl]))
                return Fail($"the Groq key link opened [{string.Join(", ", opened)}]");
            _remoteHelp.Close();

            // OpenRouter: search, a model's providers (columns, "—" for unknowns), Vision only.
            _cards.Add(worker); // the Apply list is filled when the page opens
            SaveWorkers();
            OpenRemoteApi("reviewer");
            Service("openrouter");
            Find<TextBox>("RemoteKeyEnv").Text = Unset;
            await Loaded();
            Find<TextBox>("RemoteSearch").Text = "qwen";
            if (Find<StackPanel>("RemoteModels").Children is not { Count: 1 } found || ((RemoteCatalog.ModelEntry)((ToggleButton)found[0]).Tag).Id != "qwen/qwen3-coder" ||
                Find<TextBlock>("RemoteModelsNote").Text != "1 of 3 models")
                return Fail("search did not narrow the models to Qwen3 Coder");
            Find<TextBox>("RemoteSearch").Text = "";
            if (SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: Glm }) is not { } glm)
                return Fail("clearing the search did not bring the models back");
            SmokeClick(glm);
            await Loaded();
            if (Find<TextBox>("RemoteModelId").Text != Glm || glm.IsChecked != true || !fake.Seen.Contains($"{OpenRouter}/models/{Glm}/endpoints"))
                return Fail($"choosing a model: id {Find<TextBox>("RemoteModelId").Text}, asked {string.Join(", ", fake.Seen)}");
            var head = Find<StackPanel>("RemoteProviderRows").Children.OfType<Grid>().FirstOrDefault();
            string[] wantRows = ["Relace|fp4|$0.0285|$0.0923|$0.0285|—|—|93.3|", "OpenInference|fp4|$0.03|$0.22|$0.02|0.45|86|100|",
                "GMICloud|fp8|$0.06|$0.44|—|—|—|99.5|", "Mystery|—|—|—|—|—|—|—|✓"];
            if (Find<StackPanel>("RemoteProviders").Visibility != Visibility.Visible ||
                string.Join("|", head?.Children.OfType<TextBlock>().Select(t => t.Text) ?? []) != "|Provider|Quant.|In $/M|Out $/M|Cache $/M|Latency|tok/s|Uptime|Vision" ||
                !Rows().SequenceEqual(wantRows))
                return Fail($"the providers table: {string.Join(" / ", Rows())}");
            Toggle(Find<ToggleButton>("RemoteVisionOnly"));
            if (!Rows().SequenceEqual([wantRows[3]]))
                return Fail($"Vision only left {string.Join(" / ", Rows())}");
            Toggle(Find<ToggleButton>("RemoteVisionOnly"));
            if (Rows().Count != 4)
                return Fail($"Vision only off left {Rows().Count} providers");

            // Ticked providers keep the order ticked (not sorted), on the page and in what Apply writes.
            Toggle(Tick("relace/fp4")!);
            Toggle(Tick("open-inference/fp4")!);
            if (OrderText() != "Only, in this order: Relace (relace/fp4) → OpenInference (open-inference/fp4)" || Tick("relace/fp4")?.Content as string != "1" ||
                Tick("open-inference/fp4")?.Content as string != "2")
                return Fail($"the tick order shows \"{OrderText()}\"");
            var ticked = new LauncherLogic.RemoteRole(OpenRouter, Glm, Unset, "", "relace/fp4, open-inference/fp4");
            Apply();
            if (Wrote("reviewer", ticked) is { } wrong)
                return Fail($"Apply to REVIEWER wrote {wrong}");

            // Busy: refused, nothing written.
            Reset();
            ToastStack.Children.Clear();
            _chatBusy = true;
            Apply();
            _chatBusy = false;
            if (!Toasted("Wait for the reply or run to finish") || !Unchanged())
                return Fail("Apply during a reply was not refused");

            // COMPACTOR following MAIN gets its own roles.compactor, then the remote settings.
            foreach (var t in targets)
            {
                var root = JsonNode.Parse(File.ReadAllText(t))!.AsObject();
                root["roles"]![LauncherLogic.Compactor] = new JsonObject { ["same_as"] = "planner" };
                File.WriteAllText(t, root.ToJsonString());
            }
            Target(LauncherLogic.Compactor);
            Apply();
            if (Wrote(LauncherLogic.Compactor, ticked) is { } compactor)
                return Fail($"Apply to COMPACTOR wrote {compactor}");
            if (targets.FirstOrDefault(t => !LauncherLogic.CompactorHasOwn(JsonNode.Parse(File.ReadAllText(t))!["roles"] as JsonObject)) is { } follows)
                return Fail($"COMPACTOR still follows MAIN in {follows}");
            Reset();

            // A custom worker keeps its remote settings on the card (and in the saved workers).
            Target(worker.Role);
            Apply();
            if (worker.Config.Remote != ticked || worker.ModelId != Glm || AppSettings.Load().Workers.FirstOrDefault(w => w.Id == worker.Role)?.Remote != ticked)
                return Fail($"Apply to a custom worker: {worker.Config.Remote}");

            // Another service starts with no model and no providers.
            Service("openai");
            if (Find<TextBox>("RemoteModelId").Text.Length != 0 || OrderText() != "Any provider.")
                return Fail($"switching the service kept model \"{Find<TextBox>("RemoteModelId").Text}\", order \"{OrderText()}\"");

            // A service that lists models only with a key, without one: the note, no request, and a typed model id applies.
            Find<TextBox>("RemoteKeyEnv").Text = Unset;
            await Loaded();
            if (Find<TextBlock>("RemoteModelsNote") is not { Visibility: Visibility.Visible } needsKey ||
                needsKey.Text != "OpenAI lists its models only with an API key: set OPENAI_API_KEY (see Key help), or type the model id below." ||
                fake.Seen.Any(u => u.Contains("api.openai.com", StringComparison.Ordinal)) || Find<TextBox>("RemoteModelId") is not { Visibility: Visibility.Visible, IsReadOnly: false } ||
                Find<StackPanel>("RemoteProviders").Visibility == Visibility.Visible)
                return Fail("OpenAI without a key: no needs-a-key note, or the models were asked for");
            Reset();
            Target("reviewer");
            var typed = new LauncherLogic.RemoteRole("https://api.openai.com/v1", "", Unset, "");
            Apply();
            if (Find<TextBlock>("RemoteError") is not { Visibility: Visibility.Visible } empty || empty.Text != LauncherLogic.ValidateRemoteRole(typed) || !Unchanged())
                return Fail("Apply with no model id was not refused");
            Find<TextBox>("RemoteModelId").Text = "gpt-4o-mini";
            Apply();
            if (Wrote("reviewer", typed with { Model = "gpt-4o-mini" }) is { } openai)
                return Fail($"Apply of a typed model id wrote {openai}");
            Reset();
            Service("custom");
            Find<TextBox>("RemoteBaseUrl").Text = "ftp://x";
            Apply();
            if (Find<TextBlock>("RemoteError").Text != LauncherLogic.ValidateRemoteRole(new("ftp://x", "m", Unset, "")) || !Unchanged())
                return Fail($"Apply with a bad base URL: \"{Find<TextBlock>("RemoteError").Text}\"");

            // A preset's provider OpenRouter does not list stays in the order until Clear.
            OpenRemoteApi("reviewer", new LauncherLogic.RemoteRole(OpenRouter, Glm, Unset, "", "gone/x, relace/fp4"));
            await Loaded();
            if (OrderText() != "Only, in this order: gone/x → Relace (relace/fp4)" || Tick("relace/fp4")?.Content as string != "2")
                return Fail($"the preset's order shows \"{OrderText()}\"");
            SmokeClick(Find<Button>("RemoteClearOrder"));
            if (OrderText() != "Any provider." || Tick("relace/fp4")?.IsChecked != false || Find<Button>("RemoteClearOrder").Visibility == Visibility.Visible)
                return Fail($"Clear left \"{OrderText()}\"");

            if (SmokeFind<Button>(SidebarList, b => b.Tag as string == "+New chat") is not { } newChat)
                return Fail("no New chat in the sidebar");
            newChat.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(400);
            if (RemoteApiView.Visibility == Visibility.Visible || (CodeView.Visibility != Visibility.Visible && ChatView.Visibility != Visibility.Visible))
                return Fail($"New chat left page {RemoteApiView.Visibility}, code {CodeView.Visibility}, chat {ChatView.Visibility}");
            return true;
        }
        finally
        {
            _remoteHelp?.Close();
            _chatBusy = false;
            if (_cards.Contains(worker))
                RemoveWorker(worker);
            Reset();
            PrefillCards();
            (_remoteHttp, _openExternal, _settings.RemoteHelpShown, _remoteLast) = (http, open, shown, last);
        }
    }
}

// Canned OpenRouter replies for --ui-smoke and --screenshots: /models lists three models, /models/<id>/endpoints four
// providers (one with latency and throughput, as with a key; one with an unknown price). Records every address asked.
internal sealed class RemoteApiFakeHandler : HttpMessageHandler
{
    public List<string> Seen { get; } = [];

    private const string Models = "{\"data\":[" +
        "{\"id\":\"z-ai/glm-5.3-flash\",\"name\":\"Z.ai: GLM 5.3 Flash\",\"context_length\":1048576}," +
        "{\"id\":\"qwen/qwen3-coder\",\"name\":\"Qwen: Qwen3 Coder\",\"context_length\":262144}," +
        "{\"id\":\"openai/gpt-4o-mini\",\"name\":\"OpenAI: GPT-4o-mini\",\"context_length\":128000}]}";

    private const string Endpoints = "{\"data\":{\"id\":\"z-ai/glm-5.3-flash\",\"architecture\":{\"input_modalities\":[\"text\"]},\"endpoints\":[" +
        "{\"provider_name\":\"OpenInference\",\"tag\":\"open-inference/fp4\",\"quantization\":\"fp4\",\"context_length\":1048576," +
        "\"pricing\":{\"prompt\":\"0.00000003\",\"completion\":\"0.00000022\",\"input_cache_read\":\"0.00000002\"},\"uptime_last_30m\":100," +
        "\"supports_image_reference\":false,\"latency_last_30m\":{\"p50\":450},\"throughput_last_30m\":{\"p50\":85.5}}," +
        "{\"provider_name\":\"Relace\",\"tag\":\"relace/fp4\",\"quantization\":\"fp4\",\"context_length\":1048576," +
        "\"pricing\":{\"prompt\":\"0.0000000285\",\"completion\":\"0.0000000923\",\"input_cache_read\":\"0.0000000285\"},\"uptime_last_30m\":93.33333333333333," +
        "\"supports_image_reference\":false,\"latency_last_30m\":null,\"throughput_last_30m\":null}," +
        "{\"provider_name\":\"GMICloud\",\"tag\":\"gmicloud/fp8\",\"quantization\":\"fp8\",\"context_length\":202752," +
        "\"pricing\":{\"prompt\":\"0.00000006\",\"completion\":\"0.00000044\"},\"uptime_last_30m\":99.5,\"latency_last_30m\":null,\"throughput_last_30m\":null}," +
        "{\"provider_name\":\"Mystery\",\"tag\":\"mystery\",\"quantization\":\"unknown\",\"context_length\":65536,\"pricing\":{\"prompt\":\"-1\"}," +
        "\"uptime_last_30m\":null,\"supports_image_reference\":true,\"latency_last_30m\":null,\"throughput_last_30m\":null}]}}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (Seen)
            Seen.Add(url);
        var body = url.EndsWith("/endpoints", StringComparison.Ordinal) ? Endpoints : url.EndsWith("/models", StringComparison.Ordinal) ? Models : null;
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

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
using System.Windows.Media.Imaging;

namespace AgentOrchestrator;

// The Remote API page (the sidebar's Remote API), in the centre column in place of Code and Chat: an OpenAI-compatible
// service, its models (RemoteCatalog), for OpenRouter the providers to use in order, and Apply to a worker. The key stays
// in the environment variable the page names; the app never stores it.
public partial class MainWindow
{
    private HttpClient _remoteHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan }; // RemoteCatalog caps each request at 15 s; smokes put a fake handler here
    private Action<string> _openExternal = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); // smokes record the address instead
    private DialogShell? _remoteHelp;
    private Func<ContextMenu> _remoteKeyMenu = () => new(); // the API key field's saved-keys menu (the page's); smokes click its items
    private (string Service, string BaseUrl, string Model)? _remoteLast; // the page's last choice, this session only
    private CancellationTokenSource? _remoteModelsCts, _remoteProvidersCts;
    private Task _remoteModelsTask = Task.CompletedTask, _remoteProvidersTask = Task.CompletedTask; // smokes await them

    // The providers table: 0 = the Provider column, which takes the width left over (at least 130).
    private const double ConnectionOneLine = 700, KeyEnvWidth = 210; // narrower: the connection settings take two lines; OPENROUTER_API_KEY fits
    private static readonly double[] ProviderColumns = [34, 0, 64, 76, 76, 82, 68, 56, 64, 52];
    private const double ProviderRowHeight = 28;
    private static readonly string?[] HeadTips = [null, null, "Quantization", "Input, USD per million tokens", "Output, USD per million tokens",
        "Cached input read, USD per million tokens", "Median latency in seconds, last 30 minutes", "Median throughput in tokens per second, last 30 minutes",
        "Uptime in %, last 30 minutes", "Takes images"];

    // role: the worker Apply to starts on; preset: its current remote settings (service, model, provider order).
    private void OpenRemoteApi(string? role = null, LauncherLogic.RemoteRole? preset = null)
    {
        CloseRemoteApi();
        CloseModelHub();
        _settings.RemoteFavorites = RemoteFavoritesKnown(_settings.RemoteFavorites); // saved with the next star
        var red =new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A));
        var amber = new SolidColorBrush(Color.FromRgb(0xD9, 0x9A, 0x2B));
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
        var keyStatus = new TextBlock { Tag = "RemoteKeyStatus", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        RemoteCatalog.Service Selected() => (RemoteCatalog.Service)((ComboBoxItem)service.SelectedItem).Tag;
        // The key goes only where the user confirmed: a known service's own host, or a Custom URL confirmed with Enter or
        // Load models (a preset's URL counts). keyVar is the variable name committed with Enter or leaving the box, never a
        // half-typed one that happens to name another variable.
        var keyVar = "";
        var urlConfirmed = preset is not null;
        var loadModels = new Button { Content = "Load models", Tag = "RemoteLoadModels", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 4, 0, 0) };
        DockPanel.SetDock(loadModels, Dock.Right);
        // One line: Service, Base URL (the width left over), key variable, its status. Narrower than ConnectionOneLine, the key
        // variable and its status take a second line, so nothing clips.
        UIElement[] connectionCells =
        [
            new StackPanel { Children = { Label("Service"), service } }, new StackPanel { Children = { Label("Base URL"), new DockPanel { Children = { loadModels, baseUrl } } } },
            new StackPanel { Children = { Label("API key variable"), keyEnv } }, new Border { Height = 30, VerticalAlignment = VerticalAlignment.Bottom, Child = keyStatus }, // level with the boxes
        ];
        var connection = new Grid
        {
            Tag = "RemoteConnection", RowDefinitions = { new RowDefinition(), new RowDefinition() },
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(170) }, new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(KeyEnvWidth) }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        void PlaceConnection(bool oneLine)
        {
            (int Column, int Row, int Span)[] at = oneLine ? [(0, 0, 1), (1, 0, 1), (2, 0, 1), (3, 0, 1)] : [(0, 0, 1), (1, 0, 3), (0, 1, 1), (1, 1, 3)];
            for (var i = 0; i < connectionCells.Length; i++)
            {
                Grid.SetColumn(connectionCells[i], at[i].Column);
                Grid.SetRow(connectionCells[i], at[i].Row);
                Grid.SetColumnSpan(connectionCells[i], at[i].Span);
                ((FrameworkElement)connectionCells[i]).Margin = new Thickness(at[i].Column == 0 ? 0 : 12, 0, 0, 0);
            }
            connection.ColumnDefinitions[2].Width = new GridLength(oneLine ? KeyEnvWidth : 0);
            keyStatus.MaxWidth = oneLine ? 120 : double.PositiveInfinity; // "✗ Key not set — see Key help" takes two lines there
        }
        foreach (var cell in connectionCells)
            connection.Children.Add(cell);
        PlaceConnection(true);
        connection.SizeChanged += (_, e) => PlaceConnection(e.NewSize.Width >= ConnectionOneLine);
        string? EnvKey() => keyVar.Length > 0 && Environment.GetEnvironmentVariable(keyVar) is { Length: > 0 } key ? key : null;
        string? Key() => (Selected() == custom ? urlConfirmed : RemoteCatalog.ServiceFor(baseUrl.Text) == Selected()) ? EnvKey() : null;

        // ---- models
        var models = new List<RemoteCatalog.ModelEntry>();
        // A box with a hint while empty, the hint where the box's own text (and its caret) starts: read from the laid-out caret
        // (the template's content host sits left of the box's Padding), so a theme change cannot misalign them.
        Grid Hinted(TextBox box, string text)
        {
            var hint = RemoteMuted(text);
            (hint.IsHitTestVisible, hint.VerticalAlignment, hint.TextWrapping, hint.Margin) = (false, VerticalAlignment.Center, TextWrapping.NoWrap, new Thickness(22, 4, 0, 0));
            void Place() // when shown: a box in a collapsed panel has no laid-out caret yet
            {
                if (box.IsVisible && box.GetRectFromCharacterIndex(0).X is var x and > 0 and < double.PositiveInfinity) // Rect.Empty before layout
                    hint.Margin = new Thickness(x, 4, 0, 0);
            }
            box.Loaded += (_, _) => Place();
            box.SizeChanged += (_, _) => Place();
            box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            return new Grid { Children = { box, hint } };
        }
        var search = Box("RemoteSearch");
        var searchBox = Hinted(search, "Search models");
        var list = new StackPanel { Tag = "RemoteModels" };
        var listScroll = new ScrollViewer
        {
            Tag = "RemoteModelsScroll", Content = list, MaxHeight = 9 * ModelRow, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        TextBlock Head(string text, string tag)
        {
            var t = RemoteMuted(text);
            (t.Tag, t.FontWeight, t.TextWrapping) = (tag, FontWeights.SemiBold, TextWrapping.NoWrap);
            return t;
        }
        var listHead = ModelColumns(new FrameworkElement(), Head("Model name", "name"), Head("Context", "context"));
        listHead.Tag = "RemoteModelsHeader";
        listHead.Margin = new Thickness(ModelRowSide, 2, ModelRowSide, 4);
        KeepClearOfScrollBar(listScroll, listHead);
        DockPanel.SetDock(listHead, Dock.Top);
        var listBox = new Border
        {
            Tag = "RemoteModelsBox", Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1),
            Child = new DockPanel { Children = { listHead, listScroll } }, // the header stays put while the list scrolls
        };
        listBox.SetResourceReference(Border.BackgroundProperty, "Card");
        listBox.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        listBox.SetResourceReference(Border.CornerRadiusProperty, "SmallRadius");
        var modelsNote = RemoteMuted("");
        modelsNote.Tag = "RemoteModelsNote";
        modelsNote.Margin = new Thickness(2, 6, 0, 0);
        // What Apply writes: the model picked in the list, read-only (select or Copy it). When the service cannot list its
        // models (no key, an unconfirmed or failing address, an empty list) the manual box takes the list's place and this
        // box mirrors it.
        var modelId = Box("RemoteModelId");
        modelId.IsReadOnly = true;
        var copyId = new Button { Content = "Copy", Tag = "RemoteModelCopy", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 4, 0, 0) };
        DockPanel.SetDock(copyId, Dock.Right);
        copyId.Click += (_, _) =>
        {
            try { Clipboard.SetText(modelId.Text); }
            catch (System.Runtime.InteropServices.COMException) { } // another app holds the clipboard: the box is still selectable
        };
        var manual = Box("RemoteModelManual");
        var manualHint = RemoteMuted("Enter a model id");
        (manualHint.IsHitTestVisible, manualHint.VerticalAlignment, manualHint.Margin) = (false, VerticalAlignment.Center, new Thickness(12, 4, 0, 0));
        manual.TextChanged += (_, _) =>
        {
            manualHint.Visibility = manual.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            modelId.Text = manual.Text;
        };
        var manualBox = new Grid { Margin = new Thickness(0, 6, 0, 0), Children = { manual, manualHint } };
        // Starred models of every known service; choosing one switches to its service and picks it. Shown once one is starred.
        var favorites = new ComboBox { Tag = "RemoteFavorites", Margin = new Thickness(0, 4, 0, 0) };
        var favoritesBox = new StackPanel { Children = { Label("Favorites"), favorites } };
        void Manual(bool on) // the list cannot be had: type the id instead
        {
            if (on)
                manual.Text = modelId.Text;
            manualBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- providers (OpenRouter)
        var rows = new List<RemoteCatalog.ProviderRow>();
        var order = (preset?.Provider ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var (sortColumn, sortDescending) = (-1, false); // -1: as rows arrive, cheapest input first
        // The rows scroll (9 at most in view) under a header that stays put; header and rows scroll sideways together.
        var table = new StackPanel { Tag = "RemoteProviderRows" };
        var tableRows = new ScrollViewer
        {
            Tag = "RemoteProvidersScroll", Content = table, MaxHeight = 9 * ProviderRowHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var tableHead = new Border { Tag = "RemoteProvidersHeader", BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 2) }; // its bottom border is the rule
        tableHead.SetResourceReference(Border.BorderBrushProperty, "Border");
        KeepClearOfScrollBar(tableRows, tableHead);
        var tableBox = new StackPanel { MinWidth = ProviderColumns.Sum() + 130 + SystemParameters.VerticalScrollBarWidth, Children = { tableHead, tableRows } };
        var tableScroll = new ScrollViewer
        {
            Tag = "RemoteProvidersTable", Content = tableBox, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 8, 0, 0),
        };
        tableScroll.SizeChanged += (_, e) => tableBox.Width = Math.Max(tableBox.MinWidth, e.NewSize.Width); // the whole column; narrower scrolls
        var providersNote = RemoteMuted("");
        providersNote.Tag = "RemoteProvidersNote";
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
        // Price: OpenRouter's provider.sort "price" and max_price ($/M, empty = no cap), and the model id's ":floor" variant
        // (the Model id box keeps the base id; Apply adds the suffix).
        const string Floor = ":floor";
        ToggleButton Switch(string tag) => new() { Style = (Style)FindResource("ToggleSwitch"), Tag = tag, VerticalAlignment = VerticalAlignment.Center };
        StackPanel Labelled(string text, UIElement control)
        {
            var label = RemoteMuted(text);
            (label.VerticalAlignment, label.TextWrapping, label.Margin) = (VerticalAlignment.Center, TextWrapping.NoWrap, new Thickness(0, 0, 8, 0));
            return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 24, 0), Children = { label, control } };
        }
        Grid MaxBox(TextBox box, string hint)
        {
            box.ToolTip = "US dollars per million tokens, such as 0.3; empty: no cap";
            var g = Hinted(box, hint);
            (g.Width, g.Margin) = (120, new Thickness(0, 0, 12, 0));
            return g;
        }
        var sortPrice = Switch("RemoteSortPrice");
        var floor = Switch("RemoteFloor");
        var maxIn = Box("RemoteMaxInput");
        var maxOut = Box("RemoteMaxOutput");
        var cheapestNow = RemoteMuted("");
        cheapestNow.Tag = "RemoteCheapestNow";
        cheapestNow.Margin = new Thickness(0, 6, 0, 0);
        var floorNote = RemoteMuted("");
        floorNote.Tag = "RemoteFloorNote";
        floorNote.Margin = new Thickness(0, 2, 0, 0);
        var priceHelp = RemoteMuted("Cheapest and :floor apply only when no provider is ticked; the max prices always apply. " +
            "The cheapest provider is not always the best: quantization, speed and reliability differ.");
        priceHelp.Margin = new Thickness(0, 2, 0, 0);
        var priceRow = new WrapPanel
        {
            Tag = "RemotePriceRow", Margin = new Thickness(0, 8, 0, 0),
            Children = { Labelled("Cheapest", sortPrice), MaxBox(maxIn, "Max in $/M"), MaxBox(maxOut, "Max out $/M"), Labelled("Use :floor", floor) },
        };
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
                priceRow, cheapestNow, floorNote, priceHelp,
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
        // The key typed here: encrypted for this Windows user (ApiKeys) and put in the environment under the variable's name.
        // Several keys can be saved per variable, each under a name; the field shows the active one's name (never a key) and its
        // chevron lists them to switch, add, rename or remove. With none saved, or while adding one, it is the paste box.
        var keyInput = new PasswordBox { Tag = "RemoteKeyInput", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(14, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(keyInput, "API key");
        var keyShown = new TextBlock { Tag = "RemoteKeyShown", Margin = new Thickness(14, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var keyMenuButton = new Button { Tag = "RemoteKeyMenu", Style = (Style)FindResource("BareButton"), Padding = new Thickness(8, 0, 12, 0), ToolTip = "Saved keys",
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center } };
        keyMenuButton.SetResourceReference(Control.ForegroundProperty, "Muted");
        System.Windows.Automation.AutomationProperties.SetName(keyMenuButton, "Saved keys");
        Grid.SetColumn(keyMenuButton, 1);
        var keyField = new Border { Height = 30, Margin = new Thickness(0, 4, 0, 0), BorderThickness = new Thickness(1), Child = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { keyInput, keyShown, keyMenuButton },
        } };
        foreach (var (property, key) in new[] { (Border.BackgroundProperty, "Card"), (Border.BorderBrushProperty, "ControlBorder"), (Border.CornerRadiusProperty, "ControlRadius") })
            keyField.SetResourceReference(property, key);
        keyInput.GotKeyboardFocus += (_, _) => keyField.SetResourceReference(Border.BorderBrushProperty, "Accent");
        keyInput.LostKeyboardFocus += (_, _) => keyField.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        var keyName = new TextBox { Tag = "RemoteKeyName", Height = 30, MaxLength = 40, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(12, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "The key's name" };
        System.Windows.Automation.AutomationProperties.SetName(keyName, "Key name");
        var saveKey = new Button { Content = "Save key", Tag = "RemoteKeySave", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 6, 0, 0) };
        DockPanel.SetDock(saveKey, Dock.Right);
        var keyRowNote = RemoteMuted("Paste the key here: it is kept encrypted for your Windows account in the app's data folder and sent only to the selected service. " +
                                     "Or set the variable yourself; a key saved here wins. Save several under different names and switch with the arrow.");
        keyRowNote.Margin = new Thickness(0, 2, 0, 0);
        // The name box and Save key go under the field (only while pasting a key), so a narrow page still fits the key.
        var keyRow = new StackPanel { Children = { Label("API key"), keyRowNote, keyField, new DockPanel { Children = { saveKey, keyName } } } };
        var addingKey = false; // "Add new key…": the paste box although keys are saved

        void KeyStatus()
        {
            var env = keyVar;
            var set = EnvKey() is not null;
            var labels = env.Length > 0 ? ApiKeys.Labels(env) : [];
            var active = env.Length > 0 ? ApiKeys.Active(env) : null;
            var saved = active is not null;
            var entry = addingKey || labels.Count == 0;
            keyInput.Visibility = saveKey.Visibility = keyName.Visibility = entry ? Visibility.Visible : Visibility.Collapsed;
            keyShown.Visibility = entry ? Visibility.Collapsed : Visibility.Visible;
            keyShown.Text = $"••••••••   {active}";
            keyMenuButton.Visibility = labels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!entry || keyName.Text.Trim().Length == 0)
                keyName.Text = labels.Count == 0 ? "Default" : Enumerable.Range(labels.Count + 1, labels.Count + 1).Select(n => $"Key {n}").First(n => !labels.Contains(n));
            keyStatus.Text = env.Length == 0 ? "No key variable" : saved ? $"✓ Key set: {active}" : set ? "✓ Key set" : "✗ Key not set — see Key help";
            keyStatus.ToolTip = env.Length == 0 ? "No variable: requests go without a key." : saved ? $"A key is saved in the app for {env}." : set ? $"{env} is set in your environment." : $"{env} is not set: paste the key into the API key box below, or see Key help.";
            if (set || env.Length == 0)
                keyStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            else
                keyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0xC8, 0x73));
            keyNote.Visibility = set ? Visibility.Collapsed : Visibility.Visible;
        }
        bool CommitKeyEnv() // true if the variable changed
        {
            var was = keyVar;
            keyVar = keyEnv.Text.Trim();
            if (keyVar != was)
                (addingKey, keyName.Text) = (false, "");
            KeyStatus();
            return keyVar != was;
        }

        var pinnedOverCap = false; // the list shows the pick although it is over the price cap
        void RenderModels()
        {
            list.Children.Clear();
            var q = search.Text.Trim();
            var (capIn, capOut, picked) = (LauncherLogic.MaxPrice(maxIn.Text), LauncherLogic.MaxPrice(maxOut.Text), modelId.Text.Trim());
            var found = models.Where(m => q.Length == 0 || m.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            bool Over(RemoteCatalog.ModelEntry m) => RemoteOverCap(m.InputPerM, m.OutputPerM, capIn, capOut);
            var shown = found.Where(m => !Over(m) || m.Id == picked).ToList(); // the pick stays, tagged, so the user does not lose it
            var capped = found.Count - shown.Count;
            pinnedOverCap = shown.Any(Over);
            foreach (var m in shown.Take(200)) // ponytail: a 200-row cap instead of a virtualized list; search narrows the rest
            {
                var context = RemoteMuted(RemoteContext(m.ContextLength));
                context.Tag = "context";
                var name =new TextBlock { Tag = "name", TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = m.Id };
                name.Inlines.Add(new Run(m.Name));
                if (m.Name != m.Id)
                {
                    var id = new Run("   " + m.Id) { FontSize = 11 };
                    id.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                    name.Inlines.Add(id);
                }
                if (Over(m))
                {
                    var over = new Run("   over cap") { FontSize = 10.5, Tag = "overCap" };
                    over.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                    name.Inlines.Add(over);
                }
                Button? star = null;
                if (Selected() != custom) // a Custom URL is the user's own address: nothing to come back to by name
                {
                    var favorite = RemoteFavorite(Selected().Id, m.Id);
                    star = new Button { Tag = "fav:" + favorite, Style = (Style)FindResource("GhostButton"), Width = 24, Height = 22, Padding = new Thickness(0), FontSize = 14 };
                    void Star()
                    {
                        var on = _settings.RemoteFavorites.Contains(favorite);
                        (star.Content, star.ToolTip) = on ? ("★", "Remove from favorites") : ("☆", "Add to favorites");
                        if (on)
                            star.Foreground = amber;
                        else
                            star.ClearValue(ForegroundProperty);
                    }
                    Star();
                    star.Click += (_, e) =>
                    {
                        e.Handled = true; // starring does not choose the row
                        _settings.RemoteFavorites = RemoteFavoritesWith(_settings.RemoteFavorites, favorite, !_settings.RemoteFavorites.Contains(favorite));
                        _settings.Save();
                        Star();
                        RenderFavorites();
                    };
                }
                var row = new ToggleButton
                {
                    Content = ModelColumns(RemoteBadge(m.Id), name, context, star), Tag = m, IsChecked = m.Id == modelId.Text.Trim(),
                    Height = ModelRow - 1, Padding = new Thickness(ModelRowSide, 0, ModelRowSide, 0), Margin = new Thickness(0, 0, 0, 1),
                };
                row.SetResourceReference(StyleProperty, "NavToggle");
                row.Click += (_, _) => modelId.Text = m.Id; // its TextChanged marks the row and loads the providers
                list.Children.Add(row);
            }
            listBox.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (models.Count > 0)
                Note(modelsNote, (shown.Count > 200 ? $"Showing 200 of {shown.Count} models: search to narrow." : q.Length > 0 || capped > 0 ? $"{shown.Count} of {models.Count} models" : $"{models.Count} models") +
                    (capped > 0 ? $" · {capped} over your price cap hidden" : ""));
        }

        void RenderFavorites()
        {
            favorites.Items.Clear();
            foreach (var f in _settings.RemoteFavorites)
            {
                var (sid, mid) = RemoteFavoriteParts(f)!.Value; // RemoteFavoritesKnown kept well-formed ones only
                var label = new TextBlock { Text = $"{RemoteCatalog.Services.First(s => s.Id == sid).Name} · {mid}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                favorites.Items.Add(new ComboBoxItem { Tag = f, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { RemoteBadge(mid), label } } });
            }
            FavoriteShown();
            favoritesBox.Visibility = favorites.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void FavoriteShown() // the dropdown shows the model chosen when it is a favorite, else nothing
        {
            var now = RemoteFavorite(Selected().Id, modelId.Text.Trim());
            favorites.SelectedItem = favorites.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == now);
        }

        void LoadModels()
        {
            _remoteModelsCts?.Cancel();
            var cts = _remoteModelsCts = new CancellationTokenSource();
            models = [];
            RenderModels();
            Manual(true);
            var s = Selected();
            var url = baseUrl.Text.Trim();
            var key = Key();
            _remoteLast = (s.Id, url, modelId.Text.Trim());
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                Note(modelsNote, "Enter the service's base URL (such as http://127.0.0.1:8080/v1) to list its models.");
                return;
            }
            if (s == custom && !urlConfirmed) // nothing goes to a half-typed address
            {
                Note(modelsNote, "Press Enter or Load models to list this server's models.");
                return;
            }
            if (s.ModelsNeedKey && s != custom && key is null)
            {
                Note(modelsNote, $"{s.Name} lists its models only with an API key: set {s.KeyEnv} (see Key help), or type the model id below.");
                return;
            }
            Note(modelsNote, "Loading models…");
            Manual(false);
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
                    Manual(models.Count == 0);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) // any failure replaces "Loading models…": an escaped one would leave it there for good
                {
                    if (ct.IsCancellationRequested)
                        return;
                    Manual(true);
                    Note(modelsNote, e.Message + (key is null && s == custom ? " If it needs a key, name its variable above." : ""), error: true);
                }
            }
        }

        void RenderProviders()
        {
            table.Children.Clear();
            var (capIn, capOut) = (LauncherLogic.MaxPrice(maxIn.Text), LauncherLogic.MaxPrice(maxOut.Text));
            bool Over(RemoteCatalog.ProviderRow r) => RemoteOverCap(r.InputPerM, r.OutputPerM, capIn, capOut);
            orderText.Text = order.Count == 0 ? "Any provider." : "Only, in this order: ";
            for (var i = 0; i < order.Count; i++)
            {
                var row = rows.FirstOrDefault(r => r.Tag == order[i]);
                orderText.Inlines.Add((i > 0 ? " → " : "") + (row?.Name is { Length: > 0 } n ? $"{n} ({order[i]})" : order[i]));
                if (rows.Count > 0 && row is null) // still applies: OpenRouter then refuses every request (no fallbacks)
                    orderText.Inlines.Add(new Run(" (not offered for this model)") { Foreground = amber });
                else if (row is not null && Over(row)) // hidden in the table, kept in the order: the tick is the user's
                    orderText.Inlines.Add(new Run(" (over cap)") { Foreground = amber });
            }
            clearOrder.Visibility = order.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var withinCap = rows.Where(r => !Over(r)).ToList();
            Note(cheapestNow, withinCap.Count == 0 ? "" : $"Cheapest now: in {RemotePrice(withinCap.Min(r => r.InputPerM))} / out {RemotePrice(withinCap.Min(r => r.OutputPerM))}");
            tableScroll.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (rows.Count == 0)
                return;
            Grid Line(params UIElement[] cells)
            {
                var g = new Grid { Height = ProviderRowHeight };
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
            // A header click sorts by its column: ascending, then descending, then back to cheapest input first.
            var heads = new[] { "", "Provider", "Quant.", "In $/M", "Out $/M", "Cache $/M", "Latency", "tok/s", "Uptime", "Vision" }
                .Select((h, i) =>
                {
                    var t = RemoteMuted(h);
                    t.TextWrapping = TextWrapping.NoWrap;
                    t.FontWeight = FontWeights.SemiBold;
                    t.VerticalAlignment = VerticalAlignment.Bottom;
                    t.Margin = new Thickness(0, 0, i == sortColumn ? 2 : 12, 4); // the arrow hangs in the gap, the label stays over its figures
                    t.TextAlignment = i >= 3 ? TextAlignment.Right : TextAlignment.Left;
                    if (i == 0)
                        return (UIElement)t;
                    if (i == sortColumn)
                        t.Inlines.Add(new Run(sortDescending ? " ▼" : " ▲") { FontSize = 8 });
                    var sort = new Button { Content = t, Tag = $"sort:{i}", ToolTip = HeadTips[i] ?? "Sort by " + h.ToLowerInvariant() };
                    sort.SetResourceReference(StyleProperty, "BareButton");
                    sort.Click += (_, _) =>
                    {
                        (sortColumn, sortDescending) = sortColumn != i ? (i, false) : !sortDescending ? (i, true) : (-1, false);
                        RenderProviders();
                    };
                    return sort;
                }).ToArray();
            tableHead.Child = Line(heads);
            foreach (var r in SortProviders(rows, sortColumn, sortDescending).Where(r => (visionOnly.IsChecked != true || r.Vision) && !Over(r)))
            {
                var position = order.IndexOf(r.Tag);
                var tick = new ToggleButton
                {
                    Style = (Style)FindResource("SegToggle"), Width = 22, Height = 22, Padding = new Thickness(0), FontSize = 11, Tag = "tick:" + r.Tag,
                    IsChecked = position >= 0, IsEnabled = r.Tag.Length > 0, Content = position >= 0 ? (position + 1).ToString(CultureInfo.InvariantCulture) : "",
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
                catch (Exception e) // as with the models: never left on "Loading providers…"
                {
                    if (!ct.IsCancellationRequested)
                        Note(providersNote, e.Message, error: true);
                }
            }
        }

        // The id Apply writes: the base id, plus ":floor" when that is on. An id with a variant of its own (":free", ":nitro", …)
        // gets no second one: the switch is off and disabled for it.
        string SavedId()
        {
            var id = modelId.Text.Trim();
            return floor.IsChecked == true && id.Length > 0 && !id.Contains(':') ? id + Floor : id;
        }
        void FloorShown()
        {
            var id = modelId.Text.Trim();
            floor.IsEnabled = !id.Contains(':');
            if (!floor.IsEnabled)
                floor.IsChecked = false;
            Note(floorNote, !floor.IsEnabled ? "This id already has a variant: :floor cannot be added." : SavedId() != id ? $"Will be saved as {SavedId()}" : "");
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
            CommitKeyEnv(); // what the box shows is what gets written
            var url = baseUrl.Text.Trim().TrimEnd('/');
            var remote = Selected().HasProviders // the price settings are OpenRouter's
                ? new LauncherLogic.RemoteRole(url, SavedId(), keyVar, "", string.Join(", ", order), sortPrice.IsChecked == true, maxIn.Text.Trim(), maxOut.Text.Trim())
                : new LauncherLogic.RemoteRole(url, modelId.Text.Trim(), keyVar, "");
            if (LauncherLogic.ValidateRemoteRole(remote) is { } problem)
            {
                Note(errorLine, problem, error: true);
                return;
            }
            var name = ChatName(card, card.Role);
            if (card.IsCustom)
            {
                var w = card.Config;
                var before = (w.Remote, w.LocalModelId, w.LocalModelVariant, card.ModelId, card.ModelVariant, new LauncherLogic.ModelInfo(card.ModelName, card.Weights, card.Quant));
                SetCustomRemote(card, remote);
                if (!SyncCustomWorkers() || card.SetupProblem is not null) // not written: the worker stays as it was
                {
                    var refused = card.SetupProblem;
                    (w.Remote, w.LocalModelId, w.LocalModelVariant, card.ModelId, card.ModelVariant, _) = before;
                    card.SetModel(before.Item6);
                    SyncCustomWorkers();
                    Note(errorLine, refused is not null ? $"Not applied to {name}: {refused}." : "Could not update config.json: see the log.", error: true);
                    return;
                }
                SaveWorkers();
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
                    // COMPACTOR following MAIN gets its own copy in the same single write: a refusal or throw writes nothing
                    failed = WriteConfigs(targets, t => LauncherLogic.WriteRemoteRole(t, card.Role, remote, Path.Combine(_workflowRoot ?? "", "config.json")));
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
                Toast($"Applied to {name}: paste the {remote.KeyEnv} key into the API key box on this page", "#F7C873");
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
        loadModels.Visibility = start == custom ? Visibility.Visible : Visibility.Collapsed;
        keyEnv.Text = preset?.KeyEnv is { Length: > 0 } presetKey ? presetKey : start.KeyEnv;
        var presetModel = preset?.Model.Trim();
        if (start.HasProviders && presetModel?.EndsWith(Floor, StringComparison.Ordinal) == true && !presetModel[..^Floor.Length].Contains(':')) // the box shows the base id, the switch the suffix
            (presetModel, floor.IsChecked) = (presetModel[..^Floor.Length], true);
        if (start.HasProviders && preset is not null)
            (sortPrice.IsChecked, maxIn.Text, maxOut.Text) = (preset.SortPrice, preset.MaxInput, preset.MaxOutput);
        modelId.Text = presetModel ?? (_remoteLast is { } m && m.Service == start.Id ? m.Model : "");
        FloorShown();
        CommitKeyEnv();

        service.SelectionChanged += (_, _) =>
        {
            var s = Selected();
            baseUrl.IsReadOnly = s != custom;
            loadModels.Visibility = s == custom ? Visibility.Visible : Visibility.Collapsed;
            order.Clear(); // another service's model, providers and prices mean nothing here
            (sortPrice.IsChecked, floor.IsChecked, maxIn.Text, maxOut.Text) = (false, false, "", "");
            modelId.Text = "";
            FloorShown();
            keyEnv.Text = s.KeyEnv; // Custom's are empty: the last service's key variable and address must not carry over
            CommitKeyEnv(); // before the address, whose TextChanged loads
            baseUrl.Text = s.BaseUrl;
            urlConfirmed = false;
            LoadModels();
            LoadProviders();
        };
        void ConfirmUrl()
        {
            urlConfirmed = true;
            LoadModels();
        }
        baseUrl.TextChanged += (_, _) =>
        {
            urlConfirmed = false;
            LoadModels();
        };
        baseUrl.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                ConfirmUrl();
        };
        loadModels.Click += (_, _) => ConfirmUrl();
        void KeyEnvDone()
        {
            if (CommitKeyEnv())
                LoadModels();
        }
        keyEnv.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                KeyEnvDone();
        };
        keyEnv.LostFocus += (_, _) => KeyEnvDone();
        search.TextChanged += (_, _) => RenderModels();
        foreach (var cap in new[] { maxIn, maxOut }) // live, as the search
            cap.TextChanged += (_, _) =>
            {
                RenderModels();
                RenderProviders();
            };
        modelId.TextChanged += (_, _) =>
        {
            var marked = false;
            foreach (var row in list.Children.OfType<ToggleButton>())
                marked |= (row.IsChecked = ((RemoteCatalog.ModelEntry)row.Tag).Id == modelId.Text.Trim()) == true;
            if (pinnedOverCap || !marked) // the old pick over the cap goes, a new one (a favorite) over the cap comes
                RenderModels();
            _remoteLast = (Selected().Id, baseUrl.Text.Trim(), modelId.Text.Trim());
            order.Clear(); // another model's provider tags (a preset's are set above, before this handler); the prices stay
            FloorShown();
            LoadProviders();
            FavoriteShown();
        };
        floor.Click += (_, _) => FloorShown();
        favorites.SelectionChanged += (_, _) =>
        {
            if (favorites.SelectedItem is not ComboBoxItem { Tag: string f } || f == RemoteFavorite(Selected().Id, modelId.Text.Trim()) || RemoteFavoriteParts(f) is not { } chosen)
                return;
            if (Selected().Id != chosen.Service) // as the Service dropdown: no model or providers carried over, its own key variable and address
                service.SelectedItem = service.Items.OfType<ComboBoxItem>().First(i => ((RemoteCatalog.Service)i.Tag).Id == chosen.Service);
            if (manualBox.Visibility == Visibility.Visible) // the service cannot list its models (yet): the id goes in the box
                manual.Text = chosen.Model;
            else
                modelId.Text = chosen.Model; // marks the row (now or once the list loads) and loads the providers
        };
        visionOnly.Click += (_, _) => RenderProviders();
        clearOrder.Click += (_, _) =>
        {
            order.Clear();
            RenderProviders();
        };
        apply.Click += (_, _) => Apply();
        keyHelp.Click += (_, _) => ShowRemoteHelp(Selected().Id);
        bool KeyChange(Action change, string log) // a saved-keys change: its error on the page, or logged (never a key)
        {
            try
            {
                change();
            }
            catch (Exception x) when (x is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Note(errorLine, x.Message, error: true);
                return false;
            }
            Note(errorLine, "");
            Log(log);
            return true;
        }
        void SaveKey()
        {
            CommitKeyEnv();
            if (!KeyChange(() => ApiKeys.Save(keyVar, keyName.Text, keyInput.Password), $"API key \"{keyName.Text.Trim()}\" saved in the app for {keyVar}"))
                return;
            keyInput.Clear();
            addingKey = false;
            KeyStatus();
            LoadModels(); // the list may need the key
        }
        saveKey.Click += (_, _) => SaveKey();
        foreach (var box in new Control[] { keyInput, keyName })
            box.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                    SaveKey();
                else if (e.Key == System.Windows.Input.Key.Escape && addingKey) // back to the saved keys
                {
                    (addingKey, keyName.Text) = (false, "");
                    keyInput.Clear();
                    KeyStatus();
                }
            };
        _remoteKeyMenu = () =>
        {
            var menu = NewPresetMenu(keyField);
            var active = ApiKeys.Active(keyVar);
            foreach (var label in ApiKeys.Labels(keyVar))
            {
                var item = PresetItem(label, () =>
                {
                    if (!KeyChange(() => ApiKeys.Select(keyVar, label), $"API key \"{label}\" in use for {keyVar}"))
                        return;
                    (addingKey, keyName.Text) = (false, "");
                    keyInput.Clear();
                    KeyStatus();
                    LoadModels();
                });
                (item.IsChecked, item.Tag) = (label == active, "key:" + label);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
            var add = PresetItem("Add new key…", () =>
            {
                (addingKey, keyName.Text) = (true, "");
                keyInput.Clear();
                KeyStatus();
                keyInput.Focus();
            });
            var rename = PresetItem("Rename…", () =>
            {
                if (active is not null && SidebarAsk("Rename key", $"A new name for the key \"{active}\" saved for {keyVar}.", "Rename", active) is { } name &&
                    KeyChange(() => ApiKeys.Rename(keyVar, active, name), $"API key \"{active}\" renamed \"{name}\" for {keyVar}"))
                    KeyStatus();
            }, active is not null);
            var remove = PresetItem("Remove", () =>
            {
                if (active is null || Choose("Remove key", $"Remove the key \"{active}\" saved for {keyVar}? It cannot be brought back.", ("Remove", "remove")) != "remove" ||
                    !KeyChange(() => ApiKeys.Remove(keyVar, active), $"API key \"{active}\" removed from the app for {keyVar}"))
                    return;
                keyName.Text = ""; // the next name to suggest may have changed
                KeyStatus();
                if (ApiKeys.Has(keyVar))
                    LoadModels(); // another saved key took over
            }, active is not null);
            (add.Tag, rename.Tag, remove.Tag) = ("RemoteKeyAdd", "RemoteKeyRename", "RemoteKeyRemove");
            menu.Items.Add(add);
            menu.Items.Add(rename);
            menu.Items.Add(remove);
            return menu;
        };
        keyMenuButton.Click += (_, _) => _remoteKeyMenu().IsOpen = true;

        // ---- the page
        var title = new TextBlock { Text = "Remote API", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 22, FontWeight = FontWeights.SemiBold };
        var intro = RemoteMuted("Run a worker on an OpenAI-compatible service instead of this computer. Paste the API key below (kept encrypted for your Windows account) or set it as an environment variable.");
        intro.FontSize = 12;
        intro.Margin = new Thickness(0, 4, 0, 0);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 22, 0, 0) };
        divider.SetResourceReference(Border.BackgroundProperty, "Border");
        (searchBox.Width, searchBox.HorizontalAlignment) = (260, HorizontalAlignment.Right);
        DockPanel.SetDock(searchBox, Dock.Right);
        var modelHead = new TextBlock { Text = "Model", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var page = new StackPanel
        {
            MaxWidth = 860, Margin = new Thickness(36, 28, 36, 36),
            Children =
            {
                new DockPanel { Children = { keyHelp, new StackPanel { Children = { title, intro } } } },
                connection,
                keyRow,
                new DockPanel { Margin = new Thickness(0, 18, 0, 0), Children = { searchBox, modelHead } },
                listBox, modelsNote, manualBox, favoritesBox,
                Label("Model id"), RemoteMuted("What the worker's config gets: the model picked or entered above."), new DockPanel { Children = { copyId, modelId } },
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
        RenderFavorites();
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
            Children = { RemoteMuted("Paste the key into the API key box on the Remote API page (kept encrypted for your Windows account), or set the environment variable named there. Where to get one:") },
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

    private const double ModelRow = 31, ModelRowSide = 10; // a model row's height with its 1 px gap; its side padding

    // The model list's columns, shared by its header and rows so they line up: badge, name, context, the row's star.
    private static Grid ModelColumns(FrameworkElement badge, FrameworkElement name, TextBlock context, FrameworkElement? star = null)
    {
        (context.TextAlignment, context.VerticalAlignment, name.VerticalAlignment) = (TextAlignment.Right, VerticalAlignment.Center, VerticalAlignment.Center);
        var g = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(30) }, new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(72) }, new ColumnDefinition { Width = new GridLength(34) } },
        };
        badge.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(name, 1);
        Grid.SetColumn(context, 2);
        g.Children.Add(badge);
        g.Children.Add(name);
        g.Children.Add(context);
        if (star is not null)
        {
            (star.HorizontalAlignment, star.VerticalAlignment) = (HorizontalAlignment.Right, VerticalAlignment.Center);
            Grid.SetColumn(star, 3);
            g.Children.Add(star);
        }
        return g;
    }

    // A model maker's badge: its logo (RemoteVendorIcon) on a neutral tile from the theme, a "-color" logo in its own colours,
    // any other in the theme's text colour; a maker with no logo gets its initials on its colour (RemoteVendorBadge).
    // icon: a logo to use instead of the id's (the Models Hub's, from the model's brand).
    private static Border RemoteBadge(string modelId, string? icon = null)
    {
        icon ??= RemoteVendorIcon(modelId);
        if (icon is not null && VendorIconImage(icon) is { } image)
        {
            FrameworkElement logo = new Image { Source = image };
            if (!icon.EndsWith("-color", StringComparison.Ordinal))
            {
                var shape = new System.Windows.Shapes.Rectangle { OpacityMask = new ImageBrush(image) };
                shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextPrimary");
                logo = shape;
            }
            var tile = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Padding = new Thickness(3), Tag = "icon:" + icon, Child = logo };
            tile.SetResourceReference(Border.BackgroundProperty, "Border");
            RenderOptions.SetBitmapScalingMode(tile, BitmapScalingMode.HighQuality);
            return tile;
        }
        var (initials, rgb) = RemoteVendorBadge(modelId);
        return new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Tag = "badge:" + initials,
            Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
            Child = new TextBlock
            {
                Text = initials, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = initials.Length > 1 ? 9.5 : 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    // A favorite model: "<service id>|<model id>" (no service id holds "|"); null for anything else.
    internal static string RemoteFavorite(string service, string model) => service + "|" + model;
    internal static (string Service, string Model)? RemoteFavoriteParts(string favorite) =>
        favorite.IndexOf('|') is > 0 and var bar && bar < favorite.Length - 1 ? (favorite[..bar], favorite[(bar + 1)..]) : null;

    // Starred or not, once each, in the order starred.
    internal static List<string> RemoteFavoritesWith(List<string> favorites, string favorite, bool on) =>
        on ? favorites.Contains(favorite) ? [.. favorites] : [.. favorites, favorite] : [.. favorites.Where(f => f != favorite)];

    // The saved favorites the page offers: well-formed, once each, on a service the catalog still has other than Custom URL.
    internal static List<string> RemoteFavoritesKnown(IEnumerable<string?>? favorites) =>
        [.. (favorites ?? []).OfType<string>().Where(f => RemoteFavoriteParts(f) is { } p && RemoteCatalog.Services.SkipLast(1).Any(s => s.Id == p.Service)).Distinct()];

    // A sticky header over a scrolling list keeps its columns over the rows: it leaves room for the list's scroll bar.
    private static void KeepClearOfScrollBar(ScrollViewer rows, FrameworkElement head)
    {
        var right = head.Margin.Right;
        rows.ScrollChanged += (_, _) => head.Margin = head.Margin with { Right = right + Math.Max(0, rows.ActualWidth - rows.ViewportWidth) };
    }

    // A model maker's badge, keyed by the id's prefix before "/": its initials on its colour. Others (and ids with no prefix,
    // as most services list them) get grey and the id's first letter. Mid-tone colours under white text read on every theme.
    private static readonly Dictionary<string, (string Text, uint Rgb)> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = ("OA", 0x10806A), ["anthropic"] = ("A", 0xB05A36), ["google"] = ("G", 0x2B6CD4), ["meta-llama"] = ("M", 0x5848C8),
        ["mistralai"] = ("Mi", 0xC4520E), ["deepseek"] = ("DS", 0x1F7A9C), ["qwen"] = ("Q", 0x6A3FC2), ["z-ai"] = ("Z", 0x384B8F),
        ["x-ai"] = ("X", 0x404040), ["nvidia"] = ("NV", 0x4E7A00), ["cohere"] = ("Co", 0xA3407A), ["microsoft"] = ("MS", 0x00749E),
        ["amazon"] = ("Am", 0xA86400), ["perplexity"] = ("Px", 0x1F6F8B), ["moonshotai"] = ("K", 0x3A3A8C), ["minimax"] = ("MM", 0xC2185B),
    };
    private const uint VendorGrey = 0x6B7280;

    internal static (string Text, uint Rgb) RemoteVendorBadge(string modelId) =>
        modelId.IndexOf('/') is > 0 and var slash && Vendors.TryGetValue(modelId[..slash], out var v) ? v
        : (modelId.Length > 0 ? char.ToUpperInvariant(modelId[0]).ToString() : "?", VendorGrey);

    // A model maker's logo, keyed like Vendors by the id's prefix (aliases: the same maker under the names services use):
    // the name of a 64 px PNG in Assets\Vendors (LobeHub icons, MIT; see NOTICE.md there). "accounts" is Fireworks'
    // own ids (accounts/fireworks/models/…).
    private static readonly Dictionary<string, string> VendorIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = "openai", ["anthropic"] = "anthropic", ["google"] = "google-color", ["meta-llama"] = "meta-color", ["meta"] = "meta-color",
        ["mistralai"] = "mistral-color", ["mistral"] = "mistral-color", ["deepseek"] = "deepseek-color", ["deepseek-ai"] = "deepseek-color",
        ["qwen"] = "qwen-color", ["z-ai"] = "zai", ["zai-org"] = "zai", ["thudm"] = "zhipu-color", ["x-ai"] = "xai", ["xai"] = "xai",
        ["nvidia"] = "nvidia-color", ["cohere"] = "cohere-color", ["microsoft"] = "microsoft-color", ["amazon"] = "aws",
        ["perplexity"] = "perplexity-color", ["moonshotai"] = "moonshot", ["minimax"] = "minimax-color", ["nousresearch"] = "nousresearch",
        ["ai21"] = "ai21", ["allenai"] = "ai2-color", ["baidu"] = "baidu-color", ["bytedance"] = "bytedance-color", ["bytedance-seed"] = "bytedance-color",
        ["tencent"] = "tencent-color", ["inflection"] = "inflection", ["liquid"] = "liquid", ["upstage"] = "upstage-color", ["01-ai"] = "zeroone",
        ["stepfun-ai"] = "stepfun-color", ["stepfun"] = "stepfun-color", ["inception"] = "inception", ["relace"] = "relace", ["arcee-ai"] = "arcee-color",
        ["openchat"] = "openchat-color", ["ibm-granite"] = "ibm", ["ibm"] = "ibm", ["alibaba"] = "alibaba-color", ["cognitivecomputations"] = "dolphin",
        ["huggingfaceh4"] = "huggingface-color", ["morph"] = "morph-color", ["deepcogito"] = "deepcogito-color", ["meituan"] = "longcat-color",
        ["xiaomi"] = "xiaomimimo", ["inclusionai"] = "ling-color", ["kwaipilot"] = "kwaipilot", ["essentialai"] = "essentialai-color",
        ["aion-labs"] = "aionlabs-color", ["poolside"] = "poolside-color", ["openrouter"] = "openrouter-color", ["groq"] = "groq",
        ["together"] = "together-color", ["togethercomputer"] = "together-color", ["fireworks"] = "fireworks-color", ["accounts"] = "fireworks-color",
        ["deepinfra"] = "deepinfra-color", ["cerebras"] = "cerebras-color",
    };

    internal static string? RemoteVendorIcon(string modelId) =>
        modelId.IndexOf('/') is > 0 and var slash && VendorIcons.TryGetValue(modelId[..slash], out var icon) ? icon : null;

    // The logo from the app's resources, loaded once (frozen); null if there is no such resource.
    private static readonly Dictionary<string, BitmapImage?> VendorIconImages = [];
    private static BitmapImage? VendorIconImage(string icon)
    {
        if (!VendorIconImages.TryGetValue(icon, out var image))
        {
            try
            {
                image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri($"pack://application:,,,/Assets/Vendors/{icon}.png");
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
            }
            catch (IOException)
            {
                image = null;
            }
            VendorIconImages[icon] = image;
        }
        return image;
    }

    private static TextBlock RemoteMuted(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    // USD per million tokens; "—" unknown, "$0" free, "<$0.0001" any other price the format would round down to $0.00.
    private static string RemotePrice(decimal? perMillion) => perMillion switch
    {
        null => "—",
        0 => "$0",
        < 0.0001m => "<$0.0001",
        { } v => "$" + v.ToString("0.00##", CultureInfo.InvariantCulture),
    };

    // The providers table by a column (1 Provider … 9 Vision, as the header), unknowns last either way, ties as they came;
    // column -1: as they came (RemoteCatalog's cheapest input first).
    internal static List<RemoteCatalog.ProviderRow> SortProviders(IEnumerable<RemoteCatalog.ProviderRow> rows, int column, bool descending)
    {
        if (column < 1)
            return [.. rows];
        IComparable? Key(RemoteCatalog.ProviderRow r) => column switch
        {
            1 => r.Name.Length > 0 ? r.Name : r.Tag,
            2 => r.Quantization is { Length: > 0 } and not "unknown" ? r.Quantization : null,
            3 => r.InputPerM, 4 => r.OutputPerM, 5 => r.CacheReadPerM, 6 => r.LatencySeconds, 7 => r.ThroughputTps, 8 => r.Uptime,
            _ => r.Vision,
        };
        var by = Comparer<IComparable?>.Create((a, b) => a is string x && b is string y ? StringComparer.OrdinalIgnoreCase.Compare(x, y) : System.Collections.Comparer.Default.Compare(a, b));
        var known = rows.OrderBy(r => Key(r) is null);
        return [.. descending ? known.ThenByDescending(Key, by) : known.ThenBy(Key, by)];
    }

    // Agent details' line for a remote role: model @ providers, then the price settings that are set.
    private static string RemoteSummary(LauncherLogic.RemoteRole r) =>
        $"Remote: {r.Model} @ {(r.Provider.Trim() is { Length: > 0 } p ? p : "any provider")}" + (r.SortPrice ? " · cheapest" : "") +
        (LauncherLogic.MaxPrice(r.MaxInput) is { } i ? $" · max {RemotePrice(i)}/M in" : "") + (LauncherLogic.MaxPrice(r.MaxOutput) is { } o ? $" · max {RemotePrice(o)}/M out" : "");

    // A model or provider row priced above a set cap ($/M): lifted comparisons, so an unknown price or an unset cap never hides.
    private static bool RemoteOverCap(decimal? inPerM, decimal? outPerM, decimal? maxIn, decimal? maxOut) => inPerM > maxIn || outPerM > maxOut;

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
        Check(RemotePrice(0.0285m) == "$0.0285" && RemotePrice(0.03m) == "$0.03" && RemotePrice(1.5m) == "$1.50" && RemotePrice(0m) == "$0" &&
            RemotePrice(0.00004m) == "<$0.0001" && RemotePrice(0.00009m) == "<$0.0001" && RemotePrice(0.0001m) == "$0.0001" &&
            RemotePrice(12.345678m) == "$12.3457" && RemotePrice(null) == "—", "price format");
        Check(RemoteContext(262144) == "262K" && RemoteContext(1048576) == "1.05M" && RemoteContext(2_000_000) == "2M" && RemoteContext(8192) == "8K" && RemoteContext(0) == "",
            "context length");
        var plain = new LauncherLogic.RemoteRole("u", "vendor/m", "K", "");
        Check(RemoteSummary(plain) == "Remote: vendor/m @ any provider" && RemoteSummary(plain with { Provider = "a/x" }) == "Remote: vendor/m @ a/x" &&
            RemoteSummary(plain with { SortPrice = true, MaxOutput = "0.3" }) == "Remote: vendor/m @ any provider · cheapest · max $0.30/M out" &&
            RemoteSummary(plain with { MaxInput = "1.5", MaxOutput = "2" }) == "Remote: vendor/m @ any provider · max $1.50/M in · max $2.00/M out", "remote summary");
        Check(RemoteOverCap(1.5m, 9m, 1m, null) && RemoteOverCap(0.1m, 9m, null, 8.99m) && RemoteOverCap(2m, 0.1m, 1m, 1m) && RemoteOverCap(0.1m, 2m, 1m, 1m) &&
            !RemoteOverCap(1m, 1m, 1m, 1m) && !RemoteOverCap(0.5m, 0.5m, 1m, 1m) && !RemoteOverCap(0m, 0m, 0m, 0m) && !RemoteOverCap(null, null, 0m, 0m) &&
            !RemoteOverCap(null, 0.1m, 0m, 1m) && !RemoteOverCap(99m, 99m, null, null) &&
            !RemoteOverCap(99m, 99m, LauncherLogic.MaxPrice("abc"), LauncherLogic.MaxPrice("-1")) && RemoteOverCap(0.31m, 0m, LauncherLogic.MaxPrice(" 0.3 "), null),
            "price cap: above hides, equal or below or unknown or no cap shows");
        Check(RemoteFigure(0.45, "0.00") == "0.45" && RemoteFigure(93.33333, "0.#") == "93.3" && RemoteFigure(85.5, "0") == "86" && RemoteFigure(null, "0") == "—", "figures");
        Check(RemoteVendorBadge("openai/gpt-4o-mini") == ("OA", 0x10806A) && RemoteVendorBadge("z-ai/glm-5.3-flash").Text == "Z" && RemoteVendorBadge("Meta-Llama/llama-4").Text == "M" &&
            RemoteVendorBadge("llama3.1:8b") == ("L", VendorGrey) && RemoteVendorBadge("acme/x") == ("A", VendorGrey) && RemoteVendorBadge("/x").Text == "/" &&
            RemoteVendorBadge("").Text == "?" && Vendors.Values.Select(v => v.Rgb).Append(VendorGrey).Distinct().Count() == Vendors.Count + 1, "vendor badges");
        Check(RemoteVendorIcon("openai/gpt-4o-mini") == "openai" && RemoteVendorIcon("z-ai/glm-5.3-flash") == "zai" && RemoteVendorIcon("zai-org/GLM-4.6") == "zai" &&
            RemoteVendorIcon("Meta-Llama/llama-4") == "meta-color" && RemoteVendorIcon("meta/llama-3.1-8b-instruct") == "meta-color" &&
            RemoteVendorIcon("QWEN/qwen3-coder") == "qwen-color" && RemoteVendorIcon("google/gemma-3-27b-it") == "google-color" &&
            RemoteVendorIcon("llama3.1:8b") is null && RemoteVendorIcon("acme/x") is null && RemoteVendorIcon("/x") is null && RemoteVendorIcon("") is null &&
            Vendors.Keys.All(k => RemoteVendorIcon(k + "/m") is not null), "vendor icons");
        var missing = VendorIcons.Values.Concat(HubLogic.Brands.Select(b => b.IconKey).OfType<string>()).Distinct().Where(i => VendorIconImage(i) is null).ToList();
        Check(missing.Count == 0, "vendor icons not in the app's resources: " + string.Join(", ", missing));
        RemoteCatalog.ProviderRow[] p =
        [
            new("A", "a", "fp8", 1m, 1m, null, null, null, 90, false, 0), new("b", "b", "", null, 2m, null, null, null, null, false, 0),
            new("C", "c", "fp4", 2m, 1m, null, null, null, 90, true, 0), new("D", "d", "unknown", 0.5m, 3m, null, null, null, 99, false, 0),
        ];
        string Sorted(int column, bool descending) => string.Concat(SortProviders(p, column, descending).Select(r => r.Tag));
        Check(Sorted(-1, false) == "abcd" && Sorted(-1, true) == "abcd", "providers: the default order is as they came");
        Check(Sorted(8, false) == "acdb" && Sorted(8, true) == "dacb", "providers by uptime: ties as they came, unknown last both ways");
        Check(Sorted(3, false) == "dacb" && Sorted(3, true) == "cadb", "providers by input price");
        Check(Sorted(2, false) == "cabd" && Sorted(2, true) == "acbd", "providers by quantization: empty and unknown last");
        Check(Sorted(1, false) == "abcd" && Sorted(1, true) == "dcba" && Sorted(4, true) == "dbac" && Sorted(9, true) == "cabd", "providers by name, output, vision");
        Check(RemoteFavorite("openrouter", "z-ai/glm-5.3-flash") == "openrouter|z-ai/glm-5.3-flash" && RemoteFavoriteParts("ollama|llama3.1:8b") == ("ollama", "llama3.1:8b") &&
            RemoteFavoriteParts("openrouter|a|b") == ("openrouter", "a|b") && RemoteFavoriteParts("|x") is null && RemoteFavoriteParts("openrouter|") is null &&
            RemoteFavoriteParts("openrouter") is null, "favorite keys");
        List<string> favs = ["openrouter|a", "openai|b"];
        Check(RemoteFavoritesWith(favs, "groq|c", true).SequenceEqual(["openrouter|a", "openai|b", "groq|c"]) && RemoteFavoritesWith(favs, "openrouter|a", true).SequenceEqual(favs) &&
            RemoteFavoritesWith(favs, "openrouter|a", false).SequenceEqual(["openai|b"]) && RemoteFavoritesWith(favs, "groq|c", false).SequenceEqual(favs) &&
            favs.SequenceEqual(["openrouter|a", "openai|b"]), "favorites add and remove");
        Check(RemoteFavoritesKnown(["openai|b", "gone|x", "custom|m", "openrouter|a", "openai|b", null, "bad"]).SequenceEqual(["openai|b", "openrouter|a"]) &&
            RemoteFavoritesKnown(null).Count == 0 && RemoteCatalog.Services[^1].Id == "custom", "favorites on known services");
    }

    // --ui-smoke: the sidebar's Remote API opens the page (the first time with the key help) in place of Code and Chat,
    // lists the (fake) service's models, filters them, shows a model's providers, writes the ticked order on Apply (to a
    // built-in role, COMPACTOR and a custom worker), refuses bad input and a busy Apply, and New chat leaves it.
    private async Task<bool> RemoteApiSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: remote api: {what}"); return false; }
        void Snap(string name, FrameworkElement? target = null) // AGENT_ORCHESTRATOR_SNAPSHOTS=<folder>, as RunUiSmokeAsync's
        {
            if (Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_SNAPSHOTS") is { Length: > 0 } folder)
                SavePicture(target ?? this, Path.Combine(folder, name + ".png"));
        }
        var (http, open, shown, last) = (_remoteHttp, _openExternal, _settings.RemoteHelpShown, _remoteLast);
        var (favoritesWere, choiceWas) = (_settings.RemoteFavorites, _codeChoice);
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
        string RowTags() => string.Join(", ", Find<StackPanel>("RemoteProviderRows").Children.OfType<Grid>().Select(g => (g.Tag as RemoteCatalog.ProviderRow)?.Tag).OfType<string>());
        static string Said(TextBlock t) => new TextRange(t.ContentStart, t.ContentEnd).Text; // Text leaves out the sort arrow's Run
        string Heads(Grid? head) => string.Join("|", head?.Children.OfType<FrameworkElement>().Select(e => Said(e as TextBlock ?? (TextBlock)((Button)e).Content)) ?? []);
        string OrderText() => Find<TextBlock>("RemoteOrder").Text;
        void Apply() => SmokeClick(Find<Button>("RemoteApply"));
        void Type(string tag, string text) // a keystroke at a time, each raising TextChanged
        {
            var box = Find<TextBox>(tag);
            box.Text = "";
            for (var i = 1; i <= text.Length; i++)
                box.Text = text[..i];
        }
        void KeyEnv(string name) // the box takes effect on Enter or leaving it, not per keystroke
        {
            var box = Find<TextBox>("RemoteKeyEnv");
            box.Text = name;
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        }
        const string KeyA = "AO_SMOKE_KEY_A", KeyB = "AO_SMOKE_KEY_B";
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
                !fake.Seen.Any(r => r.Url == "https://openrouter.ai/api/v1/models"))
                return Fail($"the model list: {string.Join(", ", fake.Seen)}");

            // Service, Base URL, key variable and its status on one line; narrower, on two, with nothing past the page's edge.
            var connection = Find<Grid>("RemoteConnection");
            FrameworkElement[] parts = [Find<ComboBox>("RemoteService"), Find<TextBox>("RemoteBaseUrl"), Find<TextBox>("RemoteKeyEnv"), Find<TextBlock>("RemoteKeyStatus")];
            Rect At(FrameworkElement e) => e.TransformToAncestor(connection).TransformBounds(new Rect(e.RenderSize));
            connection.Width = ConnectionOneLine; // the window's own width varies from run to run
            RemoteApiView.UpdateLayout();
            var line = parts.Select(At).ToArray();
            if (line.Any(r => Math.Abs((r.Top + r.Bottom) / 2 - (line[0].Top + line[0].Bottom) / 2) > 3) || line[3].Left < line[2].Right || line[2].Left < line[1].Right ||
                line[3].Right > ConnectionOneLine + 0.5 || line[1].Width < 150)
                return Fail($"the connection settings are not on one line: {string.Join(" / ", line)}");
            connection.Width = 360;
            RemoteApiView.UpdateLayout();
            line = parts.Select(At).ToArray();
            connection.Width = double.NaN;
            if (line.Any(r => r.Right > 360.5) || line[1].Width < 150 || line[2].Top < line[0].Bottom)
                return Fail($"narrow, the connection settings clip: {string.Join(" / ", line)}");

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
            KeyEnv(Unset);
            await Loaded();
            await Task.Delay(400);
            // an empty box's hint (search, the max prices) starts where its own text (and caret) starts
            string? HintOff(params (string Box, string Hint)[] boxes)
            {
                RemoteApiView.UpdateLayout();
                foreach (var (boxTag, hintText) in boxes)
                {
                    var hinted = Find<TextBox>(boxTag);
                    var hint = SmokeFind<TextBlock>(RemoteApiView, t => t.Text == hintText)!;
                    if (Math.Abs(hint.TranslatePoint(new Point(), hinted).X - hinted.GetRectFromCharacterIndex(0).X) > 1.5)
                        return $"the hint \"{hintText}\" starts at {hint.TranslatePoint(new Point(), hinted).X}, the text at {hinted.GetRectFromCharacterIndex(0).X}";
                }
                return null;
            }
            if (HintOff(("RemoteSearch", "Search models")) is { } searchOff)
                return Fail(searchOff);
            Find<TextBox>("RemoteSearch").Text = "qwen";
            if (Find<StackPanel>("RemoteModels").Children is not { Count: 1 } found || ((RemoteCatalog.ModelEntry)((ToggleButton)found[0]).Tag).Id != "qwen/qwen3-coder" ||
                Find<TextBlock>("RemoteModelsNote").Text != "1 of 3 models")
                return Fail("search did not narrow the models to Qwen3 Coder");
            Find<TextBox>("RemoteSearch").Text = "";
            if (SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: Glm }) is not { } glm)
                return Fail("clearing the search did not bring the models back");
            SmokeClick(glm);
            await Loaded();
            if (Find<TextBox>("RemoteModelId") is not { IsReadOnly: true, Text: Glm } || glm.IsChecked != true || !fake.Seen.Any(r => r.Url == $"{OpenRouter}/models/{Glm}/endpoints") ||
                Find<TextBox>("RemoteModelManual").IsVisible)
                return Fail($"choosing a model: id {Find<TextBox>("RemoteModelId").Text}, asked {string.Join(", ", fake.Seen)}");

            // The model list: a vendor badge per row, a header outside the scrolling list whose columns sit over the rows', 9 rows tall at most.
            RemoteApiView.UpdateLayout();
            var listScroll = Find<ScrollViewer>("RemoteModelsScroll");
            var listHead = Find<Grid>("RemoteModelsHeader");
            Rect In(FrameworkElement e) => e.TransformToAncestor(RemoteApiView).TransformBounds(new Rect(e.RenderSize));
            Rect Cell(FrameworkElement root, string tag) => In(SmokeFind<TextBlock>(root, t => t.Tag as string == tag)!);
            if (!listHead.IsVisible || listHead.IsDescendantOf(listScroll) || listScroll.MaxHeight != 9 * ModelRow ||
                Math.Abs(Cell(listHead, "name").Left - Cell(glm, "name").Left) > 1 || Math.Abs(Cell(listHead, "context").Right - Cell(glm, "context").Right) > 1 ||
                SmokeFind<Border>(glm, b => b.Tag as string == "icon:zai") is not { Child: System.Windows.Shapes.Rectangle } ||
                SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: "qwen/qwen3-coder" }) is not { } qwenRow ||
                SmokeFind<Border>(qwenRow, b => b.Tag as string == "icon:qwen-color") is not { Child: Image } || RemoteBadge("acme/x").Tag as string != "badge:A")
                return Fail($"the model list header: name {Cell(listHead, "name")} / {Cell(glm, "name")}, context {Cell(listHead, "context")} / {Cell(glm, "context")}, or the vendor logos");
            var head = Find<Border>("RemoteProvidersHeader").Child as Grid; // outside the rows' scroll, so it stays put
            string[] wantRows = ["Untagged|—|<$0.0001|$0|—|—|—|—|", "Relace|fp4|$0.0285|$0.0923|$0.0285|—|—|93.3|", "OpenInference|fp4|$0.03|$0.22|$0.02|0.45|86|100|",
                "GMICloud|fp8|$0.06|$0.44|—|—|—|99.5|", "Mystery|—|—|—|—|—|—|—|✓"];
            if (Find<StackPanel>("RemoteProviders").Visibility != Visibility.Visible ||
                Heads(head) != "|Provider|Quant.|In $/M|Out $/M|Cache $/M|Latency|tok/s|Uptime|Vision" ||
                !Rows().SequenceEqual(wantRows))
                return Fail($"the providers table: {string.Join(" / ", Rows())}");
            if (Tick("") is not { IsEnabled: false } || Tick("relace/fp4") is not { IsEnabled: true }) // no tag: nothing to put in the order
                return Fail("the untagged provider's tick is enabled (or a tagged one's is not)");
            Toggle(Find<ToggleButton>("RemoteVisionOnly"));
            if (!Rows().SequenceEqual([wantRows[4]]))
                return Fail($"Vision only left {string.Join(" / ", Rows())}");
            Toggle(Find<ToggleButton>("RemoteVisionOnly"));
            if (Rows().Count != 5)
                return Fail($"Vision only off left {Rows().Count} providers");

            // The rows scroll under the header, 9 in view at most, the header's columns over theirs; 5 providers, 5 rows tall.
            var tableScroll = Find<ScrollViewer>("RemoteProvidersTable");
            var rowsScroll = Find<ScrollViewer>("RemoteProvidersScroll");
            async Task ProvidersOf(string model, int extra)
            {
                fake.ExtraProviders = extra;
                SmokeClick(SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry m && m.Id == model)!);
                await Loaded();
                tableScroll.Width = 900; // the window's own width varies from run to run
                RemoteApiView.UpdateLayout();
                RemoteApiView.UpdateLayout(); // the header's room for the scroll bar follows the first pass
            }
            double X(Grid g, int column) => g.TransformToAncestor(RemoteApiView).Transform(new Point(g.ColumnDefinitions[column].Offset + g.ColumnDefinitions[column].ActualWidth, 0)).X;
            await ProvidersOf("qwen/qwen3-coder", 7);
            var first = Find<StackPanel>("RemoteProviderRows").Children.OfType<Grid>().First();
            head = Find<Border>("RemoteProvidersHeader").Child as Grid;
            if (Rows().Count != 12 || Math.Abs(rowsScroll.ActualHeight - 9 * ProviderRowHeight) > 0.5 || rowsScroll.ScrollableHeight <= 0 || head is null || head.IsDescendantOf(rowsScroll) ||
                Enumerable.Range(0, ProviderColumns.Length).Any(i => Math.Abs(X(head, i) - X(first, i)) > 1))
                return Fail($"12 providers: {Rows().Count} rows, {rowsScroll.ActualHeight} tall, scrolls {rowsScroll.ScrollableHeight}, column ends " +
                    string.Join(" ", Enumerable.Range(0, ProviderColumns.Length).Select(i => $"{X(head!, i):0.#}/{X(first, i):0.#}")));
            await ProvidersOf(Glm, 0);
            if (!Rows().SequenceEqual(wantRows) || Math.Abs(rowsScroll.ActualHeight - 5 * ProviderRowHeight) > 0.5 || rowsScroll.ScrollableHeight != 0)
                return Fail($"5 providers: {rowsScroll.ActualHeight} tall, scrolls {rowsScroll.ScrollableHeight}");
            tableScroll.Width = double.NaN;

            // Ticked providers keep the order ticked (not sorted), on the page and in what Apply writes.
            Toggle(Tick("relace/fp4")!);
            Toggle(Tick("open-inference/fp4")!);
            if (OrderText() != "Only, in this order: Relace (relace/fp4) → OpenInference (open-inference/fp4)" || Tick("relace/fp4")?.Content as string != "1" ||
                Tick("open-inference/fp4")?.Content as string != "2")
                return Fail($"the tick order shows \"{OrderText()}\"");

            // A header click sorts by its column: up, down, back to cheapest input first; unknowns last; ticks and Vision only hold.
            var tickOrder = OrderText();
            Button SortBy(int column) => SmokeFind<Button>(Find<Border>("RemoteProvidersHeader"), b => b.Tag as string == $"sort:{column}")!;
            string Uptime() => Said((TextBlock)SortBy(8).Content);
            SmokeClick(SortBy(8));
            if (RowTags() != "relace/fp4, gmicloud/fp8, open-inference/fp4, , mystery" || Uptime() != "Uptime ▲" || OrderText() != tickOrder ||
                Tick("relace/fp4")?.Content as string != "1" || Tick("open-inference/fp4")?.Content as string != "2" || Tick("") is null)
                return Fail($"sorted by uptime: [{RowTags()}], header {Uptime()}, \"{OrderText()}\"");
            SmokeClick(SortBy(8));
            if (RowTags() != "open-inference/fp4, gmicloud/fp8, relace/fp4, , mystery" || Uptime() != "Uptime ▼" || OrderText() != tickOrder)
                return Fail($"sorted by uptime, down: [{RowTags()}], header {Uptime()}, \"{OrderText()}\"");
            Toggle(Find<ToggleButton>("RemoteVisionOnly"));
            if (!Rows().SequenceEqual([wantRows[4]]))
                return Fail($"sorted, Vision only left {string.Join(" / ", Rows())}");
            Toggle(Find<ToggleButton>("RemoteVisionOnly"));
            SmokeClick(SortBy(8));
            if (!Rows().SequenceEqual(wantRows) || Uptime() != "Uptime" || OrderText() != tickOrder)
                return Fail($"a third click did not bring back cheapest first: {string.Join(" / ", Rows())}");
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

            // Price (OpenRouter): Cheapest and a max price go with the ticked order, and alone (then no allow_fallbacks); ":floor"
            // writes the suffixed id while the Model id box keeps the base id; bad prices write nothing.
            string Provider(string role) => string.Join(" | ", targets.Select(t => JsonNode.Parse(File.ReadAllText(t))!["roles"]![role]!["sampling"]?["extra_body"]?["provider"]?.ToJsonString()).Distinct());
            ToggleButton Cheapest() => Find<ToggleButton>("RemoteSortPrice");
            ToggleButton FloorSwitch() => Find<ToggleButton>("RemoteFloor");
            string FloorNote() => Find<TextBlock>("RemoteFloorNote") is { Visibility: Visibility.Visible } n ? n.Text : "";
            if (Cheapest() is not { IsVisible: true, IsChecked: false } || !Find<TextBox>("RemoteMaxInput").IsVisible || !Find<TextBox>("RemoteMaxOutput").IsVisible ||
                FloorSwitch() is not { IsVisible: true, IsEnabled: true, IsChecked: false } || Find<TextBlock>("RemoteCheapestNow").Text != "Cheapest now: in <$0.0001 / out $0")
                return Fail($"the price controls for OpenRouter, cheapest now \"{Find<TextBlock>("RemoteCheapestNow").Text}\"");
            if (HintOff(("RemoteMaxInput", "Max in $/M"), ("RemoteMaxOutput", "Max out $/M")) is { } maxOff)
                return Fail(maxOff);
            // narrow, the price row wraps: nothing past the providers' edge
            var priceRow = Find<WrapPanel>("RemotePriceRow");
            Find<StackPanel>("RemoteProviders").Width = 360;
            RemoteApiView.UpdateLayout();
            var priceParts = priceRow.Children.OfType<FrameworkElement>().Select(e => e.TransformToAncestor(priceRow).TransformBounds(new Rect(e.RenderSize))).ToArray();
            Find<StackPanel>("RemoteProviders").Width = double.NaN;
            if (priceParts.Any(r => r.Right > 360.5))
                return Fail($"narrow, the price row clips: {string.Join(" / ", priceParts)}");
            Target("reviewer");
            Toggle(Cheapest());
            Type("RemoteMaxOutput", "0.3");
            Apply();
            var priced = ticked with { SortPrice = true, MaxOutput = "0.3" };
            if (Wrote("reviewer", priced) is not null || Provider("reviewer") != """{"order":["relace/fp4","open-inference/fp4"],"allow_fallbacks":false,"sort":"price","max_price":{"completion":0.3}}""")
                return Fail($"Cheapest and max out 0.3 with ticked providers wrote {Provider("reviewer")}");
            SmokeClick(Find<Button>("RemoteClearOrder"));
            Apply();
            if (Wrote("reviewer", priced with { Provider = "" }) is not null || Provider("reviewer") != """{"sort":"price","max_price":{"completion":0.3}}""")
                return Fail($"Cheapest and max out 0.3 wrote {Provider("reviewer")}");
            Toggle(FloorSwitch());
            if (Find<TextBox>("RemoteModelId").Text != Glm || FloorNote() != $"Will be saved as {Glm}:floor")
                return Fail($":floor on: the box shows \"{Find<TextBox>("RemoteModelId").Text}\", the note \"{FloorNote()}\"");
            Apply();
            if (Wrote("reviewer", priced with { Provider = "", Model = Glm + ":floor" }) is { } floored)
                return Fail($":floor wrote {floored}");
            // A preset's ":floor" turns the switch on and leaves the box; an id with a variant of its own takes no second one.
            Reset();
            var preset = new LauncherLogic.RemoteRole(OpenRouter, Glm + ":floor", Unset, "", "", true, "", "0.3");
            OpenRemoteApi("reviewer", preset);
            await Loaded();
            if (Find<TextBox>("RemoteModelId").Text != Glm || FloorSwitch().IsChecked != true || Cheapest().IsChecked != true || Find<TextBox>("RemoteMaxOutput").Text != "0.3" ||
                FloorNote() != $"Will be saved as {Glm}:floor")
                return Fail($"the :floor preset shows \"{Find<TextBox>("RemoteModelId").Text}\", switch {FloorSwitch().IsChecked}, note \"{FloorNote()}\"");
            Find<TextBox>("RemoteModelId").Text = Glm + ":free";
            if (FloorSwitch() is not { IsEnabled: false, IsChecked: false } || FloorNote() != "This id already has a variant: :floor cannot be added.")
                return Fail($"an id with :free: switch enabled {FloorSwitch().IsEnabled}, note \"{FloorNote()}\"");
            Find<TextBox>("RemoteModelId").Text = Glm;
            await Loaded();
            foreach (var bad in new[] { "abc", "-1" })
            {
                Type("RemoteMaxInput", bad);
                Apply();
                if (Find<TextBlock>("RemoteError") is not { Visibility: Visibility.Visible } refused || refused.Text != LauncherLogic.ValidateRemoteRole(preset with { MaxInput = bad }) || !Unchanged())
                    return Fail($"Apply with max in \"{bad}\" was not refused");
            }
            Type("RemoteMaxInput", "");
            Target(worker.Role);
            Apply();
            if (worker.Config.Remote != preset with { Model = Glm } || AppSettings.Load().Workers.FirstOrDefault(w => w.Id == worker.Role)?.Remote != preset with { Model = Glm })
                return Fail($"Apply of the prices to a custom worker: {worker.Config.Remote}");

            // Another model drops the ticked providers: their tags belong to the last model. The prices stay.
            Toggle(Tick("relace/fp4")!);
            Toggle(FloorSwitch());
            SmokeClick(SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: "qwen/qwen3-coder" })!);
            await Loaded();
            if (Find<TextBox>("RemoteModelId").Text != "qwen/qwen3-coder" || OrderText() != "Any provider.")
                return Fail($"another model kept the order \"{OrderText()}\"");
            if (Cheapest().IsChecked != true || Find<TextBox>("RemoteMaxOutput").Text != "0.3" || FloorSwitch().IsChecked != true)
                return Fail("another model dropped the price settings");

            // Another service starts with no model, no providers and no price settings (and shows none).
            Service("openai");
            if (Find<TextBox>("RemoteModelId").Text.Length != 0 || OrderText() != "Any provider.")
                return Fail($"switching the service kept model \"{Find<TextBox>("RemoteModelId").Text}\", order \"{OrderText()}\"");
            if (Cheapest().IsChecked != false || FloorSwitch().IsChecked != false || Find<TextBox>("RemoteMaxOutput").Text.Length != 0 || Cheapest().IsVisible)
                return Fail("switching the service kept (or showed) the price settings");

            // A service that lists models only with a key, without one: the note, no request, and a typed model id applies.
            KeyEnv(Unset);
            await Loaded();
            if (Find<TextBlock>("RemoteModelsNote") is not { Visibility: Visibility.Visible } needsKey ||
                needsKey.Text != "OpenAI lists its models only with an API key: set OPENAI_API_KEY (see Key help), or type the model id below." ||
                fake.Seen.Any(r => r.Url.Contains("api.openai.com", StringComparison.Ordinal)) || Find<TextBox>("RemoteModelManual") is not { IsVisible: true, IsReadOnly: false } || Find<StackPanel>("RemoteModels").IsVisible ||
                Find<StackPanel>("RemoteProviders").Visibility == Visibility.Visible)
                return Fail("OpenAI without a key: no needs-a-key note, or the models were asked for");
            Reset();
            Target("reviewer");
            var typed = new LauncherLogic.RemoteRole("https://api.openai.com/v1", "", Unset, "");
            Apply();
            if (Find<TextBlock>("RemoteError") is not { Visibility: Visibility.Visible } empty || empty.Text != LauncherLogic.ValidateRemoteRole(typed) || !Unchanged())
                return Fail("Apply with no model id was not refused");
            Type("RemoteModelManual", "gpt-4o-mini");
            if (Find<TextBox>("RemoteModelId").Text != "gpt-4o-mini")
                return Fail($"the model id box shows \"{Find<TextBox>("RemoteModelId").Text}\" for a typed gpt-4o-mini");
            Apply();
            if (Wrote("reviewer", typed with { Model = "gpt-4o-mini" }) is { } openai)
                return Fail($"Apply of a typed model id wrote {openai}");
            Reset();
            Service("custom");
            Find<TextBox>("RemoteBaseUrl").Text = "ftp://x";
            Apply();
            if (Find<TextBlock>("RemoteError").Text != LauncherLogic.ValidateRemoteRole(new("ftp://x", "m", Unset, "")) || !Unchanged())
                return Fail($"Apply with a bad base URL: \"{Find<TextBlock>("RemoteError").Text}\"");

            // A preset's provider OpenRouter does not list stays in the order, marked, until Clear.
            OpenRemoteApi("reviewer", new LauncherLogic.RemoteRole(OpenRouter, Glm, Unset, "", "gone/x, relace/fp4"));
            await Loaded();
            if (OrderText() != "Only, in this order: gone/x (not offered for this model) → Relace (relace/fp4)" || Tick("relace/fp4")?.Content as string != "2")
                return Fail($"the preset's order shows \"{OrderText()}\"");
            SmokeClick(Find<Button>("RemoteClearOrder"));
            if (OrderText() != "Any provider." || Tick("relace/fp4")?.IsChecked != false || Find<Button>("RemoteClearOrder").Visibility == Visibility.Visible)
                return Fail($"Clear left \"{OrderText()}\"");

            // A reply that is no list (a duplicate key), or a failure of any kind, replaces both loading notes with the error.
            foreach (var (body, failure, words) in new (string?, Exception?, string[])[]
                { ("{\"data\":[],\"data\":[]}", null, ["not a model list", "not a provider list"]), (null, new ArgumentException("fake failure"), ["fake failure", "fake failure"]) })
            {
                (fake.Body, fake.Failure) = (body, failure);
                OpenRemoteApi("reviewer", new LauncherLogic.RemoteRole(OpenRouter, Glm, Unset, ""));
                try { await Loaded(); }
                catch (Exception e) { return Fail($"a broken reply escaped the page: {e.GetType().Name}: {e.Message}"); }
                var notes = new[] { Find<TextBlock>("RemoteModelsNote"), Find<TextBlock>("RemoteProvidersNote") };
                if (notes.Where((n, i) => n.Visibility != Visibility.Visible || !n.Text.Contains(words[i], StringComparison.Ordinal)).Any())
                    return Fail($"a broken reply left the notes \"{notes[0].Text}\" / \"{notes[1].Text}\"");
            }
            (fake.Body, fake.Failure) = (null, null);

            // The key goes only where the user confirmed: Custom URL starts empty (no address, no key variable), asks nothing
            // while its address is typed, and sends only the variable typed for it once Load models confirms the address.
            Environment.SetEnvironmentVariable(KeyA, "secret-a");
            Environment.SetEnvironmentVariable(KeyB, "secret-b");
            string Asked(int from) => string.Join(", ", fake.Seen.Skip(from));
            OpenRemoteApi("reviewer");
            Service("openrouter");
            KeyEnv(KeyA);
            await Loaded();
            if (!fake.Seen.Contains(($"{OpenRouter}/models", "Bearer secret-a")))
                return Fail($"OpenRouter with {KeyA} asked [{Asked(0)}]");
            var before = fake.Seen.Count;
            Service("custom");
            if (Find<TextBox>("RemoteBaseUrl").Text.Length != 0 || Find<TextBox>("RemoteKeyEnv").Text.Length != 0 || Find<TextBox>("RemoteModelId").Text.Length != 0 ||
                Find<StackPanel>("RemoteModels").Children.Count != 0)
                return Fail($"Custom URL kept \"{Find<TextBox>("RemoteBaseUrl").Text}\", key variable \"{Find<TextBox>("RemoteKeyEnv").Text}\"");
            const string Lan = "http://192.168.1.5:8080/v1";
            Type("RemoteBaseUrl", Lan);
            await Loaded();
            if (fake.Seen.Count != before || Find<TextBlock>("RemoteModelsNote").Text != "Press Enter or Load models to list this server's models." || !Find<TextBox>("RemoteModelManual").IsVisible)
                return Fail($"typing a Custom URL asked [{Asked(before)}], note \"{Find<TextBlock>("RemoteModelsNote").Text}\"");
            Type("RemoteKeyEnv", KeyB);
            KeyEnv(KeyB);
            await Loaded();
            if (fake.Seen.Count != before)
                return Fail($"a key variable for an unconfirmed Custom URL asked [{Asked(before)}]");
            SmokeClick(Find<Button>("RemoteLoadModels"));
            await Loaded();
            if (!fake.Seen.Skip(before).SequenceEqual([($"{Lan}/models", "Bearer secret-b")]))
                return Fail($"Load models asked [{Asked(before)}]");

            // A key pasted into the page: saved encrypted (not in settings) under the name beside it, set for this process,
            // shown as set (by name), sent to the confirmed host with the list request. A second key under another name becomes
            // the active one; the field's menu lists both, switches, renames (a name in use refused) and removes (asked first)
            // them, the last one leaving the variable unset. A name that is not key-like is refused.
            const string KeyC = "AO_SMOKE_KEY_C";
            var keysFile = Path.Combine(AppSettings.DataRoot, "keys.json");
            List<MenuItem> KeyItems() => [.. _remoteKeyMenu().Items.OfType<MenuItem>()];
            string Keys() => string.Join(", ", KeyItems().Where(i => i.Tag is string t && t.StartsWith("key:", StringComparison.Ordinal)).Select(i => (i.IsChecked ? "*" : "") + i.Header));
            void KeyMenu(string tag) => KeyItems().First(i => i.Tag as string == tag).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            string KeyStatus() => Find<TextBlock>("RemoteKeyStatus").Text;
            bool Shown(string tag) => Find<FrameworkElement>(tag).Visibility == Visibility.Visible;
            string KeyState() => $"env \"{Environment.GetEnvironmentVariable(KeyC)}\", keys [{Keys()}], status \"{KeyStatus()}\", name \"{Find<TextBox>("RemoteKeyName").Text}\", error \"{Find<TextBlock>("RemoteError").Text}\"";
            string? answer = null;
            var asked = new List<string>();
            _codeChoice = title => { asked.Add(title); return answer; };
            before = fake.Seen.Count;
            KeyEnv(KeyC);
            await Loaded();
            if (!Shown("RemoteKeyInput") || !Shown("RemoteKeyName") || Find<TextBox>("RemoteKeyName").Text != "Default" || Shown("RemoteKeyMenu") || Shown("RemoteKeyShown"))
                return Fail($"the empty key field: {KeyState()}");
            Find<PasswordBox>("RemoteKeyInput").Password = "secret-c";
            SmokeClick(Find<Button>("RemoteKeySave"));
            await Loaded();
            if (Environment.GetEnvironmentVariable(KeyC) != "secret-c" || !ApiKeys.Has(KeyC) || Find<PasswordBox>("RemoteKeyInput").Password.Length != 0 ||
                Find<TextBlock>("RemoteKeyStatus") is not { Text: "✓ Key set: Default", ToolTip: string savedTip } || !savedTip.StartsWith("A key is saved", StringComparison.Ordinal) ||
                !fake.Seen.Skip(before).Contains(($"{Lan}/models", "Bearer secret-c")) || File.ReadAllText(keysFile).Contains("secret-c", StringComparison.Ordinal))
                return Fail($"a pasted key: {KeyState()}, saved {ApiKeys.Has(KeyC)}, asked [{Asked(before)}]");
            if (Shown("RemoteKeyInput") || Shown("RemoteKeySave") || Shown("RemoteKeyName") || !Shown("RemoteKeyMenu") || Find<TextBlock>("RemoteKeyShown") is not { Text: var shownText } ||
                !shownText.EndsWith("Default", StringComparison.Ordinal) || shownText.Contains("secret", StringComparison.Ordinal) || Keys() != "*Default")
                return Fail($"the field with a saved key: shows \"{Find<TextBlock>("RemoteKeyShown").Text}\", {KeyState()}");
            KeyMenu("RemoteKeyAdd");
            if (!Shown("RemoteKeyInput") || !Shown("RemoteKeySave") || Find<TextBox>("RemoteKeyName").Text != "Key 2")
                return Fail($"Add new key: {KeyState()}");
            Find<TextBox>("RemoteKeyName").Text = "Work";
            Find<PasswordBox>("RemoteKeyInput").Password = "secret-w";
            await Task.Delay(400); // laid out, for the picture
            Snap("remote-keys-add");
            SmokeClick(Find<Button>("RemoteKeySave"));
            await Loaded();
            if (Environment.GetEnvironmentVariable(KeyC) != "secret-w" || Keys() != "Default, *Work" || KeyStatus() != "✓ Key set: Work" || Shown("RemoteKeyInput") ||
                File.ReadAllText(keysFile) is var saved2 && (saved2.Contains("secret-w", StringComparison.Ordinal) || saved2.Contains("secret-c", StringComparison.Ordinal)))
                return Fail($"a second key: {KeyState()}");
            Snap("remote-keys");
            var keysMenu = _remoteKeyMenu();
            keysMenu.IsOpen = true;
            await Task.Delay(400);
            Snap("remote-keys-menu", keysMenu);
            keysMenu.IsOpen = false;
            before = fake.Seen.Count;
            KeyMenu("key:Default");
            await Loaded();
            if (Environment.GetEnvironmentVariable(KeyC) != "secret-c" || Keys() != "*Default, Work" || KeyStatus() != "✓ Key set: Default" ||
                !fake.Seen.Skip(before).Contains(($"{Lan}/models", "Bearer secret-c")))
                return Fail($"switching keys: {KeyState()}, asked [{Asked(before)}]");
            answer = "Personal";
            KeyMenu("RemoteKeyRename");
            if (asked.LastOrDefault() != "Rename key" || Keys() != "*Personal, Work" || KeyStatus() != "✓ Key set: Personal" || Environment.GetEnvironmentVariable(KeyC) != "secret-c")
                return Fail($"renaming a key: {KeyState()}");
            answer = "Work";
            KeyMenu("RemoteKeyRename");
            if (Keys() != "*Personal, Work" || !Shown("RemoteError"))
                return Fail($"renaming a key to a name in use: {KeyState()}");
            answer = null; // Cancel
            KeyMenu("RemoteKeyRemove");
            if (asked.LastOrDefault() != "Remove key" || Keys() != "*Personal, Work" || Environment.GetEnvironmentVariable(KeyC) != "secret-c")
                return Fail($"a cancelled Remove: {KeyState()}");
            answer = "remove";
            KeyMenu("RemoteKeyRemove");
            await Loaded();
            if (Keys() != "*Work" || Environment.GetEnvironmentVariable(KeyC) != "secret-w" || KeyStatus() != "✓ Key set: Work")
                return Fail($"removing the active key: {KeyState()}");
            KeyMenu("RemoteKeyRemove");
            if (ApiKeys.Has(KeyC) || Environment.GetEnvironmentVariable(KeyC) is not null || KeyStatus() != "✗ Key not set — see Key help" ||
                !Shown("RemoteKeyInput") || Shown("RemoteKeyMenu") || Find<TextBox>("RemoteKeyName").Text != "Default")
                return Fail($"Remove left the pasted key: {KeyState()}");
            // A key saved by an older version (one per variable) is the key named Default.
            File.WriteAllText(keysFile, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [KeyC] = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect("secret-l"u8.ToArray(), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)),
            }));
            ApiKeys.ApplyAll();
            KeyEnv(KeyB);
            KeyEnv(KeyC);
            await Loaded();
            if (Environment.GetEnvironmentVariable(KeyC) != "secret-l" || Keys() != "*Default" || KeyStatus() != "✓ Key set: Default")
                return Fail($"an old key file: {KeyState()}");
            KeyMenu("RemoteKeyRemove");
            if (ApiKeys.Has(KeyC) || Environment.GetEnvironmentVariable(KeyC) is not null)
                return Fail($"removing the old key: {KeyState()}");
            _codeChoice = choiceWas;
            KeyEnv("PATH");
            Find<PasswordBox>("RemoteKeyInput").Password = "x";
            SmokeClick(Find<Button>("RemoteKeySave"));
            if (Find<TextBlock>("RemoteError") is not { Visibility: Visibility.Visible } || ApiKeys.Has("PATH"))
                return Fail("a key for a variable named PATH was saved");
            KeyEnv(KeyB);
            await Loaded();

            // A half-typed variable name that names another variable is neither looked up nor sent until it is committed.
            before = fake.Seen.Count;
            Type("RemoteKeyEnv", KeyA); // on its way to AO_SMOKE_KEY_AB, say
            await Loaded();
            if (fake.Seen.Count != before || Find<TextBlock>("RemoteKeyStatus") is not { Text: "✓ Key set", ToolTip: string tip } || tip != $"{KeyB} is set in your environment.")
                return Fail($"a half-typed key variable asked [{Asked(before)}], status \"{Find<TextBlock>("RemoteKeyStatus").Text}\"");
            Find<TextBox>("RemoteKeyEnv").Text = KeyB;
            if (Find<TextBox>("RemoteModelManual").IsVisible) // the server listed its models: picked, not typed
                return Fail("the manual model id box shows beside a model list");
            SmokeClick(SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: Glm })!);
            Apply();
            if (Wrote("reviewer", new(Lan, Glm, KeyB, "")) is { } lan)
                return Fail($"Apply to the Custom URL wrote {lan}");
            Reset();
            if (fake.Seen.Where(r => r.Auth?.Contains("secret-a", StringComparison.Ordinal) == true && !r.Url.StartsWith(OpenRouter + "/", StringComparison.Ordinal)).Select(r => r.Url).FirstOrDefault() is { } leak)
                return Fail($"{KeyA}'s value went to {leak}");

            // Favorites: a star saves the model without choosing it; the Favorites dropdown, under the list and over the model
            // id, shows once one is starred, lists every service's, and switches to the one chosen, a key going only to its own host.
            if (JsonSerializer.Deserialize<AppSettings>("{\"Theme\":\"Graphite\",\"RemoteHelpShown\":true}") is not { RemoteFavorites.Count: 0 })
                return Fail("settings saved before favorites did not load an empty list");
            _settings.RemoteFavorites = [];
            OpenRemoteApi("reviewer");
            Service("openrouter");
            KeyEnv(KeyA);
            await Loaded();
            Button? Star(string favorite) => SmokeFind<Button>(Find<StackPanel>("RemoteModels"), b => b.Tag as string == "fav:" + favorite);
            var favorites = Find<ComboBox>("RemoteFavorites");
            var favoritesBox = (FrameworkElement)favorites.Parent;
            List<string> Listed() => [.. favorites.Items.OfType<ComboBoxItem>().Select(i => string.Join("", SmokeFind<TextBlock>(i, t => t.Parent is StackPanel)?.Text, "/", SmokeFind<Border>(i, b => b.Tag is string t && (t.StartsWith("badge:", StringComparison.Ordinal) || t.StartsWith("icon:", StringComparison.Ordinal)))?.Tag))];
            const string GlmFav = "openrouter|" + Glm, OpenAiFav = "openai|openai/gpt-4o-mini";
            if (favoritesBox.Visibility != Visibility.Collapsed || Star(GlmFav) is not { Content: "☆" } star || Find<TextBox>("RemoteModelId").Text.Length != 0)
                return Fail("before any star: the Favorites dropdown shows, or GLM has no empty star");
            SmokeClick(star);
            var glmRow = SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: Glm })!;
            if (!AppSettings.Load().RemoteFavorites.SequenceEqual([GlmFav]) || star.Content as string != "★" || glmRow.IsChecked == true || Find<TextBox>("RemoteModelId").Text.Length != 0)
                return Fail($"starring GLM: saved [{string.Join(", ", AppSettings.Load().RemoteFavorites)}], star {star.Content}, model id \"{Find<TextBox>("RemoteModelId").Text}\"");
            var modelIdBox = Find<TextBox>("RemoteModelId");
            foreach (var e in new FrameworkElement[] { favorites, modelIdBox, Find<Border>("RemoteModelsBox") })
                e.Width = 500; // the window's own width varies from run to run
            RemoteApiView.UpdateLayout();
            if (favoritesBox.Visibility != Visibility.Visible || !(In(Find<Border>("RemoteModelsBox")).Bottom <= In(favorites).Top && In(favorites).Bottom <= In(modelIdBox).Top) ||
                !Listed().SequenceEqual([$"OpenRouter · {Glm}/icon:zai"]))
                return Fail($"the Favorites dropdown: {favoritesBox.Visibility}, list {In(Find<Border>("RemoteModelsBox"))}, it {In(favorites)}, model id {In(modelIdBox)}, [{string.Join(", ", Listed())}]");
            SmokeClick(star);
            if (AppSettings.Load().RemoteFavorites.Count != 0 || star.Content as string != "☆" || favoritesBox.Visibility != Visibility.Collapsed)
                return Fail("un-starring GLM left it saved or the dropdown shown");
            SmokeClick(star);
            Service("openai");
            KeyEnv(KeyB);
            await Loaded();
            SmokeClick(Star(OpenAiFav)!);
            Service("custom");
            Type("RemoteBaseUrl", Lan);
            SmokeClick(Find<Button>("RemoteLoadModels"));
            await Loaded();
            if (Find<StackPanel>("RemoteModels").Children.Count == 0 || SmokeFind<Button>(Find<StackPanel>("RemoteModels"), b => (b.Tag as string)?.StartsWith("fav:", StringComparison.Ordinal) == true) is not null)
                return Fail("a Custom URL model has a star (or the server listed none)");
            Service("openrouter");
            KeyEnv(KeyA);
            await Loaded();
            if (!Listed().SequenceEqual([$"OpenRouter · {Glm}/icon:zai", "OpenAI · openai/gpt-4o-mini/icon:openai"]) || !AppSettings.Load().RemoteFavorites.SequenceEqual([GlmFav, OpenAiFav]) ||
                Star(GlmFav)?.Content as string != "★" || Star("openrouter|qwen/qwen3-coder")?.Content as string != "☆")
                return Fail($"two favorites listed [{string.Join(", ", Listed())}], or the rebuilt rows' stars do not show them");
            before = fake.Seen.Count;
            favorites.SelectedItem = favorites.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == OpenAiFav);
            await Loaded();
            if ((Find<ComboBox>("RemoteService").SelectedItem as ComboBoxItem)?.Tag is not RemoteCatalog.Service { Id: "openai" } || Find<TextBox>("RemoteModelId").Text != "openai/gpt-4o-mini" ||
                fake.Seen.Skip(before).Any(r => r.Auth?.Contains("secret-a", StringComparison.Ordinal) == true) || favorites.SelectedItem is not ComboBoxItem { Tag: OpenAiFav })
                return Fail($"choosing the OpenAI favorite: service {Find<ComboBox>("RemoteService").Text}, model id \"{Find<TextBox>("RemoteModelId").Text}\", asked [{Asked(before)}]");
            KeyEnv(KeyB);
            await Loaded();
            if (SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: "openai/gpt-4o-mini" }) is not { IsChecked: true } ||
                !fake.Seen.Skip(before).All(r => r.Auth is null || r.Auth != "Bearer secret-a" && r.Url.StartsWith("https://api.openai.com/", StringComparison.Ordinal)))
                return Fail($"the OpenAI favorite's row is not chosen once listed, or a key went astray: [{Asked(before)}]");
            favorites.SelectedItem = favorites.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == GlmFav);
            await Loaded();
            if ((Find<ComboBox>("RemoteService").SelectedItem as ComboBoxItem)?.Tag is not RemoteCatalog.Service { Id: "openrouter" } || Find<TextBox>("RemoteModelId").Text != Glm ||
                !fake.Seen.Any(r => r.Url == $"{OpenRouter}/models/{Glm}/endpoints"))
                return Fail($"choosing the OpenRouter favorite: model id \"{Find<TextBox>("RemoteModelId").Text}\"");

            // The max prices hide the models and the providers priced above them, live; equal, free and unknown prices stay,
            // as do the pick (tagged over cap), a ticked provider (in the order, as over cap) and the search.
            fake.PricedModels = true;
            OpenRemoteApi("reviewer");
            Service("openrouter");
            await Loaded();
            string Listed8() => string.Join(", ", Find<StackPanel>("RemoteModels").Children.OfType<ToggleButton>().Select(t => ((RemoteCatalog.ModelEntry)t.Tag).Id));
            string ModelsNote() => Find<TextBlock>("RemoteModelsNote").Text;
            const string All8 = "acme/cheap, acme/free, acme/mid, acme/pricey, acme/unpriced, openai/gpt-4o-mini, qwen/qwen3-coder, z-ai/glm-5.3-flash";
            string? Capped(string maxIn, string maxOut, string search, string want, string note)
            {
                Type("RemoteMaxInput", maxIn);
                Type("RemoteMaxOutput", maxOut);
                Type("RemoteSearch", search);
                return Listed8() == want && ModelsNote() == note ? null : $"max in \"{maxIn}\", out \"{maxOut}\", search \"{search}\" listed [{Listed8()}], note \"{ModelsNote()}\"";
            }
            if ((Capped("", "", "", All8, "8 models") ??
                Capped("", "10", "", All8.Replace("acme/pricey, ", ""), "7 of 8 models · 1 over your price cap hidden") ??
                Capped("1", "", "", All8.Replace("acme/pricey, ", ""), "7 of 8 models · 1 over your price cap hidden") ??
                Capped("0.5", "", "", All8.Replace("acme/mid, acme/pricey, ", ""), "6 of 8 models · 2 over your price cap hidden") ??
                Capped("1", "2.9", "", All8.Replace("acme/mid, acme/pricey, ", ""), "6 of 8 models · 2 over your price cap hidden") ??
                Capped("1", "2.9", "acme", "acme/cheap, acme/free, acme/unpriced", "3 of 8 models · 2 over your price cap hidden") ??
                Capped("abc", "-1", "", All8, "8 models") ??
                Capped("", "", "", All8, "8 models")) is { } cap)
                return Fail(cap);
            SmokeClick(SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: "acme/pricey" })!);
            await Loaded();
            Type("RemoteMaxOutput", "10");
            var pricey = SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: "acme/pricey" });
            if (pricey is not { IsChecked: true } || !Said(SmokeFind<TextBlock>(pricey, t => t.Tag as string == "name")!).TrimEnd().EndsWith("   over cap", StringComparison.Ordinal) || Listed8() != All8 ||
                Find<StackPanel>("RemoteModels").Children.OfType<ToggleButton>().Count(r => Said(SmokeFind<TextBlock>(r, t => t.Tag as string == "name")!).Contains("over cap")) != 1)
                return Fail($"the pick over the cap: [{Listed8()}], tagged \"{(pricey is null ? "" : Said(SmokeFind<TextBlock>(pricey, t => t.Tag as string == "name")!))}\"");
            SmokeClick(SmokeFind<ToggleButton>(Find<StackPanel>("RemoteModels"), t => t.Tag is RemoteCatalog.ModelEntry { Id: Glm })!);
            await Loaded();
            if (Listed8() != All8.Replace("acme/pricey, ", "") || ModelsNote() != "7 of 8 models · 1 over your price cap hidden")
                return Fail($"another pick left the one over the cap: [{Listed8()}], note \"{ModelsNote()}\"");
            Type("RemoteMaxOutput", "");
            Toggle(Tick("relace/fp4")!);
            Toggle(Tick("gmicloud/fp8")!);
            const string Both = "Only, in this order: Relace (relace/fp4) → GMICloud (gmicloud/fp8)";
            string? Providers(string maxIn, string maxOut, string tags, string order)
            {
                Type("RemoteMaxInput", maxIn);
                Type("RemoteMaxOutput", maxOut);
                var said = Said(Find<TextBlock>("RemoteOrder"));
                return RowTags() == tags && said == order ? null : $"providers at max in \"{maxIn}\", out \"{maxOut}\": [{RowTags()}], order \"{said}\"";
            }
            if ((Providers("", "", ", relace/fp4, open-inference/fp4, gmicloud/fp8, mystery", Both) ??
                Providers("", "0.3", ", relace/fp4, open-inference/fp4, mystery", Both + " (over cap)") ??
                Providers("0.029", "", ", relace/fp4, mystery", Both + " (over cap)") ??
                Providers("0.03", "0.22", ", relace/fp4, open-inference/fp4, mystery", Both + " (over cap)") ??
                Providers("", "", ", relace/fp4, open-inference/fp4, gmicloud/fp8, mystery", Both)) is { } providerCap)
                return Fail(providerCap);
            fake.PricedModels = false;

            if (SmokeFind<Button>(SidebarList, b => b.Tag as string == "+New") is not { } newChat)
                return Fail("no New in the sidebar");
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
            Environment.SetEnvironmentVariable(KeyA, null);
            Environment.SetEnvironmentVariable(KeyB, null);
            if (_cards.Contains(worker))
                RemoveWorker(worker);
            Reset();
            PrefillCards();
            (_remoteHttp, _openExternal, _settings.RemoteHelpShown, _remoteLast, _settings.RemoteFavorites, _codeChoice) = (http, open, shown, last, favoritesWere, choiceWas);
            _settings.Save(); // the stars the smoke saved
        }
    }
}

// Canned OpenRouter replies for --ui-smoke and --screenshots: /models lists three models, /models/<id>/endpoints five
// providers (one with latency and throughput, as with a key; one with an unknown price; one with no tag and a price under $0.0001). Records every address asked
// with the Authorization header it carried (null: none).
internal sealed class RemoteApiFakeHandler : HttpMessageHandler
{
    public List<(string Url, string? Auth)> Seen { get; } = [];
    public string? Body; // when set, the reply to every address
    public Exception? Failure; // when set, thrown for every address
    public int ExtraProviders; // more providers after the five, Extra 1, 2, … ($1/M in, 90% up)
    public bool PricedModels; // /models also lists five priced ones (OpenRouter's "pricing", USD per token)

    private const string Models = "{\"data\":[" +
        "{\"id\":\"z-ai/glm-5.3-flash\",\"name\":\"Z.ai: GLM 5.3 Flash\",\"context_length\":1048576}," +
        "{\"id\":\"qwen/qwen3-coder\",\"name\":\"Qwen: Qwen3 Coder\",\"context_length\":262144}," +
        "{\"id\":\"openai/gpt-4o-mini\",\"name\":\"OpenAI: GPT-4o-mini\",\"context_length\":128000}]}";

    // $/M in / out: cheap 0.1 / 0.2, mid 1 / 3, pricey 5 / 15, free 0 / 0, unpriced variable ("-1").
    private const string Priced =
        ",{\"id\":\"acme/cheap\",\"name\":\"Acme: Cheap\",\"context_length\":131072,\"pricing\":{\"prompt\":\"0.0000001\",\"completion\":\"0.0000002\"}}" +
        ",{\"id\":\"acme/mid\",\"name\":\"Acme: Mid\",\"context_length\":131072,\"pricing\":{\"prompt\":\"0.000001\",\"completion\":\"0.000003\"}}" +
        ",{\"id\":\"acme/pricey\",\"name\":\"Acme: Pricey\",\"context_length\":131072,\"pricing\":{\"prompt\":\"0.000005\",\"completion\":\"0.000015\"}}" +
        ",{\"id\":\"acme/free\",\"name\":\"Acme: Free\",\"context_length\":131072,\"pricing\":{\"prompt\":\"0\",\"completion\":\"0\"}}" +
        ",{\"id\":\"acme/unpriced\",\"name\":\"Acme: Unpriced\",\"context_length\":131072,\"pricing\":{\"prompt\":\"-1\",\"completion\":\"-1\"}}";

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
        "\"uptime_last_30m\":null,\"supports_image_reference\":true,\"latency_last_30m\":null,\"throughput_last_30m\":null}," +
        "{\"provider_name\":\"Untagged\",\"quantization\":\"\",\"context_length\":8192,\"pricing\":{\"prompt\":\"0.00000000004\",\"completion\":\"0\"}," +
        "\"uptime_last_30m\":null,\"supports_image_reference\":false,\"latency_last_30m\":null,\"throughput_last_30m\":null}]}}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (Seen)
            Seen.Add((url, request.Headers.Authorization?.ToString()));
        if (Failure is not null)
            throw Failure;
        var endpoints = Endpoints[..^3] + string.Concat(Enumerable.Range(1, ExtraProviders).Select(i => $",{{\"provider_name\":\"Extra {i}\",\"tag\":\"extra/{i}\",\"quantization\":\"fp8\"," +
            $"\"context_length\":131072,\"pricing\":{{\"prompt\":\"0.000001\",\"completion\":\"0.000002\"}},\"uptime_last_30m\":90}}")) + "]}}";
        var body = Body ?? (url.EndsWith("/endpoints", StringComparison.Ordinal) ? endpoints : url.EndsWith("/models", StringComparison.Ordinal) ? (PricedModels ? Models[..^2] + Priced + "]}" : Models) : null);
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

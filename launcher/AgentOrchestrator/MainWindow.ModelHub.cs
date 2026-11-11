using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fit = AgentOrchestrator.HubLogic.Fit;
using HubKind = AgentOrchestrator.HubLogic.HubKind;
using HubModel = AgentOrchestrator.HubLogic.HubModel;
using HubVariant = AgentOrchestrator.HubLogic.HubVariant;

namespace AgentOrchestrator;

// The Models Hub page (the sidebar's Models Hub, under Remote API), in the centre column in place of Code and Chat: search
// Hugging Face's GGUF models (HubLogic: URLs, paging, fuzzy filter, ranking), sort them, and see one in the detail pane.
// docs/model-hub-findings.md has the facts and the plan.
public partial class MainWindow
{
    // --ui-smoke and --screenshots put a fake handler here (no live network in any test); null: the real one.
    internal static Func<HttpMessageHandler>? HubHttpHandlerFactory;
    internal static double? HubVramGiBOverride, HubRamGiBOverride; // --ui-smoke: the GPU memory and RAM the fit checks assume

    // OnDisk: the repos with a GGUF in the hub cache ("org/name"), the Models folder or the download folder ("name"): the
    // rows' green dot. Plus the downloads finished since.
    private sealed record HubMachine(double? VramGiB, double RamGiB, HashSet<string> OnDisk);

    // The page's one download: its repo and weights, the last progress, and how it ended. Cancelled: back to null.
    private enum HubDlState { Running, Failed, Done }
    private sealed record HubDl(string Repo, string Label, HubProgress? Progress, HubDlState State, string Error = "");
    private HubDl? _hubDl;
    private CancellationTokenSource? _hubDlCts; // OnClosing cancels it; leaving the page or switching rows does not
    private Task _hubDlTask = Task.CompletedTask; // smokes await it
    // Downloads must not follow redirects themselves (HubDownloader reads the hub's redirect) and may take hours.
    private static HttpClient? _hubDlHttp;
    private static HttpClient HubDlHttp => _hubDlHttp ??= NewHubDlHttp();
    private Border _hubStrip = new(), _hubDlSlot = new(), _hubDlNotice = new();
    private TextBlock _hubStripText = new(), _hubDlText = new();
    private Border _hubDlFill = new();
    // Bumped when the GGUFs on disk change (a download finished, the models folder changed): the model pickers rescan.
    internal int _localModelsVersion;

    private const double HubGiB = 1024.0 * 1024 * 1024;
    private const int HubMaxPages = 10, HubEnoughRows = 8;
    private static readonly TimeSpan HubCacheFor = TimeSpan.FromMinutes(5);

    private HttpClient? _hubHttp;
    private HttpClient HubHttp => _hubHttp ??= NewHubHttp();
    private readonly Dictionary<(string Query, string Sort, HubKind Kinds, string Brands), (DateTime At, List<HubModel> Rows, Uri? Next)> _hubPageCache = new();
    private Task<HubMachine> _hubMachine = Task.FromResult(new HubMachine(null, 0, []));
    private CancellationTokenSource? _hubCts;
    private Task _hubTask = Task.CompletedTask; // smokes await it
    private DispatcherTimer? _hubDebounce;
    private (string Query, string Sort, HubKind Kinds, string Brands) _hubKey = ("", "trending", HubKind.Text, "");
    private List<HubModel> _hubAll = [], _hubShown = [];
    private Uri? _hubNext;
    private int _hubPages;
    private bool _hubLoadingMore;
    private HubModel? _hubSelected;
    // Repo id -> its smallest weights (HubLogic.SmallestWeights; null: no GGUF), from the detail and, while Fits my GPU is on,
    // the background fetch (_hubSizeTask, smokes await it); _hubSizeTried: failed fetches, not asked again until reopened.
    private readonly Dictionary<string, long?> _hubSmallest = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hubSizeTried = new(StringComparer.OrdinalIgnoreCase);
    private Task _hubSizeTask = Task.CompletedTask;
    private CancellationTokenSource? _hubSizeFor;
    private TextBox _hubSearch = new();
    private StackPanel _hubList = new(), _hubStatus = new();
    private TextBlock _hubFooter = new(), _hubSortLabel = new(), _hubKindsLabel = new();
    private System.Windows.Shapes.Ellipse _hubBrandDot = new();
    private Border _hubDetail = new();
    private ScrollViewer _hubDetailScroll = new();
    // The selected model's detail: its loads (cancelled when another row is selected), their replies cached by address
    // (null: 404), the weights picked for Download and the variants in fit order.
    private CancellationTokenSource? _hubDetailCts;
    private Task _hubDetailTask = Task.CompletedTask; // smokes await it
    private readonly Dictionary<string, (DateTime At, string? Text)> _hubTextCache = new();
    private HubVariant? _hubVariant;
    private List<HubVariant> _hubVariantList = [];
    private string _hubSha = "";
    private (double? Vram, double? Ram) _hubFitFor;
    private WrapPanel _hubChips = new();
    private Border _hubWeights = new(), _hubReadme = new(), _hubSaveTo = new(), _hubFolderChip = new() { Tag = new TextBlock() };
    private TextBlock _hubFacts = new();
    private Button _hubDownloadButton = new();

    private static HttpClient NewHubHttp()
    {
        var http = new HttpClient(HubHttpHandlerFactory?.Invoke() ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
        return http;
    }

    private static HttpClient NewHubDlHttp()
    {
        var http = new HttpClient(HubHttpHandlerFactory?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
        return http;
    }

    // Where downloads go: the chosen folder (flat: folder\<repo name>\<file>) when it exists or can be created, else the cache.
    private (string Root, HubLayout Layout) HubTarget()
    {
        if (_settings.HubDownloadFolder is { Length: > 0 } folder)
            try
            {
                Directory.CreateDirectory(folder);
                return (folder, HubLayout.Flat);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return (LauncherLogic.HubCache(), HubLayout.HfCache);
    }

    // The Downloaded set's key for a repo: its id in the cache, its folder name (the part after the slash) in a flat folder.
    private static string HubDownloadedKey(string repo, HubLayout layout) => layout == HubLayout.Flat ? repo.Split('/')[^1] : repo;

    private bool HubRowOnDisk(string repo) =>
        _hubMachine.IsCompletedSuccessfully && _hubMachine.Result.OnDisk is var on && (on.Contains(repo) || on.Contains(repo.Split('/')[^1]));

    private HashSet<string> HubOnDisk()
    {
        var on = HubDownloadedAt(LauncherLogic.HubCache(), HubLayout.HfCache);
        foreach (var folder in new[] { _settings.ModelsFolder, _settings.HubDownloadFolder })
            if (folder.Length > 0)
                on.UnionWith(HubDownloadedAt(folder, HubLayout.Flat));
        return on;
    }

    // The rows' green dots from OnDisk.
    private void HubShowOnDisk()
    {
        foreach (var row in HubRows())
            if (SmokeFind<System.Windows.Shapes.Ellipse>(row, e => e.Tag as string == "HubRowOnDisk") is { } dot)
                dot.Visibility = HubRowOnDisk(((HubModel)row.DataContext).Id) ? Visibility.Visible : Visibility.Collapsed;
    }

    // The repos holding a GGUF at the target (as HubDownloadedKey names them).
    private static HashSet<string> HubDownloadedAt(string root, HubLayout layout)
    {
        static bool HasGguf(string dir) => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories).Any();
        try
        {
            return layout == HubLayout.Flat
                ? Directory.EnumerateDirectories(root).Where(HasGguf).Select(d => Path.GetFileName(d)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : Directory.EnumerateDirectories(root, "models--*").Where(d => HasGguf(Path.Combine(d, "snapshots"))).Select(HubCacheRepo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static string HubCacheRepo(string dir) => Path.GetFileName(dir)["models--".Length..].Replace("--", "/");

    // Save to: the folder chosen ("" or the cache itself: the cache), then everything that depends on it.
    private void HubSetFolder(string folder)
    {
        if (folder.Length > 0 && string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(LauncherLogic.HubCache()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            folder = "";
        _settings.HubDownloadFolder = folder;
        _settings.Save();
        var root = HubTarget().Root;
        _hubMachine = _hubMachine.IsCompletedSuccessfully
            ? Task.FromResult(_hubMachine.Result with { OnDisk = HubOnDisk() })
            : HubReadMachineAsync(); // still reading the machine: read it again for this folder
        ((TextBlock)_hubFolderChip.Tag).Text = HubShortPath(root);
        _hubFolderChip.ToolTip = "Downloads go to " + root;
        HubShowOnDisk();
        HubShowSaveTo();
        UpdateHubDownloadButton();
    }

    // "Save to <folder>  Change…  Reset" over the download card; under it, when the model picker would not see the chosen
    // folder, the offer to make it the Models folder. Disabled while a download runs (it keeps the folder it started with).
    private void HubShowSaveTo()
    {
        var root = HubTarget().Root;
        var label = RemoteMuted("Save to");
        label.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(label, Dock.Left);
        var path = new TextBlock { Text = HubShortPath(root), Tag = "HubSaveToPath", ToolTip = root, FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        path.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var change = new Button { Content = "Change…", Tag = "HubSaveToChange", ToolTip = "Choose where to save models", Style = (Style)FindResource("GhostButton"), FontSize = 11.5, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(10, 0, 0, 0) };
        change.Click += (_, _) =>
        {
            if (PickFolder("Choose where to save models", root) is { } picked)
                HubSetFolder(picked);
        };
        DockPanel.SetDock(change, Dock.Right);
        var line = new DockPanel();
        var custom = _settings.HubDownloadFolder;
        if (custom.Length > 0)
        {
            var reset = new Hyperlink(new Run("Reset")) { Tag = "HubSaveToReset", ToolTip = "Save to the Hugging Face cache" };
            reset.SetResourceReference(TextElement.ForegroundProperty, "AccentLight");
            reset.Click += (_, _) => HubSetFolder("");
            var resetText = new TextBlock { Margin = new Thickness(10, 0, 4, 0), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Inlines = { reset } };
            DockPanel.SetDock(resetText, Dock.Right);
            line.Children.Add(resetText);
        }
        foreach (var e in new UIElement[] { change, label, path })
            line.Children.Add(e);
        var panel = new StackPanel { Children = { line } };
        if (custom.Length > 0 && !HubInside(root, _settings.ModelsFolder))
        {
            var where = RemoteMuted($"The model picker looks in {(_settings.ModelsFolder.Length > 0 ? _settings.ModelsFolder : "the Hugging Face cache")}.");
            (where.VerticalAlignment, where.TextWrapping) = (VerticalAlignment.Center, TextWrapping.Wrap);
            var use = new Button { Content = "Use this folder as the Models folder", Tag = "HubUseAsModels", Style = (Style)FindResource("GhostButton"), FontSize = 11.5, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            use.SetResourceReference(BackgroundProperty, "Panel");
            use.Click += (_, _) => HubUseAsModelsFolder(root);
            DockPanel.SetDock(use, Dock.Right);
            panel.Children.Add(new DockPanel { Tag = "HubModelsNotice", Margin = new Thickness(0, 6, 0, 0), Children = { use, where } });
        }
        _hubSaveTo.Child = panel;
        _hubSaveTo.IsEnabled = _hubDl is not { State: HubDlState.Running };
    }

    // folder is models or inside it (full paths, any case)
    private static bool HubInside(string folder, string models)
    {
        static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\') + "\\";
        return models.Length > 0 && Full(folder).StartsWith(Full(models), StringComparison.OrdinalIgnoreCase);
    }

    // The model picker scans the Models folder: point it here (asking before replacing another one).
    private void HubUseAsModelsFolder(string folder)
    {
        if (_settings.ModelsFolder.Length > 0 && Choose("Replace the Models folder?",
                $"The model picker looks in {_settings.ModelsFolder}. Look in {folder} instead?", ("Use " + HubShortPath(folder), "replace")) != "replace")
            return;
        _settings.ModelsFolder = folder;
        _settings.Save();
        _localModelsVersion++;
        ShowModelsFolder();
        HubShowSaveTo();
    }

    private void OpenModelHub()
    {
        CloseRemoteApi();
        CloseModelHub();
        CloseModelSettings();
        if (!HubLogic.Sorts.Any(s => s.Id == _settings.HubSort))
            _settings.HubSort = "trending";
        _hubMachine = HubReadMachineAsync();
        _hubSelected = null;
        _hubSizeTried.Clear();

        // ---- header: title, subtitle, this machine
        var title = new TextBlock { Text = "Model hub", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 22, FontWeight = FontWeights.SemiBold };
        var intro = RemoteMuted("Discover and download models locally.");
        intro.Margin = new Thickness(0, 4, 0, 0);
        var vram = HubChip("VRAM", "…", "GPU memory");
        var ram = HubChip("RAM", "…", "System memory");
        var cpu = HubChip("CPU", Environment.ProcessorCount + " threads", "Logical processors");
        var folder = HubTarget().Root;
        _hubFolderChip = HubChip("Folder", HubShortPath(folder), "Downloads go to " + folder);
        var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 2, 0, 0) };
        foreach (var chip in new[] { vram, ram, cpu, _hubFolderChip })
            chips.Children.Add(chip);
        DockPanel.SetDock(chips, Dock.Right);
        // Fits my GPU: hides the rows whose smallest weights (fetched in the background while on) are larger than the GPU
        var fits = new Button { Tag = "HubFitsGpu", Style = (Style)FindResource("GhostButton"), Height = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(10, 0, 0, 0),
            ToolTip = "Hide models whose smallest weights are larger than this GPU's memory" };
        fits.SetResourceReference(BackgroundProperty, "Card");
        fits.SetResourceReference(ForegroundProperty, "TextPrimary");
        ToolTipService.SetShowOnDisabled(fits, true);
        void FitsLabel() => fits.Content = _settings.HubFitsGpu ? "✓ Fits my GPU" : "Fits my GPU";
        FitsLabel();
        fits.Click += (_, _) =>
        {
            _settings.HubFitsGpu = !_settings.HubFitsGpu;
            _settings.Save();
            FitsLabel();
            HubFillRows();
            HubFetchSizes();
        };
        DockPanel.SetDock(fits, Dock.Right);
        _hubMachine.ContinueWith(t =>
        {
            ((TextBlock)vram.Tag).Text = t.Result.VramGiB is { } v ? $"{v:0} GB" : "n/a";
            ((TextBlock)ram.Tag).Text = $"{t.Result.RamGiB:0} GB";
            if (t.Result.VramGiB is not > 0)
                (fits.IsEnabled, fits.ToolTip) = (false, "GPU memory is unknown");
        }, TaskScheduler.FromCurrentSynchronizationContext());

        // ---- search box (with a clear button) and the sort pill
        _hubSearch = new TextBox { Tag = "HubSearch", Height = 34, Padding = new Thickness(16, 0, 16, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var glass = new TextBlock { Text = "\uE721", FontSize = 12, Margin = new Thickness(13, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        glass.SetResourceReference(StyleProperty, "RailGlyph");
        glass.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var hint = RemoteMuted("Search GGUF models on Hugging Face");
        hint.Tag = "HubSearchHint";
        // at the TextBox's text origin, so the caret sits before the "S": its border, its padding twice (the template's Border and
        // the text view both apply it: 16 here puts the text 35 px in, clear of the glass) and the 2 px the text view keeps
        (hint.IsHitTestVisible, hint.VerticalAlignment, hint.TextWrapping, hint.Margin) = (false, VerticalAlignment.Center, TextWrapping.NoWrap, new Thickness(35, 0, 0, 0));
        var clear = new Button
        {
            Content = "✕", Tag = "HubClear", Style = (Style)FindResource("GhostButton"), Width = 26, Height = 26, Padding = new Thickness(0), FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed, ToolTip = "Clear search",
        };
        clear.Click += (_, _) =>
        {
            _hubSearch.Text = "";
            _hubSearch.Focus();
        };
        _hubDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hubDebounce.Tick += (_, _) => HubSearch();
        _hubSearch.TextChanged += (_, _) =>
        {
            var empty = _hubSearch.Text.Length == 0;
            hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            _hubDebounce.Stop();
            _hubDebounce.Start();
        };
        Button Pill(string tag, object content, Func<UIElement, ContextMenu> menu)
        {
            var b = new Button { Tag = tag, Style = (Style)FindResource("GhostButton"), Height = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(10, 0, 0, 0), Content = content };
            b.SetResourceReference(BackgroundProperty, "Card");
            b.SetResourceReference(ForegroundProperty, "TextPrimary");
            b.Click += (_, _) => menu(b).IsOpen = true;
            DockPanel.SetDock(b, Dock.Right);
            return b;
        }
        _hubSortLabel = new TextBlock { Tag = "HubSortLabel", VerticalAlignment = VerticalAlignment.Center };
        var sortButton = Pill("HubSort", _hubSortLabel, BuildHubSortMenu);
        HubSortLabel();
        _hubKindsLabel = new TextBlock { Tag = "HubKindsLabel", VerticalAlignment = VerticalAlignment.Center };
        var kindsButton = Pill("HubKinds", _hubKindsLabel, BuildHubKindsMenu);
        var funnel = new TextBlock { Text = "", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        funnel.SetResourceReference(StyleProperty, "RailGlyph");
        _hubBrandDot = new System.Windows.Shapes.Ellipse { Tag = "HubBrandDot", Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -6, 0) };
        _hubBrandDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        var brandButton = Pill("HubBrands", new Grid { Children = { funnel, _hubBrandDot } }, BuildHubBrandMenu);
        (brandButton.Width, brandButton.Padding, brandButton.ToolTip) = (34, new Thickness(0), "Filter by model maker");
        HubFilterLabels();
        var searchRow = new DockPanel { Margin = new Thickness(0, 20, 0, 0), Children = { sortButton, kindsButton, brandButton, fits, new Grid { Children = { _hubSearch, glass, hint, clear } } } };

        // ---- the running download, whichever row is shown
        _hubStripText = new TextBlock { Tag = "HubStripText", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _hubStripText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var stripCancel = new Button { Content = "Cancel", Tag = "HubStripCancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 2, 12, 2), FontSize = 11.5 };
        stripCancel.Click += (_, _) => _hubDlCts?.Cancel();
        DockPanel.SetDock(stripCancel, Dock.Right);
        _hubStrip = new Border
        {
            Tag = "HubStrip", Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(14, 3, 4, 3), CornerRadius = new CornerRadius(10), Visibility = Visibility.Collapsed,
            Child = new DockPanel { Children = { stripCancel, _hubStripText } },
        };
        _hubStrip.SetResourceReference(Border.BackgroundProperty, "Card");
        var top = new StackPanel { Children = { _hubStrip, searchRow } };

        // ---- results (left) and detail (right)
        _hubStatus = new StackPanel { Tag = "HubStatus", Margin = new Thickness(10, 8, 10, 8), Visibility = Visibility.Collapsed };
        _hubList = new StackPanel { Tag = "HubRows" };
        _hubFooter = RemoteMuted("Loading more…");
        (_hubFooter.Margin, _hubFooter.Visibility) = (new Thickness(12, 6, 12, 8), Visibility.Collapsed);
        var scroll = new ScrollViewer
        {
            Tag = "HubScroll", Content = new StackPanel { Children = { _hubStatus, _hubList, _hubFooter } },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        scroll.ScrollChanged += (_, _) =>
        {
            if (scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 300)
                HubLoadMore();
        };
        var results = HubCard(scroll, new Thickness(4));
        _hubDetail = HubCard(HubEmptyDetail(), new Thickness(22, 20, 22, 22));
        _hubDetail.Tag = "HubDetailHost";
        _hubDetail.VerticalAlignment = VerticalAlignment.Top;
        _hubDetailScroll = new ScrollViewer { Content = _hubDetail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new Grid
        {
            Margin = new Thickness(0, 16, 0, 0),
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 300 }, new ColumnDefinition { Width = new GridLength(16) }, new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star) } },
        };
        Grid.SetColumn(_hubDetailScroll, 2);
        body.Children.Add(results);
        body.Children.Add(_hubDetailScroll);

        var page = new Grid
        {
            Tag = "ModelHubPage", MaxWidth = 1400, Margin = new Thickness(36, 28, 36, 28),
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition() },
        };
        var header = new DockPanel { Children = { chips, new StackPanel { Children = { title, intro } } } };
        Grid.SetRow(top, 1);
        Grid.SetRow(body, 2);
        page.Children.Add(header);
        page.Children.Add(top);
        page.Children.Add(body);
        page.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.Enter && HubKey(e.Key, e.OriginalSource == _hubSearch))
                e.Handled = true;
        };
        ModelHubView.Children.Clear();
        ModelHubView.Children.Add(page);
        Reveal(CodeView, false);
        Reveal(ChatView, false);
        Reveal(ModelHubView, true);
        HubDlShowProgress();
        HubSearch();
    }

    private void CloseModelHub()
    {
        _hubCts?.Cancel();
        _hubDetailCts?.Cancel();
        _hubDebounce?.Stop();
        Reveal(ModelHubView, false);
    }

    // VRAM (the override, else the first GPU's, read once per run off the UI thread), RAM, the repos on disk.
    private async Task<HubMachine> HubReadMachineAsync()
    {
        double? vram = HubVramGiBOverride;
        if (vram is null && await (_gpuMemory ??= Task.Run(LauncherLogic.GpuMemoryBytes)) is { } bytes and > 0)
            vram = bytes / HubGiB;
        var ram = HubRamGiBOverride ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / HubGiB;
        return new HubMachine(vram, ram, await Task.Run(HubOnDisk));
    }

    // "C:\Users\me\.cache\huggingface\hub" -> "…\huggingface\hub"
    private static string HubShortPath(string path)
    {
        var parts = path.TrimEnd('\\', '/').Split('\\', '/');
        return parts.Length <= 3 ? path : "…\\" + string.Join("\\", parts[^2..]);
    }

    // A header chip: muted label, the value (its Tag, for updating), a tooltip.
    private static Border HubChip(string label, string value, string tip)
    {
        var text = new TextBlock { Text = value, Tag = "HubChip:" + label, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var name = new TextBlock { Text = label + "  ", FontSize = 11.5 };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var chip = new Border
        {
            Tag = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 6), CornerRadius = new CornerRadius(12), ToolTip = tip,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, text } },
        };
        chip.SetResourceReference(Border.BackgroundProperty, "Card");
        return chip;
    }

    private static Border HubCard(UIElement child, Thickness padding)
    {
        var card = new Border { Padding = padding, BorderThickness = new Thickness(1), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        card.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        return card;
    }

    // ---- sort menu

    private void HubSortLabel() =>
        _hubSortLabel.Text = $"↑↓  {HubLogic.Sorts.First(s => s.Id == _settings.HubSort).Label}  ▾";

    // The sorts (the current one checked). --ui-smoke clicks its items without opening it.
    private ContextMenu BuildHubSortMenu(UIElement anchor)
    {
        var menu = NewPresetMenu(anchor);
        foreach (var sort in HubLogic.Sorts)
        {
            var item = PresetItem(sort.Label, () =>
            {
                _settings.HubSort = sort.Id;
                _settings.Save();
                HubSortLabel();
                HubSearch();
            });
            (item.IsChecked, item.Tag) = (sort.Id == _settings.HubSort, "hubsort:" + sort.Id);
            menu.Items.Add(item);
        }
        return menu;
    }

    // ---- model type and model maker filters

    private const HubKind HubAllKinds = HubKind.Text | HubKind.Audio | HubKind.Image | HubKind.Video;
    private static readonly (HubKind Kind, string Label, string Short)[] HubKindNames =
        [(HubKind.Text, "Text & vision", "Text"), (HubKind.Audio, "Audio", "Audio"), (HubKind.Image, "Image generation", "Image"), (HubKind.Video, "Video", "Video")];
    private const double HubMenuRow = 32; // a filter menu's row; PinnedHeaderMenu's list shows 9 (MaxHeight 288)

    private HubKind HubKinds => (HubKind)_settings.HubKinds & HubAllKinds;

    // The brand filter's keys for these types: their brands, then "other".
    private static List<string> HubBrandKeys(HubKind kinds) => [.. HubLogic.BrandsFor(kinds).Select(b => b.Key), "other"];

    private void HubFilterLabels()
    {
        var on = HubKindNames.Where(k => HubKinds.HasFlag(k.Kind)).ToList();
        _hubKindsLabel.Text = (on.Count switch { 0 => "No types", 1 => on[0].Label, 2 => on[0].Short + ", " + on[1].Short, 4 => "All types", _ => on.Count + " types" }) + "  ▾";
        _hubBrandDot.Visibility = _settings.HubBrands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Brands the types no longer list are dropped (kept while no type is picked); saved, relabelled, searched again.
    private void HubFiltersChanged()
    {
        if (HubKinds != 0)
            _settings.HubBrands = [.. _settings.HubBrands.Intersect(HubBrandKeys(HubKinds))];
        _settings.Save();
        HubFilterLabels();
        HubSearch();
    }

    // "Select All" (or "Deselect All" when every entry is on) and a line, pinned (PinnedHeaderMenu: the menu's Tag), then a
    // checkable item per entry, 9 showing (Tag <tag>:<key>, <tag>:all for the first). Toggling keeps the menu open and saves
    // the keys that are on.
    private ContextMenu HubCheckMenu(UIElement anchor, string tag, List<(string Key, object Header)> entries, HashSet<string> on, Action<HashSet<string>> save)
    {
        var menu = NewPresetMenu(anchor);
        menu.Style = (Style)FindResource("PinnedHeaderMenu");
        MenuItem Item(object header, string key) => new() { Header = header, Tag = tag + ":" + key, Style = (Style)FindResource(typeof(MenuItem)), StaysOpenOnClick = true, Height = HubMenuRow };
        var all = Item("", "all");
        var items = entries.Select(e => (e.Key, Item: Item(e.Header, e.Key))).ToList();
        void Sync()
        {
            foreach (var (key, item) in items)
                item.IsChecked = on.Contains(key);
            all.Header = items.All(i => i.Item.IsChecked) ? "Deselect All" : "Select All";
        }
        void Toggled()
        {
            save(on);
            Sync();
        }
        all.Click += (_, _) =>
        {
            if (items.All(i => i.Item.IsChecked))
                on.Clear();
            else
                on.UnionWith(items.Select(i => i.Key));
            Toggled();
        };
        foreach (var (key, item) in items)
            item.Click += (_, _) =>
            {
                if (!on.Remove(key))
                    on.Add(key);
                Toggled();
            };
        Sync();
        menu.Tag = new StackPanel { Children = { all, new Separator { Style = (Style)FindResource("MenuSeparator") } } };
        foreach (var (_, item) in items)
            menu.Items.Add(item);
        return menu;
    }

    private ContextMenu BuildHubKindsMenu(UIElement anchor) =>
        HubCheckMenu(anchor, "hubkind", [.. HubKindNames.Select(k => (k.Kind.ToString(), (object)k.Label))],
            [.. HubKindNames.Where(k => HubKinds.HasFlag(k.Kind)).Select(k => k.Kind.ToString())],
            on =>
            {
                _settings.HubKinds = (int)HubKindNames.Where(k => on.Contains(k.Kind.ToString())).Aggregate((HubKind)0, (a, k) => a | k.Kind);
                HubFiltersChanged();
            });

    // The brands of the picked types (logo and name) and Other.
    private ContextMenu BuildHubBrandMenu(UIElement anchor)
    {
        static object Header(Border badge, string label)
        {
            badge.Margin = new Thickness(0, 0, 10, 0);
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { badge, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
        }
        var entries = HubLogic.BrandsFor(HubKinds).Select(b => (b.Key, Header(RemoteBadge(b.Label, b.IconKey), b.Label))).Append(("other", Header(RemoteBadge("?"), "Other"))).ToList();
        return HubCheckMenu(anchor, "hubbrand", entries, [.. _settings.HubBrands], on =>
        {
            _settings.HubBrands = [.. on];
            HubFiltersChanged();
        });
    }

    // ---- search, paging, cache

    private void HubSearch(bool fresh = false)
    {
        _hubDebounce?.Stop();
        _hubCts?.Cancel();
        var cts = _hubCts = new CancellationTokenSource();
        _hubTask = HubSearchAsync((_hubSearch.Text.Trim(), _settings.HubSort, HubKinds, string.Join(",", _settings.HubBrands.Order(StringComparer.Ordinal))), fresh, cts.Token);
    }

    private static HashSet<string> HubBrandSet(string brands) => [.. brands.Split(',', StringSplitOptions.RemoveEmptyEntries)];

    // The rows to show: matching the query, ranked, of the picked types and makers.
    private static List<HubModel> HubVisible(IEnumerable<HubModel> all, (string Query, string Sort, HubKind Kinds, string Brands) key) =>
        HubLogic.FilterBrands(HubLogic.FilterKinds(HubLogic.Rank(HubLogic.FuzzyFilter(all, key.Query), key.Query, key.Sort, DateTimeOffset.UtcNow), key.Kinds), HubBrandSet(key.Brands));

    // The first page: each query variant in turn (QueryVariants) until enough rows show; with no query and makers picked, each
    // maker's word (BrandQueries; Other: the plain listing). Text is asked for without a pipeline_tag (FilterKinds keeps it),
    // the other types with one request per tag. "latest" also asks for the trending ones (Rank re-orders the union). Paging
    // follows the first request's Link header. Cached per (query, sort, types, makers) for 5 minutes. No type: nothing asked.
    private async Task HubSearchAsync((string Query, string Sort, HubKind Kinds, string Brands) key, bool fresh, CancellationToken ct)
    {
        var (query, sort) = (key.Query, key.Sort);
        _hubKey = key;
        _hubNext = null;
        _hubFooter.Visibility = Visibility.Collapsed;
        if (key.Kinds == 0)
        {
            (_hubAll, _hubShown) = ([], []);
            _hubList.Children.Clear();
            HubStatus("Pick at least one model type");
            return;
        }
        try
        {
            if (fresh || !_hubPageCache.TryGetValue(_hubKey, out var hit) || DateTime.UtcNow - hit.At > HubCacheFor)
            {
                HubStatus("Searching Hugging Face…");
                _hubList.Children.Clear();
                var rows = new List<HubModel>();
                Uri? next = null;
                var first = true;
                string[] sorts = sort == "latest" ? ["latest", "trending"] : [sort];
                string?[] tags = [.. key.Kinds.HasFlag(HubKind.Text) ? [null] : Array.Empty<string?>(), .. HubLogic.PipelineTagsFor(key.Kinds & ~HubKind.Text)];
                var brands = HubBrandSet(key.Brands);
                var picked = HubLogic.Brands.Where(b => brands.Contains(b.Key)).ToList();
                List<string> words = query.Length > 0 ? [.. HubLogic.QueryVariants(query)]
                    : picked.Count == 0 || HubBrandKeys(key.Kinds).All(brands.Contains) ? [""]
                    : [.. HubLogic.BrandQueries(picked), .. brands.Contains("other") ? [""] : Array.Empty<string>()];
                foreach (var word in words)
                {
                    foreach (var s in sorts)
                        foreach (var tag in tags)
                        {
                            var (page, link) = await HubFetchAsync(HubLogic.SearchUrl(word, s, pipelineTag: tag), ct);
                            if (first)
                                (next, first) = (link, false);
                            HubMerge(rows, page);
                        }
                    var snapshot = rows.ToList();
                    if (query.Length > 0 && await Task.Run(() => HubVisible(snapshot, key).Count, ct) >= HubEnoughRows)
                        break;
                }
                hit = _hubPageCache[_hubKey] = (DateTime.UtcNow, rows, next);
            }
            (_hubAll, _hubNext, _hubPages) = (hit.Rows.ToList(), hit.Next, 1);
            await HubRenderAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException)
        {
            HubError(e);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { } // a newer search took over
    }

    private void HubLoadMore()
    {
        if (_hubNext is not { } next || _hubLoadingMore || _hubPages >= HubMaxPages || _hubCts is not { } cts || cts.IsCancellationRequested)
            return;
        _hubTask = HubLoadMoreAsync(next, cts.Token);
    }

    private async Task HubLoadMoreAsync(Uri url, CancellationToken ct)
    {
        _hubLoadingMore = true;
        _hubNext = null;
        _hubFooter.Visibility = Visibility.Visible;
        try
        {
            var (page, next) = await HubFetchAsync(url, ct);
            _hubPages++;
            _hubNext = _hubPages < HubMaxPages ? next : null;
            HubMerge(_hubAll, page);
            await HubRenderAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException)
        {
            HubError(e);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _hubLoadingMore = false;
            if (!ct.IsCancellationRequested)
                _hubFooter.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<(List<HubModel> Models, Uri? Next)> HubFetchAsync(Uri url, CancellationToken ct)
    {
        using var response = await HubHttp.GetAsync(url, ct);
        response.EnsureSuccessStatusCode(); // 429 and the rest: HttpRequestException with its StatusCode
        var json = await response.Content.ReadAsStringAsync(ct);
        var link = response.Headers.TryGetValues("Link", out var values) ? string.Join(", ", values) : null;
        var models = await Task.Run(() => HubLogic.ParseModels(json), ct);
        ct.ThrowIfCancellationRequested();
        return (models, HubLogic.NextPage(link));
    }

    private static void HubMerge(List<HubModel> into, IEnumerable<HubModel> page)
    {
        var seen = into.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        into.AddRange(page.Where(m => seen.Add(m.Id)));
    }

    private async Task HubRenderAsync(CancellationToken ct)
    {
        var (all, key) = (_hubAll.ToList(), _hubKey);
        var shown = await Task.Run(() => HubVisible(all, key), ct);
        await _hubMachine; // the fit filter and the dots need it
        ct.ThrowIfCancellationRequested();
        _hubShown = shown;
        HubFillRows();
        HubFetchSizes();
    }

    // Fits my GPU on (and the VRAM known): fetch the shown repos' sizes, unless this search's fetch already runs (it picks up
    // the rows shown since). Off, nothing is fetched: up to 100 repos a page would invite Hugging Face's 429s.
    private void HubFetchSizes()
    {
        if (!_settings.HubFitsGpu || !_hubMachine.IsCompletedSuccessfully || _hubMachine.Result.VramGiB is not > 0 || _hubCts is not { IsCancellationRequested: false } cts ||
            !_hubSizeTask.IsCompleted && _hubSizeFor == cts)
            return;
        _hubSizeFor = cts;
        _hubSizeTask = HubFetchSizesAsync(cts.Token);
    }

    // 8 repos at a time (the 5-minute text cache serves the detail too), the rows refilled after each batch; failures stay shown.
    private async Task HubFetchSizesAsync(CancellationToken ct)
    {
        while (_settings.HubFitsGpu && !ct.IsCancellationRequested &&
               _hubShown.Where(m => !_hubSmallest.ContainsKey(m.Id) && !_hubSizeTried.Contains(m.Id)).Take(8).ToList() is { Count: > 0 } batch)
        {
            await Task.WhenAll(batch.Select(async m =>
            {
                try
                {
                    var json = await HubGetTextAsync(HubBlobsUrl(m.Id), TimeSpan.FromSeconds(15), ct)
                        ?? throw new HttpRequestException("Not found", null, HttpStatusCode.NotFound);
                    _hubSmallest[m.Id] = HubLogic.SmallestWeights(await Task.Run(() => HubLogic.ParseVariants(json), ct));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { _hubSizeTried.Add(m.Id); }
            }));
            if (!ct.IsCancellationRequested)
                HubFillRows();
        }
    }

    private static string HubBlobsUrl(string id) => $"https://huggingface.co/api/models/{id}?blobs=true";

    // The rows of _hubShown less the repos known to have no GGUF and, with Fits my GPU on, those larger than the GPU.
    // ponytail: rebuilds every row on each page (up to 10 x 100); a virtualizing list if that ever drags.
    private void HubFillRows()
    {
        var machine = _hubMachine.IsCompletedSuccessfully ? _hubMachine.Result : null;
        var rows = HubLogic.FilterFetched(_hubShown, _hubSmallest, _settings.HubFitsGpu, machine?.VramGiB);
        _hubList.Children.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var m in rows)
            _hubList.Children.Add(HubRow(m, now));
        var query = _hubKey.Query;
        if (rows.Count == 0 && _hubShown.Count == 0 && (_hubKey.Kinds != HubKind.Text || _hubKey.Brands.Length > 0))
            HubStatus("No models match these filters", clearFilters: true);
        else
            HubStatus(rows.Count > 0 ? "" : (query.Length == 0 ? "No GGUF models found" : $"No GGUF models match “{query}”")
                + (rows.Count == 0 && _hubShown.Count > 0 && _settings.HubFitsGpu ? " that fit this GPU" : ""));
        // a short list does not scroll: keep loading until it fills (or the pages run out)
        Dispatcher.BeginInvoke(HubLoadMore, DispatcherPriority.Background);
    }

    // clearFilters: a "Clear filters" link after it (back to Text & vision, every maker).
    private void HubStatus(string text, bool clearFilters = false)
    {
        _hubStatus.Children.Clear();
        _hubStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (text.Length == 0)
            return;
        var line = RemoteMuted(text);
        if (clearFilters)
        {
            var link = new Hyperlink(new Run("Clear filters")) { Tag = "HubClearFilters" };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentLight");
            link.Click += (_, _) =>
            {
                (_settings.HubKinds, _settings.HubBrands) = ((int)HubKind.Text, []);
                HubFiltersChanged();
            };
            line.Inlines.Add(new Run("   "));
            line.Inlines.Add(link);
        }
        _hubStatus.Children.Add(line);
    }

    private void HubError(Exception e)
    {
        _hubStatus.Children.Clear();
        _hubStatus.Children.Add(HubNotice(HubErrorText(e), "Hub", () => HubSearch(fresh: true)));
        _hubStatus.Visibility = Visibility.Visible;
    }

    private static string HubErrorText(Exception e, int timeoutSeconds = 15) => e switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Hugging Face is rate limiting; wait a moment, then retry.",
        HttpRequestException { StatusCode: { } code } => $"Hugging Face answered {(int)code} ({code}).",
        OperationCanceledException => $"Hugging Face did not answer within {timeoutSeconds} seconds.",
        JsonException => "Hugging Face sent a reply that could not be read.",
        _ => "Could not reach Hugging Face: " + e.Message,
    };

    // A red message with Retry on its right; tagged <tag>Error and <tag>Retry.
    private DockPanel HubNotice(string text, string tag, Action retry)
    {
        var message = new TextBlock { Text = text, Tag = tag + "Error", FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A)) };
        var button = new Button { Content = "Retry", Tag = tag + "Retry", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(10, 0, 0, 0) };
        button.Click += (_, _) => retry();
        DockPanel.SetDock(button, Dock.Right);
        return new DockPanel { Children = { button, message } };
    }

    // ---- rows

    private static readonly uint[] HubTileColors = [0x2B6CD4, 0x10806A, 0xB05A36, 0x6A3FC2, 0xC4520E, 0x1F7A9C, 0xA3407A, 0x4E7A00, 0xC2185B, 0x384B8F];

    // FNV-1a over the lower-cased owner: the same owner gets the same colour on every run.
    internal static uint HubOwnerColor(string owner)
    {
        var h = 2166136261u;
        foreach (var c in owner.ToLowerInvariant())
            h = (h ^ c) * 16777619u;
        return HubTileColors[h % (uint)HubTileColors.Length];
    }

    // The maker's logo (RemoteBadge): the brand named in the repo name (HubLogic.BrandOf), else the owner's; with neither, the
    // owner's initial on a colour from its name.
    private static FrameworkElement HubBadge(HubModel m)
    {
        if ((HubLogic.BrandOf(m.Id)?.IconKey ?? RemoteVendorIcon(m.Id)) is { } icon && VendorIconImage(icon) is not null)
            return RemoteBadge(m.Id, icon);
        var initial = m.Owner.Length > 0 ? char.ToUpperInvariant(m.Owner[0]).ToString() : "?";
        var rgb = HubOwnerColor(m.Owner);
        return new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Tag = "hubtile:" + initial,
            Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
            Child = new TextBlock { Text = initial, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    private static string HubStats(HubModel m) => $"♡ {HubLogic.FormatCount(m.Likes)}   ↓ {HubLogic.FormatCount(m.Downloads)}";

    // Badge, name over owner, likes and downloads over the age. Checked: the keyboard's row (Up/Down), and the one shown.
    private ToggleButton HubRow(HubModel m, DateTimeOffset now)
    {
        TextBlock Line(string text, bool strong = false)
        {
            var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = strong ? 13 : 11.5 };
            if (strong)
                t.FontWeight = FontWeights.SemiBold;
            else
                t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }
        // the logo, with a green dot on its top-left corner when the repo is on disk
        var dot = new System.Windows.Shapes.Ellipse
        {
            Tag = "HubRowOnDisk", Width = 7, Height = 7, ToolTip = "Downloaded", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-2, -2, 0, 0), Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xBF, 0x7F)), Visibility = HubRowOnDisk(m.Id) ? Visibility.Visible : Visibility.Collapsed,
        };
        var badge = new Grid { VerticalAlignment = VerticalAlignment.Center, Children = { HubBadge(m), dot } };
        var stats = Line(HubStats(m));
        stats.Tag = "hubstats";
        var age = Line(HubLogic.FormatAge(m.LastModified, now));
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { stats, age } };
        (stats.HorizontalAlignment, age.HorizontalAlignment) = (HorizontalAlignment.Right, HorizontalAlignment.Right);
        var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { Line(m.Name, strong: true), Line(m.Owner) } };
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(names, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(badge);
        grid.Children.Add(names);
        grid.Children.Add(right);
        var row = new ToggleButton
        {
            Content = grid, Tag = "hubrow:" + m.Id, DataContext = m, IsChecked = m.Id == _hubSelected?.Id, ToolTip = m.Id,
            Padding = new Thickness(12, 7, 10, 7), Margin = new Thickness(0, 0, 0, 1), HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        row.SetResourceReference(StyleProperty, "NavToggle");
        row.Click += (_, _) => HubSelect(row);
        return row;
    }

    private List<ToggleButton> HubRows() => _hubList.Children.OfType<ToggleButton>().ToList();

    private void HubCursor(ToggleButton row)
    {
        foreach (var r in HubRows())
            r.IsChecked = r == row;
        row.BringIntoView();
        row.Focus();
    }

    private void HubSelect(ToggleButton row)
    {
        HubCursor(row);
        _hubSelected = (HubModel)row.DataContext;
        _hubDetailCts?.Cancel();
        _hubDetailCts = new CancellationTokenSource();
        _hubDetail.Child = BuildHubDetail(_hubSelected, _hubDetailCts.Token);
        _hubDetailScroll.ScrollToTop();
    }

    // Up/Down move the checked row, Enter shows it (in the search box: searches now). Up from the first row goes to the box.
    private bool HubKey(Key key, bool inSearch)
    {
        if (key == Key.Enter && inSearch)
        {
            HubSearch();
            return true;
        }
        var rows = HubRows();
        if (rows.Count == 0)
            return false;
        var at = inSearch ? -1 : rows.FindIndex(r => r.IsChecked == true);
        switch (key)
        {
            case Key.Down:
                HubCursor(rows[Math.Min(rows.Count - 1, at + 1)]);
                return true;
            case Key.Up when at <= 0:
                if (inSearch)
                    return false;
                _hubSearch.Focus();
                return true;
            case Key.Up:
                HubCursor(rows[at - 1]);
                return true;
            case Key.Enter when at >= 0:
                HubSelect(rows[at]);
                return true;
        }
        return false;
    }

    // ---- detail pane

    private static TextBlock HubEmptyDetail()
    {
        var t = RemoteMuted("Select a model to see its details.");
        t.Tag = "HubDetailEmpty";
        return t;
    }

    private static Border HubPill(string text, Brush? foreground = null)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5 };
        if (foreground is null)
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        else
            t.Foreground = foreground;
        var pill = new Border { Tag = "HubTag:" + text, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(8), Child = t };
        if (foreground is SolidColorBrush status) // a status pill (Downloaded, Gated): a tint of its own colour, as the site's status chips
            pill.Background = new SolidColorBrush(Color.FromArgb(0x26, status.Color.R, status.Color.G, status.Color.B));
        else // a tag: a hairline chip
        {
            pill.SetResourceReference(Border.BackgroundProperty, "Hover");
            pill.SetResourceReference(Border.BorderBrushProperty, "Border");
            pill.BorderThickness = new Thickness(1);
        }
        return pill;
    }

    private static TextBlock HubNote(string text, string tag)
    {
        var t = RemoteMuted(text);
        (t.Tag, t.Margin, t.VerticalAlignment) = (tag, new Thickness(10, 8, 10, 8), VerticalAlignment.Center);
        return t;
    }

    private static readonly FontFamily HubIconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // Green fits, amber marginal, sky partial, rose too big, grey unknown.
    internal static Color? HubFitColor(Fit fit) => fit switch
    {
        Fit.Fits => Color.FromRgb(0x4A, 0xDE, 0x80),
        Fit.Marginal => Color.FromRgb(0xF5, 0xB9, 0x42),
        Fit.Partial => Color.FromRgb(0x38, 0xBD, 0xF8),
        Fit.Oom => Color.FromRgb(0xFB, 0x71, 0x85),
        _ => null,
    };

    private static TextBlock HubFitIcon(Fit fit)
    {
        var icon = new TextBlock { Text = "", FontFamily = HubIconFont, FontSize = 13, Tag = "hubfit:" + fit, ToolTip = HubLogic.FitTip(fit), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        if (HubFitColor(fit) is { } c)
            icon.Foreground = new SolidColorBrush(c);
        else
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return icon;
    }

    private Fit HubFitOf(HubVariant v) => HubLogic.FitOf(v.DownloadBytes, _hubFitFor.Vram, _hubFitFor.Ram);

    // The selected model: logo, name, owner, copy id and its Hugging Face page; chips; the download card (weights picker and
    // Download); facts; the README. The weights and README load in the background, cancelled by ct (another row selected).
    private FrameworkElement BuildHubDetail(HubModel model, CancellationToken ct)
    {
        var name = new TextBlock { Text = model.Name, Tag = "HubDetailName", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var owner = RemoteMuted(model.Owner);
        owner.VerticalAlignment = VerticalAlignment.Center;
        var badge = new Viewbox { Width = 40, Height = 40, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top, Child = HubBadge(model) };
        DockPanel.SetDock(badge, Dock.Left);
        var url = "https://huggingface.co/" + model.Id;
        var link = new Hyperlink(new Run("Open on Hugging Face")) { Tag = "HubOpenHf", ToolTip = url };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentLight");
        link.Click += (_, _) => _openExternal(url);
        var copy = new Button { Content = "Copy id", Tag = "HubCopyId", ToolTip = "Copy " + model.Id, Style = (Style)FindResource("GhostButton"), FontSize = 11.5, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(14, 0, 0, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(model.Id); }
            catch (System.Runtime.InteropServices.COMException) { } // another app holds the clipboard
        };
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0), Children = { owner, new TextBlock { Margin = new Thickness(14, 0, 0, 0), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Inlines = { link } }, copy } };

        _hubChips = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (model.PipelineTag is { Length: > 0 } pipeline)
            _hubChips.Children.Add(HubPill(pipeline));
        if (model.Gated)
            _hubChips.Children.Add(HubPill("Gated", new SolidColorBrush(Color.FromRgb(0xD9, 0x9A, 0x2B))));
        _hubWeights = new Border { Tag = "HubWeightsCard", CornerRadius = new CornerRadius(14), Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 0), MinHeight = 46 };
        _hubWeights.SetResourceReference(Border.BackgroundProperty, "Panel"); // Hover barely differs from the Card around it
        _hubFacts = RemoteMuted("");
        (_hubFacts.Tag, _hubFacts.Margin) = ("HubDetailCounts", new Thickness(4, 10, 0, 0));
        _hubReadme = new Border { Tag = "HubReadmeHost", Margin = new Thickness(0, 22, 0, 0) };
        _hubDlNotice = new Border { Tag = "HubDownloadNotice", Margin = new Thickness(4, 8, 0, 0), Visibility = Visibility.Collapsed };
        _hubSaveTo = new Border { Tag = "HubSaveTo", Margin = new Thickness(4, 14, 0, 0) };
        HubShowSaveTo();
        (_hubVariant, _hubVariantList, _hubSha) = (null, [], "");
        HubFacts();
        _hubDetailTask = Task.WhenAll(HubLoadWeightsAsync(model, ct), HubLoadReadmeAsync(model, ct));
        return new StackPanel
        {
            Tag = "HubDetail:" + model.Id,
            Children = { new DockPanel { Children = { badge, new StackPanel { Children = { name, actions } } } }, _hubChips, _hubSaveTo, _hubWeights, _hubDlNotice, _hubFacts, _hubReadme },
        };
    }

    // Updated, the chosen weights' download, downloads, likes, parameters.
    private void HubFacts()
    {
        if (_hubSelected is not { } m)
            return;
        var facts = new List<string>();
        if (m.LastModified is not null)
            facts.Add("Updated " + HubLogic.FormatAge(m.LastModified, DateTimeOffset.UtcNow));
        if (_hubVariant is { } v)
            facts.Add(HubLogic.FormatSize(v.DownloadBytes) + " download");
        facts.Add($"↓ {HubLogic.FormatCount(m.Downloads)} downloads");
        facts.Add($"♡ {HubLogic.FormatCount(m.Likes)} likes");
        if (m.ParamCount is { } p)
            facts.Add(HubLogic.FormatCount(p) + " params");
        _hubFacts.Text = string.Join("   ·   ", facts);
    }

    // GET through the page's client, cached 5 minutes by address; 404 gives (and caches) null; other failures throw.
    private async Task<string?> HubGetTextAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        if (_hubTextCache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < HubCacheFor)
            return hit.Text;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var response = await HubHttp.GetAsync(url, cts.Token);
        var text = response.StatusCode == HttpStatusCode.NotFound ? null : await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync(cts.Token);
        _hubTextCache[url] = (DateTime.UtcNow, text);
        return text;
    }

    // The repo's GGUF variants (?blobs=true for sizes) in fit order; the first is picked. mmproj: the Vision chip.
    private async Task HubLoadWeightsAsync(HubModel model, CancellationToken ct)
    {
        _hubWeights.Child = HubNote("Loading weights…", "HubWeightsLoading");
        try
        {
            var json = await HubGetTextAsync(HubBlobsUrl(model.Id), TimeSpan.FromSeconds(15), ct)
                ?? throw new HttpRequestException("Not found", null, HttpStatusCode.NotFound);
            var variants = await Task.Run(() => HubLogic.ParseVariants(json), ct);
            _hubSmallest[model.Id] = HubLogic.SmallestWeights(variants);
            var machine = await _hubMachine;
            ct.ThrowIfCancellationRequested(); // another row was selected meanwhile
            if (variants.Variants.Count == 0)
            {
                // no GGUF: the row goes (HubFillRows drops it) and the one now in its place is shown
                var at = HubRows().FindIndex(r => ((HubModel)r.DataContext).Id == model.Id);
                HubFillRows();
                if (HubRows() is { Count: > 0 } rows)
                    HubSelect(rows[Math.Clamp(at, 0, rows.Count - 1)]);
                else
                    (_hubSelected, _hubDetail.Child) = (null, HubEmptyDetail());
                return;
            }
            _hubFitFor = (machine.VramGiB, machine.RamGiB > 0 ? machine.RamGiB : null);
            _hubVariantList = HubLogic.OrderByFit(variants.Variants, _hubFitFor.Vram, _hubFitFor.Ram, v => HubIsDownloaded(model.Id, v));
            (_hubSha, _hubVariant) = (variants.Sha, _hubVariantList.FirstOrDefault());
            if (variants.HasVision)
                _hubChips.Children.Insert(model.PipelineTag is { Length: > 0 } ? 1 : 0, HubPill("Vision"));
            HubShowVariant();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            _hubWeights.Child = HubNotice(HubErrorText(e), "HubWeights", () => _hubDetailTask = HubLoadWeightsAsync(model, ct));
        }
    }

    // The download card: the chosen weights (fit, quant, GGUF, size, chevron: opens the variants) and Download.
    private void HubShowVariant()
    {
        HubFacts();
        if (_hubVariant is not { } v)
            return;
        var label = new TextBlock { Text = v.Label, Tag = "HubWeightsLabel", FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary"); // not the ghost button's muted text
        var gguf = HubPill("GGUF");
        (gguf.Margin, gguf.VerticalAlignment) = (new Thickness(10, 0, 0, 0), VerticalAlignment.Center);
        var size = RemoteMuted(HubLogic.FormatSize(v.SizeBytes));
        (size.Tag, size.Margin, size.VerticalAlignment) = ("HubWeightsSize", new Thickness(10, 0, 0, 0), VerticalAlignment.Center);
        var chevron = new TextBlock { Text = "", FontFamily = HubIconFont, FontSize = 10, Margin = new Thickness(12, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var selector = new Button
        {
            Tag = "HubWeights", ToolTip = "Choose the weights", Style = (Style)FindResource("GhostButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { HubFitIcon(HubFitOf(v)), label, gguf, size, chevron } },
        };
        selector.Click += (_, _) => BuildHubVariantMenu(selector).IsOpen = true;
        _hubDownloadButton = new Button { Content = "Download", Tag = "HubDownload", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(22, 7, 22, 7), VerticalAlignment = VerticalAlignment.Center };
        _hubDownloadButton.Click += (_, _) =>
        {
            if (_hubSelected is { } m && _hubVariant is { } chosen)
                HubStartDownload(m, _hubSha, chosen);
        };
        _hubDlSlot = new Border { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_hubDlSlot, Dock.Right);
        _hubWeights.Child = new DockPanel { Children = { _hubDlSlot, selector } };
        UpdateHubDownloadButton();
    }

    // Every variant in fit order: fit icon (its tip says why), quant, size on the right; the chosen one checked.
    // --ui-smoke clicks its items without opening it.
    private ContextMenu BuildHubVariantMenu(UIElement anchor)
    {
        var menu = NewPresetMenu(anchor);
        foreach (var v in _hubVariantList)
        {
            var item = PresetItem(v.Label, () =>
            {
                _hubVariant = v;
                HubShowVariant();
            });
            var downloaded = _hubSelected is { } m && HubIsDownloaded(m.Id, v);
            item.Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { HubFitIcon(HubFitOf(v)), new TextBlock { Text = v.Label + (downloaded ? "  ✓" : ""), Tag = downloaded ? "hubdownloaded" : null, VerticalAlignment = VerticalAlignment.Center } } };
            (item.IsChecked, item.Tag, item.InputGestureText) = (ReferenceEquals(v, _hubVariant), "hubvariant:" + v.Label, HubLogic.FormatSize(v.SizeBytes));
            menu.Items.Add(item);
        }
        return menu;
    }

    private bool HubIsDownloaded(string repo, HubVariant v)
    {
        var (root, layout) = HubTarget();
        return v.Files.Concat(v.Companions).All(f => HubDownloader.IsDownloaded(root, layout, repo, f.Name));
    }

    // ponytail: any *.incomplete file of the repo counts, whichever weights it belongs to.
    private bool HubHasPartial(string repo)
    {
        var (root, layout) = HubTarget();
        var dir = layout == HubLayout.Flat ? Path.Combine(root, HubDownloadedKey(repo, layout)) : Path.Combine(HubDownloader.RepoFolder(root, repo), "blobs");
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.incomplete", SearchOption.AllDirectories).Any();
    }

    // The card's right side: the progress pill (this model's download running), "Downloaded ✓", "Resume" (a partial file
    // left by a cancel or a failure) or "Download"; under the card, the failed download's notice with Retry.
    private void UpdateHubDownloadButton()
    {
        if (_hubSelected is not { } m || _hubVariant is not { } v)
        {
            _hubDownloadButton.IsEnabled = false;
            return;
        }
        var dl = _hubDl is { } d && d.Repo == m.Id ? d : null;
        // the pill under the weights (beside them it squeezed the selector), the button beside them
        DockPanel.SetDock(_hubDlSlot, dl is { State: HubDlState.Running } ? Dock.Bottom : Dock.Right);
        if (dl is { State: HubDlState.Running })
            _hubDlSlot.Child = HubProgressPill();
        else
        {
            var done = HubIsDownloaded(m.Id, v);
            (_hubDownloadButton.Content, _hubDownloadButton.IsEnabled) = done ? ("Downloaded ✓", false) : (HubHasPartial(m.Id) ? "Resume" : "Download", true);
            _hubDownloadButton.ToolTip = done ? null : $"Download {v.Label} ({HubLogic.FormatSize(v.DownloadBytes)})";
            _hubDlSlot.Child = _hubDownloadButton;
        }
        _hubDlNotice.Child = dl is { State: HubDlState.Failed } ? HubNotice(dl.Error, "HubDownload", () =>
        {
            if (_hubSelected is { } sm && _hubVariant is { } sv)
                HubStartDownload(sm, _hubSha, sv);
        }) : null;
        _hubDlNotice.Visibility = _hubDlNotice.Child is null ? Visibility.Collapsed : Visibility.Visible;
        HubDlShowProgress();
    }

    // Bar, "42%  ·  1.2 GB / 2.9 GB  ·  12 MB/s  ·  2m left", Cancel; HubDlShowProgress fills it in.
    private Border HubProgressPill()
    {
        _hubDlFill = new Border { Width = 0, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
        _hubDlFill.SetResourceReference(Border.BackgroundProperty, "Accent");
        var track = new Border { Width = 90, Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Child = _hubDlFill };
        track.SetResourceReference(Border.BackgroundProperty, "Hover");
        _hubDlText = new TextBlock { Tag = "HubDownloadProgress", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _hubDlText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var cancel = new Button { Content = "✕", Tag = "HubDownloadCancel", ToolTip = "Cancel the download", Style = (Style)FindResource("GhostButton"), Width = 26, Height = 26, Padding = new Thickness(0), FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
        cancel.Click += (_, _) => _hubDlCts?.Cancel();
        var pill = new Border
        {
            Tag = "HubDownloadPill", Padding = new Thickness(14, 3, 4, 3), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { track, _hubDlText, cancel } },
        };
        pill.SetResourceReference(Border.BackgroundProperty, "Card");
        return pill;
    }

    // The strip (any row) and the pill (the downloading model's detail) from _hubDl.
    private void HubDlShowProgress()
    {
        var running = _hubDl is { State: HubDlState.Running } dl ? dl : null;
        _hubStrip.Visibility = running is null ? Visibility.Collapsed : Visibility.Visible;
        _hubSaveTo.IsEnabled = running is null;
        if (running is null)
            return;
        var p = running.Progress;
        var percent = p is { TotalBytes: > 0 } ? (int)(p.DoneBytes * 100 / p.TotalBytes) : 0;
        _hubStripText.Text = $"Downloading {running.Repo} {running.Label}: {percent}%";
        var parts = new List<string> { percent + "%" };
        if (p is not null)
        {
            parts.Add($"{HubLogic.FormatSize(p.DoneBytes)} / {HubLogic.FormatSize(p.TotalBytes)}"); // "0.0 / 0.0 GB" under 50 MB
            if (HubLogic.FormatSpeed(p.BytesPerSecond) is { Length: > 0 } speed)
                parts.Add(speed);
            if (HubLogic.FormatEta(p.TotalBytes - p.DoneBytes, p.BytesPerSecond) is { Length: > 0 } eta)
                parts.Add(eta + " left");
        }
        _hubDlText.Text = string.Join("  ·  ", parts);
        _hubDlFill.Width = 90 * percent / 100.0;
    }

    // One download at a time, off the UI thread, with its own cancel (only Cancel and closing the app stop it). The token
    // comes from the environment only (HF_TOKEN), never stored or shown.
    private void HubStartDownload(HubModel model, string sha, HubVariant variant)
    {
        if (_hubDl is { State: HubDlState.Running })
        {
            Toast("A download is running", "#F7C873");
            return;
        }
        var (root, layout) = HubTarget();
        var files = variant.Files.Concat(variant.Companions).Select(f => new HubDownloadFile(f.Name, f.Size)).ToList();
        var missing = files.Where(f => !HubDownloader.IsDownloaded(root, layout, model.Id, f.Name)).Sum(f => f.Size);
        if (HubDownloader.CheckDiskSpace(root, missing) is { } full)
        {
            _hubDl = new HubDl(model.Id, variant.Label, null, HubDlState.Failed, full);
            UpdateHubDownloadButton();
            return;
        }
        _hubDlCts?.Dispose();
        _hubDlCts = new CancellationTokenSource();
        _hubDl = new HubDl(model.Id, variant.Label, null, HubDlState.Running);
        _hubDlTask = HubDownloadAsync(model.Id, sha, variant.Label, files, root, layout, _hubDlCts.Token);
        UpdateHubDownloadButton();
    }

    private async Task HubDownloadAsync(string repo, string sha, string label, List<HubDownloadFile> files, string root, HubLayout layout, CancellationToken ct)
    {
        var progress = new Progress<HubProgress>(p =>
        {
            if (_hubDl is { State: HubDlState.Running } dl && dl.Repo == repo)
            {
                _hubDl = dl with { Progress = p };
                HubDlShowProgress();
            }
        });
        var downloader = new HubDownloader(HubDlHttp, root, layout, HubDownloader.EnvToken());
        try
        {
            await Task.Run(() => downloader.DownloadAsync(repo, sha, files, progress, ct), ct);
            _hubDl = new HubDl(repo, label, _hubDl?.Progress, HubDlState.Done);
            if (_hubMachine.IsCompletedSuccessfully)
                _hubMachine.Result.OnDisk.Add(HubDownloadedKey(repo, layout));
            _localModelsVersion++;
            Toast($"{repo} {label} downloaded");
            HubShowOnDisk();
            if (_hubSelected?.Id == repo && _hubVariantList.Count > 0)
            {
                _hubVariantList = HubLogic.OrderByFit(_hubVariantList, _hubFitFor.Vram, _hubFitFor.Ram, v => HubIsDownloaded(repo, v));
                HubShowVariant();
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _hubDl = null; // the partial file stays: Resume
        }
        catch (Exception e)
        {
            _hubDl = new HubDl(repo, label, null, HubDlState.Failed, HubDownloadErrorText(e));
        }
        if (_hubSelected?.Id == repo && _hubVariant is not null)
            UpdateHubDownloadButton();
        else
            HubDlShowProgress();
    }

    private static string HubDownloadErrorText(Exception e) => e switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            e.Message + ". Set the HF_TOKEN environment variable and restart the app.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "File not found on Hugging Face.",
        HttpRequestException { StatusCode: not null } => e.Message + ".",
        HttpRequestException => "Could not reach Hugging Face: " + e.Message,
        OperationCanceledException => "Hugging Face stopped sending data. Retry resumes the download.",
        IOException or UnauthorizedAccessException => "Could not write the download: " + e.Message,
        _ => "The download failed: " + e.Message,
    };

    // README.md from the repo, cleaned (HubLogic.CleanReadme) and rendered; the wheel scrolls the detail pane.
    private async Task HubLoadReadmeAsync(HubModel model, CancellationToken ct)
    {
        _hubReadme.Child = HubNote("Loading README…", "HubReadmeLoading");
        try
        {
            var md = await HubGetTextAsync($"https://huggingface.co/{model.Id}/resolve/main/README.md", TimeSpan.FromSeconds(10), ct);
            var clean = md is null ? "" : await Task.Run(() => HubLogic.CleanReadme(md), ct);
            ct.ThrowIfCancellationRequested();
            if (clean.Trim().Length == 0)
            {
                _hubReadme.Child = HubNote("No README", "HubNoReadme");
                return;
            }
            var view = new RichTextBox
            {
                IsReadOnly = true, IsDocumentEnabled = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                FontFamily = FontFamily, Cursor = Cursors.IBeam, Tag = "HubReadme", Document = Markdown.ToDocument(clean, 13),
            };
            Markdown.NoTopMargin(view.Document.Blocks.FirstBlock);
            view.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentLight");
            view.PreviewMouseWheel += (_, e) =>
            {
                e.Handled = true;
                _hubDetailScroll.ScrollToVerticalOffset(_hubDetailScroll.VerticalOffset - e.Delta);
            };
            _hubReadme.Child = view;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            _hubReadme.Child = HubNotice(HubErrorText(e, 10), "HubReadme", () => _hubDetailTask = HubLoadReadmeAsync(model, ct));
        }
    }

    // ---- --ui-smoke

    // The sidebar's Models Hub (under Remote API) opens the page in place of Code, Chat and Remote API; on canned replies the
    // rows show (the diffusion repo dropped) with their counts and tiles; a row fills the detail; Up/Down/Enter; a typo finds
    // its model; the first page is cached; each sort asks HF for its own order; the fit filter (downloaded repos stay);
    // paging; a 429 and Retry; Remote API and New chat close it.
    private async Task<bool> ModelHubSmoke(Action<string, FrameworkElement?> snap)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: model hub: {what}"); return false; }
        var fake = new HubFakeHandler();
        var (factory, vramWas, ramWas, open, sortWas, fitWas, cacheWas) = (HubHttpHandlerFactory, HubVramGiBOverride, HubRamGiBOverride, _openExternal, _settings.HubSort, _settings.HubFitsGpu, Environment.GetEnvironmentVariable("HF_HUB_CACHE"));
        var (remoteHttp, helpShown) = (_remoteHttp, _settings.RemoteHelpShown);
        var (kindsWas, brandsWas) = (_settings.HubKinds, _settings.HubBrands);
        var (hubFolderWas, modelsWas, pickWas, choiceWas) = (_settings.HubDownloadFolder, _settings.ModelsFolder, _codeFolderPick, _codeChoice);
        var opened = new List<string>();
        var cache = Path.Combine(AppSettings.DataRoot, "hub-smoke-cache");
        var (saveDir, modelsDir) = (Path.Combine(AppSettings.DataRoot, "hub-smoke-saveto"), Path.Combine(AppSettings.DataRoot, "hub-smoke-models"));
        var (onDiskDir, tinyCached) = (Path.Combine(AppSettings.DataRoot, "hub-smoke-ondisk"), Path.Combine(cache, "models--zyxlab--Tiny-1B-GGUF"));
        string Url(string query, string sort, string? pipelineTag = null) => HubLogic.SearchUrl(query, sort, pipelineTag: pipelineTag).AbsoluteUri;
        string Rows() => string.Join(", ", HubRows().Select(r => ((HubModel)r.DataContext).Id));
        T? Find<T>(string tag) where T : FrameworkElement => SmokeFind<T>(ModelHubView, e => e.Tag as string == tag);
        async Task Settled()
        {
            await Task.Delay(450); // the 300 ms debounce
            await _hubTask;
            await Task.Delay(100); // a page the short list asks for next
            await _hubTask;
        }
        async Task Type(string text)
        {
            for (var i = 0; i <= text.Length; i++)
                _hubSearch.Text = text[..i];
            await Settled();
        }
        string Status() => string.Join(" ", SmokeVisuals(_hubStatus).OfType<TextBlock>().Select(t => t.Text));
        async Task Menu(string tag)
        {
            var item = BuildHubSortMenu(this).Items.OfType<MenuItem>().First(i => i.Tag as string == tag);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Settled();
        }
        async Task<bool> OpenFromSidebar()
        {
            var items = SidebarList.Children.OfType<FrameworkElement>().Select(e => e.Tag as string).ToList();
            var at = items.IndexOf("+Remote API");
            if (at < 0 || at + 1 >= items.Count || items[at + 1] != "+Models Hub")
                return false;
            SmokeClick((Button)SidebarList.Children[at + 1]);
            await Settled();
            await Task.Delay(300); // the views' fades
            return true;
        }
        const string Qwen = HubFakeHandler.Qwen, Tiny = HubFakeHandler.Tiny;
        try
        {
            HubHttpHandlerFactory = () => fake;
            _hubHttp = null;
            _hubPageCache.Clear();
            HubVramGiBOverride = 4;
            HubRamGiBOverride = 16;
            _hubTextCache.Clear();
            _openExternal = opened.Add;
            Environment.SetEnvironmentVariable("HF_HUB_CACHE", cache);
            (_settings.HubSort, _settings.HubFitsGpu, _settings.HubDownloadFolder, _settings.HubKinds, _settings.HubBrands) = ("trending", false, "", (int)HubKind.Text, []);

            if (!await OpenFromSidebar())
                return Fail("no Models Hub right under Remote API in the sidebar");
            if (ModelHubView.Visibility != Visibility.Visible || CodeView.Visibility == Visibility.Visible || ChatView.Visibility == Visibility.Visible || RemoteApiView.Visibility == Visibility.Visible)
                return Fail($"page {ModelHubView.Visibility}, code {CodeView.Visibility}, chat {ChatView.Visibility}, remote api {RemoteApiView.Visibility}");
            if (Rows() != $"{Qwen}, {Tiny}" || !fake.Seen.Contains(Url("", "trending")) || fake.Seen.Any(u => !u.Contains("filter=gguf")))
                return Fail($"rows [{Rows()}] from [{string.Join(", ", fake.Seen)}]");
            var stats = HubRows().Select(r => SmokeFind<TextBlock>(r, t => t.Tag as string == "hubstats")?.Text).ToList();
            if (stats[0] != "♡ 1.4K   ↓ 61K" || stats[1] != "♡ 12   ↓ 1.3M")
                return Fail("row counts: " + string.Join(" / ", stats));
            // unsloth's Qwen3.8 gets Qwen's logo (the brand in its name); zyxlab, no brand and no logo, its initial
            if (SmokeFind<Border>(HubRows()[0], b => b.Tag as string == "icon:qwen-color") is null || SmokeFind<Border>(HubRows()[1], b => b.Tag as string == "hubtile:Z") is null)
                return Fail("the brand logo and the owner's initial tile");
            var hint = Find<TextBlock>("HubSearchHint")!;
            var caretX = _hubSearch.TransformToAncestor(ModelHubView).Transform(_hubSearch.GetRectFromCharacterIndex(0).TopLeft).X;
            var hintX = hint.TransformToAncestor(ModelHubView).Transform(new Point(0, 0)).X;
            if (Math.Abs(caretX - hintX) > 1)
                return Fail($"the search hint starts at {hintX:0.#}, the caret at {caretX:0.#}");
            if (Find<TextBlock>("HubChip:VRAM")?.Text != "4 GB" || Find<TextBlock>("HubChip:CPU")?.Text != Environment.ProcessorCount + " threads" || Find<TextBlock>("HubSortLabel")?.Text != "↑↓  Trending  ▾")
                return Fail($"header: VRAM {Find<TextBlock>("HubChip:VRAM")?.Text}, CPU {Find<TextBlock>("HubChip:CPU")?.Text}, sort {Find<TextBlock>("HubSortLabel")?.Text}");

            // a row fills the detail; Down moves the checked row, Enter shows it
            SmokeClick(HubRows()[0]);
            await _hubDetailTask;
            await Task.Delay(200);
            string? Text(string tag) => SmokeFind<TextBlock>(_hubDetail, t => t.Tag as string == tag)?.Text;
            string Chips() => string.Join(",", _hubChips.Children.OfType<Border>().Select(b => b.Tag as string));
            if (SmokeFind<StackPanel>(_hubDetail, p => p.Tag as string == "HubDetail:" + Qwen) is null || Text("HubDetailName") != "Qwen3.8-480B-GGUF" ||
                Chips() != "HubTag:text-generation,HubTag:Vision,HubTag:Gated" || Text("HubDetailCounts")?.Contains("♡ 1.4K likes", StringComparison.Ordinal) != true)
                return Fail($"the detail of the clicked row: chips {Chips()}, facts {Text("HubDetailCounts")}");
            // the weights: the biggest that fits 4 GB (RAM 16 GB) is picked; the menu lists them in fit order with sizes
            if (_hubVariant?.Label != "Q2_K" || Text("HubWeightsLabel") != "Q2_K" || Text("HubWeightsSize") != "1.5 GB" || _hubSha != "qwensha" ||
                SmokeFind<TextBlock>(_hubDetail, t => t.Tag as string == "hubfit:Fits") is null || Text("HubDetailCounts")?.Contains("2.1 GB download", StringComparison.Ordinal) != true ||
                Find<Button>("HubDownload") is not { IsEnabled: true })
                return Fail($"default weights {_hubVariant?.Label}: {Text("HubWeightsLabel")} {Text("HubWeightsSize")} {Text("HubDetailCounts")}");
            var variantItems = BuildHubVariantMenu(this).Items.OfType<MenuItem>().ToList();
            string Item(MenuItem i)
            {
                var icon = ((Panel)i.Header).Children.OfType<TextBlock>().First();
                var tipOk = Equals(icon.ToolTip, HubLogic.FitTip(Enum.Parse<Fit>(((string)icon.Tag)["hubfit:".Length..])));
                return $"{i.Tag} {i.InputGestureText} {icon.Tag} {((SolidColorBrush)icon.Foreground).Color} {tipOk} {i.IsChecked}";
            }
            var wantItems = string.Join(" | ",
                $"hubvariant:Q2_K 1.5 GB hubfit:Fits {HubFitColor(Fit.Fits)} True True", $"hubvariant:Q3_K_M 2.1 GB hubfit:Marginal {HubFitColor(Fit.Marginal)} True False",
                $"hubvariant:UD-Q4_K_XL 5.0 GB hubfit:Partial {HubFitColor(Fit.Partial)} True False", $"hubvariant:BF16 20 GB hubfit:Oom {HubFitColor(Fit.Oom)} True False");
            if (string.Join(" | ", variantItems.Select(Item)) != wantItems)
                return Fail("weights menu: " + string.Join(" | ", variantItems.Select(Item)));
            variantItems[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (_hubVariant?.Label != "UD-Q4_K_XL" || _hubVariant.Files.Count != 2 || Text("HubWeightsLabel") != "UD-Q4_K_XL" || Text("HubWeightsSize") != "5.0 GB" ||
                SmokeFind<TextBlock>(_hubDetail, t => t.Tag as string == "hubfit:Partial") is null || Text("HubDetailCounts")?.Contains("5.6 GB download", StringComparison.Ordinal) != true ||
                BuildHubVariantMenu(this).Items.OfType<MenuItem>().Single(i => i.IsChecked).Tag as string != "hubvariant:UD-Q4_K_XL")
                return Fail($"switching weights: {_hubVariant?.Label} {Text("HubWeightsLabel")} {Text("HubWeightsSize")} {Text("HubDetailCounts")}");
            // the README: front matter and the HTML block gone, heading and paragraph rendered
            var readme = SmokeFind<RichTextBox>(_hubDetail, r => r.Tag as string == "HubReadme");
            var blocks = readme?.Document.Blocks.OfType<Paragraph>().Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text).ToList() ?? [];
            if (blocks.Count != 2 || blocks[0] != "Qwen3.8 480B" || blocks[1] != "Run it with llama.cpp on your own machine.")
                return Fail("README: " + string.Join(" / ", blocks));
            await Task.Delay(200); // the switched card's layout
            snap("20-model-hub", null);
            SmokeFind<Hyperlink>(_hubDetail, h => h.Tag as string == "HubOpenHf")?.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            if (!opened.SequenceEqual(["https://huggingface.co/" + Qwen]))
                return Fail("Open on Hugging Face: " + string.Join(", ", opened));
            if (!HubKey(Key.Down, false) || HubRows()[1].IsChecked != true || HubRows()[0].IsChecked == true || _hubSelected?.Id != Qwen)
                return Fail("Down did not move to the next row (or changed the detail)");
            if (!HubKey(Key.Enter, false) || _hubSelected?.Id != Tiny || SmokeFind<StackPanel>(_hubDetail, p => p.Tag as string == "HubDetail:" + Tiny) is null)
                return Fail("Enter did not show the row");
            await _hubDetailTask;
            if (Text("HubNoReadme") != "No README" || _hubVariant?.Label != "Q8_0" || Chips() != "HubTag:text-generation")
                return Fail($"Tiny: README {Text("HubNoReadme")}, weights {_hubVariant?.Label}, chips {Chips()}");

            // another row selected while the first one's weights are still loading: only the new model shows, even when the
            // old reply lands afterwards
            _hubTextCache.Clear();
            fake.Hold = new TaskCompletionSource();
            SmokeClick(HubRows()[0]);
            var stale = _hubDetailTask;
            await Task.Delay(100);
            if (Find<TextBlock>("HubWeightsLoading")?.Text != "Loading weights…")
                return Fail("no skeleton while the weights load");
            SmokeClick(HubRows()[1]);
            await _hubDetailTask;
            fake.Hold.SetResult();
            fake.Hold = null;
            await stale;
            await Task.Delay(100);
            if (_hubSelected?.Id != Tiny || SmokeFind<StackPanel>(_hubDetail, p => p.Tag as string == "HubDetail:" + Tiny) is null || _hubVariant?.Label != "Q8_0" ||
                Text("HubWeightsLabel") != "Q8_0" || _hubSha != "tinysha" || Chips() != "HubTag:text-generation" || Text("HubNoReadme") != "No README")
                return Fail($"a stale load showed: {_hubSelected?.Id}, weights {_hubVariant?.Label} {Text("HubWeightsLabel")}, sha {_hubSha}, chips {Chips()}");

            // a typo finds its model (the query's variants asked in turn); no match says so; the clear button
            await Type("qwn 3.8");
            if (Rows() != Qwen || !fake.Seen.Contains(Url("qwn 3.8", "trending")) || !fake.Seen.Contains(Url("qwn3.8", "trending")))
                return Fail($"\"qwn 3.8\": rows [{Rows()}] from [{string.Join(", ", fake.Seen)}]");
            await Type("zzzzzz");
            if (Rows() != "" || !Status().Contains("No GGUF models match “zzzzzz”"))
                return Fail("no-match state: " + Status());
            var asked = fake.Seen.Count;
            SmokeClick(Find<Button>("HubClear")!);
            await Settled();
            if (_hubSearch.Text != "" || Rows() != $"{Qwen}, {Tiny}" || fake.Seen.Count != asked)
                return Fail($"clear: \"{_hubSearch.Text}\", rows [{Rows()}], {fake.Seen.Count - asked} requests (the first page is cached)");

            // makers' logos from the repo name (DeepSeek and Qwen uploaded by others, LiquidAI's account has none); the detail's too
            fake.BrandRows = true;
            _hubPageCache.Clear();
            HubSearch();
            await Settled();
            foreach (var (id, icon) in new[] { (HubFakeHandler.DeepSeek, "deepseek-color"), (HubFakeHandler.Huihui, "qwen-color"), (HubFakeHandler.Liquid, "liquid") })
                if (Row(id) is not { } brandRow || SmokeFind<Border>(brandRow, b => b.Tag as string == "icon:" + icon) is null)
                    return Fail($"{id}: no {icon} logo in [{Rows()}]");
            SmokeClick(Row(HubFakeHandler.DeepSeek)!);
            await Task.Delay(200);
            if (SmokeFind<Border>(_hubDetail, b => b.Tag as string == "icon:deepseek-color") is null)
                return Fail("no DeepSeek logo in the detail");

            // the filter menus: Select All / Deselect All and its line pinned above the items (the menu's Tag)
            async Task Toggle(ContextMenu menu, string tag)
            {
                menu.Items.OfType<MenuItem>().Concat(((Panel)menu.Tag).Children.OfType<MenuItem>()).First(i => i.Tag as string == tag)
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Settled();
            }
            string? AllHeader(ContextMenu menu) => ((Panel)menu.Tag).Children.OfType<MenuItem>().Single().Header as string;
            string? KindsLabel() => Find<TextBlock>("HubKindsLabel")?.Text;

            // the type pill: Text & vision; Image adds the diffusion repo, asked with its pipeline_tag; All types; none
            if (KindsLabel() != "Text & vision  ▾" || Rows().Contains("art/flux-gguf"))
                return Fail($"type pill \"{KindsLabel()}\", rows [{Rows()}]");
            await Toggle(BuildHubKindsMenu(this), "hubkind:Image");
            if (KindsLabel() != "Text, Image  ▾" || !Rows().Contains("art/flux-gguf") || !fake.Seen.Contains(Url("", "trending", "text-to-image")) ||
                AppSettings.Load().HubKinds != (int)(HubKind.Text | HubKind.Image))
                return Fail($"Image: \"{KindsLabel()}\", rows [{Rows()}], saved {AppSettings.Load().HubKinds}");
            await Toggle(BuildHubKindsMenu(this), "hubkind:all");
            var kindsMenu = BuildHubKindsMenu(this);
            if (KindsLabel() != "All types  ▾" || AllHeader(kindsMenu) != "Deselect All" || AppSettings.Load().HubKinds != (int)HubAllKinds)
                return Fail($"all types: \"{KindsLabel()}\", \"{AllHeader(kindsMenu)}\", saved {AppSettings.Load().HubKinds}");
            kindsMenu.IsOpen = true;
            await Task.Delay(400);
            snap("20a-hub-type-menu", kindsMenu);
            kindsMenu.IsOpen = false;
            await Toggle(kindsMenu, "hubkind:all");
            if (Rows() != "" || !Status().Contains("Pick at least one model type") || AppSettings.Load().HubKinds != 0)
                return Fail($"no type: rows [{Rows()}], \"{Status()}\", saved {AppSettings.Load().HubKinds}");
            await Toggle(BuildHubKindsMenu(this), "hubkind:Text");

            // the maker menu: the brands of the picked types and Other, 9 rows showing under the pinned Select All
            var brandMenu = BuildHubBrandMenu(this);
            var brandTags = brandMenu.Items.OfType<MenuItem>().Select(i => i.Tag as string).ToList();
            if (!brandTags.SequenceEqual(HubLogic.BrandsFor(HubKind.Text).Select(b => "hubbrand:" + b.Key).Append("hubbrand:other")) || AllHeader(brandMenu) != "Select All")
                return Fail($"maker menu: {string.Join(", ", brandTags)}");
            brandMenu.IsOpen = true;
            await Task.Delay(400);
            var list = brandMenu.Template.FindName("Rows", brandMenu) as ScrollViewer;
            var allItem = ((Panel)brandMenu.Tag).Children.OfType<MenuItem>().Single();
            if (list is null || list.ViewportHeight != 9 * HubMenuRow || list.ExtentHeight <= list.ViewportHeight || !allItem.IsVisible || allItem.IsDescendantOf(list) ||
                !SmokeVisuals(list).OfType<System.Windows.Controls.Primitives.ScrollBar>().Any(b => b.Orientation == Orientation.Vertical && b.IsVisible))
                return Fail($"maker menu: {list?.ViewportHeight / HubMenuRow:0.##} rows showing of {list?.ExtentHeight / HubMenuRow:0.##}, Select All pinned {allItem.IsVisible && list is not null && !allItem.IsDescendantOf(list)}");
            snap("20b-hub-maker-menu", brandMenu);            brandMenu.IsOpen = false;

            // a maker keeps its rows, asked for by its word, and dots the filter button; a maker with none says so
            await Toggle(BuildHubBrandMenu(this), "hubbrand:deepseek");
            if (Rows() != HubFakeHandler.DeepSeek || !fake.Seen.Contains(Url("deepseek", "trending")) || Find<System.Windows.Shapes.Ellipse>("HubBrandDot")?.Visibility != Visibility.Visible ||
                !AppSettings.Load().HubBrands.SequenceEqual(["deepseek"]))
                return Fail($"DeepSeek: rows [{Rows()}], dot {Find<System.Windows.Shapes.Ellipse>("HubBrandDot")?.Visibility}, saved [{string.Join(", ", AppSettings.Load().HubBrands)}]");
            snap("20c-hub-maker-filtered", null);
            await Toggle(BuildHubBrandMenu(this), "hubbrand:deepseek");
            await Toggle(BuildHubBrandMenu(this), "hubbrand:kimi");
            if (Rows() != "" || !Status().Contains("No models match these filters") || !AppSettings.Load().HubBrands.SequenceEqual(["kimi"]))
                return Fail($"Kimi: rows [{Rows()}], \"{Status()}\"");
            SmokeVisuals(_hubStatus).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<Hyperlink>()).First(h => h.Tag as string == "HubClearFilters")
                .RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            await Settled();
            if (Find<System.Windows.Shapes.Ellipse>("HubBrandDot")?.Visibility == Visibility.Visible || KindsLabel() != "Text & vision  ▾" ||
                AppSettings.Load() is not { HubBrands.Count: 0, HubKinds: (int)HubKind.Text } || Row(HubFakeHandler.DeepSeek) is null)
                return Fail($"clear filters: rows [{Rows()}], \"{KindsLabel()}\"");
            fake.BrandRows = false;
            _hubPageCache.Clear();
            HubSearch();
            await Settled();
            if (Rows() != $"{Qwen}, {Tiny}")
                return Fail($"after the filters: rows [{Rows()}]");

            // each sort asks HF for its own order (Latest: newest and trending), is checked, labelled and saved
            foreach (var sort in HubLogic.Sorts)
            {
                _hubPageCache.Clear();
                lock (fake.Seen)
                    fake.Seen.Clear();
                await Menu("hubsort:" + sort.Id);
                string[] want = sort.Id == "latest" ? [Url("", "latest"), Url("", "trending")] : [Url("", sort.Id)];
                if (!fake.Seen.Order().SequenceEqual(want.Order()) || Rows() != $"{Qwen}, {Tiny}" && Rows() != $"{Tiny}, {Qwen}")
                    return Fail($"{sort.Label}: asked [{string.Join(", ", fake.Seen)}], rows [{Rows()}]");
                if (Find<TextBlock>("HubSortLabel")?.Text != $"↑↓  {sort.Label}  ▾" || AppSettings.Load().HubSort != sort.Id ||
                    BuildHubSortMenu(this).Items.OfType<MenuItem>().Where(i => i.IsChecked).Select(i => i.Tag as string).SingleOrDefault() != "hubsort:" + sort.Id)
                    return Fail($"{sort.Label}: label, check mark or saved sort");
            }

            // the green dot: a GGUF in the hub cache (Tiny) or in a Models folder (Qwen, by its folder name)
            System.Windows.Shapes.Ellipse? Dot(string id) => Row(id) is { } r ? SmokeFind<System.Windows.Shapes.Ellipse>(r, e => e.Tag as string == "HubRowOnDisk") : null;
            if (Dot(Qwen) is not { Visibility: Visibility.Collapsed } || Dot(Tiny) is not { Visibility: Visibility.Collapsed })
                return Fail("a dot on a model not on disk");
            Directory.CreateDirectory(Path.Combine(tinyCached, "snapshots", "x"));
            File.WriteAllBytes(Path.Combine(tinyCached, "snapshots", "x", "Tiny-1B-Q8_0.gguf"), [1]);
            Directory.CreateDirectory(Path.Combine(onDiskDir, "Qwen3.8-480B-GGUF"));
            File.WriteAllBytes(Path.Combine(onDiskDir, "Qwen3.8-480B-GGUF", "Qwen3.8-480B-Q2_K.gguf"), [1]);
            _settings.ModelsFolder = onDiskDir;
            await OpenFromSidebar();
            if (Dot(Qwen) is not { Visibility: Visibility.Visible } qwenDot || Dot(Tiny) is not { Visibility: Visibility.Visible } || qwenDot.ToolTip as string != "Downloaded")
                return Fail($"dots: Qwen {Dot(Qwen)?.Visibility} \"{Dot(Qwen)?.ToolTip}\", Tiny {Dot(Tiny)?.Visibility}");
            snap("20d-hub-on-disk", null);
            Directory.Delete(tinyCached, recursive: true);
            Directory.Delete(onDiskDir, recursive: true);
            _settings.ModelsFolder = modelsWas;

            // Fits my GPU: off by default; disabled while the GPU memory is unknown
            Button Fits() => Find<Button>("HubFitsGpu")!;
            if (Fits().Content as string != "Fits my GPU" || !Fits().IsEnabled)
                return Fail($"Fits my GPU: \"{Fits().Content}\", enabled {Fits().IsEnabled}");
            HubVramGiBOverride = 0;
            await OpenFromSidebar();
            if (Fits().IsEnabled || Fits().ToolTip as string != "GPU memory is unknown" || !ToolTipService.GetShowOnDisabled(Fits()))
                return Fail($"Fits my GPU, VRAM unknown: enabled {Fits().IsEnabled}, \"{Fits().ToolTip}\"");
            // 1.2 GB: on, the sizes fetched hide Qwen (smallest 1.5 GB) and keep Tiny (1.1 GB); off, Qwen is back
            HubVramGiBOverride = 1.2;
            await OpenFromSidebar();
            _hubSmallest.Clear(); // known from the clicks above: the fetch must find them
            _hubTextCache.Clear();
            SmokeClick(Fits());
            await _hubSizeTask;
            if (Rows() != Tiny || Fits().Content as string != "✓ Fits my GPU" || !AppSettings.Load().HubFitsGpu || !fake.Seen.Contains(HubBlobsUrl(Qwen)))
                return Fail($"Fits my GPU on: rows [{Rows()}], \"{Fits().Content}\", saved {AppSettings.Load().HubFitsGpu}");
            SmokeClick(Fits());
            if (Row(Qwen) is null || Row(Tiny) is null || AppSettings.Load().HubFitsGpu)
                return Fail($"Fits my GPU off: rows [{Rows()}]");
            HubVramGiBOverride = 4;
            await OpenFromSidebar();

            // paging: the Link header's next page is fetched while the list is short, its rows appended once each
            fake.Paged = true;
            _hubPageCache.Clear();
            await Menu("hubsort:trending");
            fake.Paged = false;
            if (Rows() != $"{Qwen}, {Tiny}, {HubFakeHandler.PageTwo}" || !fake.Seen.Contains(HubFakeHandler.NextUrl))
                return Fail($"paging: rows [{Rows()}]");

            // a repo without GGUF files leaves the list and the row now in its place shows; a failing load shows a red notice
            // and Retry loads it
            SmokeClick(HubRows()[2]);
            await _hubDetailTask;
            await _hubDetailTask;
            if (Rows() != $"{Qwen}, {Tiny}" || _hubSelected?.Id != Tiny || SmokeFind<StackPanel>(_hubDetail, p => p.Tag as string == "HubDetail:" + Tiny) is null || _hubVariant?.Label != "Q8_0")
                return Fail($"no-GGUF repo: rows [{Rows()}], showing {_hubSelected?.Id} {_hubVariant?.Label}");
            _hubTextCache.Clear();
            fake.Status = HttpStatusCode.InternalServerError;
            SmokeClick(HubRows()[0]);
            await _hubDetailTask;
            fake.Status = HttpStatusCode.OK;
            if (Text("HubWeightsError") != "Hugging Face answered 500 (InternalServerError)." || Find<Button>("HubWeightsRetry") is not { } weightsRetry || Find<Button>("HubDownload") is not null)
                return Fail("failed weights: " + Text("HubWeightsError"));
            SmokeClick(weightsRetry);
            await _hubDetailTask;
            if (_hubVariant?.Label != "Q2_K" || Text("HubWeightsError") is not null || Find<Button>("HubDownload") is null)
                return Fail($"weights Retry: {_hubVariant?.Label}, {Text("HubWeightsError")}");

            // HTTP 429: a readable error with Retry
            fake.Status = HttpStatusCode.TooManyRequests;
            await Type("tiny");
            if (Rows() != "" || Find<TextBlock>("HubError")?.Text.Contains("rate limiting") != true || Find<Button>("HubRetry") is not { } retry)
                return Fail("429: " + Status());
            fake.Status = HttpStatusCode.OK;
            SmokeClick(retry);
            await Settled();
            if (Rows() != Tiny || Find<TextBlock>("HubError") is not null)
                return Fail($"Retry: rows [{Rows()}], {Status()}");

            // downloads: Mini's Q4_K_M (with its mmproj) stops half-way; the pill and the strip show it
            const string Mini = HubFakeHandler.Mini, Locked = HubFakeHandler.Locked;
            var repo = HubDownloader.RepoFolder(cache, Mini);
            if (Directory.Exists(repo))
                Directory.Delete(repo, recursive: true);
            (fake.Downloads, _hubDlHttp, fake.Gate) = (true, null, new TaskCompletionSource());
            _hubPageCache.Clear();
            await Type("");
            ToggleButton? Row(string id) => HubRows().FirstOrDefault(r => r.Tag as string == "hubrow:" + id);
            async Task Show(string id)
            {
                SmokeClick(Row(id)!);
                await _hubDetailTask;
                await Task.Delay(100);
            }
            string Button() => $"{Find<Button>("HubDownload")?.Content} {Find<Button>("HubDownload")?.IsEnabled}";
            if (Row(Mini) is null || Row(Locked) is null)
                return Fail($"download rows: [{Rows()}]");
            await Show(Mini);
            if (_hubVariant?.Label != "Q4_K_M" || Button() != "Download True")
                return Fail($"Mini: weights {_hubVariant?.Label}, button {Button()}");
            var versionWas = _localModelsVersion;
            SmokeClick(Find<Button>("HubDownload")!);
            await fake.AtGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(400); // the last progress report
            var pill = Find<TextBlock>("HubDownloadProgress")?.Text ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(pill, @"^\d+%  ·  [\d.]+ [KM]B / 3\.5 MB  ·  [\d.]+ MB/s") || _hubStrip.Visibility != Visibility.Visible ||
                !_hubStripText.Text.StartsWith("Downloading zyxlab/Mini-GGUF Q4_K_M", StringComparison.Ordinal))
                return Fail($"while downloading: pill \"{pill}\", strip {_hubStrip.Visibility} \"{_hubStripText.Text}\"");
            snap("21-hub-downloading", null);
            // another row keeps the strip; a second download is refused; back on Mini the pill is there
            await Show(Tiny);
            SmokeClick(Find<Button>("HubDownload")!);
            if (_hubStrip.Visibility != Visibility.Visible || _hubDl?.Repo != Mini || !ToastsOf("A download is running").Any())
                return Fail($"another row while downloading: strip {_hubStrip.Visibility}, download {_hubDl?.Repo}");
            await Show(Mini);
            if (Find<Border>("HubDownloadPill") is null)
                return Fail("no progress pill back on the downloading row");
            // closing the page does not stop it: the strip is back on reopening
            CloseModelHub();
            await Task.Delay(300);
            if (_hubDlTask.IsCompleted || _hubDlCts?.IsCancellationRequested != false)
                return Fail("closing the page stopped the download");
            await OpenFromSidebar();
            if (_hubStrip.Visibility != Visibility.Visible || !_hubStripText.Text.StartsWith("Downloading zyxlab/Mini-GGUF", StringComparison.Ordinal))
                return Fail($"reopened: strip {_hubStrip.Visibility} \"{_hubStripText.Text}\"");
            // cancel mid-file: Resume asks for the rest (Range) and the bytes match
            await Show(Mini);
            SmokeClick(Find<Button>("HubDownloadCancel")!);
            await _hubDlTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (Button() != "Resume True" || _hubDl is not null || _hubStrip.Visibility == Visibility.Visible)
                return Fail($"cancelled: button {Button()}, download {_hubDl?.State}, strip {_hubStrip.Visibility}");
            fake.Gate = null;
            lock (fake.Seen)
                fake.Seen.Clear();
            SmokeClick(Find<Button>("HubDownload")!);
            await _hubDlTask.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(100);
            if (!fake.Seen.Any(s => System.Text.RegularExpressions.Regex.IsMatch(s, @"^Range: bytes=[1-9]\d*-$")))
                return Fail("Resume sent no Range: " + string.Join(", ", fake.Seen));
            var snapshot = Path.Combine(repo, "snapshots", "minisha");
            foreach (var file in new[] { "Mini-Q4_K_M.gguf", "mmproj-F16.gguf" })
            {
                var bytes = HubFakeHandler.MiniFiles[file];
                var blob = Path.Combine(repo, "blobs", HubFakeHandler.Sha256(bytes));
                if (!File.Exists(blob) && !File.Exists(Path.Combine(snapshot, file)) || !File.ReadAllBytes(Path.Combine(snapshot, file)).AsSpan().SequenceEqual(bytes))
                    return Fail($"downloaded {file}: blob or snapshot missing or different");
            }
            if (File.Exists(Path.Combine(snapshot, "Mini-Q2_K.gguf")) || !File.Exists(Path.Combine(repo, "refs", "main")) || File.ReadAllText(Path.Combine(repo, "refs", "main")) != "minisha")
                return Fail("the cache layout: refs/main or an extra file");
            // done: the toast, "Downloaded ✓", the menu's check, the row's dot, the pickers' rescan
            var checkedItems = BuildHubVariantMenu(this).Items.OfType<MenuItem>().Where(i => ((Panel)i.Header).Children.OfType<TextBlock>().Any(t => t.Tag as string == "hubdownloaded")).Select(i => i.Tag as string);
            if (!ToastsOf("zyxlab/Mini-GGUF Q4_K_M downloaded").Any() || Button() != "Downloaded ✓ False" || string.Join(",", checkedItems) != "hubvariant:Q4_K_M" ||
                Dot(Mini) is not { Visibility: Visibility.Visible } || _localModelsVersion <= versionWas ||
                !LauncherLogic.FindLocalGgufs("", cache).Any(g => g.File == Path.Combine(snapshot, "Mini-Q4_K_M.gguf")) || _hubStrip.Visibility == Visibility.Visible)
                return Fail($"downloaded: button {Button()}, menu ✓ [{string.Join(",", checkedItems)}], models version {_localModelsVersion} (was {versionWas})");
            snap("22-hub-downloaded", null);
            // a file Hugging Face does not have; a repo that refuses without a token
            BuildHubVariantMenu(this).Items.OfType<MenuItem>().First(i => i.Tag as string == "hubvariant:Q3_K_S").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            SmokeClick(Find<Button>("HubDownload")!);
            await _hubDlTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (Find<TextBlock>("HubDownloadError")?.Text != "File not found on Hugging Face." || Find<Button>("HubDownloadRetry") is null)
                return Fail("404: " + Find<TextBlock>("HubDownloadError")?.Text);
            await Show(Locked);
            SmokeClick(Find<Button>("HubDownload")!);
            await _hubDlTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (Find<TextBlock>("HubDownloadError")?.Text.Contains("Set the HF_TOKEN environment variable", StringComparison.Ordinal) != true || Find<Button>("HubDownloadRetry") is null)
                return Fail("401: " + Find<TextBlock>("HubDownloadError")?.Text);
            snap("23-hub-download-refused", null);

            // Save to: the cache by default; a chosen folder is flat (folder\Mini-GGUF\<file>) and moves the chip, the menu's
            // check and the button with it (the dot stays: Mini is still in the cache)
            foreach (var dir in new[] { saveDir, modelsDir })
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            await Show(Mini);
            string? Chip() => Find<TextBlock>("HubChip:Folder")?.Text;
            string? SaveTo() => Find<TextBlock>("HubSaveToPath")?.ToolTip as string;
            Hyperlink? Reset() => SmokeFind<Hyperlink>(_hubDetail, h => h.Tag as string == "HubSaveToReset");
            bool Badge() => Dot(Mini) is { Visibility: Visibility.Visible };
            string Marks() => string.Join(",", BuildHubVariantMenu(this).Items.OfType<MenuItem>().Where(i => ((Panel)i.Header).Children.OfType<TextBlock>().Any(t => t.Tag as string == "hubdownloaded")).Select(i => i.Tag as string));
            if (HubTarget() != (cache, HubLayout.HfCache) || SaveTo() != cache || Chip() != HubShortPath(cache) || Reset() is not null || Find<DockPanel>("HubModelsNotice") is not null)
                return Fail($"default Save to: target {HubTarget()}, line {SaveTo()}, chip {Chip()}, reset {Reset() is not null}");
            var replaceAsked = new List<string>();
            (_codeFolderPick, _codeChoice, _settings.ModelsFolder) = (() => saveDir, title => { replaceAsked.Add(title); return replaceAsked.Count == 1 ? null : "replace"; }, modelsDir);
            SmokeClick(Find<Button>("HubSaveToChange")!);
            if (HubTarget() != (saveDir, HubLayout.Flat) || SaveTo() != saveDir || Chip() != HubShortPath(saveDir) || _hubFolderChip.ToolTip as string != "Downloads go to " + saveDir ||
                Reset() is null || !Badge() || Marks() != "" || Button() != "Download True" || AppSettings.Load().HubDownloadFolder != saveDir)
                return Fail($"chosen folder: target {HubTarget()}, line {SaveTo()}, chip {Chip()} ({_hubFolderChip.ToolTip}), badge {Badge()}, marks [{Marks()}], button {Button()}");
            var notice = Find<DockPanel>("HubModelsNotice");
            if (notice?.Children.OfType<TextBlock>().FirstOrDefault()?.Text != $"The model picker looks in {modelsDir}." || Find<Button>("HubUseAsModels") is null)
                return Fail("no Models-folder notice for a folder outside it");
            await Task.Delay(300); // the layout pass the snapshot draws
            snap("24-hub-save-to", null);
            BuildHubVariantMenu(this).Items.OfType<MenuItem>().First(i => i.Tag as string == "hubvariant:Q4_K_M").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            SmokeClick(Find<Button>("HubDownload")!);
            if (_hubSaveTo.IsEnabled)
                return Fail("Save to stays enabled while downloading");
            await _hubDlTask.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(100);
            foreach (var file in new[] { "Mini-Q4_K_M.gguf", "mmproj-F16.gguf" })
                if (Path.Combine(saveDir, "Mini-GGUF", file) is var at && (!File.Exists(at) || !File.ReadAllBytes(at).AsSpan().SequenceEqual(HubFakeHandler.MiniFiles[file])))
                    return Fail($"flat download {file}: missing or different");
            if (!_hubSaveTo.IsEnabled || !Badge() || Marks() != "hubvariant:Q4_K_M" || Button() != "Downloaded ✓ False")
                return Fail($"flat downloaded: save to {_hubSaveTo.IsEnabled}, badge {Badge()}, marks [{Marks()}], button {Button()}");
            // the notice: replacing another Models folder asks first (cancelled: nothing changes), then takes it
            var modelsVersion = _localModelsVersion;
            SmokeClick(Find<Button>("HubUseAsModels")!);
            if (replaceAsked.FirstOrDefault() != "Replace the Models folder?" || _settings.ModelsFolder != modelsDir || _localModelsVersion != modelsVersion || Find<DockPanel>("HubModelsNotice") is null)
                return Fail($"cancelled replacing the Models folder: replaceAsked [{string.Join(", ", replaceAsked)}], folder {_settings.ModelsFolder}");
            SmokeClick(Find<Button>("HubUseAsModels")!);
            if (_settings.ModelsFolder != saveDir || _localModelsVersion <= modelsVersion || Find<DockPanel>("HubModelsNotice") is not null ||
                !LauncherLogic.FindLocalGgufs(saveDir, cache).Any(g => g.File == Path.Combine(saveDir, "Mini-GGUF", "Mini-Q4_K_M.gguf")))
                return Fail($"accepted the Models folder: folder {_settings.ModelsFolder}, version {_localModelsVersion} (was {modelsVersion}), notice {Find<DockPanel>("HubModelsNotice") is not null}");
            // a folder inside the Models folder needs no notice
            var inside = Path.Combine(modelsDir, "hub");
            (_codeFolderPick, _settings.ModelsFolder) = (() => inside, modelsDir);
            SmokeClick(Find<Button>("HubSaveToChange")!);
            if (HubTarget() != (inside, HubLayout.Flat) || Find<DockPanel>("HubModelsNotice") is not null)
                return Fail($"inside the Models folder: target {HubTarget()}, notice {Find<DockPanel>("HubModelsNotice") is not null}");
            // Reset: the cache again, where Mini is downloaded too
            Reset()!.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            if (HubTarget() != (cache, HubLayout.HfCache) || SaveTo() != cache || Chip() != HubShortPath(cache) || Reset() is not null || !Badge() || Button() != "Downloaded ✓ False" ||
                AppSettings.Load().HubDownloadFolder != "")
                return Fail($"reset: target {HubTarget()}, line {SaveTo()}, chip {Chip()}, badge {Badge()}, button {Button()}");
            fake.Downloads = false;

            // Remote API takes its place (and back); New chat leaves it
            _remoteHttp = new HttpClient(new RemoteApiFakeHandler());
            _settings.RemoteHelpShown = true;
            SmokeClick(SmokeFind<Button>(SidebarList, b => b.Tag as string == "+Remote API")!);
            await Task.WhenAll(_remoteModelsTask, _remoteProvidersTask);
            await Task.Delay(400);
            if (ModelHubView.Visibility == Visibility.Visible || RemoteApiView.Visibility != Visibility.Visible)
                return Fail($"Remote API: hub {ModelHubView.Visibility}, remote api {RemoteApiView.Visibility}");
            await OpenFromSidebar();
            if (ModelHubView.Visibility != Visibility.Visible || RemoteApiView.Visibility == Visibility.Visible)
                return Fail($"back to the hub: hub {ModelHubView.Visibility}, remote api {RemoteApiView.Visibility}");
            SmokeClick(SmokeFind<Button>(SidebarList, b => b.Tag as string == "+New")!);
            await Task.Delay(400);
            if (ModelHubView.Visibility == Visibility.Visible || (CodeView.Visibility != Visibility.Visible && ChatView.Visibility != Visibility.Visible))
                return Fail($"New chat left hub {ModelHubView.Visibility}, code {CodeView.Visibility}, chat {ChatView.Visibility}");
            return true;
        }
        finally
        {
            CloseModelHub();
            _hubDlCts?.Cancel();
            (HubHttpHandlerFactory, HubVramGiBOverride, HubRamGiBOverride, _openExternal, _hubHttp, _hubDlHttp) = (factory, vramWas, ramWas, open, null, null);
            _hubTextCache.Clear();
            (_settings.HubSort, _settings.HubFitsGpu, _remoteHttp, _settings.RemoteHelpShown) = (sortWas, fitWas, remoteHttp, helpShown);
            (_settings.HubKinds, _settings.HubBrands) = (kindsWas, brandsWas);
            (_settings.HubDownloadFolder, _settings.ModelsFolder, _codeFolderPick, _codeChoice) = (hubFolderWas, modelsWas, pickWas, choiceWas);
            _settings.Save();
            foreach (var dir in new[] { saveDir, modelsDir, onDiskDir, tinyCached })
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            _hubPageCache.Clear();
            Environment.SetEnvironmentVariable("HF_HUB_CACHE", cacheWas);
        }
    }
}

// Canned /api/models replies for --ui-smoke and --screenshots, whatever the query: a big unsloth model (gated), a small one
// from an owner with no logo, and a diffusion repo the hub drops. Paged: the first page links to a second (one row repeated,
// one new). Status: every reply fails with it. Records every address asked.
internal sealed class HubFakeHandler : HttpMessageHandler
{
    public const string Qwen = "unsloth/Qwen3.8-480B-GGUF", Tiny = "zyxlab/Tiny-1B-GGUF", PageTwo = "zyxlab/Page-Two-GGUF";
    public const string NextUrl = "https://huggingface.co/api/models?filter=gguf&cursor=2";
    public List<string> Seen { get; } = [];
    public HttpStatusCode Status = HttpStatusCode.OK;
    public bool Paged;

    private const string QwenJson = """{"id":"unsloth/Qwen3.8-480B-GGUF","downloads":61000,"likes":1400,"lastModified":"2026-11-01T00:00:00.000Z","createdAt":"2026-10-20T00:00:00.000Z","trendingScore":120,"gguf":{"total":480000000000},"pipeline_tag":"text-generation","gated":"manual","tags":["gguf"]}""";
    private const string Models = "[" + QwenJson + """,{"id":"zyxlab/Tiny-1B-GGUF","downloads":1300000,"likes":12,"lastModified":"2026-10-01T00:00:00.000Z","createdAt":"2026-09-01T00:00:00.000Z","trendingScore":3,"gguf":{"total":1000000000},"pipeline_tag":"text-generation","tags":["gguf"]},{"id":"art/flux-gguf","library_name":"diffusers","pipeline_tag":"text-to-image","tags":["gguf"],"downloads":5,"likes":1}]""";
    private const string SecondPage = "[" + QwenJson + """,{"id":"zyxlab/Page-Two-GGUF","downloads":10,"likes":1,"lastModified":"2026-08-01T00:00:00.000Z","gguf":{"total":2000000000},"tags":["gguf"]}]""";

    // One repo's files (?blobs=true): Qwen has a sharded variant, three plain ones and an mmproj; Tiny one plain variant;
    // Page-Two no GGUF at all. Only Qwen has a README (with front matter and HTML to clean); the others 404.
    public const string QwenBlobs = """{"sha":"qwensha","siblings":[{"rfilename":"README.md","size":900},{"rfilename":"UD-Q4_K_XL/Qwen3.8-480B-UD-Q4_K_XL-00001-of-00002.gguf","size":3000000000},{"rfilename":"UD-Q4_K_XL/Qwen3.8-480B-UD-Q4_K_XL-00002-of-00002.gguf","size":2000000000},{"rfilename":"Qwen3.8-480B-Q2_K.gguf","size":1500000000},{"rfilename":"Qwen3.8-480B-Q3_K_M.gguf","size":2100000000},{"rfilename":"Qwen3.8-480B-BF16.gguf","size":20000000000},{"rfilename":"mmproj-BF16.gguf","size":600000000}]}""";
    private const string TinyBlobs = """{"sha":"tinysha","siblings":[{"rfilename":"Tiny-1B-Q8_0.gguf","size":1100000000}]}""";
    private const string NoGgufBlobs = """{"sha":"p2","siblings":[{"rfilename":"README.md","size":10},{"rfilename":"model.safetensors","size":2000000000}]}""";
    public const string QwenReadme = "---\nlicense: apache-2.0\ntags: [gguf]\n---\n<div align=\"center\">\n<img src=\"logo.png\">\n</div>\n\n# Qwen3.8 480B\n\nRun it with **llama.cpp** on your own machine.\n";
    // Set: Qwen's ?blobs=true reply waits for it, ignoring cancellation (a reply already on its way).
    public TaskCompletionSource? Hold;

    // Downloads: the listing adds Mini (Q4_K_M, Q2_K, an mmproj, and a Q3_K_S whose file 404s) and Locked (401). The hub
    // answers like huggingface.co (302 to cdn.test with X-Linked-Etag); cdn.test serves the bytes slowly, with Range. Gate set:
    // a file stops half-way (AtGate completes) until the gate opens or the read is cancelled.
    public const string Mini = "zyxlab/Mini-GGUF", Locked = "zyxlab/Locked-GGUF";
    public bool Downloads;
    public TaskCompletionSource? Gate;
    public readonly TaskCompletionSource AtGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static readonly Dictionary<string, byte[]> MiniFiles = new()
    {
        ["Mini-Q4_K_M.gguf"] = SeededBytes(3_000_000, 1), ["Mini-Q2_K.gguf"] = SeededBytes(1_000_000, 2), ["mmproj-F16.gguf"] = SeededBytes(500_000, 3),
    };
    private static readonly Dictionary<string, byte[]> Cdn = MiniFiles.Values.ToDictionary(Sha256);
    private const string DownloadRows = """,{"id":"zyxlab/Mini-GGUF","downloads":50,"likes":2,"lastModified":"2026-07-01T00:00:00.000Z","gguf":{"total":3000000},"pipeline_tag":"text-generation","tags":["gguf"]},{"id":"zyxlab/Locked-GGUF","downloads":40,"likes":1,"lastModified":"2026-07-01T00:00:00.000Z","gguf":{"total":1000},"gated":"manual","pipeline_tag":"text-generation","tags":["gguf"]}]""";
    private static readonly string MiniBlobs = """{"sha":"minisha","siblings":[""" + string.Join(",", MiniFiles.Select(f => $$"""{"rfilename":"{{f.Key}}","size":{{f.Value.Length}}}"""))
        + """,{"rfilename":"Mini-Q3_K_S.gguf","size":800000}]}""";
    private const string LockedBlobs = """{"sha":"lockedsha","siblings":[{"rfilename":"Locked-Q4_0.gguf","size":1000}]}""";

    // BrandRows: the listing adds repos named for a maker that another account uploaded (and LiquidAI, whose account has no logo).
    public const string DeepSeek = "ISTA-DASLab/DeepSeek-V4-Flash-GGUF", Huihui = "huihui-ai/Huihui-Qwen3.8-30B-GGUF", Liquid = "LiquidAI/LFM3-8B-GGUF";
    public bool BrandRows;
    private const string BrandRowsJson = """,{"id":"ISTA-DASLab/DeepSeek-V4-Flash-GGUF","downloads":30,"likes":3,"pipeline_tag":"text-generation","tags":["gguf"]},{"id":"huihui-ai/Huihui-Qwen3.8-30B-GGUF","downloads":20,"likes":2,"pipeline_tag":"text-generation","tags":["gguf"]},{"id":"LiquidAI/LFM3-8B-GGUF","downloads":10,"likes":1,"pipeline_tag":"text-generation","tags":["gguf"]}]""";

    public static string Sha256(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
    private static byte[] SeededBytes(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        lock (Seen)
        {
            Seen.Add(url);
            if (request.Headers.Range is { } range)
                Seen.Add("Range: " + range);
        }
        if (Status != HttpStatusCode.OK)
            return new HttpResponseMessage(Status) { Content = new StringContent("") };
        var path = request.RequestUri.AbsolutePath;
        if (request.RequestUri.Host == "cdn.test")
        {
            var data = Cdn[path[1..]];
            var from = (int)(request.Headers.Range?.Ranges.First().From ?? 0);
            var ok = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(new Slow(this, data[from..], from, data.Length / 2)) };
            if (from > 0)
                ok.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            return ok;
        }
        if (path.Contains("/resolve/", StringComparison.Ordinal) && !path.EndsWith("/resolve/main/README.md", StringComparison.Ordinal))
        {
            if (path.StartsWith("/" + Locked + "/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (!MiniFiles.TryGetValue(Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]), out var file))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("https://cdn.test/" + Sha256(file));
            moved.Headers.Add("X-Linked-Etag", $"\"{Sha256(file)}\"");
            return moved;
        }
        if (path.StartsWith("/api/models/", StringComparison.Ordinal) || path.EndsWith("/resolve/main/README.md", StringComparison.Ordinal))
        {
            if (Hold is { } hold && path == "/api/models/" + Qwen)
                await hold.Task;
            var body = path switch
            {
                "/api/models/" + Qwen => QwenBlobs,
                "/api/models/" + Tiny => TinyBlobs,
                "/api/models/" + PageTwo => NoGgufBlobs,
                "/api/models/" + Mini => MiniBlobs,
                "/api/models/" + Locked => LockedBlobs,
                "/" + Qwen + "/resolve/main/README.md" => QwenReadme,
                _ => null,
            };
            return new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(body ?? "") };
        }
        var second = url == NextUrl;
        var listing = Downloads ? Models[..^1] + DownloadRows : Models;
        if (BrandRows)
            listing = listing[..^1] + BrandRowsJson;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(second ? SecondPage : listing) };
        if (Paged && !second)
            response.Headers.TryAddWithoutValidation("Link", $"<{NextUrl}>; rel=\"next\"");
        return response;
    }

    // 64 KB per 20 ms read (so the meter measures a speed); past half the file it waits for an open Gate.
    private sealed class Slow(HubFakeHandler hub, byte[] rest, long start, long half) : MemoryStream(rest, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            if (hub.Gate is { } gate && start + Position >= half)
            {
                hub.AtGate.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken);
            }
            return Read(buffer.Span[..Math.Min(buffer.Length, 1 << 16)]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}

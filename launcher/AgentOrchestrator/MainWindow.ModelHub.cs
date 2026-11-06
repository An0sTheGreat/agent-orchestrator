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
using HubModel = AgentOrchestrator.HubLogic.HubModel;

namespace AgentOrchestrator;

// The Models Hub page (the sidebar's Models Hub, under Remote API), in the centre column in place of Code and Chat: search
// Hugging Face's GGUF models (HubLogic: URLs, paging, fuzzy filter, ranking), sort them, and see one in the detail pane.
// docs/model-hub-findings.md has the facts and the plan.
public partial class MainWindow
{
    // --ui-smoke and --screenshots put a fake handler here (no live network in any test); null: the real one.
    internal static Func<HttpMessageHandler>? HubHttpHandlerFactory;
    internal static double? HubVramGiBOverride; // --ui-smoke: the GPU memory the fit filter assumes

    private sealed record HubMachine(double? VramGiB, double RamGiB, HashSet<string> Cached);

    private const double HubGiB = 1024.0 * 1024 * 1024;
    private const int HubMaxPages = 10, HubEnoughRows = 8;
    private static readonly TimeSpan HubCacheFor = TimeSpan.FromMinutes(5);

    private HttpClient? _hubHttp;
    private HttpClient HubHttp => _hubHttp ??= NewHubHttp();
    private readonly Dictionary<(string Query, string Sort), (DateTime At, List<HubModel> Rows, Uri? Next)> _hubPageCache = new();
    private Task<HubMachine> _hubMachine = Task.FromResult(new HubMachine(null, 0, []));
    private CancellationTokenSource? _hubCts;
    private Task _hubTask = Task.CompletedTask; // smokes await it
    private DispatcherTimer? _hubDebounce;
    private (string Query, string Sort) _hubKey = ("", "trending");
    private List<HubModel> _hubAll = [], _hubShown = [];
    private Uri? _hubNext;
    private int _hubPages;
    private bool _hubLoadingMore;
    private HubModel? _hubSelected;
    private TextBox _hubSearch = new();
    private StackPanel _hubList = new(), _hubStatus = new();
    private TextBlock _hubFooter = new(), _hubSortLabel = new();
    private Border _hubDetail = new();

    private static HttpClient NewHubHttp()
    {
        var http = new HttpClient(HubHttpHandlerFactory?.Invoke() ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrchestrator");
        return http;
    }

    private void OpenModelHub()
    {
        CloseRemoteApi();
        CloseModelHub();
        if (!HubLogic.Sorts.Any(s => s.Id == _settings.HubSort))
            _settings.HubSort = "trending";
        _hubMachine = HubReadMachineAsync();
        _hubSelected = null;

        // ---- header: title, subtitle, this machine
        var title = new TextBlock { Text = "Model hub", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 22, FontWeight = FontWeights.SemiBold };
        var intro = RemoteMuted("Discover and download models locally.");
        intro.Margin = new Thickness(0, 4, 0, 0);
        var vram = HubChip("VRAM", "…", "GPU memory");
        var ram = HubChip("RAM", "…", "System memory");
        var cpu = HubChip("CPU", Environment.ProcessorCount + " threads", "Logical processors");
        var folder = LauncherLogic.HubCache();
        var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 2, 0, 0) };
        foreach (var chip in new[] { vram, ram, cpu, HubChip("Folder", HubShortPath(folder), "Downloads go to " + folder) })
            chips.Children.Add(chip);
        DockPanel.SetDock(chips, Dock.Right);
        _hubMachine.ContinueWith(t =>
        {
            ((TextBlock)vram.Tag).Text = t.Result.VramGiB is { } v ? $"{v:0} GB" : "n/a";
            ((TextBlock)ram.Tag).Text = $"{t.Result.RamGiB:0} GB";
        }, TaskScheduler.FromCurrentSynchronizationContext());

        // ---- search box (with a clear button) and the sort pill
        _hubSearch = new TextBox { Tag = "HubSearch", Height = 34, Padding = new Thickness(32, 0, 32, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var glass = new TextBlock { Text = "\uE721", FontSize = 12, Margin = new Thickness(13, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        glass.SetResourceReference(StyleProperty, "RailGlyph");
        glass.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var hint = RemoteMuted("Search GGUF models on Hugging Face");
        (hint.IsHitTestVisible, hint.VerticalAlignment, hint.TextWrapping, hint.Margin) = (false, VerticalAlignment.Center, TextWrapping.NoWrap, new Thickness(36, 0, 0, 0));
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
        _hubSortLabel = new TextBlock { Tag = "HubSortLabel", VerticalAlignment = VerticalAlignment.Center };
        var sortButton = new Button { Tag = "HubSort", Style = (Style)FindResource("GhostButton"), Height = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(10, 0, 0, 0), Content = _hubSortLabel };
        sortButton.SetResourceReference(BackgroundProperty, "Card");
        sortButton.SetResourceReference(ForegroundProperty, "TextPrimary");
        sortButton.Click += (_, _) => BuildHubSortMenu(sortButton).IsOpen = true;
        HubSortLabel();
        DockPanel.SetDock(sortButton, Dock.Right);
        var searchRow = new DockPanel { Margin = new Thickness(0, 20, 0, 0), Children = { sortButton, new Grid { Children = { _hubSearch, glass, hint, clear } } } };

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
        var detailScroll = new ScrollViewer { Content = _hubDetail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new Grid
        {
            Margin = new Thickness(0, 16, 0, 0),
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 300 }, new ColumnDefinition { Width = new GridLength(16) }, new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star) } },
        };
        Grid.SetColumn(detailScroll, 2);
        body.Children.Add(results);
        body.Children.Add(detailScroll);

        var page = new Grid
        {
            Tag = "ModelHubPage", MaxWidth = 1400, Margin = new Thickness(36, 28, 36, 28),
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition() },
        };
        var header = new DockPanel { Children = { chips, new StackPanel { Children = { title, intro } } } };
        Grid.SetRow(searchRow, 1);
        Grid.SetRow(body, 2);
        page.Children.Add(header);
        page.Children.Add(searchRow);
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
        HubSearch();
    }

    private void CloseModelHub()
    {
        _hubCts?.Cancel();
        _hubDebounce?.Stop();
        Reveal(ModelHubView, false);
    }

    // VRAM (the override, else the first GPU's, read once per run off the UI thread), RAM, and the repos already in the cache.
    private static async Task<HubMachine> HubReadMachineAsync()
    {
        double? vram = HubVramGiBOverride;
        if (vram is null && await (_gpuMemory ??= Task.Run(LauncherLogic.GpuMemoryBytes)) is { } bytes and > 0)
            vram = bytes / HubGiB;
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / HubGiB;
        var cached = await Task.Run(() =>
        {
            try
            {
                return Directory.EnumerateDirectories(LauncherLogic.HubCache(), "models--*")
                    .Select(d => Path.GetFileName(d)["models--".Length..].Replace("--", "/")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return new HashSet<string>(); }
        });
        return new HubMachine(vram, ram, cached);
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

    // The sorts (the current one checked), then "Only show models that fit". --ui-smoke clicks its items without opening it.
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
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        var fit = PresetItem("Only show models that fit", () =>
        {
            _settings.HubOnlyFit = !_settings.HubOnlyFit;
            _settings.Save();
            HubFillRows();
        });
        (fit.IsChecked, fit.Tag, fit.ToolTip) = (_settings.HubOnlyFit, "HubOnlyFit", "Hides models too large for this computer's GPU and RAM; models already downloaded stay.");
        menu.Items.Add(fit);
        return menu;
    }

    // ---- search, paging, cache

    private void HubSearch(bool fresh = false)
    {
        _hubDebounce?.Stop();
        _hubCts?.Cancel();
        var cts = _hubCts = new CancellationTokenSource();
        _hubTask = HubSearchAsync(_hubSearch.Text.Trim(), _settings.HubSort, fresh, cts.Token);
    }

    // The first page: each query variant in turn (QueryVariants) until enough rows match; "latest" also asks for the trending
    // ones (Rank re-orders the union). Paging follows the first request's Link header. Cached per (query, sort) for 5 minutes.
    private async Task HubSearchAsync(string query, string sort, bool fresh, CancellationToken ct)
    {
        _hubKey = (query, sort);
        _hubNext = null;
        _hubFooter.Visibility = Visibility.Collapsed;
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
                foreach (var variant in query.Length == 0 ? [""] : HubLogic.QueryVariants(query))
                {
                    foreach (var s in sorts)
                    {
                        var (page, link) = await HubFetchAsync(HubLogic.SearchUrl(variant, s), ct);
                        if (first)
                            (next, first) = (link, false);
                        HubMerge(rows, page);
                    }
                    var snapshot = rows.ToList();
                    if (await Task.Run(() => HubLogic.Rank(HubLogic.FuzzyFilter(snapshot, query), query, sort, DateTimeOffset.UtcNow).Count, ct) >= HubEnoughRows)
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
        var (all, (query, sort)) = (_hubAll.ToList(), _hubKey);
        var shown = await Task.Run(() => HubLogic.Rank(HubLogic.FuzzyFilter(all, query), query, sort, DateTimeOffset.UtcNow), ct);
        await _hubMachine; // the fit filter needs it
        ct.ThrowIfCancellationRequested();
        _hubShown = shown;
        HubFillRows();
    }

    // The rows of _hubShown, less those that do not fit when "Only show models that fit" is on (downloaded ones stay).
    // ponytail: rebuilds every row on each page (up to 10 x 100); a virtualizing list if that ever drags.
    private void HubFillRows()
    {
        var machine = _hubMachine.IsCompletedSuccessfully ? _hubMachine.Result : null;
        var rows = _settings.HubOnlyFit && machine is not null
            ? _hubShown.Where(m => machine.Cached.Contains(m.Id) || HubLogic.ModelFits(m, machine.VramGiB, machine.RamGiB)).ToList()
            : _hubShown;
        _hubList.Children.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var m in rows)
            _hubList.Children.Add(HubRow(m, now));
        var query = _hubKey.Query;
        HubStatus(rows.Count > 0 ? "" : (query.Length == 0 ? "No GGUF models found" : $"No GGUF models match “{query}”")
            + (rows.Count == 0 && rows.Count < _hubShown.Count ? " that fit this computer" : ""));
        // a short list does not scroll: keep loading until it fills (or the pages run out)
        Dispatcher.BeginInvoke(HubLoadMore, DispatcherPriority.Background);
    }

    private void HubStatus(string text)
    {
        _hubStatus.Children.Clear();
        _hubStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (text.Length > 0)
            _hubStatus.Children.Add(RemoteMuted(text));
    }

    private void HubError(Exception e)
    {
        var text = e switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Hugging Face is rate limiting; wait a moment, then retry.",
            HttpRequestException { StatusCode: { } code } => $"Hugging Face answered {(int)code} ({code}).",
            TaskCanceledException => "Hugging Face did not answer within 15 seconds.",
            JsonException => "Hugging Face sent a reply that could not be read.",
            _ => "Could not reach Hugging Face: " + e.Message,
        };
        var message = new TextBlock { Text = text, Tag = "HubError", FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A)) };
        var retry = new Button { Content = "Retry", Tag = "HubRetry", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(10, 0, 0, 0) };
        retry.Click += (_, _) => HubSearch(fresh: true);
        DockPanel.SetDock(retry, Dock.Right);
        _hubStatus.Children.Clear();
        _hubStatus.Children.Add(new DockPanel { Children = { retry, message } });
        _hubStatus.Visibility = Visibility.Visible;
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

    // The maker's logo (RemoteBadge) when it has one, else its initial on a colour from its name.
    private static FrameworkElement HubBadge(HubModel m)
    {
        if (RemoteVendorIcon(m.Id) is { } icon && VendorIconImage(icon) is not null)
            return RemoteBadge(m.Id);
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
        var badge = HubBadge(m);
        badge.VerticalAlignment = VerticalAlignment.Center;
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
        _hubDetail.Child = BuildHubDetail(_hubSelected);
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
        var pill = new Border { Tag = "HubTag:" + text, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(10), Child = t };
        pill.SetResourceReference(Border.BackgroundProperty, "Hover");
        return pill;
    }

    // The selected model's header: logo, name, owner, tags, counts and its Hugging Face page.
    // The download step replaces this with the full pane (model card, weights and downloads).
    private FrameworkElement BuildHubDetail(HubModel model)
    {
        var name = new TextBlock { Text = model.Name, Tag = "HubDetailName", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var owner = RemoteMuted(model.Owner);
        var badge = new Viewbox { Width = 40, Height = 40, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top, Child = HubBadge(model) };
        DockPanel.SetDock(badge, Dock.Left);
        var tags = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (model.PipelineTag is { Length: > 0 } pipeline)
            tags.Children.Add(HubPill(pipeline));
        tags.Children.Add(HubPill("GGUF"));
        if (model.Gated)
            tags.Children.Add(HubPill("Gated", new SolidColorBrush(Color.FromRgb(0xD9, 0x9A, 0x2B))));
        var counts = RemoteMuted($"♡ {HubLogic.FormatCount(model.Likes)} likes     ↓ {HubLogic.FormatCount(model.Downloads)} downloads"
            + (model.LastModified is null ? "" : "     Updated " + HubLogic.FormatAge(model.LastModified, DateTimeOffset.UtcNow)));
        counts.Tag = "HubDetailCounts";
        var url = "https://huggingface.co/" + model.Id;
        var link = new Hyperlink(new Run("Open on Hugging Face")) { Tag = "HubOpenHf", ToolTip = url };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentLight");
        link.Click += (_, _) => _openExternal(url);
        return new StackPanel
        {
            Tag = "HubDetail:" + model.Id,
            Children =
            {
                new DockPanel { Children = { badge, new StackPanel { Children = { name, owner } } } },
                tags,
                counts,
                new TextBlock { Margin = new Thickness(0, 12, 0, 0), FontSize = 12.5, Inlines = { link } },
            },
        };
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
        var (factory, vramWas, open, sortWas, fitWas, cacheWas) = (HubHttpHandlerFactory, HubVramGiBOverride, _openExternal, _settings.HubSort, _settings.HubOnlyFit, Environment.GetEnvironmentVariable("HF_HUB_CACHE"));
        var (remoteHttp, helpShown) = (_remoteHttp, _settings.RemoteHelpShown);
        var opened = new List<string>();
        var cache = Path.Combine(AppSettings.DataRoot, "hub-smoke-cache");
        string Url(string query, string sort) => HubLogic.SearchUrl(query, sort).AbsoluteUri;
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
            _openExternal = opened.Add;
            Environment.SetEnvironmentVariable("HF_HUB_CACHE", cache);
            (_settings.HubSort, _settings.HubOnlyFit) = ("trending", false);

            if (!await OpenFromSidebar())
                return Fail("no Models Hub right under Remote API in the sidebar");
            if (ModelHubView.Visibility != Visibility.Visible || CodeView.Visibility == Visibility.Visible || ChatView.Visibility == Visibility.Visible || RemoteApiView.Visibility == Visibility.Visible)
                return Fail($"page {ModelHubView.Visibility}, code {CodeView.Visibility}, chat {ChatView.Visibility}, remote api {RemoteApiView.Visibility}");
            if (Rows() != $"{Qwen}, {Tiny}" || !fake.Seen.Contains(Url("", "trending")) || fake.Seen.Any(u => !u.Contains("filter=gguf")))
                return Fail($"rows [{Rows()}] from [{string.Join(", ", fake.Seen)}]");
            var stats = HubRows().Select(r => SmokeFind<TextBlock>(r, t => t.Tag as string == "hubstats")?.Text).ToList();
            if (stats[0] != "♡ 1.4K   ↓ 61K" || stats[1] != "♡ 12   ↓ 1.3M")
                return Fail("row counts: " + string.Join(" / ", stats));
            if (SmokeFind<Border>(HubRows()[0], b => b.Tag as string == "hubtile:U") is null || SmokeFind<Border>(HubRows()[1], b => b.Tag as string == "hubtile:Z") is null)
                return Fail("the owners' initial tiles");
            if (Find<TextBlock>("HubChip:VRAM")?.Text != "4 GB" || Find<TextBlock>("HubChip:CPU")?.Text != Environment.ProcessorCount + " threads" || Find<TextBlock>("HubSortLabel")?.Text != "↑↓  Trending  ▾")
                return Fail($"header: VRAM {Find<TextBlock>("HubChip:VRAM")?.Text}, CPU {Find<TextBlock>("HubChip:CPU")?.Text}, sort {Find<TextBlock>("HubSortLabel")?.Text}");

            // a row fills the detail; Down moves the checked row, Enter shows it
            SmokeClick(HubRows()[0]);
            await Task.Delay(200);
            if (SmokeFind<StackPanel>(_hubDetail, p => p.Tag as string == "HubDetail:" + Qwen) is null || SmokeFind<TextBlock>(_hubDetail, t => t.Tag as string == "HubDetailName")?.Text != "Qwen3.8-480B-GGUF" ||
                SmokeFind<Border>(_hubDetail, b => b.Tag as string == "HubTag:Gated") is null || SmokeFind<Border>(_hubDetail, b => b.Tag as string == "HubTag:text-generation") is null ||
                SmokeFind<TextBlock>(_hubDetail, t => t.Tag as string == "HubDetailCounts")?.Text.StartsWith("♡ 1.4K likes", StringComparison.Ordinal) != true)
                return Fail("the detail of the clicked row");
            snap("20-model-hub", null);
            SmokeFind<Hyperlink>(_hubDetail, h => h.Tag as string == "HubOpenHf")?.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            if (!opened.SequenceEqual(["https://huggingface.co/" + Qwen]))
                return Fail("Open on Hugging Face: " + string.Join(", ", opened));
            if (!HubKey(Key.Down, false) || HubRows()[1].IsChecked != true || HubRows()[0].IsChecked == true || _hubSelected?.Id != Qwen)
                return Fail("Down did not move to the next row (or changed the detail)");
            if (!HubKey(Key.Enter, false) || _hubSelected?.Id != Tiny || SmokeFind<StackPanel>(_hubDetail, p => p.Tag as string == "HubDetail:" + Tiny) is null)
                return Fail("Enter did not show the row");

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

            // only models that fit: 4 GB of VRAM hides the 480B; once downloaded it stays
            await Menu("HubOnlyFit");
            if (Rows() != Tiny || !AppSettings.Load().HubOnlyFit || BuildHubSortMenu(this).Items.OfType<MenuItem>().Single(i => i.Tag as string == "HubOnlyFit").IsChecked != true)
                return Fail($"fit filter: rows [{Rows()}]");
            Directory.CreateDirectory(Path.Combine(cache, "models--unsloth--Qwen3.8-480B-GGUF"));
            await OpenFromSidebar();
            if (!Rows().Contains(Qwen))
                return Fail($"a downloaded model was hidden by the fit filter: [{Rows()}]");
            await Menu("HubOnlyFit");

            // paging: the Link header's next page is fetched while the list is short, its rows appended once each
            fake.Paged = true;
            _hubPageCache.Clear();
            await Menu("hubsort:trending");
            fake.Paged = false;
            if (Rows() != $"{Qwen}, {Tiny}, {HubFakeHandler.PageTwo}" || !fake.Seen.Contains(HubFakeHandler.NextUrl))
                return Fail($"paging: rows [{Rows()}]");

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
            (HubHttpHandlerFactory, HubVramGiBOverride, _openExternal, _hubHttp) = (factory, vramWas, open, null);
            (_settings.HubSort, _settings.HubOnlyFit, _remoteHttp, _settings.RemoteHelpShown) = (sortWas, fitWas, remoteHttp, helpShown);
            _settings.Save();
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

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        lock (Seen)
            Seen.Add(url);
        if (Status != HttpStatusCode.OK)
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("") });
        var second = url == NextUrl;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(second ? SecondPage : Models) };
        if (Paged && !second)
            response.Headers.TryAddWithoutValidation("Link", $"<{NextUrl}>; rel=\"next\"");
        return Task.FromResult(response);
    }
}

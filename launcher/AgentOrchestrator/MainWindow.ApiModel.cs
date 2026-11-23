using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AgentOrchestrator;

// Model Settings' API Model: the Remote API page's favorites one pick away. A pick asks which providers to use (OpenRouter's;
// another service just confirms), then puts the worker on that model as the page's Apply does.
public partial class MainWindow
{
    // The smoke answers the provider window here instead of showing it.
    internal Func<RemoteCatalog.Service, string, LauncherLogic.RemoteRole?, LauncherLogic.RemoteRole?>? _askProvidersTest;

    // A favorite as a worker's remote settings: the service's address and key variable, the providers in the order picked
    // ("" = the service chooses, with fallbacks).
    internal static LauncherLogic.RemoteRole FavoriteRole(RemoteCatalog.Service service, string model, IEnumerable<string> providers) =>
        new(service.BaseUrl, model, service.KeyEnv, "", string.Join(", ", providers));

    private FrameworkElement ApiModelPicker(RoleCard card, LauncherLogic.RemoteRole? remoteNow)
    {
        var favorites = RemoteFavoritesKnown(_settings.RemoteFavorites);
        var box = new ComboBox { Tag = "ApiModel", Margin = new Thickness(0, 6, 0, 0), IsEnabled = favorites.Count > 0 };
        var none = new ComboBoxItem { Content = favorites.Count == 0 ? "No favorites yet" : "Choose a favorite…", Tag = "" };
        box.Items.Add(none);
        foreach (var f in favorites)
        {
            var (sid, mid) = RemoteFavoriteParts(f)!.Value;
            var text = new TextBlock { Text = $"{mid}  ·  {RemoteCatalog.Services.First(s => s.Id == sid).Name}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            box.Items.Add(new ComboBoxItem { Tag = f, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { RemoteBadge(mid), text } } });
        }
        // The worker's remote model shows when it is a favorite.
        var current = remoteNow is not null && RemoteCatalog.ServiceFor(remoteNow.BaseUrl) is { } now ? RemoteFavorite(now.Id, remoteNow.Model.Trim()) : null;
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? none;
        var shown = box.SelectedItem;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem == shown)
                return;
            if (box.SelectedItem is ComboBoxItem { Tag: string f } && RemoteFavoriteParts(f) is { } parts &&
                ChooseApiModel(card, RemoteCatalog.Services.First(s => s.Id == parts.Service), parts.Model, remoteNow))
                shown = box.SelectedItem; // applied: the page rebuilds with it
            else
                Dispatcher.BeginInvoke(() => box.SelectedItem = shown); // cancelled or refused: back to what it showed
        };
        var label = new TextBlock { Text = "API Model", FontWeight = FontWeights.SemiBold, FontSize = 12 };
        var hint = new TextBlock { FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
            Text = favorites.Count == 0 ? "Star models on the Remote API page to list them here." : "Your Remote API favorites: pick one to run this worker on it." };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        hint.Margin = new Thickness(0, 4, 0, 0);
        return new StackPanel { Tag = "ApiModelBlock", Children = { label, box, hint } };
    }

    // The provider window, then the write; false when cancelled or refused (the dropdown goes back).
    private bool ChooseApiModel(RoleCard card, RemoteCatalog.Service service, string model, LauncherLogic.RemoteRole? remoteNow)
    {
        if (CodeBusy)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            return false;
        }
        var remote = _askProvidersTest is { } ask ? ask(service, model, remoteNow) : AskProviders(service, model, remoteNow);
        if (remote is null || !DiscardWorkerEdits())
            return false;
        if (ApplyRemoteRole(card, remote, "on the Remote API page") is { } problem)
        {
            Toast($"{card.Title}: {problem}", "#FF667A");
            return false;
        }
        return true;
    }

    private LauncherLogic.RemoteRole? AskProviders(RemoteCatalog.Service service, string model, LauncherLogic.RemoteRole? remoteNow)
    {
        var (dialog, result) = BuildProviderDialog(service, model, remoteNow);
        dialog.ShowDialog();
        return result();
    }

    private static readonly System.Windows.Media.Brush DealGreen = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0xBF, 0x8A)); // the same green as PrivacyShield's Private

    // A price; with a discount, the listed price struck through and what is paid (listed x (1 - discount)) in green.
    private static FrameworkElement PriceCell(decimal? perMillion, decimal? discount)
    {
        if (perMillion is not { } listed || discount is not > 0)
            return RemoteMuted(RemotePrice(perMillion));
        var was = RemoteMuted(RemotePrice(listed));
        was.TextDecorations = TextDecorations.Strikethrough;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { was, new TextBlock { Text = RemotePrice(listed * (1 - discount)), Foreground = DealGreen, Margin = new Thickness(5, 0, 0, 0) } } };
    }

    // Pick providers (tried in the order ticked, no fallbacks), Default (none: the service chooses), or Cancel (null). The
    // worker's own providers start ticked when it already runs this model. OpenRouter's providers load in the background.
    internal (DialogShell Dialog, Func<LauncherLogic.RemoteRole?> Result) BuildProviderDialog(RemoteCatalog.Service service, string model, LauncherLogic.RemoteRole? remoteNow)
    {
        var picked = remoteNow?.Model.Trim() == model ? remoteNow.Provider.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() : [];
        LauncherLogic.RemoteRole? chosen = null;
        var message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left,
            Text = service.HasProviders
                ? $"Pick the providers {model} may use. They are tried in the order you tick them, with no fallbacks. Or keep the default and let {service.Name} choose."
                : $"{service.Name} has no provider choice: {model} runs where the service decides.",
        };
        var status = RemoteMuted(service.HasProviders ? "Loading providers…" : "");
        status.Margin = new Thickness(0, 8, 0, 0);
        status.Tag = "ProvidersStatus";
        var list = new StackPanel { Tag = "ProvidersList" };
        // name+tag, In, Out, Cache, Latency, tok/s, Uptime, Vision, order mark (the heads follow the Remote API page's table)
        Grid Columns() => new() { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(105) }, new ColumnDefinition { Width = new GridLength(105) }, new ColumnDefinition { Width = new GridLength(105) },
            new ColumnDefinition { Width = new GridLength(62) }, new ColumnDefinition { Width = new GridLength(52) }, new ColumnDefinition { Width = new GridLength(58) }, new ColumnDefinition { Width = new GridLength(76) }, new ColumnDefinition { Width = new GridLength(28) } } };
        var header = Columns();
        header.Margin = new Thickness(12, 8, 12, 0);
        header.Visibility = service.HasProviders ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (text, column) in new[] { "Provider", "In $/M", "Out $/M", "Cache $/M", "Latency", "tok/s", "Uptime", "Vision" }.Select((t, i) => (t, i)))
        {
            var head = RemoteMuted(text);
            Grid.SetColumn(head, column);
            header.Children.Add(head);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 2, 0, 0),
            Visibility = service.HasProviders ? Visibility.Visible : Visibility.Collapsed };
        var use = new Button { Content = "Use selected", Tag = "ProvidersUse", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 5, 16, 5), IsEnabled = false,
            Visibility = service.HasProviders ? Visibility.Visible : Visibility.Collapsed };
        var auto = new Button { Content = service.HasProviders ? "Default (any provider)" : "Use this model", Tag = "ProvidersDefault", Style = (Style)FindResource(service.HasProviders ? "GhostButton" : "PrimaryButton"),
            Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Tag = "ProvidersCancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, auto, use } };
        var dialog = new DialogShell(this, $"Providers for {model}", new StackPanel { MinWidth = 820, Children = { message, status, header, scroll, buttons } });
        var orderMarks = new Dictionary<string, TextBlock>();
        void Marks()
        {
            foreach (var (tag, mark) in orderMarks)
                mark.Text = picked.IndexOf(tag) is >= 0 and var at ? $"#{at + 1}" : "";
            use.IsEnabled = picked.Count > 0;
            use.Content = picked.Count > 0 ? $"Use {picked.Count} selected" : "Use selected";
        }
        void ShowRows(List<RemoteCatalog.ProviderRow> rows)
        {
            list.Children.Clear();
            orderMarks.Clear();
            picked.RemoveAll(t => rows.All(r => r.Tag != t)); // a provider gone since it was picked
            foreach (var r in rows.Where(r => r.Tag.Length > 0))
            {
                var name = new TextBlock { Text = r.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var tag = RemoteMuted(r.Tag);
                tag.VerticalAlignment = VerticalAlignment.Center;
                tag.Margin = new Thickness(8, 0, 0, 0);
                var vision = RemoteMuted(r.Vision ? "images" : "text only");
                var mark = new TextBlock { FontWeight = FontWeights.SemiBold, Width = 28, TextAlignment = TextAlignment.Right };
                mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
                orderMarks[r.Tag] = mark;
                var grid = Columns();
                var who = new StackPanel { Orientation = Orientation.Horizontal, ClipToBounds = true, Children = { name } };
                if (r.Discount is > 0)
                    who.Children.Add(new TextBlock { Text = $"{Math.Round(r.Discount.Value * 100)}% off", Tag = "deal:" + r.Tag, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = DealGreen,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
                who.Children.Add(tag);
                foreach (var (cell, column) in new (FrameworkElement, int)[] { (who, 0), (PriceCell(r.InputPerM, r.Discount), 1), (PriceCell(r.OutputPerM, r.Discount), 2), (PriceCell(r.CacheReadPerM, r.Discount), 3),
                    (RemoteMuted(RemoteFigure(r.LatencySeconds, "0.00")), 4), (RemoteMuted(RemoteFigure(r.ThroughputTps, "0")), 5), (RemoteMuted(RemoteFigure(r.Uptime, "0.#")), 6), (vision, 7), (mark, 8) })
                {
                    cell.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(cell, column);
                    grid.Children.Add(cell);
                }
                var row = new ToggleButton { Content = grid, Tag = "provider:" + r.Tag, IsChecked = picked.Contains(r.Tag), Height = 34, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 0, 1),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                row.SetResourceReference(StyleProperty, "NavToggle");
                var rowTag = r.Tag;
                row.Click += (_, _) =>
                {
                    if (row.IsChecked == true && !picked.Contains(rowTag))
                        picked.Add(rowTag);
                    else if (row.IsChecked != true)
                        picked.Remove(rowTag);
                    Marks();
                };
                list.Children.Add(row);
            }
            Marks();
        }
        async void Load()
        {
            try
            {
                var key = Environment.GetEnvironmentVariable(service.KeyEnv);
                var rows = await RemoteCatalog.FetchProvidersAsync(_remoteHttp, model, CancellationToken.None, key);
                ShowRows(rows);
                status.Text = rows.Count == 0 ? $"No providers listed for {model}: the default still works." : $"{rows.Count} providers · USD per million tokens, struck-through = listed price before the provider's discount" +
                    (string.IsNullOrEmpty(key) ? " · Latency and throughput show only with an OpenRouter key in the environment." : "");
            }
            catch (Exception x) when (x is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                status.Text = $"Could not load the providers ({x.Message}). The default still works.";
            }
        }
        use.Click += (_, _) =>
        {
            chosen = FavoriteRole(service, model, picked);
            dialog.Close();
        };
        auto.Click += (_, _) =>
        {
            chosen = FavoriteRole(service, model, []);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        if (service.HasProviders)
            Load();
        return (dialog, () => chosen);
    }
}

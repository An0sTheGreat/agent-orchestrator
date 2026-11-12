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
        return new StackPanel { Margin = new Thickness(0, 12, 0, 0), Tag = "ApiModelBlock", Children = { label, hint, box } };
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
        var scroll = new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0),
            Visibility = service.HasProviders ? Visibility.Visible : Visibility.Collapsed };
        var use = new Button { Content = "Use selected", Tag = "ProvidersUse", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 5, 16, 5), IsEnabled = false,
            Visibility = service.HasProviders ? Visibility.Visible : Visibility.Collapsed };
        var auto = new Button { Content = service.HasProviders ? "Default (any provider)" : "Use this model", Tag = "ProvidersDefault", Style = (Style)FindResource(service.HasProviders ? "GhostButton" : "PrimaryButton"),
            Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Tag = "ProvidersCancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, auto, use } };
        var dialog = new DialogShell(this, $"Providers for {model}", new StackPanel { MinWidth = 460, Children = { message, status, scroll, buttons } });
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
                var price = RemoteMuted($"{RemotePrice(r.InputPerM)} / {RemotePrice(r.OutputPerM)} per M");
                var vision = RemoteMuted(r.Vision ? "images" : "text only");
                var mark = new TextBlock { FontWeight = FontWeights.SemiBold, Width = 28, TextAlignment = TextAlignment.Right };
                mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
                orderMarks[r.Tag] = mark;
                var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(150) }, new ColumnDefinition { Width = new GridLength(70) }, new ColumnDefinition { Width = GridLength.Auto } } };
                var who = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, tag } };
                foreach (var (cell, column) in new (FrameworkElement, int)[] { (who, 0), (price, 1), (vision, 2), (mark, 3) })
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
                status.Text = rows.Count == 0 ? $"No providers listed for {model}: the default still works." : $"{rows.Count} providers · price in / out, USD per million tokens";
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AgentOrchestrator;

// The worker details column collapses to a narrow rail: one activity dot per visible worker, in card order. Hovering a dot
// shows that worker's full card (live, same RoleCard) in a popup left of the rail; it closes shortly after the pointer
// leaves both the dot and the popup. The rail's top button and the column header's chevron switch between the two; the
// choice is saved (AppSettings.DetailsCollapsed, collapsed by default).
public partial class MainWindow
{
    private readonly DispatcherTimer _railClose = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private void InitWorkerRail()
    {
        RailDots.ItemsSource = _cards;
        _railClose.Tick += (_, _) =>
        {
            _railClose.Stop();
            if (!RailPopup.Child.IsMouseOver && RailPopup.PlacementTarget is not { IsMouseOver: true })
                RailPopup.IsOpen = false;
        };
        SetWorkerRail(_settings.DetailsCollapsed);
    }

    private void WorkerRailToggle_Click(object sender, RoutedEventArgs e)
    {
        SetWorkerRail(!_settings.DetailsCollapsed);
        _settings.Save();
    }

    // Collapsed: the rail alone, no splitter; expanded: the column at its saved width (the splitter resizes it as before).
    // Switching between them on screen slides the column like the sidebar (240 ms, decelerating): the details keep their
    // full width and are uncovered or clipped as the column grows or shrinks.
    private void SetWorkerRail(bool collapsed)
    {
        StopColumnTween(); // a slide still running finishes first, before the pages change
        // what leaves the column when collapsing: the browser when it is open there (it gives the column back), else the details
        var leaving = BrowserPanel.IsVisible ? BrowserPanel : DetailsPanel;
        var slide = collapsed ? leaving.IsVisible : WorkerRail.IsVisible;
        _settings.DetailsCollapsed = collapsed;
        if (BrowserPanel.IsVisible) // closed by this, not at startup
            _settings.BrowserOpen = false;
        RailPopup.IsOpen = false;
        BrowserPanel.Visibility = Visibility.Collapsed;
        WorkerRail.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SlideColumn(collapsed ? leaving : DetailsPanel, collapsed, slide);
    }

    // The right column between the rail (collapsed) and a page at its saved width (the details or the browser). slide: the
    // switch happens on screen, so it slides like the sidebar (240 ms, decelerating): the page keeps its full width and is
    // uncovered or clipped as the column grows or shrinks; a collapsing page stays under the rail until the slide ends.
    private void SlideColumn(FrameworkElement page, bool collapsed, bool slide)
    {
        var target = Math.Clamp(_settings.DetailsWidth, 260, 1400);
        var from = DetailsColumn.ActualWidth;
        SplitterBar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SplitterColumn.Width = new GridLength(collapsed ? 0 : 6);
        void Settle()
        {
            page.ClearValue(WidthProperty);
            page.HorizontalAlignment = HorizontalAlignment.Stretch;
            page.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible; // (an interrupted slide's cleanup may have hidden it)
            DetailsColumn.MinWidth = collapsed ? 0 : 260;
            DetailsColumn.Width = collapsed ? GridLength.Auto : new GridLength(target);
            FitDetailsColumn();
        }
        StopColumnTween();
        if (!slide || !IsLoaded || !IsVisible || from <= 0)
        {
            Settle();
            return;
        }
        page.Visibility = Visibility.Visible;
        page.Width = collapsed ? from : target;
        page.HorizontalAlignment = HorizontalAlignment.Left;
        DetailsColumn.MinWidth = 0;
        TweenColumn(from, collapsed ? WorkerRail.Width : target, Settle);
    }

    // A ColumnDefinition's GridLength width can't take a DoubleAnimation, so the slide is stepped each frame.
    private EventHandler? _columnTween;
    private Action? _columnTweenDone; // a slide cut short still finishes its layout before the next starts

    private void TweenColumn(double from, double to, Action done)
    {
        StopColumnTween();
        _columnTweenDone = done;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        _columnTween = (_, _) =>
        {
            var t = Math.Min(1, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds / 240);
            DetailsColumn.Width = new GridLength(from + (to - from) * Decelerate.Ease(t));
            if (t < 1)
                return;
            StopColumnTween();
        };
        CompositionTarget.Rendering += _columnTween;
    }

    private void StopColumnTween()
    {
        if (_columnTween is not null)
            CompositionTarget.Rendering -= _columnTween;
        _columnTween = null;
        var done = _columnTweenDone;
        _columnTweenDone = null;
        done?.Invoke();
    }

    private void RailDot_MouseEnter(object sender, MouseEventArgs e) => ShowRailCard((FrameworkElement)sender);

    private void RailPopup_MouseEnter(object sender, MouseEventArgs e) => _railClose.Stop();

    private void RailHover_MouseLeave(object sender, MouseEventArgs e) => _railClose.Start();

    private void ShowRailCard(FrameworkElement dot)
    {
        _railClose.Stop();
        if (RailPopup.IsOpen && RailPopup.PlacementTarget == dot)
            return;
        RailPopup.IsOpen = false;
        RailCard.Content = dot.DataContext;
        RailPopup.PlacementTarget = dot;
        RailPopup.IsOpen = true;
    }

    // --ui-smoke, from the Chat view with a custom card among the built-ins: the rail shows one dot per visible
    // card, an active card's dot turns the accent, hovering opens that card in the
    // popup, and the expand toggle shows the full column and is saved.
    private async Task<bool> WorkerRailSmoke(Action<string, FrameworkElement?> snap)
    {
        static Task Idle() => Task.Delay(400);
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: worker rail: {what}"); return false; }
        static IEnumerable<DependencyObject> Visuals(DependencyObject d) =>
            Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(d)).Select(i => VisualTreeHelper.GetChild(d, i)).SelectMany(c => Visuals(c).Prepend(c));
        FrameworkElement? Dot(RoleCard card)
        {
            RailDots.UpdateLayout();
            return RailDots.ItemContainerGenerator.ContainerFromItem(card) as FrameworkElement;
        }
        bool DotsMatch() => _cards.All(c => Dot(c) is { } d && d.IsVisible == !c.Hidden) &&
                            Visuals(RailDots).OfType<Grid>().Count(g => g.IsVisible && g.DataContext is RoleCard && g.Height == 32) == _cards.Count(c => !c.Hidden);
        bool Accent(RoleCard card) => Dot(card) is { } d && Equals(Visuals(d).OfType<Ellipse>().Last().Fill, FindResource("Accent")); // the core; the ring is always accent, faded out

        SetWorkerRail(true);
        _settings.Save();
        await Idle();
        if (!WorkerRail.IsVisible || DetailsPanel.IsVisible || SplitterBar.IsVisible || WorkerRail.ActualWidth > 50 || !AppSettings.Load().DetailsCollapsed)
            return Fail("collapsed layout");
        if (!DotsMatch()) // one dot per shown card (a role not added has none)
            return Fail("Chat view dots");

        var implementer = CardFor("implementer")!;
        if (Accent(implementer))
            return Fail("idle dot is accent");
        implementer.IsActive = true;
        await Idle();
        if (!Accent(implementer))
            return Fail("active dot is not accent");

        var planner = CardFor("planner")!;
        ShowRailCard((FrameworkElement)VisualTreeHelper.GetChild(Dot(planner)!, 0));
        await Idle();
        if (!RailPopup.IsOpen || RailCard.Content != planner ||
            !Visuals(RailCard).OfType<FrameworkElement>().Any(f => f.Tag as string == "CardRoot" && f.DataContext == planner) ||
            !Visuals(RailCard).OfType<Button>().Any(b => b.Tag as string == "ModelSettingsGear"))
            return Fail("hover card");
        snap("32-worker-rail", null);
        snap("32-worker-rail-card", (FrameworkElement)RailPopup.Child);
        var theme = _settings.Theme;
        _settings.Theme = "Parchment";
        ApplyTheme();
        await Idle();
        snap("32-worker-rail-parchment", null);
        snap("32-worker-rail-card-parchment", (FrameworkElement)RailPopup.Child);
        _settings.Theme = theme;
        ApplyTheme();
        implementer.IsActive = false;

        WorkerRailToggle_Click(RailExpandButton, new RoutedEventArgs());
        await Idle();
        if (RailPopup.IsOpen || WorkerRail.IsVisible || !DetailsPanel.IsVisible || !SplitterBar.IsVisible || DetailsColumn.ActualWidth < 259 ||
            AppSettings.Load().DetailsCollapsed)
            return Fail("expand");
        return true;
    }
}

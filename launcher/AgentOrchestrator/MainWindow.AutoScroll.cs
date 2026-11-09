using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrchestrator;

// Middle-click scrolling, as in browsers: a middle click on scrollable content drops a marker; moving the pointer away from it
// scrolls that way, faster the further it is; a click (any button), Esc, the wheel or switching away stops it. Holding the
// middle button and dragging works too: letting go stops it.
public partial class MainWindow
{
    private ScrollViewer? _autoScroll;
    private Point _autoOrigin;
    private bool _autoMoved; // the pointer left the dead zone since the press: releasing the button then ends it
    private long _autoTick;
    private AutoScrollMarker? _autoMarker;
    private AdornerLayer? _autoLayer;
    private readonly DispatcherTimer _autoTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    internal Func<Point>? AutoPointerForTest; // --ui-smoke supplies the pointer

    private const double AutoDeadZone = 12, AutoMaxSpeed = 6000;

    // Pixels per second for a pointer `distance` px from the marker along one axis: nothing inside the dead zone, then growing.
    internal static double AutoScrollSpeed(double distance)
    {
        var excess = Math.Abs(distance) - AutoDeadZone;
        return excess <= 0 ? 0 : Math.Sign(distance) * Math.Min(AutoMaxSpeed, excess * 7 + excess * excess * 0.6);
    }

    private void InitAutoScroll()
    {
        PreviewMouseDown += AutoScroll_PreviewMouseDown;
        PreviewMouseUp += AutoScroll_PreviewMouseUp;
        PreviewKeyDown += (_, e) => { if (_autoScroll is not null) StopAutoScroll(); };
        PreviewMouseWheel += (_, _) => StopAutoScroll();
        Deactivated += (_, _) => StopAutoScroll();
        _autoTimer.Tick += (_, _) => AutoScrollTick();
    }

    // The nearest scrollable ancestor of what was clicked (a text box or document inside a conversation is not scrollable itself).
    private static ScrollViewer? ScrollerAt(DependencyObject? at)
    {
        for (; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : (at as FrameworkContentElement)?.Parent)
        {
            if (at is ButtonBase or MenuItem or Slider or System.Windows.Controls.Primitives.Thumb or ScrollBar or ComboBox)
                return null; // controls that mean something by a middle click (a browser tab's chip closes it)
            if (at is ScrollViewer { IsVisible: true } sv && (sv.ScrollableHeight > 0.5 || sv.ScrollableWidth > 0.5))
                return sv;
        }
        return null;
    }

    private void AutoScroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_autoScroll is not null)
        {
            StopAutoScroll();
            e.Handled = true; // the click that stops it does nothing else
            return;
        }
        if (e.ChangedButton != MouseButton.Middle || ScrollerAt(e.OriginalSource as DependencyObject) is not { } sv)
            return;
        e.Handled = true;
        StartAutoScroll(sv, e.GetPosition(this));
    }

    private void AutoScroll_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_autoScroll is not null && e.ChangedButton == MouseButton.Middle && _autoMoved)
        {
            StopAutoScroll(); // held and dragged: letting go ends it
            e.Handled = true;
        }
    }

    internal void StartAutoScroll(ScrollViewer sv, Point origin)
    {
        (_autoScroll, _autoOrigin, _autoMoved, _autoTick) = (sv, origin, false, Stopwatch.GetTimestamp());
        Mouse.OverrideCursor = Cursors.ScrollAll;
        if (Content is Visual root && AdornerLayer.GetAdornerLayer(root) is { } layer)
        {
            _autoLayer = layer;
            layer.Add(_autoMarker = new AutoScrollMarker((UIElement)Content, origin, sv.ScrollableHeight > 0.5, sv.ScrollableWidth > 0.5, (Brush)FindResource("TextPrimary"), (Brush)FindResource("Card")));
        }
        _autoTimer.Start();
    }

    internal void StopAutoScroll()
    {
        if (_autoScroll is null)
            return;
        _autoScroll = null;
        _autoTimer.Stop();
        Mouse.OverrideCursor = null;
        if (_autoMarker is not null)
            _autoLayer?.Remove(_autoMarker);
        _autoMarker = null;
    }

    internal void AutoScrollTick()
    {
        if (_autoScroll is not { IsVisible: true } sv)
        {
            StopAutoScroll();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Min(0.1, (now - _autoTick) / (double)Stopwatch.Frequency);
        _autoTick = now;
        var pointer = AutoPointerForTest?.Invoke() ?? Mouse.GetPosition(this);
        var (dx, dy) = (pointer.X - _autoOrigin.X, pointer.Y - _autoOrigin.Y);
        if (Math.Abs(dx) > AutoDeadZone || Math.Abs(dy) > AutoDeadZone)
            _autoMoved = true;
        _autoMarker?.Aim(dx, dy);
        var (vx, vy) = (AutoScrollSpeed(dx), AutoScrollSpeed(dy));
        if (vy != 0 && sv.ScrollableHeight > 0.5)
            sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + vy * seconds, 0, sv.ScrollableHeight));
        if (vx != 0 && sv.ScrollableWidth > 0.5)
            sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset + vx * seconds, 0, sv.ScrollableWidth));
    }

    // --ui-smoke: middle-click scrolling on a long reply: the pointer below the marker scrolls down, back at it stops the motion,
    // a click ends it; the scroll cursor goes with it; a button is left to mean what it means.
    private bool AutoScrollSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: autoscroll: {what}"); return false; }
        var (turn, reply) = (_turn, _reply);
        try
        {
            ClearConversation(_chat);
            _turn = _chat;
            var r = _reply = AddReply("planner");
            EnsureAnswer(r).Text = string.Join("\n\n", Enumerable.Range(1, 200).Select(n => $"Paragraph {n}"));
            ShowMarkdown(r);
            UpdateLayout();
            var scroll = _chat.Scroll;
            if (scroll.ScrollableHeight < 100 || ScrollerAt(r.Rendered) != scroll || ScrollerAt(ChatSend) is not null)
                return Fail($"scroller {scroll.ScrollableHeight} / {ScrollerAt(r.Rendered) == scroll} / {ScrollerAt(ChatSend) is null}");
            var pointer = new Point(200, 200);
            AutoPointerForTest = () => pointer;
            scroll.ScrollToVerticalOffset(0);
            StartAutoScroll(scroll, new Point(200, 200));
            if (Mouse.OverrideCursor != Cursors.ScrollAll)
                return Fail("no scroll cursor");
            pointer = new Point(200, 330); // 130 px below the marker
            System.Threading.Thread.Sleep(40);
            AutoScrollTick();
            var moved = scroll.VerticalOffset;
            if (moved <= 0)
                return Fail("did not scroll down");
            pointer = new Point(200, 205); // inside the dead zone
            System.Threading.Thread.Sleep(40);
            AutoScrollTick();
            if (scroll.VerticalOffset != moved)
                return Fail("moved inside the dead zone");
            StopAutoScroll();
            if (Mouse.OverrideCursor is not null)
                return Fail("the cursor stayed");
            System.Threading.Thread.Sleep(40);
            AutoScrollTick();
            return scroll.VerticalOffset == moved || Fail("scrolled after it stopped");
        }
        finally
        {
            AutoPointerForTest = null;
            StopAutoScroll();
            ClearConversation(_chat);
            (_turn, _reply) = (turn, reply);
        }
    }

    // The round marker left where the middle click was: a dot with an arrow for each direction it can scroll, the one being
    // scrolled toward lit.
    private sealed class AutoScrollMarker : Adorner
    {
        private readonly Point _at;
        private readonly bool _vertical, _horizontal;
        private readonly Brush _ink, _fill;
        private double _dx, _dy;

        public AutoScrollMarker(UIElement over, Point at, bool vertical, bool horizontal, Brush ink, Brush fill) : base(over)
        {
            (_at, _vertical, _horizontal, _ink, _fill) = (at, vertical, horizontal, ink, fill);
            IsHitTestVisible = false;
        }

        public void Aim(double dx, double dy)
        {
            (_dx, _dy) = (dx, dy);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var ring = new Pen(_ink, 1.5);
            dc.PushOpacity(0.92);
            dc.DrawEllipse(_fill, ring, _at, 15, 15);
            dc.DrawEllipse(_ink, null, _at, 2.2, 2.2);
            void Arrow(double ux, double uy, bool lit)
            {
                var tip = new Point(_at.X + ux * 10, _at.Y + uy * 10);
                var (px, py) = (-uy, ux);
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(tip, true, true);
                    g.LineTo(new Point(tip.X - ux * 4 + px * 3.5, tip.Y - uy * 4 + py * 3.5), false, false);
                    g.LineTo(new Point(tip.X - ux * 4 - px * 3.5, tip.Y - uy * 4 - py * 3.5), false, false);
                }
                geometry.Freeze();
                dc.PushOpacity(lit ? 1 : 0.45);
                dc.DrawGeometry(_ink, null, geometry);
                dc.Pop();
            }
            if (_vertical)
            {
                Arrow(0, -1, _dy < -AutoDeadZone);
                Arrow(0, 1, _dy > AutoDeadZone);
            }
            if (_horizontal)
            {
                Arrow(-1, 0, _dx < -AutoDeadZone);
                Arrow(1, 0, _dx > AutoDeadZone);
            }
            dc.Pop();
        }
    }
}

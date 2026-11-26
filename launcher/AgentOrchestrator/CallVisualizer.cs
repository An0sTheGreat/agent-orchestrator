using System.Windows;
using System.Windows.Media;

namespace AgentOrchestrator;

// The call's voice, drawn: a soft accent blob through 64 radial points whose radii follow the 24 band energies (mirrored left and
// right, eased per frame), a radial-gradient fill with a faint outer glow. Quiet: it breathes. Listening (the mic): a dimmer,
// smaller swing. Thinking: a bulge and a highlight turn slowly around it. Paused: grey and still. Animates through
// CompositionTarget.Rendering only while visible; the brushes follow the theme's Accent and Muted resources.
internal sealed class CallVisualizer : FrameworkElement
{
    internal const int Points = 64;
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(CallVisualizer),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CallVisualizer)d)._brushes = null));
    public static readonly DependencyProperty GreyProperty = DependencyProperty.Register(nameof(Grey), typeof(Brush), typeof(CallVisualizer),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CallVisualizer)d)._brushes = null));
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Grey { get => (Brush)GetValue(GreyProperty); set => SetValue(GreyProperty, value); }

    private readonly double[] _radii = new double[Points];  // eased swing per point, 0..1
    private float[] _bands = new float[24];
    private bool _speaking;
    private double _lastFeed = -1, _time, _lastFrame = -1, _glow, _paused;
    private (Brush Fill, Brush Halo, Pen Rim, Pen Shimmer, Color Grey)? _brushes;
    private CallState _state = CallState.Listening;

    internal int RenderCount { get; private set; } // --ui-smoke: frames drawn

    public CallVisualizer()
    {
        SetResourceReference(AccentProperty, "Accent");
        SetResourceReference(GreyProperty, "Muted");
        IsVisibleChanged += (_, _) =>
        {
            CompositionTarget.Rendering -= Frame;
            _lastFrame = -1;
            if (IsVisible)
                CompositionTarget.Rendering += Frame;
        };
    }

    internal CallState State { get => _state; set { _state = value; InvalidateVisual(); } }

    internal void Feed(float[] bands, bool speaking)
    {
        (_bands, _speaking, _lastFeed) = (bands, speaking, _time);
    }

    // The swing each point aims for: point i and its mirror (63 - i) read the same band, low bands at the top; the bands are
    // interpolated between, the high ones lifted (a voice is mostly low), and the whole blob swells a little with the loudness.
    internal static double[] Targets(float[] bands, int points = Points)
    {
        var targets = new double[points];
        if (bands.Length == 0)
            return targets;
        var mean = bands.Average();
        var half = points / 2;
        for (var i = 0; i < points; i++)
        {
            var j = i < half ? i : points - 1 - i;
            var at = (double)j / (half - 1) * (bands.Length - 1);
            int lo = (int)Math.Floor(at), hi = Math.Min(lo + 1, bands.Length - 1);
            var level = bands[lo] + (bands[hi] - bands[lo]) * (at - lo);
            targets[i] = Math.Clamp(level * (1 + 0.6 * at / Math.Max(1, bands.Length - 1)) * 0.75 + mean * 0.35, 0, 1);
        }
        return targets;
    }

    // Toward the target, frame-rate independent: a quick rise (a syllable lands at once), a slower fall. Never overshoots.
    internal static double Ease(double current, double target, double dt)
    {
        var rate = target > current ? 18 : 7;
        return current + (target - current) * (1 - Math.Exp(-rate * Math.Max(0, dt)));
    }

    private void Frame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime.TotalSeconds : _time + 1 / 60.0;
        if (now == _lastFrame)
            return; // Rendering can fire twice for one frame
        var dt = _lastFrame < 0 ? 1 / 60.0 : Math.Min(0.1, now - _lastFrame);
        _lastFrame = now;
        _time += dt;
        var live = _state is CallState.Listening or CallState.Speaking && _time - _lastFeed < 0.25; // levels stop: settle to idle
        var targets = live ? Targets(_bands) : new double[Points];
        for (var i = 0; i < Points; i++)
            _radii[i] = Ease(_radii[i], targets[i], dt);
        _glow = Ease(_glow, live ? _bands.Average() : 0, dt);
        _paused = Ease(_paused, _state == CallState.Paused ? 1 : 0, dt * 0.4);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        RenderCount++;
        double w = ActualWidth, h = ActualHeight, size = Math.Min(w, h);
        if (size <= 0)
            return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // the whole square takes the click
        var b = _brushes ??= MakeBrushes();
        var center = new Point(w / 2, h / 2);
        var listening = _state == CallState.Listening || _state == CallState.Speaking && !_speaking;
        var swing = size * (listening ? 0.07 : 0.12);          // ~13 px on the mic, ~22 px for the voice at 185 px
        var baseR = size * 0.335 * (1 - 0.08 * _paused) * (1 + 0.025 * Math.Sin(_time * 2 * Math.PI / 3.6) * (1 - _paused));
        var thinking = _state == CallState.Thinking;
        var spin = _time * 1.4;
        var pts = new Point[Points];
        double sum = 0;
        for (var i = 0; i < Points; i++)
        {
            var a = -Math.PI / 2 + 2 * Math.PI * (i + 0.5) / Points;
            var wobble = size * 0.008 * Math.Sin(3 * a + _time * 0.9) * Math.Sin(2 * a - _time * 0.6) * (1 - _paused); // alive, never a perfect circle
            var bulge = thinking ? size * 0.05 * Math.Pow(Math.Max(0, Math.Cos(a - spin)), 6) : 0;
            var radius = baseR + wobble + bulge + _radii[i] * swing;
            sum += radius;
            pts[i] = new Point(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
        }
        var mean = sum / Points;
        var shape = Blob(pts);
        // the glow: a halo that brightens with the voice, then a fainter, larger copy of the blob for depth
        dc.PushOpacity((0.45 + 0.55 * Math.Min(1, _glow * 1.6)) * (1 - 0.6 * _paused) * (listening ? 0.75 : 1));
        var halo = mean * (1.3 + 0.12 * _glow);
        dc.DrawEllipse(b.Halo, null, center, halo, halo);
        dc.PushTransform(new ScaleTransform(1.09, 1.09, center.X, center.Y));
        dc.PushOpacity(0.28);
        dc.DrawGeometry(b.Fill, null, shape);
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.PushOpacity(listening ? 0.82 : 1);
        dc.DrawGeometry(b.Fill, b.Rim, shape);
        if (thinking)
        {
            dc.PushTransform(new RotateTransform(spin * 180 / Math.PI, center.X, center.Y));
            dc.DrawGeometry(null, b.Shimmer, shape);
            dc.Pop();
        }
        dc.Pop();
        if (_paused > 0.01) // greys over the accent as it pauses
        {
            dc.PushOpacity(_paused * 0.85);
            dc.DrawGeometry(new SolidColorBrush(b.Grey), null, shape);
            dc.Pop();
        }
    }

    // A closed Catmull-Rom curve through the points, as Béziers.
    private static StreamGeometry Blob(Point[] p)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(p[0], true, true);
            var n = p.Length;
            var controls = new Point[n * 3];
            for (var i = 0; i < n; i++)
            {
                Point p0 = p[(i - 1 + n) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
                controls[i * 3] = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                controls[i * 3 + 1] = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                controls[i * 3 + 2] = p2;
            }
            c.PolyBezierTo(controls, true, true);
        }
        g.Freeze();
        return g;
    }

    private (Brush, Brush, Pen, Pen, Color) MakeBrushes()
    {
        var accent = (Accent as SolidColorBrush)?.Color ?? Colors.SteelBlue;
        var grey = (Grey as SolidColorBrush)?.Color ?? Colors.Gray;
        static Color A(Color c, double a) => Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);
        static Color Mix(Color c, Color d, double t) => Color.FromRgb((byte)(c.R + (d.R - c.R) * t), (byte)(c.G + (d.G - c.G) * t), (byte)(c.B + (d.B - c.B) * t));
        var light = Mix(accent, Colors.White, 0.45);
        var fill = new RadialGradientBrush { GradientOrigin = new Point(0.38, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.7, RadiusY = 0.7,
            GradientStops = { new(A(light, 0.95), 0), new(A(accent, 0.9), 0.55), new(A(Mix(accent, Colors.Black, 0.25), 0.8), 1) } };
        var halo = new RadialGradientBrush { GradientStops = { new(A(accent, 0.38), 0.55), new(A(accent, 0.12), 0.8), new(A(accent, 0), 1) } };
        var rim = new Pen(new SolidColorBrush(A(light, 0.35)), 1.2);
        var shimmer = new Pen(new LinearGradientBrush(new GradientStopCollection { new(A(Colors.White, 0), 0.2), new(A(Colors.White, 0.85), 0.5), new(A(Colors.White, 0), 0.8) }, 0), 2.2);
        foreach (var f in new Freezable[] { fill, halo, rim, shimmer })
            f.Freeze();
        return (fill, halo, rim, shimmer, grey);
    }

    // --self-test: the curve maths.
    internal static void SelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Call visualizer self-test failed: " + what);
        }
        Check(Targets(new float[24]).All(t => t == 0) && Targets([]).Length == Points, "silence: no swing");
        Check(Targets(Enumerable.Repeat(1f, 24).ToArray()).All(t => t == 1), "full: full swing, clamped");
        var ramp = Targets(Enumerable.Range(0, 24).Select(i => i / 23f).ToArray());
        Check(Enumerable.Range(0, Points).All(i => ramp[i] == ramp[Points - 1 - i]), "mirrored left and right");
        Check(ramp[0] < ramp[Points / 2 - 1] && Enumerable.Range(1, Points / 2 - 1).All(i => ramp[i] >= ramp[i - 1]), "low bands at the top, rising smoothly to the high ones");
        var one = new float[24];
        one[0] = 1;
        var top = Targets(one);
        Check(top[0] > top[Points / 4] && top[Points / 4] == top[Points / 2 - 1], "a low band moves the top, the rest only with the loudness");
        var up = Ease(0, 1, 1 / 60.0);
        var down = 1 - Ease(1, 0, 1 / 60.0); // the distance fallen
        Check(up is > 0.2 and < 0.35 && down is > 0.05 and < 0.15, $"one frame: up {up:0.00}, down {down:0.00}");
        double x = 0;
        for (var i = 0; i < 60; i++)
            x = Ease(x, 1, 1 / 60.0);
        Check(x is > 0.99 and <= 1 && Ease(0.5, 0.5, 1) == 0.5 && Ease(0.2, 1, 0) == 0.2 && Ease(0, 1, 10) <= 1, $"converges without overshooting: {x:0.000}");
        Check(Math.Abs(Ease(0, 1, 1 / 30.0) - Ease(Ease(0, 1, 1 / 60.0), 1, 1 / 60.0)) < 1e-9, "frame-rate independent");
    }
}

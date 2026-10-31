using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentOrchestrator;

// The engine's "context" event: how full the target model's context window is after a model call. Saved with a conversation.
public sealed class ContextSnapshot
{
    public long? NCtx { get; set; } // null: unknown (remote role)
    public string? Role { get; set; } // the worker whose call this measured (null in snapshots saved before it was kept)
    public long Prompt { get; set; }
    public long Completion { get; set; }
    public Dictionary<string, long> Parts { get; set; } = new();

    public long Used => Prompt + Completion;
    public double Fraction => NCtx is > 0 ? Math.Min(1, (double)Used / NCtx.Value) : 0;

    public static ContextSnapshot From(JsonElement ev)
    {
        static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        var s = new ContextSnapshot { Prompt = Long(ev, "prompt_tokens"), Completion = Long(ev, "completion_tokens"),
            Role = ev.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String ? role.GetString() : null };
        if (Long(ev, "n_ctx") is > 0 and var n)
            s.NCtx = n;
        if (ev.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Object)
            foreach (var p in parts.EnumerateObject())
                s.Parts[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : 0;
        return s;
    }

    // After a compaction: the prompt is `after` tokens, no reply yet, its parts scaled down so the popup rows still add up to it.
    public ContextSnapshot Compacted(long after)
    {
        var s = new ContextSnapshot { NCtx = NCtx, Role = Role, Prompt = after };
        var sum = Parts.Values.Sum();
        if (sum <= 0)
            return s;
        foreach (var (key, n) in Parts)
            s.Parts[key] = n * after / sum;
        var largest = s.Parts.MaxBy(p => p.Value).Key;
        s.Parts[largest] += after - s.Parts.Values.Sum(); // the rounding remainder
        return s;
    }

    // (name, colour, tokens) per popup row, in bar order: the prompt's parts, this turn's reply, then free space.
    public static readonly (string Key, string Name, string Color)[] Categories =
    {
        ("system", "System prompt", "#8C7AE6"), ("tools", "Tools", "#3FA7D6"), ("skills", "Skills", "#E07BB5"),
        ("messages", "Messages", "#D9A441"), ("tool_results", "Tool results", "#5DAE8B"), ("reply", "Reply", "#E07856"),
    };

    public List<(string Name, string Color, long Tokens)> Rows()
    {
        var rows = Categories.Select(c => (c.Name, c.Color, c.Key == "reply" ? Completion : Parts.GetValueOrDefault(c.Key)))
            .Where(r => r.Item3 > 0).ToList();
        if (NCtx is > 0)
            rows.Add(("Free space", "#8A8A8A", Math.Max(0, NCtx.Value - Used)));
        return rows;
    }

    // 683.8k, 1M, 32k, 950
    public static string Tokens(long n) => n switch
    {
        >= 1_000_000 => Trim(n / 1_000_000.0) + "M",
        >= 1_000 => Trim(n / 1_000.0) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private static string Trim(double v) => (Math.Floor(v * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture);

    public string Summary => NCtx is > 0 ? $"{Tokens(Used)} / {Tokens(NCtx.Value)} tokens ({Fraction:0%})" : $"{Tokens(Used)} tokens (context size unknown)";

    public static void SelfTest()
    {
        void Is(string got, string want) { if (got != want) throw new InvalidOperationException($"Context format: {got} != {want}"); }
        Is(Tokens(950), "950"); Is(Tokens(12_345), "12.3k"); Is(Tokens(32_768), "32.7k"); Is(Tokens(683_800), "683.8k"); Is(Tokens(1_000_000), "1M");
        var s = new ContextSnapshot { NCtx = 32_000, Prompt = 12_000, Completion = 300, Parts = { ["system"] = 2_000, ["messages"] = 10_000, ["tools"] = 0 } };
        Is(s.Summary, "12.3k / 32k tokens (38%)");
        var rows = s.Rows();
        Is(string.Join(",", rows.Select(r => r.Name)), "System prompt,Messages,Reply,Free space");
        if (rows.Sum(r => r.Tokens) != 32_000)
            throw new InvalidOperationException("Context rows must fill the window");
        Is(new ContextSnapshot { Prompt = 5 }.Summary, "5 tokens (context size unknown)");
        var c = s.Compacted(3_333);
        Is(c.Summary, "3.3k / 32k tokens (10%)");
        if (c.Parts.Values.Sum() != 3_333 || c.Parts["messages"] <= c.Parts["system"] || c.Parts["tools"] != 0 || s.Prompt != 12_000)
            throw new InvalidOperationException("A compacted snapshot's parts must add up to its prompt");
        Is(string.Join(",", new[] { 0.01, 0.60, 0.604, 0.61, 0.89, 0.894, 0.895, 0.90, 1.0 }.Select(MainWindow.RingColor)),
            "#4C8DFF,#4C8DFF,#4C8DFF,#E8A33D,#E8A33D,#E8A33D,#E5484D,#E5484D,#E5484D");
    }
}

public partial class MainWindow
{
    private ContextSnapshot? _chatContext, _codeContext;

    private ContextSnapshot? ContextOf(bool code) => code ? _codeContext : _chatContext;

    // The view's latest snapshot: stored, and shown on its ring (null leaves the empty ring).
    private void ShowContext(bool code, ContextSnapshot? s)
    {
        if (code) _codeContext = s; else _chatContext = s;
        var ring = code ? CodeContextRing : ChatContextRing;
        var track = new Ellipse { Width = 18, Height = 18, StrokeThickness = 2.5 };
        track.SetResourceReference(Shape.StrokeProperty, "Border");
        var grid = new Grid { Width = 18, Height = 18 };
        grid.Children.Add(track);
        if (s is { NCtx: > 0 })
            grid.Children.Add(Arc(s.Fraction, RingColor(s.Fraction)));
        ring.Content = grid;
        ring.Tag = s?.Fraction;
        ring.ToolTip = s?.Summary ?? "Context: no reply yet in this conversation";
        RefreshContextSection();
        RefreshContextPins();
    }

    // Centered conversations leave room beside them: there the ring pins its card open (saved), showing the open chat's
    // context as it changes. Without that room (or with centering off) the ring opens the usual popup.
    private const double PinWidth = 280;

    private bool CanPin(bool code)
    {
        var scroll = code ? CodeScroll : ChatScroll;
        return _settings.CenterChat && (scroll.ActualWidth - ChatWidth) / 2 >= PinWidth + 20;
    }

    private void RefreshContextPins()
    {
        foreach (var code in new[] { false, true })
        {
            var pin = code ? CodeContextPin : ChatContextPin;
            var ring = code ? CodeContextRing : ChatContextRing;
            var show = _settings.ContextPinned && CanPin(code);
            pin.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            pin.Content = show ? ContextCard(ContextOf(code)) : null;
            if (show)
                ring.SetResourceReference(BackgroundProperty, "Hover"); // pressed while its card is pinned
            else
                ring.ClearValue(BackgroundProperty);
        }
    }

    private void ConversationScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            RefreshContextPins();
    }

    // The card of the ring's popup and of the pinned view; s null: no reply yet.
    private Border ContextCard(ContextSnapshot? s)
    {
        StackPanel panel;
        if (s is null)
        {
            panel = new StackPanel();
            var title = new TextBlock { Text = "Context", FontSize = 13, FontWeight = FontWeights.SemiBold };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var empty = new TextBlock { Text = "No reply yet in this conversation", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            panel.Children.Add(title);
            panel.Children.Add(empty);
        }
        else
            panel = ContextBody(s, title: true);
        var card = new Border { Child = panel, Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(1), Tag = "ContextCard" };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        return card;
    }

    // A context length saved in the Models window shows at once on each view whose last measured call was that worker's (an
    // older saved snapshot without a role: the view's Single target). Empty or 0 (auto) is only known after the next call.
    private void ContextLengthSaved(string role, string? value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return;
        foreach (var code in new[] { false, true })
            if (ContextOf(code) is { } s && (s.Role ?? TargetOf(code ? _code : _chat)?.Role) == role)
            {
                s.NCtx = n;
                ShowContext(code, s);
            }
    }

    // The worker details column's Context section: the shown view's snapshot; open or closed as last left (saved).
    private void RefreshContextSection()
    {
        var s = ContextOf(NavCode.IsChecked == true);
        var open = _settings.ContextExpanded;
        ContextSectionToggle.IsChecked = open;
        ContextSummary.Text = s is null ? "—" : s.NCtx is > 0 ? $"{s.Fraction:0%}  ·  {Tokens(s.Used)} / {Tokens(s.NCtx.Value)}" : $"{Tokens(s.Used)} / ?";
        var empty = new TextBlock { Text = "No reply yet in this conversation", FontSize = 12 };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        ContextSectionBody.Content = !open ? null : s is null ? empty : ContextBody(s, title: false);
        ContextSectionBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ContextSummary.Visibility = open ? Visibility.Collapsed : Visibility.Visible; // the open body repeats it
    }

    private void ContextSection_Click(object sender, RoutedEventArgs e)
    {
        _settings.ContextExpanded = ContextSectionToggle.IsChecked == true;
        _settings.Save();
        RefreshContextSection();
    }

    // Blue up to 60%, yellow to 89%, red from 90% (whole percent, as the popup shows it).
    internal static string RingColor(double fraction) => Math.Round(fraction * 100) switch
    {
        >= 90 => "#E5484D",
        > 60 => "#E8A33D",
        _ => "#4C8DFF",
    };

    private static Path Arc(double fraction, string? color)
    {
        const double r = 7.75, c = 9;
        var angle = Math.Min(fraction, 0.9999) * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r) };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var path = new Path { Data = new PathGeometry(new[] { figure }), StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        if (color is null)
            path.SetResourceReference(Shape.StrokeProperty, "Accent");
        else
            path.Stroke = (Brush)new BrushConverter().ConvertFromString(color)!;
        return path;
    }

    private Popup? _contextPopup;

    private void ContextRing_Click(object sender, RoutedEventArgs e)
    {
        var code = sender == CodeContextRing;
        if (CanPin(code))
        {
            _settings.ContextPinned = !_settings.ContextPinned;
            _settings.Save();
            RefreshContextPins();
            return;
        }
        if (ContextOf(code) is not { } s)
            return;
        _contextPopup = ContextPopup(s, (Button)sender);
        _contextPopup.IsOpen = true;
    }

    private Popup ContextPopup(ContextSnapshot s, FrameworkElement anchor)
    {
        var card = ContextCard(s);
        card.Child.SetValue(WidthProperty, 280.0);
        return new Popup { Child = card, PlacementTarget = anchor, Placement = PlacementMode.Bottom, HorizontalOffset = -262, VerticalOffset = 6,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
    }

    // The breakdown shared by the ring's popup and the worker column's Context section: usage, bar, one row per part.
    private StackPanel ContextBody(ContextSnapshot s, bool title)
    {
        TextBlock Text(string text, string brush = "TextPrimary", double size = 12, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
        Brush Fill(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
        var window = s.NCtx is > 0 ? s.NCtx.Value : Math.Max(1, s.Used);
        var panel = new StackPanel();
        if (title)
            panel.Children.Add(Text("Context", size: 13, weight: FontWeights.SemiBold));
        var head = new DockPanel { Margin = new Thickness(0, title ? 6 : 0, 0, 8) };
        var total = Text(s.NCtx is > 0 ? $"{Tokens(s.Used)} / {Tokens(window)}" : $"{Tokens(s.Used)} / ?", "Muted");
        DockPanel.SetDock(total, Dock.Right);
        head.Children.Add(total);
        head.Children.Add(Text(s.NCtx is > 0 ? $"{s.Fraction:0%} used" : "Context size unknown"));
        panel.Children.Add(head);
        var rows = s.Rows();
        var bar = new Grid { Height = 8, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var row in rows)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(row.Tokens, GridUnitType.Star) });
            var seg = new Rectangle { Fill = row.Name == "Free space" ? Brushes.Transparent : Fill(row.Color) };
            Grid.SetColumn(seg, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(seg);
        }
        var track = new Border { CornerRadius = new CornerRadius(4), Child = bar, ClipToBounds = true };
        track.SetResourceReference(Border.BackgroundProperty, "Hover");
        panel.Children.Add(track);
        foreach (var row in rows)
        {
            var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Tag = row.Tokens };
            var share = Text($"{Tokens(row.Tokens)}   {(double)row.Tokens / window:0.0%}", "Muted");
            DockPanel.SetDock(share, Dock.Right);
            line.Children.Add(share);
            line.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 8, 0),
                Fill = Fill(row.Color), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(Text(row.Name));
            panel.Children.Add(line);
        }
        return panel;
    }

    private static string Tokens(long n) => ContextSnapshot.Tokens(n);
}

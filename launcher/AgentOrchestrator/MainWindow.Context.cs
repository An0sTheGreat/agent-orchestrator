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
    public long Prompt { get; set; }
    public long Completion { get; set; }
    public Dictionary<string, long> Parts { get; set; } = new();

    public long Used => Prompt + Completion;
    public double Fraction => NCtx is > 0 ? Math.Min(1, (double)Used / NCtx.Value) : 0;

    public static ContextSnapshot From(JsonElement ev)
    {
        static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        var s = new ContextSnapshot { Prompt = Long(ev, "prompt_tokens"), Completion = Long(ev, "completion_tokens") };
        if (Long(ev, "n_ctx") is > 0 and var n)
            s.NCtx = n;
        if (ev.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Object)
            foreach (var p in parts.EnumerateObject())
                s.Parts[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : 0;
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
    }
}

public partial class MainWindow
{
    private ContextSnapshot? _chatContext, _codeContext;

    private ContextSnapshot? ContextOf(bool code) => code ? _codeContext : _chatContext;

    // The view's latest snapshot: stored, and shown on its ring (null clears it to the grey "—" state).
    private void ShowContext(bool code, ContextSnapshot? s)
    {
        if (code) _codeContext = s; else _chatContext = s;
        var ring = code ? CodeContextRing : ChatContextRing;
        var track = new Ellipse { Width = 18, Height = 18, StrokeThickness = 2.5 };
        track.SetResourceReference(Shape.StrokeProperty, "Border");
        var grid = new Grid { Width = 18, Height = 18 };
        grid.Children.Add(track);
        if (s is { NCtx: > 0 })
            grid.Children.Add(Arc(s.Fraction, s.Fraction > 0.95 ? "#E5484D" : s.Fraction > 0.8 ? "#E8A33D" : null));
        else if (s is null)
            grid.Children.Add(new TextBlock { Text = "—", FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        ring.Content = grid;
        ring.Tag = s?.Fraction;
        ring.ToolTip = s?.Summary ?? "Context: no reply yet in this conversation";
    }

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
        if (ContextOf(sender == CodeContextRing) is not { } s)
            return;
        _contextPopup = ContextPopup(s, (Button)sender);
        _contextPopup.IsOpen = true;
    }

    private Popup ContextPopup(ContextSnapshot s, FrameworkElement anchor)
    {
        TextBlock Text(string text, string brush = "TextPrimary", double size = 12, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
        Brush Fill(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
        var window = s.NCtx is > 0 ? s.NCtx.Value : Math.Max(1, s.Used);
        var panel = new StackPanel { Width = 280 };
        panel.Children.Add(Text("Context", size: 13, weight: FontWeights.SemiBold));
        var head = new DockPanel { Margin = new Thickness(0, 6, 0, 8) };
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
        var card = new Border { Child = panel, Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        return new Popup { Child = card, PlacementTarget = anchor, Placement = PlacementMode.Bottom, HorizontalOffset = -262, VerticalOffset = 6,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
    }

    private static string Tokens(long n) => ContextSnapshot.Tokens(n);
}

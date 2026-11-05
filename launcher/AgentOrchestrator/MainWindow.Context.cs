using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    public bool Unloaded { get; set; } // a worker's model unloaded since this call: its last measured call, not what is loaded now

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
    // Claude's context colours: system prompt amber, system tools orange, MCP tools teal (here: tool results), skills
    // grey, messages blue, autocompact buffer dark grey (here: the reply's reserved share), free space darkest grey.
    public static readonly (string Key, string Name, string Color)[] Categories =
    {
        ("system", "System prompt", "#C88705"), ("tools", "Tools", "#DD6122"), ("skills", "Skills", "#8E8E8E"),
        ("messages", "Messages", "#3085E2"), ("tool_results", "Tool results", "#00A082"), ("reply", "Reply", "#595959"),
    };

    public List<(string Name, string Color, long Tokens)> Rows()
    {
        var rows = Categories.Select(c => (c.Name, c.Color, c.Key == "reply" ? Completion : Parts.GetValueOrDefault(c.Key)))
            .Where(r => r.Item3 > 0).ToList();
        if (NCtx is > 0)
            rows.Add(("Free space", "#2F2F2F",Math.Max(0, NCtx.Value - Used)));
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

internal static class TextBlockExtensions
{
    public static TextBlock WithForeground(this TextBlock t, string brush)
    {
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }
}

public partial class MainWindow
{
    private ContextSnapshot? _chatContext, _codeContext;
    // Code: every worker's context in this conversation by the event's role ("<worker> Context" cards in the details
    // column); the lead's is also _codeContext, which the header ring shows.
    private readonly Dictionary<string, ContextSnapshot> _codeContexts = new();

    private ContextSnapshot? ContextOf(bool code) => code ? _codeContext : _chatContext;

    private string CodeLead => CodeTarget()?.Role ?? "planner";

    private string ContextTitle(string role) => $"{ChatName(CardFor(role), role)} Context";

    // A Code turn's or run's measured call, filed under its worker; the lead's also moves the header ring.
    private void SetCodeContext(ContextSnapshot s)
    {
        var role = s.Role ?? CodeLead;
        _codeContexts[role] = s;
        if (role == CodeLead)
            ShowContext(true, s);
        else
        {
            RefreshContextSection();
            RefreshCodeContextStack();
        }
    }

    // A model loading (or the models unloaded) leaves no worker anything in its window: each worker's card keeps its last
    // measured call, marked unloaded, until its next call replaces it. The lead's stays as it is: its next call sends the
    // whole conversation again.
    // ponytail: assumes one local model at a time (llama-server); per-server tracking if several ever run at once.
    private void WorkerContextsCleared()
    {
        foreach (var (role, s) in _codeContexts)
            if (role != CodeLead)
                s.Unloaded = true;
        RefreshContextSection();
        RefreshCodeContextStack();
    }

    // The view's latest snapshot: stored, and shown on its Context card (null: the empty ring; in Code, a new conversation).
    private void ShowContext(bool code, ContextSnapshot? s)
    {
        if (code) _codeContext = s; else _chatContext = s;
        if (code && s is null)
            _codeContexts.Clear();
        var ring = code ? CodeContextRing : ChatContextRing;
        ring.Tag = s?.Fraction;
        ring.ToolTip = s?.Summary ?? "Context: no reply yet in this conversation";
        RefreshContextSection();
        RefreshContextPins();
    }

    // Centered conversations leave room beside them: there the ring pins its card open (saved), showing the open chat's
    // context as it changes. Without that room (or with centering off) the ring opens the usual popup.
    private const double PinWidth = 314; // a rail worker card's width (RailPopup: 320 less its 6 gap)

    // Chat and Code place the context card alike: level with the header's Context button, its right edge where a rail
    // worker card's is (6 left of the collapsed rail), else the button's.
    private Thickness PinMargin(FrameworkElement area, FrameworkElement ring)
    {
        AlignContextRing(ring);
        area.UpdateLayout();
        // Code's run clocks share the button's place, top-right: the cards stack 6 below them, right edges flush.
        var corner = ring == CodeContextRing && RunClocks.Visibility == Visibility.Visible
            ? RunClocks.TranslatePoint(new Point(RunClocks.ActualWidth, RunClocks.ActualHeight + 6), area)
            : ring.TranslatePoint(new Point(ring.ActualWidth, 0), area);
        return new Thickness(0, corner.Y, Math.Max(0, area.ActualWidth - corner.X), 0);
    }

    // The header button itself moves so its right edge is a rail worker card's (12 from the header's edge without the rail).
    private void AlignContextRing(FrameworkElement ring)
    {
        var slot = ring.Parent is Grid shared ? shared : ring; // Code's button shares its place with the run clocks
        if (slot.Parent is not FrameworkElement header)
            return;
        var right = 12.0;
        if (WorkerRail.IsVisible)
        {
            header.UpdateLayout();
            right = Math.Max(0, header.ActualWidth - (WorkerRail.TranslatePoint(new Point(), header).X - 6));
        }
        if (Math.Abs(slot.Margin.Right - right) > 0.5)
            slot.Margin = new Thickness(0, 0, right, 0);
    }

    private bool CanPin(bool code)
    {
        var scroll = code ? CodeScroll : ChatScroll;
        return _settings.CenterChat && (scroll.ActualWidth - ChatWidth) / 2 >= PinWidth + 20;
    }

    // The header's Context card: "Context" and its arrow on the left, the percentage (Settings) and the ring on the right.
    // With room beside the centered conversation it is one card laid over the header (the pin): its header row toggles it
    // and its breakdown slides open below inside the same outline. Without room the header button shows the card itself
    // and opens the popup. The header button stays in the layout either way, keeping the card's place.
    private sealed class ContextOverlay
    {
        public required Border Card;
        public required Button Header;
        public required Border Body;
        public required TextBlock Arrow;
        public bool Closing;
    }

    private readonly Dictionary<bool, ContextOverlay> _overlays = new();

    private ContextOverlay OverlayFor(bool code)
    {
        if (_overlays.TryGetValue(code, out var made))
            return made;
        var arrow = new TextBlock { Text = "\u25B8", FontSize = 10, Margin = new Thickness(6, 1, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.55), RenderTransform = new RotateTransform() }.WithForeground("Muted");
        var header = new Button { Padding = new Thickness(12, 5, 8, 5), Tag = "ContextHeader", ToolTip = "Show or hide the context breakdown" };
        header.SetResourceReference(StyleProperty, "BareButton");
        header.Click += (_, _) => ContextRing_Click(code ? CodeContextRing : ChatContextRing, new RoutedEventArgs());
        var body = new Border { Padding = new Thickness(14, 2, 14, 12), ClipToBounds = true, Visibility = Visibility.Collapsed };
        var card = new Border { Width = PinWidth, BorderThickness = new Thickness(1), Tag = "ContextCard", Child = new StackPanel { Children = { header, body } } };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "CardHover"); // a little brighter, no outline
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "Card");
        (code ? CodeContextPin : ChatContextPin).Content = card;
        return _overlays[code] = new ContextOverlay { Card = card, Header = header, Body = body, Arrow = arrow };
    }

    // Code: the context cards stack under the header, their right edges flush with Start project's: the lead's ("Lead
    // Context") first, then each other worker that worked in the chat. A card's header opens its breakdown below it (the
    // lead's stays as last left, saved; the others for this app session).
    private readonly HashSet<string> _openWorkerContexts = new();

    private void RefreshCodeContextStack()
    {
        // The stack replaces the header button in Code; hidden, the button keeps the place the stack lines up with (as Chat's).
        CodeContextRing.Visibility = RunClocks.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Hidden; // the clocks take its place
        CodeContextRing.Content = ContextFace(null, null, PinWidth, bordered: true, ContextTitle(CodeLead));
        var entries = _codeContexts.OrderBy(p => p.Key != CodeLead).Select(p => (Role: p.Key, Snapshot: (ContextSnapshot?)p.Value)).ToList();
        if (entries.All(e => e.Role != CodeLead))
            entries.Insert(0, (CodeLead, null));
        var stack = new StackPanel { Tag = "ContextStack" };
        foreach (var (role, s) in entries)
        {
            var open = role == CodeLead ? _settings.ContextPinned : _openWorkerContexts.Contains(role);
            var arrow = new TextBlock { Text = "▸", FontSize = 10, Margin = new Thickness(6, 1, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.55), RenderTransform = new RotateTransform(open ? 90 : 0) }.WithForeground("Muted");
            var header = new Button { Padding = new Thickness(12, 5, 8, 5), Tag = role, ToolTip = "Show or hide the context breakdown",
                Content = ContextFace(s, arrow, double.NaN, bordered: false, ContextTitle(role)) };
            header.SetResourceReference(StyleProperty, "BareButton");
            header.Click += (_, _) =>
            {
                if (role == CodeLead)
                {
                    _settings.ContextPinned = !_settings.ContextPinned;
                    _settings.Save();
                }
                else if (!_openWorkerContexts.Remove(role))
                    _openWorkerContexts.Add(role);
                RefreshCodeContextStack();
            };
            var body = new Border { Padding = new Thickness(14, 2, 14, 12), Visibility = open ? Visibility.Visible : Visibility.Collapsed,
                Child = s is null ? new TextBlock { Text = "No reply yet in this conversation", FontSize = 12 }.WithForeground("Muted") : ContextBody(s, title: false) };
            var card = new Border { Width = PinWidth, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 6), Tag = "ContextCard",
                Child = new StackPanel { Children = { header, body } } };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
            card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "CardHover"); // a little brighter, no outline
            card.MouseLeave += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "Card");
            stack.Children.Add(card);
        }
        CodeContextPin.Content = stack;
        CodeContextPin.Visibility = Visibility.Visible;
        if (CodeContextPin.Parent is FrameworkElement area)
            CodeContextPin.Margin = PinMargin(area, CodeContextRing);
        ReserveCodeContextStack();
    }

    // Without room beside the conversation, the conversation starts below the stack instead of under it.
    private void ReserveCodeContextStack() =>
        CodeScroll.Margin = new Thickness(0, CanPin(true) || CodeContextPin.Visibility != Visibility.Visible ? 0 : CodeContextPin.Margin.Top + CodeContextPin.ActualHeight + 4, 0, 0);

    private void CodeContextPin_SizeChanged(object sender, SizeChangedEventArgs e) => ReserveCodeContextStack();

    // While a collapsed-rail worker card is open, the context stack slides left out of its way, and back when it closes.
    private void DodgeRailCard(bool open)
    {
        if (CodeContextPin.RenderTransform is not TranslateTransform shift)
            CodeContextPin.RenderTransform = shift = new TranslateTransform();
        var by = 0.0;
        if (open && CodeContextPin.IsVisible && RailPopup.Child is FrameworkElement railCard && railCard.IsVisible)
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var cardLeft = railCard.PointToScreen(new Point()).X;
            var stackRight = CodeContextPin.PointToScreen(new Point(CodeContextPin.ActualWidth, 0)).X - shift.X * scale; // where it rests
            by = Math.Min(0, (cardLeft - stackRight) / scale - 12);
        }
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(by, TimeSpan.FromMilliseconds(200)) { EasingFunction = Decelerate });
    }

    private void RefreshContextPins()
    {
        foreach (var code in new[] { false, true })
        {
            if (code)
            {
                RefreshCodeContextStack();
                continue;
            }
            var pin = code ? CodeContextPin : ChatContextPin;
            var ring = code ? CodeContextRing : ChatContextRing;
            var s = ContextOf(code);
            var room = CanPin(code);
            var label = code ? ContextTitle(CodeLead) : "Context";
            ring.Content = ContextFace(s, null, PinWidth, bordered: true, label);
            ring.Opacity = room ? 0 : 1; // the card over it is the one shown
            pin.Visibility = room ? Visibility.Visible : Visibility.Collapsed;
            if (!room)
            {
                AlignContextRing(ring);
                continue;
            }
            var overlay = OverlayFor(code);
            var open = _settings.ContextPinned || overlay.Closing;
            overlay.Header.Content = ContextFace(s, overlay.Arrow, double.NaN, bordered: false, label);
            ((RotateTransform)overlay.Arrow.RenderTransform).Angle = open ? 90 : 0;
            overlay.Body.Child = s is null ? new TextBlock { Text = "No reply yet in this conversation", FontSize = 12 }.WithForeground("Muted") : ContextBody(s, title: false);
            if (overlay.Body.HasAnimatedProperties == false)
                overlay.Body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (pin.Parent is FrameworkElement area && ring.IsVisible)
                pin.Margin = PinMargin(area, ring);
        }
    }

    // The breakdown slides open from nothing to its height, or shut back to nothing (then the card closes), while the arrow
    // turns: 240 ms, decelerating, as the sidebar. Once open it sizes itself again (the height goes back to auto).
    private void AnimatePin(bool code, bool open)
    {
        var overlay = OverlayFor(code);
        var body = overlay.Body;
        body.Visibility = Visibility.Visible;
        body.UpdateLayout();
        var full = body.ActualHeight;
        if (open)
        {
            body.Measure(new Size(PinWidth - 2, double.PositiveInfinity));
            full = body.DesiredSize.Height;
        }
        var time = TimeSpan.FromMilliseconds(240);
        var slide = new DoubleAnimation(open ? 0 : full, open ? full : 0, time) { EasingFunction = Decelerate };
        slide.Completed += (_, _) =>
        {
            body.BeginAnimation(HeightProperty, null);
            body.BeginAnimation(OpacityProperty, null);
            body.Height = double.NaN;
            if (open)
                return;
            overlay.Closing = false;
            body.Visibility = Visibility.Collapsed;
            RefreshContextPins();
        };
        body.BeginAnimation(HeightProperty, slide);
        body.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, time) { EasingFunction = Decelerate });
        overlay.Arrow.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(open ? 90 : 0, time) { EasingFunction = Decelerate });
    }

    // The header row's content; bordered: as its own card (the header button, sizing the overlay's place or shown without
    // room). arrow: the overlay's turning arrow, else a fixed one.
    private FrameworkElement ContextFace(ContextSnapshot? s, TextBlock? arrow, double width, bool bordered, string title = "Context")
    {
        TextBlock Text(string text, string brush, double size = 12) =>
            new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center }.WithForeground(brush);
        var track = new Ellipse { Width = 18, Height = 18, StrokeThickness = 2.5 };
        track.SetResourceReference(Shape.StrokeProperty, "Border");
        var ring = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(track);
        if (s is { NCtx: > 0 })
            ring.Children.Add(Arc(s.Fraction, RingColor(s.Fraction)));
        DockPanel.SetDock(ring, Dock.Right);
        var face = new DockPanel { LastChildFill = false };
        face.Children.Add(ring);
        if (_settings.ContextPercent && s is { NCtx: > 0 })
        {
            var percent = Text($"{s.Fraction:0%}", "Muted");
            percent.Margin = new Thickness(0, 0, 8, 0);
            percent.Tag = "ContextPercent";
            DockPanel.SetDock(percent, Dock.Right);
            face.Children.Add(percent);
        }
        if (s is { Unloaded: true })
        {
            var unloaded = Text("unloaded", "Muted", 10.5);
            unloaded.Margin = new Thickness(0, 0, 8, 0);
            unloaded.Tag = "ContextUnloaded";
            unloaded.ToolTip = "The last call's context; this worker's model has unloaded since";
            DockPanel.SetDock(unloaded, Dock.Right);
            face.Children.Add(unloaded);
        }
        var label = Text(title, "TextPrimary");
        label.FontWeight = FontWeights.SemiBold;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        face.Children.Add(label);
        if (arrow is not null)
            (arrow.Parent as Panel)?.Children.Remove(arrow);
        face.Children.Add(arrow ?? new TextBlock { Text = "\u25B8", FontSize = 10, Margin = new Thickness(6, 1, 12, 0), VerticalAlignment = VerticalAlignment.Center }.WithForeground("Muted"));
        if (!bordered)
            return face;
        var card = new Border { Child = face, Padding = new Thickness(12, 5, 8, 5), BorderThickness = new Thickness(1), Width = width, MinWidth = 140 };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        return card;
    }

    private void ConversationScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            RefreshContextPins();
    }

    // The popup's card (title: with its "Context" heading); s null: no reply yet.
    private Border ContextCard(ContextSnapshot? s, bool title = true)
    {
        StackPanel panel;
        if (s is null)
        {
            panel = new StackPanel();
            if (title)
                panel.Children.Add(new TextBlock { Text = "Context", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) }.WithForeground("TextPrimary"));
            panel.Children.Add(new TextBlock { Text = "No reply yet in this conversation", FontSize = 12 }.WithForeground("Muted"));
        }
        else
            panel = ContextBody(s, title);
        var card = new Border { Child = panel, Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(1) };
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
        foreach (var s in _codeContexts.Values.Where(s => s.Role == role))
            s.NCtx = n;
        foreach (var code in new[] { false, true })
            if (ContextOf(code) is { } s && (s.Role ?? TargetOf(code ? _code : _chat)?.Role) == role)
            {
                s.NCtx = n;
                ShowContext(code, s);
            }
        RefreshContextSection();
    }

    // The worker details column's Context section, open or closed as last left (saved). Chat: the conversation's snapshot.
    // Code: one "<worker> Context" card per worker that worked in it, the lead's first (the summary line is the lead's).
    private void RefreshContextSection()
    {
        var code = NavCode.IsChecked == true;
        var s = ContextOf(code);
        var open = _settings.ContextExpanded;
        ContextSectionToggle.IsChecked = open;
        ContextSummary.Text = s is null ? "—" : s.NCtx is > 0 ? $"{s.Fraction:0%}  ·  {Tokens(s.Used)} / {Tokens(s.NCtx.Value)}" : $"{Tokens(s.Used)} / ?";
        var empty = new TextBlock { Text = "No reply yet in this conversation", FontSize = 12 };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        FrameworkElement? body = s is null ? empty : ContextBody(s, title: false);
        if (code && _codeContexts.Count > 0)
        {
            var cards = new StackPanel();
            foreach (var (role, worker) in _codeContexts.OrderBy(p => p.Key != CodeLead))
            {
                var heading = new TextBlock { Text = ContextTitle(role), FontSize = 12, FontWeight = FontWeights.SemiBold, Tag = "ContextTitle",
                    Margin = new Thickness(0, cards.Children.Count == 0 ? 0 : 14, 0, 6) }.WithForeground("TextPrimary");
                cards.Children.Add(heading);
                cards.Children.Add(ContextBody(worker, title: false));
            }
            body = cards;
        }
        ContextSectionBody.Content = !open ? null : body;
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
            var overlay = OverlayFor(code);
            if (overlay.Closing)
                return; // already sliding shut
            var open = !_settings.ContextPinned;
            _settings.ContextPinned = open;
            _settings.Save();
            overlay.Closing = !open; // the breakdown stays until it has slid shut
            RefreshContextPins();
            AnimatePin(code, open);
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
        card.Width = PinWidth; // under the header button, edges flush with it
        return new Popup { Child = card, PlacementTarget = anchor, Placement = PlacementMode.Bottom, HorizontalOffset = anchor.ActualWidth - PinWidth, VerticalOffset = 6,
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
        if (s.Unloaded)
        {
            var unloaded = Text("Last call · unloaded", "Muted", 11);
            unloaded.Margin = new Thickness(0, -4, 0, 8);
            unloaded.Tag = "ContextUnloaded";
            panel.Children.Add(unloaded);
        }
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

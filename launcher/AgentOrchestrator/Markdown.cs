using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgentOrchestrator;

// A small Markdown renderer for chat replies: headings, paragraphs, bullet and numbered lists (nested by indent), block
// quotes, fenced code, tables, rules, and inline bold, italic, strikethrough, code and links. Colours are theme resource
// references, so a theme change restyles rendered replies. Anything it does not recognise shows as plain text.
internal static class Markdown
{
    // Where a clicked link opens (the app's browser); null: the default browser.
    public static Action<string>? OpenLink;

    // Settings > Appearance > Reading size: Comfortable and Large space replies out as ChatGPT does (taller lines, more room
    // around paragraphs, headings, lists, code and tables); Compact keeps the denser layout.
    public static bool Roomy = true;

    private static double R(double compact, double roomy) => Roomy ? roomy : compact;

    // Messages' and replies' weights: Light text, Regular for bold, headings and table headers (the styles Fonts\ has).
    public static readonly FontWeight TextWeight = FontWeights.Light, StrongWeight = FontWeights.Normal;

    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    private static readonly Regex Rule = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$");
    private static readonly Regex Item = new(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$");
    private static readonly Regex Fence = new(@"^\s*(```|~~~)\s*([\w#+.-]*)");
    private static readonly Regex TableRule = new(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$");
    private static readonly Regex Inline = new(
        @"(?<code>`+)(?<codetext>.+?)\k<code>|\*\*(?<bold>.+?)\*\*|(?<!\w)__(?<bold2>.+?)__(?!\w)|~~(?<strike>.+?)~~|" +
        @"\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)|(?<!\*)\*(?![\s*])(?<em>.+?)(?<!\s)\*|(?<!\w)_(?![\s_])(?<em2>.+?)(?<!\s)_(?!\w)|" +
        @"(?<bare>https?://[^\s<>()]+[^\s<>().,;:!?'""])");

    // The document being built is the viewer's page (the editor's formatted Markdown), styled after Notepad++'s dark sheet in
    // the theme's colours: banded and accent-barred headings, quotes and code, striped tables, accent list markers.
    private static bool _page;

    // muted: thinking text, in the theme's muted colour. page: the Markdown viewer's look, not a reply's.
    public static FlowDocument ToDocument(string markdown, double fontSize, bool muted = false, bool page = false)
    {
        var outer = _page;
        _page = page || outer; // a quote's inner document keeps its page's look
        try { return Build(markdown, fontSize, muted); }
        finally { _page = outer; }
    }

    private static FlowDocument Build(string markdown, double fontSize, bool muted)
    {
        // Text weights are about 0.7 of the usual (Light body, Regular bold and headings): a lighter, smoother page
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = fontSize, TextAlignment = TextAlignment.Left, FontWeight = TextWeight,
            LineHeight = _page ? Math.Round(fontSize * 1.65) : Roomy ? Math.Round(fontSize * 1.5) : double.NaN }; // Claude's 1.5; a minimum: a taller line (a heading) keeps its own
        doc.SetResourceReference(FlowDocument.ForegroundProperty, _page ? "MdBody" : muted ? "Muted" : "TextPrimary");
        var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
        var paragraph = new List<string>();
        var lists = new List<(int Indent, List List)>(); // the open lists, outermost first
        void EndParagraph()
        {
            if (paragraph.Count == 0)
                return;
            var p = Para();
            for (var i = 0; i < paragraph.Count; i++)
            {
                if (i > 0)
                    p.Inlines.Add(new LineBreak());
                AddInlines(p.Inlines, paragraph[i]);
            }
            Add(p);
            paragraph.Clear();
        }
        void Add(Block block)
        {
            if (lists.Count > 0 && block is not List)
            {
                if (_page)
                    block.SetResourceReference(TextElement.ForegroundProperty, "MdBody"); // the list's accent is for its markers
                lists[^1].List.ListItems.LastOrDefault()?.Blocks.Add(block); // a paragraph continuing the open list item
            }
            else
                doc.Blocks.Add(block);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            // "4. ```python": the item, then the fence on a line of its own
            if (Item.Match(line) is { Success: true } fenced && Fence.IsMatch(fenced.Groups[3].Value))
            {
                lines.Insert(i + 1, fenced.Groups[3].Value);
                line = lines[i] = fenced.Groups[1].Value + fenced.Groups[2].Value + " ";
            }
            if (Fence.Match(line) is { Success: true } fence)
            {
                EndParagraph();
                var code = new List<string>();
                while (++i < lines.Count && !lines[i].TrimStart().StartsWith(fence.Groups[1].Value))
                    code.Add(lines[i]);
                lists.Clear();
                doc.Blocks.Add(CodeBlock(string.Join("\n", code), fence.Groups[2].Value, closed: i < lines.Count));
                continue;
            }
            if (line.Trim().Length == 0)
            {
                EndParagraph();
                // a blank line ends the lists unless an indented or list line follows
                if (i + 1 < lines.Count && !Item.IsMatch(lines[i + 1]) && !lines[i + 1].StartsWith("  "))
                    lists.Clear();
                continue;
            }
            if (Heading.Match(line) is { Success: true } heading)
            {
                EndParagraph();
                lists.Clear();
                var level = heading.Groups[1].Length;
                var p = Para();
                p.FontSize = fontSize * (level switch { 1 => 1.5, 2 => R(1.3, 1.25), 3 => R(1.15, 1.06), _ => 1.0 });
                // headings in the serif, as Claude's, at Regular (0.7 of Medium/SemiBold; their size sets them apart)
                p.FontFamily = AppFonts.Serif;
                p.FontWeight = StrongWeight;
                p.LineHeight = Roomy ? Math.Round(p.FontSize * 1.3) : double.NaN;
                // more room above than below: a heading belongs to the section under it (a reply's first block has none: NoTopMargin)
                p.Margin = new Thickness(0, level <= 2 ? R(14, 26) : R(10, 20), 0, R(4, 8));
                if (_page) // the viewer's: the app's font, # underlined, ## on a band with the accent bar at its left
                {
                    p.FontSize = fontSize * (level switch { 1 => 1.6, 2 => 1.3, 3 => 1.12, _ => 1.0 });
                    p.ClearValue(TextElement.FontFamilyProperty);
                    p.LineHeight = Math.Round(p.FontSize * 1.35);
                    p.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
                    p.Margin = new Thickness(0, level <= 2 ? 26 : 20, 0, level == 2 ? 14 : 10);
                    if (level == 1)
                    {
                        (p.BorderThickness, p.Padding) = (new Thickness(0, 0, 0, 1), new Thickness(0, 0, 0, 8));
                        p.SetResourceReference(Block.BorderBrushProperty, "Border");
                    }
                    else if (level == 2)
                    {
                        (p.BorderThickness, p.Padding) = (new Thickness(4, 0, 0, 0), new Thickness(10, 6, 10, 6));
                        p.SetResourceReference(Block.BorderBrushProperty, "Accent");
                        p.SetResourceReference(TextElement.BackgroundProperty, "MdBand");
                    }
                }
                AddInlines(p.Inlines, heading.Groups[2].Value);
                doc.Blocks.Add(p);
                continue;
            }
            if (Rule.IsMatch(line) && paragraph.Count == 0)
            {
                lists.Clear();
                var rule = new Paragraph { Margin = _page ? new Thickness(0, 28, 0, 28) : new Thickness(0, R(8, 20), 0, R(8, 22)), BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 2, LineHeight = 2 };
                rule.SetResourceReference(Block.BorderBrushProperty, "Border");
                doc.Blocks.Add(rule);
                continue;
            }
            if (line.TrimStart().StartsWith('>'))
            {
                EndParagraph();
                lists.Clear();
                var quote = new List<string>();
                for (; i < lines.Count && lines[i].TrimStart().StartsWith('>'); i++)
                    quote.Add(lines[i].TrimStart()[1..].TrimStart());
                i--;
                var section = new Section { Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(10, 0, 0, 0), BorderThickness = new Thickness(3, 0, 0, 0) };
                section.SetResourceReference(Block.BorderBrushProperty, _page ? "Accent" : "Muted");
                section.SetResourceReference(TextElement.ForegroundProperty, _page ? "MdBody" : "Muted");
                if (_page) // the viewer's: on a band, behind the accent bar
                {
                    (section.Margin, section.Padding, section.BorderThickness) = (new Thickness(0, 16, 0, 16), new Thickness(16, 10, 16, 0), new Thickness(4, 0, 0, 0));
                    section.SetResourceReference(TextElement.BackgroundProperty, "MdBand");
                }
                var inner = ToDocument(string.Join("\n", quote), fontSize);
                foreach (var block in inner.Blocks.ToList())
                {
                    inner.Blocks.Remove(block);
                    section.Blocks.Add(block);
                }
                doc.Blocks.Add(section);
                continue;
            }
            if (line.Contains('|') && i + 1 < lines.Count && TableRule.IsMatch(lines[i + 1]))
            {
                EndParagraph();
                lists.Clear();
                var rows = new List<string> { line };
                for (i += 2; i < lines.Count && lines[i].Contains('|') && lines[i].Trim().Length > 0; i++)
                    rows.Add(lines[i]);
                i--;
                doc.Blocks.Add(TableOf(rows));
                continue;
            }
            if (Item.Match(line) is { Success: true } item)
            {
                EndParagraph();
                var indent = item.Groups[1].Value.Replace("\t", "    ").Length;
                var ordered = char.IsDigit(item.Groups[2].Value[0]);
                while (lists.Count > 0 && lists[^1].Indent > indent)
                    lists.RemoveAt(lists.Count - 1);
                if (lists.Count == 0 || lists[^1].Indent < indent || (lists[^1].List.MarkerStyle == TextMarkerStyle.Decimal) != ordered)
                {
                    if (lists.Count > 0 && lists[^1].Indent == indent)
                        lists.RemoveAt(lists.Count - 1); // the other kind of list at the same depth starts a new one
                    var list = new List { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 2, 0, R(6, 14)), Padding = new Thickness(R(22, 26), 0, 0, 0) };
                    if (_page)
                        list.SetResourceReference(TextElement.ForegroundProperty, "Accent"); // the markers; the items' text is MdBody
                    if (ordered && int.TryParse(item.Groups[2].Value.TrimEnd('.', ')'), out var start) && start > 1)
                        list.StartIndex = start;
                    if (lists.Count > 0 && lists[^1].List.ListItems.LastOrDefault() is { } parent)
                    {
                        list.Margin = new Thickness(0);
                        parent.Blocks.Add(list);
                    }
                    else
                        doc.Blocks.Add(list);
                    lists.Add((indent, list));
                }
                var p = Para();
                p.Margin = new Thickness(0, R(1, 3), 0, R(1, 3));
                if (_page)
                    p.SetResourceReference(TextElement.ForegroundProperty, "MdBody");
                AddInlines(p.Inlines, item.Groups[3].Value);
                lists[^1].List.ListItems.Add(new ListItem(p));
                continue;
            }
            paragraph.Add(lists.Count > 0 ? line.Trim() : line.TrimEnd());
        }
        EndParagraph();
        ColorEmoji(doc.Blocks);
        return doc;
    }

    // WPF draws emoji as gray outlines: each becomes a colour image drawn by Direct2D (EmojiImages), as browsers show them.
    private static void ColorEmoji(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks.ToList())
            switch (block)
            {
                case Paragraph p:
                    ColorEmoji(p.Inlines);
                    break;
                case List list:
                    foreach (var item in list.ListItems.ToList())
                        ColorEmoji(item.Blocks);
                    break;
                case Table table:
                    foreach (var cell in table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).ToList())
                        ColorEmoji(cell.Blocks);
                    break;
                case Section section:
                    ColorEmoji(section.Blocks);
                    break;
            }
    }

    private static IEnumerable<Match> EmojiIn(string text) => EmojiImages.Match.Matches(text);

    public static bool HasEmoji(string text) => EmojiIn(text).Any();

    // Plain text (a user's message: no Markdown) with its emoji in colour, sized to its text as a bubble needs.
    public static TextBlock EmojiTextBlock(string text, double fontSize)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = fontSize };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                block.Inlines.Add(new LineBreak());
            block.Inlines.Add(new Run(lines[i]));
        }
        ColorEmoji(block.Inlines);
        return block;
    }

    private static void ColorEmoji(InlineCollection inlines)
    {
        foreach (var inline in inlines.ToList())
        {
            if (inline is Span span)
            {
                ColorEmoji(span.Inlines);
                continue;
            }
            if (inline is not Run run || run.Text is not { Length: > 0 } text)
                continue;
            var at = 0;
            foreach (var m in EmojiIn(text))
            {
                if (EmojiImages.Inline(m.Value, run.FontSize) is not { } image)
                    continue; // no Direct2D: the emoji stays in the text
                if (m.Index > at)
                    inlines.InsertBefore(run, Piece(run, text[at..m.Index]));
                inlines.InsertBefore(run, image);
                at = m.Index + m.Length;
            }
            if (at == 0)
                continue;
            if (at < text.Length)
                inlines.InsertBefore(run, Piece(run, text[at..]));
            inlines.Remove(run);
        }

        static Run Piece(Run from, string text) // keeps the run's own look (inline code's font and chip)
        {
            var run = new Run(text);
            for (var e = from.GetLocalValueEnumerator(); e.MoveNext();)
                if (e.Current.Property != Run.TextProperty && !e.Current.Property.ReadOnly)
                    run.SetValue(e.Current.Property, e.Current.Value);
            return run;
        }
    }

    // While a reply streams: the length of the longest prefix of text that more text cannot render differently, so its blocks
    // can be kept and only the rest re-rendered (ToDocument of the prefix, then of the rest, gives ToDocument of the whole).
    // A prefix ends after a closing code fence, or before a complete line that follows a blank line and does not continue a
    // list (ToDocument's rule for ending lists). from: a length this returned before, or 0.
    // ponytail: a table row that starts with ``` reads as a fence here; the turn's final full render corrects it.
    public static int StableLength(string text, int from)
    {
        var (stable, blank) = (from, false);
        string? fence = null;
        for (int at = from, end; (end = text.IndexOf('\n', at)) >= 0; at = end + 1)
        {
            var line = text[at..end];
            if (fence is not null)
            {
                if (line.TrimStart().StartsWith(fence))
                    (fence, stable) = (null, end + 1);
                continue;
            }
            if (line.Trim().Length == 0)
            {
                blank = true;
                continue;
            }
            if (blank && !Item.IsMatch(line) && !line.StartsWith("  "))
                stable = at;
            blank = false;
            if (Fence.Match(line) is { Success: true } open)
                fence = open.Groups[1].Value;
        }
        return stable;
    }

    // Where the code of a code block still open at the text's end starts (after its fence line, which must be complete); null when
    // none is open. from: a length StableLength returned (outside any block).
    public static int? OpenCodeStart(string text, int from)
    {
        string? fence = null;
        var start = 0;
        for (int at = from, end; (end = text.IndexOf('\n', at)) >= 0; at = end + 1)
        {
            var line = text[at..end];
            if (fence is not null)
            {
                if (line.TrimStart().StartsWith(fence))
                    fence = null;
                continue;
            }
            if (Fence.Match(line) is { Success: true } open)
                (fence, start) = (open.Groups[1].Value, end + 1);
        }
        return fence is null ? null : start;
    }

    // A fence line's marker (``` or ~~~) and language.
    public static (string Fence, string Language) FenceOf(string line) => Fence.Match(line) is { Success: true } m ? (m.Groups[1].Value, m.Groups[2].Value) : ("", "");

    // A reply's first block starts right under its header: a heading's or a rule's room above goes.
    public static void NoTopMargin(Block? first)
    {
        if (first is Paragraph p)
            p.Margin = new Thickness(p.Margin.Left, 0, p.Margin.Right, p.Margin.Bottom);
    }

    private static Paragraph Para()
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, _page ? 12 : R(8, 14)) };
        if (!Roomy && !_page)
            p.LineHeight = double.NaN;
        return p;
    }

    // A FlowDocument paragraph cannot round its corners, so a code block is a rounded card: the coloured code (wrapped, clear
    // of the button's column) and a copy button at the top right, whose Tag is the raw code. MainWindow wires the button:
    // copying, and keeping it in view while the block scrolls (its TranslateTransform).
    private static Block CodeBlock(string code, string language, bool closed = true)
    {
        var pieces = Highlight(code, language);
        if (_page) // the viewer's: the listing alone on a blue-tinted card, the accent bar down its left edge
        {
            var inner = new Border { Child = SelectableCode(pieces, numbered: false), Padding = new Thickness(2, 13, 2, 13), BorderThickness = new Thickness(0, 1, 1, 1), CornerRadius = new CornerRadius(0, 4, 4, 0) };
            inner.SetResourceReference(Border.BackgroundProperty, "MdCode");
            inner.SetResourceReference(Border.BorderBrushProperty, "Border");
            var bar = new Border { Child = inner, Padding = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(4) };
            bar.SetResourceReference(Border.BackgroundProperty, "Accent");
            return new BlockUIContainer(bar) { Margin = new Thickness(0, 6, 0, 16) };
        }
        var lineCount = code.Count(c => c == (char)10) + 1;
        // selectable (and numbered, unless one line or huge) once the block is closed: a streaming one is re-rendered every frame
        UIElement body = closed ? SelectableCode(pieces, numbered: lineCount is >= 2 and <= MaxNumbered) : OpenListing(pieces);
        var copyText = new TextBlock { FontSize = 12, FontWeight = FontWeights.Medium };
        CopyLabel(copyText, copied: false);
        var copy = new Button
        {
            Content = copyText, Height = 24, Padding = new Thickness(7, 0, 7, 0), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 10, 0), ToolTip = "Copy code", Tag = code, RenderTransform = new TranslateTransform(),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        copy.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        // the site's code block: a header bar (code icon, the language), a hairline under it, then the listing
        var icon = new TextBlock { Text = CodeGlyph, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(FrameworkElement.StyleProperty, "RailGlyph");
        var name = new TextBlock { Text = language.Length > 0 ? language : "code", FontFamily = AppFonts.Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var header = new Border { Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name } }, Height = 42, Padding = new Thickness(14, 0, 90, 0), BorderThickness = new Thickness(0, 0, 0, 1) };
        header.SetResourceReference(Border.BorderBrushProperty, "Border");
        var listing = new Border { Child = body, Padding = new Thickness(0, 10, 0, 10) };
        var card = new Border { Child = new Grid { Children = { new StackPanel { Children = { header, listing } }, copy } }, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
        card.SetResourceReference(Border.BackgroundProperty, "Card"); // the surface shade with a hairline ring
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        return new BlockUIContainer(card) { Margin = new Thickness(0, R(2, 6), 0, R(10, 16)) };
    }

    // A whole file, coloured as a code block's listing (the file viewer): numbered rows unless it is huge.
    public static UIElement CodeListing(string code, string language)
    {
        var pieces = Highlight(code, language);
        return SelectableCode(pieces, numbered: code.Count(c => c == (char)10) + 1 <= MaxNumbered);
    }

    // Code's size and its line-number gutter's width (the editor panel draws its text and gutter with these too).
    public static double CodeFontSize => R(12.5, 13.5);
    public static double GutterWidth(int lineCount) => Math.Max(2, lineCount.ToString().Length) * 7 + 22;

    private const int MaxNumbered = 1500; // longer listings stay one block of text: a row each would slow a render

    // The copy button's content: the copy glyph and "Copy", or a check and "Copied" in green.
    public static void CopyLabel(TextBlock label, bool copied)
    {
        label.Inlines.Clear();
        var glyph = new Run(copied ? CopiedGlyph : CopyGlyph) { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 };
        label.Inlines.Add(glyph);
        label.Inlines.Add(new Run(copied ? "  Copied" : "  Copy"));
        label.SetResourceReference(TextBlock.ForegroundProperty, copied ? "CodeComment" : "Muted");
    }

    // A streaming block's listing: a text block per line, so new code lays out again only the lines it reaches (one text
    // block of the whole listing is laid out again on every frame, which slows a long listing down to a crawl).
    private static StackPanel OpenListing(List<(string Text, string? Color)> pieces)
    {
        var listing = new StackPanel { Margin = new Thickness(14, 1, 14, 1), Tag = "codetext" };
        foreach (var line in CodeLines(pieces))
        {
            var text = CodeLine();
            SetCodeLine(text, line);
            listing.Children.Add(text);
        }
        return listing;
    }

    public static TextBlock CodeLine()
    {
        var text = new TextBlock { FontFamily = AppFonts.Mono, FontWeight = TextWeight, FontSize = CodeFontSize, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "CodeText");
        if (Roomy)
            text.LineHeight = 20.5;
        return text;
    }

    public static void SetCodeLine(TextBlock line, List<(string Text, string? Color)> pieces)
    {
        line.Inlines.Clear();
        foreach (var (piece, color) in pieces)
        {
            var run = new Run(piece);
            if (color is not null)
                run.SetResourceReference(TextElement.ForegroundProperty, color);
            line.Inlines.Add(run);
        }
    }

    // Highlighted pieces cut at line ends: a list of pieces per line.
    public static List<List<(string Text, string? Color)>> CodeLines(List<(string Text, string? Color)> pieces)
    {
        var lines = new List<List<(string Text, string? Color)>> { new() };
        foreach (var (piece, color) in pieces)
        {
            var parts = piece.Split((char)10);
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    lines.Add(new());
                if (parts[i].Length > 0)
                    lines[^1].Add((parts[i], color));
            }
        }
        return lines;
    }

    // A listing as selectable read-only text (drag to select, Ctrl+C; the Copy button stays), its syntax colours kept. The
    // highlighted pieces are cut at line ends (a block comment can span lines, so the code is highlighted whole first): one
    // paragraph per line, wrapping under itself. numbered: each line's number is a UI element hanging in a muted gutter with
    // a hairline down its right edge, so it is never part of a selection; Copy takes the selected code only (SelectedCode).
    private static FrameworkElement SelectableCode(List<(string Text, string? Color)> pieces, bool numbered)
    {
        var lines = CodeLines(pieces);
        var gutter = numbered ? GutterWidth(lines.Count) : 0;
        var indent = numbered ? gutter + 10 : 14; // a code block's text sits 10 px right of the gutter, or 14 px in from the edge
        var document = new FlowDocument { PagePadding = new Thickness(0, 1, 0, 1), FontFamily = AppFonts.Mono, FontWeight = TextWeight, FontSize = CodeFontSize };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "CodeText");
        for (var n = 0; n < lines.Count; n++)
        {
            var line = new Paragraph { Margin = new Thickness(indent, 0, 14, 0), TextIndent = numbered ? -indent : 0 };
            if (Roomy)
                line.LineHeight = 20.5;
            if (numbered)
            {
                // drawn 3 px lower (a render offset, so the line keeps its height): the smaller number then sits on the code's baseline
                var number = new TextBlock { Text = (n + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11, Width = gutter, TextAlignment = TextAlignment.Center,
                    RenderTransform = new TranslateTransform(0, 3) };
                number.SetResourceReference(TextBlock.ForegroundProperty, "CodeLineNumber");
                line.Inlines.Add(new InlineUIContainer(new Border { Child = number, Width = indent }) { BaselineAlignment = BaselineAlignment.Center });
            }
            foreach (var (text, color) in lines[n])
            {
                var run = new Run(text);
                if (color is not null)
                    run.SetResourceReference(TextElement.ForegroundProperty, color);
                line.Inlines.Add(run);
            }
            document.Blocks.Add(line);
        }
        var box = new RichTextBox
        {
            Document = document, IsReadOnly = true, IsReadOnlyCaretVisible = false, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Tag = "codetext", Cursor = System.Windows.Input.Cursors.IBeam, SelectionOpacity = 0.35,
        };
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Accent");
        box.CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.ApplicationCommands.Copy, (_, e) =>
        {
            e.Handled = true;
            if (SelectedCode(box) is { Length: > 0 } code)
                try { Clipboard.SetText(code); }
                catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy: nothing copied
        }));
        // the wheel scrolls the conversation, not this box (it never scrolls itself)
        box.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            (VisualTreeHelper.GetParent(box) as UIElement)?.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent });
        };
        if (!numbered)
            return box;
        var rule = new Border { Width = gutter, HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0, 0, 1, 0) };
        rule.SetResourceReference(Border.BorderBrushProperty, "Border");
        return new Grid { Children = { rule, box } };
    }

    // The code a listing's selection covers, without its line numbers (they are UI elements, not text): one line per paragraph.
    internal static string SelectedCode(RichTextBox box) => CodeBetween(box, box.Selection.Start, box.Selection.End);

    // A listing's whole code (SelectableCode).
    internal static string CodeOf(RichTextBox box) => CodeBetween(box, box.Document.ContentStart, box.Document.ContentEnd);

    private static string CodeBetween(RichTextBox box, TextPointer start, TextPointer end)
    {
        var blocks = box.Document.Blocks.ToList();
        var text = new StringBuilder();
        Block? last = null;
        for (var at = start; at is not null && at.CompareTo(end) < 0; at = at.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (at.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text || at.Paragraph is not { } line)
                continue;
            if (last is not null && line != last)
                text.Append('\n', Math.Max(1, blocks.IndexOf(line) - blocks.IndexOf(last))); // blank lines between count too
            last = line;
            var run = at.GetTextInRun(LogicalDirection.Forward);
            var room = at.GetOffsetToPosition(end);
            text.Append(room < run.Length ? run[..room] : run);
        }
        return text.ToString();
    }

    public const string CopyGlyph = "\uE8C8", CopiedGlyph = "\uE73E", CodeGlyph = "\uE943";

    // The code blocks' cards (Border > Grid of code text and copy button), quoted ones included.
    public static IEnumerable<Border> CodeBlocks(IEnumerable<Block> blocks) => blocks.SelectMany(block => block switch
    {
        BlockUIContainer { Child: Border card } => new[] { card },
        Section section => CodeBlocks(section.Blocks),
        _ => Enumerable.Empty<Border>(),
    });

    // ---- syntax colours for code blocks: one tokenizer for the common languages (VS Code dark colours, readable on every theme)

    // Theme resource keys (MainWindow.ApplyTheme sets dark or light shades).
    private const string CommentColor = "CodeComment", StringColor = "CodeString", NumberColor = "CodeNumber", KeywordColor = "CodeKeyword",
        ControlColor = "CodeControl", CallColor = "CodeCall", TypeColor = "CodeType";

    private static readonly HashSet<string> Control = new(StringComparer.Ordinal)
    {
        "if", "else", "elif", "for", "foreach", "while", "do", "return", "break", "continue", "switch", "case", "default", "try", "catch",
        "except", "finally", "throw", "raise", "yield", "await", "goto", "match", "when", "then", "fi", "done", "esac", "loop", "with", "pass",
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "def", "class", "struct", "enum", "interface", "record", "function", "func", "fn", "let", "var", "const", "val", "mut", "public",
        "private", "protected", "internal", "static", "readonly", "sealed", "abstract", "override", "virtual", "async", "void", "int", "uint",
        "long", "short", "byte", "string", "bool", "boolean", "float", "double", "decimal", "char", "object", "dynamic", "new", "delete",
        "import", "from", "export", "as", "using", "namespace", "package", "include", "true", "false", "null", "None", "True", "False", "nil",
        "undefined", "this", "self", "super", "base", "in", "is", "not", "and", "or", "lambda", "typeof", "instanceof", "extends", "implements",
        "impl", "trait", "pub", "use", "mod", "type", "where", "global", "nonlocal", "assert", "del", "echo", "local", "export", "param",
        "SELECT", "FROM", "WHERE", "INSERT", "INTO", "UPDATE", "DELETE", "CREATE", "TABLE", "JOIN", "ON", "AND", "OR", "NOT", "NULL", "AS",
        "ORDER", "BY", "GROUP", "VALUES", "SET", "LIMIT",
    };

    private static readonly Regex Tokens = new(
        @"(?<comment>//[^\n]*|/\*.*?\*/|<!--.*?-->|(?<![\w$])#(?![\w-]*\()[^\n]*|--[^\n]*)|" +
        @"(?<string>@?""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`)|" +
        @"(?<number>\b(?:0x[0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\b)|" +
        @"(?<word>[A-Za-z_$][\w$]*)", RegexOptions.Singleline);

    // (text, colour or null for the default) pieces of the code, in order.
    public static List<(string Text, string? Color)> Highlight(string code, string language)
    {
        var lang = language.ToLowerInvariant();
        var hashComments = lang is "" or "py" or "python" or "sh" or "bash" or "shell" or "zsh" or "ps1" or "powershell" or "pwsh" or "rb" or "ruby"
            or "yaml" or "yml" or "toml" or "r" or "perl" or "dockerfile" or "makefile" or "ini" or "conf";
        var dashComments = lang is "sql" or "lua" or "haskell" or "hs";
        var pieces = new List<(string, string?)>();
        var at = 0;
        foreach (Match m in Tokens.Matches(code))
        {
            string? color = null;
            if (m.Groups["comment"].Success)
            {
                var text = m.Value;
                if ((text.StartsWith('#') && !hashComments) || (text.StartsWith("--") && !dashComments))
                    continue; // # is a preprocessor line or CSS id, -- an operator, in other languages
                color = CommentColor;
            }
            else if (m.Groups["string"].Success)
                color = StringColor;
            else if (m.Groups["number"].Success)
                color = NumberColor;
            else
            {
                var word = m.Value;
                var next = m.Index + m.Length < code.Length ? code[m.Index + m.Length] : '\0';
                color = Control.Contains(word) ? ControlColor : Keywords.Contains(word) ? KeywordColor : next == '(' ? CallColor
                    : char.IsUpper(word[0]) && word.Length > 1 && word.Any(char.IsLower) ? TypeColor : null;
                if (color is null)
                    continue;
            }
            if (m.Index > at)
                pieces.Add((code[at..m.Index], null));
            pieces.Add((m.Value, color));
            at = m.Index + m.Length;
        }
        if (at < code.Length)
            pieces.Add((code[at..], null));
        return pieces;
    }

    private static Table TableOf(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var header = Cells(rows[0]);
        var table = new Table { CellSpacing = 0, Margin = _page ? new Thickness(0, 16, 0, 16) : new Thickness(0, R(2, 6), 0, R(10, 16)), BorderThickness = new Thickness(1, 1, 0, 0) };
        table.SetResourceReference(Table.BorderBrushProperty, "Border");
        // the viewer's columns share the width by their longest cell, as a browser's do (a short ID column stays narrow)
        // (shares of 1: star values that sum past about 100 can leave the last column nearly no width)
        var widths = header.Select((_, c) =>
        {
            var texts = rows.Select(r => Cells(r).ElementAtOrDefault(c) ?? "").ToList();
            return (double)Math.Max(Math.Min(texts.Max(t => t.Length), 60), texts.SelectMany(t => t.Split(' ')).Max(w => w.Length) + 5);
        }).ToArray();
        foreach (var width in widths)
            table.Columns.Add(_page ? new TableColumn { Width = new GridLength(width / widths.Sum(), GridUnitType.Star) } : new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            var cells = Cells(rows[r]);
            for (var c = 0; c < header.Length; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                AddInlines(p.Inlines, c < cells.Length ? cells[c] : "");
                if (r == 0)
                    p.FontWeight = StrongWeight;
                var cell = new TableCell(p) { Padding = _page ? new Thickness(10, 8, 10, 8) : Roomy ? new Thickness(12, 7, 12, 7) : new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Border");
                if (_page) // the viewer's: a centred, brighter header on its own shade, striped rows on the band
                {
                    cell.SetResourceReference(TableCell.BackgroundProperty, r == 0 ? "MdTableHead" : r % 2 == 0 ? "MdBandAlt" : "MdBand");
                    if (r == 0)
                    {
                        p.TextAlignment = TextAlignment.Center;
                        p.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
                    }
                }
                else if (r == 0)
                    cell.SetResourceReference(TableCell.BackgroundProperty, "Hover");
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        return table;
    }

    // links: false inside a link's own text, which would otherwise match itself again (a bare URL is its own label) and
    // recurse until the stack overflows.
    public static void AddInlines(InlineCollection target, string text, bool links = true)
    {
        var at = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > at)
                target.Add(new Run(text[at..m.Index]));
            at = m.Index + m.Length;
            if (!links && (m.Groups["url"].Success || m.Groups["bare"].Success))
            {
                target.Add(new Run(m.Groups["label"].Success ? m.Groups["label"].Value : m.Value));
                continue;
            }
            if (m.Groups["codetext"].Success)
            {
                // inline code keeps the text colour on a light chip, like other AI chat apps
                var code = new Run(m.Groups["codetext"].Value.Trim()) { FontFamily = AppFonts.Mono };
                code.SetResourceReference(TextElement.BackgroundProperty, "InlineCode");
                if (_page) // the viewer's: string-coloured, as Notepad++ shows it
                    code.SetResourceReference(TextElement.ForegroundProperty, "CodeString");
                target.Add(code);
            }
            else if (m.Groups["bold"].Success || m.Groups["bold2"].Success)
            {
                var bold = new Bold { FontWeight = StrongWeight }; // 0.7 of Claude's 600
                if (_page)
                    bold.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary"); // brighter than the page's text
                AddInlines(bold.Inlines, m.Groups["bold"].Success ? m.Groups["bold"].Value : m.Groups["bold2"].Value, links);
                target.Add(bold);
            }
            else if (m.Groups["em"].Success || m.Groups["em2"].Success)
            {
                var em = new Italic();
                if (_page)
                    em.SetResourceReference(TextElement.ForegroundProperty, "CodeControl"); // Notepad++'s purple, the theme's shade
                AddInlines(em.Inlines, m.Groups["em"].Success ? m.Groups["em"].Value : m.Groups["em2"].Value, links);
                target.Add(em);
            }
            else if (m.Groups["strike"].Success)
            {
                var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlines(strike.Inlines, m.Groups["strike"].Value, links);
                target.Add(strike);
            }
            else
            {
                var url = m.Groups["url"].Success ? m.Groups["url"].Value : m.Groups["bare"].Value;
                var label = m.Groups["label"].Success ? m.Groups["label"].Value : url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
                {
                    target.Add(new Run(m.Value)); // only web and mail links open
                    continue;
                }
                var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
                AddInlines(link.Inlines, label, links: false);
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentLight");
                link.RequestNavigate += (_, e) =>
                {
                    if (OpenLink is { } open)
                        open(e.Uri.AbsoluteUri);
                    else
                        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                };
                target.Add(link);
            }
        }
        if (at < text.Length)
            target.Add(new Run(text[at..]));
    }

    // --self-test: a bare URL, a link labelled with its URL, and a bare URL in bold each render as one link (they used to
    // recurse until the stack overflowed, which ends the app without any error).
    public static void SelfTest()
    {
        var doc = ToDocument("see https://example.com/a and [https://x.org](https://x.org) **https://b.io**", 14);
        static IEnumerable<Inline> All(IEnumerable<Inline> inlines) => inlines.SelectMany(i => i is Span s ? All(s.Inlines).Prepend(i) : [i]);
        var links = doc.Blocks.OfType<Paragraph>().SelectMany(p => All(p.Inlines)).OfType<Hyperlink>().Count();
        if (links != 3 || PlainText(doc).Trim() != "see https://example.com/a and https://x.org https://b.io")
            throw new InvalidOperationException($"Markdown links: {links} links, text '{PlainText(doc).Trim()}'");
        // emoji become colour images in a heading, a list item, a table cell, bold text and a quote; code keeps them as text
        var emoji = ToDocument("## ✅ Done\n\n- 📁 files\n\n| a |\n|---|\n| 🚀 |\n\n**🎮 bold**\n\n> 💡 tip\n\n```\n🚫\n```", 14);
        static IEnumerable<object> Everything(IEnumerable<Block> blocks) => blocks.SelectMany(b => b switch
        {
            Paragraph p => All(p.Inlines).Cast<object>(),
            List l => Everything(l.ListItems.SelectMany(i => i.Blocks)),
            Table t => Everything(t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => c.Blocks)),
            Section s => Everything(s.Blocks),
            _ => [],
        });
        var colored = Everything(emoji.Blocks).OfType<InlineUIContainer>().Select(e => (string)e.Tag).ToList();
        if (!colored.SequenceEqual(["✅", "📁", "🚀", "🎮", "💡"]))
            throw new InvalidOperationException($"Markdown emoji: {string.Join(" ", colored.Select(e => char.ConvertToUtf32(e, 0).ToString("X")))}");
        // a fence opened on a list item's line is a code block, and the text after its closing fence is not code
        var listed = ToDocument("4. ```python\ndef f():\n    return 1\n```\n\n5. after", 14);
        if (listed.Blocks.Count != 3 || listed.Blocks.ElementAt(1) is List or Paragraph || PlainText(listed).Contains("```") ||
            listed.Blocks.LastBlock is not List { ListItems.Count: 1 } last || last.ListItems.FirstListItem.Blocks.FirstBlock is not Paragraph { Inlines.FirstInline: Run { Text: "after" } })
            throw new InvalidOperationException($"Markdown: a fence on a list item's line: {string.Join(", ", listed.Blocks.Select(b => b.GetType().Name))} '{PlainText(listed)}'");
        // replies are Light, their bold and headings Regular
        var weights = ToDocument("## Head\n\nPlain **bold**", 14);
        if (weights.FontWeight != FontWeights.Light || ((Paragraph)weights.Blocks.FirstBlock).FontWeight != FontWeights.Normal ||
            ((Paragraph)weights.Blocks.LastBlock).Inlines.OfType<Bold>().Single().FontWeight != FontWeights.Normal)
            throw new InvalidOperationException("Markdown weights");
        // the viewer's table columns: shares of 1 by content (larger star values left a long table's last column no width),
        // none narrower than its longest word; a reply's table keeps equal columns
        var row = "| " + new string('x', 70) + " ";
        var wide = (Table)ToDocument("| ID | A | B | C |\n|---|---|---|---|\n| R1 " + row + row + row + "|", 15, page: true).Blocks.FirstBlock;
        var shares = wide.Columns.Select(c => c.Width).ToList();
        if (shares.Any(w => !w.IsStar) || Math.Abs(shares.Sum(w => w.Value) - 1) > 1e-9 || shares[0].Value >= shares[1].Value ||
            ((Table)ToDocument("| ID | A |\n|---|---|\n| R1 | " + row + "|", 15).Blocks.FirstBlock).Columns.Any(c => !c.Width.IsAuto))
            throw new InvalidOperationException($"Markdown: the viewer's table columns: {string.Join(", ", shares)}");
        // a user's message: its emoji in colour, its text and lines kept; © and arrows stay text
        var message = EmojiTextBlock("Ship it 🚀\n© ok → next", 16);
        if (message.Inlines.OfType<InlineUIContainer>().Single() is not { Tag: "🚀", Child: Image { Width: 20, Source.Width: 40 } } || message.Inlines.OfType<LineBreak>().Count() != 1 ||
            string.Concat(message.Inlines.OfType<Run>().Select(r => r.Text)) != "Ship it © ok → next" || HasEmoji("© → ™") || !HasEmoji("done ✅"))
            throw new InvalidOperationException("Markdown emoji: a user's message");
        // a closed code block's listing is selectable text: part of it selects (and copies) the code alone, no line numbers;
        // a streaming one stays a plain text block
        var listing = CodeBlocks(ToDocument("```python\na = 1\n\nb = 2  # x\n```", 15).Blocks).Single();
        var codeBox = Find<RichTextBox>(listing);
        if (codeBox is null || Find<TextBlock>(listing, t => t.Tag as string == "codetext") is not null || CodeOf(codeBox) != "a = 1\n\nb = 2  # x")
            throw new InvalidOperationException("Markdown: a code block's listing is not selectable text");
        // the position of the code's character `index` (each paragraph a line), across the highlighter's runs
        TextPointer At(int index)
        {
            var lines = codeBox.Document.Blocks.ToList();
            var (line, column) = (0, index);
            var code = CodeOf(codeBox).Split('\n');
            while (column > code[line].Length)
                (column, line) = (column - code[line].Length - 1, line + 1);
            for (var p = lines[line].ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
                if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    var run = p.GetTextInRun(LogicalDirection.Forward).Length;
                    if (column <= run)
                        return p.GetPositionAtOffset(column)!;
                    column -= run;
                }
            return lines[line].ContentEnd;
        }
        codeBox.Selection.Select(At(2), At(10));
        if (SelectedCode(codeBox) != "= 1\n\nb =")
            throw new InvalidOperationException($"Markdown: a code block's selection copied \"{SelectedCode(codeBox)}\"");
        if (Find<RichTextBox>(CodeBlocks(ToDocument("```python\na = 1\nb", 15).Blocks).Single()) is not null)
            throw new InvalidOperationException("Markdown: a streaming code block became a text box");
    }

    // The first T under `root` (visual or logical children) that matches, for tests.
    private static T? Find<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        if (root is T hit && (match is null || match(hit)))
            return hit;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (Find(child, match) is { } found)
                return found;
        return null;
    }

    // The document's text with its structure flattened, for tests.
    public static string PlainText(FlowDocument doc) => new TextRange(doc.ContentStart, doc.ContentEnd).Text;
}

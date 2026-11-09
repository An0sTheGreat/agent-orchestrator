using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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

    // muted: thinking text, in the theme's muted colour.
    public static FlowDocument ToDocument(string markdown, double fontSize, bool muted = false)
    {
        // Text weights are about 0.7 of the usual (Light body, Regular bold and headings): a lighter, smoother page
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = fontSize, TextAlignment = TextAlignment.Left, FontWeight = TextWeight,
            LineHeight = Roomy ? Math.Round(fontSize * 1.5) : double.NaN }; // Claude's 1.5; a minimum: a taller line (a heading) keeps its own
        doc.SetResourceReference(FlowDocument.ForegroundProperty, muted ? "Muted" : "TextPrimary");
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
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
                lists[^1].List.ListItems.LastOrDefault()?.Blocks.Add(block); // a paragraph continuing the open list item
            else
                doc.Blocks.Add(block);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Fence.Match(line) is { Success: true } fence)
            {
                EndParagraph();
                var code = new List<string>();
                while (++i < lines.Length && !lines[i].TrimStart().StartsWith(fence.Groups[1].Value))
                    code.Add(lines[i]);
                lists.Clear();
                doc.Blocks.Add(CodeBlock(string.Join("\n", code), fence.Groups[2].Value));
                continue;
            }
            if (line.Trim().Length == 0)
            {
                EndParagraph();
                // a blank line ends the lists unless an indented or list line follows
                if (i + 1 < lines.Length && !Item.IsMatch(lines[i + 1]) && !lines[i + 1].StartsWith("  "))
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
                AddInlines(p.Inlines, heading.Groups[2].Value);
                doc.Blocks.Add(p);
                continue;
            }
            if (Rule.IsMatch(line) && paragraph.Count == 0)
            {
                lists.Clear();
                var rule = new Paragraph { Margin = new Thickness(0, R(8, 20), 0, R(8, 22)), BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 2, LineHeight = 2 };
                rule.SetResourceReference(Block.BorderBrushProperty, "Border");
                doc.Blocks.Add(rule);
                continue;
            }
            if (line.TrimStart().StartsWith('>'))
            {
                EndParagraph();
                lists.Clear();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                    quote.Add(lines[i].TrimStart()[1..].TrimStart());
                i--;
                var section = new Section { Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(10, 0, 0, 0), BorderThickness = new Thickness(3, 0, 0, 0) };
                section.SetResourceReference(Block.BorderBrushProperty, "Muted");
                section.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                var inner = ToDocument(string.Join("\n", quote), fontSize);
                foreach (var block in inner.Blocks.ToList())
                {
                    inner.Blocks.Remove(block);
                    section.Blocks.Add(block);
                }
                doc.Blocks.Add(section);
                continue;
            }
            if (line.Contains('|') && i + 1 < lines.Length && TableRule.IsMatch(lines[i + 1]))
            {
                EndParagraph();
                lists.Clear();
                var rows = new List<string> { line };
                for (i += 2; i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0; i++)
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

    // A reply's first block starts right under its header: a heading's or a rule's room above goes.
    public static void NoTopMargin(Block? first)
    {
        if (first is Paragraph p)
            p.Margin = new Thickness(p.Margin.Left, 0, p.Margin.Right, p.Margin.Bottom);
    }

    private static Paragraph Para()
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, R(8, 14)) };
        if (!Roomy)
            p.LineHeight = double.NaN;
        return p;
    }

    // A FlowDocument paragraph cannot round its corners, so a code block is a rounded card: the coloured code (wrapped, clear
    // of the button's column) and a copy button at the top right, whose Tag is the raw code. MainWindow wires the button:
    // copying, and keeping it in view while the block scrolls (its TranslateTransform).
    private static Block CodeBlock(string code, string language)
    {
        var pieces = Highlight(code, language);
        var lineCount = code.Count(c => c == (char)10) + 1;
        UIElement body = lineCount is >= 2 and <= MaxNumbered // a line-number gutter, each number level with its (possibly wrapped) line
            ? NumberedLines(pieces, lineCount) : CodeText(pieces);
        var glyph = new TextBlock { Text = CopyGlyph, FontSize = 13 };
        glyph.SetResourceReference(FrameworkElement.StyleProperty, "RailGlyph");
        var copy = new Button
        {
            Content = glyph, Width = 26, Height = 26, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, ToolTip = "Copy code", Tag = code, RenderTransform = new TranslateTransform(),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        copy.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        var card = new Border { Child = new Grid { Children = { body, copy } }, Padding = Roomy ? new Thickness(16, 12, 8, 12) : new Thickness(10, 6, 6, 6) };
        card.SetResourceReference(Border.BackgroundProperty, "Card"); // the site's code block: the surface shade with a hairline ring
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.BorderThickness = new Thickness(1);
        if (Roomy)
            card.CornerRadius = new CornerRadius(10);
        else
            card.SetResourceReference(Border.CornerRadiusProperty, "SmallRadius");
        return new BlockUIContainer(card) { Margin = new Thickness(0, R(2, 6), 0, R(10, 16)) };
    }

    private const int MaxNumbered = 200; // longer listings stay one block of text: a row each would slow a streaming render

    private static TextBlock CodeText(IEnumerable<(string Text, string? Color)> pieces, bool lastColumn = true)
    {
        var text = new TextBlock { FontFamily = AppFonts.Mono, FontWeight = TextWeight, FontSize = R(12.5, 13.5), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, lastColumn ? 30 : 0, 2) };
        if (Roomy)
            text.LineHeight = 20;
        foreach (var (piece, color) in pieces)
        {
            var run = new Run(piece);
            if (color is not null)
                run.SetResourceReference(TextElement.ForegroundProperty, color);
            text.Inlines.Add(run);
        }
        return text;
    }

    // The highlighted pieces cut at line ends (a block comment or template string can span lines, so the code is highlighted
    // whole first), one row per line: the number in a muted gutter, then the line.
    private static StackPanel NumberedLines(List<(string Text, string? Color)> pieces, int lineCount)
    {
        var lines = new List<List<(string, string?)>> { new() };
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
        var rows = new StackPanel();
        var width = lineCount.ToString().Length * 8 + 14;
        for (var n = 0; n < lines.Count; n++)
        {
            var number = new TextBlock
            {
                Text = (n + 1).ToString(), FontFamily = AppFonts.Mono, FontSize = R(11, 12), Width = width, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 2), Opacity = 0.55,
            };
            if (Roomy)
                number.LineHeight = 20;
            number.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
            var text = CodeText(lines[n].Count > 0 ? lines[n] : [(" ", null)], lastColumn: true);
            Grid.SetColumn(text, 1);
            row.Children.Add(number);
            row.Children.Add(text);
            rows.Children.Add(row);
        }
        return rows;
    }

    public const string CopyGlyph = "", CopiedGlyph = "";

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
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, R(2, 6), 0, R(10, 16)), BorderThickness = new Thickness(1, 1, 0, 0) };
        table.SetResourceReference(Table.BorderBrushProperty, "Border");
        foreach (var _ in header)
            table.Columns.Add(new TableColumn());
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
                var cell = new TableCell(p) { Padding = Roomy ? new Thickness(12, 7, 12, 7) : new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Border");
                if (r == 0)
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
                target.Add(code);
            }
            else if (m.Groups["bold"].Success || m.Groups["bold2"].Success)
            {
                var bold = new Bold { FontWeight = StrongWeight }; // 0.7 of Claude's 600
                AddInlines(bold.Inlines, m.Groups["bold"].Success ? m.Groups["bold"].Value : m.Groups["bold2"].Value, links);
                target.Add(bold);
            }
            else if (m.Groups["em"].Success || m.Groups["em2"].Success)
            {
                var em = new Italic();
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
        // replies are Light, their bold and headings Regular
        var weights = ToDocument("## Head\n\nPlain **bold**", 14);
        if (weights.FontWeight != FontWeights.Light || ((Paragraph)weights.Blocks.FirstBlock).FontWeight != FontWeights.Normal ||
            ((Paragraph)weights.Blocks.LastBlock).Inlines.OfType<Bold>().Single().FontWeight != FontWeights.Normal)
            throw new InvalidOperationException("Markdown weights");
        // a user's message: its emoji in colour, its text and lines kept; © and arrows stay text
        var message = EmojiTextBlock("Ship it 🚀\n© ok → next", 16);
        if (message.Inlines.OfType<InlineUIContainer>().Single() is not { Tag: "🚀", Child: Image { Width: 20, Source.Width: 40 } } || message.Inlines.OfType<LineBreak>().Count() != 1 ||
            string.Concat(message.Inlines.OfType<Run>().Select(r => r.Text)) != "Ship it © ok → next" || HasEmoji("© → ™") || !HasEmoji("done ✅"))
            throw new InvalidOperationException("Markdown emoji: a user's message");
    }

    // The document's text with its structure flattened, for tests.
    public static string PlainText(FlowDocument doc) => new TextRange(doc.ContentStart, doc.ContentEnd).Text;
}

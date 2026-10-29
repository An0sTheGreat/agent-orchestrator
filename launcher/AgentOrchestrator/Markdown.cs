using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgentOrchestrator;

// A small Markdown renderer for chat replies: headings, paragraphs, bullet and numbered lists (nested by indent), block
// quotes, fenced code, tables, rules, and inline bold, italic, strikethrough, code and links. Colours are theme resource
// references, so a theme change restyles rendered replies. Anything it does not recognise shows as plain text.
internal static class Markdown
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    private static readonly Regex Rule = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$");
    private static readonly Regex Item = new(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$");
    private static readonly Regex Fence = new(@"^\s*(```|~~~)");
    private static readonly Regex TableRule = new(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$");
    private static readonly Regex Inline = new(
        @"(?<code>`+)(?<codetext>.+?)\k<code>|\*\*(?<bold>.+?)\*\*|(?<!\w)__(?<bold2>.+?)__(?!\w)|~~(?<strike>.+?)~~|" +
        @"\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)|(?<!\*)\*(?![\s*])(?<em>.+?)(?<!\s)\*|(?<!\w)_(?![\s_])(?<em2>.+?)(?<!\s)_(?!\w)|" +
        @"(?<bare>https?://[^\s<>()]+[^\s<>().,;:!?'""])");

    public static FlowDocument ToDocument(string markdown, double fontSize)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = fontSize, TextAlignment = TextAlignment.Left };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimary");
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
                doc.Blocks.Add(CodeBlock(string.Join("\n", code)));
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
                p.FontSize = fontSize * (level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.0 });
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 4);
                AddInlines(p.Inlines, heading.Groups[2].Value);
                doc.Blocks.Add(p);
                continue;
            }
            if (Rule.IsMatch(line) && paragraph.Count == 0)
            {
                lists.Clear();
                var rule = new Paragraph { Margin = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 2 };
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
                    var list = new List { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(22, 0, 0, 0) };
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
                p.Margin = new Thickness(0, 1, 0, 1);
                AddInlines(p.Inlines, item.Groups[3].Value);
                lists[^1].List.ListItems.Add(new ListItem(p));
                continue;
            }
            paragraph.Add(lists.Count > 0 ? line.Trim() : line.TrimEnd());
        }
        EndParagraph();
        return doc;
    }

    private static Paragraph Para() => new() { Margin = new Thickness(0, 0, 0, 8), LineHeight = double.NaN };

    private static Block CodeBlock(string code)
    {
        var p = new Paragraph(new Run(code)) { FontFamily = new FontFamily("Consolas"), FontSize = 12.5, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 2, 0, 10) };
        p.SetResourceReference(Block.BackgroundProperty, "Hover");
        return p;
    }

    private static Table TableOf(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var header = Cells(rows[0]);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 10), BorderThickness = new Thickness(1, 1, 0, 0) };
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
                    p.FontWeight = FontWeights.SemiBold;
                var cell = new TableCell(p) { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Border");
                if (r == 0)
                    cell.SetResourceReference(TableCell.BackgroundProperty, "Hover");
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        return table;
    }

    public static void AddInlines(InlineCollection target, string text)
    {
        var at = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > at)
                target.Add(new Run(text[at..m.Index]));
            at = m.Index + m.Length;
            if (m.Groups["codetext"].Success)
            {
                var code = new Run(m.Groups["codetext"].Value.Trim()) { FontFamily = new FontFamily("Consolas") };
                code.SetResourceReference(TextElement.BackgroundProperty, "Hover");
                code.SetResourceReference(TextElement.ForegroundProperty, "AccentLight");
                target.Add(code);
            }
            else if (m.Groups["bold"].Success || m.Groups["bold2"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups["bold"].Success ? m.Groups["bold"].Value : m.Groups["bold2"].Value);
                target.Add(bold);
            }
            else if (m.Groups["em"].Success || m.Groups["em2"].Success)
            {
                var em = new Italic();
                AddInlines(em.Inlines, m.Groups["em"].Success ? m.Groups["em"].Value : m.Groups["em2"].Value);
                target.Add(em);
            }
            else if (m.Groups["strike"].Success)
            {
                var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlines(strike.Inlines, m.Groups["strike"].Value);
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
                AddInlines(link.Inlines, label);
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentLight");
                link.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                target.Add(link);
            }
        }
        if (at < text.Length)
            target.Add(new Run(text[at..]));
    }

    // The document's text with its structure flattened, for tests.
    public static string PlainText(FlowDocument doc) => new TextRange(doc.ContentStart, doc.ContentEnd).Text;
}

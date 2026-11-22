using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace AgentOrchestrator;

// The editor: a file in the right column, in place of the worker rail (as the browser is). Its text is an AvalonEdit
// editor drawn as a reply's code block is (the same font, weight, size, colours and line-number gutter), so viewing and
// editing are one surface: typing changes the file, and Save (Ctrl+S) or Cancel appear once it differs from the disk.
// A Markdown file can also be shown formatted, as a reply is.
public partial class MainWindow
{
    private const int EditorMaxBytes = 2_000_000; // bigger files open in the text app instead
    private string? _editorFile;
    private string _editorOnDisk = ""; // the text as last read or saved: Cancel puts it back
    private bool _editorAsText; // a Markdown file shown as its text instead of formatted (kept for the next one)
    private TextEditor? _editorText;
    private CodeColorizer? _editorColors;
    private CodeGutter? _editorGutter;
    private readonly DiffLines _editorBands = new();
    private IReadOnlyList<LauncherLogic.FileEdit>? _editorEdits; // the shown file's edits in a reply's turn (its file chip)
    private bool _editorShowChanges; // those edits shown: the file read-only, added lines green and removed ones red

    private void Editor_Click(object sender, RoutedEventArgs e)
    {
        if (EditorPanel.IsVisible)
            ShowWorkerRail();
        else
            OpenEditor(null);
    }

    private void EditorClose_Click(object sender, RoutedEventArgs e) => ShowWorkerRail();

    // path null: only open (the last file stays shown). From the rail the column slides open; from the browser or terminal the pages swap.
    // edits: a reply's file chip, whose file opens on its Changes.
    private void OpenEditor(string? path, IReadOnlyList<LauncherLogic.FileEdit>? edits = null)
    {
        EditorTitle.FontFamily = AppFonts.Mono;
        ShowColumnPage(EditorPanel);
        if (path is not null)
            ShowEditorFile(path, edits);
    }

    private void EditorMode_Click(object sender, RoutedEventArgs e)
    {
        if (KeepEditorChanges())
        {
            _editorShowChanges = sender == EditorChangesMode;
            if (!_editorShowChanges)
                _editorAsText = sender == EditorTextMode;
        }
        if (_editorFile is { } path)
            ShowEditorFile(path);
    }

    // The editor surface, made once: the code block's look, and the app's own colouring of the language.
    private TextEditor EditorText()
    {
        if (_editorText is { } made)
            return made;
        var editor = _editorText = new TextEditor
        {
            FontFamily = AppFonts.Mono, FontWeight = Markdown.TextWeight, FontSize = Markdown.CodeFontSize, Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 10, 0, 10),
            WordWrap = false, ShowLineNumbers = false,
        };
        editor.SetResourceReference(Control.ForegroundProperty, "CodeText");
        editor.Options.EnableHyperlinks = editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = false;
        editor.Options.ConvertTabsToSpaces = false;
        editor.Options.AllowScrollBelowDocument = false;
        var area = editor.TextArea;
        area.TextView.Margin = new Thickness(0, 0, 14, 0); // a code block's text sits 10 px right of the gutter (its spacer), 14 px from its edge
        area.LeftMargins.Add(_editorGutter = new CodeGutter { Bands = _editorBands });
        area.TextView.BackgroundRenderers.Add(_editorBands);
        area.SelectionCornerRadius = 0;
        area.SelectionBorder = null;
        area.SetResourceReference(TextArea.SelectionBrushProperty, "Hover");
        area.SelectionForeground = null; // selected text keeps its colours
        area.Caret.CaretBrush = (Brush)FindResource("TextPrimary");
        area.TextView.LineTransformers.Add(_editorColors = new CodeColorizer(key => TryFindResource(key) as Brush));
        editor.TextChanged += (_, _) => SyncEditorButtons();
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SaveEditor();
            }
        };
        EditorTextHost.Children.Add(editor);
        return editor;
    }

    private void ShowEditorFile(string path, IReadOnlyList<LauncherLogic.FileEdit>? edits = null)
    {
        if (!KeepEditorChanges())
        {
            SyncEditorModes(LanguageOf(_editorFile ?? "") is "md" or "markdown");
            return;
        }
        if (edits is { Count: > 0 })
            (_editorEdits, _editorShowChanges) = (edits, true);
        else if (!string.Equals(path, _editorFile, StringComparison.OrdinalIgnoreCase))
            (_editorEdits, _editorShowChanges) = (null, false);
        _editorFile = path;
        EditorTitle.Text = Path.GetFileName(path);
        EditorTitle.ToolTip = path;
        EditorView.Content = null;
        EditorView.Visibility = EditorTextHost.Visibility = Visibility.Collapsed;
        EditorModes.Visibility = Visibility.Collapsed;
        EditorMessage.Visibility = Visibility.Visible;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                EditorMessage.Text = $"{path} is not there any more.";
                return;
            }
            if (info.Length > EditorMaxBytes)
            {
                EditorMessage.Text = $"{info.Name} is {VramEstimate.Gb(info.Length)}: too big to show here. Open it in your text app (the ↗ button).";
                return;
            }
            var text = File.ReadAllText(path);
            if (text.AsSpan(0, Math.Min(text.Length, 8000)).Contains('\0'))
            {
                EditorMessage.Text = $"{info.Name} is not a text file.";
                return;
            }
            var markdown = LanguageOf(path) is "md" or "markdown";
            var changes = _editorShowChanges && _editorEdits is { } edited ? DiffView(text, edited) : default;
            if (_editorShowChanges && !(changes.Kinds?.Any(k => k != ' ') ?? false))
                (_editorEdits, _editorShowChanges, changes) = (null, false, default); // none of its edits is in the file any more
            SyncEditorModes(markdown);
            if (markdown && !_editorAsText && !_editorShowChanges)
            {
                // formatted, as a reply is: wrapped to the panel; Text shows (and edits) the source
                EditorView.Content = MarkdownView(text);
                EditorView.Visibility = Visibility.Visible;
            }
            else
            {
                var editor = EditorText();
                _editorColors!.Language = LanguageOf(path);
                _editorOnDisk = text;
                (_editorBands.Kinds, _editorGutter!.Numbers) = _editorShowChanges ? (changes.Kinds, changes.Numbers) : ([], null);
                editor.Document = new TextDocument(_editorShowChanges ? changes.Text : text); // a new document: no undo back into the last file
                editor.IsReadOnly = _editorShowChanges;
                _editorGutter.InvalidateMeasure();
                if (_editorShowChanges)
                    editor.ScrollTo(Math.Max(1, Array.FindIndex(changes.Kinds, k => k != ' ') - 1), 0); // the first change, under a little context
                else
                    editor.ScrollToHome();
                EditorTextHost.Visibility = Visibility.Visible;
            }
            EditorMessage.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            EditorMessage.Text = $"Could not open {path}: {e.Message}";
        }
        finally { SyncEditorButtons(); }
    }

    // A Markdown file formatted, as a reply is. It scrolls itself (no horizontal scrolling), so its text wraps to the panel's
    // width: inside a scroller that offered unlimited width it laid out one letter per line.
    // Styled as Notepad++'s Markdown sheet, in the theme's colours: the text at most PageWidth wide and centred (its margins
    // are the page's padding, so the scroll bar stays at the panel's edge and the wheel scrolls anywhere over it).
    internal static RichTextBox MarkdownView(string text)
    {
        var view = new RichTextBox
        {
            Document = Markdown.ToDocument(text, 15, page: true), IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0), Background = null,
            Padding = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        view.SizeChanged += (_, e) => view.Document.PagePadding = PageMargin(e.NewSize.Width);
        return view;
    }

    private const double PageWidth = 1250;

    // Notepad++'s body: 44 px either side (14 in a narrow panel), more once the panel is wider than the page, 34 above.
    private static Thickness PageMargin(double width)
    {
        var narrow = width < 700;
        var side = Math.Max(narrow ? 14 : 44, (width - PageWidth) / 2);
        return new Thickness(side, narrow ? 10 : 34, side, 40);
    }

    // The Markdown / Text / Changes chips: Markdown for a Markdown file, Changes for a file opened from a reply's chip.
    private void SyncEditorModes(bool markdown)
    {
        EditorMarkdownMode.Visibility = markdown ? Visibility.Visible : Visibility.Collapsed;
        EditorChangesMode.Visibility = _editorEdits is null ? Visibility.Collapsed : Visibility.Visible;
        EditorModes.Visibility = markdown || _editorEdits is not null ? Visibility.Visible : Visibility.Collapsed;
        EditorMarkdownMode.IsChecked = markdown && !_editorAsText && !_editorShowChanges;
        EditorTextMode.IsChecked = !_editorShowChanges && (!markdown || _editorAsText);
        EditorChangesMode.IsChecked = _editorShowChanges;
    }

    // The Changes view: the file's text with each edit's removed lines put back above where they were; each line's kind (' ' kept,
    // '+' added, '-' removed) and its number in the file (0: removed). An edit whose new text is no longer in the file is left out.
    internal static (string Text, char[] Kinds, int[] Numbers) DiffView(string text, IReadOnlyList<LauncherLogic.FileEdit> edits)
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        static string[] Split(string s) => s.Length == 0 ? [] : (s.EndsWith('\n') ? s[..^1] : s).Split('\n');
        text = Lf(text);
        var lines = Split(text);
        var kinds = Enumerable.Repeat(' ', lines.Length).ToArray();
        var removed = new List<string>?[lines.Length + 1];
        foreach (var edit in edits)
        {
            var (old, now) = (Lf(edit.Old), Lf(edit.New));
            // ponytail: a pure deletion (no new text) has nowhere to show; its old text's neighbours would place it
            var at = now.Length == 0 ? -1 : text.IndexOf(now, StringComparison.Ordinal);
            if (at < 0)
                continue;
            // whole lines: the edit's text with the rest of its first and last lines
            var start = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
            var end = text.IndexOf('\n', at + now.Length) is var e and >= 0 ? e : text.Length;
            var (prefix, suffix) = (text[start..at], text[(at + now.Length)..end]);
            var line = text.AsSpan(0, start).Count('\n');
            foreach (var (kind, value) in LineDiff(Split(prefix + old + suffix), Split(prefix + now + suffix)))
                if (kind == '-')
                    (removed[Math.Min(line, lines.Length)] ??= []).Add(value);
                else if (line < lines.Length)
                {
                    if (kind == '+')
                        kinds[line] = '+';
                    line++;
                }
        }
        var shown = new StringBuilder();
        var (outKinds, numbers) = (new List<char>(), new List<int>());
        for (var i = 0; i <= lines.Length; i++)
        {
            foreach (var gone in removed[i] ?? [])
                Line(gone, '-', 0);
            if (i < lines.Length)
                Line(lines[i], kinds[i], i + 1);
        }
        void Line(string value, char kind, int number)
        {
            shown.Append(value).Append('\n');
            outKinds.Add(kind);
            numbers.Add(number);
        }
        return (shown.Length > 0 ? shown.ToString(0, shown.Length - 1) : "", [.. outKinds], [.. numbers]);
    }

    // Lines of a and b in order: ' ' in both, '-' only in a, '+' only in b (a removed line before the line added in its place).
    internal static List<(char Kind, string Line)> LineDiff(string[] a, string[] b)
    {
        var (p, s) = (0, 0);
        while (p < a.Length && p < b.Length && a[p] == b[p])
            p++;
        while (s < a.Length - p && s < b.Length - p && a[^(s + 1)] == b[^(s + 1)])
            s++;
        var (n, m) = (a.Length - p - s, b.Length - p - s);
        var ops = a.Take(p).Select(x => (' ', x)).ToList();
        if ((long)n * m > 1_000_000) // ponytail: too big to match line by line; shown as all removed, then all added
            ops.AddRange(a.Skip(p).Take(n).Select(x => ('-', x)).Concat(b.Skip(p).Take(m).Select(x => ('+', x))));
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[p + i] == b[p + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            var (x, y) = (0, 0);
            while (x < n || y < m)
                if (x < n && y < m && a[p + x] == b[p + y])
                {
                    ops.Add((' ', b[p + y]));
                    (x, y) = (x + 1, y + 1);
                }
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
                    ops.Add(('-', a[p + x++]));
                else
                    ops.Add(('+', b[p + y++]));
        }
        ops.AddRange(b.Skip(b.Length - s).Select(x => (' ', x)));
        return ops;
    }

    private bool EditorChanged => _editorText is { } editor && EditorTextHost.IsVisible && !editor.IsReadOnly && editor.Text != _editorOnDisk;

    private void SyncEditorButtons() =>
        EditorSaveButton.Visibility = EditorCancelButton.Visibility = EditorChanged ? Visibility.Visible : Visibility.Collapsed;

    private void EditorSave_Click(object sender, RoutedEventArgs e) => SaveEditor();

    // Back to the file as it is on disk.
    private void EditorCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_editorText is { } editor)
        {
            editor.Document.Text = _editorOnDisk; // one step: Ctrl+Z brings the changes back
            SyncEditorButtons();
            editor.Focus();
        }
    }

    private bool SaveEditor()
    {
        if (_editorFile is not { } path || _editorText is not { } editor || !EditorChanged)
            return false;
        try
        {
            File.WriteAllText(path, editor.Text);
            _editorOnDisk = editor.Text;
            SyncEditorButtons();
            Toast($"Saved {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Not saved: {e.Message}", "#FF667A");
            return false;
        }
    }

    // Before the shown text goes (another file, the other view): unsaved changes are saved on Save, kept on Cancel (false).
    private bool KeepEditorChanges() =>
        !EditorChanged || (AskDialog("Unsaved changes", $"Save the changes to {EditorTitle.Text}?", "Save") is not null && SaveEditor());

    // After a theme change: the colours and the gutter are drawn again.
    private void RefreshEditorTheme()
    {
        if (_editorText is not { } editor)
            return;
        editor.TextArea.Caret.CaretBrush = (Brush)FindResource("TextPrimary");
        editor.TextArea.TextView.Redraw();
        foreach (var margin in editor.TextArea.LeftMargins.OfType<CodeGutter>())
            margin.InvalidateVisual();
    }

    private void EditorExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_editorFile is { } path)
            OpenInTextApp(path);
    }

    private void EditorFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_editorFile is { } path)
            ShowInFolder(path);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);

    // The app that opens .txt files (any text file opens in it: a .py would otherwise run), else Notepad.
    private void OpenInTextApp(string path)
    {
        var app = "notepad.exe";
        uint length = 1024;
        var found = new StringBuilder((int)length);
        if (AssocQueryString(0, 2 /* ASSOCSTR_EXECUTABLE */, ".txt", "open", found, ref length) == 0 && File.Exists(found.ToString()))
            app = found.ToString();
        try { Process.Start(new ProcessStartInfo(app, $"\"{path}\"") { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Toast($"Could not open {Path.GetFileName(path)}: {e.Message}", "#FF667A");
        }
    }

    // Explorer at the file's folder with the file selected.
    private static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = false });

    // The app's code colouring (Markdown.Highlight, as replies' code blocks) on the editor's text: the whole text is
    // coloured once per change (a block comment can span lines), then each drawn line takes its pieces.
    internal sealed class CodeColorizer(Func<string, Brush?> brush) : DocumentColorizingTransformer
    {
        private string _language = "";
        private ITextSourceVersion? _coloured; // the document version the spans are for
        private List<(int Start, int End, string Key)> _spans = new();

        public string Language
        {
            get => _language;
            set => (_language, _coloured) = (value, null);
        }

        // ponytail: the whole text again on each change; fine to the 2 MB limit, per-line state if typing in big files lags
        internal static List<(int Start, int End, string Key)> Spans(string text, string language)
        {
            var spans = new List<(int, int, string)>();
            var at = 0;
            foreach (var (piece, key) in Markdown.Highlight(text, language))
            {
                if (key is not null && piece.Length > 0)
                    spans.Add((at, at + piece.Length, key));
                at += piece.Length;
            }
            return spans;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var document = CurrentContext.Document;
            var now = document.Version;
            if (_coloured is not { } was || !was.BelongsToSameDocumentAs(now) || was.CompareAge(now) != 0)
                (_spans, _coloured) = (Spans(document.Text, _language), now);
            // the first span that ends inside or after this line, then on while they start before its end
            int lo = 0, hi = _spans.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_spans[mid].End <= line.Offset)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            for (var i = lo; i < _spans.Count && _spans[i].Start < line.EndOffset; i++)
            {
                var (start, end, key) = _spans[i];
                if (brush(key) is not { } b)
                    continue;
                ChangeLinePart(Math.Max(start, line.Offset), Math.Min(end, line.EndOffset), e => e.TextRunProperties.SetForegroundBrush(b));
            }
        }
    }

    // A code block's line-number gutter: the numbers centred in the muted CodeLineNumber colour at 11 px, the width the block
    // uses for that many lines, and a hairline down its right edge.
    // The Changes view's bands: each added line green and each removed one red, across the whole width (the gutter's spacer too).
    private sealed class DiffLines : IBackgroundRenderer
    {
        internal static readonly Brush Added = Band(0x3C, 0xBB, 0x72), Removed = Band(0xEE, 0x5C, 0x61);
        public char[] Kinds = [];

        private static Brush Band(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x38, r, g, b));
            brush.Freeze();
            return brush;
        }

        internal Brush? For(int lineNumber) =>
            lineNumber - 1 < Kinds.Length ? Kinds[lineNumber - 1] switch { '+' => Added, '-' => Removed, _ => null } : null;

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView view, DrawingContext dc)
        {
            if (Kinds.Length == 0 || !view.VisualLinesValid)
                return;
            foreach (var line in view.VisualLines)
                if (For(line.FirstDocumentLine.LineNumber) is { } band)
                    dc.DrawRectangle(band, null, new Rect(0, line.VisualTop - view.VerticalOffset, view.ActualWidth, line.Height));
        }
    }

    private sealed class CodeGutter : AbstractMargin
    {
        private const double Spacer = 10; // between the rule and the text
        public DiffLines? Bands;
        public int[]? Numbers; // the Changes view: each line's number in the file (0: a removed line, unnumbered)
        private int _lines;

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
                oldTextView.VisualLinesChanged -= Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
                newTextView.VisualLinesChanged += Redraw;
        }

        private void Redraw(object? sender, EventArgs e)
        {
            if (Document?.LineCount is { } count && count.ToString().Length != _lines.ToString().Length)
                InvalidateMeasure();
            _lines = Document?.LineCount ?? 0;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) => new(Markdown.GutterWidth(Document?.LineCount ?? 1) + Spacer, 0);

        protected override void OnRender(DrawingContext dc)
        {
            if (TextView is not { VisualLinesValid: true } view)
                return;
            var number = TryFindResource("CodeLineNumber") as Brush ?? Brushes.Gray;
            var rule = TryFindResource("Border") as Brush ?? Brushes.Gray;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(AppFonts.Mono, FontStyles.Normal, Markdown.TextWeight, FontStretches.Normal);
            var width = ActualWidth - Spacer;
            foreach (var line in view.VisualLines)
            {
                var at = line.FirstDocumentLine.LineNumber;
                if (Bands?.For(at) is { } band)
                    dc.DrawRectangle(band, null, new Rect(width, line.VisualTop - view.VerticalOffset, Spacer, line.Height));
                var shown = Numbers is { } numbers && at - 1 < numbers.Length ? numbers[at - 1] : at;
                if (shown == 0)
                    continue;
                var text = new FormattedText(shown.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 11, number, dpi);
                var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
                var height = line.TextLines[0].Height;
                dc.DrawText(text, new Point((width - text.Width) / 2, top + (height - text.Height) / 2));
            }
            dc.DrawRectangle(rule, null, new Rect(width - 1, 0, 1, ActualHeight));
        }
    }
}

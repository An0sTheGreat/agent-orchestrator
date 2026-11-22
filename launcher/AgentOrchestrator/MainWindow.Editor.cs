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

    private void Editor_Click(object sender, RoutedEventArgs e)
    {
        if (EditorPanel.IsVisible)
            ShowWorkerRail();
        else
            OpenEditor(null);
    }

    private void EditorClose_Click(object sender, RoutedEventArgs e) => ShowWorkerRail();

    // path null: only open (the last file stays shown). From the rail the column slides open; from the browser or terminal the pages swap.
    private void OpenEditor(string? path)
    {
        EditorTitle.FontFamily = AppFonts.Mono;
        ShowColumnPage(EditorPanel);
        if (path is not null)
            ShowEditorFile(path);
    }

    private void EditorMode_Click(object sender, RoutedEventArgs e)
    {
        if (!KeepEditorChanges())
        {
            (EditorMarkdownMode.IsChecked, EditorTextMode.IsChecked) = (!_editorAsText, _editorAsText);
            return;
        }
        _editorAsText = sender == EditorTextMode;
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
        area.TextView.Margin = new Thickness(10, 0, 14, 0); // a code block's text sits 10 px right of the gutter, 14 px from its edge
        area.LeftMargins.Add(new CodeGutter());
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

    private void ShowEditorFile(string path)
    {
        if (!KeepEditorChanges())
            return;
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
            EditorModes.Visibility = markdown ? Visibility.Visible : Visibility.Collapsed;
            (EditorMarkdownMode.IsChecked, EditorTextMode.IsChecked) = (markdown && !_editorAsText, markdown && _editorAsText);
            if (markdown && !_editorAsText)
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
                editor.Document = new TextDocument(text); // a new document: no undo back into the last file
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

    private bool EditorChanged => _editorText is { } editor && EditorTextHost.IsVisible && editor.Text != _editorOnDisk;

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
    private sealed class CodeGutter : AbstractMargin
    {
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

        protected override Size MeasureOverride(Size availableSize) => new(Markdown.GutterWidth(Document?.LineCount ?? 1), 0);

        protected override void OnRender(DrawingContext dc)
        {
            if (TextView is not { VisualLinesValid: true } view)
                return;
            var number = TryFindResource("CodeLineNumber") as Brush ?? Brushes.Gray;
            var rule = TryFindResource("Border") as Brush ?? Brushes.Gray;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(AppFonts.Mono, FontStyles.Normal, Markdown.TextWeight, FontStretches.Normal);
            foreach (var line in view.VisualLines)
            {
                var text = new FormattedText(line.FirstDocumentLine.LineNumber.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 11, number, dpi);
                var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
                var height = line.TextLines[0].Height;
                dc.DrawText(text, new Point((ActualWidth - text.Width) / 2, top + (height - text.Height) / 2));
            }
            dc.DrawRectangle(rule, null, new Rect(ActualWidth - 1, 0, 1, ActualHeight));
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AgentOrchestrator;

// The file viewer: a file in the right column, in place of the worker rail (as the browser is), coloured as a code block.
// A reply's file chips open their file here; Edit switches to a plain text box and Save (or Ctrl+S) writes it back.
public partial class MainWindow
{
    private const int EditorMaxBytes = 2_000_000; // bigger files open in the text app instead
    private string? _editorFile;

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
        EditorTitle.FontFamily = EditorBox.FontFamily = AppFonts.Mono;
        ShowColumnPage(EditorPanel);
        if (path is not null)
            ShowEditorFile(path);
    }

    private void ShowEditorFile(string path)
    {
        if (EditorBox.IsVisible && !LeaveEdit())
            return;
        _editorFile = path;
        EditorTitle.Text = Path.GetFileName(path);
        EditorTitle.ToolTip = path;
        EditorEditButton.IsEnabled = false;
        EditorView.Content = null;
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
            EditorView.Content = Markdown.CodeListing(text.Replace("\r\n", "\n").TrimEnd('\n'), LanguageOf(path));
            EditorScroll.ScrollToHome();
            EditorMessage.Visibility = Visibility.Collapsed;
            EditorEditButton.IsEnabled = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            EditorMessage.Text = $"Could not open {path}: {e.Message}";
        }
    }

    // Edit shows the text in a box; Save writes it and shows the coloured view again.
    private void EditorEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_editorFile is not { } path)
            return;
        if (EditorBox.IsVisible)
        {
            if (SaveEditor())
                ShowEditorFile(path);
            return;
        }
        try { EditorBox.Text = File.ReadAllText(path); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            Toast($"Could not read {Path.GetFileName(path)}: {x.Message}", "#FF667A");
            return;
        }
        EditorBox.Tag = EditorBox.Text; // what is on disk: unsaved changes are anything else
        EditorBox.Visibility = Visibility.Visible;
        EditorScroll.Visibility = Visibility.Collapsed;
        EditorEditButton.Content = "Save";
        EditorBox.Focus();
    }

    private void EditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SaveEditor();
        }
    }

    private bool SaveEditor()
    {
        if (_editorFile is not { } path)
            return false;
        try
        {
            File.WriteAllText(path, EditorBox.Text);
            EditorBox.Tag = EditorBox.Text;
            Toast($"Saved {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Not saved: {e.Message}", "#FF667A");
            return false;
        }
    }

    // Out of the text box to the coloured view; false when the user keeps unsaved changes.
    private bool LeaveEdit()
    {
        if (EditorBox.Text != EditorBox.Tag as string)
        {
            var answer = AskDialog("Unsaved changes", $"Save the changes to {EditorTitle.Text}?", "Save");
            if (answer is null || !SaveEditor())
                return false;
        }
        EditorBox.Visibility = Visibility.Collapsed;
        EditorScroll.Visibility = Visibility.Visible;
        EditorEditButton.Content = "Edit";
        return true;
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
}

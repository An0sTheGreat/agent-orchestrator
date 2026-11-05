using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrchestrator;

// A project run's output survives the app closing: what the run view's plain-text transcript (_transcript) gains is
// written to <data>\conversations\<id>.run.txt of the run's Code conversation every 1.5 s, at the run's end and at close
// (so a kill loses at most that window). Reopening the conversation shows the file's end in a collapsed, read-only
// PREVIOUS RUN OUTPUT section, which a new run's own output replaces. Chat messages are not in it: they are in the JSON.
public partial class MainWindow
{
    internal const long RunLogCap = 2 * 1024 * 1024, RunLogKeep = 1536 * 1024; // past the cap, the newest ~1.5 MB stay
    internal const int RunLogShown = 200 * 1024;

    private string? _runLogId; // the conversation the current run writes to; null: none
    private int _runLogWritten; // how much of _transcript is in the file
    private bool _runLogFailed; // an IO error is logged once
    private DispatcherTimer? _runLogTimer;
    private Expander? _previousRun;

    // ResetRunView: the run about to start writes to the open conversation's file, after a separator line.
    private void BeginRunLog()
    {
        EndRunLog();
        if (_previousRun?.Parent is Panel panel)
            panel.Children.Remove(_previousRun);
        _previousRun = null;
        if ((_runLogId = _codeConv?.Id) is null)
            return;
        _runLogWritten = 0; // ResetRunView clears _transcript next
        WriteRunLog($"\n=== run started {DateTime.Now:yyyy-MM-dd HH:mm} ===\n");
        _runLogTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => FlushRunLog(), Dispatcher);
    }

    // The run ended (exit, Stop, error): the rest goes to the file and nothing more is written.
    private void EndRunLog()
    {
        FlushRunLog();
        _runLogTimer?.Stop();
        _runLogTimer = null;
        _runLogId = null;
    }

    internal void FlushRunLog()
    {
        if (_runLogId is null || _transcript.Length == _runLogWritten)
            return;
        if (_transcript.Length < _runLogWritten) // cleared by someone else: only what follows is new
        {
            _runLogWritten = _transcript.Length;
            return;
        }
        var text = _transcript.ToString(_runLogWritten, _transcript.Length - _runLogWritten);
        _runLogWritten = _transcript.Length;
        WriteRunLog(text);
    }

    private void WriteRunLog(string text)
    {
        if (_runLogId is null || _store.Get(_runLogId) is null) // deleted meanwhile: never brought back
            return;
        if (!AppendRunLog(_store.RunLogPath(_runLogId), text) && !_runLogFailed)
        {
            _runLogFailed = true;
            Log($"Could not save the run's output to {_store.RunLogPath(_runLogId)}");
        }
    }

    // A Code conversation was shown (ResumeCodeConversation): its saved run output, after its messages, unless a run's own
    // output is already on screen.
    private void ShowPreviousRunOutput(string id)
    {
        var path = _store.RunLogPath(id);
        if (OutputPanel.Parent is not null || ReadRunLogTail(path) is not { } text || text.Trim().Length == 0)
            return;
        var box = new TextBox
        {
            Text = text.Trim('\n'), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), FontFamily = AppFonts.Mono, FontSize = 12, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Cursor = System.Windows.Input.Cursors.IBeam,
        };
        box.SetResourceReference(TextBox.ForegroundProperty, "Muted");
        DateTime saved;
        try { saved = File.GetLastWriteTime(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { saved = DateTime.Now; }
        _previousRun = BuildExpander("PREVIOUS RUN OUTPUT", $"saved {saved:yyyy-MM-dd HH:mm}", box, out _);
        _previousRun.IsExpanded = false;
        _previousRun.Tag = "previous-run";
        CodePanel.Children.Add(_previousRun);
        CodeEmpty.Visibility = Visibility.Collapsed;
    }

    // ---- the file (pure: --self-test)

    // Appends `text`; when the file would pass `cap`, keeps only the newest `keep` bytes cut at a line, under a
    // "(earlier output trimmed)" line. False on an IO error (a missing folder, a locked file); never throws for those.
    internal static bool AppendRunLog(string path, string text, long cap = RunLogCap, long keep = RunLogKeep)
    {
        try
        {
            var add = Encoding.UTF8.GetBytes(text);
            using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (file.Length + add.Length <= cap)
            {
                file.Seek(0, SeekOrigin.End);
                file.Write(add);
                return true;
            }
            var all = new byte[file.Length + add.Length];
            file.ReadExactly(all, 0, (int)file.Length);
            add.CopyTo(all, (int)file.Length);
            var tail = FromLine(all.AsSpan(all.Length - (int)Math.Min(keep, all.Length)));
            file.SetLength(0);
            file.Write(Encoding.UTF8.GetBytes("(earlier output trimmed)\n"));
            file.Write(tail);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // The file's last `max` bytes (from a line start), prefixed with where the rest is when cut; null when unreadable.
    internal static string? ReadRunLogTail(string path, int max = RunLogShown)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, file.Length - max);
            var bytes = new byte[file.Length - start];
            file.Seek(start, SeekOrigin.Begin);
            file.ReadExactly(bytes);
            return start == 0 ? Encoding.UTF8.GetString(bytes) : $"(earlier output not shown, see {path})\n" + Encoding.UTF8.GetString(FromLine(bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // After the first line break; with none, past any partial UTF-8 character at the start.
    private static ReadOnlySpan<byte> FromLine(ReadOnlySpan<byte> bytes)
    {
        var cut = bytes.IndexOf((byte)'\n');
        if (cut >= 0)
            return bytes[(cut + 1)..];
        while (bytes.Length > 0 && (bytes[0] & 0xC0) == 0x80)
            bytes = bytes[1..];
        return bytes;
    }

    internal static void RunLogSelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("RunLog self-test failed: " + what);
        }
        var root = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-runlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "a.run.txt");
            Check(ReadRunLogTail(path) is null, "missing file");
            Check(AppendRunLog(path, "\n=== run started 2026-01-01 10:00 ===\n") && AppendRunLog(path, "[thinking]\nhmm é\n") &&
                  ReadRunLogTail(path) == "\n=== run started 2026-01-01 10:00 ===\n[thinking]\nhmm é\n", "append");
            Check(File.ReadAllBytes(path)[0] == (byte)'\n', "no BOM");
            // a second run follows its separator
            Check(AppendRunLog(path, "\n=== run started 2026-01-02 11:00 ===\n") && File.ReadAllText(path).Split("=== run started").Length == 3, "second run");
            // past the cap: the newest bytes stay, from a line start, under the trimmed line
            File.WriteAllText(path, string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i:000}\n"))); // 9 bytes a line
            Check(AppendRunLog(path, "last\n", cap: 904, keep: 50), "trim");
            var trimmed = File.ReadAllText(path);
            Check(trimmed.StartsWith("(earlier output trimmed)\nline 0") && trimmed.EndsWith("line 099\nlast\n") && !trimmed.Contains("line 094") &&
                  trimmed.Length < 80, "trimmed: " + trimmed);
            Check(AppendRunLog(path, "x\n", cap: 904, keep: 50) &&File.ReadAllText(path) == trimmed + "x\n", "under the cap: appended");
            // the shown tail: cut at a line, says where the rest is
            var shown = ReadRunLogTail(path, max: 20)!;
            Check(shown.StartsWith($"(earlier output not shown, see {path})\n") && shown.EndsWith("last\nx\n") && !shown.Contains("trimmed"), "tail: " + shown);
            // IO errors: a missing folder, a file locked by another writer
            Check(!AppendRunLog(Path.Combine(root, "gone", "b.run.txt"), "x"), "missing folder");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(!AppendRunLog(path, "x") && ReadRunLogTail(path) is null, "locked");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // --ui-smoke: a run in conversation A is saved as it streams; after a restart A shows it collapsed; a new run replaces
    // the section and appends to the file; a conversation without a file shows none; deleting A deletes its file.
    private async Task<bool> RunLogSmoke(Action<string, FrameworkElement?> snap)
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.RunLog.cs:{line}"); return false; }
        var folder = Path.Combine(AppSettings.DataRoot, "runlog-smoke");
        Directory.CreateDirectory(folder);
        ConversationStore.Conversation Seed(string title)
        {
            var c = _store.Create(folder)!;
            c.Title = title;
            _store.Save(c);
            return c;
        }
        var (a, b) = (Seed("Smoke run log"), Seed("Smoke no run log"));
        var path = _store.RunLogPath(a.Id);
        Expander? Previous() => CodePanel.Children.OfType<Expander>().FirstOrDefault(x => x.Tag as string == "previous-run");
        OpenCodeConversation(a.Id);
        if (_codeConv?.Id != a.Id || Previous() is not null || File.Exists(path))
            return Fail();
        ResetRunView(); // as Start_Click does
        AddNote("PROJECT RUN", "Working through smoke PROJECT.md");
        foreach (var json in new[]
                 {
                     """{"e":"thinking","role":"planner","text":"Smoke pondering"}""",
                     """{"e":"tool","role":"planner","name":"read_file","args":"{\"path\":\"STATE.md\"}","result":"smoke tool result"}""",
                     """{"e":"message","role":"planner","text":"Smoke answer","final":true}""",
                 })
            _lines.Enqueue(LauncherLogic.EventPrefix + json);
        DrainLines();
        FlushRunLog();
        var saved = File.Exists(path) ? File.ReadAllText(path) : "";
        if (!saved.StartsWith("\n=== run started ") || !saved.Contains("[PROJECT RUN] Working through smoke PROJECT.md") || !saved.Contains("[thinking]\nSmoke pondering") ||
            !saved.Contains("[tool read_file]") || !saved.Contains("=> smoke tool result") || !saved.Contains("[final]\nSmoke answer"))
        {
            Console.Error.WriteLine($"ui-smoke: run log: {saved}");
            return Fail();
        }
        EndRunLog(); // as Start_Click's end does
        // a restart: another conversation, then a fresh run view and transcript, then A again
        OpenCodeConversation(b.Id);
        if (Previous() is not null)
            return Fail();
        (OutputPanel, _) = (new StackPanel(), _transcript.Clear());
        OpenCodeConversation(a.Id);
        await Task.Delay(400);
        if (Previous() is not { IsExpanded: false, Content: TextBox box } section || !box.Text.Contains("[final]\nSmoke answer") || !box.IsReadOnly || CodePanel.Children[^1] != section)
            return Fail();
        snap("50-previous-run-collapsed", null);
        section.IsExpanded = true;
        await Task.Delay(400);
        snap("51-previous-run-expanded", null);
        // a new run: the section goes, its output follows the first run's in the file
        ResetRunView();
        AddNote("PROJECT RUN", "Second smoke run");
        EndRunLog();
        saved = File.ReadAllText(path);
        if (Previous() is not null || saved.Split("=== run started").Length != 3 || saved.IndexOf("Smoke answer") > saved.IndexOf("Second smoke run"))
            return Fail();
        // deleting A deletes its file
        OpenCodeConversation(b.Id);
        _store.Delete(a.Id);
        if (File.Exists(path))
            return Fail();
        CloseCodeConversation();
        _store.Delete(b.Id);
        RefreshSidebarList();
        return true;
    }
}

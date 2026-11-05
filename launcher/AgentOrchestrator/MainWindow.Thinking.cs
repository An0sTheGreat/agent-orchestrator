using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace AgentOrchestrator;

// "View thinking" (the toggle beside CHAT and CODE). Off: each run of a reply's work (thinking and stage sections, tool
// steps, their stats lines, screenshots) folds into one THINKING row with a summary ("2 thoughts · read 3 files"), as
// Claude shows it; a click on the row shows the run again. On: every step shows, as before. Only the view changes: the
// steps stay where they are, hidden, so streaming and saving are untouched.
public partial class MainWindow
{
    private const string WorkGroup = "work-group";
    private readonly Dictionary<UIElement, Expander> _workRows = new(); // a run's first step → its row (keeps it open or shut)
    private readonly HashSet<UIElement> _hiddenWork = new();
    private bool _regroupQueued;

    private void ViewThinking_Click(object sender, RoutedEventArgs e)
    {
        _settings.ViewThinking = ((ToggleButton)sender).IsChecked == true;
        _settings.Save();
        ChatThinkingToggle.IsChecked = CodeThinkingToggle.IsChecked = _settings.ViewThinking;
        RegroupAll();
    }

    // After the reply's content changed: once, when the dispatcher is idle (a burst of deltas regroups once).
    private void QueueRegroup()
    {
        if (_regroupQueued)
            return;
        _regroupQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _regroupQueued = false;
            RegroupAll();
        });
    }

    private void RegroupAll()
    {
        foreach (var panel in new[] { _chat.Panel, _parked?.Panel, CodePanel }.OfType<Panel>())
            foreach (var root in panel.Children.OfType<FrameworkElement>())
                if (root.Tag is ChatReply reply)
                    Regroup(reply.Content);
    }

    // The muted line that says the context was compacted. Not a working step (no "stats" tag): it shows with View thinking off too.
    private static TextBlock CompactionNote(string text)
    {
        var note = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(2, 2, 0, 4), TextWrapping = TextWrapping.Wrap, Tag = "compaction-note" };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return note;
    }

    // A reply's working steps; its answers, notes and rules are not.
    private static bool IsWork(UIElement e) => e is Expander { Tag: not WorkGroup } or TextBlock { Tag: "stats" } or WrapPanel;

    private void Regroup(StackPanel content)
    {
        var rows = content.Children.OfType<Expander>().Where(x => x.Tag as string == WorkGroup).ToList();
        var steps = content.Children.OfType<UIElement>().Where(e => !rows.Contains(e)).ToList();
        foreach (var step in steps.Where(_hiddenWork.Contains))
        {
            step.Visibility = Visibility.Visible;
            _hiddenWork.Remove(step);
        }
        var runs = new List<List<UIElement>>();
        var shown = steps.Where(s => s.Visibility == Visibility.Visible || IsWork(s)).ToList(); // a hidden raw answer box splits nothing
        if (!_settings.ViewThinking)
            for (var i = 0; i < shown.Count; i++)
                if (IsWork(shown[i]))
                {
                    if (i == 0 || !IsWork(shown[i - 1]))
                        runs.Add([]);
                    runs[^1].Add(shown[i]);
                }
        var kept = new HashSet<Expander>();
        foreach (var run in runs)
        {
            if (!_workRows.TryGetValue(run[0], out var row))
            {
                row = BuildExpander("THINKING", "", new StackPanel(), out _);
                row.Tag = WorkGroup;
                row.Margin = new Thickness(0, 2, 0, 2);
                row.Expanded += (_, _) => Regroup(content);
                row.Collapsed += (_, _) => Regroup(content);
                _workRows[run[0]] = row;
            }
            kept.Add(row);
            ((TextBlock)((StackPanel)row.Header).Children[1]).Text = WorkSummary(run);
            var at = content.Children.IndexOf(run[0]);
            if (content.Children.IndexOf(row) != at - 1)
            {
                content.Children.Remove(row);
                content.Children.Insert(content.Children.IndexOf(run[0]), row);
            }
            if (!row.IsExpanded)
                foreach (var step in run.Where(s => s.Visibility == Visibility.Visible))
                {
                    step.Visibility = Visibility.Collapsed;
                    _hiddenWork.Add(step);
                }
        }
        foreach (var row in rows.Where(r => !kept.Contains(r)))
            content.Children.Remove(row);
    }

    // "2 thoughts · read 3 files · searched 2 times · ran 1 command": the run's steps by kind.
    internal static string WorkSummary(IEnumerable<UIElement> run)
    {
        var counts = new Dictionary<string, int>();
        foreach (var step in run.OfType<Expander>())
        {
            var kind = (step.Header as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
            var key = kind.StartsWith("THINKING", StringComparison.OrdinalIgnoreCase) ? "thought" : kind.ToLowerInvariant() switch
            {
                "read" or "read_file" => "read",
                "search_files" or "grep" or "find" or "list_files" or "ls" => "searched",
                "edit" or "replace_text" => "edited",
                "write" or "write_file" => "wrote",
                "run_command" or "run_checks" or "bash" => "ran",
                "browser" => "browser",
                "web_search" or "fetch_page" => "web",
                "" => "step",
                _ when kind == kind.ToUpperInvariant() => "thought", // a stage section (DELEGATE, a worker's name)
                _ => "step",
            };
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        static string N(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        var parts = new List<string>();
        foreach (var (key, n) in counts)
            parts.Add(key switch
            {
                "thought" => N(n, "thought", "thoughts"),
                "read" => "read " + N(n, "file", "files"),
                "searched" => "searched " + N(n, "time", "times"),
                "edited" => "edited " + N(n, "file", "files"),
                "wrote" => "wrote " + N(n, "file", "files"),
                "ran" => "ran " + N(n, "command", "commands"),
                "browser" => "used the browser " + N(n, "time", "times"),
                "web" => "searched the web " + N(n, "time", "times"),
                _ => N(n, "other step", "other steps"),
            });
        return parts.Count > 0 ? string.Join(" · ", parts) : "working";
    }

    // --ui-smoke: off, a reply's steps fold into one THINKING row between its answers, which opens on a click; on, they
    // show again and the row goes.
    private bool ThinkingSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: view thinking: {what}"); return false; }
        var (view, turn, reply) = (_settings.ViewThinking, _turn, _reply);
        try
        {
            ClearConversation(_chat);
            _turn = _chat;
            var r = _reply = AddReply("planner");
            EnsureAnswer(r).Text = "First answer.";
            DetachAnswer(r);
            r.Content.Children.Add(BuildExpander("THINKING", "hmm", new StackPanel(), out _));
            r.Content.Children.Add(BuildExpander("read", "index.html", new StackPanel(), out _));
            r.Content.Children.Add(BuildExpander("read", "app.js", new StackPanel(), out _));
            r.Content.Children.Add(new TextBlock { Text = "Lead · 40 tok/s", Tag = "stats" });
            var compacted = CompactionNote("Context compacted: 40,000 → 9,000 tokens");
            r.Content.Children.Add(compacted);
            r.Content.Children.Add(CompactionRule());
            EnsureAnswer(r).Text = "Second answer.";
            _settings.ViewThinking = false;
            RegroupAll();
            var rows = r.Content.Children.OfType<Expander>().Where(x => x.Tag as string == WorkGroup).ToList();
            var hidden = r.Content.Children.OfType<UIElement>().Count(e => IsWork(e) && e.Visibility == Visibility.Collapsed);
            if (rows.Count != 1 || hidden != 4 || WorkSummary(r.Content.Children.OfType<UIElement>().Where(IsWork)) != "1 thought · read 2 files" ||
                ((TextBlock)((StackPanel)rows[0].Header).Children[1]).Text != "1 thought · read 2 files" || r.Content.Children.IndexOf(rows[0]) != 2)
                return Fail($"{rows.Count} rows, {hidden} hidden");
            if (compacted.Visibility != Visibility.Visible || IsWork(compacted))
                return Fail("the compaction note folded into the row");
            rows[0].IsExpanded = true;
            if (r.Content.Children.OfType<UIElement>().Any(e => IsWork(e) && e.Visibility != Visibility.Visible))
                return Fail("the opened row");
            _settings.ViewThinking = true;
            RegroupAll();
            if (r.Content.Children.OfType<Expander>().Any(x => x.Tag as string == WorkGroup) ||
                r.Content.Children.OfType<UIElement>().Any(e => IsWork(e) && e.Visibility != Visibility.Visible))
                return Fail("View thinking on");
            return true;
        }
        finally
        {
            _settings.ViewThinking = view;
            ClearConversation(_chat);
            (_turn, _reply) = (turn, reply);
        }
    }
}

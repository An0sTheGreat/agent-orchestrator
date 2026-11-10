using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
                else if (root is StackPanel { Tag: RunOutput } run)
                    Regroup(run, inRun: true);
    }

    private const string RunOutput = "run-output"; // a project run's OutputPanel (each run's stays in the Code conversation)

    // A project run's working steps: its THINKING and TOOL sections and live stats lines; not its responses, nor the
    // block still streaming (it folds once it ends, so no text goes out of sight mid-stream).
    private bool IsRunWork(UIElement e)
    {
        if (e == _stream?.Expander)
            return false;
        return e is TextBlock { Tag: "stats" } ||
               e is Expander { Tag: not WorkGroup, Header: StackPanel header } && header.Children[0] is TextBlock { Text: not ("RESPONSE" or "FINAL RESPONSE") };
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

    private void Regroup(StackPanel content, bool inRun = false)
    {
        // the streaming call's live "tok/s · tokens" line stays in view with View thinking off: it is the one thing to watch while a long write runs
        Func<UIElement, bool> isWork = inRun ? IsRunWork : e => IsWork(e) && !(e is TextBlock { Tag: "stats" } live && ReferenceEquals(live, _reply?.Block?.Live));
        var rows = content.Children.OfType<Expander>().Where(x => x.Tag as string == WorkGroup).ToList();
        var steps = content.Children.OfType<UIElement>().Where(e => !rows.Contains(e)).ToList();
        foreach (var step in steps.Where(_hiddenWork.Contains))
        {
            step.Visibility = Visibility.Visible;
            _hiddenWork.Remove(step);
        }
        var runs = new List<List<UIElement>>();
        var shown = steps.Where(s => s.Visibility == Visibility.Visible || isWork(s)).ToList(); // a hidden raw answer box splits nothing
        if (!_settings.ViewThinking)
            for (var i = 0; i < shown.Count; i++)
                if (isWork(shown[i]))
                {
                    if (i == 0 || !isWork(shown[i - 1]))
                        runs.Add([]);
                    runs[^1].Add(shown[i]);
                }
        if (inRun) // a lone live line (under a streaming block) is no working row
            runs.RemoveAll(r => !r.OfType<Expander>().Any());
        var kept = new HashSet<Expander>();
        foreach (var run in runs)
        {
            if (!_workRows.TryGetValue(run[0], out var row))
            {
                row = BuildExpander("THINKING", "", new StackPanel(), out _);
                var heading = (TextBlock)((StackPanel)row.Header).Children[0];
                heading.ClearValue(StyleProperty); // the site's thinking row: a sparkle and "Thought for 4 seconds", not a small-caps label
                (heading.FontSize, heading.FontWeight, heading.Margin) = (13, FontWeights.Medium, new Thickness(0, 0, 10, 0));
                row.Tag = WorkGroup;
                row.Margin = new Thickness(0, 2, 0, 2);
                row.Expanded += (_, _) => Regroup(content, inRun);
                row.Collapsed += (_, _) => Regroup(content, inRun);
                _workRows[run[0]] = row;
            }
            kept.Add(row);
            ((TextBlock)((StackPanel)row.Header).Children[1]).Text = WorkSummary(run);
            var seconds = run.OfType<Expander>().Sum(x => x.DataContext is double d ? d : 0); // each ended thinking block keeps its length
            SetThoughtLabel((TextBlock)((StackPanel)row.Header).Children[0], seconds > 0 ? ThoughtFor(TimeSpan.FromSeconds(seconds)) : "Worked");
            var at = content.Children.IndexOf(run[0]);
            if (!content.Children.Contains(row) || content.Children.IndexOf(row) != at - 1) // (a run at the very start has no step before it)
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

    // The row's heading: a muted sparkle, then the text ("Thought for 4 seconds"), in the ink-2 shade.
    private static void SetThoughtLabel(TextBlock label, string text)
    {
        if (label.Inlines.Count == 2 && ((Run)label.Inlines.LastInline).Text == text)
            return;
        label.Inlines.Clear();
        var spark = new Run("✦  ") { FontSize = 12 };
        spark.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CodeLineNumber");
        label.Inlines.Add(spark);
        label.Inlines.Add(new Run(text));
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }

    // "2 thoughts · read 3 files · searched 2 times · ran 1 command": the run's steps by kind.
    internal static string WorkSummary(IEnumerable<UIElement> run)
    {
        var counts = new Dictionary<string, int>();
        foreach (var step in run.OfType<Expander>())
        {
            var texts = (step.Header as StackPanel)?.Children.OfType<TextBlock>().ToList() ?? [];
            var kind = texts.FirstOrDefault()?.Text ?? "";
            if (kind == "TOOL") // a run's tool section: its preview starts with the tool's name
                kind = texts.ElementAtOrDefault(1)?.Text.Split(' ')[0] ?? "";
            var key = kind.StartsWith("THINKING", StringComparison.OrdinalIgnoreCase) ? "thought" : kind.ToLowerInvariant() switch
            {
                "read" or "read_file" => "read",
                "search_files" or "grep" or "find" or "list_files" or "ls" => "searched",
                "edit" or "replace_text" => "edited",
                "write" or "write_file" => "wrote",
                "run_command" or "run_checks" or "bash" => "ran",
                "browser" => "browser",
                "computer" => "computer",
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
                "computer" => "used the computer " + N(n, "time", "times"),
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

    // --ui-smoke: a tool call that writes a file streams into a section like thinking: collapsible, its code in a fenced block,
    // folded into the THINKING row with View thinking off; the finished call's result joins that section (no second one).
    private bool ToolCallSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: tool call: {what}"); return false; }
        var (view, turn, reply) = (_settings.ViewThinking, _turn, _reply);
        try
        {
            ClearConversation(_chat);
            _turn = _chat;
            var r = _reply = AddReply("planner");
            _settings.ViewThinking = true;
            const string path = """{"path": "index.html", """;
            AppendChatDelta("planner", "tool", path, "write");
            if (r.Content.Children.OfType<Expander>().Any())
                return Fail("a section before any code streamed");
            AppendChatDelta("planner", "tool", "\"content\": \"<p class=\\\"a\\\">hi", "write");
            AppendChatDelta("planner", "tool", """</p>\n<b>x""", "write");
            var sections = r.Content.Children.OfType<Expander>().ToList();
            if (sections.Count != 1 || sections[0].Content is not StackPanel { Children: [TextBox raw, RichTextBox, ..] } panel)
                return Fail($"{sections.Count} sections while streaming");
            if (raw.Text != "```html\n<p class=\"a\">hi</p>\n<b>x" || sections[0].IsExpanded)
                return Fail("code so far: " + raw.Text);
            var preview = HeaderPreview(sections[0]).Text;
            if (!preview.Contains("characters"))
                return Fail("preview: " + preview);
            _settings.ViewThinking = false;
            RegroupAll();
            if (sections[0].Visibility != Visibility.Collapsed || r.Content.Children.OfType<Expander>().Count(x => x.Tag as string == WorkGroup) != 1)
                return Fail($"View thinking off did not fold the call: {sections[0].Visibility}, rows {r.Content.Children.OfType<Expander>().Count(x => x.Tag as string == WorkGroup)}, children {r.Content.Children.Count}, vt {_settings.ViewThinking}");
            _settings.ViewThinking = true;
            RegroupAll();
            AppendChatDelta("planner", "tool", "\"}", "write");
            AddCodeTool("write", """{"path":"index.html","content":"<p class=\"a\">hi</p>\n<b>x"}""", "Successfully wrote index.html");
            sections = r.Content.Children.OfType<Expander>().ToList();
            if (sections.Count != 1 || panel.Children.Count != 3 || panel.Children[2] is not TextBox { Text: "Successfully wrote index.html" } ||
                !raw.Text.EndsWith("\n```") || ((TextBlock)((StackPanel)sections[0].Header).Children[0]).Text != "write")
                return Fail($"after the result: {sections.Count} sections, {panel.Children.Count} parts, {raw.Text.Length} chars");
            // a call's live "tok/s · tokens" line belongs to the block it began in, and stays while its tool call streams
            ClearConversation(_chat);
            r = _reply = AddReply("planner");
            AppendChatDelta("planner", "thinking", "hmm");
            var liveLine = r.Block?.Live;
            AppendChatDelta("planner", "tool", "{\"path\": \"a.py\", \"content\": \"x", "write");
            if (r.Block is null || liveLine is null || liveLine.Parent is null || r.Block.Live != liveLine)
                return Fail("the call's live stats line went when its tool call began");
            // ...and with View thinking off it is not folded into the row
            _settings.ViewThinking = false;
            RegroupAll();
            if (liveLine.Visibility != Visibility.Visible)
                return Fail("the live stats line folded away with View thinking off");
            _settings.ViewThinking = true;
            RegroupAll();
            ClearConversation(_chat);
            r = _reply = AddReply("planner");
            _toolArgs.Clear();
            _toolBlock = null;
            // a call that never streamed (replayed) shows its code too
            AddCodeTool("write", """{"path":"a.py","content":"print(1)"}""", "ok");
            if (r.Content.Children.OfType<Expander>().Last().Content is not StackPanel { Children: [TextBox { Text: "```py\nprint(1)\n```" }, RichTextBox, TextBox] })
                return Fail("a replayed call's code");
            return true;
        }
        finally
        {
            _settings.ViewThinking = view;
            _toolArgs.Clear();
            _toolBlock = null;
            ClearConversation(_chat);
            (_turn, _reply) = (turn, reply);
        }
    }

    // --ui-smoke: a project run folds the same way, live: each run of THINKING and TOOL sections becomes one row between the
    // role headers and responses; a block still streaming stays in view until it ends; a new run leaves the old rows alone.
    private async Task<bool> RunThinkingSmoke(Action<string, FrameworkElement?> snap)
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.Thinking.cs:{line}"); return false; }
        async Task Run(params string[] events)
        {
            foreach (var json in events)
                _lines.Enqueue(LauncherLogic.EventPrefix + json);
            DrainLines();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); // the queued regroup runs first
        }
        static List<Expander> Rows(Panel p) => p.Children.OfType<Expander>().Where(x => x.Tag as string == WorkGroup).ToList();
        static string Text(Expander x, int i) => ((TextBlock)((StackPanel)x.Header).Children[i]).Text;
        var (view, chatShown) = (_settings.ViewThinking, NavChat.IsChecked == true);
        StackPanel? first = null, second = null;
        try
        {
            ShowView(); // Code, where a run's output shows
            _settings.ViewThinking = false;
            ResetRunView();
            first = OutputPanel;
            await Run("""{"e":"delta","role":"planner","kind":"thinking","text":"planning the page"}""",
                """{"e":"delta","role":"planner","kind":"tool","index":0,"name":"read_file","text":"{\"path\":\"a.js\"}"}""",
                """{"e":"done","role":"planner","final":false}""",
                """{"e":"tool","role":"planner","name":"read_file","args":"{\"path\":\"a.js\"}","result":"OK"}""",
                """{"e":"tool","role":"planner","name":"run_command","args":"{\"command\":\"npm test\"}","result":"passed"}""",
                """{"e":"delta","role":"planner","kind":"text","text":"Built it."}""",
                """{"e":"done","role":"planner","final":true}""",
                """{"e":"delta","role":"reviewer","kind":"thinking","text":"checking"}""",
                """{"e":"delta","role":"reviewer","kind":"text","text":"Looks right."}""",
                """{"e":"done","role":"reviewer","final":false}""");
            var rows = Rows(first);
            var responses = first.Children.OfType<Expander>().Where(x => Text(x, 0) is "RESPONSE" or "FINAL RESPONSE").ToList();
            var headers = first.Children.OfType<TextBlock>().Where(t => t.Tag is null).ToList();
            var work = first.Children.OfType<UIElement>().Where(IsRunWork).ToList();
            if (rows.Count != 2 || Text(rows[0], 1) != "1 thought · read 1 file · ran 1 command" || Text(rows[1], 1) != "1 thought" ||
                responses.Count != 2 || headers.Count != 2 || responses.Concat<UIElement>(headers).Any(e => e.Visibility != Visibility.Visible) ||
                work.Count != 4 || work.Any(e => e.Visibility != Visibility.Collapsed) ||
                first.Children.IndexOf(rows[0]) != first.Children.IndexOf(headers[0]) + 1 || first.Children.IndexOf(rows[1]) != first.Children.IndexOf(headers[1]) + 1)
                return Fail();
            await Task.Delay(400);
            snap("41-run-thinking-off", null);
            rows[0].IsExpanded = true; // its steps show, the reviewer's stay folded, and it stays open through a regroup
            RegroupAll();
            if (work.Take(3).Any(e => e.Visibility != Visibility.Visible) || work[3].Visibility != Visibility.Collapsed || Rows(first).Count != 2 ||
                responses.Any(e => e.Visibility != Visibility.Visible))
                return Fail();
            rows[0].IsExpanded = false;
            if (work.Any(e => e.Visibility != Visibility.Collapsed) || responses.Any(e => e.Visibility != Visibility.Visible))
                return Fail();
            _settings.ViewThinking = true;
            RegroupAll();
            if (Rows(first).Count != 0 || first.Children.OfType<UIElement>().Any(e => e.Visibility != Visibility.Visible))
                return Fail();
            // Off mid-stream: the open block keeps its text in view and grows; once it ends it folds into a row of its own.
            await Run("""{"e":"delta","role":"reviewer","kind":"thinking","text":"one more"}""");
            var open = _stream?.Expander;
            _settings.ViewThinking = false;
            RegroupAll();
            if (open is null || open.Visibility != Visibility.Visible || Rows(first).Count != 2)
                return Fail();
            await Run("""{"e":"delta","role":"reviewer","kind":"thinking","text":" look"}""");
            if (open.Visibility != Visibility.Visible || !_stream!.Text.ToString().Contains("one more look"))
                return Fail();
            await Run("""{"e":"done","role":"reviewer","final":false}""");
            if (open.Visibility != Visibility.Collapsed || Rows(first).Count != 3 || Text(Rows(first)[2], 1) != "1 thought")
                return Fail();
            // The next run's panel folds on its own; the last run's rows are not duplicated.
            ResetRunView();
            second = OutputPanel;
            await Run("""{"e":"delta","role":"planner","kind":"thinking","text":"again"}""", """{"e":"done","role":"planner","final":false}""",
                """{"e":"delta","role":"planner","kind":"text","text":"Done again."}""", """{"e":"done","role":"planner","final":true}""");
            if (second == first || second.Parent != CodePanel || first.Parent != CodePanel || Rows(first).Count != 3 || Rows(second).Count != 1)
                return Fail();
            return true;
        }
        finally
        {
            _settings.ViewThinking = view;
            RegroupAll();
            ResetRunView();
            StopClocks();
            CodePanel.Children.Remove(first);
            CodePanel.Children.Remove(second);
            ShowView(chat: chatShown);
        }
    }
}

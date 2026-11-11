using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Entry = AgentOrchestrator.ConversationStore.Entry;

namespace AgentOrchestrator;

// The sidebar's Search: a palette over the window (SearchOverlay) that finds conversations by title and message text.
// Its tabs pick All, Chat or Code conversations; "Show archived" adds the archived ones. The texts are copied from the
// store when it opens and scanned off the UI thread, a newer query cancelling the older scan. A result opens like its
// sidebar row (the same refusals); the palette stays open when that is refused. Local only: nothing is sent.
public partial class MainWindow
{
    // One conversation as searched: its sidebar entry, whether archived, and each message's text, whitespace collapsed.
    internal sealed record SearchDoc(Entry Entry, bool Archived, string[] Texts);
    // A listed conversation: whether its title matched, and the snippet of the matching message (null: none) with the
    // (start, length) of each matched word in it.
    internal sealed record SearchHit(SearchDoc Doc, bool TitleMatch, string? Snippet, IReadOnlyList<(int Start, int Length)> Marks);

    internal static readonly string[] SearchTabNames = ["All", "Chat", "Code"];
    private const int SearchLimit = 50, SnippetLength = 80;

    internal static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Every word of the query in the title, or every word in one message (case-insensitive); title matches first, then the
    // most recently updated; at most 50. An empty query lists the most recent conversations.
    internal static List<SearchHit> SearchConversations(IEnumerable<SearchDoc> docs, string query, string tab, bool includeArchived, CancellationToken cancel = default)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool All(string text) => words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        var hits = new List<SearchHit>();
        foreach (var doc in docs)
        {
            cancel.ThrowIfCancellationRequested();
            if (!includeArchived && doc.Archived || tab == "Chat" && doc.Entry.Kind != "chat" || tab == "Code" && doc.Entry.Kind == "chat")
                continue;
            if (words.Length == 0)
            {
                hits.Add(new(doc, false, null, []));
                continue;
            }
            var title = All(doc.Entry.Title);
            var body = doc.Texts.FirstOrDefault(All);
            if (!title && body is null)
                continue;
            if (body is null)
                hits.Add(new(doc, title, null, []));
            else
            {
                var (snippet, marks) = Snippet(body, words);
                hits.Add(new(doc, title, snippet, marks));
            }
        }
        return hits.OrderByDescending(h => h.TitleMatch).ThenByDescending(h => h.Doc.Entry.Updated).Take(SearchLimit).ToList();
    }

    // About 80 characters of text around its first match, "…" on a cut end, and every word's matches in it (merged).
    internal static (string Text, List<(int Start, int Length)> Marks) Snippet(string text, string[] words)
    {
        var first = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, Math.Min(first - 25, text.Length - SnippetLength));
        var end = Math.Min(text.Length, start + SnippetLength);
        if (start > 0 && char.IsLowSurrogate(text[start]))
            start++;
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
            end--;
        var lead = start > 0 ? "…" : "";
        var cut = lead + text[start..end].Trim() + (end < text.Length ? "…" : "");
        var spans = new List<(int Start, int Length)>();
        foreach (var w in words)
            for (var i = cut.IndexOf(w, StringComparison.OrdinalIgnoreCase); i >= 0; i = cut.IndexOf(w, i + w.Length, StringComparison.OrdinalIgnoreCase))
                spans.Add((i, w.Length));
        var marks = new List<(int Start, int Length)>();
        foreach (var (s, l) in spans.OrderBy(m => m.Start))
            if (marks.Count > 0 && s <= marks[^1].Start + marks[^1].Length)
                marks[^1] = (marks[^1].Start, Math.Max(marks[^1].Length, s + l - marks[^1].Start));
            else
                marks.Add((s, l));
        return (cut, marks);
    }

    internal static string RelativeDate(DateTime when, DateTime now)
    {
        var days = (now.Date - when.Date).Days;
        return days <= 0 ? "Today" : days == 1 ? "Yesterday" : days < 7 ? "Past week" : days < 30 ? "Past month" : days < 365 ? "Past year"
            : when.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    // ---- the palette

    private Task<List<SearchDoc>>? _searchDocs;
    private Task _searchRun = Task.CompletedTask; // --ui-smoke awaits the latest scan
    private CancellationTokenSource? _searchCancel;
    private DispatcherTimer? _searchDebounce;
    private List<SearchHit> _searchHits = [];
    private readonly List<Border> _searchRows = [];
    private int _searchSelected = -1;
    private string _searchTab = "All";
    private bool _searchArchived, _webHiddenForSearch;
    internal Func<DateTime> SearchNow = () => DateTime.Now; // --ui-smoke fixes the date labels

    private void OpenSearch()
    {
        // The texts as they are now, copied here (the store is the UI thread's); collapsed off it.
        var view = _store.View();
        var copied = view.Groups.SelectMany(g => g.Items).Concat(view.Ungrouped).Select(e => (e, false))
            .Concat(view.Archived.Select(e => (e, true)))
            .Select(p => (p.e, archived: p.Item2, texts: _store.Get(p.e.Id)?.Messages.Select(m => m.Content ?? "").ToArray() ?? [])).ToList();
        _searchDocs = Task.Run(() => copied.Select(c => new SearchDoc(c.e, c.archived, c.texts.Select(Collapse).ToArray())).ToList());
        (_searchTab, _searchArchived) = ("All", false);
        SearchArchived.IsChecked = false;
        SearchBox.Text = "";
        SearchOverlay.Visibility = Visibility.Visible;
        if (_web is { Visibility: Visibility.Visible } web) // the browser's page is a window of its own, drawn over WPF
        {
            web.Visibility = Visibility.Hidden;
            _webHiddenForSearch = true;
        }
        SearchBox.Focus();
        RunSearch();
    }

    private void CloseSearch()
    {
        _searchCancel?.Cancel();
        _searchDebounce?.Stop();
        SearchOverlay.Visibility = Visibility.Collapsed;
        SearchResults.Children.Clear();
        _searchRows.Clear();
        (_searchDocs, _searchHits) = (null, []);
        if (_webHiddenForSearch && _web is { } web)
            web.Visibility = Visibility.Visible;
        _webHiddenForSearch = false;
    }

    private void RunSearch()
    {
        foreach (var pill in SearchTabs.Children.OfType<ToggleButton>())
            pill.IsChecked = (string)pill.Tag == _searchTab;
        _searchCancel?.Cancel();
        if (_searchDocs is not { } loading)
            return;
        var cancel = (_searchCancel = new CancellationTokenSource()).Token;
        var (query, tab, archived) = (SearchBox.Text, _searchTab, _searchArchived);
        _searchRun = Run();
        async Task Run()
        {
            try
            {
                var docs = await loading;
                var hits = await Task.Run(() => SearchConversations(docs, query, tab, archived, cancel), cancel);
                if (!cancel.IsCancellationRequested)
                    ShowSearchHits(hits);
            }
            catch (OperationCanceledException) { }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_searchDebounce is null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                RunSearch();
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void ShowSearchHits(List<SearchHit> hits)
    {
        _searchHits = hits;
        SearchResults.Children.Clear();
        _searchRows.Clear();
        var now = SearchNow();
        foreach (var hit in hits)
        {
            var entry = hit.Doc.Entry;
            var glyph = new TextBlock { Text = entry.Kind == "chat" ? "" : "", FontSize = 13, Width = 28, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
            glyph.SetResourceReference(StyleProperty, "RailGlyph");
            var date = new TextBlock { Text = RelativeDate(entry.Updated, now), FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            date.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(glyph, Dock.Left);
            DockPanel.SetDock(date, Dock.Right);
            var titleLine = new DockPanel();
            if (hit.Doc.Archived)
            {
                var tagText = new TextBlock { Text = "Archived", FontSize = 10 };
                tagText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                var tag = new Border { Child = tagText, Tag = "Archived", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
                tag.SetResourceReference(Border.BorderBrushProperty, "Border");
                DockPanel.SetDock(tag, Dock.Right);
                titleLine.Children.Add(tag);
            }
            titleLine.Children.Add(new TextBlock { Text = entry.Title, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { titleLine } };
            if (hit.Snippet is { } snippet)
            {
                var line = new TextBlock { Tag = "Snippet", FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                var at = 0;
                foreach (var (start, length) in hit.Marks)
                {
                    line.Inlines.Add(new Run(snippet[at..start]));
                    var bold = new Run(snippet.Substring(start, length)) { FontWeight = FontWeights.Bold };
                    bold.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
                    line.Inlines.Add(bold);
                    at = start + length;
                }
                line.Inlines.Add(new Run(snippet[at..]));
                text.Children.Add(line);
            }
            var row = new Border { Tag = entry.Id, Padding = new Thickness(10, 8, 12, 8), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand,
                Child = new DockPanel { Children = { glyph, date, text } } };
            row.SetResourceReference(Border.CornerRadiusProperty, "SmallRadius");
            var index = _searchRows.Count;
            row.MouseEnter += (_, _) => SelectSearchRow(index, scroll: false);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenSearchHit(index);
            };
            _searchRows.Add(row);
            SearchResults.Children.Add(row);
        }
        if (hits.Count == 0)
        {
            var none = new TextBlock { Text = "No conversations found", FontSize = 12, Margin = new Thickness(12, 10, 12, 10) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            SearchResults.Children.Add(none);
        }
        SearchScroll.ScrollToTop();
        SelectSearchRow(hits.Count > 0 ? 0 : -1, scroll: false);
    }

    private void SelectSearchRow(int index, bool scroll = true)
    {
        _searchSelected = _searchRows.Count == 0 ? -1 : Math.Clamp(index, 0, _searchRows.Count - 1);
        for (var i = 0; i < _searchRows.Count; i++)
            if (i == _searchSelected)
                _searchRows[i].SetResourceReference(Border.BackgroundProperty, "Hover");
            else
                _searchRows[i].Background = System.Windows.Media.Brushes.Transparent;
        if (scroll && _searchSelected >= 0)
            _searchRows[_searchSelected].BringIntoView();
    }

    // Opens like the sidebar row; closes the palette only when the conversation is then the one shown.
    private void OpenSearchHit(int index)
    {
        if (index < 0 || index >= _searchHits.Count)
            return;
        var entry = _searchHits[index].Doc.Entry;
        OpenSidebarConversation(entry);
        var chat = entry.Kind == "chat";
        if ((chat ? _chatConv : _codeConv)?.Id == entry.Id && (chat ? NavChat : NavCode).IsChecked == true)
            CloseSearch();
    }

    private void SwitchSearchTab(int index)
    {
        _searchTab = SearchTabNames[(index % 3 + 3) % 3];
        RunSearch();
    }

    private void SearchTab_Click(object sender, RoutedEventArgs e) => SwitchSearchTab(Array.IndexOf(SearchTabNames, (string)((FrameworkElement)sender).Tag));

    private void SearchArchived_Click(object sender, RoutedEventArgs e)
    {
        _searchArchived = SearchArchived.IsChecked == true;
        RunSearch();
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void SearchOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SearchOverlay)) // the scrim, not the card
            CloseSearch();
    }

    private void SearchOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape: CloseSearch(); break;
            case Key.Down: SelectSearchRow(_searchSelected + 1); break;
            case Key.Up: SelectSearchRow(_searchSelected - 1); break;
            case Key.Enter: OpenSearchHit(_searchSelected); break;
            case Key.Tab: SwitchSearchTab(Array.IndexOf(SearchTabNames, _searchTab) + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1)); break;
            case Key.D1 or Key.D2 or Key.D3 when ctrl: SwitchSearchTab(e.Key - Key.D1); break;
            default: e.Handled = false; break;
        }
    }

    // --ui-smoke: the sidebar row, the palette's focus and tabs, matching by message text per tab, archived on request,
    // opening a chat and a Code result in their views, a refused open keeping it up, and Esc / the scrim closing it.
    private async Task<bool> SearchSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: search: {what}"); return false; }
        var (turn, busy, now) = (_turn, _chatBusy, SearchNow);
        var folder = System.IO.Path.Combine(AppSettings.DataRoot, "search-smoke");
        System.IO.Directory.CreateDirectory(folder);
        ConversationStore.Conversation Seed(string kind, string title, string text)
        {
            var c = _store.Create(kind == "chat" ? "" : folder, kind)!;
            (c.Title, c.Messages) = (title, [new("user", text), new("assistant", "Noted.")]);
            _store.Save(c);
            return c;
        }
        var chat = Seed("chat", "Smoke weather", "Tell me about the Zephyrine\n dashboard, please");
        var code = Seed("code", "Smoke build", "Fix zephyrine in the code");
        var archived = Seed("chat", "Smoke archived", "An old zephyrine note");
        _store.Archive(archived.Id, true);
        RefreshSidebarList();
        string Rows() => string.Join(",", _searchRows.Select(r => r.Tag as string == chat.Id ? "chat" : r.Tag as string == code.Id ? "code" : r.Tag as string == archived.Id ? "archived" : "?"));
        void Press(Key key) => SearchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        async Task Typed(string text)
        {
            SearchBox.Text = text;
            await Task.Delay(300); // the debounce
            await _searchRun;
        }
        try
        {
            var tags = SidebarList.Children.OfType<Button>().Select(b => b.Tag as string).ToList();
            var at = tags.IndexOf("+Search");
            if (at < 1 || tags[at - 1] != "+New" || tags.IndexOf("+Remote API") < at)
                return Fail("the Search row is not below New: " + string.Join(",", tags));
            // Model Settings sits between Search and Remote API; it opens the page with the worker tabs and the model selector
            // in place of the conversation, and the view (as New and every conversation open it) leaves it.
            if (at + 2 >= tags.Count || tags[at + 1] != "+Model Settings" || tags[at + 2] != "+Remote API")
                return Fail("the sidebar order is not Search, Model Settings, Remote API: " + string.Join(",", tags));
            var wasChat = NavChat.IsChecked == true;
            SelectWorker(CardFor("planner"));
            SmokeClick(SidebarList.Children.OfType<Button>().First(b => b.Tag as string == "+Model Settings"));
            await Task.Delay(400); // the views' fades
            if (!ModelSettingsView.IsVisible || ChatView.IsVisible || CodeView.IsVisible || !WorkerTabs.IsVisible || !AddWorkerButton.IsVisible ||
                SmokeFind<ComboBox>(WorkerSettingsHost, c => c.Tag as string == "Model") is not { IsVisible: true } || !WorkerRail.IsVisible)
                return Fail("the Model Settings page");
            ShowView(wasChat);
            await Task.Delay(400);
            if (ModelSettingsView.IsVisible)
                return Fail("the view did not leave Model Settings");
            SmokeClick(SidebarList.Children.OfType<Button>().First(b => b.Tag as string == "+Search"));
            await _searchRun;
            if (SearchOverlay.Visibility != Visibility.Visible || !SearchBox.IsFocused)
                return Fail("the palette did not open with the box focused");
            if (string.Join(",", SearchTabs.Children.OfType<ToggleButton>().Select(t => $"{t.Content}{(t.IsChecked == true ? "*" : "")}")) != "All*,Chat,Code")
                return Fail("the tabs");
            // empty: the recent conversations, newest first, titles only, archived left out
            if (!Rows().StartsWith("code,chat") || Rows().Contains("archived") || SmokeFind<TextBlock>(SearchResults, t => t.Tag as string == "Snippet") is not null)
                return Fail("the empty query's list: " + Rows());
            SearchNow = () => DateTime.Now.AddDays(1);
            await Typed("zephyrine");
            if (Rows() != "code,chat" || SmokeFind<TextBlock>(_searchRows[1], t => t.Tag as string == "Snippet") is not { } snippet ||
                !snippet.Inlines.OfType<Run>().Any(r => r.Text == "Zephyrine" && r.FontWeight == FontWeights.Bold) ||
                SmokeFind<TextBlock>(_searchRows[1], t => t.Text == "Yesterday") is null)
                return Fail("All: " + Rows());
            Press(Key.Tab);
            await _searchRun;
            if (Rows() != "chat")
                return Fail("Chat: " + Rows());
            Press(Key.Tab);
            await _searchRun;
            if (Rows() != "code")
                return Fail("Code: " + Rows());
            SmokeClick(SearchTabs.Children.OfType<ToggleButton>().First());
            await _searchRun;
            SearchArchived.IsChecked = true;
            SmokeClick(SearchArchived);
            await _searchRun;
            if (Rows() != "archived,code,chat" || SmokeFind<Border>(_searchRows[0], b => b.Tag as string == "Archived") is null) // seeded last: newest
                return Fail("Show archived: " + Rows());
            SearchArchived.IsChecked = false;
            SmokeClick(SearchArchived);
            await _searchRun;
            if (Rows() != "code,chat")
                return Fail("Show archived off: " + Rows());
            // refused while a Code reply runs: a toast, the palette stays
            ToastStack.Children.Clear();
            (_chatBusy, _turn) = (true, _code);
            SelectSearchRow(0);
            Press(Key.Enter);
            (_chatBusy, _turn) = (busy, turn);
            if (!Toasted("Wait for the reply or run to finish") || SearchOverlay.Visibility != Visibility.Visible)
                return Fail("a refused open");
            Press(Key.Down);
            Press(Key.Enter);
            if (SearchOverlay.Visibility == Visibility.Visible || NavChat.IsChecked != true || _chatConv?.Id != chat.Id)
                return Fail("Enter did not open the chat");
            OpenSearch();
            await Typed("zephyrine");
            Press(Key.Enter);
            if (SearchOverlay.Visibility == Visibility.Visible || NavCode.IsChecked != true || _codeConv?.Id != code.Id)
                return Fail("Enter did not open the Code conversation");
            OpenSearch();
            Press(Key.Escape);
            if (SearchOverlay.Visibility == Visibility.Visible)
                return Fail("Esc");
            OpenSearch();
            SearchCard.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
            if (SearchOverlay.Visibility != Visibility.Visible)
                return Fail("a click on the card closed it");
            SearchOverlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
            if (SearchOverlay.Visibility == Visibility.Visible)
                return Fail("the scrim click");
            return true;
        }
        finally
        {
            CloseSearch();
            (_chatBusy, _turn, SearchNow) = (busy, turn, now);
        }
    }

    internal static void SearchSelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Search self-test failed: " + what);
        }
        var now = new DateTime(2026, 6, 15, 12, 0, 0);
        SearchDoc Doc(string id, string title, string kind, int daysAgo, bool archived = false, params string[] texts) =>
            new(new Entry(id, title, "", now.AddDays(-daysAgo), kind), archived, texts.Select(Collapse).ToArray());
        var docs = new List<SearchDoc>
        {
            Doc("c1", "Weather dashboard", "chat", 3, false, "How do I build a dashboard?", "Use a grid of cards."),
            Doc("c2", "Groceries", "chat", 0, false, "Milk, eggs and a DASHBOARD of fruit"),
            Doc("k1", "Fix the build", "code", 1, false, "The build fails on the  login\n page", "Patched login.js and the dashboard"),
            Doc("a1", "Old dashboard", "chat", 10, true, "archived text"),
        };
        string Ids(List<SearchHit> hits) => string.Join(",", hits.Select(h => h.Doc.Entry.Id));
        // title first (c1), then by recency (c2 today, k1 yesterday); case-insensitive; archived left out
        Check(Ids(SearchConversations(docs, "Dashboard", "All", false)) == "c1,c2,k1", "title first, then recency");
        Check(Ids(SearchConversations(docs, "dashboard", "Chat", false)) == "c1,c2", "Chat tab");
        Check(Ids(SearchConversations(docs, "dashboard", "Code", false)) == "k1", "Code tab");
        Check(Ids(SearchConversations(docs, "dashboard", "All", true)) == "c1,a1,c2,k1", "archived included");
        // multi-word: every word in one message (any order), or in the title
        Check(Ids(SearchConversations(docs, "login PATCHED", "All", false)) == "k1", "multi-word in one message");
        Check(Ids(SearchConversations(docs, "build login", "All", false)) == "k1", "multi-word, collapsed whitespace");
        Check(SearchConversations(docs, "eggs grid", "All", false).Count == 0, "words in different conversations");
        Check(Ids(SearchConversations(docs, "", "All", false)) == "c2,k1,c1", "empty query: most recent");
        Check(SearchConversations(docs, "", "All", false).All(h => h.Snippet is null), "empty query: no snippets");
        // the snippet: the matching message's text, bold on each word; none for a title-only match
        var groceries = SearchConversations(docs, "milk dashboard", "All", false).Single();
        Check(groceries.Snippet == "Milk, eggs and a DASHBOARD of fruit" && groceries.Marks.SequenceEqual([(0, 4), (17, 9)]), "highlights for two words");
        Check(SearchConversations(docs, "weather", "All", false).Single() is { TitleMatch: true, Snippet: null }, "title-only: no snippet");
        Check(SearchConversations(docs, "dashboard", "All", false)[0] is { TitleMatch: true, Snippet: "How do I build a dashboard?" }, "title and body: the body's snippet");
        // long messages: cut to about 80 around the first match, ellipsis on cut ends
        var filler = string.Concat(Enumerable.Repeat("lorem ipsum ", 40));
        var (mid, midMarks) = Snippet(filler + "needle" + filler, ["needle"]);
        Check(mid.StartsWith('…') && mid.EndsWith('…') && mid.Length <= SnippetLength + 2 && midMarks is [(var m, 6)] && mid.Substring(m, 6) == "needle", "middle: " + mid);
        var (start, _) = Snippet("needle " + filler, ["needle"]);
        Check(start.StartsWith("needle") && start.EndsWith('…'), "start: " + start);
        var (end, endMarks) = Snippet(filler + "needle", ["needle"]);
        Check(end.StartsWith('…') && end.EndsWith("needle") && endMarks.Count == 1, "end: " + end);
        // at most 50
        var many = Enumerable.Range(0, 70).Select(i => Doc("m" + i, "Note " + i, "chat", i)).ToList();
        Check(SearchConversations(many, "note", "All", false) is { Count: 50 } top && top[0].Doc.Entry.Id == "m0", "50-row cap");
        // date labels
        Check(new[] { 0, 1, 3, 20, 200 }.Select(d => RelativeDate(now.AddDays(-d), now)).SequenceEqual(["Today", "Yesterday", "Past week", "Past month", "Past year"])
            && RelativeDate(new DateTime(2024, 3, 2), now) == "Mar 2, 2024", "relative dates");
    }
}

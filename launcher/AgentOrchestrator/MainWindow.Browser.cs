using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentOrchestrator;

// The in-app browser: a page in the right column, in place of the worker details, opened by the globe beside the context
// ring. Links in replies open in it, and with "Follow the model" on it shows each search and page the model reads. It has
// tabs, one WebView2 (the Edge engine Windows ships) each, made as they open; their data stays in <data>\browser.
public partial class MainWindow
{
    private sealed class BrowserTab
    {
        public required WebView2 Web;
        public required ToggleButton Chip;
        public required TextBlock Title;
        public string Url = "";
        public readonly List<string> Errors = new(); // its page's script errors since it started loading
        public TaskCompletionSource<bool>? Loaded;
    }

    private readonly List<BrowserTab> _tabs = new();
    private BrowserTab? _tab; // the shown one
    private WebView2? _web => _tab?.Web;
    private const string BrowserHome = "https://duckduckgo.com/";

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserPanel.IsVisible)
            CloseBrowser();
        else
            OpenBrowser(null);
    }

    private void BrowserClose_Click(object sender, RoutedEventArgs e) => CloseBrowser();

    // url null: the last page, else the home page. trusted: a file:// page the engine already checked (the model's browser tool).
    // navigate false: only open (the model's browser tool loads its page once the column has slid open).
    private void OpenBrowser(string? url, bool trusted = false, bool navigate = true)
    {
        StopColumnTween(); // a slide still running finishes first
        if (!BrowserPanel.IsVisible)
        {
            RailPopup.IsOpen = false;
            var fromRail = WorkerRail.IsVisible; // from the rail the column slides open; from the details it only swaps pages
            WorkerRail.Visibility = DetailsPanel.Visibility = Visibility.Collapsed;
            BrowserPanel.Visibility = Visibility.Visible;
            SlideColumn(BrowserPanel, collapsed: false, slide: fromRail);
            BrowserFollowToggle.IsChecked = _settings.BrowserFollow;
            _settings.BrowserOpen = true;
            _settings.Save();
        }
        if (navigate && (url is not null || _web is null))
            Navigate(url ?? (_settings.BrowserUrl.Length > 0 ? _settings.BrowserUrl : BrowserHome), trusted);
    }

    // Back to the worker details (or their rail), as they were.
    private void CloseBrowser()
    {
        _settings.BrowserOpen = false;
        _settings.Save();
        SetWorkerRail(_settings.DetailsCollapsed); // hides the browser
    }

    // In the shown tab (the first one if none is open).
    private void Navigate(string text, bool trusted = false)
    {
        if ((trusted ? text : BrowserUrl(text)) is not { } url || (_tab ?? NewTab()) is not { } tab)
            return;
        tab.Errors.Clear();
        tab.Url = BrowserAddress.Text = url;
        if (tab.Web.CoreWebView2 is { } core)
            core.Navigate(url);
        else
            tab.Web.Source = new Uri(url); // the first page: this starts the engine
    }

    private void OpenTab(string url, bool trusted = false)
    {
        if (NewTab() is not null)
            Navigate(url, trusted);
    }

    private void BrowserNewTab_Click(object sender, RoutedEventArgs e) => OpenTab(BrowserHome);

    // A new, blank tab, shown. Null when the browser can't start.
    private BrowserTab? NewTab()
    {
        if (BrowserMessage.IsVisible)
            return null;
        WebView2 web;
        try
        {
            web = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(AppSettings.DataRoot, "browser") } };
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
            BrowserFailed(e);
            return null;
        }
        var title = new TextBlock { Text = "New tab", MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = "\u00D7", FontSize = 12, Padding = new Thickness(4, 0, 2, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Close tab",
            VerticalAlignment = VerticalAlignment.Center };
        close.SetResourceReference(StyleProperty, "BareButton");
        var chip = new ToggleButton { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, close } } };
        chip.SetResourceReference(StyleProperty, "ChipToggle");
        var tab = new BrowserTab { Web = web, Chip = chip, Title = title };
        chip.Click += (_, _) => SelectTab(tab);
        chip.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
                CloseTab(tab);
        };
        close.Click += (_, e) =>
        {
            e.Handled = true; // not the chip's click
            CloseTab(tab);
        };
        web.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (!_tabs.Contains(tab)) // closed while the engine started (its start then fails)
                return;
            if (!e.IsSuccess)
            {
                BrowserFailed(e.InitializationException);
                return;
            }
            // the page's script errors, for the model's browser tool
            Forget(web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}"));
            web.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, a) =>
                tab.Errors.Add(ExceptionText(a.ParameterObjectAsJson));
            web.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, a) =>
            {
                if (ConsoleError(a.ParameterObjectAsJson) is { } line)
                    tab.Errors.Add(line);
            };
            // pop-ups and target=_blank links open in a new tab, not in a separate window
            web.CoreWebView2.NewWindowRequested += (_, a) =>
            {
                a.Handled = true;
                OpenTab(a.Uri, trusted: true);
            };
            web.CoreWebView2.DocumentTitleChanged += (_, _) => RefreshTab(tab);
        };
        web.NavigationCompleted += (_, done) =>
        {
            tab.Loaded?.TrySetResult(done.IsSuccess);
            if (web.Source?.AbsoluteUri is { } shown)
            {
                tab.Url = shown;
                if (tab == _tab)
                {
                    BrowserAddress.Text = shown;
                    _settings.BrowserUrl = shown;
                    _settings.Save();
                }
            }
            RefreshTab(tab);
            RefreshBrowserButtons();
        };
        BrowserHost.Children.Add(web);
        BrowserTabStrip.Children.Add(chip);
        _tabs.Add(tab);
        SelectTab(tab);
        return tab;
    }

    private void SelectTab(BrowserTab tab)
    {
        _tab = tab;
        foreach (var t in _tabs)
        {
            t.Web.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
            t.Chip.IsChecked = t == tab;
        }
        BrowserAddress.Text = tab.Url;
        if (tab.Url.Length > 0)
        {
            _settings.BrowserUrl = tab.Url;
            _settings.Save();
        }
        RefreshBrowserButtons();
        tab.Chip.BringIntoView();
    }

    // The last tab closing closes the browser; it opens again on the home page.
    private void CloseTab(BrowserTab tab)
    {
        var at = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        BrowserHost.Children.Remove(tab.Web);
        BrowserTabStrip.Children.Remove(tab.Chip);
        tab.Loaded?.TrySetResult(false);
        tab.Web.Dispose();
        if (_tab != tab)
            return;
        _tab = null;
        if (_tabs.Count > 0)
        {
            SelectTab(_tabs[Math.Min(at, _tabs.Count - 1)]);
            return;
        }
        BrowserAddress.Text = _settings.BrowserUrl = "";
        CloseBrowser();
    }

    private static string TabTitle(BrowserTab tab) =>
        tab.Web.CoreWebView2?.DocumentTitle is { Length: > 0 } title ? title
        : Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) ? (uri.IsFile ? Path.GetFileName(uri.LocalPath) : uri.Host) : "New tab";

    private void RefreshTab(BrowserTab tab)
    {
        tab.Title.Text = TabTitle(tab);
        tab.Chip.ToolTip = tab.Url.Length > 0 ? tab.Url : null;
    }

    private void BrowserFailed(Exception? e)
    {
        BrowserHost.Children.Clear();
        BrowserTabStrip.Children.Clear();
        _tabs.Clear();
        _tab = null;
        BrowserMessage.Text = e is WebView2RuntimeNotFoundException
            ? "The browser needs the Microsoft Edge WebView2 Runtime, which this PC doesn't have. Install it from Microsoft (search \"WebView2 Runtime\"), then reopen the browser."
            : $"The browser could not start: {e?.Message}";
        BrowserMessage.Visibility = Visibility.Visible;
    }

    private void RefreshBrowserButtons()
    {
        BrowserBack.IsEnabled = _web?.CanGoBack == true;
        BrowserForward.IsEnabled = _web?.CanGoForward == true;
    }

    private void BrowserBack_Click(object sender, RoutedEventArgs e) => _web?.GoBack();

    private void BrowserForward_Click(object sender, RoutedEventArgs e) => _web?.GoForward();

    private void BrowserReload_Click(object sender, RoutedEventArgs e) => _web?.Reload();

    private void BrowserExternal_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserUrl(_web?.Source?.AbsoluteUri ?? BrowserAddress.Text) is { } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void BrowserAddress_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        Navigate(BrowserAddress.Text);
    }

    private void BrowserFollow_Click(object sender, RoutedEventArgs e)
    {
        _settings.BrowserFollow = BrowserFollowToggle.IsChecked == true;
        _settings.Save();
    }

    // A link in a reply: opens where Settings > General > Open links in says; the first one asks (and remembers).
    private void OpenLink(string url)
    {
        if (_settings.LinkOpen is not ("app" or "default"))
        {
            if (Choose("Open links", "Where should links from replies open? You can change this later in Settings > General > Links.",
                    ("In-app browser", "app"), ("Default browser", "default")) is not { } where)
                return;
            _settings.LinkOpen = where;
            _settings.Save();
            SelectTagged(LinkOpenBox, where);
        }
        if (_settings.LinkOpen == "default")
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Toast($"Could not open the link: {e.Message}", "#F7C873"); }
        }
        else
            OpenBrowser(url);
    }

    private void LinkOpen_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LinkOpenBox.SelectedItem is not ComboBoxItem { Tag: string where } || where == _settings.LinkOpen)
            return;
        _settings.LinkOpen = where;
        _settings.Save();
    }

    // A model's web_search or fetch_page: shown in the open browser when it follows the model. A file the model wrote or
    // edited (result: not an error) reloads each tab's local page that is that file or sits in its folder (its CSS,
    // scripts, images), so the changes show live.
    private void BrowserFollowTool(string tool, string args, string result = "")
    {
        if (tool is "write" or "edit" or "write_file" or "replace_text")
        {
            if (!result.StartsWith("ERROR") && BrowserPanel.IsVisible && ToolTarget(args) is { Length: > 0 } raw && SelectedProjectPath() is var project)
            {
                try
                {
                    var full = Path.GetFullPath(Path.IsPathRooted(raw) || project.Length == 0 ? raw : Path.Combine(project, raw));
                    foreach (var t in _tabs)
                        if (t.Web.Source is { IsFile: true } page &&
                            full.StartsWith(Path.GetDirectoryName(Path.GetFullPath(page.LocalPath)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            t.Web.Reload();
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            return;
        }
        if (!_settings.BrowserFollow || tool is not ("web_search" or "fetch_page"))
            return;
        try
        {
            using var doc = JsonDocument.Parse(args);
            var key = tool == "fetch_page" ? "url" : "query";
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } text &&
                BrowserUrl(tool == "fetch_page" ? text : SearchUrl(text)) is { } url)
                FollowTo(url);
        }
        catch (JsonException) { }
    }

    // Follow the model: each search or page opens the browser (sliding in) in a tab of its own, at most FollowTabs of them
    // (Settings > General > Links); past that the oldest one the model opened shows the next page. The user's own tabs are
    // never taken, and the user can open more.
    private readonly List<BrowserTab> _followTabs = new(); // oldest first
    private int FollowTabs => _settings.FollowTabs is >= 3 and <= 10 ? _settings.FollowTabs : int.MaxValue; // 0: unlimited

    private void FollowTabs_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FollowTabsValue is null) // the slider's first value, while the window is built
            return;
        var stop = (int)Math.Round(FollowTabsSlider.Value);
        FollowTabsValue.Text = stop >= 11 ? "Unlimited" : $"{stop} tabs";
        var value = stop >= 11 ? 0 : stop;
        if (_settings is null || !IsLoaded || value == _settings.FollowTabs) // only the user's moves save
            return;
        _settings.FollowTabs = value;
        _settings.Save();
    }

    private void FollowTo(string url)
    {
        _followTabs.RemoveAll(t => !_tabs.Contains(t)); // closed by the user
        if (!BrowserPanel.IsVisible)
            OpenBrowser(null, navigate: false);
        BrowserTab tab;
        if (_followTabs.Count >= FollowTabs)
        {
            tab = _followTabs[0];
            _followTabs.RemoveAt(0);
            SelectTab(tab);
        }
        else if (NewTab() is { } fresh)
            tab = fresh;
        else
            return; // the browser can't start
        _followTabs.Add(tab);
        Navigate(url);
    }

    private static string SearchUrl(string words) => BrowserHome + "?q=" + Uri.EscapeDataString(words);

    // What the address box (or a link) leads to: an http(s) address as is, a bare domain over https, other words a search.
    // Null for nothing, or another scheme (file:, javascript:...).
    internal static string? BrowserUrl(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)
            return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
        return !text.Contains(' ') && text.Contains('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var site) ? site.AbsoluteUri : SearchUrl(text);
    }

    // ---- the model's browser tool (orchestrator.browser_handler): a "browser" request answered on the asking process's stdin

    // open: the browser opens (expanding the column) on the page the engine checked (a file in the chat's folders, or a web
    // address), in the shown tab or (new_tab) a new one; then, or for read (of tab N, which it shows), the page's address,
    // title, script errors and visible text go back to the model (the engine pages through the text). screenshot: tab N
    // shown, scrolled to the first `find` match, as a JPEG data URL in "image". tabs: the open tabs, numbered.
    private async Task AnswerBrowserAsync(JsonElement ev)
    {
        var asker = _chatProcess ?? _process;
        var (result, image) = await BrowserAnswerAsync(Str(ev, "action"), Str(ev, "url"),
            ev.TryGetProperty("new_tab", out var flag) && flag.ValueKind == JsonValueKind.True, (int)Num(ev, "tab"), Str(ev, "find"));
        NoteShot(image);
        ToTurn(asker, JsonSerializer.Serialize(new { id = Str(ev, "id"), result, image }));
    }

    private async Task<(string Result, string? Image)> BrowserAnswerAsync(string action, string url, bool newTab, int number, string find)
    {
        string result;
        string? image = null;
        try
        {
            if (action == "tabs")
                result = _tabs.Count == 0 ? "No tabs are open." : TabList();
            else if (action == "open")
            {
                if (!BrowserPanel.IsVisible)
                {
                    OpenBrowser(null, navigate: false); // the same slide as the globe button, before the engine starts loading
                    await Task.Delay(320);
                }
                var tab = newTab && _tabs.Count > 0 ? NewTab() : _tab ?? NewTab();
                if (tab is null)
                    result = $"ERROR: {BrowserMessage.Text}";
                else
                {
                    var loaded = tab.Loaded = new TaskCompletionSource<bool>();
                    Navigate(url, trusted: url.StartsWith("file:", StringComparison.OrdinalIgnoreCase));
                    if (await Task.WhenAny(loaded.Task, Task.Delay(20000)) != loaded.Task)
                        Log($"Browser: {url} was still loading after 20 s; reading it as it is");
                    await Task.Delay(400); // scripts that run just after load
                    result = await Report();
                }
            }
            else
            {
                if (!BrowserPanel.IsVisible)
                    OpenBrowser(null);
                if (number > _tabs.Count || number < 0)
                    result = $"ERROR: there is no tab {number}; the open tabs:\n{TabList()}";
                else
                {
                    if (number > 0)
                        SelectTab(_tabs[number - 1]);
                    if (action != "screenshot")
                        result = await Report();
                    else if (_tab?.Web.CoreWebView2 is not { } core)
                        result = await Report(); // the error
                    else
                    {
                        await Task.Delay(320); // a column that just opened lays the page out first
                        var found = find == "" || await core.ExecuteScriptAsync(FindScript(find)) == "true";
                        await Task.Delay(150);
                        using var shot = new MemoryStream();
                        var capture = core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, shot);
                        if (await Task.WhenAny(capture, Task.Delay(15000)) != capture) // the engine waits for this answer
                            return ("ERROR: the screenshot did not finish within 15 s; use read", null);
                        await capture;
                        image = "data:image/jpeg;base64," + Convert.ToBase64String(shot.ToArray());
                        result = $"TAB: {_tabs.IndexOf(_tab) + 1} of {_tabs.Count}\nURL: {core.Source}\nTITLE: {core.DocumentTitle}\n" +
                                 (found ? find == "" ? "SCREENSHOT: the part of the page the tab shows" : $"SCREENSHOT: scrolled to the first \"{find}\""
                                     : $"SCREENSHOT: \"{find}\" is not on the page, so this is the part the tab shows");
                    }
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException or IOException)
        {
            (result, image) = ($"ERROR: the browser failed: {e.Message}", null);
        }
        return (result, image);
        Task<string> Report() =>
            _tab?.Web.CoreWebView2 is { } core ? PageReport(_tab, core) : Task.FromResult(BrowserMessage.IsVisible ? $"ERROR: {BrowserMessage.Text}" : "ERROR: the browser has no page yet");
    }

    // Scrolls the first shown element whose text contains `find` (any case) to the top; "true" when there is one.
    private static string FindScript(string find) =>
        $$"""
        (() => { const q = {{JsonSerializer.Serialize(find.ToLowerInvariant())}}; const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                 while (w.nextNode()) {
                     const p = w.currentNode.parentElement; // only shown text: not a script's data (GitHub's README is in one too)
                     if (!p || p.closest("script, style, noscript, template") || !p.getClientRects().length) continue;
                     if (w.currentNode.nodeValue.toLowerCase().includes(q)) { p.scrollIntoView({ block: "start" }); return true; }
                 }
                 return false; })()
        """;

    // The page's text, main content first: the first <article> (a README, a post), else <main>, then the rest of the page.
    private const string PageTextScript = """
        (() => { const all = document.body ? document.body.innerText : '';
                 const m = document.querySelector('article') || document.querySelector('main, [role=main]');
                 const main = m ? m.innerText.trim() : '';
                 if (main.length < 200 || main.length > all.length * 0.95) return all;
                 const at = all.indexOf(main);
                 return 'MAIN CONTENT:\n' + main + '\n\nREST OF THE PAGE:\n' + (at < 0 ? all : all.slice(0, at) + all.slice(at + main.length)); })()
        """;

    private string TabList() => string.Join("\n", _tabs.Select((t, i) => $"{i + 1}. {TabTitle(t)} - {t.Url}{(t == _tab ? " (shown)" : "")}"));

    private async Task<string> PageReport(BrowserTab tab, CoreWebView2 core)
    {
        var json = await core.ExecuteScriptAsync(PageTextScript);
        var text = JsonSerializer.Deserialize<string>(json) ?? "";
        var errors = tab.Errors.Count == 0 ? "none" : string.Join("\n", tab.Errors.Distinct().Take(20).Select(e => "- " + e));
        return $"TAB: {_tabs.IndexOf(tab) + 1} of {_tabs.Count}\nURL: {core.Source}\nTITLE: {core.DocumentTitle}\nSCRIPT ERRORS: {errors}\n\n" +
               $"VISIBLE TEXT:\n{(text.Length > 400_000 ? text[..400_000] : text)}"; // the engine cuts it to the model's window

    }

    // Runtime.exceptionThrown: "message (url:line)".
    internal static string ExceptionText(string json)
    {
        try
        {
            var d = JsonDocument.Parse(json).RootElement.GetProperty("exceptionDetails");
            var message = d.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var desc) ? desc.GetString() : d.GetProperty("text").GetString();
            var at = d.TryGetProperty("url", out var u) ? $" ({u.GetString()}:{(d.TryGetProperty("lineNumber", out var l) ? l.GetInt32() + 1 : 0)})" : "";
            return (message ?? "error").Split('\n')[0] + at;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return "a script error"; }
    }

    // Runtime.consoleAPICalled of type "error": its arguments as text; null for other types.
    internal static string? ConsoleError(string json)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.GetProperty("type").GetString() != "error")
                return null;
            return "console.error: " + string.Join(" ", root.GetProperty("args").EnumerateArray()
                .Select(a => a.TryGetProperty("value", out var v) ? v.ToString() : a.TryGetProperty("description", out var d) ? d.GetString() : ""));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    internal static void BrowserSelfTest()
    {
        if (ExceptionText("""{"exceptionDetails":{"text":"Uncaught","url":"file:///C:/p/index.html","lineNumber":4,"exception":{"description":"ReferenceError: x is not defined\n    at y"}}}""")
                != "ReferenceError: x is not defined (file:///C:/p/index.html:5)" ||
            ConsoleError("""{"type":"error","args":[{"type":"string","value":"boom"},{"type":"number","value":2}]}""") != "console.error: boom 2" ||
            ConsoleError("""{"type":"log","args":[]}""") is not null)
            throw new InvalidOperationException("Browser error parsing");
        if (BrowserUrl("https://example.org/a?b=1") != "https://example.org/a?b=1" || BrowserUrl("example.org") != "https://example.org/" ||
            BrowserUrl("what is wpf") != BrowserHome + "?q=what%20is%20wpf" || BrowserUrl("javascript:alert(1)") is not null ||
            BrowserUrl(@"file:///C:/x") is not null || BrowserUrl("  ") is not null || BrowserUrl("localhost") != BrowserHome + "?q=localhost")
            throw new InvalidOperationException("BrowserUrl");
    }
}

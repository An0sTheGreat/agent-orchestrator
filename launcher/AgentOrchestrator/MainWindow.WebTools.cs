using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace AgentOrchestrator;

// The phone app's Code screen, its tools (MainWindow.WebApp.cs routes /api/terminal and /api/browser): the desktop's one
// PowerShell, read from a buffer of its output so the PC panel need not be open, and typed into as the user; the browser
// pane's tabs and address, a screenshot on request, and taps, typing and keys sent as the user's own input (no "model is
// driving" badge, no Model input switch). The panel may open inside the window; the window is never shown or activated.
public partial class MainWindow
{
    private const int WebTermRing = 256 * 1024; // chars of terminal output the phone can read back
    private const int WebTermInputMax = 64 * 1024; // UTF-8 bytes per input call
    private readonly StringBuilder _termRing = new(); // the last WebTermRing chars written (guarded by _termOut)
    private long _termSeq; // chars ever written: the end of _termRing

    // The pty's output (reader thread) and the app's own lines: to the next frame for the panel, and to the phone's buffer.
    // ponytail: trimming by chars can split an escape sequence or a surrogate pair at the buffer's start; xterm.js skips it.
    private void TermAppend(string text)
    {
        lock (_termOut)
        {
            _termOut.Append(text);
            _termRing.Append(text);
            _termSeq += text.Length;
            if (_termRing.Length > WebTermRing) // down to three quarters, so a full buffer is not moved on every chunk
                _termRing.Remove(0, _termRing.Length - WebTermRing * 3 / 4);
        }
    }

    // GET /api/terminal?since=: what followed `since`; the whole buffer when `since` is older than it (or missing, or ahead).
    private (int, object) WebTerminal(string? since)
    {
        long seq;
        string text;
        lock (_termOut)
        {
            seq = _termSeq;
            var start = seq - _termRing.Length;
            var from = long.TryParse(since, out var s) && s >= start && s <= seq ? s : start;
            text = _termRing.ToString((int)(from - start), (int)(seq - from));
        }
        return (200, new { seq, text, running = _pty is not null, cwd = TerminalFolder.Text });
    }

    // POST /api/terminal/input {data} and /api/terminal/new {restart?}
    private (int, object) WebTerminalPost(string action, JsonElement body)
    {
        if (WebPromptOpen())
            return (409, new { error = "DialogOpen" });
        if (action == "new")
        {
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("restart", out var r) && r.ValueKind == JsonValueKind.True)
                RestartTerminal(); // the panel's New
            else if (_pty is null)
                StartTerminal(); // in the folder the desktop picks; an open panel shows it, a later one replays the buffer
            long seq;
            lock (_termOut) seq = _termSeq;
            return (200, new { seq, running = _pty is not null, cwd = TerminalFolder.Text });
        }
        var data = WebField(body, "data");
        if (data.Length == 0)
            return (400, new { error = "Send data" });
        if (Encoding.UTF8.GetByteCount(data) > WebTermInputMax)
            return (413, new { error = "Send at most 64 KB at a time" });
        if (_pty is not { } pty)
            return (409, new { error = "No terminal — start one" });
        pty.Write(data);
        return (200, new { ok = true });
    }

    private bool BrowserShown => BrowserPanel.Visibility == Visibility.Visible; // IsVisible is false while the window is hidden

    // The phone's browsing: every /api/browser call stamps it; within the grace after the last one (the phone's Browser pane polls
    // every 3 s) a page's dialogs are cancelled, not shown on the PC, and file pickers stay intercepted. The PC's own browsing,
    // with no phone call within the grace, shows its dialogs as before.
    private const int PhoneBrowserGraceMs = 10_000;
    private long _phoneBrowserAt = long.MinValue / 2; // Environment.TickCount64 of the last call (UI thread)
    private bool PhoneBrowsing => Environment.TickCount64 - _phoneBrowserAt < PhoneBrowserGraceMs;
    private System.Windows.Threading.DispatcherTimer? _pickerWatch;

    // Page.setInterceptFileChooserDialog on a tab the phone gives input to, until the grace ends: a picker never opens on the PC.
    private async Task WebInterceptPickers(BrowserTab tab, CoreWebView2 core)
    {
        if (!tab.Intercepting)
        {
            await Cdp(core, "Page.enable", new { });
            await Cdp(core, "Page.setInterceptFileChooserDialog", new { enabled = true });
            tab.Intercepting = true;
        }
        _pickerWatch ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        {
            if (PhoneBrowsing)
                return;
            _pickerWatch!.Stop();
            foreach (var t in _tabs.Where(t => t.Intercepting))
            {
                t.Intercepting = false;
                if (t.Web.CoreWebView2 is { } c)
                    Forget(Cdp(c, "Page.setInterceptFileChooserDialog", new { enabled = false }));
            }
        }, Dispatcher);
        _pickerWatch.Start();
    }

    // GET /api/browser
    private (int, object) WebBrowser()
    {
        _phoneBrowserAt = Environment.TickCount64;
        return (200, new
        {
            open = BrowserShown,
            tabs = _tabs.Select((t, i) => new { index = i, title = TabTitle(t), url = t.Url, shown = t == _tab }).ToList(),
            follow = _settings.BrowserFollow,
            input = _settings.BrowserInput,
        });
    }

    // GET /api/browser/shot: the shown tab as the model's screenshot takes it (recording its size, so taps map), as JPEG bytes.
    private async Task<(int, object)> WebBrowserShot()
    {
        _phoneBrowserAt = Environment.TickCount64;
        if (_tab is not { } tab || tab.Web.CoreWebView2 is not { } core)
            return (404, new { error = "No tab" });
        try
        {
            var (image, _) = await CaptureAsync(tab, core);
            return image is null ? (504, new { error = "The screenshot did not finish within 15 s" })
                : (200, new WebServer.Response(200, "image/jpeg", Convert.FromBase64String(image[(image.IndexOf(',') + 1)..])));
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            return (409, new { error = $"The browser failed: {e.Message}" });
        }
    }

    // POST /api/browser/{action}: the desktop's own handlers; the page's input as CDP events within 15 s. Stamps the phone's
    // browsing as it starts and as it ends, so the grace runs from the end of a slow call.
    private async Task<(int, object)> WebBrowserAct(string action, JsonElement body)
    {
        _phoneBrowserAt = Environment.TickCount64;
        try { return await WebBrowserActNow(action, body); }
        finally { _phoneBrowserAt = Environment.TickCount64; }
    }

    private async Task<(int, object)> WebBrowserActNow(string action, JsonElement body)
    {
        if (WebPromptOpen())
            return (409, new { error = "DialogOpen" });
        int? Index() => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("index", out var v) && v.TryGetInt32(out var i) && i >= 0 && i < _tabs.Count ? i : null;
        bool? Flag(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        void Panel()
        {
            if (!BrowserShown)
                OpenBrowser(null, navigate: false); // the panel inside the window; no page is loaded for it
        }
        switch (action)
        {
            case "navigate":
                if (BrowserUrl(WebField(body, "url")) is not { } url)
                    return (400, new { error = "Send a web address or words to search for" });
                Panel();
                Navigate(url);
                return _tab is null ? (409, new { error = BrowserMessage.Text }) : WebBrowser();
            case "newtab":
                Panel();
                OpenTab(BrowserHome);
                return _tab is null ? (409, new { error = BrowserMessage.Text }) : WebBrowser();
            case "tab" or "close":
                if (Index() is not { } index)
                    return (404, new { error = "No such tab" });
                if (action == "tab")
                    SelectTab(_tabs[index]);
                else
                    CloseTab(_tabs[index]);
                return WebBrowser();
            case "options":
                var (follow, input) = (Flag("follow"), Flag("input"));
                if (follow is null && input is null)
                    return (400, new { error = "Send follow or input: true or false" });
                if (follow is { } f)
                    BrowserFollowToggle.IsChecked = _settings.BrowserFollow = f;
                if (input is { } on)
                    BrowserInputToggle.IsChecked = _settings.BrowserInput = on;
                _settings.Save();
                return WebBrowser();
        }
        if (_tab is not { } tab || tab.Web.CoreWebView2 is not { } core)
            return (404, new { error = "No tab" });
        switch (action)
        {
            case "back": tab.Web.GoBack(); return WebBrowser();
            case "forward": tab.Web.GoForward(); return WebBrowser();
            case "reload": tab.Web.Reload(); return WebBrowser();
        }
        Func<Task<(int, object)?>>? work = action switch
        {
            "tap" => () => WebTapAsync(tab, core, body),
            "type" => WebField(body, "text") is { Length: > 0 } text ? async () => { await Cdp(core, "Input.insertText", new { text }); return null; } : null,
            "key" => WebKeys(WebField(body, "key")) is { } specs ? async () => { foreach (var k in specs) await KeyAsync(core, k); return null; } : null,
            _ => null,
        };
        if (work is null)
            return action is "tap" or "type" or "key" ? (400, new { error = action == "key" ? "Unknown key" : "Send text" }) : WebNotFound;
        // as the user's input, so the page's alert or confirm is cancelled rather than shown on the PC (tab.Driving), but no badge
        tab.Driving = true;
        try
        {
            await WebInterceptPickers(tab, core);
            tab.Picker = false;
            var run = work();
            if (await Task.WhenAny(run, Task.Delay(15000)) != run)
            {
                Forget(run.ContinueWith(_ => { }, TaskScheduler.Default)); // its exception, if any, is not unobserved
                return (504, new { error = "The page did not answer within 15 s" });
            }
            if (await run is { } refused)
                return refused;
            await Task.Delay(300); // a dialog the input opened arrives now
            return tab.Picker ? (409, new { error = "That is a file picker: choose the file on the PC" }) : WebBrowser();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            return (409, new { error = $"The browser failed: {e.Message}" });
        }
        finally
        {
            tab.Driving = false;
        }
    }

    // The model's key table: "Enter", "Ctrl+A", "Tab Tab Enter" (one to 30 chords); null when one is unknown.
    private static List<KeySpec>? WebKeys(string keys)
    {
        var chords = BrowserInputLogic.Chords(keys);
        var specs = chords.Select(BrowserInputLogic.ParseKeys).ToList();
        return chords.Length is 0 or > 30 || specs.Any(s => s is null) ? null : specs!;
    }

    // tap {x, y}: a left click at the last shot's pixel, mapped to CSS pixels as the model's click is. A native list only takes focus
    // (its pop-up can't show) and a file picker is refused: it would be a dialog on the PC.
    private async Task<(int, object)?> WebTapAsync(BrowserTab tab, CoreWebView2 core, JsonElement body)
    {
        double? Coord(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        if (Coord("x") is not { } px || Coord("y") is not { } py)
            return (400, new { error = "Send x and y" });
        var view = await ViewAsync(core);
        if (tab.ShotW <= 0 || Math.Abs(view[0] - tab.ShotViewW) > 1 || Math.Abs(view[1] - tab.ShotViewH) > 1)
            return (409, new { error = "Take a new screenshot first" });
        if (px < 0 || py < 0 || px >= tab.ShotW || py >= tab.ShotH)
            return (400, new { error = $"({px}, {py}) is outside the screenshot ({tab.ShotW}x{tab.ShotH})" });
        var (x, y) = (BrowserInputLogic.ToCss(px, tab.ShotW, view[0]), BrowserInputLogic.ToCss(py, tab.ShotH, view[1]));
        await CdpMouse(core, "mouseMoved", x, y);
        var probe = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(ProbeScript(x, y))) ?? "";
        if (probe == "file")
            return (409, new { error = "That is a file picker: choose the file on the PC" });
        if (probe == "select")
            return null;
        await CdpMouse(core, "mousePressed", x, y, "left", 1, 1);
        await CdpMouse(core, "mouseReleased", x, y, "left", 0, 1);
        return null;
    }

    // The terminal and browser routes (WebAppSmoke, while on), on the smoke's data folder. The browser loads only this server's own page.
    private async Task<bool> WebToolsSmoke(int port)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"webapp-smoke: tools: {what}"); return false; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        async Task<(int Status, JsonNode? Body, string Type)> Send(string path, object? json = null)
        {
            using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
                { Content = json is null ? null : new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json") };
            request.Headers.Add("Tailscale-User-Login", "smoke@example.com");
            using var response = await http.SendAsync(request);
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type != "application/json")
                return ((int)response.StatusCode, null, type);
            var text = await response.Content.ReadAsStringAsync();
            try { return ((int)response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null, type); }
            catch (JsonException) { return ((int)response.StatusCode, null, type); }
        }
        var hadTerminal = _pty is not null;
        var (follow, input, open, url) = (_settings.BrowserFollow, _settings.BrowserInput, _settings.BrowserOpen, _settings.BrowserUrl);
        var tabsBefore = _tabs.ToList();
        var panelBefore = BrowserPanel.IsVisible; // the browser panel takes the rail's column: put it back as it was
        try
        {
            // the terminal
            if (!hadTerminal && (await Send("/api/terminal/input", new { data = "x" })).Status != 409)
                return Fail("input with no terminal is not 409");
            if (await Send("/api/terminal/new", new { }) is not { Status: 200, Body: { } made } || made["running"]?.GetValue<bool>() != true)
                return Fail("new: not running");
            if ((await Send("/api/terminal/input", new { data = "echo phone-smoke\r" })).Status != 200)
                return Fail("input");
            JsonNode? got = null;
            for (var i = 0; i < 40 && got?["text"]?.ToString().Contains("phone-smoke") != true; i++)
            {
                await Task.Delay(250);
                got = (await Send("/api/terminal?since=0")).Body;
            }
            if (got?["text"]?.ToString().Contains("phone-smoke") != true)
            {
                Console.Error.WriteLine("webapp-smoke: tools: PowerShell's output did not come back in this session; checking the buffer fed directly");
                await Dispatcher.InvokeAsync(() => TermAppend("phone-smoke\r\n"));
                got = (await Send("/api/terminal?since=0")).Body;
                if (got?["text"]?.ToString().Contains("phone-smoke") != true)
                    return Fail("the output since 0 has no phone-smoke");
            }
            var seq = got!["seq"]!.GetValue<long>();
            var rest = "?";
            for (var i = 0; i < 20 && rest != ""; i++) // PowerShell may still be drawing its prompt: read up to where it stops
            {
                var after = (await Send($"/api/terminal?since={seq}")).Body;
                (rest, seq) = (after?["text"]?.ToString() ?? "?", after?["seq"]?.GetValue<long>() ?? seq);
                if (rest != "")
                    await Task.Delay(250);
            }
            if (rest != "")
                return Fail("since=seq is not empty");
            if ((await Send("/api/terminal/input", new { data = new string('a', WebTermInputMax + 1) })).Status != 413)
                return Fail("a too-long input is not 413");

            // the browser
            if (await Send("/api/browser") is not { Status: 200, Body: { } b0 } || b0["tabs"] is not JsonArray || b0["follow"] is null || b0["input"] is null || b0["open"] is null)
                return Fail($"browser shape: {(await Send("/api/browser")).Body}");
            if (tabsBefore.Count == 0 && ((await Send("/api/browser/shot")).Status != 404 || (await Send("/api/browser/reload", new { })).Status != 404))
                return Fail("shot or reload with no tab is not 404");
            if ((await Send("/api/browser/navigate", new { url = "javascript:alert(1)" })).Status != 400)
                return Fail("a javascript: address is not 400");
            if (await Send("/api/browser/navigate", new { url = $"http://127.0.0.1:{port}/" }) is not { Status: 200, Body: { } nav } || nav["tabs"]!.AsArray().Count != Math.Max(tabsBefore.Count, 1)) // navigate loads in the shown tab, a new one only when none is open (--ui-smoke leaves tabs)
                return Fail($"navigate: {(await Send("/api/browser")).Body}");
            var index = nav["tabs"]!.AsArray().Select((t, i) => (t, i)).FirstOrDefault(x => x.t?["shown"]?.GetValue<bool>() == true).i;
            var shot = (Status: 0, Body: (JsonNode?)null, Type: "");
            for (var i = 0; i < 20 && shot.Status != 200; i++) // the engine starts and loads the page first
            {
                await Task.Delay(500);
                shot = await Send("/api/browser/shot");
            }
            if (shot.Status != 200 || shot.Type != "image/jpeg")
                Console.Error.WriteLine($"webapp-smoke: tools: no screenshot in this session ({shot.Status} {shot.Body}); tap is checked for its refusal only");
            var tapped = await Send("/api/browser/tap", new { x = 1, y = 1 });
            if (shot.Status == 200 ? tapped.Status != 200 : tapped.Status is not (409 or 404))
                return Fail($"tap: {tapped.Status} {tapped.Body}");
            if ((await Send("/api/browser/tap", new { x = 1e6, y = 1 })).Status != (shot.Status == 200 ? 400 : 409))
                return Fail("a tap outside the shot is not refused");
            var typed = await Send("/api/browser/type", new { text = "hi" });
            if (typed.Status != 200 || (await Send("/api/browser/key", new { key = "Tab Enter" })).Status != 200)
                return Fail($"type or key: {typed.Status} {typed.Body}");
            if ((await Send("/api/browser/key", new { key = "NoSuchKey" })).Status != 400)
                return Fail("an unknown key is not 400");
            // a page's alert on load (the phone's reload) and one a second after the phone's tap (its input over): cancelled and noted, never a message box on the PC
            if (_tabs[index].Web.CoreWebView2 is not { } core)
                Console.Error.WriteLine("webapp-smoke: tools: no browser engine in this session; the page's dialogs are not checked");
            else
            {
                var tab = _tabs[index];
                var shown = new List<string>();
                var messageBox = _messageBox;
                _messageBox = (text, _, _) => { shown.Add(text); return MessageBoxResult.Cancel; };
                var script = await core.AddScriptToExecuteOnDocumentCreatedAsync("alert('phone-smoke-load'); addEventListener('click', () => setTimeout(() => alert('phone-smoke-click'), 1000));");
                try
                {
                    tab.Dialog = "";
                    if ((await Send("/api/browser/reload", new { })).Status != 200)
                        return Fail("reload");
                    for (var i = 0; i < 40 && !tab.Dialog.Contains("phone-smoke-load") && shown.Count == 0; i++)
                        await Task.Delay(250);
                    if (shot.Status == 200)
                    {
                        if ((await Send("/api/browser/tap", new { x = 1, y = 1 })).Status != 200)
                            return Fail("tap after the reload");
                        for (var i = 0; i < 20 && !tab.Dialog.Contains("phone-smoke-click") && shown.Count == 0; i++)
                            await Task.Delay(250);
                    }
                    if (shown.Count != 0 || !tab.Dialog.Contains("phone-smoke-load") || shot.Status == 200 && !tab.Dialog.Contains("phone-smoke-click"))
                        return Fail($"the page's dialogs: shown on the PC [{string.Join(", ", shown)}], noted [{tab.Dialog.Trim()}]");
                }
                finally
                {
                    core.RemoveScriptToExecuteOnDocumentCreated(script);
                    _messageBox = messageBox;
                }
            }
            if (await Send("/api/browser/options", new { follow = !follow, input = !input }) is not { Status: 200, Body: { } opts } || opts["follow"]?.GetValue<bool>() != !follow ||
                AppSettings.Load() is var saved && (saved.BrowserFollow == follow || saved.BrowserInput == input) || (await Send("/api/browser/options", new { })).Status != 400)
                return Fail("options");
            if (await Send("/api/browser/close", new { index }) is not { Status: 200, Body: { } closed } || closed["tabs"]!.AsArray().Count != Math.Max(tabsBefore.Count, 1) - 1 ||
                (await Send("/api/browser/close", new { index = 99 })).Status != 404)
                return Fail("close");
            return true;
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!hadTerminal)
                    StopTerminal();
                foreach (var t in _tabs.Except(tabsBefore).ToList())
                    CloseTab(t);
                if (!panelBefore && BrowserPanel.IsVisible)
                    ShowWorkerRail(); // hides the browser panel, as its close button
                (_settings.BrowserFollow, _settings.BrowserInput, _settings.BrowserOpen, _settings.BrowserUrl) = (follow, input, open, url);
                (BrowserFollowToggle.IsChecked, BrowserInputToggle.IsChecked) = (follow, input);
                _settings.Save();
            });
        }
    }
}

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace AgentOrchestrator;

// The model's mouse and keyboard in the in-app browser (the engine's browser tool: click, move, drag, scroll, type, key).
// Everything goes to the tab through the DevTools protocol (Input.dispatchMouseEvent / dispatchKeyEvent), so the user's own
// mouse and keyboard are never used or blocked, and a tab that is not focused works too. x and y are pixels of the tab's
// last screenshot; they are scaled to the page's CSS pixels with the viewport size read when that screenshot was taken.
// A ring in the page marks each action (the airspace rule keeps WPF from drawing over a WebView2), and the browser
// panel's "Model input" switch turns all of it off.

/// <summary>One mouse/keyboard request from the engine. Null X/Y: not given (scroll then uses the middle; click uses Find).</summary>
internal sealed record BrowserInput(double? X, double? Y, double? ToX, double? ToY, double? Dx, double? Dy,
    string Button, int Clicks, string Text, string Keys, bool Shot);

/// <summary>A parsed key chord, in the shape Input.dispatchKeyEvent wants.</summary>
internal sealed record KeySpec(string Key, string Code, int VirtualKey, string Text, int Modifiers, string[] Commands);

internal static class BrowserInputLogic
{
    public const int Alt = 1, Ctrl = 2, Meta = 4, Shift = 8; // DevTools' modifier bits

    private static readonly Dictionary<string, (string Key, string Code, int Vk, string Text)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = ("Enter", "Enter", 13, "\r"), ["return"] = ("Enter", "Enter", 13, "\r"), ["tab"] = ("Tab", "Tab", 9, ""),
        ["escape"] = ("Escape", "Escape", 27, ""), ["esc"] = ("Escape", "Escape", 27, ""), ["backspace"] = ("Backspace", "Backspace", 8, ""),
        ["delete"] = ("Delete", "Delete", 46, ""), ["del"] = ("Delete", "Delete", 46, ""), ["space"] = (" ", "Space", 32, " "),
        ["arrowup"] = ("ArrowUp", "ArrowUp", 38, ""), ["up"] = ("ArrowUp", "ArrowUp", 38, ""),
        ["arrowdown"] = ("ArrowDown", "ArrowDown", 40, ""), ["down"] = ("ArrowDown", "ArrowDown", 40, ""),
        ["arrowleft"] = ("ArrowLeft", "ArrowLeft", 37, ""), ["left"] = ("ArrowLeft", "ArrowLeft", 37, ""),
        ["arrowright"] = ("ArrowRight", "ArrowRight", 39, ""), ["right"] = ("ArrowRight", "ArrowRight", 39, ""),
        ["home"] = ("Home", "Home", 36, ""), ["end"] = ("End", "End", 35, ""), ["pageup"] = ("PageUp", "PageUp", 33, ""),
        ["pagedown"] = ("PageDown", "PageDown", 34, ""), ["insert"] = ("Insert", "Insert", 45, ""),
    };

    private static readonly Dictionary<char, string> Edits = new() { ['a'] = "selectAll", ['c'] = "copy", ['x'] = "cut", ['v'] = "paste", ['z'] = "undo", ['y'] = "redo" };

    /// <summary>"Ctrl+Shift+A", "Enter", "F5", "Ctrl++" -> the key event; null when a key or modifier name is unknown.</summary>
    public static KeySpec? ParseKeys(string chord)
    {
        var s = (chord ?? "").Trim();
        if (s.Length == 0)
            return null;
        string main;
        string[] mods;
        if (s.EndsWith('+')) // the "+" key itself: "+", "Ctrl++"
            (main, mods) = ("+", s[..^1].TrimEnd('+') is { Length: > 0 } rest ? rest.Split('+') : Array.Empty<string>());
        else
        {
            var parts = s.Split('+');
            (main, mods) = (parts[^1].Trim(), parts[..^1]);
        }
        var bits = 0;
        foreach (var m in mods)
            switch (m.Trim().ToLowerInvariant())
            {
                case "ctrl" or "control": bits |= Ctrl; break;
                case "shift": bits |= Shift; break;
                case "alt" or "option": bits |= Alt; break;
                case "meta" or "cmd" or "command" or "win" or "windows" or "super": bits |= Meta; break;
                default: return null;
            }
        var shift = (bits & Shift) != 0;
        (string Key, string Code, int Vk, string Text) k;
        if (Named.TryGetValue(main, out var named))
            k = named;
        else if (main.Length >= 2 && main.Length <= 3 && (main[0] is 'F' or 'f') && int.TryParse(main[1..], out var f) && f is >= 1 and <= 12)
            k = ($"F{f}", $"F{f}", 111 + f, "");
        else if (main.Length == 1 && char.IsAsciiLetter(main[0]))
            k = (shift ? main.ToUpperInvariant() : main.ToLowerInvariant(), "Key" + char.ToUpperInvariant(main[0]), char.ToUpperInvariant(main[0]), shift ? main.ToUpperInvariant() : main.ToLowerInvariant());
        else if (main.Length == 1 && char.IsAsciiDigit(main[0]))
            k = (main, "Digit" + main, main[0], main);
        else if (main.Length == 1 && !char.IsControl(main[0]))
            k = (main, "", 0, main);
        else
            return null;
        var typing = (bits & (Ctrl | Alt | Meta)) == 0; // a shortcut types nothing
        string[] commands = (bits & (Ctrl | Meta)) != 0 && (bits & Alt) == 0 && main.Length == 1 && Edits.TryGetValue(char.ToLowerInvariant(main[0]), out var c)
            ? [shift && c == "undo" ? "redo" : c] : [];
        return new KeySpec(k.Key, k.Code, k.Vk, typing ? k.Text : "", bits, commands);
    }

    /// <summary>A screenshot pixel on one axis as a CSS pixel: `shotPx` is the image's size on that axis, `viewCss` the viewport's.</summary>
    public static double ToCss(double v, int shotPx, double viewCss) => shotPx <= 0 ? v : v * viewCss / shotPx;

    /// <summary>Copy, cut and paste use the user's own clipboard (a paste could send what they last copied to a page), so the model may not.</summary>
    public static bool UsesClipboard(KeySpec k) => k.Commands.Any(c => c is "copy" or "cut" or "paste") || (k.Key == "Insert" && (k.Modifiers & (Ctrl | Shift)) != 0);

    public static int ButtonMask(string button) => button switch { "right" => 2, "middle" => 4, _ => 1 };

    /// <summary>How many mouseMoved steps cover a distance, so hover and drag handlers see a path rather than a jump.</summary>
    public static int Steps(double distance) => Math.Clamp((int)(distance / 80), 1, 10);

    internal static void SelfTest()
    {
        var a = ParseKeys("Ctrl+Shift+A");
        if (a is not { Key: "A", Code: "KeyA", VirtualKey: 65, Text: "", Modifiers: Ctrl | Shift } || a.Commands.Length != 1 || a.Commands[0] != "selectAll")
            throw new InvalidOperationException("ParseKeys Ctrl+Shift+A");
        if (ParseKeys("Enter") is not { Key: "Enter", VirtualKey: 13, Text: "\r", Modifiers: 0 } ||
            ParseKeys("f5") is not { Key: "F5", VirtualKey: 116 } || ParseKeys("Shift+Tab") is not { Key: "Tab", Modifiers: Shift, Text: "" } ||
            ParseKeys("Ctrl++") is not { Key: "+", Modifiers: Ctrl } || ParseKeys("Ctrl+V")?.Commands is not ["paste"] ||
            ParseKeys("x") is not { Key: "x", Text: "x", Modifiers: 0 } || ParseKeys("Alt+ArrowLeft") is not { Key: "ArrowLeft", Modifiers: Alt } ||
            ParseKeys("7") is not { Code: "Digit7", VirtualKey: 55 })
            throw new InvalidOperationException("ParseKeys");
        if (!UsesClipboard(ParseKeys("Ctrl+V")!) || !UsesClipboard(ParseKeys("Meta+C")!) || !UsesClipboard(ParseKeys("Shift+Insert")!) ||
            UsesClipboard(ParseKeys("Ctrl+A")!) || UsesClipboard(ParseKeys("Ctrl+Z")!) || UsesClipboard(ParseKeys("v")!))
            throw new InvalidOperationException("UsesClipboard");
        if (ParseKeys("") is not null || ParseKeys("Hyper+A") is not null || ParseKeys("Ctrl+Nope") is not null || ParseKeys("F13") is not null)
            throw new InvalidOperationException("ParseKeys refuses");
        // a 1200-pixel-wide screenshot of a 800-CSS-pixel viewport (a 150% display): pixel 600 is CSS pixel 400
        if (Math.Abs(ToCss(600, 1200, 800) - 400) > 1e-9 || ToCss(5, 0, 800) != 5 || ButtonMask("left") != 1 || ButtonMask("right") != 2 ||
            ButtonMask("middle") != 4 || Steps(10) != 1 || Steps(400) != 5 || Steps(5000) != 10)
            throw new InvalidOperationException("coordinate scale");
    }
}

public partial class MainWindow
{
    private DispatcherTimer? _drivingTimer;

    internal struct CursorPoint { public int X, Y; }

    // The user's own pointer, for --ui-smoke: the model's mouse must not move it.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out CursorPoint point);

    private void BrowserInputToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.BrowserInput = BrowserInputToggle.IsChecked == true;
        _settings.Save();
    }

    // The "model is driving" badge in the browser panel's header while an action runs, and a moment after.
    private void ShowDriving()
    {
        BrowserDriving.Visibility = Visibility.Visible;
        _drivingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _drivingTimer.Tick -= HideDriving;
        _drivingTimer.Tick += HideDriving;
        _drivingTimer.Stop();
        _drivingTimer.Start();
    }

    private void HideDriving(object? sender, EventArgs e)
    {
        BrowserDriving.Visibility = Visibility.Collapsed;
        _drivingTimer?.Stop();
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static Task Cdp(CoreWebView2 core, string method, object args) => core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(args));

    private static Task CdpMouse(CoreWebView2 core, string type, double x, double y, string button = "none", int buttons = 0, int clicks = 0) =>
        Cdp(core, "Input.dispatchMouseEvent", new { type, x, y, button, buttons, clickCount = clicks, pointerType = "mouse" });

    // [innerWidth, innerHeight, scrollX, scrollY] of the tab's page.
    private static async Task<double[]> ViewAsync(CoreWebView2 core) =>
        JsonSerializer.Deserialize<double[]>(await core.ExecuteScriptAsync("[innerWidth, innerHeight, Math.round(scrollX), Math.round(scrollY)]")) ?? [0, 0, 0, 0];

    // The tab's screenshot as a JPEG data URL, and the note that tells the model its coordinate frame. Image null: it timed out.
    private async Task<(string? Image, string Note)> CaptureAsync(BrowserTab tab, CoreWebView2 core)
    {
        using var shot = new MemoryStream();
        var capture = core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, shot);
        if (await Task.WhenAny(capture, Task.Delay(15000)) != capture) // the engine waits for this answer
            return (null, "");
        await capture;
        var bytes = shot.ToArray();
        var view = await ViewAsync(core);
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            (tab.ShotW, tab.ShotH, tab.ShotViewW, tab.ShotViewH) = (frame.PixelWidth, frame.PixelHeight, view[0], view[1]);
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            tab.ShotW = tab.ShotH = 0;
        }
        var note = tab.ShotW > 0
            ? $"IMAGE: {tab.ShotW}x{tab.ShotH} px; x and y for click, move, drag and scroll are pixels of this image\nVIEWPORT: {Num(view[0])} x {Num(view[1])} CSS px, scrolled to {Num(view[2])},{Num(view[3])}"
            : "";
        return ("data:image/jpeg;base64," + Convert.ToBase64String(bytes), note);
    }

    // click / move / drag / scroll / type / key in tab `number` (0: the shown one). Returns what changed and, when asked, a screenshot.
    // The engine waits for this answer, so it always comes: a page that blocks (a dialog the cancel of which does not reach it, a busy page) ends in an error.
    private async Task<(string Result, string? Image)> DriveAsync(string action, int number, string find, BrowserInput input)
    {
        var work = DriveCoreAsync(action, number, find, input);
        if (await Task.WhenAny(work, Task.Delay(45000)) == work)
            return await work;
        Forget(work.ContinueWith(_ => { }, TaskScheduler.Default)); // its exception, if any, is not unobserved
        return ("ERROR: the page did not answer within 45 s (busy, or blocked by a dialog); take a screenshot to see where it is", null);
    }

    private async Task<(string Result, string? Image)> DriveCoreAsync(string action, int number, string find, BrowserInput input)
    {
        if (!_settings.BrowserInput)
            return ("ERROR: the user switched off the model's mouse and keyboard (the Model input switch in the browser panel); ask them to turn it on, or use read", null);
        if (!BrowserPanel.IsVisible)
            OpenBrowser(null);
        if (number > _tabs.Count || number < 0)
            return ($"ERROR: there is no tab {number}; the open tabs:\n{TabList()}", null);
        if (number > 0)
            SelectTab(_tabs[number - 1]);
        if (_tab is not { } tab || tab.Web.CoreWebView2 is not { } core)
            return ("ERROR: no page is open in the browser; use open first", null);
        ShowDriving();
        tab.Driving = true; // the page's context menu (a right click) is not shown while the model acts
        tab.Dialog = "";
        try
        {
            var tabsBefore = _tabs.Count;
            string done;
            switch (action)
            {
                case "type":
                    await TypeAsync(core, input.Text);
                    done = $"TYPED {input.Text.Length} character{(input.Text.Length == 1 ? "" : "s")}";
                    break;
                case "key":
                    if (BrowserInputLogic.ParseKeys(input.Keys) is not { } spec)
                        return ($"ERROR: unknown key \"{input.Keys}\"; use Enter, Tab, Escape, Backspace, Delete, ArrowUp/Down/Left/Right, Home, End, PageUp, PageDown, F1-F12, a letter or digit, with Ctrl+, Shift+, Alt+ or Meta+", null);
                    if (BrowserInputLogic.UsesClipboard(spec))
                        return ("ERROR: copy, cut and paste use the user's own clipboard, which the model may not touch; type the text instead", null);
                    await KeyAsync(core, spec);
                    done = $"PRESSED {input.Keys}";
                    break;
                default:
                    if (await PointAsync(tab, core, action, find, input) is { } failed)
                        return (failed, null);
                    done = tab.Done;
                    break;
            }
            await Task.Delay(350); // the page reacts (and a click may start a navigation: wait for it)
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    if (await core.ExecuteScriptAsync("document.readyState") == "\"complete\"")
                        break;
                }
                catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // the page is being replaced
                }
                await Task.Delay(250);
            }
            var shown = _tab ?? tab; // a click that opens a pop-up shows the new tab
            var focus = "nothing";
            if (shown.Web.CoreWebView2 is { } now)
            {
                try
                {
                    focus = JsonSerializer.Deserialize<string>(await now.ExecuteScriptAsync(FocusScript)) ?? "nothing";
                }
                catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException) { }
                var result = $"{done}\nTAB: {_tabs.IndexOf(shown) + 1} of {_tabs.Count}{(_tabs.Count > tabsBefore ? " (a new tab opened and is shown)" : "")}\nURL: {now.Source}\nTITLE: {now.DocumentTitle}\nFOCUSED: {focus}" +
                             (tab.Dialog.Length > 0 ? "\nDIALOG: " + tab.Dialog.TrimEnd() : "");
                if (!input.Shot)
                    return (result, null);
                var (image, note) = await CaptureAsync(shown, now);
                return image is null ? (result + "\n(the screenshot did not finish within 15 s; take one with screenshot)", null) : ($"{result}\n{note}", image);
            }
            return ($"{done}\nthe page is gone", null);
        }
        finally
        {
            await Task.Delay(300); // a context menu event can arrive just after the click
            tab.Driving = false;
            try
            {
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException) { } // the tab closed
            ShowDriving(); // the badge stays up for a moment after the last step, not 2.5 s from the first
        }
    }

    private const string FocusScript = """
        (() => { const a = document.activeElement; if (!a || a === document.body) return 'nothing';
                 const n = a.name || a.id || a.getAttribute('aria-label') || a.placeholder || '';
                 return a.tagName.toLowerCase() + (a.type ? '[type=' + a.type + ']' : '') + (n ? ' "' + n + '"' : ''); })()
        """;

    // The centre (CSS px) of the first shown text, label or placeholder containing the text, scrolled into view; null when none.
    private static string ClickTargetScript(string find) =>
        $$"""
        (() => { const q = {{JsonSerializer.Serialize(find.ToLowerInvariant())}}; const shown = e => e.getClientRects().length > 0;
                 const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                 while (w.nextNode()) {
                     const n = w.currentNode, p = n.parentElement;
                     if (!p || p.closest("script, style, noscript, template") || !shown(p)) continue;
                     const i = n.nodeValue.toLowerCase().indexOf(q); if (i < 0) continue;
                     p.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
                     const r = document.createRange(); r.setStart(n, i); r.setEnd(n, i + q.length);
                     const b = r.getBoundingClientRect(); if (!b.width && !b.height) continue;
                     return [b.left + b.width / 2, b.top + b.height / 2];
                 }
                 for (const e of document.querySelectorAll("[aria-label], [title], [placeholder], input[value]")) {
                     if (!shown(e)) continue;
                     if ((e.getAttribute("aria-label") || e.title || e.placeholder || e.value || "").toLowerCase().includes(q)) {
                         e.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
                         const b = e.getBoundingClientRect(); return [b.left + b.width / 2, b.top + b.height / 2];
                     }
                 }
                 return null; })()
        """;

    // What a native control under the point needs instead of a click: "select" (its list is a native pop-up), "file" (a picker, which would block the app;
    // a page script that opens one from another element is not caught).
    private static string ProbeScript(double x, double y) =>
        $$"""
        (() => { const e = document.elementFromPoint({{Num(x)}}, {{Num(y)}}); if (!e) return ''; const s = e.closest('select');
                 if (s && !s.multiple && s.size <= 1) { s.focus(); return 'select'; }
                 const l = e.closest('label'); // a styled upload button is a label for a file input
                 return e.matches('input[type=file]') || (l && l.control && l.control.matches('input[type=file]')) ? 'file' : ''; })()
        """;

    private static string CursorScript(double x, double y, bool click) =>
        $$"""
        (() => { let d = document.getElementById('__ao_cursor');
                 if (!d) { d = document.createElement('div'); d.id = '__ao_cursor'; const s = d.style;
                     s.position = 'fixed'; s.zIndex = '2147483647'; s.pointerEvents = 'none'; s.width = s.height = '22px'; s.margin = '-11px 0 0 -11px';
                     s.boxSizing = 'border-box'; s.border = '3px solid #e5484d'; s.borderRadius = '50%'; s.background = 'rgba(229,72,77,.25)';
                     s.transition = 'left .12s, top .12s, transform .15s'; document.documentElement.appendChild(d); }
                 d.style.left = '{{Num(x)}}px'; d.style.top = '{{Num(y)}}px'; d.style.transform = {{(click ? "'scale(1.6)'" : "'scale(1)'")}};
                 if ({{(click ? "true" : "false")}}) setTimeout(() => { d.style.transform = 'scale(1)'; }, 160);
                 clearTimeout(window.__aoCursor); window.__aoCursor = setTimeout(() => d.remove(), 2500); })()
        """;

    // The ring where the model's pointer is; a page that rejects scripts (or is mid-navigation) just has none.
    private static async Task Ring(CoreWebView2 core, double x, double y, bool click)
    {
        try
        {
            await core.ExecuteScriptAsync(CursorScript(x, y, click));
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    // click / move / drag / scroll: the point (screenshot pixels, or a text to find) in CSS pixels, then the events. Null: done (tab.Done says what);
    // otherwise the error text.
    private async Task<string?> PointAsync(BrowserTab tab, CoreWebView2 core, string action, string find, BrowserInput input)
    {
        double x, y;
        double scaleX = 1, scaleY = 1;
        var view = await ViewAsync(core);
        var framed = input.X is not null && input.Y is not null;
        var fits = tab.ShotW > 0 && view[0] > 0 && view[1] > 0 && Math.Abs(view[0] - tab.ShotViewW) <= 1 && Math.Abs(view[1] - tab.ShotViewH) <= 1; // the last screenshot still matches the page's size
        if ((framed || action == "drag") && !fits)
            return tab.ShotW <= 0 ? "ERROR: take a screenshot first; x and y are pixels of the last screenshot"
                : "ERROR: the browser changed size since the last screenshot; take a new screenshot";
        if (fits) // scroll distances count in screenshot pixels too
            (scaleX, scaleY) = (BrowserInputLogic.ToCss(1, tab.ShotW, view[0]), BrowserInputLogic.ToCss(1, tab.ShotH, view[1]));
        if (framed)
        {
            if (input.X < 0 || input.Y < 0 || input.X >= tab.ShotW || input.Y >= tab.ShotH)
                return $"ERROR: ({Num(input.X!.Value)}, {Num(input.Y!.Value)}) is outside the last screenshot ({tab.ShotW}x{tab.ShotH})";
            (x, y) = (input.X!.Value * scaleX, input.Y!.Value * scaleY);
        }
        else if (action == "click")
        {
            var json = await core.ExecuteScriptAsync(ClickTargetScript(find));
            if (JsonSerializer.Deserialize<double[]?>(json) is not { Length: 2 } at)
                return $"ERROR: no shown text, label or placeholder contains \"{find}\"";
            (x, y) = (at[0], at[1]);
        }
        else
            (x, y) = (view[0] / 2, view[1] / 2); // a scroll without a point: the middle of the page
        var (fromX, fromY) = tab.HasMouse ? (tab.MouseX, tab.MouseY) : (x, y);
        switch (action)
        {
            case "move":
                await MoveAsync(core, tab, fromX, fromY, x, y);
                tab.Done = $"MOVED the pointer to ({Num(x / scaleX)}, {Num(y / scaleY)}) of the screenshot";
                break;
            case "click":
                await MoveAsync(core, tab, fromX, fromY, x, y);
                var probe = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(ProbeScript(x, y))) ?? "";
                if (probe == "select")
                {
                    tab.Done = "FOCUSED a <select>: its list is a native pop-up the browser cannot show; change it with key ArrowDown / ArrowUp (or type the option's first letters)";
                    break;
                }
                if (probe == "file")
                    return "ERROR: that is a file picker, which the model cannot use; ask the user to choose the file";
                var mask = BrowserInputLogic.ButtonMask(input.Button);
                for (var n = 1; n <= Math.Clamp(input.Clicks, 1, 3); n++)
                {
                    await Ring(core, x, y, click: true);
                    await CdpMouse(core, "mousePressed", x, y, input.Button, mask, n);
                    await CdpMouse(core, "mouseReleased", x, y, input.Button, 0, n); // ponytail: a failed press leaves no button down; a failed release means the tab is gone
                }
                tab.Done = $"{(input.Clicks > 1 ? input.Clicks == 2 ? "DOUBLE-" : "TRIPLE-" : "")}{(input.Button == "left" ? "CLICKED" : input.Button.ToUpperInvariant() + "-CLICKED")} at ({Num(x / scaleX)}, {Num(y / scaleY)}) of the screenshot";
                break;
            case "drag":
                var toX = (input.ToX ?? 0) * scaleX;
                var toY = (input.ToY ?? 0) * scaleY;
                if (input.ToX < 0 || input.ToY < 0 || input.ToX >= tab.ShotW || input.ToY >= tab.ShotH)
                    return $"ERROR: the drop point ({Num(input.ToX ?? 0)}, {Num(input.ToY ?? 0)}) is outside the last screenshot ({tab.ShotW}x{tab.ShotH})";
                await DragAsync(core, tab, fromX, fromY, x, y, toX, toY);
                tab.Done = $"DRAGGED from ({Num(x / scaleX)}, {Num(y / scaleY)}) to ({Num(toX / scaleX)}, {Num(toY / scaleY)}) of the screenshot";
                break;
            case "scroll":
                await MoveAsync(core, tab, fromX, fromY, x, y);
                await Cdp(core, "Input.dispatchMouseEvent", new { type = "mouseWheel", x, y, deltaX = (input.Dx ?? 0) * scaleX, deltaY = (input.Dy ?? 0) * scaleY, pointerType = "mouse" });
                await Task.Delay(300); // smooth scrolling
                tab.Done = $"SCROLLED by ({Num(input.Dx ?? 0)}, {Num(input.Dy ?? 0)}) at ({Num(x / scaleX)}, {Num(y / scaleY)}) of the screenshot";
                break;
        }
        return null;
    }

    private static async Task MoveAsync(CoreWebView2 core, BrowserTab tab, double fromX, double fromY, double x, double y, int buttons = 0, string button = "none")
    {
        var steps = BrowserInputLogic.Steps(Math.Sqrt((x - fromX) * (x - fromX) + (y - fromY) * (y - fromY)));
        for (var i = 1; i <= steps; i++)
            await CdpMouse(core, "mouseMoved", fromX + (x - fromX) * i / steps, fromY + (y - fromY) * i / steps, button, buttons);
        (tab.MouseX, tab.MouseY, tab.HasMouse) = (x, y, true);
        await Ring(core, x, y, click: false);
    }

    // Press at (x, y), move to (toX, toY), release. An element that is draggable (HTML5 drag and drop) would start the system's drag
    // loop, so drags are intercepted and delivered as drag events, the way browser automation does it.
    private async Task DragAsync(CoreWebView2 core, BrowserTab tab, double fromX, double fromY, double x, double y, double toX, double toY)
    {
        string? data = null;
        var pressed = false;
        var receiver = core.GetDevToolsProtocolEventReceiver("Input.dragIntercepted");
        void Intercepted(object? s, CoreWebView2DevToolsProtocolEventReceivedEventArgs a) => data = a.ParameterObjectAsJson;
        receiver.DevToolsProtocolEventReceived += Intercepted;
        await Cdp(core, "Input.setInterceptDrags", new { enabled = true });
        try
        {
            await MoveAsync(core, tab, fromX, fromY, x, y);
            await CdpMouse(core, "mousePressed", x, y, "left", 1, 1);
            pressed = true;
            await MoveAsync(core, tab, x, y, toX, toY, buttons: 1, button: "left");
            if (data is not null)
            {
                var item = JsonDocument.Parse(data).RootElement.GetProperty("data");
                foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
                    await Cdp(core, "Input.dispatchDragEvent", new { type, x = toX, y = toY, data = item });
            }
        }
        finally
        {
            if (pressed) // the button is released even when a step above failed, so the page is not left holding a drag
                await CdpMouse(core, "mouseReleased", toX, toY, "left", 0, 1);
            receiver.DevToolsProtocolEventReceived -= Intercepted;
            await Cdp(core, "Input.setInterceptDrags", new { enabled = false });
        }
    }

    // Typing: each ASCII character as a key press (the page sees keydown, keypress and input, as it would from a person); a line break
    // is Enter; anything else, and a long text, is inserted in one piece.
    private static async Task TypeAsync(CoreWebView2 core, string text)
    {
        if (text.Length > 400)
        {
            await Cdp(core, "Input.insertText", new { text });
            return;
        }
        foreach (var rune in text.Replace("\r\n", "\n").EnumerateRunes()) // by character, so an emoji's two UTF-16 halves stay together
        {
            var c = rune.IsBmp ? (char)rune.Value : '\0';
            if (c == '\n')
                await KeyAsync(core, BrowserInputLogic.ParseKeys("Enter")!);
            else if (c == '\t')
                await KeyAsync(core, BrowserInputLogic.ParseKeys("Tab")!);
            else if (c >= ' ' && c < 127 && BrowserInputLogic.ParseKeys(c == ' ' ? "Space" : c.ToString()) is { } spec)
                await KeyAsync(core, spec with { Key = c.ToString(), Text = c.ToString() });
            else
                await Cdp(core, "Input.insertText", new { text = rune.ToString() });
        }
    }

    private static async Task KeyAsync(CoreWebView2 core, KeySpec k)
    {
        await Cdp(core, "Input.dispatchKeyEvent", new Dictionary<string, object>
        {
            ["type"] = k.Text.Length > 0 ? "keyDown" : "rawKeyDown", ["key"] = k.Key, ["code"] = k.Code, ["windowsVirtualKeyCode"] = k.VirtualKey,
            ["nativeVirtualKeyCode"] = k.VirtualKey, ["modifiers"] = k.Modifiers, ["text"] = k.Text, ["unmodifiedText"] = k.Text, ["commands"] = k.Commands,
        });
        await Cdp(core, "Input.dispatchKeyEvent", new Dictionary<string, object>
        {
            ["type"] = "keyUp", ["key"] = k.Key, ["code"] = k.Code, ["windowsVirtualKeyCode"] = k.VirtualKey, ["nativeVirtualKeyCode"] = k.VirtualKey, ["modifiers"] = k.Modifiers,
        });
    }
}

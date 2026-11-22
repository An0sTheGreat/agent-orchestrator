using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentOrchestrator;

// The terminal: PowerShell in a Windows pseudo console (ConPty.cs), drawn by xterm.js in a WebView2, in the right column in
// place of the worker rail (as the browser and the editor are). It starts in the chat's project folder (else the user's
// folder) the first time it opens and keeps running while hidden; New starts another in the folder of the chat shown now.
public partial class MainWindow
{
    private WebView2? _termWeb;
    private ConPty? _pty;
    private short _termCols = 100, _termRows = 30;
    private readonly StringBuilder _termOut = new(); // output waiting for the next frame (filled on the reader thread)
    private DispatcherTimer? _termFlush;

    private void Terminal_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalPanel.IsVisible)
            ShowWorkerRail();
        else
            OpenTerminal();
    }

    private void TerminalClose_Click(object sender, RoutedEventArgs e) => ShowWorkerRail();

    private void TerminalNew_Click(object sender, RoutedEventArgs e)
    {
        StopTerminal();
        if (_termWeb?.CoreWebView2 is { } core)
        {
            core.PostWebMessageAsJson("""{"t":"reset"}""");
            StartTerminal();
        }
    }

    private async void OpenTerminal()
    {
        ShowColumnPage(TerminalPanel);
        if (_termWeb is not null)
        {
            _termWeb.Focus();
            return;
        }
        try
        {
            var web = _termWeb = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(AppSettings.DataRoot, "browser") } };
            TerminalHost.Children.Add(web);
            await web.EnsureCoreWebView2Async();
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.WebMessageReceived += (_, a) => TerminalMessageIn(a.WebMessageAsJson);
            web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            web.NavigateToString(TerminalPage(Resource("xterm.css"), Resource("xterm.js"), Resource("addon-fit.js"), TerminalTheme()));
        }
        catch (Exception x) when (x is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
            TerminalMessage.Text = $"The terminal could not start: {x.Message}";
            TerminalMessage.Visibility = Visibility.Visible;
            _termWeb = null;
        }
    }

    private static string Resource(string name)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(name)!;
        return new StreamReader(stream).ReadToEnd();
    }

    // The page: xterm.js and its fit add-on inline (no files or network), the terminal filling it. It posts keys
    // ({t:"i"}), its size ({t:"r"}) and once ready ({t:"ready"}); it takes output ({t:"o"}), a theme and a reset.
    internal static string TerminalPage(string css, string js, string fit, string theme) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>{{css}}
        html,body{margin:0;height:100%;overflow:hidden;background:transparent}#t{height:100%}
        .xterm-viewport::-webkit-scrollbar{width:8px}.xterm-viewport::-webkit-scrollbar-thumb{background:rgba(128,128,128,.35);border-radius:4px}
        </style></head><body><div id="t"></div>
        <script>{{js}}</script><script>{{fit}}</script><script>
        const term = new Terminal({fontFamily: "'Cascadia Mono', 'JetBrains Mono', Consolas, monospace", fontSize: 13, cursorBlink: true,
          scrollback: 5000, allowTransparency: true, theme: {{theme}}});
        const fit = new FitAddon.FitAddon();
        term.loadAddon(fit);
        term.open(document.getElementById('t'));
        const post = m => chrome.webview.postMessage(m);
        // Ctrl+C copies when text is selected (else it interrupts, as in any terminal); Ctrl+V pastes
        term.attachCustomKeyEventHandler(e => {
          if (e.type === 'keydown' && e.ctrlKey && e.key === 'c' && term.hasSelection()) { navigator.clipboard.writeText(term.getSelection()); term.clearSelection(); return false; }
          if (e.type === 'keydown' && e.ctrlKey && e.key === 'v') { navigator.clipboard.readText().then(t => term.paste(t)); return false; }
          return true;
        });
        term.element.addEventListener('contextmenu', e => { e.preventDefault(); navigator.clipboard.readText().then(t => term.paste(t)); });
        term.onData(d => post({t: 'i', d}));
        term.onResize(s => post({t: 'r', cols: s.cols, rows: s.rows}));
        new ResizeObserver(() => { try { fit.fit(); } catch {} }).observe(document.body);
        chrome.webview.addEventListener('message', e => {
          const m = e.data;
          if (m.t === 'o') term.write(m.d);
          else if (m.t === 'theme') term.options.theme = m.theme;
          else if (m.t === 'reset') term.reset();
        });
        fit.fit();
        post({t: 'ready', cols: term.cols, rows: term.rows});
        term.focus();
        </script></body></html>
        """;

    // The theme's colours for xterm.js: the card as the background, the text colour, the accent for the cursor.
    private string TerminalTheme()
    {
        string Hex(string key, string fallback) => TryFindResource(key) is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : fallback;
        var accent = Hex("Accent", "#7C8CFF");
        return JsonSerializer.Serialize(new { background = "#00000000", foreground = Hex("TextPrimary", "#E6E6E6"), cursor = accent, cursorAccent = Hex("Card", "#202020"),
            selectionBackground = accent + "55" });
    }

    // After a theme change.
    private void RefreshTerminalTheme() => _termWeb?.CoreWebView2?.PostWebMessageAsJson($$"""{"t":"theme","theme":{{TerminalTheme()}}}""");

    private void TerminalMessageIn(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "i":
                    _pty?.Write(m.GetProperty("d").GetString() ?? "");
                    break;
                case "r":
                    (_termCols, _termRows) = ((short)m.GetProperty("cols").GetInt32(), (short)m.GetProperty("rows").GetInt32());
                    _pty?.Resize(_termCols, _termRows);
                    break;
                case "ready":
                    (_termCols, _termRows) = ((short)m.GetProperty("cols").GetInt32(), (short)m.GetProperty("rows").GetInt32());
                    if (_pty is null)
                        StartTerminal();
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    // PowerShell in the chat's project folder (a usable one), else the user's folder.
    private void StartTerminal()
    {
        var project = SelectedProjectPath();
        var folder = LauncherLogic.FolderUsable(project) ? project : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        TerminalFolder.Text = folder;
        TerminalFolder.ToolTip = folder;
        try
        {
            var pty = _pty = new ConPty("powershell.exe -NoLogo", folder, _termCols, _termRows);
            pty.Output += text => { lock (_termOut) _termOut.Append(text); };
            pty.Exited += () => Dispatcher.InvokeAsync(() =>
            {
                if (_pty != pty)
                    return;
                _pty = null;
                lock (_termOut) _termOut.Append("\r\n\x1b[90m[PowerShell ended: press New to start another]\x1b[0m\r\n");
            });
            pty.Start();
        }
        catch (Exception x) when (x is System.ComponentModel.Win32Exception or IOException)
        {
            lock (_termOut) _termOut.Append($"\x1b[31mPowerShell could not start: {x.Message}\x1b[0m\r\n");
        }
        // output goes to the page at most once a frame, in one message
        _termFlush ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) =>
        {
            string text;
            lock (_termOut)
            {
                if (_termOut.Length == 0)
                    return;
                text = _termOut.ToString();
                _termOut.Clear();
            }
            _termWeb?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "o", d = text }));
        }, Dispatcher);
        _termFlush.Start();
    }

    // Ends the PowerShell (the app closing, or New).
    private void StopTerminal()
    {
        var pty = _pty;
        _pty = null;
        pty?.Dispose();
    }

    // --self-test: a program in the pseudo console runs in the given folder and its output comes back.
    internal static void TerminalSelfTest()
    {
        var folder = Path.GetTempPath();
        var got = new StringBuilder();
        using var done = new ManualResetEventSlim();
        using (var pty = new ConPty("cmd.exe /c cd & echo terminal-ok", folder, 120, 30))
        {
            pty.Output += text => { lock (got) got.Append(text); if (got.ToString().Contains("terminal-ok")) done.Set(); };
            pty.Start();
            done.Wait(TimeSpan.FromSeconds(10));
        }
        string text;
        lock (got) text = got.ToString();
        if (!text.Contains("terminal-ok") || !text.Contains(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Terminal: a pseudo console's output did not come back: '{text}'");
        // the editor colours its text as a reply's code block: the same pieces at the same offsets, a block comment across lines whole
        const string code = "def f():\n    /* a\n b */ return \"s\"  # note\n";
        var spans = CodeColorizer.Spans(code, "py");
        var pieces = Markdown.Highlight(code, "py");
        var expected = new List<(int, int, string)>();
        var at = 0;
        foreach (var (piece, key) in pieces)
        {
            if (key is not null && piece.Length > 0)
                expected.Add((at, at + piece.Length, key));
            at += piece.Length;
        }
        if (at != code.Length || spans.Count == 0 || !spans.SequenceEqual(expected) || spans.Any(s => code[s.Start..s.End] != pieces.First(p => p.Color == s.Key && code.IndexOf(p.Text, s.Start, StringComparison.Ordinal) == s.Start).Text))
            throw new InvalidOperationException("Editor: its colours must be the code block's pieces at their offsets");
        var page = TerminalPage("CSS", "JS", "FIT", """{"background":"#000"}""");
        if (!page.Contains("<style>CSS") || !page.Contains("<script>JS</script><script>FIT</script>") || !page.Contains("""theme: {"background":"#000"}"""))
            throw new InvalidOperationException("Terminal: the page must carry xterm.js, its fit add-on and the theme inline");
    }
}

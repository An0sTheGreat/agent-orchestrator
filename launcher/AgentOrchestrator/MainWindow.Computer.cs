using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrchestrator;

// Computer use (the engine's computer tool; Options > General > Computer use, off by default): the Code lead drives one
// window of an application the user approved, with the real mouse and keyboard (SendInput). One window per reply: the
// first the model opens. x and y are pixels of that window's last screenshot. Esc pressed by the user (a low-level keyboard
// hook; the model's own key presses are injected and ignored) stops it and the reply. A banner at the top of the screen,
// left out of screenshots, says the model is driving. Unlike the browser tool, the user's own pointer and focus move.

/// <summary>The rules of computer use that need no window: names, coordinates and which keys go how.</summary>
internal static class ComputerLogic
{
    /// <summary>An approved program's name for the model and the Options list: its description, else its file name.</summary>
    public static string AppName(string exe)
    {
        try
        {
            if (FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim() is { Length: > 0 } description)
                return description;
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException or IOException) { }
        return Path.GetFileNameWithoutExtension(exe);
    }

    /// <summary>Whether `name` (from the model) names the program: its description or its file name, any case.</summary>
    public static bool Names(string exe, string name) =>
        name.Trim() is { Length: > 0 } n && (AppName(exe).Equals(n, StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileNameWithoutExtension(exe).Equals(n, StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetFileName(exe).Equals(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a program may be approved: not this app (the model would change its own Options), not the frame host of
    /// Store apps (approving it would approve every one of them).</summary>
    public static bool Approvable(string exe, string? self) =>
        !Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) &&
        !(self is not null && Path.GetFullPath(exe).Equals(Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase));

    /// <summary>A screenshot pixel as a screen pixel: `origin` and `size` are the window's on that axis when the shot was taken, `image` the shot's.</summary>
    public static int ToScreen(double v, int origin, int size, int image) => origin + (int)Math.Floor(image <= 0 ? v : v * size / image);

    /// <summary>The longest side of a screenshot, in pixels (larger windows are scaled down).</summary>
    public const int MaxShot = 1600;

    /// <summary>Why a key may not be pressed in the window, or null: the Windows key and Alt+Tab act on the desktop, not the
    /// window; copy, cut and paste use the user's own clipboard.</summary>
    public static string? Refusal(KeySpec k)
    {
        if ((k.Modifiers & BrowserInputLogic.Meta) != 0)
            return "the Windows key acts on the desktop, not the window, so the model may not press it";
        if ((k.Modifiers & (BrowserInputLogic.Alt | BrowserInputLogic.Ctrl)) != 0 && k.Key is "Tab" or "Escape")
            return "that switches away from the window, which the model may not leave";
        if (k.VirtualKey is 44 or >= 166 and <= 183) // PrintScreen, browser, launch, volume and media keys
            return "that key acts outside the window (it starts or controls other applications)";
        if (BrowserInputLogic.UsesClipboard(k))
            return "copy, cut and paste use the user's own clipboard, which the model may not touch; type the text instead";
        return null;
    }

    /// <summary>A key that types its character (sent as that character, so any keyboard layout gives it) rather than a key press.</summary>
    public static bool Typed(KeySpec k) =>
        (k.Modifiers & (BrowserInputLogic.Ctrl | BrowserInputLogic.Alt | BrowserInputLogic.Meta)) == 0 && k.Text.Length > 0 &&
        k.Text is not ("\r" or " ") && !k.Code.StartsWith("Numpad", StringComparison.Ordinal);

    /// <summary>Virtual keys that need the extended flag (else arrows and Home/End arrive as the numpad's).</summary>
    public static bool Extended(int vk) => vk is >= 33 and <= 40 or 45 or 46 or 91 or 92 or 93 or 111 or 144 or >= 166 and <= 183;

    internal static void SelfTest()
    {
        if (ToScreen(800, 100, 2400, 1600) != 1300 || ToScreen(0, -50, 10, 10) != -50 || ToScreen(5, 10, 0, 0) != 15)
            throw new InvalidOperationException("ToScreen");
        var missing = @"Z:\nowhere\My Tool.exe";
        if (AppName(missing) != "My Tool" || !Names(missing, "my tool") || !Names(missing, "My Tool.exe") || Names(missing, "") || Names(missing, "Tool"))
            throw new InvalidOperationException("AppName");
        if (Approvable(@"C:\Windows\System32\ApplicationFrameHost.exe", null) || Approvable(@"C:\App\Agent Orchestrator.exe", @"c:\app\agent orchestrator.exe") ||
            !Approvable(@"C:\Windows\notepad.exe", @"C:\App\Agent Orchestrator.exe"))
            throw new InvalidOperationException("Approvable");
        KeySpec K(string s) => BrowserInputLogic.ParseKeys(s)!;
        if (Refusal(K("Meta+R")) is null || Refusal(K("Meta")) is null || Refusal(K("Alt+Tab")) is null || Refusal(K("Ctrl+Escape")) is null ||
            Refusal(K("Ctrl+V")) is null || Refusal(K("Shift+Insert")) is null || Refusal(K("PrintScreen")) is null || Refusal(K("AudioVolumeUp")) is null ||
            Refusal(K("BrowserHome")) is null || Refusal(K("LaunchMail")) is null ||
            Refusal(K("Ctrl+S")) is not null || Refusal(K("Alt+F4")) is not null || Refusal(K("Escape")) is not null || Refusal(K("Tab")) is not null)
            throw new InvalidOperationException("Refusal");
        if (!Typed(K("a")) || !Typed(K("Shift+1")) || !Typed(K("\u00e9")) || Typed(K("Enter")) || Typed(K("Space")) || Typed(K("Ctrl+A")) ||
            Typed(K("Numpad5")) || Typed(K("F5")))
            throw new InvalidOperationException("Typed");
        if (!Extended(37) || !Extended(46) || Extended(65) || Extended(13))
            throw new InvalidOperationException("Extended");
    }
}

public partial class MainWindow
{
    // ---- Win32
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int length, out int returned);

    private const uint QueryLimited = 0x1000, TokenQuery = 8;
    private const int ExStyle = -20;
    private const long ToolWindow = 0x80, Transparent = 0x20, NoActivate = 0x8000000, Layered = 0x80000;

    // ---- state: one bound window per reply (per asking engine process)
    private Process? _cuOwner;
    private IntPtr _cuWindow, _cuHook;
    private uint _cuPid;
    private string _cuExe = "";
    private bool _cuStopped, _cuDragging;
    private volatile bool _cuArmed; // a window is bound: the Esc watch (its own thread) acts
    private RECT _cuShotRect;
    private int _cuShotW, _cuShotH;
    private List<IntPtr> _cuListed = new();
    private LowLevelProc? _cuHookProc; // kept: the hook calls it after this method returns
    private Window? _cuBanner;
    private DispatcherTimer? _cuWatch;

    private static T Physical<T>(Func<T> f)
    {
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: every coordinate below is a real screen pixel
        try
        {
            return f();
        }
        finally
        {
            if (old != IntPtr.Zero)
                SetThreadDpiAwarenessContext(old);
        }
    }

    private static uint PidOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static string ExeOf(uint pid)
    {
        var handle = OpenProcess(QueryLimited, false, pid);
        if (handle == IntPtr.Zero)
            return "";
        try
        {
            var name = new StringBuilder(1024);
            var size = name.Capacity;
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString() : "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // An admin (elevated) process: Windows drops input sent to it from this one. A token that cannot be read is treated as one.
    private static bool Elevated(uint pid)
    {
        var handle = OpenProcess(QueryLimited, false, pid);
        if (handle == IntPtr.Zero)
            return true;
        try
        {
            if (!OpenProcessToken(handle, TokenQuery, out var token))
                return true;
            try
            {
                return !GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, 4, out _) || elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    // The windows on the taskbar: shown, titled, not owned, not tool windows, not cloaked (another virtual desktop, a suspended app); not this app's.
    private static List<(IntPtr Hwnd, string Exe, string Title)> AppWindows()
    {
        var found = new List<(IntPtr, string, string)>();
        var self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero || ((long)GetWindowLongPtr(hwnd, ExStyle) & ToolWindow) != 0)
                return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0)
                return true;
            var title = TitleOf(hwnd);
            var pid = PidOf(hwnd);
            if (title.Length > 0 && pid != self && ExeOf(pid) is { Length: > 0 } exe)
                found.Add((hwnd, exe, title));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private bool Approved(string exe) => _settings.ComputerApps.Any(a => a.Equals(exe, StringComparison.OrdinalIgnoreCase));

    private static RECT Bounds(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS: the visible frame, in screen pixels */, out RECT r, Marshal.SizeOf<RECT>()) == 0 ? r : default;

    // ---- the request from the engine

    // asker: the engine process that sent the request (a Code reply's or a project run's); the answer goes back to it.
    private async Task AnswerComputerAsync(JsonElement ev, Process? asker)
    {
        double? Opt(string name) => ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var input = new BrowserInput(Opt("x"), Opt("y"), Opt("to_x"), Opt("to_y"), Opt("dx"), Opt("dy"), Str(ev, "button") is { Length: > 0 } button ? button : "left",
            Math.Max((int)Num(ev, "clicks"), 1), Str(ev, "text"), Str(ev, "keys"), !(ev.TryGetProperty("shot", out var shot) && shot.ValueKind == JsonValueKind.False));
        string result;
        string? image = null;
        try
        {
            (result, image) = await ComputerAnswerAsync(asker, Str(ev, "action"), Str(ev, "app"), (int)Num(ev, "window"), input);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ExternalException or ArgumentException or IOException)
        {
            result = "ERROR: " + e.Message;
        }
        NoteShot(image);
        ToTurn(asker, JsonSerializer.Serialize(new { id = Str(ev, "id"), result, image }));
    }

    private async Task<(string Result, string? Image)> ComputerAnswerAsync(Process? asker, string action, string app, int window, BrowserInput input)
    {
        if (!ReferenceEquals(asker, _cuOwner)) // a new reply: no window yet, and Esc from the last one no longer applies
        {
            EndComputer();
            (_cuOwner, _cuStopped, _cuListed) = (asker, false, new());
        }
        if (!_settings.ComputerUse)
            return ("ERROR: the user switched off Computer use (Options > General); ask them to turn it on", null);
        if (_cuStopped)
            return ("ERROR: the user pressed Esc to take back control; do not use the computer again in this reply. Stop and tell them what you did", null);
        if (action == "apps")
            return (AppsList(), null);
        if (action == "open")
            return await OpenWindowAsync(app, window, input.Shot);
        if (_cuWindow == IntPtr.Zero)
            return ("ERROR: no window yet; use action 'open' with an approved application first", null);
        if (!IsWindow(_cuWindow) || PidOf(_cuWindow) != _cuPid)
            return ("ERROR: the window you were using has closed; this reply cannot open another", null);
        if (!Approved(_cuExe))
            return ("ERROR: the user removed this application from the approved list", null);
        if (!Front())
            return ("ERROR: the window could not be brought to the front; ask the user to click it once", null);
        if (action == "screenshot")
            return await ShotAsync("SCREENSHOT");
        ShowBanner();
        string done;
        switch (action)
        {
            case "type":
                if (input.Text.Length > 5000)
                    return ("ERROR: type at most 5000 characters at once", null);
                if (await TypeTextAsync(input.Text) is { } typeError)
                    return (typeError, null);
                done = $"TYPED {input.Text.Length} character{(input.Text.Length == 1 ? "" : "s")}";
                break;
            case "key":
                var chords = BrowserInputLogic.Chords(input.Keys);
                if (chords.Length is 0 or > 30)
                    return ("ERROR: key needs one to 30 keys, e.g. Enter, Ctrl+S, or Tab Tab Enter", null);
                var specs = new List<KeySpec>();
                foreach (var chord in chords)
                {
                    if (BrowserInputLogic.ParseKeys(chord) is not { } spec || (!ComputerLogic.Typed(spec) && spec.VirtualKey == 0))
                        return ($"ERROR: unknown key \"{chord}\"; keys are letters, digits, punctuation, Enter, Tab, Escape, Backspace, Delete, Insert, Space, Arrow*, Home, End, PageUp, PageDown, F1-F24, Numpad0-9, CapsLock, ContextMenu and the media keys, alone or with Ctrl+, Shift+, Alt+", null);
                    if (ComputerLogic.Refusal(spec) is { } why)
                        return ($"ERROR: {chord}: {why}", null);
                    specs.Add(spec);
                }
                foreach (var spec in specs)
                {
                    if (!Ours())
                        return ("ERROR: another window took the keyboard; take a screenshot to see where things are", null);
                    PressKey(spec);
                    await Task.Delay(30);
                }
                done = $"PRESSED {string.Join(" ", chords)}";
                break;
            default:
                var pointed = await PointAtAsync(action, input);
                if (pointed.StartsWith("ERROR", StringComparison.Ordinal))
                    return (pointed, null);
                done = pointed;
                break;
        }
        await Task.Delay(450); // the application reacts
        if (!IsWindow(_cuWindow))
            return ($"{done}\nthe window closed", null);
        return input.Shot ? await ShotAsync(done) : ($"{done}\nWINDOW: \"{TitleOf(_cuWindow)}\"", null);
    }

    private string AppsList()
    {
        var windows = AppWindows();
        _cuListed = new();
        var lines = new List<string>();
        foreach (var exe in _settings.ComputerApps)
        {
            var mine = windows.Where(w => w.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList();
            lines.Add($"{ComputerLogic.AppName(exe)}{(mine.Count == 0 ? " (no window; open starts it)" : "")}");
            foreach (var w in mine)
            {
                _cuListed.Add(w.Hwnd);
                lines.Add($"  window {_cuListed.Count}: \"{w.Title}\"" + (w.Hwnd == _cuWindow ? " (yours)" : ""));
            }
        }
        return lines.Count == 0 ? "No applications are approved; ask the user to add one in Options > General > Computer use."
            : "APPROVED APPLICATIONS\n" + string.Join("\n", lines);
    }

    private async Task<(string, string?)> OpenWindowAsync(string app, int number, bool shot)
    {
        IntPtr target;
        if (app.Length > 0)
        {
            if (_settings.ComputerApps.FirstOrDefault(a => ComputerLogic.Names(a, app)) is not { } exe)
                return ($"ERROR: \"{app}\" is not an approved application; the approved: {string.Join(", ", _settings.ComputerApps.Select(ComputerLogic.AppName))}", null);
            target = exe.Equals(_cuExe, StringComparison.OrdinalIgnoreCase) && IsWindow(_cuWindow) ? _cuWindow
                : AppWindows().FirstOrDefault(w => w.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).Hwnd;
            if (target == IntPtr.Zero)
            {
                if (_cuWindow != IntPtr.Zero)
                    return ($"ERROR: you already use \"{TitleOf(_cuWindow)}\" in this reply; only one window per reply", null);
                if (!File.Exists(exe))
                    return ($"ERROR: {exe} is no longer there; ask the user to add the application again", null);
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                for (var i = 0; i < 60 && target == IntPtr.Zero; i++)
                {
                    await Task.Delay(250);
                    target = AppWindows().FirstOrDefault(w => w.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).Hwnd;
                }
                if (target == IntPtr.Zero)
                    return ($"ERROR: {ComputerLogic.AppName(exe)} started but showed no window within 15 s; try 'apps' in a moment", null);
                await Task.Delay(800); // it draws its first frame
            }
        }
        else
        {
            if (_cuListed.Count == 0)
                AppsList();
            if (number < 1 || number > _cuListed.Count)
                return ($"ERROR: there is no window {number}; use 'apps' to list them", null);
            target = _cuListed[number - 1];
            if (!IsWindow(target) || !Approved(ExeOf(PidOf(target))))
                return ("ERROR: that window has closed; use 'apps' again", null);
        }
        if (_cuWindow != IntPtr.Zero && target != _cuWindow)
            return ($"ERROR: you already use \"{TitleOf(_cuWindow)}\" in this reply; only one window per reply", null);
        var pid = PidOf(target);
        if (!StartHook())
            return ("ERROR: the Esc watch could not start, so the user could not stop you; computer use is unavailable", null);
        if (Elevated(pid))
            return ("ERROR: that application runs as administrator, and Windows does not let another program use it", null);
        (_cuWindow, _cuPid, _cuExe) = (target, pid, ExeOf(pid));
        StartWatch();
        ShowBanner();
        if (!Front())
            return ("ERROR: the window could not be brought to the front; ask the user to click it once", null);
        await Task.Delay(250);
        var opened = $"OPENED \"{TitleOf(target)}\" ({ComputerLogic.AppName(_cuExe)}); this reply works in this window only";
        return shot ? await ShotAsync(opened) : (opened, null);
    }

    // The bound window (or its open dialog) in front, restored if minimized; true once it is.
    private bool Front()
    {
        if (Ours())
            return true;
        if (IsIconic(_cuWindow))
            ShowWindow(_cuWindow, 9 /* SW_RESTORE */);
        var target = GetLastActivePopup(_cuWindow) is var popup && Mine(popup) ? popup : _cuWindow;
        var fg = GetForegroundWindow();
        var (theirs, me) = (GetWindowThreadProcessId(fg, out _), GetCurrentThreadId());
        var attached = theirs != 0 && theirs != me && AttachThreadInput(me, theirs, true); // the foreground lock lets a window that owns input pass focus on
        try
        {
            BringWindowToTop(target);
            SetForegroundWindow(target);
        }
        finally
        {
            if (attached)
                AttachThreadInput(me, theirs, false);
        }
        for (var i = 0; i < 10 && !Ours(); i++)
            Thread.Sleep(30);
        return Ours();
    }

    private bool Ours() => Mine(GetForegroundWindow());

    // The bound window itself, a control in it, a dialog it owns, or a pop-up menu its application opened (menus have no
    // owner). Not another window of the same application (a second window, the taskbar of Explorer).
    private bool Mine(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _cuWindow == IntPtr.Zero)
            return false;
        if (GetAncestor(hwnd, 3 /* GA_ROOTOWNER */) == _cuWindow)
            return true;
        var root = GetAncestor(hwnd, 2 /* GA_ROOT */);
        var kind = new StringBuilder(64);
        GetClassName(root, kind, kind.Capacity);
        return kind.ToString() == "#32768" && PidOf(root) == _cuPid;
    }

    private bool MineAt(POINT at) => Physical(() => Mine(WindowFromPoint(at)));

    // Checked right before each button or wheel event: Esc was not pressed, and the window under the pointer is still the bound one.
    private string? Gate()
    {
        if (_cuStopped)
            return "ERROR: the user pressed Esc to take back control; nothing more was done. Stop and tell them what you did";
        var at = Physical(() => GetCursorPos(out CursorPoint p) ? new POINT { X = p.X, Y = p.Y } : default);
        return MineAt(at) ? null : "ERROR: another window is now under the pointer, so nothing was clicked; take a new screenshot";
    }

    // A JPEG of the window as the screen shows it (dialogs on top of it included), at most MaxShot pixels a side.
    private async Task<(string, string?)> ShotAsync(string done)
    {
        await Task.Delay(50);
        var r = Bounds(_cuWindow);
        var (w, h) = (r.Right - r.Left, r.Bottom - r.Top);
        if (w <= 0 || h <= 0)
            return ($"{done}\nthe window has no size (minimized?)", null);
        using var full = new System.Drawing.Bitmap(w, h);
        Physical(() =>
        {
            using var g = System.Drawing.Graphics.FromImage(full);
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
            return 0;
        });
        var scale = Math.Min(1.0, (double)ComputerLogic.MaxShot / Math.Max(w, h));
        var (iw, ih) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
        using var small = scale < 1 ? new System.Drawing.Bitmap(full, iw, ih) : null;
        using var jpeg = new MemoryStream();
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var quality = new System.Drawing.Imaging.EncoderParameters(1);
        quality.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        (small ?? full).Save(jpeg, codec, quality);
        (_cuShotRect, _cuShotW, _cuShotH) = (r, iw, ih);
        return ($"{done}\nWINDOW: \"{TitleOf(_cuWindow)}\"\nIMAGE: {iw}x{ih} px; x and y for click, move, drag and scroll are pixels of this image",
            "data:image/jpeg;base64," + Convert.ToBase64String(jpeg.ToArray()));
    }

    // A screenshot pixel as a screen point, checked: a screenshot was taken, the window has not moved or resized since, and the
    // point lands on this application (not on a window in front of it). Error text in Error.
    private (POINT At, string? Error) ScreenPoint(double x, double y)
    {
        if (_cuShotW <= 0)
            return (default, "ERROR: take a screenshot first; x and y are pixels of the last screenshot");
        var now = Bounds(_cuWindow);
        if (now.Left != _cuShotRect.Left || now.Top != _cuShotRect.Top || now.Right != _cuShotRect.Right || now.Bottom != _cuShotRect.Bottom)
            return (default, "ERROR: the window moved or changed size since the last screenshot; take a new screenshot");
        if (x < 0 || y < 0 || x >= _cuShotW || y >= _cuShotH)
            return (default, $"ERROR: ({x:0.#}, {y:0.#}) is outside the last screenshot ({_cuShotW}x{_cuShotH})");
        var at = new POINT
        {
            X = ComputerLogic.ToScreen(x, now.Left, now.Right - now.Left, _cuShotW),
            Y = ComputerLogic.ToScreen(y, now.Top, now.Bottom - now.Top, _cuShotH),
        };
        return MineAt(at) ? (at, null) : (at, $"ERROR: ({x:0.#}, {y:0.#}) is covered by another window; take a new screenshot");
    }

    private async Task<string> PointAtAsync(string action, BrowserInput input)
    {
        POINT at;
        if (input.X is { } x && input.Y is { } y)
        {
            var (p, error) = ScreenPoint(x, y);
            if (error is not null)
                return error;
            at = p;
        }
        else if (action == "scroll")
        {
            var r = Bounds(_cuWindow);
            at = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        }
        else
            return "ERROR: give x and y";
        var (to, toError) = action == "drag" ? ScreenPoint(input.ToX ?? -1, input.ToY ?? -1) : (default, null);
        if (toError is not null) // the drop point too must be on the window
            return toError;
        await GlideAsync(at);
        if (action != "move" && Gate() is { } gated)
            return gated;
        switch (action)
        {
            case "move":
                return _cuStopped ? Gate()! : $"MOVED the pointer to ({input.X:0.#}, {input.Y:0.#})";
            case "click":
                var (down, up) = input.Button switch { "right" => (0x8u, 0x10u), "middle" => (0x20u, 0x40u), _ => (0x2u, 0x4u) };
                for (var n = 0; n < Math.Clamp(input.Clicks, 1, 3); n++)
                {
                    if (n > 0 && Gate() is { } between)
                        return between;
                    SendMouse(down);
                    SendMouse(up);
                    await Task.Delay(40);
                }
                return $"{(input.Clicks > 1 ? input.Clicks == 2 ? "DOUBLE-" : "TRIPLE-" : "")}{(input.Button == "left" ? "CLICKED" : input.Button.ToUpperInvariant() + "-CLICKED")} at ({input.X:0.#}, {input.Y:0.#})";
            case "drag":
                SendMouse(0x2);
                _cuDragging = true;
                try
                {
                    await GlideAsync(to, 15);
                    await Task.Delay(60);
                }
                finally
                {
                    _cuDragging = false;
                    SendMouse(0x4); // released even when Esc stopped the glide
                }
                return _cuStopped ? Gate()! : $"DRAGGED from ({input.X:0.#}, {input.Y:0.#}) to ({input.ToX:0.#}, {input.ToY:0.#})";
            default: // scroll: wheel notches; positive dy = down, positive dx = right; at most 50 at once
                if (input.Dy is { } dy && dy != 0)
                    SendMouse(0x800, unchecked((uint)(int)Math.Round(-Math.Clamp(dy, -50, 50) * 120)));
                if (input.Dx is { } dx && dx != 0)
                    SendMouse(0x1000, unchecked((uint)(int)Math.Round(Math.Clamp(dx, -50, 50) * 120)));
                await Task.Delay(200);
                return $"SCROLLED by ({input.Dx ?? 0:0.#}, {input.Dy ?? 0:0.#}) notches";
        }
    }

    // The pointer moves to the point in steps, so hover effects and drags see a path rather than a jump.
    private async Task GlideAsync(POINT to, int delay = 8)
    {
        var start = Physical(() => GetCursorPos(out CursorPoint from) ? new POINT { X = from.X, Y = from.Y } : to);
        var steps = BrowserInputLogic.Steps(Math.Sqrt(Math.Pow(to.X - start.X, 2) + Math.Pow(to.Y - start.Y, 2)));
        for (var i = 1; i <= steps && !_cuStopped; i++)
        {
            var (x, y) = (start.X + (to.X - start.X) * i / steps, start.Y + (to.Y - start.Y) * i / steps);
            Physical(() => SetCursorPos(x, y));
            await Task.Delay(delay);
        }
    }

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    // Every event the model sends carries this, so the Esc watch tells its key presses from the user's (an Esc from the
    // On-Screen Keyboard or a remote-desktop tool is injected too, and must still stop it).
    private static readonly IntPtr ModelInput = new(0x414F4355);

    private static void SendMouse(uint flags, uint data = 0) =>
        Send(new INPUT { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = ModelInput } } });

    private static INPUT KeyInput(ushort vk, bool up) =>
        new() { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? 2u : 0) | (ComputerLogic.Extended(vk) ? 1u : 0), dwExtraInfo = ModelInput } } };

    private static void TypeChar(char c) =>
        Send(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = c, dwFlags = 4 /* UNICODE */, dwExtraInfo = ModelInput } } },
             new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = c, dwFlags = 4 | 2, dwExtraInfo = ModelInput } } });

    private static void PressKey(KeySpec k)
    {
        if (ComputerLogic.Typed(k))
        {
            foreach (var c in k.Text)
                TypeChar(c);
            return;
        }
        var mods = new List<ushort>();
        if ((k.Modifiers & BrowserInputLogic.Ctrl) != 0) mods.Add(0x11);
        if ((k.Modifiers & BrowserInputLogic.Alt) != 0) mods.Add(0x12);
        if ((k.Modifiers & BrowserInputLogic.Shift) != 0) mods.Add(0x10);
        var main = (ushort)k.VirtualKey;
        var keys = mods.Contains(main) || k.Key is "Control" or "Alt" or "Shift" ? mods : [.. mods, main];
        Send([.. keys.Select(v => KeyInput(v, false)), .. Enumerable.Reverse(keys).Select(v => KeyInput(v, true))]);
    }

    private async Task<string?> TypeTextAsync(string text)
    {
        var n = 0;
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            if (_cuStopped)
                return "ERROR: the user pressed Esc to take back control";
            if (!Ours())
                return $"ERROR: another window took the keyboard after {n} characters; take a screenshot";
            if (c == '\n')
                Send(KeyInput(0x0D, false), KeyInput(0x0D, true));
            else if (c == '\t')
                Send(KeyInput(0x09, false), KeyInput(0x09, true));
            else if (c != '\r')
                TypeChar(c);
            if (++n % 8 == 0)
                await Task.Delay(10); // the application keeps up, and an Esc gets through
        }
        return null;
    }

    // ---- Esc, the banner, and the end of a reply

    // The Esc watch: a low-level keyboard hook on a thread of its own, so a busy UI thread never makes Windows drop it.
    // It stays for the app's life and only acts while a window is bound. False: it could not start.
    private bool StartHook()
    {
        if (_cuHook != IntPtr.Zero)
            return true;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _cuHookProc = EscHook;
            _cuHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _cuHookProc, GetModuleHandle(null), 0);
            ready.Set();
            if (_cuHook == IntPtr.Zero)
                return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // the hook's calls are delivered while this waits
        }) { IsBackground = true, Name = "Esc watch" };
        thread.Start();
        ready.Wait(3000);
        return _cuHook != IntPtr.Zero;
    }

    private void StartWatch()
    {
        _cuArmed = true;
        _cuWatch ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            bool ended;
            try
            {
                ended = _cuOwner is null || _cuOwner.HasExited;
            }
            catch (InvalidOperationException)
            {
                ended = true; // disposed
            }
            if (ended)
                EndComputer(); // the reply ended
        }, Dispatcher);
        _cuWatch.Start();
    }

    private IntPtr EscHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is 0x100 or 0x104) // WM_KEYDOWN, WM_SYSKEYDOWN
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (_cuArmed && key.vkCode == 0x1B && key.dwExtraInfo != ModelInput) // Esc, not one the model sent
                Dispatcher.BeginInvoke(ComputerPanic);
        }
        return CallNextHookEx(_cuHook, code, wParam, lParam);
    }

    // Esc: the model's hands come off the computer and its reply stops.
    internal void ComputerPanic()
    {
        if (_cuWindow == IntPtr.Zero || _cuStopped)
            return;
        _cuStopped = true;
        if (_cuDragging)
            SendMouse(0x4); // a drag in progress lets go
        var owner = _cuOwner;
        EndComputer(keepOwner: true);
        Toast("Stopped: the model let go of your computer (Esc)");
        Log("Computer use stopped with Esc.");
        try
        {
            if (owner is { HasExited: false }) // the reply (or project run) that was using it stops
            {
                if (ReferenceEquals(owner, _process))
                    Stop_Click(this, new RoutedEventArgs());
                else
                {
                    _chatStopRequested = true;
                    LauncherLogic.KillTreeExceptLlama(owner);
                }
            }
        }
        catch (InvalidOperationException) { } // already gone
    }

    private void EndComputer(bool keepOwner = false)
    {
        (_cuWindow, _cuPid, _cuExe, _cuShotW, _cuShotH, _cuArmed) = (IntPtr.Zero, 0, "", 0, 0, false);
        if (!keepOwner)
            _cuOwner = null;
        _cuWatch?.Stop();
        _cuBanner?.Hide();
    }

    // "Model is using <app>: press Esc to stop" at the top of the screen: click-through, never focused, left out of screenshots.
    private void ShowBanner()
    {
        if (_cuBanner is null)
        {
            var text = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold };
            _cuBanner = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
                ShowActivated = false, Focusable = false, IsHitTestVisible = false, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                Content = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 0xC2, 0x2F, 0x34)), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 6, 14, 6), Child = text },
            };
            _cuBanner.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(_cuBanner).Handle;
                SetWindowLongPtr(hwnd, ExStyle, new IntPtr((long)GetWindowLongPtr(hwnd, ExStyle) | Transparent | NoActivate | ToolWindow | Layered));
                SetWindowDisplayAffinity(hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
            };
            _cuBanner.SizeChanged += (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                (_cuBanner.Left, _cuBanner.Top) = (area.Left + (area.Width - _cuBanner.ActualWidth) / 2, area.Top + 10);
            };
        }
        ((TextBlock)((Border)_cuBanner.Content).Child).Text = $"●  The model is using {ComputerLogic.AppName(_cuExe)}: press Esc to stop";
        if (!_cuBanner.IsVisible)
            _cuBanner.Show();
    }

    // ---- Options > General > Computer use

    private void InitComputer()
    {
        ComputerToggle.IsChecked = _settings.ComputerUse;
        RenderComputerApps();
        Closed += (_, _) => _cuBanner?.Close(); // a hidden window would keep the app running after the main one closes
    }

    private void ComputerToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ComputerUse = ComputerToggle.IsChecked == true;
        _settings.Save();
        if (!_settings.ComputerUse)
            EndComputer();
        else if (_settings.ComputerApps.Count == 0)
            Toast("Add the applications the model may use below");
    }

    private void RenderComputerApps()
    {
        ComputerAppsList.Children.Clear();
        if (_settings.ComputerApps.Count == 0)
            ComputerAppsList.Children.Add(new TextBlock { Text = "None yet: the model can use only the applications you add here.", Foreground = (Brush)FindResource("Muted") });
        foreach (var exe in _settings.ComputerApps.ToList())
        {
            var remove = new Button { Content = "Remove", Padding = new Thickness(10, 4, 10, 4), Tag = exe, VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => RemoveComputerApp(exe);
            DockPanel.SetDock(remove, Dock.Right);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = ComputerLogic.AppName(exe), FontWeight = FontWeights.SemiBold });
            name.Children.Add(new TextBlock { Text = exe, Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = exe });
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), Tag = exe };
            row.Children.Add(remove);
            row.Children.Add(name);
            ComputerAppsList.Children.Add(row);
        }
    }

    internal void AddComputerApp(string exe)
    {
        if (exe.Length == 0 || Approved(exe))
            return;
        if (!ComputerLogic.Approvable(exe, Environment.ProcessPath))
        {
            Toast(Path.GetFileName(exe).StartsWith("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                ? "Store apps share one frame program, so they cannot be approved one by one"
                : "The model may not use Agent Orchestrator itself");
            return;
        }
        _settings.ComputerApps.Add(exe);
        _settings.Save();
        RenderComputerApps();
    }

    internal void RemoveComputerApp(string exe)
    {
        _settings.ComputerApps.RemoveAll(a => a.Equals(exe, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        RenderComputerApps();
    }

    // The picker: the applications with a window open now, then a program chosen from disk.
    private void ComputerAdd_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ComputerAddButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var group in AppWindows().Where(w => !Approved(w.Exe) && ComputerLogic.Approvable(w.Exe, Environment.ProcessPath)).GroupBy(w => w.Exe, StringComparer.OrdinalIgnoreCase).OrderBy(g => ComputerLogic.AppName(g.Key)))
        {
            var exe = group.Key;
            var item = new MenuItem { Header = $"{ComputerLogic.AppName(exe)}  ·  {group.First().Title}", ToolTip = exe };
            item.Click += (_, _) => AddComputerApp(exe);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No other application has a window open", IsEnabled = false });
        menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = "Browse for a program…" };
        browse.Click += (_, _) =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "Choose an application the model may use", Filter = "Programs (*.exe)|*.exe" };
            if (pick.ShowDialog(this) == true)
                AddComputerApp(pick.FileName);
        };
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    // --ui-smoke: the Options list adds and removes; a Code request names the approved applications only while Computer use is on;
    // the tool refuses while off, an unapproved application, input before a window, a bad window number, and everything after Esc.
    private async Task<bool> ComputerSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: computer: {what}"); return false; }
        var (on, apps) = (_settings.ComputerUse, _settings.ComputerApps.ToList());
        try
        {
            _settings.ComputerApps.Clear();
            AddComputerApp(@"Z:\tools\Fake Tool.exe");
            AddComputerApp(@"z:\TOOLS\fake tool.exe"); // the same program
            if (_settings.ComputerApps.Count != 1 || ComputerAppsList.Children.Count != 1 || ((DockPanel)ComputerAppsList.Children[0]).Tag as string != @"Z:\tools\Fake Tool.exe")
                return Fail("adding to the list");
            ComputerToggle.IsChecked = false;
            ComputerToggle_Click(ComputerToggle, new RoutedEventArgs());
            if (CurrentExtras().Computer is not [])
                return Fail("off still offers the tool");
            var (offResult, _) = await ComputerAnswerAsync(null, "apps", "", 0, new BrowserInput(null, null, null, null, null, null, "left", 1, "", "", true));
            if (!offResult.Contains("switched off Computer use"))
                return Fail("off: " + offResult);
            ComputerToggle.IsChecked = true;
            ComputerToggle_Click(ComputerToggle, new RoutedEventArgs());
            if (CurrentExtras().Computer is not ["Fake Tool"] ||
                JsonDocument.Parse(LauncherLogic.BuildCodeRequest("chat", "planner", [], @"Y:\P", [], CurrentExtras())).RootElement.GetProperty("computer")[0].GetString() != "Fake Tool")
                return Fail("on: the request does not name the application");
            var none = new BrowserInput(10, 10, null, null, null, null, "left", 1, "", "", false);
            async Task<string> Ask(string action, string app = "", int window = 0) => (await ComputerAnswerAsync(null, action, app, window, none)).Item1;
            if (!(await Ask("apps")).Contains("Fake Tool (no window; open starts it)") || !(await Ask("open", "Notepad")).Contains("not an approved application") ||
                !(await Ask("click")).Contains("no window yet") || !(await Ask("open", window: 9)).Contains("no window 9") ||
                !(await Ask("open", "fake tool")).Contains("no longer there"))
                return Fail("refusals");
            if (!StartHook() || !StartHook())
                return Fail("the Esc watch did not start");
            var self = new WindowInteropHelper(this).Handle; // a window and its child are "mine"; another window is not
            _cuWindow = self;
            if (!Mine(self) || Mine(IntPtr.Zero) || (_cuBanner is { } banner && Mine(new WindowInteropHelper(banner).Handle)))
                return Fail("Mine");
            (_cuWindow, _cuStopped) = (new IntPtr(1), false); // as if bound: Esc stops everything after it, until the next reply
            ComputerPanic();
            if (_cuWindow != IntPtr.Zero || !(await Ask("apps")).Contains("pressed Esc"))
                return Fail("Esc");
            _cuOwner = new Process(); // the next reply starts clean
            if ((await Ask("apps")).Contains("pressed Esc"))
                return Fail("Esc outlived its reply");
            RemoveComputerApp(@"z:\tools\FAKE TOOL.EXE");
            return _settings.ComputerApps.Count == 0 && ComputerAppsList.Children[0] is TextBlock || Fail("removing from the list");
        }
        finally
        {
            EndComputer();
            (_settings.ComputerUse, _settings.ComputerApps) = (on, apps);
            InitComputer();
        }
    }
}

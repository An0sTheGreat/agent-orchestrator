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

    /// <summary>One computer action for the daily log, so a bad run can be looked into after its chat is gone: the action,
    /// its arguments (the request less its id, event and shot flag; long text cut), the screenshot size the coordinates
    /// refer to, and the first line of the result.</summary>
    public static string LogLine(string action, string request, int shotW, int shotH, string result)
    {
        var args = "";
        try
        {
            var fields = System.Text.Json.Nodes.JsonNode.Parse(request)!.AsObject();
            foreach (var drop in new[] { "id", "e", "event", "action", "shot" })
                fields.Remove(drop);
            args = fields.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException) { }
        if (args.Length > 300)
            args = args[..300] + "…";
        var first = result.Split('\n')[0];
        return $"Computer: {action} {args}{(shotW > 0 ? $" (screenshot {shotW}x{shotH})" : "")} -> {(first.Length > 200 ? first[..200] + "…" : first)}";
    }

    /// <summary>Whether a program may be approved: not this app (the model would change its own Options), not the frame host of
    /// Store apps (approving it would approve every one of them).</summary>
    public static bool Approvable(string exe, string? self) =>
        !Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) &&
        !(self is not null && Path.GetFullPath(exe).Equals(Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase));

    /// <summary>A screenshot pixel as a screen pixel: `origin` and `size` are the window's on that axis when the shot was taken, `image` the shot's.</summary>
    public static int ToScreen(double v, int origin, int size, int image) => origin + (int)Math.Floor(image <= 0 ? v : v * size / image);

    /// <summary>How long the pointer takes to glide `distance` screen pixels, in ms: slow enough to follow, never sluggish.</summary>
    public static double GlideMs(double distance) => distance < 1 ? 0 : Math.Clamp(140 + distance * 0.2, 140, 400);

    /// <summary>Ease in and out (cubic): 0 at 0, 1 at 1, slow at both ends.</summary>
    public static double Ease(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>The longest side of a screenshot, in pixels (larger windows are scaled down).</summary>
    // A screenshot's pixel budget: image tokens grow with the area, so the cap is on the area (a 16:9 window comes out about
    // 1280x720), with a longest side; a wide window keeps its height instead of being squeezed to a strip.
    public const int MaxShotArea = 921_600, MaxShotSide = 1800;

    /// <summary>A round grid spacing (in screenshot pixels) giving at most about ten lines across `span` screenshot pixels.</summary>
    public static double GridStep(double span) => new double[] { 2, 5, 10, 20, 25, 50, 100, 200 }.FirstOrDefault(s => span / s <= 10, 500);

    /// <summary>A faint grid on an image, labelled in screenshot pixels: image pixel p is screenshot pixel origin + p x units.
    /// A screenshot gets a line every 100 px (units 1); a zoom gets lines in the full screenshot's pixels, so a point in it can
    /// be read off in the numbers clicks use.</summary>
    public static void DrawGrid(System.Drawing.Graphics g, int iw, int ih, double originX, double originY, double units)
    {
        var step = units == 1 ? 100 : GridStep(Math.Max(iw, ih) * units);
        using var line = new System.Drawing.Pen(System.Drawing.Color.FromArgb(80, 0, 220, 255), 1);
        using var back = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(170, 0, 0, 0));
        using var ink = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(235, 120, 240, 255));
        using var font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        string Label(double v) => v.ToString(v == Math.Floor(v) ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
        void Tag(string text, float x, float y)
        {
            var size = g.MeasureString(text, font);
            g.FillRectangle(back, x, y, size.Width, size.Height - 1);
            g.DrawString(text, font, ink, x, y - 1);
        }
        for (var v = Math.Ceiling(originX / step) * step; (v - originX) / units < iw; v += step)
        {
            var px = (float)((v - originX) / units);
            g.DrawLine(line, px, 0, px, ih);
            if (v > 0)
                Tag(Label(v), px + 1, 1);
        }
        for (var v = Math.Ceiling(originY / step) * step; (v - originY) / units < ih; v += step)
        {
            var py = (float)((v - originY) / units);
            g.DrawLine(line, 0, py, iw, py);
            if (v > 0)
                {
                var text = Label(v);
                Tag(text, iw - g.MeasureString(text, font).Width, py + 1); // right edge: the left column is where controls are
            }
        }
    }

    /// <summary>The scale of a zoom of a w x h screen area: enlarged up to 4x toward the pixel budget (more detail for the model
    /// to see), shrunk only when the area is bigger than the budget.</summary>
    public static double ZoomScale(int w, int h) =>
        w <= 0 || h <= 0 ? 1 : Math.Min(4.0, Math.Min(Math.Sqrt((double)MaxShotArea / ((double)w * h)), (double)MaxShotSide / Math.Max(w, h)));

    /// <summary>A zoom box (two corners in pixels of a shotW x shotH screenshot of the window at `window`) as screen pixels,
    /// clamped to the window; null when it is under 4 screenshot pixels either way.</summary>
    public static (int Left, int Top, int Right, int Bottom)? ZoomBox(double x1, double y1, double x2, double y2, (int Left, int Top, int Right, int Bottom) window, int shotW, int shotH)
    {
        var (left, right) = (Math.Clamp(Math.Min(x1, x2), 0, shotW), Math.Clamp(Math.Max(x1, x2), 0, shotW));
        var (top, bottom) = (Math.Clamp(Math.Min(y1, y2), 0, shotH), Math.Clamp(Math.Max(y1, y2), 0, shotH));
        if (right - left < 4 || bottom - top < 4)
            return null;
        var (w, h) = (window.Right - window.Left, window.Bottom - window.Top);
        return (ToScreen(left, window.Left, w, shotW), ToScreen(top, window.Top, h, shotH),
                Math.Min(window.Right, (int)Math.Ceiling(window.Left + right * w / shotW)), Math.Min(window.Bottom, (int)Math.Ceiling(window.Top + bottom * h / shotH)));
    }

    /// <summary>The scale a w x h window's screenshot is taken at: 1 when it fits the budget.</summary>
    public static double ShotScale(int w, int h) =>
        w <= 0 || h <= 0 ? 1 : Math.Min(1.0, Math.Min(Math.Sqrt((double)MaxShotArea / ((double)w * h)), (double)MaxShotSide / Math.Max(w, h)));

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
        if (GlideMs(0.5) != 0 || Math.Abs(GlideMs(100) - 160) > 1e-6 || GlideMs(5000) != 400 || GlideMs(1) < 140 ||
            Ease(0) != 0 || Ease(1) != 1 || Math.Abs(Ease(0.5) - 0.5) > 1e-9 || Ease(0.1) >= 0.1 || Ease(0.9) <= 0.9)
            throw new InvalidOperationException("Glide");
        // a small window stays as it is; a 16:9 one keeps the budget; an ultrawide one keeps more height than a side cap would give
        int Side(int w, int h, bool width) => (int)Math.Round((width ? w : h) * ShotScale(w, h));
        if (ShotScale(800, 600) != 1 || Math.Abs(Side(2560, 1440, true) * Side(2560, 1440, false) - MaxShotArea) > 4000 ||
            Side(3440, 1390, false) <= 450 || Side(3440, 1390, true) * Side(3440, 1390, false) > MaxShotArea + 4000 || Side(10000, 200, true) > MaxShotSide)
            throw new InvalidOperationException("ShotScale");
        // a 1236x518 shot of a 3440x1440 window at (100, 50): the box (100,200)-(300,300) is the screen's (378,605)-(935,884)
        if (ZoomBox(300, 300, 100, 200, (100, 50, 3540, 1490), 1236, 518) is not (378, 605, 935, 884) ||
            ZoomBox(10, 10, 12, 50, (0, 0, 100, 100), 100, 100) is not null || ZoomBox(-50, -50, 2000, 2000, (0, 0, 100, 100), 100, 100) is not (0, 0, 100, 100) ||
            ZoomScale(200, 100) != 4 || ZoomScale(4000, 2000) >= 1 || Math.Abs(ZoomScale(800, 400) * ZoomScale(800, 400) * 800 * 400 - MaxShotArea) > 1)
            throw new InvalidOperationException("ZoomBox");
        if (GridStep(40) != 5 || GridStep(100) != 10 || GridStep(450) != 50 || GridStep(1000) != 100 || GridStep(1800) != 200 || GridStep(9000) != 500)
            throw new InvalidOperationException("GridStep");
        if (LogLine("click", """{"e":"computer","id":"9","action":"click","x":512,"y":300,"shot":true}""", 1280, 720, "CLICKED at (512, 300)\nWINDOW: \"x\"")
                != """Computer: click {"x":512,"y":300} (screenshot 1280x720) -> CLICKED at (512, 300)""" ||
            !LogLine("type", $$"""{"text":"{{new string('a', 500)}}"}""", 0, 0, "ERROR: no window yet").EndsWith("… -> ERROR: no window yet", StringComparison.Ordinal))
            throw new InvalidOperationException("LogLine");
        // the pointer art, drawn at 4x: painted at its tip (the hot spot), white inside, and clear far from it
        var art = MainWindow.PointerArt();
        art.Measure(new Size(32, 32));
        art.Arrange(new Rect(0, 0, 32, 32));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(128, 128, 384, 384, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(art);
        var pixels = new byte[128 * 128 * 4];
        bitmap.CopyPixels(pixels, 128 * 4, 0);
        byte At(double x, double y, int channel) => pixels[((int)(y * 4) * 128 + (int)(x * 4)) * 4 + channel];
        if (At(2.6, 2.6, 3) < 128 || At(7, 7, 0) < 240 || At(7, 7, 3) < 240 || At(28, 4, 3) != 0 || At(3, 28, 3) != 0)
            throw new InvalidOperationException("PointerArt");
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
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
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
    private Window? _cuBanner, _cuPulse;
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
        static BrowserInput Parse(JsonElement e)
        {
            double? Opt(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            string Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return new BrowserInput(Opt("x"), Opt("y"), Opt("to_x"), Opt("to_y"), Opt("dx"), Opt("dy"), Text("button") is { Length: > 0 } button ? button : "left",
                Math.Max((int)(Opt("clicks") ?? 1), 1), Text("text"), Text("keys"), !(e.TryGetProperty("shot", out var shot) && shot.ValueKind == JsonValueKind.False),
                Opt("element") is { } n ? (int)n : null);
        }
        var input = Parse(ev);
        var steps = ev.TryGetProperty("steps", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).Select(s => (Str(s, "action"), Parse(s))).ToList() : null;
        string result;
        string? image = null;
        try
        {
            (result, image) = await ComputerAnswerAsync(asker, Str(ev, "action"), Str(ev, "app"), (int)Num(ev, "window"), input, steps);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ExternalException or ArgumentException or IOException)
        {
            result = "ERROR: " + e.Message;
        }
        Log(ComputerLogic.LogLine(Str(ev, "action"), ev.GetRawText(), _cuShotW, _cuShotH, result));
        NoteShot(image);
        ToTurn(asker, JsonSerializer.Serialize(new { id = Str(ev, "id"), result, image }));
    }

    private async Task<(string Result, string? Image)> ComputerAnswerAsync(Process? asker, string action, string app, int window, BrowserInput input,
        IReadOnlyList<(string Action, BrowserInput Input)>? steps = null)
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
        if (action == "zoom")
            return await ZoomAsync(input);
        ShowBanner();
        ShowPointer();
        // "batch": several steps in a row, one screenshot after the last (fewer model calls); a step that fails ends it
        IReadOnlyList<(string Action, BrowserInput Input)> plan = action == "batch" ? steps ?? [] : [(action, input)];
        if (plan.Count is 0 or > 20)
            return ("ERROR: steps must hold 1 to 20 actions", null);
        var lines = new List<string>();
        for (var i = 0; i < plan.Count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(200); // the application reacts before the next step
                if (_cuStopped || !IsWindow(_cuWindow) || !Front())
                {
                    lines.Add(_cuStopped ? "ERROR: the user pressed Esc to take back control" : "ERROR: the window closed or could not be brought to the front");
                    break;
                }
            }
            var done = await ActAsync(plan[i].Action, plan[i].Input);
            lines.Add(plan.Count > 1 ? $"{i + 1}. {done}" : done);
            if (done.StartsWith("ERROR", StringComparison.Ordinal))
            {
                if (plan.Count == 1)
                    return (done, null);
                if (i + 1 < plan.Count)
                    lines.Add($"(steps {i + 2} to {plan.Count} were not run)");
                break;
            }
        }
        var report = string.Join("\n", lines);
        await Task.Delay(250); // the application reacts
        if (!IsWindow(_cuWindow))
            return ($"{report}\nthe window closed", null);
        return input.Shot && !_cuStopped ? await ShotAsync(report) : ($"{report}\nWINDOW: \"{TitleOf(_cuWindow)}\"", null);
    }

    // One click / move / drag / scroll / type / key in the bound window: what was done, or the error text.
    private async Task<string> ActAsync(string action, BrowserInput input)
    {
        switch (action)
        {
            case "type":
                if (input.Text.Length > 5000)
                    return "ERROR: type at most 5000 characters at once";
                return await TypeTextAsync(input.Text) ?? $"TYPED {input.Text.Length} character{(input.Text.Length == 1 ? "" : "s")}";
            case "key":
                var chords = BrowserInputLogic.Chords(input.Keys);
                if (chords.Length is 0 or > 30)
                    return "ERROR: key needs one to 30 keys, e.g. Enter, Ctrl+S, or Tab Tab Enter";
                var specs = new List<KeySpec>();
                foreach (var chord in chords)
                {
                    if (BrowserInputLogic.ParseKeys(chord) is not { } spec || (!ComputerLogic.Typed(spec) && spec.VirtualKey == 0))
                        return $"ERROR: unknown key \"{chord}\"; keys are letters, digits, punctuation, Enter, Tab, Escape, Backspace, Delete, Insert, Space, Arrow*, Home, End, PageUp, PageDown, F1-F24, Numpad0-9, CapsLock, ContextMenu and the media keys, alone or with Ctrl+, Shift+, Alt+";
                    if (ComputerLogic.Refusal(spec) is { } why)
                        return $"ERROR: {chord}: {why}";
                    specs.Add(spec);
                }
                foreach (var spec in specs)
                {
                    if (_cuStopped)
                        return "ERROR: the user pressed Esc to take back control";
                    if (!Ours())
                        return "ERROR: another window took the keyboard; take a screenshot to see where things are";
                    PressKey(spec);
                    await Task.Delay(30);
                }
                return $"PRESSED {string.Join(" ", chords)}";
            case "click" or "move" or "drag" or "scroll":
                return await PointAtAsync(action, input);
            default:
                return $"ERROR: \"{action}\" cannot be a step; steps are click, move, drag, scroll, type and key";
        }
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

    // A JPEG of the window as the screen shows it (dialogs on top of it included), within the pixel budget (ShotScale).
    private async Task<(string, string?)> ShotAsync(string done)
    {
        await Task.Delay(50);
        var r = Bounds(_cuWindow);
        var (w, h) = (r.Right - r.Left, r.Bottom - r.Top);
        if (w <= 0 || h <= 0)
            return ($"{done}\nthe window has no size (minimized?)", null);
        var scale = ComputerLogic.ShotScale(w, h);
        var (iw, ih) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
        var hwnd = _cuWindow;
        var found = await Task.Run(() => Physical(() => Controls(hwnd, r, IsBrowser(_cuExe)))); // the controls the application reports, while the capture waits
        var marks = found.Select((c, i) => new Mark(i + 1, c.Kind, c.Name, (c.Box.Left + c.Box.Right) / 2.0 - r.Left, (c.Box.Top + c.Box.Bottom) / 2.0 - r.Top, c.Box))
            .Select(m => m with { X = Math.Round(m.X * iw / w), Y = Math.Round(m.Y * ih / h) }).ToList();
        var (image, _, _) = Capture(r, scale, g =>
        {
            ComputerLogic.DrawGrid(g, iw, ih, 0, 0, 1);
            DrawMarks(g, marks, r, (double)iw / w);
        });
        (_cuShotRect, _cuShotW, _cuShotH, _cuMarks) = (r, iw, ih, marks);
        var list = marks.Count == 0 ? "" : "\nELEMENTS (numbered boxes in the image; click one with `element`):\n" +
            string.Join("\n", marks.Select(m => $"{m.N}: {m.Kind}{(m.Name.Length > 0 ? $" \"{m.Name}\"" : "")} at {m.X:0},{m.Y:0}"));
        return ($"{done}\nWINDOW: \"{TitleOf(_cuWindow)}\"\nIMAGE: {iw}x{ih} px with a grid labelled every 100 px; x and y for click, move, drag and scroll are pixels of this image{list}", image);
    }

    // A control the application reports through UI Automation (its kind, name, screen box), numbered on a screenshot at X, Y.
    private sealed record Mark(int N, string Kind, string Name, double X, double Y, RECT Box);
    private List<Mark> _cuMarks = new();

    private static readonly Dictionary<System.Windows.Automation.ControlType, string> Clickable = new()
    {
        [System.Windows.Automation.ControlType.Button] = "button", [System.Windows.Automation.ControlType.SplitButton] = "button",
        [System.Windows.Automation.ControlType.CheckBox] = "checkbox", [System.Windows.Automation.ControlType.RadioButton] = "radio",
        [System.Windows.Automation.ControlType.ComboBox] = "dropdown", [System.Windows.Automation.ControlType.Edit] = "field",
        [System.Windows.Automation.ControlType.Hyperlink] = "link", [System.Windows.Automation.ControlType.MenuItem] = "menu item",
        [System.Windows.Automation.ControlType.ListItem] = "list item", [System.Windows.Automation.ControlType.TabItem] = "tab",
        [System.Windows.Automation.ControlType.TreeItem] = "tree item", [System.Windows.Automation.ControlType.Slider] = "slider",
        [System.Windows.Automation.ControlType.Spinner] = "spinner", [System.Windows.Automation.ControlType.DataItem] = "item",
    };

    // The clickable controls of a window the user sees, from UI Automation, nearest the top of the tree first: at most
    // MaxMarks, within a time budget (a big web page can expose thousands). Games (VaM) draw their own controls and report none.
    private const int MaxMarks = 60;
    private static List<(string Kind, string Name, RECT Box)> Controls(IntPtr hwnd, RECT window, bool browser)
    {
        var found = new List<(string, string, RECT)>();
        var clock = Stopwatch.StartNew();
        try
        {
            var walker = System.Windows.Automation.TreeWalker.ControlViewWalker;
            var queue = new Queue<System.Windows.Automation.AutomationElement>();
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (!browser)
                queue.Enqueue(root);
            else // a browser: only the web page (its Document), not the tabs, bookmarks and extension buttons around it
                foreach (System.Windows.Automation.AutomationElement d in root.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Document)))
                    if (!d.Current.IsOffscreen && d.Current.BoundingRectangle is { IsEmpty: false, Width: > 50, Height: > 50 })
                        queue.Enqueue(d);
            while (queue.Count > 0 && found.Count < MaxMarks && clock.ElapsedMilliseconds < 900)
            {
                var child = walker.GetFirstChild(queue.Dequeue());
                for (; child is not null && clock.ElapsedMilliseconds < 900; child = walker.GetNextSibling(child))
                {
                    var c = child.Current;
                    if (c.IsOffscreen)
                        continue;
                    queue.Enqueue(child);
                    var b = c.BoundingRectangle;
                    if (!Clickable.TryGetValue(c.ControlType, out var kind) || !c.IsEnabled || b.IsEmpty || b.Width < 4 || b.Height < 4 ||
                        b.Left < window.Left - 2 || b.Top < window.Top - 2 || b.Right > window.Right + 2 || b.Bottom > window.Bottom + 2)
                        continue;
                    if (browser && !HitsItself(child, walker, b)) // a stale element that is not what is shown at its place
                        continue;
                    var name = string.Join(' ', (c.Name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    found.Add((kind, name.Length > 40 ? name[..40] + "…" : name,
                        new RECT { Left = (int)b.Left, Top = (int)b.Top, Right = (int)b.Right, Bottom = (int)b.Bottom }));
                    if (found.Count >= MaxMarks)
                        break;
                }
            }
        }
        catch (Exception e) when (e is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException) { }
        return found;
    }

    private static bool IsBrowser(string exe) =>
        Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() is "chrome" or "msedge" or "brave" or "firefox" or "opera" or "vivaldi";

    // Whether the element at the centre of `box` is `e` or inside it.
    private static bool HitsItself(System.Windows.Automation.AutomationElement e, System.Windows.Automation.TreeWalker walker, System.Windows.Rect box)
    {
        try
        {
            var hit = System.Windows.Automation.AutomationElement.FromPoint(new System.Windows.Point(box.Left + box.Width / 2, box.Top + box.Height / 2));
            for (var i = 0; hit is not null && i < 8; i++, hit = walker.GetParent(hit))
                if (System.Windows.Automation.Automation.Compare(hit, e))
                    return true;
        }
        catch (Exception ex) when (ex is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException) { }
        return false;
    }

    // Each control's box and its number on a tag at the box's corner, in a colour that stands out from most interfaces.
    private static void DrawMarks(System.Drawing.Graphics g, List<Mark> marks, RECT window, double scale)
    {
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(150, 255, 64, 160), 1f);
        using var tag = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(235, 255, 64, 160));
        using var font = new System.Drawing.Font("Segoe UI", 8.5f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        foreach (var m in marks)
        {
            var (x, y) = ((float)((m.Box.Left - window.Left) * scale), (float)((m.Box.Top - window.Top) * scale));
            var (w, h) = ((float)((m.Box.Right - m.Box.Left) * scale), (float)((m.Box.Bottom - m.Box.Top) * scale));
            g.DrawRectangle(pen, x, y, w, h);
            var text = m.N.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var size = g.MeasureString(text, font);
            g.FillRectangle(tag, x, Math.Max(0, y - size.Height + 2), size.Width, size.Height - 2);
            g.DrawString(text, font, System.Drawing.Brushes.White, x, Math.Max(0, y - size.Height + 2) - 1);
        }
    }

    // zoom: a box of the last screenshot (its pixels) captured from the screen at full detail, enlarged to the pixel budget
    // when small, so small text and buttons can be read. The last screenshot stays the frame clicks are measured in.
    private async Task<(string, string?)> ZoomAsync(BrowserInput input)
    {
        if (_cuShotW <= 0)
            return ("ERROR: take a screenshot first; the zoom box is in pixels of the last screenshot", null);
        var now = Bounds(_cuWindow);
        if (now.Left != _cuShotRect.Left || now.Top != _cuShotRect.Top || now.Right != _cuShotRect.Right || now.Bottom != _cuShotRect.Bottom)
            return ("ERROR: the window moved or changed size since the last screenshot; take a new screenshot", null);
        if (ComputerLogic.ZoomBox(input.X ?? 0, input.Y ?? 0, input.ToX ?? 0, input.ToY ?? 0, (now.Left, now.Top, now.Right, now.Bottom), _cuShotW, _cuShotH) is not { } box)
            return ($"ERROR: the zoom box must be at least 4 pixels each way, inside the last screenshot ({_cuShotW}x{_cuShotH})", null);
        await Task.Delay(50);
        var area = new RECT { Left = box.Left, Top = box.Top, Right = box.Right, Bottom = box.Bottom };
        // the box's corners in screenshot pixels, as the screen box (rounded to whole pixels) really covers them
        var (sw, sh) = (now.Right - now.Left, now.Bottom - now.Top);
        var (x1, y1) = ((box.Left - now.Left) * (double)_cuShotW / sw, (box.Top - now.Top) * (double)_cuShotH / sh);
        var (x2, y2) = ((box.Right - now.Left) * (double)_cuShotW / sw, (box.Bottom - now.Top) * (double)_cuShotH / sh);
        var zoom = ComputerLogic.ZoomScale(box.Right - box.Left, box.Bottom - box.Top);
        var (iw, ih) = (Math.Max(1, (int)Math.Round((box.Right - box.Left) * zoom)), Math.Max(1, (int)Math.Round((box.Bottom - box.Top) * zoom)));
        var (image, _, _) = Capture(area, zoom, g => ComputerLogic.DrawGrid(g, iw, ih, x1, y1, (x2 - x1) / iw));
        return ($"ZOOM of ({x1:0.#}, {y1:0.#}) to ({x2:0.#}, {y2:0.#}) of the last screenshot, {iw}x{ih} px. Its grid is labelled in the last full " +
                $"screenshot's pixels ({_cuShotW}x{_cuShotH}), the ones clicks use: read a point's x and y off the grid", image);
    }

    // A screen rectangle as a JPEG data URL at `scale` (smaller or larger), and its size in pixels.
    private static (string Image, int W, int H) Capture(RECT r, double scale, Action<System.Drawing.Graphics>? overlay = null)
    {
        var (w, h) = (r.Right - r.Left, r.Bottom - r.Top);
        using var full = new System.Drawing.Bitmap(w, h);
        Physical(() =>
        {
            using var g = System.Drawing.Graphics.FromImage(full);
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
            return 0;
        });
        var (iw, ih) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
        using var sized = iw == w && ih == h ? null : new System.Drawing.Bitmap(iw, ih);
        if (sized is not null)
        {
            using var g = System.Drawing.Graphics.FromImage(sized);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(full, 0, 0, iw, ih);
        }
        if (overlay is not null)
        {
            using var g = System.Drawing.Graphics.FromImage(sized ?? full);
            overlay(g);
        }
        using var jpeg = new MemoryStream();
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var quality = new System.Drawing.Imaging.EncoderParameters(1);
        quality.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        (sized ?? full).Save(jpeg, codec, quality);
        return ("data:image/jpeg;base64," + Convert.ToBase64String(jpeg.ToArray()), iw, ih);
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
        if (input.Element is { } number) // a numbered control of the last screenshot: its centre
        {
            if (_cuMarks.FirstOrDefault(m => m.N == number) is not { } mark)
                return _cuMarks.Count == 0 ? "ERROR: the last screenshot numbered no controls (this application does not list them); use x and y"
                    : $"ERROR: there is no element {number} in the last screenshot (1 to {_cuMarks.Count})";
            input = input with { X = mark.X, Y = mark.Y };
        }
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
                    await Task.Delay(ClickHoldMs); // the pointer rests first, so the application sees it hover
                    Pulse(at);
                    SendMouse(down);
                    try
                    {
                        await Task.Delay(ClickHoldMs); // held like a person's click: a game reading input once a frame sees the press
                    }
                    finally
                    {
                        SendMouse(up);
                    }
                }
                return $"{(input.Clicks > 1 ? input.Clicks == 2 ? "DOUBLE-" : "TRIPLE-" : "")}{(input.Button == "left" ? "CLICKED" : input.Button.ToUpperInvariant() + "-CLICKED")} at ({input.X:0.#}, {input.Y:0.#})";
            case "drag":
                Pulse(at);
                SendMouse(0x2);
                _cuDragging = true;
                try
                {
                    await GlideAsync(to, 1.6);
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

    // The pointer glides to the point, easing in and out, so the user can follow it (and hover effects and drags see a
    // path). slower: a factor on the time (a drag moves more deliberately).
    private async Task GlideAsync(POINT to, double slower = 1)
    {
        var start = Physical(() => GetCursorPos(out CursorPoint from) ? new POINT { X = from.X, Y = from.Y } : to);
        var ms = ComputerLogic.GlideMs(Math.Sqrt(Math.Pow(to.X - start.X, 2) + Math.Pow(to.Y - start.Y, 2))) * slower;
        var clock = Stopwatch.StartNew();
        while (!_cuStopped)
        {
            var t = ms <= 0 ? 1 : Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            var e = ComputerLogic.Ease(t);
            var (x, y) = ((int)Math.Round(start.X + (to.X - start.X) * e), (int)Math.Round(start.Y + (to.Y - start.Y) * e));
            Physical(() => SetCursorPos(x, y));
            if (t >= 1)
                break;
            await Task.Delay(10);
        }
    }

    private static readonly Color Ripple = Color.FromRgb(0x00, 0xD8, 0xFF);

    // Where the model clicks, cyan rings spread out from the point and fade, one after another, like a stone thrown into a
    // pond. Click-through, never focused, left out of screenshots.
    private void Pulse(POINT at)
    {
        if (_cuPulse is null)
        {
            var pond = new Grid();
            for (var i = 0; i < 3; i++)
                pond.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 160, Height = 160, StrokeThickness = 3 - i * 0.7, Opacity = 0, Stroke = new SolidColorBrush(Ripple),
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
                });
            pond.Children.Add(new System.Windows.Shapes.Ellipse // the splash at the centre
            {
                Width = 18, Height = 18, Opacity = 0, Fill = new SolidColorBrush(Ripple), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Ripple, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.9 },
            });
            _cuPulse = Overlay(new Window { Width = 170, Height = 170, Content = pond });
        }
        _cuPulse.Show();
        var hwnd = new WindowInteropHelper(_cuPulse).Handle;
        Physical(() => GetWindowRect(hwnd, out var r) &&
            SetWindowPos(hwnd, new IntPtr(-1) /* topmost */, at.X - (r.Right - r.Left) / 2, at.Y - (r.Bottom - r.Top) / 2, 0, 0, 0x1 | 0x10 /* no size, no activate */));
        System.Windows.Media.Animation.DoubleAnimation Anim(double from, double to, double ms, double delay = 0, bool ease = false) =>
            new(from, to, new Duration(TimeSpan.FromMilliseconds(ms))) { BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = ease ? new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } : null };
        var shapes = ((Grid)_cuPulse.Content).Children.Cast<System.Windows.Shapes.Ellipse>().ToList();
        for (var i = 0; i < 3; i++) // the rings: each starts a little later and spreads to the full size as it fades
        {
            var grow = Anim(0.08, 1, 700, i * 140, ease: true);
            var fade = Anim(0.95, 0, 700, i * 140);
            if (i == 2)
                fade.Completed += (_, _) => _cuPulse?.Hide(); // before it starts: a handler added later is never called
            ((ScaleTransform)shapes[i].RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            ((ScaleTransform)shapes[i].RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            shapes[i].BeginAnimation(UIElement.OpacityProperty, fade);
        }
        var splash = Anim(1.4, 0.6, 260, ease: true);
        ((ScaleTransform)shapes[3].RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, splash);
        ((ScaleTransform)shapes[3].RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, splash);
        shapes[3].BeginAnimation(UIElement.OpacityProperty, Anim(1, 0, 320));
    }

    // ---- the model's pointer: a large white arrow that stands in for the system cursor while computer use is on. The
    // system cursors are made blank meanwhile (and restored when it ends, when the app closes, and at the next start after a
    // crash), and the arrow follows the real pointer wherever it is, moved by the model or by the user.

    private Window? _cuPointer;
    private DispatcherTimer? _cuPointerTimer;
    private POINT _cuPointerAt = new() { X = int.MinValue };
    private static bool _cuCursorHidden;
    private static readonly uint[] CursorIds = [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651];
    private static string CursorMarker => Path.Combine(AppSettings.DataRoot, "cursor-hidden");

    [DllImport("user32.dll")] private static extern IntPtr CreateCursor(IntPtr instance, int xHot, int yHot, int width, int height, byte[] and, byte[] xor);
    [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    private void ShowPointer()
    {
        if (_cuPointer is null)
        {
            var art = new Viewbox { Width = ArtSize * PointerScale, Height = ArtSize * PointerScale, Child = PointerArt() };
            _cuPointer = Overlay(new Window { Width = art.Width, Height = art.Height, Content = art });
            _cuPointerTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Render, (_, _) => MovePointer(), Dispatcher);
        }
        if (!_cuPointer.IsVisible)
        {
            _cuPointer.Show();
            _cuPointerAt = new POINT { X = int.MinValue };
        }
        HideSystemCursor();
        MovePointer();
        _cuPointerTimer!.Start();
    }

    private void MovePointer()
    {
        if (_cuPointer is not { IsVisible: true } pointer)
            return;
        var hwnd = new WindowInteropHelper(pointer).Handle;
        var at = Physical(() => GetCursorPos(out CursorPoint p) ? new POINT { X = p.X, Y = p.Y } : _cuPointerAt);
        if (at.X == _cuPointerAt.X && at.Y == _cuPointerAt.Y)
            return;
        _cuPointerAt = at;
        Physical(() =>
        {
            var per = GetWindowRect(hwnd, out var r) ? (r.Right - r.Left) / ArtSize : 1; // screen pixels per unit of the art
            return SetWindowPos(hwnd, new IntPtr(-1), at.X - (int)Math.Round(HotSpot * per), at.Y - (int)Math.Round(HotSpot * per), 0, 0, 0x1 | 0x10); // the tip on the point
        });
    }

    private const int ClickHoldMs = 50; // the rest before a click and the time its button is held
    private const double PointerScale = 2.0; // the art is drawn larger than a normal cursor, so the model's pointer stands out
    private const double ArtSize = 32, HotSpot = 2; // the art's box and its tip, in the units of a 32-pixel cursor

    // The pointer art: the "light" cursor scheme's arrow (a rounded white arrow, a thin dark rim, a soft shadow) redrawn as
    // vectors, so it stays sharp at any size and display scale. A dark shape a little larger than the white one is the rim.
    internal static Canvas PointerArt()
    {
        var shape = Geometry.Parse("M 3,3 L 13.4,8.8 L 8.8,10.4 L 5.4,14.6 Z");
        shape.Freeze();
        var rim = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
        return new Canvas
        {
            Width = ArtSize, Height = ArtSize,
            Children =
            {
                new System.Windows.Shapes.Path { Data = shape, Fill = rim, Stroke = rim, StrokeThickness = 4.4, StrokeLineJoin = PenLineJoin.Round,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3.5, ShadowDepth = 1.4, Direction = 300, Opacity = 0.45 } },
                new System.Windows.Shapes.Path { Data = shape, Fill = Brushes.White, Stroke = Brushes.White, StrokeThickness = 2.8, StrokeLineJoin = PenLineJoin.Round },
            },
        };
    }

    private void HidePointer()
    {
        _cuPointerTimer?.Stop();
        _cuPointer?.Hide();
        RestoreSystemCursor();
    }

    private static void HideSystemCursor()
    {
        if (_cuCursorHidden)
            return;
        try
        {
            File.WriteAllText(CursorMarker, ""); // a crash leaves this, and the next start restores the cursors
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var (and, xor) = (Enumerable.Repeat((byte)0xFF, 128).ToArray(), new byte[128]); // a 32x32 cursor that draws nothing
        foreach (var id in CursorIds)
            SetSystemCursor(CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor), id); // the system takes each cursor
        _cuCursorHidden = true;
    }

    internal static void RestoreSystemCursor(bool force = false)
    {
        if (!_cuCursorHidden && !(force && File.Exists(CursorMarker)))
            return;
        SystemParametersInfo(0x57 /* SPI_SETCURSORS: reload the user's cursor scheme */, 0, IntPtr.Zero, 0);
        _cuCursorHidden = false;
        try
        {
            File.Delete(CursorMarker);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // A borderless, transparent, topmost window that is click-through, never takes focus and is left out of screen captures.
    private static Window Overlay(Window w)
    {
        (w.WindowStyle, w.AllowsTransparency, w.Background, w.Topmost, w.ShowInTaskbar, w.ShowActivated, w.Focusable, w.IsHitTestVisible, w.ResizeMode) =
            (WindowStyle.None, true, Brushes.Transparent, true, false, false, false, false, ResizeMode.NoResize);
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            SetWindowLongPtr(hwnd, ExStyle, new IntPtr((long)GetWindowLongPtr(hwnd, ExStyle) | Transparent | NoActivate | ToolWindow | Layered));
            SetWindowDisplayAffinity(hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
        };
        return w;
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
        HidePointer();
    }

    // "Model is using <app>: press Esc to stop" at the top of the screen: click-through, never focused, left out of screenshots.
    private void ShowBanner()
    {
        if (_cuBanner is null)
        {
            var text = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold };
            _cuBanner = Overlay(new Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 0xC2, 0x2F, 0x34)), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 6, 14, 6), Child = text },
            });
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
        Closed += (_, _) => { HidePointer(); _cuBanner?.Close(); _cuPulse?.Close(); _cuPointer?.Close(); }; // a hidden window would keep the app running
        RestoreSystemCursor(force: true); // the last run crashed while the model's pointer stood in for the cursor
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSystemCursor();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreSystemCursor();
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
            var (zoomed, zw, zh) = Capture(new RECT { Left = 0, Top = 0, Right = 50, Bottom = 20 }, ComputerLogic.ZoomScale(50, 20)); // a zoom is enlarged
            if (zw != 200 || zh != 80 || !zoomed.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal))
                return Fail($"zoom capture {zw}x{zh}");
            // the controls UI Automation reports for this app's own window (it lists its buttons), drawn as numbered boxes on a grid
            var own = new WindowInteropHelper(this).Handle;
            var frame = Bounds(own);
            var controls = await Task.Run(() => Physical(() => Controls(own, frame, false)));
            if (controls.Count == 0 || !controls.Any(c => c.Kind == "button" && c.Name.Length > 0) || controls.Count > MaxMarks ||
                controls.Any(c => c.Box.Left < frame.Left - 2 || c.Box.Bottom > frame.Bottom + 2))
                return Fail($"controls: {controls.Count} ({string.Join(", ", controls.Take(5).Select(c => c.Kind + " " + c.Name))})");
            var (fw, fh) = (frame.Right - frame.Left, frame.Bottom - frame.Top);
            var sketch = controls.Select((c, i) => new Mark(i + 1, c.Kind, c.Name, 0, 0, c.Box)).ToList();
            using (var board = new System.Drawing.Bitmap(Math.Max(1, (int)(fw * ComputerLogic.ShotScale(fw, fh))), Math.Max(1, (int)(fh * ComputerLogic.ShotScale(fw, fh)))))
            {
                using (var g = System.Drawing.Graphics.FromImage(board))
                {
                    g.Clear(System.Drawing.Color.FromArgb(40, 42, 48));
                    ComputerLogic.DrawGrid(g, board.Width, board.Height, 0, 0, 1);
                    DrawMarks(g, sketch, frame, ComputerLogic.ShotScale(fw, fh));
                }
                if (Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_SNAPSHOTS") is { Length: > 0 } snaps)
                    board.Save(Path.Combine(snaps, "computer-marks.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            Pulse(new POINT { X = -20000, Y = -20000 }); // off every screen: it shows, then hides itself when the ring has faded
            if (_cuPulse is not { IsVisible: true })
                return Fail("the click pulse did not show");
            await Task.Delay(1400);
            if (_cuPulse.IsVisible)
                return Fail("the click pulse stayed");
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

using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace AgentOrchestrator;

// The system tray (the app run normally, not a test mode): the window's × and Windows' own Close (Alt+F4, the taskbar)
// hide it to the tray, where its icon opens it again (click) or quits (its menu). The app keeps running meanwhile: its
// models, sharing and the phone app stay up. A close sent by another program (WM_CLOSE alone, as publish.ps1 sends) still
// quits, as does Windows signing out. Start with Windows (Settings > General) starts it hidden in the tray (--tray).
public partial class MainWindow
{
    internal System.Windows.Forms.NotifyIcon? _tray; // null in test modes: closing then closes
    internal static readonly string QuitEvent = $"AgentOrchestrator-Quit-{Environment.UserName}";
    private bool _trayTold; // the first hide this session says where the app went
    private WindowState _trayRestore = WindowState.Normal;

    // Called by App for a normal start; hidden: --tray (Start with Windows).
    internal void InitTray(bool hidden)
    {
        var tray = _tray = _notifier = new System.Windows.Forms.NotifyIcon { Icon = AppIcon(), Text = "Agent Orchestrator", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Agent Orchestrator", null, (_, _) => ShowFromTray());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                ShowFromTray();
        };
        tray.BalloonTipClicked += (_, _) => ShowFromTray();
        Closed += (_, _) => tray.Dispose(); // the tray icon goes with the app
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TrayCloseHook);
        // "Agent Orchestrator.exe --quit" sets this event: the real close, as Quit (a hidden window has no main window to close)
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
        ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, executeOnlyOnce: false);
        Closed += (_, _) => quit.Dispose();
        if (hidden) // shown minimized and unfocused (so it loads), then hidden; the tray restores the saved size
        {
            (_trayRestore, ShowActivated, WindowState) = (WindowState, false, WindowState.Minimized);
            Loaded += (_, _) => Dispatcher.BeginInvoke(() => HideToTray(quiet: true));
        }
    }

    // Windows' own Close (Alt+F4, the taskbar's or the system menu's): WM_SYSCOMMAND SC_CLOSE goes to the tray. A bare WM_CLOSE
    // (another program closing the app) is left alone, so it quits.
    private IntPtr TrayCloseHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int SysCommand = 0x0112, ScClose = 0xF060;
        if (msg == SysCommand && (wParam.ToInt64() & 0xFFF0) == ScClose && _tray is not null)
        {
            handled = true;
            HideToTray();
        }
        return IntPtr.Zero;
    }

    internal void HideToTray(bool quiet = false)
    {
        EndCall(); // the microphone is never left open in the tray
        StopDictation();
        StopSpeaking();
        if (_tray is null)
        {
            Close();
            return;
        }
        if (WindowState != WindowState.Minimized)
            _trayRestore = WindowState;
        Hide();
        if (!quiet && !_trayTold)
            _tray.ShowBalloonTip(4000, "Agent Orchestrator is still running", "It keeps working in the tray. Click its icon to open it, or right-click it to quit.",
                System.Windows.Forms.ToolTipIcon.None);
        _trayTold = true;
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _trayRestore;
        Activate();
    }

    // Quit from the tray: the real close (OnClosing still asks about a running task; a No keeps the app, in the tray).
    private void Quit()
    {
        ShowFromTray(); // its questions need the window
        Close();
    }

    // ---- Start with Windows: the current user's Run key holds "<exe>" --tray while it is on (the key is the truth, not a setting)

    internal string _runKey = @"Software\Microsoft\Windows\CurrentVersion\Run"; // --ui-smoke points it elsewhere
    private const string RunValue = "Agent Orchestrator";

    private bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        return key?.GetValue(RunValue) is string command && command.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
    }

    private void SetStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKey);
        if (on)
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private void InitStartup() => StartupToggle.IsChecked = StartsWithWindows();

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try { SetStartWithWindows(StartupToggle.IsChecked == true); }
        catch (Exception x) when (x is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Toast("Could not change Start with Windows: " + x.Message, "#FF667A");
        }
        StartupToggle.IsChecked = StartsWithWindows(); // what Windows will do
    }

    // --ui-smoke: × and Alt+F4 hide to the tray (the window stays, hidden) and the tray brings it back; Start with Windows writes
    // and removes the Run value ("<exe>" --tray) in a test key.
    internal async Task<bool> TraySmoke(Func<Task> settle)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: tray: {what}"); return false; }
        var (runKey, notifier) = (_runKey, _notifier);
        _runKey = @"Software\AgentOrchestratorSmoke\Run";
        InitTray(hidden: false);
        _trayTold = true; // no balloon from a test
        try
        {
            Close_Click(this, new RoutedEventArgs());
            await settle();
            if (IsVisible || !_tray!.Visible)
                return Fail($"× left the window visible {IsVisible}, tray {_tray!.Visible}");
            ShowFromTray();
            await settle();
            if (!IsVisible)
                return Fail("the tray did not bring the window back");
            var handled = false;
            TrayCloseHook(IntPtr.Zero, 0x0112, new IntPtr(0xF060), IntPtr.Zero, ref handled); // Alt+F4
            await settle();
            if (!handled || IsVisible)
                return Fail("Alt+F4 did not go to the tray");
            ShowFromTray();
            await settle();
            StartupToggle.IsChecked = true;
            Startup_Click(StartupToggle, new RoutedEventArgs());
            using (var key = Registry.CurrentUser.OpenSubKey(_runKey))
                if (key?.GetValue(RunValue) is not string command || command != $"\"{Environment.ProcessPath}\" --tray" || StartupToggle.IsChecked != true)
                    return Fail("Start with Windows did not write the Run value");
            StartupToggle.IsChecked = false;
            Startup_Click(StartupToggle, new RoutedEventArgs());
            using (var key = Registry.CurrentUser.OpenSubKey(_runKey))
                if (key?.GetValue(RunValue) is not null || StartupToggle.IsChecked != false)
                    return Fail("Start with Windows off left the Run value");
            return true;
        }
        finally
        {
            Show();
            _tray?.Dispose();
            (_tray, _notifier, _runKey) = (null, notifier, runKey);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\AgentOrchestratorSmoke", throwOnMissingSubKey: false);
        }
    }
}

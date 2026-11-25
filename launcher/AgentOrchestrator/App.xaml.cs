using System.Windows;

namespace AgentOrchestrator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // --quit: the running app (in the tray or not) closes as its tray's Quit does; exit 1 when none is running
        if (e.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            var found = EventWaitHandle.TryOpenExisting(AgentOrchestrator.MainWindow.QuitEvent, out var quit);
            using (quit)
                quit?.Set();
            Environment.Exit(found ? 0 : 1);
        }
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            LauncherLogic.SelfTest();
            ConversationStore.SelfTest();
            Markdown.SelfTest();
            UsageStats.SelfTest();
            VramEstimate.SelfTest();
            AgentOrchestrator.MainWindow.BrowserSelfTest();
            ComputerLogic.SelfTest();
            AgentOrchestrator.MainWindow.ToolCallSelfTest();
            AgentOrchestrator.MainWindow.RemoteApiSelfTest();
            AgentOrchestrator.MainWindow.SearchSelfTest();
            AgentOrchestrator.MainWindow.SkillCompleteSelfTest();
            AgentOrchestrator.MainWindow.RunLogSelfTest();
            AgentOrchestrator.HubLogic.HubLogicSelfTest();
            AgentOrchestrator.HubDownloader.HubDownloadSelfTest();
            RemoteServer.SelfTest();
            RemoteTunnel.SelfTest();
            ApiKeys.SelfTest();
            AgentOrchestrator.MainWindow.NotifySelfTest();
            ImageGen.ImageGenSelfTest();
            ImageMetadata.SelfTest();
            ModelShare.SelfTest();
            Tailscale.SelfTest();
            AgentOrchestrator.MainWindow.TerminalSelfTest();
            ImageShare.SelfTest();
            WebServer.SelfTest();
            WebEvents.SelfTest();
            Shutdown(0);
            return;
        }

        // --ui-smoke: run the real window off-screen through its templates and animations; exit 1 on any exception.
        // --webapp-smoke: the same window, the phone app's checks only (MainWindow.WebApp.cs WebAppSmoke).
        var webAppSmoke = e.Args.Contains("--webapp-smoke", StringComparer.OrdinalIgnoreCase);
        if (webAppSmoke || e.Args.Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase))
        {
            var data = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AgentOrchestrator-smoke-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA", data);
            var failed = false;
            DispatcherUnhandledException += (_, args) =>
            {
                Console.Error.WriteLine(args.Exception);
                failed = true;
                args.Handled = true;
            };
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            KeepBehind();
            window.Dispatcher.InvokeAsync(async () =>
            {
                var ok = false;
                // An exception here would stay inside InvokeAsync's Task and hang the run, so report it.
                try { ok = webAppSmoke ? await window.WebAppSmoke() : await window.RunUiSmokeAsync(); }
                catch (Exception exception) { Console.Error.WriteLine(exception); }
                window.Close();
                try { System.IO.Directory.Delete(data, recursive: true); } catch (Exception) { } // never let cleanup hang the run
                Environment.Exit(ok && !failed ? 0 : 1); // the generated Main ignores Shutdown's exit code
            });
            return;
        }

        // --screenshots <folder>: the README's pictures (MainWindow.Screenshots.cs), off-screen and behind the user's windows.
        if (Array.FindIndex(e.Args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase)) is var at and >= 0 && at + 1 < e.Args.Length)
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1400, Height = 1060, // tall enough for a whole conversation in one picture
                ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            KeepBehind();
            window.Dispatcher.InvokeAsync(async () =>
            {
                var ok = false;
                try { await window.RunScreenshotsAsync(e.Args[at + 1]); ok = true; }
                catch (Exception exception) { Console.Error.WriteLine(exception); }
                window.Close();
                Environment.Exit(ok ? 0 : 1);
            });
            return;
        }

        // --image-share-loopback <port> [ip]: the window off-screen and behind, with the image share server on 127.0.0.1:<port>, or
        // on <ip> (a Tailscale address) (a live test of MainWindow.Share.cs; the key is AO_SHARE_OWN_KEY). It runs until its process is stopped.
        if (Array.FindIndex(e.Args, a => a.Equals("--image-share-loopback", StringComparison.OrdinalIgnoreCase)) is var share and >= 0 && share + 1 < e.Args.Length &&
            int.TryParse(e.Args[share + 1], out var port))
        {
            var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                WindowState = WindowState.Normal }; // a saved maximized window can't be shown without activating
            window.Show();
            KeepBehind();
            window.StartImageShare(share + 2 < e.Args.Length ? System.Net.IPAddress.Parse(e.Args[share + 2]) : System.Net.IPAddress.Loopback, port);
            return;
        }

        // --image-chat-live <recipe id> <prompt> [main model file]: one real picture through the chat's paint path (a live test of
        // Image Gen's PaintForChatAsync; uses the GPU), the window off-screen. The result line goes to stderr; exit 0 when a
        // picture was saved and the image server was stopped after it.
        if (Array.FindIndex(e.Args, a => a.Equals("--image-chat-live", StringComparison.OrdinalIgnoreCase)) is var live and >= 0 && live + 2 < e.Args.Length)
        {
            var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                WindowState = WindowState.Normal };
            window.Show();
            KeepBehind();
            _ = window.Dispatcher.InvokeAsync(async () =>
                Shutdown(await window.ImageChatLiveAsync(e.Args[live + 1], e.Args[live + 2], live + 3 < e.Args.Length ? e.Args[live + 3] : "") ? 0 : 1));
            return;
        }

        base.OnStartup(e);
        // A UI exception is logged and shown, and the app keeps running; one that ends the process is logged first.
        DispatcherUnhandledException += (_, args) =>
        {
            var path = LogCrash(args.Exception);
            args.Handled = true;
            MessageBox.Show($"Something went wrong: {args.Exception.Message}\n\nDetails were saved to {path}", "Agent Orchestrator",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { LogCrash(args.Exception); args.SetObserved(); };
        var main = new MainWindow { LaunchStartsWebApp = true };
        main.StartWebAppAtLaunch();
        main.InitTray(hidden: e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)); // --tray: Start with Windows
        MainWindow = main;
        main.Show();
    }

    // --ui-smoke runs behind the user's windows: every 100 ms each of this app's windows goes to the bottom of the z-order
    // (not activated), and the foreground goes back to the window the user had when a check (maximize, a dialog) took it.
    private static void KeepBehind()
    {
        var user = GetForegroundWindow();
        var pid = (uint)Environment.ProcessId;
        // Popups (menus, tooltips, pickers) are their own windows, which WPF keeps on a monitor: they go off-screen too,
        // menus and tooltips as they open, any other popup on the next tick.
        const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010;
        static void OffScreen(IntPtr hwnd) => SetWindowPos(hwnd, IntPtr.Zero, -20000, -20000, 0, 0, NoSize | NoZOrder | NoActivate);
        RoutedEventHandler opened = (sender, _) =>
        {
            if (PresentationSource.FromVisual((System.Windows.Media.Visual)sender) is System.Windows.Interop.HwndSource source)
                OffScreen(source.Handle);
        };
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent, opened);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, opened);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var windows = Current.Windows.Cast<Window>().Select(w => new System.Windows.Interop.WindowInteropHelper(w).Handle).ToHashSet();
            EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
            {
                if (!windows.Contains(hwnd) && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var rect) && rect.Left > -10000)
                    OffScreen(hwnd);
                return true;
            }, IntPtr.Zero);
            foreach (Window w in Current.Windows)
                if (new System.Windows.Interop.WindowInteropHelper(w).Handle is var hwnd && hwnd != IntPtr.Zero)
                    SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_BOTTOM; NOSIZE, NOMOVE, NOACTIVATE
            var front = GetForegroundWindow();
            if (user != IntPtr.Zero && front != user && GetWindowThreadProcessId(front, out var owner) != 0 && owner == pid)
                SetForegroundWindow(user);
        };
        timer.Start();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowsProc callback, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // Appends to crash.log in the data folder; returns its path. Never throws: it runs while the app is failing.
    internal static string LogCrash(Exception? exception)
    {
        var path = System.IO.Path.Combine(AppSettings.DataRoot, "crash.log");
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.DataRoot);
            System.IO.File.AppendAllText(path, $"==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n{exception}\n\n");
        }
        catch (Exception) { }
        return path;
    }
}

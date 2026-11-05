using System.Windows;

namespace AgentOrchestrator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            LauncherLogic.SelfTest();
            ConversationStore.SelfTest();
            Markdown.SelfTest();
            UsageStats.SelfTest();
            VramEstimate.SelfTest();
            AgentOrchestrator.MainWindow.BrowserSelfTest();
            AgentOrchestrator.MainWindow.RemoteApiSelfTest();
            AgentOrchestrator.MainWindow.SearchSelfTest();
            AgentOrchestrator.MainWindow.SkillCompleteSelfTest();
            RemoteServer.SelfTest();
            RemoteTunnel.SelfTest();
            Shutdown(0);
            return;
        }

        // --ui-smoke: run the real window off-screen through its templates and animations; exit 1 on any exception.
        if (e.Args.Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase))
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
                try { ok = await window.RunUiSmokeAsync(); }
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
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    // --ui-smoke runs behind the user's windows: every 100 ms each of this app's windows goes to the bottom of the z-order
    // (not activated), and the foreground goes back to the window the user had when a check (maximize, a dialog) took it.
    private static void KeepBehind()
    {
        var user = GetForegroundWindow();
        var pid = (uint)Environment.ProcessId;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            foreach (Window w in Current.Windows)
                if (new System.Windows.Interop.WindowInteropHelper(w).Handle is var hwnd && hwnd != IntPtr.Zero)
                    SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_BOTTOM; NOSIZE, NOMOVE, NOACTIVATE
            var front = GetForegroundWindow();
            if (user != IntPtr.Zero && front != user && GetWindowThreadProcessId(front, out var owner) != 0 && owner == pid)
                SetForegroundWindow(user);
        };
        timer.Start();
    }

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

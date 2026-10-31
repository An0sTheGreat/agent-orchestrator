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

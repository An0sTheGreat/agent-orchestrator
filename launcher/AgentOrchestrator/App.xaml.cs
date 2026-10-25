using System.Windows;

namespace AgentOrchestrator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            LauncherLogic.SelfTest();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }
}

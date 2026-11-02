using System.Windows;

namespace AgentOrchestrator;

// Desktop notifications (Options > Notifications): a Windows notification when a reply has fully finished (no queued
// message left to send) or a project run ends, while the app is not the window in front. Clicking it brings the app back.
public partial class MainWindow
{
    private System.Windows.Forms.NotifyIcon? _notifier;
    private Action<string, string>? _notifyHook; // --ui-smoke: receives the notification instead of Windows

    private void InitNotify() => NotifyToggle.IsChecked = _settings.NotifyWhenDone;

    private void Notify_Click(object sender, RoutedEventArgs e)
    {
        _settings.NotifyWhenDone = NotifyToggle.IsChecked == true;
        _settings.Save();
    }

    private void NotifyDone(string title, string text)
    {
        if (!_settings.NotifyWhenDone)
            return;
        if (_notifyHook is { } hook)
        {
            hook(title, text);
            return;
        }
        if (IsActive && WindowState != WindowState.Minimized)
            return; // the user is looking at it
        if (_notifier is null)
        {
            _notifier = new System.Windows.Forms.NotifyIcon
            {
                Icon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null, Text = "Agent Orchestrator", Visible = true,
            };
            _notifier.BalloonTipClicked += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
            };
            Closed += (_, _) => _notifier.Dispose(); // the tray icon goes with the app
        }
        _notifier.ShowBalloonTip(5000, title, text.Length > 0 ? text : title, System.Windows.Forms.ToolTipIcon.None);
    }
}

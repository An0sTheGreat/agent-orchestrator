using System.IO;
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
            _notifier = new System.Windows.Forms.NotifyIcon { Icon = AppIcon(), Text = "Agent Orchestrator", Visible = true };
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

    // The app's icon from its own resources (Assets\app.ico), at the tray's size for this screen: Windows' icon for the exe
    // comes from the shell's icon cache, which can keep an earlier icon for the same path long after the exe changed.
    internal static System.Drawing.Icon? AppIcon()
    {
        try
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            return stream is null ? null : new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException) { return null; }
    }

    // --self-test: the notification icon loads from the resources and is the current one (purple: blue over green), not the
    // old orange tile (red over blue).
    internal static void NotifySelfTest()
    {
        using var icon = AppIcon() ?? throw new InvalidOperationException("Notify self-test failed: no app icon");
        using var bitmap = icon.ToBitmap();
        long r = 0, g = 0, b = 0, n = 0;
        for (var x = 0; x < bitmap.Width; x++)
            for (var y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(x, y) is { A: > 128 } p)
                    (r, g, b, n) = (r + p.R, g + p.G, b + p.B, n + 1);
        if (n == 0 || b <= g || b <= r * 0.8)
            throw new InvalidOperationException($"Notify self-test failed: icon colour {r / Math.Max(n, 1)},{g / Math.Max(n, 1)},{b / Math.Max(n, 1)}");
    }
}

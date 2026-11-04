using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace AgentOrchestrator;

// --screenshots <folder>: the README's pictures. The caller seeds the data folder (AGENT_ORCHESTRATOR_DATA) with a chat
// and a Code conversation; they are rendered off-screen in several looks, as --ui-smoke's snapshots are.
public partial class MainWindow
{
    // Saves a picture of target (rendered without a screen) as a PNG.
    internal static void SavePicture(FrameworkElement target, string path) => SavePicture(Picture(target, target), path);

    private static RenderTargetBitmap Picture(Visual visual, FrameworkElement size)
    {
        var dpi = VisualTreeHelper.GetDpi(size);
        var bitmap = new RenderTargetBitmap((int)(size.ActualWidth * dpi.DpiScaleX), (int)(size.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void SavePicture(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        encoder.Save(file);
    }

    internal async Task RunScreenshotsAsync(string folder)
    {
        _settingsShow = window => { window.ShowActivated = false; window.Show(); };
        _dialogsQuiet = true;
        static Task Settle() => Task.Delay(800);
        var view = _store.View();
        var entries = view.Ungrouped.Concat(view.Groups.SelectMany(g => g.Items)).ToList();
        var chat = entries.FirstOrDefault(e => e.Kind == "chat");
        var code = entries.FirstOrDefault(e => e.Kind != "chat");
        void Look(string theme, string accent)
        {
            _settings.Theme = theme;
            _settings.ThemeAccents[theme] = accent;
            ApplyTheme();
        }
        void Snap(string name, FrameworkElement? target = null) => SavePicture(target ?? this, Path.Combine(folder, name + ".png"));
        // The browser's page is a WebView2 window, which a WPF picture leaves blank: its own capture is drawn over that spot.
        async Task SnapBrowser(string name)
        {
            if (_web?.CoreWebView2 is not { } core)
                return;
            using var shot = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
            shot.Position = 0;
            var page = BitmapFrame.Create(shot, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var window = Picture(this, this);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(window, new Rect(0, 0, ActualWidth, ActualHeight));
                dc.DrawImage(page, new Rect(_web.TranslatePoint(new Point(), this), new Size(_web.ActualWidth, _web.ActualHeight)));
            }
            SavePicture(Picture(visual, this), Path.Combine(folder, name + ".png"));
        }

        await Settle();
        foreach (var (theme, accent) in new[] { ("ParchmentDark", "Clay"), ("Parchment", "Clay"), ("Graphite", "Silver"), ("Oled", "Purple"), ("Glass", "LightBlue"), ("Gold", "Gold") })
        {
            Look(theme, accent);
            if (chat is not null)
            {
                SetWorkerRail(true);
                OpenChatConversation(chat.Id);
                ShowView(chat: true);
                await Settle();
                Snap("chat-" + theme);
            }
        }
        Look("ParchmentDark", "Clay");
        if (code is not null)
        {
            ShowView();
            OpenCodeConversation(code.Id);
            SetWorkerRail(true);
            await Settle();
            Snap("code");
            SetWorkerRail(false);
            SelectWorker(CardFor("planner"));
            await Settle();
            Snap("workers");
            OpenBrowser("https://github.com/ggml-org/llama.cpp");
            await Task.Delay(8000); // the page loads from the network
            await SnapBrowser("browser");
            CloseBrowser();
            SetWorkerRail(true);
        }
        OpenSettings();
        await Settle();
        if (_settingsWindow is { } settings)
        {
            foreach (var tab in SettingsTabs.Children.OfType<RadioButton>().Where(t => t.Tag as string == "Appearance"))
                tab.IsChecked = true;
            await Settle();
            Snap("settings-appearance", settings);
            settings.Close();
        }
    }
}

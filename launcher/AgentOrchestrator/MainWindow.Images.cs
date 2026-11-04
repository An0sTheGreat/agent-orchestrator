using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentOrchestrator;

// Pictures as thumbnails, as Claude shows them: the composer's attached photos, the photos above a sent message, and the
// model's browser screenshots in its reply (saved in <data>\screenshots, so a reopened conversation shows them too).
// A click shows the picture full size over the window (ImageViewer); a click or Esc closes it.
public partial class MainWindow
{
    private List<string> _turnUserImages = new(); // the photos sent with this turn's message, saved with it
    private List<string> _turnShots = new(); // this turn's browser screenshots, saved with its reply
    private string? _pendingShot; // a screenshot waiting for its browser step, to show under it
    private bool _webHiddenForImage;

    private static BitmapImage? LoadPicture(string path, int decodeHeight = 0)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(File.ReadAllBytes(path)); // never the file itself: a failed decode would keep it open
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeHeight > 0)
                bitmap.DecodePixelHeight = decodeHeight;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or ArgumentException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }

    // A rounded square thumbnail that opens the picture full size; null when the file is gone or not a picture.
    private FrameworkElement? Thumb(string path, double size)
    {
        if (!File.Exists(path) || LoadPicture(path, (int)(size * 2)) is not { } bitmap)
            return null;
        var thumb = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Tag = "Thumb",
            Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }, Cursor = Cursors.Hand, ToolTip = Path.GetFileName(path),
        };
        thumb.SetResourceReference(Border.BorderBrushProperty, "Border");
        thumb.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowImage(path);
        };
        return thumb;
    }

    // Thumbnails in a row (those that still load), or null when none does.
    private WrapPanel? ThumbRow(IEnumerable<string>? paths, double size, HorizontalAlignment align)
    {
        var row = new WrapPanel { HorizontalAlignment = align };
        foreach (var path in paths ?? [])
            if (Thumb(path, size) is { } thumb)
            {
                thumb.Margin = new Thickness(0, 0, 6, 6);
                row.Children.Add(thumb);
            }
        return row.Children.Count > 0 ? row : null;
    }

    internal void ShowImage(string path)
    {
        if (LoadPicture(path) is not { } picture)
        {
            Toast($"Could not open {Path.GetFileName(path)}", "#F7C873");
            return;
        }
        ImageViewerPicture.Source = picture;
        ImageViewer.Visibility = Visibility.Visible;
        // The browser's page is a window of its own, drawn over anything WPF puts on top: hidden while the picture shows
        if (_web is { Visibility: Visibility.Visible } web)
        {
            web.Visibility = Visibility.Hidden;
            _webHiddenForImage = true;
        }
        ImageViewer.Focus();
    }

    private void CloseImage()
    {
        ImageViewer.Visibility = Visibility.Collapsed;
        ImageViewerPicture.Source = null;
        if (_webHiddenForImage && _web is { } web)
            web.Visibility = Visibility.Visible;
        _webHiddenForImage = false;
    }

    private void ImageViewer_MouseUp(object sender, MouseButtonEventArgs e) => CloseImage();

    private void ImageViewer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        CloseImage();
    }

    // A browser screenshot (a data URL) saved as a file for its thumbnail; null when it is not one.
    // ponytail: screenshots are kept for good, as pasted pictures are; a cleanup when a conversation is deleted if they pile up.
    internal static string? SaveShot(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (!dataUrl.StartsWith("data:image/", StringComparison.Ordinal) || comma < 0)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            var folder = Path.Combine(AppSettings.DataRoot, "screenshots");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"shot-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{(dataUrl.StartsWith("data:image/png", StringComparison.Ordinal) ? ".png" : ".jpg")}");
            File.WriteAllBytes(file, bytes);
            return file;
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { return null; }
    }

    // A screenshot the model took during a reply: kept for the reply's saved message and shown under its browser step.
    private void NoteShot(string? dataUrl)
    {
        if (_reply is null || dataUrl is null || SaveShot(dataUrl) is not { } file)
            return;
        _turnShots.Add(file);
        _pendingShot = file;
    }

    private List<string>? TurnShots() => _turnShots.Count > 0 ? _turnShots.ToList() : null;

    private void ShowPendingShot(ChatReply reply)
    {
        if (_pendingShot is not { } file)
            return;
        _pendingShot = null;
        if (ThumbRow([file], 160, HorizontalAlignment.Left) is { } row)
        {
            row.Margin = new Thickness(0, 2, 0, 4);
            reply.Content.Children.Add(row);
        }
    }

    // --ui-smoke: an attached photo is a thumbnail in the composer, the sent bubble shows it (and drops its name from the
    // attachment line), a saved conversation shows the user's photos and the reply's screenshots again, a screenshot is
    // saved as a file, and a thumbnail's click opens the picture full size, Esc closing it.
    private bool ImagesSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: images: {what}"); return false; }
        static int Thumbs(DependencyObject root)
        {
            var n = root is Border { Tag: "Thumb" } ? 1 : 0;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                n += Thumbs(child);
            return n;
        }
        var photo = SavePastedImage(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16));
        var shot = SaveShot("data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(photo)));
        var turn = _turn;
        try
        {
            if (shot is null || !File.Exists(shot) || SaveShot("not a picture") is not null)
                return Fail("the screenshot file");
            Attach(_chat, photo);
            if (Thumbs(ChatAttachList) != 1)
                return Fail("the composer's thumbnail");
            if (BubbleText($"Look{AttachMarker}<image name=\"{Path.GetFileName(photo)}\" />\n</attachments>", [photo]) != "Look")
                return Fail("the bubble's attachment line");
            ClearConversation(_chat);
            Replay(_chat, [new("user", $"Look{AttachMarker}<image name=\"{Path.GetFileName(photo)}\" />\n</attachments>", null, [photo]),
                new("assistant", "Here it is.", null, [shot])], "planner");
            if (Thumbs(_chat.Panel) != 2)
                return Fail($"{Thumbs(_chat.Panel)} thumbnails in the reopened conversation");
            ShowImage(photo);
            if (ImageViewer.Visibility != Visibility.Visible || ImageViewerPicture.Source is null)
                return Fail("the viewer did not open");
            ImageViewer_KeyDown(ImageViewer, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            if (ImageViewer.Visibility != Visibility.Collapsed || ImageViewerPicture.Source is not null)
                return Fail("Esc did not close the viewer");
            return true;
        }
        finally
        {
            CloseImage();
            _turn = turn;
            _chatAttachments.Clear();
            RefreshAttachments(_chat);
            ClearConversation(_chat);
            foreach (var file in new[] { photo, shot })
                if (file is not null)
                    File.Delete(file);
        }
    }
}

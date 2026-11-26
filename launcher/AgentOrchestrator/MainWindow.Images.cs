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
                                      or System.Runtime.InteropServices.COMException or InvalidOperationException or FormatException) { return null; } // FileFormatException: a file that is not a picture
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
            DataContext = path, // read back by the click for the row's set
        };
        thumb.SetResourceReference(Border.BorderBrushProperty, "Border");
        DragOut(thumb, () => path);
        thumb.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // the set is read from the row now: pictures may have joined it since it was built
            var row = (thumb.Parent as WrapPanel)?.Children.OfType<FrameworkElement>().Where(c => c.DataContext is string).ToList();
            ShowImage(path, row?.Select(c => (string)c.DataContext).ToList(), row?.IndexOf(thumb) ?? -1); // this copy's place, when a file is in the row twice
        };
        return thumb;
    }

    // A picture dragged out of the app (to a folder, the desktop, another app) as its file: a copy, the original stays. A drag
    // starts past the system's drag distance, so a plain click still opens or picks the picture.
    private static void DragOut(FrameworkElement source, Func<string?> path)
    {
        Point? down = null;
        source.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(source);
        source.PreviewMouseLeftButtonUp += (_, _) => down = null;
        source.MouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed)
                return;
            var at = e.GetPosition(source);
            if (Math.Abs(at.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(at.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            down = null;
            if (path() is { } file && File.Exists(file))
                DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, new[] { file }), DragDropEffects.Copy);
        };
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
        // generated pictures (the gallery's files) are the reply's result, not a working step: View thinking off never folds them
        if (paths?.All(p => Path.GetFullPath(p).StartsWith(Path.GetFullPath(ImageGen.ImagesFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) == true)
            row.Tag = PictureRow;
        return row.Children.Count > 0 ? row : null;
    }

    private List<string> _viewSet = [];
    private int _viewIndex;

    // The picture full size; `set` is the thumbnail row it belongs to, for Left/Right (null or one picture: no arrows).
    internal void ShowImage(string path, IReadOnlyList<string>? set = null, int index = -1)
    {
        if (LoadPicture(path) is not { } picture)
        {
            Toast($"Could not open {Path.GetFileName(path)}", "#F7C873");
            return;
        }
        _viewSet = set?.ToList() ?? [];
        _viewIndex = index >= 0 && index < _viewSet.Count && _viewSet[index] == path ? index : _viewSet.IndexOf(path); // the clicked copy when a file is in the row twice
        if (_viewIndex < 0)
            _viewSet = [];
        ShowViewCount();
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

    private void ShowViewCount()
    {
        ImageViewerCount.Text = $"{_viewIndex + 1} / {_viewSet.Count}";
        ImageViewerCount.Visibility = _viewSet.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ImageViewer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            var next = _viewIndex + (e.Key == Key.Right ? 1 : -1);
            // stops at the ends; a picture that no longer loads leaves the current one showing
            if (_viewSet.Count > 1 && next >= 0 && next < _viewSet.Count && LoadPicture(_viewSet[next]) is { } picture)
            {
                _viewIndex = next;
                ImageViewerPicture.Source = picture;
                ShowViewCount();
            }
            return;
        }
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
        NoteTurnPicture(file);
    }

    // A picture file joining the reply's (shown under its step, saved with its message): a screenshot, or a generated picture
    // referenced in the gallery itself (its Delete removes it everywhere; a reopened conversation skips a picture that is gone).
    private void NoteTurnPicture(string file)
    {
        if (_reply is null)
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
        // a reply's generated pictures share one row: the next joins the row of the one before
        if (reply.Content.Children.OfType<WrapPanel>().LastOrDefault(p => p.Tag as string == PictureRow) is { } pictures &&
            ThumbRow([file], 160, HorizontalAlignment.Left) is { Tag: PictureRow } one)
        {
            var thumb = one.Children[0];
            one.Children.Clear();
            pictures.Children.Add(thumb);
            return;
        }
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
            // Left/Right step through the pictures of one thumbnail row, stopping at its ends; one picture ignores them
            static void Press(MainWindow w, Key key) =>
                w.ImageViewer_KeyDown(w.ImageViewer, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
            var temps = new[] { 0, 1, 2 }.Select(_ => Path.Combine(Path.GetTempPath(), $"smoke-set-{Guid.NewGuid():N}.png")).ToList();
            try
            {
                foreach (var t in temps)
                    File.Copy(photo, t);
                var setRow = ThumbRow(temps, 64, HorizontalAlignment.Left)!;
                setRow.Children[1].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                string Seen() => ImageViewerCount.Visibility == Visibility.Visible ? ImageViewerCount.Text : "-";
                var seen = new List<string> { Seen() };
                Press(this, Key.Right); seen.Add(Seen());
                Press(this, Key.Right); seen.Add(Seen());
                Press(this, Key.Left); Press(this, Key.Left); seen.Add(Seen());
                Press(this, Key.Left); seen.Add(Seen());
                if (string.Join(",", seen) != "2 / 3,3 / 3,3 / 3,1 / 3,1 / 3")
                    return Fail($"arrows through a row of three: {string.Join(",", seen)}");
                Press(this, Key.Escape);
                ShowImage(photo);
                Press(this, Key.Right); Press(this, Key.Left);
                if (ImageViewer.Visibility != Visibility.Visible || ImageViewerCount.Visibility == Visibility.Visible)
                    return Fail("a single picture's viewer reacted to the arrows");
                Press(this, Key.Escape);
                if (ImageViewer.Visibility != Visibility.Collapsed)
                    return Fail("Esc did not close the viewer after the arrows");
            }
            finally
            {
                CloseImage();
                foreach (var t in temps)
                    File.Delete(t);
            }
            // a picture's reply (its details line, for the model) shows no text: the picture is the reply
            var made1 = _reply = AddReply("planner");
            SetChatFinal(ImageGen.ChatResult("m", new ImageSettings { Prompt = "p" }, 1, 1));
            if (made1.Answer is not null || made1.Final is null)
                return Fail("a picture's reply showed its details line");
            // the chat's picture while it is made: the dots, "Making a picture..." at its top and the percentage; the status line hidden; gone once it ends
            var reply = _reply = AddReply("planner");
            ShowPendingPicture(1024, 1536);
            PendingPictureProgress("Step 7/8 · 1.2 s/it…", 0.88);
            SetChatStatus("Making a picture… Step 7/8");
            if (_pendingPicture is not var (pending, percent, status, stepLine, _) || !reply.Content.Children.Contains(pending) || percent.Text != "88%" ||
                stepLine.Text != "Step 7/8 · 1.2 s/it" || stepLine.VerticalAlignment != VerticalAlignment.Bottom || stepLine.HorizontalAlignment != HorizontalAlignment.Left ||
                !status.Text.StartsWith("Making a picture.", StringComparison.Ordinal) || status.VerticalAlignment != VerticalAlignment.Top || reply.Status.Visibility == Visibility.Visible)
                return Fail("the picture's placeholder");
            // a step preview takes the dots' place on the card, the text still over it; the stage is left alone meanwhile
            var stageBefore = _imgResult?.Source;
            ShowImagePreview(File.ReadAllBytes(photo));
            if (SmokeFind<Image>(pending, i => i.Tag as string == "PendingPicturePreview") is not { Source: not null } cardFrame || cardFrame.Parent is not Grid cardGrid ||
                cardGrid.Children[0].Visibility != Visibility.Collapsed || cardGrid.Children.IndexOf(cardFrame) >= cardGrid.Children.IndexOf(status) || _imgStagePreview ||
                (_imgResult is not null && _imgResult.Source != stageBefore))
                return Fail("the step preview on the picture's card");
            RemovePendingPicture();
            _reply = null;
            if (reply.Content.Children.Contains(pending) || _pendingPicture is not null)
                return Fail("the placeholder stayed after the picture");
            // View thinking off: a generated picture (a gallery file) stays in view; a browser screenshot folds with its step
            var view = _settings.ViewThinking;
            Directory.CreateDirectory(ImageGen.ImagesFolder);
            var made = Path.Combine(ImageGen.ImagesFolder, $"smoke-thinking-{Guid.NewGuid():N}.png");
            File.Copy(photo, made);
            try
            {
                var steps = AddReply("planner");
                steps.Content.Children.Add(BuildToolExpander("browser", "x", new TextBlock(), out _));
                var shotRow = ThumbRow([shot!], 160, HorizontalAlignment.Left)!;
                var madeRow = ThumbRow([made], 160, HorizontalAlignment.Left)!;
                steps.Content.Children.Add(shotRow);
                steps.Content.Children.Add(madeRow);
                _settings.ViewThinking = false;
                Regroup(steps.Content);
                if (madeRow.Visibility != Visibility.Visible || shotRow.Visibility != Visibility.Collapsed)
                    return Fail($"View thinking off: picture {madeRow.Visibility}, screenshot {shotRow.Visibility}");
                // several pictures in one reply share one row, past the steps between them; a screenshot keeps its own
                var several = _reply = AddReply("planner");
                foreach (var file in new[] { made, shot!, made, made })
                {
                    several.Content.Children.Add(BuildToolExpander(file == made ? "generate_image" : "browser", "x", new TextBlock(), out _));
                    _pendingShot = file;
                    ShowPendingShot(several);
                }
                _reply = null;
                var rows = several.Content.Children.OfType<WrapPanel>().ToList();
                if (rows.Count != 2 || rows[0].Tag as string != PictureRow || rows[0].Children.Count != 3 || rows[1].Children.Count != 1)
                    return Fail($"several pictures: {string.Join(", ", rows.Select(r => $"{r.Tag}:{r.Children.Count}"))}");
            }
            finally
            {
                _settings.ViewThinking = view;
                File.Delete(made);
            }
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

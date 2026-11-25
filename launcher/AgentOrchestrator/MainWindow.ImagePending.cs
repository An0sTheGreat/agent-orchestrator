using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AgentOrchestrator;

// The chat's picture while it is made (generate_image, /image): a field of dots in the picture's shape that light in slow drifting
// glows, "Making a picture" in its upper left (its dots counting . .. ... until the picture comes), the current step in its lower
// left (Loading model…, Step 5/8…) and the percentage in its lower right; once the generation sends step previews, the latest takes the dots' place. The reply's status line stays hidden meanwhile (SetChatStatus): the card says it. The picture replaces it.
public partial class MainWindow
{
    private (FrameworkElement Root, TextBlock Percent, TextBlock Status, TextBlock Step, DispatcherTimer Timer)? _pendingPicture;

    private void ShowPendingPicture(int width, int height)
    {
        RemovePendingPicture();
        if (_reply is null)
            return;
        const double w = 256, step = 11;
        var h = Math.Clamp(w * height / Math.Max(1.0, width), 144, 320); // a tall picture's field stops at 320: still upright, not a tower
        var canvas = new Canvas { Width = w, Height = h };
        var dots = new List<(Ellipse Dot, double X, double Y)>();
        for (var y = step / 2; y < h; y += step)
            for (var x = step / 2; x < w; x += step)
            {
                var dot = new Ellipse { Width = 2.6, Height = 2.6, Opacity = 0.1 };
                dot.SetResourceReference(Shape.FillProperty, "Accent");
                Canvas.SetLeft(dot, x - 1.3);
                Canvas.SetTop(dot, y - 1.3);
                canvas.Children.Add(dot);
                dots.Add((dot, x / w, y / h));
            }
        var percent = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 2, 8, 2) };
        percent.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var pill = new Border { Child = percent, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8), Visibility = Visibility.Collapsed };
        pill.SetResourceReference(Border.BackgroundProperty, "Hover");
        var status = new TextBlock { Text = "Making a picture.", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 10, 0, 0), Tag = "PendingPictureStatus" };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var stepText = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, MaxWidth = w - 84,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 11), Tag = "PendingPictureStep" }; // clear of the percentage
        stepText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var field = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), ClipToBounds = true,
            Child = new Grid { Children = { canvas, status, stepText, pill } }, Tag = "PendingPicture" };
        field.SetResourceReference(Border.BackgroundProperty, "Card");
        field.SetResourceReference(Border.BorderBrushProperty, "Border");
        var root = new StackPanel { Margin = new Thickness(0, 2, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, Children = { field } };
        static double Glow(double x, double y, double cx, double cy) => Math.Exp(-((x - cx) * (x - cx) + (y - cy) * (y - cy)) / 0.035);
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var t = clock.Elapsed.TotalSeconds; // two soft glows drifting on slow loops, a faint shimmer across the field
            var (ax, ay) = (0.5 + 0.32 * Math.Sin(t * 0.7), 0.5 + 0.3 * Math.Sin(t * 0.9 + 1));
            var (bx, by) = (0.5 + 0.32 * Math.Cos(t * 0.55 + 2), 0.5 + 0.3 * Math.Cos(t * 0.8));
            foreach (var (dot, x, y) in dots)
                dot.Opacity = 0.08 + 0.8 * Math.Min(1, Glow(x, y, ax, ay) + Glow(x, y, bx, by)) * (0.65 + 0.35 * Math.Sin(t * 3 + (x + y) * 9));
            status.Text = "Making a picture" + new string('.', 1 + (int)(t * 2.5) % 3); // . .. ... and again, 0.4 s each
        };
        timer.Start();
        _reply.Content.Children.Add(root);
        _reply.Status.Visibility = Visibility.Collapsed; // the card says the step
        _pendingPicture = (root, percent, status, stepText, timer);
    }

    // The generation's progress (ImageStatus): its step text, and the percentage once there is one.
    private void PendingPictureProgress(string text, double? progress)
    {
        if (_pendingPicture is not var (_, percent, _, stepText, _))
            return;
        stepText.Text = text.TrimEnd('…', '.', ' ');
        if (progress is { } p && percent.Parent is Border pill)
        {
            percent.Text = $"{Math.Clamp(p, 0, 1) * 100:0}%";
            pill.Visibility = Visibility.Visible;
        }
    }

    // A step preview on the card: it takes the dots' place (the text stays over it). False when no picture is being made here.
    private bool PendingPicturePreview(BitmapSource frame)
    {
        if (_pendingPicture is not var (root, _, _, _, _) || root is not Panel { Children: [Border { Child: Grid grid }, ..] })
            return false;
        if (grid.Children.OfType<Image>().FirstOrDefault() is not { } shown)
        {
            shown = new Image { Stretch = Stretch.UniformToFill, Tag = "PendingPicturePreview" };
            grid.Children.Insert(1, shown); // over the dots, under the text
            grid.Children[0].Visibility = Visibility.Collapsed;
        }
        shown.Source = frame;
        return true;
    }

    private void RemovePendingPicture()
    {
        if (_pendingPicture is not var (root, _, _, _, timer))
            return;
        _pendingPicture = null;
        timer.Stop();
        (root.Parent as Panel)?.Children.Remove(root);
    }
}

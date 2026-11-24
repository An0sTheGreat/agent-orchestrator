using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AgentOrchestrator;

// The chat's picture while it is made (generate_image, /image), under the reply's status line ("Making a picture… Step 3/8 · …"):
// a field of dots in the picture's shape that light in slow drifting glows, with the percentage in its corner. The picture replaces it.
public partial class MainWindow
{
    private (FrameworkElement Root, TextBlock Percent, DispatcherTimer Timer)? _pendingPicture;

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
        var field = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), ClipToBounds = true,
            Child = new Grid { Children = { canvas, pill } }, Tag = "PendingPicture" };
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
        };
        timer.Start();
        _reply.Content.Children.Add(root);
        _pendingPicture = (root, percent, timer);
    }

    // The generation's progress (ImageStatus): the percentage, once there is one.
    private void PendingPictureProgress(double? progress)
    {
        if (_pendingPicture is not var (_, percent, _))
            return;
        if (progress is { } p && percent.Parent is Border pill)
        {
            percent.Text = $"{Math.Clamp(p, 0, 1) * 100:0}%";
            pill.Visibility = Visibility.Visible;
        }
    }

    private void RemovePendingPicture()
    {
        if (_pendingPicture is not var (root, _, timer))
            return;
        _pendingPicture = null;
        timer.Stop();
        (root.Parent as Panel)?.Children.Remove(root);
    }
}

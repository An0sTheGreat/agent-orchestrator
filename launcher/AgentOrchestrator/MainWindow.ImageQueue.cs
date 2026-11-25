using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AgentOrchestrator;

// The Images page's queue: Generate adds its pictures (PicturesToMake) and the queue's job (RunQueueAsync) makes them one after
// another. While pictures wait, Generate narrows and a button the same size slides out beside it with their count; hovering it
// drops a list of them, one row each with a bar at 0% and a red ✕ that takes that one out (Cancel still stops everything).
public partial class MainWindow
{
    private sealed record QueuedPicture(ImageSettings Run);

    private readonly List<QueuedPicture> _imgQueue = [];
    private Button _imgQueueButton = null!;
    private TextBlock _imgQueueText = null!;
    private Grid _imgQueueRow = null!;
    internal Popup? _imgQueuePopup; // the hover list (--ui-smoke)
    private StackPanel _imgQueueList = null!;
    private bool _imgQueueShown; // the button is out (or sliding out)
    private const double QueueGap = 8;

    private async Task GenerateClickAsync()
    {
        if (PicturesToMake() is not { } pictures)
            return;
        _imgQueue.AddRange(pictures);
        RefreshQueue();
        if (!ImageBusy)
            await RunImageJob(RunQueueAsync);
    }

    // [Generate][queue]: Generate takes the room the queue button leaves; the button's width slides between 0 and half the row.
    private Grid QueueRow()
    {
        _imgQueueText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var glyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        _imgQueueButton = new Button
        {
            Tag = "ImagesQueue", Padding = _imgGenerate.Padding, Width = 0, Opacity = 0, Margin = new Thickness(0), Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Center, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, _imgQueueText } },
            ToolTip = null, Focusable = true, ClipToBounds = true,
        };
        System.Windows.Automation.AutomationProperties.SetName(_imgQueueButton, "Queued pictures");
        Grid.SetColumn(_imgQueueButton, 1);
        _imgQueueRow = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { _imgGenerate, _imgQueueButton } };
        _imgQueueRow.SizeChanged += (_, _) =>
        {
            if (_imgQueueShown && _imgQueueButton.HasAnimatedProperties is false)
                _imgQueueButton.Width = QueueWidth();
        };

        // the hover list: it opens on the button and stays while the mouse is on either; leaving both closes it shortly after
        _imgQueueList = new StackPanel();
        var card = new Border { Child = _imgQueueList, BorderThickness = new Thickness(1), Padding = new Thickness(6), MinWidth = 260 };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        var popup = _imgQueuePopup = new Popup { Child = card, PlacementTarget = _imgQueueButton, Placement = PlacementMode.Top, VerticalOffset = -6, AllowsTransparency = true,
            StaysOpen = true, PopupAnimation = PopupAnimation.Fade };
        var closing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        closing.Tick += (_, _) =>
        {
            closing.Stop();
            if (!_imgQueueButton.IsMouseOver && !card.IsMouseOver)
                popup.IsOpen = false;
        };
        _imgQueueButton.MouseEnter += (_, _) => OpenQueueList();
        _imgQueueButton.Click += (_, _) => OpenQueueList(); // touch and keyboard
        _imgQueueButton.MouseLeave += (_, _) => closing.Start();
        card.MouseLeave += (_, _) => closing.Start();
        return _imgQueueRow;
    }

    private double QueueWidth() => Math.Max(0, (_imgQueueRow.ActualWidth - QueueGap) / 2);

    private void OpenQueueList()
    {
        if (_imgQueue.Count == 0)
            return;
        ShowQueueRows();
        if (_imgQueuePopup is { } popup)
        {
            ((Border)popup.Child).Width = Math.Max(260, _imgQueueButton.ActualWidth);
            popup.IsOpen = true;
        }
    }

    // The count on the button, the button sliding out or back, and the open list's rows.
    private void RefreshQueue()
    {
        if (_imgQueueButton is null)
            return;
        var count = _imgQueue.Count;
        if (count > 0)
            _imgQueueText.Text = count == 1 ? "1 queued" : $"{count} queued";
        if (count > 0 != _imgQueueShown)
            SlideQueueButton(count > 0);
        if (_imgQueuePopup is { IsOpen: true } popup)
        {
            if (count == 0)
                popup.IsOpen = false;
            else
                ShowQueueRows();
        }
    }

    private void SlideQueueButton(bool show)
    {
        _imgQueueShown = show;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(260);
        var (width, gap, opacity) = show ? (QueueWidth(), QueueGap, 1.0) : (0.0, 0.0, 0.0);
        if (show)
            _imgQueueButton.Visibility = Visibility.Visible;
        var grow = new DoubleAnimation(_imgQueueButton.ActualWidth, width, time) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        grow.Completed += (_, _) =>
        {
            _imgQueueButton.Width = _imgQueueShown ? QueueWidth() : 0; // held as a plain value, so a resize can set it
            if (!_imgQueueShown)
                _imgQueueButton.Visibility = Visibility.Collapsed;
        };
        _imgQueueButton.Width = width; // where it ends (the animation runs over it, then stops)
        _imgQueueButton.Margin = new Thickness(gap, 0, 0, 0);
        _imgQueueButton.Opacity = opacity;
        _imgQueueButton.BeginAnimation(WidthProperty, grow);
        _imgQueueButton.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(show ? 0 : QueueGap, 0, 0, 0), new Thickness(gap, 0, 0, 0), time) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        _imgQueueButton.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 0 : 1, opacity, time) { FillBehavior = FillBehavior.Stop });
    }

    // One row a waiting picture: what it is (the prompt's start, its seed), a bar at 0% and a red ✕ that takes it out of the queue.
    private void ShowQueueRows()
    {
        _imgQueueList.Children.Clear();
        foreach (var picture in _imgQueue)
        {
            var run = picture.Run;
            var prompt = string.Join(' ', run.Prompt.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var label = new TextBlock { Text = (run.RemoteModel.Length > 0 && run.Batch > 1 ? $"{run.Batch} pictures · " : "") + prompt + (run.RemoteModel.Length > 0 ? "" : $" · seed {run.Seed}"),
                FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 0, 4) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 5, VerticalAlignment = VerticalAlignment.Center };
            var percent = new TextBlock { Text = "0%", FontSize = 11, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            percent.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var remove = new Button
            {
                Content = "✕", Tag = "ImagesQueueCancel", Padding = new Thickness(6, 0, 6, 0), FontSize = 11, MinWidth = 0, Focusable = false,
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Take this picture out of the queue",
            };
            remove.SetResourceReference(StyleProperty, "GhostButton");
            remove.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A));
            remove.Click += (_, _) =>
            {
                _imgQueue.Remove(picture);
                RefreshQueue();
            };
            Grid.SetColumn(percent, 1);
            Grid.SetColumn(remove, 2);
            var line = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } },
                Children = { bar, percent, remove },
            };
            _imgQueueList.Children.Add(new StackPanel { Tag = "ImagesQueueRow", Margin = new Thickness(6, 5, 2, 5), Children = { label, line } });
        }
    }
}

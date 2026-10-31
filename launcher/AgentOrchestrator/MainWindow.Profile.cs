using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace AgentOrchestrator;

// The sidebar's profile: picture, name and lifetime usage (UsageStats), counted from every model call's usage event.
public partial class MainWindow
{
    private readonly UsageStats _stats = UsageStats.Load();
    private readonly Stopwatch _statsTask = new(); // the chat turn or project run in progress, for Longest task
    private Popup? _profilePopup;

    private string ProfileName => _settings.ProfileName.Trim() is { Length: > 0 } n ? n : Environment.UserName;

    // A model call's prompt + generated tokens.
    private void RecordUsage(long tokens)
    {
        _stats.Add(tokens, DateTime.Now);
        _stats.Save();
        RefreshProfile();
    }

    // busy: a chat turn or project run starts; otherwise one ends and its time may be the new longest.
    private void TimeTask(bool busy)
    {
        if (busy)
            _statsTask.Restart();
        else if (_statsTask.IsRunning)
        {
            _statsTask.Stop();
            _stats.AddTask(_statsTask.Elapsed);
            _stats.Save();
        }
    }

    private void RefreshProfile()
    {
        ProfileNameText.Text = ProfileName;
        ProfileTokensText.Text = $"{UsageStats.Short(_stats.Lifetime)} lifetime tokens";
        ProfileAvatar.Content = Avatar(30);
        RailAvatar.Content = Avatar(26);
        StatsSummary.Text = $"{UsageStats.Short(_stats.Lifetime)} tokens · longest task {UsageStats.Duration(_stats.LongestTaskSeconds)}";
    }

    // The picture in a circle, or the name's initial on the accent colour.
    private FrameworkElement Avatar(double size)
    {
        var circle = new Ellipse { Width = size, Height = size };
        if (LoadPicture() is { } picture)
        {
            circle.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
            return circle;
        }
        circle.SetResourceReference(Shape.FillProperty, "Accent");
        var initial = new TextBlock { Text = ProfileName[..1].ToUpperInvariant(), FontSize = size * 0.45, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return new Grid { Width = size, Height = size, Children = { circle, initial } };
    }

    private BitmapImage? _picture;
    private string? _pictureFrom;

    private BitmapImage? LoadPicture()
    {
        var path = _settings.ProfilePicture;
        if (path == _pictureFrom)
            return _picture;
        _pictureFrom = path;
        _picture = null;
        if (path.Length == 0 || !File.Exists(path))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read now: the file is not kept open
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 160;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            _picture = image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        return _picture;
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        _profilePopup = ProfilePopup((FrameworkElement)sender);
        _profilePopup.IsOpen = true;
    }

    private Popup ProfilePopup(FrameworkElement anchor)
    {
        TextBlock Text(string text, string brush = "TextPrimary", double size = 12, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextAlignment = TextAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Top, VerticalOffset = -6, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
        var panel = new StackPanel { Width = 300 };

        var avatar = Avatar(64);
        avatar.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(avatar);
        var pictureRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
        Button Link(string label, Action click)
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 3, 8, 3), FontSize = 11, Margin = new Thickness(2, 0, 2, 0) };
            b.SetResourceReference(StyleProperty, "GhostButton");
            b.Click += (_, _) => { popup.IsOpen = false; click(); };
            return b;
        }
        pictureRow.Children.Add(Link("Change picture…", ChoosePicture));
        if (_settings.ProfilePicture.Length > 0)
            pictureRow.Children.Add(Link("Remove", () => SetPicture(null)));
        panel.Children.Add(pictureRow);

        var name = new TextBox { Text = ProfileName, FontSize = 15, FontWeight = FontWeights.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(40, 0, 40, 12), ToolTip = "Your name (empty: the Windows user name)" };
        AutomationProperties.SetName(name, "Profile name");
        panel.Children.Add(name);

        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (value, label) in new[]
        {
            (UsageStats.Short(_stats.Lifetime), "Lifetime tokens"), (UsageStats.Short(_stats.Peak), "Peak tokens"),
            (UsageStats.Duration(_stats.LongestTaskSeconds), "Longest task"), (Days(_stats.LongestStreak), "Longest streak"),
            (Days(_stats.CurrentStreak(DateTime.Today)), "Current streak"), (_stats.Daily.Count(d => d.Value > 0).ToString(), "Active days"),
        })
        {
            var tile = new StackPanel { Margin = new Thickness(4, 6, 4, 6) };
            tile.Children.Add(Text(value, size: 15, weight: FontWeights.SemiBold));
            tile.Children.Add(Text(label, "Muted", 11));
            tiles.Children.Add(tile);
        }
        var tilesCard = new Border { Child = tiles, BorderThickness = new Thickness(1), Padding = new Thickness(4) };
        tilesCard.SetResourceReference(Border.BorderBrushProperty, "Border");
        tilesCard.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        panel.Children.Add(tilesCard);

        var activity = Text("Token activity · last 26 weeks", "Muted", 11);
        activity.TextAlignment = TextAlignment.Left;
        activity.Margin = new Thickness(2, 12, 0, 6);
        panel.Children.Add(activity);
        panel.Children.Add(Heatmap(26));

        var card = new Border { Child = panel, Padding = new Thickness(16, 16, 16, 14), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
        popup.Child = card;
        popup.Closed += (_, _) =>
        {
            if (name.Text.Trim() == ProfileName)
                return;
            _settings.ProfileName = name.Text.Trim();
            _settings.Save();
            RefreshProfile();
        };
        name.KeyDown += (_, k) => { if (k.Key is Key.Enter or Key.Escape) popup.IsOpen = false; };
        return popup;
    }

    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";

    // One square per day, a column per week (oldest left), shaded by that day's share of the busiest day shown.
    private FrameworkElement Heatmap(int weeks)
    {
        var grid = new UniformGrid { Rows = 7, Columns = weeks, HorizontalAlignment = HorizontalAlignment.Left };
        var today = DateTime.Today;
        var start = today.AddDays(-(int)today.DayOfWeek - 7 * (weeks - 1)); // a Sunday
        var max = Math.Max(1, Enumerable.Range(0, weeks * 7).Select(i => _stats.Daily.GetValueOrDefault(UsageStats.Key(start.AddDays(i)))).Max());
        for (var row = 0; row < 7; row++)
            for (var col = 0; col < weeks; col++)
            {
                var day = start.AddDays(col * 7 + row);
                var tokens = _stats.Daily.GetValueOrDefault(UsageStats.Key(day));
                var cell = new Border { Width = 8, Height = 8, Margin = new Thickness(1.25), CornerRadius = new CornerRadius(2),
                    ToolTip = $"{day:ddd d MMM yyyy}: {tokens:N0} tokens", Visibility = day > today ? Visibility.Hidden : Visibility.Visible };
                if (tokens > 0)
                {
                    cell.SetResourceReference(Border.BackgroundProperty, "Accent");
                    cell.Opacity = 0.3 + 0.7 * tokens / max;
                }
                else
                    cell.SetResourceReference(Border.BackgroundProperty, "Hover");
                grid.Children.Add(cell);
            }
        return grid;
    }

    private void ChoosePicture()
    {
        var dialog = new OpenFileDialog { Title = "Choose a profile picture", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
            SetPicture(dialog.FileName);
    }

    // Copies the picture into the data folder, so moving or deleting the original keeps it; null removes it.
    private void SetPicture(string? source)
    {
        try
        {
            foreach (var old in Directory.Exists(AppSettings.DataRoot) ? Directory.GetFiles(AppSettings.DataRoot, "avatar.*") : Array.Empty<string>())
                File.Delete(old);
            var path = "";
            if (source is not null)
            {
                path = System.IO.Path.Combine(AppSettings.DataRoot, "avatar" + System.IO.Path.GetExtension(source).ToLowerInvariant());
                Directory.CreateDirectory(AppSettings.DataRoot);
                File.Copy(source, path, overwrite: true);
            }
            _settings.ProfilePicture = path;
            _settings.Save();
            _pictureFrom = null; // reload even when the path is the same
            if (path.Length > 0 && LoadPicture() is null)
            {
                Toast("That file isn't a picture this app can show", "#FF667A");
                SetPicture(null);
                return;
            }
            RefreshProfile();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Couldn't save the picture: {e.Message}", "#FF667A");
        }
    }

    private void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        if (SidebarAsk("Reset usage stats", "Set lifetime tokens, peak tokens, longest task and streaks back to zero? This can't be undone.", "Reset") is null)
            return;
        _stats.Lifetime = 0;
        _stats.Daily.Clear();
        _stats.LongestTaskSeconds = 0;
        _stats.Save();
        RefreshProfile();
    }
}

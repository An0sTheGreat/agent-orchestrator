using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace AgentOrchestrator;

// Shared frame of every dialog: borderless window with a slim title bar like the main window's (glyph, title,
// close button, drag to move), themed through the owner's resources. Esc closes it (the same as Cancel).
internal sealed class DialogShell : Window
{
    public DialogShell(Window owner, string title, UIElement body)
    {
        Title = title;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false,
        });
        Resources.MergedDictionaries.Add(owner.Resources);
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");

        var glyph = new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/title.png")),
            Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(glyph, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = title.ToUpperInvariant(), FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold,
            FontSize = 12, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
        var close = new Button { Content = "×", Style = (Style)owner.FindResource("CloseButton") };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close();
        var titleBar = new Grid();
        titleBar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
            Children = { glyph, name },
        });
        titleBar.Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { close } });
        var bar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = titleBar };
        bar.SetResourceReference(Border.BackgroundProperty, "TitleBackground");
        bar.SetResourceReference(Border.BorderBrushProperty, "Border");
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        };

        var rows = new Grid();
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var padded = new Border { Padding = new Thickness(20, 16, 20, 18), Child = body };
        Grid.SetRow(padded, 1);
        rows.Children.Add(bar);
        rows.Children.Add(padded);
        var frame = new Border { BorderThickness = new Thickness(1), Child = rows };
        frame.SetResourceReference(Border.BorderBrushProperty, "EdgeHighlight");
        Content = frame;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }
}

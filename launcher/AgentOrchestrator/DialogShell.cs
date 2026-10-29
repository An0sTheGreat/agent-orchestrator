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

// The large frame of a settings window: no title bar, a rounded and shadowed panel about 960 x 640 (within the owner's
// work area) centred over the owner, an optional header and a small close button in its top strip, which drags it.
// Themed live through the owner's resources; Esc closes it. Closing releases the body so its owner can take it back.
internal sealed class PanelShell : Window
{
    private const double ShadowRoom = 16; // transparent margin around the panel that its shadow falls into

    public PanelShell(Window owner, UIElement body, string? header = null)
    {
        Owner = owner;
        var work = MainWindow.WorkAreaOf(owner);
        Width = Math.Min(960 + 2 * ShadowRoom, work.Width);
        Height = Math.Min(640 + 2 * ShadowRoom, work.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Resources.MergedDictionaries.Add(owner.Resources);
        SetResourceReference(ForegroundProperty, "TextPrimary");

        var close = new Button
        {
            Content = "×", Style = (Style)owner.FindResource("CloseButton"), Width = 36, Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 6, 0), ToolTip = "Close (Esc)",
        };
        close.Click += (_, _) => Close();
        var strip = new Grid { Height = 40, Background = Brushes.Transparent };
        if (header is not null)
            strip.Children.Add(new TextBlock
            {
                Text = header, Style = (Style)owner.FindResource("SectionHeader"), Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        strip.Children.Add(close);
        strip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        };
        var host = new Border { Padding = new Thickness(28, 0, 28, 24), Child = body };
        Closed += (_, _) => host.Child = null;
        var rows = new DockPanel();
        DockPanel.SetDock(strip, Dock.Top);
        rows.Children.Add(strip);
        rows.Children.Add(host);
        var frame = new Border { BorderThickness = new Thickness(1), Child = rows };
        // The shadow is a sibling under the panel, so the effect never rasterizes the panel's text.
        var shadow = new Border { Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.28 } };
        foreach (var part in new[] { frame, shadow })
        {
            part.SetResourceReference(Border.BackgroundProperty, "Panel");
            part.SetResourceReference(Border.BorderBrushProperty, "Border");
            part.SetResourceReference(Border.CornerRadiusProperty, "PanelRadius");
        }
        Frame = frame;
        Content = new Grid { Margin = new Thickness(ShadowRoom), Children = { shadow, frame } };
    }

    // The rounded panel (for --ui-smoke: its brushes follow the theme).
    public Border Frame { get; }

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

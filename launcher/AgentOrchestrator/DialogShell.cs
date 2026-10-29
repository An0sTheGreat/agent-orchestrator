using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AgentOrchestrator;

// Shared frame of every popup window: no title bar, a rounded and shadowed panel on the DialogBackground shade, a small ×
// at the top right, and a top strip that drags it; Esc closes it (the same as Cancel). The title, when given, is a heading
// at the top of the body. Small dialogs size to their content; a large one (the Settings window) is about 960 x 640 within
// the owner's work area. Themed live through the owner's resources.
internal sealed class DialogShell : Window
{
    private const double ShadowRoom = 16; // transparent margin around the panel that its shadow falls into

    public DialogShell(Window owner, string? title, UIElement body, bool large = false)
    {
        Title = title ?? "";
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (large)
        {
            var work = MainWindow.WorkAreaOf(owner);
            Width = Math.Min(960 + 2 * ShadowRoom, work.Width);
            Height = Math.Min(640 + 2 * ShadowRoom, work.Height);
        }
        else
            SizeToContent = SizeToContent.WidthAndHeight;
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

        CloseX = new Button
        {
            Content = "×", Style = (Style)owner.FindResource("CloseButton"), Width = 36, Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 6, 0), ToolTip = "Close (Esc)",
        };
        CloseX.Click += (_, _) => Close();
        // Transparent, over the top of the body: the heading (or the empty top margin) below it is where the window is dragged.
        var strip = new Grid { Height = 40, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Children = { CloseX } };
        strip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        };
        var column = new DockPanel();
        if (!string.IsNullOrEmpty(title))
        {
            var heading = new TextBlock
            {
                Text = title, FontFamily = (FontFamily)owner.FindResource("DisplayFont"), FontSize = 16, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 36, 12), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            DockPanel.SetDock(heading, Dock.Top);
            column.Children.Add(heading);
        }
        column.Children.Add(body);
        var padding = large ? new Thickness(28, 40, 28, 24) : new Thickness(24, string.IsNullOrEmpty(title) ? 40 : 18, 24, 20);
        Frame = new Border { BorderThickness = new Thickness(1), Child = new Grid { Children = { new Border { Padding = padding, Child = column }, strip } } };
        // The shadow is a sibling under the panel, so the effect never rasterizes the panel's text.
        var shadow = new Border { Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.28 } };
        foreach (var part in new[] { Frame, shadow })
        {
            part.SetResourceReference(Border.BackgroundProperty, "DialogBackground");
            part.SetResourceReference(Border.BorderBrushProperty, "Border");
            part.SetResourceReference(Border.CornerRadiusProperty, "PanelRadius");
        }
        Content = new Grid { Margin = new Thickness(ShadowRoom), Children = { shadow, Frame } };
    }

    // The rounded panel and its × (for --ui-smoke: the panel's brushes follow the theme).
    public Border Frame { get; }

    public Button CloseX { get; }

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

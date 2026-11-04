using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentOrchestrator;

// An empty conversation (Chat or Code): its "Start a conversation" text and the message box sit together in the middle of the
// column, as Claude opens; the first message slides the box down to its place at the bottom. Only transforms move (the layout
// keeps the box at the bottom), so nothing reflows; window and box size changes re-centre an empty conversation.
public partial class MainWindow
{
    private const double LandingGap = 28; // between the empty text and the box

    private void InitComposerLanding()
    {
        foreach (var (box, empty, panel) in new[] { (ChatComposer, (FrameworkElement)ChatEmpty, (Panel)ChatPanel), (CodeComposer, CodeEmpty, CodePanel) })
        {
            box.RenderTransform = new TranslateTransform();
            empty.RenderTransform = new TranslateTransform();
            void Again() => PlaceComposer(box, empty, panel, animate: false);
            box.SizeChanged += (_, _) => Again();
            ((FrameworkElement)empty.Parent).SizeChanged += (_, _) => Again();
            empty.SizeChanged += (_, _) => Again();
            empty.IsVisibleChanged += (_, _) => PlaceComposer(box, empty, panel, animate: !empty.IsVisible);
        }
    }

    // The box's offset up from its place (and the text's, to centre the two together); 0 once the conversation has messages.
    private void PlaceComposer(FrameworkElement box, FrameworkElement empty, Panel panel, bool animate)
    {
        if (box.RenderTransform is not TranslateTransform move || empty.RenderTransform is not TranslateTransform lift)
            return;
        double boxY = 0, textY = 0;
        if (empty.Visibility == Visibility.Visible && panel.Children.Count == 0 && empty.Parent is FrameworkElement area)
        {
            var (rows, text, height) = (area.ActualHeight, empty.ActualHeight, box.ActualHeight);
            var below = box.Margin.Top; // the gap between the rows and the box
            var space = rows + below + height; // the conversation's rows and the box under them
            var top = (space - (text + LandingGap + height)) / 2; // the pair's top, centred in that space
            boxY = Math.Min(0, top + text + LandingGap - (rows + below));
            textY = top - (rows - text) / 2;
        }
        lift.Y = textY;
        if (animate && Math.Abs(move.Y - boxY) > 1)
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(boxY, TimeSpan.FromMilliseconds(380)) { EasingFunction = Decelerate });
        else
        {
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.Y = boxY;
        }
    }
}

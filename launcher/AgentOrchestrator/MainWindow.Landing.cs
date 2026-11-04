using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentOrchestrator;

// An empty chat: "Start a conversation" and the message box sit together in the middle of the column, as Claude opens; the
// first message slides the box down to its place at the bottom. Only transforms move (the layout keeps the box at the bottom),
// so nothing reflows; window and box size changes re-centre an empty chat.
public partial class MainWindow
{
    private const double LandingGap = 28; // between the empty text and the box

    private void InitComposerLanding()
    {
        ChatComposer.RenderTransform = new TranslateTransform();
        ChatEmpty.RenderTransform = new TranslateTransform();
        void Again(object? sender, EventArgs e) => PlaceChatComposer(animate: false);
        ChatComposer.SizeChanged += (_, _) => Again(null, EventArgs.Empty);
        ((FrameworkElement)ChatEmpty.Parent).SizeChanged += (_, _) => Again(null, EventArgs.Empty);
        ChatEmpty.SizeChanged += (_, _) => Again(null, EventArgs.Empty);
        ChatEmpty.IsVisibleChanged += (_, _) => PlaceChatComposer(animate: !ChatEmpty.IsVisible);
    }

    // The box's offset up from its place (and the text's, to centre the two together); 0 once the chat has messages.
    private void PlaceChatComposer(bool animate)
    {
        if (ChatComposer.RenderTransform is not TranslateTransform move || ChatEmpty.RenderTransform is not TranslateTransform lift)
            return;
        double boxY = 0, textY = 0;
        if (ChatEmpty.Visibility == Visibility.Visible && _chat?.Panel.Children.Count == 0 && ChatEmpty.Parent is FrameworkElement area)
        {
            var (rows, text, box) = (area.ActualHeight, ChatEmpty.ActualHeight, ChatComposer.ActualHeight);
            var space = rows + ChatComposer.Margin.Top + box; // the conversation's rows and the box under them
            var top = (space - (text + LandingGap + box)) / 2; // the pair's top, centred in that space
            boxY = Math.Min(0, top + text + LandingGap - (rows + ChatComposer.Margin.Top));
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

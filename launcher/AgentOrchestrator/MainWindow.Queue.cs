using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AgentOrchestrator;

// Messages typed while a reply (or the end-of-turn unload) uses the models. Enter queues them, one list per view, shown above
// the input. When a turn completes, the next queued message is sent as the next turn (the finished view's first). A stopped
// or failed turn, or a refused send, puts the queued messages back in the input instead, so nothing goes out unseen.
// While a Pi Code turn runs, Send now steers a queued Code message into it (the model reads it after its current tool calls).
public partial class MainWindow
{
    private sealed record QueuedMessage(string Text, List<string> Attachments);

    private readonly List<QueuedMessage> _chatQueue = new(), _codeQueue = new();

    private bool _turnSteers; // the running turn is a Pi Code turn with a user message: it takes steered messages
    private readonly Dictionary<string, (QueuedMessage Item, string Text)> _steering = new(); // by key: written, not delivered yet

    private List<QueuedMessage> QueueOf(Conversation c) => c.IsCode ? _codeQueue : _chatQueue;

    // While this is true, Send queues instead of sending.
    private bool Queues => _chatBusy || _unloading;

    // A view with messages still queued queues the next one too (between a turn's end and SendQueued), so none jumps ahead.
    private void SendOrQueue(Conversation c)
    {
        var behind = QueueOf(c).Count > 0;
        if (!Queues && !behind)
        {
            Send(c);
            return;
        }
        var typed = c.Input.Text.Trim();
        var attachments = AttachmentsOf(c);
        if (typed.Length == 0 && attachments.Count == 0)
            return;
        QueueOf(c).Add(new QueuedMessage(typed, attachments.ToList()));
        attachments.Clear();
        c.Input.Clear();
        RefreshAttachments(c);
        RefreshQueue(c);
        if (!Queues)
            Dispatcher.InvokeAsync(SendQueued, DispatcherPriority.Background);
    }

    // One row per queued message: its text (and attachment count), Edit (back into the input) and remove; Send now while a Pi
    // Code turn runs. A row being steered waits for the engine's "steered" event with its buttons off.
    private void RefreshQueue(Conversation c)
    {
        var panel = c.IsCode ? CodeQueueList : ChatQueueList;
        panel.Children.Clear();
        foreach (var item in QueueOf(c))
        {
            var steering = _steering.Values.Any(s => s.Item == item);
            var label = new TextBlock { Text = steering ? "Steering…" : "Queued", FontSize = 11, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var files = item.Attachments.Count switch { 0 => "", 1 => "  · 1 attachment", var n => $"  · {n} attachments" };
            var text = new TextBlock { Text = (item.Text.Length > 0 ? item.Text.ReplaceLineEndings(" ") : "(attachments)") + files, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = item.Text };
            var edit = new Button { Content = "Edit", Padding = new Thickness(6, 0, 6, 0), FontSize = 11, ToolTip = "Move back into the message box", Tag = "EditQueued" };
            edit.SetResourceReference(StyleProperty, "GhostButton");
            edit.Click += (_, _) => Unqueue(c, item);
            var remove = new Button { Content = "×", Padding = new Thickness(6, 0, 4, 0), FontSize = 13, ToolTip = "Don't send", Tag = "RemoveQueued" };
            remove.SetResourceReference(StyleProperty, "GhostButton");
            remove.Click += (_, _) =>
            {
                QueueOf(c).Remove(item);
                RefreshQueue(c);
            };
            edit.IsEnabled = remove.IsEnabled = !steering;
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(edit, Dock.Right);
            DockPanel.SetDock(label, Dock.Left);
            var dock = new DockPanel { Children = { remove, edit } };
            if (c.IsCode && _chatBusy && _turnSteers && !IsCommand(item.Text)) // a command is a turn of its own, not a message
            {
                var now = new Button { Content = "Send now", Padding = new Thickness(6, 0, 6, 0), FontSize = 11, IsEnabled = !steering,
                    ToolTip = "Send into the running reply: the model reads it after its current step", Tag = "SteerQueued" };
                now.SetResourceReference(StyleProperty, "GhostButton");
                now.Click += (_, _) => Steer(item);
                DockPanel.SetDock(now, Dock.Right);
                dock.Children.Add(now);
            }
            dock.Children.Add(label);
            dock.Children.Add(text);
            var row = new Border { Padding = new Thickness(8, 3, 2, 3), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(8),
                Child = dock };
            row.SetResourceReference(Border.BackgroundProperty, "Hover");
            panel.Children.Add(row);
        }
        panel.Visibility = panel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshChatInput(); // the "n queued" status
    }

    // Send now: one line on the turn's stdin, which the engine hands to Pi. Composing sets _turnImages, which the running
    // turn no longer reads (RunTurn wrote its request before starting it). One never delivered is queued again at the turn's end.
    private void Steer(QueuedMessage item)
    {
        if (_chatProcess is not { } process)
            return;
        var key = Guid.NewGuid().ToString("N");
        var text = ComposeMessage(_code, item.Text, item.Attachments);
        ToTurn(process, JsonSerializer.Serialize(new { steer = text, images = _turnImages, key })); // a failed write: the turn ended, which reverts the row
        _steering[key] = (item, text);
        RefreshQueue(_code);
    }

    // Takes queued messages (all when item is null) out of the queue and puts their text before whatever is typed, and
    // their attachments back with the composer's.
    private void Unqueue(Conversation c, QueuedMessage? item = null)
    {
        var queue = QueueOf(c);
        var taken = item is null ? queue.ToList() : queue.Contains(item) ? [item] : [];
        if (taken.Count == 0)
            return;
        queue.RemoveAll(taken.Contains);
        var texts = taken.Select(m => m.Text).Where(t => t.Length > 0).ToList();
        if (c.Input.Text.Trim().Length > 0)
            texts.Add(c.Input.Text.Trim());
        c.Input.Text = string.Join("\n\n", texts);
        c.Input.CaretIndex = c.Input.Text.Length;
        var attachments = AttachmentsOf(c);
        foreach (var path in taken.SelectMany(m => m.Attachments))
            if (!attachments.Contains(path, StringComparer.OrdinalIgnoreCase))
                attachments.Add(path);
        RefreshAttachments(c);
        RefreshQueue(c);
    }

    // RunTurn's end (only the latest turn's). Completed: send the next queued message once the dispatcher is idle (after the
    // turn's own saving); otherwise every queued message goes back into its input.
    private void AfterTurn(bool completed)
    {
        if (completed)
        {
            if (_chatQueue.Count == 0 && _codeQueue.Count == 0) // fully finished: nothing queued goes next
                NotifyDone($"{(_turn.IsCode ? "Code" : "Chat")} reply finished",
                    Preview(TypedText(_turn.History.LastOrDefault(m => m.Role == "assistant")?.Content ?? "")));
            Dispatcher.InvokeAsync(SendQueued, DispatcherPriority.Background);
            return;
        }
        Unqueue(_chat);
        Unqueue(_code);
    }

    private bool _sendingQueued; // a send's checks may open a dialog, whose message loop could run SendQueued again

    // Also called when the end-of-turn unload finishes. A send that is refused (no API key, no folder, ...) leaves the app
    // idle: that view's queue then goes back into its input and the other view's is tried. A project run (a Code turn may
    // start one) has no queue: both go back into their inputs.
    private void SendQueued()
    {
        if (_sendingQueued || _chatBusy || _unloading || _workflowRoot is null)
            return;
        if (_process is not null)
        {
            Unqueue(_chat);
            Unqueue(_code);
            return;
        }
        _sendingQueued = true;
        try
        {
            foreach (var c in _turn.IsCode ? new[] { _code, _chat } : [_chat, _code])
            {
                var queue = QueueOf(c);
                if (queue.Count == 0)
                    continue;
                var next = queue[0];
                queue.RemoveAt(0);
                RefreshQueue(c);
                if (c.IsCode)
                    Forget(SendCode(queued: next));
                else
                    SendChat(next);
                if (_chatBusy)
                    return;
                queue.Insert(0, next);
                Unqueue(c);
            }
        }
        finally
        {
            _sendingQueued = false;
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrchestrator;

// Chat view: a conversation with one worker, which may delegate parts to the others.
// Every message starts one `orchestrator.py --chat` process; its events stream into the reply under construction.
// The Code view's conversation (MainWindow.Code.cs) runs on the same plumbing: one turn at a time, of either kind.
public partial class MainWindow
{
    private Process? _chatProcess;
    private bool _chatBusy, _chatStopRequested; // _chatBusy: a conversation turn (Chat or Code) is running
    private bool _turnCompleted; // the last turn ended with a reply (not stopped or failed)
    private string? _chatError;
    private string? _chatRequestFile, _chatTempFolder; // the current turn's temp files; _chatTempFolder only when PrepareChatFolder made it
    private readonly HashSet<(string Url, string Path)> _loadedByChat = new(); // unloaded when the app closes, or after each turn when ChatKeepLoaded is off
    private HashSet<(string Url, string Path)> _turnLoaded = new(); // what the current turn's loading events added; a fresh set per turn so a late unload never touches the next turn
    private Task _chatUnload = Task.CompletedTask;
    private int _unloadsPending; // end-of-turn unloads in flight: Send waits so they cannot unload what the next turn loads
    private bool _unloading => _unloadsPending > 0;
    private ChatReply? _reply;

    // A conversation kind's own controls, history and scroll state.
    private sealed class Conversation
    {
        public required bool IsCode;
        public required StackPanel Panel;
        public required ScrollViewer Scroll;
        public required FrameworkElement Empty;
        public required Button Jump, Send, Stop, Plus, Chip;
        public required TextBox Input;
        public required TextBlock Hint, Busy;
        public readonly List<LauncherLogic.ChatMessage> History = new();
        public bool Follow = true;
        public long Tokens; // all turns of this conversation (saved with it)
        public string? Summary; // the engine's summary of History[..SummaryCovers] (history_compacted; saved with it)
        public int SummaryCovers;
        public bool Owns(object o) => o == Input || o == Send || o == Scroll || o == Jump || o == Plus || o == Chip;
    }

    private Conversation _chat = null!, _code = null!, _turn = null!; // _turn: the conversation of the running (or last) turn

    // A chat turn keeps running while the user opens another chat: its view (panel, history, context) is parked off screen and
    // keeps streaming there; opening that chat again brings it back, and the turn's end saves it wherever it is.
    private Conversation? _parked;
    private ConversationStore.Conversation? _parkedConv;
    private ContextSnapshot? _parkedContext;
    private string _turnWorker = ""; // the Chat target when the turn started (opening another chat changes ChatWorker)

    // The saved conversation a reply or project run is using (null: none): it cannot be deleted meanwhile.
    private string? RunningId => _process is not null ? _codeConv?.Id : !_chatBusy ? null : _turn.IsCode ? _codeConv?.Id : _parked is not null ? _parkedConv?.Id : _chatConv?.Id;

    private bool ChatTurnShown => _chatBusy && !_turn.IsCode && _turn == _chat;

    private void ParkChatTurn()
    {
        var turn = _parked = _chat;
        (_parkedConv, _parkedContext) = (_chatConv, _chatContext);
        var panel = new StackPanel { Margin = turn.Panel.Margin, MaxWidth = turn.Panel.MaxWidth };
        _chat = new Conversation { IsCode = false, Panel = panel, Scroll = ChatScroll, Empty = ChatEmpty, Jump = ChatJumpPill, Send = ChatSend, Stop = ChatStop,
            Plus = ChatPlus, Chip = ChatTargetChip, Input = ChatInput, Hint = ChatHint, Busy = ChatBusyText };
        // what the turn still touches while it streams: stand-ins, so the chat on screen is left alone
        (turn.Scroll, turn.Empty, turn.Jump) = (new ScrollViewer(), new StackPanel(), new Button());
        ChatScroll.Content = panel;
        _chatConv = null;
    }

    private void UnparkChatTurn()
    {
        var turn = _parked!;
        (turn.Scroll, turn.Empty, turn.Jump, turn.Follow) = (ChatScroll, ChatEmpty, ChatJumpPill, true);
        _chat = turn;
        ChatScroll.Content = turn.Panel;
        ChatEmpty.Visibility = turn.Panel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _chatConv = _parkedConv;
        ShowContext(false, _parkedContext);
        (_parked, _parkedConv, _parkedContext) = (null, null, null);
        Jump(turn);
    }

    // The running turn's context: shown, or kept for its parked view.
    private void TurnContext(ContextSnapshot? s)
    {
        if (_turn == _parked)
            _parkedContext = s;
        else if (_turn.IsCode && s is not null)
            SetCodeContext(s);
        else
            ShowContext(_turn.IsCode, s);
    }
    private List<LauncherLogic.ChatMessage> _chatHistory => _chat.History;
    private Conversation ConvOf(object sender) => _code.Owns(sender) ? _code : _chat;
    private RoleCard? TargetOf(Conversation c) => c.IsCode ? CodeTarget() : ChatTarget();

    // One assistant reply: sections (thinking, Plan / Draft / Review) above the prominent answer.
    private sealed class ChatReply
    {
        public StackPanel Content = null!;
        public TextBlock Status = null!;
        public Button Copy = null!;
        public StackPanel Footer = null!; // timestamp and copy icon, shown once the reply is complete
        public TextBlock Stamp = null!;
        public TextBlock Stats = null!; // the footer's call / turn / task tokens
        public string? Last, Pending; // stats of the last call; of a call whose text streamed into the answer, not yet shown
        public long TurnTokens;
        public string Stage = "answer", StageTitle = "";
        public StreamBlock? Block;
        public TextBox? Answer; // the answer as it streams (plain text); hidden once the final reply is rendered
        public RichTextBox? Rendered; // the answer as formatted Markdown, re-rendered as it streams
        public readonly StringBuilder AnswerText = new();
        public string? Final;
    }

    private void InitChat()
    {
        _chat = _turn = new Conversation { IsCode = false, Panel = ChatPanel, Scroll = ChatScroll, Empty = ChatEmpty, Jump = ChatJumpPill, Send = ChatSend, Stop = ChatStop,
            Plus = ChatPlus, Chip = ChatTargetChip, Input = ChatInput, Hint = ChatHint, Busy = ChatBusyText };
        _code = new Conversation { IsCode = true, Panel = CodePanel, Scroll = CodeScroll, Empty = CodeEmpty, Jump = CodeJumpPill, Send = CodeSend, Stop = CodeStop,
            Plus = CodePlus, Chip = CodeTargetChip, Input = CodeInput, Hint = CodeHint, Busy = CodeBusyText };
        RefreshExtras();
        RefreshPermissions();
        InitAttachments();
        InitNotify();
        RefreshPonytailSetting();
        if (CardFor(_settings.ChatWorker) is null)
            _settings.ChatWorker = "planner";
        ChatKeepToggle.IsChecked = _settings.ChatKeepLoaded;
        CenterChatToggle.IsChecked = _settings.CenterChat;
        ContextPercentToggle.IsChecked = _settings.ContextPercent;
        ChatThinkingToggle.IsChecked = CodeThinkingToggle.IsChecked = _settings.ViewThinking;
        CodeReviewerToggle.IsChecked = _settings.ReviewEveryTurn;
        SelectTagged(ReadingSizeBox, _settings.ReadingSize);
        SelectTagged(EmojiBox, _settings.Emoji);
        SelectTagged(LinkOpenBox, _settings.LinkOpen);
        FollowTabsSlider.Value = _settings.FollowTabs is >= 3 and <= 10 ? _settings.FollowTabs : 11;
        FollowTabs_Changed(FollowTabsSlider, new RoutedPropertyChangedEventArgs<double>(0, FollowTabsSlider.Value)); // the label
        ApplyChatWidth();
        ShowContext(false, null);
        ShowContext(true, null);
        RefreshChatTarget();
        RefreshChatInput();
    }

    private RoleCard? ChatTarget() => CardFor(_settings.ChatWorker) is { Hidden: false, Role: not "compactor" } target ? target : CardFor("planner");

    private static string ChatName(RoleCard? card, string role) =>
        card is { IsCustom: true } ? card.Title : role == "planner" ? "Lead" : RoleName(role);

    private static string RoleName(string role) => role.Length == 0 ? role : char.ToUpperInvariant(role[0]) + role[1..];

    private void RefreshChatTarget()
    {
        var target = ChatTarget();
        ChatTargetChip.Content = $"To: {ChatName(target, target?.Role ?? "")}  ▾";
        RefreshLeadChip();
        RefreshCardVisibility();
        RefreshEffort();
        RefreshVision();
    }

    // The Code view always talks to the lead (the Planner): its chip names it; its model shows in the dropdown.
    private void RefreshLeadChip() => CodeTargetChip.Content = "Lead  ▾";

    // Both views: the Implementer and Reviewer tabs and rail dots follow the workflow mode (AppSettings.AddedRoles); a role it
    // leaves out runs as LEAD (the planner), and its tab lists the roles it covers.
    private void RefreshCardVisibility()
    {
        var chat = ChatShown; // Chat shows LEAD and the agents added to it; Code follows the workflow mode
        foreach (var card in _cards)
            card.Hidden = chat ? card.Role != "planner" && !_settings.ChatCards.Contains(card.Role)
                : card.IsBuiltIn && card.Role is not ("planner" or "compactor") && !_settings.AddedRoles.Contains(card.Role);
        if (CardFor("planner") is { } main)
        {
            var covered = Roles.Where(r => r == "planner" || r != "compactor" && CardFor(r) is not { Hidden: false }).Select(RoleName).ToList();
            main.Covers = covered.Count > 1 ? string.Join(" · ", covered) : ""; // no Covers line for the planner alone
        }
        if (_shownWorker is { Hidden: true })
            SelectWorker(CardFor("planner"));
    }

    private bool ChatShown => NavChat.IsChecked == true;

    // The workers c's target chip lists below Options…: none for Code (always the lead); for Chat LEAD, the shown roles and
    // the custom workers.
    private List<RoleCard> ListedWorkers(Conversation c) => c.IsCode ? [] : _cards.Where(k => !k.Hidden && k.Role != "compactor").ToList(); // COMPACTOR only summarizes

    // Send, the stop icon beside it while a reply or a project run is using the models, the lock while a reply streams,
    // and why sending is unavailable. A turn of either kind locks both.
    private void RefreshChatInput()
    {
        if (_chat is null)
            return; // before InitChat
        var runActive = _process is not null;
        foreach (var c in new[] { _chat, _code })
        {
            var mine = _chatBusy && _turn == c;
            c.Chip.IsEnabled = !_chatBusy; // typing (and attaching) stays open: Send queues while the models work
            c.Stop.Visibility = _chatBusy || runActive ? Visibility.Visible : Visibility.Collapsed;
            var hasInput = c.Input.Text.Trim().Length > 0 || AttachmentsOf(c).Count > 0;
            c.Send.IsEnabled = !runActive && _workflowRoot is not null && hasInput;
            c.Send.Visibility = hasInput ? Visibility.Visible : Visibility.Collapsed;
            c.Send.ToolTip = Queues ? "Queue (Enter): sent when the current reply finishes" : "Send (Enter)";
            // The status sits beside the stop square (top row); the bottom row keeps only the idle hint.
            var queued = QueueOf(c).Count > 0 ? $" {QueueOf(c).Count} queued." : " Enter queues a message.";
            var status = runActive ? $"A project run is using the models; {(c.IsCode ? "Code" : "chat")} is available when it finishes."
                : mine ? "Replying…" + queued : _chatBusy ? $"{(_turn.IsCode == c.IsCode ? (c.IsCode ? "Another conversation's" : "Another chat's") : c.IsCode ? "A chat" : "A Code")} reply is using the models;" + queued
                : _unloading ? "Unloading models…" + queued : "";
            c.Busy.Text = status;
            c.Busy.ToolTip = status;
            c.Busy.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            c.Hint.Visibility = status.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        ChatKeepToggle.IsEnabled = !_chatBusy;
        ChatUnloadButton.IsEnabled = CodeUnloadButton.IsEnabled = !_chatBusy && !runActive && _unloadsPending == 0 && ModelsLoaded;
        CodeNewButton.IsEnabled = CodeFoldersList.IsEnabled = AddCodeFolderButton.IsEnabled = !_chatBusy && !runActive;
        var hasMd = HasProjectMd();
        CodeStartButton.IsEnabled = !_chatBusy && !runActive && _workflowRoot is not null && hasMd;
        CodeStartButton.ToolTip = hasMd ? "Start or resume the project run in this folder (Ctrl+Enter)"
            : "This folder has no PROJECT.md: create one with a model, add one or start from the template to start the project";
        CodeProjectButton.IsEnabled = !_chatBusy && !runActive && SelectedProjectPath().Length > 0;
    }

    // ---- worker menu (+ button and target chip)

    private ContextMenu BuildWorkerMenu(FrameworkElement target, Conversation? c = null, IEnumerable<RoleCard>? cards = null)
    {
        c ??= _chat;
        RefreshSetupProblems();
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        foreach (var card in cards ?? _cards.Where(k => !k.Hidden))
        {
            var name = ChatName(card, card.Role);
            var broken = card.IsCustom ? card.SetupProblem : null;
            var item = new MenuItem
            {
                Header = broken is null ? name : $"{name}  —  {broken}",
                IsChecked = card.Role == TargetOf(c)?.Role,
                IsEnabled = broken is null,
                InputGestureText = broken is null ? card.ModelName : "", // the chip names only the worker: its model shows here
                ToolTip = broken is null ? card.ModelName : card.SetupTip,
                Tag = card.Role,
            };
            item.Click += (_, _) => PickWorker(c, card.Role);
            menu.Items.Add(item);
        }
        return menu;
    }

    // Picking a worker makes it Chat's target (Code always talks to the lead).
    private void PickWorker(Conversation c, string role)
    {
        if (c.IsCode)
            return;
        _settings.ChatWorker = role;
        _settings.Save();
        RefreshChatTarget();
        c.Input.Focus();
    }

    private void PickChatWorker(string role) => PickWorker(_chat, role);

    // The target chip: a dropdown (upward) of the listed workers, picking one makes it the target; "Options…" on top opens
    // the Models window with the same workers.
    private void ChatTarget_Click(object sender, RoutedEventArgs e)
    {
        var chip = (Button)sender;
        var c = ConvOf(sender);
        var menu = chip.ContextMenu = BuildWorkerMenu(chip, c, c.IsCode ? CodeTarget() is { } lead ? [lead] : [] : ListedWorkers(c));
        var options = new MenuItem { Header = "Options…", ToolTip = "Model settings for these workers", IsEnabled = _process is null };
        options.Click += (_, _) => Dispatcher.InvokeAsync(() => OpenModelOptions(c), System.Windows.Threading.DispatcherPriority.Background);
        menu.Items.Insert(0, options);
        menu.Items.Insert(1, new Separator { Style = (Style)FindResource("MenuSeparator") });
        menu.IsOpen = true;
    }

    // The target's worker tab (Code: the lead's, the Planner).
    private void OpenModelOptions(Conversation c) => OpenWorkerTab(TargetOf(c));

    // Centered: the conversations keep ChatWidth in the middle of the column (a Stretch element narrower than its slot is
    // centered); off: they fill it. The roomier reading sizes keep lines at a comfortable length, as ChatGPT does.
    private double ChatWidth => _settings.ReadingSize == "compact" ? 860 : 800;

    // The reading size's text: replies and the user's messages.
    private double ReadingFont => _settings.ReadingSize switch { "compact" => 14, "large" => 17.5, _ => 16 };

    private void ApplyChatWidth()
    {
        Markdown.Roomy = _settings.ReadingSize != "compact";
        foreach (var part in new FrameworkElement?[] { _chat.Panel, _parked?.Panel, CodePanel, ChatComposer, CodeComposer }.OfType<FrameworkElement>())
            part.MaxWidth = _settings.CenterChat ? ChatWidth : double.PositiveInfinity;
    }

    private void ContextPercent_Click(object sender, RoutedEventArgs e)
    {
        _settings.ContextPercent = ContextPercentToggle.IsChecked == true;
        _settings.Save();
        RefreshContextPins();
    }

    private static void SelectTagged(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    // Reading size: the messages on screen take it now (a streaming reply's rest renders in it too).
    private void ReadingSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReadingSizeBox.SelectedItem is not ComboBoxItem { Tag: string size } || size == _settings.ReadingSize)
            return;
        var old = ReadingFont;
        _settings.ReadingSize = size;
        _settings.Save();
        ApplyChatWidth();
        RefreshContextPins();
        foreach (var panel in new[] { _chat.Panel, _parked?.Panel, CodePanel }.OfType<Panel>())
            Restyle(panel, old);
    }

    // The answers' and user messages' text (thinking and tool output keep their smaller size) and the replies' footers.
    private void Restyle(DependencyObject root, double old)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToList())
        {
            if (child is TextBox { FontStyle: var style } box && style != FontStyles.Italic && box.FontSize == old)
                box.FontSize = ReadingFont;
            if (child is TextBlock emojiBubble && emojiBubble.FontSize == old) // a message with emoji (Markdown.EmojiTextBlock)
                emojiBubble.FontSize = ReadingFont;
            if (child is RichTextBox { Tag: MarkdownState state } view)
            {
                Render(view);
                continue;
            }
            Restyle(child, old);
        }
    }

    // --ui-smoke: a reading size re-renders the reply on screen in its text size and spacing (a first heading with no room
    // above it), and the emoji setting goes with the next request.
    private bool ReadingSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: reading size: {what}"); return false; }
        var (size, emoji, turn) = (_settings.ReadingSize, _settings.Emoji, _turn);
        RichTextBox? View() => SmokeFind<RichTextBox>(_chat.Panel, v => v.Tag is MarkdownState);
        try
        {
            ClearConversation(_chat);
            Replay(_chat, [new("user", "Hi"), new("assistant", "## Plan\n\nFirst paragraph.\n\nSecond paragraph.")], "planner");
            foreach (var (tag, font, roomy) in new[] { ("large", 17.5, true), ("compact", 14.0, false), ("comfortable", 16.0, true) })
            {
                SelectTagged(ReadingSizeBox, tag);
                if (View() is not { Document: var doc } || doc.FontSize != font || Markdown.Roomy != roomy || doc.Blocks.FirstBlock.Margin.Top != 0 ||
                    ((Paragraph)doc.Blocks.LastBlock).Margin.Bottom != (roomy ? 14 : 8))
                    return Fail($"{tag}: {View()?.Document.FontSize}");
            }
            SelectTagged(EmojiBox, "none");
            if (System.Text.Json.Nodes.JsonNode.Parse(LauncherLogic.BuildChatRequest("planner", [], CurrentExtras()))?["emoji"]?.GetValue<string>() != "none" || _settings.Emoji != "none")
                return Fail("the emoji setting");
            return true;
        }
        finally
        {
            SelectTagged(ReadingSizeBox, size);
            SelectTagged(EmojiBox, emoji);
            ClearConversation(_chat);
            _turn = turn;
        }
    }

    // Code's Reviewer chip: the Reviewer checks each Code turn at its end (orchestrator.reviewed_turn); off, turns run as before.
    private void ReviewerToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReviewEveryTurn = CodeReviewerToggle.IsChecked == true;
        _settings.Save();
    }

    private void Emoji_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EmojiBox.SelectedItem is not ComboBoxItem { Tag: string emoji } || emoji == _settings.Emoji)
            return;
        _settings.Emoji = emoji;
        _settings.Save();
    }

    private void CenterChat_Click(object sender, RoutedEventArgs e)
    {
        _settings.CenterChat = CenterChatToggle.IsChecked == true;
        _settings.Save();
        ApplyChatWidth();
        RefreshContextPins();
    }

    // Unload model (under CHAT and CODE): what chat has loaded, and the local llama-server, now; with Keep models loaded
    // on, this frees the memory without the round trip through Settings. Greyed out with nothing loaded or while working.
    private void UnloadModels_Click(object sender, RoutedEventArgs e)
    {
        UnloadChat(_loadedByChat.ToList());
        if (_process is null) // a run may be using the local server
            StopLocalServer();
        WorkerContextsCleared();
        Toast("Models unloaded");
        RefreshChatInput();
    }

    private bool ModelsLoaded => _loadedByChat.Count > 0 || LocalRuntime && File.Exists(_llamaStateFile);

    // Off: unload what chat has loaded right now (the toggle is locked while a reply streams).
    private void ChatKeep_Click(object sender, RoutedEventArgs e)
    {
        _settings.ChatKeepLoaded = ChatKeepToggle.IsChecked == true;
        _settings.Save();
        if (_settings.ChatKeepLoaded)
            return;
        UnloadChat(_loadedByChat.ToList());
        if (_process is null) // a run may be using the local server
            StopLocalServer();
    }

    // Unloads the given chat-loaded models, each on the server that loaded it, without blocking the UI thread. Only models
    // chat itself still tracks (never ones a project run loaded or the user loaded beforehand).
    private void UnloadChat(List<(string Url, string Path)> wanted)
    {
        var pairs = wanted.Where(_loadedByChat.Remove).Where(p => !_loadedByRun.Contains(p)).ToList();
        if (pairs.Count == 0)
            return;
        _unloadsPending++;
        RefreshChatInput();
        _chatUnload = UnloadChatAsync(pairs, _runKey);
    }

    // The POSTs run on the thread pool; the log line and the Send lock are updated back on the UI thread.
    private async Task UnloadChatAsync(List<(string Url, string Path)> pairs, string key)
    {
        var unloaded = await Task.Run(async () =>
        {
            var count = 0;
            foreach (var (url, path) in pairs)
                if (await PostUnloadAsync(url, path, key).ConfigureAwait(false))
                    count++;
            return count;
        });
        Log($"Unloaded {unloaded} chat model(s)");
        if (unloaded > 0)
            WorkerContextsCleared();
        _unloadsPending--;
        RefreshChatInput();
        SendQueued(); // a message queued behind the unload
    }

    // ---- input

    private void ChatInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshChatInput();
        RefreshSkillCompletion((TextBox)sender);
    }

    private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SkillCompletionKey((TextBox)sender, e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return; // Shift+Enter inserts a new line
        e.Handled = true;
        if (ConvOf(sender).Send.IsEnabled)
            SendOrQueue(ConvOf(sender));
    }

    private void Send(Conversation c)
    {
        if (c.IsCode)
            Forget(SendCode());
        else
            SendChat();
    }

    private void ChatSend_Click(object sender, RoutedEventArgs e) => SendOrQueue(ConvOf(sender));

    // The stop icon (and Ctrl+Shift+X): stops the project run, else the reply.
    private void StopWork_Click(object sender, RoutedEventArgs e)
    {
        if (_process is not null)
            Stop_Click(sender, e);
        else if (_chatBusy && _chatProcess is { } p)
        {
            try
            {
                _chatStopRequested = true;
                p.Kill(entireProcessTree: true);
                Log("Chat stop requested.");
            }
            catch (InvalidOperationException) { } // already gone
        }
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (ChatTurnShown)
            ParkChatTurn();
        ShowView(chat: true);
        ClearChatConversation();
        ChatInput.Focus();
    }

    // ---- saved Chat-view conversations (the sidebar lists them with the Code ones)

    private ConversationStore.Conversation? _chatConv; // the open saved one; null until a chat's first reply saves it

    // A project's "+" chat, saved into that project at its first reply.
    private string? _newChatGroup;

    private ConversationStore.Conversation? NewStoredChat()
    {
        var c = _store.Create("", "chat");
        if (c is not null && _newChatGroup is { } group)
            _store.MoveToGroup(c.Id, group);
        _newChatGroup = null;
        return c;
    }

    private void ClearChatConversation()
    {
        ClearConversation(_chat);
        _chatConv = null;
        _newChatGroup = null;
        RefreshSidebarList();
    }

    // After a completed Chat turn: the first reply creates the saved chat, later ones update it.
    // A new conversation's first turn may bring a model-written title (chat_title); it is kept until the user renames.
    private string? _turnTitle;

    private void SaveChatConversation()
    {
        var parked = _parked is not null && _turn == _parked; // a turn's end while the user is in another chat
        var ui = parked ? _parked! : _chat;
        if (!parked && _chatConv is null)
            StoreDo(() => _chatConv = NewStoredChat());
        if ((parked ? _parkedConv : _chatConv) is { } c)
        {
            (c.Worker, c.Messages) = (parked ? _turnWorker : _settings.ChatWorker, ui.History.ToList());
            c.Context = parked ? _parkedContext : _chatContext;
            c.Tokens = ui.Tokens;
            (c.Summary, c.SummaryCovers) = (ui.Summary, ui.SummaryCovers);
            c.Title ??= TakeTurnTitle();
            StoreDo(() => _store.Save(c));
        }
        RefreshSidebarList();
    }

    private string? TakeTurnTitle()
    {
        var title = string.IsNullOrWhiteSpace(_turnTitle) ? null : _turnTitle.Trim();
        _turnTitle = null;
        return title;
    }

    // A sidebar row: shows the saved chat in the Chat view with its mode and worker. Refused while a reply streams.
    // While a chat replies, its view is parked and comes back when its chat is opened again.
    private void OpenChatConversation(string id)
    {
        if (_parked is not null && _parkedConv?.Id == id)
        {
            Unqueue(_chat); // queued for the chat being left: back to the input, not sent into this one
            UnparkChatTurn();
            ShowView(chat: true);
            return;
        }
        if (_chatConv?.Id != id && _store.Get(id) is { } saved)
        {
            if (ChatTurnShown)
                ParkChatTurn();
            ClearConversation(_chat);
            _chatConv = saved;
            if (CardFor(saved.Worker) is not null)
                _settings.ChatWorker = saved.Worker;
            _settings.Save();
            Replay(_chat, saved.Messages, ChatTarget()?.Role ?? "");
            ShowContext(false, saved.Context);
            _chat.Tokens = saved.Tokens ?? 0;
            (_chat.Summary, _chat.SummaryCovers) = (saved.Summary, saved.SummaryCovers);
        }
        ShowView(chat: true);
    }

    // Saved messages into a cleared conversation: user bubbles and finished replies.
    // A running turn (in another conversation) keeps its _turn and _reply.
    private void Replay(Conversation c, List<LauncherLogic.ChatMessage> messages, string role)
    {
        var keep = _chatBusy ? (_turn, _reply) : (c, null);
        _turn = c;
        foreach (var message in messages)
            if (message.Role == "user")
                AddUserBubble(message.Content, message.Time, message.Images);
            else
            {
                var reply = _reply = AddReply(role);
                SetChatFinal(message.Content);
                if (ThumbRow(message.Images, 160, HorizontalAlignment.Left) is { } shots) // its browser screenshots
                {
                    shots.Margin = new Thickness(0, 6, 0, 0);
                    reply.Content.Children.Add(shots);
                }
                reply.Status.Visibility = Visibility.Collapsed;
                SetStamp(reply.Stamp, message.Time);
                reply.Footer.Visibility = Visibility.Visible;
            }
        (_turn, _reply) = keep;
        c.History.AddRange(messages);
    }

    private void ClearConversation(Conversation c)
    {
        Unqueue(c); // queued for the conversation being closed: back to the input, not sent into the next one
        c.Panel.Children.Clear();
        c.History.Clear();
        c.Tokens = 0;
        (c.Summary, c.SummaryCovers) = (null, 0);
        if (c == _turn) // a running turn elsewhere keeps its reply
            _reply = null;
        c.Empty.Visibility = Visibility.Visible;
        ShowContext(c.IsCode, null);
        Jump(c);
    }

    // ---- sending

    // queued: a message from the queue instead of the composer (which keeps what is being typed).
    private void SendChat(QueuedMessage? queued = null)
    {
        var text = queued is null ? ComposeMessage(_chat, ChatInput.Text.Trim()) : ComposeMessage(_chat, queued.Text, queued.Attachments);
        if (text.Length > 0)
            Forget(RunTurn(_chat, text, ChatTarget()!.Role, role => ChatRequest(role, text),
                LauncherLogic.BuildChatStartInfo, "Chat", queued is not null));
    }

    // The request of a Chat turn: the stored summary stands in for the messages it covers.
    private string ChatRequest(string role, string text) =>
        LauncherLogic.BuildChatRequest(role, _chatHistory.Append(new LauncherLogic.ChatMessage("user", text)), CurrentExtras(message: text) with { Images = _turnImages }, title: _chatHistory.Count == 0,
            effort: EffortFor(CardFor(role)), summary: _chat.Summary ?? "", summaryCovers: _chat.SummaryCovers);

    // Shared by Chat and Code: the checks, then one orchestrator process whose events stream into a new reply in c.
    // text null: no user message (a Code action such as write_project). buildRequest gets the target role.
    // True when the turn completed with a reply.
    private async Task<bool> RunTurn(Conversation c, string? text, string role, Func<string, string> buildRequest,
        Func<string, string, string, string, string, ProcessStartInfo> buildStartInfo, string label, bool queued = false)
    {
        if (_chatBusy || _process is not null || _workflowRoot is null || !SaveWorkerEditsFirst() || !RuntimeReady())
            return false;
        if (ApiKeyBox.Password.Length == 0)
            TryLoadApiKeyFile();
        _runKey = ApiKeyBox.Password.Trim();
        var unsloth = UsesUnsloth(SelectedProjectPath());
        if (_runKey.Length == 0 && unsloth)
        {
            SetStatus("API key required", "#FF667A");
            Log("Enter your Unsloth API key.");
            OpenSettings(ApiKeyBox);
            return false;
        }
        var skipped = new List<string>();
        if (!SyncCustomWorkers(skipped))
        {
            Toast("Message not sent: config.json could not be updated", "#FF667A");
            return false;
        }
        if (CardFor(role) is { IsCustom: true, SetupProblem: { } problem } target)
        {
            Toast($"{ChatName(target, target.Role)} cannot be used: {problem}", "#F7C873");
            return false;
        }
        foreach (var reason in skipped)
            Log($"Not written to config.json, so it will not take part: {reason}");

        var requestFile = _chatRequestFile = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-chat-{Guid.NewGuid():N}.json");
        var exitCode = -1;
        string? failure = null;
        StartTurn(c, text, role, queued);
        var turn = ++_turnId;
        if (text is not null && !queued)
            c.Input.Clear();
        try
        {
            var prepared = LauncherLogic.PrepareChatFolder(SelectedProjectPath(), _workflowRoot, CurrentWorkers(), out var tempSkipped);
            var folder = prepared.Path;
            if (prepared.IsTemp)
            {
                _chatTempFolder = folder;
                foreach (var reason in tempSkipped)
                    Log($"Not written to config.json, so it will not take part: {reason}");
            }
            File.WriteAllText(requestFile, buildRequest(role));
            var startInfo = buildStartInfo(LauncherLogic.FindPython(), _workflowRoot, folder, _runKey, requestFile);
            ApplyRuntime(startInfo);
            var process = _chatProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };
            _apiBase = ManagementUrl(folder) ?? _apiBase;
            _runStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            Log($"{label} in {folder}");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (unsloth || LocalRuntime) // live speed: Unsloth's monitor, or llama-server's /slots
                _poll.Start();
            _pump.Start();
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            Log($"ERROR: {exception.Message}");
        }
        finally
        {
            _poll.Stop();
            _pump.Stop();
            _chatProcess?.Dispose();
            _chatProcess = null;
            CleanupChatTemp();
        }
        var completed = false;
        try
        {
            DrainLines();
            if (FinishChatTurn(exitCode, _chatStopRequested, failure))
                Toast(c.IsCode ? "Code stopped" : "Chat stopped", "#F7C873");
            return completed = _turnCompleted;
        }
        finally
        {
            // An exception after the process ended must not leave the app busy (unless a newer turn already started).
            if (_chatBusy && _turnId == turn)
                SetChatBusy(false);
            RefreshCodeFolder(); // a folder change during the turn takes effect now that its history is saved
            if (_turnId == turn)
                AfterTurn(completed); // the next queued message, or the queue back into the input
        }
    }

    private int _turnId; // RunTurn's count: tells a turn's own cleanup from one a follow-up turn (StartCodeProject) started

    // Runs a turn without awaiting it; a fault reaches the dispatcher (and fails --ui-smoke) instead of vanishing in the Task.
    private static async void Forget(Task task) => await task;

    private Task _toTurn = Task.CompletedTask; // the lines written to a turn's stdin, one after another

    // One line to the stdin of the process that asked (a turn's or a run's), off the UI thread: a long steer waits until the
    // engine reads it, which must not freeze the window. Every writer comes through here, so lines never interleave.
    private void ToTurn(Process? asker, string line)
    {
        if (asker is null)
            return;
        _toTurn = Write(_toTurn);
        async Task Write(Task before)
        {
            await before;
            try
            {
                await Task.Run(() => { asker.StandardInput.WriteLine(line); asker.StandardInput.Flush(); });
            }
            catch (Exception) { } // the turn already ended (or any failure): a faulted write would stop every later one
        }
    }

    // Deletes the turn's request file and the temp folder (never a real project). Idempotent: the app closing mid-reply
    // and the turn's own cleanup may both run it.
    private void CleanupChatTemp()
    {
        var (file, folder) = (_chatRequestFile, _chatTempFolder);
        _chatRequestFile = _chatTempFolder = null;
        if (file is not null)
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (folder is not null)
            try { Directory.Delete(folder, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // The user's message (none for a Code action) and an empty reply appear, and the GPU-sharing lock goes on.
    private string? _pendingUser;
    private DateTimeOffset _pendingUserTime;
    private readonly List<(string Said, string Text, DateTimeOffset Time)> _steered = new(); // this turn's delivered steers, and the reply before each

    private void StartChatTurn(string text) => StartTurn(_chat, text, ChatTarget()?.Role ?? "");

    // queued: the message came from the queue, so the composer's attachments belong to the next message and stay.
    private void StartTurn(Conversation c, string? text, string role, bool queued = false)
    {
        _steered.Clear();
        _turnSteers = c.IsCode && text is not null && PiEngine;
        _turn = c;
        _turnWorker = _settings.ChatWorker;
        _pendingUser = text;
        _turnLoaded = new();
        _codeStartRequested = false;
        _turnTitle = null; // a title from an earlier, unsaved turn never lands on this one
        _pendingUserTime = Now();
        _turnUserImages = text is null ? [] : _turnImages.ToList();
        (_turnShots, _pendingShot) = ([], null);
        if (text is not null)
        {
            AddUserBubble(text, _pendingUserTime, _turnUserImages);
            SavePendingTurn(c, text);
            if (!queued)
            {
                AttachmentsOf(c).Clear(); // they went with this message
                RefreshAttachments(c);
            }
        }
        _reply = AddReply(role);
        _chatError = null;
        _chatStopRequested = false;
        _activeRole = null;
        SetChatBusy(true);
    }

    // The user's message bubble and an empty reply (header, content, status, Copy), added to _turn's conversation.
    private void AddUserBubble(string text, DateTimeOffset? time, IReadOnlyList<string>? images = null)
    {
        var photos = ThumbRow(images, 120, HorizontalAlignment.Right);
        var shownText = BubbleText(text, photos is null ? null : images!.Where(File.Exists));
        // with emoji: a TextBlock, which can show them in colour (not selectable; Copy below copies the message)
        FrameworkElement box = Markdown.HasEmoji(shownText) ? Markdown.EmojiTextBlock(shownText, ReadingFont).WithForeground("TextPrimary")
            : BodyText(shownText, mono: false, muted: false, conv: _turn);
        box.SetValue(TextElement.FontSizeProperty, ReadingFont);
        box.SetValue(TextElement.FontWeightProperty, Markdown.TextWeight); // as replies: Light
        var bubble = new Border
        {
            Child = box, Padding = new Thickness(14, 10, 14, 10), MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Right, // no outline: a quiet fill, as Claude's
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "Hover");
        bubble.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        var (row, stamp, _) = MessageFooter(HorizontalAlignment.Right, () => TypedText(text)); // Copy: what the user wrote, not the attached files
        SetStamp(stamp, time);
        var message = new StackPanel { Children = { bubble, row }, Margin = new Thickness(60, 10, 0, 6), HorizontalAlignment = HorizontalAlignment.Right };
        if (photos is not null)
        {
            photos.Margin = new Thickness(0, 0, -6, 0); // the last thumbnail's gap
            message.Children.Insert(0, photos);
        }
        if (photos is not null && shownText is "(see the attachments)" or "")
            bubble.Visibility = Visibility.Collapsed; // photos alone: no empty bubble under them
        ChatAdd(message);
    }

    // ---- message footers: a muted relative timestamp (exact time on hover) and a copy icon under every message

    private readonly List<(TextBlock Label, DateTimeOffset Time)> _stamps = new();
    private DispatcherTimer? _stampTimer; // one for the window; a minute is as fine as the text gets
    internal Func<DateTimeOffset> Now = () => DateTimeOffset.Now; // --ui-smoke steps its own
    private Action<string>? _copyMessageHook; // --ui-smoke: receives the copied message instead of the clipboard

    private (StackPanel Row, TextBlock Stamp, Button Copy) MessageFooter(HorizontalAlignment align, Func<string> text)
    {
        var stamp = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        stamp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var glyph = new TextBlock { Text = Markdown.CopyGlyph, FontSize = 12 };
        glyph.SetResourceReference(StyleProperty, "RailGlyph");
        var copy = new Button { Content = glyph, Width = 24, Height = 24, Padding = new Thickness(0), ToolTip = "Copy", Cursor = System.Windows.Input.Cursors.Hand };
        copy.SetResourceReference(StyleProperty, "GhostButton");
        copy.Click += (_, _) =>
        {
            if (_copyMessageHook is { } hook)
                hook(text());
            else
                CopyToClipboard(text(), "Copied", "Nothing to copy");
            glyph.Text = Markdown.CopiedGlyph; // a check for a moment
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) => { timer.Stop(); glyph.Text = Markdown.CopyGlyph; };
            timer.Start();
        };
        _stampTimer ??= StartStampTimer();
        return (new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, Margin = new Thickness(0, 2, 0, 0), Children = { stamp, copy } }, stamp, copy);
    }

    private DispatcherTimer StartStampTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) => RefreshStamps();
        timer.Start();
        return timer;
    }

    // Shows the time (nothing for an old saved message without one) and keeps it fresh.
    private void SetStamp(TextBlock label, DateTimeOffset? time)
    {
        _stamps.RemoveAll(s => s.Label == label);
        if (time is not { } t)
        {
            label.Text = "";
            return;
        }
        label.ToolTip = t.ToLocalTime().ToString("f");
        label.Text = LauncherLogic.RelativeTime(t, Now());
        _stamps.Add((label, t));
    }

    internal void RefreshStamps()
    {
        _stamps.RemoveAll(s => PresentationSource.FromVisual(s.Label) is null); // cleared conversations
        foreach (var (label, time) in _stamps)
            label.Text = LauncherLogic.RelativeTime(time, Now());
    }

    private ChatReply AddReply(string role)
    {
        var root = new StackPanel { Margin = new Thickness(0, 8, 0, 14) };
        var reply = new ChatReply { Content = new StackPanel() };
        root.Tag = reply; // View thinking finds the replies by it
        var header = new TextBlock { Text = ChatName(CardFor(role), role), Margin = new Thickness(2, 0, 0, 6) };
        header.SetResourceReference(StyleProperty, "SectionHeader");
        header.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
        reply.Status = new TextBlock { Text = "Starting…", FontStyle = FontStyles.Italic, FontSize = 12, Margin = new Thickness(2, 4, 0, 0) };
        reply.Status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        (reply.Footer, reply.Stamp, reply.Copy) = MessageFooter(HorizontalAlignment.Left, () => reply.Final ?? reply.AnswerText.ToString());
        reply.Footer.Visibility = Visibility.Collapsed;
        if (Markdown.Roomy) // the stats stand back from the answer
            (reply.Footer.Margin, reply.Footer.Opacity) = (new Thickness(0, 10, 0, 0), 0.8);
        reply.Stats = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        reply.Stats.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        reply.Footer.Children.Add(reply.Stats);
        root.Children.Add(header);
        root.Children.Add(reply.Content);
        root.Children.Add(reply.Status);
        root.Children.Add(reply.Footer);
        ChatAdd(root);
        return reply;
    }

    // After the process ended (its output already applied): close the open section, show errors, remember the exchange.
    // Returns true when the turn counts as stopped: a clean exit with the final reply wins over a Stop that came too late.
    private bool FinishChatTurn(int exitCode, bool stopped, string? failure)
    {
        var c = _turn;
        var completed = false;
        var kept = false; // a stopped or failed turn kept in the history: the message and what of the reply arrived
        FlushMarkdown(); // a stopped or failed turn shows all it received
        if (_reply is { } reply)
        {
            EndChatBlock();
            reply.Status.Visibility = Visibility.Collapsed;
            var answer = reply.Final ?? (reply.AnswerText.Length > 0 ? reply.AnswerText.ToString() : null);
            stopped &= !(failure is null && exitCode == 0 && reply.Final is not null);
            string? problem = stopped ? null : failure is not null || exitCode != 0 ? failure ?? _chatError ?? $"The chat failed (exit code {exitCode})."
                : answer is null ? "The worker returned no reply." : null;
            if (stopped)
                AddChatNote(reply, "Stopped.");
            else if (problem is not null)
                AddChatNote(reply, problem, error: true);
            if ((stopped || problem is not null) && _pendingUser is { } lost)
            {
                var note = stopped ? "(Stopped before the reply finished.)" : $"(The reply failed: {problem})";
                c.History.AddRange(TurnMessages(lost));
                c.History.Add(new LauncherLogic.ChatMessage("assistant", answer is { Length: > 0 } ? $"{answer.TrimEnd()}\n\n{note}" : note, Now(), TurnShots()));
                kept = true;
            }
            if (!stopped && problem is null)
            {
                if (reply.Final is null)
                    SetChatFinal(answer);
                if (_pendingUser is { } user) // a turn without a user message stays out of the history: roles must alternate
                {
                    c.History.AddRange(TurnMessages(user));
                    c.History.Add(new LauncherLogic.ChatMessage("assistant", answer, Now(), TurnShots()));
                }
                completed = true;
            }
            if (answer is not null)
            {
                SetStamp(reply.Stamp, Now());
                if ((reply.Pending ?? reply.Last) is { } call)
                    reply.Stats.ToolTip = reply.Stats.Text = $"{call} · {LauncherLogic.TotalsLine(reply.TurnTokens, c.Tokens)}";
            }
            reply.Footer.Visibility = answer is null ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var card in _cards)
        {
            card.ClearLive();
            card.IsActive = false;
            card.Status = "Idle";
        }
        _activeRole = null;
        _chatClosed.Clear();
        _callLive.Clear();
        var loadedThisTurn = _turnLoaded.ToList();
        _turnLoaded = new();
        if (!_settings.ChatKeepLoaded)
        {
            UnloadChat(loadedThisTurn);
            StopLocalServer();
        }
        SetChatBusy(false);
        if (c.Input.IsVisible)
            c.Input.Focus();
        _turnCompleted = completed;
        if (c.IsCode && completed)
            CodeTurnFinished();
        else if (completed || kept) // a failed or stopped turn too: the message and what arrived, with a note (roles alternate)
        {
            if (c.IsCode)
                SaveCodeConversation();
            else
                SaveChatConversation();
        }
        if (_turn == _parked) // its chat is saved; the parked view goes (opening the chat loads it from the store)
            (_parked, _parkedConv, _parkedContext) = (null, null, null);
        return stopped;
    }

    private long _pendingSaved; // when SavePendingTurn last wrote the turn in progress (Environment.TickCount64)

    // Closing mid-reply: the message and the reply so far stay saved, with the interrupted note.
    private void SaveInterruptedTurn()
    {
        if (_chatBusy && _pendingUser is { } user && _turn != _parked)
            SavePendingTurn(_turn, user, _reply?.AnswerText.ToString() ?? "");
    }

    internal const string InterruptedReply = "(No reply: the app closed before this reply finished.)";

    // Saved before the reply streams, so a crash or a killed app keeps the message, with a note in place of the reply. The
    // turn's end saves over it: the real exchange, or the history without it when the turn failed.
    // partial: the reply's text so far (saved again while it streams, and when the app closes), kept before the note.
    private void SavePendingTurn(Conversation c, string text, string partial = "")
    {
        _pendingSaved = Environment.TickCount64;
        var reply = partial.Trim().Length > 0 ? $"{partial.TrimEnd()}\n\n{InterruptedReply}" : InterruptedReply;
        var pending = new List<LauncherLogic.ChatMessage>(c.History);
        pending.AddRange(TurnMessages(text));
        pending.Add(new("assistant", reply, _pendingUserTime, TurnShots()));
        if (c.IsCode)
        {
            if (_codeConv is { } code)
            {
                (code.Messages, code.Summary, code.SummaryCovers) = (pending, c.Summary, c.SummaryCovers);
                StoreDo(() => _store.Save(code));
            }
        }
        else
        {
            if (_chatConv is null)
                StoreDo(() => _chatConv = NewStoredChat());
            if (_chatConv is { } chat)
            {
                (chat.Worker, chat.Messages, chat.Summary, chat.SummaryCovers) = (_settings.ChatWorker, pending, c.Summary, c.SummaryCovers);
                StoreDo(() => _store.Save(chat));
            }
        }
        RefreshSidebarList();
    }

    internal const string SteeredReply = "(Still working when the next message arrived.)";

    // The turn's messages before its final reply, for the history: the user's, then per delivered steer the reply so far
    // (a placeholder when a tool call cleared it: empty messages are dropped and roles must alternate) and the steered message.
    private IEnumerable<LauncherLogic.ChatMessage> TurnMessages(string user)
    {
        yield return new("user", user, _pendingUserTime, _turnUserImages.Count > 0 ? _turnUserImages : null);
        foreach (var (said, text, time) in _steered)
        {
            yield return new("assistant", said.Trim().Length > 0 ? said.TrimEnd() : SteeredReply, time);
            yield return new("user", text, time);
        }
    }

    private void SetChatBusy(bool busy)
    {
        _chatBusy = busy;
        if (!busy)
            _steering.Clear(); // never delivered: queued rows again, sent as the next turn (or back in the input) as usual
        RefreshQueue(_code); // Send now while a Pi Code turn runs
        AddWorkerButton.IsEnabled = PresetsButton.IsEnabled = !busy;
        foreach (var card in _cards)
            card.Editable = !busy;
        SetStatus(busy ? "Replying" : "Ready", busy ? "#8B7CFF" : "#667085");
        SetActivity(busy);
        TimeTask(busy);
        RefreshChatInput();
        RefreshSidebarList(); // the running chat's marker
    }

    // ---- streaming into the reply

    private void NoteChatLine(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            _chatError = t.Length > 6 ? t[6..].TrimStart(':', ' ') : t;
    }

    private void HandleChatEvent(JsonElement ev)
    {
        var role = Str(ev, "role");
        var card = CardForActivity(role);
        var name = ChatName(card, role);
        QueueRegroup(); // View thinking off: the event's new steps fold into their row once it is handled
        switch (Str(ev, "e"))
        {
            case "loading":
                EndChatBlock();
                Activate(role, "Loading model");
                if (Str(ev, "path").Length > 0 && LoadedOn(ev) is { } loaded)
                {
                    _loadedByChat.Add(loaded);
                    _turnLoaded.Add(loaded);
                }
                if (_turn.IsCode)
                    WorkerContextsCleared();
                SetChatStatus($"Loading the {name} model…");
                Log($"[{name}] loading {Str(ev, "path")} ...");
                break;
            case "model":
                EndChatBlock();
                Activate(role, "Working");
                card?.SetModel(ModelInfoFor(role, Str(ev, "id")));
                SetChatStatus($"{name} is working…");
                Log($"[{name}] {Str(ev, "id")}");
                break;
            case "done":
                EndChatBlock();
                break;
            case "usage":
                Activate(role, "Working");
                card?.AddUsage((long)Num(ev, "prompt"), (long)Num(ev, "completion"), Num(ev, "tps"));
                RecordUsage((long)(Num(ev, "prompt") + Num(ev, "completion")));
                ChatUsage(role, LauncherLogic.StatsLine(name, Num(ev, "tps"), (long)Num(ev, "completion"), (long)Num(ev, "prompt")), Num(ev, "tps"), (long)(Num(ev, "prompt") + Num(ev, "completion")));
                break;
            case "chat_stage":
                EndChatBlock();
                // The lead's text (Code) streams as the answer; a delegation's goes into a section named after the sub-agent.
                if (_reply is { } r)
                    (r.Stage, r.StageTitle) = Str(ev, "stage") switch
                    {
                        "lead" => ("answer", ""),
                        "delegate" => ("delegate", name),
                        var s => (s, Str(ev, "title")),
                    };
                // Before its first output the model reads the whole prompt (conversation, files, images): nothing to show yet
                SetChatStatus($"{name} is reading the conversation (no output yet)…");
                break;
            case "chat_reply" or "code_reply":
                SetChatFinal(Str(ev, "text"));
                break;
            case "chat_title":
                _turnTitle = Str(ev, "text");
                break;
            // A Send now message reached the model: the reply so far closes, the message shows as the user's and the reply
            // goes on below it, as a reopened conversation shows it.
            case "steered":
                if (!_steering.Remove(Str(ev, "key"), out var steered))
                    break;
                _codeQueue.Remove(steered.Item);
                RefreshQueue(_code);
                FlushMarkdown();
                EndChatBlock();
                var before = _reply;
                if (before is not null)
                    before.Status.Visibility = Visibility.Collapsed;
                _steered.Add((before?.Final ?? before?.AnswerText.ToString() ?? "", steered.Text, Now()));
                AddUserBubble(steered.Text, Now());
                _reply = AddReply(role);
                _reply.TurnTokens = before?.TurnTokens ?? 0;
                if (_pendingUser is { } steeredUser && _turn != _parked)
                    SavePendingTurn(_turn, steeredUser); // a crash keeps the steered message
                break;
            case "context":
                TurnContext(ContextSnapshot.From(ev));
                break;
            case "tool":
                EndChatBlock();
                _toolArgs.Clear();
                if (_reply is { Pending: { } step } stepReply) // text before a tool call: its call's stats under it
                {
                    stepReply.Pending = null;
                    var line = new TextBlock { Text = step, FontSize = 11, Margin = new Thickness(2, 2, 0, 4), TextWrapping = TextWrapping.Wrap, Tag = "stats" };
                    line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    stepReply.Content.Children.Add(line);
                }
                if (_reply is { } toolReply)
                    DetachAnswer(toolReply);
                Activate(role, "Using tools");
                SetChatStatus($"{name} is working…"); // the tool has run (its result is shown); the model reads it next
                BrowserFollowTool(Str(ev, "name"), ev.TryGetProperty("args", out var shown) && shown.ValueKind != JsonValueKind.String ? shown.GetRawText() : Str(ev, "args"), Str(ev, "result"));
                AddCodeTool(Str(ev, "name"), ev.TryGetProperty("args", out var args) && args.ValueKind != JsonValueKind.String ? args.GetRawText() : Str(ev, "args"), Str(ev, "result"));
                break;
            // compacted: the engine summarized earlier steps to fit the context window; history_compacted: earlier messages,
            // before the turn (the summary is kept, so later turns send it in their place). Their print lines are in the log.
            case "compacted" or "history_compacted":
                if (Str(ev, "e") == "history_compacted" && Str(ev, "summary") is { Length: > 0 } summary)
                    (_turn.Summary, _turn.SummaryCovers) = (summary, (int)Num(ev, "covers"));
                // the ring drops now, not at the next call's context event; in Code, the compacted worker's card
                if ((_turn == _parked ? _parkedContext : _turn.IsCode ? _codeContexts.GetValueOrDefault(role.Length > 0 ? role : CodeLead) : ContextOf(false)) is { } full)
                    TurnContext(full.Compacted((long)Num(ev, "after")));
                EndChatBlock();
                if (_reply is { } compactReply)
                {
                    DetachAnswer(compactReply); // text after it starts below the line
                    var note = new TextBlock { Text = CompactedLine(ev), FontSize = 11, Margin = new Thickness(2, 2, 0, 4), TextWrapping = TextWrapping.Wrap, Tag = "stats" };
                    note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    compactReply.Content.Children.Add(note);
                    compactReply.Content.Children.Add(CompactionRule());
                }
                break;
            case "approval": // the sandboxed run_command asks for network or more folders: the user decides
                SetChatStatus("Waiting for your approval…");
                AnswerApproval(ev);
                break;
            case "browser": // the model's browser tool: the in-app browser opens on the page
                SetChatStatus("Looking at the page in the browser…");
                Forget(AnswerBrowserAsync(ev));
                break;
            case "code_project":
                Log($"[{name}] wrote {Str(ev, "path")}");
                Toast("PROJECT.md written");
                break;
            case "code_start":
                _codeStartRequested = true;
                break;
        }
    }

    private static string CompactedLine(JsonElement ev) => $"Context compacted · {Tokens((long)Num(ev, "before"))} → {Tokens((long)Num(ev, "after"))} tokens";

    private readonly List<StreamBlock> _chatClosed = new(); // blocks closed since their call's usage event

    // The usage event follows the call's done: the line goes to every block that call closed (sections now, the answer's in the footer).
    private void ChatUsage(string role, string line, double tps, long total)
    {
        if (_reply is not { } r)
            return;
        r.TurnTokens += total;
        _turn.Tokens += total;
        r.Last = line;
        _callLive.Remove(role);
        foreach (var block in _chatClosed.Where(b => b.Role == role).ToList())
        {
            _chatClosed.Remove(block);
            if (block.Expander is null)
                r.Pending = line;
            else
                AddStatsLine(block, line, tps);
        }
    }

    private void SetChatStatus(string text)
    {
        if (_reply is not { } r)
            return;
        r.Status.Text = text;
        r.Status.Visibility = Visibility.Visible;
    }

    // A Code turn's tool call as its arguments stream (a whole-file write can take minutes): its name, file and size so far
    // in the reply's status, so a long call is visibly alive. Reset when other output or the call's result arrives.
    private readonly StringBuilder _toolArgs = new();
    private static readonly System.Text.RegularExpressions.Regex ToolPath = new("\"(?:path|file_path)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");

    private void AppendChatDelta(string role, string kind, string text, string name = "")
    {
        Activate(role, kind == "thinking" ? "Thinking" : kind == "tool" ? "Using tools" : "Working");
        if (_reply is not { } r)
            return;
        QueueRegroup();
        if (kind == "tool")
        {
            _toolArgs.Append(text);
            var path = ToolPath.Match(_toolArgs.ToString()) is { Success: true } m ? " " + System.IO.Path.GetFileName(m.Groups[1].Value.Replace("\\\\", "\\")) : "";
            SetChatStatus($"Writing {(name.Length > 0 ? name + path : path.Length > 0 ? path.TrimStart() : "a tool call")}… {_toolArgs.Length:N0} characters");
            return; // the call itself shows once it has run (AddCodeTool)
        }
        _toolArgs.Clear();
        r.Status.Visibility = Visibility.Collapsed; // the thinking or text itself shows what is happening
        if (r.Block is null || r.Block.Kind != kind)
        {
            EndChatBlock();
            r.Block = StartChatBlock(r, role, kind);
        }
        var block = r.Block;
        block.Text.Append(text);
        block.Body.AppendText(text);
        if (kind == "text" && _pendingUser is { } user && _turn != _parked && Environment.TickCount64 - _pendingSaved > 10_000)
            SavePendingTurn(_turn, user, r.AnswerText.ToString() + (block.Expander is null ? "" : text)); // a crash keeps most of it
        if (block.View is { } view)
            RenderSoon(view);
        if (block.Expander is null)
            r.AnswerText.Append(text);
        else if (block.Preview is not null && block.Text.Length <= 300)
            block.Preview.Text = PreviewFor(block);
        if (!_turn.Follow)
            Reveal(_turn.Jump, true, slide: 10);
    }

    private StreamBlock StartChatBlock(ChatReply r, string role, string kind)
    {
        var block = new StreamBlock { Role = role, Kind = kind };
        if (kind == "text" && r.Stage == "answer")
        {
            block.Body = EnsureAnswer(r); // the answer streams straight into the prominent text, not a section
            block.View = r.Rendered;
            r.Content.Children.Add(block.Live = NewLiveLine());
            return block;
        }
        DetachAnswer(r);
        block.Body = BodyText("", mono: false, muted: kind == "thinking", conv: _turn);
        block.View = MarkdownView(block.Body);
        var content = new StackPanel();
        content.Children.Add(block.Body);
        content.Children.Add(block.View);
        var stage = r.StageTitle.Length > 0 ? r.StageTitle : r.Stage;
        var title = kind == "thinking" ? (r.Stage == "delegate" ? $"THINKING  ·  {stage.ToUpperInvariant()}" : "THINKING") : stage.ToUpperInvariant();
        block.Expander = BuildExpander(title, "", content, out block.Preview);
        block.Label = (TextBlock)((StackPanel)block.Expander.Header).Children[0];
        block.Expander.Expanded += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        block.Expander.Collapsed += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        r.Content.Children.Add(block.Expander);
        r.Content.Children.Add(block.Live = NewLiveLine()); // outside the section, so it shows while the section is folded
        FadeIn(block.Expander);
        SetPulse(block, true);
        return block;
    }

    // Answer text that streamed before a tool call or a new section stays where it is; text after them starts a new answer
    // below, so the reply reads in the order it happened. The final reply replaces only that last answer.
    private static void DetachAnswer(ChatReply r)
    {
        if (r.Answer is null || r.Final is not null)
            return;
        (r.Answer, r.Rendered) = (null, null);
        r.AnswerText.Clear();
    }

    private TextBox EnsureAnswer(ChatReply r)
    {
        if (r.Answer is not null)
            return r.Answer;
        var box = r.Answer = BodyText("", mono: false, muted: false, conv: _turn);
        box.FontSize = ReadingFont;
        box.Margin = new Thickness(2, 4, 0, 0);
        r.Content.Children.Add(box);
        r.Rendered = MarkdownView(box);
        r.Content.Children.Add(r.Rendered);
        return box;
    }

    // The orchestrator's final reply is authoritative: it replaces whatever streamed into the answer.
    private void SetChatFinal(string text)
    {
        if (_reply is not { } r)
            return;
        r.Final = text;
        // A model that answered inside its thinking (a template that opens <think> and a model that never closes it): the
        // engine sends that text as the reply, so the section repeating it goes and the answer shows once.
        if (r.Answer is null)
            foreach (var section in r.Content.Children.OfType<Expander>().ToList())
                if (section.Content is StackPanel { Children.Count: > 0 } body && body.Children[0] is TextBox thought && thought.Text.Trim() == text.Trim())
                    r.Content.Children.Remove(section);
        EnsureAnswer(r).Text = text;
        r.AnswerText.Clear().Append(text);
        ShowMarkdown(r);
        if (!_turn.Follow)
            Reveal(_turn.Jump, true, slide: 10);
    }

    // Streamed text (the answer and the thinking / stage sections) shows as Markdown with coloured code: the TextBox keeps the
    // raw text, hidden, and a read-only RichTextBox shows it rendered. While it streams, every frame reveals a little more of
    // it (a typewriter) and re-renders only the tail after its last finished block.
    private sealed class MarkdownState
    {
        public required TextBox Raw;
        public int Shown; // characters of Raw.Text on screen
        public int Stable, StableBlocks; // the kept prefix (Markdown.StableLength) and the document blocks rendered from it
    }

    private readonly HashSet<RichTextBox> _markdownDirty = new(); // views still revealing
    private bool _framing; // OnMarkdownFrame is subscribed
    private double _frameAt;
    private Func<double> _clockMs = () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; // --ui-smoke steps its own

    private RichTextBox MarkdownView(TextBox raw)
    {
        var view = new RichTextBox
        {
            IsReadOnly = true, IsDocumentEnabled = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Margin = raw.Margin, FontFamily = FontFamily, Cursor = Cursors.IBeam, Tag = new MarkdownState { Raw = raw },
        };
        view.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentLight");
        ForwardWheel(view, _turn);
        raw.Visibility = Visibility.Collapsed;
        Render(view);
        return view;
    }

    // All of the raw text, rendered from scratch.
    private void Render(RichTextBox view)
    {
        var s = (MarkdownState)view.Tag;
        var text = s.Raw.Text;
        view.Document = DocumentOf(s, text);
        Markdown.NoTopMargin(view.Document.Blocks.FirstBlock);
        WireCodeBlocks(view.Document.Blocks);
        (s.Shown, s.Stable, s.StableBlocks) = (text.Length, 0, 0);
    }

    private static FlowDocument DocumentOf(MarkdownState s, string markdown) =>
        Markdown.ToDocument(markdown, s.Raw.FontSize, muted: s.Raw.FontStyle == FontStyles.Italic); // BodyText's muted text is italic

    private void WireCodeBlocks(IEnumerable<Block> blocks)
    {
        foreach (var card in Markdown.CodeBlocks(blocks))
        {
            // on screen only while loaded: a re-render's new document unloads the old cards
            card.Loaded += (_, _) => { _codeBlocks.Add(card); PinCodeButtons(); };
            card.Unloaded += (_, _) => _codeBlocks.Remove(card);
            CopyButtonOf(card).Click += (sender, _) => CopyCode((Button)sender);
        }
    }

    // The first `shown` characters: the previous tail's blocks go, newly finished blocks are rendered once and kept, and
    // the unfinished tail is rendered again.
    private void RenderShown(RichTextBox view, MarkdownState s, string shown)
    {
        var blocks = view.Document.Blocks;
        while (blocks.Count > s.StableBlocks)
        {
            var last = blocks.LastBlock;
            blocks.Remove(last);
            foreach (var card in Markdown.CodeBlocks([last]))
                _codeBlocks.Remove(card);
        }
        var stable = Markdown.StableLength(shown, s.Stable);
        if (stable > s.Stable)
        {
            s.StableBlocks += AddBlocks(view, s, shown[s.Stable..stable]);
            s.Stable = stable;
        }
        AddBlocks(view, s, shown[stable..]);
    }

    private int AddBlocks(RichTextBox view, MarkdownState s, string markdown)
    {
        var source = DocumentOf(s, markdown);
        var moved = source.Blocks.ToList();
        if (view.Document.Blocks.Count == 0)
            Markdown.NoTopMargin(moved.FirstOrDefault());
        WireCodeBlocks(moved);
        foreach (var block in moved)
        {
            source.Blocks.Remove(block); // a block belongs to one document at a time
            view.Document.Blocks.Add(block);
        }
        return moved.Count;
    }

    // ---- code blocks: a copy button that stays at the top right of the block's visible part

    private readonly HashSet<Border> _codeBlocks = new(); // rendered code blocks currently loaded
    private Action<string>? _copyCodeHook; // --ui-smoke: receives the copied code instead of the clipboard

    private static Button CopyButtonOf(Border card) => (Button)((Grid)card.Child).Children[1];

    private void CopyCode(Button copy)
    {
        var code = (string)copy.Tag;
        if (_copyCodeHook is { } hook)
            hook(code);
        else
            CopyToClipboard(code, "Code copied", "Code block is empty");
        var glyph = (TextBlock)copy.Content;
        glyph.Text = Markdown.CopiedGlyph; // a check while the toast shows
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { timer.Stop(); glyph.Text = Markdown.CopyGlyph; };
        timer.Start();
    }

    // While a block's top is scrolled above its conversation's viewport the button slides down with the viewport's top,
    // until it meets the block's bottom padding; then it leaves with the block.
    private void PinCodeButtons()
    {
        foreach (var card in _codeBlocks)
        {
            var scroll = _chat.Scroll.IsAncestorOf(card) ? _chat.Scroll : _code.Scroll.IsAncestorOf(card) ? _code.Scroll : null;
            if (scroll is null || !card.IsVisible)
                continue;
            var copy = CopyButtonOf(card);
            var top = card.TransformToAncestor(scroll).Transform(new Point()).Y;
            var room = card.ActualHeight - card.Padding.Top - card.Padding.Bottom - copy.ActualHeight;
            ((TranslateTransform)copy.RenderTransform).Y = Math.Clamp(-top, 0, Math.Max(0, room));
        }
    }

    // New text for a view: frames run (on WPF's render loop) until every view shows all of its text.
    private void RenderSoon(RichTextBox view)
    {
        _markdownDirty.Add(view);
        if (_framing)
            return;
        _framing = true;
        _frameAt = _clockMs();
        CompositionTarget.Rendering += OnMarkdownFrame;
    }

    private void OnMarkdownFrame(object? sender, EventArgs e) => MarkdownFrame();

    // One frame: each streaming view shows more of what it received, max(1, backlog * dt / 120 ms) characters, so a burst
    // is caught up within about 120 ms and the text never lags far behind.
    private void MarkdownFrame()
    {
        var now = _clockMs();
        var dt = now - _frameAt;
        _frameAt = now;
        foreach (var view in _markdownDirty.ToList())
        {
            var s = (MarkdownState)view.Tag;
            var text = s.Raw.Text;
            var backlog = text.Length - s.Shown;
            if (backlog > 0)
            {
                s.Shown += Math.Clamp((int)Math.Ceiling(backlog * dt / 120), 1, backlog);
                if (s.Shown < text.Length && char.IsHighSurrogate(text[s.Shown - 1]))
                    s.Shown++; // never half an emoji
                RenderShown(view, s, text[..s.Shown]);
            }
            if (s.Shown >= text.Length)
                _markdownDirty.Remove(view);
        }
        if (_markdownDirty.Count == 0)
            StopFrames();
    }

    private void StopFrames()
    {
        if (_framing)
            CompositionTarget.Rendering -= OnMarkdownFrame;
        _framing = false;
    }

    // The turn ended: everything still revealing shows in full, rendered from scratch.
    private void FlushMarkdown()
    {
        foreach (var view in _markdownDirty)
            Render(view);
        _markdownDirty.Clear();
        StopFrames();
    }

    // The final reply replaces whatever streamed; it and every section still revealing show in full now.
    private void ShowMarkdown(ChatReply r)
    {
        EnsureAnswer(r);
        _markdownDirty.Add(r.Rendered!);
        FlushMarkdown();
    }

    private void EndChatBlock()
    {
        if (_reply?.Block is not { } block)
            return;
        _reply.Block = null;
        _chatClosed.Add(block);
        DropLive(block); // the call's live line moves to its next block; its usage event adds the exact stats
        if (block.Expander is null)
            return;
        SetPulse(block, false);
        if (block.Preview is not null)
            block.Preview.Text = PreviewFor(block);
        Fold(block);
    }

    private void AddChatNote(ChatReply r, string text, bool error = false)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0), FontSize = 12 };
        if (error)
            note.Foreground = (Brush)new BrushConverter().ConvertFromString("#FF667A")!;
        else
            note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        r.Content.Children.Add(note);
    }

    // ---- conversation list

    // Adds to the running turn's conversation.
    private void ChatAdd(UIElement element)
    {
        _turn.Empty.Visibility = Visibility.Collapsed;
        _turn.Panel.Children.Add(element);
        if (!_turn.Follow)
            Reveal(_turn.Jump, true, slide: 10);
        FadeIn(element);
    }

    // Same follow-the-tail rule as Model Output: stay at the end unless the user scrolled up.
    private void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var c = ConvOf(sender);
        PinCodeButtons();
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            c.Follow = c.Scroll.VerticalOffset >= c.Scroll.ScrollableHeight - 8;
            if (c.Follow)
                Reveal(c.Jump, false, slide: 10);
        }
        else if (c.Follow && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0))
            c.Scroll.ScrollToEnd();
    }

    private void ChatJump_Click(object sender, RoutedEventArgs e) => Jump(ConvOf(sender));

    private void Jump(Conversation c)
    {
        c.Follow = true;
        c.Scroll.ScrollToEnd();
        Reveal(c.Jump, false, slide: 10);
    }
}

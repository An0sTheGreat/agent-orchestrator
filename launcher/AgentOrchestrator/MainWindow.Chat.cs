using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrchestrator;

// Chat view: a conversation with one worker (Single) or with Planner -> Implementer -> Reviewer (Multi).
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
        public required TextBlock Hint;
        public readonly List<LauncherLogic.ChatMessage> History = new();
        public bool Follow = true;
        public bool Owns(object o) => o == Input || o == Send || o == Scroll || o == Jump || o == Plus || o == Chip;
    }

    private Conversation _chat = null!, _code = null!, _turn = null!; // _turn: the conversation of the running (or last) turn
    private List<LauncherLogic.ChatMessage> _chatHistory => _chat.History;
    private Conversation ConvOf(object sender) => _code.Owns(sender) ? _code : _chat;
    private bool MultiOf(Conversation c) => !c.IsCode && ChatMulti;
    private RoleCard? TargetOf(Conversation c) => c.IsCode ? CodeTarget() : ChatTarget();

    // One assistant reply: sections (thinking, Plan / Draft / Review) above the prominent answer.
    private sealed class ChatReply
    {
        public StackPanel Content = null!;
        public TextBlock Status = null!;
        public Button Copy = null!;
        public bool Multi;
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
            Plus = ChatPlus, Chip = ChatTargetChip, Input = ChatInput, Hint = ChatHint };
        _code = new Conversation { IsCode = true, Panel = CodePanel, Scroll = CodeScroll, Empty = CodeEmpty, Jump = CodeJumpPill, Send = CodeSend, Stop = CodeStop,
            Plus = CodePlus, Chip = CodeTargetChip, Input = CodeInput, Hint = CodeHint };
        RefreshExtras();
        if (_settings.ChatMode != "multi")
            _settings.ChatMode = "single";
        if (CardFor(_settings.ChatWorker) is null)
            _settings.ChatWorker = "implementer";
        if (CardFor(_settings.CodeWorker) is null)
            _settings.CodeWorker = "implementer";
        ChatKeepToggle.IsChecked = _settings.ChatKeepLoaded;
        RefreshChatTarget();
        RefreshChatInput();
    }

    private bool ChatMulti => _settings.ChatMode == "multi";

    private RoleCard? ChatTarget() => CardFor(_settings.ChatWorker) ?? CardFor("implementer");

    private static string ChatName(RoleCard? card, string role) =>
        card is { IsCustom: true } ? card.Title : role.Length == 0 ? role : char.ToUpperInvariant(role[0]) + role[1..];

    private const string ChainLabel = "Planner → Implementer → Reviewer";

    private void RefreshChatTarget()
    {
        ChatSingleToggle.IsChecked = !ChatMulti;
        ChatMultiToggle.IsChecked = ChatMulti;
        var target = ChatTarget();
        ChatTargetChip.Content = ChatMulti ? ChainLabel : $"To: {ChatName(target, target?.Role ?? "")}  ▾";
        ChatTargetChip.Cursor = ChatMulti ? Cursors.Arrow : Cursors.Hand;
        var code = CodeTarget();
        CodeTargetChip.Content = $"To: {ChatName(code, code?.Role ?? "")}  ▾";
    }

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
            c.Input.IsEnabled = c.Plus.IsEnabled = c.Chip.IsEnabled = !_chatBusy;
            c.Stop.Visibility = _chatBusy || runActive ? Visibility.Visible : Visibility.Collapsed;
            c.Send.IsEnabled = !_chatBusy && !runActive && !_unloading && _workflowRoot is not null && c.Input.Text.Trim().Length > 0;
            c.Send.Visibility = c.Stop.Visibility == Visibility.Collapsed && c.Input.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            c.Hint.Text = runActive ? $"A project run is using the models; {(c.IsCode ? "Code" : "chat")} is available when it finishes."
                : mine ? "Replying…" : _chatBusy ? $"{(c.IsCode ? "A chat" : "A Code")} reply is using the models; wait for it to finish."
                : _unloading ? "Unloading models…" : "Enter to send  ·  Shift+Enter for a new line";
        }
        ChatSingleToggle.IsEnabled = ChatMultiToggle.IsEnabled = NewChatButton.IsEnabled = ChatKeepToggle.IsEnabled = !_chatBusy;
        CodeNewButton.IsEnabled = CodeFoldersList.IsEnabled = AddCodeFolderButton.IsEnabled = !_chatBusy && !runActive;
        CodeStartButton.IsEnabled = !_chatBusy && !runActive && _workflowRoot is not null;
        CodeProjectButton.IsEnabled = !_chatBusy && !runActive && SelectedProjectPath().Length > 0;
    }

    // ---- worker menu (+ button and target chip)

    private ContextMenu BuildWorkerMenu(FrameworkElement target, Conversation? c = null)
    {
        c ??= _chat;
        RefreshSetupProblems();
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        foreach (var card in _cards)
        {
            var name = ChatName(card, card.Role);
            var broken = card.IsCustom ? card.SetupProblem : null;
            var item = new MenuItem
            {
                Header = broken is null ? name : $"{name}  —  {broken}",
                IsChecked = !MultiOf(c) && card.Role == TargetOf(c)?.Role,
                IsEnabled = broken is null,
                ToolTip = broken is null ? card.ModelName : card.SetupTip,
                Tag = card.Role,
            };
            item.Click += (_, _) => PickWorker(c, card.Role);
            menu.Items.Add(item);
        }
        return menu;
    }

    // Picking a worker makes it the target; in Chat it is the Single-mode one (and switches to Single, so the choice is visible).
    private void PickWorker(Conversation c, string role)
    {
        if (c.IsCode)
            _settings.CodeWorker = role;
        else
            (_settings.ChatWorker, _settings.ChatMode) = (role, "single");
        _settings.Save();
        RefreshChatTarget();
        c.Input.Focus();
    }

    private void PickChatWorker(string role) => PickWorker(_chat, role);

    private void ChatPlus_Click(object sender, RoutedEventArgs e) => BuildWorkerMenu((Button)sender, ConvOf(sender)).IsOpen = true;

    private void ChatTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!MultiOf(ConvOf(sender)))
            BuildWorkerMenu((Button)sender, ConvOf(sender)).IsOpen = true;
    }

    private void ChatMode_Click(object sender, RoutedEventArgs e)
    {
        _settings.ChatMode = ((FrameworkElement)sender).Tag as string == "multi" ? "multi" : "single";
        _settings.Save();
        RefreshChatTarget();
    }

    // Off: unload what chat has loaded right now (the toggle is locked while a reply streams).
    private void ChatKeep_Click(object sender, RoutedEventArgs e)
    {
        _settings.ChatKeepLoaded = ChatKeepToggle.IsChecked == true;
        _settings.Save();
        if (!_settings.ChatKeepLoaded)
            UnloadChat(_loadedByChat.ToList());
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
        _unloadsPending--;
        RefreshChatInput();
    }

    // ---- input

    private void ChatInput_TextChanged(object sender, TextChangedEventArgs e) => RefreshChatInput();

    private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return; // Shift+Enter inserts a new line
        e.Handled = true;
        if (ConvOf(sender).Send.IsEnabled && !_chatBusy)
            Send(ConvOf(sender));
    }

    private void Send(Conversation c)
    {
        if (c.IsCode)
            Forget(SendCode());
        else
            SendChat();
    }

    private void ChatSend_Click(object sender, RoutedEventArgs e)
    {
        if (!_chatBusy)
            Send(ConvOf(sender));
    }

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
        if (_chatBusy)
            return;
        ShowView(false, chat: true);
        ClearChatConversation();
        ChatInput.Focus();
    }

    // ---- saved Chat-view conversations (the sidebar lists them with the Code ones)

    private ConversationStore.Conversation? _chatConv; // the open saved one; null until a chat's first reply saves it

    private void ClearChatConversation()
    {
        ClearConversation(_chat);
        _chatConv = null;
        RefreshSidebarList();
    }

    // After a completed Chat turn: the first reply creates the saved chat, later ones update it.
    private void SaveChatConversation()
    {
        if (_chatConv is null)
            StoreDo(() => _chatConv = _store.Create("", "chat"));
        if (_chatConv is { } c)
        {
            (c.Mode, c.Worker, c.Messages) = (_settings.ChatMode, _settings.ChatWorker, _chatHistory.ToList());
            StoreDo(() => _store.Save(c));
        }
        RefreshSidebarList();
    }

    // A sidebar row: shows the saved chat in the Chat view with its mode and worker. Refused while a reply streams.
    private void OpenChatConversation(string id)
    {
        if (_chatBusy)
        {
            Toast("Wait for the reply to finish", "#F7C873");
            RefreshSidebarList(); // the clicked row unchecks itself
            return;
        }
        if (_chatConv?.Id != id && _store.Get(id) is { } saved)
        {
            ClearConversation(_chat);
            _chatConv = saved;
            _settings.ChatMode = saved.Mode;
            if (CardFor(saved.Worker) is not null)
                _settings.ChatWorker = saved.Worker;
            _settings.Save();
            Replay(_chat, saved.Messages, saved.Mode == "multi", ChatTarget()?.Role ?? "");
        }
        ShowView(false, chat: true);
    }

    // Saved messages into a cleared conversation: user bubbles and finished replies.
    private void Replay(Conversation c, List<LauncherLogic.ChatMessage> messages, bool multi, string role)
    {
        _turn = c;
        foreach (var message in messages)
            if (message.Role == "user")
                AddUserBubble(message.Content);
            else
            {
                var reply = _reply = AddReply(multi, role);
                SetChatFinal(message.Content);
                reply.Status.Visibility = Visibility.Collapsed;
                reply.Copy.Visibility = Visibility.Visible;
            }
        _reply = null;
        c.History.AddRange(messages);
    }

    private void ClearConversation(Conversation c)
    {
        c.Panel.Children.Clear();
        c.History.Clear();
        _reply = null;
        c.Empty.Visibility = Visibility.Visible;
        Jump(c);
    }

    // ---- sending

    private void SendChat()
    {
        var text = ChatInput.Text.Trim();
        if (text.Length > 0)
            Forget(RunTurn(_chat, text, ChatTarget()!.Role, ChatMulti,
                role => LauncherLogic.BuildChatRequest(_settings.ChatMode, role, _chatHistory.Append(new LauncherLogic.ChatMessage("user", text)), CurrentExtras()),
                LauncherLogic.BuildChatStartInfo, $"Chat ({_settings.ChatMode})"));
    }

    // Shared by Chat and Code: the checks, then one orchestrator process whose events stream into a new reply in c.
    // text null: no user message (a Code action such as write_project). buildRequest gets the target role.
    // True when the turn completed with a reply.
    private async Task<bool> RunTurn(Conversation c, string? text, string role, bool multi, Func<string, string> buildRequest,
        Func<string, string, string, string, string, ProcessStartInfo> buildStartInfo, string label)
    {
        if (_chatBusy || _process is not null || _workflowRoot is null)
            return false;
        if (ApiKeyBox.Password.Length == 0)
            TryLoadApiKeyFile();
        _runKey = ApiKeyBox.Password.Trim();
        var unsloth = UsesUnsloth(SelectedProjectPath());
        if (_runKey.Length == 0 && unsloth)
        {
            SetStatus("API key required", "#FF667A");
            Log("Enter your Unsloth API key.");
            ShowView(options: true);
            ApiKeyBox.Focus();
            return false;
        }
        var skipped = new List<string>();
        if (!SyncCustomWorkers(skipped))
        {
            Toast("Message not sent: config.json could not be updated", "#FF667A");
            return false;
        }
        if (!multi && CardFor(role) is { IsCustom: true, SetupProblem: { } problem } target)
        {
            Toast($"{ChatName(target, target.Role)} cannot be used: {problem}", "#F7C873");
            return false;
        }
        foreach (var reason in skipped)
            Log($"Not written to config.json, so it will not take part: {reason}");

        var requestFile = _chatRequestFile = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-chat-{Guid.NewGuid():N}.json");
        var exitCode = -1;
        string? failure = null;
        StartTurn(c, text, multi, role);
        var turn = ++_turnId;
        if (text is not null)
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
            var process = _chatProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };
            _apiBase = ManagementUrl(folder) ?? _apiBase;
            _runStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            Log($"{label} in {folder}");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (unsloth)
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
        try
        {
            DrainLines();
            if (FinishChatTurn(exitCode, _chatStopRequested, failure))
                Toast(c.IsCode ? "Code stopped" : "Chat stopped", "#F7C873");
            return _turnCompleted;
        }
        finally
        {
            // An exception after the process ended must not leave the app busy (unless a newer turn already started).
            if (_chatBusy && _turnId == turn)
                SetChatBusy(false);
            RefreshCodeFolder(); // a folder change during the turn takes effect now that its history is saved
        }
    }

    private int _turnId; // RunTurn's count: tells a turn's own cleanup from one a follow-up turn (StartCodeProject) started

    // Runs a turn without awaiting it; a fault reaches the dispatcher (and fails --ui-smoke) instead of vanishing in the Task.
    private static async void Forget(Task task) => await task;

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

    private void StartChatTurn(string text, bool multi) => StartTurn(_chat, text, multi, ChatTarget()?.Role ?? "");

    private void StartTurn(Conversation c, string? text, bool multi, string role)
    {
        _turn = c;
        _pendingUser = text;
        _turnLoaded = new();
        _codeStartRequested = false;
        if (text is not null)
            AddUserBubble(text);
        _reply = AddReply(multi, role);
        _chatError = null;
        _chatStopRequested = false;
        _activeRole = null;
        SetChatBusy(true);
    }

    // The user's message bubble and an empty reply (header, content, status, Copy), added to _turn's conversation.
    private void AddUserBubble(string text)
    {
        var box = BodyText(text, mono: false, muted: false, conv: _turn);
        box.FontSize = 14;
        var bubble = new Border
        {
            Child = box, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(60, 10, 0, 6), MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Right, BorderThickness = new Thickness(1),
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "Hover");
        bubble.SetResourceReference(Border.BorderBrushProperty, "EdgeHighlight");
        bubble.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
        ChatAdd(bubble);
    }

    private ChatReply AddReply(bool multi, string role)
    {
        var root = new StackPanel { Margin = new Thickness(0, 8, 0, 14) };
        var header = new TextBlock { Text = multi ? ChainLabel : ChatName(CardFor(role), role), Margin = new Thickness(2, 0, 0, 6) };
        header.SetResourceReference(StyleProperty, "SectionHeader");
        header.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
        var reply = new ChatReply { Multi = multi, Content = new StackPanel() };
        reply.Status = new TextBlock { Text = "Starting…", FontStyle = FontStyles.Italic, FontSize = 12, Margin = new Thickness(2, 4, 0, 0) };
        reply.Status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        reply.Copy = new Button { Content = "Copy reply", Padding = new Thickness(10, 4, 10, 4), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed, ToolTip = "Copy the reply" };
        reply.Copy.SetResourceReference(StyleProperty, "GhostButton");
        reply.Copy.Click += (_, _) => CopyToClipboard(reply.Final ?? reply.AnswerText.ToString(), "Reply copied", "Reply is empty");
        root.Children.Add(header);
        root.Children.Add(reply.Content);
        root.Children.Add(reply.Status);
        root.Children.Add(reply.Copy);
        ChatAdd(root);
        return reply;
    }

    // After the process ended (its output already applied): close the open section, show errors, remember the exchange.
    // Returns true when the turn counts as stopped: a clean exit with the final reply wins over a Stop that came too late.
    private bool FinishChatTurn(int exitCode, bool stopped, string? failure)
    {
        var c = _turn;
        var completed = false;
        if (_reply is { } reply)
        {
            EndChatBlock();
            reply.Status.Visibility = Visibility.Collapsed;
            var answer = reply.Final ?? (reply.AnswerText.Length > 0 ? reply.AnswerText.ToString() : null);
            stopped &= !(failure is null && exitCode == 0 && reply.Final is not null);
            if (stopped)
                AddChatNote(reply, "Stopped.");
            else if (failure is not null || exitCode != 0)
                AddChatNote(reply, failure ?? _chatError ?? $"The chat failed (exit code {exitCode}).", error: true);
            else if (answer is null)
                AddChatNote(reply, "The worker returned no reply.", error: true);
            else
            {
                if (reply.Final is null)
                    SetChatFinal(answer);
                if (_pendingUser is { } user) // a turn without a user message stays out of the history: roles must alternate
                {
                    c.History.Add(new LauncherLogic.ChatMessage("user", user));
                    c.History.Add(new LauncherLogic.ChatMessage("assistant", answer));
                }
                completed = true;
            }
            reply.Copy.Visibility = answer is null ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var card in _cards)
        {
            card.ClearLive();
            card.IsActive = false;
            card.Status = "Idle";
        }
        _activeRole = null;
        var loadedThisTurn = _turnLoaded.ToList();
        _turnLoaded = new();
        if (!_settings.ChatKeepLoaded)
            UnloadChat(loadedThisTurn);
        SetChatBusy(false);
        if (c.Input.IsVisible)
            c.Input.Focus();
        _turnCompleted = completed;
        if (c.IsCode && completed)
            CodeTurnFinished();
        else if (completed)
            SaveChatConversation();
        return stopped;
    }

    private void SetChatBusy(bool busy)
    {
        _chatBusy = busy;
        AddWorkerButton.IsEnabled = PresetsButton.IsEnabled = !busy;
        foreach (var card in _cards)
            card.Editable = !busy;
        SetStatus(busy ? "Replying" : "Ready", busy ? "#8B7CFF" : "#667085");
        SetActivity(busy);
        RefreshChatInput();
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
        var card = CardFor(role);
        var name = ChatName(card, role);
        switch (Str(ev, "e"))
        {
            case "loading":
                EndChatBlock();
                Activate(role, "Loading model");
                if (Str(ev, "path").Length > 0)
                {
                    _loadedByChat.Add(LoadedOn(ev));
                    _turnLoaded.Add(LoadedOn(ev));
                }
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
                break;
            case "chat_stage":
                EndChatBlock();
                if (_reply is { } r)
                {
                    r.Stage = Str(ev, "stage");
                    r.StageTitle = Str(ev, "title");
                }
                SetChatStatus($"{(Str(ev, "title") is { Length: > 0 } t ? t : name)}…");
                break;
            case "chat_reply" or "code_reply":
                SetChatFinal(Str(ev, "text"));
                break;
            case "tool":
                EndChatBlock();
                Activate(role, "Using tools");
                SetChatStatus($"{name} is using {Str(ev, "name")}…");
                AddCodeTool(Str(ev, "name"), ev.TryGetProperty("args", out var args) && args.ValueKind != JsonValueKind.String ? args.GetRawText() : Str(ev, "args"), Str(ev, "result"));
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

    private void SetChatStatus(string text)
    {
        if (_reply is not { } r)
            return;
        r.Status.Text = text;
        r.Status.Visibility = Visibility.Visible;
    }

    private void AppendChatDelta(string role, string kind, string text)
    {
        Activate(role, kind == "thinking" ? "Thinking" : "Working");
        if (_reply is not { } r || kind == "tool")
            return; // a chat has no tool calls
        SetChatStatus(kind == "thinking" ? "Thinking…" : "Writing…");
        if (r.Block is null || r.Block.Kind != kind)
        {
            EndChatBlock();
            r.Block = StartChatBlock(r, role, kind);
        }
        var block = r.Block;
        block.Text.Append(text);
        block.Body.AppendText(text);
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
            return block;
        }
        block.Body = BodyText("", mono: false, muted: kind == "thinking", conv: _turn);
        block.View = MarkdownView(block.Body);
        var content = new StackPanel();
        content.Children.Add(block.Body);
        content.Children.Add(block.View);
        var stage = r.StageTitle.Length > 0 ? r.StageTitle : r.Stage;
        var title = kind == "thinking" ? (r.Multi && stage.Length > 0 ? $"THINKING  ·  {stage.ToUpperInvariant()}" : "THINKING") : stage.ToUpperInvariant();
        block.Expander = BuildExpander(title, "", content, out block.Preview);
        block.Label = (TextBlock)((StackPanel)block.Expander.Header).Children[0];
        block.Expander.Expanded += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        block.Expander.Collapsed += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        r.Content.Children.Add(block.Expander);
        FadeIn(block.Expander);
        SetPulse(block, true);
        return block;
    }

    private TextBox EnsureAnswer(ChatReply r)
    {
        if (r.Answer is not null)
            return r.Answer;
        if (r.Multi)
        {
            var label = Label("FINAL ANSWER", accent: true);
            label.Margin = new Thickness(2, 10, 0, 4);
            r.Content.Children.Add(label);
        }
        var box = r.Answer = BodyText("", mono: false, muted: false, conv: _turn);
        box.FontSize = 14;
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
        EnsureAnswer(r).Text = text;
        r.AnswerText.Clear().Append(text);
        ShowMarkdown(r);
        if (!_turn.Follow)
            Reveal(_turn.Jump, true, slide: 10);
    }

    // Streamed text (the answer and the thinking / stage sections) shows as Markdown with coloured code: the TextBox keeps the
    // raw text, hidden, and a read-only RichTextBox shows it rendered, at most every 200 ms while it streams.
    private readonly HashSet<RichTextBox> _markdownDirty = new();
    private DispatcherTimer? _markdownTimer;

    private RichTextBox MarkdownView(TextBox raw)
    {
        var view = new RichTextBox
        {
            IsReadOnly = true, IsDocumentEnabled = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Margin = raw.Margin, FontFamily = FontFamily, Cursor = Cursors.IBeam, Tag = raw,
        };
        view.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentLight");
        ForwardWheel(view, _turn);
        raw.Visibility = Visibility.Collapsed;
        Render(view);
        return view;
    }

    private static void Render(RichTextBox view)
    {
        var raw = (TextBox)view.Tag;
        view.Document = Markdown.ToDocument(raw.Text, raw.FontSize, muted: raw.FontStyle == FontStyles.Italic); // BodyText's muted text is italic
    }

    private void RenderSoon(RichTextBox view)
    {
        _markdownDirty.Add(view);
        _markdownTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var dirty in _markdownDirty)
                Render(dirty);
            _markdownDirty.Clear();
            _markdownTimer!.Stop();
        }, Dispatcher);
        _markdownTimer.Start();
    }

    // The final reply replaces whatever streamed; render it now.
    private void ShowMarkdown(ChatReply r)
    {
        EnsureAnswer(r);
        _markdownDirty.Remove(r.Rendered!);
        Render(r.Rendered!);
    }

    private void EndChatBlock()
    {
        if (_reply?.Block is not { } block)
            return;
        _reply.Block = null;
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

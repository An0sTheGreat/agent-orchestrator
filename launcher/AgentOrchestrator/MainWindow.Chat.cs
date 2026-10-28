using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrchestrator;

// Chat view: a conversation with one worker (Single) or with Planner -> Implementer -> Reviewer (Multi).
// Every message starts one `orchestrator.py --chat` process; its events stream into the reply under construction.
public partial class MainWindow
{
    private Process? _chatProcess;
    private bool _chatBusy, _chatFollow = true, _chatStopRequested;
    private string? _chatError;
    private string? _chatRequestFile, _chatTempFolder; // the current turn's temp files; _chatTempFolder only when PrepareChatFolder made it
    private readonly HashSet<(string Url, string Path)> _loadedByChat = new(); // unloaded when the app closes, or after each turn when ChatKeepLoaded is off
    private HashSet<(string Url, string Path)> _turnLoaded = new(); // what the current turn's loading events added; a fresh set per turn so a late unload never touches the next turn
    private Task _chatUnload = Task.CompletedTask;
    private int _unloadsPending; // end-of-turn unloads in flight: Send waits so they cannot unload what the next turn loads
    private bool _unloading => _unloadsPending > 0;
    private readonly List<LauncherLogic.ChatMessage> _chatHistory = new();
    private ChatReply? _reply;

    // One assistant reply: sections (thinking, Plan / Draft / Review) above the prominent answer.
    private sealed class ChatReply
    {
        public StackPanel Content = null!;
        public TextBlock Status = null!;
        public Button Copy = null!;
        public bool Multi;
        public string Stage = "answer", StageTitle = "";
        public StreamBlock? Block;
        public TextBox? Answer;
        public readonly StringBuilder AnswerText = new();
        public string? Final;
    }

    private void InitChat()
    {
        if (_settings.ChatMode != "multi")
            _settings.ChatMode = "single";
        if (CardFor(_settings.ChatWorker) is null)
            _settings.ChatWorker = "implementer";
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
    }

    // Send / Stop, the lock while a reply streams, and why sending is unavailable.
    private void RefreshChatInput()
    {
        var runActive = _process is not null;
        ChatInput.IsEnabled = !_chatBusy;
        ChatPlus.IsEnabled = ChatTargetChip.IsEnabled = ChatSingleToggle.IsEnabled = ChatMultiToggle.IsEnabled = NewChatButton.IsEnabled = ChatKeepToggle.IsEnabled = !_chatBusy;
        ChatSend.Content = _chatBusy ? "Stop" : "Send";
        ChatSend.IsEnabled = _chatBusy || (!runActive && !_unloading && _workflowRoot is not null && ChatInput.Text.Trim().Length > 0);
        ChatHint.Text = runActive ? "A project run is using the models; chat is available when it finishes."
            : _chatBusy ? "Replying…" : _unloading ? "Unloading models…" : "Enter to send  ·  Shift+Enter for a new line";
    }

    // ---- worker menu (+ button and target chip)

    private ContextMenu BuildWorkerMenu(FrameworkElement target)
    {
        RefreshSetupProblems();
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        foreach (var card in _cards)
        {
            var name = ChatName(card, card.Role);
            var broken = card.IsCustom ? card.SetupProblem : null;
            var item = new MenuItem
            {
                Header = broken is null ? name : $"{name}  —  {broken}",
                IsChecked = !ChatMulti && card.Role == ChatTarget()?.Role,
                IsEnabled = broken is null,
                ToolTip = broken is null ? card.ModelName : card.SetupTip,
                Tag = card.Role,
            };
            item.Click += (_, _) => PickChatWorker(card.Role);
            menu.Items.Add(item);
        }
        return menu;
    }

    // Picking a worker makes it the Single-mode target (and switches to Single, so the choice is visible).
    private void PickChatWorker(string role)
    {
        _settings.ChatWorker = role;
        _settings.ChatMode = "single";
        _settings.Save();
        RefreshChatTarget();
        ChatInput.Focus();
    }

    private void ChatPlus_Click(object sender, RoutedEventArgs e) => BuildWorkerMenu(ChatPlus).IsOpen = true;

    private void ChatTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!ChatMulti)
            BuildWorkerMenu(ChatTargetChip).IsOpen = true;
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
        if (ChatSend.IsEnabled && !_chatBusy)
            SendChat();
    }

    private void ChatSend_Click(object sender, RoutedEventArgs e)
    {
        if (!_chatBusy)
            SendChat();
        else if (_chatProcess is { } p)
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
        ChatPanel.Children.Clear();
        _chatHistory.Clear();
        _reply = null;
        ChatEmpty.Visibility = Visibility.Visible;
        ChatJump_Click(ChatJumpPill, e);
        ChatInput.Focus();
    }

    // ---- sending

    private async void SendChat()
    {
        if (_chatBusy || _process is not null || _workflowRoot is null)
            return;
        var text = ChatInput.Text.Trim();
        if (text.Length == 0)
            return;
        if (ApiKeyBox.Password.Length == 0)
            TryLoadApiKeyFile();
        _runKey = ApiKeyBox.Password.Trim();
        if (_runKey.Length == 0)
        {
            SetStatus("API key required", "#FF667A");
            Log("Enter your Unsloth API key.");
            ShowView(options: true);
            ApiKeyBox.Focus();
            return;
        }
        var skipped = new List<string>();
        if (!SyncCustomWorkers(skipped))
        {
            Toast("Message not sent: config.json could not be updated", "#FF667A");
            return;
        }
        var target = ChatTarget()!;
        if (!ChatMulti && target.IsCustom && target.SetupProblem is { } problem)
        {
            Toast($"{ChatName(target, target.Role)} cannot be used: {problem}", "#F7C873");
            return;
        }
        foreach (var reason in skipped)
            Log($"Not written to config.json, so it will not take part: {reason}");

        var requestFile = _chatRequestFile = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-chat-{Guid.NewGuid():N}.json");
        var exitCode = -1;
        string? failure = null;
        StartChatTurn(text, ChatMulti);
        ChatInput.Clear();
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
            var request = LauncherLogic.BuildChatRequest(_settings.ChatMode, target.Role,
                _chatHistory.Append(new LauncherLogic.ChatMessage("user", text)));
            File.WriteAllText(requestFile, request);
            var startInfo = LauncherLogic.BuildChatStartInfo(LauncherLogic.FindPython(), _workflowRoot, folder, _runKey, requestFile);
            var process = _chatProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };
            _apiBase = ManagementUrl(folder) ?? _apiBase;
            _runStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            Log($"Chat ({_settings.ChatMode}) in {folder}");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
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
            DrainLines();
            _chatProcess?.Dispose();
            _chatProcess = null;
            CleanupChatTemp();
        }
        if (FinishChatTurn(exitCode, _chatStopRequested, failure))
            Toast("Chat stopped", "#F7C873");
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

    // The user's message and an empty reply appear, and the GPU-sharing lock goes on.
    private string _pendingUser = "";

    private void StartChatTurn(string text, bool multi)
    {
        _pendingUser = text;
        _turnLoaded = new();
        var box = BodyText(text, mono: false, muted: false, chat: true);
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

        var target = ChatTarget();
        var root = new StackPanel { Margin = new Thickness(0, 8, 0, 14) };
        var header = new TextBlock { Text = multi ? ChainLabel : ChatName(target, target?.Role ?? ""), Margin = new Thickness(2, 0, 0, 6) };
        header.SetResourceReference(StyleProperty, "SectionHeader");
        header.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
        var reply = _reply = new ChatReply { Multi = multi, Content = new StackPanel() };
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

        _chatError = null;
        _chatStopRequested = false;
        _activeRole = null;
        SetChatBusy(true);
    }

    // After the process ended (its output already applied): close the open section, show errors, remember the exchange.
    // Returns true when the turn counts as stopped: a clean exit with the final reply wins over a Stop that came too late.
    private bool FinishChatTurn(int exitCode, bool stopped, string? failure)
    {
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
                _chatHistory.Add(new LauncherLogic.ChatMessage("user", _pendingUser));
                _chatHistory.Add(new LauncherLogic.ChatMessage("assistant", answer));
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
        if (ChatView.IsVisible)
            ChatInput.Focus();
        return stopped;
    }

    private void SetChatBusy(bool busy)
    {
        _chatBusy = busy;
        StartButton.IsEnabled = !busy && _workflowRoot is not null;
        ProjectButton.IsEnabled = AddWorkerButton.IsEnabled = PresetsButton.IsEnabled = !busy;
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
            case "chat_reply":
                SetChatFinal(Str(ev, "text"));
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
        if (block.Expander is null)
            r.AnswerText.Append(text);
        else if (block.Preview is not null && block.Text.Length <= 300)
            block.Preview.Text = PreviewFor(block);
        if (!_chatFollow)
            Reveal(ChatJumpPill, true, slide: 10);
    }

    private StreamBlock StartChatBlock(ChatReply r, string role, string kind)
    {
        var block = new StreamBlock { Role = role, Kind = kind };
        if (kind == "text" && r.Stage == "answer")
        {
            block.Body = EnsureAnswer(r); // the answer streams straight into the prominent text, not a section
            return block;
        }
        block.Body = BodyText("", mono: false, muted: kind == "thinking", chat: true);
        var stage = r.StageTitle.Length > 0 ? r.StageTitle : r.Stage;
        var title = kind == "thinking" ? (r.Multi && stage.Length > 0 ? $"THINKING  ·  {stage.ToUpperInvariant()}" : "THINKING") : stage.ToUpperInvariant();
        block.Expander = BuildExpander(title, "", block.Body, out block.Preview);
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
        var box = r.Answer = BodyText("", mono: false, muted: false, chat: true);
        box.FontSize = 14;
        box.Margin = new Thickness(2, 4, 0, 0);
        r.Content.Children.Add(box);
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
        if (!_chatFollow)
            Reveal(ChatJumpPill, true, slide: 10);
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

    private void ChatAdd(UIElement element)
    {
        ChatEmpty.Visibility = Visibility.Collapsed;
        ChatPanel.Children.Add(element);
        if (!_chatFollow)
            Reveal(ChatJumpPill, true, slide: 10);
        FadeIn(element);
    }

    // Same follow-the-tail rule as Model Output: stay at the end unless the user scrolled up.
    private void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            _chatFollow = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 8;
            if (_chatFollow)
                Reveal(ChatJumpPill, false, slide: 10);
        }
        else if (_chatFollow && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0))
            ChatScroll.ScrollToEnd();
    }

    private void ChatJump_Click(object sender, RoutedEventArgs e)
    {
        _chatFollow = true;
        ChatScroll.ScrollToEnd();
        Reveal(ChatJumpPill, false, slide: 10);
    }
}

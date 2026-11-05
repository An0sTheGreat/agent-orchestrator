using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AgentOrchestrator;

public partial class MainWindow : Window
{
    // COMPACTOR (the compactor role) is always shown and summarizes on MAIN's model until it has its own (FollowsMain).
    private static readonly string[] Roles = ["planner", "implementer", "reviewer", "compactor"];

    private Process? _process;
    private string? _workflowRoot; // not readonly: --ui-smoke (RemoteSmoke) points it at a stand-in engine

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<RoleCard> _cards = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
    private readonly DispatcherTimer _pump = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Queue<StreamBlock> _pendingTools = new();
    private StreamBlock? _stream;
    private bool _stopRequested;
    private string? _runError; // the run's last ERROR line, shown in the conversation when it ends
    // The project run's output: a panel appended to the Code conversation (AddBlock) after the messages shown when it started.
    private StackPanel OutputPanel = new();
    private readonly StringBuilder _log = new(), _transcript = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _activeRole, _blockRole;
    // TASK TIME / TURN TIME in the Code header; _clock ticks only while one of them runs.
    private readonly Stopwatch _taskWatch = new(), _turnWatch = new();
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) }; // Background starves while output streams
    private bool _initializing = true, _polling;

    public MainWindow()
    {
        InitializeComponent();
        if (AppSettings.Unified) // one conversation view (a chat until it works in a folder), no Chat/Code switch
        {
            NavSwitch.Visibility = Visibility.Collapsed;
        }
        LoadWorkers();
        _cards.CollectionChanged += (_, _) => RefreshCardVisibility(); // a preset rebuilds the cards
        InitWorkerTabs();
        _pump.Tick += (_, _) => DrainLines();
        _clock.Tick += (_, _) => ShowClocks();
        SizeChanged += (_, _) => FitDetailsColumn();
        Loaded += (_, _) => FitDetailsColumn();
        _workflowRoot = LauncherLogic.FindWorkflowRoot();

        InitWorkerRail();
        SetSidebar(_settings.SidebarCollapsed, animate: false);
        SidePane.SizeChanged += (_, _) => FitDetailsColumn();
        _settings.Theme = _settings.Theme is "Glass" or "Gold" or "Oled" or "Graphite" or "Flat" or "Parchment" or "ParchmentDark" ? _settings.Theme : "Graphite";
        ThemeFlatRadio.IsChecked = _settings.Theme == "Flat";
        ThemeGoldRadio.IsChecked = _settings.Theme == "Gold";
        ThemeGlassRadio.IsChecked = _settings.Theme == "Glass";
        ThemeOledRadio.IsChecked = _settings.Theme == "Oled";
        ThemeGraphiteRadio.IsChecked = _settings.Theme == "Graphite";
        ThemeParchmentRadio.IsChecked = _settings.Theme == "Parchment";
        ThemeParchmentDarkRadio.IsChecked = _settings.Theme == "ParchmentDark";
        ApplyTheme();
        InitRuntime();
        _initializing = false;
        InitChat();
        StoreDo(_store.Load);
        RestoreWindowBounds();
        InitRemote();

        Closing += OnClosing;
        Activated += (_, _) => RefreshCodeFolder(); // PROJECT.md may have been added or removed in Explorer meanwhile
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(LimitMaximizeToWorkArea);
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        _poll.Tick += Poll_Tick;

        if (_workflowRoot is null)
        {
            SetStatus("Workflow files not found", "#FF667A");
            CodeStartButton.IsEnabled = false;
            Log("Place this EXE in the Agent Setup folder or its dist subfolder.");
        }
        else
            HoldWorkerTab(() => // one build of the worker tab for all of this
            {
                ProjectPath = _settings.LastProject.Length > 0 ? _settings.LastProject : _workflowRoot;
                Log($"Workflow: {_workflowRoot}");
                if (_settings.LoadFailed)
                    Log("settings.json could not be read; settings changed this session will not be saved.");
                MigrateRoles();
                PrefillCards();
                RefreshPrimary();
                if (_store.Get(_settings.LastConversation) is not null)
                    OpenCodeConversation(_settings.LastConversation);
            });
        ShowView(chat: true); // the app opens in Chat; the last Code conversation waits in the Code view
        RefreshProfile();
        Markdown.OpenLink = OpenLink;
        if (_settings.BrowserOpen)
            OpenBrowser(null);
    }

    // ---------------------------------------------------------------- run control

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_workflowRoot is null || _chatBusy || !SaveWorkerEditsFirst()) // a chat reply is using the models
            return;

        var project = SelectedProjectPath();
        if (project.Length == 0 || !Directory.Exists(project) || LauncherLogic.MissingProjectFiles(project).Length != 0)
        {
            SetStatus("Use Start project in Code first", "#F7C873"); // it writes the workflow files beside PROJECT.md
            return;
        }

        if (!RuntimeReady())
            return;
        if (LauncherLogic.IsProjectComplete(project) && !RestartProject(project))
            return;
        if (!PrepareRun())
            return;
        var requestFile = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-run-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(requestFile, RunRequest(project));
            var python = LauncherLogic.FindPython();
            var startInfo = LauncherLogic.BuildStartInfo(python, _workflowRoot, project, _settings.Once, requestFile);
            ApplyRuntime(startInfo);
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };
            _process.ErrorDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };

            _settings.LastProject = project;
            _settings.Save();
            Log($"Starting workflow for {project}");
            SetRunning(true);
            _process.Start();
            RunClocks.Visibility = Visibility.Visible;
            AddNote("PROJECT RUN", $"Working through PROJECT.md in {project}" + (_settings.Once ? ", stopping after the first finished item" : ""));
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _poll.Start(); // live speed from llama-server's /slots
            _pump.Start();
            await _process.WaitForExitAsync();
            var exitCode = _process.ExitCode;
            _pump.Stop();
            DrainLines();
            EndStream(false);
            Log($"Process exited with code {exitCode}.");
            SetStatus(exitCode == 0 ? "Completed" : "Stopped with an error", exitCode == 0 ? "#5BD6A2" : "#FF667A");
            var outcome = exitCode == 0 ? "Run finished" : _stopRequested ? "Run stopped" : $"Run failed (exit code {exitCode})";
            AddNote(outcome.ToUpperInvariant(), _runError ?? (exitCode == 0 ? "The run ended." : "Open run logs (Project menu) has the details."));
            Toast(outcome, exitCode == 0 ? null : _stopRequested ? "#F7C873" : "#FF667A");
            NotifyDone($"Project run: {outcome}", _runError ?? Path.GetFileName(project.TrimEnd('\\', '/')));
        }
        catch (Exception exception)
        {
            Log($"ERROR: {exception.Message}");
            AddNote("RUN COULD NOT START", exception.Message);
            SetStatus("Could not start", "#FF667A");
            Toast("Run could not start", "#FF667A");
        }
        finally
        {
            try { File.Delete(requestFile); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
            _poll.Stop();
            _pump.Stop();
            DrainLines();
            EndStream(false);
            _process?.Dispose();
            _process = null;
            _callLive.Clear();
            foreach (var card in _cards)
            {
                card.ClearLive();
                card.IsActive = false;
                card.Status = "Idle";
            }
            _activeRole = null;
            StopClocks(); // a stopped or failed run freezes both; TASK TOKENS only change on task_end
            SetRunning(false, preserveStatus: true);
        }
    }

    // The run's request: a Code turn's tools (the conversation's extra folders, search, utilities, skills, sandbox and
    // effort) and the workflow mode ("workflow": "dual" or "tri", Settings > Agents).
    private string RunRequest(string project)
    {
        var request = JsonNode.Parse(LauncherLogic.BuildCodeRequest("chat", "planner", [], project, CodeExtraFolders, CurrentExtras(),
            effort: EffortFor(CardFor("planner")), session: _codeConv?.Id ?? "", sandboxNetwork: _settings.SandboxNetwork, permissions: _settings.CommandPermissions))!;
        request["workflow"] = _settings.WorkflowMode == "tri" ? "tri" : "dual";
        return request.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var p = _process;
        if (p is null || p.HasExited)
            return;
        try
        {
            var activity = StopActivity();
            p.Kill(entireProcessTree: true);
            _stopRequested = true;
            Log("Stop requested.");
            SetStatus("Stopping…", "#F7C873");
            // Kill returns before the process is gone; let it finish so it cannot overwrite the note.
            try { await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(2000)); }
            catch (InvalidOperationException) { } // already disposed by the run's cleanup
            RecordStopNote(activity);
        }
        catch (InvalidOperationException) { }
    }

    // What the run was doing when stopped: the active worker and its status, else the overall status text.
    private string StopActivity() =>
        _activeRole is { } role && CardFor(role) is { } card ? $"{card.Title} — {card.Status}" : StatusText.Text;

    private void RecordStopNote(string activity)
    {
        try
        {
            Log(LauncherLogic.RecordStop(SelectedProjectPath(), DateTime.Now, activity));
            Toast("Handoff saved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Could not save the handoff note: {exception.Message}");
        }
    }

    // Before a run: write the custom workers to config.json, clear the view, then say which workers will not take
    // part (after the clear, so the lines stay). False (nothing started) when config.json could not be written,
    // since the run would otherwise use whatever custom_workers is on disk.
    private bool PrepareRun()
    {
        var skipped = new List<string>();
        if (!SyncCustomWorkers(skipped))
        {
            Toast("Run not started: config.json could not be updated", "#FF667A");
            return false;
        }
        ResetRunView();
        foreach (var reason in skipped)
            Log($"Not written to config.json, so it will not take part: {reason}");
        if (skipped.Count > 0)
            Toast($"Not taking part: {string.Join("; ", skipped)}", "#FFB74D");
        return true;
    }

    private void ResetRunView()
    {
        OutputPanel = new StackPanel(); // the last run's output stays in the conversation above
        if (_stream is { } live)
            SetPulse(live, false);
        _stream = null;
        _pendingTools.Clear();
        _lines.Clear();
        _stopRequested = false;
        _runError = null;
        Jump(_code);
        _transcript.Clear();
        _log.Clear();
        _activeRole = _blockRole = null;
        foreach (var card in _cards)
        {
            card.BeginTurn();
            card.IsActive = false;
            card.Status = "Idle";
        }
    }

    private void SetRunning(bool running, bool preserveStatus = false)
    {
        AddWorkerButton.IsEnabled = !running;
        PresetsButton.IsEnabled = !running;
        foreach (var card in _cards)
            card.Editable = !running;
        if (running)
            SetStatus("Running", "#8B7CFF");
        else if (!preserveStatus)
            SetStatus("Ready", "#667085");
        SetActivity(running);
        TimeTask(running);
        RefreshPrimary();
        RefreshChatInput();
    }

    // While a run is in progress the status dot sends out a ring and a light sweeps along the header's lower edge.
    // Idle removes both animations, so nothing keeps ticking between runs.
    private void SetActivity(bool on)
    {
        static DoubleAnimation Loop(double from, double to, double seconds, IEasingFunction ease) =>
            new(from, to, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        StatusRing.BeginAnimation(OpacityProperty, on ? Loop(0.6, 0, 1.6, Decelerate) : null);
        StatusRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, on ? Loop(1, 2.2, 1.6, Decelerate) : null);
        StatusRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, on ? Loop(1, 2.2, 1.6, Decelerate) : null);
        ShimmerShift.BeginAnimation(TranslateTransform.XProperty, on ? Loop(-34, 100, 1.8, new SineEase { EasingMode = EasingMode.EaseInOut }) : null);
        Shimmer.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(250)));
    }

    // Code's start button: "Start project" until the folder has the workflow files, then "Start Task" for a project with
    // no tasks yet, otherwise "Resume Project — <active or last completed task>" (or "Restart Project").
    private void RefreshPrimary()
    {
        if (_workflowRoot is null)
            return;
        var project = SelectedProjectPath();
        var ready = project.Length > 0 && Directory.Exists(project) && LauncherLogic.MissingProjectFiles(project).Length == 0;
        CodeStartButton.Content = ready ? LauncherLogic.PrimaryLabel(project) : "Start project";
        FooterLeft.Text = project;
        FooterRight.Text = ready && LauncherLogic.CurrentTaskId(project) is { } id ? $"TASK {id}" : "NO TASK YET";
    }

    // ---------------------------------------------------------------- output stream

    private static string Str(JsonElement ev, string name) =>
        ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement ev, string name) =>
        ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    // Output arrives on thread-pool threads; a fast timer applies it so streamed text lands a few tokens at a time.
    private void DrainLines()
    {
        var pending = new StringBuilder();
        (string Role, string Kind, int Index)? key = null;
        var name = "";
        var chunks = 0; // deltas before coalescing: one per streamed token

        void Flush()
        {
            if (key is { } k && pending.Length > 0)
            {
                if (_chatBusy)
                    AppendChatDelta(k.Role, k.Kind, pending.ToString(), name);
                else
                    AppendDelta(k.Role, k.Kind, k.Index, name, pending.ToString());
                CardForActivity(k.Role)?.AddDeltas(chunks, Environment.TickCount64);
                CountLive(k.Role, chunks, _chatBusy ? _reply?.Block : _stream);
            }
            chunks = 0;
            pending.Clear();
            key = null;
        }

        while (_lines.TryDequeue(out var line))
        {
            var ev = LauncherLogic.TryParseEvent(line);
            if (ev is { } e && Str(e, "e") == "delta")
            {
                var next = (Str(e, "role"), Str(e, "kind"), (int)Num(e, "index"));
                if (key is { } current && current != next)
                    Flush();
                key = next;
                chunks++;
                name = Str(e, "name");
                pending.Append(Str(e, "text"));
                continue;
            }
            Flush();
            try
            {
                if (ev is { } other)
                    HandleEvent(other);
                else
                {
                    if (_chatBusy)
                        NoteChatLine(line);
                    else if (line.TrimStart().StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                        _runError = line.Trim();
                    Log(line);
                }
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                Log($"Unreadable event ({exception.Message}): {line}");
            }
        }
        Flush();
    }

    private void HandleEvent(JsonElement ev)
    {
        if (_chatBusy) // a chat reply streams into the conversation, not into Model Output
        {
            HandleChatEvent(ev);
            return;
        }
        var role = Str(ev, "role");
        var card = CardForActivity(role);
        switch (Str(ev, "e"))
        {
            case "loading":
                EndStream(false);
                Activate(role, "Loading model");
                WorkerContextsCleared();
                Log($"[{card?.Title ?? role}] loading {Str(ev, "path")} ...");
                break;
            case "model":
                EndStream(false);
                Activate(role, "Working");
                card?.SetModel(ModelInfoFor(role, Str(ev, "id")));
                Log($"[{card?.Title ?? role}] {Str(ev, "id")}");
                break;
            case "done":
                EndStream(ev.TryGetProperty("final", out var f) && f.ValueKind == JsonValueKind.True);
                break;
            case "thinking":
                Activate(role, "Thinking");
                AddThinking(role, Str(ev, "text"));
                break;
            case "message":
                Activate(role, "Working");
                AddMessage(role, Str(ev, "text"), ev.TryGetProperty("final", out var g) && g.ValueKind == JsonValueKind.True);
                break;
            case "tool":
                Activate(role, "Using tools");
                BrowserFollowTool(Str(ev, "name"), Str(ev, "args"), Str(ev, "result"));
                if (_pendingTools.Count > 0)
                    CompleteTool(_pendingTools.Dequeue(), Str(ev, "name"), Str(ev, "args"), Str(ev, "result"));
                else
                    AddTool(role, Str(ev, "name"), Str(ev, "args"), Str(ev, "result"));
                break;
            case "usage":
                Activate(role, "Working");
                card?.AddUsage((long)Num(ev, "prompt"), (long)Num(ev, "completion"), Num(ev, "tps"));
                RecordUsage((long)(Num(ev, "prompt") + Num(ev, "completion")));
                _callLive.Remove(role);
                foreach (var ended in _runClosed.Where(b => b.Role == role).ToList()) // the call that just ended: its thinking and response
                {
                    _runClosed.Remove(ended);
                    AddStatsLine(ended, LauncherLogic.StatsLine(ChatName(card, role), Num(ev, "tps"), (long)Num(ev, "completion"), (long)Num(ev, "prompt")), Num(ev, "tps"));
                }
                break;
            case "compacted": // its print line is already in the log
                EndStream(false);
                AddNote("CONTEXT", $"{card?.Title ?? role}: {CompactedLine(ev)}");
                AddBlock(CompactionRule(), "\n----\n");
                if (_codeContexts.GetValueOrDefault(role) is { } full)
                    SetCodeContext(full.Compacted((long)Num(ev, "after")));
                break;
            case "context": // the run's calls fill the same per-worker cards as Code turns
                SetCodeContext(ContextSnapshot.From(ev));
                break;
            case "task":
                EndStream(false);
                Log($"Task {Str(ev, "id")}: {Str(ev, "outcome")}");
                AddNote($"TASK  {Str(ev, "id")}", Str(ev, "outcome"));
                RefreshPrimary();
                break;
            case "task_start": // planning (or a resume) begins: TASK TIME restarts, workers count this task from zero
                StartClock(_taskWatch);
                foreach (var c in _cards)
                    c.BeginTask();
                break;
            case "task_end": // passed or blocked: every card shows its share of the task (0 if it took no part)
                StopClock(_taskWatch);
                foreach (var c in _cards)
                    c.EndTask();
                break;
            case "complete":
                EndStream(false);
                Log($"Project complete: {Str(ev, "summary")}");
                AddNote("PROJECT COMPLETE", Str(ev, "summary"));
                Toast("Project complete");
                RefreshPrimary();
                break;
            case "resume":
                EndStream(false);
                Log($"Resuming task {Str(ev, "id")} at review round {Num(ev, "round") + 1}");
                AddNote($"RESUMED  {Str(ev, "id")}", $"Continuing at review round {Num(ev, "round") + 1}");
                RefreshPrimary();
                break;
            case "approval": // the sandboxed run_command asks for network or more folders: the user decides
                AnswerApproval(ev);
                break;
            case "browser": // the model's browser tool: the in-app browser opens on the page
                Forget(AnswerBrowserAsync(ev));
                break;
            case "verdict":
                EndStream(false);
                Log($"Review round {Num(ev, "round")}: {Str(ev, "verdict")}");
                AddNote($"REVIEW  {Str(ev, "verdict")}", $"Task {Str(ev, "id")}, round {Num(ev, "round")}");
                Toast($"Review {Str(ev, "verdict")}: task {Str(ev, "id")}, round {Num(ev, "round")}", Str(ev, "verdict") == "PASS" ? null : "#FF667A");
                RefreshPrimary();
                break;
        }
    }

    // A turn is a stretch of one role working; a different role taking over starts that role's turn fresh.
    private void Activate(string role, string status)
    {
        if (CardForActivity(role) is not { } card)
            return;
        role = card.Role; // a delegation to MAIN continues MAIN's turn
        if (_activeRole != role)
        {
            foreach (var other in _cards.Where(c => c.IsActive))
            {
                other.ClearLive();
                other.IsActive = false;
                other.Status = "Idle";
            }
            card.BeginTurn();
            _activeRole = role;
            if (!_chatBusy) // the header clocks follow workflow runs only
                StartClock(_turnWatch);
        }
        card.IsActive = true;
        card.Status = status;
    }

    private void StartClock(Stopwatch watch)
    {
        watch.Restart();
        _clock.Start();
        ShowClocks();
    }

    private void StopClock(Stopwatch watch)
    {
        watch.Stop();
        ShowClocks();
    }

    private void StopClocks()
    {
        _taskWatch.Stop();
        _turnWatch.Stop();
        ShowClocks();
    }

    private void ShowClocks()
    {
        TaskTimeText.Text = LauncherLogic.FormatElapsed(_taskWatch);
        TurnTimeText.Text = LauncherLogic.FormatElapsed(_turnWatch);
        if (!_taskWatch.IsRunning && !_turnWatch.IsRunning)
            _clock.Stop();
    }

    private void AddRoleHeaderIfNeeded(string role)
    {
        if (_blockRole == role)
            return;
        _blockRole = role;
        var card = CardFor(role);
        var name = card?.ModelName ?? "";
        var title = card?.Title ?? role.ToUpperInvariant();
        var header = new TextBlock { Text = $"{title}  ·  {name}", Margin = new Thickness(2, 14, 0, 8) };
        header.SetResourceReference(StyleProperty, "SectionHeader");
        header.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
        AddBlock(header, $"\n=== {title} · {name} ===\n");
    }

    // ---- live blocks: thinking / tool / response grow as deltas arrive

    private sealed class StreamBlock
    {
        public string Role = "", Kind = "", Name = "";
        public int Index;
        public Expander? Expander;
        public TextBlock? Preview, Label;
        public TextBlock? Live; // the call's running stats under the block until its usage event
        public TextBox Body = null!, Result = null!;
        public RichTextBox? View; // Chat and Code: Body rendered as Markdown (Body itself is hidden)
        public TextBlock ResultLabel = null!;
        public readonly StringBuilder Text = new();
        public bool Touched, Programmatic;
    }

    private void AppendDelta(string role, string kind, int index, string name, string text)
    {
        Activate(role, kind == "thinking" ? "Thinking" : kind == "tool" ? "Using tools" : "Working");
        if (_stream is null || _stream.Role != role || _stream.Kind != kind || (kind == "tool" && _stream.Index != index))
        {
            EndStream(false);
            _stream = StartStream(role, kind, index);
        }
        if (kind == "tool" && name.Length > 0)
            _stream.Name = name;
        _stream.Text.Append(text);
        _stream.Body.AppendText(text);
        if (!_code.Follow)
            Reveal(CodeJumpPill, true, slide: 10); // returns at once while the pill is already showing
        _transcript.Append(text);
        if (_stream.Preview is not null && _stream.Text.Length <= 300)
            _stream.Preview.Text = PreviewFor(_stream);
    }

    private static string PreviewFor(StreamBlock b) =>
        b.Kind == "tool" ? $"{b.Name}  {Preview(b.Text.ToString())}" : Preview(b.Text.ToString());

    private StreamBlock StartStream(string role, string kind, int index)
    {
        AddRoleHeaderIfNeeded(role);
        var block = new StreamBlock { Role = role, Kind = kind, Index = index };
        UIElement body;
        if (kind == "tool")
        {
            block.Body = BodyText("", mono: true, muted: false);
            block.ResultLabel = Label("RESULT");
            block.Result = BodyText("", mono: true, muted: false);
            block.ResultLabel.Visibility = block.Result.Visibility = Visibility.Collapsed;
            var panel = new StackPanel();
            panel.Children.Add(Label("ARGUMENTS"));
            panel.Children.Add(block.Body);
            panel.Children.Add(block.ResultLabel);
            panel.Children.Add(block.Result);
            body = panel;
        }
        else
            body = new StackPanel { Children = { (block.Body = BodyText("", mono: false, muted: kind == "thinking")) } }; // room for the stats line

        var title = kind == "tool" ? "TOOL" : kind == "text" ? "RESPONSE" : "THINKING";
        block.Expander = BuildExpander(title, "", body, out block.Preview); // collapsed; the user opens what they want to read
        block.Label = (TextBlock)((StackPanel)block.Expander.Header).Children[0];
        block.Expander.Expanded += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        block.Expander.Collapsed += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        AddBlock(block.Expander, kind == "tool" ? "\n[tool call]\n" : kind == "text" ? "\n[response]\n" : "\n[thinking]\n");
        if (kind != "tool")
            OutputPanel.Children.Add(block.Live = NewLiveLine());
        SetPulse(block, true);
        return block;
    }

    // While its model call is active, only the title pulses; the streamed text (body and collapsed preview) is held
    // at a static dim so it stays readable, then eases back to full opacity once the call ends.
    private static void SetPulse(StreamBlock block, bool on)
    {
        block.Label?.BeginAnimation(UIElement.OpacityProperty, on
            ? new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }
            : null);
        foreach (UIElement? text in new UIElement?[] { block.Body, block.Preview })
            text?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 0.55 : 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = Decelerate });
    }

    private void Fold(StreamBlock block)
    {
        if (block.Expander is null || block.Touched)
            return;
        block.Programmatic = true;
        block.Expander.IsExpanded = false;
        block.Programmatic = false;
    }

    // The current model call finished: close the open block (a finished tool call waits for its result).
    private void EndStream(bool final)
    {
        if (_stream is not { } block)
            return;
        _stream = null;
        DropLive(block); // the call's live line moves to its next block; its usage event adds the exact stats
        if (block.Kind != "tool")
            _runClosed.Add(block);
        SetPulse(block, false);
        if (block.Preview is not null)
            block.Preview.Text = PreviewFor(block);
        switch (block.Kind)
        {
            case "text" when final && block.Label is not null:
                block.Label.Text = "FINAL RESPONSE";
                block.Label.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
                break;
            case "tool":
                _pendingTools.Enqueue(block);
                break;
            case "thinking":
                Fold(block);
                break;
        }
    }

    private readonly List<StreamBlock> _runClosed = new(); // closed thinking / response blocks waiting for their call's usage event

    // The call's stats as a muted line at the bottom of the section, and its speed after the collapsed preview.
    private void AddStatsLine(StreamBlock block, string text, double tps)
    {
        if (block.Expander?.Content is not StackPanel panel)
            return;
        var line = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), Tag = "stats", TextWrapping = TextWrapping.Wrap };
        line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(line);
        if (block.Preview is not null && tps > 0)
            block.Preview.Text += $"  ·  {tps:0.#} tok/s";
    }

    // While a call streams, a muted line under its open block: the call's delta chunks so far (about one token each) and
    // their rate since its first delta, refreshed at most 4×/s. Its usage event swaps it for the exact StatsLine.
    private sealed class CallLive { public double StartMs, ShownMs; public long Chunks; }
    private readonly Dictionary<string, CallLive> _callLive = new();

    private static TextBlock NewLiveLine()
    {
        var line = new TextBlock { FontSize = 11, Margin = new Thickness(2, 2, 0, 4), TextWrapping = TextWrapping.Wrap, Tag = "stats" };
        line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return line;
    }

    private void CountLive(string role, int chunks, StreamBlock? block)
    {
        var now = _clockMs();
        if (!_callLive.TryGetValue(role, out var call))
            _callLive[role] = call = new CallLive { StartMs = now };
        call.Chunks += chunks;
        if (block?.Live is not { } line || block.Role != role || (line.Text.Length > 0 && now - call.ShownMs < 250))
            return;
        call.ShownMs = now;
        var seconds = (now - call.StartMs) / 1000;
        line.Text = LauncherLogic.LiveLine(ChatName(CardFor(role), role), seconds > 0 ? call.Chunks / seconds : 0, call.Chunks);
    }

    private static void DropLive(StreamBlock block)
    {
        if (block.Live?.Parent is Panel panel)
            panel.Children.Remove(block.Live);
        block.Live = null;
    }

    private void CompleteTool(StreamBlock block, string name, string args, string result)
    {
        if (name.Length > 0)
            block.Name = name;
        if (args.Length > 0)
        {
            block.Text.Clear().Append(args);
            block.Body.Text = PrettyJson(args);
        }
        block.Result.Text = result;
        block.ResultLabel.Visibility = block.Result.Visibility = Visibility.Visible;
        if (block.Preview is not null)
            block.Preview.Text = PreviewFor(block);
        _transcript.Append($"\n=> {result}\n");
        Fold(block);
    }

    // ---- whole blocks (used when a model call could not be streamed)

    private void AddThinking(string role, string text)
    {
        if (text.Trim().Length == 0)
            return;
        AddRoleHeaderIfNeeded(role);
        AddBlock(BuildExpander("THINKING", Preview(text), BodyText(text, mono: false, muted: true), out _), $"[thinking]\n{text}\n");
    }

    private void AddTool(string role, string name, string args, string result)
    {
        AddRoleHeaderIfNeeded(role);
        var body = new StackPanel();
        body.Children.Add(Label("ARGUMENTS"));
        body.Children.Add(BodyText(PrettyJson(args), mono: true, muted: false));
        body.Children.Add(Label("RESULT"));
        body.Children.Add(BodyText(result, mono: true, muted: false));
        AddBlock(BuildExpander("TOOL", $"{name}  {Preview(args)}", body, out _), $"[tool {name}] {args}\n=> {result}\n");
    }

    private void AddMessage(string role, string text, bool final)
    {
        if (text.Trim().Length == 0)
            return;
        AddRoleHeaderIfNeeded(role);
        var expander = BuildExpander(final ? "FINAL RESPONSE" : "RESPONSE", Preview(text), BodyText(text, mono: false, muted: false), out _);
        if (final)
            ((TextBlock)((StackPanel)expander.Header).Children[0]).SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
        AddBlock(expander, $"[{(final ? "final" : "response")}]\n{text}\n");
    }

    private void AddNote(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 10, 0, 6) };
        panel.Children.Add(Label(title, accent: true));
        panel.Children.Add(BodyText(text, mono: false, muted: true));
        _blockRole = null;
        AddBlock(panel, $"\n[{title}] {text}\n");
    }

    // A thin rule under a context-compacted note, as Claude and ChatGPT mark it: what is above it was summarized.
    private static Border CompactionRule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 12), Tag = "compaction-rule" };
        rule.SetResourceReference(Border.BackgroundProperty, "Border");
        return rule;
    }

    private void AddBlock(UIElement block, string transcript)
    {
        if (OutputPanel.Parent is null) // first output of this run, or the conversation was cleared since
            CodePanel.Children.Add(OutputPanel);
        CodeEmpty.Visibility = Visibility.Collapsed;
        OutputPanel.Children.Add(block);
        _transcript.Append(transcript);
        if (!_code.Follow)
            Reveal(CodeJumpPill, true, slide: 10);
        FadeIn(block);
    }

    // Each section fades and slides in once when it is added; streamed text never comes through here.
    private static void FadeIn(UIElement block)
    {
        var time = TimeSpan.FromMilliseconds(180);
        var shift = new TranslateTransform();
        block.RenderTransform = shift;
        block.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = Decelerate, FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, time) { EasingFunction = Decelerate, FillBehavior = FillBehavior.Stop });
    }

    private static readonly IEasingFunction Decelerate = new CubicEase { EasingMode = EasingMode.EaseOut };

    // Wanted visibility per element for Reveal. IsHitTestVisible cannot hold it: WPF forces it false on every
    // child of a parent whose IsHitTestVisible is false (such as a log box inside a hidden pane).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, System.Runtime.CompilerServices.StrongBox<bool>> RevealWanted = new();

    // Fades an element in or out (sliding it by `slide` px when given) instead of snapping its visibility.
    // A fade-out still running cannot be clicked and does not collapse an element shown again before it finished.
    private static void Reveal(UIElement element, bool show, double slide = 0)
    {
        var shown = RevealWanted.TryGetValue(element, out var wanted) ? wanted.Value : element.Visibility == Visibility.Visible;
        if (show == shown)
            return;
        RevealWanted.AddOrUpdate(element, new System.Runtime.CompilerServices.StrongBox<bool>(show));
        var time = TimeSpan.FromMilliseconds(show ? 160 : 120);
        var fade = new DoubleAnimation(show ? 1 : 0, time) { EasingFunction = Decelerate };
        if (show && element.Visibility != Visibility.Visible)
        {
            fade.From = 0;
            element.Visibility = Visibility.Visible;
        }
        else if (!show)
            fade.Completed += (_, _) => { if (RevealWanted.TryGetValue(element, out var now) && !now.Value) element.Visibility = Visibility.Collapsed; };
        element.IsHitTestVisible = show;
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        if (slide == 0)
            return;
        if (element.RenderTransform is not TranslateTransform shift)
            element.RenderTransform = shift = new TranslateTransform();
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(show ? 0 : slide, time) { From = fade.From is null ? null : slide, EasingFunction = Decelerate });
    }

    private Expander BuildExpander(string kind, string summary, UIElement body, out TextBlock preview)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Label(kind, accent: false, margin: new Thickness(0, 0, 10, 0)));
        preview = new TextBlock { Text = summary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        header.Children.Add(preview);
        var expander = new Expander { Header = header, Content = body };
        expander.SetResourceReference(StyleProperty, "BlockExpander");
        return expander;
    }

    private TextBlock Label(string text, bool accent = false, Thickness? margin = null)
    {
        var label = new TextBlock { Text = text, Margin = margin ?? new Thickness(0, 6, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(StyleProperty, "SectionHeader");
        label.SetResourceReference(TextBlock.ForegroundProperty, accent ? "AccentLight" : "Muted");
        label.FontSize = 10;
        return label;
    }

    // Read-only TextBox so the text is selectable; wheel input is passed on to the output scroller.
    private TextBox BodyText(string text, bool mono, bool muted, Conversation? conv = null)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = mono ? AppFonts.Mono : FontFamily,
            FontSize = mono ? 12 : 13,
            FontStyle = muted ? FontStyles.Italic : FontStyles.Normal,
            Cursor = Cursors.IBeam,
        };
        box.SetResourceReference(TextBox.ForegroundProperty, muted ? "Muted" : "TextPrimary");
        ForwardWheel(box, conv);
        return box;
    }

    // Selectable text boxes would swallow the mouse wheel; pass it on to the conversation's scroller (a run's: Code).
    private void ForwardWheel(UIElement element, Conversation? conv)
    {
        element.PreviewMouseWheel += (_, e) =>
        {
            var c = conv ?? _code;
            e.Handled = true;
            if (e.Delta > 0)
                c.Follow = false;
            c.Scroll.ScrollToVerticalOffset(c.Scroll.VerticalOffset - e.Delta);
        };
    }

    private static string Preview(string text)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length > 110 ? flat[..110] + "…" : flat;
    }

    private static string PrettyJson(string text)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(text).RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
        catch (JsonException) { return text; }
    }

    // ---------------------------------------------------------------- execution log

    // The execution log also goes to a file per day in <data>\logs, so a run's errors survive the app closing (the Run
    // output's "Open logs" opens the folder). Files older than 14 days are removed at the first write of a session.
    internal static string LogsRoot => Path.Combine(AppSettings.DataRoot, "logs");
    private bool _logsPruned;

    private void WriteLogFile(string line)
    {
        try
        {
            Directory.CreateDirectory(LogsRoot);
            if (!_logsPruned)
            {
                _logsPruned = true;
                foreach (var old in new DirectoryInfo(LogsRoot).GetFiles("*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-14)))
                    old.Delete();
            }
            File.AppendAllText(Path.Combine(LogsRoot, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // logging never stops the app
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LogsRoot);
        Process.Start(new ProcessStartInfo(LogsRoot) { UseShellExecute = true });
    }

    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        _log.AppendLine(line);
        WriteLogFile(line);
    }

    // --ui-smoke: stream a fake turn and flip every animated state, so a broken template or storyboard fails a
    // check instead of the user. Hover and press storyboards need real pointer input and are not covered.
    internal async Task<bool> RunUiSmokeAsync()
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.xaml.cs:{line}"); return false; }
        static Task Settle() => Task.Delay(400);
        _llamaStateFile = Path.Combine(AppSettings.DataRoot, "llama-state.json"); // never the real server's record
        // Chat shows every agent the checks use (it shows LEAD alone by default: checked on its own)
        _settings.ChatCards = ["implementer", "reviewer", "compactor", "smoke-chat-custom"];
        _settingsShow = window => { window.ShowActivated = false; window.Show(); }; // ShowDialog would block the run
        _dialogsQuiet = true;
        // AGENT_ORCHESTRATOR_SNAPSHOTS=<folder> also saves a picture of each state (rendered without a screen).
        var snapshots = Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_SNAPSHOTS");
        void Snap(string name, FrameworkElement? target = null) // target: a popup such as a menu, which the window picture leaves out
        {
            if (string.IsNullOrEmpty(snapshots))
                return;
            SavePicture(target ?? this, Path.Combine(snapshots, name + ".png"));
        }

        await Settle();
        if (!WorkerRail.IsVisible || DetailsPanel.IsVisible) // collapsed to the rail by default; the checks below use the full column
            return Fail();
        SetWorkerRail(false);
        await Settle();
        Snap("1-idle");
        SetSidebar(true);
        await Settle();
        if (SidePane.ActualWidth > 60 || SidebarFull.IsVisible || !SidebarRail.IsVisible)
            return Fail();
        Snap("5-collapsed");
        SetSidebar(false);
        await Settle();
        if (Math.Abs(SidePane.ActualWidth - SidebarWidth) > 2 || !SidebarFull.IsVisible || SidebarRail.IsVisible)
            return Fail();
        // Dual by default (Lead + Reviewer): IMPLEMENTER is hidden and runs as LEAD (its activity lights LEAD), "+" adds only
        // custom workers; tri (Settings > Agents) shows it with its own config entry. The run's request carries the mode, and the
        // cards go ALWAYS ON (LEAD, COMPACTOR), then HELPERS (IMPLEMENTER, REVIEWER), then the custom workers in the order added.
        // Chat shows the same cards (one set for every conversation, checked below).
        ShowView();
        {
            var main = CardFor("planner")!;
            var (implementer, reviewer) = (CardFor("implementer")!, CardFor("reviewer")!);
            List<string> Plus() => BuildAddWorkerMenu(this).Items.OfType<MenuItem>().Select(i => (string)i.Header + (i.IsEnabled ? "" : " (off)")).ToList();
            bool TabShown(RoleCard card) => WorkerTabs.ItemContainerGenerator.ContainerFromItem(card) is UIElement { Visibility: Visibility.Visible };
            // Its tab, its rail dot and its line in the Chat target dropdown.
            bool Shown(RoleCard card) => TabShown(card) && RailDots.ItemContainerGenerator.ContainerFromItem(card) is UIElement { Visibility: Visibility.Visible } &&
                                         ListedWorkers(_chat).Contains(card);
            var workerConfig = ActiveConfigPath();
            JsonNode? RoleConfig(string role) => JsonNode.Parse(File.ReadAllText(workerConfig))!["roles"]?[role];
            string Order() => string.Join(",", _cards.Where(c => !c.Hidden).Select(c => c.Title));
            string? RunMode() => (string?)JsonNode.Parse(RunRequest(@"Y:\smoke"))!["workflow"];
            if (_settings.WorkflowMode != "dual" || WorkflowDualRadio.IsChecked != true || !implementer.Hidden || reviewer.Hidden || main.Hidden || TabShown(implementer) ||
                Shown(implementer) || ListedWorkers(_chat).Contains(implementer) || !Shown(reviewer) || !Shown(main) || main.Covers != "Planner · Implementer" || Plus() is not ["Custom worker…"] ||
                Order() != "LEAD,COMPACTOR,REVIEWER" || RunMode() != "dual")
                return Fail();
            // The group labels: ALWAYS ON over LEAD, HELPERS · WHEN NEEDED over the first helper shown
            if (main.GroupLabel != "ALWAYS ON" || CardFor("compactor")!.GroupLabel != "" || reviewer.GroupLabel != "HELPERS · WHEN NEEDED")
                return Fail();
            // Chat shows Code's cards, and its "+" adds only custom workers (no Chat-only agent list)
            var chatCards = _settings.ChatCards.ToList();
            _settings.ChatCards.Clear();
            ShowView(chat: true);
            var chatDefault = Order();
            var chatPlus = Plus();
            _settings.ChatCards = chatCards;
            ShowView();
            if (chatDefault != "LEAD,COMPACTOR,REVIEWER" || chatPlus is not ["Custom worker…"] || Order() != "LEAD,COMPACTOR,REVIEWER")
                return Fail();
            foreach (var json in new[] { """{"e":"delta","role":"implementer","kind":"text","text":"tok "}""", """{"e":"usage","role":"implementer","prompt":7,"completion":3,"tps":21}""" })
                _lines.Enqueue(LauncherLogic.EventPrefix + json);
            DrainLines();
            if (!main.IsActive || implementer.IsActive || _activeRole != "planner" || main.TokensPerSec != "21.0 tok/s" || !main.TurnDetail.Contains("↑ 7 prompt") ||
                implementer.TokensPerSec != "—" || main.Covers != "Planner · Implementer")
                return Fail();
            main.ClearLive();
            (main.IsActive, main.Status, _activeRole) = (false, "Idle", null);
            WorkflowTriRadio.IsChecked = true;
            await Settle();
            if (_settings.WorkflowMode != "tri" || AppSettings.Load().WorkflowMode != "tri" || implementer.Hidden || !Shown(implementer) || main.HasCovers ||
                RoleConfig("implementer")?["same_as"] is not null || RoleConfig("reviewer")?["same_as"] is not null ||
                Order() != "LEAD,COMPACTOR,IMPLEMENTER,REVIEWER" || RunMode() != "tri")
                return Fail();
            SelectWorker(implementer);
            WorkflowDualRadio.IsChecked = true;
            await Settle();
            if (_settings.WorkflowMode != "dual" || !implementer.Hidden || TabShown(implementer) || ListedWorkers(_chat).Contains(implementer) || _shownWorker != main ||
                (string?)RoleConfig("implementer")?["same_as"] != "planner" || RoleConfig("reviewer")?["same_as"] is not null ||
                Order() != "LEAD,COMPACTOR,REVIEWER" || RunMode() != "dual")
                return Fail();
            Snap("0-dual");
            // The checks below use all three built-ins. Saved workers with a custom one before COMPACTOR load in the fixed order.
            SetWorkflow("tri");
            var savedWorkers = _settings.Workers;
            void Reload(List<WorkerSetting> workers) => HoldWorkerTab(() =>
                { _settings.Workers = workers; _cards.Clear(); LoadWorkers(); PrefillCards(); SelectWorker(CardFor("planner")); });
            Reload([new() { Id = "planner" }, new() { Id = "smoke-order", Title = "ORDER W", Custom = true }, new() { Id = "reviewer" }]);
            var ordered = Order();
            Reload(savedWorkers);
            await Settle();
            if (ordered != "LEAD,COMPACTOR,IMPLEMENTER,REVIEWER,ORDER W" || _cards.Any(c => c.Hidden) || !TabShown(CardFor("implementer")!) ||
                !TabShown(CardFor("reviewer")!) || CardFor("planner")!.HasCovers || _shownWorker != CardFor("planner"))
                return Fail();
        }
        foreach (var json in new[]
        {
            """{"e":"model","role":"planner","id":"smoke-model Q4_K_M"}""",
            """{"e":"delta","role":"planner","kind":"thinking","text":"thinking about it"}""",
            """{"e":"delta","role":"planner","kind":"tool","index":0,"name":"read_file","text":"{\"path\":\"a.js\"}"}""",
            """{"e":"done","role":"planner","final":false}""",
            """{"e":"tool","role":"planner","name":"read_file","args":"{\"path\":\"a.js\"}","result":"OK"}""",
            """{"e":"delta","role":"planner","kind":"text","text":"the answer"}""",
            """{"e":"done","role":"planner","final":true}""",
            """{"e":"usage","role":"planner","prompt":10,"completion":5,"tps":50}""",
        })
            _lines.Enqueue(LauncherLogic.EventPrefix + json);
        _lines.Enqueue("plain log line");
        _lines.Enqueue(LauncherLogic.EventPrefix + """{"e":"resume","id":"T0","round":1}""");
        DrainLines();
        await Settle();
        if (!_transcript.ToString().Contains("[RESUMED  T0] Continuing at review round 2"))
            return Fail();

        var expanders = OutputPanel.Children.OfType<Expander>().ToList();
        if (expanders.Count < 3)
            return Fail(); // thinking, tool call and response
        SetAllExpanded(true);
        await Settle();
        if (!expanders.All(x => x.IsExpanded))
            return Fail();
        Snap("2-expanded");
        SetAllExpanded(false);
        await Settle();
        if (expanders.Any(x => x.IsExpanded))
            return Fail();
        var tool = SectionText(expanders.First(x => ((TextBlock)((StackPanel)x.Header).Children[0]).Text == "TOOL"));
        if (!(tool.StartsWith("TOOL") && tool.Contains("ARGUMENTS") && tool.Contains("a.js") && tool.Contains("RESULT") && tool.EndsWith("OK")))
            return Fail();
        // The run's output is part of the Code conversation, not a separate view.
        ShowView();
        await Settle();
        PlayHoverStoryboards(this);
        if (OutputPanel.Parent != CodePanel || CodeEmpty.IsVisible || !CodeChat.IsVisible)
            return Fail();
        // Scrolled away from the end: new output brings in the jump pill, which returns to following.
        _code.Follow = false;
        _lines.Enqueue(LauncherLogic.EventPrefix + """{"e":"verdict","role":"reviewer","id":"T1","verdict":"PASS","round":1}""");
        DrainLines(); // the verdict also raises a toast
        Toast("Something failed", "#FF667A");
        await Settle();
        if (!CodeJumpPill.IsVisible || !CodeJumpPill.IsHitTestVisible || ToastStack.Children.Count != 2)
            return Fail();
        Snap("2b-toast-and-jump");
        ChatJump_Click(CodeJumpPill, new RoutedEventArgs());
        await Settle();
        if (!_code.Follow || CodeJumpPill.IsVisible)
            return Fail();
        // toasts leave on their own: 3 s on screen + a 180 ms fade, with margin for a busy dispatcher
        for (var waited = 0; waited < 5000 && ToastStack.Children.Count != 0; waited += 100)
            await Task.Delay(100);
        if (ToastStack.Children.Count != 0)
            return Fail();
        // Live token stats: deltas alone drive the count and rate until the call's usage event gives the exact numbers.
        var live = CardFor("implementer");
        if (live is null)
            return Fail();
        for (var tick = 0; tick < 8; tick++)
        {
            for (var n = 0; n < 5; n++)
                _lines.Enqueue(LauncherLogic.EventPrefix + """{"e":"delta","role":"implementer","kind":"text","text":"tok "}""");
            DrainLines();
            await Task.Delay(40);
        }
        if (live.LiveCompletion != 40 || live.TokensPerSec == "—" || !live.TurnDetail.Contains("40 generated"))
            return Fail();
        _lines.Enqueue(LauncherLogic.EventPrefix + """{"e":"usage","role":"implementer","prompt":123,"completion":77,"tps":42}""");
        DrainLines();
        if (live.LiveCompletion != 0 || !live.TurnDetail.Contains("123 prompt") || !live.TurnDetail.Contains("77 generated") || live.TokensPerSec != "42.0 tok/s")
            return Fail();
        // Task counters: TASK TOKENS commit on task_end (usage only, no live estimates) and hold through the next
        // task; TASK TIME freezes at task_end and restarts at task_start; TURN TIME restarts per role and freezes on stop.
        void Feed(params string[] events)
        {
            foreach (var json in events)
                _lines.Enqueue(LauncherLogic.EventPrefix + json);
            DrainLines();
        }
        string TaskTokens() => string.Join("|", _cards.Select(c => $"{c.Role}:{c.TaskTokens}"));
        var expected = string.Join("|", _cards.Select(c => c.Role + ":" + c.Role switch { "planner" => "300", "implementer" => "1,500", "reviewer" => "500", _ => "0" }));
        Feed("""{"e":"task_start"}""", """{"e":"usage","role":"planner","prompt":200,"completion":100,"tps":30}""",
             """{"e":"task","id":"T2","outcome":"counters"}""");
        if (_cards.Any(c => c.TaskTokens != "—") || !_taskWatch.IsRunning || TaskTimeText.Text != "0:00")
            return Fail();
        Feed("""{"e":"compacted","role":"implementer","before":30100,"after":8200}"""); // a note in the run output
        if (!_transcript.ToString().Contains(": Context compacted · 30.1k → 8.2k tokens"))
            return Fail();
        Feed(Enumerable.Repeat("""{"e":"delta","role":"implementer","kind":"text","text":"tok "}""", 5)
            .Append("""{"e":"usage","role":"implementer","prompt":1000,"completion":500,"tps":40}""")
            .Append("""{"e":"usage","role":"reviewer","prompt":400,"completion":100,"tps":40}""")
            .Append("""{"e":"verdict","role":"reviewer","id":"T2","verdict":"PASS","round":1}""")
            .Append("""{"e":"task_end","id":"T2","status":"passed"}""").ToArray());
        var frozenTask = TaskTimeText.Text;
        if (TaskTokens() != expected || _taskWatch.IsRunning || !_turnWatch.IsRunning)
            return Fail();
        await Task.Delay(1100);
        if (TaskTimeText.Text != frozenTask)
            return Fail(); // frozen at task_end
        Feed("""{"e":"task_start"}""");
        await Task.Delay(1300); // the 250 ms clock has ticked past 1 s whatever its phase
        if (TaskTimeText.Text is not ("0:01" or "0:02") || TurnTimeText.Text == "0:00")
            return Fail(); // ticking again from zero; the reviewer's turn kept ticking
        Feed("""{"e":"usage","role":"planner","prompt":50,"completion":50,"tps":30}""");
        if (TurnTimeText.Text != "0:00" || TaskTokens() != expected)
            return Fail(); // the planner's turn starts fresh; the last completed task's tokens stay
        await Settle();
        Snap("9-task-counters");
        StopClocks(); // what the run's cleanup does on Stop
        var (stoppedTask, stoppedTurn) = (TaskTimeText.Text, TurnTimeText.Text);
        await Task.Delay(1100);
        if (TaskTimeText.Text != stoppedTask || TurnTimeText.Text != stoppedTurn || _clock.IsEnabled || TaskTokens() != expected)
            return Fail();
        // Presets: save, change the workers and the config, then apply; both come back as saved.
        var presetProject = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-smoke-{Guid.NewGuid():N}");
        var previousProject = _projectPath;
        try
        {
            Directory.CreateDirectory(presetProject);
            var presetConfig = Path.Combine(presetProject, "config.json");
            string Config(string temperature, string prompt = "") => """{"roles":{"planner":{"sampling":{"temperature":""" + temperature + """},"chat_system_prompt":"Planner: """ + prompt + "\"},\"implementer\":{\"base_url\":\"http://h/v1\"},\"reviewer\":{}}}";
            string Cards() => string.Join("|", _cards.Select(c => $"{c.Role}:{c.Title}"));
            File.WriteAllText(presetConfig, Config("0.3", "saved"));
            ProjectPath = presetProject;
            var presetWorker = new RoleCard("smoke-preset", "PRESET W", custom: true);
            presetWorker.Config.ModelOptions["top_k"] = "7";
            _cards.Add(presetWorker);
            SaveWorkers();
            var saved = Cards();
            SavePreset("Smoke");
            SetWorkflow("dual"); // applying the preset keeps the mode
            presetWorker.Config.ModelOptions["top_k"] = "9"; // an edit saved after the preset
            SaveWorkers();
            AddCustomWorker();
            File.WriteAllText(presetConfig, Config("0.9", "changed"));
            if (Cards() == saved || _settings.Presets.Count(p => p.Name == "Smoke") != 1)
                return Fail();
            SelectWorker(CardFor("planner")); // the open tab must show the restored values after the apply
            RefreshWorkerTab(force: true); // (the config was rewritten behind it)
            await Settle();
            if (SmokeField("ChatSystemPrompt").Text != "Planner: changed")
                return Fail();
            var menu = BuildPresetsMenu();
            menu.IsOpen = true;
            await Settle();
            if (!menu.IsOpen || menu.Items.OfType<MenuItem>().All(i => i.Header as string != "Smoke"))
                return Fail();
            Snap("7-presets-menu", menu);
            menu.IsOpen = false;
            ApplyPreset(_settings.Presets.Single(p => p.Name == "Smoke"));
            await Settle();
            // The worker config follows a project's preset too, and takes the roles itself with no project; the template never changes.
            var workerConfig = LauncherLogic.EnsureWorkerConfig(_workflowRoot!);
            var templateBytes = File.ReadAllBytes(Path.Combine(_workflowRoot!, "config.json"));
            double Temperature(string path) => (double)JsonNode.Parse(File.ReadAllText(path))!["roles"]!["planner"]!["sampling"]!["temperature"]!;
            if (Cards() != saved || Temperature(presetConfig) != 0.3 || Temperature(workerConfig) != 0.3 ||
                _settings.WorkflowMode != "dual" || !CardFor("implementer")!.Hidden || CardFor("reviewer")!.Hidden)
                return Fail();
            SetWorkflow("tri");
            // Both parts of the parent preset come back (a built-in's chat prompt, a custom worker's option), the tab stays and shows them.
            if ((string)JsonNode.Parse(File.ReadAllText(presetConfig))!["roles"]!["planner"]!["chat_system_prompt"]! != "Planner: saved" ||
                CardFor("smoke-preset")!.Config.ModelOptions["top_k"] != "7" || _shownWorker?.Role != "planner" || !CardFor("planner")!.IsSelected ||
                SmokeField("ChatSystemPrompt").Text != "Planner: saved")
                return Fail();
            File.WriteAllText(workerConfig, Config("0.9"));
            ProjectPath = "";
            ApplyPreset(_settings.Presets.Single(p => p.Name == "Smoke"));
            ProjectPath = presetProject;
            if (Temperature(workerConfig) != 0.3 || !File.ReadAllBytes(Path.Combine(_workflowRoot!, "config.json")).SequenceEqual(templateBytes))
                return Fail();
            DeletePreset(_settings.Presets.Single(p => p.Name == "Smoke"));
            if (_settings.Presets.Any(p => p.Name == "Smoke"))
                return Fail();
            RemoveWorker(CardFor("smoke-preset")!);
            File.WriteAllText(presetConfig, Config("0.3"));
            File.WriteAllText(workerConfig, Config("0.9"));
            RefreshWorkerTab(force: true);

            // The workflow root's config.json is the project template: syncing with it selected must leave it untouched.
            var templatePath = Path.Combine(_workflowRoot ?? "", "config.json");
            var templateBefore = File.ReadAllBytes(templatePath);
            ProjectPath = _workflowRoot ?? "";
            SyncCustomWorkers();
            ProjectPath = presetProject;
            if (!File.ReadAllBytes(templatePath).SequenceEqual(templateBefore))
            {
                File.WriteAllBytes(templatePath, templateBefore);
                return Fail();
            }
            var planner = CardFor("planner")!;
            if (!await ModelSettingsSmoke(planner, presetConfig, Snap))
                return Fail();
            if (!await ModelConfigSmoke(presetConfig, Snap))
                return Fail();
            if (!await UiTweaksSmoke(Snap))
                return Fail();
            if (!await CompactorSmoke(presetConfig, Snap))
                return Fail();

            // Dialogs are built without ShowDialog (it would block), shown off-screen, and closed.
            async Task<bool> Show(DialogShell dialog, string name)
            {
                dialog.ShowActivated = false;
                dialog.Show();
                await Settle();
                var shown = dialog.IsVisible && dialog.WindowStyle == WindowStyle.None && dialog.AllowsTransparency && dialog.ActualWidth > 100 && dialog.ActualHeight > 60
                    && ReferenceEquals(dialog.Frame.Background, Resources["DialogBackground"]); // borderless, on the popup shade
                Snap(name, dialog);
                dialog.Close();
                return shown && !dialog.IsVisible;
            }
            var mdFile = Path.Combine(presetProject, "PROJECT.md");
            File.WriteAllText(mdFile, "# Smoke project\n");
            var limitsDialog = BuildTaskLimitsDialog(presetConfig, ("6", ""), [("Implementer", 16384)]);
            if (limitsDialog.Warning.Text != "6 files per task may not fit Implementer's 16k context" || limitsDialog.Warning.Visibility != Visibility.Visible
                || !await Show(limitsDialog.Dialog, "9b-task-limits-dialog"))
                return Fail();
            if (!await Show(BuildAskDialog("Save preset", "Name this set of workers and their settings.", "Save", "").Dialog, "10-prompt")
                || !await Show(BuildExistingProjectMdDialog(mdFile).Dialog, "11-existing-projectmd"))
                return Fail();

            // Restart Project on a complete temp project under bin; the Recycle Bin is replaced by a delete confined to it,
            // and RestartProject is called directly, so no run starts.
            var restartProject = Path.Combine(AppContext.BaseDirectory, $"smoke-restart-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(Path.Combine(restartProject, ".git"));
                Directory.CreateDirectory(Path.Combine(restartProject, "src", "deep"));
                foreach (var name in LauncherLogic.ProjectFiles.Append("app.js").Append("HANDOFF.md").Append(".ao_old.txt"))
                    File.WriteAllText(Path.Combine(restartProject, name), "old " + name);
                File.WriteAllText(Path.Combine(restartProject, ".git", "HEAD"), "ref: refs/heads/main");
                File.WriteAllText(Path.Combine(restartProject, "src", "deep", "x.js"), "x");
                File.WriteAllText(Path.Combine(restartProject, "PROJECT.md"), "# Smoke restart\nkeep me\n");
                File.WriteAllText(Path.Combine(restartProject, "STATE.md"), "# Verified state\n\n## Passed: WEB-012\n- x\n\n## Project complete\n- Summary: done\n");
                string Entries() => string.Join("|", Directory.GetFileSystemEntries(restartProject, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(restartProject, p) + ":" + (File.Exists(p) ? File.ReadAllText(p) : "")).Order(StringComparer.Ordinal));
                void Delete(string path)
                {
                    if (!Path.GetFullPath(path).StartsWith(restartProject + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"smoke delete outside its project: {path}");
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path);
                }
                ProjectPath = restartProject;
                if (CodeStartButton.Content as string != "Restart Project")
                    return Fail();
                await Settle();
                Snap("15b-restart-button");
                if (!await Show(BuildRestartDialog(restartProject), "15-restart-dialog"))
                    return Fail();
                // Cancel: nothing changes.
                var before = Entries();
                _restartRecycle = Delete;
                _restartConfirm = _ => false;
                if (RestartProject(restartProject) || Entries() != before)
                    return Fail();
                // A file that cannot be recycled stops the restart there: earlier items are gone, later ones and init untouched.
                _restartConfirm = _ => true;
                _restartRecycle = path => { if (Path.GetFileName(path) == "app.js") throw new IOException("locked"); Delete(path); };
                if (RestartProject(restartProject) || Directory.Exists(Path.Combine(restartProject, ".ao_old.txt")) || File.Exists(Path.Combine(restartProject, ".ao_old.txt")) ||
                    !File.Exists(Path.Combine(restartProject, "app.js")) || File.ReadAllText(Path.Combine(restartProject, "STATE.md")) == File.ReadAllText(Path.Combine(_workflowRoot!, "STATE.md")) ||
                    !_log.ToString().Contains("app.js could not be moved to the Recycle Bin"))
                    return Fail();
                // Confirmed: only PROJECT.md and .git survive, then init adds the template files; PROJECT.md is unchanged.
                var recycled = new List<string>();
                _restartRecycle = path => { recycled.Add(Path.GetFileName(path)); Delete(path); };
                if (!RestartProject(restartProject) || recycled.Contains("PROJECT.md") || recycled.Contains(".git") ||
                    File.ReadAllText(Path.Combine(restartProject, "PROJECT.md")) != "# Smoke restart\nkeep me\n" ||
                    File.ReadAllText(Path.Combine(restartProject, ".git", "HEAD")) != "ref: refs/heads/main" ||
                    Directory.GetFileSystemEntries(restartProject).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                        LauncherLogic.ProjectFiles.Append(".git").Order(StringComparer.OrdinalIgnoreCase)) is false ||
                    File.ReadAllText(Path.Combine(restartProject, "STATE.md")) != File.ReadAllText(Path.Combine(_workflowRoot!, "STATE.md")) ||
                    CodeStartButton.Content as string != "Start Task")
                    return Fail();
                // The template folder is refused before anything is asked or recycled.
                var asked = false;
                _restartRecycle = _ => throw new IOException("smoke must not recycle the template");
                _restartConfirm = _ => { asked = true; return false; };
                if (RestartProject(_workflowRoot!) || asked || !_log.ToString().Contains("holds the Agent Orchestrator template"))
                    return Fail();
            }
            finally
            {
                _restartConfirm = null;
                _restartRecycle = LauncherLogic.Recycle;
                ProjectPath = presetProject;
                if (Directory.Exists(restartProject))
                    Directory.Delete(restartProject, recursive: true);
            }

            // Custom workers: reserved titles are refused, the settings dialog has the system prompt (built-ins do not),
            // and the project's config.json receives the custom_workers entry.
            static bool HasTag(DependencyObject d, string tag) =>
                d is FrameworkElement { Tag: string t } && t == tag || LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().Any(c => HasTag(c, tag));
            var tester = new RoleCard("smoke-tester", "TESTER", custom: true);
            _cards.Add(tester);
            if (RenameTo(tester, "Reviewer") is null || RenameTo(tester, " planner ") is null || RenameTo(tester, "IMPLEMENTER") is null || tester.Title != "TESTER" ||
                RenameTo(tester, "Docs") is not null || tester.Title != "Docs")
                return Fail();
            tester.ModelId = tester.Config.ModelPath = "org/M-GGUF";
            tester.Config.ModelExpected = "M-GGUF";
            tester.Config.Kind = "reviewer";
            // No system prompt or description: nothing is missing; config.json gets the kind's defaults, the dialog shows them
            // prefilled, and Save keeps them (switching kind swaps in the other kind's defaults).
            SyncCustomWorkers();
            var synced = JsonNode.Parse(File.ReadAllText(presetConfig))!["custom_workers"]!.AsArray();
            if (tester.HasSetupProblem || synced.Count != 1 || (string)synced[0]!["title"]! != "Docs" || (string)synced[0]!["kind"]! != "reviewer" ||
                (string)synced[0]!["system_prompt"]! != LauncherLogic.DefaultSystemPrompt("reviewer"))
                return Fail();
            SelectWorker(tester); // its tab
            await Settle();
            var prefilled = WorkerSettingsHost;
            static T? FindIn<T>(DependencyObject d, Func<T, bool> match) where T : DependencyObject =>
                d is T t && match(t) ? t : LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().Select(c => FindIn(c, match)).FirstOrDefault(r => r is not null);
            var promptBox = FindIn<TextBox>(prefilled, t => t.Tag as string == "SystemPrompt");
            var specialist = FindIn<RadioButton>(prefilled, r => r.Tag as string == "specialist");
            var reviewerKind = FindIn<RadioButton>(prefilled, r => r.Tag as string == "reviewer");
            if (promptBox?.Text != LauncherLogic.DefaultSystemPrompt("reviewer") || specialist is null || reviewerKind is null)
                return Fail();
            specialist.IsChecked = true;
            var swapped = promptBox.Text == LauncherLogic.DefaultSystemPrompt("specialist");
            reviewerKind.IsChecked = true;
            // Its options show what it inherits (the Implementer's temperature); Save keeps only what differs, so it still inherits.
            var inheritedTemperature = FindIn<TextBox>(prefilled, t => t.Tag as string == "temperature")?.Text;
            FindIn<TextBox>(prefilled, t => t.Tag as string == "top_k")!.Text = "12";
            FindIn<Button>(prefilled, b => b.Content as string == "Save")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (!swapped || tester.Config.SystemPrompt != LauncherLogic.DefaultSystemPrompt("reviewer") || tester.Config.Description != LauncherLogic.DefaultDescription("reviewer") ||
                inheritedTemperature != "0.33" || string.Join(",", tester.Config.ModelOptions.Select(p => $"{p.Key}={p.Value}")) != "top_k=12")
                return Fail();
            await Settle();
            if (_shownWorker != tester || !HasTag(WorkerSettingsHost, "SystemPrompt") || !HasTag(WorkerSettingsHost, "Description") || !SelectWorker(planner))
                return Fail();
            if (HasTag(WorkerSettingsHost, "SystemPrompt") || !HasTag(WorkerSettingsHost, "Base URL"))
                return Fail();
            RemoveWorker(tester);
            if (JsonNode.Parse(File.ReadAllText(presetConfig))!["custom_workers"]!.AsArray().Count != 0)
                return Fail();

            // Legacy duplicate titles: the second card warns and is not written.
            var first = new RoleCard("smoke-d1", "Dup", custom: true);
            var second = new RoleCard("smoke-d2", "DUP", custom: true);
            foreach (var card in new[] { first, second })
            {
                card.ModelId = card.Config.ModelPath = "org/M-GGUF";
                card.Config.Kind = "reviewer";
                card.Config.SystemPrompt = "p";
                _cards.Add(card);
            }
            // Start's pre-run step: the skipped worker's line survives the view reset, and a toast is raised.
            Log("stale line");
            if (!PrepareRun() || first.HasSetupProblem || !second.HasSetupProblem || !second.SetupTip.Contains("already used") ||
                _log.ToString().Contains("stale line") || !_log.ToString().Contains("DUP: title 'DUP' is already used") || ToastStack.Children.Count == 0 ||
                JsonNode.Parse(File.ReadAllText(presetConfig))!["custom_workers"]!.AsArray().Count != 1)
                return Fail();
            // config.json cannot be written: the start is aborted before anything is cleared or launched.
            Log("kept line");
            File.SetAttributes(presetConfig, FileAttributes.ReadOnly);
            bool started;
            try { started = PrepareRun(); }
            finally { File.SetAttributes(presetConfig, FileAttributes.Normal); }
            if (started || _process is not null || StatusText.Text != "Could not update config.json" || !_log.ToString().Contains("kept line") ||
                !_log.ToString().Contains("Could not write custom workers") || ToastStack.Children.OfType<Border>().All(t => !((TextBlock)((StackPanel)t.Child).Children[1]).Text.StartsWith("Run not started")))
                return Fail();
            RemoveWorker(first);
            RemoveWorker(second);
            SetStatus("Ready", "#667085");
        }
        finally
        {
            ProjectPath = previousProject;
            if (Directory.Exists(presetProject))
                Directory.Delete(presetProject, recursive: true);
        }
        // Chat view, driven without Python: worker menu, target chip, Single and Multi turns fed by fake events.
        {
            var outputCount = OutputPanel.Children.Count;
            ShowView(chat: true);
            await Settle();
            if (!ChatView.IsVisible)
                return Fail();
            var stray = new RoleCard("smoke-chat-custom", "Smoke chat", custom: true);
            _cards.Add(stray);
            var workerMenu = BuildWorkerMenu(ChatPlus);
            var headers = workerMenu.Items.OfType<MenuItem>().ToList();
            var names = headers.Select(i => (string)i.Header).ToList();
            var broken = headers.FirstOrDefault(i => ((string)i.Header).StartsWith("Smoke chat"));
            if (new[] { "Lead", "Implementer", "Reviewer" }.Any(n => !names.Contains(n)) || broken is null || broken.IsEnabled || workerMenu.Placement != PlacementMode.Top)
                return Fail();
            workerMenu.IsOpen = true;
            await Settle();
            Snap("14-chat-plus-menu", workerMenu);
            workerMenu.IsOpen = false;
            RemoveWorker(stray);

            PickChatWorker("reviewer");
            // No Single / Multi toggles any more: Chat messages one worker, which may delegate.
            if ((string)ChatTargetChip.Content != "To: Reviewer  ▾" || FindName("ChatSingleToggle") is not null || FindName("ChatMultiToggle") is not null ||
                FindName("CodeSingleToggle") is not null || FindName("CodeMultiToggle") is not null)
                return Fail();
            string Ev(string json) => LauncherLogic.EventPrefix + json;
            string Delta(string role, string kind, string text) => Ev($$"""{"e":"delta","role":"{{role}}","kind":"{{kind}}","text":"{{text}}"}""");

            async Task<bool> Turn(string text, string[] events, string reply)
            {
                StartChatTurn(text);
                if (!_chatBusy || CodeStartButton.IsEnabled)
                    return false;
                foreach (var line in events)
                    _lines.Enqueue(line);
                _lines.Enqueue(Ev($$"""{"e":"chat_reply","role":"reviewer","text":"{{reply}}"}"""));
                DrainLines();
                var current = _reply!;
                await Settle();
                FinishChatTurn(0, false, null);
                return current.Answer?.Text == reply && current.Final == reply && !_chatBusy && CodeStartButton.IsEnabled == (_workflowRoot is not null);
            }

            var reviewer = CardFor("reviewer")!;
            if (!await Turn("Hello", new[]
            {
                Ev("""{"e":"chat_stage","role":"reviewer","stage":"answer","title":"Reviewer"}"""),
                Ev("""{"e":"loading","role":"reviewer","path":"smoke/chat-model.gguf"}"""),
                Ev("""{"e":"model","role":"reviewer","id":"chat-model Q4_K_M"}"""),
                Delta("reviewer", "thinking", "hmm"),
                Delta("reviewer", "text", "streamed answer"),
                Ev("""{"e":"done","role":"reviewer","final":true}"""),
                Ev("""{"e":"usage","role":"reviewer","prompt":321,"completion":45,"tps":30}"""),
            }, "Final reviewed reply") || _chatHistory.Count != 2 ||
                !reviewer.TurnDetail.Contains("321 prompt") || !reviewer.TurnDetail.Contains("45 generated") || OutputPanel.Children.Count != outputCount ||
                _reply!.Content.Children.OfType<Expander>().Count() != 1)
                return Fail();
            await Settle();
            Snap("15-chat-single");

            // A delegation (orchestrator.py's delegate tool): the target answers, hands a part to the Implementer, whose thinking and
            // text go into sections named after it, then answers again; no FINAL ANSWER label.
            var delegated = new[]
            {
                Ev("""{"e":"chat_stage","role":"reviewer","stage":"answer"}"""),
                Delta("reviewer", "text", "Asking the implementer"),
                Ev("""{"e":"chat_stage","role":"implementer","stage":"delegate","title":"Write the draft"}"""),
                Ev("""{"e":"loading","role":"implementer","path":"smoke/implementer.gguf"}"""),
                Delta("implementer", "thinking", "hmm"),
                Delta("implementer", "text", "Draft body"),
                Ev("""{"e":"done","role":"implementer","final":true}"""),
                Ev("""{"e":"chat_stage","role":"reviewer","stage":"answer"}"""),
                Delta("reviewer", "text", "Reviewed result"),
            };
            string SectionTitle(Expander e) => ((TextBlock)((StackPanel)e.Header).Children[0]).Text;
            if (!await Turn("Build it", delegated, "Reviewed result") || _chatHistory.Count != 4 || OutputPanel.Children.Count != outputCount ||
                _reply!.Content.Children.OfType<Expander>().Select(SectionTitle).SequenceEqual(new[] { "THINKING  ·  IMPLEMENTER", "IMPLEMENTER" }) is false ||
                _reply.Content.Children.OfType<TextBlock>().Any(t => t.Text == "FINAL ANSWER"))
                return Fail();
            await Settle();
            Snap("16-chat-delegate");

            // Chat shows the agents added to it (here every one, as a custom worker added from Chat's "+" is); Code shows the
            // workflow's built-ins and the custom ones.
            var visible = new RoleCard("smoke-visible", "Visible", custom: true);
            _cards.Add(visible);
            _settings.ChatCards.Add(visible.Role);
            await Settle();
            bool Shown(RoleCard card) => WorkerTabs.ItemContainerGenerator.ContainerFromItem(card) is UIElement { Visibility: Visibility.Visible };
            bool AllShown() => _cards.All(card => !card.Hidden && Shown(card));
            SelectWorker(CardFor("planner"));
            RefreshCardVisibility();
            await Settle();
            var firstShown = _cards.Where(Shown).Select(card => (UIElement)WorkerTabs.ItemContainerGenerator.ContainerFromItem(card)).FirstOrDefault();
            if (!AllShown() || firstShown?.TranslatePoint(new Point(), WorkerTabs) is not { X: < 1, Y: < 1 } || !await WorkerRailSmoke(Snap) || !AllShown())
                return Fail();
            ShowView();
            await Settle();
            if (!CodeChat.IsVisible || !AllShown())
                return Fail();
            ShowView(chat: true);
            await Settle();
            if (!AllShown())
                return Fail();

            // The effort chip, in both composers: its menu opens upwards with six levels, a choice is saved and relabels both
            // chips; a target model that does not think greys it out and its requests carry no effort. The worker config (temp
            // data) gets a Planner that thinks and an Implementer that does not, and is put back afterwards.
            var effortConfig = ActiveConfigPath();
            var configBefore = File.ReadAllText(effortConfig);
            var effortRoot = JsonNode.Parse(configBefore)!;
            effortRoot["roles"]!["planner"] = new JsonObject { ["auto_load"] = new JsonObject { ["model_path"] = "org/Fake-Model-GGUF", ["reasoning_budget"] = 8192 } };
            effortRoot["roles"]!["implementer"] = new JsonObject { ["auto_load"] = new JsonObject { ["model_path"] = "unsloth/Qwen2.5-Coder-3B-Instruct-GGUF", ["reasoning_budget"] = 0 } };
            File.WriteAllText(effortConfig, effortRoot.ToJsonString());
            var effortMenu = BuildEffortMenu(CodeEffortChip);
            var levels = effortMenu.Items.OfType<MenuItem>().ToList();
            if (effortMenu.Placement != PlacementMode.Top || levels.Select(i => (string)i.Header).SequenceEqual(new[] { "Default", "Off (no thinking)", "Low", "Medium", "High", "Max" }) is false ||
                levels[0].IsChecked is false || (string)ChatEffortChip.Content != "Effort: Default  ▾")
                return Fail();
            levels[4].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (_settings.ChatEffort != "high" || AppSettings.Load().ChatEffort != "high" || (string)ChatEffortChip.Content != "Effort: High  ▾" ||
                (string)CodeEffortChip.Content != "Effort: High  ▾" || !BuildEffortMenu(ChatEffortChip).Items.OfType<MenuItem>().ElementAt(4).IsChecked)
                return Fail();
            string SentEffort(string role) =>
                (string)JsonNode.Parse(LauncherLogic.BuildChatRequest(role, [new("user", "x")], effort: EffortFor(CardFor(role))))!["effort"]!;
            PickChatWorker("planner"); // a reasoning budget: thinks
            if (!ChatEffortChip.IsEnabled || SentEffort("planner") != "high")
                return Fail();
            await Settle();
            Snap("28-effort-chip");
            PickChatWorker("implementer"); // Qwen2.5-Coder-3B-Instruct, reasoning budget 0: does not think
            if (ChatEffortChip.IsEnabled || (string)ChatEffortChip.ToolTip != "This model doesn't think" || SentEffort("implementer") != "" ||
                !CodeEffortChip.IsEnabled) // Code's target is the lead, the Planner, which thinks
                return Fail();
            _settings.ChatEffort = "";
            PickChatWorker("reviewer");
            RemoveWorker(visible);
            File.WriteAllText(effortConfig, configBefore);

            NewChat_Click(ChatSend, new RoutedEventArgs());
            if (_chatHistory.Count != 0 || ChatPanel.Children.Count != 0)
                return Fail();

            // Stop landing as the reply completes keeps the answer (exit 0 + chat_reply); a real stop keeps the message with what
            // arrived of the reply and a note, and so does a failed turn (with its error).
            StartChatTurn("Race");
            _lines.Enqueue(Ev("""{"e":"chat_reply","role":"reviewer","text":"Raced reply"}"""));
            DrainLines();
            if (FinishChatTurn(0, true, null) || _chatBusy || _chatHistory.Count != 2 || _chatHistory[1] is not { Role: "assistant", Content: "Raced reply", Time: not null } ||
                _reply!.Final != "Raced reply" || _reply.Answer?.Text != "Raced reply")
                return Fail();
            StartChatTurn("Really stopped");
            AppendChatDelta("reviewer", "text", "Half an ans");
            if (!FinishChatTurn(-1, true, null) || _chatBusy || _chatHistory.Count != 4 || _chatHistory[2] is not { Role: "user", Content: "Really stopped" } ||
                _chatHistory[3].Content != "Half an ans\n\n(Stopped before the reply finished.)")
                return Fail();
            StartChatTurn("Will fail");
            if (FinishChatTurn(1, false, "boom") || _chatHistory.Count != 6 || _chatHistory[5].Content != "(The reply failed: boom)" ||
                _store.Get(_chatConv!.Id)?.Messages.Count != 6) // saved, not only shown
                return Fail();
            NewChat_Click(ChatSend, new RoutedEventArgs());

            // Smooth streaming, on a stepped clock: each frame reveals part of what arrived (the answer and the thinking), what
            // shows is always the render of a prefix of it (never half a surrogate pair), a finished block keeps its object while
            // only the tail re-renders, the result matches a full render, code blocks register and unregister as the tail
            // replaces them and still pin, and a stopped turn shows everything at once.
            ShowView(chat: true);
            var realClock = _clockMs;
            var fakeMs = 0.0;
            _clockMs = () => fakeMs;
            try
            {
                StartChatTurn("Stream smoothly");
                AppendChatDelta("reviewer", "thinking", string.Concat(Enumerable.Repeat("Weighing the options. ", 40)));
                AppendChatDelta("reviewer", "text", "First paragraph stays put.\n\n" + string.Join("\n", Enumerable.Repeat("Second paragraph grows word by word.", 30)) + "\n\n");
                var answerView = _reply!.Rendered!;
                var (answer, thinking) = ((MarkdownState)answerView.Tag, (MarkdownState)_markdownDirty.Single(v => v != answerView).Tag);
                static string Structure(System.Windows.Documents.FlowDocument doc) => string.Join(",", doc.Blocks.Select(b => b.GetType().Name)) + "|" + Markdown.PlainText(doc);
                System.Windows.Documents.Block? first = null;
                var kept = 0; // frames that grew the tail and kept the first paragraph's block
                bool Frame()
                {
                    var before = answer.Shown;
                    fakeMs += 16;
                    MarkdownFrame();
                    var text = answer.Raw.Text;
                    if (answer.Shown < before || answer.Shown > text.Length || (answer.Shown < text.Length && char.IsLowSurrogate(text[answer.Shown])) ||
                        Structure(answerView.Document) != Structure(Markdown.ToDocument(text[..answer.Shown], answer.Raw.FontSize)))
                        return false;
                    if (first is not null && answerView.Document.Blocks.FirstBlock != first)
                        return false;
                    if (first is not null && answer.Shown > before)
                        kept++;
                    if (answer.StableBlocks > 0)
                        first ??= answerView.Document.Blocks.FirstBlock;
                    return true;
                }
                bool CatchUp()
                {
                    for (var frames = 0; answer.Shown < answer.Raw.Text.Length; frames++)
                        if (frames > 500 || !Frame())
                            return false;
                    return true;
                }
                if (!Frame() || answer.Shown == 0 || answer.Shown >= answer.Raw.Text.Length || thinking.Shown == 0 || thinking.Shown >= thinking.Raw.Text.Length)
                    return Fail();
                var shownAfterOne = answer.Shown;
                if (!Frame() || answer.Shown <= shownAfterOne || !CatchUp() || kept < 3)
                    return Fail();
                var code = string.Join("\n", Enumerable.Range(1, 12).Select(n => $"print({n})"));
                AppendChatDelta("reviewer", "text", "```python\n" + code + "\n");
                if (!CatchUp())
                    return Fail();
                await Settle(); // the open code block (the tail) loads
                var openCard = Markdown.CodeBlocks(answerView.Document.Blocks).SingleOrDefault();
                if (openCard is null || !_codeBlocks.Contains(openCard))
                    return Fail();
                AppendChatDelta("reviewer", "text", "```\n\n" + string.Join("\n\n", Enumerable.Range(1, 30).Select(n => $"After {n}")) + "\n\n- one\n- two 😀😀😀");
                if (!CatchUp() || Structure(answerView.Document) != Structure(Markdown.ToDocument(answer.Raw.Text, answer.Raw.FontSize)))
                    return Fail();
                await Settle();
                var streamedCard = Markdown.CodeBlocks(answerView.Document.Blocks).Single();
                string? streamedCopy = null;
                _copyCodeHook = c => streamedCopy = c;
                CopyButtonOf(streamedCard).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                _copyCodeHook = null;
                if (streamedCard == openCard || _codeBlocks.Contains(openCard) || !_codeBlocks.Contains(streamedCard) || streamedCopy != code)
                    return Fail();
                ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset + streamedCard.TransformToAncestor(ChatScroll).Transform(new Point()).Y + 50);
                await Settle();
                if (Math.Abs(((TranslateTransform)CopyButtonOf(streamedCard).RenderTransform).Y - 50) > 2)
                    return Fail();
                AppendChatDelta("reviewer", "text", "\n\n" + string.Concat(Enumerable.Repeat("Cut off by Stop. ", 40)));
                if (!Frame() || answer.Shown >= answer.Raw.Text.Length || !FinishChatTurn(-1, true, null) || answer.Shown != answer.Raw.Text.Length ||
                    thinking.Shown != thinking.Raw.Text.Length || _markdownDirty.Count != 0 || _framing ||
                    Structure(answerView.Document) != Structure(Markdown.ToDocument(answer.Raw.Text, answer.Raw.FontSize)))
                    return Fail();
            }
            finally
            {
                _clockMs = realClock;
            }
            NewChat_Click(ChatSend, new RoutedEventArgs());

            // Closing mid-reply: the turn's request file and created temp folder are deleted, a real project is not touched.
            var strayFile = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-chat-{Guid.NewGuid():N}.json");
            var strayDir = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-chat-{Guid.NewGuid():N}");
            File.WriteAllText(strayFile, "{}");
            Directory.CreateDirectory(strayDir);
            File.WriteAllText(Path.Combine(strayDir, "config.json"), "{}");
            (_chatRequestFile, _chatTempFolder) = (strayFile, strayDir);
            StartChatTurn("Busy");
            OnClosing(this, new CancelEventArgs()); // the real close path: no chat process here, only the files
            if (File.Exists(strayFile) || Directory.Exists(strayDir) || _chatRequestFile is not null || _chatTempFolder is not null)
                return Fail();
            CleanupChatTemp(); // idempotent: SendChat's own cleanup runs after this
            FinishChatTurn(-1, true, null);
            var realProject = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-smoke-project-{Guid.NewGuid():N}");
            Directory.CreateDirectory(realProject);
            try
            {
                var projectConfigFile = Path.Combine(realProject, "config.json");
                File.WriteAllText(projectConfigFile, "{\"keep\":1}");
                var kept = LauncherLogic.PrepareChatFolder(realProject, _workflowRoot!, CurrentWorkers(), out _);
                var made = LauncherLogic.PrepareChatFolder("", _workflowRoot!, CurrentWorkers(), out _);
                var madeExists = made.IsTemp && Directory.Exists(made.Path);
                if (made.IsTemp) Directory.Delete(made.Path, true);
                if (kept.IsTemp || kept.Path != realProject || File.ReadAllText(projectConfigFile) != "{\"keep\":1}" || !made.IsTemp || !madeExists)
                    return Fail();
            }
            finally { Directory.Delete(realProject, true); }

            // Code view: a turn with a tool call lands in the Code conversation (not Chat); code_reply is the answer; both sub-views toggle.
            ShowView();
            await Settle();
            if (!CodeChat.IsVisible || NavCode.IsChecked != true)
                return Fail();
            var chatCount = ChatPanel.Children.Count;
            // The enter arrow shows only once the box has text (and nothing is running).
            CodeInput.Text = "";
            var arrowHidden = CodeSend.Visibility == Visibility.Collapsed;
            CodeInput.Text = "Fix it";
            if (!arrowHidden || CodeSend.Visibility != Visibility.Visible || CodeStop.Visibility != Visibility.Collapsed)
                return Fail();
            CodeInput.Text = "";
            var hadCodeConv = _codeConv is not null;
            if (!EnsureCodeConversation(SelectedProjectPath())) // SendCode opens it before a turn
                return Fail();
            StartTurn(_code, "Fix it", "planner");
            if (!_chatBusy || ChatSend.IsEnabled || CodeSend.IsEnabled || CodeStop.Visibility != Visibility.Visible || ChatStop.Visibility != Visibility.Visible ||
                CodeSend.Visibility == Visibility.Visible || ChatSend.Visibility == Visibility.Visible)
                return Fail();
            _lines.Enqueue(Delta("implementer", "text", "Looking"));
            _lines.Enqueue(Ev("""{"e":"tool","role":"implementer","name":"read_file","args":"{\"path\":\"src/a.js\"}","result":"OK"}"""));
            _lines.Enqueue(Ev("""{"e":"compacted","role":"implementer","before":30100,"after":8200}"""));
            _lines.Enqueue(Delta("implementer", "text", "Now fixing"));
            DrainLines();
            // Saved before the reply: a crash now keeps the message, with a note in place of the reply.
            if (_codeConv?.Messages is not [{ Role: "user", Content: "Fix it" }, { Role: "assistant", Content: InterruptedReply }])
                return Fail();
            _lines.Enqueue(Ev("""{"e":"code_reply","role":"implementer","text":"Fixed"}"""));
            DrainLines();
            var codeReply = _reply!;
            await Settle();
            Snap("18-code-turn");
            FinishChatTurn(0, false, null);
            var toolRow = codeReply.Content.Children.OfType<Expander>().SingleOrDefault();
            if (codeReply.Final != "Fixed" || codeReply.Answer?.Text != "Fixed" || _code.History.Count != 2 || ChatPanel.Children.Count != chatCount ||
                toolRow is null || ((TextBlock)((StackPanel)toolRow.Header).Children[1]).Text != "src/a.js" || _chatBusy)
                return Fail();
            // In the order it happened: the text before the tool call stays above it, the reply after it below.
            var parts = codeReply.Content.Children.Cast<UIElement>().ToList();
            var early = parts.OfType<TextBox>().FirstOrDefault(t => t.Text == "Looking");
            var compactedNote = parts.OfType<TextBlock>().FirstOrDefault(t => t.Text == "Context compacted · 30.1k → 8.2k tokens"); // a muted line where it happened
            if (early is null || parts.IndexOf(early) > parts.IndexOf(toolRow) || parts.IndexOf(toolRow) > parts.IndexOf(codeReply.Answer!) ||
                compactedNote is null || parts.IndexOf(compactedNote) < parts.IndexOf(toolRow) || parts.IndexOf(compactedNote) > parts.IndexOf(codeReply.Answer!) ||
                _codeConv?.Messages is not [_, { Content: "Fixed" }])
                return Fail();
            // A Code chat turn sends the stored summary in place of the messages it covers; write_project reads the whole history.
            (_code.Summary, _code.SummaryCovers) = ("CS", 2);
            var codeSummed = JsonNode.Parse(CodeRequest("chat", "implementer", "Next", @"Y:\app"))!;
            var projectFull = JsonNode.Parse(CodeRequest("write_project", "implementer", null, @"Y:\app"))!;
            (_code.Summary, _code.SummaryCovers) = (null, 0);
            if ((string)codeSummed["summary"]! != "CS" || codeSummed["messages"]!.AsArray().Count != 1 || (string)projectFull["summary"]! != "" ||
                projectFull["messages"]!.AsArray().Count != 2)
                return Fail();
            if (!hadCodeConv && _codeConv is { } madeConv) // leave the store as the checks below expect it
            {
                StoreDo(() => _store.Delete(madeConv.Id));
                _codeConv = null;
                _settings.LastConversation = "";
                RefreshSidebarList();
            }
            // Code always talks to the lead: the chip names the Planner's model, a pick does not move it, no request carries a mode,
            // and the chip's dropdown is Options… alone.
            bool NoMode(string action) => JsonNode.Parse(CodeRequest(action, "planner", action == "chat" ? "Fix it" : null, @"Y:\app"))!["mode"] is null;
            PickWorker(_code, "implementer");
            RefreshChatTarget();
            if (CodeTarget()?.Role != "planner" || (string)CodeTargetChip.Content != "Lead  ▾" ||
                !NoMode("chat") || !NoMode("write_project") || ListedWorkers(_code).Count != 0 || CodePlus.Visibility != Visibility.Visible)
                return Fail();
            // New conversation (the "Main" preset applies), extra folders through the Add / × buttons, save, resume, Start new.
            var codeProject = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-smoke-{Guid.NewGuid():N}");
            var codeExtra = Path.Combine(codeProject, "extra");
            var codePrevious = _projectPath;
            Directory.CreateDirectory(codeExtra);
            try
            {
                string? pick = codeProject, answer = "new";
                (_codeFolderPick, _codeChoice) = (() => pick, _ => answer);
                SavePreset("main");
                NewCodeConversation();
                DeletePreset(_settings.Presets.Single(p => p.Name == "main"));
                var first = _codeConv;
                if (first is null || _store.Get(first.Id) != first || _settings.LastConversation != first.Id || SelectedProjectPath() != codeProject ||
                    _code.History.Count != 0 || CodeExtraFolders.Count != 0 || CodeChat.Visibility != Visibility.Visible ||
                    AddCodeFolderButton.Visibility != Visibility.Visible || !_log.ToString().Contains("Preset applied: main"))
                    return Fail();
                void AddFolder() => AddCodeFolderButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                (pick, answer) = (codeExtra, "write");
                AddFolder();
                AddFolder(); // a duplicate
                pick = codeProject + "\\";
                AddFolder(); // the project itself
                if (CodeExtraFolders.Count != 1 || CodeExtraFolders[0] != new LauncherLogic.CodeFolder(codeExtra, "write") || CodeFoldersList.Children.Count != 1 ||
                    CodeFoldersEmpty.Visibility == Visibility.Visible)
                    return Fail();
                if (_store.Get(first.Id) is not { Folders.Count: 1 }) // an extra-folder change is saved
                    return Fail();
                _code.History.Add(new LauncherLogic.ChatMessage("user", "Hi"));
                _code.History.Add(new LauncherLogic.ChatMessage("assistant", "Hello"));
                SaveCodeConversation();
                // A second conversation in the same folder; the first stays stored. Left empty, it is deleted on leaving.
                NewCodeConversation();
                var second = _codeConv;
                if (second is null || second.Id == first.Id || _code.History.Count != 0 || CodePanel.Children.Count != 0 || CodeEmpty.Visibility != Visibility.Visible)
                    return Fail();
                await Settle();
                Snap("18b-code-empty-no-projectmd");
                // Reopening shows the history, extra folders and folder; nothing is written.
                var firstPath = Path.Combine(AppSettings.DataRoot, "conversations", first.Id + ".json");
                var savedText = File.ReadAllText(firstPath);
                ProjectPath = codeExtra; // another folder closes the open conversation
                OpenCodeConversation(first.Id);
                await Settle();
                Snap("19-code-resumed");
                if (_codeConv != first || _store.Get(second.Id) is not null || SelectedProjectPath() != codeProject || _code.History.Count != 2 ||
                    CodeExtraFolders.Count != 1 || CodePanel.Children.Count != 2 || CodePanel.Children[0] is not StackPanel { Children: [Border { Child: TextBox { Text: "Hi" } }, ..] } ||
                    CodePanel.Children[1] is not StackPanel { Children: [_, StackPanel content, ..] } || !content.Children.OfType<TextBox>().Any(t => t.Text == "Hello") ||
                    CodeEmpty.Visibility == Visibility.Visible || File.ReadAllText(firstPath) != savedText)
                    return Fail();
                // Opening is refused while a run is active.
                _process = new Process();
                var third = _store.Create(codeProject)!;
                OpenCodeConversation(third.Id);
                var lastToast = ToastStack.Children.OfType<UIElement>().LastOrDefault();
                NewCodeConversation(); // refused with the same toast
                _process = null;
                RefreshChatInput();
                if (_codeConv != first || ToastStack.Children.OfType<UIElement>().LastOrDefault() == lastToast)
                    return Fail();
                // A chat with no usable folder is refused, and the open one stays.
                var noFolder = _store.Create("")!;
                noFolder.Folders = [new(codeExtra, "read")];
                _store.Save(noFolder);
                OpenCodeConversation(noFolder.Id);
                _store.Delete(noFolder.Id);
                if (_codeConv != first || CodeExtraFolders.Count != 1)
                    return Fail();
                ((Button)((DockPanel)CodeFoldersList.Children[0]).Children[0]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (CodeExtraFolders.Count != 0 || CodeFoldersList.Children.Count != 0 || _store.Get(first.Id) is not { Folders.Count: 0, Messages.Count: 2 })
                    return Fail();
                // Saved extra folders that are gone, the project, or repeated are dropped when it opens.
                third.Messages = [new("user", "Hi"), new("assistant", "Hello")];
                third.Folders = [new(codeExtra, "write"), new(Path.Combine(codeProject, "gone"), "read"), new(codeProject, "write"), new(codeExtra + "\\", "read")];
                _store.Save(third);
                OpenCodeConversation(third.Id);
                if (_codeConv != third || _store.Get(first.Id) is null || _code.History.Count != 2 || CodePanel.Children.Count != 2 || CodeExtraFolders.Count != 1 ||
                    CodeExtraFolders[0] != new LauncherLogic.CodeFolder(codeExtra, "write"))
                    return Fail();
                // A folder without PROJECT.md: the empty state offers Create with a model / Add / template, Start project is off
                // (and says why), and neither Start nor the worker's start_project starts anything or asks a model to write the file.
                // A conversation that starts with the Create message carries the bundled init-projMD skill on every turn.
                string EmptyState() => $"{CodeEmptyTitle.Text}|{CodeEmptyChoose.Visibility}|{CodeEmptyInit.Visibility}|{CodeEmptyAddMd.Visibility}|{CodeEmptyTemplate.Visibility}|{CodeStartButton.IsEnabled}";
                string? SkillsOf(string? text) => JsonNode.Parse(CodeRequest("chat", "planner", text, codeProject))!["skills"]!.AsArray().FirstOrDefault()?.ToString();
                var latestOnly = SkillsOf(InitProjectMessage); // this conversation starts with "Hi": the first message decides
                var keptHistory = _code.History.ToList();
                _code.History[0] = new("user", InitProjectMessage);
                var initLater = SkillsOf("Hi again");
                _code.History.Clear();
                _code.History.AddRange(keptHistory);
                if (EmptyState() != "Add a PROJECT.md to start the project|Collapsed|Visible|Visible|Visible|False" || !ToolTipService.GetShowOnDisabled(CodeStartButton) ||
                    CodeStartButton.ToolTip as string is not { } whyOff || !whyOff.Contains("no PROJECT.md") ||
                    latestOnly is not null || initLater != Path.Combine(_workflowRoot!, "skills", InitProjectSkill))
                    return Fail();
                await Settle();
                Snap("20a-code-no-projectmd");
                SetStatus("Ready", "#667085");
                StartCodeProject();
                if (_chatBusy || _chatProcess is not null || _process is not null || StatusText.Text != "Add a PROJECT.md first" || File.Exists(Path.Combine(codeProject, "PROJECT.md")))
                    return Fail();
                SetStatus("Ready", "#667085");
                _codeStartRequested = true;
                CodeTurnFinished();
                if (_codeStartRequested || _chatBusy || _chatProcess is not null || _process is not null || StatusText.Text != "Add a PROJECT.md first")
                    return Fail();
                // Project menu: its items; Use a PROJECT.md… copies a file in, then backs up the one it replaces.
                string MenuItems() => string.Join("|", BuildProjectMenu().Items.OfType<MenuItem>().Select(i => i.Header + (i.IsEnabled ? "" : " (off)") + (i.IsChecked ? " (on)" : "")));
                const string items = "Use a PROJECT.md…|Edit PROJECT.md{0}|Folders…|Stop after the first finished item{1}|Open run logs|Copy run log";
                var onceMark = _settings.Once ? " (on)" : "";
                if (!CodeProjectButton.IsEnabled || MenuItems() != string.Format(items, " (off)", onceMark))
                    return Fail();
                var projectMenu = BuildProjectMenu();
                projectMenu.IsOpen = true;
                await Settle();
                Snap("20-project-menu", projectMenu);
                projectMenu.IsOpen = false;
                var mdSource = Path.Combine(codeExtra, "my-project.md");
                var mdTarget = Path.Combine(codeProject, "PROJECT.md");
                File.WriteAllText(mdSource, "# Mine\n");
                _projectMdPick = () => mdSource;
                UseProjectMd();
                const string withMd = "Start a conversation|Collapsed|Collapsed|Collapsed|Collapsed|True";
                if (File.ReadAllText(mdTarget) != "# Mine\n" || MenuItems() != string.Format(items, "", onceMark) || EmptyState() != withMd ||
                    CodeStartButton.ToolTip as string != "Start or resume the project run in this folder (Ctrl+Enter)")
                    return Fail();
                File.WriteAllText(mdSource, "# Mine v2\n");
                answer = "replace";
                UseProjectMd();
                if (File.ReadAllText(mdTarget) != "# Mine v2\n" || File.ReadAllText(Path.Combine(codeProject, "PROJECT.md.bak")) != "# Mine\n")
                    return Fail();
                // Removed outside the app: the next refresh (window activation) notices; Start from template copies the
                // template in and opens it.
                File.Delete(mdTarget);
                RefreshCodeFolder();
                if (EmptyState() != "Add a PROJECT.md to start the project|Collapsed|Visible|Visible|Visible|False")
                    return Fail();
                string? opened = null;
                _openProjectMd = file => opened = file;
                CodeEmptyTemplate.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (File.ReadAllText(mdTarget) != File.ReadAllText(Path.Combine(_workflowRoot!, "PROJECT.md")) || opened != mdTarget || EmptyState() != withMd)
                    return Fail();
                // The Agent Orchestrator folder: only Read only as an extra folder, and never a Code project (no process starts).
                (pick, answer) = (_workflowRoot!, "write");
                AddFolder();
                if (CodeExtraFolders.Count != 1)
                    return Fail();
                ProjectPath = _workflowRoot!;
                CodeInput.Text = "Edit the templates";
                var refused = SendCode();
                if (!refused.IsCompleted || refused.Result || _chatBusy || _chatProcess is not null || CodeInput.Text.Length == 0 || StatusText.Text != "Choose a project folder")
                    return Fail();
                CodeInput.Clear();
                // Nor does adding an extra folder start a chat there.
                (pick, answer) = (codeExtra, "read");
                SetStatus("Ready", "#667085");
                AddFolder();
                if (_codeConv is not null || CodeExtraFolders.Count != 0 || StatusText.Text != "Choose a project folder")
                    return Fail();
                // No folder: only Choose a folder…, and Start project is off.
                var keptFolder = _projectPath;
                ProjectPath = "";
                var noFolderState = EmptyState();
                ProjectPath = keptFolder;
                if (noFolderState != "Choose a folder to start|Visible|Collapsed|Collapsed|Collapsed|False")
                    return Fail();

                // A mirror write that fails (a problem or an exception) is only a warning, shown as a toast: the save still
                // counts as done. The first target failing is the caller's problem, and nothing is mirrored.
                var beforeMirror = ToastStack.Children.OfType<UIElement>().LastOrDefault();
                var mirrored = new List<string>();
                if (WriteConfigs(["project", "worker"], path => path == "worker" ? throw new IOException("mirror locked") : null) is not null ||
                    WriteConfigs(["project", "worker"], path => path == "worker" ? "mirror refused" : null) is not null ||
                    WriteConfigs(["project", "worker"], path => { mirrored.Add(path); return path == "project" ? "project refused" : null; }) != "project refused" ||
                    mirrored is not ["project"] || !_log.ToString().Contains("the launcher's worker config could not be updated: mirror locked") ||
                    !_log.ToString().Contains("the launcher's worker config could not be updated: mirror refused") ||
                    ToastStack.Children.OfType<UIElement>().LastOrDefault() == beforeMirror)
                    return Fail();

                // Sidebar list: rows for the stored chats; rename, reorder, projects, collapse, archive and delete through the
                // real menu items; opening a chat by its row.
                CloseCodeConversation();
                var stored = _store.View();
                foreach (var entry in stored.Groups.SelectMany(g => g.Items).Concat(stored.Ungrouped).Concat(stored.Archived))
                    _store.Delete(entry.Id);
                var chatA = _store.Create(codeProject)!;
                chatA.Messages = [new("user", "Alpha question"), new("assistant", "Alpha answer")];
                var chatB = _store.Create(codeProject)!;
                chatB.Messages = [new("user", "Beta question"), new("assistant", "Beta answer")];
                _store.Save(chatA);
                _store.Save(chatB);
                RefreshSidebarList();
                FrameworkElement Main(UIElement row) => row is Grid { Children: [FrameworkElement main, ..] } ? main : (FrameworkElement)row;
                string Rows() => string.Join(",", SidebarList.Children.Cast<UIElement>().Select(r => Main(r).Tag is string tag ? tag == chatA.Id ? "A" : tag == chatB.Id ? "B" : tag : "?"));
                void ClickRow(string tag) => SidebarList.Children.Cast<UIElement>().Select(Main).First(m => (string)m.Tag == tag).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                MenuItem SidebarItem(string id, string header) => _sidebarMenus[id]().Items.OfType<MenuItem>().Single(i => (string)i.Header == header);
                void ClickItem(string id, string header) => SidebarItem(id, header).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                // a section header's + and folder buttons ("+new:code", "+project:code")
                void ClickHeader(string tag) => SidebarList.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => (string?)b.Tag == tag)
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                // New chat and Work in a folder, then the CHAT section (no chats yet: its empty line) over the CODE section
                const string top = "+New chat,+Work in a folder,SECTION:CHAT,?,SECTION:CODE";
                await Settle();
                Snap("21-sidebar");
                if (Rows() != top + ",RECENTS:code,B,A" || SidebarList.Children.OfType<Grid>().Any(g => g.Children[0] is ToggleButton { IsChecked: true }))
                    return Fail();
                answer = "Renamed B";
                ClickItem(chatB.Id, "Rename…");
                if (_store.Get(chatB.Id)?.Title != "Renamed B" || !SidebarList.Children.OfType<Grid>().Any(g => g.Children[0] is ToggleButton { Content: DockPanel { Children: [TextBlock { Text: "" }, TextBlock { Text: "Renamed B" }] } })) // the Code braces, then the title
                    return Fail();
                ClickItem(chatB.Id, "Move down");
                if (Rows() != top + ",RECENTS:code,A,B" || SidebarItem(chatB.Id, "Move down").IsEnabled)
                    return Fail();
                ClickItem(chatB.Id, "Move up");
                if (Rows() != top + ",RECENTS:code,B,A")
                    return Fail();
                answer = "Smoke project";
                ClickHeader("+project:code");
                ClickItem(chatA.Id, "Move to Smoke project");
                if (Rows() != top + ",▾Smoke project,A,RECENTS:code,B")
                    return Fail();
                ClickRow("▾Smoke project");
                if (Rows() != top + ",▸Smoke project,RECENTS:code,B" || _store.View().Groups is not [{ Collapsed: true }])
                    return Fail();
                // An empty chat moved into a project, or archived, is kept on leaving it.
                var emptyGrouped = _store.Create(codeProject)!;
                var emptyArchived = _store.Create(codeProject)!;
                _store.MoveToGroup(emptyGrouped.Id, _store.View().Groups[0].Id);
                _store.Archive(emptyArchived.Id, true);
                foreach (var empty in new[] { emptyGrouped, emptyArchived })
                {
                    OpenCodeConversation(empty.Id);
                    CloseCodeConversation();
                    if (_store.Get(empty.Id) is null)
                        return Fail();
                    _store.Delete(empty.Id);
                }
                RefreshSidebarList();
                ClickItem(chatB.Id, "Archive");
                if (Rows() != top + ",▸Smoke project,RECENTS:code,▸ARCHIVED")
                    return Fail();
                ClickRow("▸ARCHIVED");
                if (Rows() != top + ",▸Smoke project,RECENTS:code,▾ARCHIVED,B")
                    return Fail();
                _process = new Process(); // a run in another conversation leaves Delete on
                var deleteOff = SidebarItem(chatB.Id, "Delete…").IsEnabled;
                _process = null;
                answer = "yes";
                ClickItem(chatB.Id, "Delete…");
                if (!deleteOff || Rows() != top + ",▸Smoke project,RECENTS:code" || _store.Get(chatB.Id) is not null ||
                    File.Exists(Path.Combine(AppSettings.DataRoot, "conversations", chatB.Id + ".json")))
                    return Fail();
                ClickRow("▸Smoke project");
                ClickRow(chatA.Id);
                await Settle();
                Snap("22-sidebar-open");
                if (_codeConv != chatA || _code.History.Count != 2 || CodePanel.Children.Count != 2 || Rows() != top + ",▾Smoke project,A,RECENTS:code" ||
                    SidebarList.Children.Cast<UIElement>().Select(Main).Single(m => m is ToggleButton { IsChecked: true }).Tag as string != chatA.Id)
                    return Fail();
                // A project's "+" opens a new Code chat in that project, in its chats' folder, without asking for one.
                {
                    var smokeGroup = _store.View().Groups[0].Id;
                    var (asked, keptPick) = (false, _codeFolderPick);
                    _codeFolderPick = () => { asked = true; return null; };
                    SidebarList.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => (string?)b.Tag == "+in:" + smokeGroup)
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    _codeFolderPick = keptPick;
                    var added = _codeConv;
                    if (asked || added is null || added == chatA || !SameFolder(added.Folder, codeProject) ||
                        _store.View().Groups[0].Items.FirstOrDefault()?.Id != added.Id)
                        return Fail();
                    CloseCodeConversation();
                    _store.Delete(added.Id);
                    ClickRow(chatA.Id);
                    if (_codeConv != chatA)
                        return Fail();
                    // A project of Code chats stays out of the CHAT section; an empty project shows only in the section it was made in.
                    var chatOnly = _store.CreateGroup("Chat only", "chat")!;
                    RefreshSidebarList();
                    var sections = Rows().Split("SECTION:CODE");
                    var (inChat, inCode) = (sections[0], sections[^1]);
                    _store.DeleteGroup(chatOnly);
                    RefreshSidebarList();
                    if (sections.Length != 2 || inChat.Contains("Smoke project") || !inChat.Contains("Chat only") || !inCode.Contains("Smoke project") || inCode.Contains("Chat only"))
                    {
                        Console.Error.WriteLine($"ui-smoke: projects by view: chat [{inChat}] code [{inCode}]");
                        return Fail();
                    }
                }

                // Drag and drop, through the drop handlers the rows register: a chat dropped on a project header goes to its
                // top, on a chat takes its place (from either direction), on RECENTS leaves its project; a project dropped on
                // another takes its place. A drop that does nothing there is refused.
                var chatC = _store.Create(codeProject)!;
                chatC.Messages = [new("user", "Gamma question")];
                _store.Save(chatC);
                answer = "Second project";
                ClickHeader("+project:code");
                var (secondGroup, smoke) = (_store.View().Groups[0].Id, _store.View().Groups[1].Id);
                bool Drop(string target, string payload)
                {
                    RefreshSidebarList();
                    var done = _sidebarDrops[target](payload) is { } change && change();
                    RefreshSidebarList();
                    return done;
                }
                string Items(int group) => string.Join(",", _store.View().Groups[group].Items.Select(e => e.Id == chatA.Id ? "A" : e.Id == chatC.Id ? "C" : "?"));
                if (!Drop(secondGroup, "chat:" + chatC.Id) || Items(0) != "C" || !Drop(smoke, "project:" + secondGroup) || _store.View().Groups[0].Id != smoke ||
                    !Drop(chatA.Id, "chat:" + chatC.Id) || Items(0) != "C,A" || !Drop(chatC.Id, "chat:" + chatA.Id) || Items(0) != "A,C" ||
                    !Drop(chatA.Id, "chat:" + chatC.Id) || Items(0) != "C,A" || !Drop("RECENTS:code", "chat:" + chatC.Id) || Items(0) != "A" ||
                    _store.View().Ungrouped is not [{ Id: var recent }] || recent != chatC.Id)
                    return Fail();
                if (_sidebarDrops[chatA.Id]("chat:" + chatA.Id) is not null || _sidebarDrops[smoke]("project:" + smoke) is not null || _sidebarDrops[chatA.Id]("project:" + secondGroup) is not null ||
                    _sidebarDrops["RECENTS:code"]("project:" + secondGroup) is not null || _sidebarDrops[chatA.Id]("junk") is not null || _sidebarDrops[chatA.Id](null) is not null)
                    return Fail();
                // Deleting the open Code conversation opens a fresh chat, not the folder's empty Code view.
                ClickRow(chatC.Id);
                answer = "yes";
                ClickItem(chatC.Id, "Delete…");
                if (_store.Get(chatC.Id) is not null || _codeConv is not null || NavChat.IsChecked != true || _chatConv is not null || ChatPanel.Children.Count != 0)
                    return Fail();
                ClickRow(chatA.Id);
                if (_codeConv != chatA || NavCode.IsChecked != true)
                    return Fail();

                // Chat-view chats: a completed reply saves one (no folder; later replies update it) that lists with the Code
                // chats and opens in the Chat view; New chat leaves it saved; deleting it empties the Chat view.
                ClearChatConversation();
                StartChatTurn("Saved chat question");
                SetChatFinal("Saved chat answer");
                var markdownReply = _reply!;
                const string sample = "# Peonies\n\nPlant in **full sun** and *well-drained* soil, see [guide](https://example.org/p).\n\n" +
                    "- Water `weekly`\n  - deeper in summer\n- Mulch\n\n1. Dig\n2. Plant\n\n| Month | Task |\n|---|---|\n| May | Bloom |\n\n> Note\n\n```\ncode **raw**\n```\n\n---\nEnd";
                SetChatFinal(sample);
                await Settle();
                ShowView(chat: true);
                await Settle();
                Snap("25-markdown-reply");
                var blocks = markdownReply.Rendered?.Document.Blocks.ToList() ?? [];
                var lists = blocks.OfType<System.Windows.Documents.List>().ToList();
                var mdText = markdownReply.Rendered is { } rendered ? Markdown.PlainText(rendered.Document) : "";
                if (markdownReply.Answer?.Visibility != Visibility.Collapsed || markdownReply.Answer.Text != sample ||
                    blocks.FirstOrDefault() is not System.Windows.Documents.Paragraph { FontWeight: var headingWeight, FontFamily: var headingFont } || headingWeight != Markdown.StrongWeight || headingFont != AppFonts.Serif || lists.Count != 2 || lists[0].MarkerStyle != TextMarkerStyle.Disc ||
                    lists[1].MarkerStyle != TextMarkerStyle.Decimal || lists[0].ListItems.First().Blocks.OfType<System.Windows.Documents.List>().Count() != 1 ||
                    blocks.OfType<System.Windows.Documents.Table>().Single().RowGroups[0].Rows.Count != 2 || blocks.OfType<System.Windows.Documents.Section>().Count() != 1 ||
                    Markdown.CodeBlocks(blocks).SingleOrDefault() is not { Child: Grid { Children: [TextBlock codeText, Button { Tag: "code **raw**" }] } } ||
                    string.Concat(codeText.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text)) != "code **raw**" ||
                    mdText.Contains("# Peonies") || mdText.Contains("**full sun**") ||
                    blocks.OfType<System.Windows.Documents.Paragraph>().SelectMany(p => p.Inlines).OfType<System.Windows.Documents.Hyperlink>().Single().NavigateUri.AbsoluteUri != "https://example.org/p")
                    return Fail();
                // Code is coloured (keywords, calls, strings, numbers, comments; # is a comment only where the language uses it)
                // and keeps every character; inline code keeps the text colour.
                const string snippet = "if (x) return Foo(\"s\", 42); // done\n#include <a>";
                var pieces = Markdown.Highlight(snippet, "c");
                var inlineCode = blocks.OfType<System.Windows.Documents.List>().First().ListItems.First().Blocks.OfType<System.Windows.Documents.Paragraph>().First()
                    .Inlines.OfType<System.Windows.Documents.Run>().Single(run => run.Text == "weekly");
                if (string.Concat(pieces.Select(x => x.Text)) != snippet || pieces.Count(x => x.Color is not null) != 6 ||
                    pieces.Any(x => x.Text.StartsWith("#include") && x.Color is not null) || Markdown.Highlight("# note", "python").Single().Color is null ||
                    inlineCode.ReadLocalValue(System.Windows.Documents.TextElement.ForegroundProperty) != DependencyProperty.UnsetValue)
                    return Fail();
                // A code block is a rounded card with a copy button that copies exactly its code and shows a check; the
                // button stays at the top of the block's visible part while the conversation scrolls, capped at its end.
                var longCode = string.Join("\n", Enumerable.Range(1, 80).Select(n => $"    total += Step({n}); // line {n}"));
                SetChatFinal("Before\n\n```python\n" + longCode + "\n```\n\n" + string.Join("\n\n", Enumerable.Range(1, 40).Select(n => $"After {n}")));
                await Settle();
                var codeCard = Markdown.CodeBlocks(markdownReply.Rendered!.Document.Blocks).Single();
                var copyCode = (Button)((Grid)codeCard.Child).Children[1];
                string? copied = null;
                _copyCodeHook = code => copied = code;
                copyCode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                _copyCodeHook = null;
                if (codeCard.Parent is not System.Windows.Documents.BlockUIContainer || codeCard.CornerRadius.TopLeft <= 0 || copied != longCode ||
                    ((TextBlock)copyCode.Content).Text != Markdown.CopiedGlyph || !_codeBlocks.Contains(codeCard))
                    return Fail();
                double PinOf(Border card) => ((TranslateTransform)((Grid)card.Child).Children[1].RenderTransform).Y;
                double CardTop(Border card) => card.TransformToAncestor(ChatScroll).Transform(new Point()).Y;
                var pinRoom = codeCard.ActualHeight - codeCard.Padding.Top - codeCard.Padding.Bottom - copyCode.ActualHeight;
                if (PinOf(codeCard) != 0 && CardTop(codeCard) >= 0)
                    return Fail();
                ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset + CardTop(codeCard) + 300); // the block's top 300 px above the viewport
                await Settle();
                if (Math.Abs(PinOf(codeCard) - 300) > 2 || PinOf(codeCard) > pinRoom)
                    return Fail();
                Snap("29-code-block");
                Render(markdownReply.Rendered); // a re-render (as while streaming) keeps the button pinned, and drops the old card
                await Settle();
                var rerendered = Markdown.CodeBlocks(markdownReply.Rendered.Document.Blocks).Single();
                if (Math.Abs(PinOf(rerendered) - 300) > 2 || _codeBlocks.Contains(codeCard) || !_codeBlocks.Contains(rerendered))
                    return Fail();
                var pinTheme = _settings.Theme;
                _settings.Theme = "Parchment";
                ApplyTheme();
                await Settle();
                Snap("29-code-block-parchment");
                _settings.Theme = pinTheme;
                ApplyTheme();
                ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset + CardTop(rerendered) + rerendered.ActualHeight - 5); // past the block's end
                await Settle();
                if (Math.Abs(PinOf(rerendered) - pinRoom) > 0.5)
                    return Fail();
                SetChatFinal("Saved chat answer"); // the saved conversation below expects the original reply
                // The context ring: empty before the first context event, then the turn's share of the window.
                if (ChatContextRing.Tag is not null || ChatContextRing.ToolTip as string != "Context: no reply yet in this conversation")
                    return Fail();
                _lines.Enqueue(Ev("""{"e":"context","role":"reviewer","n_ctx":32000,"prompt_tokens":12000,"completion_tokens":300,"parts":{"system":1500,"tools":2000,"skills":500,"messages":6000,"tool_results":2000}}"""));
                // The first turn's model-written title names the new chat; a later turn's title never replaces it.
                _lines.Enqueue(Ev("""{"e":"chat_title","text":"Saved chat title"}"""));
                DrainLines();
                FinishChatTurn(0, false, null);
                var savedChat = _chatConv;
                if (ChatContextRing.Tag is not double ringFill || Math.Abs(ringFill - 12300 / 32000.0) > 1e-9 || ChatContextRing.ToolTip as string != "12.3k / 32k tokens (38%)")
                    return Fail();
                ContextRing_Click(ChatContextRing, new RoutedEventArgs());
                await Settle();
                var contextRows = ((StackPanel)((Border)_contextPopup!.Child).Child).Children.OfType<DockPanel>().Where(d => d.Tag is long).ToList();
                if (!_contextPopup.IsOpen || contextRows.Count != 7 || contextRows.Sum(d => (long)d.Tag) != 32000
                    || contextRows.Take(5).Sum(d => (long)d.Tag) != 12000) // the prompt rows add up to prompt_tokens
                    return Fail();
                Snap("30-context-ring");
                Snap("30-context-popup", (FrameworkElement)_contextPopup.Child);
                var ringTheme = _settings.Theme;
                _settings.Theme = "Parchment";
                ApplyTheme();
                await Settle();
                Snap("30-context-ring-parchment");
                Snap("30-context-popup-parchment", (FrameworkElement)_contextPopup.Child);
                _settings.Theme = ringTheme;
                ApplyTheme();
                _contextPopup.IsOpen = false;
                // A context length saved for the measured worker shows at once (0 = auto waits); another worker's leaves it.
                ContextLengthSaved("planner", "64000");
                ContextLengthSaved("reviewer", "0");
                var otherKept = ChatContextRing.ToolTip as string == "12.3k / 32k tokens (38%)";
                ContextLengthSaved("reviewer", "64000");
                var resized = ChatContextRing.ToolTip as string == "12.3k / 64k tokens (19%)";
                ContextLengthSaved("reviewer", "32000");
                if (!otherKept || !resized)
                    return Fail();
                // The worker column's Context section: closed (with the summary) until opened; open shows the same rows and is saved.
                if (ContextSectionBody.Visibility == Visibility.Visible || ContextSummary.Text != "38%  ·  12.3k / 32k")
                    return Fail();
                ContextSectionToggle.IsChecked = true;
                ContextSection_Click(ContextSectionToggle, new RoutedEventArgs());
                await Settle();
                if (ContextSectionBody.Content is not StackPanel sectionBody || sectionBody.Children.OfType<DockPanel>().Count(d => d.Tag is long) != 7 || !AppSettings.Load().ContextExpanded)
                    return Fail();
                var railWas = _settings.DetailsCollapsed;
                SetWorkerRail(false);
                await Settle();
                Snap("30-context-section");
                SetWorkerRail(railWas);
                // Code: one "<worker> Context" card per worker that worked (a run's events here), the lead's on the header ring;
                // a model loading keeps each worker's last call, marked unloaded, until its next call replaces it, and leaves the lead's
                // as it was (its next call resends the conversation).
                {
                    ShowView();
                    ShowContext(true, null);
                    DrainContext("""{"e":"context","role":"planner","n_ctx":32000,"prompt_tokens":8000,"completion_tokens":0,"parts":{"messages":8000}}""",
                        """{"e":"context","role":"implementer","n_ctx":16000,"prompt_tokens":4000,"completion_tokens":0,"parts":{"messages":4000}}""");
                    var titles = () => (ContextSectionBody.Content as StackPanel)?.Children.OfType<TextBlock>().Where(t => (string?)t.Tag == "ContextTitle").Select(t => t.Text);
                    ContextSectionToggle.IsChecked = true;
                    ContextSection_Click(ContextSectionToggle, new RoutedEventArgs());
                    if (_codeContexts.Count != 2 || CodeContextRing.Tag is not double leadFill || Math.Abs(leadFill - 0.25) > 1e-9 ||
                        string.Join("|", titles() ?? []) != "Lead Context|Implementer Context")
                        return Fail();
                    // The cards stack level with the header's Context button, the lead's first, lined up as Chat's (PinMargin).
                    await Settle();
                    Snap("38-code-context-stack");
                    var stacked = (CodeContextPin.Content as StackPanel)?.Children.OfType<Border>().ToList() ?? [];
                    var stackArea = (FrameworkElement)CodeContextPin.Parent;
                    double RightOf(FrameworkElement e) => e.TranslatePoint(new Point(e.ActualWidth, 0), stackArea).X;                    if (stacked.Count != 2 || ((stacked[0].Child as StackPanel)?.Children[0] as Button)?.Tag as string != "planner" ||
                        Math.Abs(RightOf(stacked[0]) - (stackArea.ActualWidth - PinMargin(stackArea, CodeContextRing).Right)) > 1 ||
                        Math.Abs(stacked[0].TranslatePoint(new Point(), stackArea).Y - CodeContextRing.TranslatePoint(new Point(), stackArea).Y) > 1 || !CodeContextPin.IsVisible || CodeContextRing.IsVisible)
                        return Fail();
                    // Without room beside the conversation (centering off), the conversation starts below the stack.
                    var centered = _settings.CenterChat;
                    _settings.CenterChat = false;
                    RefreshContextPins();
                    await Settle();
                    var stackBottom = CodeContextPin.TranslatePoint(new Point(0, CodeContextPin.ActualHeight), stackArea).Y;
                    var below = CodeScroll.TranslatePoint(new Point(), stackArea).Y >= stackBottom;
                    _settings.CenterChat = centered;
                    RefreshContextPins();
                    if (!below)
                        return Fail();
                    DrainContext("""{"e":"loading","role":"reviewer","path":""}""");
                    bool UnloadedShown() => SmokeFind<TextBlock>(ContextSectionBody, t => t.Tag as string == "ContextUnloaded") is not null &&
                        SmokeFind<TextBlock>(CodeContextPin, t => t.Tag as string == "ContextUnloaded") is not null;
                    if (_codeContexts["implementer"] is not { Used: 4000, NCtx: 16000, Unloaded: true } || _codeContexts["planner"] is not { Used: 8000, Unloaded: false } ||
                        !UnloadedShown())
                        return Fail();
                    DrainContext("""{"e":"context","role":"implementer","n_ctx":16000,"prompt_tokens":5000,"completion_tokens":0,"parts":{"messages":5000}}""");
                    if (_codeContexts["implementer"] is not { Used: 5000, Unloaded: false } || UnloadedShown())
                        return Fail();
                    ContextSectionToggle.IsChecked = false;
                    ContextSection_Click(ContextSectionToggle, new RoutedEventArgs());
                    ShowContext(true, null);
                    _activeRole = null;
                    ShowView(chat: true);
                    void DrainContext(params string[] events)
                    {
                        foreach (var json in events)
                            _lines.Enqueue(Ev(json));
                        DrainLines();
                    }
                }
                ContextSectionToggle.IsChecked = false;
                ContextSection_Click(ContextSectionToggle, new RoutedEventArgs());
                if (savedChat is null || _store.Get(savedChat.Id) is not { Kind: "chat", Folder: "", Messages.Count: 2, Title: "Saved chat title" } || _store.View().Ungrouped[0].Id != savedChat.Id)
                    return Fail();
                StartChatTurn("Follow-up");
                SetChatFinal("Second answer");
                _lines.Enqueue(Ev("""{"e":"chat_title","text":"Other title"}"""));
                DrainLines();
                FinishChatTurn(0, false, null);
                if (_chatConv != savedChat || _store.Get(savedChat.Id) is not { Messages.Count: 4, Title: "Saved chat title" } || _store.View().Ungrouped.Count(e => e.Kind == "chat") != 1)
                    return Fail();
                // A history summarized before the turn: the ring drops at once, the summary is saved with the chat, and the next
                // request sends it in place of the messages it covers.
                StartChatTurn("Third");
                _lines.Enqueue(Ev("""{"e":"history_compacted","role":"reviewer","summary":"SUM","covers":4,"before":12300,"after":3200}"""));
                DrainLines();
                var compactedRing = ChatContextRing.ToolTip as string;
                var historyNote = _reply!.Content.Children.OfType<TextBlock>().Any(t => t.Text == "Context compacted · 12.3k → 3.2k tokens");
                SetChatFinal("Third answer");
                FinishChatTurn(0, false, null);
                var summed = JsonNode.Parse(ChatRequest("reviewer", "Fourth"))!;
                if (compactedRing != "3.2k / 32k tokens (10%)" || !historyNote || _store.Get(savedChat.Id) is not { Messages.Count: 6, Summary: "SUM", SummaryCovers: 4 } ||
                    (string)summed["summary"]! != "SUM" || (int)summed["summary_covers"]! != 4 ||
                    string.Join("|", summed["messages"]!.AsArray().Select(m => (string)m!["content"]!)) != "Third|Third answer|Fourth")
                    return Fail();
                NewChat_Click(ChatSend, new RoutedEventArgs());
                if (_chatConv is not null || ChatPanel.Children.Count != 0 || _store.Get(savedChat.Id) is null || ChatContextRing.Tag is not null)
                    return Fail();
                ShowView(); // the chat lists in the CHAT section, never the CODE one
                if (Rows().Split("SECTION:CODE") is not [var chatRows, var codeRows] || !chatRows.Contains(savedChat.Id) || codeRows.Contains(savedChat.Id))
                    return Fail();
                ShowView(chat: true);
                ClickRow(savedChat.Id);
                await Settle();
                Snap("23-sidebar-chat");
                if (ChatContextRing.ToolTip as string != "3.2k / 32k tokens (10%)" || _chat is not { Summary: "SUM", SummaryCovers: 4 }) // the saved chat brings back its context and summary
                    return Fail();
                // The title bar names the open chat.
                if (TitleChatButton.Visibility != Visibility.Visible || TitleChatText.Text != _store.View().Ungrouped.Single(e => e.Id == savedChat.Id).Title)
                    return Fail();
                if (NavChat.IsChecked != true || _chatConv != savedChat || _chatHistory.Count != 6 || ChatPanel.Children.Count != 6 || _codeConv != chatA ||
                    SidebarList.Children.Cast<UIElement>().Select(Main).Single(m => m is ToggleButton { IsChecked: true }).Tag as string != savedChat.Id)
                    return Fail();
                // Under every message: a muted timestamp (exact time on hover) and a copy icon; the reopened chat shows its saved times.
                var savedTimes = _store.Get(savedChat.Id)!.Messages.Select(m => m.Time).ToList();
                (TextBlock Stamp, Button Copy)? FooterAt(int index) => ChatPanel.Children[index] switch
                {
                    StackPanel { Children: [Border, StackPanel { Children: [TextBlock s, Button b] }] } => (s, b), // a user bubble
                    StackPanel { Children: [.., StackPanel { Children: [TextBlock s, Button b, TextBlock] }] } root when !root.Children.OfType<Button>().Any() => (s, b), // a reply (the tail is its call / turn / task stats)
                    _ => null,
                };
                string? copiedMessage = null;
                _copyMessageHook = text => copiedMessage = text;
                var footers = Enumerable.Range(0, 4).Select(FooterAt).ToList();
                if (footers.Any(f => f is null) || savedTimes.Any(t => t is null) || footers.Select((f, i) => f!.Value.Stamp.ToolTip as string != savedTimes[i]!.Value.ToLocalTime().ToString("f")).Any(wrong => wrong) ||
                    footers.Any(f => f!.Value.Stamp.Text != "just now" || ((TextBlock)f.Value.Copy.Content).Text != Markdown.CopyGlyph))
                    return Fail();
                footers[3]!.Value.Copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var replyChecked = ((TextBlock)footers[3]!.Value.Copy.Content).Text == Markdown.CopiedGlyph;
                footers[2]!.Value.Copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                _copyMessageHook = null;
                if (!replyChecked || copiedMessage != "Follow-up" || FindTextButtons(ChatPanel).Any(t => t == "Copy reply"))
                    return Fail();
                var realNow = Now;
                Now = () => realNow().AddHours(3); // the minute timer's refresh
                RefreshStamps();
                var aged = footers.All(f => f!.Value.Stamp.Text == "3 hours ago");
                Now = realNow;
                RefreshStamps();
                if (!aged || footers[0]!.Value.Stamp.Text != "just now")
                    return Fail();
                await Settle();
                Snap("31-message-footer");
                var footerTheme = _settings.Theme;
                _settings.Theme = "Parchment";
                ApplyTheme();
                await Settle();
                Snap("31-message-footer-parchment");
                _settings.Theme = footerTheme;
                ApplyTheme();

                // Output stats: each model call's tok/s and tokens under its thinking / stage sections, the answer's in the reply
                // footer with the Turn and Task totals; the Task total is saved with the chat.
                string StatsIn(Expander e) => ((StackPanel)e.Content).Children.OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "stats")?.Text ?? "";
                string StatsName(string role) => ChatName(CardFor(role), role);
                var taskBefore = _chat.Tokens;
                StartChatTurn("Stats question");
                foreach (var line in new[]
                {
                    Ev("""{"e":"chat_stage","role":"implementer","stage":"answer","title":"Implementer"}"""),
                    Delta("implementer", "thinking", "pondering"), Delta("implementer", "text", "streamed"),
                    Ev("""{"e":"done","role":"implementer","final":true}"""),
                    Ev("""{"e":"usage","role":"implementer","prompt":5678,"completion":1234,"tps":42.14}"""),
                    Ev("""{"e":"chat_reply","role":"implementer","text":"Stats answer"}"""),
                })
                    _lines.Enqueue(line);
                DrainLines();
                var statsOne = _reply!;
                FinishChatTurn(0, false, null);
                var oneLine = LauncherLogic.StatsLine(StatsName("implementer"), 42.14, 1234, 5678);
                var thinkingSection = statsOne.Content.Children.OfType<Expander>().Single();
                if (StatsIn(thinkingSection) != oneLine || !oneLine.Contains("42.1 tok/s") || !oneLine.Contains("1,234 generated") || !oneLine.Contains("5,678 prompt") ||
                    !((TextBlock)((StackPanel)thinkingSection.Header).Children[1]).Text.Contains("42.1 tok/s") ||
                    statsOne.Stats.Text != $"{oneLine} · {LauncherLogic.TotalsLine(6912, taskBefore + 6912)}" || !statsOne.Stats.Text.Contains("Turn: 6.9k tokens"))
                    return Fail();
                StartChatTurn("Stats multi"); // three delegations, a section each
                var multiEvents = new List<string>();
                foreach (var (role, stage, title, n) in new[] { ("planner", "delegate", "Plan", 1), ("implementer", "delegate", "Draft", 2), ("reviewer", "delegate", "Review", 3) })
                {
                    multiEvents.Add(Ev($$"""{"e":"chat_stage","role":"{{role}}","stage":"{{stage}}","title":"{{title}}"}"""));
                    multiEvents.Add(Delta(role, "text", $"{title} body"));
                    multiEvents.Add(Ev($$"""{"e":"done","role":"{{role}}","final":true}"""));
                    multiEvents.Add(Ev($$"""{"e":"usage","role":"{{role}}","prompt":{{n * 100}},"completion":{{n * 10}},"tps":{{n * 10}}}"""));
                }
                multiEvents.Add(Ev("""{"e":"chat_reply","role":"reviewer","text":"Multi answer"}"""));
                foreach (var line in multiEvents)
                    _lines.Enqueue(line);
                DrainLines();
                var statsMulti = _reply!;
                FinishChatTurn(0, false, null);
                var stageSections = statsMulti.Content.Children.OfType<Expander>().ToList();
                var stageRoles = new[] { "planner", "implementer", "reviewer" };
                if (stageSections.Count != 3 || stageSections.Select((e, i) => StatsIn(e) == LauncherLogic.StatsLine(StatsName(stageRoles[i]), (i + 1) * 10, (i + 1) * 10, (i + 1) * 100)).Contains(false) ||
                    statsMulti.Stats.Text != $"{LauncherLogic.StatsLine(StatsName("reviewer"), 30, 30, 300)} · {LauncherLogic.TotalsLine(660, taskBefore + 6912 + 660)}" ||
                    _chat.Tokens != taskBefore + 6912 + 660)
                    return Fail();
                foreach (var e in stageSections.Append(thinkingSection))
                    e.IsExpanded = true;
                await Settle();
                Snap("34-output-stats");
                NewChat_Click(ChatSend, new RoutedEventArgs());
                if (_chat.Tokens != 0 || _store.Get(savedChat.Id)?.Tokens != taskBefore + 6912 + 660)
                    return Fail();

                // Live stats, on a stepped clock: thinking deltas open a THINKING section and the answer streams outside it; while
                // a call streams, a muted line under its open block counts its chunks (tokens) and their rate, moving on with the
                // stream; the call's usage event brings the exact stats line (in the section, and in the footer for the answer).
                var liveClock = _clockMs;
                var liveMs = 1000.0;
                _clockMs = () => liveMs;
                void FeedLive(IEnumerable<string> lines)
                {
                    foreach (var line in lines)
                        _lines.Enqueue(line);
                    DrainLines();
                }
                StartChatTurn("Live stats");
                var liveName = StatsName("implementer");
                FeedLive(new[] { Ev("""{"e":"chat_stage","role":"implementer","stage":"answer","title":"Implementer"}""") }
                    .Concat(Enumerable.Repeat(Delta("implementer", "thinking", "mull "), 10)));
                var liveReply = _reply!;
                var thinkLive = liveReply.Block?.Live;
                var firstCount = thinkLive?.Text;
                liveMs += 500;
                FeedLive(Enumerable.Repeat(Delta("implementer", "thinking", "mull "), 20));
                if (thinkLive is null || firstCount != LauncherLogic.LiveLine(liveName, 0, 10) || thinkLive.Text != LauncherLogic.LiveLine(liveName, 60, 30))
                    return Fail();
                liveMs += 100; // within 250 ms of the last refresh: the line waits
                FeedLive(Enumerable.Repeat(Delta("implementer", "thinking", "mull "), 5));
                if (thinkLive.Text != LauncherLogic.LiveLine(liveName, 60, 30))
                    return Fail();
                FeedLive(new[] { Ev("""{"e":"done","role":"implementer","final":false}""") }.Concat(Enumerable.Repeat(Delta("implementer", "text", "Answer "), 5)));
                var answerLive = liveReply.Block?.Live;
                var liveSection = liveReply.Content.Children.OfType<Expander>().SingleOrDefault();
                if (answerLive is null || answerLive.Text != LauncherLogic.LiveLine(liveName, 40 / 0.6, 40) || liveSection is null ||
                    ((TextBlock)((StackPanel)liveSection.Header).Children[0]).Text != "THINKING" ||
                    ((TextBox)((StackPanel)liveSection.Content).Children[0]).Text != string.Concat(Enumerable.Repeat("mull ", 35)) ||
                    liveReply.AnswerText.ToString() != string.Concat(Enumerable.Repeat("Answer ", 5)) || liveReply.Content.Children.Contains(thinkLive))
                    return Fail();
                _clockMs = liveClock;
                await Settle();
                Snap("35-live-stats");
                FeedLive(new[] { Ev("""{"e":"done","role":"implementer","final":true}"""),
                    Ev("""{"e":"usage","role":"implementer","prompt":900,"completion":40,"tps":38.5}"""),
                    Ev("""{"e":"chat_reply","role":"implementer","text":"Answer"}""") });
                var liveExact = LauncherLogic.StatsLine(liveName, 38.5, 40, 900);
                if (liveReply.Content.Children.Contains(thinkLive) || liveReply.Content.Children.Contains(answerLive) || StatsIn(liveSection) != liveExact)
                    return Fail();
                FinishChatTurn(-1, true, null); // stopped: shows the footer without saving the chat
                if (!liveReply.Stats.Text.StartsWith(liveExact + " · "))
                    return Fail();
                NewChat_Click(ChatSend, new RoutedEventArgs());
                OpenChatConversation(savedChat.Id);
                if (_chat.Tokens != taskBefore + 6912 + 660 || _chatConv != savedChat)
                    return Fail();
                answer = "yes";
                ClickItem(savedChat.Id, "Delete…");
                if (_chatConv is not null || ChatPanel.Children.Count != 0 || _store.Get(savedChat.Id) is not null)
                    return Fail();

                // Composer extras: a chip in one view switches both views and the saved setting; New skill… makes an enabled
                // skill that the Skills menu lists and the next request carries; unticking it drops it from the request.
                void Flip(ToggleButton chip)
                {
                    chip.IsChecked = chip.IsChecked != true; // what a click does before its Click handler
                    ExtraToggle_Click(chip, new RoutedEventArgs());
                }
                Flip(CodeSearchToggle);
                Flip(ChatToolsToggle);
                if (!_settings.UseSearch || !_settings.UseTools || ChatSearchToggle.IsChecked != true || CodeToolsToggle.IsChecked != true || AppSettings.Load() is not { UseSearch: true, UseTools: true })
                    return Fail();
                answer = "Smoke skill";
                NewSkill();
                var skillFolder = Path.Combine(AppSettings.SkillsRoot, "Smoke skill");
                var request = JsonNode.Parse(LauncherLogic.BuildChatRequest("implementer", [new("user", "x")], CurrentExtras()))!;
                if (!File.Exists(Path.Combine(skillFolder, "SKILL.md")) || _settings.Skills is not ["Smoke skill"] || CodeSkillsButton.IsChecked != true ||
                    ((TextBlock)((StackPanel)ChatSkillsButton.Content).Children[1]).Text != "Skills · 1  ▾" || !(bool)request["search"]! || !(bool)request["tools"]! ||
                    (string)request["skills"]![0]! != skillFolder)
                    return Fail();
                ShowView(chat: true);
                var skillsMenu = BuildSkillsMenu(ChatSkillsButton);
                skillsMenu.IsOpen = true;
                await Settle();
                Snap("24-skills-menu", skillsMenu);
                var skillItem = skillsMenu.Items.OfType<MenuItem>().First(i => i.Tag as string == "Smoke skill");
                skillsMenu.IsOpen = false;
                if (skillItem is not { Header: "Smoke skill", IsChecked: true, ToolTip: "When to use this skill, in one sentence." } ||
                    !skillsMenu.Items.OfType<MenuItem>().Any(i => i is { Header: "init-projMD (built-in)", IsChecked: false })) // bundled with the app
                    return Fail();
                SetSkill("SMOKE skill", true); // a key in another case is the same folder: no duplicate, still sent
                if (_settings.Skills.Count != 1 || CurrentExtras().Skills.Count != 1)
                    return Fail();
                skillItem.IsChecked = false;
                skillItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                ChatSkillsButton.IsChecked = true; // a click on Skills toggles it; RefreshExtras ties its look to the enabled skills
                RefreshExtras();
                if (_settings.Skills.Count != 0 || CurrentExtras().Skills.Count != 0 || ChatSkillsButton.IsChecked != false || NewSkillRefused("Smoke skill") || NewSkillRefused("SMOKE SKILL") ||
                    NewSkillRefused("a/b") || NewSkillRefused("notes."))
                    return Fail();
                bool NewSkillRefused(string name)
                {
                    answer = name;
                    NewSkill();
                    return _settings.Skills.Count != 0;
                }
            }
            finally
            {
                (_codeFolderPick, _codeChoice, _projectMdPick, _openProjectMd) = (null, null, null, null);
                ProjectPath = codePrevious;
                try { Directory.Delete(codeProject, true); } catch (IOException) { }
            }
            CloseCodeConversation();
            ShowView();
            await Settle();
        }
        _process = new Process(); // never started; Start_Click sets _process before SetRunning(true)
        SetRunning(true); // status ring and header shimmer
        await Settle();
        if (CodeProjectButton.IsEnabled)
            return Fail();
        Snap("3-running");
        _process.Dispose();
        _process = null;
        SetRunning(false);
        await Settle();
        if (!CodeProjectButton.IsEnabled)
            return Fail();
        // Maximize, then drag the title bar: each cycle restores at the size from before maximizing, under the cursor.
        // This maximizes on a real screen for a moment; no input is sent.
        MouseButtonEventArgs TitleClick(int count, RoutedEvent kind)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = kind };
            typeof(MouseButtonEventArgs).GetProperty(nameof(args.ClickCount))!.SetValue(args, count);
            return args;
        }
        foreach (var size in new[] { new Size(1100, 700), new Size(1100, 700), new Size(1100, 700), new Size(1240, 760) })
        {
            (Width, Height) = (size.Width, size.Height);
            await Settle();
            MaximizeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle();
            TitleBar.RaiseEvent(TitleClick(1, MouseLeftButtonDownEvent)); // a plain click on the maximized bar does not restore
            TitleBar.RaiseEvent(TitleClick(1, MouseLeftButtonUpEvent));
            if (WindowState != WindowState.Maximized || _maximizedPress is not null || TitleBar.IsMouseCaptured)
                return Fail();
            var inBar = new Point(TitleBar.ActualWidth * 0.25, 10);
            var cursor = TitleBar.PointToScreen(inBar);
            RestoreForDrag(cursor, inBar);
            await Settle();
            var at = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice.Transform(cursor);
            if (WindowState != WindowState.Normal || Math.Abs(ActualWidth - size.Width) > 1 || Math.Abs(ActualHeight - size.Height) > 1 ||
                Math.Abs((at.X - Left) / ActualWidth - 0.25) > 0.01 || at.Y < Top || at.Y > Top + TitleBar.ActualHeight)
            {
                Console.Error.WriteLine($"ui-smoke: drag restore: {WindowState} {Left},{Top} {ActualWidth}x{ActualHeight}, wanted {size} under {at}");
                return Fail();
            }
        }
        TitleBar.RaiseEvent(TitleClick(2, MouseLeftButtonDownEvent)); // double-click toggles
        await Settle();
        if (WindowState != WindowState.Maximized)
            return Fail();
        TitleBar.RaiseEvent(TitleClick(2, MouseLeftButtonDownEvent));
        await Settle();
        if (WindowState != WindowState.Normal || Math.Abs(ActualWidth - 1240) > 1)
            return Fail();
        Minimize_Click(this, new RoutedEventArgs());
        await Settle();
        if (WindowState != WindowState.Minimized)
            return Fail();
        WindowState = WindowState.Normal;
        (Left, Top) = (-20000, -20000);
        await Settle();
        // The Settings window: the gear and the rail's gear open it, owned and borderless with the Options content inside;
        // a theme picked in it restyles it live; × and Esc close it, and its content goes back to the holder each time, so it opens again.
        for (var way = 0; way < 2; way++)
        {
            if (way == 0)
                NavOptions.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            else
                RailSettings.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle();
            if (_settingsWindow is not { } settings || !settings.IsVisible || settings.Owner != this || settings.WindowStyle != WindowStyle.None
                || !settings.AllowsTransparency || settings.ActualWidth < 900 || SettingsHolder.Child is not null || !ThemeFlatRadio.IsDescendantOf(settings))
                return Fail();
            if (way == 0)
            {
                var background = ((SolidColorBrush)settings.Frame.Background).Color;
                var picked = new[] { ThemeFlatRadio, ThemeGoldRadio, ThemeGlassRadio, ThemeOledRadio, ThemeGraphiteRadio, ThemeParchmentRadio, ThemeParchmentDarkRadio }
                    .Single(r => r.IsChecked == true);
                (picked == ThemeOledRadio ? ThemeParchmentRadio : ThemeOledRadio).IsChecked = true;
                await Settle();
                if (((SolidColorBrush)settings.Frame.Background).Color == background)
                    return Fail();
                Snap("26-settings-window-live-theme", settings);
                picked.IsChecked = true;
                await Settle();
                if (((SolidColorBrush)settings.Frame.Background).Color != background)
                    return Fail();
                // Each tab shows its own page only.
                foreach (var tab in SettingsTabs.Children.OfType<RadioButton>().Reverse())
                {
                    tab.IsChecked = true;
                    await Settle();
                    if (SettingsPages.Children.OfType<FrameworkElement>().Count(p => p.IsVisible) != 1
                        || SettingsPages.Children.OfType<FrameworkElement>().Single(p => p.IsVisible).Tag as string != tab.Tag as string
                        || SettingsTabs.Children.OfType<RadioButton>().Count(t => t.IsChecked == true) != 1)
                        return Fail();
                    Snap($"26-settings-{tab.Tag}", settings);
                }
            }
            if (way == 1) // Esc
                settings.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(settings)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            else // ×
                settings.CloseX.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle();
            if (settings.IsVisible || _settingsWindow is not null || SettingsHolder.Child != SettingsContent)
                return Fail();
        }
        // Attachments: "+" offers files/photos and a folder; a text file, a photo and a folder become chips, then the message
        // carries the texts (the bubble shows only names) and the request the photo; a binary file is refused.
        {
            var area = Path.Combine(AppSettings.DataRoot, "smoke-attach");
            Directory.CreateDirectory(Path.Combine(area, "src", "node_modules"));
            File.WriteAllText(Path.Combine(area, "notes.txt"), "remember the milk");
            File.WriteAllText(Path.Combine(area, "src", "app.cs"), "class App {}");
            File.WriteAllText(Path.Combine(area, "src", "node_modules", "junk.js"), "junk");
            File.WriteAllBytes(Path.Combine(area, "photo.png"), [0x89, 0x50, 0x4E, 0x47, 0]);
            File.WriteAllBytes(Path.Combine(area, "blob.bin"), [1, 0, 2]);
            ShowView(chat: true);
            var plusItems = PlusMenu(ChatPlus, _chat).Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();
            foreach (var path in new[] { "notes.txt", "photo.png", "src", "blob.bin" })
                Attach(_chat, Path.Combine(area, path));
            var composed = ComposeMessage(_chat, "Look");
            var request = JsonNode.Parse(LauncherLogic.BuildChatRequest("planner", [new("user", composed)], CurrentExtras() with { Images = _turnImages }))!;
            if (plusItems is not ["Add files or photos", "Add folder…"] || // the chat's asks: Work in it or Attach its files ChatAttachList.Children.Count != 3 || !ChatSend.IsEnabled && _workflowRoot is not null ||
                !composed.StartsWith("Look" + AttachMarker) || !composed.Contains("remember the milk") || !composed.Contains("<file name=\"src/app.cs\">") ||
                composed.Contains("junk") || composed.Contains("blob") || request["images"]!.AsArray().Count != 1 ||
                BubbleText(composed) != "Look\n\n📎 notes.txt, photo.png, src/" || TypedText(composed) != "Look" || TypedText("Plain") != "Plain")
                return Fail();
            _chatAttachments.Clear();
            RefreshAttachments(_chat);
            Directory.Delete(area, recursive: true);
        }
        // Centered and wide: the ring pins its card beside the chat (saved) and the card follows the context; clicked again it
        // closes. A reply that came only as thinking shows once, as the answer.
        {
            var (width, pinned, details) = (Width, _settings.ContextPinned, (DetailsColumn.MinWidth, DetailsColumn.Width));
            Width = 2400;
            (DetailsColumn.MinWidth, DetailsColumn.Width) = (260, new GridLength(340)); // room for the pinned card beside the chat
            _settings.ContextPinned = false;
            ShowView(chat: true);
            await Settle();
            ChatContextRing.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle();
            var shown = ChatContextPin.IsVisible && _settings.ContextPinned && ChatContextPin.Content is Border { Tag: "ContextCard" } && _overlays[false].Body.IsVisible;
            ShowContext(false, new ContextSnapshot { Prompt = 500, Completion = 100, NCtx = 8192 });
            await Settle();
            var follows = SmokeFind<TextBlock>(_overlays[false].Body, t => t.Text.StartsWith("600 / ")) is not null; // one card: the breakdown under its header
            bool Percent() => ChatContextRing.Content is Border { Child: DockPanel face } && face.Children.OfType<TextBlock>().Any(t => t.Tag as string == "ContextPercent" && t.Text == "7%");
            var percentOff = !Percent(); // off by default; Settings turns it on
            ContextPercentToggle.IsChecked = true;
            ContextPercent_Click(ContextPercentToggle, new RoutedEventArgs());
            var percentOn = Percent();
            await Settle();
            Snap("29-context-pinned");
            ContextPercentToggle.IsChecked = false;
            ContextPercent_Click(ContextPercentToggle, new RoutedEventArgs());
            if (!percentOff || !percentOn)
                return Fail();
            ChatContextRing.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle();
            if (!shown || !follows || _overlays[false].Body.IsVisible || _settings.ContextPinned)
                return Fail();
            StartChatTurn("Hello?");
            _lines.Enqueue(LauncherLogic.EventPrefix + """{"e":"delta","role":"planner","kind":"thinking","text":"Hi there"}""");
            _lines.Enqueue(LauncherLogic.EventPrefix + """{"e":"done","role":"planner"}""");
            _lines.Enqueue(LauncherLogic.EventPrefix + """{"e":"chat_reply","role":"planner","text":"Hi there"}""");
            DrainLines();
            var once = _reply!.Content.Children.OfType<Expander>().Count() == 0 && _reply.Answer?.Text == "Hi there";
            FinishChatTurn(0, false, null);
            (Width, _settings.ContextPinned, (DetailsColumn.MinWidth, DetailsColumn.Width)) = (width, pinned, details);
            ShowContext(false, null);
            await Settle();
            if (!once)
                return Fail();
        }
        // While a chat replies, other chats open (its view is parked and keeps streaming), and it comes back when reopened; its end
        // saves it even when another chat is shown. Other chats can be deleted meanwhile; the replying one cannot.
        {
            ShowView(chat: true);
            NewChat_Click(this, new RoutedEventArgs());
            var other = _store.Create("", "chat")!;
            other.Messages = [new("user", "Other question"), new("assistant", "Other answer")];
            _store.Save(other);
            var spare = _store.Create("", "chat")!;
            spare.Messages = [new("user", "Spare"), new("assistant", "Spare answer")];
            _store.Save(spare);
            string Ev(string json) => LauncherLogic.EventPrefix + json;
            StartChatTurn("Long question");
            var running = _chatConv!;
            var runningPanel = _chat.Panel;
            OpenChatConversation(other.Id);
            _lines.Enqueue(Ev("""{"e":"delta","role":"planner","kind":"text","text":"Partial"}"""));
            DrainLines();
            var parkedOk = _chatConv == other && _chatHistory.Count == 2 && _chat.Panel != runningPanel && ChatScroll.Content == _chat.Panel &&
                RunningId == running.Id && _chat.Panel.Children.Count == 2;
            _codeChoice = _ => "";
            DeleteChat(new ConversationStore.Entry(running.Id, "x", "", DateTime.Now, "chat"));
            var keptRunning = _store.Get(running.Id) is not null;
            DeleteChat(new ConversationStore.Entry(spare.Id, "x", "", DateTime.Now, "chat"));
            var deletedSpare = _store.Get(spare.Id) is null;
            OpenChatConversation(running.Id); // back: the live view
            var unparked = _chat.Panel == runningPanel && ChatScroll.Content == runningPanel && _chatConv == running;
            OpenChatConversation(other.Id);
            _lines.Enqueue(Ev("""{"e":"chat_reply","role":"planner","text":"Full answer"}"""));
            DrainLines();
            FinishChatTurn(0, false, null);
            var saved = _store.Get(running.Id)?.Messages.Select(m => m.Content).ToList();
            var otherIntact = _chatConv == other && _chatHistory.Count == 2 && _store.Get(other.Id)?.Messages.Count == 2;
            OpenChatConversation(running.Id);
            var reopened = _chatHistory.Count == 2 && _chatHistory[1].Content == "Full answer";
            _codeChoice = null;
            if (!parkedOk || !keptRunning || !deletedSpare || !unparked || saved is not ["Long question", "Full answer"] || !otherIntact || !reopened)
            {
                Console.Error.WriteLine($"park {parkedOk} {keptRunning} {deletedSpare} {unparked} {string.Join("|", saved ?? [])} {otherIntact} {reopened}");
                return Fail();
            }
            NewChat_Click(this, new RoutedEventArgs());
        }
        // The browser: the title bar's globe opens it in the right column in place of the worker details (saved); a model search shows there
        // when it follows the model; the globe again, or opening a worker's settings, gives the column back.
        {
            var collapsed = _settings.DetailsCollapsed;
            ShowView(chat: true);
            BrowserButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle();
            var opened = BrowserPanel.IsVisible && !DetailsPanel.IsVisible && !WorkerRail.IsVisible && _settings.BrowserOpen && BrowserAddress.Text == "https://duckduckgo.com/";
            _settings.BrowserFollow = true;
            BrowserFollowTool("web_search", """{"query":"agent orchestrator"}""");
            var followed = BrowserAddress.Text == "https://duckduckgo.com/?q=agent%20orchestrator";
            BrowserFollowTool("calculator", """{"expression":"1+1"}""");
            var ignored = BrowserAddress.Text == "https://duckduckgo.com/?q=agent%20orchestrator";
            // Each followed search gets a tab of its own, up to the setting (the slider: 3 tabs here); then the oldest followed
            // one is reused. Unlimited (the slider's last stop) keeps opening tabs.
            var followSetting = _settings.FollowTabs;
            FollowTabsSlider.Value = 3;
            var tabsBefore = _tabs.Count;
            for (var n = 1; n <= 5; n++)
                BrowserFollowTool("web_search", $$"""{"query":"cap {{n}}"}""");
            var capped = _settings.FollowTabs == 3 && FollowTabsValue.Text == "3 tabs" && _followTabs.Count == 3 && _tabs.Count <= tabsBefore + 3 &&
                         BrowserAddress.Text == SearchUrl("cap 5");
            FollowTabsSlider.Value = 11;
            for (var n = 6; n <= 9; n++)
                BrowserFollowTool("web_search", $$"""{"query":"cap {{n}}"}""");
            capped &= _settings.FollowTabs == 0 && FollowTabsValue.Text == "Unlimited" && _followTabs.Count == 7;
            _settings.FollowTabs = followSetting;
            FollowTabsSlider.Value = followSetting is >= 3 and <= 10 ? followSetting : 11;
            if (!capped)
                return Fail();
            // A reply's link: the first asks where links open (saved, shown in Settings > General), later ones don't ask
            var (linkOpen, choice) = (_settings.LinkOpen, _codeChoice);
            string? askedTitle = null;
            _settings.LinkOpen = "";
            _codeChoice = title => { askedTitle = title; return "app"; };
            OpenLink(BrowserHome + "?q=link");
            var linked = askedTitle == "Open links" && _settings.LinkOpen == "app" && BrowserAddress.Text == BrowserHome + "?q=link" &&
                         (LinkOpenBox.SelectedItem as ComboBoxItem)?.Tag as string == "app";
            askedTitle = null;
            OpenLink(BrowserHome + "?q=again");
            linked &= askedTitle is null && BrowserAddress.Text == BrowserHome + "?q=again";
            (_settings.LinkOpen, _codeChoice) = (linkOpen, choice);
            SelectTagged(LinkOpenBox, linkOpen);
            if (!linked)
                return Fail();
            // Tabs: a new one shows (its chip checked), a chip switches back, closing the shown one shows its neighbour.
            var before = _tabs.Count;
            OpenTab(BrowserHome + "?q=two");
            var tabbed = _tabs.Count == before + 1 && _tab == _tabs[^1] && BrowserTabStrip.Children.Count == _tabs.Count &&
                         _tabs[^1].Chip.IsChecked == true && BrowserAddress.Text == BrowserHome + "?q=two";
            _tabs[0].Chip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var switched = _tab == _tabs[0] && BrowserAddress.Text == _tabs[0].Url && _tabs[^1].Web.Visibility == Visibility.Collapsed && _tabs[^1].Chip.IsChecked == false;
            SelectTab(_tabs[^1]);
            CloseTab(_tab!);
            tabbed = tabbed && switched && _tabs.Count == before && _tab == _tabs[^1] && _tab.Web.Visibility == Visibility.Visible;
            // The model's browser tool on a real page: its main content (the <article>) comes before the menus around it, and a
            // screenshot scrolled to a word comes back as a JPEG data URL.
            var pageFile = Path.Combine(AppSettings.DataRoot, "smoke-page.html");
            File.WriteAllText(pageFile, "<p>top</p><script type='application/json'>{\"note\": \"Screens here\"}</script>" + // data in the body, as GitHub's
                "<nav>" + string.Concat(Enumerable.Repeat("<p>menu item</p>", 40)) + "</nav><main><article><h1>README</h1>" +
                string.Concat(Enumerable.Repeat("<p>The readme text.</p>", 20)) + "<p style='margin-top:3000px'>Screens here</p></article></main>");
            var (pageText, _) = await BrowserAnswerAsync("open", new Uri(pageFile).AbsoluteUri, true, 0, "");
            var (shotText, shot) = await BrowserAnswerAsync("screenshot", "", false, 0, "Screens");
            var scrolled = await _tab!.Web.CoreWebView2.ExecuteScriptAsync("window.scrollY") is var y && double.TryParse(y, out var dy) && dy > 1000;
            var pageOk = pageText.Contains("VISIBLE TEXT:\nMAIN CONTENT:\nREADME") && pageText.IndexOf("REST OF THE PAGE:\ntop") > pageText.IndexOf("The readme text.") && pageText.Contains("menu item") &&
                         shot?.StartsWith("data:image/jpeg;base64,/9j/") == true && shotText.Contains("scrolled to the first \"Screens\"") && scrolled;
            CloseTab(_tab!);
            File.Delete(pageFile);
            if (!pageOk)
            {
                Console.Error.WriteLine($"browser page {pageText[..Math.Min(300, pageText.Length)]} | {shotText} | {shot?[..Math.Min(40, shot.Length)]} | {scrolled}");
                return Fail();
            }
            BrowserButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle();
            var closed = !BrowserPanel.IsVisible && !_settings.BrowserOpen && (collapsed ? WorkerRail.IsVisible : DetailsPanel.IsVisible);
            OpenBrowser(null); // no new page: one finishing later would take the focus from the popups below
            SelectWorker(CardFor("planner"));
            SetWorkerRail(false);
            await Settle();
            var handedBack = !BrowserPanel.IsVisible && DetailsPanel.IsVisible && !_settings.BrowserOpen;
            SetWorkerRail(collapsed);
            tabbed = tabbed && BrowserMessage.Visibility != Visibility.Visible; // a closed tab's engine start failing is not the browser's
            if (!opened || !followed || !ignored || !tabbed || !closed || !handedBack)
            {
                Console.Error.WriteLine($"browser {opened} {followed} {ignored} {tabbed} {closed} {handedBack}");
                return Fail();
            }
        }
        // The profile: usage adds to the lifetime count shown under the name, and the card opens from the sidebar with the stats.
        var lifetime = _stats.Lifetime;
        RecordUsage(1234);
        if (_stats.Lifetime != lifetime + 1234 || !ProfileTokensText.Text.EndsWith("lifetime tokens") || !File.Exists(Path.Combine(AppSettings.DataRoot, "stats.json")))
            return Fail();
        ProfileButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Settle();
        if (_profilePopup is not { IsOpen: true, Child: FrameworkElement profileCard })
            return Fail();
        Snap("28-profile-card", profileCard);
        _profilePopup.IsOpen = false;
        if (!await QueueSmoke(Snap))
            return false;
        if (!await SteerSmoke())
            return false;
        if (!PiSmoke() || !await NodeSmoke())
            return false;
        if (!await LlamaPollSmoke())
            return false;
        if (!AttachSmoke() || !ImagesSmoke() || !ReadingSmoke() || !ThinkingSmoke())
            return false;
        // Notifications: off sends nothing; on, a fully finished reply (nothing queued) notifies with its preview.
        {
            var (was, sent) = (_settings.NotifyWhenDone, new List<string>());
            _notifyHook = (title, text) => sent.Add($"{title}|{text}");
            _settings.NotifyWhenDone = false;
            AfterTurn(completed: true);
            _settings.NotifyWhenDone = true;
            _codeQueue.Add(new QueuedMessage("later", []));
            AfterTurn(completed: true); // a queued message goes next: not finished yet
            _codeQueue.Clear();
            AfterTurn(completed: true);
            (_settings.NotifyWhenDone, _notifyHook) = (was, null);
            if (sent.Count != 1 || !sent[0].Contains("reply finished|"))
            {
                Console.Error.WriteLine($"ui-smoke: notifications: {string.Join(" / ", sent)}");
                return false;
            }
        }
        if (!SkillCompletionSmoke(Snap))
            return false;
        if (!LocalCommandSmoke())
            return false;
        if (!await ApprovalSmoke())
            return false;
        if (!await LocalRuntimeSmoke())
            return false;
        if (!await RemoteSmoke() || !await RemoteTunnelSmoke())
            return false;
        if (!string.IsNullOrEmpty(snapshots))
        {
            foreach (var theme in new[] { "Gold", "Glass", "Oled", "Graphite", "Parchment", "ParchmentDark", "Flat" })
            {
                _settings.Theme = theme;
                ApplyTheme();
                await Settle();
                Snap("5-theme-" + theme);
                ShowView(chat: true);
                await Settle();
                Snap("17-chat-theme-" + theme);
                ShowView();
                OpenSettings();
                await Settle();
                Snap("26-settings-window-" + theme, _settingsWindow);
                _settingsWindow!.Close();
                if (theme is "Parchment" or "Graphite") // the column with the Planner's tab and its settings
                {
                    SetWorkerRail(false);
                    SelectWorker(CardFor("planner"));
                    await Settle();
                    Snap("27-model-settings-" + theme);
                }
            }
        }
        return true;
    }

    // --ui-smoke, sandbox approvals: an "approval" event shows the dialog and writes the answer, with the request's id, to
    // the turn process's stdin (a real child process stands in for orchestrator.py); the network setting persists.
    private async Task<bool> ApprovalSmoke()
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.xaml.cs:{line}"); return false; }
        var (choice, turn, run) = (_codeChoice, _chatProcess, _process);
        var info = new ProcessStartInfo(LauncherLogic.FindPython()) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-c", "import sys; print(sys.stdin.readline().strip()); print(sys.stdin.readline().strip())" })
            info.ArgumentList.Add(arg);
        foreach (var asRun in new[] { false, true }) // a chat/Code turn, then a project run (its answer goes to the run's stdin)
            try
            {
                var asked = new List<string>();
                using var child = Process.Start(info)!;
                _chatProcess = asRun ? null : child;
                _process = asRun ? child : null;
                _codeChoice = title => { asked.Add(title); return asked.Count == 1 ? "allow" : null; }; // allow, then close the dialog
                AnswerApproval(JsonDocument.Parse("""{"e":"approval","id":"a1","kind":"network","detail":"npm install","reason":"deps"}""").RootElement);
                AnswerApproval(JsonDocument.Parse("""{"e":"approval","id":"a2","kind":"write","detail":"C:\\other\n\necho","reason":""}""").RootElement);
                var read = child.StandardOutput.ReadToEndAsync();
                if (await Task.WhenAny(read, Task.Delay(10000)) != read) // no answer reached it: the engine would wait forever
                {
                    child.Kill();
                    return Fail();
                }
                if ((await read).Replace("\r", "") != "{\"id\":\"a1\",\"allow\":true}\n{\"id\":\"a2\",\"allow\":false}\n" ||
                    !asked.SequenceEqual(["Allow network access?", "Allow writing in other folders?"]))
                    return Fail();
            }
            finally
            {
                (_codeChoice, _chatProcess, _process) = (choice, turn, run);
            }
        // Allow similar: the next request of that kind and key in the same chat is answered yes unasked; another key asks.
        {
            var asked = new List<string>();
            var lines = new ProcessStartInfo(LauncherLogic.FindPython()) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var arg in new[] { "-c", "import sys\nfor _ in range(3): print(sys.stdin.readline().strip())" })
                lines.ArgumentList.Add(arg);
            try
            {
                using var child = _chatProcess = Process.Start(lines)!;
                _codeChoice = title => { asked.Add(title); return asked.Count == 1 ? "similar" : null; };
                foreach (var (id, key) in new[] { ("s1", "npm"), ("s2", "npm"), ("s3", "git") })
                    AnswerApproval(JsonDocument.Parse($$"""{"e":"approval","id":"{{id}}","kind":"command","detail":"{{key}} x","reason":"","key":"{{key}}"}""").RootElement);
                var read = child.StandardOutput.ReadToEndAsync();
                if (await Task.WhenAny(read, Task.Delay(10000)) != read)
                {
                    child.Kill();
                    return Fail();
                }
                if ((await read).Replace("\r", "") != "{\"id\":\"s1\",\"allow\":true}\n{\"id\":\"s2\",\"allow\":true}\n{\"id\":\"s3\",\"allow\":false}\n" ||
                    !asked.SequenceEqual(["Allow this command?", "Allow this command?"]))
                    return Fail();
            }
            finally
            {
                (_codeChoice, _chatProcess) = (choice, turn);
                _similarAllowed.Clear();
            }
        }
        var network = _settings.SandboxNetwork;
        SandboxNetworkToggle.IsChecked = !network;
        SandboxNetwork_Click(SandboxNetworkToggle, new RoutedEventArgs());
        var saved = AppSettings.Load().SandboxNetwork;
        SandboxNetworkToggle.IsChecked = network;
        SandboxNetwork_Click(SandboxNetworkToggle, new RoutedEventArgs());
        return saved == !network ? true : Fail();
    }

    // --ui-smoke, Code engine: the radios persist, a Code request names the engine, its Pi home and the conversation, and
    // with Pi chosen but not installed (the smoke data folder has none) a Code turn is refused; Built-in is always ready.
    private bool PiSmoke()
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.xaml.cs:{line}"); return false; }
        var engine = _settings.CodeEngine;
        try
        {
            EnginePiRadio.IsChecked = true;
            if (AppSettings.Load().CodeEngine != "pi" || File.Exists(LauncherLogic.PiCli) || !PiStatus.Text.StartsWith("Pi not installed") ||
                (string)PiInstallButton.Content != "Install Pi")
                return Fail();
            var request = JsonNode.Parse(CodeRequest("chat", "planner", "hi", @"Y:\app"))!;
            if ((string)request["engine"]! != "pi" || (string)request["pi_home"]! != LauncherLogic.PiHome || (string)request["session"]! != (_codeConv?.Id ?? ""))
                return Fail();
            if (CodeEngineReady())
                return Fail();
            EngineBuiltinRadio.IsChecked = true;
            if (AppSettings.Load().CodeEngine != "" || !CodeEngineReady() || (string)JsonNode.Parse(CodeRequest("chat", "planner", "hi", @"Y:\app"))!["engine"]! != "")
                return Fail();
        }
        finally
        {
            (engine == "pi" ? EnginePiRadio : EngineBuiltinRadio).IsChecked = true;
        }
        return true;
    }

    // --ui-smoke, Install Pi without Node.js (PATH emptied): a popup offers to install Node.js; declining installs nothing
    // and logs why, and the install button is usable again.
    private async Task<bool> NodeSmoke()
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.xaml.cs:{line}"); return false; }
        var (path, choice, asked) = (Environment.GetEnvironmentVariable("PATH"), _codeChoice, new List<string>());
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.Combine(AppSettings.DataRoot, "no-node"));
            _codeChoice = title => { asked.Add(title); return null; };
            await PiInstallAsync();
            if (!asked.SequenceEqual(["Install Node.js"]) || !_log.ToString().Contains("Pi install failed: Node.js is not installed") ||
                File.Exists(LauncherLogic.PiCli) || !PiStatus.Text.StartsWith("Pi not installed") || !PiInstallButton.IsEnabled)
                return Fail();
        }
        finally
        {
            _codeChoice = choice;
            Environment.SetEnvironmentVariable("PATH", path);
        }
        return true;
    }

    // --ui-smoke, queued messages: while a reply runs Send queues (input cleared, a row each, "n queued"); nothing is sent
    // while busy; Edit and × take one out; a stopped turn and closing the conversation put the queue back in the input.
    private async Task<bool> QueueSmoke(Action<string, FrameworkElement?> snap)
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.xaml.cs:{line}"); return false; }
        void Queue(string text)
        {
            ChatInput.Text = text;
            ChatSend.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        ShowView(chat: true);
        _chatBusy = true;
        try
        {
            ChatInput.Text = "first";
            RefreshChatInput();
            if (!ChatInput.IsEnabled || !ChatSend.IsEnabled || ChatSend.Visibility != Visibility.Visible || ChatStop.Visibility != Visibility.Visible)
                return Fail();
            Queue("first");
            Queue("second");
            await Task.Delay(200);
            if (_chatQueue.Select(q => q.Text).SequenceEqual(["first", "second"]) is false || ChatInput.Text.Length != 0 ||
                ChatQueueList is not { Visibility: Visibility.Visible, Children.Count: 2 } || !ChatBusyText.Text.Contains("2 queued"))
                return Fail();
            snap("29-queued-messages", ChatComposer);
            SendQueued(); // busy: nothing goes out
            if (_chatQueue.Count != 2)
                return Fail();
            Unqueue(_chat, _chatQueue[1]); // Edit
            if (ChatInput.Text != "second" || _chatQueue.Count != 1)
                return Fail();
            Queue("second");
            var remove = ((DockPanel)((Border)ChatQueueList.Children[0]).Child).Children.OfType<Button>().Single(b => (string)b.Tag == "RemoveQueued");
            remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (_chatQueue.Select(q => q.Text).SequenceEqual(["second"]) is false || ChatQueueList.Children.Count != 1)
                return Fail();
            ChatInput.Text = "typing";
            AfterTurn(completed: false);
            if (ChatInput.Text != "second\n\ntyping" || _chatQueue.Count != 0 || ChatQueueList.Visibility != Visibility.Collapsed)
                return Fail();
            Queue("third");
            ClearConversation(_chat);
            if (ChatInput.Text != "third" || _chatQueue.Count != 0)
                return Fail();
            // A completed turn sends the next queued message; here the send is refused (no llama-server yet), so the
            // message goes back before what is being typed, and no turn starts.
            ChatInput.Clear();
            Queue("auto");
            ChatInput.Text = "draft";
            var bubbles = _chat.Panel.Children.Count;
            _chatBusy = false;
            Queue("late"); // a queued message waits for it: Enter does not jump ahead
            ChatInput.Text = "draft";
            if (_chatQueue.Select(q => q.Text).SequenceEqual(["auto", "late"]) is false)
                return Fail();
            AfterTurn(completed: true);
            await Task.Delay(300);
            if (ChatInput.Text != "auto\n\nlate\n\ndraft" || _chatQueue.Count != 0 || _chatBusy || _chat.Panel.Children.Count != bubbles)
                return Fail();
        }
        finally
        {
            _chatBusy = false;
            _chatQueue.Clear();
            ChatInput.Clear();
            RefreshQueue(_chat);
        }
        return true;
    }

    // --ui-smoke, steering: while a Pi Code turn runs, a queued Code message's Send now writes it to the turn's stdin (a real
    // child process stands in for orchestrator.py) and its row shows Steering…; the "steered" event takes it out of the queue
    // into the conversation (user, assistant, user, assistant in the history); one never delivered is queued again at the end.
    // A command (/compact) has no Send now.
    private async Task<bool> SteerSmoke()
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.xaml.cs:{line}"); return false; }
        DockPanel Row(int i) => (DockPanel)((Border)CodeQueueList.Children[i]).Child;
        Button? SendNow(int i) => Row(i).Children.OfType<Button>().SingleOrDefault(b => (string)b.Tag == "SteerQueued");
        string Label(int i) => Row(i).Children.OfType<TextBlock>().First().Text;
        var info = new ProcessStartInfo(LauncherLogic.FindPython()) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-c", "import sys\nwhile line := sys.stdin.readline(): print(line.strip(), flush=True)" })
            info.ArgumentList.Add(arg);
        var (engine, conv, history, turn) = (_settings.CodeEngine, _codeConv, _code.History.ToList(), _chatProcess);
        _settings.CodeEngine = "pi";
        _codeConv = null; // nothing goes to the store
        _code.History.Clear();
        var child = Process.Start(info)!;
        try
        {
            _chatProcess = child;
            StartTurn(_code, "Fix it", "planner");
            _codeQueue.AddRange([new("Also this", []), new("And that", []), new("/compact keep it", [])]);
            RefreshQueue(_code);
            if (SendNow(0) is not { IsEnabled: true } now || SendNow(1) is null || SendNow(2) is not null) // a command is no message
                return Fail();
            async Task<string?> Written()
            {
                var read = child.StandardOutput.ReadLineAsync();
                return await Task.WhenAny(read, Task.Delay(10000)) == read ? await read : null;
            }
            now.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var line = await Written();
            var key = line is null ? "" : JsonNode.Parse(line)!["key"]?.GetValue<string>() ?? "";
            if (line is null || (string)JsonNode.Parse(line)!["steer"]! != "Also this" || key.Length == 0 || Label(0) != "Steering…" || SendNow(0)!.IsEnabled)
                return Fail();
            string Ev(string json) => LauncherLogic.EventPrefix + json;
            _lines.Enqueue(Ev($$"""{"e":"steered","role":"planner","key":"{{key}}"}"""));
            _lines.Enqueue(Ev("""{"e":"code_reply","role":"planner","text":"Both done"}"""));
            DrainLines();
            if (_codeQueue.Select(q => q.Text).SequenceEqual(["And that", "/compact keep it"]) is false || CodeQueueList.Children.Count != 2 || Label(0) != "Queued")
                return Fail();
            SendNow(0)!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // the engine never delivers this one
            if (await Written() is null || Label(0) != "Steering…")
                return Fail();
            FinishChatTurn(0, false, null);
            if (_code.History.Select(m => (m.Role, m.Content)).SequenceEqual([("user", "Fix it"), ("assistant", SteeredReply), ("user", "Also this"),
                    ("assistant", "Both done")]) is false || _codeQueue.Count != 2 || Label(0) != "Queued" || SendNow(0) is not null)
                return Fail();
        }
        finally
        {
            _chatProcess = turn;
            if (_chatBusy)
                SetChatBusy(false);
            child.StandardInput.Close();
            if (!child.WaitForExit(5000))
                child.Kill();
            child.Dispose();
            (_settings.CodeEngine, _codeConv) = (engine, conv);
            _code.History.Clear();
            _code.History.AddRange(history);
            _codeQueue.Clear();
            RefreshQueue(_code);
        }
        return true;
    }

    // --ui-smoke, local runtime: the status follows the data folder, a turn without llama-server is refused, and stopping
    // the recorded server kills a llama-server but never another process.
    private async Task<bool> LocalRuntimeSmoke()
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.xaml.cs:{line}"); return false; }
        bool Toasted(string text) => ToastStack.Children.OfType<Border>().Any(b => b.Child is StackPanel { Children: [_, TextBlock t] } && t.Text == text);
        RefreshLlamaStatus();
        if (LlamaStatus.Text != "llama-server not installed" || (string)LlamaDownloadButton.Content != "Download llama-server")
            return Fail();
        var replies = _chat.Panel.Children.Count;
        if (await RunTurn(_chat, "hi", "implementer", _ => "{}", LauncherLogic.BuildChatStartInfo, "Chat") ||
            _chatBusy || _chat.Panel.Children.Count != replies || !Toasted("Download llama-server in Settings first"))
            return Fail();
        // A stand-in llama-server.exe that runs: a copy of ping, so its process name is llama-server.
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        Directory.CreateDirectory(LauncherLogic.LlamaFolder);
        File.Copy(ping, LauncherLogic.LlamaServerPath);
        File.WriteAllText(Path.Combine(LauncherLogic.LlamaFolder, "version.txt"), "b9999\nCUDA 12.4");
        RefreshLlamaStatus();
        if (LlamaStatus.Text != "llama-server b9999 installed (CUDA 12.4)" || (string)LlamaDownloadButton.Content != "Update llama-server")
            return Fail();
        void Record(int pid) => File.WriteAllText(_llamaStateFile, $"{{\"pid\": {pid}, \"port\": 8899, \"signature\": \"s\", \"model_file\": \"m.gguf\", \"log\": \"l\"}}");
        Process Run(string exe) => Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        Record(Environment.ProcessId);
        StopLocalServer(); // our own pid: not a llama-server, so it must survive (this line returning is the check)
        using var other = Run(ping);
        using var server = Run(LauncherLogic.LlamaServerPath);
        try
        {
            Record(other.Id);
            StopLocalServer();
            await Task.Delay(500);
            if (other.HasExited)
                return Fail(); // killed a process that is not llama-server
            Record(server.Id);
            if (server.HasExited)
                return Fail(); // the stand-in must be running for the kill below to mean anything
            StopLocalServer();
            if (!server.WaitForExit(5000) || File.Exists(_llamaStateFile))
                return Fail();
        }
        finally
        {
            foreach (var p in new[] { other, server })
                try { p.Kill(); } catch (InvalidOperationException) { }
        }
        // Change lists the GGUFs on disk: a file in the models folder is assigned by its path.
        var models = Path.Combine(AppSettings.DataRoot, "smoke-models");
        var fake = Path.Combine(models, "Fake-Model-Q4_K_M.gguf");
        Directory.CreateDirectory(models);
        File.WriteAllText(fake, "");
        _settings.ModelsFolder = models;
        var choices = await FetchModelChoicesAsync();
        _settings.ModelsFolder = "";
        return choices.SingleOrDefault(c => c.File == fake) is { Label: "Fake-Model-Q4_K_M", Variant: "Q4_K_M", LoadPath: "Fake-Model-Q4_K_M" } || Fail();
    }

    // Real hover cannot be faked, so begin every IsMouseOver storyboard of each control and data template directly:
    // a target name missing from its template throws here instead of on the user's first hover.
    private static void PlayHoverStoryboards(DependencyObject node)
    {
        if (node is FrameworkElement element && VisualTreeHelper.GetChildrenCount(element) > 0) // never-shown elements have no template yet
        {
            FrameworkTemplate? template = element switch { Control c => c.Template, ContentPresenter p => p.ContentTemplate, _ => null };
            var triggers = template switch { ControlTemplate t => t.Triggers, DataTemplate d => d.Triggers, _ => null };
            foreach (var trigger in triggers?.OfType<Trigger>().Where(t => t.Property == IsMouseOverProperty) ?? [])
                foreach (var action in trigger.EnterActions.Concat(trigger.ExitActions).OfType<BeginStoryboard>())
                    try { action.Storyboard.Begin(element, template!); }
                    catch (InvalidOperationException e) { throw new InvalidOperationException($"{element.GetType().Name} '{element.Name}' hover: {e.Message}", e); }
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            PlayHoverStoryboards(VisualTreeHelper.GetChild(node, i));
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_log.ToString(), "Run log copied", "Log is empty");

    // ---- output navigation: expand / collapse all (Ctrl+E / Ctrl+Shift+E), per-section copy

    // Counts as the user's choice (StreamBlock.Touched), so a finishing section is not folded back afterwards.
    private void SetAllExpanded(bool open)
    {
        foreach (var expander in OutputPanel.Children.OfType<Expander>())
            expander.IsExpanded = open;
    }

    // The copy button sits in the BlockExpander template, so its templated parent is the section.
    private void SectionCopy_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).TemplatedParent is Expander expander)
            CopyToClipboard(SectionText(expander), "Section copied", "Section is empty");
    }

    // Kind label, then each visible body part: the text, or for a tool call ARGUMENTS / RESULT and their contents.
    private static string SectionText(Expander expander)
    {
        var parts = new List<string> { ((TextBlock)((StackPanel)expander.Header).Children[0]).Text };
        IEnumerable<UIElement> pieces = expander.Content is Panel panel ? panel.Children.OfType<UIElement>() : new[] { (UIElement)expander.Content };
        foreach (var piece in pieces.Where(p => p.Visibility == Visibility.Visible && p is not TextBlock { Tag: "stats" }))
            parts.Add(piece switch { TextBox box => box.Text, TextBlock label => label.Text, RichTextBox { Tag: TextBox raw } => raw.Text, _ => "" });
        return string.Join("\n", parts);
    }

    private static IEnumerable<string> FindTextButtons(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(child => (child is Button { Content: string text } ? [text] : Enumerable.Empty<string>()).Concat(FindTextButtons(child)));

    private void CopyToClipboard(string text, string done, string empty)
    {
        if (text.Length == 0)
        {
            SetStatus(empty, "#F7C873");
            Toast(empty, "#F7C873");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            SetStatus(done, "#5BD6A2");
            Toast(done);
        }
        catch (Exception exception)
        {
            Log($"Could not copy: {exception.Message}");
            SetStatus("Could not copy", "#FF667A");
            Toast("Could not copy", "#FF667A");
        }
    }

    // ---------------------------------------------------------------- live speed (llama-server's /slots)

    private async void Poll_Tick(object? sender, EventArgs e)
    {
        if (_polling || (_process ?? _chatProcess) is null || _activeRole is null || CardFor(_activeRole) is not { } card)
            return;
        _polling = true;
        try { await PollLlamaAsync(card); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // /slots unavailable: the per-call numbers from the orchestrator still arrive.
        }
        finally { _polling = false; }
    }

    // ---------------------------------------------------------------- project / config helpers

    private JsonDocument? ReadConfig(string project)
    {
        string active;
        try { active = LauncherLogic.ActiveConfigPath(project, _workflowRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { active = ""; }
        foreach (var path in new[] { active, _workflowRoot is null ? "" : Path.Combine(_workflowRoot, "config.json") })
        {
            try
            {
                if (path.Length > 0 && File.Exists(path))
                    return JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        return null;
    }

    private string? ConfiguredVariant(string role)
    {
        if (CardFor(role) is { IsCustom: true } custom) // custom workers live outside "roles"
            return custom.ModelVariant;
        using var config = ReadConfig(SelectedProjectPath());
        return config is not null && config.RootElement.TryGetProperty("roles", out var roles) && roles.TryGetProperty(ConfigRole(role), out var r) &&
               r.TryGetProperty("auto_load", out var load) && load.TryGetProperty("gguf_variant", out var v) ? v.GetString() : null;
    }

    // The roles.<entry> a worker's model is read from: an implementer or reviewer not added runs as the planner.
    private string ConfigRole(string role) => role is "implementer" or "reviewer" && !_settings.AddedRoles.Contains(role) ? "planner" : role;

    // The model id of a built-in role set to a remote API (Settings > Remote API), else null.
    private string? RemoteModel(string role)
    {
        using var config = ReadConfig(SelectedProjectPath());
        return config is not null && config.RootElement.TryGetProperty("roles", out var roles) && roles.TryGetProperty(ConfigRole(role), out var r) &&
               !r.TryGetProperty("auto_load", out _) && r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
    }

    private LauncherLogic.ModelInfo ModelInfoFor(string role, string id) =>
        RemoteModel(role) is not null ? LauncherLogic.RemoteModelInfo(id) : LauncherLogic.DescribeModel(id, ConfiguredVariant(role));

    // Show the configured models on the cards before anything runs.
    private void PrefillCards()
    {
        RefreshEffort(); // callers have just changed a model or the project
        if (_process is not null || _initializing)
            return;
        RefreshWorkerTab();
        using var config = ReadConfig(SelectedProjectPath());
        if (config is null || !config.RootElement.TryGetProperty("roles", out var roles))
            return;
        foreach (var card in _cards)
        {
            if (!card.IsCustom && RemoteModel(card.Role == "compactor" && !LauncherLogic.CompactorHasOwn(roles) ? "planner" : card.Role) is { } remote)
            {
                card.SetModel(LauncherLogic.RemoteModelInfo(remote));
                continue;
            }
            if (card.Role == "compactor")
                card.FollowsMain = !LauncherLogic.CompactorHasOwn(roles);
            var source = card.Role == "compactor" && card.FollowsMain ? "planner" : ConfigRole(card.Role);
            if (!roles.TryGetProperty(source, out var r) || !r.TryGetProperty("auto_load", out var load) ||
                !load.TryGetProperty("model_path", out var path) || path.GetString() is not { } id)
                continue;
            if (r.TryGetProperty("display_name", out var label) && label.GetString() is { Length: > 0 } shown)
                id = shown;
            card.SetModel(LauncherLogic.DescribeModel(id, load.TryGetProperty("gguf_variant", out var v) ? v.GetString() : null));
        }
        RefreshLeadChip(); // the Code chip names the Planner's model
        RefreshVision();
    }

    // Test hooks for --ui-smoke: answer the restart confirmation without showing it, and replace the Recycle Bin.
    private Func<DialogShell, bool>? _restartConfirm;
    private Action<string> _restartRecycle = LauncherLogic.Recycle;

    // Restart Project on a complete project: after confirmation, send everything but PROJECT.md (and .git) to the
    // Recycle Bin, then initialize the project again from the template. True when the caller should start the run.
    private bool RestartProject(string project)
    {
        if (_workflowRoot is null || _process is not null || _chatBusy)
            return false;
        if (LauncherLogic.RestartRefusal(project, _workflowRoot) is { } reason)
        {
            Log($"Restart refused: {reason}.");
            SetStatus("Restart refused", "#FF667A");
            Toast($"Restart refused: {reason}", "#FF667A");
            return false;
        }
        var dialog = BuildRestartDialog(project);
        if (!(_restartConfirm is { } confirm ? confirm(dialog) : dialog.ShowDialog() == true) || !DiscardWorkerEdits()) // the config.json is replaced below
        {
            SetStatus("Restart cancelled", "#F7C873");
            return false;
        }
        if (LauncherLogic.RecycleForRestart(project, _restartRecycle) is { } failure)
        {
            Log($"Restart stopped: {failure.Path} could not be moved to the Recycle Bin ({failure.Error}). Items recycled before it are in the Recycle Bin; the rest of the project is unchanged and no run was started.");
            SetStatus("Restart stopped", "#FF667A");
            Toast($"Restart stopped: {Path.GetFileName(failure.Path)} could not be recycled", "#FF667A");
            RefreshWorkerTab(force: true); // the edits were discarded above
            RefreshPrimary();
            return false;
        }
        var copied = LauncherLogic.InitializeProject(_workflowRoot, project, customConfigFile: LauncherLogic.EnsureWorkerConfig(_workflowRoot)); // PROJECT.md exists, so it is kept
        Log($"Project restarted: everything but PROJECT.md moved to the Recycle Bin. Added: {string.Join(", ", copied)}");
        Toast("Project restarted");
        HoldWorkerTab(() =>
        {
            PrefillCards();
            RefreshWorkerTab(force: true); // the tab was built from the config.json just replaced
        });
        RefreshPrimary();
        return true;
    }

    private DialogShell BuildRestartDialog(string project)
    {
        var names = LauncherLogic.RestartTargets(project).Select(Path.GetFileName).ToArray();
        var listed = string.Join(", ", names.Take(6)) + (names.Length > 6 ? $" and {names.Length - 6} more" : "");
        var text = $"Are you sure? This will move {names.Length} item{(names.Length == 1 ? "" : "s")} in {project} to the Recycle Bin ({listed}): " +
            "everything except PROJECT.md, including the generated code and the workflow records (STATE.md, TASK.md, RESULT.md, REVIEW.md, " +
            "ISSUES.md, HANDOFF.md, config.json and any saved checkpoint)." +
            (Directory.Exists(Path.Combine(project, ".git")) ? " Your .git folder is kept." : "") +
            "\n\nPROJECT.md is kept unchanged. The project is then initialized again from the template and a fresh run starts from the first task.";
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        var restart = new Button { Content = "Restart project", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        restart.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A)); // no danger style exists; the app's error colour
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsDefault = true, IsCancel = true };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { restart, cancel } });
        var dialog = new DialogShell(this, "Restart project", panel);
        restart.Click += (_, _) => dialog.DialogResult = true;
        return dialog;
    }

    private enum ExistingProjectMdChoice { Use, Replace, Cancel }

    private ExistingProjectMdChoice AskExistingProjectMd(string path)
    {
        if (_codeChoice is { } answer) // --ui-smoke
            return answer("PROJECT.md already exists") switch { "replace" => ExistingProjectMdChoice.Replace, "use" => ExistingProjectMdChoice.Use, _ => ExistingProjectMdChoice.Cancel };
        var (dialog, result) = BuildExistingProjectMdDialog(path);
        dialog.ShowDialog();
        return result();
    }

    private (DialogShell Dialog, Func<ExistingProjectMdChoice> Result) BuildExistingProjectMdDialog(string path)
    {
        var heading = "";
        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
            heading = File.ReadLines(path).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // details are optional
        TextBlock Line(string text, bool muted, double top)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, top, 0, 0) };
            if (muted)
            {
                block.FontSize = 11;
                block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            }
            return block;
        }

        var details = $"{path}  ({size:N0} bytes)" + (heading.Length > 0 ? $"\nStarts with: {heading[..Math.Min(heading.Length, 80)]}" : "");
        var panel = new StackPanel();
        panel.Children.Add(Line("This folder already has a PROJECT.md. Do you want to use it?", false, 0));
        panel.Children.Add(Line(details, true, 8));
        panel.Children.Add(Line("Replace keeps your current file as a PROJECT.md.bak backup, then installs " +
            "the PROJECT.md you selected.", true, 8));
        panel.Children.Add(Line("Using the existing file means the PROJECT.md you selected will not be used.", true, 4));

        var result = ExistingProjectMdChoice.Cancel;
        DialogShell? dialog = null;
        Button Make(string text, string style, ExistingProjectMdChoice value, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button
            {
                Content = text, Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel,
            };
            if (style.Length > 0)
                button.Style = (Style)FindResource(style);
            button.Click += (_, _) => { result = value; dialog!.Close(); };
            return button;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Make("Cancel", "GhostButton", ExistingProjectMdChoice.Cancel, isCancel: true));
        buttons.Children.Add(Make("Replace it", "", ExistingProjectMdChoice.Replace));
        buttons.Children.Add(Make("Use existing PROJECT.md", "PrimaryButton", ExistingProjectMdChoice.Use, isDefault: true));
        panel.Children.Add(buttons);
        dialog = new DialogShell(this, "PROJECT.md already exists", panel);
        return (dialog, () => result);
    }

    private void EditProject_Click(object sender, RoutedEventArgs e)
    {
        var projectFile = Path.Combine(SelectedProjectPath(), "PROJECT.md");
        if (!File.Exists(projectFile))
        {
            SetStatus("This folder has no PROJECT.md", "#F7C873");
            return;
        }
        if (_openProjectMd is { } open)
            open(projectFile);
        else
            Process.Start(new ProcessStartInfo(projectFile) { UseShellExecute = true });
    }

    private Action<string>? _openProjectMd; // --ui-smoke: records the file instead of opening an editor

    private string _projectPath = "";

    // The project folder is chosen with the folder picker; setting it refreshes everything that depends on it. Another
    // folder with unsaved edits to the worker tab asks first; keeping them keeps the project (callers that act before the
    // switch ask first themselves: DiscardWorkerEdits). The tab is rebuilt for the new project's config.
    private string ProjectPath
    {
        get => _projectPath;
        set
        {
            var switching = value != _projectPath;
            if (switching && !DiscardWorkerEdits())
                return;
            _projectPath = value;
            HoldWorkerTab(() =>
            {
                RefreshCodeFolder();
                PrefillCards();
                if (switching)
                    RefreshWorkerTab(force: true);
            });
            RefreshPrimary();
        }
    }

    private string SelectedProjectPath()
    {
        var path = _projectPath.Trim().Trim('"');
        if (path.Length == 0)
            return "";
        try
        {
            return Path.GetFullPath(path, _workflowRoot ?? Environment.CurrentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    // Bottom-right toasts slide and fade in and leave after about three seconds (hovering one holds it).
    // At most four are on screen; a fifth pushes out the oldest. No colour means the theme accent.
    private void Toast(string text, string? color = null)
    {
        while (ToastStack.Children.Count >= 4)
            ToastStack.Children.RemoveAt(0);
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        if (color is null)
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        else
            dot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        var shift = new TranslateTransform(0, 14);
        var toast = new Border
        {
            Child = row, Padding = new Thickness(13, 9, 16, 9), Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 480, Opacity = 0, RenderTransform = shift,
        };
        toast.SetResourceReference(Border.BackgroundProperty, "Card");
        toast.SetResourceReference(Border.BorderBrushProperty, "EdgeHighlight");
        toast.SetResourceReference(Border.CornerRadiusProperty, "SmallRadius");
        ToastStack.Children.Add(toast);
        var time = TimeSpan.FromMilliseconds(200);
        toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, time) { EasingFunction = Decelerate });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, time) { EasingFunction = Decelerate });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => ToastStack.Children.Remove(toast);
            toast.BeginAnimation(UIElement.OpacityProperty, fade);
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, TimeSpan.FromMilliseconds(180)));
        };
        timer.Start();
    }

    // Window shortcuts. They arrive after the focused control, so a key a text box uses itself never reaches here.
    // Preview, so a focused button (which clicks on Enter regardless of modifiers) cannot swallow a shortcut.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ||
            Keyboard.FocusedElement is Visual focused && WorkerSettingsHost.IsAncestorOf(focused)) // typing in the worker tab
            return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter when !shift && CodeStartButton.IsEnabled && !ChatView.IsVisible:
                StartCodeProject();
                break;
            case Key.X when shift && (_process is not null || _chatBusy) && Keyboard.FocusedElement is not (TextBox or PasswordBox):
                StopWork_Click(CodeStop, e);
                break;
            case Key.D1 or Key.NumPad1 when !shift:
                ShowView(chat: true);
                break;
            case Key.D2 or Key.NumPad2 when !shift:
                ShowView();
                break;
            case Key.D3 or Key.NumPad3 when !shift:
                OpenSettings();
                break;
            case Key.B when !shift:
                SetSidebar(!_settings.SidebarCollapsed);
                break;
            case Key.U when !shift && !_chatBusy && (ChatView.IsVisible || CodeChat.IsVisible):
                AddFiles(ChatView.IsVisible ? _chat : _code);
                break;
            case Key.E:
                SetAllExpanded(!shift);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusPill.Visibility = text == "Ready" ? Visibility.Collapsed : Visibility.Visible;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    // ---------------------------------------------------------------- worker cards

    private RoleCard? CardFor(string role) => _cards.FirstOrDefault(c => c.Role == role);

    // The card an event's activity lands on: an Implementer or Reviewer that is not added runs as MAIN, so MAIN shows it.
    private RoleCard? CardForActivity(string role) => CardFor(ConfigRole(role));

    // Saved order and user-created workers; the three built-in workers are always present.
    private void LoadWorkers()
    {
        // Built-ins first in Roles order (LEAD, IMPLEMENTER, REVIEWER, COMPACTOR), then the custom workers in the order added.
        var saved = _settings.Workers.Where(w => w.Id.Length > 0 && (w.Custom || Roles.Contains(w.Id))).ToList();
        // the test build groups them: LEAD and COMPACTOR (always on), then the helpers
        string[] order = AppSettings.Unified ? ["planner", "compactor", "implementer", "reviewer"] : Roles;
        foreach (var w in order.Select(r => saved.FirstOrDefault(x => x.Id == r && !x.Custom) ?? new WorkerSetting { Id = r }).Concat(saved.Where(x => x.Custom)))
        {
            var card = new RoleCard(w.Id, w.Custom ? w.Title : null, w.Custom) { ModelId = w.ModelId, ModelVariant = w.ModelVariant, Config = w.Clone() };
            if (w.Custom && w.ModelId is not null)
                card.SetModel(LauncherLogic.DescribeModel(w.ModelId, w.ModelVariant));
            _cards.Add(card);
        }
        RefreshSetupProblems();
        RefreshCardVisibility();
    }

    // Once: an implementer with a model of its own (LauncherLogic.HasOwnModel) picks the tri workflow (else dual), and the
    // saved presets are cleared for a fresh start. An unreadable config: nothing changes, next start retries.
    private void MigrateRoles()
    {
        if (_settings.RolesMigrated || LauncherLogic.RolesWithOwnModel(ActiveConfigPath()) is not { } added)
            return;
        _settings.WorkflowMode = added.Contains("implementer") ? "tri" : "dual";
        ShowWorkflowMode();
        _settings.Presets.Clear();
        _settings.ModelPresets.Clear();
        _settings.RolesMigrated = true;
        _settings.Save();
        RefreshCardVisibility();
    }

    // Cards of custom workers the orchestrator would ignore show a light warning.
    private void RefreshSetupProblems()
    {
        var workers = CurrentWorkers();
        var path = ActiveConfigPath();
        var problems = LauncherLogic.CustomProblems(workers, !File.Exists(path) || LauncherLogic.ImplementerHasBaseUrl(path));
        for (var i = 0; i < workers.Count; i++)
            if (CardFor(workers[i].Id) is { IsCustom: true } card)
                card.SetupProblem = problems[i];
    }

    private List<WorkerSetting> CurrentWorkers() => _cards.Select(c =>
    {
        var w = c.Config.Clone();
        w.Id = c.Role;
        w.Title = c.Title;
        w.Custom = c.IsCustom;
        w.ModelId = c.ModelId;
        w.ModelVariant = c.ModelVariant;
        return w;
    }).ToList();

    private void SaveWorkers()
    {
        _settings.Workers = CurrentWorkers();
        _settings.Save();
    }

    // The orchestrator reads custom workers from config.json; keep the active config (and the worker config) in step
    // with the cards. Returns false when the write failed. With collectSkipped the caller reports the skipped workers itself.
    // A project's not-added role with a model of its own keeps it (the user is told once) unless removing that role is the change.
    private readonly HashSet<string> _ownModelNotices = new(StringComparer.OrdinalIgnoreCase);

    private bool SyncCustomWorkers(List<string>? collectSkipped = null, string? removedRole = null)
    {
        RefreshSetupProblems();
        try
        {
            var skipped = Array.Empty<string>();
            var targets = WorkerConfigTargets();
            foreach (var path in targets)
            {
                var keepOwn = path != targets[^1] ? Roles.Skip(1).Where(r => r != removedRole) : []; // the worker config is always last
                foreach (var role in LauncherLogic.WriteRoleFallbacks(path, _settings.AddedRoles, keepOwn))
                    if (_ownModelNotices.Add($"{path}|{role}"))
                    {
                        var notice = $"{Path.GetFileName(Path.GetDirectoryName(path))}: its {RoleName(role)} has its own model; choose Lead + Implementer + Reviewer in Settings > Agents to use it";
                        Log(notice);
                        Toast(notice, "#F7C873");
                    }
                skipped = LauncherLogic.WriteCustomWorkers(path, CurrentWorkers(), Path.Combine(_workflowRoot ?? "", "config.json"));
            }
            if (collectSkipped is not null)
                collectSkipped.AddRange(skipped);
            else
                foreach (var reason in skipped)
                    Log($"Not written to config.json, so it will not take part: {reason}");
            RefreshEffort(); // a custom worker's model or reasoning budget may have changed
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log($"Could not write custom workers to config.json: {exception.Message}");
            SetStatus("Could not update config.json", "#FF667A");
            return false;
        }
    }

    // ---- presets: named sets of workers plus the built-in roles' config.json objects

    // The config worker changes go to (LauncherLogic.ActiveConfigPath); "" when there is none or it cannot be made.
    private string ActiveConfigPath()
    {
        try { return LauncherLogic.ActiveConfigPath(SelectedProjectPath(), _workflowRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not create the worker config: {e.Message}");
            return "";
        }
    }

    // Where a worker change is written: the active config, plus the worker config when the active one is a project's,
    // so the current setup carries to new projects.
    private string[] WorkerConfigTargets()
    {
        var active = ActiveConfigPath();
        if (active.Length == 0)
            return [];
        var worker = LauncherLogic.EnsureWorkerConfig(_workflowRoot!); // ActiveConfigPath already made it
        return string.Equals(active, worker, StringComparison.OrdinalIgnoreCase) ? [active] : [active, worker];
    }

    // Writes targets[0] (its problem, or exception, is the caller's), then mirrors the change to the rest. The project
    // already has it then, so a mirror that fails is only a warning and the save counts as done. write: a problem or null.
    private string? WriteConfigs(IReadOnlyList<string> targets, Func<string, string?> write)
    {
        if (write(targets[0]) is { } problem)
            return problem;
        foreach (var mirror in targets.Skip(1))
        {
            string? failed;
            try { failed = write(mirror); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException) { failed = e.Message; }
            if (failed is null)
                continue;
            Log($"Saved to the project; the launcher's worker config could not be updated: {failed}");
            Toast($"Saved to the project; the launcher's worker config could not be updated: {failed}", "#F7C873");
        }
        return null;
    }

    private void Presets_Click(object sender, RoutedEventArgs e)
    {
        if (_process is null)
            BuildPresetsMenu().IsOpen = true;
    }

    private ContextMenu NewPresetMenu(UIElement? target = null) =>
        new() { Style = (Style)FindResource(typeof(ContextMenu)), PlacementTarget = target ?? PresetsButton, Placement = PlacementMode.Bottom };

    private MenuItem PresetItem(string header, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource(typeof(MenuItem)), IsEnabled = enabled, Opacity = enabled ? 1 : 0.45 };
        item.Click += (_, _) => click();
        return item;
    }

    // Chat has its own presets (with the agents it shows); Code's are the workflow's.
    private List<WorkerPreset> PresetList => ChatShown ? _settings.ChatPresets : _settings.Presets;

    private ContextMenu BuildPresetsMenu()
    {
        var menu = NewPresetMenu();
        menu.Items.Add(PresetItem("+ New preset…", NewPresetPrompt));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        foreach (var preset in PresetList)
            menu.Items.Add(PresetItem(preset.Name, () => ApplyPreset(preset)));
        if (PresetList.Count == 0)
            menu.Items.Add(PresetItem("No saved presets", () => { }, enabled: false));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        menu.Items.Add(PresetItem("Save current as preset…", SavePresetPrompt));
        menu.Items.Add(PresetItem("Delete preset…", () => Dispatcher.InvokeAsync(DeletePresetMenu, DispatcherPriority.Background), PresetList.Count > 0));
        return menu;
    }

    // A second menu (the item template has no submenus) listing what can be deleted.
    private void DeletePresetMenu()
    {
        var menu = NewPresetMenu();
        foreach (var preset in PresetList)
            menu.Items.Add(PresetItem($"Delete  {preset.Name}", () =>
            {
                if (AskDialog("Delete preset", $"Delete the preset \"{preset.Name}\"?", "Delete") is null)
                    return;
                DeletePreset(preset);
            }));
        menu.IsOpen = true;
    }

    // Themed prompt: a name box when input is not null (empty names rejected), otherwise a plain confirmation.
    // Returns the entered text ("" for a confirmation), or null when cancelled.
    private string? AskDialog(string title, string message, string okLabel, string? input = null, int maxLength = 60, Func<string, string?>? validate = null)
    {
        var (dialog, result) = BuildAskDialog(title, message, okLabel, input, maxLength, validate);
        dialog.ShowDialog();
        return result();
    }

    private (DialogShell Dialog, Func<string?> Result) BuildAskDialog(string title, string message, string okLabel, string? input, int maxLength = 60, Func<string, string?>? validate = null)
    {
        var box = input is null ? null : new TextBox { Text = input, MaxLength = maxLength, Margin = new Thickness(0, 10, 0, 0) };
        var error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A)), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var ok = new Button { Content = okLabel, Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 5, 16, 5), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        if (box is not null)
            panel.Children.Add(box);
        panel.Children.Add(error);
        panel.Children.Add(buttons);
        var dialog = new DialogShell(this, title, panel);
        ok.Click += (_, _) =>
        {
            if (box is not null && box.Text.Trim().Length == 0)
            {
                error.Text = "Enter a name.";
                error.Visibility = Visibility.Visible;
                return;
            }
            if (box is not null && validate?.Invoke(box.Text) is { } problem)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                return;
            }
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) =>
        {
            box?.Focus();
            box?.SelectAll();
        };
        return (dialog, () => dialog.DialogResult == true ? box?.Text.Trim() ?? "" : null);
    }

    private bool SamePreset(WorkerPreset preset, string name) => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase);

    private void SavePresetPrompt()
    {
        if (_process is not null || AskDialog("Save preset", "Name this set of workers and their settings.", "Save", "") is not { } name)
            return;
        if (PresetList.Any(p => SamePreset(p, name)) &&
            AskDialog("Overwrite preset", $"A preset named \"{name}\" already exists. Replace it?", "Overwrite") is null)
            return;
        SavePreset(name);
    }

    // "+ New preset…": a fresh template to edit: the built-in workers with the app's default settings (the template
    // config.json), no custom workers (the workflow mode stays), saved under its name and applied. The current workers are kept only
    // in a preset they were saved to.
    private void NewPresetPrompt()
    {
        if (_process is not null || _workflowRoot is null || AskDialog("New preset",
                "Name the new preset. It starts from the default workers and settings; edit them, then Save current as preset to keep your changes.",
                "Create", "") is not { } name)
            return;
        if (PresetList.Any(p => SamePreset(p, name)) &&
            AskDialog("Overwrite preset", $"A preset named \"{name}\" already exists. Replace it?", "Overwrite") is null)
            return;
        NewPreset(name);
    }

    private void NewPreset(string name)
    {
        WorkerPreset preset;
        try
        {
            preset = LauncherLogic.CapturePreset(name, Path.Combine(_workflowRoot ?? "", "config.json"), Roles.Select(r => new WorkerSetting { Id = r }));
            if (ChatShown)
                preset.ChatCards = []; // a new Chat setup: LEAD alone
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log($"Could not read the default config.json: {exception.Message}");
            SetStatus("Could not read the default config.json", "#FF667A");
            return;
        }
        var index = PresetList.FindIndex(p => SamePreset(p, name));
        if (index >= 0)
            PresetList[index] = preset;
        else
            PresetList.Add(preset);
        _settings.Save();
        ApplyPreset(preset);
    }

    private void SavePreset(string name)
    {
        WorkerPreset preset;
        try
        {
            preset = LauncherLogic.CapturePreset(name, ActiveConfigPath(), CurrentWorkers());
            if (ChatShown)
                preset.ChatCards = _settings.ChatCards.ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log($"Could not read config.json: {exception.Message}");
            SetStatus("Could not read config.json", "#FF667A");
            return;
        }
        var index = PresetList.FindIndex(p => SamePreset(p, name));
        if (index >= 0)
            PresetList[index] = preset;
        else
            PresetList.Add(preset);
        _settings.Save();
        Log($"Preset saved: {name}");
        Toast($"Preset saved: {name}");
    }

    private void ApplyPreset(WorkerPreset preset)
    {
        if (_process is not null)
            return;
        if (!DiscardWorkerEdits())
            return;
        var keepRole = _shownWorker?.Role;
        bool rolesApplied;
        try
        {
            var targets = WorkerConfigTargets();
            rolesApplied = targets.Length > 0 && WriteConfigs(targets, path => LauncherLogic.ApplyPresetRoles(path, preset) ? null : $"{path} is missing") is null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log($"Could not update config.json: {exception.Message}");
            SetStatus("Could not update config.json", "#FF667A");
            RefreshWorkerTab(force: true); // the edits were discarded above
            return;
        }
        _settings.Workers = preset.Workers;
        if (preset.ChatCards is { } chatCards) // a Chat preset: the agents Chat shows
            _settings.ChatCards = chatCards.ToList();
        HoldWorkerTab(() => // one build of the tab, not one per step
        {
            _cards.Clear();
            LoadWorkers(); // keeps only valid entries and re-adds any missing built-in worker
            SaveWorkers();
            SyncCustomWorkers();
            PrefillCards();
            SelectWorker(_cards.FirstOrDefault(c => c.Role == keepRole) ?? FirstVisibleWorker());
            RefreshWorkerTab(force: true); // the restored settings, also when the same worker stays open
        });
        Log($"Preset applied: {preset.Name}");
        if (preset.Roles.Count > 0 && !rolesApplied)
        {
            SetStatus("Built-in model settings were not changed", "#F7C873");
            Toast($"Preset applied: {preset.Name}. Built-in model settings could not be written.", "#F7C873");
        }
        else
            Toast($"Preset applied: {preset.Name}");
    }

    private void DeletePreset(WorkerPreset preset)
    {
        PresetList.Remove(preset);
        _settings.Save();
        Log($"Preset deleted: {preset.Name}");
        Toast($"Preset deleted: {preset.Name}");
    }

    // "+": a custom worker (the built-in Implementer and Reviewer follow the workflow mode, Settings > Agents).
    private void AddWorker_Click(object sender, RoutedEventArgs e) => BuildAddWorkerMenu((FrameworkElement)sender).IsOpen = true;

    private ContextMenu BuildAddWorkerMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        if (ChatShown) // Chat: which agents it shows besides LEAD, checked when shown
        {
            foreach (var card in _cards.Where(c => c.Role != "planner"))
            {
                var item = new MenuItem { Header = ChatName(card, card.Role), IsCheckable = true, IsChecked = _settings.ChatCards.Contains(card.Role),
                    Tag = "chat:" + card.Role, InputGestureText = card.ModelName, ToolTip = "Show this agent in Chat; its model and settings are the same as in Code" };
                item.Click += (_, _) => SetChatCard(card.Role, item.IsChecked);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        }
        var custom = new MenuItem { Header = "Custom worker…", ToolTip = "Add a worker with a custom title and model", Tag = "custom" };
        custom.Click += (_, _) => AddCustomWorker();
        menu.Items.Add(custom);
        return menu;
    }

    private void SetChatCard(string role, bool shown)
    {
        _settings.ChatCards.Remove(role);
        if (shown)
            _settings.ChatCards.Add(role);
        _settings.Save();
        RefreshCardVisibility();
        RefreshChatTarget();
        if (shown && CardFor(role) is { } card)
            SelectWorker(card);
    }

    private void AddCustomWorker()
    {
        var n = 1;
        while (_cards.Any(c => string.Equals(c.Title, $"WORKER {n}", StringComparison.OrdinalIgnoreCase)))
            n++;
        var card = new RoleCard(Guid.NewGuid().ToString("N"), $"WORKER {n}", custom: true);
        _cards.Add(card);
        if (ChatShown) // added in Chat: shown there
            _settings.ChatCards.Add(card.Role);
        SaveWorkers();
        RefreshSetupProblems();
        SelectWorker(card);
    }

    // Settings > Agents > Workflow. Refused while a reply or run is using the models (that turn already chose its workers).
    private void Workflow_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        var mode = WorkflowTriRadio.IsChecked == true ? "tri" : "dual";
        if (mode == _settings.WorkflowMode)
            return;
        if (_process is not null || _chatBusy)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            ShowWorkflowMode(); // re-enters here with nothing to change
            return;
        }
        SetWorkflow(mode);
    }

    private void ShowWorkflowMode() => (_settings.WorkflowMode == "tri" ? WorkflowTriRadio : WorkflowDualRadio).IsChecked = true;

    // tri gives the Implementer its own roles.implementer entry back (or a copy of the planner's) and its tab; dual runs it
    // as the planner again ("same_as") and hides it. The Reviewer is in both.
    private void SetWorkflow(string mode)
    {
        _settings.WorkflowMode = mode;
        _settings.Save();
        ShowWorkflowMode();
        HoldWorkerTab(() =>
        {
            SyncCustomWorkers(removedRole: mode == "tri" ? null : "implementer");
            RefreshCardVisibility(); // a hidden tab that was shown hands over to LEAD
            PrefillCards();
        });
    }

    private void RemoveWorker(RoleCard card)
    {
        if (card.IsCustom)
        {
            _cards.Remove(card);
            SaveWorkers();
            SyncCustomWorkers();
        }
    }

    // Same rules as the standard card's title box: custom workers only, at most 32 characters, never empty,
    // never a reserved built-in title or another worker's title. Returns the problem (nothing changed) or null.
    private string? RenameTo(RoleCard card, string title)
    {
        if (LauncherLogic.TitleProblem(title, card.Role, CurrentWorkers()) is { } problem)
            return problem;
        card.Title = title.Trim();
        SaveWorkers();
        SyncCustomWorkers();
        return null;
    }

    // ---- model assignment

    // File: auto_load.gguf_file, for a llama.cpp model outside the Hugging Face cache.
    private sealed record ModelChoice(string Label, string Display, string LoadPath, string? Variant, string Expected, string? File = null);

    // A custom worker's model (kept in the launcher's settings); built-in roles write theirs in ModelSettingsBody's Save.
    private void AssignModel(RoleCard card, ModelChoice choice)
    {
        card.ModelId = choice.Display;
        card.ModelVariant = choice.Variant;
        card.SetModel(LauncherLogic.DescribeModel(choice.Display, choice.Variant));
        card.Config.ModelPath = choice.LoadPath;
        card.Config.ModelExpected = choice.Expected;
        card.Config.GgufFile = choice.File;
        Log($"{card.Title}: model set to {choice.Label}");
        Toast($"{card.Title}: {choice.Label}");
        SaveWorkers();
        SyncCustomWorkers();
        RefreshEffort();
    }

    private static void SetRoleModel(JsonObject settings, ModelChoice choice)
    {
        {
            var load = (settings["auto_load"] ??= new JsonObject()).AsObject();
            load["model_path"] = choice.LoadPath;
            if (choice.Variant is null)
                load.Remove("gguf_variant");
            else
                load["gguf_variant"] = choice.Variant;
            if (choice.File is null)
                load.Remove("gguf_file");
            else
                load["gguf_file"] = choice.File;
            settings["expected_model_contains"] = choice.Expected;
            settings["display_name"] = choice.Display; // the launcher's label; load paths for local models are opaque ids
        }
    }

    // Installed GGUF models: the files the engine itself would find (models folder and Hugging Face cache), no server needed.
    private async Task<List<ModelChoice>> FetchModelChoicesAsync() =>
        (await Task.Run(() => LauncherLogic.FindLocalGgufs(_settings.ModelsFolder, LauncherLogic.HubCache())))
            .Select(g =>
            {
                if (g.Repo.Length > 0)
                {
                    var tail = g.Repo.Split('/')[^1];
                    return new ModelChoice($"{tail}  ·  {(g.Variant.Length > 0 ? g.Variant : Path.GetFileName(g.File))}", g.Repo, g.Repo, g.Variant.Length > 0 ? g.Variant : null, tail);
                }
                var name = Path.GetFileNameWithoutExtension(g.File);
                return new ModelChoice(name, name, name, g.Variant.Length > 0 ? g.Variant : null, name, g.File);
            }).DistinctBy(c => c.Label).OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();

    // ---------------------------------------------------------------- navigation, theme, window chrome

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        var tag = ((FrameworkElement)sender).Tag as string;
        ShowView(chat: tag == "Chat");
    }

    // Not chat: the Code view.
    private void ShowView(bool chat = false)
    {
        var code = !chat;
        NavCode.IsChecked = code;
        NavChat.IsChecked = chat;
        RefreshCardVisibility(); // the run output skips RefreshChatTarget below
        Reveal(CodeView, code);
        Reveal(ChatView, chat);
        RefreshSidebarList(); // the highlighted row is the open chat of the view shown
        RefreshContextSection();
        ApplyPromptView();
        if (chat || code)
        {
            RefreshChatTarget();
            (chat ? ChatInput : CodeInput).Focus();
        }
    }

    // ---- the Settings window (the sidebar's gear, Ctrl+3): it borrows SettingsContent from its hidden holder while open

    private DialogShell? _settingsWindow;
    private Action<DialogShell>? _settingsShow; // --ui-smoke: shows it without blocking (ShowDialog otherwise)

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // focus: the control to put the cursor in, such as the API key box when a run needs the key.
    private void OpenSettings(IInputElement? focus = null)
    {
        if (_settingsWindow is { } open)
        {
            open.Activate();
            focus?.Focus();
            return;
        }
        if (focus is Visual target && SettingsPages.Children.OfType<FrameworkElement>().FirstOrDefault(p => target.IsDescendantOf(p)) is { Tag: string tab })
            ShowSettingsTab(tab);
        SettingsHolder.Child = null;
        var window = _settingsWindow = new DialogShell(this, null, SettingsContent, large: true);
        window.Loaded += (_, _) => focus?.Focus();
        window.Closed += (_, _) =>
        {
            ((Panel)SettingsContent.Parent).Children.Remove(SettingsContent);
            SettingsHolder.Child = SettingsContent;
            _settingsWindow = null;
        };
        if (_settingsShow is { } show)
            show(window);
        else
            window.ShowDialog();
    }

    // A tab on the Settings window's left shows the page with its Tag; the others collapse.
    private void SettingsTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tab })
            ShowSettingsTab(tab);
    }

    private void ShowSettingsTab(string tab)
    {
        if (SettingsPages is null) // the first tab's IsChecked, while the window is still being built
            return;
        foreach (var page in SettingsPages.Children.OfType<FrameworkElement>())
            page.Visibility = (string)page.Tag == tab ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in SettingsTabs.Children.OfType<RadioButton>())
            button.IsChecked = (string)button.Tag == tab;
        SettingsScroll.ScrollToTop();
    }

    // ---- collapsible sidebar

    private const double SidebarWidth = 260, RailWidth = 54;

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => SetSidebar(!_settings.SidebarCollapsed);

    // The pane's width slides between the full sidebar and the icon rail while the two layouts cross-fade.
    private void SetSidebar(bool collapsed, bool animate = true)
    {
        _settings.SidebarCollapsed = collapsed;
        var focusMoves = animate && SidePane.IsKeyboardFocusWithin;
        if (animate)
            _settings.Save();
        var width = collapsed ? RailWidth : SidebarWidth;
        if (animate)
        {
            Reveal(SidebarFull, !collapsed);
            Reveal(SidebarRail, collapsed);
            SidePane.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(240)) { EasingFunction = Decelerate });
            // The focused button is going away; keep keyboard users on the opposite toggle.
            if (focusMoves)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => (collapsed ? SidebarExpandButton : SidebarCollapseButton).Focus());
            return;
        }
        SidePane.BeginAnimation(WidthProperty, null);
        SidePane.Width = width;
        SidebarFull.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarRail.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    // The details pane may grow, but never past what leaves the sidebar and a usable output column on screen.
    // The details column's least width: a third of this monitor's work area, at least 260, and less when the window is too
    // narrow to leave the work area its 380 beside it.
    private double DetailsMin() => Math.Max(260, Math.Min(WorkAreaOf(this).Width / 3, ActualWidth - SidePane.ActualWidth - 380 - 6));

    private void FitDetailsColumn()
    {
        if (DetailsColumn.MinWidth > 0) // open (0: the rail, or a slide under way)
            DetailsColumn.MinWidth = DetailsMin();
        DetailsColumn.MaxWidth = Math.Max(DetailsColumn.MinWidth, ActualWidth - SidePane.ActualWidth - 380 - 6);
    }

    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings.DetailsWidth = DetailsColumn.ActualWidth;
        _settings.Save();
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.Theme = ThemeParchmentDarkRadio.IsChecked == true ? "ParchmentDark" : ThemeParchmentRadio.IsChecked == true ? "Parchment" : ThemeGraphiteRadio.IsChecked == true ? "Graphite" : ThemeOledRadio.IsChecked == true ? "Oled" : ThemeGlassRadio.IsChecked == true ? "Glass" : ThemeGoldRadio.IsChecked == true ? "Gold" : "Flat";
        ApplyTheme();
        _settings.Save();
    }

    private void ApplyTheme()
    {
        var glass = _settings.Theme == "Glass";
        var oled = _settings.Theme == "Oled";
        var graphite = _settings.Theme == "Graphite";
        var parchment = _settings.Theme == "Parchment"; // the one light theme
        var clay = _settings.Theme == "ParchmentDark"; // Parchment's dark variant: near-neutral grays; the accent tint adds the clay
        var flat = _settings.Theme == "Flat" || oled || graphite || parchment || clay; // these have the Flat shapes
        // Surfaces lean slightly toward the accent (more on raised ones), so each accent colours the whole window a little.
        // OLED's black background stays pure black.
        var tint = (Color)ColorConverter.ConvertFromString(AccentColors.First(x => x.Key == AccentKey).Accent)!;
        void Surface(string key, string color, double amount)
        {
            var c = (Color)ColorConverter.ConvertFromString(color)!;
            byte Mix(byte from, byte to) => (byte)Math.Round(from + (to - from) * amount);
            Resources[key] = new SolidColorBrush(Color.FromArgb(c.A, Mix(c.R, tint.R), Mix(c.G, tint.G), Mix(c.B, tint.B)));
        }
        // Graphite: Claude's dark palette (its stylesheet: bg-200 for the window, bg-100 for the sidebar, bg-000 for cards,
        // inputs and popups, text-100, text-400, border-100 at 15%), made neutral gray at the same brightness, untinted.
        // Parchment: warm off-white paper (its background stays exactly #F4F3EE), white cards, near-black text.
        Surface("AppBackground", clay ? "#1E1C1B" : parchment ? "#F4F3EE" : graphite ? "#1E1E1E" : oled ? "#000000" : flat ? "#101116" : glass ? "#E6101522" : "#0A0A0C", oled || parchment || graphite ? 0 : 0.035);
        // Popup windows sit a shade off the window: lighter on the dark themes, darker on Parchment (Glass keeps its translucency).
        var back = ((SolidColorBrush)Resources["AppBackground"]).Color;
        var (toward, share) = parchment ? ((byte)0, 0.035) : ((byte)255, 0.07);
        byte Shade(byte from) => (byte)Math.Round(from + (toward - from) * share);
        Resources["DialogBackground"] = graphite ? new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)) : new SolidColorBrush(Color.FromArgb(back.A, Shade(back.R), Shade(back.G), Shade(back.B)));
        // Inputs on a popup: set apart from its shade, darker than it on the dark themes (the window colour), white on Parchment.
        Resources["DialogInput"] = parchment ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(255, back.R, back.G, back.B));
        SetThemeBrush("TextPrimary", clay ? "#EFEBE8" : parchment ? "#1F1E1D" : graphite ? "#F9F9F9" : oled ? "#EEEEF5" : flat ? "#ECEEF5" : glass ? "#F4F7FF" : "#E8E8F0");
        Surface("TitleBackground", clay ? "#181716" : parchment ? "#EDEBE4" : graphite ? "#1E1E1E" : oled ? "#000000" : flat ? "#101116" : glass ? "#DD111827" : "#0D0D10", oled || graphite ? 0 : 0.035);
        Surface("Panel", clay ? "#181716" : parchment ? "#EDEBE4" : graphite ? "#1E1E1E" : oled ? "#08080A" : flat ? "#16171D" : glass ? "#CC182131" : "#111116", graphite ? 0 : 0.045);
        // The left sidebar: the Panel shade, except Graphite's, which is lighter than its chat area.
        Surface("NavBackground", clay ? "#232120" : parchment ? "#EDEBE4" : graphite ? "#262626" : oled ? "#08080A" : flat ? "#16171D" : glass ? "#CC182131" : "#111116", graphite ? 0 : 0.045);
        Surface("Card", clay ? "#2A2726" : parchment ? "#FFFFFF" : graphite ? "#303030" : oled ? "#131217" : flat ? "#1E2028" : glass ? "#B8233043" : "#16161C", graphite ? 0 : clay ? 0.015 : 0.06); // Dark Parchment: near-neutral greys, as Claude's
        Surface("Hover", clay ? "#33302E" : parchment ? "#E6E3DA" : graphite ? "#303030" : oled ? "#221E2E" : flat ? "#292C36" : glass ? "#B53A4B66" : "#1E1E26", graphite ? 0 : clay ? 0.02 : 0.09);
        Surface("Border", clay ? "#3A3634" : parchment ? "#DCD8CC" : graphite ? "#3B3B3B" : oled ? "#26232F" : flat ? "#242630" : glass ? "#7795A8C8" : "#2A2A35", graphite ? 0 : clay ? 0.02 : 0.08);
        Surface("ControlBorder", flat ? "#00000000" : glass ? "#7795A8C8" : "#2A2A35", clay ? 0.02 : 0.08);
        SetThemeBrush("Muted", clay ? "#A39C97" : parchment ? "#6F6D66" : graphite ? "#9A9A9A" : oled ? "#8A8798" : flat ? "#8E93A6" : glass ? "#BAC7DA" : "#8888A0");
        // A card under the mouse (the Context cards): the card a step toward the text colour, brighter on dark themes
        {
            var (card, text) = (((SolidColorBrush)Resources["Card"]).Color, ((SolidColorBrush)Resources["TextPrimary"]).Color);
            byte Step(byte from, byte to) => (byte)Math.Round(from + (to - from) * 0.06);
            Resources["CardHover"] = new SolidColorBrush(Color.FromArgb(card.A, Step(card.R, text.R), Step(card.G, text.G), Step(card.B, text.B)));
        }
        // Top-edge rim of raised surfaces: white fading out over the first 24 px, stronger on translucent Glass.
        var rim = (byte)(parchment || graphite ? 0 : glass ? 0x50 : oled ? 0x18 : flat ? 0x22 : 0x1A); // Graphite: flat, as Claude's
        // Code colours: VS Code's light palette on Parchment, its dark one elsewhere; inline code sits on a chip of the opposite shade.
        foreach (var (key, dark, light) in new[] { ("CodeComment", "#6A9955", "#008000"), ("CodeString", "#CE9178", "#A31515"), ("CodeNumber", "#B5CEA8", "#098658"),
                     ("CodeKeyword", "#569CD6", "#0000FF"), ("CodeControl", "#C586C0", "#AF00DB"), ("CodeCall", "#DCDCAA", "#795E26"), ("CodeType", "#4EC9B0", "#267F99"),
                     ("InlineCode", "#33FFFFFF", "#14000000") })
            SetThemeBrush(key, parchment ? light : dark);
        SetBrush("EdgeHighlight", new LinearGradientBrush(Color.FromArgb(rim, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0, 24))
            { MappingMode = BrushMappingMode.Absolute });
        ApplyAccent();
        Resources["ControlRadius"] = new CornerRadius(flat ? 8 : 7); // buttons, toggles, inputs: softly rounded, not pills
        Resources["CardRadius"] = new CornerRadius(flat ? 14 : 5);
        Resources["PanelRadius"] = new CornerRadius(flat ? 14 : 9);
        Resources["SmallRadius"] = new CornerRadius(flat ? 10 : 6);
        ThemeBackdrop.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
    }

    // Accent colours selectable for every theme: key, label, accent, light accent, text on the accent.
    private static readonly (string Key, string Label, string Accent, string Light, string On)[] AccentColors =
    {
        ("Green", "Green", "#3DDC84", "#8EEBB4", "#07140C"),
        ("Aqua", "Aqua", "#1FC8C8", "#86E8E4", "#061314"),
        ("Purple", "Purple", "#9B6BFF", "#C0A3FF", "#FFFFFF"),
        ("LightBlue", "Light blue", "#6EA8FF", "#BDD7FF", "#101522"),
        ("Navy", "Navy blue", "#2B4FB0", "#6F8FE0", "#FFFFFF"),
        ("Gold", "Track gold", "#D4A017", "#F0C040", "#0A0A0C"),
        ("Blue", "Flat blue", "#4C8DFF", "#86B3FF", "#FFFFFF"),
        ("Pink", "Pink", "#FF6FB5", "#FFA6D2", "#1A0A12"),
        ("Orange", "Orange", "#FF8A3D", "#FFB880", "#1A0D04"),
        ("Silver", "Light gray", "#C0C0C0", "#DCDCDC", "#141414"), // Claude's text-200, border-100 and bg-300, made neutral
        ("Peru", "Peru", "#CF8E4E", "#E6B587", "#1A0F05"),
        ("Clay", "Clay", "#C15F3C", "#D97757", "#FFFFFF"),
    };

    private string AccentKey => _settings.ThemeAccents.TryGetValue(_settings.Theme, out var key) && AccentColors.Any(x => x.Key == key)
        ? key
        : _settings.Theme switch { "Gold" => "Gold", "Glass" => "LightBlue", "Oled" => "Purple", "Graphite" => "Silver", "Parchment" or "ParchmentDark" => "Clay", _ => "Blue" };

    private void ApplyAccent()
    {
        var accent = AccentColors.First(x => x.Key == AccentKey);
        SetThemeBrush("Accent", accent.Accent);
        // AccentLight colours text and outlines; on the light theme it must be darker than the accent, not lighter.
        var accentColor = (Color)ColorConverter.ConvertFromString(accent.Accent)!;
        if (_settings.Theme == "Parchment")
            Resources["AccentLight"] = new SolidColorBrush(Color.FromRgb((byte)(accentColor.R * 0.82), (byte)(accentColor.G * 0.82), (byte)(accentColor.B * 0.82)));
        else
            SetThemeBrush("AccentLight", accent.Light);
        SetThemeBrush("OnAccent", accent.On);
        AccentLabel.Text = accent.Label;
        var strong = (Color)ColorConverter.ConvertFromString(accent.Accent)!;
        var soft = (Color)ColorConverter.ConvertFromString(accent.Light)!;
        GlassA1.Color = Color.FromArgb(0x70, strong.R, strong.G, strong.B);
        GlassA2.Color = Color.FromArgb(0x00, strong.R, strong.G, strong.B);
        GlassB1.Color = Color.FromArgb(0x50, soft.R, soft.G, soft.B);
        GlassB2.Color = Color.FromArgb(0x00, soft.R, soft.G, soft.B);

        static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        // Primary fill: lifted a third of the way toward the light accent at the top-left, so text contrast holds.
        var lifted = Color.FromRgb((byte)((strong.R * 2 + soft.R) / 3), (byte)((strong.G * 2 + soft.G) / 3), (byte)((strong.B * 2 + soft.B) / 3));
        SetBrush("AccentGradient", new LinearGradientBrush(lifted, strong, new Point(0, 0), new Point(1, 1)));
        SetBrush("AccentEdge", new LinearGradientBrush(Alpha(soft, 0xC0), Alpha(strong, 0x30), 90));
        SetBrush("AccentGlow", new RadialGradientBrush(Alpha(strong, 0x66), Alpha(strong, 0)));
        SetBrush("ShimmerBrush", new LinearGradientBrush(
            new GradientStopCollection { new(Alpha(soft, 0), 0), new(soft, 0.5), new(Alpha(soft, 0), 1) }, 0));
    }

    private void SetBrush(string key, Brush brush)
    {
        brush.Freeze();
        Resources[key] = brush;
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        AccentMenu.PlacementTarget = AccentButton;
        AccentMenu.IsOpen = true;
    }

    private void AccentMenu_Opened(object sender, RoutedEventArgs e)
    {
        AccentMenu.Items.Clear();
        foreach (var color in AccentColors)
        {
            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 12, Height = 12, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Accent)!),
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(swatch);
            header.Children.Add(new TextBlock { Text = color.Label });
            var item = new MenuItem { Header = header, Tag = color.Key, IsChecked = color.Key == AccentKey };
            item.Click += AccentItem_Click;
            AccentMenu.Items.Add(item);
        }
    }

    private void AccentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key })
            return;
        _settings.ThemeAccents[_settings.Theme] = key;
        ApplyTheme(); // the surfaces carry a tint of the accent too
        _settings.Save();
    }

    private void SetThemeBrush(string key, string color) =>
        Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);

    // Reopen where the user left the window, as long as it would still be reachable on the current screens.
    private void RestoreWindowBounds()
    {
        if (_settings.WindowLeft is not { } left || _settings.WindowTop is not { } top ||
            _settings.WindowWidth is not { } width || _settings.WindowHeight is not { } height)
            return;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        if (width < MinWidth || height < MinHeight || left + width < screenLeft + 100 || left > screenRight - 100 ||
            top < screenTop - 5 || top > screenBottom - 60)
            return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (left, top, width, height);
        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!DiscardWorkerEdits())
        {
            e.Cancel = true; // the user kept the worker tab's unsaved edits
            return;
        }
        _tunnel.Stop();
        StopRemoteServer();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height }.All(double.IsFinite))
            (_settings.WindowLeft, _settings.WindowTop, _settings.WindowWidth, _settings.WindowHeight) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (_process is { HasExited: false } p)
        {
            var activity = StopActivity();
            p.Kill(entireProcessTree: true);
            p.WaitForExit(2000); // let it finish so it cannot overwrite the note
            RecordStopNote(activity);
        }
        if (_chatProcess is { HasExited: false } chat)
        {
            DrainLines(); // the reply's last output first
            SaveInterruptedTurn();
            chat.Kill(entireProcessTree: true);
            chat.WaitForExit(2000);
        }
        CleanupChatTemp(); // a reply cut off by closing leaves its conversation and temp config behind otherwise
        DrainLines(); // a loading event that arrived just before the kill still counts
        _llamaDownload?.Cancel();
        StopLocalServer();
        _settings.Save();
    }

    private Point? _maximizedPress; // a title-bar press on the maximized window that may still become a drag (title-bar coordinates)

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            EndMaximizedPress();
            ToggleMaximize();
        }
        else if (WindowState == WindowState.Maximized)
        {
            // DragMove does nothing while maximized, and a click or double-click must not restore: wait for the press to move.
            _maximizedPress = e.GetPosition(TitleBar);
            TitleBar.CaptureMouse();
        }
        else if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_maximizedPress is not { } press)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndMaximizedPress();
            return;
        }
        var at = e.GetPosition(TitleBar);
        if (Math.Abs(at.X - press.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(at.Y - press.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        EndMaximizedPress();
        RestoreForDrag(TitleBar.PointToScreen(at), at);
        DragMove(); // the same press carries on moving the restored window
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndMaximizedPress();

    private void EndMaximizedPress()
    {
        _maximizedPress = null;
        TitleBar.ReleaseMouseCapture();
    }

    // Restore a maximized window under the cursor (screen pixels; inBar: its spot in the title bar) at the size it had
    // before maximizing. Called once a maximized title-bar press starts moving, and directly by --ui-smoke.
    internal void RestoreForDrag(Point cursor, Point inBar)
    {
        var fromDevice = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromPoint(new NativePoint { X = (int)cursor.X, Y = (int)cursor.Y }, 2 /* nearest */), ref monitor);
        var work = new Rect(fromDevice.Transform(new Point(monitor.Work.Left, monitor.Work.Top)),
            fromDevice.Transform(new Point(monitor.Work.Right, monitor.Work.Bottom)));
        var bounds = LauncherLogic.DragRestoreBounds(fromDevice.Transform(cursor), inBar, TitleBar.ActualWidth, TitleBar.ActualHeight,
            RestoreBounds, work, new Size(_settings.WindowWidth ?? 1400, _settings.WindowHeight ?? 860));
        WindowState = WindowState.Normal;
        (Left, Top, Width, Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private struct NativePoint { public int X, Y; }

    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

#pragma warning disable CS0649 // filled in by Windows through marshalling
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
#pragma warning restore CS0649

    // A borderless (WindowStyle None) window maximizes over its whole monitor, taskbar included. WM_GETMINMAXINFO limits the
    // maximized size and position to the monitor's work area, so the window ends on top of the taskbar.
    // ponytail: an auto-hidden taskbar has no work-area strip, so it cannot pop up over a maximized window; reserve 1-2 px if that matters.
    private IntPtr LimitMaximizeToWorkArea(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (msg != WmGetMinMaxInfo || !GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* nearest */), ref info))
            return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new NativePoint { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        limits.MaxSize = new NativePoint { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        Marshal.StructureToPtr(limits, lParam, false);
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // The work area (device-independent units) of the monitor nearest a shown window; the primary one's before it is shown.
    internal static Rect WorkAreaOf(Window window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (PresentationSource.FromVisual(window)?.CompositionTarget is not { } target
            || !GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(window).Handle, 2 /* nearest */), ref info))
            return SystemParameters.WorkArea;
        var fromDevice = target.TransformFromDevice;
        return new Rect(fromDevice.Transform(new Point(info.Work.Left, info.Work.Top)), fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}

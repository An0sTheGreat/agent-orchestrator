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
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AgentOrchestrator;

public partial class MainWindow : Window
{
    private static readonly string[] Roles = ["planner", "implementer", "reviewer"];

    private Process? _process;
    private readonly string? _workflowRoot;
    private string? _customProjectFile;
    private string? _customConfigFile;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<RoleCard> _cards = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
    private readonly DispatcherTimer _pump = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Queue<StreamBlock> _pendingTools = new();
    private readonly HttpClient _httpLong = new() { Timeout = TimeSpan.FromSeconds(25) };
    private StreamBlock? _stream;
    private readonly HashSet<(string Url, string Path)> _loadedByRun = new(); // only these are unloaded on Stop/close, each on the server it loaded on
    private bool _followTail = true, _stopRequested;
    private readonly StringBuilder _log = new(), _transcript = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _activeRole, _blockRole;
    // TASK TIME / TURN TIME in the Model Output header; _clock ticks only while one of them runs.
    private readonly Stopwatch _taskWatch = new(), _turnWatch = new();
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) }; // Background starves while output streams
    private int _logLines;
    private bool _initializing = true, _polling;
    private double _runStart;
    private string _apiBase = "http://127.0.0.1:8888", _runKey = "";

    public MainWindow()
    {
        InitializeComponent();
        LoadWorkers();
        ApplyCompact();
        CardsList.ItemsSource = _cards;
        _pump.Tick += (_, _) => DrainLines();
        _clock.Tick += (_, _) => ShowClocks();
        SizeChanged += (_, _) => FitDetailsColumn();
        Loaded += (_, _) => FitDetailsColumn();
        _workflowRoot = LauncherLogic.FindWorkflowRoot();

        DetailsColumn.Width = new GridLength(Math.Clamp(_settings.DetailsWidth, 260, 1400));
        OnceToggle.IsChecked = _settings.Once;
        SetSidebar(_settings.SidebarCollapsed, animate: false);
        SidePane.SizeChanged += (_, _) => FitDetailsColumn();
        _settings.Theme = _settings.Theme is "Glass" or "Gold" or "Oled" ? _settings.Theme : "Flat";
        ThemeFlatRadio.IsChecked = _settings.Theme == "Flat";
        ThemeGoldRadio.IsChecked = _settings.Theme == "Gold";
        ThemeGlassRadio.IsChecked = _settings.Theme == "Glass";
        ThemeOledRadio.IsChecked = _settings.Theme == "Oled";
        ApplyTheme();
        _initializing = false;
        InitChat();
        RestoreWindowBounds();

        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            EmptyState_IsVisibleChanged(EmptyState, default); // pause or resume the idle emblem
        };
        _poll.Tick += Poll_Tick;

        if (_workflowRoot is null)
        {
            SetStatus("Workflow files not found", "#FF667A");
            StartButton.IsEnabled = false;
            Log("Place this EXE in the Agent Setup folder or its dist subfolder.");
        }
        else
        {
            ProjectPath = _settings.LastProject.Length > 0 ? _settings.LastProject : _workflowRoot;
            Log($"Workflow: {_workflowRoot}");
            TryLoadApiKeyFile();
            RefreshInitializationPrompt();
            PrefillCards();
            RefreshPrimary();
        }
    }

    // ---------------------------------------------------------------- run control

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_workflowRoot is null || _chatBusy) // a chat reply is using the models
            return;

        var project = SelectedProjectPath();
        if (project.Length == 0 || !Directory.Exists(project) || LauncherLogic.MissingProjectFiles(project).Length != 0)
        {
            RefreshInitializationPrompt();
            SetStatus("Initialize the project first", "#F7C873");
            return;
        }

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
        if (LauncherLogic.IsProjectComplete(project) && !RestartProject(project))
            return;
        if (!PrepareRun())
            return;
        try
        {
            var python = LauncherLogic.FindPython();
            var startInfo = LauncherLogic.BuildStartInfo(python, _workflowRoot, project, _runKey, OnceToggle.IsChecked == true);
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };
            _process.ErrorDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };

            _settings.LastProject = project;
            _settings.Save();
            _apiBase = ManagementUrl(project) ?? _apiBase;
            _runStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            Log($"Starting workflow for {project}");
            SetRunning(true);
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _poll.Start();
            _pump.Start();
            await _process.WaitForExitAsync();
            var exitCode = _process.ExitCode;
            _pump.Stop();
            DrainLines();
            EndStream(false);
            Log($"Process exited with code {exitCode}.");
            SetStatus(exitCode == 0 ? "Completed" : "Stopped with an error", exitCode == 0 ? "#5BD6A2" : "#FF667A");
            if (exitCode == 0)
                Toast("Run finished");
            else if (_stopRequested)
                Toast("Run stopped", "#F7C873");
            else
                Toast($"Run failed (exit code {exitCode})", "#FF667A");
        }
        catch (Exception exception)
        {
            Log($"ERROR: {exception.Message}");
            SetStatus("Could not start", "#FF667A");
            Toast("Run could not start", "#FF667A");
        }
        finally
        {
            _poll.Stop();
            _pump.Stop();
            DrainLines();
            EndStream(false);
            _process?.Dispose();
            _process = null;
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
        // The killed orchestrator cannot run its own cleanup, so release the models from here.
        await UnloadModelsAsync();
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
        OutputPanel.Children.Clear();
        EmptyState.Visibility = Visibility.Visible;
        if (_stream is { } live)
            SetPulse(live, false);
        _stream = null;
        _pendingTools.Clear();
        _lines.Clear();
        _loadedByRun.Clear();
        _followTail = true;
        _stopRequested = false;
        Reveal(JumpPill, false, slide: 10);
        _transcript.Clear();
        _log.Clear();
        LogBox.Clear();
        _logLines = 0;
        LogCount.Text = "0";
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
        StartButton.IsEnabled = !running && _workflowRoot is not null;
        StopButton.IsEnabled = running;
        ApiKeyBox.IsEnabled = !running;
        ProjectButton.IsEnabled = !running;
        OnceToggle.IsEnabled = !running;
        AddWorkerButton.IsEnabled = !running;
        PresetsButton.IsEnabled = !running;
        foreach (var card in _cards)
            card.Editable = !running;
        if (running)
            SetStatus("Running", "#8B7CFF");
        else if (!preserveStatus)
            SetStatus("Ready", "#667085");
        SetActivity(running);
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

    // The empty-state emblem breathes slowly, at a reduced frame rate, and only while it is on screen.
    private void EmptyState_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DoubleAnimation? Breath(double from, double to)
        {
            if (!EmptyState.IsVisible || WindowState == WindowState.Minimized)
                return null;
            var breath = new DoubleAnimation(from, to, TimeSpan.FromSeconds(4))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(breath, 30);
            return breath;
        }
        EmblemGlow.BeginAnimation(OpacityProperty, Breath(0.45, 0.9));
        EmblemScale.BeginAnimation(ScaleTransform.ScaleXProperty, Breath(0.92, 1.06));
        EmblemScale.BeginAnimation(ScaleTransform.ScaleYProperty, Breath(0.92, 1.06));
    }

    // "Start Task" for a project with no tasks yet, otherwise "Resume Project — <active or last completed task>".
    private void RefreshPrimary()
    {
        if (_workflowRoot is null)
            return;
        var project = SelectedProjectPath();
        var ready = project.Length > 0 && Directory.Exists(project) && LauncherLogic.MissingProjectFiles(project).Length == 0;
        StartButton.Content = ready ? LauncherLogic.PrimaryLabel(project) : "Start Task";
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
                    AppendChatDelta(k.Role, k.Kind, pending.ToString());
                else
                    AppendDelta(k.Role, k.Kind, k.Index, name, pending.ToString());
                CardFor(k.Role)?.AddDeltas(chunks, Environment.TickCount64);
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
        var card = CardFor(role);
        switch (Str(ev, "e"))
        {
            case "loading":
                EndStream(false);
                Activate(role, "Loading model");
                _loadedByRun.Add(LoadedOn(ev));
                Log($"[{card?.Title ?? role}] loading {Str(ev, "path")} ...");
                break;
            case "model":
                EndStream(false);
                Activate(role, "Working");
                card?.SetModel(LauncherLogic.DescribeModel(Str(ev, "id"), ConfiguredVariant(role)));
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
                if (_pendingTools.Count > 0)
                    CompleteTool(_pendingTools.Dequeue(), Str(ev, "name"), Str(ev, "args"), Str(ev, "result"));
                else
                    AddTool(role, Str(ev, "name"), Str(ev, "args"), Str(ev, "result"));
                break;
            case "usage":
                Activate(role, "Working");
                card?.AddUsage((long)Num(ev, "prompt"), (long)Num(ev, "completion"), Num(ev, "tps"));
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
        if (CardFor(role) is not { } card)
            return;
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
        public TextBox Body = null!, Result = null!;
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
        if (!_followTail)
            Reveal(JumpPill, true, slide: 10); // returns at once while the pill is already showing
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
            body = block.Body = BodyText("", mono: false, muted: kind == "thinking");

        var title = kind == "tool" ? "TOOL" : kind == "text" ? "RESPONSE" : "THINKING";
        block.Expander = BuildExpander(title, "", body, out block.Preview); // collapsed; the user opens what they want to read
        block.Label = (TextBlock)((StackPanel)block.Expander.Header).Children[0];
        block.Expander.Expanded += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        block.Expander.Collapsed += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        AddBlock(block.Expander, kind == "tool" ? "\n[tool call]\n" : kind == "text" ? "\n[response]\n" : "\n[thinking]\n");
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

    private void AddBlock(UIElement block, string transcript)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        OutputPanel.Children.Add(block);
        _transcript.Append(transcript);
        if (!_followTail)
            Reveal(JumpPill, true, slide: 10);
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
    // child of a parent whose IsHitTestVisible is false (the log box while Options hides the work area).
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
    private TextBox BodyText(string text, bool mono, bool muted, bool chat = false)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = mono ? new FontFamily("Consolas") : FontFamily,
            FontSize = mono ? 12 : 13,
            FontStyle = muted ? FontStyles.Italic : FontStyles.Normal,
            Cursor = Cursors.IBeam,
        };
        box.SetResourceReference(TextBox.ForegroundProperty, muted ? "Muted" : "TextPrimary");
        var scroller = chat ? ChatScroll : OutputScroll;
        box.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (e.Delta > 0)
            {
                if (chat) _chatFollow = false; else _followTail = false;
            }
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta);
        };
        return box;
    }

    // Follow new output unless the user has scrolled up; scrolling back to the bottom resumes following.
    private void OutputScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            _followTail = OutputScroll.VerticalOffset >= OutputScroll.ScrollableHeight - 8;
            if (_followTail)
                Reveal(JumpPill, false, slide: 10);
        }
        else if (_followTail && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0))
            OutputScroll.ScrollToEnd();
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

    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        _log.AppendLine(line);
        LogBox.AppendText(line + Environment.NewLine);
        LogCount.Text = (++_logLines).ToString();
        if (LogToggle.IsChecked == true)
            LogBox.ScrollToEnd();
    }

    // --ui-smoke: stream a fake turn and flip every animated state, so a broken template or storyboard fails a
    // check instead of the user. Hover and press storyboards need real pointer input and are not covered.
    internal async Task<bool> RunUiSmokeAsync()
    {
        static bool Fail([CallerLineNumber] int line = 0) { Console.Error.WriteLine($"ui-smoke: check failed at MainWindow.xaml.cs:{line}"); return false; }
        static Task Settle() => Task.Delay(400);
        // AGENT_ORCHESTRATOR_SNAPSHOTS=<folder> also saves a picture of each state (rendered without a screen).
        var snapshots = Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_SNAPSHOTS");
        void Snap(string name, FrameworkElement? target = null) // target: a popup such as a menu, which the window picture leaves out
        {
            if (string.IsNullOrEmpty(snapshots))
                return;
            target ??= this;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)(target.ActualWidth * dpi.DpiScaleX), (int)(target.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(target);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(snapshots);
            using var file = File.Create(Path.Combine(snapshots, name + ".png"));
            encoder.Save(file);
        }

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
        foreach (var open in new[] { true, false })
        {
            LogToggle.IsChecked = open;
            LogToggle_Click(LogToggle, new RoutedEventArgs());
            await Settle();
            ShowView(options: open);
            await Settle();
            OnceToggle.IsChecked = !OnceToggle.IsChecked;
            _settings.Compact = open;
            ApplyCompact();
            await Settle();
            PlayHoverStoryboards(this); // both card layouts
        }
        // Scrolled away from the end: new output brings in the jump pill, which returns to following.
        _followTail = false;
        _lines.Enqueue(LauncherLogic.EventPrefix + """{"e":"verdict","role":"reviewer","id":"T1","verdict":"PASS","round":1}""");
        DrainLines(); // the verdict also raises a toast
        Toast("Something failed", "#FF667A");
        await Settle();
        if (!JumpPill.IsVisible || !JumpPill.IsHitTestVisible || ToastStack.Children.Count != 2)
            return Fail();
        Snap("2b-toast-and-jump");
        JumpToLatest_Click(JumpPill, new RoutedEventArgs());
        await Settle();
        if (!_followTail || JumpPill.IsVisible)
            return Fail();
        // toasts leave on their own: 3 s on screen + a 180 ms fade, with margin for a busy dispatcher
        for (var waited = 0; waited < 5000 && ToastStack.Children.Count != 0; waited += 100)
            await Task.Delay(100);
        if (ToastStack.Children.Count != 0)
            return Fail();
        // The log was closed while the Options view hid its pane; it must end up collapsed, not stuck open.
        if (LogBox.Visibility != Visibility.Collapsed)
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
        _settings.Compact = true;
        ApplyCompact();
        await Settle();
        Snap("9b-task-counters-compact");
        _settings.Compact = false;
        ApplyCompact();
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
            string Config(string temperature) => """{"roles":{"planner":{"sampling":{"temperature":""" + temperature + """}},"implementer":{"base_url":"http://h/v1"},"reviewer":{}}}""";
            string Cards() => string.Join("|", _cards.Select(c => $"{c.Role}:{c.Title}"));
            File.WriteAllText(presetConfig, Config("0.3"));
            ProjectPath = presetProject;
            var saved = Cards();
            SavePreset("Smoke");
            AddWorker_Click(AddWorkerButton, new RoutedEventArgs());
            File.WriteAllText(presetConfig, Config("0.9"));
            if (Cards() == saved || _settings.Presets.Count(p => p.Name == "Smoke") != 1)
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
            if (Cards() != saved || (double)JsonNode.Parse(File.ReadAllText(presetConfig))!["roles"]!["planner"]!["sampling"]!["temperature"]! != 0.3)
                return Fail();
            DeletePreset(_settings.Presets.Single(p => p.Name == "Smoke"));
            if (_settings.Presets.Any(p => p.Name == "Smoke"))
                return Fail();

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
            // Compact card context menu: items per worker kind.
            var planner = CardFor("planner")!;
            var extra = new RoleCard("smoke-custom", "SMOKE", custom: true);
            _cards.Add(extra);
            string Items(RoleCard c) => string.Join("|", BuildCardMenu(c, this).Items.OfType<MenuItem>().Select(i => i.Header + (i.IsEnabled ? "" : " (off)")));
            if (Items(planner) != "Change model…|Settings…" || Items(extra) != "Change model…|Settings…|Rename…|Remove")
                return Fail();
            var cardMenu = BuildCardMenu(planner, this);
            cardMenu.IsOpen = true;
            await Settle();
            if (!cardMenu.IsOpen)
                return Fail();
            Snap("8-card-menu", cardMenu);
            cardMenu.IsOpen = false;
            planner.Editable = false;
            var locked = Items(planner);
            planner.Editable = true;
            if (locked != "Change model… (off)|Settings… (off)")
                return Fail();
            RemoveWorker(extra);
            if (_cards.Contains(extra))
                return Fail();

            // Dialogs are built without ShowDialog (it would block), shown off-screen, and closed.
            async Task<bool> Show(DialogShell dialog, string name)
            {
                dialog.ShowActivated = false;
                dialog.Show();
                await Settle();
                var shown = dialog.IsVisible && dialog.WindowStyle == WindowStyle.None && dialog.ActualWidth > 100 && dialog.ActualHeight > 60;
                Snap(name, dialog);
                dialog.Close();
                return shown && !dialog.IsVisible;
            }
            var mdFile = Path.Combine(presetProject, "PROJECT.md");
            File.WriteAllText(mdFile, "# Smoke project\n");
            var settingsDialog = BuildRoleSettingsDialog(planner, presetConfig, LauncherLogic.ReadRoleSettings(presetConfig, "planner"));
            var limitsDialog = BuildTaskLimitsDialog(presetConfig, ("6", ""), [("Implementer", 16384)]);
            if (limitsDialog.Warning.Text != "6 files per task may not fit Implementer's 16k context" || limitsDialog.Warning.Visibility != Visibility.Visible
                || !await Show(limitsDialog.Dialog, "9b-task-limits-dialog"))
                return Fail();
            if (!await Show(settingsDialog, "9-settings-dialog") ||!await Show(BuildAskDialog("Save preset", "Name this set of workers and their settings.", "Save", "").Dialog, "10-prompt")
                || !await Show(BuildExistingProjectMdDialog(mdFile, customSelected: true).Dialog, "11-existing-projectmd"))
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
                if (StartButton.Content as string != "Restart Project")
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
                    !LogBox.Text.Contains("app.js could not be moved to the Recycle Bin"))
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
                    StartButton.Content as string != "Start Task")
                    return Fail();
                // The template folder is refused before anything is asked or recycled.
                var asked = false;
                _restartRecycle = _ => throw new IOException("smoke must not recycle the template");
                _restartConfirm = _ => { asked = true; return false; };
                if (RestartProject(_workflowRoot!) || asked || !LogBox.Text.Contains("holds the Agent Orchestrator template"))
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
            // No system prompt yet: the card warns, nothing is written, and the Settings dialog refuses to save.
            SyncCustomWorkers();
            if (!tester.HasSetupProblem || JsonNode.Parse(File.ReadAllText(presetConfig))!["custom_workers"]!.AsArray().Count != 0)
                return Fail();
            var incompleteDialog = BuildCustomSettingsDialog(tester);
            static T? FindIn<T>(DependencyObject d, Func<T, bool> match) where T : DependencyObject =>
                d is T t && match(t) ? t : LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().Select(c => FindIn(c, match)).FirstOrDefault(r => r is not null);
            FindIn<Button>(incompleteDialog, b => b.Content as string == "Save")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var rejection = FindIn<TextBlock>(incompleteDialog, t => t.Visibility == Visibility.Visible && t.Text.StartsWith("Enter a system prompt"));
            if (rejection is null || incompleteDialog.DialogResult is not null || tester.Config.SystemPrompt.Length != 0)
                return Fail();
            await Settle();
            (CardsList.ItemContainerGenerator.ContainerFromItem(tester) as FrameworkElement)?.BringIntoView();
            await Settle();
            Snap("13-incomplete-worker");
            tester.Config.SystemPrompt = "You test.";
            SyncCustomWorkers();
            if (tester.HasSetupProblem)
                return Fail();
            var synced = JsonNode.Parse(File.ReadAllText(presetConfig))!["custom_workers"]!.AsArray();
            if (synced.Count != 1 || (string)synced[0]!["title"]! != "Docs" || (string)synced[0]!["kind"]! != "reviewer")
                return Fail();
            var customDialog = BuildCustomSettingsDialog(tester);
            if (!HasTag(customDialog, "SystemPrompt") || !HasTag(customDialog, "Description") || HasTag(settingsDialog, "SystemPrompt") ||
                !await Show(customDialog, "12-custom-settings-dialog"))
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
                LogBox.Text.Contains("stale line") || !LogBox.Text.Contains("DUP: title 'DUP' is already used") || ToastStack.Children.Count == 0 ||
                JsonNode.Parse(File.ReadAllText(presetConfig))!["custom_workers"]!.AsArray().Count != 1)
                return Fail();
            // config.json cannot be written: the start is aborted before anything is cleared or launched.
            Log("kept line");
            File.SetAttributes(presetConfig, FileAttributes.ReadOnly);
            bool started;
            try { started = PrepareRun(); }
            finally { File.SetAttributes(presetConfig, FileAttributes.Normal); }
            if (started || _process is not null || StatusText.Text != "Could not update config.json" || !LogBox.Text.Contains("kept line") ||
                !LogBox.Text.Contains("Could not write custom workers") || ToastStack.Children.OfType<Border>().All(t => !((TextBlock)((StackPanel)t.Child).Children[1]).Text.StartsWith("Run not started")))
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
            ShowView(false, chat: true);
            await Settle();
            if (!ChatView.IsVisible)
                return Fail();
            var stray = new RoleCard("smoke-chat-custom", "Smoke chat", custom: true);
            _cards.Add(stray);
            var workerMenu = BuildWorkerMenu(ChatPlus);
            var headers = workerMenu.Items.OfType<MenuItem>().ToList();
            var names = headers.Select(i => (string)i.Header).ToList();
            var broken = headers.FirstOrDefault(i => ((string)i.Header).StartsWith("Smoke chat"));
            if (new[] { "Planner", "Implementer", "Reviewer" }.Any(n => !names.Contains(n)) || broken is null || broken.IsEnabled || workerMenu.Placement != PlacementMode.Top)
                return Fail();
            workerMenu.IsOpen = true;
            await Settle();
            Snap("14-chat-plus-menu", workerMenu);
            workerMenu.IsOpen = false;
            RemoveWorker(stray);

            PickChatWorker("reviewer");
            if (ChatMulti || !((string)ChatTargetChip.Content).Contains("Reviewer"))
                return Fail();
            string Ev(string json) => LauncherLogic.EventPrefix + json;
            string Delta(string role, string kind, string text) => Ev($$"""{"e":"delta","role":"{{role}}","kind":"{{kind}}","text":"{{text}}"}""");

            async Task<bool> Turn(string text, bool multi, string[] events, string reply)
            {
                StartChatTurn(text, multi);
                if (!_chatBusy || StartButton.IsEnabled)
                    return false;
                foreach (var line in events)
                    _lines.Enqueue(line);
                _lines.Enqueue(Ev($$"""{"e":"chat_reply","role":"reviewer","text":"{{reply}}"}"""));
                DrainLines();
                var current = _reply!;
                await Settle();
                FinishChatTurn(0, false, null);
                return current.Answer?.Text == reply && current.Final == reply && !_chatBusy && StartButton.IsEnabled == (_workflowRoot is not null);
            }

            var reviewer = CardFor("reviewer")!;
            if (!await Turn("Hello", false, new[]
            {
                Ev("""{"e":"chat_stage","role":"reviewer","stage":"answer","title":"Reviewer"}"""),
                Ev("""{"e":"loading","role":"reviewer","path":"smoke/chat-model.gguf"}"""),
                Ev("""{"e":"model","role":"reviewer","id":"chat-model Q4_K_M"}"""),
                Delta("reviewer", "thinking", "hmm"),
                Delta("reviewer", "text", "streamed answer"),
                Ev("""{"e":"done","role":"reviewer","final":true}"""),
                Ev("""{"e":"usage","role":"reviewer","prompt":321,"completion":45,"tps":30}"""),
            }, "Final reviewed reply") || _chatHistory.Count != 2 || !_loadedByChat.Any(l => l.Path == "smoke/chat-model.gguf") ||
                !reviewer.TurnDetail.Contains("321 prompt") || !reviewer.TurnDetail.Contains("45 generated") || OutputPanel.Children.Count != outputCount ||
                _reply!.Content.Children.OfType<Expander>().Count() != 1)
                return Fail();
            await Settle();
            Snap("15-chat-single");

            ChatMode_Click(ChatMultiToggle, new RoutedEventArgs());
            if (!ChatMulti || (string)ChatTargetChip.Content != ChainLabel)
                return Fail();
            var stages = new List<string>();
            foreach (var (role, stage, title) in new[] { ("planner", "plan", "Plan"), ("implementer", "draft", "Draft"), ("reviewer", "review", "Review") })
            {
                stages.Add(Ev($$"""{"e":"chat_stage","role":"{{role}}","stage":"{{stage}}","title":"{{title}}"}"""));
                stages.Add(Ev($$"""{"e":"loading","role":"{{role}}","path":"smoke/{{role}}.gguf"}"""));
                stages.Add(Ev($$"""{"e":"model","role":"{{role}}","id":"{{role}}-model"}"""));
                stages.Add(Delta(role, "text", $"{title} body"));
                stages.Add(Ev($$"""{"e":"done","role":"{{role}}","final":true}"""));
            }
            // The real Multi protocol (orchestrator.py run_chat): plan, draft, review stages, then only chat_reply carries the answer.
            if (!await Turn("Build it", true, stages.ToArray(), "Reviewed result") || _chatHistory.Count != 4 || OutputPanel.Children.Count != outputCount ||
                _reply!.Content.Children.OfType<Expander>().Count() != 3 || !_loadedByChat.Any(l => l.Path == "smoke/planner.gguf"))
                return Fail();
            await Settle();
            Snap("16-chat-multi");

            NewChat_Click(ChatSend, new RoutedEventArgs());
            if (_chatHistory.Count != 0 || ChatPanel.Children.Count != 0)
                return Fail();
            ChatMode_Click(ChatSingleToggle, new RoutedEventArgs());

            // Stop landing as the reply completes keeps the answer (exit 0 + chat_reply); a real stop still discards the turn.
            StartChatTurn("Race", false);
            _lines.Enqueue(Ev("""{"e":"chat_reply","role":"reviewer","text":"Raced reply"}"""));
            DrainLines();
            if (FinishChatTurn(0, true, null) || _chatBusy || _chatHistory.Count != 2 || _chatHistory[1] != new LauncherLogic.ChatMessage("assistant", "Raced reply") ||
                _reply!.Final != "Raced reply" || _reply.Answer?.Text != "Raced reply")
                return Fail();
            StartChatTurn("Really stopped", false);
            if (!FinishChatTurn(-1, true, null) || _chatBusy || _chatHistory.Count != 2)
                return Fail();
            NewChat_Click(ChatSend, new RoutedEventArgs());

            // Closing mid-reply: the turn's request file and created temp folder are deleted, a real project is not touched.
            var strayFile = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-chat-{Guid.NewGuid():N}.json");
            var strayDir = Path.Combine(Path.GetTempPath(), $"AgentOrchestrator-chat-{Guid.NewGuid():N}");
            File.WriteAllText(strayFile, "{}");
            Directory.CreateDirectory(strayDir);
            File.WriteAllText(Path.Combine(strayDir, "config.json"), "{}");
            (_chatRequestFile, _chatTempFolder) = (strayFile, strayDir);
            StartChatTurn("Busy", false);
            _loadedByChat.Clear(); // the close path would unload these from a real server
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

            // Unload on close, called on the UI thread against two fake servers: each receives exactly its own paths, and fast.
            (System.Net.Sockets.TcpListener Listener, System.Collections.Concurrent.ConcurrentBag<string> Seen, Task Serving) Fake()
            {
                var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                var task = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            var client = await listener.AcceptTcpClientAsync();
                            _ = Task.Run(async () =>
                            {
                                using var c = client;
                                var stream = c.GetStream();
                                var head = new StringBuilder();
                                var one = new byte[1];
                                while (!head.ToString().EndsWith("\r\n\r\n") && await stream.ReadAsync(one) == 1)
                                    head.Append((char)one[0]);
                                var length = System.Text.RegularExpressions.Regex.Match(head.ToString(), @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                var body = new byte[length.Success ? int.Parse(length.Groups[1].Value) : 0];
                                for (var got = 0; got < body.Length;)
                                    got += await stream.ReadAsync(body.AsMemory(got));
                                bag.Add(Encoding.UTF8.GetString(body));
                                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                            });
                        }
                    }
                    catch (Exception) { } // the listener was stopped
                });
                return (listener, bag, task);
            }
            string UrlOf(System.Net.Sockets.TcpListener l) => $"http://127.0.0.1:{((System.Net.IPEndPoint)l.LocalEndpoint).Port}";
            var (fakeA, seenA, servingA) = Fake();
            var (fakeB, seenB, servingB) = Fake();
            var (savedBase, savedKey) = (_apiBase, _runKey);
            (_apiBase, _runKey) = (UrlOf(fakeA), "smoke-key"); // the fallback for a loading event without a url
            _loadedByChat.Clear();
            _loadedByRun.Clear();
            _chatBusy = true;
            _lines.Enqueue(Ev($$"""{"e":"loading","role":"reviewer","path":"smoke/a.gguf","url":"{{UrlOf(fakeA)}}"}""")); // chat, server A
            _lines.Enqueue(Ev("""{"e":"loading","role":"planner","path":"smoke/b.gguf"}""")); // chat, no url: _apiBase (A)
            DrainLines();
            _chatBusy = false;
            _lines.Enqueue(Ev($$"""{"e":"loading","role":"planner","path":"smoke/c.gguf","url":"{{UrlOf(fakeB)}}"}""")); // run, server B
            // G3: a line still queued when closing begins is drained first, so it is unloaded too.
            _lines.Enqueue(Ev($$"""{"e":"loading","role":"implementer","path":"smoke/d.gguf","url":"{{UrlOf(fakeB)}}"}"""));
            if (_loadedByChat.Count != 2 || _loadedByRun.Count != 0 || _lines.Count != 2) // c and d are still queued, undrained
                return Fail();
            var clock = Stopwatch.StartNew();
            OnClosing(this, new CancelEventArgs()); // the real close path (drain, then unload) on the UI thread
            clock.Stop();
            (_apiBase, _runKey) = (savedBase, savedKey);
            fakeA.Stop();
            fakeB.Stop();
            await servingA;
            await servingB;
            bool Only(System.Collections.Concurrent.ConcurrentBag<string> bag, params string[] names) =>
                bag.Count == names.Length && names.All(n => bag.Count(b => b.Contains($"smoke/{n}.gguf")) == 1);
            if (!Only(seenA, "a", "b") || !Only(seenB, "c", "d") ||
                clock.Elapsed > TimeSpan.FromSeconds(2) || _loadedByChat.Count != 0 || _loadedByRun.Count != 0)
            {
                Console.Error.WriteLine($"ui-smoke: unload on close: server A saw {seenA.Count}, server B saw {seenB.Count}, took {clock.Elapsed.TotalSeconds:F1}s");
                return Fail();
            }

            // Keep models loaded: On leaves them tracked and unposted; Off unloads exactly what the turn loaded, on its server;
            // switching On to Off unloads the tracked ones once; a model chat did not load is never touched.
            var (fakeC, seenC, servingC) = Fake();
            (_apiBase, _runKey) = (UrlOf(fakeC), "smoke-key");
            _loadedByChat.Clear();
            _loadedByRun.Clear();
            async Task KeepTurn(params string[] names)
            {
                StartChatTurn("K", false);
                foreach (var n in names)
                    _lines.Enqueue(Ev($$"""{"e":"loading","role":"reviewer","path":"smoke/{{n}}.gguf","url":"{{UrlOf(fakeC)}}"}"""));
                DrainLines();
                FinishChatTurn(-1, true, null);
                await _chatUnload;
            }
            bool Posted(params string[] names) => Only(seenC, names) && names.All(n => _loadedByChat.All(l => l.Path != $"smoke/{n}.gguf"));
            _settings.ChatKeepLoaded = true;
            ChatKeepToggle.IsChecked = true;
            await KeepTurn("k1", "k2");
            var keepOk = seenC.IsEmpty && _loadedByChat.Count == 2 && !_unloading;
            ChatKeepToggle.IsChecked = false;
            ChatKeep_Click(ChatKeepToggle, new RoutedEventArgs());
            var sentWhileBusy = _unloading && !ChatSend.IsEnabled; // Send waits for the unload
            await _chatUnload;
            var switchOk = keepOk && sentWhileBusy && !_settings.ChatKeepLoaded && Posted("k1", "k2") && _loadedByChat.Count == 0 && !_unloading;
            ChatKeep_Click(ChatKeepToggle, new RoutedEventArgs()); // already Off: nothing more to unload
            await _chatUnload;
            await KeepTurn("k3", "k4");
            var offOk = switchOk && Posted("k1", "k2", "k3", "k4") && _loadedByChat.Count == 0;
            await KeepTurn(); // a model that was already loaded sends no loading event: nothing is unloaded
            _settings.ChatKeepLoaded = true;
            ChatKeepToggle.IsChecked = true;
            fakeC.Stop();
            await servingC;
            (_apiBase, _runKey) = (savedBase, savedKey);
            if (!offOk || !Only(seenC, "k1", "k2", "k3", "k4") || _loadedByChat.Count != 0)
            {
                Console.Error.WriteLine($"ui-smoke: keep loaded: keep={keepOk} switch={switchOk} off={offOk}, server saw {seenC.Count}");
                return Fail();
            }
            ShowView(false);
            await Settle();
        }
        SetRunning(true); // status ring and header shimmer
        await Settle();
        if (RailProjectButton.IsEnabled || RailOnceToggle.IsEnabled)
            return Fail();
        Snap("3-running");
        _settings.Compact = true;
        ApplyCompact();
        await Settle();
        Snap("4-compact");
        _settings.Compact = false;
        ApplyCompact();
        SetRunning(false);
        await Settle();
        if (!RailProjectButton.IsEnabled || !RailOnceToggle.IsEnabled)
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
        if (!string.IsNullOrEmpty(snapshots))
        {
            foreach (var theme in new[] { "Gold", "Glass", "Oled", "Flat" })
            {
                _settings.Theme = theme;
                ApplyTheme();
                await Settle();
                Snap("5-theme-" + theme);
                ShowView(false, chat: true);
                await Settle();
                Snap("17-chat-theme-" + theme);
                ShowView(false);
            }
            ShowView(options: true);
            await Settle();
            Snap("6-options");
        }
        return true;
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

    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        Reveal(LogBox, LogToggle.IsChecked == true, slide: 12);
        if (LogToggle.IsChecked == true)
            LogBox.ScrollToEnd();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_log.ToString(), "Full log copied", "Log is empty");

    private void CopyOutput_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_transcript.ToString(), "Output copied", "No output yet");

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        OutputPanel.Children.Clear();
        if (_stream is { } live)
            SetPulse(live, false);
        _stream = null;
        _pendingTools.Clear();
        _transcript.Clear();
        _blockRole = null;
        EmptyState.Visibility = Visibility.Visible;
        JumpToLatest_Click(JumpPill, e);
    }

    // ---- output navigation: jump-to-latest pill, expand / collapse all, per-section copy

    private void JumpToLatest_Click(object sender, RoutedEventArgs e)
    {
        _followTail = true;
        OutputScroll.ScrollToEnd();
        Reveal(JumpPill, false, slide: 10);
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(true);

    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(false);

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
        foreach (var piece in pieces.Where(p => p.Visibility == Visibility.Visible))
            parts.Add(piece switch { TextBox box => box.Text, TextBlock label => label.Text, _ => "" });
        return string.Join("\n", parts);
    }

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

    // ---------------------------------------------------------------- live speed (Unsloth's own monitor endpoint)

    private async void Poll_Tick(object? sender, EventArgs e)
    {
        if (_polling || (_process ?? _chatProcess) is null || _activeRole is null || CardFor(_activeRole) is not { } card)
            return;
        _polling = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _apiBase + "/api/inference/monitor");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _runKey);
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return;
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            JsonElement? best = null;
            var bestStart = 0.0;
            foreach (var row in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (Prop(row, "kind") != "request" || Prop(row, "status") != "running" ||
                    !(row.TryGetProperty("via_api_key", out var via) && via.ValueKind == JsonValueKind.True))
                    continue;
                var started = Number(row, "started_at") ?? 0;
                if (started >= _runStart - 2 && started >= bestStart)
                    (best, bestStart) = (row, started);
            }
            if (best is not { } live || (_process ?? _chatProcess) is null)
                return;
            var tps = Number(live, "tok_per_sec");
            var decodeMs = Number(live, "decode_ms");
            var generated = tps > 0 && decodeMs > 0 ? (long)Math.Round(tps.Value * decodeMs.Value / 1000) : 0;
            card.SetLive((long)(Number(live, "prompt_tokens") ?? 0), generated, tps, Number(live, "prompt_tok_per_sec"));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Monitor unavailable: the per-call numbers from the orchestrator still arrive.
        }
        finally { _polling = false; }

        static string Prop(JsonElement el, string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static double? Number(JsonElement el, string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    // ---------------------------------------------------------------- project / config helpers

    private JsonDocument? ReadConfig(string project)
    {
        foreach (var path in new[] { Path.Combine(project, "config.json"), _workflowRoot is null ? "" : Path.Combine(_workflowRoot, "config.json") })
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
        return config is not null && config.RootElement.TryGetProperty("roles", out var roles) && roles.TryGetProperty(role, out var r) &&
               r.TryGetProperty("auto_load", out var load) && load.TryGetProperty("gguf_variant", out var v) ? v.GetString() : null;
    }

    private string? ManagementUrl(string project)
    {
        using var config = ReadConfig(project);
        return config is not null && config.RootElement.TryGetProperty("roles", out var roles) && roles.TryGetProperty("planner", out var p) &&
               p.TryGetProperty("management_url", out var u) ? u.GetString()?.TrimEnd('/') : null;
    }

    // Show the configured models on the cards before anything runs.
    private void PrefillCards()
    {
        if (_process is not null || _initializing)
            return;
        using var config = ReadConfig(SelectedProjectPath());
        if (config is null || !config.RootElement.TryGetProperty("roles", out var roles))
            return;
        foreach (var card in _cards)
        {
            if (!roles.TryGetProperty(card.Role, out var r) || !r.TryGetProperty("auto_load", out var load) ||
                !load.TryGetProperty("model_path", out var path) || path.GetString() is not { } id)
                continue;
            if (r.TryGetProperty("display_name", out var label) && label.GetString() is { Length: > 0 } shown)
                id = shown;
            card.SetModel(LauncherLogic.DescribeModel(id, load.TryGetProperty("gguf_variant", out var v) ? v.GetString() : null));
        }
    }

    // A loading event's (server, model): the orchestrator names the management URL it loaded on; older ones omit it.
    private (string Url, string Path) LoadedOn(JsonElement ev) => (Str(ev, "url") is { Length: > 0 } u ? u : _apiBase, Str(ev, "path"));

    // Release only the models this run loaded; one the user had already loaded in Unsloth is left alone.
    private async Task UnloadModelsAsync()
    {
        var paths = _loadedByRun.ToList();
        _loadedByRun.Clear();
        foreach (var (url, path) in paths)
            if (await PostUnloadAsync(url, path, _runKey))
                Log($"Unloaded {path}");
    }

    // One unload POST; false when the server is unreachable or refuses. Touches no UI, so it is safe off the UI thread.
    private async Task<bool> PostUnloadAsync(string url, string path, string key)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/api/inference/unload")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model_path = path }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }

    // The close-time unload. It runs on the UI thread inside Closing, so it must not await anything that needs the
    // dispatcher (a Task.Wait on UnloadModelsAsync starves its own continuation and only the first POST goes out).
    // Synchronous sends, each bounded, the whole thing bounded; every tracked path from both sets is attempted.
    private int UnloadOnClose(double budgetSeconds = 4)
    {
        var paths = _loadedByRun.Concat(_loadedByChat).Distinct().ToList();
        _loadedByRun.Clear();
        _loadedByChat.Clear();
        var deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);
        var unloaded = 0;
        foreach (var (url, path) in paths)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
                break;
            try
            {
                using var cancel = new CancellationTokenSource(left < TimeSpan.FromSeconds(2) ? left : TimeSpan.FromSeconds(2));
                using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/api/inference/unload")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { model_path = path }), Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _runKey);
                using var response = _http.Send(request, cancel.Token);
                if (response.IsSuccessStatusCode)
                    unloaded++;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { } // try the next path
        }
        return unloaded;
    }

    private bool TryLoadApiKeyFile()
    {
        if (_workflowRoot is null)
            return false;
        try
        {
            var detected = LauncherLogic.FindApiKeyFile(_workflowRoot, AppContext.BaseDirectory);
            if (detected is null)
                return false;
            ApiKeyBox.Password = detected.Value.Key;
            ApiKeyHint.Text = "API key loaded from api_key.txt; it remains hidden and is not saved by the launcher.";
            ApiKeyHint.ToolTip = detected.Value.Path;
            Log("API key loaded from api_key.txt.");
            return true;
        }
        catch (Exception exception)
        {
            ApiKeyHint.Text = "api_key.txt is invalid; enter the key manually or correct the file.";
            Log($"Could not load api_key.txt: {exception.Message}");
            SetStatus("Invalid api_key.txt", "#FF667A");
            return false;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the target project folder",
            InitialDirectory = Directory.Exists(ProjectPath) ? ProjectPath : _workflowRoot,
        };
        if (dialog.ShowDialog(this) == true)
            ProjectPath = dialog.FolderName;
    }

    private void InitializeProject_Click(object sender, RoutedEventArgs e)
    {
        if (_workflowRoot is null)
            return;
        var project = SelectedProjectPath();
        if (project.Length == 0)
        {
            SetStatus("Enter a project folder", "#FF667A");
            return;
        }

        try
        {
            if (_customProjectFile is not null && string.IsNullOrWhiteSpace(File.ReadAllText(_customProjectFile)))
                throw new InvalidDataException("Custom PROJECT.md cannot be empty.");
            if (_customConfigFile is not null)
                LauncherLogic.ValidateConfigFile(_customConfigFile);
            var existed = Directory.Exists(project);
            string? backup = null;
            var existingMd = Path.Combine(project, "PROJECT.md");
            var keptExisting = false;
            if (File.Exists(existingMd))
            {
                var choice = AskExistingProjectMd(existingMd, _customProjectFile is not null);
                if (choice == ExistingProjectMdChoice.Cancel)
                {
                    SetStatus("Initialization cancelled", "#F7C873");
                    return;
                }
                if (choice == ExistingProjectMdChoice.Replace)
                    backup = LauncherLogic.BackupProjectMd(project);
                else
                    keptExisting = true;
            }
            var copied = LauncherLogic.InitializeProject(_workflowRoot, project, _customProjectFile, _customConfigFile);
            Log($"{(existed ? "Initialized" : "Created and initialized")} {project}");
            if (backup is not null)
                Log($"PROJECT.md replaced; previous saved as {backup}");
            else if (keptExisting)
                Log("Using existing PROJECT.md");
            if (copied.Length > 0)
                Log($"Added: {string.Join(", ", copied)}");
            if (_customProjectFile is not null && !copied.Contains("PROJECT.md"))
                Log("Existing PROJECT.md was preserved; the selected custom file was not copied.");
            if (_customConfigFile is not null && !copied.Contains("config.json"))
                Log("Existing config.json was preserved; the selected custom file was not copied.");
            SetStatus("Project initialized", "#5BD6A2");
            Toast(backup is not null ? $"PROJECT.md replaced; previous saved as {backup}" :
                keptExisting ? "Using existing PROJECT.md" : "Project initialized");
            RefreshInitializationPrompt();
            PrefillCards();
            RefreshPrimary();
        }
        catch (Exception exception)
        {
            Log($"Initialization failed: {exception.Message}");
            SetStatus("Initialization failed", "#FF667A");
        }
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
        if (!(_restartConfirm is { } confirm ? confirm(dialog) : dialog.ShowDialog() == true))
        {
            SetStatus("Restart cancelled", "#F7C873");
            return false;
        }
        if (LauncherLogic.RecycleForRestart(project, _restartRecycle) is { } failure)
        {
            Log($"Restart stopped: {failure.Path} could not be moved to the Recycle Bin ({failure.Error}). Items recycled before it are in the Recycle Bin; the rest of the project is unchanged and no run was started.");
            SetStatus("Restart stopped", "#FF667A");
            Toast($"Restart stopped: {Path.GetFileName(failure.Path)} could not be recycled", "#FF667A");
            RefreshInitializationPrompt();
            RefreshPrimary();
            return false;
        }
        var copied = LauncherLogic.InitializeProject(_workflowRoot, project); // PROJECT.md exists, so it is kept
        Log($"Project restarted: everything but PROJECT.md moved to the Recycle Bin. Added: {string.Join(", ", copied)}");
        Toast("Project restarted");
        RefreshInitializationPrompt();
        PrefillCards();
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

    private ExistingProjectMdChoice AskExistingProjectMd(string path, bool customSelected)
    {
        var (dialog, result) = BuildExistingProjectMdDialog(path, customSelected);
        dialog.ShowDialog();
        return result();
    }

    private (DialogShell Dialog, Func<ExistingProjectMdChoice> Result) BuildExistingProjectMdDialog(string path, bool customSelected)
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
            (customSelected ? "the PROJECT.md you selected." : "the template PROJECT.md."), true, 8));
        if (customSelected)
            panel.Children.Add(Line("Using the existing file means the custom PROJECT.md you selected will not be used.", true, 4));

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

    private void ChooseProjectFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose PROJECT.md", Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            _customProjectFile = dialog.FileName;
            UpdateCustomFilesText();
        }
    }

    private void ChooseConfigFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose config.json", Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                LauncherLogic.ValidateConfigFile(dialog.FileName);
                _customConfigFile = dialog.FileName;
                UpdateCustomFilesText();
            }
            catch (Exception exception)
            {
                Log($"Invalid custom config.json: {exception.Message}");
                SetStatus("Invalid custom config.json", "#FF667A");
            }
        }
    }

    private void UpdateCustomFilesText()
    {
        var names = new List<string>();
        if (_customProjectFile is not null)
            names.Add($"PROJECT.md ← {_customProjectFile}");
        if (_customConfigFile is not null)
            names.Add($"config.json ← {_customConfigFile}");
        CustomFilesText.Text = names.Count == 0 ? "Using Agent Setup templates." : string.Join("\n", names);
    }

    private void EditProject_Click(object sender, RoutedEventArgs e)
    {
        var projectFile = Path.Combine(SelectedProjectPath(), "PROJECT.md");
        if (!File.Exists(projectFile))
        {
            RefreshInitializationPrompt();
            SetStatus("Initialize the project first", "#F7C873");
            return;
        }
        Process.Start(new ProcessStartInfo(projectFile) { UseShellExecute = true });
    }

    private void Once_Click(object sender, RoutedEventArgs e)
    {
        _settings.Once = OnceToggle.IsChecked == true;
        _settings.Save();
    }

    private string _projectPath = "";

    // The project folder is chosen with the folder picker; setting it refreshes everything that depends on it.
    private string ProjectPath
    {
        get => _projectPath;
        set
        {
            _projectPath = value;
            var shown = SelectedProjectPath();
            ProjectName.Text = shown.Length == 0 ? "Choose project folder…" : Path.GetFileName(shown.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : shown;
            ProjectPathText.Text = shown;
            ProjectPathText.Visibility = shown.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ProjectButton.ToolTip = shown.Length == 0 ? "Choose the project folder" : shown;
            RefreshInitializationPrompt();
            PrefillCards();
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

    private void RefreshInitializationPrompt()
    {
        if (_initializing || _workflowRoot is null)
            return;
        var project = SelectedProjectPath();
        if (project.Length == 0)
        {
            InitializationPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (!Directory.Exists(project))
        {
            InitializationText.Text = "This folder does not exist. Initialize Project will create it and add the required workflow files.";
            InitializationPanel.Visibility = Visibility.Visible;
            return;
        }

        var missing = LauncherLogic.MissingProjectFiles(project);
        InitializationText.Text = missing.Length == 0
            ? ""
            : $"This folder needs {missing.Length} workflow file{(missing.Length == 1 ? "" : "s")}. Existing files will not be overwritten.";
        InitializationPanel.Visibility = missing.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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
        if (e.Handled || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter when !shift && StartButton.IsEnabled && !ChatView.IsVisible:
                Start_Click(StartButton, e);
                break;
            case Key.X when shift && StopButton.IsEnabled && Keyboard.FocusedElement is not (TextBox or PasswordBox):
                Stop_Click(StopButton, e);
                break;
            case Key.L when !shift:
                LogToggle.IsChecked = LogToggle.IsChecked != true;
                LogToggle_Click(LogToggle, e);
                break;
            case Key.D1 or Key.NumPad1 when !shift:
                ShowView(options: false);
                break;
            case Key.D2 or Key.NumPad2 when !shift:
                ShowView(options: true);
                break;
            case Key.D3 or Key.NumPad3 when !shift:
                ShowView(options: false, chat: true);
                break;
            case Key.B when !shift:
                SetSidebar(!_settings.SidebarCollapsed);
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
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    // ---------------------------------------------------------------- worker cards

    private RoleCard? CardFor(string role) => _cards.FirstOrDefault(c => c.Role == role);

    // Saved order and user-created workers; the three built-in workers are always present.
    private void LoadWorkers()
    {
        var saved = _settings.Workers.Where(w => w.Id.Length > 0 && (w.Custom || Roles.Contains(w.Id))).ToList();
        foreach (var role in Roles.Where(r => saved.All(w => w.Id != r)))
            saved.Add(new WorkerSetting { Id = role });
        foreach (var w in saved)
        {
            var card = new RoleCard(w.Id, w.Custom ? w.Title : null, w.Custom) { ModelId = w.ModelId, ModelVariant = w.ModelVariant, Config = w.Clone() };
            if (w.Custom && w.ModelId is not null)
                card.SetModel(LauncherLogic.DescribeModel(w.ModelId, w.ModelVariant));
            _cards.Add(card);
        }
        RefreshSetupProblems();
    }

    // Cards of custom workers the orchestrator would ignore show a light warning.
    private void RefreshSetupProblems()
    {
        var workers = CurrentWorkers();
        var path = ProjectConfigPath();
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

    // The orchestrator reads custom workers from the project's config.json; keep it in step with the cards.
    // Returns false when the write failed. With collectSkipped the caller reports the skipped workers itself.
    private bool SyncCustomWorkers(List<string>? collectSkipped = null)
    {
        RefreshSetupProblems();
        var path = ProjectConfigPath();
        if (!File.Exists(path) || LauncherLogic.IsTemplateConfig(path, _workflowRoot)) // chat without a project uses a temp copy
            return true;
        try
        {
            var skipped = LauncherLogic.WriteCustomWorkers(path, CurrentWorkers());
            if (collectSkipped is not null)
                collectSkipped.AddRange(skipped);
            else
                foreach (var reason in skipped)
                    Log($"Not written to config.json, so it will not take part: {reason}");
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

    private string ProjectConfigPath() => SelectedProjectPath() is { Length: > 0 } project ? Path.Combine(project, "config.json") : "";

    private void Presets_Click(object sender, RoutedEventArgs e)
    {
        if (_process is null)
            BuildPresetsMenu().IsOpen = true;
    }

    private ContextMenu NewPresetMenu() => new() { Style = (Style)FindResource(typeof(ContextMenu)), PlacementTarget = PresetsButton, Placement = PlacementMode.Bottom };

    private MenuItem PresetItem(string header, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource(typeof(MenuItem)), IsEnabled = enabled, Opacity = enabled ? 1 : 0.45 };
        item.Click += (_, _) => click();
        return item;
    }

    private ContextMenu BuildPresetsMenu()
    {
        var menu = NewPresetMenu();
        foreach (var preset in _settings.Presets)
            menu.Items.Add(PresetItem(preset.Name, () => ApplyPreset(preset)));
        if (_settings.Presets.Count == 0)
            menu.Items.Add(PresetItem("No saved presets", () => { }, enabled: false));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        menu.Items.Add(PresetItem("Save current as preset…", SavePresetPrompt));
        menu.Items.Add(PresetItem("Delete preset…", () => Dispatcher.InvokeAsync(DeletePresetMenu, DispatcherPriority.Background), _settings.Presets.Count > 0));
        return menu;
    }

    // A second menu (the item template has no submenus) listing what can be deleted.
    private void DeletePresetMenu()
    {
        var menu = NewPresetMenu();
        foreach (var preset in _settings.Presets)
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
        if (_settings.Presets.Any(p => SamePreset(p, name)) &&
            AskDialog("Overwrite preset", $"A preset named \"{name}\" already exists. Replace it?", "Overwrite") is null)
            return;
        SavePreset(name);
    }

    private void SavePreset(string name)
    {
        WorkerPreset preset;
        try
        {
            preset = LauncherLogic.CapturePreset(name, ProjectConfigPath(), CurrentWorkers());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log($"Could not read config.json: {exception.Message}");
            SetStatus("Could not read config.json", "#FF667A");
            return;
        }
        var index = _settings.Presets.FindIndex(p => SamePreset(p, name));
        if (index >= 0)
            _settings.Presets[index] = preset;
        else
            _settings.Presets.Add(preset);
        _settings.Save();
        Log($"Preset saved: {name}");
        Toast($"Preset saved: {name}");
    }

    private void ApplyPreset(WorkerPreset preset)
    {
        if (_process is not null)
            return;
        bool rolesApplied;
        try
        {
            rolesApplied = !LauncherLogic.IsTemplateConfig(ProjectConfigPath(), _workflowRoot) && LauncherLogic.ApplyPresetRoles(ProjectConfigPath(), preset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log($"Could not update config.json: {exception.Message}");
            SetStatus("Could not update config.json", "#FF667A");
            return;
        }
        _settings.Workers = preset.Workers;
        _cards.Clear();
        LoadWorkers(); // keeps only valid entries and re-adds any missing built-in worker
        SaveWorkers();
        SyncCustomWorkers();
        PrefillCards();
        Log($"Preset applied: {preset.Name}");
        if (preset.Roles.Count > 0 && !rolesApplied)
        {
            SetStatus("Select or initialize a project to change worker settings", "#F7C873");
            Toast($"Preset applied: {preset.Name}. Built-in model settings need an initialized project.", "#F7C873");
        }
        else
            Toast($"Preset applied: {preset.Name}");
    }

    private void DeletePreset(WorkerPreset preset)
    {
        _settings.Presets.Remove(preset);
        _settings.Save();
        Log($"Preset deleted: {preset.Name}");
        Toast($"Preset deleted: {preset.Name}");
    }

    private void AddWorker_Click(object sender, RoutedEventArgs e)
    {
        var n = 1;
        while (_cards.Any(c => string.Equals(c.Title, $"WORKER {n}", StringComparison.OrdinalIgnoreCase)))
            n++;
        _cards.Add(new RoleCard(Guid.NewGuid().ToString("N"), $"WORKER {n}", custom: true));
        SaveWorkers();
        RefreshSetupProblems();
    }

    private void RemoveWorker_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RoleCard card)
            RemoveWorker(card);
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

    private void RenameWorker(RoleCard card)
    {
        if (_process is not null || !card.IsCustom ||
            AskDialog("Rename worker", "Worker title", "Rename", card.Title, maxLength: 32, validate: t => LauncherLogic.TitleProblem(t, card.Role, CurrentWorkers())) is not { } title)
            return;
        RenameTo(card, title);
    }

    // ---- compact card context menu (right-click): what the standard card offers as buttons and a title box

    private void CompactCard_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RoleCard card } root)
            return;
        BuildCardMenu(card, root).IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildCardMenu(RoleCard card, FrameworkElement anchor)
    {
        var menu = new ContextMenu { Style = (Style)FindResource(typeof(ContextMenu)), PlacementTarget = anchor, Placement = PlacementMode.MousePoint };
        var idle = _process is null && card.Editable;
        menu.Items.Add(PresetItem("Change model…", () => ChangeModel(card, anchor), idle));
        menu.Items.Add(PresetItem("Settings…", () => Dispatcher.InvokeAsync(() => ShowRoleSettings(card), DispatcherPriority.Background),
            idle && (card.IsCustom || File.Exists(ProjectConfigPath()))));
        if (card.IsCustom)
        {
            menu.Items.Add(PresetItem("Rename…", () => Dispatcher.InvokeAsync(() => RenameWorker(card), DispatcherPriority.Background), idle));
            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
            menu.Items.Add(PresetItem("Remove", () => RemoveWorker(card), idle));
        }
        return menu;
    }

    private void TitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RoleCard { IsCustom: true } card)
            return;
        if (LauncherLogic.TitleProblem(card.Title, card.Role, CurrentWorkers()) is { } problem)
        {
            // the box edits the card live, so put back the last saved title
            card.Title = _settings.Workers.FirstOrDefault(w => w.Id == card.Role)?.Title ?? "WORKER";
            Toast(problem, "#F7C873");
            return;
        }
        RenameTo(card, card.Title);
    }

    // ---- drag-and-drop reordering: the card follows the pointer and the others make room live

    private RoleCard? _dragCard;
    private Point _grab;

    private void Handle_Down(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RoleCard card)
            return;
        if (LauncherLogic.IsInsideButton(e.OriginalSource as DependencyObject))
            return; // a button inside the card is a click, not a drag
        DependencyObject? d = (DependencyObject)sender;
        while (d is not null && d is not FrameworkElement { Tag: "CardRoot" })
            d = VisualTreeHelper.GetParent(d);
        if (d is not FrameworkElement root)
            return;

        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)(root.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(root.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        _grab = e.GetPosition(root);
        DragGhost.Source = bitmap;
        DragGhost.Width = root.ActualWidth;
        DragGhost.Height = root.ActualHeight;
        _dragCard = card;
        card.IsDragging = true;
        PositionGhost(e.GetPosition(this));
        DragLayer.Visibility = Visibility.Visible;
        DetailsScroll.CaptureMouse(); // stays valid while the list re-creates cards as they move
        e.Handled = true;
    }

    private void PositionGhost(Point windowPoint)
    {
        Canvas.SetLeft(DragGhost, windowPoint.X - _grab.X);
        Canvas.SetTop(DragGhost, windowPoint.Y - _grab.Y);
    }

    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (_dragCard is null)
            return;
        PositionGhost(e.GetPosition(this));

        var inScroll = e.GetPosition(DetailsScroll).Y;
        if (inScroll < 50)
            DetailsScroll.ScrollToVerticalOffset(DetailsScroll.VerticalOffset - 14);
        else if (inScroll > DetailsScroll.ActualHeight - 50)
            DetailsScroll.ScrollToVerticalOffset(DetailsScroll.VerticalOffset + 14);

        // swap when the pointer reaches the inner half of another card (works for one or several columns)
        var p = e.GetPosition(CardsList);
        var current = _cards.IndexOf(_dragCard);
        for (var i = 0; i < _cards.Count; i++)
        {
            if (i == current || CardsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement item)
                continue;
            var rect = new Rect(item.TranslatePoint(new Point(0, 0), CardsList), item.RenderSize);
            rect.Inflate(-rect.Width / 4, -rect.Height / 4);
            if (!rect.Contains(p))
                continue;
            _cards.Move(current, i);
            CardsList.UpdateLayout();
            break;
        }
    }

    private void Drag_Up(object sender, MouseEventArgs e)
    {
        if (_dragCard is not { } card)
            return;
        _dragCard = null;
        card.IsDragging = false;
        DragLayer.Visibility = Visibility.Collapsed;
        DetailsScroll.ReleaseMouseCapture();
        SaveWorkers();
    }

    // ---- generation settings

    private void RoleSettings_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RoleCard card)
            ShowRoleSettings(card);
    }

    private void ShowRoleSettings(RoleCard card)
    {
        if (_process is not null)
            return;
        if (card.IsCustom)
        {
            BuildCustomSettingsDialog(card).ShowDialog();
            return;
        }
        var project = SelectedProjectPath();
        var path = Path.Combine(project, "config.json");
        if (project.Length == 0 || !File.Exists(path))
        {
            SetStatus("Initialize the project first", "#F7C873");
            return;
        }
        if (LauncherLogic.IsTemplateConfig(path, _workflowRoot))
        {
            SetStatus("Select or initialize a project to change worker settings", "#F7C873");
            return;
        }
        LauncherLogic.RoleSettings current;
        try
        {
            current = LauncherLogic.ReadRoleSettings(path, card.Role);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
        {
            Log($"Could not read config.json: {exception.Message}");
            SetStatus("Could not read config.json", "#FF667A");
            return;
        }
        BuildRoleSettingsDialog(card, path, current).ShowDialog();
    }

    private DialogShell BuildRoleSettingsDialog(RoleCard card, string path, LauncherLogic.RoleSettings current)
    {
        var (grid, boxes, thinkingChoices, thinking) = BuildSamplingFields(current, custom: false);
        var note = new TextBlock { Text = "Context length and reasoning budget apply when the orchestrator loads the model.", FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var save = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var dialog = new DialogShell(this, $"{card.Title} settings", new StackPanel { Children = { grid, thinking, note, error, buttons } });
        save.Click += (_, _) =>
        {
            var values = ReadFields(boxes, thinkingChoices);
            try
            {
                var problem = LauncherLogic.WriteRoleSettings(path, card.Role, values);
                if (problem is not null)
                {
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or InvalidOperationException)
            {
                Log($"Could not update config.json: {exception.Message}");
                SetStatus("Could not update config.json", "#FF667A");
                dialog.Close();
                return;
            }
            Log($"{card.Title}: generation settings saved");
            Toast($"{card.Title}: settings saved");
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) =>
        {
            boxes[0].Focus();
            boxes[0].SelectAll();
        };
        return dialog;
    }

    // The generation fields shared by both Settings dialogs. custom: empty means "use the Implementer's value".
    private (Grid Grid, TextBox[] Boxes, RadioButton[] Choices, StackPanel Thinking) BuildSamplingFields(LauncherLogic.RoleSettings current, bool custom)
    {
        var empty = custom ? "empty = Implementer's" : "empty = unchanged";
        var fields = new (string Label, string Hint, string Value)[]
        {
            ("Temperature", $"0 to 2; {empty}", current.Temperature),
            ("Max tokens", $"1 to 262144; {empty}", current.MaxTokens),
            ("Repetition penalty", custom ? "1.0 to 2.0; empty = Implementer's" : "1.0 to 2.0; empty = off", current.RepetitionPenalty),
            ("Reasoning budget", $"0 or more; {empty}", current.ReasoningBudget),
            ("Context length", $"512 to 262144; {empty}", current.ContextLength),
        };
        var grid = new Grid { MinWidth = 340 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        var boxes = new TextBox[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hint = new TextBlock { Text = fields[i].Hint, FontSize = 10.5 };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var label = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, i == 0 ? 0 : 9, 12, 0),
                Children = { new TextBlock { Text = fields[i].Label, FontWeight = FontWeights.SemiBold, FontSize = 12 }, hint },
            };
            boxes[i] = new TextBox { Text = fields[i].Value, Height = 30, Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, i == 0 ? 0 : 9, 0, 0) };
            Grid.SetRow(label, i);
            Grid.SetRow(boxes[i], i);
            Grid.SetColumn(boxes[i], 1);
            grid.Children.Add(label);
            grid.Children.Add(boxes[i]);
        }

        var thinkingHint = new TextBlock
        {
            Text = custom ? "Auto = model default. Off avoids long hidden reasoning, which suits Specialists that edit files."
                          : "Auto = model default. Off avoids long hidden reasoning; recommended for the Implementer on reasoning models.",
            FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left,
        };
        thinkingHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var choices = LauncherLogic.ThinkingChoices.Select(choice => new RadioButton
        {
            Content = choice, GroupName = "Thinking", IsChecked = choice == current.Thinking, Margin = new Thickness(0, 0, 18, 0),
        }).ToArray();
        var thinkingRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var choice in choices)
            thinkingRow.Children.Add(choice);
        var thinking = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Children = { new TextBlock { Text = "Thinking", FontWeight = FontWeights.SemiBold, FontSize = 12 }, thinkingHint, thinkingRow },
        };
        return (grid, boxes, choices, thinking);
    }

    private static LauncherLogic.RoleSettings ReadFields(TextBox[] boxes, RadioButton[] thinking) =>
        new(boxes[0].Text, boxes[1].Text, boxes[2].Text, boxes[3].Text, boxes[4].Text, thinking.First(radio => radio.IsChecked == true).Content.ToString()!);

    // Custom workers keep everything in the launcher's settings (and the generated custom_workers in config.json),
    // so this dialog works without an initialized project. The system prompt exists only here.
    private DialogShell BuildCustomSettingsDialog(RoleCard card)
    {
        var w = card.Config;
        TextBlock Muted(string text, Thickness margin)
        {
            var t = new TextBlock { Text = text, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left, Margin = margin };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }
        TextBlock Label(string text, double top) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, top, 0, 0) };
        TextBox Editor(string text, double height) => new()
        {
            Text = text, Height = height, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 4, 0, 0),
        };

        var kinds = new[] { ("specialist", "Specialist"), ("reviewer", "Extra reviewer") };
        var kindExplanation = Muted("", new Thickness(0, 4, 0, 0));
        var kindChoices = kinds.Select(k => new RadioButton { Content = k.Item2, Tag = k.Item1, GroupName = "WorkerKind", IsChecked = (w.Kind == "reviewer" ? "reviewer" : "specialist") == k.Item1, Margin = new Thickness(0, 0, 18, 0) }).ToArray();
        void Explain() => kindExplanation.Text = kindChoices[0].IsChecked == true
            ? "Specialist: the Planner can assign it tasks, and it edits files like the Implementer."
            : "Extra reviewer: reviews every task alongside the Reviewer; its FAIL sends the task back.";
        foreach (var choice in kindChoices)
            choice.Checked += (_, _) => Explain();
        Explain();
        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var choice in kindChoices)
            kindRow.Children.Add(choice);

        var description = Editor(w.Description, 58);
        description.Tag = "Description";
        var prompt = Editor(w.SystemPrompt, 170);
        prompt.Tag = "SystemPrompt";
        prompt.FontFamily = new FontFamily("Consolas");

        var (grid, boxes, thinkingChoices, thinking) = BuildSamplingFields(
            new LauncherLogic.RoleSettings(w.Temperature, w.MaxTokens, w.RepetitionPenalty, w.ReasoningBudget, w.ContextLength, w.Thinking), custom: true);
        grid.Margin = new Thickness(0, 12, 0, 0);
        var note = Muted("Context length and reasoning budget apply when the orchestrator loads the model.", new Thickness(0, 12, 0, 0));
        var error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var save = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var body = new StackPanel
        {
            MinWidth = 440,
            Children =
            {
                Label("Kind", 0), kindRow, kindExplanation,
                Label("Description", 12), Muted("Shown to the Planner: it assigns Specialist tasks based on this, so say what the worker is for.", new Thickness(0)), description,
                Label("System prompt", 12), Muted("The worker's standing instructions. Only custom workers have an editable prompt.", new Thickness(0)), prompt,
                grid, thinking, note, error,
            },
        };
        var scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 190), Padding = new Thickness(0, 0, 8, 0),
        };
        var dialog = new DialogShell(this, $"{card.Title} settings", new StackPanel { Children = { scroll, buttons } });
        save.Click += (_, _) =>
        {
            var values = ReadFields(boxes, thinkingChoices);
            if (LauncherLogic.ValidateRoleSettings(values) is { } problem)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                return;
            }
            var kind = (string)kindChoices.First(r => r.IsChecked == true).Tag;
            if (string.IsNullOrWhiteSpace(prompt.Text) || kind != "reviewer" && string.IsNullOrWhiteSpace(description.Text))
            {
                error.Text = string.IsNullOrWhiteSpace(prompt.Text)
                    ? "Enter a system prompt: the orchestrator ignores a worker without one."
                    : "Enter a description: the Planner assigns Specialist tasks based on it.";
                error.Visibility = Visibility.Visible;
                return;
            }
            w.Kind = kind;
            w.Description = description.Text.Trim();
            w.SystemPrompt = prompt.Text;
            (w.Temperature, w.MaxTokens, w.RepetitionPenalty, w.ReasoningBudget, w.ContextLength, w.Thinking) =
                (values.Temperature.Trim(), values.MaxTokens.Trim(), values.RepetitionPenalty.Trim(), values.ReasoningBudget.Trim(), values.ContextLength.Trim(), values.Thinking);
            SaveWorkers();
            SyncCustomWorkers();
            Log($"{card.Title}: worker settings saved");
            Toast($"{card.Title}: settings saved");
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => prompt.Focus();
        return dialog;
    }

    // ---- model assignment

    private sealed record ModelChoice(string Label, string Display, string LoadPath, string? Variant, string Expected);

    private void ChangeModel_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RoleCard card)
            ChangeModel(card, (FrameworkElement)sender);
    }

    // anchor: the control the model list opens beneath (a standard card's button, or a compact card itself)
    private async void ChangeModel(RoleCard card, FrameworkElement anchor)
    {
        if (_process is not null)
            return;
        SetStatus("Loading installed models…", "#F7C873");
        List<ModelChoice> choices;
        try
        {
            choices = await FetchModelChoicesAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            Log($"Could not list Unsloth models: {exception.Message}");
            SetStatus("Could not reach Unsloth", "#FF667A");
            return;
        }
        SetStatus("Ready", "#667085");
        if (choices.Count == 0)
        {
            SetStatus("No installed text models found", "#F7C873");
            return;
        }

        var menu = new ContextMenu { Style = (Style)FindResource(typeof(ContextMenu)), PlacementTarget = anchor };
        foreach (var choice in choices)
        {
            var current = LauncherLogic.DescribeModel(choice.Display, choice.Variant);
            var item = new MenuItem
            {
                Header = choice.Label, Tag = choice, Style = (Style)FindResource(typeof(MenuItem)),
                IsChecked = card.ModelName == current.Name && (card.Quant == current.Quant || current.Quant == "—"),
            };
            item.Click += (_, _) => AssignModel(card, choice);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void AssignModel(RoleCard card, ModelChoice choice)
    {
        if (!card.IsCustom && !WriteRoleModel(SelectedProjectPath(), card.Role, choice))
            return;
        card.ModelId = choice.Display;
        card.ModelVariant = choice.Variant;
        card.SetModel(LauncherLogic.DescribeModel(choice.Display, choice.Variant));
        if (card.IsCustom)
        {
            card.Config.ModelPath = choice.LoadPath;
            card.Config.ModelExpected = choice.Expected;
        }
        Log($"{card.Title}: model set to {choice.Label}");
        Toast($"{card.Title}: {choice.Label}");
        SaveWorkers();
        if (card.IsCustom)
            SyncCustomWorkers();
    }

    // The orchestrator reads each built-in worker's model from the project's config.json.
    private bool WriteRoleModel(string project, string role, ModelChoice choice)
    {
        var path = Path.Combine(project, "config.json");
        if (project.Length == 0 || !File.Exists(path))
        {
            SetStatus("Initialize the project first", "#F7C873");
            return false;
        }
        if (LauncherLogic.IsTemplateConfig(path, _workflowRoot))
        {
            SetStatus("Select or initialize a project to change worker settings", "#F7C873");
            return false;
        }
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var settings = root["roles"]![role]!.AsObject();
            var load = (settings["auto_load"] ??= new JsonObject()).AsObject();
            load["model_path"] = choice.LoadPath;
            if (choice.Variant is null)
                load.Remove("gguf_variant");
            else
                load["gguf_variant"] = choice.Variant;
            settings["expected_model_contains"] = choice.Expected;
            settings["display_name"] = choice.Display; // the launcher's label; load paths for local models are opaque ids
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or NullReferenceException or InvalidOperationException)
        {
            Log($"Could not update config.json: {exception.Message}");
            SetStatus("Could not update config.json", "#FF667A");
            return false;
        }
    }

    // Installed text-generation GGUF models: local files, plus each downloaded quantization of cached Hugging Face repos.
    private async Task<List<ModelChoice>> FetchModelChoicesAsync()
    {
        var baseUrl = ManagementUrl(SelectedProjectPath()) ?? _apiBase;
        if (ApiKeyBox.Password.Length == 0)
            TryLoadApiKeyFile();
        var key = ApiKeyBox.Password.Trim();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        async Task<JsonDocument> Get(string route)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _httpLong.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
        }

        var local = new List<ModelChoice>();
        var repos = new List<string>();
        using (var doc = await Get("/api/models/local"))
        {
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                var partial = m.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
                if (partial || Str(m, "model_format") != "gguf" || Str(m, "task") != "text-generation")
                    continue;
                if (Str(m, "source") == "hf_cache")
                    repos.Add(Str(m, "id"));
                else
                    local.Add(new ModelChoice(Str(m, "display_name"), Str(m, "display_name"), Str(m, "id"), null, Str(m, "display_name")));
            }
        }

        var cached = await Task.WhenAll(repos.Select(async repo =>
        {
            try
            {
                using var doc = await Get("/api/models/gguf-variants?repo_id=" + Uri.EscapeDataString(repo));
                return doc.RootElement.GetProperty("variants").EnumerateArray()
                    .Where(v => v.TryGetProperty("downloaded", out var d) && d.ValueKind == JsonValueKind.True && Str(v, "quant").Length > 0)
                    .Select(v =>
                    {
                        var tail = repo.Split('/')[^1];
                        return new ModelChoice($"{tail}  ·  {Str(v, "quant")}", repo, repo, Str(v, "quant"), tail);
                    }).ToList();
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
            {
                return new List<ModelChoice>();
            }
        }));
        return local.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Concat(cached.SelectMany(c => c).OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    // ---------------------------------------------------------------- navigation, theme, window chrome

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        var tag = ((FrameworkElement)sender).Tag as string;
        ShowView(tag == "Options", chat: tag == "Chat");
    }

    private void ShowView(bool options, bool chat = false)
    {
        NavWorkspace.IsChecked = !options && !chat;
        NavChat.IsChecked = chat;
        NavOptions.IsChecked = options;
        Reveal(WorkArea, !options && !chat);
        Reveal(ChatView, chat);
        foreach (UIElement part in new UIElement[] { SplitterBar, DetailsPanel })
            Reveal(part, !options);
        Reveal(OptionsView, options);
        if (chat)
        {
            RefreshChatTarget();
            ChatInput.Focus();
        }
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

    // The rail has no room for the initialization form, so a project that needs it reopens the sidebar instead.
    private void RailProject_Click(object sender, RoutedEventArgs e)
    {
        if (InitializationPanel.Visibility == Visibility.Visible)
            SetSidebar(false);
        else
            Browse_Click(sender, e);
    }

    // ---- compact worker cards

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        _settings.Compact = CompactToggle.IsChecked == true;
        ApplyCompact();
        _settings.Save();
    }

    private void ApplyCompact()
    {
        CompactToggle.IsChecked = _settings.Compact;
        CardsList.ItemsPanel = (ItemsPanelTemplate)FindResource(_settings.Compact ? "CompactPanel" : "StandardPanel");
        CardsList.ItemTemplate = (DataTemplate)FindResource(_settings.Compact ? "CompactCardTemplate" : "RoleCardTemplate");
    }

    // The details pane may grow, but never past what leaves the sidebar and a usable output column on screen.
    private void FitDetailsColumn()
    {
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
        _settings.Theme = ThemeOledRadio.IsChecked == true ? "Oled" : ThemeGlassRadio.IsChecked == true ? "Glass" : ThemeGoldRadio.IsChecked == true ? "Gold" : "Flat";
        ApplyTheme();
        _settings.Save();
    }

    private void ApplyTheme()
    {
        var glass = _settings.Theme == "Glass";
        var oled = _settings.Theme == "Oled";
        var flat = _settings.Theme == "Flat" || oled; // OLED has the Flat shapes
        SetThemeBrush("AppBackground", oled ? "#000000" : flat ? "#101116" : glass ? "#E6101522" : "#0A0A0C");
        SetThemeBrush("TextPrimary", oled ? "#EEEEF5" : flat ? "#ECEEF5" : glass ? "#F4F7FF" : "#E8E8F0");
        SetThemeBrush("TitleBackground", oled ? "#000000" : flat ? "#101116" : glass ? "#DD111827" : "#0D0D10");
        SetThemeBrush("Panel", oled ? "#08080A" : flat ? "#16171D" : glass ? "#CC182131" : "#111116");
        SetThemeBrush("Card", oled ? "#131217" : flat ? "#1E2028" : glass ? "#B8233043" : "#16161C");
        SetThemeBrush("Hover", oled ? "#221E2E" : flat ? "#292C36" : glass ? "#B53A4B66" : "#1E1E26");
        SetThemeBrush("Border", oled ? "#26232F" : flat ? "#242630" : glass ? "#7795A8C8" : "#2A2A35");
        SetThemeBrush("ControlBorder", flat ? "#00000000" : glass ? "#7795A8C8" : "#2A2A35");
        SetThemeBrush("Muted", oled ? "#8A8798" : flat ? "#8E93A6" : glass ? "#BAC7DA" : "#8888A0");
        // Top-edge rim of raised surfaces: white fading out over the first 24 px, stronger on translucent Glass.
        var rim = (byte)(glass ? 0x50 : oled ? 0x18 : flat ? 0x22 : 0x1A);
        SetBrush("EdgeHighlight", new LinearGradientBrush(Color.FromArgb(rim, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0, 24))
            { MappingMode = BrushMappingMode.Absolute });
        ApplyAccent();
        Resources["ControlRadius"] = new CornerRadius(flat ? 17 : 7);
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
    };

    private string AccentKey => _settings.ThemeAccents.TryGetValue(_settings.Theme, out var key) && AccentColors.Any(x => x.Key == key)
        ? key
        : _settings.Theme switch { "Gold" => "Gold", "Glass" => "LightBlue", "Oled" => "Purple", _ => "Blue" };

    private void ApplyAccent()
    {
        var accent = AccentColors.First(x => x.Key == AccentKey);
        SetThemeBrush("Accent", accent.Accent);
        SetThemeBrush("AccentLight", accent.Light);
        SetThemeBrush("OnAccent", accent.On);
        AccentLabel.Text = "Accent: " + accent.Label;
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
        ApplyAccent();
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
            chat.Kill(entireProcessTree: true);
            chat.WaitForExit(2000);
        }
        CleanupChatTemp(); // a reply cut off by closing leaves its conversation and temp config behind otherwise
        DrainLines(); // a loading event that arrived just before the kill still counts
        UnloadOnClose(); // the models this run loaded, and those chat left loaded between messages
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
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}

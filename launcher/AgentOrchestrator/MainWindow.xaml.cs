using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
    private bool _followTail = true;
    private readonly StringBuilder _log = new(), _transcript = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _activeRole, _blockRole;
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
        SizeChanged += (_, _) => FitDetailsColumn();
        Loaded += (_, _) => FitDetailsColumn();
        _workflowRoot = LauncherLogic.FindWorkflowRoot();

        DetailsColumn.Width = new GridLength(Math.Clamp(_settings.DetailsWidth, 260, 1400));
        OnceToggle.IsChecked = _settings.Once;
        _settings.Theme = _settings.Theme is "Glass" or "Gold" or "Oled" ? _settings.Theme : "Flat";
        ThemeFlatRadio.IsChecked = _settings.Theme == "Flat";
        ThemeGoldRadio.IsChecked = _settings.Theme == "Gold";
        ThemeGlassRadio.IsChecked = _settings.Theme == "Glass";
        ThemeOledRadio.IsChecked = _settings.Theme == "Oled";
        ApplyTheme();
        _initializing = false;

        Closing += OnClosing;
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
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
        if (_workflowRoot is null)
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
        try
        {
            var python = LauncherLogic.FindPython();
            var startInfo = LauncherLogic.BuildStartInfo(python, _workflowRoot, project, _runKey, OnceToggle.IsChecked == true);
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };
            _process.ErrorDataReceived += (_, args) => { if (args.Data is not null) _lines.Enqueue(args.Data); };

            _settings.LastProject = project;
            _settings.Save();
            ResetRunView();
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
        }
        catch (Exception exception)
        {
            Log($"ERROR: {exception.Message}");
            SetStatus("Could not start", "#FF667A");
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
            SetRunning(false, preserveStatus: true);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_process is null || _process.HasExited)
            return;
        try
        {
            _process.Kill(entireProcessTree: true);
            Log("Stop requested.");
            SetStatus("Stopping…", "#F7C873");
        }
        catch (InvalidOperationException) { }
        // The killed orchestrator cannot run its own cleanup, so release the models from here.
        await UnloadModelsAsync(SelectedProjectPath());
    }

    private void ResetRunView()
    {
        OutputPanel.Children.Clear();
        EmptyState.Visibility = Visibility.Visible;
        _stream = null;
        _pendingTools.Clear();
        _lines.Clear();
        _followTail = true;
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
        foreach (var card in _cards)
            card.Editable = !running;
        if (running)
            SetStatus("Running", "#8B7CFF");
        else if (!preserveStatus)
            SetStatus("Ready", "#667085");
        RefreshPrimary();
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

        void Flush()
        {
            if (key is { } k && pending.Length > 0)
                AppendDelta(k.Role, k.Kind, k.Index, name, pending.ToString());
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
                    Log(line);
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
        var role = Str(ev, "role");
        var card = CardFor(role);
        switch (Str(ev, "e"))
        {
            case "loading":
                EndStream(false);
                Activate(role, "Loading model");
                Log($"[{role}] loading {Str(ev, "path")} ...");
                break;
            case "model":
                EndStream(false);
                Activate(role, "Working");
                card?.SetModel(LauncherLogic.DescribeModel(Str(ev, "id"), ConfiguredVariant(role)));
                Log($"[{role}] {Str(ev, "id")}");
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
            case "verdict":
                EndStream(false);
                Log($"Review round {Num(ev, "round")}: {Str(ev, "verdict")}");
                AddNote($"REVIEW  {Str(ev, "verdict")}", $"Task {Str(ev, "id")}, round {Num(ev, "round")}");
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
        }
        card.IsActive = true;
        card.Status = status;
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
        if (kind == "text")
        {
            var panel = new StackPanel { Margin = new Thickness(2, 0, 0, 10) };
            block.Label = Label("RESPONSE");
            block.Body = BodyText("", mono: false, muted: false);
            panel.Children.Add(block.Label);
            panel.Children.Add(block.Body);
            AddBlock(panel, "\n[response]\n");
            return block;
        }

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
            body = block.Body = BodyText("", mono: false, muted: true);

        block.Expander = BuildExpander(kind == "tool" ? "TOOL" : "THINKING", "", body, out block.Preview);
        block.Expander.IsExpanded = true; // visible while it streams, folded away once it finishes
        block.Expander.Expanded += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        block.Expander.Collapsed += (_, _) => { if (!block.Programmatic) block.Touched = true; };
        AddBlock(block.Expander, kind == "tool" ? "\n[tool call]\n" : "\n[thinking]\n");
        return block;
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
        var panel = new StackPanel { Margin = new Thickness(2, 0, 0, 10) };
        panel.Children.Add(Label(final ? "FINAL RESPONSE" : "RESPONSE", accent: final));
        panel.Children.Add(BodyText(text, mono: false, muted: false));
        AddBlock(panel, $"[{(final ? "final" : "response")}]\n{text}\n");
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
    private TextBox BodyText(string text, bool mono, bool muted)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = mono ? new FontFamily("Consolas") : new FontFamily("Segoe UI"),
            FontSize = mono ? 12 : 13,
            FontStyle = muted ? FontStyles.Italic : FontStyles.Normal,
            Cursor = Cursors.IBeam,
        };
        box.SetResourceReference(TextBox.ForegroundProperty, muted ? "Muted" : "TextPrimary");
        box.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (e.Delta > 0)
                _followTail = false;
            OutputScroll.ScrollToVerticalOffset(OutputScroll.VerticalOffset - e.Delta);
        };
        return box;
    }

    // Follow new output unless the user has scrolled up; scrolling back to the bottom resumes following.
    private void OutputScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            _followTail = OutputScroll.VerticalOffset >= OutputScroll.ScrollableHeight - 8;
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

    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Visibility = LogToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (LogToggle.IsChecked == true)
            LogBox.ScrollToEnd();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_log.ToString(), "Full log copied", "Log is empty");

    private void CopyOutput_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_transcript.ToString(), "Output copied", "No output yet");

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        OutputPanel.Children.Clear();
        _stream = null;
        _pendingTools.Clear();
        _transcript.Clear();
        _blockRole = null;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void CopyToClipboard(string text, string done, string empty)
    {
        if (text.Length == 0)
        {
            SetStatus(empty, "#F7C873");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            SetStatus(done, "#5BD6A2");
        }
        catch (Exception exception)
        {
            Log($"Could not copy: {exception.Message}");
            SetStatus("Could not copy", "#FF667A");
        }
    }

    // ---------------------------------------------------------------- live speed (Unsloth's own monitor endpoint)

    private async void Poll_Tick(object? sender, EventArgs e)
    {
        if (_polling || _process is null || _activeRole is null || CardFor(_activeRole) is not { } card)
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
            if (best is not { } live || _process is null)
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

    private async Task UnloadModelsAsync(string project)
    {
        try
        {
            using var config = ReadConfig(project);
            if (config is null || !config.RootElement.TryGetProperty("roles", out var roles))
                return;
            var paths = roles.EnumerateObject()
                .Select(r => r.Value.TryGetProperty("auto_load", out var l) && l.TryGetProperty("model_path", out var p) ? p.GetString() : null)
                .Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            foreach (var path in paths)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _apiBase + "/api/inference/unload")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { model_path = path }), Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _runKey);
                using var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    Log($"Unloaded {path}");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
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
            var copied = LauncherLogic.InitializeProject(_workflowRoot, project, _customProjectFile, _customConfigFile);
            Log($"{(existed ? "Initialized" : "Created and initialized")} {project}");
            if (copied.Length > 0)
                Log($"Added: {string.Join(", ", copied)}");
            if (_customProjectFile is not null && !copied.Contains("PROJECT.md"))
                Log("Existing PROJECT.md was preserved; the selected custom file was not copied.");
            if (_customConfigFile is not null && !copied.Contains("config.json"))
                Log("Existing config.json was preserved; the selected custom file was not copied.");
            SetStatus("Project initialized", "#5BD6A2");
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
            var card = new RoleCard(w.Id, w.Custom ? w.Title : null, w.Custom) { ModelId = w.ModelId, ModelVariant = w.ModelVariant };
            if (w.Custom && w.ModelId is not null)
                card.SetModel(LauncherLogic.DescribeModel(w.ModelId, w.ModelVariant));
            _cards.Add(card);
        }
    }

    private void SaveWorkers()
    {
        _settings.Workers = _cards.Select(c => new WorkerSetting
        {
            Id = c.Role, Title = c.Title, Custom = c.IsCustom, ModelId = c.ModelId, ModelVariant = c.ModelVariant,
        }).ToList();
        _settings.Save();
    }

    private void AddWorker_Click(object sender, RoutedEventArgs e)
    {
        var n = _cards.Count(c => c.IsCustom) + 1;
        _cards.Add(new RoleCard(Guid.NewGuid().ToString("N"), $"WORKER {n}", custom: true));
        SaveWorkers();
    }

    private void RemoveWorker_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RoleCard { IsCustom: true } card)
        {
            _cards.Remove(card);
            SaveWorkers();
        }
    }

    private void TitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RoleCard { IsCustom: true } card)
            return;
        card.Title = card.Title.Trim().Length == 0 ? "WORKER" : card.Title.Trim();
        SaveWorkers();
    }

    // ---- drag-and-drop reordering: the card follows the pointer and the others make room live

    private RoleCard? _dragCard;
    private Point _grab;

    private void Handle_Down(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RoleCard card)
            return;
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

    // ---- model assignment

    private sealed record ModelChoice(string Label, string Display, string LoadPath, string? Variant, string Expected);

    private async void ChangeModel_Click(object sender, RoutedEventArgs e)
    {
        if (_process is not null || ((FrameworkElement)sender).DataContext is not RoleCard card)
            return;
        var button = (Button)sender;
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

        var menu = new ContextMenu { Style = (Style)FindResource(typeof(ContextMenu)), PlacementTarget = button };
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
        Log($"{card.Title}: model set to {choice.Label}");
        SaveWorkers();
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

    private void Nav_Click(object sender, RoutedEventArgs e) =>
        ShowView(((FrameworkElement)sender).Tag as string == "Options");

    private void ShowView(bool options)
    {
        NavWorkspace.IsChecked = !options;
        NavOptions.IsChecked = options;
        foreach (UIElement part in new UIElement[] { WorkArea, SplitterBar, DetailsPanel })
            part.Visibility = options ? Visibility.Collapsed : Visibility.Visible;
        OptionsView.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
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
        DetailsColumn.MaxWidth = Math.Max(DetailsColumn.MinWidth, ActualWidth - 260 - 380 - 6);
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            UnloadModelsAsync(SelectedProjectPath()).Wait(TimeSpan.FromSeconds(4));
        }
        _settings.Save();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AgentOrchestrator;

// One card in the right-hand column: model identity plus live speed and per-turn token totals.
internal sealed class RoleCard : INotifyPropertyChanged
{
    private string _modelName = "Not loaded", _weights = "—", _quant = "—", _status = "Idle", _tps = "—";
    private bool _isActive, _dropHover, _dragging, _editable = true;
    private string _title;
    private long _bankedPrompt, _bankedCompletion, _livePrompt, _liveCompletion;

    // role is the stable id: planner/implementer/reviewer for built-in workers, a guid for user-created ones.
    public RoleCard(string role, string? title = null, bool custom = false)
    {
        Role = role;
        IsCustom = custom;
        _title = title ?? role.ToUpperInvariant();
    }

    public string Role { get; }
    public bool IsCustom { get; }
    public bool IsBuiltIn => !IsCustom;
    public string Title { get => _title; set => Set(ref _title, IsCustom ? value : _title); }
    public bool IsDragging { get => _dragging; set => Set(ref _dragging, value); }
    public bool Editable { get => _editable; set => Set(ref _editable, value); }
    public bool DropHover { get => _dropHover; set => Set(ref _dropHover, value); }
    // Display name, weights and quantization of the assigned model (custom workers keep these in settings).
    public string? ModelId { get; set; }
    public string? ModelVariant { get; set; }
    // Custom workers' settings (kind, prompt, sampling, model path); written back by MainWindow.CurrentWorkers.
    public WorkerSetting Config { get; set; } = new();
    // Why the orchestrator would ignore this custom worker (null = fine); refreshed by MainWindow.RefreshSetupProblems.
    private string? _setupProblem;
    public string? SetupProblem { get => _setupProblem; set { if (_setupProblem != value) { _setupProblem = value; Raise(nameof(SetupProblem)); Raise(nameof(HasSetupProblem)); Raise(nameof(SetupTip)); } } }
    public bool HasSetupProblem => _setupProblem is not null;
    public string SetupTip => $"The orchestrator will skip this worker: {_setupProblem}";
    public string ModelName { get => _modelName; private set => Set(ref _modelName, value); }
    public string Weights { get => _weights; private set => Set(ref _weights, value); }
    public string Quant { get => _quant; private set => Set(ref _quant, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string TokensPerSec { get => _tps; private set => Set(ref _tps, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public string TurnTotal => (_bankedPrompt + _bankedCompletion + _livePrompt + _liveCompletion).ToString("N0");
    public string TurnDetail => $"↑ {_bankedPrompt + _livePrompt:N0} prompt   ↓ {_bankedCompletion + _liveCompletion:N0} generated";
    // Tokens this worker used in the last completed task ("—" before one completes); unchanged while the next task runs.
    public string TaskTokens => _taskTokens?.ToString("N0") ?? "—";
    private long _taskSum; // usage-confirmed tokens since task_start: what the turn totals settle on, never live estimates
    private long? _taskTokens;

    public void BeginTask() => _taskSum = 0;

    public void EndTask()
    {
        _taskTokens = _taskSum;
        Raise(nameof(TaskTokens));
    }

    public void SetModel(LauncherLogic.ModelInfo info)
    {
        ModelName = info.Name;
        Weights = info.Weights;
        Quant = info.Quant;
    }

    public void BeginTurn()
    {
        _bankedPrompt = _bankedCompletion = _livePrompt = _liveCompletion = 0;
        _recent.Clear();
        TokensPerSec = "—";
        NotifyTotals();
    }

    // A finished model call: its own numbers replace whatever the live poller had estimated.
    public void AddUsage(long prompt, long completion, double tps)
    {
        _bankedPrompt += prompt;
        _bankedCompletion += completion;
        _taskSum += prompt + completion;
        _livePrompt = _liveCompletion = 0;
        _recent.Clear();
        if (tps > 0)
            TokensPerSec = FormatRate(tps);
        NotifyTotals();
    }

    // Unsloth's monitor row for the call that is running right now.
    public void SetLive(long prompt, long completion, double? decodeTps, double? promptTps)
    {
        _livePrompt = prompt;
        if (_liveCompletion > 0) // streamed deltas are counted exactly; the monitor only supplies the prompt side
        {
            NotifyTotals();
            return;
        }
        _liveCompletion = completion;
        if (decodeTps > 0)
            TokensPerSec = FormatRate(decodeTps.Value);
        else if (promptTps > 0)
            TokensPerSec = $"Prompt: {promptTps.Value:N0} tok/s";
        NotifyTotals();
    }

    // Streamed delta chunks (about one token each) since the last usage event; the rate uses a ~1 s sliding window.
    public long LiveCompletion => _liveCompletion;

    public void AddDeltas(int count, long nowMs)
    {
        _liveCompletion += count;
        _recent.Enqueue((nowMs, count));
        while (_recent.Count > 1 && nowMs - _recent.Peek().Ms > WindowMs)
            _recent.Dequeue();
        var (first, firstCount) = _recent.Peek();
        if (nowMs > first) // the oldest batch only marks where the window starts
            TokensPerSec = FormatRate((_recent.Sum(r => (double)r.Count) - firstCount) * 1000.0 / (nowMs - first));
        NotifyTotals();
    }

    private const long WindowMs = 1000;
    private readonly Queue<(long Ms, int Count)> _recent = new();

    public void ClearLive()
    {
        _livePrompt = _liveCompletion = 0;
        _recent.Clear();
        NotifyTotals();
    }

    private static string FormatRate(double d) => (d >= 10 ? d.ToString("0.0") : d.ToString("0.00")) + " tok/s";

    private void NotifyTotals()
    {
        Raise(nameof(TurnTotal));
        Raise(nameof(TurnDetail));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Raise(name!);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class AppSettings
{
    // Worker cards in display order. Built-in workers keep their fixed title; their models live in the project's config.json.
    public List<WorkerSetting> Workers { get; set; } = new();
    // Named worker sets, global across projects.
    public List<WorkerPreset> Presets { get; set; } = new();
    public string Theme { get; set; } = "Parchment";
    public Dictionary<string, string> ThemeAccents { get; set; } = new();
    public double DetailsWidth { get; set; } = 340;
    public string LastProject { get; set; } = "";
    public bool Once { get; set; } = true;
    public bool Compact { get; set; }
    public bool SidebarCollapsed { get; set; }
    // Chat view: "single" (one worker) or "multi" (Planner -> Implementer -> Reviewer), and the single-mode target worker id.
    public string ChatMode { get; set; } = "single";
    public string ChatWorker { get; set; } = "implementer";
    public string CodeWorker { get; set; } = "implementer"; // the Code view's target worker id
    // true: chat models stay loaded until the app closes; false: each turn unloads the models it loaded.
    public bool ChatKeepLoaded { get; set; } = true;
    // Where local models run: "unsloth" (Unsloth Studio's API) or "llamacpp" (llama-server from <data>\llama.cpp).
    public string Runtime { get; set; } = "unsloth";
    public string ModelsFolder { get; set; } = ""; // llamacpp only: searched for GGUF files besides the Hugging Face cache; "" = none
    // Window position and size in device-independent pixels; null until the window has been closed once.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public string LastConversation { get; set; } = ""; // the Code conversation open at exit, reopened at startup

    // The composer's extras (Chat and Code): web search, utilities, and the skills turned on (folder names under SkillsRoot).
    public bool UseSearch { get; set; }
    public bool UseTools { get; set; }
    public List<string> Skills { get; set; } = new();
    public static string SkillsRoot => Path.Combine(DataRoot, "skills");

    // The launcher's data folder (settings, conversations, worker config). AGENT_ORCHESTRATOR_DATA redirects it (used by
    // UI tests so real data stays untouched).
    public static string DataRoot => Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Agent Orchestrator");

    private static string FilePath => Path.Combine(DataRoot, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed class WorkerSetting
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Custom { get; set; }
    public string? ModelId { get; set; }      // custom workers only
    public string? ModelVariant { get; set; } // custom workers only
    // Custom workers only. ModelPath/ModelExpected rebuild auto_load.model_path and expected_model_contains
    // (ModelId is the display name, ModelVariant the gguf_variant).
    public string? ModelPath { get; set; }
    public string? ModelExpected { get; set; }
    public string? GgufFile { get; set; } // auto_load.gguf_file: a llama.cpp model file outside the Hugging Face cache
    public string Kind { get; set; } = "specialist"; // "specialist" | "reviewer"
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    // Same text form as the built-in Settings dialog; "" = inherit the Implementer's value.
    public string Temperature { get; set; } = "";
    public string MaxTokens { get; set; } = "";
    public string RepetitionPenalty { get; set; } = "";
    public string ReasoningBudget { get; set; } = "";
    public string ContextLength { get; set; } = "";
    public string Thinking { get; set; } = "Auto";

    public WorkerSetting Clone() => (WorkerSetting)MemberwiseClone();
}

// A named snapshot of the worker cards plus each built-in role's roles.<role> object from the project's config.json.
internal sealed class WorkerPreset
{
    public string Name { get; set; } = "";
    public List<WorkerSetting> Workers { get; set; } = new();
    public Dictionary<string, string> Roles { get; set; } = new(); // built-in role -> its config.json object as JSON; empty when saved without a project config
    // config.json task_limits as JSON; "{}" = none set (the defaults); null = saved without a project config, or before presets carried it.
    public string? TaskLimits { get; set; }
}

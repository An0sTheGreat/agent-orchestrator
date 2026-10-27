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
    public string ModelName { get => _modelName; private set => Set(ref _modelName, value); }
    public string Weights { get => _weights; private set => Set(ref _weights, value); }
    public string Quant { get => _quant; private set => Set(ref _quant, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string TokensPerSec { get => _tps; private set => Set(ref _tps, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public string TurnTotal => (_bankedPrompt + _bankedCompletion + _livePrompt + _liveCompletion).ToString("N0");
    public string TurnDetail => $"↑ {_bankedPrompt + _livePrompt:N0} prompt   ↓ {_bankedCompletion + _liveCompletion:N0} generated";

    public void SetModel(LauncherLogic.ModelInfo info)
    {
        ModelName = info.Name;
        Weights = info.Weights;
        Quant = info.Quant;
    }

    public void BeginTurn()
    {
        _bankedPrompt = _bankedCompletion = _livePrompt = _liveCompletion = 0;
        TokensPerSec = "—";
        NotifyTotals();
    }

    // A finished model call: its own numbers replace whatever the live poller had estimated.
    public void AddUsage(long prompt, long completion, double tps)
    {
        _bankedPrompt += prompt;
        _bankedCompletion += completion;
        _livePrompt = _liveCompletion = 0;
        if (tps > 0)
            TokensPerSec = FormatRate(tps);
        NotifyTotals();
    }

    // Unsloth's monitor row for the call that is running right now.
    public void SetLive(long prompt, long completion, double? decodeTps, double? promptTps)
    {
        _livePrompt = prompt;
        _liveCompletion = completion;
        if (decodeTps > 0)
            TokensPerSec = FormatRate(decodeTps.Value);
        else if (promptTps > 0)
            TokensPerSec = $"Prompt: {promptTps.Value:N0} tok/s";
        NotifyTotals();
    }

    public void ClearLive()
    {
        _livePrompt = _liveCompletion = 0;
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
    public string Theme { get; set; } = "Flat";
    public Dictionary<string, string> ThemeAccents { get; set; } = new();
    public double DetailsWidth { get; set; } = 340;
    public string LastProject { get; set; } = "";
    public bool Once { get; set; } = true;
    public bool Compact { get; set; }
    public bool SidebarCollapsed { get; set; }
    // Window position and size in device-independent pixels; null until the window has been closed once.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // AGENT_ORCHESTRATOR_DATA redirects the settings folder (used by UI tests so real settings stay untouched).
    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("AGENT_ORCHESTRATOR_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Agent Orchestrator"),
        "settings.json");

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
}

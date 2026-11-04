using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator;

// One card in the right-hand column: model identity plus live speed and per-turn token totals.
internal sealed class RoleCard : INotifyPropertyChanged
{
    private string _modelName = "Not loaded", _weights = "—", _quant = "—", _status = "Idle", _tps = "—";
    private bool _isActive, _selected, _editable = true;
    private string _title;
    private long _bankedPrompt, _bankedCompletion, _livePrompt, _liveCompletion;

    // role is the stable id: planner/implementer/reviewer for built-in workers, a guid for user-created ones.
    public RoleCard(string role, string? title = null, bool custom = false)
    {
        Role = role;
        IsCustom = custom;
        _title = title ?? (role == "planner" ? "LEAD" : role.ToUpperInvariant());
    }

    // The planner's tab: the built-in roles it currently covers ("Planner · Implementer · Reviewer"); "" on other tabs.
    private string _covers = "";
    public string Covers { get => _covers; set { if (_covers != value) { _covers = value; Raise(nameof(Covers)); Raise(nameof(HasCovers)); } } }
    public bool HasCovers => _covers.Length > 0;

    // COMPACTOR's tab while compaction runs on MAIN's model (roles.compactor absent or "same_as": "planner").
    private bool _followsMain;
    public bool FollowsMain { get => _followsMain; set { if (_followsMain != value) { _followsMain = value; Raise(nameof(FollowsMain)); } } }

    public string Role { get; }
    public bool IsCustom { get; }
    public bool IsBuiltIn => !IsCustom;
    public string Title { get => _title; set => Set(ref _title, IsCustom ? value : _title); }
    public bool Editable { get => _editable; set => Set(ref _editable, value); }
    // The worker tab whose settings the column shows (MainWindow.WorkerTabs.cs); a selected tab stays shown even when Hidden.
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    // Collapsed in the worker panel (MainWindow.RefreshCardVisibility: an implementer or reviewer not added).
    private bool _hidden;
    public bool Hidden { get => _hidden; set => Set(ref _hidden, value); }
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
    // Compact forms for the worker tab (1.5k, 2M); the full numbers are in its tooltip.
    public string TaskTokensShort => _taskTokens is { } t ? Compact(t) : "—";
    public string TurnTotalShort => Compact(_bankedPrompt + _bankedCompletion + _livePrompt + _liveCompletion);
    public string TabTip => $"{Title}\n{ModelName}\n{TokensPerSec}\nLast task: {TaskTokens} tokens · this turn: {TurnTotal} tokens";
    private static string Compact(long n) => n switch
    {
        < 1000 => n.ToString(),
        < 1_000_000 => (n / 1000.0).ToString("0.#") + "k",
        _ => (n / 1_000_000.0).ToString("0.#") + "M",
    };
    private long _taskSum; // usage-confirmed tokens since task_start: what the turn totals settle on, never live estimates
    private long? _taskTokens;

    public void BeginTask() => _taskSum = 0;

    public void EndTask()
    {
        _taskTokens = _taskSum;
        Raise(nameof(TaskTokens));
        Raise(nameof(TaskTokensShort));
        Raise(nameof(TabTip));
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
        Raise(nameof(TurnTotalShort));
        Raise(nameof(TabTip));
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

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(Title) or nameof(ModelName) or nameof(TokensPerSec))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabTip)));
    }
}

internal sealed class AppSettings
{
    // Worker cards in display order. Built-in workers keep their fixed title; their models live in the project's config.json.
    public List<WorkerSetting> Workers { get; set; } = new();
    // Settings > Agents: "dual" (Lead + Reviewer: the lead does the work) or "tri" (Lead + Implementer + Reviewer). Settings
    // from before it: tri when the Implementer had been added through "+", else dual (Load).
    public string WorkflowMode { get; set; } = "dual";
    // The built-in roles the workflow shows; a role not in it runs as the planner ("same_as": "planner").
    [JsonIgnore]
    public List<string> AddedRoles => WorkflowMode == "tri" ? ["implementer", "reviewer"] : ["reviewer"];
    // Settings files from before WorkflowMode: the built-in roles added through "+". Read once by Load, never written.
    [JsonPropertyName("AddedRoles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OldAddedRoles { get; set; }
    // The one-time move to AddedRoles (MainWindow.MigrateRoles) has run; a new user's settings start with it done.
    public bool RolesMigrated { get; set; }
    // Named worker sets, global across projects.
    public List<WorkerPreset> Presets { get; set; } = new();
    // Named sets of Model settings option fields (ModelOptions keys), global across workers.
    public List<ModelPreset> ModelPresets { get; set; } = new();
    public string Theme { get; set; } = "Graphite"; // its default accent is light gray (Silver)
    public Dictionary<string, string> ThemeAccents { get; set; } = new();
    public double DetailsWidth { get; set; } = 340;
    public bool DetailsCollapsed { get; set; } = true; // the worker details column shows as the dot rail
    public bool PromptsExpanded { get; set; } // the worker settings' system prompts show (collapsed by default)
    public bool ModelAdvanced { get; set; } // the worker settings' Advanced switch: the Memory, Speed, Output and Server groups
    public bool ContextExpanded { get; set; } // the worker details column's Context section (collapsed by default)
    public string LastProject { get; set; } = "";
    public bool Once { get; set; } = true;
    public string SelectedWorker { get; set; } = ""; // the worker tab shown in the details column ("" = the first)
    public bool SidebarCollapsed { get; set; }
    // The sidebar's profile: a display name ("" = the Windows user name) and the picture copied into the data folder.
    public string ProfileName { get; set; } = "";
    public string ProfilePicture { get; set; } = "";
    // The Chat view's target worker id (it may delegate to the others). The Code view always talks to the Planner, the lead.
    // Older settings files' ChatMode / CodeMode / CodeWorker are ignored on load.
    public string ChatWorker { get; set; } = "planner";
    // Thinking effort sent with Chat and Code messages: "" (the model's default), off, low, medium, high or max.
    public string ChatEffort { get; set; } = "";
    // true: chat models stay loaded until the app closes; false: each turn unloads the models it loaded.
    public bool ChatKeepLoaded { get; set; } = true;
    // Chat and Code conversations (messages and the message box) at a readable width in the middle of the column.
    public bool CenterChat { get; set; } = true;
    // Replies' and messages' text: "compact" (the denser layout), "comfortable" or "large" (ChatGPT-like spacing).
    public string ReadingSize { get; set; } = "comfortable";
    // How many emoji the models put in replies (an instruction the engine adds): "none", "less", "standard" or "more".
    public string Emoji { get; set; } = "standard";
    // Code's Reviewer chip (Review every turn): the Reviewer checks each Code turn at its end (orchestrator.reviewed_turn).
    public bool ReviewEveryTurn { get; set; }
    // The toggle beside CHAT and CODE: off, each run of a reply's thinking and tool steps folds into one THINKING row.
    public bool ViewThinking { get; set; } = true;
    // Centered conversations: the context ring keeps its card open beside the conversation until clicked again.
    public bool ContextPinned { get; set; }
    public bool ContextPercent { get; set; } // the Context card shows the used share as a number beside its ring
    // The in-app browser: open in the right column, its last page, and whether it opens the pages the model reads.
    public bool BrowserOpen { get; set; }
    public string BrowserUrl { get; set; } = "";
    public bool BrowserFollow { get; set; } = true;
    // Where local models run: "unsloth" (Unsloth Studio's API) or "llamacpp" (llama-server from <data>\llama.cpp).
    public string Runtime { get; set; } = "unsloth";
    public string ModelsFolder { get; set; } = ""; // llamacpp only: searched for GGUF files besides the Hugging Face cache; "" = none
    // Who runs a Code turn's agent loop: "" (the built-in engine) or "pi" (Pi from <data>\pi, LauncherLogic.PiCli).
    public string CodeEngine { get; set; } = "";
    // Whether sandboxed model commands (run_command) may use the network without asking; off: each use is approved.
    public bool SandboxNetwork { get; set; }
    // Remote access (RemoteServer.cs, docs/remote-access.md): the browser page on 127.0.0.1:RemotePort (the next free port
    // when taken), signed in with the password whose RemoteAuth.HashPassword string is kept here (never the password).
    public bool RemoteAccess { get; set; }
    public int RemotePort { get; set; } = 8765;
    public string? RemotePasswordHash { get; set; }
    // Ponytail (prompts/ponytail.txt) added after every model's system prompt: "" (off), lite, full or ultra (/ponytail).
    public string Ponytail { get; set; } = "";
    public bool NotifyWhenDone { get; set; } // a desktop notification when a reply or project run finishes (MainWindow.Notify.cs)
    public string CommandPermissions { get; set; } = "safe"; // run_command: "ask", "safe" or "all" (the Code composer's permissions dropup)
    // Window position and size in device-independent pixels; null until the window has been closed once.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public string LastConversation { get; set; } = ""; // the Code conversation open at exit, reopened at startup

    // The composer's extras (Chat and Code): web search, utilities, and the skills turned on (folder names under SkillsRoot).
    public bool UseSearch { get; set; }
    public bool UseVision { get; set; } = true; // off: no image or screenshot reaches a model (the request's "vision")
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
        if (!File.Exists(FilePath))
            return new() { RolesMigrated = true }; // a new user: nothing to migrate
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            if (settings.OldAddedRoles is { } old)
                (settings.WorkflowMode, settings.OldAddedRoles) = (old.Contains("implementer") ? "tri" : "dual", null);
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new() { LoadFailed = true }; }
    }

    // settings.json exists but could not be read (locked, unreadable, bad JSON): these are defaults, so Save never
    // writes them over the user's file.
    [JsonIgnore]
    public bool LoadFailed { get; private init; }

    public void Save()
    {
        if (LoadFailed)
            return;
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
    // The Model settings window's fields (ModelOptions table keys; only non-empty ones). Supersedes the six above.
    public Dictionary<string, string> ModelOptions { get; set; } = new();

    public WorkerSetting Clone()
    {
        var copy = (WorkerSetting)MemberwiseClone();
        copy.ModelOptions = new Dictionary<string, string>(ModelOptions);
        return copy;
    }
}

internal sealed class ModelPreset
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Options { get; set; } = new();
    public string SystemPrompt { get; set; } = ""; // "" = none stored (blank, or the worker kind's default when saved)
    // The model it was saved for (the picker's name for it): presets list only for that model, whatever the role.
    // "" = saved before presets had a model: listed for every model.
    public string Model { get; set; } = "";
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

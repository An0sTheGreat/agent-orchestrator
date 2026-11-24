using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace AgentOrchestrator;

// The composer's Search / Tools toggles, shared by the Chat and Code views, and the skills sent (the Skills page sets them). Each message sends them in its
// request (LauncherLogic.Extras); orchestrator.py turns them into tools.
public partial class MainWindow
{
    // initProject: also the init-projMD skill, for a Code conversation "Create PROJECT.md with a model…" started.
    // message: also every skill it names as "/name" or "$name" (skill completion), switched on or not.
    private LauncherLogic.Extras CurrentExtras(bool initProject = false, string? message = null) =>
        new(_settings.UseSearch, _settings.UseTools,
            AllSkills().Where(s => HasSkill(s) || InvokesSkill(message, s.Name) ||
                initProject && Path.GetFileName(s.Folder).Equals(InitProjectSkill, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Folder).ToList(), Vision: _settings.UseVision, Emoji: _settings.Emoji,
            Computer: _settings.ComputerUse ? _settings.ComputerApps.Select(ComputerLogic.AppName).ToList() : [],
            ImageGen: _settings.UseImageGen && (_settings.ChatImage.SharedModel.Length == 0 || _settings.ShareHost.Length > 0) // the primary PC's: only while connected
                ? AgentOrchestrator.ImageGen.ChatRequest(_settings.ChatImage, _settings.ImageKeepBoth) : null);

    // The user's skills (AppSettings.SkillsRoot) and the ones bundled with the app (<workflow root>\skills).
    private List<LauncherLogic.Skill> AllSkills() =>
        LauncherLogic.ListSkills(AppSettings.SkillsRoot, _workflowRoot is null ? null : Path.Combine(_workflowRoot, "skills"));

    private void RefreshExtras()
    {
        foreach (var (search, tools) in new[] { (ChatSearchToggle, ChatToolsToggle), (CodeSearchToggle, CodeToolsToggle) })
        {
            search.IsChecked = _settings.UseSearch;
            (search == ChatSearchToggle ? ChatVisionToggle : CodeVisionToggle).IsChecked = _settings.UseVision;
            tools.IsChecked = _settings.UseTools;
        }
        RefreshImageChip();
        RefreshVision();
    }

    private void ExtraToggle_Click(object sender, RoutedEventArgs e)
    {
        var on = ((ToggleButton)sender).IsChecked == true;
        switch (((FrameworkElement)sender).Tag as string)
        {
            case "search":
                _settings.UseSearch = on;
                break;
            case "vision":
                _settings.UseVision = on;
                break;
            default:
                _settings.UseTools = on;
                break;
        }
        _settings.Save();
        RefreshExtras();
    }

    // The Vision switches turn grey, with a tip saying why, while the model a message goes to cannot see: Chat's target, or
    // Code's lead (MAIN). Read off the UI thread (the Hugging Face cache may be large); only the latest read applies.
    private int _visionRead;

    private async void RefreshVision()
    {
        if (_chat is null || _workflowRoot is null)
            return;
        var mine = ++_visionRead;
        var (folder, hub) = (_settings.ModelsFolder ?? "", LauncherLogic.HubCache());
        var loads = new[] { ChatTarget(), CardFor("planner") }.Select(card => card is null ? null : VisionLoad(card)).ToArray();
        var seeing = await Task.Run(() => loads.Select(load => LauncherLogic.CanSee(load, folder, hub)).ToArray());
        if (mine != _visionRead)
            return;
        foreach (var (toggle, sees) in new[] { (ChatVisionToggle, seeing[0]), (CodeVisionToggle, seeing[1]) })
        {
            toggle.IsEnabled = sees;
            toggle.ToolTip = sees ? "Send attached images and browser screenshots to the model" : "Vision is not supported for the selected model(s).";
        }
    }

    // The auto_load a card's model loads with (a custom worker's from its settings), or null for a remote role or none.
    private JsonObject? VisionLoad(RoleCard card)
    {
        if (card.IsCustom)
            return card.Config.ModelPath is { } path ? new JsonObject { ["model_path"] = path, ["gguf_variant"] = card.Config.ModelVariant, ["gguf_file"] = card.Config.GgufFile } : null;
        try
        {
            var roles = JsonNode.Parse(File.ReadAllText(ActiveConfigPath()))?["roles"] as JsonObject;
            var role = card.Role == LauncherLogic.Compactor && !LauncherLogic.CompactorHasOwn(roles) ? "planner" : ConfigRole(card.Role);
            return roles?[role]?["auto_load"]?.DeepClone() as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { return null; }
    }

    // ---- thinking effort: one choice (_settings.ChatEffort) for Chat and Code, sent only when the model it reaches thinks

    private static readonly (string Value, string Label)[] Efforts =
        [("", "Default"), ("off", "Off (no thinking)"), ("minimal", "Minimal"), ("low", "Low"), ("medium", "Medium"), ("high", "High"), ("xhigh", "Extra high"), ("max", "Max")];
    private static readonly string[] AnyEfforts = ["off", "low", "medium", "high", "max"]; // a remote API, or a template that could not be read

    // The effort levels a worker's model takes besides Default (empty: it does not think), from the model saved in the active
    // config.json (roles[role] for a built-in worker, its custom_workers entry for a custom one), loaded or not: the GGUF's
    // chat template when it can be read (LauncherLogic.EffortLevels), else IsThinkingModel and every level.
    private string[] EffortLevels(RoleCard? card) => EffortInfo(card).Levels;

    // The levels, and the level Default resolves to (the template's reasoning_effort|default(...), LauncherLogic.EffortDefault;
    // null for a remote API, an unreadable template or one that sets none).
    private (string[] Levels, string? Default) EffortInfo(RoleCard? card)
    {
        if (card is null)
            return ([], null);
        JsonObject? entry = null;
        try
        {
            var path = ActiveConfigPath();
            if (File.Exists(path))
            {
                var root = JsonNode.Parse(File.ReadAllText(path));
                entry = card.IsCustom
                    ? root?["custom_workers"]?.AsArray().OfType<JsonObject>().FirstOrDefault(w => w["id"]?.ToString() == card.Role)
                    : root?["roles"]?[ConfigRole(card.Role)] as JsonObject; // a not-added role runs as the planner
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        if (LauncherLogic.IsRemote(entry)) // a remote API decides for itself (and ignores a level its model lacks); names say little there
            return (AnyEfforts, null);
        var load = entry?["auto_load"] as JsonObject;
        var folder = _settings.ModelsFolder ?? "";
        if (!LauncherLogic.IsThinkingModel(entry?["model"]?.ToString() ?? load?["model_path"]?.ToString() ?? card.Config.ModelPath ?? card.ModelName, load, folder))
            return ([], null);
        var template = LauncherLogic.ReadGgufChatTemplate(LauncherLogic.ResolveGguf(load, folder, LauncherLogic.HubCache()));
        return (LauncherLogic.EffortLevels(template) is { Length: > 0 } own ? own : AnyEfforts, LauncherLogic.EffortDefault(template));
    }

    private bool Thinks(RoleCard? card) => EffortLevels(card).Length > 0;

    // The effort a request carries: the chosen one when the target worker's model takes it, else "" (the model's default).
    private string EffortFor(RoleCard? card) => _settings.ChatEffort is { Length: > 0 } chosen && EffortLevels(card).Contains(chosen) ? chosen : "";

    private static string EffortLabel(string value) => Efforts.FirstOrDefault(e => e.Value == value).Label ?? "Default";

    // The worker the box talks to and its saved model ("Lead · Qwen3.8 27B"), loaded or not.
    private string EffortTarget(RoleCard card) => $"{ChatName(card, card.Role)} · {card.ModelName}";

    private void RefreshEffort()
    {
        if (_chat is null)
            return; // before InitChat
        foreach (var (chip, card) in new[] { (ChatEffortChip, ChatTarget()), (CodeEffortChip, CodeTarget()) })
        {
            var levels = EffortLevels(card);
            var sent = EffortFor(card);
            chip.Content = $"Effort: {EffortLabel(sent).Split(' ')[0]}  ▾";
            chip.IsEnabled = levels.Length > 0;
            chip.ToolTip = levels.Length == 0 ? "This model doesn't think"
                : $"How much {EffortTarget(card!)} thinks before it answers" +
                  (sent != _settings.ChatEffort ? $" ({EffortLabel(_settings.ChatEffort)} is not one of its levels: it gets Default)" : "");
        }
    }

    private void Effort_Click(object sender, RoutedEventArgs e) => BuildEffortMenu((FrameworkElement)sender).IsOpen = true;

    // Only the levels the target's saved model takes, under a line naming it.
    private ContextMenu BuildEffortMenu(FrameworkElement target)
    {
        var menu = NewPresetMenu(target);
        menu.Placement = PlacementMode.Top;
        var card = target == CodeEffortChip ? CodeTarget() : ChatTarget();
        var (levels, fallback) = EffortInfo(card);
        if (card is not null)
        {
            var heading = new TextBlock { Text = EffortTarget(card), FontSize = 11, Margin = new Thickness(8, 4, 8, 4), Tag = "EffortTarget" };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            menu.Items.Add(heading);
            menu.Items.Add(new Separator());
        }
        var sent = EffortFor(card);
        foreach (var (value, label) in Efforts.Where(e => e.Value.Length == 0 || levels.Contains(e.Value)))
        {
            var item = PresetItem(label, () =>
            {
                _settings.ChatEffort = value;
                _settings.Save();
                RefreshEffort();
            });
            item.IsChecked = value == sent;
            if (value.Length == 0 && fallback is not null) // what Default really is: "Default (Extra high)", the level in grey
            {
                var header = new TextBlock();
                header.Inlines.Add(new Run(label));
                var resolved = new Run($"  ({EffortLabel(fallback)})");
                resolved.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                header.Inlines.Add(resolved);
                (item.Header, item.Tag) = (header, label);
                item.ToolTip = $"Sends no effort: this model's template then uses {EffortLabel(fallback)}";
            }
            menu.Items.Add(item);
        }
        return menu;
    }

    // Skill keys are folder names, so they compare like Windows paths: ignoring case. The user's skills are on when listed in
    // Skills; the bundled ones are on unless listed in SkillsOff, so every skill the app ships starts on.
    private bool HasSkill(LauncherLogic.Skill skill) => skill.BuiltIn
        ? !_settings.SkillsOff.Contains(Path.GetFileName(skill.Folder), StringComparer.OrdinalIgnoreCase)
        : _settings.Skills.Contains(Path.GetFileName(skill.Folder), StringComparer.OrdinalIgnoreCase);

    private void SetSkill(string key, bool on, bool builtIn = false)
    {
        var list = builtIn ? _settings.SkillsOff : _settings.Skills;
        list.RemoveAll(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase));
        if (on != builtIn) // a user skill turned on, or a bundled one turned off
            list.Add(key);
        _settings.Save();
        RefreshExtras();
    }

    // Creates <skills>\<name>\SKILL.md from a template, turns it on and opens it for editing.
    private void NewSkill()
    {
        if (SidebarAsk("New skill", "Name the skill. Its SKILL.md opens so you can write when to use it and its instructions.", "Create", "") is not { } raw)
            return;
        var name = raw.Trim();
        var folder = Path.Combine(AppSettings.SkillsRoot, name);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) // Windows drops a trailing dot
        {
            Toast("A skill name can't contain \\ / : * ? \" < > | or end with a dot", "#F7C873");
            return;
        }
        if (Directory.Exists(folder))
        {
            Toast("A skill with that name already exists", "#F7C873");
            return;
        }
        var file = Path.Combine(folder, "SKILL.md");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, $"---\nname: {name}\ndescription: When to use this skill, in one sentence.\n---\n\n# {name}\n\n" +
                "Step-by-step instructions the model follows when it uses this skill.\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast($"Could not create the skill: {e.Message}", "#FF667A");
            return;
        }
        SetSkill(name, true);
        if (SkillsView.Visibility == Visibility.Visible)
            RenderSkillsPage();
        if (_codeChoice is null) // not under --ui-smoke
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}

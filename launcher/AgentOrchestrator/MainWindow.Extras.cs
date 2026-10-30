using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AgentOrchestrator;

// The composer's Search / Tools toggles and Skills menu, shared by the Chat and Code views. Each message sends them in its
// request (LauncherLogic.Extras); orchestrator.py turns them into tools.
public partial class MainWindow
{
    private LauncherLogic.Extras CurrentExtras() =>
        new(_settings.UseSearch, _settings.UseTools,
            LauncherLogic.ListSkills(AppSettings.SkillsRoot).Where(s => HasSkill(Path.GetFileName(s.Folder))).Select(s => s.Folder).ToList());

    private void RefreshExtras()
    {
        var skills = CurrentExtras().Skills.Count;
        foreach (var (search, tools, skill) in new[] { (ChatSearchToggle, ChatToolsToggle, ChatSkillsButton), (CodeSearchToggle, CodeToolsToggle, CodeSkillsButton) })
        {
            search.IsChecked = _settings.UseSearch;
            tools.IsChecked = _settings.UseTools;
            skill.IsChecked = skills > 0;
            ((TextBlock)((StackPanel)skill.Content).Children[1]).Text = skills > 0 ? $"Skills · {skills}  ▾" : "Skills  ▾";
        }
    }

    private void ExtraToggle_Click(object sender, RoutedEventArgs e)
    {
        var on = ((ToggleButton)sender).IsChecked == true;
        if (((FrameworkElement)sender).Tag as string == "search")
            _settings.UseSearch = on;
        else
            _settings.UseTools = on;
        _settings.Save();
        RefreshExtras();
    }

    private void Skills_Click(object sender, RoutedEventArgs e)
    {
        RefreshExtras(); // a click toggled the button; its look follows the enabled skills
        BuildSkillsMenu((FrameworkElement)sender).IsOpen = true;
    }

    // One checkable item per skill folder (a folder under the skills folder with a SKILL.md), then New skill… and the folder.
    private ContextMenu BuildSkillsMenu(FrameworkElement target)
    {
        var menu = NewPresetMenu(target);
        menu.Placement = PlacementMode.Top;
        var skills = LauncherLogic.ListSkills(AppSettings.SkillsRoot);
        foreach (var skill in skills)
        {
            var key = Path.GetFileName(skill.Folder);
            var item = PresetItem(skill.Name, () => { }); // a checkable item toggles itself before Click
            item.IsCheckable = true;
            item.IsChecked = HasSkill(key);
            item.Tag = key;
            item.ToolTip = skill.Description.Length > 0 ? skill.Description : null;
            item.Click += (_, _) => SetSkill(key, item.IsChecked);
            menu.Items.Add(item);
        }
        if (skills.Count == 0)
            menu.Items.Add(PresetItem("No skills yet", () => { }, false));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        menu.Items.Add(PresetItem("New skill…", NewSkill));
        menu.Items.Add(PresetItem("Open skills folder", () =>
        {
            Directory.CreateDirectory(AppSettings.SkillsRoot);
            Process.Start(new ProcessStartInfo(AppSettings.SkillsRoot) { UseShellExecute = true });
        }));
        return menu;
    }

    // ---- thinking effort: one choice (_settings.ChatEffort) for Chat and Code, sent only when the model it reaches thinks

    private static readonly (string Value, string Label)[] Efforts =
        [("", "Default"), ("off", "Off (no thinking)"), ("low", "Low"), ("medium", "Medium"), ("high", "High"), ("max", "Max")];

    // Whether a worker's model thinks (LauncherLogic.IsThinkingModel), from the active config.json: roles[role] for a
    // built-in worker, its custom_workers entry for a custom one.
    private bool Thinks(RoleCard? card)
    {
        if (card is null)
            return false;
        JsonObject? entry = null;
        try
        {
            var path = ActiveConfigPath();
            if (File.Exists(path))
            {
                var root = JsonNode.Parse(File.ReadAllText(path));
                entry = card.IsCustom
                    ? root?["custom_workers"]?.AsArray().OfType<JsonObject>().FirstOrDefault(w => w["id"]?.ToString() == card.Role)
                    : root?["roles"]?[card.Role] as JsonObject;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        var load = entry?["auto_load"] as JsonObject;
        return LauncherLogic.IsThinkingModel(entry?["model"]?.ToString() ?? load?["model_path"]?.ToString() ?? card.Config.ModelPath ?? card.ModelName, load,
            _settings.ModelsFolder ?? "");
    }

    // Multi (Planner -> Implementer -> Reviewer) thinks when any built-in worker does.
    private bool Thinks(bool multi, RoleCard? card) => multi ? _cards.Where(k => k.IsBuiltIn).Any(Thinks) : Thinks(card);

    // The effort a request carries: "" (the model's default) unless the model it reaches thinks.
    private string EffortFor(bool multi, RoleCard? card) => Thinks(multi, card) ? _settings.ChatEffort : "";

    private void RefreshEffort()
    {
        if (_chat is null)
            return; // before InitChat
        var label = Efforts.FirstOrDefault(e => e.Value == _settings.ChatEffort).Label?.Split(' ')[0] ?? "Default";
        foreach (var (chip, thinks) in new[] { (ChatEffortChip, Thinks(ChatMulti, ChatTarget())), (CodeEffortChip, Thinks(CodeMulti, CodeTarget())) })
        {
            chip.Content = $"Effort: {label}  ▾";
            chip.IsEnabled = thinks;
            chip.ToolTip = thinks ? "How much the model thinks before it answers" : "This model doesn't think";
        }
    }

    private void Effort_Click(object sender, RoutedEventArgs e) => BuildEffortMenu((FrameworkElement)sender).IsOpen = true;

    private ContextMenu BuildEffortMenu(FrameworkElement target)
    {
        var menu = NewPresetMenu(target);
        menu.Placement = PlacementMode.Top;
        foreach (var (value, label) in Efforts)
        {
            var item = PresetItem(label, () =>
            {
                _settings.ChatEffort = value;
                _settings.Save();
                RefreshEffort();
            });
            item.IsChecked = value == _settings.ChatEffort;
            menu.Items.Add(item);
        }
        return menu;
    }

    // Skill keys are folder names, so they compare like Windows paths: ignoring case.
    private bool HasSkill(string key) => _settings.Skills.Contains(key, StringComparer.OrdinalIgnoreCase);

    private void SetSkill(string key, bool on)
    {
        _settings.Skills.RemoveAll(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase));
        if (on)
            _settings.Skills.Add(key);
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
        if (_codeChoice is null) // not under --ui-smoke
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}

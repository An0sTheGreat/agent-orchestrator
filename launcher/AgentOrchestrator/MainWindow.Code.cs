using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace AgentOrchestrator;

// Code view: a conversation with the lead (the Planner, the primary agent; it may delegate to the other workers) about a folder (`orchestrator.py --code`), whose tool calls read and edit
// its files, beside the project run's output. Turns share the Chat plumbing (RunTurn); one turn of either kind at a time.
public partial class MainWindow
{
    private readonly List<LauncherLogic.CodeFolder> CodeExtraFolders = new(); // folders beyond the project the worker may use
    private bool _codeStartRequested; // the worker called start_project: the run starts once the turn succeeds
    private bool _codeRun; // the Code view shows Run output rather than the conversation

    private RoleCard? CodeTarget() => CardFor("planner"); // the lead: it does the work and may delegate to the other workers

    // action "chat" sends the typed message; "write_project" asks the worker (the Planner, say) to write PROJECT.md from
    // the conversation so far, with no user message.
    // True when the turn completed with a reply.
    private Task<bool> SendCode(string action = "chat", string? worker = null, QueuedMessage? queued = null)
    {
        var project = SelectedProjectPath();
        if (project.Length == 0)
        {
            SetStatus("Choose a folder", "#F7C873");
            Toast("Choose a folder first", "#F7C873");
            return Task.FromResult(false);
        }
        if (RefuseWorkflowRoot(project))
            return Task.FromResult(false);
        _turnImages = [];
        var text = action != "chat" ? null : queued is null ? ComposeMessage(_code, CodeInput.Text.Trim()) : ComposeMessage(_code, queued.Text, queued.Attachments);
        if (text is { Length: 0 } || !EnsureCodeConversation(project))
            return Task.FromResult(false);
        return RunTurn(_code, text, worker ?? CodeTarget()!.Role, role => CodeRequest(action, role, text, project),
            LauncherLogic.BuildCodeStartInfo, $"Code ({action})", queued is not null);
    }

    // The request of a Code turn; text null: no user message.
    private string CodeRequest(string action, string role, string? text, string project) =>
        LauncherLogic.BuildCodeRequest(action, role, text is null ? _code.History : _code.History.Append(new LauncherLogic.ChatMessage("user", text)),
            project, CodeExtraFolders, CurrentExtras(initProject: (_code.History.FirstOrDefault()?.Content ?? text) == InitProjectMessage) with { Images = _turnImages }, title: action == "chat" && _code.History.Count == 0, effort: EffortFor(CardFor(role)),
            summary: action == "chat" ? _code.Summary ?? "" : "", summaryCovers: action == "chat" ? _code.SummaryCovers : 0); // write_project reads it all

    // A Code turn or run never gets write access to the Agent Orchestrator folder (templates, prompts, orchestrator.py).
    private bool RefuseWorkflowRoot(string project)
    {
        if (!LauncherLogic.OverlapsWorkflowRoot(project, _workflowRoot))
            return false;
        SetStatus("Choose a project folder", "#F7C873");
        Toast(WorkflowRootRefusal, "#F7C873");
        return true;
    }

    private const string WorkflowRootRefusal = "Choose a project folder — the Agent Orchestrator folder itself can't be a Code project";

    // A completed Code turn: its exchange is in the history; start the run if the worker asked for it (and the folder
    // was not changed meanwhile: the run would start in another folder). StartCodeProject refuses a folder without PROJECT.md.
    private void CodeTurnFinished()
    {
        SaveCodeConversation();
        if (_codeStartRequested && SameFolder(SelectedProjectPath(), _codeConv?.Folder))
            StartCodeProject();
        _codeStartRequested = false;
    }

    // One tool call: the name and its path or pattern as the title, the result folded inside.
    private void AddCodeTool(string name, string args, string result)
    {
        if (_reply is not { } r)
            return;
        var body = BodyText(result, mono: true, muted: false, conv: _turn);
        var expander = BuildExpander(name, ToolTarget(args), body, out _);
        expander.Margin = new Thickness(0, 2, 0, 2);
        r.Content.Children.Add(expander);
        FadeIn(expander);
        if (!_turn.Follow)
            Reveal(_turn.Jump, true, slide: 10);
    }

    // "path" or "pattern" when the arguments have one, else the first string argument.
    internal static string ToolTarget(string args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "";
            foreach (var key in new[] { "path", "pattern" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString()!;
            return doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String).Value is { ValueKind: JsonValueKind.String } first
                ? Preview(first.GetString()!) : "";
        }
        catch (JsonException) { return ""; }
    }

    // ---- view

    private void ShowCodeRun(bool run)
    {
        _codeRun = run;
        CodeConversationToggle.IsChecked = !run;
        CodeRunToggle.IsChecked = run;
        Reveal(WorkArea, run);
        Reveal(CodeChat, !run);
        if (!run && NavCode.IsChecked == true)
            CodeInput.Focus();
    }

    private void CodeSubView_Click(object sender, RoutedEventArgs e) => ShowCodeRun(((FrameworkElement)sender).Tag as string == "run");

    // The header and empty state follow the project folder. The open conversation belongs to its folder: another folder
    // closes it (the next message there starts a new one). While a turn runs nothing changes; RunTurn calls this again
    // when the turn ends. Also called when the window is activated (PROJECT.md may have been added or removed outside).
    private void RefreshCodeFolder()
    {
        if (_chatBusy)
            return;
        var shown = SelectedProjectPath();
        if (_codeConv is { } open && !SameFolder(shown, open.Folder))
            CloseCodeConversation();
        RefreshCodeFolders();
        CodeFolderText.Text = shown.Length == 0 ? "No folder" : Path.GetFileName(shown.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : shown;
        CodeFolderText.ToolTip = shown.Length == 0 ? null : shown;
        var hasMd = HasProjectMd();
        (CodeEmptyTitle.Text, CodeEmptyText.Text) = shown.Length == 0
            ? ("Choose a folder to start", "Choose the working folder; the worker can read, search and edit the files in it.")
            : hasMd ? ("Start a conversation", "Message the lead below: it can work through PROJECT.md with you. Or press Start project for an autonomous run.")
            : ("Add a PROJECT.md to start the project", "Discuss the project with the lead, then say init-project: it writes PROJECT.md and you can " +
                "ask it to begin work. Start project runs it autonomously instead.");
        CodeEmptyChoose.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CodeEmptyInit.Visibility = CodeEmptyAddMd.Visibility = CodeEmptyTemplate.Visibility = shown.Length > 0 && !hasMd ? Visibility.Visible : Visibility.Collapsed;
        RefreshChatInput(); // the Project menu needs a folder; Start project needs PROJECT.md
    }

    // A cheap check, repeated on every refresh: the user may add or delete the file outside the app.
    private bool HasProjectMd() => SelectedProjectPath() is { Length: > 0 } folder && File.Exists(Path.Combine(folder, "PROJECT.md"));

    private void CodeNew_Click(object sender, RoutedEventArgs e) => NewCodeConversation();

    private void CodeEmptyAddMd_Click(object sender, RoutedEventArgs e) => UseProjectMd();

    private void CodeEmptyTemplate_Click(object sender, RoutedEventArgs e) => StartProjectMdFromTemplate();

    // Copies the workflow root's PROJECT.md template into a folder that has none, then opens it to be filled in.
    private void StartProjectMdFromTemplate()
    {
        var folder = SelectedProjectPath();
        if (_chatBusy || _process is not null || _workflowRoot is null || folder.Length == 0 || !Directory.Exists(folder) || RefuseWorkflowRoot(folder))
            return;
        var target = Path.Combine(folder, "PROJECT.md");
        try
        {
            if (!File.Exists(target))
            {
                File.Copy(Path.Combine(_workflowRoot, "PROJECT.md"), target);
                Log($"PROJECT.md template copied into {folder}");
                Toast("PROJECT.md added from the template; fill it in, then press Start project");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Could not copy the PROJECT.md template: {exception.Message}");
            Toast("Could not copy the PROJECT.md template", "#FF667A");
            return;
        }
        RefreshCodeFolder();
        RefreshPrimary();
        EditProject_Click(this, new RoutedEventArgs());
    }

    private const string InitProjectSkill = "init-project"; // bundled in <workflow root>\skills
    // The first message of a "Create PROJECT.md with a model…" conversation; every turn of a conversation that starts with
    // it carries the init-project skill (CodeRequest). The worker writes the file itself, from the skill's template.
    private const string InitProjectMessage = "Use the init-project skill to create PROJECT.md for this folder. Inspect the folder first, then ask me what you need. " +
        "When we agree, save it with write_file as PROJECT.md in the project root.";

    private void CodeEmptyInit_Click(object sender, RoutedEventArgs e) => Forget(CreateProjectMdWithModel());

    // Pick a worker (the Planner by default), make it the Single target and send InitProjectMessage: it interviews the user
    // in this conversation and writes PROJECT.md; RefreshCodeFolder after the turn then enables Start project.
    private Task<bool> CreateProjectMdWithModel()
    {
        var folder = SelectedProjectPath();
        if (_chatBusy || _process is not null || folder.Length == 0 || RefuseWorkflowRoot(folder))
            return Task.FromResult(false);
        if (!AllSkills().Any(s => Path.GetFileName(s.Folder).Equals(InitProjectSkill, StringComparison.OrdinalIgnoreCase)))
        {
            Toast("The init-project skill is missing from the app's skills folder", "#FF667A");
            return Task.FromResult(false);
        }
        if (ChooseWorker("Create PROJECT.md with a model", "Who should interview you about the project and write PROJECT.md?") is not { } role)
            return Task.FromResult(false);
        ShowCodeRun(false);
        CodeInput.Text = InitProjectMessage;
        return SendCode(worker: role); // the chosen worker takes this first turn; later turns go to the lead
    }

    // A worker to talk to: the Planner first and checked, then the others; a custom worker the orchestrator would skip is
    // listed off, with why. Its role, or null when cancelled.
    private string? ChooseWorker(string title, string message)
    {
        if (_codeChoice is { } answer)
            return answer(title);
        RefreshSetupProblems();
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var card in _cards.Where(k => !k.Hidden).OrderBy(k => k.Role != "planner"))
        {
            var broken = card.IsCustom ? card.SetupProblem : null;
            list.Children.Add(new RadioButton { Content = $"{ChatName(card, card.Role)} — {broken ?? card.ModelName}", Tag = card.Role, IsEnabled = broken is null,
                ToolTip = broken is null ? null : card.SetupTip, Margin = new Thickness(0, 4, 0, 4) });
        }
        var radios = list.Children.OfType<RadioButton>().ToList();
        if (radios.FirstOrDefault(r => r.IsEnabled) is { } first)
            first.IsChecked = true;
        string? result = null;
        DialogShell? dialog = null;
        var ok = new Button { Content = "Start", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(14, 5, 14, 5), IsDefault = true,
            IsEnabled = radios.Any(r => r.IsEnabled) };
        ok.Click += (_, _) => { result = radios.FirstOrDefault(r => r.IsChecked == true)?.Tag as string; dialog!.Close(); };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => dialog!.Close();
        var panel = new StackPanel { MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        panel.Children.Add(list);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, ok } });
        dialog = new DialogShell(this, title, panel);
        dialog.ShowDialog();
        return result;
    }

    private void CodeStart_Click(object sender, RoutedEventArgs e) => StartCodeProject();

    // ---- folder, conversation lifecycle, extra folders, start

    private readonly ConversationStore _store = new(AppSettings.DataRoot); // every saved conversation (Code and Chat) and the sidebar's order
    private ConversationStore.Conversation? _codeConv; // the open one (the store's own instance); null: none yet

    // Test hooks for --ui-smoke: pick a folder and answer Choose (by its title) without showing a dialog.
    private Func<string?>? _codeFolderPick;
    private Func<string, string?>? _codeChoice;

    private string? PickFolder(string title)
    {
        if (_codeFolderPick is { } pick)
            return pick();
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(ProjectPath) ? ProjectPath : _workflowRoot };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    // A message and buttons (the first is the default); the chosen value, or null when cancelled.
    private string? Choose(string title, string message, params (string Label, string Value)[] options)
    {
        if (_codeChoice is { } answer)
            return answer(title);
        string? result = null;
        DialogShell? dialog = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        cancel.Click += (_, _) => dialog!.Close();
        buttons.Children.Add(cancel);
        foreach (var (label, value) in options.Reverse())
        {
            var button = new Button { Content = label, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsDefault = value == options[0].Value };
            if (button.IsDefault)
                button.Style = (Style)FindResource("PrimaryButton");
            button.Click += (_, _) => { result = value; dialog!.Close(); };
            buttons.Children.Add(button);
        }
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        panel.Children.Add(buttons);
        dialog = new DialogShell(this, title, panel);
        dialog.ShowDialog();
        return result;
    }

    // Pick the folder and start a new conversation there (with the "Main" preset, when there is one). Earlier ones stay in
    // the sidebar; a folder may have several.
    private void NewCodeConversation()
    {
        if (_chatBusy || _process is not null)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            return;
        }
        if (PickFolder("Choose the folder to work in") is not { } folder || !DiscardWorkerEdits()) // the switch below rebuilds the tab
            return;
        CloseCodeConversation();
        ProjectPath = folder;
        ShowView();
        ShowCodeRun(false);
        if (!EnsureCodeConversation(SelectedProjectPath()))
            return;
        if (_settings.Presets.FirstOrDefault(p => p.Name.Equals("Main", StringComparison.OrdinalIgnoreCase)) is { } main)
            ApplyPreset(main);
        RefreshCodeFolders();
        RefreshChatTarget();
    }

    // Opens the conversation when none is: a message or an extra folder in a folder with no conversation starts one.
    private bool EnsureCodeConversation(string project)
    {
        if (_codeConv is not null)
            return true;
        if (!StoreDo(() => _codeConv = _store.Create(project)) || _codeConv is null)
            return false;
        _settings.LastConversation = _codeConv.Id;
        _settings.Save();
        RefreshSidebarList();
        return true;
    }

    // A sidebar row: shows the saved conversation in the Code view, in its own folder. Refused while a turn or run is
    // active (it would change the conversation that turn belongs to).
    private void OpenCodeConversation(string id)
    {
        if ((_chatBusy && _turn.IsCode) || _process is not null) // a chat's reply leaves Code conversations free
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            RefreshSidebarList(); // the clicked row unchecks itself
            return;
        }
        if (_codeConv?.Id != id && _store.Get(id) is { } conversation)
        {
            if (!Path.IsPathFullyQualified(conversation.Folder)) // hand-edited or damaged: there is no folder to work in
            {
                Toast("This chat has no folder, so it can't be opened", "#FF667A");
                RefreshSidebarList();
                return;
            }
            if (!DiscardWorkerEdits()) // the switch below rebuilds the tab
            {
                RefreshSidebarList();
                return;
            }
            CloseCodeConversation();
            ProjectPath = conversation.Folder;
            ResumeCodeConversation(conversation, SelectedProjectPath());
            _codeConv = conversation;
            _settings.LastConversation = id;
            _settings.Save();
        }
        ShowView();
        ShowCodeRun(false);
        RefreshSidebarList();
    }

    // Empties the Code view. A conversation left with nothing in it (no messages, extra folders or title) is deleted
    // rather than kept as clutter, unless it was moved into a project or archived.
    private void CloseCodeConversation()
    {
        if (_codeConv is { Title: null } c && _code.History.Count == 0 && CodeExtraFolders.Count == 0 && !_store.IsOrganized(c.Id))
            StoreDo(() => _store.Delete(c.Id));
        _codeConv = null;
        ClearConversation(_code);
        CodeExtraFolders.Clear();
        RefreshCodeFolders();
        if (_settings.LastConversation.Length > 0)
        {
            _settings.LastConversation = "";
            _settings.Save();
        }
        RefreshSidebarList();
    }

    // A store write that failed (disk full, access denied) is reported instead of ending the app. So is a store that
    // could not read everything at startup: it is read-only, and the change is not attempted.
    private bool StoreDo(Action change)
    {
        try
        {
            if (!_store.ReadOnly)
                change(); // Load included: it is what may turn the store read-only
            if (!_store.ReadOnly)
                return true;
            Log($"Conversations could not be loaded ({_store.LoadError}); changes to them are not saved until the launcher restarts");
            Toast("Conversations could not be loaded; changes won't be saved", "#FF667A");
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not save the conversations: {e.Message}");
            Toast("Could not save the conversations", "#FF667A");
            return false;
        }
    }

    // Shows a saved conversation in the (cleared) Code view: its extra folders, worker and messages. Extra folders that
    // are gone, are the project, or repeat one are dropped; Full edit inside the Agent Orchestrator folder becomes Read only.
    private void ResumeCodeConversation(ConversationStore.Conversation saved, string project)
    {
        foreach (var folder in saved.Folders)
        {
            var dropped = !Directory.Exists(folder.Path) ? "it no longer exists" : SamePath(folder.Path, project) ? "it is the project folder"
                : CodeExtraFolders.Any(f => SamePath(f.Path, folder.Path)) ? "it is listed twice" : null;
            if (dropped is not null)
                Log($"Code: dropped the extra folder {folder.Path}: {dropped}");
            else if (folder.Access == "write" && LauncherLogic.OverlapsWorkflowRoot(folder.Path, _workflowRoot))
            {
                CodeExtraFolders.Add(folder with { Access = "read" });
                Log($"Code: {folder.Path} is in the Agent Orchestrator folder, so it is Read only");
            }
            else
                CodeExtraFolders.Add(folder);
        }
        Replay(_code, saved.Messages, CodeTarget()?.Role ?? "planner");
        ShowContext(true, saved.Context);
        _code.Tokens = saved.Tokens ?? 0;
        (_code.Summary, _code.SummaryCovers) = (saved.Summary, saved.SummaryCovers);
        RefreshCodeFolders();
        RefreshChatTarget();
    }

    // After a completed turn or an extra-folder change; the sidebar shows the new title and time.
    private void SaveCodeConversation()
    {
        if (_codeConv is not { } c)
            return;
        c.Folders = CodeExtraFolders.ToList();
        c.Worker = "planner"; // the lead
        c.Messages = _code.History.ToList();
        c.Context = _codeContext;
        c.Tokens = _code.Tokens;
        (c.Summary, c.SummaryCovers) = (_code.Summary, _code.SummaryCovers);
        c.Title ??= TakeTurnTitle();
        StoreDo(() => _store.Save(c));
        RefreshSidebarList();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    // SamePath for full paths that may be empty ("no folder").
    private static bool SameFolder(string a, string? b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b ?? ""), StringComparison.OrdinalIgnoreCase);

    private void AddCodeFolder_Click(object sender, RoutedEventArgs e)
    {
        var project = SelectedProjectPath();
        if (_chatBusy || _process is not null || project.Length == 0 || RefuseWorkflowRoot(project) || PickFolder("Choose a folder the worker may use") is not { } picked)
            return;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked));
        if (SamePath(path, project))
        {
            Toast("That is the project folder; the worker can already edit it", "#F7C873");
            return;
        }
        if (CodeExtraFolders.Any(f => SamePath(f.Path, path)))
        {
            Toast("That folder is already added", "#F7C873");
            return;
        }
        if (Choose("Folder access", $"What may the worker do in {path}?", ("Read only", "read"), ("Full edit access", "write")) is not { } access)
            return;
        if (access == "write" && LauncherLogic.OverlapsWorkflowRoot(path, _workflowRoot))
        {
            Toast("The Agent Orchestrator folder can only be added Read only", "#F7C873");
            return;
        }
        if (!EnsureCodeConversation(project))
            return;
        CodeExtraFolders.Add(new LauncherLogic.CodeFolder(path, access));
        RefreshCodeFolders();
        SaveCodeConversation();
    }

    private void RemoveCodeFolder(LauncherLogic.CodeFolder folder)
    {
        if (_chatBusy || _process is not null || !CodeExtraFolders.Remove(folder))
            return;
        RefreshCodeFolders();
        SaveCodeConversation();
    }

    // ---- Project menu: per-folder setup (PROJECT.md, task limits, extra folders, run once)

    private Func<string?>? _projectMdPick; // --ui-smoke: the PROJECT.md to use, without the file dialog

    private void CodeProject_Click(object sender, RoutedEventArgs e)
    {
        if (CodeProjectButton.IsEnabled)
            BuildProjectMenu().IsOpen = true;
    }

    private ContextMenu BuildProjectMenu()
    {
        var menu = NewPresetMenu(CodeProjectButton);
        menu.Items.Add(PresetItem("Use a PROJECT.md…", UseProjectMd));
        menu.Items.Add(PresetItem("Edit PROJECT.md", () => EditProject_Click(this, new RoutedEventArgs()), File.Exists(Path.Combine(SelectedProjectPath(), "PROJECT.md"))));
        // No Task limits… here: the lead project run ignores task limits (only the legacy --pipeline reads them).
        menu.Items.Add(PresetItem("Folders…", () => Dispatcher.InvokeAsync(ShowCodeFolders, System.Windows.Threading.DispatcherPriority.Background)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        var once = PresetItem("Stop after the first finished item", () => { }); // a checkable item toggles itself before Click
        once.IsCheckable = true;
        once.IsChecked = _settings.Once;
        once.Click += (_, _) => { _settings.Once = once.IsChecked; _settings.Save(); };
        menu.Items.Add(once);
        return menu;
    }

    // Copies a chosen file in as the folder's PROJECT.md; an existing one is kept (Use) or moved to a backup first (Replace).
    private void UseProjectMd()
    {
        var folder = SelectedProjectPath();
        if (_chatBusy || _process is not null || folder.Length == 0 || !Directory.Exists(folder) || RefuseWorkflowRoot(folder))
            return;
        string? source;
        if (_projectMdPick is { } pick)
            source = pick();
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a PROJECT.md", Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*" };
            source = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (source is null)
            return;
        var target = Path.Combine(folder, "PROJECT.md");
        try
        {
            if (SamePath(source, target))
            {
                Toast("That is already this folder's PROJECT.md", "#F7C873");
                return;
            }
            if (string.IsNullOrWhiteSpace(File.ReadAllText(source)))
            {
                Toast("That PROJECT.md is empty", "#F7C873");
                return;
            }
            string? backup = null;
            if (File.Exists(target))
            {
                var choice = AskExistingProjectMd(target);
                if (choice == ExistingProjectMdChoice.Use)
                    Log("Using the existing PROJECT.md");
                if (choice != ExistingProjectMdChoice.Replace)
                    return;
                backup = LauncherLogic.BackupProjectMd(folder);
            }
            File.Copy(source, target);
            Log($"PROJECT.md copied from {source} into {folder}" + (backup is null ? "" : $"; previous saved as {backup}"));
            Toast(backup is null ? "PROJECT.md added" : $"PROJECT.md replaced; previous saved as {backup}");
            RefreshCodeFolder();
            RefreshPrimary();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Could not use that PROJECT.md: {exception.Message}");
            Toast("Could not use that PROJECT.md", "#FF667A");
        }
    }

    // The extra-folder list lives in a hidden XAML host (CodeFoldersHost) and is lent to a dialog while it is open.
    private void ShowCodeFolders()
    {
        RefreshCodeFolders();
        CodeFoldersHost.Content = null;
        var dialog = new DialogShell(this, "Folders", CodeFoldersPanel);
        dialog.Closed += (_, _) => { ((Panel)CodeFoldersPanel.Parent).Children.Remove(CodeFoldersPanel); CodeFoldersHost.Content = CodeFoldersPanel; };
        dialog.ShowDialog();
    }

    // The Folders list: name (full path in the tooltip), access chip, remove.
    private void RefreshCodeFolders()
    {
        var hasProject = SelectedProjectPath().Length > 0;
        CodeFoldersList.Children.Clear();
        foreach (var folder in CodeExtraFolders)
        {
            var remove = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove this folder" };
            remove.SetResourceReference(StyleProperty, "GhostButton");
            remove.Click += (_, _) => RemoveCodeFolder(folder);
            var chip = new Border { Child = new TextBlock { Text = folder.Access == "write" ? "Full edit" : "Read only", FontSize = 10 }, VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(StyleProperty, "Chip");
            var name = new TextBlock { Text = Path.GetFileName(folder.Path) is { Length: > 0 } n ? n : folder.Path, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = folder.Path };
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(chip, Dock.Right);
            CodeFoldersList.Children.Add(new DockPanel { Margin = new Thickness(8, 2, 0, 2), Children = { remove, chip, name } });
        }
        CodeFoldersEmpty.Text = hasProject ? "Only the project folder" : "Choose a project folder first";
        CodeFoldersEmpty.Visibility = CodeExtraFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddCodeFolderButton.Visibility = hasProject ? Visibility.Visible : Visibility.Collapsed;
    }

    // Initializes the missing workflow files, then starts the normal run. The folder must already have a PROJECT.md:
    // without one nothing starts and no model is asked to write it; the user adds it (with a model, Add PROJECT.md… or the template).
    private void StartCodeProject()
    {
        var folder = SelectedProjectPath();
        if (_chatBusy || _process is not null || _workflowRoot is null)
            return;
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            Toast("Choose a folder first", "#F7C873");
            return;
        }
        if (RefuseWorkflowRoot(folder))
            return;
        if (!HasProjectMd())
        {
            SetStatus("Add a PROJECT.md first", "#F7C873");
            Toast("Project not started: add a PROJECT.md to this folder (create it with a model, Add PROJECT.md… or Start from template)", "#F7C873");
            RefreshCodeFolder();
            return;
        }
        // A run keeps its folder's conversation in the sidebar (with no chat yet it would count as empty and be dropped),
        // so the project can be found again after the app closes; the run's log is in the logs folder (Log).
        if (EnsureCodeConversation(folder) && _codeConv is { Title: null } conv)
        {
            conv.Title = $"{Path.GetFileName(folder.TrimEnd('\\', '/'))} · project run";
            StoreDo(() => _store.Save(conv));
            RefreshSidebarList();
        }
        try
        {
            var copied = LauncherLogic.InitializeProject(_workflowRoot, folder, customConfigFile: LauncherLogic.EnsureWorkerConfig(_workflowRoot));
            if (copied.Length > 0)
                Log($"Added: {string.Join(", ", copied)}");
            PrefillCards();
            RefreshPrimary();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Initialization failed: {e.Message}");
            Toast("Project not started: initialization failed", "#FF667A");
            return;
        }
        Start_Click(this, new RoutedEventArgs()); // switches the Code view to Run output once the run starts
        if (_process is null) // refused (a declined restart, say) or failed to start: Start_Click set the run before its first await
            Toast("PROJECT.md and the workflow files are ready, but the run was not started", "#F7C873");
    }
}

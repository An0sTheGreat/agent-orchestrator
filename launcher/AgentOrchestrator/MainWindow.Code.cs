using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace AgentOrchestrator;

// Code view: a conversation with one worker about a folder (`orchestrator.py --code`), whose tool calls read and edit
// its files, beside the project run's output. Turns share the Chat plumbing (RunTurn); one turn of either kind at a time.
public partial class MainWindow
{
    private readonly List<LauncherLogic.CodeFolder> CodeExtraFolders = new(); // folders beyond the project the worker may use
    private bool _codeStartRequested; // the worker called start_project: the run starts once the turn succeeds
    private bool _codeRun; // the Code view shows Run output rather than the conversation

    private RoleCard? CodeTarget() => CardFor(_settings.CodeWorker) ?? CardFor("implementer");

    // action "chat" sends the typed message; "write_project" asks the worker (the Planner, say) to write PROJECT.md from
    // the conversation so far, with no user message.
    private void SendCode(string action = "chat", string? worker = null)
    {
        var project = SelectedProjectPath();
        if (project.Length == 0)
        {
            SetStatus("Choose a folder", "#F7C873");
            Toast("Choose a folder first", "#F7C873");
            return;
        }
        var text = action == "chat" ? CodeInput.Text.Trim() : null;
        if (text is { Length: 0 })
            return;
        RunTurn(_code, text, worker ?? CodeTarget()!.Role, false,
            role => LauncherLogic.BuildCodeRequest(action, role, text is null ? _code.History : _code.History.Append(new LauncherLogic.ChatMessage("user", text)),
                project, CodeExtraFolders),
            LauncherLogic.BuildCodeStartInfo, $"Code ({action})");
    }

    // A completed Code turn: its exchange is in the history; start the run if the worker asked for it.
    private void CodeTurnFinished()
    {
        SaveCodeConversation();
        if (_codeStartRequested)
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

    // The header and empty state follow the project folder.
    private void RefreshCodeFolder()
    {
        var shown = SelectedProjectPath();
        CodeFolderText.Text = shown.Length == 0 ? "No folder" : Path.GetFileName(shown.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : shown;
        CodeFolderText.ToolTip = shown.Length == 0 ? null : shown;
        CodeEmptyTitle.Text = shown.Length == 0 ? "Choose a folder to start" : "Start a conversation";
    }

    private void CodeNew_Click(object sender, RoutedEventArgs e) => NewCodeConversation();

    private void CodeStart_Click(object sender, RoutedEventArgs e) => StartCodeProject();

    // ---- wired by part B (folder picker, resume, start flow, persistence)

    private void NewCodeConversation() => Log("Code: New conversation is not wired yet.");

    private void StartCodeProject() => Log("Code: Start project is not wired yet.");

    private void SaveCodeConversation() { }
}

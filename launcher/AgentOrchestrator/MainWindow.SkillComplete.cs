using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace AgentOrchestrator;

// Skill and command completion in the Chat and Code message boxes: a message that is just "/" or "$" and part of a name
// lists the matching skills (and, after "/", the app's commands such as /compact) above the box, each with what it does. Up/Down move the highlight, Tab or Enter completes ("/name "), a click completes, Escape
// closes. A message that names a skill this way sends it with the turn (CurrentExtras), switched on or not.
public partial class MainWindow
{
    private static readonly Regex SkillToken = new(@"^([/$])([\w.-]*)$");
    private static readonly Regex ArgToken = new(@"^/([\w.-]+) (\w*)$"); // a command's argument in progress: "/ponytail u"

    private Popup? _skillPopup;
    private StackPanel? _skillRows;
    private TextBox? _skillInput;
    private int _skillIndex;
    private readonly HashSet<TextBox> _skillWatched = new();

    // The app's own commands ("/" only) rather than skills: /compact runs in the engine, the others in the launcher
    // (RunLocalCommand). Args: the words completed after "/name ".
    private static readonly (string Name, string Help, (string Name, string Help)[] Args)[] Commands =
    {
        ("clear", "Start a new conversation in the same place (this one stays in the sidebar)", []),
        ("compact", "Summarize the conversation now (add what to keep: /compact keep the API decisions)", []),
        ("ponytail", "Ponytail for every model: the smallest change that works (off, lite, full, ultra)",
            [("off", "Turn Ponytail off"), ("lite", "Build what was asked; name the smaller option"),
             ("full", "Shortest complete change (the default)"), ("ultra", "Also push back on what the request does not need")]),
    };

    // A message that is one of those commands (as the engine reads it: "/name", then anything after a space).
    private static bool IsCommand(string text) => Commands.Any(c => Regex.IsMatch(text, $@"\A\s*/{c.Name}(\s|\z)", RegexOptions.IgnoreCase));

    // The commands the launcher answers itself, before anything is sent or queued; true when the box held one.
    // /clear: a new conversation where this one is (its folder and sidebar project), as the "+" buttons start one.
    // /ponytail [off|lite|full|ultra]: the global level (Settings > Ponytail); no word means full.
    private bool RunLocalCommand(Conversation c)
    {
        var m = Regex.Match(c.Input.Text, @"\A\s*/(clear|ponytail)(?:\s+(\w*))?\s*\z", RegexOptions.IgnoreCase);
        if (!m.Success)
            return false;
        var arg = m.Groups[2].Value.ToLowerInvariant();
        if (m.Groups[1].Value.Equals("ponytail", StringComparison.OrdinalIgnoreCase))
        {
            if (arg is not ("" or "off" or "lite" or "full" or "ultra"))
            {
                Toast("Use /ponytail off, lite, full or ultra", "#F7C873");
                return true;
            }
            c.Input.Clear();
            SetPonytail(arg is "off" ? "" : arg is "" ? "full" : arg);
            return true;
        }
        if (arg.Length > 0)
            return false; // "/clear something" is a message
        c.Input.Clear();
        if (!c.IsCode)
        {
            var group = _chatConv is { } saved ? _store.GroupOf(saved.Id) : _newChatGroup;
            if (ChatTurnShown)
                ParkChatTurn(); // a reply still streaming finishes into its own chat
            ClearChatConversation();
            _newChatGroup = group;
        }
        else if (_codeConv is { } open)
            NewCodeConversation(open.Folder, _store.GroupOf(open.Id));
        else
            ClearConversation(_code);
        return true;
    }

    // Settings > Ponytail shows the saved level; a click there saves it as /ponytail does.
    private void RefreshPonytailSetting()
    {
        _ponytailShowing = true;
        foreach (var radio in PonytailLevels.Children.OfType<RadioButton>())
            radio.IsChecked = (string)radio.Tag == _settings.Ponytail;
        _ponytailShowing = false;
    }

    private bool _ponytailShowing;

    private void Ponytail_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ponytailShowing && sender is RadioButton { Tag: string level } && level != _settings.Ponytail)
            SetPonytail(level);
    }

    private void SetPonytail(string level)
    {
        _settings.Ponytail = level;
        _settings.Save();
        RefreshPonytailSetting();
        Toast(level.Length == 0 ? "Ponytail off" : $"Ponytail {level}: on from the next message");
    }

    // The matching commands and skills for the box's text, with what each does; empty when it is not a lone "/name" or
    // "$name" in progress.
    private List<(string Name, string Help)> SkillMatches(string text)
    {
        if (CompletionToken(text) is not var (head, typed))
            return [];
        bool Fits(string n) => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && !n.Equals(typed, StringComparison.OrdinalIgnoreCase);
        if (head.Length > 1) // a command's argument; the level on now is marked
            return Commands.First(c => head == $"/{c.Name} ").Args.Where(a => Fits(a.Name))
                .Select(a => (a.Name, a.Help + (a.Name == (_settings.Ponytail is "" ? "off" : _settings.Ponytail) && head == "/ponytail " ? " (on now)" : ""))).ToList();
        var commands = head == "/" ? Commands.Where(c => Fits(c.Name)).Select(c => (c.Name, c.Help)).ToList() : [];
        var skills = AllSkills().DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(s => Fits(s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => (s.Name, s.Description));
        return [.. commands, .. skills];
    }

    // What completion replaces: the text before the word ("/", "$" or "/command ") and the part of the word typed; null
    // when the box is not a lone "/name", "$name" or "/command arg" in progress.
    private static (string Head, string Typed)? CompletionToken(string text)
    {
        if (SkillToken.Match(text) is { Success: true } m)
            return (m.Groups[1].Value, m.Groups[2].Value);
        if (ArgToken.Match(text) is { Success: true } a && Commands.Any(c => c.Name == a.Groups[1].Value.ToLowerInvariant() && c.Args.Length > 0))
            return ($"/{a.Groups[1].Value.ToLowerInvariant()} ", a.Groups[2].Value);
        return null;
    }

    private void RefreshSkillCompletion(TextBox input)
    {
        var names = SkillMatches(input.Text);
        if (names.Count == 0 || !input.IsKeyboardFocusWithin && _skillCompletionTest is null)
        {
            CloseSkillCompletion();
            return;
        }
        if (_skillPopup is null)
        {
            _skillRows = new StackPanel { Margin = new Thickness(4) };
            var card = new Border { Child = _skillRows, BorderThickness = new Thickness(1), MinWidth = 220 };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
            _skillPopup = new Popup { Child = card, Placement = PlacementMode.Top, VerticalOffset = -6, AllowsTransparency = true, StaysOpen = true };
        }
        var typed = CompletionToken(input.Text)!.Value.Typed.Length;
        _skillRows!.Children.Clear();
        foreach (var (name, help) in names)
        {
            var text = new TextBlock { FontSize = 13, MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Inlines.Add(new Run(name[..typed]) { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Run(name[typed..]));
            if (help.Length > 0)
            {
                var hint = new Run("   " + help) { FontSize = 12 };
                hint.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                text.Inlines.Add(hint);
            }
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var row = new Border { Child = text, Padding = new Thickness(10, 5, 10, 5), Tag = name, Cursor = Cursors.Hand };
            row.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; CompleteSkill(input, name); };
            row.MouseEnter += (_, _) => HighlightSkill(_skillRows.Children.IndexOf(row));
            _skillRows.Children.Add(row);
        }
        if (_skillWatched.Add(input))
            input.LostKeyboardFocus += (_, _) => { if (_skillInput == input) CloseSkillCompletion(); };
        _skillInput = input;
        _skillPopup.PlacementTarget = input;
        _skillPopup.IsOpen = true;
        HighlightSkill(0);
    }

    private void HighlightSkill(int index)
    {
        if (_skillRows is null || _skillRows.Children.Count == 0)
            return;
        _skillIndex = (index % _skillRows.Children.Count + _skillRows.Children.Count) % _skillRows.Children.Count; // wraps
        for (var i = 0; i < _skillRows.Children.Count; i++)
        {
            var row = (Border)_skillRows.Children[i];
            if (i == _skillIndex)
                row.SetResourceReference(Border.BackgroundProperty, "Hover");
            else
                row.Background = null;
        }
    }

    private void CompleteSkill(TextBox input, string name)
    {
        var head = CompletionToken(input.Text)?.Head ?? input.Text[..1];
        CloseSkillCompletion();
        input.Text = $"{head}{name} "; // "/ponytail " lists its words next
        input.CaretIndex = input.Text.Length;
        input.Focus();
    }

    private void CloseSkillCompletion()
    {
        if (_skillPopup is not null)
            _skillPopup.IsOpen = false;
        _skillInput = null;
    }

    // The message box's keys while the list is open; true when handled.
    private bool SkillCompletionKey(TextBox input, Key key)
    {
        if (_skillPopup is not { IsOpen: true } || _skillInput != input || _skillRows is null)
            return false;
        switch (key)
        {
            case Key.Down:
                HighlightSkill(_skillIndex + 1);
                return true;
            case Key.Up:
                HighlightSkill(_skillIndex - 1);
                return true;
            case Key.Tab or Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                CompleteSkill(input, (string)((Border)_skillRows.Children[_skillIndex]).Tag);
                return true;
            case Key.Escape:
                CloseSkillCompletion();
                return true;
            default:
                return false;
        }
    }

    // A skill the message names as "/name" or "$name" (word-bounded, any case).
    private static bool InvokesSkill(string? message, string name) =>
        message is { Length: > 0 } && Regex.IsMatch(message, $@"(?<![\w-])[/$]{Regex.Escape(name)}(?![\w-])", RegexOptions.IgnoreCase);

    private Func<bool>? _skillCompletionTest; // --ui-smoke: lets the list open without keyboard focus

    // --ui-smoke: typing "/st" lists start-project (bundled); Down/Up wrap, Tab completes to "/start-project ", an exact
    // name or a second word closes the list, Escape closes it; a message naming a skill sends it.
    private bool SkillCompletionSmoke(Action<string, FrameworkElement?> snap)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: skill completion: {what}"); return false; }
        var (text, test) = (CodeInput.Text, _skillCompletionTest);
        _skillCompletionTest = () => true;
        try
        {
            CodeInput.Text = "/st";
            if (_skillPopup is not { IsOpen: true } || _skillRows!.Children.OfType<Border>().All(b => (string)b.Tag != "start-project"))
                return Fail("'/st' does not list start-project");
            snap("37-skill-completion", (FrameworkElement)_skillPopup.Child);
            var count = _skillRows.Children.Count;
            SkillCompletionKey(CodeInput, Key.Up);
            if (_skillIndex != count - 1)
                return Fail("Up does not wrap to the last");
            SkillCompletionKey(CodeInput, Key.Down);
            var index = _skillRows.Children.OfType<Border>().ToList().FindIndex(b => (string)b.Tag == "start-project");
            HighlightSkill(index);
            if (!SkillCompletionKey(CodeInput, Key.Tab) || CodeInput.Text != "/start-project " || _skillPopup.IsOpen)
                return Fail($"Tab completed to '{CodeInput.Text}'");
            CodeInput.Text = "/start-project";
            if (_skillPopup.IsOpen)
                return Fail("an exact name still lists");
            CodeInput.Text = "$init";
            if (!_skillPopup.IsOpen || !SkillCompletionKey(CodeInput, Key.Escape) || _skillPopup.IsOpen)
                return Fail("Escape does not close");
            CodeInput.Text = "/comp";
            if (!_skillPopup.IsOpen || !SkillCompletionKey(CodeInput, Key.Tab) || CodeInput.Text != "/compact ")
                return Fail($"'/comp' completed to '{CodeInput.Text}'");
            CodeInput.Text = "/pony";
            if (!SkillCompletionKey(CodeInput, Key.Tab) || CodeInput.Text != "/ponytail " || !_skillPopup.IsOpen || _skillRows.Children.Count != 4)
                return Fail($"'/pony' completed to '{CodeInput.Text}' without listing its 4 levels");
            CodeInput.Text = "/ponytail u";
            if (!SkillCompletionKey(CodeInput, Key.Tab) || CodeInput.Text != "/ponytail ultra " || _skillPopup.IsOpen)
                return Fail($"'/ponytail u' completed to '{CodeInput.Text}'");
            CodeInput.Text = "/clear ";
            if (_skillPopup.IsOpen)
                return Fail("/clear lists words");
            CodeInput.Text = "$comp";
            if (_skillPopup.IsOpen)
                return Fail("commands listed after $");
            CodeInput.Text = "hello /st";
            if (_skillPopup.IsOpen)
                return Fail("lists mid-message");
            if (!InvokesSkill("please /start-project now", "start-project") || InvokesSkill("restart-project", "start-project") ||
                !CurrentExtras(message: "$init-projMD").Skills.Any(s => s.EndsWith("init-projMD", StringComparison.OrdinalIgnoreCase)))
                return Fail("a named skill is not sent");
            return true;
        }
        finally
        {
            CodeInput.Text = text;
            CloseSkillCompletion();
            _skillCompletionTest = test;
        }
    }

    // --ui-smoke: /ponytail sets the level (Settings > Ponytail and the engine's environment follow), a wrong word keeps the
    // box, /clear empties the chat, "/clear something" and mid-message commands are messages.
    private bool LocalCommandSmoke()
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: local commands: {what}"); return false; }
        var (level, text) = (_settings.Ponytail, ChatInput.Text);
        string? Env()
        {
            var info = new System.Diagnostics.ProcessStartInfo();
            info.Environment["AGENT_ORCHESTRATOR_PONYTAIL"] = "stale";
            ApplyRuntime(info);
            return info.Environment.TryGetValue("AGENT_ORCHESTRATOR_PONYTAIL", out var v) ? v : null;
        }
        bool Checked(string tag) => PonytailLevels.Children.OfType<RadioButton>().Single(r => (string)r.Tag == tag).IsChecked == true;
        try
        {
            ChatInput.Text = "/ponytail ultra ";
            if (!RunLocalCommand(_chat) || _settings.Ponytail != "ultra" || ChatInput.Text.Length > 0 || !Checked("ultra") || Env() != "ultra")
                return Fail($"/ponytail ultra gave '{_settings.Ponytail}', env '{Env()}'");
            ChatInput.Text = "/PONYTAIL";
            if (!RunLocalCommand(_chat) || _settings.Ponytail != "full")
                return Fail("/ponytail alone is not full");
            ChatInput.Text = "/ponytail loud";
            if (!RunLocalCommand(_chat) || _settings.Ponytail != "full" || ChatInput.Text != "/ponytail loud")
                return Fail("a wrong level changed something");
            PonytailLevels.Children.OfType<RadioButton>().Single(r => (string)r.Tag == "lite").IsChecked = true;
            if (_settings.Ponytail != "lite")
                return Fail("Settings > Ponytail does not save");
            ChatInput.Text = "/ponytail off";
            if (!RunLocalCommand(_chat) || _settings.Ponytail != "" || !Checked("") || Env() is not null)
                return Fail($"/ponytail off left '{_settings.Ponytail}', env '{Env()}'");
            foreach (var message in new[] { "/clear the cache", "please /clear", "/ponytailx" })
            {
                ChatInput.Text = message;
                if (RunLocalCommand(_chat))
                    return Fail($"'{message}' was taken as a command");
            }
            _chat.History.Add(new LauncherLogic.ChatMessage("user", "x"));
            ChatInput.Text = "/clear";
            if (!RunLocalCommand(_chat) || _chat.History.Count != 0 || ChatInput.Text.Length > 0 || _chatConv is not null)
                return Fail("/clear left the chat");
            return true;
        }
        finally
        {
            ChatInput.Text = text;
            _settings.Ponytail = level;
            _settings.Save();
            RefreshPonytailSetting();
        }
    }
}

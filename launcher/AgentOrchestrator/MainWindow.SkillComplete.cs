using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace AgentOrchestrator;

// Skill and command completion in the Chat and Code message boxes: typing "/" or "$" and part of a name anywhere in the
// message (at its start or after a space or new line, so never inside "src/app" or a URL) lists the matching skills (and,
// for a "/" that starts the message, the app's commands such as /compact) above the box, each with what it does. Up/Down
// move the highlight, Tab or Enter completes that word alone ("/name "), a click completes, Escape closes, and so does a
// space or moving the caret off the word. A message that names a skill this way sends it with the turn (CurrentExtras),
// switched on or not.
public partial class MainWindow
{
    private static readonly Regex SkillToken = new(@"(?<!\S)([/$])([\w.-]*)\z"); // on the text before the caret
    private static readonly Regex ArgToken = new(@"\A/([\w.-]+) (\w*)\z"); // a command's argument in progress: "/ponytail u"

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

    // The matching commands and skills for the word at the caret, with what each does; empty when it is not a "/name" or
    // "$name" in progress.
    private List<(string Name, string Help)> SkillMatches(string text, int caret)
    {
        if (CompletionToken(text, caret) is not var (start, _, head, typed))
            return [];
        bool Fits(string n) => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && !n.Equals(typed, StringComparison.OrdinalIgnoreCase);
        if (head.Length > 1) // a command's argument; the level on now is marked
            return Commands.First(c => head == $"/{c.Name} ").Args.Where(a => Fits(a.Name))
                .Select(a => (a.Name, a.Help + (a.Name == (_settings.Ponytail is "" ? "off" : _settings.Ponytail) && head == "/ponytail " ? " (on now)" : ""))).ToList();
        var commands = head == "/" && start == 0 ? Commands.Where(c => Fits(c.Name)).Select(c => (c.Name, c.Help)).ToList() : [];
        var skills = AllSkills().DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(s => Fits(s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => (s.Name, s.Description));
        return [.. commands, .. skills];
    }

    // What completion replaces, for the caret at `caret`: text[Start..End] is the head ("/", "$" or "/command ") and the
    // whole word under the caret, Typed the part of it before the caret; null when the caret is not in or just after a
    // "/name" or "$name" that starts the text or follows whitespace, or a "/command arg" that starts the text.
    internal static (int Start, int End, string Head, string Typed)? CompletionToken(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var end = caret + Regex.Match(text[caret..], @"\A[\w.-]*").Length;
        if (SkillToken.Match(text[..caret]) is { Success: true } m)
            return (m.Index, end, m.Groups[1].Value, m.Groups[2].Value);
        if (ArgToken.Match(text[..caret]) is { Success: true } a && Commands.Any(c => c.Name == a.Groups[1].Value.ToLowerInvariant() && c.Args.Length > 0))
            return (0, caret + Regex.Match(text[caret..], @"\A\w*").Length, $"/{a.Groups[1].Value.ToLowerInvariant()} ", a.Groups[2].Value);
        return null;
    }

    // The text with the word at the caret completed to `name` and one space after it (an existing one is kept), the rest
    // untouched, and the caret after that space; null when there is nothing to complete.
    internal static (string Text, int Caret)? CompleteToken(string text, int caret, string name)
    {
        if (CompletionToken(text, caret) is not var (start, end, head, _))
            return null;
        var rest = text[end..];
        var done = text[..start] + head + name + " ";
        return (done + (rest.StartsWith(' ') ? rest[1..] : rest), done.Length);
    }

    // --self-test: the word at the caret is found at the start, after a space or a new line, never inside a path or URL,
    // and completing it keeps the text around it.
    internal static void SkillCompleteSelfTest()
    {
        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Skill completion self-test failed: " + what);
        }
        // "|" marks the caret.
        static (int Start, int End, string Head, string Typed)? At(string marked) => CompletionToken(marked.Replace("|", ""), marked.IndexOf('|'));
        Check(At("/sta|") == (0, 4, "/", "sta"), "at the start");
        Check(At("fix this /sta|") == (9, 13, "/", "sta"), "after text and a space");
        Check(At("first line\n/sta|") == (11, 15, "/", "sta"), "after a new line");
        Check(At("run /st|art-pro now") == (4, 14, "/", "st"), "caret inside the word");
        Check(At("$ini|") == (0, 4, "$", "ini"), "$ prefix");
        Check(At("src/app|") is null && At("src/|") is null, "inside a path");
        Check(At("see https://a/b|") is null && At("https://a/|") is null, "inside a URL");
        Check(At("a /b|") == (2, 4, "/", "b"), "'a /b'");
        Check(At("please /|") == (7, 8, "/", "") && At("/|") == (0, 1, "/", ""), "a lone / at the end");
        Check(At("run /start-project |") is null && At("/start-project |now") is null, "after a completed word");
        Check(At("run /sta| x") == (4, 8, "/", "sta"), "text after the word");
        Check(At("|/sta") is null, "caret before the slash");
        Check(At("/ponytail u|") == (0, 11, "/ponytail ", "u") && At("x /ponytail u|") is null, "a command's argument at the start only");
        Check(CompleteToken("please run /sta", 15, "start-project") == ("please run /start-project ", 26), "completes at the end");
        Check(CompleteToken("run /sta now", 8, "start-project") == ("run /start-project now", 19), "keeps the text after, no second space");
        Check(CompleteToken("run /st,x", 7, "start-project") == ("run /start-project ,x", 19), "adds a space before other text");
        Check(CompleteToken("run /st|art", 7, "start-project") == ("run /start-project |art", 19), "only up to the word's end");
        Check(CompleteToken("a\n/sta\nb", 6, "start-project") == ("a\n/start-project \nb", 17), "second line");
        Check(CompleteToken("run /st", 6, "start-project") == ("run /start-project ", 19), "caret mid-word replaces the whole word");
        Check(CompleteToken("/ponytail u", 11, "ultra") == ("/ponytail ultra ", 16), "a command's argument");
        Check(CompleteToken("src/app", 7, "x") is null, "nothing to complete");
    }

    private void RefreshSkillCompletion(TextBox input)
    {
        if (_skillWatched.Add(input))
        {
            input.LostKeyboardFocus += (_, _) => { if (_skillInput == input) CloseSkillCompletion(); };
            // Moving the caret refilters or closes an open list; only typing opens one (so arrowing through lines does not).
            input.SelectionChanged += (_, _) => { if (_skillInput == input) RefreshSkillCompletion(input); };
        }
        var names = SkillMatches(input.Text, input.CaretIndex);
        if (names.Count == 0 || !input.IsKeyboardFocusWithin && _skillCompletionTest is null)
        {
            CloseSkillCompletion();
            return;
        }
        if (_skillPopup is null)
        {
            _skillRows = new StackPanel { Margin = new Thickness(4) };
            // under the rows, as the site's command list: a muted line saying the box filters them
            var footer = new TextBlock { Text = "Type to search commands", FontSize = 12, Margin = new Thickness(14, 2, 14, 8) };
            footer.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var card = new Border { Child = new StackPanel { Children = { _skillRows, footer } }, BorderThickness = new Thickness(1), MinWidth = 260, CornerRadius = new CornerRadius(14) };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            _skillPopup = new Popup { Child = card, Placement = PlacementMode.Top, VerticalOffset = -6, AllowsTransparency = true, StaysOpen = true };
        }
        _skillRows!.Children.Clear();
        foreach (var (name, help) in names)
        {
            var text = new TextBlock { FontSize = 13, MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Inlines.Add(new Run(name) { FontWeight = FontWeights.SemiBold }); // the whole command in bold, its hint dim
            if (help.Length > 0)
            {
                var hint = new Run("   " + help) { FontSize = 12 };
                hint.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                text.Inlines.Add(hint);
            }
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var row = new Border { Child = text, Padding = new Thickness(12, 6, 12, 6), Tag = name, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(9) };
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; CompleteSkill(input, name); };
            row.MouseEnter += (_, _) => HighlightSkill(_skillRows.Children.IndexOf(row));
            _skillRows.Children.Add(row);
        }
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
        CloseSkillCompletion();
        if (CompleteToken(input.Text, input.CaretIndex, name) is not var (text, caret))
            return;
        input.Text = text;
        input.CaretIndex = caret;
        RefreshSkillCompletion(input); // "/ponytail " lists its words next
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
    // name or a second word closes the list, Escape closes it; a message naming a skill sends it. Mid-message too: "please
    // run /sta" lists it and Enter completes that word alone without sending; a second line works, a URL or path does not
    // list, a space or moving the caret off the word closes.
    private bool SkillCompletionSmoke(Action<string, FrameworkElement?> snap)
    {
        static bool Fail(string what) { Console.Error.WriteLine($"ui-smoke: skill completion: {what}"); return false; }
        var (text, test) = (CodeInput.Text, _skillCompletionTest);
        _skillCompletionTest = () => true;
        // As typing does: the caret is after the new character when the text changes.
        void Type(string before, string after = "")
        {
            CodeInput.Text = before + after;
            CodeInput.CaretIndex = before.Length;
            RefreshSkillCompletion(CodeInput);
        }
        void Press(Key key) => CodeInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        bool Lists(string name) => _skillPopup is { IsOpen: true } && _skillRows!.Children.OfType<Border>().Any(b => (string)b.Tag == name);
        try
        {
            Type("/st");
            if (!Lists("start-project"))
                return Fail("'/st' does not list start-project");
            var count = _skillRows!.Children.Count;
            SkillCompletionKey(CodeInput, Key.Up);
            if (_skillIndex != count - 1)
                return Fail("Up does not wrap to the last");
            SkillCompletionKey(CodeInput, Key.Down);
            var index = _skillRows.Children.OfType<Border>().ToList().FindIndex(b => (string)b.Tag == "start-project");
            HighlightSkill(index);
            if (!SkillCompletionKey(CodeInput, Key.Tab) || CodeInput.Text != "/start-project " || _skillPopup!.IsOpen)
                return Fail($"Tab completed to '{CodeInput.Text}'");
            Type("/start-project");
            if (_skillPopup.IsOpen)
                return Fail("an exact name still lists");
            Type("$init");
            if (!_skillPopup.IsOpen || !SkillCompletionKey(CodeInput, Key.Escape) || _skillPopup.IsOpen)
                return Fail("Escape does not close");
            Type("/comp");
            if (!_skillPopup.IsOpen || !SkillCompletionKey(CodeInput, Key.Tab) || CodeInput.Text != "/compact ")
                return Fail($"'/comp' completed to '{CodeInput.Text}'");
            Type("/pony");
            if (!SkillCompletionKey(CodeInput, Key.Tab) || CodeInput.Text != "/ponytail " || !_skillPopup.IsOpen || _skillRows.Children.Count != 4)
                return Fail($"'/pony' completed to '{CodeInput.Text}' without listing its 4 levels");
            Type("/ponytail u");
            if (!SkillCompletionKey(CodeInput, Key.Tab) || CodeInput.Text != "/ponytail ultra " || _skillPopup.IsOpen)
                return Fail($"'/ponytail u' completed to '{CodeInput.Text}'");
            Type("/clear ");
            if (_skillPopup.IsOpen)
                return Fail("/clear lists words");
            Type("$comp");
            if (_skillPopup.IsOpen)
                return Fail("commands listed after $");
            Type("please /comp");
            if (_skillPopup.IsOpen)
                return Fail("commands listed mid-message, where they are not commands");

            // Mid-message: Down never moves the caret, Enter completes the word and does not send (sending empties the box).
            Type("please run /sta");
            if (!Lists("start-project"))
                return Fail("'please run /sta' does not list start-project");
            snap("37-skill-completion", (FrameworkElement)_skillPopup.Child);
            Press(Key.Down);
            if (CodeInput.CaretIndex != 15 || !_skillPopup.IsOpen)
                return Fail("Down moved the caret");
            HighlightSkill(_skillRows.Children.OfType<Border>().ToList().FindIndex(b => (string)b.Tag == "start-project"));
            Press(Key.Enter);
            if (CodeInput.Text != "please run /start-project " || CodeInput.CaretIndex != CodeInput.Text.Length || _skillPopup.IsOpen)
                return Fail($"Enter mid-message gave '{CodeInput.Text}' (caret {CodeInput.CaretIndex})");
            if (SkillCompletionKey(CodeInput, Key.Enter))
                return Fail("Enter is still taken after completing, so the message cannot be sent");
            Type("run /sta", " on this repo");
            if (!Lists("start-project"))
                return Fail("a word with text after it does not list");
            HighlightSkill(_skillRows.Children.OfType<Border>().ToList().FindIndex(b => (string)b.Tag == "start-project"));
            Press(Key.Tab);
            if (CodeInput.Text != "run /start-project on this repo" || CodeInput.CaretIndex != 19)
                return Fail($"the text after the word became '{CodeInput.Text}' (caret {CodeInput.CaretIndex})");
            Type("fix this /sta", " now");
            Press(Key.Escape);
            if (_skillPopup.IsOpen || CodeInput.Text != "fix this /sta now")
                return Fail("Escape mid-message changed the text or left the list");
            Type("first line\n/sta");
            if (!Lists("start-project"))
                return Fail("a second line's word does not list");
            HighlightSkill(_skillRows.Children.OfType<Border>().ToList().FindIndex(b => (string)b.Tag == "start-project"));
            Press(Key.Enter);
            if (CodeInput.Text != "first line\n/start-project ")
                return Fail($"the second line completed to '{CodeInput.Text}'");
            foreach (var path in new[] { "see https://example.com/sta", "src/sta", "a/sta" })
            {
                Type(path);
                if (_skillPopup.IsOpen)
                    return Fail($"'{path}' lists");
            }
            Type("run /sta now");
            CodeInput.CaretIndex = 2;
            if (_skillPopup.IsOpen)
                return Fail("moving the caret off the word leaves the list");
            Type("run /sta ");
            if (_skillPopup.IsOpen)
                return Fail("a space leaves the list");
            if (!InvokesSkill("please /start-project now", "start-project") || InvokesSkill("restart-project", "start-project") ||
                !CurrentExtras(message: "$init-projMD").Skills.Any(s => s.EndsWith("init-projMD", StringComparison.OrdinalIgnoreCase)) ||
                !CurrentExtras(message: "please run /start-project now").Skills.Any(s => s.EndsWith("start-project", StringComparison.OrdinalIgnoreCase)))
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

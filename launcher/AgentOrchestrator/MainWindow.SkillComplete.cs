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

    private Popup? _skillPopup;
    private StackPanel? _skillRows;
    private TextBox? _skillInput;
    private int _skillIndex;
    private readonly HashSet<TextBox> _skillWatched = new();

    // The app's own commands ("/" only), handled by the engine rather than a skill.
    private static readonly (string Name, string Help)[] Commands =
    {
        ("compact", "Summarize the conversation now (add what to keep: /compact keep the API decisions)"),
    };

    // A message that is one of those commands (as the engine reads it: "/name", then anything after a space).
    private static bool IsCommand(string text) => Commands.Any(c => Regex.IsMatch(text, $@"\A\s*/{c.Name}(\s|\z)", RegexOptions.IgnoreCase));

    // The matching commands and skills for the box's text, with what each does; empty when it is not a lone "/name" or
    // "$name" in progress.
    private List<(string Name, string Help)> SkillMatches(string text)
    {
        if (SkillToken.Match(text) is not { Success: true } m)
            return [];
        var typed = m.Groups[2].Value;
        bool Fits(string n) => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && !n.Equals(typed, StringComparison.OrdinalIgnoreCase);
        var commands = m.Groups[1].Value == "/" ? Commands.Where(c => Fits(c.Name)).ToList() : [];
        var skills = AllSkills().DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(s => Fits(s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => (s.Name, s.Description));
        return [.. commands, .. skills];
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
        var typed = SkillToken.Match(input.Text).Groups[2].Value.Length;
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
        input.Text = $"{input.Text[0]}{name} ";
        input.CaretIndex = input.Text.Length;
        CloseSkillCompletion();
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
}

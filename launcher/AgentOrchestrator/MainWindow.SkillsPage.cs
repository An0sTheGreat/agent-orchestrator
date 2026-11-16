using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AgentOrchestrator;

// The Skills page (the sidebar's Skills): every skill with a switch. A skill switched on is offered to the model in every
// Chat and Code conversation (CurrentExtras); any skill, on or off, also goes with a message that names it as /name or $name.
// The app's own skills start on (AppSettings.SkillsOff), the user's are on once created (AppSettings.Skills).
public partial class MainWindow
{
    private void OpenSkills()
    {
        CloseRemoteApi();
        CloseModelHub();
        CloseModelSettings();
        RenderSkillsPage();
        Reveal(CodeView, false);
        Reveal(ChatView, false);
        Reveal(SkillsView, true);
    }

    private void CloseSkills() => Reveal(SkillsView, false);

    private void RenderSkillsPage()
    {
        TextBlock Muted(string text, double size = 12)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }
        var title = new TextBlock { Text = "Skills", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 22, FontWeight = FontWeights.SemiBold };
        var intro = Muted("Skills switched on here are offered to the model in every Chat and Code conversation, and it loads one when the task calls for it. " +
            "Any skill, on or off, can also be called in a message with /name.");
        intro.Margin = new Thickness(0, 4, 0, 0);
        var newSkill = new Button { Content = "New skill…", Tag = "SkillsNew", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        newSkill.Click += (_, _) => NewSkill();
        var openFolder = new Button { Content = "Open skills folder", Tag = "SkillsFolder", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4) };
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppSettings.SkillsRoot);
            Process.Start(new ProcessStartInfo(AppSettings.SkillsRoot) { UseShellExecute = true });
        };
        var page = new StackPanel
        {
            MaxWidth = 860, Margin = new Thickness(36, 28, 36, 36),
            Children = { title, intro, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { newSkill, openFolder } } },
        };
        var skills = AllSkills();
        foreach (var (heading, builtIn, empty) in new[] { ("YOUR SKILLS", false, $"None yet. New skill… makes one, or put a folder with a SKILL.md in {AppSettings.SkillsRoot}."), ("BUILT IN", true, "") })
        {
            var group = skills.Where(s => s.BuiltIn == builtIn).ToList();
            if (group.Count == 0 && builtIn)
                continue;
            var head = Muted(heading, 11);
            head.FontWeight = FontWeights.SemiBold;
            head.Margin = new Thickness(0, 26, 0, 6);
            page.Children.Add(head);
            if (group.Count == 0)
                page.Children.Add(Muted(empty));
            var card = new StackPanel();
            foreach (var skill in group)
            {
                var key = Path.GetFileName(skill.Folder);
                var toggle = new ToggleButton { Style = (Style)FindResource("ToggleSwitch"), IsChecked = HasSkill(skill), Tag = "skill:" + key, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Offer this skill in every conversation" };
                toggle.Click += (_, _) => SetSkill(key, toggle.IsChecked == true, skill.BuiltIn);
                DockPanel.SetDock(toggle, Dock.Right);
                var name = new TextBlock { Text = "/" + skill.Name, FontWeight = FontWeights.SemiBold, FontSize = 13 };
                var about = Muted(skill.Description.Length > 0 ? skill.Description : "No description.", 11.5);
                about.Margin = new Thickness(0, 2, 16, 0);
                var row = new DockPanel { Margin = new Thickness(14, 10, 14, 10), Children = { toggle, new StackPanel { Children = { name, about } } } };
                if (card.Children.Count > 0)
                {
                    var rule = new Border { Height = 1, Opacity = 0.7 };
                    rule.SetResourceReference(Border.BackgroundProperty, "Border");
                    card.Children.Add(rule);
                }
                card.Children.Add(row);
            }
            if (group.Count > 0)
            {
                var border = new Border { Child = card, BorderThickness = new Thickness(1) };
                border.SetResourceReference(Border.BackgroundProperty, "Card");
                border.SetResourceReference(Border.BorderBrushProperty, "Border");
                border.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
                page.Children.Add(border);
            }
        }
        SkillsView.Children.Clear();
        SkillsView.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Tag = "SkillsPage" });
    }
}

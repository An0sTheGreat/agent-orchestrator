using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrchestrator;

// Project > Task limits: task_limits in the selected project's config.json (never the template in the workflow root).
public partial class MainWindow
{
    private void TaskLimits_Click(object sender, RoutedEventArgs e)
    {
        if (_process is not null)
            return;
        var path = ProjectConfigPath();
        if (path.Length == 0 || !File.Exists(path))
        {
            SetStatus("Initialize the project first", "#F7C873");
            return;
        }
        if (LauncherLogic.IsTemplateConfig(path, _workflowRoot))
        {
            SetStatus("Select or initialize a project to change task limits", "#F7C873");
            return;
        }
        (string Files, string Work) current;
        List<(string Name, long Context)> contexts;
        try
        {
            current = LauncherLogic.ReadTaskLimits(path);
            contexts = LauncherLogic.RoleContexts(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log($"Could not read config.json: {exception.Message}");
            SetStatus("Could not read config.json", "#FF667A");
            return;
        }
        BuildTaskLimitsDialog(path, current, contexts).Dialog.ShowDialog();
    }

    private (DialogShell Dialog, TextBlock Warning) BuildTaskLimitsDialog(string path, (string Files, string Work) current, List<(string Name, long Context)> contexts)
    {
        var fields = new (string Label, string Hint, string Value)[]
        {
            ("Max files per task", $"1 or more; empty = default ({LauncherLogic.DefaultMaxFiles})", current.Files),
            ("Max work items per task", "1 or more; empty = default (10)", current.Work),
        };
        var grid = new Grid { MinWidth = 340 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        var boxes = new TextBox[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hint = new TextBlock { Text = fields[i].Hint, FontSize = 10.5 };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var label = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, i == 0 ? 0 : 9, 12, 0),
                Children = { new TextBlock { Text = fields[i].Label, FontWeight = FontWeights.SemiBold, FontSize = 12 }, hint },
            };
            boxes[i] = new TextBox { Text = fields[i].Value, Height = 30, Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, i == 0 ? 0 : 9, 0, 0) };
            Grid.SetRow(label, i);
            Grid.SetRow(boxes[i], i);
            Grid.SetColumn(boxes[i], 1);
            grid.Children.Add(label);
            grid.Children.Add(boxes[i]);
        }
        // Non-blocking: shown while the files field would not fit a role's context length; saving still works.
        var warning = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0xC8, 0x73)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        void RefreshWarning()
        {
            var text = boxes[0].Text.Trim();
            var files = text.Length == 0 ? LauncherLogic.DefaultMaxFiles : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            warning.Text = files > 0 ? LauncherLogic.TaskContextWarning(files, contexts) ?? "" : "";
            warning.Visibility = warning.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        boxes[0].TextChanged += (_, _) => RefreshWarning();
        RefreshWarning();
        var note = new TextBlock { Text = "The Planner keeps each task within these limits. Larger tasks need more context.", FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x7A)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var save = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var dialog = new DialogShell(this, "Task limits", new StackPanel { Children = { grid, warning, note, error, buttons } });
        save.Click += (_, _) =>
        {
            try
            {
                if (LauncherLogic.WriteTaskLimits(path, boxes[0].Text, boxes[1].Text) is { } problem)
                {
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                Log($"Could not update config.json: {exception.Message}");
                SetStatus("Could not update config.json", "#FF667A");
                dialog.Close();
                return;
            }
            Log("Task limits saved");
            Toast("Task limits saved");
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) =>
        {
            boxes[0].Focus();
            boxes[0].SelectAll();
        };
        return (dialog, warning);
    }
}

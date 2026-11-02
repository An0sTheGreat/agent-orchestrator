using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AgentOrchestrator;

// The Code composer's command permissions (left of Search): when the sandboxed run_command asks before it runs. Saved
// (AppSettings.CommandPermissions) and sent with every Code turn and project run as the request's "permissions".
public partial class MainWindow
{
    private static readonly (string Value, string Label, string Help)[] PermissionModes =
    {
        ("ask", "Always Ask", "Asks before every command or script the model runs."),
        ("safe", "Allow Safe", "Runs commands in the sandbox without asking: they can change only the chat's folders, with no network. Asks before network access or writing other folders."),
        ("all", "Allow All", "Runs commands without asking and grants network access and other folders when the model asks for them. Commands still run in the sandbox, which never opens system folders, AppData or this app."),
    };

    private static string PermissionsTip =>
        "Command permissions: when the model's commands and scripts need your OK.\n\n" +
        string.Join("\n\n", PermissionModes.Select(m => $"{m.Label}: {m.Help}")) +
        "\n\nIn a request popup, Allow similar commands for this session allows the same program again in that chat until the app closes.";

    private void RefreshPermissions()
    {
        var mode = PermissionModes.FirstOrDefault(m => m.Value == _settings.CommandPermissions);
        if (mode.Value is null)
            mode = PermissionModes[1];
        CodePermissionsText.Text = $"{mode.Label}  ▴";
        CodePermissionsButton.IsChecked = mode.Value != "safe"; // lit when it differs from the default
        CodePermissionsButton.ToolTip = PermissionsTip;
    }

    private void Permissions_Click(object sender, RoutedEventArgs e)
    {
        RefreshPermissions(); // a ToggleButton flips itself on click
        var menu = NewPresetMenu(CodePermissionsButton);
        menu.Placement = PlacementMode.Top; // a dropup: the composer sits at the window's bottom
        foreach (var (value, label, help) in PermissionModes)
        {
            var item = PresetItem(label, () => SetPermissions(value));
            item.IsChecked = _settings.CommandPermissions == value;
            item.ToolTip = help;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SetPermissions(string value)
    {
        _settings.CommandPermissions = value;
        _settings.Save();
        RefreshPermissions();
    }
}

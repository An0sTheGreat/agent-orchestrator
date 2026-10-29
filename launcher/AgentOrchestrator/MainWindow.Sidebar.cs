using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Entry = AgentOrchestrator.ConversationStore.Entry;
using GroupView = AgentOrchestrator.ConversationStore.GroupView;

namespace AgentOrchestrator;

// The sidebar's conversation list, rebuilt flat from the store after every change: New chat / New project, each project
// (a collapsible group of chats), RECENTS (chats in no project), then a collapsible ARCHIVED section. A chat or project
// has a menu ("…" on hover, or right-click) to rename, reorder, move, archive or delete it. Each row's main control
// carries a Tag (a chat's id, a header's chevron and name) that --ui-smoke reads the order from.
public partial class MainWindow
{
    // The menu builders by chat or project id: menus are built fresh when opened, and --ui-smoke clicks their items.
    private readonly Dictionary<string, Func<ContextMenu>> _sidebarMenus = new();

    private bool CodeBusy => _chatBusy || _process is not null || _codeStarting;

    private void RefreshSidebarList()
    {
        SidebarList.Children.Clear();
        _sidebarMenus.Clear();
        var view = _store.View();
        SidebarList.Children.Add(SidebarAction("", "New chat", NewCodeConversation));
        SidebarList.Children.Add(SidebarAction("", "New project", NewSidebarProject));
        foreach (var group in view.Groups)
        {
            var header = SidebarHeader(ProjectName(group), group.Collapsed, group.Items.Count, () => _store.SetGroupCollapsed(group.Id, !group.Collapsed));
            SidebarList.Children.Add(WithMenu(header, group.Id, ProjectName(group), () => ProjectMenu(group, view.Groups)));
            if (!group.Collapsed)
                for (var i = 0; i < group.Items.Count; i++)
                    SidebarList.Children.Add(ChatRow(group.Items[i], group.Id, i, group.Items.Count, view.Groups, archived: false));
        }
        if (view.Ungrouped.Count > 0)
        {
            var recents = new TextBlock { Text = "RECENTS", Tag = "RECENTS", Margin = new Thickness(8, 14, 0, 6) };
            recents.SetResourceReference(StyleProperty, "SectionHeader");
            SidebarList.Children.Add(recents);
            for (var i = 0; i < view.Ungrouped.Count; i++)
                SidebarList.Children.Add(ChatRow(view.Ungrouped[i], null, i, view.Ungrouped.Count, view.Groups, archived: false));
        }
        if (view.Archived.Count > 0)
        {
            SidebarList.Children.Add(SidebarHeader("ARCHIVED", view.ArchivedCollapsed, view.Archived.Count, () => _store.SetArchivedCollapsed(!view.ArchivedCollapsed)));
            if (!view.ArchivedCollapsed)
                foreach (var entry in view.Archived)
                    SidebarList.Children.Add(ChatRow(entry, null, 0, 1, view.Groups, archived: true));
        }
    }

    private static string ProjectName(GroupView group) => group.Name.Length > 0 ? group.Name : "Untitled project";

    // ---- rows

    private Button SidebarAction(string glyph, string label, Action click)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 12, Width = 22, HorizontalAlignment = HorizontalAlignment.Left };
        icon.SetResourceReference(StyleProperty, "RailGlyph");
        var button = new Button { Content = new DockPanel { Children = { icon, new TextBlock { Text = label } } }, Tag = "+" + label, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 1) };
        button.SetResourceReference(StyleProperty, "SidebarRow");
        button.Click += (_, _) => click();
        return button;
    }

    // A collapsible header: chevron, name, muted count. Clicking it flips the collapsed state.
    private Button SidebarHeader(string name, bool collapsed, int count, Action toggle)
    {
        var chevron = new TextBlock { Text = collapsed ? "▸" : "▾", Width = 14 };
        var number = new TextBlock { Text = count.ToString(), Opacity = 0.7, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(number, Dock.Right);
        var button = new Button
        {
            Content = new DockPanel { Children = { chevron, number, new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis } } },
            Tag = chevron.Text + name, FontSize = 11, FontWeight = FontWeights.SemiBold, Padding = new Thickness(8, 5, 30, 5), Margin = new Thickness(0, 12, 0, 2),
        };
        button.SetResourceReference(StyleProperty, "SidebarRow");
        button.SetResourceReference(ForegroundProperty, "Muted");
        AutomationProperties.SetName(button, $"{name}, {count}, {(collapsed ? "collapsed" : "expanded")}");
        button.Click += (_, _) => StoreChange(toggle);
        return button;
    }

    // One line: the title (ellipsis), highlighted when it is the open chat; the folder and last change in the tooltip.
    private UIElement ChatRow(Entry entry, string? groupId, int index, int count, IReadOnlyList<GroupView> groups, bool archived)
    {
        var row = new ToggleButton
        {
            Content = new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis }, Tag = entry.Id, IsChecked = _codeConv?.Id == entry.Id,
            Padding = new Thickness(14, 6, 30, 6), Margin = new Thickness(groupId is null ? 0 : 12, 0, 0, 1), ToolTip = $"{entry.Folder}\n{entry.Updated:g}",
        };
        row.SetResourceReference(StyleProperty, "NavToggle");
        row.Click += (_, _) => OpenCodeConversation(entry.Id);
        return WithMenu(row, entry.Id, entry.Title, () => ChatMenu(entry, groupId, index, count, groups, archived));
    }

    // The row with a "…" button over its right end (shown on hover) and the same menu on right-click.
    private Grid WithMenu(Control row, string id, string name, Func<ContextMenu> build)
    {
        _sidebarMenus[id] = build;
        row.ContextMenu = new ContextMenu(); // never shown: ContextMenuOpening opens a freshly built menu instead
        row.ContextMenuOpening += (_, e) =>
        {
            e.Handled = true;
            OpenSidebarMenu(build(), row, PlacementMode.MousePoint);
        };
        var glyph = new TextBlock { Text = "", FontSize = 12 };
        glyph.SetResourceReference(StyleProperty, "RailGlyph");
        var more = new Button
        {
            Content = glyph, Width = 24, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, row.Margin.Top, 4, row.Margin.Bottom),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = "More",
        };
        more.SetResourceReference(StyleProperty, "SidebarRow");
        more.SetResourceReference(ForegroundProperty, "Muted");
        AutomationProperties.SetName(more, $"More options for {name}");
        more.Click += (_, _) => OpenSidebarMenu(build(), more, PlacementMode.Bottom);
        var grid = new Grid { Children = { row, more } };
        more.SetBinding(VisibilityProperty, new Binding(nameof(IsMouseOver)) { Source = grid, Converter = new BooleanToVisibilityConverter() });
        return grid;
    }

    private static void OpenSidebarMenu(ContextMenu menu, UIElement target, PlacementMode placement)
    {
        (menu.PlacementTarget, menu.Placement) = (target, placement);
        menu.IsOpen = true;
    }

    // ---- menus

    // Archived chats only rename, unarchive or delete; their order and project are kept for when they come back.
    private ContextMenu ChatMenu(Entry entry, string? groupId, int index, int count, IReadOnlyList<GroupView> groups, bool archived)
    {
        var menu = NewPresetMenu(SidebarList);
        menu.Items.Add(PresetItem("Rename…", () => RenameChat(entry)));
        if (!archived)
        {
            menu.Items.Add(PresetItem("Move up", () => StoreChange(() => _store.MoveUp(entry.Id)), index > 0));
            menu.Items.Add(PresetItem("Move down", () => StoreChange(() => _store.MoveDown(entry.Id)), index < count - 1));
            var moves = groups.Where(g => g.Id != groupId).Select(g => PresetItem($"Move to {ProjectName(g)}", () => StoreChange(() => _store.MoveToGroup(entry.Id, g.Id))))
                .Append(groupId is null ? null : PresetItem("Remove from project", () => StoreChange(() => _store.MoveToGroup(entry.Id, null)))).OfType<MenuItem>().ToList();
            if (moves.Count > 0)
                menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
            moves.ForEach(item => menu.Items.Add(item));
        }
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        menu.Items.Add(PresetItem(archived ? "Unarchive" : "Archive", () => StoreChange(() => _store.Archive(entry.Id, !archived))));
        menu.Items.Add(PresetItem("Delete…", () => DeleteChat(entry), !CodeBusy));
        return menu;
    }

    private ContextMenu ProjectMenu(GroupView group, IReadOnlyList<GroupView> groups)
    {
        var index = groups.Select(g => g.Id).ToList().IndexOf(group.Id);
        var menu = NewPresetMenu(SidebarList);
        menu.Items.Add(PresetItem("Rename…", () =>
        {
            if (SidebarAsk("Rename project", "Project name", "Rename", group.Name) is { } name)
                StoreChange(() => _store.RenameGroup(group.Id, name));
        }));
        menu.Items.Add(PresetItem("Move up", () => StoreChange(() => _store.MoveGroupUp(group.Id)), index > 0));
        menu.Items.Add(PresetItem("Move down", () => StoreChange(() => _store.MoveGroupDown(group.Id)), index < groups.Count - 1));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        menu.Items.Add(PresetItem("Delete project…", () =>
        {
            if (SidebarAsk("Delete project", $"Delete the project \"{ProjectName(group)}\"? Its chats are kept and move to Recents.", "Delete") is not null)
                StoreChange(() => _store.DeleteGroup(group.Id));
        }));
        return menu;
    }

    private void NewSidebarProject()
    {
        if (SidebarAsk("New project", "Name the project; move chats into it from their menu.", "Create", "") is { } name)
            StoreChange(() => _store.CreateGroup(name));
    }

    private void RenameChat(Entry entry)
    {
        if (SidebarAsk("Rename chat", "Chat title", "Rename", entry.Title) is { } title && title != entry.Title)
            StoreChange(() => _store.Rename(entry.Id, title));
    }

    // The open chat is closed first (its view emptied); refused while a turn or run is active.
    private void DeleteChat(Entry entry)
    {
        if (CodeBusy)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            return;
        }
        if (SidebarAsk("Delete chat", $"Delete \"{entry.Title}\"? This can't be undone.", "Delete") is null)
            return;
        if (_codeConv?.Id == entry.Id)
        {
            _codeConv = null; // so closing does not treat it as an empty chat to clean up
            CloseCodeConversation();
        }
        StoreChange(() => _store.Delete(entry.Id));
    }

    private void StoreChange(Action change)
    {
        StoreDo(change);
        RefreshSidebarList();
    }

    // AskDialog, answered by --ui-smoke's _codeChoice hook (by title) when it is set.
    private string? SidebarAsk(string title, string message, string okLabel, string? input = null) =>
        _codeChoice is { } answer ? answer(title) : AskDialog(title, message, okLabel, input);
}

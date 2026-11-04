using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Entry = AgentOrchestrator.ConversationStore.Entry;
using GroupView = AgentOrchestrator.ConversationStore.GroupView;

namespace AgentOrchestrator;

// The sidebar's conversation list, rebuilt flat from the store after every change: New chat / New project, each project
// (a collapsible group of chats), RECENTS (chats in no project), then a collapsible ARCHIVED section. A chat or project
// has a menu ("…" on hover, or right-click) to rename, reorder, move, archive or delete it, and can be dragged: a chat
// onto another chat (takes its place), a project header (its top) or RECENTS; a project onto another project's header.
// Code chats open in the Code view, Chat-view chats in the Chat view. Each row's main control carries a Tag (a chat's
// id, a header's chevron and name) that --ui-smoke reads the order from.
public partial class MainWindow
{
    // The menu builders by chat or project id: menus are built fresh when opened, and --ui-smoke clicks their items.
    private readonly Dictionary<string, Func<ContextMenu>> _sidebarMenus = new();

    // The drop handlers by target (a chat's id, a project's id, "RECENTS"): the store change for a dragged "chat:<id>" or
    // "project:<id>", or null when that drop does nothing there. --ui-smoke calls them directly.
    private readonly Dictionary<string, Func<string?, Func<bool>?>> _sidebarDrops = new();
    private const string DragFormat = "AgentOrchestrator.SidebarItem";

    private bool CodeBusy => _chatBusy || _process is not null;

    private void RefreshSidebarList()
    {
        SidebarList.Children.Clear();
        _sidebarMenus.Clear();
        _sidebarDrops.Clear();
        var view = _store.View();
        if (AppSettings.Unified) // the test build: no Chat/Code switch; Chat's section on top, Code's below, each with its own lists
        {
            SidebarList.Children.Add(SidebarAction("", "New chat", NewSidebarChat));
            SidebarList.Children.Add(SidebarAction("", "Work in a folder", () => NewCodeConversation()));
            AddSidebarSection(view, chat: true, "CHAT");
            AddSidebarSection(view, chat: false, "CODE");
        }
        else
        {
            SidebarList.Children.Add(SidebarAction("", "New chat", NewSidebarChat));
            SidebarList.Children.Add(SidebarAction("", "New project", NewSidebarProject));
            AddSidebarSection(view, NavChat.IsChecked == true, null);
        }
        RefreshTitleChat(view);
    }

    // One kind's lists: its projects, RECENTS, then ARCHIVED. title (the unified sidebar): the section's name over them, with
    // a "+" for a new project of that kind; its drop keys and archived state are the section's own.
    // ponytail: Move up/down swaps with the store neighbour, which may be the other kind's (no visible move then).
    private void AddSidebarSection(ConversationStore.SidebarView view, bool chat, string? title)
    {
        List<Entry> Mine(IEnumerable<Entry> entries) => entries.Where(e => (e.Kind == "chat") == chat).ToList();
        var kindName = chat ? "chat" : "code";
        // a dragged chat stays in its own section (its kind decides where it shows)
        bool Fits(string id) => title is null || (_store.Get(id)?.Kind == "chat") == chat;
        var suffix = title is null ? "" : ":" + kindName;
        if (title is not null)
        {
            var label = new TextBlock { Text = title, Tag = "SECTION:" + title, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 18, 0, 2) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            // + starts a conversation of the section's kind; the folder beside it makes a project there
            Button HeaderButton(string glyph, string tag, string tip, double right, Action click)
            {
                var icon = new TextBlock { Text = glyph, FontSize = 11 };
                icon.SetResourceReference(StyleProperty, "RailGlyph");
                var button = new Button { Content = icon, Width = 24, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, 16, right, 0), Tag = tag,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
                button.SetResourceReference(StyleProperty, "SidebarRow");
                button.SetResourceReference(ForegroundProperty, "Muted");
                AutomationProperties.SetName(button, tip);
                button.Click += (_, _) => click();
                return button;
            }
            var plus = HeaderButton("", "+new" + suffix, chat ? "New chat" : "New code conversation", 4, chat ? NewSidebarChat : NewCodeChat);
            var project = HeaderButton("", "+project" + suffix, $"New project in {title.ToLowerInvariant()}", 30, () => NewSidebarProject(kindName));
            SidebarList.Children.Add(new Grid { Children = { label, project, plus } });
        }
        // A project shows in the view of its chats: one with chats of the other kind only stays out, an empty one shows where
        // it was made (an older one, with no kind, in both). Moves and drops offer only these.
        var shown = view.Groups.Where(g => Mine(g.Items).Count > 0 || g.Items.Count == 0 && (g.Kind.Length == 0 || g.Kind == kindName)).ToList();
        foreach (var group in shown)
        {
            var items = Mine(group.Items);
            var header = SidebarHeader(ProjectName(group), group.Collapsed, items.Count, () => _store.SetGroupCollapsed(group.Id, !group.Collapsed));
            Draggable(header, "project:" + group.Id);
            header.Padding = new Thickness(8, 5, 56, 5); // room for + and …
            SidebarList.Children.Add(DropTarget(WithMenu(header, group.Id, ProjectName(group), () => ProjectMenu(group, shown), () => NewChatInProject(group, chat)), group.Id,
                (kind, id) => kind == "chat" ? Fits(id) ? () => _store.MoveToGroup(id, group.Id) : null : id == group.Id ? null : () => _store.MoveGroupTo(id, group.Id)));
            if (!group.Collapsed)
                for (var i = 0; i < items.Count; i++)
                    SidebarList.Children.Add(ChatRow(items[i], group.Id, i, items.Count, shown, archived: false));
        }
        var ungrouped = Mine(view.Ungrouped);
        if (ungrouped.Count > 0 || shown.Count > 0) // with projects, RECENTS is where a chat is dragged out to
        {
            var recents = new TextBlock { Text = "RECENTS", Tag = "RECENTS" + suffix, Margin = new Thickness(8, 14, 0, 6) };
            recents.SetResourceReference(StyleProperty, "SectionHeader");
            SidebarList.Children.Add(DropTarget(new Grid { Children = { recents } }, "RECENTS" + suffix, (kind, id) => kind == "chat" && Fits(id) ? () => _store.MoveToGroup(id, null) : null));
            for (var i = 0; i < ungrouped.Count; i++)
                SidebarList.Children.Add(ChatRow(ungrouped[i], null, i, ungrouped.Count, shown, archived: false));
        }
        else if (title is not null)
        {
            var empty = new TextBlock { Text = chat ? "No chats yet" : "No folder conversations yet", FontSize = 11, Margin = new Thickness(10, 6, 0, 4) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            SidebarList.Children.Add(empty);
        }
        var archivedRows = Mine(view.Archived);
        var collapsed = title is not null && chat ? view.ChatArchivedCollapsed : view.ArchivedCollapsed;
        if (archivedRows.Count > 0)
        {
            SidebarList.Children.Add(SidebarHeader("ARCHIVED", collapsed, archivedRows.Count, () => _store.SetArchivedCollapsed(!collapsed, chat: title is not null && chat)));
            if (!collapsed)
                foreach (var entry in archivedRows)
                    SidebarList.Children.Add(ChatRow(entry, null, 0, 1, shown, archived: true));
        }
    }

    // The title bar names the chat open in the current view (Chat or Code); its menu is that chat's sidebar menu.
    private string? TitleChatId => (NavChat.IsChecked == true ? _chatConv : NavCode.IsChecked == true ? _codeConv : null)?.Id;

    private void RefreshTitleChat(ConversationStore.SidebarView view)
    {
        var entry = TitleChatId is { } id ? view.Groups.SelectMany(g => g.Items).Concat(view.Ungrouped).Concat(view.Archived).FirstOrDefault(e => e.Id == id) : null;
        TitleChatButton.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        TitleChatText.Text = entry?.Title ?? "";
    }

    private void TitleChat_Click(object sender, RoutedEventArgs e)
    {
        if (TitleChatId is { } id && _sidebarMenus.TryGetValue(id, out var build))
            OpenSidebarMenu(build(), TitleChatButton, PlacementMode.Bottom);
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
        var number = new TextBlock { Text = count.ToString(), Opacity = 0.7, Margin = new Thickness(6, 0, 0, 0),
            Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed }; // expanded, the rows below show it
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

    // One line: the title (ellipsis), highlighted when it is the chat open in the view shown; the folder (or "Chat") and
    // last change in the tooltip.
    private UIElement ChatRow(Entry entry, string? groupId, int index, int count, IReadOnlyList<GroupView> groups, bool archived)
    {
        var open = NavChat.IsChecked == true ? _chatConv : NavCode.IsChecked == true ? _codeConv : null;
        var content = new DockPanel();
        // A chat's speech bubble or a Code conversation's braces, as on the Chat / Code toggle.
        var glyph = new TextBlock { Text = entry.Kind == "chat" ? "\uE8BD" : "\uE943", FontSize = 11, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        glyph.SetResourceReference(StyleProperty, "RailGlyph");
        content.Children.Add(glyph);
        if (entry.Id == RunningId) // a reply or run is working in it
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Working…" };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
            DockPanel.SetDock(dot, Dock.Right);
            content.Children.Add(dot);
        }
        content.Children.Add(new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new ToggleButton
        {
            Content = content, Tag = entry.Id, IsChecked = open?.Id == entry.Id, Padding = new Thickness(14, 6, 30, 6),
            Margin = new Thickness(groupId is null ? 0 : 12, 0, 0, 1), ToolTip = $"{(entry.Kind == "chat" ? "Chat" : entry.Folder)}\n{entry.Updated:g}",
        };
        row.SetResourceReference(StyleProperty, "NavToggle");
        row.Click += (_, _) => OpenSidebarConversation(entry);
        var withMenu = WithMenu(row, entry.Id, entry.Title, () => ChatMenu(entry, groupId, index, count, groups, archived));
        if (archived)
            return withMenu;
        Draggable(row, "chat:" + entry.Id);
        return DropTarget(withMenu, entry.Id, (kind, id) => kind == "chat" && id != entry.Id ? () => _store.MoveTo(id, groupId, entry.Id) : null);
    }

    private void OpenSidebarConversation(Entry entry)
    {
        if (entry.Kind == "chat")
            OpenChatConversation(entry.Id);
        else
            OpenCodeConversation(entry.Id);
    }

    // A new chat in the view shown: the Chat view's own, else a Code chat (which asks for its folder).
    private void NewSidebarChat()
    {
        if (AppSettings.Unified) // the test build: a new conversation is a chat until it works in a folder
        {
            ShowView(chat: true);
            NewChat_Click(this, new RoutedEventArgs());
        }
        else if (NavChat.IsChecked == true)
            NewChat_Click(this, new RoutedEventArgs());
        else
            NewCodeConversation();
    }

    // A project header's "+": a new chat already in that project. In Code it works in the folder of the project's latest
    // Code chat, without asking; only a project with no Code chat in an existing folder asks for one.
    // The Code section's +: a new Code conversation without asking for a folder. The Code view shows its empty state: the
    // current folder's (its PROJECT.md message), or choose a folder when there is none.
    private void NewCodeChat()
    {
        if (CodeBusy)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            return;
        }
        if (!DiscardWorkerEdits())
            return;
        CloseCodeConversation();
        ShowView();
        RefreshCodeFolder();
        RefreshSidebarList();
        CodeInput.Focus();
    }

    // chat: the section (or view) the project is in.
    private void NewChatInProject(GroupView group, bool chat)
    {
        if (CodeBusy && !chat)
        {
            Toast("Wait for the reply or run to finish", "#F7C873");
            return;
        }
        if (group.Collapsed)
            _store.SetGroupCollapsed(group.Id, false);
        if (chat)
        {
            ShowView(chat: true);
            NewChat_Click(this, new RoutedEventArgs());
            _newChatGroup = group.Id; // the chat is saved at its first reply (NewStoredChat)
            return;
        }
        var folder = group.Items.Where(e => e.Kind != "chat" && e.Folder.Length > 0 && System.IO.Directory.Exists(e.Folder))
            .OrderByDescending(e => e.Updated).FirstOrDefault()?.Folder;
        NewCodeConversation(folder, group.Id);
    }

    // ---- drag and drop

    // A press, then a move past the system's drag distance, drags the payload. The row lets go of the mouse first, so it
    // does not stay pressed (or click) when the drop ends.
    private void Draggable(Control row, string payload)
    {
        Point? press = null;
        row.PreviewMouseLeftButtonDown += (_, e) => press = e.GetPosition(row);
        row.PreviewMouseLeftButtonUp += (_, _) => press = null;
        row.PreviewMouseMove += (_, e) =>
        {
            if (press is not { } start || e.LeftButton != MouseButtonState.Pressed)
                return;
            var moved = e.GetPosition(row) - start;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            press = null;
            if (_store.ReadOnly)
                return; // nothing could move; the press stays a click
            row.ReleaseMouseCapture();
            DragDrop.DoDragDrop(row, new DataObject(DragFormat, payload), DragDropEffects.Move);
        };
    }

    // The target highlights while a drop there would do something; the change runs once the drag has ended (the list is
    // rebuilt, so not while the dragged row is still in it).
    private Grid DropTarget(Grid target, string key, Func<string, string, Func<bool>?> changeFor)
    {
        Func<bool>? ChangeOf(string? payload) => payload?.Split(':', 2) is [var kind, var id] ? changeFor(kind, id) : null;
        _sidebarDrops[key] = ChangeOf;
        Func<bool>? Change(DragEventArgs e) => ChangeOf(e.Data.GetData(DragFormat) as string);
        void Mark(bool on)
        {
            if (on)
                target.SetResourceReference(Panel.BackgroundProperty, "Hover");
            else
                target.Background = Brushes.Transparent;
        }
        target.Background = Brushes.Transparent; // hit-testable across the whole row
        target.AllowDrop = true;
        target.DragEnter += (_, e) => Mark(Change(e) is not null);
        target.DragLeave += (_, _) => Mark(false);
        target.DragOver += (_, e) =>
        {
            e.Effects = Change(e) is null ? DragDropEffects.None : DragDropEffects.Move;
            e.Handled = true;
        };
        target.Drop += (_, e) =>
        {
            Mark(false);
            e.Handled = true;
            if (Change(e) is { } change)
                Dispatcher.BeginInvoke(() => StoreChange(() => change()));
        };
        return target;
    }

    // The row with a "…" button over its right end (shown on hover) and the same menu on right-click.
    // add: a "+" left of "…" (a project header's New chat in this project).
    private Grid WithMenu(Control row, string id, string name, Func<ContextMenu> build, Action? add = null)
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
        if (add is not null)
        {
            var addGlyph = new TextBlock { Text = "", FontSize = 11 }; // Add, in the icon font "…" uses, so it sits centred like it
            addGlyph.SetResourceReference(StyleProperty, "RailGlyph");
            var plus = new Button
            {
                Content = addGlyph, Width = 24, Height = 22, Padding = new Thickness(0), Tag = "+in:" + id,
                Margin = new Thickness(0, row.Margin.Top, 28, row.Margin.Bottom), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "New chat in this project",
            };
            plus.SetResourceReference(StyleProperty, "SidebarRow");
            plus.SetResourceReference(ForegroundProperty, "Muted");
            AutomationProperties.SetName(plus, $"New chat in {name}");
            plus.Click += (_, _) => add();
            grid.Children.Add(plus); // always shown: the way to start a chat inside the project
        }
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
        menu.Items.Add(PresetItem("Delete…", () => DeleteChat(entry), entry.Id != RunningId));
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

    // kind: the section it is made in (the unified sidebar); else the view shown.
    private void NewSidebarProject() => NewSidebarProject(NavChat.IsChecked == true ? "chat" : "code");

    private void NewSidebarProject(string kind)
    {
        if (SidebarAsk("New project", "Name the project; drag chats into it, or use a chat's menu.", "Create", "") is { } name)
            StoreChange(() => _store.CreateGroup(name, kind));
    }

    private void RenameChat(Entry entry)
    {
        if (SidebarAsk("Rename chat", "Chat title", "Rename", entry.Title) is { } title && title != entry.Title)
            StoreChange(() => _store.Rename(entry.Id, title));
    }

    // The open chat is closed first (its view emptied); refused for the chat a reply or run is using.
    private void DeleteChat(Entry entry)
    {
        if (entry.Id == RunningId)
        {
            Toast("Stop its reply or run first", "#F7C873");
            return;
        }
        if (SidebarAsk("Delete chat", $"Delete \"{entry.Title}\"? This can't be undone.", "Delete") is null)
            return;
        var wasOpen = _codeConv?.Id == entry.Id && NavCode.IsChecked == true;
        if (_codeConv?.Id == entry.Id)
        {
            _codeConv = null; // so closing does not treat it as an empty chat to clean up
            CloseCodeConversation();
        }
        if (_chatConv?.Id == entry.Id)
            ClearChatConversation();
        StoreChange(() => _store.Delete(entry.Id));
        if (AppSettings.Unified && wasOpen) // the test build: a fresh chat, not the folder's PROJECT.md prompts
            NewSidebarChat();
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

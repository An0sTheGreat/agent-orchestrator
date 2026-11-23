using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AgentOrchestrator;

// The Model Settings page: a header with Connection and the Target agent segments (WorkerTabTemplate, one per worker) and,
// below it, the selected worker's model
// settings inline (ModelSettingsBody in WorkerSettingsHost). The selection is saved (AppSettings.SelectedWorker); leaving a
// tab with unsaved edits asks first.
public partial class MainWindow
{
    private RoleCard? _shownWorker;
    private Func<bool> _workerDirty = () => false;
    private Func<bool> _workerSave = () => true; // the body's Save: true when it wrote the edits
    private int _workerTabHold;
    private bool _workerTabPending;

    private void InitWorkerTabs()
    {
        WorkerTabs.ItemsSource = _cards;
        SelectWorker(_cards.FirstOrDefault(c => c.Role == _settings.SelectedWorker && !c.Hidden) ?? FirstVisibleWorker());
        _cards.CollectionChanged += (_, _) =>
        {
            if (_shownWorker is null || !_cards.Contains(_shownWorker))
                SelectWorker(FirstVisibleWorker());
        };
    }

    // A page in place of Code and Chat, as Remote API and Models Hub are (each closes the others).
    private void OpenModelSettings()
    {
        CloseRemoteApi();
        CloseModelHub();
        CloseSkills();
        CloseImages();
        Reveal(CodeView, false);
        Reveal(ChatView, false);
        Reveal(ModelSettingsView, true);
    }

    private void CloseModelSettings() => Reveal(ModelSettingsView, false);

    private RoleCard? FirstVisibleWorker() => _cards.FirstOrDefault(c => !c.Hidden) ?? _cards.FirstOrDefault();

    private void WorkerTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RoleCard card })
            SelectWorker(card);
    }

    private void RailCardSettings_Click(object sender, RoutedEventArgs e)
    {
        RailPopup.IsOpen = false;
        if (sender is FrameworkElement { DataContext: RoleCard card })
            OpenWorkerTab(card);
    }

    // The header's three parts side by side, the pill exactly centred, when the page is 1200 wide and the left part fits
    // beside the centred pill; otherwise stacked and centred.
    private void ModelHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var natural = new Size(double.PositiveInfinity, double.PositiveInfinity);
        ModelHeaderLeft.Measure(natural);
        ModelHeaderPill.Measure(natural);
        var stack = ModelSettingsPage.ActualWidth < 1200 ||
                    ModelHeaderLeft.DesiredSize.Width > (ModelHeaderBar.ActualWidth - ModelHeaderPill.DesiredSize.Width) / 2;
        Grid.SetColumnSpan(ModelHeaderLeft, stack ? 3 : 1);
        ModelHeaderLeft.HorizontalAlignment = stack ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Grid.SetColumn(ModelHeaderPill, stack ? 0 : 1);
        Grid.SetColumnSpan(ModelHeaderPill, stack ? 3 : 1);
        Grid.SetRow(ModelHeaderPill, stack ? 1 : 0);
        ModelHeaderPill.Margin = new Thickness(0, stack ? 10 : 0, 0, 0);
    }

    // Connection [Local | Remote PC]: the shown worker's Run on the primary PC switch (in its body), whichever is used.
    private ToggleButton? PrimarySwitch() =>
        WorkerSettingsHost.Child is { } body ? SmokeFind<ToggleButton>(body, b => b.Tag as string == "RunOnPrimary") : null;

    private void SyncConnection()
    {
        var primary = PrimarySwitch();
        ConnectionLocal.IsEnabled = primary is not null;
        ConnectionRemote.IsEnabled = primary is { IsEnabled: true };
        ConnectionRemote.ToolTip = string.IsNullOrEmpty(_settings.ShareHost) ? "Connect to a primary PC in Settings › Models" : "Run this worker on the primary PC";
        (primary?.IsChecked == true ? ConnectionRemote : ConnectionLocal).IsChecked = true;
    }

    private void Connection_Click(object sender, RoutedEventArgs e)
    {
        var remote = sender == ConnectionRemote;
        if (PrimarySwitch() is { IsEnabled: true } primary && (primary.IsChecked == true) != remote)
        {
            primary.IsChecked = remote; // as a click on the switch: it flips, then its Click acts
            primary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        SyncConnection();
    }

    private void WorkerTabs_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (WorkerTabsScroll.ScrollableWidth <= 0)
            return;
        WorkerTabsScroll.ScrollToHorizontalOffset(WorkerTabsScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // True when the shown worker has no unsaved edits or the user discards them (the edits then count as gone; the caller
    // rebuilds the tab). False: the user kept them.
    private bool DiscardWorkerEdits()
    {
        if (_shownWorker is not { } shown || !_cards.Contains(shown) || !_workerDirty())
            return true;
        if (SidebarAsk("Unsaved changes", $"Discard the unsaved changes to {ChatName(shown, shown.Role)}?", "Discard") is null)
            return false;
        _workerDirty = () => false;
        return true;
    }

    // Before a run or a turn: unsaved edits are saved (continue only if that worked), discarded, or the start is cancelled.
    private bool SaveWorkerEditsFirst()
    {
        if (_shownWorker is not { } shown || !_cards.Contains(shown) || !_workerDirty())
            return true;
        switch (Choose("Save changes", $"Save changes to {ChatName(shown, shown.Role)} first?", ("Save", "save"), ("Discard", "discard")))
        {
            case "save":
                return _workerSave();
            case "discard":
                RefreshWorkerTab(force: true);
                return true;
            default:
                return false;
        }
    }

    // False when the user kept unsaved edits to the shown worker.
    private bool SelectWorker(RoleCard? card)
    {
        if (card == _shownWorker)
            return true;
        if (!DiscardWorkerEdits())
            return false;
        foreach (var c in _cards)
            c.IsSelected = c == card;
        _shownWorker = card;
        _settings.SelectedWorker = card?.Role ?? "";
        if (!_initializing)
            _settings.Save();
        RefreshWorkerTab(force: true);
        return true;
    }

    // The sidebar's Model Settings, the composer's Options…, the rail's button and its card's gear: the page on the worker's tab.
    private void OpenWorkerTab(RoleCard? card)
    {
        if (card is null)
            return;
        OpenModelSettings();
        if (SelectWorker(card))
            (WorkerTabs.ItemContainerGenerator.ContainerFromItem(card) as FrameworkElement)?.BringIntoView();
    }

    // Rebuilds the shown worker's settings from disk, unless they hold unsaved edits (force: discard them). Inside
    // HoldWorkerTab the rebuild waits for the end of the operation.
    private void RefreshWorkerTab(bool force = false)
    {
        if (_initializing || (!force && _workerDirty()))
            return;
        _workerDirty = () => false;
        if (_workerTabHold > 0)
            _workerTabPending = true;
        else
            BuildWorkerTab();
    }

    // Runs change with the tab's rebuilds held, then builds it once if any was asked for (a preset apply, a removal or a
    // save asks several times).
    private void HoldWorkerTab(Action change)
    {
        _workerTabHold++;
        try { change(); }
        finally
        {
            if (--_workerTabHold == 0 && _workerTabPending)
                BuildWorkerTab();
        }
    }

    private void BuildWorkerTab()
    {
        _workerTabPending = false;
        WorkerSettingsHost.Child = null; // the radio groups are shared by name: never two bodies at once
        (_workerDirty, _workerSave) = (() => false, () => true);
        if (_shownWorker is not { } card)
        {
            SyncConnection(); // no worker: the pill doesn't keep the last one's state
            return;
        }
        if (ModelSettingsBody(card, () => { RefreshWorkerTab(force: true); RefreshEffort(); }, () => RefreshWorkerTab(force: true), "Revert") is { } built)
        {
            (WorkerSettingsHost.Child, _workerDirty, _workerSave) = built;
            if (PrimarySwitch() is { } primary)
            {
                primary.Checked += (_, _) => SyncConnection();
                primary.Unchecked += (_, _) => SyncConnection();
            }
            SyncConnection();
            return;
        }
        var note = new TextBlock { Text = $"Could not read config.json for {ChatName(card, card.Role)}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 14, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        WorkerSettingsHost.Child = note;
        SyncConnection();
    }
}

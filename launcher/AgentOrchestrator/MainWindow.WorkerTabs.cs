using System.Windows;
using System.Windows.Input;

namespace AgentOrchestrator;

// The worker details column: a horizontal strip of worker tabs (WorkerTabTemplate) and, below it, the selected worker's model
// settings inline (ModelSettingsBody in WorkerSettingsHost). The selection is saved (AppSettings.SelectedWorker); leaving a
// tab with unsaved edits asks first.
public partial class MainWindow
{
    private RoleCard? _shownWorker;
    private Func<bool> _workerDirty = () => false;

    private void InitWorkerTabs()
    {
        WorkerTabs.ItemsSource = _cards;
        Resources["WorkerTabWidth"] = 88.0;
        SelectWorker(_cards.FirstOrDefault(c => c.Role == _settings.SelectedWorker) ?? FirstVisibleWorker());
        _cards.CollectionChanged += (_, _) =>
        {
            if (_shownWorker is null || !_cards.Contains(_shownWorker))
                SelectWorker(FirstVisibleWorker());
        };
    }

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

    // Three tabs across the column, each at least 88 wide; a narrower column scrolls the strip.
    private void WorkerTabs_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Resources["WorkerTabWidth"] = Math.Max(88, WorkerTabsScroll.ActualWidth / 3 - 6);

    private void WorkerTabs_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (WorkerTabsScroll.ScrollableWidth <= 0)
            return;
        WorkerTabsScroll.ScrollToHorizontalOffset(WorkerTabsScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // False when the user kept unsaved edits to the shown worker.
    private bool SelectWorker(RoleCard? card)
    {
        if (card == _shownWorker)
            return true;
        if (_shownWorker is { } shown && _cards.Contains(shown) && _workerDirty() &&
            SidebarAsk("Unsaved changes", $"Discard the unsaved changes to {ChatName(shown, shown.Role)}?", "Discard") is null)
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

    // The composer's Options… and the rail card's gear: show the column, then the worker's tab.
    private void OpenWorkerTab(RoleCard? card)
    {
        if (card is null)
            return;
        if (_settings.DetailsCollapsed)
        {
            SetWorkerRail(false);
            _settings.Save();
        }
        if (SelectWorker(card))
            (WorkerTabs.ItemContainerGenerator.ContainerFromItem(card) as FrameworkElement)?.BringIntoView();
    }

    // Rebuilds the shown worker's settings from disk, unless they hold unsaved edits (force: discard them).
    private void RefreshWorkerTab(bool force = false)
    {
        if (_initializing || (!force && _workerDirty()))
            return;
        WorkerSettingsHost.Child = null; // the radio groups are shared by name: never two bodies at once
        _workerDirty = () => false;
        if (_shownWorker is { } card && ModelSettingsBody(card, () => RefreshWorkerTab(force: true), () => RefreshWorkerTab(force: true), "Revert") is { } built)
            (WorkerSettingsHost.Child, _workerDirty) = (built.Body, built.IsDirty);
    }
}

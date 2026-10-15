using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VSL.ViewModels;

namespace VSL.Views;

public enum DockSide
{
    Left,
    Right,
    Bottom,
}

/// <summary>
/// Tool window docking: drag a tool window's tab onto the Dock Left / Right / Bottom guides (or use its context
/// menu) to move it between the three tool groups. Empty groups collapse; the layout is remembered.
/// </summary>
public partial class MainWindow
{
    const double DragThreshold = 8;

    TabItem? _dragItem;
    Point _dragStart;
    bool _dragging;
    DockSide? _dropTarget;

    IEnumerable<TabControl> ToolGroups => [LeftTabs, RightTabs, BottomTabs];

    IEnumerable<TabItem> ToolTabs => ToolGroups.SelectMany(g => g.Items.OfType<TabItem>());

    TabControl GroupOf(DockSide side) => side switch
    {
        DockSide.Left => LeftTabs,
        DockSide.Right => RightTabs,
        _ => BottomTabs,
    };

    public DockSide? SideOf(string toolWindow)
    {
        foreach (var side in Enum.GetValues<DockSide>())
            if (GroupOf(side).Items.OfType<TabItem>().Any(t => (string?)t.Tag == toolWindow))
                return side;
        return null;
    }

    /// <summary>True when the tool window is the front tab of its group.</summary>
    public bool IsToolWindowShowing(string toolWindow) =>
        ToolGroups.Any(g => g.IsVisible && g.SelectedItem is TabItem { Tag: string id } && id == toolWindow);

    void InitializeDocking()
    {
        foreach (var item in ToolTabs.ToList())
        {
            item.AddHandler(PointerPressedEvent, OnToolTabPressed, RoutingStrategies.Tunnel);
            item.AddHandler(PointerMovedEvent, OnToolTabMoved, RoutingStrategies.Tunnel);
            item.AddHandler(PointerReleasedEvent, OnToolTabReleased, RoutingStrategies.Tunnel);
            item.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble);
            item.ContextRequested += OnToolTabContextRequested;
        }
        UpdateDockLayout();
    }

    /// <summary>Restores the saved layout (tool window id → side) when the view model arrives.</summary>
    void ApplySavedDockLayout(MainViewModel vm)
    {
        foreach (var (id, sideName) in vm.Settings.ToolLayout.ToList())
            if (Enum.TryParse<DockSide>(sideName, out var side) && ToolTabs.FirstOrDefault(t => (string?)t.Tag == id) is { } item)
                MoveToolWindow(item, side, remember: false);
    }

    void OnToolWindowRequested(string id, string[] unlessShowing)
    {
        if (ToolGroups.Any(g => g.SelectedItem is TabItem { Tag: string selected } && unlessShowing.Contains(selected)))
            return;
        if (ToolTabs.FirstOrDefault(t => (string?)t.Tag == id) is { } item && ToolGroups.FirstOrDefault(g => g.Items.Contains(item)) is { } group)
            group.SelectedItem = item;
    }

    /// <summary>Moves a tool window to another group and selects it there.</summary>
    public void MoveToolWindow(string id, DockSide side)
    {
        if (ToolTabs.FirstOrDefault(t => (string?)t.Tag == id) is { } item)
            MoveToolWindow(item, side, remember: true);
    }

    void MoveToolWindow(TabItem item, DockSide side, bool remember)
    {
        var from = ToolGroups.FirstOrDefault(g => g.Items.Contains(item));
        var to = GroupOf(side);
        if (from is null || from == to)
            return;
        from.Items.Remove(item);
        to.Items.Add(item);
        to.SelectedItem = item;
        if (from.SelectedItem is null && from.ItemCount > 0)
            from.SelectedIndex = 0;
        UpdateDockLayout();
        if (remember && Vm is { } vm && item.Tag is string id)
        {
            vm.Settings.ToolLayout[id] = side.ToString();
            vm.Settings.Save();
        }
    }

    /// <summary>Shows groups that hold tool windows and collapses empty ones (and their splitters).</summary>
    void UpdateDockLayout()
    {
        var columns = MainGrid.ColumnDefinitions;
        var rows = LeftGrid.RowDefinitions;
        var left = LeftTabs.ItemCount > 0;
        var right = RightTabs.ItemCount > 0;
        var bottom = BottomTabs.ItemCount > 0;

        columns[0].Width = left ? (columns[0].Width.Value > 0 ? columns[0].Width : new GridLength(300)) : new GridLength(0);
        columns[1].Width = new GridLength(left ? 4 : 0);
        LeftDock.IsVisible = LeftSplitter.IsVisible = left;

        columns[4].Width = right ? (columns[4].Width.Value > 0 ? columns[4].Width : new GridLength(400)) : new GridLength(0);
        columns[3].Width = new GridLength(right ? 4 : 0);
        RightDock.IsVisible = RightSplitter.IsVisible = right;

        rows[2].Height = bottom ? (rows[2].Height.Value > 0 ? rows[2].Height : new GridLength(250)) : new GridLength(0);
        rows[1].Height = new GridLength(bottom ? 4 : 0);
        BottomDock.IsVisible = BottomSplitter.IsVisible = bottom;
    }

    void OnToolTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TabItem item && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragItem = item;
            _dragStart = e.GetPosition(this);
            _dragging = false;
        }
    }

    void OnToolTabMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var position = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStart.X) < DragThreshold && Math.Abs(position.Y - _dragStart.Y) < DragThreshold)
                return;
            _dragging = true;
            e.Pointer.Capture(_dragItem);
            DockOverlay.IsVisible = true;
        }
        _dropTarget = ZoneAt(e.GetPosition(DockOverlay));
        DockZoneLeft.Classes.Set("hot", _dropTarget == DockSide.Left);
        DockZoneRight.Classes.Set("hot", _dropTarget == DockSide.Right);
        DockZoneBottom.Classes.Set("hot", _dropTarget == DockSide.Bottom);
        e.Handled = true;
    }

    void OnToolTabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || _dragItem is null)
        {
            _dragItem = null;
            return;
        }
        var item = _dragItem;
        var target = _dropTarget;
        e.Pointer.Capture(null);
        EndDrag();
        if (target is { } side)
            MoveToolWindow(item, side, remember: true);
        e.Handled = true;
    }

    void EndDrag()
    {
        _dragItem = null;
        _dragging = false;
        _dropTarget = null;
        DockOverlay.IsVisible = false;
        foreach (var zone in new[] { DockZoneLeft, DockZoneRight, DockZoneBottom })
            zone.Classes.Set("hot", false);
    }

    DockSide? ZoneAt(Point p)
    {
        if (DockZoneLeft.Bounds.Contains(p))
            return DockSide.Left;
        if (DockZoneRight.Bounds.Contains(p))
            return DockSide.Right;
        if (DockZoneBottom.Bounds.Contains(p))
            return DockSide.Bottom;
        return null;
    }

    void OnToolTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not TabItem { Tag: string id } item)
            return;
        e.Handled = true;
        var current = SideOf(id);
        MenuItem Item(string header, DockSide side)
        {
            var menuItem = new MenuItem { Header = header, IsEnabled = current != side };
            menuItem.Click += (_, _) => MoveToolWindow(item, side, remember: true);
            return menuItem;
        }
        new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("Dock Left", DockSide.Left),
                Item("Dock Right", DockSide.Right),
                Item("Dock Bottom", DockSide.Bottom),
                new Separator(),
                new MenuItem
                {
                    Header = "Reset Window Layout",
                    Command = new CommunityToolkit.Mvvm.Input.RelayCommand(ResetWindowLayout),
                },
            },
        }.Open(item);
    }

    /// <summary>Window > Reset Window Layout: every tool window back to its default group.</summary>
    public void ResetWindowLayout()
    {
        foreach (var id in new[] { ToolWindows.SolutionExplorer, ToolWindows.TestExplorer, ToolWindows.GitChanges, ToolWindows.Claude })
            MoveToolWindow(id, DockSide.Right);
        foreach (var id in new[] { ToolWindows.ErrorList, ToolWindows.Output, ToolWindows.FindResults, ToolWindows.Locals, ToolWindows.Watch, ToolWindows.CallStack, ToolWindows.Terminal })
            MoveToolWindow(id, DockSide.Bottom);
        if (Vm is { } vm)
        {
            vm.Settings.ToolLayout.Clear();
            vm.Settings.Save();
        }
    }
}

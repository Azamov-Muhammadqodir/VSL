using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VSL.ViewModels;
using VSL.Workspace;

namespace VSL.Views;

public partial class MainWindow : Window, IDialogService
{
    bool _closingConfirmed;
    Key? _chord;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Dialogs = this;
                vm.BreakModeEntered += () =>
                {
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;
                    Activate();
                };
                foreach (var channel in vm.OutputChannels)
                    channel.Appended += ScrollOutputToEnd;
            }
        };
        Activated += (_, _) => Vm?.CheckExternalChanges();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        RecentMenu.SubmenuOpened += (_, _) => BuildRecentMenu();
        // Ctrl+wheel zooms the Output window too, independently of the editors.
        OutputEditor.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0)
                return;
            OutputEditor.FontSize = Math.Clamp(OutputEditor.FontSize + (e.Delta.Y > 0 ? 1 : -1), 6, 48);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        BuildRecentMenu();
    }

    MainViewModel? Vm => DataContext as MainViewModel;

    Editor.EditorHost? ActiveHost => Vm?.ActiveEditor?.Host;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || Vm is not { } vm)
            return;
        e.Cancel = true;
        vm.SaveSession();
        if (await vm.CloseSolutionCoreAsync())
        {
            await vm.ShutdownTerminalAsync();
            _closingConfirmed = true;
            Close();
        }
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
            return;
        // Ctrl+` opens the terminal (VS's View.Terminal).
        if (ctrl && e.Key == Key.OemTilde && Vm is { } terminalVm)
        {
            terminalVm.ShowTerminalCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Inside the terminal every other key belongs to the shell (Ctrl+R is reverse search, Ctrl+E end of line...).
        if (FocusManager?.GetFocusedElement() is Visual focused
            && (focused is Exclr8.Terminal.TerminalControl || focused.FindAncestorOfType<Exclr8.Terminal.TerminalControl>() is not null))
            return;
        // Visual Studio chords: Ctrl+R, A / Ctrl+R, L / Ctrl+R, Ctrl+R / Ctrl+E, T.
        if (_chord is { } first && Vm is { } chordVm)
        {
            _chord = null;
            e.Handled = true;
            switch (first, e.Key)
            {
                case (Key.R, Key.A):
                    chordVm.RunAllTestsCommand.Execute(null);
                    break;
                case (Key.R, Key.L):
                    chordVm.RepeatLastTestRunCommand.Execute(null);
                    break;
                case (Key.R, Key.R):
                    OnRename(this, new RoutedEventArgs());
                    break;
                case (Key.E, Key.T):
                    chordVm.RightTabIndex = 1;
                    break;
            }
            return;
        }
        if (ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key is Key.R or Key.E)
        {
            _chord = e.Key;
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.OemSemicolon)
        {
            ExplorerSearch.Focus();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Tab && Vm is { Documents.Count: > 1 } vm)
        {
            var i = vm.ActiveDocument is null ? 0 : vm.Documents.IndexOf(vm.ActiveDocument);
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            vm.ActiveDocument = vm.Documents[(i + step + vm.Documents.Count) % vm.Documents.Count];
            e.Handled = true;
        }
    }

    void BuildRecentMenu()
    {
        if (Vm is not { } vm)
            return;
        RecentMenu.Items.Clear();
        foreach (var path in vm.RecentSolutions)
        {
            var item = new MenuItem { Header = path, Command = vm.OpenRecentCommand, CommandParameter = path };
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
    }

    // ---- IDialogService ----------------------------------------------------------------------------------

    public async Task<string?> PickSolutionAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Project/Solution",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Solution files") { Patterns = ["*.sln", "*.slnx"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PromptAsync(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Width = 340 };
        var dialog = CreateDialog(title, new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label }, box } },
            ("OK", true), ("Cancel", false));
        box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                dialog.Close(true);
        };
        var ok = await dialog.ShowDialog<bool?>(this);
        return ok == true ? box.Text : null;
    }

    public Task<bool?> ConfirmSaveAsync(string fileName)
    {
        var dialog = CreateDialog("VSL", new TextBlock { Text = $"Save changes to the following item?\n\n{fileName}", TextWrapping = TextWrapping.Wrap },
            ("Save", true), ("Don't Save", false), ("Cancel", null));
        return dialog.ShowDialog<bool?>(this);
    }

    public Task<bool?> ConfirmAsync(string title, string message)
    {
        var dialog = CreateDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            ("Yes", true), ("No", false));
        return dialog.ShowDialog<bool?>(this);
    }

    void OnBranchButtonClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Button button)
            return;
        var menu = new MenuFlyout();
        foreach (var branch in vm.Branches)
            menu.Items.Add(new MenuItem
            {
                Header = branch == vm.GitBranch ? "✓ " + branch : "   " + branch,
                Command = vm.SwitchBranchCommand,
                CommandParameter = branch,
            });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "New Branch...", Command = vm.NewBranchCommand });
        menu.Items.Add(new MenuItem { Header = "Git Changes", Command = vm.ShowGitChangesCommand });
        menu.ShowAt(button);
    }

    void OnLaunchProfilesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control anchor)
            return;
        var menu = new MenuFlyout();
        foreach (var profile in vm.LaunchProfiles)
        {
            var item = new MenuItem { Header = (profile == vm.LaunchProfile ? "✓ " : "   ") + profile };
            item.Click += (_, _) => vm.LaunchProfile = profile;
            menu.Items.Add(item);
        }
        if (vm.LaunchProfiles.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No launch profiles (Properties/launchSettings.json)", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Start Without Debugging", Command = vm.StartWithoutDebuggingCommand, InputGesture = new KeyGesture(Key.F5, KeyModifiers.Control) });
        menu.ShowAt(anchor);
    }

    /// <summary>Right-click on a document tab: VS's tab context menu.</summary>
    void OnDocumentTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: DocumentTab doc } anchor || Vm is not { } vm)
            return;
        e.Handled = true;
        MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture is not null)
                item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => action();
            return item;
        }
        var index = vm.Documents.IndexOf(doc);
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item($"Save {doc.Title.TrimEnd('*')}", () => vm.SaveDocument((DocumentViewModel)doc), "Ctrl+S", doc is DocumentViewModel { IsDirty: true }),
                new Separator(),
                Item("Close", () => vm.CloseDocumentCommand.Execute(doc), "Ctrl+F4"),
                Item("Close All But This", () => vm.CloseAllButThisCommand.Execute(doc), enabled: vm.Documents.Count > 1),
                Item("Close All But Pinned", () => vm.CloseAllButPinnedCommand.Execute(null), enabled: vm.Documents.Any(d => !d.IsPinned)),
                Item("Close All Documents", () => vm.CloseAllDocumentsCommand.Execute(null)),
                Item("Close All Tabs to the Left", () => vm.CloseTabsToLeftCommand.Execute(doc), enabled: index > 0),
                Item("Close All Tabs to the Right", () => vm.CloseTabsToRightCommand.Execute(doc), enabled: index < vm.Documents.Count - 1),
                new Separator(),
                Item(doc.IsPinned ? "Unpin Tab" : "Pin Tab", () => vm.TogglePinCommand.Execute(doc)),
                new Separator(),
                Item("Copy Full Path", () => _ = Clipboard?.SetTextAsync((doc as DocumentViewModel)?.FilePath ?? doc.Key), enabled: doc is DocumentViewModel),
                Item("Open Containing Folder", () => OpenFolder(Path.GetDirectoryName(((DocumentViewModel)doc).FilePath)!), enabled: doc is DocumentViewModel),
                Item("Select in Solution Explorer", () =>
                {
                    vm.ActiveDocument = doc;
                    vm.RightTabIndex = 0;
                    OnSyncWithActiveDocument(this, new RoutedEventArgs());
                }, enabled: vm.Solution is not null && doc is DocumentViewModel),
            },
        };
        menu.Open(anchor);
    }

    /// <summary>Middle-click closes a tab, like VS.</summary>
    void OnDocumentTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle && sender is Control { DataContext: DocumentTab doc })
        {
            Vm?.CloseDocumentCommand.Execute(doc);
            e.Handled = true;
        }
    }

    static void OpenFolder(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", folder) { UseShellExecute = false });
        }
        catch (Exception)
        {
            // No file manager registered.
        }
    }

    void OnGitItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: GitFileItem item })
            Vm?.OpenDiffCommand.Execute(item);
    }

    Window CreateDialog(string title, Control body, params (string Text, bool? Result)[] buttons)
    {
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)this.FindResource("VsEnvironmentBrush")!,
            ShowInTaskbar = false,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (text, result) in buttons)
        {
            var b = new Button { Content = text, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => dialog.Close(result);
            row.Children.Add(b);
        }
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 16, Children = { body, row } };
        return dialog;
    }

    // ---- Solution Explorer ---------------------------------------------------------------------------------

    void OnExplorerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is ExplorerNode node && Vm is { } vm)
        {
            if (node.Kind is NodeKind.File)
                vm.OpenNode(node);
        }
    }

    void OnExplorerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ExplorerTree.SelectedItem is ExplorerNode node && Vm is { } vm)
        {
            if (node.Kind == NodeKind.File)
                vm.OpenNode(node);
            else
                node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
        }
    }

    CancellationTokenSource? _searchCts;

    async void OnExplorerSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var text = ExplorerSearch.Text ?? "";
        _searchCts?.Cancel();
        if (text.Length < 2 || Vm is not { } vm)
        {
            ExplorerSearchResults.IsVisible = false;
            ExplorerTree.IsVisible = true;
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
            var results = await Task.Run(() => vm.SearchFiles(text).Take(500).ToList(), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            ExplorerSearchResults.ItemsSource = results;
            ExplorerSearchResults.IsVisible = true;
            ExplorerTree.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }

    void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ExplorerSearchResults.SelectedItem is ExplorerNode node)
            Vm?.OpenNode(node);
    }

    void OnSyncWithActiveDocument(object? sender, RoutedEventArgs e)
    {
        ExplorerSearch.Text = "";
        if (Vm?.SyncWithActiveDocument() is { } node)
            Dispatcher.UIThread.Post(() => ExplorerTree.SelectedItem = node, DispatcherPriority.Loaded);
    }

    // ---- Tool windows ---------------------------------------------------------------------------------------

    void OnErrorDoubleTapped(object? sender, TappedEventArgs e) =>
        Vm?.NavigateToErrorCommand.Execute(ErrorListBox.SelectedItem as ErrorItem);

    void OnFindResultDoubleTapped(object? sender, TappedEventArgs e) =>
        Vm?.NavigateToFindResultCommand.Execute(FindResultsBox.SelectedItem as FindResultItem);

    void ScrollOutputToEnd()
    {
        if (OutputEditor.Document is { } doc && Vm?.SelectedOutput.Document == doc)
            OutputEditor.ScrollToEnd();
    }

    void OnClearOutput(object? sender, RoutedEventArgs e) => Vm?.SelectedOutput.Clear();

    void OnShowSolutionExplorer(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            vm.RightTabIndex = 0;
        ExplorerTree.Focus();
    }

    void OnShowErrorList(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            vm.BottomTabIndex = 0;
    }

    void OnShowOutput(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            vm.BottomTabIndex = 1;
    }

    // ---- Edit menu -> active editor ------------------------------------------------------------------------

    void OnUndo(object? sender, RoutedEventArgs e) => ActiveHost?.Editor.Undo();

    void OnRedo(object? sender, RoutedEventArgs e) => ActiveHost?.Editor.Redo();

    void OnFind(object? sender, RoutedEventArgs e) => ActiveHost?.Editor.SearchPanel.Open();

    async void OnFindInFiles(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.Solution is null)
            return;
        var initial = ActiveHost?.Editor.SelectedText ?? "";
        if (await PromptAsync("Find in Files", "Find what:", initial) is { Length: > 0 } text)
            await vm.FindInFilesAsync(text);
    }

    void OnFormatDocument(object? sender, RoutedEventArgs e) => _ = ActiveHost?.FormatDocumentAsync();

    void OnGoToDefinition(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
            _ = h.GoToAsync(h.Editor.CaretOffset, implementation: false);
    }

    void OnGoToImplementation(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
            _ = h.GoToAsync(h.Editor.CaretOffset, implementation: true);
    }

    void OnFindReferences(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
            _ = h.FindReferencesAsync(h.Editor.CaretOffset);
    }

    void OnRename(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
        {
            h.Editor.TextArea.Focus();
            h.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.F2 });
        }
    }

    void OnExit(object? sender, RoutedEventArgs e) => Close();

    // ---- Zoom -------------------------------------------------------------------------------------------------

    void OnZoomIn(object? sender, RoutedEventArgs e) => ActiveHost?.ZoomIn();

    void OnZoomOut(object? sender, RoutedEventArgs e) => ActiveHost?.ZoomOut();

    void OnZoomReset(object? sender, RoutedEventArgs e) => ActiveHost?.SetZoom(1.0);

    void OnZoomButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DocumentViewModel doc } button)
            return;
        var menu = new MenuFlyout();
        foreach (var level in DocumentViewModel.ZoomLevels)
        {
            var item = new MenuItem { Header = $"{level} %" };
            item.Click += (_, _) => doc.Host.SetZoom(level / 100.0);
            menu.Items.Add(item);
        }
        menu.ShowAt(button);
    }

    // ---- Debug menu ---------------------------------------------------------------------------------------

    void OnToggleBreakpoint(object? sender, RoutedEventArgs e)
    {
        if (ActiveHost is { } h)
            h.ToggleBreakpoint(h.Editor.TextArea.Caret.Line);
    }

    void OnShowLocals(object? sender, RoutedEventArgs e) => ShowBottomTab(MainViewModel.LocalsTab);

    void OnShowTestExplorer(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            vm.RightTabIndex = 1;
    }

    void OnTestDoubleTapped(object? sender, TappedEventArgs e) => Vm?.NavigateToTest(TestTree.SelectedItem as TestNode);

    void OnShowWatch(object? sender, RoutedEventArgs e) => ShowBottomTab(MainViewModel.WatchTab);

    void OnShowCallStack(object? sender, RoutedEventArgs e) => ShowBottomTab(MainViewModel.CallStackTab);

    void ShowBottomTab(int index)
    {
        if (Vm is { } vm)
            vm.BottomTabIndex = index;
    }

    void OnWatchInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm)
            return;
        vm.AddWatchCommand.Execute(WatchInput.Text);
        WatchInput.Text = "";
        e.Handled = true;
    }

    async void OnShortcuts(object? sender, RoutedEventArgs e)
    {
        const string text = """
            Ctrl+Shift+O      Open solution
            Ctrl+S / Ctrl+Shift+S   Save / Save all
            Ctrl+Shift+B      Build solution
            F5 / Ctrl+F5      Start or Continue / Start without debugging
            Shift+F5          Stop
            Ctrl+Shift+F5     Restart
            F9                Toggle breakpoint (or click the left margin)
            F10 / F11         Step over / Step into
            Shift+F11         Step out
            Ctrl+Alt+Break    Break all
            Ctrl+. / Alt+Enter Quick Actions and Refactorings
            Ctrl+R, A         Run all tests
            Ctrl+R, L         Repeat last test run
            Ctrl+R, Ctrl+R    Rename
            Ctrl+E, T         Test Explorer
            Ctrl+Wheel        Zoom the editor (also Ctrl+Shift+. / Ctrl+Shift+, ; Ctrl+0 resets)
            Ctrl+`            Terminal
            Ctrl+Space        IntelliSense completion
            Ctrl+Shift+Space  Parameter info
            F12 / Ctrl+Click  Go to definition
            Ctrl+F12          Go to implementation
            Shift+F12         Find all references
            F2                Rename
            Ctrl+K, Ctrl+D    Format document
            Ctrl+K, Ctrl+C/U  Comment / uncomment lines
            Ctrl+F            Find in file
            Ctrl+Shift+F      Find in files
            Ctrl+;            Search Solution Explorer
            Ctrl+Tab          Next document
            Ctrl+F4           Close document
            """;
        var body = new TextBlock { Text = text, FontFamily = (FontFamily)this.FindResource("VsCodeFont")! };
        await CreateDialog("Keyboard Shortcuts", body, ("OK", true)).ShowDialog<bool?>(this);
    }

    async void OnAbout(object? sender, RoutedEventArgs e)
    {
        var body = new TextBlock
        {
            Text = "VSL — Visual Studio for Linux\n\nNative Avalonia IDE for C#, ASP.NET Core and Blazor.\nIntelliSense: Roslyn language server (MIT) with Razor cohosting.\nBuild/Run: .NET SDK.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
        await CreateDialog("About VSL", body, ("OK", true)).ShowDialog<bool?>(this);
    }
}

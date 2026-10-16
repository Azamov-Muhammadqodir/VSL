using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    bool _outputScrollPending;
    Key? _chord;

    public MainWindow()
    {
        InitializeComponent();
        InitializeDocking();
        ExplorerTree.AddHandler(DoubleTappedEvent, OnExplorerDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Dialogs = this;
                vm.ToolWindowRequested += OnToolWindowRequested;
                ApplySavedDockLayout(vm);
                vm.BreakModeEntered += () =>
                {
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;
                    Activate();
                };
                foreach (var channel in vm.OutputChannels)
                    channel.Appended += ScrollOutputToEnd;
                vm.ExplorerRenameRequested += BeginExplorerRename;
                vm.ExplorerSelectRequested += SelectExplorerNode;
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
        // Following the output keeps its last line at the bottom; scrolling below the document would push it out of view.
        OutputEditor.Options.AllowScrollBelowDocument = false;
        OutputEditor.TextArea.TextView.LineTransformers.Add(new Editor.OutputColorizer(() => Vm?.SelectedOutput));
        // URLs stay Ctrl+clickable, in VSL's link color instead of AvaloniaEdit's default pure blue (unreadable on dark).
        OutputEditor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.Parse("#3794FF"));
        OutputEditor.AttachedToVisualTree += (_, _) =>
        {
            if (!_outputScrollPending)
                return;
            _outputScrollPending = false;
            Dispatcher.UIThread.Post(ScrollOutputToEnd, DispatcherPriority.Background);
        };
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
        SaveDockLayout(vm);
        vm.SaveSession();
        if (await vm.CloseSolutionCoreAsync())
        {
            await vm.ShutdownTerminalAsync();
            await vm.ShutdownClaudeAsync();
            await vm.ShutdownBuildServersAsync();
            _closingConfirmed = true;
            Close();
        }
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
            return;
        // Ctrl+Esc opens Claude Code, as in the VS Code extension.
        if (ctrl && e.Key == Key.Escape && Vm is { } claudeVm)
        {
            claudeVm.ShowClaude();
            e.Handled = true;
            return;
        }
        // Ctrl+` opens the terminal (VS's View.Terminal).
        if (ctrl && e.Key == Key.OemTilde && Vm is { } terminalVm)
        {
            terminalVm.ShowTerminalCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Project > Add New Item (Ctrl+Shift+A) and Add Existing Item (Shift+Alt+A): into Solution Explorer's
        // selection when it has focus, else next to the active document.
        if (e.Key == Key.A && Vm is { HasSolution: true } addVm
            && (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) || e.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Alt)))
        {
            var target = ExplorerTree.IsKeyboardFocusWithin ? ExplorerTree.SelectedItem as ExplorerNode : null;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                _ = addVm.AddExistingItemsAsync(target);
            else
                _ = addVm.AddNewItemAsync(target);
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
                case (Key.R, Key.A) when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                    chordVm.DebugAllTestsCommand.Execute(null);
                    break;
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
                    chordVm.ShowToolWindow(ToolWindows.TestExplorer);
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
        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.N)
        {
            _ = ShowStartWindowAsync(StartPage.NewProject);
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

    // ---- Outlining (Edit > Outlining) ------------------------------------------------------------------

    void OnToggleOutlining(object? sender, RoutedEventArgs e) => ActiveHost?.ToggleOutliningExpansion();

    void OnToggleAllOutlining(object? sender, RoutedEventArgs e) => ActiveHost?.ToggleAllOutlining();

    void OnCollapseToDefinitions(object? sender, RoutedEventArgs e) => ActiveHost?.CollapseToDefinitions();

    void OnExpandAllOutlining(object? sender, RoutedEventArgs e) => ActiveHost?.ExpandAllOutlining();

    // ---- Start window --------------------------------------------------------------------------------------

    void OnStartWindow(object? sender, RoutedEventArgs e) => _ = ShowStartWindowAsync(StartPage.Home);

    void OnNewProject(object? sender, RoutedEventArgs e) => _ = ShowStartWindowAsync(StartPage.NewProject);

    void OnCloneRepository(object? sender, RoutedEventArgs e) => _ = ShowStartWindowAsync(StartPage.Clone);

    /// <summary>Opens the start window over the IDE; whatever it opens, creates or clones replaces the current solution.</summary>
    public async Task ShowStartWindowAsync(StartPage page)
    {
        if (Vm is not { } vm)
            return;
        var startVm = new StartViewModel(vm.Settings);
        var start = new StartWindow { DataContext = startVm };
        if (page == StartPage.NewProject)
            startVm.ShowNewProjectCommand.Execute(null);
        else
            startVm.Page = page;
        var chosen = await start.ShowDialog<string?>(this);
        if (chosen is not null)
            await vm.OpenSolutionAsync(chosen);
        BuildRecentMenu();
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
                new FilePickerFileType("All Project Files") { Patterns = ["*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj"] },
                new FilePickerFileType("Solution Files") { Patterns = ["*.sln", "*.slnx"] },
                new FilePickerFileType("Project Files") { Patterns = ["*.csproj", "*.fsproj", "*.vbproj"] },
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

    public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) =>
        new AddItemWindow { Title = title, DataContext = new AddItemViewModel(folder, initial) }.ShowDialog<NewItemChoice?>(this);

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
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
                    vm.ShowToolWindow(ToolWindows.SolutionExplorer);
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

    /// <summary>
    /// Double-click opens a file, also one with nested files under it (X.razor with X.razor.cs). The tree item's
    /// header handles a double-click first by toggling expansion (and marks it handled), so this listens to
    /// handled double-clicks too, opens the file and undoes that toggle: expanding a file is the chevron's job.
    /// Folders and projects still expand on double-click, as in VS.
    /// </summary>
    void OnExplorerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
            return;
        var item = source.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is not ExplorerNode { Kind: NodeKind.File } node)
            return;
        if (e.Handled && item.ItemCount > 0)
            node.IsExpanded = !node.IsExpanded;
        vm.OpenNode(node);
        e.Handled = true;
    }

    /// <summary>Right-click in Solution Explorer: VS's context menu for solutions, projects, folders and files.</summary>
    void OnExplorerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: ExplorerNode node } anchor || Vm is null)
            return;
        e.Handled = true;
        ExplorerTree.SelectedItem = node;
        var items = ExplorerMenuItems(node);
        if (items.Count > 0)
            new ContextMenu { ItemsSource = items }.Open(anchor);
    }

    /// <summary>The context menu for a Solution Explorer node (separators and menu items, submenus included).</summary>
    internal List<object> ExplorerMenuItems(ExplorerNode node)
    {
        if (Vm is not { } vm)
            return [];
        MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
            item.Click += (_, _) => action();
            return item;
        }
        var path = node.Path;
        var folder = node.Kind switch
        {
            NodeKind.Solution or NodeKind.Project => path is null ? null : Path.GetDirectoryName(path),
            NodeKind.Folder => path,
            NodeKind.File => path is null ? null : Path.GetDirectoryName(path),
            _ => null,
        };
        var items = new List<object>();
        MenuItem AddMenu()
        {
            var add = new MenuItem { Header = "Add" };
            add.Items.Add(Item("New Item...", () => _ = vm.AddNewItemAsync(node), "Ctrl+Shift+A"));
            add.Items.Add(Item("Existing Item...", () => _ = vm.AddExistingItemsAsync(node), "Shift+Alt+A"));
            add.Items.Add(Item("New Folder", () => vm.AddNewFolder(node)));
            add.Items.Add(new Separator());
            add.Items.Add(Item("Class...", () => _ = vm.AddNewItemAsync(node, ItemTemplates.Class)));
            add.Items.Add(Item("Razor Component...", () => _ = vm.AddNewItemAsync(node, ItemTemplates.RazorComponent)));
            return add;
        }
        switch (node.Kind)
        {
            case NodeKind.Solution:
                items.Add(Item("Build Solution", () => vm.BuildSolutionCommand.Execute(null)));
                items.Add(Item("Rebuild Solution", () => vm.RebuildSolutionCommand.Execute(null)));
                items.Add(Item("Clean Solution", () => vm.CleanSolutionCommand.Execute(null)));
                items.Add(new Separator());
                items.Add(Item("Manage NuGet Packages for Solution...", () => vm.ManageNuGetPackagesCommand.Execute(null)));
                break;
            case NodeKind.Project when node.Project is { } project:
                items.Add(Item("Build", () => vm.BuildProjectCommand.Execute(project)));
                items.Add(Item("Rebuild", () => vm.RebuildProjectCommand.Execute(project)));
                items.Add(Item("Clean", () => vm.CleanProjectCommand.Execute(project)));
                items.Add(new Separator());
                items.Add(AddMenu());
                items.Add(Item("Manage NuGet Packages...", () => vm.ManageNuGetPackagesCommand.Execute(project)));
                items.Add(Item("Set as Startup Project", () => vm.SetStartupProjectCommand.Execute(project), enabled: project.IsRunnable));
                items.Add(new Separator());
                items.Add(Item("Edit Project File", () => vm.EditProjectFile(project)));
                break;
            case NodeKind.Dependencies or NodeKind.DependencyGroup or NodeKind.Package:
                var owner = vm.Solution?.Projects.FirstOrDefault(p => vm.ExplorerRoots.FirstOrDefault()?.Children
                    .Any(c => c.Project == p && Contains(c, node)) == true);
                items.Add(Item("Manage NuGet Packages...", () => vm.ManageNuGetPackagesCommand.Execute(owner), enabled: owner is not null));
                break;
            case NodeKind.Folder when path is not null:
                items.Add(AddMenu());
                items.Add(new Separator());
                items.Add(Item("Rename", () => vm.BeginRename(node), "F2"));
                items.Add(Item("Delete", () => _ = vm.DeleteAsync(node), "Delete"));
                break;
            case NodeKind.File when path is not null:
                items.Add(Item("Open", () => vm.OpenNode(node)));
                items.Add(Item("Compare with Unmodified", () =>
                {
                    if (vm.OpenDocument(path) is { } doc)
                        _ = vm.CompareWithUnmodifiedAsync(doc);
                }, enabled: vm.HasGit));
                items.Add(new Separator());
                items.Add(Item("Rename", () => vm.BeginRename(node), "F2"));
                items.Add(Item("Delete", () => _ = vm.DeleteAsync(node), "Delete"));
                break;
        }
        if (folder is not null)
        {
            if (items.Count > 0)
                items.Add(new Separator());
            if (node.Kind != NodeKind.File)
                items.Add(Item("Open in Terminal", () => _ = vm.OpenTerminalAtAsync(folder)));
            items.Add(Item(node.Kind == NodeKind.File ? "Open Containing Folder" : "Open Folder in File Explorer", () => OpenFolder(folder)));
        }
        if (path is not null)
            items.Add(Item("Copy Full Path", () => _ = Clipboard?.SetTextAsync(path)));
        return items;

        static bool Contains(ExplorerNode parent, ExplorerNode target) =>
            parent.Children.Any(c => c == target || Contains(c, target));
    }

    /// <summary>Renames a node in place, as VS does: a text box over its name with the base name selected.</summary>
    void BeginExplorerRename(ExplorerNode node)
    {
        node.EditName = node.Name;
        node.IsRenaming = true;
        SelectExplorerNode(node);
        Dispatcher.UIThread.Post(() =>
        {
            var box = (ExplorerTree.TreeContainerFromItem(node) as Control)?.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(b => b.Classes.Contains("rename") && b.DataContext == node);
            if (box is null)
            {
                node.IsRenaming = false;
                return;
            }
            box.Focus();
            box.SelectionStart = 0;
            box.SelectionEnd = node.Kind == NodeKind.File && Path.GetFileNameWithoutExtension(node.Name) is { Length: > 0 } stem ? stem.Length : node.Name.Length;
        }, DispatcherPriority.Loaded);
    }

    void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ExplorerNode node })
            return;
        if (e.Key == Key.Enter)
        {
            CommitRename(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            node.IsRenaming = false;
            ExplorerTree.Focus();
            e.Handled = true;
        }
    }

    void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ExplorerNode node })
            CommitRename(node);
    }

    void CommitRename(ExplorerNode node)
    {
        if (!node.IsRenaming)
            return;
        node.IsRenaming = false;
        if (Vm is { } vm && node.EditName != node.Name)
            vm.Rename(node, node.EditName);
        ExplorerTree.Focus();
    }

    void SelectExplorerNode(ExplorerNode node)
    {
        ExplorerTree.SelectedItem = node;
        Dispatcher.UIThread.Post(() => (ExplorerTree.TreeContainerFromItem(node) as Control)?.BringIntoView(), DispatcherPriority.Loaded);
    }

    void OnExplorerKeyDown(object? sender, KeyEventArgs e)
    {
        // Keys typed into the rename box are the box's (Delete removes a character, not the file).
        if (e.Source is TextBox)
            return;
        if (e.Key == Key.F2 && ExplorerTree.SelectedItem is ExplorerNode renamed && Vm is { } renameVm)
        {
            renameVm.BeginRename(renamed);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && ExplorerTree.SelectedItem is ExplorerNode deleted && Vm is { } deleteVm)
        {
            _ = deleteVm.DeleteAsync(deleted);
            e.Handled = true;
            return;
        }
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
        if (OutputEditor.Document is not { } doc || Vm?.SelectedOutput.Document != doc)
            return;
        // Behind another tab the Output window has no viewport: scrolling then would leave it past the last line,
        // showing nothing once the tab is opened, so it scrolls when it is shown again.
        if (TopLevel.GetTopLevel(OutputEditor) is null)
            _outputScrollPending = true;
        else
            OutputEditor.ScrollToEnd();
    }

    void OnClearOutput(object? sender, RoutedEventArgs e) => Vm?.SelectedOutput.Clear();

    void OnShowSolutionExplorer(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowToolWindow(ToolWindows.SolutionExplorer);
        ExplorerTree.Focus();
    }

    void OnShowErrorList(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowToolWindow(ToolWindows.ErrorList);
    }

    void OnShowOutput(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowToolWindow(ToolWindows.Output);
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

    void OnResetWindowLayout(object? sender, RoutedEventArgs e) => ResetWindowLayout();

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

    void OnShowLocals(object? sender, RoutedEventArgs e) => Vm?.ShowToolWindow(ToolWindows.Locals);

    void OnShowTestExplorer(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowToolWindow(ToolWindows.TestExplorer);
    }

    void OnTestDoubleTapped(object? sender, TappedEventArgs e) => Vm?.NavigateToTest(TestTree.SelectedItem as TestNode);

    /// <summary>Right-click in Test Explorer: Run, Debug, Go To Test.</summary>
    void OnTestContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: TestNode node } anchor || Vm is not { } vm)
            return;
        e.Handled = true;
        vm.SelectedTest = node;
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
        new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("Run", () => vm.RunSelectedTestsCommand.Execute(null), vm.RunSelectedTestsCommand.CanExecute(null)),
                Item("Debug", () => vm.DebugSelectedTestsCommand.Execute(null), vm.DebugSelectedTestsCommand.CanExecute(null)),
                new Separator(),
                Item("Go To Test", () => vm.NavigateToTest(node), node.SourcePath is not null),
            },
        }.Open(anchor);
    }

    void OnShowWatch(object? sender, RoutedEventArgs e) => Vm?.ShowToolWindow(ToolWindows.Watch);

    void OnShowCallStack(object? sender, RoutedEventArgs e) => Vm?.ShowToolWindow(ToolWindows.CallStack);


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
            Ctrl+R, Ctrl+A    Debug all tests
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

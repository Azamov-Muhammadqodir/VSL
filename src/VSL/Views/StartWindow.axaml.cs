using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VSL.ViewModels;

namespace VSL.Views;

/// <summary>Visual Studio's start window: shown at launch and from File > Start Window / New Project / Clone Repository.</summary>
public partial class StartWindow : Window
{
    bool _finished;

    public StartWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (Vm is not { } vm)
                return;
            vm.SolutionChosen += path => Finish(path);
            vm.ContinueRequested += () => Finish(null);
            vm.PickSolution = PickSolutionAsync;
            vm.PickFolder = PickFolderAsync;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        };
        RecentList.AddHandler(ContextRequestedEvent, OnRecentContextRequested);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => FocusForPage();
    }

    StartViewModel? Vm => DataContext as StartViewModel;

    /// <summary>Raised once: the solution or project to open, or null for "Continue without code" or closing the window.</summary>
    public event Action<string?>? Finished;

    void Finish(string? path)
    {
        if (_finished)
            return;
        _finished = true;
        Finished?.Invoke(path);
        Close(path);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // Closing the start window at launch opens the IDE without code, as in VS. This runs before the window
        // is gone, so the IDE window exists by then and the app does not shut down for lack of windows.
        if (!e.Cancel && !_finished)
        {
            _finished = true;
            Finished?.Invoke(null);
        }
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartViewModel.Page))
            Dispatcher.UIThread.Post(FocusForPage, DispatcherPriority.Background);
    }

    void FocusForPage()
    {
        switch (Vm?.Page)
        {
            case StartPage.Home:
                RecentSearchBox.Focus();
                break;
            case StartPage.NewProject:
                TemplateSearchBox.Focus();
                break;
            case StartPage.Configure:
                ProjectNameBox.Focus();
                ProjectNameBox.SelectAll();
                break;
            case StartPage.Clone:
                CloneUrlBox.Focus();
                break;
        }
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (e.Key == Key.Escape && !vm.IsHome && !vm.IsBusy)
        {
            vm.BackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Alt)
        {
            (vm.IsNewProject ? TemplateSearchBox : RecentSearchBox).Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && vm.IsNewProject && vm.NextCommand.CanExecute(null))
        {
            vm.NextCommand.Execute(null);
            e.Handled = true;
        }
    }

    void OnTemplateDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { } vm && vm.NextCommand.CanExecute(null))
            vm.NextCommand.Execute(null);
    }

    void OnRecentContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var element = e.Source as StyledElement;
        while (element is not null && element.DataContext is not RecentItem)
            element = element.Parent;
        if (element is not Control { DataContext: RecentItem item } target)
            return;
        e.Handled = true;
        MenuItem Item(string header, Action action)
        {
            var menuItem = new MenuItem { Header = header };
            menuItem.Click += (_, _) => action();
            return menuItem;
        }
        new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("Open", () => vm.OpenRecentCommand.Execute(item)),
                Item(item.IsPinned ? "Unpin" : "Pin", () => vm.TogglePinCommand.Execute(item)),
                Item("Copy Path", () => _ = Clipboard is { } clipboard ? clipboard.SetTextAsync(item.Path) : Task.CompletedTask),
                new Separator(),
                Item("Remove From List", () => vm.RemoveRecentCommand.Execute(item)),
            },
        }.Open(target);
    }

    async Task<string?> PickSolutionAsync()
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

    async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}

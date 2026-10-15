using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Terminal;

namespace VSL.ViewModels;

/// <summary>The integrated terminal tool window (View > Terminal, Ctrl+`).</summary>
public partial class MainViewModel
{
    public const int TerminalTab = 6;

    TerminalSession? _terminal;

    /// <summary>Created on first use; the shell itself starts when the terminal is first laid out.</summary>
    public TerminalSession Terminal => _terminal ??= CreateTerminal();

    [ObservableProperty]
    public partial string TerminalTitle { get; set; } = "Terminal";

    TerminalSession CreateTerminal()
    {
        var terminal = new TerminalSession();
        if (Solution is not null)
            terminal.WorkingDirectory = Solution.Directory;
        terminal.StateChanged += () => TerminalTitle = terminal.Title;
        return terminal;
    }

    [RelayCommand]
    void ShowTerminal()
    {
        BottomTabIndex = TerminalTab;
        Dispatcher.UIThread.Post(() => Terminal.Control.Focus(), DispatcherPriority.Loaded);
    }

    [RelayCommand]
    async Task NewTerminal()
    {
        Terminal.WorkingDirectory = Solution?.Directory ?? Terminal.WorkingDirectory;
        ShowTerminal();
        await Terminal.RestartAsync();
    }

    [RelayCommand]
    Task KillTerminal() => _terminal?.KillAsync() ?? Task.CompletedTask;

    public async Task ShutdownTerminalAsync()
    {
        if (_terminal is { } terminal)
            await terminal.DisposeAsync();
    }
}

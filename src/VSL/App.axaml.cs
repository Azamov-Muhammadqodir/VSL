using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VSL.ViewModels;
using VSL.Views;

namespace VSL;

public partial class App : Application
{
    public static string[] StartupArgs { get; set; } = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            var solution = StartupArgs.FirstOrDefault(a => a.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                ?? vm.Settings.LastSolution;
            if (solution is not null && File.Exists(solution))
                _ = vm.OpenSolutionAsync(Path.GetFullPath(solution));
        }
        base.OnFrameworkInitializationCompleted();
    }
}

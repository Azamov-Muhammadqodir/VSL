using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VSL.ViewModels;
using VSL.Views;
using VSL.Workspace;

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
            var solution = StartupArgs.FirstOrDefault(SolutionLoader.CanOpen);
            if (solution is not null && File.Exists(solution))
            {
                // `vsl App.sln` (or a project) opens it directly; otherwise the start window comes first, as in VS.
                desktop.MainWindow = new MainWindow { DataContext = vm };
                _ = vm.OpenSolutionAsync(Path.GetFullPath(solution));
            }
            else
            {
                var start = new StartWindow { DataContext = new StartViewModel(vm.Settings) };
                start.Finished += path =>
                {
                    var main = new MainWindow { DataContext = vm };
                    desktop.MainWindow = main;
                    main.Show();
                    if (path is not null)
                        _ = vm.OpenSolutionAsync(path);
                };
                desktop.MainWindow = start;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

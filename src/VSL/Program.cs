using Avalonia;

namespace VSL;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.StartupArgs = args;
        // Child tools that look up `dotnet` on PATH (vstest.console, MSBuild nodes) must find the same SDK VSL uses,
        // not a distro package that happens to be first on a desktop session's PATH.
        Build.DotnetCli.ApplyToCurrentProcess();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = "VSL" })
        .WithInterFont()
        .LogToTrace();
}

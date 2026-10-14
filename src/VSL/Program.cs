using Avalonia;

namespace VSL;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.StartupArgs = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = "VSL" })
        .WithInterFont()
        .LogToTrace();
}

// The shared view model posts to Avalonia's UI thread and uses its timers; in Visual Studio those are WPF's.
namespace Avalonia.Threading;

public enum DispatcherPriority
{
    Background,
    Normal,
}

public sealed class DispatcherTimer : System.Windows.Threading.DispatcherTimer
{
    public DispatcherTimer()
        : base(System.Windows.Threading.DispatcherPriority.Normal)
    {
    }

    public DispatcherTimer(TimeSpan interval, DispatcherPriority priority, EventHandler callback)
        : base(interval, Map(priority), callback, System.Windows.Threading.Dispatcher.CurrentDispatcher)
    {
    }

    internal static System.Windows.Threading.DispatcherPriority Map(DispatcherPriority priority) => priority switch
    {
        DispatcherPriority.Background => System.Windows.Threading.DispatcherPriority.Background,
        _ => System.Windows.Threading.DispatcherPriority.Normal,
    };
}

public sealed class Dispatcher
{
    readonly System.Windows.Threading.Dispatcher _dispatcher;

    Dispatcher(System.Windows.Threading.Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Visual Studio's main thread (the dispatcher of its application).</summary>
    public static Dispatcher UIThread { get; } = new(System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);

    public void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal) =>
        _dispatcher.BeginInvoke(action, DispatcherTimer.Map(priority));

    public bool CheckAccess() => _dispatcher.CheckAccess();
}

using System.Collections;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using Exclr8.Terminal;
using Exclr8.Terminal.Pty;
using Exclr8.Terminal.Render;
using Porta.Pty;

namespace VSL.Terminal;

/// <summary>
/// The integrated terminal (View > Terminal): the user's shell in a real pseudo-terminal, started in the solution
/// folder on first show. Rendering and VT parsing come from Exclr8.Terminal, the PTY from Porta.Pty (both MIT).
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    PtyTerminalAdapter _adapter;
    (int Cols, int Rows)? _size;
    bool _starting;
    bool _exited;

    public TerminalSession()
    {
        Control = new TerminalControl
        {
            FontFamily = "avares://VSL/Assets/Fonts#Cascadia Mono, DejaVu Sans Mono, monospace",
            FontSize = Editor.EditorHost.BaseFontSize,
            ScrollbackLimit = 10_000,
            ColorScheme = VsDarkTheme(),
        };
        _adapter = CreateAdapter();
        Control.Resized += (_, size) =>
        {
            _size = size;
            if (!_adapter.IsAlive && !_starting && !_exited)
                _ = StartAsync(size.Item1, size.Item2);
        };
        Control.TitleChanged += (_, title) => Dispatcher.UIThread.Post(() =>
        {
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title;
            StateChanged?.Invoke();
        });
        // After the shell exits, Enter starts a new one (like VS / Windows Terminal).
        Control.Input += (_, data) =>
        {
            if (_exited && data.Span.IndexOf((byte)'\r') >= 0)
                Dispatcher.UIThread.Post(() => _ = RestartAsync());
        };
    }

    public TerminalControl Control { get; }

    /// <summary>Folder a new shell starts in; the view model points it at the open solution.</summary>
    public string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Shell => Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh && File.Exists(sh) ? sh : "/bin/bash";

    static string DefaultTitle => Path.GetFileName(Shell);

    public string Title { get; private set; } = DefaultTitle;

    public bool IsAlive => _adapter.IsAlive;

    public int? ProcessId => _adapter.Pid;

    /// <summary>Raised on the UI thread when the title or running state changes.</summary>
    public event Action? StateChanged;

    PtyTerminalAdapter CreateAdapter()
    {
        var adapter = new PtyTerminalAdapter(Control);
        adapter.ProcessExited += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (adapter != _adapter)
                return;
            _exited = true;
            Control.Write(Encoding.UTF8.GetBytes($"\r\n\x1b[90m[Process exited with code {e.ExitCode}. Press Enter to start a new terminal.]\x1b[0m\r\n"));
            Title = DefaultTitle;
            StateChanged?.Invoke();
        });
        return adapter;
    }

    async Task StartAsync(int cols, int rows)
    {
        _starting = true;
        try
        {
            var env = new Dictionary<string, string>();
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                env[(string)e.Key] = e.Value?.ToString() ?? "";
            env["TERM"] = "xterm-256color";
            env["COLORTERM"] = "truecolor";
            env["TERM_PROGRAM"] = "VSL";
            await _adapter.StartAsync(new PtyOptions
            {
                Name = "xterm-256color",
                // Porta.Pty creates the pty with zeroed termios (no ONLCR, ICRNL, ECHO...), which makes output
                // "staircase" because LF is not turned into CR LF. Reset the line discipline, then become the shell.
                App = "/bin/sh",
                Cwd = Directory.Exists(WorkingDirectory) ? WorkingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Cols = cols,
                Rows = rows,
                CommandLine = ["-c", "stty sane 2>/dev/null; exec \"$0\"", Shell],
                Environment = env,
            }, CancellationToken.None);
            _exited = false;
        }
        catch (Exception ex)
        {
            Control.Write(Encoding.UTF8.GetBytes($"\x1b[31mCould not start {Shell}: {ex.Message}\x1b[0m\r\n"));
            _exited = true;
        }
        finally
        {
            _starting = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Ends a session: the adapter's dispose waits for its read loop, which only returns once the pty closes,
    /// so the shell's process tree is killed first (as VS does when a terminal is closed).
    /// </summary>
    static async Task StopAsync(PtyTerminalAdapter adapter)
    {
        if (adapter.Pid is { } pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
        try
        {
            await adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            // Never let a stuck pty freeze the IDE.
        }
    }

    /// <summary>Kills the current shell (if any) and starts a fresh one in <see cref="WorkingDirectory"/>.</summary>
    public async Task RestartAsync()
    {
        var old = _adapter;
        _adapter = CreateAdapter();
        await StopAsync(old);
        _exited = false;
        Control.PrepareForNewSession();
        if (_size is { } size)
            await StartAsync(size.Cols, size.Rows);
    }

    public async Task KillAsync()
    {
        if (!_adapter.IsAlive)
            return;
        var old = _adapter;
        _adapter = CreateAdapter();
        await StopAsync(old);
        _exited = true;
        Control.Write(Encoding.UTF8.GetBytes("\r\n\x1b[90m[Terminal killed. Press Enter to start a new terminal.]\x1b[0m\r\n"));
        StateChanged?.Invoke();
    }

    /// <summary>The visible screen and scrollback as plain text (used by tests).</summary>
    internal string ReadText()
    {
        Control.Buffer.SelectAll();
        var text = Control.Buffer.GetSelectedText();
        Control.Buffer.ClearSelection();
        return text;
    }

    /// <summary>Visual Studio's dark terminal palette.</summary>
    static TerminalTheme VsDarkTheme() => new()
    {
        Background = Color.Parse("#1E1E1E"),
        Foreground = Color.Parse("#CCCCCC"),
        Cursor = Color.Parse("#AEAFAD"),
        AnsiColors =
        [
            Color.Parse("#000000"), Color.Parse("#CD3131"), Color.Parse("#0DBC79"), Color.Parse("#E5E510"),
            Color.Parse("#2472C8"), Color.Parse("#BC3FBC"), Color.Parse("#11A8CD"), Color.Parse("#E5E5E5"),
            Color.Parse("#666666"), Color.Parse("#F14C4C"), Color.Parse("#23D18B"), Color.Parse("#F5F543"),
            Color.Parse("#3B8EEA"), Color.Parse("#D670D6"), Color.Parse("#29B8DB"), Color.Parse("#FFFFFF"),
        ],
    };

    public async ValueTask DisposeAsync() => await StopAsync(_adapter);
}

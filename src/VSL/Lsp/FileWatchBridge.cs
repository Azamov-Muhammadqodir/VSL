using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VSL.Lsp;

/// <summary>Converts LSP glob patterns (<c>**/*.{cs,razor}</c>) to regular expressions.</summary>
public static class Glob
{
    public static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        var inGroup = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    inGroup++;
                    sb.Append("(?:");
                    break;
                case '}' when inGroup > 0:
                    inGroup--;
                    sb.Append(')');
                    break;
                case ',' when inGroup > 0:
                    sb.Append('|');
                    break;
                case '[':
                    var close = glob.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }
                    var body = glob[(i + 1)..close];
                    if (body.StartsWith('!'))
                        body = "^" + body[1..];
                    sb.Append('[').Append(body.Replace(@"\", @"\\")).Append(']');
                    i = close;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}

/// <summary>
/// Implements workspace/didChangeWatchedFiles for the server. Roslyn would otherwise create one inotify
/// instance per FileSystemWatcher (hundreds per solution) and hit the default per-user limit of 128.
/// We watch the solution directory with a single recursive watcher and forward matching events.
/// Registrations outside the solution (NuGet cache, SDK packs) are immutable in practice and skipped.
/// </summary>
public sealed class FileWatchBridge : IDisposable
{
    const int Created = 1, Changed = 2, Deleted = 3;

    sealed record Watcher(string? BaseDir, Regex Pattern, int Kind);

    readonly string _root;
    readonly Func<IReadOnlyList<(string Uri, int Type)>, Task> _send;
    readonly ConcurrentDictionary<string, List<Watcher>> _registrations = new();
    readonly ConcurrentDictionary<string, int> _pending = new();
    readonly Timer _flushTimer;
    FileSystemWatcher? _fsw;

    public FileWatchBridge(string root, Func<IReadOnlyList<(string Uri, int Type)>, Task> send)
    {
        _root = Path.GetFullPath(root).TrimEnd('/');
        _send = send;
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int WatcherCount => _registrations.Values.Sum(l => l.Count);

    public void Register(string id, JsonElement registerOptions)
    {
        var list = new List<Watcher>();
        foreach (var w in registerOptions.GetProperty("watchers").EnumerateArray())
        {
            var kind = w.TryGetProperty("kind", out var k) ? k.GetInt32() : 7;
            var gp = w.GetProperty("globPattern");
            if (gp.ValueKind == JsonValueKind.String)
            {
                list.Add(new Watcher(null, Glob.ToRegex(gp.GetString()!), kind));
                continue;
            }
            var baseUri = gp.GetProperty("baseUri");
            var uri = baseUri.ValueKind == JsonValueKind.String ? baseUri.GetString()! : baseUri.GetProperty("uri").GetString()!;
            var baseDir = new Uri(uri).LocalPath.TrimEnd('/');
            if (!IsUnderRoot(baseDir) && !IsUnderRoot(baseDir + "/"))
                continue;
            list.Add(new Watcher(baseDir, Glob.ToRegex(gp.GetProperty("pattern").GetString()!), kind));
        }
        _registrations[id] = list;
        EnsureStarted();
    }

    public void Unregister(string id) => _registrations.TryRemove(id, out _);

    bool IsUnderRoot(string path) => path == _root || path.StartsWith(_root + "/", StringComparison.Ordinal);

    void EnsureStarted()
    {
        if (_fsw is not null || !Directory.Exists(_root))
            return;
        var fsw = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        fsw.Created += (_, e) => OnEvent(e.FullPath, Created);
        fsw.Changed += (_, e) => OnEvent(e.FullPath, Changed);
        fsw.Deleted += (_, e) => OnEvent(e.FullPath, Deleted);
        fsw.Renamed += (_, e) =>
        {
            OnEvent(e.OldFullPath, Deleted);
            OnEvent(e.FullPath, Created);
        };
        fsw.EnableRaisingEvents = true;
        _fsw = fsw;
    }

    void OnEvent(string path, int type)
    {
        if (path.Contains("/.git/", StringComparison.Ordinal) || !Matches(path, type))
            return;
        _pending.AddOrUpdate(path, type, (_, previous) => (previous, type) switch
        {
            (Created, Changed) => Created,
            (Created, Deleted) => Deleted,
            (Deleted, Created) => Changed,
            _ => type,
        });
        _flushTimer.Change(300, Timeout.Infinite);
    }

    internal bool Matches(string path, int type)
    {
        var kindBit = type switch { Created => 1, Changed => 2, _ => 4 };
        foreach (var list in _registrations.Values)
        {
            foreach (var w in list)
            {
                if ((w.Kind & kindBit) == 0)
                    continue;
                if (w.BaseDir is null)
                {
                    if (w.Pattern.IsMatch(path) || w.Pattern.IsMatch(Path.GetFileName(path)))
                        return true;
                }
                else if (path.StartsWith(w.BaseDir + "/", StringComparison.Ordinal) && w.Pattern.IsMatch(path[(w.BaseDir.Length + 1)..]))
                {
                    return true;
                }
            }
        }
        return false;
    }

    void Flush()
    {
        var changes = new List<(string, int)>();
        foreach (var path in _pending.Keys)
            if (_pending.TryRemove(path, out var type))
                changes.Add((new Uri(path).AbsoluteUri, type));
        if (changes.Count > 0)
            _ = _send(changes);
    }

    public void Dispose()
    {
        _fsw?.Dispose();
        _flushTimer.Dispose();
    }
}

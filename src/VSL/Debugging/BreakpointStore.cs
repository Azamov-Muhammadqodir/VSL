namespace VSL.Debugging;

/// <summary>Line breakpoints per file (1-based lines) plus what the debugger reported about each one.</summary>
public sealed class BreakpointStore
{
    readonly Dictionary<string, SortedSet<int>> _lines = new(StringComparer.Ordinal);
    readonly Dictionary<(string File, int Line), bool> _verified = [];

    /// <summary>Raised with the file whose breakpoint lines changed; the flag is true when the editor itself caused it.</summary>
    public event Action<string, bool>? Changed;

    /// <summary>Raised when the debugger reported (or forgot) whether a file's breakpoints bind. Redraw only.</summary>
    public event Action<string>? VerificationChanged;

    public IReadOnlyCollection<int> For(string file) => _lines.TryGetValue(file, out var set) ? set : [];

    public bool Has(string file, int line) => _lines.TryGetValue(file, out var set) && set.Contains(line);

    /// <summary>null = no session has reported on this breakpoint yet.</summary>
    public bool? IsVerified(string file, int line) => _verified.TryGetValue((file, line), out var v) ? v : null;

    public IReadOnlyDictionary<string, IReadOnlyList<int>> All =>
        _lines.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value.ToList());

    public int Count => _lines.Values.Sum(s => s.Count);

    public void Toggle(string file, int line)
    {
        if (!_lines.TryGetValue(file, out var set))
            _lines[file] = set = [];
        if (!set.Remove(line))
            set.Add(line);
        _verified.Remove((file, line));
        Changed?.Invoke(file, false);
    }

    /// <summary>The editor moved breakpoints because lines were inserted or deleted above them.</summary>
    public void Replace(string file, IEnumerable<int> lines)
    {
        var updated = new SortedSet<int>(lines);
        if (_lines.TryGetValue(file, out var current) && current.SetEquals(updated))
            return;
        _lines[file] = updated;
        foreach (var key in _verified.Keys.Where(k => k.File == file).ToList())
            _verified.Remove(key);
        Changed?.Invoke(file, true);
    }

    public void SetVerification(string file, IReadOnlyList<int> lines, IReadOnlyList<BreakpointResult> results)
    {
        for (var i = 0; i < lines.Count && i < results.Count; i++)
            _verified[(file, lines[i])] = results[i].Verified;
        VerificationChanged?.Invoke(file);
    }

    public void ClearVerification()
    {
        var files = _verified.Keys.Select(k => k.File).Distinct().ToList();
        _verified.Clear();
        foreach (var f in files)
            VerificationChanged?.Invoke(f);
    }

    public void Load(IReadOnlyDictionary<string, List<int>> saved)
    {
        _lines.Clear();
        _verified.Clear();
        foreach (var (file, lines) in saved)
            _lines[file] = new SortedSet<int>(lines);
    }

    public Dictionary<string, List<int>> Snapshot() =>
        _lines.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
}

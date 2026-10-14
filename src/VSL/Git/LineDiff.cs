namespace VSL.Git;

public enum LineChangeKind
{
    Added,
    Modified,
    Deleted,
}

/// <summary>A changed region in the current text: 1-based first line and line count (0 for a deletion marker).</summary>
public readonly record struct LineChange(int Line, int Count, LineChangeKind Kind);

/// <summary>Line diff between a baseline (HEAD) and the current buffer for the editor's change margin.</summary>
public static class LineDiff
{
    const int MaxMatrix = 4_000_000;

    public static IReadOnlyList<LineChange> Compute(IReadOnlyList<string> baseline, IReadOnlyList<string> current)
    {
        // Trim the common prefix and suffix; typical edits leave only a small middle to compare.
        var prefix = 0;
        while (prefix < baseline.Count && prefix < current.Count && baseline[prefix] == current[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < baseline.Count - prefix && suffix < current.Count - prefix
               && baseline[baseline.Count - 1 - suffix] == current[current.Count - 1 - suffix])
            suffix++;

        var a = baseline.Skip(prefix).Take(baseline.Count - prefix - suffix).ToArray();
        var b = current.Skip(prefix).Take(current.Count - prefix - suffix).ToArray();
        if (a.Length == 0 && b.Length == 0)
            return [];
        if ((long)a.Length * b.Length > MaxMatrix)
            return [Region(prefix, a.Length, b.Length)];

        // LCS table, then walk it to produce runs of deletions/insertions between matches.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var changes = new List<LineChange>();
        int x = 0, y = 0, removed = 0, added = 0, runStart = 0;
        void Flush()
        {
            if (removed > 0 || added > 0)
                changes.Add(Region(prefix + runStart, removed, added));
            removed = added = 0;
        }
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                Flush();
                x++;
                y++;
                runStart = y;
            }
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                if (removed == 0 && added == 0)
                    runStart = y;
                added++;
                y++;
            }
            else
            {
                if (removed == 0 && added == 0)
                    runStart = y;
                removed++;
                x++;
            }
        }
        Flush();
        return changes;
    }

    /// <summary>removed/added line counts at 0-based current line → margin region.</summary>
    static LineChange Region(int currentLine, int removed, int added) => (removed, added) switch
    {
        (0, _) => new LineChange(currentLine + 1, added, LineChangeKind.Added),
        (_, 0) => new LineChange(Math.Max(1, currentLine), 0, LineChangeKind.Deleted),
        _ => new LineChange(currentLine + 1, added, LineChangeKind.Modified),
    };

    public static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');
}

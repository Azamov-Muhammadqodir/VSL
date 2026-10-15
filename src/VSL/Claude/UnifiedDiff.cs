namespace VSL.Claude;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Gap,
}

public sealed record DiffLine(DiffLineKind Kind, string Text)
{
    public string Prefix => Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => " " };

    public bool IsAdded => Kind == DiffLineKind.Added;

    public bool IsRemoved => Kind == DiffLineKind.Removed;

    public bool IsGap => Kind == DiffLineKind.Gap;
}

/// <summary>A small line diff (LCS) for showing Claude's edits inline: changed lines with a little context.</summary>
public static class UnifiedDiff
{
    public static IReadOnlyList<DiffLine> Compute(string before, string after, int context = 2, int maxLines = 400)
    {
        var a = Split(before);
        var b = Split(after);
        // Trim the common prefix and suffix so the LCS table stays small for typical edits.
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
            start++;
        var endA = a.Length;
        var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }

        var lines = new List<DiffLine>();
        for (var i = Math.Max(0, start - context); i < start; i++)
            lines.Add(new DiffLine(DiffLineKind.Context, a[i]));
        var n = endA - start;
        var m = endB - start;
        if ((long)n * m > 4_000_000)
        {
            // Too big to align: show it as a replacement.
            lines.AddRange(a[start..endA].Select(l => new DiffLine(DiffLineKind.Removed, l)));
            lines.AddRange(b[start..endB].Select(l => new DiffLine(DiffLineKind.Added, l)));
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y])
                {
                    lines.Add(new DiffLine(DiffLineKind.Context, a[start + x]));
                    x++;
                    y++;
                }
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
                {
                    // Removals before additions, as in a unified diff.
                    lines.Add(new DiffLine(DiffLineKind.Removed, a[start + x++]));
                }
                else
                {
                    lines.Add(new DiffLine(DiffLineKind.Added, b[start + y++]));
                }
            }
        }
        for (var i = endA; i < Math.Min(a.Length, endA + context); i++)
            lines.Add(new DiffLine(DiffLineKind.Context, a[i]));
        return Collapse(lines, context, maxLines);
    }

    /// <summary>Every line added (a new file).</summary>
    public static IReadOnlyList<DiffLine> Added(string text, int maxLines = 400) =>
        Collapse(Split(text).Select(l => new DiffLine(DiffLineKind.Added, l)).ToList(), 0, maxLines);

    /// <summary>Long unchanged runs inside the hunk become a gap marker; very long diffs are cut.</summary>
    static IReadOnlyList<DiffLine> Collapse(List<DiffLine> lines, int context, int maxLines)
    {
        var result = new List<DiffLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffLineKind.Context)
            {
                var run = i;
                while (run < lines.Count && lines[run].Kind == DiffLineKind.Context)
                    run++;
                var length = run - i;
                if (i > 0 && run < lines.Count && length > context * 2 + 1)
                {
                    result.AddRange(lines.Skip(i).Take(context));
                    result.Add(new DiffLine(DiffLineKind.Gap, $"... {length - context * 2} unchanged lines"));
                    result.AddRange(lines.Skip(run - context).Take(context));
                    i = run - 1;
                    continue;
                }
            }
            result.Add(lines[i]);
        }
        if (result.Count > maxLines)
        {
            var more = result.Count - maxLines;
            result = result.Take(maxLines).ToList();
            result.Add(new DiffLine(DiffLineKind.Gap, $"... {more} more lines"));
        }
        return result;
    }

    static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}

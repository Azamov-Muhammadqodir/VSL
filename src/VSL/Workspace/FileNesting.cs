using System.Text.RegularExpressions;

namespace VSL.Workspace;

/// <summary>Visual Studio's default file-nesting rules for ASP.NET Core / Blazor projects.</summary>
public static class FileNesting
{
    static readonly Regex Designer = new(@"^(?<base>.+)\.Designer\.cs$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Groups sibling file names into (parent, nested children) pairs, preserving parents' order.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Children)> Nest(IEnumerable<string> fileNames)
    {
        var names = fileNames.ToList();
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (FindParent(name, set) is { } parent)
                direct[name] = parent;

        // Flatten chains (A.razor.cs.map -> A.razor.cs -> A.razor) so everything sits one level under the root file.
        var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (child, parent) in direct)
        {
            var root = parent;
            for (var guard = 0; guard < 8 && direct.TryGetValue(root, out var up); guard++)
                root = up;
            if (!root.Equals(child, StringComparison.OrdinalIgnoreCase))
                parentOf[child] = root;
        }

        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (child, parent) in parentOf)
        {
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = [];
            list.Add(child);
        }

        return names
            .Where(n => !parentOf.ContainsKey(n))
            .Select(n => (n, (IReadOnlyList<string>)(children.TryGetValue(n, out var c)
                ? c.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                : [])))
            .ToList();
    }

    static string? FindParent(string name, HashSet<string> siblings)
    {
        var designer = Designer.Match(name);
        if (designer.Success)
        {
            foreach (var ext in new[] { ".resx", ".cs" })
                if (siblings.Contains(designer.Groups["base"].Value + ext))
                    return designer.Groups["base"].Value + ext;
        }

        // Index.razor.cs, Index.razor.css, Page.cshtml.cs, View.axaml.cs -> strip the last extension.
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0)
        {
            var stripped = name[..lastDot];
            if (stripped.Contains('.') && siblings.Contains(stripped))
                return stripped;
        }

        // appsettings.Development.json -> appsettings.json
        var parts = name.Split('.');
        if (parts.Length >= 3)
        {
            var candidate = parts[0] + "." + parts[^1];
            if (!candidate.Equals(name, StringComparison.OrdinalIgnoreCase) && siblings.Contains(candidate))
                return candidate;
        }
        return null;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace VSL.VisualStudio.Git;

public enum GitNodeKind
{
    /// <summary>The folder the repositories share (shown when there are several).</summary>
    Root,
    Repository,
    Folder,
    File,
}

/// <summary>
/// A row of the changes tree, as Git Changes draws it: the shared folder, a repository, folders (a chain of single
/// folders shown as one, "Components\Camera"), and the changed files. The tree is shown as a flat list of the rows
/// that are visible, each indented by its depth.
/// </summary>
public sealed partial class GitTreeNode : ObservableObject
{
    public GitTreeNode(GitNodeKind kind, string name, GitRepo? repo, string path, bool isStaged, GitChange? change = null)
    {
        Kind = kind;
        Name = name;
        Repo = repo;
        Path = path;
        IsStaged = isStaged;
        Change = change;
    }

    public GitNodeKind Kind { get; }

    public string Name { get; }

    public GitRepo? Repo { get; }

    /// <summary>The path inside the repository ("" for the repository itself and the root).</summary>
    public string Path { get; }

    public bool IsStaged { get; }

    /// <summary>The change of a file row.</summary>
    public GitChange? Change { get; }

    public List<GitTreeNode> Children { get; } = [];

    public int Depth { get; set; }

    /// <summary>Remembers the expanded state across refreshes.</summary>
    public string Key => $"{(IsStaged ? "S" : "U")}|{Repo?.Root}|{Kind}|{Path}";

    public bool IsExpandable => Kind != GitNodeKind.File;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool IsExpanded { get; set; } = true;

    public string Chevron => IsExpanded ? "Chevron" : "ChevronRight";

    public double Indent => Depth * 14;

    public string Icon => Kind switch
    {
        GitNodeKind.Root or GitNodeKind.Folder => "Folder",
        GitNodeKind.Repository => "Repo",
        _ => "File",
    };

    /// <summary>C# files show "C#" in place of the page icon, as in Visual Studio.</summary>
    public bool IsCSharp => Kind == GitNodeKind.File && Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Razor pages and components show a page with a green "@", as in Visual Studio.</summary>
    public bool IsRazor => Kind == GitNodeKind.File
        && (Name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase));

    public string Letter => Change?.Letter ?? "";

    public string? ToolTip => Kind switch
    {
        GitNodeKind.File => $"{Change!.Kind}: {Path}",
        GitNodeKind.Repository => Repo!.Root,
        _ => null,
    };

    /// <summary>Builds the tree of one section (staged or not) from its changes.</summary>
    public static List<GitTreeNode> Build(IEnumerable<GitChange> changes, bool isStaged, bool showRoot)
    {
        var repositories = new List<GitTreeNode>();
        foreach (var group in changes.GroupBy(c => c.Repo).OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase))
        {
            var repo = new GitTreeNode(GitNodeKind.Repository, group.Key.Name, group.Key, "", isStaged);
            foreach (var change in group)
            {
                var parts = change.Path.Split('/');
                var parent = repo;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var path = string.Join("/", parts, 0, i + 1);
                    var folder = parent.Children.FirstOrDefault(n => n.Kind == GitNodeKind.Folder && n.Path == path);
                    if (folder is null)
                    {
                        folder = new GitTreeNode(GitNodeKind.Folder, parts[i], group.Key, path, isStaged);
                        parent.Children.Add(folder);
                    }
                    parent = folder;
                }
                parent.Children.Add(new GitTreeNode(GitNodeKind.File, parts[^1], group.Key, change.Path, isStaged, change));
            }
            repositories.Add(Tidy(repo));
        }
        if (!showRoot || repositories.Count == 0)
            return repositories;
        var root = new GitTreeNode(GitNodeKind.Root, CommonFolder(repositories.Select(r => r.Repo!.Root)), null, "", isStaged);
        root.Children.AddRange(repositories);
        return [root];
    }

    /// <summary>Folders first, then files, each by name; a folder holding only one folder joins it ("Components\Camera").</summary>
    static GitTreeNode Tidy(GitTreeNode node)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (child.Kind != GitNodeKind.Folder)
                continue;
            while (child.Children is [{ Kind: GitNodeKind.Folder } only])
            {
                var joined = new GitTreeNode(GitNodeKind.Folder, child.Name + "\\" + only.Name, only.Repo, only.Path, only.IsStaged);
                joined.Children.AddRange(only.Children);
                child = joined;
            }
            node.Children[i] = Tidy(child);
        }
        var sorted = node.Children.OrderBy(n => n.Kind == GitNodeKind.File).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
        node.Children.Clear();
        node.Children.AddRange(sorted);
        return node;
    }

    /// <summary>The deepest folder all the repositories are in ("E:\Uztelecom\AIVision").</summary>
    static string CommonFolder(IEnumerable<string> roots)
    {
        var parents = roots.Select(r => (System.IO.Path.GetDirectoryName(r) ?? r).Split(System.IO.Path.DirectorySeparatorChar)).ToList();
        var common = parents[0].ToList();
        foreach (var parts in parents.Skip(1))
        {
            var n = 0;
            while (n < common.Count && n < parts.Length && string.Equals(common[n], parts[n], StringComparison.OrdinalIgnoreCase))
                n++;
            common.RemoveRange(n, common.Count - n);
        }
        var folder = string.Join(System.IO.Path.DirectorySeparatorChar.ToString(), common);
        return folder.EndsWith(":", StringComparison.Ordinal) ? folder + System.IO.Path.DirectorySeparatorChar : folder;
    }

    /// <summary>This row and the rows under it that are visible (their parents expanded), with their depth.</summary>
    public static IEnumerable<GitTreeNode> Visible(IEnumerable<GitTreeNode> nodes, int depth = 0)
    {
        foreach (var node in nodes)
        {
            node.Depth = depth;
            yield return node;
            if (node.IsExpanded)
                foreach (var child in Visible(node.Children, depth + 1))
                    yield return child;
        }
    }

    /// <summary>Every node of the tree under (and including) these.</summary>
    public static IEnumerable<GitTreeNode> All(IEnumerable<GitTreeNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(All(n.Children)));
}

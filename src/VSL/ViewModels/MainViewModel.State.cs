using CommunityToolkit.Mvvm.ComponentModel;
using VSL.Editor;
using VSL.Services;
using VSL.Workspace;

namespace VSL.ViewModels;

/// <summary>Restoring and saving everything about the open solution in its .vs folder (see Services.SolutionState).</summary>
public partial class MainViewModel
{
    SolutionState _state = new();
    bool _restoringState;

    /// <summary>Whether opening a solution brings back its saved state (tests turn it off to start clean).</summary>
    public bool RestoresSolutionState { get; set; } = true;

    /// <summary>The node selected in Solution Explorer (bound to the tree).</summary>
    [ObservableProperty]
    public partial ExplorerNode? SelectedExplorerNode { get; set; }

    void RestoreSolutionState(SolutionInfo solution, SolutionState state)
    {
        Configuration = state.Configuration;
        LoadDebugState(state);
        foreach (var p in solution.Projects.Where(p => p.IsRunnable))
            StartupProjects.Add(p);
        // The launch profile comes back with the startup project (OnStartupProjectChanged reads it from the state).
        StartupProject = StartupProjects.FirstOrDefault(p => p.FilePath == state.StartupProject)
            ?? StartupProjects.FirstOrDefault(p => p.IsWeb)
            ?? StartupProjects.FirstOrDefault();

        foreach (var saved in state.Documents.Where(d => File.Exists(d.Path)))
        {
            if (OpenDocument(saved.Path, activate: false) is not { } doc)
                continue;
            doc.IsPinned = saved.Pinned;
            doc.Host.RestoreViewState(new EditorViewState(saved.CaretLine, saved.CaretColumn, saved.FirstVisibleLine, saved.Collapsed));
        }
        var root = solution.Root;
        foreach (var key in state.ExpandedNodes)
            if (FindExplorerNode(root, key) is { } node)
            {
                ExpandAncestors(root, node);
                node.IsExpanded = true;
            }
        if (state.SelectedNode is { } selected && FindExplorerNode(root, selected) is { } selectedNode)
        {
            ExpandAncestors(root, selectedNode);
            SelectedExplorerNode = selectedNode;
            ExplorerSelectRequested?.Invoke(selectedNode);
        }
        // Last: selecting a file in Solution Explorer may open it.
        ActiveDocument = Editors.FirstOrDefault(d => d.FilePath == state.ActiveDocument) ?? Documents.FirstOrDefault();
    }

    void CaptureSolutionState(SolutionState state)
    {
        state.Documents = Editors.Select(d =>
        {
            var view = d.Host.CaptureViewState();
            return new SolutionState.DocumentState
            {
                Path = d.FilePath,
                CaretLine = view.CaretLine,
                CaretColumn = view.CaretColumn,
                FirstVisibleLine = view.FirstVisibleLine,
                Pinned = d.IsPinned,
                Collapsed = [.. view.Collapsed],
            };
        }).ToList();
        state.ActiveDocument = ActiveEditor?.FilePath;
        state.StartupProject = StartupProject?.FilePath ?? state.StartupProject;
        state.LaunchProfile = LaunchProfile ?? state.LaunchProfile;
        state.Configuration = Configuration;
        SaveDebugState(state);
        // A Claude panel never opened this time keeps the conversation it would restore.
        if (_claude is { } claude)
            state.ClaudeConversation = claude.SessionId;
        if (ExplorerRoots.FirstOrDefault() is { } root)
        {
            var expanded = new List<string>();
            CollectExpanded(root, "", expanded);
            state.ExpandedNodes = expanded;
            state.SelectedNode = SelectedExplorerNode is { } node ? KeyOf(root, node) : null;
        }
    }

    /// <summary>Expanded nodes under <paramref name="node"/> (the solution itself is always expanded).</summary>
    static void CollectExpanded(ExplorerNode node, string folderPath, List<string> keys)
    {
        foreach (var child in node.Children)
        {
            var childFolder = child.Kind == NodeKind.SolutionFolder ? (folderPath.Length == 0 ? child.Name : folderPath + "/" + child.Name) : folderPath;
            if (child.IsExpanded && Key(child, childFolder) is { } key)
                keys.Add(key);
            // Only what has been loaded: a folder never expanded has nothing expanded inside.
            if (child.IsLoaded && child.Kind is not (NodeKind.Dependencies or NodeKind.DependencyGroup))
                CollectExpanded(child, childFolder, keys);
        }
    }

    static string? Key(ExplorerNode node, string solutionFolderPath) => node.Kind switch
    {
        NodeKind.SolutionFolder => "folder:" + solutionFolderPath,
        NodeKind.Project or NodeKind.Folder or NodeKind.File when node.Path is not null => node.Path,
        _ => null,
    };

    static string? KeyOf(ExplorerNode root, ExplorerNode target)
    {
        string? Search(ExplorerNode node, string folderPath)
        {
            foreach (var child in node.Children)
            {
                var childFolder = child.Kind == NodeKind.SolutionFolder ? (folderPath.Length == 0 ? child.Name : folderPath + "/" + child.Name) : folderPath;
                if (child == target)
                    return Key(child, childFolder);
                if ((child.IsLoaded || child.Kind == NodeKind.SolutionFolder) && Search(child, childFolder) is { } found)
                    return found;
            }
            return null;
        }
        return Search(root, "");
    }

    static ExplorerNode? FindExplorerNode(ExplorerNode root, string key)
    {
        if (!key.StartsWith("folder:", StringComparison.Ordinal))
            return root.FindByPath(key);
        var node = root;
        foreach (var name in key["folder:".Length..].Split('/'))
        {
            node = node.Children.FirstOrDefault(c => c.Kind == NodeKind.SolutionFolder && c.Name == name);
            if (node is null)
                return null;
        }
        return node;
    }
}

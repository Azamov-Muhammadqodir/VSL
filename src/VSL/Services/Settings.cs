using System.Text.Json;

namespace VSL.Services;

/// <summary>Per-user settings persisted to ~/.config/VSL/settings.json.</summary>
public sealed class Settings
{
    public string? LastSolution { get; set; }
    public double EditorZoom { get; set; } = 1.0;

    /// <summary>Tool window id → dock side ("Left", "Right", "Bottom") for windows the user moved.</summary>
    public Dictionary<string, string> ToolLayout { get; set; } = [];
    public List<string> RecentSolutions { get; set; } = [];

    /// <summary>When each recent solution was last opened (for the start window's Today / This week groups).</summary>
    public Dictionary<string, DateTime> RecentOpened { get; set; } = [];

    /// <summary>Solutions pinned to the top of the start window's recent list.</summary>
    public List<string> PinnedSolutions { get; set; } = [];

    /// <summary>Template identities used for new projects, most recent first.</summary>
    public List<string> RecentTemplates { get; set; } = [];

    public string? NewProjectLocation { get; set; }

    /// <summary>Claude Code panel: model alias ("default", "opus"...) and permission mode.</summary>
    public string? ClaudeModel { get; set; }

    public string? ClaudePermissionMode { get; set; }

    /// <summary>Claude Code reasoning effort chosen in the Modes menu (low ... max).</summary>
    public string? ClaudeEffort { get; set; }

    /// <summary>The Claude Code account (its configuration folder) the panel runs with; null for the CLI's own choice.</summary>
    public string? ClaudeAccount { get; set; }
    public Dictionary<string, SolutionState> Solutions { get; set; } = [];

    public sealed class SolutionState
    {
        public List<string> OpenDocuments { get; set; } = [];
        public string? ActiveDocument { get; set; }
        public string? StartupProject { get; set; }
        public string? LaunchProfile { get; set; }
        public string Configuration { get; set; } = "Debug";
        public Dictionary<string, List<int>> Breakpoints { get; set; } = [];
        public List<string> Watches { get; set; } = [];
    }

    static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("VSL_CONFIG_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VSL"),
        "settings.json");

    public SolutionState For(string solutionPath)
    {
        if (!Solutions.TryGetValue(solutionPath, out var state))
            Solutions[solutionPath] = state = new SolutionState();
        return state;
    }

    const int MaxRecent = 20;

    public void AddRecent(string solutionPath)
    {
        LastSolution = solutionPath;
        RecentSolutions.Remove(solutionPath);
        RecentSolutions.Insert(0, solutionPath);
        RecentOpened[solutionPath] = DateTime.Now;
        // Pinned entries never fall off the list.
        while (RecentSolutions.Count > MaxRecent && RecentSolutions.LastOrDefault(p => !PinnedSolutions.Contains(p)) is { } oldest)
            RemoveRecent(oldest);
    }

    public void RemoveRecent(string solutionPath)
    {
        RecentSolutions.Remove(solutionPath);
        RecentOpened.Remove(solutionPath);
        PinnedSolutions.Remove(solutionPath);
        if (LastSolution == solutionPath)
            LastSolution = RecentSolutions.FirstOrDefault();
    }

    public void AddRecentTemplate(string identity)
    {
        RecentTemplates.Remove(identity);
        RecentTemplates.Insert(0, identity);
        if (RecentTemplates.Count > 5)
            RecentTemplates.RemoveRange(5, RecentTemplates.Count - 5);
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // Corrupt settings should never block startup.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
    }
}

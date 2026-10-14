using System.Text.Json;

namespace VSL.Services;

/// <summary>Per-user settings persisted to ~/.config/VSL/settings.json.</summary>
public sealed class Settings
{
    public string? LastSolution { get; set; }
    public List<string> RecentSolutions { get; set; } = [];
    public Dictionary<string, SolutionState> Solutions { get; set; } = [];

    public sealed class SolutionState
    {
        public List<string> OpenDocuments { get; set; } = [];
        public string? ActiveDocument { get; set; }
        public string? StartupProject { get; set; }
        public string? LaunchProfile { get; set; }
        public string Configuration { get; set; } = "Debug";
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

    public void AddRecent(string solutionPath)
    {
        LastSolution = solutionPath;
        RecentSolutions.Remove(solutionPath);
        RecentSolutions.Insert(0, solutionPath);
        if (RecentSolutions.Count > 10)
            RecentSolutions.RemoveRange(10, RecentSolutions.Count - 10);
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

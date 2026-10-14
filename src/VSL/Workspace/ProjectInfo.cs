using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VSL.Workspace;

public sealed record PackageRef(string Name, string? Version);

public sealed record LaunchProfile(
    string Name,
    string? ApplicationUrl,
    bool LaunchBrowser,
    string? LaunchUrl,
    string? CommandLineArgs,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

/// <summary>What VSL needs to know about an SDK-style project, read straight from the .csproj.</summary>
public sealed class ProjectInfo
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public string Directory => Path.GetDirectoryName(FilePath)!;
    public string Sdk { get; private init; } = "";
    public string? OutputType { get; private init; }
    public IReadOnlyList<string> TargetFrameworks { get; private init; } = [];
    public IReadOnlyList<PackageRef> Packages { get; private init; } = [];
    public IReadOnlyList<string> ProjectReferences { get; private init; } = [];
    public IReadOnlyList<LaunchProfile> LaunchProfiles { get; private init; } = [];
    public bool IsTestProject { get; private init; }

    public bool IsWeb => Sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);

    public bool IsRunnable => !IsTestProject && (IsWeb
        || string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(OutputType, "WinExe", StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;

    static readonly Regex PropertyRef = new(@"\$\((\w+)\)", RegexOptions.Compiled);

    public static ProjectInfo Load(string name, string filePath)
    {
        if (!File.Exists(filePath))
            return new ProjectInfo { Name = name, FilePath = filePath };

        XDocument doc;
        try
        {
            doc = XDocument.Load(filePath);
        }
        catch (Exception)
        {
            return new ProjectInfo { Name = name, FilePath = filePath };
        }

        var root = doc.Root!;
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup").SelectMany(g => g.Elements()))
            props[p.Name.LocalName] = p.Value.Trim();
        string Expand(string? value) => value is null ? "" : PropertyRef.Replace(value, m => props.GetValueOrDefault(m.Groups[1].Value, m.Value));

        var items = root.Elements().Where(e => e.Name.LocalName == "ItemGroup").SelectMany(g => g.Elements()).ToList();
        var packages = items
            .Where(e => e.Name.LocalName == "PackageReference" && e.Attribute("Include") is not null)
            .Select(e => new PackageRef(e.Attribute("Include")!.Value,
                Expand(e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dir = Path.GetDirectoryName(filePath)!;
        var projectRefs = items
            .Where(e => e.Name.LocalName == "ProjectReference" && e.Attribute("Include") is not null)
            .Select(e => Path.GetFullPath(Path.Combine(dir, e.Attribute("Include")!.Value.Replace('\\', '/'))))
            .ToList();

        var tfms = props.TryGetValue("TargetFrameworks", out var many) ? many.Split(';', StringSplitOptions.RemoveEmptyEntries)
            : props.TryGetValue("TargetFramework", out var one) ? [one] : [];

        return new ProjectInfo
        {
            Name = name,
            FilePath = filePath,
            Sdk = root.Attribute("Sdk")?.Value ?? "",
            OutputType = props.GetValueOrDefault("OutputType"),
            TargetFrameworks = tfms.Select(Expand).ToList(),
            Packages = packages,
            ProjectReferences = projectRefs,
            IsTestProject = string.Equals(props.GetValueOrDefault("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
                || packages.Any(p => p.Name == "Microsoft.NET.Test.Sdk"),
            LaunchProfiles = LoadLaunchProfiles(Path.Combine(dir, "Properties", "launchSettings.json")),
        };
    }

    static IReadOnlyList<LaunchProfile> LoadLaunchProfiles(string path)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles))
                return [];
            var list = new List<LaunchProfile>();
            foreach (var p in profiles.EnumerateObject())
            {
                var v = p.Value;
                // Only "Project" profiles can run on Linux (no IIS Express, no Docker integration yet).
                if (!v.TryGetProperty("commandName", out var cmd) || cmd.GetString() != "Project")
                    continue;
                string? Str(string n) => v.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                var env = new Dictionary<string, string>();
                if (v.TryGetProperty("environmentVariables", out var e) && e.ValueKind == JsonValueKind.Object)
                    foreach (var kv in e.EnumerateObject())
                        env[kv.Name] = kv.Value.ToString();
                var browser = v.TryGetProperty("launchBrowser", out var lb) && lb.ValueKind == JsonValueKind.True;
                list.Add(new LaunchProfile(p.Name, Str("applicationUrl"), browser, Str("launchUrl"), Str("commandLineArgs"), env));
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

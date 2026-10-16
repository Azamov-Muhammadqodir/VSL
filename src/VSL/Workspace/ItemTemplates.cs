using System.Text;
using System.Xml.Linq;

namespace VSL.Workspace;

/// <summary>An "Add New Item" template: what it makes and the text of the new file.</summary>
public sealed record ItemTemplate(string Name, string Description, string Extension, string BaseName, Func<string, string, string> Content)
{
    /// <summary>A file of this kind, for its Solution Explorer icon.</summary>
    public string SampleFile => BaseName + Extension;

    public override string ToString() => Name;
}

/// <summary>
/// The item templates Visual Studio offers in Add New Item for C# and Blazor projects, writing what VS's templates
/// write (file-scoped namespace from the project's root namespace and the folder, as `dotnet new class` does).
/// </summary>
public static class ItemTemplates
{
    static string CSharp(string ns, string body) => $"namespace {ns};\n\n{body}\n";

    public static IReadOnlyList<ItemTemplate> All { get; } =
    [
        new("Class", "An empty class definition", ".cs", "Class", (ns, name) => CSharp(ns, $"public class {name}\n{{\n}}")),
        new("Interface", "An empty interface definition", ".cs", "Interface", (ns, name) => CSharp(ns, $"public interface {name}\n{{\n}}")),
        new("Record", "An empty record definition", ".cs", "Record", (ns, name) => CSharp(ns, $"public record {name};")),
        new("Struct", "An empty struct definition", ".cs", "Struct", (ns, name) => CSharp(ns, $"public struct {name}\n{{\n}}")),
        new("Enum", "An enumeration", ".cs", "Enum", (ns, name) => CSharp(ns, $"public enum {name}\n{{\n}}")),
        new("Razor Component", "A Razor component for Blazor", ".razor", "Component", (_, name) => $"<h3>{name}</h3>\n\n@code {{\n\n}}\n"),
        new("Code File", "An empty C# code file", ".cs", "CodeFile", (_, _) => ""),
        new("JSON File", "A JSON file", ".json", "JSONFile", (_, _) => "{\n}\n"),
        new("Text File", "A blank text file", ".txt", "TextFile", (_, _) => ""),
        new("XML File", "A blank XML file", ".xml", "XMLFile", (_, _) => "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n"),
        new("HTML Page", "An HTML page", ".html", "HTMLPage", (_, name) =>
            $"<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n    <meta charset=\"utf-8\" />\n    <title>{name}</title>\n</head>\n<body>\n\n</body>\n</html>\n"),
        new("JavaScript File", "A JavaScript file", ".js", "JavaScript", (_, _) => ""),
        new("Style Sheet", "A cascading style sheet (CSS)", ".css", "StyleSheet", (_, _) => "body {\n}\n"),
        new("Markdown File", "A Markdown file", ".md", "Markdown", (_, name) => $"# {name}\n"),
    ];

    public static ItemTemplate Class => All[0];

    public static ItemTemplate RazorComponent => All.First(t => t.Extension == ".razor");

    /// <summary>"Class1.cs", "Class2.cs"...: the first name not taken in <paramref name="folder"/>, as VS suggests.</summary>
    public static string SuggestName(ItemTemplate template, string folder)
    {
        for (var i = 1; ; i++)
        {
            var name = template.BaseName + i + template.Extension;
            if (!File.Exists(Path.Combine(folder, name)) && !Directory.Exists(Path.Combine(folder, name)))
                return name;
        }
    }

    /// <summary>The file name to create: the typed name, with the template's extension when it has none.</summary>
    public static string FileName(ItemTemplate template, string typed)
    {
        var name = typed.Trim();
        return Path.HasExtension(name) ? name : name + template.Extension;
    }

    /// <summary>Why a name cannot be used, or null when it can.</summary>
    public static string? Validate(string folder, string fileName)
    {
        if (fileName.Length == 0 || fileName.Trim('.').Length == 0)
            return "Enter a name.";
        if (fileName.IndexOfAny(['/', '\0']) >= 0 || fileName is "." or "..")
            return $"'{fileName}' is not a valid file name.";
        if (File.Exists(Path.Combine(folder, fileName)) || Directory.Exists(Path.Combine(folder, fileName)))
            return $"A file or folder named '{fileName}' already exists here.";
        return null;
    }

    /// <summary>The text of a new item named <paramref name="fileName"/> in <paramref name="folder"/> of <paramref name="project"/>.</summary>
    public static string Render(ItemTemplate template, string fileName, string folder, ProjectInfo? project)
    {
        var name = Identifier(Path.GetFileNameWithoutExtension(fileName));
        return template.Content(Namespace(project, folder), name);
    }

    /// <summary>
    /// The project's root namespace (its RootNamespace, else its name) followed by the folders down to
    /// <paramref name="folder"/>, as Visual Studio names a new class's namespace.
    /// </summary>
    public static string Namespace(ProjectInfo? project, string folder)
    {
        if (project is null)
            return Identifier(new DirectoryInfo(folder).Name);
        var parts = new List<string> { RootNamespace(project) };
        var relative = Path.GetRelativePath(project.Directory, folder);
        if (relative != "." && !relative.StartsWith("..", StringComparison.Ordinal))
            parts.AddRange(relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Identifier));
        return string.Join('.', parts);
    }

    static string RootNamespace(ProjectInfo project)
    {
        try
        {
            var declared = XDocument.Load(project.FilePath).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value.Trim();
            if (declared is { Length: > 0 } && !declared.Contains("$(", StringComparison.Ordinal))
                return declared;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
        }
        return string.Join('.', project.Name.Split('.').Select(Identifier));
    }

    /// <summary>A name made usable as a C# identifier: invalid characters become '_', a leading digit gets one.</summary>
    static string Identifier(string text)
    {
        var builder = new StringBuilder(text.Length + 1);
        foreach (var c in text)
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (builder.Length == 0 || char.IsDigit(builder[0]))
            builder.Insert(0, '_');
        return builder.ToString();
    }
}

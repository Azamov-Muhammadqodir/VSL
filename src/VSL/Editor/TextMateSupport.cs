using System.Text;
using System.Text.Json.Nodes;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace VSL.Editor;

/// <summary>Shared TextMate registry: VS Code's "Visual Studio Dark" theme plus VSL's Visual Studio overrides.</summary>
public static class TextMateSupport
{
    static readonly Lazy<RegistryOptions> _registry = new(() => new RegistryOptions(ThemeName.VisualStudioDark));
    static readonly Lazy<VslRegistryOptions> _vslRegistry = new(() => new VslRegistryOptions(_registry.Value));

    public static RegistryOptions Registry => _registry.Value;

    /// <summary>Token colors layered on top of the stock theme so editors look like Visual Studio, not VS Code.</summary>
    static readonly (string Scope, string Foreground, string FontStyle)[] Overrides =
    [
        // The HTML grammar flags capitalised tags as illegal (red + underline); in Razor they are components.
        ("invalid.illegal.unrecognized-tag.html", "#4EC9B0", ""),
        // Diff view (Git > Compare): the stock theme leaves these uncolored.
        ("markup.inserted", "#89D185", ""),
        ("markup.deleted", "#F48771", ""),
        ("markup.changed", "#569CD6", ""),
        ("meta.diff.range", "#C586C0", ""),
        ("meta.diff.header", "#569CD6", ""),
        ("meta.diff.index", "#808080", ""),
    ];

    public static TextMate.Installation? Install(TextEditor editor, string filePath)
    {
        var scope = ScopeFor(filePath);
        if (scope is null)
            return null;
        var installation = editor.InstallTextMate(_vslRegistry.Value);
        installation.SetGrammar(scope);
        return installation;
    }

    public static string? ScopeFor(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        ext = ext switch
        {
            ".csproj" or ".props" or ".targets" or ".resx" or ".config" or ".slnx" or ".xaml" or ".axaml" => ".xml",
            ".razor" => ".cshtml",
            _ => ext,
        };
        if (Path.GetFileName(filePath).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase))
            ext = ".dockerfile";
        var language = Registry.GetLanguageByExtension(ext);
        return language is null ? null : Registry.GetScopeByLanguageId(language.Id);
    }

    static IRawTheme BuildTheme()
    {
        using var stream = typeof(RegistryOptions).Assembly.GetManifestResourceStream("TextMateSharp.Grammars.Resources.Themes.visual-studio-dark-theme.json")
            ?? throw new InvalidOperationException("Visual Studio Dark theme resource not found.");
        var theme = JsonNode.Parse(stream, documentOptions: new System.Text.Json.JsonDocumentOptions
        {
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!.AsObject();
        var rules = (theme["tokenColors"] ?? theme["settings"])!.AsArray();
        foreach (var (scope, foreground, fontStyle) in Overrides)
            rules.Add(new JsonObject
            {
                ["scope"] = scope,
                ["settings"] = new JsonObject { ["foreground"] = foreground, ["fontStyle"] = fontStyle },
            });
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(theme.ToJsonString())));
        return ThemeReader.ReadThemeSync(reader);
    }

    /// <summary>Delegates grammars to the stock registry but serves our patched theme.</summary>
    sealed class VslRegistryOptions(RegistryOptions inner) : IRegistryOptions
    {
        readonly Lazy<IRawTheme> _theme = new(BuildTheme);

        public IRawTheme GetTheme(string scopeName) => inner.GetTheme(scopeName);

        public IRawGrammar GetGrammar(string scopeName) => inner.GetGrammar(scopeName);

        public ICollection<string> GetInjections(string scopeName) => inner.GetInjections(scopeName);

        public IRawTheme GetDefaultTheme() => _theme.Value;
    }
}

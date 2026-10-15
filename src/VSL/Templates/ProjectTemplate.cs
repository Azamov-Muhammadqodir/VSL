namespace VSL.Templates;

public sealed record TemplateChoice(string Value, string Label, string? Description);

public enum TemplateOptionKind
{
    Choice,
    Bool,
}

/// <summary>
/// A template parameter shown on "Additional information". <see cref="Invert"/> means the checkbox is the
/// opposite of the template symbol (VS shows "Configure for HTTPS" for the NoHttps symbol, for example).
/// </summary>
public sealed record TemplateOption(
    string Symbol,
    string CliName,
    string Label,
    string? Description,
    TemplateOptionKind Kind,
    IReadOnlyList<TemplateChoice> Choices,
    string? DefaultValue,
    bool Invert = false)
{
    /// <summary>The checkbox state when nothing was chosen.</summary>
    public bool DefaultChecked => string.Equals(DefaultValue, "true", StringComparison.OrdinalIgnoreCase) != Invert;

    /// <summary>The <c>dotnet new</c> arguments for a value (a choice value, or "true"/"false" for the checkbox).</summary>
    public IEnumerable<string> Arguments(string value)
    {
        if (Kind == TemplateOptionKind.Choice)
            return [CliName, value];
        var symbolValue = bool.Parse(value) != Invert;
        return [CliName, symbolValue ? "true" : "false"];
    }
}

/// <summary>A project template installed in the .NET SDK (one entry per template group and language).</summary>
public sealed record ProjectTemplate(
    string Identity,
    string GroupIdentity,
    string Name,
    string ShortName,
    string Description,
    string? Author,
    string? Language,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Platforms,
    int Order,
    string DefaultName,
    IReadOnlyList<TemplateOption> Options)
{
    /// <summary>The template's own icon (extracted from its package), when it ships one.</summary>
    public string? IconPath { get; init; }

    /// <summary>Language first, then platforms and project types, as VS lists them under a template.</summary>
    public IReadOnlyList<string> AllTags => [.. (Language is null ? [] : new[] { Language }), .. Platforms, .. Tags];

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            Name.Contains(word, StringComparison.OrdinalIgnoreCase)
            || ShortName.Contains(word, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(word, StringComparison.OrdinalIgnoreCase)
            || AllTags.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }
}

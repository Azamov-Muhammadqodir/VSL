using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using VSL.Lsp;

namespace VSL.Editor;

/// <summary>
/// Collapsible blocks for the editor (Visual Studio's outlining): from Roslyn's folding ranges for C#, or from
/// a structural scan (braces, brackets, #region, doc comments, XML elements) for other files and until the
/// language server is ready.
/// </summary>
public static partial class Outlining
{
    static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".config", ".xaml", ".axaml", ".resx", ".nuspec", ".slnx", ".svg",
    };

    /// <summary>Roslyn's folding ranges as foldings: whole lines unless the range gives columns, as VS shows them.</summary>
    public static List<NewFolding> FromLsp(TextDocument document, IReadOnlyList<LspFoldingRange> ranges)
    {
        var foldings = new List<NewFolding>();
        var notCode = new HashSet<NewFolding>();
        foreach (var range in ranges)
        {
            if (range.StartLine < 0 || range.EndLine >= document.LineCount || range.EndLine <= range.StartLine)
                continue;
            var startLine = document.GetLineByNumber(range.StartLine + 1);
            var endLine = document.GetLineByNumber(range.EndLine + 1);
            var lineText = document.GetText(startLine);
            int start, end;
            string title;
            if (range.Kind == "region" || RegionStart().IsMatch(lineText))
            {
                // A collapsed region shows its name, like VS.
                start = startLine.Offset + (lineText.Length - lineText.TrimStart().Length);
                end = endLine.EndOffset;
                title = RegionTitle(lineText);
            }
            else
            {
                start = range.StartCharacter is { } sc ? startLine.Offset + Math.Min(sc, startLine.Length) : startLine.EndOffset;
                end = range.EndCharacter is { } ec ? endLine.Offset + Math.Min(ec, endLine.Length) : endLine.EndOffset;
                title = range.CollapsedText is { Length: > 0 } collapsed ? collapsed
                    : range.Kind == "imports" ? "..."
                    : range.Kind == "comment" ? CommentTitle(lineText)
                    : "...";
            }
            if (end <= start)
                continue;
            var folding = new NewFolding(start, end) { Name = title };
            foldings.Add(folding);
            if (range.Kind is "region" or "comment" or "imports" || title != "..." && RegionStart().IsMatch(lineText))
                notCode.Add(folding);
        }
        return MarkDefinitions(document, foldings, notCode);
    }

    /// <summary>Foldings found by scanning the text, for files the language server does not outline.</summary>
    public static List<NewFolding> Scan(TextDocument document, string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (XmlExtensions.Contains(extension))
        {
            var xml = new XmlFoldingStrategy { ShowAttributesWhenFolded = true };
            return xml.CreateNewFoldings(document, out _).ToList();
        }
        var foldings = Braces(document, cStyle: extension is not ".json");
        var notCode = Regions(document).Concat(DocComments(document)).ToHashSet();
        foldings.AddRange(notCode);
        return MarkDefinitions(document, foldings, notCode);
    }

    /// <summary>{ } and [ ] blocks over several lines, skipping strings and comments.</summary>
    static List<NewFolding> Braces(TextDocument document, bool cStyle)
    {
        var text = document.Text;
        var foldings = new List<NewFolding>();
        var open = new Stack<(char Bracket, int Offset)>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (cStyle && c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i = text.IndexOf('\n', i) is var nl and >= 0 ? nl : text.Length;
                continue;
            }
            if (cStyle && c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var close and >= 0 ? close + 1 : text.Length;
                continue;
            }
            if (c is '"' or '\'')
            {
                i = SkipString(text, i, verbatim: i > 0 && text[i - 1] == '@' || i > 1 && text[i - 2] == '@' && text[i - 1] == '$');
                continue;
            }
            if (c is '{' or '[')
            {
                open.Push((c, i));
            }
            else if (c is '}' or ']' && open.Count > 0)
            {
                var (bracket, start) = open.Pop();
                if ((bracket == '{') != (c == '}'))
                    continue;
                var startLine = document.GetLineByOffset(start);
                var endLine = document.GetLineByOffset(i);
                if (endLine.LineNumber <= startLine.LineNumber)
                    continue;
                // Allman style ("{" alone on its line): fold from the end of the declaration above, as VS shows "void M() {...}".
                var foldStart = start;
                if (document.GetText(startLine).Trim() == bracket.ToString() && startLine.PreviousLine is { } declaration
                    && document.GetText(declaration).Trim().Length > 0)
                    foldStart = declaration.EndOffset;
                foldings.Add(new NewFolding(foldStart, i + 1) { Name = bracket == '{' ? "{...}" : "[...]" });
            }
        }
        return foldings;
    }

    static int SkipString(string text, int start, bool verbatim)
    {
        var quote = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\n' && !verbatim)
                return i;
            if (!verbatim && text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == quote)
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == quote)
                {
                    i++;
                    continue;
                }
                return i;
            }
        }
        return text.Length;
    }

    /// <summary>#region ... #endregion, shown by name when collapsed.</summary>
    static IEnumerable<NewFolding> Regions(TextDocument document)
    {
        var open = new Stack<DocumentLine>();
        foreach (var line in document.Lines)
        {
            var text = document.GetText(line);
            if (RegionStart().IsMatch(text))
            {
                open.Push(line);
            }
            else if (RegionEnd().IsMatch(text) && open.Count > 0)
            {
                var start = open.Pop();
                var startText = document.GetText(start);
                yield return new NewFolding(start.Offset + (startText.Length - startText.TrimStart().Length), line.EndOffset) { Name = RegionTitle(startText) };
            }
        }
    }

    /// <summary>Runs of /// documentation comments, shown as their first line.</summary>
    static IEnumerable<NewFolding> DocComments(TextDocument document)
    {
        DocumentLine? first = null;
        DocumentLine? last = null;
        foreach (var line in document.Lines.Append(null))
        {
            if (line is not null && document.GetText(line).TrimStart().StartsWith("///", StringComparison.Ordinal))
            {
                first ??= line;
                last = line;
                continue;
            }
            if (first is not null && last is not null && last.LineNumber > first.LineNumber)
            {
                var text = document.GetText(first);
                yield return new NewFolding(first.Offset + (text.Length - text.TrimStart().Length), last.EndOffset) { Name = CommentTitle(text) };
            }
            first = last = null;
        }
    }

    static string RegionTitle(string line)
    {
        var name = RegionStart().Replace(line, "").Trim();
        return name.Length > 0 ? name : "#region";
    }

    static string CommentTitle(string line)
    {
        var text = line.Trim();
        return (text.Length > 60 ? text[..60] : text) + " ...";
    }

    /// <summary>
    /// Member bodies (methods, properties, constructors...) are "definitions": what Collapse to Definitions folds.
    /// A member is a block directly inside a type that is not itself a type or a statement.
    /// </summary>
    static List<NewFolding> MarkDefinitions(TextDocument document, List<NewFolding> foldings, HashSet<NewFolding> notCode)
    {
        bool IsRegionOrComment(NewFolding folding) => notCode.Contains(folding);
        foldings.Sort((a, b) => a.StartOffset != b.StartOffset ? a.StartOffset.CompareTo(b.StartOffset) : b.EndOffset.CompareTo(a.EndOffset));
        var stack = new Stack<(NewFolding Folding, bool IsType)>();
        foreach (var folding in foldings)
        {
            while (stack.Count > 0 && stack.Peek().Folding.EndOffset <= folding.StartOffset)
                stack.Pop();
            var header = HeaderText(document, folding);
            var isType = TypeDeclaration().IsMatch(header);
            var isStatement = Statement().IsMatch(header);
            var parentIsType = stack.FirstOrDefault(s => !IsRegionOrComment(s.Folding)) is { Folding: not null } parent && parent.IsType;
            folding.IsDefinition = parentIsType && !isType && !isStatement && !IsRegionOrComment(folding);
            if (!IsRegionOrComment(folding))
                stack.Push((folding, isType));
        }
        return foldings;
    }

    /// <summary>The declaration a block belongs to: its own first line, or the line above a lone "{".</summary>
    static string HeaderText(TextDocument document, NewFolding folding)
    {
        var line = document.GetLineByOffset(Math.Min(folding.StartOffset, document.TextLength));
        var text = document.GetText(line).Trim();
        if (text.StartsWith('{') && line.PreviousLine is { } previous)
            text = document.GetText(previous).Trim();
        return text;
    }

    [GeneratedRegex(@"^\s*#\s*region\b")]
    private static partial Regex RegionStart();

    [GeneratedRegex(@"^\s*#\s*endregion\b")]
    private static partial Regex RegionEnd();

    [GeneratedRegex(@"\b(class|struct|interface|enum|record|namespace)\b")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"^(\}?\s*)(if|else|for|foreach|while|do|switch|try|catch|finally|using|lock|fixed|unsafe|checked|unchecked|return|var|await|case|default)\b|=>\s*$|=\s*new\b|^\s*\(|^[\w.]+\s*\(")]
    private static partial Regex Statement();
}

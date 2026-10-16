using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace VSL.Editor;

/// <summary>
/// Roslyn's semantic tokens (its classifications) drawn over the TextMate colors with Visual Studio's Dark theme colors:
/// what a grammar cannot know, such as locals and parameters vs fields, methods, structs vs classes, or control keywords.
/// Tokens move with edits until the server sends fresh ones; an edited line keeps its TextMate colors meanwhile.
/// </summary>
public sealed class SemanticColorizer : DocumentColorizingTransformer
{
    /// <summary>Visual Studio 2022 Dark's colors for Roslyn's classification names (the server's token types).</summary>
    static readonly Dictionary<string, IBrush> Colors = new(StringComparer.Ordinal)
    {
        ["namespace"] = Brush("#DCDCDC"),
        ["type"] = Brush("#4EC9B0"),
        ["class"] = Brush("#4EC9B0"),
        ["recordClass"] = Brush("#4EC9B0"),
        ["delegate"] = Brush("#4EC9B0"),
        ["module"] = Brush("#4EC9B0"),
        ["struct"] = Brush("#86C691"),
        ["recordStruct"] = Brush("#86C691"),
        ["interface"] = Brush("#B8D7A3"),
        ["enum"] = Brush("#B8D7A3"),
        ["typeParameter"] = Brush("#B8D7A3"),
        ["enumMember"] = Brush("#DCDCDC"),
        ["parameter"] = Brush("#9CDCFE"),
        ["variable"] = Brush("#9CDCFE"),
        ["local"] = Brush("#9CDCFE"),
        ["property"] = Brush("#DCDCDC"),
        ["field"] = Brush("#DCDCDC"),
        ["event"] = Brush("#DCDCDC"),
        ["constant"] = Brush("#DCDCDC"),
        ["label"] = Brush("#C8C8C8"),
        ["function"] = Brush("#DCDCAA"),
        ["method"] = Brush("#DCDCAA"),
        ["extensionMethod"] = Brush("#DCDCAA"),
        ["macro"] = Brush("#9B9B9B"),
        ["preprocessorKeyword"] = Brush("#9B9B9B"),
        ["preprocessorText"] = Brush("#DCDCDC"),
        ["excludedCode"] = Brush("#9B9B9B"),
        ["keyword"] = Brush("#569CD6"),
        ["modifier"] = Brush("#569CD6"),
        ["controlKeyword"] = Brush("#D8A0DF"),
        ["comment"] = Brush("#57A64A"),
        ["xmlDocCommentText"] = Brush("#57A64A"),
        ["xmlDocCommentComment"] = Brush("#57A64A"),
        ["xmlDocCommentDelimiter"] = Brush("#608B4E"),
        ["xmlDocCommentName"] = Brush("#608B4E"),
        ["xmlDocCommentAttributeName"] = Brush("#608B4E"),
        ["xmlDocCommentAttributeQuotes"] = Brush("#608B4E"),
        ["xmlDocCommentAttributeValue"] = Brush("#608B4E"),
        ["xmlDocCommentCDataSection"] = Brush("#608B4E"),
        ["xmlDocCommentEntityReference"] = Brush("#608B4E"),
        ["xmlDocCommentProcessingInstruction"] = Brush("#608B4E"),
        ["string"] = Brush("#D69D85"),
        ["stringVerbatim"] = Brush("#D69D85"),
        ["stringEscapeCharacter"] = Brush("#FFD68F"),
        ["regexp"] = Brush("#D69D85"),
        ["number"] = Brush("#B5CEA8"),
        ["operator"] = Brush("#B4B4B4"),
        ["operatorOverloaded"] = Brush("#B4B4B4"),
        ["punctuation"] = Brush("#DCDCDC"),
    };

    readonly Dictionary<int, List<(int Start, int Length, IBrush Brush, string Type)>> _lines = [];

    static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color)).ToImmutable();

    public static IBrush? ColorOf(string tokenType) => Colors.GetValueOrDefault(tokenType);

    public bool HasTokens => _lines.Count > 0;

    /// <summary>Replaces the tokens with the server's (relative encoding: line delta, start delta, length, type, modifiers).</summary>
    public void SetTokens(IReadOnlyList<int> data, IReadOnlyList<string> legend)
    {
        _lines.Clear();
        int line = 0, start = 0;
        for (var i = 0; i + 4 < data.Count; i += 5)
        {
            if (data[i] != 0)
                start = 0;
            line += data[i];
            start += data[i + 1];
            var type = data[i + 3];
            if (type < 0 || type >= legend.Count || ColorOf(legend[type]) is not { } brush)
                continue;
            // LSP lines are 0-based; AvaloniaEdit's are 1-based.
            if (!_lines.TryGetValue(line + 1, out var tokens))
                _lines[line + 1] = tokens = [];
            tokens.Add((start, data[i + 2], brush, legend[type]));
        }
    }

    public void Clear() => _lines.Clear();

    /// <summary>The color the server gave the character at <paramref name="column"/> of <paramref name="line"/> (1-based), if any.</summary>
    internal IBrush? ColorAt(int line, int column) => TokenAt(line, column)?.Brush;

    /// <summary>The server's token type at a position (1-based line), if any.</summary>
    internal string? TypeAt(int line, int column) => TokenAt(line, column)?.Type;

    (IBrush Brush, string Type)? TokenAt(int line, int column) =>
        _lines.TryGetValue(line, out var tokens)
            ? tokens.Where(t => column >= t.Start && column < t.Start + t.Length).Select(t => ((IBrush, string)?)(t.Brush, t.Type)).FirstOrDefault()
            : null;

    /// <summary>Keeps tokens on their lines across an edit: the edited lines lose theirs, later lines shift.</summary>
    public void OnDocumentChanged(TextDocument document, DocumentChangeEventArgs e)
    {
        if (_lines.Count == 0)
            return;
        var firstLine = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength)).LineNumber;
        var removed = Count(e.RemovedText.Text);
        var inserted = Count(e.InsertedText.Text);
        var shifted = new Dictionary<int, List<(int, int, IBrush, string)>>();
        foreach (var (line, tokens) in _lines)
        {
            if (line < firstLine)
                shifted[line] = tokens;
            else if (line > firstLine + removed)
                shifted[line + inserted - removed] = tokens;
        }
        _lines.Clear();
        foreach (var (line, tokens) in shifted)
            _lines[line] = tokens;

        static int Count(string text)
        {
            var n = 0;
            foreach (var c in text)
                if (c == '\n')
                    n++;
            return n;
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!_lines.TryGetValue(line.LineNumber, out var tokens))
            return;
        foreach (var (start, length, brush, _) in tokens)
        {
            var from = line.Offset + start;
            var to = Math.Min(from + length, line.EndOffset);
            if (from < line.Offset || from >= to)
                continue;
            ChangeLinePart(from, to, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}

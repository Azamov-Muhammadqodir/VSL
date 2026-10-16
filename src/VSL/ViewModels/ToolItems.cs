using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using VSL.Lsp;

namespace VSL.ViewModels;

/// <summary>A row in the Error List.</summary>
public sealed record ErrorItem(
    DiagnosticSeverity Severity,
    string Code,
    string Description,
    string Project,
    string? FilePath,
    int Line,
    int Column,
    string Source)
{
    public string File => FilePath is null ? "" : Path.GetFileName(FilePath);

    public string LineText => Line > 0 ? Line.ToString() : "";

    /// <summary><see cref="Views.VsIcon"/> kind for the severity column.</summary>
    public string SeverityIcon => Severity switch
    {
        DiagnosticSeverity.Error => "Error",
        DiagnosticSeverity.Warning => "Warning",
        _ => "Info",
    };
}

/// <summary>A row in the Find Symbol Results / Find References window.</summary>
public sealed record FindResultItem(string FilePath, int Line, int Column, string Preview, LspRange Range)
{
    public string Display => $"{Path.GetFileName(FilePath)}({Line},{Column}): {Preview}";
}

/// <summary>
/// An Output window channel ("Show output from: Build"). Appends are marshalled to the UI thread. ANSI colors in the
/// text (a program's console logger) become <see cref="Spans"/>, drawn like the integrated terminal draws them.
/// </summary>
public sealed partial class OutputChannel : ObservableObject
{
    const int MaxLength = 2_000_000;

    readonly AnsiText _ansi = new();
    readonly List<OutputSpan> _spans = [];

    public OutputChannel(string name) => Name = name;

    public string Name { get; }

    public TextDocument Document { get; } = new();

    /// <summary>The colored runs of the text, in order.</summary>
    public IReadOnlyList<OutputSpan> Spans => _spans;

    public event Action? Appended;

    public void Append(string line)
    {
        if (Dispatcher.UIThread.CheckAccess())
            AppendCore(line);
        else
            Dispatcher.UIThread.Post(() => AppendCore(line));
    }

    /// <summary>Appends text as-is (debuggee output already carries its own newlines).</summary>
    public void AppendRaw(string text)
    {
        if (Dispatcher.UIThread.CheckAccess())
            AppendText(text);
        else
            Dispatcher.UIThread.Post(() => AppendText(text));
    }

    void AppendCore(string line) => AppendText(line + "\n");

    void AppendText(string text)
    {
        if (Document.TextLength > MaxLength)
            Trim(Document.TextLength / 2);
        var (plain, spans) = _ansi.Parse(text);
        var at = Document.TextLength;
        // The runs go in before the text so the editor colors the new lines when it draws them.
        foreach (var span in spans)
            _spans.Add(span with { Start = span.Start + at });
        Document.Insert(at, plain);
        Appended?.Invoke();
    }

    void Trim(int length)
    {
        Document.Remove(0, length);
        for (var i = _spans.Count - 1; i >= 0; i--)
        {
            var span = _spans[i];
            var end = span.Start + span.Length - length;
            if (end <= 0)
                _spans.RemoveAt(i);
            else
                _spans[i] = span with { Start = Math.Max(span.Start - length, 0), Length = end - Math.Max(span.Start - length, 0) };
        }
    }

    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
            ClearCore();
        else
            Dispatcher.UIThread.Post(ClearCore);
    }

    void ClearCore()
    {
        _spans.Clear();
        _ansi.Reset();
        Document.Text = "";
    }

    public override string ToString() => Name;
}

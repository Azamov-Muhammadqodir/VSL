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

    public string SeverityGlyph => Severity switch
    {
        DiagnosticSeverity.Error => "⨂",
        DiagnosticSeverity.Warning => "⚠",
        _ => "ⓘ",
    };

    public string SeverityColor => Severity switch
    {
        DiagnosticSeverity.Error => "#F14C4C",
        DiagnosticSeverity.Warning => "#CCA700",
        _ => "#3794FF",
    };
}

/// <summary>A row in the Find Symbol Results / Find References window.</summary>
public sealed record FindResultItem(string FilePath, int Line, int Column, string Preview, LspRange Range)
{
    public string Display => $"{Path.GetFileName(FilePath)}({Line},{Column}): {Preview}";
}

/// <summary>An Output window channel ("Show output from: Build"). Appends are marshalled to the UI thread.</summary>
public sealed partial class OutputChannel : ObservableObject
{
    const int MaxLength = 2_000_000;

    public OutputChannel(string name) => Name = name;

    public string Name { get; }

    public TextDocument Document { get; } = new();

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
            Document.Remove(0, Document.TextLength / 2);
        Document.Insert(Document.TextLength, text);
        Appended?.Invoke();
    }

    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Document.Text = "";
        else
            Dispatcher.UIThread.Post(() => Document.Text = "");
    }

    public override string ToString() => Name;
}

using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Folding;

namespace VSL.Editor;

/// <summary>What a collapsible block is, for Collapse to Definitions.</summary>
public enum OutlineKind
{
    Code,
    Definition,
    Region,
    Comment,
}

/// <summary>Outlining: collapsible methods, types, regions and comments with VS's commands (Ctrl+M chords).</summary>
public sealed partial class EditorHost
{
    static readonly IBrush FoldingMarkerBrush = new SolidColorBrush(Color.Parse("#7A7A7A"));
    static readonly IBrush FoldingMarkerSelectedBrush = new SolidColorBrush(Color.Parse("#D0D0D0"));

    FoldingManager? _folding;
    CancellationTokenSource? _outliningCts;
    readonly ConditionalWeakTable<FoldingSection, object> _outlineKinds = [];

    /// <summary>The blocks that can be collapsed, in document order.</summary>
    public IReadOnlyList<FoldingSection> Outlines => _folding?.AllFoldings.ToList() ?? [];

    public OutlineKind KindOf(FoldingSection section) =>
        _outlineKinds.TryGetValue(section, out var kind) ? (OutlineKind)kind : OutlineKind.Code;

    /// <summary>Raised after the blocks were recomputed (tests and status).</summary>
    public event Action? OutliningUpdated;

    /// <summary>Whether the current blocks came from the language server (rather than the text scan).</summary>
    public bool OutlinesFromServer { get; private set; }

    void InitializeOutlining()
    {
        _folding = FoldingManager.Install(Editor.TextArea);
        if (Editor.TextArea.LeftMargins.OfType<FoldingMargin>().FirstOrDefault() is { } margin)
        {
            margin.FoldingMarkerBrush = FoldingMarkerBrush;
            margin.FoldingMarkerBackgroundBrush = EditorBackground;
            margin.SelectedFoldingMarkerBrush = FoldingMarkerSelectedBrush;
            margin.SelectedFoldingMarkerBackgroundBrush = EditorBackground;
        }
        FoldingElementGenerator.TextBrush = new SolidColorBrush(Color.Parse("#9B9B9B"));
        Document.Changed += (_, _) => ScheduleOutlining();
        ApplyOutlines(Outlining.Scan(Document, FilePath));
    }

    /// <summary>Recomputes the blocks after edits settle: Roslyn's folding ranges when it serves the file, else a scan.</summary>
    public void ScheduleOutlining(int delayMs = 500)
    {
        if (_folding is null)
            return;
        _outliningCts?.Cancel();
        var cts = _outliningCts = new CancellationTokenSource();
        _ = UpdateOutliningAsync(delayMs, cts.Token);
    }

    async Task UpdateOutliningAsync(int delayMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct);
            List<NewFolding>? foldings = null;
            if (_lsp is { } lsp)
            {
                FlushChanges();
                var ranges = await lsp.FoldingRangesAsync(FilePath, ct);
                if (ct.IsCancellationRequested)
                    return;
                if (ranges.Count > 0)
                    foldings = Outlining.FromLsp(Document, ranges);
            }
            OutlinesFromServer = foldings is not null;
            ApplyOutlines(foldings ?? Outlining.Scan(Document, FilePath));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            // The server may be restarting; the scanned blocks stay until the next update.
        }
    }

    void ApplyOutlines(List<NewFolding> foldings)
    {
        if (_folding is null)
            return;
        // UpdateFoldings keeps sections whose range did not change, so collapsed blocks stay collapsed while typing.
        _folding.UpdateFoldings(foldings.Where(f => f.EndOffset <= Document.TextLength), -1);
        var kinds = new Dictionary<(int, int), OutlineKind>();
        foreach (var f in foldings)
            kinds[(f.StartOffset, f.EndOffset)] = f.IsDefinition ? OutlineKind.Definition
                : f.Name is "..." or "{...}" or "[...]" ? OutlineKind.Code
                : f.Name.StartsWith("//", StringComparison.Ordinal) || f.Name.StartsWith("/*", StringComparison.Ordinal) ? OutlineKind.Comment
                : OutlineKind.Region;
        foreach (var section in _folding.AllFoldings)
            if (kinds.TryGetValue((section.StartOffset, section.EndOffset), out var kind))
                _outlineKinds.AddOrUpdate(section, kind);
        OutliningUpdated?.Invoke();
    }

    /// <summary>Ctrl+M, Ctrl+M: the block on the caret's line (or the innermost one around the caret).</summary>
    public void ToggleOutliningExpansion()
    {
        if (_folding is null)
            return;
        var caret = Editor.CaretOffset;
        var line = Document.GetLineByOffset(caret);
        var section = _folding.AllFoldings
            .Where(f => f.StartOffset >= line.Offset && f.StartOffset <= line.EndOffset)
            .OrderBy(f => f.StartOffset).ThenByDescending(f => f.EndOffset)
            .FirstOrDefault()
            ?? _folding.GetFoldingsContaining(caret).OrderByDescending(f => f.StartOffset).FirstOrDefault();
        if (section is not null)
            section.IsFolded = !section.IsFolded;
    }

    /// <summary>Ctrl+M, Ctrl+L: expand everything if anything is collapsed, otherwise collapse everything.</summary>
    public void ToggleAllOutlining()
    {
        if (_folding is null)
            return;
        var collapse = !_folding.AllFoldings.Any(f => f.IsFolded);
        foreach (var section in _folding.AllFoldings)
            section.IsFolded = collapse;
    }

    /// <summary>Ctrl+M, Ctrl+O: member bodies and regions collapse, the types around them stay open.</summary>
    public void CollapseToDefinitions()
    {
        if (_folding is null)
            return;
        foreach (var section in _folding.AllFoldings)
            section.IsFolded = KindOf(section) is OutlineKind.Definition or OutlineKind.Region;
        // Keep the caret visible.
        RevealOffset(Editor.CaretOffset);
    }

    /// <summary>Ctrl+M, Ctrl+X.</summary>
    public void ExpandAllOutlining()
    {
        foreach (var section in _folding?.AllFoldings ?? [])
            section.IsFolded = false;
    }

    /// <summary>Ctrl+M, Ctrl+A.</summary>
    public void CollapseAllOutlining()
    {
        foreach (var section in _folding?.AllFoldings ?? [])
            section.IsFolded = true;
    }

    /// <summary>Opens the collapsed blocks hiding an offset (navigation, breakpoints, search results).</summary>
    public void RevealOffset(int offset)
    {
        if (_folding is null)
            return;
        foreach (var section in _folding.GetFoldingsContaining(offset))
            if (section.StartOffset < offset)
                section.IsFolded = false;
    }

    bool HandleOutliningChord(Avalonia.Input.Key key)
    {
        switch (key)
        {
            case Avalonia.Input.Key.M: ToggleOutliningExpansion(); return true;
            case Avalonia.Input.Key.L: ToggleAllOutlining(); return true;
            case Avalonia.Input.Key.O: CollapseToDefinitions(); return true;
            case Avalonia.Input.Key.X: ExpandAllOutlining(); return true;
            case Avalonia.Input.Key.A: CollapseAllOutlining(); return true;
            default: return false;
        }
    }

    void DisposeOutlining()
    {
        _outliningCts?.Cancel();
        if (_folding is not null)
            FoldingManager.Uninstall(_folding);
        _folding = null;
    }
}

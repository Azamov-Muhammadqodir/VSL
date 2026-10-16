using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;
using VSL.Lsp;

namespace VSL.Editor;

/// <summary>Where the user was in a document: caret, the first line on screen, collapsed blocks.</summary>
public sealed record EditorViewState(int CaretLine, int CaretColumn, int FirstVisibleLine, IReadOnlyList<int> Collapsed);

/// <summary>Saving and restoring a document's view state with the solution (see Services.SolutionState).</summary>
public sealed partial class EditorHost
{
    EditorViewState? _pendingView;
    IReadOnlyList<int>? _pendingCollapsed;
    ScrollViewer? _scrollViewer;
    bool _laidOut;

    void InitializeViewState()
    {
        Editor.TemplateApplied += (_, e) => _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        Editor.TextArea.TextView.SizeChanged += (_, e) =>
        {
            if (_laidOut || e.NewSize.Height <= 0)
                return;
            _laidOut = true;
            Dispatcher.UIThread.Post(ApplyPendingScroll, DispatcherPriority.Loaded);
        };
        // Blocks collapsed when the document was saved apply until the user starts editing.
        Document.Changed += (_, _) => _pendingCollapsed = null;
    }

    /// <summary>The current view state; a document never shown yet keeps the one it was restored with.</summary>
    public EditorViewState CaptureViewState()
    {
        var caret = Editor.TextArea.Caret;
        var firstLine = _pendingView?.FirstVisibleLine ?? 1;
        if (_laidOut && _pendingView is null)
        {
            var textView = Editor.TextArea.TextView;
            // Half a pixel in: a scroll offset exactly on a line's top must not round to the line above.
            firstLine = textView.GetDocumentLineByVisualTop(textView.ScrollOffset.Y + 0.5)?.LineNumber ?? 1;
        }
        var collapsed = _pendingCollapsed ?? (_folding?.AllFoldings.Where(f => f.IsFolded)
            .Select(f => Document.GetLineByOffset(f.StartOffset).LineNumber).Distinct().Order().ToList() ?? []);
        return new EditorViewState(caret.Line, caret.Column, firstLine, collapsed);
    }

    /// <summary>Puts the caret back now; the scroll position once the editor is laid out; the collapsed blocks once they exist.</summary>
    public void RestoreViewState(EditorViewState state)
    {
        var line = Math.Clamp(state.CaretLine, 1, Document.LineCount);
        var column = Math.Clamp(state.CaretColumn, 1, Document.GetLineByNumber(line).Length + 1);
        Editor.TextArea.Caret.Position = new TextViewPosition(line, column);
        _pendingView = state;
        _pendingCollapsed = state.Collapsed.Count > 0 ? state.Collapsed : null;
        ApplyPendingCollapsed();
        if (_laidOut)
            Dispatcher.UIThread.Post(ApplyPendingScroll, DispatcherPriority.Loaded);
    }

    /// <summary>Called whenever the blocks are (re)computed: the scan first, then the language server's.</summary>
    void ApplyPendingCollapsed()
    {
        if (_pendingCollapsed is not { } lines || _folding is null)
            return;
        var wanted = lines.ToHashSet();
        foreach (var section in _folding.AllFoldings)
            if (wanted.Contains(Document.GetLineByOffset(section.StartOffset).LineNumber))
                section.IsFolded = true;
        // The language server's blocks replace the scanned ones; keep the request until they are in.
        if (OutlinesFromServer || !RoslynClient.IsSupported(FilePath))
            _pendingCollapsed = null;
    }

    void ApplyPendingScroll()
    {
        if (_pendingView is not { } view)
            return;
        _pendingView = null;
        var line = Math.Clamp(view.FirstVisibleLine, 1, Document.LineCount);
        var textView = Editor.TextArea.TextView;
        textView.EnsureVisualLines();
        var top = textView.GetVisualTopByDocumentLine(line);
        // Editor.ScrollToVerticalOffset does not move an editor the user has not scrolled yet; the viewer does.
        if (_scrollViewer is not null)
            _scrollViewer.Offset = new Avalonia.Vector(_scrollViewer.Offset.X, top);
        else
            Editor.ScrollToVerticalOffset(top);
    }
}

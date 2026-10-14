using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace VSL.Editor;

/// <summary>What the debug adornments need to know about the document they decorate.</summary>
public interface IDebugLineSource
{
    IReadOnlyCollection<int> BreakpointLines { get; }
    bool? IsBreakpointVerified(int line);
    int? ExecutionLine { get; }
    void ToggleBreakpoint(int line);
}

/// <summary>Visual Studio's indicator margin: red breakpoint dots and the yellow current-statement arrow.</summary>
public sealed class BreakpointMargin(IDebugLineSource source) : AbstractMargin
{
    static readonly IBrush MarginBrush = new SolidColorBrush(Color.Parse("#333337"));
    static readonly IBrush BreakpointBrush = new SolidColorBrush(Color.Parse("#E51400"));
    static readonly IPen BreakpointPen = new Pen(new SolidColorBrush(Color.Parse("#E51400")), 1.5);
    static readonly IBrush ArrowBrush = new SolidColorBrush(Color.Parse("#FFE400"));
    static readonly IPen ArrowPen = new Pen(new SolidColorBrush(Color.Parse("#5A5000")), 1);

    protected override Size MeasureOverride(Size availableSize) => new(18, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        InvalidateVisual();
    }

    void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(MarginBrush, new Rect(Bounds.Size));
        var view = TextView;
        if (view is null || !view.VisualLinesValid)
            return;
        var breakpoints = source.BreakpointLines;
        var execution = source.ExecutionLine;
        foreach (var visual in view.VisualLines)
        {
            var line = visual.FirstDocumentLine.LineNumber;
            var top = visual.VisualTop - view.VerticalOffset;
            var height = visual.TextLines.Count > 0 ? visual.TextLines[0].Height : view.DefaultLineHeight;
            var center = new Point(9, top + height / 2);
            if (breakpoints.Contains(line))
            {
                // Hollow until the debugger confirms the breakpoint binds to code, like VS.
                if (source.IsBreakpointVerified(line) == false)
                    dc.DrawEllipse(null, BreakpointPen, center, 5.5, 5.5);
                else
                    dc.DrawEllipse(BreakpointBrush, null, center, 6.5, 6.5);
            }
            if (execution == line)
            {
                var arrow = new StreamGeometry();
                using (var ctx = arrow.Open())
                {
                    // Smaller than the dot so a breakpoint underneath stays visible, as in VS.
                    ctx.BeginFigure(new Point(4.5, center.Y - 2.5), true);
                    ctx.LineTo(new Point(9, center.Y - 2.5));
                    ctx.LineTo(new Point(9, center.Y - 5));
                    ctx.LineTo(new Point(14.5, center.Y));
                    ctx.LineTo(new Point(9, center.Y + 5));
                    ctx.LineTo(new Point(9, center.Y + 2.5));
                    ctx.LineTo(new Point(4.5, center.Y + 2.5));
                    ctx.EndFigure(true);
                }
                dc.DrawGeometry(ArrowBrush, ArrowPen, arrow);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var view = TextView;
        if (view is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var visual = view.GetVisualLineFromVisualTop(e.GetPosition(this).Y + view.VerticalOffset);
        if (visual is null)
            return;
        source.ToggleBreakpoint(visual.FirstDocumentLine.LineNumber);
        e.Handled = true;
    }
}

/// <summary>Line backgrounds: dark red for breakpoints, yellow for the current statement.</summary>
public sealed class DebugLineRenderer(IDebugLineSource source) : IBackgroundRenderer
{
    static readonly IBrush BreakpointLineBrush = new SolidColorBrush(Color.Parse("#7A2626"));
    static readonly IBrush ExecutionLineBrush = new SolidColorBrush(Color.Parse("#66FFE400"));

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid)
            return;
        var breakpoints = source.BreakpointLines;
        var execution = source.ExecutionLine;
        if (breakpoints.Count == 0 && execution is null)
            return;
        foreach (var visual in textView.VisualLines)
        {
            var line = visual.FirstDocumentLine.LineNumber;
            var brush = execution == line ? ExecutionLineBrush
                : breakpoints.Contains(line) && source.IsBreakpointVerified(line) != false ? BreakpointLineBrush
                : null;
            if (brush is null)
                continue;
            var top = visual.VisualTop - textView.VerticalOffset;
            drawingContext.FillRectangle(brush, new Rect(0, top, textView.Bounds.Width, visual.Height));
        }
    }
}

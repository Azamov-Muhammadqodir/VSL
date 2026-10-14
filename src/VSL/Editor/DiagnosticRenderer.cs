using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using VSL.Lsp;

namespace VSL.Editor;

public sealed class DiagnosticSegment : TextSegment
{
    public required LspDiagnostic Diagnostic { get; init; }
}

/// <summary>Draws Visual Studio style squiggles: red errors, green warnings, gray dots for suggestions.</summary>
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    static readonly IPen ErrorPen = new Pen(new SolidColorBrush(Color.Parse("#F14C4C")), 1);
    static readonly IPen WarningPen = new Pen(new SolidColorBrush(Color.Parse("#4CC24C")), 1);
    static readonly IPen InfoPen = new Pen(new SolidColorBrush(Color.Parse("#3794FF")), 1);
    static readonly IBrush HintBrush = new SolidColorBrush(Color.Parse("#A0A0A0"));

    readonly TextSegmentCollection<DiagnosticSegment> _segments;
    readonly TextDocument _document;

    public DiagnosticRenderer(TextDocument document)
    {
        _document = document;
        // Bound to the document so offsets shift as the user types until fresh diagnostics arrive.
        _segments = new TextSegmentCollection<DiagnosticSegment>(document);
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public void SetDiagnostics(IEnumerable<LspDiagnostic> diagnostics)
    {
        _segments.Clear();
        foreach (var d in diagnostics)
        {
            var start = TextPositions.ToOffset(_document, d.Range.Start);
            var end = TextPositions.ToOffset(_document, d.Range.End);
            if (end <= start)
            {
                // Zero-width diagnostics (e.g. "; expected") still need something visible.
                end = Math.Min(start + 1, _document.TextLength);
                if (end <= start && start > 0)
                    start--;
            }
            _segments.Add(new DiagnosticSegment { StartOffset = start, EndOffset = end, Diagnostic = d });
        }
    }

    public IEnumerable<LspDiagnostic> At(int offset) =>
        _segments.FindSegmentsContaining(offset).Select(s => s.Diagnostic);

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_segments.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
            return;
        var first = textView.VisualLines[0].FirstDocumentLine.Offset;
        var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var segment in _segments.FindOverlappingSegments(first, last - first))
        {
            var severity = segment.Diagnostic.Severity;
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                if (severity == DiagnosticSeverity.Hint)
                {
                    DrawDots(drawingContext, rect);
                    continue;
                }
                var pen = severity switch
                {
                    DiagnosticSeverity.Error => ErrorPen,
                    DiagnosticSeverity.Warning => WarningPen,
                    _ => InfoPen,
                };
                DrawSquiggle(drawingContext, rect, pen);
            }
        }
    }

    static void DrawSquiggle(DrawingContext dc, Rect rect, IPen pen)
    {
        const double step = 2.5, height = 2;
        var y = rect.Bottom - 1;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(rect.Left, y), false);
            var up = true;
            for (var x = rect.Left + step; x <= rect.Right + step / 2; x += step, up = !up)
                ctx.LineTo(new Point(x, up ? y - height : y));
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geometry);
    }

    static void DrawDots(DrawingContext dc, Rect rect)
    {
        // VS draws a few gray dots under the start of the span for suggestions.
        var y = rect.Bottom - 1;
        for (var i = 0; i < 4 && rect.Left + i * 3 < rect.Right; i++)
            dc.FillRectangle(HintBrush, new Rect(rect.Left + i * 3, y, 1.5, 1.5));
    }
}

public static class TextPositions
{
    /// <summary>LSP positions are 0-based UTF-16 line/character, which matches AvaloniaEdit's char offsets.</summary>
    public static int ToOffset(TextDocument doc, LspPosition pos)
    {
        if (pos.Line >= doc.LineCount)
            return doc.TextLength;
        var line = doc.GetLineByNumber(pos.Line + 1);
        return line.Offset + Math.Min(pos.Character, line.Length);
    }

    public static LspPosition ToPosition(TextDocument doc, int offset)
    {
        var loc = doc.GetLocation(Math.Clamp(offset, 0, doc.TextLength));
        return new LspPosition(loc.Line - 1, loc.Column - 1);
    }
}

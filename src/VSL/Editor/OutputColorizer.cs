using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using VSL.ViewModels;

namespace VSL.Editor;

/// <summary>Draws an Output channel's ANSI color runs (a program's console log levels, like the terminal shows them).</summary>
public sealed class OutputColorizer(Func<OutputChannel?> channel) : DocumentColorizingTransformer
{
    readonly Dictionary<Color, IBrush> _brushes = [];

    IBrush BrushOf(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
            _brushes[color] = brush = new SolidColorBrush(color).ToImmutable();
        return brush;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (channel() is not { } output || output.Document != CurrentContext.Document || output.Spans is not { Count: > 0 } spans)
            return;
        // Runs are ordered and do not overlap: find the first that ends after the line starts.
        int low = 0, high = spans.Count - 1, first = spans.Count;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (spans[mid].Start + spans[mid].Length > line.Offset)
            {
                first = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }
        for (var i = first; i < spans.Count && spans[i].Start < line.EndOffset; i++)
        {
            var span = spans[i];
            var from = Math.Max(span.Start, line.Offset);
            var to = Math.Min(span.Start + span.Length, line.EndOffset);
            if (from >= to)
                continue;
            ChangeLinePart(from, to, element =>
            {
                if (span.Foreground is { } fg)
                    element.TextRunProperties.SetForegroundBrush(BrushOf(fg));
                if (span.Background is { } bg)
                    element.TextRunProperties.SetBackgroundBrush(BrushOf(bg));
            });
        }
    }
}

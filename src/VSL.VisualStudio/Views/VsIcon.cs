using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace VSL.VisualStudio.Views;

/// <summary>
/// VSL's 16x16 vector icons for the Claude Code panel (the same drawings as VSL's VsIcon). Monochrome glyphs take the
/// inherited Foreground, so they follow the Visual Studio theme.
/// </summary>
public sealed class VsIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(VsIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(VsIcon),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    static readonly Color Accent = (Color)ColorConverter.ConvertFromString("#D97757");
    static readonly Color Green = (Color)ColorConverter.ConvertFromString("#3FB950");
    static readonly ConcurrentDictionary<string, Geometry> Geometries = new();

    static VsIcon()
    {
        WidthProperty.OverrideMetadata(typeof(VsIcon), new FrameworkPropertyMetadata(16.0));
        HeightProperty.OverrideMetadata(typeof(VsIcon), new FrameworkPropertyMetadata(16.0));
        VerticalAlignmentProperty.OverrideMetadata(typeof(VsIcon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
        IsEnabledProperty.OverrideMetadata(typeof(VsIcon), new UIPropertyMetadata(true, (d, _) => ((VsIcon)d).InvalidateVisual()));
    }

    public string? Kind
    {
        get => (string?)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    static Geometry G(string data) => Geometries.GetOrAdd(data, d =>
    {
        var geometry = Geometry.Parse(d);
        geometry.Freeze();
        return geometry;
    });

    protected override void OnRender(DrawingContext dc)
    {
        if (Kind is not { } kind || ActualWidth <= 0)
            return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 16;
        dc.PushTransform(new TranslateTransform((ActualWidth - 16 * scale) / 2, (ActualHeight - 16 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (!IsEnabled)
            dc.PushOpacity(0.4);
        var fg = (Foreground as SolidColorBrush)?.Color ?? Colors.Gray;
        Draw(dc, kind, fg);
    }

    static void Fill(DrawingContext dc, string data, Color color) => dc.DrawGeometry(new SolidColorBrush(color), null, G(data));

    static void Stroke(DrawingContext dc, string data, Color color, double width = 1.3) =>
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, G(data));

    static void Draw(DrawingContext dc, string kind, Color fg)
    {
        switch (kind)
        {
            case "Claude":
                // A warm eight-ray spark.
                Stroke(dc, "M8,1.5 L8,14.5 M1.5,8 L14.5,8 M3.4,3.4 L12.6,12.6 M12.6,3.4 L3.4,12.6", Accent, 2.1);
                Fill(dc, "M8,5.6 A2.4,2.4 0 1 0 8,10.4 A2.4,2.4 0 1 0 8,5.6 Z", Accent);
                break;
            case "Chevron":
                Stroke(dc, "M4.5,6.5 L8,10 L11.5,6.5", fg, 1.3);
                break;
            case "Close":
                Stroke(dc, "M4.5,4.5 L11.5,11.5 M11.5,4.5 L4.5,11.5", fg, 1.3);
                break;
            case "Plus":
                Stroke(dc, "M8,3 L8,13 M3,8 L13,8", fg, 1.4);
                break;
            case "Ok":
                Fill(dc, "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 Z M4.4,8.3 L6.9,10.8 L11.7,5.7 L10.6,4.7 L6.9,8.7 L5.5,7.2 Z", Green);
                break;
            case "History":
                Stroke(dc, "M2.6,8 A5.4,5.4 0 1 0 4.2,4.2 M8,4.8 L8,8.3 L10.4,9.8", fg, 1.3);
                Fill(dc, "M1.4,2.6 L4.9,2.9 L4.4,6.3 Z", fg);
                break;
            case "Send":
                Stroke(dc, "M8,13 L8,3.5 M3.8,7.6 L8,3.4 L12.2,7.6", fg, 1.8);
                break;
            case "Copy":
                Stroke(dc, "M5.5,5.5 L13.5,5.5 L13.5,14.5 L5.5,14.5 Z M3.5,11 L2.5,11 L2.5,1.5 L10.5,1.5 L10.5,2.5", fg, 1.1);
                break;
            case "Eye":
                Stroke(dc, "M1,8 C3,4.3 13,4.3 15,8 C13,11.7 3,11.7 1,8 Z", fg, 1.1);
                Fill(dc, "M8,5.9 A2.1,2.1 0 1 0 8,10.1 A2.1,2.1 0 1 0 8,5.9 Z", fg);
                break;
            case "EyeOff":
                Stroke(dc, "M1,8 C3,4.3 13,4.3 15,8 C13,11.7 3,11.7 1,8 Z M2.5,13.5 L13.5,2.5", fg, 1.1);
                break;
            case "Check":
                Stroke(dc, "M2.5,8.5 L6.2,12 L13.5,4", fg, 1.6);
                break;
            case "Hand":
                // An open hand: stop and ask.
                Stroke(dc, "M5,8.5 L5,3.2 C5,2.4 6.4,2.4 6.4,3.2 L6.4,7.5 M6.4,7.5 L6.4,2.2 C6.4,1.4 7.8,1.4 7.8,2.2 L7.8,7.5 M7.8,7.5 L7.8,2.8 C7.8,2 9.2,2 9.2,2.8 L9.2,7.8 M9.2,7.8 L9.2,4.2 C9.2,3.4 10.6,3.4 10.6,4.2 L10.6,9.5 C10.6,12.5 9,14.5 6.8,14.5 C5.2,14.5 4.4,13.6 3.6,12.2 L2.2,9.6 C1.8,8.8 2.9,8.1 3.5,8.8 L5,10.5", fg, 1.1);
                break;
            case "CodeBrackets":
                Stroke(dc, "M5.5,4 L2,8 L5.5,12 M10.5,4 L14,8 L10.5,12 M9,3 L7,13", fg, 1.2);
                break;
            case "PlanDoc":
                Stroke(dc, "M3.5,1.5 L12.5,1.5 L12.5,14.5 L3.5,14.5 Z M5.5,5 L10.5,5 M5.5,8 L10.5,8 M5.5,11 L8.5,11", fg, 1.1);
                break;
            case "Bolt":
                Stroke(dc, "M9,1.5 L3.5,9 L8,9 L7,14.5 L12.5,7 L8,7 Z", fg, 1.1);
                break;
            case "Gauge":
                Stroke(dc, "M2,11.5 A6,6 0 1 1 14,11.5 M8,11 L11,6.5", fg, 1.3);
                Fill(dc, "M8,9.7 A1.3,1.3 0 1 0 8,12.3 A1.3,1.3 0 1 0 8,9.7 Z", fg);
                break;
            case "Plug":
                Stroke(dc, "M5.5,1.5 L5.5,4.5 M10.5,1.5 L10.5,4.5 M3.5,4.5 L12.5,4.5 L12.5,7 C12.5,9.5 10.5,11 8,11 C5.5,11 3.5,9.5 3.5,7 Z M8,11 L8,14.5", fg, 1.2);
                break;
            case "Compact":
                // Two arrows pressing a block together.
                Stroke(dc, "M8,1.5 L8,6 M5.5,3.8 L8,6.3 L10.5,3.8 M8,14.5 L8,10 M5.5,12.2 L8,9.7 L10.5,12.2 M2.5,8 L13.5,8", fg, 1.2);
                break;
            case "Globe":
                Stroke(dc, "M8,1.5 A6.5,6.5 0 1 0 8,14.5 A6.5,6.5 0 1 0 8,1.5 Z M1.5,8 L14.5,8 M8,1.5 C5.5,4 5.5,12 8,14.5 C10.5,12 10.5,4 8,1.5", fg, 1.1);
                break;
            case "User":
                // A person: head and shoulders.
                Stroke(dc, "M8,2 A2.9,2.9 0 1 0 8,7.8 A2.9,2.9 0 1 0 8,2 Z M2.5,14.5 C2.5,11.3 5,9.6 8,9.6 C11,9.6 13.5,11.3 13.5,14.5", fg, 1.2);
                break;
            case "Attach":
                // A paper clip.
                Stroke(dc, "M11.5,5.5 L6,11 C5,12 3.6,12 2.8,11.2 C2,10.4 2,9 3,8 L9,2 C9.8,1.2 11.2,1.2 12,2 C12.8,2.8 12.8,4.2 12,5 L6.3,10.7 C5.9,11.1 5.3,11.1 4.9,10.7 C4.5,10.3 4.5,9.7 4.9,9.3 L9.5,4.7", fg, 1.1);
                break;
            default:
                // A page with a folded corner.
                Stroke(dc, "M3.5,1.5 L9.5,1.5 L12.5,4.5 L12.5,14.5 L3.5,14.5 Z M9.5,1.5 L9.5,4.5 L12.5,4.5", fg, 1.0);
                break;
        }
    }
}

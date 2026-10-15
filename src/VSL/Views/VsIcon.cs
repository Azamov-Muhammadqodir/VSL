using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VSL.Views;

/// <summary>
/// 16x16 vector icons in the style of Visual Studio 2022's dark theme (drawn for VSL, not copied).
/// Monochrome glyphs take the inherited Foreground; colored ones use VS's icon palette.
/// Disabled controls render their icons dimmed, like VS toolbars.
/// </summary>
public sealed class VsIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<VsIcon, string?>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<VsIcon>();

    static VsIcon()
    {
        AffectsRender<VsIcon>(KindProperty, ForegroundProperty, IsEffectivelyEnabledProperty);
        WidthProperty.OverrideDefaultValue<VsIcon>(16);
        HeightProperty.OverrideDefaultValue<VsIcon>(16);
        VerticalAlignmentProperty.OverrideDefaultValue<VsIcon>(Avalonia.Layout.VerticalAlignment.Center);
    }

    /// <summary>Every icon kind, for the gallery test.</summary>
    public static IReadOnlyList<string> AllKinds { get; } =
    [
        "Open", "Save", "SaveAll", "Undo", "Redo", "Build", "Chevron", "Start", "StartNoDebug", "Stop", "Pause", "Restart",
        "StepInto", "StepOver", "StepOut", "RunAll", "DebugTests", "RunFailed", "Breakpoint", "Sync", "Refresh", "CollapseAll", "Search",
        "Close", "Pin", "Plus", "Minus", "Trash", "Discard", "Fetch", "Pull", "Push", "GitSync", "Branch", "Pencil",
        "Method", "Field", "Local", "Property", "Class", "Struct", "Interface", "Enum", "EnumMember", "Event", "Namespace",
        "Keyword", "Snippet", "Constant", "TypeParameter", "Operator", "ClearAll",
        "Error", "Warning", "Info", "Ok", "Pending", "Running", "Solution", "Folder", "FolderOpen", "PropertiesFolder", "WebFolder", "Project",
        "Dependencies", "Package", "Framework", "cs", "razor", "css", "js", "json", "xml", "html", "sql", "md", "proto",
        "yaml", "image", "binary", "file", "CloneRepository", "OpenProject", "NewProject", "FsProject", "VbProject", "ArrowRight",
        "Claude", "History", "Send", "Copy", "Eye", "EyeOff",
    ];

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    // VS 2022 dark icon palette.
    static readonly Color Blue = Color.Parse("#75BEFF");
    static readonly Color Green = Color.Parse("#89D185");
    static readonly Color Red = Color.Parse("#F48771");
    static readonly Color ErrorRed = Color.Parse("#F14C4C");
    static readonly Color Yellow = Color.Parse("#CCA700");
    static readonly Color Folder = Color.Parse("#DCB67A");
    static readonly Color FolderDark = Color.Parse("#B8935A");
    static readonly Color Purple = Color.Parse("#B180D7");
    static readonly Color Gray = Color.Parse("#C5C5C5");
    static readonly Color Page = Color.Parse("#C5C5C5");

    static readonly ConcurrentDictionary<string, Geometry> Geometries = new();

    static Geometry G(string data) => Geometries.GetOrAdd(data, Geometry.Parse);

    public override void Render(DrawingContext dc)
    {
        if (Kind is not { } kind || Bounds.Width <= 0)
            return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 16;
        var offset = new Point((Bounds.Width - 16 * scale) / 2, (Bounds.Height - 16 * scale) / 2);
        using var transform = dc.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y));
        using var opacity = IsEffectivelyEnabled ? default(DrawingContext.PushedState?) : dc.PushOpacity(0.4);
        var fg = (Foreground as ISolidColorBrush)?.Color ?? Gray;
        Draw(dc, kind, fg);
    }

    static void Fill(DrawingContext dc, string data, Color color) => dc.DrawGeometry(new SolidColorBrush(color), null, G(data));

    static void Stroke(DrawingContext dc, string data, Color color, double width = 1.3, PenLineCap cap = PenLineCap.Round) =>
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), width, lineCap: cap, lineJoin: PenLineJoin.Round), G(data));

    /// <summary>A colored rounded label with 1-3 letters, as on VS file and project icons.</summary>
    static void Label(DrawingContext dc, string text, Color background, Rect rect)
    {
        dc.DrawRectangle(new SolidColorBrush(background), null, rect, 1.5, 1.5);
        var size = text.Length > 2 ? 4.6 : text.Length == 2 ? 5.6 : 7;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), size, Brushes.White);
        dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    /// <summary>A page with a folded corner and a colored label, like VS's file icons.</summary>
    static void FileIcon(DrawingContext dc, string? text, Color color)
    {
        Fill(dc, "M3,0.5 L9.5,0.5 L13.5,4.5 L13.5,15.5 L3,15.5 Z M4,1.5 L4,14.5 L12.5,14.5 L12.5,5.5 L8.5,5.5 L8.5,1.5 Z", Page);
        Fill(dc, "M9.5,1.9 L12.1,4.5 L9.5,4.5 Z", Page);
        if (text is not null)
            Label(dc, text, color, new Rect(0, 7.5, 11, 8));
    }

    static void FolderIcon(DrawingContext dc, bool open)
    {
        if (open)
        {
            Fill(dc, "M0.5,2.5 L5.5,2.5 L7,4 L13.5,4 L13.5,6.5 L3.5,6.5 L0.5,13 Z", FolderDark);
            Fill(dc, "M3.5,7 L15.5,7 L12.5,13.5 L0.5,13.5 Z", Folder);
        }
        else
        {
            Fill(dc, "M0.5,2.5 L5.5,2.5 L7,4 L15.5,4 L15.5,13.5 L0.5,13.5 Z", Folder);
            Fill(dc, "M0.5,5.5 L15.5,5.5 L15.5,6.3 L0.5,6.3 Z", FolderDark);
        }
    }

    static void Floppy(DrawingContext dc, Color color, double x, double y, double s)
    {
        using var t = dc.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(x, y));
        Fill(dc, "M1.5,1.5 L12,1.5 L14.5,4 L14.5,14.5 L1.5,14.5 Z M4.5,2.5 L10.5,2.5 L10.5,5.5 L4.5,5.5 Z M3.5,8.5 L12.5,8.5 L12.5,13.5 L3.5,13.5 Z", color);
        Fill(dc, "M4.5,9.5 L11.5,9.5 L11.5,12.5 L4.5,12.5 Z", Color.Parse("#E8E8E8"));
    }

    /// <summary>VS's isometric cube (methods purple, fields blue).</summary>
    static void Cube(DrawingContext dc, Color front, Color side, Color top)
    {
        Fill(dc, "M1.5,4.8 L8,8.2 L8,15 L1.5,11.6 Z", front);
        Fill(dc, "M8,8.2 L14.5,4.8 L14.5,11.6 L8,15 Z", side);
        Fill(dc, "M1.5,4.8 L8,1.4 L14.5,4.8 L8,8.2 Z", top);
    }

    static void Draw(DrawingContext dc, string kind, Color fg)
    {
        switch (kind)
        {
            // ---- Standard toolbar --------------------------------------------------------------------------
            case "Open":
                FolderIcon(dc, open: true);
                break;
            case "Save":
                Floppy(dc, Blue, 0, 0, 1);
                break;
            case "SaveAll":
                Floppy(dc, Color.Parse("#4A86C5"), 4, 0, 0.75);
                Floppy(dc, Blue, 0, 4, 0.75);
                break;
            case "Undo":
                Stroke(dc, "M5,6.5 L10.5,6.5 C12.8,6.5 14,8.2 14,10 C14,11.8 12.8,13.5 10.5,13.5 L6.5,13.5", fg, 1.6);
                Fill(dc, "M1.5,6.5 L5.8,2.8 L5.8,10.2 Z", fg);
                break;
            case "Redo":
                Stroke(dc, "M11,6.5 L5.5,6.5 C3.2,6.5 2,8.2 2,10 C2,11.8 3.2,13.5 5.5,13.5 L9.5,13.5", fg, 1.6);
                Fill(dc, "M14.5,6.5 L10.2,2.8 L10.2,10.2 Z", fg);
                break;
            case "Build":
                Fill(dc, "M1.5,9.5 L3.5,9.5 L3.5,12.5 L12.5,12.5 L12.5,9.5 L14.5,9.5 L14.5,14.5 L1.5,14.5 Z", Gray);
                Fill(dc, "M6.8,1.5 L9.2,1.5 L9.2,6.5 L11.8,6.5 L8,10.5 L4.2,6.5 L6.8,6.5 Z", Blue);
                break;
            case "Chevron":
                Stroke(dc, "M4.5,6.5 L8,10 L11.5,6.5", fg, 1.3);
                break;

            // ---- Debug ---------------------------------------------------------------------------------------
            case "Start":
                Fill(dc, "M4,2 L13.5,8 L4,14 Z", Green);
                break;
            case "StartNoDebug":
                Stroke(dc, "M4.5,2.8 L12.8,8 L4.5,13.2 Z", Green, 1.4, PenLineCap.Flat);
                break;
            case "Stop":
                Fill(dc, "M3.5,3.5 L12.5,3.5 L12.5,12.5 L3.5,12.5 Z", Red);
                break;
            case "Pause":
                Fill(dc, "M3.5,2.5 L6.5,2.5 L6.5,13.5 L3.5,13.5 Z M9.5,2.5 L12.5,2.5 L12.5,13.5 L9.5,13.5 Z", Blue);
                break;
            case "Restart":
                Stroke(dc, "M13,8.5 A5,5 0 1 1 8,3.5", Green, 1.8);
                Fill(dc, "M7.5,0.5 L11.5,3.5 L7.5,6.5 Z", Green);
                break;
            case "StepInto":
                Stroke(dc, "M8,1.5 L8,8", Blue, 1.8);
                Fill(dc, "M4.5,6.5 L11.5,6.5 L8,10.5 Z", Blue);
                Fill(dc, "M6.3,13 A1.7,1.7 0 1 0 9.7,13 A1.7,1.7 0 1 0 6.3,13 Z", Blue);
                break;
            case "StepOut":
                Stroke(dc, "M8,10 L8,4", Blue, 1.8);
                Fill(dc, "M4.5,5 L11.5,5 L8,1 Z", Blue);
                Fill(dc, "M6.3,13.5 A1.7,1.7 0 1 0 9.7,13.5 A1.7,1.7 0 1 0 6.3,13.5 Z", Blue);
                break;
            case "StepOver":
                Stroke(dc, "M2.5,9 C2.5,3 12,2.5 12.5,7.5", Blue, 1.8);
                Fill(dc, "M9.5,6.5 L15,6.5 L12.5,10.5 Z", Blue);
                Fill(dc, "M6.3,13 A1.7,1.7 0 1 0 9.7,13 A1.7,1.7 0 1 0 6.3,13 Z", Blue);
                break;
            case "RunAll":
                Fill(dc, "M1,2.5 L8,8 L1,13.5 Z M8,2.5 L15,8 L8,13.5 Z", Green);
                break;
            case "DebugTests":
                Fill(dc, "M1,2 L10.5,8 L1,14 Z", Green);
                Fill(dc, "M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5 Z", Color.Parse("#E51400"));
                break;
            case "RunFailed":
                Fill(dc, "M1,2.5 L9,8 L1,13.5 Z", Green);
                Fill(dc, "M12,6 A4,4 0 1 0 12,14 A4,4 0 1 0 12,6 Z M10.3,8.3 L12,10 L13.7,8.3 L14.4,9 L12.7,10.7 L12.7,9.3 L14.4,11 L13.7,11.7 L12,10 L10.3,11.7 L9.6,11 L11.3,9.3 L9.6,9 Z", ErrorRed);
                break;
            case "Breakpoint":
                Fill(dc, "M8,2.5 A5.5,5.5 0 1 0 8,13.5 A5.5,5.5 0 1 0 8,2.5 Z", Color.Parse("#E51400"));
                break;

            // ---- Tool windows --------------------------------------------------------------------------------
            case "Sync":
                Stroke(dc, "M2,5 L13,5 M10.5,2.5 L13,5 L10.5,7.5 M14,11 L3,11 M5.5,8.5 L3,11 L5.5,13.5", fg, 1.3);
                break;
            case "Refresh":
                Stroke(dc, "M13.2,7 A5.2,5.2 0 1 0 12,11.2", Blue, 1.6);
                Fill(dc, "M10.5,6.5 L15,6.5 L15,2 Z", Blue);
                break;
            case "CollapseAll":
                Stroke(dc, "M5,2 L14,2 L14,11", fg, 1.1, PenLineCap.Square);
                Stroke(dc, "M2,5 L11,5 L11,14 L2,14 Z", fg, 1.1, PenLineCap.Square);
                Stroke(dc, "M4.5,9.5 L8.5,9.5", fg, 1.3);
                break;
            case "Search":
                Stroke(dc, "M14,6.5 A4,4 0 1 0 6,6.5 A4,4 0 1 0 14,6.5 Z M7.2,9.3 L2,14.5", fg, 1.4);
                break;
            case "Close":
                Stroke(dc, "M4.5,4.5 L11.5,11.5 M11.5,4.5 L4.5,11.5", fg, 1.3);
                break;
            case "Pin":
                Stroke(dc, "M5.5,2.5 L10.5,2.5 M6.5,2.5 L6.5,8 M9.5,2.5 L9.5,8 M4,8.5 L12,8.5 M8,8.5 L8,14", fg, 1.2);
                break;
            case "Plus":
                Stroke(dc, "M8,3 L8,13 M3,8 L13,8", fg, 1.4);
                break;
            case "Minus":
                Stroke(dc, "M3,8 L13,8", fg, 1.4);
                break;
            case "Trash":
                Stroke(dc, "M2.5,4.5 L13.5,4.5 M6,4.5 L6,2.5 L10,2.5 L10,4.5 M4,4.5 L5,14 L11,14 L12,4.5 M6.8,7 L6.8,11.5 M9.2,7 L9.2,11.5", fg, 1.2);
                break;
            case "Discard":
                Stroke(dc, "M5,5.5 L10.5,5.5 C12.6,5.5 14,7.2 14,9.2 C14,11.3 12.6,13 10.5,13 L6,13", fg, 1.4);
                Fill(dc, "M2,5.5 L5.5,2.5 L5.5,8.5 Z", fg);
                break;

            // ---- Git -----------------------------------------------------------------------------------------
            case "Fetch":
                Stroke(dc, "M8,1.5 L8,3 M8,5 L8,6.5 M8,8.5 L8,9.5", Blue, 1.6, PenLineCap.Flat);
                Fill(dc, "M4.5,9 L11.5,9 L8,12.5 Z", Blue);
                Stroke(dc, "M3,14.5 L13,14.5", fg, 1.3);
                break;
            case "Pull":
                Stroke(dc, "M8,1.5 L8,9.5", Blue, 1.8);
                Fill(dc, "M4.5,8.5 L11.5,8.5 L8,12.5 Z", Blue);
                Stroke(dc, "M3,14.5 L13,14.5", fg, 1.3);
                break;
            case "Push":
                Stroke(dc, "M8,12.5 L8,5", Blue, 1.8);
                Fill(dc, "M4.5,5.5 L11.5,5.5 L8,1.5 Z", Blue);
                Stroke(dc, "M3,14.5 L13,14.5", fg, 1.3);
                break;
            case "GitSync":
                Stroke(dc, "M5,13 L5,4", Blue, 1.6);
                Fill(dc, "M2,5 L8,5 L5,1.5 Z", Blue);
                Stroke(dc, "M11,3 L11,12", Blue, 1.6);
                Fill(dc, "M8,11 L14,11 L11,14.5 Z", Blue);
                break;
            case "Branch":
                Stroke(dc, "M5,4.5 L5,11.5 M11,5.5 C11,9 5,7.5 5,11", fg, 1.2);
                Stroke(dc, "M5,1.8 A1.6,1.6 0 1 0 5,5 A1.6,1.6 0 1 0 5,1.8 Z M11,2.6 A1.6,1.6 0 1 0 11,5.8 A1.6,1.6 0 1 0 11,2.6 Z M5,11 A1.6,1.6 0 1 0 5,14.2 A1.6,1.6 0 1 0 5,11 Z", fg, 1.1);
                break;
            case "Pencil":
                Stroke(dc, "M2.5,13.5 L3.2,10.3 L10.8,2.7 L13.3,5.2 L5.7,12.8 Z M9.3,4.2 L11.8,6.7", fg, 1.1);
                break;

            // ---- Code elements (IntelliSense) ------------------------------------------------------------------
            case "Method":
                Cube(dc, Color.Parse("#B180D7"), Color.Parse("#8A5DB0"), Color.Parse("#D4B7EC"));
                break;
            case "Field":
                Cube(dc, Color.Parse("#75BEFF"), Color.Parse("#3E8DD6"), Color.Parse("#B3DCFF"));
                break;
            case "Local":
                Stroke(dc, "M3,5.5 L13,5.5 L13,13.5 L3,13.5 Z", Blue, 1.4);
                Stroke(dc, "M5.5,8.5 L10.5,8.5 M5.5,11 L8.5,11", Blue, 1.1);
                break;
            case "Property":
                Stroke(dc, "M2.5,13.5 L8,8", Gray, 2.4);
                Fill(dc, "M10.8,1.6 A4,4 0 1 0 14.4,5.2 L12.2,5.6 L10.4,3.8 Z", Gray);
                break;
            case "Class":
                Fill(dc, "M1.5,1.5 L7,1.5 L7,7 L1.5,7 Z M9,6 L14.5,6 L14.5,11.5 L9,11.5 Z M3,9.5 L7.5,9.5 L7.5,14.5 L3,14.5 Z", Color.Parse("#E8AB53"));
                Stroke(dc, "M4.25,7 L4.25,9.5 M7,4.25 L11.75,4.25 L11.75,6", Color.Parse("#E8AB53"), 1.1);
                break;
            case "Struct":
                Fill(dc, "M1.5,3 L7,3 L7,8.5 L1.5,8.5 Z M9,3 L14.5,3 L14.5,8.5 L9,8.5 Z M5,10 L11,10 L11,14.5 L5,14.5 Z", Blue);
                break;
            case "Interface":
                Stroke(dc, "M11,2.5 A3.5,3.5 0 1 0 11,9.5 A3.5,3.5 0 1 0 11,2.5 Z", Blue, 1.8);
                Stroke(dc, "M7.6,6 L1.5,6", Blue, 1.8);
                break;
            case "Enum":
                Fill(dc, "M1.5,2.5 L9,2.5 L9,7.5 L1.5,7.5 Z", Color.Parse("#E8AB53"));
                Stroke(dc, "M4,10 L14.5,10 M4,13 L14.5,13", Color.Parse("#E8AB53"), 1.5);
                break;
            case "EnumMember":
                Fill(dc, "M1.5,6 L14.5,6 L14.5,11 L1.5,11 Z", Blue);
                Stroke(dc, "M4,8.5 L12,8.5", Color.Parse("#1E1E1E"), 1.2);
                break;
            case "Event":
                Fill(dc, "M9,1 L3,9 L7.5,9 L6,15 L13,6 L8.5,6 Z", Color.Parse("#E8AB53"));
                break;
            case "Namespace":
                Stroke(dc, "M6,2 C3.5,2 4.5,7 2,8 C4.5,9 3.5,14 6,14 M10,2 C12.5,2 11.5,7 14,8 C11.5,9 12.5,14 10,14", Gray, 1.3);
                break;
            case "Keyword":
                Stroke(dc, "M2,3.5 L14,3.5 L14,12.5 L2,12.5 Z", Gray, 1.1);
                Stroke(dc, "M4.5,6.5 L11.5,6.5 M4.5,9.5 L9,9.5", Gray, 1.2);
                break;
            case "Snippet":
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Gray), 1.1, new DashStyle([2, 1.5], 0)), G("M2,3.5 L14,3.5 L14,12.5 L2,12.5 Z"));
                Stroke(dc, "M4.5,8 L11.5,8", Gray, 1.2);
                break;
            case "Constant":
                Stroke(dc, "M2,3.5 L14,3.5 L14,12.5 L2,12.5 Z", Gray, 1.1);
                Stroke(dc, "M5,6.8 L11,6.8 M5,9.6 L11,9.6", Gray, 1.3);
                break;
            case "TypeParameter":
                Stroke(dc, "M4.5,3 L1.5,8 L4.5,13 M11.5,3 L14.5,8 L11.5,13 M5.5,4.5 L10.5,4.5 M8,4.5 L8,12", Gray, 1.2);
                break;
            case "Operator":
                Stroke(dc, "M5,2 L5,8 M2,5 L8,5 M9,12 L14,12", Gray, 1.4);
                break;
            case "ClearAll":
                Stroke(dc, "M2,4 L9,4 M2,7.5 L7,7.5 M2,11 L6,11", fg, 1.3);
                Stroke(dc, "M9.5,8.5 L14,13 M14,8.5 L9.5,13", Color.Parse("#F48771"), 1.4);
                break;

            // ---- Status / severity ---------------------------------------------------------------------------
            case "Error":
                Fill(dc, "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 Z M5.3,4.3 L8,7 L10.7,4.3 L11.7,5.3 L9,8 L11.7,10.7 L10.7,11.7 L8,9 L5.3,11.7 L4.3,10.7 L7,8 L4.3,5.3 Z", ErrorRed);
                break;
            case "Warning":
                Fill(dc, "M8,1 L15.5,14.5 L0.5,14.5 Z M7.2,5.2 L8.8,5.2 L8.8,10 L7.2,10 Z M7.2,11.2 L8.8,11.2 L8.8,12.8 L7.2,12.8 Z", Yellow);
                break;
            case "Info":
                Fill(dc, "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 Z M7.2,6.5 L8.8,6.5 L8.8,12 L7.2,12 Z M7.2,3.8 L8.8,3.8 L8.8,5.4 L7.2,5.4 Z", Color.Parse("#3794FF"));
                break;
            case "Pending":
                Stroke(dc, "M8,2 A6,6 0 1 0 8,14 A6,6 0 1 0 8,2 Z", Color.Parse("#8A8A8A"), 1.6);
                break;
            case "Running":
                Stroke(dc, "M8,2 A6,6 0 1 0 8,14 A6,6 0 1 0 8,2 Z", Color.Parse("#3B4A5A"), 1.8);
                Stroke(dc, "M8,2 A6,6 0 0 1 14,8", Color.Parse("#3794FF"), 1.8);
                break;
            case "Ok":
                Fill(dc, "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 Z M4.4,8.3 L6.9,10.8 L11.7,5.7 L10.6,4.7 L6.9,8.7 L5.5,7.2 Z", Green);
                break;

            // ---- Solution Explorer ---------------------------------------------------------------------------
            case "Solution":
                Fill(dc, "M1.5,2.5 L14.5,2.5 L14.5,13.5 L1.5,13.5 Z M2.5,5.5 L2.5,12.5 L13.5,12.5 L13.5,5.5 Z", Purple);
                Fill(dc, "M4,7 L7.5,7 L7.5,11 L4,11 Z M8.5,7 L12,7 L12,11 L8.5,11 Z", Purple);
                break;
            case "SolutionFolder":
            case "Folder":
            case "DependencyGroup":
                FolderIcon(dc, open: false);
                break;
            case "FolderOpen":
                FolderIcon(dc, open: true);
                break;
            case "PropertiesFolder":
                FolderIcon(dc, open: false);
                Stroke(dc, "M8.5,13 L12.5,9 M11.5,8 A2,2 0 1 0 13.5,10", Color.Parse("#6B5A3A"), 1.4);
                break;
            case "WebFolder":
                FolderIcon(dc, open: false);
                Stroke(dc, "M11,7.5 A3,3 0 1 0 11,13.5 A3,3 0 1 0 11,7.5 Z M8,10.5 L14,10.5 M11,7.5 C9.5,9 9.5,12 11,13.5 C12.5,12 12.5,9 11,7.5", Color.Parse("#2B6CB0"), 0.9);
                break;
            case "Project":
            case "ProjectReference":
                Fill(dc, "M1.5,1.5 L14.5,1.5 L14.5,14.5 L1.5,14.5 Z M2.5,2.5 L2.5,13.5 L13.5,13.5 L13.5,2.5 Z", Color.Parse("#388A34"));
                Label(dc, "C#", Color.Parse("#388A34"), new Rect(3.5, 3.5, 9, 9));
                break;
            case "FsProject":
                Fill(dc, "M1.5,1.5 L14.5,1.5 L14.5,14.5 L1.5,14.5 Z M2.5,2.5 L2.5,13.5 L13.5,13.5 L13.5,2.5 Z", Color.Parse("#378BBA"));
                Label(dc, "F#", Color.Parse("#378BBA"), new Rect(3.5, 3.5, 9, 9));
                break;
            case "VbProject":
                Fill(dc, "M1.5,1.5 L14.5,1.5 L14.5,14.5 L1.5,14.5 Z M2.5,2.5 L2.5,13.5 L13.5,13.5 L13.5,2.5 Z", Color.Parse("#00539C"));
                Label(dc, "VB", Color.Parse("#00539C"), new Rect(3.5, 3.5, 9, 9));
                break;

            // ---- Start window --------------------------------------------------------------------------------
            case "CloneRepository":
                // A repository box with a download arrow.
                Stroke(dc, "M2.5,6.5 L2.5,14.5 L13.5,14.5 L13.5,6.5", Gray, 1.1);
                Stroke(dc, "M1.5,6.5 L5,6.5 M11,6.5 L14.5,6.5", Gray, 1.1);
                Fill(dc, "M6.9,1 L9.1,1 L9.1,7 L11.4,7 L8,10.6 L4.6,7 L6.9,7 Z", Blue);
                break;
            case "OpenProject":
                FolderIcon(dc, open: true);
                Fill(dc, "M8.5,8.5 L15.5,8.5 L15.5,15.5 L8.5,15.5 Z", Color.Parse("#252526"));
                Fill(dc, "M9,9 L15,9 L15,15 L9,15 Z M9.8,10.8 L9.8,14.2 L14.2,14.2 L14.2,10.8 Z", Purple);
                Fill(dc, "M10.6,11.6 L11.8,11.6 L11.8,13.4 L10.6,13.4 Z M12.2,11.6 L13.4,11.6 L13.4,13.4 L12.2,13.4 Z", Purple);
                break;
            case "NewProject":
                Fill(dc, "M2,0.5 L8.5,0.5 L12.5,4.5 L12.5,9 L11.5,9 L11.5,5.5 L7.5,5.5 L7.5,1.5 L3,1.5 L3,14.5 L8,14.5 L8,15.5 L2,15.5 Z", Page);
                Fill(dc, "M8.5,1.9 L11.1,4.5 L8.5,4.5 Z", Page);
                Stroke(dc, "M12.5,10 L12.5,15.5 M9.75,12.75 L15.25,12.75", Blue, 1.6);
                break;
            // ---- Claude Code panel ---------------------------------------------------------------------------
            case "Claude":
                // A warm eight-ray spark.
                Stroke(dc, "M8,1.5 L8,14.5 M1.5,8 L14.5,8 M3.4,3.4 L12.6,12.6 M12.6,3.4 L3.4,12.6", Color.Parse("#D97757"), 2.1);
                Fill(dc, "M8,5.6 A2.4,2.4 0 1 0 8,10.4 A2.4,2.4 0 1 0 8,5.6 Z", Color.Parse("#D97757"));
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
            case "ArrowRight":
                Stroke(dc, "M2.5,8 L13,8 M9,4 L13,8 L9,12", fg, 1.3);
                break;
            case "Dependencies":
                Stroke(dc, "M2.5,3.5 L7.5,3.5 L7.5,7.5 L2.5,7.5 Z M8.5,8.5 L13.5,8.5 L13.5,12.5 L8.5,12.5 Z M5,7.5 L5,10.5 L8.5,10.5", Gray, 1.1);
                break;
            case "Package":
                Fill(dc, "M8,1 L14.5,4.5 L14.5,11.5 L8,15 L1.5,11.5 L1.5,4.5 Z M8,2.2 L2.9,5 L8,7.8 L13.1,5 Z", Color.Parse("#2F7EC7"));
                break;
            case "Framework":
                Label(dc, ".N", Color.Parse("#5C2D91"), new Rect(1.5, 1.5, 13, 13));
                break;
            case "cs": FileIcon(dc, "C#", Color.Parse("#388A34")); break;
            case "razor": FileIcon(dc, "@", Color.Parse("#8E44AD")); break;
            case "css": FileIcon(dc, "#", Color.Parse("#1572B6")); break;
            case "js": FileIcon(dc, "JS", Color.Parse("#B8A12E")); break;
            case "json": FileIcon(dc, "{}", Color.Parse("#8A6D3B")); break;
            case "xml": FileIcon(dc, "<>", Color.Parse("#C66B12")); break;
            case "html": FileIcon(dc, "<>", Color.Parse("#E44D26")); break;
            case "sql": FileIcon(dc, "SQL", Color.Parse("#A33")); break;
            case "md": FileIcon(dc, "M", Color.Parse("#4A6FA5")); break;
            case "proto": FileIcon(dc, "P", Color.Parse("#5C6BC0")); break;
            case "yaml": FileIcon(dc, "Y", Color.Parse("#CB171E")); break;
            case "image":
                FileIcon(dc, null, default);
                Fill(dc, "M4.5,13.5 L7,10 L8.5,11.5 L10,9.5 L12,13.5 Z", Color.Parse("#2E8B57"));
                break;
            case "binary": FileIcon(dc, "01", Color.Parse("#5A5A5A")); break;
            default:
                FileIcon(dc, null, default);
                break;
        }
    }
}

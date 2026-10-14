using System.Globalization;
using Avalonia;
using Avalonia.Controls;

using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using VSL.ViewModels;

namespace VSL.Views;

/// <summary>Small vector icons in the spirit of Visual Studio's file-type glyphs (drawn here, not copied).</summary>
public static class Icons
{
    static readonly Geometry FolderGeometry = Geometry.Parse("M1,3 L6,3 L7.5,4.5 L15,4.5 L15,13.5 L1,13.5 Z");
    static readonly Geometry SolutionGeometry = Geometry.Parse("M2,4 L8,1 L14,4 L14,12 L8,15 L2,12 Z");
    static readonly Geometry PuzzleGeometry = Geometry.Parse("M2,5 L6,5 C6,3 9,3 9,5 L12,5 L12,8 C14,8 14,11 12,11 L12,14 L2,14 Z");

    public static Control Create(string key) => key switch
    {
        "Solution" => Shape(SolutionGeometry, "#68217A", "#B48CC8"),
        "SolutionFolder" or "Folder" or "DependencyGroup" => Shape(FolderGeometry, "#DCB67A", null),
        "Project" => Badge("C#", "#388A34"),
        "Dependencies" => Shape(PuzzleGeometry, "#9B9B9B", null),
        "Package" => Badge("N", "#004880"),
        "ProjectReference" => Badge("C#", "#388A34"),
        "Framework" => Badge("F", "#5C2D91"),
        "cs" => Badge("C#", "#388A34"),
        "razor" => Badge("@", "#8E44AD"),
        "css" => Badge("#", "#1572B6"),
        "js" => Badge("JS", "#B8A12E"),
        "json" => Badge("{}", "#7E7E7E"),
        "xml" => Badge("<>", "#C66B12"),
        "html" => Badge("<>", "#E44D26"),
        "sql" => Badge("DB", "#A33"),
        "md" => Badge("M", "#4A6FA5"),
        "image" => Badge("▣", "#2E8B57"),
        "proto" => Badge("P", "#5C6BC0"),
        "yaml" => Badge("Y", "#CB171E"),
        "binary" => Badge("▤", "#5A5A5A"),
        _ => Badge("", "#6E6E6E"),
    };

    static Control Shape(Geometry geometry, string fill, string? stroke) => new Avalonia.Controls.Shapes.Path
    {
        Data = geometry,
        Fill = new SolidColorBrush(Color.Parse(fill)),
        Stroke = stroke is null ? null : new SolidColorBrush(Color.Parse(stroke)),
        StrokeThickness = 1,
        Width = 16,
        Height = 16,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Control Badge(string text, string color) => new Border
    {
        Width = 16,
        Height = 16,
        CornerRadius = new CornerRadius(2),
        Background = new SolidColorBrush(Color.Parse(color)),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = text.Length > 1 ? 7.5 : 9,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };
}

public sealed class IconConverter : IValueConverter
{
    public static readonly IconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Icons.Create(key) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Status bar color: blue when ready, orange while building/running, purple without a solution.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusKind.Busy or StatusKind.Running => new SolidColorBrush(Color.Parse("#CA5100")),
        StatusKind.NoSolution => new SolidColorBrush(Color.Parse("#68217A")),
        _ => new SolidColorBrush(Color.Parse("#007ACC")),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Test outcome glyphs (even-odd paths, so the check/cross is cut out of the disc).</summary>
public sealed class TestStateIconConverter : IValueConverter
{
    public static readonly TestStateIconConverter Instance = new();

    static readonly Geometry Passed = Geometry.Parse("M7,0 A7,7 0 1 0 7,14 A7,7 0 1 0 7,0 Z M3.4,7.3 L5.9,9.8 L10.7,4.7 L9.6,3.7 L5.9,7.7 L4.5,6.2 Z");
    static readonly Geometry Failed = Geometry.Parse("M7,0 A7,7 0 1 0 7,14 A7,7 0 1 0 7,0 Z M4.3,3.3 L7,6 L9.7,3.3 L10.7,4.3 L8,7 L10.7,9.7 L9.7,10.7 L7,8 L4.3,10.7 L3.3,9.7 L6,7 L3.3,4.3 Z");
    static readonly Geometry Skipped = Geometry.Parse("M7,0.5 L14,13.5 L0,13.5 Z M6.2,4.5 L7.8,4.5 L7.8,9 L6.2,9 Z M6.2,10.2 L7.8,10.2 L7.8,11.8 L6.2,11.8 Z");
    static readonly Geometry Pending = Geometry.Parse("M7,0 A7,7 0 1 0 7,14 A7,7 0 1 0 7,0 Z M7,2 A5,5 0 1 1 7,12 A5,5 0 1 1 7,2 Z");
    static readonly Geometry Running = Geometry.Parse("M7,0 A7,7 0 1 0 7,14 A7,7 0 1 0 7,0 Z M7,2 A5,5 0 1 1 7,12 L7,7 Z");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        VSL.Testing.TestState.Passed => Passed,
        VSL.Testing.TestState.Failed => Failed,
        VSL.Testing.TestState.Skipped => Skipped,
        VSL.Testing.TestState.Running => Running,
        _ => Pending,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ColorStringConverter : IValueConverter
{
    public static readonly ColorStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? new SolidColorBrush(Color.Parse(s)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

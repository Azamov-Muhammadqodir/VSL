using System.Globalization;
using Avalonia;
using Avalonia.Controls;

using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using VSL.ViewModels;

namespace VSL.Views;

public sealed class IconConverter : IValueConverter
{
    public static readonly IconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? new VsIcon { Kind = key } : null;

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

/// <summary>1 when any input is true, else 0: VS shows a tab's close button only on the active or hovered tab.</summary>
public sealed class AnyTrueToOpacityConverter : IMultiValueConverter
{
    public static readonly AnyTrueToOpacityConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Any(v => v is true) ? 1.0 : 0.0;
}

public sealed class ColorStringConverter : IValueConverter
{
    public static readonly ColorStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? new SolidColorBrush(Color.Parse(s)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

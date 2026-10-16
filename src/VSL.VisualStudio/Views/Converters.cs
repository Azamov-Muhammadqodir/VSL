using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Avalonia.Media.Imaging;

namespace VSL.VisualStudio.Views;

/// <summary>
/// Visible for a "truthy" value: true, a non-null object, a non-empty string or collection, a non-zero count.
/// With Invert, visible for the opposite (Avalonia's <c>!</c> and <c>IsVisible="{Binding Count}"</c> bindings).
/// </summary>
public sealed class VisibleWhen : IValueConverter
{
    public bool Invert { get; set; }

    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        int n => n != 0,
        string s => s.Length > 0,
        ICollection c => c.Count > 0,
        IEnumerable e => e.GetEnumerator().MoveNext(),
        _ => true,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        IsTruthy(value) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Negates a bool (for IsEnabled and similar).</summary>
public sealed class Not : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !VisibleWhen.IsTruthy(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => !VisibleWhen.IsTruthy(value);
}

/// <summary>An attachment's thumbnail (the shared model's Bitmap) as a WPF image source.</summary>
public sealed class ThumbnailSource : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as Bitmap)?.Source;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when two accounts are the same folder (the ticked row of the accounts menu).</summary>
public sealed class SameAccount : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [VSL.Claude.ClaudeAccount a, VSL.Claude.ClaudeAccount b] && string.Equals(a.ConfigDirectory, b.ConfigDirectory, StringComparison.OrdinalIgnoreCase);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"Warn" once a limit is half used (the meter turns yellow; IsHigh turns it orange at 80%).</summary>
public sealed class LimitLevel : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double percent && percent >= 50 ? "Warn" : "Ok";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when both values are the same item (the chosen row of the model menu).</summary>
public sealed class SameItem : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [{ } a, { } b] && Equals(a, b);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

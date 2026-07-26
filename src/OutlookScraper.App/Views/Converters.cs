using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OutlookScraper.App.Views;

/// <summary>
/// Visible when the bound boolean is false.
/// </summary>
/// <remarks>
/// The built-in <c>BooleanToVisibilityConverter</c> ignores <c>ConverterParameter</c>
/// entirely, so "invert" has to be its own converter rather than a parameter.
/// </remarks>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>Collapses an element when its bound string is null or whitespace.</summary>
public sealed class StringPresentToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

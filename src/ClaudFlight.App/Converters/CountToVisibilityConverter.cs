using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudFlight.App.Converters;

/// <summary>Collapses an element when a bound count is zero (e.g. hide the "Hidden devices" bar
/// when there aren't any).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

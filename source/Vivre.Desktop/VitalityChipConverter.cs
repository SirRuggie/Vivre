using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Vivre.Desktop;

/// <summary>A non-null value → <see cref="Visibility.Visible"/>, null → <see cref="Visibility.Collapsed"/>.
/// Hides the Vitals chip on rows that haven't been scored yet (no band set).</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

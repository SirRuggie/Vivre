using System.Globalization;
using System.Windows.Data;

namespace Vivre.Desktop;

/// <summary>Returns the logical negation of a bound boolean (used to pair two radio buttons to one flag).</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

using System.Globalization;
using System.Windows.Data;

namespace Vivre.Desktop;

/// <summary>
/// Two-way enum ⇄ bool keyed on the binding's <see cref="ConverterParameter"/> — lets a group of
/// radio-style <c>MenuItem</c>s (one per enum value) bind to a single enum-valued property:
/// the item whose parameter equals the current value is checked; clicking another sets the
/// property to that other parameter; when a sibling is auto-unchecked we return
/// <see cref="Binding.DoNothing"/> so we don't fight the new value going back through.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && parameter is not null && value.Equals(parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Vivre.Desktop.ViewModels;

namespace Vivre.Desktop;

/// <summary>
/// Converts <see cref="FleetSection"/> to <see cref="Visibility"/>: Visible when the value matches
/// the <c>ConverterParameter</c> string ("Health" or "Patching"), Collapsed otherwise. Used to
/// Visibility-toggle the two <c>TabControlEx</c> strips so only one is visible at a time while
/// BOTH stay in the visual tree (the keep-alive requirement).
/// </summary>
public sealed class FleetSectionToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FleetSection section && parameter is string target
            && Enum.TryParse<FleetSection>(target, ignoreCase: true, out FleetSection targetSection))
        {
            return section == targetSection ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

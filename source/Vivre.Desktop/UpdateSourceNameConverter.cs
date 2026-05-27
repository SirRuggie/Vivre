using System.Globalization;
using System.Windows.Data;
using Vivre.Core.Updates;

namespace Vivre.Desktop;

/// <summary>Friendly display name for an <see cref="UpdateSource"/> in the Source toggle
/// (the raw enum names — "WindowsUpdate" etc. — read poorly in the dropdown).</summary>
public sealed class UpdateSourceNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is UpdateSource source
            ? source switch
            {
                UpdateSource.WindowsUpdate => "Windows Update",
                UpdateSource.MicrosoftUpdate => "Microsoft Update",
                UpdateSource.Managed => "Managed (WSUS/SCCM)",
                _ => source.ToString(),
            }
            : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

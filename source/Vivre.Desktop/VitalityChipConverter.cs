using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Vivre.Core.Vitals;

namespace Vivre.Desktop;

/// <summary>
/// Maps a <see cref="VitalityBand"/> to the Vitals-chip background brush. One palette, frozen
/// brushes — the same no-alloc pattern as <see cref="PhaseChipBrushConverter"/>, and deliberately
/// the same green/amber/red so the Vitals chip reads consistently with the Status pill beside it.
///   green = Healthy · amber = Warning · red = Critical · dark-grey = Offline · grey = Unknown.
/// </summary>
public sealed class VitalityChipBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Healthy = Frozen(0x16, 0xA3, 0x4A);  // green (matches Done)
    private static readonly SolidColorBrush Warning = Frozen(0xD9, 0x77, 0x06);  // amber (matches RebootPending)
    private static readonly SolidColorBrush Critical = Frozen(0xDC, 0x26, 0x26); // red (matches Error)
    private static readonly SolidColorBrush Offline = Frozen(0x3F, 0x3F, 0x46);  // dark grey
    private static readonly SolidColorBrush Unknown = Frozen(0x6B, 0x72, 0x80);  // grey (matches Idle)

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is VitalityBand b ? BrushFor(b) : Brushes.Transparent;

    internal static SolidColorBrush BrushFor(VitalityBand b) => b switch
    {
        VitalityBand.Healthy => Healthy,
        VitalityBand.Warning => Warning,
        VitalityBand.Critical => Critical,
        VitalityBand.Offline => Offline,
        _ => Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// <see cref="VitalityBand"/> → readable text colour for the Vitals chip. White fails contrast on the
/// lighter fills (amber Warning, green Healthy), so those get near-black text; the darker fills keep
/// white. Mirrors <see cref="PhaseChipForegroundConverter"/>.
/// </summary>
public sealed class VitalityChipForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Dark = Frozen(0x17, 0x17, 0x17);
    private static readonly SolidColorBrush Light = Frozen(0xFF, 0xFF, 0xFF);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is VitalityBand b && b is VitalityBand.Healthy or VitalityBand.Warning ? Dark : Light;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A non-null value → <see cref="Visibility.Visible"/>, null → <see cref="Visibility.Collapsed"/>.
/// Hides the Vitals chip on rows that haven't been scored yet (no band set).</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

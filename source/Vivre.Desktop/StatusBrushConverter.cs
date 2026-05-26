using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Vivre.Core.Logging;

namespace Vivre.Desktop;

/// <summary>
/// Maps a tri-state <see cref="bool"/>? to a status dot brush. Colours are chosen to
/// read on both light and dark backgrounds.
///   • default polarity (e.g. Online): true → green, false → red, null → grey.
///   • ConverterParameter="problem" (health items): true → red, false → green, null → grey.
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Good = Frozen(0x16, 0xA3, 0x4A);   // green
    private static readonly SolidColorBrush Bad = Frozen(0xDC, 0x26, 0x26);    // red
    private static readonly SolidColorBrush Unknown = Frozen(0x9C, 0xA3, 0xAF); // grey

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool state)
        {
            return Unknown;
        }

        bool problemWhenTrue = parameter as string == "problem";
        bool good = problemWhenTrue ? !state : state;
        return good ? Good : Bad;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>tri-state bool? → "Yes" / "No" / "Unknown" (for status-dot tooltips).</summary>
public sealed class BoolStateTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { true => "Yes", false => "No", _ => "Unknown" };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Activity-log severity → text colour (legible on light and dark).</summary>
public sealed class LogSeverityBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = Freeze(0x9C, 0xA3, 0xAF);    // grey
    private static readonly SolidColorBrush Warning = Freeze(0xD9, 0x77, 0x06); // amber
    private static readonly SolidColorBrush Error = Freeze(0xDC, 0x26, 0x26);   // red

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogSeverity.Error => Error,
        LogSeverity.Warning => Warning,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

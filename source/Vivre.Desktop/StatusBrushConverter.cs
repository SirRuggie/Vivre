using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Vivre.Core.Logging;

namespace Vivre.Desktop;

/// <summary>
/// Maps a tri-state <see cref="bool"/>? to a status dot brush. Resolves from the WPF-UI
/// application resource dictionary so the brush updates on a live Light↔Dark theme switch.
///   • default polarity (e.g. Online): true → green, false → red, null → grey.
///   • ConverterParameter="problem" (health items): true → red, false → green, null → grey.
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool state)
            return Resolve("SystemFillColorNeutralBrush");

        bool problemWhenTrue = parameter as string == "problem";
        bool good = problemWhenTrue ? !state : state;
        return good
            ? Resolve("SystemFillColorSuccessBrush")
            : Resolve("SystemFillColorCriticalBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static object Resolve(string key) =>
        Application.Current.Resources[key] ?? Brushes.Transparent;
}

/// <summary>tri-state bool? → "Yes" / "No" / "Unknown" (for status-dot tooltips).</summary>
public sealed class BoolStateTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { true => "Yes", false => "No", _ => "Unknown" };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Activity-log severity → text colour, resolved from WPF-UI theme resources so it
/// updates on a live Light↔Dark switch.</summary>
public sealed class LogSeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogSeverity.Error => StatusBrushConverter.Resolve("SystemFillColorCriticalBrush"),
        LogSeverity.Warning => StatusBrushConverter.Resolve("SystemFillColorCautionBrush"),
        _ => StatusBrushConverter.Resolve("SystemFillColorNeutralBrush"),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

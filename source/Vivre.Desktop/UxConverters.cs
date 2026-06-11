using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Vivre.Core.Logging;

namespace Vivre.Desktop;

/// <summary>Non-empty string → Visible, null/blank → Collapsed (used to hide an optional Help "Tip" box).</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Non-zero integer → <c>true</c>, zero → <c>false</c>. Used to drive a <see cref="System.Windows.Controls.TextBlock"/>
/// visibility trigger keyed on a count: when the count is zero the element collapses.
/// </summary>
public sealed class NonZeroToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n != 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Tri-state <see cref="bool"/>? → a status <c>SymbolRegular</c> so the grid dots carry a SHAPE,
/// not colour alone (WCAG 1.4.1). Mirrors <see cref="StatusBrushConverter"/>'s polarity so the
/// glyph and the colour always agree:
///   • default (e.g. Online): true → ✓ checkmark, false → ✕ dismiss, null → ? question.
///   • ConverterParameter="problem" (health items): true → ✕, false → ✓, null → ?.
/// Pair with <see cref="StatusBrushConverter"/> for the Foreground so colour + shape both encode state.
/// </summary>
public sealed class StatusSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool state)
        {
            return Wpf.Ui.Controls.SymbolRegular.QuestionCircle24; // unknown / not checked yet
        }

        bool problemWhenTrue = parameter as string == "problem";
        bool good = problemWhenTrue ? !state : state;
        return good
            ? Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24
            : Wpf.Ui.Controls.SymbolRegular.DismissCircle24;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Activity-log <see cref="LogSeverity"/> → a leading glyph, so Warning vs Error survives
/// without relying on text colour alone (WCAG 1.4.1). Colour still comes from
/// <see cref="LogSeverityBrushConverter"/>.</summary>
public sealed class LogSeveritySymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogSeverity.Error => Wpf.Ui.Controls.SymbolRegular.ErrorCircle24,
        LogSeverity.Warning => Wpf.Ui.Controls.SymbolRegular.Warning24,
        _ => Wpf.Ui.Controls.SymbolRegular.Info24,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="bool"/> (the window's <c>ToolbarCompact</c> flag) to one of two
/// <see cref="Thickness"/> values supplied via the <c>ConverterParameter</c> as
/// <c>"normal;compact"</c> — each in <c>left,top,right,bottom</c> (or shorthand) form.
/// Used to tighten the spacing between command-bar buttons (and the group dividers) when labels
/// collapse to icons, so the bar reads as one cohesive group rather than floating icons. Bound as a
/// local value, so the expanded layout the collapse measurement relies on is untouched (the
/// expanded width is still measured with the normal spacing).
/// </summary>
public sealed class CompactThicknessConverter : IValueConverter
{
    private static readonly ThicknessConverter Tc = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string[] parts = (parameter as string ?? "0;0").Split(';');
        bool compact = value is true;
        string spec = (compact && parts.Length > 1 ? parts[1] : parts[0]).Trim();
        return Tc.ConvertFromInvariantString(spec) ?? new Thickness(0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

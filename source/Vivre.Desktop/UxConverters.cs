using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Vivre.Core.Logging;
using Vivre.Core.Updates;

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
/// <see cref="PatchState"/> → readable text colour for the Status pill. The pill fill comes from
/// <see cref="PhaseChipBrushConverter"/>; white text fails contrast on the lighter fills (amber
/// RebootPending, green Done, steel Available, grey Idle), so those get near-black text while the
/// darker fills (blue working, red Error) keep white. Keeps the pill ≥ ~4.5:1 in both themes.
/// </summary>
public sealed class PhaseChipForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Dark = Frozen(0x17, 0x17, 0x17);
    private static readonly SolidColorBrush Light = Frozen(0xFF, 0xFF, 0xFF);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PatchState s && s is PatchState.Idle or PatchState.Available or PatchState.RebootPending or PatchState.Done
            ? Dark
            : Light;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Vivre.Core.Updates;

namespace Vivre.Desktop;

/// <summary>
/// Maps a <see cref="PatchState"/> to the Status-chip background brush (and, via the sibling
/// converters, the chip label and the message text colour). One palette, frozen brushes — cheap,
/// no per-render allocation (same pattern as <see cref="StatusBrushConverter"/>).
///   grey = Idle · blue = working (Scanning/Downloading/Installing) · steel = Available ·
///   amber = RebootPending · green = Done/back-online · red = Error.
/// </summary>
public sealed class PhaseChipBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Idle = Frozen(0x6B, 0x72, 0x80);     // grey
    private static readonly SolidColorBrush Working = Frozen(0x25, 0x63, 0xEB);  // blue
    private static readonly SolidColorBrush Available = Frozen(0x47, 0x76, 0x9C);// steel/info
    private static readonly SolidColorBrush Reboot = Frozen(0xD9, 0x77, 0x06);   // amber
    private static readonly SolidColorBrush Done = Frozen(0x16, 0xA3, 0x4A);     // green
    private static readonly SolidColorBrush Error = Frozen(0xDC, 0x26, 0x26);    // red

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PatchState s ? BrushFor(s) : Idle;

    internal static SolidColorBrush BrushFor(PatchState s) => s switch
    {
        PatchState.Scanning or PatchState.Downloading or PatchState.Installing => Working,
        PatchState.Available => Available,
        PatchState.RebootPending => Reboot,
        PatchState.Done => Done,
        PatchState.Error => Error,
        _ => Idle,
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

/// <summary><see cref="PatchState"/> → the short chip / message label.</summary>
public sealed class PhaseChipLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PatchState s ? LabelFor(s) : string.Empty;

    internal static string LabelFor(PatchState s) => s switch
    {
        PatchState.Idle => "Idle",
        PatchState.Scanning => "Scanning",
        PatchState.Available => "Available",
        PatchState.Downloading => "Downloading",
        PatchState.Installing => "Installing",
        PatchState.RebootPending => "Reboot pending",
        PatchState.Done => "Done",
        PatchState.Error => "Error",
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Globalization;
using System.Windows.Data;
using Vivre.Core.Updates;

namespace Vivre.Desktop;

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
        PatchState.Uninstalling => "Uninstalling",
        PatchState.RebootPending => "Reboot pending",
        PatchState.Done => "Done",
        PatchState.Unverified => "Unverified",
        PatchState.Error => "Error",
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

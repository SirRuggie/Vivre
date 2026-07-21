using System.Globalization;
using System.Windows.Data;
using Vivre.Core.Updates;

namespace Vivre.Desktop;

/// <summary><see cref="PatchState"/> → the short chip / message label.</summary>
public sealed class PhaseChipLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PatchState s ? LabelFor(s) : string.Empty;

    // Label map lives in Core (PatchStateLabels) so the CSV export shares it — one source of truth.
    internal static string LabelFor(PatchState s) => PatchStateLabels.For(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

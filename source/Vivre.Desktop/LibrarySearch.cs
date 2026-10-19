using System.Globalization;
using System.Windows.Data;
using Vivre.Core.Scripts;
namespace Vivre.Desktop;
internal static class LibrarySearch
{
    public static bool Matches(ScriptFile script, string query) =>
        script.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || (script.Category?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
}
public sealed class SearchActiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !string.IsNullOrWhiteSpace(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

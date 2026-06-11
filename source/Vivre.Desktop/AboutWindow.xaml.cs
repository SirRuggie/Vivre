using System.Globalization;
using System.Reflection;
using System.Windows;
using Vivre.Core;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>The About dialog — the name story (Vivre Card lore) and build info.</summary>
public partial class AboutWindow : FluentWindow
{
    public AboutWindow()
    {
        InitializeComponent();

        // Single source of truth for the name/tagline; append the running version + build stamp.
        NameText.Text = AppInfo.ProductName;
        TaglineText.Text = AppInfo.Tagline;
        EditionText.Text = $"{AppInfo.Edition}  ·  {RunningVersion()}";
    }

    /// <summary>Formats the assembly's informational version (e.g. "1.5.0+build.202606031540") into
    /// "v1.5.0 · build 2026-06-03 15:40", so any build is identifiable. Falls back to the numeric version.</summary>
    internal static string RunningVersion()
    {
        Assembly app = Assembly.GetExecutingAssembly();
        string informational = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? app.GetName().Version?.ToString(3)
            ?? string.Empty;
        if (informational.Length == 0)
        {
            return "v?";
        }

        int plus = informational.IndexOf('+');
        string baseVersion = plus >= 0 ? informational[..plus] : informational;
        string result = $"v{baseVersion}";

        const string marker = "+build.";
        int m = informational.IndexOf(marker, StringComparison.Ordinal);
        if (m >= 0)
        {
            // After "+build." is the date stamp, possibly followed by ".<git-sha>" the SDK appends —
            // take just the date token for display (the full SHA stays in the exe's version for forensics).
            string stamp = informational[(m + marker.Length)..];
            int dot = stamp.IndexOf('.');
            string dateToken = dot >= 0 ? stamp[..dot] : stamp;
            result += DateTime.TryParseExact(dateToken, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime built)
                ? $"  ·  build {built:yyyy-MM-dd HH:mm}"
                : dateToken.Length > 0 ? $"  ·  build {dateToken}" : string.Empty;
        }

        return result;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

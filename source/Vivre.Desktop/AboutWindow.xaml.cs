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

        // Single source of truth for the name/tagline; append the running version.
        NameText.Text = AppInfo.ProductName;
        TaglineText.Text = AppInfo.Tagline;
        string? version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        EditionText.Text = version is null ? AppInfo.Edition : $"{AppInfo.Edition}  ·  v{version}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

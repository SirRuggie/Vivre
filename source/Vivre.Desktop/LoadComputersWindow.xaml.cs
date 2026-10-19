using System.Windows;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Adds pasted machines by default; explicit replacement shares list protections.</summary>
public partial class LoadComputersWindow : FluentWindow
{
    private readonly WorkspaceViewModel _workspace;

    public LoadComputersWindow(WorkspaceViewModel workspace)
    {
        InitializeComponent();
        _workspace = workspace;
        Loaded += (_, _) => NamesBox.Focus();
    }

    private void OnLoad(object sender, RoutedEventArgs e)
    {
        string[] names = Vivre.Core.Computers.ComputerNameInput.Parse(NamesBox.Text);
        try
        {
            if (ReplaceMode.IsChecked == true)
            {
                if (!WorkspaceListActions.Replace(_workspace, names, message =>
                    System.Windows.MessageBox.Show(this, message, "Replace machine list", System.Windows.MessageBoxButton.YesNo,
                        MessageBoxImage.Warning, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes)) return;
            }
            else _workspace.AddComputers(names);
            Close();
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (ApplyButton is not null) ApplyButton.Content = ReplaceMode.IsChecked == true ? "Replace machines" : "Add machines";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

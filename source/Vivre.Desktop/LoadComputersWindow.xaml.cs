using System.Windows;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Adds pasted machines by default; explicit replacement shares list protections.</summary>
public partial class LoadComputersWindow : FluentWindow
{
    private readonly WorkspaceViewModel? _workspace;
    private readonly Action<string>? _acceptEntries;

    public LoadComputersWindow(WorkspaceViewModel workspace)
    {
        InitializeComponent();
        _workspace = workspace;
        Loaded += (_, _) => NamesBox.Focus();
    }

    public LoadComputersWindow(string title, string instructions, string acceptText, Action<string> acceptEntries)
    {
        InitializeComponent();
        _acceptEntries = acceptEntries;
        Title = PasteTitleBar.Title = title;
        InstructionsText.Text = instructions;
        AddMode.Visibility = ReplaceMode.Visibility = Visibility.Collapsed;
        ApplyButton.Content = acceptText;
        System.Windows.Automation.AutomationProperties.SetName(NamesBox, "People names, usernames, or emails");
        Loaded += (_, _) => NamesBox.Focus();
    }

    private void OnLoad(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_acceptEntries is not null)
            {
                _acceptEntries(NamesBox.Text);
                DialogResult = true;
                return;
            }
            string[] names = Vivre.Core.Computers.ComputerNameInput.Parse(NamesBox.Text);
            if (_workspace is null) return;
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

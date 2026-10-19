using System.Windows;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Adds pasted machines by default; explicit replacement shares list protections.</summary>
public partial class LoadComputersWindow : FluentWindow
{
    private readonly WorkspaceViewModel? _workspace;
    private readonly Action<string>? _acceptEntries;
    private readonly Action<string[]>? _createTab;
    private readonly Action<SavedListOpenMode>? _openSavedList;

    public LoadComputersWindow(WorkspaceViewModel workspace, Action<string[]>? createTab = null)
    {
        InitializeComponent();
        _workspace = workspace;
        _createTab = createTab;
        NewTabMode.Visibility = createTab is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => NamesBox.Focus();
    }

    public enum SavedListOpenMode { Append, Replace, NewTab }

    public LoadComputersWindow(string listName, Action<SavedListOpenMode> openSavedList)
    {
        InitializeComponent();
        _openSavedList = openSavedList;
        Title = PasteTitleBar.Title = "Open saved list";
        InstructionsText.Text = $"Open '{listName}': append keeps existing results; replace removes machines outside this list and their results. Saved files are unchanged.";
        AddMode.Content = "Append list";
        ReplaceMode.Content = "Replace list";
        NewTabMode.Content = "New tab (default)";
        NamesBox.Visibility = Visibility.Collapsed;
        Height = 330;
        MinHeight = 300;
        NewTabMode.IsChecked = true;
        Loaded += (_, _) => NewTabMode.Focus();
    }

    public LoadComputersWindow(string title, string instructions, string acceptText, Action<string> acceptEntries)
    {
        InitializeComponent();
        _acceptEntries = acceptEntries;
        Title = PasteTitleBar.Title = title;
        InstructionsText.Text = instructions;
        AddMode.Visibility = ReplaceMode.Visibility = NewTabMode.Visibility = Visibility.Collapsed;
        ApplyButton.Content = acceptText;
        System.Windows.Automation.AutomationProperties.SetName(NamesBox, "People names, usernames, or emails");
        Loaded += (_, _) => NamesBox.Focus();
    }

    private void OnLoad(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_openSavedList is not null)
            {
                _openSavedList(NewTabMode.IsChecked == true ? SavedListOpenMode.NewTab
                    : ReplaceMode.IsChecked == true ? SavedListOpenMode.Replace : SavedListOpenMode.Append);
                DialogResult = true;
                return;
            }
            if (_acceptEntries is not null)
            {
                _acceptEntries(NamesBox.Text);
                DialogResult = true;
                return;
            }
            string[] names = Vivre.Core.Computers.ComputerNameInput.Parse(NamesBox.Text);
            if (_workspace is null) return;
            if (NewTabMode.IsChecked == true)
            {
                if (names.Length == 0) { ErrorText.Text = "Enter at least one machine name."; return; }
                _createTab?.Invoke(names);
            }
            else if (ReplaceMode.IsChecked == true)
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
        if (ApplyButton is not null) ApplyButton.Content = NewTabMode?.IsChecked == true ? "Create tab" : ReplaceMode.IsChecked == true ? "Replace machines" : "Add machines";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

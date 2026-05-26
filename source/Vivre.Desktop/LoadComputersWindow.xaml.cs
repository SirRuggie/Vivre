using System.Windows;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Paste-a-list loader: replaces the grid's machines with the entered names.</summary>
public partial class LoadComputersWindow : FluentWindow
{
    private readonly WorkspaceViewModel _workspace;

    public LoadComputersWindow(WorkspaceViewModel workspace)
    {
        InitializeComponent();
        _workspace = workspace;

        // Pre-fill with the current list so the user can edit rather than retype.
        NamesBox.Text = string.Join(Environment.NewLine, workspace.Computers.Select(c => c.Name));
    }

    private void OnLoad(object sender, RoutedEventArgs e)
    {
        string[] names = NamesBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _workspace.SetComputers(names);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

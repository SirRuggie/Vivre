using System.Windows;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

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

        // Focus the paste box (caret at end) so the user can start typing/pasting immediately.
        Loaded += (_, _) =>
        {
            NamesBox.Focus();
            NamesBox.CaretIndex = NamesBox.Text.Length;
        };
    }

    private async void OnLoad(object sender, RoutedEventArgs e)
    {
        string[] names = NamesBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Confirm only when this actually DROPS currently-loaded machines (a real wipe) — not on a
        // pure add or an unchanged list.
        var newSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        int dropped = _workspace.Computers.Count(c => !newSet.Contains(c.Name));
        if (dropped > 0)
        {
            var confirm = new MessageBox
            {
                Title = "Replace machine list",
                Content = $"Replace this tab's machines? {dropped} currently-loaded machine(s) will be removed.\n\n"
                          + "They stay in any saved list — re-load to bring them back.",
                PrimaryButtonText = "Replace list",
                CloseButtonText = "Cancel",
            };
            if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary)
            {
                return;
            }
        }

        _workspace.SetComputers(names);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

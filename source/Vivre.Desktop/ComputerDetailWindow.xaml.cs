using System.Windows;
using Vivre.Core.Remediation;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Vivre.Desktop;

/// <summary>Per-machine detail window: the box's Vitals (with inline triage actions), its update
/// picture, and its activity-log messages. Modeless — binds the live model so it tracks changes as
/// they happen. Built via <c>WorkspaceViewModel.CreateDetailViewModel</c>; the destructive triage
/// actions (free disk, end process) confirm here before calling the view model.</summary>
public partial class ComputerDetailWindow : FluentWindow
{
    public ComputerDetailWindow(ComputerDetailViewModel viewModel)
    {
        InitializeComponent();
        Title = $"Details — {viewModel.Computer.Name}";
        DataContext = viewModel;
    }

    private ComputerDetailViewModel? ViewModel => DataContext as ComputerDetailViewModel;

    /// <summary>Free up disk space — destructive (deletes temp / cache / recycle-bin files), so confirm first.</summary>
    private async void OnFreeDiskSpace(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var confirm = new MessageBox
        {
            Title = "Free up disk space",
            Content = $"Clear TEMP, the Windows Update download cache, and the recycle bin on {vm.Computer.Name}?\n\n"
                      + "This deletes files and can't be undone (it won't touch your documents).",
            PrimaryButtonText = "Free up space",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            await vm.FreeDiskSpaceAsync();
        }
    }

    /// <summary>End a process — destructive, so confirm first. The clicked row's process is the button's DataContext.</summary>
    private async void OnEndProcess(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || (sender as FrameworkElement)?.DataContext is not ProcessInfo process)
        {
            return;
        }

        var confirm = new MessageBox
        {
            Title = "End process",
            Content = $"Force-end {process.Name} (PID {process.Id}) on {vm.Computer.Name}?\n\n"
                      + "Unsaved work in that process is lost.",
            PrimaryButtonText = "End process",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            await vm.EndProcessAsync(process);
        }
    }
}

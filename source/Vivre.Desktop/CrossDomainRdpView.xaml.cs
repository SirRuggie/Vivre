using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vivre.Core.Rdp;
using Vivre.Desktop.ViewModels;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Vivre.Desktop;

/// <summary>
/// The Cross-Domain RDP tab content: a folder tree of hosts (left) and live, tabbed RDP sessions (right). Owns
/// the add/edit dialogs and forwards their results to <see cref="CrossDomainRdpViewModel"/>. DataContext is the
/// tab's <see cref="CrossDomainRdpViewModel"/> (set by the shell via a type-keyed DataTemplate).
/// </summary>
public partial class CrossDomainRdpView : UserControl
{
    public CrossDomainRdpView()
    {
        InitializeComponent();
    }

    private CrossDomainRdpViewModel? Vm => DataContext as CrossDomainRdpViewModel;

    // TreeView.SelectedItem isn't bindable — mirror it onto the view-model here.
    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm is not null)
        {
            Vm.SelectedNode = e.NewValue as RdpNodeViewModel;
        }
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { SelectedNode: RdpHostNodeViewModel host })
        {
            Vm.ConnectTo(host);
        }
    }

    // Right-click selects the item under the cursor first, so the context menu acts on the clicked node.
    private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null and not TreeViewItem)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        if (source is TreeViewItem item)
        {
            item.IsSelected = true;
        }
    }

    private void OnContextConnect(object sender, RoutedEventArgs e)
    {
        if (Vm is { SelectedNode: RdpHostNodeViewModel host })
        {
            Vm.ConnectTo(host);
        }
    }

    // ---- drag-and-drop: move a host/folder into another folder (or out to the top level) ----
    private System.Windows.Point _dragStart;
    private RdpNodeViewModel? _dragNode;

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragNode = NodeAt(e.OriginalSource as DependencyObject);
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragNode is null)
        {
            return;
        }

        System.Windows.Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(HostTree, _dragNode, DragDropEffects.Move);
        _dragNode = null;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _dragNode is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private async void OnTreeDrop(object sender, DragEventArgs e)
    {
        RdpNodeViewModel? dragged = _dragNode;
        _dragNode = null;
        if (dragged is null || Vm is null)
        {
            return;
        }

        RdpFolderNodeViewModel? target = TargetFolderAt(e.OriginalSource as DependencyObject);
        if (!Vm.CanMove(dragged, target))
        {
            return;
        }

        string targetName = target?.Name ?? "the top level";
        var confirm = new MessageBox
        {
            Title = "Move",
            Content = $"Move “{dragged.Name}” to {targetName}?",
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            Vm.MoveNode(dragged, target);
        }
    }

    // A folder target = drop onto a folder; a host target = its parent folder; empty space = top level (null).
    private RdpFolderNodeViewModel? TargetFolderAt(DependencyObject? source) => NodeAt(source) switch
    {
        RdpFolderNodeViewModel folder => folder,
        RdpHostNodeViewModel host => host.Parent,
        _ => null,
    };

    private static RdpNodeViewModel? NodeAt(DependencyObject? source)
    {
        while (source is not null and not TreeViewItem)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return (source as TreeViewItem)?.DataContext as RdpNodeViewModel;
    }

    private void OnSessionReconnect(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RdpSessionViewModel session })
        {
            session.RequestReconnect();
        }
    }

    private void OnSessionClose(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && sender is FrameworkElement { DataContext: RdpSessionViewModel session })
        {
            Vm.CloseSession(session);
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var dialog = new RdpFolderDialog("Add folder") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            Vm.AddFolder(dialog.FolderName, dialog.Domain, dialog.UserName, dialog.Password);
        }
    }

    private void OnAddHost(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var dialog = new RdpHostDialog("Add host") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            Vm.AddHost(dialog.HostName, dialog.Server, dialog.Port, dialog.NlaEnabled, dialog.Domain, dialog.UserName, dialog.Password);
        }
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        switch (Vm?.SelectedNode)
        {
            case RdpHostNodeViewModel hostNode:
            {
                RdpHost host = hostNode.Host;
                RdpCredential? cred = Vm.GetCredential(host.CredentialId);
                var dialog = new RdpHostDialog("Edit host", host.Name, host.Server, host.Port, host.NlaEnabled, cred?.Domain, cred?.UserName)
                {
                    Owner = Window.GetWindow(this),
                };
                if (dialog.ShowDialog() == true)
                {
                    Vm.UpdateHost(hostNode, dialog.HostName, dialog.Server, dialog.Port, dialog.NlaEnabled, dialog.Domain, dialog.UserName, dialog.Password);
                }

                break;
            }

            case RdpFolderNodeViewModel folderNode:
            {
                RdpCredential? cred = Vm.GetCredential(folderNode.Folder.CredentialId);
                var dialog = new RdpFolderDialog("Edit folder", folderNode.Folder.Name, cred?.Domain, cred?.UserName)
                {
                    Owner = Window.GetWindow(this),
                };
                if (dialog.ShowDialog() == true)
                {
                    Vm.UpdateFolder(folderNode, dialog.FolderName, dialog.Domain, dialog.UserName, dialog.Password);
                }

                break;
            }
        }
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Vm is not { CanRemoveSelected: true, SelectedNode: { } node })
        {
            return;
        }

        string what = node is RdpFolderNodeViewModel ? "folder (and everything in it)" : "host";
        var confirm = new MessageBox
        {
            Title = "Remove",
            Content = $"Remove the {what} “{node.Name}”? Saved credentials are kept; you can re-add it.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            Vm.RemoveNode(node);
        }
    }

    private void OnCloseSession(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && sender is FrameworkElement { Tag: RdpSessionViewModel session })
        {
            Vm.CloseSession(session);
        }
    }
}

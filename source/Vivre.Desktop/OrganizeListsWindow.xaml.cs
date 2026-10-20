using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Vivre.Desktop.ViewModels;

namespace Vivre.Desktop;

public partial class OrganizeListsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly WorkspaceViewModel _workspace;
    private readonly Func<Window, string, bool> _confirmRemove;
    private ICollectionView? _view;
    private sealed record Entry(string Name, string? Folder)
    { public string FolderLabel => Folder ?? "Top level"; }

    public OrganizeListsWindow(WorkspaceViewModel workspace, Func<Window, string, bool>? confirmRemove = null)
    {
        InitializeComponent();
        _workspace = workspace;
        _confirmRemove = confirmRemove ?? ((owner, folder) => System.Windows.MessageBox.Show(owner,
            $"Remove the folder '{folder}'? Its lists will stay available at the top level.", "Remove list folder",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        FolderChoice.Configure(() => workspace.SavedListFolders(workspace.SavedLists()), workspace.CreateListFolder);
        FolderChoice.FolderChanged += (_, _) => UpdateButtons();
        Reload();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void Reload(string? selectedList = null)
    {
        var names = _workspace.SavedLists();
        var folders = _workspace.SavedListFolders(names);
        FolderChoice.Reload(FolderChoice.SelectedFolder);
        var entries = names.Select(name => new Entry(name, folders.FolderFor(name))).ToArray();
        _view = CollectionViewSource.GetDefaultView(entries);
        _view.Filter = item => item is Entry entry && entry.Name.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        Lists.ItemsSource = _view;
        Lists.SelectedItem = entries.FirstOrDefault(entry => string.Equals(entry.Name, selectedList, StringComparison.OrdinalIgnoreCase));
        UpdateEmpty();
        UpdateButtons();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateEmpty();
        UpdateButtons();
    }

    private void UpdateEmpty()
    {
        if (_view is null) return;
        EmptyText.Text = SearchBox.Text.Trim().Length > 0
            ? "No lists match. Clear the search to see all lists."
            : "No saved lists yet. Save a tab as a list to organize it here.";
        EmptyText.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnListSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Lists.SelectedItem is Entry entry) FolderChoice.SelectFolder(entry.Folder);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (MoveButton is null) return;
        MoveButton.IsEnabled = Lists.SelectedItem is Entry entry && _view?.Contains(entry) == true
            && FolderChoice.HasSelection && !string.Equals(entry.Folder, FolderChoice.SelectedFolder, StringComparison.OrdinalIgnoreCase);
        RenameButton.IsEnabled = RemoveButton.IsEnabled = FolderChoice.SelectedFolder is not null;
    }

    private void OnMove(object sender, RoutedEventArgs e)
    {
        if (!MoveButton.IsEnabled || Lists.SelectedItem is not Entry entry) return;
        string? destination = FolderChoice.SelectedFolder;
        try
        {
            _workspace.MoveSavedList(entry.Name, destination);
            StatusText.Text = $"Moved '{entry.Name}' to {destination ?? "the top level"}.";
        }
        catch (Exception ex) { StatusText.Text = $"Could not move list: {ex.Message}"; return; }
        RefreshAfterChange(entry.Name);
    }

    private void OnRenameFolder(object sender, RoutedEventArgs e)
    {
        if (FolderChoice.SelectedFolder is not { } folder) return;
        var dialog = new TextPromptWindow("Rename list folder", "Folder name:", folder) { Owner = this, AcceptText = "Rename" };
        dialog.ValidateAndAccept = name => { _workspace.RenameListFolder(folder, name); return null; };
        if (dialog.ShowDialog() == true) RefreshAfterChange((Lists.SelectedItem as Entry)?.Name);
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FolderChoice.SelectedFolder is not { } folder || !_confirmRemove(this, folder)) return;
        try { _workspace.RemoveListFolder(folder); StatusText.Text = "Folder removed; its lists remain at the top level."; }
        catch (Exception ex) { StatusText.Text = $"Could not remove folder: {ex.Message}"; return; }
        RefreshAfterChange((Lists.SelectedItem as Entry)?.Name);
    }

    private void RefreshAfterChange(string? selectedList)
    {
        try { Reload(selectedList); }
        catch (Exception ex)
        {
            // The mutation already succeeded; keep its success visible and don't invite repeating it.
            StatusText.Text += $" Could not refresh: {ex.Message} Close and reopen Organize lists.";
            Lists.IsEnabled = FolderChoice.IsEnabled = MoveButton.IsEnabled = RenameButton.IsEnabled = RemoveButton.IsEnabled = false;
        }
    }
}

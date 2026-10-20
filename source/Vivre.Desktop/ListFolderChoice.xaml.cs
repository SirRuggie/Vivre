using System.Windows;
using System.Windows.Controls;
using Vivre.Core.Computers;

namespace Vivre.Desktop;

/// <summary>The shared folder choice for Save list and Organize lists. No list contents are changed here.</summary>
public partial class ListFolderChoice : UserControl
{
    private Func<ListFolderSnapshot>? _read;
    private Action<string>? _create;
    private ListFolderSnapshot? _snapshot;
    private bool _refreshing;
    public bool HasExplicitSelection { get; private set; }
    private sealed record Choice(string Label, string? Folder);
    public event EventHandler? FolderChanged;
    public string? SelectedFolder => (Folders.SelectedItem as Choice)?.Folder;
    public bool HasSelection => Folders.SelectedItem is Choice;

    public ListFolderChoice() => InitializeComponent();

    public void Configure(Func<ListFolderSnapshot> read, Action<string> create, string? initialList = null)
    {
        _read = read;
        _create = create;
        var snapshot = read();
        SetChoices(snapshot, initialList is null ? null : snapshot.FolderFor(initialList));
    }

    public void Reload(string? selectedFolder)
        => SetChoices(_read!(), selectedFolder);

    private void SetChoices(ListFolderSnapshot snapshot, string? selectedFolder)
    {
        _snapshot = snapshot;
        _refreshing = true;
        try
        {
            Folders.ItemsSource = new[] { new Choice("Top level (no folder)", null) }
                .Concat(snapshot.Folders.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Select(name => new Choice(name, name))).ToArray();
            SelectFolder(selectedFolder);
        }
        finally { _refreshing = false; }
    }

    public void SelectFolder(string? folder)
        => Folders.SelectedItem = Folders.Items.Cast<Choice>().FirstOrDefault(item =>
            string.Equals(item.Folder, folder, StringComparison.OrdinalIgnoreCase));

    public void DefaultForList(string name)
    {
        if (HasExplicitSelection || _snapshot is null) return;
        _refreshing = true;
        try { SelectFolder(_snapshot.FolderFor(name.Trim())); }
        finally { _refreshing = false; }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing) HasExplicitSelection = true;
        FolderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNewFolder(object sender, RoutedEventArgs e)
    {
        if (_create is null) return;
        var dialog = new TextPromptWindow("New list folder", "Folder name:")
        { Owner = Window.GetWindow(this), AcceptText = "Create" };
        dialog.ValidateAndAccept = name =>
        {
            _create(name);
            // The creation succeeded; a failed refresh must not invite creating the same folder twice.
            try { Reload(name); StatusText.Text = ""; }
            catch (Exception ex)
            {
                Folders.ItemsSource = new[] { new Choice("Top level (no folder)", null), new Choice(name, name) };
                Folders.SelectedIndex = 1;
                StatusText.Text = $"Folder created. Other folders could not be refreshed; close and reopen this window to reload them. {ex.Message}";
            }
            HasExplicitSelection = true;
            return null;
        };
        dialog.ShowDialog();
    }
}

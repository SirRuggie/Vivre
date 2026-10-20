using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Vivre.Core.Computers;

namespace Vivre.Desktop;

public partial class ListFolderPickerWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Action<string?> _move;
    private readonly string? _current;
    private readonly ICollectionView _destinations;
    private sealed record Destination(string Label, string? Folder);

    public ListFolderPickerWindow(string list, ListFolderSnapshot folders, Action<string?> move)
    {
        InitializeComponent();
        _move = move;
        _current = folders.FolderFor(list);
        ListNameText.Text = $"Move: {list}";
        var choices = new[] { new Destination("Top level (outside folders)", null) }
            .Concat(folders.Folders.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new Destination(name, name))).ToArray();
        _destinations = CollectionViewSource.GetDefaultView(choices);
        _destinations.Filter = value => value is Destination choice
            && choice.Label.Contains(FolderSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        Destinations.ItemsSource = _destinations;
        Destinations.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.Folder, _current, StringComparison.OrdinalIgnoreCase));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_destinations is null) return;
        _destinations.Refresh();
        NoMatchesText.Visibility = _destinations.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        if (MoveButton is not null)
            MoveButton.IsEnabled = Destinations.SelectedItem is Destination choice
                && !string.Equals(choice.Folder, _current, StringComparison.OrdinalIgnoreCase);
    }

    private void OnMove(object sender, RoutedEventArgs e)
    {
        if (Destinations.SelectedItem is not Destination choice || !MoveButton.IsEnabled) return;
        try { _move(choice.Folder); DialogResult = true; }
        catch (Exception ex) { ErrorText.Text = $"Could not move list: {ex.Message}"; }
    }
}

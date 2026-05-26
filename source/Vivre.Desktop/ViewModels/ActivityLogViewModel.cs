using System.ComponentModel;
using System.Windows.Data;
using Vivre.Core.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// Backs the activity-log panel: a filtered view over the shared
/// <see cref="IActivityLog"/> entries. The search box matches machine OR message,
/// so typing a host name pinpoints it and typing "failed" shows failures.
/// </summary>
public partial class ActivityLogViewModel : ObservableObject
{
    private readonly IActivityLog _log;

    public ActivityLogViewModel(IActivityLog log)
    {
        _log = log;
        Entries = CollectionViewSource.GetDefaultView(log.Entries);
        Entries.Filter = Matches;
    }

    /// <summary>Filtered, newest-first view bound to the panel's grid.</summary>
    public ICollectionView Entries { get; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => Entries.Refresh();

    [RelayCommand]
    private void Clear() => _log.Clear();

    private bool Matches(object item)
    {
        if (item is not LogEntry entry)
        {
            return false;
        }

        string search = SearchText.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        return (entry.Machine?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}

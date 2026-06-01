using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// Backs the "How to use Vivre" window: a category-grouped, searchable list of <see cref="HelpTopic"/>
/// cards. Typing in the search box filters the list and auto-expands matches; clearing it collapses
/// everything back to the tidy default.
/// </summary>
public partial class HelpViewModel : ObservableObject
{
    private readonly CollectionViewSource _source;

    public HelpViewModel()
    {
        _source = new CollectionViewSource { Source = HelpContent.Topics };
        _source.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HelpTopic.Category)));
        _source.View.Filter = o => Matches((HelpTopic)o);
    }

    /// <summary>Grouped, filtered view bound by the window (groups become category headers).</summary>
    public ICollectionView Topics => _source.View;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        string q = value.Trim();

        // Auto-expand matches while searching; collapse all when the box is cleared.
        foreach (HelpTopic t in HelpContent.Topics)
        {
            t.IsExpanded = q.Length > 0 && t.Haystack.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        _source.View.Refresh();
    }

    private bool Matches(HelpTopic t)
    {
        string q = SearchText.Trim();
        return q.Length == 0 || t.Haystack.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (HelpTopic t in HelpContent.Topics)
        {
            t.IsExpanded = true;
        }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (HelpTopic t in HelpContent.Topics)
        {
            t.IsExpanded = false;
        }
    }
}

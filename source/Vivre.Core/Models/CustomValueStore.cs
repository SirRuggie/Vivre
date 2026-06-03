using System.ComponentModel;

namespace Vivre.Core.Models;

/// <summary>
/// A tiny bindable key→value bag for one machine's custom-column results. The indexer setter raises
/// <c>PropertyChanged("Item[]")</c> — the change notification WPF's indexer binding
/// (<c>{Binding CustomValues[ColumnName]}</c>) listens for — so a custom column's cell updates live as its
/// value is filled in by a sweep. Plain dictionary-backed; values are short display strings (null = unset).
/// </summary>
public sealed class CustomValueStore : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public string? this[string key]
    {
        get => _values.TryGetValue(key, out string? value) ? value : null;
        set
        {
            _values[key] = value;
            PropertyChanged?.Invoke(this, IndexerChanged);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

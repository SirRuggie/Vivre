using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Vivre.Desktop;

/// <summary>Shared grid preferences. Save only after user resize/reorder, never on scan/layout churn.</summary>
public sealed class GridLayoutPersistence
{
    private readonly DataGrid _grid;
    private readonly string _key;
    private readonly AppSettingsStore _store;
    private readonly Func<DataGridColumn, string> _columnKey;
    private readonly Dictionary<string, GridColumnPreference> _defaults = [];
    private List<GridColumnPreference> _baseline = [];
    private bool _loaded, _applying;

    public GridLayoutPersistence(DataGrid grid, string key, AppSettingsStore? store = null, Func<DataGridColumn, string>? columnKey = null)
    {
        _grid = grid; _key = key; _store = store ?? new(); _columnKey = columnKey ?? HeaderKey;
        foreach (var item in Capture()) _defaults.TryAdd(item.Key, item);
        grid.Loaded += OnLoaded;
        grid.Unloaded += OnUnloaded;
    }
    private static string HeaderKey(DataGridColumn column) => column.Header is TextBlock text
        ? new TextRange(text.ContentStart, text.ContentEnd).Text : column.Header?.ToString() ?? "";
    private List<GridColumnPreference> Capture() => _grid.Columns.Select(c =>
        new GridColumnPreference(_columnKey(c), c.Width.Value, (int)c.Width.UnitType, c.DisplayIndex)).ToList();
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Restore();
        _grid.ColumnReordered += OnReordered;
        _grid.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseUp), true);
        ((INotifyCollectionChanged)_grid.Columns).CollectionChanged += OnColumnsChanged;
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _grid.ColumnReordered -= OnReordered;
        _grid.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseUp));
        ((INotifyCollectionChanged)_grid.Columns).CollectionChanged -= OnColumnsChanged;
        _loaded = false;
    }
    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Restore();
    private void OnReordered(object? sender, DataGridColumnEventArgs e) => SaveUserLayout();
    private void OnMouseUp(object sender, MouseButtonEventArgs e) => SaveUserLayout();

    public void Restore()
    {
        if (_applying) return;
        _applying = true;
        try
        {
            foreach (var item in Capture()) _defaults.TryAdd(item.Key, item);
            var settings = _store.Load();
            var saved = settings.GridLayouts?.GetValueOrDefault(_key);
            Apply(_defaults.Values);
            Apply(saved ?? []);
            _baseline = Capture();
        }
        catch (Exception ex) { AppSettingsStore.ActivityLog?.Warn(null, $"Could not restore {_key} column layout: {ex.Message}"); }
        finally { _applying = false; }
    }
    private void Apply(IEnumerable<GridColumnPreference> saved)
    {
        var byKey = saved.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Key)).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
        var columns = _grid.Columns.ToList();
        foreach (var column in columns)
        {
            if (!byKey.TryGetValue(_columnKey(column), out var p)) continue;
            if (double.IsFinite(p.Width) && p.Width >= 0 && p.Width <= 4000 && Enum.IsDefined(typeof(DataGridLengthUnitType), p.Unit))
                column.Width = new DataGridLength(p.Width, (DataGridLengthUnitType)p.Unit);
        }
        // Identity remains first regardless of a malformed or old saved order.
        var ordered = columns.Where(c => !c.CanUserReorder).Concat(columns.Where(c => c.CanUserReorder)
            .OrderBy(c => byKey.TryGetValue(_columnKey(c), out var p) && p.Order >= 0 ? p.Order : int.MaxValue)).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].DisplayIndex = i;
    }
    public void SaveUserLayout()
    {
        if (_applying) return;
        var current = Capture();
        if (_baseline.SequenceEqual(current)) return;
        try
        {
            var settings = _store.Load();
            settings.GridLayouts ??= [];
            settings.GridLayouts[_key] = current;
            _store.Save(settings);
            _baseline = current;
        }
        catch (Exception ex) { AppSettingsStore.ActivityLog?.Warn(null, $"Could not save {_key} column layout: {ex.Message}"); }
    }
    public void Reset()
    {
        _applying = true;
        try
        {
            var settings = _store.Load();
            settings.GridLayouts?.Remove(_key);
            Apply(_defaults.Values);
            _baseline = Capture();
            _store.Save(settings);
        }
        catch (Exception ex) { AppSettingsStore.ActivityLog?.Warn(null, $"Could not reset {_key} column layout: {ex.Message}"); }
        finally { _applying = false; }
    }
}

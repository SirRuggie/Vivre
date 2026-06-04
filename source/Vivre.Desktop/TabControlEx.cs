using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Vivre.Desktop;

/// <summary>
/// A <see cref="TabControl"/> that keeps each tab's content alive instead of destroying and rebuilding it
/// on every tab switch (the WPF default). Each item's content lives in its own <see cref="ContentPresenter"/>
/// inside <c>PART_ItemsHolder</c>; switching tabs just toggles which presenter is visible.
/// </summary>
/// <remarks>
/// Needed so the Cross-Domain RDP tab's live RDP sessions survive switching to a machine tab and back — with the
/// default TabControl the embedded controls (and their connections) would be torn down on every switch.
/// Machine workspaces also benefit (no rebuild on switch). Based on the well-known TabControlEx pattern.
/// Because content is collapsed (not removed) on switch, a child's <c>Unloaded</c> fires only on real
/// removal (tab closed) — which is exactly when a session should be disposed.
/// </remarks>
public class TabControlEx : TabControl
{
    private Panel? _itemsHolder;

    public TabControlEx()
    {
        // The first selection can occur before the template/containers exist; re-sync when they're ready.
        ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
            {
                UpdateSelectedItem();
            }
        };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _itemsHolder = GetTemplateChild("PART_ItemsHolder") as Panel;
        UpdateSelectedItem();
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);

        if (_itemsHolder is null)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _itemsHolder.Children.Clear();
        }
        else if (e.OldItems is not null)
        {
            // Removing an item removes its presenter from the tree → its content's Unloaded fires (the cue
            // a session uses to disconnect + dispose its RDP control).
            foreach (object item in e.OldItems)
            {
                if (FindPresenter(item) is { } presenter)
                {
                    _itemsHolder.Children.Remove(presenter);
                }
            }
        }

        UpdateSelectedItem();
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        UpdateSelectedItem();
    }

    private void UpdateSelectedItem()
    {
        if (_itemsHolder is null)
        {
            return;
        }

        EnsurePresenter(SelectedItem);

        foreach (ContentPresenter child in _itemsHolder.Children.OfType<ContentPresenter>())
        {
            child.Visibility = Equals(child.Content, SelectedItem) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EnsurePresenter(object? item)
    {
        if (item is null || FindPresenter(item) is not null)
        {
            return;
        }

        // No explicit template — the presenter resolves the type-keyed implicit DataTemplate from the
        // control's resources (WorkspaceView / CrossDomainRdpView).
        _itemsHolder!.Children.Add(new ContentPresenter
        {
            Content = item,
            ContentTemplate = SelectedContentTemplate,
            ContentTemplateSelector = SelectedContentTemplateSelector,
            ContentStringFormat = SelectedContentStringFormat,
        });
    }

    private ContentPresenter? FindPresenter(object item) =>
        _itemsHolder?.Children.OfType<ContentPresenter>().FirstOrDefault(cp => Equals(cp.Content, item));
}

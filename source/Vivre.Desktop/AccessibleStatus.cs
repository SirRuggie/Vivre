using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Vivre.Desktop;

/// <summary>Announces changed dialog status without announcing every fleet progress update.</summary>
public static class AccessibleStatus
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AccessibleStatus), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty SubscriptionProperty = DependencyProperty.RegisterAttached(
        "Subscription", typeof(Subscription), typeof(AccessibleStatus));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (element.GetValue(SubscriptionProperty) is Subscription old) old.Dispose();
        element.ClearValue(SubscriptionProperty);
        if ((bool)e.NewValue) element.SetValue(SubscriptionProperty, new Subscription(element));
    }

    private sealed class Subscription : IDisposable
    {
        private readonly FrameworkElement _element;
        private readonly DependencyPropertyDescriptor _text;
        private bool _attached;
        public Subscription(FrameworkElement element)
        {
            _element = element;
            DependencyProperty property = element is TextBox ? TextBox.TextProperty : TextBlock.TextProperty;
            _text = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
            AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded) Attach();
        }
        private void OnLoaded(object sender, RoutedEventArgs e) => Attach();
        private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();
        private void Attach()
        {
            if (_attached) return;
            _text.AddValueChanged(_element, OnTextChanged);
            _attached = true;
        }
        private void Detach()
        {
            if (!_attached) return;
            _text.RemoveValueChanged(_element, OnTextChanged);
            _attached = false;
        }
        private void OnTextChanged(object? sender, EventArgs e)
        {
            if (!_element.IsVisible || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
            UIElementAutomationPeer.CreatePeerForElement(_element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        public void Dispose()
        {
            Detach();
            _element.Loaded -= OnLoaded;
            _element.Unloaded -= OnUnloaded;
        }
    }
}

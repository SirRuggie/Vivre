using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// Dedicated "Exclude updates" dialog — replaces the generic TextPromptWindow.
/// Accepts a comma-separated list of update-title terms to skip during scans/installs.
/// All behavior (read terms → save) is unchanged; only the presentation is improved.
/// </summary>
public partial class ExcludeUpdatesWindow : FluentWindow
{
    public ExcludeUpdatesWindow(string initialValue = "")
    {
        InitializeComponent();
        InputBox.Text = initialValue;

        // Populate the "Currently excluded" chip list from the initial value.
        RefreshTermsList(initialValue);

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };

        // Keep the terms list live as the user types.
        InputBox.TextChanged += (_, _) => RefreshTermsList(InputBox.Text);
    }

    /// <summary>The saved text once Save was pressed; null if cancelled.</summary>
    public string? Value { get; private set; }

    private void RefreshTermsList(string raw)
    {
        var terms = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .ToList();

        TermsList.ItemsSource = terms;
        TermsPanel.Visibility = terms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Save();
            e.Handled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        Value = InputBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

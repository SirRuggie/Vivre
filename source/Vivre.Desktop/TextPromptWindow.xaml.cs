using System.Windows;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Small reusable single-line text prompt (e.g. naming a saved list, renaming a tab).</summary>
public partial class TextPromptWindow : FluentWindow
{
    public TextPromptWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        TitleBarControl.Title = title;
        PromptLabel.Text = prompt;
        InputBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>The entered text once OK was pressed; null if cancelled.</summary>
    public string? Value { get; private set; }

    public Func<string, string?>? ValidateAndAccept { get; set; }
    public string AcceptText { get => (string)AcceptButton.Content; set => AcceptButton.Content = value; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            ErrorText.Text = "Enter a name.";
            return;
        }

        string? error;
        try { error = ValidateAndAccept?.Invoke(text); }
        catch (Exception ex) { error = ex.Message; }
        if (error is not null) { ErrorText.Text = error; return; }
        Value = text;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

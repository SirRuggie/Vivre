using System.Windows;
using System.Windows.Controls;
using System.Xml;
using Vivre.Core.Credentials;
using Vivre.Core.Models;
using Vivre.Core.PowerShell;
using Vivre.Core.Scripts;
using Vivre.Desktop.ViewModels;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Vivre.Desktop;

/// <summary>
/// The Run Script window. Code-behind is the AvalonEdit glue: load highlighting,
/// move text between the editor and the view model (AvalonEdit's Text isn't a
/// bindable dependency property), and hand the editor content to the run command.
/// </summary>
public partial class ScriptRunnerWindow : FluentWindow
{
    private readonly ScriptRunnerViewModel _viewModel;

    public ScriptRunnerWindow(IReadOnlyList<Computer> targets, CredentialStore credentials, Core.Logging.IActivityLog activity, IScriptLibrary library, ScriptFile? initialScript = null)
    {
        InitializeComponent();

        _viewModel = new ScriptRunnerViewModel(targets, new PSRunspaceHost(), library, credentials, activity);
        DataContext = _viewModel;

        Editor.SyntaxHighlighting = LoadPowerShellHighlighting();

        if (initialScript is not null)
        {
            // Pre-load the script the user picked from the cascading menu, ready to review and Run.
            Editor.Text = _viewModel.LoadScript(initialScript);
            _viewModel.ScriptName = initialScript.Name;
            _viewModel.SelectedScript = _viewModel.Scripts.FirstOrDefault(s => s.FullPath == initialScript.FullPath);
        }
        else
        {
            Editor.Text = "# Pick a saved script on the left, or type / paste PowerShell here.\n";
        }
    }

    private void OnScriptSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ScriptList.SelectedItem is ScriptFile script)
        {
            Editor.Text = _viewModel.LoadScript(script);
            _viewModel.ScriptName = script.Name;
            _viewModel.SaveCategory = string.IsNullOrEmpty(script.Category) ? "My Scripts" : script.Category;
        }
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (_viewModel.RunCommand.CanExecute(Editor.Text))
        {
            _viewModel.RunCommand.Execute(Editor.Text);
        }
    }

    private void OnOutputChanged(object sender, TextChangedEventArgs e) => OutputBox.ScrollToEnd();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.ScriptName))
        {
            _viewModel.SaveScript(_viewModel.ScriptName, Editor.Text);
        }
    }

    private async void OnDeleteScript(object sender, RoutedEventArgs e)
    {
        // The button is disabled for built-in defaults / no selection; this is a belt-and-braces guard.
        if (_viewModel.SelectedScript is not { } script || !_viewModel.CanDeleteSelectedScript)
        {
            return;
        }

        var confirm = new MessageBox
        {
            Title = "Delete script",
            Content = $"Delete '{script.Name}' from your library? This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            _viewModel.DeleteSelectedScript();
            Editor.Text = "# Pick a saved script on the left, or type / paste PowerShell here.\n";
        }
    }

    private static IHighlightingDefinition? LoadPowerShellHighlighting()
    {
        try
        {
            var assembly = typeof(ScriptRunnerWindow).Assembly;
            string? name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("PowerShell.xshd", StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return null;
            }

            using System.IO.Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            using var reader = new XmlTextReader(stream);
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch
        {
            // Highlighting is cosmetic — never let a bad definition break the editor.
            return null;
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Xml;
using Vivre.Core.Scripts;
using Vivre.Desktop.ViewModels;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Vivre.Desktop;

/// <summary>
/// The Scripts nav section — a library manager for the PowerShell script library.
/// Lets the user browse, edit, add, and delete scripts in
/// <c>%APPDATA%\Vivre\Scripts</c>. No run / no targets / no output panel.
/// Running scripts against machines stays in <see cref="ScriptRunnerWindow"/>.
/// Lives in the keep-alive content grid — never rebuilt on nav switches.
/// </summary>
public partial class ScriptsPage : UserControl
{
    private ScriptsViewModel? _vm;

    public ScriptsPage()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = LoadPowerShellHighlighting();
    }

    /// <summary>Called once by MainWindow after the page is in the visual tree.</summary>
    public void Initialize(IScriptLibrary library)
    {
        _vm = new ScriptsViewModel(library);
        DataContext = _vm;
    }

    // ── List selection ─────────────────────────────────────────────────────

    private void OnScriptSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (ScriptList.SelectedItem is ScriptFile script)
        {
            Editor.Text = _vm.LoadScript(script);
            _vm.ScriptName = script.Name;
            _vm.SaveCategory = string.IsNullOrEmpty(script.Category) ? "My Scripts" : script.Category;
        }
    }

    // ── Add ────────────────────────────────────────────────────────────────

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        // Deselect any script in the list and clear the editor + name for a new entry.
        ScriptList.SelectedItem = null;
        _vm.SelectedScript = null;
        _vm.ScriptName = string.Empty;
        _vm.SaveCategory = "My Scripts";
        Editor.Text = "# New script\n";
        NameBox.Focus();
    }

    // ── Save ───────────────────────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        if (string.IsNullOrWhiteSpace(_vm.ScriptName))
        {
            NameBox.Focus();
            return;
        }

        _vm.SaveScript(_vm.ScriptName, Editor.Text);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        // Belt-and-braces: the button should already be disabled for defaults / no selection.
        if (_vm.SelectedScript is not { } script || !_vm.CanDeleteSelectedScript)
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

        if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary)
        {
            return;
        }

        try
        {
            _vm.DeleteSelectedScript();
            Editor.Text = "# Pick a script on the left, or click Add to start a new one.\n";
        }
        catch (InvalidOperationException ex)
        {
            // Library throws for built-in defaults (shouldn't reach here because the button
            // is disabled, but surface it clearly rather than swallowing it).
            var warn = new MessageBox
            {
                Title = "Cannot delete",
                Content = ex.Message,
                CloseButtonText = "OK",
            };
            await warn.ShowDialogAsync();
        }
    }

    // ── AvalonEdit helpers ─────────────────────────────────────────────────

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

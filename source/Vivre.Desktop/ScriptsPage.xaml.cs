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
    public Func<ScriptEditChoice>? UnsavedChangesPromptOverride { get; set; }
    public Action<string, string>? ErrorPresenterOverride { get; set; }
    private ScriptsViewModel? _vm;
    private ScriptFile? _loadedScript;
    private readonly ScriptEditorDirtyState _dirty = new();
    private bool _changingSelection;

    public ScriptsPage()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = LoadPowerShellHighlighting();
    }

    /// <summary>Called once by MainWindow after the page is in the visual tree.</summary>
    public void Initialize(IScriptLibrary library)
    {
        _vm = new ScriptsViewModel(library);
        _vm.LibraryRefreshed += OnLibraryRefreshed;
        DataContext = _vm;
        MarkClean();
    }

    // ── List selection ─────────────────────────────────────────────────────

    private void OnScriptSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (ScriptList is null || Resources["GroupedScripts"] is not System.Windows.Data.CollectionViewSource { View: { } view }) return;
        string query = ScriptSearchBox.Text;
        string? currentPath = _loadedScript?.FullPath;
        bool wasChanging = _changingSelection;
        _changingSelection = true;
        try
        {
            view.Filter = item => item is ScriptFile script && (LibrarySearch.Matches(script, query)
                || string.Equals(script.FullPath, _loadedScript?.FullPath, StringComparison.OrdinalIgnoreCase));
            if (currentPath is not null)
                ScriptList.SelectedItem = view.Cast<ScriptFile>().FirstOrDefault(script => string.Equals(script.FullPath, currentPath, StringComparison.OrdinalIgnoreCase));
        }
        finally { _changingSelection = wasChanging; }
    }

    private void OnScriptSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || _changingSelection) return;
        ScriptFile? next = ScriptList.SelectedItem as ScriptFile;
        if (next is not null && _loadedScript is not null
            && string.Equals(next.FullPath, _loadedScript.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            _loadedScript = next;
            return;
        }
        if (!ResolveUnsavedChanges())
        {
            _changingSelection = true;
            ScriptList.SelectedItem = _loadedScript is null ? null : _vm.Scripts.FirstOrDefault(s => s.FullPath == _loadedScript.FullPath);
            _changingSelection = false;
            return;
        }
        if (next is { } script)
        {
            _changingSelection = true;
            ScriptList.SelectedItem = script;
            _vm.SelectedScript = script;
            _changingSelection = false;
            Editor.Text = _vm.LoadScript(script);
            _vm.ScriptName = script.Name;
            _vm.SaveCategory = string.IsNullOrEmpty(script.Category) ? "My Scripts" : script.Category;
            _loadedScript = script;
            MarkClean();
        }
    }

    // ── Add ────────────────────────────────────────────────────────────────

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (!ResolveUnsavedChanges()) return;

        // Deselect any script in the list and clear the editor + name for a new entry.
        _changingSelection = true;
        ScriptList.SelectedItem = null;
        _changingSelection = false;
        _vm.SelectedScript = null;
        _vm.ScriptName = string.Empty;
        _vm.SaveCategory = "My Scripts";
        Editor.Text = "# New script\n";
        _loadedScript = null;
        MarkClean();
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

        try
        {
            _changingSelection = true;
            ScriptFile saved = _vm.SaveScript(_vm.ScriptName, Editor.Text);
            _loadedScript = saved;
            MarkClean();
        }
        catch (Exception ex)
        {
            ShowError("Script was not saved", ex.Message);
        }
        finally { _changingSelection = false; }
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
            _changingSelection = true;
            _vm.DeleteScript(script);
            Editor.Text = "# Pick a script on the left, or click Add to start a new one.\n";
            _loadedScript = null;
            MarkClean();
        }
        catch (Exception ex)
        {
            var warn = new MessageBox
            {
                Title = "Script was not deleted",
                Content = ex.Message,
                CloseButtonText = "OK",
            };
            await warn.ShowDialogAsync();
        }
        finally { _changingSelection = false; }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedScript is not { } script || !_vm.CanResetSelectedScript) return;
        if (System.Windows.MessageBox.Show(Window.GetWindow(this),
            $"Replace your edited copy of '{script.Name}' with the current built-in version?",
            "Reset built-in script", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.OK) return;
        try
        {
            _changingSelection = true;
            _vm.ResetSelectedScript();
            ScriptFile refreshed = _vm.Scripts.First(s => s.FullPath == script.FullPath);
            Editor.Text = _vm.LoadScript(refreshed);
            _loadedScript = refreshed;
            MarkClean();
        }
        catch (Exception ex)
        {
            ShowError("Script was not reset", ex.Message);
        }
        finally { _changingSelection = false; }
    }

    public bool ConfirmCanClose() => ResolveUnsavedChanges();

    private bool ResolveUnsavedChanges()
    {
        if (_vm is null || !IsDirty()) return true;
        ScriptEditChoice choice = PromptUnsavedChanges();
        if (choice == ScriptEditChoice.Cancel) return false;
        if (choice == ScriptEditChoice.Discard)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(_vm.ScriptName))
        {
            NameBox.Focus();
            return false;
        }
        try
        {
            _changingSelection = true;
            ScriptFile saved = _vm.SaveScript(_vm.ScriptName, Editor.Text);
            _loadedScript = saved;
            MarkClean();
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Script was not saved", ex.Message);
            return false;
        }
        finally { _changingSelection = false; }
    }

    private bool IsDirty() => _vm is not null && _dirty.IsDirty(Editor.Text, _vm.ScriptName, _vm.SaveCategory);

    private void MarkClean()
    {
        if (_vm is null) return;
        _dirty.MarkClean(Editor.Text, _vm.ScriptName, _vm.SaveCategory);
        if (!string.IsNullOrWhiteSpace(ScriptSearchBox.Text)) OnScriptSearchChanged(this, null!);
    }

    private ScriptEditChoice PromptUnsavedChanges()
    {
        if (UnsavedChangesPromptOverride is not null) return UnsavedChangesPromptOverride();
        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(Window.GetWindow(this),
            "Save changes to the current script before continuing?", "Unsaved script changes",
            System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Warning);
        return result switch
        {
            System.Windows.MessageBoxResult.Yes => ScriptEditChoice.Save,
            System.Windows.MessageBoxResult.No => ScriptEditChoice.Discard,
            _ => ScriptEditChoice.Cancel
        };
    }

    private void ShowError(string title, string message)
    {
        if (ErrorPresenterOverride is not null) ErrorPresenterOverride(title, message);
        else System.Windows.MessageBox.Show(Window.GetWindow(this), message, title,
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    private void OnLibraryRefreshed(object? sender, EventArgs e)
    {
        if (_changingSelection || _vm is null || _loadedScript is null || IsDirty()) return;
        ScriptFile? refreshed = _vm.Scripts.FirstOrDefault(s => s.FullPath == _loadedScript.FullPath);
        if (refreshed is null) return;
        Editor.Text = _vm.LoadScript(refreshed);
        _loadedScript = refreshed;
        MarkClean();
    }

    public void Dispose()
    {
        if (_vm is null) return;
        _vm.LibraryRefreshed -= OnLibraryRefreshed;
        _vm.Dispose();
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

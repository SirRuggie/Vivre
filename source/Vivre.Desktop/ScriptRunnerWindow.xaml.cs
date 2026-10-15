using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
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
    public Func<ScriptEditChoice>? UnsavedChangesPromptOverride { get; set; }
    public Action<string, string>? ErrorPresenterOverride { get; set; }
    private readonly ScriptRunnerViewModel _viewModel;
    private readonly ScriptEditorDirtyState _dirty = new();
    private ScriptFile? _loadedScript;
    private bool _changingSelection;

    public ScriptRunnerWindow(IReadOnlyList<Computer> targets, IPowerShellHost powerShell, CredentialStore credentials, Core.Logging.IActivityLog activity, IScriptLibrary library, ScriptFile? initialScript = null, Vivre.Core.Operations.HostOperationCoordinator? operations = null)
    {
        InitializeComponent();

        // Use the shared routing host (passed in), NOT a raw new PSRunspaceHost() — so script runs share the
        // session's Kerberos→SMB transport cache (fast-fail on known-bad hosts; record WinRM-healthy on success).
        _viewModel = new ScriptRunnerViewModel(targets, powerShell, library, credentials, activity, operations);
        _viewModel.LibraryRefreshed += OnLibraryRefreshed;
        DataContext = _viewModel;

        Editor.SyntaxHighlighting = LoadPowerShellHighlighting();
        Closing += OnWindowClosing;
        Closed += (_, _) => { _viewModel.LibraryRefreshed -= OnLibraryRefreshed; _viewModel.Dispose(); };

        if (initialScript is not null)
        {
            // Pre-load the script the user picked from the cascading menu, ready to review and Run.
            Editor.Text = _viewModel.LoadScript(initialScript);
            _viewModel.ScriptName = initialScript.Name;
            _viewModel.SaveCategory = string.IsNullOrEmpty(initialScript.Category) ? "My Scripts" : initialScript.Category;
            _viewModel.SelectedScript = _viewModel.Scripts.FirstOrDefault(s => s.FullPath == initialScript.FullPath);
            _loadedScript = _viewModel.SelectedScript;
        }
        else
        {
            Editor.Text = "# Pick a saved script on the left, or type / paste PowerShell here.\n";
        }
        MarkClean();
    }

    private void OnScriptSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection) return;
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
            ScriptList.SelectedItem = _loadedScript is null ? null : _viewModel.Scripts.FirstOrDefault(s => s.FullPath == _loadedScript.FullPath);
            _changingSelection = false;
            return;
        }
        if (next is { } script)
        {
            _changingSelection = true;
            ScriptList.SelectedItem = script;
            _viewModel.SelectedScript = script;
            _changingSelection = false;
            Editor.Text = _viewModel.LoadScript(script);
            _viewModel.ScriptName = script.Name;
            _viewModel.SaveCategory = string.IsNullOrEmpty(script.Category) ? "My Scripts" : script.Category;
            _loadedScript = script;
            MarkClean();
        }
    }

    private bool _approvingRun;
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_approvingRun || !_viewModel.CanRun) return;
        string script = Editor.Text;
        ScriptOperationRisk risk = _viewModel.GetExecutionRisk(script);
        _approvingRun = true;
        try
        {
            if (risk != ScriptOperationRisk.ReadOnly)
            {
                var confirm = new MessageBox
                {
                    Title = risk == ScriptOperationRisk.Reboot ? "Run reboot script" : "Run script that can change machines",
                    Content = $"Run the captured script on {_viewModel.Targets.Count} selected machine(s)? "
                        + (risk == ScriptOperationRisk.Reboot
                            ? "This can restart machines and interrupt sessions. Unsaved work may be lost."
                            : "Custom or mutating scripts can change services, delete files or restart machines. Review the script before proceeding; unsaved work may be lost."),
                    PrimaryButtonText = $"Run on {_viewModel.Targets.Count}",
                    CloseButtonText = "Cancel",
                };
                if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary) return;
            }
            if (_viewModel.RunCommand.CanExecute(script)) _viewModel.RunCommand.Execute(script);
        }
        finally { _approvingRun = false; }
    }

    private void OnOutputChanged(object sender, TextChangedEventArgs e) => OutputBox.ScrollToEnd();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.ScriptName))
        {
            try
            {
                _changingSelection = true;
                _viewModel.SaveScript(_viewModel.ScriptName, Editor.Text);
                _loadedScript = _viewModel.SelectedScript;
                MarkClean();
            }
            catch (Exception ex)
            {
                ShowError("Script was not saved", ex.Message);
            }
            finally { _changingSelection = false; }
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
            try
            {
                _changingSelection = true;
                _viewModel.DeleteScript(script);
                Editor.Text = "# Pick a saved script on the left, or type / paste PowerShell here.\n";
                _loadedScript = null;
                MarkClean();
            }
            catch (Exception ex)
            {
                ShowError("Script was not deleted", ex.Message);
            }
            finally { _changingSelection = false; }
        }
    }

    private void OnResetScript(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedScript is not { } script || !_viewModel.CanResetSelectedScript) return;
        if (System.Windows.MessageBox.Show(this,
            $"Replace your edited copy of '{script.Name}' with the current built-in version?",
            "Reset built-in script", System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.OK) return;
        try
        {
            _changingSelection = true;
            _viewModel.ResetSelectedScript();
            ScriptFile refreshed = _viewModel.Scripts.First(s => s.FullPath == script.FullPath);
            Editor.Text = _viewModel.LoadScript(refreshed);
            _loadedScript = refreshed;
            MarkClean();
        }
        catch (Exception ex)
        {
            ShowError("Script was not reset", ex.Message);
        }
        finally { _changingSelection = false; }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmCanClose()) e.Cancel = true;
    }

    public bool ConfirmCanClose()
    {
        if (_viewModel.IsBusy)
        {
            ShowError("Script is still running", "Stop the active script run before closing this window.");
            return false;
        }
        return ResolveUnsavedChanges();
    }

    private bool ResolveUnsavedChanges()
    {
        if (!IsDirty()) return true;
        ScriptEditChoice choice = PromptUnsavedChanges();
        if (choice == ScriptEditChoice.Cancel) return false;
        if (choice == ScriptEditChoice.Discard) return true;
        if (string.IsNullOrWhiteSpace(_viewModel.ScriptName)) { NameBox.Focus(); return false; }
        try
        {
            _changingSelection = true;
            _viewModel.SaveScript(_viewModel.ScriptName, Editor.Text);
            _loadedScript = _viewModel.SelectedScript;
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

    private bool IsDirty() => _dirty.IsDirty(Editor.Text, _viewModel.ScriptName, _viewModel.SaveCategory);

    private void MarkClean() => _dirty.MarkClean(Editor.Text, _viewModel.ScriptName, _viewModel.SaveCategory);

    private ScriptEditChoice PromptUnsavedChanges()
    {
        if (UnsavedChangesPromptOverride is not null) return UnsavedChangesPromptOverride();
        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(this,
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
        else System.Windows.MessageBox.Show(this, message, title,
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    private void OnLibraryRefreshed(object? sender, EventArgs e)
    {
        if (_changingSelection || _loadedScript is null || IsDirty()) return;
        ScriptFile? refreshed = _viewModel.Scripts.FirstOrDefault(s => s.FullPath == _loadedScript.FullPath);
        if (refreshed is null) return;
        Editor.Text = _viewModel.LoadScript(refreshed);
        _loadedScript = refreshed;
        MarkClean();
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

using System.Collections.ObjectModel;
using System.Text;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Core.PowerShell;
using Vivre.Core.Scripts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// View model for the Run Script window (the PsScript feature). Lets the user pick a
/// saved script or paste one and run it against the machines selected in the grid,
/// through the verified <see cref="IPowerShellHost"/>. Output is appended per machine.
/// </summary>
public partial class ScriptRunnerViewModel : ObservableObject
{
    private readonly IPowerShellHost _powerShell;
    private readonly IScriptLibrary _library;
    private readonly CredentialStore _credentials;
    private readonly IActivityLog _activity;

    public ScriptRunnerViewModel(IReadOnlyList<Computer> targets, IPowerShellHost powerShell, IScriptLibrary library, CredentialStore credentials, IActivityLog activity)
    {
        Targets = targets;
        _powerShell = powerShell;
        _library = library;
        _credentials = credentials;
        _activity = activity;
        TargetSummary = targets.Count == 0
            ? "No machines selected"
            : $"Run against {targets.Count} machine(s): {string.Join(", ", targets.Select(t => t.Name))}";
        ReloadScripts();
    }

    /// <summary>Machines the script will run against (the grid's selection, or all).</summary>
    public IReadOnlyList<Computer> Targets { get; }

    /// <summary>Human-readable description of <see cref="Targets"/> for the window header.</summary>
    public string TargetSummary { get; }

    /// <summary>Saved scripts available to pick.</summary>
    public ObservableCollection<ScriptFile> Scripts { get; } = [];

    /// <summary>Existing category folders, offered in the Save "Folder" dropdown.</summary>
    public ObservableCollection<string> Categories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedScript))]
    public partial ScriptFile? SelectedScript { get; set; }

    /// <summary>True when a user-created script is selected (built-in defaults can't be deleted).</summary>
    public bool CanDeleteSelectedScript => SelectedScript is { } script && !_library.IsDefault(script);

    /// <summary>Name used when saving the current editor content back to the library.</summary>
    [ObservableProperty]
    public partial string ScriptName { get; set; } = string.Empty;

    /// <summary>Folder to save into (pick an existing one or type a new name to create it).</summary>
    [ObservableProperty]
    public partial string SaveCategory { get; set; } = "My Scripts";

    /// <summary>Accumulated run output, shown in the bottom pane.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>False while a run is in flight — gates the Run command + button so a second
    /// click can't start an overlapping pass over the same (production) targets.</summary>
    public bool CanRun => !IsBusy;

    /// <summary>Reads a saved script's contents (the window loads it into the editor).</summary>
    public string LoadScript(ScriptFile script) => _library.Load(script);

    /// <summary>Deletes the selected user script from the library (no-op for built-in defaults).</summary>
    public void DeleteSelectedScript()
    {
        if (SelectedScript is { } script && !_library.IsDefault(script))
        {
            _library.Delete(script);
            SelectedScript = null;
            ScriptName = string.Empty;
            ReloadScripts();
        }
    }

    /// <summary>Saves the editor content into <see cref="SaveCategory"/> (creating the folder if new).</summary>
    public void SaveScript(string name, string content)
    {
        string? category = string.IsNullOrWhiteSpace(SaveCategory) ? null : SaveCategory;
        ScriptFile saved = _library.Save(name, content, category);
        ReloadScripts();
        SelectedScript = Scripts.FirstOrDefault(s => s.FullPath == saved.FullPath);
    }

    /// <summary>
    /// Runs <paramref name="script"/> against every target. Source-generated as
    /// <c>RunCommand</c> + <c>RunCancelCommand</c>; the editor text is passed as the parameter.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRun))]
    private async Task RunAsync(string? script, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            Output = "(nothing to run)";
            return;
        }

        if (Targets.Count == 0)
        {
            Output = "No machines selected.";
            return;
        }

        IsBusy = true;
        var log = new StringBuilder();
        try
        {
            foreach (Computer target in Targets)
            {
                token.ThrowIfCancellationRequested();

                string machineOutput;
                try
                {
                    PSExecutionResult result = IsLocal(target.Name)
                        ? await _powerShell.RunLocalAsync(script, token)
                        : await _powerShell.RunRemoteAsync(target.Name, script, _credentials.Current?.ToPowerShellCredential(), cancellationToken: token);

                    machineOutput = FormatResult(result);
                    _activity.Info(target.Name, result.HadErrors ? "Script ran with errors" : "Script ran");
                }
                catch (OperationCanceledException)
                {
                    target.CommandResult = "(cancelled)";
                    throw;
                }
                catch (RemoteShellInitException ex)
                {
                    // The target's WinRM/PSRP shell init is failing — turn the cryptic
                    // "type initializer for 'InitialSessionState'…" remoting error into guidance.
                    machineOutput = $"FAILED (WinRM shell init): {ex.Message}";
                    _activity.Error(target.Name, $"Script failed — WinRM shell init: {ex.Message}");
                }
                catch (Exception ex) when (ex.IsWinRmUnavailable())
                {
                    // WinRM is broken on this box (Kerberos/SPN, or the service is down). Scripts run over
                    // WinRM and there is no remote alternative here, so name the honest one (RDP) instead of
                    // dumping the raw SSPI text. No new execution channel is introduced. On a Kerberos
                    // rejection the software check's DCOM fallback still works, so point at it too.
                    machineOutput = ex is KerberosWrongPrincipalException
                        ? "WinRM is broken on this box (Kerberos/SPN), so scripts can't run here remotely — RDP into the box to run them. (For installed software, use Software ▸ Check software…; for a reboot, use the Reboot Wave.)"
                        : "WinRM is broken on this box (Kerberos/SPN), so scripts can't run here remotely — RDP into the box to run them. (For a reboot, use the Reboot Wave.)";
                    _activity.Warn(target.Name, "Script skipped — WinRM unavailable (RDP to run scripts on this box).");
                }
                catch (Exception ex)
                {
                    machineOutput = $"FAILED: {ex.Message}";
                    _activity.Error(target.Name, $"Script failed — {ex.Message}");
                }

                // Output lands on the machine's own grid row (Command result column)…
                target.CommandResult = machineOutput;

                // …and in this window's combined log.
                log.AppendLine($"===== {target.Name} =====");
                log.AppendLine(machineOutput);
                log.AppendLine();
                Output = log.ToString();
            }
        }
        catch (OperationCanceledException)
        {
            log.AppendLine("(cancelled)");
            Output = log.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatResult(PSExecutionResult result)
    {
        var sb = new StringBuilder();
        foreach (var o in result.Output)
        {
            sb.AppendLine(o?.ToString());
        }

        foreach (string w in result.Warnings)
        {
            sb.AppendLine("WARN : " + w);
        }

        foreach (string err in result.Errors)
        {
            sb.AppendLine("ERROR: " + err);
        }

        if (result.Output.Count == 0 && !result.HadErrors)
        {
            sb.AppendLine("(no output)");
        }

        return sb.ToString().TrimEnd();
    }

    private void ReloadScripts()
    {
        Scripts.Clear();
        foreach (ScriptFile script in _library.List())
        {
            Scripts.Add(script);
        }

        Categories.Clear();
        foreach (string category in Scripts
                     .Select(s => s.Category)
                     .Where(c => !string.IsNullOrEmpty(c))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            Categories.Add(category);
        }
    }

    private static bool IsLocal(string host) => HostName.IsLocal(host);
}

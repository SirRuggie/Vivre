using System.Collections.ObjectModel;
using System.IO;
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
public partial class ScriptRunnerViewModel : ObservableObject, IDisposable
{
    private readonly Vivre.Core.Operations.HostOperationCoordinator _operations;
    private readonly IPowerShellHost _powerShell;
    private readonly IScriptLibrary _library;
    private readonly CredentialStore _credentials;
    private readonly IActivityLog _activity;
    public event EventHandler? LibraryRefreshed;

    public ScriptRunnerViewModel(IReadOnlyList<Computer> targets, IPowerShellHost powerShell, IScriptLibrary library, CredentialStore credentials, IActivityLog activity, Vivre.Core.Operations.HostOperationCoordinator? operations = null)
    {
        _operations = operations ?? new Vivre.Core.Operations.HostOperationCoordinator();
        Targets = targets.ToArray();
        _powerShell = powerShell;
        _library = library;
        _credentials = credentials;
        _activity = activity;
        TargetSummary = targets.Count == 0
            ? "No machines selected"
            : $"Run against {targets.Count} selected machine(s)";
        ReloadScripts();
        _library.LibraryChanged += OnLibraryChanged;
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
    [NotifyPropertyChangedFor(nameof(CanResetSelectedScript))]
    [NotifyPropertyChangedFor(nameof(SelectedScriptNotice))]
    public partial ScriptFile? SelectedScript { get; set; }

    /// <summary>True when a user-created script is selected (built-in defaults can't be deleted).</summary>
    public bool CanDeleteSelectedScript => SelectedScript is { } script && !_library.IsDefault(script);

    public bool CanResetSelectedScript => SelectedScript is { } script && _library.GetPolicy(script).CanReset;

    public string SelectedScriptNotice
    {
        get
        {
            if (SelectedScript is not { } script) return string.Empty;
            ScriptPolicy policy = _library.GetPolicy(script);
            return policy.CanReset && !policy.IsVerifiedBuiltIn
                ? "Your saved copy differs from the current built-in. Reset it to use the shipped version."
                : string.Empty;
        }
    }

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

    public ScriptOperationRisk GetExecutionRisk(string script) => SelectedScript is { } selected
        ? _library.GetPolicy(selected, script).OperationRisk
        : ScriptOperationRisk.ExclusiveMutation;

    /// <summary>Reads a saved script's contents (the window loads it into the editor).</summary>
    public string LoadScript(ScriptFile script) => _library.Load(script);

    /// <summary>Deletes the selected user script from the library (no-op for built-in defaults).</summary>
    public void DeleteSelectedScript()
    {
        if (SelectedScript is { } script && !_library.IsDefault(script))
        {
            DeleteScript(script);
        }
    }

    public void DeleteScript(ScriptFile script)
    {
        _library.Delete(script);
        if (SelectedScript?.FullPath == script.FullPath) SelectedScript = null;
        ScriptName = string.Empty;
        ReloadScripts();
    }

    /// <summary>Saves the editor content into <see cref="SaveCategory"/> (creating the folder if new).</summary>
    public void SaveScript(string name, string content, string? expectedRevision = null, string? categoryOverride = null)
    {
        string? category = categoryOverride ?? SaveCategory;
        if (string.IsNullOrWhiteSpace(category)) category = null;
        ScriptFile saved = _library.Save(name, content, category, expectedRevision);
        ReloadScripts();
        SelectedScript = Scripts.FirstOrDefault(s => s.FullPath == saved.FullPath);
    }

    public void ResetSelectedScript()
    {
        if (SelectedScript is not { } script) return;
        _library.ResetToDefault(script);
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

        ScriptOperationRisk risk = GetExecutionRisk(script);
        IsBusy = true;
        var log = new BoundedTextBuffer(PSScriptCapturePresentation.AggregateMaximumCharacters);
        try
        {
            foreach (Computer target in Targets)
            {
                token.ThrowIfCancellationRequested();

                using var claim = risk == ScriptOperationRisk.ReadOnly
                    ? _operations.TryBeginOperation(target.Name, "Read-only script")
                    : _operations.TryBeginCleanup(target.Name, "Run script");
                if (!claim.Acquired)
                {
                    target.CommandResult = claim.RefusalMessage ?? "Blocked by another host operation.";
                    _activity.Warn(target.Name, target.CommandResult);
                    log.Append($"===== {target.Name} =====\n{target.CommandResult}");
                    Output = PresentAggregate(log);
                    continue;
                }
                string machineOutput;
                try
                {
                    PSScriptCaptureResult result = IsLocal(target.Name)
                        ? await _powerShell.RunLocalScriptCaptureAsync(script, new PSScriptCaptureLimits(), token)
                        : await _powerShell.RunRemoteScriptCaptureAsync(target.Name, script, new PSScriptCaptureLimits(), _credentials.Current?.ToPowerShellCredential(), cancellationToken: token);

                    if (risk != ScriptOperationRisk.ReadOnly && result.HadErrors) claim.MarkRemoteStateUncertain();
                    machineOutput = PSScriptCapturePresentation.Format(result);
                    if (machineOutput.Length == 0 && !result.HadErrors) machineOutput = "(no output)";
                    _activity.Info(target.Name, result.HadErrors ? "Script ran with errors" : "Script ran");
                }
                catch (OperationCanceledException)
                {
                    if (risk != ScriptOperationRisk.ReadOnly) claim.MarkRemoteStateUncertain();
                    target.CommandResult = "(cancelled — verify host work and recovery before retrying)";
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
                    bool mayStillBeRunning = ex is RemoteSessionLostException { AtConnect: false };
                    if (risk != ScriptOperationRisk.ReadOnly && mayStillBeRunning)
                    {
                        claim.MarkRemoteStateUncertain();
                    }
                    // WinRM is broken on this box (Kerberos/SPN, or the service is down). Scripts run over
                    // WinRM and there is no remote alternative here, so name the honest one (RDP) instead of
                    // dumping the raw SSPI text. No new execution channel is introduced. On a Kerberos
                    // rejection the software check's DCOM fallback still works, so point at it too.
                    machineOutput = mayStillBeRunning
                        ? "Connection was lost while the script may still be running. Verify host work and recovery before starting conflicting work."
                        : ex is KerberosWrongPrincipalException
                        ? "WinRM is broken on this box (Kerberos/SPN), so scripts can't run here remotely — RDP into the box to run them. (For installed software, use Software ▸ Check software…; for a reboot, use the Reboot Wave.)"
                        : "WinRM is broken on this box (Kerberos/SPN), so scripts can't run here remotely — RDP into the box to run them. (For a reboot, use the Reboot Wave.)";
                    _activity.Warn(target.Name, mayStillBeRunning
                        ? "Script connection lost mid-run — host state is uncertain; verify before conflicting work."
                        : "Script skipped — WinRM unavailable (RDP to run scripts on this box).");
                }
                catch (Exception ex)
                {
                    if (risk != ScriptOperationRisk.ReadOnly) claim.MarkRemoteStateUncertain();
                    machineOutput = $"FAILED: {ex.Message}";
                    _activity.Error(target.Name, $"Script failed — {ex.Message}");
                }

                // Output lands on the machine's own grid row (Command result column)…
                target.CommandResult = machineOutput;

                // …and in this window's combined log.
                log.Append($"===== {target.Name} =====");
                log.Append(machineOutput);
                log.Append(string.Empty);
                Output = PresentAggregate(log);
            }
        }
        catch (OperationCanceledException)
        {
            log.Append("(cancelled)");
            Output = PresentAggregate(log);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReloadScripts()
    {
        string? selectedPath = SelectedScript?.FullPath;
        ScriptLibrarySnapshot snapshot = _library.Snapshot();
        ScriptProjectionSync.Apply(Scripts, snapshot.Scripts);

        // An editable ComboBox writes back when its items change. Preserve typed folder text.
        string saveCategory = SaveCategory;
        ScriptProjectionSync.ApplyCategories(Categories, snapshot.Categories);
        SaveCategory = saveCategory;
        SelectedScript = selectedPath is null ? null : Scripts.FirstOrDefault(s => PathsEqual(s.FullPath, selectedPath));
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        ReloadScripts();
        OnPropertyChanged(nameof(CanResetSelectedScript));
        OnPropertyChanged(nameof(SelectedScriptNotice));
        LibraryRefreshed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _library.LibraryChanged -= OnLibraryChanged;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsLocal(string host) => HostName.IsLocal(host);

    private static string PresentAggregate(BoundedTextBuffer buffer)
    {
        string text = buffer.ToString();
        if (!buffer.WasTruncated) return text;
        const string marker = "[aggregate output truncated]";
        int keep = Math.Max(0, PSScriptCapturePresentation.AggregateMaximumCharacters
            - marker.Length - Environment.NewLine.Length);
        if (text.Length > keep) text = text[..keep];
        return text + Environment.NewLine + marker;
    }
}

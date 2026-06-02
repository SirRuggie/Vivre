using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Management.Automation;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Core.Remediation;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// Backs the per-machine detail window: the live <see cref="Computer"/>, a private machine-filtered
/// view of the activity log, and the <b>triage actions</b> on the Vitals tab — start a stopped
/// service, free disk space, list/end processes. Remediation runs through <see cref="IRemediationService"/>
/// under the session credential; after each action the machine's vitals are re-checked
/// (<see cref="_refreshVitals"/>) so the score/readings update in place. Built via
/// <c>WorkspaceViewModel.CreateDetailViewModel</c>.
/// </summary>
public partial class ComputerDetailViewModel : ObservableObject
{
    private readonly IActivityLog _log;
    private readonly IRemediationService _remediation;
    private readonly Func<PSCredential?> _credential;
    private readonly Func<Task> _refreshVitals;

    public ComputerDetailViewModel(
        Computer computer,
        IActivityLog log,
        IRemediationService remediation,
        Func<PSCredential?> credential,
        Func<Task> refreshVitals)
    {
        Computer = computer;
        _log = log;
        _remediation = remediation;
        _credential = credential;
        _refreshVitals = refreshVitals;

        var source = new CollectionViewSource { Source = log.Entries };
        source.Filter += (_, e) =>
            e.Accepted = e.Item is LogEntry entry
                && string.Equals(entry.Machine, computer.Name, StringComparison.OrdinalIgnoreCase);
        Messages = source.View;
    }

    /// <summary>The live machine model bound throughout the window.</summary>
    public Computer Computer { get; }

    /// <summary>This machine's activity-log entries only (newest-first), kept live.</summary>
    public ICollectionView Messages { get; }

    /// <summary>Top processes by memory, populated on demand by <see cref="LoadProcessesCommand"/>.</summary>
    public ObservableCollection<ProcessInfo> TopProcesses { get; } = [];

    /// <summary>True while a triage action runs — gates the action buttons (via <see cref="IsIdle"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    /// <summary>Inverse of <see cref="IsBusy"/>, for button enable-state binding.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>The last triage action's result line, shown under the controls.</summary>
    [ObservableProperty]
    public partial string? TriageStatus { get; set; }

    /// <summary>Start a stopped auto-start service by display name (one-click — starting is reversible).</summary>
    [RelayCommand]
    private Task StartService(string? displayName) =>
        displayName is { Length: > 0 } name
            ? RunActionAsync($"Starting '{name}'…", async () =>
                (await _remediation.StartServiceAsync(Computer.Name, name, _credential())).Message)
            : Task.CompletedTask;

    /// <summary>Clears TEMP / Windows Update cache / recycle bin on the system drive. Destructive — the
    /// window confirms before calling this.</summary>
    public Task FreeDiskSpaceAsync() =>
        RunActionAsync("Freeing disk space…", async () =>
        {
            DiskCleanupResult r = await _remediation.FreeDiskSpaceAsync(Computer.Name, _credential());
            if (!r.Ok)
            {
                return r.Message;
            }

            double mb = r.ReclaimedBytes / 1024d / 1024d;
            string freed = $"{r.Message} — reclaimed {mb:N0} MB";
            return r.NewFreePercent is { } p ? $"{freed} (now {p:0.#}% free)" : freed;
        });

    /// <summary>Loads the top processes by memory into <see cref="TopProcesses"/> for the End-process list.</summary>
    [RelayCommand]
    private Task LoadProcesses() =>
        RunActionAsync("Reading processes…", async () =>
        {
            IReadOnlyList<ProcessInfo> procs = await _remediation.GetTopProcessesAsync(Computer.Name, _credential());
            TopProcesses.Clear();
            foreach (ProcessInfo p in procs)
            {
                TopProcesses.Add(p);
            }

            return $"Loaded {TopProcesses.Count} process(es)";
        },
        refresh: false);

    /// <summary>Force-ends a process. Destructive — the window confirms before calling this.</summary>
    public Task EndProcessAsync(ProcessInfo process) =>
        RunActionAsync($"Ending {process.Name} (PID {process.Id})…", async () =>
        {
            RemediationResult r = await _remediation.EndProcessAsync(Computer.Name, process.Id, _credential());
            if (r.Ok)
            {
                TopProcesses.Remove(process);
            }

            return r.Message;
        },
        refresh: false);

    /// <summary>Shared runner: busy-gate, run, log + status, then re-check this machine's vitals
    /// (unless <paramref name="refresh"/> is false — e.g. just listing processes).</summary>
    private async Task RunActionAsync(string startStatus, Func<Task<string>> action, bool refresh = true)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        TriageStatus = startStatus;
        try
        {
            string result = await action();
            TriageStatus = result;
            _log.Info(Computer.Name, $"Triage: {result}");

            if (refresh)
            {
                await _refreshVitals();
            }
        }
        catch (Exception ex)
        {
            TriageStatus = ex.Message;
            _log.Error(Computer.Name, $"Triage failed — {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace Vivre.Core.Models;

/// <summary>
/// A single SCCM client shown in the computer grid. Observable so the WPF
/// DataGrid updates live as ping/health results arrive (no backend yet —
/// Session 2 loads test data).
///
/// Faithful to the legacy <c>ComputerType</c> POCO (REBUILD_PLAN.md §11/§12):
/// <list type="bullet">
///   <item>Name        ← ComputerName</item>
///   <item>IsOnline     ← OnlineStatus (legacy used an Int16 tri-state; a bool is
///                        enough for the grid today, revisit if "unknown" is needed)</item>
///   <item>SiteCode     ← SiteCode</item>
///   <item>AgentVersion ← AgentVersion</item>
///   <item>LastStatus   ← StatusMessage</item>
///   <item>LastError    ← ErrorMessage</item>
/// </list>
/// </summary>
public partial class Computer : ObservableObject
{
    public Computer()
    {
    }

    public Computer(string name) => Name = name;

    /// <summary>Host name of the SCCM client.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Reachability: null = not checked yet (grey), true = responded (green), false = offline (red).</summary>
    [ObservableProperty]
    public partial bool? IsOnline { get; set; }

    /// <summary>ConfigMgr site code (e.g. "PS1"), null until queried.</summary>
    [ObservableProperty]
    public partial string? SiteCode { get; set; }

    /// <summary>ConfigMgr client agent version, null until queried.</summary>
    [ObservableProperty]
    public partial string? AgentVersion { get; set; }

    /// <summary>Most recent status message from an action or health check.</summary>
    [ObservableProperty]
    public partial string? LastStatus { get; set; }

    /// <summary>Most recent error message, null when the last operation succeeded.</summary>
    [ObservableProperty]
    public partial string? LastError { get; set; }

    /// <summary>
    /// Output of the most recent PowerShell/command run against this machine, shown in
    /// the grid's "Command result" column (full text on hover / in the script window).
    /// </summary>
    [ObservableProperty]
    public partial string? CommandResult { get; set; }

    // Health signals (null = unknown/not checked). true = condition present.
    /// <summary>A reboot is pending.</summary>
    [ObservableProperty]
    public partial bool? RebootRequired { get; set; }

    /// <summary>Required updates are missing.</summary>
    [ObservableProperty]
    public partial bool? MissingUpdates { get; set; }

    /// <summary>An install/update is in progress.</summary>
    [ObservableProperty]
    public partial bool? RunningUpdates { get; set; }

    /// <summary>An interactive user is logged on.</summary>
    [ObservableProperty]
    public partial bool? UserLoggedOn { get; set; }

    /// <summary>Last OS boot time, null if unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastRebootDisplay))]
    public partial DateTime? LastBootTime { get; set; }

    /// <summary>Relative "time since last reboot" (e.g. "3h", "2d") for the grid; exact value in the tooltip.</summary>
    public string? LastRebootDisplay => LastBootTime is { } boot ? Relative(DateTime.Now - boot) : null;

    /// <summary>
    /// Re-evaluates <see cref="LastRebootDisplay"/> (it's relative to <c>DateTime.Now</c>, so it
    /// drifts between health checks). Called on a timer by the shell so the grid stays current.
    /// </summary>
    public void RefreshRelativeTime() => OnPropertyChanged(nameof(LastRebootDisplay));

    private static string Relative(TimeSpan since)
    {
        if (since < TimeSpan.Zero)
        {
            return "0m";
        }

        return since.TotalDays >= 1 ? $"{(int)since.TotalDays}d"
            : since.TotalHours >= 1 ? $"{(int)since.TotalHours}h"
            : since.TotalMinutes >= 1 ? $"{(int)since.TotalMinutes}m"
            : "just now";
    }
}

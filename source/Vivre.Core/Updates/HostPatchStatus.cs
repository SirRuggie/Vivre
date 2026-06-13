namespace Vivre.Core.Updates;

/// <summary>The stage a host is at in the scan/install lifecycle.</summary>
public enum PatchPhase
{
    /// <summary>Idle / not yet scanned.</summary>
    Idle,

    /// <summary>A WUA search is running over WinRM.</summary>
    Scanning,

    /// <summary>Scan finished — <see cref="HostPatchStatus.AvailableCount"/> updates apply.</summary>
    Available,

    /// <summary>The SYSTEM task is downloading payloads.</summary>
    Downloading,

    /// <summary>The SYSTEM task is installing.</summary>
    Installing,

    /// <summary>The SYSTEM task is uninstalling.</summary>
    Uninstalling,

    /// <summary>The Server 2016 full-package lane is staging the cumulative update via DISM (an agent
    /// AddPackage run). Shown to the operator as "Staging"; it reduces to <see cref="PatchState.Scanning"/>
    /// for the display-state (so the colour/tally/Stop "working" logic is unchanged — only the chip label
    /// differs).</summary>
    Staging,

    /// <summary>Install finished and the box needs a reboot.</summary>
    PendingReboot,

    /// <summary>The box is rebooting / being waited for (Phase 2).</summary>
    Rebooting,

    /// <summary>Finished (installed, or nothing applicable).</summary>
    Done,

    /// <summary>Something failed — see <see cref="HostPatchStatus.Message"/>.</summary>
    Error,
}

/// <summary>
/// The glanceable display state for the grid's Status chip / message color / fleet counts — a
/// UI-agnostic reduction of <see cref="PatchPhase"/> combined with the row's reboot-pending flag.
/// One source of truth so the chip, the colored message, and the fleet tally never disagree.
/// </summary>
public enum PatchState
{
    /// <summary>Not scanned / nothing happening (grey).</summary>
    Idle,

    /// <summary>A scan is running (blue, indeterminate).</summary>
    Scanning,

    /// <summary>Scanned and updates are available to install (info).</summary>
    Available,

    /// <summary>Downloading payloads (blue).</summary>
    Downloading,

    /// <summary>Installing (blue).</summary>
    Installing,

    /// <summary>Uninstalling (blue — same working colour as <see cref="Installing"/>).</summary>
    Uninstalling,

    /// <summary>A reboot is required / in progress (amber).</summary>
    RebootPending,

    /// <summary>Finished / up to date / back online with nothing pending (green).</summary>
    Done,

    /// <summary>Something failed (red).</summary>
    Error,
}

/// <summary>
/// A snapshot of one host's patch state, emitted by <see cref="PatchService"/> via
/// <see cref="IProgress{T}"/>. The view model writes it onto the matching
/// <c>Computer</c> row each poll. Immutable — a new instance per update.
/// </summary>
/// <param name="Phase">Where the host is in the lifecycle.</param>
/// <param name="Message">Human-readable status for the grid (e.g. "Installing (3/8)").</param>
/// <param name="Percent">0-100 progress for the bar, or null when indeterminate.</param>
/// <param name="AvailableCount">Updates the scan found applicable (after the exclude filter).</param>
/// <param name="InstalledCount">Updates installed so far.</param>
/// <param name="FailedCount">Updates that failed to install.</param>
/// <param name="RebootPending">True once the install reports a reboot is required.</param>
public sealed record HostPatchStatus(
    PatchPhase Phase,
    string Message,
    int? Percent = null,
    int AvailableCount = 0,
    int InstalledCount = 0,
    int FailedCount = 0,
    bool RebootPending = false)
{
    /// <summary>The applicable updates (populated after a scan; empty otherwise).</summary>
    public IReadOnlyList<SoftwareUpdate> Updates { get; init; } = [];

    /// <summary>An error status with no counts.</summary>
    public static HostPatchStatus Failed(string message) => new(PatchPhase.Error, message);
}

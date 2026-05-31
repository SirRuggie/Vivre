namespace Vivre.Core.PowerShell;

/// <summary>
/// A remote PowerShell session ended unexpectedly — <b>not</b> because the caller cancelled, but
/// because the target tore the WinRM/PSRP session down (the box rebooted, WinRM went unhealthy, or
/// the pipeline was stopped server-side). Without this, the SDK's raw
/// <c>PipelineStoppedException.Message</c> ("The pipeline has been stopped.") leaks straight into
/// the UI; callers translate this into an actionable, host-named message instead.
/// </summary>
public sealed class RemoteSessionLostException : Exception
{
    public RemoteSessionLostException(string host, Exception inner)
        : base($"Lost connection to {host} — the remote session ended (the target may have rebooted or WinRM is unhealthy).", inner)
        => Host = host;

    /// <summary>The host whose session was lost.</summary>
    public string Host { get; }
}

/// <summary>
/// The target's WinRM/PSRP shell failed to initialise — classically the
/// "The type initializer for 'System.Management.Automation.Runspaces.InitialSessionState' threw an
/// exception" error. On a real box that means a pending reboot has corrupted the servicing/WSMan
/// stack, or too many WinRM shells are open (<c>MaxShellsPerUser</c>, default 30). Both clear only
/// by rebooting the target; until then <em>every</em> remote op against the host fails the same way,
/// so the caller should stop hammering it and tell the user to reboot it.
/// </summary>
public sealed class RemoteShellInitException : Exception
{
    public RemoteShellInitException(string host, Exception inner)
        : base($"WinRM/PSRP shell init failed on {host} — the target is likely reboot-pending or has too many open WinRM shells (MaxShellsPerUser). Reboot the target to clear it.", inner)
        => Host = host;

    /// <summary>The host whose shell init failed.</summary>
    public string Host { get; }
}

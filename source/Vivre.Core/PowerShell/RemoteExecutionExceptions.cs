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
    public RemoteSessionLostException(string host, Exception inner, bool atConnect = false)
        : base(BuildMessage(atConnect), inner)
    {
        Host = host;
        AtConnect = atConnect;
    }

    // The message is accurate to WHEN the session was lost: a connect-time failure ("couldn't connect at
    // all") must NOT read like a mid-operation drop ("the target may have rebooted"). The host name is
    // intentionally omitted — the grid row already names the machine, so repeating it in the message is
    // redundant. Branching here means every caller that surfaces this exception's Message (vitals,
    // ConfigMgr actions, the software/column probes, the reboot probe, …) gets the right wording with no
    // per-site change.
    private static string BuildMessage(bool atConnect) => atConnect
        ? "Couldn't reach over WinRM — service may be down (using SMB agent if 445 is open)."
        : "Lost connection — the box may have rebooted or WinRM is unhealthy.";

    /// <summary>The host whose session was lost.</summary>
    public string Host { get; }

    /// <summary>
    /// True when the failure happened at CONNECT time — the WinRM/PSRP runspace never opened, so <b>no</b>
    /// remote script ran on the target (nothing was dropped, registered, or started). This is the only
    /// session-loss it is safe to retry over a different transport for a state-changing op like install:
    /// a mid-run drop (<see langword="false"/>) may have left work already in flight on the box, so
    /// re-running it elsewhere could double-apply. Set by <see cref="PSRunspaceHost"/>'s connect-phase
    /// catch; the execute-phase catch leaves it <see langword="false"/>. (Scan is read-only and ignores it.)
    /// </summary>
    public bool AtConnect { get; }
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

/// <summary>
/// A connect-time Kerberos failure that makes WinRM unusable on the ambient Windows login for this host.
/// Covers BOTH of the Kerberos errors seen in this fleet:
/// <list type="bullet">
///   <item><b>SEC_E_WRONG_PRINCIPAL</b> (HRESULT <c>0x80090322</c>) — the KDC issued a service ticket the
///   target cannot honour because its Kerberos service identity no longer matches AD (a stale
///   machine-account password/SPN or an encryption-type mismatch, classic after a VM snapshot revert); and</item>
///   <item><b>SEC_E_TARGET_UNKNOWN</b> (HRESULT <c>0x80090303</c>, "Cannot find the computer … using
///   Kerberos authentication") — Kerberos can't find an SPN for the host, i.e. it has no AD computer
///   account / isn't domain-joined.</item>
/// </list>
/// Either way this is an <em>authentication/identity failure at login</em>, NOT a mid-run session drop, so
/// it must not be mislabeled as <see cref="RemoteSessionLostException"/> ("the target may have rebooted").
/// WinRM cannot be salvaged on such a host with the ambient login (Kerberos-by-name fails, and WinRM
/// refuses NTLM to an IP without explicit credentials), so callers switch the host to the SMB/DCOM
/// transport. The condition is per-host and persists until the host's Kerberos identity/SPN is repaired (or
/// it is domain-joined), so callers cache the decision and stop attempting WinRM for the host.
/// <para>The type name is retained (it predates the target-unknown case) to avoid churn across the catch
/// sites; it now represents the broader "WinRM Kerberos unavailable" condition.</para>
/// </summary>
public sealed class KerberosWrongPrincipalException : Exception
{
    public KerberosWrongPrincipalException(string host, Exception inner)
        : base($"WinRM Kerberos authentication failed for {host} — its Kerberos SPN is missing or incorrect (SEC_E_WRONG_PRINCIPAL/0x80090322 or target-unknown/0x80090303: a stale machine-account/SPN, an encryption-type mismatch, or a host that isn't domain-joined). WinRM cannot be used with the current Windows login.", inner)
        => Host = host;

    /// <summary>The host whose Kerberos WinRM authentication failed.</summary>
    public string Host { get; }
}

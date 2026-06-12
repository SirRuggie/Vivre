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

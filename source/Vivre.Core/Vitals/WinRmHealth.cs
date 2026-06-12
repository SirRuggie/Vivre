namespace Vivre.Core.Vitals;

/// <summary>
/// Whether a host's WinRM/Kerberos transport is healthy, as observed while gathering vitals. This is
/// transport metadata, not an OS reading — it does not count toward <see cref="MachineVitals.IsEmpty"/>,
/// and it feeds the Vitals "needs attention" finding rather than the runtime-health math directly.
/// It never appears on an operation result (the no-"fell back" contract); only in the health channel.
/// </summary>
public enum WinRmHealth
{
    /// <summary>WinRM/Kerberos works (the fast primary path).</summary>
    Healthy = 0,

    /// <summary>
    /// WinRM Kerberos authentication failed (SEC_E_WRONG_PRINCIPAL 0x80090322 or target-unknown
    /// 0x80090303); vitals were gathered over the SMB/DCOM path instead. The host needs a Kerberos/SPN
    /// fix (or to be domain-joined), so the scorer flags it for attention and names the fix.
    /// </summary>
    KerberosRejected,

    /// <summary>
    /// WinRM failed for a NON-Kerberos reason (the service is stopped/misconfigured, or the session
    /// dropped mid-read), but vitals were still read over DCOM on the current login. Distinct from
    /// <see cref="KerberosRejected"/> because the fix differs (check the WinRM service, not the SPN) and
    /// it's often transient — so it is NOT cached as a permanent transport decision. The scorer still
    /// flags it for attention so a DCOM-rescued row never looks identical to a healthy WinRM read.
    /// </summary>
    WinRmUnavailable,
}

namespace Vivre.Core.Updates;

/// <summary>
/// What the OS said when the DCOM reboot method on <c>Win32_OperatingSystem</c> was invoked — the
/// classified form of its raw return code. Kept separate from the transport so the decision is pure and
/// testable without a live box (see <see cref="ShutdownReturnCode"/>).
/// </summary>
internal enum ShutdownCallOutcome
{
    /// <summary>Return code 0 — the OS accepted the reboot. The box is going down; nothing else to try.</summary>
    Accepted,

    /// <summary>1115 (<c>ERROR_SHUTDOWN_IN_PROGRESS</c>) — a shutdown is ALREADY underway on the target, so
    /// the box IS going offline on its own. Not a failure and not a reason to issue a second reboot: the
    /// caller reports "already in progress" and lets the wave watch for the commit.</summary>
    AlreadyInProgress,

    /// <summary>1191 (<c>ERROR_SHUTDOWN_USERS_LOGGED_ON</c>) — Windows refused the <b>graceful</b> form of the
    /// shutdown because a user session exists on the box (Active <em>or</em> disconnected; an RDP session left
    /// hanging counts). The DCOM channel is HEALTHY — authentication, the CIM query, and the method invocation
    /// all succeeded, and the OS answered with a specific policy refusal. It is <b>not</b> a transport failure
    /// and specifically <b>not</b> a Kerberos/SPN symptom: a Kerberos-broken box fails earlier, as a thrown
    /// access/authentication error, never as a numeric return code. Only the graceful form was refused — the
    /// forced form (reboot | force) is what clears it.</summary>
    GracefulRefused,

    /// <summary>Any other non-zero code — the call did not take. The caller falls back to the SMB/SCM channel.</summary>
    Failed,

    /// <summary>The method returned NO result code at all (a null <c>ReturnValue</c>). Never success: an
    /// accepted call always answers with an explicit 0, so a missing code means the reboot cannot be
    /// confirmed. (Same reasoning as the sibling guard in <c>WinRmEnabler.InterpretCreateReturn</c>.)</summary>
    NoResultCode,
}

/// <summary>
/// The pure razor over the DCOM reboot method's return code: raw code in, <see cref="ShutdownCallOutcome"/>
/// out. No I/O, no side effects, no CIM types — so every branch the reboot transport takes is coverable by a
/// unit test, including the codes a live box only produces in rare states (1115, 1191).
/// </summary>
internal static class ShutdownReturnCode
{
    /// <summary>Win32 <c>ERROR_SHUTDOWN_IN_PROGRESS</c> (HRESULT 0x8007045B) — a shutdown is already underway.</summary>
    internal const uint AlreadyInProgress = 1115;

    /// <summary>Win32 <c>ERROR_SHUTDOWN_USERS_LOGGED_ON</c> — a session exists, so the graceful form was refused.</summary>
    internal const uint UsersLoggedOn = 1191;

    /// <summary>
    /// Classifies the return code the OS gave back. The 1115 and 1191 matches are EXACT, not ranges —
    /// neighbouring codes (1190, 1192, …) are ordinary failures.
    /// </summary>
    /// <param name="code">The raw return code, or <c>null</c> when the call produced no result code at all.</param>
    internal static ShutdownCallOutcome Classify(uint? code) => code switch
    {
        null => ShutdownCallOutcome.NoResultCode,
        0 => ShutdownCallOutcome.Accepted,
        AlreadyInProgress => ShutdownCallOutcome.AlreadyInProgress,
        UsersLoggedOn => ShutdownCallOutcome.GracefulRefused,
        _ => ShutdownCallOutcome.Failed,
    };
}

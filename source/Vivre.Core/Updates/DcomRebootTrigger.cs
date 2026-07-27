using System.ComponentModel;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Vivre.Core.Remoting;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IRebootTrigger"/>
/// <remarks>
/// Reboots over DCOM via the <c>Win32_OperatingSystem</c> shutdown method on the ambient Windows login —
/// the same channel vitals use. Flags: 2 = reboot (graceful — services get their normal stop sequence so
/// SQL flushes), 6 = reboot + force (2 | 4) for the escalation when a graceful reboot won't take or is
/// refused.
///
/// <para><b>1191 is NOT a broken channel.</b> Windows answers the GRACEFUL form with 1191
/// (<c>ERROR_SHUTDOWN_USERS_LOGGED_ON</c>) whenever any session exists on the box — Active <em>or</em>
/// merely disconnected. Authentication, the CIM query and the method invocation all SUCCEEDED; the OS
/// simply refused that form, and the force flag is the only thing that clears it (the shutdown-tracker
/// method returns 1191 too). It has nothing to do with the SPN/Kerberos cause that breaks WinRM — a
/// Kerberos-broken box fails EARLIER, as a thrown access/authentication error, never as a return code.
/// So a 1191 on a graceful call is answered here by re-sending the FORCED form on the SAME session —
/// never by switching transports. That escalation completes a reboot the operator already ordered and
/// confirmed on this box (see <see cref="RebootDispatch.EscalatedToForced"/>); it is never an independent
/// decision to reboot or force anything.</para>
///
/// <para><b>SMB/SCM fallback:</b> reserved for boxes DCOM genuinely cannot REACH or that refuse outright —
/// a connect/auth failure (the Kerberos-broken Vision boxes, whose http SPN belongs to the SSRS service
/// account rather than the box), a timeout, a missing result code, or a non-zero code with no escalation
/// left. Those all arrive on a different path from a numeric refusal: a transport failure THROWS. The
/// fallback is the <em>proven</em> channel that already delivers the update agent: create a one-shot
/// LocalSystem service whose image runs <c>shutdown.exe</c> (NTLM SSO over <c>\\host\IPC$\svcctl</c>, no
/// Kerberos). The fallback sends the caller's form (graceful = no <c>/f</c>, force = <c>/f</c>) EXCEPT when
/// the DCOM attempt already proved the OS itself refuses the GRACEFUL form (1191): that knowledge is
/// threaded out as <c>ForceRequired</c> and the fallback then sends <c>/f</c>, because a graceful
/// <c>shutdown.exe</c> against a box that has just refused a graceful shutdown would only be refused again
/// (see <see cref="FallbackForced"/>). Boxes DCOM can reach never touch it.</para>
/// </remarks>
public sealed class DcomRebootTrigger : IRebootTrigger
{
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(20);

    private const int EwxReboot = 2;
    private const int EwxForce = 4;

    // Win32 ERROR_SHUTDOWN_IN_PROGRESS (HRESULT 0x8007045B). A reboot call that comes back with this means
    // a shutdown is ALREADY underway — the box is going offline on its own, so it is NOT a reboot failure.
    // Single source of truth lives on the classifier; this int alias is what IsShutdownInProgress compares
    // a Win32Exception.NativeErrorCode against.
    private const int ErrorShutdownInProgress = (int)ShutdownReturnCode.AlreadyInProgress;

    private readonly Vivre.Core.Logging.IActivityLog? _trace;

    /// <param name="trace">Optional file-only diagnostic breadcrumb sink — records which channel (DCOM vs the
    /// SMB/SCM fallback) took the reboot, for post-hoc diagnosis. Never mirrored to the UI. Null = no tracing.
    /// Purely observational: it changes no logic, no flags, and no call path.</param>
    public DcomRebootTrigger(Vivre.Core.Logging.IActivityLog? trace = null)
    {
        _trace = trace;
    }

    public Task<RebootDispatch> RebootAsync(string host, bool forced, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return Task.Run(() => RebootSync(host, forced, cancellationToken), cancellationToken);
    }

    private RebootDispatch RebootSync(string host, bool forced, CancellationToken cancellationToken)
    {
        // 1) Preferred path: DCOM (works on healthy, domain-correct boxes — including the ones that refuse a
        //    GRACEFUL shutdown with 1191, which TryDcomShutdown resolves in-place by escalating to forced).
        //    A non-null dispatch means DCOM RESOLVED the reboot; null means it must fall back.
        (RebootDispatch? dispatch, string dcomFailure, bool forceRequired) = TryDcomShutdown(host, forced, cancellationToken);
        if (dispatch is { } resolved)
        {
            // Accepted (0), accepted after the graceful→forced escalation, or a shutdown ALREADY in progress
            // (1115 — the box is going offline on its own). All three are traced at the send site with the
            // real flags; nothing further to try on this box.
            return resolved;
        }

        // 2) DCOM did NOT resolve it — a connect/auth failure (Kerberos-broken box), a timeout, a missing
        //    result code, or a code the OS refused with no escalation left. Fall back to the SMB/SCM
        //    channel — the same transport that delivers the agent, which authenticates over NTLM.
        //    forceRequired carries the ONE thing the DCOM attempt proved when it reached the box: the OS
        //    itself refused the GRACEFUL form, so the fallback must not send that same form again.
        bool smbForced = FallbackForced(forced, forceRequired);
        cancellationToken.ThrowIfCancellationRequested();
        _trace?.Trace(host, $"reboot channel: DCOM failed ({dcomFailure}) — falling back to SMB/SCM forced={smbForced}");
        try
        {
            RebootViaSmbScm(host, smbForced);

            // The box may be going down FORCED on a form the CALLER never asked for (the OS refused the
            // graceful one) — the wave has to know that, or it applies the graceful go-offline window to a
            // box under /f. FallbackDispatch is that report; the trace keeps the two channels apart.
            RebootDispatch smbDispatch = FallbackDispatch(forced, smbForced);
            _trace?.Trace(host, smbDispatch == RebootDispatch.EscalatedToForced
                ? $"reboot channel: SMB/SCM issued forced={smbForced} → EscalatedToForced (the OS refused the graceful form over DCOM; the force came from that refusal, and this is the SMB/SCM channel — NOT the DCOM escalation)"
                : $"reboot channel: SMB/SCM issued forced={smbForced} → Issued");
            return smbDispatch;
        }
        catch (Exception smbEx)
        {
            // Even the fallback can report "a shutdown is already in progress" — treat that as going-offline,
            // not a failure (don't turn a box that's actually rebooting into a red error).
            if (IsShutdownInProgress(smbEx))
            {
                _trace?.Trace(host, $"reboot channel: SMB/SCM reports a shutdown already in progress (1115) forced={smbForced}");
                return RebootDispatch.AlreadyInProgress;
            }

            // Both channels failed — surface both reasons so the wave flags the box (it never auto-forces
            // beyond the escalation it already drives).
            throw new InvalidOperationException(
                $"Couldn't reboot {host}. DCOM: {dcomFailure}. SMB/SCM fallback: {smbEx.Message}", smbEx);
        }
    }

    /// <summary>The form the SMB/SCM fallback ACTUALLY puts on the wire: the caller's request, OR forced
    /// because the OS ITSELF refused the GRACEFUL form over DCOM (1191 — a session is logged on, Active or
    /// merely disconnected). Falling back to a graceful <c>shutdown.exe</c> against a box that has just
    /// refused a graceful shutdown throws away what the DCOM attempt proved and only gets refused again.
    /// <para><b>Cardinal scope:</b> this picks the FORM of a reboot the operator already selected and
    /// confirmed on this box — it is never a decision to reboot, and never widens WHICH boxes reboot.</para>
    /// <para>Internal rather than private so the reboot-wave harness can drive the REAL decision instead of
    /// duplicating it.</para></summary>
    internal static bool FallbackForced(bool requested, bool gracefulRefusedByTheOs) => requested || gracefulRefusedByTheOs;

    /// <summary>What the SMB/SCM fallback REPORTS for a reboot it issued. Normally
    /// <see cref="RebootDispatch.Issued"/> — but <see cref="RebootDispatch.EscalatedToForced"/> when the
    /// forced form came from the OS REFUSING the graceful one (1191) rather than from the caller, because
    /// that is exactly what the wave must do with such a box: apply the FORCED go-offline window (a box under
    /// <c>/f</c> mid-CBS-commit can hold the network well past the graceful window) and NEVER dispatch again.
    /// The channel it travelled is a trace concern, not a dispatch one.
    /// <para>A caller-REQUESTED forced reboot stays <see cref="RebootDispatch.Issued"/>: that leg is already
    /// on the forced window, and the wave asserts the trigger never escalates a call that already asked for
    /// the forced form.</para>
    /// <para><b>Cardinal scope:</b> pure reporting — it picks no flags, sends nothing, and cannot widen WHICH
    /// boxes reboot. The reboot was already issued by the line above it.</para>
    /// <para>Internal rather than private so the reboot-wave harness can drive the REAL decision instead of
    /// duplicating it.</para></summary>
    internal static RebootDispatch FallbackDispatch(bool requested, bool sentForced) =>
        sentForced && !requested ? RebootDispatch.EscalatedToForced : RebootDispatch.Issued;

    /// <summary>True when an error indicates a shutdown is ALREADY in progress on the target (Win32 1115 /
    /// ERROR_SHUTDOWN_IN_PROGRESS, HRESULT 0x8007045B) — i.e. the box is already going offline, so a reboot
    /// "failure" here is really "it's already rebooting". Best-effort across the forms it can take (a typed
    /// Win32Exception/COMException code, or the message text).</summary>
    private static bool IsShutdownInProgress(Exception ex)
    {
        if (ex is Win32Exception w && w.NativeErrorCode == ErrorShutdownInProgress)
        {
            return true;
        }

        if (ex.HResult == unchecked((int)0x8007045B))
        {
            return true;
        }

        string m = ex.Message ?? string.Empty;
        return m.Contains("shutdown is already in progress", StringComparison.OrdinalIgnoreCase)
            || m.Contains("a system shutdown is in progress", StringComparison.OrdinalIgnoreCase)
            || m.Contains("shutdown is in progress", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Issues the reboot over DCOM and reports whether DCOM RESOLVED it.
    /// <para>A NON-NULL <c>Dispatch</c> means DCOM took the reboot and nothing else must be tried:
    /// <see cref="RebootDispatch.Issued"/> (code 0), <see cref="RebootDispatch.EscalatedToForced"/> (a
    /// graceful call refused with 1191 and re-sent forced on the same session, accepted), or
    /// <see cref="RebootDispatch.AlreadyInProgress"/> (1115 — the box is already going offline).</para>
    /// <para>A NULL <c>Dispatch</c> plus a reason means DCOM did NOT resolve it and the caller falls back to
    /// the SMB/SCM channel: a thrown connect/auth/timeout failure, a missing result code (never success —
    /// an accepted call always answers with an explicit 0), or a non-zero code with no escalation left.</para>
    /// <para><c>ForceRequired</c> is true on exactly the arms where the OS ITSELF answered 1191 on this call:
    /// the escalated-then-failed one, the already-forced one, and a THROW from the ESCALATED send (the
    /// refusal came first, so the throw cannot un-prove it). It tells the fallback that a graceful
    /// <c>shutdown.exe</c> would be refused too, so it must send <c>/f</c>. Every other arm — including a
    /// throw with NO prior 1191, the Kerberos-broken-box path — leaves it false, so the fallback behaves
    /// exactly as before there.</para>
    /// <para><b>Loop guard:</b> the escalation fires ONLY when the caller asked for the graceful form
    /// (<c>!forced</c>). A forced call that itself comes back 1191 is NEVER retried — it falls back instead.
    /// That guard is monotone because <c>forced</c> is a by-value parameter that is never reassigned, so the
    /// escalated send can never re-enter this decision; keying the guard on the return code instead of on
    /// <c>forced</c> would NOT terminate.</para>
    /// Cancellation propagates, and is re-checked immediately before the escalated send.</summary>
    private (RebootDispatch? Dispatch, string Failure, bool ForceRequired) TryDcomShutdown(string host, bool forced, CancellationToken cancellationToken)
    {
        // The form ACTUALLY put on the wire by the most recent send — NEVER the caller's request. A graceful
        // call that escalates reads forced=True / flags=6 from the escalated send onward, so every channel
        // trace line (DCOM and SMB/SCM alike) reports the same countable field with the same meaning.
        bool sentForced = forced;
        int sentFlags = forced ? EwxReboot | EwxForce : EwxReboot;

        // Has the OS ITSELF answered 1191 on THIS call? Once it has, that refusal is proven knowledge no
        // later failure can erase — including a THROW from the escalated send — so it must travel out as
        // ForceRequired. Deliberately NOT sentForced: that is also true when the CALLER asked for forced,
        // which would blur "the OS refused the graceful form" into "the caller wanted force".
        bool gracefulRefusedByTheOs = false;
        try
        {
            using var options = new DComSessionOptions { Timeout = CimTimeout };
            using CimSession session = CimSession.Create(host, options);
            using var cimOptions = new CimOperationOptions
            {
                Timeout = CimTimeout,
                CancellationToken = cancellationToken,
            };

            foreach (CimInstance os in session.QueryInstances(
                         @"root\cimv2", "WQL", "SELECT __PATH FROM Win32_OperatingSystem", cimOptions))
            {
                using (os)
                {
                    uint? code = SendShutdown(session, os, sentFlags, cimOptions);
                    _trace?.Trace(host, $"reboot channel: DCOM shutdown sent forced={sentForced} flags={sentFlags} → returned {Describe(code)}");

                    switch (ShutdownReturnCode.Classify(code))
                    {
                        // 0 — the OS took the reboot.
                        case ShutdownCallOutcome.Accepted:
                            _trace?.Trace(host, $"reboot channel: DCOM accepted forced={sentForced} flags={sentFlags}");
                            return (RebootDispatch.Issued, string.Empty, false);

                        // 1115 = a shutdown is already in progress → the box IS going offline; not a failure.
                        case ShutdownCallOutcome.AlreadyInProgress:
                            _trace?.Trace(host, $"reboot channel: DCOM reports a shutdown already in progress (1115) forced={sentForced} flags={sentFlags}");
                            return (RebootDispatch.AlreadyInProgress, string.Empty, false);

                        // 1191 on a GRACEFUL call = Windows refused THAT FORM because a session is logged on
                        // (Active or merely disconnected). The channel is HEALTHY — the query and the method
                        // call both worked — so switching transports fixes nothing. Complete the reboot the
                        // operator already ordered and confirmed by re-sending the FORCED form on the SAME
                        // session and the SAME instance we still hold. One extra send, never a loop.
                        case ShutdownCallOutcome.GracefulRefused when !forced:
                        {
                            // The OS has now REFUSED the graceful form on this call. Record it BEFORE the
                            // escalated send, so a throw from that send still carries the refusal out.
                            gracefulRefusedByTheOs = true;

                            int forcedFlags = EwxReboot | EwxForce;
                            _trace?.Trace(host,
                                $"reboot channel: DCOM refused the GRACEFUL reboot — a user session is logged on (1191); the channel is healthy, escalating to the FORCED form on the same session to complete the operator's ordered reboot: forced=True flags={forcedFlags}");

                            // An operator Stop between reading the refusal and sending the escalation must
                            // PREVENT the escalated send — nothing goes down after Stop.
                            cancellationToken.ThrowIfCancellationRequested();

                            // From here on the wire carries the FORCED form, so the trace fields say so —
                            // including from the catch below, if the escalated send itself throws.
                            sentForced = true;
                            sentFlags = forcedFlags;

                            uint? escalatedCode = SendShutdown(session, os, sentFlags, cimOptions);
                            _trace?.Trace(host, $"reboot channel: DCOM shutdown sent forced={sentForced} flags={sentFlags} → returned {Describe(escalatedCode)}");

                            switch (ShutdownReturnCode.Classify(escalatedCode))
                            {
                                case ShutdownCallOutcome.Accepted:
                                    _trace?.Trace(host, $"reboot channel: DCOM accepted the forced escalation forced={sentForced} flags={sentFlags} — the box is going down FORCED");
                                    return (RebootDispatch.EscalatedToForced, string.Empty, false);

                                case ShutdownCallOutcome.AlreadyInProgress:
                                    _trace?.Trace(host, $"reboot channel: DCOM reports a shutdown already in progress (1115) forced={sentForced} flags={sentFlags}");
                                    return (RebootDispatch.AlreadyInProgress, string.Empty, false);

                                default:
                                    // Name BOTH codes: the refusal and what the escalation answered, so the
                                    // fallback line says exactly why DCOM couldn't resolve this box. The OS
                                    // REFUSED the graceful form here, so the fallback must not send it again:
                                    // ForceRequired.
                                    return (null,
                                        $"the graceful reboot was refused ({Describe(code)}) and the forced escalation returned {Describe(escalatedCode)}",
                                        true);
                            }
                        }

                        // 1191 on a call that was ALREADY forced — there is no stronger form to send, so it is
                        // NOT retried (that is the loop guard; see the method doc). Fall back, forced: the OS
                        // refused the graceful form, and this call didn't even ask for it.
                        case ShutdownCallOutcome.GracefulRefused:
                            return (null, $"the forced reboot was refused ({Describe(code)})", true);

                        // No result code at all. NEVER success: an accepted call always answers with an
                        // explicit 0, so a missing code cannot confirm the reboot — the same guard the sibling
                        // DCOM call site enforces (WinRmEnabler.InterpretCreateReturn, WinRmEnabler.cs:77-81).
                        case ShutdownCallOutcome.NoResultCode:
                            return (null, "the shutdown method returned no result code — the reboot can't be confirmed", false);

                        // Any other non-zero code — the call didn't take; let the caller fall back.
                        default:
                            return (null, $"the shutdown method returned {Describe(code)}", false);
                    }
                }
            }

            return (null, "Win32_OperatingSystem instance not found", false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A THROWN failure is a completely different path from a numeric refusal: the connect, the auth,
            // the query or the invoke itself failed, so DCOM never delivered anything. A shutdown already in
            // progress can surface this way too — treat that as going-offline, not a fall-back-worthy
            // failure. Everything else (Kerberos / access denied / timeout) falls back to SMB/SCM, exactly
            // as before, which is what keeps the Kerberos-broken boxes working.
            if (IsShutdownInProgress(ex))
            {
                _trace?.Trace(host, $"reboot channel: DCOM reports a shutdown already in progress (1115) forced={sentForced} flags={sentFlags}: {ex.Message}");
                return (RebootDispatch.AlreadyInProgress, string.Empty, false);
            }

            // A THROW is not ITSELF a refusal — but it does not erase one the OS already gave, so which of
            // the two throws this is decides ForceRequired:
            //   • NO prior 1191 (the connect, auth, query or FIRST send failed outright): nothing proves the
            //     graceful form would be rejected, so ForceRequired stays false and the fallback sends the
            //     caller's form, exactly as before — this is the Kerberos-broken-box path.
            //   • The ESCALATED send threw: the OS ALREADY answered 1191 on the send before it, so the
            //     refusal is proven. ForceRequired travels out with the failure and the fallback sends /f —
            //     without this, the fallback would send the very graceful form Windows just refused.
            return (null, $"{ex.GetType().Name}: {ex.Message}", gracefulRefusedByTheOs);
        }
    }

    /// <summary>ONE send of the DCOM shutdown method on an instance we already hold, returning the raw
    /// result code (<c>null</c> when the method answered with none). Factored so the graceful send and the
    /// forced escalation are literally the same call with different flags — same session, same instance,
    /// one primitive call site.</summary>
    private static uint? SendShutdown(CimSession session, CimInstance os, int flags, CimOperationOptions cimOptions)
    {
        using var inParams = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("Flags", flags, CimType.SInt32, CimFlags.In),
        };

        using CimMethodResult result = session.InvokeMethod(@"root\cimv2", os, "Win32Shutdown", inParams, cimOptions);
        object? rv = result.ReturnValue?.Value;
        return rv is null ? null : Convert.ToUInt32(rv);
    }

    /// <summary>Renders a raw result code for a trace/reason string, keeping "no result code at all"
    /// visibly distinct from any number (it must never read like a success).</summary>
    private static string Describe(uint? code) => code?.ToString() ?? "(no result code)";

    /// <summary>
    /// Fallback reboot via the SMB/SCM channel: create a one-shot LocalSystem demand-start service whose
    /// image runs <c>shutdown.exe</c>, start it (which fires the reboot), then best-effort delete it. Same
    /// mechanism <see cref="RemoteServiceController"/> uses for the agent, so it works on the boxes that
    /// reject DCOM/Kerberos. Graceful = no <c>/f</c> (the OS runs its normal service-stop sequence);
    /// forced = <c>/f</c>. A short <c>/t 5</c> delay lets the SCM start transaction complete before the box drops.
    /// <para><paramref name="forced"/> is <see cref="FallbackForced"/>'s answer, not the caller's raw request:
    /// a graceful call whose DCOM attempt was REFUSED with 1191 arrives here forced, because sending the same
    /// graceful form down a second channel would only be refused again.</para>
    /// </summary>
    private static void RebootViaSmbScm(string host, bool forced)
    {
        string runId = Guid.NewGuid().ToString("N");
        string serviceName = "Vivre_Reboot_" + runId; // unique per call → concurrent waves never collide
        string switches = forced ? "/r /f /t 5" : "/r /t 5";
        string binPath = "cmd /c shutdown " + switches + " /c \"Vivre Reboot Wave\"";

        // Create failure (couldn't open the SCM / create the service) means the reboot was genuinely NOT
        // issued — let it propagate so the wave flags the box. Once the service is created, treat the reboot
        // as issued: the wave's offline check is the authoritative success signal (and it escalates to
        // forced, then flags, if the box does NOT drop), so a Start()/Delete() error must never turn a box
        // that is actually rebooting into a red failure.
        RemoteServiceController service = RemoteServiceController.Create(host, serviceName, "Vivre Reboot " + runId, binPath);
        try
        {
            service.Start(); // launches cmd → shutdown.exe; the box begins rebooting
        }
        catch (Exception startEx)
        {
            // A non-service-aware image (cmd→shutdown) exits before reporting RUNNING, and a box that
            // reboots on the /t 5 timer drops the open SCM RPC connection — both surface here (1053, or
            // RPC-unavailable 1722/1726/1727) even though the reboot WAS issued. Swallow: don't fail a
            // box that's going down. If it genuinely didn't reboot, the wave's offline wait catches it.
            System.Diagnostics.Debug.WriteLine($"Reboot-service start on {host} (reboot likely issued): {startEx.Message}");
        }
        finally
        {
            // Best-effort delete: the box is going down, so this SCM delete may race the reboot and throw.
            // A leftover demand-start one-shot service never runs again on its own — harmless. (A blocked
            // graceful + its 8-min-later forced attempt can each leave one, since each uses a unique name;
            // up to two harmless orphans — the list-load reaper (OrphanRebootServiceReaper) removes them
            // next time the host is loaded.)
            try { service.Delete(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Reboot-service cleanup on {host}: {ex.Message}"); }
            service.Dispose();
        }
    }
}

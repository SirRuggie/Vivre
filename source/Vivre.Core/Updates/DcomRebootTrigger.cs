using System.ComponentModel;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Vivre.Core.Remoting;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IRebootTrigger"/>
/// <remarks>
/// Reboots over DCOM via <c>Win32_OperatingSystem.Win32Shutdown</c> on the ambient Windows login — the
/// same channel vitals use. Flags: 2 = reboot (graceful — services get their normal stop sequence so SQL
/// flushes), 6 = reboot + force (2 | 4) for the escalation when a graceful reboot won't take.
///
/// <para><b>SMB/SCM fallback:</b> on the Kerberos-broken Vision boxes the DCOM Win32Shutdown call is
/// rejected (it returns 1191, or throws an access/Kerberos error) for the same reason WinRM is — the
/// http SPN belongs to the SSRS service account, not the box. When DCOM doesn't take the reboot, we fall
/// back to the <em>proven</em> SMB/SCM channel that already delivers the update agent: create a one-shot
/// LocalSystem service whose image runs <c>shutdown.exe</c> (NTLM SSO over <c>\\host\IPC$\svcctl</c>, no
/// Kerberos). The graceful→force escalation is unchanged — only the reboot primitive falls back, and the
/// <paramref name="forced"/> flag is honored in the fallback too (graceful = no <c>/f</c>, force = <c>/f</c>).
/// Healthy boxes that accept DCOM never touch the fallback.</para>
/// </remarks>
public sealed class DcomRebootTrigger : IRebootTrigger
{
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(20);

    private const int EwxReboot = 2;
    private const int EwxForce = 4;

    public Task RebootAsync(string host, bool forced, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return Task.Run(() => RebootSync(host, forced, cancellationToken), cancellationToken);
    }

    private static void RebootSync(string host, bool forced, CancellationToken cancellationToken)
    {
        // 1) Preferred path: DCOM Win32Shutdown (works on healthy, domain-correct boxes).
        (bool ok, string dcomFailure) = TryDcomShutdown(host, forced, cancellationToken);
        if (ok)
        {
            return;
        }

        // 2) DCOM didn't take it (e.g. 1191 / access denied on a Kerberos-broken box). Fall back to the
        //    SMB/SCM channel — the same transport that delivers the agent, which authenticates over NTLM.
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RebootViaSmbScm(host, forced);
        }
        catch (Exception smbEx)
        {
            // Both channels failed — surface both reasons so the wave flags the box (it never auto-forces
            // beyond the escalation it already drives).
            throw new InvalidOperationException(
                $"Couldn't reboot {host}. DCOM: {dcomFailure}. SMB/SCM fallback: {smbEx.Message}", smbEx);
        }
    }

    /// <summary>Issues the reboot over DCOM. Returns (true, "") when the OS accepted it; (false, reason)
    /// when it returned non-zero or the call failed (so the caller can fall back). Cancellation propagates.</summary>
    private static (bool Ok, string Failure) TryDcomShutdown(string host, bool forced, CancellationToken cancellationToken)
    {
        int flags = forced ? EwxReboot | EwxForce : EwxReboot;
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
                    var inParams = new CimMethodParametersCollection
                    {
                        CimMethodParameter.Create("Flags", flags, CimType.SInt32, CimFlags.In),
                    };

                    using CimMethodResult result = session.InvokeMethod(@"root\cimv2", os, "Win32Shutdown", inParams, cimOptions);
                    object? rv = result.ReturnValue?.Value;
                    uint code = rv is null ? 0 : Convert.ToUInt32(rv);
                    return code == 0
                        ? (true, string.Empty)
                        : (false, $"Win32Shutdown returned {code}");
                }
            }

            return (false, "Win32_OperatingSystem instance not found");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Kerberos / access rejection, unreachable, timeout — let the SMB/SCM fallback try.
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fallback reboot via the SMB/SCM channel: create a one-shot LocalSystem demand-start service whose
    /// image runs <c>shutdown.exe</c>, start it (which fires the reboot), then best-effort delete it. Same
    /// mechanism <see cref="RemoteServiceController"/> uses for the agent, so it works on the boxes that
    /// reject DCOM/Kerberos. Graceful = no <c>/f</c> (the OS runs its normal service-stop sequence);
    /// forced = <c>/f</c>. A short <c>/t 5</c> delay lets the SCM start transaction complete before the box drops.
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
            // up to two harmless orphans, reaped manually or ignored.)
            try { service.Delete(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Reboot-service cleanup on {host}: {ex.Message}"); }
            service.Dispose();
        }
    }
}

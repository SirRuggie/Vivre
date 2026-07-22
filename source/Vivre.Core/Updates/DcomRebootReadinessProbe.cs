using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IRebootReadinessProbe"/>
/// <remarks>
/// Checks reboot-readiness over a DCOM <see cref="CimSession"/> on the ambient Windows login — no
/// credential prompt, works on the Kerberos-broken Vision boxes. Three signals must all hold before
/// a box is safe to reboot into a staged LCU:
/// <list type="number">
///   <item><description><b>TrustedInstaller stopped</b> — online servicing is complete (or absent).
///   If TI is still running the update is mid-install; rebooting now forces a 2-hour Stopping
///   hang or corrupts the component store.</description></item>
///   <item><description><b>TiWorker.exe not running</b> — the CBS worker thread has finished.
///   TI can report Stopped while TiWorker is still flushing its last writes; waiting for both
///   avoids that race.</description></item>
///   <item><description><b>CBS RebootPending key present</b> — something is actually staged.
///   Without this signal a "clean" box could pass the first two checks and be rebooted for
///   nothing, resetting its uptime and alarming on-call.</description></item>
/// </list>
/// The verdict's <see cref="RebootReadinessKind"/> tells the wave HOW to treat a not-ready read: servicing
/// still busy (TI running / TiWorker alive) → <see cref="RebootReadinessKind.ServicingActive"/> (wait); the
/// CBS RebootPending key definitively absent (StdRegProv ReturnValue 2) → <see cref="RebootReadinessKind.NothingStaged"/>
/// (nothing to commit); and any unreadable / denied / offline read → <see cref="RebootReadinessKind.CantConfirm"/>.
/// The wave now genuinely RETRIES a ServicingActive/CantConfirm verdict (a bounded pre-reboot settle poll)
/// rather than committing a box it cannot read — and never treats an unreadable read as evidence of anything.
/// </remarks>
public sealed class DcomRebootReadinessProbe : IRebootReadinessProbe
{
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(8);

    /// <summary>HKEY_LOCAL_MACHINE hive constant for StdRegProv calls.</summary>
    private const uint HklmHive = 0x80000002;

    /// <summary>CBS key whose mere existence signals that an update is staged and waiting for a reboot
    /// to commit. We only need to know it's there — EnumKey ReturnValue 0 = present.</summary>
    private const string RebootPendingKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";

    public Task<RebootReadiness> CheckAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // CIM calls are synchronous — run off the caller's thread so the wave stays responsive.
        return Task.Run(() => CheckSync(host, cancellationToken), cancellationToken);
    }

    private static RebootReadiness CheckSync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var options = new DComSessionOptions { Timeout = CimTimeout };
            using CimSession session = CimSession.Create(host, options);
            using var cimOptions = new CimOperationOptions
            {
                Timeout = CimTimeout,
                CancellationToken = cancellationToken,
            };

            // Signal 1: TrustedInstaller service must be Stopped.
            string? tiState = QueryFirstString(
                session, cimOptions,
                "SELECT State FROM Win32_Service WHERE Name='TrustedInstaller'",
                "State");

            // A null here means the query SUCCEEDED but returned no rows / no State property — that is NOT
            // evidence of servicing, it's an unreadable answer. Split it out as CantConfirm (today it lumped
            // into "still unknown") so the wave waits and re-checks rather than misreading it as busy.
            if (tiState is null)
            {
                return new RebootReadiness(false, "TrustedInstaller state unreadable", RebootReadinessKind.CantConfirm);
            }

            if (!string.Equals(tiState, "Stopped", StringComparison.OrdinalIgnoreCase))
            {
                // A real, read state that isn't Stopped: online servicing is provably in progress — worth waiting for.
                return new RebootReadiness(false,
                    $"TrustedInstaller is still {tiState} (online servicing in progress)", RebootReadinessKind.ServicingActive);
            }

            // Signal 2: TiWorker.exe must not be running — it can outlive TI reporting Stopped.
            bool tiWorkerRunning = QueryHasAny(
                session, cimOptions,
                "SELECT ProcessId FROM Win32_Process WHERE Name='TiWorker.exe'");

            if (tiWorkerRunning)
            {
                return new RebootReadiness(false, "TiWorker.exe is still running", RebootReadinessKind.ServicingActive);
            }

            // Signal 3: CBS RebootPending key must exist — something is actually staged. Read the raw
            // StdRegProv EnumKey ReturnValue and classify it three-way (0 = present, 2 = absent, anything
            // else = unreadable) so an ACCESS-DENIED (rv=5) never masquerades as "nothing staged".
            uint? rebootPendingRv = EnumKeyReturnValue(session, cimOptions, RebootPendingKey);
            RebootReadinessKind? cbsVerdict = ClassifyRebootPendingRv(rebootPendingRv);
            if (cbsVerdict == RebootReadinessKind.NothingStaged)
            {
                return new RebootReadiness(false, "no pending reboot — nothing is staged", RebootReadinessKind.NothingStaged);
            }

            if (cbsVerdict == RebootReadinessKind.CantConfirm)
            {
                return new RebootReadiness(false,
                    $"couldn't read the CBS RebootPending key (StdRegProv ReturnValue {RvText(rebootPendingRv)})",
                    RebootReadinessKind.CantConfirm);
            }

            // cbsVerdict is null → rv == 0 → the key is present and all three signals hold — safe to commit.
            return new RebootReadiness(true,
                "TrustedInstaller stopped, TiWorker idle, reboot pending — ready to commit.", RebootReadinessKind.Ready);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline / still booting / DCOM not up / denied — not a verdict; the wave retries (CantConfirm).
            return new RebootReadiness(false, $"couldn't reach {host} to check reboot-readiness", RebootReadinessKind.CantConfirm);
        }
    }

    /// <summary>Returns the string value of <paramref name="property"/> from the first instance
    /// returned by <paramref name="wql"/>, or <see langword="null"/> when the query yields no
    /// rows or the property is absent.</summary>
    private static string? QueryFirstString(
        CimSession session, CimOperationOptions cimOptions, string wql, string property)
    {
        foreach (CimInstance instance in session.QueryInstances(@"root\cimv2", "WQL", wql, cimOptions))
        {
            using (instance)
            {
                return instance.CimInstanceProperties[property]?.Value?.ToString();
            }
        }

        return null;
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="wql"/> yields at least one
    /// instance — used to detect a running process without reading any property values.</summary>
    private static bool QueryHasAny(CimSession session, CimOperationOptions cimOptions, string wql)
    {
        foreach (CimInstance instance in session.QueryInstances(@"root\cimv2", "WQL", wql, cimOptions))
        {
            using (instance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Invokes <c>StdRegProv.EnumKey</c> on <paramref name="subKey"/> under HKLM and returns its raw
    /// <c>ReturnValue</c> (a Win32 code): 0 = key present, 2 = key absent (ERROR_FILE_NOT_FOUND), 5 = access
    /// denied, etc. Returns <see langword="null"/> when the ReturnValue itself is missing/unconvertible. This
    /// method only READS — the three-way meaning is applied by <see cref="ClassifyRebootPendingRv"/>.</summary>
    private static uint? EnumKeyReturnValue(CimSession session, CimOperationOptions cimOptions, string subKey)
    {
        using var inParams = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("hDefKey", HklmHive, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("sSubKeyName", subKey, CimType.String, CimFlags.In),
        };

        using CimMethodResult result = session.InvokeMethod(
            @"root\cimv2", "StdRegProv", "EnumKey", inParams, cimOptions);

        object? rv = result.ReturnValue?.Value;
        if (rv is null)
        {
            return null;
        }

        try
        {
            return Convert.ToUInt32(rv);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            // An unconvertible ReturnValue is "unknown" — never mistaken for 0 (present) or 2 (absent).
            return null;
        }
    }

    /// <summary>
    /// Classifies a <c>StdRegProv.EnumKey</c> ReturnValue for the CBS RebootPending key into the not-ready
    /// KIND it implies — or <see langword="null"/> when the key is PRESENT (rv == 0). A non-null result is a
    /// not-ready verdict; null means "present, keep checking".
    /// <list type="bullet">
    ///   <item><description><c>0</c> → present → <see langword="null"/>.</description></item>
    ///   <item><description><c>2</c> (ERROR_FILE_NOT_FOUND) → <see cref="RebootReadinessKind.NothingStaged"/> —
    ///   a settled "absent" answer.</description></item>
    ///   <item><description>anything else, incl. <c>5</c> (access denied) and <see langword="null"/> →
    ///   <see cref="RebootReadinessKind.CantConfirm"/> — NOT a verdict, so it never masquerades as "nothing staged".</description></item>
    /// </list>
    /// Pure (no DCOM), so the mapping is unit-testable in isolation. Mirrors the DcomSoftwareReader RV
    /// discipline: only 0 and 2 are benign; every other code is unknown.
    /// </summary>
    internal static RebootReadinessKind? ClassifyRebootPendingRv(uint? rv) => rv switch
    {
        0 => null,
        2 => RebootReadinessKind.NothingStaged,
        _ => RebootReadinessKind.CantConfirm,
    };

    /// <summary>Renders a nullable EnumKey ReturnValue for the CantConfirm message (null → "unknown").</summary>
    private static string RvText(uint? rv) => rv?.ToString() ?? "unknown";
}

using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Updates;

/// <summary>
/// Post-reboot confirmation for non-2016 boxes. Confirms a reboot ONLY when the box returns with a
/// <b>newer <c>LastBootUpTime</c></b> than it had before the reboot — querying <c>Win32_OperatingSystem</c>
/// over DCOM/CIM (8-second timeout, ambient login — works on Kerberos-broken boxes).
///
/// <para><b>Why the boot time, not just "the OS answered":</b> a reboot-pending box briefly drops off the
/// network during reboot-prep (services stopping) and then answers again on the SAME (old) boot before it
/// actually goes down. The previous "OS is queryable ⇒ Confirmed" rule mistook that flicker for a completed
/// reboot and declared success in ~0 min — then the post-reboot rescan ran on a box that hadn't rebooted.
/// Gating on a newer boot time fixes that: a flicker (same boot) stays <see cref="RebootConfirmationOutcome.NotReady"/>
/// (keep watching) until the boot time advances or the wave's offline ceiling / hard cap is reached.</para>
///
/// <para>This strategy NEVER returns <see cref="RebootConfirmationOutcome.Failed"/>: whether updates "took"
/// on a non-2016 box is decided later by the WUA rescan, not here.</para>
/// </summary>
public sealed class ReadyConfirmation : IPostRebootConfirmation
{
    /// <summary>Reads the box's <c>LastBootUpTime</c> (or null when it can't be read). Injected so tests
    /// can simulate boot times without a live box.</summary>
    private readonly Func<string, CancellationToken, Task<DateTime?>> _queryBootTime;

    /// <summary>The boot time captured BEFORE the reboot (<see cref="CaptureBaselineAsync"/>). Null when it
    /// couldn't be read — in which case we can't prove a reboot, so confirmation keeps waiting (relying on
    /// the wave's hard-cap / the standalone Verify), never a false success.</summary>
    private DateTime? _baselineBootTime;

    /// <summary>Production constructor — uses a real DCOM CIM query for LastBootUpTime.</summary>
    public ReadyConfirmation() : this(DcomBootTimeQueryAsync) { }

    /// <summary>Test constructor — supply a delegate that returns the simulated LastBootUpTime (or null).</summary>
    internal ReadyConfirmation(Func<string, CancellationToken, Task<DateTime?>> bootTimeQuery)
    {
        _queryBootTime = bootTimeQuery ?? throw new ArgumentNullException(nameof(bootTimeQuery));
    }

    /// <inheritdoc/>
    public async Task CaptureBaselineAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            _baselineBootTime = await _queryBootTime(host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Couldn't read the pre-reboot boot time — leave the baseline null. Confirmation will then keep
            // waiting rather than ever confirming without proof of a newer boot (no false success).
            _baselineBootTime = null;
        }
    }

    /// <inheritdoc/>
    public async Task<RebootConfirmationResult> ConfirmAsync(string host, CancellationToken cancellationToken)
    {
        DateTime? bootTime;
        try
        {
            bootTime = await _queryBootTime(host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // DCOM/CIM blew up mid-query — treat as not yet up; the wave loop retries.
            return NotReady("Back online — waiting for it to finish coming up…");
        }

        // Can't read the boot time yet → still coming up.
        if (bootTime is null)
        {
            return NotReady("Back online — waiting for it to finish coming up…");
        }

        // No pre-reboot baseline (its capture failed) → we can't prove a reboot happened, so we must NOT
        // confirm on "the OS answered" alone (that was the flicker bug). Keep watching; the wave's hard cap
        // / the standalone Verify is the net. (See the boot-time note in the class summary.)
        if (_baselineBootTime is null)
        {
            return NotReady("Back online — confirming the reboot…");
        }

        // The reboot is REAL only when the box reports a NEWER boot time than before it. A box that merely
        // flickered offline and came back on the SAME boot has NOT rebooted → keep watching.
        return bootTime > _baselineBootTime
            ? new RebootConfirmationResult(RebootConfirmationOutcome.Confirmed, "Back online — rebooted.")
            : NotReady("Back online, but it hasn't rebooted yet (no new boot time) — still waiting for the reboot to take…");
    }

    private static RebootConfirmationResult NotReady(string message) =>
        new(RebootConfirmationOutcome.NotReady, message);

    private static Task<DateTime?> DcomBootTimeQueryAsync(string host, CancellationToken cancellationToken) =>
        Task.Run(() => TryReadLastBootTime(host, cancellationToken), cancellationToken);

    /// <summary>Reads <c>Win32_OperatingSystem.LastBootUpTime</c> over DCOM/CIM; returns null when the box
    /// can't be reached / queried (still booting, DCOM not up, etc.) — a retry signal, never a failure.</summary>
    private static DateTime? TryReadLastBootTime(string host, CancellationToken cancellationToken)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(8);
            using var options = new DComSessionOptions { Timeout = timeout };
            using CimSession session = CimSession.Create(host, options);
            using var cimOptions = new CimOperationOptions
            {
                Timeout = timeout,
                CancellationToken = cancellationToken,
            };

            foreach (CimInstance instance in session.QueryInstances(
                @"root\cimv2", "WQL",
                "SELECT LastBootUpTime FROM Win32_OperatingSystem",
                cimOptions))
            {
                using (instance)
                {
                    // MI maps the CIM datetime to a System.DateTime; null/other → no boot time read.
                    return instance.CimInstanceProperties["LastBootUpTime"]?.Value as DateTime?;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline, still booting, DCOM not up — not a failure; caller retries.
            return null;
        }
    }
}

using System.Management.Automation;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Updates;

/// <summary>Which channel completed (or attempted) a force reboot.</summary>
public enum ForceRebootChannel
{
    /// <summary>The normal path: <c>shutdown.exe</c> sent over WinRM.</summary>
    WinRm,

    /// <summary>The Kerberos fallback: the same <see cref="IRebootTrigger"/> (DCOM → SMB/SCM) the
    /// Reboot Wave uses on Kerberos-broken boxes.</summary>
    Dcom,
}

/// <summary>Outcome of one force-reboot attempt. <see cref="Error"/> non-null means the WinRM channel
/// itself WORKED but <c>shutdown.exe</c> reported an error (e.g. "a shutdown is already in progress",
/// access denied) — the reboot was NOT issued and no fallback fired (falling back on a command the box
/// already ran and refused is the double-act risk). <see cref="Dispatch"/> is null in that case.</summary>
public sealed record ForceRebootResult(ForceRebootChannel Channel, RebootDispatch? Dispatch, string? Error);

/// <summary>
/// Runs the operator's confirmed "Force reboot" for one host: <c>shutdown.exe /r /f /t 5</c> over WinRM,
/// with ONE narrow fallback — on a WinRM Kerberos AUTH rejection (<see cref="KerberosWrongPrincipalException"/>,
/// fresh 0x80090322/0x80090303 or the routing host's cached fast-fail), the same confirmed reboot is
/// completed over the injected <see cref="IRebootTrigger"/> (the DCOM → SMB/SCM trigger the Reboot Wave
/// already uses on those boxes; its forced SMB fallback runs the identical <c>shutdown /r /f /t 5</c>).
///
/// <para><b>Why that class is the ONLY fallback trigger:</b> authentication precedes execution — a
/// rejected auth means no shell was ever established, so the shutdown command provably NEVER reached the
/// box and a second channel cannot double-reboot it. Deliberately NOT caught:
/// <see cref="RemoteSessionLostException"/> (the session can drop mid-command — the reboot may already
/// have fired), timeouts/cancellation, shell-init/busy failures, and unknown errors — all propagate to
/// the caller unchanged, no fallback, no double-reboot risk. A result with <c>HadErrors</c> means the
/// channel worked and the box itself refused — also surfaced, never fallen back on.</para>
///
/// <para><b>Cardinal:</b> this class contains no shutdown primitive and makes no reboot decision — it
/// only completes the single reboot the operator clicked + confirmed, choosing the channel. The repo's
/// only shutdown-primitive call site remains <see cref="DcomRebootTrigger"/> (the gate grep keys on the
/// primitive's name — deliberately not repeated here so that grep stays exactly one file).</para>
/// </summary>
public sealed class ForceRebootRunner
{
    /// <summary>The exact command Force reboot has always sent (moved verbatim from the view model);
    /// the <c>/t 5</c> delay lets the WinRM call return cleanly before the box goes down.</summary>
    internal const string Script = "shutdown.exe /r /f /t 5 /c \"Vivre forced reboot\"";

    private readonly IPowerShellHost _powerShell;
    private readonly IRebootTrigger _dcomFallback;

    public ForceRebootRunner(IPowerShellHost powerShell, IRebootTrigger dcomFallback)
    {
        _powerShell = powerShell ?? throw new ArgumentNullException(nameof(powerShell));
        _dcomFallback = dcomFallback ?? throw new ArgumentNullException(nameof(dcomFallback));
    }

    /// <summary>Sends the forced reboot to <paramref name="host"/>. See the class doc for the exact
    /// fallback classification. Exceptions other than the Kerberos auth rejection propagate.</summary>
    public async Task<ForceRebootResult> RebootAsync(string host, PSCredential? credential, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        PSExecutionResult result;
        try
        {
            result = HostName.IsLocal(host)
                ? await _powerShell.RunLocalAsync(Script, cancellationToken).ConfigureAwait(false)
                : await _powerShell.RunRemoteAsync(host, Script, credential, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (KerberosWrongPrincipalException)
        {
            // WinRM REJECTED authentication — the command provably never ran (see class doc). Complete
            // the operator's already-confirmed reboot over the proven DCOM → SMB/SCM trigger, forced,
            // exactly once. AlreadyInProgress from the trigger means the box is going down on its own
            // (1115) — reported, never re-fired. A both-channels failure throws with both reasons.
            RebootDispatch dispatch = await _dcomFallback.RebootAsync(host, forced: true, cancellationToken).ConfigureAwait(false);
            return new ForceRebootResult(ForceRebootChannel.Dcom, dispatch, Error: null);
        }

        if (result.HadErrors)
        {
            // The WinRM channel worked; shutdown.exe itself reported the failure. Surface it — no fallback.
            return new ForceRebootResult(
                ForceRebootChannel.WinRm,
                Dispatch: null,
                result.Errors.Count > 0 ? result.Errors[0] : "shutdown reported an error");
        }

        return new ForceRebootResult(ForceRebootChannel.WinRm, RebootDispatch.Issued, Error: null);
    }
}

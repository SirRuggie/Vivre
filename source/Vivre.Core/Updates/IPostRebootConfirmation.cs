namespace Vivre.Core.Updates;

/// <summary>The verdict from <see cref="IPostRebootConfirmation.ConfirmAsync"/>.</summary>
public enum RebootConfirmationOutcome
{
    /// <summary>The goal was achieved — terminal success.</summary>
    Confirmed,
    /// <summary>The update did not take / rolled back — terminal failure (red).</summary>
    Failed,
    /// <summary>Can't tell yet; the box is still coming up — retry.</summary>
    NotReady,
}

/// <param name="Outcome">The verdict.</param>
/// <param name="Message">Operator-facing summary.</param>
public sealed record RebootConfirmationResult(RebootConfirmationOutcome Outcome, string Message);

/// <summary>Confirms whether a returned box's reboot achieved its goal.
/// <list type="bullet">
///   <item><see cref="RebootConfirmationOutcome.Confirmed"/> — success (terminal green).</item>
///   <item><see cref="RebootConfirmationOutcome.Failed"/> — it did not take / rolled back (terminal red).</item>
///   <item><see cref="RebootConfirmationOutcome.NotReady"/> — can't tell yet, still coming up (retry).</item>
/// </list>
/// The strategy varies per box (2016 = UBR check; others = OS-up rescan-as-verify), so it is
/// passed per call rather than baked into the wave.</summary>
public interface IPostRebootConfirmation
{
    /// <summary>
    /// Called by the wave <b>before</b> it issues the reboot, so a strategy that needs a pre-reboot
    /// baseline can capture it. <see cref="ReadyConfirmation"/> records the box's <c>LastBootUpTime</c>
    /// here so <see cref="ConfirmAsync"/> can tell a REAL reboot (newer boot time) from a brief
    /// reachability flicker during reboot-prep (same boot time). Default no-op — strategies that don't
    /// need a baseline (e.g. the 2016 <c>UbrConfirmation</c>, which implicitly requires the real reboot
    /// because the UBR only advances after the CU commits) inherit this.
    /// </summary>
    Task CaptureBaselineAsync(string host, CancellationToken cancellationToken) => Task.CompletedTask;

    Task<RebootConfirmationResult> ConfirmAsync(string host, CancellationToken cancellationToken);
}

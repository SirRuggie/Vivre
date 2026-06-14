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
    Task<RebootConfirmationResult> ConfirmAsync(string host, CancellationToken cancellationToken);
}

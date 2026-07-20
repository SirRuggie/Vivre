namespace Vivre.Core.Updates;

/// <summary>
/// A single reading of a target's own clock and boot time, taken in ONE query so both values come from the
/// SAME clock. Their difference (<see cref="Uptime"/>) is therefore immune to a clock step: if the target's
/// clock jumps forward or back (an NTP correction, a manual set), BOTH <see cref="LocalNow"/> and
/// <see cref="LastBootUpTime"/> move by the same amount, so the uptime is unchanged.
/// </summary>
/// <param name="LocalNow">The target's local time at the moment of the read (its own clock).</param>
/// <param name="LastBootUpTime">The target's last boot time, read from that same clock.</param>
public sealed record BootTimeReading(DateTime LocalNow, DateTime LastBootUpTime)
{
    /// <summary>How long the target has been up (<see cref="LocalNow"/> − <see cref="LastBootUpTime"/>).
    /// Clock-step-immune because both values are read from the same clock in one query — that is the whole
    /// point of reading them together.</summary>
    public TimeSpan Uptime => LocalNow - LastBootUpTime;
}

/// <summary>
/// Reads a target's uptime as a single clock-immune <see cref="BootTimeReading"/> (its own
/// <c>LocalDateTime</c> and <c>LastBootUpTime</c>, one query). Used by the reboot wave to PROVE a reboot
/// completed even when the box's drop off the network was never observed — a reset (much smaller) uptime is
/// the proof.
/// </summary>
/// <remarks>
/// The contract is a retry signal, never a verdict: <b>any</b> failure (offline, still booting, DCOM not up,
/// access denied, either value missing) returns <c>null</c> so the caller keeps waiting rather than treating
/// an unreadable box as proof of anything — an unreadable read must never become a false success. Only
/// cancellation throws (<see cref="OperationCanceledException"/>). A non-null reading is always two values
/// from the target's OWN clock in ONE query, so their difference is immune to clock steps.
/// </remarks>
public interface IBootTimeReader
{
    /// <summary>Reads the target's current <c>LocalDateTime</c> + <c>LastBootUpTime</c> as one
    /// <see cref="BootTimeReading"/>, or <c>null</c> on ANY failure (a retry signal, never a verdict). Only
    /// cancellation throws.</summary>
    Task<BootTimeReading?> ReadAsync(string host, CancellationToken cancellationToken);
}

namespace Vivre.Core.Updates;

/// <summary>
/// Bounds the RATE of reboot issuance across a fleet wave. Acquired only immediately around the
/// reboot trigger and released as soon as the reboot is issued — never held through the offline
/// watch (so it never serializes per-box verify).
/// </summary>
public interface IRebootGate
{
    /// <summary>Acquires the gate. The returned <see cref="IDisposable"/> releases it when disposed.</summary>
    Task<IDisposable> EnterAsync(CancellationToken cancellationToken);
}

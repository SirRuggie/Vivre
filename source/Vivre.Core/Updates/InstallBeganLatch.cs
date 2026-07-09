namespace Vivre.Core.Updates;

/// <summary>Decorates the UI-marshalling progress sink with a synchronous "install has begun" latch.
/// Report runs INLINE on the producing (streaming) thread — unlike Progress<T>, which posts its
/// callback to the captured UI context — so the latch is set BEFORE InstallAsync's task completes
/// and the retry re-entry guard can never read a stale false, on any thread. (The guard read raced
/// only the UI-posted write; latching producer-side removes the post from the flag's path.)</summary>
public sealed class InstallBeganLatch : IProgress<HostPatchStatus>
{
    private readonly IProgress<HostPatchStatus> _inner;
    private int _began;

    public InstallBeganLatch(IProgress<HostPatchStatus> inner) => _inner = inner;

    /// <summary>True once any report showed install actually started (Installing/PendingReboot/Done
    /// phase, or a non-zero installed count). Monotonic — never resets. Volatile so a reader on
    /// another thread sees the latch.</summary>
    public bool Began => Volatile.Read(ref _began) == 1;

    public void Report(HostPatchStatus value)
    {
        if (value.Phase is PatchPhase.Installing or PatchPhase.PendingReboot or PatchPhase.Done
            || value.InstalledCount > 0)
        {
            Volatile.Write(ref _began, 1);
        }
        _inner.Report(value);
    }
}

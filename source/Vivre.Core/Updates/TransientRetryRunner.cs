namespace Vivre.Core.Updates;

/// <summary>
/// Drives the <b>silent transient-retry loop</b> for a WUA operation (scan / install): run the
/// attempt; if it failed with a <b>transient</b> reach HRESULT and retries remain, pause and
/// re-dispatch the WHOLE operation again; on success or a <b>terminal</b> failure, return
/// immediately (no retry); when the transient retries are exhausted, return the honest
/// "couldn't reach Windows Update" status — never a false "up to date".
///
/// <para>Wraps the ENTIRE operation (service-registration → search → download → install), because the
/// proven failure (<c>0x80072EE2</c>) is the SLS service-locator call at service-registration, BEFORE
/// search/download — a download-only retry would miss it. Classification keys on the HRESULT, not the
/// phase (see <see cref="TransientWuaError"/>).</para>
///
/// <para>Pure + host-free: the attempt, the inter-attempt delay, the "retrying" notification, and the
/// exhausted-status builder are all injected, so the policy is unit-testable without real boxes or
/// real waits.</para>
/// </summary>
public static class TransientRetryRunner
{
    /// <param name="attempt">Runs one full attempt and returns its terminal status. Re-invoked per try.</param>
    /// <param name="maxRetries">EXTRA attempts after the first (3 ⇒ up to 4 total tries).</param>
    /// <param name="delay">Awaited between a transient failure and the next attempt; receives the
    /// upcoming retry number (1-based). Injected so tests don't really wait; a real caller passes
    /// <c>Task.Delay(backoff, ct)</c>.</param>
    /// <param name="onRetrying">Invoked (with the upcoming 1-based retry number) just before each retry
    /// so the caller can show a calm "Retrying…" row state — never an error. NOTE: this (like
    /// <paramref name="attempt"/> and <paramref name="buildExhausted"/>) is invoked on THIS runner's
    /// context — after an internal <c>ConfigureAwait(false)</c>, i.e. a thread-pool thread, NOT the
    /// caller's captured SynchronizationContext. A UI caller MUST marshal any UI-bound write it does here.</param>
    /// <param name="buildExhausted">Builds the honest terminal status from the last transient message
    /// once retries run out (the VM passes <see cref="HostPatchStatus.Unreachable"/>).</param>
    /// <param name="token">Cancellation (user Stop) — propagates out of the attempt or the delay.</param>
    public static async Task<HostPatchStatus> RunAsync(
        Func<CancellationToken, Task<HostPatchStatus>> attempt,
        int maxRetries,
        Func<int, CancellationToken, Task> delay,
        Action<int>? onRetrying,
        Func<string, HostPatchStatus> buildExhausted,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(buildExhausted);

        for (int attemptIndex = 0; ; attemptIndex++)
        {
            HostPatchStatus result = await attempt(token).ConfigureAwait(false);

            // Success or a genuine (terminal) failure → surface immediately, no retry.
            bool transient = result.Phase == PatchPhase.Error && TransientWuaError.IsTransient(result.Message);
            if (!transient)
            {
                return result;
            }

            // Out of retries → honest "couldn't reach Windows Update" (NEVER up-to-date / 0-applicable).
            if (attemptIndex >= maxRetries)
            {
                return buildExhausted(result.Message);
            }

            // Transient and retries remain: announce the calm retry state, pause, then re-dispatch the
            // whole operation. A user Stop during the pause throws out of delay and ends the loop.
            int upcomingRetry = attemptIndex + 1;
            onRetrying?.Invoke(upcomingRetry);
            await delay(upcomingRetry, token).ConfigureAwait(false);
        }
    }
}

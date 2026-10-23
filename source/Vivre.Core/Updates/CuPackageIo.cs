using Vivre.Core.Threading;

namespace Vivre.Core.Updates;

/// <summary>
/// One large CU file operation per controller process, shared across tabs and lane instances.
/// Only preparation/copy/verification uses this queue; remote installation releases the slot.
/// </summary>
internal static class CuPackageIo
{
    private static readonly KeyedOperationGate Gate = new();

    internal static async Task<T> RunAsync<T>(Func<T> work, CancellationToken token)
    {
        using var lease = await Gate.AcquireAsync("CU package I/O", token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Even an immediately available slot must never run file I/O on the caller's dispatcher.
        return await Task.Run(work, token).ConfigureAwait(false);
    }
}

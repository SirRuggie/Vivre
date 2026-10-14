using System.Management.Automation;
using Vivre.Core.Operations;

namespace Vivre.Core.Remediation;

/// <summary>Adds app-wide exclusive ownership and persistent staging-path preservation to cleanup.</summary>
public sealed class CoordinatedRemediationService : IRemediationService
{
    private readonly IRemediationService _inner;
    private readonly HostOperationCoordinator _operations;
    private readonly ProtectedStagingPathRegistry _protectedPaths;

    public CoordinatedRemediationService(
        IRemediationService inner,
        HostOperationCoordinator operations,
        ProtectedStagingPathRegistry protectedPaths)
    {
        _inner = inner;
        _operations = operations;
        _protectedPaths = protectedPaths;
    }

    public Task<RemediationResult> StartServiceAsync(string host, string serviceDisplayName, PSCredential? credential = null, CancellationToken cancellationToken = default) =>
        _inner.StartServiceAsync(host, serviceDisplayName, credential, cancellationToken);

    public async Task<DiskCleanupResult> FreeDiskSpaceAsync(
        string host,
        PSCredential? credential = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? protectedPaths = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using HostOperationCoordinator.HostOperationClaim claim =
            _operations.TryBeginCleanup(host);
        if (!claim.Acquired)
        {
            return new DiskCleanupResult(DiskCleanupOutcome.Failed, null, null, claim.RefusalMessage!);
        }

        IReadOnlyCollection<string> persisted;
        try
        {
            persisted = _protectedPaths.GetProtectedPaths(host);
        }
        catch (Exception ex)
        {
            return new DiskCleanupResult(
                DiskCleanupOutcome.Failed,
                null,
                null,
                $"Disk cleanup blocked — protected staging paths could not be read: {ex.Message}");
        }

        string[] combined = persisted
            .Concat(protectedPaths ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Once started, cleanup owns recovery. Do not let caller cancellation return while target-side
        // service recovery may still be running; the inner implementation completes its finally path.
        try
        {
            DiskCleanupResult result = await _inner.FreeDiskSpaceAsync(host, credential, CancellationToken.None, combined).ConfigureAwait(false);
            if (!result.SafeToReleaseHost)
            {
                claim.MarkRemoteStateUncertain();
            }
            return result;
        }
        catch
        {
            claim.MarkRemoteStateUncertain();
            throw;
        }
    }

    public Task<IReadOnlyList<ProcessInfo>> GetTopProcessesAsync(string host, PSCredential? credential = null, CancellationToken cancellationToken = default) =>
        _inner.GetTopProcessesAsync(host, credential, cancellationToken);

    public Task<RemediationResult> EndProcessAsync(string host, int processId, PSCredential? credential = null, CancellationToken cancellationToken = default) =>
        _inner.EndProcessAsync(host, processId, credential, cancellationToken);
}

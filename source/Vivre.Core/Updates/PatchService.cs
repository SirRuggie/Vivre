using Vivre.Core.Credentials;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IPatchService"/>
/// <remarks>
/// Thin orchestrator over <see cref="WuaUpdateLane"/> (the WUA lane is the only lane
/// today; an SCCM-deployment lane is deferred — see REBUILD_PLAN.md A1). Mirrors
/// <c>ConfigMgrClient</c>'s construction (an <see cref="IPowerShellHost"/> in, logic
/// delegated). Takes a <see cref="ConnectionCredential"/> rather than a
/// <c>PSCredential</c> because install needs the raw credential for the DCOM
/// <c>Win32_Process.Create</c> fallback as well as the WinRM path.
/// </remarks>
public sealed class PatchService : IPatchService
{
    private readonly WuaUpdateLane _wua;

    public PatchService(IPowerShellHost powerShell) => _wua = new WuaUpdateLane(powerShell);

    public Task<HostPatchStatus> ScanAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        CancellationToken cancellationToken = default) =>
        _wua.ScanAsync(host, options, credential, cancellationToken);

    public Task<HostPatchStatus> InstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default) =>
        _wua.InstallAsync(host, options, credential, progress, cancellationToken);
}

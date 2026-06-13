namespace Vivre.Core.Updates;

/// <summary>
/// The month's Server 2016 cumulative-update target the operator confirms each cycle: which KB, which
/// architecture, and (for the post-reboot check) the build's expected UBR. Size/hash are optional extra
/// guards on the package file.
/// </summary>
/// <param name="Kb">The CU article, e.g. "KB5094122" (or bare "5094122").</param>
/// <param name="Arch">Architecture token expected in the .msu name, e.g. "x64".</param>
/// <param name="ExpectedSizeBytes">Approximate package size for a sanity check, or null.</param>
/// <param name="ToleranceBytes">Allowed size delta (default 5 MB) when <see cref="ExpectedSizeBytes"/> is set.</param>
/// <param name="ExpectedSha256">Authoritative hash when known (beats size); null to skip.</param>
/// <param name="TargetUbr">The UBR the box should report after the commit (e.g. 9234) — used by Verify.</param>
public sealed record LcuTarget(
    string Kb,
    string Arch = "x64",
    long? ExpectedSizeBytes = null,
    long ToleranceBytes = 5_242_880,
    string? ExpectedSha256 = null,
    int? TargetUbr = null);

/// <summary>
/// The Server 2016 (build 14393) full-package cumulative-update lane: it installs the complete CU .msu
/// via DISM (sidestepping the broken Express delta pipeline) instead of going through WUA. It rides the
/// proven SMB/DCOM transport (<see cref="ISmbAgentLane"/>) — copy the package to the box's hardened drop
/// dir, DISM-add it as SYSTEM, and stop at "Staged — reboot-ready"; the operator commits later via a
/// Reboot Wave.
///
/// <para>This type assumes its caller has already gated to 14393 (the routing in <c>PatchService</c>
/// does that). Operator actions are manual and confirmed: <see cref="StageAsync"/> here; Component
/// Cleanup, Verify, and the Reboot Wave follow.</para>
/// </summary>
/// <summary>The verdict of <see cref="FullPackageLcuLane.VerifyAsync"/>: did the staged CU actually
/// commit? <see cref="Unreachable"/> means "couldn't read the box yet" (still coming up) — a retry, NOT
/// a failure. Only <see cref="WrongBuild"/> is a real red (the update didn't take / rolled back).</summary>
public enum LcuVerifyOutcome { Verified, WrongBuild, Unreachable }

/// <param name="Outcome">The verdict.</param>
/// <param name="CurrentBuild">The host's OS build (e.g. 14393), or null when unreadable.</param>
/// <param name="Ubr">The host's UBR (e.g. 9234), or null when unreadable.</param>
/// <param name="Message">Operator-facing summary.</param>
public sealed record LcuVerifyResult(LcuVerifyOutcome Outcome, int? CurrentBuild, int? Ubr, string Message);

public sealed class FullPackageLcuLane
{
    private readonly ISmbAgentLane _smb;
    private readonly LcuPackageStore _packages;
    private readonly ILcuBuildReader _builds;

    public FullPackageLcuLane(ISmbAgentLane smb, LcuPackageStore? packages = null, ILcuBuildReader? buildReader = null)
    {
        _smb = smb ?? throw new ArgumentNullException(nameof(smb));
        _packages = packages ?? new LcuPackageStore();
        _builds = buildReader ?? new DcomLcuBuildReader();
    }

    /// <summary>
    /// Reclaims component-store space on the host (DISM /StartComponentCleanup as SYSTEM) to make room for
    /// the CU. The agent refuses if a reboot is already pending (CBS serialises TrustedInstaller), so this
    /// can't collide with a staged update. Run it before Stage on a tight box.
    /// </summary>
    public Task<HostPatchStatus> ComponentCleanupAsync(
        string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        return _smb.RunComponentCleanupAsync(host, options, progress, cancellationToken);
    }

    /// <summary>
    /// Reads the host's build/UBR and decides whether the CU committed — the "UBR decides, not the clock"
    /// engine behind both the Reboot-Wave tracker and the standalone Verify action. A box that can't be
    /// read yet is <see cref="LcuVerifyOutcome.Unreachable"/> (retry — it may still be coming up), so a
    /// slow-returning box is never written off; only a successful read of the wrong build is a red.
    /// </summary>
    public async Task<LcuVerifyResult> VerifyAsync(string host, int targetUbr, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        (int? build, int? ubr) = await _builds.ReadAsync(host, cancellationToken).ConfigureAwait(false);
        return Decide(host, build, ubr, targetUbr);
    }

    /// <summary>The verdict rule, shared by <see cref="VerifyAsync"/> and the Reboot Wave so they can't
    /// drift: a null UBR is "can't read yet" (retry, never a failure); UBR == target is green; any other
    /// readable UBR is a red (rolled back / didn't take). The clock never appears here — only the UBR.</summary>
    public static LcuVerifyResult Decide(string host, int? currentBuild, int? ubr, int targetUbr)
    {
        if (ubr is null)
        {
            return new LcuVerifyResult(LcuVerifyOutcome.Unreachable, currentBuild, ubr,
                $"Couldn't read the build on {host} yet — it may still be booting/committing. Re-verify when it's back up.");
        }

        if (ubr == targetUbr)
        {
            return new LcuVerifyResult(LcuVerifyOutcome.Verified, currentBuild, ubr,
                $"Verified — {host} is at {currentBuild}.{ubr}.");
        }

        return new LcuVerifyResult(LcuVerifyOutcome.WrongBuild, currentBuild, ubr,
            $"{host} is at {currentBuild}.{ubr}, expected .{targetUbr} — the update didn't take (rolled back?).");
    }

    /// <summary>
    /// Read-only precheck (touches NO box): is the right CU <c>.msu</c> present + correct in
    /// <paramref name="packageDirectory"/>? Returns the same resolution <see cref="StageAsync"/> uses, so the
    /// UI can guide the operator to drop the file BEFORE a Stage runs. Found = ready; anything else carries a
    /// plain reason + the catalog link.
    /// </summary>
    public LcuPackageResolution CheckPackage(string packageDirectory, LcuTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(target);
        return _packages.Resolve(
            packageDirectory, target.Kb, target.Arch,
            target.ExpectedSizeBytes, target.ToleranceBytes, target.ExpectedSha256);
    }

    /// <summary>
    /// Daytime step: verify the right CU package is present in <paramref name="packageDirectory"/>, then
    /// deliver + DISM-add it on <paramref name="host"/> while the server keeps serving. Does NOT reboot.
    /// Returns the terminal status: <see cref="PatchPhase.PendingReboot"/> = staged/reboot-ready,
    /// <see cref="PatchPhase.Done"/> = already current (no change), or a failure. If the package isn't
    /// present/correct, fails fast with the catalog link so the operator can drop the file — no box is
    /// touched until the package is right.
    /// </summary>
    public async Task<HostPatchStatus> StageAsync(
        string host,
        string packageDirectory,
        LcuTarget target,
        PatchOptions options,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        progress.Report(new HostPatchStatus(PatchPhase.Scanning, "Checking the update package…"));

        LcuPackageResolution package = _packages.Resolve(
            packageDirectory, target.Kb, target.Arch,
            target.ExpectedSizeBytes, target.ToleranceBytes, target.ExpectedSha256);

        if (package.Status != LcuPackageStatus.Found || package.Path is null)
        {
            // The package isn't ready — surface the reason + the catalog link and stop. The operator
            // drops the correct file, then re-runs Stage. (Auto-fetch is a later convenience layer.)
            var notReady = HostPatchStatus.Failed($"{package.Message} Download: {package.CatalogUrl}");
            progress.Report(notReady);
            return notReady;
        }

        // Deliver + DISM-add as SYSTEM over the SMB/DCOM lane. Terminal status flows straight back:
        // PendingReboot = staged/reboot-ready, Done = already current, else a failure with its reason.
        return await _smb
            .InstallFullPackageAsync(host, package.Path, options, progress, cancellationToken)
            .ConfigureAwait(false);
    }
}

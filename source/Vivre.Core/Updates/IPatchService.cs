using Vivre.Core.Credentials;

namespace Vivre.Core.Updates;

/// <summary>
/// Orchestrates the Windows Update Agent (WUA) lane — the BatchPatch replacement.
/// Scan reads the applicable-update list over WinRM; Install runs the work as a
/// one-time SYSTEM scheduled task on the target (WUA install won't run inside a
/// WinRM network-logon → <c>WU_E_NO_INTERACTIVE_USER</c>) and reports progress
/// by polling a JSON file the task writes.
/// </summary>
public interface IPatchService
{
    /// <summary>
    /// Searches <paramref name="host"/> for applicable updates from the configured
    /// source and returns the list + count (after the exclude filter). Read-only.
    /// </summary>
    Task<HostPatchStatus> ScanAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads + installs all applicable updates on <paramref name="host"/> via a
    /// one-time SYSTEM scheduled task, reporting each phase through
    /// <paramref name="progress"/>. Always cleans up the task + temp files (incl. on
    /// cancellation/error). Returns the terminal status.
    /// </summary>
    Task<HostPatchStatus> InstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uninstalls the selected installed updates on <paramref name="host"/> via the same
    /// one-time SYSTEM scheduled task pattern as <see cref="InstallAsync"/>. Honors
    /// <see cref="PatchOptions.IncludeKbArticleIds"/> as the per-machine selection, and only
    /// touches updates Windows marks as <c>IsUninstallable</c>. Reports progress through
    /// <paramref name="progress"/>; cleans up the task + temp files regardless of outcome.
    /// </summary>
    Task<HostPatchStatus> UninstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default);
}

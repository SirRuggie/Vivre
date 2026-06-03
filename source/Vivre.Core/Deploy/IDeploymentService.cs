using System.Management.Automation;

namespace Vivre.Core.Deploy;

/// <summary>The outcome of staging a package onto one machine.</summary>
/// <param name="Ok">True when the files were copied onto the target intact.</param>
/// <param name="Path">The file/folder the package was copied to on the target (for the row + log), or
/// null on failure.</param>
/// <param name="Message">A human-readable result/failure line for the row + activity log.</param>
public sealed record StageResult(bool Ok, string? Path, string Message);

/// <summary>
/// <b>Stages</b> an admin-supplied package (a single file, or a folder of files) onto a machine — it
/// copies the files to a destination and stops there. It does NOT run or install anything; the admin
/// runs the install themselves (e.g. right-click ▸ Run script… pointing at the staged path, or their
/// normal batch / Company Portal flow).
///
/// <para><b>Why copy-only.</b> An earlier version also ran the installer as SYSTEM and watched it to
/// completion. That fought too many variables we don't control: security agents (SentinelOne,
/// CrowdStrike) load filter/network drivers mid-install that tear down WinRM, so "did it succeed?"
/// over a connection the install kills is inherently unreliable. Delivering files and letting the
/// admin's own scripts install is robust.</para>
///
/// <para><b>Transport.</b> Prefers the <b>SMB admin share</b> (<c>\\host\C$\…</c>) — the same fast,
/// battle-tested channel SCCM, PsExec, and PDQ use to push files to Windows machines (LAN speed, no
/// encoding overhead, a single copy). If SMB/admin$ is blocked on a box, it automatically falls back to
/// <b>WinRM</b>: zip the payload, ship it in chunks (a multi-MB installer base64'd into one remote
/// command is too big — WinRM rejects it), SHA-256 verify the assembled file, and expand it. Either way
/// nothing is executed on the target.</para>
/// </summary>
public interface IDeploymentService
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> (a file or a folder on the Vivre host) onto
    /// <paramref name="host"/> at <paramref name="targetPath"/>. Tries the SMB admin share first, falling
    /// back to WinRM. Nothing is executed — the files are just delivered for the admin to install.
    /// </summary>
    /// <param name="host">Target machine (local when <see cref="PowerShell.HostName.IsLocal"/>, else remote).</param>
    /// <param name="sourcePath">The file or folder on the Vivre host to copy.</param>
    /// <param name="sourceIsFolder">True when <paramref name="sourcePath"/> is a folder (its contents are copied).</param>
    /// <param name="targetPath">The final file/folder location on the target (e.g.
    /// <c>C:\Windows\Temp\VivrePackages\CrowdStrike</c>, or <c>…\Setup.msi</c> for a single file).</param>
    /// <param name="credential">Account for the copy, or null for the current Windows identity.</param>
    Task<StageResult> StageAsync(
        string host,
        string sourcePath,
        bool sourceIsFolder,
        string targetPath,
        PSCredential? credential,
        CancellationToken token);
}

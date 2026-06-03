using System.Management.Automation;

namespace Vivre.Core.Software;

/// <summary>The result of checking one machine for an installed product (and optionally its service).</summary>
/// <param name="Found">True when a matching installed product was found.</param>
/// <param name="Name">The matched product's display name (null when not found).</param>
/// <param name="Version">The matched product's version (null when not found / unversioned).</param>
/// <param name="ServiceState">The matched service's state when a service was requested:
/// <c>"Running"</c>, <c>"Stopped"</c> (or another status), or <c>"not found"</c>. Null when no service
/// check was asked for.</param>
public sealed record SoftwareCheckResult(bool Found, string? Name, string? Version, string? ServiceState);

/// <summary>Thrown when a software check couldn't read the target at all (unreachable / nothing returned)
/// — distinct from a clean "not installed" answer.</summary>
public sealed class SoftwareProbeException : Exception
{
    public SoftwareProbeException(string message) : base(message)
    {
    }
}

/// <summary>
/// Checks whether a named product is installed on a machine (by searching the registry uninstall
/// entries), so the grid can show a per-machine "is X installed, and what version" column. Read-only;
/// the registry search is fast and — unlike <c>Win32_Product</c> — never triggers an MSI repair.
/// </summary>
public interface ISoftwareProbe
{
    /// <summary>
    /// Looks for an installed product whose display name contains <paramref name="query"/> on
    /// <paramref name="host"/> (local when <see cref="PowerShell.HostName.IsLocal"/>, else WinRM).
    /// Returns the first match (name + version), or <see cref="SoftwareCheckResult.Found"/> = false.
    /// When <paramref name="serviceName"/> is non-empty, also reports the state of the first service
    /// whose name/display name contains it (so you can confirm an agent is installed AND running).
    /// </summary>
    Task<SoftwareCheckResult> CheckAsync(
        string host,
        string query,
        string? serviceName,
        PSCredential? credential,
        CancellationToken cancellationToken);
}

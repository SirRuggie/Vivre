namespace Vivre.Core.PowerShell;

/// <summary>
/// Shared host-name helpers for the local-vs-remote dispatch decision. Centralised so the several
/// call sites that pick a local runspace over WinRM can't drift apart — change "what counts as
/// local" (e.g. to match the FQDN) in one place.
/// </summary>
public static class HostName
{
    /// <summary>True when <paramref name="host"/> refers to this machine, so a local runspace should
    /// be used instead of a remote WinRM session.</summary>
    public static bool IsLocal(string? host) =>
        string.IsNullOrWhiteSpace(host)
        || host is "localhost" or "127.0.0.1" or "::1" or "."
        || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}

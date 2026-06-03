using System.Management.Automation;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Software;

/// <inheritdoc cref="ISoftwareProbe"/>
/// <remarks>
/// Structure mirrors <see cref="Vitals.VitalsProbe"/>: one script run through <see cref="IPowerShellHost"/>
/// (local vs remote chosen by <see cref="HostName.IsLocal"/>) emitting a single <c>[PSCustomObject]</c>
/// that <see cref="Parse"/> reads. The product name is embedded as a single-quoted literal (no parameter
/// binding on the remote path), so a name with awkward characters can't break the script. Results are
/// emitted as a raw object and read via its properties — never <c>ConvertTo-Json</c> (a JSON string has
/// no properties to read).
/// </remarks>
public sealed class SoftwareProbe : ISoftwareProbe
{
    private readonly IPowerShellHost _powerShell;

    public SoftwareProbe(IPowerShellHost powerShell) => _powerShell = powerShell;

    public async Task<SoftwareCheckResult> CheckAsync(
        string host,
        string query,
        string? serviceName,
        PSCredential? credential,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        string script = BuildScript(PsSingleQuote(query), PsSingleQuote(serviceName?.Trim() ?? string.Empty));
        PSExecutionResult result = HostName.IsLocal(host)
            ? await _powerShell.RunLocalAsync(script, cancellationToken).ConfigureAwait(false)
            : await _powerShell.RunRemoteAsync(host, script, credential, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        return Parse(result);
    }

    private static SoftwareCheckResult Parse(PSExecutionResult result)
    {
        if (result.Output.Count == 0)
        {
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "no data returned";
            throw new SoftwareProbeException($"Could not query software on target: {detail}");
        }

        PSObject row = result.Output[0];
        return new SoftwareCheckResult(
            GetBool(row, "Found"), GetString(row, "Name"), GetString(row, "Version"), GetString(row, "ServiceState"));
    }

    /// <summary>
    /// Searches the 64- and 32-bit registry uninstall hives for the first product whose display name
    /// contains the query, emitting <c>{ Found; Name; Version; ServiceState }</c>. When a service name is
    /// given, also reports the first matching service's status (else "not found"). Registry-based (fast,
    /// and never triggers the MSI self-repair that <c>Win32_Product</c> does). Internal so tests can
    /// assert on it.
    /// </summary>
    internal static string BuildScript(string queryLiteral, string serviceLiteral) =>
        $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $q = {{queryLiteral}}
        $svc = {{serviceLiteral}}
        $paths = @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
        )
        $match = $null
        foreach ($p in $paths) {
            $match = Get-ItemProperty -Path $p -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -and ($_.DisplayName -like "*$q*" -or $_.Publisher -like "*$q*") } |
                Sort-Object DisplayName | Select-Object -First 1
            if ($match) { break }
        }
        $svcState = $null
        if ($svc) {
            $s = Get-Service -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like "*$svc*" -or $_.DisplayName -like "*$svc*" } |
                Select-Object -First 1
            if ($s) { $svcState = "$($s.Status)" } else { $svcState = 'not found' }
        }
        if ($match) {
            [PSCustomObject]@{ Found = $true; Name = "$($match.DisplayName)"; Version = "$($match.DisplayVersion)"; ServiceState = $svcState }
        } else {
            [PSCustomObject]@{ Found = $false; Name = $null; Version = $null; ServiceState = $svcState }
        }
        """;

    /// <summary>Wraps a string as a single-quoted PowerShell literal, doubling embedded single quotes.</summary>
    private static string PsSingleQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static object? Value(PSObject row, string name) => row.Properties[name]?.Value;

    private static bool GetBool(PSObject row, string name) => Value(row, name) is bool b && b;

    private static string? GetString(PSObject row, string name)
    {
        object? value = Value(row, name);
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString();
    }
}

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
/// <para>
/// When an <see cref="IDcomSoftwareReader"/> is supplied and WinRM is unavailable — any failure the
/// <see cref="RemoteFailureClassifier.IsWinRmUnavailable"/> classifier matches (a Kerberos 0x80090322
/// rejection, WinRM stopped/misconfigured, or a lost session) — the probe falls back to a read-only DCOM
/// registry read (like <see cref="Vitals.VitalsProbe"/>) so the column shows a real answer instead of an
/// error. If DCOM also fails the error names both transports.
/// </para>
/// </remarks>
public sealed class SoftwareProbe : ISoftwareProbe
{
    private readonly IPowerShellHost _powerShell;
    private readonly IDcomSoftwareReader? _dcomReader;

    public SoftwareProbe(IPowerShellHost powerShell, IDcomSoftwareReader? dcomReader = null)
    {
        _powerShell = powerShell;
        _dcomReader = dcomReader;
    }

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

        PSExecutionResult result;
        try
        {
            result = HostName.IsLocal(host)
                ? await _powerShell.RunLocalAsync(script, cancellationToken).ConfigureAwait(false)
                : await _powerShell.RunRemoteAsync(host, script, credential, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.IsWinRmUnavailable() && _dcomReader is not null && !HostName.IsLocal(host))
        {
            // WinRM is unavailable on this box — any failure the IsWinRmUnavailable classifier matches (a
            // Kerberos 0x80090322 rejection, WinRM stopped/misconfigured, or a lost session) reroutes here.
            // Read the installed-software answer over the read-only DCOM registry channel so the column shows
            // a real verdict instead of an error, on the ambient Windows login (the alternate credential
            // applies to the WinRM path only). The result is identical to a healthy WinRM read — the operator
            // never sees which transport answered. OperationCanceledException rethrows first; if DCOM also
            // fails, the error names BOTH transports.
            try
            {
                return await _dcomReader
                    .CheckAsync(host, query, serviceName?.Trim() is { Length: > 0 } s ? s : null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception dcomEx)
            {
                throw new SoftwareProbeException(
                    "WinRM was unavailable on this box and the DCOM fallback also failed: " + dcomEx.Message);
            }
        }

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
        if (Value(row, "Found") is not bool found)
        {
            throw new SoftwareProbeException("Could not query software on target: the response did not contain a valid Found verdict");
        }

        bool searchComplete = Value(row, "SearchComplete") is bool complete && complete;
        string? queryWarning = GetString(row, "QueryWarning");
        string? streamWarning = result.Errors.Count > 0 ? string.Join("; ", result.Errors) : null;
        if (result.HadErrors && streamWarning is null)
        {
            streamWarning = "PowerShell reported an error without details";
        }
        queryWarning = JoinWarnings(queryWarning, streamWarning);

        // Older/malformed output must never default to a clean negative. A positive is still useful
        // evidence, but absence requires the explicit completion marker emitted by the current script.
        if (!found && (!searchComplete || queryWarning is not null))
        {
            string detail = queryWarning
                ?? (result.Errors.Count > 0 ? result.Errors[0] : "the registry search did not report completion");
            throw new SoftwareProbeException($"Could not query software on target: {detail}");
        }

        if (found && GetString(row, "Name") is null)
        {
            throw new SoftwareProbeException("Could not query software on target: a match was reported without a product name");
        }

        return new SoftwareCheckResult(
            found,
            GetString(row, "Name"),
            GetString(row, "Version"),
            GetString(row, "ServiceState"),
            queryWarning,
            GetString(row, "ServiceError"));
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
        $ErrorActionPreference = 'Stop'
        $q = [System.Management.Automation.WildcardPattern]::Escape({{queryLiteral}})
        $svc = [System.Management.Automation.WildcardPattern]::Escape({{serviceLiteral}})
        $paths = @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
        )
        $match = $null
        $searchErrors = [System.Collections.Generic.List[string]]::new()
        $enumeratedRoots = 0
        foreach ($p in $paths) {
            $rows = [System.Collections.Generic.List[object]]::new()
            try {
                $keys = @(Get-ChildItem -Path $p -ErrorAction Stop)
                $enumeratedRoots++
                foreach ($key in $keys) {
                    try {
                        $item = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop
                        if ($item.DisplayName) { $rows.Add($item) }
                    } catch {
                        $searchErrors.Add("Registry entry '$($key.PSChildName)' could not be read: $($_.Exception.Message)")
                    }
                }
            } catch [System.Management.Automation.ItemNotFoundException] {
                # A missing optional uninstall path is benign only when another supported root enumerates.
            } catch {
                $searchErrors.Add("Uninstall path '$p' could not be read: $($_.Exception.Message)")
            }
            $match = $rows |
                Where-Object { $_.DisplayName -and ($_.DisplayName -like "*$q*" -or $_.Publisher -like "*$q*") } |
                Sort-Object DisplayName | Select-Object -First 1
            if ($match) { break }
        }
        $svcState = $null
        $svcError = $null
        if ($svc) {
            try {
                $s = Get-Service -ErrorAction Stop |
                    Where-Object { $_.Name -like "*$svc*" -or $_.DisplayName -like "*$svc*" } |
                    Select-Object -First 1
                if ($s) {
                    $svcState = "$($s.Status)"
                    if (-not $svcState) { $svcError = 'Service query returned a matching service without a status'; $svcState = $null }
                } else { $svcState = 'not found' }
            } catch {
                $svcError = "Service query failed: $($_.Exception.Message)"
            }
        }
        $complete = ($searchErrors.Count -eq 0 -and $enumeratedRoots -gt 0)
        $warning = if ($searchErrors.Count) { $searchErrors -join '; ' } else { $null }
        if ($match) {
            [PSCustomObject]@{ Found = $true; Name = "$($match.DisplayName)"; Version = "$($match.DisplayVersion)"; ServiceState = $svcState; SearchComplete = $complete; QueryWarning = $warning; ServiceError = $svcError }
        } else {
            [PSCustomObject]@{ Found = $false; Name = $null; Version = $null; ServiceState = $svcState; SearchComplete = $complete; QueryWarning = $warning; ServiceError = $svcError }
        }
        """;

    /// <summary>Wraps a string as a single-quoted PowerShell literal, doubling embedded single quotes.</summary>
    private static string PsSingleQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static object? Value(PSObject row, string name) => row.Properties[name]?.Value;

    private static string? JoinWarnings(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? null : second;
        return string.IsNullOrWhiteSpace(second) ? first : first + "; " + second;
    }

    private static string? GetString(PSObject row, string name)
    {
        object? value = Value(row, name);
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString();
    }
}

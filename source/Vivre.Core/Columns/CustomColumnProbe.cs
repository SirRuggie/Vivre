using System.Management.Automation;
using System.Text;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Columns;

/// <inheritdoc cref="ICustomColumnProbe"/>
/// <remarks>
/// Builds ONE combined script per host: each column's one-liner is base64-encoded, decoded on the target
/// into a <c>[ScriptBlock]</c>, and invoked inside its own <c>try/catch</c>, with the first output line
/// stuffed into a <c>[PSCustomObject]</c> property named after the column. Base64 + per-column try/catch
/// isolates a malformed or one-off-failing one-liner so it can't break the other columns or the batch.
/// Results come back as a real object read via its properties — never <c>ConvertTo-Json</c>. Local vs
/// remote is chosen by <see cref="HostName.IsLocal"/>, mirroring <see cref="Vitals.VitalsProbe"/>.
/// </remarks>
public sealed class CustomColumnProbe : ICustomColumnProbe
{
    private readonly IPowerShellHost _powerShell;

    public CustomColumnProbe(IPowerShellHost powerShell) => _powerShell = powerShell;

    public async Task<IReadOnlyDictionary<string, string?>> RunAsync(
        string host,
        IReadOnlyList<CustomColumnSpec> columns,
        PSCredential? credential,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            return new Dictionary<string, string?>();
        }

        string script = BuildScript(columns);
        PSExecutionResult result = HostName.IsLocal(host)
            ? await _powerShell.RunLocalAsync(script, cancellationToken).ConfigureAwait(false)
            : await _powerShell.RunRemoteAsync(host, script, credential, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        return Parse(result, columns);
    }

    private static IReadOnlyDictionary<string, string?> Parse(PSExecutionResult result, IReadOnlyList<CustomColumnSpec> columns)
    {
        PSObject? row = result.Output.Count > 0 ? result.Output[0] : null;
        if (row is null)
        {
            // The whole call returned nothing (host unreachable / pipeline died) — a host-level failure.
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "no data returned";
            throw new CustomColumnProbeException($"Could not evaluate columns on target: {detail}");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (CustomColumnSpec column in columns)
        {
            values[column.Name] = GetString(row, column.Name);
        }

        return values;
    }

    /// <summary>The combined per-host script (internal so tests can assert on its shape).</summary>
    internal static string BuildScript(IReadOnlyList<CustomColumnSpec> columns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine("$o = [ordered]@{}");
        foreach (CustomColumnSpec column in columns)
        {
            string nameLiteral = PsSingleQuote(column.Name);
            string scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(column.Script));
            sb.AppendLine("try {");
            sb.AppendLine($"  $sb = [ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{scriptBase64}')))");
            // Capture real output AND Write-Host text (6>&1 merges the information stream). Prefer the
            // returned value when there is one; fall back to whatever the script printed — so a plain
            // value column is clean, but a beginner's Write-Host still shows something.
            sb.AppendLine("  $info = New-Object System.Collections.ArrayList");
            sb.AppendLine("  $out = & $sb 6>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.InformationRecord]) { [void]$info.Add([string]$_) } else { $_ } }");
            sb.AppendLine("  if ($out) { $o[" + nameLiteral + "] = ($out | Select-Object -First 1 | Out-String).Trim() } else { $o[" + nameLiteral + "] = ($info -join ' ').Trim() }");
            sb.AppendLine("} catch {");
            sb.AppendLine($"  $o[{nameLiteral}] = 'ERR: ' + $_.Exception.Message");
            sb.AppendLine("}");
        }

        sb.AppendLine("[PSCustomObject]$o");
        return sb.ToString();
    }

    private static string PsSingleQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string? GetString(PSObject row, string name)
    {
        object? value = row.Properties[name]?.Value;
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString();
    }
}

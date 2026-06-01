using System.Management.Automation;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Remoting;

/// <summary>
/// Aggregates the standard pending-reboot markers (CBS, Windows Update Auto Update,
/// <c>PendingFileRenameOperations</c>, a pending computer rename, and the SCCM client when
/// installed) into a single small PowerShell run over WinRM. Mirrors the dispatch pattern of
/// <c>ConfigMgrClient</c>: same <see cref="IPowerShellHost"/>, local-or-remote based on the host.
/// </summary>
public sealed class HostRebootProbe : IHostRebootProbe
{
    private readonly IPowerShellHost _powerShell;

    public HostRebootProbe(IPowerShellHost powerShell) => _powerShell = powerShell;

    public async Task<bool?> IsRebootPendingAsync(
        string host,
        PSCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        PSExecutionResult result = IsLocal(host)
            ? await _powerShell.RunLocalAsync(Script, cancellationToken).ConfigureAwait(false)
            : await _powerShell.RunRemoteAsync(host, Script, credential, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Output.Count == 0)
        {
            return null;
        }

        object? value = result.Output[0].Properties["RebootPending"]?.Value;
        return value is bool b ? b : null;
    }

    private static bool IsLocal(string host) => HostName.IsLocal(host);

    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $pending = $false

        if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') {
            $pending = $true
        }

        if (-not $pending -and (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')) {
            $pending = $true
        }

        if (-not $pending) {
            $pfro = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue
            if ($pfro -and $pfro.PendingFileRenameOperations) { $pending = $true }
        }

        if (-not $pending) {
            $active = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName' -Name ComputerName -ErrorAction SilentlyContinue
            $next   = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName'       -Name ComputerName -ErrorAction SilentlyContinue
            if ($active -and $next -and ($active.ComputerName -ne $next.ComputerName)) { $pending = $true }
        }

        if (-not $pending) {
            try {
                $util = Get-CimInstance -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_ClientUtilities -ErrorAction Stop
                $r = Invoke-CimMethod -InputObject $util -MethodName DetermineIfRebootPending -ErrorAction Stop
                if ($r -and ($r.RebootPending -or $r.IsHardRebootPending)) { $pending = $true }
            } catch { }
        }

        [PSCustomObject]@{ RebootPending = [bool]$pending }
        """;
}

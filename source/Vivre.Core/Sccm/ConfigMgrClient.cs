using System.Management.Automation;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Sccm;

/// <inheritdoc cref="IConfigMgrClient"/>
public sealed class ConfigMgrClient : IConfigMgrClient
{
    private readonly IPowerShellHost _powerShell;

    public ConfigMgrClient(IPowerShellHost powerShell) => _powerShell = powerShell;

    public async Task<SccmClientInfo> GetClientHealthAsync(
        string host,
        PSCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        PSExecutionResult result = IsLocal(host)
            ? await _powerShell.RunLocalAsync(HealthScript, cancellationToken).ConfigureAwait(false)
            : await _powerShell.RunRemoteAsync(host, HealthScript, credential, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        return Parse(result);
    }

    public async Task<string> TriggerScheduleAsync(
        string host,
        ScheduleAction action,
        PSCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // ScheduleId comes from the fixed ClientActions list (a constant GUID), so
        // interpolating it into the script is safe — no external input reaches here.
        // $$ raw string: interpolations use {{ }}, so PowerShell's @{ } stays literal.
        string script = $$"""
            Invoke-CimMethod -Namespace 'ROOT\ccm' -ClassName SMS_Client -MethodName TriggerSchedule -Arguments @{ sScheduleID = '{{action.ScheduleId}}' } -ErrorAction Stop | Out-Null
            '{{action.CompletionMessage}}'
            """;

        PSExecutionResult result = IsLocal(host)
            ? await _powerShell.RunLocalAsync(script, cancellationToken).ConfigureAwait(false)
            : await _powerShell.RunRemoteAsync(host, script, credential, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        if (result.HadErrors || result.Output.Count == 0)
        {
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "no result returned";
            throw new SccmQueryException($"{action.Label} failed: {detail}");
        }

        return result.Output[0].ToString();
    }

    private static bool IsLocal(string host) => HostName.IsLocal(host);

    private static SccmClientInfo Parse(PSExecutionResult result)
    {
        if (result.Output.Count == 0)
        {
            // The script throws (terminating) if SMS_Client is absent, so no output
            // means "not a ConfigMgr client" or the query failed — surface the reason.
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "no data returned";
            throw new SccmQueryException($"No ConfigMgr client data from target: {detail}");
        }

        PSObject row = result.Output[0];
        return new SccmClientInfo(
            ClientVersion: GetString(row, "ClientVersion"),
            SiteCode: GetString(row, "SiteCode"),
            RebootRequired: GetBool(row, "RebootRequired"),
            MissingUpdates: GetBool(row, "MissingUpdates"),
            RunningUpdates: GetBool(row, "RunningUpdates"),
            UserLoggedOn: GetBool(row, "UserLoggedOn"),
            LastBootTime: GetDateTime(row, "LastBootTime"));
    }

    private static DateTime? GetDateTime(PSObject row, string name) =>
        row.Properties[name]?.Value is DateTime dt ? dt : null;

    private static string? GetString(PSObject row, string name)
    {
        object? value = row.Properties[name]?.Value;
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString();
    }

    private static bool GetBool(PSObject row, string name) =>
        row.Properties[name]?.Value is bool b && b;

    // Modernized from the legacy cm12 HealthCheck.ps.txt: Get-WmiObject -> CIM cmdlets
    // (Get-WmiObject doesn't exist in PowerShell 7, which the SDK host runs). Adds the
    // SMS_Client identity (ClientVersion + assigned site) and emits a single typed
    // object instead of the old semicolon-delimited string. SMS_Client is queried with
    // -ErrorAction Stop so a non-ConfigMgr target produces a terminating error (caught
    // by the host as HadErrors / no output) rather than silently returning blanks.
    private const string HealthScript = """
        $client = Get-CimInstance -Namespace 'ROOT\ccm' -ClassName SMS_Client -ErrorAction Stop

        # Site code: read SMS_Authority.Name ("SMS:<site>") rather than calling
        # SMS_Client.GetAssignedSite() — the method needs elevation (returns "Access
        # denied" unprivileged), but the property read works for a normal user.
        $site = $null
        $auth = @(Get-CimInstance -Namespace 'ROOT\ccm' -ClassName SMS_Authority -ErrorAction SilentlyContinue)
        if ($auth.Count -gt 0 -and $auth[0].Name) { $site = $auth[0].Name -replace '^SMS:', '' }

        $ccmReboot = $false
        $ccmHardReboot = $false
        try {
            $r = Invoke-CimMethod -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_ClientUtilities -MethodName DetermineIfRebootPending -ErrorAction Stop
            $ccmReboot = [bool]$r.RebootPending
            $ccmHardReboot = [bool]$r.IsHardRebootPending
        } catch { }

        $updates = @(Get-CimInstance -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_SoftwareUpdate -ErrorAction SilentlyContinue)
        $apps    = @(Get-CimInstance -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_Application -ErrorAction SilentlyContinue)
        $progs   = @(Get-CimInstance -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_Program -ErrorAction SilentlyContinue)

        $missingUpdates = @($updates | Where-Object { $_.ComplianceState -eq 0 }).Count -gt 0
        $patchReboot    = @($updates | Where-Object { $_.EvaluationState -in 8,9,10,16 }).Count -gt 0
        $runningUpdates = (@($apps  | Where-Object { $_.EvaluationState -in 11,12,27 }).Count -gt 0) -or `
                          (@($progs | Where-Object { $_.EvaluationState -eq 14 }).Count -gt 0) -or `
                          (@($updates | Where-Object { $_.EvaluationState -in 2,3,4,5,6,7,11 }).Count -gt 0)

        $lastBoot = $null
        try { $lastBoot = (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime } catch { }

        $componentReboot = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
        $fileRename = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction SilentlyContinue).PendingFileRenameOperations | Where-Object { $_ }).Count -ne 0

        $rebootRequired = $ccmReboot -or $ccmHardReboot -or $patchReboot -or $componentReboot -or $fileRename
        $userLoggedOn = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'explorer.exe'" -ErrorAction SilentlyContinue).Count -gt 0

        [PSCustomObject]@{
            ClientVersion  = $client.ClientVersion
            SiteCode       = $site
            RebootRequired = [bool]$rebootRequired
            MissingUpdates = [bool]$missingUpdates
            RunningUpdates = [bool]$runningUpdates
            UserLoggedOn   = [bool]$userLoggedOn
            LastBootTime   = $lastBoot
        }
        """;
}

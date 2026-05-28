using System.Globalization;
using System.Management.Automation;
using System.Text;
using System.Text.Json;
using Vivre.Core.Credentials;
using Vivre.Core.PowerShell;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Updates;

/// <summary>
/// The Windows Update Agent (WUA) lane: the engine behind <see cref="PatchService"/>.
///
/// <para><b>Scan</b> runs a search script over WinRM (read-only) and returns the
/// applicable-update list for the chosen <see cref="UpdateSource"/>.</para>
///
/// <para><b>Install</b> can't run inside a WinRM network-logon session (WUA returns
/// <c>WU_E_NO_INTERACTIVE_USER</c>), so the lane writes a worker script to
/// <c>C:\Windows\Temp</c> on the target and runs it from a <b>one-time SYSTEM
/// scheduled task</b>. The worker does search → download → install locally as SYSTEM
/// and writes a progress JSON after each state change; the controller polls that JSON
/// over WinRM, then deletes the task + files. Registering/starting the task is WinRM-first
/// with a <b>DCOM <c>Win32_Process.Create</c> fallback</b> (the same channel
/// <c>WinRmEnabler</c> uses). Polling/cleanup require WinRM.</para>
/// </summary>
public sealed class WuaUpdateLane
{
    private readonly IPowerShellHost _powerShell;

    public WuaUpdateLane(IPowerShellHost powerShell) => _powerShell = powerShell;

    // --- scan -------------------------------------------------------------

    public async Task<HostPatchStatus> ScanAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);

        string script = BuildScanScript(options.Source, options.IncludeDrivers);
        PSExecutionResult result = await RunScriptAsync(host, script, credential, cancellationToken).ConfigureAwait(false);

        if (result.HadErrors && result.Output.Count == 0)
        {
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "scan returned no data";
            return HostPatchStatus.Failed($"Scan failed: {detail}");
        }

        IReadOnlyList<SoftwareUpdate> updates = ParseScan(result.Output);
        updates = ApplyExclude(updates, options.ExcludeNameContains);

        string message = updates.Count == 0
            ? "Up to date"
            : $"{updates.Count} update(s) available";

        return new HostPatchStatus(PatchPhase.Available, message, AvailableCount: updates.Count)
        {
            Updates = updates,
        };
    }

    // --- install ----------------------------------------------------------

    public async Task<HostPatchStatus> InstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        string runId = Guid.NewGuid().ToString("N");
        string taskName = $"Vivre_WUA_{runId}";
        string workerPath = $@"C:\Windows\Temp\{taskName}.ps1";
        string progressPath = $@"C:\Windows\Temp\{taskName}_progress.json";

        progress.Report(new HostPatchStatus(PatchPhase.Scanning, "Starting update task…"));

        try
        {
            string worker = BuildInstallWorker(options, progressPath);
            string bootstrap = BuildBootstrapScript(taskName, workerPath, progressPath, worker, options);

            // WinRM-first; DCOM Win32_Process.Create fallback for the register+start step only.
            await StartTaskAsync(host, bootstrap, credential, cancellationToken).ConfigureAwait(false);

            return await PollAsync(host, taskName, progressPath, options, credential, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            progress.Report(new HostPatchStatus(PatchPhase.Idle, "Cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            var failed = HostPatchStatus.Failed(ex.Message);
            progress.Report(failed);
            return failed;
        }
        finally
        {
            // Always tear down the task + temp files, even on cancel/error. Best-effort:
            // a cleanup failure must not mask the real outcome.
            await CleanupAsync(host, taskName, workerPath, progressPath, credential).ConfigureAwait(false);
        }
    }

    private async Task<HostPatchStatus> PollAsync(
        string host,
        string taskName,
        string progressPath,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken)
    {
        string pollScript = BuildPollScript(taskName, progressPath);
        HostPatchStatus last = new(PatchPhase.Scanning, "Searching for updates…");
        string lastRaw = string.Empty;
        DateTime lastChange = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);

            PSExecutionResult result = await RunScriptAsync(host, pollScript, credential, cancellationToken).ConfigureAwait(false);
            string raw = result.Output.Count > 0 ? result.Output[0]?.ToString() ?? string.Empty : string.Empty;

            if (raw.Length > 0 && raw != lastRaw)
            {
                lastRaw = raw;
                lastChange = DateTime.UtcNow;

                if (TryParseProgress(raw, out HostPatchStatus status))
                {
                    last = status;
                    progress.Report(status);

                    if (status.Phase is PatchPhase.Done or PatchPhase.PendingReboot or PatchPhase.Error)
                    {
                        return status;
                    }
                }
                else if (raw == TaskGoneMarker)
                {
                    // The task finished/vanished before writing a terminal JSON — treat as done.
                    return last with { Phase = PatchPhase.Done };
                }
            }
            else if (DateTime.UtcNow - lastChange > options.StuckThreshold)
            {
                return HostPatchStatus.Failed(
                    $"No progress for {options.StuckThreshold.TotalMinutes:N0} min — host may be stuck.");
            }
        }
    }

    // --- target transport -------------------------------------------------

    /// <summary>Runs a script on the target (local or WinRM), mirroring <c>ConfigMgrClient</c>'s dispatch.</summary>
    private Task<PSExecutionResult> RunScriptAsync(
        string host,
        string script,
        ConnectionCredential? credential,
        CancellationToken cancellationToken) =>
        IsLocal(host)
            ? _powerShell.RunLocalAsync(script, cancellationToken)
            : _powerShell.RunRemoteAsync(host, script, credential?.ToPowerShellCredential(), cancellationToken: cancellationToken);

    /// <summary>Registers + starts the SYSTEM task: WinRM first, DCOM <c>Win32_Process.Create</c> on failure.</summary>
    private async Task StartTaskAsync(
        string host,
        string bootstrap,
        ConnectionCredential? credential,
        CancellationToken cancellationToken)
    {
        try
        {
            PSExecutionResult result = await RunScriptAsync(host, bootstrap, credential, cancellationToken).ConfigureAwait(false);
            if (!result.HadErrors)
            {
                return;
            }

            // WinRM reached the box but the registration itself errored — surface it; DCOM won't help.
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "task registration failed";
            throw new InvalidOperationException(detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            // WinRM unreachable — fall back to DCOM (the EnableWinRM channel).
            cancellationToken.ThrowIfCancellationRequested();
            StartTaskViaDcom(host, bootstrap, credential);
        }
    }

    /// <summary>
    /// DCOM fallback: run the bootstrap as a base64 <c>-EncodedCommand</c> via
    /// <c>Win32_Process.Create</c> (same plumbing as <c>WinRmEnabler</c>). Note polling
    /// still needs WinRM, so this only helps when WinRM reads work but the initial
    /// process-launch needed DCOM.
    /// </summary>
    private static void StartTaskViaDcom(string host, string bootstrap, ConnectionCredential? credential)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
        string commandLine = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}";

        using var options = new DComSessionOptions();
        if (credential is not null)
        {
            options.AddDestinationCredentials(new CimCredential(
                PasswordAuthenticationMechanism.Default,
                credential.Domain,
                credential.UserName,
                credential.Password));
        }

        using CimSession session = CimSession.Create(host, options);
        using var arguments = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("CommandLine", commandLine, CimFlags.In),
        };

        using CimMethodResult result =
            session.InvokeMethod(@"root\cimv2", "Win32_Process", "Create", arguments);

        uint returnValue = Convert.ToUInt32(result.ReturnValue.Value, CultureInfo.InvariantCulture);
        if (returnValue != 0)
        {
            throw new InvalidOperationException(
                $"DCOM Win32_Process.Create on '{host}' returned {returnValue} (could not start the update task).");
        }
    }

    private async Task CleanupAsync(
        string host,
        string taskName,
        string workerPath,
        string progressPath,
        ConnectionCredential? credential)
    {
        try
        {
            string script = BuildCleanupScript(taskName, workerPath, progressPath);
            // Use a detached token: cleanup must run even after a cancelled install.
            await RunScriptAsync(host, script, credential, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a leftover task/file is harmless (it's named per-run) and a
            // re-scan reflects the true state. Don't let cleanup mask the real result.
        }
    }

    // --- parsing (static, unit-tested host-free) --------------------------

    /// <summary>Turns the scan script's <c>PSCustomObject</c> rows into typed updates.</summary>
    public static IReadOnlyList<SoftwareUpdate> ParseScan(IReadOnlyList<PSObject> rows)
    {
        var list = new List<SoftwareUpdate>(rows.Count);
        foreach (PSObject row in rows)
        {
            string? title = Str(row, "Title");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            list.Add(new SoftwareUpdate(
                Title: title!,
                ArticleId: Str(row, "KB"),
                IsDownloaded: Bool(row, "IsDownloaded"),
                SizeMb: Dbl(row, "SizeMb")));
        }

        return list;
    }

    /// <summary>Drops updates whose title contains any exclude substring (case-insensitive).</summary>
    public static IReadOnlyList<SoftwareUpdate> ApplyExclude(
        IReadOnlyList<SoftwareUpdate> updates,
        IReadOnlyList<string> excludeNameContains)
    {
        if (excludeNameContains is null || excludeNameContains.Count == 0)
        {
            return updates;
        }

        string[] terms = [.. excludeNameContains
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())];

        if (terms.Length == 0)
        {
            return updates;
        }

        return [.. updates.Where(u =>
            !terms.Any(t => u.Title.Contains(t, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>Parses one progress-JSON line into a status. Returns false for non-JSON markers.</summary>
    public static bool TryParseProgress(string raw, out HostPatchStatus status)
    {
        status = new HostPatchStatus(PatchPhase.Scanning, "Working…");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // The task host (Windows PowerShell 5.1) prefixes a UTF-8 BOM and appends a
        // newline — isolate the JSON object by its outer braces (also rejects markers).
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        string json = raw[start..(end + 1)];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            PatchPhase phase = MapPhase(GetString(root, "phase"));
            string message = GetString(root, "message") ?? phase.ToString();
            int? percent = GetInt(root, "percent");
            int available = GetInt(root, "available") ?? 0;
            int installed = GetInt(root, "installed") ?? 0;
            int failed = GetInt(root, "failed") ?? 0;
            bool rebootPending = GetBool(root, "rebootPending");

            status = new HostPatchStatus(
                Phase: phase,
                Message: message,
                Percent: percent,
                AvailableCount: available,
                InstalledCount: installed,
                FailedCount: failed,
                RebootPending: rebootPending);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static PatchPhase MapPhase(string? phase) => phase?.ToLowerInvariant() switch
    {
        "searching" or "scanning" => PatchPhase.Scanning,
        "downloading" => PatchPhase.Downloading,
        "installing" => PatchPhase.Installing,
        "pendingreboot" or "rebootrequired" => PatchPhase.PendingReboot,
        "rebooting" => PatchPhase.Rebooting,
        "done" or "noupdates" or "complete" => PatchPhase.Done,
        "error" => PatchPhase.Error,
        _ => PatchPhase.Scanning,
    };

    // --- embedded scripts -------------------------------------------------

    /// <summary>Marker the poll script emits when the scheduled task is gone and no JSON remains.</summary>
    private const string TaskGoneMarker = "__VIVRE_TASK_GONE__";

    private static string BuildScanScript(UpdateSource source, bool includeDrivers)
    {
        WuaServerSelection sel = WuaServerSelection.For(source);
        // Default off matches the Windows Update UI and BatchPatch; flip via PatchOptions.IncludeDrivers.
        string typeFilter = includeDrivers ? string.Empty : " and Type='Software'";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $session  = New-Object -ComObject Microsoft.Update.Session
            $searcher = $session.CreateUpdateSearcher()
            {{SourceSelectionSnippet(sel)}}
            $result = $searcher.Search("IsInstalled=0 and IsHidden=0{{typeFilter}}")
            foreach ($u in $result.Updates) {
                $kb = $null
                if ($u.KBArticleIDs.Count -gt 0) { $kb = $u.KBArticleIDs.Item(0) }
                $size = 0
                try { $size = [math]::Round($u.MaxDownloadSize / 1MB, 1) } catch { }
                [PSCustomObject]@{
                    Title        = $u.Title
                    KB           = $kb
                    IsDownloaded = [bool]$u.IsDownloaded
                    SizeMb       = $size
                }
            }
            """;
    }

    /// <summary>PowerShell that sets the searcher's source (and registers Microsoft Update when needed).</summary>
    private static string SourceSelectionSnippet(WuaServerSelection sel)
    {
        if (sel.ServiceId is null)
        {
            return $"$searcher.ServerSelection = {sel.ServerSelection}";
        }

        // ServerSelection 3 (ssOthers) + a registered ServiceID = Microsoft Update.
        return $$"""
            try {
                $sm = New-Object -ComObject Microsoft.Update.ServiceManager
                $null = $sm.AddService2('{{sel.ServiceId}}', 2, '')
            } catch { }
            $searcher.ServerSelection = {{sel.ServerSelection}}
            $searcher.ServiceID = '{{sel.ServiceId}}'
            """;
    }

    /// <summary>
    /// The worker the SYSTEM task runs locally: search → per-update download+install,
    /// writing a progress JSON after each step. All settings are baked in (no args).
    /// </summary>
    private static string BuildInstallWorker(PatchOptions options, string progressPath)
    {
        WuaServerSelection sel = WuaServerSelection.For(options.Source);
        string excludeArray = BuildExcludePsArray(options.ExcludeNameContains);
        string includeArray = BuildIncludeKbPsArray(options.IncludeKbArticleIds);
        // Default off matches the Windows Update UI and BatchPatch; flip via PatchOptions.IncludeDrivers.
        string typeFilter = options.IncludeDrivers ? string.Empty : " and Type='Software'";
        string rebootAfter = options.RebootBehavior == RebootBehavior.RebootAndWait ? "$true" : "$false";

        // Single-quoted here-string callers embed this verbatim — keep it free of a leading '@.
        return $$"""
            $ErrorActionPreference = 'Stop'
            $progressPath = '{{progressPath}}'
            $excludes = {{excludeArray}}
            $includeKbs = {{includeArray}}
            $rebootAfter = {{rebootAfter}}

            function Write-Progress2($phase, $message, $percent, $available, $installed, $failed, $rebootPending) {
                $obj = [PSCustomObject]@{
                    phase = $phase; message = $message; percent = $percent
                    available = $available; installed = $installed; failed = $failed
                    rebootPending = [bool]$rebootPending
                }
                $tmp = "$progressPath.tmp"
                ($obj | ConvertTo-Json -Compress) | Set-Content -Path $tmp -Encoding UTF8
                Move-Item -Path $tmp -Destination $progressPath -Force
            }

            try {
                Write-Progress2 'Searching' 'Searching for updates…' 0 0 0 0 $false
                $session  = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                {{SourceSelectionSnippet(sel)}}
                $result = $searcher.Search("IsInstalled=0 and IsHidden=0 and DeploymentAction='Installation'{{typeFilter}}")

                $applicable = @()
                foreach ($u in $result.Updates) {
                    $skip = $false
                    foreach ($x in $excludes) { if ($u.Title -like "*$x*") { $skip = $true; break } }
                    if (-not $skip -and $includeKbs.Count -gt 0) {
                        $kb = $null
                        if ($u.KBArticleIDs.Count -gt 0) { $kb = $u.KBArticleIDs.Item(0) }
                        if (($null -eq $kb) -or ($includeKbs -notcontains $kb)) { $skip = $true }
                    }
                    if (-not $skip) { $applicable += $u }
                }

                $total = $applicable.Count
                if ($total -eq 0) {
                    Write-Progress2 'Done' 'No applicable updates' 100 0 0 0 $false
                    return
                }

                $installed = 0; $failed = 0; $rebootPending = $false
                for ($i = 0; $i -lt $total; $i++) {
                    $u = $applicable[$i]
                    $pct = [int](($i / $total) * 100)

                    $coll = New-Object -ComObject Microsoft.Update.UpdateColl
                    $null = $coll.Add($u)

                    Write-Progress2 'Downloading' ("Downloading {0} of {1}" -f ($i + 1), $total) $pct $total $installed $failed $rebootPending
                    $downloader = $session.CreateUpdateDownloader()
                    $downloader.Updates = $coll
                    $null = $downloader.Download()

                    Write-Progress2 'Installing' ("Installing {0} of {1}" -f ($i + 1), $total) $pct $total $installed $failed $rebootPending
                    $installer = $session.CreateUpdateInstaller()
                    $installer.Updates = $coll
                    $r = $installer.Install()
                    if ($r.ResultCode -eq 2) { $installed++ } else { $failed++ }
                    if ($r.RebootRequired) { $rebootPending = $true }
                }

                if ($rebootPending) {
                    Write-Progress2 'PendingReboot' ("Installed {0}, reboot required" -f $installed) 100 $total $installed $failed $true
                    if ($rebootAfter) {
                        Start-Sleep -Seconds 5
                        Restart-Computer -Force
                    }
                } else {
                    Write-Progress2 'Done' ("Installed {0} update(s)" -f $installed) 100 $total $installed $failed $false
                }
            } catch {
                Write-Progress2 'Error' ($_.Exception.Message) $null 0 0 0 $false
            }
            """;
    }

    private static string BuildBootstrapScript(
        string taskName,
        string workerPath,
        string progressPath,
        string worker,
        PatchOptions options)
    {
        // Single-quoted here-string keeps the worker literal (no PS interpolation/escaping).
        string trigger = options is { RunBehavior: RunBehavior.ScheduleAt, ScheduleAt: { } at }
            ? $"$trigger = New-ScheduledTaskTrigger -Once -At '{at.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}'"
            : "$trigger = $null";

        string registerTrigger = options.RunBehavior == RunBehavior.ScheduleAt
            ? "-Trigger $trigger "
            : string.Empty;

        string startNow = options.RunBehavior == RunBehavior.ScheduleAt
            ? string.Empty
            : $"Start-ScheduledTask -TaskName '{taskName}'";

        return $$"""
            $ErrorActionPreference = 'Stop'
            $worker = @'
            {{worker}}
            '@
            Set-Content -Path '{{workerPath}}' -Value $worker -Encoding UTF8
            Remove-Item '{{progressPath}}' -ErrorAction SilentlyContinue
            {{trigger}}
            $action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File "{{workerPath}}"'
            $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -RunLevel Highest
            $settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 6)
            Register-ScheduledTask -TaskName '{{taskName}}' -Action $action -Principal $principal -Settings $settings {{registerTrigger}}-Force | Out-Null
            {{startNow}}
            'started'
            """;
    }

    private static string BuildPollScript(string taskName, string progressPath)
    {
        // Emit the latest progress JSON if present; otherwise, if the task is gone, the marker.
        return $$"""
            if (Test-Path '{{progressPath}}') {
                Get-Content -Path '{{progressPath}}' -Raw
            } else {
                $t = Get-ScheduledTask -TaskName '{{taskName}}' -ErrorAction SilentlyContinue
                if ($null -eq $t) { '{{TaskGoneMarker}}' } else { '' }
            }
            """;
    }

    private static string BuildCleanupScript(string taskName, string workerPath, string progressPath) =>
        $$"""
            Unregister-ScheduledTask -TaskName '{{taskName}}' -Confirm:$false -ErrorAction SilentlyContinue
            Remove-Item '{{workerPath}}' -Force -ErrorAction SilentlyContinue
            Remove-Item '{{progressPath}}' -Force -ErrorAction SilentlyContinue
            Remove-Item '{{progressPath}}.tmp' -Force -ErrorAction SilentlyContinue
            'cleaned'
            """;

    private static string BuildExcludePsArray(IReadOnlyList<string> excludes)
    {
        string[] terms = [.. (excludes ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => "'" + t.Trim().Replace("'", "''", StringComparison.Ordinal) + "'")];

        return terms.Length == 0 ? "@()" : "@(" + string.Join(", ", terms) + ")";
    }

    /// <summary>
    /// Builds the PowerShell array literal of KB ids the install worker restricts to (the ticked
    /// updates from the per-machine checklist). Null/empty ⇒ <c>@()</c>, which the worker treats as
    /// "no KB filter" (install everything not excluded). Public for host-free testing.
    /// </summary>
    public static string BuildIncludeKbPsArray(IReadOnlyList<string>? includeKbs)
    {
        string[] terms = [.. (includeKbs ?? [])
            .Where(kb => !string.IsNullOrWhiteSpace(kb))
            .Select(kb => "'" + kb.Trim().Replace("'", "''", StringComparison.Ordinal) + "'")];

        return terms.Length == 0 ? "@()" : "@(" + string.Join(", ", terms) + ")";
    }

    // --- helpers ----------------------------------------------------------

    private static bool IsLocal(string host) =>
        string.IsNullOrWhiteSpace(host)
        || host is "localhost" or "127.0.0.1" or "::1" or "."
        || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    private static string? Str(PSObject row, string name)
    {
        object? value = row.Properties[name]?.Value;
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString();
    }

    private static bool Bool(PSObject row, string name) =>
        row.Properties[name]?.Value is bool b && b;

    private static double Dbl(PSObject row, string name)
    {
        object? value = row.Properties[name]?.Value;
        return value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out int n) ? n : null,
            _ => null,
        };
    }

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement el)
        && el.ValueKind is JsonValueKind.True or JsonValueKind.False
        && el.GetBoolean();
}

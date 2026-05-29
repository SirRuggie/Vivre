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

        string script = BuildScanScript(options.Source, options.IncludeDrivers, options.Scope);
        PSExecutionResult result = await RunScriptAsync(host, script, credential, cancellationToken).ConfigureAwait(false);

        if (result.HadErrors && result.Output.Count == 0)
        {
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "scan returned no data";
            return HostPatchStatus.Failed($"Scan failed: {detail}");
        }

        IReadOnlyList<SoftwareUpdate> updates = ParseScan(result.Output);
        updates = ApplyExclude(updates, options.ExcludeNameContains);

        string message = options.Scope == UpdateScope.Installed
            ? (updates.Count == 0 ? "No installed updates" : $"{updates.Count} installed update(s)")
            : (updates.Count == 0 ? "Up to date" : $"{updates.Count} update(s) available");

        return new HostPatchStatus(PatchPhase.Available, message, AvailableCount: updates.Count)
        {
            Updates = updates,
        };
    }

    // --- install ----------------------------------------------------------

    public Task<HostPatchStatus> InstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken) =>
        RunWorkerTaskAsync(host, options, credential, progress, BuildInstallWorker, "Starting update task…", cancellationToken);

    public Task<HostPatchStatus> UninstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken) =>
        RunWorkerTaskAsync(host, options, credential, progress, BuildUninstallWorker, "Starting uninstall task…", cancellationToken);

    /// <summary>
    /// Shared implementation for install + uninstall — same SYSTEM-task plumbing (register, start,
    /// poll the progress JSON, clean up) with the worker script swapped in.
    /// </summary>
    private async Task<HostPatchStatus> RunWorkerTaskAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        Func<PatchOptions, string, string> buildWorker,
        string startingMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        string runId = Guid.NewGuid().ToString("N");
        string taskName = $"Vivre_WUA_{runId}";
        string workerPath = $@"C:\Windows\Temp\{taskName}.ps1";
        string progressPath = $@"C:\Windows\Temp\{taskName}_progress.json";

        progress.Report(new HostPatchStatus(PatchPhase.Scanning, startingMessage));

        try
        {
            string worker = buildWorker(options, progressPath);
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
            // Defensive: a null row or a row that throws while we read its properties shouldn't
            // dynamite the whole parse — just skip it. Pairs with the per-iteration try/catch on
            // the PowerShell side.
            if (row is null)
            {
                continue;
            }

            try
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
                    SizeMb: Dbl(row, "SizeMb"),
                    IsUninstallable: BoolOr(row, "IsUninstallable", fallback: true),
                    InstalledAt: DateTimeOrNull(row, "InstalledAt")));
            }
            catch
            {
                // Skip rows that can't be parsed.
            }
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

    private static string BuildScanScript(UpdateSource source, bool includeDrivers, UpdateScope scope)
    {
        WuaServerSelection sel = WuaServerSelection.For(source);
        // Default off matches the Windows Update UI and BatchPatch; flip via PatchOptions.IncludeDrivers.
        string typeFilter = includeDrivers ? string.Empty : " and Type='Software'";
        // Applicable → "IsInstalled=0" (default), Installed → "IsInstalled=1" (the uninstall flow).
        string installedFilter = scope == UpdateScope.Installed ? "IsInstalled=1" : "IsInstalled=0";
        // For Applicable, IsUninstallable isn't meaningful — emit $true so the checklist's checkboxes
        // are enabled for install. For Installed, use the WUA value so non-uninstallable rows are greyed.
        string uninstallableExpr = scope == UpdateScope.Installed ? "[bool]$u.IsUninstallable" : "$true";
        // For Installed scope, build maps from the WUA history so each row carries its install date.
        // Match primarily by UpdateID (Identity GUID); also keep a KB-keyed fallback because WUSA-
        // installed updates sometimes show different UpdateIDs in history than the current Identity.
        string historyBlock = scope == UpdateScope.Installed
            ? """
                $dates = @{}
                $kbDates = @{}
                try {
                    $histCount = $searcher.GetTotalHistoryCount()
                    if ($histCount -gt 0) {
                        $history = $searcher.QueryHistory(0, $histCount)
                        foreach ($h in $history) {
                            if ($h.Operation -ne 1) { continue }
                            if (-not $h.UpdateIdentity) { continue }
                            $id = $h.UpdateIdentity.UpdateID
                            if ($id) {
                                if (-not $dates.ContainsKey($id) -or $dates[$id] -lt $h.Date) {
                                    $dates[$id] = $h.Date
                                }
                            }
                            if ($h.Title -and $h.Title -match 'KB(\d+)') {
                                $kbKey = $matches[1]
                                if (-not $kbDates.ContainsKey($kbKey) -or $kbDates[$kbKey] -lt $h.Date) {
                                    $kbDates[$kbKey] = $h.Date
                                }
                            }
                        }
                    }
                } catch { }
                """
            : "$dates = @{}; $kbDates = @{}";
        // Multi-line block (not inline) so the lookup is wrapped in its own try/catch — a single
        // update with a stale Identity COM proxy can otherwise throw NRE and kill the whole scan
        // foreach.
        string installedAtBlock = scope == UpdateScope.Installed
            ? """
                $installedAt = $null
                try {
                    $uid = $null
                    if ($null -ne $u.Identity) { $uid = $u.Identity.UpdateID }
                    if ($uid -and $dates.ContainsKey($uid)) {
                        $installedAt = $dates[$uid]
                    } elseif ($kb -and $kbDates.ContainsKey($kb)) {
                        $installedAt = $kbDates[$kb]
                    }
                } catch { }
                """
            : "$installedAt = $null";

        return $$"""
            $ErrorActionPreference = 'Stop'
            $session  = New-Object -ComObject Microsoft.Update.Session
            $searcher = $session.CreateUpdateSearcher()
            {{SourceSelectionSnippet(sel)}}
            {{historyBlock}}
            $result = $searcher.Search("{{installedFilter}} and IsHidden=0{{typeFilter}}")
            foreach ($u in $result.Updates) {
                # Wrap the whole iteration so a single weird update (stale COM proxy, missing
                # property, unusual subtype) just gets skipped instead of killing the scan.
                try {
                    $kb = $null
                    if ($u.KBArticleIDs.Count -gt 0) { $kb = $u.KBArticleIDs.Item(0) }
                    $size = 0
                    try { $size = [math]::Round($u.MaxDownloadSize / 1MB, 1) } catch { }
                    {{installedAtBlock}}
                    [PSCustomObject]@{
                        Title           = $u.Title
                        KB              = $kb
                        IsDownloaded    = [bool]$u.IsDownloaded
                        SizeMb          = $size
                        IsUninstallable = {{uninstallableExpr}}
                        InstalledAt     = $installedAt
                    }
                } catch { }
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
                # ts (timestamp ticks) makes every write unique even when nothing visible changed,
                # so the controller's "no progress" stuck-detector never false-positives on a slow link.
                $obj = [PSCustomObject]@{
                    phase = $phase; message = $message; percent = $percent
                    available = $available; installed = $installed; failed = $failed
                    rebootPending = [bool]$rebootPending
                    ts = (Get-Date).Ticks
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
                # Interim heartbeat so a slow filter step (large \$result.Updates) doesn't look
                # identical to a still-running search.
                Write-Progress2 'Searching' ("Search complete — {0} updates returned, filtering…" -f $result.Updates.Count) 3 0 0 0 $false

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
                Write-Progress2 'Searching' ("$total update(s) matched — starting downloads…") 5 $total 0 0 $false

                $installed = 0; $failed = 0; $rebootPending = $false
                for ($i = 0; $i -lt $total; $i++) {
                    # Per-iteration try/catch: a single update throwing (BeginDownload returning
                    # null, stale COM proxy, missing property, COM HRESULT) shouldn't kill the
                    # whole worker. The bare CLR exception text from one bad update was bubbling
                    # up to the outer catch and surfacing as the row's UpdateMessage.
                    try {
                    $u = $applicable[$i]

                    $coll = New-Object -ComObject Microsoft.Update.UpdateColl
                    $null = $coll.Add($u)

                    # --- Download with live progress polling ---
                    # Begin* returns immediately with an IDownloadJob; we poll GetProgress() every
                    # 2s and write a fresh JSON so the UI shows real bytes/percent. Each update
                    # contributes 50% download + 50% install to the overall progress bar.
                    $dlPrefix = "Downloading {0} of {1}" -f ($i + 1), $total
                    Write-Progress2 'Downloading' "$dlPrefix starting…" ([int](($i * 100) / $total)) $total $installed $failed $rebootPending

                    $downloader = $session.CreateUpdateDownloader()
                    $downloader.Updates = $coll

                    # Try the async Begin* path for live byte/percent progress; fall back to
                    # synchronous Download() if Begin* throws or returns null. The async API
                    # wants non-null IUnknown callbacks on some WUA configurations and PS can't
                    # supply real COM callback objects — so we silently get NRE on .IsCompleted
                    # otherwise, and the per-iteration catch above eats every update as $failed.
                    $dlResult = $null
                    $dlJob = $null
                    try { $dlJob = $downloader.BeginDownload($null, $null, $null) } catch { }

                    if ($null -ne $dlJob) {
                        while (-not $dlJob.IsCompleted) {
                            Start-Sleep -Seconds 2
                            $dlPct = 0; $bytesDl = 0; $bytesTotal = 0
                            try {
                                $dlProgress = $dlJob.GetProgress()
                                $dlPct = [int]$dlProgress.PercentComplete
                                $bytesDl = [long]$dlProgress.TotalBytesDownloaded
                                $bytesTotal = [long]$dlProgress.TotalBytesToDownload
                            } catch { }
                            $overallPct = [int]((($i * 100) + ($dlPct / 2)) / $total)
                            if ($bytesTotal -gt 0) {
                                $sizeText = " ({0:N0}/{1:N0} MB)" -f ($bytesDl / 1MB), ($bytesTotal / 1MB)
                            } else {
                                $sizeText = ""
                            }
                            Write-Progress2 'Downloading' ("$dlPrefix — $dlPct%$sizeText") $overallPct $total $installed $failed $rebootPending
                        }
                        try { $dlResult = $downloader.EndDownload($dlJob) } catch { }
                    }

                    if ($null -eq $dlResult) {
                        Write-Progress2 'Downloading' "$dlPrefix (sync mode — no live progress)" ([int](($i * 100) / $total)) $total $installed $failed $rebootPending
                        $dlResult = $downloader.Download()
                    }
                    if ($dlResult.ResultCode -ne 2) {
                        $failed++
                        $overallPct = [int]((($i + 1) * 100) / $total)
                        Write-Progress2 'Downloading' ("$dlPrefix failed (result code {0})" -f $dlResult.ResultCode) $overallPct $total $installed $failed $rebootPending
                        continue
                    }

                    # --- Install with live progress polling ---
                    $instPrefix = "Installing {0} of {1}" -f ($i + 1), $total
                    Write-Progress2 'Installing' "$instPrefix starting…" ([int]((($i * 100) + 50) / $total)) $total $installed $failed $rebootPending

                    $installer = $session.CreateUpdateInstaller()
                    $installer.Updates = $coll

                    # Same Begin*-with-sync-fallback pattern as Download above.
                    $r = $null
                    $instJob = $null
                    try { $instJob = $installer.BeginInstall($null, $null, $null) } catch { }

                    if ($null -ne $instJob) {
                        while (-not $instJob.IsCompleted) {
                            Start-Sleep -Seconds 2
                            $instPct = 0
                            try {
                                $instProgress = $instJob.GetProgress()
                                $instPct = [int]$instProgress.PercentComplete
                            } catch { }
                            $overallPct = [int]((($i * 100) + 50 + ($instPct / 2)) / $total)
                            Write-Progress2 'Installing' ("$instPrefix — $instPct%") $overallPct $total $installed $failed $rebootPending
                        }
                        try { $r = $installer.EndInstall($instJob) } catch { }
                    }

                    if ($null -eq $r) {
                        Write-Progress2 'Installing' "$instPrefix (sync mode — no live progress)" ([int]((($i * 100) + 50) / $total)) $total $installed $failed $rebootPending
                        $r = $installer.Install()
                    }
                    if ($r.ResultCode -eq 2) { $installed++ } else { $failed++ }
                    if ($r.RebootRequired) { $rebootPending = $true }
                    } catch {
                        # Mark this row failed, surface a useful message, keep going.
                        $failed++
                        $overallPct = [int]((($i + 1) * 100) / $total)
                        Write-Progress2 'Installing' ("Update {0} of {1} failed: $($_.Exception.Message)" -f ($i + 1), $total) $overallPct $total $installed $failed $rebootPending
                    }
                }

                # Surface the failed count in the final message — "Installed 0 update(s)" hides
                # the difference between "no work to do" and "5 attempts all failed".
                $summary = if ($failed -gt 0) { "Installed $installed, $failed failed" } else { "Installed $installed update(s)" }
                if ($rebootPending) {
                    Write-Progress2 'PendingReboot' "$summary, reboot required" 100 $total $installed $failed $true
                    if ($rebootAfter) {
                        Start-Sleep -Seconds 5
                        Restart-Computer -Force
                    }
                } else {
                    Write-Progress2 'Done' $summary 100 $total $installed $failed $false
                }
            } catch {
                Write-Progress2 'Error' ($_.Exception.Message) $null 0 0 0 $false
            }
            """;
    }

    /// <summary>
    /// The uninstall worker the SYSTEM task runs locally: search installed updates → filter to the
    /// per-machine selection (uninstallable + ticked) → per-update <c>BeginUninstall</c> with live
    /// progress polling, writing the same progress JSON shape the install worker does so the
    /// controller's <c>PollAsync</c> works unchanged. Reuses <c>Phase=Installing</c> because there
    /// is no separate Uninstalling phase in the JSON schema; the message says "Uninstalling X of N".
    /// </summary>
    private static string BuildUninstallWorker(PatchOptions options, string progressPath)
    {
        WuaServerSelection sel = WuaServerSelection.For(options.Source);
        string excludeArray = BuildExcludePsArray(options.ExcludeNameContains);
        string includeArray = BuildIncludeKbPsArray(options.IncludeKbArticleIds);
        string rebootAfter = options.RebootBehavior == RebootBehavior.RebootAndWait ? "$true" : "$false";

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
                    ts = (Get-Date).Ticks
                }
                $tmp = "$progressPath.tmp"
                ($obj | ConvertTo-Json -Compress) | Set-Content -Path $tmp -Encoding UTF8
                Move-Item -Path $tmp -Destination $progressPath -Force
            }

            try {
                Write-Progress2 'Searching' 'Finding installed updates to uninstall…' 0 0 0 0 $false
                $session  = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                {{SourceSelectionSnippet(sel)}}
                $result = $searcher.Search("IsInstalled=1 and IsHidden=0")

                $applicable = @()
                foreach ($u in $result.Updates) {
                    if (-not $u.IsUninstallable) { continue }
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
                    Write-Progress2 'Done' 'No uninstallable updates matched the selection' 100 0 0 0 $false
                    return
                }

                $installed = 0; $failed = 0; $rebootPending = $false
                for ($i = 0; $i -lt $total; $i++) {
                    # Per-iteration try/catch — see the install worker for the rationale; the same
                    # one-row-shouldn't-nuke-the-worker pattern applies to uninstall.
                    try {
                    $u = $applicable[$i]

                    $coll = New-Object -ComObject Microsoft.Update.UpdateColl
                    $null = $coll.Add($u)

                    $unPrefix = "Uninstalling {0} of {1}" -f ($i + 1), $total
                    Write-Progress2 'Installing' "$unPrefix starting…" ([int](($i * 100) / $total)) $total $installed $failed $rebootPending

                    $installer = $session.CreateUpdateInstaller()
                    $installer.Updates = $coll

                    # Same Begin*-with-sync-fallback pattern as the install worker uses.
                    $r = $null
                    $unJob = $null
                    try { $unJob = $installer.BeginUninstall($null, $null, $null) } catch { }

                    if ($null -ne $unJob) {
                        while (-not $unJob.IsCompleted) {
                            Start-Sleep -Seconds 2
                            $unPct = 0
                            try {
                                $unProgress = $unJob.GetProgress()
                                $unPct = [int]$unProgress.PercentComplete
                            } catch { }
                            $overallPct = [int]((($i * 100) + $unPct) / $total)
                            Write-Progress2 'Installing' ("$unPrefix — $unPct%") $overallPct $total $installed $failed $rebootPending
                        }
                        try { $r = $installer.EndUninstall($unJob) } catch { }
                    }

                    if ($null -eq $r) {
                        Write-Progress2 'Installing' "$unPrefix (sync mode — no live progress)" ([int](($i * 100) / $total)) $total $installed $failed $rebootPending
                        $r = $installer.Uninstall()
                    }
                    if ($r.ResultCode -eq 2) { $installed++ } else { $failed++ }
                    if ($r.RebootRequired) { $rebootPending = $true }
                    } catch {
                        $failed++
                        $overallPct = [int]((($i + 1) * 100) / $total)
                        Write-Progress2 'Installing' ("Update {0} of {1} failed: $($_.Exception.Message)" -f ($i + 1), $total) $overallPct $total $installed $failed $rebootPending
                    }
                }

                $summary = if ($failed -gt 0) { "Uninstalled $installed, $failed failed" } else { "Uninstalled $installed update(s)" }
                if ($rebootPending) {
                    Write-Progress2 'PendingReboot' "$summary, reboot required" 100 $total $installed $failed $true
                    if ($rebootAfter) {
                        Start-Sleep -Seconds 5
                        Restart-Computer -Force
                    }
                } else {
                    Write-Progress2 'Done' $summary 100 $total $installed $failed $false
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

    /// <summary>Reads a boolean PSObject property, falling back to a default when the property is
    /// absent (older scan rows that pre-date a new field).</summary>
    private static bool BoolOr(PSObject row, string name, bool fallback) =>
        row.Properties[name]?.Value is bool b ? b : fallback;

    /// <summary>Reads a DateTime PSObject property, returning null when absent or not a DateTime
    /// (older scan rows or Applicable-scope rows that don't emit a date).</summary>
    private static DateTime? DateTimeOrNull(PSObject row, string name) =>
        row.Properties[name]?.Value is DateTime dt ? dt : null;

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

using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using Vivre.Core.Credentials;
using Vivre.Core.PowerShell;

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
    private readonly TimeSpan _watchdogPollInterval;
    private readonly Func<byte[]> _agentBytes;

    /// <param name="powerShell">The remoting host all transport goes through.</param>
    /// <param name="watchdogPollInterval">How often the liveness watchdog wakes to check for total
    /// silence. Defaults to 15s; overridden only by tests so the watchdog can be exercised without a
    /// real-time wait.</param>
    /// <param name="agentBytesProvider">Supplies the compiled agent EXE bytes to drop on the target.
    /// Defaults to reading <c>Vivre.UpdateAgent.exe</c> from the app directory; overridden only by
    /// tests (which have no bundled agent) so the install/uninstall streaming path can be tested.</param>
    public WuaUpdateLane(
        IPowerShellHost powerShell,
        TimeSpan? watchdogPollInterval = null,
        Func<byte[]>? agentBytesProvider = null)
    {
        _powerShell = powerShell;
        _watchdogPollInterval = watchdogPollInterval ?? TimeSpan.FromSeconds(15);
        _agentBytes = agentBytesProvider ?? ReadAgentBytes;
    }

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
        RunWorkerTaskAsync(host, options, credential, progress, "Install", "Starting update task…", cancellationToken);

    public Task<HostPatchStatus> UninstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken) =>
        RunWorkerTaskAsync(host, options, credential, progress, "Uninstall", "Starting uninstall task…", cancellationToken);

    /// <summary>
    /// Shared implementation for install + uninstall. One persistent PSSession from start to
    /// finish: the bootstrap script registers + starts the SYSTEM task on the target, then
    /// tails the worker's append-only progress log, emitting each new JSON line via
    /// <c>Write-Output</c>. We receive those lines live via
    /// <see cref="PSDataCollection{T}.DataAdded"/>, parse them into <see cref="HostPatchStatus"/>,
    /// and forward to <paramref name="progress"/> as they arrive. Cancellation stops the
    /// pipeline; the server-side <c>finally</c> in the bootstrap unregisters the task and
    /// removes the temp files no matter how we leave.
    /// </summary>
    private async Task<HostPatchStatus> RunWorkerTaskAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        string mode,
        string startingMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        string runId = Guid.NewGuid().ToString("N");
        string taskName = $"Vivre_WUA_{runId}";
        string exePath = $@"C:\Windows\Temp\{taskName}.exe";
        string configPath = $@"C:\Windows\Temp\{taskName}_config.json";
        string progressPath = $@"C:\Windows\Temp\{taskName}_progress.json";

        progress.Report(new HostPatchStatus(PatchPhase.Scanning, startingMessage));

        // The worker is now the compiled Vivre.UpdateAgent.exe (real WUA progress callbacks),
        // not a generated PowerShell script. Ship it + its config to the target as base64 so the
        // bootstrap can drop both with no here-string quoting concerns; the SYSTEM task runs the
        // EXE and it writes the same progress JSONL the streaming controller below tails.
        string base64Exe = Convert.ToBase64String(_agentBytes());
        string base64Config = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(BuildAgentConfigJson(options, progressPath, mode)));
        string bootstrap = BuildBootstrapScript(taskName, exePath, configPath, progressPath, base64Exe, base64Config, options);

        HostPatchStatus last = new(PatchPhase.Scanning, startingMessage);
        bool progressSeen = false;

        // Liveness watchdog. The server-side controller heartbeats every ~15s while the session is
        // alive (even during a slow download/install), so total silence — no progress AND no
        // heartbeat — for NoResponseTimeout means the session is dead/hung (e.g. a silently-dropped
        // connection the SDK never threw on). We cancel + report that, while a genuinely slow update
        // keeps heartbeating and is never flagged. lastLineTicks is bumped on EVERY received line.
        long[] lastLineTicks = [DateTime.UtcNow.Ticks];
        bool watchdogStale = false;
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task watchdog = Task.Run(async () =>
        {
            try
            {
                while (!watchdogCts.IsCancellationRequested)
                {
                    await Task.Delay(_watchdogPollInterval, watchdogCts.Token).ConfigureAwait(false);
                    long idle = DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref lastLineTicks[0]);
                    if (idle > options.NoResponseTimeout.Ticks)
                    {
                        watchdogStale = true;
                        watchdogCts.Cancel(); // stops the streaming pipeline → surfaces as OperationCanceled
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal: the op finished (or was cancelled) and we cancelled the watchdog.
            }
        });

        try
        {
            PSExecutionResult result = await RunStreamingAsync(
                host,
                bootstrap,
                credential,
                onOutput: psObj =>
                {
                    string raw = psObj?.BaseObject?.ToString() ?? psObj?.ToString() ?? string.Empty;
                    if (raw.Length == 0)
                    {
                        return;
                    }

                    // Any line — progress OR heartbeat — proves the session is alive; reset the watchdog.
                    System.Threading.Interlocked.Exchange(ref lastLineTicks[0], DateTime.UtcNow.Ticks);

                    // Heartbeat lines keep the channel visibly alive during long sync-mode
                    // downloads but must NOT change the UI phase (Heartbeat would otherwise
                    // map to Scanning via MapPhase's unknown-phase fallback, regressing the
                    // UI from "Installing 1 of 5 — 50%" back to "Searching…").
                    if (raw.Contains("\"phase\":\"Heartbeat\"", StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (TryParseProgress(raw, out HostPatchStatus parsed))
                    {
                        last = parsed;
                        progressSeen = true;
                        progress.Report(parsed);
                    }
                },
                watchdogCts.Token).ConfigureAwait(false);

            if (!progressSeen)
            {
                // Bootstrap returned without emitting any progress — usually Register-ScheduledTask
                // or Start-ScheduledTask threw before the streaming loop got going. Surface the
                // captured PS error so the user sees the real cause instead of a silent zero.
                string detail = result.Errors.Count > 0
                    ? result.Errors[0]
                    : "Worker task did not emit any progress.";
                var failed = HostPatchStatus.Failed(detail);
                progress.Report(failed);
                return failed;
            }

            return last;
        }
        catch (OperationCanceledException) when (watchdogStale && !cancellationToken.IsCancellationRequested)
        {
            // The watchdog tripped (total silence) — not a user Stop. The session is dead/hung.
            var stale = HostPatchStatus.Failed(
                $"No response from {host} for {options.NoResponseTimeout.TotalSeconds:N0}s — the remote session stopped sending progress and heartbeats (it appears dead or hung). Hit Stop, then re-scan once it's back.");
            progress.Report(stale);
            await SafetyCleanupAsync(host, taskName, exePath, configPath, progressPath, credential).ConfigureAwait(false);
            return stale;
        }
        catch (OperationCanceledException)
        {
            progress.Report(new HostPatchStatus(PatchPhase.Idle, "Cancelled"));
            // Server-side finally usually handles cleanup, but if the pipeline was torn down
            // before that block ran we still want to reap the task + temp files.
            await SafetyCleanupAsync(host, taskName, exePath, configPath, progressPath, credential).ConfigureAwait(false);
            throw;
        }
        catch (RemoteShellInitException ex)
        {
            // The target's WinRM/PSRP shell init is failing (pending reboot / shell exhaustion).
            // The exception message is already actionable ("reboot the target"); surface it as-is.
            var failed = HostPatchStatus.Failed(ex.Message);
            progress.Report(failed);
            await SafetyCleanupAsync(host, taskName, exePath, configPath, progressPath, credential).ConfigureAwait(false);
            return failed;
        }
        catch (RemoteSessionLostException)
        {
            // The remote session died unexpectedly (target rebooted / WinRM dropped) — distinct
            // from a user Stop ("Cancelled") and from a genuine worker error. Don't leak the raw
            // "The pipeline has been stopped." SDK string.
            var lost = HostPatchStatus.Failed(
                $"Lost connection to {host} — the remote session ended; the target may have rebooted or WinRM is unhealthy. Re-scan once it's back.");
            progress.Report(lost);
            await SafetyCleanupAsync(host, taskName, exePath, configPath, progressPath, credential).ConfigureAwait(false);
            return lost;
        }
        catch (Exception ex)
        {
            // Defense-in-depth: name the host so even an untranslated failure never shows a bare,
            // context-free SDK sentence in the update-message column.
            var failed = HostPatchStatus.Failed($"Update failed on {host}: {ex.Message}");
            progress.Report(failed);
            await SafetyCleanupAsync(host, taskName, exePath, configPath, progressPath, credential).ConfigureAwait(false);
            return failed;
        }
        finally
        {
            // Stop + observe the watchdog however we left the try (success, cancel, or fault).
            watchdogCts.Cancel();
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — cancelling the watchdog's Task.Delay.
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

    /// <summary>
    /// Streaming variant of <see cref="RunScriptAsync"/>: each output object reaches
    /// <paramref name="onOutput"/> the moment the script writes it, not at end-of-script.
    /// Local hosts fall back to the non-streaming path and replay output at end (local
    /// install is the rare case; live progress matters most for remote targets).
    /// </summary>
    private async Task<PSExecutionResult> RunStreamingAsync(
        string host,
        string script,
        ConnectionCredential? credential,
        Action<PSObject> onOutput,
        CancellationToken cancellationToken)
    {
        if (IsLocal(host))
        {
            PSExecutionResult result = await _powerShell.RunLocalAsync(script, cancellationToken).ConfigureAwait(false);
            foreach (PSObject row in result.Output)
            {
                onOutput(row);
            }
            return result;
        }

        return await _powerShell.RunRemoteStreamingAsync(
            host,
            script,
            onOutput,
            credential?.ToPowerShellCredential(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Safety net for cleanup when the server-side <c>finally</c> in the bootstrap didn't
    /// get to run (e.g., the WSMan channel was torn down before the worker exited). Best-
    /// effort over a fresh short WinRM call — a leftover task/file is harmless (per-run
    /// name) and a re-scan reflects the true state.
    /// </summary>
    private async Task SafetyCleanupAsync(
        string host,
        string taskName,
        string exePath,
        string configPath,
        string progressPath,
        ConnectionCredential? credential)
    {
        try
        {
            string script = BuildSafetyCleanupScript(taskName, exePath, configPath, progressPath);
            await RunScriptAsync(host, script, credential, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
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

    private static string BuildScanScript(UpdateSource source, bool includeDrivers, UpdateScope scope)
    {
        WuaServerSelection sel = WuaServerSelection.For(source);
        // Default off matches the Windows Update UI and BatchPatch; flip via PatchOptions.IncludeDrivers.
        string typeFilter = includeDrivers ? string.Empty : " and Type='Software'";
        // Applicable → "IsInstalled=0" (default), Installed → "IsInstalled=1" (the uninstall flow).
        string installedFilter = scope == UpdateScope.Installed ? "IsInstalled=1" : "IsInstalled=0";
        // For Applicable, removability isn't meaningful — emit $true so the checklist's checkboxes are
        // enabled for install. For Installed, a row is "removable" only if WUA itself reports
        // IsUninstallable=true — the authoritative "Windows says you can remove this" signal. We do NOT
        // mark a row removable merely because a Package_for_KB exists on disk: modern cumulative/
        // servicing-stack updates always have a package present yet are permanent and refuse removal
        // (0x800F0825), so that test over-promised and let the user tick updates that can never come
        // off. Truly-removable updates (standalone/optional) report IsUninstallable=true. (The agent
        // still tries DISM as a runtime fallback for a ticked update whose WUA uninstall fails.)
        string uninstallableExpr = scope == UpdateScope.Installed
            ? "[bool]$u.IsUninstallable"
            : "$true";
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
        // (Removed: the old DISM/Get-WindowsPackage enumeration that built a "$dismKbs" set of
        // KBs with an installed Package_for_KB. Package existence does NOT mean DISM can remove it —
        // permanent SSU/cumulative packages are present yet refuse removal — so it over-promised
        // removability. Removability is now WUA IsUninstallable alone (see uninstallableExpr), which
        // also drops a slow Get-WindowsPackage call from every Installed scan.)
        string dismBlock = string.Empty;
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
            {{dismBlock}}
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
    /// The settings JSON the on-target <c>Vivre.UpdateAgent.exe</c> reads (its AgentConfig
    /// deserialises this). Keys match AgentConfig's property names. <paramref name="mode"/> is
    /// "Install" or "Uninstall"; <paramref name="progressPath"/> is the target path the agent
    /// appends its progress JSONL to (the same file the streaming controller tails). Public so
    /// the shape stays host-free unit-testable.
    /// </summary>
    public static string BuildAgentConfigJson(PatchOptions options, string progressPath, string mode)
    {
        WuaServerSelection sel = WuaServerSelection.For(options.Source);
        var config = new
        {
            Mode = mode,
            ServerSelection = sel.ServerSelection,
            ServiceId = sel.ServiceId,
            IncludeDrivers = options.IncludeDrivers,
            Excludes = (options.ExcludeNameContains ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .ToArray(),
            IncludeKbs = (options.IncludeKbArticleIds ?? [])
                .Where(kb => !string.IsNullOrWhiteSpace(kb))
                .Select(kb => kb.Trim())
                .ToArray(),
            RebootAfter = options.RebootBehavior == RebootBehavior.RebootAndWait,
            ProgressPath = progressPath,
        };

        return JsonSerializer.Serialize(config);
    }

    /// <summary>
    /// Reads the bundled <c>Vivre.UpdateAgent.exe</c> (shipped next to the app) so the bootstrap
    /// can base64-drop it onto the target. Throws a clear error if it's missing from the build
    /// output rather than silently producing a non-working install.
    /// </summary>
    private static byte[] ReadAgentBytes()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, AgentExeName);
        if (!System.IO.File.Exists(path))
        {
            throw new System.IO.FileNotFoundException(
                $"Update agent '{AgentExeName}' was not found next to the app ({path}). The build/publish must bundle it.",
                path);
        }

        return System.IO.File.ReadAllBytes(path);
    }

    private const string AgentExeName = "Vivre.UpdateAgent.exe";


    /// <summary>
    /// The whole server-side controller in one script: base64-drop the compiled agent EXE +
    /// its config onto the target, register the SYSTEM scheduled task to run the EXE, start it,
    /// then <b>tail the append-only progress log and emit each new line via <c>Write-Output</c></b>.
    /// The client receives those lines live via <see cref="PSDataCollection{T}.DataAdded"/> —
    /// one persistent PSSession from start to finish, no per-poll WinRM shells. Cleanup
    /// (unregister + delete files) runs in a <c>finally</c> so a client cancel still tears the
    /// task down. For <see cref="RunBehavior.ScheduleAt"/> the script registers the task and
    /// returns immediately; the task fires at its trigger and writes the log file, but with no
    /// live stream the user verifies via a later scan (so the EXE + config are left in place).
    /// </summary>
    internal static string BuildBootstrapScript(
        string taskName,
        string exePath,
        string configPath,
        string progressPath,
        string base64Exe,
        string base64Config,
        PatchOptions options)
    {
        string trigger = options is { RunBehavior: RunBehavior.ScheduleAt, ScheduleAt: { } at }
            ? $"$trigger = New-ScheduledTaskTrigger -Once -At '{at.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}'"
            : "$trigger = $null";

        string registerTrigger = options.RunBehavior == RunBehavior.ScheduleAt
            ? "-Trigger $trigger "
            : string.Empty;

        string runNow = options.RunBehavior == RunBehavior.ScheduleAt ? "$false" : "$true";

        return $$"""
            $ErrorActionPreference = 'Stop'
            [System.IO.File]::WriteAllBytes('{{exePath}}', [Convert]::FromBase64String('{{base64Exe}}'))
            [System.IO.File]::WriteAllBytes('{{configPath}}', [Convert]::FromBase64String('{{base64Config}}'))
            Remove-Item '{{progressPath}}' -ErrorAction SilentlyContinue
            {{trigger}}
            $action    = New-ScheduledTaskAction -Execute '{{exePath}}' -Argument '"{{configPath}}"'
            $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -RunLevel Highest
            $settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 6)
            Register-ScheduledTask -TaskName '{{taskName}}' -Action $action -Principal $principal -Settings $settings {{registerTrigger}}-Force | Out-Null

            $runNow = {{runNow}}
            if (-not $runNow) {
                # ScheduleAt: task is registered and will fire at its trigger. There is no live
                # stream to deliver here — emit one terminal-shape line so the client surfaces
                # a status, then exit. No cleanup; the task must survive until it runs.
                $sched = [PSCustomObject]@{
                    phase = 'Done'; message = 'Scheduled — will run at the configured time'
                    percent = 100; available = 0; installed = 0; failed = 0
                    rebootPending = $false; ts = (Get-Date).Ticks
                }
                Write-Output ($sched | ConvertTo-Json -Compress)
                return
            }

            Start-ScheduledTask -TaskName '{{taskName}}'

            # --- Streaming controller -------------------------------------------------
            # Position-tracked tail of the append-only progress log. Read only the new
            # bytes since last iteration (positional FileStream over FileShare.ReadWrite
            # so the worker can keep appending), split on newlines, and emit each line to
            # Write-Output. Loop exits on a terminal phase (Done / Error / PendingReboot).
            try {
                $started = Get-Date
                $lastSeen = Get-Date
                $lastLen = 0L
                $done = $false

                while (-not $done) {
                    Start-Sleep -Milliseconds 500

                    if (Test-Path '{{progressPath}}') {
                        $file = Get-Item '{{progressPath}}'
                        if ($file.Length -gt $lastLen) {
                            $newText = ''
                            try {
                                $fs = [System.IO.File]::Open('{{progressPath}}', 'Open', 'Read', 'ReadWrite')
                                $fs.Seek($lastLen, 'Begin') | Out-Null
                                $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
                                $newText = $sr.ReadToEnd()
                                $lastLen = $fs.Length
                                $sr.Dispose()
                                $fs.Dispose()
                            } catch { }

                            foreach ($line in ($newText -split "`r?`n")) {
                                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                                # Emit the raw JSON line to the client.
                                $line
                                $lastSeen = Get-Date
                                # Check for a terminal phase so we know when to stop tailing.
                                try {
                                    $obj = $line | ConvertFrom-Json
                                    if ($obj.phase -eq 'Done' -or $obj.phase -eq 'Error' -or $obj.phase -eq 'PendingReboot') {
                                        $done = $true
                                        break
                                    }
                                } catch { }
                            }
                        }
                    }

                    # Heartbeat — 15s of silence emits a synthetic line so a quiet stretch (a
                    # long single-update install) never looks identical to a hung channel.
                    if (-not $done -and ((Get-Date) - $lastSeen) -gt [TimeSpan]::FromSeconds(15)) {
                        $hb = [PSCustomObject]@{
                            phase = 'Heartbeat'; message = 'Worker still running…'; percent = $null
                            available = 0; installed = 0; failed = 0; rebootPending = $false
                            ts = (Get-Date).Ticks
                        }
                        Write-Output ($hb | ConvertTo-Json -Compress)
                        $lastSeen = Get-Date
                    }

                    # If the worker never started writing within 2 min, give up — the task
                    # didn't launch (typically a permission or COM-server issue on the host).
                    if (-not (Test-Path '{{progressPath}}') -and ((Get-Date) - $started) -gt [TimeSpan]::FromMinutes(2)) {
                        $err = [PSCustomObject]@{
                            phase = 'Error'; message = 'Worker did not start writing progress within 2 minutes.'
                            percent = $null; available = 0; installed = 0; failed = 0
                            rebootPending = $false; ts = (Get-Date).Ticks
                        }
                        Write-Output ($err | ConvertTo-Json -Compress)
                        $done = $true
                    }
                }
            } finally {
                # Always reap the task + temp files, even on a client cancel (which trips
                # PipelineStoppedException — PowerShell still runs the finally block). Wait for the
                # worker to actually stop first: a running EXE is file-locked, so deleting it while
                # the agent is still alive (e.g. the brief pre-reboot window) silently fails and
                # orphans it. Bounded so we never hang the session.
                $deadline = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $deadline) {
                    $t = Get-ScheduledTask -TaskName '{{taskName}}' -ErrorAction SilentlyContinue
                    if (-not $t -or $t.State -ne 'Running') { break }
                    Start-Sleep -Milliseconds 500
                }
                Unregister-ScheduledTask -TaskName '{{taskName}}' -Confirm:$false -ErrorAction SilentlyContinue
                Remove-Item '{{exePath}}' -Force -ErrorAction SilentlyContinue
                Remove-Item '{{configPath}}' -Force -ErrorAction SilentlyContinue
                Remove-Item '{{progressPath}}' -Force -ErrorAction SilentlyContinue
            }
            """;
    }

    private static string BuildSafetyCleanupScript(string taskName, string exePath, string configPath, string progressPath) =>
        $$"""
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                $t = Get-ScheduledTask -TaskName '{{taskName}}' -ErrorAction SilentlyContinue
                if (-not $t -or $t.State -ne 'Running') { break }
                Start-Sleep -Milliseconds 500
            }
            Unregister-ScheduledTask -TaskName '{{taskName}}' -Confirm:$false -ErrorAction SilentlyContinue
            Remove-Item '{{exePath}}' -Force -ErrorAction SilentlyContinue
            Remove-Item '{{configPath}}' -Force -ErrorAction SilentlyContinue
            Remove-Item '{{progressPath}}' -Force -ErrorAction SilentlyContinue
            """;

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

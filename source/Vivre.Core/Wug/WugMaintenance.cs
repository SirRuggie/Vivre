using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vivre.Core.Wug;

/// <summary>The outcome of a WhatsUp Gold maintenance-mode run.</summary>
/// <param name="Ok">True when maintenance was set on at least one device.</param>
/// <param name="DevicesSet">How many WUG devices were put into / taken out of maintenance.</param>
/// <param name="Unmatched">Machine names that didn't map to a WUG device (by IP).</param>
/// <param name="Error">A human-readable failure reason, or null on success.</param>
public sealed record WugMaintenanceResult(
    bool Ok,
    int DevicesSet,
    IReadOnlyList<string> Unmatched,
    string? Error);

/// <summary>The outcome of a pre-flight connection test to the WhatsUp Gold server.</summary>
/// <param name="ModulePresent">True when the WhatsUpGoldPS module is available.</param>
/// <param name="Connected">True when a test connect+disconnect succeeded.</param>
/// <param name="Error">A human-readable failure reason, or null when fully connected.</param>
public sealed record WugPreflightResult(bool ModulePresent, bool Connected, string? Error);

/// <summary>The read-only WhatsUp Gold maintenance state of a set of machines.</summary>
/// <param name="ByName">
/// Maintenance state keyed by the INPUT machine name (case-insensitive): <c>true</c> = in maintenance,
/// <c>false</c> = definitively not in maintenance, <c>null</c> = the state couldn't be read (unknown —
/// never assumed false).
/// </param>
/// <param name="Unmatched">Input names that didn't map to a WUG device.</param>
/// <param name="Error">A human-readable failure reason, or null when the read succeeded.</param>
public sealed record WugMaintenanceStateResult(
    IReadOnlyDictionary<string, bool?> ByName,   // null = unknown
    IReadOnlyList<string> Unmatched,
    string? Error);

/// <summary>
/// One per-device result streamed from the maintenance-state read as each name resolves.
/// <paramref name="Matched"/> = false means no WUG device matched the input <paramref name="Name"/>;
/// <paramref name="InMaintenance"/> null = unknown (the state couldn't be read — never assumed false).
/// </summary>
public sealed record WugDeviceState(string Name, bool Matched, bool? InMaintenance);

/// <summary>
/// Sets WhatsUp Gold maintenance mode for a set of machines via the <c>WhatsUpGoldPS</c> module — an
/// adaptation of the admin's standalone script, supplied the tab's machine names instead of a file.
///
/// <para><b>Runs under Windows PowerShell 5.1</b> (<c>powershell.exe</c>), NOT Vivre's embedded
/// PowerShell 7: <c>WhatsUpGoldPS</c> is a Windows-PowerShell module and PS7 either can't find it on
/// its own module path or drags it through a Windows-PowerShell compatibility session over local
/// WinRM (which hangs on a flaky box). Shelling out to 5.1 with <c>-NonInteractive</c> matches the
/// proven environment, can never hang on a prompt, and is bounded by a hard timeout. The WUG password
/// is passed via a child-process environment variable — never on the command line, never to disk.</para>
/// </summary>
public static class WugMaintenance
{
    // The on-host run, reading its inputs from environment variables (so the password never appears
    // on a command line). Emits a single JSON result object: { ok, devicesSet, unmatched[], error }.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $result = [ordered]@{ ok = $false; devicesSet = 0; unmatched = @(); error = $null }
        function Emit($r) { $r | ConvertTo-Json -Compress -Depth 4 }
        # Live step markers the host reads off stdout and pushes to the activity log, so a slow run
        # shows what it's doing (and, on timeout, the last step reached) instead of a silent wait.
        function Progress($m) { Write-Output "__WUGP__$m" }

        $names  = @($env:VIVRE_WUG_NAMES -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $server = $env:VIVRE_WUG_SERVER
        $enable = $env:VIVRE_WUG_ENABLE -eq '1'
        $reason = $env:VIVRE_WUG_REASON
        try {
            $sec  = ConvertTo-SecureString $env:VIVRE_WUG_PASS -AsPlainText -Force
            $cred = New-Object System.Management.Automation.PSCredential($env:VIVRE_WUG_USER, $sec)
        } catch {
            $result.error = "Couldn't build the WhatsUp Gold credential: $($_.Exception.Message)."
            Emit $result; return
        }

        # 1. Ensure the WhatsUpGoldPS module: import, else install for the current user, else explain.
        try {
            Progress 'Loading the WhatsUpGoldPS module...'
            if (-not (Get-Module -ListAvailable -Name WhatsUpGoldPS)) {
                # PowerShell Gallery requires TLS 1.2; 5.1 doesn't always negotiate it by default, and
                # without this Install-Module can hang on the handshake until the caller's timeout kills it.
                try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
                try {
                    if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
                        Install-PackageProvider -Name NuGet -Scope CurrentUser -Force -ErrorAction Stop | Out-Null
                    }
                    if ((Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue).InstallationPolicy -ne 'Trusted') {
                        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
                    }
                    Install-Module -Name WhatsUpGoldPS -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop
                } catch {
                    $result.error = "The WhatsUpGoldPS module isn't installed and auto-install failed: $($_.Exception.Message). Install it once: Install-Module WhatsUpGoldPS -Scope CurrentUser (needs PSGallery/internet access)."
                    Emit $result; return
                }
            }
            Import-Module WhatsUpGoldPS -ErrorAction Stop
        } catch {
            $result.error = "Couldn't load the WhatsUpGoldPS module: $($_.Exception.Message)."
            Emit $result; return
        }

        # 2. Connect to the WUG server (HTTPS, ignoring the cert as the standalone script does).
        try {
            Progress "Connecting to WhatsUp Gold at $server..."
            Connect-WUGServer -ServerUri $server -Protocol https -Credential $cred -IgnoreSSLErrors -ErrorAction Stop | Out-Null
        } catch {
            $result.error = "Couldn't connect to WhatsUp Gold at $server`: $($_.Exception.Message). Check the address, that the server is reachable, and the WUG username/password."
            Emit $result; return
        }

        # 3. Resolve each machine to its WUG DeviceId with a targeted SearchValue lookup - one call per
        #    entry, NOT a full inventory pull (which is slow on a large WUG install). SearchValue takes a
        #    hostname or IP; on a miss for a name we DNS-resolve and retry by IP. Mirrors the proven
        #    standalone Set-WUGMaintenanceMode_SearchValue script.
        $deviceIds = @()
        $unmatched = @()
        $total = $names.Count
        $i = 0
        try {
            foreach ($srv in $names) {
                $i++
                Progress "Looking up $srv ($i of $total)..."
                $match = $null

                $results = @(Get-WUGDevice -SearchValue $srv -View overview -ErrorAction SilentlyContinue)
                if ($results.Count -gt 0) {
                    # Prefer an exact hit on name or IP; fall back to the first result.
                    $match = $results | Where-Object { $_.displayName -eq $srv -or $_.networkAddress -eq $srv } | Select-Object -First 1
                    if (-not $match) { $match = $results[0] }
                }
                elseif ($srv -notmatch '^(?:\d{1,3}\.){3}\d{1,3}$') {
                    # A name with no direct hit - resolve to IP and search again by address.
                    try {
                        $ip = [System.Net.Dns]::GetHostAddresses($srv) |
                              Where-Object AddressFamily -eq 'InterNetwork' |
                              Select-Object -First 1 -ExpandProperty IPAddressToString
                    } catch { $ip = $null }
                    if ($ip) {
                        Progress "  $srv resolved to $ip, retrying..."
                        $results2 = @(Get-WUGDevice -SearchValue $ip -View overview -ErrorAction SilentlyContinue)
                        if ($results2.Count -gt 0) {
                            $match = $results2 | Where-Object { $_.networkAddress -eq $ip } | Select-Object -First 1
                            if (-not $match) { $match = $results2[0] }
                        }
                    }
                }

                if ($null -ne $match) { $deviceIds += $match.id } else { $unmatched += $srv }
            }
        } catch {
            $result.error = "Connected, but couldn't search WhatsUp Gold devices: $($_.Exception.Message)."
            Emit $result; return
        }

        $result.unmatched = @($unmatched)

        if ($deviceIds.Count -eq 0) {
            $result.error = "None of the $($names.Count) machine(s) matched a WhatsUp Gold device. Unmatched: $($unmatched -join ', ')."
            Emit $result; return
        }

        # 4. Set maintenance mode.
        try {
            Progress "Setting maintenance for $($deviceIds.Count) device(s)..."
            Set-WUGDeviceMaintenance -DeviceId $deviceIds -Enabled $enable -Reason $reason -ErrorAction Stop | Out-Null
            $result.ok = $true
            $result.devicesSet = $deviceIds.Count
        } catch {
            $result.error = "Mapped $($deviceIds.Count) device(s), but Set-WUGDeviceMaintenance failed: $($_.Exception.Message)."
        }

        Emit $result
        """;

    // Prefix the on-host script puts on each live status line (see the script's Progress helper).
    private const string ProgressMarker = "__WUGP__";

    // Prefix the pre-flight script's Emit puts on its single JSON result line so the host extracts
    // EXACTLY that line regardless of any other stdout a cmdlet prints (see PreflightScript / ParsePreflight).
    private const string PreflightResultMarker = "__WUGRESULT__";

    // Prefix the state read puts on each per-device result line (see StateScript's EmitDevice). Distinct
    // from __WUGP__ (progress → activity log, set path) and __WUGRESULT__ (the final authoritative
    // summary): a device line is one machine's tri-state, streamed as it resolves. Never overload
    // __WUGP__ for it. Internal so tests can reference the exact marker.
    internal const string DeviceMarker = "__WUGDEV__";

    /// <summary>
    /// Stall watchdog for the streaming state read: if no per-device line arrives for this long the run
    /// is declared wedged and killed. 90s ≈ 3× the 30s pre-flight budget for the same launch+import+
    /// connect startup and ~50× a healthy per-device lookup, so it cannot fire on a healthy run but names
    /// a wedge in a minute and a half.
    /// </summary>
    public static readonly TimeSpan StateReadStallTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Absolute backstop for the streaming state read so a pathological child can't live forever. Sized
    /// above a healthy 324-box run at the pilot-checked 5 s/lookup (~28 min) — the stall timer, not this,
    /// is what catches hangs.
    /// </summary>
    public static readonly TimeSpan StateReadCeiling = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Runs the maintenance set under Windows PowerShell 5.1 and returns a typed result. Bounded by
    /// <paramref name="timeout"/> so it can never hang indefinitely. <paramref name="progress"/>, if
    /// supplied, receives a line per step (loading module, connecting, listing devices, setting) so a
    /// slow run can show what it's doing and, on timeout, the last step it reached.
    /// </summary>
    public static Task<WugMaintenanceResult> RunAsync(
        IReadOnlyList<string> names,
        bool enable,
        string server,
        string username,
        string password,
        string reason,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
        => RunCoreAsync(Script, names, enable, server, username, password, reason, timeout, cancellationToken, progress);

    // Internal seam so the cancel-kill contract is testable with a synthetic script — the public RunAsync
    // delegates here with the real Script const; the only difference is the caller-supplied script body.
    internal static async Task<WugMaintenanceResult> RunCoreAsync(
        string script,
        IReadOnlyList<string> names,
        bool enable,
        string server,
        string username,
        string password,
        string reason,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        string psExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(psExe))
        {
            return new WugMaintenanceResult(false, 0, [], $"Windows PowerShell wasn't found at {psExe}.");
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), $"Vivre_Wug_{Guid.NewGuid():N}.ps1");
        await WritePs51ScriptAsync(scriptPath, script, cancellationToken).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo(psExe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Inputs via env vars (the password is never on the command line / in process args).
            psi.Environment["VIVRE_WUG_NAMES"] = string.Join("\n", names);
            psi.Environment["VIVRE_WUG_SERVER"] = server;
            psi.Environment["VIVRE_WUG_ENABLE"] = enable ? "1" : "0";
            psi.Environment["VIVRE_WUG_REASON"] = reason ?? string.Empty;
            psi.Environment["VIVRE_WUG_USER"] = username;
            psi.Environment["VIVRE_WUG_PASS"] = password;
            // Strip the inherited (PS7-contaminated) PSModulePath so this 5.1 child rebuilds its NATIVE
            // WindowsPowerShell module path and finds an already-installed WhatsUpGoldPS directly — without
            // relying on the auto-install fallback below (which needs PSGallery/internet). Same fix as the
            // pre-flight launcher; see RunPreflightProcessAsync for the full rationale. No-op if unset.
            psi.Environment.Remove("PSModulePath");

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                // Live status lines go to the progress callback; everything else (incl. the final
                // JSON result) is buffered for parsing once the process exits.
                if (e.Data.StartsWith(ProgressMarker, StringComparison.Ordinal))
                {
                    progress?.Report(e.Data[ProgressMarker.Length..]);
                }
                else
                {
                    stdout.AppendLine(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

            if (!proc.Start())
            {
                return new WugMaintenanceResult(false, 0, [], "Couldn't start Windows PowerShell (powershell.exe).");
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new WugMaintenanceResult(false, 0, [],
                    $"Timed out after {timeout.TotalSeconds:N0}s. The WhatsUpGoldPS module may be missing/incompatible on this machine, or {server} isn't reachable. Run your standalone script once here to confirm the module + connectivity.");
            }
            catch (OperationCanceledException)
            {
                // A caller cancel must kill the child — before this fix a cancelled set kept running and
                // could still flip WUG maintenance after the UI reported "cancelled". INTENTIONAL BEHAVIOR
                // CHANGE to the shipped set path (operator-approved, Amendment 3).
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw;
            }

            return Parse(stdout.ToString(), stderr.ToString());
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* temp cleanup is best-effort */ }
        }
    }

    // ── Pre-flight: lightweight module-present + connect test ────────────────────────────────────

    // Emits ONE JSON line: { modulePresent, connected, error }.
    // Reads server/user/pass from env vars — password NEVER on the command line.
    private const string PreflightScript = """
        $ErrorActionPreference = 'Stop'
        # Tag the result line with a unique marker so the host extracts EXACTLY it, immune to any
        # banner / warning / object text a cmdlet might print to stdout (see ParsePreflight).
        function Emit($r) { Write-Output ("__WUGRESULT__" + ($r | ConvertTo-Json -Compress -Depth 2)) }

        $server = $env:VIVRE_WUG_SERVER
        $result = [ordered]@{ modulePresent = $false; connected = $false; error = $null }

        # Backstop: if anything terminates unexpectedly OUTSIDE the per-stage try/catch below, still
        # emit a structured result carrying what we already know (modulePresent is set true once the
        # check passes) so a downstream failure is never dropped and re-read by the host as "module
        # missing". Reserves the "install the module" prompt for a genuine missing-module signal.
        trap { $result.error = "Pre-flight error: $($_.Exception.Message)"; Emit $result; exit 0 }

        # 1. Module check
        if (-not (Get-Module -ListAvailable -Name WhatsUpGoldPS)) {
            $result.error = "The WhatsUpGoldPS module isn't installed."
            Emit $result; return
        }
        $result.modulePresent = $true

        # 2. Import
        try {
            Import-Module WhatsUpGoldPS -ErrorAction Stop
        } catch {
            $result.error = "Couldn't load the WhatsUpGoldPS module: $($_.Exception.Message)"
            Emit $result; return
        }

        # 3. Build credential (same pattern as the main script)
        try {
            $sec  = ConvertTo-SecureString $env:VIVRE_WUG_PASS -AsPlainText -Force
            $cred = New-Object System.Management.Automation.PSCredential($env:VIVRE_WUG_USER, $sec)
        } catch {
            $result.error = "Couldn't build the WhatsUp Gold credential: $($_.Exception.Message)"
            Emit $result; return
        }

        # 4. Connect test — identical flags to the real run
        try {
            Connect-WUGServer -ServerUri $server -Protocol https -Credential $cred -IgnoreSSLErrors -ErrorAction Stop | Out-Null
            $result.connected = $true
        } catch {
            $msg = $_.Exception.Message
            if ($msg -match '401|unauthorized|invalid|credential') {
                $result.error = "The WhatsUp Gold username or password was rejected."
            } else {
                $result.error = "Couldn't reach WhatsUp Gold at $server — check the address, that the server is reachable, and the username/password. ($msg)"
            }
        }

        # 5. Best-effort disconnect so the test leaves no session
        if ($result.connected) {
            try { Disconnect-WUGServer -ErrorAction SilentlyContinue | Out-Null } catch { }
        }

        Emit $result
        """;

    // ── Install helper: operator-consented module install ────────────────────────────────────────

    // Emits ONE JSON line: { ok, error }.  No creds or server address needed.
    private const string InstallModuleScript = """
        $ErrorActionPreference = 'Stop'
        function Emit($r) { $r | ConvertTo-Json -Compress -Depth 2 }
        $result = [ordered]@{ ok = $false; error = $null }
        try {
            # PowerShell Gallery requires TLS 1.2; 5.1 doesn't always negotiate it by default.
            try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
            if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
                Install-PackageProvider -Name NuGet -Scope CurrentUser -Force -ErrorAction Stop | Out-Null
            }
            if ((Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue).InstallationPolicy -ne 'Trusted') {
                Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
            }
            Install-Module -Name WhatsUpGoldPS -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop
            $result.ok = $true
        } catch {
            $result.error = $_.Exception.Message
        }
        Emit $result
        """;

    // ── Maintenance-state read: read-only per-machine "in maintenance?" tri-state ─────────────────

    // Emits ONE JSON line: { ok, devices[{ name, inMaintenance }], unmatched[], error }.
    // Reads server/user/pass + names from env vars — password NEVER on the command line. Read-only:
    // it never sets maintenance. No __WUGP__ progress lines (the shared launcher has no progress plumbing).
    private const string StateScript = """
        $ErrorActionPreference = 'Stop'
        # Tag the result line with a unique marker so the host extracts EXACTLY it, immune to any banner /
        # warning / object text a cmdlet might print to stdout (see ParseMaintenanceState).
        function Emit($r) { Write-Output ("__WUGRESULT__" + ($r | ConvertTo-Json -Compress -Depth 4)) }
        # Stream one result line per device AS it resolves, so a long run shows progress and an aborted
        # read keeps what already came back. JSON per line is load-bearing: 5.1's ConvertTo-Json escapes
        # non-ASCII to \uXXXX so the payload is pure ASCII on the wire, immune to the OEM code page of
        # redirected stdout; never switch to a raw delimited format.
        function EmitDevice($e) { Write-Output ("__WUGDEV__" + ($e | ConvertTo-Json -Compress -Depth 3)) }

        $names  = @($env:VIVRE_WUG_NAMES -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $server = $env:VIVRE_WUG_SERVER
        $result = [ordered]@{ ok = $false; devices = @(); unmatched = @(); error = $null }

        # Backstop: any terminating error OUTSIDE the per-stage try/catch still emits a structured result,
        # so a downstream failure is never dropped and misread by the host as a definite state.
        trap { $result.error = "Maintenance-state read error: $($_.Exception.Message)"; Emit $result; exit 0 }

        # 1. Module check — byte-identical signal the dialog string-matches (keep in sync with PreflightScript).
        if (-not (Get-Module -ListAvailable -Name WhatsUpGoldPS)) {
            $result.error = "The WhatsUpGoldPS module isn't installed."
            Emit $result; return
        }

        # 2. Import
        try {
            Import-Module WhatsUpGoldPS -ErrorAction Stop
        } catch {
            $result.error = "Couldn't load the WhatsUpGoldPS module: $($_.Exception.Message)."
            Emit $result; return
        }

        # 3. Build credential (same pattern as the other scripts)
        try {
            $sec  = ConvertTo-SecureString $env:VIVRE_WUG_PASS -AsPlainText -Force
            $cred = New-Object System.Management.Automation.PSCredential($env:VIVRE_WUG_USER, $sec)
        } catch {
            $result.error = "Couldn't build the WhatsUp Gold credential: $($_.Exception.Message)."
            Emit $result; return
        }

        # 4. Connect (HTTPS, ignoring the cert as the standalone script does)
        try {
            Connect-WUGServer -ServerUri $server -Protocol https -Credential $cred -IgnoreSSLErrors -ErrorAction Stop | Out-Null
        } catch {
            $result.error = "Couldn't connect to WhatsUp Gold at $server`: $($_.Exception.Message). Check the address, that the server is reachable, and the WUG username/password."
            Emit $result; return
        }

        # 5. Resolve each machine to its WUG device with a targeted SearchValue lookup - one call per
        #    entry, NOT a full inventory pull. SearchValue takes a hostname or IP; on a name miss we
        #    DNS-resolve and retry by IP. Mirrors the main script's resolution loop.
        $devices = @()
        $unmatched = @()
        try {
            foreach ($srv in $names) {
                $match = $null

                $results = @(Get-WUGDevice -SearchValue $srv -View overview -ErrorAction SilentlyContinue)
                if ($results.Count -gt 0) {
                    # Prefer an exact hit on name or IP; fall back to the first result.
                    $match = $results | Where-Object { $_.displayName -eq $srv -or $_.networkAddress -eq $srv } | Select-Object -First 1
                    if (-not $match) { $match = $results[0] }
                }
                elseif ($srv -notmatch '^(?:\d{1,3}\.){3}\d{1,3}$') {
                    # A name with no direct hit - resolve to IP and search again by address.
                    try {
                        $ip = [System.Net.Dns]::GetHostAddresses($srv) |
                              Where-Object AddressFamily -eq 'InterNetwork' |
                              Select-Object -First 1 -ExpandProperty IPAddressToString
                    } catch { $ip = $null }
                    if ($ip) {
                        $results2 = @(Get-WUGDevice -SearchValue $ip -View overview -ErrorAction SilentlyContinue)
                        if ($results2.Count -gt 0) {
                            $match = $results2 | Where-Object { $_.networkAddress -eq $ip } | Select-Object -First 1
                            if (-not $match) { $match = $results2[0] }
                        }
                    }
                }

                if ($null -ne $match) {
                    # Tri-state maintenance read. Absent state fields => UNKNOWN ($state stays $null).
                    # Presence MUST be tested via PSObject.Properties.Name -contains: on PS 5.1 an ABSENT
                    # property compares -eq 'Maintenance' to $false, silently faking a definite "not in
                    # maintenance". Only a present, non-empty field decides.
                    $state = $null
                    $hasBest  = $match.PSObject.Properties.Name -contains 'bestState'
                    $hasWorst = $match.PSObject.Properties.Name -contains 'worstState'
                    $bestSet  = $hasBest  -and -not [string]::IsNullOrWhiteSpace($match.bestState)
                    $worstSet = $hasWorst -and -not [string]::IsNullOrWhiteSpace($match.worstState)
                    if ($bestSet -or $worstSet) {
                        # PS -eq is case-insensitive (free robustness); any non-Maintenance value (Up, Down,
                        # Warning, ...) correctly reads as not-in-maintenance. The literal is server-version
                        # dependent, so OR the two fields - they normally agree, and OR biases toward "in
                        # maintenance".
                        $state = ($match.bestState -eq 'Maintenance' -or $match.worstState -eq 'Maintenance')
                    }
                    # Entry carries the INPUT name, not the WUG displayName, so the host keys back by what it asked.
                    $devices += [ordered]@{ name = $srv; inMaintenance = $state }
                    EmitDevice ([ordered]@{ name = $srv; matched = $true; inMaintenance = $state })
                } else {
                    $unmatched += $srv
                    EmitDevice ([ordered]@{ name = $srv; matched = $false; inMaintenance = $null })
                }
            }
        } catch {
            $result.error = "Connected, but couldn't search WhatsUp Gold devices: $($_.Exception.Message)."
            Emit $result; return
        }

        # Keep devices an ARRAY even for a single entry - a bare scalar assignment serializes one device
        # as a JSON object, not an array.
        $result.devices = @($devices)
        $result.unmatched = @($unmatched)
        $result.ok = $true
        Emit $result
        """;

    // ── Shared process-launch helper (TestConnectionAsync + InstallModuleAsync + GetMaintenanceStateAsync) ──

    /// <summary>
    /// Writes a PowerShell script to <paramref name="path"/> for Windows PowerShell 5.1 to run.
    /// MUST be UTF-8 WITH BOM: 5.1 reads a BOM-less .ps1 as the system ANSI code page, which corrupts
    /// any non-ASCII character (e.g. the em-dash in our error messages) so the whole script fails to
    /// parse before a line runs and emits nothing. The BOM marks the file UTF-8. Internal so a test
    /// can lock the BOM in (a missing BOM is invisible to the build and broke the WUG pre-flight once).
    /// </summary>
    internal static Task WritePs51ScriptAsync(string path, string script, CancellationToken ct)
        => File.WriteAllTextAsync(path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);

    /// <summary>
    /// Writes <paramref name="script"/> to a temp .ps1, launches it under Windows PowerShell 5.1
    /// with the supplied environment variables, waits up to <paramref name="timeout"/> (the absolute
    /// ceiling), kills on timeout, cleans up the temp file.  Returns (stdout, stderr).
    /// <para><paramref name="onDeviceLine"/>, when set, receives each <c>__WUGDEV__</c> line (marker
    /// stripped) as it streams; those lines are routed OUT of the returned stdout buffer. It is invoked
    /// on a ThreadPool async-read thread — callers own any marshalling.</para>
    /// <para><paramref name="stallTimeout"/>, when set, arms a watchdog that kills the run (throwing
    /// <see cref="WugStallException"/>) if no device line arrives for that long — a wedge detector far
    /// tighter than the ceiling. Chatter on other stdout does NOT reset it.</para>
    /// </summary>
    internal static async Task<(string Stdout, string Stderr)> RunPreflightProcessAsync(
        string script,
        IReadOnlyDictionary<string, string> env,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string>? onDeviceLine = null,
        TimeSpan? stallTimeout = null)
    {
        string psExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(psExe))
        {
            throw new InvalidOperationException($"Windows PowerShell wasn't found at {psExe}.");
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), $"Vivre_WugPre_{Guid.NewGuid():N}.ps1");
        await WritePs51ScriptAsync(scriptPath, script, ct).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo(psExe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var kv in env)
            {
                psi.Environment[kv.Key] = kv.Value;
            }

            // CRITICAL (the false "module not installed" fix): strip the inherited PSModulePath so the
            // shelled-out Windows PowerShell 5.1 rebuilds its NATIVE module path — the CurrentUser + AllUsers
            // WindowsPowerShell\Modules folders where WhatsUpGoldPS actually lives — exactly like a plain 5.1
            // shell (which finds the module fine). Vivre hosts an IN-PROCESS PowerShell 7 runspace whose
            // initialization rewrites THIS process's PSModulePath to PS7's module paths; a child launched
            // with UseShellExecute=false inherits that, so the 5.1 child would otherwise look only at the
            // PS7 module folders and falsely report the module missing. Remove is a harmless no-op if unset.
            psi.Environment.Remove("PSModulePath");

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            // Absolute ceiling: linked to the caller token, fires after `timeout` no matter what.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Stall watchdog (opt-in): a second token CHAINED under the ceiling that each device line
            // pushes forward. Chaining keeps the ceiling firing even while the stall timer keeps resetting.
            using var waitCts = stallTimeout is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token)
                : null;

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                // Device lines are routed OUT of the summary buffer entirely — even when onDeviceLine is
                // null — so the summary parse can never mistake a per-device line for the result line (the
                // fabricated-clean-read false-green). Every other non-null line buffers as before.
                if (e.Data.StartsWith(DeviceMarker, StringComparison.Ordinal))
                {
                    onDeviceLine?.Invoke(e.Data[DeviceMarker.Length..]);
                    // The stall timer resets ONLY on a device line — arriving chatter on other stdout must
                    // never keep a dead run alive. Late lines can fire during teardown, so guard the
                    // already-disposed timer.
                    if (stallTimeout is { } stall && waitCts is not null)
                    {
                        try { waitCts.CancelAfter(stall); } catch (ObjectDisposedException) { }
                    }
                }
                else
                {
                    stdout.AppendLine(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

            if (!proc.Start())
            {
                throw new InvalidOperationException("Couldn't start Windows PowerShell (powershell.exe).");
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            timeoutCts.CancelAfter(timeout);
            if (stallTimeout is { } initialStall)
            {
                waitCts!.CancelAfter(initialStall);
            }

            CancellationToken waitToken = waitCts?.Token ?? timeoutCts.Token;
            try
            {
                await proc.WaitForExitAsync(waitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Decode WHY the wait ended, kill the child in every case, and throw the matching signal.
                if (ct.IsCancellationRequested)
                {
                    // Operator cancel must not leave the child running — a BEHAVIOR CHANGE from the old
                    // launcher, where a caller cancel abandoned a live child. Kill, then honour the cancel.
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    throw;
                }

                if (timeoutCts.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    throw new TimeoutException($"Timed out after {timeout.TotalSeconds:N0}s.");
                }

                // Only the stall timer fired: the run went quiet without hitting the ceiling.
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new WugStallException(stallTimeout!.Value);
            }

            return (stdout.ToString(), stderr.ToString());
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* temp cleanup is best-effort */ }
        }
    }

    // ── Public preflight API ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tests that the WhatsUpGoldPS module is present and that the supplied credentials can reach
    /// the WUG server, without touching any managed device.  Bounded by <paramref name="timeout"/>.
    /// The <paramref name="password"/> is passed to the child process via <c>VIVRE_WUG_PASS</c>
    /// environment variable only — never on the command line, never written to disk.
    /// </summary>
    public static async Task<WugPreflightResult> TestConnectionAsync(
        string server,
        string username,
        string password,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VIVRE_WUG_SERVER"] = server,
            ["VIVRE_WUG_USER"]   = username,
            ["VIVRE_WUG_PASS"]   = password,   // password reaches the child via env var ONLY
        };

        try
        {
            var (stdout, stderr) = await RunPreflightProcessAsync(PreflightScript, env, timeout, ct).ConfigureAwait(false);
            return ParsePreflight(stdout, stderr);
        }
        catch (Exception ex)
        {
            // A timeout or launch failure says nothing about whether the module is present — never
            // render it as "module not installed" (which would wrongly prompt to reinstall a present
            // module). Report it as a connection-stage failure carrying the real reason.
            return new WugPreflightResult(true, false, ex.Message);
        }
    }

    /// <summary>
    /// Installs the WhatsUpGoldPS module from the PowerShell Gallery for the current user.
    /// This is operator-consented; the silent auto-install inside <see cref="RunAsync"/> is separate.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> InstallModuleAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        try
        {
            var (stdout, stderr) = await RunPreflightProcessAsync(
                InstallModuleScript,
                new Dictionary<string, string>(0, StringComparer.Ordinal),
                timeout,
                ct).ConfigureAwait(false);

            // Parse { ok, error }
            string? json = stdout
                .Split('\n')
                .Select(s => s.Trim())
                .LastOrDefault(s => s.Contains('{') && s.Contains('}'));

            if (json is null)
            {
                string detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "No output from installer.";
                return (false, detail);
            }

            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            using JsonDocument doc = JsonDocument.Parse(json[start..(end + 1)]);
            JsonElement root = doc.RootElement;

            bool ok = root.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
            string? error = root.TryGetProperty("error", out JsonElement eEl) && eEl.ValueKind == JsonValueKind.String
                ? eEl.GetString()
                : null;
            return (ok, error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Reads the current WhatsUp Gold maintenance state for <paramref name="names"/> WITHOUT changing
    /// anything, keyed back by the input machine name. Bounded by <paramref name="timeout"/> (the
    /// ceiling); when <paramref name="stallTimeout"/> is set, a quiet run with no new per-device line for
    /// that long is aborted early. <paramref name="deviceProgress"/>, if set, receives each device result
    /// as it streams. The <paramref name="password"/> reaches the child process via the
    /// <c>VIVRE_WUG_PASS</c> environment variable only — never on the command line, never written to disk.
    /// <para>FAILS OPEN: a launch / timeout / stall / parse failure never fabricates a "not in
    /// maintenance". An aborted read now KEEPS the per-device results already streamed and names the last
    /// machine that resolved in the error, rather than discarding everything.</para>
    /// </summary>
    public static async Task<WugMaintenanceStateResult> GetMaintenanceStateAsync(
        IReadOnlyList<string> names,
        string server,
        string username,
        string password,
        TimeSpan timeout,
        CancellationToken ct = default,
        IProgress<WugDeviceState>? deviceProgress = null,
        TimeSpan? stallTimeout = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VIVRE_WUG_NAMES"]  = string.Join("\n", names),
            ["VIVRE_WUG_SERVER"] = server,
            ["VIVRE_WUG_USER"]   = username,
            ["VIVRE_WUG_PASS"]   = password,   // password reaches the child via env var ONLY
        };

        // Partial state assembled from the streamed per-device lines so an aborted read (stall / ceiling /
        // crash) still returns what already came back instead of discarding it. OnDeviceLine fires on a
        // ThreadPool async-read thread, so every touch of these is under `gate`. lastName/seen name the
        // machine an abort stopped after, for the error text.
        var gate = new object();
        var partial = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var partialUnmatched = new List<string>();
        string? lastName = null;
        int seen = 0;

        // Attached ALWAYS (independent of deviceProgress): the partial map is what lets an aborted read
        // keep its results. ParseDeviceLine is the SAME tri-state contract as AddDevice — never a divergent
        // per-line parser, the likeliest re-entry point for the fabricated "not in maintenance" bug.
        void OnDeviceLine(string json)
        {
            WugDeviceState? parsed = ParseDeviceLine(json);
            if (parsed is null)
            {
                return;
            }

            lock (gate)
            {
                if (parsed.Matched)
                {
                    partial[parsed.Name] = parsed.InMaintenance;
                }
                else
                {
                    partialUnmatched.Add(parsed.Name);
                }
                lastName = parsed.Name;
                seen++;
            }

            deviceProgress?.Report(parsed);
        }

        try
        {
            var (stdout, stderr) = await RunPreflightProcessAsync(
                StateScript, env, timeout, ct, OnDeviceLine, stallTimeout).ConfigureAwait(false);
            // Normal exit: the __WUGRESULT__ summary is authoritative — the partial map is NOT consulted.
            return ParseMaintenanceState(stdout, stderr);
        }
        catch (WugStallException ex)
        {
            // Stalled: KEEP the partial results, name the last machine that resolved. Never discard.
            // The launcher has killed the child, but stragglers from its draining async output pump can
            // still fire OnDeviceLine and write the live collections AFTER we return — so the result must
            // wrap snapshot COPIES taken under `gate`, never the live maps (a cross-thread write-during-read
            // on a Dictionary is this codebase's cardinal crash class).
            Dictionary<string, bool?> snap; List<string> snapUnmatched; string? ln; int sn;
            lock (gate)
            {
                snap = new Dictionary<string, bool?>(partial, StringComparer.OrdinalIgnoreCase);
                snapUnmatched = new List<string>(partialUnmatched);
                ln = lastName; sn = seen;
            }
            return new WugMaintenanceStateResult(
                snap, snapUnmatched, ComposeAbortError("Stalled", ln, sn, names.Count, ex.Stall));
        }
        catch (TimeoutException)
        {
            // Hit the absolute ceiling: same shape, ceiling wording and window. Snapshot under `gate` for
            // the same reason as above — the killed child's draining pump can still write the live maps.
            Dictionary<string, bool?> snap; List<string> snapUnmatched; string? ln; int sn;
            lock (gate)
            {
                snap = new Dictionary<string, bool?>(partial, StringComparer.OrdinalIgnoreCase);
                snapUnmatched = new List<string>(partialUnmatched);
                ln = lastName; sn = seen;
            }
            return new WugMaintenanceStateResult(
                snap, snapUnmatched, ComposeAbortError("Timed out", ln, sn, names.Count, timeout));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Callers own cancellation semantics; the launcher already killed the child. Rethrow.
            throw;
        }
        catch (Exception ex)
        {
            // Any other failure (launch / unexpected): return the partial map — never a fresh empty one —
            // plus the real reason. Still fails open, never a fabricated "not in maintenance". Snapshot
            // under `gate` for the same reason as above — the killed child's draining pump can still write
            // the live maps.
            Dictionary<string, bool?> snap; List<string> snapUnmatched;
            lock (gate)
            {
                snap = new Dictionary<string, bool?>(partial, StringComparer.OrdinalIgnoreCase);
                snapUnmatched = new List<string>(partialUnmatched);
            }
            return new WugMaintenanceStateResult(snap, snapUnmatched, ex.Message);
        }
    }

    /// <summary>
    /// Composes the error text for an aborted streaming state read, naming the last machine that resolved
    /// and how many of <paramref name="total"/> were checked before the abort. <paramref name="reason"/>
    /// is "Stalled" or "Timed out"; <paramref name="window"/> is the stall or ceiling window that fired.
    /// </summary>
    internal static string ComposeAbortError(string reason, string? lastName, int seen, int total, TimeSpan window)
        => seen > 0
            ? $"{reason} after {lastName} — {seen} of {total} checked (no result for {window.TotalSeconds:N0}s)"
            : $"{reason} before the first result — 0 of {total} checked (no result for {window.TotalSeconds:N0}s)";

    /// <summary>The activity-log line for an operator-stopped/superseded state check — an aborted run
    /// must never be indistinguishable from a completed one.</summary>
    public static string ComposeStoppedMessage(int seen, int total) => $"Stopped — {seen} of {total} checked";

    // ── Parse helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the single-line JSON the pre-flight script emits.  On no JSON or malformed output,
    /// returns a failed result with <paramref name="stderr"/> as the error.
    /// </summary>
    internal static WugPreflightResult ParsePreflight(string stdout, string stderr)
    {
        // Prefer the marker-tagged result line (Emit prefixes __WUGRESULT__); fall back to the last
        // braced line for safety / marker-less input. The marker makes extraction immune to any other
        // stdout a cmdlet might print before the result.
        string[] lines = stdout.Split('\n').Select(s => s.Trim()).ToArray();
        string? marked = lines.LastOrDefault(s => s.StartsWith(PreflightResultMarker, StringComparison.Ordinal));
        string? json = marked is not null
            ? marked[PreflightResultMarker.Length..]
            : lines.LastOrDefault(s => s.Contains('{') && s.Contains('}'));

        // No result line at all (the process was killed on timeout before emitting, or produced no
        // output). This is NOT evidence the module is missing — report it as a connection-stage failure
        // carrying the real detail, never as "module not installed" (which would wrongly prompt to
        // reinstall a module that is actually present).
        if (json is null)
        {
            string detail = !string.IsNullOrWhiteSpace(stderr)
                ? stderr.Trim()
                : "Windows PowerShell returned no result (the pre-flight may have timed out).";
            return new WugPreflightResult(ModulePresent: true, Connected: false, Error: detail);
        }

        try
        {
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            using JsonDocument doc = JsonDocument.Parse(json[start..(end + 1)]);
            JsonElement root = doc.RootElement;

            // Treat the module as present UNLESS the script explicitly reported it missing. The canned
            // "module isn't installed" prompt must fire only on that definite signal — a parse oddity or
            // a connect/credential failure has to surface the real error, not a reinstall prompt.
            bool modulePresent = !(root.TryGetProperty("modulePresent", out JsonElement mpEl) && mpEl.ValueKind == JsonValueKind.False);
            bool connected     = root.TryGetProperty("connected", out JsonElement cnEl) && cnEl.ValueKind == JsonValueKind.True;
            string? error      = root.TryGetProperty("error", out JsonElement eEl) && eEl.ValueKind == JsonValueKind.String
                ? eEl.GetString()
                : null;

            return new WugPreflightResult(modulePresent, connected, error);
        }
        catch (JsonException)
        {
            string detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "Couldn't parse the pre-flight result: " + json;
            return new WugPreflightResult(ModulePresent: true, Connected: false, Error: detail);
        }
    }

    /// <summary>
    /// Parses the single-line JSON the maintenance-state script emits into a per-machine tri-state map.
    /// FAILS OPEN: on no result line or malformed JSON, returns an empty (case-insensitive) map plus the
    /// stderr detail as the error — never a fabricated "not in maintenance". Never throws.
    /// </summary>
    internal static WugMaintenanceStateResult ParseMaintenanceState(string stdout, string stderr)
    {
        var byName = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        // REQUIRE the __WUGRESULT__ marker — there is NO last-braced-line fallback here (there is one in
        // ParsePreflight, which has no streamed lines). With per-device __WUGDEV__ JSON lines now on the
        // wire, a braced-line fallback could parse a trailing device line AS the summary (no devices[],
        // no error → a fabricated clean-but-empty read), the exact quiet false-green this feature must
        // never produce. No marker → the fail-open no-result path below.
        string[] lines = stdout.Split('\n').Select(s => s.Trim()).ToArray();
        string? marked = lines.LastOrDefault(s => s.StartsWith(PreflightResultMarker, StringComparison.Ordinal));
        string? json = marked is not null ? marked[PreflightResultMarker.Length..] : null;

        // No result line (killed on timeout before emitting, or no output). This is NOT a machine state —
        // surface it as unknown-with-error, never as "not in maintenance".
        if (json is null)
        {
            string detail = !string.IsNullOrWhiteSpace(stderr)
                ? stderr.Trim()
                : "Windows PowerShell returned no result (the maintenance-state read may have timed out).";
            return new WugMaintenanceStateResult(byName, [], detail);
        }

        try
        {
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start < 0 || end < start)
            {
                // A marker line with no JSON body — fail open, never throw.
                string bad = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "Couldn't parse the maintenance-state result: " + json;
                return new WugMaintenanceStateResult(byName, [], bad);
            }

            using JsonDocument doc = JsonDocument.Parse(json[start..(end + 1)]);
            JsonElement root = doc.RootElement;

            // devices is normally an array; a one-device result serializes as a single object.
            if (root.TryGetProperty("devices", out JsonElement devicesEl))
            {
                if (devicesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement dev in devicesEl.EnumerateArray())
                    {
                        AddDevice(byName, dev);
                    }
                }
                else if (devicesEl.ValueKind == JsonValueKind.Object)
                {
                    AddDevice(byName, devicesEl);
                }
            }

            var unmatched = new List<string>();
            if (root.TryGetProperty("unmatched", out JsonElement uEl))
            {
                if (uEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in uEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            unmatched.Add(item.GetString()!);
                        }
                    }
                }
                else if (uEl.ValueKind == JsonValueKind.String)
                {
                    unmatched.Add(uEl.GetString()!);
                }
            }

            string? error = root.TryGetProperty("error", out JsonElement eEl) && eEl.ValueKind == JsonValueKind.String
                ? eEl.GetString()
                : null;

            return new WugMaintenanceStateResult(byName, unmatched, error);
        }
        catch (JsonException)
        {
            string detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "Couldn't parse the maintenance-state result: " + json;
            return new WugMaintenanceStateResult(byName, [], detail);
        }
    }

    // Adds one device entry to the tri-state map: the name must be a string (skip the entry otherwise);
    // inMaintenance True/False map to true/false, and anything else (absent, null, string, number) to
    // null — unknown, never assumed false.
    private static void AddDevice(Dictionary<string, bool?> byName, JsonElement dev)
    {
        if (dev.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!dev.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            return;
        }

        bool? state = dev.TryGetProperty("inMaintenance", out JsonElement mEl)
            ? mEl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?)null,
            }
            : null;

        byName[nameEl.GetString()!] = state;
    }

    /// <summary>
    /// Parses ONE marker-stripped <c>__WUGDEV__</c> payload into a <see cref="WugDeviceState"/>, or null
    /// when the line carries no usable name. Never throws (a <see cref="JsonException"/> yields null).
    /// </summary>
    internal static WugDeviceState? ParseDeviceLine(string json)
    {
        // A divergent per-line parser is the most likely re-entry point for the fabricated "not in
        // maintenance" bug — keep it in LOCKSTEP with AddDevice: name must be a JSON string; matched is
        // false ONLY on an explicit JSON false (absent/true/other => matched, so a miss is always explicit);
        // inMaintenance is true/false ONLY on JSON true/false, and absent/null/other => unknown, never false.
        try
        {
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start < 0 || end < start)
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(json[start..(end + 1)]);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            bool matched = !(root.TryGetProperty("matched", out JsonElement matchedEl) && matchedEl.ValueKind == JsonValueKind.False);

            bool? inMaintenance = root.TryGetProperty("inMaintenance", out JsonElement mEl)
                ? mEl.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => (bool?)null,
                }
                : null;

            return new WugDeviceState(nameEl.GetString()!, matched, inMaintenance);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses the single JSON result line the script emits; falls back to stderr on failure.</summary>
    internal static WugMaintenanceResult Parse(string stdout, string stderr)
    {
        string? json = stdout
            .Split('\n')
            .Select(s => s.Trim())
            .LastOrDefault(s => s.Contains('{') && s.Contains('}'));

        if (json is null)
        {
            string detail = !string.IsNullOrWhiteSpace(stderr)
                ? stderr.Trim()
                : "Windows PowerShell returned no result.";
            return new WugMaintenanceResult(false, 0, [], $"No result from WhatsUp Gold — {detail}");
        }

        try
        {
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            using JsonDocument doc = JsonDocument.Parse(json[start..(end + 1)]);
            JsonElement root = doc.RootElement;

            bool ok = root.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
            int set = root.TryGetProperty("devicesSet", out JsonElement dEl) && dEl.TryGetInt32(out int d) ? d : 0;
            string? error = root.TryGetProperty("error", out JsonElement eEl) && eEl.ValueKind == JsonValueKind.String
                ? eEl.GetString()
                : null;

            var unmatched = new List<string>();
            if (root.TryGetProperty("unmatched", out JsonElement uEl))
            {
                if (uEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in uEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            unmatched.Add(item.GetString()!);
                        }
                    }
                }
                else if (uEl.ValueKind == JsonValueKind.String)
                {
                    unmatched.Add(uEl.GetString()!);
                }
            }

            return new WugMaintenanceResult(ok, set, unmatched, error);
        }
        catch (JsonException)
        {
            return new WugMaintenanceResult(false, 0, [], "Couldn't parse the WhatsUp Gold result: " + json);
        }
    }
}

/// <summary>
/// Thrown when the streaming state read goes quiet for the stall window without hitting the absolute
/// ceiling — a wedged run detected far sooner than the ceiling would. Derives from
/// <see cref="TimeoutException"/> so an existing catch still treats it as a timeout; <see cref="Stall"/>
/// carries the window that fired for the abort-error text.
/// </summary>
internal sealed class WugStallException(TimeSpan stall)
    : TimeoutException($"No result for {stall.TotalSeconds:N0}s")
{
    public TimeSpan Stall { get; } = stall;
}

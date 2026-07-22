using System.Diagnostics;
using System.Globalization;
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

/// <summary>
/// The optional manual-maintenance detail for one in-maintenance machine: the free-text reason, the WUG
/// user who set it, and the UTC entry time (<c>lastChangeUtc</c> — while a device sits in maintenance
/// nothing polls, so its last state change IS the moment it entered; operator-verified against the
/// timeline's "Manual Maintenance - Start" row to the second). Every field is independently optional —
/// absent means the detail read didn't return it, NEVER a statement about the state itself.
/// </summary>
public sealed record WugMaintenanceDetail(string? Reason, string? User, string? SinceUtc);

/// <summary>The read-only WhatsUp Gold maintenance state of a set of machines.</summary>
/// <param name="ByName">
/// Maintenance state keyed by the INPUT machine name (case-insensitive): <c>true</c> = in maintenance,
/// <c>false</c> = definitively not in maintenance, <c>null</c> = the state couldn't be read (unknown —
/// never assumed false).
/// </param>
/// <param name="Unmatched">Input names that didn't map to a WUG device.</param>
/// <param name="Error">A human-readable failure reason, or null when the read succeeded.</param>
/// <param name="LookupErrors">How many names hit a WUG search error (state unknown, NOT "no device").</param>
/// <param name="Ambiguous">How many names had hits but no confident exact match (unknown, never guessed).</param>
/// <param name="MatchedByIp">How many names resolved only via the DNS→IP fall-through.</param>
/// <param name="DetailsByName">
/// Manual-maintenance detail (reason / who / since) keyed by INPUT name, present only for in-maintenance
/// machines whose detail read returned something. Null / missing entries are a DISPLAY downgrade only
/// (row shows plain "in maintenance") — never a state signal.
/// </param>
public sealed record WugMaintenanceStateResult(
    IReadOnlyDictionary<string, bool?> ByName,   // null = unknown
    IReadOnlyList<string> Unmatched,
    string? Error,
    int LookupErrors = 0,
    int Ambiguous = 0,
    int MatchedByIp = 0,
    IReadOnlyDictionary<string, WugMaintenanceDetail>? DetailsByName = null);

/// <summary>
/// One per-device result streamed from the maintenance-state read as each name resolves.
/// <paramref name="Matched"/> means the row shows a state or "unknown" (true) rather than
/// "no matching device" (false) — a lookup error or an ambiguous name is <c>Matched=true</c> with
/// <c>InMaintenance=null</c> (state unknown), NOT a false "no matching device".
/// <paramref name="InMaintenance"/> null = unknown (the state couldn't be read — never assumed false).
/// <paramref name="MatchedByIp"/> = true when the name resolved only via the DNS→IP fall-through.
/// <paramref name="Reason"/>/<paramref name="User"/>/<paramref name="SinceUtc"/> are the optional
/// manual-maintenance detail (see <see cref="WugMaintenanceDetail"/>) — populated only for
/// in-maintenance rows whose detail read succeeded; absent detail never changes the state.
/// </summary>
public sealed record WugDeviceState(
    string Name,
    bool Matched,
    bool? InMaintenance,
    bool MatchedByIp = false,
    string? Reason = null,
    string? User = null,
    string? SinceUtc = null);

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
    // ── Shared per-name resolver (SINGLE-SOURCED into BOTH Script and StateScript) ─────────────────
    //
    // The one place that turns an input machine name into a WUG device, so the set path and the state
    // read can NEVER diverge on how a name is matched. Defines three functions and RUNS nothing (safe to
    // splice anywhere before Import-Module; it depends on Get-WUGDevice only at call time):
    //
    //   Resolve-WugDnsAddress  — first IPv4 for a name, or $null. OVERRIDABLE: a test can redefine it
    //                            after this block to return a canned IP / $null (last definition wins).
    //   Test-WugNameMatch      — normalized dot-boundary name match (the replacement for the dead
    //                            `$_.displayName -eq $srv` verify, which is null for FQDN-registered fleets).
    //   Resolve-WugName        — the control flow: error-aware name search → exact name match → DNS→IP
    //                            fall-through → EXACT-IP-count classify (1 => MatchedByIp, 0 => NoDevice,
    //                            2+ => Ambiguous — SearchValue is substring, so 0-exact never counts as a
    //                            hit), with honest outcomes and NO silent [0].
    //
    // Outcome is one of: MatchedByName | MatchedByIp | NoDevice | Ambiguous | LookupError.
    internal const string ResolveFunctionScript = """
        # First IPv4 address for $name, or $null. Wrapped in a function so tests can stub it.
        function Resolve-WugDnsAddress {
            param($name)
            try {
                return [System.Net.Dns]::GetHostAddresses($name) |
                       Where-Object AddressFamily -eq 'InterNetwork' |
                       Select-Object -First 1 -ExpandProperty IPAddressToString
            } catch { return $null }
        }

        # Normalized, case-insensitive, DOT-BOUNDARY name match. Replaces the dead exact-verify: WUG
        # registers devices FQDN ($_.name = "APVHOP.EMPLOYEES.ROOT.local"), $_.hostName is sometimes bare
        # ("APVWUG") sometimes FQDN, and $_.displayName is often ABSENT — so the old `displayName -eq $srv`
        # matched nothing and $results[0] was the de-facto pick. Compares $query against name/hostName/
        # displayName (each PRESENCE-guarded — a missing property must never satisfy OR throw) plus a
        # networkAddress equality clause for IP-literal inputs. Dot boundary rejects prefix collisions:
        # "APVSQL1" must NOT match "APVSQL10.domain". [string]::StartsWith (NOT -like) avoids wildcard injection.
        function Test-WugNameMatch {
            param($query, $device)
            $q = ([string]$query).TrimEnd('.')
            foreach ($prop in 'name','hostName','displayName') {
                if ($device.PSObject.Properties.Name -contains $prop) {
                    $val = $device.$prop
                    if (-not [string]::IsNullOrWhiteSpace($val)) {
                        $stored = ([string]$val).TrimEnd('.')
                        if ([string]::Equals($stored, $q, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                        # stored startswith query+"." : bare input vs FQDN store  ("APVHOP" ~ "APVHOP.EMPLOYEES.ROOT.local")
                        if ($stored.StartsWith($q + '.', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                        # query startswith stored+"." : FQDN input vs bare store
                        if ($q.StartsWith($stored + '.', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    }
                }
            }
            # Fourth clause: an IP-literal input equals the device's networkAddress (presence-guarded).
            if ($device.PSObject.Properties.Name -contains 'networkAddress') {
                if ($device.networkAddress -eq $query) { return $true }
            }
            return $false
        }

        # Resolve ONE input name to a WUG device, honestly. Returns [ordered]@{ outcome; device; viaIp; hits; error }.
        # Control flow (a currently-working lookup still resolves via name-exact OR ip-exact):
        #   1. Name search (error-aware). A CAPTURED ERROR => LookupError immediately (never a false no-match,
        #      never a second call to a struggling server).
        #   2. Clean hits with a normalized-exact match => MatchedByName (first exact if several).
        #   3. Clean (hits-no-exact OR empty) and NOT an IP literal => DNS→IP => IP search (error-aware) =>
        #      classify by the COUNT of EXACT networkAddress matches (SearchValue is a SUBSTRING search, so
        #      .10 also returns .101/.109): exactly 1 exact => MatchedByIp; 0 exact => substring-only rows
        #      are NOT hits, fall through (=> NoDevice unless the NAME search saw hits); 2+ exact => real
        #      shared-IP Ambiguous. NO $results2[0] fallback.
        #   4. Saw hits anywhere but pinned nothing => Ambiguous (unknown, never [0]).
        #      Nothing seen anywhere => NoDevice (the ONLY honest "no matching device").
        function Resolve-WugName {
            param($query)
            $out = [ordered]@{ outcome = 'NoDevice'; device = $null; viaIp = $false; hits = 0; error = $null }
            $sawHits = $false

            # 1. Name search. -ErrorVariable (no '+' => reset per call) captures a search error even under
            #    -ErrorAction SilentlyContinue; an errored search is NOT a clean no-match.
            $nameErr = $null
            $results = @(Get-WUGDevice -SearchValue $query -View overview -ErrorAction SilentlyContinue -ErrorVariable nameErr)
            if ($nameErr -and $nameErr.Count -gt 0) {
                $out.outcome = 'LookupError'; $out.error = "$($nameErr[0])"; return $out
            }
            $out.hits = $results.Count
            if ($results.Count -gt 0) {
                $sawHits = $true
                $exact = $results | Where-Object { Test-WugNameMatch $query $_ } | Select-Object -First 1
                if ($exact) { $out.outcome = 'MatchedByName'; $out.device = $exact; return $out }
            }

            # 3. IP fall-through from BOTH the empty and the hits-no-exact branches (the renamed-box rescue) —
            #    unless the input is itself an IP literal (nothing to resolve).
            if ($query -notmatch '^(?:\d{1,3}\.){3}\d{1,3}$') {
                $ip = Resolve-WugDnsAddress $query
                if ($ip) {
                    $ipErr = $null
                    $results2 = @(Get-WUGDevice -SearchValue $ip -View overview -ErrorAction SilentlyContinue -ErrorVariable ipErr)
                    if ($ipErr -and $ipErr.Count -gt 0) {
                        $out.outcome = 'LookupError'; $out.error = "$($ipErr[0])"; return $out
                    }
                    if ($results2.Count -gt 0) {
                        # Classify by the COUNT of EXACT networkAddress matches. WUG's SearchValue is a
                        # SUBSTRING search (.10 also returns .101/.109), so substring-only rows are NOT
                        # evidence this box exists in WUG and must not count as hits: 0 exact falls through
                        # to an honest NoDevice (unless the NAME search already saw hits). Two or more exact
                        # = devices genuinely sharing the IP = real ambiguity (unknown), never a silent [0].
                        $exactIp = @($results2 | Where-Object { $_.networkAddress -eq $ip })
                        if ($exactIp.Count -eq 1) { $out.outcome = 'MatchedByIp'; $out.device = $exactIp[0]; $out.viaIp = $true; return $out }
                        if ($exactIp.Count -ge 2) { $sawHits = $true }
                    }
                }
            }

            # 4. Saw hits but pinned nothing => Ambiguous; nothing seen anywhere => NoDevice.
            if ($sawHits) { $out.outcome = 'Ambiguous' } else { $out.outcome = 'NoDevice' }
            return $out
        }
        """;

    // ── Shared SSL-trust install (SINGLE-SOURCED into Script, StateScript, PreflightScript) ─────────
    //
    // The ONE place that establishes certificate trust for the HTTPS calls to WUG. Installs a COMPILED
    // static delegate (VivreWugCertValidator) as the process-wide ServerCertificateValidationCallback,
    // ONCE, before any TLS. A compiled delegate has no runspace affinity, so it validates on the
    // I/O-completion threads that service cold TLS handshakes — exactly where the module's PowerShell
    // SCRIPTBLOCK callback dies (the mass per-row LookupError bug). Because we install this ourselves and
    // connect without asking the module to ignore SSL errors, the module never installs, re-arms, or
    // clears its own scriptblock callback (every one of its callback sites is gated on that flag). Each
    // host script splices this in BEFORE its Connect-WUGServer and hard-fails on $sslTrustErr its own way;
    // the snippet itself neither emits nor returns. The compiled type name MUST NOT contain the old
    // "VivreWugTrustAll" string (the absence tests depend on that). The delegate assignment lives inside
    // the compiled Install() to dodge PowerShell delegate-cast quirks.
    internal const string SslTrustInstallScript = """
        # Compiled, runspace-free certificate validator installed as the process-wide callback BEFORE any
        # TLS. A compiled delegate validates on I/O-completion threads (no runspace), where a PowerShell
        # scriptblock callback dies on a cold handshake (the mass-LookupError bug). The module is connected
        # without asking it to ignore SSL errors, so it never installs, re-arms, or clears its own
        # scriptblock callback (all its callback sites are gated on that flag) and this compiled delegate
        # stays the sole validator. ASCII only in this comment.
        $sslTrustErr = $null
        try {
            if (-not ('VivreWugCertValidator' -as [type])) {
                Add-Type -TypeDefinition 'using System; using System.Net; using System.Net.Security; using System.Security.Cryptography.X509Certificates; public static class VivreWugCertValidator { public static bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) { return true; } public static void Install() { ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(Validate); } }'
            }
            [VivreWugCertValidator]::Install()
        } catch { $sslTrustErr = $_.Exception.Message }
        """;

    // The on-host run, reading its inputs from environment variables (so the password never appears
    // on a command line). Emits a single JSON result object: { ok, devicesSet, unmatched[], error }.
    // Built by concatenating the SHARED resolver (ResolveFunctionScript) into the body — see that const.
    internal static readonly string Script =
        """
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

        """
        + SslTrustInstallScript + "\n" +
        """
        # Connect now DEPENDS on the compiled delegate for cert trust (installed above): we do not ask the
        # module to ignore SSL errors, so it never installs, re-arms, or clears its own scriptblock callback.
        if ($sslTrustErr) {
            $result.error = "Couldn't establish a trusted connection to WhatsUp Gold: $sslTrustErr."
            Emit $result; return
        }

        # 2. Connect to the WUG server (HTTPS; cert trust is the compiled delegate installed above).
        try {
            Progress "Connecting to WhatsUp Gold at $server..."
            Connect-WUGServer -ServerUri $server -Protocol https -Credential $cred -ErrorAction Stop | Out-Null
        } catch {
            $result.error = "Couldn't connect to WhatsUp Gold at $server`: $($_.Exception.Message). Check the address, that the server is reachable, and the WUG username/password."
            Emit $result; return
        }

        """
        + ResolveFunctionScript + "\n" +
        """
        # 3. Resolve each machine to its WUG DeviceId via the SHARED resolver — one targeted SearchValue
        #    lookup per entry, NOT a full inventory pull. The resolver is error-aware and never guesses:
        #    LookupError / Ambiguous machines are EXCLUDED from unmatched (an errored / unpinned lookup is
        #    NOT a proven "no matching device"), and folded into a fail-safe honesty report below.
        $deviceIds = @()
        $unmatched = @()
        $lookupErrors = 0
        $ambiguousCount = 0
        $firstErr = $null
        $total = $names.Count
        $i = 0
        try {
            foreach ($srv in $names) {
                $i++
                Progress "Looking up $srv ($i of $total)..."
                $r = Resolve-WugName $srv
                if ($r.outcome -eq 'MatchedByName' -or $r.outcome -eq 'MatchedByIp') {
                    if ($r.viaIp) { Progress "  $srv matched by IP" }
                    $deviceIds += $r.device.id
                }
                elseif ($r.outcome -eq 'LookupError') {
                    $lookupErrors++
                    if ($null -eq $firstErr) { $firstErr = $r.error }
                }
                elseif ($r.outcome -eq 'Ambiguous') {
                    $ambiguousCount++
                }
                else {
                    # NoDevice — the only proven clean-empty miss.
                    $unmatched += $srv
                }
            }
        } catch {
            $result.error = "Connected, but couldn't search WhatsUp Gold devices: $($_.Exception.Message)."
            Emit $result; return
        }

        $result.unmatched = @($unmatched)

        # All-nothing-mapped guard: keep the honest wording, but NEVER say "no matching device" for boxes
        # that actually errored / were ambiguous — that's the false-negative this fix forbids.
        if ($deviceIds.Count -eq 0) {
            if ($lookupErrors -gt 0 -or $ambiguousCount -gt 0) {
                $failed = $lookupErrors + $ambiguousCount
                $result.error = "$failed of $($names.Count) machine(s) couldn't be looked up ($lookupErrors errored, $ambiguousCount ambiguous) — 0 device(s) were set; re-run to cover the rest."
            } else {
                $result.error = "None of the $($names.Count) machine(s) matched a WhatsUp Gold device. Unmatched: $($unmatched -join ', ')."
            }
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

        # FAIL-SAFE honesty: any lookup we couldn't complete (errored or ambiguous) makes the run report
        # FAILURE with exact counts — setting maintenance twice is idempotent, so over-reporting failure is
        # the safe direction; the one forbidden direction is silently claiming "set" for a box we never
        # cleanly looked up. Doesn't clobber a genuine Set-WUGDeviceMaintenance failure already recorded.
        if (($lookupErrors -gt 0 -or $ambiguousCount -gt 0) -and $null -eq $result.error) {
            $failed = $lookupErrors + $ambiguousCount
            $result.ok = $false
            $result.error = "$failed of $($names.Count) machine(s) couldn't be looked up ($lookupErrors errored, $ambiguousCount ambiguous) — $($result.devicesSet) device(s) were still set; re-run to cover the rest."
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
    /// connect startup and ~80× a healthy per-device lookup (measured ~1.1s live), so it cannot fire on a
    /// healthy run but names a wedge in a minute and a half.
    /// </summary>
    public static readonly TimeSpan StateReadStallTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Absolute backstop for the streaming state read so a pathological child can't live forever. Sized
    /// far above a healthy 324-box run (measured ~1.1s/lookup: ~6.5 min sequential, ~3 min at N=2) — a pure
    /// runaway backstop; the stall timer, not this, is what catches hangs.
    /// </summary>
    public static readonly TimeSpan StateReadCeiling = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Hard ceiling on how many per-name lookups the streaming state read may run in parallel. MEASURED
    /// ceiling: the live Gate 0 ramp flatlined past 2 concurrent lookups — wall time halved 1→2 then did
    /// not improve 2→4→8, with per-lookup latency creeping UP as workers piled on (WUG serialises under
    /// load). So anything above 4 is pure extra load on the one box that monitors the whole fleet for no
    /// wall-time gain. The parameter default stays 1 (sequential); the operator setting
    /// (AppSettings.WugStateConcurrency, default 2) supplies the real value at call time.
    /// </summary>
    public const int StateReadMaxConcurrency = 4;

    /// <summary>
    /// Bounds a requested state-read concurrency into the safe range [1, <see cref="StateReadMaxConcurrency"/>].
    /// The in-script drain clamps the same range again (defence in depth) so a hand-edited env var can
    /// never open an unbounded pool against the WUG server.
    /// </summary>
    internal static int ClampConcurrency(int requested) => Math.Clamp(requested, 1, StateReadMaxConcurrency);

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
    internal const string PreflightScript = """
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

        """
        + SslTrustInstallScript + "\n" +
        """
        # SSL trust hard-fail: the compiled delegate installed above is now the sole cert-trust path (we do
        # not ask the module to ignore SSL errors, so it installs no scriptblock). Fail before the connect.
        if ($sslTrustErr) {
            $result.error = "Couldn't establish a trusted connection to WhatsUp Gold: $sslTrustErr."
            Emit $result; return
        }

        # 4. Connect test (cert trust is the compiled delegate installed above)
        try {
            Connect-WUGServer -ServerUri $server -Protocol https -Credential $cred -ErrorAction Stop | Out-Null
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

    // The per-runspace worker tail (state read, POOLED branch only): the connect-guard + the single
    // Resolve-WugName call, assigned to $workerText's tail. Composed into a SINGLE-QUOTED here-string
    // ($workerTail = @'...'@) so its body is LITERAL script text — every $env:/$global: reference is
    // evaluated by the WORKER at run time, never expanded at compose time. Connect ONCE PER RUNSPACE:
    // the guard fires the first time a pool slot runs a worker, then that runspace reuses its
    // authenticated session for every later lookup it handles. NEVER copy the module's auth globals
    // across runspaces by reference — the headers dict is not thread-safe and sharing it produced garbage
    // reads under load; each worker reads server/user/pass from the process-global env and builds its own
    // session instead. No line may begin with '@ at column 0 (that would close the here-string early).
    internal const string StateWorkerTailBody = """
        if (-not $global:WUGBearerHeaders) {
            $sec  = ConvertTo-SecureString $env:VIVRE_WUG_PASS -AsPlainText -Force
            $cred = New-Object System.Management.Automation.PSCredential($env:VIVRE_WUG_USER, $sec)
            # No SSL-ignore flag: cert trust is the compiled process-wide delegate the HEAD installed once,
            # which every worker runspace inherits (a process-global ServicePointManager setting). The module
            # installs no scriptblock callback here, so there is nothing to re-null.
            Connect-WUGServer -ServerUri $env:VIVRE_WUG_SERVER -Protocol https -Credential $cred -ErrorAction Stop | Out-Null
        }
        Resolve-WugName $srv
        """;

    // The streaming resolve loop (state read), composed into StateScript AFTER $resolverText and
    // $workerTail so it reads BOTH as the ONE resolver source (never a second, forked resolver): the
    // sequential branch Invoke-Expressions $resolverText into the main scope; the pooled branch embeds the
    // same text in each worker. Defines Process-WugOutcome — the outcome dispatch + tri-state read +
    // EmitDevice + counters, MOVED VERBATIM from the old inline loop body (the tri-state block is
    // byte-identical; the counter mutations gained a $script: scope prefix so both branches share one
    // accounting). Internal so a test can string-lock it (DefaultConnectionLimit, etc.) and compose it
    // over stubs under real 5.1. No line may begin with '@ at column 0.
    internal const string StateResolveLoopScript = """
        # Per-run accounting (script scope: Process-WugOutcome is called from either branch and writes ONE set).
        $devices = @()
        $unmatched = @()
        $lookupErrors = 0
        $ambiguous = 0
        $matchedByIp = 0
        $resolvedStates = 0
        $firstErr = $null
        $reasonErrors = 0
        $firstReasonErr = $null
        # Per-lookup elapsed (ms), recorded in COMPLETION order, for the degradation check below.
        $script:lookupMs = @()

        # The outcome dispatch + tri-state read + detail enrichment + EmitDevice (the tri-state block is
        # byte-identical to the original inline loop body; the emission gained the in-maintenance-only
        # detail enrichment). Counters use $script: scope so the pooled drain and the sequential loop
        # accumulate into one set.
        function Process-WugOutcome {
            param($srv, $r)
            if ($r.outcome -eq 'MatchedByName' -or $r.outcome -eq 'MatchedByIp') {
                $match = $r.device
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
                # Detail enrichment (reason / who / since): IN-MAINTENANCE rows ONLY, so a machine not in
                # maintenance costs exactly what it cost before this feature. Two read-only GETs against the
                # session the main-scope Connect established (guarded on its globals - absent means no
                # session, e.g. under the test stubs, and enrichment silently skips). EVERY failure fails
                # OPEN ($state untouched, fields simply absent) and is COUNTED so the summary carries a
                # reason-lookup note - degraded detail is said out loud, never silent, never a state change.
                # Runs only on the emitting thread (sequential loop / pooled drain): EmitDevice stays
                # single-writer, and each row still emits well inside the stall window (two 15s-capped GETs).
                $detail = $null
                if ($state -eq $true -and $global:WhatsUpServerBaseURI -and $global:WUGBearerHeaders) {
                    $devId = "$($match.id)"
                    if (-not [string]::IsNullOrWhiteSpace($devId)) {
                        # The two detail GETs ride the process-wide compiled delegate the HEAD installed
                        # before any fan-out - nothing to re-arm per read.
                        $detail = [ordered]@{}
                        try {
                            $mc = (Invoke-RestMethod -Uri "$($global:WhatsUpServerBaseURI)/api/v1/devices/$devId/config/polling" -Method Get -Headers $global:WUGBearerHeaders -TimeoutSec 15).data.maintenance.manual
                            if ($mc) {
                                if (-not [string]::IsNullOrWhiteSpace("$($mc.reason)")) { $detail.reason = "$($mc.reason)" }
                                if (-not [string]::IsNullOrWhiteSpace("$($mc.user)"))   { $detail.user   = "$($mc.user)" }
                            }
                            $st = (Invoke-RestMethod -Uri "$($global:WhatsUpServerBaseURI)/api/v1/devices/$devId/status" -Method Get -Headers $global:WUGBearerHeaders -TimeoutSec 15).data
                            if ($st -and -not [string]::IsNullOrWhiteSpace("$($st.lastChangeUtc)")) { $detail.sinceUtc = "$($st.lastChangeUtc)" }
                        } catch {
                            $script:reasonErrors++
                            if ($null -eq $script:firstReasonErr) {
                                $script:firstReasonErr = "$($_.Exception.Message)"
                            }
                        }
                        if ($detail.Count -eq 0) { $detail = $null }
                    }
                }
                # Entry carries the INPUT name, not the WUG displayName, so the host keys back by what it asked.
                $sum = [ordered]@{ name = $srv; inMaintenance = $state }
                $dev = [ordered]@{ name = $srv; matched = $true; inMaintenance = $state }
                if ($detail) { foreach ($k in @($detail.Keys)) { $sum[$k] = $detail[$k]; $dev[$k] = $detail[$k] } }
                $script:devices += $sum
                if ($r.outcome -eq 'MatchedByIp') { $dev.matchedByIp = $true; $script:matchedByIp++ }
                EmitDevice $dev
                $script:resolvedStates++
            }
            elseif ($r.outcome -eq 'LookupError') {
                # A WUG search errored — state UNKNOWN, never a false "no matching device". Counted.
                $script:lookupErrors++
                if ($null -eq $script:firstErr) { $script:firstErr = $r.error }
                EmitDevice ([ordered]@{ name = $srv; matched = $true; inMaintenance = $null })
            }
            elseif ($r.outcome -eq 'Ambiguous') {
                # Hits but nothing exact and no IP rescue — UNKNOWN, never $results[0]. Counted.
                $script:ambiguous++
                EmitDevice ([ordered]@{ name = $srv; matched = $true; inMaintenance = $null })
            }
            else {
                # NoDevice — a clean empty answer everywhere is the ONLY honest "no matching device".
                $script:unmatched += $srv
                EmitDevice ([ordered]@{ name = $srv; matched = $false; inMaintenance = $null })
            }
        }

        # Concurrency: absent / invalid => 1 (sequential). Clamp 1..4 in-script too — defence in depth on
        # top of the C# ClampConcurrency, so a hand-edited env var can never open an unbounded pool.
        $conc = 1
        $rawConc = $env:VIVRE_WUG_CONCURRENCY
        if ($rawConc) { $parsedConc = 0; if ([int]::TryParse($rawConc, [ref]$parsedConc)) { $conc = $parsedConc } }
        if ($conc -lt 1) { $conc = 1 }
        if ($conc -gt 4) { $conc = 4 }

        try {
            if ($conc -le 1) {
                # SEQUENTIAL branch — today's behaviour exactly. Invoke-Expression of our own compiled-in
                # literal defines the resolver functions HERE in the main scope (workers embed the same text
                # instead of sharing them by reference). Concurrency 1 (the setting's floor, and the
                # parameter default) lands here — byte-equivalent to the pre-pool sequential behaviour.
                Invoke-Expression $resolverText
                foreach ($srv in $names) {
                    $sw = [System.Diagnostics.Stopwatch]::StartNew()
                    $r = Resolve-WugName $srv
                    $sw.Stop()
                    $script:lookupMs += $sw.Elapsed.TotalMilliseconds
                    Process-WugOutcome $srv $r
                }
            }
            else {
                # POOLED branch. All four fan-out traps are honoured below.
                # T1: .NET Framework defaults the per-host connection cap to 2 and the module never raises
                #     it; without this a pool of ANY size silently throttles to 2 concurrent HTTP calls.
                [System.Net.ServicePointManager]::DefaultConnectionLimit = 32

                # T2: a runspace pool whose runspaces already have the module imported. The import is
                #     CONDITIONAL so the test harness (module absent, stubs embedded in $resolverText shadow
                #     it) still opens; in production the module-present check above already passed.
                $iss = [initialsessionstate]::CreateDefault()
                # Test seam: VIVRE_WUG_MODULE_OVERRIDE (never set in production) lets the process tests carry a
                # lightweight stub module through this SAME ImportPSModule path instead of cold-loading the real
                # WhatsUpGoldPS (~8s per runspace). ImportPSModule accepts a filesystem path as well as a name.
                if ($env:VIVRE_WUG_MODULE_OVERRIDE) { $iss.ImportPSModule($env:VIVRE_WUG_MODULE_OVERRIDE) }
                elseif (Get-Module -ListAvailable WhatsUpGoldPS) { $iss.ImportPSModule('WhatsUpGoldPS') }
                $pool = [runspacefactory]::CreateRunspacePool(1, $conc, $iss, $Host)
                $pool.Open()

                # One worker script for every lookup (only the -SearchValue argument differs): param($srv),
                # then the resolver text, then the per-runspace connect-guard tail. Single-quoted
                # 'param($srv)' keeps $srv LITERAL so it binds to the AddArgument value, not this drain
                # scope's loop variable.
                $workerText = 'param($srv)' + "`n" + $resolverText + "`n" + $workerTail

                try {
                    $pending = [System.Collections.Generic.Queue[string]]::new()
                    foreach ($n in $names) { $pending.Enqueue($n) }
                    $inflight = [System.Collections.Generic.List[object]]::new()

                    # COMPLETION-ORDER poll-drain: keep <= $conc in flight and emit each result as it
                    # FINISHES — NOT submission-order EndInvoke (head-of-line blocking would starve stdout and
                    # trip the external stall watchdog) and NOT WaitHandle.WaitAny (64-handle cap). T4:
                    # PowerShell.Stop() is cooperative and cannot interrupt a blocked Invoke-RestMethod, so
                    # there is deliberately NO per-lookup Stop() plumbing here — the C# stall watchdog +
                    # ceiling stay the sole authority over a wedged run.
                    while ($pending.Count -gt 0 -or $inflight.Count -gt 0) {
                        while ($inflight.Count -lt $conc -and $pending.Count -gt 0) {
                            $srv = $pending.Dequeue()
                            $ps = [powershell]::Create()
                            $ps.RunspacePool = $pool
                            $null = $ps.AddScript($workerText).AddArgument($srv)
                            $inflight.Add([pscustomobject]@{ Name = $srv; PS = $ps; Handle = $ps.BeginInvoke(); Sw = [System.Diagnostics.Stopwatch]::StartNew() })
                        }

                        $done = @($inflight | Where-Object { $_.Handle.IsCompleted })
                        if ($done.Count -eq 0) {
                            Start-Sleep -Milliseconds 100
                            continue
                        }
                        foreach ($slot in $done) {
                            $r = $null
                            try {
                                # T5: per-worker EndInvoke try/catch is MANDATORY. A THROW (worker connect
                                # failure, stub throw, anything) becomes a LookupError-shaped outcome so ONE
                                # bad lookup can never abort the other 323 — a thrown lookup reads UNKNOWN
                                # (matched:true/inMaintenance:null downstream), never matched:false, never
                                # unmatched (preserves the Bug-1 error-aware contract).
                                $out = $slot.PS.EndInvoke($slot.Handle)
                                $r = $out | Select-Object -Last 1
                                if ($null -eq $r) { throw "the lookup produced no result" }
                            }
                            catch {
                                $r = [ordered]@{ outcome = 'LookupError'; device = $null; viaIp = $false; hits = 0; error = "$($_.Exception.Message)" }
                            }
                            $slot.Sw.Stop()
                            $script:lookupMs += $slot.Sw.Elapsed.TotalMilliseconds
                            # EMISSION RULE: __WUGDEV__ lines are written ONLY here, on the main drain thread —
                            # never from a PSDataCollection.DataAdded handler (fires on worker threads =>
                            # multi-writer stdout) and never via [Console]::WriteLine anywhere.
                            Process-WugOutcome $slot.Name $r
                            $slot.PS.Dispose()
                            [void]$inflight.Remove($slot)
                        }
                    }
                }
                finally {
                    $pool.Close(); $pool.Dispose()
                }
            }
        }
        catch {
            $result.error = "Connected, but couldn't search WhatsUp Gold devices: $($_.Exception.Message)."
            Emit $result; return
        }

        # Keep devices an ARRAY even for a single entry - a bare scalar assignment serializes one device
        # as a JSON object, not an array.
        $result.devices = @($devices)
        $result.unmatched = @($unmatched)
        $result.lookupErrors = $lookupErrors
        $result.ambiguous = $ambiguous
        $result.matchedByIp = $matchedByIp
        $result.ok = $true

        # Latency visibility. baseline = mean of the first up-to-5 COMPLETED lookups; avg = mean of all.
        # avgLookupMs / baselineLookupMs are FUTURE-PROOFING — the C# parser ignores them for now; the
        # error text below is the operator-visible signal.
        $avgMs = 0.0; $baseMs = 0.0
        if ($script:lookupMs.Count -gt 0) {
            $avgMs  = ($script:lookupMs | Measure-Object -Average).Average
            $baseCount = [Math]::Min(5, $script:lookupMs.Count)
            $baseMs = ($script:lookupMs | Select-Object -First $baseCount | Measure-Object -Average).Average
        }
        $result.avgLookupMs = [Math]::Round($avgMs, 1)
        $result.baselineLookupMs = [Math]::Round($baseMs, 1)

        # Summary honesty. A search error must not read as a clean green run: surface how many failed with
        # the first captured reason (ok stays true if some resolved — the host logs an Error line whenever
        # error is non-null). Otherwise the all-failed guard (the set path has one): if NOTHING produced a
        # real state, say so instead of an empty clean summary.
        if ($lookupErrors -gt 0) {
            $result.error = "$lookupErrors of $($names.Count) lookups failed — $firstErr"
        }
        elseif ($resolvedStates -eq 0 -and $names.Count -gt 0) {
            $result.error = "None of the $($names.Count) machine(s) matched a WhatsUp Gold device ($($unmatched.Count) unmatched, $ambiguous ambiguous)."
        }

        # Reason-lookup honesty (APPENDED, never replacing the text above): in-maintenance rows whose
        # detail read failed still show a correct plain "in maintenance" - but a degraded read is said out
        # loud here, never silently.
        if ($reasonErrors -gt 0) {
            $reasonMsg = "maintenance-reason lookup failed for $reasonErrors machine(s) — $firstReasonErr"
            if ($result.error) { $result.error = "$($result.error); $reasonMsg" } else { $result.error = $reasonMsg }
        }

        # Degradation warning (APPENDED, never replacing the honesty text above). Floor at 50ms so instant
        # stubs / sub-millisecond lookups don't trip the 2x ratio on pure jitter; a real WUG lookup is ~1s.
        if ($baseMs -ge 50 -and $avgMs -gt (2 * $baseMs)) {
            $slowMsg = "WUG lookups slowed during the run — avg {0:N1}s vs {1:N1}s baseline" -f ($avgMs / 1000), ($baseMs / 1000)
            if ($conc -gt 1) { $slowMsg += " — consider lowering the concurrency setting" }
            if ($result.error) { $result.error = "$($result.error); $slowMsg" } else { $result.error = $slowMsg }
        }
        Emit $result
        """;

    // Emits ONE JSON summary line: { ok, devices[{ name, inMaintenance, reason?, user?, sinceUtc? }],
    // unmatched[], error, lookupErrors, ambiguous, matchedByIp, avgLookupMs, baselineLookupMs } plus one
    // streamed __WUGDEV__ line per device as it resolves (same optional detail fields, in-maintenance rows
    // only). Reads server/user/pass + names from env vars — password NEVER on the command line. Read-only:
    // it never sets maintenance (the detail enrichment is two GETs). No __WUGP__ progress lines (the
    // shared launcher has no progress plumbing).
    //
    // Composition (the recomposed streaming/pooled seam):
    //   HEAD (Emit/EmitDevice, env reads, $result init, trap, module check, Import, credential, Connect —
    //         the main-runspace connect stays FIRST: it validates + installs the process-wide TLS callback
    //         before any fan-out and keeps the early-exit error paths)
    //   + $resolverText = @'  <the ONE ResolveFunctionScript>  '@   (single-quoted here-string; ONE copy
    //         serves both branches — the sequential branch IEXes it, each pooled worker embeds it; NO fork)
    //   + $workerTail = @'    <StateWorkerTailBody>              '@
    //   + StateResolveLoopScript (defines Process-WugOutcome + the sequential/pooled dispatch + summary)
    // VIVRE_WUG_CONCURRENCY (absent => 1 => sequential) selects the branch.
    internal static readonly string StateScript =
        """
        $ErrorActionPreference = 'Stop'
        # Tag the result line with a unique marker so the host extracts EXACTLY it, immune to any banner /
        # warning / object text a cmdlet might print to stdout (see ParseMaintenanceState).
        function Emit($r) { Write-Output ("__WUGRESULT__" + ($r | ConvertTo-Json -Compress -Depth 4)) }
        # Stream one result line per device AS it resolves, so a long run shows progress and an aborted
        # read keeps what already came back. `matched` here means the row shows a STATE or "unknown" (true)
        # rather than "no matching device" (false): a lookup error or an ambiguous name is matched:true with
        # inMaintenance:null (state unknown), NEVER a false "no matching device". matchedByIp is emitted only
        # when true. JSON per line is load-bearing: 5.1's ConvertTo-Json escapes non-ASCII to \uXXXX so the
        # payload is pure ASCII on the wire, immune to the OEM code page of redirected stdout; never switch
        # to a raw delimited format.
        function EmitDevice($e) { Write-Output ("__WUGDEV__" + ($e | ConvertTo-Json -Compress -Depth 3)) }

        $names  = @($env:VIVRE_WUG_NAMES -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $server = $env:VIVRE_WUG_SERVER
        $result = [ordered]@{ ok = $false; devices = @(); unmatched = @(); error = $null; lookupErrors = 0; ambiguous = 0; matchedByIp = 0 }

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

        """
        + SslTrustInstallScript + "\n" +
        """
        # SSL trust must be established before we open TLS to WUG: the compiled delegate installed above is
        # the SOLE cert-trust path. We do not ask the module to ignore SSL errors, so it never installs,
        # re-arms, or clears its own scriptblock callback (all its callback sites are gated on that flag).
        # The delegate rides every worker handshake and every detail GET, validating on I/O-completion
        # threads where a scriptblock callback would die on a cold handshake (the mass-LookupError bug).
        if ($sslTrustErr) {
            $result.error = "Couldn't establish a trusted connection to WhatsUp Gold: $sslTrustErr."
            Emit $result; return
        }

        # 4. Connect (HTTPS; cert trust is the compiled delegate installed above)
        try {
            Connect-WUGServer -ServerUri $server -Protocol https -Credential $cred -ErrorAction Stop | Out-Null
        } catch {
            $result.error = "Couldn't connect to WhatsUp Gold at $server`: $($_.Exception.Message). Check the address, that the server is reachable, and the WUG username/password."
            Emit $result; return
        }

        """
        // The ONE resolver, single-sourced into a single-quoted here-string (literal — no compose-time
        // expansion). Explicit "\n" fencing puts @' at line end and '@ at column 0 regardless of C#
        // indentation; ResolveFunctionScript contains no line beginning with '@.
        + "$resolverText = @'\n" + ResolveFunctionScript + "\n'@\n"
        + "$workerTail = @'\n" + StateWorkerTailBody + "\n'@\n"
        + StateResolveLoopScript;

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
    /// <para><paramref name="concurrency"/> is how many per-name lookups the on-host script runs in
    /// parallel (clamped to [1, <see cref="StateReadMaxConcurrency"/>]). It DEFAULTS to 1 (sequential) —
    /// the ViewModel wrapper passes the operator's Settings value (default 2) at call time; 1 = the
    /// pre-pool sequential path.</para>
    /// </summary>
    public static async Task<WugMaintenanceStateResult> GetMaintenanceStateAsync(
        IReadOnlyList<string> names,
        string server,
        string username,
        string password,
        TimeSpan timeout,
        CancellationToken ct = default,
        IProgress<WugDeviceState>? deviceProgress = null,
        TimeSpan? stallTimeout = null,
        int concurrency = 1)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VIVRE_WUG_NAMES"]  = string.Join("\n", names),
            ["VIVRE_WUG_SERVER"] = server,
            ["VIVRE_WUG_USER"]   = username,
            ["VIVRE_WUG_PASS"]   = password,   // password reaches the child via env var ONLY
            // Clamped here AND re-clamped in-script (defence in depth). 1 => the sequential branch.
            ["VIVRE_WUG_CONCURRENCY"] = ClampConcurrency(concurrency).ToString(CultureInfo.InvariantCulture),
        };

        // Partial state assembled from the streamed per-device lines so an aborted read (stall / ceiling /
        // crash) still returns what already came back instead of discarding it. OnDeviceLine fires on a
        // ThreadPool async-read thread, so every touch of these is under `gate`. lastName/seen name the
        // machine an abort stopped after, for the error text.
        var gate = new object();
        var partial = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var partialDetails = new Dictionary<string, WugMaintenanceDetail>(StringComparer.OrdinalIgnoreCase);
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
                    // Keep the streamed detail too, so an ABORTED read's reconcile can't downgrade a row
                    // that already showed reason/who/since back to a plain "in maintenance".
                    if (parsed.Reason is not null || parsed.User is not null || parsed.SinceUtc is not null)
                    {
                        partialDetails[parsed.Name] = new WugMaintenanceDetail(parsed.Reason, parsed.User, parsed.SinceUtc);
                    }
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
            Dictionary<string, bool?> snap; Dictionary<string, WugMaintenanceDetail> snapDetails; List<string> snapUnmatched; string? ln; int sn;
            lock (gate)
            {
                snap = new Dictionary<string, bool?>(partial, StringComparer.OrdinalIgnoreCase);
                snapDetails = new Dictionary<string, WugMaintenanceDetail>(partialDetails, StringComparer.OrdinalIgnoreCase);
                snapUnmatched = new List<string>(partialUnmatched);
                ln = lastName; sn = seen;
            }
            return new WugMaintenanceStateResult(
                snap, snapUnmatched, ComposeAbortError("Stalled", ln, sn, names.Count, ex.Stall),
                DetailsByName: snapDetails);
        }
        catch (TimeoutException)
        {
            // Hit the absolute ceiling: same shape, ceiling wording and window. Snapshot under `gate` for
            // the same reason as above — the killed child's draining pump can still write the live maps.
            Dictionary<string, bool?> snap; Dictionary<string, WugMaintenanceDetail> snapDetails; List<string> snapUnmatched; string? ln; int sn;
            lock (gate)
            {
                snap = new Dictionary<string, bool?>(partial, StringComparer.OrdinalIgnoreCase);
                snapDetails = new Dictionary<string, WugMaintenanceDetail>(partialDetails, StringComparer.OrdinalIgnoreCase);
                snapUnmatched = new List<string>(partialUnmatched);
                ln = lastName; sn = seen;
            }
            return new WugMaintenanceStateResult(
                snap, snapUnmatched, ComposeAbortError("Timed out", ln, sn, names.Count, timeout),
                DetailsByName: snapDetails);
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
            Dictionary<string, bool?> snap; Dictionary<string, WugMaintenanceDetail> snapDetails; List<string> snapUnmatched;
            lock (gate)
            {
                snap = new Dictionary<string, bool?>(partial, StringComparer.OrdinalIgnoreCase);
                snapDetails = new Dictionary<string, WugMaintenanceDetail>(partialDetails, StringComparer.OrdinalIgnoreCase);
                snapUnmatched = new List<string>(partialUnmatched);
            }
            return new WugMaintenanceStateResult(snap, snapUnmatched, ex.Message, DetailsByName: snapDetails);
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
        var details = new Dictionary<string, WugMaintenanceDetail>(StringComparer.OrdinalIgnoreCase);

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
                        AddDevice(byName, details, dev);
                    }
                }
                else if (devicesEl.ValueKind == JsonValueKind.Object)
                {
                    AddDevice(byName, details, devicesEl);
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

            // New honesty counts (absent => 0 for back-compat with pre-fix summaries). Guard ValueKind so
            // a non-number never throws through the JsonException-only catch.
            int lookupErrors = ReadCount(root, "lookupErrors");
            int ambiguous    = ReadCount(root, "ambiguous");
            int matchedByIp  = ReadCount(root, "matchedByIp");

            return new WugMaintenanceStateResult(byName, unmatched, error, lookupErrors, ambiguous, matchedByIp, details);
        }
        catch (JsonException)
        {
            string detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "Couldn't parse the maintenance-state result: " + json;
            return new WugMaintenanceStateResult(byName, [], detail);
        }
    }

    // Reads an optional non-negative integer summary count; absent / non-number => 0. Never throws.
    private static int ReadCount(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v)
            ? v
            : 0;

    // Adds one device entry to the tri-state map: the name must be a string (skip the entry otherwise);
    // inMaintenance True/False map to true/false, and anything else (absent, null, string, number) to
    // null — unknown, never assumed false. The optional detail (reason/user/sinceUtc, via the shared
    // ReadDetail) can only ADD display text — a malformed detail never changes the state.
    private static void AddDevice(Dictionary<string, bool?> byName, Dictionary<string, WugMaintenanceDetail> details, JsonElement dev)
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

        string name = nameEl.GetString()!;
        byName[name] = state;
        if (ReadDetail(dev) is { } detail)
        {
            details[name] = detail;
        }
    }

    // The ONE reader for the optional maintenance detail on a device entry — shared by AddDevice and
    // ParseDeviceLine so the summary and the streamed lines can never diverge. Each field must be a
    // non-whitespace JSON string (trimmed); anything else (absent, null, number, object) => null. All
    // three null => no detail at all.
    private static WugMaintenanceDetail? ReadDetail(JsonElement dev)
    {
        string? reason = ReadOptionalString(dev, "reason");
        string? user = ReadOptionalString(dev, "user");
        string? since = ReadOptionalString(dev, "sinceUtc");
        return reason is null && user is null && since is null
            ? null
            : new WugMaintenanceDetail(reason, user, since);
    }

    private static string? ReadOptionalString(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v)
           && v.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

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

            // Optional, present only when true. JSON true => true; absent / null / any other shape => false.
            bool matchedByIp = root.TryGetProperty("matchedByIp", out JsonElement ipEl) && ipEl.ValueKind == JsonValueKind.True;

            // Optional maintenance detail — the SAME reader as AddDevice (lockstep); display-only, can
            // never affect matched / inMaintenance above.
            WugMaintenanceDetail? detail = ReadDetail(root);

            return new WugDeviceState(
                nameEl.GetString()!, matched, inMaintenance, matchedByIp,
                detail?.Reason, detail?.User, detail?.SinceUtc);
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

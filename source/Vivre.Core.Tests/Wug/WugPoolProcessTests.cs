using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Real <c>powershell.exe</c> (Windows PowerShell 5.1) tests for the PARALLEL state-read seam — the
/// runspace-pool fan-out inside <see cref="WugMaintenance.StateResolveLoopScript"/> (composed into
/// <see cref="WugMaintenance.StateScript"/>). Mirrors the proven <c>WugResolverProcessTests</c> harness:
/// each test composes a head-mirror + a <c>$resolverText</c> here-string (a stub <c>Get-WUGDevice</c> with
/// per-name delay/error/throw + the REAL <see cref="WugMaintenance.ResolveFunctionScript"/> + a DNS
/// null-override so no test ever hits real DNS) + a <c>$workerTail</c> here-string (a stub
/// <c>Connect-WUGServer</c> that records one line per connect + the REAL connect-guard) + the REAL
/// <see cref="WugMaintenance.StateResolveLoopScript"/>. Device lines stream through the real launcher and
/// are parsed by the real <see cref="WugMaintenance.ParseDeviceLine"/>; the summary by the real
/// <see cref="WugMaintenance.ParseMaintenanceState"/> — so the streaming + parse contract is exercised end
/// to end, not re-implemented.
///
/// <para>ALL in ONE class ON PURPOSE (xUnit serialises within a class; the whole suite is serial via
/// TestParallelization.cs). Names/concurrency travel via the PER-CHILD env dictionary — never
/// process-level env. Composed scripts include the shipped em-dashes from StateResolveLoopScript; the
/// launcher writes them UTF-8-with-BOM internally. Time bounds are deliberately generous — these assert
/// behaviour + ordering, not speed.</para>
/// </summary>
public class WugPoolProcessTests
{
    private static string PsExePath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static void RequirePowerShell51()
        => Assert.True(File.Exists(PsExePath),
            $"Windows PowerShell 5.1 was not found at {PsExePath}. These process tests require it (it is always present on the dev box).");

    // A committed stub .psm1 test asset (Wug/Fixtures/WugStubModule.psm1, copied to the test output by the
    // csproj) that rides the SAME ISS.ImportPSModule(<path>) mechanics the production pooled branch uses for
    // WhatsUpGoldPS — but cold-loads in ~0ms instead of ~8s per runspace. Its exported functions are
    // shadowed by the per-test resolver-text stubs (Get-WUGDevice) and worker-tail stub (Connect-WUGServer),
    // so its only job is to prove ImportPSModule-BY-PATH works under the launcher's stripped PSModulePath and
    // to spare the plumbing tests the real module's cold-load. A durable on-disk fixture imported by path is
    // MORE production-like than a per-run temp file: production's WhatsUpGoldPS is itself a durable on-disk
    // module, so importing this by path exercises the identical import-by-path mechanics — with ZERO runtime
    // writes, cleanup, or leak. Carried via VIVRE_WUG_MODULE_OVERRIDE.
    private static readonly string StubModulePath =
        Path.Combine(AppContext.BaseDirectory, "Wug", "Fixtures", "WugStubModule.psm1");

    // Fail with a clear message if the fixture didn't make it to the test output (a missing csproj copy).
    private static void RequireStubModule()
        => Assert.True(File.Exists(StubModulePath),
            $"The committed WUG stub module fixture was not found at {StubModulePath}. It must be copied to the test output by Vivre.Core.Tests.csproj (None Include=\"Wug\\Fixtures\\WugStubModule.psm1\" CopyToOutputDirectory=\"PreserveNewest\").");

    // Quick child check: is the REAL WhatsUpGoldPS installed on this machine? Used only by the one
    // real-module smoke test so it can fail with a clear message instead of a cryptic False probe.
    private static async Task<bool> WhatsUpGoldModuleInstalledAsync()
    {
        const string probe = "if (Get-Module -ListAvailable WhatsUpGoldPS) { Write-Output 'MODULE_YES' } else { Write-Output 'MODULE_NO' }";
        var (stdout, _) = await WugMaintenance.RunPreflightProcessAsync(
            probe, new Dictionary<string, string>(StringComparer.Ordinal), TimeSpan.FromSeconds(60), CancellationToken.None);
        return stdout.Contains("MODULE_YES");
    }

    // A canned WUG device (same shape as WugResolverProcessTests). Best null => no bestState/worstState
    // props (state UNKNOWN); HostName null => no hostName prop. displayName is NEVER emitted.
    private sealed record StubDev(string Name, string? HostName, string Ip, string? Best);

    private sealed record PoolRun(
        IReadOnlyList<WugDeviceState> Devices,
        WugMaintenanceStateResult Summary,
        TimeSpan FirstDeviceAt,
        TimeSpan ReturnedAt,
        string Stdout,
        string Stderr);

    // ── PS composition ────────────────────────────────────────────────────────────────────────────────

    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private static string BuildInventory(IEnumerable<StubDev> devs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$script:Inv = @(");
        foreach (StubDev d in devs)
        {
            sb.Append("  [pscustomobject][ordered]@{ name = ").Append(PsQuote(d.Name));
            if (d.HostName is not null) { sb.Append("; hostName = ").Append(PsQuote(d.HostName)); }
            sb.Append("; networkAddress = ").Append(PsQuote(d.Ip));
            if (d.Best is not null) { sb.Append("; bestState = ").Append(PsQuote(d.Best)).Append("; worstState = ").Append(PsQuote(d.Best)); }
            sb.AppendLine(" }");
        }
        sb.AppendLine(")");
        return sb.ToString();
    }

    private static string BuildNameList(string varName, IEnumerable<string>? names)
    {
        string joined = names is null ? "" : string.Join(",", names.Select(PsQuote));
        return $"${varName} = @({joined})\n";
    }

    private static string BuildDelays(IReadOnlyDictionary<string, int>? delays)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$script:Delays = @{");
        if (delays is not null)
        {
            foreach (var kv in delays) { sb.Append("  ").Append(PsQuote(kv.Key)).Append(" = ").Append(kv.Value).AppendLine(); }
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // The stub Get-WUGDevice: an ADVANCED function (honours -ErrorAction / -ErrorVariable like the real
    // cmdlet). Optional per-name delay (Start-Sleep), optional terminating THROW, optional non-terminating
    // Write-Error, else every inventory item whose name/hostName/networkAddress SUBSTRING-contains the
    // SearchValue (WUG's server-side rule). $script:Delays/$script:ThrowNames/$script:ErrNames come from
    // the SAME $resolverText here-string so a worker runspace rebuilds them too.
    private const string StubGetWugDevice = """
        function Get-WUGDevice {
            [CmdletBinding()]
            param([string]$SearchValue, [string]$View)
            $t0 = [DateTime]::UtcNow.Ticks
            if ($script:Delays.ContainsKey($SearchValue)) { Start-Sleep -Milliseconds $script:Delays[$SearchValue] }
            # Optional per-lookup execution interval (concurrency proof) — recorded around the sleep so two
            # lookups running in parallel produce OVERLAPPING [t0,t1] intervals. Retry past cross-runspace
            # file-lock contention. Gated so other tests never touch it.
            if ($env:VIVRE_TEST_TIMELINE) {
                $t1 = [DateTime]::UtcNow.Ticks
                for ($k = 0; $k -lt 100; $k++) {
                    try { Add-Content -LiteralPath $env:VIVRE_TEST_TIMELINE -Value "$t0 $t1" -ErrorAction Stop; break }
                    catch { Start-Sleep -Milliseconds 15 }
                }
            }
            # Optional real-module presence probe (the ONE smoke test): records whether the REAL
            # WhatsUpGoldPS is actually imported into THIS worker runspace, proving the production
            # ImportPSModule('WhatsUpGoldPS') branch fired (True) rather than a stub override (False).
            # Gated so every other test never touches it.
            if ($env:VIVRE_TEST_MODULEPROBE) {
                $present = [bool](Get-Module WhatsUpGoldPS)
                for ($m = 0; $m -lt 100; $m++) {
                    try { Add-Content -LiteralPath $env:VIVRE_TEST_MODULEPROBE -Value "$present" -ErrorAction Stop; break }
                    catch { Start-Sleep -Milliseconds 15 }
                }
            }
            if ($script:ThrowNames -contains $SearchValue) { throw "stub hard failure: $SearchValue" }
            if ($script:ErrNames -contains $SearchValue) { Write-Error "stub search failed: $SearchValue"; return }
            $sv = $SearchValue
            $script:Inv | Where-Object {
                (($_.PSObject.Properties.Name -contains 'name') -and $_.name -like "*$sv*") -or
                (($_.PSObject.Properties.Name -contains 'hostName') -and $_.hostName -like "*$sv*") -or
                (($_.PSObject.Properties.Name -contains 'networkAddress') -and $_.networkAddress -like "*$sv*")
            }
        }
        """;

    // Head-mirror: the parts of StateScript's HEAD that StateResolveLoopScript depends on (Emit/EmitDevice/
    // $names/$result init) — the SAME precedent as WugResolverProcessTests' Driver head.
    private const string HeadMirror = """
        $ErrorActionPreference = 'Stop'
        function Emit($r) { Write-Output ("__WUGRESULT__" + ($r | ConvertTo-Json -Compress -Depth 4)) }
        function EmitDevice($e) { Write-Output ("__WUGDEV__" + ($e | ConvertTo-Json -Compress -Depth 3)) }
        $names  = @($env:VIVRE_WUG_NAMES -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $result = [ordered]@{ ok = $false; devices = @(); unmatched = @(); error = $null; lookupErrors = 0; ambiguous = 0; matchedByIp = 0 }
        """;

    // The TEST worker tail: mirrors the REAL connect-guard line (identical `if (-not $global:WUGBearerHeaders)`
    // block, cred build, and `Resolve-WugName $srv`) but calls a STUB Connect-WUGServer that sets the global
    // AND appends one line to $env:VIVRE_TEST_CONNECTS (retrying past cross-runspace file-lock contention).
    private const string TestWorkerTail = """
        function Connect-WUGServer {
            [CmdletBinding()]
            param($ServerUri, $Protocol, $Credential, [switch]$IgnoreSSLErrors)
            $global:WUGBearerHeaders = @{ auth = 'stub' }
            if ($env:VIVRE_TEST_CONNECTS) {
                for ($t = 0; $t -lt 100; $t++) {
                    try { Add-Content -LiteralPath $env:VIVRE_TEST_CONNECTS -Value 'connect' -ErrorAction Stop; break }
                    catch { Start-Sleep -Milliseconds 15 }
                }
            }
        }
        if (-not $global:WUGBearerHeaders) {
            $sec  = ConvertTo-SecureString $env:VIVRE_WUG_PASS -AsPlainText -Force
            $cred = New-Object System.Management.Automation.PSCredential($env:VIVRE_WUG_USER, $sec)
            Connect-WUGServer -ServerUri $env:VIVRE_WUG_SERVER -Protocol https -Credential $cred -IgnoreSSLErrors -ErrorAction Stop | Out-Null
        }
        Resolve-WugName $srv
        """;

    private static string ComposePoolScript(
        IEnumerable<StubDev> inventory,
        IReadOnlyDictionary<string, int>? delays,
        IEnumerable<string>? errNames,
        IEnumerable<string>? throwNames)
    {
        // $resolverText = @' <stub env + stub Get-WUGDevice + REAL resolver + DNS null-override> '@
        // ONE copy: the sequential branch IEXes it; each pooled worker embeds it. Explicit "\n" fencing
        // keeps @' at line-end and '@ at column 0 (no body line begins with '@).
        string resolverBody =
            BuildInventory(inventory) + "\n" +
            BuildNameList("script:ErrNames", errNames) +
            BuildNameList("script:ThrowNames", throwNames) +
            BuildDelays(delays) + "\n" +
            StubGetWugDevice + "\n" +
            WugMaintenance.ResolveFunctionScript + "\n" +
            "function Resolve-WugDnsAddress { param($name) return $null }";

        return
            HeadMirror + "\n" +
            "$resolverText = @'\n" + resolverBody + "\n'@\n" +
            "$workerTail = @'\n" + TestWorkerTail + "\n'@\n" +
            WugMaintenance.StateResolveLoopScript;
    }

    private static async Task<PoolRun> RunPoolAsync(
        IEnumerable<StubDev> inventory,
        int? concurrency,
        IEnumerable<string> names,
        IReadOnlyDictionary<string, int>? delays = null,
        IEnumerable<string>? errNames = null,
        IEnumerable<string>? throwNames = null,
        string? connectsFile = null,
        string? timelineFile = null,
        bool useRealModule = false,
        string? moduleProbeFile = null)
    {
        string script = ComposePoolScript(inventory, delays, errNames, throwNames);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VIVRE_WUG_NAMES"]  = string.Join("\n", names),
            ["VIVRE_WUG_USER"]   = "u",
            ["VIVRE_WUG_PASS"]   = "p",
            ["VIVRE_WUG_SERVER"] = "wug.test",
        };
        // Plumbing tests carry a lightweight stub module through the SAME ISS.ImportPSModule path (the T2
        // seam) so pooled runspaces don't each cold-load the real WhatsUpGoldPS (~8s). The one smoke test
        // passes useRealModule:true to omit the override, letting the production import fire. The override
        // is inert on the sequential (N<=1) branch, which opens no pool — safe to set uniformly.
        if (!useRealModule) { RequireStubModule(); env["VIVRE_WUG_MODULE_OVERRIDE"] = StubModulePath; }
        if (concurrency is int c) { env["VIVRE_WUG_CONCURRENCY"] = c.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        if (connectsFile is not null) { env["VIVRE_TEST_CONNECTS"] = connectsFile; }
        if (timelineFile is not null) { env["VIVRE_TEST_TIMELINE"] = timelineFile; }
        if (moduleProbeFile is not null) { env["VIVRE_TEST_MODULEPROBE"] = moduleProbeFile; }

        var devices = new List<WugDeviceState>();
        var gate = new object();
        var sw = Stopwatch.StartNew();
        TimeSpan firstDeviceAt = TimeSpan.Zero;

        var (stdout, stderr) = await WugMaintenance.RunPreflightProcessAsync(
            script, env, TimeSpan.FromSeconds(120), CancellationToken.None,
            onDeviceLine: line =>
            {
                WugDeviceState? d = WugMaintenance.ParseDeviceLine(line);
                if (d is not null)
                {
                    lock (gate)
                    {
                        if (devices.Count == 0) { firstDeviceAt = sw.Elapsed; }
                        devices.Add(d);
                    }
                }
            });

        TimeSpan returnedAt = sw.Elapsed;
        WugMaintenanceStateResult summary = WugMaintenance.ParseMaintenanceState(stdout, stderr);
        List<WugDeviceState> snapshot;
        lock (gate) { snapshot = new List<WugDeviceState>(devices); }
        return new PoolRun(snapshot, summary, firstDeviceAt, returnedAt, stdout, stderr);
    }

    private static WugDeviceState DeviceFor(IReadOnlyList<WugDeviceState> devices, string name)
        => Assert.Single(devices, d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    // A small fleet whose bare inputs (box1..boxN) each match one FQDN device by name.
    private static StubDev[] Fleet(int n)
    {
        var devs = new StubDev[n];
        for (int i = 1; i <= n; i++)
        {
            // Alternate Maintenance / Up so the tri-state read is exercised both ways.
            string best = (i % 2 == 0) ? "Maintenance" : "Up";
            devs[i - 1] = new StubDev($"box{i}.employees.root.local", $"box{i}", $"10.0.0.{i}", best);
        }
        return devs;
    }

    private static string[] BareNames(int n) => Enumerable.Range(1, n).Select(i => $"box{i}").ToArray();

    // ── Seam validation: the recomposed StateScript AND the (unchanged) Script both parse under real 5.1 ─

    private const string ParseCheckScript = """
        $errs = $null; $tokens = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($env:VIVRE_PARSE_TARGET, [ref]$tokens, [ref]$errs)
        if ($errs -and $errs.Count -gt 0) {
            foreach ($e in $errs) { Write-Output ("PARSEERR line " + $e.Extent.StartLineNumber + ": " + $e.Message) }
        } else {
            Write-Output "PARSE_OK"
        }
        """;

    private static async Task AssertParsesUnder51(string script)
    {
        string target = Path.Combine(Path.GetTempPath(), $"Vivre_WugParse_{Guid.NewGuid():N}.ps1");
        await WugMaintenance.WritePs51ScriptAsync(target, script, CancellationToken.None);
        try
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["VIVRE_PARSE_TARGET"] = target };
            var (stdout, stderr) = await WugMaintenance.RunPreflightProcessAsync(
                ParseCheckScript, env, TimeSpan.FromSeconds(60), CancellationToken.None);
            Assert.False(stdout.Contains("PARSEERR"), $"Composed script had parse errors under 5.1:\n{stdout}\n{stderr}");
            Assert.Contains("PARSE_OK", stdout);
        }
        finally { try { File.Delete(target); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Recomposed_StateScript_parses_clean_under_51()
    {
        RequirePowerShell51();
        await AssertParsesUnder51(WugMaintenance.StateScript);
    }

    [Fact]
    public async Task Set_path_Script_still_parses_clean_under_51()
    {
        RequirePowerShell51();
        await AssertParsesUnder51(WugMaintenance.Script);
    }

    // ── #1 Pooled streaming: every device line arrives, summary counts correct, first line lands early ──

    [Fact]
    public async Task Pooled_streams_every_device_line_and_first_lands_before_exit()
    {
        RequirePowerShell51();

        string[] names = BareNames(6);
        var delays = names.ToDictionary(n => n, _ => 300);

        PoolRun run = await RunPoolAsync(Fleet(6), concurrency: 2, names, delays: delays);

        // Every device resolved and streamed.
        Assert.Equal(6, run.Devices.Count);
        foreach (string n in names)
        {
            WugDeviceState d = DeviceFor(run.Devices, n);
            Assert.True(d.Matched);
        }
        // box2/box4/box6 are 'Maintenance' (even), box1/3/5 'Up'.
        Assert.True(DeviceFor(run.Devices, "box2").InMaintenance);
        Assert.False(DeviceFor(run.Devices, "box1").InMaintenance);

        // Summary parses with correct counts (clean green run).
        Assert.Equal(0, run.Summary.LookupErrors);
        Assert.Equal(0, run.Summary.Ambiguous);
        Assert.Empty(run.Summary.Unmatched);
        Assert.Null(run.Summary.Error);

        // The FIRST device line landed well before the process exited — proof of live streaming from the pool.
        Assert.True(run.ReturnedAt - run.FirstDeviceAt >= TimeSpan.FromMilliseconds(250),
            $"First device at {run.FirstDeviceAt.TotalSeconds:F2}s, returned at {run.ReturnedAt.TotalSeconds:F2}s — expected a clear streaming gap.");
    }

    // ── #2 Connect ONCE PER RUNSPACE (trap T2): 8 names at N=2 => 1..2 connects (once per runspace USED, « 8 — never per lookup) ─

    [Fact]
    public async Task Pooled_connects_once_per_runspace_not_per_lookup()
    {
        RequirePowerShell51();

        string connectsFile = Path.Combine(Path.GetTempPath(), $"Vivre_WugConnects_{Guid.NewGuid():N}.txt");
        File.WriteAllText(connectsFile, string.Empty);
        try
        {
            string[] names = BareNames(8);
            // A modest delay makes it LIKELY both pool runspaces participate (each holding a lookup long enough
            // that a second runspace spins up) — but the pool never GUARANTEES it: a fast runspace 1 may legally
            // win the startup race and drain all 8 items itself, yielding a single connect. The assertions below
            // tolerate exactly that (1..2), because the property under test is once-per-runspace, not per-lookup.
            var delays = names.ToDictionary(n => n, _ => 200);

            PoolRun run = await RunPoolAsync(Fleet(8), concurrency: 2, names, delays: delays, connectsFile: connectsFile);

            Assert.Equal(8, run.Devices.Count);   // all resolved

            int connects = File.ReadAllLines(connectsFile).Count(l => !string.IsNullOrWhiteSpace(l));
            // A healthy pool connects ONCE PER RUNSPACE ACTUALLY USED, never once per lookup. RunspacePool
            // is min=1/max=N: the max is a cap, not a target — a fast first runspace can legitimately win
            // the startup race and drain the whole batch, so 1 connect is as correct as 2. The load-bearing
            // invariant is that connects NEVER scale with the lookup count: dropping the
            // `if (-not $global:WUGBearerHeaders)` guard (a per-lookup reconnect) gives connects == 8 here,
            // which both assertions below fail loudly. (The guard's presence in the PRODUCTION tail is
            // separately string-locked by WugConcurrencyTests.)
            Assert.InRange(connects, 1, 2);                 // once per runspace ACTUALLY USED — pool max is a cap, not a target
            Assert.True(connects < names.Length,            // the real invariant: connects never scale with lookups
                $"Expected connects « lookups (once per runspace, not per lookup): got {connects} for {names.Length} lookups.");
        }
        finally { try { File.Delete(connectsFile); } catch { /* best-effort */ } }
    }

    // ── #3 Per-worker error isolation (trap T5): one throwing lookup can't abort the other 323 ──────────

    [Fact]
    public async Task Pooled_one_worker_throw_is_isolated_as_unknown_others_still_resolve()
    {
        RequirePowerShell51();

        string[] names = new[] { "box1", "box2", "boombox", "box3", "box4" };
        PoolRun run = await RunPoolAsync(
            Fleet(4), concurrency: 2, names,
            throwNames: new[] { "boombox" });

        // The throwing lookup reads UNKNOWN (matched:true / inMaintenance:null), NEVER "no matching device".
        WugDeviceState bad = DeviceFor(run.Devices, "boombox");
        Assert.True(bad.Matched);
        Assert.Null(bad.InMaintenance);
        Assert.DoesNotContain("boombox", run.Summary.Unmatched);

        Assert.True(run.Summary.LookupErrors >= 1);
        Assert.False(string.IsNullOrWhiteSpace(run.Summary.Error));

        // ALL the other names still resolved correctly (the run completed).
        Assert.Equal(5, run.Devices.Count);
        Assert.True(DeviceFor(run.Devices, "box2").InMaintenance);   // even => Maintenance
        Assert.False(DeviceFor(run.Devices, "box1").InMaintenance);  // odd => Up
        Assert.True(DeviceFor(run.Devices, "box4").InMaintenance);
        Assert.False(DeviceFor(run.Devices, "box3").InMaintenance);
    }

    // ── #4 Sequential inertness: concurrency absent == an N=2 run (data equality) + ZERO worker connects ─

    [Fact]
    public async Task Sequential_absent_concurrency_matches_pooled_and_makes_no_worker_connects()
    {
        RequirePowerShell51();

        // A mix: matched names, one erroring (LookupError), one unmatched (NoDevice via the DNS null-override).
        StubDev[] fleet = Fleet(4);
        string[] names = new[] { "box1", "box2", "errbox", "box3", "ghostbox", "box4" };
        var errNames = new[] { "errbox" };

        string seqConnects = Path.Combine(Path.GetTempPath(), $"Vivre_WugSeq_{Guid.NewGuid():N}.txt");
        string poolConnects = Path.Combine(Path.GetTempPath(), $"Vivre_WugPool_{Guid.NewGuid():N}.txt");
        File.WriteAllText(seqConnects, string.Empty);
        File.WriteAllText(poolConnects, string.Empty);
        try
        {
            // Sequential: concurrency omitted entirely (env var absent => branch chooses 1).
            PoolRun seq = await RunPoolAsync(fleet, concurrency: null, names, errNames: errNames, connectsFile: seqConnects);
            // Pooled N=2 over the SAME inventory + names.
            PoolRun pool = await RunPoolAsync(fleet, concurrency: 2, names, errNames: errNames, connectsFile: poolConnects);

            // Order-independent data equality of the per-device outcomes.
            Assert.Equal(seq.Devices.Count, pool.Devices.Count);
            foreach (WugDeviceState s in seq.Devices)
            {
                WugDeviceState p = DeviceFor(pool.Devices, s.Name);
                Assert.Equal(s.Matched, p.Matched);
                Assert.Equal(s.InMaintenance, p.InMaintenance);
                Assert.Equal(s.MatchedByIp, p.MatchedByIp);
            }

            // Summary counts equal (order-independent).
            Assert.Equal(seq.Summary.LookupErrors, pool.Summary.LookupErrors);
            Assert.Equal(seq.Summary.Ambiguous, pool.Summary.Ambiguous);
            Assert.Equal(seq.Summary.MatchedByIp, pool.Summary.MatchedByIp);
            Assert.Equal(
                seq.Summary.Unmatched.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                pool.Summary.Unmatched.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            Assert.Equal(1, seq.Summary.LookupErrors);            // errbox
            Assert.Contains("ghostbox", seq.Summary.Unmatched);   // NoDevice

            // The sequential branch uses the MAIN-scope resolver (IEX) — it spawns no workers, so it never
            // ran the connect-guard tail. Zero connect lines.
            int seqConnectCount = File.ReadAllLines(seqConnects).Count(l => !string.IsNullOrWhiteSpace(l));
            Assert.Equal(0, seqConnectCount);
        }
        finally
        {
            try { File.Delete(seqConnects); } catch { /* best-effort */ }
            try { File.Delete(poolConnects); } catch { /* best-effort */ }
        }
    }

    // ── #5 Parallelism proof: at N=2 lookups run CONCURRENTLY (overlapping execution); at N=1 they don't ─
    //
    // A raw wall-clock "N=2 beats N=1" assertion is fragile: each runspace pays a per-runspace startup
    // cost (here the lightweight stub-module import via the T2 seam; in production the real WhatsUpGoldPS
    // cold-load ~8s, amortised over ~162 lookups/runspace) plus process-launch jitter, which can swamp the
    // parallelisable work for the handful of lookups a unit test can afford. So instead of timing the whole
    // run, this proves the THING that makes the run faster — that two lookups actually execute at the same
    // time — by recording each stub lookup's [start,end] interval and checking for OVERLAP. Immune to the
    // one-time per-runspace startup (which happens before any lookup body runs). A long delay guarantees the
    // first runspace is still busy when the second finishes warming up, so their intervals must overlap; the
    // sequential branch runs one at a time, so none can.

    private static int OverlappingPairs(string timelineFile)
    {
        var intervals = File.ReadAllLines(timelineFile)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(' '))
            .Where(p => p.Length == 2)
            .Select(p => (Start: long.Parse(p[0]), End: long.Parse(p[1])))
            .ToArray();

        int overlaps = 0;
        for (int i = 0; i < intervals.Length; i++)
        {
            for (int j = i + 1; j < intervals.Length; j++)
            {
                // Half-open overlap: a starts before b ends AND b starts before a ends.
                if (intervals[i].Start < intervals[j].End && intervals[j].Start < intervals[i].End) { overlaps++; }
            }
        }
        return overlaps;
    }

    [Fact]
    public async Task Pooled_runs_lookups_concurrently_sequential_does_not()
    {
        RequirePowerShell51();

        string[] names = BareNames(2);
        // Long enough to outlast the 2nd runspace's STUB-module warmup (well under a second via the
        // VIVRE_WUG_MODULE_OVERRIDE seam) with generous margin — the old 5000ms was sized for the real
        // module's ~8s cold-load, which these plumbing tests no longer pay.
        var delays = names.ToDictionary(n => n, _ => 1500);

        string poolTimeline = Path.Combine(Path.GetTempPath(), $"Vivre_WugTL_pool_{Guid.NewGuid():N}.txt");
        string seqTimeline  = Path.Combine(Path.GetTempPath(), $"Vivre_WugTL_seq_{Guid.NewGuid():N}.txt");
        File.WriteAllText(poolTimeline, string.Empty);
        File.WriteAllText(seqTimeline, string.Empty);
        try
        {
            PoolRun pool = await RunPoolAsync(Fleet(2), concurrency: 2, names, delays: delays, timelineFile: poolTimeline);
            PoolRun seq  = await RunPoolAsync(Fleet(2), concurrency: 1, names, delays: delays, timelineFile: seqTimeline);

            Assert.Equal(2, pool.Devices.Count);
            Assert.Equal(2, seq.Devices.Count);

            // N=2: the two lookups ran at the same time — their execution intervals overlap. (Same runspace-
            // startup race as test #2's connect-count, but here the 1500ms delay dwarfs runspace 2's warmup —
            // usually well under a second, occasionally slower, which is the very race that made #2 flaky at
            // 200ms — so runspace 2 reliably wins a lookup while runspace 1 is still busy.)
            Assert.True(OverlappingPairs(poolTimeline) >= 1,
                "At N=2 the two lookups should execute concurrently (overlapping intervals).");
            // N=1: the sequential branch runs one lookup at a time — no intervals overlap.
            Assert.Equal(0, OverlappingPairs(seqTimeline));
        }
        finally
        {
            try { File.Delete(poolTimeline); } catch { /* best-effort */ }
            try { File.Delete(seqTimeline); } catch { /* best-effort */ }
        }
    }

    // ── #6 Degradation warning: an in-run slowdown surfaces "slowed during the run"; a flat run does not ─

    [Fact]
    public async Task Degradation_warning_fires_on_slowdown_and_is_absent_on_a_flat_run()
    {
        RequirePowerShell51();

        // N=1 (deterministic input order). First 5 fast (~60ms), last 5 slow (~400ms): the baseline sits
        // comfortably above the script's 50ms jitter floor and the avg (~230ms) comfortably above 2x the
        // baseline, at about half the runtime of the earlier 100/700 sizing.
        string[] names = BareNames(10);
        var slowing = new Dictionary<string, int>();
        for (int i = 1; i <= 10; i++) { slowing[$"box{i}"] = (i <= 5) ? 60 : 400; }

        PoolRun slow = await RunPoolAsync(Fleet(10), concurrency: 1, names, delays: slowing);
        Assert.Equal(10, slow.Devices.Count);
        Assert.NotNull(slow.Summary.Error);
        Assert.Contains("slowed during the run", slow.Summary.Error!);

        // A clean, flat run (all ~60ms, still above the 50ms floor) => no slowdown text, no error.
        var flat = names.ToDictionary(n => n, _ => 60);
        PoolRun even = await RunPoolAsync(Fleet(10), concurrency: 1, names, delays: flat);
        Assert.Equal(10, even.Devices.Count);
        if (even.Summary.Error is not null)
        {
            Assert.DoesNotContain("slowed during the run", even.Summary.Error);
        }
    }

    // ── #7 (SMOKE) No override => the PRODUCTION branch fires => each pool runspace imports the REAL module ─
    //
    // The single test that pays the real WhatsUpGoldPS cold-load, guarding the production ISS import path
    // the other tests deliberately bypass via VIVRE_WUG_MODULE_OVERRIDE. Pooled N=2, behaviour still stubbed
    // by the resolver-text Get-WUGDevice — but with NO override, so the T2 line's production
    // `elseif (Get-Module -ListAvailable WhatsUpGoldPS) { $iss.ImportPSModule('WhatsUpGoldPS') }` runs and
    // each worker runspace genuinely loads the module. The gated stub Get-WUGDevice writes
    // `[bool](Get-Module WhatsUpGoldPS)` (True only if the real module is loaded in THAT runspace) to a temp
    // file; we assert every probe line reads True. Skip-guard: fail with a clear message if the module is
    // absent (it is always present on the dev box).

    [Fact]
    public async Task Real_module_smoke_pool_runspaces_import_the_actual_WhatsUpGoldPS()
    {
        RequirePowerShell51();
        Assert.True(await WhatsUpGoldModuleInstalledAsync(),
            "WhatsUpGoldPS is not installed on this machine — the real-module smoke test requires it (it is always present on the dev box). Install it once: Install-Module WhatsUpGoldPS -Scope CurrentUser.");

        string probeFile = Path.Combine(Path.GetTempPath(), $"Vivre_WugModProbe_{Guid.NewGuid():N}.txt");
        File.WriteAllText(probeFile, string.Empty);
        try
        {
            string[] names = BareNames(2);
            // useRealModule:true => NO VIVRE_WUG_MODULE_OVERRIDE => production ImportPSModule('WhatsUpGoldPS').
            PoolRun run = await RunPoolAsync(
                Fleet(2), concurrency: 2, names, useRealModule: true, moduleProbeFile: probeFile);

            // The run still succeeds end to end (behaviour stubbed via the resolver text).
            Assert.Equal(2, run.Devices.Count);
            Assert.True(DeviceFor(run.Devices, "box2").InMaintenance);    // even => Maintenance
            Assert.False(DeviceFor(run.Devices, "box1").InMaintenance);   // odd => Up
            Assert.Equal(0, run.Summary.LookupErrors);
            Assert.Empty(run.Summary.Unmatched);

            // The REAL module was genuinely imported into the worker runspaces (proves the production T2
            // branch fired, not a stub override): every probe line is True, none False.
            string[] probes = File.ReadAllLines(probeFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.NotEmpty(probes);
            Assert.All(probes, p => Assert.Equal("True", p.Trim()));
        }
        finally { try { File.Delete(probeFile); } catch { /* best-effort */ } }
    }
}

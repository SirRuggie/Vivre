using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Real <c>powershell.exe</c> (Windows PowerShell 5.1) tests for the SHARED name resolver
/// (<see cref="WugMaintenance.ResolveFunctionScript"/>) — the FQDN-aware, error-aware, no-silent-[0]
/// matcher that replaced the dead <c>$_.displayName -eq $srv</c> verify.
///
/// <para>Each test composes a script = a STUB <c>Get-WUGDevice</c> (server-side SUBSTRING search over a
/// canned inventory built with the REAL fleet shape: <c>name</c> = FQDN, <c>hostName</c> mixed bare/FQDN,
/// <c>networkAddress</c> = IP, and NO <c>displayName</c> property) + the real
/// <see cref="WugMaintenance.ResolveFunctionScript"/> + an optional DNS stub (redefining the overridable
/// <c>Resolve-WugDnsAddress</c>) + a driver that mirrors <c>StateScript</c>'s resolve→emit loop. The
/// device lines stream through the real launcher and are parsed by the real
/// <see cref="WugMaintenance.ParseDeviceLine"/>; the summary by
/// <see cref="WugMaintenance.ParseMaintenanceState"/> — so the whole streaming + parse contract is
/// exercised, not a re-implementation.</para>
///
/// <para>ALL in ONE class ON PURPOSE (xUnit serializes within a class). These pass names via the
/// PER-CHILD env dictionary — they never mutate process-level env — but the serial grouping keeps the
/// spawned-process count sane. The composed scripts are ASCII-only; the launcher writes them
/// UTF-8-with-BOM internally. Time bounds are generous — these assert behavior, not speed.</para>
/// </summary>
public class WugResolverProcessTests
{
    private static string PsExePath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static void RequirePowerShell51()
        => Assert.True(File.Exists(PsExePath),
            $"Windows PowerShell 5.1 was not found at {PsExePath}. These process tests require it (it is always present on the dev box).");

    // A canned WUG device. Best null => no bestState/worstState props (state UNKNOWN); HostName null =>
    // no hostName prop. displayName is NEVER emitted — mirrors the -View overview objects on the fleet.
    private sealed record StubDev(string Name, string? HostName, string Ip, string? Best);

    // ── PS composition ──────────────────────────────────────────────────────────────────────────────

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

    private static string BuildErrNames(IEnumerable<string>? errNames)
    {
        string joined = errNames is null ? "" : string.Join(",", System.Linq.Enumerable.Select(errNames, PsQuote));
        return "$script:ErrNames = @(" + joined + ")\n";
    }

    private static string BuildDnsOverride(IReadOnlyDictionary<string, string>? dns)
    {
        if (dns is null) { return ""; }   // keep the real Resolve-WugDnsAddress
        var sb = new StringBuilder();
        sb.AppendLine("$script:Dns = @{");
        foreach (var kv in dns) { sb.Append("  ").Append(PsQuote(kv.Key)).Append(" = ").Append(PsQuote(kv.Value)).AppendLine(); }
        sb.AppendLine("}");
        // Redefine AFTER ResolveFunctionScript so THIS wins (Resolve-WugName resolves it at call time).
        sb.AppendLine("function Resolve-WugDnsAddress { param($name) if ($script:Dns.ContainsKey($name)) { return $script:Dns[$name] } return $null }");
        return sb.ToString();
    }

    // The stub Get-WUGDevice: an ADVANCED function (so it honours -ErrorAction / -ErrorVariable exactly
    // like the real cmdlet) that Write-Errors for names in $script:ErrNames, else returns every inventory
    // item whose name/hostName/networkAddress SUBSTRING-contains the SearchValue (WUG's server-side rule).
    private const string StubGetWugDevice = """
        function Get-WUGDevice {
            [CmdletBinding()]
            param([string]$SearchValue, [string]$View)
            if ($script:ErrNames -contains $SearchValue) { Write-Error "stub search failed: $SearchValue"; return }
            $sv = $SearchValue
            $script:Inv | Where-Object {
                (($_.PSObject.Properties.Name -contains 'name') -and $_.name -like "*$sv*") -or
                (($_.PSObject.Properties.Name -contains 'hostName') -and $_.hostName -like "*$sv*") -or
                (($_.PSObject.Properties.Name -contains 'networkAddress') -and $_.networkAddress -like "*$sv*")
            }
        }
        """;

    // Mirrors StateScript's resolve→emit→summary loop (ASCII '-' instead of the shipped em-dash).
    private const string Driver = """
        $ErrorActionPreference = 'Stop'
        function Emit($r) { Write-Output ("__WUGRESULT__" + ($r | ConvertTo-Json -Compress -Depth 4)) }
        function EmitDevice($e) { Write-Output ("__WUGDEV__" + ($e | ConvertTo-Json -Compress -Depth 3)) }
        $names  = @($env:VIVRE_WUG_NAMES -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $result = [ordered]@{ ok = $false; devices = @(); unmatched = @(); error = $null; lookupErrors = 0; ambiguous = 0; matchedByIp = 0 }
        $devices = @(); $unmatched = @(); $lookupErrors = 0; $ambiguous = 0; $matchedByIp = 0; $resolvedStates = 0; $firstErr = $null
        foreach ($srv in $names) {
            $r = Resolve-WugName $srv
            if ($r.outcome -eq 'MatchedByName' -or $r.outcome -eq 'MatchedByIp') {
                $match = $r.device
                $state = $null
                $hasBest  = $match.PSObject.Properties.Name -contains 'bestState'
                $hasWorst = $match.PSObject.Properties.Name -contains 'worstState'
                $bestSet  = $hasBest  -and -not [string]::IsNullOrWhiteSpace($match.bestState)
                $worstSet = $hasWorst -and -not [string]::IsNullOrWhiteSpace($match.worstState)
                if ($bestSet -or $worstSet) { $state = ($match.bestState -eq 'Maintenance' -or $match.worstState -eq 'Maintenance') }
                $devices += [ordered]@{ name = $srv; inMaintenance = $state }
                $dev = [ordered]@{ name = $srv; matched = $true; inMaintenance = $state }
                if ($r.outcome -eq 'MatchedByIp') { $dev.matchedByIp = $true; $matchedByIp++ }
                EmitDevice $dev
                $resolvedStates++
            } elseif ($r.outcome -eq 'LookupError') {
                $lookupErrors++; if ($null -eq $firstErr) { $firstErr = $r.error }
                EmitDevice ([ordered]@{ name = $srv; matched = $true; inMaintenance = $null })
            } elseif ($r.outcome -eq 'Ambiguous') {
                $ambiguous++
                EmitDevice ([ordered]@{ name = $srv; matched = $true; inMaintenance = $null })
            } else {
                $unmatched += $srv
                EmitDevice ([ordered]@{ name = $srv; matched = $false; inMaintenance = $null })
            }
        }
        $result.devices = @($devices)
        $result.unmatched = @($unmatched)
        $result.lookupErrors = $lookupErrors
        $result.ambiguous = $ambiguous
        $result.matchedByIp = $matchedByIp
        $result.ok = $true
        if ($lookupErrors -gt 0) { $result.error = "$lookupErrors of $($names.Count) lookups failed - $firstErr" }
        elseif ($resolvedStates -eq 0 -and $names.Count -gt 0) { $result.error = "None of the $($names.Count) machine(s) matched a WhatsUp Gold device ($($unmatched.Count) unmatched, $ambiguous ambiguous)." }
        Emit $result
        """;

    private static async Task<(IReadOnlyList<WugDeviceState> Devices, WugMaintenanceStateResult Summary)> RunResolverAsync(
        IEnumerable<StubDev> inventory,
        IReadOnlyDictionary<string, string>? dns,
        IEnumerable<string>? errNames,
        params string[] names)
    {
        string script =
            "$ErrorActionPreference = 'Stop'\n" +
            BuildInventory(inventory) + "\n" +
            BuildErrNames(errNames) + "\n" +
            StubGetWugDevice + "\n" +
            WugMaintenance.ResolveFunctionScript + "\n" +
            BuildDnsOverride(dns) + "\n" +
            Driver;

        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["VIVRE_WUG_NAMES"] = string.Join("\n", names) };

        var devices = new List<WugDeviceState>();
        var gate = new object();
        var (stdout, stderr) = await WugMaintenance.RunPreflightProcessAsync(
            script, env, TimeSpan.FromSeconds(60), CancellationToken.None,
            onDeviceLine: line =>
            {
                WugDeviceState? d = WugMaintenance.ParseDeviceLine(line);
                if (d is not null) { lock (gate) { devices.Add(d); } }
            });

        WugMaintenanceStateResult summary = WugMaintenance.ParseMaintenanceState(stdout, stderr);
        List<WugDeviceState> snapshot;
        lock (gate) { snapshot = new List<WugDeviceState>(devices); }
        return (snapshot, summary);
    }

    private static WugDeviceState DeviceFor(IReadOnlyList<WugDeviceState> devices, string name)
        => Assert.Single(devices, d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    // ── Operator scenario 1: the still-works case (bare input vs FQDN-registered device) ─────────────

    [Fact]
    public async Task Bare_name_matches_its_FQDN_registered_device_by_name()
    {
        RequirePowerShell51();

        var (devices, summary) = await RunResolverAsync(
            new[] { new StubDev("APVHOP.EMPLOYEES.ROOT.local", "APVHOP", "10.1.1.5", "Maintenance") },
            dns: null, errNames: null, "APVHOP");

        WugDeviceState d = DeviceFor(devices, "APVHOP");
        Assert.True(d.Matched);
        Assert.True(d.InMaintenance);            // read from APVHOP's own bestState
        Assert.False(d.MatchedByIp);
        Assert.Null(summary.Error);              // clean green run
        Assert.Empty(summary.Unmatched);
        Assert.Equal(0, summary.LookupErrors);
    }

    // ── Operator scenario 2: prefix collision — exact-match the OWN FQDN, never $results[0] ──────────

    [Fact]
    public async Task Prefix_collision_matches_own_fqdn_not_the_first_result()
    {
        RequirePowerShell51();

        // Inventory ORDER puts APVSQL10 first, so the old $results[0] would have picked APVSQL10 (Up).
        // Only APVSQL1 is 'Maintenance' — so InMaintenance==true PROVES APVSQL1 (its own FQDN) was chosen.
        var (devices, summary) = await RunResolverAsync(
            new[]
            {
                new StubDev("APVSQL10.employees.root.local", "APVSQL10", "10.2.0.10", "Up"),
                new StubDev("APVSQL11.employees.root.local", "APVSQL11", "10.2.0.11", "Up"),
                new StubDev("APVSQL1.employees.root.local",  "APVSQL1",  "10.2.0.1",  "Maintenance"),
            },
            dns: null, errNames: null, "APVSQL1");

        WugDeviceState d = DeviceFor(devices, "APVSQL1");
        Assert.True(d.Matched);
        Assert.True(d.InMaintenance);            // APVSQL1's state, NOT APVSQL10's (which would be false)
        Assert.False(d.MatchedByIp);
        Assert.Null(summary.Error);
    }

    // ── Operator scenario 3: renamed-box rescue via DNS→IP → MatchedByIp on the wire ─────────────────

    [Fact]
    public async Task Renamed_box_is_rescued_by_ip_and_flagged_matchedByIp()
    {
        RequirePowerShell51();

        // Name search returns hits (both contain 'root') but NONE exact for 'OLDNAME'; DNS maps OLDNAME
        // to the renamed box's IP; the IP search finds it by exact networkAddress => MatchedByIp.
        var (devices, summary) = await RunResolverAsync(
            new[]
            {
                new StubDev("unrelated.employees.root.local",     "unrelated",     "10.9.9.9", "Up"),
                new StubDev("renamed-in-wug.employees.root.local", "renamed-in-wug", "10.3.0.7", "Maintenance"),
            },
            dns: new Dictionary<string, string> { ["OLDNAME"] = "10.3.0.7" },
            errNames: null, "OLDNAME");

        WugDeviceState d = DeviceFor(devices, "OLDNAME");
        Assert.True(d.Matched);
        Assert.True(d.MatchedByIp);              // the wire flag is set
        Assert.True(d.InMaintenance);            // read from the rescued device
        Assert.Equal(1, summary.MatchedByIp);
        Assert.Null(summary.Error);
        Assert.Empty(summary.Unmatched);
    }

    // ── Operator scenario 4: genuine junk name — clean-empty everywhere => matched:false, NO error ────

    [Fact]
    public async Task Junk_name_reads_no_matching_device_with_no_error()
    {
        RequirePowerShell51();

        var (devices, summary) = await RunResolverAsync(
            new[] { new StubDev("APVHOP.employees.root.local", "APVHOP", "10.1.1.5", "Up") },
            dns: null, errNames: null, "TOTALLY-NOT-THERE");

        WugDeviceState d = DeviceFor(devices, "TOTALLY-NOT-THERE");
        Assert.False(d.Matched);                 // honest "no matching device"
        Assert.Null(d.InMaintenance);
        Assert.Contains("TOTALLY-NOT-THERE", summary.Unmatched);
        // All-failed guard fires (zero resolved) but it's NOT a lookup error and NOT a fabricated state.
        Assert.Equal(0, summary.LookupErrors);
    }

    // ── Bug 1: a WUG search ERROR => state unknown, counted, summary error — NEVER "no matching device" ─

    [Fact]
    public async Task Search_error_reads_state_unknown_and_is_counted_not_unmatched()
    {
        RequirePowerShell51();

        // BOOMBOX errors on the NAME search AND has a DNS mapping — proving an errored name search does
        // NOT fall through to the IP path (a struggling server gets no second call).
        var (devices, summary) = await RunResolverAsync(
            new[] { new StubDev("apvhop.employees.root.local", "APVHOP", "10.1.1.5", "Up") },
            dns: new Dictionary<string, string> { ["BOOMBOX"] = "10.1.1.5" },
            errNames: new[] { "BOOMBOX" }, "BOOMBOX");

        WugDeviceState d = DeviceFor(devices, "BOOMBOX");
        Assert.True(d.Matched);                  // the row shows a state/unknown, not "no matching device"
        Assert.Null(d.InMaintenance);            // UNKNOWN — never a fabricated false
        Assert.False(d.MatchedByIp);
        Assert.Equal(1, summary.LookupErrors);
        Assert.DoesNotContain("BOOMBOX", summary.Unmatched);   // an errored lookup is NOT unmatched
        Assert.False(string.IsNullOrWhiteSpace(summary.Error)); // summary is NOT clean-green
    }

    // ── Bug 1 mixed: some resolve, one errors — ok/green killed by error, but resolved rows still shown ─

    [Fact]
    public async Task Mixed_success_and_error_still_surfaces_the_error_and_keeps_the_good_row()
    {
        RequirePowerShell51();

        var (devices, summary) = await RunResolverAsync(
            new[] { new StubDev("apvhop.employees.root.local", "APVHOP", "10.1.1.5", "Maintenance") },
            dns: null, errNames: new[] { "BADBOX" }, "APVHOP", "BADBOX");

        Assert.True(DeviceFor(devices, "APVHOP").InMaintenance);          // good row intact
        Assert.Null(DeviceFor(devices, "BADBOX").InMaintenance);          // errored row unknown
        Assert.True(DeviceFor(devices, "BADBOX").Matched);
        Assert.Equal(1, summary.LookupErrors);
        Assert.Contains("lookups failed", summary.Error);                 // surfaces the count
    }

    // ── Ambiguous, no rescue: hits, none exact, DNS null => unknown + ambiguous count, never [0] ──────

    [Fact]
    public async Task Ambiguous_name_without_rescue_reads_unknown_and_is_counted()
    {
        RequirePowerShell51();

        // 'PRODBOX' substring-matches both, exact-matches neither (dot boundary), DNS returns $null.
        // A clean matched sibling keeps resolvedStates>0 so the all-failed guard does NOT fire and we can
        // isolate the ambiguous count with a null summary error.
        var (devices, summary) = await RunResolverAsync(
            new[]
            {
                new StubDev("PRODBOX-A.employees.root.local", "PRODBOX-A", "10.5.0.1", "Up"),
                new StubDev("PRODBOX-B.employees.root.local", "PRODBOX-B", "10.5.0.2", "Up"),
                new StubDev("apvhop.employees.root.local",    "APVHOP",    "10.1.1.5", "Up"),
            },
            dns: new Dictionary<string, string>(), // present-but-empty => DNS always $null
            errNames: null, "PRODBOX", "APVHOP");

        WugDeviceState amb = DeviceFor(devices, "PRODBOX");
        Assert.True(amb.Matched);                // unknown, NOT "no matching device"
        Assert.Null(amb.InMaintenance);
        Assert.False(amb.MatchedByIp);
        Assert.DoesNotContain("PRODBOX", summary.Unmatched);   // ambiguous is NOT unmatched
        Assert.Equal(1, summary.Ambiguous);
        Assert.Null(summary.Error);              // a sibling resolved, so no all-failed / no lookup error
    }

    // ── All-failed guard (state path): every input NoDevice/Ambiguous, zero errors => summary error set ─

    [Fact]
    public async Task All_ambiguous_or_nodevice_trips_the_all_failed_guard_without_a_lookup_error()
    {
        RequirePowerShell51();

        var (_, summary) = await RunResolverAsync(
            new[]
            {
                new StubDev("PRODBOX-A.employees.root.local", "PRODBOX-A", "10.5.0.1", "Up"),
                new StubDev("PRODBOX-B.employees.root.local", "PRODBOX-B", "10.5.0.2", "Up"),
            },
            dns: new Dictionary<string, string>(), errNames: null,
            "PRODBOX", "GHOSTBOX");

        Assert.Equal(0, summary.LookupErrors);
        Assert.Equal(1, summary.Ambiguous);              // PRODBOX
        Assert.Contains("GHOSTBOX", summary.Unmatched);  // GHOSTBOX
        Assert.False(string.IsNullOrWhiteSpace(summary.Error));  // all-failed guard fired
        Assert.Contains("None of the", summary.Error);
    }

    // ── IP-literal input matches by networkAddress (the fourth match clause) ──────────────────────────

    [Fact]
    public async Task Ip_literal_input_matches_by_network_address()
    {
        RequirePowerShell51();

        var (devices, summary) = await RunResolverAsync(
            new[] { new StubDev("apvhop.employees.root.local", "APVHOP", "10.1.1.5", "Maintenance") },
            dns: null, errNames: null, "10.1.1.5");

        WugDeviceState d = DeviceFor(devices, "10.1.1.5");
        Assert.True(d.Matched);
        Assert.True(d.InMaintenance);
        Assert.False(d.MatchedByIp);             // matched during the NAME search via the networkAddress clause
        Assert.Null(summary.Error);
    }

    // ── Pure matcher: normalize helper across FQDN/bare/case/trailing-dot pairs + prefix rejection ────

    [Fact]
    public async Task Test_WugNameMatch_normalizes_fqdn_bare_case_and_trailing_dot()
    {
        RequirePowerShell51();

        // Exercises Test-WugNameMatch DIRECTLY (no substring-search gate), the way the task specifies for
        // the trailing-dot / FQDN-vs-bare variants. Emits one ASCII "RESULT <label> <bool>" line each.
        string script =
            WugMaintenance.ResolveFunctionScript + "\n" +
            """
            function Dev($n,$h,$ip){ [pscustomobject][ordered]@{ name=$n; hostName=$h; networkAddress=$ip } }
            function DevIpOnly($ip){ [pscustomobject]@{ networkAddress=$ip } }
            Write-Output ("RESULT equal "        + [bool](Test-WugNameMatch 'apvhop' (Dev 'APVHOP' 'APVHOP' '10.1.1.5')))
            Write-Output ("RESULT bareVsFqdn "   + [bool](Test-WugNameMatch 'APVHOP' (Dev 'APVHOP.EMPLOYEES.ROOT.local' 'APVHOP' '10.1.1.5')))
            Write-Output ("RESULT fqdnVsBare "   + [bool](Test-WugNameMatch 'apvhop.employees.root.local' (Dev 'APVHOP' 'APVHOP' '10.1.1.5')))
            Write-Output ("RESULT fqdnVsFqdn "   + [bool](Test-WugNameMatch 'APVHOP.EMPLOYEES.ROOT.LOCAL' (Dev 'apvhop.employees.root.local' 'apvhop' '10.1.1.5')))
            Write-Output ("RESULT trailDotQuery "+ [bool](Test-WugNameMatch 'apvhop.employees.root.local.' (Dev 'apvhop.employees.root.local' 'x' '10.1.1.5')))
            Write-Output ("RESULT trailDotStore "+ [bool](Test-WugNameMatch 'apvhop.employees.root.local' (Dev 'apvhop.employees.root.local.' 'x' '10.1.1.5')))
            Write-Output ("RESULT viaHostName "  + [bool](Test-WugNameMatch 'APVWUG' (Dev 'weird.domain' 'APVWUG' '10.1.1.9')))
            Write-Output ("RESULT prefixCollide "+ [bool](Test-WugNameMatch 'APVSQL1' (Dev 'APVSQL10.employees.root.local' 'APVSQL10' '10.2.0.10')))
            Write-Output ("RESULT absentProps "  + [bool](Test-WugNameMatch 'APVHOP' (DevIpOnly '10.1.1.5')))
            Write-Output ("RESULT genuineMiss "  + [bool](Test-WugNameMatch 'NOPE' (Dev 'apvhop.domain' 'apvhop' '10.1.1.5')))
            """;

        var (stdout, _) = await WugMaintenance.RunPreflightProcessAsync(
            script, new Dictionary<string, string>(StringComparer.Ordinal), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Contains("RESULT equal True", stdout);
        Assert.Contains("RESULT bareVsFqdn True", stdout);
        Assert.Contains("RESULT fqdnVsBare True", stdout);
        Assert.Contains("RESULT fqdnVsFqdn True", stdout);
        Assert.Contains("RESULT trailDotQuery True", stdout);
        Assert.Contains("RESULT trailDotStore True", stdout);
        Assert.Contains("RESULT viaHostName True", stdout);
        Assert.Contains("RESULT prefixCollide False", stdout);   // dot boundary rejects the collision
        Assert.Contains("RESULT absentProps False", stdout);     // missing props never throw / never match
        Assert.Contains("RESULT genuineMiss False", stdout);
    }

    // ── IP fall-through classified by the COUNT of EXACT networkAddress matches (the substring-hit fix) ──
    //
    // WUG's SearchValue is a SUBSTRING search: an IP search for x.y.z.10 also returns x.y.z.101 / .109.
    // The resolver must classify by how many rows EXACTLY equal the IP — 1 => MatchedByIp; 0 => the
    // substring-only rows are NOT evidence the box exists (=> NoDevice, unless the NAME search saw hits);
    // 2+ => devices genuinely share the IP => Ambiguous (never a silent first-pick). Live-confirmed on
    // AZRPWDEGWEB (.10 => .109/.108, 0 exact) and AZRLIC8 (.12 => .120/.124, 0 exact).

    [Fact]
    public async Task Ip_search_with_one_exact_amid_substring_siblings_matches_the_exact_device()
    {
        RequirePowerShell51();

        // IP search '10.0.0.10' also returns the .109/.108 substring siblings (Up). Only the EXACT .10 is
        // 'Maintenance', so InMaintenance==true PROVES the exact device (not a substring sibling) was picked.
        var (devices, summary) = await RunResolverAsync(
            new[]
            {
                new StubDev("neighbor-a.employees.root.local", "neighbor-a", "10.0.0.109", "Up"),
                new StubDev("the-right-box.employees.root.local", "the-right-box", "10.0.0.10", "Maintenance"),
                new StubDev("neighbor-b.employees.root.local", "neighbor-b", "10.0.0.108", "Up"),
            },
            dns: new Dictionary<string, string> { ["AZRPWDEGWEB"] = "10.0.0.10" },
            errNames: null, "AZRPWDEGWEB");

        WugDeviceState d = DeviceFor(devices, "AZRPWDEGWEB");
        Assert.True(d.Matched);
        Assert.True(d.MatchedByIp);              // rescued via the DNS→IP fall-through
        Assert.True(d.InMaintenance);            // the EXACT .10's state, NOT a substring sibling's (false)
        Assert.Equal(1, summary.MatchedByIp);
        Assert.Null(summary.Error);
        Assert.Empty(summary.Unmatched);
    }

    [Fact]
    public async Task Ip_search_with_zero_exact_only_substring_rows_reads_no_matching_device()
    {
        RequirePowerShell51();

        // The live AZRPWDEGWEB bug: name search clean-empty, DNS→'10.0.0.10', but WUG only has .109/.108
        // (substring hits, ZERO exact). Those rows are NOT proof the box exists => honest NoDevice, NOT the
        // old Ambiguous "state unknown".
        var (devices, summary) = await RunResolverAsync(
            new[]
            {
                new StubDev("neighbor-a.employees.root.local", "neighbor-a", "10.0.0.109", "Up"),
                new StubDev("neighbor-b.employees.root.local", "neighbor-b", "10.0.0.108", "Up"),
            },
            dns: new Dictionary<string, string> { ["AZRPWDEGWEB"] = "10.0.0.10" },
            errNames: null, "AZRPWDEGWEB");

        WugDeviceState d = DeviceFor(devices, "AZRPWDEGWEB");
        Assert.False(d.Matched);                 // honest "no matching device", not a fabricated unknown
        Assert.Null(d.InMaintenance);
        Assert.False(d.MatchedByIp);
        Assert.Contains("AZRPWDEGWEB", summary.Unmatched);
        Assert.Equal(0, summary.LookupErrors);
        Assert.Equal(0, summary.Ambiguous);      // substring-only IP rows must NOT count as an ambiguity
    }

    [Fact]
    public async Task Ip_search_with_two_exact_matches_reads_ambiguous_never_first_pick()
    {
        RequirePowerShell51();

        // Two WUG devices GENUINELY share 10.0.0.12 (both networkAddress -eq the IP). That is real ambiguity
        // => Ambiguous (unknown), never the old silent first-pick. A clean-by-name sibling (APVHOP) keeps
        // resolvedStates>0 so the all-failed guard stays quiet and summary.Error isolates to null.
        var (devices, summary) = await RunResolverAsync(
            new[]
            {
                new StubDev("shared-a.employees.root.local", "shared-a", "10.0.0.12", "Up"),
                new StubDev("shared-b.employees.root.local", "shared-b", "10.0.0.12", "Maintenance"),
                new StubDev("apvhop.employees.root.local",   "APVHOP",   "10.9.9.9",  "Up"),
            },
            dns: new Dictionary<string, string> { ["AZRLIC8"] = "10.0.0.12" },
            errNames: null, "AZRLIC8", "APVHOP");

        WugDeviceState amb = DeviceFor(devices, "AZRLIC8");
        Assert.True(amb.Matched);                // unknown, NOT "no matching device"
        Assert.Null(amb.InMaintenance);          // never picks shared-a or shared-b's state
        Assert.False(amb.MatchedByIp);
        Assert.DoesNotContain("AZRLIC8", summary.Unmatched);
        Assert.Equal(1, summary.Ambiguous);
        Assert.Null(summary.Error);              // APVHOP resolved, so no all-failed / no lookup error
    }

    [Fact]
    public async Task Ip_search_error_reads_lookup_error_never_no_matching_device()
    {
        RequirePowerShell51();

        // Name search clean-empty => DNS→'10.0.0.10' => the IP search itself ERRORS (errNames holds the IP).
        // An errored IP search is state UNKNOWN (LookupError), NEVER a fabricated NoDevice — the b67ed55
        // error-honesty guard on the IP path.
        var (devices, summary) = await RunResolverAsync(
            new[] { new StubDev("unrelated.employees.root.local", "unrelated", "10.5.5.5", "Up") },
            dns: new Dictionary<string, string> { ["AZRPWDEGWEB"] = "10.0.0.10" },
            errNames: new[] { "10.0.0.10" }, "AZRPWDEGWEB");

        WugDeviceState d = DeviceFor(devices, "AZRPWDEGWEB");
        Assert.True(d.Matched);                  // shows a state/unknown, not "no matching device"
        Assert.Null(d.InMaintenance);            // UNKNOWN
        Assert.False(d.MatchedByIp);
        Assert.Equal(1, summary.LookupErrors);
        Assert.DoesNotContain("AZRPWDEGWEB", summary.Unmatched);   // an errored lookup is NOT unmatched
        Assert.Contains("stub search failed", summary.Error);      // carries the real IP-search error text
    }

    // ── Single-source (splice) locks: the ONE ResolveFunctionScript feeds BOTH paths, never forked ──────

    [Fact]
    public void ResolveFunctionScript_is_spliced_verbatim_into_both_set_and_state_paths()
    {
        // The set path (Script) concatenates the const directly; the state path (StateScript) embeds it
        // inside the $resolverText here-string. Either splice breaking (a fork) is a load-bearing regression.
        Assert.Contains(WugMaintenance.ResolveFunctionScript, WugMaintenance.Script);
        Assert.Contains(WugMaintenance.ResolveFunctionScript, WugMaintenance.StateScript);
    }

    [Fact]
    public void Both_paths_carry_the_distinctive_resolver_function_line()
    {
        Assert.Contains("function Resolve-WugName", WugMaintenance.Script);
        Assert.Contains("function Resolve-WugName", WugMaintenance.StateScript);
    }
}

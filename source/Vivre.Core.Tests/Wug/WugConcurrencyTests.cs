using System.Text.RegularExpressions;
using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Pure (no-process) unit tests for the parallel state-read seam: the concurrency clamp, the measured
/// ceiling constant, and the string-locks that guard the recomposed <see cref="WugMaintenance.StateScript"/>
/// against regressions the compiler can't catch (the fan-out trap markers, the single-sourced resolver,
/// the concurrency env var).
/// </summary>
public class WugConcurrencyTests
{
    // ── ClampConcurrency + the measured ceiling ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(-3, 1)]
    [InlineData(int.MaxValue, 4)]
    public void ClampConcurrency_bounds_into_1_to_ceiling(int requested, int expected)
        => Assert.Equal(expected, WugMaintenance.ClampConcurrency(requested));

    [Fact]
    public void StateReadMaxConcurrency_is_the_measured_ceiling_of_4()
        => Assert.Equal(4, WugMaintenance.StateReadMaxConcurrency);

    // ── String locks: the fan-out traps + single-sourced resolver survive any future edit ────────────

    [Fact]
    public void StateResolveLoopScript_raises_the_dotnet_connection_cap_trap_T1()
        => Assert.Contains("DefaultConnectionLimit", WugMaintenance.StateResolveLoopScript);

    [Fact]
    public void StateScript_carries_the_per_runspace_connect_guard()
        => Assert.Contains("if (-not $global:WUGBearerHeaders)", WugMaintenance.StateScript);

    [Fact]
    public void StateScript_reads_the_concurrency_env_var()
        => Assert.Contains("VIVRE_WUG_CONCURRENCY", WugMaintenance.StateScript);

    [Fact]
    public void StateScript_still_emits_the_required_result_marker()
        => Assert.Contains("__WUGRESULT__", WugMaintenance.StateScript);

    [Fact]
    public void StateScript_embeds_the_resolver_exactly_once_no_fork()
    {
        // The ONE ResolveFunctionScript is spliced a single time (into $resolverText); both branches share
        // it (sequential IEXes it, workers embed the same text). A second copy would mean a forked resolver.
        int defs = Regex.Matches(WugMaintenance.StateScript, "function Resolve-WugName").Count;
        Assert.Equal(1, defs);
    }

    [Fact]
    public void StateScript_defines_both_here_string_seams()
    {
        // The single-quoted here-string openers that carry the resolver + worker tail as LITERAL text.
        Assert.Contains("$resolverText = @'", WugMaintenance.StateScript);
        Assert.Contains("$workerTail = @'", WugMaintenance.StateScript);
    }

    [Fact]
    public void StateResolveLoopScript_keeps_the_module_override_test_seam_and_production_import()
    {
        // The T2 test seam (VIVRE_WUG_MODULE_OVERRIDE — NEVER set in production) lets the process tests
        // ride the SAME ImportPSModule path with a lightweight stub instead of cold-loading the real module.
        Assert.Contains("VIVRE_WUG_MODULE_OVERRIDE", WugMaintenance.StateResolveLoopScript);
        // The production import branch must survive untouched: with the override unset it is the sole path
        // that pulls the real WhatsUpGoldPS into each pool runspace.
        Assert.Contains("ImportPSModule", WugMaintenance.StateResolveLoopScript);
        Assert.Contains("$iss.ImportPSModule('WhatsUpGoldPS')", WugMaintenance.StateResolveLoopScript);
    }
}

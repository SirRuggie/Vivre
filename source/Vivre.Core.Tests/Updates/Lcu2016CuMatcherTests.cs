using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="Lcu2016CuMatcher"/> — confidently identifies the single Server 2016 OS cumulative
/// update KB among a scan, fail-safe to null when none / ambiguous, and excludes .NET Framework CUs.
/// </summary>
public class Lcu2016CuMatcherTests
{
    [Fact]
    public void Finds_the_2016_os_cu_kb_from_a_realistic_title()
    {
        (string, string?)[] scan =
        [
            ("2026-06 Cumulative Update for Windows Server 2016 for x64-based Systems (KB5094122)", "5094122"),
            ("Security Intelligence Update for Microsoft Defender Antivirus", "2267602"),
        ];

        Assert.Equal("5094122", Lcu2016CuMatcher.FindCuKb(scan));
    }

    [Fact]
    public void Matches_on_1607_when_title_says_windows_10_version_1607()
    {
        (string, string?)[] scan =
        [
            ("2026-06 Cumulative Update for Windows 10 Version 1607 for x64-based Systems (KB5094122)", "5094122"),
        ];

        Assert.Equal("5094122", Lcu2016CuMatcher.FindCuKb(scan));
    }

    [Fact]
    public void Excludes_dotnet_framework_cumulative_updates()
    {
        // A .NET Framework CU is a separate WUA package, not the OS LCU — must not be matched.
        (string, string?)[] scan =
        [
            ("2026-06 Cumulative Update for .NET Framework 4.8 for Windows Server 2016 (KB5099999)", "5099999"),
        ];

        Assert.Null(Lcu2016CuMatcher.FindCuKb(scan));
    }

    [Fact]
    public void Returns_null_when_no_cu_present()
    {
        (string, string?)[] scan =
        [
            ("Security Intelligence Update for Microsoft Defender Antivirus", "2267602"),
            ("Update for Windows Server 2016 (KB5000000)", "5000000"), // not a "Cumulative Update"
        ];

        Assert.Null(Lcu2016CuMatcher.FindCuKb(scan));
    }

    [Fact]
    public void Returns_null_when_ambiguous_two_distinct_cu_kbs()
    {
        // Two different OS CU KBs in one scan — don't guess which is "the" CU.
        (string, string?)[] scan =
        [
            ("2026-05 Cumulative Update for Windows Server 2016 (KB5090000)", "5090000"),
            ("2026-06 Cumulative Update for Windows Server 2016 (KB5094122)", "5094122"),
        ];

        Assert.Null(Lcu2016CuMatcher.FindCuKb(scan));
    }

    [Fact]
    public void Collapses_duplicate_cu_kb_rows_to_one()
    {
        // Same KB twice (e.g. SSU+LCU rows sharing a KB) is still a confident single match.
        (string, string?)[] scan =
        [
            ("2026-06 Cumulative Update for Windows Server 2016 (KB5094122)", "5094122"),
            ("2026-06 Cumulative Update for Windows Server 2016 (KB5094122)", "KB5094122"),
        ];

        Assert.Equal("5094122", Lcu2016CuMatcher.FindCuKb(scan));
    }

    [Fact]
    public void Ignores_a_cu_row_with_no_kb()
    {
        (string, string?)[] scan =
        [
            ("Cumulative Update for Windows Server 2016", null),
        ];

        Assert.Null(Lcu2016CuMatcher.FindCuKb(scan));
    }

    [Theory]
    [InlineData("KB5094122", "5094122")]
    [InlineData("kb5094122", "5094122")]
    [InlineData("5094122", "5094122")]
    [InlineData("  KB5094122  ", "5094122")]
    public void NormalizeKb_strips_the_prefix(string input, string expected) =>
        Assert.Equal(expected, Lcu2016CuMatcher.NormalizeKb(input));
}

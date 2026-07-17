using Vivre.Core.Configuration;
using Xunit;

namespace Vivre.Core.Tests.Configuration;

/// <summary>
/// The pure month/year label suggester. Fed an ALREADY-LOCALIZED date (the caller does <c>ToLocalTime()</c>) so
/// these assertions are timezone-independent. Mid-month dates avoid any month-boundary ambiguity. The value is a
/// convenience guess from a download date, not an authoritative release month — these only lock the formatting.
/// </summary>
public class MonthTagSuggestionTests
{
    [Fact]
    public void SuggestFrom_formats_month_and_year()
    {
        Assert.Equal("July 2026", MonthTagSuggestion.SuggestFrom(new DateTime(2026, 7, 16)));
    }

    [Fact]
    public void SuggestFrom_formats_a_different_month()
    {
        Assert.Equal("December 2026", MonthTagSuggestion.SuggestFrom(new DateTime(2026, 12, 15)));
    }

    [Fact]
    public void SuggestFrom_null_returns_empty_and_does_not_throw()
    {
        Assert.Equal(string.Empty, MonthTagSuggestion.SuggestFrom(null));
    }
}

/// <summary>
/// <see cref="MonthlyCu.Display"/> — the 2016 panel string. Empty tag yields exactly the legacy "KB / UBR"
/// form; a set tag appends " — {tag}". The tag is a label only and never affects identity.
/// </summary>
public class MonthlyCuDisplayTests
{
    [Fact]
    public void Display_without_month_tag_is_kb_slash_ubr()
    {
        var cu = new MonthlyCu { Kb = "KB5099999", TargetUbr = 9339 };
        Assert.Equal("KB5099999 / 9339", cu.Display);
    }

    [Fact]
    public void Display_with_month_tag_appends_the_label()
    {
        var cu = new MonthlyCu { Kb = "KB5099999", TargetUbr = 9339, MonthTag = "July 2026" };
        Assert.Equal("KB5099999 / 9339 — July 2026", cu.Display);
    }
}

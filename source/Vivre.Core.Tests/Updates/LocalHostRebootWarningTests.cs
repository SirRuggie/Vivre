using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the DISCLOSURE razor behind the "this selection includes the box Vivre runs on" sentence added to
/// the existing reboot confirms.
/// <para>
/// Every test here is PROOF: none of this behaviour existed before — <see cref="LocalHostRebootWarning"/> is
/// new, and before it no reboot path told the operator (or knew) that a target was the Vivre host. Mutating
/// <see cref="LocalHostRebootWarning.TargetsTheVivreHost"/> to <c>false</c> — the old, no-warning behaviour —
/// fails every detection test below.
/// </para>
/// <para>
/// NOTE what is deliberately NOT tested, because it deliberately does not exist: there is no filtering,
/// exclusion, or ordering here. The razor returns text. Which boxes reboot is untouched by this type.
/// </para>
/// </summary>
public class LocalHostRebootWarningTests
{
    /// <summary>PROOF — the local box by every alias <c>HostName.IsLocal</c> accepts trips the warning.
    /// Selecting "localhost" or "." must disclose exactly like selecting the machine by name.</summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData(".")]
    public void Local_alias_in_the_selection_targets_the_vivre_host(string alias) =>
        Assert.True(LocalHostRebootWarning.TargetsTheVivreHost([alias]));

    /// <summary>KNOWN GAP, locked deliberately: <c>HostName.IsLocal</c> matches its aliases with a
    /// case-SENSITIVE pattern (<c>host is "localhost" or …</c>) and only the machine-NAME branch is
    /// case-insensitive — so "LOCALHOST" is not recognised as local. That is pre-existing behaviour of the
    /// shared local-vs-WinRM dispatch detector, not something this disclosure introduced, and it is NOT
    /// papered over here: normalising the input locally would fork "what counts as local" into a second
    /// answer, which is exactly what <c>HostName</c> exists to prevent. If this is ever fixed, fix it in
    /// <c>HostName.IsLocal</c> (which changes remoting dispatch too) and flip this assertion.</summary>
    [Fact]
    public void Uppercased_localhost_alias_is_not_detected_matching_HostName_IsLocal() =>
        Assert.False(LocalHostRebootWarning.TargetsTheVivreHost(["LOCALHOST"]));

    /// <summary>PROOF — the local box selected by its actual machine name (the normal case: the operator's
    /// own workstation is a row in the grid like any other), case-insensitively.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Local_machine_name_in_the_selection_targets_the_vivre_host(bool lowerCase)
    {
        string name = lowerCase ? Environment.MachineName.ToLowerInvariant() : Environment.MachineName;
        Assert.True(LocalHostRebootWarning.TargetsTheVivreHost([name]));
    }

    /// <summary>PROOF — a MIXED selection (remote boxes plus this one) still discloses. This is the dangerous
    /// case: the operator is looking at a list of servers and doesn't notice their own box is in it.</summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void Mixed_selection_containing_the_local_box_targets_the_vivre_host(string local) =>
        Assert.True(LocalHostRebootWarning.TargetsTheVivreHost(["APVSQL1", "APVWEB2", local, "APVDC3"]));

    /// <summary>PROOF — an all-remote selection does NOT warn, so the ordinary fleet reboot confirm reads
    /// exactly as it always has. A disclosure that fires every time discloses nothing.</summary>
    [Fact]
    public void Selection_without_the_local_box_does_not_target_the_vivre_host() =>
        Assert.False(LocalHostRebootWarning.TargetsTheVivreHost(["APVSQL1", "APVWEB2", "APVDC3"]));

    /// <summary>PROOF — an empty or null selection never warns.</summary>
    [Fact]
    public void Empty_or_null_selection_does_not_target_the_vivre_host()
    {
        Assert.False(LocalHostRebootWarning.TargetsTheVivreHost([]));
        Assert.False(LocalHostRebootWarning.TargetsTheVivreHost(null));
    }

    /// <summary>PROOF — a blank / whitespace / null row name is NOT treated as this machine. <c>HostName.IsLocal</c>
    /// answers true for an empty host (correct for its own job: no host means run here), so without the
    /// skip in the razor an unnamed row would raise a false alarm on a purely remote selection.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_row_name_does_not_target_the_vivre_host(string? blank) =>
        Assert.False(LocalHostRebootWarning.TargetsTheVivreHost(["APVSQL1", blank]));

    /// <summary>PROOF — the composed sentence names the box EXPLICITLY (not "this machine") and says what
    /// rebooting it costs: Vivre itself, plus any wave in flight against the other machines.</summary>
    [Fact]
    public void Warning_names_the_host_and_the_cost()
    {
        string text = LocalHostRebootWarning.Warning("APVHOP");

        Assert.Contains("APVHOP", text, StringComparison.Ordinal);
        Assert.DoesNotContain("this machine", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("terminates Vivre", text, StringComparison.Ordinal);
        Assert.Contains("in-flight wave", text, StringComparison.Ordinal);
    }

    /// <summary>PROOF — the warning is TWO SENTENCES, matching the terseness of the confirm text it is
    /// appended to. Locked so a later edit can't grow it into a paragraph nobody reads. (The fixture host
    /// name is dot-free, so full stops == sentences.)</summary>
    [Fact]
    public void Warning_is_two_sentences()
    {
        string text = LocalHostRebootWarning.Warning("APVHOP");

        Assert.Equal(2, text.Count(c => c == '.'));
        Assert.EndsWith(".", text, StringComparison.Ordinal);
    }

    /// <summary>PROOF — the appended-or-nothing form: the real machine name is used even when the operator
    /// selected the box by an alias, and an all-remote selection yields null so the dialog is unchanged.</summary>
    [Fact]
    public void WarningOrNull_uses_the_real_machine_name_and_is_null_for_remote_only()
    {
        string? viaAlias = LocalHostRebootWarning.WarningOrNull(["APVSQL1", "localhost"]);
        Assert.NotNull(viaAlias);
        Assert.Contains(Environment.MachineName, viaAlias, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", viaAlias, StringComparison.Ordinal);

        Assert.Null(LocalHostRebootWarning.WarningOrNull(["APVSQL1", "APVWEB2"]));
    }
}

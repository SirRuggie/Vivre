using Vivre.Core.People;

namespace Vivre.Core.Tests.People;

public class PeopleLookupTests
{
    private static DirectoryPerson Person(bool enabled = true) => new("id", "Michael Smith", "msmith", "msmith@example.test",
        "michael@example.test", "IT", "", "", enabled);
    [Fact]
    public void PastePreservesCommaNamesAndDeduplicatesCase()
    {
        Assert.Equal(new[] { "Smith, Mike", "alice@example.test" }, PeopleInput.Parse(" Smith, Mike\r\nalice@example.test\nSMITH, MIKE\n"));
    }
    [Fact]
    public void LdapSpecialCharactersCannotChangeFilterStructure()
    {
        Assert.Equal(@"\2a\29\28mail=\2a\29\5c\00", PeopleInput.EscapeLdap("*)(mail=*)\\\0"));
        Assert.Contains(@"sAMAccountName=\2a\29", PeopleInput.ExactFilter("*)"));
    }
    [Fact]
    public void SuggestionsNeverAutomaticallyResolve()
    {
        var candidate = PersonMatching.Suggest(Person(), "Mike");
        Assert.Equal("Possible nickname", candidate.Reason);
        Assert.False(PersonMatching.CanResolve(new([candidate], false)));
    }
    [Fact]
    public void OnlyUniqueEnabledCompleteExactMatchesResolve()
    {
        var candidate = new PersonCandidate(Person(), "Exact match", 0);
        Assert.True(PersonMatching.CanResolve(new([candidate], true)));
        Assert.False(PersonMatching.CanResolve(new([candidate, candidate], true)));
        Assert.False(PersonMatching.CanResolve(new([candidate], true, "Incomplete")));
        Assert.False(PersonMatching.CanResolve(new([candidate with { Person = Person(false) }], true)));
    }
    [Fact]
    public void QualifiedUsernamesRequireValidatedDomain()
    {
        Assert.Equal("alice", PeopleInput.NormalizeQualified(@"EXAMPLE\alice", "EXAMPLE", "example.test"));
        Assert.Equal("alice", PeopleInput.NormalizeQualified(@"example.test\alice", "EXAMPLE", "example.test"));
        Assert.Throws<ArgumentException>(() => PeopleInput.NormalizeQualified(@"FOREIGN\alice", "EXAMPLE", "example.test"));
    }
    [Theory]
    [InlineData("Smith, Mike", "Smith", "Mike")]
    [InlineData("Mike Smith", "Smith", "Mike")]
    [InlineData("alice@example.test", "", "")]
    [InlineData("user", "", "")]
    public void NameParsingIsConservative(string input, string surname, string first) =>
        Assert.Equal((surname, first), PeopleInput.NameParts(input));
}

namespace Vivre.Core.People;

public sealed record DirectoryPerson(string Id, string Name, string Username, string Upn,
    string Email, string Department, string Title, string Office, bool Enabled);
public sealed record PersonCandidate(DirectoryPerson Person, string Reason, int Rank);
public sealed record PersonLookupResult(IReadOnlyList<PersonCandidate> Candidates, bool Exact, string? Notice = null);
public interface IPeopleLookup
{
    Task<PersonLookupResult> LookupAsync(string input, CancellationToken token);
}

public static class PeopleInput
{
    public const int MaxEntries = 2000;
    public static string[] Parse(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string EscapeLdap(string text) => text.Replace("\\", "\\5c").Replace("*", "\\2a")
        .Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    public static string ExactFilter(string input)
    {
        string value = EscapeLdap(input);
        return $"(&(objectCategory=person)(objectClass=user)(|(sAMAccountName={value})(userPrincipalName={value})(mail={value})(displayName={value})(name={value})))";
    }

    public static string NormalizeQualified(string input, string netbiosDomain, string dnsDomain)
    {
        if (!input.Contains('\\')) return input;
        var parts = input.Split('\\');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]) ||
            !(parts[0].Equals(netbiosDomain, StringComparison.OrdinalIgnoreCase) ||
              parts[0].Equals(dnsDomain, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The qualified username is outside this lookup's current AD domain.");
        return parts[1];
    }

    public static (string Surname, string First) NameParts(string input)
    {
        if (input.Contains('@') || input.Contains('\\')) return ("", "");
        var comma = input.Split(',', 2, StringSplitOptions.TrimEntries);
        if (comma.Length == 2) return (comma[0], comma[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 ? (words[^1], words[0]) : ("", "");
    }
}

public static class PersonMatching
{
    private static readonly string[][] Nicknames =
    [
        ["Michael","Mike","Mick"], ["David","Dave"], ["William","Bill","Billy","Will","Liam"],
        ["Robert","Bob","Bobby","Rob","Robbie","Bert"], ["Richard","Rick","Ricky","Rich","Dick"],
        ["James","Jim","Jimmy","Jamie"], ["John","Jack","Johnny"], ["Thomas","Tom","Tommy"],
        ["Joseph","Joe","Joey"], ["Charles","Chuck","Charlie"], ["Anthony","Tony"],
        ["Edward","Ed","Eddie","Ted","Ned"], ["Theodore","Ted","Teddy","Theo"],
        ["Stephen","Steve"], ["Andrew","Andy","Drew"], ["Nicholas","Nick","Nicky"],
        ["Daniel","Dan","Danny"], ["Lawrence","Larry"], ["Alexander","Alex","Al","Sandy"],
        ["Margaret","Maggie","Meg","Peggy","Marge"], ["Elizabeth","Liz","Beth","Betty","Lisa","Eliza"],
        ["Katherine","Kate","Kathy","Katie","Kat"], ["Catherine","Cathy","Kate","Katie","Cat"],
        ["Patricia","Pat","Patty","Trish"], ["Susan","Sue","Susie","Suzy"],
        ["Rebecca","Becky","Becca"], ["Deborah","Debbie","Deb"]
    ];
    public static PersonCandidate Suggest(DirectoryPerson person, string first)
    {
        string display = person.Name.Contains(',') ? person.Name.Split(',', 2)[1].Trim() : person.Name;
        string actual = display.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        bool equal = string.Equals(first, actual, StringComparison.OrdinalIgnoreCase);
        bool nickname = Nicknames.Any(group => group.Contains(first, StringComparer.OrdinalIgnoreCase)
            && group.Contains(actual, StringComparer.OrdinalIgnoreCase));
        string initial = first.TrimEnd('.');
        bool initialMatch = initial.Length == 1 && actual.StartsWith(initial, StringComparison.OrdinalIgnoreCase);
        bool prefix = first.Length >= 3 && actual.Length >= 3
            && (actual.StartsWith(first, StringComparison.OrdinalIgnoreCase) || first.StartsWith(actual, StringComparison.OrdinalIgnoreCase));
        return new(person, equal ? "First name matches" : nickname ? "Possible nickname" :
            initialMatch ? "Initial matches" : prefix ? "Similar first name" : "Surname/name suggestion",
            equal ? 0 : nickname ? 1 : initialMatch ? 2 : prefix ? 3 : 4);
    }
    public static bool CanResolve(PersonLookupResult result) =>
        result.Notice is null && result.Exact && result.Candidates.Count == 1 && result.Candidates[0].Person.Enabled;
}

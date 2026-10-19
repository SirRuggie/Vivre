using System.DirectoryServices.Protocols;

namespace Vivre.Core.People;

/// <summary>Read-only ambient-domain AD access; no RSAT or remote machine connection required.</summary>
public sealed class AdPeopleLookup : IPeopleLookup
{
    public Task<PersonLookupResult> LookupAsync(string input, CancellationToken token) =>
        Task.Run(() => Read(input, token), token);

    private static PersonLookupResult Read(string input, CancellationToken token)
    {
        if (input.Length is 0 or > 256) throw new ArgumentException("Use an entry between 1 and 256 characters.");
        token.ThrowIfCancellationRequested();
        using var connection = new LdapConnection(new LdapDirectoryIdentifier((string)null!))
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.Signing = true;
        connection.SessionOptions.Sealing = true;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        connection.Bind();
        token.ThrowIfCancellationRequested();
        var root = (SearchResponse)connection.SendRequest(new SearchRequest("", "(objectClass=*)",
            SearchScope.Base, "defaultNamingContext", "configurationNamingContext"));
        string? baseDn = root.Entries.Count == 1 ? root.Entries[0].Attributes["defaultNamingContext"]?[0]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(baseDn)) throw new InvalidOperationException("No AD domain is available for the current Windows account.");
        if (input.Contains('\\'))
        {
            string? config = root.Entries[0].Attributes["configurationNamingContext"]?[0]?.ToString();
            if (string.IsNullOrWhiteSpace(config)) throw new InvalidOperationException("Cannot validate the username domain.");
            token.ThrowIfCancellationRequested();
            var domains = (SearchResponse)connection.SendRequest(new SearchRequest("CN=Partitions," + config,
                $"(&(objectClass=crossRef)(nCName={PeopleInput.EscapeLdap(baseDn)}))", SearchScope.OneLevel,
                "nETBIOSName", "dnsRoot") { SizeLimit = 2, TimeLimit = TimeSpan.FromSeconds(5) });
            if (domains.Entries.Count != 1) throw new InvalidOperationException("Cannot validate the username domain.");
            string netbios = domains.Entries[0].Attributes["nETBIOSName"]?[0]?.ToString() ?? "";
            string dns = domains.Entries[0].Attributes["dnsRoot"]?[0]?.ToString() ?? "";
            input = PeopleInput.NormalizeQualified(input, netbios, dns);
        }
        var exact = Search(connection, baseDn, PeopleInput.ExactFilter(input), token);
        if (exact.Count > 0) return new(exact.Select(p => new PersonCandidate(p, "Exact match", 0)).ToArray(), true);
        var (surname, first) = PeopleInput.NameParts(input);
        if (surname.Length == 0) return new([], false);
        string escaped = PeopleInput.EscapeLdap(surname);
        var candidates = Search(connection, baseDn,
            $"(&(objectCategory=person)(objectClass=user)(sn={escaped}))", token);
        if (candidates.Count == 0)
            candidates = Search(connection, baseDn,
                $"(&(objectCategory=person)(objectClass=user)(displayName=*{escaped}*))", token);
        return new(candidates.Select(p => PersonMatching.Suggest(p, first))
            .OrderBy(p => p.Rank).ThenByDescending(p => p.Person.Enabled).ThenBy(p => p.Person.Name).ToArray(), false);
    }

    private static IReadOnlyList<DirectoryPerson> Search(LdapConnection connection, string baseDn, string filter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree,
            "objectGUID", "displayName", "name", "sAMAccountName", "userPrincipalName", "mail",
            "department", "title", "physicalDeliveryOfficeName", "userAccountControl")
        {
            SizeLimit = 200, TimeLimit = TimeSpan.FromSeconds(5)
        };
        // Restrict search to this naming context; do not request subordinate-domain referrals.
        request.Controls.Add(new SearchOptionsControl(System.DirectoryServices.Protocols.SearchOption.DomainScope));
        SearchResponse response;
        try { response = (SearchResponse)connection.SendRequest(request); }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode is ResultCode.SizeLimitExceeded or ResultCode.TimeLimitExceeded)
        {
            throw new ArgumentException("Directory results were incomplete; refine the entry to a username or full email address.", ex);
        }
        token.ThrowIfCancellationRequested();
        if (response.References.Count > 0) throw new InvalidOperationException("Directory lookup requires another domain; results are incomplete.");
        return response.Entries.Cast<SearchResultEntry>().Select(e =>
        {
            string Value(string name) => e.Attributes[name]?[0]?.ToString() ?? "";
            string display = Value("displayName");
            bool enabled = int.TryParse(Value("userAccountControl"), out int flags) && (flags & 2) == 0;
            return new DirectoryPerson(e.DistinguishedName, display.Length > 0 ? display : Value("name"),
                Value("sAMAccountName"), Value("userPrincipalName"), Value("mail"), Value("department"),
                Value("title"), Value("physicalDeliveryOfficeName"), enabled);
        }).ToArray();
    }
}

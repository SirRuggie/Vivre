using System.DirectoryServices.Protocols;
using System.Text.RegularExpressions;

namespace Vivre.Core.Computers;

public record AdLinuxLabels(IReadOnlyDictionary<string, string> Values, string? Error = null, bool Available = true);

public interface IAdLinuxLabelLookup
{
    Task<AdLinuxLabels> LookupAsync(IReadOnlyList<string> names, CancellationToken token);
}

/// <summary>Cosmetic AD metadata only. One directory request at a time across all tabs;
/// no target connections, PowerShell modules, or scan throttle slots.</summary>
public sealed class AdLinuxLabelLookup : IAdLinuxLabelLookup
{
    public const int BatchSize = 64;
    private const int CacheLimit = 4096;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string? Os, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<DirectoryComputer>> _read;
    private readonly TimeProvider _clock;
    private DateTimeOffset _retryAfter;

    public AdLinuxLabelLookup() : this(ReadDirectory, TimeProvider.System) { }
    internal AdLinuxLabelLookup(Func<IReadOnlyList<string>, IReadOnlyList<DirectoryComputer>> read, TimeProvider? clock = null)
    {
        _read = read;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AdLinuxLabels> LookupAsync(IReadOnlyList<string> names, CancellationToken token)
    {
        if (names.Count > BatchSize) throw new ArgumentOutOfRangeException(nameof(names));
        string[] keys = names.Select(Normalize).Where(IsHostName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Even a cache miss must never execute LDAP synchronously on the caller's UI thread.
            return await Task.Run(() => LookupCore(keys, token), token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private AdLinuxLabels LookupCore(string[] keys, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var now = _clock.GetUtcNow();
        foreach (string key in keys)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.Expires > now)
            {
                if (cached.Os is { } os) values[key] = os;
            }
            else missing.Add(key);
        }
        if (missing.Count == 0) return new(values);
        if (now < _retryAfter) return new(values, Available: false);
        try
        {
            var mapped = Match(missing, _read(missing));
            token.ThrowIfCancellationRequested();
            if (_cache.Count + missing.Count > CacheLimit) _cache.Clear();
            foreach (string key in missing)
            {
                mapped.TryGetValue(key, out string? os);
                _cache[key] = (os, _clock.GetUtcNow().AddMinutes(10));
                if (os is not null) values[key] = os;
            }
            return new(values);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // One failed DC must not cause a retry for every host or every tab.
            _retryAfter = _clock.GetUtcNow().AddMinutes(1);
            return new(values, $"AD Linux labels unavailable: {ex.Message}", Available: false);
        }
    }

    public static bool IsLinux(string? os) => os is not null && Regex.IsMatch(os.Trim(),
        @"^(?:linux|oracle linux|kali(?: linux)?|arch(?: linux)?|gentoo(?: linux)?|mint|redhat-linux-gnu|red hat(?: enterprise)? linux|ubuntu|debian|centos|amazon linux|rhel|fedora|rocky linux|almalinux|suse(?: linux)?|opensuse)(?:$|[\s_\-\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static string Normalize(string name) => name.Trim().TrimEnd('.');
    private static bool IsHostName(string name) => name.Length is > 0 and <= 253 &&
        !System.Net.IPAddress.TryParse(name, out _) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal sealed record DirectoryComputer(string SamAccountName, string DnsHostName, string? OperatingSystem);

    internal static Dictionary<string, string> Match(IReadOnlyList<string> names, IReadOnlyList<DirectoryComputer> entries)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            // Never shorten an FQDN: same short name in another domain is not proof of identity.
            var matches = entries.Where(e => name.Contains('.')
                ? string.Equals(name, e.DnsHostName, StringComparison.OrdinalIgnoreCase)
                : string.Equals(name + "$", e.SamAccountName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1 && IsLinux(matches[0].OperatingSystem)) result[name] = matches[0].OperatingSystem!;
        }
        return result;
    }

    internal static string BuildFilter(IReadOnlyList<string> names) =>
        "(&(objectCategory=computer)(|" + string.Concat(names.Select(n =>
            n.Contains('.') ? $"(dNSHostName={Escape(n)})" : $"(sAMAccountName={Escape(n + "$")})")) + "))";

    private static string Escape(string value) => value.Replace("\\", "\\5c").Replace("*", "\\2a")
        .Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    private static IReadOnlyList<DirectoryComputer> ReadDirectory(IReadOnlyList<string> names)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AD labels require Windows.");
        using var connection = new LdapConnection(new LdapDirectoryIdentifier((string)null!));
        connection.Timeout = TimeSpan.FromSeconds(3);
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.Signing = true;
        connection.SessionOptions.Sealing = true;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        connection.Bind(); // ambient Windows login; default joined domain
        var root = (SearchResponse)connection.SendRequest(new SearchRequest("", "(objectClass=*)",
            SearchScope.Base, "defaultNamingContext"));
        string? baseDn = root.Entries.Count == 1 ? root.Entries[0].Attributes["defaultNamingContext"]?[0]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(baseDn)) throw new InvalidOperationException("No AD naming context available.");
        var request = new SearchRequest(baseDn, BuildFilter(names), SearchScope.Subtree,
            "sAMAccountName", "dNSHostName", "operatingSystem")
        {
            SizeLimit = BatchSize * 2,
            TimeLimit = TimeSpan.FromSeconds(3),
        };
        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Cast<SearchResultEntry>().Select(e => new DirectoryComputer(
            e.Attributes["sAMAccountName"]?[0]?.ToString() ?? "",
            e.Attributes["dNSHostName"]?[0]?.ToString() ?? "",
            e.Attributes["operatingSystem"]?[0]?.ToString())).ToArray();
    }
}

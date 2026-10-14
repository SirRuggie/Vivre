using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Software;

/// <inheritdoc cref="IDcomSoftwareReader"/>
/// <remarks>
/// Reads the uninstall registry over a DCOM <see cref="CimSession"/> on the ambient Windows login
/// (mirrors <c>DcomVitalsProbe</c> / <c>DcomLcuBuildReader</c>), via <c>StdRegProv</c> — so the software
/// check works on the boxes where WinRM/Kerberos is rejected.
/// <para>
/// Registry failures are accumulated while the remaining supported paths are searched. A proven match
/// survives unrelated failures and carries a warning; an incomplete search without a match throws so a
/// hidden install is never reported as absent. Service-query failures are returned separately from a
/// completed query that found no matching service.
/// </para>
/// </remarks>
public sealed class DcomSoftwareReader : IDcomSoftwareReader
{
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(8);
    private const uint HklmHive = 0x80000002;
    private const string Hive1UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Hive2UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly IDcomSoftwareProvider? _provider;

    public DcomSoftwareReader()
    {
    }

    internal DcomSoftwareReader(IDcomSoftwareProvider provider) => _provider = provider;

    public Task<SoftwareCheckResult> CheckAsync(string host, string query, string? serviceName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        // CIM calls are synchronous — run off the caller's thread so the sweep stays async.
        return Task.Run(() => CheckSync(host, query, serviceName, ct), ct);
    }

    private SoftwareCheckResult CheckSync(string host, string query, string? serviceName, CancellationToken ct)
    {
        try
        {
            // No explicit credential: ambient Windows identity, exactly like the other Dcom* readers.
            using var options = new DComSessionOptions { Timeout = CimTimeout };
            using CimSession? session = _provider is null ? CimSession.Create(host, options) : null;
            using var cimOptions = new CimOperationOptions
            {
                Timeout = CimTimeout,
                CancellationToken = ct,
            };

            IDcomSoftwareProvider provider = _provider ?? new CimSoftwareProvider(session!, cimOptions);
            (bool found, string? name, string? version, string? queryWarning) = ReadInstalled(provider, query, ct);
            (string? serviceState, string? serviceError) = ReadServiceState(provider, serviceName, ct);
            return new SoftwareCheckResult(found, name, version, serviceState, queryWarning, serviceError);
        }
        catch (OperationCanceledException)
        {
            throw; // a timeout must surface as the VM's honest "check timed out", never wrapped
        }
        catch (SoftwareProbeException)
        {
            throw; // already typed
        }
        catch (Exception ex)
        {
            throw new SoftwareProbeException("Couldn't read installed software over DCOM: " + ex.Message);
        }
    }

    private static (bool Found, string? Name, string? Version, string? QueryWarning) ReadInstalled(
        IDcomSoftwareProvider provider, string query, CancellationToken ct)
    {
        HiveScan hive1Scan = ScanHive(provider, Hive1UninstallKey, query, ct);
        HiveScan hive2Scan = ScanHive(provider, Hive2UninstallKey, query, ct);
        List<Candidate> hive1 = hive1Scan.Candidates;
        List<Candidate> hive2 = hive2Scan.Candidates;
        List<UninstallRow> hive1Rows = hive1.ConvertAll(c => c.Row);
        List<UninstallRow> hive2Rows = hive2.ConvertAll(c => c.Row);
        string? warning = JoinErrors(hive1Scan.Errors.Concat(hive2Scan.Errors));
        (bool found, string? name, string? aggregateWarning) = AssessSearch(
            hive1Rows, hive1Scan.Missing, hive1Scan.Completed,
            hive2Rows, hive2Scan.Missing, hive2Scan.Completed,
            query, warning);
        warning = aggregateWarning;
        if (!found) return (false, null, null, null);

        // MatchAcrossHives yields the winning NAME but not its registry path, so DisplayVersion (read for
        // the WINNER only) needs the subkey back. Re-locate the winner with the SAME predicate + hive
        // precedence over the path-carrying candidates. Locating by name alone is unsafe: the same
        // DisplayName can exist in both hives while only one entry actually matches the query.
        Candidate? winner = WinningCandidate(hive1, query) ?? WinningCandidate(hive2, query);
        string? version = null;
        if (winner is not null)
        {
            try
            {
                version = ReadStringValue(provider, winner.SubKeyPath, "DisplayVersion", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warning = JoinErrors(new[] { warning, "Matched product version could not be read: " + ex.Message });
            }
        }

        return (found, name, version, warning);
    }

    /// <summary>Enumerates one hive's uninstall subkeys and reads each entry's DisplayName (and Publisher,
    /// lazily). Return value 2 is a missing optional path; every other provider/read failure is retained
    /// with any candidates already proven by successful reads.</summary>
    private static HiveScan ScanHive(
        IDcomSoftwareProvider provider, string hiveKey, string query, CancellationToken ct)
    {
        uint enumRv;
        string[] subKeys;
        try
        {
            (enumRv, subKeys) = provider.EnumSubKeys(hiveKey, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HiveScan(false, false, [], [$"Uninstall path '{hiveKey}' could not be enumerated: {ex.Message}"]);
        }

        // RV=2 = hive key absent (e.g. no WOW6432Node on a 32-bit OS): benign, answered with nothing.
        if (IsMissingHiveReturn(enumRv))
        {
            return new HiveScan(true, true, [], []);
        }

        // RV=0 with zero subkeys is a valid empty enumeration; anything other than 0/2 is a denial or
        // fault a match could hide behind — refuse to answer "not installed".
        if (enumRv != 0)
        {
            return new HiveScan(false, false, [],
                [$"Uninstall path '{hiveKey}' could not be enumerated (StdRegProv EnumKey returned {enumRv})."]);
        }

        var candidates = new List<Candidate>();
        var errors = new List<string>();
        foreach (string subKey in subKeys)
        {
            string subKeyPath = hiveKey + "\\" + subKey;
            string? displayName;
            try
            {
                displayName = ReadStringValue(provider, subKeyPath, "DisplayName", ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"Registry entry '{subKey}' could not be read: {ex.Message}");
                continue;
            }
            if (displayName is null)
            {
                continue; // absent/blank DisplayName (RV=2 or blank success) — parity with the WinRM guard
            }

            // Publisher is only needed when the NAME didn't already contain the query: a publisher-only
            // match can sort ahead of a name match, so it must be read for every DisplayName-miss — no
            // early-exit shortcut. When the name already matches, skip the extra round-trip.
            string? publisher = null;
            if (!Contains(displayName, query))
            {
                try
                {
                    publisher = ReadStringValue(provider, subKeyPath, "Publisher", ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"Publisher for registry entry '{subKey}' could not be read: {ex.Message}");
                }
            }

            candidates.Add(new Candidate(subKeyPath, new UninstallRow(displayName, DisplayVersion: null, publisher)));
        }

        return new HiveScan(true, false, candidates, errors);
    }

    private static (string? State, string? Error) ReadServiceState(
        IDcomSoftwareProvider provider, string? serviceName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return (null, null); // no service check requested
        }

        try
        {
            // Client-side contains matching (Name or DisplayName) avoids the WQL LIKE-escaping pitfalls
            // that awkward service names would otherwise hit.
            foreach (DcomServiceRow svc in provider.ReadServices(ct))
            {
                string? name = svc.Name;
                string? displayName = svc.DisplayName;
                if (Contains(name, serviceName) || Contains(displayName, serviceName))
                {
                    string? state = svc.State;
                    if (string.IsNullOrWhiteSpace(state))
                    {
                        return (null, "Service query returned a matching service without a state over DCOM.");
                    }

                    return (SoftwareShaping.NormalizeServiceState(state), null);
                }
            }

            return ("not found", null); // completed query, no match — parity with the WinRM script's literal
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "Service query failed over DCOM: " + ex.Message);
        }
    }

    /// <summary>StdRegProv EnumKey against a hive path. Returns (ReturnValue, subkey names); RV=0 with a
    /// null <c>sNames</c> is a completed enumeration of ZERO subkeys (benign empty), not a failure.</summary>
    private static (uint Rv, string[] SubKeys) EnumSubKeys(CimSession session, CimOperationOptions cimOptions, string hiveKey)
    {
        using var inParams = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("hDefKey", HklmHive, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("sSubKeyName", hiveKey, CimType.String, CimFlags.In),
        };

        using CimMethodResult result = session.InvokeMethod(@"root\cimv2", "StdRegProv", "EnumKey", inParams, cimOptions);
        uint rv = ToUInt(result.ReturnValue?.Value);
        if (rv != 0)
        {
            return (rv, []);
        }

        string[] names = result.OutParameters?["sNames"]?.Value as string[] ?? [];
        return (rv, names);
    }

    private static (uint Rv, string? Value) ReadRawStringValue(
        CimSession session, CimOperationOptions cimOptions, string subKeyPath, string valueName)
    {
        using var inParams = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("hDefKey", HklmHive, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("sSubKeyName", subKeyPath, CimType.String, CimFlags.In),
            CimMethodParameter.Create("sValueName", valueName, CimType.String, CimFlags.In),
        };

        using CimMethodResult result = session.InvokeMethod(@"root\cimv2", "StdRegProv", "GetStringValue", inParams, cimOptions);
        return (ToUInt(result.ReturnValue?.Value), result.OutParameters?["sValue"]?.Value as string);
    }

    /// <summary>StdRegProv GetStringValue with the read policy: RV=0 -> the value (null when blank);
    /// RV=2 (value/key absent) -> null; RV=1, RV=5, or any other nonzero -> throw, because a value could be
    /// hidden behind the denial. A <see cref="CimException"/> bubbles to the outer catch.</summary>
    private static string? ReadStringValue(
        IDcomSoftwareProvider provider, string subKeyPath, string valueName, CancellationToken ct)
    {
        (uint rv, string? rawValue) = provider.ReadStringValue(subKeyPath, valueName, ct);
        return rv switch
        {
            0 => !string.IsNullOrWhiteSpace(rawValue) ? rawValue : null,
            2 => null,
            _ => throw new SoftwareProbeException(
                $"Registry read of '{valueName}' was refused over DCOM (StdRegProv GetStringValue returned {rv})."),
        };
    }

    // Mirrors SoftwareShaping.Match's predicate + ordering over path-carrying candidates. Used ONLY to map
    // the pure verdict back to a subkey for the single DisplayVersion read — the verdict itself is the one
    // from MatchAcrossHives.
    private static Candidate? WinningCandidate(IReadOnlyList<Candidate> candidates, string query) =>
        candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Row.DisplayName))
            .Where(c => Contains(c.Row.DisplayName, query) || Contains(c.Row.Publisher, query))
            .OrderBy(c => c.Row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static bool Contains(string? value, string query) =>
        value is not null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    // A missing/unconvertible ReturnValue reads as "unknown nonzero" (never success), so it can never be
    // mistaken for RV=0 and fake a clean answer.
    private static uint ToUInt(object? value)
    {
        if (value is null)
        {
            return uint.MaxValue;
        }

        try
        {
            return Convert.ToUInt32(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return uint.MaxValue;
        }
    }

    private static string? JoinErrors(IEnumerable<string?> errors)
    {
        string[] present = errors.Where(e => !string.IsNullOrWhiteSpace(e)).Cast<string>().ToArray();
        return present.Length == 0 ? null : string.Join("; ", present);
    }

    internal static (bool Found, string? Name, string? Warning) AssessSearch(
        IReadOnlyList<UninstallRow> hive1,
        bool hive1Missing,
        bool hive1Completed,
        IReadOnlyList<UninstallRow> hive2,
        bool hive2Missing,
        bool hive2Completed,
        string query,
        string? warning)
    {
        (bool found, string? name, _) = SoftwareShaping.MatchAcrossHives(hive1, hive2, query);
        if (found)
        {
            return (true, name, warning);
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            throw new SoftwareProbeException("Couldn't complete the uninstall registry search over DCOM: " + warning);
        }

        // Missing WOW6432Node is a supported empty optional path. Both roots missing means there was no
        // supported uninstall search at all, and therefore cannot prove absence.
        if (!hive1Completed || !hive2Completed || (hive1Missing && hive2Missing))
        {
            throw new SoftwareProbeException("Couldn't enumerate either uninstall hive over DCOM — the registry was unreadable.");
        }

        return (false, null, null);
    }

    // StdRegProv EnumKey: only ERROR_FILE_NOT_FOUND (2) proves an optional path is absent. Return 1
    // and access/provider codes are failures, never alternate spellings of "missing".
    internal static bool IsMissingHiveReturn(uint returnValue) => returnValue == 2;

    private sealed class CimSoftwareProvider(CimSession session, CimOperationOptions options) : IDcomSoftwareProvider
    {
        public (uint ReturnValue, string[] SubKeys) EnumSubKeys(string hiveKey, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return DcomSoftwareReader.EnumSubKeys(session, options, hiveKey);
        }

        public (uint ReturnValue, string? Value) ReadStringValue(
            string subKeyPath, string valueName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ReadRawStringValue(session, options, subKeyPath, valueName);
        }

        public IReadOnlyList<DcomServiceRow> ReadServices(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<DcomServiceRow>();
            foreach (CimInstance svc in session.QueryInstances(@"root\cimv2", "WQL",
                         "SELECT Name, DisplayName, State FROM Win32_Service", options))
            {
                using (svc)
                {
                    rows.Add(new DcomServiceRow(
                        svc.CimInstanceProperties["Name"]?.Value as string,
                        svc.CimInstanceProperties["DisplayName"]?.Value as string,
                        svc.CimInstanceProperties["State"]?.Value as string));
                }
            }

            return rows;
        }
    }

    private sealed record Candidate(string SubKeyPath, UninstallRow Row);
    private sealed record HiveScan(bool Completed, bool Missing, List<Candidate> Candidates, List<string> Errors);
}

internal interface IDcomSoftwareProvider
{
    (uint ReturnValue, string[] SubKeys) EnumSubKeys(string hiveKey, CancellationToken ct);
    (uint ReturnValue, string? Value) ReadStringValue(string subKeyPath, string valueName, CancellationToken ct);
    IReadOnlyList<DcomServiceRow> ReadServices(CancellationToken ct);
}

internal sealed record DcomServiceRow(string? Name, string? DisplayName, string? State);

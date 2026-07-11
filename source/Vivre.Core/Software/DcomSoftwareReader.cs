using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Software;

/// <inheritdoc cref="IDcomSoftwareReader"/>
/// <remarks>
/// Reads the uninstall registry over a DCOM <see cref="CimSession"/> on the ambient Windows login
/// (mirrors <c>DcomVitalsProbe</c> / <c>DcomLcuBuildReader</c>), via <c>StdRegProv</c> — so the software
/// check works on the boxes where WinRM/Kerberos is rejected.
/// <para>
/// STRUCTURE: follows <c>DcomLcuBuildReader</c>, NOT <c>DcomVitalsProbe</c>. The vitals probe swallows a
/// per-probe failure to a null field — safe for a null verdict, but FATAL here because <see cref="SoftwareCheckResult.Found"/>
/// is a bool that paints the grid cell red "missing". So per-read helpers let a real failure BUBBLE and
/// exactly ONE outer try/catch classifies it: a completed read may answer <c>Found=false</c>, but a read
/// that could NOT complete (access denied, session lost) THROWS so a hidden install is never reported as
/// absent.
/// </para>
/// </remarks>
public sealed class DcomSoftwareReader : IDcomSoftwareReader
{
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(8);
    private const uint HklmHive = 0x80000002;
    private const string Hive1UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Hive2UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<SoftwareCheckResult> CheckAsync(string host, string query, string? serviceName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        // CIM calls are synchronous — run off the caller's thread so the sweep stays async.
        return Task.Run(() => CheckSync(host, query, serviceName, ct), ct);
    }

    private static SoftwareCheckResult CheckSync(string host, string query, string? serviceName, CancellationToken ct)
    {
        try
        {
            // No explicit credential: ambient Windows identity, exactly like the other Dcom* readers.
            using var options = new DComSessionOptions { Timeout = CimTimeout };
            using CimSession session = CimSession.Create(host, options);
            using var cimOptions = new CimOperationOptions
            {
                Timeout = CimTimeout,
                CancellationToken = ct,
            };

            (bool found, string? name, string? version) = ReadInstalled(session, cimOptions, query);
            string? serviceState = ReadServiceState(session, cimOptions, serviceName);
            return new SoftwareCheckResult(found, name, version, serviceState);
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

    private static (bool Found, string? Name, string? Version) ReadInstalled(
        CimSession session, CimOperationOptions cimOptions, string query)
    {
        List<Candidate> hive1 = ScanHive(session, cimOptions, Hive1UninstallKey, query, out uint rv1);
        List<Candidate> hive2 = ScanHive(session, cimOptions, Hive2UninstallKey, query, out uint rv2);

        // COMPLETION CRITERION (load-bearing): a "not installed" verdict is only honest when at least one
        // hive actually enumerated (RV=0). RV=5/other already threw inside ScanHive, so here each rv is 0
        // or 2 (key-absent). If BOTH are 2, not even the primary 64-bit Uninstall key listed — treat the
        // box as unreadable rather than clean, so a hidden install can't be painted "missing".
        if (rv1 != 0 && rv2 != 0)
        {
            throw new SoftwareProbeException("Couldn't enumerate either uninstall hive over DCOM — the registry was unreadable.");
        }

        List<UninstallRow> hive1Rows = hive1.ConvertAll(c => c.Row);
        List<UninstallRow> hive2Rows = hive2.ConvertAll(c => c.Row);
        (bool found, string? name, _) = SoftwareShaping.MatchAcrossHives(hive1Rows, hive2Rows, query);
        if (!found)
        {
            return (false, null, null);
        }

        // MatchAcrossHives yields the winning NAME but not its registry path, so DisplayVersion (read for
        // the WINNER only) needs the subkey back. Re-locate the winner with the SAME predicate + hive
        // precedence over the path-carrying candidates. Locating by name alone is unsafe: the same
        // DisplayName can exist in both hives while only one entry actually matches the query.
        Candidate? winner = WinningCandidate(hive1, query) ?? WinningCandidate(hive2, query);
        string? version = winner is null
            ? null
            : ReadStringValue(session, cimOptions, winner.SubKeyPath, "DisplayVersion");

        return (found, name, version);
    }

    /// <summary>Enumerates one hive's uninstall subkeys and reads each entry's DisplayName (and Publisher,
    /// lazily), returning the path-carrying candidates. <paramref name="enumRv"/> reports the EnumKey
    /// ReturnValue (0 = enumerated, 2 = key absent) so the caller can enforce the completion criterion; a
    /// denial (RV=5/other) throws here because a match could be hidden behind it.</summary>
    private static List<Candidate> ScanHive(
        CimSession session, CimOperationOptions cimOptions, string hiveKey, string query, out uint enumRv)
    {
        (enumRv, string[] subKeys) = EnumSubKeys(session, cimOptions, hiveKey);

        // RV=2 = hive key absent (e.g. no WOW6432Node on a 32-bit OS): benign, answered with nothing.
        if (enumRv == 2)
        {
            return [];
        }

        // RV=0 with zero subkeys is a valid empty enumeration; anything other than 0/2 is a denial or
        // fault a match could hide behind — refuse to answer "not installed".
        if (enumRv != 0)
        {
            throw new SoftwareProbeException(
                $"Couldn't enumerate the uninstall hive over DCOM (StdRegProv EnumKey returned {enumRv}).");
        }

        var candidates = new List<Candidate>();
        foreach (string subKey in subKeys)
        {
            string subKeyPath = hiveKey + "\\" + subKey;

            string? displayName = ReadStringValue(session, cimOptions, subKeyPath, "DisplayName");
            if (displayName is null)
            {
                continue; // no DisplayName (RV=1/RV=2) — dropped, parity with the WinRM DisplayName guard
            }

            // Publisher is only needed when the NAME didn't already contain the query: a publisher-only
            // match can sort ahead of a name match, so it must be read for every DisplayName-miss — no
            // early-exit shortcut. When the name already matches, skip the extra round-trip.
            string? publisher = Contains(displayName, query)
                ? null
                : ReadStringValue(session, cimOptions, subKeyPath, "Publisher");

            candidates.Add(new Candidate(subKeyPath, new UninstallRow(displayName, DisplayVersion: null, publisher)));
        }

        return candidates;
    }

    private static string? ReadServiceState(CimSession session, CimOperationOptions cimOptions, string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null; // no service check requested
        }

        try
        {
            // Client-side contains matching (Name or DisplayName) avoids the WQL LIKE-escaping pitfalls
            // that awkward service names would otherwise hit.
            foreach (CimInstance svc in session.QueryInstances(@"root\cimv2", "WQL",
                         "SELECT Name, DisplayName, State FROM Win32_Service",
                         cimOptions))
            {
                using (svc)
                {
                    string? name = svc.CimInstanceProperties["Name"]?.Value as string;
                    string? displayName = svc.CimInstanceProperties["DisplayName"]?.Value as string;
                    if (Contains(name, serviceName) || Contains(displayName, serviceName))
                    {
                        string state = svc.CimInstanceProperties["State"]?.Value as string ?? string.Empty;
                        return SoftwareShaping.NormalizeServiceState(state);
                    }
                }
            }

            return "not found"; // completed query, no match — parity with the WinRM script's literal
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The service lookup threw but the registry scan already produced a verdict. Accept the
            // silent loss: return null (no annotation) rather than fabricate "not found" — the VM renders
            // a null service state as no annotation, never a false "no service".
            return null;
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

    /// <summary>StdRegProv GetStringValue with the read policy: RV=0 -> the value (null when blank);
    /// RV=1/RV=2 (value/key absent) -> null; RV=5 or any other nonzero -> throw, because a value could be
    /// hidden behind the denial. A <see cref="CimException"/> bubbles to the outer catch.</summary>
    private static string? ReadStringValue(CimSession session, CimOperationOptions cimOptions, string subKeyPath, string valueName)
    {
        using var inParams = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("hDefKey", HklmHive, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("sSubKeyName", subKeyPath, CimType.String, CimFlags.In),
            CimMethodParameter.Create("sValueName", valueName, CimType.String, CimFlags.In),
        };

        using CimMethodResult result = session.InvokeMethod(@"root\cimv2", "StdRegProv", "GetStringValue", inParams, cimOptions);
        uint rv = ToUInt(result.ReturnValue?.Value);
        return rv switch
        {
            0 => result.OutParameters?["sValue"]?.Value as string is { } v && !string.IsNullOrWhiteSpace(v) ? v : null,
            1 or 2 => null,
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

    private sealed record Candidate(string SubKeyPath, UninstallRow Row);
}

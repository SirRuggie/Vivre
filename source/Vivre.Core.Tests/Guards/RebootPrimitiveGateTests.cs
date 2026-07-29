using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Vivre.Core.Tests.Guards;

/// <summary>
/// THE REBOOT PRIMITIVE GATE, run automatically by <c>dotnet test</c>.
///
/// <para>This is the SECOND CALLER of the gate, not a fork of it. <c>tools/reboot-primitive-gate.sh</c> stays
/// for manual runs; both read the same inventory from <c>tools/reboot-primitive-gate.manifest</c>, so the two
/// can never disagree about what to look for or how many to expect. The SCANNERS are necessarily separate
/// implementations (bash grep vs .NET <see cref="Regex"/>) — that duplication is unavoidable across a shell
/// script and a test assembly — but the DATA is shared, which is where drift would actually hurt.</para>
///
/// <para><b>Why this exists as a test at all:</b> reboot finding #5 was a gate that covered one of five
/// primitives. The gate that replaced it was correct but manual, and a gate nobody runs is not a gate.</para>
///
/// <para><b>THE LOAD-BEARING RULE — this test must never pass having scanned nothing.</b> A repo-scanning test
/// that cannot find the repo is the "green tests lie" failure this project has been bitten by, so the marker
/// lookup and the visited-file count are asserted EXPLICITLY and FIRST, before any count assertion. There is
/// no skip path and no early return: every failure mode is a loud assertion.</para>
/// </summary>
public class RebootPrimitiveGateTests
{
    private const string RepoMarker = "source/Vivre.slnx";
    private const string ManifestPath = "tools/reboot-primitive-gate.manifest";

    // Both gate files name every token they hunt for, so a scan that included them would report the gate
    // itself as an unknown primitive site. Mirrors the shell script's --exclude flags.
    private static readonly string[] SelfExcluded =
        ["reboot-primitive-gate.sh", "reboot-primitive-gate.manifest"];

    [Fact]
    public void Reboot_primitive_inventory_is_unchanged()
    {
        // ── 1. Locate the repo, and FAIL LOUDLY if we cannot. ────────────────────────────────────────────
        string? repoRoot = FindRepoRoot(AppContext.BaseDirectory, out string searched);

        Assert.False(
            repoRoot is null,
            $"REBOOT GATE COULD NOT RUN: no '{RepoMarker}' found walking up from '{AppContext.BaseDirectory}'. "
            + $"Directories searched: {searched}. This test scans the repository, so a failed lookup means it "
            + "scanned NOTHING — that is a hard failure, never a skip. Fix the marker or the lookup.");

        string root = repoRoot!;

        // ── 2. Load the shared inventory, and FAIL LOUDLY if it is missing or empty. ──────────────────────
        string manifestFile = Path.Combine(root, ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(manifestFile), $"REBOOT GATE COULD NOT RUN: missing inventory '{manifestFile}'.");

        string[] manifestLines = File.ReadAllLines(manifestFile)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .ToArray();

        string? tokens = manifestLines
            .Select(l => l.Split('\t'))
            .Where(f => f.Length >= 2 && f[0] == "TOKENS")
            .Select(f => f[1])
            .FirstOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(tokens), $"REBOOT GATE COULD NOT RUN: no TOKENS line in {ManifestPath}.");

        var sites = manifestLines
            .Select(l => l.Split('\t'))
            .Where(f => f.Length >= 5 && f[0] == "SITE")
            .Select(f => (Expected: int.Parse(f[1]), Root: f[2], Label: f[3], Pattern: f[4]))
            .ToList();
        Assert.NotEmpty(sites);

        var expectedFiles = manifestLines
            .Select(l => l.Split('\t'))
            .Where(f => f.Length >= 3 && f[0] == "FILE")
            .ToDictionary(f => f[2], f => int.Parse(f[1]), StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(expectedFiles);

        // ── 3. Scan, and FAIL LOUDLY if the scan visited nothing. ─────────────────────────────────────────
        List<(string RelPath, string Text)> scanned = ReadScannableFiles(root, ["source", "scripts", "tools"]);

        Assert.True(
            scanned.Count > 0,
            $"REBOOT GATE COULD NOT RUN: the scan visited 0 files under '{root}'. A gate that scanned nothing "
            + "must never report success.");

        // A real repo has thousands of text files; a handful means the walk is broken even though it found
        // the marker. Deliberately a floor, not a pin — it must not need editing when files are added.
        Assert.True(
            scanned.Count >= 100,
            $"REBOOT GATE COULD NOT RUN: only {scanned.Count} file(s) scanned under '{root}' — implausibly few, "
            + "so the walk is broken. Refusing to report success.");

        var tokenRegex = new Regex(tokens!, RegexOptions.None);

        // ── 4. LAYER 1 — the confirmed primitives, at exact counts. ───────────────────────────────────────
        // Counts are MATCHING LINES, not total matches, because that is what the shell script's
        // `grep -rE … | wc -l` produces. Counting matches instead would silently disagree with it.
        var failures = new List<string>();

        foreach ((int expected, string siteRoot, string label, string pattern) in sites)
        {
            var siteRegex = new Regex(pattern, RegexOptions.None);
            string prefix = siteRoot.Replace('\\', '/').TrimEnd('/') + "/";

            int actual = scanned
                .Where(f => f.RelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Sum(f => CountMatchingLines(f.Text, siteRegex));

            if (actual != expected)
            {
                failures.Add($"{label} — EXPECTED {expected} occurrence(s), FOUND {actual}");
            }
        }

        // ── 5. LAYER 2 — containment: no reboot token in any unlisted file. ───────────────────────────────
        // This is the check that catches a SIXTH primitive, including one smuggled into a file that already
        // legitimately mentions one (a count change), not just a brand-new file.
        Dictionary<string, int> actualFiles = scanned
            .Select(f => (f.RelPath, Count: CountMatchingLines(f.Text, tokenRegex)))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.RelPath, x => x.Count, StringComparer.OrdinalIgnoreCase);

        foreach ((string path, int count) in actualFiles.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!expectedFiles.TryGetValue(path, out int want))
            {
                failures.Add(
                    $"containment — UNLISTED file '{path}' contains {count} reboot token(s). This may be a SIXTH "
                    + "REBOOT PRIMITIVE. Classify it before touching the manifest: does it ISSUE a reboot, or "
                    + "only discuss one? See docs/reboot-path-and-guardrail-findings.md > finding 5.");
            }
            else if (want != count)
            {
                failures.Add($"containment — '{path}' EXPECTED {want} token(s), FOUND {count}");
            }
        }

        foreach ((string path, int want) in expectedFiles.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!actualFiles.ContainsKey(path))
            {
                failures.Add(
                    $"containment — listed file '{path}' now contains NO reboot token (expected {want}). A "
                    + "primitive may have MOVED; find where it went before editing the manifest.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "THE REBOOT PRIMITIVE INVENTORY HAS CHANGED — do NOT edit the manifest to make this pass until a "
            + $"human has agreed the code is right.\nRepo: {root}\nFiles scanned: {scanned.Count}\n  - "
            + string.Join("\n  - ", failures));
    }

    /// <summary>Walks up from <paramref name="start"/> looking for <see cref="RepoMarker"/>. Returns null when
    /// it is never found, and reports every directory tried so a failure names where it looked.</summary>
    private static string? FindRepoRoot(string start, out string searched)
    {
        var tried = new List<string>();
        string marker = RepoMarker.Replace('/', Path.DirectorySeparatorChar);

        for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
        {
            tried.Add(dir.FullName);
            if (File.Exists(Path.Combine(dir.FullName, marker)))
            {
                searched = string.Join(" -> ", tried);
                return dir.FullName;
            }
        }

        searched = string.Join(" -> ", tried);
        return null;
    }

    /// <summary>Every text file under <paramref name="roots"/>, as repo-relative forward-slash paths. Build
    /// output and the gate's own two files are excluded; binaries are detected by a NUL byte and skipped, so
    /// coverage does NOT depend on an extension allowlist (a red-team pass proved a .cmd file evaded one).</summary>
    private static List<(string RelPath, string Text)> ReadScannableFiles(string root, string[] roots)
    {
        var result = new List<(string, string)>();

        foreach (string sub in roots)
        {
            string dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string full in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, full).Replace('\\', '/');

                if (rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    || rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                    || SelfExcluded.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(full);
                }
                catch (IOException)
                {
                    continue;   // locked by another process mid-run; not a gate failure
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                // NUL in the first 8 KB => binary. Same intent as grep's -I.
                int probe = Math.Min(bytes.Length, 8192);
                if (Array.IndexOf(bytes, (byte)0, 0, probe) >= 0)
                {
                    continue;
                }

                result.Add((rel, Encoding.UTF8.GetString(bytes)));
            }
        }

        return result;
    }

    /// <summary>Number of LINES containing at least one match — matching the shell script's line-oriented
    /// grep count rather than a total-match count.</summary>
    private static int CountMatchingLines(string text, Regex regex)
    {
        int n = 0;
        foreach (string line in text.Split('\n'))
        {
            if (regex.IsMatch(line))
            {
                n++;
            }
        }

        return n;
    }
}

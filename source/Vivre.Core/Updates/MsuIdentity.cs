using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Vivre.Core.Updates;

/// <summary>
/// The typed outcome of reading a Windows Update <c>.msu</c>'s embedded servicing metadata — either
/// <see cref="Accepted"/> (a genuine Server 2016 cumulative update, carrying the exact values the Settings
/// flow persists) or <see cref="Refused"/> (anything else, with an operator-language reason that names what
/// was actually found). The hierarchy is closed to those two: there is no third state and no partial result.
/// A package Vivre can't positively identify as the Server 2016 CU is always <see cref="Refused"/>, never a
/// guess — a wrong CU identity fed into the staging lane is a real-harm class.
/// </summary>
public abstract record MsuIdentityResult
{
    // Private ctor closes the hierarchy — only the two nested records below can derive.
    private MsuIdentityResult() { }

    /// <summary>A confirmed Server 2016 cumulative update. <paramref name="Kb"/> is the KB-prefixed article
    /// pulled from the package's own metadata (e.g. "KB5099535"), <paramref name="TargetUbr"/> the build
    /// revision the box should report once it commits (e.g. 9339 → 14393.9339), and <paramref name="Arch"/>
    /// the filename token the package store matches ("x64", mapped from the identity's "amd64").</summary>
    public sealed record Accepted(
        string Kb,
        int TargetUbr,
        string Arch,
        string IdentityName,
        string Version,
        string Description) : MsuIdentityResult;

    /// <summary>The package is not a genuine Server 2016 cumulative update (wrong product, wrong build, wrong
    /// architecture, renamed file, malformed metadata, or a combined bundle). <paramref name="Reason"/> is
    /// operator-language and states the actual value found so the operator can correct the folder.</summary>
    public sealed record Refused(string Reason) : MsuIdentityResult;
}

/// <summary>
/// Pure, I/O-free identifier for a Windows Server 2016 cumulative-update <c>.msu</c>. Given the file name, the
/// name of the single servicing XML expanded out of it, and that XML's text, it returns a typed
/// <see cref="MsuIdentityResult"/>. Every guard here is load-bearing: the result is fed straight into the
/// Settings-side CU identity, and a misread (an SSU, a .NET rollup, a 2019/Win10 CU, a renamed or combined
/// package) that slipped through would stage the wrong update on a live Server 2016 box. Companion I/O reader:
/// <see cref="MsuPackageReader"/>.
/// </summary>
public static class MsuIdentity
{
    /// <summary>The human description returned for an accepted package.</summary>
    public const string Server2016CuDescription = "Server 2016 Cumulative Update";

    /// <summary>The one servicing-identity name a Server 2016 OS cumulative update carries.</summary>
    private const string RollupFixName = "Package_for_RollupFix";

    /// <summary>
    /// Reads the servicing metadata and decides whether <paramref name="msuFileName"/> is a genuine Server 2016
    /// cumulative update. Never throws for content reasons — a malformed document or an unexpected shape is a
    /// <see cref="MsuIdentityResult.Refused"/> with the real cause, never a value.
    /// </summary>
    /// <param name="msuFileName">The .msu file name (no path needed) — the KB cross-check reads its "kbNNN" token.</param>
    /// <param name="xmlMemberFileName">The name of the single .xml expanded from the .msu — carries the authoritative KB.</param>
    /// <param name="xmlContent">The servicing XML text (an &lt;unattend&gt;/&lt;servicing&gt;/&lt;package&gt; document).</param>
    public static MsuIdentityResult Parse(string msuFileName, string xmlMemberFileName, string xmlContent)
    {
        // 1. Parse the servicing XML. Namespace-tolerant (matched by LOCAL element/attribute name below).
        //    A malformed document is refused with the real parser error — never a guess.
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlContent);
        }
        catch (XmlException ex)
        {
            return new MsuIdentityResult.Refused(
                $"The package's servicing metadata couldn't be read — the embedded XML is malformed: {ex.Message}");
        }

        // 2. Exactly one package identity. Zero = not a servicing package; more than one = the combined
        //    servicing-stack + cumulative shape, which we refuse rather than pick a half of.
        XElement[] identities = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "assemblyIdentity", StringComparison.Ordinal))
            .ToArray();

        if (identities.Length == 0)
        {
            return new MsuIdentityResult.Refused(
                "The package carries no servicing identity — it isn't a recognisable Windows update package.");
        }

        if (identities.Length > 1)
        {
            return new MsuIdentityResult.Refused(
                $"The package contains {identities.Length} update entries — this looks like a combined servicing-stack + "
                + "cumulative update bundle, not a single Server 2016 cumulative update; refusing.");
        }

        XElement identity = identities[0];
        string name = LocalAttr(identity, "name");
        string version = LocalAttr(identity, "version");
        string arch = LocalAttr(identity, "processorArchitecture");

        // 3. PRODUCT PIN — the load-bearing check. Name AND version AND arch must all be the Server 2016 CU's.
        //    Each refusal names what WAS found so the operator understands why.
        if (!string.Equals(name, RollupFixName, StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("ServicingStack", StringComparison.OrdinalIgnoreCase))
            {
                return new MsuIdentityResult.Refused(
                    $"The package identifies as '{DescribeName(name)}' — that's a servicing stack update (SSU), not the cumulative update.");
            }

            return new MsuIdentityResult.Refused(
                $"The package identifies as '{DescribeName(name)}' — not a Server 2016 cumulative update.");
        }

        Match versionMatch = Regex.Match(version, @"^14393\.(\d+)\.", RegexOptions.CultureInvariant);
        if (!versionMatch.Success || !int.TryParse(versionMatch.Groups[1].Value, out int targetUbr))
        {
            return new MsuIdentityResult.Refused(
                $"The package is for a different Windows build (version {DescribeValue(version)}; Server 2016 is 14393.x).");
        }

        if (!string.Equals(arch, "amd64", StringComparison.OrdinalIgnoreCase))
        {
            return new MsuIdentityResult.Refused(
                $"The package is for {DescribeValue(arch)}, not x64 (amd64) — Server 2016 patching is x64 only.");
        }

        // 4. KB CROSS-CHECK — the .msu file name AND the embedded metadata name must carry the SAME KB. This
        //    catches a renamed file or two files mixed up. The KB returned on accept is the embedded one.
        string? fileKb = ExtractKb(msuFileName);
        string? memberKb = ExtractKb(xmlMemberFileName);

        if (fileKb is null && memberKb is null)
        {
            return new MsuIdentityResult.Refused(
                $"Neither the file name ('{msuFileName}') nor the package metadata carries a KB number — was the file renamed? Refusing to guess.");
        }

        if (fileKb is null)
        {
            return new MsuIdentityResult.Refused(
                $"The file name ('{msuFileName}') doesn't carry a KB number — was it renamed? The package metadata says KB{memberKb}; refusing until the file name matches.");
        }

        if (memberKb is null)
        {
            return new MsuIdentityResult.Refused(
                $"The package's embedded metadata ('{xmlMemberFileName}') doesn't carry a KB number — this isn't the expected package shape; refusing.");
        }

        if (!string.Equals(fileKb, memberKb, StringComparison.OrdinalIgnoreCase))
        {
            return new MsuIdentityResult.Refused(
                $"The file is named KB{fileKb} but the package metadata says KB{memberKb} — the file may be renamed or mixed up; refusing.");
        }

        // Every check passed — accept. The embedded KB is authoritative (and equals the file's).
        return new MsuIdentityResult.Accepted(
            Kb: $"KB{memberKb}",
            TargetUbr: targetUbr,
            Arch: "x64",
            IdentityName: name,
            Version: version,
            Description: Server2016CuDescription);
    }

    /// <summary>First attribute matched by LOCAL name (namespace-agnostic), or "" when absent.</summary>
    private static string LocalAttr(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value
        ?? string.Empty;

    /// <summary>The bare KB digits from a file name's "kbNNNN" token (case-insensitive), or null when absent.</summary>
    private static string? ExtractKb(string? fileName)
    {
        Match m = Regex.Match(fileName ?? string.Empty, @"kb(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string DescribeName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "(no name)" : name;

    private static string DescribeValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : value;
}

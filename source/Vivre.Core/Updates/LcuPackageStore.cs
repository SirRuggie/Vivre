using System.Security.Cryptography;

namespace Vivre.Core.Updates;

/// <summary>Status of looking for a Cumulative Update package in the package directory.</summary>
public enum LcuPackageStatus { Found, Missing, Mismatch, Ambiguous }

/// <summary>
/// Outcome of <see cref="LcuPackageStore.Resolve"/>. Every result carries a
/// <see cref="CatalogUrl"/> so the operator can immediately download the correct file.
/// </summary>
public sealed record LcuPackageResolution(
    LcuPackageStatus Status,
    string? Path,
    string Message,
    string CatalogUrl);

/// <summary>
/// Verifies that a local package directory contains exactly the expected
/// monthly Cumulative Update .msu before patching begins — guards against
/// installing a wrong or corrupt file on a Server 2016 box.
/// </summary>
public sealed class LcuPackageStore
{
    /// <summary>
    /// Builds the Microsoft Update Catalog search URL for a KB article.
    /// Accepts "KB5094122" or "5094122"; always returns the KB-prefixed form.
    /// </summary>
    public static string CatalogSearchUrl(string kb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kb);

        // Normalise: strip any leading "KB" prefix (case-insensitive), keep digits.
        string digits = kb.TrimStart();
        if (digits.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
            digits = digits[2..];

        return $"https://www.catalog.update.microsoft.com/Search.aspx?q=KB{digits.ToUpperInvariant()}";
    }

    /// <summary>
    /// Looks in <paramref name="directory"/> for the .msu that matches the expected CU.
    /// </summary>
    /// <remarks>
    /// Matching is done on file name: the .msu must contain the KB digits AND the arch
    /// token (e.g. "x64"), both case-insensitive. Exactly one match is then optionally
    /// verified by SHA-256 hash or file size; ambiguous or missing sets are reported
    /// without guessing so the operator can correct the directory before patching.
    /// </remarks>
    public LcuPackageResolution Resolve(
        string directory,
        string kb,
        string arch,
        long? expectedSizeBytes,
        long toleranceBytes,
        string? expectedSha256)
    {
        string catalogUrl = CatalogSearchUrl(kb);

        // Strip leading "KB" to get the bare digits used in name matching.
        string kbDigits = kb.TrimStart();
        if (kbDigits.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
            kbDigits = kbDigits[2..];

        if (!Directory.Exists(directory))
        {
            return new LcuPackageResolution(
                LcuPackageStatus.Missing,
                null,
                $"Package directory '{directory}' does not exist. Download KB{kbDigits} from the Catalog.",
                catalogUrl);
        }

        string[] candidates = Directory.GetFiles(directory, "*.msu")
            .Where(f =>
                Path.GetFileName(f).Contains(kbDigits, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(f).Contains(arch, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
        {
            return new LcuPackageResolution(
                LcuPackageStatus.Missing,
                null,
                $"No .msu for KB{kbDigits} ({arch}) found in '{directory}'. Download it from the Catalog.",
                catalogUrl);
        }

        if (candidates.Length > 1)
        {
            return new LcuPackageResolution(
                LcuPackageStatus.Ambiguous,
                null,
                $"{candidates.Length} matching .msu files found in '{directory}'. Remove all but the correct KB{kbDigits} ({arch}) file.",
                catalogUrl);
        }

        string matchedPath = candidates[0];

        // --- Verification: hash beats size; size beats name-only ---

        if (!string.IsNullOrEmpty(expectedSha256))
        {
            string actualHex = ComputeSha256Hex(matchedPath);
            if (!string.Equals(actualHex, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new LcuPackageResolution(
                    LcuPackageStatus.Mismatch,
                    null,
                    $"SHA-256 mismatch for '{Path.GetFileName(matchedPath)}'. " +
                    $"Expected {expectedSha256.ToUpperInvariant()}, got {actualHex.ToUpperInvariant()}. " +
                    $"Re-download KB{kbDigits} from the Catalog.",
                    catalogUrl);
            }

            return new LcuPackageResolution(
                LcuPackageStatus.Found,
                matchedPath,
                $"KB{kbDigits} ({arch}) verified by SHA-256.",
                catalogUrl);
        }

        if (expectedSizeBytes.HasValue)
        {
            long actualSize = new FileInfo(matchedPath).Length;
            long delta = Math.Abs(actualSize - expectedSizeBytes.Value);
            if (delta > toleranceBytes)
            {
                return new LcuPackageResolution(
                    LcuPackageStatus.Mismatch,
                    null,
                    $"Size mismatch for '{Path.GetFileName(matchedPath)}': " +
                    $"expected ~{expectedSizeBytes.Value:N0} bytes, actual {actualSize:N0} bytes " +
                    $"(delta {delta:N0}, tolerance {toleranceBytes:N0}). " +
                    $"Re-download KB{kbDigits} from the Catalog.",
                    catalogUrl);
            }

            return new LcuPackageResolution(
                LcuPackageStatus.Found,
                matchedPath,
                $"KB{kbDigits} ({arch}) verified by file size.",
                catalogUrl);
        }

        // Name-only match — no hash or size provided; warn the operator.
        return new LcuPackageResolution(
            LcuPackageStatus.Found,
            matchedPath,
            $"KB{kbDigits} ({arch}) matched by name only — no hash or size check was performed.",
            catalogUrl);
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}

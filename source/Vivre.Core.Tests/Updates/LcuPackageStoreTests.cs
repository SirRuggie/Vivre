using System.Security.Cryptography;
using Vivre.Core.Updates;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Covers <see cref="LcuPackageStore"/> and <see cref="LcuPackageResolution"/> — pure file-system
/// logic only. Each test creates real temp files so the hash/size paths exercise actual I/O without
/// touching a production package directory.
/// </summary>
public class LcuPackageStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LcuPackageStore _store = new();

    public LcuPackageStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LcuPackageStoreTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // --- CatalogSearchUrl -------------------------------------------------

    [Theory]
    [InlineData("5094122",   "https://www.catalog.update.microsoft.com/Search.aspx?q=KB5094122")]
    [InlineData("KB5094122", "https://www.catalog.update.microsoft.com/Search.aspx?q=KB5094122")]
    [InlineData("kb5094122", "https://www.catalog.update.microsoft.com/Search.aspx?q=KB5094122")]
    public void CatalogSearchUrl_normalises_kb_with_and_without_prefix(string input, string expected) =>
        Assert.Equal(expected, LcuPackageStore.CatalogSearchUrl(input));

    // --- Missing: directory does not exist --------------------------------

    [Fact]
    public void Resolve_returns_Missing_when_directory_does_not_exist()
    {
        string missing = Path.Combine(_tempDir, "nonexistent");

        LcuPackageResolution result = _store.Resolve(missing, "KB5094122", "x64",
            expectedSizeBytes: null, toleranceBytes: 0, expectedSha256: null);

        Assert.Equal(LcuPackageStatus.Missing, result.Status);
        Assert.Null(result.Path);
        Assert.Contains("KB5094122", result.CatalogUrl);
    }

    // --- Missing: empty directory -----------------------------------------

    [Fact]
    public void Resolve_returns_Missing_when_directory_is_empty()
    {
        LcuPackageResolution result = _store.Resolve(_tempDir, "KB5094122", "x64",
            expectedSizeBytes: null, toleranceBytes: 0, expectedSha256: null);

        Assert.Equal(LcuPackageStatus.Missing, result.Status);
        Assert.Null(result.Path);
        Assert.Contains("KB5094122", result.CatalogUrl);
    }

    // --- Found: size within tolerance -------------------------------------

    [Fact]
    public void Resolve_returns_Found_when_size_is_within_tolerance()
    {
        string msu = WriteMsu("windows10.0-kb5094122-x64.msu", byteCount: 1_000);

        LcuPackageResolution result = _store.Resolve(_tempDir, "KB5094122", "x64",
            expectedSizeBytes: 1_000, toleranceBytes: 100, expectedSha256: null);

        Assert.Equal(LcuPackageStatus.Found, result.Status);
        Assert.Equal(msu, result.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("KB5094122", result.CatalogUrl);
    }

    // --- Mismatch: size outside tolerance ---------------------------------

    [Fact]
    public void Resolve_returns_Mismatch_when_size_is_outside_tolerance()
    {
        WriteMsu("windows10.0-kb5094122-x64.msu", byteCount: 500);

        LcuPackageResolution result = _store.Resolve(_tempDir, "KB5094122", "x64",
            expectedSizeBytes: 1_000, toleranceBytes: 100, expectedSha256: null);

        Assert.Equal(LcuPackageStatus.Mismatch, result.Status);
        Assert.Null(result.Path);
        Assert.Contains("KB5094122", result.CatalogUrl);
    }

    // --- Found: correct SHA-256 -------------------------------------------

    [Fact]
    public void Resolve_returns_Found_when_sha256_matches()
    {
        byte[] content = "correct content for hashing"u8.ToArray();
        string msu = WriteMsuBytes("windows10.0-kb5094122-x64.msu", content);
        string expectedHash = Convert.ToHexString(SHA256.HashData(content));

        LcuPackageResolution result = _store.Resolve(_tempDir, "KB5094122", "x64",
            expectedSizeBytes: null, toleranceBytes: 0, expectedSha256: expectedHash);

        Assert.Equal(LcuPackageStatus.Found, result.Status);
        Assert.Equal(msu, result.Path, StringComparer.OrdinalIgnoreCase);
    }

    // --- Mismatch: wrong SHA-256 ------------------------------------------

    [Fact]
    public void Resolve_returns_Mismatch_when_sha256_does_not_match()
    {
        WriteMsuBytes("windows10.0-kb5094122-x64.msu", "wrong content"u8.ToArray());
        string wrongHash = new string('A', 64); // 32 zero bytes hex — won't match

        LcuPackageResolution result = _store.Resolve(_tempDir, "KB5094122", "x64",
            expectedSizeBytes: null, toleranceBytes: 0, expectedSha256: wrongHash);

        Assert.Equal(LcuPackageStatus.Mismatch, result.Status);
        Assert.Null(result.Path);
    }

    // --- Ambiguous: two matching .msu files -------------------------------

    [Fact]
    public void Resolve_returns_Ambiguous_when_two_matching_msu_files_exist()
    {
        WriteMsu("windows10.0-kb5094122-x64.msu", byteCount: 100);
        WriteMsu("windows10.0-kb5094122-x64-v2.msu", byteCount: 100);

        LcuPackageResolution result = _store.Resolve(_tempDir, "KB5094122", "x64",
            expectedSizeBytes: null, toleranceBytes: 0, expectedSha256: null);

        Assert.Equal(LcuPackageStatus.Ambiguous, result.Status);
        Assert.Null(result.Path);
        Assert.Contains("KB5094122", result.CatalogUrl);
    }

    // --- Name-only match --------------------------------------------------

    [Fact]
    public void Resolve_returns_Found_with_name_only_note_when_no_hash_or_size_provided()
    {
        string msu = WriteMsu("windows10.0-kb5094122-x64.msu", byteCount: 200);

        LcuPackageResolution result = _store.Resolve(_tempDir, "KB5094122", "x64",
            expectedSizeBytes: null, toleranceBytes: 0, expectedSha256: null);

        Assert.Equal(LcuPackageStatus.Found, result.Status);
        Assert.Equal(msu, result.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("name only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Arch filter: an x86 file is not returned for x64 ----------------

    [Fact]
    public void Resolve_returns_Missing_when_only_wrong_arch_msu_exists()
    {
        WriteMsu("windows10.0-kb5094122-x86.msu", byteCount: 100);

        LcuPackageResolution result = _store.Resolve(_tempDir, "KB5094122", "x64",
            expectedSizeBytes: null, toleranceBytes: 0, expectedSha256: null);

        Assert.Equal(LcuPackageStatus.Missing, result.Status);
    }

    // --- SHA-256 takes precedence over size -------------------------------

    [Fact]
    public void Resolve_uses_sha256_when_both_hash_and_size_are_provided()
    {
        byte[] content = "hash wins"u8.ToArray();
        WriteMsuBytes("windows10.0-kb5094122-x64.msu", content);
        string correctHash = Convert.ToHexString(SHA256.HashData(content));

        // Size is wildly wrong but hash is correct — should still return Found.
        LcuPackageResolution result = _store.Resolve(_tempDir, "KB5094122", "x64",
            expectedSizeBytes: 999_999, toleranceBytes: 0, expectedSha256: correctHash);

        Assert.Equal(LcuPackageStatus.Found, result.Status);
    }

    // --- Helpers ----------------------------------------------------------

    /// <summary>Creates a .msu file of exactly <paramref name="byteCount"/> zero bytes.</summary>
    private string WriteMsu(string fileName, int byteCount)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, new byte[byteCount]);
        return path;
    }

    /// <summary>Creates a .msu file with specific <paramref name="content"/>.</summary>
    private string WriteMsuBytes(string fileName, byte[] content)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }
}

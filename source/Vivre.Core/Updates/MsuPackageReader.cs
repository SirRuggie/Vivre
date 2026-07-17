using System.Diagnostics;
using System.Text;

namespace Vivre.Core.Updates;

/// <summary>
/// What the pure parser decided, enriched with the file it read. <see cref="Identity"/> is the
/// <see cref="MsuIdentityResult"/> (Accepted / Refused). <see cref="MsuPath"/> and <see cref="FileName"/> are
/// the single .msu that was read (null for folder-level refusals — no .msu, empty folder, or too many);
/// <see cref="SizeBytes"/> is that file's size (0 when there was no single file). <see cref="FileModifiedUtc"/>
/// is that file's last-modified stamp (the file's own timestamp — NOT a release date; null when there was no
/// single file, or the stat failed).
/// </summary>
public sealed record MsuReadResult(
    MsuIdentityResult Identity,
    string? MsuPath,
    string? FileName,
    long SizeBytes,
    DateTime? FileModifiedUtc);

/// <summary>The raw outcome of expanding the servicing XML out of a .msu: <c>expand.exe</c>'s exit code, its
/// diagnostic text (stderr, or stdout when stderr was empty), and the .xml file paths it produced.</summary>
public sealed record MsuXmlExtraction(int ExitCode, string Diagnostic, IReadOnlyList<string> XmlPaths);

/// <summary>
/// The injectable seam over the one-off <c>expand.exe</c> call so <see cref="MsuPackageReader"/>'s folder-level
/// behaviour (0 / 1 / many .msu, extractor failure, unexpected shape) is unit-testable without real 1.8 GB
/// packages. The real implementation is <see cref="ExpandMsuXmlExtractor"/>; tests supply a fake.
/// </summary>
public interface IMsuXmlExtractor
{
    /// <summary>Extract the servicing <c>.xml</c> member(s) of <paramref name="msuPath"/> into
    /// <paramref name="destinationDir"/> and report the outcome. Throws <see cref="TimeoutException"/> if the
    /// extractor overran its own timeout, or another exception on a hard launch failure (both surfaced by the
    /// reader as a refusal — never a false negative).</summary>
    Task<MsuXmlExtraction> ExtractXmlAsync(string msuPath, string destinationDir, CancellationToken cancellationToken);
}

/// <summary>
/// The real extractor: shells <c>expand.exe "&lt;msu&gt;" -F:*.xml "&lt;dir&gt;"</c>, which selectively pulls the one
/// small servicing XML out of the package in well under a second (it does NOT decompress the multi-GB payload).
/// No window, output redirected, hard 30s cap with a kill so a locked or mid-copy file can't hang the read.
/// </summary>
internal sealed class ExpandMsuXmlExtractor : IMsuXmlExtractor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<MsuXmlExtraction> ExtractXmlAsync(string msuPath, string destinationDir, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("expand.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(msuPath);
        psi.ArgumentList.Add("-F:*.xml");
        psi.ArgumentList.Add(destinationDir);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout tripped (not a caller cancel): kill the child and surface it as a timeout.
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"expand.exe did not finish within {Timeout.TotalSeconds:N0}s.");
        }
        catch (OperationCanceledException)
        {
            // Caller cancel — kill the child and propagate.
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        string[] xmlPaths = Directory.Exists(destinationDir)
            ? Directory.GetFiles(destinationDir, "*.xml")
            : [];

        // expand.exe writes progress to stdout; prefer stderr for the failure text, fall back to stdout so a
        // non-zero exit never surfaces an empty reason.
        string diagnostic = stderr.Length > 0 ? stderr.ToString().Trim() : stdout.ToString().Trim();
        return new MsuXmlExtraction(proc.ExitCode, diagnostic, xmlPaths);
    }
}

/// <summary>
/// Reads the CU package folder and identifies the single Server 2016 cumulative-update <c>.msu</c> in it,
/// entirely read-only. It NEVER picks between candidates: zero, or more than one, .msu is refused outright so
/// two different months can't coin-flip. The one .msu is expanded (via <see cref="IMsuXmlExtractor"/>), its
/// servicing XML handed to the pure <see cref="MsuIdentity"/> parser, and the parser's verdict returned
/// enriched with the file path + size. The temp extraction directory is always deleted. This is a read-and-
/// confirm accelerator for the Settings CU fields — it computes no hash and never writes settings itself.
/// </summary>
public sealed class MsuPackageReader
{
    private readonly IMsuXmlExtractor _extractor;

    public MsuPackageReader() : this(new ExpandMsuXmlExtractor()) { }

    public MsuPackageReader(IMsuXmlExtractor extractor) => _extractor = extractor;

    /// <summary>
    /// Look in <paramref name="folder"/> for exactly one .msu and identify it. Returns a folder-level refusal
    /// (missing folder, no .msu, more than one .msu, extractor failure/timeout, unexpected shape) or the pure
    /// parser's Accepted/Refused verdict enriched with the file. No host is contacted; nothing is written.
    /// </summary>
    public async Task<MsuReadResult> ReadAsync(string folder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return FolderRefusal("No CU package folder is set. Set it in Settings ▸ Server 2016 cumulative update, then drop the month's .msu there.");
        }

        if (!Directory.Exists(folder))
        {
            return FolderRefusal($"The CU package folder '{folder}' doesn't exist. Create it (or set it in Settings) and drop the month's .msu there.");
        }

        // Enumeration can itself fail (an ACL-denied folder, a share that drops mid-read) — the reader's
        // contract is refusals, never throws, so surface the real error as a refusal.
        string[] msus;
        try
        {
            msus = Directory.GetFiles(folder, "*.msu");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FolderRefusal($"Couldn't list '{folder}': {ex.Message} Nothing was changed.");
        }

        if (msus.Length == 0)
        {
            return FolderRefusal($"No .msu found in '{folder}'. Download the month's Server 2016 cumulative update from the Microsoft Update Catalog and drop it here.");
        }

        if (msus.Length > 1)
        {
            string names = string.Join(", ", msus.Select(Path.GetFileName)
                                                  .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            return FolderRefusal($"{msus.Length} .msu files found — remove the extras so exactly one remains: {names}.");
        }

        string msuPath = msus[0];
        string fileName = Path.GetFileName(msuPath);
        long sizeBytes = SafeSize(msuPath);
        DateTime? fileModifiedUtc = SafeModifiedUtc(msuPath);

        string tempDir = Path.Combine(Path.GetTempPath(), $"Vivre_MsuRead_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A full disk / locked-down temp dir must refuse, not throw — same contract as every other failure.
            return FileRefusal($"Couldn't create a temporary folder to read the package: {ex.Message} Nothing was changed.", msuPath, fileName, sizeBytes, fileModifiedUtc);
        }

        try
        {
            MsuXmlExtraction extraction;
            try
            {
                extraction = await _extractor.ExtractXmlAsync(msuPath, tempDir, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // a caller cancel propagates — it is not a package problem
            }
            catch (TimeoutException)
            {
                return FileRefusal("Timed out reading the package — the .msu may be locked or still being copied. Nothing was changed.", msuPath, fileName, sizeBytes, fileModifiedUtc);
            }
            catch (Exception ex)
            {
                return FileRefusal($"Couldn't read the package: {ex.Message} Nothing was changed.", msuPath, fileName, sizeBytes, fileModifiedUtc);
            }

            if (extraction.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(extraction.Diagnostic)
                    ? $"expand.exe exited with code {extraction.ExitCode}."
                    : $"expand.exe exited with code {extraction.ExitCode}: {extraction.Diagnostic}";
                return FileRefusal($"Couldn't read the package — {detail} Nothing was changed.", msuPath, fileName, sizeBytes, fileModifiedUtc);
            }

            if (extraction.XmlPaths.Count == 0)
            {
                return FileRefusal("The package yielded no servicing metadata — this isn't the expected .msu shape. Nothing was changed.", msuPath, fileName, sizeBytes, fileModifiedUtc);
            }

            if (extraction.XmlPaths.Count > 1)
            {
                return FileRefusal($"The package yielded {extraction.XmlPaths.Count} metadata files — this isn't the expected .msu shape. Nothing was changed.", msuPath, fileName, sizeBytes, fileModifiedUtc);
            }

            string xmlPath = extraction.XmlPaths[0];
            string xmlMemberName = Path.GetFileName(xmlPath);
            string xmlContent;
            try
            {
                xmlContent = await System.IO.File.ReadAllTextAsync(xmlPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FileRefusal($"Couldn't read the servicing metadata inside the package: {ex.Message} Nothing was changed.", msuPath, fileName, sizeBytes, fileModifiedUtc);
            }

            MsuIdentityResult identity = MsuIdentity.Parse(fileName, xmlMemberName, xmlContent);
            return new MsuReadResult(identity, msuPath, fileName, sizeBytes, fileModifiedUtc);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>The .msu's own last-modified stamp (UTC), or null when the stat fails — same never-throws
    /// pattern as <see cref="SafeSize"/>. This is the file's timestamp, not a release date.</summary>
    private static DateTime? SafeModifiedUtc(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc; }
        catch { return null; }
    }

    /// <summary>A folder-level refusal — there is no single .msu to name.</summary>
    private static MsuReadResult FolderRefusal(string reason) =>
        new(new MsuIdentityResult.Refused(reason), MsuPath: null, FileName: null, SizeBytes: 0, FileModifiedUtc: null);

    /// <summary>A refusal that still carries the one .msu it was reading.</summary>
    private static MsuReadResult FileRefusal(string reason, string msuPath, string fileName, long sizeBytes, DateTime? fileModifiedUtc) =>
        new(new MsuIdentityResult.Refused(reason), msuPath, fileName, sizeBytes, fileModifiedUtc);
}

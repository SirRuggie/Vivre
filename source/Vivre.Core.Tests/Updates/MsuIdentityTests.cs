using Vivre.Core.Updates;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Covers the pure <see cref="MsuIdentity"/> parser (A1) and the <see cref="MsuPackageReader"/> folder logic (A2)
/// via the injectable extractor seam. The parser cases build servicing XML in-line — zero I/O, no real .msu; the
/// reader cases use a fake <see cref="IMsuXmlExtractor"/> so 0/1/many-file and extractor-failure behaviour is
/// exercised without 1.8 GB packages. Every negative case asserts a Refused verdict (never a coined value) —
/// a misread CU identity fed into the staging lane is the real-harm class this parser exists to prevent.
/// </summary>
public class MsuIdentityTests
{
    // The real 2026-07 spike shape: one Package_for_RollupFix identity, version 14393.9339.1.26, amd64.
    private const string SpikeXml =
        "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"><servicing><package action=\"install\">" +
        "<assemblyIdentity name=\"Package_for_RollupFix\" version=\"14393.9339.1.26\" language=\"neutral\" " +
        "processorArchitecture=\"amd64\" publicKeyToken=\"31bf3856ad364e35\"/>" +
        "<source location=\"%configsetroot%\\Windows10.0-KB5099535-x64.cab\"/></package></servicing></unattend>";

    private static string Servicing(string name, string version, string arch) =>
        "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"><servicing><package action=\"install\">" +
        $"<assemblyIdentity name=\"{name}\" version=\"{version}\" language=\"neutral\" " +
        $"processorArchitecture=\"{arch}\" publicKeyToken=\"31bf3856ad364e35\"/>" +
        "</package></servicing></unattend>";

    private static MsuIdentityResult.Accepted AssertAccepted(MsuIdentityResult result)
    {
        MsuIdentityResult.Accepted accepted = Assert.IsType<MsuIdentityResult.Accepted>(result);
        return accepted;
    }

    private static MsuIdentityResult.Refused AssertRefused(MsuIdentityResult result) =>
        Assert.IsType<MsuIdentityResult.Refused>(result);

    // ── A1: pure parser ──────────────────────────────────────────────────

    [Fact]
    public void Parse_accepts_the_real_spike_identity()
    {
        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099535-x64_0123456789abcdef0123456789abcdef01234567.msu",
            "Windows10.0-KB5099535-x64.xml",
            SpikeXml);

        MsuIdentityResult.Accepted a = AssertAccepted(result);
        Assert.Equal("KB5099535", a.Kb);
        Assert.Equal(9339, a.TargetUbr);
        Assert.Equal("x64", a.Arch);
        Assert.Equal("Package_for_RollupFix", a.IdentityName);
        Assert.Equal("14393.9339.1.26", a.Version);
        Assert.Contains("Server 2016", a.Description);
    }

    [Fact]
    public void Parse_refuses_a_servicing_stack_update()
    {
        string xml = Servicing("Package_for_ServicingStack_9339", "14393.9339.1.0", "amd64");

        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099536-x64.msu", "Windows10.0-KB5099536-x64.xml", xml);

        MsuIdentityResult.Refused r = AssertRefused(result);
        Assert.Contains("servicing stack", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_refuses_a_rollupfix_for_a_different_build()
    {
        // Server 2019 / build 17763 — a genuine RollupFix, but not 14393.
        string xml = Servicing("Package_for_RollupFix", "17763.5820.1.4", "amd64");

        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099999-x64.msu", "Windows10.0-KB5099999-x64.xml", xml);

        MsuIdentityResult.Refused r = AssertRefused(result);
        Assert.Contains("different Windows build", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("17763", r.Reason);
        Assert.Contains("14393", r.Reason);
    }

    [Fact]
    public void Parse_refuses_when_filename_kb_and_member_kb_disagree()
    {
        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099535-x64.msu",   // file says 5099535
            "Windows10.0-KB5088888-x64.xml",   // metadata says 5088888
            SpikeXml);

        MsuIdentityResult.Refused r = AssertRefused(result);
        Assert.Contains("KB5099535", r.Reason);
        Assert.Contains("KB5088888", r.Reason);
    }

    [Fact]
    public void Parse_refuses_malformed_xml_and_never_returns_a_value()
    {
        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099535-x64.msu", "Windows10.0-KB5099535-x64.xml",
            "<unattend><servicing><package>  not closed properly");

        MsuIdentityResult.Refused r = AssertRefused(result);
        Assert.Contains("malformed", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_refuses_empty_xml_content_and_never_returns_a_value()
    {
        // No document at all — XDocument.Parse("") can't yield a root. Must refuse, never coin a value.
        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099535-x64.msu", "Windows10.0-KB5099535-x64.xml", "");

        AssertRefused(result);
    }

    [Fact]
    public void Parse_refuses_a_combined_multi_package_bundle()
    {
        // Two package/assemblyIdentity entries — the combined SSU + CU shape.
        string xml =
            "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"><servicing>" +
            "<package action=\"install\"><assemblyIdentity name=\"Package_for_ServicingStack_9339\" " +
            "version=\"14393.9339.1.0\" processorArchitecture=\"amd64\"/></package>" +
            "<package action=\"install\"><assemblyIdentity name=\"Package_for_RollupFix\" " +
            "version=\"14393.9339.1.26\" processorArchitecture=\"amd64\"/></package>" +
            "</servicing></unattend>";

        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099535-x64.msu", "Windows10.0-KB5099535-x64.xml", xml);

        MsuIdentityResult.Refused r = AssertRefused(result);
        Assert.Contains("combined", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("14393")]          // no second field at all
    [InlineData("14393.")]         // second field empty
    [InlineData("14393..1.26")]    // second field missing between dots
    [InlineData("14393.abc.1.26")] // second field non-numeric
    public void Parse_refuses_a_version_with_a_bad_second_field(string version)
    {
        string xml = Servicing("Package_for_RollupFix", version, "amd64");

        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099535-x64.msu", "Windows10.0-KB5099535-x64.xml", xml);

        AssertRefused(result); // never coins a UBR from a malformed version
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("arm64")]
    public void Parse_refuses_a_wrong_architecture(string arch)
    {
        string xml = Servicing("Package_for_RollupFix", "14393.9339.1.26", arch);

        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099535-x64.msu", "Windows10.0-KB5099535-x64.xml", xml);

        MsuIdentityResult.Refused r = AssertRefused(result);
        Assert.Contains(arch, r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_refuses_a_renamed_file_with_no_kb_token_in_the_msu_name()
    {
        MsuIdentityResult result = MsuIdentity.Parse(
            "this-months-update.msu",          // renamed — no kb token
            "Windows10.0-KB5099535-x64.xml",
            SpikeXml);

        MsuIdentityResult.Refused r = AssertRefused(result);
        Assert.Contains("KB number", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_refuses_a_non_rollupfix_dotnet_or_other_product()
    {
        // A .NET-style rollup identity is not RollupFix — refused as "not a Server 2016 cumulative update".
        string xml = Servicing("Package_for_DotNetRollup", "14393.9339.1.26", "amd64");

        MsuIdentityResult result = MsuIdentity.Parse(
            "windows10.0-kb5099535-ndp48-x64.msu", "Windows10.0-KB5099535-ndp48-x64.xml", xml);

        MsuIdentityResult.Refused r = AssertRefused(result);
        Assert.Contains("not a Server 2016 cumulative update", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── A2: reader folder logic via the injectable seam ──────────────────

    /// <summary>Fake extractor: writes the supplied xml (or nothing) into the destination dir and reports a
    /// fixed exit code — or throws, to exercise the reader's failure path — without a real expand.exe.</summary>
    private sealed class FakeExtractor : IMsuXmlExtractor
    {
        private readonly string? _xmlMemberName;
        private readonly string? _xmlContent;
        private readonly int _exitCode;
        private readonly string _diagnostic;
        private readonly Exception? _throw;
        private readonly int _extraXmlFiles;

        public FakeExtractor(string? xmlMemberName, string? xmlContent, int exitCode = 0,
            string diagnostic = "", Exception? throwThis = null, int extraXmlFiles = 0)
        {
            _xmlMemberName = xmlMemberName;
            _xmlContent = xmlContent;
            _exitCode = exitCode;
            _diagnostic = diagnostic;
            _throw = throwThis;
            _extraXmlFiles = extraXmlFiles;
        }

        public Task<MsuXmlExtraction> ExtractXmlAsync(string msuPath, string destinationDir, CancellationToken cancellationToken)
        {
            if (_throw is not null)
            {
                throw _throw;
            }

            var paths = new List<string>();
            if (_xmlMemberName is not null && _xmlContent is not null)
            {
                string p = Path.Combine(destinationDir, _xmlMemberName);
                File.WriteAllText(p, _xmlContent);
                paths.Add(p);
            }

            for (int i = 0; i < _extraXmlFiles; i++)
            {
                string p = Path.Combine(destinationDir, $"extra_{i}.xml");
                File.WriteAllText(p, "<x/>");
                paths.Add(p);
            }

            return Task.FromResult(new MsuXmlExtraction(_exitCode, _diagnostic, paths));
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"MsuReaderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Reader_refuses_a_missing_folder()
    {
        var reader = new MsuPackageReader(new FakeExtractor(null, null));
        string missing = Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}");

        MsuReadResult result = await reader.ReadAsync(missing, CancellationToken.None);

        MsuIdentityResult.Refused r = AssertRefused(result.Identity);
        Assert.Contains(missing, r.Reason);
    }

    [Fact]
    public async Task Reader_refuses_an_empty_folder_with_no_msu()
    {
        string dir = NewTempDir();
        try
        {
            var reader = new MsuPackageReader(new FakeExtractor(null, null));

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            MsuIdentityResult.Refused r = AssertRefused(result.Identity);
            Assert.Contains("No .msu found", r.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reader_accepts_when_exactly_one_msu_and_the_fake_yields_the_spike_xml()
    {
        string dir = NewTempDir();
        try
        {
            string msu = Path.Combine(dir, "windows10.0-kb5099535-x64_deadbeef.msu");
            File.WriteAllText(msu, "not a real msu — the fake extractor stands in for expand.exe");

            var reader = new MsuPackageReader(new FakeExtractor("Windows10.0-KB5099535-x64.xml", SpikeXml));

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            MsuIdentityResult.Accepted a = AssertAccepted(result.Identity);
            Assert.Equal("KB5099535", a.Kb);
            Assert.Equal(9339, a.TargetUbr);
            Assert.Equal("x64", a.Arch);
            Assert.Equal(msu, result.MsuPath);
            Assert.Equal("windows10.0-kb5099535-x64_deadbeef.msu", result.FileName);
            Assert.True(result.SizeBytes > 0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reader_refuses_two_msu_files_and_lists_both_without_picking()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "windows10.0-kb5099535-x64.msu"), "a");
            File.WriteAllText(Path.Combine(dir, "windows10.0-kb5088888-x64.msu"), "b");

            var reader = new MsuPackageReader(new FakeExtractor("x.xml", SpikeXml));

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            MsuIdentityResult.Refused r = AssertRefused(result.Identity);
            Assert.Contains("windows10.0-kb5099535-x64.msu", r.Reason);
            Assert.Contains("windows10.0-kb5088888-x64.msu", r.Reason);
            Assert.Null(result.MsuPath); // never picked one
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reader_refuses_when_the_extractor_throws()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "windows10.0-kb5099535-x64.msu"), "a");
            var reader = new MsuPackageReader(
                new FakeExtractor(null, null, throwThis: new InvalidOperationException("expand blew up")));

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            MsuIdentityResult.Refused r = AssertRefused(result.Identity);
            Assert.Contains("expand blew up", r.Reason);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reader_refuses_a_nonzero_extractor_exit_with_its_diagnostic()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "windows10.0-kb5099535-x64.msu"), "a");
            var reader = new MsuPackageReader(
                new FakeExtractor(null, null, exitCode: 2, diagnostic: "the file is locked"));

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            MsuIdentityResult.Refused r = AssertRefused(result.Identity);
            Assert.Contains("the file is locked", r.Reason);
            Assert.Contains("2", r.Reason);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reader_refuses_a_timeout()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "windows10.0-kb5099535-x64.msu"), "a");
            var reader = new MsuPackageReader(
                new FakeExtractor(null, null, throwThis: new TimeoutException()));

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            MsuIdentityResult.Refused r = AssertRefused(result.Identity);
            Assert.Contains("Timed out", r.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reader_refuses_when_the_extractor_yields_more_than_one_xml()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "windows10.0-kb5099535-x64.msu"), "a");
            var reader = new MsuPackageReader(
                new FakeExtractor("Windows10.0-KB5099535-x64.xml", SpikeXml, extraXmlFiles: 1));

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            MsuIdentityResult.Refused r = AssertRefused(result.Identity);
            Assert.Contains("expected .msu shape", r.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reader_refuses_when_the_extractor_yields_no_xml()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "windows10.0-kb5099535-x64.msu"), "a");
            var reader = new MsuPackageReader(new FakeExtractor(null, null)); // exit 0, no xml files

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            MsuIdentityResult.Refused r = AssertRefused(result.Identity);
            Assert.Contains("no servicing metadata", r.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reader_accept_path_populates_the_file_modified_stamp()
    {
        string dir = NewTempDir();
        try
        {
            string msu = Path.Combine(dir, "windows10.0-kb5099535-x64_deadbeef.msu");
            File.WriteAllText(msu, "not a real msu — the fake extractor stands in for expand.exe");
            DateTime expected = new FileInfo(msu).LastWriteTimeUtc;

            var reader = new MsuPackageReader(new FakeExtractor("Windows10.0-KB5099535-x64.xml", SpikeXml));

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            AssertAccepted(result.Identity);
            Assert.NotNull(result.FileModifiedUtc);
            // The stamp is the file's own LastWriteTimeUtc, read off the same unchanged file.
            Assert.True(Math.Abs((expected - result.FileModifiedUtc!.Value).TotalSeconds) < 1);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reader_folder_level_refusal_has_no_file_modified_stamp()
    {
        string dir = NewTempDir();
        try
        {
            // Two .msu → a folder-level refusal that names no single file, so no stamp either.
            File.WriteAllText(Path.Combine(dir, "windows10.0-kb5099535-x64.msu"), "a");
            File.WriteAllText(Path.Combine(dir, "windows10.0-kb5088888-x64.msu"), "b");

            var reader = new MsuPackageReader(new FakeExtractor("x.xml", SpikeXml));

            MsuReadResult result = await reader.ReadAsync(dir, CancellationToken.None);

            AssertRefused(result.Identity);
            Assert.Null(result.MsuPath);         // never picked one
            Assert.Null(result.FileModifiedUtc); // and no single-file stamp
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

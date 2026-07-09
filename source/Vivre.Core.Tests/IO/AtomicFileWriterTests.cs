using Vivre.Core.IO;
using Xunit;

namespace Vivre.Core.Tests.IO;

public class AtomicFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vivre-atomicwrite-" + Guid.NewGuid().ToString("N"));

    public AtomicFileWriterTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Write_creates_the_file_when_absent()
    {
        string path = Path.Combine(_dir, "settings.json");

        AtomicFileWriter.Write(path, "hello");

        Assert.True(File.Exists(path));
        Assert.Equal("hello", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Write_atomically_replaces_existing_content()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "old content");

        AtomicFileWriter.Write(path, "new content");

        Assert.Equal("new content", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Write_overwrites_a_stale_tmp()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path + ".tmp", "junk left by a prior crash");
        File.WriteAllText(path, "old content");

        AtomicFileWriter.Write(path, "new content");

        Assert.Equal("new content", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

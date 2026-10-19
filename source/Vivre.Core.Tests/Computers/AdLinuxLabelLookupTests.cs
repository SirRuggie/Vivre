using Vivre.Core.Computers;
using Xunit;
using Entry = Vivre.Core.Computers.AdLinuxLabelLookup.DirectoryComputer;

namespace Vivre.Core.Tests.Computers;

public class AdLinuxLabelLookupTests
{
    [Theory]
    [InlineData("redhat-linux-gnu", true)]
    [InlineData("Ubuntu 22.04 LTS", true)]
    [InlineData("CentOS Linux 7", true)]
    [InlineData("Amazon Linux 2023", true)]
    [InlineData("Red Hat Enterprise Linux 9", true)]
    [InlineData("Linux", true)]
    [InlineData("Oracle Linux Server 9", true)]
    [InlineData("Linux Mint 22", true)]
    [InlineData("Kali GNU/Linux", true)]
    [InlineData("Arch Linux", true)]
    [InlineData("Microsoft Windows Server 2022", false)]
    [InlineData("Windows Subsystem for Linux", false)]
    [InlineData("not linux", false)]
    [InlineData("Cisco ISE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognizes_only_supported_linux_descriptions(string? value, bool expected) =>
        Assert.Equal(expected, AdLinuxLabelLookup.IsLinux(value));

    [Fact]
    public void Exact_identity_and_ambiguity_are_preserved()
    {
        var result = AdLinuxLabelLookup.Match(["APVANSIBLE", "same.other.example", "ambiguous", "windows"],
            [new("APVANSIBLE$", "apvansible.example", "redhat-linux-gnu"),
             new("same$", "same.example", "Linux"),
             new("ambiguous$", "one.example", "Linux"), new("ambiguous$", "two.example", "Linux"),
             new("windows$", "windows.example", "Windows Server 2022")]);
        Assert.Single(result);
        Assert.Equal("redhat-linux-gnu", result["APVANSIBLE"]);
    }

    [Fact]
    public void Filter_escapes_special_characters_and_keeps_fqdn_exact()
    {
        Assert.Equal("(&(objectCategory=computer)(|(sAMAccountName=a\\2a\\29\\28$)(dNSHostName=b.example)))",
            AdLinuxLabelLookup.BuildFilter(["a*)(", "b.example"]));
    }

    [Fact]
    public async Task Cache_includes_misses_and_expires()
    {
        int calls = 0;
        var clock = new Clock();
        var lookup = new AdLinuxLabelLookup(_ => { calls++; return [new("a$", "a.example", "Linux")]; }, clock);
        Assert.Single((await lookup.LookupAsync(["a", "missing"], default)).Values);
        Assert.Single((await lookup.LookupAsync(["A.", "missing"], default)).Values);
        Assert.Equal(1, calls);
        clock.Now += TimeSpan.FromMinutes(11);
        await lookup.LookupAsync(["a", "missing"], default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Unavailable_directory_has_global_cooldown_and_retries_later()
    {
        int calls = 0;
        var clock = new Clock();
        var lookup = new AdLinuxLabelLookup(_ => { calls++; throw new InvalidOperationException("offline"); }, clock);
        Assert.Contains("offline", (await lookup.LookupAsync(["a"], default)).Error);
        var cooldown = await lookup.LookupAsync(["different"], default);
        Assert.Null(cooldown.Error);
        Assert.False(cooldown.Available);
        Assert.Equal(1, calls);
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.NotNull((await lookup.LookupAsync(["a"], default)).Error);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Invalid_hosts_do_not_query_directory_and_batches_are_bounded()
    {
        int calls = 0;
        var lookup = new AdLinuxLabelLookup(_ => { calls++; return []; });
        Assert.Empty((await lookup.LookupAsync(["127.0.0.1", "::1", "*", "", "a)(b"], default)).Values);
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lookup.LookupAsync(new string[65], default));
    }

    [Fact]
    public async Task Slow_directory_returns_control_and_serializes_concurrent_tabs()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var lookup = new AdLinuxLabelLookup(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test release missing");
            return [new("a$", "a.example", "Linux")];
        });
        Task<AdLinuxLabels> first = lookup.LookupAsync(["a"], default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(first.IsCompleted);
            using var cts = new CancellationTokenSource();
            Task<AdLinuxLabels> second = lookup.LookupAsync(["a"], cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(1, calls);
        }
        finally { release.Set(); }
        Assert.Single((await first).Values);
        Assert.Single((await lookup.LookupAsync(["a"], default)).Values);
        Assert.Equal(1, calls);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

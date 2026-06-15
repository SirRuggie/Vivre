using System.Management.Automation;
using System.Text;
using Vivre.Core.Columns;
using Vivre.Core.PowerShell;
using Xunit;

namespace Vivre.Core.Tests.Columns;

public class CustomColumnProbeTests
{
    private static readonly IReadOnlyList<CustomColumnSpec> TwoColumns =
    [
        new("Serial", "(Get-CimInstance Win32_BIOS).SerialNumber"),
        new("DaysUp", "[int]((Get-Date) - (gcim Win32_OperatingSystem).LastBootUpTime).TotalDays"),
    ];

    [Fact]
    public async Task Returns_a_value_per_column()
    {
        var host = new FakeHost { Result = Row(("Serial", "7CG8123"), ("DaysUp", "42")) };

        IReadOnlyDictionary<string, string?> values =
            await new CustomColumnProbe(host).RunAsync("NYC-FP1", TwoColumns, null, CancellationToken.None);

        Assert.Equal("7CG8123", values["Serial"]);
        Assert.Equal("42", values["DaysUp"]);
    }

    [Fact]
    public async Task No_output_throws()
    {
        var host = new FakeHost { Result = new PSExecutionResult([], ["Connecting to remote server failed"], [], HadErrors: true) };

        CustomColumnProbeException ex = await Assert.ThrowsAsync<CustomColumnProbeException>(() =>
            new CustomColumnProbe(host).RunAsync("NYC-FP1", TwoColumns, null, CancellationToken.None));
        Assert.Contains("Connecting to remote server failed", ex.Message);
    }

    [Fact]
    public async Task Empty_column_list_makes_no_call()
    {
        var host = new FakeHost { Result = Row(("x", "y")) };

        IReadOnlyDictionary<string, string?> values =
            await new CustomColumnProbe(host).RunAsync("NYC-FP1", [], null, CancellationToken.None);

        Assert.Empty(values);
        Assert.False(host.LocalCalled);
        Assert.False(host.RemoteCalled);
    }

    [Fact]
    public async Task Local_host_runs_locally_remote_over_winrm()
    {
        var local = new FakeHost { Result = Row(("Serial", "A"), ("DaysUp", "1")) };
        await new CustomColumnProbe(local).RunAsync("localhost", TwoColumns, null, CancellationToken.None);
        Assert.True(local.LocalCalled);
        Assert.False(local.RemoteCalled);

        var remote = new FakeHost { Result = Row(("Serial", "A"), ("DaysUp", "1")) };
        await new CustomColumnProbe(remote).RunAsync("NYC-FP1", TwoColumns, null, CancellationToken.None);
        Assert.True(remote.RemoteCalled);
        Assert.False(remote.LocalCalled);
    }

    [Fact]
    public async Task Script_base64_encodes_each_one_liner_and_isolates_them()
    {
        var host = new FakeHost { Result = Row(("Serial", "A"), ("DaysUp", "1")) };
        await new CustomColumnProbe(host).RunAsync("NYC-FP1", TwoColumns, null, CancellationToken.None);

        // Each one-liner is base64'd, decoded into a ScriptBlock, and run in its own try/catch.
        foreach (CustomColumnSpec col in TwoColumns)
        {
            Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(col.Script)), host.Script);
            Assert.Contains(col.Name, host.Script);
        }

        Assert.Contains("ScriptBlock]::Create", host.Script);
        Assert.Contains("try {", host.Script);
        // Read via PSObject properties, never ConvertTo-Json (a JSON string has no properties).
        Assert.DoesNotContain("ConvertTo-Json", host.Script);
    }

    private static PSExecutionResult Row(params (string Name, object? Value)[] properties)
    {
        var o = new PSObject();
        foreach ((string name, object? value) in properties)
        {
            o.Properties.Add(new PSNoteProperty(name, value));
        }

        return new PSExecutionResult([o], [], [], HadErrors: false);
    }

    private sealed class FakeHost : IPowerShellHost
    {
        public bool LocalCalled { get; private set; }

        public bool RemoteCalled { get; private set; }

        public string Script { get; private set; } = string.Empty;

        public PSExecutionResult Result { get; init; } = new([], [], [], HadErrors: false);

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default)
        {
            LocalCalled = true;
            Script = script;
            return Task.FromResult(Result);
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false)
        {
            RemoteCalled = true;
            Script = script;
            return Task.FromResult(Result);
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new NotSupportedException();
    }
}

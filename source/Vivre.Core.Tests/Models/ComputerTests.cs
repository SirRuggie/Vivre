using Vivre.Core.Models;
using Xunit;

namespace Vivre.Core.Tests.Models;

public class ComputerTests
{
    [Fact]
    public void Constructor_sets_Name()
    {
        var computer = new Computer("WKS-FINANCE-01");

        Assert.Equal("WKS-FINANCE-01", computer.Name);
    }

    [Fact]
    public void Default_Name_is_empty_not_null()
    {
        var computer = new Computer();

        Assert.Equal(string.Empty, computer.Name);
    }

    [Fact]
    public void Setting_IsOnline_raises_PropertyChanged()
    {
        var computer = new Computer("WKS-FINANCE-01");
        var raised = new List<string?>();
        computer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        computer.IsOnline = true;

        Assert.Contains(nameof(Computer.IsOnline), raised);
    }

    [Fact]
    public void Setting_LastError_raises_PropertyChanged()
    {
        var computer = new Computer("WKS-FINANCE-01");
        var raised = new List<string?>();
        computer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        computer.LastError = "The RPC server is unavailable.";

        Assert.Contains(nameof(Computer.LastError), raised);
    }

    [Fact]
    public void Setting_same_value_does_not_raise_PropertyChanged()
    {
        var computer = new Computer("WKS-FINANCE-01") { IsOnline = true };
        var raisedCount = 0;
        computer.PropertyChanged += (_, _) => raisedCount++;

        computer.IsOnline = true; // unchanged

        Assert.Equal(0, raisedCount);
    }
}

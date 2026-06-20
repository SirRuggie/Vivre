using Vivre.Core.Models;
using Vivre.Core.Vitals;
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

    [Fact]
    public void Setting_Vitals_raises_PropertyChanged()
    {
        // Regression guard for the "Machine Details readings don't refresh after Check Vitals" bug: Vitals
        // must stay observable so an open detail panel's Vitals.* bindings re-resolve when ApplyVitals
        // replaces the snapshot. (Reverting it to a plain auto-property would fail this.)
        var computer = new Computer("WKS-FINANCE-01");
        var raised = new List<string?>();
        computer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        computer.Vitals = new MachineVitals(SystemDriveFreePercent: 12.5);

        Assert.Contains(nameof(Computer.Vitals), raised);
    }

    [Fact]
    public void Setting_VitalityReasons_raises_PropertyChanged()
    {
        var computer = new Computer("WKS-FINANCE-01");
        var raised = new List<string?>();
        computer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        computer.VitalityReasons = ["Disk 4% free"];

        Assert.Contains(nameof(Computer.VitalityReasons), raised);
    }
}

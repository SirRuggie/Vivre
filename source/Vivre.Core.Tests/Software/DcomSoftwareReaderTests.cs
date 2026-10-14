using Vivre.Core.Software;
using Xunit;

namespace Vivre.Core.Tests.Software;

public class DcomSoftwareReaderTests
{
    [Theory]
    [InlineData(2u, true)]
    [InlineData(1u, false)]
    [InlineData(5u, false)]
    [InlineData(uint.MaxValue, false)]
    public void Only_return_code_2_is_a_missing_optional_hive(uint returnValue, bool expected) =>
        Assert.Equal(expected, DcomSoftwareReader.IsMissingHiveReturn(returnValue));

    [Fact]
    public void Proven_match_survives_unrelated_hive_failure_with_warning()
    {
        var rows = new[] { new UninstallRow("CrowdStrike Sensor", "7.18", "CrowdStrike") };

        var result = DcomSoftwareReader.AssessSearch(
            [], false, false, rows, false, true, "CrowdStrike", "64-bit hive: access denied");

        Assert.True(result.Found);
        Assert.Equal("CrowdStrike Sensor", result.Name);
        Assert.Contains("access denied", result.Warning);
    }

    [Fact]
    public void Incomplete_search_without_match_throws_instead_of_absence()
    {
        SoftwareProbeException ex = Assert.Throws<SoftwareProbeException>(() =>
            DcomSoftwareReader.AssessSearch(
                [], false, false, [], false, true, "CrowdStrike", "64-bit hive: provider failure"));

        Assert.Contains("provider failure", ex.Message);
    }

    [Fact]
    public void Complete_primary_with_missing_optional_hive_proves_absence()
    {
        var result = DcomSoftwareReader.AssessSearch(
            [], false, true, [], true, true, "CrowdStrike", null);

        Assert.False(result.Found);
    }

    [Fact]
    public void Both_missing_hives_cannot_prove_absence()
    {
        Assert.Throws<SoftwareProbeException>(() =>
            DcomSoftwareReader.AssessSearch(
                [], true, true, [], true, true, "CrowdStrike", null));
    }

    [Fact]
    public void Incomplete_marker_without_warning_cannot_prove_absence()
    {
        Assert.Throws<SoftwareProbeException>(() =>
            DcomSoftwareReader.AssessSearch(
                [], false, false, [], false, true, "CrowdStrike", null));
    }

    [Fact]
    public async Task Pre_canceled_check_propagates_cancellation_without_touching_a_live_target()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DcomSoftwareReader().CheckAsync("unused-test-host", "CrowdStrike", null, cts.Token));
    }

    [Fact]
    public async Task Version_denial_preserves_proven_match_with_warning()
    {
        var provider = Provider(
            enumKeys: (path, _) => path.Contains("WOW6432Node") ? (0, []) : (0, ["Product"]),
            read: (_, value, _) => value switch
            {
                "DisplayName" => (0u, "CrowdStrike Sensor"),
                "DisplayVersion" => (5u, null),
                _ => (2u, null),
            });

        SoftwareCheckResult result = await new DcomSoftwareReader(provider)
            .CheckAsync("stub", "CrowdStrike", null, CancellationToken.None);

        Assert.True(result.Found);
        Assert.Null(result.Version);
        Assert.Contains("DisplayVersion", result.QueryWarning);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(5u)]
    public async Task Unproven_row_read_without_match_throws(uint returnValue)
    {
        var provider = Provider(
            enumKeys: (path, _) => path.Contains("WOW6432Node") ? (0, []) : (0, ["Unreadable"]),
            read: (_, value, _) => value == "DisplayName" ? (returnValue, null) : (2, null));

        SoftwareProbeException ex = await Assert.ThrowsAsync<SoftwareProbeException>(() =>
            new DcomSoftwareReader(provider).CheckAsync("stub", "CrowdStrike", null, CancellationToken.None));

        Assert.Contains(returnValue.ToString(), ex.Message);
    }

    [Fact]
    public async Task Sibling_and_hive_exceptions_do_not_discard_match()
    {
        var provider = Provider(
            enumKeys: (path, _) => path.Contains("WOW6432Node")
                ? throw new InvalidOperationException("second hive failed")
                : (0, ["Good", "Bad"]),
            read: (path, value, _) => path.EndsWith("Bad") && value == "DisplayName"
                ? throw new InvalidOperationException("sibling failed")
                : value == "DisplayName" ? (0u, "CrowdStrike Sensor") : (2u, null));

        SoftwareCheckResult result = await new DcomSoftwareReader(provider)
            .CheckAsync("stub", "CrowdStrike", null, CancellationToken.None);

        Assert.True(result.Found);
        Assert.Contains("sibling failed", result.QueryWarning);
        Assert.Contains("second hive failed", result.QueryWarning);
    }

    [Fact]
    public async Task Service_query_failure_is_unknown_not_not_found()
    {
        var provider = Provider(
            enumKeys: (_, _) => (0, []),
            services: _ => throw new InvalidOperationException("service provider failed"));

        SoftwareCheckResult result = await new DcomSoftwareReader(provider)
            .CheckAsync("stub", "CrowdStrike", "FalconSvc", CancellationToken.None);

        Assert.False(result.Found);
        Assert.Null(result.ServiceState);
        Assert.Contains("service provider failed", result.ServiceError);
    }

    [Fact]
    public async Task Completed_service_query_without_match_reports_not_found()
    {
        var provider = Provider(enumKeys: (_, _) => (0, []), services: _ => []);

        SoftwareCheckResult result = await new DcomSoftwareReader(provider)
            .CheckAsync("stub", "CrowdStrike", "FalconSvc", CancellationToken.None);

        Assert.Equal("not found", result.ServiceState);
        Assert.Null(result.ServiceError);
    }

    [Fact]
    public async Task Cancellation_during_value_read_propagates()
    {
        var provider = Provider(
            enumKeys: (path, _) => path.Contains("WOW6432Node") ? (0, []) : (0, ["Product"]),
            read: (_, _, _) => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DcomSoftwareReader(provider).CheckAsync("stub", "CrowdStrike", null, CancellationToken.None));
    }

    private static IDcomSoftwareProvider Provider(
        Func<string, CancellationToken, (uint ReturnValue, string[] SubKeys)>? enumKeys = null,
        Func<string, string, CancellationToken, (uint ReturnValue, string? Value)>? read = null,
        Func<CancellationToken, IReadOnlyList<DcomServiceRow>>? services = null) =>
        new FakeProvider(
            enumKeys ?? ((_, _) => (0, [])),
            read ?? ((_, _, _) => (2, null)),
            services ?? (_ => []));

    private sealed class FakeProvider(
        Func<string, CancellationToken, (uint ReturnValue, string[] SubKeys)> enumKeys,
        Func<string, string, CancellationToken, (uint ReturnValue, string? Value)> read,
        Func<CancellationToken, IReadOnlyList<DcomServiceRow>> services) : IDcomSoftwareProvider
    {
        public (uint ReturnValue, string[] SubKeys) EnumSubKeys(string hiveKey, CancellationToken ct) => enumKeys(hiveKey, ct);
        public (uint ReturnValue, string? Value) ReadStringValue(string path, string valueName, CancellationToken ct) => read(path, valueName, ct);
        public IReadOnlyList<DcomServiceRow> ReadServices(CancellationToken ct) => services(ct);
    }
}

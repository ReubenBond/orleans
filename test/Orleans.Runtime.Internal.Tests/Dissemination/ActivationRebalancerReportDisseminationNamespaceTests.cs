using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public sealed class ActivationRebalancerReportDisseminationNamespaceTests
{
    [Fact]
    public async Task PublishesSingleLatestValue()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var receiver = new FakeReceiver();
        var dissemination = new FakeDisseminationService();
        var disseminationNamespace = CreateNamespace(receiver, serializer);
        var report = CreateReport(RebalancerStatus.Executing, 0.25);

        Assert.True(await disseminationNamespace.PublishAsync(dissemination, report, CancellationToken.None));

        var version = Assert.Single(dissemination.PublishedVersions);
        var repair = disseminationNamespace.CreateRepair(new DisseminationRepairRequest(
            "report",
            fromVersion: null,
            toVersion: version,
            maxItemCount: 1,
            maxBatchBytes: 1024 * 1024,
            maxPayloadBytes: 1024 * 1024));

        Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
        var value = Assert.Single(repair.Values);
        Assert.Equal((0, version), (value.FromVersion, value.ToVersion));
        Assert.Equal(report.Host, Assert.Single(receiver.Reports).Host);
    }

    [Fact]
    public async Task RejectsOlderReportsAfterApplyingLatest()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var receiver = new FakeReceiver();
        var disseminationNamespace = CreateNamespace(receiver, serializer);
        var olderReport = CreateReport(RebalancerStatus.Executing, 0.5);
        var latestReport = CreateReport(RebalancerStatus.Suspended, 0.1);
        var older = new DisseminationValue(
            "report",
            fromVersion: 0,
            toVersion: 10,
            serializer.SerializeToArray(olderReport));
        var latest = new DisseminationValue(
            "report",
            fromVersion: 0,
            toVersion: 11,
            serializer.SerializeToArray(latestReport));

        Assert.Equal(
            DisseminationApplyResult.Applied,
            await disseminationNamespace.ApplyValueAsync(latest, CancellationToken.None));
        Assert.Equal(
            DisseminationApplyResult.Obsolete,
            await disseminationNamespace.ApplyValueAsync(older, CancellationToken.None));

        var received = Assert.Single(receiver.Reports);
        Assert.Equal(latestReport.Status, received.Status);
        Assert.Equal(latestReport.ClusterImbalance, received.ClusterImbalance);
    }

    private static ActivationRebalancerReportDisseminationNamespace CreateNamespace(
        IActivationRebalancerReportReceiver receiver,
        Serializer serializer)
    {
        var options = new ActivationRebalancerOptions();
        options.ReportDissemination.Enabled = true;
        return new(receiver, Options.Create(options), TimeProvider.System, serializer);
    }

    private static RebalancingReport CreateReport(RebalancerStatus status, double clusterImbalance) =>
        new()
        {
            Host = SiloAddress.FromParsableString("127.0.0.1:100@100"),
            Status = status,
            SuspensionDuration = status == RebalancerStatus.Suspended ? TimeSpan.FromMinutes(1) : null,
            ClusterImbalance = clusterImbalance,
            Statistics = ImmutableArray<RebalancingStatistics>.Empty,
        };

    private sealed class FakeReceiver : IActivationRebalancerReportReceiver
    {
        public List<RebalancingReport> Reports { get; } = [];

        public void ReceiveReport(RebalancingReport report) => Reports.Add(report);
    }

    private sealed class FakeDisseminationService : IDisseminationService
    {
        public List<long> PublishedVersions { get; } = [];

        public ValueTask<bool> Publish(
            IDisseminationNamespace disseminationNamespace,
            DisseminationKey key,
            long version,
            CancellationToken cancellationToken)
        {
            PublishedVersions.Add(version);
            return ValueTask.FromResult(true);
        }
    }
}

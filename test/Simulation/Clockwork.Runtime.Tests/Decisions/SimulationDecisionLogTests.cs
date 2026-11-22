using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;

namespace Orleans.Testing.Simulation.Runtime.Tests.Decisions;

/// <summary>
/// Covers <see cref="SimulationDecisionLog"/> and the underlying <see cref="SimulationDecisionRecord"/>
/// model: monotonic ordering, exact field roundtrip, and thread-safety of concurrent recording.
/// </summary>
public sealed class SimulationDecisionLogTests
{
    private static SimulationDecisionRequest MakeRequest(string sourceId = "site-1", string selectedResult = "42") =>
        new(
            Domain: SimulationSeedDomain.Network,
            Kind: SimulationDecisionKind.RandomDraw,
            SourceId: sourceId,
            InputMetadata: "[0, 100)",
            SelectedResult: selectedResult,
            RuntimeId: Guid.NewGuid(),
            NodeId: "node-1",
            LogicalExecutionId: SimulationLogicalExecutionId.None);

    [Fact]
    public void RecordsIsEmptyForANewLog()
    {
        var log = new SimulationDecisionLog();
        Assert.Empty(log.Records);
    }

    [Fact]
    public void RecordAssignsMonotonicallyIncreasingSequentialIds()
    {
        var log = new SimulationDecisionLog();

        var first = log.Record(MakeRequest());
        var second = log.Record(MakeRequest());
        var third = log.Record(MakeRequest());

        Assert.Equal(0, first.Id.Sequence);
        Assert.Equal(1, second.Id.Sequence);
        Assert.Equal(2, third.Id.Sequence);
    }

    [Fact]
    public void RecordsReflectsExactInsertionOrder()
    {
        var log = new SimulationDecisionLog();
        log.Record(MakeRequest(sourceId: "first"));
        log.Record(MakeRequest(sourceId: "second"));
        log.Record(MakeRequest(sourceId: "third"));

        Assert.Equal(["first", "second", "third"], log.Records.Select(r => r.SourceId));
    }

    [Fact]
    public void RecordRoundTripsEveryFieldOfTheRequest()
    {
        var log = new SimulationDecisionLog();
        var runtimeId = Guid.NewGuid();
        var logicalExecutionId = new SimulationLogicalExecutionIdSource().Next();

        var request = new SimulationDecisionRequest(
            Domain: SimulationSeedDomain.Buggify,
            Kind: SimulationDecisionKind.FaultActivation,
            SourceId: "some-call-site",
            InputMetadata: "probability=0.1",
            SelectedResult: "activated",
            RuntimeId: runtimeId,
            NodeId: "node-42",
            LogicalExecutionId: logicalExecutionId);

        var recorded = log.Record(request);

        Assert.Equal(SimulationSeedDomain.Buggify, recorded.Domain);
        Assert.Equal(SimulationDecisionKind.FaultActivation, recorded.Kind);
        Assert.Equal("some-call-site", recorded.SourceId);
        Assert.Equal("probability=0.1", recorded.InputMetadata);
        Assert.Equal("activated", recorded.SelectedResult);
        Assert.Equal(runtimeId, recorded.RuntimeId);
        Assert.Equal("node-42", recorded.NodeId);
        Assert.Equal(logicalExecutionId, recorded.LogicalExecutionId);
    }

    [Fact]
    public void RecordAllowsNullSourceIdAndNullNodeIdForClusterLevelDecisions()
    {
        var log = new SimulationDecisionLog();
        var request = new SimulationDecisionRequest(
            Domain: SimulationSeedDomain.Scheduler,
            Kind: SimulationDecisionKind.SchedulingOrder,
            SourceId: null,
            InputMetadata: null,
            SelectedResult: "run-item-3",
            RuntimeId: Guid.NewGuid(),
            NodeId: null,
            LogicalExecutionId: SimulationLogicalExecutionId.None);

        var recorded = log.Record(request);

        Assert.Null(recorded.SourceId);
        Assert.Null(recorded.InputMetadata);
        Assert.Null(recorded.NodeId);
    }

    [Fact]
    public void ConcurrentRecordCallsEachGetAUniqueSequentialId()
    {
        var log = new SimulationDecisionLog();
        const int count = 200;

        Parallel.For(0, count, _ => log.Record(MakeRequest()));

        var ids = log.Records.Select(r => r.Id.Sequence).OrderBy(x => x).ToArray();
        Assert.Equal(count, ids.Length);
        Assert.Equal(Enumerable.Range(0, count).Select(i => (long)i), ids);
    }

#pragma warning disable CA1859 // Deliberately exercised through the interface, not the concrete type.
    [Fact]
    public void AsISimulationDecisionLogInterfaceBehavesIdentically()
    {
        var log = new SimulationDecisionLog();
        var recorded = RecordThroughInterface(log, MakeRequest());
        Assert.Same(log.Records[0], recorded);
    }

    private static SimulationDecisionRecord RecordThroughInterface(ISimulationDecisionLog log, SimulationDecisionRequest request) =>
        log.Record(request);
#pragma warning restore CA1859
}

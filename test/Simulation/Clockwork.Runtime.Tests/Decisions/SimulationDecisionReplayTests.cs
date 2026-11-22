using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;

namespace Orleans.Testing.Simulation.Runtime.Tests.Decisions;

/// <summary>
/// Covers <see cref="SimulationInMemoryDecisionReplayReader"/> and
/// <see cref="SimulationDecisionReplayValidator"/>: exact replay of an identical sequence
/// succeeds, the first content divergence is detected (and only the first - no cascading
/// re-throws), replay exhaustion is treated as a divergence, and comparison is by decision
/// <em>content</em> only - not by the run-identifying fields (<c>Id</c>/<c>RuntimeId</c>/<c>NodeId</c>/
/// <c>LogicalExecutionId</c>) that are expected to differ between the original recording and a
/// later replay.
/// </summary>
public sealed class SimulationDecisionReplayTests
{
    private static SimulationDecisionRecord MakeRecord(
        long sequence,
        SimulationSeedDomain domain = SimulationSeedDomain.Network,
        SimulationDecisionKind kind = SimulationDecisionKind.RandomDraw,
        string? sourceId = "site-1",
        string? inputMetadata = "[0, 100)",
        string selectedResult = "42",
        Guid? runtimeId = null,
        string? nodeId = "node-1") =>
        new(
            new SimulationDecisionId(sequence),
            domain,
            kind,
            sourceId,
            inputMetadata,
            selectedResult,
            runtimeId ?? Guid.NewGuid(),
            nodeId,
            SimulationLogicalExecutionId.None);

    [Fact]
    public void InMemoryReaderReturnsRecordsInOrderThenReportsExhaustion()
    {
        var records = new[] { MakeRecord(0), MakeRecord(1) };
        var reader = new SimulationInMemoryDecisionReplayReader(records);

        Assert.Equal(2, reader.RemainingCount);
        Assert.True(reader.TryGetNext(out var first));
        Assert.Equal(records[0], first);
        Assert.Equal(1, reader.RemainingCount);

        Assert.True(reader.TryGetNext(out var second));
        Assert.Equal(records[1], second);
        Assert.Equal(0, reader.RemainingCount);

        Assert.False(reader.TryGetNext(out var third));
        Assert.Null(third);
    }

    [Fact]
    public void InMemoryReaderThrowsForNullRecordsList()
    {
        Assert.Throws<ArgumentNullException>(() => new SimulationInMemoryDecisionReplayReader(null!));
    }

    [Fact]
    public void ValidateSucceedsForAnExactReplayEvenWhenRunIdentityDiffers()
    {
        var recorded = new[]
        {
            MakeRecord(0, sourceId: "a", selectedResult: "1", runtimeId: Guid.NewGuid(), nodeId: "node-1"),
            MakeRecord(1, sourceId: "b", selectedResult: "2", runtimeId: Guid.NewGuid(), nodeId: "node-1"),
        };

        var validator = new SimulationDecisionReplayValidator(new SimulationInMemoryDecisionReplayReader(recorded));

        // A "replay" with the exact same content but entirely different run-identifying fields
        // (different Id sequence base, different RuntimeId, different NodeId) must still pass,
        // because those fields identify *which run* produced the decision, not its content.
        var replayed1 = MakeRecord(100, sourceId: "a", selectedResult: "1", runtimeId: Guid.NewGuid(), nodeId: "node-99");
        var replayed2 = MakeRecord(101, sourceId: "b", selectedResult: "2", runtimeId: Guid.NewGuid(), nodeId: "node-99");

        validator.Validate(replayed1);
        validator.Validate(replayed2);
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("kind")]
    [InlineData("sourceId")]
    [InlineData("inputMetadata")]
    [InlineData("selectedResult")]
    public void ValidateThrowsAtTheFirstDivergentField(string fieldToDiverge)
    {
        var recorded = new[]
        {
            MakeRecord(0, sourceId: "a", selectedResult: "1"),
            MakeRecord(1, sourceId: "b", selectedResult: "2"),
        };
        var validator = new SimulationDecisionReplayValidator(new SimulationInMemoryDecisionReplayReader(recorded));

        // First decision always matches, so the validator advances past it.
        validator.Validate(MakeRecord(0, sourceId: "a", selectedResult: "1"));

        var divergent = fieldToDiverge switch
        {
            "domain" => MakeRecord(1, domain: SimulationSeedDomain.Application, sourceId: "b", selectedResult: "2"),
            "kind" => MakeRecord(1, kind: SimulationDecisionKind.Choice, sourceId: "b", selectedResult: "2"),
            "sourceId" => MakeRecord(1, sourceId: "different", selectedResult: "2"),
            "inputMetadata" => MakeRecord(1, sourceId: "b", inputMetadata: "different", selectedResult: "2"),
            "selectedResult" => MakeRecord(1, sourceId: "b", selectedResult: "different"),
            _ => throw new InvalidOperationException(),
        };

        var exception = Assert.Throws<SimulationDecisionReplayMismatchException>(() => validator.Validate(divergent));
        Assert.Equal(recorded[1], exception.Expected);
        Assert.Equal(divergent, exception.Actual);
    }

    [Fact]
    public void ValidateDoesNotThrowAgainForDecisionsAfterTheFirstDivergence()
    {
        var recorded = new[] { MakeRecord(0, selectedResult: "expected") };
        var validator = new SimulationDecisionReplayValidator(new SimulationInMemoryDecisionReplayReader(recorded));

        Assert.Throws<SimulationDecisionReplayMismatchException>(() => validator.Validate(MakeRecord(0, selectedResult: "actual")));

        // Once diverged, further Validate calls must not throw again (cascading noise after the
        // first real divergence is not useful) - even though the reader is now exhausted too.
        validator.Validate(MakeRecord(1, selectedResult: "whatever"));
        validator.Validate(MakeRecord(2, selectedResult: "anything"));
    }

    [Fact]
    public void ValidateThrowsWhenTheReplaySourceIsExhaustedButANewDecisionWasMade()
    {
        var recorded = new[] { MakeRecord(0) };
        var validator = new SimulationDecisionReplayValidator(new SimulationInMemoryDecisionReplayReader(recorded));

        validator.Validate(MakeRecord(0));

        var exception = Assert.Throws<SimulationDecisionReplayMismatchException>(() => validator.Validate(MakeRecord(1)));
        Assert.Null(exception.Expected);
        Assert.NotNull(exception.Actual);
    }

    [Fact]
    public void ValidateCompleteRejectsAnUnconsumedRecordedSuffix()
    {
        var recorded = new[] { MakeRecord(0), MakeRecord(1, sourceId: "extra") };
        var validator = new SimulationDecisionReplayValidator(new SimulationInMemoryDecisionReplayReader(recorded));
        validator.Validate(MakeRecord(100));

        var exception = Assert.Throws<SimulationDecisionReplayMismatchException>(validator.ValidateComplete);

        Assert.Equal(recorded[1], exception.Expected);
        Assert.Null(exception.Actual);
        Assert.Contains("unconsumed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCompleteSucceedsAfterExactConsumption()
    {
        var validator = new SimulationDecisionReplayValidator(
            new SimulationInMemoryDecisionReplayReader([MakeRecord(0)]));
        validator.Validate(MakeRecord(100));

        validator.ValidateComplete();
    }

    [Fact]
    public void ValidateThrowsForNullActual()
    {
        var validator = new SimulationDecisionReplayValidator(new SimulationInMemoryDecisionReplayReader([]));
        Assert.Throws<ArgumentNullException>(() => validator.Validate(null!));
    }

    [Fact]
    public void ConstructorThrowsForNullReader()
    {
        Assert.Throws<ArgumentNullException>(() => new SimulationDecisionReplayValidator(null!));
    }

    [Fact]
    public void EndToEndRecordThenExactReplayRoundTripsThroughARealLog()
    {
        var log = new SimulationDecisionLog();
        log.Record(new SimulationDecisionRequest(
            SimulationSeedDomain.Network, SimulationDecisionKind.RandomDraw, "site-a", "[0,10)", "3", Guid.NewGuid(), "node-1", default));
        log.Record(new SimulationDecisionRequest(
            SimulationSeedDomain.Network, SimulationDecisionKind.RandomDraw, "site-b", "[0,10)", "7", Guid.NewGuid(), "node-1", default));

        var reader = new SimulationInMemoryDecisionReplayReader(log.Records);
        var validator = new SimulationDecisionReplayValidator(reader);

        // Simulate a second run producing the exact same content but with fresh run identity.
        var replayLog = new SimulationDecisionLog();
        var replayed1 = replayLog.Record(new SimulationDecisionRequest(
            SimulationSeedDomain.Network, SimulationDecisionKind.RandomDraw, "site-a", "[0,10)", "3", Guid.NewGuid(), "node-1", default));
        var replayed2 = replayLog.Record(new SimulationDecisionRequest(
            SimulationSeedDomain.Network, SimulationDecisionKind.RandomDraw, "site-b", "[0,10)", "7", Guid.NewGuid(), "node-1", default));

        validator.Validate(replayed1);
        validator.Validate(replayed2);
    }
}

using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Each check of <see cref="LedgerTrace"/> fires when the log shows what its invariant forbids.
/// </summary>
public sealed class LedgerTraceTests
{
    private const string TailCheck =
        "ledger: a replica's contiguous tail is at most the writer's completed prefix, or the recorded T once the map closes the ledger, and never decreases within a process (LR-I6)";

    private const string CloseCheck = "ledger: the map closes a ledger only at a candidate end of a completed recovery (LG-I9)";

    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };

    private static LedgerId Ledger => SimulatedStorage.Ledger;

    /// <summary>
    /// A replica which answers a writer write written after it answered a seal violates LR-I8.
    /// </summary>
    [Fact]
    public void AWrittenAnswerAfterASealFails() =>
        AssertFires("ledger: no replica answers a writer write Written after it answered sealed (LR-I8)", static (ledger, node) =>
        {
            ledger.Served("recovery", node, Ledger, LedgerOperation.Seal, null, Slot.Zero, LedgerTrace.TailAnswer, new LedgerReplicaTail(Slot.Zero, IsSealed: true), floor: null);
            ledger.Served("writer", node, Ledger, LedgerOperation.WriterWrite, 0, Slot.Zero, nameof(ReplicaAnswers.Written), tail: null, floor: null);
            return Task.CompletedTask;
        });

    /// <summary>
    /// A replica which reports a contiguous tail above the writer's completed prefix violates LR-I6.
    /// </summary>
    [Fact]
    public void ATailAboveTheWritersPrefixFails() =>
        AssertFires(TailCheck, static (ledger, node) =>
        {
            ledger.Served("checker", node, Ledger, LedgerOperation.TailRead, null, Slot.Zero, LedgerTrace.TailAnswer, new LedgerReplicaTail(new Slot(1), IsSealed: false), floor: null);
            return Task.CompletedTask;
        });

    /// <summary>
    /// A replica which reports a contiguous tail below one an operation it answered carried violates LR-I6, though the
    /// tail lies within the writer's completed prefix.
    /// </summary>
    [Fact]
    public void ATailWhichDecreasesFails() =>
        AssertFires(TailCheck, static async (ledger, node) =>
        {
            await using var writer = AnsweringLedger(default);
            for (var address = 0; address < 4; address++)
            {
                using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(address));
                _ = await ledger.WriteAsync(writer, "writer", Ledger, new Slot(address), payload);
            }

            ledger.Served("writer", node, Ledger, LedgerOperation.WriterWrite, 3, new Slot(3), nameof(ReplicaAnswers.Written), tail: null, floor: null);
            var floor = ledger.Floor(node, Ledger);
            ledger.Served("checker", node, Ledger, LedgerOperation.TailRead, null, Slot.Zero, LedgerTrace.TailAnswer, new LedgerReplicaTail(new Slot(2), IsSealed: false), floor);
        });

    /// <summary>
    /// A recovery whose end the writer saw written violates LP-I7.
    /// </summary>
    [Fact]
    public void ARecoveryEndingAtAWrittenAddressFails() =>
        AssertFires("ledger: a recovery's end lies in [c, c + 2K] and the writer never sees it written (LP-I7)", static async (ledger, node) =>
        {
            await using var writer = AnsweringLedger(default);
            using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(0));
            _ = await ledger.WriteAsync(writer, "writer", Ledger, Slot.Zero, payload);

            // The writer's write reached none of the recovery's replicas, so the recovery ends at the written address.
            await using var recoveryLedger = EmptyLedger();
            _ = await ledger.RecoverAsync(recoveryLedger, "recovery", Ledger);
        });

    /// <summary>
    /// A seal check which begins after a recovery completed and reports the ledger unsealed violates LP-I9.
    /// </summary>
    [Fact]
    public void AnUnsealedCheckAfterARecoveryFails() =>
        AssertFires("ledger: a seal check begun after a recovery completed reports sealed (LP-I9)", static async (ledger, node) =>
        {
            await using (var recoveryLedger = EmptyLedger())
            {
                _ = await ledger.RecoverAsync(recoveryLedger, "recovery", Ledger);
            }

            await using var checker = AnsweringLedger(new LedgerReplicaTail(Slot.Zero, IsSealed: false));
            _ = await ledger.CheckSealAsync(checker, "checker", Ledger);
        });

    /// <summary>
    /// Once the map closes the ledger at <c>T</c>, a replica may report <c>T</c>, which a <see cref="Ledger"/> that learned
    /// it carries (LP-I11), though the writer's completed prefix lies lower; a tail above <c>T</c> violates LR-I6.
    /// </summary>
    [Fact]
    public void ATailAboveTheRecordedTFails()
    {
        var violation = AssertFires(TailCheck, static async (ledger, node) =>
        {
            await using (var recoveryLedger = await LedgerOverAsync(written: 2))
            {
                Assert.Equal(new Slot(2), (await ledger.RecoverAsync(recoveryLedger, "recovery", Ledger)).End);
            }

            ledger.Closed(Ledger, "recovery", 2);
            ledger.Served("checker", node, Ledger, LedgerOperation.TailRead, null, Slot.Zero, LedgerTrace.TailAnswer, new LedgerReplicaTail(new Slot(2), IsSealed: true), floor: null);
            ledger.Served("checker", node, Ledger, LedgerOperation.TailRead, null, Slot.Zero, LedgerTrace.TailAnswer, new LedgerReplicaTail(new Slot(3), IsSealed: true), floor: null);
        });

        Assert.Contains("reported contiguous tail 3 of ledger 1 to checker, above its recorded T 2", violation.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A writer's slot which completes at or above a recovery's candidate end violates LP-I8. A log in which the writer's
    /// slot 1 completes before slot 0, past an end the writer never saw written, shows the check alone.
    /// </summary>
    [Fact]
    public void ACompletionAtOrAboveACandidateEndFails() =>
        AssertFires("ledger: no slot at or above a recovery's candidate end completes (LP-I8)", static async (ledger, node) =>
        {
            await using (var recoveryLedger = EmptyLedger())
            {
                _ = await ledger.RecoverAsync(recoveryLedger, "recovery", Ledger);
            }

            CompleteOutOfOrder(ledger);
        });

    /// <summary>
    /// A map which closes the ledger at an end no completed recovery returned violates LG-I9.
    /// </summary>
    [Fact]
    public void ACloseAtAnEndNoRecoveryReturnedFails() =>
        AssertFires(CloseCheck, static (ledger, node) =>
        {
            ledger.Closed(Ledger, "recovery", 5);
            return Task.CompletedTask;
        });

    /// <summary>
    /// A map which closes the ledger at one candidate end and then at another violates LG-I9: it records one end for
    /// the ledger's life.
    /// </summary>
    [Fact]
    public void ASecondCloseAtAnotherEndFails() =>
        AssertFires(CloseCheck, static async (ledger, node) =>
        {
            await using (var empty = EmptyLedger())
            {
                Assert.Equal(Slot.Zero, (await ledger.RecoverAsync(empty, "recovery-1", Ledger)).End);
            }

            await using (var written = await LedgerOverAsync(written: 2))
            {
                Assert.Equal(new Slot(2), (await ledger.RecoverAsync(written, "recovery-2", Ledger)).End);
            }

            ledger.Closed(Ledger, "recovery-1", 0);
            ledger.Closed(Ledger, "recovery-1", 0);
            ledger.Closed(Ledger, "recovery-2", 2);
        });

    /// <summary>
    /// A recovery whose close loses while the map holds the ledger open violates LG-I9: a close loses only to a recorded
    /// <c>T</c>.
    /// </summary>
    [Fact]
    public void ALostCloseOfAnOpenLedgerFails() =>
        AssertFires(CloseCheck, static (ledger, node) =>
        {
            ledger.Superseded(Ledger, "recovery");
            return Task.CompletedTask;
        });

    /// <summary>
    /// A client which learns a <c>T</c> the map did not record violates LG-I9.
    /// </summary>
    [Fact]
    public void LearningATTheMapDidNotRecordFails() =>
        AssertFires(CloseCheck, static async (ledger, node) =>
        {
            await using var learner = EmptyLedger();
            ledger.ObserveRecordedTail(learner, "reader", Ledger, Slot.Zero);
        });

    /// <summary>
    /// A recovery which ends at or below a writer's slot which already completed violates LP-I8, though the writer never
    /// saw the end written.
    /// </summary>
    [Fact]
    public void ARecoveryEndingAtOrBelowACompletedSlotFails() =>
        AssertFires("ledger: no slot at or above a recovery's candidate end completes (LP-I8)", static async (ledger, node) =>
        {
            CompleteOutOfOrder(ledger);

            await using var recoveryLedger = EmptyLedger();
            _ = await ledger.RecoverAsync(recoveryLedger, "recovery", Ledger);
        });

    /// <summary>
    /// A writer's seal check which finds its ledger sealed and does not stop the writer's lifetime violates the open
    /// check's rule (ledgers.md §8.3): here the writer's process checks through its <see cref="Ledger"/> rather than its
    /// writer, as an open check which stops nothing would.
    /// </summary>
    [Fact]
    public void ASealedCheckWhichStopsNothingFails() =>
        AssertFires("ledger: a writer's seal check which finds its ledger sealed stops its lifetime (ledgers §8.3)", static async (ledger, node) =>
        {
            await using var writerLedger = EmptyLedger();
            await using var writer = writerLedger.CreateWriter(SimulationEnvironment.Clock);
            ledger.TraceWriter(writer, "writer", Ledger);
            await using var sealedLedger = AnsweringLedger(new LedgerReplicaTail(Slot.Zero, IsSealed: true));
            _ = await ledger.CheckSealAsync(sealedLedger, "writer", Ledger);
        });

    /// <summary>
    /// A recovery holds only the seal answers it handled itself: a client's next recovery of the ledger marks no read
    /// from the answers its last one handled (LP-I7).
    /// </summary>
    [Fact]
    public void ARecoveryHoldsOnlyTheSealAnswersItHandled() => _ = SeededSimulation.Run(9, static async environment =>
    {
        var ledger = new LedgerTrace(environment);
        var answer = new LedgerReplicaTail(Slot.Zero, IsSealed: true);
        ledger.Handled("recovery", "replica-0", Ledger, new RecoveryStep(RecoveryStepKind.Seal, 0, Address: null, answer, RecoveryAnswer.Tail, AfterSeal: false, Failure: null, Stopped: false));
        Assert.True(ledger.IsSealHandled("recovery", "replica-0", Ledger));

        await using var recoveryLedger = EmptyLedger();
        _ = await ledger.RecoverAsync(recoveryLedger, "recovery", Ledger);
        Assert.False(ledger.IsSealHandled("recovery", "replica-0", Ledger));
    });

    private static Ledger EmptyLedger() => Over([.. Enumerable.Range(0, 3).Select(static _ => new InMemoryLedgerReplica(Options))]);

    /// <summary>
    /// Builds a <see cref="Ledger"/> over <paramref name="replicas"/> with quorum 2, as a ledger on nodes of fresh
    /// identities, whose recoveries log their steps in the ledger trace.
    /// </summary>
    private static Ledger Over(ILedgerReplica[] replicas)
    {
        var nodes = new LedgerLocation.Nodes([.. replicas.Select(static _ => NodeId.New())]);
        return new(Ledger, LedgerQuorum.ForNodes(Ledger, nodes.Replicas, replicas, quorum: 2, Options, SimulationEnvironment.Clock, localNode: null, LedgerTrace.RecoveryObserver), nodes);
    }

    /// <summary>
    /// Builds a <see cref="Ledger"/> over three replicas which answer every write written and every contiguous-tail read
    /// <paramref name="tail"/>, as a client's view of replicas the test does not run.
    /// </summary>
    private static Ledger AnsweringLedger(LedgerReplicaTail tail) => Over([.. Enumerable.Range(0, 3).Select(_ => new AnsweringLedgerReplica(tail))]);

    /// <summary>
    /// Logs the writer's slot 1 completing before slot 0, as a lifetime which completed slots out of order would (LP-I3).
    /// </summary>
    private static void CompleteOutOfOrder(LedgerTrace ledger)
    {
        ledger.Acquired("writer", Ledger, 0);
        ledger.Acquired("writer", Ledger, 1);
        ledger.Completed("writer", Ledger, 1);
    }

    /// <summary>
    /// Builds a <see cref="Ledger"/> over three memory replicas, each holding a batch at the first
    /// <paramref name="written"/> addresses.
    /// </summary>
    private static async Task<Ledger> LedgerOverAsync(int written)
    {
        InMemoryLedgerReplica[] replicas = [.. Enumerable.Range(0, 3).Select(static _ => new InMemoryLedgerReplica(Options))];
        foreach (var replica in replicas)
        {
            for (var address = 0; address < written; address++)
            {
                using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(address));
                Assert.Equal(ReplicaAnswers.Written, await replica.WriteAsync(new Slot(address), payload, Slot.Zero, recovery: false));
            }
        }

        return Over(replicas);
    }

    private static InvariantViolation AssertFires(string invariant, Func<LedgerTrace, LedgerStoreNode, Task> breakIt)
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(9, async environment =>
        {
            var node = new LedgerStoreNode(environment, "replica-0", SimulatedStorage.Cluster);
            await breakIt(new LedgerTrace(environment), node);
        }));

        var violation = Assert.IsType<InvariantViolationException>(failure.InnerException);
        Assert.Equal(invariant, violation.Violation!.Invariant);
        return violation.Violation;
    }

    /// <summary>
    /// A replica which answers every write written and every contiguous-tail read with one answer, as a client's view of a
    /// quorum the test does not run.
    /// </summary>
    private sealed class AnsweringLedgerReplica(LedgerReplicaTail tail) : ILedgerReplica
    {
        public ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReplicaAnswers.Written);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReplicaAnswers.Missing);

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReplicaAnswers.Missing);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReplicaTailRead>(tail);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

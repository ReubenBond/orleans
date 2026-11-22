using System.Globalization;
using System.Text.RegularExpressions;
using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Conformance;

/// <summary>
/// The fold oracle (ledger-protocol.md §2, §8): a primary's <see cref="Ledger"/> and <see cref="LedgerWriter"/> over
/// memory replicas, or LedgerStore replicas, behind a recording wrapper, with checks and recoveries in its process and
/// in others, conform to the ledger storage contract's spec (<see cref="LedgerStorageSpec"/>, LL-I1 to LL-I9) with the
/// quorum's recording oracle (<see cref="LedgerQuorumOracle"/>). The recording checks every contiguous tail a replica request carries against the completed
/// prefix and the recorded <c>T</c>; that a recovery raises its process's known contiguous tail to at most the greater of
/// its prior value and its <c>c</c>, which the harness takes from the first <c>N − Q + 1</c> seal answers the recovery
/// handled, each a different answer the recording holds; and that a
/// recovery in the writer's process stops its lifetime before its first seal request (LP-I11, LP-I14). Accordant
/// generates every sequence up to a small depth; CsCheck generates long random sequences, and sequences with groups of
/// concurrent appends and reads, and shrinks a failure.
/// </summary>
/// <remarks>
/// The replicas are skewed so that the checks discriminate (<see cref="SkewedLedgerReplica"/>). One replica, which each
/// case chooses from its seed, leads: it answers seals after the others and alone answers a read below the tail it
/// carries with a batch, so its contiguous tail can lead those of the first <c>N − Q + 1</c> seal answers. The others
/// answer such a read missing, fail it, or hold it past the hedge delay on the quorum's fake clock, which the harness
/// advances, so the primary's reads, which prefer the first replica, its own node's, take the batch from it, fall back
/// past a hole, and hedge (LP-I10, ledger-protocol.md §8.3). An append can land its write on every replica while every
/// answer to it is lost, so a recovery finds a batch past the completed prefix, and the recorded <c>T</c> exceeds it.
/// </remarks>
public sealed class LedgerConformanceTests : IDisposable
{
    private const int Replicas = 3;
    private const int Quorum = 2;
    private const int Window = 2;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact(Timeout = 120_000)]
    public async Task EverySequenceToASmallDepthConforms()
    {
        var coverage = new LedgerCoverage();
        var kit = Kit(TestContext.Current.CancellationToken, coverage: coverage);
        var spec = QuorumSpec();
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LedgerOutcome>(operation).With(request, request.ToString()));

        Add(LedgerStorageSpec.Append, new LedgerAppend(1));
        Add(LedgerStorageSpec.Append, new LedgerAppend(2));
        Add(LedgerStorageSpec.AppendUnanswered, new LedgerAppendUnanswered(3));
        Add(LedgerStorageSpec.AppendUnanswered, new LedgerAppendUnanswered(5, Landed: false));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(0));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(1));
        Add(LedgerStorageSpec.Check, new LedgerCheck());
        Add(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true));
        Add(LedgerStorageSpec.Learn, new LedgerLearn(Elsewhere: true));
        Add(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: false));
        Add(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true));
        Add(LedgerStorageSpec.Observe, LedgerObserve.Instance);
        Add(LedgerStorageSpec.Acquire, new LedgerAcquire(4, AcquireUse.AppendTwice));
        Add(LedgerStorageSpec.Drop, new LedgerDrop(Every: true));

        var sequences = spec.GenerateTests(new LedgerState(), inputs, kit.GenerationOptions(maxDepth: 5));
        Assert.NotEmpty(sequences);
        await kit.RunAsync(sequences);

        // A recovery found an unresolved append's batch past the completed prefix, and the primary's process then learned
        // that T, so the recorded T, not the completed prefix, bounded the tails its requests carried.
        Assert.True(coverage.ObservedPastCompleted > 0, "No sequence learned a recorded T above the completed prefix.");
    }

    /// <summary>
    /// Random sequences conform, and their reads below the tail took the batch from the preferred replica, fell back past
    /// a hole there, and hedged a held replica.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RandomSequencesConform()
    {
        var coverage = new LedgerCoverage();
        var kit = Kit(TestContext.Current.CancellationToken, coverage: coverage);
        await kit.SampleAsync(kit.Sequences(30), iterations: 300);

        Assert.True(coverage.Hedges > 0, "No read below the tail hedged.");
        Assert.True(coverage.MissingFallbacks > 0, "No read below the tail fell back past a replica's hole.");
    }

    /// <summary>
    /// Appends and reads run in concurrent groups, so each append's write is sent while up to <c>K</c> lower slots are in
    /// flight: the tail it carries is at most the prefix completed when it was sent (LP-I2, LP-I11).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ConcurrentAppendsAndReadsConform()
    {
        var kit = Kit(TestContext.Current.CancellationToken);
        var appendsAndReads = Gen.Frequency(
            (3, Values.Select(static value => new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(value)))),
            (1, Slots.Select(static slot => new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(slot)))));
        var rounds = Gen.Frequency(
            (3, Calls.Select(static call => new ConformanceRound([call]))),
            (2, appendsAndReads.Array[2, 4].Select(static calls => new ConformanceRound(calls))));
        await kit.SampleAsync(rounds.Array[1, 20].Select(static rounds => new ConformanceCase(rounds)), iterations: 300);
    }

    /// <summary>
    /// The quorum's oracle refuses a recovery which raised its known contiguous tail to its end above its <c>c</c>, the
    /// <c>learn-own-end</c> mutant (LP-I11), which the contract alone allows, as it does the recovery raising it to
    /// <c>c</c>.
    /// </summary>
    [Fact]
    public void TheOracleRefusesARecoveryWhichLearnsItsOwnEnd()
    {
        var state = new LedgerState { Next = 3, Completed = 3, Values = [1, 2, 3] };
        var learnsItsEnd = new LedgerOutcome(LedgerOutcomeKind.End, Value: 3, Recording: new(KnownBefore: 0, KnownAfter: 3, C: 2));

        Assert.False(Allows(QuorumSpec(), LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true), learnsItsEnd, state));
        Assert.True(Allows(QuorumSpec(), LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true), learnsItsEnd with { Recording = new(KnownBefore: 0, KnownAfter: 2, C: 2) }, state));
        Assert.True(Allows(LedgerStorageSpec.Create(), LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true), learnsItsEnd, state));
    }

    /// <summary>
    /// The contract's spec judges answers alone (ledger-protocol.md §2.7): it allows a call with no recording, and
    /// refuses a read below the bound which answers <c>Missing</c> (LL-I6), a check whose tail passes every candidate end
    /// or which reports sealed before a recovery completed (LL-I5, LL-I7), a recovery whose end lies below a reported tail
    /// (LL-I7) or past the written batches (LL-I3), and a read past <c>T</c> once the reclamation completed (LL-I9).
    /// </summary>
    [Fact]
    public void TheContractSpecJudgesAnswersAlone()
    {
        var spec = LedgerStorageSpec.Create();
        static LedgerState Open(long completed = 2) => new() { Next = 2, Completed = completed, Values = [1, 2], Reported = 2 };
        static LedgerState Reclaimed() => new() { Next = 2, Completed = 2, Values = [1, 2], Sealed = true, End = 2, Recorded = true, Reclaiming = true, Reclaimed = true };

        Assert.True(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(1), new LedgerOutcome(LedgerOutcomeKind.Batch, Bytes: LedgerStorageSpec.Payload(2)), Open()));
        Assert.False(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(1), new LedgerOutcome(LedgerOutcomeKind.Missing), Open()));
        Assert.True(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(2), new LedgerOutcome(LedgerOutcomeKind.Missing), Open()));
        Assert.True(Allows(spec, LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true), new LedgerOutcome(LedgerOutcomeKind.Tail, Value: 1), Open()));
        Assert.False(Allows(spec, LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true), new LedgerOutcome(LedgerOutcomeKind.Tail, Value: 3), Open()));
        Assert.False(Allows(spec, LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true), new LedgerOutcome(LedgerOutcomeKind.Tail, Value: 1, IsSealed: true), Open()));
        Assert.True(Allows(spec, LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true), new LedgerOutcome(LedgerOutcomeKind.End, Value: 2), Open()));
        Assert.False(Allows(spec, LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true), new LedgerOutcome(LedgerOutcomeKind.End, Value: 1), Open(completed: 1)));
        Assert.False(Allows(spec, LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true), new LedgerOutcome(LedgerOutcomeKind.End, Value: 3), Open()));
        Assert.True(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(2), new LedgerOutcome(LedgerOutcomeKind.Dropped), Reclaimed()));
        Assert.False(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(2), new LedgerOutcome(LedgerOutcomeKind.Missing), Reclaimed()));
    }

    /// <summary>
    /// A ledger's life through its reclamation (LL-I9): after the close, the drop on one node and then on every node, the
    /// primary's reads, ranged reads, scans, checks, recoveries, and appends answer <c>Dropped</c>, a batch below
    /// <c>T</c>, a seal, or, before the reclamation completes, what they answered before; an append whose outcome is
    /// unknown and never landed leaves no batch, so the recovery ends below it (LL-I3, LL-I8).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ALedgerConformsThroughItsReclamation()
    {
        ConformanceCall Call(string operation, object request) => new(operation, request);
        ConformanceCall[] calls =
        [
            Call(LedgerStorageSpec.Append, new LedgerAppend(1)),
            Call(LedgerStorageSpec.Append, new LedgerAppend(2)),
            Call(LedgerStorageSpec.AppendUnanswered, new LedgerAppendUnanswered(3, Landed: false)),
            Call(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true)),
            Call(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)),
            Call(LedgerStorageSpec.Observe, LedgerObserve.Instance),
            Call(LedgerStorageSpec.Drop, new LedgerDrop(Every: false)),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(1)),
            Call(LedgerStorageSpec.ReadRange, new LedgerRangeRead(0, 1, 4)),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(2)),
            Call(LedgerStorageSpec.Scan, new LedgerScan(0)),
            Call(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)),
            Call(LedgerStorageSpec.Drop, new LedgerDrop(Every: true)),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(0)),
            Call(LedgerStorageSpec.Scan, new LedgerScan(0)),
            Call(LedgerStorageSpec.Check, new LedgerCheck()),
            Call(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true)),
            Call(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: false)),
            Call(LedgerStorageSpec.Append, new LedgerAppend(4)),
        ];
        var kit = Kit(TestContext.Current.CancellationToken);
        foreach (var seed in Enumerable.Range(0, Replicas))
        {
            await kit.RunAsync(new ConformanceCase([.. calls.Select(static call => new ConformanceRound([call]))], Seed: seed));
        }
    }

    /// <summary>
    /// A sealed append sent its write to every replica, so its batch may have landed on an unsealed minority replica
    /// whose seal from an earlier recovery had not yet landed (ledger-protocol.md §6.2, §8.3, #439). The quorum's spec
    /// and oracle allow reads at that slot to observe either that exact batch or <c>Missing</c>, refuse any other bytes
    /// or <c>Dropped</c> before reclamation, allow a later recovery to find the batch and extend its end to cover it, and
    /// after reclamation completes allow only <c>Dropped</c> (LL-I1, LL-I3, LL-I9).
    /// </summary>
    [Fact]
    public void ASealedAppendMayLeaveAMinorityBatch()
    {
        var spec = QuorumSpec();
        var afterSealedAppend = new LedgerState
        {
            Next = 2,
            Completed = 1,
            Values = [8, 5],
            Partial = [1],
            Sealed = true,
            Stopped = true,
            End = 1,
        };
        var ok = new LedgerRecording(MaxCarried: 1);

        Assert.True(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(1), new LedgerOutcome(LedgerOutcomeKind.Batch, Bytes: LedgerStorageSpec.Payload(5), Recording: ok), afterSealedAppend));
        Assert.True(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(1), new LedgerOutcome(LedgerOutcomeKind.Missing, Recording: ok), afterSealedAppend));
        Assert.False(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(1), new LedgerOutcome(LedgerOutcomeKind.Batch, Bytes: LedgerStorageSpec.Payload(8), Recording: ok), afterSealedAppend));
        Assert.False(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(1), new LedgerOutcome(LedgerOutcomeKind.Dropped, Recording: ok), afterSealedAppend));

        var slice = LedgerStorageSpec.Payload(5)[1..5];
        Assert.True(Allows(spec, LedgerStorageSpec.ReadRange, new LedgerRangeRead(1, 1, 4), new LedgerOutcome(LedgerOutcomeKind.Batch, Bytes: slice, Recording: ok), afterSealedAppend));
        Assert.True(Allows(spec, LedgerStorageSpec.ReadRange, new LedgerRangeRead(1, 1, 4), new LedgerOutcome(LedgerOutcomeKind.Missing, Recording: ok), afterSealedAppend));
        Assert.True(Allows(spec, LedgerStorageSpec.ReadRange, new LedgerRangeRead(1, 0, LedgerStorageSpec.Payload(5).Length + 1), new LedgerOutcome(LedgerOutcomeKind.RangeRefused, Recording: ok), afterSealedAppend));

        var recoverOk = new LedgerRecording(MaxCarried: 1, KnownBefore: 0, KnownAfter: 1, C: 1);
        Assert.True(Allows(spec, LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true), new LedgerOutcome(LedgerOutcomeKind.End, Value: 1, LifetimeStopped: true, Recording: recoverOk), afterSealedAppend));
        Assert.True(Allows(spec, LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true), new LedgerOutcome(LedgerOutcomeKind.End, Value: 2, LifetimeStopped: true, Recording: recoverOk), afterSealedAppend));
        Assert.False(Allows(spec, LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true), new LedgerOutcome(LedgerOutcomeKind.End, Value: 3, LifetimeStopped: true, Recording: recoverOk), afterSealedAppend));

        var reclaimed = new LedgerState
        {
            Next = 2,
            Completed = 1,
            Values = [8, 5],
            Partial = [1],
            Sealed = true,
            Stopped = true,
            End = 1,
            Recorded = true,
            Reclaiming = true,
            Reclaimed = true,
        };
        Assert.True(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(1), new LedgerOutcome(LedgerOutcomeKind.Dropped, Recording: ok), reclaimed));
        Assert.False(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(1), new LedgerOutcome(LedgerOutcomeKind.Batch, Bytes: LedgerStorageSpec.Payload(5), Recording: ok), reclaimed));
        Assert.False(Allows(spec, LedgerStorageSpec.Read, new LedgerSlotRead(1), new LedgerOutcome(LedgerOutcomeKind.Missing, Recording: ok), reclaimed));
    }

    /// <summary>
    /// Reproduces #439 deterministically: <c>RecoverElsewhere</c> seals <c>N − Q + 1</c> replicas and stops before the
    /// third replica's seal lands; the writer's <c>Append(5)</c> at slot 1 meets the seal on the two sealed replicas
    /// (<c>Sealed(1)</c>) while its write lands on the unsealed minority replica; consecutive reads of slot 1 then
    /// observe <c>Batch(Payload(5))</c> when the minority answers first and <c>Missing</c> when the sealed majority
    /// answers first, and a subsequent <c>RecoverElsewhere</c> finds the minority batch and returns <c>End(2)</c>
    /// (ledger-protocol.md §6.2, §8.3).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReadAtOrAboveEndAfterASealedAppendMayObserveAMinorityBatch()
    {
        MinoritySealedAppendNodes? observed = null;
        var kit = Kit(
            TestContext.Current.CancellationToken,
            nodes: () =>
            {
                var created = new MinoritySealedAppendNodes(LedgerSystem.NodeIds(Replicas));
                observed = created;
                return Task.FromResult<ILedgerSystemNodes>(created);
            });

        await kit.RunAsync(new ConformanceCase(
            [
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(8))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(5))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(1))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(1))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true))]),
            ],
            Seed: 0));

        Assert.NotNull(observed);
        Assert.True(observed.MinorityWriteLanded, "The sealed append's write never landed on the unsealed minority replica.");
        Assert.True(observed.FirstReadAnsweredByMinority, "The first Read(1) was not answered by the unsealed minority replica.");
        Assert.True(observed.SecondReadAnsweredByMajority, "The second Read(1) was not answered by the sealed majority.");
    }

    /// <summary>
    /// The writer's background explicit contiguous-tail write runs outside any harness call's execution context and must
    /// not be attributed to an active call's recording (#443). Advancing the writer clock during a primary <c>Learn</c>
    /// fires the background <c>WriteContiguousTail</c> to every replica, which interval-based attribution mistakes for
    /// <c>Learn</c> asking other nodes (or for <c>Acquire(Cancelled)</c> sending requests).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABackgroundContiguousTailWriteIsNotAttributedToAnActiveCall()
    {
        var writerClock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var backgroundCalls = 0;
        var inBackground = 0;
        var triggered = 0;
        var kit = Kit(
            TestContext.Current.CancellationToken,
            writerClock: writerClock,
            wrap: inner => new TriggeringContiguousTailReplica(
                inner,
                onReadTail: () =>
                {
                    if (Volatile.Read(ref inBackground) != 0)
                    {
                        Interlocked.Increment(ref backgroundCalls);
                        return;
                    }

                    if (Interlocked.Exchange(ref triggered, 1) == 0)
                    {
                        Volatile.Write(ref inBackground, 1);
                        try
                        {
                            using var done = new ManualResetEventSlim();
                            ThreadPool.UnsafeQueueUserWorkItem(
                                _ =>
                                {
                                    try
                                    {
                                        writerClock.Advance(LedgerWriter.ContiguousTailInterval);
                                    }
                                    finally
                                    {
                                        done.Set();
                                    }
                                },
                                state: null);
                            done.Wait(TestContext.Current.CancellationToken);
                        }
                        finally
                        {
                            Volatile.Write(ref inBackground, 0);
                        }
                    }
                }));

        await kit.RunAsync(new ConformanceCase(
            [
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(8))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Learn, new LedgerLearn(Elsewhere: false))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Acquire, new LedgerAcquire(3, AcquireUse.Cancelled))]),
            ],
            Seed: 0));

        Assert.Equal(1, Volatile.Read(ref triggered));
        Assert.Equal(Replicas, Volatile.Read(ref backgroundCalls));
    }

    /// <summary>
    /// Reproduces #444 deterministically: when the leading replica is also the primary's preferred replica
    /// (<c>Seed: 0</c>) and its write for slot 1 is still a straggler when <c>Read(1)</c> begins, it answers
    /// <c>Missing</c>; if its straggler write then completes mid-read and records slot 1 in <see cref="LeaderSlots"/>
    /// before the remaining replicas are asked, <see cref="SkewedLedgerReplica"/> must still keep those replicas honest
    /// for that read rather than failing both with <c>TimeoutException</c>.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADecidedReadRemainsHonestWhenTheLeaderStragglerCompletesMidRead()
    {
        LeaderStragglerWriteNodes? observed = null;
        var kit = Kit(
            TestContext.Current.CancellationToken,
            nodes: () =>
            {
                var created = new LeaderStragglerWriteNodes(LedgerSystem.NodeIds(Replicas));
                observed = created;
                return Task.FromResult<ILedgerSystemNodes>(created);
            });

        await kit.RunAsync(new ConformanceCase(
            [
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(8))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(4))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(3))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(1))]),
            ],
            Seed: 0));

        Assert.NotNull(observed);
        Assert.True(observed.LeaderWriteCompletedMidRead, "The leader's straggler write did not complete mid-read.");
    }

    /// <summary>
    /// The quorum over LedgerStore replicas, one store per node, conforms: random sequences of every operation, drops
    /// included, through each store's replicas, seals, and tombstones (ledgerstore.md §2.2, LL-I1 to LL-I9).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ALedgerStoreQuorumConforms()
    {
        var kit = Kit(TestContext.Current.CancellationToken, nodes: static async () => await LedgerStoreSystemNodes.CreateAsync(LedgerSystem.NodeIds(Replicas)));
        await kit.SampleAsync(kit.Sequences(20), iterations: 40);
    }

    /// <summary>
    /// A recovery takes its <c>c</c> from the first <c>N − Q + 1</c> seal answers it handles, which may come in another
    /// order than the one the recording shows: the recording stamps an answer before the layers above it deliver it,
    /// <c>Task.WhenAny</c> picks among answers already complete in its set's order, and a transport may reorder them too
    /// (LP-I6, LP-I11, #237). The leading replica alone heard the read of slot 0 carry tail 1. Its seal answer is recorded
    /// last, after replica 2's, but reaches the recovery second, while replica 2's is held until the recovery handled it,
    /// so <c>c</c> is its tail, 1, which is also the recovery's end: every slot below it holds a decided batch. The fold
    /// judges the append, the read, and the recovery.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARecoveryTakesItsCFromTheSealAnswersItReceivesFirst()
    {
        const int Leader = 0;
        var order = new SealOrder(Replicas);
        order.HoldAnswer = replica => replica == Leader ? Task.WhenAll(order.Answered(2), order.Handled(1)) : Task.CompletedTask;
        order.HoldDelivery = replica => replica == 2 ? order.Handled(Leader) : Task.CompletedTask;
        var kit = Kit(TestContext.Current.CancellationToken, sealOrder: order);

        await kit.RunAsync(new ConformanceCase(
            [
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(1))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(0))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true))]),
            ],
            Seed: Leader));

        // The recording shows the leader's answer, with the greatest tail, last; the recovery handled it second, and
        // replica 2's third, unless the recovery ended before it arrived.
        Assert.Equal([(1, 0L), (2, 0L), (Leader, 1L)], order.RecordedAnswers);
        int[] handled = [1, Leader, 2];
        Assert.InRange(order.HandledAnswers.Count, Replicas - Quorum + 1, Replicas);
        Assert.Equal(handled[..order.HandledAnswers.Count], order.HandledAnswers);
    }

    /// <summary>
    /// The spec refuses a writer's recovery whose first seal request preceded the lifetime's stop (LP-I14), and a write
    /// which carried a tail above the completed prefix (LP-I11).
    /// </summary>
    [Fact]
    public void TheSpecRefusesASealBeforeTheStopAndATailAboveTheCompletedPrefix()
    {
        var state = new LedgerState { Next = 1, Completed = 1, Values = [1] };
        var sealedFirst = new LedgerOutcome(LedgerOutcomeKind.End, Value: 1, LifetimeStopped: true, Recording: new(MaxCarried: 1, KnownBefore: 1, KnownAfter: 1, C: 1, StoppedFirst: false));
        var eager = new LedgerOutcome(LedgerOutcomeKind.Appended, Value: 1, Recording: new(MaxCarried: 2));

        Assert.False(Allows(QuorumSpec(), LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: false), sealedFirst, state));
        Assert.True(Allows(QuorumSpec(), LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: false), sealedFirst with { Recording = sealedFirst.Recording! with { StoppedFirst = true } }, state));
        Assert.False(Allows(QuorumSpec(), LedgerStorageSpec.Append, new LedgerAppend(2), eager, state));
        Assert.True(Allows(QuorumSpec(), LedgerStorageSpec.Append, new LedgerAppend(2), eager with { Recording = new(MaxCarried: 1) }, state));
    }

    /// <summary>
    /// The harness fails a <see cref="Ledger"/> whose recovery raises its known contiguous tail to its end (LP-I11), and
    /// shrinks the case to an append and a recovery.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheHarnessCatchesALedgerWhichRaisesItsTailToTheEnd() =>
        CatchesAsync(LedgerMutation.RaiseTailToEnd, maxRounds: 2, @"^Recover(Elsewhere)? → End\(", TestContext.Current.CancellationToken);

    /// <summary>
    /// The harness fails a <see cref="Ledger"/> whose recovery in the writer's process stops the writer's lifetime only
    /// after it sealed (LP-I14).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheHarnessCatchesALedgerWhichStopsItsWriterAfterTheSeal() =>
        CatchesAsync(LedgerMutation.StopAfterSeal, maxRounds: 2, @"^Recover → End\(", TestContext.Current.CancellationToken);

    /// <summary>
    /// The harness fails a <see cref="Ledger"/> whose learn raises its known contiguous tail one past the tail its replica
    /// answered (LP-I11).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheHarnessCatchesALedgerWhichLearnsPastTheTail() =>
        CatchesAsync(LedgerMutation.LearnPastTail, maxRounds: 2, @"^Learn(Elsewhere)? → Learned\(", TestContext.Current.CancellationToken);

    /// <summary>
    /// Reproduces #447 deterministically: running <see cref="LedgerMutation.LearnPastTail"/> after
    /// <see cref="LedgerStorageSpec.Observe"/> has recorded <c>T</c> on the primary's <see cref="Ledger"/> is rejected by
    /// the conformance spec without tripping <c>Ledger.AssertAtMostRecordedTail</c> in Debug builds.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheHarnessCatchesALedgerWhichLearnsPastTheTailAfterObservingRecordedTail()
    {
        var kit = Kit(TestContext.Current.CancellationToken, LedgerMutation.LearnPastTail);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => kit.RunAsync(new ConformanceCase(
            [
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: false))]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Observe, LedgerObserve.Instance)]),
                new ConformanceRound([new ConformanceCall(LedgerStorageSpec.Learn, new LedgerLearn(Elsewhere: false))]),
            ],
            Seed: 0)));

        var timeout = TimeSpan.FromSeconds(5);
        Assert.Matches(new Regex(@"The spec does not allow round 3 of 3, seed 0:.*?^> 3\. Learn → Learned\(", RegexOptions.Singleline | RegexOptions.Multiline, timeout), failure.Message);
    }

    /// <summary>
    /// The harness fails a writer which appends at a slot it already appended at (ledger-protocol.md §2.3), and shrinks the
    /// case to the acquisition which appends twice.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheHarnessCatchesAWriterWhichAcceptsASlotTwice() =>
        CatchesAsync(LedgerMutation.AcceptSlotTwice, maxRounds: 1, @"^Acquire\(\d+, AppendTwice\) → AppendedTwice", TestContext.Current.CancellationToken);

    /// <summary>
    /// The harness fails a read of the primary's process, whole or ranged, judged as carrying a tail above its own
    /// (LP-I11): each read is judged by the requests which flow from it, so the read check is never vacuous.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task TheHarnessCatchesALedgerWhoseReadsCarryATailAboveItsOwn() =>
        CatchesAsync(LedgerMutation.ReadsCarryAboveTail, maxRounds: 2, @"^Read(Range)?\(", TestContext.Current.CancellationToken);

    private static Gen<byte> Values => LedgerConformanceOperations.Values;

    private static Gen<long> Slots => LedgerConformanceOperations.Slots;

    private static Gen<ConformanceCall> Calls => LedgerConformanceOperations.Calls;

    /// <summary>Returns the quorum's spec: the contract's, with the quorum's recording oracle.</summary>
    private static Microsoft.Accordant.Spec<LedgerState> QuorumSpec() => LedgerStorageSpec.Create(LedgerQuorumOracle.Judge);

    /// <summary>Returns whether <paramref name="spec"/> allows <paramref name="outcome"/> of a call in <paramref name="state"/>.</summary>
    private static bool Allows<TRequest>(Microsoft.Accordant.Spec<LedgerState> spec, string operation, TRequest request, LedgerOutcome outcome, LedgerState state)
        where TRequest : notnull =>
        spec.Allows(spec.GetOperation<TRequest, LedgerOutcome>(operation), request, outcome, new StateProfile(state)).Item1;

    private ConformanceKit<LedgerState, LedgerSystem> Kit(
        CancellationToken cancellationToken,
        LedgerMutation mutation = LedgerMutation.None,
        LedgerCoverage? coverage = null,
        SealOrder? sealOrder = null,
        Func<Task<ILedgerSystemNodes>>? nodes = null,
        Func<ILedgerReplica, ILedgerReplica>? wrap = null,
        TimeProvider? writerClock = null) =>
        new ConformanceKit<LedgerState, LedgerSystem>(
                QuorumSpec(),
                static () => new LedgerState(),
                async seed => new LedgerSystem(
                    Replicas,
                    Quorum,
                    Window,
                    mutation,
                    coverage,
                    leader: (int)((uint)seed % Replicas),
                    sealOrder,
                    wrap: wrap,
                    nodes: nodes is null ? null : await nodes(),
                    writerClock: writerClock),
                LedgerStorageSpec.IsCallerAllowed,
                cancellationToken,
                _leaks.InScopeAsync)
            .WithLedgerOperations();
    /// <summary>
    /// Runs random sequences against a harness-built defect and checks the report: its shrunk case fails at its last round,
    /// has at most <paramref name="maxRounds"/> rounds, and fails at a call matching <paramref name="failingCall"/>.
    /// </summary>
    private async Task CatchesAsync(LedgerMutation mutation, int maxRounds, string failingCall, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(5);
        var kit = Kit(cancellationToken, mutation);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => kit.SampleAsync(kit.Sequences(30), iterations: 200));

        var report = Regex.Match(
            failure.Message,
            @"The spec does not allow round (?<round>\d+) of (?<rounds>\d+), seed \d+:.*?^> \k<round>\. (?<call>[^\r\n]*)",
            RegexOptions.Singleline | RegexOptions.Multiline,
            timeout);
        Assert.True(report.Success, failure.Message);
        Assert.Equal(report.Groups["rounds"].Value, report.Groups["round"].Value);
        Assert.InRange(int.Parse(report.Groups["rounds"].Value, CultureInfo.InvariantCulture), 1, maxRounds);
        Assert.Matches(new Regex(failingCall, RegexOptions.None, timeout), report.Groups["call"].Value);
    }
}


/// <summary>
/// Orders a recovery's seal answers: when each replica answers, which the recording shows, and when the recovery receives
/// each answer, which the layers above the recording may delay past later answers. Each signal fires once per replica, so
/// a <see cref="SealOrder"/> orders only its system's first recovery.
/// </summary>
/// <param name="replicas">The number of replicas.</param>
internal sealed class SealOrder(int replicas) : ISealOrder
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource[] _answered = Signals(replicas);
    private readonly TaskCompletionSource[] _handled = Signals(replicas);
    private readonly Lock _lock = new();
    private readonly List<(int Replica, long Tail)> _recorded = [];
    private readonly List<int> _handledOrder = [];

    /// <summary>Gets or sets what a replica's seal answer waits for once its seal landed, before the recording sees it.</summary>
    internal Func<int, Task> HoldAnswer { get; set; } = static _ => Task.CompletedTask;

    /// <summary>Gets or sets what a replica's recorded seal answer waits for before the recovery receives it.</summary>
    internal Func<int, Task> HoldDelivery { get; set; } = static _ => Task.CompletedTask;

    /// <summary>Gets each seal answer's replica and tail, in the order the recording holds them.</summary>
    internal List<(int Replica, long Tail)> RecordedAnswers
    {
        get
        {
            lock (_lock)
            {
                return [.. _recorded];
            }
        }
    }

    /// <summary>Gets the replicas whose seal answers the recovery handled, in the order it handled them.</summary>
    internal List<int> HandledAnswers
    {
        get
        {
            lock (_lock)
            {
                return [.. _handledOrder];
            }
        }
    }

    /// <summary>Completes once the recording holds the replica's seal answer, or fails after 10 s.</summary>
    internal Task Answered(int replica) =>
        BoundedAsync(_answered[replica].Task, string.Create(CultureInfo.InvariantCulture, $"The recording never held replica {replica}'s seal answer."));

    /// <summary>Completes once the recovery handled the replica's seal answer, or fails after 10 s.</summary>
    internal Task Handled(int replica) =>
        BoundedAsync(_handled[replica].Task, string.Create(CultureInfo.InvariantCulture, $"The recovery never handled replica {replica}'s seal answer."));

    /// <summary>Observes a step the recovery handled.</summary>
    public void OnStep(RecoveryStep step)
    {
        if (step.Kind == RecoveryStepKind.Seal)
        {
            lock (_lock)
            {
                _handledOrder.Add(step.Replica);
            }

            _ = _handled[step.Replica].TrySetResult();
        }
    }

    /// <summary>Wraps the replica below the recording, so its seal answers wait for <see cref="HoldAnswer"/>.</summary>
    public ILedgerReplica Answering(ILedgerReplica inner, int replica) => new HeldSeal(inner, () => HoldAnswer(replica), answered: null);

    /// <summary>
    /// Wraps the replica above the recording, so its recorded seal answers are noted and wait for <see cref="HoldDelivery"/>.
    /// </summary>
    public ILedgerReplica Delivering(ILedgerReplica inner, int replica) =>
        new HeldSeal(
            inner,
            () => HoldDelivery(replica),
            answer =>
            {
                lock (_lock)
                {
                    _recorded.Add((replica, answer.TryGetValue(out LedgerReplicaTail tail) ? tail.ContiguousTail.Value : -1));
                }

                _ = _answered[replica].TrySetResult();
            });

    private static async Task BoundedAsync(Task signal, string never)
    {
        try
        {
            await signal.WaitAsync(Bound).ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException(never, timeout);
        }
    }

    private static TaskCompletionSource[] Signals(int count) =>
        [.. Enumerable.Range(0, count).Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];

    private sealed class HeldSeal(ILedgerReplica inner, Func<Task> hold, Action<ReplicaTailRead>? answered) : ILedgerReplica
    {
        public ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
        {
            var answer = await inner.ReadTailAsync(contiguousTail, seal, cancellationToken).ConfigureAwait(false);
            if (seal)
            {
                answered?.Invoke(answer);
                await hold().WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return answer;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>
/// Deterministic node harness for #439: holds one minority replica's first seal until cancelled so it stays unsealed,
/// lets the sealed append at slot 1 land on that minority replica before the sealed majority answers <c>Sealed</c>,
/// forces the first <c>Read(1)</c> to be answered by the minority (<c>Batch</c>) and the second by the sealed majority
/// (<c>Missing</c>), and lets the subsequent recovery seal and read normally.
/// </summary>
internal sealed class MinoritySealedAppendNodes(NodeId[] nodes) : ILedgerSystemNodes
{
    private static readonly Task Never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    private readonly InMemoryLedgerSystemNodes _inner = new();
    private readonly NodeId _minority = nodes[0];
    private readonly TaskCompletionSource _minorityWriteLanded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _majorityEnteredFirstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _minorityEnteredSecondRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _firstSealHeld;
    private int _firstReadMajorityEntered;
    private int _secondReadMajorityAnswered;
    private int _phase;

    internal bool MinorityWriteLanded => _minorityWriteLanded.Task.IsCompletedSuccessfully;

    internal bool FirstReadAnsweredByMinority { get; private set; }

    internal bool SecondReadAnsweredByMajority => Volatile.Read(ref _secondReadMajorityAnswered) >= 2;

    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
        new ControlledReplica(_inner.Open(node, ledger, options), this, isMinority: node == _minority);

    public ValueTask DropAsync(NodeId node, LedgerId ledger) => _inner.DropAsync(node, ledger);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class ControlledReplica(ILedgerReplica inner, MinoritySealedAppendNodes owner, bool isMinority) : ILedgerReplica
    {
        public async ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            if (slot.Value == 1 && !recovery)
            {
                if (isMinority)
                {
                    var outcome = await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken).ConfigureAwait(false);
                    _ = owner._minorityWriteLanded.TrySetResult();
                    return outcome;
                }

                await owner._minorityWriteLanded.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            if (slot.Value == 1 && owner.MinorityWriteLanded)
            {
                var phase = Volatile.Read(ref owner._phase);
                if (phase == 0)
                {
                    if (!isMinority)
                    {
                        if (Interlocked.Increment(ref owner._firstReadMajorityEntered) == 2)
                        {
                            _ = owner._majorityEnteredFirstRead.TrySetResult();
                        }

                        await Never.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await owner._majorityEnteredFirstRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var answer = await inner.ReadAsync(slot, contiguousTail, cancellationToken).ConfigureAwait(false);
                    owner.FirstReadAnsweredByMinority = true;
                    Volatile.Write(ref owner._phase, 1);
                    return answer;
                }

                if (phase == 1)
                {
                    if (isMinority)
                    {
                        _ = owner._minorityEnteredSecondRead.TrySetResult();
                        await Never.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await owner._minorityEnteredSecondRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var answer = await inner.ReadAsync(slot, contiguousTail, cancellationToken).ConfigureAwait(false);
                    if (Interlocked.Increment(ref owner._secondReadMajorityAnswered) == 2)
                    {
                        Volatile.Write(ref owner._phase, 2);
                    }

                    return answer;
                }
            }

            return await inner.ReadAsync(slot, contiguousTail, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
        {
            if (isMinority && seal && Interlocked.Exchange(ref owner._firstSealHeld, 1) == 0)
            {
                await Never.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.ReadTailAsync(contiguousTail, seal, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>
/// Wraps a replica and invokes a callback on each unsealed <c>ReadTailAsync</c> call so a regression test can
/// deterministically fire the writer's background explicit contiguous-tail write during an active harness call.
/// </summary>
internal sealed class TriggeringContiguousTailReplica(ILedgerReplica inner, Action onReadTail) : ILedgerReplica
{
    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(slot, contiguousTail, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

    public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
    {
        if (!seal)
        {
            onReadTail();
        }

        return inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>
/// Deterministic node harness for #444: holds the leading replica's (node 0's) write for slot 1 while the other two
/// replicas form a quorum, so <c>Read(1)</c> asks the leader first and receives <c>Missing</c>; before returning that
/// <c>Missing</c>, releases the held write synchronously so <see cref="SkewedLedgerReplica.WriteAsync"/> records slot 1
/// in <see cref="LeaderSlots"/> before <see cref="LedgerQuorum"/> asks the remaining two replicas.
/// </summary>
internal sealed class LeaderStragglerWriteNodes(NodeId[] nodes) : ILedgerSystemNodes
{
    private readonly InMemoryLedgerSystemNodes _inner = new();
    private readonly NodeId _leader = nodes[0];
    private readonly TaskCompletionSource _releaseLeaderWrite = new();
    private int _leaderWriteCompleted;

    internal bool LeaderWriteCompletedMidRead => Volatile.Read(ref _leaderWriteCompleted) != 0;

    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
        new ControlledReplica(_inner.Open(node, ledger, options), this, isLeader: node == _leader);

    public ValueTask DropAsync(NodeId node, LedgerId ledger) => _inner.DropAsync(node, ledger);

    public ValueTask DisposeAsync()
    {
        _ = _releaseLeaderWrite.TrySetCanceled();
        return _inner.DisposeAsync();
    }

    private sealed class ControlledReplica(ILedgerReplica inner, LeaderStragglerWriteNodes owner, bool isLeader) : ILedgerReplica
    {
        public async ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            if (isLeader && slot.Value == 1 && !recovery)
            {
                await owner._releaseLeaderWrite.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                var outcome = await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref owner._leaderWriteCompleted, 1);
                return outcome;
            }

            return await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            var answer = await inner.ReadAsync(slot, contiguousTail, cancellationToken).ConfigureAwait(false);
            if (isLeader && slot.Value == 1 && answer.Case == ReplicaReadCase.Missing)
            {
                _ = owner._releaseLeaderWrite.TrySetResult();
            }

            return answer;
        }

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

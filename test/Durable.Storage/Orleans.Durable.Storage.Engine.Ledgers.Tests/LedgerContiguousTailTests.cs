using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the quorum's contiguous-tail mechanisms (ledger-protocol.md §8.2, §8.3): a read below the known contiguous tail
/// repairs a preferred replica which answered missing or corrupt with a recovery write of the batch it found, carrying the
/// tail (LP-I7, LP-I10); a byte-range read forwards its range to each replica it asks and repairs nothing; an explicit
/// contiguous-tail write sends the tail to every replica without sealing (LR-I6, LP-I11); and a learn reads the preferred
/// replica's retained tail without sealing (LP-I11). Each replica is a double which
/// records its calls, and a repair or an explicit write, which runs past its operation, ends with the quorum.
/// </summary>
public sealed class LedgerContiguousTailTests : IDisposable
{
    private const int Replicas = 3;
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 8 };
    private static readonly Slot Below = new(3);
    private static readonly Slot Tail = new(5);
    private static readonly TimeSpan Delay = LedgerQuorum.ReadHedgeDelay;
    private static readonly TimeSpan Signal = TimeSpan.FromSeconds(3);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A preferred replica which answers a decided slot missing, as one which missed the write does, or corrupt receives a
    /// recovery write of the batch the read found at the next replica, carrying the read's tail: read repair (LP-I10,
    /// LR-I5). No other replica receives a write.
    /// </summary>
    [Theory(Timeout = 10_000)]
    [InlineData('M')]
    [InlineData('C')]
    public async Task APreferredReplicaWhichLacksADecidedBatchIsRepairedWithTheBatchTheReadFound(char answer)
    {
        var replicas = Holding();
        replicas[0].ReadAsyncHandler = (slot, _) => Answer(answer, slot);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0, verifyBatch: AcceptAll);

        using (var batch = (await quorum.ReadAsync(Below, Tail, TestContext.Current.CancellationToken)).Batch)
        {
            Assert.Equal("held"u8.ToArray(), batch.ToArray());
        }

        var repair = Assert.Single(replicas[0].WriteCalls);
        Assert.Equal((Below, Tail, true), (repair.Address, repair.ContiguousTail, repair.Recovery));
        Assert.Equal("held"u8.ToArray(), repair.Payload);
        Assert.Empty(replicas[1].WriteCalls);
        Assert.Empty(replicas[2].WriteCalls);
    }

    /// <summary>
    /// A preferred replica which answered backpressure or failed may hold the batch, so the read which passes it repairs
    /// nothing; neither does a read whose preferred replica holds the batch.
    /// </summary>
    [Theory(Timeout = 10_000)]
    [InlineData('B')]
    [InlineData('t')]
    [InlineData('F')]
    [InlineData('H')]
    public async Task AReadWhosePreferredReplicaDidNotReportTheSlotMissingOrCorruptRepairsNothing(char answer)
    {
        var replicas = Holding();
        if (answer != 'H')
        {
            replicas[0].ReadAsyncHandler = (slot, _) => Answer(answer, slot);
        }

        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0, verifyBatch: AcceptAll);

        (await quorum.ReadAsync(Below, Tail, TestContext.Current.CancellationToken)).Release();

        Assert.All(replicas, static replica => Assert.Empty(replica.WriteCalls));
    }

    /// <summary>
    /// Only a preferred replica is repaired: a read without one, which starts its rotation at a replica that answers
    /// missing, and a read at the tail, which decides nothing, write to no replica.
    /// </summary>
    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadWithoutAPreferredReplicaOrAtTheTailRepairsNothing(bool atTail)
    {
        var replicas = Holding();
        replicas[0].ReadAsyncHandler = static (slot, _) => Answer('M', slot);
        await using var quorum = atTail
            ? LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0, verifyBatch: AcceptAll)
            : new LedgerQuorum(replicas, 2, Window, TimeProvider.System, preferredReplica: null, readRotation: Replicas - 1, verifyBatch: AcceptAll);

        (await quorum.ReadAsync(atTail ? Tail : Below, Tail, TestContext.Current.CancellationToken)).Release();

        _ = Assert.Single(replicas[0].ReadCalls);
        Assert.All(replicas, static replica => Assert.Empty(replica.WriteCalls));
    }

    /// <summary>
    /// A read whose preferred replica outlasts the hedge delay and then answers missing while the hedge is pending repairs
    /// it once the next replica's batch decides the read.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AHedgedReadRepairsAPreferredReplicaWhichAnsweredMissingWhileTheHedgeWasPending()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new TimerSignallingTimeProvider();
        var (replicas, answers, asked) = Held();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, time, preferredReplica: 0, verifyBatch: AcceptAll);
        using var unblock = Unblock(answers);
        using var found = new TransferredArcBuffer("found"u8);

        var reading = quorum.ReadAsync(Below, Tail, token).AsTask();
        await time.WaitForTimersAsync(1, token).WaitAsync(Signal, TimeProvider.System, token);
        time.Advance(Delay);
        await asked[1].Task.WaitAsync(Signal, TimeProvider.System, token);

        answers[0].SetResult(ReplicaAnswers.Missing);
        await asked[2].Task.WaitAsync(Signal, TimeProvider.System, token);
        Assert.Empty(replicas[0].WriteCalls);

        answers[2].SetResult(new ReplicaRead(found.Take()));
        (await reading.WaitAsync(Signal, TimeProvider.System, token)).Release();

        var repair = Assert.Single(replicas[0].WriteCalls);
        Assert.Equal((Below, Tail, true), (repair.Address, repair.ContiguousTail, repair.Recovery));
        Assert.Equal("found"u8.ToArray(), repair.Payload);
    }

    /// <summary>
    /// A repair which its replica holds outlives the read, which returns at once, under the quorum's lifetime: the quorum's
    /// disposal cancels it, waits for it, and the repair releases its view of the batch (ledger-protocol.md §6.3, BUF-I1).
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ARepairHeldAtItsReplicaEndsWithTheQuorumAndReleasesItsBatch()
    {
        var token = TestContext.Current.CancellationToken;
        var replicas = Holding();
        using var found = new TransferredArcBuffer("found"u8);
        replicas[0].ReadAsyncHandler = static (slot, _) => Answer('M', slot);
        replicas[1].ReadAsyncHandler = (_, _) => ValueTask.FromResult(new ReplicaRead(found.Take()));
        var held = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas[0].WriteAsyncHandler = async (_, _, _, cancellationToken) =>
        {
            held.SetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
            return ReplicaAnswers.Written;
        };
        var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0, verifyBatch: AcceptAll);

        (await quorum.ReadAsync(Below, Tail, token)).Release();
        var repairToken = await held.Task.WaitAsync(Signal, TimeProvider.System, token);
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(found.Pages, 2);

        await quorum.DisposeAsync().AsTask().WaitAsync(Signal, TimeProvider.System, token);

        Assert.True(repairToken.IsCancellationRequested);
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(found.Pages, 1);
    }

    /// <summary>
    /// A byte-range read forwards its range to each replica it asks, so only the range crosses the network, takes the range
    /// past a preferred replica's hole, and repairs nothing, since it holds only the range (ledger-protocol.md §8.3).
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AByteRangeReadForwardsItsRangeToEachReplicaItAsksAndRepairsNothing()
    {
        var replicas = Holding();
        replicas[0].ReadAsyncHandler = static (slot, _) => Answer('M', slot);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0, verifyBatch: AcceptAll);

        using (var range = (await quorum.ReadAsync(Below, 1, 2, Tail, TestContext.Current.CancellationToken)).Batch)
        {
            Assert.Equal("el"u8.ToArray(), range.ToArray());
        }

        var forwarded = new LedgerReplicaRangeReadCall(Below, 1, 2, Tail);
        Assert.Equal([forwarded], replicas[0].RangeReadCalls);
        Assert.Equal([forwarded], replicas[1].RangeReadCalls);
        Assert.Empty(replicas[2].RangeReadCalls);
        Assert.All(replicas, static replica => Assert.Empty(replica.WriteCalls));
    }

    /// <summary>
    /// A slot holds one batch, which every replica that holds it reports (LP-I1, LP-I10), so a replica's answer that the
    /// range extends past the batch, a value with the batch's length, answers the read: below the tail without asking
    /// another replica, and at it too.
    /// </summary>
    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AByteRangeReadPastTheBatchAnswersTheReplicasOutsideBatch(bool atTail)
    {
        var replicas = Holding();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0);

        var read = await quorum.ReadAsync(atTail ? Tail : Below, 2, 5, Tail, TestContext.Current.CancellationToken);

        Assert.True(read.TryGetValue(out ReplicaRead.OutsideBatch? outside));
        Assert.Equal(4, outside.Length);
        if (!atTail)
        {
            Assert.Equal([1, 0, 0], [.. replicas.Select(static replica => replica.RangeReadCalls.Count)]);
        }
    }

    /// <summary>
    /// An explicit contiguous-tail write sends the tail to every replica as a contiguous-tail read which does not seal,
    /// under the quorum's lifetime (LR-I6, ledger-protocol.md §8.2); once the quorum's reads stopped, or it was disposed,
    /// it sends nothing.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AnExplicitContiguousTailWriteSendsTheTailToEveryReplicaWithoutSealing()
    {
        var replicas = Holding();
        var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0);

        quorum.WriteContiguousTail(new Slot(7));

        Assert.All(replicas, static replica =>
        {
            var sent = Assert.Single(replica.ReadTailCalls);
            Assert.Equal((new Slot(7), false), (sent.ContiguousTail, sent.Seal));
            Assert.True(sent.CancellationToken.CanBeCanceled);
        });

        await quorum.StopReadsAsync();
        quorum.WriteContiguousTail(new Slot(8));
        await quorum.DisposeAsync().AsTask().WaitAsync(Signal, TimeProvider.System, TestContext.Current.CancellationToken);
        quorum.WriteContiguousTail(new Slot(9));

        Assert.All(replicas, static replica => Assert.Single(replica.ReadTailCalls));
    }

    /// <summary>
    /// An explicit contiguous-tail write which a replica holds ends with the quorum: its disposal cancels the call and waits
    /// for it (ledger-protocol.md §6.3).
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AnExplicitContiguousTailWriteHeldAtAReplicaEndsWithTheQuorum()
    {
        var token = TestContext.Current.CancellationToken;
        var replicas = Holding();
        var held = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas[1].ReadTailAsyncHandler = async (contiguousTail, seal, cancellationToken) =>
        {
            held.SetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
            return new LedgerReplicaTail(contiguousTail, seal);
        };
        var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0);

        quorum.WriteContiguousTail(new Slot(4));
        var sent = await held.Task.WaitAsync(Signal, TimeProvider.System, token);
        Assert.Equal(Replicas, quorum.TrackedStragglers);

        await quorum.DisposeAsync().AsTask().WaitAsync(Signal, TimeProvider.System, token);
        Assert.True(sent.IsCancellationRequested);
    }

    /// <summary>
    /// A <see cref="Ledger"/> writes its known contiguous tail explicitly only above every tail its writes and its earlier
    /// explicit writes carried, so an explicit write follows only a completion no write carried (ledger-protocol.md §8.2).
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ALedgerWritesItsTailExplicitlyOnlyAboveWhatItCarried()
    {
        var replicas = Holding();
        await using var ledger = LedgerTestData.Create(replicas, 2, Window);

        Assert.False(ledger.WriteContiguousTail());
        _ = ledger.RaiseKnownContiguousTail(new Slot(3));
        Assert.True(ledger.WriteContiguousTail());
        Assert.False(ledger.WriteContiguousTail());

        using (var payload = LedgerReplicaTestData.Buffer("next"u8))
        {
            _ = ledger.RaiseKnownContiguousTail(new Slot(4));
            Assert.Equal(ReplicaWriteCase.Written, (await ledger.WriteAsync(new Slot(4), payload, TestContext.Current.CancellationToken)).Case);
        }

        Assert.False(ledger.WriteContiguousTail());
        _ = ledger.RaiseKnownContiguousTail(new Slot(5));
        Assert.True(ledger.WriteContiguousTail());

        // A closed ledger's readers learn its recorded T from the map, so the ledger writes no tail once it observed T.
        ledger.ObserveRecordedTail(new Slot(6));
        Assert.False(ledger.WriteContiguousTail());

        Assert.All(replicas, static replica =>
            Assert.Equal([3L, 5], replica.ReadTailCalls.Where(static call => !call.Seal).Select(static call => call.ContiguousTail.Value)));
    }

    /// <summary>
    /// A recovery write keeps whatever it stores for the ledger's life (LR-I5), so a repair copies only bytes the quorum's
    /// batch verifier accepts: a batch which fails verification, as bytes damaged after the replica which sent them
    /// checked them do, repairs nothing, and neither does a quorum built with no verifier.
    /// </summary>
    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABatchWhichFailsVerificationRepairsNothing(bool verifier)
    {
        var replicas = Holding();
        replicas[0].ReadAsyncHandler = static (slot, _) => Answer('M', slot);
        var verified = new List<byte[]>();
        await using var quorum = LedgerQuorumTestData.Create(
            replicas,
            2,
            Window,
            preferredReplica: 0,
            verifyBatch: verifier ? batch => { verified.Add(batch.ToArray()); return false; }
        : null);

        (await quorum.ReadAsync(Below, Tail, TestContext.Current.CancellationToken)).Release();

        Assert.Equal(verifier ? ["held"u8.ToArray()] : [], verified);
        Assert.All(replicas, static replica => Assert.Empty(replica.WriteCalls));
    }

    /// <summary>
    /// Skipping a repair is always safe, so a quorum keeps at most <c>K</c> repairs in flight and one per slot: while the
    /// preferred replica holds its repairs, reads of <c>K + 1</c> slots start <c>K</c>, and a second read of a slot whose
    /// repair is pending starts none.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task RepairsInFlightStayWithinTheWindowAndOnePerSlot()
    {
        var token = TestContext.Current.CancellationToken;
        var replicas = Holding();
        replicas[0].ReadAsyncHandler = static (slot, _) => Answer('M', slot);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas[0].WriteAsyncHandler = async (_, _, _, _) =>
        {
            await release.Task;
            return ReplicaAnswers.Written;
        };
        var tail = new Slot(2 * Window.WindowSize);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0, verifyBatch: AcceptAll);
        try
        {
            (await quorum.ReadAsync(Slot.Zero, tail, token)).Release();
            (await quorum.ReadAsync(Slot.Zero, tail, token)).Release();
            for (var slot = 1L; slot <= Window.WindowSize; slot++)
            {
                (await quorum.ReadAsync(new Slot(slot), tail, token)).Release();
            }

            Assert.Equal(
                Enumerable.Range(0, Window.WindowSize).Select(static slot => (long)slot),
                replicas[0].WriteCalls.Select(static call => call.Address.Value));
        }
        finally
        {
            release.SetResult();
        }
    }

    /// <summary>
    /// A repair which ended frees its slot and its place among the <c>K</c>: with repairs the preferred replica answers at
    /// once, a slot read twice repairs twice, and reads of more than <c>K</c> slots repair each.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ARepairWhichEndedFreesItsSlotAndItsPlace()
    {
        var token = TestContext.Current.CancellationToken;
        var replicas = Holding();
        replicas[0].ReadAsyncHandler = static (slot, _) => Answer('M', slot);
        var tail = new Slot(2 * Window.WindowSize);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0, verifyBatch: AcceptAll);

        (await quorum.ReadAsync(Slot.Zero, tail, token)).Release();
        for (var slot = 0L; slot <= Window.WindowSize; slot++)
        {
            (await quorum.ReadAsync(new Slot(slot), tail, token)).Release();
        }

        Assert.Equal(
            Enumerable.Range(0, Window.WindowSize + 1).Prepend(0).Select(static slot => (long)slot),
            replicas[0].WriteCalls.Select(static call => call.Address.Value));
    }

    /// <summary>
    /// A learn asks the preferred replica alone, with a contiguous-tail read which does not seal and carries the known
    /// tail, and answers the greater of that tail and the replica's (LR-I6, LP-I11, ledger-protocol.md §8.2); a quorum with
    /// no preferred replica sends nothing.
    /// </summary>
    [Theory(Timeout = 10_000)]
    [InlineData(9L, 9L)]
    [InlineData(2L, 4L)]
    public async Task ALearnReadsThePreferredReplicasTailAlone(long replicaTail, long learned)
    {
        var replicas = Holding();
        replicas[1].ReadTailAsyncHandler = (_, seal, _) => ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(new Slot(replicaTail), seal));
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 1);
        await using var remote = LedgerQuorumTestData.Create(Holding(), 2, Window);

        Assert.Equal(new Slot(learned), await quorum.LearnTailAsync(new Slot(4), TestContext.Current.CancellationToken));
        Assert.Equal(new Slot(4), await remote.LearnTailAsync(new Slot(4), TestContext.Current.CancellationToken));

        var sent = Assert.Single(replicas[1].ReadTailCalls);
        Assert.Equal((new Slot(4), false), (sent.ContiguousTail, sent.Seal));
        Assert.Empty(replicas[0].ReadTailCalls);
        Assert.Empty(replicas[2].ReadTailCalls);
    }

    /// <summary>
    /// An answer which teaches nothing leaves the learned tail where it was: <c>Dropped</c>, <c>Backpressure</c>, or a
    /// failure. Once the quorum's reads stopped, a learn fails as every read does (ledger-protocol.md §6.3).
    /// </summary>
    [Theory(Timeout = 10_000)]
    [InlineData('D')]
    [InlineData('B')]
    [InlineData('t')]
    public async Task ALearnWhichLearnsNothingAnswersTheKnownTail(char answer)
    {
        var replicas = Holding();
        replicas[0].ReadTailAsyncHandler = (_, _, _) => answer switch
        {
            'D' => ValueTask.FromResult(new ReplicaTailRead(ReplicaWriteOutcome.Dropped.Instance)),
            'B' => ValueTask.FromResult(new ReplicaTailRead(ReplicaWriteOutcome.Backpressure.Instance)),
            _ => ValueTask.FromException<ReplicaTailRead>(new IOException("The replica is unreachable.")),
        };
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0);

        Assert.Equal(new Slot(4), await quorum.LearnTailAsync(new Slot(4), TestContext.Current.CancellationToken));

        await quorum.StopReadsAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await quorum.LearnTailAsync(new Slot(4), TestContext.Current.CancellationToken));
    }

    private static bool AcceptAll(ArcBuffer batch) => true;

    private static ValueTask<ReplicaRead> Answer(char answer, Slot slot) => answer switch
    {
        'M' => ValueTask.FromResult(ReplicaAnswers.Missing),
        'B' => ValueTask.FromResult(new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance)),
        'C' => ValueTask.FromException<ReplicaRead>(new LedgerReplicaCorruptException(slot, "The batch fails its checksum.")),
        'F' => ValueTask.FromException<ReplicaRead>(new ObjectDisposedException("replica", "The replica's quorum was disposed.")),
        't' => ValueTask.FromException<ReplicaRead>(new IOException("The replica is unreachable.")),
        _ => throw new ArgumentOutOfRangeException(nameof(answer)),
    };

    /// <summary>Returns replica doubles which each answer every read with a batch of their own.</summary>
    private static RecordingLedgerReplica[] Holding()
    {
        var replicas = Enumerable.Range(0, Replicas).Select(static _ => new RecordingLedgerReplica()).ToArray();
        foreach (var replica in replicas)
        {
            replica.ReadAsyncHandler = static (_, _) => ValueTask.FromResult(new ReplicaRead(LedgerReplicaTestData.Buffer("held"u8)));
        }

        return replicas;
    }

    /// <summary>
    /// Returns replica doubles whose reads each wait for the test to answer them, and signal when the read asks them.
    /// </summary>
    private static (RecordingLedgerReplica[] Replicas, TaskCompletionSource<ReplicaRead>[] Answers, TaskCompletionSource[] Asked) Held()
    {
        var replicas = Enumerable.Range(0, Replicas).Select(static _ => new RecordingLedgerReplica()).ToArray();
        var answers = replicas.Select(static _ => new TaskCompletionSource<ReplicaRead>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var asked = replicas.Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (var index = 0; index < Replicas; index++)
        {
            var answer = answers[index];
            var signal = asked[index];
            replicas[index].ReadAsyncHandler = (_, _) =>
            {
                signal.SetResult();
                return new ValueTask<ReplicaRead>(answer.Task);
            };
        }

        return (replicas, answers, asked);
    }

    private static Unblocker Unblock(TaskCompletionSource<ReplicaRead>[] answers) => new(answers);

    /// <summary>Answers every held read still waiting when the test ends, so the quorum's disposal never waits on one.</summary>
    private sealed class Unblocker(TaskCompletionSource<ReplicaRead>[] answers) : IDisposable
    {
        public void Dispose()
        {
            foreach (var answer in answers)
            {
                _ = answer.TrySetResult(ReplicaAnswers.Missing);
            }
        }
    }
}

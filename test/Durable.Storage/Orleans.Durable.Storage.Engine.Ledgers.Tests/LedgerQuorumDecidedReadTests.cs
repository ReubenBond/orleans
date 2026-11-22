using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the <see cref="LedgerQuorum"/>'s reads below the known contiguous tail (LP-I10; ledger-protocol.md §8.3,
/// §8.4): one replica at a time, the preferred one first or else one by rotation, the next on an answer without a batch,
/// one hedge outstanding after the hedge delay within the hedge budget, the losers cancelled once the read decides, and
/// §6.4's answer when no replica holds a batch. Each hedge is scripted on a clock which signals every timer it creates.
/// </summary>
public sealed class LedgerQuorumDecidedReadTests : IDisposable
{
    private const int Replicas = 3;
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 8 };
    private static readonly Slot Below = new(3);
    private static readonly Slot Tail = new(5);
    private static readonly TimeSpan Delay = LedgerQuorum.ReadHedgeDelay;
    // A bound on each signal well inside the test's timeout, so a defect which never signals fails an assertion.
    private static readonly TimeSpan Signal = TimeSpan.FromSeconds(3);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A read below the tail asks its preferred replica alone while that replica holds the batch; a read at the tail
    /// still asks every replica.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ABelowTailReadAsksThePreferredReplicaAlone()
    {
        var token = TestContext.Current.CancellationToken;
        var replicas = Holding();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 2);

        for (var read = 0; read < 3; read++)
        {
            (await quorum.ReadAsync(Below, Tail, token)).Release();
        }

        Assert.Equal([0, 0, 3], ReadCounts(replicas));
        (await quorum.ReadAsync(Tail, Tail, token)).Release();
        Assert.Equal([1, 1, 4], ReadCounts(replicas));
    }

    /// <summary>A quorum without a preferred replica rotates the first replica of each read below the tail.</summary>
    [Fact(Timeout = 10_000)]
    public async Task ABelowTailReadWithoutAPreferredReplicaRotatesItsFirstReplica()
    {
        var replicas = Holding();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);

        for (var read = 0; read < 3; read++)
        {
            (await quorum.ReadAsync(Below, Tail, TestContext.Current.CancellationToken)).Release();
        }

        Assert.Equal([1, 1, 1], ReadCounts(replicas));
    }

    /// <summary>The rotation turns evenly from any start, the largest included, under the build's checked arithmetic.</summary>
    [Fact(Timeout = 10_000)]
    public async Task TheRotationTurnsEvenlyFromAnyStart()
    {
        var replicas = Holding();
        await using var quorum = new LedgerQuorum(replicas, 2, Window, TimeProvider.System, preferredReplica: null, readRotation: uint.MaxValue - 1);

        for (var read = 0; read < 6; read++)
        {
            (await quorum.ReadAsync(Below, Tail, TestContext.Current.CancellationToken)).Release();
        }

        Assert.Equal([2, 2, 2], ReadCounts(replicas));
    }

    /// <summary>
    /// A ledger without a preferred replica starts its rotation where its ID puts it, so the first reads of different
    /// ledgers spread across their replicas.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task FirstReadsOfDifferentLedgersSpreadAcrossTheirReplicas()
    {
        var first = new int[Replicas];
        for (var id = 1UL; id <= 12; id++)
        {
            var replicas = Holding();
            await using var ledger = LedgerTestData.OnNodes(new LedgerId(id), replicas, 2, Window, TimeProvider.System);
            _ = ledger.RaiseKnownContiguousTail(Tail);
            (await ledger.ReadAsync(Below, TestContext.Current.CancellationToken)).Release();
            first[Array.FindIndex(ReadCounts(replicas), static count => count == 1)]++;
        }

        Assert.All(first, static count => Assert.True(count > 0));
    }

    /// <summary>
    /// A decided slot's batch is on <c>Q</c> replicas, so a replica's answer without one is not the slot's answer: a read
    /// whose preferred replica answers missing (a replica which missed the write), backpressure, a transient or permanent
    /// failure, or corruption asks the next replica at once, returns its batch, and asks no other.
    /// </summary>
    [Theory(Timeout = 10_000)]
    [InlineData('M')]
    [InlineData('B')]
    [InlineData('t')]
    [InlineData('F')]
    [InlineData('C')]
    public async Task ABelowTailReadAsksTheNextReplicaWhenOneAnswersWithoutABatch(char answer)
    {
        var replicas = Holding();
        replicas[0].ReadAsyncHandler = (slot, _) => Answer(answer, slot);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0);

        using (var batch = (await quorum.ReadAsync(Below, Tail, TestContext.Current.CancellationToken)).Batch)
        {
            Assert.Equal("held"u8.ToArray(), batch.ToArray());
        }

        Assert.Equal([1, 1, 0], ReadCounts(replicas));
    }

    /// <summary>LP-I5, ledger-protocol.md §6.4: a drop is definite and permanent, so a read below the tail answers it at once.</summary>
    [Fact(Timeout = 10_000)]
    public async Task ABelowTailReadAnswersADropAtOnce()
    {
        var replicas = Holding();
        replicas[0].ReadAsyncHandler = static (slot, _) => Answer('D', slot);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0);

        DroppedAnswers.AssertDropped(await quorum.ReadAsync(Below, Tail, TestContext.Current.CancellationToken));

        Assert.Equal([1, 0, 0], ReadCounts(replicas));
    }

    /// <summary>The cases of <see cref="ABelowTailReadWhichFindsNoBatchAnswersByThePrecedenceOfItsAnswers"/>.</summary>
    public static TheoryData<string, string, int> NoBatchCases
    {
        get
        {
            var cases = new TheoryData<string, string, int>();
            foreach (var preferred in Enumerable.Range(0, Replicas))
            {
                cases.Add("BBD", "D", preferred);
                cases.Add("FFB", "01", preferred);
                cases.Add("BtM", "B", preferred);
                cases.Add("tFM", "01", preferred);
                cases.Add("MMM", "I", preferred);
            }

            return cases;
        }
    }

    /// <summary>
    /// ledger-protocol.md §6.4, LP-I10: a read below the tail which finds no batch at any replica answers by the same
    /// precedence as any read, whichever replica it asks first: <c>Dropped</c> (<c>D</c>); the failures repetition cannot
    /// change once <c>N − Q + 1</c> replicas failed so; <c>Backpressure</c> (<c>B</c>); <see cref="InvalidDataException"/>
    /// (<c>I</c>) when every replica answers missing, never <c>Missing</c>; and otherwise every failure.
    /// <paramref name="reported"/> lists the replicas whose failures it reports.
    /// </summary>
    [Theory(Timeout = 10_000)]
    [MemberData(nameof(NoBatchCases))]
    public async Task ABelowTailReadWhichFindsNoBatchAnswersByThePrecedenceOfItsAnswers(string answers, string reported, int preferred)
    {
        var failures = answers.Select(static answer => Failure(answer)).ToArray();
        var replicas = Holding();
        for (var index = 0; index < Replicas; index++)
        {
            var answer = answers[index];
            var failure = failures[index];
            replicas[index].ReadAsyncHandler = (slot, _) => failure is null ? Answer(answer, slot) : ValueTask.FromException<ReplicaRead>(failure);
        }

        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: preferred);

        var read = quorum.ReadAsync(Below, Tail, TestContext.Current.CancellationToken).AsTask();
        switch (reported)
        {
            case "D":
                DroppedAnswers.AssertDropped(await read);
                break;
            case "B":
                Assert.Equal(ReplicaReadCase.Backpressure, (await read).Case);
                break;
            case "I":
                _ = await Assert.ThrowsAsync<InvalidDataException>(() => read);
                break;
            default:
                var error = await Assert.ThrowsAsync<AggregateException>(() => read);
                Assert.Equal(
                    reported.Select(replica => failures[replica - '0']).ToHashSet(ReferenceEqualityComparer.Instance),
                    error.InnerExceptions.ToHashSet(ReferenceEqualityComparer.Instance));
                break;
        }
    }

    /// <summary>
    /// ledger-protocol.md §8.4: a read whose replica outlasts the hedge delay asks one more, the hedge, and no further
    /// replica while that hedge is pending, however long it waits; an answer without a batch then asks the next at once,
    /// and the read returns the first batch. The batch a slow replica reports late is released.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ABelowTailReadHedgesWithOneReplicaAtATime()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new TimerSignallingTimeProvider();
        var (replicas, answers, asked) = Held();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, time, preferredReplica: 0);
        using var unblock = Unblock(answers);
        using var found = new TransferredArcBuffer("found"u8);
        using var late = new TransferredArcBuffer("late"u8);

        var reading = quorum.ReadAsync(Below, Tail, token).AsTask();
        await time.WaitForTimersAsync(1, token).WaitAsync(Signal, TimeProvider.System, token);
        Assert.Equal(Delay, Assert.Single(time.DueTimes));
        time.Advance(Delay - TimeSpan.FromTicks(1));
        Assert.Equal([1, 0, 0], ReadCounts(replicas));

        time.Advance(TimeSpan.FromTicks(1));
        await asked[1].Task.WaitAsync(Signal, TimeProvider.System, token);
        Assert.Equal([1, 1, 0], ReadCounts(replicas));

        // The hedge is pending, so the read arms no timer and asks no replica, however long it waits.
        time.Advance(10 * Delay);
        Assert.Equal(1, time.Timers);
        Assert.Equal([1, 1, 0], ReadCounts(replicas));

        answers[1].SetResult(ReplicaAnswers.Missing);
        await asked[2].Task.WaitAsync(Signal, TimeProvider.System, token);
        Assert.Equal([1, 1, 1], ReadCounts(replicas));

        answers[2].SetResult(new ReplicaRead(found.Take()));
        using (var batch = (await reading.WaitAsync(Signal, TimeProvider.System, token)).Batch)
        {
            Assert.Equal("found"u8.ToArray(), batch.ToArray());
        }

        Assert.Equal(1, quorum.ReadHedges);
        answers[0].SetResult(new ReplicaRead(late.Take()));
        await quorum.DisposeAsync();
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(late.Pages, 1);
    }

    /// <summary>
    /// ledger-protocol.md §8.3: once a read decides, it cancels the replica reads it sent after its first, so those
    /// which lost stop where their transport can stop them.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ABelowTailReadCancelsTheHedgeOnceItDecides()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new TimerSignallingTimeProvider();
        var (replicas, answers, _) = Held();
        var hedgeToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas[1].ReadAsyncHandler = (_, cancellationToken) =>
        {
            hedgeToken.SetResult(cancellationToken);
            return new ValueTask<ReplicaRead>(answers[1].Task);
        };
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, time, preferredReplica: 0);
        using var unblock = Unblock(answers);
        using var found = new TransferredArcBuffer("found"u8);

        var reading = quorum.ReadAsync(Below, Tail, token).AsTask();
        await time.WaitForTimersAsync(1, token).WaitAsync(Signal, TimeProvider.System, token);
        time.Advance(Delay);
        var hedge = await hedgeToken.Task.WaitAsync(Signal, TimeProvider.System, token);
        Assert.False(hedge.IsCancellationRequested);

        answers[0].SetResult(new ReplicaRead(found.Take()));
        (await reading.WaitAsync(Signal, TimeProvider.System, token)).Release();

        Assert.True(hedge.IsCancellationRequested);
        _ = answers[1].TrySetCanceled(hedge);
    }

    /// <summary>
    /// ledger-protocol.md §6.3: a read cancelled while it waits on a hedge fails with the cancellation, asks no further
    /// replica, cancels its hedge, and releases every batch its replicas report afterwards.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ABelowTailReadCancelledDuringAHedgeAsksNoMoreAndReleasesLateBatches()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new TimerSignallingTimeProvider();
        var (replicas, answers, asked) = Held();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, time, preferredReplica: 0);
        using var unblock = Unblock(answers);
        using var first = new TransferredArcBuffer("first"u8);
        using var second = new TransferredArcBuffer("second"u8);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

        var reading = quorum.ReadAsync(Below, Tail, cancellation.Token).AsTask();
        await time.WaitForTimersAsync(1, token).WaitAsync(Signal, TimeProvider.System, token);
        time.Advance(Delay);
        await asked[1].Task.WaitAsync(Signal, TimeProvider.System, token);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Signal, TimeProvider.System, token));
        time.Advance(10 * Delay);
        Assert.Equal([1, 1, 0], ReadCounts(replicas));

        answers[0].SetResult(new ReplicaRead(first.Take()));
        answers[1].SetResult(new ReplicaRead(second.Take()));
        await quorum.DisposeAsync();
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(first.Pages, 1);
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(second.Pages, 1);
    }

    /// <summary>
    /// ledger-protocol.md §6.3: disposal stops a read in its hedge, which fails with <see cref="ObjectDisposedException"/>
    /// though its caller's token stands, then waits for the replica calls it left running and releases the batches which
    /// arrive after it.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task DisposalStopsAReadInItsHedgeAndReleasesTheBatchesWhichArriveLater()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new TimerSignallingTimeProvider();
        var (replicas, answers, asked) = Held();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, time, preferredReplica: 0);
        using var unblock = Unblock(answers);
        using var found = new TransferredArcBuffer("found"u8);
        using var late = new TransferredArcBuffer("late"u8);

        var reading = quorum.ReadAsync(Below, Tail, token).AsTask();
        await time.WaitForTimersAsync(1, token).WaitAsync(Signal, TimeProvider.System, token);
        time.Advance(Delay);
        await asked[1].Task.WaitAsync(Signal, TimeProvider.System, token);
        var disposal = quorum.DisposeAsync().AsTask();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => reading.WaitAsync(Signal, TimeProvider.System, token));
        Assert.False(disposal.IsCompleted);

        answers[1].SetResult(new ReplicaRead(found.Take()));
        Assert.False(disposal.IsCompleted);
        answers[0].SetResult(new ReplicaRead(late.Take()));
        await disposal.WaitAsync(Signal, TimeProvider.System, token);
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(found.Pages, 1);
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(late.Pages, 1);
    }

    /// <summary>
    /// ledger-protocol.md §8.4: hedges spend a budget which reads below the tail refill at a tenth of a hedge each, up to
    /// ten hedges, so a replica which is slow on every read is hedged on its first eleven reads, the burst and the tenths
    /// its reads deposited, and from then on on every tenth, and a read which meets an empty budget waits on its replica.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task HedgesStayWithinTheirBudget()
    {
        const int Reads = 40;
        var token = TestContext.Current.CancellationToken;
        var time = new TimerSignallingTimeProvider();
        var slow = new Queue<TaskCompletionSource<ReplicaRead>>();
        var replicas = Holding();
        replicas[0].ReadAsyncHandler = (_, _) =>
        {
            var answer = new TaskCompletionSource<ReplicaRead>(TaskCreationOptions.RunContinuationsAsynchronously);
            slow.Enqueue(answer);
            return new ValueTask<ReplicaRead>(answer.Task);
        };
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, time, preferredReplica: 0);
        var hedged = new List<int>();

        try
        {
            await ReadAllAsync();
        }
        finally
        {
            while (slow.TryDequeue(out var answer))
            {
                _ = answer.TrySetResult(ReplicaAnswers.Missing);
            }
        }

        Assert.Equal([.. Enumerable.Range(1, 11), 21, 31], hedged);
        Assert.Equal(hedged.Count, quorum.ReadHedges);
        Assert.Equal(Reads - hedged.Count, quorum.ReadHedgeBudgetExhaustions);

        async Task ReadAllAsync()
        {
            for (var read = 0; read < Reads; read++)
            {
                var decided = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                quorum.HedgeDecided = hedge => decided.TrySetResult(hedge);
                var reading = quorum.ReadAsync(Below, Tail, token).AsTask();
                await time.WaitForTimersAsync(read + 1, token).WaitAsync(Signal, TimeProvider.System, token);
                time.Advance(Delay);

                // The read either hedges to a replica which answers at once, or meets an empty budget and waits on its own.
                if (await decided.Task.WaitAsync(Signal, TimeProvider.System, token))
                {
                    hedged.Add(read + 1);
                }
                else
                {
                    slow.Last().SetResult(new ReplicaRead(LedgerReplicaTestData.Buffer("slow"u8)));
                }

                (await reading.WaitAsync(Signal, TimeProvider.System, token)).Release();
            }
        }
    }

    /// <summary>
    /// ledger-protocol.md §8.4: one hedge of a read is pending at a time, not one per read. Over four replicas, a read
    /// hedges a slow first replica, its hedge answers missing, so it asks the next at once, and once the delay passes
    /// with that one slow too, it hedges again, with the last replica.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ABelowTailReadHedgesAgainOnceItsHedgeAnswered()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new TimerSignallingTimeProvider();
        var (replicas, answers, asked) = Held(4);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, time, preferredReplica: 0);
        using var unblock = Unblock(answers);
        using var found = new TransferredArcBuffer("found"u8);

        var reading = quorum.ReadAsync(Below, Tail, token).AsTask();
        await time.WaitForTimersAsync(1, token).WaitAsync(Signal, TimeProvider.System, token);
        time.Advance(Delay);
        await asked[1].Task.WaitAsync(Signal, TimeProvider.System, token);

        answers[1].SetResult(ReplicaAnswers.Missing);
        await asked[2].Task.WaitAsync(Signal, TimeProvider.System, token);
        Assert.Equal([1, 1, 1, 0], ReadCounts(replicas));

        await time.WaitForTimersAsync(2, token).WaitAsync(Signal, TimeProvider.System, token);
        time.Advance(Delay);
        await asked[3].Task.WaitAsync(Signal, TimeProvider.System, token);
        Assert.Equal([1, 1, 1, 1], ReadCounts(replicas));
        Assert.Equal(2, quorum.ReadHedges);

        answers[3].SetResult(new ReplicaRead(found.Take()));
        using var batch = (await reading.WaitAsync(Signal, TimeProvider.System, token)).Batch;
        Assert.Equal("found"u8.ToArray(), batch.ToArray());
    }

    /// <summary>
    /// ledger-protocol.md §8.3: a read without a preferred replica asks its first, remote, replica under a token it
    /// cancels once a hedge wins, so a first call still waiting in its transport's queue leaves it.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AHedgeWhichWinsCancelsTheFirstRemoteRead()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new TimerSignallingTimeProvider();
        var (replicas, answers, _) = Held();
        var firstToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas[1].ReadAsyncHandler = (_, cancellationToken) =>
        {
            firstToken.SetResult(cancellationToken);
            return new ValueTask<ReplicaRead>(answers[1].Task);
        };
        replicas[2].ReadAsyncHandler = static (_, _) => ValueTask.FromResult(new ReplicaRead(LedgerReplicaTestData.Buffer("found"u8)));
        await using var quorum = new LedgerQuorum(replicas, 2, Window, time, preferredReplica: null, readRotation: 0);
        using var unblock = Unblock(answers);

        var reading = quorum.ReadAsync(Below, Tail, token).AsTask();
        var first = await firstToken.Task.WaitAsync(Signal, TimeProvider.System, token);
        await time.WaitForTimersAsync(1, token).WaitAsync(Signal, TimeProvider.System, token);
        time.Advance(Delay);
        (await reading.WaitAsync(Signal, TimeProvider.System, token)).Release();

        Assert.Equal([0, 1, 1], ReadCounts(replicas));
        Assert.True(first.IsCancellationRequested);
        _ = answers[1].TrySetCanceled(first);
    }

    /// <summary>
    /// A preferred replica which missed writes below the contiguous tail, slot 1 as a hole and slot 3 as the latest write
    /// of a replica lagging behind, answers missing there, so the read takes another replica's batch; where it holds the
    /// batch, the read asks it alone. Every read returns the batch the writer wrote.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ABelowTailReadPassesAPreferredReplicaWhichMissedWrites()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Enumerable.Range(0, Replicas)
            .Select(_ => new ControlledLedgerReplica(nodes.Open(NodeId.New(), LedgerReplicaTestData.Ledger, Window)))
            .ToArray();
        replicas[0].WriteFailure = static (slot, _) => slot.Value is 1 or 3 ? new IOException("The write was lost.") : null;
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, preferredReplica: 0);
        for (var slot = 0; slot < 4; slot++)
        {
            using var payload = LedgerReplicaTestData.Buffer([(byte)slot]);
            Assert.Equal(ReplicaWriteCase.Written, (await quorum.WriteAsync(new Slot(slot), payload, new Slot(slot), token)).Case);
        }

        for (var slot = 0; slot < 4; slot++)
        {
            using var batch = (await quorum.ReadAsync(new Slot(slot), new Slot(4), token)).Batch;
            Assert.Equal([(byte)slot], batch.ToArray());
        }

        Assert.Equal([0L, 1, 2, 3], replicas[0].Reads.Select(static read => read.Address.Value));
        Assert.Equal([1L, 3], replicas[1].Reads.Select(static read => read.Address.Value));
        Assert.Empty(replicas[2].Reads);
    }

    private static ValueTask<ReplicaRead> Answer(char answer, Slot slot) => answer switch
    {
        'M' => ValueTask.FromResult(ReplicaAnswers.Missing),
        'B' => ValueTask.FromResult(new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance)),
        'D' => ValueTask.FromResult(new ReplicaRead(ReplicaWriteOutcome.Dropped.Instance)),
        'C' => ValueTask.FromException<ReplicaRead>(new LedgerReplicaCorruptException(slot, "The batch fails its checksum.")),
        _ => ValueTask.FromException<ReplicaRead>(Failure(answer)!),
    };

    private static Exception? Failure(char answer) => answer switch
    {
        'F' => new ObjectDisposedException("replica", "The replica's quorum was disposed."),
        't' => new IOException("The replica is unreachable."),
        _ => null,
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
    private static (RecordingLedgerReplica[] Replicas, TaskCompletionSource<ReplicaRead>[] Answers, TaskCompletionSource[] Asked) Held(int count = Replicas)
    {
        var replicas = Enumerable.Range(0, count).Select(static _ => new RecordingLedgerReplica()).ToArray();
        var answers = replicas.Select(static _ => new TaskCompletionSource<ReplicaRead>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var asked = replicas.Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (var index = 0; index < count; index++)
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

    /// <summary>
    /// Answers every held read still waiting when the test ends, so a failed assertion surfaces rather than the quorum's
    /// disposal waiting on reads nobody answers.
    /// </summary>
    private static Unblocker Unblock(TaskCompletionSource<ReplicaRead>[] answers) => new(answers);

    private static int[] ReadCounts(RecordingLedgerReplica[] replicas) => [.. replicas.Select(static replica => replica.ReadCalls.Count)];

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

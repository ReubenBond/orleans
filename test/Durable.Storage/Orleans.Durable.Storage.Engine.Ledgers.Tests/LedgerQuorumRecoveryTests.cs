using System.Globalization;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the <see cref="LedgerQuorum"/>'s recovery (ledger-protocol.md §8.3; LP-I6, LP-I7): it seals every replica,
/// reads from the contiguous tail <c>c</c> its first <c>N − Q + 1</c> seal answers give, copies every position some
/// replica holds to <c>Q</c> replicas, and ends at the first position <c>N − Q + 1</c> sealed replicas lack, so its end
/// lies above every acknowledged write and no writer write at it is ever written. A replica which is down or slow
/// delays it, and a dropped answer, DATA beyond the window, or replicas which can no longer decide or hold a position
/// fail it; the values it buffers stay within its byte budget, and its caller's cancellation surfaces as cancellation.
/// </summary>
public sealed class LedgerQuorumRecoveryTests : IDisposable
{
    private static readonly LedgerReplicaOptions Window4 = new() { WindowSize = 4 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LP-I6, LP-I7, LP-I4: over replicas a single writer filled under the window discipline, some of its writes
    /// reaching only some replicas, with up to <c>N − max(Q, N − Q + 1)</c> replicas unreachable, a writer racing the
    /// recovery, and a carried contiguous tail no greater than the acknowledged one, the end <c>T</c> it returns lies at or
    /// above every acknowledged contiguous prefix, no writer write at <c>T</c> is ever written, every position below
    /// <c>T</c> holds its one value on <c>Q</c> replicas, and a later recovery ends no lower. The contiguous tail <c>c</c>
    /// it reads from, which every read and recovery write carries and the replica retains, is at least the carried tail and
    /// the <c>(N − Q + 1)</c>-th smallest contiguous tail the reachable replicas retained before it, since it is the
    /// greatest among <c>N − Q + 1</c> of their seal answers, and at most the greatest tail any of them could report; it
    /// equals the greatest of the carried tail and the first <c>N − Q + 1</c> seal answers' tails the observer reports;
    /// every recovery write copies a position's one value below <c>T</c>.
    /// </summary>
    [Fact]
    public Task ARecoveryEndsAboveEveryAcknowledgedWriteWithEveryLowerPositionOnQ() =>
        Scenarios.SampleAsync(scenario => _leaks.InScopeAsync(() => CheckAsync(scenario)), iter: 300);

    /// <summary>
    /// Regression test for #119, LP-I7: DATA on a quorum at a position above a hole which only one replica fills, while
    /// every replica's retained contiguous tail lies below it. Recovery reads from <c>c</c>, never below it, finds the
    /// minority DATA and the quorum's, copies the minority value from the replica which reported it, and ends above
    /// both. The seal alignment which recovery replaced found no source to fill the hole from.
    /// </summary>
    [Fact]
    public async Task ARecoveryFindsDataOnAQuorumAboveAHoleWhichOneReplicaFills()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0, 1, 2], address: 0, contiguousTail: 0);
        await WriteAsync(replicas, [0], address: 1, contiguousTail: 1);
        await WriteAsync(replicas, [1, 2], address: 2, contiguousTail: 1);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, new FakeTimeProvider());

        Assert.Equal(new LedgerRecovery(new Slot(1), new Slot(3)), await quorum.RecoverAsync(Slot.Zero, token));

        Assert.All(replicas, static replica => Assert.Equal(0, replica.ReadsOf(0)));
        for (var address = 0L; address < 3; address++)
        {
            Assert.InRange(await HoldersAsync(replicas, address), 2, 3);
        }

        Assert.DoesNotContain(replicas[0].Writes, static write => write.Recovery && write.Address.Value != 2);
        Assert.All(replicas[1..], static replica => Assert.Contains(replica.Writes, static write => write.Recovery && write.Address.Value == 1));
        Assert.All(replicas.SelectMany(static replica => replica.Writes.Where(static write => write.Recovery)), static write =>
        {
            Assert.Equal(Value(write.Address.Value), write.Payload);
            Assert.Equal(new Slot(1), write.ContiguousTail);
        });
    }

    /// <summary>
    /// LP-I11, LP-I7: a recovery returns the contiguous tail <c>c</c> it read from, the greatest of the tail it carries and
    /// its seal answers, and forwards it with its reads and recovery writes.
    /// </summary>
    [Fact]
    public async Task ARecoveryReturnsAndCarriesTheTailItReadsFrom()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        for (var address = 0L; address < 3; address++)
        {
            await WriteAsync(replicas, [0, 1, 2], address, contiguousTail: 2);
        }

        await WriteAsync(replicas, [0], address: 3, contiguousTail: 2);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, new FakeTimeProvider());

        Assert.Equal(new LedgerRecovery(new Slot(2), new Slot(4)), await quorum.RecoverAsync(new Slot(1), token));

        var copies = replicas.SelectMany(static replica => replica.Writes.Where(static write => write.Recovery)).ToArray();
        Assert.Contains(copies, static write => write.Address.Value == 3);
        Assert.All(copies, static write =>
        {
            Assert.InRange(write.Address.Value, 2, 3);
            Assert.Equal(new Slot(2), write.ContiguousTail);
        });
        Assert.Equal(new LedgerTail(new Slot(2), IsSealed: true), (await quorum.CheckAsync(token)).Tail);
    }

    /// <summary>
    /// LP-I7: a missing answer to a read sent before the replica's seal answer does not count toward the end, since a
    /// writer write may land there before the seal does; the recovery reads the position again once the seal answers.
    /// A writer write at position 0 reached replica 0, which cannot read, before the recovery, and reaches replica 2
    /// only after replica 2 answered the recovery's first read of position 0 missing, while its seal is held: the write
    /// is then on <c>Q = 2</c> replicas, written. Replica 1 lacks it, so a recovery which counted replica 2's first answer
    /// would end at 0, below the written position; this one reads replica 2 again once its seal answers, finds the DATA,
    /// copies it, and ends above it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AMissingAnswerReadBeforeTheReplicasSealAnswerIsReadAgain()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0], address: 0, contiguousTail: 0);
        replicas[0].ReadFailure = static _ => new IOException("The replica cannot read.");
        replicas[2].HoldSeals();
        var steps = new List<RecoveryStep>();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, new FakeTimeProvider(), recoveryObserver: steps.Add);

        var recovery = quorum.RecoverAsync(Slot.Zero, token).AsTask();
        await LedgerStoreTestExtensions.WaitUntilAsync(
            () => replicas[1].AnsweredReadsOf(0) > 0 && replicas[2].AnsweredReadsOf(0) > 0, "replicas 1 and 2 answer position 0 missing");
        await WriteAsync(replicas, [2], address: 0, contiguousTail: 0);
        Assert.False(recovery.IsCompleted);

        replicas[2].ReleaseSeals();
        var end = (await recovery).Recovery.End;
        Assert.Equal(new Slot(1), end);
        Assert.Equal(2, replicas[2].ReadsOf(0));
        replicas[0].ReadFailure = null;
        Assert.Equal(3, await HoldersAsync(replicas, 0));

        // The observer reports replica 2's first answer at position 0 as read before its seal answer, and the read the
        // recovery sent again once it handled that seal answer as read after it.
        CheckSteps(steps, replicas, end, quorum: 2);
        var seal = steps.FindIndex(static step => step is { Kind: RecoveryStepKind.Seal, Replica: 2 });
        var first = steps.FindIndex(static step => step is { Kind: RecoveryStepKind.Read, Replica: 2, Address: 0 });
        var again = steps.FindLastIndex(static step => step is { Kind: RecoveryStepKind.Read, Replica: 2, Address: 0 });
        Assert.True(first < seal && seal < again, string.Create(CultureInfo.InvariantCulture, $"Replica 2 read position 0 at {first} and {again}, and sealed at {seal}."));
        Assert.Equal((RecoveryAnswer.Missing, false), (steps[first].Answer, steps[first].AfterSeal));
        Assert.Equal((RecoveryAnswer.Batch, true), (steps[again].Answer, steps[again].AfterSeal));
    }

    /// <summary>
    /// The observer a recovery is given receives each replica answer the recovery handles, once, in the order it handles
    /// them (test/Durable.Storage/docs/spec/verification.md §5.1): over the hole of
    /// <see cref="ARecoveryFindsDataOnAQuorumAboveAHoleWhichOneReplicaFills"/>, the seals' answers, the reads from
    /// <c>c = 1</c> which find replica 0's DATA at position 1 and the end at 3, and the recovery writes which copy
    /// position 1 to the other two replicas, all below the end.
    /// </summary>
    [Fact]
    public async Task TheObserverReportsEachAnswerOnceInTheOrderTheRecoveryHandlesIt()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0, 1, 2], address: 0, contiguousTail: 0);
        await WriteAsync(replicas, [0], address: 1, contiguousTail: 1);
        await WriteAsync(replicas, [1, 2], address: 2, contiguousTail: 1);
        var steps = new List<RecoveryStep>();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, new FakeTimeProvider(), recoveryObserver: steps.Add);

        var end = (await quorum.RecoverAsync(Slot.Zero, token)).Recovery.End;

        Assert.Equal(new Slot(3), end);
        CheckSteps(steps, replicas, end, quorum: 2);
        Assert.DoesNotContain(steps, static step => step is { Kind: RecoveryStepKind.Read, Address: < 1 });
        Assert.Contains(steps, static step => step is { Kind: RecoveryStepKind.Read, Replica: 0, Address: 1, Answer: RecoveryAnswer.Batch, AfterSeal: true });
        Assert.Contains(steps, static step => step is { Kind: RecoveryStepKind.RecoveryWrite, Replica: 1, Address: 1, Failure: null });
        Assert.Contains(steps, static step => step is { Kind: RecoveryStepKind.RecoveryWrite, Replica: 2, Address: 1, Failure: null });
    }

    /// <summary>
    /// ledger-protocol.md §6.5: a replica whose seals fail transiently is asked again with backoff on the replica's clock until it
    /// answers, and a replica which stays down delays nothing once the others decide: the recovery ends once
    /// <c>max(Q, N − Q + 1)</c> replicas answer.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ATransientlyFailingReplicaIsAskedAgainUntilItAnswers()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0, 1], address: 0, contiguousTail: 0);
        replicas[0].FailSeals(2);
        replicas[1].Unreachable = true;
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, time);

        var recovery = quorum.RecoverEndAsync(Slot.Zero, token).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, token);
        Assert.False(recovery.IsCompleted);
        while (!recovery.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            _ = await Task.WhenAny(recovery, Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, token));
        }

        Assert.Equal(new Slot(1), await recovery);
        Assert.Equal(3, replicas[0].SealCalls);
        Assert.Equal(Value(0), await ReadAsync(replicas[2], 0));
        replicas[1].Unreachable = false;
    }

    /// <summary>
    /// LP-I7: every writer write lies below <c>c + 2K</c>, the recovery's contiguous tail plus two windows, so DATA up to
    /// <c>c + 2K − 1</c> keeps the window discipline: with <c>c = 0</c> and <c>K = 2</c>, DATA on a quorum at every
    /// position from 0 to 3 is recovered, and the recovery reads position 4 and ends there.
    /// </summary>
    [Fact]
    public async Task DataUpToTheLastPositionBelowTwoWindowsPastTheTailIsRecovered()
    {
        var token = TestContext.Current.CancellationToken;
        var window = new LedgerReplicaOptions { WindowSize = 2 };
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, window);
        await WriteQuorumsAsync(replicas, positions: 4);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, window, new FakeTimeProvider());

        Assert.Equal(new LedgerRecovery(Slot.Zero, new Slot(4)), await quorum.RecoverAsync(Slot.Zero, token));
        Assert.All(replicas, static replica => Assert.Equal(1, replica.ReadsOf(4)));
        for (var address = 0L; address < 4; address++)
        {
            Assert.InRange(await HoldersAsync(replicas, address), 2, 3);
        }
    }

    /// <summary>
    /// LP-I7: DATA at <c>c + 2K</c>, here at position 4 with <c>c = 0</c> and <c>K = 2</c>, breaks the window discipline,
    /// and the recovery fails with <see cref="InvalidDataException"/> rather than end past it.
    /// </summary>
    [Fact]
    public async Task DataAtOrAboveTwoWindowsPastTheTailFailsTheRecovery()
    {
        var token = TestContext.Current.CancellationToken;
        var window = new LedgerReplicaOptions { WindowSize = 2 };
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, window);
        await WriteQuorumsAsync(replicas, positions: 5);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, window, new FakeTimeProvider());

        _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await quorum.RecoverAsync(Slot.Zero, token));
    }

    /// <summary>
    /// LP-I7: a position the recovery found must reach <c>Q</c> replicas before the recovery ends above it, so when a
    /// replica's recovery write fails in a way repetition cannot change and too few replicas remain to hold the position,
    /// the recovery fails with <see cref="AggregateException"/> holding that failure. With <c>N = Q = 3</c>, replica 0
    /// alone reports DATA at position 0 while replicas 1 and 2 cannot read it yet, and replica 1's replica was disposed.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ACopyWhichCannotReachTheQuorumFailsTheRecovery()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0], address: 0, contiguousTail: 0);
        var failed = new ObjectDisposedException("replica", "The replica's quorum was disposed.");
        replicas[1].ReadFailure = CannotReadPositionZero;
        replicas[2].ReadFailure = CannotReadPositionZero;
        replicas[1].WriteFailure = (_, recovery) => recovery ? failed : null;
        await using var quorum = LedgerQuorumTestData.Create(replicas, 3, Window4, new FakeTimeProvider());

        var failure = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.RecoverAsync(Slot.Zero, token));
        Assert.Contains(failed, failure.InnerExceptions);
        Assert.Contains(replicas[1].Writes, static write => write.Recovery && write.Address == Slot.Zero);

        static Exception? CannotReadPositionZero(Slot slot) =>
            slot == Slot.Zero ? new IOException("The replica cannot read the position yet.") : null;
    }

    /// <summary>
    /// ledger-protocol.md §8.3: a recovery buffers each value it found until <c>Q</c> replicas hold it, and reads no further
    /// position while its buffered values reach its byte budget, here one value. Replica 0 alone holds positions 0 to 3,
    /// which replica 2 cannot read yet, so only replica 0's DATA decides them; with replica 1's recovery writes held, the
    /// recovery reads the window's positions 0 and 1 and no further. Once replica 1 takes the copies, <c>Q</c> replicas
    /// hold those positions, the recovery releases their values and reads on, and it ends at position 4, with every lower
    /// position on <c>Q</c> replicas. Its copies to the slow replica 2 stay held throughout: once <c>Q</c> replicas hold a
    /// position, the recovery cancels them and ends without them, and every value it buffered is released.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ARecoveryReadsNoFurtherWhileItsBufferedValuesReachItsBudget()
    {
        var token = TestContext.Current.CancellationToken;
        var window = new LedgerReplicaOptions { WindowSize = 2 };
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, window);
        for (var address = 0L; address < 4; address++)
        {
            await WriteAsync(replicas, [0], address, contiguousTail: 0);
        }

        replicas[2].ReadFailure = static address => address.Value < 4 ? new IOException("The replica cannot read the position yet.") : null;
        replicas[1].HoldWrites();
        replicas[2].HoldWrites();
        await using var quorum = new LedgerQuorum(replicas, 2, window, new FakeTimeProvider()) { RecoveryByteBudget = Value(0).Length };

        var recovery = quorum.RecoverEndAsync(Slot.Zero, token).AsTask();
        await LedgerStoreTestExtensions.WaitUntilAsync(
            () => replicas[1].HeldWrites == 2 && replicas[2].HeldWrites == 2, "the copies of positions 0 and 1 are held");
        Assert.All(replicas, static replica => Assert.Equal(0, replica.ReadsOf(2)));
        Assert.False(recovery.IsCompleted);

        replicas[1].ReleaseWrites();
        Assert.Equal(new Slot(4), await recovery);
        Assert.All(replicas, static replica => Assert.True(replica.ReadsOf(3) > 0));
        Assert.Equal(4, replicas[2].HeldWrites);
        Assert.All(replicas[2].Writes, static write => Assert.True(write.CancellationToken.IsCancellationRequested, "A copy to the slow replica was not cancelled."));
        replicas[2].ReadFailure = null;
        for (var address = 0L; address < 4; address++)
        {
            Assert.Equal(2, await HoldersAsync(replicas, address));
        }

        replicas[2].ReleaseWrites();
    }

    /// <summary>
    /// The caller's cancellation of a recovery surfaces as <see cref="OperationCanceledException"/>, never as the failure
    /// of the replicas whose operations it stopped. The cancellation comes from inside replica 1's seal, or from inside its
    /// recovery write of position 0, which only replica 0 holds and replica 2 answers corrupt; the replica operation then
    /// observes it, and the recovery's other operations stop, so their events are already waiting when the recovery next
    /// looks. A recovery which accounted for those events as replica failures would run out of replicas and fail with
    /// <see cref="AggregateException"/>.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationIsReportedAsCancellation(bool copying)
    {
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0], address: 0, contiguousTail: 0);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (copying)
        {
            replicas[2].ReadFailure = static address => address == Slot.Zero ? new LedgerReplicaCorruptException("The slot is faulty.") : null;
            replicas[1].WriteFailure = (_, recovery) => recovery ? Cancel() : null;
        }
        else
        {
            replicas[1].SealFailure = Cancel;
        }

        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, new FakeTimeProvider());

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await quorum.RecoverAsync(Slot.Zero, cancellation.Token));
        Assert.Equal(copying ? 1 : 0, replicas[1].Writes.Count(static write => write.Recovery));

        Exception Cancel()
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        }
    }

    /// <summary>
    /// ReplicaRetry, ledger-protocol.md §6.5: a cancellation a replica raises itself, such as its transport's timeout, is not the
    /// recovery's, so the recovery asks the replica again with backoff on the replica's clock. Replica 0's seal times out
    /// twice and replica 1's read of position 1 is cancelled twice; with replica 2 unreachable the recovery needs both,
    /// and it ends once they answer.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AReplicasOwnCancellationIsAskedAgain()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0, 1], address: 0, contiguousTail: 0);
        var sealTimeouts = 2;
        var readCancellations = 2;
        replicas[0].SealFailure = () => Interlocked.Decrement(ref sealTimeouts) >= 0 ? new TaskCanceledException("The seal timed out.") : null;
        replicas[1].ReadFailure = address =>
            address.Value == 1 && Interlocked.Decrement(ref readCancellations) >= 0 ? new OperationCanceledException("The read was cancelled.") : null;
        replicas[2].Unreachable = true;
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, time);

        Assert.Equal(new Slot(1), await AdvanceUntilAsync(time, quorum.RecoverEndAsync(Slot.Zero, token).AsTask()));
        Assert.Equal(3, replicas[0].SealCalls);
        Assert.Equal(3, replicas[1].ReadsOf(1));
        replicas[2].Unreachable = false;
    }

    /// <summary>
    /// LR-I8, LP-I7: no replica answers a recovery write <see cref="ReplicaAnswers.Sealed"/>, so a replica which does
    /// misbehaves, and its answer holds nothing toward <c>Q</c>: it counts as that replica's failure. Replica 0 alone holds
    /// position 0, and replica 1, whose held seal keeps its missing answers from counting, answers its copy sealed and
    /// stores nothing. With <c>Q = 2</c> the recovery waits for replica 2's copy, held until the test releases it, and ends
    /// with position 0 on the two honest replicas; with <c>Q = 3</c>, replica 2's seal held too, no quorum can hold the
    /// position, and the recovery fails with <see cref="AggregateException"/>.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ARecoveryWriteAnsweredSealedNeverCountsTowardTheQuorum(int q)
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0], address: 0, contiguousTail: 0);
        replicas[1].AnswersRecoveryWritesSealed = true;
        replicas[1].HoldSeals();
        if (q == 3)
        {
            replicas[2].HoldSeals();
        }
        else
        {
            replicas[2].HoldWrites(observeCancellation: true);
        }

        await using var quorum = LedgerQuorumTestData.Create(replicas, q, Window4, new FakeTimeProvider());
        try
        {
            var recovery = quorum.RecoverEndAsync(Slot.Zero, token).AsTask();
            if (q == 3)
            {
                var failure = await Assert.ThrowsAsync<AggregateException>(() => recovery);
                Assert.Contains(failure.InnerExceptions, static inner => inner is InvalidDataException);
                return;
            }

            await LedgerStoreTestExtensions.WaitUntilAsync(
                () => replicas[2].Writes.Any(static write => write.Recovery), "the recovery copies position 0 to replica 2");
            replicas[2].ReleaseWrites();
            Assert.Equal(new Slot(1), await recovery);
        }
        finally
        {
            foreach (var replica in replicas)
            {
                replica.ReleaseSeals();
            }
        }

        Assert.Contains(replicas[1].Writes, static write => write.Recovery && write.Address == Slot.Zero);
        Assert.Null(await ReadAsync(replicas[1], 0));
        Assert.Equal(2, await HoldersAsync(replicas, 0));
    }

    /// <summary>
    /// LP-I5: a recovery which meets a dropped answer, here to a read, answers <see cref="ReplicaWriteOutcome.Dropped"/>: the
    /// ledger has left the map, so no close applies. With <c>Q = 1</c> the end needs every replica's answer.
    /// </summary>
    [Fact]
    public async Task ARecoveryWhichMeetsADroppedAnswerAnswersDropped()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        replicas[1].ReadAnswer = static _ => ReplicaWriteOutcome.Dropped.Instance;
        await using var quorum = LedgerQuorumTestData.Create(replicas, 1, Window4, new FakeTimeProvider());

        DroppedAnswers.AssertDropped(await quorum.RecoverAsync(Slot.Zero, token));
    }

    /// <summary>
    /// LP-I5: a recovery whose recovery write meets a dropped answer answers <see cref="ReplicaWriteOutcome.Dropped"/>.
    /// Replica 0 alone holds position 0 and replica 2 is unreachable, so the position reaches <c>Q</c> only through
    /// replica 1, whose recovery write answers dropped.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ARecoveryWhoseRecoveryWriteMeetsADroppedAnswerAnswersDropped()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0], address: 0, contiguousTail: 0);
        replicas[1].WriteAnswer = static (_, recovery) => recovery ? ReplicaAnswers.Dropped : (ReplicaWriteOutcome?)null;
        replicas[2].Unreachable = true;
        var time = new FakeTimeProvider();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, time);

        var recovery = quorum.RecoverAsync(Slot.Zero, token).AsTask();
        await AdvanceUntilCompletedAsync(recovery, time, token);

        DroppedAnswers.AssertDropped(await recovery);
        Assert.Contains(replicas[1].Writes, static write => write.Recovery && write.Address == Slot.Zero);
        replicas[2].Unreachable = false;
    }

    /// <summary>
    /// outcomes.md §1, ledger-protocol.md §6.4: a recovery repeats a seal which answers <c>Backpressure</c>, since it
    /// asserts nothing, until the replica answers its tail. With replica 1 unreachable, the recovery needs replica 0's
    /// seal, which answers backpressure twice.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ARecoveryRepeatsASealWhichAnswersBackpressure()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0, 2], address: 0, contiguousTail: 0);
        var answers = 0;
        replicas[0].SealAnswer = () => Interlocked.Increment(ref answers) <= 2 ? new ReplicaTailRead(ReplicaWriteOutcome.Backpressure.Instance) : (ReplicaTailRead?)null;
        replicas[1].Unreachable = true;
        var time = new FakeTimeProvider();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, time);

        var recovery = quorum.RecoverEndAsync(Slot.Zero, token).AsTask();
        await AdvanceUntilCompletedAsync(recovery, time, token);

        Assert.Equal(new Slot(1), await recovery);
        Assert.Equal(3, replicas[0].SealCalls);
        Assert.Equal(new LedgerTail(Slot.Zero, IsSealed: true), (await quorum.CheckAsync(token)).Tail);
        replicas[1].Unreachable = false;
    }

    /// <summary>
    /// outcomes.md §1, ledger-protocol.md §6.4, LP-I7: a recovery repeats a read which answers <c>Backpressure</c> rather
    /// than count it as missing. Position 0 is on exactly <c>Q = 2</c> replicas, 0 and 1; replica 0 is unreachable and
    /// replica 1 answers backpressure twice, while replica 2 answers missing. A recovery which counted backpressure as
    /// missing would end at 0, below the written position; this one reads replica 1 again, finds the DATA, copies it to
    /// replica 2, and ends above it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ARecoveryRepeatsAReadWhichAnswersBackpressure()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0, 1], address: 0, contiguousTail: 0);
        var answers = 0;
        replicas[1].ReadAnswer = address =>
            address == Slot.Zero && Interlocked.Increment(ref answers) <= 2 ? new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance) : (ReplicaRead?)null;
        replicas[0].Unreachable = true;
        var time = new FakeTimeProvider();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, time);

        var recovery = quorum.RecoverEndAsync(Slot.Zero, token).AsTask();
        await AdvanceUntilCompletedAsync(recovery, time, token);

        Assert.Equal(new Slot(1), await recovery);
        Assert.Equal(3, replicas[1].ReadsOf(0));
        Assert.Equal(Value(0), await ReadAsync(replicas[2], 0));
        replicas[0].Unreachable = false;
    }

    /// <summary>
    /// outcomes.md §1, ledger-protocol.md §6.4, LP-I7: a recovery repeats a recovery write which answers
    /// <c>Backpressure</c>, which applied nothing, until the replica answers written and holds the value. Replica 0 alone
    /// holds position 0 and replica 2 is unreachable, so the position reaches <c>Q</c> only through replica 1, whose first
    /// recovery write answers backpressure.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ARecoveryRepeatsARecoveryWriteWhichAnswersBackpressure()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0], address: 0, contiguousTail: 0);
        var answers = 0;
        replicas[1].WriteAnswer = (_, recovery) => recovery && Interlocked.Increment(ref answers) == 1 ? ReplicaAnswers.Backpressure : (ReplicaWriteOutcome?)null;
        replicas[2].Unreachable = true;
        var time = new FakeTimeProvider();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, time);

        var recovery = quorum.RecoverEndAsync(Slot.Zero, token).AsTask();
        await AdvanceUntilCompletedAsync(recovery, time, token);

        Assert.Equal(new Slot(1), await recovery);
        Assert.Equal(2, replicas[1].Writes.Count(static write => write.Recovery && write.Address == Slot.Zero));
        Assert.Equal(Value(0), await ReadAsync(replicas[1], 0));
        replicas[2].Unreachable = false;
    }

    /// <summary>
    /// ledger-protocol.md §8.3, outcomes.md §1: a recovery's replica operation is repeated with backoff on the recovery's
    /// clock while it answers <c>Backpressure</c> or fails transiently, and stops at any other answer, and at a failure
    /// repetition cannot change, which it reports unchanged.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AReplicaOperationIsRepeatedWhileItAnswersBackpressureOrFailsTransiently()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var retry = ReplicaRetry.Create<ReplicaWriteOutcome>(time, static answer => answer is ReplicaWriteOutcome.Backpressure);
        var attempts = 0;
        var repeated = retry.ExecuteAsync(
            _ => Interlocked.Increment(ref attempts) switch
            {
                1 => ValueTask.FromResult(ReplicaAnswers.Backpressure),
                2 => ValueTask.FromException<ReplicaWriteOutcome>(new IOException("The replica is unreachable.")),
                _ => ValueTask.FromResult(ReplicaAnswers.Sealed),
            },
            token).AsTask();
        await AdvanceUntilCompletedAsync(repeated, time, token);

        Assert.Equal(ReplicaWriteCase.Sealed, (await repeated).Case);
        Assert.Equal(3, attempts);

        var permanent = new ObjectDisposedException("replica", "The replica's quorum was disposed.");
        var failures = 0;
        var failed = retry.ExecuteAsync(
            _ =>
            {
                failures++;
                return ValueTask.FromException<ReplicaWriteOutcome>(permanent);
            },
            token).AsTask();

        Assert.Same(permanent, await Assert.ThrowsAsync<ObjectDisposedException>(() => failed));
        Assert.Equal(1, failures);
    }

    /// <summary>
    /// A replica whose failure repetition cannot change takes no further part, so a position fewer than
    /// <c>N − Q + 1</c> remaining replicas can report missing, and none reports DATA, is undecidable, and the recovery
    /// fails with <see cref="AggregateException"/> holding those failures.
    /// </summary>
    [Fact]
    public async Task NonTransientFailuresWhichLeaveAPositionUndecidableFailTheRecovery()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        replicas[1].ReadFailure = static _ => new ObjectDisposedException("replica", "The replica's quorum was disposed.");
        replicas[2].ReadFailure = static _ => new ObjectDisposedException("replica", "The replica's quorum was disposed.");
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, new FakeTimeProvider());

        var failure = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.RecoverAsync(Slot.Zero, token));
        Assert.Contains(failure.InnerExceptions, static inner => inner is ObjectDisposedException);
    }

    /// <summary>
    /// LR-I2: a corrupt answer is neither DATA nor missing, so it never counts toward the end: with <c>Q = 1</c> the end
    /// needs all three replicas missing, and a corrupt one leaves the position undecidable.
    /// </summary>
    [Fact]
    public async Task ACorruptAnswerNeverCountsAsMissing()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        replicas[1].ReadFailure = static address => address.Value == 0 ? new LedgerReplicaCorruptException("The slot is faulty.") : null;
        await using var quorum = LedgerQuorumTestData.Create(replicas, 1, Window4, new FakeTimeProvider());

        _ = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.RecoverAsync(Slot.Zero, token));
    }

    /// <summary>
    /// A recovery copies DATA it found to a replica which answered corrupt there too, as a recovery write replaces a
    /// faulty value (LR-I2, LR-I5); the corrupt answer counts toward neither the value nor the end.
    /// </summary>
    [Fact]
    public async Task ARecoveryCopiesFoundDataToACorruptReplica()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        await WriteAsync(replicas, [0], address: 0, contiguousTail: 0);
        replicas[1].ReadFailure = static address => address.Value == 0 ? new LedgerReplicaCorruptException("The slot is faulty.") : null;
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, new FakeTimeProvider());

        Assert.Equal(new Slot(1), await quorum.RecoverEndAsync(Slot.Zero, token));
        Assert.Contains(replicas[1].Writes, static write => write.Recovery && write.Address == Slot.Zero);
        replicas[1].ReadFailure = null;
        Assert.Equal(3, await HoldersAsync(replicas, 0));
    }

    /// <summary>
    /// Regression test for #395, LP-I7, LR-I2: a batch which fails the quorum's batch verifier during recovery counts as
    /// corrupt and is never copied to another replica, nor is its replica counted as a recovery-write target (LR-I5),
    /// while a valid copy from another replica succeeds and is copied to a replica which lacks the slot.
    /// </summary>
    [Fact]
    public async Task ABatchWhichFailsVerificationIsNotCopiedAndAnotherReplicasCopyIsCopied()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        var damaged = new byte[] { 0xFF, 0xEE };
        var valid = Value(0);
        using (var damagedPayload = LedgerReplicaTestData.Buffer(damaged))
        {
            Assert.Equal(ReplicaAnswers.Written, await replicas[0].WriteAsync(Slot.Zero, damagedPayload, Slot.Zero, recovery: false, token));
        }

        await WriteAsync(replicas, [1], address: 0, contiguousTail: 0);
        // Hold replica 1's read until recovery has observed and rejected replica 0's damaged batch, so recovery always
        // evaluates the unverified copy first rather than racing replica 1's valid copy.
        replicas[1].HoldReads();
        var steps = new List<RecoveryStep>();
        await using var quorum = LedgerQuorumTestData.Create(
            replicas,
            2,
            Window4,
            new FakeTimeProvider(),
            recoveryObserver: step =>
            {
                steps.Add(step);
                if (step is { Kind: RecoveryStepKind.Read, Replica: 0, Address: 0 })
                {
                    replicas[1].ReleaseReads();
                }
            },
            verifyBatch: batch => batch.ToArray().SequenceEqual(valid));

        Assert.Equal(new Slot(1), await quorum.RecoverEndAsync(Slot.Zero, token));

        Assert.Contains(steps, static step => step is { Kind: RecoveryStepKind.Read, Replica: 0, Address: 0, Answer: null, Failure: LedgerReplicaCorruptException });
        Assert.DoesNotContain(replicas[0].Writes, static write => write.Recovery && write.Address == Slot.Zero);
        var copy = Assert.Single(replicas[2].Writes, static write => write.Recovery && write.Address == Slot.Zero);
        Assert.Equal(valid, copy.Payload);
        Assert.Equal(valid, await ReadAsync(replicas[1], 0));
        Assert.Equal(valid, await ReadAsync(replicas[2], 0));
    }

    /// <summary>
    /// Regression test for #395, LP-I7, LR-I2: when every available copy of a slot fails the quorum's batch verifier,
    /// recovery treats each rejected batch as corrupt (neither DATA nor missing), fails with
    /// <see cref="AggregateException"/> carrying <see cref="LedgerReplicaCorruptException"/>, and sends no recovery
    /// writes.
    /// </summary>
    [Theory]
    [InlineData(2, 3)]
    [InlineData(1, 1)]
    public async Task WhenAllAvailableCopiesFailVerificationRecoveryFailsAndCopiesNothing(int quorumSize, int badReplicaCount)
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        for (var index = 0; index < badReplicaCount; index++)
        {
            using var damaged = LedgerReplicaTestData.Buffer([0xFA, (byte)index]);
            Assert.Equal(ReplicaAnswers.Written, await replicas[index].WriteAsync(Slot.Zero, damaged, Slot.Zero, recovery: false, token));
        }

        await using var quorum = LedgerQuorumTestData.Create(
            replicas,
            quorumSize,
            Window4,
            new FakeTimeProvider(),
            verifyBatch: static _ => false);

        var failure = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.RecoverAsync(Slot.Zero, token));

        Assert.NotEmpty(failure.InnerExceptions);
        Assert.All(failure.InnerExceptions, static inner => Assert.IsType<LedgerReplicaCorruptException>(inner));
        Assert.All(replicas, static replica => Assert.DoesNotContain(replica.Writes, static write => write.Recovery));
    }

    /// <summary>
    /// Regression test for #395, LP-I7, LR-I5: when two replicas return batches rejected by the batch verifier and only
    /// one replica holds a valid copy in a quorum of two, recovery cannot place the valid batch on <c>Q</c> replicas
    /// without counting a rejected replica whose intact slot a recovery write would keep, so it fails and copies nothing.
    /// </summary>
    [Fact]
    public async Task ARecoveryWhichCannotReachQDueToVerificationFailuresFailsAndCopiesNothing()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        var valid = Value(0);
        using (var damaged = LedgerReplicaTestData.Buffer([0xDE, 0xAD]))
        {
            Assert.Equal(ReplicaAnswers.Written, await replicas[0].WriteAsync(Slot.Zero, damaged, Slot.Zero, recovery: false, token));
            Assert.Equal(ReplicaAnswers.Written, await replicas[1].WriteAsync(Slot.Zero, damaged, Slot.Zero, recovery: false, token));
        }

        await WriteAsync(replicas, [2], address: 0, contiguousTail: 0);
        // Hold replica 2's valid read until both unverified reads have been observed and rejected.
        replicas[2].HoldReads();
        var rejected = 0;
        await using var quorum = LedgerQuorumTestData.Create(
            replicas,
            2,
            Window4,
            new FakeTimeProvider(),
            recoveryObserver: step =>
            {
                if (step is { Kind: RecoveryStepKind.Read, Address: 0, Replica: 0 or 1 } && ++rejected == 2)
                {
                    replicas[2].ReleaseReads();
                }
            },
            verifyBatch: batch => batch.ToArray().SequenceEqual(valid));

        var failure = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.RecoverAsync(Slot.Zero, token));

        Assert.NotEmpty(failure.InnerExceptions);
        Assert.All(failure.InnerExceptions, static inner => Assert.IsType<LedgerReplicaCorruptException>(inner));
        Assert.All(replicas, static replica => Assert.DoesNotContain(replica.Writes, static write => write.Recovery));
    }

    /// <summary>
    /// Cancelling a recovery stops its wait while its seals are held; the seals it sent may still land, and disposal
    /// waits for them.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CancellationStopsTheRecoverysWait()
    {
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Replicas(nodes, 3, Window4);
        foreach (var replica in replicas)
        {
            replica.HoldSeals();
        }

        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window4, new FakeTimeProvider());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var recovery = quorum.RecoverAsync(Slot.Zero, cancellation.Token).AsTask();
        await LedgerStoreTestExtensions.WaitUntilAsync(() => replicas.All(static replica => replica.SealCalls == 1), "every seal is held");
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        foreach (var replica in replicas)
        {
            replica.ReleaseSeals();
        }
    }

    /// <summary>
    /// The scenarios of <see cref="ARecoveryEndsAboveEveryAcknowledgedWriteWithEveryLowerPositionOnQ"/>.
    /// </summary>
    private static readonly Gen<Scenario> Scenarios =
        Gen.Int[1, 5].SelectMany(static replicas => Gen.Int[1, replicas].SelectMany(quorum => Gen.Select(
                Gen.Int[1, 3],
                Gen.Select(Gen.Int[0, 2], Gen.Int[0, (1 << replicas) - 1]).Array[0, 12],
                Gen.Int[0, (1 << replicas) - 1],
                Gen.Int[0, 2].Array[0, 6],
                Gen.Frequency((1, Gen.Const(0L)), (1, Gen.Long[0, 12])))
            .Select((window, history, unreachable, racing, known) =>
                new Scenario(replicas, quorum, window, history, Unreachable(replicas, quorum, unreachable), racing, known))));

    private static async Task CheckAsync(Scenario scenario)
    {
        var token = TestContext.Current.CancellationToken;
        var (n, q, k) = (scenario.ReplicaCount, scenario.Quorum, scenario.Window);
        var options = new LedgerReplicaOptions { WindowSize = k };
        using var nodes = new InMemoryLedgerReplicaTransport();
        var nodeIds = Enumerable.Range(0, n).Select(static _ => NodeId.New()).ToArray();
        ILedgerReplica Open(int replica) => nodes.Open(nodeIds[replica], LedgerReplicaTestData.Ledger, options);

        // The writer's history before the recovery: each write lies within the window above the writer's contiguous
        // tail, carries it, and reaches some replicas; Q of them acknowledge it.
        var direct = Enumerable.Range(0, n).Select(Open).ToArray();
        var holders = new Dictionary<long, int>();
        var acknowledged = new HashSet<long>();
        var tail = 0L;
        foreach (var (offset, mask) in scenario.History)
        {
            var address = tail + offset % k;
            for (var replica = 0; replica < n; replica++)
            {
                if ((mask & (1 << replica)) != 0)
                {
                    using var payload = LedgerReplicaTestData.Buffer(Value(address));
                    Assert.Equal(ReplicaAnswers.Written, await direct[replica].WriteAsync(new Slot(address), payload, new Slot(tail), recovery: false, token));
                }
            }

            holders[address] = await HoldersAsync(direct, address);
            if (holders[address] >= q)
            {
                _ = acknowledged.Add(address);
            }

            while (acknowledged.Contains(tail))
            {
                tail++;
            }
        }

        // The contiguous tail each replica the recovery can reach retained before it; the racing writer may raise them.
        var reachable = Enumerable.Range(0, n).Where(replica => !scenario.Unavailable.Contains(replica)).ToArray();
        var retainedBefore = new List<long>();
        foreach (var replica in reachable)
        {
            retainedBefore.Add((await direct[replica].ReadTailAsync(Slot.Zero, seal: false, token)).Tail.ContiguousTail.Value);
        }

        retainedBefore.Sort();
        var known = new Slot(scenario.Known % (tail + 1));
        var controlled = Enumerable.Range(0, n).Select(replica => new ControlledLedgerReplica(Open(replica)) { Unreachable = scenario.Unavailable.Contains(replica) }).ToArray();
        Slot end;
        List<(long Address, long Tail, LedgerWriteOutcome Result)> raced;
        var steps = new List<RecoveryStep>();
        await using (var recovery = LedgerQuorumTestData.Create(controlled, q, options, new FakeTimeProvider(), recoveryObserver: steps.Add))
        await using (var writer = LedgerQuorumTestData.Create([.. Enumerable.Range(0, n).Select(Open)], q, options, new FakeTimeProvider()))
        {
            var racing = Task.Run(() => RaceAsync(writer, scenario.Racing, k, tail, acknowledged, token), token);
            var recovered = await recovery.RecoverAsync(known, token);
            end = recovered.Recovery.End;
            raced = await racing;
            CheckSteps(steps, controlled, end, q);

            // LP-I6: c is the greatest of the carried tail and N − Q + 1 seal answers, each at least its replica's tail
            // before the recovery and at most the greatest tail a caller ever sent it.
            var c = recovered.Recovery.ContiguousTail;
            var highest = Math.Max(Math.Max(retainedBefore[^1], known.Value), raced.Select(static write => write.Tail).DefaultIfEmpty(0).Max());
            Assert.True(c >= known, string.Create(CultureInfo.InvariantCulture, $"The recovery read from {c}, below the carried tail {known}."));
            Assert.True(
                c.Value >= retainedBefore[n - q],
                string.Create(CultureInfo.InvariantCulture, $"The recovery read from {c}, below the {n - q + 1}-th smallest tail the reachable replicas retained, [{string.Join(", ", retainedBefore)}]."));
            Assert.True(c.Value <= highest, string.Create(CultureInfo.InvariantCulture, $"The recovery read from {c}, above every tail a replica could report ({highest})."));
            Assert.All(controlled.SelectMany(static replica => replica.Reads), read => Assert.Equal(c, read.ContiguousTail));

            // LP-I6, exactly: c is the greatest of the carried tail and the tails of the first N − Q + 1 seal answers the
            // recovery handled, in the order its observer reports them.
            var firstSeals = steps
                .Select(static step => step is { Kind: RecoveryStepKind.Seal, Tail: { } tail } ? tail.ContiguousTail.Value : (long?)null)
                .OfType<long>()
                .Take(n - q + 1)
                .ToList();
            Assert.Equal(n - q + 1, firstSeals.Count);
            Assert.True(
                c.Value == Math.Max(known.Value, firstSeals.Max()),
                string.Create(CultureInfo.InvariantCulture, $"The recovery read from {c}, not the greatest of the carried tail {known} and its first {n - q + 1} seal answers' tails, [{string.Join(", ", firstSeals)}]."));

            // Every position below the end holds its one value on Q replicas directly.
            for (var address = 0L; address < end.Value; address++)
            {
                Assert.True(await HoldersAsync(direct, address) >= q, string.Create(CultureInfo.InvariantCulture, $"Position {address} lies below the end {end} on fewer than {q} replicas."));
            }

            // Every recovery write copies a position's one value below the end, carrying the one contiguous tail c the
            // recovery read from.
            var copies = controlled.SelectMany(static replica => replica.Writes).ToArray();
            Assert.All(copies, write =>
            {
                Assert.True(write.Recovery);
                Assert.True(write.Address < end, string.Create(CultureInfo.InvariantCulture, $"A recovery write at {write.Address} lies at or above the end {end}."));
                Assert.Equal(Value(write.Address.Value), write.Payload);
                Assert.Equal(c, write.ContiguousTail);
            });
            Assert.True(c <= end);
        }

        // LP-I4, LP-I7: no acknowledged prefix extends past the end, and no writer write at the end was written.
        var written = new HashSet<long>(acknowledged.Concat(raced.Where(static write => write.Result is ReplicaWriteOutcome.Written).Select(static write => write.Address)));
        var contiguous = 0L;
        while (written.Contains(contiguous))
        {
            contiguous++;
        }

        Assert.True(contiguous <= end.Value, string.Create(CultureInfo.InvariantCulture, $"The acknowledged prefix ends at {contiguous}, above the end {end}."));
        Assert.DoesNotContain(raced, write => write.Address == end.Value && write.Result is ReplicaWriteOutcome.Written);

        // A recovery after this one, over every replica, ends no lower.
        await using var again = LedgerQuorumTestData.Create([.. Enumerable.Range(0, n).Select(Open)], q, options, new FakeTimeProvider());
        Assert.True(await again.RecoverEndAsync(Slot.Zero, token) >= end);
    }

    /// <summary>
    /// Checks the answers a recovery's observer reported against the replica operations the recovery sent: each answer or
    /// failure at most once per operation the replica received, where a read or recovery write the recovery stopped may
    /// never have reached it; an answered seal with its tail, an answered read with its kind, and
    /// a read counted as sent after its replica's seal answer only after the observer reported that seal answer (LP-I7);
    /// at least <c>N − Q + 1</c> replicas' missing answers to reads sent after their seal answers at the end; and every
    /// recovery write below the end.
    /// </summary>
    private static void CheckSteps(List<RecoveryStep> steps, ControlledLedgerReplica[] replicas, Slot end, int quorum)
    {
        var sealedReplicas = new HashSet<int>();
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case RecoveryStepKind.Seal:
                    Assert.Equal(step.Failure is null, step.Tail is not null);
                    if (step.Tail is not null)
                    {
                        _ = sealedReplicas.Add(step.Replica);
                    }

                    break;
                case RecoveryStepKind.Read:
                    Assert.Equal(step.Failure is null && !step.Stopped, step.Answer is not null);
                    Assert.Null(step.Tail);
                    Assert.True(
                        !step.AfterSeal || sealedReplicas.Contains(step.Replica),
                        string.Create(CultureInfo.InvariantCulture, $"Replica {step.Replica}'s read of {step.Address} counts as sent after a seal answer the observer had not reported."));
                    break;
                case RecoveryStepKind.RecoveryWrite:
                    Assert.True(step.Address < end.Value, string.Create(CultureInfo.InvariantCulture, $"A recovery write at {step.Address} lies at or above the end {end}."));
                    break;
                default:
                    Assert.Fail($"The observer reported a step of no kind: {step}.");
                    break;
            }
        }

        var deciding = steps
            .Where(step => step is { Kind: RecoveryStepKind.Read, Answer: RecoveryAnswer.Missing, AfterSeal: true } && step.Address == end.Value)
            .Select(static step => step.Replica)
            .Distinct()
            .Count();
        Assert.True(
            deciding >= replicas.Length - quorum + 1,
            string.Create(CultureInfo.InvariantCulture, $"Only {deciding} replicas' answers after their seals report the end {end} missing."));
        for (var replica = 0; replica < replicas.Length; replica++)
        {
            var mine = steps.Where(step => step.Replica == replica).ToList();
            Assert.InRange(mine.Count(static step => step.Kind == RecoveryStepKind.Seal), 0, replicas[replica].SealCalls);
            foreach (var reads in mine.Where(static step => step is { Kind: RecoveryStepKind.Read, Stopped: false }).GroupBy(static step => step.Address!.Value))
            {
                Assert.InRange(reads.Count(), 1, replicas[replica].ReadsOf(reads.Key));
            }

            var received = replicas[replica].Writes;
            foreach (var copies in mine.Where(static step => step is { Kind: RecoveryStepKind.RecoveryWrite, Failure: null, Stopped: false }).GroupBy(static step => step.Address!.Value))
            {
                Assert.InRange(copies.Count(), 1, received.Count(write => write.Recovery && write.Address.Value == copies.Key));
            }
        }
    }

    /// <summary>
    /// Writes as the ledger's writer, within the window above the contiguous tail it knows, which each write carries,
    /// until a write reports sealed.
    /// </summary>
    private static async Task<List<(long Address, long Tail, LedgerWriteOutcome Result)>> RaceAsync(
        LedgerQuorum writer, int[] offsets, int window, long tail, HashSet<long> acknowledged, CancellationToken cancellationToken)
    {
        var known = new HashSet<long>(acknowledged);
        var results = new List<(long Address, long Tail, LedgerWriteOutcome Result)>();
        foreach (var offset in offsets)
        {
            var address = tail + offset % window;
            using var payload = LedgerReplicaTestData.Buffer(Value(address));
            var result = await writer.WriteAsync(new Slot(address), payload, new Slot(tail), cancellationToken);
            results.Add((address, tail, result));
            if (result is ReplicaWriteOutcome.Sealed)
            {
                break;
            }

            _ = known.Add(address);
            while (known.Contains(tail))
            {
                tail++;
            }
        }

        return results;
    }

    /// <summary>
    /// Returns the replicas of <paramref name="mask"/> a recovery may find unreachable and still end: at most
    /// <c>N − max(Q, N − Q + 1)</c>, the lowest first.
    /// </summary>
    private static int[] Unreachable(int replicas, int quorum, int mask) =>
        [.. Enumerable.Range(0, replicas).Where(replica => (mask & (1 << replica)) != 0).Take(replicas - Math.Max(quorum, replicas - quorum + 1))];

    /// <summary>
    /// Advances <paramref name="time"/> until <paramref name="task"/> completes, so the backoff between a recovery's
    /// repetitions passes.
    /// </summary>
    private static async Task AdvanceUntilCompletedAsync(Task task, FakeTimeProvider time, CancellationToken cancellationToken)
    {
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            _ = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, cancellationToken));
        }
    }

    private static ControlledLedgerReplica[] Replicas(InMemoryLedgerReplicaTransport nodes, int count, LedgerReplicaOptions options) =>
        [.. Enumerable.Range(0, count).Select(_ => new ControlledLedgerReplica(nodes.Open(NodeId.New(), LedgerReplicaTestData.Ledger, options)))];

    /// <summary>
    /// Writes the value of <paramref name="address"/> to the replicas of <paramref name="indexes"/> directly, as the
    /// writer's write carrying <paramref name="contiguousTail"/> which reached only them.
    /// </summary>
    private static async Task WriteAsync(ILedgerReplica[] replicas, int[] indexes, long address, long contiguousTail)
    {
        using var payload = LedgerReplicaTestData.Buffer(Value(address));
        foreach (var index in indexes)
        {
            Assert.Equal(
                ReplicaAnswers.Written,
                await replicas[index].WriteAsync(new Slot(address), payload, new Slot(contiguousTail), recovery: false, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// Writes the value of each position below <paramref name="positions"/> to two of the three replicas directly, a
    /// different pair at each, carrying no contiguous tail.
    /// </summary>
    private static async Task WriteQuorumsAsync(ILedgerReplica[] replicas, int positions)
    {
        for (var address = 0; address < positions; address++)
        {
            await WriteAsync(replicas, [address % 3, (address + 1) % 3], address, contiguousTail: 0);
        }
    }

    /// <summary>
    /// Advances <paramref name="time"/> by the longest backoff until <paramref name="task"/> completes, so that every
    /// replica operation the recovery repeats is sent again, and fails if it has not completed after 400 advances.
    /// </summary>
    private static async Task<T> AdvanceUntilAsync<T>(FakeTimeProvider time, Task<T> task)
    {
        for (var advance = 0; advance < 400 && !task.IsCompleted; advance++)
        {
            time.Advance(ReplicaRetry.MaximumDelay);
            _ = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken));
        }

        Assert.True(task.IsCompleted, "The recovery did not end although every replica it needs answers.");
        return await task;
    }

    /// <summary>
    /// Returns how many replicas hold the value of <paramref name="address"/>, failing on any other value.
    /// </summary>
    private static async Task<int> HoldersAsync(IEnumerable<ILedgerReplica> replicas, long address)
    {
        var holders = 0;
        foreach (var replica in replicas)
        {
            if (await ReadAsync(replica, address) is { } value)
            {
                Assert.Equal(Value(address), value);
                holders++;
            }
        }

        return holders;
    }

    private static Task<byte[]?> ReadAsync(ILedgerReplica replica, long address) =>
        LedgerReplicaTestData.ReadBytesAsync(replica, address, TestContext.Current.CancellationToken);

    /// <summary>
    /// Returns the one value the ledger's writer writes at <paramref name="address"/>.
    /// </summary>
    private static byte[] Value(long address) => [(byte)(address + 1), (byte)(address * 7 + 3)];

    /// <summary>
    /// A recovery scenario: <c>N</c>, <c>Q</c>, and <c>K</c>; the writer's history before the recovery, each write an
    /// offset into its window and the replicas it reached; the replicas the recovery cannot reach; the offsets the writer
    /// racing the recovery writes at; and the known contiguous tail the recovery is given, reduced modulo one more than
    /// the acknowledged contiguous tail, which bounds it (ledger-replicas.md §3.2).
    /// </summary>
    private sealed record Scenario(int ReplicaCount, int Quorum, int Window, (int Offset, int Mask)[] History, int[] Unavailable, int[] Racing, long Known)
    {
        public override string ToString()
        {
            var history = string.Join(", ", History.Select(static write => string.Create(CultureInfo.InvariantCulture, $"+{write.Offset} to {write.Mask:b}")));
            return string.Create(
                CultureInfo.InvariantCulture,
                $"N = {ReplicaCount}, Q = {Quorum}, K = {Window}; history [{history}]; unreachable [{string.Join(", ", Unavailable)}]; racing [{string.Join(", ", Racing)}]; known {Known}");
        }
    }
}

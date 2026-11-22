using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks that a <see cref="LedgerQuorum"/> releases every buffer its operations and recoveries leave in flight, and
/// that disposal waits for operations in flight and the replica operations they leave running before it disposes the
/// replicas they use (ledger-protocol.md §6.3, §8.3; BUF-I1, BUF-I2). Each replica is gated, so the test decides when a
/// straggler answers.
/// </summary>
public sealed class LedgerQuorumDisposalTests : IDisposable
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 16 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A write in flight when disposal starts completes, later operations fail with
    /// <see cref="ObjectDisposedException"/>, and the replicas are disposed only once the write is done with them.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DisposalWaitsForAnOperationInFlightBeforeDisposingTheReplicas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var replicas = Replicas.Create();
        replicas.HoldAll();
        Task disposal;
        using (var payload = Payload(0))
        {
            var write = replicas.Replica.WriteAsync(Slot.Zero, payload, cancellationToken).AsTask();
            disposal = replicas.Replica.DisposeAsync().AsTask();
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await replicas.Replica.ReadAsync(Slot.Zero, cancellationToken));
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await replicas.Replica.RecoverAsync(Slot.Zero, cancellationToken));
            Assert.False(disposal.IsCompleted);
            Assert.All(replicas.Gated, static replica => Assert.False(replica.IsDisposed));

            replicas.ReleaseAll();
            Assert.Equal(ReplicaWriteCase.Written, (await write).Case);
        }

        await disposal;
        Assert.All(replicas.Gated, static replica => Assert.True(replica.IsDisposed && !replica.UsedAfterDisposal));
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// A seal check's replica calls run under one token, the caller's linked to the quorum's lifetime or, when the
    /// caller's cannot be cancelled, the lifetime's, which serves the stragglers the check leaves: once they end, the token
    /// follows the caller's no more, its linked source released; and while one runs, a stop of the quorum's reads reaches
    /// it (ledger-protocol.md §6.3).
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASealChecksTokenServesItsStragglersUntilTheyEndAndIsReleasedThen(bool cancellable)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Func<CancellationToken, ValueTask<ReplicaTailRead>>? straggler = null;
        await using var quorum = new LedgerQuorum(
            Enumerable.Range(0, 3).Select(index => new DelegatingLedgerReplica
            {
                ReadTail = (tail, _, token) => index == 2
                    ? straggler!(token)
                    : ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(tail, IsSealed: false)),
            }),
            quorum: 2,
            Window,
            TimeProvider.System);

        // Answered once the check returned: its continuations run as the answer is set, so the source is released then.
        using (var caller = new CancellationTokenSource())
        {
            var answer = new TaskCompletionSource<ReplicaTailRead>();
            var held = default(CancellationToken);
            straggler = token =>
            {
                held = token;
                return new ValueTask<ReplicaTailRead>(answer.Task);
            };
            var tail = await quorum.CheckAsync(Slot.Zero, cancellable ? caller.Token : CancellationToken.None);

            Assert.False(tail.Value is LedgerTail { IsSealed: true });
            answer.SetResult(new LedgerReplicaTail(Slot.Zero, IsSealed: false));
            await caller.CancelAsync();
            Assert.False(held.IsCancellationRequested);
        }

        // Held until its token is cancelled: the stop of the quorum's reads reaches it after the check returned.
        using (var caller = new CancellationTokenSource())
        {
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            straggler = async token =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, token);
                }
                finally
                {
                    _ = stopped.TrySetResult();
                }

                throw new UnreachableException();
            };
            _ = await quorum.CheckAsync(Slot.Zero, cancellable ? caller.Token : CancellationToken.None);
            Assert.False(stopped.Task.IsCompleted);

            await quorum.StopReadsAsync();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, cancellationToken);
        }
    }

    /// <summary>
    /// ledger-protocol.md §6.3, §8.3: a stop of the quorum's reads ends a decided read held at its preferred replica, the
    /// replica's call among them, before the hedge delay or after a hedge, under a caller's token which can and cannot be
    /// cancelled, and of a ledger whose one replica is the preferred one, which the stop's ending its call leaves no other
    /// replica to ask: the read fails with <see cref="ObjectDisposedException"/>, not as a replica's failure, and the
    /// quorum's disposal, which waits for every replica call, completes.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(3, false, false)]
    [InlineData(3, false, true)]
    [InlineData(3, true, false)]
    [InlineData(3, true, true)]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    public async Task AStopEndsADecidedReadHeldAtItsPreferredReplicaAndTheDisposalCompletes(int replicas, bool afterHedge, bool cancellable)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var started = Enumerable.Range(0, replicas).Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var quorum = new LedgerQuorum(
            Enumerable.Range(0, replicas).Select(index => new DelegatingLedgerReplica
            {
                Read = async (_, _, token) =>
                {
                    _ = started[index].TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, token);
                    throw new UnreachableException();
                },
            }),
            quorum: replicas / 2 + 1,
            Window,
            time,
            preferredReplica: 0);
        using var caller = new CancellationTokenSource();

        var read = quorum.ReadAsync(Slot.Zero, new Slot(1), cancellable ? caller.Token : CancellationToken.None).AsTask();
        await started[0].Task.WaitAsync(cancellationToken);
        if (afterHedge)
        {
            time.Advance(LedgerQuorum.ReadHedgeDelay);
            await started[1].Task.WaitAsync(cancellationToken);
        }

        await quorum.StopReadsAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, cancellationToken));
        await quorum.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, cancellationToken);
    }

    /// <summary>
    /// ledger-protocol.md §8.3: a stop which ends a hedged read's preferred call before it cancels the read's linked token
    /// is no replica's failure. By construction: the preferred call registers on the lifetime after the hedge linked the
    /// read's token to it, so the lifetime's cancellation, which runs the newest registration first, completes the call
    /// inline while the linked token stands; the hedged replica answered <c>Missing</c>, so no replica is left to ask, and
    /// the read answers <see cref="ObjectDisposedException"/>, not that its quorum was unreachable.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStopWhichEndsAHedgedReadsPreferredCallFirstAnswersDisposed(bool cancellable)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var preferred = new TaskCompletionSource<ReplicaRead>();
        var preferredToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var quorum = new LedgerQuorum(
            Enumerable.Range(0, 2).Select(index => new DelegatingLedgerReplica
            {
                Read = (_, _, token) =>
                {
                    if (index == 0)
                    {
                        _ = preferredToken.TrySetResult(token);
                        return new ValueTask<ReplicaRead>(preferred.Task);
                    }

                    _ = hedged.TrySetResult();
                    return ValueTask.FromResult(ReplicaAnswers.Missing);
                },
            }),
            quorum: 2,
            Window,
            time,
            preferredReplica: 0);
        using var caller = new CancellationTokenSource();

        var read = quorum.ReadAsync(Slot.Zero, new Slot(1), cancellable ? caller.Token : CancellationToken.None).AsTask();
        var lifetime = await preferredToken.Task.WaitAsync(cancellationToken);
        time.Advance(LedgerQuorum.ReadHedgeDelay);
        await hedged.Task.WaitAsync(cancellationToken);
        using var registration = lifetime.Register(() => preferred.TrySetCanceled(lifetime));

        await quorum.StopReadsAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, cancellationToken));
    }

    /// <summary>
    /// ledger-protocol.md §8.3: a decided read calls its preferred replica under one token, the quorum's lifetime, for
    /// every read, so a read which the preferred replica answers creates no token source; the caller's cancellation does
    /// not reach the call, whose wait alone it ends, and a stop of the quorum's reads does.
    /// </summary>
    [Fact]
    public async Task APreferredReplicaReadsUnderTheQuorumsLifetimeForEveryRead()
    {
        var tokens = new List<CancellationToken>();
        await using var quorum = new LedgerQuorum(
            Enumerable.Range(0, 3).Select(index => new DelegatingLedgerReplica
            {
                Read = (_, _, token) =>
                {
                    tokens.Add(token);
                    return ValueTask.FromResult(new ReplicaRead(Payload(0)));
                },
            }),
            quorum: 2,
            Window,
            new FakeTimeProvider(),
            preferredReplica: 0);
        using var caller = new CancellationTokenSource();

        foreach (var token in new[] { caller.Token, CancellationToken.None, caller.Token })
        {
            var read = await quorum.ReadAsync(Slot.Zero, new Slot(1), token);
            read.Batch.Dispose();
        }

        Assert.Equal(3, tokens.Count);
        Assert.All(tokens, token => Assert.Equal(tokens[0], token));
        await caller.CancelAsync();
        Assert.False(tokens[0].IsCancellationRequested);
        await quorum.StopReadsAsync();
        Assert.True(tokens[0].IsCancellationRequested);
    }

    /// <summary>
    /// A read returns at the first DATA; the DATA of a straggler which answers afterwards is released.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AReadReleasesTheDataOfAReplicaWhichAnswersAfterIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var replicas = Replicas.Create();
        await replicas.WriteAsync([0, 1, 2], slot: 0);
        replicas.Gated[2].Hold();

        var read = await replicas.Replica.ReadAsync(Slot.Zero, cancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, read.Case);
        read.Batch.Dispose();

        await replicas.Gated[2].ReadStarted;
        replicas.Gated[2].Release();
        await replicas.Replica.DisposeAsync();
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// Disposal waits for a straggler which answers after its read returned, and disposes the replicas only then.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DisposalWaitsForAStragglerBeforeDisposingTheReplicas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var replicas = Replicas.Create();
        await replicas.WriteAsync([0, 1, 2], slot: 0);
        replicas.Gated[2].Hold();
        var read = await replicas.Replica.ReadAsync(Slot.Zero, cancellationToken);
        read.Batch.Dispose();
        await replicas.Gated[2].ReadStarted;

        var disposal = replicas.Replica.DisposeAsync().AsTask();
        Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken)));
        Assert.All(replicas.Gated, static replica => Assert.False(replica.IsDisposed));

        replicas.ReleaseAll();
        await disposal;
        Assert.All(replicas.Gated, static replica => Assert.True(replica.IsDisposed && !replica.UsedAfterDisposal));
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// Regression test (#337), ledger-protocol.md §6.3: disposal cancels the replica writes it would otherwise wait for,
    /// held at a callee which never answers until its caller cancels: a straggler of a write already decided, or every
    /// call of an admitted write, which then fails with <see cref="ObjectDisposedException"/> and reports nothing written.
    /// The disposal ends at once, every held call saw its cancellation, and each request still lands once its callee runs
    /// it, as a request already sent may.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalCancelsTheWritesItWaitsForAndEndsWhileTheirRequestsStillLand(bool admitted)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var callees = Enumerable.Range(0, 3).Select(static _ => new CalleeHeldLedgerReplica(new InMemoryLedgerReplica(Window))).ToArray();
        var held = admitted ? callees : [callees[2]];
        foreach (var callee in held)
        {
            callee.Hold();
        }

        var quorum = LedgerQuorumTestData.Create(callees, 2, Window);
        Task<LedgerWriteOutcome> write;
        using (var payload = Payload(0))
        {
            write = quorum.WriteAsync(Slot.Zero, payload, cancellationToken).AsTask();
            await Task.WhenAll(held.Select(static callee => callee.Sent)).WaitAsync(cancellationToken);
            if (!admitted)
            {
                Assert.Equal(ReplicaWriteCase.Written, (await write).Case);
            }

            Assert.All(held, static callee => Assert.False(callee.Token.IsCancellationRequested));
            await quorum.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, cancellationToken);
        }

        Assert.All(held, static callee => Assert.True(callee.Token.IsCancellationRequested));
        Assert.All(callees, static callee => Assert.True(callee.IsDisposed));
        if (admitted)
        {
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => write);
        }

        foreach (var callee in held)
        {
            Assert.Equal(ReplicaAnswers.Written, await callee.LandAsync());
            Assert.Equal([1], await LedgerReplicaTestData.ReadBytesAsync(callee.Inner, 0, cancellationToken));
        }

        foreach (var callee in callees)
        {
            await callee.Inner.DisposeAsync();
        }

        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// A read cancelled while the replicas which hold DATA are held releases the DATA they report later.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ACancelledReadReleasesTheDataWhichArrivesLater()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var replicas = Replicas.Create();
        await replicas.WriteAsync([1, 2], slot: 0);
        replicas.Gated[1].Hold();
        replicas.Gated[2].Hold();

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = replicas.Replica.ReadAsync(Slot.Zero, cancellation.Token).AsTask();
        await replicas.Gated[1].ReadStarted;
        await replicas.Gated[2].ReadStarted;
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        replicas.ReleaseAll();
        await replicas.Replica.DisposeAsync();
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// A recovery returns once its candidate end is decided and copied, leaving the reads a held replica has not
    /// answered running; disposal waits for them, releases the DATA they report, and only then disposes the replicas.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DisposalWaitsForARecoverysStragglersAndReleasesTheirData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var replicas = Replicas.Create();
        await replicas.WriteAsync([0, 1, 2], slot: 0);
        replicas.Gated[2].HoldReads();

        // Replicas 0 and 1 hold position 0 and lack position 1, which decides the end without replica 2.
        Assert.Equal(new Slot(1), await replicas.Replica.RecoverEndAsync(Slot.Zero, cancellationToken));
        await replicas.Gated[2].ReadStarted;
        Assert.True(replicas.Replica.TrackedStragglers > 0);

        var disposal = replicas.Replica.DisposeAsync().AsTask();
        Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken)));
        Assert.All(replicas.Gated, static replica => Assert.False(replica.IsDisposed));

        replicas.ReleaseAll();
        await disposal;
        Assert.All(replicas.Gated, static replica => Assert.True(replica.IsDisposed && !replica.UsedAfterDisposal));
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// A recovery cancelled after it read a value and while the replicas it copies to are held releases that value, and
    /// the DATA of the reads which answer later.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ACancelledRecoveryReleasesTheValuesItReadAndTheDataWhichArrivesLater()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var replicas = Replicas.Create();
        await replicas.WriteAsync([0], slot: 0);
        await replicas.WriteAsync([0], slot: 1);
        replicas.Gated[1].HoldWritesAndReads();
        replicas.Gated[2].HoldWritesAndReads();

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var recovery = replicas.Replica.RecoverAsync(Slot.Zero, cancellation.Token).AsTask();
        await replicas.Gated[1].WriteStarted;
        await replicas.Gated[2].WriteStarted;
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);

        replicas.ReleaseAll();
        await replicas.Replica.DisposeAsync();
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// ledger-protocol.md §6.3: a recovery asks an unreachable replica again until it answers, so a recovery which cannot reach
    /// <c>N − Q + 1</c> replicas, here two of three unreachable for good, never ends by itself. Disposal stops it: the
    /// recovery fails with <see cref="ObjectDisposedException"/>, and disposal completes rather than wait for it forever.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DisposalStopsARecoveryWhichCannotReachEnoughReplicas()
    {
        var time = new FakeTimeProvider();
        var replicas = Enumerable.Range(0, 3).Select(static index => new ControlledLedgerReplica(new InMemoryLedgerReplica(Window)) { Unreachable = index > 0 }).ToArray();
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window, time);
        var recovery = quorum.RecoverAsync(Slot.Zero, TestContext.Current.CancellationToken).AsTask();
        await LedgerStoreTestExtensions.WaitUntilAsync(
            () =>
            {
                time.Advance(ReplicaRetry.MaximumDelay);
                return replicas[1].SealCalls >= 3 && replicas[2].SealCalls >= 3;
            },
            "the recovery asks the unreachable replicas again");
        Assert.False(recovery.IsCompleted);

        await quorum.DisposeAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => recovery);
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// Regression test (#337), ledger-protocol.md §6.3: disposal stops a recovery whose reads are held at replicas
    /// which answer only once their call is cancelled, as calls to a stopped silo may: every read the recovery sent sees
    /// its cancellation, though the stop's callbacks end the recovery, and with it its positions, while they still run,
    /// and the disposal ends.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DisposalCancelsEveryReadOfARecoveryItStops()
    {
        var started = 0;
        var cancelled = 0;
        var replicas = Enumerable.Range(0, 3).Select(_ => new DelegatingLedgerReplica
        {
            ReadTail = static (tail, seal, _) => ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(tail, IsSealed: seal)),
            Read = async (_, _, token) =>
            {
                _ = Interlocked.Increment(ref started);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, token);
                }
                finally
                {
                    _ = Interlocked.Increment(ref cancelled);
                }

                throw new UnreachableException();
            },
        }).ToArray();
        var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);
        var recovery = quorum.RecoverAsync(Slot.Zero, TestContext.Current.CancellationToken).AsTask();
        await LedgerStoreTestExtensions.WaitUntilAsync(
            () => Volatile.Read(ref started) == replicas.Length * Window.WindowSize,
            "the recovery reads a window of positions from every replica");

        await quorum.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => recovery);
        Assert.Equal(replicas.Length * Window.WindowSize, Volatile.Read(ref cancelled));
    }

    /// <summary>
    /// Regression test (#337, review of #349), ledger-protocol.md §6.3: disposal stops a recovery while it copies a found
    /// position to replicas whose recovery writes answer only once their call is cancelled: every copy sees its
    /// cancellation, though the stop's callbacks end the recovery while they still run, and the disposal ends.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DisposalCancelsEveryCopyOfARecoveryItStops()
    {
        var started = 0;
        var cancelled = 0;
        var replicas = Enumerable.Range(0, 3).Select(index => new DelegatingLedgerReplica
        {
            ReadTail = static (tail, seal, _) => ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(tail, IsSealed: seal)),

            // Replica 0 alone holds position 0, so the recovery copies it to the others; no replica holds a later one.
            Read = (slot, _, _) => ValueTask.FromResult(index == 0 && slot == Slot.Zero ? new ReplicaRead(LedgerReplicaTestData.Buffer([1])) : ReplicaAnswers.Missing),
            Write = async (_, _, _, recovery, token) =>
            {
                Assert.True(recovery);
                _ = Interlocked.Increment(ref started);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, token);
                }
                finally
                {
                    _ = Interlocked.Increment(ref cancelled);
                }

                throw new UnreachableException();
            },
        }).ToArray();
        var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);
        var recovery = quorum.RecoverAsync(Slot.Zero, TestContext.Current.CancellationToken).AsTask();
        await LedgerStoreTestExtensions.WaitUntilAsync(() => Volatile.Read(ref started) == 2, "the recovery copies position 0 to both other replicas");
        Assert.False(recovery.IsCompleted);

        await quorum.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => recovery);
        Assert.Equal(2, Volatile.Read(ref cancelled));
        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// A write observes the failure of a straggler which fails after the write was written at <c>Q</c>, so the failure
    /// never surfaces as an unobserved task exception.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AWriteObservesTheFailureOfAStragglerWhichFailsAfterIt()
    {
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            foreach (var exception in args.Exception.InnerExceptions)
            {
                unobserved.Enqueue(exception);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var (failures, collected) = await WriteOverAStragglerWhichFailsAsync(TestContext.Current.CancellationToken);

            // Resume in a fresh frame: an unoptimized frame keeps its locals, and with them the write's tasks, reachable.
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // Unless the replica was collected, the straggler's task it tracked was never finalized and the check below is
            // vacuous.
            Assert.All(collected, static reference => Assert.False(reference.IsAlive));
            Assert.Single(failures);
            Assert.DoesNotContain(unobserved, failures.Contains);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    /// <summary>
    /// Writes over replicas 0 and 1 while replica 2 is held, then fails replica 2's write, and returns the failure it
    /// threw, once nothing which ran the write remains reachable, with a weak reference to the replica.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(Exception[] Failures, WeakReference[] Collected)> WriteOverAStragglerWhichFailsAsync(CancellationToken cancellationToken)
    {
        var replicas = Replicas.Create();
        replicas.Gated[2].Hold();
        replicas.Gated[2].FailWrites(1);
        using (var payload = Payload(0))
        {
            Assert.Equal(ReplicaWriteCase.Written, (await replicas.Replica.WriteAsync(Slot.Zero, payload, cancellationToken)).Case);
        }

        replicas.ReleaseAll();
        await replicas.Gated[2].WritesFailed;
        await replicas.Replica.DisposeAsync();
        return ([.. replicas.Gated[2].Failures], [new WeakReference(replicas.Replica)]);
    }

    private static ArcBuffer Payload(long slot) => LedgerReplicaTestData.Buffer([(byte)(slot + 1)]);

    private sealed class Replicas
    {
        private readonly InMemoryLedgerReplica[] _inner;

        private Replicas(InMemoryLedgerReplica[] inner)
        {
            _inner = inner;
            Gated = [.. inner.Select(static replica => new GatedLedgerReplica(replica))];
            Replica = LedgerQuorumTestData.Create(Gated, 2, Window, new FakeTimeProvider());
        }

        internal GatedLedgerReplica[] Gated { get; }

        internal LedgerQuorum Replica { get; }

        internal static Replicas Create() =>
            new([.. Enumerable.Range(0, 3).Select(static _ => new InMemoryLedgerReplica(Window))]);

        /// <summary>
        /// Writes the value of <paramref name="slot"/> to the replicas of <paramref name="replicas"/> directly.
        /// </summary>
        internal async Task WriteAsync(int[] replicas, long slot)
        {
            using var payload = Payload(slot);
            foreach (var replica in replicas)
            {
                Assert.Equal(ReplicaAnswers.Written, await _inner[replica].WriteAsync(new Slot(slot), payload, TestContext.Current.CancellationToken));
            }
        }

        internal void HoldAll()
        {
            foreach (var replica in Gated)
            {
                replica.Hold();
            }
        }

        internal void ReleaseAll()
        {
            foreach (var replica in Gated)
            {
                replica.Release();
            }
        }
    }

    /// <summary>
    /// A replica on another node, as a transport reaches it: while held, a write's request reaches its callee, which runs
    /// it only once the test lands it, and its caller's wait ends only at the caller's cancellation, as a transport ends
    /// it at once while the request may still land (ledger-replicas.md §2.2). Its disposal, the quorum's, leaves the
    /// callee's replica open, as a node's replica outlives a caller's handle.
    /// </summary>
    private sealed class CalleeHeldLedgerReplica(InMemoryLedgerReplica inner) : ILedgerReplica
    {
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _held;
        private (Slot Slot, byte[] Payload, Slot ContiguousTail)? _request;

        public InMemoryLedgerReplica Inner => inner;

        /// <summary>Gets a task which completes once a held write's request reached the callee.</summary>
        public Task Sent => _sent.Task;

        /// <summary>Gets the token of the held write's call.</summary>
        public CancellationToken Token { get; private set; }

        public bool IsDisposed { get; private set; }

        public void Hold() => _held = true;

        /// <summary>Runs the held write's request at its callee, as a request delivered late does.</summary>
        public async Task<ReplicaWriteOutcome> LandAsync()
        {
            var (slot, bytes, contiguousTail) = _request ?? throw new InvalidOperationException("No write was held.");
            using var payload = LedgerReplicaTestData.Buffer(bytes);
            return await inner.WriteAsync(slot, payload, contiguousTail, recovery: false, CancellationToken.None);
        }

        public async ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            if (!_held)
            {
                return await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
            }

            _request = (slot, payload.ToArray(), contiguousTail);
            Token = cancellationToken;
            _ = _sent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
            throw new UnreachableException();
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Forwards to a real replica, except that while held its operations wait until released, that it can hold only its
    /// reads, or its writes and reads, and that it can fail its next writes; it records any operation which reaches the
    /// replica after its disposal.
    /// </summary>
    private sealed class GatedLedgerReplica(InMemoryLedgerReplica inner) : ILedgerReplica
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writesFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<Exception> _failures = new();
        private TaskCompletionSource _gate = Open();
        private TaskCompletionSource _readGate = Open();
        private TaskCompletionSource _writeGate = Open();
        private int _writeFailures;
        private int _disposed;
        private int _usedAfterDisposal;

        public Task ReadStarted => _readStarted.Task;

        public Task WriteStarted => _writeStarted.Task;

        public Task WritesFailed => _writesFailed.Task;

        public IReadOnlyCollection<Exception> Failures => _failures;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool UsedAfterDisposal => Volatile.Read(ref _usedAfterDisposal) != 0;

        public void Hold() => Volatile.Write(ref _gate, Closed());

        /// <summary>
        /// Holds every read until released.
        /// </summary>
        public void HoldReads() => Volatile.Write(ref _readGate, Closed());

        /// <summary>
        /// Holds every write and read until released.
        /// </summary>
        public void HoldWritesAndReads()
        {
            HoldReads();
            Volatile.Write(ref _writeGate, Closed());
        }

        public void Release()
        {
            _ = Volatile.Read(ref _gate).TrySetResult();
            _ = Volatile.Read(ref _readGate).TrySetResult();
            _ = Volatile.Read(ref _writeGate).TrySetResult();
        }

        /// <summary>
        /// Fails the next <paramref name="count"/> writes without applying them.
        /// </summary>
        public void FailWrites(int count) => Volatile.Write(ref _writeFailures, count);

        public async ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            _ = _writeStarted.TrySetResult();
            await Volatile.Read(ref _writeGate).Task;
            await PassAsync();
            var remaining = Interlocked.Decrement(ref _writeFailures);
            if (remaining >= 0)
            {
                var failure = new IOException("An injected write failure.");
                _failures.Enqueue(failure);
                if (remaining == 0)
                {
                    _ = _writesFailed.TrySetResult();
                }

                throw failure;
            }

            return await inner.WriteAsync(slot, payload, contiguousTail, recovery, CancellationToken.None);
        }

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

        public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            _ = _readStarted.TrySetResult();
            await Volatile.Read(ref _readGate).Task;
            await PassAsync();
            return await inner.ReadAsync(slot, contiguousTail, CancellationToken.None);
        }

        public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
        {
            await PassAsync();
            return await inner.ReadTailAsync(contiguousTail, seal, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            _ = Interlocked.Exchange(ref _disposed, 1);
            await inner.DisposeAsync();
        }

        private static TaskCompletionSource Open()
        {
            var open = new TaskCompletionSource();
            open.SetResult();
            return open;
        }

        private static TaskCompletionSource Closed() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A held operation waits for its release whatever its caller's cancellation, as a replica already serving a
        // request answers it; the replica then serves it to the end.
        private async Task PassAsync()
        {
            await Volatile.Read(ref _gate).Task;
            if (IsDisposed)
            {
                _ = Interlocked.Exchange(ref _usedAfterDisposal, 1);
            }
        }
    }
}

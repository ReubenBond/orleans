using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The caller side of the replica transport over a node grain double (orleans.md §5.12): each node's data lane, its own
/// node's included, holds a call beyond the node's budget in arrival order, a large call at the head not overtaken by
/// smaller ones behind it, fails it unsent only at its deadline, and withdraws it at its caller's cancellation, with
/// mutants which overtake, fail fast, or time out early each caught by its oracle (OR-I17); one deadline from the call's
/// start bounds its local registration wait too; contiguous-tail reads and seals take the control lane; a remote whole read
/// reserves the read reply and reads a longer batch again reserving its length, a local read reserves no bytes, and a
/// local call's cancellation ends only its caller's wait; a payload above the payload limit is refused before it is sent;
/// a remote write's payload crosses in one sender copy in Orleans' pool, taken once granted and shared by every remote call
/// of the payload, and a read's reply comes back copied into Clog's page pool, the delivered buffer released (OR-I18); every
/// DATA the grain double delivers is released by its teardown; a <c>Backpressure</c> answer is repeated
/// after a seeded, jittered backoff, holding no budget, while the deadline allows; and every result reaches the caller as
/// §5.12's table states (OR-I15, OR-I16), the snapshot pinning one case per row.
/// </summary>
public sealed class OrleansReplicaTransportCallerTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>OR-I17: a call beyond its node's budget waits in order behind a large call at the head, on either path.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALargeCallAtTheHeadIsNotOvertakenBySmallerOnesBehindIt(bool local) =>
        Assert.Null(await OrderViolationAsync(CallBudgetMutant.None, local));

    /// <summary>OR-I17: a call waiting for budget fails unsent with the queue timeout only at its deadline, on either path.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACallWaitsUntilItsDeadlineAndThenFailsUnsent(bool local) =>
        Assert.Null(await DeadlineViolationAsync(CallBudgetMutant.None, local));

    /// <summary>Each lane defect is caught by the oracle which names it: overtaking by the order oracle, failing fast or early by the deadline oracle.</summary>
    [Theory]
    [InlineData(false, CallBudgetMutant.Overtake, "order")]
    [InlineData(false, CallBudgetMutant.FailFast, "deadline")]
    [InlineData(false, CallBudgetMutant.EarlyDeadline, "deadline")]
    [InlineData(true, CallBudgetMutant.Overtake, "order")]
    [InlineData(true, CallBudgetMutant.FailFast, "deadline")]
    [InlineData(true, CallBudgetMutant.EarlyDeadline, "deadline")]
    internal async Task EachMutantIsCaughtByItsOracle(bool local, CallBudgetMutant mutant, string oracle)
    {
        var violation = oracle == "order" ? await OrderViolationAsync(mutant, local) : await DeadlineViolationAsync(mutant, local);
        Assert.NotNull(violation);
        Assert.StartsWith(oracle, violation, StringComparison.Ordinal);
    }

    /// <summary>OR-I17: a waiting call's cancellation withdraws it, unsent, with the caller's token, and lets the call behind it in.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingAWaitingCallWithdrawsItWithTheCallersToken(bool local)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        scenario.Grain.Answer = _ => ReplicaWriteOutcome.Written.Instance;
        var replica = scenario.Open(local);
        using var cancel = new CancellationTokenSource();
        using (await scenario.Transport.Budget(scenario.Node(local)).EnterAsync(60, scenario.DeadlineFromNow(), token))
        {
            var large = WriteAsync(replica, 1, 50, cancel.Token);
            var small = WriteAsync(replica, 2, 10, token);

            await cancel.CancelAsync();

            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => large.WaitAsync(Bound, token));
            Assert.Equal(cancel.Token, canceled.CancellationToken);
            _ = Assert.IsType<ReplicaWriteOutcome.Written>((await small.WaitAsync(Bound, token)).Value);
        }

        Assert.Equal(local ? 0 : 1, scenario.Grain.Sent);
    }

    /// <summary>
    /// Regression test (review of row 4.7): a local call's cancellation ends only its caller's wait; its store operation
    /// runs to its end, holding its grant until then, and its write takes effect.
    /// </summary>
    [Fact]
    public async Task CancellingARunningLocalCallEndsOnlyTheWaitAndKeepsItsGrantUntilTheStoreEnds()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var replica = scenario.Open(local: true);
        var lane = scenario.Transport.Budget(scenario.Local.Node);
        using var cancel = new CancellationTokenSource();
        scenario.Storage.Close();

        var write = WriteAsync(replica, 0, 10, cancel.Token);
        await ReplicaCluster.EventuallyAsync(() => scenario.Storage.Held > 0);
        await cancel.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Bound, token));
        Assert.Equal((1, 10L), (lane.OutstandingCalls, lane.OutstandingBytes));
        scenario.Storage.Open();
        await ReplicaCluster.EventuallyAsync(() => lane.OutstandingCalls == 0);
        Assert.Equal("data", await ReadValueAsync(replica, 0));
    }

    /// <summary>
    /// Regression test (#337), OR-I16, OR-I18: a remote read whose grain call ignores its caller's cancellation, as one an
    /// outgoing grain call filter holds before Orleans registers its token does, ends its caller's wait at the cancellation,
    /// returning its grant; the DATA reply which arrives afterwards, which no caller takes, is released.
    /// </summary>
    [Fact]
    public async Task ACancelledRemoteReadWhoseCallIgnoresTheCancellationReleasesTheDataWhichArrivesLater()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        scenario.Grain.HonoursCancellation = false;
        var replica = scenario.Open(local: false);
        var lane = scenario.Transport.Budget(scenario.Node(local: false));
        using var cancel = new CancellationTokenSource();

        var read = replica.ReadAsync(new Slot(0), Slot.Zero, cancel.Token).AsTask();
        await scenario.Grain.WaitSentAsync(1);
        await cancel.CancelAsync();

        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound, token));
        Assert.Equal(cancel.Token, canceled.CancellationToken);
        Assert.Equal(0, lane.OutstandingCalls);

        var data = WireBytes.Data(1, 2, 3);
        var bytes = data.Bytes;
        _ = scenario.Grain.Calls[0].Reply.TrySetResult(data);
        await ReplicaCluster.EventuallyAsync(() => WireBytes.IsReleased(bytes));
    }

    /// <summary>
    /// Regression test (#337), OR-I18: an abandoned call's answer which arrives after its caller's cancellation goes to the
    /// abandoned path once, which releases its DATA; one which arrived before the wait ended is the caller's, which the
    /// abandoned path never touches, also when the caller's token was cancelled by then.
    /// </summary>
    [Fact]
    public async Task AnAbandonedCallReleasesOnlyTheDataWhichArrivesAfterItsCancellationAndOnlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var released = 0;
        void Abandoned(RemoteReplicaRead answer)
        {
            _ = Interlocked.Increment(ref released);
            OrleansLedgerReplicaTransport.ReleaseData(answer);
        }

        // After the cancellation: released once, by the abandoned path.
        using (var cancel = new CancellationTokenSource())
        {
            var call = new TaskCompletionSource<RemoteReplicaRead>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = OrleansLedgerReplicaTransport.UntilCancelledAsync(call.Task, Abandoned, cancel.Token);
            await cancel.CancelAsync();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(Bound, token));
            Assert.Equal(cancel.Token, canceled.CancellationToken);

            var late = WireBytes.Data(4, 5);
            var lateBytes = late.Bytes;
            Assert.Equal(0, Volatile.Read(ref released));
            call.SetResult(late);
            await ReplicaCluster.EventuallyAsync(() => Volatile.Read(ref released) == 1);
            Assert.True(WireBytes.IsReleased(lateBytes));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, token);
            Assert.Equal(1, Volatile.Read(ref released));
        }

        // Before the cancellation, and before the caller looks with its token already cancelled: the caller's.
        foreach (var cancelledFirst in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource();
            var early = WireBytes.Data(6, 7);
            var earlyBytes = early.Bytes;
            if (cancelledFirst)
            {
                await cancel.CancelAsync();
            }

            var answer = await OrleansLedgerReplicaTransport.UntilCancelledAsync(Task.FromResult<RemoteReplicaRead>(early), Abandoned, cancel.Token);
            await cancel.CancelAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, token);
            Assert.Same(early, answer.Value);
            Assert.False(WireBytes.IsReleased(earlyBytes));
            Assert.Equal(1, Volatile.Read(ref released));
            early.Bytes.Dispose();
        }
    }

    /// <summary>
    /// OR-I2, OR-I17: a local call made before the node registers enters its lane and then waits for the registration
    /// within its deadline, from the call's start: it proceeds once the node registers, and fails unsent with the queue
    /// timeout once the deadline passes first. A remote call does not wait for the local registration.
    /// </summary>
    [Fact]
    public async Task ALocalCallWaitsForTheRegistrationWithinItsDeadline()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync(registered: false);
        var local = scenario.Open(local: true);
        var lane = scenario.Transport.Budget(scenario.Local.Node);

        scenario.Grain.Answer = _ => ReplicaWriteOutcome.Written.Instance;
        Assert.Equal(ReplicaWriteOutcome.Written.Instance, (await WriteAsync(scenario.Open(local: false), 0, 1, token).WaitAsync(Bound, token)).Value);

        var timedOut = WriteAsync(local, 0, 10, token);
        Assert.False(timedOut.IsCompleted);
        Assert.Equal(1, lane.OutstandingCalls);
        scenario.Time.Advance(TransportScenario.Deadline - TimeSpan.FromTicks(1));
        Assert.False(timedOut.IsCompleted);
        scenario.Time.Advance(TimeSpan.FromTicks(1));
        _ = await Assert.ThrowsAsync<LedgerReplicaQueueTimeoutException>(() => timedOut.WaitAsync(Bound, token));

        var waiting = WriteAsync(local, 1, 10, token);
        Assert.False(waiting.IsCompleted);
        scenario.Register();
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await waiting.WaitAsync(Bound, token)).Value);
        Assert.Equal([TransportScenario.Ledger], await scenario.Store.ListReplicasAsync(token));
    }

    /// <summary>
    /// OR-I17: a local call's one deadline, from the call's start, covers both its wait in its node's lane and its wait for
    /// the node's registration: a call which waits half its deadline in the lane times out waiting for the registration
    /// at its deadline, not a whole deadline after the lane granted it.
    /// </summary>
    [Fact]
    public async Task ALocalCallsDeadlineSpansItsLaneAndRegistrationWaits()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync(registered: false);
        var replica = scenario.Open(local: true);
        var lane = scenario.Transport.Budget(scenario.Local.Node);
        var half = TransportScenario.Deadline / 2;

        Task<ReplicaWriteOutcome> write;
        int timers;
        using (await lane.EnterAsync(TransportScenario.Budget, scenario.DeadlineFromNow(), token))
        {
            write = WriteAsync(replica, 0, 10, token);
            Assert.Equal(1, lane.Waiting);
            scenario.Time.Advance(half);
            Assert.False(write.IsCompleted);
            timers = scenario.Time.Timers;
        }

        // Granted, the call arms its registration wait's timer with what is left of its deadline.
        await ReplicaCluster.EventuallyAsync(() => scenario.Time.Timers > timers);
        Assert.Equal((1, 0), (lane.OutstandingCalls, lane.Waiting));
        scenario.Time.Advance(TransportScenario.Deadline - half - TimeSpan.FromTicks(1));
        Assert.False(write.IsCompleted);
        scenario.Time.Advance(TimeSpan.FromTicks(1));
        _ = await Assert.ThrowsAsync<LedgerReplicaQueueTimeoutException>(() => write.WaitAsync(Bound, token));
        Assert.Empty(await scenario.Store.ListReplicasAsync(token));
    }

    /// <summary>
    /// OR-I2: a local call fails as unavailable, without effect, when its node's registration fails or the node stops; and
    /// once the node is closed, through a handle opened before or after, with its store disposed, a write, a seal, and a
    /// read fail so too, and the node's store is no longer answered.
    /// </summary>
    [Fact]
    public async Task ALocalCallFailsAsUnavailableWhenTheRegistrationFailsOrTheNodeStops()
    {
        var token = TestContext.Current.CancellationToken;
        await using (var failed = await TransportScenario.CreateAsync(registered: false))
        {
            var waiting = WriteAsync(failed.Open(local: true), 0, 10, token);
            failed.Local.RegistrationFailed(new InvalidOperationException("Refused."));
            var unavailable = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => waiting.WaitAsync(Bound, token));
            _ = Assert.IsType<InvalidOperationException>(unavailable.InnerException);
            Assert.Empty(await failed.Store.ListReplicasAsync(token));
        }

        await using (var stopped = await TransportScenario.CreateAsync())
        {
            var replica = stopped.Open(local: true);
            stopped.Local.Stop();
            _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => WriteAsync(replica, 0, 10, token).WaitAsync(Bound, token));
            Assert.Empty(await stopped.Store.ListReplicasAsync(token));
        }

        await using var closed = await TransportScenario.CreateAsync();
        await using var before = closed.Open(local: true);
        closed.Local.Stop();
        closed.Local.Close();
        await closed.Store.DisposeAsync();
        await using var after = closed.Open(local: true);

        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => WriteAsync(before, 0, 10, token).WaitAsync(Bound, token));
        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(async () => await before.ReadTailAsync(Slot.Zero, seal: true, token));
        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(async () => await after.ReadAsync(Slot.Zero, Slot.Zero, token));
        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => WriteAsync(after, 0, 10, token).WaitAsync(Bound, token));
        _ = Assert.Throws<InvalidOperationException>(() => closed.Local.Store);

        // orleans.md §5.14: a diskless silo holds no LedgerStore, so local replica calls answer unavailable.
        var disklessLocal = new LocalNode();
        var disklessNode = new NodeId(Guid.NewGuid());
        disklessLocal.OpenedDiskless(disklessNode);
        disklessLocal.Register(
            new NodeRegistryEntry(disklessNode, SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: true),
            new Engine.Archive.ArchiveStoreIdentity(Engine.Archive.ArchiveStoreKind.Lsm, new Engine.Archive.ArchiveStorePrefix("t"), new Engine.Archive.ArchiveStoreId(new UInt128(0, 1))),
            new Engine.Ledgers.Objects.ObjectLedgerStoreId(new UInt128(0, 2)));
        var disklessTransport = new OrleansLedgerReplicaTransport(
            disklessLocal,
            _ => throw new InvalidOperationException("Local diskless calls never invoke a remote grain."),
            static () => { },
            TransportScenario.Limits,
            NodeCallBudget.Create,
            new Random(1),
            TimeProvider.System);
        await using var disklessReplica = disklessTransport.Open(disklessNode, TransportScenario.Ledger, TransportScenario.Options);
        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => WriteAsync(disklessReplica, 0, 10, token).WaitAsync(Bound, token));
        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(async () => await disklessReplica.ReadAsync(Slot.Zero, Slot.Zero, token));
        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(async () => await disklessReplica.ReadTailAsync(Slot.Zero, seal: false, token));
    }

    /// <summary>
    /// OR-I2: a local call which passed the serving gate before the node stopped, and which its store refuses once the
    /// node closed it, answers <see cref="LedgerReplicaUnavailableException"/>, as one the gate refuses does; on a node
    /// which stopped but has not closed, whose store is open, the refusal is a defect, and surfaces as
    /// <see cref="ObjectDisposedException"/>. By construction: the read meets the replica's drop, whose record the WAL
    /// holds, and waits for its outcome; the node stops, and closes or not, and the store's disposal begins, so once the
    /// drop is durable the read finds the store disposed (ledgerstore.md §8.10).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALocalCallWhichPassedTheGateAnswersUnavailableWhenItsStoreClosesUnderIt(bool closed)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var replica = scenario.Open(local: true);
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await WriteAsync(replica, 0, 10, token)).Value);
        scenario.Storage.Close();
        var drop = scenario.Store.DropReplicaAsync(TransportScenario.Ledger, token).AsTask();
        await ReplicaCluster.EventuallyAsync(() => scenario.Storage.Held > 0);
        var read = replica.ReadAsync(Slot.Zero, Slot.Zero, token).AsTask();
        Assert.False(read.IsCompleted);

        scenario.Local.Stop();
        if (closed)
        {
            scenario.Local.Close();
        }

        var disposal = scenario.Store.DisposeAsync().AsTask();
        scenario.Storage.Open();
        await drop.WaitAsync(Bound, token);
        await disposal.WaitAsync(Bound, token);

        if (closed)
        {
            var unavailable = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => read.WaitAsync(Bound, token));
            _ = Assert.IsType<ObjectDisposedException>(unavailable.InnerException);
        }
        else
        {
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(Bound, token));
        }
    }

    /// <summary>
    /// OR-I17: a remote whole read reserves the read reply and a ranged read its length in the data lane until its reply
    /// arrives; a local read reserves no bytes, so it runs while the lane's bytes are all held; and contiguous-tail reads
    /// and seals take the control lane, by call count alone, apart from the data lane.
    /// </summary>
    [Fact]
    public async Task ReadsReserveTheirRepliesAndTailReadsTakeTheControlLane()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var remote = scenario.Open(local: false);
        var lane = scenario.Transport.Budget(scenario.Remote);
        var control = scenario.Transport.ControlBudget(scenario.Remote);

        var whole = remote.ReadAsync(new Slot(0), Slot.Zero, token).AsTask();
        await scenario.Grain.WaitSentAsync(1);
        Assert.Equal((TransportScenario.ReadReply, 1L), (lane.OutstandingBytes, (long)lane.OutstandingCalls));
        var ranged = remote.ReadAsync(new Slot(0), 2, 5, Slot.Zero, token).AsTask();
        await scenario.Grain.WaitSentAsync(2);
        Assert.Equal(TransportScenario.ReadReply + 5, lane.OutstandingBytes);
        var tail = remote.ReadTailAsync(Slot.Zero, seal: true, token).AsTask();
        await scenario.Grain.WaitSentAsync(3);
        Assert.Equal((2, 1), (lane.OutstandingCalls, control.OutstandingCalls));
        Assert.Equal(TransportScenario.ReadReply, ((LedgerReplicaReadRequest)scenario.Grain.Calls[0].Request).MaxReplyBytes);
        Assert.Equal(5, ((LedgerReplicaReadRequest)scenario.Grain.Calls[1].Request).MaxReplyBytes);

        scenario.Grain.ReleaseAll();
        Assert.Equal(ReplicaRead.Missing.Instance, (await whole.WaitAsync(Bound, token)).Value);
        Assert.Equal(ReplicaRead.Missing.Instance, (await ranged.WaitAsync(Bound, token)).Value);
        _ = await tail.WaitAsync(Bound, token);

        var local = scenario.Open(local: true);
        var localLane = scenario.Transport.Budget(scenario.Local.Node);
        var localControl = scenario.Transport.ControlBudget(scenario.Local.Node);
        using (await localLane.EnterAsync(TransportScenario.Budget, scenario.DeadlineFromNow(), token))
        {
            Assert.True((await local.ReadAsync(new Slot(0), Slot.Zero, token).AsTask().WaitAsync(Bound, token)).TryGetValue(out ReplicaRead.Missing? _));
            Assert.True((await local.ReadTailAsync(Slot.Zero, seal: false, token).AsTask().WaitAsync(Bound, token)).TryGetValue(out LedgerReplicaTail _));
        }

        var held = new List<IDisposable>();
        for (var call = 0; call < TransportScenario.ControlCalls; call++)
        {
            held.Add(await localControl.EnterAsync(0, scenario.DeadlineFromNow(), token));
        }

        var queued = local.ReadTailAsync(Slot.Zero, seal: false, token).AsTask();
        Assert.Equal(1, localControl.Waiting);
        held.ForEach(static grant => grant.Dispose());
        _ = await queued.WaitAsync(Bound, token);
    }

    /// <summary>
    /// OR-I17: a remote whole read whose batch is longer than the read reply is answered <c>TooLarge</c> with its length,
    /// and reads again reserving exactly that; a <c>TooLarge</c> naming a length above the payload limit fails its reply
    /// check.
    /// </summary>
    [Fact]
    public async Task AWholeReadOfALongerBatchReadsAgainReservingItsLength()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var replica = scenario.Open(local: false);
        var length = TransportScenario.ReadReply + 10;
        scenario.Grain.Answer = request => ((LedgerReplicaReadRequest)request).MaxReplyBytes < length
            ? new RemoteReplicaRead.TooLarge(length)
            : WireBytes.Data(new byte[length]);

        Assert.True((await replica.ReadAsync(new Slot(0), Slot.Zero, token)).TryGetValue(out ArcBuffer batch));
        using (batch)
        {
            Assert.Equal(length, batch.Length);
        }

        Assert.Equal([TransportScenario.ReadReply, length], scenario.Grain.Calls.Select(static call => ((LedgerReplicaReadRequest)call.Request).MaxReplyBytes));
        Assert.All(scenario.Grain.Calls, static call => Assert.Equal(0, ((LedgerReplicaReadRequest)call.Request).Length));

        scenario.Grain.Answer = _ => new RemoteReplicaRead.TooLarge(TransportScenario.PayloadLimit + 1);
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => replica.ReadAsync(new Slot(0), Slot.Zero, token).AsTask());
    }

    /// <summary>
    /// OR-I17: a payload above the payload limit, or a ranged read longer than it, is refused with
    /// <see cref="ArgumentException"/> before it is sent or takes any budget, on the local node too.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACallAboveThePayloadLimitIsRefusedBeforeSending(bool local)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var replica = scenario.Open(local);

        Assert.Equal("payload", (await Assert.ThrowsAnyAsync<ArgumentException>(() => WriteAsync(replica, 0, TransportScenario.PayloadLimit + 1, token))).ParamName);
        Assert.Equal("length", (await Assert.ThrowsAnyAsync<ArgumentException>(() => replica.ReadAsync(new Slot(0), 0, TransportScenario.PayloadLimit + 1, Slot.Zero, token).AsTask())).ParamName);

        Assert.Equal(0, scenario.Grain.Sent);
        Assert.All(scenario.Transport.Lanes, static lane => Assert.Equal((0L, 0), (lane.OutstandingBytes, lane.OutstandingCalls)));
        Assert.Empty(await scenario.Store.ListReplicasAsync(token));
    }

    /// <summary>
    /// OR-I18: a remote write takes its sender copy only once granted, so a write waiting in its lane holds none; every
    /// granted remote call of one pooled payload carries one copy in Orleans' pool, which is not the payload's page, shared
    /// while any of the calls holds it and released once, by the last, so with each message's pin released too its pages
    /// return; a read's reply comes back copied into Clog's page pool, and the caller releases the buffer Orleans delivered.
    /// </summary>
    [Fact]
    public async Task RemoteCallsOfOnePayloadShareOneSenderCopyTakenOnceGranted()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var first = scenario.Open(local: false);
        var second = scenario.Transport.Open(new NodeId(Guid.NewGuid()), TransportScenario.Ledger, TransportScenario.Options);
        using var payload = TransportScenario.Payload(20, fill: 7);

        var writes = new[]
        {
            first.WriteAsync(new Slot(0), payload, Slot.Zero, recovery: false, token).AsTask(),
            second.WriteAsync(new Slot(0), payload, Slot.Zero, recovery: false, token).AsTask(),
        };
        await scenario.Grain.WaitSentAsync(2);
        var copies = scenario.Grain.Calls.Select(static call => ((LedgerReplicaWriteRequest)call.Request).Payload).ToArray();
        Assert.Same(copies[0].First, copies[1].First);
        Assert.NotSame(payload.First.Array, copies[0].First!.Array);
        Assert.Equal(payload.ToArray(), copies[0].ToArray());
        Assert.Equal(1, scenario.Transport.SenderCopyCount);

        scenario.Grain.ReleaseAll();
        _ = await Task.WhenAll(writes).WaitAsync(Bound, token);
        Assert.Equal(0, scenario.Transport.SenderCopyCount);
        Assert.True(WireBytes.IsReleased(copies[0]));
        await second.DisposeAsync();

        using (await scenario.Transport.Budget(scenario.Remote).EnterAsync(TransportScenario.Budget, scenario.DeadlineFromNow(), token))
        {
            var waiting = WriteAsync(first, 2, 10, token);
            Assert.Equal(0, scenario.Transport.SenderCopyCount);
            Assert.Equal(1, scenario.Transport.Budget(scenario.Remote).Waiting);
            _ = waiting;
        }

        await scenario.Grain.WaitSentAsync(3);
        var reply = WireBytes.Of(1, 2, 3);
        scenario.Grain.Answer = _ => new RemoteReplicaRead.Data(reply);
        Assert.True((await first.ReadAsync(new Slot(0), Slot.Zero, token)).TryGetValue(out ArcBuffer batch));
        using (batch)
        {
            Assert.Equal([1, 2, 3], batch.ToArray());
            Assert.NotSame(reply.First!.Array, batch.First.Array);
        }

        Assert.True(WireBytes.IsReleased(reply));
    }

    /// <summary>
    /// orleans.md §5.12: a call answered <c>Backpressure</c>, which had no effect, returns its grant, waits a seeded,
    /// jittered backoff, doubling to a second, and is attempted again while its deadline allows the next attempt: a host
    /// which frees answers the store's outcome, and one which never does is reported <c>Backpressure</c> at the attempt
    /// whose next backoff would pass the deadline.
    /// </summary>
    [Fact]
    public async Task ABackpressureAnswerIsRepeatedAfterASeededBackoffWhileTheDeadlineAllows()
    {
        var token = TestContext.Current.CancellationToken;
        const int Seed = 42;
        await using var scenario = await TransportScenario.CreateAsync(seed: Seed);
        var replica = scenario.Open(local: false);
        var lane = scenario.Transport.Budget(scenario.Remote);
        var jitter = new Random(Seed);
        var backoff = TimeSpan.FromMilliseconds(10);
        TimeSpan NextDelay()
        {
            var delay = backoff * (0.5 + jitter.NextDouble() / 2);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.TicksPerSecond));
            return delay;
        }

        var refusals = 2;
        scenario.Grain.Answer = _ => Interlocked.Decrement(ref refusals) >= 0 ? ReplicaWriteOutcome.Backpressure.Instance : ReplicaWriteOutcome.Written.Instance;
        var timers = scenario.Time.Timers;
        var repeated = WriteAsync(replica, 0, 10, token);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await ReplicaCluster.EventuallyAsync(() => scenario.Time.Timers > timers);
            timers = scenario.Time.Timers;
            Assert.Equal((attempt, 0), (scenario.Grain.Sent, lane.OutstandingCalls));
            scenario.Time.Advance(NextDelay());
        }

        Assert.Equal(ReplicaWriteOutcome.Written.Instance, (await repeated.WaitAsync(Bound, token)).Value);
        Assert.Equal(3, scenario.Grain.Sent);

        // The oracle: from the call's start, an attempt follows each refusal after its backoff while the deadline left
        // exceeds that backoff; the jitter continues from the first call's draws, and the backoff starts again.
        scenario.Grain.Answer = _ => ReplicaWriteOutcome.Backpressure.Instance;
        backoff = TimeSpan.FromMilliseconds(10);
        var sent = scenario.Grain.Sent;
        var left = TransportScenario.Deadline;
        var spent = WriteAsync(replica, 1, 10, token);
        var expected = 1;
        while (true)
        {
            await scenario.Grain.WaitSentAsync(sent + expected);
            var delay = NextDelay();
            if (left <= delay)
            {
                break;
            }

            await ReplicaCluster.EventuallyAsync(() => scenario.Time.Timers > timers);
            timers = scenario.Time.Timers;
            Assert.Equal(0, lane.OutstandingCalls);
            scenario.Time.Advance(delay);
            left -= delay;
            expected++;
        }

        Assert.Equal(ReplicaWriteOutcome.Backpressure.Instance, (await spent.WaitAsync(Bound, token)).Value);
        Assert.Equal(sent + expected, scenario.Grain.Sent); Assert.InRange(expected, 10, 100);
    }

    /// <summary>
    /// Regression test (review of row 4.7): a call's deadline runs from when it was made, its wait for budget included, so
    /// a call granted just before its deadline and answered <c>Backpressure</c> reports it without another attempt.
    /// </summary>
    [Fact]
    public async Task ACallsBackpressureDeadlineIncludesItsWaitInTheQueue()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var replica = scenario.Open(local: false);
        scenario.Grain.Answer = _ => ReplicaWriteOutcome.Backpressure.Instance;
        Task<ReplicaWriteOutcome> write;
        using (await scenario.Transport.Budget(scenario.Remote).EnterAsync(TransportScenario.Budget, scenario.DeadlineFromNow(), token))
        {
            write = WriteAsync(replica, 0, 10, token);
            scenario.Time.Advance(TransportScenario.Deadline - TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal(ReplicaWriteOutcome.Backpressure.Instance, (await write.WaitAsync(Bound, token)).Value);
        Assert.Equal(1, scenario.Grain.Sent);
    }

    /// <summary>OR-I14: a <c>NotThisNode</c> answer and a placement refusal each fail as unavailable and request a registry re-read.</summary>
    [Fact]
    public async Task NotThisNodeAndAPlacementRefusalRequestARegistryReread()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var replica = scenario.Open(local: false);
        scenario.Grain.Answer = _ => NotThisNode.Instance;
        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => WriteAsync(replica, 0, 1, token));
        Assert.Equal(1, scenario.Invalidations);

        scenario.Grain.Answer = _ => new NodePlacementRefusedException(scenario.Remote, registered: null);
        var refusal = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => replica.ReadTailAsync(Slot.Zero, seal: false, token).AsTask());
        _ = Assert.IsType<NodePlacementRefusedException>(refusal.InnerException);
        Assert.Equal(2, scenario.Invalidations);
    }

    /// <summary>
    /// orleans.md §5.12, OR-I15, OR-I16: one case per row of the result table, each produced at the callee or in Orleans
    /// through the grain double, or at the caller, and reported to the caller as the table states. The rows a callee's
    /// answer produces are those <see cref="OrleansReplicaTransportTests"/> runs across silos too.
    /// </summary>
    [Fact]
    public async Task EveryResultReachesTheCallerAsSpecified()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var replica = scenario.Open(local: false);
        var context = new ReplicaResultTable.Context(replica, TransportScenario.ReadReply);
        var rows = new List<ReplicaResultTable.RowReport>();
        foreach (var row in ReplicaResultTable.AnswerRows(TransportScenario.ReadReply))
        {
            scenario.Grain.Answer = row.Answer;
            rows.Add(await ReplicaResultTable.RunAsync(row, context));
        }

        scenario.Grain.Answer = _ => ReplicaWriteOutcome.Backpressure.Instance;
        rows.Add(new("Backpressure from the host's limits, its attempts spent", await ReplicaResultTable.ReportAsync(
            async () =>
            {
                var call = ReplicaResultTable.Write(context, token);
                while (!call.IsCompleted)
                {
                    scenario.Time.Advance(TimeSpan.FromSeconds(1));
                    await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, token);
                }

                return await call;
            },
            token)));

        scenario.Grain.Answer = _ => ReplicaWriteOutcome.Written.Instance;
        rows.Add(new("a payload above the payload limit, at the caller", await ReplicaResultTable.ReportAsync(() => ReplicaResultTable.Write(context, token, recovery: false, TransportScenario.PayloadLimit + 1), token)));
        rows.Add(new("the deadline passes in the node's replica queue", await ReplicaResultTable.ReportAsync(
            async () =>
            {
                using (await scenario.Transport.Budget(scenario.Remote).EnterAsync(TransportScenario.Budget, scenario.DeadlineFromNow(), token))
                {
                    var call = ReplicaResultTable.Write(context, token);
                    scenario.Time.Advance(TransportScenario.Deadline);
                    return await call;
                }
            },
            token)));

        scenario.Grain.Answer = null;
        using (var cancel = new CancellationTokenSource())
        {
            rows.Add(new("the caller's cancellation, once sent", await ReplicaResultTable.ReportAsync(
                async () =>
                {
                    var sent = scenario.Grain.Sent;
                    var call = ReplicaResultTable.Write(context, cancel.Token);
                    await scenario.Grain.WaitSentAsync(sent + 1);
                    await cancel.CancelAsync();
                    return await call;
                },
                cancel.Token)));
        }

        rows.Add(await ReplicaResultTable.CancelledWhileQueuedAsync(context, scenario.Transport.Budget(scenario.Remote)));
        scenario.Grain.ReleaseAll();
        rows.Add(await ReplicaResultTable.DisposedAsync(context));

        await Verify(rows);
    }

    /// <summary>Runs the double's rows a callee's answer produces, with the caller rows around any callee, for the TestingHost twin.</summary>
    internal static async Task<List<ReplicaResultTable.RowReport>> AnswerRowsOverTheDoubleAsync()
    {
        await using var scenario = await TransportScenario.CreateAsync();
        var replica = scenario.Open(local: false);
        var context = new ReplicaResultTable.Context(replica, TransportScenario.ReadReply);
        var rows = new List<ReplicaResultTable.RowReport>();
        foreach (var row in ReplicaResultTable.AnswerRows(TransportScenario.ReadReply))
        {
            scenario.Grain.Answer = row.Answer;
            rows.Add(await ReplicaResultTable.RunAsync(row, context));
        }

        scenario.Grain.Answer = null;
        rows.Add(await ReplicaResultTable.CancelledWhileQueuedAsync(context, scenario.Transport.Budget(scenario.Remote)));
        scenario.Grain.ReleaseAll();
        rows.Add(await ReplicaResultTable.DisposedAsync(context));
        return rows;
    }

    private static async Task<ReplicaWriteOutcome> WriteAsync(ILedgerReplica replica, long slot, int length, CancellationToken token)
    {
        using var payload = TransportScenario.Payload(length);
        return await replica.WriteAsync(new Slot(slot), payload, Slot.Zero, recovery: false, token);
    }

    private static async Task<object?> ReadValueAsync(ILedgerReplica replica, long slot)
    {
        var read = await replica.ReadAsync(new Slot(slot), Slot.Zero, TestContext.Current.CancellationToken);
        if (read.TryGetValue(out ArcBuffer batch))
        {
            batch.Dispose();
            return "data";
        }

        return read.Value;
    }

    /// <summary>
    /// Runs the order scenario on a node's data lane, returning the first violation of arrival order, prefixed "order", or
    /// null: with a 60-byte grant held, a 50-byte call heads the queue and a 10-byte call, which would fit, waits behind it.
    /// </summary>
    private static async Task<string?> OrderViolationAsync(CallBudgetMutant mutant, bool local)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync(mutant);
        scenario.Grain.Answer = _ => ReplicaWriteOutcome.Written.Instance;
        var replica = scenario.Open(local);
        var lane = scenario.Transport.Budget(scenario.Node(local));
        string? violation = null;
        Task<ReplicaWriteOutcome> large;
        Task<ReplicaWriteOutcome> small;
        using (await lane.EnterAsync(60, scenario.DeadlineFromNow(), token))
        {
            large = WriteAsync(replica, 1, 50, token);
            small = WriteAsync(replica, 2, 10, token);
            if (lane.Waiting != 2 || lane.OutstandingCalls != 1)
            {
                violation = string.Create(CultureInfo.InvariantCulture, $"order: {lane.Waiting} calls wait and {lane.OutstandingCalls} hold grants while a 60-byte grant holds the lane; both calls wait, the 50-byte one at the head.");
            }
        }

        var failures = new[] { await Record.ExceptionAsync(() => large.WaitAsync(Bound, token)), await Record.ExceptionAsync(() => small.WaitAsync(Bound, token)) };
        if (violation is null && failures.FirstOrDefault(static failure => failure is not null) is { } failure)
        {
            violation = $"order: a queued call failed with {failure.GetType().Name}.";
        }


        return violation;
    }

    /// <summary>
    /// Runs the deadline scenario on a node's data lane, returning the first violation of the deadline's contract, prefixed
    /// "deadline", or null: with the lane's bytes all held, a call waits until its deadline, then fails unsent with the
    /// queue timeout.
    /// </summary>
    private static async Task<string?> DeadlineViolationAsync(CallBudgetMutant mutant, bool local)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync(mutant);
        var replica = scenario.Open(local);
        var lane = scenario.Transport.Budget(scenario.Node(local));
        string? violation = null;
        Task<ReplicaWriteOutcome> waiting;
        using (await lane.EnterAsync(TransportScenario.Budget, scenario.DeadlineFromNow(), token))
        {
            waiting = WriteAsync(replica, 1, 10, token);
            if (waiting.IsCompleted || lane.Waiting != 1)
            {
                violation = "deadline: a call which lacked budget ended at once.";
            }
            else
            {
                scenario.Time.Advance(TransportScenario.Deadline - TimeSpan.FromTicks(1));
                if (lane.Waiting != 1)
                {
                    violation = "deadline: a call stopped waiting before its deadline passed.";
                }
                else
                {
                    scenario.Time.Advance(TimeSpan.FromTicks(1));
                }
            }

            var failure = await Record.ExceptionAsync(() => waiting.WaitAsync(Bound, token));
            if (violation is null && failure is not LedgerReplicaQueueTimeoutException)
            {
                violation = $"deadline: a call whose deadline passed ended with {failure?.GetType().Name ?? "no failure"}.";
            }
        }

        var sent = local ? (await scenario.Store.ListReplicasAsync(token)).Count : scenario.Grain.Sent;
        return violation ?? (sent == 0 ? null : "deadline: a call which timed out in the queue was sent.");
    }
}

using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The replica transport across <c>Orleans.TestingHost</c> silos, each over its own node root (orleans.md §5.12, §9): a
/// silo reaches its own node's replicas in process and every other node's through that node's
/// <see cref="ILedgerStoreGrain"/>, every carried value reaching the store unchanged (OR-I9, OR-I15); a call of an unknown
/// protocol version is refused and changes nothing; a node with no registered active silo fails as unavailable with no
/// activation; an activation beside another node's store answers <c>NotThisNode</c>, creates nothing, and deactivates
/// (OR-I14); a node restarted at a new address is reached again; a contiguous-tail read of a replica its node neither
/// holds nor dropped answers the tail sent, while an operation on a ledger its node dropped answers <c>Dropped</c>; and a
/// replica retains the contiguous tail a writer on one silo writes explicitly for callers on every silo. Every
/// row of §5.12's result table a callee's answer produces reaches the caller across silos as it does over the grain
/// double. The class's leak check counts the pages every callee's call rents, and each cluster ends quiescent.
/// </summary>
[Collection(TestingHostCollection.Name)]
public sealed class OrleansReplicaTransportTests : IAsyncLifetime
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 4 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly List<ILedgerReplica> _replicas = [];
    private ReplicaCluster? _cluster;

    private ReplicaCluster Cluster => _cluster!;

    private string Caller => Cluster.SiloNames[0];

    private string Callee => Cluster.SiloNames[1];

    public async ValueTask InitializeAsync()
    {
        _cluster = await ReplicaCluster.DeployAsync(2);
        _cluster.CalleeAccounting = _leaks;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var replica in _replicas)
        {
            await replica.DisposeAsync();
        }

        if (_cluster is not null)
        {
            await _cluster.DisposeAsync();
        }

        _leaks.Dispose();
    }

    /// <summary>OR-I9, OR-I15: the caller's own node in process and the callee's through its grain both answer as their stores do.</summary>
    [Fact(Timeout = 120_000)]
    public async Task EachNodesReplicaIsReachedInProcessOrThroughItsGrain()
    {
        var token = TestContext.Current.CancellationToken;
        var ledger = new LedgerId(11);
        foreach (var silo in new[] { Caller, Callee })
        {
            var replica = Open(silo, ledger);

            var written = Assert.IsType<ReplicaWriteOutcome.Written>((await WriteAsync(replica, 0, [1, 2, 3, 4])).Value);
            Assert.Equal(LedgerLeaseOptions.DefaultLeaseDuration, written.LeaseGrant);
            Assert.Equal([1, 2, 3, 4], await ReadAsync(replica, 0));
            Assert.True((await replica.ReadAsync(new Slot(0), 1, 2, Slot.Zero, token)).TryGetValue(out ArcBuffer range));
            using (range)
            {
                Assert.Equal([2, 3], range.ToArray());
            }

            Assert.True((await replica.ReadAsync(new Slot(1), Slot.Zero, token)).TryGetValue(out ReplicaRead.Missing? _));
            var sealedTail = Tail(await replica.ReadTailAsync(new Slot(1), seal: true, token));
            Assert.Equal((new Slot(1), true), (sealedTail.ContiguousTail, sealedTail.IsSealed));
            Assert.True(sealedTail.LeaseRemaining > TimeSpan.Zero && sealedTail.LeaseRemaining <= LedgerLeaseOptions.DefaultLeaseDuration);
            Assert.Same(ReplicaWriteOutcome.Sealed.Instance, (await WriteAsync(replica, 1, [5])).Value);
            Assert.Same(ReplicaWriteOutcome.Written.Instance, (await WriteAsync(replica, 1, [5], recovery: true)).Value);
            Assert.Equal([5], await ReadAsync(replica, 1));
            Assert.Equal([ledger], await Cluster.Node(silo).Store.ListReplicasAsync(token));

            // The caller's own node is reached in process, with no grain call; the callee's through its grain.
            Assert.Equal(0, Cluster.CalleeCalls(Caller));
            Assert.Equal(silo == Callee, Cluster.CalleeCalls(Callee) > 0);
        }

        Assert.Equal((0, 0L), (Cluster.Host(Callee).Calls, Cluster.Host(Callee).Bytes));
        Assert.True(await IsActiveAsync(Cluster.TestCluster.Client.GetGrain<IManagementGrain>(0), Cluster.Node(Callee).Node));
    }

    /// <summary>
    /// OR-I15: a remote call carries the ledger, the window, the slot, its contiguous tail, and the recovery flag to the
    /// callee's store unchanged: the callee's own replica retains the tail sent, holds the write, and refuses another window.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task EveryCarriedValueReachesTheCalleesStoreUnchanged()
    {
        var token = TestContext.Current.CancellationToken;
        var ledger = new LedgerId(12);
        var remote = Open(Callee, ledger);
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await WriteAsync(remote, 2, [9, 8], contiguousTail: 2)).Value);

        await using var own = Cluster.Node(Callee).Store.OpenReplica(ledger, Window);
        Assert.Equal(new LedgerReplicaTail(new Slot(2), IsSealed: false), Tail(await own.ReadTailAsync(Slot.Zero, seal: false, token)));
        Assert.Equal([9, 8], await ReadAsync(own, 2));

        var otherWindow = Cluster.Transport(Caller).Open(Cluster.Node(Callee).Node, ledger, new LedgerReplicaOptions { WindowSize = 5 });
        _replicas.Add(otherWindow);
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => otherWindow.ReadTailAsync(Slot.Zero, seal: false, token).AsTask());
    }

    /// <summary>OR-I18: a payload and a DATA reply of 40 KiB, longer than Orleans' 16 KiB pages, cross between silos unchanged.</summary>
    [Fact(Timeout = 120_000)]
    public async Task APayloadLongerThanOrleansPagesCrossesUnchanged()
    {
        var bytes = new byte[40 << 10];
        new Random(40).NextBytes(bytes);
        var remote = Open(Callee, new LedgerId(20));

        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await WriteAsync(remote, 0, bytes)).Value);
        Assert.Equal(bytes, await ReadAsync(remote, 0));
        Assert.True((await remote.ReadAsync(new Slot(0), 100, 20 << 10, Slot.Zero, TestContext.Current.CancellationToken)).TryGetValue(out ArcBuffer range));
        using (range)
        {
            Assert.Equal(bytes[100..((20 << 10) + 100)], range.ToArray());
        }
    }

    /// <summary>OR-I15: a callee refuses a call of a protocol version it does not speak, before anything else, and changes nothing.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ACallOfAnUnknownProtocolVersionIsRefusedAndChangesNothing()
    {
        var grain = Cluster.TestCluster.Client.GetGrain<ILedgerStoreGrain>(Cluster.Node(Callee).Node.Value);
        var request = new LedgerReplicaWriteRequest { Version = LedgerReplicaProtocol.Version + 1, Ledger = 13, WindowSize = 4, Payload = WireBytes.Of(1) };

        var answer = await grain.WriteAsync(request, TestContext.Current.CancellationToken);
        request.Payload.Dispose();

        Assert.Equal(ReplicaRefusal.UnsupportedVersion, Assert.IsType<ReplicaCallRefused>(answer.Value).Kind);
        Assert.Empty(await Cluster.Node(Callee).Store.ListReplicasAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// orleans.md §5.12, OR-I15, OR-I16: each row of the result table a callee's answer produces, answered at the callee
    /// across silos in place of its host, or around any callee at the caller, reaches the caller exactly as it does over
    /// the grain double, whose snapshot pins the table: answers and exceptions cross Orleans unchanged, and a reply the
    /// caller cannot decode surfaces as <see cref="InvalidDataException"/>.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheResultTableAcrossSilosMatchesTheGrainDoubles()
    {
        var replica = Open(Callee, new LedgerId(21));
        var transport = Cluster.Transport(Caller);
        var context = new ReplicaResultTable.Context(replica, transport.Limits.MaxReadReplyBytes);
        var rows = new List<ReplicaResultTable.RowReport>();
        var calleeNode = Cluster.Node(Callee).Node;
        try
        {
            foreach (var row in ReplicaResultTable.AnswerRows(transport.Limits.MaxReadReplyBytes))
            {
                Cluster.CalleeScript = row.WireAnswer ?? row.Answer;
                rows.Add(await ReplicaResultTable.RunAsync(row, context).WaitAsync(TestContext.Current.CancellationToken));
                transport.ClearStoppingIncarnation(calleeNode);
            }
        }
        finally
        {
            Cluster.CalleeScript = null;
            transport.ClearStoppingIncarnation(calleeNode);
        }

        rows.Add(await ReplicaResultTable.CancelledWhileQueuedAsync(context, transport.Budget(Cluster.Node(Callee).Node)));
        rows.Add(await ReplicaResultTable.DisposedAsync(context));

        Assert.Equal(await OrleansReplicaTransportCallerTests.AnswerRowsOverTheDoubleAsync(), rows);
    }

    /// <summary>The cluster's callee leak check counts the pages a callee's call keeps, and their return.</summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCalleesLeakCheckCountsThePagesACalleeKeeps()
    {
        var grain = Cluster.TestCluster.Client.GetGrain<IPageKeepingGrain>(0);
        var scope = _leaks.Scope();
        try
        {
            Cluster.CalleeAccounting = scope;
            await grain.KeepAsync().WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, scope.Outstanding);
            await grain.ReleaseAsync();
            Assert.Equal(0, scope.Outstanding);
        }
        finally
        {
            Cluster.CalleeAccounting = _leaks;
            scope.Dispose();
        }
    }

    /// <summary>OR-I14: a node the caller's registry names no active silo for fails as unavailable before any activation exists.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ANodeWithNoRegisteredSiloFailsAsUnavailableWithNoActivation()
    {
        var node = new NodeId(Guid.NewGuid());
        var replica = Cluster.Transport(Caller).Open(node, new LedgerId(14), Window);
        _replicas.Add(replica);

        var failure = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => WriteAsync(replica, 0, [1]).WaitAsync(TestContext.Current.CancellationToken));

        Assert.Null(failure.InnerException);
        Assert.Equal(0, Cluster.Transport(Caller).SenderCopyCount);
        Assert.False(await IsActiveAsync(Cluster.TestCluster.Client.GetGrain<IManagementGrain>(0), node));
    }

    /// <summary>
    /// OR-I14: a node the registry maps to another node's silo, as a defect or a directory race would place it, is
    /// answered <c>NotThisNode</c>, which creates nothing on that silo's store, and its activation deactivates.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnActivationBesideAnotherNodesStoreAnswersNotThisNodeCreatesNothingAndDeactivates()
    {
        var token = TestContext.Current.CancellationToken;
        var misplaced = new NodeId(Guid.NewGuid());
        var calleeSilo = Cluster.TestCluster.Silos.Single(silo => silo.Name == Callee).SiloAddress;
        var register = await Cluster.MetaStore.GetRegisterAsync(Cluster.Cluster, NodeRegistryValue.RegisterName, token);
        var store = Assert.NotNull((await NodeRegistryCodec.ReadAsync(register, token)).Value.ArchiveStore);
        await using (var committer = Committer(register))
        {
            _ = Assert.IsType<NodeRegistrationOutcome.Registered>((await committer.RegisterAsync(new NodeRegistryEntry(misplaced, calleeSilo), store, nodeToReplace: null, token)).Value);
        }

        Cluster.Resolver(Caller).RequestReread();
        await ReplicaCluster.EventuallyAsync(() => calleeSilo.Equals(Cluster.Resolver(Caller).Resolve(misplaced)));
        var replica = Cluster.Transport(Caller).Open(misplaced, new LedgerId(15), Window);
        _replicas.Add(replica);
        Assert.Empty(await Cluster.Node(Callee).Store.ListReplicasAsync(token));

        var failure = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => WriteAsync(replica, 0, [1]));
        Assert.Contains("NotThisNode", failure.Message, StringComparison.Ordinal);
        Assert.Empty(await Cluster.Node(Callee).Store.ListReplicasAsync(token));

        var management = Cluster.TestCluster.Client.GetGrain<IManagementGrain>(0);
        await EventuallyAsync(async () => !await IsActiveAsync(management, misplaced));
    }

    /// <summary>OR-I14: a node restarted at a new address registers there, and the caller's next calls reach it, its replica intact.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ANodeRestartedAtANewAddressIsReachedAgain()
    {
        var ledger = new LedgerId(16);
        var name = Callee;
        var node = Cluster.Node(name).Node;
        var replica = Open(Callee, ledger);
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await WriteAsync(replica, 0, [7])).Value);

        var callee = Cluster.TestCluster.Silos.Single(silo => silo.Name == name);
        var restarted = await Cluster.TestCluster.RestartSiloAsync(callee).WaitAsync(TestContext.Current.CancellationToken);
        await Cluster.WaitUntilResolvedAsync();
        Assert.Equal(node, Cluster.Node(restarted!.Name).Node);
        Assert.NotEqual(callee.SiloAddress, restarted.SiloAddress);

        byte[]? read = null;
        await EventuallyAsync(async () =>
        {
            try
            {
                read = await ReadAsync(replica, 0);
                return true;
            }
            catch (LedgerReplicaUnavailableException)
            {
                return false;
            }
        });
        Assert.Equal([7], read);
    }

    /// <summary>
    /// OR-I15: a contiguous-tail read of a replica its node neither holds nor dropped answers the tail sent, which passes
    /// the reply check; once the node dropped the ledger, every operation answers <c>Dropped</c> and changes nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnUnheldReplicaAnswersTheTailSentAndADroppedOneAnswersDropped()
    {
        var token = TestContext.Current.CancellationToken;
        var ledger = new LedgerId(17);
        var replica = Open(Callee, ledger);

        Assert.Equal(new LedgerReplicaTail(new Slot(3), IsSealed: false), Tail(await replica.ReadTailAsync(new Slot(3), seal: false, token)));
        Assert.Empty(await Cluster.Node(Callee).Store.ListReplicasAsync(token));

        await Cluster.Node(Callee).Store.DropReplicaAsync(ledger, token);
        Assert.Same(ReplicaWriteOutcome.Dropped.Instance, (await WriteAsync(replica, 0, [1])).Value);
        Assert.True((await replica.ReadAsync(new Slot(0), Slot.Zero, token)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));
        Assert.True((await replica.ReadTailAsync(Slot.Zero, seal: true, token)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));
        Assert.Empty(await Cluster.Node(Callee).Store.ListReplicasAsync(token));
    }

    /// <summary>
    /// LR-I6 across silos (ledger-protocol.md §8.2): an idle writer on one silo writes its contiguous tail explicitly to its
    /// ledger's replica on another silo's node, which retains it for every caller: the node's own silo, in process, and the
    /// writer's silo, through the node's grain, read it back without carrying it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReplicaRetainsAContiguousTailAcrossCallersOnSeveralSilos()
    {
        var token = TestContext.Current.CancellationToken;
        var ledger = new LedgerId(19);
        var node = Cluster.Node(Callee).Node;
        var time = new FakeTimeProvider();
        await using (var primary = new Ledger(
            ledger,
            LedgerQuorum.ForNodes(ledger, [node], [Cluster.Transport(Caller).Open(node, ledger, Window)], quorum: 1, Window, time, localNode: null),
            new LedgerLocation.Nodes([node])))
        await using (var writer = primary.CreateWriter(time))
        {
            for (var value = (byte)1; value <= 2; value++)
            {
                var slot = (await writer.AcquireAsync(token))!.Value;
                using var buffer = new ArcBufferWriter();
                buffer.Write([value]);
                using var payload = buffer.ConsumeSlice(1);
                Assert.Equal(new AppendOutcome(new AppendOutcome.Appended(slot)), await writer.AppendAsync(slot, payload, token));
            }

            time.Advance(LedgerWriter.ContiguousTailInterval);
            var own = Cluster.Transport(Callee).Open(node, ledger, Window);
            _replicas.Add(own);
            await EventuallyAsync(async () => Tail(await own.ReadTailAsync(Slot.Zero, seal: false, token)).ContiguousTail == new Slot(2));
        }

        var remote = Open(Callee, ledger);
        Assert.Equal(new LedgerReplicaTail(new Slot(2), IsSealed: false), Tail(await remote.ReadTailAsync(Slot.Zero, seal: false, token)));
    }

    /// <summary>
    /// OR-I16: a request delivered twice, or after a seal or a drop, as a delayed or duplicated message would be, leaves
    /// only states the store's rules allow: a duplicated write stores its one value, a writer write delayed past the seal
    /// answers <c>Sealed</c> and stores nothing, and a write or seal delayed past the node's drop creates nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task DelayedAndDuplicatedRequestsLeaveOnlyStatesTheContractAllows()
    {
        var token = TestContext.Current.CancellationToken;
        var grain = Cluster.TestCluster.Client.GetGrain<ILedgerStoreGrain>(Cluster.Node(Callee).Node.Value);
        LedgerReplicaWriteRequest Write(ulong ledger, long slot) => new()
        {
            Version = LedgerReplicaProtocol.Version,
            Ledger = ledger,
            WindowSize = Window.WindowSize,
            Slot = slot,
            Payload = WireBytes.Of((byte)(slot + 1)),
        };

        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await grain.WriteAsync(Write(18, 0), token)).Value);
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await grain.WriteAsync(Write(18, 0), token)).Value);
        var replica = Open(Callee, new LedgerId(18));
        Assert.Equal([1], await ReadAsync(replica, 0));

        _ = await replica.ReadTailAsync(Slot.Zero, seal: true, token);
        Assert.Same(ReplicaWriteOutcome.Sealed.Instance, (await grain.WriteAsync(Write(18, 1), token)).Value);
        Assert.True((await replica.ReadAsync(new Slot(1), Slot.Zero, token)).TryGetValue(out ReplicaRead.Missing? _));

        await Cluster.Node(Callee).Store.DropReplicaAsync(new LedgerId(19), token);
        Assert.Same(ReplicaWriteOutcome.Dropped.Instance, (await grain.WriteAsync(Write(19, 0), token)).Value);
        var seal = new LedgerReplicaTailRequest { Version = LedgerReplicaProtocol.Version, Ledger = 19, WindowSize = Window.WindowSize, Seal = true };
        Assert.Same(ReplicaWriteOutcome.Dropped.Instance, (await grain.ReadTailAsync(seal, token)).Value);
        Assert.Equal([new LedgerId(18)], await Cluster.Node(Callee).Store.ListReplicasAsync(token));
    }

    /// <summary>
    /// OR-I16: a call to a node whose silo stopped ends as an unknown outcome, <see cref="LedgerReplicaUnavailableException"/>,
    /// never as an answer such as <c>Missing</c>.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACallToAStoppedSiloFailsAsUnavailableNeverAsAnAnswer()
    {
        var replica = Open(Callee, new LedgerId(20));
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await WriteAsync(replica, 0, [1])).Value);

        await Cluster.TestCluster.KillSiloAsync(Cluster.TestCluster.Silos.Single(silo => silo.Name == Callee), TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => ReadAsync(replica, 0));
        _ = await Assert.ThrowsAsync<LedgerReplicaUnavailableException>(() => WriteAsync(replica, 1, [2]));
    }

    /// <summary>
    /// Regression test (#337), OR-I16: a remote call held at its callee, or in its caller's outgoing filter before Orleans
    /// registers its token, ends its caller's wait as soon as the caller cancels, with the caller's token, well within
    /// Orleans' response timeout, releasing the caller's grant and sender copy; its outcome is unknown, and the request
    /// held at its callee still lands once the hold ends. The <c>atCaller: true</c> row guards the transport's own end of
    /// the wait: Orleans ends a sent call's wait at its cancellation itself, so the <c>atCaller: false</c> row passes
    /// without it, and checks that the abandoned request lands.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancelledCallHeldInFlightEndsItsWaitAtOnceAndItsRequestStillLands(bool atCaller)
    {
        var token = TestContext.Current.CancellationToken;
        var replica = Open(Callee, new LedgerId(atCaller ? 22UL : 21UL));
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Hold(object _)
        {
            _ = arrived.TrySetResult();
            return release.Task;
        }

        if (atCaller)
        {
            Cluster.CallerHold = Hold;
        }
        else
        {
            Cluster.CalleeHold = Hold;
        }

        using var cancel = new CancellationTokenSource();
        using (var writer = new ArcBufferWriter())
        {
            writer.Write([7, 7]);
            using var payload = writer.ConsumeSlice(2);
            var write = replica.WriteAsync(new Slot(0), payload, Slot.Zero, recovery: false, cancel.Token).AsTask();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

            var started = TimeProvider.System.GetTimestamp();
            await cancel.CancelAsync();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(30), token));
            Assert.Equal(cancel.Token, canceled.CancellationToken);
            Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(5), "The cancelled call's wait outlasted its cancellation.");
        }

        var lane = Cluster.Transport(Caller).Budget(Cluster.Node(Callee).Node);
        Assert.Equal((0, 0L, 0), (lane.OutstandingCalls, lane.OutstandingBytes, Cluster.Transport(Caller).SenderCopyCount));

        Cluster.CallerHold = null;
        Cluster.CalleeHold = null;
        release.SetResult();
        if (atCaller)
        {
            // Orleans sends no request whose token is cancelled once the filter lets it go, so this one never leaves.
            return;
        }

        await EventuallyAsync(async () =>
        {
            var read = await replica.ReadAsync(new Slot(0), Slot.Zero, token);
            if (!read.TryGetValue(out ArcBuffer batch))
            {
                return false;
            }

            using (batch)
            {
                Assert.Equal([7, 7], batch.ToArray());
            }

            return true;
        });
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(TimeProvider.System.GetUtcNow() < deadline, "The condition did not hold in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Returns whether any silo's catalog holds an activation of <paramref name="node"/>'s <see cref="ILedgerStoreGrain"/>.</summary>
    private static async Task<bool> IsActiveAsync(IManagementGrain management, NodeId node) =>
        (await management.GetDetailedGrainStatistics()).Any(statistic => statistic.GrainId.Key.ToString() == node.Value.ToString("N"));

    private static LedgerReplicaTail Tail(ReplicaTailRead read) => read.TryGetValue(out LedgerReplicaTail tail) ? tail : throw new InvalidOperationException($"{read} holds no tail.");

    private static async Task<ReplicaWriteOutcome> WriteAsync(ILedgerReplica replica, long slot, byte[] bytes, bool recovery = false, long contiguousTail = 0)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        using var payload = writer.ConsumeSlice(bytes.Length);
        return await replica.WriteAsync(new Slot(slot), payload, new Slot(contiguousTail), recovery, TestContext.Current.CancellationToken);
    }

    private static async Task<byte[]> ReadAsync(ILedgerReplica replica, long slot)
    {
        var read = await replica.ReadAsync(new Slot(slot), Slot.Zero, TestContext.Current.CancellationToken);
        Assert.True(read.TryGetValue(out ArcBuffer batch), $"Slot {slot} answered {read}.");
        using (batch)
        {
            return batch.ToArray();
        }
    }

    private ILedgerReplica Open(string silo, LedgerId ledger)
    {
        var replica = Cluster.Transport(Caller).Open(Cluster.Node(silo).Node, ledger, Window);
        _replicas.Add(replica);
        return replica;
    }
}

/// <summary>A grain which keeps a pooled page between calls, for the callee leak check's self-test.</summary>
public interface IPageKeepingGrain : IGrainWithIntegerKey
{
    /// <summary>Rents a page and keeps it.</summary>
    Task KeepAsync();

    /// <summary>Returns the kept page.</summary>
    Task ReleaseAsync();
}

/// <inheritdoc cref="IPageKeepingGrain"/>
public sealed class PageKeepingGrain : Grain, IPageKeepingGrain
{
    private ArcBuffer _kept;

    /// <inheritdoc/>
    public Task KeepAsync()
    {
        using var writer = new ArcBufferWriter();
        writer.Write([1]);
        _kept = writer.ConsumeSlice(1);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ReleaseAsync()
    {
        _kept.Dispose();
        _kept = default;
        return Task.CompletedTask;
    }
}

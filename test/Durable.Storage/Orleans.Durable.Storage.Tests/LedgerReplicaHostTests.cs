using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The replica host behind <see cref="ILedgerStoreGrain"/> (orleans.md §5.12): it refuses an unknown protocol version
/// before anything else (OR-I15); answers <c>NotThisNode</c> for another node's key before it waits for the registration
/// or opens any replica (OR-I14); waits for the registration, propagating its failure, and refuses once its node stops
/// (OR-I2); refuses a payload or reply above the payload limit; and answers <c>Backpressure</c> before the store beyond
/// either of its bounds, each read reserving the reply its caller reserved, a longer batch answered <c>TooLarge</c> (OR-I17).
/// A call holds its admission while its store operation runs, whatever its caller's cancellation, and returns it when the
/// operation ends, a DATA reply holding it until Orleans first copies it; the store's own refusals and corruption answer as
/// values, and anything else it throws returns the admission and escapes.
/// </summary>
public sealed class LedgerReplicaHostTests : IDisposable
{
    private const int PayloadLimit = 64;
    private const int ReadReply = 16;
    private static readonly TimeSpan ReplyDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly OrleansBufferLedger _wire = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<LedgerReplicaHost> _hosts = [];

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        try
        {
            _wire.Dispose();
        }
        finally
        {
            _leaks.Dispose();
        }
    }

    [Fact]
    public async Task AnUnknownProtocolVersionIsRefusedBeforeAnythingElse()
    {
        // The node has no store and no registration: the version check precedes both.
        var host = Host(new LocalNode());
        var node = new NodeId(Guid.NewGuid());
        var token = TestContext.Current.CancellationToken;

        AssertRefused(ReplicaRefusal.UnsupportedVersion, (await host.WriteAsync(node, Write(slot: 0) with { Version = LedgerReplicaProtocol.Version + 1 }, token)).Value);
        AssertRefused(ReplicaRefusal.UnsupportedVersion, (await host.ReadAsync(node, Read(slot: 0) with { Version = 0 }, token)).Value);
        AssertRefused(ReplicaRefusal.UnsupportedVersion, (await host.ReadTailAsync(node, Tail() with { Version = LedgerReplicaProtocol.Version + 1 }, token)).Value);
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    /// <summary>OR-I14: a call keyed by another node is answered <c>NotThisNode</c> before the registration and creates nothing.</summary>
    [Fact]
    public async Task ACallKeyedByAnotherNodeAnswersNotThisNodeBeforeTheRegistration()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync(registered: false);
        var host = Host(scenario.Local);
        var other = new NodeId(Guid.NewGuid());

        Assert.Same(NotThisNode.Instance, (await host.WriteAsync(other, Write(slot: 0), token).WaitAsync(Bound, token)).Value);
        Assert.Same(NotThisNode.Instance, (await host.ReadAsync(other, Read(slot: 0), token).WaitAsync(Bound, token)).Value);
        Assert.Same(NotThisNode.Instance, (await host.ReadTailAsync(other, Tail() with { Seal = true }, token).WaitAsync(Bound, token)).Value);

        Assert.Empty(await scenario.Store.ListReplicasAsync(token));
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    /// <summary>
    /// OR-I2: a call waits for the node's registration and then runs; a failed registration escapes, an unknown outcome to
    /// its caller; and once the node stops, a call is refused, without effect.
    /// </summary>
    [Fact]
    public async Task ACallWaitsForTheRegistrationAndIsRefusedOnceTheNodeStops()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync(registered: false);
        var host = Host(scenario.Local);
        var node = scenario.Store.NodeId;

        var write = host.WriteAsync(node, Write(slot: 0), token);
        Assert.False(write.IsCompleted);
        Assert.Empty(await scenario.Store.ListReplicasAsync(token));
        scenario.Register();
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await write.WaitAsync(Bound, token)).Value);

        scenario.Local.Stop();
        AssertRefused(ReplicaRefusal.Stopping, (await host.WriteAsync(node, Write(slot: 1), token)).Value);
        AssertRefused(ReplicaRefusal.Stopping, (await host.ReadTailAsync(node, Tail() with { Seal = true }, token)).Value);
        Assert.False((await TailOfAsync(scenario)).IsSealed);

        await using var failed = await TransportScenario.CreateAsync(registered: false);
        var refused = Host(failed.Local).WriteAsync(failed.Store.NodeId, Write(slot: 0), token);
        failed.Local.RegistrationFailed(new InvalidOperationException("Refused."));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.WaitAsync(Bound, token));
        Assert.Empty(await failed.Store.ListReplicasAsync(token));
    }

    /// <summary>
    /// OR-I17: a payload or reserved reply above the payload limit, and a read whose reservation does not fit its range, is
    /// refused before the store; the store's own argument refusals answer as values too, each with no effect.
    /// </summary>
    [Fact]
    public async Task ArgumentsAreRefusedAsValuesWithNoEffect()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var host = Host(scenario.Local);
        var node = scenario.Store.NodeId;

        AssertRefused(ReplicaRefusal.Argument, (await host.WriteAsync(node, Write(slot: 0, WireBytes.Arc(new byte[PayloadLimit + 1])), token)).Value);
        AssertRefused(ReplicaRefusal.Argument, (await host.ReadAsync(node, Read(slot: 0) with { MaxReplyBytes = PayloadLimit + 1 }, token)).Value);
        AssertRefused(ReplicaRefusal.Argument, (await host.ReadAsync(node, Read(slot: 0) with { MaxReplyBytes = 0 }, token)).Value);
        AssertRefused(ReplicaRefusal.Argument, (await host.ReadAsync(node, Read(slot: 0) with { Offset = 1, Length = 2, MaxReplyBytes = 3 }, token)).Value);
        AssertRefused(ReplicaRefusal.Argument, (await host.WriteAsync(node, Write(slot: 0, WireArcBuffer.Empty), token)).Value);
        AssertRefused(ReplicaRefusal.ArgumentOutOfRange, (await host.WriteAsync(node, Write(slot: 0) with { WindowSize = 0 }, token)).Value);

        Assert.Empty(await scenario.Store.ListReplicasAsync(token));
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    /// <summary>
    /// OR-I17: reads reserve the reply their callers reserved at admission and hold it, with their call, until their DATA
    /// reply is serialized; a call beyond the host's bytes or calls is answered <c>Backpressure</c> before the store, so a
    /// write refused so stores nothing; and the bytes the host holds never pass its bound.
    /// </summary>
    [Fact]
    public async Task CallsBeyondEitherBoundAreAnsweredBackpressureBeforeTheStore()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var node = scenario.Store.NodeId;
        var host = Host(scenario.Local, maxCalls: 5);
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await host.WriteAsync(node, Write(slot: 0), token)).Value);

        var replies = new List<RemoteReplicaRead.Data>();
        for (var read = 0; read < 4; read++)
        {
            replies.Add(Assert.IsType<RemoteReplicaRead.Data>((await host.ReadAsync(node, Read(slot: 0) with { MaxReplyBytes = PayloadLimit }, token)).Value));
        }

        Assert.Equal((4, 4L * PayloadLimit), (host.Calls, host.Bytes));
        Assert.Equal(host.MaxBytes, host.Bytes);

        // The bytes are spent, so a write of one byte is refused; once the calls are spent too, so is a contiguous-tail
        // read, which holds no bytes.
        Assert.Same(ReplicaWriteOutcome.Backpressure.Instance, (await host.WriteAsync(node, Write(slot: 1, WireBytes.Arc(1)), token)).Value);
        using (var holding = host.TryAdmit(0))
        {
            Assert.NotNull(holding);
            Assert.Same(ReplicaWriteOutcome.Backpressure.Instance, (await host.ReadTailAsync(node, Tail(), token)).Value);
        }

        Assert.True((await scenario.Open(local: true).ReadAsync(new Slot(1), Slot.Zero, token)).TryGetValue(out ReplicaRead.Missing? _));

        // Serializing a reply returns its admission.
        _ = new RemoteReplicaReadConverter().ConvertToSurrogate(replies[0]);
        Assert.Equal((3, 3L * PayloadLimit), (host.Calls, host.Bytes));
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await host.WriteAsync(node, Write(slot: 1), token)).Value);
        replies.ForEach(static reply =>
        {
            reply.Admission!.Dispose();
            reply.Bytes.Dispose();
        });
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    /// <summary>
    /// OR-I17: a whole read of a batch longer than the reply its caller reserved returns none of it, answering
    /// <c>TooLarge</c> with the batch's length, and returns its admission.
    /// </summary>
    [Fact]
    public async Task AReadOfABatchLongerThanItsReservedReplyAnswersTooLarge()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var host = Host(scenario.Local);
        var node = scenario.Store.NodeId;
        _ = await host.WriteAsync(node, Write(slot: 0), token);

        Assert.Equal(new RemoteReplicaRead.TooLarge(4), (await host.ReadAsync(node, Read(slot: 0) with { MaxReplyBytes = 3 }, token)).Value);
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    /// <summary>
    /// OR-I17: a read whose answer carries no DATA, and every write and contiguous-tail read, returns its admission when
    /// its store operation ends; a ranged read reserves its range's length, held until its reply is serialized.
    /// </summary>
    [Fact]
    public async Task OnlyADataReplyHoldsItsAdmissionPastItsCall()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var node = scenario.Store.NodeId;
        var host = Host(scenario.Local);

        Assert.Same(ReplicaRead.Missing.Instance, (await host.ReadAsync(node, Read(slot: 0), token)).Value);
        _ = await host.WriteAsync(node, Write(slot: 0), token);
        _ = await host.ReadTailAsync(node, Tail() with { Seal = true }, token);
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));

        var ranged = Assert.IsType<RemoteReplicaRead.Data>((await host.ReadAsync(node, Read(slot: 0) with { Offset = 1, Length = 2, MaxReplyBytes = 2 }, token)).Value);
        Assert.Equal(new byte[] { 2, 3 }, ranged.Bytes.ToArray());
        ranged.Bytes.Dispose();
        Assert.Equal((1, 2L), (host.Calls, host.Bytes));
        _ = new RemoteReplicaReadConverter().ConvertToSurrogate(ranged);
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    /// <summary>
    /// Regression test (review of row 4.7): a write and a sealing contiguous-tail read hold their admissions while their
    /// store operations run, after their callers' cancellation too, since the store runs them to their end; each returns
    /// its admission, and answers the store's outcome, once its operation ends.
    /// </summary>
    [Fact]
    public async Task ACallHoldsItsAdmissionWhileItsStoreOperationRunsWhateverItsCallersCancellation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var node = scenario.Store.NodeId;
        var host = Host(scenario.Local);
        _ = await host.WriteAsync(node, Write(slot: 0), token);
        using var cancel = new CancellationTokenSource();
        scenario.Storage.Close();

        var write = host.WriteAsync(node, Write(slot: 1), cancel.Token);
        await ReplicaCluster.EventuallyAsync(() => scenario.Storage.Held > 0);
        var seal = host.ReadTailAsync(node, Tail() with { Seal = true }, cancel.Token);
        await ReplicaCluster.EventuallyAsync(() => host.Calls == 2);
        await cancel.CancelAsync();

        Assert.Equal((2, 4L), (host.Calls, host.Bytes));
        Assert.False(write.IsCompleted);
        scenario.Storage.Open();
        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await write.WaitAsync(Bound, token)).Value);
        Assert.True((await seal.WaitAsync(Bound, token)).Value is RemoteReplicaTailRead.Tail { IsSealed: true });
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    /// <summary>
    /// OR-I17: a DATA reply dropped unserialized holds its admission for the call's whole deadline, Orleans' response
    /// timeout, here 30 s: still held a tick before it, and returned once it passes.
    /// </summary>
    [Fact]
    public async Task AnUnserializedDataReplyHoldsItsAdmissionForTheWholeResponseTimeout()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var node = scenario.Store.NodeId;
        var host = Host(scenario.Local);
        _ = await host.WriteAsync(node, Write(slot: 0), token);

        var reply = Assert.IsType<RemoteReplicaRead.Data>((await host.ReadAsync(node, Read(slot: 0), token)).Value);
        Assert.Equal((1, ReadReply), (host.Calls, host.Bytes));

        _time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.False(reply.Admission!.IsReleased);
        Assert.Equal((1, ReadReply), (host.Calls, host.Bytes));
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.True(reply.Admission.IsReleased);
        reply.Bytes.Dispose();
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    /// <summary>
    /// OR-I18: the host releases the payload a write request carried on every path: a refusal before the store, an answer
    /// of <c>NotThisNode</c> or <c>Backpressure</c>, the store's outcome or refusal, a failure which escapes, and the
    /// caller's cancellation while the call waits at the serving gate.
    /// </summary>
    [Fact]
    public async Task TheHostReleasesAWritesPayloadOnEveryPath()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var node = scenario.Store.NodeId;
        var host = Host(scenario.Local, maxCalls: 1);

        await AssertReleasedAsync(request => host.WriteAsync(node, request with { Version = LedgerReplicaProtocol.Version + 1 }, token));
        await AssertReleasedAsync(request => host.WriteAsync(new NodeId(Guid.NewGuid()), request, token));
        await AssertReleasedAsync(request => host.WriteAsync(node, request, token), payloadLength: PayloadLimit + 1);
        await AssertReleasedAsync(request => host.WriteAsync(node, request with { WindowSize = 0 }, token));
        await AssertReleasedAsync(request => host.WriteAsync(node, request, token));
        using (host.TryAdmit(0))
        {
            await AssertReleasedAsync(request => host.WriteAsync(node, request with { Slot = 1 }, token));
        }

        await using (var unregistered = await TransportScenario.CreateAsync(registered: false))
        {
            using var cancel = new CancellationTokenSource();
            await AssertReleasedAsync(async request =>
            {
                var waiting = Host(unregistered.Local).WriteAsync(unregistered.Store.NodeId, request, cancel.Token);
                await cancel.CancelAsync();
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Bound, token));
            });
        }

        scenario.Local.Stop();
        await AssertReleasedAsync(request => host.WriteAsync(node, request with { Slot = 1 }, token));
        await using var disposed = await TransportScenario.CreateAsync();
        var failing = Host(disposed.Local);
        await disposed.Store.DisposeAsync();
        await AssertReleasedAsync(request => Assert.ThrowsAnyAsync<ObjectDisposedException>(() => failing.WriteAsync(disposed.Store.NodeId, request, token)));

        async Task AssertReleasedAsync(Func<LedgerReplicaWriteRequest, Task> call, int payloadLength = 4)
        {
            var payload = WireBytes.Arc(new byte[payloadLength]);
            await call(Write(slot: 0, payload));
            Assert.True(WireBytes.IsReleased(payload));
        }
    }

    /// <summary>A store which throws after a call's admission, other than a refusal or corruption, returns the admission, and the failure escapes.</summary>
    [Fact]
    public async Task AStoreWhichThrowsAfterAdmissionReturnsTheAdmission()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scenario = await TransportScenario.CreateAsync();
        var node = scenario.Store.NodeId;
        var host = Host(scenario.Local);
        await scenario.Store.DisposeAsync();

        _ = await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => host.WriteAsync(node, Write(slot: 0), token));
        _ = await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => host.ReadAsync(node, Read(slot: 0), token));
        _ = await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => host.ReadTailAsync(node, Tail(), token));
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    /// <summary>
    /// orleans.md §5.14: a diskless silo holds no LedgerStore, so <see cref="LedgerReplicaHost"/> refuses every replica
    /// call with <see cref="ReplicaRefusal.Argument"/> and releases the write payload with no effect.
    /// </summary>
    [Fact]
    public async Task ADisklessNodeRefusesEveryReplicaCallWithNoEffect()
    {
        var token = TestContext.Current.CancellationToken;
        var local = new LocalNode();
        var node = new NodeId(Guid.NewGuid());
        local.OpenedDiskless(node);
        local.Register(
            new NodeRegistryEntry(node, Orleans.Runtime.SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: true),
            new Engine.Archive.ArchiveStoreIdentity(Engine.Archive.ArchiveStoreKind.Lsm, new Engine.Archive.ArchiveStorePrefix("t"), new Engine.Archive.ArchiveStoreId(new UInt128(0, 1))),
            new Engine.Ledgers.Objects.ObjectLedgerStoreId(new UInt128(0, 2)));
        var host = Host(local);

        var payload = WireBytes.Arc([1, 2, 3, 4]);
        AssertRefused(ReplicaRefusal.Argument, (await host.WriteAsync(node, Write(slot: 0, payload), token)).Value);
        Assert.True(WireBytes.IsReleased(payload));
        AssertRefused(ReplicaRefusal.Argument, (await host.ReadAsync(node, Read(slot: 0), token)).Value);
        AssertRefused(ReplicaRefusal.Argument, (await host.ReadTailAsync(node, Tail(), token)).Value);
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    private static void AssertRefused(ReplicaRefusal kind, object? answer) => Assert.Equal(kind, Assert.IsType<ReplicaCallRefused>(answer).Kind);

    private static async Task<LedgerReplicaTail> TailOfAsync(TransportScenario scenario)
    {
        await using var replica = scenario.Store.OpenReplica(TransportScenario.Ledger, TransportScenario.Options);
        var read = await replica.ReadTailAsync(Slot.Zero, seal: false, TestContext.Current.CancellationToken);
        return read.TryGetValue(out LedgerReplicaTail tail) ? tail : throw new InvalidOperationException($"{read} holds no tail.");
    }

    private LedgerReplicaWriteRequest Write(long slot) => Write(slot, WireBytes.Arc(1, 2, 3, 4));

    /// <summary>Returns a write of <paramref name="payload"/>, which the host receives and must release.</summary>
    private LedgerReplicaWriteRequest Write(long slot, WireArcBuffer payload) => new()
    {
        Version = LedgerReplicaProtocol.Version,
        Ledger = TransportScenario.Ledger.Value,
        WindowSize = TransportScenario.Options.WindowSize,
        Slot = slot,
        Payload = _wire.Track(payload),
    };

    private static LedgerReplicaReadRequest Read(long slot) => new()
    {
        Version = LedgerReplicaProtocol.Version,
        Ledger = TransportScenario.Ledger.Value,
        WindowSize = TransportScenario.Options.WindowSize,
        Slot = slot,
        MaxReplyBytes = ReadReply,
    };

    private static LedgerReplicaTailRequest Tail() => new()
    {
        Version = LedgerReplicaProtocol.Version,
        Ledger = TransportScenario.Ledger.Value,
        WindowSize = TransportScenario.Options.WindowSize,
    };

    private LedgerReplicaHost Host(LocalNode local, int maxCalls = 8)
    {
        var host = new LedgerReplicaHost(
            local,
            TestReplicaLimits.Of(
                options =>
                {
                    options.MaxReplicaCalls = maxCalls;
                    options.MaxReplicaCallBytes = 4 * PayloadLimit;
                    options.MaxNodeCallBytes = 2 * PayloadLimit;
                    options.MaxReadReplyBytes = ReadReply;
                },
                ReplyDeadline),
            _time);
        _hosts.Add(host);
        return host;
    }
}

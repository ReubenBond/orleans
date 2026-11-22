using System.Buffers;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The node registry resolver (orleans.md §5.3, R1): it maps every registered node, a retired one's included, to its
/// registered silo and back, and an unregistered node to none; it takes a newer value a committer gives it; it re-reads the
/// register after a request once its second ends, shares one re-read among the requests made before it is dispatched, and retries a failed one at the same rate; it keeps serving the cached value
/// until a re-read replaces it; it re-reads on each membership change, restarting a watch which faults, and on a backoff
/// while an active silo has no entry; and disposal ends a waiting first read.
/// </summary>
public sealed class NodeRegistryResolverTests : IAsyncDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly ScriptedConditionalRegister _register = new();
    private readonly CountingTimeProvider _time = new();
    private readonly FakeMembership _membership = new();
    private readonly CountingRegister _reads;
    private readonly NodeRegistryResolver _resolver;

    public NodeRegistryResolverTests()
    {
        _reads = new(_register);
        _resolver = new(_ => ValueTask.FromResult<IConditionalRegister>(_reads), _membership, _time, NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _resolver.DisposeAsync();
        _leaks.Dispose();
    }

    /// <summary>
    /// Each node resolves to the silo its entry names, a retired node's included, which serves its replicas until its
    /// retirement gate opens (orleans.md §5.8); an unregistered node resolves to none.
    /// </summary>
    [Fact]
    public async Task ItMapsRegisteredNodesToTheirSilosAndBack()
    {
        await RegisterAsync(Entry(1, 2, ordinal: 0), Entry(2, 1), Entry(3, 1, ordinal: 0, host: 1));

        var registry = await _resolver.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3UL, registry.Revision);
        Assert.True(registry.Find(Node(1))!.IsRetired);
        Assert.Equal(Silo(1, 2), _resolver.Resolve(Node(1)));
        Assert.Equal(Silo(2, 1), _resolver.Resolve(Node(2)));
        Assert.Equal(Silo(1, 1), _resolver.Resolve(Node(3)));
        Assert.Null(_resolver.Resolve(Node(4)));
        Assert.Equal(Node(2), _resolver.NodeOf(Silo(2, 1)));
        Assert.Null(_resolver.NodeOf(Silo(2, 2)));
    }

    /// <summary>
    /// A value the controller grain's registry committer read or wrote raises the cached value when it is newer, and a later
    /// read of an older value, dispatched before it landed, leaves it (orleans.md §5.3).
    /// </summary>
    [Fact]
    public async Task AnAdvancedValueRaisesTheCacheAndAnOlderReadLeavesIt()
    {
        await RegisterAsync(Entry(1, 1));
        var read = await _resolver.GetAsync(TestContext.Current.CancellationToken);
        var newer = new NodeRegistryValue(read.Revision + 1, [Entry(1, 2)], read.ArchiveStore);

        _resolver.Advance(newer);
        _resolver.Advance(read);

        Assert.Same(newer, _resolver.Current);
        Assert.Equal(Silo(1, 2), _resolver.Resolve(Node(1)));
        _resolver.RequestReread();
        await AdvanceArmedAsync(NodeRegistryResolver.RereadInterval);
        await EventuallyAsync(() => _reads.Started == 2);
        Assert.Same(newer, _resolver.Current);
    }
    [Fact]
    public async Task ARequestedRereadIsDispatchedOnceItsSecondEnds()
    {
        await RegisterAsync(Entry(1, 1));
        _ = await _resolver.GetAsync(TestContext.Current.CancellationToken);
        await RegisterAsync(Entry(1, 2));
        var reads = _reads.Started;
        var timers = _time.Timers;

        _resolver.RequestReread();
        _resolver.RequestReread();
        await EventuallyAsync(() => _time.Timers == timers + 1);
        _time.Advance(NodeRegistryResolver.RereadInterval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(reads, _reads.Started);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => Silo(1, 2).Equals(_resolver.Resolve(Node(1))));
        Assert.Equal(reads + 1, _reads.Started);
    }

    [Fact]
    public async Task RequestsMadeBeforeAReadIsDispatchedShareIt()
    {
        await RegisterAsync(Entry(1, 1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _register.ReadGate = gate;
        var first = _resolver.GetAsync(TestContext.Current.CancellationToken);
        await EventuallyAsync(() => _reads.Started == 1);

        // These follow the first read's dispatch, so one more read serves them all.
        for (var request = 0; request < 10; request++)
        {
            _resolver.RequestReread();
        }

        _register.ReadGate = null;
        gate.SetResult();
        _ = await first;
        await AdvanceUntilAsync(() => _reads.Started == 2);
        for (var second = 0; second < 3; second++)
        {
            await AdvanceArmedAsync(NodeRegistryResolver.RereadInterval);
        }

        Assert.Equal(2, _reads.Started);
    }

    /// <summary>
    /// While re-reads fail, every resolution keeps the cached mapping, and the failed re-read is retried once per second.
    /// </summary>
    [Fact]
    public async Task AFailedRereadIsRetriedAtTheSameRateWhileTheCachedValueServes()
    {
        await RegisterAsync(Entry(1, 1));
        _ = await _resolver.GetAsync(TestContext.Current.CancellationToken);
        _register.ReadFailure = new TimeoutException("The MetaStore is unavailable.");
        var reads = _reads.Started;
        var timers = _time.Timers;

        _resolver.RequestReread();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await EventuallyAsync(() => _time.Timers == timers + attempt);
            _time.Advance(NodeRegistryResolver.RereadInterval - TimeSpan.FromMilliseconds(1));
            Assert.Equal(reads + attempt - 1, _reads.Started);
            _time.Advance(TimeSpan.FromMilliseconds(1));
            await EventuallyAsync(() => _reads.Started == reads + attempt);

            Assert.Equal(Silo(1, 1), _resolver.Resolve(Node(1)));
            Assert.True(_resolver.GetAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        }

        _register.ReadFailure = null;
        await RegisterAsync(Entry(1, 2));
        await AdvanceUntilAsync(() => Silo(1, 2).Equals(_resolver.Resolve(Node(1))));
    }

    [Fact]
    public async Task AMembershipChangeRequestsARereadAndAFaultedWatchRestarts()
    {
        await RegisterAsync(Entry(1, 1));
        _resolver.Start();
        await AdvanceUntilAsync(() => _resolver.Current is not null);
        await RegisterAsync(Entry(2, 1));

        _membership.Set();
        await AdvanceUntilAsync(() => Silo(2, 1).Equals(_resolver.Resolve(Node(2))));

        _membership.FailNextWatch();
        await AdvanceUntilAsync(() => _membership.Watches >= 3);
        await RegisterAsync(Entry(3, 1));
        _membership.Set();
        await AdvanceUntilAsync(() => Silo(3, 1).Equals(_resolver.Resolve(Node(3))));
    }

    /// <summary>
    /// While an active silo has no entry, as while its registration is pending, the resolver re-reads on a backoff from a
    /// tenth of a second, and stops once every active silo has one.
    /// </summary>
    [Fact]
    public async Task AnActiveSiloWithNoEntryIsReadForOnABackoff()
    {
        await RegisterAsync(Entry(1, 1));
        _membership.Set((Silo(1, 1), SiloStatus.Active), (Silo(2, 1), SiloStatus.Active));
        _ = await _resolver.GetAsync(TestContext.Current.CancellationToken);
        var reads = _reads.Started;

        // Within a second, the backoff from a tenth of a second reads more than once.
        for (var step = 0; step < 100; step++)
        {
            await SettleAsync();
            _time.Advance(NodeRegistryResolver.RereadInterval / 100);
        }

        Assert.True(_reads.Started - reads >= 2, $"{_reads.Started - reads} reads in a second.");
        await RegisterAsync(Entry(2, 1));
        await AdvanceUntilAsync(() => Silo(2, 1).Equals(_resolver.Resolve(Node(2))), NodeRegistryResolver.FirstBackoff);

        reads = _reads.Started;
        for (var second = 0; second < 3; second++)
        {
            _time.Advance(NodeRegistryResolver.RereadInterval);
            await SettleAsync();
        }

        Assert.Equal(reads, _reads.Started);
    }

    [Fact]
    public async Task ARefusalRequestsARereadOnlyWhenTheCacheIsStaleAgainstMembership()
    {
        await RegisterAsync(Entry(1, 1));
        _membership.Set((Silo(1, 1), SiloStatus.Active));
        _ = await _resolver.GetAsync(TestContext.Current.CancellationToken);
        var reads = _reads.Started;

        _resolver.RequestRereadIfStale();
        for (var second = 0; second < 3; second++)
        {
            _time.Advance(NodeRegistryResolver.RereadInterval);
            await SettleAsync();
        }

        Assert.Equal(reads, _reads.Started);
        Assert.False(_resolver.IsStale(_resolver.Current));
        _membership.Set((Silo(1, 1), SiloStatus.Active), (Silo(2, 1), SiloStatus.Active));
        Assert.True(_resolver.IsStale(_resolver.Current));
    }

    [Fact]
    public async Task DisposalEndsAWaitingFirstRead()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _register.ReadGate = gate;
        var first = _resolver.GetAsync(TestContext.Current.CancellationToken);
        await EventuallyAsync(() => _reads.Started == 1);

        await _resolver.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => first);
    }

    /// <summary>Lets the resolver's tasks run: a few turns of the thread pool, in real time.</summary>
    private static async Task SettleAsync()
    {
        for (var turn = 0; turn < 10; turn++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetUtcNow() < deadline, "The condition did not hold in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits until the resolver arms a timer, then advances the clock by <paramref name="step"/>.</summary>
    private async Task AdvanceArmedAsync(TimeSpan step)
    {
        var timers = _time.Timers;
        await SettleAsync();
        _time.Advance(step);
        _ = timers;
    }

    /// <summary>Advances the clock a step at a time, letting the resolver run, until the condition holds.</summary>
    private async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan? step = null)
    {
        for (var turn = 0; !condition(); turn++)
        {
            Assert.True(turn < 1000, "The condition did not hold in time.");
            await SettleAsync();
            _time.Advance(step ?? NodeRegistryResolver.RereadInterval / 10);
        }
    }

    private Task RegisterAsync(params NodeRegistryEntry[] entries) => RegisterInOrderAsync(_register, entries);

    /// <summary>Counts the reads the resolver starts, before the register's gate.</summary>
    private sealed class CountingRegister(ScriptedConditionalRegister register) : IConditionalRegister
    {
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _started);
            return register.ReadAsync(cancellationToken);
        }

        public ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, ReadOnlySequence<byte> value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The resolver reads only.");
    }
}

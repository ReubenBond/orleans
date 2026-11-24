using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Journals.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The gate through which the silo's data plane calls its Journals (orleans.md §5.2, OR-I8): its states only advance, a
/// call reaches the store only while it serves and is otherwise answered <c>Moved</c> naming no owner, and a call which
/// meets the store disposed as the runtime stops it gets that answer too, which the store's
/// <see cref="ObjectDisposedException"/> allows, since it asserts no effect (journals.md §2.2), while the same exception
/// from a gate which serves is the caller's.
/// </summary>
public sealed class LocalJournalsTests : IAsyncLifetime
{
    private static readonly LocalJournalWriteOutcome StoppedWrite = new(new JournalMoved(Owner: null));

    private static readonly NodeId Self = Node(1);

    private static readonly JournalReadOutcome StoppedRead = new(new JournalMoved(Owner: null));

    private static readonly JournalRowKey Key = JournalRowKey.Encode("k");

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly InMemoryArchiveStore _archive = new();
    private readonly NodePressure _pressure = new(TimeSpan.FromSeconds(1), new FakeTimeProvider(), NullLogger<NodePressure>.Instance);
    private LedgerReader? _reader;
    private LedgerWriterRegistry? _writers;
    private JournalStore? _store;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private JournalStore Store => _store!;

    private LedgerMapView View => _reader!.Map;

    public async ValueTask InitializeAsync()
    {
        var register = await new InMemoryMetaStore().GetRegisterAsync(Guid.NewGuid(), "ledger-map", Token);
        var view = await LedgerMapView.OpenAsync(register, Token);
        _reader = new LedgerReader(view, new NoReplicas(), TimeProvider.System, Self);
        _writers = new LedgerWriterRegistry(_reader, 32 << 20, LedgerWriterOptions.Default, TimeProvider.System, NullLoggerFactory.Instance);
        _store = new JournalStore(_writers, _archive, new JournalStoreOptions(), TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _store!.DisposeAsync();
        await _writers!.DisposeAsync();
        await _reader!.DisposeAsync();
        _archive.Dispose();
        _leaks.Dispose();
    }

    /// <summary>
    /// The states only advance: a gate checks until it is served or refused, a refused gate never serves, a stopped one
    /// neither serves nor is refused, and a gate serves one store; serving reports whether the gate serves.
    /// </summary>
    [Fact]
    public void TheStatesOnlyAdvance()
    {
        var served = NewJournals();
        Assert.Equal(LocalJournals.JournalsState.Checking, served.State);
        Assert.True(served.Serve(Store, View, Self));
        Assert.Equal(LocalJournals.JournalsState.Serving, served.State);
        served.Refuse();
        Assert.Equal(LocalJournals.JournalsState.Serving, served.State);
        _ = Assert.Throws<InvalidOperationException>(() => served.Serve(Store, View, Self));
        served.Stop();
        Assert.Equal(LocalJournals.JournalsState.Stopped, served.State);

        var refused = NewJournals();
        refused.Refuse();
        Assert.False(refused.Serve(Store, View, Self));
        Assert.Equal(LocalJournals.JournalsState.Refused, refused.State);
        refused.Stop();
        Assert.Equal(LocalJournals.JournalsState.Stopped, refused.State);

        var stopped = NewJournals();
        stopped.Stop();
        stopped.Refuse();
        Assert.False(stopped.Serve(Store, View, Self));
        Assert.Equal(LocalJournals.JournalsState.Stopped, stopped.State);
    }

    /// <summary>
    /// A call reaches the store only while the gate serves; otherwise it is answered <c>Moved</c> naming no owner at once.
    /// The store is disposed, so a call which reaches it throws its <see cref="ObjectDisposedException"/>, the caller's
    /// while the gate serves.
    /// </summary>
    [Fact]
    public async Task ACallReachesTheStoreOnlyWhileTheGateServes()
    {
        var journals = NewJournals();
        await Store.DisposeAsync();

        Assert.Equal(StoppedWrite, await WriteAsync(journals));
        Assert.Equal(StoppedRead, await journals.ReadAsync(Key, Token));
        Assert.True(journals.Serve(Store, View, Self));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => WriteAsync(journals));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => journals.ReadAsync(Key, Token));
        journals.Stop();
        Assert.Equal(StoppedWrite, await WriteAsync(journals));
        Assert.Equal(StoppedRead, await journals.ReadAsync(Key, Token));
    }

    /// <summary>
    /// A call the gate admitted which reaches the store after the runtime stopped the gate and disposed the store is
    /// answered <c>Moved</c> naming no owner, not with the store's <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact]
    public async Task ACallWhichMeetsTheStoreDisposedAsTheGateStopsGetsTheStoppedAnswer()
    {
        var journals = NewJournals();
        Assert.True(journals.Serve(Store, View, Self));
        journals.BeforeCall = async () =>
        {
            journals.Stop();
            await Store.DisposeAsync();
        };

        Assert.Equal(StoppedWrite, await WriteAsync(journals));
        Assert.Equal(StoppedRead, await journals.ReadAsync(Key, Token));
    }

    /// <summary>
    /// orleans.md §5.9, OR-I10: while the node is under disk pressure, a write to a key whose open ledger the node is
    /// primary of is answered <c>Throttled</c> before the store, and counted, and a write to a key of a ledger it only
    /// replicates reaches the store, uncounted; a read is never throttled.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyAThrottledPrimarysWriteIsAnsweredThrottledBeforeTheStore(bool primary)
    {
        var (view, committer) = await ViewListingAsync(primary ? Self : Node(2));
        await using var _ = committer;
        var journals = NewJournals();
        Assert.True(journals.Serve(Store, view, Self));
        _pressure.SetUnderPressure(true);
        var calls = 0;
        journals.BeforeCall = () =>
        {
            calls++;
            return Task.CompletedTask;
        };

        var write = await WriteAsync(journals);
        var read = await journals.ReadAsync(Key, Token);

        Assert.Equal(primary ? new LocalJournalWriteOutcome(LocalJournalWriteOutcome.Throttled.Instance) : StoppedWrite, write);
        Assert.Equal(StoppedRead, read);
        Assert.Equal(primary ? 1 : 2, calls);
        Assert.Equal(primary ? 1 : 0, _pressure.ThrottledWrites.Values.Sum());
    }

    /// <summary>
    /// A gate which stopped answers a write <c>Moved</c> naming no owner, not <c>Throttled</c>, though the node is under disk
    /// pressure and is the primary of the key's ledger: only a serving gate throttles.
    /// </summary>
    [Fact]
    public async Task AStoppedGateUnderPressureAnswersMovedNamingNoOwner()
    {
        var (view, committer) = await ViewListingAsync(Self);
        await using var _ = committer;
        var journals = NewJournals();
        Assert.True(journals.Serve(Store, view, Self));
        journals.Stop();
        _pressure.SetUnderPressure(true);

        Assert.Equal(StoppedWrite, await WriteAsync(journals));
    }

    /// <summary>
    /// journals.md §8.3, §8.6: when local <see cref="IJournalIndex"/> is supplied to <see cref="NodeRuntime"/>, it opens
    /// the node's <see cref="JournalStateDatabase"/> and <see cref="LedgerApplyLoop"/>, wires them into
    /// <see cref="NodeRuntime.Journals"/> so writes advance the database's watermark and owner reads observe them,
    /// flushes durably on clean shutdown, and disposes both cleanly without leaking pages; without a journal index, the
    /// runtime starts with <see cref="NodeRuntime.StateDatabase"/> unset and still serves writes and reads.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NodeRuntimeWiresOptionalJournalIndexIntoJournalStateDatabase(bool withJournalIndex)
    {
        var root = Path.Combine(Path.GetTempPath(), "clog-orleans-runtime-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            await using var ledgerStore = await LedgerStore.OpenAsync(
                new GatedWalStorage(),
                new LedgerStoreOptions { DirectoryPath = root, FileSize = 1 << 20, PreallocateFiles = false },
                Guid.NewGuid(),
                create: true,
                new LedgerStoreCheckpointOptions(),
                TimeProvider.System,
                Token);
            var register = await new InMemoryMetaStore().GetRegisterAsync(Guid.NewGuid(), "ledger-map", Token);
            var view = await LedgerMapView.OpenAsync(register, Token);
            await using var committer = new LedgerMapCommitter(register, 1 << 20, view);

            var transport = new SingleStoreTransport(ledgerStore);
            var disk = new NodeDisk(ledgerStore.NodeId, budgetBytes: 1L << 30, minimumFreeBytes: 1L << 20, walFileSize: 1 << 20, NullLogger<NodeDisk>.Instance);
            var archive = new SiloArchive.Lsm(new InMemoryObjectStore(), new ArchiveStorePrefix("t"));
            var index = withJournalIndex ? new InMemoryJournalIndex() : null;

            await using var runtime = await NodeRuntime.StartAsync(
                ledgerStore.NodeId,
                generation: 1,
                disk,
                _pressure,
                releaseDistance: 4,
                view,
                transport,
                ledgerStore,
                archive,
                SiloObjectLedgers.CreateUnconfigured(),
                isActive: static () => true,
                deadNodes: static () => new Dictionary<NodeId, int>(),
                activeNodes: static () => 1,
                maxBatchLength: 32 << 20,
                LedgerWriterOptions.Default,
                LedgerLeaseOptions.Default,
                new JournalStoreOptions(),
                journalFormat: "test-format",
                new ArchiveOptions(),
                new ArchiveLogOptions(),
                new ControlOptions(),
                blockedDrainRetryInterval: TimeSpan.FromSeconds(5),
                TimeProvider.System,
                NullLoggerFactory.Instance,
                index,
                cancellationToken: Token);

            Assert.Equal(withJournalIndex, runtime.StateDatabase is not null);

            var executor = new TransitionExecutor(runtime.Writers, NullLogger<TransitionExecutor>.Instance);
            var applied = Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(
                new Transition.Bootstrap(new TransitionChoice(ledgerStore.NodeId, [ledgerStore.NodeId], k: 1, chosenAt: 1, epoch: view.Current.Epoch)),
                committer.CommitAsync,
                Token)).Value);
            await runtime.Writers.ReconciledAsync(applied.Epoch, Token);
            var openLedger = view.Current.Ledgers.Single(static ledger => ledger.Status.Value is LedgerStatus.Open).Id;

            using (var payload = Orleans.Durable.Storage.Engine.Buffers.ArcBuffer.Wrap("wired-state-db"u8.ToArray()))
            using (var record = runtime.Journals.EncodeAppend(Key, JournalVersion.Origin, payload))
            {
                var writeOutcome = await runtime.Journals.WriteAsync(record, Token);
                var accepted = Assert.IsType<JournalWriteOutcome.Accepted>(writeOutcome.Value);
                Assert.Equal(JournalVersion.Origin.Next(), accepted.Version);
            }

            if (withJournalIndex)
            {
                var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(5);
                while (runtime.StateDatabase!.Watermark(openLedger) < new Slot(1))
                {
                    Assert.True(TimeProvider.System.GetUtcNow() < deadline, "Timed out waiting for wired LedgerApplyLoop to advance JournalStateDatabase watermark.");
                    await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Token);
                }
            }

            var readOutcome = await runtime.Journals.ReadAsync(Key, Token);
            using var read = Assert.IsType<JournalReadResult>(readOutcome.Value);
            Assert.True(read.Exists);
            var bytes = new List<byte>();
            await foreach (var chunk in read.ReadPayloadsAsync(Token))
            {
                using (chunk)
                {
                    bytes.AddRange(chunk.ToArray());
                }
            }

            Assert.Equal("wired-state-db"u8.ToArray(), bytes.ToArray());

            await runtime.StopJournalsAsync();
            await runtime.CancelAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Writes a <c>Delete</c> of <see cref="Key"/> through <paramref name="journals"/>.</summary>
    private static async Task<LocalJournalWriteOutcome> WriteAsync(LocalJournals journals)
    {
        using var record = JournalEntryRecord.Delete(Key, JournalVersion.Origin, DateTimeOffset.UnixEpoch);
        return await journals.WriteAsync(record, Token);
    }

    /// <summary>
    /// Returns a map view of its own, apart from the store's, which lists one open ledger over every key whose primary is
    /// <paramref name="primary"/>, and the committer which keeps it current.
    /// </summary>
    private static async Task<(LedgerMapView View, LedgerMapCommitter Committer)> ViewListingAsync(NodeId primary)
    {
        var register = await new InMemoryMetaStore().GetRegisterAsync(Guid.NewGuid(), "ledger-map", Token);
        var view = await LedgerMapView.OpenAsync(register, Token);
        var committer = new LedgerMapCommitter(register, 1 << 20, view);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await committer.CommitAsync(
            new LedgerMapChange.Install([], [new LedgerOpening(KeyRange.Root, [Self, Node(2), Node(3)], 4, new PrimaryClaim(primary, SequencerInstanceId.New()))], 1),
            Token)).Value);
        return (view, committer);
    }

    private LocalJournals NewJournals() => new(_pressure);

    private sealed class SingleStoreTransport(LedgerStore store) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options)
        {
            Assert.Equal(store.NodeId, node);
            return store.OpenReplica(ledger, options);
        }

        public async ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default)
        {
            Assert.Equal(store.NodeId, node);
            return new ReplicaHeartbeatRead(await store.HeartbeatReplicasAsync(ledgers, cancellationToken));
        }
    }
}

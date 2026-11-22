using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Journals.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Warm state, per-node key indexes, indexed tombstone expiry, snapshot requests, and <see cref="ArchiveReader.ReadStoreAsync"/>
/// over the LSM under deterministic Clockwork simulation (journals.md §8.2, §8.3, §8.6, §8.7, §10; archive-lsm.md §8.3;
/// JS-I6, JS-I7, JS-I9).
/// </summary>
/// <remarks>
/// <para>
/// Two primary-capable processes run over three <see cref="LedgerStoreNode"/> replicas, each with its own
/// <see cref="InMemoryJournalIndex"/> that survives process restarts, its own <see cref="JournalStateDatabase"/> and
/// <see cref="LedgerApplyLoop"/>, and its own <see cref="LsmArchiveStore"/> over a shared <see cref="SimulatedObjectStore"/>
/// and <see cref="ManifestView"/>. A tight <see cref="JournalStoreOptions.WarmStateBytes"/> budget forces continuous LRU
/// evictions of idle keys while an authoritative reference oracle checks every read, write, snapshot request, and expiry
/// across:
/// </para>
/// <list type="number">
/// <item><description>warm-state evictions, reloads, and §8.7 snapshot requests (<c>ReplaceRequested</c>);</description></item>
/// <item><description>restarts whose <see cref="InMemoryJournalIndex"/> durable watermark lags the ledger's tail (JS-I6, JS-I9);</description></item>
/// <item><description>takeovers and handoffs whose predecessor index on the new primary lags <c>T</c> (JS-I6, JS-I9);</description></item>
/// <item><description>a range handed away to the other primary, mutated there, and handed back (JS-I6);</description></item>
/// <item><description>LSM drains, ledger drops, compactions, key forgetting after tombstone expiry, and zero-object-store-call preloaded absence reads (archive-lsm.md §8.3, JS-I9); and</description></item>
/// <item><description>cold indexed tombstone expiry over <see cref="JournalStateDatabase"/>'s <c>tombstones</c> table (JS-I7).</description></item>
/// </list>
/// </remarks>
public sealed class JournalWarmStateSimulationTests
{
    private const int FirstSeed = 1;
    private const int SeedCount = 1;
    private const int ReplaySeed = 2;
    private const int MutantSeed = 1;
    private const int Window = 4;

    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<int, Lazy<ClusterSimulationRun>> Runs = new();

    private enum WarmStateMutant
    {
        None,
        SkipCatchUp,
        KeepWarmStateAcrossClaims,
        UncoveredDrop,
        IgnoreRetention,
        ApplyOnAdmit,
    }

    /// <summary>Gets the seed corpus.</summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>
    /// JS-I6, JS-I7, and JS-I9 hold against the oracle across evictions, lagging-watermark restarts, lagging-predecessor
    /// takeovers/handoffs, range hand-away-and-back, LSM drains/compactions/forgetting, and indexed tombstone expiry.
    /// </summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void JournalsWarmStateAndIndexesHoldTheirContractUnderSimulation(int seed) => _ = RunSeed(seed);

    /// <summary>Every required scenario path executes on each seed of the corpus.</summary>
    [Fact]
    public void TheCorpusExercisesEveryScenario()
    {
        string[] scenarios =
        [
            "evictions",
            "snapshot-request",
            "lagging-restart",
            "lagging-takeover",
            "hand-away-and-back",
            "lsm-compaction-forget",
            "preloaded-absence-zero-io",
            "indexed-expiry",
        ];

        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            var trace = RunSeed(seed).Trace;
            var missing = scenarios.Where(name => !trace.Any(entry => entry.Contains($"scenario:{name}", StringComparison.Ordinal))).ToArray();
            Assert.True(missing.Length == 0, $"Seed {seed} did not exercise: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    /// Every Part E mutant fails its own check under deterministic simulation: counting an index before catch-up (JS-I9),
    /// keeping warm state across claims (JS-I6), letting the LSM read pair a map with an uncovered dropped ledger
    /// (<c>uncovered-drop</c>), expiring a tombstone before retention passes (JS-I7), and updating warm state at admission
    /// before durability (JS-I2 / JS-I3).
    /// </summary>
    /// <param name="mutantName">The mutant to enable.</param>
    /// <param name="expectedFragment">A fragment expected in the oracle's failure message.</param>
    [Theory]
    [InlineData(nameof(WarmStateMutant.SkipCatchUp), "JS-I9")]
    [InlineData(nameof(WarmStateMutant.KeepWarmStateAcrossClaims), "JS-I6")]
    [InlineData(nameof(WarmStateMutant.UncoveredDrop), "uncovered-drop")]
    [InlineData(nameof(WarmStateMutant.IgnoreRetention), "JS-I7")]
    [InlineData(nameof(WarmStateMutant.ApplyOnAdmit), "JS-I2")]
    public void EachWarmStateMutantFailsItsCheck(string mutantName, string expectedFragment)
    {
        var mutant = Enum.Parse<WarmStateMutant>(mutantName);
        var failure = Assert.Throws<SimulationFailedException>(() =>
            SeededSimulation.Run(MutantSeed, environment => SimulateAsync(environment, mutant), minimize: false));
        Assert.Contains(expectedFragment, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The scenario replays deterministically from its seed and from its recorded artifact.</summary>
    [Fact]
    public void TheScenarioReplaysFromItsSeed()
    {
        var run = SeededSimulation.RunAndReplay(ReplaySeed, static environment => SimulateAsync(environment, WarmStateMutant.None));
        Assert.Contains(run.Trace, entry => entry.Contains("warm-state simulation verified", StringComparison.Ordinal));
    }

    private static ClusterSimulationRun RunSeed(int seed) =>
        Runs.GetOrAdd(seed, static s => new Lazy<ClusterSimulationRun>(() =>
            SeededSimulation.Run(s, static environment => SimulateAsync(environment, WarmStateMutant.None)))).Value;

    private static async Task SimulateAsync(SimulationEnvironment environment, WarmStateMutant mutant)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(3);

        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 3, token);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        _ = LedgerStoreInvariants.Register(environment, replicas);

        await using var mapRegister = new SimulatedConditionalRegister(environment, "ledger-map");
        await using var mapCommitter = new LedgerMapCommitter(mapRegister.Writer(0), int.MaxValue);
        await using var objects = new SimulatedObjectStore(environment, "archive-objects");
        var sharedManifestView = new ManifestView(objects);
        await using var manifestCommitter = new ManifestCommitter(objects, sharedManifestView, SimulationEnvironment.Clock, TimeSpan.Zero);

        var storage0 = new InMemoryJournalIndex();
        var storage1 = new InMemoryJournalIndex();

        var lsmOptions = new ArchiveOptions
        {
            GridBits = 1,
            MaxSectionsPerPartition = 2,
            SectionBytes = 4096,
            DrainMemoryBytes = 1024 * 1024,
            ViewAdoptionInterval = TimeSpan.FromMilliseconds(10),
        };

        var storeOptions = new JournalStoreOptions
        {
            WarmStateBytes = 2048,
            TombstoneRetention = Retention,
        };

        var scenario = new Scenario(
            environment,
            replicas,
            nodes,
            mapRegister,
            mapCommitter,
            manifestCommitter,
            objects,
            storage0,
            storage1,
            lsmOptions,
            storeOptions,
            mutant,
            token);
        await scenario.RunAsync();
    }

    private sealed class ClientTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            replicas.Open(node, ledger, options, client);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);
    }

    /// <summary>Authoritative per-journal reference state for verifying JS-I6, JS-I7, and JS-I9.</summary>
    private sealed class ReferenceJournal
    {
        public JournalVersion Version { get; set; } = JournalVersion.Origin;

        public bool Exists { get; set; }

        public bool Deleted { get; set; }

        public List<byte> Content { get; } = [];

        public long BaseLength { get; set; }

        public long BytesSinceBase { get; set; }

        public bool ExpectsReplaceRequest => Exists && BytesSinceBase > BaseLength;
    }

    private sealed class CountingObjectStore(IObjectStore inner) : IObjectStore
    {
        public int Calls { get; private set; }

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.CreateAsync(name, content, crc32c, cancellationToken);
        }

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.ReadAsync(name, offset, length, cancellationToken);
        }

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.GetPropertiesAsync(name, cancellationToken);
        }

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.ListAsync(prefix, startAfter, cancellationToken);
        }

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.DeleteAsync(name, cancellationToken);
        }

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.ReadVersionedAsync(name, cancellationToken);
        }

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(
            ObjectName name,
            ObjectVersion? expectedVersion,
            ArcBuffer content,
            uint? crc32c,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
        }
    }

    /// <summary>One simulated primary-capable process holding its map view, manifest view, LSM store, state DB, apply loop, and journal store.</summary>
    private sealed class PrimaryProcess : IAsyncDisposable
    {
        public string Name { get; }

        public NodeId NodeId { get; }

        public LedgerMapView MapView { get; }

        public ManifestView ManifestView { get; }

        public CountingObjectStore CountingObjects { get; }

        public LsmArchiveStore ArchiveStore { get; }

        public LedgerReader LedgerReader { get; }

        public ArchiveReader SlowPathReader { get; }

        public LedgerWriterRegistry Registry { get; }

        public TransitionExecutor Executor { get; }

        public JournalStateDatabase StateDb { get; private set; }

        public LedgerApplyLoop? ApplyLoop { get; private set; }

        public JournalStore Store { get; private set; }

        private PrimaryProcess(
            string name,
            NodeId nodeId,
            LedgerMapView mapView,
            IObjectStore objects,
            SimulatedLedgerReplicaTransport replicas,
            ArchiveOptions lsmOptions,
            WarmStateMutant mutant)
        {
            Name = name;
            NodeId = nodeId;
            MapView = mapView;
            ManifestView = new ManifestView(objects);
            CountingObjects = new CountingObjectStore(objects);
            ArchiveStore = new LsmArchiveStore(CountingObjects, ManifestView, new TailCache(CountingObjects, lsmOptions), lsmOptions, SimulationEnvironment.Clock)
            {
                UncoveredDropMutant = mutant == WarmStateMutant.UncoveredDrop,
            };

            LedgerReader = new LedgerReader(mapView, new ClientTransport(replicas, name), SimulationEnvironment.Clock, nodeId);
            SlowPathReader = new ArchiveReader(LedgerReader, ArchiveStore, SimulationEnvironment.Clock);
            Registry = new LedgerWriterRegistry(LedgerReader, 1 << 25, LedgerWriterOptions.Default, SimulationEnvironment.Clock, NullLoggerFactory.Instance, SmallSegments.Random(name));
            Executor = new TransitionExecutor(Registry, NullLogger<TransitionExecutor>.Instance);
            StateDb = null!;
            Store = null!;
        }

        private void AttachState(JournalStateDatabase stateDb, JournalStoreOptions storeOptions, WarmStateMutant mutant, LedgerTrace trace, bool startApplyLoop)
        {
            StateDb = stateDb;
            StateDb.SkipCatchUpMutant = mutant == WarmStateMutant.SkipCatchUp;
            if (startApplyLoop)
            {
                EnsureApplyLoopStarted();
            }

            Store = new JournalStore(Registry, ArchiveStore, storeOptions, SimulationEnvironment.Clock, StateDb)
            {
                KeepWarmStateAcrossClaimsMutant = mutant == WarmStateMutant.KeepWarmStateAcrossClaims,
                IgnoreRetentionMutant = mutant == WarmStateMutant.IgnoreRetention,
                ApplyOnAdmitMutant = mutant == WarmStateMutant.ApplyOnAdmit,
                OnAdmission = (ledger, writer) => trace.TraceWriter(writer, Name, ledger),
            };
        }

        public static async Task<PrimaryProcess> StartAsync(
            string name,
            NodeId nodeId,
            InMemoryJournalIndex storage,
            IConditionalRegister mapRegister,
            IObjectStore objects,
            SimulatedLedgerReplicaTransport replicas,
            ArchiveOptions lsmOptions,
            JournalStoreOptions storeOptions,
            WarmStateMutant mutant,
            bool startApplyLoop,
            CancellationToken cancellationToken)
        {
            var mapView = await LedgerMapView.OpenAsync(mapRegister, cancellationToken);
            var process = new PrimaryProcess(name, nodeId, mapView, objects, replicas, lsmOptions, mutant);
            try
            {
                var stateDb = await JournalStateDatabase.OpenAsync(storage, process.LedgerReader, new JournalIndexOptions(), SimulationEnvironment.Clock, cancellationToken);
                process.AttachState(stateDb, storeOptions, mutant, replicas.LedgerTrace, startApplyLoop);
                return process;
            }
            catch
            {
                await process.DisposeAsync();
                throw;
            }
        }

        public void EnsureApplyLoopStarted()
        {
            if (ApplyLoop is null)
            {
                ApplyLoop = new LedgerApplyLoop(LedgerReader, StateDb.Applier, SimulationEnvironment.Clock);
                StateDb.Attach(ApplyLoop);
            }
        }

        public async Task CatchUpLedgerAsync(LedgerId ledger, TimeProvider testTime, CancellationToken cancellationToken)
        {
            EnsureApplyLoopStarted();
            var meta = MapView.Current.Get(ledger) ?? throw new InvalidOperationException($"Ledger {ledger} missing from map.");
            var target = meta.Status.Value switch
            {
                LedgerStatus.Closed closed => closed.T,
                _ => Assert.IsType<LedgerTail>((await LedgerReader.ReadTailAsync(meta, cancellationToken)).Value).ContiguousTail,
            };

            ApplyLoop!.Wake(ledger);
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (StateDb.Watermark(ledger) >= target)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(2), testTime, cancellationToken);
            }

            throw new InvalidOperationException($"Ledger {ledger} did not catch up to {target}; watermark is {StateDb.Watermark(ledger)}.");
        }

        /// <summary>Simulates a crash: disposes all process memory without flushing the state DB's in-memory tail.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Store is not null)
            {
                await Store.DisposeAsync();
            }

            if (ApplyLoop is not null)
            {
                await ApplyLoop.DisposeAsync();
            }

            if (StateDb is not null)
            {
                await StateDb.DisposeAsync();
            }

            await Registry.DisposeAsync();
            await LedgerReader.DisposeAsync();
        }
    }

    private sealed class Scenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        IReadOnlyList<LedgerStoreNode> nodes,
        SimulatedConditionalRegister mapRegister,
        LedgerMapCommitter mapCommitter,
        ManifestCommitter manifestCommitter,
        SimulatedObjectStore objects,
        InMemoryJournalIndex storage0,
        InMemoryJournalIndex storage1,
        ArchiveOptions lsmOptions,
        JournalStoreOptions storeOptions,
        WarmStateMutant mutant,
        CancellationToken token)
    {
        private readonly Dictionary<string, ReferenceJournal> _oracle = [with(StringComparer.Ordinal)];
        private readonly ImmutableArray<NodeId> _replicaSet = [.. nodes.Select(static n => n.NodeId)];
        private LedgerMapValue _lastObservedMap = LedgerMapValue.Empty;
        private int _processGen;

        private void RecordMapCloses(LedgerMapValue current)
        {
            if (current.Epoch > _lastObservedMap.Epoch)
            {
                replicas.LedgerTrace.RecordCloses(_lastObservedMap, current, node => nodes.Single(candidate => candidate.NodeId == node).Address);
                _lastObservedMap = current;
            }
        }

        public async Task RunAsync()
        {
            var p0 = await StartProcessAsync("p0", nodes[0].NodeId, storage0, startApplyLoop: true);
            var p1 = await StartProcessAsync("p1", nodes[1].NodeId, storage1, startApplyLoop: true);

            try
            {
                // Bootstrap the root ledger with p0 as primary.
                await BootstrapRootAsync(p0);
                await SyncViewsAsync(p0, p1);

                // Phase 1: Warm-state evictions (tight WarmStateBytes=2048, ~3 entries) & §8.7 snapshot requests.
                await RunEvictionsAndSnapshotRequestsAsync(p0);

                // Phase 2: Restart with lagging durable watermark (JS-I6, JS-I9).
                p0 = await RunLaggingWatermarkRestartAsync(p0, p1);

                // Phase 3: Takeover / handoff whose predecessor index on the new primary lags T (JS-I6, JS-I9).
                p1 = await RunLaggingPredecessorTakeoverAsync(p0, p1);

                // Phase 4: Range handed away to p1, mutated there, and handed back to p0 (JS-I6).
                await RunHandAwayAndBackAsync(p0, p1);

                // Phase 5: Indexed tombstone expiry & JS-I7 checks (including cold evicted/restarted tombstones & ApplyOnAdmit).
                await RunIndexedTombstoneExpiryAsync(p0, p1);

                // Phase 6: LSM drains, ledger drops, compactions, key forgetting, UncoveredDrop check, and zero-IO preloaded absence read.
                await RunLsmCompactionForgettingAndPreloadedAbsenceAsync(p0, p1);

                // Final full-oracle sweep of every tracked journal.
                await VerifyAllJournalsAsync(p0);
                environment.Trace.Record("warm-state simulation verified");
            }
            finally
            {
                await p0.DisposeAsync();
                await p1.DisposeAsync();
            }
        }

        private async Task<PrimaryProcess> StartProcessAsync(string prefix, NodeId nodeId, InMemoryJournalIndex storage, bool startApplyLoop)
        {
            var gen = ++_processGen;
            return await PrimaryProcess.StartAsync(
                string.Create(CultureInfo.InvariantCulture, $"{prefix}-g{gen}"),
                nodeId,
                storage,
                mapRegister,
                objects,
                replicas,
                lsmOptions,
                storeOptions,
                mutant,
                startApplyLoop,
                token);
        }

        private async Task BootstrapRootAsync(PrimaryProcess primary)
        {
            var epoch = primary.MapView.Current.Epoch;
            var choice = new TransitionChoice(primary.NodeId, _replicaSet, Window, 0, epoch);
            var outcome = await primary.Executor.ExecuteAsync(new Transition.Bootstrap(choice), mapCommitter.CommitAsync, token);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(outcome.Value);
            _ = await primary.MapView.ReadAsync(token);
            RecordMapCloses(primary.MapView.Current);
            await primary.Registry.ReconciledAsync(primary.MapView.Current.Epoch, token);
            Assert.True(primary.MapView.Current.Epoch > epoch);
        }

        private async Task ReplaceRootAsync(PrimaryProcess executorProcess, NodeId newPrimary)
        {
            _ = await executorProcess.MapView.ReadAsync(token);
            var open = executorProcess.MapView.Current.Ledgers.Single(static l => l.Status.Value is LedgerStatus.Open);
            var choice = new TransitionChoice(newPrimary, _replicaSet, Window, 0, executorProcess.MapView.Current.Epoch);
            var outcome = await executorProcess.Executor.ExecuteAsync(new Transition.Replace(open.Id, choice), mapCommitter.CommitAsync, token);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(outcome.Value);
            _ = await executorProcess.MapView.ReadAsync(token);
            RecordMapCloses(executorProcess.MapView.Current);
            await executorProcess.Registry.ReconciledAsync(executorProcess.MapView.Current.Epoch, token);
        }

        private async Task SyncViewsAsync(params PrimaryProcess[] processes)
        {
            foreach (var proc in processes)
            {
                _ = await proc.MapView.ReadAsync(token);
                await proc.Registry.ReconciledAsync(proc.MapView.Current.Epoch, token);
            }
        }

        private ReferenceJournal Ref(string key)
        {
            if (!_oracle.TryGetValue(key, out var state))
            {
                state = new ReferenceJournal();
                _oracle[key] = state;
            }

            return state;
        }

        private async Task<JournalWriteOutcome.Accepted> AppendAndCheckAsync(PrimaryProcess owner, string key, string payload)
        {
            var state = Ref(key);
            var bytes = Encoding.UTF8.GetBytes(payload);
            using var buffer = ArcBuffer.Wrap(bytes);
            using var record = owner.Store.EncodeAppend(JournalRowKey.Encode(key), state.Version, buffer);
            var outcome = await owner.Store.WriteAsync(record, token);
            var accepted = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value);

            state.Version = accepted.Version;
            state.Exists = true;
            state.Deleted = false;
            state.Content.AddRange(bytes);
            state.BytesSinceBase += bytes.Length;

            Assert.Equal(state.ExpectsReplaceRequest, accepted.ReplaceRequested);
            return accepted;
        }

        private async Task<JournalWriteOutcome.Accepted> ReplaceAndCheckAsync(PrimaryProcess owner, string key, string payload)
        {
            var state = Ref(key);
            var bytes = Encoding.UTF8.GetBytes(payload);
            using var buffer = ArcBuffer.Wrap(bytes);
            using var record = owner.Store.EncodeReplace(JournalRowKey.Encode(key), state.Version, buffer);
            var outcome = await owner.Store.WriteAsync(record, token);
            var accepted = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value);

            state.Version = accepted.Version;
            state.Exists = true;
            state.Deleted = false;
            state.Content.Clear();
            state.Content.AddRange(bytes);
            state.BaseLength = bytes.Length;
            state.BytesSinceBase = 0;

            Assert.False(accepted.ReplaceRequested);
            return accepted;
        }

        private async Task<JournalWriteOutcome.Accepted> DeleteAndCheckAsync(PrimaryProcess owner, string key)
        {
            var state = Ref(key);
            using var record = owner.Store.EncodeDelete(JournalRowKey.Encode(key), state.Version);
            var outcome = await owner.Store.WriteAsync(record, token);
            var accepted = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value);

            state.Version = accepted.Version;
            state.Exists = false;
            state.Deleted = true;
            state.Content.Clear();
            state.BaseLength = 0;
            state.BytesSinceBase = 0;

            Assert.False(accepted.ReplaceRequested);
            return accepted;
        }

        private async Task ReadAndCheckAsync(PrimaryProcess owner, string key, string context, bool allowRebase = false, bool checkSlowPath = true)
        {
            var expected = Ref(key);
            var rowKey = JournalRowKey.Encode(key);
            var outcome = await owner.Store.ReadAsync(rowKey, token);
            using var result = Assert.IsType<JournalReadResult>(outcome.Value);

            var actualBytes = new List<byte>();
            await foreach (var chunk in result.ReadPayloadsAsync(token))
            {
                using (chunk)
                {
                    actualBytes.AddRange(chunk.ToArray());
                }
            }

            if (checkSlowPath)
            {
                using var slowRead = await owner.SlowPathReader.ReadAsync(rowKey.Memory, token);
                var slowState = JournalState.Fold(slowRead);
                var slowExists = slowState?.Exists ?? false;
                var slowVersion = slowState?.CurrentVersion ?? JournalVersion.Origin;
                var slowContent = slowExists ? ArchiveOracle.Fold(slowRead.Prefix, slowRead.Rows).Content : [];

                if (result.Exists != slowExists || result.CurrentVersion != slowVersion || !actualBytes.SequenceEqual(slowContent))
                {
                    throw new InvalidOperationException(
                        $"{context}: indexed JournalStore.ReadAsync for '{key}' (Exists={result.Exists}, Version={result.CurrentVersion}, Bytes={actualBytes.Count}) disagreed with unindexed slow path (Exists={slowExists}, Version={slowVersion}, Bytes={slowContent.Length}).");
                }
            }

            if (result.Exists != expected.Exists)
            {
                throw new InvalidOperationException($"{context}: key '{key}' Exists={result.Exists}, expected {expected.Exists}.");
            }

            if (expected.Exists)
            {
                if (result.CurrentVersion != expected.Version)
                {
                    // After all closed ledgers are drained and dropped into the LSM, a key with no live rows rebases
                    // its version to (NextToken, 0) (journals.md §4.2, JS-I10) and its LSM archive prefix becomes its
                    // new base (BaseLength = ContentLength, BytesSinceBase = 0; journals.md §8.7). The slow-path
                    // comparison above already verified result.CurrentVersion equals the unindexed archive fold.
                    if (allowRebase && result.CurrentVersion.Count == 0 && result.CurrentVersion.Base != JournalVersion.Origin.Base)
                    {
                        expected.Version = result.CurrentVersion;
                        expected.BaseLength = expected.Content.Count;
                        expected.BytesSinceBase = 0;
                    }
                    else
                    {
                        throw new InvalidOperationException($"{context}: key '{key}' Version={result.CurrentVersion}, expected {expected.Version}.");
                    }
                }

                if (!actualBytes.SequenceEqual(expected.Content))
                {
                    throw new InvalidOperationException(
                        $"{context}: key '{key}' content='{Encoding.UTF8.GetString([.. actualBytes])}', expected '{Encoding.UTF8.GetString([.. expected.Content])}'.");
                }

                if (result.ReplaceRequested != expected.ExpectsReplaceRequest)
                {
                    throw new InvalidOperationException($"{context}: key '{key}' ReplaceRequested={result.ReplaceRequested}, expected {expected.ExpectsReplaceRequest}.");
                }
            }
            else if (actualBytes.Count != 0)
            {
                throw new InvalidOperationException($"{context}: absent key '{key}' returned {actualBytes.Count} bytes.");
            }
            else if (expected.Deleted)
            {
                if (result.CurrentVersion != expected.Version)
                {
                    throw new InvalidOperationException($"{context}: deleted key '{key}' Version={result.CurrentVersion}, expected {expected.Version}.");
                }
            }
            else if (result.CurrentVersion != expected.Version)
            {
                // Update oracle version when an expired/forgotten key's version moved (validated against slow path above).
                expected.Version = result.CurrentVersion;
            }
        }

        private async Task RunEvictionsAndSnapshotRequestsAsync(PrimaryProcess p0)
        {
            // Write 6 keys under WarmStateBytes=2048 (~3 entries) so LRU evictions fire continuously.
            for (var i = 0; i < 6; i++)
            {
                var key = string.Create(CultureInfo.InvariantCulture, $"evict-{i}");
                _ = await ReplaceAndCheckAsync(p0, key, string.Create(CultureInfo.InvariantCulture, $"base-{i}"));
            }

            // Append past baseLength on evict-0 to trigger ReplaceRequested (§8.7).
            var triggered = await AppendAndCheckAsync(p0, "evict-0", "-overflow-payload");
            Assert.True(triggered.ReplaceRequested);
            await ReadAndCheckAsync(p0, "evict-0", "JS-I6: snapshot request read");
            environment.Trace.Record("scenario:snapshot-request");

            // Replace clears ReplaceRequested.
            var reset = await ReplaceAndCheckAsync(p0, "evict-0", "compacted-snapshot");
            Assert.False(reset.ReplaceRequested);

            // Re-read all 6 keys (forcing reloads of evicted keys) and verify against oracle.
            for (var i = 0; i < 6; i++)
            {
                await ReadAndCheckAsync(p0, string.Create(CultureInfo.InvariantCulture, $"evict-{i}"), "JS-I6: post-eviction read");
            }

            environment.Trace.Record("scenario:evictions");
        }

        private async Task<PrimaryProcess> RunLaggingWatermarkRestartAsync(PrimaryProcess p0, PrimaryProcess p1)
        {
            var openBefore = p0.MapView.Current.Ledgers.Single(static l => l.Status.Value is LedgerStatus.Open).Id;
            await p0.CatchUpLedgerAsync(openBefore, environment.TimeProvider, token);

            // Flush current durable state, then pause durable flushes so the next writes land ONLY in the ledger & memory!
            await p0.StateDb.FlushAsync(token);
            storage0.PauseFlushes();

            _ = await AppendAndCheckAsync(p0, "evict-1", "-after-pause-1");
            _ = await ReplaceAndCheckAsync(p0, "lag-key", "written-while-storage-paused");
            await Task.Delay(TimeSpan.FromMilliseconds(15), environment.TimeProvider, token);

            // Crash p0 without flushing storage0: durable watermark in storage0 strictly lags openBefore's tail!
            await p0.DisposeAsync();
            storage0.ResumeFlushes();

            // Restart p0 over the SAME InMemoryJournalIndex (lagging durable watermark!) with ApplyLoop not yet started
            // identically for normal and mutant runs: an indexed read MUST wait until ApplyLoop catches up openBefore (JS-I9).
            var restarted = await StartProcessAsync("p0", nodes[0].NodeId, storage0, startApplyLoop: false);
            await ReplaceRootAsync(restarted, nodes[0].NodeId);
            await SyncViewsAsync(restarted, p1);

            var pendingRead = ReadAndCheckAsync(restarted, "evict-1", "JS-I9: lagging-watermark restart read", checkSlowPath: false);
            var delay = Task.Delay(TimeSpan.FromMilliseconds(15), environment.TimeProvider, token);
            if (await SimulationTasks.WhenAny(pendingRead, delay) == pendingRead)
            {
                await pendingRead;
                throw new InvalidOperationException("JS-I9: lagging-watermark restart read completed before ApplyLoop caught up.");
            }

            restarted.EnsureApplyLoopStarted();
            await pendingRead;

            await ReadAndCheckAsync(restarted, "evict-1", "JS-I9: lagging-watermark restart read slow-path check");
            await ReadAndCheckAsync(restarted, "lag-key", "JS-I9: lagging-watermark restart new key");
            _ = await AppendAndCheckAsync(restarted, "lag-key", "-post-restart");
            await ReadAndCheckAsync(restarted, "lag-key", "JS-I9: post-restart append");

            environment.Trace.Record("scenario:lagging-restart");
            return restarted;
        }

        private async Task<PrimaryProcess> RunLaggingPredecessorTakeoverAsync(PrimaryProcess p0, PrimaryProcess p1)
        {
            // Dispose p1 and restart it WITHOUT its ApplyLoop running so p1's index for p0's open ledger lags T!
            await p1.DisposeAsync();
            var laggingP1 = await StartProcessAsync("p1", nodes[1].NodeId, storage1, startApplyLoop: false);

            _ = await ReplaceAndCheckAsync(p0, "takeover-key", "written-on-p0-before-takeover");
            _ = await AppendAndCheckAsync(p0, "takeover-key", "-v2");

            // Takeover: p1 replaces p0's ledger, closing p0's ledger at T while laggingP1's index is behind T!
            await ReplaceRootAsync(laggingP1, nodes[1].NodeId);
            await SyncViewsAsync(p0, laggingP1);

            // Issue the read while ApplyLoop is still unstarted identically for normal and mutant runs: it MUST wait
            // until ApplyLoop starts and catches up the closed predecessor to T before counting its index (JS-I9).
            var pendingRead = ReadAndCheckAsync(laggingP1, "takeover-key", "JS-I9: lagging-predecessor takeover read", checkSlowPath: false);
            var delay = Task.Delay(TimeSpan.FromMilliseconds(15), environment.TimeProvider, token);
            if (await SimulationTasks.WhenAny(pendingRead, delay) == pendingRead)
            {
                await pendingRead;
                throw new InvalidOperationException("JS-I9: lagging-predecessor takeover read completed before ApplyLoop caught up.");
            }

            laggingP1.EnsureApplyLoopStarted();
            await pendingRead;

            await ReadAndCheckAsync(laggingP1, "takeover-key", "JS-I9: lagging-predecessor takeover slow-path check");
            _ = await AppendAndCheckAsync(laggingP1, "takeover-key", "-on-p1");
            await ReadAndCheckAsync(laggingP1, "takeover-key", "JS-I9: post-takeover append");

            environment.Trace.Record("scenario:lagging-takeover");
            return laggingP1;
        }

        private async Task RunHandAwayAndBackAsync(PrimaryProcess p0, PrimaryProcess p1)
        {
            // Hand back to p0 first so p0 warms "handback-key" under claim C1.
            await ReplaceRootAsync(p0, nodes[0].NodeId);
            await SyncViewsAsync(p0, p1);

            _ = await ReplaceAndCheckAsync(p0, "handback-key", "claim1-on-p0");
            var staleClaim1Version = Ref("handback-key").Version;
            await ReadAndCheckAsync(p0, "handback-key", "JS-I6: claim1 warm read");

            // Hand away to p1: p1 mutates "handback-key" under claim C2!
            await ReplaceRootAsync(p1, nodes[1].NodeId);
            await SyncViewsAsync(p0, p1);
            await Task.Delay(TimeSpan.FromMilliseconds(20), environment.TimeProvider, token);
            _ = await AppendAndCheckAsync(p1, "handback-key", "-mutated-on-p1");

            // Hand BACK to p0 under claim C3: p0 MUST have discarded C1's warm state (JS-I6).
            // Verify BOTH that a stale write at claim C1's version is rejected with Conflict (catching
            // KeepWarmStateAcrossClaimsMutant, which would wrongly admit the stale write!) AND that reading and
            // appending at claim C2's version succeed.
            await ReplaceRootAsync(p0, nodes[0].NodeId);
            await SyncViewsAsync(p0, p1);

            using (var staleBuf = ArcBuffer.Wrap("-stale-claim1"u8.ToArray()))
            using (var staleRecord = p0.Store.EncodeAppend(JournalRowKey.Encode("handback-key"), staleClaim1Version, staleBuf))
            {
                var staleOutcome = await p0.Store.WriteAsync(staleRecord, token);
                if (staleOutcome.Value is not JournalWriteOutcome.Rejected)
                {
                    throw new InvalidOperationException(
                        $"JS-I6: stale write at claim-1 version {staleClaim1Version} after hand-away-and-back returned {staleOutcome.Value}, expected Rejected.");
                }
            }

            await ReadAndCheckAsync(p0, "handback-key", "JS-I6: hand-away-and-back read");
            _ = await AppendAndCheckAsync(p0, "handback-key", "-claim3-on-p0");
            await ReadAndCheckAsync(p0, "handback-key", "JS-I6: hand-away-and-back append");

            environment.Trace.Record("scenario:hand-away-and-back");
        }

        private async Task RunIndexedTombstoneExpiryAsync(PrimaryProcess p0, PrimaryProcess p1)
        {
            // Delete a key and evict it from p0's warm state by touching 4 other keys.
            _ = await ReplaceAndCheckAsync(p0, "cold-tomb", "soon-deleted");
            _ = await DeleteAndCheckAsync(p0, "cold-tomb");
            for (var i = 0; i < 4; i++)
            {
                _ = await AppendAndCheckAsync(p0, string.Create(CultureInfo.InvariantCulture, $"evict-{i}"), string.Create(CultureInfo.InvariantCulture, $"-push-{i}"));
            }

            // Catch up p0's index so "cold-tomb" is indexed in the tombstones table.
            var openLedger = p0.MapView.Current.Ledgers.Single(static l => l.Status.Value is LedgerStatus.Open).Id;
            await p0.CatchUpLedgerAsync(openLedger, environment.TimeProvider, token);

            // Before retention elapses, ExpireTombstonesAsync MUST expire 0 tombstones (JS-I7).
            await Task.Delay(TimeSpan.FromMilliseconds(20), environment.TimeProvider, token);
            var early = await p0.Store.ExpireTombstonesAsync(token);
            if (early.Expired != 0)
            {
                throw new InvalidOperationException($"JS-I7: tombstone expired before retention elapsed ({early.Expired} expired).");
            }

            // Deterministic JS-I2 / JS-I3 ApplyOnAdmit check (runs identically in normal and mutant runs): isolate replicas
            // 1 and 2 so a Delete stays in flight (no quorum). Under ApplyOnAdmitMutant, warm state applies the un-durable
            // Delete at admission, so a conflicting write at the pre-delete version rejects immediately and a concurrent
            // owner read can observe the uncommitted deletion!
            var inflightKey = "inflight-tomb";
            _ = await ReplaceAndCheckAsync(p0, inflightKey, "v1");
            var verBeforeDelete = Ref(inflightKey).Version;
            var inflightRowKey = JournalRowKey.Encode(inflightKey);

            nodes[1].Isolate();
            nodes[2].Isolate();
            JournalWriteOutcome.Accepted? deletedInFlight = null;
            try
            {
                using var deleteRecord = p0.Store.EncodeDelete(inflightRowKey, verBeforeDelete);
                using var probeBuffer = ArcBuffer.Wrap("probe"u8.ToArray());
                using var probeRecord = p0.Store.EncodeAppend(inflightRowKey, verBeforeDelete, probeBuffer);

                var pendingDelete = p0.Store.WriteAsync(deleteRecord, token);
                await Task.Delay(TimeSpan.FromMilliseconds(5), environment.TimeProvider, token);

                var probeWrite = p0.Store.WriteAsync(probeRecord, token);
                var concurrentRead = p0.Store.ReadAsync(inflightRowKey, token);
                var delay = Task.Delay(TimeSpan.FromMilliseconds(15), environment.TimeProvider, token);
                var winner = await SimulationTasks.WhenAny(probeWrite, delay);

                if (concurrentRead.IsCompletedSuccessfully)
                {
                    using var observed = Assert.IsType<JournalReadResult>(concurrentRead.Result.Value);
                    if (!observed.Exists || observed.CurrentVersion != verBeforeDelete)
                    {
                        throw new InvalidOperationException(
                            $"JS-I2/JS-I3: concurrent owner read observed uncommitted in-flight Delete (Exists={observed.Exists}, Version={observed.CurrentVersion}, expected {verBeforeDelete}).");
                    }
                }

                nodes[1].Reconnect();
                nodes[2].Reconnect();

                if (winner == probeWrite)
                {
                    var probeOutcome = await probeWrite;
                    throw new InvalidOperationException($"JS-I2/JS-I3: ApplyOnAdmit decided conflicting write ({probeOutcome.Value}) against un-durable in-flight Delete.");
                }

                deletedInFlight = Assert.IsType<JournalWriteOutcome.Accepted>((await pendingDelete).Value);
                var rejectedProbe = Assert.IsType<JournalWriteOutcome.Rejected>((await probeWrite).Value);
                Assert.Equal(deletedInFlight.Version, rejectedProbe.CurrentVersion);
                try
                {
                    using var completedRead = Assert.IsType<JournalReadResult>((await concurrentRead).Value);
                }
                catch (JournalStoreUnavailableException)
                {
                    // A read whose replica tail check ran during the quorum outage may surface Unavailable.
                }
            }
            finally
            {
                nodes[1].Reconnect();
                nodes[2].Reconnect();
            }

            var inflightRef = Ref(inflightKey);
            inflightRef.Version = deletedInFlight.Version;
            inflightRef.Exists = false;
            inflightRef.Deleted = true;
            inflightRef.Content.Clear();
            inflightRef.BaseLength = 0;
            inflightRef.BytesSinceBase = 0;
            // Resurrect inflight-tomb so cold-tomb remains the single cold tombstone eligible for expiry.
            _ = await AppendAndCheckAsync(p0, inflightKey, "resurrected");

            // Advance past retention and expire the cold indexed tombstone!
            await Task.Delay(Retention + TimeSpan.FromMilliseconds(200), environment.TimeProvider, token);
            var scan = await p0.Store.ExpireTombstonesAsync(token);
            if (scan.Expired != 1)
            {
                throw new InvalidOperationException($"JS-I7: expected 1 cold indexed tombstone to expire, got {scan.Expired}.");
            }

            // Mark cold-tomb as expired in oracle (still non-existent, tombstone cleared).
            var tombRef = Ref("cold-tomb");
            tombRef.Deleted = false;

            await p0.CatchUpLedgerAsync(openLedger, environment.TimeProvider, token);
            environment.Trace.Record("scenario:indexed-expiry");
            _ = p1;
        }

        private async Task RunLsmCompactionForgettingAndPreloadedAbsenceAsync(PrimaryProcess p0, PrimaryProcess p1)
        {
            // Keep p1's ArchiveStore AND a second stale LsmArchiveStore at an OLD view before the upcoming drain + drop
            // (for UncoveredDropMutant proofs through both JournalStore.ReadAsync with KeyIndex and ArchiveReader.ReadStoreAsync).
            await p1.ManifestView.ReadAsync(token);
            await p1.ArchiveStore.PreloadAsync([KeyRange.Root]);
            var staleManifestForReadStore = new ManifestView(objects);
            await staleManifestForReadStore.ReadAsync(token);
            var staleStoreForReadStore = new LsmArchiveStore(
                objects,
                staleManifestForReadStore,
                new TailCache(objects, lsmOptions),
                lsmOptions,
                SimulationEnvironment.Clock)
            {
                UncoveredDropMutant = mutant == WarmStateMutant.UncoveredDrop,
            };
            await staleStoreForReadStore.PreloadAsync([KeyRange.Root]);

            // Replace root once so all closed ledgers (including the one with cold-tomb's Delete + Expired and live keys)
            // are closed and can be completely drained and dropped into the LSM!
            await ReplaceRootAsync(p0, nodes[0].NodeId);
            await SyncViewsAsync(p0, p1);

            // Drain every closed ledger to completion using DrainWorker.
            var drainWorker = new DrainWorker(objects, p0.ManifestView, p0.LedgerReader, lsmOptions, SimulationEnvironment.Clock);
            foreach (var closed in p0.MapView.Current.Ledgers.Where(static l => l.Status.Value is LedgerStatus.Closed).OrderBy(static l => l.Id))
            {
                var t = Assert.IsType<LedgerStatus.Closed>(closed.Status.Value).T;
                while (true)
                {
                    var outcome = await drainWorker.DrainAsync(closed, t, manifestCommitter.CommitAsync, token);
                    if (outcome.Value is DrainOutcome.Covered)
                    {
                        break;
                    }

                    var drained = Assert.IsType<DrainOutcome.Drained>(outcome.Value);
                    _ = Assert.IsType<ManifestCommitOutcome.Applied>(drained.Outcome.Value);
                    await p0.ManifestView.ReadAsync(token);
                    if (drained.Change.Complete)
                    {
                        break;
                    }
                }
            }

            // Drop all completed closed ledgers so their rows live ONLY in the LSM archive!
            await p0.ManifestView.ReadAsync(token);
            var dropper = new LedgerDropper(p0.MapView, p0.ManifestView, NullLogger<LedgerDropper>.Instance);
            var dropped = await dropper.DropCompleteAsync(mapCommitter.CommitAsync, token);
            Assert.NotEmpty(dropped);

            // Refresh p1's map view ONLY (NOT its manifest view!) and hand primary to p1 so p1's JournalStore.ReadAsync
            // (with KeyIndex!) and ArchiveReader.ReadStoreAsync both read with a map that dropped those ledgers against
            // an old manifest that does not yet show them complete!
            await ReplaceRootAsync(p1, nodes[1].NodeId);
            await SyncViewsAsync(p0, p1);

            var readerOnP1 = new ArchiveReader(p1.LedgerReader, staleStoreForReadStore, SimulationEnvironment.Clock, p1.StateDb.KeyIndex);
            using (var storeRead = await readerOnP1.ReadStoreAsync(JournalRowKey.Encode("evict-0").Memory, token))
            {
                if (storeRead.Prefix is null)
                {
                    throw new InvalidOperationException("uncovered-drop: ReadStoreAsync returned empty prefix for a key whose ledger was dropped.");
                }
            }

            await ReadAndCheckAsync(p1, "evict-0", "uncovered-drop: JournalStore.ReadAsync with KeyIndex after ledger drop", allowRebase: true, checkSlowPath: false);

            // Hand primary back to p0 for compaction, key forgetting, and preloaded absence checks.
            await ReplaceRootAsync(p0, nodes[0].NodeId);
            await SyncViewsAsync(p0, p1);

            // Compact the LSM so the expired empty Delete of "cold-tomb" drops with all older rows (key forgetting!).
            _ = await p0.MapView.ReadAsync(token);
            await p0.ManifestView.ReadAsync(token);
            var compactor = new CompactionWorker(objects, p0.ArchiveStore.Tails, p0.ManifestView, lsmOptions);
            var compactionsCommitted = 0;
            for (var round = 0; round < 8; round++)
            {
                var currentManifest = p0.ManifestView.Current?.Value;
                if (currentManifest is null)
                {
                    break;
                }

                var overfull = CompactionWorker.OverfullPartitions(currentManifest, lsmOptions);
                if (overfull.Length == 0)
                {
                    break;
                }

                var compactedAny = false;
                foreach (var partition in overfull)
                {
                    var outcome = await compactor.CompactAsync(partition, p0.ManifestView.Current!.Value, p0.MapView.Current, token);
                    if (outcome.Value is CompactionOutcome.Compacted compacted)
                    {
                        _ = Assert.IsType<ManifestCommitOutcome.Applied>((await manifestCommitter.CommitAsync(compacted.Change, token)).Value);
                        await p0.ManifestView.ReadAsync(token);
                        compactionsCommitted++;
                        compactedAny = true;
                    }
                }

                if (!compactedAny)
                {
                    break;
                }
            }

            Assert.True(compactionsCommitted > 0, "Expected at least one LSM compaction to commit in Phase 6.");

            // Advance past ViewAdoptionInterval, preload Root section metadata on p0, and catch up the open ledger's index.
            await Task.Delay(lsmOptions.ViewAdoptionInterval + TimeSpan.FromMilliseconds(5), environment.TimeProvider, token);
            await p0.ManifestView.ReadAsync(token);
            await p0.ArchiveStore.PreloadAsync([KeyRange.Root]);

            var openLedger = p0.MapView.Current.Ledgers.Single(static l => l.Status.Value is LedgerStatus.Open).Id;
            await p0.CatchUpLedgerAsync(openLedger, environment.TimeProvider, token);

            // Explicitly verify that the LSM forgot "cold-tomb" (ArchiveStore.ReadAsync returns Absent) and that
            // JournalStore.ReadAsync returns Exists == false and CurrentVersion == JournalVersion.Origin before re-creating it!
            var coldTombKey = JournalRowKey.Encode("cold-tomb");
            var lsmColdTomb = await p0.ArchiveStore.ReadAsync(coldTombKey.Memory, p0.MapView.Current, token);
            if (lsmColdTomb.Value is ArchiveStoreRead.Read unexpectedRead)
            {
                unexpectedRead.Dispose();
            }

            _ = Assert.IsType<ArchiveStoreRead.Absent>(lsmColdTomb.Value);

            using (var forgottenRead = Assert.IsType<JournalReadResult>((await p0.Store.ReadAsync(coldTombKey, token)).Value))
            {
                Assert.False(forgottenRead.Exists);
                Assert.Equal(JournalVersion.Origin, forgottenRead.CurrentVersion);
            }

            Ref("cold-tomb").Version = JournalVersion.Origin;
            await ReadAndCheckAsync(p0, "cold-tomb", "JS-I6: forgotten key after compaction");

            // Re-create "cold-tomb" cleanly at Origin after forgetting!
            _ = await AppendAndCheckAsync(p0, "cold-tomb", "reborn-after-forgetting");
            await ReadAndCheckAsync(p0, "cold-tomb", "JS-I6: reborn after forgetting");
            environment.Trace.Record("scenario:lsm-compaction-forget");

            await p0.CatchUpLedgerAsync(openLedger, environment.TimeProvider, token);
            var callsBefore = p0.CountingObjects.Calls;
            await ReadAndCheckAsync(p0, "never-existed-99", "JS-I9: preloaded absence read", checkSlowPath: false);
            if (p0.CountingObjects.Calls != callsBefore)
            {
                throw new InvalidOperationException(
                    $"JS-I9: preloaded absence read made {p0.CountingObjects.Calls - callsBefore} object-store calls (expected 0).");
            }

            await ReadAndCheckAsync(p0, "never-existed-99", "JS-I9: preloaded absence slow-path check");
            environment.Trace.Record("scenario:preloaded-absence-zero-io");
        }

        private async Task VerifyAllJournalsAsync(PrimaryProcess owner)
        {
            _ = await owner.MapView.ReadAsync(token);
            await owner.ManifestView.ReadAsync(token);
            await owner.Registry.ReconciledAsync(owner.MapView.Current.Epoch, token);

            foreach (var key in _oracle.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                await ReadAndCheckAsync(owner, key, "JS-I6: final oracle check", allowRebase: true);
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Runs a ledger's quorum over three LedgerStore nodes whose processes and operating systems crash and restart from
/// what their disks kept, up to every node at once, and whose local storage operations fail, which stops their processes, while writers and readers use the ledger and a controller creates
/// and drops other replicas, across a network which loses, duplicates, delays, and partitions messages
/// (test/Durable.Storage/docs/spec/verification.md §7.3).
/// </summary>
/// <remarks>
/// <para>
/// Each node's disk completes WAL writes out of order after random latencies, so a crash cuts the WAL with writes in
/// flight: a process crash keeps them, an operating-system crash loses or tears them. One node's disk also suffers media
/// damage. A failed local storage operation (a WAL write, torn or not, a read, a checkpoint's write or read, a deletion,
/// or a directory flush) stops its node's process at once, so the node answers nothing more, and the node crashes and
/// restarts (conventions.md §14). After every step the kit checks each node against LS-I1, LS-I2, LS-I6, and LS-I11, and the ledger trace's
/// checks (LR-I6, LR-I8, LP-I7, LP-I9); after every restart it reads back every entry the node acknowledged, which
/// must be itself or corrupt and never missing (LS-I5, LR-I2), and the seal of every replica which answered sealed.
/// </para>
/// <para>
/// While a quorum of nodes is down, writes and reads fail and nothing is lost. Once the faults stop, every write the
/// quorum acknowledged reads back, unless damage left fewer than a quorum of readable copies, and the clients' history
/// is linearizable with each address a register which a write the quorum acknowledged decides: every read begun after
/// the acknowledgement returns the address's value, and no read returns another (LR-I1, LP-I4). A recovery then
/// closes the ledger while a node crashes as it serves the recovery's seal and, once that node restarts, another as the
/// recovery runs, each with the request's write in flight or just durable, so the request may land without an answer
/// (<see cref="RecoveryCrashes"/>). The recovery completes within
/// <see cref="RecoveryLiveness"/>'s bound from its start, or from the last crash or restart before it ends, the writers
/// never saw its end written (LP-I7), and every position below its end reads back its writer's value, before and after
/// every node's operating system crashes and restarts, when the ledger still reports sealed (LR-I5, LP-I7, LP-I9,
/// LS-I11).
/// </para>
/// </remarks>
public sealed class LedgerStoreSimulationTests
{
    private const int Writers = 3;
    private const int Readers = 2;
    private const int MinimumCrashes = 2;
    private const int Quorum = 2;
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 64 };
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan DiskLatency = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// The seed every change runs: the lowest from 100 whose run, with each replica created by its first write, fired
    /// every kind of fault; a scripted step after the faults end fires a local storage failure on every seed. A run costs
    /// about 15 s of wall time, so exploration runs more through DURABLE_STORAGE_SIM_SEEDS, and
    /// <c>just ledger-traces</c> runs seeds 1 to 15 (test/Durable.Storage/docs/spec/verification.md §7.4, §12).
    /// </summary>
    private const int CorpusSeed = 113;
    private const int ReplaySeed = 1;

    // The theory and the corpus check share each seed's run, so it runs once.
    private static readonly ConcurrentDictionary<int, Lazy<ClusterSimulationRun>> Runs = new();

    /// <summary>
    /// Gets the seed corpus.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(CorpusSeed, 1);

    /// <summary>
    /// Every acknowledged write survives crashes and restarts of the nodes, the clients' history is linearizable, and a
    /// recovery closes the ledger over every acknowledged write below its end, which survives a crash of every node.
    /// </summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void AcknowledgedWritesSurviveCrashesAndRestarts(int seed) => _ = RunSeed(seed);

    /// <summary>
    /// Across the corpus, every kind of fault fires, including a quorum of nodes down at once, and the crashes of a node
    /// while it serves the recovery's seal and while the recovery runs.
    /// </summary>
    /// <remarks>
    /// The fault schedule follows the run's random draws, which the workload shapes, so a change to the workload moves
    /// the faults a seed fires; the assertion names the kinds the corpus misses.
    /// </remarks>
    [Fact]
    public void TheCorpusFiresEveryFault() =>
        Assert.Empty(MissingFaults([.. SeededSimulation.CorpusSeeds(CorpusSeed, 1).SelectMany(static seed => RunSeed(seed).Trace)]));

    /// <summary>
    /// The crash simulation replays from its seed and from its artifact, over a smaller workload, since the check runs it
    /// three times and determinism does not depend on its size.
    /// </summary>
    [Fact]
    public void TheCrashSimulationReplaysFromItsSeed() =>
        _ = SeededSimulation.RunAndReplay(ReplaySeed, static environment => SimulateAsync(environment, Scale.Replay));

    private static ClusterSimulationRun RunSeed(int seed) =>
        Runs.GetOrAdd(seed, static seed => new Lazy<ClusterSimulationRun>(() => SeededSimulation.Run(seed, static environment => SimulateAsync(environment, Scale.Corpus)))).Value;

    /// <summary>
    /// Returns the kinds of fault which no entry of <paramref name="trace"/> records.
    /// </summary>
    private static IEnumerable<string> MissingFaults(IReadOnlyList<string> trace) =>
        new[] { "(operating system)", "(process)", "nodes down", "pause ", "isolate ", "damage ", "fail a ", "request duplicated", "ledger 2 dropped everywhere", "while it serves recovery's seal", "while recovery runs" }
            .Where(fault => !trace.Any(entry => entry.Contains(fault, StringComparison.Ordinal)));

    private static async Task SimulateAsync(SimulationEnvironment environment, Scale scale)
    {
        // The scenario's calls carry a cancellable token, as production callers' do, which nothing cancels: the scenario
        // owns it, so no thread outside the simulation cancels it (test/Durable.Storage/docs/spec/verification.md §7.1). CancellationToken.None
        // would skip the code under test's registrations on it, and change the seeds' schedules.
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        environment.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        environment.Network.MaxJitter = TimeSpan.FromMilliseconds(3);
        environment.Network.MessageDropRate = 0.02;
        environment.Rpc.DuplicationRate = 0.05;
        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 3, cancellationToken);
        foreach (var node in nodes)
        {
            node.MaxDiskLatency = DiskLatency;
        }

        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        replicas.LedgerTrace.LogsRecoveredReplicas = LedgerTraces.Enabled;
        var invariants = LedgerStoreInvariants.Register(environment, replicas);
        await using var replica = replicas.OpenLedger(SimulatedStorage.Ledger, Options, Quorum, "client");
        var workload = new Workload(environment, replicas.LedgerTrace, replica, scale, cancellationToken);
        var faults = new FaultInjector(environment, nodes, scale, cancellationToken);
        await SimulationTasks.WhenAll(
        [
            .. Enumerable.Range(0, Writers).Select(workload.WriteAsync),
            .. Enumerable.Range(Writers, Readers).Select(workload.ReadAsync),
            faults.InjectAsync(),
            ChurnReplicasAsync(environment, replicas, cancellationToken),
        ]);

        // With the faults over, every node runs and no message is lost: every acknowledged write reads back.
        await faults.EndAsync();
        environment.Network.MessageDropRate = 0;
        environment.Rpc.DuplicationRate = 0;
        environment.Trace.Record("faults end");
        await workload.WriteThroughAStorageFailureAsync(nodes[0]);
        var restarts = await workload.RestartHoldingADamagedSlotAsync(replicas, faults.Damaged);
        await workload.ReadBackAcknowledgedAsync(nodes);
#pragma warning disable RS0030 // The checker is test-host code, which only polls the test's token.
        LinearizabilityAssert.Linearizable(SingleValueRegisterModel.Instance, workload.History.History(), cancellationToken: TestContext.Current.CancellationToken);
#pragma warning restore RS0030

        // Once the faults heal, a recovery needs only max(Q, N − Q + 1) responsive replicas (ledger-protocol §6.5), so it
        // completes within the bound its rounds take; the ledger trace checks its end against the writers' (LP-I7). A
        // node crashes while it serves the recovery's seal, and once it restarts another while the recovery runs, each
        // with the request's write in flight or just durable; one node down at a time leaves the recovery enough
        // replicas, and a request the crash lost fails at its deadline and is repeated, so the bound runs from the last
        // crash or restart before the recovery ends, as from a healing.
        var crashes = new RecoveryCrashes(environment, replicas, SimulatedStorage.Ledger, "recovery", Options.WindowSize);
        var roundTrip = RecoveryLiveness.RoundTrip(environment, DiskLatency);
        var started = environment.Trace.Elapsed;
        Slot end;
        await using (var recovery = replicas.OpenLedger(SimulatedStorage.Ledger, Options, Quorum, "recovery"))
        {
            end = (await replicas.LedgerTrace.RecoverAsync(recovery, "recovery", SimulatedStorage.Ledger, cancellationToken)).End;
        }

        crashes.Disarm();

        // The recovery's conditional write closes the ledger in the map at its candidate end, the recorded T (LG-I9).
        replicas.LedgerTrace.Closed(SimulatedStorage.Ledger, "recovery", end.Value);
        var ended = environment.Trace.Elapsed;
        var healed = crashes.Faults.Where(fault => fault > started && fault <= ended).DefaultIfEmpty(started).Max();
        var bound = healed > started
            ? RecoveryLiveness.BoundFromHealing(Options.WindowSize, roundTrip, Deadline)
            : RecoveryLiveness.Bound(Options.WindowSize, roundTrip);
        var took = ended - healed;
        environment.Trace.Record(string.Create(CultureInfo.InvariantCulture, $"recovered end {end.Value} after the faults in {took.TotalMilliseconds:0.###} ms"));
        Assert.True(took <= bound, string.Create(CultureInfo.InvariantCulture, $"A recovery after the faults healed took {took.TotalMilliseconds} ms, over its bound of {bound.TotalMilliseconds} ms."));
        await crashes.SettledAsync();
        await workload.ReadBelowAsync(end);

        // The recovery's writes and seals are durable, so the closed ledger survives a crash of every node (LS-I11).
        foreach (var node in nodes)
        {
            await node.CrashAsync(operatingSystem: true);
        }

        foreach (var node in nodes)
        {
            await node.StartAsync(cancellationToken);
        }

        await workload.ReadBelowAsync(end);
        Assert.True((await replicas.LedgerTrace.CheckSealAsync(replica, "client", SimulatedStorage.Ledger, cancellationToken)).Tail.IsSealed);
        Assert.True(faults.Crashes >= MinimumCrashes, string.Create(CultureInfo.InvariantCulture, $"{faults.Crashes} crashes."));
        Assert.Equal(faults.Crashes + nodes.Sum(static node => node.FailStops) + restarts + crashes.Crashes + nodes.Length, invariants.Recoveries);
        Assert.True(invariants.VerifiedEntries > 0, "No restart read back an acknowledged entry.");
        LedgerTraces.Export(environment, "crash", replicas.LedgerTrace, SimulatedStorage.Ledger, nodes, Quorum, Options.WindowSize);
    }

    /// <summary>
    /// Creates other ledgers' replicas on every node by their first writes and drops one of them, as each node's
    /// reconciliation does, retrying each request until its node acknowledges it.
    /// </summary>
    private static async Task ChurnReplicasAsync(SimulationEnvironment environment, SimulatedLedgerReplicaTransport replicas, CancellationToken cancellationToken)
    {
        for (var ledger = 2UL; ledger <= 4; ledger++)
        {
            await Task.Delay(environment.Random.NextTimeSpan(TimeSpan.FromMilliseconds(40)), environment.TimeProvider, cancellationToken);
            await SimulatedStorage.WriteEverywhereAsync(replicas, new LedgerId(ledger), Options, cancellationToken);
            environment.Trace.Record(string.Create(CultureInfo.InvariantCulture, $"ledger {ledger} created everywhere"));
            if (ledger == 3)
            {
                await SimulatedStorage.DropEverywhereAsync(replicas, new LedgerId(2), cancellationToken);
                environment.Trace.Record("ledger 2 dropped everywhere");
            }
        }
    }

    /// <summary>
    /// The scenario's work: each writer's writes, each reader's reads, and the faults injected.
    /// </summary>
    private sealed record Scale(int WritesPerWriter, int ReadsPerReader, int Faults)
    {
        public static readonly Scale Corpus = new(12, 24, 14);
        public static readonly Scale Replay = new(2, 4, 2);

        public int Addresses => Writers * WritesPerWriter;
    }

    /// <summary>
    /// Injects crashes, pauses, partitions, media damage, and local storage failures, one at a time but overlapping in
    /// effect: a crashed node restarts after its own downtime, so up to every node may be down at once.
    /// </summary>
    private sealed class FaultInjector(SimulationEnvironment environment, LedgerStoreNode[] nodes, Scale scale, CancellationToken cancellationToken)
    {
        private readonly List<Task> _recoveries = [];

        public int Crashes { get; private set; }

        /// <summary>
        /// Gets the one node whose media the faults damage, so at most Q − 1 replicas ever answer corrupt (system model §3).
        /// </summary>
        public LedgerStoreNode Damaged { get; } = environment.Random.Choose(nodes);

        public async Task InjectAsync()
        {
            var random = environment.Random;
            for (var fault = 0; fault < scale.Faults; fault++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5) + random.NextTimeSpan(TimeSpan.FromMilliseconds(20)), environment.TimeProvider, cancellationToken);

                // The run checks recovery after at least MinimumCrashes crashes, so once only enough faults remain for the
                // crashes still owed, each is a crash, which waits for a node to be up to crash.
                var choice = random.Next(100);
                if (scale.Faults - fault <= MinimumCrashes - Crashes)
                {
                    while (!nodes.Any(static node => node.IsUp))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken);
                    }

                    await CrashAsync();
                }
                else if (choice < 50)
                {
                    await CrashAsync();
                }
                else if (choice < 65)
                {
                    var pause = TimeSpan.FromMilliseconds(1) + random.NextTimeSpan(TimeSpan.FromMilliseconds(20));
                    random.Choose(nodes).Pause(pause);
                    _recoveries.Add(Task.Delay(pause + TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken));
                }
                else if (choice < 80)
                {
                    var node = random.Choose(nodes);
                    node.Isolate();
                    _recoveries.Add(ReconnectAsync(node));
                }
                else if (choice < 90)
                {
                    _ = Damaged.Damage();
                }
                else if (nodes.Where(static node => node.IsUp).ToArray() is { Length: > 0 } up)
                {
                    random.Choose(up).FailNextStorageOperation(random.Choose(Enum.GetValues<StorageFault>()));
                }
            }
        }

        /// <summary>
        /// Waits for every crashed node to restart, every paused node to resume, and every partition to heal.
        /// </summary>
        public async Task EndAsync()
        {
            foreach (var node in nodes)
            {
                node.DisarmStorageFailure();
            }

            await SimulationTasks.WhenAll([.. _recoveries, .. nodes.Select(static node => node.FailStopped)]);
            foreach (var node in nodes)
            {
                node.Reconnect();
            }
        }

        private async Task CrashAsync()
        {
            var up = nodes.Where(static node => node.IsUp).ToArray();
            if (up.Length == 0)
            {
                return;
            }

            // The first crash always loses the operating system's cache, where acknowledged writes must not be.
            var node = environment.Random.Choose(up);
            await node.CrashAsync(operatingSystem: Crashes == 0 || environment.Random.Chance(0.75));
            Crashes++;
            if (nodes.Count(static node => !node.IsUp) is var down and > 1)
            {
                environment.Trace.Record(string.Create(CultureInfo.InvariantCulture, $"{down} nodes down: the quorum is lost"));
            }

            _recoveries.Add(RestartAsync(node));
        }

        private async Task RestartAsync(LedgerStoreNode node)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5) + environment.Random.NextTimeSpan(TimeSpan.FromMilliseconds(40)), environment.TimeProvider, cancellationToken);
            await node.StartAsync(cancellationToken);
        }

        private async Task ReconnectAsync(LedgerStoreNode node)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5) + environment.Random.NextTimeSpan(TimeSpan.FromMilliseconds(25)), environment.TimeProvider, cancellationToken);
            node.Reconnect();
        }
    }

    /// <summary>
    /// Writers which each write their own addresses once through the client's <see cref="Ledger"/>, whose known contiguous
    /// tail no completion raises, so each write carries zero, and readers of random addresses,
    /// recording every invocation and response on virtual time, and each write's outcome in the ledger trace.
    /// </summary>
    private sealed class Workload(SimulationEnvironment environment, LedgerTrace ledger, Ledger replica, Scale scale, CancellationToken cancellationToken)
    {
        private readonly List<long> _acknowledged = [];

        public HistoryRecorder<RegisterInput, RegisterOutput> History { get; } = new(environment.TimeProvider);

        public async Task WriteAsync(int writer)
        {
            for (var address = (long)writer; address < scale.Addresses; address += Writers)
            {
                await DelayAsync(15);
                await WriteAsync(writer, address);
            }
        }

        /// <summary>
        /// Every seed: once the faults end, the first writer writes the address after its last while
        /// <paramref name="node"/>'s next WAL write fails, so the corpus fires a local storage failure whatever the random
        /// rounds drew. The node's process stops, the driver takes its report, and the node restarts (conventions.md §14).
        /// </summary>
        public async Task WriteThroughAStorageFailureAsync(LedgerStoreNode node)
        {
            const int waits = 1_000;
            var reported = string.Create(CultureInfo.InvariantCulture, $"{node.Address} reported a failed ");
            var failStops = node.FailStops;
            var reports = environment.Trace.Entries.Count(entry => entry.Contains(reported, StringComparison.Ordinal));
            node.FailNextStorageOperation(StorageFault.Write);
            await WriteAsync(0, scale.Addresses);

            // The quorum may answer before the node writes its copy, so the step waits for the node's write.
            for (var wait = 0; wait < waits && node.FailStops == failStops; wait++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken);
            }

            Assert.True(node.FailStops == failStops + 1, string.Create(CultureInfo.InvariantCulture, $"{node.Address}'s armed WAL write failure never fired."));
            await node.FailStopped;
            Assert.True(node.IsUp, string.Create(CultureInfo.InvariantCulture, $"{node.Address} never restarted after its WAL write failed."));
            Assert.True(
                environment.Trace.Entries.Count(entry => entry.Contains(reported, StringComparison.Ordinal)) == reports + 1,
                string.Create(CultureInfo.InvariantCulture, $"No report of {node.Address}'s failed WAL write reached the driver."));
        }

        /// <summary>
        /// Every seed: once the faults end, <paramref name="node"/>, the node whose media the faults damage, restarts, so
        /// the record of its highest DATA slot lies in a WAL file its recovery classified, and the page holding the record
        /// is damaged. A read of the slot on the node answers corrupt, and the node restarts again holding it faulty, so
        /// every seed's trace holds a slot a replica answered corrupt before its node restarted holding it (LS-I5, LR-I2),
        /// which the trace mutant mutant-restart-damage-as-missing changes (test/Durable.Storage/docs/spec/verification.md §5.1).
        /// </summary>
        /// <returns>The number of restarts the step took.</returns>
        public async Task<int> RestartHoldingADamagedSlotAsync(SimulatedLedgerReplicaTransport replicas, LedgerStoreNode node)
        {
            const int waits = 1_000;
            await RestartAsync(node);

            // Damage which ends above the catalog checkpoint's cut may hide a create or drop, which refuses the next open
            // (LS-I5), so a catalog checkpoint past the recovered WAL precedes the damage.
            var store = node.Store;
            var frontier = store.Wal.DurableFrontier;
            store.RequestCatalogCheckpoint();
            for (var wait = 0; wait < waits && !(store.CatalogCut >= frontier && store.CatalogCheckpointCurrent && store.MaintenanceIdle); wait++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken);
            }

            Assert.True(store.CatalogCut >= frontier, string.Create(CultureInfo.InvariantCulture, $"{node.Address} wrote no catalog checkpoint past {frontier}."));

            // The node's highest DATA slot, whose record the restart left in a WAL file its recovery classified.
            var live = store.FindLive(SimulatedStorage.Ledger)!;
            var (held, _) = await live.SlotStatesAsync(scale.Addresses, cancellationToken);
            Assert.True(held.Count > 0, string.Create(CultureInfo.InvariantCulture, $"{node.Address} holds no DATA slot."));
            var address = held[^1];
            var slot = new Slot(address);
            var page = await live.RecordPageAsync(address, cancellationToken);
            Assert.True(page is { } recorded && node.Damage(recorded), string.Create(CultureInfo.InvariantCulture, $"{node.Address} recovered no page of address {address} to damage."));

            var answer = "no answer";
            await using (var traced = replicas.LedgerTrace.Traced(replicas.Open(node.NodeId, SimulatedStorage.Ledger, Options, "client"), "client", node.Address, SimulatedStorage.Ledger))
            {
                try
                {
                    var result = await traced.ReadAsync(slot, Slot.Zero, cancellationToken);
                    answer = result.Case.ToString();
                    result.Release();
                }
                catch (LedgerReplicaCorruptException)
                {
                    answer = nameof(LedgerReplicaCorruptException);
                }
            }

            Assert.True(answer == nameof(LedgerReplicaCorruptException), string.Create(CultureInfo.InvariantCulture, $"{node.Address} answered {answer} for damaged address {address}."));

            // A checkpoint of the slot table records the fault and moves the replica's replay start past the damage, so
            // the restart recovers the slot faulty from the checkpoint rather than raising the damage's end over every
            // slot before it (ledgerstore.md §6.2).
            for (var wait = 0; wait < waits && store.Wal.DurableFrontier <= live.LastPublished; wait++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken);
            }

            await live.CheckpointAsync(cancellationToken);
            await RestartAsync(node);
            var (data, faulty) = await node.Store.FindLive(SimulatedStorage.Ledger)!.SlotStatesAsync(address + 1, cancellationToken);
            Assert.True(
                faulty.Contains(address),
                string.Create(CultureInfo.InvariantCulture, $"{node.Address} restarted without address {address} faulty, which it answered corrupt: data [{string.Join(", ", data)}], faulty [{string.Join(", ", faulty)}]."));
            Record($"{node.Address} restarted holding address {address}, which it answered corrupt");
            return 2;
        }


        private async Task RestartAsync(LedgerStoreNode node)
        {
            await node.CrashAsync(operatingSystem: false);
            await node.StartAsync(cancellationToken);
        }

        private async Task WriteAsync(int writer, long address)
        {
            var invocation = History.Invoke(writer, new RegisterInput(address, IsWrite: true));
            using var writerBuffer = new ArcBufferWriter();
            writerBuffer.Write(SimulatedStorage.Payload(address));
            using var payload = writerBuffer.PeekSlice(writerBuffer.Length);
            try
            {
                var result = await ledger.WriteAsync(
                    replica,
                    string.Create(CultureInfo.InvariantCulture, $"writer-{writer}"),
                    SimulatedStorage.Ledger,
                    new Slot(address),
                    payload,
                    cancellationToken);
                Assert.Equal(ReplicaWriteCase.Written, result.Case);
                History.Complete(invocation, RegisterOutput.Written);
                _acknowledged.Add(address);
                Record($"write {address} acknowledged");
            }
            catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: true))
            {
                // No decision reached a quorum (ledger-replicas §6.4); the write may still land.
                History.CompleteUnbounded(invocation, RegisterOutput.Failed);
                Record($"write {address} unavailable");
            }
        }

        public async Task ReadAsync(int reader)
        {
            for (var read = 0; read < scale.ReadsPerReader; read++)
            {
                await DelayAsync(10);
                var address = (long)environment.Random.Next(scale.Addresses);
                var invocation = History.Invoke(reader, new RegisterInput(address, IsWrite: false));
                var output = await TryReadAsync(address);
                History.Complete(invocation, output);
                Record($"read {address} {output}");
            }
        }

        /// <summary>
        /// Reads back every acknowledged address, retrying a read which fails, and requires the written value unless a
        /// node reads the address as corrupt: damage then left fewer than a quorum of readable copies.
        /// </summary>
        public async Task ReadBackAcknowledgedAsync(LedgerStoreNode[] nodes)
        {
            const int attempts = 5;
            var damaged = 0;
            foreach (var address in _acknowledged)
            {
                var output = RegisterOutput.Failed;
                for (var attempt = 0; attempt < attempts && output == RegisterOutput.Failed; attempt++)
                {
                    var invocation = History.Invoke(Writers + Readers, new RegisterInput(address, IsWrite: false));
                    output = await TryReadAsync(address);
                    History.Complete(invocation, output);
                }

                if (output != RegisterOutput.Data && await IsCorruptOnSomeNodeAsync(nodes, address))
                {
                    damaged++;
                    continue;
                }

                Assert.True(output == RegisterOutput.Data, string.Create(CultureInfo.InvariantCulture, $"Acknowledged address {address} read back as {output}."));
            }

            Record($"read back {_acknowledged.Count} acknowledged writes, {damaged} of them unreadable after damage");
        }

        /// <summary>
        /// Reads every position below a recovery's end, retrying a read which fails as the faults explain: the recovery
        /// left each on a quorum of replicas, with the value its writer wrote there (LP-I7, LR-I5).
        /// </summary>
        public async Task ReadBelowAsync(Slot end)
        {
            const int attempts = 5;
            for (var address = 0L; address < end.Value; address++)
            {
                var output = RegisterOutput.Failed;
                for (var attempt = 0; attempt < attempts && output == RegisterOutput.Failed; attempt++)
                {
                    output = await TryReadAsync(address);
                }

                Assert.True(output == RegisterOutput.Data, string.Create(CultureInfo.InvariantCulture, $"Address {address}, below the recovered end {end.Value}, read back as {output}."));
            }

            Record($"read back the {end.Value} positions below the recovered end");
        }

        private static async Task<bool> IsCorruptOnSomeNodeAsync(LedgerStoreNode[] nodes, long address)
        {
            foreach (var node in nodes)
            {
                try
                {
                    var result = await node.ServeReplicaAsync(
                        SimulatedStorage.Ledger,
                        Options,
                        replica => replica.ReadAsync(new Slot(address), Slot.Zero));
                    result.Release();
                }
                catch (LedgerReplicaCorruptException)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<RegisterOutput> TryReadAsync(long address)
        {
            try
            {
                var result = await replica.ReadAsync(new Slot(address), cancellationToken);
                switch (result.Case)
                {
                    case ReplicaReadCase.Batch:
                        using (var value = result.Batch)
                        {
                            return value.ToArray().AsSpan().SequenceEqual(SimulatedStorage.Payload(address)) ? RegisterOutput.Data : RegisterOutput.WrongData;
                        }

                    case ReplicaReadCase.Missing:
                        return RegisterOutput.Missing;

                    // Backpressure asserts nothing, as a failed read does (ledger-protocol.md §6.4).
                    case ReplicaReadCase.Backpressure:
                        return RegisterOutput.Failed;

                    // The register's ledger is never dropped, so a dropped answer is a false one (LP-I5).
                    case ReplicaReadCase.Dropped:
                        throw new InvalidOperationException($"A read of address {address} answered Dropped, but the register's ledger is never dropped.");

                    case ReplicaReadCase.OutsideBatch:
                    default:
                        throw new UnreachableException();
                }
            }
            catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: true))
            {
                return RegisterOutput.Failed;
            }
        }

        private Task DelayAsync(int maximumMilliseconds) =>
            Task.Delay(environment.Random.NextTimeSpan(TimeSpan.FromMilliseconds(maximumMilliseconds)), environment.TimeProvider, cancellationToken);

        private void Record(FormattableString entry) => environment.Trace.Record(entry.ToString(CultureInfo.InvariantCulture));
    }
}

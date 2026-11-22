using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Runs one ledger's <see cref="Ledger"/>s and its primary's <see cref="LedgerWriter"/> over three LedgerStore replicas on
/// simulated nodes, reached across Clockwork's network (ledger-protocol.md §2, §8; ledgers.md §8.1 – §8.3): a primary
/// which keeps appending and checking that its ledger is open while executors in other processes recover the ledger, the
/// map's close at the first executor's candidate end, and every process learning that recorded <c>T</c>. The build
/// rewrites Clog's assemblies and the kit, so the seed decides every interleaving, network delay, lost or duplicated
/// message, partition, pause, and backoff.
/// </summary>
public sealed class LedgerSimulationTests
{
    private const int Appends = 24;
    private const int Quorum = 2;
    private const int FirstSeed = 3;

    // Every change runs three seeds, and exploration runs more through CLOG_SIM_SEEDS (docs/spec/verification.md §7.4, §12).
    private const int SeedCount = 3;
    private const int ReplaySeed = 5;
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Gets the seed corpus.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    /// <summary>
    /// A deposed primary keeps appending, with up to <c>K</c> appends in flight, and keeps checking that its ledger is
    /// open, while two executors in other processes recover the ledger, across lost and duplicated messages, a partitioned
    /// replica, and a paused one. The primary helps the recovery once its lifetime stops; the map closes the ledger at the
    /// first executor's candidate end <c>T</c>, and every process learns it.
    /// </summary>
    /// <remarks>
    /// After every step the ledger trace checks that no slot at or above a candidate end completes (LP-I8), that the map
    /// closes the ledger at a candidate end (LG-I9), and the replicas' guarantees, with each replica's reported tail at
    /// most the writer's completed prefix, or <c>T</c> once the map records it (LR-I6, LR-I8, LP-I7, LP-I9). At the
    /// end, the appended batches hold the slots of a prefix below <c>T</c> (LP-I3, LP-I8); every slot below <c>T</c>
    /// holds the batch of the append which acquired it, so an append there which met a seal or could not complete is
    /// durable (LP-I7, ledgers.md §8.2); every tail a process reports is at most <c>T</c>, and each which learned
    /// <c>T</c> reports it (LP-I11); and the primary's open check, once it finds the ledger sealed, leaves its lifetime
    /// stopped, so its next append acquires nothing (ledgers.md §8.3, LP-I13).
    /// </remarks>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ADeposedPrimaryWhichKeepsAppendingWhileAnotherProcessRecoversLosesNoAcknowledgedAppend(int seed) =>
        _ = SeededSimulation.Run(seed, SimulateAsync);

    /// <summary>
    /// The scenario replays from its seed and from its artifact, so the writer's jittered write backoff and the
    /// recovery's backoff are deterministic under Clockwork. Its executors start only once every append has completed,
    /// so the primary's open check, not a seal its appends meet, is what stops its lifetime, which the ledger trace
    /// checks (ledgers.md §8.3).
    /// </summary>
    [Fact]
    public void TheScenarioReplaysFromItsSeed()
    {
        var run = SeededSimulation.RunAndReplay(ReplaySeed, SimulateAppendsBeforeTheSealAsync);

        var summary = string.Create(CultureInfo.InvariantCulture, $"{Appends} acknowledged, {Appends} slots taken; recorded T {Appends}");
        Assert.Contains(run.Trace, entry => entry.EndsWith(summary, StringComparison.Ordinal));
    }

    private static Task SimulateAsync(SimulationEnvironment environment) => SimulateAsync(environment, executorsAfterTheAppends: false);

    private static Task SimulateAppendsBeforeTheSealAsync(SimulationEnvironment environment) => SimulateAsync(environment, executorsAfterTheAppends: true);

    private static async Task SimulateAsync(SimulationEnvironment environment, bool executorsAfterTheAppends)
    {
        // The scenario's calls carry a cancellable token, as production callers' do, which nothing cancels: the scenario
        // owns it, so no thread outside the simulation cancels it (docs/spec/verification.md §7.1). CancellationToken.None
        // would skip the code under test's registrations on it, and change the seeds' schedules.
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(4);
        network.MessageDropRate = 0.05;
        environment.Rpc.DuplicationRate = 0.05;
        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 3, cancellationToken);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        replicas.LedgerTrace.LogsRecoveredReplicas = LedgerTraces.Enabled;
        _ = LedgerStoreInvariants.Register(environment, replicas);
        await new DeposedPrimaryScenario(environment, replicas, nodes, executorsAfterTheAppends, cancellationToken).RunAsync();
        if (!executorsAfterTheAppends)
        {
            LedgerTraces.Export(environment, "ledger", replicas.LedgerTrace, SimulatedStorage.Ledger, nodes, Quorum, Options.WindowSize);
        }
    }

    /// <summary>
    /// The primary's appends and open checks, the executors' seals, the map's close, and the faults, until the faults heal
    /// and every operation ends; with <c>executorsAfterTheAppends</c>, the executors start only once every append has
    /// completed.
    /// </summary>
    private sealed class DeposedPrimaryScenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        LedgerStoreNode[] nodes,
        bool executorsAfterTheAppends,
        CancellationToken cancellationToken)
    {
        private readonly SimulationRandom _random = environment.Random;
        private readonly LedgerTrace _ledger = replicas.LedgerTrace;
        private readonly List<(string Client, Ledger Ledger)> _ledgers = [];
        private readonly List<Task> _observations = [];
        private readonly Dictionary<long, int> _appendAt = [];
        private readonly List<long> _acknowledged = [];
        private readonly List<long> _primaryTails = [];
        private Ledger? _primaryLedger;
        private LedgerWriter? _primary;
        private Task? _helping;
        private long? _recorded;
        private bool _checking = true;

        private static LedgerId Ledger => SimulatedStorage.Ledger;

        private LedgerWriter Primary => _primary!;

        private Ledger PrimaryLedger => _primaryLedger!;

        private static byte[] Payload(int index) => [.. BitConverter.GetBytes(index), 0xA7, 0x10];

        public async Task RunAsync()
        {
            try
            {
                _primaryLedger = Open("primary");
                _primary = PrimaryLedger.CreateWriter(SimulationEnvironment.Clock);
                _ledger.TraceWriter(Primary, "primary", Ledger);
                _observations.Add(ObserveWhenClosedAsync("primary", PrimaryLedger));
                var faulty = _random.Choose(nodes);
                var slow = _random.Choose(nodes.Where(node => node != faulty).ToArray());
                var checks = CheckOpenAsync();
                var appends = SimulationTasks.WhenAll(Enumerable.Range(0, Appends).Select(AppendAsync));
                var start = executorsAfterTheAppends ? appends : Task.CompletedTask;
                var executors = SimulationTasks.WhenAll(ExecuteAsync("executor-1", start, maximumDelayMilliseconds: 300), ExecuteAsync("executor-2", start, maximumDelayMilliseconds: 400));
                await HealAsync(SimulationTasks.WhenAll(PartitionAsync(faulty), PauseAsync(slow)));
                await executors;
                await appends;
                await (_helping ?? Task.CompletedTask);
                await SimulationTasks.WhenAll(_observations);
                _checking = false;
                await checks;
                await VerifyAsync();
            }
            finally
            {
                if (_primary is { } primary)
                {
                    await primary.DisposeAsync();
                }

                foreach (var (_, ledger) in _ledgers)
                {
                    await ledger.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Opens a process's <see cref="Ledger"/> of the ledger over the three replicas, on the simulation's clock.
        /// </summary>
        private Ledger Open(string client)
        {
            var ledger = replicas.OpenLedger(Ledger, Options, Quorum, client);
            _ledgers.Add((client, ledger));
            return ledger;
        }

        /// <summary>
        /// Appends one payload after a random delay, recording the position its slot took and its outcome; an append which
        /// met a seal or could not complete leads the primary to help the recovery.
        /// </summary>
        private async Task AppendAsync(int index)
        {
            await DelayAsync(150);
            using var buffer = new ArcBufferWriter();
            buffer.Write(Payload(index));
            using var payload = buffer.PeekSlice(buffer.Length);
            var outcome = await Primary.AppendAsync(payload, cancellationToken);
            switch (outcome.Value)
            {
                case AppendOutcome.Appended appended:
                    _appendAt.Add(appended.Slot.Value, index);
                    _acknowledged.Add(appended.Slot.Value);
                    return;
                case AppendOutcome.Sealed sealedAt:
                    _appendAt.Add(sealedAt.Slot.Value, index);
                    Record($"append {index} at {sealedAt.Slot.Value} met a seal");
                    break;
                case AppendOutcome.Unresolved unresolved:
                    if (unresolved.Slot is { } slot)
                    {
                        _appendAt.Add(slot.Value, index);
                    }

                    Record($"append {index} unresolved at {unresolved.Slot?.Value.ToString(CultureInfo.InvariantCulture) ?? "no slot"}");
                    break;
                default:
                    throw new InvalidOperationException("An append outcome holds no case.");
            }

            Help();
        }

        /// <summary>
        /// Checks that the ledger is open at random times until the run ends (ledgers.md §8.3): a check which finds it
        /// sealed stops the primary's lifetime, and the primary helps the recovery.
        /// </summary>
        private async Task CheckOpenAsync()
        {
            while (_checking)
            {
                await DelayAsync(40);
                try
                {
                    var tail = await _ledger.CheckSealAsPrimaryAsync(PrimaryLedger, "primary", Ledger, cancellationToken);
                    _primaryTails.Add(tail.Tail.ContiguousTail.Value);
                    if (tail.Tail.IsSealed)
                    {
                        Help();
                    }
                }
                catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: false))
                {
                    // No quorum answered (ledger-replicas §6.4).
                }
            }
        }

        /// <summary>
        /// Once its lifetime stops, a primary whose map view lists its ledger open recovers the ledger itself through its
        /// writer, from its own known contiguous tail (ledgers.md §8.2). Its candidate end decides nothing.
        /// </summary>
        private void Help()
        {
            if (_helping is null && _recorded is null)
            {
                _helping = _ledger.RecoverAsPrimaryAsync(PrimaryLedger, "primary", Ledger, cancellationToken).AsTask();
            }
        }

        /// <summary>
        /// Recovers the ledger as a transition's executor in another process does (ledgers.md §8.1), through a
        /// <see cref="Ledger"/> of its own: the primary's lifetime lives in the primary's process, which the recovery
        /// cannot stop (LP-I16). The first executor whose recovery returns writes the map's close at its candidate end,
        /// and each executor then learns the recorded <c>T</c>. It starts after a random delay once <paramref name="start"/>
        /// completes.
        /// </summary>
        private async Task ExecuteAsync(string client, Task start, int maximumDelayMilliseconds)
        {
            await start;
            await DelayAsync(maximumDelayMilliseconds);
            var ledger = Open(client);
            var end = (await _ledger.RecoverAsync(ledger, client, Ledger, cancellationToken)).End;
            if (_recorded is null)
            {
                _recorded = end.Value;
                _ledger.Closed(Ledger, client, end.Value);
            }
            else
            {
                _ledger.Superseded(Ledger, client);
            }

            _observations.Add(ObserveWhenClosedAsync(client, ledger));
        }

        /// <summary>
        /// Passes the recorded <c>T</c> to <paramref name="ledger"/> at a random time after the map records it, as a process's
        /// map view learns it (ledgers.md §8.1).
        /// </summary>
        private async Task ObserveWhenClosedAsync(string client, Ledger ledger)
        {
            while (_recorded is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), environment.TimeProvider, cancellationToken);
            }

            await DelayAsync(20);
            _ledger.ObserveRecordedTail(ledger, client, Ledger, new Slot(_recorded.Value));
        }

        private async Task VerifyAsync()
        {
            Assert.NotNull(_recorded);
            var end = _recorded.Value;

            // LP-I3, LP-I8: the appended batches hold the slots of a prefix, which lies below T.
            _acknowledged.Sort();
            Assert.Equal(Enumerable.Range(0, _acknowledged.Count).Select(static position => (long)position), _acknowledged);
            Assert.True(_acknowledged.Count <= end, Invariant($"{_acknowledged.Count} appends were acknowledged, above the recorded T {end}"));

            // LP-I7, ledgers.md §8.2: every slot below T holds the batch of the append which acquired it.
            var (_, reader) = _ledgers[^1];
            for (var position = 0L; position < end; position++)
            {
                Assert.True(_appendAt.TryGetValue(position, out var index), Invariant($"no append acquired slot {position}, below the recorded T {end}"));
                var read = await ReadAsync(reader, position);
                using var value = read.Batch;
                Assert.Equal(ReplicaReadCase.Batch, read.Case);
                Assert.Equal(Payload(index), value.ToArray());
            }

            // LP-I11: every tail a process reports is at most T, and each which learned T reports it.
            Assert.All(_primaryTails, tail => Assert.True(tail <= end, Invariant($"the primary reported tail {tail}, above the recorded T {end}")));
            Assert.Equal(new LedgerTail(new Slot(end), IsSealed: true), (await CheckSealAsync(() => _ledger.CheckSealAsPrimaryAsync(PrimaryLedger, "primary", Ledger, cancellationToken))).Tail);
            foreach (var (client, ledger) in _ledgers.Skip(1))
            {
                Assert.Equal(new LedgerTail(new Slot(end), IsSealed: true), (await CheckSealAsync(() => _ledger.CheckSealAsync(ledger, client, Ledger, cancellationToken))).Tail);
            }

            // ledgers.md §8.3: the primary's open check found the ledger sealed, and the ledger trace checked that it
            // stopped the primary's lifetime, so its next append acquires no slot.
            using var buffer = new ArcBufferWriter();
            buffer.Write(Payload(Appends));
            using var payload = buffer.PeekSlice(buffer.Length);
            Assert.Equal(new AppendOutcome(new AppendOutcome.Unresolved(Slot: null)), await Primary.AppendAsync(payload, cancellationToken));
            Record($"{_acknowledged.Count} acknowledged, {_appendAt.Count} slots taken; recorded T {end}");
        }

        /// <summary>
        /// Reads <paramref name="position"/>, repeating a read which fails as the faults explain.
        /// </summary>
        private async Task<ReplicaRead> ReadAsync(Ledger ledger, long position)
        {
            while (true)
            {
                try
                {
                    return await ledger.ReadAsync(new Slot(position), cancellationToken);
                }
                catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: false))
                {
                    await DelayAsync(10);
                }
            }
        }

        /// <summary>
        /// Checks the seal through <paramref name="check"/>, repeating a check which fails as the faults explain.
        /// </summary>
        private async Task<LedgerTailRead> CheckSealAsync(Func<ValueTask<LedgerTailRead>> check)
        {
            while (true)
            {
                try
                {
                    return await check();
                }
                catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: false))
                {
                    await DelayAsync(10);
                }
            }
        }

        private async Task PartitionAsync(LedgerStoreNode node)
        {
            await DelayAsync(20);
            node.Isolate();
            await DelayAsync(30);
            node.Reconnect();
        }

        private async Task PauseAsync(LedgerStoreNode node)
        {
            await DelayAsync(20);
            var pause = _random.NextTimeSpan(TimeSpan.FromMilliseconds(100));
            node.Pause(pause);
            await Task.Delay(pause + TimeSpan.FromMilliseconds(1), environment.TimeProvider, cancellationToken);
        }

        /// <summary>
        /// Stops message loss and duplication at a random time after the partition heals and the paused node resumes.
        /// </summary>
        private async Task HealAsync(Task faults)
        {
            await faults;
            await Task.Delay(TimeSpan.FromMilliseconds(1) + _random.NextTimeSpan(TimeSpan.FromMilliseconds(20)), environment.TimeProvider, cancellationToken);
            environment.Network.MessageDropRate = 0;
            environment.Rpc.DuplicationRate = 0;
            Record($"faults healed");
        }

        private Task DelayAsync(int maximumMilliseconds) =>
            Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(maximumMilliseconds)), environment.TimeProvider, cancellationToken);

        private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

        private void Record(FormattableString entry) => environment.Trace.Record(entry.ToString(CultureInfo.InvariantCulture));
    }
}

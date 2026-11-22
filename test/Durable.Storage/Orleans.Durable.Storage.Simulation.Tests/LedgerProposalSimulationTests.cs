using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Runs one ledger's primary, executors, and readers, each in a process of its own with its own map view, over three
/// LedgerStore replicas on simulated nodes and a simulated <c>ledger-map</c> register (ledgers.md §2.4, §2.5, §8.2 –
/// §8.4): a deposed primary which keeps proposing and checking that its ledger is open while two executors recover the
/// ledger and the first closes it in the map, and readers whose map views stay stale until a sealed answer makes them
/// re-read the map. The build rewrites Clog's assemblies and the kit, so the seed decides every interleaving, network
/// delay, lost or duplicated message, partition, pause, and backoff.
/// </summary>
public sealed class LedgerProposalSimulationTests
{
    private const int Proposals = 24;
    private const int Quorum = 2;
    private const int Window = 4;
    private const int ReplaySeed = 3;
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = Window };
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);
    private const int FirstSeed = 1;

    // Every change runs three seeds and exploration runs more through CLOG_SIM_SEEDS
    // (docs/spec/verification.md §7.4, §12); the replay check reuses one of them.
    private const int SeedCount = 3;

    // The trace entry which counts, for a run, the proposals whose write met a seal, each decided from the map's
    // recorded T, and the readers' map re-reads after a sealed answer.
    private const string CountsEntry = "sealed writes and sealed reader checks: ";
    private static readonly byte[] Key = [0x10];

    /// <summary>
    /// Gets the seed corpus.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(FirstSeed, SeedCount);

    // The theory, the corpus check, and the replay check share each seed's run, so each runs once.
    private static readonly ConcurrentDictionary<int, Lazy<ClusterSimulationRun>> Runs = new();

    /// <summary>
    /// LG-I5 and LG-I8, over a deposed primary which keeps proposing and readers with stale maps.
    /// </summary>
    /// <remarks>
    /// At the end: the proposals decided <see cref="ProposalOutcome.Durable"/> are exactly the slots below the recorded
    /// <c>T</c>, a prefix of the proposals in call order, after which every proposal is
    /// <see cref="ProposalOutcome.Fenced"/>, and none is <see cref="ProposalOutcome.Ambiguous"/>, since the ledger stays in
    /// the map; each durable slot holds its proposal's batch; every open check's tail is at most <c>T</c>; and every batch
    /// a reader saw lies below <c>T</c> and is its slot's decided batch, while each reader reaches <c>T</c>. After every
    /// step the ledger trace checks the replicas' guarantees and that no slot at or above a candidate end completes.
    /// </remarks>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ADeposedPrimaryWhichKeepsProposingAndReadersWithStaleMapsSeeOnlyDecidedBatches(int seed) => _ = RunSeed(seed);

    /// <summary>
    /// ledgers.md §8.2, §8.4: across the corpus, some proposal whose write met a seal is decided from the map's recorded
    /// <c>T</c>, and some reader re-reads the map after a sealed answer, so the corpus exercises both.
    /// </summary>
    [Fact]
    public void TheCorpusDecidesSealedProposalsFromTheRecordedTailAndReReadsTheMapAfterSealedAnswers()
    {
        var (writes, checks) = (0, 0);
        foreach (var seed in SeededSimulation.CorpusSeeds(FirstSeed, SeedCount))
        {
            var counts = RunSeed(seed).Trace.Single(static entry => entry.Contains(CountsEntry, StringComparison.Ordinal));
            var values = counts[(counts.IndexOf(CountsEntry, StringComparison.Ordinal) + CountsEntry.Length)..].Split(' ');
            writes += int.Parse(values[0], CultureInfo.InvariantCulture);
            checks += int.Parse(values[1], CultureInfo.InvariantCulture);
        }

        Assert.True(writes > 0, "No proposal in the corpus met a seal.");
        Assert.True(checks > 0, "No reader in the corpus re-read the map after a sealed answer.");
    }

    /// <summary>
    /// The scenario replays from its seed and from its artifact.
    /// </summary>
    [Fact]
    public void TheScenarioReplaysFromItsSeed()
    {
        var run = SeededSimulation.AssertReplays(RunSeed(ReplaySeed), SimulateAsync);

        Assert.Contains(run.Trace, entry => entry.Contains("durable below recorded T", StringComparison.Ordinal));
    }

    private static ClusterSimulationRun RunSeed(int seed) =>
        Runs.GetOrAdd(seed, static seed => new Lazy<ClusterSimulationRun>(() => SeededSimulation.Run(seed, SimulateAsync))).Value;

    private static async Task SimulateAsync(SimulationEnvironment environment)
    {
        // The scenario owns the token, so no thread outside the simulation cancels it (docs/spec/verification.md §7.1).
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
        _ = LedgerStoreInvariants.Register(environment, replicas);
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
        await new Scenario(environment, replicas, register, nodes, cancellationToken).RunAsync();
    }

    /// <summary>
    /// The primary's proposals, open checks, and map follower; the executors' recoveries and the map's close; the readers;
    /// and the faults, until the faults heal and every operation ends.
    /// </summary>
    private sealed class Scenario(
        SimulationEnvironment environment,
        SimulatedLedgerReplicaTransport replicas,
        SimulatedConditionalRegister register,
        LedgerStoreNode[] nodes,
        CancellationToken cancellationToken)
    {
        private readonly SimulationRandom _random = environment.Random;
        private readonly LedgerTrace _trace = replicas.LedgerTrace;
        private readonly List<IAsyncDisposable> _owned = [];
        private readonly List<(int Call, int Index, ProposalOutcome Outcome)> _outcomes = [];
        private readonly List<(string Reader, long Slot, int Index)> _seen = [];
        private readonly Dictionary<string, long> _readerEnds = [];
        private readonly List<long> _openTails = [];
        private int _calls;
        private int _sealedWrites;
        private int _sealedChecks;
        private LedgerReader.LedgerLease _primaryLease;
        private Ledger? _primaryLedger;
        private LedgerWriter? _writer;
        private LedgerMapView? _primaryView;
        private LedgerMetadata _ledger;
        private Task? _helping;
        private long? _recorded;
        private bool _checking = true;

        private static LedgerId Id => SimulatedStorage.Ledger;

        private LedgerWriter Writer => _writer!;

        public async Task RunAsync()
        {
            try
            {
                // The primary's process reaches the ledger through its reader, which opens the process's Ledger from the
                // map's entry, and creates the writer through its lease over the lifetime its claim names.
                _primaryView = await LedgerMapView.OpenAsync(register, cancellationToken);
                var primaryReader = new LedgerReader(_primaryView, new TracedTransport(replicas, "primary"), SimulationEnvironment.Clock, localNode: null) { RecoveryObserver = LedgerTrace.RecoveryObserver };
                _owned.Add(primaryReader);
                var lifetime = new Sequencer(Window);
                _ledger = new LedgerMetadata(
                    Id,
                    KeyRange.Root,
                    [.. nodes.Select(static node => node.NodeId)],
                    Window,
                    new PrimaryClaim(new NodeId(Guid.Parse("00000000-0000-0000-0000-00000000aaaa")), lifetime.InstanceId),
                    LedgerStatus.Open.Instance);
                _primaryLease = primaryReader.LeaseLedger(_ledger);
                _primaryLedger = _primaryLease.Ledger;
                _trace.Register(_primaryLedger, [.. nodes.Select(static node => node.Address)]);
                _writer = _primaryLease.CreateWriter(lifetime, _primaryView, new LedgerWriterHost(int.MaxValue, NullLogger.Instance), SimulationEnvironment.Clock);
                _trace.TraceWriter(Writer, "primary", Id);
                var traced = Writer.Sequencer.Observer!;
                Writer.Sequencer.Observer = step =>
                {
                    traced(step);
                    if (step.Kind == SequencerStepKind.Sealed)
                    {
                        // The write met a seal, so the map's recorded T decides its proposal (ledgers.md §8.2).
                        _sealedWrites++;
                    }
                };
                await WriteMapAsync(new LedgerMapValue(1, new LedgerId(Id.Value + 1), 0, [_ledger]));
                _ = await _primaryView.ReadAsync(cancellationToken);

                // Each reader's view reads the map once, now, and again only when a sealed or dropped answer makes it.
                var readers = SimulationTasks.WhenAll(ReadAsync("reader-1"), ReadAsync("reader-2"));
                var faulty = _random.Choose(nodes);
                var slow = _random.Choose(nodes.Where(node => node != faulty).ToArray());
                var checks = CheckOpenAsync();
                var following = FollowMapAsync();
                var proposals = SimulationTasks.WhenAll(Enumerable.Range(0, Proposals).Select(ProposeAsync));
                var executors = SimulationTasks.WhenAll(ExecuteAsync("executor-1", maximumDelayMilliseconds: 300), ExecuteAsync("executor-2", maximumDelayMilliseconds: 400));
                await HealAsync(SimulationTasks.WhenAll(PartitionAsync(faulty), PauseAsync(slow)));
                await executors;
                await proposals;
                await following;
                await (_helping ?? Task.CompletedTask);
                await readers;
                _checking = false;
                await checks;
                await VerifyAsync();
            }
            finally
            {
                if (_writer is { } writer)
                {
                    await writer.DisposeAsync();
                }

                _primaryLease.Dispose();

                foreach (var owned in Enumerable.Reverse(_owned))
                {
                    await owned.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Proposes one batch after a random delay and records its outcome in its call's order; a proposal after which the
        /// lifetime is stopped leads the primary to help the recovery (ledgers.md §8.2).
        /// </summary>
        private async Task ProposeAsync(int index)
        {
            await DelayAsync(150);
            var call = _calls++;
            ProposalOutcome outcome;
            using (var buffer = new ArcBufferWriter())
            {
                buffer.Write(BitConverter.GetBytes(index));
                using var payload = buffer.PeekSlice(buffer.Length);
                using var row = new Row(Key, RowKind.Append, payload);

                // The batch is created in call order, once the batch created before it is proposed, so the row lives until
                // the proposal took its bytes.
                outcome = await Writer.ProposeAsync([row], cancellationToken);
            }

            _outcomes.Add((call, index, outcome));
            if (Writer.Sequencer.IsStopped)
            {
                Help();
            }
        }

        /// <summary>
        /// Checks that the ledger is open at random times until the run ends (ledgers.md §8.3); a check which finds it sealed
        /// leaves the lifetime stopped, and the primary helps the recovery.
        /// </summary>
        private async Task CheckOpenAsync()
        {
            while (_checking)
            {
                await DelayAsync(40);
                try
                {
                    switch ((await Writer.ConfirmOpenAsync(cancellationToken)).Value)
                    {
                        case OpenCheckOutcome.Open open:
                            _openTails.Add(open.ContiguousTail.Value);
                            break;
                        case OpenCheckOutcome.Sealed:
                            Assert.True(Writer.Sequencer.IsStopped, "An open check which found the ledger sealed left the lifetime running (LP-I14).");
                            Help();
                            break;
                        case ReplicaWriteOutcome.Backpressure:
                            break;
                        default:
                            throw new InvalidOperationException("An open check holds no case.");
                    }
                }
                catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: false))
                {
                    // No quorum answered (ledger-replicas §6.4).
                }
            }
        }

        /// <summary>
        /// The primary's process reads the map from time to time, as its composition does, until its view lists the ledger
        /// closed (ledgers.md §8.3).
        /// </summary>
        private async Task FollowMapAsync()
        {
            while (_primaryView!.Current.Get(Id)?.IsOpen ?? false)
            {
                await DelayAsync(60);
                _ = await _primaryView.ReadAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Once its lifetime stops, a primary whose map view lists its ledger open recovers it from its own known contiguous
        /// tail (ledgers.md §8.2). Its candidate end decides nothing.
        /// </summary>
        private void Help()
        {
            if (_helping is null && _recorded is null)
            {
                _helping = _trace.RecoverAsPrimaryAsync(_primaryLedger!, "primary", Id, cancellationToken).AsTask();
            }
        }

        /// <summary>
        /// Recovers the ledger as a transition's executor in another process does; the first whose recovery returns closes
        /// the ledger in the map at its candidate end, opening a successor over the range (ledgers.md §8.1).
        /// </summary>
        private async Task ExecuteAsync(string client, int maximumDelayMilliseconds)
        {
            await DelayAsync(maximumDelayMilliseconds);
            var ledger = replicas.OpenLedger(Id, Options, Quorum, client);
            _owned.Add(ledger);
            var end = (await _trace.RecoverAsync(ledger, client, Id, cancellationToken)).End;
            if (_recorded is not null)
            {
                return;
            }

            _recorded = end.Value;
            _trace.Closed(Id, client, end.Value);
            var successor = new LedgerMetadata(
                new LedgerId(Id.Value + 1),
                KeyRange.Root,
                _ledger.Replicas,
                Window,
                new PrimaryClaim(new NodeId(Guid.Parse("00000000-0000-0000-0000-00000000bbbb")), new SequencerInstanceId(Guid.Parse("00000000-0000-0000-0000-00000000cccc"))),
                LedgerStatus.Open.Instance);
            await WriteMapAsync(new LedgerMapValue(2, new LedgerId(Id.Value + 2), 0, [_ledger.Close(end), successor]));
            Record($"map closes s{Id} at {end.Value}");
        }

        /// <summary>
        /// A reader in a process of its own: it reads the ledger below the contiguous tail its seal checks learn while its
        /// view lists the ledger open, and below <c>T</c> once its view lists it closed, recording every batch it sees,
        /// until it has read every slot below <c>T</c> (ledgers.md §2.5, §8.4).
        /// </summary>
        private async Task ReadAsync(string name)
        {
            var view = await LedgerMapView.OpenAsync(register, cancellationToken);
            var reader = new LedgerReader(view, new TracedTransport(replicas, name), SimulationEnvironment.Clock, localNode: null) { RecoveryObserver = LedgerTrace.RecoveryObserver };
            _owned.Add(reader);
            var next = Slot.Zero;
            while (true)
            {
                await DelayAsync(30);
                var ledger = view.Current.Get(Id)!.Value;
                try
                {
                    Slot below;
                    if (ledger.Status.Value is LedgerStatus.Closed closed)
                    {
                        below = closed.T;
                    }
                    else
                    {
                        var answer = (await reader.ReadTailAsync(ledger, cancellationToken)).Value;
                        if (answer is LedgerTail { IsSealed: true })
                        {
                            _sealedChecks++;
                        }

                        if (answer is not LedgerTail { IsSealed: false } tail)
                        {
                            // A sealed answer made the reader re-read the map, so it starts again from its view; backpressure
                            // asserts nothing, so it tries again.
                            continue;
                        }

                        below = tail.ContiguousTail;
                    }

                    await foreach (var item in reader.ScanAsync(ledger, next, below, cancellationToken))
                    {
                        var read = item.Read;
                        if (read.TryGetValue(out ReplicaWriteOutcome.Backpressure? _))
                        {
                            break;
                        }

                        Assert.True(read.TryGetValue(out LedgerRead.Data data), Invariant($"{name} read {read} below a contiguous tail"));
                        using (data)
                        {
                            _seen.Add((name, data.Slot.Value, DecidedBatches.IndexOf(data)));
                        }

                        next = data.Slot + 1;
                    }

                    if (ledger.Status.Value is LedgerStatus.Closed { T: var end } && next == end)
                    {
                        _readerEnds.Add(name, next.Value);
                        return;
                    }
                }
                catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: false))
                {
                    // No quorum answered (ledger-replicas §6.4); the reader starts again from its view.
                }
            }
        }

        private async Task VerifyAsync()
        {
            Assert.NotNull(_recorded);
            var end = _recorded.Value;
            Assert.Equal(Proposals, _outcomes.Count);

            // LG-I5: the durable proposals are a prefix in call order, exactly the slots below T, and every later one is
            // Fenced; the ledger stays in the map, so none is Ambiguous.
            var durableAt = new Dictionary<long, int>();
            var fenced = false;
            foreach (var (call, index, outcome) in _outcomes.OrderBy(static outcome => outcome.Call))
            {
                switch (outcome.Value)
                {
                    case ProposalOutcome.Durable durable:
                        Assert.False(fenced, Invariant($"proposal {call} is durable at {durable.Slot} after a fenced proposal"));

                        // ledger-protocol.md §2.6: proposals take slots in the order of their calls.
                        Assert.True(durable.Slot.Value == call, Invariant($"proposal {call} is durable at slot {durable.Slot}"));
                        Assert.True(durable.Slot.Value < end, Invariant($"proposal {call} is durable at {durable.Slot}, at or above the recorded T {end}"));
                        durableAt.Add(durable.Slot.Value, index);
                        break;
                    case ProposalOutcome.Fenced:
                        fenced = true;
                        break;
                    case ProposalOutcome.Ambiguous:
                        Assert.Fail(Invariant($"proposal {call} is ambiguous, though its ledger stayed in the map"));
                        break;
                    default:
                        throw new InvalidOperationException("A proposal outcome holds no case.");
                }
            }

            Assert.Equal(Enumerable.Range(0, (int)end).Select(static slot => (long)slot), durableAt.Keys.Order());

            // Each durable slot holds its proposal's batch; every open check's tail is at most T.
            var view = await LedgerMapView.OpenAsync(register, cancellationToken);
            await using (var reader = new LedgerReader(view, new TracedTransport(replicas, "verifier"), SimulationEnvironment.Clock, localNode: null))
            {
                await DecidedBatches.VerifyAsync(reader, view.Current.Get(Id)!.Value, durableAt, () => DelayAsync(10), cancellationToken);
            }

            // Replica-side, each decided slot holds a batch which at least Q replicas answered Written to a writer or
            // recovery write (LP-I5).
            for (var decided = 0L; decided < end; decided++)
            {
                var written = _trace.Events
                    .Where(entry => entry.Kind == LedgerEventKind.Served && entry.Ledger == Id && entry.Address == decided)
                    .Where(static entry => entry.Operation is LedgerOperation.WriterWrite or LedgerOperation.RecoveryWrite)
                    .Where(static entry => entry.Answer == nameof(ReplicaWriteOutcome.Written))
                    .Select(static entry => entry.Node)
                    .Distinct()
                    .Count();
                Assert.True(written >= Quorum, Invariant($"decided slot {decided} was written at {written} replicas"));
            }

            Assert.All(_openTails, tail => Assert.True(tail <= end, Invariant($"an open check reported tail {tail}, above the recorded T {end}")));

            // LG-I8: every batch a reader saw lies below T and is its slot's decided batch, and each reader reached T.
            foreach (var (name, slot, index) in _seen)
            {
                Assert.True(slot < end, Invariant($"{name} saw slot {slot}, at or above the recorded T {end}"));
                Assert.Equal(durableAt[slot], index);
            }

            Assert.Equal(new Dictionary<string, long> { ["reader-1"] = end, ["reader-2"] = end }, _readerEnds);
            Record($"{durableAt.Count} proposals durable below recorded T {end}, {Proposals - durableAt.Count} fenced; readers saw {_seen.Count} batches");
            Record($"{CountsEntry}{_sealedWrites} {_sealedChecks}");
        }

        private async Task WriteMapAsync(LedgerMapValue value)
        {
            using var encoded = value.Encode();
            Assert.IsType<RegisterWriteOutcome.Applied>((await register.WriteAsync(register.Version, encoded.AsReadOnlySequence(), cancellationToken)).Value);
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

    /// <summary>
    /// A reader's process's transport: each replica it opens is the client's, traced so the ledger trace checks its reads.
    /// </summary>
    private sealed class TracedTransport(SimulatedLedgerReplicaTransport replicas, string client) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            replicas.LedgerTrace.Traced(
                replicas.Open(node, ledger, options, client),
                client,
                replicas.Nodes.Single(candidate => candidate.NodeId == node).Address,
                ledger);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            replicas.HeartbeatAsync(node, ledgers, cancellationToken);
    }
}

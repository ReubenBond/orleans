using System.Collections.Immutable;
using System.Diagnostics;
using CsCheck;
using Microsoft.Accordant;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// Checks <see cref="JournalStore"/> over live ledgers on yielding memory replicas, a
/// <see cref="SimulatedConditionalRegister"/>, and a <see cref="SimulatedArchiveStore"/> against
/// <see cref="JournalStoreSpec"/> (verification.md §3.1, §4.1; journals.md §10), each case in a Clockwork simulation of
/// its own: concurrent windows explored by <see cref="WindowScheduleExplorer"/> within the preemption bound, seeded
/// sequences and concurrent sequences over the full operation set (including takeovers, fenced writes, crashing writes
/// which leave <c>Ambiguous</c> outcomes, expiry scans, and archive stale-snapshot restarts), and race-mutant witnesses
/// which pass the default schedule and fail only under explored interleavings.
/// </summary>
public sealed class SimulatedJournalStoreConformanceTests
{
    /// <summary>The archive store's limit on a generation's appends, which <see cref="JournalStoreSpec"/> judges.</summary>
    private const int MaxAppends = 3;

    /// <summary>The archive store's threshold of a generation's appends for its replace request.</summary>
    private const int ReplaceRequestAppends = 2;

    private static readonly JournalStoreSpecOptions Limits = new(MaxAppends, ReplaceRequestAppends);

    private static readonly NodeId PrimaryNode = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"));

    private static readonly ImmutableArray<NodeId> ThreeReplicas =
    [
        PrimaryNode,
        new(Guid.Parse("00000000-0000-0000-0000-00000000000b")),
        new(Guid.Parse("00000000-0000-0000-0000-00000000000c")),
    ];

    /// <summary>
    /// The stride of the explored windows at the gate: every third case runs under <see cref="WindowScheduleExplorer"/>
    /// (covering empty, cross-key, cold/warm populated, limit-reached, deleted/expiry-scan, taken-over, fenced, and
    /// crash-recovered states) and the rest run under their seeded schedules; <c>DURABLE_STORAGE_SCHEDULE_BOUND=deep</c> explores
    /// every window.
    /// </summary>
    private const int GateStride = 3;

    /// <summary>
    /// Concurrent windows of <see cref="JournalStoreSpec"/> over a warm and a cold primary, empty and populated journals,
    /// the archive store's append limit, deleted tombstones eligible for expiry, cross-key batching, ledger takeovers,
    /// fenced writes, and crash recovery conform: every <see cref="GateStride"/>th case under every explored schedule of
    /// <see cref="WindowScheduleExplorer"/> within the active bound and the rest under their seeded schedules, or every
    /// case explored under <c>DURABLE_STORAGE_SCHEDULE_BOUND=deep</c>.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = Kit(
            conformance,
            (environment, seed) => Target.StartAsync(
                environment,
                seed,
                replicas: 1,
                maxLatency: TimeSpan.Zero,
                staleRate: 0,
                warmStateBytes: seed % 2 == 0 ? 0 : null,
                rejectionMutation: LedgerAdmission.RejectionMutant.None));
        var sampledKit = Kit(
            new SimulatedConformance(),
            (environment, seed) => Target.StartAsync(
                environment,
                seed,
                replicas: 1,
                maxLatency: TimeSpan.Zero,
                staleRate: 0,
                warmStateBytes: seed % 2 == 0 ? 0 : null,
                rejectionMutation: LedgerAdmission.RejectionMutant.None));

        var cases = ExploredWindowCases();
        for (var index = 0; index < cases.Count; index++)
        {
            if (ScheduleBound.IsDeep || index % GateStride == 0)
            {
                await kit.RunAsync(cases[index]);
            }
            else
            {
                await sampledKit.RunAsync(cases[index]);
            }
        }

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// CsCheck's sequences and concurrent sequences over the full <see cref="JournalStoreSpec"/> operation set — appends,
    /// replaces, deletes, stale versions, reads, expiry scans, takeovers, fenced writes, and crashing writes — conform over
    /// three-replica quorum ledgers with simulated register and archive latencies and stale archive reads, each case in a
    /// simulation of its own under its seeded schedule.
    /// </summary>
    [Fact]
    public async Task TheStoreConformsOverSimulatedLedgersUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = Kit(
            conformance,
            (environment, seed) => Target.StartAsync(
                environment,
                seed,
                replicas: 3,
                maxLatency: TimeSpan.FromMilliseconds(2),
                staleRate: 0.1,
                warmStateBytes: seed % 2 == 0 ? 0 : null,
                rejectionMutation: LedgerAdmission.RejectionMutant.None));

        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(10), iterations: 60, threads: 1);

        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// A <see cref="LedgerAdmission"/> which decides a write arriving while its key's state is still loading
    /// (<see cref="LedgerAdmission.RejectionMutant.DecidesWhileLoading"/>) against the unloaded state rejects it with
    /// <c>Origin</c> even when the journal is already at a newer version (JS-I1, JS-I4, journals.md §8.1). On the default
    /// LIFO schedule the second call loads, commits, and evicts the key before the first starts, so the case passes; one
    /// preemption runs the first call while the second's load is in flight and fails the spec on every run.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchAdmissionWhichDecidesWhileLoading()
    {
        ConformanceCase witness = new(
        [
            new([Write(0, JournalWriteKind.Append, 0, 1)]),
            new([Write(0, JournalWriteKind.Append, 0, 2), Write(0, JournalWriteKind.Append, 0, 3)]),
            new([Read(0)]),
        ],
        Seed: 0);

        await AssertRaceMutantCaughtAsync(
            witness,
            warmStateBytes: 0,
            LedgerAdmission.RejectionMutant.DecidesWhileLoading,
            "decide-while-loading");
    }

    /// <summary>
    /// A <see cref="LedgerAdmission"/> which rejects a conflicting write immediately from the key's applied state while an
    /// earlier write to the same key is still in flight (<see cref="LedgerAdmission.RejectionMutant.RejectsWaiting"/>)
    /// answers <c>Rejected</c> with the caller's own expected version (JS-I1, JS-I4, journals.md §8.1 step 3). On the
    /// default LIFO schedule the second call finishes and applies before the first starts, so the case passes; one
    /// preemption runs the first call while the second is in flight at the replica and fails the spec on every run.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchAdmissionWhichRejectsWhileInFlight()
    {
        ConformanceCase witness = new(
        [
            new([Write(0, JournalWriteKind.Append, 0, 1), Write(0, JournalWriteKind.Append, 0, 2)]),
            new([Read(0)]),
        ],
        Seed: 1);

        await AssertRaceMutantCaughtAsync(
            witness,
            warmStateBytes: null,
            LedgerAdmission.RejectionMutant.RejectsWaiting,
            "reject-while-in-flight");
    }

    private static async Task AssertRaceMutantCaughtAsync(
        ConformanceCase witness,
        long? warmStateBytes,
        LedgerAdmission.RejectionMutant mutant,
        string label)
    {
        var conformance = new SimulatedConformance(ScheduleBound.Gate);
        var kit = Kit(
            conformance,
            (environment, seed) => Target.StartAsync(
                environment,
                seed,
                replicas: 1,
                maxLatency: TimeSpan.Zero,
                staleRate: 0,
                warmStateBytes: warmStateBytes,
                rejectionMutation: mutant));

        var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(() => kit.RunAsync(witness));

        Assert.Contains("The spec does not allow round", failure.Message, StringComparison.Ordinal);
        var found = Assert.Single(conformance.Explorations);
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);

        const int Seeds = 40;
        var caught = 0;
        var sampledKit = Kit(
            new SimulatedConformance(),
            (environment, seed) => Target.StartAsync(
                environment,
                seed,
                replicas: 1,
                maxLatency: TimeSpan.Zero,
                staleRate: 0,
                warmStateBytes: warmStateBytes,
                rejectionMutation: mutant));
        for (var seed = 1; seed <= Seeds; seed++)
        {
            try
            {
                await sampledKit.RunAsync(witness with { Seed = seed });
            }
            catch (ConformanceScheduleException)
            {
                caught++;
            }
        }

        TestDiagnostics.WriteLine(FormattableString.Invariant(
            $"The {label} mutant: explored, caught at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caught} of {Seeds} seeds."));
    }

    /// <summary>
    /// Returns the concurrent window cases explored under <see cref="WindowScheduleExplorer"/>: even seeds run with
    /// <see cref="JournalStoreOptions.WarmStateBytes"/> zero (cold loads) and odd seeds run with the warm budget, covering
    /// empty, populated, limit-reached, deleted, taken-over, and crash-recovered states.
    /// </summary>
    private static List<ConformanceCase> ExploredWindowCases()
    {
        var cases = new List<ConformanceCase>();
        var nextWarmSeed = 1;
        var nextColdSeed = 2;

        void Add(bool cold, params ConformanceRound[] rounds)
        {
            var seed = cold ? nextColdSeed : nextWarmSeed;
            if (cold)
            {
                nextColdSeed += 2;
            }
            else
            {
                nextWarmSeed += 2;
            }

            cases.Add(new ConformanceCase([.. rounds], Seed: seed));
        }

        // Empty (Origin) journal: competing writes, cross-key batching, and reads (gate indices 0, 3).
        Add(cold: false, Group(Write(0, JournalWriteKind.Append, 0, 1), Write(0, JournalWriteKind.Append, 0, 2)), Group(Read(0)));
        Add(cold: false, Group(Write(0, JournalWriteKind.Append, 0, 1), Write(0, JournalWriteKind.Replace, 0, 2)), Group(Read(0)));
        Add(cold: false, Group(Write(0, JournalWriteKind.Append, 0, 1), Write(0, JournalWriteKind.Delete, 0, 0)), Group(Read(0)));
        Add(cold: false, Group(Write(0, JournalWriteKind.Append, 0, 1), Write(1, JournalWriteKind.Append, 0, 2)), Group(Read(0)), Group(Read(1)));
        Add(cold: false, Group(Write(0, JournalWriteKind.Append, 0, 1), Read(0)), Group(Read(0)));
        Add(cold: false, Group(Write(0, JournalWriteKind.Replace, 0, 1), Read(0)), Group(Read(0)));

        // Existing journal (after one append): cold/warm competing writes, deletes, stale versions, reads (gate indices 6, 9, 12).
        var setupOne = Group(Write(0, JournalWriteKind.Append, 0, 1));
        Add(cold: true, setupOne, Group(Write(0, JournalWriteKind.Append, 0, 2), Write(0, JournalWriteKind.Append, 0, 3)), Group(Read(0)));
        Add(cold: false, setupOne, Group(Write(0, JournalWriteKind.Append, 0, 2), Write(0, JournalWriteKind.Append, 0, 3)), Group(Read(0)));
        Add(cold: false, setupOne, Group(Write(0, JournalWriteKind.Append, 0, 2), Write(0, JournalWriteKind.Replace, 0, 3)), Group(Read(0)));
        Add(cold: false, setupOne, Group(Write(0, JournalWriteKind.Append, 0, 2), Write(0, JournalWriteKind.Delete, 0, 0)), Group(Read(0)));
        Add(cold: false, setupOne, Group(Write(0, JournalWriteKind.Replace, 0, 2), Write(0, JournalWriteKind.Delete, 0, 0)), Group(Read(0)));
        Add(cold: false, setupOne, Group(Write(0, JournalWriteKind.Append, 0, 2), Write(0, JournalWriteKind.Append, 1, 3)), Group(Read(0)));
        Add(cold: true, setupOne, Group(Write(0, JournalWriteKind.Append, 0, 2), Read(0)), Group(Read(0)));
        Add(cold: false, setupOne, Group(Write(0, JournalWriteKind.Delete, 0, 0), Read(0)), Group(Read(0)));

        // At the archive store's append limit (MaxAppends = 3): ReplaceRequired racing Replace, Delete, Append, Read (gate index 15).
        var atLimit = new[]
        {
            Group(Write(0, JournalWriteKind.Append, 0, 1)),
            Group(Write(0, JournalWriteKind.Append, 0, 2)),
            Group(Write(0, JournalWriteKind.Append, 0, 3)),
        };
        Add(cold: false, [.. atLimit, Group(Write(0, JournalWriteKind.Append, 0, 4), Write(0, JournalWriteKind.Delete, 0, 0)), Group(Read(0))]);
        Add(cold: false, [.. atLimit, Group(Write(0, JournalWriteKind.Append, 0, 4), Write(0, JournalWriteKind.Replace, 0, 5)), Group(Read(0))]);
        Add(cold: false, [.. atLimit, Group(Write(0, JournalWriteKind.Append, 0, 4), Write(0, JournalWriteKind.Append, 0, 5)), Group(Read(0))]);
        Add(cold: false, [.. atLimit, Group(Write(0, JournalWriteKind.Append, 0, 4), Read(0)), Group(Read(0))]);

        // Deleted journal (retention = 0): tombstone expiry scan racing Append, Replace, Delete, Read (gate index 18).
        var deleted = Group(Write(0, JournalWriteKind.Delete, 0, 0));
        Add(cold: false, deleted, Group(Write(0, JournalWriteKind.Append, 0, 2), Scan()), Group(Read(0)));
        Add(cold: false, deleted, Group(Write(0, JournalWriteKind.Replace, 0, 2), Scan()), Group(Read(0)));
        Add(cold: false, deleted, Group(Write(0, JournalWriteKind.Delete, 0, 0), Scan()), Group(Read(0)));

        // Takeover, fenced write, and crashing (Ambiguous) write + takeover (gate indices 21, 24, 27).
        Add(cold: false, setupOne, Group(Takeover()), Group(Write(0, JournalWriteKind.Append, 0, 2), Write(0, JournalWriteKind.Append, 0, 3)), Group(Read(0)));
        Add(cold: false, deleted, Group(Read(0), Scan()), Group(Read(0)));
        Add(cold: false, deleted, Group(Write(0, JournalWriteKind.Append, 0, 2), Read(0)), Group(Read(0)));
        Add(cold: true, setupOne, Group(FencedWrite(0, JournalWriteKind.Append, 0, 2)), Group(Write(0, JournalWriteKind.Append, 0, 3), Read(0)), Group(Read(0)));
        Add(cold: false, setupOne, Group(Takeover()), Group(Write(0, JournalWriteKind.Append, 0, 2), Read(0)), Group(Read(0)));
        Add(cold: true, setupOne, Group(FencedWrite(0, JournalWriteKind.Append, 0, 2)), Group(Write(0, JournalWriteKind.Replace, 0, 3), Write(0, JournalWriteKind.Delete, 0, 0)), Group(Read(0)));
        Add(cold: false, Group(CrashingWrite(0, JournalWriteKind.Append, 0, 1)), Group(Takeover()), Group(Read(0), Write(0, JournalWriteKind.Append, 0, 2)), Group(Read(0)));

        return cases;
    }

    private static ConformanceRound Group(params ConformanceCall[] calls) => new([.. calls]);

    private static ConformanceCall Write(int key, JournalWriteKind kind, int back, byte payload) =>
        new(JournalStoreSpec.Write, new JournalWrite(key, kind, back, payload));

    private static ConformanceCall FencedWrite(int key, JournalWriteKind kind, int back, byte payload) =>
        new(JournalStoreSpec.FencedWrite, new JournalWrite(key, kind, back, payload, JournalDisruption.Takeover));

    private static ConformanceCall CrashingWrite(int key, JournalWriteKind kind, int back, byte payload) =>
        new(JournalStoreSpec.CrashingWrite, new JournalWrite(key, kind, back, payload, JournalDisruption.Crash));

    private static ConformanceCall Read(int key) => new(JournalStoreSpec.Read, new JournalRead(key));

    private static ConformanceCall Takeover() => new(JournalStoreSpec.Takeover, JournalTakeover.Instance);

    private static ConformanceCall Scan() => new(JournalStoreSpec.ExpiryScan, JournalScan.Instance);

    private static ConformanceKit<JournalStoreState, Target> Kit(
        SimulatedConformance conformance,
        Func<SimulationEnvironment, int, Task<Target>> create)
    {
        var keys = Gen.Int[0, JournalStoreSpec.Keys.Length - 1];
        var kinds = Gen.Frequency((5, Gen.Const(JournalWriteKind.Append)), (2, Gen.Const(JournalWriteKind.Replace)), (2, Gen.Const(JournalWriteKind.Delete)));
        var backs = Gen.Frequency((6, Gen.Const(0)), (2, Gen.Const(1)), (1, Gen.Const(2)));
        Gen<JournalWrite> Writes(JournalDisruption disruption) =>
            Gen.Select(keys, kinds, backs, Gen.Byte[1, 255], (key, kind, back, value) => new JournalWrite(key, kind, back, value, disruption));

        return new ConformanceKit<JournalStoreState, Target>(
                JournalStoreSpec.Create(Limits),
                static () => new JournalStoreState(),
                seed => create(conformance.Environment, seed),
                static (_, _) => true,
                CancellationToken.None,
                conflicts: JournalStoreSpec.Conflicts,
                scheduler: conformance)
            .Operation(JournalStoreSpec.Write, Writes(JournalDisruption.None), static (target, request) => target.WriteAsync(request), weight: 12)
            .Operation(JournalStoreSpec.Read, keys.Select(static key => new JournalRead(key)), static (target, request) => target.ReadAsync(request), weight: 6)
            .Operation(JournalStoreSpec.FencedWrite, Writes(JournalDisruption.Takeover), static (target, request) => target.WriteAsync(request))
            .Operation(JournalStoreSpec.CrashingWrite, Writes(JournalDisruption.Crash), static (target, request) => target.WriteAsync(request))
            .Operation(JournalStoreSpec.Takeover, Gen.Const(JournalTakeover.Instance), static (target, _) => target.TakeOverAsync(), weight: 3)
            .Operation(JournalStoreSpec.ExpiryScan, Gen.Const(JournalScan.Instance), static (target, _) => target.ScanAsync());
    }

    /// <summary>
    /// Runs <see cref="JournalStoreSpec"/>'s operations against a <see cref="JournalStore"/> over Clockwork-simulated
    /// ledgers, register, and archive store, releasing every rented buffer before returning.
    /// </summary>
    private sealed class Target : IAsyncDisposable
    {
        private const int Window = 4;

        private readonly SimulationEnvironment _environment;
        private readonly SimulatedConditionalRegister _register;
        private readonly InMemoryLedgerReplicaTransport _replicas;
        private readonly CrashTransport _transport;
        private readonly SimulatedArchiveStore _archive;
        private readonly ImmutableArray<NodeId> _replicaNodes;
        private readonly JournalStoreOptions _options;
        private readonly LedgerAdmission.RejectionMutant _rejectionMutation;
        private readonly int _seed;
        private readonly Lock _lock = new();
        private readonly List<JournalVersion>[] _learned = [.. JournalStoreSpec.Keys.Select(static _ => new List<JournalVersion> { JournalVersion.Origin })];

        private LedgerMapView _map;
        private LedgerReader _reader;
        private LedgerMapCommitter _committer;
        private LedgerWriterRegistry _registry;
        private TransitionExecutor _executor;
        private JournalStore _journals;
        private int _generation;

        private Target(
            SimulationEnvironment environment,
            SimulatedConditionalRegister register,
            InMemoryLedgerReplicaTransport replicas,
            CrashTransport transport,
            SimulatedArchiveStore archive,
            ImmutableArray<NodeId> replicaNodes,
            JournalStoreOptions options,
            LedgerAdmission.RejectionMutant rejectionMutation,
            int seed,
            LedgerMapView map,
            LedgerReader reader,
            LedgerMapCommitter committer,
            LedgerWriterRegistry registry,
            TransitionExecutor executor,
            JournalStore journals)
        {
            _environment = environment;
            _register = register;
            _replicas = replicas;
            _transport = transport;
            _archive = archive;
            _replicaNodes = replicaNodes;
            _options = options;
            _rejectionMutation = rejectionMutation;
            _seed = seed;
            _map = map;
            _reader = reader;
            _committer = committer;
            _registry = registry;
            _executor = executor;
            _journals = journals;
        }

        public static async Task<Target> StartAsync(
            SimulationEnvironment environment,
            int seed,
            int replicas,
            TimeSpan maxLatency,
            double staleRate,
            long? warmStateBytes,
            LedgerAdmission.RejectionMutant rejectionMutation)
        {
            var replicaNodes = replicas == 1 ? [PrimaryNode] : ThreeReplicas;
            var register = new SimulatedConditionalRegister(environment, "map") { MaxLatency = maxLatency };
            var memoryReplicas = new InMemoryLedgerReplicaTransport { YieldsAtLandingAndAnswer = true };
            var transport = new CrashTransport(environment, memoryReplicas);
            var archive = new SimulatedArchiveStore(environment, "archive", maxAppends: MaxAppends, replaceRequestAppends: ReplaceRequestAppends)
            {
                MaxLatency = maxLatency,
                StaleRate = staleRate,
            };

            var options = new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero };
            if (warmStateBytes is { } budget)
            {
                options.WarmStateBytes = budget;
            }

            var (map, reader, committer, registry, executor, journals) = await OpenProcessAsync(
                register,
                transport,
                archive,
                options,
                rejectionMutation,
                seed,
                generation: 0);

            var target = new Target(
                environment,
                register,
                memoryReplicas,
                transport,
                archive,
                replicaNodes,
                options,
                rejectionMutation,
                seed,
                map,
                reader,
                committer,
                registry,
                executor,
                journals);

            await target.ExecuteAsync(new Transition.Bootstrap(target.Choice()));
            return target;
        }

        public async Task<JournalAnswer> WriteAsync(JournalWrite request)
        {
            var expected = Resolve(request.Key, request.Back);
            var payload = request.Payload;
            var input = request.Kind switch
            {
                JournalWriteKind.Append => JournalInput.Append(JournalStoreSpec.Keys[request.Key], expected.ToString(), payload),
                JournalWriteKind.Replace => JournalInput.Replace(JournalStoreSpec.Keys[request.Key], expected.ToString(), payload),
                JournalWriteKind.Delete => JournalInput.Delete(JournalStoreSpec.Keys[request.Key], expected.ToString()),
                _ => throw new UnreachableException(),
            };
            var outcome = request.Disruption switch
            {
                JournalDisruption.None => await SendAsync(request.Key, request.Kind, expected, payload),
                JournalDisruption.Takeover => await FencedAsync(request.Key, request.Kind, expected, payload),
                JournalDisruption.Crash => await CrashingAsync(request.Key, request.Kind, expected, payload),
                _ => throw new UnreachableException(),
            };
            return Answer(request.Key, input, outcome);
        }

        public async Task<JournalAnswer> ReadAsync(JournalRead request)
        {
            var key = JournalStoreSpec.Keys[request.Key];
            var input = JournalInput.Read(key);
            var outcome = await _journals.ReadAsync(JournalRowKey.Encode(key), CancellationToken.None);
            var answer = outcome switch
            {
                JournalReadResult result => await AnswerAsync(input, result),
                JournalMoved => new JournalAnswer(input, JournalOutput.Moved),
                null => throw new UnreachableException(),
            };
            if (answer.Output.Outcome == JournalOutcome.Accepted)
            {
                Learn(request.Key, JournalVersion.Parse(answer.Output.Version!));
            }

            return answer;
        }

        public async Task<JournalTakenOver> TakeOverAsync()
        {
            var encoded = JournalRowKey.Encode(JournalStoreSpec.Keys[0]).Span.ToArray();
            var replaced = (_map.Current.Find(encoded)?.Open ?? throw new InvalidOperationException("The map view lists no open ledger.")).Id;
            await ExecuteAsync(new Transition.Replace(replaced, Choice()));
            var view = await _map.ReadAsync(CancellationToken.None);
            await _registry.ReconciledAsync(view.Epoch, CancellationToken.None);
            for (var attempt = 0; _journals.ServedLedgers.Contains(replaced); attempt++)
            {
                Assert.True(attempt < 10_000, $"The store kept the admission of the replaced ledger {replaced}.");
                await SimulationTasks.Delay(TimeSpan.FromMilliseconds(1), SimulationEnvironment.Clock, CancellationToken.None);
            }

            return JournalTakenOver.Instance;
        }

        public async Task<JournalScanAnswer> ScanAsync()
        {
            var proposals = new List<JournalAnswer>();
            _journals.OnExpiryProposed = (rowKey, expected) => outcome =>
            {
                var input = JournalInput.Expire(rowKey.ToString(), expected.ToString());
                var requested = false;
                var output = outcome is { } answered ? Output(answered, out requested) : JournalOutput.Ambiguous;
                lock (proposals)
                {
                    proposals.Add(new JournalAnswer(input, output, requested));
                }
            };
            try
            {
                _ = await _journals.ExpireTombstonesAsync(CancellationToken.None);
            }
            finally
            {
                _journals.OnExpiryProposed = null;
            }

            lock (proposals)
            {
                return new JournalScanAnswer([.. proposals]);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _archive.AfterNextRead = null;
            _archive.AfterNextReadOf = default;
            _transport.BeforeWriteAnswer = null;
            await _journals.DisposeAsync();
            await _registry.DisposeAsync();
            await _reader.DisposeAsync();
            await _committer.DisposeAsync();
            _replicas.Dispose();
            _archive.Dispose();
        }

        private static async Task<(
            LedgerMapView Map,
            LedgerReader Reader,
            LedgerMapCommitter Committer,
            LedgerWriterRegistry Registry,
            TransitionExecutor Executor,
            JournalStore Journals)> OpenProcessAsync(
            IConditionalRegister register,
            ILedgerReplicaTransport transport,
            IArchiveStore archive,
            JournalStoreOptions options,
            LedgerAdmission.RejectionMutant rejectionMutation,
            int seed,
            int generation)
        {
            var clock = SimulationEnvironment.Clock;
            var map = await LedgerMapView.OpenAsync(register, CancellationToken.None);
            var reader = new LedgerReader(map, transport, clock, PrimaryNode);
            var committer = new LedgerMapCommitter(register, int.MaxValue);
            var registry = new LedgerWriterRegistry(
                reader,
                1 << 25,
                LedgerWriterOptions.Default,
                clock,
                NullLoggerFactory.Instance,
                new Random(HashCode.Combine(seed, generation)));
            var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);
            var journals = new JournalStore(registry, archive, options, clock)
            {
                RejectionMutation = rejectionMutation,
            };
            return (map, reader, committer, registry, executor, journals);
        }

        private static async Task<JournalAnswer> AnswerAsync(JournalInput input, JournalReadResult result)
        {
            using (result)
            {
                var content = new byte[result.ContentLength];
                var length = 0;
                await foreach (var chunk in result.ReadPayloadsAsync(CancellationToken.None))
                {
                    using (chunk)
                    {
                        chunk.CopyTo(content.AsSpan(length));
                        length += chunk.Length;
                    }
                }

                Assert.Equal(content.Length, length);
                return new JournalAnswer(input, JournalOutput.Read(result.Exists, result.CurrentVersion.ToString(), content), result.ReplaceRequested);
            }
        }

        private static JournalOutput Output(JournalWriteOutcome outcome, out bool replaceRequested)
        {
            replaceRequested = false;
            return outcome.Value switch
            {
                JournalWriteOutcome.Accepted accepted => Accepted(accepted, out replaceRequested),
                JournalWriteOutcome.Rejected rejected => JournalOutput.Rejected(rejected.CurrentVersion.ToString()),
                JournalWriteOutcome.ReplaceRequired => JournalOutput.ReplaceRequired,
                JournalMoved => JournalOutput.Moved,
                JournalWriteOutcome.Fenced => JournalOutput.Fenced,
                JournalWriteOutcome.Ambiguous => JournalOutput.Ambiguous,
                null => throw new UnreachableException(),
                _ => throw new UnreachableException(),
            };

            static JournalOutput Accepted(JournalWriteOutcome.Accepted accepted, out bool replaceRequested)
            {
                replaceRequested = accepted.ReplaceRequested;
                return JournalOutput.Accepted(accepted.Version.ToString());
            }
        }

        private JournalAnswer Answer(int key, JournalInput input, JournalWriteOutcome outcome)
        {
            var output = Output(outcome, out var replaceRequested);
            if (outcome.Value is JournalWriteOutcome.Accepted accepted)
            {
                Learn(key, accepted.Version);
            }
            else if (outcome.Value is JournalWriteOutcome.Rejected rejected)
            {
                Learn(key, rejected.CurrentVersion);
            }

            return new JournalAnswer(input, output, replaceRequested);
        }

        private JournalVersion Resolve(int key, int back)
        {
            lock (_lock)
            {
                var learned = _learned[key];
                return learned[Math.Max(0, learned.Count - 1 - back)];
            }
        }

        private void Learn(int key, JournalVersion version)
        {
            lock (_lock)
            {
                if (_learned[key][^1] != version)
                {
                    _learned[key].Add(version);
                }
            }
        }

        private async Task<JournalWriteOutcome> SendAsync(int key, JournalWriteKind kind, JournalVersion expected, byte[] payload)
        {
            var rowKey = JournalRowKey.Encode(JournalStoreSpec.Keys[key]);
            using var buffer = payload.Length == 0 ? default : ArcBuffer.Wrap(payload);
            using var record = kind switch
            {
                JournalWriteKind.Append => _journals.EncodeAppend(rowKey, expected, buffer),
                JournalWriteKind.Replace => _journals.EncodeReplace(rowKey, expected, buffer),
                JournalWriteKind.Delete => _journals.EncodeDelete(rowKey, expected),
                _ => throw new UnreachableException(),
            };
            return await _journals.WriteAsync(record, CancellationToken.None);
        }

        private async Task<JournalWriteOutcome> FencedAsync(int key, JournalWriteKind kind, JournalVersion expected, byte[] payload)
        {
            var tookOver = false;
            _archive.AfterNextReadOf = JournalRowKey.Encode(JournalStoreSpec.Keys[key]).Memory;
            _archive.AfterNextRead = () =>
            {
                // Called from rewritten SimulatedArchiveStore; schedule the unrewritten async takeover on the lane so
                // its continuations stay on the simulation's synchronization context.
                tookOver = true;
                var done = new TaskCompletionSource();
                _environment.Cluster.SchedulerLane.Enqueue(() => _ = CompleteTakeOverAsync(done));
                return done.Task;
            };

            try
            {
                var outcome = await SendAsync(key, kind, expected, payload);
                if (!tookOver)
                {
                    _archive.AfterNextRead = null;
                    _archive.AfterNextReadOf = default;
                    _ = await TakeOverAsync();
                }

                return outcome;
            }
            finally
            {
                _archive.AfterNextRead = null;
                _archive.AfterNextReadOf = default;
            }

            async Task CompleteTakeOverAsync(TaskCompletionSource done)
            {
                try
                {
                    _ = await TakeOverAsync();
                    done.SetResult();
                }
                catch (Exception exception)
                {
                    done.SetException(exception);
                }
            }
        }

        private async Task<JournalWriteOutcome> CrashingAsync(int key, JournalWriteKind kind, JournalVersion expected, byte[] payload)
        {
            var stored = new TaskCompletionSource();
            var acknowledge = new TaskCompletionSource();
            _transport.BeforeWriteAnswer = () =>
            {
                _ = stored.TrySetResult();
                return acknowledge.Task;
            };

            JournalWriteOutcome outcome;
            try
            {
                var write = SendAsync(key, kind, expected, payload);
                _ = await SimulationTasks.WhenAny(stored.Task, write);
                await _registry.DisposeAsync();
                outcome = await write;
            }
            finally
            {
                _transport.BeforeWriteAnswer = null;
                _ = acknowledge.TrySetResult();
            }

            await _journals.DisposeAsync();
            await _reader.DisposeAsync();
            await _committer.DisposeAsync();
            (_map, _reader, _committer, _registry, _executor, _journals) = await OpenProcessAsync(
                _register,
                _transport,
                _archive,
                _options,
                _rejectionMutation,
                _seed,
                ++_generation);
            return outcome;
        }

        private TransitionChoice Choice() => new(PrimaryNode, _replicaNodes, Window, 0, _map.Current.Epoch);

        private async Task ExecuteAsync(Transition transition)
        {
            var outcome = await _executor.ExecuteAsync(
                transition,
                (change, token) => _committer.CommitAsync(change, token),
                CancellationToken.None);
            if (outcome.Value is not LedgerMapCommitOutcome.Applied)
            {
                throw new InvalidOperationException($"The transition {transition.Value} answered {outcome.Value}.");
            }
        }
    }

    /// <summary>
    /// Wraps a replica transport so a crashing write can hold replica acknowledgements after storage without leaving the
    /// simulation's synchronization context.
    /// </summary>
    private sealed class CrashTransport(SimulationEnvironment environment, ILedgerReplicaTransport inner) : ILedgerReplicaTransport
    {
        public Func<Task>? BeforeWriteAnswer { get; set; }

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            new Replica(this, inner.Open(node, ledger, options));

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(node, ledgers, cancellationToken);

        private sealed class Replica(CrashTransport transport, ILedgerReplica inner) : ILedgerReplica
        {
            public ValueTask<ReplicaWriteOutcome> WriteAsync(
                Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
                transport.BeforeWriteAnswer is { } hold
                    ? HeldAsync(inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken), hold)
                    : inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, contiguousTail, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

            public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
                inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();

            private ValueTask<ReplicaWriteOutcome> HeldAsync(ValueTask<ReplicaWriteOutcome> writing, Func<Task> hold)
            {
                // Called from rewritten Ledger code; schedule the unrewritten async hold on the lane so its
                // continuations stay on the simulation's synchronization context.
                var done = new TaskCompletionSource<ReplicaWriteOutcome>();
                transport.Environment.Cluster.SchedulerLane.Enqueue(() => _ = RunHeldAsync(writing, hold, done));
                return new ValueTask<ReplicaWriteOutcome>(done.Task);
            }

            private static async Task RunHeldAsync(
                ValueTask<ReplicaWriteOutcome> writing,
                Func<Task> hold,
                TaskCompletionSource<ReplicaWriteOutcome> done)
            {
                try
                {
                    var outcome = await writing;
                    await hold();
                    done.SetResult(outcome);
                }
                catch (Exception exception)
                {
                    done.SetException(exception);
                }
            }
        }

        private SimulationEnvironment Environment => environment;
    }
}

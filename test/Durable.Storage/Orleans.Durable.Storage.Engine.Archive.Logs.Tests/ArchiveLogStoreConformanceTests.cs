using System.Collections.Concurrent;
using Azure;
using CsCheck;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// The Accordant <see cref="IArchiveLogStore"/> spec (archive-logs.md §3, §10; LOG-I1, LOG-I2) over the memory store, over
/// <see cref="AzureArchiveLogStore"/> on the fake Azure transport beneath the real SDK, and over the chunk layout's
/// <see cref="ChunkArchiveLogStore"/> on the memory and file-system object stores: Accordant's exhaustive
/// sequences over a small input set, and over a straggler set to depth 5, a key's chain re-issued as a racing drain or a
/// straggler does; CsCheck's random sequences, with earlier writes replayed, and concurrent groups; and both with faults
/// before and after a request lands, requests which land after their own retry or after their call gave up, server errors
/// after landing, reads whose body breaks, retries which meet their own landing, and calls with no retries at all; and
/// fixed orderings of a write between a concurrent run's units, and of an abandoned write's first request landing alone.
/// Without faults every write which lands whole answers <c>Applied</c>, and a sequence's runs land whole or not at all; a
/// read answers <c>Stale</c> only when a write overlapped it or its base is gone; the fake checks what archive logs keep
/// of their blobs after every write; and each fault kind is checked to have fired.
/// </summary>
public sealed class ArchiveLogStoreConformanceTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly ConcurrentDictionary<string, int> _fired = new(StringComparer.Ordinal);

    public enum Store
    {
        Memory,
        Azure,
        Chunks,
        ChunksOnFiles,
    }

    public static TheoryData<Store> Stores => [Store.Memory, Store.Azure, Store.Chunks, Store.ChunksOnFiles];

    public void Dispose() => _leaks.Dispose();

    private static bool IsChunks(Store store) => store is Store.Chunks or Store.ChunksOnFiles;

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task EveryExhaustiveSequenceConforms(Store store)
    {
        var spec = ArchiveLogStoreSpec.Create(unknownOutcomes: false, retriesMeetLandings: false, interleavedWrites: false, chunkLayout: IsChunks(store));
        var kit = Kit(store, spec, faultRate: 0);

        var sequences = spec.GenerateTests(new ArchiveLogStoreState(), ArchiveLogStoreKit.InputSet(spec), kit.GenerationOptions(3));

        Assert.NotEmpty(sequences);
        await kit.RunAsync(sequences);
    }

    /// <summary>
    /// A key's first run, its opener, and the run after it, each re-issued as a racing drain or a straggler re-issues it,
    /// with properties reads between, to depth 5: every write after its successor landed fails its condition and changes
    /// nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task EveryExhaustiveStragglerSequenceConforms(Store store)
    {
        var spec = ArchiveLogStoreSpec.Create(unknownOutcomes: false, retriesMeetLandings: false, interleavedWrites: false, chunkLayout: IsChunks(store));
        var kit = Kit(store, spec, faultRate: 0);

        var sequences = spec.GenerateTests(new ArchiveLogStoreState(), ArchiveLogStoreKit.InputSet(spec, ArchiveLogStoreKit.StragglerInputs), kit.GenerationOptions(5));

        Assert.NotEmpty(sequences);
        await kit.RunAsync(sequences);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public Task RandomSequencesConform(Store store)
    {
        var kit = Kit(store, ArchiveLogStoreSpec.Create(unknownOutcomes: false, retriesMeetLandings: false, interleavedWrites: false, chunkLayout: IsChunks(store)), faultRate: 0);
        return kit.SampleAsync(kit.Sequences(30).WithReplays(), iterations: 60);
    }

    /// <summary>Concurrent writers and readers; a read which a write overlapped may answer <c>Stale</c>.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public Task ConcurrentGroupsConform(Store store)
    {
        var kit = Kit(store, ArchiveLogStoreSpec.Create(unknownOutcomes: false, staleReads: true, retriesMeetLandings: false, interleavedWrites: true, chunkLayout: IsChunks(store)), faultRate: 0);
        return kit.SampleAsync(kit.ConcurrentSequences(12).WithReplays(), iterations: 40);
    }

    /// <summary>
    /// Requests fail before or after they land; the memory store also answers a landed write's condition failed; over the
    /// fake transport, requests land after their own retry or after their call gave up, a server fails after a write
    /// landed, and a read's body breaks mid-way, which the SDK, with three retries or none, meets. A call which throws
    /// answers nothing, and the spec judges every later answer against what its pending writes may have done.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task SequencesWithFaultsConform(Store store)
    {
        // Only the fake transport's reads resume a broken body, which a write may change under them; a chunk-layout read
        // lists the log again after it reads, so a held write which lands meanwhile makes it Stale.
        var staleReads = store is Store.Azure || IsChunks(store);
        var spec = ArchiveLogStoreSpec.Create(unknownOutcomes: true, staleReads: staleReads, chunkLayout: IsChunks(store));
        var kit = Kit(store, spec, faultRate: 0.2);
        await kit.SampleAsync(kit.Sequences(25).WithReplays(), iterations: 60);
        await kit.SampleAsync(kit.ConcurrentSequences(10).WithReplays(), iterations: 30);
        Assert.True(_fired.Values.Sum() > 0, "No random fault fired.");

        // Guarantee every fault kind reaches a real injection point and conforms regardless of which writes the random
        // sequences happen to land (#433: ChunkArchiveLogStore skips writes whose precondition fails on listing).
        await ExerciseEveryFaultAsync(store, spec);

        string[] kinds = store switch
        {
            Store.Memory => [.. Target.MemoryFaults.Select(static fault => fault.ToString())],
            Store.Azure => [.. Target.AzureFaults.Select(static fault => fault.ToString())],
            Store.Chunks or Store.ChunksOnFiles => [.. FaultInjectingObjectStore.WriteFaults.Select(static fault => fault.ToString())],
            _ => throw new ArgumentOutOfRangeException(nameof(store)),
        };
        Assert.All(kinds, kind => Assert.True(_fired.GetValueOrDefault(kind) > 0, $"The fault {kind} never fired."));
    }

    private async Task ExerciseEveryFaultAsync(Store store, Microsoft.Accordant.Spec<ArchiveLogStoreState> spec)
    {
        var exercise = new ConformanceCase(
        [
            new ConformanceRound([new(ArchiveLogStoreSpec.OpenOperation, new LogOpen(0, 0, 0, SpecOpener.Replace, 2))]),
            new ConformanceRound([new(ArchiveLogStoreSpec.AppendOperation, new LogAppend(1, 0, 0, [1, 1]))]),
            new ConformanceRound([new(ArchiveLogStoreSpec.ReadOperation, LogRead.Instance)]),
            new ConformanceRound([new(ArchiveLogStoreSpec.PropertiesOperation, LogProperties.Instance)]),
            new ConformanceRound([new(ArchiveLogStoreSpec.ListOperation, LogList.Instance)]),
        ]);

        switch (store)
        {
            case Store.Memory:
                foreach (var fault in Target.MemoryFaults)
                {
                    await ExerciseAsync(fault.ToString(), _ =>
                    {
                        var injected = 0;
                        return Target.Memory(
                            request => request.Kind == ArchiveLogRequestKind.OpenGeneration && Interlocked.Exchange(ref injected, 1) == 0 ? fault : ArchiveLogFault.None,
                            _fired);
                    });
                }

                break;

            case Store.Azure:
                foreach (var fault in Target.AzureFaults)
                {
                    var operation = fault == FakeAzureFault.BreakMidBody ? FakeAzureOperation.GetBlob : FakeAzureOperation.PutBlockBlob;
                    await ExerciseAsync(fault.ToString(), _ =>
                    {
                        var injected = 0;
                        return Target.Azure(
                            request => request.Operation == operation && Interlocked.Exchange(ref injected, 1) == 0 ? fault : FakeAzureFault.None,
                            retries: 0,
                            _fired);
                    });
                }

                break;

            case Store.Chunks or Store.ChunksOnFiles:
                foreach (var fault in FaultInjectingObjectStore.WriteFaults)
                {
                    await ExerciseAsync(fault.ToString(), seed => Target.Chunks(store, seed, fault, _fired));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(store));
        }

        async Task ExerciseAsync(string kind, Func<int, Target> create)
        {
            var before = _fired.GetValueOrDefault(kind);
            await Kit(spec, create).RunAsync(exercise);
            Assert.True(_fired.GetValueOrDefault(kind) > before, $"The fault {kind} never fired.");
        }
    }

    /// <summary>
    /// A write lands between two units of a concurrent run from <c>Origin</c>, as the run's second unit waits until it
    /// answers: the run answers <c>ConditionFailed</c> having landed its create and first unit, and the write between them
    /// <c>Applied</c>, which the spec allows without faults, since each unit lands apart (archive-logs.md §3; #271).
    /// </summary>
    [Fact]
    public async Task AWriteBetweenARunsUnitsConforms()
    {
        var run = new LogAppend(0, 0, 0, [1, 1]);
        var between = new LogAppend(1, 1, 1, [1]);
        var firstUnitLanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var betweenAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runRequests = 0;
        Task Schedule(CancellationToken cancellationToken)
        {
            var request = Target.CurrentRequest.Value;
            if (ReferenceEquals(request, between))
            {
                return firstUnitLanded.Task.WaitAsync(cancellationToken);
            }

            // The run's requests are its create, its first unit, and its second unit, which waits for the write between.
            if (ReferenceEquals(request, run) && Interlocked.Increment(ref runRequests) == 3)
            {
                firstUnitLanded.SetResult();
                return betweenAnswered.Task.WaitAsync(cancellationToken);
            }

            return Task.CompletedTask;
        }

        var outcomes = new ConcurrentDictionary<object, LogOutcome>();
        var kit = Kit(
            ArchiveLogStoreSpec.Create(unknownOutcomes: false, retriesMeetLandings: false, interleavedWrites: true),
            _ => Target.Azure(faults: null, retries: 0, _fired, Schedule),
            async (target, request) =>
            {
                var outcome = await target.RunAsync(request);
                outcomes[request] = outcome;
                if (ReferenceEquals(request, between))
                {
                    betweenAnswered.SetResult();
                }

                return outcome;
            });

        await kit.RunAsync(new ConformanceCase(
        [
            new ConformanceRound([new(ArchiveLogStoreSpec.AppendOperation, run), new(ArchiveLogStoreSpec.AppendOperation, between)]),
            new ConformanceRound([new(ArchiveLogStoreSpec.ReadOperation, LogRead.Instance)]),
        ]));

        Assert.Equal(LogOutcomeKind.ConditionFailed, outcomes[run].Kind);
        Assert.Equal(LogOutcomeKind.Applied, outcomes[between].Kind);
    }

    /// <summary>
    /// A write whose call gave up lands each request apart, later: a first run's create alone, after a properties read
    /// found the log absent, and an opener's base alone, after a listing found none (9vDI6cJ0jOZo, #275).
    /// </summary>
    [Fact]
    public async Task AnUnknownWritesFirstRequestLandsAloneLater()
    {
        foreach (var (write, first, observe) in new (object, FakeAzureOperation, object)[]
        {
            (new LogAppend(0, 0, 0, [1, 1, 1]), FakeAzureOperation.CreateAppendBlob, LogProperties.Instance),
            (new LogOpen(0, 0, 0, SpecOpener.Replace, 2), FakeAzureOperation.PutBlockBlob, LogList.Instance),
        })
        {
            var deferred = 0;
            var kit = Kit(
                ArchiveLogStoreSpec.Create(unknownOutcomes: true),
                _ => Target.Azure(
                    request => request.Operation == first && Interlocked.Exchange(ref deferred, 1) == 0 ? FakeAzureFault.DeliverLater : FakeAzureFault.None,
                    retries: 0,
                    _fired));

            // The write gives no answer; the next request lands its first one, which the call after observes alone.
            await kit.RunAsync(new ConformanceCase(
            [
                new ConformanceRound([new(ArchiveLogStoreSpec.OperationOf(write), write)]),
                new ConformanceRound([new(ArchiveLogStoreSpec.OperationOf(observe), observe)]),
                new ConformanceRound([new(ArchiveLogStoreSpec.ListOperation, LogList.Instance)]),
                new ConformanceRound([new(ArchiveLogStoreSpec.ReadOperation, LogRead.Instance)]),
            ]));
        }
    }

    /// <summary>
    /// An opener from generation 1 which meets no log, as after its write-ahead blob was deleted, answers
    /// <c>ConditionFailed</c> and creates no log, so its one properties read decides it <c>PredecessorMissing</c>
    /// (archive-logs.md §8.3, §9.1): <c>x-ms-if-tags</c> alone lets a <c>Put Blob</c> create an absent blob, so the
    /// Azure store's re-creation also carries <c>If-Match: *</c>. The <c>drops-existence-condition</c> mutant creates the
    /// log at generation 2, which this case reports.
    /// </summary>
    [Theory]
    [InlineData(Store.Memory, false)]
    [InlineData(Store.Azure, false)]
    [InlineData(Store.Azure, true)]
    public async Task AnOpenerWhichMeetsNoLogFailsItsConditionAndCreatesNone(Store kind, bool mutant)
    {
        using var service = new FakeAzureBlobService(checkInvariants: true);
        var layout = new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 4, 100, 50, AzureArchiveLogMetadata.NameRule);
        IArchiveLogStore store = kind == Store.Memory
            ? new MemoryArchiveLogStore()
            : new AzureArchiveLogStore(service.Client(), "json", layout) { DropsExistenceConditionMutant = mutant };
        var name = new ArchiveLogName("journals/absent", ArchiveLogNames.KeyHash("absent"u8));
        var from = new ArchiveLogPosition(1, 0, 0, 0);

        var outcome = (await store.OpenAsync(name, from, new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, 1), new([1, 2, 3]), TestContext.Current.CancellationToken)).Value;
        var read = await store.GetPropertiesAsync(name, TestContext.Current.CancellationToken);

        var detected = outcome is not ArchiveLogWriteOutcome.ConditionFailed || read is not null;
        Assert.Equal(mutant, detected);
        if (mutant)
        {
            Assert.Equal(2ul, read!.Position.Generation);
            return;
        }

        Assert.Equal(ArchiveLogOpenerConditionOutcome.PredecessorMissing.Instance, ArchiveLogConditions.ClassifyOpener(from, read).Value);
        Assert.DoesNotContain(service.Records, static record => record.Operation == FakeAzureOperation.CreateAppendBlob && record.Status == 201);
    }

    /// <summary>
    /// archive-logs.md §3, §8.5: the bases' listing meets its page contract over each store, with pages of two bases: the
    /// pages concatenate to the whole listing in order, each continuation resumes at exactly the next page, only the last
    /// is null, an empty store lists one empty page, and a continuation outlives the deletion of the bases about it and the
    /// creation of others.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheBasesListingMeetsItsPageContract(Store store)
    {
        using var service = new FakeAzureBlobService();
        using var files = store == Store.ChunksOnFiles ? new ObjectDirectory() : null;
        IArchiveLogStore logs = store switch
        {
            Store.Memory => new MemoryArchiveLogStore() { ListPageSize = 2 },
            Store.Azure => new AzureArchiveLogStore(service.Client(), "json") { ListPageSize = 2 },
            Store.Chunks => new ChunkArchiveLogStore(new InMemoryObjectStore()) { ListPageSize = 2 },
            Store.ChunksOnFiles => new ChunkArchiveLogStore(files!.Store) { ListPageSize = 2 },
            _ => throw new ArgumentOutOfRangeException(nameof(store)),
        };
        await ArchiveLogPageContract.CheckAsync(logs, TestContext.Current.CancellationToken);
    }

    private ConformanceKit<ArchiveLogStoreState, Target> Kit(Store store, Microsoft.Accordant.Spec<ArchiveLogStoreState> spec, double faultRate) =>
        Kit(spec, seed => Target.Create(store, new Random(seed), faultRate, _fired));

    private ConformanceKit<ArchiveLogStoreState, Target> Kit(
        Microsoft.Accordant.Spec<ArchiveLogStoreState> spec,
        Func<int, Target> create,
        Func<Target, object, Task<LogOutcome>>? run = null) =>
        new ConformanceKit<ArchiveLogStoreState, Target>(
                spec,
                static () => new ArchiveLogStoreState(),
                seed => Task.FromResult(create(seed)),
                ArchiveLogStoreSpec.IsCallerAllowed,
                TestContext.Current.CancellationToken,
                scope: _leaks.InScopeAsync,
                conflicts: ArchiveLogStoreSpec.Conflicts)
            .ArchiveLogStoreOperations(run ?? (static (target, request) => target.RunAsync(request)));

    /// <summary>
    /// One case's store and the executor of its calls: a call which fails at its transport answers nothing, and a read
    /// which a write overlapped and which answers <c>Stale</c> reports it as racing. On disposal, the fake's checks of its
    /// blobs must have found no violation.
    /// </summary>
    internal sealed class Target : IAsyncDisposable
    {
        /// <summary>The faults the memory store injects.</summary>
        internal static readonly ArchiveLogFault[] MemoryFaults =
            [ArchiveLogFault.FailBefore, ArchiveLogFault.FailAfter, ArchiveLogFault.RetryMeetsLanding];

        /// <summary>The faults the fake transport injects at random; damage in transit has its own tests.</summary>
        internal static readonly FakeAzureFault[] AzureFaults =
            [FakeAzureFault.FailBefore, FakeAzureFault.FailAfter, FakeAzureFault.DeliverLater, FakeAzureFault.ServerErrorAfterLanding, FakeAzureFault.BreakMidBody];

        private readonly FakeAzureBlobService? _service;
        private readonly FaultInjectingObjectStore? _objectFaults;
        private readonly ObjectDirectory? _files;
        private readonly ArchiveLogStoreExecutor _executor;
        private readonly ConcurrentDictionary<string, int> _fired;
        private int _writing;
        private int _writes;

        private Target(ArchiveLogStoreExecutor executor, ConcurrentDictionary<string, int> fired) =>
            (_executor, _fired) = (executor, fired);

        private Target(IObjectStore objects, ObjectDirectory? files, int? faultSeed, double faultRate, ConcurrentDictionary<string, int> fired, FaultInjectingObjectStore.Fault? forcedCreateFault = null)
        {
            _fired = fired;
            _files = files;
            _objectFaults = faultSeed is { } seed && (faultRate > 0 || forcedCreateFault is not null)
                ? new FaultInjectingObjectStore(objects, seed, faultRate)
                : null;
            if (forcedCreateFault is { } fault)
            {
                _objectFaults!.FaultNextCreate(fault);
            }

            _executor = new(new ChunkArchiveLogStore(_objectFaults ?? objects));
        }

        private Target(Func<FakeAzureRequest, FakeAzureFault>? faults, int retries, ConcurrentDictionary<string, int> fired, Func<CancellationToken, Task>? schedule)
        {
            _fired = fired;
            _service = new FakeAzureBlobService(faults, schedule, checkInvariants: true);
            var layout = new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 4, 100, 50, AzureArchiveLogMetadata.NameRule);
            _executor = new(new AzureArchiveLogStore(_service.Client(retries), "json", layout));
        }

        /// <summary>Gets the request whose call runs in the current flow, which a schedule reads to order calls' requests.</summary>
        internal static AsyncLocal<object?> CurrentRequest { get; } = new();

        public static Target Create(Store store, Random random, double faultRate, ConcurrentDictionary<string, int> fired)
        {
            if (store is Store.Chunks or Store.ChunksOnFiles)
            {
                int seed;
                lock (random)
                {
                    seed = random.Next();
                }

                var files = store == Store.ChunksOnFiles ? new ObjectDirectory() : null;
                return new((IObjectStore?)files?.Store ?? new InMemoryObjectStore(), files, seed, faultRate, fired);
            }

            if (store == Store.Memory)
            {
                return Memory(
                    _ =>
                    {
                        lock (random)
                        {
                            return random.NextDouble() >= faultRate ? ArchiveLogFault.None : MemoryFaults[random.Next(MemoryFaults.Length)];
                        }
                    },
                    fired);
            }

            int retries;
            lock (random)
            {
                // Some cases' clients retry nothing, so every failure reaches the store as it is.
                retries = faultRate > 0 && random.Next(4) == 0 ? 0 : 3;
            }

            return Azure(
                _ =>
                {
                    lock (random)
                    {
                        return random.NextDouble() >= faultRate ? FakeAzureFault.None : AzureFaults[random.Next(AzureFaults.Length)];
                    }
                },
                retries,
                fired);
        }

        /// <summary>Returns a target over <see cref="MemoryArchiveLogStore"/> with its own fault selector.</summary>
        public static Target Memory(Func<ArchiveLogRequest, ArchiveLogFault> faults, ConcurrentDictionary<string, int> fired) =>
            new(
                new(new MemoryArchiveLogStore(faults: request =>
                {
                    var fault = faults(request);
                    if (fault != ArchiveLogFault.None)
                    {
                        _ = fired.AddOrUpdate(fault.ToString(), 1, static (_, count) => count + 1);
                    }

                    return fault;
                })),
                fired);

        /// <summary>Returns a target over <see cref="ChunkArchiveLogStore"/> whose next object create fails with <paramref name="fault"/>.</summary>
        public static Target Chunks(Store store, int seed, FaultInjectingObjectStore.Fault fault, ConcurrentDictionary<string, int> fired)
        {
            var files = store == Store.ChunksOnFiles ? new ObjectDirectory() : null;
            return new((IObjectStore?)files?.Store ?? new InMemoryObjectStore(), files, seed, faultRate: 0, fired, fault);
        }

        /// <summary>Returns a target over the fake transport, whose client retries <paramref name="retries"/> times, with its own faults and schedule.</summary>
        public static Target Azure(Func<FakeAzureRequest, FakeAzureFault>? faults, int retries, ConcurrentDictionary<string, int> fired, Func<CancellationToken, Task>? schedule = null) =>
            new(faults, retries, fired, schedule);

        public async Task<LogOutcome> RunAsync(object request)
        {
            CurrentRequest.Value = request;
            var write = request is LogAppend or LogOpen or LogDeleteBase;
            var (writing, writes) = (Volatile.Read(ref _writing), Volatile.Read(ref _writes));
            var deferred = _service?.HasDeferred ?? _objectFaults?.HasHeld ?? false;
            if (write)
            {
                _ = Interlocked.Increment(ref _writing);
                _ = Interlocked.Increment(ref _writes);
            }

            try
            {
                var outcome = await _executor.RunAsync(request, CancellationToken.None);
                var overlapped = writing > 0 || deferred || Volatile.Read(ref _writes) != writes;
                return outcome.Kind == LogOutcomeKind.Stale && overlapped ? new LogOutcome(LogOutcomeKind.StaleRacing) : outcome;
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                return LogOutcome.Unknown;
            }
            finally
            {
                if (write)
                {
                    _ = Interlocked.Decrement(ref _writing);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            var violations = _service?.Violations ?? [];
            foreach (var (fault, count) in _service?.FaultsFired() ?? new Dictionary<FakeAzureFault, int>())
            {
                _ = _fired.AddOrUpdate(fault.ToString(), count, (_, total) => total + count);
            }

            var events = _objectFaults?.TakeEvents() ?? [];
            foreach (var fault in FaultInjectingObjectStore.WriteFaults)
            {
                var name = fault.ToString();
                var count = events.Count(@event => @event.EndsWith(": fault " + name, StringComparison.Ordinal));
                _ = _fired.AddOrUpdate(name, count, (_, total) => total + count);
            }

            _service?.Dispose();
            _files?.Dispose();
            Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
            return ValueTask.CompletedTask;
        }

        /// <summary>Returns whether a call failed at its transport, or at a server error its client did not retry, so its outcome is unknown.</summary>
        private static bool IsTransportFailure(Exception exception) => exception switch
        {
            IOException or HttpRequestException => true,
            RequestFailedException { Status: 0 or >= 500 } => true,
            AggregateException aggregate => aggregate.InnerExceptions.All(IsTransportFailure),
            _ => false,
        };
    }
}

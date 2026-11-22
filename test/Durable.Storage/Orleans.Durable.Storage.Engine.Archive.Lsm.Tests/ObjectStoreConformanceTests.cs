using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.FileSystem.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Checks every <see cref="IObjectStore"/> provider against <see cref="ObjectStoreSpec"/> through the conformance kit
/// (verification.md §3.1, §4.1): random sequences and concurrent groups which CsCheck generates and shrinks, and the
/// sequences Accordant generates to a small depth; and the spec with unknown outcomes over a store which injects
/// failures, delays writes, and lands them later.
/// </summary>
/// <remarks>
/// The file-system store runs concurrent groups without listings: a directory listing concurrent with creates and deletes
/// may return names those make as hints, which a listing judged at one point does not allow (archive §3). A separate
/// property bounds such a listing by the names no concurrent call changes.
/// </remarks>
public sealed class ObjectStoreConformanceTests : IDisposable
{
    private static readonly string RecordedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-conformance"));

    /// <summary>
    /// The most faults a faulty store throws in one case, each an unknown outcome, which bounds the case's unknown writes.
    /// The checker's search grows about tenfold for every two unknown writes (#363); six keep a sampled case under a few
    /// hundred possible states, and leave each case several faults of every kind, held writes landing later among them.
    /// </summary>
    private const int MaxUnknownOutcomes = 6;
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task TheMemoryStoreConforms()
    {
        var kit = Kit(static _ => Target(new InMemoryObjectStore()), unknownOutcomes: false);
        await kit.SampleAsync(kit.Sequences(30), iterations: 400);
        await kit.SampleAsync(kit.ConcurrentSequences(15), iterations: 200);
    }

    /// <summary>
    /// object-ledgers.md §3.1: a store of both interfaces placed under a cluster's prefix keeps the contract, appends,
    /// seals, and tombstones among it, beside a sibling cluster's objects under another prefix of the same store, which
    /// it neither lists, reads, nor changes; and every object it writes lies under its prefix.
    /// </summary>
    [Fact]
    public async Task APrefixedStoreOfBothInterfacesConformsUnderItsPrefix()
    {
        var inners = new System.Collections.Concurrent.ConcurrentBag<InMemoryObjectStore>();
        var sibling = new ObjectName("q/1/0/a");
        var kit = Kit(
            async seed =>
            {
                var inner = new InMemoryObjectStore();
                using var content = ArcBuffer.Wrap(ObjectStoreContract.Bytes(10, 9));
                _ = await inner.CreateAsync(sibling, content, crc32c: null, Token);
                _ = await inner.CreateAsync(new ObjectName("p0"), content, crc32c: null, Token);
                inners.Add(inner);
                return await Target(PrefixedAppendableObjectStore.Create(inner, "p/"));
            },
            unknownOutcomes: false);
        await kit.SampleAsync(kit.Sequences(30), iterations: 200);
        await kit.SampleAsync(kit.ConcurrentSequences(15), iterations: 100);

        foreach (var inner in inners)
        {
            var names = await inner.ListAsync(string.Empty, cancellationToken: Token).Select(static listed => listed.Name.Value).ToListAsync(Token);
            Assert.Contains(sibling.Value, names);
            Assert.Contains("p0", names);
            Assert.All(names.Where(static name => name != "p0" && !name.StartsWith("q/", StringComparison.Ordinal)), static name => Assert.StartsWith("p/", name, StringComparison.Ordinal));
            using var read = (await inner.ReadAsync(sibling, 0, 10, Token))!.Value;
            Assert.Equal(ObjectStoreContract.Bytes(10, 9), read.ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheFileSystemStoreConformsOverTheRecordedFileSystem(bool linkByRenaming)
    {
        var kit = Kit(
            _ => Target(new FileSystemObjectStore(RecordedRoot, TimeProvider.System, new RecordingFileSystem(RecordedRoot) { LinkByRenaming = linkByRenaming })),
            unknownOutcomes: false);
        await kit.SampleAsync(kit.Sequences(30), iterations: 200);
        var concurrent = Kit(
            _ => Target(new FileSystemObjectStore(RecordedRoot, TimeProvider.System, new RecordingFileSystem(RecordedRoot) { LinkByRenaming = linkByRenaming })),
            unknownOutcomes: false,
            listings: false);
        await concurrent.SampleAsync(concurrent.ConcurrentSequences(10), iterations: 100);
    }

    [Fact]
    public async Task TheFileSystemStoreConformsOverTheOperatingSystemsFileSystem()
    {
        var kit = Kit(static _ => FileSystemTarget(), unknownOutcomes: false);
        await kit.SampleAsync(kit.Sequences(15), iterations: 25, threads: 1);
        var concurrent = Kit(static _ => FileSystemTarget(), unknownOutcomes: false, listings: false);
        await concurrent.SampleAsync(concurrent.ConcurrentSequences(6), iterations: 15, threads: 1);
    }

    /// <summary>
    /// A case of 30 writes against a store which faults every call conforms within the bound: the store throws at most
    /// <see cref="MaxUnknownOutcomes"/> faults, so the checker's search over the rest of the history stays small. Without the
    /// bound, the checker keeps every unknown write's landing open for the rest of the history, and this case would take it
    /// hours (#363).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ACaseWhoseEveryCallFaultsStaysWithinTheBoundOfUnknownOutcomes()
    {
        var kit = Kit(static seed => Target(Faulty(seed, faultRate: 1.0)), unknownOutcomes: true);
        List<ConformanceRound> rounds = [.. Enumerable.Range(0, 30).Select(static index => new ConformanceRound([index % 2 == 0
            ? new ConformanceCall(ObjectStoreSpec.CreateOperation, new ObjectCreate(index / 2 % 4, (byte)(index + 1)))
            : new ConformanceCall(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(index / 2 % 4, null, (byte)(index + 1)))]))];
        await kit.RunAsync(new ConformanceCase(rounds, Seed: 7)).WaitAsync(TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task AStoreWhichFailsWithinTheContractConformsWithUnknownOutcomes()
    {
        var kit = Kit(static seed => Target(Faulty(seed)), unknownOutcomes: true);
        await kit.SampleAsync(kit.Sequences(30), iterations: 400);
        await kit.SampleAsync(kit.ConcurrentSequences(15), iterations: 200);
    }

    /// <summary>
    /// Objects of more than 4 MiB, which span several buffer pages, read whole and in ranges of 4 MiB, as a history read
    /// of a run object's section does.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheStoresServeReadsAcrossBufferPages(bool fileSystem)
    {
        var values = Gen.OneOfConst((byte)1, ObjectStoreSpec.LargeValue, (byte)(ObjectStoreSpec.LargeValue + 1));
        var lengths = Gen.OneOf(Gen.Int[1, 256], Gen.Const(ObjectStoreSpec.LargeLength));
        var kit = Kit(
            fileSystem
                ? _ => Target(new FileSystemObjectStore(RecordedRoot, TimeProvider.System, new RecordingFileSystem(RecordedRoot)))
                : static _ => Target(new InMemoryObjectStore()),
            unknownOutcomes: false,
            values: values,
            lengths: lengths);
        await kit.SampleAsync(kit.Sequences(8), iterations: 12);
    }

    [Fact]
    public async Task TheStoresConformInEveryShortSequence()
    {
        var spec = ObjectStoreSpec.Create(unknownOutcomes: false);
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, ObjectOutcome>(operation).With(request, request.ToString()));

        Add(ObjectStoreSpec.CreateOperation, new ObjectCreate(0, 1));
        Add(ObjectStoreSpec.CreateOperation, new ObjectCreate(0, 2));
        Add(ObjectStoreSpec.CreateOperation, new ObjectCreate(1, 3));
        Add(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, null, 4));
        Add(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, 1, 5));
        Add(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, 4, 6));
        Add(ObjectStoreSpec.ReadOperation, new ObjectRead(0, 200, 12));
        Add(ObjectStoreSpec.ReadVersionedOperation, new ObjectReadVersioned(0));
        Add(ObjectStoreSpec.DeleteOperation, new ObjectDelete(0));
        Add(ObjectStoreSpec.ListOperation, new ObjectList(0));
        Add(ObjectStoreSpec.ListOperation, new ObjectList(1));

        foreach (var kit in new[]
        {
            Kit(static _ => Target(new InMemoryObjectStore()), unknownOutcomes: false),
            Kit(_ => Target(new FileSystemObjectStore(RecordedRoot, TimeProvider.System, new RecordingFileSystem(RecordedRoot))), unknownOutcomes: false),
        })
        {
            var sequences = spec.GenerateTests(new ObjectStoreState(), inputs, kit.GenerationOptions(3));
            Assert.NotEmpty(sequences);
            await kit.RunAsync(sequences);
        }
    }

    /// <summary>
    /// The kit skips a call of a concurrent group only where it conflicts with an earlier call of the group, a write of the
    /// same value to the same name; writes of other values or names, and reads, run together.
    /// </summary>
    [Fact]
    public async Task TheKitSkipsOnlyTheConflictingCallsOfAGroup()
    {
        var probe = new ProbeObjectStore(new InMemoryObjectStore());
        var kit = Kit(_ => Target(probe), unknownOutcomes: false);
        ConformanceCall Call(object request) => new(ObjectStoreSpec.OperationOf(request), request);

        await kit.RunAsync(new ConformanceCase(
            [
                new ConformanceRound(
                [
                    Call(new ObjectCreate(1, 1)),
                    Call(new ObjectCreate(1, 1)),
                    Call(new ObjectReplace(0, null, 1)),
                    Call(new ObjectReplace(0, null, 1)),
                    Call(new ObjectCreate(1, 2)),
                    Call(new ObjectCreate(2, 1)),
                    Call(new ObjectRead(1, 0, 1)),
                ]),
            ],
            Seed: 1));

        Assert.Equal(
            ["Create(d/1/0/a1)", "Create(d/1/0/a1)", "Create(d/2/5/b2)", "Read(d/1/0/a1)", "Replace(manifest)"],
            probe.Calls.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A listing concurrent with creates and deletes names every object which no concurrent call changes, and names no
    /// other object but those the calls create or delete, its hints (archive §3).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConcurrentListingIsBoundedByTheNamesNoConcurrentCallChanges(bool operatingSystem)
    {
        var groups = Gen.Select(Gen.Int[0, 15], Gen.Int[1, 15], static (present, changed) => (Present: present, Changed: changed));
        await groups.SampleAsync(
            async group =>
            {
                using var directory = operatingSystem ? new TemporaryDirectory() : null;
                var store = directory is null
                    ? new FileSystemObjectStore(RecordedRoot, TimeProvider.System, new RecordingFileSystem(RecordedRoot))
                    : new FileSystemObjectStore(directory.Path, TimeProvider.System);
                var executor = new ObjectStoreExecutor(store);
                foreach (var name in Bits(group.Present))
                {
                    _ = await executor.CreateAsync(new ObjectCreate(name, 1), Token);
                }

                var calls = Bits(group.Changed)
                    .Select(name => Task.Run(() => (group.Present & (1 << name)) != 0
                        ? executor.DeleteAsync(new ObjectDelete(name), Token)
                        : executor.CreateAsync(new ObjectCreate(name, 2), Token)))
                    .ToList();
                var listing = Task.Run(() => executor.ListAsync(new ObjectList(0), cancellationToken: Token));
                await Task.WhenAll([.. calls, listing]);

                var listed = (await listing).Listed;
                Assert.Equal(group.Present & ~group.Changed, listed & ~group.Changed);
            },
            iter: operatingSystem ? 40 : 200,
            threads: 1);
    }

    /// <summary>
    /// An append whose landing a seal follows before its retry, as a client which lost the append's answer retries it,
    /// answers appended, since the sealed object holds its bytes; the mutant, which answers sealed, gives an answer the
    /// spec does not allow beside the bytes a read finds.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAppendWhichLandsBeforeASealAndIsRetriedAnswersAppended(bool mutant)
    {
        var memory = new InMemoryObjectStore();
        var store = new FaultInjectingObjectStore<InMemoryObjectStore>(memory, seed: 1, faultRate: 0)
        {
            AnswersSealedForALandedAppendMutant = mutant,
            BeforeAppendRetry = async name => _ = await memory.SealAsync(name, Token),
        };
        store.RetryNextAppend();

        var violation = ObjectStoreSpec.FirstViolation(await RunAsync(new ObjectStoreExecutor(store, store), [new ObjectCreateAppendable(4), new ObjectAppend(4, 0, 1), new ObjectSeal(4), new ObjectRead(4, 0, ObjectStoreSpec.ChunkLength)]));

        if (mutant)
        {
            Assert.NotNull(violation);
        }
        else
        {
            Assert.Null(violation);
        }
    }

    private static async Task<List<(object Request, ObjectOutcome Outcome)>> RunAsync(ObjectStoreExecutor executor, object[] requests)
    {
        var history = new List<(object Request, ObjectOutcome Outcome)>();
        foreach (var request in requests)
        {
            history.Add((request, await executor.RunAsync(request, Token)));
        }

        return history;
    }

    /// <summary>
    /// Returns the indexes of the bits <paramref name="mask"/> sets.
    /// </summary>
    private static IEnumerable<int> Bits(int mask) => Enumerable.Range(0, ObjectStoreSpec.Names.Count).Where(index => (mask & (1 << index)) != 0);

    private static Task<ObjectStoreTarget> Target<TStore>(TStore store)
        where TStore : IObjectStore, IAppendableObjectStore =>
        Task.FromResult(new ObjectStoreTarget(store, store));

    private static Task<ObjectStoreTarget> Target(IObjectStore store) => Task.FromResult(new ObjectStoreTarget(store, null));

    /// <summary>
    /// Returns a store over memory which faults within the contract, its appendable calls among them.
    /// </summary>
    private static FaultInjectingObjectStore<InMemoryObjectStore> Faulty(int seed, double faultRate = 0.25) =>
        new(new InMemoryObjectStore(), seed, faultRate, MaxUnknownOutcomes);

    private static Task<ObjectStoreTarget> FileSystemTarget()
    {
#pragma warning disable CA2000 // Ownership of the directory transfers to the target, which deletes it.
        var directory = new TemporaryDirectory();
#pragma warning restore CA2000
        var store = new FileSystemObjectStore(directory.Path, TimeProvider.System);
        return Task.FromResult(new ObjectStoreTarget(store, store, directory));
    }

    /// <summary>
    /// Returns a kit of the spec's operations, as <see cref="ObjectStoreKit.ObjectStoreOperations"/> draws them.
    /// </summary>
    private ConformanceKit<ObjectStoreState, ObjectStoreTarget> Kit(
        Func<int, Task<ObjectStoreTarget>> create,
        bool unknownOutcomes,
        bool listings = true,
        Gen<byte>? values = null,
        Gen<int>? lengths = null) =>
        new ConformanceKit<ObjectStoreState, ObjectStoreTarget>(
                ObjectStoreSpec.Create(unknownOutcomes),
                static () => new ObjectStoreState(),
                create,
                ObjectStoreSpec.IsCallerAllowed,
                Token,
                _leaks.InScopeAsync,
                ObjectStoreSpec.Conflicts)
            .ObjectStoreOperations(static (target, request) => target.RunAsync(request), listings, values, lengths, appends: true);

    /// <summary>
    /// One case's store, with the executor of its calls; an injected failure answers nothing.
    /// </summary>
    private sealed class ObjectStoreTarget(IObjectStore store, IAppendableObjectStore? appendable, IDisposable? owned = null) : IAsyncDisposable, IConformanceEvents
    {
        private readonly ObjectStoreExecutor _executor = new(store, appendable);

        public async Task<ObjectOutcome> RunAsync(object request)
        {
            try
            {
                return await _executor.RunAsync(request, Token);
            }
            catch (ObjectStoreFaultException)
            {
                return ObjectOutcome.Unknown;
            }
        }

        public IReadOnlyList<string> TakeEvents() => store is IConformanceEvents events ? events.TakeEvents() : [];

        public ValueTask DisposeAsync()
        {
            owned?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

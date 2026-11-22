using System.Buffers;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// <see cref="ArchiveLogSweeper"/> (archive-logs.md §2.5, §8.5, §10): a base is deleted only once a properties read after
/// the listing shows its log's current generation above it, never the current one, and none of a log listing one base;
/// an in-progress opener's base above the current generation is kept, which the <c>delete-without-read</c> mutant
/// deletes; an orphan a straggler re-created below the current generation is deleted again; a log which reads as corrupt
/// keeps its bases, as do a log whose bases record two key hashes, an absent log, and a base whose key hash does not
/// parse; nested log names are each swept whole from a streamed listing; a reader racing a sweep answers <c>Stale</c> and
/// reads again; and a marker is deleted only once a map read shows its ledger dropped, never the store's
/// record.
/// </summary>
public sealed class ArchiveLogSweeperTests : IDisposable
{
    private static readonly byte[] Key = "sweep-key"u8.ToArray();
    private static readonly ArchiveLogName Name = ArchiveLogNames.Hexadecimal.Of(Key);
    private static readonly LedgerSegmentId Segment = new(new LedgerId(1), Slot.Zero);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private Func<ArchiveLogRequest, ArchiveLogFault>? _faults;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LOG-I2: three openers leave three bases; a sweep reads the log once and deletes the two below its current
    /// generation, keeping the current one, and the log still reads whole.
    /// </summary>
    [Fact]
    public async Task ASweepDeletesOnlyTheBasesBelowTheCurrentGeneration()
    {
        var logs = Logs();
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        var token = await OpenAsync(logs, default, "r1");
        token = await OpenAsync(logs, token, "r2");
        _ = await OpenAsync(logs, token, "r3");

        var result = await (await SweeperAsync(logs, map)).SweepAsync(Token);

        Assert.Equal(new ArchiveLogSweepResult(3, 1, 2, 0, 0, 0, 0, BasesEnded: true), result);
        Assert.Equal("3", await GenerationsAsync(logs));
        using var read = Assert.IsType<ArchiveLogRead.Read>((await logs.ReadAsync(Name, Token)).Value);
        Assert.Equal("r3"u8.ToArray(), read.Content.ToArray());
    }

    /// <summary>
    /// A log listing one base holds only its current or an in-progress generation, so the sweep reads nothing; an opener
    /// whose base landed and whose generation did not leaves a base above the current generation, which the sweep keeps
    /// while the read shows the generation below it, and which the <c>delete-without-read</c> mutant, deleting all but the
    /// highest base it lists, would leave the current generation without: a reader then answers <c>Stale</c>.
    /// </summary>
    [Fact]
    public async Task AnInProgressOpenersBaseIsKeptAndTheCurrentGenerationsToo()
    {
        var logs = Logs();
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        var token = await OpenAsync(logs, default, "r1");
        Assert.Equal(new ArchiveLogSweepResult(1, 0, 0, 0, 0, 0, 0, BasesEnded: true), await (await SweeperAsync(logs, map)).SweepAsync(Token));
        _faults = static request => request.Kind == ArchiveLogRequestKind.OpenGeneration ? ArchiveLogFault.FailBefore : ArchiveLogFault.None;
        _ = await Assert.ThrowsAsync<IOException>(async () => await OpenAsync(logs, token, "r2"));
        _faults = null;

        Assert.Equal(new ArchiveLogSweepResult(2, 1, 0, 0, 0, 0, 0, BasesEnded: true), await (await SweeperAsync(logs, map)).SweepAsync(Token));
        Assert.Equal("1,2", await GenerationsAsync(logs));

        Assert.Equal(1, (await (await SweeperAsync(logs, map, deletesWithoutRead: true)).SweepAsync(Token)).BasesDeleted);
        Assert.IsType<ArchiveLogRead.Stale>((await logs.ReadAsync(Name, Token)).Value);
    }

    /// <summary>
    /// A reader racing a sweep (§10): the read gets the log's write-ahead blob at generation 1, and before it gets that
    /// generation's checkpoint an opener makes generation 2 and a sweep deletes generation 1's; the read answers
    /// <c>Stale</c>, and the reader's next read holds generation 2 whole.
    /// </summary>
    [Fact]
    public async Task AReaderRacingASweepAnswersStaleAndReadsAgain()
    {
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        Action? race = null;
        using var service = new FakeAzureBlobService(faults: request =>
        {
            if (request.Operation == FakeAzureOperation.GetBlob && request.Blob.StartsWith("checkpoints/", StringComparison.Ordinal) && Interlocked.Exchange(ref race, null) is { } run)
            {
                run();
            }

            return FakeAzureFault.None;
        });
        var logs = new Orleans.Durable.Storage.AzureStorage.Archive.AzureArchiveLogStore(service.Client(), "json");
        var first = await OpenAsync(logs, Name, default, "r1");
        var sweeper = await SweeperAsync(logs, map);
        race = () =>
        {
            _ = OpenAsync(logs, Name, first, "r2").GetAwaiter().GetResult();
            Assert.Equal(1, sweeper.SweepAsync(Token).GetAwaiter().GetResult().BasesDeleted);
        };

        Assert.Same(ArchiveLogRead.Stale.Instance, (await logs.ReadAsync(Name, Token)).Value);
        Assert.Null(race);
        using var read = Assert.IsType<ArchiveLogRead.Read>((await logs.ReadAsync(Name, Token)).Value);
        Assert.Equal("r2"u8.ToArray(), read.Content.ToArray());
    }

    /// <summary>
    /// A straggler opener which re-creates a base the sweeper deleted leaves an orphan below the current generation, which
    /// a later sweep deletes again; the straggler's write is held.
    /// </summary>
    [Fact]
    public async Task AnOrphanAStragglerReCreatedIsDeletedAgain()
    {
        var logs = Logs();
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        var first = await OpenAsync(logs, default, "r1");
        _ = await OpenAsync(logs, first, "r2");
        Assert.Equal(1, (await (await SweeperAsync(logs, map)).SweepAsync(Token)).BasesDeleted);

        Assert.Equal(first, await OpenAsync(logs, default, "r1"));
        Assert.Equal("1,2", await GenerationsAsync(logs));

        Assert.Equal(1, (await (await SweeperAsync(logs, map)).SweepAsync(Token)).BasesDeleted);
        Assert.Equal("2", await GenerationsAsync(logs));
    }

    /// <summary>A log whose properties read finds it corrupt keeps every base, and the sweep counts it.</summary>
    [Fact]
    public async Task ACorruptLogKeepsItsBases()
    {
        var logs = Logs();
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        _ = await OpenAsync(logs, await OpenAsync(logs, default, "r1"), "r2");
        logs.SetKeyHash(Name.Value, 7);

        Assert.Equal(new ArchiveLogSweepResult(2, 1, 0, 0, 0, 1, 0, BasesEnded: true), await (await SweeperAsync(logs, map)).SweepAsync(Token));
        Assert.Equal("1,2", await GenerationsAsync(logs));
    }

    /// <summary>
    /// A marker is deleted only once a map read after the listing shows its ledger dropped: a dropped ledger's markers go,
    /// a listed ledger's stay, and the store's record is never touched.
    /// </summary>
    [Fact]
    public async Task OnlyADroppedLedgersMarkersAreDeleted()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var dropped = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(dropped, ("a", RowKind.Append, "a0"));
        _ = await world.Ledgers.EndSegmentAsync(dropped, Token);
        await world.ProposeAsync(dropped, ("a", RowKind.Append, "a1"));
        var t = await world.CloseAsync(dropped, Token);
        var worker = world.Worker();
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(worker, dropped, t, Token)).Value);
        var listed = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(listed, ("a", RowKind.Append, "a2"));
        var end = await world.Ledgers.EndSegmentAsync(listed, Token);
        var sweeper = new ArchiveLogSweeper(world.Gate, world.Ledgers.Map, NullLogger<ArchiveLogSweeper>.Instance);
        Assert.Equal(new ArchiveLogSweepResult(0, 0, 0, 2, 0, 0, 0, BasesEnded: true), await sweeper.SweepAsync(Token));
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await worker.SubmitDropAsync(dropped, world.Ledgers.CommitAsync, Token)).Value);
        _ = await world.Ledgers.Map.ReadAsync(Token);
        Assert.Equal(new ArchiveLogDrainOutcome.OpenSegment(end), (await world.DrainAsync(worker, listed, end + 1, Token)).Value);

        Assert.Equal(new ArchiveLogSweepResult(0, 0, 0, 3, 2, 0, 0, BasesEnded: true), await sweeper.SweepAsync(Token));

        Assert.Equal([(listed, Slot.Zero, end)], await world.MarkersAsync(Token));
        Assert.Equal(
            [ArchiveLogNames.StoreRecord],
            (await world.Objects.ListAsync(ArchiveLogNames.ReservedPrefix, cancellationToken: Token).ToListAsync(Token)).Select(static listed => listed.Name.Value).Where(static name => !name.StartsWith(ArchiveLogNames.DrainMarkerPrefix, StringComparison.Ordinal)));
        Assert.True((await world.ReadAsync("a", Token)).SameAs(world.Fold("a")));
    }

    /// <summary>
    /// A log whose bases record two key hashes, which no conforming store leaves, keeps them all, and the sweep counts it
    /// unreadable; a log listing two bases which is absent, before its key's first write landed, keeps them; and a base whose
    /// recorded key hash does not parse is counted and kept, the Azure listing naming it with no key hash.
    /// </summary>
    [Fact]
    public async Task BasesTheSweepCannotJudgeAreKept()
    {
        using var service = new FakeAzureBlobService();
        var logs = new Orleans.Durable.Storage.AzureStorage.Archive.AzureArchiveLogStore(service.Client(), "json");
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        var (twoKeys, absent, unparsed) = (new ArchiveLogName("two-keys", 1), new ArchiveLogName("absent", 2), new ArchiveLogName("unparsed", 3));
        foreach (var name in (ArchiveLogName[])[twoKeys, absent, unparsed])
        {
            _ = await OpenAsync(logs, name, await OpenAsync(logs, name, default, "r1"), "r2");
        }

        Rekey(service, "checkpoints/two-keys/" + 1.ToString("D20", System.Globalization.CultureInfo.InvariantCulture), "0000000000000009");
        service.Remove("wal/absent");
        Rekey(service, "checkpoints/unparsed/" + 1.ToString("D20", System.Globalization.CultureInfo.InvariantCulture), "not-a-key-hash!!");

        var result = await (await SweeperAsync(logs, map)).SweepAsync(Token);

        Assert.Equal(new ArchiveLogSweepResult(6, 1, 0, 0, 0, 1, 1, BasesEnded: true), result);
        Assert.Equal(6, (await logs.ListGenerationsAsync(continuation: null, Token).SelectMany(static page => page.Generations).ToListAsync(Token)).Count);

        static void Rekey(FakeAzureBlobService service, string checkpoint, string keyHash) =>
            service.SetMetadata(checkpoint, [.. service.Peek(checkpoint)!.Value.Metadata.Select(pair => pair.Key == "durable_key" ? KeyValuePair.Create(pair.Key, keyHash) : pair)]);
    }

    /// <summary>
    /// A sweep streams the listing, holding open only the logs whose block of names the listing has not left: logs whose
    /// names extend another's with <c>/</c>, whose bases fall between that log's, are each judged whole, and the sweep holds
    /// at most as many logs open as the names nest.
    /// </summary>
    [Fact]
    public async Task NestedLogNamesAreEachSweptWhole()
    {
        var logs = Logs();
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        var names = (string[])["a", "a/b", "a/" + 2.ToString("D20", System.Globalization.CultureInfo.InvariantCulture), "a-c", "b"];
        foreach (var (name, index) in names.Select(static (name, index) => (name, index)))
        {
            var log = new ArchiveLogName(name, (ulong)index + 10);
            RowToken token = default;
            for (var generation = 0; generation < 3; generation++)
            {
                token = await OpenAsync(logs, log, token, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}-{generation}"));
            }
        }

        var sweeper = await SweeperAsync(logs, map);
        var result = await sweeper.SweepAsync(Token);

        Assert.Equal(new ArchiveLogSweepResult(15, 5, 10, 0, 0, 0, 0, BasesEnded: true), result);
        Assert.Equal(2, sweeper.MostOpenLogs);
        Assert.All((await logs.ListGenerationsAsync(continuation: null, Token).SelectMany(static page => page.Generations).ToListAsync(Token)).Select(static generation => generation.Generation), static generation => Assert.Equal(3UL, generation));
    }

    /// <summary>
    /// A sweep owns its deletions: a properties read which fails transiently fails the sweep only once the deletions
    /// already in flight, of a log judged before it, settled, so none outlives the sweep or its concurrency bound. Its
    /// waits are bounded, so a sweep which never settles fails the test rather than hanging it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AFailedPropertiesReadWaitsForTheDeletionsInFlight()
    {
        var inner = Logs();
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, TestContext.Current.CancellationToken);
        var (first, failing) = (new ArchiveLogName("a", 1), new ArchiveLogName("b", 2));
        foreach (var name in (ArchiveLogName[])[first, failing])
        {
            _ = await OpenAsync(inner, name, await OpenAsync(inner, name, default, "r1"), "r2");
        }

        var logs = new HoldingDeletions(inner, failing.Value);
        var sweep = (await SweeperAsync(logs, map)).SweepAsync(Token);
        await Task.WhenAll(logs.Held, logs.ReadFailed).WaitAsync(Patience, Token);
        Assert.False(sweep.IsCompleted, "The sweep returned while a deletion it started was in flight.");

        logs.Release();
        _ = await Assert.ThrowsAsync<IOException>(() => sweep.WaitAsync(Patience, Token));
        Assert.Equal(1, logs.Deleted);
    }

    /// <summary>
    /// archive-logs.md §8.5: a sweep lists at most <c>PagesPerSweep</c> pages of the bases, a slice, and the next resumes
    /// after it from the store's continuation: a log whose bases straddle two slices is judged whole once the listing
    /// passes its block, by the sweep whose slice does, and the sweep after the listing's end lists from its start.
    /// </summary>
    [Fact]
    public async Task EachSweepListsOneSliceAndTheNextResumesAfterIt()
    {
        var logs = new MemoryArchiveLogStore() { ListPageSize = 2 };
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        foreach (var log in (ArchiveLogName[])[new("a", 1), new("b", 2)])
        {
            RowToken token = default;
            for (var generation = 0; generation < 3; generation++)
            {
                token = await OpenAsync(logs, log, token, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{log.Value}-{generation}"));
            }
        }

        var sweeper = await SweeperAsync(logs, map, pagesPerSweep: 1);

        Assert.Equal(new ArchiveLogSweepResult(2, 0, 0, 0, 0, 0, 0, BasesEnded: false), await sweeper.SweepAsync(Token));
        Assert.Equal("1,1,2,2,3,3", await GenerationsAsync(logs));
        Assert.Equal(new ArchiveLogSweepResult(2, 1, 2, 0, 0, 0, 0, BasesEnded: false), await sweeper.SweepAsync(Token));
        Assert.Equal("1,2,3,3", await GenerationsAsync(logs));
        Assert.Equal(new ArchiveLogSweepResult(2, 1, 2, 0, 0, 0, 0, BasesEnded: true), await sweeper.SweepAsync(Token));
        Assert.Equal("3,3", await GenerationsAsync(logs));
        Assert.Equal(new ArchiveLogSweepResult(2, 0, 0, 0, 0, 0, 0, BasesEnded: true), await sweeper.SweepAsync(Token));
    }

    /// <summary>
    /// archive-logs.md §8.5, LOG-I2: a sweep whose slice fails leaves the next to list that slice again from its start, its
    /// open logs forgotten, so a log whose bases straddle the slice's start is judged on those the new slice lists alone:
    /// the sweeps delete exactly the listed bases below each log's current generation, fewer than a whole pass would,
    /// never one the current generation needs, and the next pass deletes the rest.
    /// </summary>
    [Fact]
    public async Task AFailedSweepsSuccessorListsItsSliceAgainFromItsStart()
    {
        var logs = new MemoryArchiveLogStore(faults: request => _faults?.Invoke(request) ?? ArchiveLogFault.None) { ListPageSize = 2 };
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        foreach (var log in (ArchiveLogName[])[new("a", 1), new("b", 2)])
        {
            RowToken token = default;
            for (var generation = 0; generation < 3; generation++)
            {
                token = await OpenAsync(logs, log, token, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{log.Value}-{generation}"));
            }
        }

        var sweeper = await SweeperAsync(logs, map, pagesPerSweep: 1);
        Assert.False((await sweeper.SweepAsync(Token)).BasesEnded);
        var sliceStart = sweeper.ResumeAfter;
        Assert.NotNull(sliceStart);

        _faults = static request => request.Kind == ArchiveLogRequestKind.List ? ArchiveLogFault.FailBefore : ArchiveLogFault.None;
        _ = await Assert.ThrowsAsync<IOException>(() => sweeper.SweepAsync(Token));
        _faults = null;
        Assert.Equal(sliceStart, sweeper.ResumeAfter);

        Assert.Equal(new ArchiveLogSweepResult(2, 0, 0, 0, 0, 0, 0, BasesEnded: false), await sweeper.SweepAsync(Token));
        Assert.Equal(new ArchiveLogSweepResult(2, 1, 2, 0, 0, 0, 0, BasesEnded: true), await sweeper.SweepAsync(Token));
        Assert.Equal("1,2,3,3", await GenerationsAsync(logs));
        _ = await sweeper.SweepAsync(Token);
        Assert.Equal(new ArchiveLogSweepResult(2, 1, 2, 0, 0, 0, 0, BasesEnded: true), await sweeper.SweepAsync(Token));
        Assert.Equal("3,3", await GenerationsAsync(logs));
    }

    /// <summary>A sweeper runs one sweep at a time: a sweep asked for while another runs is refused.</summary>
    [Fact]
    public async Task ASweeperRefusesASecondSweepWhileOneRuns()
    {
        var inner = Logs();
        await using var map = await LiveLedgers.StartAsync(new Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister(), TimeProvider.System, Token);
        var (first, second) = (new ArchiveLogName("a", 1), new ArchiveLogName("b", 2));
        foreach (var name in (ArchiveLogName[])[first, second])
        {
            _ = await OpenAsync(inner, name, await OpenAsync(inner, name, default, "r1"), "r2");
        }

        var logs = new HoldingDeletions(inner, failing: string.Empty);
        var sweeper = await SweeperAsync(logs, map);
        var sweep = sweeper.SweepAsync(Token);
        await logs.Held.WaitAsync(Patience, Token);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => sweeper.SweepAsync(Token));
        logs.Release();
        Assert.Equal(2, (await sweep.WaitAsync(Patience, Token)).BasesDeleted);
    }

    private static async Task<string> GenerationsAsync(IArchiveLogStore logs) =>
        string.Join(",", (await logs.ListGenerationsAsync(continuation: null, Token).SelectMany(static page => page.Generations).ToListAsync(Token)).Select(static generation => generation.Generation).Order());

    private static async Task<ArchiveLogSweeper> SweeperAsync(IArchiveLogStore logs, LiveLedgers map, bool deletesWithoutRead = false, int pagesPerSweep = 2_000) =>
        new(await ArchiveLogWorld.PassGateAsync(new LogArchiveStore(logs, new InMemoryObjectStore(), ArchiveLogNames.Hexadecimal), Token), map.Map, NullLogger<ArchiveLogSweeper>.Instance) { DeletesWithoutReadMutant = deletesWithoutRead, PagesPerSweep = pagesPerSweep };

    /// <summary>Writes a <c>Replace</c> of <paramref name="body"/> from <paramref name="token"/> and returns the token after it.</summary>
    private static Task<RowToken> OpenAsync(MemoryArchiveLogStore logs, RowToken token, string body) => OpenAsync(logs, Name, token, body);

    private static async Task<RowToken> OpenAsync(IArchiveLogStore logs, ArchiveLogName name, RowToken token, string body)
    {
        var write = new ArchiveWrite(Segment, token, [ArchiveCommand.Replace(body.Length)]);
        var writer = new ArchiveLogWriter(logs, TimeProvider.System, new ArchiveLogWriterOptions { MaxRetries = 0 });
        Assert.Same(ArchiveLogConditionOutcome.Held.Instance, (await writer.WriteAsync(name, write, new ReadOnlySequence<byte>(System.Text.Encoding.UTF8.GetBytes(body)), Token)).Value);
        return new LogArchiveStore(logs, new InMemoryObjectStore(), ArchiveLogNames.Hexadecimal).Plan(write);
    }

    private MemoryArchiveLogStore Logs() => new(faults: request => _faults?.Invoke(request) ?? ArchiveLogFault.None);

    /// <summary>
    /// An archive-log store which holds every deletion until released, and fails every properties read of one log,
    /// signalling the first failure as it throws.
    /// </summary>
    private sealed class HoldingDeletions(MemoryArchiveLogStore inner, string failing) : IArchiveLogStore
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deleted;

        public Task Held => _held.Task;

        /// <summary>Gets a task which completes once the failing properties read threw.</summary>
        public Task ReadFailed => _readFailed.Task;

        public int Deleted => Volatile.Read(ref _deleted);

        public ArchiveLogLayout Layout => inner.Layout;

        public void Release() => _release.TrySetResult();

        public bool AdmitsName(string name) => inner.AdmitsName(name);

        public ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.ReadAsync(name, cancellationToken);

        public ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken)
        {
            if (name.Value != failing)
            {
                return inner.GetPropertiesAsync(name, cancellationToken);
            }

            _ = _readFailed.TrySetResult();
            return ValueTask.FromException<ArchiveLogProperties?>(new IOException("The properties read failed."));
        }

        public ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<ReadOnlySequence<byte>> run, CancellationToken cancellationToken) => inner.AppendAsync(name, from, run, cancellationToken);

        public ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, CancellationToken cancellationToken) => inner.OpenAsync(name, from, opener, content, cancellationToken);

        public IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, CancellationToken cancellationToken) => inner.ListGenerationsAsync(continuation, cancellationToken);

        public async ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken)
        {
            _ = _held.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            await inner.DeleteGenerationAsync(log, generation, cancellationToken);
            _ = Interlocked.Increment(ref _deleted);
        }
    }
}

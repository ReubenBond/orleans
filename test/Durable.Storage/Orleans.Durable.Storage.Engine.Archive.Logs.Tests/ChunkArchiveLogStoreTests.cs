using System.Buffers;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// The chunk layout's objects (archive-logs.md §7.4): their names; reads of the chain of chunks up to its first gap, and
/// <c>Stale</c> when an object listed is gone; the corruption a reader reports; the names it admits; nested logs, the
/// sweeper's listing and deletion; and the listing before each create, which keeps a straggler's objects out of a
/// generation the log has not reached, as its mutant shows.
/// </summary>
public sealed class ChunkArchiveLogStoreTests : IDisposable
{
    private static readonly ArchiveLogOpener Replace = new(ArchiveLogOpenerKind.Replace, null, 2);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly InMemoryObjectStore _objects = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AGenerationsObjectsAreItsBaseAndOneChunkPerRunNamedByItsOffset()
    {
        var store = new ChunkArchiveLogStore(_objects);
        var log = Name("k/ab");
        Applied(await store.AppendAsync(log, ArchiveLogPosition.Origin, [Bytes(1, 2)], Token));
        Applied(await store.AppendAsync(log, new(1, 0, 2, 1), [Bytes(3)], Token));
        Applied(await store.OpenAsync(log, new(1, 0, 3, 2), Replace, Bytes(200, 201), Token));
        Applied(await store.AppendAsync(log, new(2, 2, 0, 0), [Bytes(4)], Token));

        var hash = log.KeyHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            [
                "k/ab/00000000000000000001/00000000000000000000",
                "k/ab/00000000000000000001/00000000000000000002",
                "k/ab/00000000000000000001/base-" + hash,
                "k/ab/00000000000000000002/00000000000000000000",
                "k/ab/00000000000000000002/base-" + hash,
            ],
            await NamesAsync(_objects));
        await AssertReadsAsync(store, log, new(2, 2, 1, 1), [200, 201, 4]);
        Assert.Equal(new ArchiveLogProperties(new(2, 2, 1, 1), Replace), await store.GetPropertiesAsync(log, Token));
    }

    /// <summary>A chunk past a gap is not the log's until the chain from offset 0 reaches it, after which it is.</summary>
    [Fact]
    public async Task AReadStopsAtTheChainsFirstGap()
    {
        var store = new ChunkArchiveLogStore(_objects);
        var log = Name("k/gap");
        Applied(await store.AppendAsync(log, ArchiveLogPosition.Origin, [Bytes(1, 2)], Token));
        await CreateAsync(ChunkArchiveLogStore.ChunkName(log.Value, 1, 5), Chunk(6, 7));

        await AssertReadsAsync(store, log, new(1, 0, 2, 1), [1, 2]);
        Assert.Equal(new ArchiveLogPosition(1, 0, 2, 1), (await store.GetPropertiesAsync(log, Token))!.Position);
        Applied(await store.AppendAsync(log, new(1, 0, 2, 1), [Bytes(3, 4, 5)], Token));
        await AssertReadsAsync(store, log, new(1, 0, 7, 3), [1, 2, 3, 4, 5, 6, 7]);
    }

    [Fact]
    public async Task AnObjectGoneAfterTheListingMakesAReadStale()
    {
        var vanishing = new VanishingObjectStore(_objects);
        var store = new ChunkArchiveLogStore(vanishing);
        var log = Name("k/stale");
        Applied(await store.AppendAsync(log, ArchiveLogPosition.Origin, [Bytes(1)], Token));

        vanishing.Vanish = ChunkArchiveLogStore.ChunkName(log.Value, 1, 0);
        _ = Assert.IsType<ArchiveLogRead.Stale>((await store.ReadAsync(log, Token)).Value);
        vanishing.Vanish = ChunkArchiveLogStore.BaseName(log.Value, 1, log.KeyHash);
        _ = Assert.IsType<ArchiveLogRead.Stale>((await store.ReadAsync(log, Token)).Value);
        vanishing.Vanish = null;
        await AssertReadsAsync(store, log, new(1, 0, 1, 1), [1]);
    }

    [Fact]
    public async Task AReaderReportsObjectsWhichBreakTheirFormatOrNameAnotherKey()
    {
        var store = new ChunkArchiveLogStore(_objects);

        var twoBases = Name("k/two");
        Applied(await store.AppendAsync(twoBases, ArchiveLogPosition.Origin, [Bytes(1)], Token));
        await CreateAsync(ChunkArchiveLogStore.BaseName(twoBases.Value, 1, twoBases.KeyHash ^ 1), ArchiveLogChunkBase.Encode(twoBases.KeyHash ^ 1, null, default));
        await AssertCorruptAsync(store, twoBases, "2 bases of generation 1");

        var other = Name("k/other");
        await CreateAsync(ChunkArchiveLogStore.BaseName(other.Value, 1, other.KeyHash ^ 1), ArchiveLogChunkBase.Encode(other.KeyHash ^ 1, null, default));
        await AssertCorruptAsync(store, other, "names the key hash");

        var header = Name("k/header");
        await CreateAsync(ChunkArchiveLogStore.BaseName(header.Value, 1, header.KeyHash), ArchiveLogChunkBase.Encode(header.KeyHash ^ 1, null, default));
        await AssertCorruptAsync(store, header, "records the key hash");

        var damaged = Name("k/damaged");
        var @base = ArchiveLogChunkBase.Encode(damaged.KeyHash, Replace, Bytes(1, 2, 3));
        @base[^1] ^= 1;
        await CreateAsync(ChunkArchiveLogStore.BaseName(damaged.Value, 1, damaged.KeyHash), @base);
        Assert.Equal(new ArchiveLogProperties(new(1, 2, 0, 0), Replace), await store.GetPropertiesAsync(damaged, Token));
        var corrupt = await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await store.ReadAsync(damaged, Token));
        Assert.Equal(ArchiveLogChunkBase.ReasonBadMd5, DecodeRejection.ReasonOf(corrupt.InnerException!));

        var twoWrites = Name("k/twice");
        Applied(await store.OpenAsync(twoWrites, ArchiveLogPosition.Origin, Replace, Bytes(1), Token));
        _ = await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await store.OpenAsync(twoWrites, ArchiveLogPosition.Origin, Replace, Bytes(2), Token));
    }

    /// <summary>
    /// An opener from <c>Origin</c> after a key's first append run meets that run's base, which records no write to
    /// compare, and fails its condition, as on Azure, where the run leaves no checkpoint.
    /// </summary>
    [Fact]
    public async Task AnOpenerWhichMeetsAFirstRunsBaseFailsItsCondition()
    {
        var store = new ChunkArchiveLogStore(_objects);
        var log = Name("k/first");
        Applied(await store.AppendAsync(log, ArchiveLogPosition.Origin, [Bytes(1)], Token));

        _ = Assert.IsType<ArchiveLogWriteOutcome.ConditionFailed>((await store.OpenAsync(log, ArchiveLogPosition.Origin, Replace, Bytes(5), Token)).Value);
        await AssertReadsAsync(store, log, new(1, 0, 1, 1), [1]);
    }

    /// <summary>
    /// A listing may miss a base created during it while it names a straggler's chunk created after that base, so a read
    /// lists the log again and answers <c>Stale</c> when a newer generation appears; the <c>skips-read-recheck</c> mutant
    /// answers a generation the log had left, with the straggler's chunk in it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadWhichMissedANewerBaseIsStale(bool mutant)
    {
        var missing = new MissingObjectStore(_objects);
        var store = new ChunkArchiveLogStore(missing) { SkipsReadRecheckMutant = mutant };
        var log = Name("k/missed");
        Applied(await store.AppendAsync(log, ArchiveLogPosition.Origin, [Bytes(1)], Token));
        Applied(await store.OpenAsync(log, new(1, 0, 1, 1), Replace, Bytes(2), Token));
        await CreateAsync(ChunkArchiveLogStore.ChunkName(log.Value, 1, 1), Chunk(3));

        missing.MissOnce = ChunkArchiveLogStore.BaseName(log.Value, 2, log.KeyHash);
        var read = (await store.ReadAsync(log, Token)).Value;
        if (mutant)
        {
            using var stale = Assert.IsType<ArchiveLogRead.Read>(read);
            Assert.Equal(new ArchiveLogPosition(1, 0, 2, 2), stale.Position);
            return;
        }

        _ = Assert.IsType<ArchiveLogRead.Stale>(read);
        missing.MissOnce = ChunkArchiveLogStore.BaseName(log.Value, 2, log.KeyHash);
        Assert.Equal(new ArchiveLogProperties(new(2, 2, 0, 0), Replace), await store.GetPropertiesAsync(log, Token));
        await AssertReadsAsync(store, log, new(2, 2, 0, 0), [2]);
    }

    /// <summary>
    /// Admission keeps a key within the layout's chunks only as far as the headroom holds (archive §3), so a generation
    /// at its limit refuses the next chunk, which blocks the key's drain, and lands nothing.
    /// </summary>
    [Fact]
    public async Task AGenerationAtItsLimitRefusesAChunk()
    {
        var store = new ChunkArchiveLogStore(_objects, new ArchiveLogLayout(ChunkArchiveLogStore.LayoutName, ArchiveLogPacking.Chunks, null, 2, 1, ChunkArchiveLogStore.NameRule));
        var log = Name("k/full");
        Applied(await store.AppendAsync(log, ArchiveLogPosition.Origin, [Bytes(1)], Token));
        Applied(await store.AppendAsync(log, new(1, 0, 1, 1), [Bytes(2)], Token));

        _ = await Assert.ThrowsAsync<ArchiveLogRefusedException>(async () => await store.AppendAsync(log, new(1, 0, 2, 2), [Bytes(3)], Token));
        await AssertReadsAsync(store, log, new(1, 0, 2, 2), [1, 2]);
    }

    /// <summary>
    /// A straggler's chunk which lands after the sweeper deleted its generation is listed before its log's next base,
    /// with that base's key hash, and the sweeper's deletion of the generation reclaims it.
    /// </summary>
    [Fact]
    public async Task AStragglerChunkInASweptGenerationIsListedAndReclaimed()
    {
        var store = new ChunkArchiveLogStore(_objects);
        var log = Name("k/orphan");
        Applied(await store.AppendAsync(log, ArchiveLogPosition.Origin, [Bytes(1)], Token));
        Applied(await store.OpenAsync(log, new(1, 0, 1, 1), Replace, Bytes(2), Token));
        await store.DeleteGenerationAsync(log.Value, 1, Token);
        await CreateAsync(ChunkArchiveLogStore.ChunkName(log.Value, 1, 1), Chunk(3));

        Assert.Equal([new ArchiveLogGeneration(log.Value, 1, log.KeyHash), new ArchiveLogGeneration(log.Value, 2, log.KeyHash)], await GenerationsAsync(store));
        await store.DeleteGenerationAsync(log.Value, 1, Token);
        Assert.Equal([ChunkArchiveLogStore.BaseName(log.Value, 2, log.KeyHash).Value], await NamesAsync(_objects));
    }

    /// <summary>An opener which finds the log past its generation still checks the base it would have created.</summary>
    [Fact]
    public async Task AnOpenerWhichFindsTheLogPastItsGenerationChecksItsBase()
    {
        var store = new ChunkArchiveLogStore(_objects);
        var log = Name("k/passed");
        Applied(await store.OpenAsync(log, ArchiveLogPosition.Origin, Replace, Bytes(1), Token));
        Applied(await store.OpenAsync(log, new(1, 2, 0, 0), Replace, Bytes(2), Token));

        _ = Assert.IsType<ArchiveLogWriteOutcome.ConditionFailed>((await store.OpenAsync(log, ArchiveLogPosition.Origin, Replace, Bytes(1), Token)).Value);
        _ = await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await store.OpenAsync(log, ArchiveLogPosition.Origin, Replace, Bytes(200), Token));
    }

    /// <summary>
    /// A reader reports, by their reasons, a base above generation 1 which records no opener, a base shorter than its
    /// header, a chunk which holds only its trailer, and a chunk whose content fails its CRC-32C.
    /// </summary>
    [Fact]
    public async Task AReaderReportsMissingOpenersAndShortOrDamagedObjects()
    {
        var store = new ChunkArchiveLogStore(_objects);

        var noOpener = Name("k/no-opener");
        await CreateAsync(ChunkArchiveLogStore.BaseName(noOpener.Value, 2, noOpener.KeyHash), ArchiveLogChunkBase.Encode(noOpener.KeyHash, null, default));
        Assert.Equal(ArchiveLogChunkBase.ReasonOpenerMissing, await CorruptReasonAsync(store, noOpener));

        var shortBase = Name("k/short");
        await CreateAsync(ChunkArchiveLogStore.BaseName(shortBase.Value, 1, shortBase.KeyHash), ArchiveLogChunkBase.Encode(shortBase.KeyHash, null, default)[..59]);
        Assert.Equal(ArchiveLogChunkBase.ReasonTruncated, await CorruptReasonAsync(store, shortBase));

        var trailerOnly = Name("k/trailer");
        Applied(await store.AppendAsync(trailerOnly, ArchiveLogPosition.Origin, [Bytes(1)], Token));
        await CreateAsync(ChunkArchiveLogStore.ChunkName(trailerOnly.Value, 1, 1), new byte[ArchiveLogChunk.TrailerLength]);
        Assert.Equal(ArchiveLogChunk.ReasonTruncated, await CorruptReasonAsync(store, trailerOnly));

        var damaged = Name("k/damaged-chunk");
        Applied(await store.AppendAsync(damaged, ArchiveLogPosition.Origin, [Bytes(1)], Token));
        var chunk = Chunk(2, 3);
        chunk[0] ^= 1;
        await CreateAsync(ChunkArchiveLogStore.ChunkName(damaged.Value, 1, 1), chunk);
        var corrupt = await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await store.ReadAsync(damaged, Token));
        Assert.Equal(ArchiveLogChunk.ReasonBadCrc, DecodeRejection.ReasonOf(corrupt.InnerException!));
    }

    [Theory]
    [InlineData("k/ab", true)]
    [InlineData("journals/a-b_c/0123456789abcdef", true)]
    [InlineData("durable-storagex/a", true)]
    [InlineData("durable-storage", false)]
    [InlineData("durable-storage/x", false)]
    [InlineData("K/ab", false)]
    [InlineData("k.ab", false)]
    [InlineData("k//ab", false)]
    [InlineData("k/", false)]
    [InlineData("", false)]
    public void AdmitsOnlyNamesItsObjectsCanCarry(string name, bool admitted) =>
        Assert.Equal(admitted, new ChunkArchiveLogStore(_objects).AdmitsName(name));

    [Fact]
    public void AdmitsANameWhileItsBasesNameFitsAnObjectName()
    {
        var store = new ChunkArchiveLogStore(_objects);

        Assert.True(store.AdmitsName("k/" + new string('a', 155)));
        Assert.False(store.AdmitsName("k/" + new string('a', 156)));
        Assert.Equal(ObjectName.MaxLength, ChunkArchiveLogStore.BaseName("k/" + new string('a', 155), ulong.MaxValue, ulong.MaxValue).Value.Length);
    }

    /// <summary>
    /// A log whose name extends another's, even by a generation's 20 digits, keeps its objects apart: each reads its own,
    /// the listing names both logs' bases and none of durable storage's objects, and deleting one log's generation leaves
    /// the other's.
    /// </summary>
    [Fact]
    public async Task NestedLogsKeepTheirObjectsApart()
    {
        var store = new ChunkArchiveLogStore(_objects);
        var outer = Name("k/a");
        var inner = Name("k/a/00000000000000000001");
        await CreateAsync(new ObjectName(ArchiveLogNames.StoreRecord), 1);
        await CreateAsync(new ObjectName(ArchiveLogNames.DrainMarker(new(3), new(0))), 2);
        Applied(await store.AppendAsync(outer, ArchiveLogPosition.Origin, [Bytes(1)], Token));
        Applied(await store.OpenAsync(inner, ArchiveLogPosition.Origin, Replace, Bytes(7), Token));
        Applied(await store.AppendAsync(inner, new(1, 2, 0, 0), [Bytes(8)], Token));
        Applied(await store.OpenAsync(outer, new(1, 0, 1, 1), Replace, Bytes(2), Token));

        await AssertReadsAsync(store, outer, new(2, 2, 0, 0), [2]);
        await AssertReadsAsync(store, inner, new(1, 2, 1, 1), [7, 8]);
        Assert.Equal(
            [("k/a/00000000000000000001", 1UL), ("k/a", 1UL), ("k/a", 2UL)],
            (await GenerationsAsync(store)).Select(static generation => (generation.Log, generation.Generation)));

        await store.DeleteGenerationAsync(outer.Value, 1, Token);
        await store.DeleteGenerationAsync(outer.Value, 1, Token);
        Assert.Equal(
            [("k/a/00000000000000000001", 1UL), ("k/a", 2UL)],
            (await GenerationsAsync(store)).Select(static generation => (generation.Log, generation.Generation)));
        Assert.Equal(
            [
                ChunkArchiveLogStore.ChunkName(inner.Value, 1, 0).Value,
                ChunkArchiveLogStore.BaseName(inner.Value, 1, inner.KeyHash).Value,
                ChunkArchiveLogStore.BaseName(outer.Value, 2, outer.KeyHash).Value,
            ],
            await NamesAsync(_objects));
        await AssertReadsAsync(store, inner, new(1, 2, 1, 1), [7, 8]);
        await AssertReadsAsync(store, outer, new(2, 2, 0, 0), [2]);
    }

    /// <summary>
    /// A straggler's write from a position the log has not reached creates nothing: an append on generation 2 while the
    /// log is at 1, and an opener from generation 1 while the log is absent, each fail their condition and leave the log
    /// as it was, so the opener of generation 2 later starts it empty. The <c>creates-before-listing</c> mutant creates the
    /// straggler's objects, which the log then reads as its own.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStragglerCreatesNothingInAGenerationTheLogHasNotReached(bool mutant)
    {
        var store = new ChunkArchiveLogStore(_objects) { CreatesBeforeListingMutant = mutant };
        var log = Name("k/straggler");
        var absent = Name("k/absent");
        Applied(await store.AppendAsync(log, ArchiveLogPosition.Origin, [Bytes(1)], Token));

        var append = (await store.AppendAsync(log, new(2, 2, 0, 0), [Bytes(5)], Token)).Value;
        var open = (await store.OpenAsync(absent, new(1, 0, 1, 1), Replace, Bytes(6), Token)).Value;
        Applied(await store.OpenAsync(log, new(1, 0, 1, 1), Replace, Bytes(2), Token));

        var position = (await store.GetPropertiesAsync(log, Token))!.Position;
        var created = await store.GetPropertiesAsync(absent, Token);
        var detected = append is not ArchiveLogWriteOutcome.ConditionFailed
            || open is not ArchiveLogWriteOutcome.ConditionFailed
            || position != new ArchiveLogPosition(2, 2, 0, 0)
            || created is not null;
        Assert.Equal(mutant, detected);
    }

    private static byte[] Chunk(params byte[] content)
    {
        using var writer = new ArcBufferWriter();
        ArchiveLogChunk.Encode(writer, new ReadOnlySequence<byte>(content));
        using var bytes = writer.ConsumeSlice(writer.Length);
        return bytes.ToArray();
    }

    private static async Task<string?> CorruptReasonAsync(ChunkArchiveLogStore store, ArchiveLogName log)
    {
        var corrupt = await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await store.GetPropertiesAsync(log, Token));
        _ = await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await store.ReadAsync(log, Token));
        return corrupt.InnerException is { } inner ? DecodeRejection.ReasonOf(inner) : null;
    }

    private static ArchiveLogName Name(string value) => new(value, ArchiveLogNames.KeyHash(System.Text.Encoding.UTF8.GetBytes(value)));

    private static ReadOnlySequence<byte> Bytes(params byte[] bytes) => new(bytes);

    private static void Applied(ArchiveLogWriteOutcome outcome) => Assert.IsType<ArchiveLogWriteOutcome.Applied>(outcome.Value);

    private static async Task AssertReadsAsync(ChunkArchiveLogStore store, ArchiveLogName log, ArchiveLogPosition position, byte[] content)
    {
        using var read = Assert.IsType<ArchiveLogRead.Read>((await store.ReadAsync(log, Token)).Value);
        Assert.Equal(position, read.Position);
        Assert.Equal(content, read.Content.ToArray());
    }

    private static async Task AssertCorruptAsync(ChunkArchiveLogStore store, ArchiveLogName log, string message)
    {
        Assert.Contains(message, (await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await store.ReadAsync(log, Token))).Message, StringComparison.Ordinal);
        Assert.Contains(message, (await Assert.ThrowsAsync<ArchiveLogCorruptException>(async () => await store.GetPropertiesAsync(log, Token))).Message, StringComparison.Ordinal);
    }

    private static async Task<List<ArchiveLogGeneration>> GenerationsAsync(ChunkArchiveLogStore store)
    {
        var generations = new List<ArchiveLogGeneration>();
        await foreach (var page in store.ListGenerationsAsync(null, Token))
        {
            generations.AddRange(page.Generations);
        }

        return generations;
    }

    private static async Task<List<string>> NamesAsync(IObjectStore objects)
    {
        var names = new List<string>();
        await foreach (var listed in objects.ListAsync(string.Empty, cancellationToken: Token))
        {
            if (!listed.Name.Value.StartsWith(ArchiveLogNames.ReservedPrefix, StringComparison.Ordinal))
            {
                names.Add(listed.Name.Value);
            }
        }

        return names;
    }

    private Task CreateAsync(ObjectName name, params byte[] content) => CreateAsync(_objects, name, content);

    private static async Task CreateAsync(IObjectStore objects, ObjectName name, byte[] content)
    {
        using var bytes = ArcBuffer.Wrap(content);
        _ = Assert.IsType<ObjectCreateOutcome.Created>((await objects.CreateAsync(name, bytes, null, Token)).Value);
    }

    /// <summary>An object store whose next listing misses the named object, as one created during the listing may be missed.</summary>
    private sealed class MissingObjectStore(IObjectStore inner) : IObjectStore
    {
        public ObjectName? MissOnce { get; set; }

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var missing = MissOnce;
            MissOnce = null;
            await foreach (var listed in inner.ListAsync(prefix, startAfter, cancellationToken))
            {
                if (listed.Name != missing)
                {
                    yield return listed;
                }
            }
        }

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }

    /// <summary>An object store whose named object reads as absent, as one the sweeper deleted after a listing named it.</summary>
    private sealed class VanishingObjectStore(IObjectStore inner) : IObjectStore
    {
        public ObjectName? Vanish { get; set; }

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            name == Vanish ? ValueTask.FromResult<ArcBuffer?>(null) : inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }
}

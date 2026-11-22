using System.Buffers;
using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// <see cref="LogArchiveStore"/> (archive-logs.md §2.2, §7.5, §8.4): a read's prefix, its position as its token, version
/// base, and coverage, <c>HasBase</c> from the opener's kind, the tombstone only while nothing followed it, <c>Stale</c> and
/// corruption as the log answers them, absence without I/O for a key the store cannot name, a generation 1 which no
/// write landed in read as absent; a base whose content differs from its record as corruption; and the store's record
/// <c>durable-storage/store</c>, whose vectors it writes and whose every field a process must match, which a mutant ignoring one
/// field fails.
/// </summary>
public sealed class LogArchiveStoreTests : IDisposable
{
    private static readonly byte[] Key = "store-key"u8.ToArray();

    private static readonly LedgerSegmentId Segment = new(new LedgerId(4), Slot.Zero);

    private static readonly ArchiveLogLayout Layout = new(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 4, 100, 50, MemoryArchiveLogStore.NameRule);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly List<ArchiveLogRequest> _requests = [];
    private readonly MemoryArchiveLogStore _logs;
    private readonly InMemoryObjectStore _objects = new();
    private readonly LogArchiveStore _store;
    private RowToken _token;

    public LogArchiveStoreTests()
    {
        _logs = new MemoryArchiveLogStore(Layout, request =>
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return ArchiveLogFault.None;
        });
        _store = new LogArchiveStore(_logs, _objects, ArchiveLogNames.Hexadecimal);
    }

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AnAbsentLogAnswersAbsent() =>
        Assert.Same(ArchiveStoreRead.Absent.Instance, (await _store.ReadAsync(Key, Map(), Token)).Value);

    /// <summary>A key whose log name the store cannot form has no rows, so its read answers <c>Absent</c> without I/O.</summary>
    [Fact]
    public async Task AKeyTheStoreCannotNameIsAbsentWithoutIo()
    {
        using var service = new FakeAzureBlobService();
        var store = new LogArchiveStore(new AzureArchiveLogStore(service.Client(), "json"), _objects, new ArchiveLogNames("test-1", static _ => "k/../x"));

        Assert.False(store.AdmitsKey(Key));
        Assert.Same(ArchiveStoreRead.Absent.Instance, (await store.ReadAsync(Key, Map(), Token)).Value);
        Assert.Empty(service.Records);
    }

    /// <summary>
    /// A key's first run whose create landed and whose append did not leaves generation 1 empty and with no opener, which
    /// holds no write: it reads as <c>Absent</c>, whose token is <c>Origin</c>, the token every key's first row carries, so
    /// a merge over it is no contradiction (archive-logs.md §2.2, §8.4).
    /// </summary>
    [Fact]
    public async Task AFirstGenerationWhichNoWriteLandedInIsAbsent()
    {
        var faults = new Queue<ArchiveLogFault>([ArchiveLogFault.None, ArchiveLogFault.FailBefore]);
        var logs = new MemoryArchiveLogStore(Layout, _ => faults.TryDequeue(out var fault) ? fault : ArchiveLogFault.None);
        var store = new LogArchiveStore(logs, _objects, ArchiveLogNames.Hexadecimal);
        var write = new ArchiveWrite(Segment, default, [ArchiveCommand.Append(1)]);
        var writer = new ArchiveLogWriter(logs, new FakeTimeProvider(), new ArchiveLogWriterOptions { MaxRetries = 0 });

        await Assert.ThrowsAsync<IOException>(async () => await writer.WriteAsync(ArchiveLogNames.Hexadecimal.Of(Key), write, new ReadOnlySequence<byte>(new byte[1]), Token));

        Assert.Equal(new ArchiveLogPosition(1, 0, 0, 0), (await logs.GetPropertiesAsync(ArchiveLogNames.Hexadecimal.Of(Key), Token))?.Position);
        Assert.Same(ArchiveStoreRead.Absent.Instance, (await store.ReadAsync(Key, Map(), Token)).Value);

        // The merge of that absence with the key's live first run is no contradiction: the run is not held, and folds whole.
        using var body = ArcBuffer.Wrap(new byte[1]);
        using var row = ArchiveRow.Live(new Tag(Segment.Ledger, Slot.Zero, 0), RowKind.Append, default(RowVersion).Next(), Segment, default, body);
        using var merged = Assert.IsType<ArchiveRead>(ArchiveMerge.Merge(store, null, [row], new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false).Value);
        Assert.False(merged.Rows.ToArray().Single().IsHeld);
    }

    /// <summary>An empty <c>Delete</c> from <c>Origin</c> also leaves <c>(1, 0, 0)</c>, but with an opener: it reads as an empty prefix at that token.</summary>
    [Fact]
    public async Task AnOpenedEmptyFirstGenerationIsAPrefix()
    {
        await WriteAsync((RowKind.Delete, string.Empty));

        using var read = await ReadAsync();
        Assert.Equal(ArchiveLogTokens.Encode(new ArchiveLogPosition(1, 1, 0, 0)), read.Prefix.Token);
    }

    /// <summary>
    /// A base whose bytes differ from the MD5 its generation records, or a second opener from the same token whose base
    /// differs from the stored one, which no write from one token makes, is corruption (archive-logs.md §3).
    /// </summary>
    [Fact]
    public async Task ABaseWhichDiffersFromItsRecordIsCorrupt()
    {
        await WriteAsync((RowKind.Replace, "abc"));
        var name = ArchiveLogNames.Hexadecimal.Of(Key);
        var writer = new ArchiveLogWriter(_logs, new FakeTimeProvider());

        var other = new ArchiveWrite(Segment, default, [ArchiveCommand.Replace(3)]);
        await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await writer.WriteAsync(name, other, new ReadOnlySequence<byte>("abd"u8.ToArray()), Token));

        _logs.SetBase(name.Value, 1, "abd"u8.ToArray());
        await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await _store.ReadAsync(Key, Map(), Token));
    }

    /// <summary>
    /// The prefix's token and version base are the encoding of the log's position, and its coverage the position itself; a
    /// key's first run reads as its appends with no base.
    /// </summary>
    [Fact]
    public async Task APrefixsTokenAndVersionBaseAreItsPosition()
    {
        await WriteAsync((RowKind.Append, "ab"), (RowKind.Append, "cde"));

        using var read = await ReadAsync();
        var position = new ArchiveLogPosition(1, 0, 5, 2);
        Assert.Equal(ArchiveLogTokens.Encode(position), read.Prefix.Token);
        Assert.Equal(read.Prefix.Token, read.Prefix.VersionBase);
        Assert.Equal(position, read.Prefix.Coverage);
        Assert.Equal("abcde"u8.ToArray(), read.Prefix.Content.ToArray());
        Assert.False(read.Prefix.HasBase);
        Assert.Null(read.Prefix.Tombstone);
    }

    /// <summary>
    /// <c>HasBase</c> is the current generation's opener's kind, an empty <c>Replace</c> included; a tombstoned
    /// <c>Delete</c> reads with its tombstone until an <c>Append</c> follows it; and an empty <c>Delete</c>, Journals'
    /// <c>Expired</c>, reads as an empty prefix with no base and no tombstone.
    /// </summary>
    [Fact]
    public async Task HasBaseAndTheTombstoneComeFromTheOpener()
    {
        await WriteAsync((RowKind.Append, "a"));
        await WriteAsync((RowKind.Replace, string.Empty));
        Assert.Equal((true, false, ""), await PrefixAsync());

        await WriteAsync((RowKind.Delete, "tomb0001"));
        Assert.Equal((false, true, ""), await PrefixAsync());

        await WriteAsync((RowKind.Append, "b"));
        Assert.Equal((false, false, "b"), await PrefixAsync());

        await WriteAsync((RowKind.Delete, "tomb0002"), (RowKind.Append, "c"));
        Assert.Equal((false, false, "c"), await PrefixAsync());

        await WriteAsync((RowKind.Delete, string.Empty));
        Assert.Equal((false, false, ""), await PrefixAsync());

        await WriteAsync((RowKind.Replace, "d"), (RowKind.Append, "e"));
        Assert.Equal((true, false, "de"), await PrefixAsync());
    }

    /// <summary>A read whose base the sweeper deleted under it answers <c>Stale</c>, and the reader reads again.</summary>
    [Fact]
    public async Task AReadWhoseBaseIsGoneIsStale()
    {
        await WriteAsync((RowKind.Replace, "a"));
        await _logs.DeleteGenerationAsync(ArchiveLogNames.Hexadecimal.Of(Key).Value, 1, Token);

        Assert.Same(ArchiveStoreRead.Stale.Instance, (await _store.ReadAsync(Key, Map(), Token)).Value);
    }

    [Fact]
    public async Task ALogWhichNamesAnotherKeyIsCorrupt()
    {
        await WriteAsync((RowKind.Append, "a"));
        _logs.SetKeyHash(ArchiveLogNames.Hexadecimal.Of(Key).Value, ArchiveLogNames.KeyHash(Key) ^ 1);

        await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await _store.ReadAsync(Key, Map(), Token));
    }

    [Fact]
    public async Task HoldsRefusesAnotherStoresPrefix()
    {
        using var content = ArcBuffer.Wrap(new byte[1]);
        using var prefix = new ArchivePrefix(default, new RowToken([1]), false, null, content, coverage: null);

        Assert.Throws<ArgumentException>(() => _store.Holds(prefix, new ArchiveWrite(Segment, default, [ArchiveCommand.Append(1)]), 1));
        await Task.CompletedTask;
    }

    /// <summary>The store's record of archive logs on Azure is the vector's, field for field.</summary>
    [Theory]
    [InlineData("archive logs on Azure")]
    [InlineData("archive logs' memory store")]
    public async Task TheStoresRecordIsItsVectors(string name)
    {
        var vector = SharedVectors.Cases("archive", "store-records.json").Single(element => SharedVectors.Name(element) == name);
        var expected = Assert.IsType<ArchiveStoreRecord>(ArchiveStoreRecord.Decode(SharedVectors.Hex(vector)).Value);
        var layout = name.Contains("Azure", StringComparison.Ordinal) ? AzureArchiveLogStore.AzureLayout : MemoryArchiveLogStore.DefaultLayout;
        var store = new LogArchiveStore(new MemoryArchiveLogStore(layout), _objects, ArchiveLogNames.Hexadecimal);

        var id = await store.CheckedIdAsync(expected.Parameters, Token);

        var versioned = await _objects.ReadVersionedAsync(new ObjectName(ArchiveLogNames.StoreRecord), Token);
        using var content = versioned!.Value.Content;
        var written = Assert.IsType<ArchiveStoreRecord>(ArchiveStoreRecord.Decode(content.ToArray()).Value);
        Assert.Equal(id, written.Id);
        Assert.Null(ArchiveStoreRecord.FirstDifference(expected, written));
    }

    /// <summary>
    /// A process whose layout differs from the store's record in any one field is refused, naming it (archive.md §2.4,
    /// HI-I3); a mutant which ignores one field lets a process with another value of it through.
    /// </summary>
    [Theory]
    [InlineData("layout")]
    [InlineData("max-append-bytes")]
    [InlineData("max-units")]
    [InlineData("replace-request-units")]
    [InlineData("name-rule")]
    [InlineData("name-scheme")]
    public async Task ARecordWhoseFieldDiffersIsRefusedUnlikeTheMutant(string field)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["format"] = "json" };
        _ = await Store(AzureArchiveLogStore.AzureLayout).CheckedIdAsync(parameters, Token);
        var (blocks, units, replace, rule) = (AzureArchiveLogStore.MaxBlockBytes, AzureArchiveLogStore.MaxBlocks, AzureArchiveLogStore.ReplaceRequestBlocks, AzureArchiveLogMetadata.NameRule);
        var other = field switch
        {
            "layout" => new ArchiveLogLayout("memory", ArchiveLogPacking.Blocks, blocks, units, replace, rule),
            "max-append-bytes" => new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 1 << 20, units, replace, rule),
            "max-units" => new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, blocks, 50_000, replace, rule),
            "replace-request-units" => new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, blocks, units, 30_000, rule),
            "name-rule" => new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, blocks, units, replace, rule + "-other"),
            _ => AzureArchiveLogStore.AzureLayout,
        };
        var names = field == "name-scheme" ? new ArchiveLogNames("hex-2", static key => "k/" + Convert.ToHexStringLower(key)) : ArchiveLogNames.Hexadecimal;

        var mismatch = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await new LogArchiveStore(new MemoryArchiveLogStore(other), _objects, names).CheckedIdAsync(parameters, Token));
        Assert.Equal((field, false), (mismatch.Field, mismatch.IsParameter));
        if (field != ArchiveStoreRecord.LayoutField)
        {
            var mutant = new LogArchiveStore(new MemoryArchiveLogStore(other), _objects, names) { IgnoredRecordField = field };
            _ = await mutant.CheckedIdAsync(parameters, Token);
        }
    }

    /// <summary>
    /// A reader whose tail lags inside a segment the store holds whole sees only the rows before its tail, and forms its
    /// write from them: <c>Holds</c> holds that write, since the log is past its opener, but <c>Plan</c> of it is not the
    /// prefix's token, which counts every row the drained write folded, so the merge answers <c>PastEnd</c> rather than
    /// the prefix's newer content as the history through the reader's tail (archive-logs.md §7.2, archive.md §2.1). A
    /// reader which saw the whole segment merges it as held. The cases: an opener and an append after it; a <c>Delete</c>
    /// and an append; and two openers, the reader stopping before the second, whose base is as long as the first's with
    /// its append, which a token of base lengths alone would confuse.
    /// </summary>
    [Theory]
    [InlineData("R:abc A:d", 1)]
    [InlineData("E: A:d", 1)]
    [InlineData("R:a A:bc R:xyz", 2)]
    [InlineData("R:a A:bc R:xyz A:de", 2)]
    public async Task AReaderWhoseTailLagsInsideAHeldSegmentReadsAgain(string segment, int seen)
    {
        var rows = segment.Split(' ').Select(static row => (Kind: row[0] switch { 'R' => RowKind.Replace, 'E' => RowKind.Delete, _ => RowKind.Append }, Body: row[2..])).ToArray();
        await WriteAsync(rows);
        using var read = await ReadAsync();
        var live = new List<ArchiveRow>();
        try
        {
            var version = default(RowVersion);
            for (var index = 0; index < rows.Length; index++)
            {
                version = version.Next();
                var body = rows[index].Kind == RowKind.Delete ? new byte[8] : System.Text.Encoding.UTF8.GetBytes(rows[index].Body);
                using var bytes = ArcBuffer.Wrap(body);
                live.Add(ArchiveRow.Live(new Tag(Segment.Ledger, new Slot(index), 0), rows[index].Kind, version, Segment, default, bytes));
            }

            Assert.Same(ArchiveMergeOutcome.PastEnd.Instance, ArchiveMerge.Merge(_store, read.Prefix, live.ToArray().AsSpan(0, seen), new Dictionary<LedgerId, Slot>(), openLedger: Segment.Ledger, linearizable: false).Value);
            using var whole = Assert.IsType<ArchiveRead>(ArchiveMerge.Merge(_store, read.Prefix, [.. live], new Dictionary<LedgerId, Slot>(), openLedger: Segment.Ledger, linearizable: false).Value);
            Assert.All(whole.Rows.ToArray(), static row => Assert.True(row.IsHeld));
        }
        finally
        {
            ArchiveMerge.Release([.. live]);
        }
    }

    /// <summary>
    /// The memory store's default layout is Azure's, field for field, so tests over it pack and admit as production does;
    /// only the name rule, which names no URI, differs.
    /// </summary>
    [Fact]
    public void TheMemoryStoresDefaultLayoutIsAzures()
    {
        var (memory, azure) = (MemoryArchiveLogStore.DefaultLayout, AzureArchiveLogStore.AzureLayout);

        Assert.Equal(
            (azure.Packing, azure.MaxAppendBytes, azure.MaxUnits, azure.ReplaceRequestUnits),
            (memory.Packing, memory.MaxAppendBytes, memory.MaxUnits, memory.ReplaceRequestUnits));
        Assert.Equal(MemoryArchiveLogStore.NameRule, memory.NameRule);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static LedgerMapValue Map() =>
        new(1, new LedgerId(100), 0, []);

    private LogArchiveStore Store(ArchiveLogLayout layout) => new(new MemoryArchiveLogStore(layout), _objects, ArchiveLogNames.Hexadecimal);

    /// <summary>Writes the key's next write of <paramref name="rows"/> through the writer, from the key's last token.</summary>
    private async Task WriteAsync(params (RowKind Kind, string Body)[] rows)
    {
        var start = 0;
        for (var index = rows.Length - 1; index >= 0; index--)
        {
            if (rows[index].Kind != RowKind.Append)
            {
                start = index;
                break;
            }
        }

        var commands = rows[start..].Select(static row => ArchiveCommand.Of(row.Kind, System.Text.Encoding.UTF8.GetBytes(row.Body))).ToImmutableArray();
        var write = new ArchiveWrite(Segment, _token, commands, start);
        var content = rows[start..].Where(static row => row.Kind != RowKind.Delete).SelectMany(static row => System.Text.Encoding.UTF8.GetBytes(row.Body)).ToArray();
        var verdict = await new ArchiveLogWriter(_logs, new FakeTimeProvider()).WriteAsync(ArchiveLogNames.Hexadecimal.Of(Key), write, new ReadOnlySequence<byte>(content), Token);
        Assert.IsType<ArchiveLogConditionOutcome.Held>(verdict.Value);
        _token = _store.Plan(write);
    }

    private async Task<ArchiveStoreRead.Read> ReadAsync() =>
        Assert.IsType<ArchiveStoreRead.Read>((await _store.ReadAsync(Key, Map(), Token)).Value);

    private async Task<(bool HasBase, bool Tombstone, string Content)> PrefixAsync()
    {
        using var read = await ReadAsync();
        Assert.Equal(_token, read.Prefix.Token);
        return (read.Prefix.HasBase, read.Prefix.Tombstone is not null, System.Text.Encoding.UTF8.GetString(read.Prefix.Content.ToArray()));
    }
}

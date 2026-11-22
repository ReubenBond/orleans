using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The history reader over a store whose only token is empty and whose coverage is the read's map value, the LSM and
/// archive logs (archive.md §8.1, archive-lsm.md §2.8): a key's rows across a ledger drained and dropped, one drained
/// and still listed, and the open one fold to its history, the dropped ledger's rows in the prefix and every listed
/// ledger's scanned from slot 0, not held; and the versions of the rows a read scans continue across ledgers (HI-I4).
/// </summary>
/// <remarks>
/// Each store's suite derives from it, giving the store and its drain of a closed ledger's rows, which completes the
/// ledger; the suite drops a ledger through the map's committer.
/// </remarks>
public abstract class ArchiveReaderContract : IAsyncLifetime
{
    private static readonly byte[] Key = "reader-key"u8.ToArray();

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private LiveLedgers? _ledgers;

    /// <summary>Gets the store under test.</summary>
    protected abstract IArchiveStore Store { get; }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private LiveLedgers Ledgers => _ledgers ?? throw new InvalidOperationException("The suite has not started.");

    public async ValueTask InitializeAsync()
    {
        _ledgers = await LiveLedgers.StartAsync(NewRegister(), TimeProvider.System, Token);
        await _ledgers.BootstrapAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ledgers is not null)
        {
            await _ledgers.DisposeAsync();
        }

        await DisposeStoreAsync();
        _leaks.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A key's rows in a dropped ledger, a drained ledger still listed, and the open ledger: the read holds the dropped
    /// ledger's in its prefix, scans the others from slot 0, none held, and folds to the key's history; its rows pass
    /// the chain checker, and it is linearizable in the writer's process.
    /// </summary>
    [Fact]
    public async Task AReadFoldsTheDroppedLedgersPrefixAndEveryListedLedgersRows()
    {
        var first = await ProposeAsync(RowKind.Append, "a", RowKind.Replace, "b");
        await CloseAndLandAsync(first);
        var second = await ProposeAsync(RowKind.Append, "c");
        await CloseAndLandAsync(second);
        await DropAsync(first);
        _ = await ProposeAsync(RowKind.Append, "d");

        var reader = new ArchiveReader(Ledgers.Reader, Store, TimeProvider.System);
        using var read = await reader.ReadAsync(Key, Token);

        Assert.Equal("bcd", System.Text.Encoding.UTF8.GetString(ArchiveOracle.Fold(read.Prefix, read.Rows).Content));
        Assert.Equal("b", System.Text.Encoding.UTF8.GetString(read.Prefix!.Content.ToArray()));
        Assert.Equal(["c", "d"], read.Rows.ToArray().Select(static row => System.Text.Encoding.UTF8.GetString(row.Body.ToArray())));
        Assert.All(read.Rows.ToArray(), static row => Assert.False(row.IsHeld));
        Assert.Equal([second, Ledgers.OpenContaining(Key).Id], read.Through.Keys.Order());
        Assert.Null(TokenChainChecker.Check(Store, read.Prefix.Token, ChainRow.Of(read.Rows)));
        Assert.True(read.Linearizable);
    }

    /// <summary>
    /// A reader whose map value is older than a ledger's drop scans the dropped ledger, meets its dropped replica, and
    /// reads again with a fresh map, which the store's prefix then covers (HI-I1); outside the writer's process it reads
    /// the open ledger below a contiguous tail, which may lag the writer's last row.
    /// </summary>
    [Fact]
    public async Task AReadOnAMapFromBeforeADropReadsAgainAndFindsEveryRow()
    {
        var first = await ProposeAsync(RowKind.Append, "a");
        await CloseAndLandAsync(first);
        _ = await ProposeAsync(RowKind.Append, "b");
        var view = await Ledgers.OpenViewAsync(Token);
        await using var stale = Ledgers.OpenReader(view);
        await DropAsync(first);
        await Ledgers.DropReplicaAsync(first, Token);

        var reader = new ArchiveReader(stale, Store, TimeProvider.System);
        using var read = await reader.ReadAsync(Key, Token);

        Assert.Equal("a", System.Text.Encoding.UTF8.GetString(read.Prefix!.Content.ToArray()));
        Assert.Equal(read.Rows.Length == 0 ? "a" : "ab", System.Text.Encoding.UTF8.GetString(ArchiveOracle.Fold(read.Prefix, read.Rows).Content));
        Assert.False(read.Through.ContainsKey(first));
        Assert.False(read.Linearizable);
    }

    /// <summary>
    /// A read when only the open ledger exists (no closed predecessors) uses the single-ledger fast path and returns the
    /// open ledger's rows in order (archive.md §8.1).
    /// </summary>
    [Fact]
    public async Task AReadWithOnlyAnOpenLedgerReturnsItsRows()
    {
        var open = await ProposeAsync(RowKind.Append, "x", RowKind.Append, "y");

        var reader = new ArchiveReader(Ledgers.Reader, Store, TimeProvider.System);
        using var read = await reader.ReadAsync(Key, Token);

        Assert.Null(read.Prefix);
        Assert.Equal("xy", System.Text.Encoding.UTF8.GetString(ArchiveOracle.Fold(read.Prefix, read.Rows).Content));
        Assert.Equal([open], read.Through.Keys.Order());
        Assert.True(read.Linearizable);
    }

    /// <summary>
    /// <see cref="ArchiveReader.ReadStoreAsync"/> returns the store's prefix for a drained-and-dropped ledger paired with
    /// the map value read before it, and returns <see langword="null"/> for an absent key (archive.md §2.2, §8.2).
    /// </summary>
    [Fact]
    public async Task ReadStoreReturnsDroppedPrefixAndNullForAbsentKey()
    {
        var first = await ProposeAsync(RowKind.Append, "a", RowKind.Replace, "b");
        await CloseAndLandAsync(first);
        await DropAsync(first);

        var reader = new ArchiveReader(Ledgers.Reader, Store, TimeProvider.System);
        using var present = await reader.ReadStoreAsync(Key, Token);
        Assert.NotNull(present.Prefix);
        Assert.Equal("b", System.Text.Encoding.UTF8.GetString(present.Prefix.Content.ToArray()));
        Assert.False(present.Map.Get(first).HasValue);

        using var absent = await reader.ReadStoreAsync("absent-key"u8.ToArray(), Token);
        Assert.Null(absent.Prefix);
    }

    /// <summary>Returns the register of the suite's ledger map.</summary>
    protected abstract IConditionalRegister NewRegister();

    /// <summary>
    /// Lands <paramref name="rows"/>, every row of the closed <paramref name="ledger"/>, which ends at <paramref name="end"/>,
    /// in tag order, as the store's drain would, and completes the ledger. The drain borrows the rows.
    /// </summary>
    protected abstract Task LandAsync(LedgerId ledger, Slot end, IReadOnlyList<TaggedRow> rows, CancellationToken cancellationToken);

    /// <summary>Releases the store.</summary>
    protected abstract ValueTask DisposeStoreAsync();

    /// <summary>Proposes one batch of the key's rows, each a kind and a payload, to the open ledger; returns the ledger.</summary>
    private async Task<LedgerId> ProposeAsync(params object[] rows)
    {
        var ledger = Ledgers.OpenContaining(Key).Id;
        _ = await Ledgers.ProposeAsync(ledger, [.. rows.Chunk(2).Select(static row => LiveRow.Of("reader-key", (RowKind)row[0], (string)row[1]))], Token);
        return ledger;
    }

    /// <summary>Closes <paramref name="ledger"/>, the open one, and lands its rows in the store, completing it.</summary>
    private async Task CloseAndLandAsync(LedgerId ledger)
    {
        await Ledgers.ReplaceAsync(ledger, Token);
        var closed = Ledgers.Ledger(ledger);
        var end = Assert.IsType<LedgerStatus.Closed>(closed.Status.Value).T;
        var rows = new List<TaggedRow>();
        try
        {
            using var view = new LedgerSegmentView(ledger);
            await foreach (var item in Ledgers.Reader.ScanAsync(closed, Slot.Zero, end, view, cancellationToken: Token))
            {
                Assert.True(item.Read.TryGetValue(out LedgerRead.Data data), $"A drain's scan read {item.Read}.");
                using (data)
                {
                    foreach (var row in item.Rows)
                    {
                        rows.Add(new TaggedRow(row.Tag, row.ToRow()));
                    }
                }
            }

            await LandAsync(ledger, end, rows, Token);
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Row.Dispose();
            }
        }
    }

    private async Task DropAsync(LedgerId ledger)
    {
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await Ledgers.CommitAsync(new LedgerMapChange.Drop(ledger), Token)).Value);
        _ = await Ledgers.Map.ReadAsync(Token);
    }
}

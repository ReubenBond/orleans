using System.Text;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// One node's process over live ledgers and a memory archive store, with its <see cref="JournalStore"/>: the node is
/// every ledger's replica and primary, so the store serves every open ledger's writer. A test writes and reads journals
/// through the store, replaces and splits ledgers through the Ledgers layer's own transitions, drains a closed ledger's
/// segments into the memory store, which checks each key's token chain and its limits (HI-I4), and drops it.
/// </summary>
internal sealed class JournalCluster : IAsyncDisposable
{
    private JournalCluster(LiveLedgers ledgers, InMemoryArchiveStore memory, JournalStoreOptions options, TimeProvider clock, Func<Task>? beforeFollowing)
    {
        Ledgers = ledgers;
        Memory = memory;
        Store = new GatedArchiveStore(memory);
        Journals = new JournalStore(ledgers.Registry, Store, options, clock, beforeFollowing);
    }

    public LiveLedgers Ledgers { get; }

    public InMemoryArchiveStore Memory { get; }

    public GatedArchiveStore Store { get; }

    public JournalStore Journals { get; }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Starts the process, with its map bootstrapped over the whole keyspace unless <paramref name="bootstrap"/> is false,
    /// and its store's follower starting once <paramref name="beforeFollowing"/> completes, if given.
    /// </summary>
    public static async Task<JournalCluster> StartAsync(JournalStoreOptions? options = null, InMemoryArchiveStore? memory = null, TimeProvider? clock = null, bool bootstrap = true, Func<Task>? beforeFollowing = null)
    {
        var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        if (bootstrap)
        {
            await ledgers.BootstrapAsync(Token);
        }

        return new JournalCluster(ledgers, memory ?? new InMemoryArchiveStore(), options ?? new JournalStoreOptions(), clock ?? TimeProvider.System, beforeFollowing);
    }

    /// <summary>Returns the open ledger whose range holds the journal <paramref name="key"/>.</summary>
    public LedgerId OpenLedger(string key) => Ledgers.OpenContaining(JournalRowKey.Encode(key).Span.ToArray()).Id;

    /// <summary>Closes the open ledger over <paramref name="key"/> and opens its successor over its range; returns the closed ledger.</summary>
    public async Task<LedgerId> ReplaceAsync(string key)
    {
        var ledger = OpenLedger(key);
        await Ledgers.ReplaceAsync(ledger, Token);
        return ledger;
    }

    /// <summary>Closes the open ledger over <paramref name="key"/> and opens its two children; returns the closed ledger.</summary>
    public async Task<LedgerId> SplitAsync(string key)
    {
        var ledger = OpenLedger(key);
        await Ledgers.SplitAsync(ledger, Token);
        return ledger;
    }

    /// <summary>Drains every segment of the closed <paramref name="ledger"/> into the memory store, each whole, in order.</summary>
    public async Task DrainAsync(LedgerId ledger)
    {
        var end = Assert.IsType<LedgerStatus.Closed>(Ledgers.Ledger(ledger).Status.Value).T;
        var segments = await ScanSegmentsAsync(ledger, end);
        try
        {
            foreach (var segment in segments)
            {
                Drain(segment);
            }
        }
        finally
        {
            foreach (var segment in segments)
            {
                KeyedRow.Release(segment);
            }
        }
    }

    /// <summary>
    /// Scans the listed <paramref name="ledger"/> below <paramref name="end"/>, its <c>T</c> or a contiguous tail its open
    /// check confirmed: its rows by segment, in order, each list owned by the caller, which releases it.
    /// </summary>
    public async Task<List<List<KeyedRow>>> ScanSegmentsAsync(LedgerId ledger, Slot end)
    {
        var segments = new List<List<KeyedRow>>();
        using var bodies = new ArcBufferWriter();
        using var view = new LedgerSegmentView(ledger);
        LedgerSegmentId? segment = null;
        try
        {
            await foreach (var item in Ledgers.Reader.ScanAsync(Ledgers.Ledger(ledger), Slot.Zero, end, view, cancellationToken: Token))
            {
                Assert.True(item.Read.TryGetValue(out LedgerRead.Data data), $"A scan read {item.Read}.");
                using (data)
                {
                    foreach (var row in item.Rows)
                    {
                        if (segment != view.Segment)
                        {
                            segments.Add([]);
                            segment = view.Segment;
                        }

                        segments[^1].Add(new KeyedRow(row.Key.ToArray(), ArchiveRow.Of(row, view.Segment, bodies)));
                    }
                }
            }
        }
        catch
        {
            foreach (var rows in segments)
            {
                KeyedRow.Release(rows);
            }

            throw;
        }

        return segments;
    }

    /// <summary>Drains one whole segment's rows by key into the memory store; the caller keeps and releases the rows.</summary>
    public void Drain(List<KeyedRow> segment) => KeyedRow.DrainByKey([.. segment], (key, keyRows) => Memory.Drain(key, keyRows));
    /// <summary>Drops the drained, closed <paramref name="ledger"/> from the map and from its replica.</summary>
    public async Task DropAsync(LedgerId ledger)
    {
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await Ledgers.CommitAsync(new LedgerMapChange.Drop(ledger), Token)).Value);
        _ = await Ledgers.Map.ReadAsync(Token);
        _ = await Ledgers.Reconciler(Ledgers.Map).ReconcileAsync(Token);
    }

    /// <summary>Reads the journal <paramref name="key"/> through the store, which must serve it.</summary>
    public async Task<(JournalVersion Version, bool Exists, string Content)> ReadAsync(string key)
    {
        using var result = Assert.IsType<JournalReadResult>((await Journals.ReadAsync(JournalRowKey.Encode(key), Token)).Value);
        var content = await ContentAsync(result);
        Assert.Equal(result.ContentLength, Encoding.UTF8.GetByteCount(content));
        return (result.CurrentVersion, result.Exists, content);
    }

    /// <summary>Returns a read's content as UTF-8, disposing each chunk it yields.</summary>
    public static async Task<string> ContentAsync(JournalReadResult result)
    {
        var text = new StringBuilder();
        await foreach (var chunk in result.ReadPayloadsAsync(Token))
        {
            using (chunk)
            {
                _ = text.Append(Encoding.UTF8.GetString(chunk.ToArray()));
            }
        }

        return text.ToString();
    }

    /// <summary>Encodes an <c>Append</c> of <paramref name="text"/> through the store and writes it.</summary>
    public async Task<JournalWriteOutcome> AppendAsync(string key, JournalVersion expected, string text)
    {
        using var payload = text.Length == 0 ? default : ArcBuffer.Wrap(Encoding.UTF8.GetBytes(text));
        using var record = Journals.EncodeAppend(JournalRowKey.Encode(key), expected, payload);
        return await Journals.WriteAsync(record, Token);
    }

    /// <summary>Encodes a <c>Replace</c> of <paramref name="text"/> through the store and writes it.</summary>
    public async Task<JournalWriteOutcome> ReplaceAsync(string key, JournalVersion expected, string text)
    {
        using var payload = text.Length == 0 ? default : ArcBuffer.Wrap(Encoding.UTF8.GetBytes(text));
        using var record = Journals.EncodeReplace(JournalRowKey.Encode(key), expected, payload);
        return await Journals.WriteAsync(record, Token);
    }

    /// <summary>Encodes a <c>Delete</c> through the store, stamped from its clock, and writes it.</summary>
    public async Task<JournalWriteOutcome> DeleteAsync(string key, JournalVersion expected)
    {
        using var record = Journals.EncodeDelete(JournalRowKey.Encode(key), expected);
        return await Journals.WriteAsync(record, Token);
    }

    /// <summary>Waits until the process's writer registry has reconciled with its map view as it is now.</summary>
    public Task ReconciledAsync() => Ledgers.Registry.ReconciledAsync(Ledgers.Map.Current.Epoch, Token);

    public async ValueTask DisposeAsync()
    {
        await Journals.DisposeAsync();
        await Ledgers.DisposeAsync();
        Memory.Dispose();
    }
}

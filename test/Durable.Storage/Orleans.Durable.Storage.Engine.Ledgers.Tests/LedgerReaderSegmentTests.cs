using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// The reader's scans over segments (ledgers.md §2.5, §8.4, §8.5, LG-I12, LG-I13): a scan from any slot rewinds to its
/// segment's start and resolves every row from that slot on, of every key or of one, to the version its writer gave it,
/// also once a recovery ended the ledger mid-segment; a lost decided batch leaves the rest of its segment unresolvable,
/// and the segments after it resolve.
/// </summary>
public sealed class LedgerReaderSegmentTests : IDisposable
{
    private static readonly string[] Script = LedgerSegmentScript.Script;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// ledgers.md §2.5, LG-I13: a scan from every slot of a ledger a recovery closed mid-segment, of every key and of each
    /// key alone, returns each batch from that slot on and resolves each of its rows to the version and token its writer's
    /// <see cref="LedgerBatch.BeginRow"/> returned and to the token its key's defining row carried.
    /// </summary>
    [Fact]
    public async Task AScanFromEverySlotRewindsToItsSegmentStartAndResolvesEachRow()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var (map, closed, expected) = await WriteAndRecoverAsync(transport, token);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var end = closed.Status.Value is LedgerStatus.Closed { T: var t } ? t : throw new InvalidOperationException();
        Assert.Equal(new Slot(Script.Length), end);

        for (var from = 0L; from <= end.Value; from++)
        {
            foreach (var key in (string[])["", "a", "b", "c", "d", "e", "f"])
            {
                var (slots, rows) = await ScanAsync(reader, closed, new Slot(from), end, key, token);
                Assert.Equal(Enumerable.Range((int)from, (int)(end.Value - from)).Select(static slot => (long)slot), slots);
                Assert.Equal(expected.Where(row => row.Slot >= from && (key.Length == 0 || row.Key == key[0])), rows);
            }
        }
    }

    /// <summary>
    /// LG-I12, ledgers.md §2.5: a decided batch every replica lost leaves the rest of its segment unresolvable: a scan from
    /// it or from a later slot of its segment, which rewinds through it, fails as corruption, as does one which reaches it;
    /// a scan from the next segment's start resolves.
    /// </summary>
    [Fact]
    public async Task ALostDecidedBatchLeavesTheRestOfItsSegmentUnresolvable()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var (map, closed, expected) = await WriteAndRecoverAsync(transport, token);
        var end = new Slot(Script.Length);
        var lost = new Slot(4);
        await using var reader = new LedgerReader(map.View, new LosingTransport(transport, lost), new FakeTimeProvider(), localNode: null);

        foreach (var from in (long[])[0, 2, 4, 5])
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => ScanAsync(reader, closed, new Slot(from), end, "", token));
            Assert.Contains("slot 4", failure.Message, StringComparison.Ordinal);
        }

        var (slots, rows) = await ScanAsync(reader, closed, new Slot(6), end, "", token);
        Assert.Equal(Enumerable.Range(6, Script.Length - 6).Select(static slot => (long)slot), slots);
        Assert.Equal(expected.Where(static row => row.Slot >= 6), rows);
    }

    /// <summary>
    /// ledgers.md §2.5: an item's rows are lent only until the scan advances: an item kept past the next one, or past the
    /// scan's end, throws <see cref="InvalidOperationException"/> rather than lending another batch's rows or none.
    /// </summary>
    [Fact]
    public async Task AnItemKeptPastTheScansNextStepIsStale()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var (map, closed, _) = await WriteAndRecoverAsync(transport, token);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        LedgerScanItem? kept = null;
        var count = 0;
        await foreach (var item in reader.ScanAsync(closed, Slot.Zero, new Slot(3), token))
        {
            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
            using (data)
            {
                Assert.InRange(CountRows(item), 1, 2);
                if (kept is { } previous)
                {
                    _ = Assert.Throws<InvalidOperationException>(() => CountRows(previous));
                }

                kept = item;
                count++;
            }
        }

        Assert.Equal(3, count);
        _ = Assert.Throws<InvalidOperationException>(() => CountRows(kept!.Value));
    }

    /// <summary>
    /// ledgers.md §2.5: a scan whose caller stops after its first item releases every batch it read ahead, which the page
    /// leak check confirms.
    /// </summary>
    [Fact]
    public async Task AScanAbandonedEarlyReleasesWhatItReadAhead()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var (map, closed, _) = await WriteAndRecoverAsync(transport, token);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        await foreach (var item in reader.ScanAsync(closed, new Slot(5), new Slot(Script.Length), token))
        {
            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
            data.Dispose();
            break;
        }
    }

    /// <summary>
    /// ledgers.md §2.5: a scan whose caller does not advance issues no read while the replies it holds and has not
    /// yielded reach <c>MaxReadAheadBytes</c>, here two and a half batches, below what the window would let it read.
    /// </summary>
    [Fact]
    public async Task AScanStopsReadingAheadOnceTheRepliesItHoldsReachTheBound()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var (map, closed, _) = await WriteAndRecoverAsync(transport, token);
        // About two and a half of the script's one-row batches, which are 29 bytes or more each; the window would hold four.
        var bound = 80;
        var held = new List<long>();
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null)
        {
            MaxReadAheadBytes = bound,
            ReadIssued = bytes => held.Add(bytes),
        };
        var seen = 0;
        await foreach (var item in reader.ScanAsync(closed, Slot.Zero, new Slot(Script.Length), token))
        {
            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
            Assert.True(data.Batch.Length <= 64, "Each batch of the script is at most 64 bytes.");
            data.Dispose();
            seen++;
        }

        Assert.Equal(Script.Length, seen);
        Assert.Equal(Script.Length, held.Count);
        Assert.All(held, bytes => Assert.InRange(bytes, 0, bound - 1));
        Assert.Contains(held, static bytes => bytes > 0);
    }

    private static int CountRows(LedgerScanItem item)
    {
        var count = 0;
        foreach (var row in item.Rows)
        {
            _ = row;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Proposes the script and recovers the ledger in another process, which ends it at the script's last slot,
    /// mid-segment (<see cref="LedgerSegmentScript"/>).
    /// </summary>
    private static async Task<(TestLedgerMap Map, LedgerMetadata Closed, List<ScriptedRow> Expected)> WriteAndRecoverAsync(InMemoryLedgerReplicaTransport transport, CancellationToken token)
    {
        var (map, open, expected) = await LedgerSegmentScript.WriteAsync(transport, token);
        return (map, await LedgerSegmentScript.RecoverAsync(map, transport, open, token), expected);
    }

    /// <summary>Scans the ledger, returning each item's slot and each resolved row, and releasing each batch.</summary>
    private static async Task<(List<long> Slots, List<ScriptedRow> Rows)> ScanAsync(LedgerReader reader, LedgerMetadata ledger, Slot from, Slot below, string key, CancellationToken token)
    {
        var slots = new List<long>();
        var rows = new List<ScriptedRow>();
        await foreach (var item in reader.ScanAsync(ledger, from, below, Encoding.UTF8.GetBytes(key), token))
        {
            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
            using (data)
            {
                slots.Add(data.Slot.Value);
                LedgerSegmentScript.Collect(item, data.Slot.Value, rows);
            }
        }

        return (slots, rows);
    }

    /// <summary>A transport whose every replica lost the batch at <paramref name="lost"/>, and reports it missing.</summary>
    private sealed class LosingTransport(ILedgerReplicaTransport inner, Slot lost) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options)
        {
            var replica = inner.Open(node, ledger, options);
            return new DelegatingLedgerReplica
            {
                Read = (slot, tail, token) => slot == lost ? ValueTask.FromResult<ReplicaRead>(ReplicaRead.Missing.Instance) : replica.ReadAsync(slot, tail, token),
                ReadTail = replica.ReadTailAsync,
                Disposal = replica.DisposeAsync,
            };
        }

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(node, ledgers, cancellationToken);
    }
}

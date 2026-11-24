using System.Text;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// LG-I15 guards in the Archive layer: <see cref="ArchiveRow.Live"/> rejects <see cref="RowKind.LocalAppend"/>, and all
/// three <see cref="ArchiveReader"/> read paths (<c>Collect</c>, <c>FetchSingleSlotIndexedAsync</c>, and
/// <c>ProcessSlot</c>) skip <see cref="RowKind.LocalAppend"/> rows cleanly.
/// </summary>
public sealed class ArchiveLocalAppendGuardTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void ArchiveRowLiveRejectsLocalAppend()
    {
        var ledger = new LedgerId(1);
        var tag = new Tag(ledger, Slot.Zero, 0);
        var segment = new LedgerSegmentId(ledger, Slot.Zero);
        Assert.Throws<ArgumentException>(() => ArchiveRow.Live(tag, RowKind.LocalAppend, default, segment, default, default));
        Assert.Throws<ArgumentException>(() => ArchiveCommand.Of(RowKind.LocalAppend, ReadOnlyMemory<byte>.Empty));
    }

    [Fact(Timeout = 30_000)]
    public async Task ArchiveReaderSkipsLocalAppendAcrossAllThreeReadPaths()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cluster = await ArchiveCluster.StartAsync();
        var stream = LocalStreamName.Parse("orleans.wakeup");
        var open = cluster.OpenLedger(1);
        var ledgerId = open.Id;
        var writer = cluster.Primary.Registry!.TryGet(ledgerId)!;
        var localKey = LedgerLocalKey.Encode(ledgerId, open.Range, stream);
        byte[] journalKey = [1, 2, 3, 4, (byte)'k'];

        // Write a batch mixing a journal row and a LocalAppend row at slot 0, and a second batch at slot 1 referencing both.
        Slot slot0;
        using (var batch0 = await writer.CreateBatchAsync(2, 4096, ct))
        {
            _ = batch0.BeginRow(journalKey, RowKind.Append, default, default, 3, "v0-"u8);
            _ = batch0.BeginRow(localKey, RowKind.LocalAppend, default, default, 3, "wk0"u8);
            Assert.True((await writer.ProposeAsync(batch0, ct)).TryGetValue(out ProposalOutcome.Durable d0));
            slot0 = d0.Slot;
        }

        Slot slot1;
        using (var batch1 = await writer.CreateBatchAsync(2, 4096, ct))
        {
            _ = batch1.BeginRow(journalKey, RowKind.Append, batch1.HeldVersion(journalKey), default, 2, "v1"u8);
            _ = batch1.BeginRow(localKey, RowKind.LocalAppend, batch1.HeldVersion(localKey), default, 3, "wk1"u8);
            Assert.True((await writer.ProposeAsync(batch1, ct)).TryGetValue(out ProposalOutcome.Durable d1));
            slot1 = d1.Slot;
        }

        var closed = await cluster.ReplaceLedgerAsync(1);
        Assert.Equal(ledgerId, closed);

        var process = await cluster.StartProcessAsync(cluster.Store);

        // Path 1: unindexed scan (Collect) over both the journal key and the local key.
        var unindexed = new ArchiveReader(process.Reader, cluster.Store, TimeProvider.System);
        using (var readJournal = await unindexed.ReadAsync(journalKey, ct))
        {
            Assert.Equal("v0-v1", ReadText(readJournal));
            Assert.Equal(2, readJournal.Rows.Length);
            foreach (var row in readJournal.Rows)
            {
                Assert.NotEqual(RowKind.LocalAppend, row.Kind);
            }
        }

        using (var readLocal = await unindexed.ReadAsync(localKey, ct))
        {
            Assert.Equal(0, readLocal.Rows.Length);
        }

        // Path 2: single-slot indexed read (FetchSingleSlotIndexedAsync) pointing at the LocalAppend defining row.
        var singleIndex = new ArchiveKeyIndex((ledger, below, key, _) =>
        {
            if (ledger.Id == closed && key.Span.SequenceEqual(localKey))
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([new(new Tag(closed, slot0, 1), slot0, 1)]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });
        var singleReader = new ArchiveReader(process.Reader, cluster.Store, TimeProvider.System, singleIndex);
        using (var readSingleLocal = await singleReader.ReadAsync(localKey, ct))
        {
            Assert.Equal(0, readSingleLocal.Rows.Length);
        }

        // Path 3: multi-slot indexed read (ProcessSlot) pointing at both LocalAppend slots (defining + referencing).
        var multiIndex = new ArchiveKeyIndex((ledger, below, key, _) =>
        {
            if (ledger.Id == closed && key.Span.SequenceEqual(localKey))
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed(
                [
                    new(new Tag(closed, slot0, 1), slot0, 1),
                    new(new Tag(closed, slot1, 1), slot0, 1),
                ]));
            }

            if (ledger.Id == closed && key.Span.SequenceEqual(journalKey))
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed(
                [
                    new(new Tag(closed, slot0, 0), slot0, 0),
                    new(new Tag(closed, slot1, 0), slot0, 0),
                ]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });
        var multiReader = new ArchiveReader(process.Reader, cluster.Store, TimeProvider.System, multiIndex);
        using (var readMultiLocal = await multiReader.ReadAsync(localKey, ct))
        {
            Assert.Equal(0, readMultiLocal.Rows.Length);
        }

        using var readMultiJournal = await multiReader.ReadAsync(journalKey, ct);
        Assert.Equal("v0-v1", ReadText(readMultiJournal));
        Assert.Equal(2, readMultiJournal.Rows.Length);
    }

    private static string ReadText(ArchiveRead read)
    {
        var sb = new StringBuilder();
        foreach (var row in read.Rows)
        {
            if (!row.IsHeld)
            {
                _ = sb.Append(Encoding.UTF8.GetString(row.Body.ToArray()));
            }
        }

        return sb.ToString();
    }
}

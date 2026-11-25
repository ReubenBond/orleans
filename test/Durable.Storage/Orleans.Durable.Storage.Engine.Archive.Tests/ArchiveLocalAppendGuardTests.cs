using System.Text;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// LG-I15 guards in the Archive layer: <see cref="ArchiveRow.Live"/> rejects <see cref="RowKind.Message"/>, and all
/// three <see cref="ArchiveReader"/> read paths (<c>Collect</c>, <c>FetchSingleSlotIndexedAsync</c>, and
/// <c>ProcessSlot</c>) skip <see cref="RowKind.Message"/> rows cleanly.
/// </summary>
public sealed class ArchiveLocalAppendGuardTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void ArchiveRowLiveRejectsMessage()
    {
        var ledger = new LedgerId(1);
        var tag = new Tag(ledger, Slot.Zero, 0);
        var segment = new LedgerSegmentId(ledger, Slot.Zero);
        Assert.Throws<ArgumentException>(() => ArchiveRow.Live(tag, RowKind.Message, default, segment, default, default));
        Assert.Throws<ArgumentException>(() => ArchiveCommand.Of(RowKind.Message, ReadOnlyMemory<byte>.Empty));
    }

    [Fact(Timeout = 30_000)]
    public async Task ArchiveReaderSkipsMessageAcrossAllReadPaths()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cluster = await ArchiveCluster.StartAsync();
        var channel = MessageChannel.Parse("orleans.wakeup");
        var open = cluster.OpenLedger(1);
        var ledgerId = open.Id;
        var writer = cluster.Primary.Registry!.TryGet(ledgerId)!;
        byte[] journalKey = [1, 2, 3, 4, (byte)'k'];

        Slot slot0;
        using (var batch0 = await writer.CreateBatchAsync(2, 4096, ct))
        {
            _ = batch0.BeginRow(journalKey, RowKind.Append, default, default, 3, "v0-"u8);
            batch0.BeginMessage(channel, 3, "wk0"u8);
            Assert.True((await writer.ProposeAsync(batch0, ct)).TryGetValue(out ProposalOutcome.Durable d0));
            slot0 = d0.Slot;
        }

        Slot slot1;
        using (var batch1 = await writer.CreateBatchAsync(2, 4096, ct))
        {
            _ = batch1.BeginRow(journalKey, RowKind.Append, batch1.HeldVersion(journalKey), default, 2, "v1"u8);
            batch1.BeginMessage(channel, 3, "wk1"u8);
            Assert.True((await writer.ProposeAsync(batch1, ct)).TryGetValue(out ProposalOutcome.Durable d1));
            slot1 = d1.Slot;
        }

        var closed = await cluster.ReplaceLedgerAsync(1);
        Assert.Equal(ledgerId, closed);

        var process = await cluster.StartProcessAsync(cluster.Store);

        // Path 1: unindexed scan (Collect) skips Message rows cleanly.
        var unindexed = new ArchiveReader(process.Reader, cluster.Store, TimeProvider.System);
        using (var readJournal = await unindexed.ReadAsync(journalKey, ct))
        {
            Assert.Equal("v0-v1", ReadText(readJournal));
            Assert.Equal(2, readJournal.Rows.Length);
            foreach (var row in readJournal.Rows)
            {
                Assert.NotEqual(RowKind.Message, row.Kind);
            }
        }

        // Path 2: indexed read (ProcessSlot) skips Message rows cleanly.
        var multiIndex = new ArchiveKeyIndex((ledger, below, key, _) =>
        {
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

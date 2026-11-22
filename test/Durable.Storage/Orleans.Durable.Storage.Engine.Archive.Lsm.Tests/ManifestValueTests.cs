using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.ManifestChanges;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// The manifest value's index of sections by key and its watermarks (archive §2.2).
/// </summary>
[NoPageLeakCheck("Manifest values, watermarks, and section references hold no buffers; the tests rent no pages.")]
public sealed class ManifestValueTests
{
    /// <summary>
    /// A key's sections are those whose partitions contain it, at every depth, in position order; a key no partition
    /// contains has none.
    /// </summary>
    [Fact]
    public void AKeysSectionsAreThoseWhosePartitionsContainItAtEveryDepth()
    {
        var root = Section("d/1/0/a", KeyRange.Root);
        var upper = Section("c/1-80/b", KeyRange.Containing([0x80], 1));
        var lower = Section("c/1-00/c", KeyRange.Containing([0x00], 1));
        var deep = Section("c/8-81/d", KeyRange.Containing([0x81], 8));
        var deeper = Section("c/8-81/e", KeyRange.Containing([0x81], 8));
        var value = new ManifestValue(1, Ledger(1), [], [root, upper, lower, deep, deeper]);

        Assert.Equal([upper, deep, deeper, root], value.SectionsContaining([0x81, 0x01]));
        Assert.Equal([upper, root], value.SectionsContaining([0x82]));
        Assert.Equal([lower, root], value.SectionsContaining([0x01]));
        Assert.Equal([deep, deeper], new ManifestValue(1, Ledger(1), [], [deep, deeper]).SectionsContaining([0x81]));
        Assert.Empty(new ManifestValue(1, Ledger(1), [], [deep]).SectionsContaining([0x82]));
        Assert.Empty(ManifestValue.Absent.SectionsContaining([0x01]));
        _ = Assert.Throws<ArgumentException>(() => value.SectionsContaining([]));
    }

    /// <summary>
    /// A key a partition at 70 bits contains, past the 8 bytes the index hashes, is found by its whole prefix: a key which
    /// shares the first 8 bytes and differs in the ninth is not.
    /// </summary>
    [Fact]
    public void APartitionPastEightBytesIsMatchedByItsWholePrefix()
    {
        byte[] key = [1, 2, 3, 4, 5, 6, 7, 8, 0b1100_0000];
        var deep = Section("c/70-0102030405060708/a", KeyRange.Containing(key, 70));
        var value = new ManifestValue(1, Ledger(1), [], [deep]);

        Assert.Equal([deep], value.SectionsContaining(key));
        Assert.Empty(value.SectionsContaining([1, 2, 3, 4, 5, 6, 7, 8, 0b1000_0000]));
    }

    /// <summary>
    /// A listed ledger's watermark is its <c>T</c> once complete, below <c>DrainedBelow</c> or marked so, and otherwise its
    /// recorded watermark or 0. A ledger the map lists open which the value shows complete has none, its map older than
    /// the value; a closed ledger whose entry lies past its <c>T</c>, or completes elsewhere than at it, is corrupt.
    /// </summary>
    [Fact]
    public void ALedgersWatermarkIsItsTailOnceCompleteAndOtherwiseTheRecordedOne()
    {
        var value = new ManifestValue(
            3,
            Ledger(3),
            [
                new(Ledger(4), new ManifestWatermark(Slot(9), true)),
                new(Ledger(5), new ManifestWatermark(Slot(2), false)),
                new(Ledger(7), new ManifestWatermark(Slot(8), true)),
            ],
            []);

        Assert.Equal(Slot(7), value.Watermark(Metadata(1, closedAt: 7)));
        Assert.Equal(Slot(9), value.Watermark(Metadata(4, closedAt: 9)));
        Assert.Equal(Slot(2), value.Watermark(Metadata(5, closedAt: 12)));
        Assert.Equal(Slot(2), value.Watermark(Metadata(5, closedAt: 2)));
        Assert.Equal(Slot(2), value.Watermark(Metadata(5)));
        Assert.Equal(Slot(0), value.Watermark(Metadata(6)));
        Assert.Null(value.Watermark(Metadata(2)));
        Assert.Null(value.Watermark(Metadata(4)));
        _ = Assert.Throws<ArchiveCorruptException>(() => value.Watermark(Metadata(5, closedAt: 1)));
        _ = Assert.Throws<ArchiveCorruptException>(() => value.Watermark(Metadata(7, closedAt: 9)));
    }

    private static SectionRef Section(string name, KeyRange partition)
    {
        var tag = new Tag(Ledger(1), Slot(0), 0);
        return new SectionRef(new ObjectName(name), 0, 100, partition, tag, tag, new ObjectRange(100, 16));
    }

    private static LedgerMetadata Metadata(ulong id, long? closedAt = null)
    {
        var node = LiveLedgers.Node;
        LedgerStatus status = closedAt is { } t ? new LedgerStatus.Closed(Slot(t)) : LedgerStatus.Open.Instance;
        return new LedgerMetadata(Ledger(id), KeyRange.Root, [node], 4, new PrimaryClaim(node, SequencerInstanceId.New()), status);
    }
}

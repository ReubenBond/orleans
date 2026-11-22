using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// A node's WAL release (orleans.md §5.9, §6, OR-I13): the retention target <c>T</c>, the resource distance <c>F</c>, and
/// the rolls the node requests: every live replica at the lowest first file once it is <c>F</c> files behind the current
/// one, and only while each of their ledgers is open.
/// </summary>
[NoPageLeakCheck("The release rules rent no pages.")]
public sealed class WalReleaseTests
{
    private static readonly KeyRange[] Ranges = [.. Enumerable.Range(0, 3).Aggregate(
        (IEnumerable<KeyRange>)[KeyRange.Root],
        static (level, _) => level.SelectMany(static range => new[] { range.Children.Lower, range.Children.Upper }))];

    /// <summary><c>T</c> defaults to the smaller of 50 GiB and half the budget; a configured target is taken as it is.</summary>
    [Fact]
    public void TheRetentionTargetDefaultsToHalfTheBudgetUpTo50GiB()
    {
        Assert.Equal(5L << 30, WalRelease.Retention(new DurableStorageOptions(), 10L << 30));
        Assert.Equal(50L << 30, WalRelease.Retention(new DurableStorageOptions(), 200L << 30));
        Assert.Equal(7, WalRelease.Retention(new DurableStorageOptions { WalRetentionBytes = 7 }, 200L << 30));
    }

    /// <summary><c>F</c> is <c>max(1, T / (2 · WalFileSize))</c>.</summary>
    [Fact]
    public void TheResourceDistanceIsHalfTheTargetInFilesAtLeastOne()
    {
        Assert.Equal(25ul, WalRelease.Distance(50L << 30, 1L << 30));
        Assert.Equal(1ul, WalRelease.Distance(1L << 30, 1L << 30));
    }

    /// <summary>
    /// The oldest open replica is rolled once its first file is <c>F</c> files behind the current one, never one file
    /// sooner, and a younger replica far behind is not.
    /// </summary>
    [Fact]
    public void TheOldestOpenReplicaIsRolledOnceItIsFFilesBehind()
    {
        var map = Map(Open(1), Open(2));
        LedgerReplicaRetention[] replicas = [new(new LedgerId(1), FirstFile: 3), new(new LedgerId(2), FirstFile: 4)];

        Assert.Empty(WalRelease.Rolls(replicas, currentFile: 4, distance: 2, map));
        Assert.Equal([new LedgerId(1)], WalRelease.Rolls(replicas, currentFile: 5, distance: 2, map));
        Assert.Equal([new LedgerId(1)], WalRelease.Rolls(replicas, currentFile: 50, distance: 2, map));
        Assert.Empty(WalRelease.Rolls([], currentFile: 50, distance: 2, map));
    }

    /// <summary>
    /// Every open replica tied at the lowest first file is rolled together, since rolling some of them frees no file; while
    /// one of them is closed or absent from the map, none is, and its drain or drop releases the file.
    /// </summary>
    [Fact]
    public void ReplicasTiedAtTheLowestFileRollTogetherOnlyWhileEveryOneIsOpen()
    {
        LedgerReplicaRetention[] tied = [new(new LedgerId(1), FirstFile: 3), new(new LedgerId(2), FirstFile: 3), new(new LedgerId(3), FirstFile: 4)];

        Assert.Equal([new LedgerId(1), new LedgerId(2)], WalRelease.Rolls(tied, currentFile: 9, distance: 1, Map(Open(1), Open(2), Open(3))));
        Assert.Empty(WalRelease.Rolls(tied, currentFile: 9, distance: 1, Map(Open(1), Closed(2), Open(3))));
        Assert.Empty(WalRelease.Rolls(tied, currentFile: 9, distance: 1, Map(Open(1), Open(3))));
    }

    private static LedgerMetadata Open(int id) => Ledger(id, LedgerStatus.Open.Instance);

    private static LedgerMetadata Closed(int id) => Ledger(id, new LedgerStatus.Closed(Slot.Zero));

    private static LedgerMetadata Ledger(int id, LedgerStatus status) =>
        new(new LedgerId((ulong)id), Ranges[id], [Node(1), Node(2), Node(3)], 4, new PrimaryClaim(Node(1), SequencerInstanceId.New()), status);

    private static LedgerMapValue Map(params LedgerMetadata[] ledgers) => new(1, new LedgerId(10), 0, [.. ledgers]);
}

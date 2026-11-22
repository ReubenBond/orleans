using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// A node's disk policy (orleans.md §5.9, §6): pressure while the LedgerStore's bytes exceed the budget or a volume holding
/// it has less free space than the minimum, ending only once both are clear by one WAL file, each change logged once
/// (events 6237, 6238); the budget's default; and the start's refusal of bounds which leave pressure no way to end.
/// </summary>
[NoPageLeakCheck("The disk policy rents no pages.")]
public sealed class NodeDiskTests
{
    private const long WalFile = 10;

    /// <summary>
    /// Pressure starts beyond the budget or below the free minimum, never at either bound, and ends only once the bytes are
    /// a WAL file below the budget and the free space a WAL file above the minimum; each change is logged once.
    /// </summary>
    [Fact]
    public void PressureStartsPastEitherBoundAndEndsOnceBothAreClearByOneWalFile()
    {
        var logger = new RecordingLogger<NodeDisk>();
        var disk = new NodeDisk(Node(1), budgetBytes: 100, minimumFreeBytes: 50, WalFile, logger);

        Assert.False(Judge(disk, 100, 50));
        Assert.True(Judge(disk, 101, 1_000));
        Assert.True(Judge(disk, 101, 1_000));
        Assert.True(Judge(disk, 91, 1_000));
        Assert.True(Judge(disk, 90, 59));
        Assert.False(Judge(disk, 90, 60));
        Assert.False(Judge(disk, 100, 50));
        Assert.True(Judge(disk, 0, 49));

        Assert.Equal([6237, 6238, 6237], logger.Events);
        Assert.Equal(new NodeDiskMeasurement(7, 3, UnderPressure: true), disk.Judge(Usage(7, 3)));
    }

    /// <summary>
    /// orleans.md §6: the budget defaults to 80% of the volume holding the WAL, whatever the other volumes; a configured
    /// budget is taken as it is.
    /// </summary>
    [Fact]
    public void TheBudgetDefaultsToMostOfTheWalsVolume()
    {
        var usage = new LedgerStoreDiskUsage(UsedBytes: 0, FreeBytes: 1L << 40, WalVolumeBytes: 1_000L << 30, SmallestVolumeBytes: 200L << 30);

        Assert.Equal(800L << 30, Create(new DurableStorageOptions(), usage).BudgetBytes);
        Assert.Equal(123L << 30, Create(new DurableStorageOptions { DiskBudgetBytes = 123L << 30 }, usage).BudgetBytes);
    }

    /// <summary>
    /// orleans.md §5.9: the start refuses room at or below <c>T</c> and three WAL files, the least a node holds past its
    /// oldest replica and the file of hysteresis by which pressure ends, the room being the smaller of the budget and the
    /// WAL's volume less the minimum free space, since a volume which fills first throttles the node before its WAL reaches
    /// <c>F</c>; and a minimum free space which, with one WAL file, reaches the size of a volume holding the store: neither
    /// pressure could end.
    /// </summary>
    [Fact]
    public void BoundsWhichLeavePressureNoWayToEndAreRefused()
    {
        var usage = new LedgerStoreDiskUsage(UsedBytes: 0, FreeBytes: 0, WalVolumeBytes: 10_000, SmallestVolumeBytes: 1_000);
        DurableStorageOptions Options(long budget, long minimumFree) =>
            new() { DiskBudgetBytes = budget, WalRetentionBytes = 100, WalFileSize = WalFile, MinimumFreeDiskBytes = minimumFree };

        Assert.Equal(131, Create(Options(budget: 131, minimumFree: 989), usage).BudgetBytes);
        _ = Assert.Throws<InvalidOperationException>(() => Create(Options(budget: 130, minimumFree: 0), usage));
        _ = Assert.Throws<InvalidOperationException>(() => Create(Options(budget: 131, minimumFree: 990), usage));

        // A budget the WAL's volume cannot hold: the volume less the minimum free space bounds the room.
        var small = new LedgerStoreDiskUsage(UsedBytes: 0, FreeBytes: 0, WalVolumeBytes: 136, SmallestVolumeBytes: 136);
        Assert.Equal(10_000, Create(Options(budget: 10_000, minimumFree: 5), small).BudgetBytes);
        _ = Assert.Throws<InvalidOperationException>(() => Create(Options(budget: 10_000, minimumFree: 6), small));
    }

    private static NodeDisk Create(DurableStorageOptions options, LedgerStoreDiskUsage usage) =>
        NodeDisk.Create(Node(1), options, usage, new RecordingLogger<NodeDisk>());

    private static bool Judge(NodeDisk disk, long used, long free) => disk.Judge(Usage(used, free)).UnderPressure;

    private static LedgerStoreDiskUsage Usage(long used, long free) => new(used, free, WalVolumeBytes: 1L << 40, SmallestVolumeBytes: 1L << 40);
}

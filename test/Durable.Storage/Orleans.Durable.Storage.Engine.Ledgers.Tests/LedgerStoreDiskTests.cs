using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// ledgerstore.md §7.1: the store measures the disk it uses, the bytes of its cluster claim and of the files beneath
/// <c>wal/</c> and <c>slots/</c>, and reads the volume of each directory for itself, since each may be its own mount
/// point; a failed listing of one of its directories stops the process (conventions.md §14).
/// </summary>
public sealed class LedgerStoreDiskTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The measurement counts every file the store holds on disk, its WAL's and a replica's checkpoints among them, and
    /// reads a volume whose free space is no larger than its size.
    /// </summary>
    [Fact]
    public async Task TheMeasurementCountsEveryFileOfTheStore()
    {
        using var directory = new TemporaryDirectory();
        await using var owned = await LedgerStoreReplicaTests.CreateAsync(directory.Path);
        for (var slot = 0; slot < 4; slot++)
        {
            _ = await LedgerStoreReplicaTests.WriteAsync(owned, slot, new byte[1_000]);
        }

        await LedgerStoreReplicaTests.SettledCheckpointAsync(owned);
        Assert.NotEmpty(Directory.EnumerateFiles(LedgerStore.SlotsPath(directory.Path), "*", SearchOption.AllDirectories));

        var usage = await owned.Store.MeasureDiskAsync(Token);

        Assert.Equal(BytesBeneath(directory.Path), usage.UsedBytes);
        Assert.InRange(usage.FreeBytes, 1, usage.SmallestVolumeBytes);
        Assert.Equal(usage.WalVolumeBytes, usage.SmallestVolumeBytes);
    }

    /// <summary>
    /// With <c>wal/</c> and <c>slots/</c> on volumes of their own, the store reports the least free space of the two,
    /// the size of <c>wal/</c>'s volume, and the size of the smaller volume.
    /// </summary>
    [Fact]
    public async Task EachDirectorysVolumeIsReadForItself()
    {
        var root = LedgerStoreReplicaTests.Simulated();
        root.Storage.VolumeSpace = static path => path == LedgerStore.WalPath(SimulatedNodeRoot.Path) ? (700, 1_000) : (300, 400);
        await using var store = await root.OpenAsync(create: true);

        var usage = await store.MeasureDiskAsync(Token);

        Assert.Equal((300, 1_000, 400), (usage.FreeBytes, usage.WalVolumeBytes, usage.SmallestVolumeBytes));
    }

    /// <summary>
    /// A directory of the store which has gone, as when its volume vanished, is a failed measurement, reported once with
    /// the directory's path, which stops the process.
    /// </summary>
    [Fact]
    public async Task AMissingDirectoryOfTheStoreStopsTheProcessNamingIt()
    {
        using var directory = new TemporaryDirectory();
        await using var store = await LedgerStoreReplicaTests.OpenStoreAsync(directory.Path, create: true);
        var slots = LedgerStore.SlotsPath(directory.Path);
        Directory.Delete(slots);
        var since = FatalErrors.ReportedCount;

        var failure = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await store.MeasureDiskAsync(Token));

        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal((FatalErrorSite.MeasureDirectory, Path.GetFullPath(slots)), (report.Site, report.Path));
        Assert.Same(failure, report.Failure.Exception);
    }

    private static long BytesBeneath(string path) =>
        new DirectoryInfo(path)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Sum(static file => file.Length);
}

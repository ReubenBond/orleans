namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// The disk a LedgerStore uses (<see cref="LedgerStore.MeasureDiskAsync"/>), which its node's disk budget governs.
/// </summary>
/// <param name="UsedBytes">The bytes of the store's files: its cluster claim, and the files beneath <c>wal/</c> and
/// <c>slots/</c>.</param>
/// <param name="FreeBytes">The least free space available to the process among the volumes holding <c>wal/</c> and
/// <c>slots/</c>.</param>
/// <param name="WalVolumeBytes">The size of the volume holding <c>wal/</c>.</param>
/// <param name="SmallestVolumeBytes">The size of the smaller of the volumes holding <c>wal/</c> and <c>slots/</c>.</param>
public sealed record LedgerStoreDiskUsage(long UsedBytes, long FreeBytes, long WalVolumeBytes, long SmallestVolumeBytes);

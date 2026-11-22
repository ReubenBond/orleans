using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// Configures a node's LedgerStore: its root directory and the write-ahead log every ledger of the node shares.
/// </summary>
public sealed record LedgerStoreOptions
{
    /// <summary>
    /// Gets the node root, which holds the WAL and catalog under <c>wal/</c> and each live ledger's checkpoints under
    /// <c>slots/{handle}/</c>.
    /// </summary>
    /// <remarks>
    /// The root is an operator's: it exists, and it and every directory above it are durable, before the store opens.
    /// The store creates and flushes only the root and what lies beneath it (ledgerstore.md §3.3).
    /// </remarks>
    public required string DirectoryPath { get; init; }

    /// <summary>
    /// Gets the WAL page size; see <see cref="WalOptions.PageSize"/>.
    /// </summary>
    public int PageSize { get; init; } = 4096;

    /// <summary>
    /// Gets the WAL file size; see <see cref="WalOptions.FileSize"/>.
    /// </summary>
    public long FileSize { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets a value indicating whether new WAL files are zero-filled at their full size; see
    /// <see cref="WalOptions.PreallocateFiles"/>.
    /// </summary>
    public bool PreallocateFiles { get; init; } = true;

    /// <summary>
    /// Gets the fewest WAL write groups in flight while records wait; see <see cref="WalOptions.MinWriteGroupsInFlight"/>.
    /// A runtime setting, which the creation configuration does not record.
    /// </summary>
    public int MinWriteGroupsInFlight { get; init; } = 1;

    /// <summary>
    /// Gets the most WAL write groups in flight; see <see cref="WalOptions.MaxWriteGroupsInFlight"/>.
    /// </summary>
    public int MaxWriteGroupsInFlight { get; init; } = 4;

    /// <summary>
    /// Gets the WAL write window W, in pages.
    /// </summary>
    public int WriteWindowPages { get; init; } = 4096;

    /// <summary>
    /// Gets the number of accepted record bytes which may await durability before writes wait.
    /// </summary>
    public long PageBufferBudgetBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>
    /// Gets the RAM-only lease duration granted by this node on unsealed non-recovery writes and heartbeats, and used
    /// as the startup <c>bootLeaseHorizon</c> on store open (ledgerstore.md §8.3, §8.11, LR-I10).
    /// <see cref="TimeSpan.Zero"/> disables leases on this node.
    /// </summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(3);

    internal WalOptions ToWalOptions(TimeProvider? timeProvider = null) => new()
    {
        TimeProvider = timeProvider ?? TimeProvider.System,
        PageSize = PageSize,
        FileSize = FileSize,
        PreallocateFiles = PreallocateFiles,
        MinWriteGroupsInFlight = MinWriteGroupsInFlight,
        MaxWriteGroupsInFlight = MaxWriteGroupsInFlight,
        WriteWindowPages = WriteWindowPages,
        PageBufferBudgetBytes = PageBufferBudgetBytes,
    };

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DirectoryPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(LeaseDuration, TimeSpan.Zero, nameof(LeaseDuration));
        ToWalOptions().Validate();
    }
}

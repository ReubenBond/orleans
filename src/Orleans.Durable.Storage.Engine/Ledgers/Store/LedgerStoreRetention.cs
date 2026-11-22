namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// What holds a LedgerStore's WAL (LS-I4).
/// </summary>
/// <param name="CurrentFile">The WAL file appends go to.</param>
/// <param name="FirstRetainedFile">The retention bound, below which every file is released: while the store holds two
/// valid catalog checkpoints, the lowest of the current file, each live replica's first file, and the file holding the
/// cut of the older of the two newest; otherwise the lowest file the store retains.</param>
/// <param name="OldestReplica">The ledger ID of the live replica with the lowest first file, which pins the WAL from that
/// file, or <see langword="null"/> when none is live.</param>
/// <param name="LiveReplicas">The number of live replicas the catalog lists.</param>
public sealed record LedgerStoreRetention(
    ulong CurrentFile,
    ulong FirstRetainedFile,
    LedgerId? OldestReplica,
    int LiveReplicas)
{
    /// <summary>
    /// Gets the number of WAL files the store keeps, from the first retained file through the current one.
    /// </summary>
    public ulong RetainedFiles => CurrentFile - FirstRetainedFile + 1;
}

/// <summary>
/// A live ledger replica and the first WAL file it may hold a record in, from which it pins the WAL (LS-I4).
/// </summary>
/// <param name="Ledger">The replica's ledger ID.</param>
/// <param name="FirstFile">Its first file.</param>
public sealed record LedgerReplicaRetention(LedgerId Ledger, ulong FirstFile);

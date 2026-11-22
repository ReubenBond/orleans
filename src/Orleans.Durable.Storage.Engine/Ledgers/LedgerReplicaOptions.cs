namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Configures the runtime behavior of an opened ledger replica.
/// </summary>
/// <remarks>
/// A replica is opened with the ledger's window <c>K</c> (ledger-replicas.md §4, §8.2). A ledger replica records the window of
/// the operation which creates it, rebuilds its retained contiguous tail from it after a restart, and refuses an
/// operation of another window (LR-I6); the ledger protocol bounds recovery's reads by it (LP-I7).
/// </remarks>
public sealed record LedgerReplicaOptions
{
    /// <summary>
    /// Gets the ledger's window <c>K</c>: the writer's bound on outstanding addresses (ledger-replicas.md §3.2). Must be
    /// positive.
    /// </summary>
    public required int WindowSize { get; init; }
}

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// What a <see cref="LedgerStore.CloseAsync"/> did with the replicas' final checkpoints (ledgerstore.md §8.10): those it
/// wrote, and those its caller's bound skipped, whose replicas the next open replays from their checkpoints in force.
/// </summary>
/// <param name="FinalCheckpoints">The final checkpoints written.</param>
/// <param name="SkippedFinalCheckpoints">The final checkpoints the bound skipped.</param>
public readonly record struct LedgerStoreCloseResult(int FinalCheckpoints, int SkippedFinalCheckpoints);

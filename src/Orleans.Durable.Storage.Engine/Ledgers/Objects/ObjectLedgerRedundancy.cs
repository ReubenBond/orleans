namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// The durability redundancy an operator states for the cluster's object-ledger store (orleans.md §5.14,
/// verification.md §13): recorded in each silo's startup log, never verified against the provider.
/// </summary>
public enum ObjectLedgerRedundancy
{
    /// <summary>A single-zone store (such as Azure LRS, S3 Express One Zone, or a zonal bucket).</summary>
    SingleZone = 0,

    /// <summary>A zone-redundant store (such as Azure ZRS or standard S3/GCS multi-zone buckets).</summary>
    ZoneRedundant = 1,
}

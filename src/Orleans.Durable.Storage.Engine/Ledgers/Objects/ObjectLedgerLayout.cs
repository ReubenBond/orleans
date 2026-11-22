namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// How an object store holds its ledgers' batches (object-ledgers.md §4), fixed per store by its record (OL-I1).
/// </summary>
public enum ObjectLedgerLayout
{
    /// <summary>One object per slot: a batch, or an empty SEAL (object-ledgers.md §8.1, §8.2).</summary>
    PerSlot,

    /// <summary>Append blobs of consecutive slots' frames (object-ledgers.md §8.1), row 7.4.</summary>
    Append,
}

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// The fuzz targets of the formats <c>Orleans.Durable.Storage.Engine.Ledgers</c> decodes: the batch, a segment's batches, the ledger map,
/// the object-ledger store's record, and the object-ledger extent (<c>CLOE</c> v1).
/// <see cref="LedgerStoreTargets"/> holds the LedgerStore's.
/// </summary>
public static class LedgersTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } =
    [
        new BatchTarget(),
        new LedgerSegmentTarget(),
        new LedgerMapTarget(),
        new ObjectStoreRecordTarget(),
        new ObjectLedgerExtentTarget(),
    ];
}

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// The fuzz targets of the LedgerStore's formats.
/// </summary>
public static class LedgerStoreTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } =
    [
        new ReplicaCatalogTarget(),
        new StoreRecordTarget(),
        new SlotLeafTarget(),
        new CheckpointRootTarget(),
        new CheckpointGenerationTarget(),
    ];
}

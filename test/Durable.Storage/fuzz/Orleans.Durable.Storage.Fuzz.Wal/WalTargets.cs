namespace Orleans.Durable.Storage.Fuzz.Wal;

/// <summary>
/// The fuzz targets of the WAL's formats.
/// </summary>
public static class WalTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } =
    [
        new WalHeaderTarget(),
        new WalPageTarget(),
        new WalScanTarget(),
    ];
}

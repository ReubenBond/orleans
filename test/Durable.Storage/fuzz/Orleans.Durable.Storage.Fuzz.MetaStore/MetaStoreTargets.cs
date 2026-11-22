namespace Orleans.Durable.Storage.Fuzz.MetaStore;

/// <summary>
/// The fuzz targets of the MetaStore's formats.
/// </summary>
public static class MetaStoreTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } =
    [
        new RegisterFileTarget(),
    ];
}

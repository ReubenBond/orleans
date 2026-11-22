namespace Orleans.Durable.Storage;

/// <summary>The composition's meter (orleans.md §8): each silo's node publishes its resources, and the controller grain the map's ledgers.</summary>
internal static class DurableStorageMeter
{
    /// <summary>The meter's name.</summary>
    public const string Name = "Orleans.Durable.Storage";
}

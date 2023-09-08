namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Creates a partition key for the provided grain.
/// </summary>
public interface IPartitionKeyProvider
{
    /// <summary>
    /// Creates a partition key for the provided grain.
    /// </summary>
    /// <param name="stateName">The grain state name.</param>
    /// <param name="grainTypeName">The grain type name.</param>
    /// <param name="grainKey">The grain key.</param>
    /// <returns>The partition key.</returns>
    ValueTask<string> GetPartitionKey(string stateName, string grainTypeName, string grainKey);
}

internal class DefaultPartitionKeyProvider : IPartitionKeyProvider
{
    public ValueTask<string> GetPartitionKey(string stateName, string grainTypeName, string grainKey) => new(CosmosIdSanitizer.Sanitize(stateName));
}
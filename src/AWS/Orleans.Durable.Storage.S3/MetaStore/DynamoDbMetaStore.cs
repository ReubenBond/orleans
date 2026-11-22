using Amazon.DynamoDBv2;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.S3.MetaStore;

/// <summary>
/// Keeps each register in one DynamoDB item, whose <c>Version</c> attribute is the register version.
/// </summary>
/// <remarks>
/// The table has a single string partition key attribute <c>Id</c> and no sort key. The register keyed
/// <c>(cluster, name)</c> is the item whose <c>Id</c> is <c>{partitionKeyPrefix}{cluster:N}/{name}</c>
/// (metastore.md §7). Getting a register performs no I/O.
/// </remarks>
public sealed class DynamoDbMetaStore : IMetaStore
{
    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;
    private readonly string _partitionKeyPrefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamoDbMetaStore"/> class over a caller-owned client.
    /// </summary>
    /// <param name="client">The DynamoDB client.</param>
    /// <param name="tableName">The table which holds the registers.</param>
    /// <param name="partitionKeyPrefix">The partition key prefix, or <see langword="null"/> for none.</param>
    public DynamoDbMetaStore(IAmazonDynamoDB client, string tableName, string? partitionKeyPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        _client = client;
        _tableName = tableName;
        _partitionKeyPrefix = partitionKeyPrefix ?? string.Empty;
    }

    /// <inheritdoc/>
    public ValueTask<IConditionalRegister> GetRegisterAsync(
        Guid cluster,
        string name,
        CancellationToken cancellationToken = default)
    {
        var partitionKey = GetPartitionKey(_partitionKeyPrefix, cluster, name);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IConditionalRegister>(
            new DynamoDbConditionalRegister(_client, _tableName, partitionKey));
    }

    /// <summary>
    /// Forms the partition key of the register keyed <paramref name="cluster"/> and <paramref name="name"/>.
    /// </summary>
    internal static string GetPartitionKey(string partitionKeyPrefix, Guid cluster, string name) =>
        string.Concat(partitionKeyPrefix, MetaStoreRegisterName.ToKey(cluster, name));
}

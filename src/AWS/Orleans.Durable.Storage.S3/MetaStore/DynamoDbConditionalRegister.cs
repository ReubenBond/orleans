using System.Buffers;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.S3.MetaStore;

/// <summary>
/// One register in a DynamoDB item whose <c>Version</c> attribute, a fresh GUID per write, is the register version.
/// </summary>
/// <remarks>
/// A write copies its value into the array of the binary attribute's stream before the request is dispatched; a read's
/// entry wraps the buffer of the binary attribute's stream when the stream exposes it, and otherwise a copy of it.
/// </remarks>
internal sealed class DynamoDbConditionalRegister : IConditionalRegister
{
    internal const string PartitionKeyAttributeName = "Id";
    internal const string ValueAttributeName = "Value";
    internal const string VersionAttributeName = "Version";

    private readonly IAmazonDynamoDB _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamoDbConditionalRegister"/> class.
    /// </summary>
    public DynamoDbConditionalRegister(IAmazonDynamoDB client, string tableName, string partitionKey)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        _client = client;
        TableName = tableName;
        PartitionKey = partitionKey;
    }

    /// <summary>
    /// Gets the table name.
    /// </summary>
    internal string TableName { get; }

    /// <summary>
    /// Gets the partition key value identifying the register item.
    /// </summary>
    internal string PartitionKey { get; }

    /// <inheritdoc/>
    public async ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.GetItemAsync(
            CreateGetItemRequest(TableName, PartitionKey),
            cancellationToken).ConfigureAwait(false);
        return response.IsItemSet ? MapItem(response.Item) : null;
    }

    /// <inheritdoc/>
    public async ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken = default)
    {
        var nextVersion = Guid.NewGuid().ToString("N");
        var request = CreatePutItemRequest(
            TableName,
            PartitionKey,
            expectedVersion,
            nextVersion,
            value);
        using var valueStream = request.Item[ValueAttributeName].B;

        // "The MetaStore component has a simple API: it is a single versioned register supporting a
        // conditional write. Reading the MetaStore returns a value with an attached version. Writing to it
        // requires supplying a new value and an expected existing version."
        // See research/papers/delos/paper.md, section 3.2.
        try
        {
            _ = await _client.PutItemAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException) when (!cancellationToken.IsCancellationRequested)
        {
            return RegisterWriteOutcome.Mismatch.Instance;
        }

        // The item's version attribute is the one this write stamped.
        return new RegisterWriteOutcome.Applied(new MetaStoreVersion(nextVersion));
    }

    internal static GetItemRequest CreateGetItemRequest(string tableName, string partitionKey) =>
        new()
        {
            TableName = tableName,
            ConsistentRead = true,
            Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [PartitionKeyAttributeName] = new AttributeValue { S = partitionKey },
            },
        };

    internal static PutItemRequest CreatePutItemRequest(
        string tableName,
        string partitionKey,
        MetaStoreVersion? expectedVersion,
        string nextVersion,
        in ReadOnlySequence<byte> value)
    {
        var request = new PutItemRequest
        {
            TableName = tableName,
            Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [PartitionKeyAttributeName] = new AttributeValue { S = partitionKey },
                [ValueAttributeName] = new AttributeValue { B = new MemoryStream(value.ToArray(), writable: false) },
                [VersionAttributeName] = new AttributeValue { S = nextVersion },
            },
            ExpressionAttributeNames = [],
        };

        if (expectedVersion is null)
        {
            request.ConditionExpression = "attribute_not_exists(#key)";
            request.ExpressionAttributeNames["#key"] = PartitionKeyAttributeName;
        }
        else
        {
            request.ConditionExpression = "#version = :expectedVersion";
            request.ExpressionAttributeNames["#version"] = VersionAttributeName;
            request.ExpressionAttributeValues = new(StringComparer.Ordinal)
            {
                [":expectedVersion"] = new AttributeValue { S = expectedVersion.Value },
            };
        }

        return request;
    }

    internal static MetaStoreEntry MapItem(IReadOnlyDictionary<string, AttributeValue> item)
    {
        if (!item.TryGetValue(VersionAttributeName, out var versionAttribute)
            || string.IsNullOrWhiteSpace(versionAttribute.S))
        {
            throw new InvalidDataException(
                $"DynamoDB item is missing string attribute '{VersionAttributeName}'.");
        }

        if (!item.TryGetValue(ValueAttributeName, out var valueAttribute) || valueAttribute.B is null)
        {
            throw new InvalidDataException(
                $"DynamoDB item is missing binary attribute '{ValueAttributeName}'.");
        }

        // The SDK's stream of the read's response holds the value; the entry wraps its buffer when the stream exposes it.
        var value = valueAttribute.B.TryGetBuffer(out var buffer) ? buffer : valueAttribute.B.ToArray();
        return new MetaStoreEntry(new MetaStoreVersion(versionAttribute.S), ArcBuffer.Wrap(value));
    }
}

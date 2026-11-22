using System.Buffers;
using Azure;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.AzureStorage.MetaStore;

/// <summary>
/// One register in an Azure Table entity whose ETag is the register version.
/// </summary>
/// <remarks>
/// A write copies its value into the entity's binary property before the request is dispatched; a read's entry wraps
/// the array of the entity the SDK returns, without a copy.
/// </remarks>
internal sealed class AzureTableConditionalRegister : IConditionalRegister
{
    internal const string ValuePropertyName = "Value";

    private readonly TableClient _table;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureTableConditionalRegister"/> class.
    /// </summary>
    public AzureTableConditionalRegister(TableClient table, string partitionKey, string rowKey)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);
        _table = table;
        PartitionKey = partitionKey;
        RowKey = rowKey;
    }

    /// <summary>
    /// Gets the entity partition key.
    /// </summary>
    internal string PartitionKey { get; }

    /// <summary>
    /// Gets the entity row key.
    /// </summary>
    internal string RowKey { get; }

    /// <inheritdoc/>
    public async ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(
            PartitionKey,
            RowKey,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.HasValue ? MapEntity(response.Value!) : null;
    }

    /// <inheritdoc/>
    public async ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken = default)
    {
        var entity = CreateEntity(PartitionKey, RowKey, value);

        // "The MetaStore component has a simple API: it is a single versioned register supporting a
        // conditional write. Reading the MetaStore returns a value with an attached version. Writing to it
        // requires supplying a new value and an expected existing version."
        // See research/papers/delos/paper.md, section 3.2.
        Response response;
        try
        {
            response = expectedVersion is null
                ? await _table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false)
                : await _table.UpdateEntityAsync(
                    entity,
                    new ETag(expectedVersion.Value),
                    TableUpdateMode.Replace,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (
            !cancellationToken.IsCancellationRequested
            && IsVersionConflict(exception, expectedVersion is null))
        {
            return RegisterWriteOutcome.Mismatch.Instance;
        }

        return new RegisterWriteOutcome.Applied(MapAppliedVersion(response));
    }

    internal static TableEntity CreateEntity(string partitionKey, string rowKey, in ReadOnlySequence<byte> value) =>
        new(partitionKey, rowKey)
        {
            [ValuePropertyName] = value.ToArray(),
        };

    internal static MetaStoreEntry MapEntity(TableEntity entity)
    {
        if (!entity.TryGetValue(ValuePropertyName, out var storedValue) || storedValue is not byte[] bytes)
        {
            throw new InvalidDataException(
                $"Azure Table entity is missing binary property '{ValuePropertyName}'.");
        }

        return new MetaStoreEntry(ToVersion(entity.ETag), ArcBuffer.Wrap(bytes));
    }

    /// <summary>
    /// Returns the version an applied write gave the entity: the ETag of the service's answer, formatted as
    /// <see cref="ToVersion"/> formats an entity's ETag on read, so both paths yield the same version.
    /// </summary>
    /// <exception cref="IOException">The answer carries no ETag, so the write's outcome is unknown.</exception>
    internal static MetaStoreVersion MapAppliedVersion(Response response) =>
        response.Headers.ETag is { } etag
            ? ToVersion(etag)
            : throw new IOException("Azure Table answered an applied write without the entity's ETag, so the write's outcome is unknown. Re-read the register to establish the operation's goal.");

    /// <summary>
    /// Returns the version an ETag names, in its header form: a response header's strong ETag is parsed without its
    /// quotes, and an entity's carries them.
    /// </summary>
    private static MetaStoreVersion ToVersion(ETag etag) => new(etag.ToString("H"));

    internal static bool IsVersionConflict(RequestFailedException exception, bool isCreate) =>
        isCreate
            ? HasErrorCode(exception, 409, TableErrorCode.EntityAlreadyExists)
            : HasErrorCode(exception, 412, TableErrorCode.UpdateConditionNotSatisfied)
                || HasErrorCode(exception, 404, TableErrorCode.ResourceNotFound)
                || HasErrorCode(exception, 404, TableErrorCode.EntityNotFound);

    private static bool HasErrorCode(
        RequestFailedException exception,
        int status,
        TableErrorCode errorCode) =>
        exception.Status == status
        && string.Equals(exception.ErrorCode, errorCode.ToString(), StringComparison.Ordinal);
}

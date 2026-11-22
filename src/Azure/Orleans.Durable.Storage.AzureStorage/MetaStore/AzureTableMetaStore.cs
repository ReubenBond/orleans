using Azure.Data.Tables;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.AzureStorage.MetaStore;

/// <summary>
/// Keeps each register in one entity of an Azure Table, whose ETag is the register version.
/// </summary>
/// <remarks>
/// The register keyed <c>(cluster, name)</c> is the entity in <c>partitionKey</c> whose row key is
/// <c>{rowKeyPrefix}{cluster:N}:{name}</c> with each <c>/</c> of the name written as <c>:</c>, because a row key
/// cannot contain <c>/</c> and no register name contains <c>:</c> (metastore.md §7). Getting a register performs
/// no I/O.
/// </remarks>
public sealed class AzureTableMetaStore : IMetaStore
{
    /// <summary>
    /// The default partition key.
    /// </summary>
    public const string DefaultPartitionKey = "durable-storage";

    private readonly TableClient _table;
    private readonly string _partitionKey;
    private readonly string _rowKeyPrefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureTableMetaStore"/> class.
    /// </summary>
    /// <param name="table">The table which holds the registers.</param>
    /// <param name="partitionKey">The partition key which holds the registers.</param>
    /// <param name="rowKeyPrefix">The row key prefix, or <see langword="null"/> for none.</param>
    public AzureTableMetaStore(
        TableClient table,
        string partitionKey = DefaultPartitionKey,
        string? rowKeyPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        _table = table;
        _partitionKey = partitionKey;
        _rowKeyPrefix = rowKeyPrefix ?? string.Empty;
    }

    /// <inheritdoc/>
    public ValueTask<IConditionalRegister> GetRegisterAsync(
        Guid cluster,
        string name,
        CancellationToken cancellationToken = default)
    {
        var rowKey = GetRowKey(_rowKeyPrefix, cluster, name);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IConditionalRegister>(
            new AzureTableConditionalRegister(_table, _partitionKey, rowKey));
    }

    /// <summary>
    /// Forms the row key of the register keyed <paramref name="cluster"/> and <paramref name="name"/>.
    /// </summary>
    internal static string GetRowKey(string rowKeyPrefix, Guid cluster, string name) =>
        string.Concat(rowKeyPrefix, MetaStoreRegisterName.ToKey(cluster, name).Replace(MetaStoreRegisterName.Separator, ':'));
}

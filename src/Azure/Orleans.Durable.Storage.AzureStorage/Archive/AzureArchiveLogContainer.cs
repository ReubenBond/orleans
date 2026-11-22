using Azure.Storage.Blobs;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.AzureStorage.Archive;

/// <summary>
/// A cluster's archive logs in one Azure Blob container (archive-logs.md §9.1, orleans.md §5.13): the keys' logs,
/// <see cref="AzureArchiveLogStore"/>, and durable storage's objects, <see cref="AzureBlobObjectStore"/>, both over one client of
/// the container and both under the store prefix <c>{prefix}/</c>, so a cluster's blobs are <c>{prefix}/wal/{id}</c>,
/// <c>{prefix}/checkpoints/{id}/{g:D20}</c>, and <c>{prefix}/durable-storage/…</c>.
/// </summary>
/// <param name="container">The container's client, created on first use.</param>
public sealed class AzureArchiveLogContainer(Func<BlobContainerClient> container) : IArchiveLogContainer
{
    private readonly Lazy<BlobContainerClient> _container = new(container ?? throw new ArgumentNullException(nameof(container)));

    /// <inheritdoc/>
    public IArchiveLogStore OpenLogs(string prefix, string format) => new AzureArchiveLogStore(_container.Value, format, BlobPrefixOf(prefix));

    /// <inheritdoc/>
    public IObjectStore OpenObjects(string prefix) => new AzureBlobObjectStore(_container.Value, BlobPrefixOf(prefix));

    private static string BlobPrefixOf(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return ObjectName.IsValid(prefix) && !prefix.Contains('/', StringComparison.Ordinal)
            ? prefix + "/"
            : throw new ArgumentException($"'{prefix}' is no store prefix: one object-name component.", nameof(prefix));
    }
}

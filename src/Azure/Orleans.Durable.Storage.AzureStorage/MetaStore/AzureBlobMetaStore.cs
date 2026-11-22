using Azure.Storage.Blobs;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.AzureStorage.MetaStore;

/// <summary>
/// Keeps each register in one Azure block blob of a container, whose ETag is the register version.
/// </summary>
/// <remarks>
/// The register keyed <c>(cluster, name)</c> is the blob <c>{prefix}{cluster:N}/{name}</c> (metastore.md §7).
/// Getting a register performs no I/O.
/// </remarks>
public sealed class AzureBlobMetaStore : IMetaStore
{
    private readonly BlobContainerClient _container;
    private readonly string _prefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBlobMetaStore"/> class.
    /// </summary>
    /// <param name="container">The container which holds the registers.</param>
    /// <param name="prefix">The blob-name prefix, empty or ending in <c>/</c>.</param>
    public AzureBlobMetaStore(BlobContainerClient container, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        prefix ??= string.Empty;
        if (prefix.Length > 0 && prefix[^1] != MetaStoreRegisterName.Separator)
        {
            throw new ArgumentException("A non-empty Azure Blob MetaStore prefix must end in '/'.", nameof(prefix));
        }

        _container = container;
        _prefix = prefix;
    }

    /// <inheritdoc/>
    public ValueTask<IConditionalRegister> GetRegisterAsync(
        Guid cluster,
        string name,
        CancellationToken cancellationToken = default)
    {
        var blobName = GetBlobName(_prefix, cluster, name);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IConditionalRegister>(
            new AzureBlobConditionalRegister(_container.GetBlobClient(blobName)));
    }

    /// <summary>
    /// Forms the blob name of the register keyed <paramref name="cluster"/> and <paramref name="name"/>.
    /// </summary>
    internal static string GetBlobName(string prefix, Guid cluster, string name) =>
        string.Concat(prefix, MetaStoreRegisterName.ToKey(cluster, name));
}

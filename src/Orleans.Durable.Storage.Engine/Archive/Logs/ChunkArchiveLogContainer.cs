using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// A container of archive logs on the chunk layout (archive-logs.md §7.4) in one object store, a bucket or a directory:
/// each store prefix holds its logs and durable storage's objects under <c>{prefix}/</c>, so clusters which share the
/// object store under distinct prefixes see only their own.
/// </summary>
/// <param name="objects">The object store, which the prefixes share.</param>
public sealed class ChunkArchiveLogContainer(IObjectStore objects) : IArchiveLogContainer
{
    private readonly IObjectStore _objects = objects ?? throw new ArgumentNullException(nameof(objects));

    /// <inheritdoc/>
    /// <remarks>The chunk layout records no format: its objects hold the content alone.</remarks>
    public IArchiveLogStore OpenLogs(string prefix, string format) => new ChunkArchiveLogStore(OpenObjects(prefix));

    /// <inheritdoc/>
    public IObjectStore OpenObjects(string prefix) =>
        ArchiveStorePrefix.IsValid(prefix)
            ? new PrefixedObjectStore(_objects, prefix + "/")
            : throw new ArgumentException($"'{prefix}' is no store prefix.", nameof(prefix));
}

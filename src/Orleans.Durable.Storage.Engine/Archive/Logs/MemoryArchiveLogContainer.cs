using System.Collections.Concurrent;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// A container of archive logs in memory, for tests and simulation: each store prefix holds logs of its own, a
/// <see cref="MemoryArchiveLogStore"/>, and durable storage's objects under it in one shared object store, so clusters which share the
/// container under distinct prefixes see only their own logs and objects.
/// </summary>
/// <param name="objects">The container's object store, which the prefixes share.</param>
/// <param name="layout">The layout of each prefix's logs, or the memory store's default.</param>
public sealed class MemoryArchiveLogContainer(IObjectStore objects, ArchiveLogLayout? layout = null) : IArchiveLogContainer
{
    private readonly IObjectStore _objects = objects ?? throw new ArgumentNullException(nameof(objects));
    private readonly ConcurrentDictionary<string, MemoryArchiveLogStore> _logs = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    /// <remarks>The memory store records no format.</remarks>
    public IArchiveLogStore OpenLogs(string prefix, string format) => LogsOf(prefix);

    /// <inheritdoc/>
    public IObjectStore OpenObjects(string prefix) => new PrefixedObjectStore(_objects, Checked(prefix) + "/");

    /// <summary>Returns the logs under <paramref name="prefix"/>.</summary>
    /// <param name="prefix">The store prefix.</param>
    /// <returns>The prefix's logs, the same store every time.</returns>
    public MemoryArchiveLogStore LogsOf(string prefix) => _logs.GetOrAdd(Checked(prefix), _ => new MemoryArchiveLogStore(layout));

    private static string Checked(string prefix) =>
        ArchiveStorePrefix.IsValid(prefix) ? prefix : throw new ArgumentException($"'{prefix}' is no store prefix.", nameof(prefix));
}

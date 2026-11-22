using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Objects;

/// <summary>
/// An object store of both interfaces over a memory store, whose calls a test overrides to observe, hold, or change them.
/// </summary>
internal class ForwardingObjectStore : IObjectStore, IAppendableObjectStore
{
    /// <summary>Gets the memory store the calls reach.</summary>
    public InMemoryObjectStore Inner { get; } = new();

    public virtual ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        Inner.CreateAsync(name, content, crc32c, cancellationToken);

    public virtual ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
        Inner.ReadAsync(name, offset, length, cancellationToken);

    public virtual ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        Inner.GetPropertiesAsync(name, cancellationToken);

    public virtual IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
        Inner.ListAsync(prefix, startAfter, cancellationToken);

    public virtual ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        Inner.DeleteAsync(name, cancellationToken);

    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        Inner.ReadVersionedAsync(name, cancellationToken);

    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        Inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);

    public virtual ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        Inner.CreateAppendableAsync(name, cancellationToken);

    public virtual ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        Inner.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);

    public virtual ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        Inner.SealAsync(name, cancellationToken);

    public virtual ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        Inner.ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken);

    /// <summary>Returns the names under <paramref name="prefix"/>, in order.</summary>
    public async Task<List<string>> NamesAsync(string prefix)
    {
        List<string> names = [];
        await foreach (var listed in Inner.ListAsync(prefix, cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(listed.Name.Value);
        }

        return names;
    }
}

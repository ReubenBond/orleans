using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The test's object store as the composition sees it under one cluster's store prefix: every name the composition uses
/// must lie under <c>{prefix}</c>, which a defect that bypassed the prefix would break, and the inner store holds each
/// object under its name without the prefix, so a test reads the cluster's objects by their own names.
/// </summary>
internal sealed class PrefixStrippingObjectStore(IObjectStore inner, string prefix) : IObjectStore
{
    public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        inner.CreateAsync(Strip(name), content, crc32c, cancellationToken);

    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(Strip(name), offset, length, cancellationToken);

    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.GetPropertiesAsync(Strip(name), cancellationToken);

    public async IAsyncEnumerable<ListedObject> ListAsync(string listing, string? startAfter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!listing.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The composition listed '{listing}', outside its store prefix '{prefix}'.");
        }

        await foreach (var listed in inner.ListAsync(listing[prefix.Length..], cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var name = prefix + listed.Name.Value;
            if (ObjectStoreArguments.IsAfter(name, startAfter))
            {
                yield return listed with { Name = new ObjectName(name) };
            }
        }
    }

    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(Strip(name), cancellationToken);

    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.ReadVersionedAsync(Strip(name), cancellationToken);

    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        inner.ReplaceAsync(Strip(name), expectedVersion, content, crc32c, cancellationToken);

    private ObjectName Strip(ObjectName name) =>
        name.Value.StartsWith(prefix, StringComparison.Ordinal)
            ? new ObjectName(name.Value[prefix.Length..])
            : throw new InvalidOperationException($"The composition named '{name}', outside its store prefix '{prefix}'.");
}

/// <summary>
/// The test's archive-log container of one cluster: it opens the test's logs and durable storage's objects for the cluster's store
/// prefix alone, placing durable storage's objects under the prefix in the bucket's object store, as a real container does, and
/// records the journal format the composition opened the logs with.
/// </summary>
/// <param name="prefix">The cluster's store prefix.</param>
/// <param name="logs">Opens the cluster's logs.</param>
/// <param name="bucket">Opens the bucket's object store, which durable storage's objects lie in under the prefix.</param>
internal sealed class TestArchiveLogContainer(string prefix, Func<IArchiveLogStore> logs, Func<IObjectStore> bucket) : IArchiveLogContainer
{
    /// <summary>Gets the journal format the composition last opened the logs with.</summary>
    public string? Format { get; private set; }

    public IArchiveLogStore OpenLogs(string opened, string format)
    {
        Check(opened);
        Format = format;
        return logs();
    }

    public IObjectStore OpenObjects(string opened)
    {
        Check(opened);
        return new PrefixedObjectStore(bucket(), prefix + "/");
    }

    private void Check(string opened)
    {
        if (opened != prefix)
        {
            throw new InvalidOperationException($"The composition opened its archive logs under '{opened}', not its store prefix '{prefix}'.");
        }
    }
}

/// <summary>An object store which records the name of every create and replace it is given, then passes the call on.</summary>
internal sealed class WriteRecordingObjectStore(IObjectStore inner) : IObjectStore
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _written = new();

    /// <summary>Gets the names of every create and replace, in order.</summary>
    public IReadOnlyCollection<string> Written => _written;

    public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        _written.Enqueue(name.Value);
        return inner.CreateAsync(name, content, crc32c, cancellationToken);
    }

    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(name, offset, length, cancellationToken);

    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.GetPropertiesAsync(name, cancellationToken);

    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, startAfter, cancellationToken);

    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);

    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.ReadVersionedAsync(name, cancellationToken);

    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        _written.Enqueue(name.Value);
        return inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }
}

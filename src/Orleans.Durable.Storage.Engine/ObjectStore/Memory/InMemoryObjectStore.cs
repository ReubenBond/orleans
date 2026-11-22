using System.Buffers;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.ObjectStore.Memory;

/// <summary>
/// An <see cref="IObjectStore"/> and <see cref="IAppendableObjectStore"/> in this process's memory, for tests and
/// simulation (object-store.md §9).
/// </summary>
/// <remarks>
/// Its objects last as long as the store, and may be empty. A create, replace, append, or tombstone copies its content
/// and checks the copy against its writer's CRC-32C, so the store keeps exactly the bytes it checked, and keeps the copy
/// only if the write applies; every applied write gives the object a version it never had, so a version names a write,
/// never only content. An append stores a new array holding the object's bytes and its own, so reads return views of the
/// stored bytes, which no later write changes. Each operation takes effect atomically under one lock, so a listing is
/// exact.
/// </remarks>
public sealed class InMemoryObjectStore : IObjectStore, IAppendableObjectStore
{
    private readonly Lock _sync = new();
    private readonly SortedDictionary<string, StoredObject> _objects = [with(StringComparer.Ordinal)];
    private long _lastVersion;

    /// <inheritdoc/>
    public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var copy = Copy(name, content, crc32c);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_objects.ContainsKey(name.Value))
            {
                return ValueTask.FromResult<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance);
            }

            _objects[name.Value] = new StoredObject(copy, NextVersion(), Appendability.None);
            return ValueTask.FromResult<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance);
        }
    }

    /// <inheritdoc/>
    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotRange(offset, length);
        cancellationToken.ThrowIfCancellationRequested();
        StoredObject? stored;
        lock (_sync)
        {
            stored = _objects.GetValueOrDefault(name.Value);
        }

        if (stored is null)
        {
            return ValueTask.FromResult<ArcBuffer?>(null);
        }

        ObjectStoreArguments.ThrowIfPastEnd(name, offset, length, stored.Content.Length);
        return ValueTask.FromResult<ArcBuffer?>(ArcBuffer.Wrap(stored.Content.AsMemory((int)offset, length)));
    }

    /// <inheritdoc/>
    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return ValueTask.FromResult(_objects.GetValueOrDefault(name.Value) is { } stored ? PropertiesOf(stored) : null);
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        ObjectStoreArguments.ThrowIfNotPrefix(prefix);
        ObjectStoreArguments.ThrowIfNotStart(startAfter);
        return Listing(prefix, startAfter).ToAsyncEnumerable();
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _ = _objects.Remove(name.Value);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        StoredObject? stored;
        lock (_sync)
        {
            stored = _objects.GetValueOrDefault(name.Value);
        }

        return ValueTask.FromResult<VersionedObject?>(stored is null ? null : new VersionedObject(ArcBuffer.Wrap(stored.Content), stored.Version));
    }

    /// <inheritdoc/>
    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfEmpty(content);
        var copy = Copy(name, content, crc32c);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_objects.GetValueOrDefault(name.Value)?.Version != expectedVersion)
            {
                return ValueTask.FromResult<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance);
            }

            var version = NextVersion();
            _objects[name.Value] = new StoredObject(copy, version, Appendability.None);
            return ValueTask.FromResult<ObjectReplaceOutcome>(new ObjectReplaceOutcome.Applied(version));
        }
    }

    /// <inheritdoc/>
    public ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_objects.ContainsKey(name.Value))
            {
                return ValueTask.FromResult<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance);
            }

            _objects[name.Value] = new StoredObject([], NextVersion(), Appendability.Open);
            return ValueTask.FromResult<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance);
        }
    }

    /// <inheritdoc/>
    public ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotAppend(expectedLength, content);
        var copy = Copy(name, content, crc32c);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_objects.GetValueOrDefault(name.Value) is not { } stored)
            {
                return ValueTask.FromResult<ObjectAppendOutcome>(ObjectAppendOutcome.Absent.Instance);
            }

            if (stored.Appendability != Appendability.Open)
            {
                return ValueTask.FromResult(SealedAnswer(stored, expectedLength, copy));
            }

            if (stored.Content.Length != expectedLength)
            {
                return ValueTask.FromResult<ObjectAppendOutcome>(new ObjectAppendOutcome.LengthMismatch(stored.Content.Length));
            }

            byte[] appended = [.. stored.Content, .. copy];
            _objects[name.Value] = new StoredObject(appended, NextVersion(), Appendability.Open);
            return ValueTask.FromResult<ObjectAppendOutcome>(new ObjectAppendOutcome.Appended(appended.Length));
        }
    }

    /// <inheritdoc/>
    public ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_objects.GetValueOrDefault(name.Value) is not { } stored)
            {
                return ValueTask.FromResult<ObjectSealOutcome>(ObjectSealOutcome.Absent.Instance);
            }

            if (stored.Appendability == Appendability.None)
            {
                return ValueTask.FromResult<ObjectSealOutcome>(ObjectSealOutcome.NotAppendable.Instance);
            }

            _objects[name.Value] = stored with { Appendability = Appendability.Sealed };
            return ValueTask.FromResult<ObjectSealOutcome>(ObjectSealOutcome.Sealed.Instance);
        }
    }

    /// <inheritdoc/>
    public ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var copy = Copy(name, content, crc32c);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _objects[name.Value] = new StoredObject(copy, NextVersion(), Appendability.None);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns what an append to an object which is not open answers: <see cref="ObjectAppendOutcome.Appended"/> when it
    /// is sealed and holds the append's bytes at its expected length, otherwise <see cref="ObjectAppendOutcome.Sealed"/>.
    /// </summary>
    private static ObjectAppendOutcome SealedAnswer(StoredObject stored, long expectedLength, byte[] content) =>
        stored.Appendability == Appendability.Sealed
        && stored.Content.Length - content.Length >= expectedLength
        && stored.Content.AsSpan((int)expectedLength, content.Length).SequenceEqual(content)
            ? new ObjectAppendOutcome.Appended(expectedLength + content.Length)
            : ObjectAppendOutcome.Sealed.Instance;

    /// <summary>
    /// Returns a copy of <paramref name="content"/>, which matches its writer's <paramref name="crc32c"/>.
    /// </summary>
    /// <exception cref="ObjectContentChangedException">The copy does not match <paramref name="crc32c"/>.</exception>
    private static byte[] Copy(ObjectName name, ArcBuffer content, uint? crc32c)
    {
        var copy = content.ToArray();
        ObjectStoreChecksums.ThrowIfChanged(name, new ReadOnlySequence<byte>(copy), crc32c);
        return copy;
    }

    /// <summary>
    /// Returns the objects with <paramref name="prefix"/> as they are when the listing is first read.
    /// </summary>
    private IEnumerable<ListedObject> Listing(string prefix, string? startAfter)
    {
        ListedObject[] listed;
        lock (_sync)
        {
            listed = [.. _objects.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) && ObjectStoreArguments.IsAfter(entry.Key, startAfter)).Select(static entry => new ListedObject(new ObjectName(entry.Key), entry.Value.Content.Length))];
        }

        foreach (var entry in listed)
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Returns a stored object's properties: its length, and its CRC-32C when it is neither empty nor appendable.
    /// </summary>
    private static ObjectProperties PropertiesOf(StoredObject stored) =>
        new(
            stored.Content.Length,
            stored.Content.Length == 0 || stored.Appendability != Appendability.None ? null : ObjectStoreChecksums.Crc32C(new ReadOnlySequence<byte>(stored.Content)));

    private ObjectVersion NextVersion() => new((++_lastVersion).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Whether a stored object is appendable, and if so whether it is open.
    /// </summary>
    private enum Appendability
    {
        /// <summary>Created or replaced whole: appends answer sealed.</summary>
        None,

        /// <summary>Appendable and open.</summary>
        Open,

        /// <summary>Appendable and sealed.</summary>
        Sealed,
    }

    private sealed record StoredObject(byte[] Content, ObjectVersion Version, Appendability Appendability);
}

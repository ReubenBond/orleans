using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Lightweight in-memory <see cref="IObjectStore"/> and <see cref="IAppendableObjectStore"/> that tracks object
/// lengths without retaining written payload bytes, keeping repeated write and coalescing benchmarks bounded in memory.
/// </summary>
internal sealed class DiscardObjectStore(bool yieldOnWrite) : IObjectStore, IAppendableObjectStore
{
    private static readonly ObjectVersion BenchmarkVersion = new("1");
    private readonly ConcurrentDictionary<string, ObjectState> _objects = new(StringComparer.Ordinal);
    private long _appendCount;

    public long AppendCount => Volatile.Read(ref _appendCount);

    public void Clear()
    {
        _objects.Clear();
        Volatile.Write(ref _appendCount, 0);
    }

    public async ValueTask<ObjectCreateOutcome> CreateAsync(
        ObjectName name,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (yieldOnWrite)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        var state = new ObjectState(content.Length, crc32c, BenchmarkVersion, appendable: false);
        return _objects.TryAdd(name.Value, state)
            ? ObjectCreateOutcome.Created.Instance
            : ObjectCreateOutcome.Exists.Instance;
    }

    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("DiscardObjectStore does not model ReplaceAsync.");
    }

    public ValueTask<ArcBuffer?> ReadAsync(
        ObjectName name,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("DiscardObjectStore does not retain payload bytes for ReadAsync.");
    }

    public ValueTask<VersionedObject?> ReadVersionedAsync(
        ObjectName name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("DiscardObjectStore does not retain payload bytes for ReadVersionedAsync.");
    }

    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_objects.TryGetValue(name.Value, out var state))
        {
            return ValueTask.FromResult<ObjectProperties?>(null);
        }

        lock (state.Gate)
        {
            return ValueTask.FromResult<ObjectProperties?>(
                new ObjectProperties(state.Length, state.Appendable ? null : state.Crc32c));
        }
    }

    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("DiscardObjectStore does not model DeleteAsync.");
    }

    public IAsyncEnumerable<ListedObject> ListAsync(
        string prefix,
        string? startAfter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("DiscardObjectStore does not model ListAsync.");
    }

    public async ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (yieldOnWrite)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        var state = new ObjectState(0, crc32c: null, BenchmarkVersion, appendable: true);
        return _objects.TryAdd(name.Value, state)
            ? ObjectCreateOutcome.Created.Instance
            : ObjectCreateOutcome.Exists.Instance;
    }

    public async ValueTask<ObjectAppendOutcome> AppendAsync(
        ObjectName name,
        long expectedLength,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (yieldOnWrite)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!_objects.TryGetValue(name.Value, out var state))
        {
            return ObjectAppendOutcome.Absent.Instance;
        }

        lock (state.Gate)
        {
            if (!state.Appendable || state.Sealed)
            {
                return ObjectAppendOutcome.Sealed.Instance;
            }

            if (state.Length != expectedLength)
            {
                return new ObjectAppendOutcome.LengthMismatch(state.Length);
            }

            state.Length += content.Length;
            state.Version = BenchmarkVersion;
            _ = Interlocked.Increment(ref _appendCount);
            return new ObjectAppendOutcome.Appended(state.Length);
        }
    }

    public ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_objects.TryGetValue(name.Value, out var state))
        {
            return ValueTask.FromResult<ObjectSealOutcome>(ObjectSealOutcome.Absent.Instance);
        }

        lock (state.Gate)
        {
            if (!state.Appendable)
            {
                return ValueTask.FromResult<ObjectSealOutcome>(ObjectSealOutcome.NotAppendable.Instance);
            }

            state.Sealed = true;
            return ValueTask.FromResult<ObjectSealOutcome>(ObjectSealOutcome.Sealed.Instance);
        }
    }

    public ValueTask ReplaceWithTombstoneAsync(
        ObjectName name,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("DiscardObjectStore does not model ReplaceWithTombstoneAsync.");
    }

    private sealed class ObjectState(long length, uint? crc32c, ObjectVersion version, bool appendable)
    {
        public readonly Lock Gate = new();
        public long Length = length;
        public uint? Crc32c = crc32c;
        public ObjectVersion Version = version;
        public bool Appendable = appendable;
        public bool Sealed;
    }
}

/// <summary>
/// Thread-safe, resettable copy-on-write overlay over an immutable pre-populated <see cref="InMemoryObjectStore"/>
/// whose base listing is pre-sorted once in setup so <see cref="ListAsync"/> streams a lazy <c>O(log N + K)</c>
/// merge without materializing all <c>N</c> base entries.
/// </summary>
internal sealed class SnapshotOverlayObjectStore : IObjectStore, IAppendableObjectStore
{
    private readonly InMemoryObjectStore _baseStore;
    private readonly ListedObject[] _sortedBase;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, OverlayObject> _overlay = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _sealedBaseNames = [with(StringComparer.Ordinal)];

    private SnapshotOverlayObjectStore(InMemoryObjectStore baseStore, ListedObject[] sortedBase)
    {
        _baseStore = baseStore;
        _sortedBase = sortedBase;
    }

    public static async Task<SnapshotOverlayObjectStore> CreateAsync(
        InMemoryObjectStore baseStore,
        CancellationToken cancellationToken = default)
    {
        List<ListedObject> items = [];
        await foreach (var item in baseStore.ListAsync(string.Empty, startAfter: null, cancellationToken).ConfigureAwait(false))
        {
            items.Add(item);
        }

        items.Sort(static (left, right) => string.CompareOrdinal(left.Name.Value, right.Name.Value));
        return new SnapshotOverlayObjectStore(baseStore, [.. items]);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _overlay.Clear();
            _sealedBaseNames.Clear();
        }
    }

    public ValueTask<ObjectCreateOutcome> CreateAsync(
        ObjectName name,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ContainsInBase(name.Value))
        {
            return ValueTask.FromResult<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance);
        }

        var bytes = CopyBytes(content);
        lock (_gate)
        {
            if (_overlay.ContainsKey(name.Value))
            {
                return ValueTask.FromResult<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance);
            }

            _overlay.Add(name.Value, new OverlayObject(bytes, crc32c));
            return ValueTask.FromResult<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance);
        }
    }

    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("SnapshotOverlayObjectStore does not model ReplaceAsync.");
    }

    public ValueTask<ArcBuffer?> ReadAsync(
        ObjectName name,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectStoreArguments.ThrowIfNotRange(offset, length);

        OverlayObject? overlayEntry;
        lock (_gate)
        {
            _ = _overlay.TryGetValue(name.Value, out overlayEntry);
        }

        if (overlayEntry is null)
        {
            return _baseStore.ReadAsync(name, offset, length, cancellationToken);
        }

        if (offset > overlayEntry.Bytes.Length || length > overlayEntry.Bytes.Length - offset)
        {
            throw new EndOfStreamException(
                $"Range offset={offset}, length={length} exceeds object length {overlayEntry.Bytes.Length}.");
        }

        var copy = new byte[length];
        overlayEntry.Bytes.AsSpan((int)offset, length).CopyTo(copy);
        return ValueTask.FromResult<ArcBuffer?>(ArcBuffer.Wrap(copy));
    }

    public ValueTask<VersionedObject?> ReadVersionedAsync(
        ObjectName name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("SnapshotOverlayObjectStore does not model ReadVersionedAsync.");
    }

    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OverlayObject? overlayEntry;
        lock (_gate)
        {
            _ = _overlay.TryGetValue(name.Value, out overlayEntry);
        }

        return overlayEntry is not null
            ? ValueTask.FromResult<ObjectProperties?>(new ObjectProperties(overlayEntry.Bytes.Length, overlayEntry.Crc32c))
            : _baseStore.GetPropertiesAsync(name, cancellationToken);
    }

    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("SnapshotOverlayObjectStore does not model DeleteAsync.");
    }

    public async IAsyncEnumerable<ListedObject> ListAsync(
        string prefix,
        string? startAfter = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);

        List<ListedObject>? overlayItems = null;
        lock (_gate)
        {
            if (_overlay.Count > 0)
            {
                foreach (var (key, value) in _overlay)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal)
                        && (startAfter is null || string.CompareOrdinal(key, startAfter) > 0))
                    {
                        (overlayItems ??= []).Add(new ListedObject(new ObjectName(key), value.Bytes.Length));
                    }
                }
            }
        }

        if (overlayItems is { Count: > 1 })
        {
            overlayItems.Sort(static (left, right) => string.CompareOrdinal(left.Name.Value, right.Name.Value));
        }

        var baseIndex = FindBaseStartIndex(prefix, startAfter);
        var overlayIndex = 0;
        var overlayCount = overlayItems?.Count ?? 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasBase = baseIndex < _sortedBase.Length
                && _sortedBase[baseIndex].Name.Value.StartsWith(prefix, StringComparison.Ordinal);
            var hasOverlay = overlayIndex < overlayCount;

            if (!hasBase && !hasOverlay)
            {
                yield break;
            }

            if (hasBase && hasOverlay)
            {
                var baseItem = _sortedBase[baseIndex];
                var overlayItem = overlayItems![overlayIndex];
                var cmp = string.CompareOrdinal(baseItem.Name.Value, overlayItem.Name.Value);
                if (cmp < 0)
                {
                    baseIndex++;
                    yield return baseItem;
                }
                else if (cmp > 0)
                {
                    overlayIndex++;
                    yield return overlayItem;
                }
                else
                {
                    baseIndex++;
                    overlayIndex++;
                    yield return overlayItem;
                }
            }
            else if (hasBase)
            {
                yield return _sortedBase[baseIndex++];
            }
            else
            {
                yield return overlayItems![overlayIndex++];
            }
        }
    }

    public ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("SnapshotOverlayObjectStore does not model CreateAppendableAsync.");
    }

    public ValueTask<ObjectAppendOutcome> AppendAsync(
        ObjectName name,
        long expectedLength,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("SnapshotOverlayObjectStore does not model AppendAsync.");
    }

    public async ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_overlay.ContainsKey(name.Value))
            {
                return ObjectSealOutcome.NotAppendable.Instance;
            }

            if (_sealedBaseNames.Contains(name.Value))
            {
                return ObjectSealOutcome.Sealed.Instance;
            }
        }

        if (!ContainsInBase(name.Value))
        {
            return ObjectSealOutcome.Absent.Instance;
        }

        var properties = await _baseStore.GetPropertiesAsync(name, cancellationToken).ConfigureAwait(false);
        if (properties is null)
        {
            return ObjectSealOutcome.Absent.Instance;
        }

        lock (_gate)
        {
            _ = _sealedBaseNames.Add(name.Value);
        }

        return ObjectSealOutcome.Sealed.Instance;
    }

    public ValueTask ReplaceWithTombstoneAsync(
        ObjectName name,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("SnapshotOverlayObjectStore does not model ReplaceWithTombstoneAsync.");
    }

    private bool ContainsInBase(string key)
    {
        var low = 0;
        var high = _sortedBase.Length - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var cmp = string.CompareOrdinal(_sortedBase[mid].Name.Value, key);
            if (cmp == 0)
            {
                return true;
            }

            if (cmp < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return false;
    }

    private int FindBaseStartIndex(string prefix, string? startAfter)
    {
        var useStrict = startAfter is not null && string.CompareOrdinal(startAfter, prefix) >= 0;
        var target = useStrict ? startAfter : prefix;

        var low = 0;
        var high = _sortedBase.Length;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            var cmp = string.CompareOrdinal(_sortedBase[mid].Name.Value, target);
            if (useStrict ? cmp <= 0 : cmp < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static byte[] CopyBytes(ArcBuffer content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var destination = new byte[content.Length];
        _ = content.CopyTo(destination);
        return destination;
    }

    private sealed class OverlayObject(byte[] bytes, uint? crc32c)
    {
        public byte[] Bytes { get; } = bytes;
        public uint? Crc32c { get; } = crc32c;
    }
}

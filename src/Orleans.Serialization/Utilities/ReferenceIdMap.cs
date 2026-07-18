using System;
using System.Runtime.CompilerServices;

#nullable disable
namespace Orleans.Serialization.Utilities;

/// <summary>
/// A hash map from an unsigned reference identifier to an object, specialized for the deserialization
/// reference table. Uses a generation counter so that <see cref="Reset"/> is an O(1) operation which does
/// not clear the backing storage, avoiding repeated large memory-zeroing costs when a pooled session is reused.
/// Entries are stored in insertion order to preserve the semantics required by the reference-tracking codecs.
/// </summary>
internal sealed class ReferenceIdMap
{
    private Bucket[] _buckets = [];
    private Entry[] _entries = [];
    private int _count;
    private int _generation = 1;

    /// <summary>
    /// Gets the number of live entries.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Attempts to get the value associated with the specified reference id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(uint key, out object value)
    {
        if (_buckets.Length != 0)
        {
            ref var bucket = ref _buckets[(int)key & (_buckets.Length - 1)];
            if (bucket.Generation == _generation)
            {
                for (var index = bucket.EntryIndex; index >= 0; index = _entries[index].Next)
                {
                    ref var entry = ref _entries[index];
                    if (entry.Key == key)
                    {
                        value = entry.Value;
                        return true;
                    }
                }
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Gets a reference to the value slot for the specified key, adding a new entry if the key is not present.
    /// </summary>
    /// <param name="key">The reference id.</param>
    /// <param name="exists">Whether the key already existed.</param>
    /// <returns>A reference to the value slot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref object GetValueRefOrAddDefault(uint key, out bool exists)
    {
        if (_count == _entries.Length)
        {
            Resize();
        }

        ref var bucket = ref _buckets[(int)key & (_buckets.Length - 1)];
        var next = -1;
        if (bucket.Generation == _generation)
        {
            next = bucket.EntryIndex;
            for (var index = next; index >= 0; index = _entries[index].Next)
            {
                ref var existing = ref _entries[index];
                if (existing.Key == key)
                {
                    exists = true;
                    return ref existing.Value;
                }
            }
        }

        var entryIndex = _count++;
        ref var entry = ref _entries[entryIndex];
        entry.Key = key;
        entry.Next = next;
        entry.Value = null;
        bucket.Generation = _generation;
        bucket.EntryIndex = entryIndex;
        exists = false;
        return ref entry.Value;
    }

    /// <summary>
    /// Gets the insertion-order index of the entry whose value is reference-equal to <paramref name="value"/>,
    /// or <c>-1</c> if not present.
    /// </summary>
    public int IndexOfValue(object value)
    {
        for (var i = 0; i < _count; i++)
        {
            if (ReferenceEquals(_entries[i].Value, value))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Invokes the provided action for each live entry, in insertion order.
    /// </summary>
    public void ForEach<TState>(TState state, Action<TState, uint, object> action)
    {
        for (var i = 0; i < _count; i++)
        {
            ref var entry = ref _entries[i];
            action(state, entry.Key, entry.Value);
        }
    }

    /// <summary>
    /// Resets the map so that it contains no entries, without clearing the backing storage.
    /// </summary>
    public void Reset()
    {
        // Release object references so they can be collected, but leave the buckets untouched.
        for (var i = 0; i < _count; i++)
        {
            _entries[i].Value = null;
        }

        _count = 0;
        if (_generation == int.MaxValue)
        {
#if NET6_0_OR_GREATER
            Array.Clear(_buckets);
#else
            Array.Clear(_buckets, 0, _buckets.Length);
#endif
            _generation = 1;
        }
        else
        {
            _generation++;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Resize()
    {
        var newSize = _entries.Length == 0 ? 8 : _entries.Length * 2;
        var newBuckets = new Bucket[newSize];
        var newEntries = new Entry[newSize];
        Array.Copy(_entries, newEntries, _count);
        for (var i = 0; i < _count; i++)
        {
            _entries[i].Value = null;
        }

        _generation = 1;
        for (var i = 0; i < _count; i++)
        {
            ref var entry = ref newEntries[i];
            ref var bucket = ref newBuckets[(int)entry.Key & (newSize - 1)];
            entry.Next = bucket.Generation == _generation ? bucket.EntryIndex : -1;
            bucket.Generation = _generation;
            bucket.EntryIndex = i;
        }

        _buckets = newBuckets;
        _entries = newEntries;
    }

    private struct Bucket
    {
        public int Generation;
        public int EntryIndex;
    }

    private struct Entry
    {
        public uint Key;
        public int Next;
        public object Value;
    }
}

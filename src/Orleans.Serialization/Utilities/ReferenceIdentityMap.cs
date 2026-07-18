using System;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Utilities;

internal sealed class ReferenceIdentityMap<TValue>
{
    private Bucket[] _buckets = [];
    private Entry[] _entries = [];
    private int _count;
    private int _generation = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(object key, out TValue value)
    {
        if (_buckets.Length != 0)
        {
            var hashCode = RuntimeHelpers.GetHashCode(key);
            ref var bucket = ref _buckets[hashCode & (_buckets.Length - 1)];
            if (bucket.Generation == _generation)
            {
                for (var index = bucket.EntryIndex; index >= 0; index = _entries[index].Next)
                {
                    ref var entry = ref _entries[index];
                    if (entry.HashCode == hashCode && ReferenceEquals(entry.Key, key))
                    {
                        value = entry.Value;
                        return true;
                    }
                }
            }
        }

        value = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(object key, TValue value)
    {
        if (_count == _entries.Length)
        {
            Resize();
        }

        var hashCode = RuntimeHelpers.GetHashCode(key);
        ref var bucket = ref _buckets[hashCode & (_buckets.Length - 1)];
        var next = -1;
        if (bucket.Generation == _generation)
        {
            next = bucket.EntryIndex;
            for (var index = next; index >= 0; index = _entries[index].Next)
            {
                ref var existing = ref _entries[index];
                if (existing.HashCode == hashCode && ReferenceEquals(existing.Key, key))
                {
                    existing.Value = value;
                    return;
                }
            }
        }

        var entryIndex = _count++;
        _entries[entryIndex] = new Entry
        {
            HashCode = hashCode,
            Next = next,
            Key = key,
            Value = value,
        };
        bucket.Generation = _generation;
        bucket.EntryIndex = entryIndex;
    }

    public void Reset()
    {
        for (var i = 0; i < _count; i++)
        {
            _entries[i].Key = null;
            _entries[i].Value = default!;
        }

        _count = 0;
        if (_generation == int.MaxValue)
        {
            Array.Clear(_buckets, 0, _buckets.Length);
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
            _entries[i].Key = null;
            _entries[i].Value = default!;
        }

        _generation = 1;
        for (var i = 0; i < _count; i++)
        {
            ref var entry = ref newEntries[i];
            ref var bucket = ref newBuckets[entry.HashCode & (newSize - 1)];
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
        public int HashCode;
        public int Next;
        public object? Key;
        public TValue Value;
    }
}

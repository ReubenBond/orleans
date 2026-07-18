using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;
using Orleans.Caching;

#nullable disable
namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// A serializer for <see cref="IdSpan"/> which caches values and avoids re-encoding and unnecessary allocations.
    /// </summary>
    internal sealed class CachingIdSpanCodec
    {
        private const int MaxPrivateCacheSize = 1024;
        private const int RecentCacheSize = 8;
        private static readonly ConcurrentLruCache<IdSpan, IdSpan> SharedCache = new(capacity: 128_000);

        // Purge entries which have not been accessed in over 2 minutes. 
        private const long PurgeAfterMilliseconds = 2 * 60 * 1000;

        // Scan for entries which are expired every minute
        private const long GarbageCollectionIntervalMilliseconds = 60 * 1000;

        private readonly Dictionary<int, (byte[] Value, long LastSeen)> _cache = new();
        private readonly (int HashCode, byte[] Value, long LastSeen)[] _recentCache = new (int, byte[], long)[RecentCacheSize];
        private readonly (int HashCode, byte[] Value)[] _recentWrites = new (int, byte[])[RecentCacheSize];
        private long _lastGarbageCollectionTimestamp;
        private int _recentHitCount;

        public CachingIdSpanCodec()
        {
            _lastGarbageCollectionTimestamp = Environment.TickCount64;
        }

        public IdSpan ReadRaw<TInput>(ref Reader<TInput> reader)
        {
            var length = reader.ReadVarUInt32();
            if (length == 0)
                return default;

            var hashCode = reader.ReadInt32();

            IdSpan result = default;
            byte[] payloadArray = default;
            if (!reader.TryReadBytes((int)length, out var payloadSpan))
            {
                payloadSpan = payloadArray = reader.ReadBytes(length);
            }

            ref var recentEntry = ref Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(_recentCache),
                hashCode & (RecentCacheSize - 1));
            if (recentEntry.Value is { } recentValue
                && recentEntry.HashCode == hashCode
                && payloadSpan.SequenceEqual(recentValue))
            {
                if ((++_recentHitCount & 1023) == 0)
                {
                    RefreshRecentEntry(ref recentEntry);
                }

                result = IdSpan.UnsafeCreate(recentValue, hashCode);
            }
            else
            {
                var currentTimestamp = Environment.TickCount64;
                if (_cache.Count >= MaxPrivateCacheSize)
                {
                    PurgeStaleEntries();
                    if (_cache.Count >= MaxPrivateCacheSize)
                    {
                        _cache.Clear();
                    }
                }

                ref var cacheEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(_cache, hashCode, out var exists);
                if (exists && payloadSpan.SequenceEqual(cacheEntry.Value))
                {
                    result = IdSpan.UnsafeCreate(cacheEntry.Value, hashCode);
                }
                else
                {
                    result = IdSpan.UnsafeCreate(payloadArray ?? payloadSpan.ToArray(), hashCode);

                    // Before adding this value to the private cache and returning it, intern it via the shared cache to hopefully reduce duplicates.
                    result = SharedCache.GetOrAdd(result, static (key, _) => key, (object)null);

                    // Update the cache. If there is a hash collision, the last entry wins.
                    cacheEntry.Value = IdSpan.UnsafeGetArray(result);
                }

                cacheEntry.LastSeen = currentTimestamp;
                recentEntry = (hashCode, cacheEntry.Value, currentTimestamp);

                // Perform periodic maintenance to prevent unbounded memory leaks.
                if (currentTimestamp - _lastGarbageCollectionTimestamp > GarbageCollectionIntervalMilliseconds)
                {
                    PurgeStaleEntries();
                    _lastGarbageCollectionTimestamp = currentTimestamp;
                }
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RefreshRecentEntry(ref (int HashCode, byte[] Value, long LastSeen) entry)
        {
            var currentTimestamp = Environment.TickCount64;
            entry.LastSeen = currentTimestamp;
            if (currentTimestamp - _lastGarbageCollectionTimestamp > GarbageCollectionIntervalMilliseconds)
            {
                PurgeStaleEntries();
                _lastGarbageCollectionTimestamp = currentTimestamp;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PurgeStaleEntries()
        {
            var currentTimestamp = Environment.TickCount64;
            foreach (var entry in _cache)
            {
                if (currentTimestamp - entry.Value.LastSeen > PurgeAfterMilliseconds)
                {
                    _cache.Remove(entry.Key);
                }
            }

            for (var i = 0; i < _recentCache.Length; i++)
            {
                if (currentTimestamp - _recentCache[i].LastSeen > PurgeAfterMilliseconds)
                {
                    _recentCache[i] = default;
                }
            }
        }

        public void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, IdSpan value) where TBufferWriter : IBufferWriter<byte>
        {
            IdSpanCodec.WriteRaw(ref writer, value);
            var hashCode = value.GetHashCode();
            var valueArray = IdSpan.UnsafeGetArray(value);
            ref var recentWrite = ref Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(_recentWrites),
                hashCode & (RecentCacheSize - 1));
            if (recentWrite.HashCode == hashCode && ReferenceEquals(recentWrite.Value, valueArray))
            {
                return;
            }

            SharedCache.GetOrAdd(value, static (key, _) => key, (object)null);
            recentWrite = (hashCode, valueArray);
        }
    }
}

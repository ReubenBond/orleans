using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A map value's ledgers by range, one lookup per distinct depth, so that finding a key's ledgers probes each depth once
/// without constructing a range (ledgers.md §2.3).
/// </summary>
/// <remarks>
/// Ranges at one depth are equal or disjoint, so a key lies in at most one listed range per depth. Each range maps to
/// the positions in the value's ledgers, ascending and so in ID order, of the ledgers over it: a ledger and the
/// successors which replaced it share its range. A lookup hashes the key's leading bytes at a depth as
/// <see cref="KeyRange.GetContainingHashCode"/> does, and compares by containment, over a key its caller validated.
/// </remarks>
internal sealed class LedgerMapIndex
{
    private readonly ImmutableArray<LedgerMetadata> _ledgers;
    private readonly (int Depth, Dictionary<KeyRange, int[]>.AlternateLookup<KeyProbe> Lookup)[] _depths;

    public LedgerMapIndex(ImmutableArray<LedgerMetadata> ledgers)
    {
        _ledgers = ledgers;
        var byDepth = new SortedDictionary<int, Dictionary<KeyRange, List<int>>>();
        for (var position = 0; position < ledgers.Length; position++)
        {
            var range = ledgers[position].Range;
            if (!byDepth.TryGetValue(range.Depth, out var ranges))
            {
                byDepth.Add(range.Depth, ranges = []);
            }

            if (!ranges.TryGetValue(range, out var listed))
            {
                ranges.Add(range, listed = []);
            }

            listed.Add(position);
        }

        _depths =
        [
            .. byDepth.Select(static depth => (
                depth.Key,
                depth.Value.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray(), RangeComparer.Instance)
                    .GetAlternateLookup<KeyProbe>())),
        ];
        AssertPartition(ledgers);
    }

    /// <summary>
    /// Returns the open ledger and the closed ledgers, in ID order, whose ranges contain <paramref name="key"/>, which the
    /// caller validated; <see langword="null"/> when no open ledger does.
    /// </summary>
    public LedgerLookup? Find(ReadOnlySpan<byte> key)
    {
        var hits = ArrayPool<int[]>.Shared.Rent(_depths.Length);
        try
        {
            var count = 0;
            var open = -1;
            var closed = 0;
            foreach (var (depth, lookup) in _depths)
            {
                if (!lookup.TryGetValue(new KeyProbe(key, depth), out var positions))
                {
                    continue;
                }

                hits[count++] = positions;
                foreach (var position in positions)
                {
                    if (_ledgers[position].IsOpen)
                    {
                        Debug.Assert(open < 0, "LG-I1: one open ledger holds each key.");
                        open = position;
                    }
                    else
                    {
                        closed++;
                    }
                }
            }

            if (open < 0)
            {
                Debug.Assert(closed == 0, "LG-I1: a key some ledger holds lies in an open ledger.");
                return null;
            }

            return new LedgerLookup(_ledgers[open], closed == 0 ? [] : Closed(hits.AsSpan(0, count), closed));
        }
        finally
        {
            ArrayPool<int[]>.Shared.Return(hits, clearArray: true);
        }
    }

    /// <summary>
    /// Returns the open ledger whose range contains <paramref name="key"/>, which the caller validated, or
    /// <see langword="null"/> when none does; it probes each depth and allocates nothing.
    /// </summary>
    public LedgerMetadata? FindOpen(ReadOnlySpan<byte> key)
    {
        foreach (var (depth, lookup) in _depths)
        {
            if (!lookup.TryGetValue(new KeyProbe(key, depth), out var positions))
            {
                continue;
            }

            foreach (var position in positions)
            {
                if (_ledgers[position].IsOpen)
                {
                    return _ledgers[position];
                }
            }
        }

        return null;
    }

    [Conditional("DEBUG")]
    private static void AssertPartition(ImmutableArray<LedgerMetadata> ledgers)
    {
        KeyRange[] open = [.. ledgers.Where(static ledger => ledger.IsOpen).Select(static ledger => ledger.Range)];
        Debug.Assert(open.Length == 0 || KeyRange.IsPartition(open), "LG-I1: the open ledgers' ranges partition the keyspace.");
    }

    /// <summary>
    /// Merges the closed ledgers of <paramref name="hits"/>, each list ascending, into ID order: the value's ledgers are
    /// in ID order, so ascending positions are ascending IDs.
    /// </summary>
    private ImmutableArray<LedgerMetadata> Closed(ReadOnlySpan<int[]> hits, int count)
    {
        var builder = ImmutableArray.CreateBuilder<LedgerMetadata>(count);
        Span<int> cursors = hits.Length <= 64 ? stackalloc int[hits.Length] : new int[hits.Length];
        cursors.Clear();
        while (builder.Count < count)
        {
            var least = -1;
            for (var list = 0; list < hits.Length; list++)
            {
                while (cursors[list] < hits[list].Length && _ledgers[hits[list][cursors[list]]].IsOpen)
                {
                    cursors[list]++;
                }

                if (cursors[list] < hits[list].Length && (least < 0 || hits[list][cursors[list]] < hits[least][cursors[least]]))
                {
                    least = list;
                }
            }

            builder.Add(_ledgers[hits[least][cursors[least]++]]);
        }

        return builder.MoveToImmutable();
    }

    /// <summary>A key and a depth: the range at that depth which contains the key, unconstructed.</summary>
    private readonly ref struct KeyProbe(ReadOnlySpan<byte> key, int depth)
    {
        public ReadOnlySpan<byte> Key { get; } = key;

        public int Depth { get; } = depth;
    }

    private sealed class RangeComparer : IEqualityComparer<KeyRange>, IAlternateEqualityComparer<KeyProbe, KeyRange>
    {
        public static RangeComparer Instance { get; } = new();

        public bool Equals(KeyRange x, KeyRange y) => x.Equals(y);

        public int GetHashCode(KeyRange obj) => obj.GetHashCode();

        public bool Equals(KeyProbe alternate, KeyRange other) => other.Depth == alternate.Depth && other.ContainsUnchecked(alternate.Key);

        public int GetHashCode(KeyProbe alternate) => KeyRange.GetContainingHashCodeUnchecked(alternate.Key, alternate.Depth);

        public KeyRange Create(KeyProbe alternate) => KeyRange.Containing(alternate.Key, alternate.Depth);
    }
}

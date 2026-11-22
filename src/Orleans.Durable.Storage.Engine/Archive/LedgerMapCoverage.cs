using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// The archive contract's members for a store whose coverage of a key is the read's ledger map value and whose only token
/// is empty (archive.md §3): the LSM's (archive-lsm.md §2.8). A prefix holds the key's rows of exactly the ledgers the map value no longer lists
/// (<see cref="LedgerMapValue.NoLongerLists"/>), and <see cref="Holds"/> holds exactly their writes: one predicate decides
/// both, so the prefix holds exactly the writes <c>Holds</c> holds. The version base is the tag of the prefix's last row.
/// </summary>
public static class LedgerMapCoverage
{
    /// <summary>The length of a version base: a tag's ledger and slot as <c>u64</c>s and its index as a <c>u32</c>.</summary>
    public const int VersionBaseLength = 20;

    /// <summary>
    /// Encodes <paramref name="tag"/> as a version base: its ledger, its slot, and its index, little-endian, as sections
    /// encode tags (archive-lsm.md §2.8). Tags are unique and order each key's rows (LG-I6), so distinct tags encode to
    /// distinct bases.
    /// </summary>
    public static RowToken VersionBaseOf(Tag tag)
    {
        Span<byte> bytes = stackalloc byte[VersionBaseLength];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, tag.Ledger.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], (ulong)tag.Slot.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], (uint)tag.Index);
        return new RowToken(bytes);
    }

    /// <summary>Begins the fold of <c>Plan</c> over the empty token, the store's only one.</summary>
    /// <param name="token">The write's token.</param>
    /// <param name="store">The store's name, for the exception.</param>
    /// <exception cref="ArchiveCorruptException"><paramref name="token"/> is not empty.</exception>
    public static ArchivePlan Begin(RowToken token, string store)
    {
        CheckToken(token, store);
        return ArchivePlan.Empty();
    }

    /// <summary>Throws unless <paramref name="token"/> is empty, the store's only token.</summary>
    /// <exception cref="ArchiveCorruptException"><paramref name="token"/> is not empty.</exception>
    public static void CheckToken(RowToken token, string store)
    {
        if (!token.IsEmpty)
        {
            throw new ArchiveCorruptException(string.Create(CultureInfo.InvariantCulture, $"The {store} could not have minted the token {token}: its only token is empty."));
        }
    }

    /// <summary>
    /// Returns whether <paramref name="prefix"/>, whose coverage is its read's map value, holds the first
    /// <paramref name="count"/> commands of <paramref name="write"/>: none, or every one when the map value no longer lists
    /// the write's ledger. A ledger at or past the value's <c>NextLedgerId</c>, which a later value may list, is not held.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative or above the write's commands.</exception>
    /// <exception cref="ArgumentException"><paramref name="prefix"/>'s coverage is not a map value.</exception>
    public static bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, write.Commands.Length);
        if (prefix.Coverage is not LedgerMapValue map)
        {
            throw new ArgumentException("The prefix's coverage is not a ledger map value.", nameof(prefix));
        }

        return count == 0 || map.NoLongerLists(write.Segment.Ledger);
    }

    /// <summary>
    /// Answers a read from a key's rows the store holds, in strictly increasing tag order: the prefix of those of the
    /// ledgers <paramref name="map"/> no longer lists, with the empty token, the last such row's tag as its version base, and
    /// <paramref name="map"/> as its coverage; or <see cref="ArchiveStoreRead.Absent"/> when there are none.
    /// </summary>
    /// <param name="store">The store, whose <c>Holds</c> a debug assertion checks against every row taken.</param>
    /// <param name="rows">The key's rows, which the read borrows.</param>
    /// <param name="map">The read's map value.</param>
    /// <exception cref="NotSupportedException">The content would pass <see cref="int.MaxValue"/> bytes.</exception>
    public static ArchiveStoreRead Read(IArchiveStore store, ReadOnlySpan<TaggedRow> rows, LedgerMapValue map)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(map);
        if (rows.IsEmpty)
        {
            return ArchiveStoreRead.Absent.Instance;
        }

        var firstExcluded = -1;
        for (var index = 0; index < rows.Length; index++)
        {
            if (!map.NoLongerLists(rows[index].Tag.Ledger))
            {
                firstExcluded = index;
                break;
            }
        }

        if (firstExcluded < 0)
        {
            var allPrefix = ArchivePrefix.Fold(rows, default, VersionBaseOf(rows[^1].Tag), map);
            Debug.Assert(HoldsEvery(store, allPrefix, rows), "The prefix holds exactly the writes its store's Holds holds: every row it takes is held.");
            return new ArchiveStoreRead.Read(allPrefix);
        }

        List<TaggedRow>? taken = null;
        if (firstExcluded > 0)
        {
            taken = [];
            _ = taken.EnsureCapacity(rows.Length - 1);
            for (var index = 0; index < firstExcluded; index++)
            {
                taken.Add(rows[index]);
            }
        }

        for (var index = firstExcluded + 1; index < rows.Length; index++)
        {
            if (map.NoLongerLists(rows[index].Tag.Ledger))
            {
                if (taken is null)
                {
                    taken = [];
                    _ = taken.EnsureCapacity(rows.Length - index);
                }

                taken.Add(rows[index]);
            }
        }

        if (taken is null)
        {
            return ArchiveStoreRead.Absent.Instance;
        }

        var takenSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(taken);
        var prefix = ArchivePrefix.Fold(takenSpan, default, VersionBaseOf(takenSpan[^1].Tag), map);
        Debug.Assert(HoldsEvery(store, prefix, takenSpan), "The prefix holds exactly the writes its store's Holds holds: every row it takes is held.");
        return new ArchiveStoreRead.Read(prefix);
    }

    private static bool HoldsEvery(IArchiveStore store, ArchivePrefix prefix, ReadOnlySpan<TaggedRow> rows)
    {
        foreach (var (tag, row) in rows)
        {
            var write = new ArchiveWrite(new LedgerSegmentId(tag.Ledger, tag.Slot), default, [ArchiveCommand.Of(row.Kind, ReadOnlyMemory<byte>.Empty)]);
            if (!store.Holds(prefix, write, 1))
            {
                return false;
            }
        }

        return true;
    }
}

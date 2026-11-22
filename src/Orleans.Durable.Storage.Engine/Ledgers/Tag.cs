namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A row's position in its ledger's history, <c>(Ledger, Slot, Index)</c>: its ledger, its batch's slot in that ledger,
/// and its index in the batch (ledgers.md §2.2, identity.md §6).
/// </summary>
/// <remarks>
/// Tags order lexicographically and compare by value. For every key, the tag order of its decided rows is their
/// acceptance order (LG-I6, ledgers.md#5-invariants), so tags order each key's history across the live ledgers and
/// the archive.
/// </remarks>
public readonly record struct Tag : IComparable<Tag>
{
    /// <summary>Initializes a new instance of the <see cref="Tag"/> struct.</summary>
    /// <exception cref="ArgumentException"><paramref name="ledger"/> is the default, which names no ledger.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public Tag(LedgerId ledger, Slot slot, int index)
    {
        if (ledger == default)
        {
            throw new ArgumentException("A tag names a ledger; the default ledger ID names none.", nameof(ledger));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Ledger = ledger;
        Slot = slot;
        Index = index;
    }

    /// <summary>Gets the row's ledger.</summary>
    public LedgerId Ledger { get; }

    /// <summary>Gets the slot of the row's batch in its ledger.</summary>
    public Slot Slot { get; }

    /// <summary>Gets the row's index in its batch, from 0.</summary>
    public int Index { get; }

    /// <inheritdoc/>
    public int CompareTo(Tag other)
    {
        var order = Ledger.CompareTo(other.Ledger);
        if (order != 0)
        {
            return order;
        }

        order = Slot.CompareTo(other.Slot);
        return order != 0 ? order : Index.CompareTo(other.Index);
    }

    public static bool operator <(Tag left, Tag right) => left.CompareTo(right) < 0;

    public static bool operator >(Tag left, Tag right) => left.CompareTo(right) > 0;

    public static bool operator <=(Tag left, Tag right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Tag left, Tag right) => left.CompareTo(right) >= 0;
}

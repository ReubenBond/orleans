using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// A committed section (archive §2.1, §4): a sorted run of rows, all with keys in <see cref="Partition"/>, stored as the
/// bytes <c>[Offset, Offset + Length)</c> of <see cref="Object"/>, with its tag span and the position of the object's
/// tail: the object's metadata and trailer, from its first section's block index to its end (archive §7).
/// </summary>
/// <remarks>
/// A section is identified by its object and offset, by which the manifest orders its sections. Equality compares every
/// field.
/// </remarks>
public sealed class SectionRef : IEquatable<SectionRef>
{
    /// <summary>
    /// The last slot a ledger has, and so the highest a tag names, 2^63 - 2 (identity.md §13).
    /// </summary>
    internal const long MaxTagSlot = long.MaxValue - 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="SectionRef"/> class.
    /// </summary>
    /// <param name="object">The run object holding the section.</param>
    /// <param name="offset">The section's first byte in the object.</param>
    /// <param name="length">The section's length in bytes.</param>
    /// <param name="partition">The dyadic range holding every key of the section's rows.</param>
    /// <param name="minTag">The lowest tag of the section's rows.</param>
    /// <param name="maxTag">The highest tag of the section's rows, at least <paramref name="minTag"/>.</param>
    /// <param name="tail">The position of the object's tail: its block indexes and Bloom filters, then its trailer, which ends the object.</param>
    /// <exception cref="ArgumentException">The tags are out of order, or the tail is the default, empty range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The section's bytes are not a byte range, or a tag names no row: its
    /// ledger is the default, or its slot lies past 2^63 - 2, the last slot a ledger has.</exception>
    public SectionRef(
        ObjectName @object,
        long offset,
        long length,
        KeyRange partition,
        Tag minTag,
        Tag maxTag,
        ObjectRange tail)
    {
        ArgumentNullException.ThrowIfNull(@object);
        if (!ObjectRange.IsValid(offset, length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), "A section's bytes start at or above 0, hold at least one byte, and end at or below 2^63 - 1.");
        }

        ThrowIfNoRow(minTag, nameof(minTag));
        ThrowIfNoRow(maxTag, nameof(maxTag));
        if (minTag > maxTag)
        {
            throw new ArgumentException("A section's lowest tag is at most its highest.", nameof(maxTag));
        }

        if (tail == default)
        {
            throw new ArgumentException("A section names its object's tail, which holds at least one byte.", nameof(tail));
        }

        Object = @object;
        Offset = offset;
        Length = length;
        Partition = partition;
        MinTag = minTag;
        MaxTag = maxTag;
        Tail = tail;
    }

    /// <summary>
    /// Gets the run object holding the section.
    /// </summary>
    public ObjectName Object { get; }

    /// <summary>
    /// Gets the section's first byte in the object.
    /// </summary>
    public long Offset { get; }

    /// <summary>
    /// Gets the section's length in bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets the dyadic range holding every key of the section's rows.
    /// </summary>
    public KeyRange Partition { get; }

    /// <summary>
    /// Gets the lowest tag of the section's rows.
    /// </summary>
    public Tag MinTag { get; }

    /// <summary>
    /// Gets the highest tag of the section's rows.
    /// </summary>
    public Tag MaxTag { get; }

    /// <summary>
    /// Gets the position of the object's tail: its block indexes and Bloom filters, then its trailer, which ends the
    /// object, so one read of it gives a section's metadata (archive §7).
    /// </summary>
    public ObjectRange Tail { get; }

    /// <summary>
    /// Gets the order of sections by their position, <see cref="CompareByPosition"/>.
    /// </summary>
    internal static IComparer<SectionRef> PositionOrder { get; } = Comparer<SectionRef>.Create(CompareByPosition);

    /// <summary>
    /// Orders sections by their position: their object's name, then their offset in it.
    /// </summary>
    public static int CompareByPosition(SectionRef left, SectionRef right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var order = left.Object.CompareTo(right.Object);
        return order != 0 ? order : left.Offset.CompareTo(right.Offset);
    }

    /// <summary>
    /// Throws unless <paramref name="tag"/> can name a row, as the manifest's format requires (archive §7).
    /// </summary>
    private static void ThrowIfNoRow(Tag tag, string name)
    {
        if (tag.Ledger == default || tag.Slot.Value > MaxTagSlot)
        {
            throw new ArgumentOutOfRangeException(name, "A section's tag names a ledger, and a slot at most 2^63 - 2.");
        }
    }

    /// <inheritdoc/>
    public bool Equals(SectionRef? other) =>
        other is not null
        && Object == other.Object
        && Offset == other.Offset
        && Length == other.Length
        && Partition == other.Partition
        && MinTag == other.MinTag
        && MaxTag == other.MaxTag
        && Tail == other.Tail;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SectionRef);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Object, Offset, Length, Partition, MinTag, MaxTag, Tail);
}

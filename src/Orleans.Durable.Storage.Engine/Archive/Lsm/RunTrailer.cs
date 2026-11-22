using System.Collections.Immutable;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// A run object's trailer (archive §7): where its metadata starts, each section's entry, and the object's reference
/// condition. The trailer ends the object.
/// </summary>
/// <param name="MetadataOffset">The first byte of the object's metadata, its first section's block index, which follows
/// every section's blocks; a <see cref="SectionRef"/>'s tail starts here.</param>
/// <param name="TrailerOffset">The trailer's first byte, which follows the last section's Bloom filter.</param>
/// <param name="ObjectLength">The object's length: the trailer's end.</param>
/// <param name="Sections">The sections, whose blocks lie back to back from offset 0 to the metadata.</param>
/// <param name="Condition">The condition under which a commit could reference the object (archive §8.5).</param>
internal sealed record RunTrailer(long MetadataOffset, long TrailerOffset, long ObjectLength, ImmutableArray<RunSectionEntry> Sections, RunObjectCondition Condition)
{
    /// <summary>
    /// Returns the entry of the section whose blocks start at <paramref name="offset"/>, or <see langword="null"/> when no
    /// section starts there.
    /// </summary>
    public RunSectionEntry? Find(long offset)
    {
        var index = Sections.AsSpan().BinarySearch(new EntryOffset(offset));
        return index >= 0 ? Sections[index] : null;
    }

    /// <summary>
    /// Returns whether the trailer lists <paramref name="section"/> as the manifest names it: at its offset, with its
    /// length, partition, and tag span (archive §7).
    /// </summary>
    public bool Lists(SectionRef section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return Find(section.Offset) is { } entry
            && entry.Length == section.Length
            && entry.Partition == section.Partition
            && entry.MinTag == section.MinTag
            && entry.MaxTag == section.MaxTag;
    }

    /// <summary>
    /// Returns whether <paramref name="name"/> agrees with the reference condition (identity.md §11): a drain output of
    /// <c>S</c> from <c>a</c> is named <c>d/{S}/{a}/{id}</c>, and a compaction output <c>c/{partition}/{id}</c>, the
    /// digest of its first section's partition.
    /// </summary>
    public bool AgreesWith(ObjectName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Condition switch
        {
            RunObjectCondition.Drain drain => RunObjectNames.DrainConditionOf(name) == drain,
            RunObjectCondition.Compaction => name.Value.Split('/') is ["c", var scope, _] && scope == RunObjectNames.PartitionScope(Sections[0].Partition),
            null => throw new System.Diagnostics.UnreachableException("A decoded trailer records its condition."),
        };
    }

    /// <inheritdoc/>
    public bool Equals(RunTrailer? other) =>
        other is not null
        && MetadataOffset == other.MetadataOffset
        && TrailerOffset == other.TrailerOffset
        && ObjectLength == other.ObjectLength
        && Sections.SequenceEqual(other.Sections)
        && Condition.Equals(other.Condition);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(MetadataOffset, TrailerOffset, ObjectLength, Sections.Length, Condition);

    /// <summary>Orders entries against an offset, for a binary search by offset.</summary>
    private readonly struct EntryOffset(long offset) : IComparable<RunSectionEntry>
    {
        public int CompareTo(RunSectionEntry? other) => other is null ? 1 : offset.CompareTo(other.Offset);
    }
}

/// <summary>
/// A section's entry in its object's trailer (archive §7): its blocks' bytes, partition, tag span, row count, and the
/// offsets of its block index and Bloom filter.
/// </summary>
/// <param name="Offset">The section's first byte in the object, its first block's.</param>
/// <param name="Length">The bytes of the section's blocks.</param>
/// <param name="Partition">The dyadic range holding every key of the section's rows.</param>
/// <param name="MinTag">The lowest tag of the section's rows.</param>
/// <param name="MaxTag">The highest tag of the section's rows.</param>
/// <param name="RowCount">The number of the section's rows.</param>
/// <param name="IndexOffset">The block index's first byte in the object.</param>
/// <param name="FilterOffset">The Bloom filter's first byte in the object, which ends the block index.</param>
/// <param name="FilterEnd">The offset just past the Bloom filter: the next section's block index, or the trailer.</param>
internal sealed record RunSectionEntry(
    long Offset,
    long Length,
    KeyRange Partition,
    Tag MinTag,
    Tag MaxTag,
    ulong RowCount,
    long IndexOffset,
    long FilterOffset,
    long FilterEnd)
{
    /// <summary>Gets the offset just past the section's last block.</summary>
    public long End => Offset + Length;

    /// <summary>
    /// Returns the manifest's reference to this section of <paramref name="object"/>, whose tail lies at
    /// <paramref name="tail"/>.
    /// </summary>
    public SectionRef ToSectionRef(ObjectName @object, ObjectRange tail) =>
        new(@object, Offset, Length, Partition, MinTag, MaxTag, tail);
}

/// <summary>
/// The condition under which a commit could reference a run object, which its trailer records (archive §7, §8.5):
/// <see cref="Drain"/> for a drain output, or <see cref="Compaction"/> for a compaction output.
/// </summary>
internal readonly union RunObjectCondition(RunObjectCondition.Drain, RunObjectCondition.Compaction) : IEquatable<RunObjectCondition>
{
    /// <inheritdoc/>
    public bool Equals(RunObjectCondition other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RunObjectCondition other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>
    /// A drain output of <paramref name="Ledger"/> from watermark <paramref name="From"/>: a commit references it only
    /// while the ledger is incomplete, at or above <c>DrainedBelow</c>, and its watermark is <paramref name="From"/>.
    /// Every row of the output is of <paramref name="Ledger"/>, at slot <paramref name="From"/> or above.
    /// </summary>
    /// <param name="Ledger">The drained ledger.</param>
    /// <param name="From">The watermark the drain started from.</param>
    public sealed record Drain(LedgerId Ledger, Slot From);

    /// <summary>
    /// A compaction output: a commit references it only while every input is listed.
    /// </summary>
    /// <param name="Inputs">The input sections, by position, in order of object name, then offset.</param>
    public sealed record Compaction(ImmutableArray<SectionPosition> Inputs)
    {
        /// <inheritdoc/>
        public bool Equals(Compaction? other) => other is not null && Inputs.SequenceEqual(other.Inputs);

        /// <inheritdoc/>
        public override int GetHashCode() => Inputs.Length;
    }
}

/// <summary>
/// A section's position, which identifies it: its object and its offset in the object.
/// </summary>
/// <param name="Object">The section's object.</param>
/// <param name="Offset">The section's first byte in the object.</param>
internal readonly record struct SectionPosition(ObjectName Object, long Offset) : IComparable<SectionPosition>
{
    /// <inheritdoc/>
    public int CompareTo(SectionPosition other)
    {
        var order = Object.CompareTo(other.Object);
        return order != 0 ? order : Offset.CompareTo(other.Offset);
    }
}

/// <summary>
/// The names of run objects (identity.md §11): <c>d/{S}/{a}/{id}</c> and <c>c/{partition}/{id}</c>.
/// </summary>
internal static class RunObjectNames
{
    /// <summary>The listing prefix of drain outputs.</summary>
    public const string DrainPrefix = "d/";

    /// <summary>The listing prefix of compaction outputs.</summary>
    public const string CompactionPrefix = "c/";

    /// <summary>
    /// Returns the condition a drain output's name <c>d/{S}/{a}/{id}</c> repeats (archive §8.5), or
    /// <see langword="null"/> when the name is no drain output's: its ledger and watermark in their decimal text forms.
    /// </summary>
    public static RunObjectCondition.Drain? DrainConditionOf(ObjectName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Comparing each number with its text form rejects a sign and leading zeros, which no writer writes.
        return name.Value.Split('/') is ["d", var ledger, var from, _]
            && ulong.TryParse(ledger, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id != 0
            && long.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
            && ledger == id.ToString(CultureInfo.InvariantCulture)
            && from == slot.ToString(CultureInfo.InvariantCulture)
                ? new RunObjectCondition.Drain(new LedgerId(id), new Slot(slot))
                : null;
    }

    /// <summary>
    /// Returns a fresh random, non-zero 128-bit ID, the <c>{id}</c> of a new run object or the store's ID, so names are never reused
    /// (LSM-I7).
    /// </summary>
    public static UInt128 NewId()
    {
        Span<byte> bytes = stackalloc byte[16];
        var value = UInt128.Zero;
        while (value == UInt128.Zero)
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            value = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(bytes);
        }

        return value;
    }

    /// <summary>
    /// Returns the name of a drain output of <paramref name="ledger"/> from <paramref name="from"/>, with the random
    /// <paramref name="id"/>.
    /// </summary>
    public static ObjectName Drain(LedgerId ledger, Slot from, UInt128 id) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{DrainPrefix}{ledger.Value}/{from.Value}/{id:x32}"));

    /// <summary>
    /// Returns the name of a compaction output whose first section's partition is <paramref name="partition"/>, with the
    /// random <paramref name="id"/>.
    /// </summary>
    public static ObjectName Compaction(KeyRange partition, UInt128 id) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{CompactionPrefix}{PartitionScope(partition)}/{id:x32}"));

    /// <summary>
    /// Returns a partition's bounded digest: its depth in decimal, then, above depth 0, <c>-</c> and the lowercase
    /// hexadecimal of its prefix's first <c>⌈min(depth, 64) / 8⌉</c> bytes.
    /// </summary>
    public static string PartitionScope(KeyRange partition)
    {
        var depth = partition.Depth.ToString(CultureInfo.InvariantCulture);
        return partition.Depth == 0
            ? depth
            : string.Concat(depth, "-", Convert.ToHexStringLower(partition.Prefix[..((Math.Min(partition.Depth, 64) + 7) / 8)]));
    }
}

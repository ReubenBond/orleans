using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// Rows read from a run object, in the order the object holds them, which own leases on their payloads until the
/// holder disposes them.
/// </summary>
/// <param name="rows">An array whose first <paramref name="count"/> rows are the rows, whose payloads' leases the instance takes.</param>
/// <param name="count">The number of rows.</param>
internal sealed class RunRows(TaggedRow[] rows, int count) : IDisposable
{
    private ArraySegment<TaggedRow> _rows = new(rows, 0, count);

    /// <summary>
    /// Initializes a new instance of the <see cref="RunRows"/> class over every row of <paramref name="rows"/>.
    /// </summary>
    public RunRows(TaggedRow[] rows)
        : this(rows, rows.Length)
    {
    }

    /// <summary>Gets an empty set of rows, which disposing and taking leave as it is.</summary>
    public static RunRows Empty { get; } = new([]);

    /// <summary>Gets the rows, in order of key, then tag.</summary>
    public ReadOnlySpan<TaggedRow> Rows => _rows;

    /// <summary>
    /// Moves the rows, with their payloads' leases, to the end of <paramref name="destination"/>, which then owns them; the
    /// instance holds none afterwards.
    /// </summary>
    public void MoveTo(List<TaggedRow> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.AddRange(TakeRows());
    }

    /// <summary>
    /// Hands over the rows, with their payloads' leases, which the caller then owns; the instance holds none afterwards.
    /// </summary>
    public ArraySegment<TaggedRow> TakeRows()
    {
        var taken = _rows;
        if (taken.Count > 0)
        {
            _rows = ArraySegment<TaggedRow>.Empty;
        }

        return taken;
    }

    /// <summary>Releases the rows' payloads.</summary>
    public void Dispose()
    {
        foreach (var row in TakeRows())
        {
            row.Row.Dispose();
        }
    }
}

/// <summary>
/// A section read whole (archive §7): its metadata and every row, checked under every rule of the format.
/// </summary>
/// <param name="metadata">The section's metadata.</param>
/// <param name="rows">The section's rows, which the instance takes.</param>
internal sealed class RunSection(RunSectionMetadata metadata, RunRows rows) : IDisposable
{
    /// <summary>Gets the section's metadata.</summary>
    public RunSectionMetadata Metadata { get; } = metadata;

    /// <summary>Gets the section's rows, in order of key, then tag.</summary>
    public RunRows Rows { get; } = rows;

    /// <summary>Releases the rows' payloads.</summary>
    public void Dispose() => Rows.Dispose();
}

/// <summary>
/// A run object read whole (archive §7): its tail and each of its sections.
/// </summary>
/// <param name="tail">The object's trailer and metadata.</param>
/// <param name="sections">The object's sections, which the instance takes.</param>
internal sealed class RunObject(RunTail tail, ImmutableArray<RunSection> sections) : IDisposable
{
    /// <summary>Gets the object's trailer and each section's metadata.</summary>
    public RunTail Tail { get; } = tail;

    /// <summary>Gets the object's trailer.</summary>
    public RunTrailer Trailer => Tail.Trailer;

    /// <summary>Gets the object's sections, in the trailer's order.</summary>
    public ImmutableArray<RunSection> Sections { get; } = sections;

    /// <summary>Releases every section's rows.</summary>
    public void Dispose()
    {
        foreach (var section in Sections)
        {
            section.Dispose();
        }
    }
}

/// <summary>
/// One section a writer writes: its partition and its rows, in order of key, then tag, which the writer reads and keeps.
/// </summary>
/// <param name="Partition">The section's partition, which contains every row's key.</param>
/// <param name="Rows">The section's rows, at least one, in strictly increasing order of key, then tag.</param>
internal readonly record struct RunSectionInput(KeyRange Partition, ReadOnlyMemory<TaggedRow> Rows);

/// <summary>
/// The settings of the sections a writer writes (archive §8.7). A reader needs neither: each block index lists its blocks,
/// and each Bloom filter records its hash count.
/// </summary>
/// <param name="BlockBytes">The block size: a block ends before a row which would make it longer, unless it is empty.</param>
/// <param name="BloomBitsPerKey">The Bloom filter's bits per key, 1 to 64.</param>
internal readonly record struct RunSectionSettings(int BlockBytes, int BloomBitsPerKey);

/// <summary>
/// A run object a writer encoded: its bytes, which the holder owns and disposes, its trailer, a CRC-32C of its bytes
/// which the writer's check then verified, the place of its tail, and its sections' blocks for write-through caching.
/// </summary>
/// <param name="content">The object's bytes, which the instance takes.</param>
/// <param name="trailer">The object's trailer.</param>
/// <param name="crc32c">A CRC-32C of <paramref name="content"/> which the writer's read-back check then verified.</param>
/// <param name="blocks">The byte ranges of the blocks across the object's sections, in section order.</param>
internal sealed class EncodedRunObject(ArcBuffer content, RunTrailer trailer, uint crc32c, ImmutableArray<ObjectRange> blocks) : IDisposable
{
    /// <summary>Gets the object's bytes.</summary>
    public ArcBuffer Content { get; } = content;

    /// <summary>
    /// Gets a CRC-32C of <see cref="Content"/> which the writer's read-back check then verified, and which the object's
    /// upload carries (archive §3).
    /// </summary>
    public uint Crc32C { get; } = crc32c;

    /// <summary>Gets the object's trailer.</summary>
    public RunTrailer Trailer { get; } = trailer;

    /// <summary>Gets the object's tail: its block indexes, Bloom filters, and trailer, which end it.</summary>
    public ObjectRange Tail => new(Trailer.MetadataOffset, Trailer.ObjectLength - Trailer.MetadataOffset);

    /// <summary>Gets the byte ranges of the object's blocks, across all of its sections.</summary>
    public ImmutableArray<ObjectRange> Blocks { get; } = blocks;

    /// <summary>
    /// Returns the manifest's references to the object's sections, once it is uploaded as <paramref name="object"/>.
    /// </summary>
    public ImmutableArray<SectionRef> SectionRefs(ObjectName @object)
    {
        ArgumentNullException.ThrowIfNull(@object);
        var tail = Tail;
        return [.. Trailer.Sections.Select(entry => entry.ToSectionRef(@object, tail))];
    }

    /// <summary>Releases the object's bytes.</summary>
    public void Dispose() => Content.Dispose();
}

/// <summary>
/// What decoding a run object's trailer gives (conventions.md §15): the trailer, or the <see cref="InvalidDataException"/>
/// naming the first rule its bytes break.
/// </summary>
internal readonly union RunTrailerDecoding(RunTrailer, InvalidDataException);

/// <summary>
/// What decoding a run object's tail gives: its trailer and every section's metadata, or the rejection.
/// </summary>
internal readonly union RunTailDecoding(RunTail, InvalidDataException);

/// <summary>
/// What decoding blocks of a section gives: their rows, which the caller owns, or the rejection.
/// </summary>
internal readonly union RunRowsDecoding(RunRows, InvalidDataException);

/// <summary>
/// What decoding a whole run object gives: the object, which the caller owns, or the rejection.
/// </summary>
internal readonly union RunObjectDecoding(RunObject, InvalidDataException);

using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;

using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// Reads run objects through the object store (archive §7): an object's tail, its block indexes and Bloom filters with
/// its trailer, verified against the <see cref="SectionRef"/> the manifest names; one key's rows; one range's rows; a whole
/// section; or, for an object no section references, its trailer by the length a listing gave.
/// </summary>
/// <remarks>
/// Every answer of a present object is checked under the format's rules, and an object which breaks one, contradicts
/// the manifest's <see cref="SectionRef"/> or its own name, or ends before the reference does, is
/// <see cref="ArchiveCorruptException"/> with the rejection inside, whose reason names the rule. A reference past
/// <see cref="RunObjectFormat.MaxObjectLength"/> breaches the bound no writer passes, which is
/// <see cref="InvalidOperationException"/>. An absent object answers <see langword="null"/>: a compaction removed it after
/// the manifest was read, and the caller reads the manifest again.
/// </remarks>
internal static class RunObjectReader
{
    /// <summary>The tail does not start at the trailer's metadata offset.</summary>
    public const string TailMismatch = "tail-mismatch";

    /// <summary>The trailer does not list the manifest's section.</summary>
    public const string SectionNotListed = "section-not-listed";

    /// <summary>The object's name does not agree with its trailer's reference condition.</summary>
    public const string NameMismatch = "name-mismatch";

    /// <summary>
    /// The bytes a trailer read by an object's length takes from its end at first: 16 KiB, past nearly every trailer.
    /// </summary>
    public const int SpeculativeTrailerRead = 16 * 1024;

    /// <summary>
    /// Reads and verifies the tail of <paramref name="section"/>'s object in one read: its trailer, which must start the
    /// metadata at the tail's offset, list the section at the offset, length, partition, and tag span the manifest names,
    /// and agree with the object's name; then every section's block index and Bloom filter.
    /// </summary>
    /// <returns>The tail, or <see langword="null"/> when the object is absent.</returns>
    /// <exception cref="ArchiveCorruptException">The tail breaks a rule or contradicts the reference.</exception>
    /// <exception cref="InvalidOperationException">The tail ends past the bound on an object's length.</exception>
    public static async ValueTask<RunTail?> ReadTailAsync(IObjectStore store, SectionRef section, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(section);
        var tail = section.Tail;
        using var bytes = await ReadAsync(store, section.Object, tail.Offset, tail.Length, "tail", cancellationToken).ConfigureAwait(false);
        if (bytes is not { } tailBytes)
        {
            return null;
        }

        var sequence = tailBytes.AsReadOnlySequence();
        var trailer = Check<RunTrailer>(section.Object, "trailer", RunObjectFormat.DecodeTrailerAt(sequence, tail.Offset).Value);
        if (trailer.MetadataOffset != tail.Offset)
        {
            throw Corrupt(section.Object, TailMismatch, string.Create(CultureInfo.InvariantCulture, $"The trailer starts the metadata at {trailer.MetadataOffset}, not at the manifest's tail, {tail.Offset}."));
        }

        if (!trailer.Lists(section))
        {
            throw Corrupt(section.Object, SectionNotListed, string.Create(CultureInfo.InvariantCulture, $"The trailer does not list the manifest's section at offset {section.Offset}, of {section.Length} bytes, in partition {section.Partition}, with its tags."));
        }

        if (!trailer.AgreesWith(section.Object))
        {
            throw Corrupt(section.Object, NameMismatch, "The object's name does not agree with its trailer's reference condition.");
        }

        var metadata = sequence.Slice(0, trailer.TrailerOffset - tail.Offset);
        return Check<RunTail>(section.Object, "metadata", RunObjectFormat.DecodeMetadata(metadata, trailer).Value);
    }

    /// <summary>
    /// Returns the metadata of <paramref name="section"/> from a verified tail of its object, which a read for another
    /// of the object's sections may have fetched: the tail must lie where the reference names it, and the trailer must
    /// list the section as the manifest names it (archive §7).
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The tail lies elsewhere, or the trailer does not list the section.</exception>
    public static RunSectionMetadata SectionOf(RunTail tail, SectionRef section)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(section);
        var trailer = tail.Trailer;
        if (section.Tail != new ObjectRange(trailer.MetadataOffset, trailer.ObjectLength - trailer.MetadataOffset))
        {
            throw Corrupt(section.Object, TailMismatch, string.Create(CultureInfo.InvariantCulture, $"The object's tail lies at {trailer.MetadataOffset} and ends at {trailer.ObjectLength}, not at the manifest's tail, {section.Tail.Offset}, of {section.Tail.Length} bytes."));
        }

        return trailer.Lists(section) && tail.Find(section.Offset) is { } metadata
            ? metadata
            : throw Corrupt(section.Object, SectionNotListed, string.Create(CultureInfo.InvariantCulture, $"The trailer does not list the manifest's section at offset {section.Offset}, of {section.Length} bytes, in partition {section.Partition}, with its tags."));
    }

    /// <summary>
    /// Reads the trailer of the object <paramref name="listed"/> names, whose length the listing gave, as the sweeper does
    /// for an object no listed section references: the object's last <see cref="SpeculativeTrailerRead"/> bytes, which
    /// hold the trailer's length and nearly always the whole trailer, and the rest of the trailer only when it is longer.
    /// </summary>
    /// <returns>The trailer, or <see langword="null"/> when the object is absent.</returns>
    /// <exception cref="ArchiveCorruptException">The listed length is shorter than a trailer or longer than any object, or
    /// the trailer breaks a rule or does not agree with the object's name.</exception>
    public static async ValueTask<RunTrailer?> ReadTrailerAsync(IObjectStore store, ListedObject listed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var (name, length) = listed;
        if (length < RunObjectFormat.MinTrailerLength)
        {
            throw Corrupt(name, RunObjectFormat.Truncated, string.Create(CultureInfo.InvariantCulture, $"The object holds {length} bytes, fewer than the smallest trailer's."));
        }

        if (length > RunObjectFormat.MaxObjectLength)
        {
            // The listing is the store's word about the object, not a bound a caller broke.
            throw Corrupt(name, RunObjectFormat.LengthMismatch, string.Create(CultureInfo.InvariantCulture, $"The object holds {length} bytes, more than the {RunObjectFormat.MaxObjectLength} of the longest run object."));
        }

        var read = (int)Math.Min(length, SpeculativeTrailerRead);
        using var end = await ReadAsync(store, name, length - read, read, "trailer", cancellationToken).ConfigureAwait(false);
        if (end is not { } endBytes)
        {
            return null;
        }

        var trailerLength = RunObjectFormat.TrailerLength(endBytes.AsReadOnlySequence())!.Value;
        if (trailerLength < RunObjectFormat.MinTrailerLength || trailerLength > length || trailerLength > RunObjectFormat.MaxTrailerLength)
        {
            throw Corrupt(name, RunObjectFormat.LengthMismatch, string.Create(CultureInfo.InvariantCulture, $"The trailer gives its length as {trailerLength}, outside {RunObjectFormat.MinTrailerLength} to the object's {length} bytes, or past {RunObjectFormat.MaxTrailerLength}."));
        }

        if (trailerLength <= read)
        {
            return Decode(endBytes.AsReadOnlySequence().Slice(read - trailerLength));
        }

        using var whole = await ReadAsync(store, name, length - trailerLength, trailerLength, "trailer", cancellationToken).ConfigureAwait(false);
        return whole is { } trailerBytes ? Decode(trailerBytes.AsReadOnlySequence()) : null;

        RunTrailer Decode(System.Buffers.ReadOnlySequence<byte> trailerBytes)
        {
            var trailer = Check<RunTrailer>(name, "trailer", RunObjectFormat.DecodeTrailer(trailerBytes, length - trailerLength).Value);
            return trailer.AgreesWith(name)
                ? trailer
                : throw Corrupt(name, NameMismatch, "The object's name does not agree with its trailer's reference condition.");
        }
    }

    /// <summary>
    /// Reads the rows of <paramref name="key"/> from <paramref name="section"/> of <paramref name="object"/>: none, and no
    /// read, when its partition or Bloom filter excludes the key; otherwise the key's rows of the blocks its block index
    /// names, in one read, each row checked under the format's rules.
    /// </summary>
    /// <returns>The key's rows in tag order, which the caller owns, or <see langword="null"/> when the object is absent.</returns>
    /// <exception cref="ArchiveCorruptException">A block breaks a rule, as a row whose key lies outside its section's
    /// partition does.</exception>
    public static ValueTask<RunRows?> ReadRowsAsync(IObjectStore store, ObjectName @object, RunSectionMetadata section, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        ReadRowsAsync(store, @object, section, key, blockCache: null, admitToCache: true, cancellationToken);

    /// <summary>
    /// Reads the rows of <paramref name="key"/> from <paramref name="section"/> of <paramref name="object"/>, consulting
    /// <paramref name="blockCache"/> when supplied and confirming any corruption past the cache before throwing
    /// <see cref="ArchiveCorruptException"/> (archive-lsm.md §8.2, §8.7).
    /// </summary>
    public static ValueTask<RunRows?> ReadRowsAsync(
        IObjectStore store,
        ObjectName @object,
        RunSectionMetadata section,
        ReadOnlyMemory<byte> key,
        BlockCache? blockCache,
        bool admitToCache = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentNullException.ThrowIfNull(section);
        return ReadRowsAsync(store, @object, section, section.Candidates(key.Span), key, blockCache, admitToCache, cancellationToken);
    }

    /// <summary>
    /// Reads the rows of <paramref name="key"/> from the precomputed candidate <paramref name="blocks"/> of
    /// <paramref name="section"/> without repeating partition, Bloom, or block-index lookup.
    /// </summary>
    internal static async ValueTask<RunRows?> ReadRowsAsync(
        IObjectStore store,
        ObjectName @object,
        RunSectionMetadata section,
        RunBlockSpan blocks,
        ReadOnlyMemory<byte> key,
        BlockCache? blockCache,
        bool admitToCache = true,
        CancellationToken cancellationToken = default)
    {
        if (blocks.Count == 0)
        {
            return RunRows.Empty;
        }

        if (blockCache is null)
        {
            using var bytes = await ReadAsync(store, @object, blocks.Offset, blocks.Length, "blocks", cancellationToken).ConfigureAwait(false);
            return bytes is { } blockBytes
                ? Check<RunRows>(@object, "blocks", RunObjectFormat.DecodeKey(blockBytes, section, blocks.First, blocks.Count, key).Value)
                : null;
        }

        return await ReadAndDecodeBlocksAsync(
            store,
            @object,
            section,
            blocks.First,
            blocks.Count,
            "blocks",
            blockCache,
            admitToCache,
            key,
            static (blockBytes, meta, first, count, targetKey) => RunObjectFormat.DecodeKey(blockBytes, meta, first, count, targetKey),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the whole section <paramref name="section"/> of <paramref name="object"/> in one read, checking every block,
    /// row, and total.
    /// </summary>
    /// <returns>The section's rows, which the caller owns, or <see langword="null"/> when the object is absent.</returns>
    /// <exception cref="ArchiveCorruptException">The section breaks a rule.</exception>
    public static ValueTask<RunRows?> ReadSectionAsync(IObjectStore store, ObjectName @object, RunSectionMetadata section, CancellationToken cancellationToken = default) =>
        ReadSectionAsync(store, @object, section, blockCache: null, admitToCache: true, cancellationToken);

    /// <summary>
    /// Reads the whole section <paramref name="section"/> of <paramref name="object"/>, consulting
    /// <paramref name="blockCache"/> when supplied and confirming any corruption past the cache (archive-lsm.md §8.2, §8.7).
    /// </summary>
    public static async ValueTask<RunRows?> ReadSectionAsync(
        IObjectStore store,
        ObjectName @object,
        RunSectionMetadata section,
        BlockCache? blockCache,
        bool admitToCache = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentNullException.ThrowIfNull(section);
        if (blockCache is null || section.Index.Blocks.Length == 0)
        {
            var entry = section.Entry;
            using var bytes = await ReadAsync(store, @object, entry.Offset, entry.Length, "section", cancellationToken).ConfigureAwait(false);
            return bytes is { } sectionBytes ? Check<RunRows>(@object, "section", RunObjectFormat.DecodeSection(sectionBytes, section).Value) : null;
        }

        return await ReadAndDecodeBlocksAsync(
            store,
            @object,
            section,
            0,
            section.Index.Blocks.Length,
            "section",
            blockCache,
            admitToCache,
            state: (object?)null,
            static (sectionBytes, meta, _, _, _) => RunObjectFormat.DecodeSection(sectionBytes, meta),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the rows of <paramref name="section"/> of <paramref name="object"/> whose keys lie in
    /// <paramref name="range"/>, in one read: none, and no read, when no block may hold one; the whole section, checked
    /// under every rule, when the range contains the section's partition; and otherwise the blocks the block index names
    /// for the range (<see cref="RunSectionMetadata.Covering"/>), every row read checked under every rule but the
    /// section's totals.
    /// </summary>
    /// <returns>The range's rows in order, which the caller owns, or <see langword="null"/> when the object is absent.</returns>
    /// <exception cref="ArchiveCorruptException">A block breaks a rule.</exception>
    public static ValueTask<RunRows?> ReadRangeAsync(IObjectStore store, ObjectName @object, RunSectionMetadata section, KeyRange range, CancellationToken cancellationToken = default) =>
        ReadRangeAsync(store, @object, section, range, blockCache: null, admitToCache: true, cancellationToken);

    /// <summary>
    /// Reads the rows of <paramref name="section"/> of <paramref name="object"/> whose keys lie in
    /// <paramref name="range"/>, consulting <paramref name="blockCache"/> when supplied (archive-lsm.md §8.2, §8.7).
    /// </summary>
    public static async ValueTask<RunRows?> ReadRangeAsync(
        IObjectStore store,
        ObjectName @object,
        RunSectionMetadata section,
        KeyRange range,
        BlockCache? blockCache,
        bool admitToCache = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentNullException.ThrowIfNull(section);
        if (range.Contains(section.Entry.Partition))
        {
            return await ReadSectionAsync(store, @object, section, blockCache, admitToCache, cancellationToken).ConfigureAwait(false);
        }

        var blocks = section.Covering(range);
        if (blocks.Count == 0)
        {
            return RunRows.Empty;
        }

        if (blockCache is null)
        {
            using var bytes = await ReadAsync(store, @object, blocks.Offset, blocks.Length, "blocks", cancellationToken).ConfigureAwait(false);
            return bytes is { } blockBytes
                ? Check<RunRows>(@object, "blocks", RunObjectFormat.DecodeRange(blockBytes, section, blocks.First, blocks.Count, range).Value)
                : null;
        }

        return await ReadAndDecodeBlocksAsync(
            store,
            @object,
            section,
            blocks.First,
            blocks.Count,
            "blocks",
            blockCache,
            admitToCache,
            range,
            static (blockBytes, meta, first, count, targetRange) => RunObjectFormat.DecodeRange(blockBytes, meta, first, count, targetRange),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RunRows?> ReadAndDecodeBlocksAsync<TState>(
        IObjectStore store,
        ObjectName @object,
        RunSectionMetadata section,
        int first,
        int count,
        string part,
        BlockCache blockCache,
        bool admitToCache,
        TState state,
        Func<ArcBuffer, RunSectionMetadata, int, int, TState, RunRowsDecoding> decode,
        CancellationToken cancellationToken)
    {
        var fetched = await ReadBlockSpanAsync(store, @object, section, first, count, part, blockCache, admitToCache, cancellationToken).ConfigureAwait(false);
        if (fetched is not var (blockBytes, usedCache, missed))
        {
            return null;
        }

        var shouldAdmit = admitToCache || blockCache.Mutant == BlockCacheMutant.AdmitsCompactionInputs;
        object? decoded;
        using (blockBytes)
        {
            decoded = decode(blockBytes, section, first, count, state).Value;
            if (decoded is RunRows rows)
            {
                if (shouldAdmit && (!usedCache || missed is not null))
                {
                    try
                    {
                        await AdmitSpanAsync(blockCache, @object, section, first, count, blockBytes, missed, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        rows.Dispose();
                        throw;
                    }
                }

                return rows;
            }
        }

        if (usedCache)
        {
            // archive-lsm.md §8.2, §8.7: confirm corruption past the block cache before answering ArchiveCorruptException.
            for (var index = 0; index < count; index++)
            {
                blockCache.Invalidate(@object, section.Index.Blocks[first + index].Offset);
            }

            var fresh = await ReadBlockSpanAsync(store, @object, section, first, count, part, blockCache: null, admitToCache: false, cancellationToken).ConfigureAwait(false);
            if (fresh is not var (freshBytes, _, _))
            {
                return null;
            }

            using (freshBytes)
            {
                var redecoded = Check<RunRows>(@object, part, decode(freshBytes, section, first, count, state).Value);
                if (shouldAdmit)
                {
                    try
                    {
                        await AdmitSpanAsync(blockCache, @object, section, first, count, freshBytes, missed: null, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        redecoded.Dispose();
                        throw;
                    }
                }

                return redecoded;
            }
        }

        return Check<RunRows>(@object, part, decoded);
    }

    private static async ValueTask<(ArcBuffer Bytes, bool UsedCache, bool[]? Missed)?> ReadBlockSpanAsync(
        IObjectStore store,
        ObjectName @object,
        RunSectionMetadata section,
        int first,
        int count,
        string part,
        BlockCache? blockCache,
        bool admitToCache,
        CancellationToken cancellationToken)
    {
        var blocks = section.Index.Blocks;
        var spanStart = blocks[first].Offset;
        var lastBlock = blocks[first + count - 1];
        var spanLength = lastBlock.Offset + lastBlock.Length - spanStart;

        if (blockCache is null)
        {
            var raw = await ReadAsync(store, @object, spanStart, spanLength, part, cancellationToken).ConfigureAwait(false);
            return raw is { } bytes ? (bytes, false, null) : null;
        }

        if (count == 1)
        {
            var block = blocks[first];
            var cached = await blockCache.TryReadAsync(@object, block.Offset, (int)block.Length, recordAccess: admitToCache, cancellationToken).ConfigureAwait(false);
            if (cached is { } hit)
            {
                return (hit, true, null);
            }

            var raw = await ReadAsync(store, @object, spanStart, spanLength, part, cancellationToken).ConfigureAwait(false);
            return raw is { } bytes ? (bytes, false, null) : null;
        }

        var hits = new ArcBuffer?[count];
        try
        {
            var anyHit = false;
            var anyMiss = false;
            for (var index = 0; index < count; index++)
            {
                var block = blocks[first + index];
                var cached = await blockCache.TryReadAsync(@object, block.Offset, (int)block.Length, recordAccess: admitToCache, cancellationToken).ConfigureAwait(false);
                if (cached is { } hit)
                {
                    hits[index] = hit;
                    anyHit = true;
                }
                else
                {
                    anyMiss = true;
                }
            }

            if (!anyMiss && count == 1)
            {
                var single = hits[0]!.Value;
                hits[0] = null;
                return (single, true, null);
            }

            if (!anyHit)
            {
                var raw = await ReadAsync(store, @object, spanStart, spanLength, part, cancellationToken).ConfigureAwait(false);
                return raw is { } bytes ? (bytes, false, null) : null;
            }

            bool[]? missed = anyMiss ? new bool[count] : null;

            // Coalesce consecutive misses into one ranged read each, then stitch hits and misses into one span buffer.
            for (var index = 0; index < count;)
            {
                if (hits[index] is not null)
                {
                    index++;
                    continue;
                }

                var missStart = index;
                while (index < count && hits[index] is null)
                {
                    index++;
                }

                var missEnd = index;
                var runStart = blocks[first + missStart].Offset;
                var runLast = blocks[first + missEnd - 1];
                var runLength = runLast.Offset + runLast.Length - runStart;
                var fetched = await ReadAsync(store, @object, runStart, runLength, part, cancellationToken).ConfigureAwait(false);
                if (fetched is not { } runBytes)
                {
                    return null;
                }

                using (runBytes)
                {
                    for (var miss = missStart; miss < missEnd; miss++)
                    {
                        var block = blocks[first + miss];
                        var relativeOffset = (int)(block.Offset - runStart);
                        var blockLength = (int)block.Length;
                        hits[miss] = runBytes.Slice(relativeOffset, blockLength);
                        missed![miss] = true;
                    }
                }
            }

            using var writer = new ArcBufferWriter((int)spanLength);
            for (var index = 0; index < count; index++)
            {
                foreach (var segment in hits[index]!.Value)
                {
                    writer.Write(segment);
                }
            }

            return (writer.ConsumeSlice((int)spanLength), true, missed);
        }
        finally
        {
            for (var index = 0; index < hits.Length; index++)
            {
                hits[index]?.Dispose();
            }
        }
    }

    private static async ValueTask AdmitSpanAsync(
        BlockCache blockCache,
        ObjectName @object,
        RunSectionMetadata section,
        int first,
        int count,
        ArcBuffer spanBytes,
        bool[]? missed,
        CancellationToken cancellationToken)
    {
        var blocks = section.Index.Blocks;
        var spanStart = blocks[first].Offset;
        var sequence = spanBytes.AsReadOnlySequence();
        for (var index = 0; index < count; index++)
        {
            if (missed is not null && !missed[index])
            {
                continue;
            }

            var block = blocks[first + index];
            await blockCache.AdmitAsync(@object, block.Offset, sequence.Slice(block.Offset - spanStart, block.Length), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads <c>[offset, offset + length)</c> of <paramref name="object"/>: a range past the bound on an object's length
    /// breaches it, and an object which ends before the range is corrupt.
    /// </summary>
    private static async ValueTask<ArcBuffer?> ReadAsync(IObjectStore store, ObjectName @object, long offset, long length, string part, CancellationToken cancellationToken)
    {
        if (length > RunObjectFormat.MaxObjectLength - offset)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The {part} of {@object}, {length} bytes at offset {offset}, ends past {RunObjectFormat.MaxObjectLength}, the longest run object a writer writes."));
        }

        try
        {
            return await store.ReadAsync(@object, offset, (int)length, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new ArchiveCorruptException(string.Create(CultureInfo.InvariantCulture, $"{@object} ends before its {part}, {length} bytes at offset {offset}."), exception);
        }
    }

    /// <summary>
    /// Returns a decoding's value, or throws its rejection as <see cref="ArchiveCorruptException"/>.
    /// </summary>
    private static T Check<T>(ObjectName @object, string part, object? decoded)
        where T : class
    {
        return decoded switch
        {
            T value => value,
            InvalidDataException rejection => throw new ArchiveCorruptException(string.Create(CultureInfo.InvariantCulture, $"The {part} of {@object} breaks the run object's format: {rejection.Message}"), rejection),
            _ => throw new System.Diagnostics.UnreachableException("A decoding holds its value or its rejection."),
        };
    }

    private static ArchiveCorruptException Corrupt(ObjectName @object, string reason, string message) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{@object} contradicts its reference: {message}"), DecodeRejection.Create(reason, message));
}

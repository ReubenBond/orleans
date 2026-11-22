using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// A chunk of the chunk layout (archive-logs.md §7.4): one append run's bytes followed by their CRC-32C, 4 bytes in
/// little-endian order, which a reader checks, since the object store keeps no checksum it answers.
/// </summary>
internal static class ArchiveLogChunk
{
    /// <summary>The trailer's length: a chunk's content is its object less these bytes.</summary>
    internal const int TrailerLength = sizeof(uint);

    internal const string ReasonTruncated = "truncated";
    internal const string ReasonBadCrc = "bad-crc";

    /// <summary>Writes the chunk of <paramref name="content"/>, an append run's non-empty bytes, to <paramref name="writer"/>.</summary>
    public static void Encode(ArcBufferWriter writer, ReadOnlySequence<byte> content)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (content.IsEmpty)
        {
            throw new ArgumentException("An append run is not empty.", nameof(content));
        }

        writer.Write(content);
        Span<byte> trailer = stackalloc byte[TrailerLength];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, ObjectStoreChecksums.Crc32C(content));
        writer.Write(trailer);
    }

    /// <summary>Checks a chunk's bytes: at least one byte of content and its trailer, which is the content's CRC-32C.</summary>
    /// <returns>The rejection naming the first rule the bytes break, or <see langword="null"/> when the chunk is whole.</returns>
    public static InvalidDataException? Check(ReadOnlySequence<byte> chunk)
    {
        if (chunk.Length <= TrailerLength)
        {
            return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"A chunk holds {chunk.Length} bytes, no content before its {TrailerLength}-byte trailer."));
        }

        var content = chunk.Slice(0, chunk.Length - TrailerLength);
        Span<byte> trailer = stackalloc byte[TrailerLength];
        chunk.Slice(content.Length).CopyTo(trailer);
        return BinaryPrimitives.ReadUInt32LittleEndian(trailer) == ObjectStoreChecksums.Crc32C(content)
            ? null
            : DecodeRejection.Create(ReasonBadCrc, "A chunk's content does not match the CRC-32C of its trailer.");
    }
}

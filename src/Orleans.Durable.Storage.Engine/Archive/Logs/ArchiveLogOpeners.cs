using System.Buffers.Binary;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// The openers of a key's writes (archive-logs.md §2.1): the opener a <c>Replace</c> or a <c>Delete</c> makes, and the
/// tombstone's bytes, a <c>Delete</c>'s 8-byte body.
/// </summary>
internal static class ArchiveLogOpeners
{
    /// <summary>
    /// Returns the opener of <paramref name="write"/>, whose first command is a <c>Replace</c> or a <c>Delete</c>: its
    /// kind, its tombstone, and the rows its base folds, those it superseded, itself, and its appends.
    /// </summary>
    /// <exception cref="ArgumentException">The write opens no generation, or a tombstone is not 8 bytes.</exception>
    public static ArchiveLogOpener Of(ArchiveWrite write)
    {
        var rows = (ulong)write.Superseded + (ulong)write.Commands.Length;
        var command = write.Commands.IsEmpty ? throw new ArgumentException("An empty write opens no generation.", nameof(write)) : write.Commands[0];
        return command.Kind switch
        {
            RowKind.Replace => new(ArchiveLogOpenerKind.Replace, null, rows),
            RowKind.Delete => new(ArchiveLogOpenerKind.Delete, command.Tombstone is { } body ? TombstoneOf(body.Span) : null, rows),
            RowKind.Append => throw new ArgumentException("An Append opens no generation.", nameof(write)),
            _ => throw new ArgumentException($"Row kind {(byte)command.Kind} is not defined.", nameof(write)),
        };
    }

    /// <summary>Returns the tombstone of a <c>Delete</c> whose body is <paramref name="body"/>, 8 bytes.</summary>
    /// <exception cref="ArgumentException">The body is not 8 bytes.</exception>
    public static ulong TombstoneOf(ReadOnlySpan<byte> body) =>
        body.Length == ArchiveLogOpener.TombstoneLength
            ? BinaryPrimitives.ReadUInt64BigEndian(body)
            : throw new ArgumentException($"An archive log's tombstone is a Delete's 8-byte body, not {body.Length} bytes.", nameof(body));

    /// <summary>Returns the bytes of <paramref name="tombstone"/>, the <c>Delete</c>'s body.</summary>
    public static byte[] BytesOf(ulong tombstone)
    {
        var bytes = new byte[ArchiveLogOpener.TombstoneLength];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, tombstone);
        return bytes;
    }
}

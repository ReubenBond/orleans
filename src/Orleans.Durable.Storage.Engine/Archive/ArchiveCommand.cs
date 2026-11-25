using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// One command of a key's write (archive.md §2.1, §4): its kind, its content length, and, for a <c>Delete</c>, its
/// tombstone, apart from its content. A command carries no bytes but a tombstone's.
/// </summary>
public readonly struct ArchiveCommand
{
    private ArchiveCommand(RowKind kind, int contentLength, ReadOnlyMemory<byte>? tombstone)
    {
        Kind = kind;
        ContentLength = contentLength;
        Tombstone = tombstone;
    }

    /// <summary>Gets the command's kind.</summary>
    public RowKind Kind { get; }

    /// <summary>Gets the length of the command's content: an <c>Append</c>'s or a <c>Replace</c>'s body, and 0 for every <c>Delete</c>.</summary>
    public int ContentLength { get; }

    /// <summary>Gets a <c>Delete</c>'s tombstone, its non-empty body; <see langword="null"/> for every other command and an empty <c>Delete</c>.</summary>
    public ReadOnlyMemory<byte>? Tombstone { get; }

    /// <summary>Gets a value indicating whether the command opens its write: a <c>Replace</c> or a <c>Delete</c>.</summary>
    public bool IsOpener => Kind is RowKind.Replace or RowKind.Delete;

    /// <summary>Returns an <c>Append</c> of <paramref name="contentLength"/> bytes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contentLength"/> is negative.</exception>
    public static ArchiveCommand Append(int contentLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        return new(RowKind.Append, contentLength, null);
    }

    /// <summary>Returns a <c>Replace</c> whose body is <paramref name="contentLength"/> bytes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contentLength"/> is negative.</exception>
    public static ArchiveCommand Replace(int contentLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        return new(RowKind.Replace, contentLength, null);
    }

    /// <summary>
    /// Returns a <c>Delete</c> whose body is <paramref name="body"/>: its tombstone, none when empty. The command borrows
    /// the body's memory rather than copying it, so the caller keeps it unchanged while the command is in use.
    /// </summary>
    public static ArchiveCommand Delete(ReadOnlyMemory<byte> body) => new(RowKind.Delete, 0, body.IsEmpty ? null : (ReadOnlyMemory<byte>?)body);

    /// <summary>Returns the command of a row of <paramref name="kind"/> with <paramref name="body"/>, which a <c>Delete</c> borrows.</summary>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is undefined.</exception>
    public static ArchiveCommand Of(RowKind kind, ReadOnlyMemory<byte> body) => kind switch
    {
        RowKind.Append => Append(body.Length),
        RowKind.Replace => Replace(body.Length),
        RowKind.Delete => Delete(body),
        RowKind.Message => throw new ArgumentException("RowKind.Message is excluded from archive commands (LG-I15).", nameof(kind)),
        _ => throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Row kind {(byte)kind} is not defined."), nameof(kind)),
    };

    /// <inheritdoc/>
    public override string ToString() =>
        Kind == RowKind.Delete
            ? string.Create(CultureInfo.InvariantCulture, $"Delete({Tombstone?.Length ?? 0}-byte tombstone)")
            : string.Create(CultureInfo.InvariantCulture, $"{Kind}({ContentLength})");
}

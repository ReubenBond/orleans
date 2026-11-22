using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// A live row of a key, scanned from a ledger and owned (archive.md §4): its tag, kind, version, segment, its defining
/// row's token, its body's length, whether the store's prefix holds it, and, when not held, its body, whose lease the row
/// owns. A held row carries only its body's length, since the prefix's content holds its bytes.
/// </summary>
public readonly struct ArchiveRow : IDisposable
{
    private ArchiveRow(Tag tag, RowKind kind, RowVersion version, LedgerSegmentId segment, RowToken token, int bodyLength, bool isHeld, ArcBuffer body)
    {
        Tag = tag;
        Kind = kind;
        Version = version;
        Segment = segment;
        Token = token;
        BodyLength = bodyLength;
        IsHeld = isHeld;
        Body = body;
    }

    /// <summary>Gets the row's tag.</summary>
    public Tag Tag { get; }

    /// <summary>Gets how the row changes its key's state.</summary>
    public RowKind Kind { get; }

    /// <summary>Gets the key's version after the row (LG-I13).</summary>
    public RowVersion Version { get; }

    /// <summary>Gets the identity of the row's segment.</summary>
    public LedgerSegmentId Segment { get; }

    /// <summary>Gets the key's token in the row's segment, which its defining row stores.</summary>
    public RowToken Token { get; }

    /// <summary>Gets the length of the row's body.</summary>
    public int BodyLength { get; }

    /// <summary>Gets a value indicating whether the store's prefix holds the row, applied or superseded.</summary>
    public bool IsHeld { get; }

    /// <summary>Gets the row's body, whose lease the row owns; the default for a held row.</summary>
    public ArcBuffer Body { get; }

    /// <summary>Makes a live row, not held, which retains its own lease on <paramref name="body"/>.</summary>
    /// <param name="tag">The row's tag.</param>
    /// <param name="kind">The row's kind.</param>
    /// <param name="version">The key's version after the row.</param>
    /// <param name="segment">The row's segment, in the row's ledger at or below its slot.</param>
    /// <param name="token">The key's token in the segment.</param>
    /// <param name="body">The row's body, of which the row retains its own lease; the caller keeps its own.</param>
    /// <exception cref="ArgumentException">The kind is undefined, or the segment is not of the row's ledger at or below its slot.</exception>
    public static ArchiveRow Live(Tag tag, RowKind kind, RowVersion version, LedgerSegmentId segment, RowToken token, ArcBuffer body)
    {
        if (!RowKinds.IsDefined(kind))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Row kind {(byte)kind} is not defined."), nameof(kind));
        }

        if (segment.Ledger != tag.Ledger || segment.Start > tag.Slot)
        {
            throw new ArgumentException("A row's segment lies in its ledger, at or below its slot.", nameof(segment));
        }

        return new ArchiveRow(tag, kind, version, segment, token, body.Length, isHeld: false, body.Retain());
    }

    /// <summary>
    /// Makes a live row, not held, carrying only its <paramref name="bodyLength"/> and no body buffer: used by metadata-only
    /// reads (<c>includeBodies: false</c>) for non-<c>Delete</c> rows whose callers inspect only <see cref="BodyLength"/>.
    /// </summary>
    internal static ArchiveRow LiveLengthOnly(Tag tag, RowKind kind, RowVersion version, LedgerSegmentId segment, RowToken token, int bodyLength)
    {
        Debug.Assert(kind is RowKind.Append or RowKind.Replace, "Only Append and Replace rows may omit their body.");
        Debug.Assert(bodyLength >= 0, "A row's body length is non-negative.");
        if (segment.Ledger != tag.Ledger || segment.Start > tag.Slot)
        {
            throw new ArgumentException("A row's segment lies in its ledger, at or below its slot.", nameof(segment));
        }

        return new ArchiveRow(tag, kind, version, segment, token, bodyLength, isHeld: false, default);
    }

    /// <summary>
    /// Makes a live row, not held, of a row a segment view lends, copying its body into <paramref name="bodies"/>, a
    /// writer the read owns, so the row retains only its own bytes and never the batch's pages, which hold other keys'
    /// rows (archive.md §4).
    /// </summary>
    /// <param name="row">The resolved row.</param>
    /// <param name="segment">The segment the view lent it in.</param>
    /// <param name="bodies">The writer into which the read copies its rows' bodies.</param>
    public static ArchiveRow Of(in ResolvedRow row, LedgerSegmentId segment, ArcBufferWriter bodies) =>
        Of(row, segment, bodies, includeBodies: true);

    internal static ArchiveRow Of(in ResolvedRow row, LedgerSegmentId segment, ArcBufferWriter bodies, bool includeBodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        if (!includeBodies && row.Kind != RowKind.Delete)
        {
            return LiveLengthOnly(row.Tag, row.Kind, row.Version, segment, row.Token, row.BodyLength);
        }

        Debug.Assert(bodies.Length == 0, "Each body is consumed as it is copied.");
        bodies.Write(row.Body);
        using var body = row.BodyLength == 0 ? default : bodies.ConsumeSlice(row.BodyLength);
        return Live(row.Tag, row.Kind, row.Version, segment, row.Token, body);
    }

    /// <summary>
    /// Returns the row's command in its key's write (archive.md §2.1): an <c>Append</c> or <c>Replace</c> by its body's
    /// length, or a <c>Delete</c> with a copy of its body, its tombstone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The row is a held <c>Delete</c>, whose body it no longer carries.</exception>
    public ArchiveCommand ToCommand() => Kind switch
    {
        RowKind.Append => ArchiveCommand.Append(BodyLength),
        RowKind.Replace => ArchiveCommand.Replace(BodyLength),
        RowKind.Delete when IsHeld && BodyLength > 0 => throw new InvalidOperationException("A held Delete carries no tombstone."),
        RowKind.Delete => ArchiveCommand.Delete(Body.ToArray()),
        _ => throw new UnreachableException("A row's kind is defined."),
    };

    /// <summary>Returns a row which holds its own lease on the body, which it retains without copying; the caller disposes it.</summary>
    public ArchiveRow Retain() => new(Tag, Kind, Version, Segment, Token, BodyLength, IsHeld, Body.Retain());

    /// <summary>Returns the row as the prefix holds it: held, with its body's length and no body.</summary>
    public ArchiveRow AsHeld() => new(Tag, Kind, Version, Segment, Token, BodyLength, isHeld: true, default);

    /// <summary>Releases the row's lease on its body.</summary>
    public void Dispose() => Body.Dispose();
}

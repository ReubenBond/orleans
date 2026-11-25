using System.Buffers;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// One row of a decoded <see cref="BatchView"/>, valid only for the step which lends it, holding what its batch alone
/// records (ledgers.md §2.2, §4; conventions.md §1).
/// </summary>
/// <remarks>
/// A defining row carries its key, as contiguous bytes (a view of the batch's page, or of a scratch array the enumerator
/// reuses when it spans pages), its key's version before it, and its key's token for the segment; a referencing row
/// carries only its key's ordinal, and its key, version, and token resolve through its segment's earlier batches
/// (<see cref="LedgerSegmentView"/>). The body is lent as a <see cref="ReadOnlySequence{T}"/> over the batch's buffer,
/// which its reader can neither release nor take; <see cref="RetainBody"/> returns an owned buffer of it, which outlives
/// the batch's. <see cref="Body"/> allocates only for a body which spans pages.
/// </remarks>
public readonly ref struct BatchRow
{

    private readonly ReadOnlySpan<byte> _keyOrChannel;

    // The body, when it lies in one page; otherwise empty, and lent from _page.
    private readonly ReadOnlyMemory<byte> _contiguousBody;

    // An alias of the batch's buffer from the start of the page which holds the body's first byte.
    private readonly ArcBuffer _page;
    private readonly int _bodyOffset;

    internal BatchRow(
        int index,
        RowKind kind,
        bool isDefining,
        ulong ordinal,
        ReadOnlySpan<byte> keyOrChannel,
        RowVersion versionBefore,
        RowToken token,
        ReadOnlyMemory<byte> contiguousBody,
        ArcBuffer page,
        int bodyOffset,
        int batchBodyOffset,
        int bodyLength)
    {
        Index = index;
        Kind = kind;
        IsDefining = isDefining;
        Ordinal = ordinal;
        _keyOrChannel = keyOrChannel;
        VersionBefore = versionBefore;
        Token = token;
        _contiguousBody = contiguousBody;
        _page = page;
        _bodyOffset = bodyOffset;
        BatchBodyOffset = batchBodyOffset;
        BodyLength = bodyLength;
    }

    /// <summary>Gets the row's index in its batch, from 0.</summary>
    public int Index { get; }

    /// <summary>Gets how the row changes its key's state.</summary>
    public RowKind Kind { get; }

    /// <summary>Gets a value indicating whether the row defines its key in its segment, rather than referencing it.</summary>
    public bool IsDefining { get; }

    /// <summary>
    /// Gets the ordinal of the row's key in its segment: stored on a referencing row, and implied on a defining one, the
    /// batch's ordinal base plus the defining rows before it; 0 for a <see cref="RowKind.Message"/> row.
    /// </summary>
    public ulong Ordinal { get; }

    /// <summary>Gets a defining row's key.</summary>
    /// <exception cref="InvalidOperationException">The row references its key or is a <see cref="RowKind.Message"/> row.</exception>
    public ReadOnlySpan<byte> Key => IsDefining ? _keyOrChannel : throw Referencing(nameof(Key));

    /// <summary>Gets a <see cref="RowKind.Message"/> row's validated ASCII channel bytes.</summary>
    /// <exception cref="InvalidOperationException">The row is not a <see cref="RowKind.Message"/> row.</exception>
    public ReadOnlySpan<byte> Channel => Kind == RowKind.Message ? _keyOrChannel : throw NotMessage(nameof(Channel));

    /// <summary>Gets a defining row's version of its key before the row, which its writer's caller supplied.</summary>
    /// <exception cref="InvalidOperationException">The row references its key or is a <see cref="RowKind.Message"/> row.</exception>
    public RowVersion VersionBefore => IsDefining ? field : throw Referencing(nameof(VersionBefore));

    /// <summary>Gets a defining row's token of its key for the segment, which its writer's caller supplied.</summary>
    /// <exception cref="InvalidOperationException">The row references its key or is a <see cref="RowKind.Message"/> row.</exception>
    public RowToken Token => IsDefining ? field : throw Referencing(nameof(Token));

    /// <summary>Gets the length of the row's body, in bytes.</summary>
    public int BodyLength { get; }

    /// <summary>Gets the byte offset of the row's body from the start of its batch's buffer.</summary>
    internal int BatchBodyOffset { get; }

    /// <summary>
    /// Gets the row's body, lent over the batch's buffer and valid while that buffer is. A body in one page is lent
    /// without allocating; one which spans pages allocates a segment per page each time it is read.
    /// </summary>
    public ReadOnlySequence<byte> Body =>
        _contiguousBody.Length == BodyLength
            ? new(_contiguousBody)
            : _page.UnsafeSlice(_bodyOffset, BodyLength).AsReadOnlySequence();

    /// <summary>
    /// Returns an owned buffer of the row's body, which retains its pages rather than copying them and outlives the
    /// batch's buffer; the caller disposes it. An empty body is the default buffer.
    /// </summary>
    public ArcBuffer RetainBody() => BodyLength == 0 ? default : _page.Slice(_bodyOffset, BodyLength);

    private static InvalidOperationException Referencing(string member) =>
        new($"A referencing or message row carries no {member}: only a defining archive row in the segment does (ledgers.md §2.2).");

    private static InvalidOperationException NotMessage(string member) =>
        new($"An archive row carries no {member}: only a Message row carries a channel (ledgers.md §2.2).");
}

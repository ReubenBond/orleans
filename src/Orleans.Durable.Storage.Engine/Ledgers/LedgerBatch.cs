using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A batch being written for one proposal of the <see cref="LedgerWriter"/> which created it (ledgers.md §2.4, §4, §7), at
/// the slot and with the segment state its creation assigned: its caller declared the row count, then writes each row
/// into the batch directly, its fields and then its body's bytes, with a running CRC-32C, so no <see cref="Row"/> is built
/// per row and each body is copied once.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LedgerWriter.CreateBatchAsync"/> creates a batch for its writer, which checks each row as it begins: its key
/// lies in the ledger's range, and the batch stays within its <c>maxLength</c> and the replica's payload limit. The
/// writer's dictionary decides whether each row defines its key in the segment, storing the key, its version before the
/// row, and its token, or references it by ordinal, and <see cref="BeginRow"/> returns the version the row receives
/// (LG-I12, LG-I13). A caller plans its batch, so a row the batch refuses is a failed precondition: the row writes
/// nothing and leaves the dictionary as it was, and the batch can never hold its declared rows.
/// <see cref="LedgerWriter.ProposeAsync"/> takes the batch's bytes once every declared row is written, after which the
/// batch is immutable.
/// </para>
/// <para>
/// The batch owns its bytes until a proposal takes them; <see cref="Dispose"/> releases what no proposal took, and
/// abandons the writer when the batch holds a slot and was never proposed, since every created batch is proposed or no
/// later batch is decided (LG-I12). A batch is written by one caller at a time. An exception while it writes leaves its
/// bytes and their CRC-32C out of step, so the batch is then unusable: every later write and proposal throws
/// <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
public sealed class LedgerBatch : IDisposable
{
    /// <summary>The most bytes of a row's fields and body head <see cref="BeginRow"/> lays out on the stack.</summary>
    private const int StackedRowLength = 512;

    /// <summary>A defining row's fields around its key and body, at their longest.</summary>
    private const int MaxDefiningFieldsLength = 1 + 3 + 1 + RowToken.MaxLength + Varint.MaxLength + 1 + RowToken.MaxLength + 5;

    // One CRC-32C per thread, which a batch borrows from the thread which creates it and returns to the thread which
    // completes or disposes it, so batches allocate none once warm.
    [ThreadStatic]
    private static Crc32? _spareCrc;

    private readonly KeyRange _range;
    private readonly LedgerSegmentDictionary _dictionary;
    private Crc32? _crc;
    private ArcBufferWriter? _writer;
    private int _bodyRemaining;
    private ulong _defined;
    private bool _poisoned;

    /// <summary>Initializes a new instance of the <see cref="LedgerBatch"/> class for a writer, and writes its header.</summary>
    /// <param name="owner">The writer whose proposal takes the batch.</param>
    /// <param name="range">The range every row's key lies in.</param>
    /// <param name="maxLength">The most bytes the batch holds: at most the replica's payload limit.</param>
    /// <param name="rowCount">The number of rows the batch holds: at least 1, or 0 for a marker, which starts a segment.</param>
    /// <param name="segment">The segment state the batch's creation assigned.</param>
    /// <param name="grant">The batch's slot, or <see langword="null"/> when it took none.</param>
    /// <param name="segmentId">The batch's segment's identity, or <see langword="null"/> when it took no slot.</param>
    internal LedgerBatch(LedgerWriter owner, KeyRange range, int maxLength, int rowCount, LedgerBatchSegment segment, SlotGrant? grant, LedgerSegmentId? segmentId)
        : this(range, maxLength, rowCount, segment, expectedLength: maxLength)
    {
        Owner = owner;
        Grant = grant;
        Segment = segmentId;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerBatch"/> class over a segment state in place of a writer, for the
    /// layer's tests and vectors, and writes its header: <c>CLLB</c>'s one encoder (ledgers.md §4).
    /// </summary>
    /// <param name="range">The range every row's key lies in.</param>
    /// <param name="maxLength">The most bytes the batch holds.</param>
    /// <param name="rowCount">The number of rows the batch holds: at least 1, or 0 for a marker, which starts a segment.</param>
    /// <param name="segment">The batch's segment state: its start flag, segment offset, ordinal base, and dictionary.</param>
    /// <param name="expectedLength">The batch's expected length, which chooses its pages (buffers.md §8), or 0.</param>
    /// <param name="segmentId">The segment the batch lies in, which a test names with the batch's <paramref name="slot"/>, or null.</param>
    /// <param name="slot">The slot a test places the batch at, with <paramref name="segmentId"/>.</param>
    /// <exception cref="ArgumentException">
    /// Only one of <paramref name="segmentId"/> and <paramref name="slot"/> is given, or the segment does not start at the
    /// slot exactly when the batch starts a segment, or starts after it.
    /// </exception>
    internal LedgerBatch(KeyRange range, int maxLength, int rowCount, LedgerBatchSegment segment, int expectedLength = 0, LedgerSegmentId? segmentId = null, Slot? slot = null)
    {
        if (segmentId.HasValue != slot.HasValue
            || segmentId is { } id && slot is { } at && (id.Start > at || segment.IsStart != (id.Start == at)))
        {
            throw new ArgumentException("A test batch names its segment with its slot, where the segment starts exactly when the batch starts one.", nameof(segmentId));
        }

        Segment = segmentId;
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        ArgumentNullException.ThrowIfNull(segment.Dictionary);
        if (rowCount == 0 && !segment.IsStart)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount), "Only a marker, which starts a segment, holds no rows.");
        }

        if (segment.IsStart != (segment.Offset == 0) || segment.IsStart && segment.OrdinalBase != 0)
        {
            throw new ArgumentException("A batch which starts a segment has offset 0 and ordinal base 0, and only it has offset 0.", nameof(segment));
        }

        _range = range;
        _dictionary = segment.Dictionary;
        IsSegmentStart = segment.IsStart;
        LedgerSegmentOffset = segment.Offset;
        OrdinalBase = segment.OrdinalBase;
        DeclaredRowCount = rowCount;
        Span<byte> header = stackalloc byte[MaxHeaderLength(rowCount) - BatchView.CrcLength];
        BatchView.WriteFixedHeader(header, segment.IsStart);
        var headerLength = BatchView.FixedHeaderLength;
        headerLength += Varint.Write(header[headerLength..], segment.Offset);
        headerLength += Varint.Write(header[headerLength..], segment.OrdinalBase);
        headerLength += Varint.Write(header[headerLength..], (ulong)rowCount);
        HeaderLength = headerLength + BatchView.CrcLength;
        if (HeaderLength > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLength),
                maxLength,
                string.Create(CultureInfo.InvariantCulture, $"A batch of {rowCount} rows takes {HeaderLength} bytes with no rows."));
        }

        MaxLength = maxLength;
        Length = HeaderLength;
        _crc = _spareCrc ?? new Crc32(Crc32ParameterSet.Crc32C);
        _spareCrc = null;
        _crc.Reset();
        _writer = expectedLength > 0 ? new ArcBufferWriter(expectedLength) : new ArcBufferWriter();
        Write(header[..headerLength]);
    }

    /// <summary>Gets the batch's slot, which its creation assigned, or <see langword="null"/> when it took none.</summary>
    /// <remarks>A batch takes no slot when its writer's lifetime had stopped; its proposal is <c>Fenced</c> at once.</remarks>
    public Slot? Slot => Grant?.Slot;

    /// <summary>
    /// Gets the identity of the batch's segment, <c>(ledger, start slot)</c>, or <see langword="null"/> when a writer's
    /// batch took no slot, which is the one sign of such a batch, or a test's batch names none.
    /// </summary>
    public LedgerSegmentId? Segment { get; }

    /// <summary>Gets a value indicating whether the batch begins a segment: it carries the start flag.</summary>
    public bool IsSegmentStart { get; }

    /// <summary>Gets the slots back to the batch's segment's first slot: 0 exactly when it begins a segment.</summary>
    public ulong LedgerSegmentOffset { get; }

    /// <summary>Gets the number of keys the batch's segment defined before it.</summary>
    public ulong OrdinalBase { get; }

    /// <summary>Gets the number of rows the batch holds once it is complete, which its creator declared.</summary>
    public int DeclaredRowCount { get; }

    /// <summary>Gets the number of rows begun.</summary>
    public int RowsBegun { get; private set; }

    /// <summary>Gets the batch's length with no rows: its header and its CRC-32C.</summary>
    public int HeaderLength { get; }

    /// <summary>Gets the batch's encoded length once it is complete: the bytes written and declared, and the CRC-32C.</summary>
    public int Length { get; private set; }

    /// <summary>Gets the writer whose proposal takes the batch, or <see langword="null"/> for the codec's own use.</summary>
    internal LedgerWriter? Owner { get; }

    /// <summary>Gets the batch's slot, as its writer's lifetime granted it, or <see langword="null"/> when it took none.</summary>
    internal SlotGrant? Grant { get; }

    /// <summary>Gets the batch's creation number in its writer, from 1, or 0 for a batch which took no slot.</summary>
    internal long Creation { get; init; }

    /// <summary>Gets the most bytes the batch holds, which its creation reserved from its writer's proposal budget.</summary>
    internal int MaxLength { get; }

    /// <summary>Gets a value indicating whether every declared row and body byte is written.</summary>
    internal bool IsComplete => RowsBegun == DeclaredRowCount && _bodyRemaining == 0;

    /// <summary>Gets a value indicating whether a proposal took the batch's bytes.</summary>
    internal bool IsProposed { get; private set; }

    /// <summary>Gets the batch's writer while it is usable.</summary>
    /// <exception cref="InvalidOperationException">The batch was proposed or disposed, or a write failed.</exception>
    private ArcBufferWriter Writer =>
        _poisoned ? throw new InvalidOperationException("A write to the batch failed, which left it unusable.")
        : _writer ?? throw new InvalidOperationException("The batch was proposed or disposed.");

    /// <summary>
    /// Returns the most bytes a batch of <paramref name="rowCount"/> rows takes with no rows: its header, with a segment
    /// offset and ordinal base of any value, and its CRC-32C. A caller bounds a batch with it before creating one.
    /// </summary>
    public static int MaxHeaderLength(int rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        return BatchView.FixedHeaderLength + 2 * Varint.MaxLength + Varint.Length((ulong)rowCount) + BatchView.CrcLength;
    }

    /// <summary>
    /// Returns a row's length as a defining row, which bounds it either way (ledgers.md §7): a key of
    /// <paramref name="keyLength"/> bytes, a version base of <paramref name="baseLength"/> and a token of
    /// <paramref name="tokenLength"/>, a version count of any value, and a body of <paramref name="bodyLength"/>.
    /// </summary>
    public static long MaxRowLength(int keyLength, int baseLength, int tokenLength, long bodyLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyLength);
        ArgumentOutOfRangeException.ThrowIfNegative(baseLength);
        ArgumentOutOfRangeException.ThrowIfNegative(tokenLength);
        ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
        return DefiningLength(keyLength, baseLength, Varint.MaxLength, tokenLength, bodyLength);
    }

    /// <summary>
    /// Returns the encoded length of a <see cref="RowKind.Message"/> row with a channel of
    /// <paramref name="channelLength"/> bytes and a body of <paramref name="bodyLength"/> bytes (ledgers.md §7).
    /// </summary>
    public static long MaxMessageLength(int channelLength, long bodyLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channelLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channelLength, MessageChannel.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
        return MessageEncodedLength(channelLength, bodyLength);
    }

    /// <summary>
    /// Returns the exact bytes a <see cref="RowKind.Message"/> row for <paramref name="channel"/> with a body of
    /// <paramref name="bodyLength"/> bytes adds to a batch.
    /// </summary>
    public static long MessageLength(MessageChannel channel, long bodyLength)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
        return MessageEncodedLength(channel.Span.Length, bodyLength);
    }

    /// <summary>
    /// Returns the bytes the row would add to this batch, as a defining or a referencing row as the writer's dictionary
    /// says, so a caller plans its batch.
    /// </summary>
    /// <exception cref="InvalidOperationException">The batch was proposed or disposed, after which another batch may be writing its dictionary.</exception>
    public long RowLength(ReadOnlySpan<byte> key, RowVersion versionBefore, RowToken token, long bodyLength)
    {
        _ = Writer;
        ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
        ref var entry = ref _dictionary.Find(key);
        return !Unsafe.IsNullRef(ref entry)
            ? ReferencingLength(entry.Ordinal, bodyLength)
            : DefiningLength(key.Length, versionBefore.Base.Length, Varint.Length(versionBefore.Count), token.Length, bodyLength);
    }

    /// <summary>
    /// Returns the version the writer's dictionary holds for <paramref name="key"/> in the batch's segment, which a
    /// referencing row's caller passes as its version before, or the default when the segment has not defined the key.
    /// </summary>
    internal RowVersion HeldVersion(ReadOnlySpan<byte> key) => _dictionary.VersionOf(key);

    /// <summary>
    /// Returns whether the key's next row in the batch defines it, storing its version and token: its segment has no row
    /// of the key yet (ledgers.md §7). Journals' admission asserts its token roll against it (journals.md §8.1 step 5).
    /// </summary>
    internal bool Defines(ReadOnlySpan<byte> key) => Unsafe.IsNullRef(ref _dictionary.Find(key));
    /// <summary>
    /// Begins the next row, writing its fields and its body's first bytes, <paramref name="bodyHead"/>, and declaring its
    /// body's length, whose remaining bytes <see cref="WriteBody(ReadOnlySpan{byte})"/> writes before the next row begins;
    /// a short row is one call. The row defines its key, storing <paramref name="versionBefore"/> and
    /// <paramref name="token"/>, when the writer's dictionary has not defined it in the segment, and references it
    /// otherwise.
    /// </summary>
    /// <param name="key">The row's key, which lies in the ledger's range.</param>
    /// <param name="kind">How the row changes its key's state.</param>
    /// <param name="versionBefore">The key's version before the row, whose count is below the <c>u64</c> maximum.</param>
    /// <param name="token">The key's token for the segment.</param>
    /// <param name="bodyLength">The body's length.</param>
    /// <param name="bodyHead">The body's first bytes, at most <paramref name="bodyLength"/>.</param>
    /// <returns>
    /// The version the row receives, which its caller adopts (LG-I13): <paramref name="versionBefore"/>'s next for a
    /// defining row, and for a referencing row the next of the version the dictionary holds for the key, which a Debug
    /// assertion checks equals <paramref name="versionBefore"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The key is empty, longer than <see cref="KeyRange.MaxKeyLength"/> bytes, or outside the ledger's range; the kind
    /// is undefined; the version's count is at the <c>u64</c> maximum; the head is longer than the body; or the batch would
    /// pass its <c>maxLength</c> or the replica's payload limit (ledgers.md §7). Nothing is written, and the dictionary is
    /// unchanged.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The previous row's body is incomplete, every declared row has begun, or the batch was proposed, disposed, or left
    /// unusable by a failed write.
    /// </exception>
    public RowVersion BeginRow(ReadOnlySpan<byte> key, RowKind kind, RowVersion versionBefore, RowToken token, int bodyLength, ReadOnlySpan<byte> bodyHead = default)
    {
        _ = Writer;
        if (_bodyRemaining != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Row {RowsBegun - 1}'s body lacks {_bodyRemaining} bytes."));
        }

        if (RowsBegun == DeclaredRowCount)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The batch holds {DeclaredRowCount} rows, and every one has begun."));
        }

        KeyRange.CheckKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bodyHead.Length, bodyLength, nameof(bodyHead));
        if (!_range.Contains(key))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Row {RowsBegun}'s key lies outside the ledger's range {_range}."), nameof(key));
        }

        if (!RowKinds.IsArchiveKind(kind))
        {
            throw new ArgumentException(
                kind == RowKind.Message
                    ? "Use BeginMessage to write a Message row."
                    : string.Create(CultureInfo.InvariantCulture, $"Row kind {(byte)kind} is not defined."),
                nameof(kind));
        }

        // The reference stays valid through the row's write, which changes no dictionary.
        ref var entry = ref _dictionary.Find(key);
        var references = !Unsafe.IsNullRef(ref entry);
        var before = references ? entry.Version : versionBefore;
        Debug.Assert(!references || versionBefore == before, "A referencing row's caller passes the version the writer's dictionary holds for its key (LG-I13).");
        if (before.Count == ulong.MaxValue)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The key's version {before} is at the u64 maximum, so no row can advance it."),
                nameof(versionBefore));
        }

        var rowLength = references
            ? ReferencingLength(entry.Ordinal, bodyLength)
            : DefiningLength(key.Length, versionBefore.Base.Length, Varint.Length(versionBefore.Count), token.Length, bodyLength);
        var length = Length + rowLength;
        if (length > MaxLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The batch would be {length} bytes, longer than the {MaxLength} bytes it holds, its maxLength, at most the replica's payload limit."),
                nameof(bodyLength));
        }

        var version = before.Next();
        WriteFields(key, kind, references, references ? entry.Ordinal : 0, versionBefore, token, bodyLength, bodyHead);
        if (references)
        {
            entry.Count = version.Count;
        }
        else
        {
            // A defining row's ordinal is implied: the batch's ordinal base plus the defining rows before it.
            _dictionary.Define(key, unchecked(OrdinalBase + _defined), version);
            _defined++;
        }

        Length = (int)length;
        _bodyRemaining = bodyLength - bodyHead.Length;
        RowsBegun++;
        return version;
    }

    /// <summary>
    /// Begins an unversioned <see cref="RowKind.Message"/> row for <paramref name="channel"/>, writing its flags,
    /// channel, body length, and <paramref name="bodyHead"/> directly into the batch without touching the segment
    /// dictionary or key range (ledgers.md §2.2, §7).
    /// </summary>
    public void BeginMessage(MessageChannel channel, int bodyLength, ReadOnlySpan<byte> bodyHead = default)
    {
        _ = Writer;
        ArgumentNullException.ThrowIfNull(channel);
        if (_bodyRemaining != 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Row {RowsBegun - 1}'s body lacks {_bodyRemaining} bytes."));
        }

        if (RowsBegun == DeclaredRowCount)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The batch holds {DeclaredRowCount} rows, and every one has begun."));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bodyHead.Length, bodyLength, nameof(bodyHead));

        var channelBytes = channel.Span;
        var rowLength = MessageEncodedLength(channelBytes.Length, bodyLength);
        var length = Length + rowLength;
        if (length > MaxLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The batch would be {length} bytes, longer than the {MaxLength} bytes it holds, its maxLength, at most the replica's payload limit."),
                nameof(bodyLength));
        }

        Span<byte> fields = stackalloc byte[StackedRowLength];
        fields[0] = (byte)RowKind.Message;
        fields[1] = (byte)channelBytes.Length;
        channelBytes.CopyTo(fields[2..]);
        var offset = 2 + channelBytes.Length;
        offset += Varint.Write(fields[offset..], (ulong)bodyLength);
        if (offset + bodyHead.Length <= StackedRowLength)
        {
            bodyHead.CopyTo(fields[offset..]);
            Write(fields[..(offset + bodyHead.Length)]);
        }
        else
        {
            Write(fields[..offset]);
            Write(bodyHead);
        }

        Length = (int)length;
        _bodyRemaining = bodyLength - bodyHead.Length;
        RowsBegun++;
    }

    /// <summary>Writes one <see cref="RowKind.Message"/> row whole: <see cref="BeginMessage"/>, then its body.</summary>
    public void AddMessage(MessageChannel channel, ReadOnlySequence<byte> body)
    {
        if (body.Length > int.MaxValue)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A message's body is at most {int.MaxValue} bytes, not {body.Length}."), nameof(body));
        }

        BeginMessage(channel, (int)body.Length);
        WriteBody(body);
    }

    /// <summary>Writes the next bytes of the current row's body.</summary>
    /// <exception cref="InvalidOperationException">
    /// The bytes run past the body's declared length, or the batch was proposed, disposed, or left unusable by a failed
    /// write.
    /// </exception>
    public void WriteBody(ReadOnlySpan<byte> bytes)
    {
        _ = Writer;
        if (bytes.Length > _bodyRemaining)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Row {RowsBegun - 1}'s body has {_bodyRemaining} bytes left, not {bytes.Length}."));
        }

        Write(bytes);
        _bodyRemaining -= bytes.Length;
    }

    /// <summary>Writes the next bytes of the current row's body.</summary>
    /// <exception cref="InvalidOperationException">
    /// The bytes run past the body's declared length, or the batch was proposed, disposed, or left unusable by a failed
    /// write.
    /// </exception>
    public void WriteBody(ReadOnlySequence<byte> bytes)
    {
        _ = Writer;
        if (bytes.Length > _bodyRemaining)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Row {RowsBegun - 1}'s body has {_bodyRemaining} bytes left, not {bytes.Length}."));
        }

        foreach (var segment in bytes)
        {
            WriteBody(segment.Span);
        }
    }

    /// <summary>Writes one row whole: <see cref="BeginRow"/>, then its body.</summary>
    /// <returns>The version the row receives, as <see cref="BeginRow"/> returns it.</returns>
    /// <exception cref="ArgumentException">As <see cref="BeginRow"/> throws it, or a body longer than <see cref="int.MaxValue"/> bytes; nothing is written.</exception>
    /// <exception cref="InvalidOperationException">As <see cref="BeginRow"/> throws it.</exception>
    public RowVersion AddRow(ReadOnlySpan<byte> key, RowKind kind, RowVersion versionBefore, RowToken token, ReadOnlySequence<byte> body)
    {
        if (body.Length > int.MaxValue)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A row's body is at most {int.MaxValue} bytes, not {body.Length}."), nameof(body));
        }

        var version = BeginRow(key, kind, versionBefore, token, (int)body.Length);
        WriteBody(body);
        return version;
    }

    /// <summary>
    /// Releases the batch's bytes, unless a proposal took them; a batch which holds a slot and was never proposed abandons
    /// its writer (ledgers.md §2.4).
    /// </summary>
    public void Dispose()
    {
        var unproposed = _writer is not null && !IsProposed;
        _writer?.Dispose();
        _writer = null;
        ReturnCrc();
        if (unproposed)
        {
            Owner?.OnUnproposed(this);
        }
    }

    /// <summary>Writes <paramref name="row"/>, which the caller keeps, as the next row.</summary>
    internal RowVersion AddRow(in Row row, RowVersion versionBefore, RowToken token)
    {
        var version = BeginRow(row.Key.Span, row.Kind, versionBefore, token, row.Body.Length);
        foreach (var span in row.Body)
        {
            WriteBody(span);
        }

        return version;
    }

    /// <summary>
    /// Appends the CRC-32C of the bytes written and returns them, whose ownership passes to the caller; the batch is then
    /// proposed and holds nothing, and every later write throws. The caller checked that the batch is complete.
    /// </summary>
    /// <exception cref="InvalidOperationException">The batch was proposed, disposed, or left unusable by a failed write.</exception>
    internal ArcBuffer Complete()
    {
        var writer = Writer;
        Debug.Assert(IsComplete, "A batch completes only once every declared row and body byte is written.");
        Span<byte> crc = stackalloc byte[BatchView.CrcLength];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, _crc!.GetCurrentHashAsUInt32());
        writer.Write(crc);
        var bytes = writer.ConsumeSlice(writer.Length);
        Debug.Assert(bytes.Length == Length, "The rows fill the batch's encoded length.");
        writer.Dispose();
        _writer = null;
        IsProposed = true;
        ReturnCrc();
        return bytes;
    }

    private static long DefiningLength(int keyLength, int baseLength, int countLength, int tokenLength, long bodyLength) =>
        1 + Varint.Length((ulong)keyLength) + (long)keyLength + 1 + baseLength + countLength + 1 + tokenLength + Varint.Length((ulong)bodyLength) + bodyLength;

    private static long ReferencingLength(ulong ordinal, long bodyLength) =>
        1 + Varint.Length(ordinal) + Varint.Length((ulong)bodyLength) + bodyLength;

    private static long MessageEncodedLength(int channelLength, long bodyLength) =>
        1 + 1 + channelLength + Varint.Length((ulong)bodyLength) + bodyLength;

    /// <summary>Lays out a row's fields up to its key, or its referencing fields and body length, in <paramref name="destination"/>.</summary>
    private static int WriteLeading(Span<byte> destination, ReadOnlySpan<byte> key, RowKind kind, bool references, ulong ordinal, int bodyLength)
    {
        destination[0] = (byte)((byte)kind | (references ? 0 : BatchView.DefiningFlag));
        if (!references)
        {
            return 1 + Varint.Write(destination[1..], (ulong)key.Length);
        }

        var length = 1 + Varint.Write(destination[1..], ordinal);
        return length + Varint.Write(destination[length..], (ulong)bodyLength);
    }

    /// <summary>Lays out a defining row's fields after its key: its version before, its token, and its body's length.</summary>
    private static int WriteTrailing(Span<byte> destination, RowVersion versionBefore, RowToken token, int bodyLength)
    {
        var offset = RowVersionCodec.Write(destination, versionBefore);
        destination[offset++] = (byte)token.Length;
        token.AsSpan().CopyTo(destination[offset..]);
        offset += token.Length;
        offset += Varint.Write(destination[offset..], (ulong)bodyLength);
        return offset;
    }

    /// <summary>Writes a row's fields and its body's head.</summary>
    private void WriteFields(ReadOnlySpan<byte> key, RowKind kind, bool references, ulong ordinal, RowVersion versionBefore, RowToken token, int bodyLength, ReadOnlySpan<byte> bodyHead)
    {
        Span<byte> fields = stackalloc byte[StackedRowLength];
        var leading = WriteLeading(fields, key, kind, references, ordinal, bodyLength);
        if (references)
        {
            if (leading + bodyHead.Length <= StackedRowLength)
            {
                bodyHead.CopyTo(fields[leading..]);
                Write(fields[..(leading + bodyHead.Length)]);
            }
            else
            {
                Write(fields[..leading]);
                Write(bodyHead);
            }

            return;
        }

        if (MaxDefiningFieldsLength + key.Length + bodyHead.Length <= StackedRowLength)
        {
            key.CopyTo(fields[leading..]);
            var length = leading + key.Length;
            length += WriteTrailing(fields[length..], versionBefore, token, bodyLength);
            bodyHead.CopyTo(fields[length..]);
            Write(fields[..(length + bodyHead.Length)]);
            return;
        }

        Write(fields[..leading]);
        Write(key);
        var trailing = WriteTrailing(fields, versionBefore, token, bodyLength);
        Write(fields[..trailing]);
        Write(bodyHead);
    }

    /// <summary>Writes <paramref name="bytes"/> and adds them to the CRC-32C; a failure leaves the batch unusable.</summary>
    private void Write(ReadOnlySpan<byte> bytes)
    {
        var writer = Writer;
        try
        {
            _crc!.Append(bytes);
            writer.Write(bytes);
        }
        catch
        {
            _poisoned = true;
            throw;
        }
    }

    private void ReturnCrc()
    {
        if (_crc is not null)
        {
            _spareCrc ??= _crc;
            _crc = null;
        }
    }
}

/// <summary>
/// A batch's segment state, which its creation assigns (ledgers.md §8.2): whether it starts a segment, its segment offset
/// and ordinal base, and the dictionary of its segment, which decides whether each of its rows defines or references its
/// key.
/// </summary>
/// <param name="IsStart">Whether the batch starts a segment.</param>
/// <param name="Offset">The slots back to the segment's first slot: 0 exactly when the batch starts one.</param>
/// <param name="OrdinalBase">The keys the segment defined before the batch: 0 when it starts one.</param>
/// <param name="Dictionary">The segment's dictionary, which the batch's rows advance.</param>
internal readonly record struct LedgerBatchSegment(bool IsStart, ulong Offset, ulong OrdinalBase, LedgerSegmentDictionary Dictionary);

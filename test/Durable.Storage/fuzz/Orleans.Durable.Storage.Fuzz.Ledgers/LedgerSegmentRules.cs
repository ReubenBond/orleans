using System.Buffers.Binary;
using System.IO.Hashing;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// One reading of the batch format's rules, <c>CLLB</c> version 1, and of the segment rules (ledgers.md §7), written apart
/// from the decoder over contiguous bytes with 64-bit offsets and exact arithmetic: the oracle of the
/// <c>ledgers-batch</c> and <c>ledgers-segment</c> targets and of the Ledgers tests' batch and segment properties.
/// </summary>
/// <remarks>
/// It checks the batch rules procedurally, in ledgers.md §7's order: the whole batch, each header varint in layout order,
/// each row and each of its fields in layout order (each first to end at or before the CRC-32C, which a varint meets when
/// it reaches the CRC-32C unterminated, then a varint's form, then the field's own rule), and then the trailing bytes.
/// A segment's batches are read in slot order, batch rules before segment rules, and within a batch the segment rules
/// procedurally too: its offset, its ordinal base, then each row in order, a defining row's key against the segment's
/// before its count. It names the first rule the bytes break by its reason code; otherwise it gives the rows the bytes
/// hold, and a segment's resolved rows.
/// </remarks>
public static class LedgerSegmentRules
{
    private const int FixedHeader = 9;
    private const int Trailer = 4;
    private const int MaxVarint = 10;
    private const int MaxToken = 32;
    private const int MaxKey = 65_535;

    private enum VarintRead
    {
        Valid,
        Unterminated,
        Invalid,
    }

    /// <summary>Reads one batch as the batch rules give it.</summary>
    public static BatchReading ReadBatch(ReadOnlySpan<byte> batch)
    {
        if (batch.Length < FixedHeader + Trailer)
        {
            return BatchReading.Rejected("truncated");
        }

        if (!batch[..4].SequenceEqual("CLLB"u8))
        {
            return BatchReading.Rejected("bad-magic");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(batch[4..]) != 1)
        {
            return BatchReading.Rejected("bad-version");
        }

        var crcAt = batch.Length - Trailer;
        if (BinaryPrimitives.ReadUInt32LittleEndian(batch[crcAt..]) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, batch[..crcAt]))
        {
            return BatchReading.Rejected("crc-mismatch");
        }

        var flags = batch[8];
        if ((flags & 0xfe) != 0)
        {
            return BatchReading.Rejected("bad-flags");
        }

        var start = flags == 1;
        var fields = new Fields(batch[..crcAt], FixedHeader);
        if (fields.Varint(out var offset) != VarintRead.Valid)
        {
            return BatchReading.Rejected("bad-varint");
        }

        if (start != (offset == 0))
        {
            return BatchReading.Rejected("bad-segment-offset");
        }

        if (fields.Varint(out var ordinalBase) != VarintRead.Valid)
        {
            return BatchReading.Rejected("bad-varint");
        }

        if (start && ordinalBase != 0)
        {
            return BatchReading.Rejected("bad-ordinal-base");
        }

        if (fields.Varint(out var count) != VarintRead.Valid)
        {
            return BatchReading.Rejected("bad-varint");
        }

        if (!start && count == 0)
        {
            return BatchReading.Rejected("no-rows");
        }

        var rows = new List<RuleRow>();
        var defining = (UInt128)0;
        for (var row = (UInt128)0; row < count; row++)
        {
            if (!fields.Take(1, out var rowFlags))
            {
                return BatchReading.Rejected("row-truncated");
            }

            var kind = (byte)(rowFlags[0] & 3);
            if ((rowFlags[0] & 0xf8) != 0)
            {
                return BatchReading.Rejected("bad-row-flags");
            }

            var isDefining = (rowFlags[0] & 4) != 0;
            byte[]? key = null;
            byte[]? versionBase = null;
            byte[]? token = null;
            ulong versionCount = 0;
            ulong ordinal;
            if (kind == 3)
            {
                if (isDefining)
                {
                    return BatchReading.Rejected("bad-row-flags");
                }

                if (!fields.Take(1, out var chLenBytes))
                {
                    return BatchReading.Rejected("row-truncated");
                }

                var chLen = chLenBytes[0];
                if (chLen is 0 or > 64)
                {
                    return BatchReading.Rejected("bad-channel");
                }

                if (!fields.Take(chLen, out var chBytes))
                {
                    return BatchReading.Rejected("row-truncated");
                }

                foreach (var b in chBytes)
                {
                    if (b is not (>= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'_' or (byte)'-'))
                    {
                        return BatchReading.Rejected("bad-channel");
                    }
                }

                key = chBytes.ToArray();
                ordinal = 0;
            }
            else if (isDefining)
            {
                if (RowVarint(ref fields, out var keyLength) is { } badKeyLength)
                {
                    return BatchReading.Rejected(badKeyLength);
                }

                if (keyLength == 0)
                {
                    return BatchReading.Rejected("empty-key");
                }

                if (keyLength > MaxKey)
                {
                    return BatchReading.Rejected("key-too-long");
                }

                if (!fields.Take((long)keyLength, out var keyBytes))
                {
                    return BatchReading.Rejected("row-truncated");
                }

                key = keyBytes.ToArray();
                if (TokenOf(ref fields, "base-too-long", out versionBase) is { } badBase)
                {
                    return BatchReading.Rejected(badBase);
                }

                if (RowVarint(ref fields, out versionCount) is { } badCount)
                {
                    return BatchReading.Rejected(badCount);
                }

                if (TokenOf(ref fields, "token-too-long", out token) is { } badToken)
                {
                    return BatchReading.Rejected(badToken);
                }

                ordinal = (ulong)((ordinalBase + defining) & ulong.MaxValue);
                defining++;
            }
            else
            {
                if (RowVarint(ref fields, out ordinal) is { } badOrdinal)
                {
                    return BatchReading.Rejected(badOrdinal);
                }

                if (ordinal >= ordinalBase + defining)
                {
                    return BatchReading.Rejected("forward-ordinal");
                }
            }

            if (RowVarint(ref fields, out var bodyLength) is { } badBodyLength)
            {
                return BatchReading.Rejected(badBodyLength);
            }

            if (bodyLength > (ulong)fields.Remaining || !fields.Take((long)bodyLength, out var body))
            {
                return BatchReading.Rejected("row-truncated");
            }

            rows.Add(new RuleRow(kind, isDefining, ordinal, key, versionBase, versionCount, token, body.ToArray()));
        }

        return fields.Remaining == 0
            ? BatchReading.Accepted(new RuleBatch(start, offset, ordinalBase, rows))
            : BatchReading.Rejected("trailing-bytes");
    }

    /// <summary>
    /// Reads the batches of one ledger at slots <paramref name="firstSlot"/>, <paramref name="firstSlot"/> + 1, …, as a
    /// segment decoder does from a slot at which a segment starts: each batch by the batch rules, then by the segment
    /// rules, the first failure winning. With <paramref name="key"/>, as a key-filtered decoder does (ledgers.md §4): each
    /// batch's segment offset and ordinal base are checked, <c>duplicate-key</c> and <c>count-overflow</c> only for rows of
    /// that key, and only its rows are resolved.
    /// </summary>
    public static LedgerSegmentReading ReadSegment(IReadOnlyList<byte[]> batches, long firstSlot, byte[]? key = null)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var keys = new List<RuleKey>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var resolved = new List<ResolvedRuleRow>();
        var started = false;
        var start = 0L;
        for (var index = 0; index < batches.Count; index++)
        {
            var slot = firstSlot + index;
            var reading = ReadBatch(batches[index]);
            if (reading.Reason is { } reason)
            {
                return LedgerSegmentReading.Rejected(index, reason);
            }

            var batch = reading.Batch!;
            if (batch.IsStart)
            {
                started = true;
                start = slot;
                keys.Clear();
                ordinals.Clear();
            }
            else if (!started || batch.Offset != (ulong)(slot - start))
            {
                return LedgerSegmentReading.Rejected(index, "segment-offset-mismatch");
            }

            if (batch.OrdinalBase != (ulong)keys.Count)
            {
                return LedgerSegmentReading.Rejected(index, "ordinal-base-mismatch");
            }

            for (var rowIndex = 0; rowIndex < batch.Rows.Count; rowIndex++)
            {
                var row = batch.Rows[rowIndex];
                if (row.Kind == 3)
                {
                    if (key is null)
                    {
                        resolved.Add(new ResolvedRuleRow(slot, rowIndex, row.Key!, row.Kind, [], 0, [], row.Body));
                    }

                    continue;
                }

                RuleKey entry;
                if (row.IsDefining)
                {
                    var name = Convert.ToHexString(row.Key!);
                    var mine = key is null || row.Key.AsSpan().SequenceEqual(key);
                    if (!mine)
                    {
                        // A filtered decoder counts another key's definition, for the ordinal bases, and checks it no further.
                        if (!ordinals.ContainsKey(name))
                        {
                            ordinals.Add(name, keys.Count);
                        }

                        keys.Add(new RuleKey(row.Key!, row.VersionBase!, row.Token!, row.VersionCount) { Skipped = true });
                        continue;
                    }

                    if (ordinals.TryGetValue(name, out var defined) && !keys[defined].Skipped)
                    {
                        return LedgerSegmentReading.Rejected(index, "duplicate-key");
                    }

                    if (row.VersionCount == ulong.MaxValue)
                    {
                        return LedgerSegmentReading.Rejected(index, "count-overflow");
                    }

                    entry = new RuleKey(row.Key!, row.VersionBase!, row.Token!, row.VersionCount);
                    ordinals[name] = keys.Count;
                    keys.Add(entry);
                }
                else
                {
                    entry = keys[checked((int)row.Ordinal)];
                    if (entry.Skipped)
                    {
                        continue;
                    }

                    if (entry.Count == ulong.MaxValue)
                    {
                        return LedgerSegmentReading.Rejected(index, "count-overflow");
                    }
                }

                entry.Count++;
                resolved.Add(new ResolvedRuleRow(slot, rowIndex, entry.Key, row.Kind, entry.Base, entry.Count, entry.Token, row.Body));
            }
        }

        return LedgerSegmentReading.Accepted(resolved);
    }

    /// <summary>
    /// Returns <paramref name="batch"/> with its last 4 bytes replaced by the CRC-32C of the bytes before them, or
    /// <see langword="null"/> when it is too short to hold its fixed header and CRC-32C.
    /// </summary>
    public static byte[]? Seal(ReadOnlySpan<byte> batch)
    {
        if (batch.Length < FixedHeader + Trailer)
        {
            return null;
        }

        var bytes = batch.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - Trailer), Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes.AsSpan(0, bytes.Length - Trailer)));
        return bytes;
    }

    /// <summary>
    /// Writes <paramref name="batch"/> in the format's layout, with its CRC-32C, as the rules read it: the defining rows'
    /// keys, version bases, counts, and tokens, and the referencing rows' ordinals, as given.
    /// </summary>
    public static byte[] Encode(RuleBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var bytes = new List<byte>();
        bytes.AddRange("CLLB"u8.ToArray());
        bytes.AddRange([1, 0, 0, 0]);
        bytes.Add(batch.IsStart ? (byte)1 : (byte)0);
        AddVarint(bytes, batch.Offset);
        AddVarint(bytes, batch.OrdinalBase);
        AddVarint(bytes, (ulong)batch.Rows.Count);
        foreach (var row in batch.Rows)
        {
            bytes.Add((byte)(row.Kind | (row.IsDefining ? 4 : 0)));
            if (row.Kind == 3)
            {
                bytes.Add((byte)row.Key!.Length);
                bytes.AddRange(row.Key);
            }
            else if (row.IsDefining)
            {
                AddVarint(bytes, (ulong)row.Key!.Length);
                bytes.AddRange(row.Key);
                bytes.Add((byte)row.VersionBase!.Length);
                bytes.AddRange(row.VersionBase);
                AddVarint(bytes, row.VersionCount);
                bytes.Add((byte)row.Token!.Length);
                bytes.AddRange(row.Token);
            }
            else
            {
                AddVarint(bytes, row.Ordinal);
            }

            AddVarint(bytes, (ulong)row.Body.Length);
            bytes.AddRange(row.Body);
        }

        bytes.AddRange(new byte[Trailer]);
        return Seal([.. bytes])!;
    }

    /// <summary>Appends <paramref name="value"/> as a varint in its shortest form.</summary>
    public static void AddVarint(List<byte> bytes, ulong value)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        do
        {
            var next = (byte)(value & 0x7f);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        }
        while (value != 0);
    }

    /// <summary>A row's varint: <c>row-truncated</c> when it reaches the CRC-32C unterminated, <c>bad-varint</c> otherwise.</summary>
    private static string? RowVarint(ref Fields fields, out ulong value) =>
        fields.Varint(out value) switch
        {
            VarintRead.Valid => null,
            VarintRead.Unterminated => "row-truncated",
            VarintRead.Invalid => "bad-varint",
            _ => throw new ArgumentOutOfRangeException(nameof(fields)),
        };

    private static string? TokenOf(ref Fields fields, string tooLong, out byte[]? token)
    {
        token = null;
        if (!fields.Take(1, out var length))
        {
            return "row-truncated";
        }

        if (length[0] > MaxToken)
        {
            return tooLong;
        }

        if (!fields.Take(length[0], out var bytes))
        {
            return "row-truncated";
        }

        token = bytes.ToArray();
        return null;
    }

    /// <summary>The bytes before the CRC-32C, taken field by field.</summary>
    private ref struct Fields(ReadOnlySpan<byte> bytes, int start)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private long _position = start;

        public readonly long Remaining => _bytes.Length - _position;

        /// <summary>Takes the next field of <paramref name="length"/> bytes, unless it would end past the CRC-32C.</summary>
        public bool Take(long length, out ReadOnlySpan<byte> field)
        {
            if (length > Remaining)
            {
                field = default;
                return false;
            }

            field = _bytes.Slice((int)_position, (int)length);
            _position += length;
            return true;
        }

        /// <summary>
        /// Reads a varint: unsigned LEB128 within 10 bytes, its 10th byte at most 1, in its shortest form; one which reaches
        /// the CRC-32C first, within 10 bytes, is unterminated.
        /// </summary>
        public VarintRead Varint(out ulong value)
        {
            value = 0;
            for (var index = 0; index < MaxVarint; index++)
            {
                if (Remaining == 0)
                {
                    return VarintRead.Unterminated;
                }

                var next = _bytes[(int)_position++];
                if (index == MaxVarint - 1 && next > 1)
                {
                    return VarintRead.Invalid;
                }

                value |= (ulong)(next & 0x7f) << (7 * index);
                if ((next & 0x80) == 0)
                {
                    return index > 0 && next == 0 ? VarintRead.Invalid : VarintRead.Valid;
                }
            }

            return VarintRead.Invalid;
        }
    }

    /// <summary>A key a segment defined: its bytes, version base, token, and count after its last row so far.</summary>
    private sealed class RuleKey(byte[] key, byte[] versionBase, byte[] token, ulong count)
    {
        public byte[] Key { get; } = key;

        public byte[] Base { get; } = versionBase;

        public byte[] Token { get; } = token;

        public ulong Count { get; set; } = count;

        /// <summary>Gets a value indicating whether a filtered reading skips the key, which is not its own.</summary>
        public bool Skipped { get; init; }
    }
}

/// <summary>
/// What the batch rules give for some bytes: the reason code of the first rule they break, or the batch they hold.
/// </summary>
public sealed record BatchReading(string? Reason, RuleBatch? Batch)
{
    /// <summary>Gets the batch's rows, or none for a rejection.</summary>
    public IReadOnlyList<RuleRow> Rows => Batch?.Rows ?? [];

    internal static BatchReading Rejected(string reason) => new(reason, null);

    internal static BatchReading Accepted(RuleBatch batch) => new(null, batch);
}

/// <summary>A batch as the rules give it: its start flag, segment offset, ordinal base, and rows.</summary>
public sealed record RuleBatch(bool IsStart, ulong Offset, ulong OrdinalBase, IReadOnlyList<RuleRow> Rows);

/// <summary>
/// A row as the batch rules give it: its kind byte, whether it defines its key, its ordinal (stored or implied), a
/// defining row's key, version base and count, and token, and its body.
/// </summary>
public sealed record RuleRow(byte Kind, bool IsDefining, ulong Ordinal, byte[]? Key, byte[]? VersionBase, ulong VersionCount, byte[]? Token, byte[] Body);

/// <summary>
/// What the rules give for a segment's batches: the index of the first batch which breaks a rule and its reason code, or
/// the resolved rows.
/// </summary>
public sealed record LedgerSegmentReading(int FailedBatch, string? Reason, IReadOnlyList<ResolvedRuleRow> Rows)
{
    internal static LedgerSegmentReading Rejected(int batch, string reason) => new(batch, reason, []);

    internal static LedgerSegmentReading Accepted(IReadOnlyList<ResolvedRuleRow> rows) => new(-1, null, rows);
}

/// <summary>A resolved row: its slot and index, key, kind, version after it, its key's token, and its body.</summary>
public sealed record ResolvedRuleRow(long Slot, int Index, byte[] Key, byte Kind, byte[] VersionBase, ulong VersionCount, byte[] Token, byte[] Body);

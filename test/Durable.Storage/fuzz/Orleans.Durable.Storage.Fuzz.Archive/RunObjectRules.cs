using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Orleans.Durable.Storage.Fuzz.Archive;

/// <summary>
/// One reading of the run object format's rules, <c>CLAR</c> version 1 (archive §7), written apart from the decoder: the
/// oracle of the <c>archive-run-object</c> target and of the archive tests' run object properties.
/// </summary>
/// <remarks>
/// It reads a whole object as a reader of the whole object does: the trailer from the object's last 8 bytes, then each
/// section's block index and Bloom filter, then each section's blocks and totals. Each part's CRC-32C is checked before
/// its fields, each field first to end within its part and then, at once, against its own rule, and a row's checks
/// against its section follow its last field. It names the first rule the bytes break by its reason code; otherwise it
/// gives the object the bytes hold.
/// </remarks>
public static class RunObjectRules
{
    private const int MinTrailer = 16;
    private const int MaxTrailer = 1 << 20;
    private const ulong Max63 = long.MaxValue;
    private const int MaxDepth = 8 * 65_535;

    /// <summary>Reads <paramref name="content"/> as the format's rules give it.</summary>
    public static RunObjectReading Read(ReadOnlySpan<byte> content)
    {
        try
        {
            return RunObjectReading.Accepted(ReadObject(content));
        }
        catch (RuleBroken broken)
        {
            return RunObjectReading.Rejected(broken.Reason);
        }
    }

    /// <summary>
    /// Returns <paramref name="input"/> with the CRC-32C of every part it can locate recomputed: the trailer's, when the
    /// object's last 8 bytes give a length which fits, and those of each section's block index, Bloom filter, and blocks
    /// which the trailer and block index place within the object; or <see langword="null"/> when it locates no trailer.
    /// </summary>
    public static byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (input.Length < MinTrailer)
        {
            return null;
        }

        var bytes = input.ToArray();
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
        if (length < MinTrailer || length > bytes.Length)
        {
            return null;
        }

        var trailerOffset = bytes.Length - (int)length;
        foreach (var (start, end) in Parts(bytes, trailerOffset))
        {
            SealPart(bytes.AsSpan(start, end - start));
        }

        SealPart(bytes.AsSpan(trailerOffset));
        return bytes;
    }

    private static RunRuleObject ReadObject(ReadOnlySpan<byte> content)
    {
        if (content.Length < MinTrailer)
        {
            throw new RuleBroken("truncated");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(content[^8..]);
        if (length < MinTrailer || length > content.Length || length > MaxTrailer)
        {
            throw new RuleBroken("length-mismatch");
        }

        var trailerOffset = (ulong)(content.Length - (int)length);
        var trailer = content[(int)trailerOffset..];
        if (!trailer[..4].SequenceEqual("CLAR"u8))
        {
            throw new RuleBroken("bad-magic");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != 1)
        {
            throw new RuleBroken("bad-version");
        }

        CheckCrc(trailer);
        var fields = new Fields(trailer[8..^8]);
        var count = fields.UInt32();
        if (count == 0)
        {
            throw new RuleBroken("no-sections");
        }

        var metadataOffset = fields.UInt64();
        if (metadataOffset > trailerOffset)
        {
            throw new RuleBroken("bad-section-layout");
        }

        var entries = new List<RunRuleEntry>();
        ulong blocksEnd = 0;
        for (var index = 0L; index < count; index++)
        {
            var entry = ReadEntry(ref fields, blocksEnd, metadataOffset, trailerOffset, entries.Count == 0 ? null : entries[^1]);
            entries.Add(entry);
            blocksEnd = entry.Offset + entry.Length;
        }

        if (blocksEnd != metadataOffset)
        {
            throw new RuleBroken("sections-not-contiguous");
        }

        var condition = ReadCondition(ref fields, entries);
        if (fields.Remaining > 0)
        {
            throw new RuleBroken("trailing-bytes");
        }

        var metadata = new List<(List<(ulong Length, byte Flag, byte[] Key)> Blocks, uint Hashes, byte[] Bits)>();
        for (var index = 0; index < entries.Count; index++)
        {
            var filterEnd = index + 1 < entries.Count ? entries[index + 1].IndexOffset : trailerOffset;
            metadata.Add(ReadMetadata(content, entries[index], filterEnd));
        }

        var sections = new List<RunRuleSection>();
        for (var index = 0; index < entries.Count; index++)
        {
            sections.Add(ReadSection(content, entries[index], metadata[index].Blocks, metadata[index].Hashes, metadata[index].Bits));
        }

        return new RunRuleObject(condition, metadataOffset, sections);
    }

    private static RunRuleEntry ReadEntry(ref Fields fields, ulong expected, ulong metadataOffset, ulong trailerOffset, RunRuleEntry? previous)
    {
        var offset = fields.UInt64();
        if (offset != expected)
        {
            throw new RuleBroken("sections-not-contiguous");
        }

        var length = fields.UInt64();
        if (length == 0 || length > metadataOffset - offset)
        {
            throw new RuleBroken("bad-section-range");
        }

        var (depth, prefix) = ReadPartition(ref fields);
        var minTag = ReadTag(ref fields);
        var maxTag = ReadTag(ref fields);
        if (RuleTag.Compare(minTag, maxTag) > 0)
        {
            throw new RuleBroken("tags-unordered");
        }

        var rowCount = fields.UInt64();
        if (rowCount == 0)
        {
            throw new RuleBroken("no-rows");
        }

        // The first block index starts the metadata; each later one starts at least 4 bytes past the previous filter, and
        // each part leaves room for the parts after it before the trailer.
        var indexOffset = fields.UInt64();
        var indexOk = previous is null ? indexOffset == metadataOffset : indexOffset >= previous.FilterOffset + 4;
        if (!indexOk || indexOffset > trailerOffset || trailerOffset - indexOffset < 8)
        {
            throw new RuleBroken("bad-section-layout");
        }

        var filterOffset = fields.UInt64();
        if (filterOffset < indexOffset + 4 || filterOffset > trailerOffset || trailerOffset - filterOffset < 4)
        {
            throw new RuleBroken("bad-section-layout");
        }

        return new RunRuleEntry(offset, length, depth, prefix, minTag, maxTag, rowCount, indexOffset, filterOffset);
    }

    private static RunRuleCondition ReadCondition(ref Fields fields, List<RunRuleEntry> entries)
    {
        var kind = fields.Byte();
        if (kind > 1)
        {
            throw new RuleBroken("bad-condition");
        }

        if (kind == 0)
        {
            var ledger = fields.UInt64();
            if (ledger == 0)
            {
                throw new RuleBroken("bad-condition");
            }

            var from = fields.UInt64();
            if (from > Max63)
            {
                throw new RuleBroken("bad-condition");
            }

            if (entries.Any(entry => entry.MinTag.Ledger != ledger || entry.MaxTag.Ledger != ledger || entry.MinTag.Slot < from))
            {
                throw new RuleBroken("condition-mismatch");
            }

            return new RunRuleCondition(ledger, from, null);
        }

        var count = fields.UInt32();
        if (count == 0)
        {
            throw new RuleBroken("bad-condition");
        }

        var inputs = new List<(string Object, ulong Offset)>();
        for (var index = 0L; index < count; index++)
        {
            var nameLength = fields.Byte();
            var name = Encoding.Latin1.GetString(fields.Bytes(nameLength));
            if (!IsObjectName(name))
            {
                throw new RuleBroken("bad-object-name");
            }

            var offset = fields.UInt64();
            if (offset > Max63)
            {
                throw new RuleBroken("bad-condition");
            }

            if (inputs.Count > 0)
            {
                var order = string.CompareOrdinal(inputs[^1].Object, name);
                if (order > 0 || order == 0 && inputs[^1].Offset >= offset)
                {
                    throw new RuleBroken("inputs-unordered");
                }
            }

            inputs.Add((name, offset));
        }

        return new RunRuleCondition(0, 0, inputs);
    }

    private static (List<(ulong Length, byte Flag, byte[] Key)> Blocks, uint Hashes, byte[] Bits) ReadMetadata(ReadOnlySpan<byte> content, RunRuleEntry entry, ulong filterEnd)
    {
        var index = content[(int)entry.IndexOffset..(int)entry.FilterOffset];
        CheckCrc(index);
        var fields = new Fields(index[..^4]);
        var blockCount = fields.UInt32();
        if (blockCount == 0)
        {
            throw new RuleBroken("no-blocks");
        }

        ulong filled = 0;
        var blocks = new List<(ulong Length, byte Flag, byte[] Key)>();
        for (var block = 0L; block < blockCount; block++)
        {
            var length = fields.UInt32();
            if (length < 5 || filled + length > entry.Length)
            {
                throw new RuleBroken("blocks-mismatch");
            }

            var flag = fields.Byte();
            if (flag > 1 || block == 0 && flag == 1)
            {
                throw new RuleBroken("bad-continuation");
            }

            var key = ReadKey(ref fields);
            if (blocks.Count > 0 && key.AsSpan().SequenceCompareTo(blocks[^1].Key) < 0)
            {
                throw new RuleBroken("keys-unordered");
            }

            if (!InPartition(key, entry.Depth, entry.Prefix))
            {
                throw new RuleBroken("first-key-outside-partition");
            }

            blocks.Add((length, flag, key));
            filled += length;
        }

        if (filled != entry.Length)
        {
            throw new RuleBroken("blocks-mismatch");
        }

        if (fields.Remaining > 0)
        {
            throw new RuleBroken("trailing-bytes");
        }

        var filter = content[(int)entry.FilterOffset..(int)filterEnd];
        CheckCrc(filter);
        fields = new Fields(filter[..^4]);
        var hashCount = fields.Byte();
        if (hashCount is < 1 or > 64)
        {
            throw new RuleBroken("bad-filter");
        }

        var bits = fields.Bytes(fields.Remaining).ToArray();
        return bits.Length == 0 ? throw new RuleBroken("bad-filter") : (blocks, hashCount, bits);
    }

    private static RunRuleSection ReadSection(ReadOnlySpan<byte> content, RunRuleEntry entry, List<(ulong Length, byte Flag, byte[] Key)> blocks, uint hashCount, byte[] bits)
    {
        var rows = new List<RunRuleRow>();
        var offset = entry.Offset;
        foreach (var (length, flag, firstKey) in blocks)
        {
            var bytes = content.Slice((int)offset, (int)length);
            CheckCrc(bytes);
            var fields = new Fields(bytes[..^4]);
            var first = true;
            while (fields.Remaining > 0)
            {
                var read = ReadRow(ref fields);
                var previous = rows.Count > 0 ? rows[^1] : null;
                if (first && (!read.Key.AsSpan().SequenceEqual(firstKey) || previous is not null && flag == 1 != read.Key.AsSpan().SequenceEqual(previous.Key)))
                {
                    throw new RuleBroken("index-mismatch");
                }

                if (previous is not null && CompareRows(previous, read) >= 0)
                {
                    throw new RuleBroken("rows-unordered");
                }

                if (!InPartition(read.Key, entry.Depth, entry.Prefix))
                {
                    throw new RuleBroken("key-outside-partition");
                }

                if (RuleTag.Compare(read.Tag, entry.MinTag) < 0 || RuleTag.Compare(read.Tag, entry.MaxTag) > 0)
                {
                    throw new RuleBroken("tag-outside-span");
                }

                if ((previous is null || !previous.Key.AsSpan().SequenceEqual(read.Key)) && !Admits(bits, (int)hashCount, read.Key))
                {
                    throw new RuleBroken("filter-miss");
                }

                rows.Add(read);
                first = false;
            }

            offset += length;
        }

        if ((ulong)rows.Count != entry.RowCount)
        {
            throw new RuleBroken("row-count-mismatch");
        }

        var tags = rows.Select(static row => row.Tag).ToList();
        tags.Sort(RuleTag.Compare);
        if (tags[0] != entry.MinTag || tags[^1] != entry.MaxTag)
        {
            throw new RuleBroken("tag-span-mismatch");
        }

        return new RunRuleSection(entry, hashCount, bits, [.. blocks.Select(static block => (block.Length, block.Flag == 1, block.Key))], rows);
    }

    private static RunRuleRow ReadRow(ref Fields fields)
    {
        var key = ReadKey(ref fields);
        var tag = ReadTag(ref fields);
        var kind = fields.Byte();
        if (kind > 2)
        {
            throw new RuleBroken("unknown-kind");
        }

        var payloadLength = fields.UInt32();
        return new RunRuleRow(key, tag, kind, fields.Bytes(payloadLength).ToArray());
    }

    private static byte[] ReadKey(ref Fields fields)
    {
        var length = fields.UInt16();
        return length == 0 ? throw new RuleBroken("empty-key") : fields.Bytes(length).ToArray();
    }

    private static (int Depth, byte[] Prefix) ReadPartition(ref Fields fields)
    {
        var depth = fields.UInt32();
        if (depth > MaxDepth)
        {
            throw new RuleBroken("bad-partition");
        }

        var prefix = fields.Bytes((depth + 7) / 8).ToArray();

        // Each bit at or past the depth, the low 8 - depth % 8 bits of the last byte, is 0.
        if (depth % 8 != 0 && (prefix[^1] & ((1 << (int)(8 - depth % 8)) - 1)) != 0)
        {
            throw new RuleBroken("bad-partition");
        }

        return ((int)depth, prefix);
    }

    private static RuleTag ReadTag(ref Fields fields)
    {
        var ledger = fields.UInt64();
        if (ledger == 0)
        {
            throw new RuleBroken("bad-tag");
        }

        var slot = fields.UInt64();
        if (slot > Max63 - 1)
        {
            throw new RuleBroken("bad-tag");
        }

        var index = fields.UInt32();
        return index > int.MaxValue ? throw new RuleBroken("bad-tag") : new RuleTag(ledger, slot, index);
    }

    private static void CheckCrc(ReadOnlySpan<byte> part)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(part[^4..]) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, part[..^4]))
        {
            throw new RuleBroken("crc-mismatch");
        }
    }

    private static void SealPart(Span<byte> part) =>
        BinaryPrimitives.WriteUInt32LittleEndian(part[^4..], Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, part[..^4]));

    /// <summary>
    /// Returns the parts of each section the trailer places within the object, each at least its CRC-32C long: its
    /// block index and Bloom filter, and its blocks as far as its block index lists them within the section.
    /// </summary>
    private static List<(int Start, int End)> Parts(byte[] bytes, int trailerOffset)
    {
        var parts = new List<(int, int)>();
        var fields = new Fields(bytes.AsSpan(trailerOffset)[8..^8]);
        if (!fields.TryUInt32(out var count) || !fields.TryUInt64(out var metadataOffset) || metadataOffset > (ulong)trailerOffset)
        {
            return parts;
        }

        var placed = new List<(ulong Offset, ulong Length, ulong Index, ulong Filter)>();
        for (var index = 0L; index < count; index++)
        {
            if (!fields.TryUInt64(out var offset) || !fields.TryUInt64(out var length) || !fields.TryUInt32(out var depth)
                || depth > MaxDepth || !fields.TrySkip((depth + 7) / 8 + 40 + 8) || !fields.TryUInt64(out var indexOffset)
                || !fields.TryUInt64(out var filterOffset))
            {
                break;
            }

            placed.Add((offset, length, indexOffset, filterOffset));
        }

        for (var index = 0; index < placed.Count; index++)
        {
            var (offset, length, indexOffset, filterOffset) = placed[index];
            var filterEnd = index + 1 < placed.Count ? placed[index + 1].Index : (ulong)trailerOffset;
            if (!(indexOffset >= metadataOffset && filterOffset >= indexOffset && filterOffset - indexOffset >= 4
                && filterEnd >= filterOffset && filterEnd - filterOffset >= 4 && filterEnd <= (ulong)trailerOffset))
            {
                continue;
            }

            parts.Add(((int)indexOffset, (int)filterOffset));
            parts.Add(((int)filterOffset, (int)filterEnd));
            if (offset > metadataOffset || length > metadataOffset - offset)
            {
                continue;
            }

            var blockIndex = new Fields(bytes.AsSpan((int)indexOffset, (int)(filterOffset - indexOffset - 4)));
            if (!blockIndex.TryUInt32(out var blockCount))
            {
                continue;
            }

            var start = offset;
            for (var block = 0L; block < blockCount; block++)
            {
                if (!blockIndex.TryUInt32(out var blockLength) || !blockIndex.TrySkip(1) || !blockIndex.TryUInt16(out var keyLength)
                    || !blockIndex.TrySkip(keyLength) || blockLength < 4 || blockLength > offset + length - start)
                {
                    break;
                }

                parts.Add(((int)start, (int)(start + blockLength)));
                start += blockLength;
            }
        }

        return parts;
    }

    private static bool Admits(byte[] bits, int hashCount, byte[] key)
    {
        var hash = XxHash3.HashToUInt64(key);
        ulong first = hash & 0xffff_ffff, step = hash >> 32, bitCount = (ulong)bits.Length * 8;
        for (ulong i = 0; i < (ulong)hashCount; i++)
        {
            var bit = (first + i * step) % bitCount;
            if ((bits[bit / 8] & (1 << (int)(bit % 8))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool InPartition(byte[] key, int depth, byte[] prefix)
    {
        for (var bit = 0; bit < depth; bit++)
        {
            var keyBit = bit / 8 < key.Length && (key[bit / 8] & (0x80 >> (bit % 8))) != 0;
            var prefixBit = (prefix[bit / 8] & (0x80 >> (bit % 8))) != 0;
            if (keyBit != prefixBit)
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareRows(RunRuleRow left, RunRuleRow right)
    {
        var order = left.Key.AsSpan().SequenceCompareTo(right.Key);
        return order != 0 ? order : RuleTag.Compare(left.Tag, right.Tag);
    }

    private static bool IsObjectName(string name)
    {
        if (name.Length is 0 or > 200 || name[0] == '/' || name[^1] == '/' || name.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return name.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '/');
    }

    /// <summary>A rule the bytes break, by its reason code.</summary>
    private sealed class RuleBroken(string reason) : Exception(reason)
    {
        public string Reason { get; } = reason;
    }

    /// <summary>
    /// A part's fields, taken in order, each only when it ends within the part; one which does not breaks
    /// <c>body-truncated</c>.
    /// </summary>
    private ref struct Fields(ReadOnlySpan<byte> body)
    {
        private readonly ReadOnlySpan<byte> _body = body;
        private int _position;

        public readonly int Remaining => _body.Length - _position;

        public ReadOnlySpan<byte> Bytes(long length)
        {
            if (length > Remaining)
            {
                throw new RuleBroken("body-truncated");
            }

            var field = _body.Slice(_position, (int)length);
            _position += (int)length;
            return field;
        }

        public bool TrySkip(long length)
        {
            if (length > Remaining)
            {
                return false;
            }

            _position += (int)length;
            return true;
        }

        public byte Byte() => Bytes(1)[0];

        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));

        public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));

        public bool TryUInt16(out ushort value)
        {
            var fits = Remaining >= 2;
            value = fits ? UInt16() : (ushort)0;
            return fits;
        }

        public bool TryUInt32(out uint value)
        {
            var fits = Remaining >= 4;
            value = fits ? UInt32() : 0;
            return fits;
        }

        public bool TryUInt64(out ulong value)
        {
            var fits = Remaining >= 8;
            value = fits ? UInt64() : 0;
            return fits;
        }
    }
}

/// <summary>
/// What the format's rules give for some bytes: the reason code of the first rule they break, or the object they hold.
/// </summary>
public sealed record RunObjectReading(string? Reason, RunRuleObject? Object)
{
    internal static RunObjectReading Rejected(string reason) => new(reason, null);

    internal static RunObjectReading Accepted(RunRuleObject value) => new(null, value);
}

/// <summary>A run object as the format's rules give it.</summary>
public sealed record RunRuleObject(RunRuleCondition Condition, ulong MetadataOffset, IReadOnlyList<RunRuleSection> Sections);

/// <summary>
/// A reference condition as the format's rules give it: a drain's ledger and watermark, when <see cref="Inputs"/> is
/// <see langword="null"/>, or a compaction's inputs.
/// </summary>
public sealed record RunRuleCondition(ulong Ledger, ulong From, IReadOnlyList<(string Object, ulong Offset)>? Inputs);

/// <summary>A section's trailer entry as the format's rules give it.</summary>
public sealed record RunRuleEntry(
    ulong Offset,
    ulong Length,
    int Depth,
    byte[] Prefix,
    RuleTag MinTag,
    RuleTag MaxTag,
    ulong RowCount,
    ulong IndexOffset,
    ulong FilterOffset);

/// <summary>A section as the format's rules give it: its entry, filter, blocks, and rows.</summary>
public sealed record RunRuleSection(
    RunRuleEntry Entry,
    uint HashCount,
    byte[] FilterBits,
    IReadOnlyList<(ulong Length, bool Continues, byte[] FirstKey)> Blocks,
    IReadOnlyList<RunRuleRow> Rows);

/// <summary>A row as the format's rules give it.</summary>
public sealed record RunRuleRow(byte[] Key, RuleTag Tag, byte Kind, byte[] Payload);

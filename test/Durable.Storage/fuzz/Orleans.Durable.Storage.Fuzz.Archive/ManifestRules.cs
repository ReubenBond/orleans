using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Orleans.Durable.Storage.Fuzz.Archive;

/// <summary>
/// One reading of the manifest format's rules, <c>CLAM</c> version 1 (archive §7), written apart from the decoder: the
/// oracle of the <c>archive-manifest</c> target and of the archive tests' manifest properties.
/// </summary>
/// <remarks>
/// It checks the rules in the table's order, and the body's fields in layout order, each integer first to end at or
/// before the body's end and then, at once, against its own rule, and names the first rule the bytes break by its
/// reason code; otherwise it gives the value the bytes hold.
/// </remarks>
public static class ManifestRules
{
    private const int Header = 16;
    private const ulong Max63 = long.MaxValue;
    private const int MaxDepth = 8 * 65_535;
    private const int MaxNameLength = 200;

    /// <summary>Reads <paramref name="manifest"/> as the format's rules give it.</summary>
    public static ManifestReading Read(ReadOnlySpan<byte> manifest)
    {
        if (manifest.Length < Header)
        {
            return ManifestReading.Rejected("truncated");
        }

        if (!manifest[..4].SequenceEqual("CLAM"u8))
        {
            return ManifestReading.Rejected("bad-magic");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(manifest[4..]) != 1)
        {
            return ManifestReading.Rejected("bad-version");
        }

        var body = manifest[Header..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(manifest[8..]) != (uint)body.Length)
        {
            return ManifestReading.Rejected("length-mismatch");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(manifest[12..]) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body))
        {
            return ManifestReading.Rejected("crc-mismatch");
        }

        var fields = new Fields(body);
        return ReadBody(ref fields);
    }

    /// <summary>
    /// Returns <paramref name="input"/> with its header's body length and CRC-32C recomputed, or <see langword="null"/>
    /// when it is too short to hold a header.
    /// </summary>
    public static byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (input.Length < Header)
        {
            return null;
        }

        var sealedInput = input.ToArray();
        var body = sealedInput.AsSpan(Header);
        BinaryPrimitives.WriteUInt32LittleEndian(sealedInput.AsSpan(8), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(sealedInput.AsSpan(12), Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body));
        return sealedInput;
    }

    private static ManifestReading ReadBody(ref Fields fields)
    {
        if (!fields.UInt64(out var epoch))
        {
            return ManifestReading.Rejected("body-truncated");
        }

        if (epoch == 0)
        {
            return ManifestReading.Rejected("zero-epoch");
        }

        if (!fields.UInt64(out var drainedBelow))
        {
            return ManifestReading.Rejected("body-truncated");
        }

        if (drainedBelow == 0)
        {
            return ManifestReading.Rejected("zero-drained-below");
        }

        if (!fields.UInt32(out var watermarkCount))
        {
            return ManifestReading.Rejected("body-truncated");
        }

        var watermarks = new List<RuleWatermark>();
        for (var entry = 0L; entry < watermarkCount; entry++)
        {
            if (!fields.UInt64(out var ledger))
            {
                return ManifestReading.Rejected("body-truncated");
            }

            if (ledger < drainedBelow)
            {
                return ManifestReading.Rejected("watermark-below-drained");
            }

            if (watermarks.Count > 0 && ledger <= watermarks[^1].Ledger)
            {
                return ManifestReading.Rejected("watermarks-unordered");
            }

            if (!fields.UInt64(out var watermark))
            {
                return ManifestReading.Rejected("body-truncated");
            }

            if (watermark > Max63)
            {
                return ManifestReading.Rejected("slot-out-of-range");
            }

            if (!fields.Bytes(1, out var flag))
            {
                return ManifestReading.Rejected("body-truncated");
            }

            if (flag[0] > 1)
            {
                return ManifestReading.Rejected("bad-complete-flag");
            }

            if (watermark == 0 && flag[0] == 0)
            {
                return ManifestReading.Rejected("empty-watermark");
            }

            if (flag[0] == 1 && ledger == drainedBelow)
            {
                return ManifestReading.Rejected("complete-at-drained-below");
            }

            watermarks.Add(new RuleWatermark(ledger, watermark, flag[0] == 1));
        }

        if (!fields.UInt32(out var sectionCount))
        {
            return ManifestReading.Rejected("body-truncated");
        }

        var sections = new List<RuleSection>();
        for (var index = 0L; index < sectionCount; index++)
        {
            var (reason, section) = ReadSection(ref fields);
            if (reason is not null)
            {
                return ManifestReading.Rejected(reason);
            }

            if (sections.Count > 0 && ComparePosition(sections[^1], section!) >= 0)
            {
                return ManifestReading.Rejected("sections-unordered");
            }

            sections.Add(section!);
        }

        return fields.Remaining > 0
            ? ManifestReading.Rejected("trailing-bytes")
            : ManifestReading.Accepted(new RuleManifest(epoch, drainedBelow, watermarks, sections));
    }

    private static (string? Reason, RuleSection? Section) ReadSection(ref Fields fields)
    {
        if (!fields.Name(out var objectName))
        {
            return ("body-truncated", null);
        }

        if (!IsObjectName(objectName))
        {
            return ("bad-object-name", null);
        }

        if (!fields.UInt64(out var offset))
        {
            return ("body-truncated", null);
        }

        if (offset > Max63)
        {
            return ("bad-section-range", null);
        }

        if (!fields.UInt64(out var length))
        {
            return ("body-truncated", null);
        }

        if (!IsByteRange(offset, length))
        {
            return ("bad-section-range", null);
        }

        if (!fields.UInt32(out var depth))
        {
            return ("body-truncated", null);
        }

        if (depth > MaxDepth)
        {
            return ("bad-partition", null);
        }

        if (!fields.Bytes((depth + 7) / 8, out var prefix))
        {
            return ("body-truncated", null);
        }

        // Each bit at or past the depth, the low 8 - depth % 8 bits of the last byte, is 0.
        if (depth % 8 != 0 && (prefix[^1] & ((1 << (int)(8 - depth % 8)) - 1)) != 0)
        {
            return ("bad-partition", null);
        }

        var (minReason, minTag) = ReadTag(ref fields);
        if (minReason is not null)
        {
            return (minReason, null);
        }

        var (maxReason, maxTag) = ReadTag(ref fields);
        if (maxReason is not null)
        {
            return (maxReason, null);
        }

        if (RuleTag.Compare(minTag, maxTag) > 0)
        {
            return ("tags-unordered", null);
        }

        if (!fields.UInt64(out var tailOffset))
        {
            return ("body-truncated", null);
        }

        if (tailOffset > Max63)
        {
            return ("bad-tail-range", null);
        }

        if (!fields.UInt64(out var tailLength))
        {
            return ("body-truncated", null);
        }

        if (!IsByteRange(tailOffset, tailLength))
        {
            return ("bad-tail-range", null);
        }

        return (null, new RuleSection(objectName, offset, length, (int)depth, prefix.ToArray(), minTag, maxTag, tailOffset, tailLength));
    }

    private static bool IsObjectName(string name)
    {
        if (name.Length is 0 or > MaxNameLength || name[0] == '/' || name[^1] == '/' || name.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return name.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '/');
    }

    private static bool IsByteRange(ulong offset, ulong length) => offset <= Max63 && length >= 1 && length <= Max63 - offset;

    /// <summary>
    /// Reads a tag's ledger, slot, and index, each checked once read: a ledger of at least 1, a slot at most
    /// <c>2^63 - 2</c>, and an index at most <c>2^31 - 1</c>.
    /// </summary>
    private static (string? Reason, RuleTag Tag) ReadTag(ref Fields fields)
    {
        if (!fields.UInt64(out var ledger))
        {
            return ("body-truncated", default);
        }

        if (ledger == 0)
        {
            return ("bad-tag", default);
        }

        if (!fields.UInt64(out var slot))
        {
            return ("body-truncated", default);
        }

        if (slot > Max63 - 1)
        {
            return ("bad-tag", default);
        }

        if (!fields.UInt32(out var index))
        {
            return ("body-truncated", default);
        }

        return index > int.MaxValue ? ("bad-tag", default) : (null, new RuleTag(ledger, slot, index));
    }

    private static int ComparePosition(RuleSection left, RuleSection right)
    {
        var order = string.CompareOrdinal(left.Object, right.Object);
        return order != 0 ? order : left.Offset.CompareTo(right.Offset);
    }

    /// <summary>
    /// The body's fields, taken in order, each only when it ends at or before the body's end.
    /// </summary>
    private ref struct Fields(ReadOnlySpan<byte> body)
    {
        private readonly ReadOnlySpan<byte> _body = body;
        private long _position;

        public readonly long Remaining => _body.Length - _position;

        public bool Bytes(long length, out ReadOnlySpan<byte> field)
        {
            if (length > Remaining)
            {
                field = default;
                return false;
            }

            field = _body.Slice((int)_position, (int)length);
            _position += length;
            return true;
        }

        public bool UInt32(out uint value)
        {
            value = Bytes(4, out var field) ? BinaryPrimitives.ReadUInt32LittleEndian(field) : 0;
            return !field.IsEmpty;
        }

        public bool UInt64(out ulong value)
        {
            value = Bytes(8, out var field) ? BinaryPrimitives.ReadUInt64LittleEndian(field) : 0;
            return !field.IsEmpty;
        }

        public bool Name(out string name)
        {
            name = string.Empty;
            if (!Bytes(1, out var length) || !Bytes(length[0], out var bytes))
            {
                return false;
            }

            name = Encoding.Latin1.GetString(bytes);
            return true;
        }
    }
}

/// <summary>
/// What the format's rules give for some bytes: the reason code of the first rule they break, or the manifest they hold.
/// </summary>
public sealed record ManifestReading(string? Reason, RuleManifest? Manifest)
{
    internal static ManifestReading Rejected(string reason) => new(reason, null);

    internal static ManifestReading Accepted(RuleManifest manifest) => new(null, manifest);
}

/// <summary>A manifest as the format's rules give it.</summary>
public sealed record RuleManifest(ulong Epoch, ulong DrainedBelow, IReadOnlyList<RuleWatermark> Watermarks, IReadOnlyList<RuleSection> Sections);

/// <summary>A watermark entry as the format's rules give it.</summary>
public readonly record struct RuleWatermark(ulong Ledger, ulong Watermark, bool Complete);

/// <summary>A section as the format's rules give it.</summary>
public sealed record RuleSection(
    string Object,
    ulong Offset,
    ulong Length,
    int Depth,
    byte[] Prefix,
    RuleTag MinTag,
    RuleTag MaxTag,
    ulong TailOffset,
    ulong TailLength);

/// <summary>A tag as the format's rules give it, ordered by ledger, slot, and index.</summary>
public readonly record struct RuleTag(ulong Ledger, ulong Slot, uint Index)
{
    /// <summary>
    /// Orders tags by ledger, then slot, then index.
    /// </summary>
    public static int Compare(RuleTag left, RuleTag right)
    {
        var order = left.Ledger.CompareTo(right.Ledger);
        if (order != 0)
        {
            return order;
        }

        order = left.Slot.CompareTo(right.Slot);
        return order != 0 ? order : left.Index.CompareTo(right.Index);
    }
}

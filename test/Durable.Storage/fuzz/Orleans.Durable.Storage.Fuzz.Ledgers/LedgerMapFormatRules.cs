using System.Buffers.Binary;
using System.IO.Hashing;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// One reading of the ledger map format's rules, <c>CLLM</c> version 1 (ledgers.md §7), written apart from the decoder:
/// the oracle of the <c>ledgers-map</c> target and of the Ledgers tests' map properties.
/// </summary>
/// <remarks>
/// It checks the value's rules in the table's order, and each ledger's fields in layout order, each first to end at or
/// before the CRC-32C and then against its own rule, and names the first rule the bytes break by its reason code;
/// otherwise it gives the value the bytes hold.
/// </remarks>
public static class LedgerMapFormatRules
{
    private const int Header = 36;
    private const int Trailer = 4;
    private const int MaxDepth = 8 * 65_535;

    /// <summary>Reads <paramref name="value"/> as the format's rules give it.</summary>
    public static MapReading Read(ReadOnlySpan<byte> value)
    {
        if (value.Length < Header + Trailer)
        {
            return MapReading.Rejected("truncated");
        }

        if (!value[..4].SequenceEqual("CLLM"u8))
        {
            return MapReading.Rejected("bad-magic");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(value[4..]) != 1)
        {
            return MapReading.Rejected("bad-version");
        }

        var crcAt = value.Length - Trailer;
        if (BinaryPrimitives.ReadUInt32LittleEndian(value[crcAt..]) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, value[..crcAt]))
        {
            return MapReading.Rejected("crc-mismatch");
        }

        var epoch = BinaryPrimitives.ReadUInt64LittleEndian(value[8..]);
        if (epoch == 0)
        {
            return MapReading.Rejected("zero-epoch");
        }

        var next = BinaryPrimitives.ReadUInt64LittleEndian(value[16..]);
        if (next == 0)
        {
            return MapReading.Rejected("zero-next-id");
        }

        var floor = BinaryPrimitives.ReadUInt64LittleEndian(value[24..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(value[32..]);
        var fields = new Fields(value[..crcAt], Header);
        var ledgers = new List<RuleLedger>();
        var previous = 0UL;
        for (var ledger = 0L; ledger < count; ledger++)
        {
            if (!fields.Take(8, out var idBytes))
            {
                return MapReading.Rejected("entry-truncated");
            }

            var id = BinaryPrimitives.ReadUInt64LittleEndian(idBytes);
            if (id == 0)
            {
                return MapReading.Rejected("zero-id");
            }

            if (id <= previous)
            {
                return MapReading.Rejected("ids-not-ascending");
            }

            if (id >= next)
            {
                return MapReading.Rejected("id-at-or-above-next");
            }

            if (!fields.Take(4, out var depthBytes))
            {
                return MapReading.Rejected("range-truncated");
            }

            var depth = BinaryPrimitives.ReadUInt32LittleEndian(depthBytes);
            if (depth > MaxDepth)
            {
                return MapReading.Rejected("range-depth");
            }

            if (!fields.Take((depth + 7) / 8, out var prefix))
            {
                return MapReading.Rejected("range-truncated");
            }

            for (var bit = depth; bit < 8 * (long)prefix.Length; bit++)
            {
                if ((prefix[(int)(bit / 8)] & (0x80 >> (int)(bit % 8))) != 0)
                {
                    return MapReading.Rejected("range-prefix-bits");
                }
            }

            if (!fields.Take(1, out var countByte))
            {
                return MapReading.Rejected("entry-truncated");
            }

            // A count of 0 is a ledger in the object store, with no replicas; any other even count is not a quorum's.
            if (countByte[0] != 0 && countByte[0] % 2 == 0)
            {
                return MapReading.Rejected("even-replica-count");
            }

            var replicas = new List<Guid>();
            for (var replica = 0; replica < countByte[0]; replica++)
            {
                if (!fields.Take(16, out var replicaBytes))
                {
                    return MapReading.Rejected("entry-truncated");
                }

                var node = new Guid(replicaBytes, bigEndian: true);
                if (node == Guid.Empty)
                {
                    return MapReading.Rejected("empty-node");
                }

                if (replicas.Contains(node))
                {
                    return MapReading.Rejected("duplicate-replica");
                }

                replicas.Add(node);
            }

            if (!fields.Take(4, out var kBytes))
            {
                return MapReading.Rejected("entry-truncated");
            }

            var k = BinaryPrimitives.ReadUInt32LittleEndian(kBytes);
            if (k == 0)
            {
                return MapReading.Rejected("zero-k");
            }

            if (k > int.MaxValue)
            {
                return MapReading.Rejected("k-above-int");
            }

            if (!fields.Take(16, out var primaryBytes))
            {
                return MapReading.Rejected("entry-truncated");
            }

            var primary = new Guid(primaryBytes, bigEndian: true);
            if (primary == Guid.Empty)
            {
                return MapReading.Rejected("empty-node");
            }

            if (!fields.Take(16, out var sequencerBytes))
            {
                return MapReading.Rejected("entry-truncated");
            }

            var sequencer = new Guid(sequencerBytes, bigEndian: true);
            if (sequencer == Guid.Empty)
            {
                return MapReading.Rejected("empty-sequencer");
            }

            if (!fields.Take(1, out var status))
            {
                return MapReading.Rejected("entry-truncated");
            }

            ulong? tail = null;
            if (status[0] == 1)
            {
                if (!fields.Take(8, out var tailBytes))
                {
                    return MapReading.Rejected("entry-truncated");
                }

                tail = BinaryPrimitives.ReadUInt64LittleEndian(tailBytes);
                if (tail >= 1UL << 63)
                {
                    return MapReading.Rejected("tail-out-of-range");
                }
            }
            else if (status[0] != 0)
            {
                return MapReading.Rejected("bad-status");
            }

            ledgers.Add(new(id, (int)depth, prefix.ToArray(), replicas, (int)k, primary, sequencer, tail));
            previous = id;
        }

        return fields.Remaining == 0 ? MapReading.Accepted(new(epoch, next, floor, ledgers)) : MapReading.Rejected("trailing-bytes");
    }

    /// <summary>
    /// Returns <paramref name="value"/> with its last 4 bytes replaced by the CRC-32C of the bytes before them, or
    /// <see langword="null"/> when it is too short to hold its header and CRC-32C.
    /// </summary>
    public static byte[]? Seal(ReadOnlySpan<byte> value)
    {
        if (value.Length < Header + Trailer)
        {
            return null;
        }

        var bytes = value.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - Trailer), Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes.AsSpan(0, bytes.Length - Trailer)));
        return bytes;
    }

    /// <summary>The bytes before the CRC-32C, taken field by field.</summary>
    private ref struct Fields(ReadOnlySpan<byte> bytes, int start)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private long _position = start;

        public readonly long Remaining => _bytes.Length - _position;

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
    }
}

/// <summary>
/// What the format's rules give for some bytes: the reason code of the first rule they break, or the value they hold.
/// </summary>
public sealed record MapReading(string? Reason, RuleMap? Map)
{
    internal static MapReading Rejected(string reason) => new(reason, null);

    internal static MapReading Accepted(RuleMap map) => new(null, map);
}

/// <summary>A map value as the format's rules give it.</summary>
public sealed record RuleMap(ulong Epoch, ulong NextLedgerId, ulong RetirementFloor, IReadOnlyList<RuleLedger> Ledgers);

/// <summary>A ledger's entry as the format's rules give it: <paramref name="Tail"/> is null while it is open.</summary>
public sealed record RuleLedger(
    ulong Id,
    int Depth,
    byte[] Prefix,
    IReadOnlyList<Guid> Replicas,
    int K,
    Guid Primary,
    Guid Sequencer,
    ulong? Tail);

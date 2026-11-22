using System.Buffers.Binary;
using System.IO.Hashing;
using System.Net;
using System.Text;
using System.Text.Unicode;

namespace Orleans.Durable.Storage.Fuzz.Orleans;

/// <summary>A node's entry as the format's rules give it.</summary>
public sealed record RuleEntry(Guid Node, string SiloEndpoint, int Generation, int? Ordinal, ulong RetiredRevision, string? FailureDomain, bool Diskless);

/// <summary>A node registry value as the format's rules give it; an archive store of 0 records none.</summary>
public sealed record RuleRegistry(ulong Revision, int ArchiveStore, UInt128? ArchiveStoreId, string? ArchiveStorePrefix, UInt128? ObjectLedgerStoreId, IReadOnlyList<RuleEntry> Nodes, ulong DrainAllRevision = 0);

/// <summary>What the format's rules give for some bytes: the reason of the first rule they break, or the value they hold.</summary>
public sealed record RegistryReading(string? Reason, RuleRegistry? Registry)
{
    public static RegistryReading Rejected(string reason) => new(reason, null);
}

/// <summary>
/// One reading of the node registry format's rules, <c>CLNR</c> version 1 (orleans.md §7.3), written apart from the
/// decoder: the oracle of the <c>orleans-registry</c> target.
/// </summary>
/// <remarks>
/// Where the decoder parses the body leniently and compares it with its re-encoding, this reading accepts the canonical
/// encoding directly: each message's fields in strictly ascending number, but the repeated <c>nodes</c>; no field at its
/// default but <c>ordinal</c>; minimal varints; <c>int32</c> values sign-extended; strings in UTF-8; and no unknown field.
/// It then checks the values in the order of §7.3's table of reasons.
/// </remarks>
public static class NodeRegistryFormatRules
{
    private const int Header = 16;

    /// <summary>Reads <paramref name="value"/> as the format's rules give it.</summary>
    public static RegistryReading Read(ReadOnlySpan<byte> value)
    {
        if (value.Length < Header)
        {
            return RegistryReading.Rejected("truncated");
        }

        if (!value[..4].SequenceEqual("CLNR"u8))
        {
            return RegistryReading.Rejected("bad-magic");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(value[4..]) != 1)
        {
            return RegistryReading.Rejected("bad-version");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(value[6..]) != 0)
        {
            return RegistryReading.Rejected("bad-flags");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(value[8..]);
        if (length > value.Length - Header)
        {
            return RegistryReading.Rejected("truncated");
        }

        var body = value.Slice(Header, (int)length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(value[12..]) != Checksum(value[..12], body))
        {
            return RegistryReading.Rejected("crc-mismatch");
        }

        if (value.Length != Header + body.Length)
        {
            return RegistryReading.Rejected("trailing-bytes");
        }

        var reader = new CanonicalReader(body);
        if (!reader.ReadRegistry(out var revision, out var store, out var storeId, out var storePrefix, out var objectStoreId, out var drainAllRevision, out var entries))
        {
            return RegistryReading.Rejected("non-canonical");
        }

        if (revision == 0)
        {
            return RegistryReading.Rejected("zero-revision");
        }

        if (store is not (0 or 1 or 2 or 3))
        {
            return RegistryReading.Rejected("unknown-archive-store");
        }

        UInt128? archiveStoreId = null;
        string? archiveStorePrefix = null;
        if (store != 0)
        {
            var id = storeId.Length == 16 ? BinaryPrimitives.ReadUInt128BigEndian(storeId) : UInt128.Zero;
            if (id == UInt128.Zero)
            {
                return RegistryReading.Rejected("bad-archive-store-id");
            }

            archiveStoreId = id;
            if (storePrefix.Length is 0 or > 16 || storePrefix.AsSpan().ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_-"u8))
            {
                return RegistryReading.Rejected("bad-archive-store-prefix");
            }

            archiveStorePrefix = Encoding.ASCII.GetString(storePrefix);
        }
        else if (storeId.Length != 0)
        {
            return RegistryReading.Rejected("bad-archive-store-id");
        }
        else if (storePrefix.Length != 0)
        {
            return RegistryReading.Rejected("bad-archive-store-prefix");
        }

        UInt128? objectLedgerStoreId = null;
        if (objectStoreId.Length != 0)
        {
            var id = objectStoreId.Length == 16 ? BinaryPrimitives.ReadUInt128BigEndian(objectStoreId) : UInt128.Zero;
            if (id == UInt128.Zero)
            {
                return RegistryReading.Rejected("bad-object-ledger-store-id");
            }

            objectLedgerStoreId = id;
        }

        if (drainAllRevision > revision)
        {
            return RegistryReading.Rejected("drain-all-above-revision");
        }

        if (drainAllRevision != 0 && objectLedgerStoreId is null)
        {
            return RegistryReading.Rejected("drain-all-without-object-ledger-store");
        }

        var nodes = new List<RuleEntry>();
        byte[]? previous = null;
        foreach (var entry in entries)
        {
            if (Identity(entry.Node) is not { } node)
            {
                return RegistryReading.Rejected("bad-node");
            }

            if (previous is not null && previous.AsSpan().SequenceCompareTo(entry.Node) is >= 0 and var order)
            {
                return RegistryReading.Rejected(order == 0 ? "repeated-node" : "nodes-not-ascending");
            }

            previous = entry.Node;
            var endpoint = Encoding.UTF8.GetString(entry.SiloEndpoint);
            if (!IPEndPoint.TryParse(endpoint, out var parsed) || parsed.ToString() != endpoint || parsed.Address.IsIPv4MappedToIPv6 || endpoint.Contains('%', StringComparison.Ordinal))
            {
                return RegistryReading.Rejected("bad-silo-endpoint");
            }

            if (entry.Generation <= 0)
            {
                return RegistryReading.Rejected("non-positive-generation");
            }

            if (entry.Ordinal < 0)
            {
                return RegistryReading.Rejected("negative-ordinal");
            }

            if (entry.RetiredRevision > revision)
            {
                return RegistryReading.Rejected("retired-above-revision");
            }

            if (entry.FailureDomain.Length > 64)
            {
                return RegistryReading.Rejected("long-failure-domain");
            }

            if (entry.Diskless && entry.Ordinal is not null)
            {
                return RegistryReading.Rejected("diskless-ordinal");
            }

            nodes.Add(new RuleEntry(
                node,
                endpoint,
                entry.Generation,
                entry.Ordinal,
                entry.RetiredRevision,
                entry.FailureDomain.Length == 0 ? null : Encoding.UTF8.GetString(entry.FailureDomain),
                entry.Diskless));
        }

        return new RegistryReading(null, new RuleRegistry(revision, store, archiveStoreId, archiveStorePrefix, objectLedgerStoreId, nodes, drainAllRevision));
    }

    /// <summary>
    /// Returns the input with its CRC-32C recomputed over its header and body, when it holds both, so the fuzzer reaches
    /// the rules behind it; otherwise <see langword="null"/>.
    /// </summary>
    public static byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (input.Length < Header || BinaryPrimitives.ReadUInt32LittleEndian(input[8..]) is var length && length > input.Length - Header)
        {
            return null;
        }

        var sealedInput = input.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(sealedInput.AsSpan(12), Checksum(input[..12], input.Slice(Header, (int)length)));
        return sealedInput;
    }

    private static uint Checksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        var crc = new Crc32(Crc32ParameterSet.Crc32C);
        crc.Append(header);
        crc.Append(body);
        return crc.GetCurrentHashAsUInt32();
    }

    private static Guid? Identity(byte[] bytes) => bytes.Length == 16 && bytes.AsSpan().ContainsAnyExcept((byte)0) ? new Guid(bytes, bigEndian: true) : null;

    /// <summary>A node entry's fields as the canonical reader read them.</summary>
    private sealed record Entry(byte[] Node, byte[] SiloEndpoint, int Generation, int? Ordinal, ulong RetiredRevision, byte[] FailureDomain, bool Diskless);

    /// <summary>Reads the canonical encoding of a <c>NodeRegistry</c>, and nothing else.</summary>
    private ref struct CanonicalReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        public bool ReadRegistry(out ulong revision, out int store, out byte[] storeId, out byte[] storePrefix, out byte[] objectStoreId, out ulong drainAllRevision, out List<Entry> entries)
        {
            revision = 0;
            store = 0;
            storeId = [];
            storePrefix = [];
            objectStoreId = [];
            drainAllRevision = 0;
            entries = [];
            var last = 0;
            while (_position < _bytes.Length)
            {
                if (!ReadTag(out var field, out var wire) || field < last || field == last && field != 1)
                {
                    return false;
                }

                last = field;
                switch (field)
                {
                    case 1 when wire == 2 && ReadLength(out var entryBytes):
                        var nested = new CanonicalReader(entryBytes);
                        if (!nested.ReadEntry(out var entry))
                        {
                            return false;
                        }

                        entries.Add(entry);
                        break;
                    case 2 when wire == 0 && ReadVarint(out revision) && revision != 0:
                        break;
                    case 3 when wire == 0 && ReadInt32(out store) && store != 0:
                        break;
                    case 4 when wire == 2 && ReadLength(out var id) && id.Length != 0:
                        storeId = id.ToArray();
                        break;
                    case 5 when wire == 2 && ReadLength(out var prefix) && prefix.Length != 0 && Utf8.IsValid(prefix):
                        storePrefix = prefix.ToArray();
                        break;
                    case 6 when wire == 2 && ReadLength(out var objectId) && objectId.Length != 0:
                        objectStoreId = objectId.ToArray();
                        break;
                    case 7 when wire == 0 && ReadVarint(out drainAllRevision) && drainAllRevision != 0:
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        private bool ReadEntry(out Entry entry)
        {
            entry = new Entry([], [], 0, null, 0, [], false);
            var last = 0;
            while (_position < _bytes.Length)
            {
                if (!ReadTag(out var field, out var wire) || field <= last)
                {
                    return false;
                }

                last = field;
                switch (field)
                {
                    case 1 when wire == 2 && ReadLength(out var node) && node.Length != 0:
                        entry = entry with { Node = node.ToArray() };
                        break;
                    case 2 when wire == 2 && ReadLength(out var endpoint) && endpoint.Length != 0 && Utf8.IsValid(endpoint):
                        entry = entry with { SiloEndpoint = endpoint.ToArray() };
                        break;
                    case 3 when wire == 0 && ReadInt32(out var generation) && generation != 0:
                        entry = entry with { Generation = generation };
                        break;
                    case 4 when wire == 0 && ReadInt32(out var ordinal):
                        entry = entry with { Ordinal = ordinal };
                        break;
                    case 5 when wire == 0 && ReadVarint(out var retired) && retired != 0:
                        entry = entry with { RetiredRevision = retired };
                        break;
                    case 6 when wire == 2 && ReadLength(out var domain) && domain.Length != 0 && Utf8.IsValid(domain):
                        entry = entry with { FailureDomain = domain.ToArray() };
                        break;
                    case 7 when wire == 0 && ReadVarint(out var diskless) && diskless == 1:
                        entry = entry with { Diskless = true };
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        private bool ReadTag(out int field, out int wire)
        {
            field = wire = 0;
            if (!ReadVarint(out var tag) || tag >> 3 is 0 or > 1 << 20)
            {
                return false;
            }

            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            return true;
        }

        /// <summary>Reads a varint in its minimal form: at most ten bytes, the tenth at most 1, and no trailing zero group.</summary>
        private bool ReadVarint(out ulong value)
        {
            value = 0;
            for (var index = 0; index < 10; index++)
            {
                if (_position >= _bytes.Length)
                {
                    return false;
                }

                var next = _bytes[_position++];
                if (index == 9 && next > 1)
                {
                    return false;
                }

                value |= (ulong)(next & 0x7F) << (7 * index);
                if ((next & 0x80) == 0)
                {
                    return index == 0 || next != 0;
                }
            }

            return false;
        }

        /// <summary>Reads an <c>int32</c> varint, which Protocol Buffers writes sign-extended to 64 bits.</summary>
        private bool ReadInt32(out int value)
        {
            value = 0;
            if (!ReadVarint(out var raw) || raw is > int.MaxValue and < 0xFFFF_FFFF_8000_0000)
            {
                return false;
            }

            value = unchecked((int)raw);
            return true;
        }

        private bool ReadLength(out ReadOnlySpan<byte> value)
        {
            value = default;
            if (!ReadVarint(out var length) || length > (ulong)(_bytes.Length - _position))
            {
                return false;
            }

            value = _bytes.Slice(_position, (int)length);
            _position += (int)length;
            return true;
        }
    }
}

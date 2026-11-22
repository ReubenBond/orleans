using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Net;
using System.Text;
using System.Text.Unicode;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// What decoding a node registry value gives (conventions.md §15): the value, or the <see cref="InvalidDataException"/>
/// naming the first rule its bytes break, with the rule's reason code (orleans.md §7.3).
/// </summary>
internal readonly union NodeRegistryDecoding(NodeRegistryValue, InvalidDataException);

/// <summary>
/// Encodes and decodes the <c>node-registry</c> register's value, <c>CLNR</c> version 1 (orleans.md §7.3): a 16-byte header
/// and the canonical Protocol Buffers encoding of <c>orleans.durable.storage.v1.NodeRegistry</c>.
/// </summary>
/// <remarks>
/// <para>
/// The body's canonical encoding writes each set field once, in field-number order, omits each proto3 field at its default
/// but <c>ordinal</c>, which it writes whenever present, and uses minimal varints, with negative <c>int32</c> values
/// sign-extended to ten bytes as Protocol Buffers writes them. The decoder parses the body, re-encodes what it parsed, and
/// accepts only bytes equal to that encoding, so unknown fields, repeated scalars, invalid UTF-8, and padded varints are
/// rejected as non-canonical.
/// </para>
/// <para>
/// A decoder checks the rules in the order of the reason codes below (orleans.md §7.3), and each rejection carries its code
/// under <see cref="DecodeRejection.ReasonKey"/>.
/// </para>
/// </remarks>
internal static class NodeRegistryCodec
{
    internal const string ReasonTruncated = "truncated";
    internal const string ReasonBadMagic = "bad-magic";
    internal const string ReasonBadVersion = "bad-version";
    internal const string ReasonBadFlags = "bad-flags";
    internal const string ReasonCrcMismatch = "crc-mismatch";
    internal const string ReasonTrailingBytes = "trailing-bytes";
    internal const string ReasonNonCanonical = "non-canonical";
    internal const string ReasonZeroRevision = "zero-revision";
    internal const string ReasonUnknownArchiveStore = "unknown-archive-store";
    internal const string ReasonBadArchiveStoreId = "bad-archive-store-id";
    internal const string ReasonBadArchiveStorePrefix = "bad-archive-store-prefix";
    internal const string ReasonBadObjectLedgerStoreId = "bad-object-ledger-store-id";
    internal const string ReasonDrainAllAboveRevision = "drain-all-above-revision";
    internal const string ReasonDrainAllWithoutObjectLedgerStore = "drain-all-without-object-ledger-store";
    internal const string ReasonBadNode = "bad-node";
    internal const string ReasonNodesNotAscending = "nodes-not-ascending";
    internal const string ReasonRepeatedNode = "repeated-node";
    internal const string ReasonBadSiloEndpoint = "bad-silo-endpoint";
    internal const string ReasonNonPositiveGeneration = "non-positive-generation";
    internal const string ReasonNegativeOrdinal = "negative-ordinal";
    internal const string ReasonRetiredAboveRevision = "retired-above-revision";
    internal const string ReasonLongFailureDomain = "long-failure-domain";
    internal const string ReasonDisklessOrdinal = "diskless-ordinal";

    /// <summary>The magic, version, flags, body length, and CRC-32C.</summary>
    internal const int HeaderLength = 16;

    /// <summary>
    /// The largest value the <c>node-registry</c> register holds, a constant of the format: Azure Table's limit, the
    /// smallest of the MetaStore providers' (metastore.md §8). Every committer of the cluster reserves room within it, so
    /// every value written has room for each of its nodes' retirements and restarts (orleans.md §5.3).
    /// </summary>
    internal const int MaxValueLength = 64 << 10;

    internal const ushort FormatVersion = 1;

    private const int IdentityLength = 16;

    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLengthDelimited = 2;
    private const int WireFixed32 = 5;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> Magic => "CLNR"u8;

    /// <summary>Returns the value's <c>CLNR</c> encoding.</summary>
    public static byte[] Encode(NodeRegistryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var body = new ArrayBufferWriter<byte>();
        WriteValue(body, value);
        var encoded = new byte[checked(HeaderLength + body.WrittenCount)];
        Magic.CopyTo(encoded);
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(8), (uint)body.WrittenCount);
        body.WrittenSpan.CopyTo(encoded.AsSpan(HeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(12), Checksum(encoded.AsSpan(0, 12), body.WrittenSpan));
        return encoded;
    }

    /// <summary>Decodes a <c>CLNR</c> value, which the caller keeps; a value spanning segments is read from a pooled copy.</summary>
    public static NodeRegistryDecoding Decode(ReadOnlySequence<byte> value)
    {
        if (value.IsSingleSegment)
        {
            return Decode(value.FirstSpan);
        }

        var length = checked((int)value.Length);
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            value.CopyTo(bytes);
            return Decode(bytes.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// Decodes a <c>CLNR</c> value, accepting only its canonical encoding, or returns the rejection naming the first rule
    /// its bytes break.
    /// </summary>
    public static NodeRegistryDecoding Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength)
        {
            return Rejection(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"A node registry value holds its {HeaderLength}-byte header, but has {bytes.Length} bytes."));
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            return Rejection(ReasonBadMagic, "A node registry value begins with the magic CLNR.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (version != FormatVersion)
        {
            return Rejection(ReasonBadVersion, string.Create(CultureInfo.InvariantCulture, $"A node registry value is version {FormatVersion}, not {version}."));
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (flags != 0)
        {
            return Rejection(ReasonBadFlags, string.Create(CultureInfo.InvariantCulture, $"A node registry value's flags are 0, not {flags}."));
        }

        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (bodyLength > (uint)(bytes.Length - HeaderLength))
        {
            return Rejection(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"A node registry value's body is {bodyLength} bytes, but {bytes.Length - HeaderLength} follow its header."));
        }

        var body = bytes.Slice(HeaderLength, (int)bodyLength);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != Checksum(bytes[..12], body))
        {
            return Rejection(ReasonCrcMismatch, "A node registry value's CRC-32C does not match its header and body.");
        }

        if (bytes.Length != HeaderLength + body.Length)
        {
            return Rejection(ReasonTrailingBytes, string.Create(CultureInfo.InvariantCulture, $"A node registry value ends at its body, but {bytes.Length - HeaderLength - body.Length} bytes follow it."));
        }

        if (!TryReadRegistry(body, out var raw) || !IsCanonical(raw, body))
        {
            return Rejection(ReasonNonCanonical, "A node registry value's body is not the canonical Protocol Buffers encoding of an orleans.durable.storage.v1.NodeRegistry.");
        }

        return ToValue(raw);
    }

    /// <summary>
    /// Reads the register once and decodes its value, disposing the entry read (conventions.md §1); an absent register
    /// reads as <see cref="NodeRegistryValue.Empty"/>, with no version.
    /// </summary>
    /// <exception cref="InvalidDataException">The register holds a value which is not a node registry: the rejection itself.</exception>
    public static async Task<(NodeRegistryValue Value, MetaStoreVersion? Version)> ReadAsync(IConditionalRegister register, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(register);
        using var entry = await register.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return (NodeRegistryValue.Empty, null);
        }

        return Decode(entry.Value.AsReadOnlySequence()) switch
        {
            NodeRegistryValue value => (value, entry.Version),
            InvalidDataException rejection => throw rejection,
            null => throw new UnreachableException(),
        };
    }

    private static uint Checksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        var crc = new Crc32(Crc32ParameterSet.Crc32C);
        crc.Append(header);
        crc.Append(body);
        return crc.GetCurrentHashAsUInt32();
    }

    private static bool IsCanonical(RawRegistry raw, ReadOnlySpan<byte> body)
    {
        if (!Utf8.IsValid(raw.ArchiveStorePrefix))
        {
            return false;
        }

        foreach (var entry in raw.Nodes)
        {
            if (!Utf8.IsValid(entry.SiloEndpoint) || !Utf8.IsValid(entry.FailureDomain))
            {
                return false;
            }
        }

        // ArrayBufferWriter refuses a capacity of 0, and an empty body parses: its revision is 0, which ToValue refuses.
        var encoded = new ArrayBufferWriter<byte>(Math.Max(body.Length, 1));
        WriteRegistry(encoded, raw);
        return encoded.WrittenSpan.SequenceEqual(body);
    }

    /// <summary>Checks a canonical body's values in the order of the reason codes, and builds the value they hold.</summary>
    private static NodeRegistryDecoding ToValue(RawRegistry raw)
    {
        if (raw.Revision == 0)
        {
            return Rejection(ReasonZeroRevision, "A stored node registry value was written, so its revision is at least 1.");
        }

        ArchiveStoreIdentity? archiveStore = null;
        if (raw.ArchiveStore != 0)
        {
            // CLNR v1 records the Archive's three pinned kinds and no other, so a kind the Archive adds later widens no
            // accepted value without a decision of this format's.
            if (raw.ArchiveStore is not ((int)ArchiveStoreKind.Lsm or (int)ArchiveStoreKind.Logs or (int)ArchiveStoreKind.Memory))
            {
                return Rejection(ReasonUnknownArchiveStore, string.Create(CultureInfo.InvariantCulture, $"A node registry's archive store is the LSM (1), archive logs (2), or the memory store (3), not {raw.ArchiveStore}."));
            }

            if (ReadStoreId(raw.ArchiveStoreId) is not { } id)
            {
                return Rejection(ReasonBadArchiveStoreId, "A recorded archive store's ID is 16 non-zero bytes.");
            }

            if (ReadStorePrefix(raw.ArchiveStorePrefix) is not { } prefix)
            {
                return Rejection(ReasonBadArchiveStorePrefix, $"A recorded archive store's prefix is 1 to {ArchiveStorePrefix.MaxLength} characters of a-z, 0-9, '_', and '-'.");
            }

            archiveStore = new ArchiveStoreIdentity((ArchiveStoreKind)raw.ArchiveStore, prefix, id);
        }
        else if (raw.ArchiveStoreId.Length != 0)
        {
            return Rejection(ReasonBadArchiveStoreId, "A node registry which records no archive store records no archive store ID.");
        }
        else if (raw.ArchiveStorePrefix.Length != 0)
        {
            return Rejection(ReasonBadArchiveStorePrefix, "A node registry which records no archive store records no archive store prefix.");
        }

        ObjectLedgerStoreId? objectLedgerStore = null;
        if (raw.ObjectLedgerStoreId.Length != 0)
        {
            if (ReadObjectLedgerStoreId(raw.ObjectLedgerStoreId) is not { } objectStoreId)
            {
                return Rejection(ReasonBadObjectLedgerStoreId, "A recorded object-ledger store's ID is 16 non-zero bytes.");
            }

            objectLedgerStore = objectStoreId;
        }

        if (raw.DrainAllRevision > raw.Revision)
        {
            return Rejection(ReasonDrainAllAboveRevision, string.Create(CultureInfo.InvariantCulture, $"A node registry's drain-all revision {raw.DrainAllRevision} lies above its revision {raw.Revision}."));
        }

        if (raw.DrainAllRevision != 0 && objectLedgerStore is null)
        {
            return Rejection(ReasonDrainAllWithoutObjectLedgerStore, "A node registry records a drain-all revision only when it records an object-ledger store ID.");
        }

        var nodes = ImmutableArray.CreateBuilder<NodeRegistryEntry>(raw.Nodes.Count);
        foreach (var rawEntry in raw.Nodes)
        {
            var rejection = CheckEntry(rawEntry, raw.Revision, nodes.Count > 0 ? nodes[^1].Node : null, out var entry);
            if (rejection is not null)
            {
                return rejection;
            }

            nodes.Add(entry!);
        }

        return new NodeRegistryValue(raw.Revision, nodes.MoveToImmutable(), archiveStore, objectLedgerStore, raw.DrainAllRevision);
    }

    /// <summary>
    /// Checks one node's fields in the order of the reason codes, after <paramref name="previous"/>, the node before it,
    /// and builds its entry, or returns the rejection naming the first rule it breaks.
    /// </summary>
    private static InvalidDataException? CheckEntry(RawEntry raw, ulong revision, NodeId? previous, out NodeRegistryEntry? entry)
    {
        entry = null;
        if (ReadIdentity(raw.Node) is not { } identity)
        {
            return Rejection(ReasonBadNode, "A node registry entry's node identity is 16 non-zero bytes.");
        }

        var node = new NodeId(identity);
        if (previous is { } before)
        {
            var order = NodeRegistryValue.CompareNodes(before, node);
            if (order == 0)
            {
                return Rejection(ReasonRepeatedNode, string.Create(CultureInfo.InvariantCulture, $"A node registry lists node {node} twice."));
            }

            if (order > 0)
            {
                return Rejection(ReasonNodesNotAscending, string.Create(CultureInfo.InvariantCulture, $"A node registry lists its nodes in ascending node byte order, but {node} follows {before}."));
            }
        }

        // The endpoint is canonical as a SiloAddress holds it, which writes an IPv4-mapped address as IPv4; a scope ID
        // names an interface of one host, which no other silo shares.
        var text = StrictUtf8.GetString(raw.SiloEndpoint);
        if (!IPEndPoint.TryParse(text, out var endpoint) || !IsSiloEndpoint(endpoint) || !string.Equals(endpoint.ToString(), text, StringComparison.Ordinal))
        {
            return Rejection(ReasonBadSiloEndpoint, string.Create(CultureInfo.InvariantCulture, $"Node {node}'s silo endpoint is \"{{ip}}:{{port}}\", not \"{text}\"."));
        }

        if (raw.Generation <= 0)
        {
            return Rejection(ReasonNonPositiveGeneration, string.Create(CultureInfo.InvariantCulture, $"Node {node}'s silo generation is positive, not {raw.Generation}."));
        }

        if (raw.Ordinal is < 0)
        {
            return Rejection(ReasonNegativeOrdinal, string.Create(CultureInfo.InvariantCulture, $"Node {node}'s ordinal is non-negative, not {raw.Ordinal}."));
        }

        if (raw.RetiredRevision > revision)
        {
            return Rejection(ReasonRetiredAboveRevision, string.Create(CultureInfo.InvariantCulture, $"Node {node} is retired at revision {raw.RetiredRevision}, above the registry's revision {revision}."));
        }

        if (raw.FailureDomain.Length > NodeRegistryEntry.MaxFailureDomainBytes)
        {
            return Rejection(ReasonLongFailureDomain, string.Create(CultureInfo.InvariantCulture, $"Node {node}'s failure domain is at most {NodeRegistryEntry.MaxFailureDomainBytes} bytes, not {raw.FailureDomain.Length}."));
        }

        if (raw.Diskless && raw.Ordinal is not null)
        {
            return Rejection(ReasonDisklessOrdinal, string.Create(CultureInfo.InvariantCulture, $"Diskless node {node} cannot carry a StatefulSet ordinal."));
        }

        var failureDomain = raw.FailureDomain.Length == 0 ? null : StrictUtf8.GetString(raw.FailureDomain);
        entry = new NodeRegistryEntry(node, SiloAddress.New(endpoint, raw.Generation), raw.Ordinal, failureDomain, raw.RetiredRevision, raw.Diskless);
        return null;
    }
    /// <summary>
    /// Returns whether <paramref name="endpoint"/> is one a silo address holds, written as <c>CLNR</c> writes it: no
    /// IPv4-mapped IPv6 address, which a <see cref="SiloAddress"/> holds as IPv4, and no IPv6 scope ID.
    /// </summary>
    internal static bool IsSiloEndpoint(IPEndPoint endpoint) =>
        !endpoint.Address.IsIPv4MappedToIPv6 && (endpoint.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || endpoint.Address.ScopeId == 0);

    private static Guid? ReadIdentity(ReadOnlySpan<byte> bytes) =>
        bytes.Length == IdentityLength && bytes.ContainsAnyExcept((byte)0) ? new Guid(bytes, bigEndian: true) : null;

    /// <summary>
    /// Reads an archive store's prefix as <c>archive_store_prefix</c> holds it, UTF-8 which the canonical check verified, or
    /// <see langword="null"/> when it is no prefix: 1 to 16 characters of <c>[a-z0-9_-]</c>.
    /// </summary>
    private static ArchiveStorePrefix? ReadStorePrefix(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes) is var text && ArchiveStorePrefix.IsValid(text) ? new ArchiveStorePrefix(text) : null;

    /// <summary>Reads an archive store's ID as <c>archive_store_id</c> holds it, its 16 bytes big-endian, or <see langword="null"/> when they are not 16 non-zero bytes.</summary>
    private static ArchiveStoreId? ReadStoreId(ReadOnlySpan<byte> bytes) =>
        bytes.Length == IdentityLength && BinaryPrimitives.ReadUInt128BigEndian(bytes) is var value && value != UInt128.Zero ? new ArchiveStoreId(value) : null;

    /// <summary>Reads an object-ledger store's ID as <c>object_ledger_store_id</c> holds it, its 16 bytes big-endian, or <see langword="null"/> when they are not 16 non-zero bytes.</summary>
    private static ObjectLedgerStoreId? ReadObjectLedgerStoreId(ReadOnlySpan<byte> bytes) =>
        bytes.Length == IdentityLength && BinaryPrimitives.ReadUInt128BigEndian(bytes) is var value && value != UInt128.Zero ? new ObjectLedgerStoreId(value) : null;

    /// <summary>Writes an object-ledger store's ID as <c>object_ledger_store_id</c> holds it: 16 bytes big-endian.</summary>
    private static void WriteObjectLedgerStoreId(IBufferWriter<byte> writer, ObjectLedgerStoreId id)
    {
        Span<byte> bytes = stackalloc byte[IdentityLength];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, id.Value);
        WriteBytes(writer, 6, bytes);
    }

    private static InvalidDataException Rejection(string reason, string message) => DecodeRejection.Create(reason, message);

    /// <summary>Writes the canonical encoding of <paramref name="value"/> without allocating intermediate byte arrays.</summary>
    private static void WriteValue(IBufferWriter<byte> writer, NodeRegistryValue value)
    {
        var entry = new ArrayBufferWriter<byte>();
        Span<byte> identity = stackalloc byte[IdentityLength];
        foreach (var node in value.Nodes)
        {
            entry.ResetWrittenCount();
            var written = node.Node.Value.TryWriteBytes(identity, bigEndian: true, out _);
            Debug.Assert(written, "An identity takes 16 bytes.");
            WriteBytes(entry, 1, identity);
            WriteUtf8(entry, 2, node.Silo.Endpoint.ToString());
            WriteVarintField(entry, 3, unchecked((ulong)node.Silo.Generation));
            if (node.Ordinal is { } ordinal)
            {
                WriteTag(entry, 4, WireVarint);
                WriteVarint(entry, unchecked((ulong)ordinal));
            }

            WriteVarintField(entry, 5, node.RetiredRevision);
            if (node.FailureDomain is { Length: > 0 } domain)
            {
                WriteUtf8(entry, 6, domain);
            }

            if (node.Diskless)
            {
                WriteVarintField(entry, 7, 1);
            }

            WriteBytes(writer, 1, entry.WrittenSpan, always: true);
        }

        WriteVarintField(writer, 2, value.Revision);
        if (value.ArchiveStore is { } archiveStore)
        {
            WriteVarintField(writer, 3, unchecked((ulong)(int)archiveStore.Kind));
            BinaryPrimitives.WriteUInt128BigEndian(identity, archiveStore.Id.Value);
            WriteBytes(writer, 4, identity);
            WriteUtf8(writer, 5, archiveStore.Prefix.Value);
        }

        if (value.ObjectLedgerStore is { } objectStore)
        {
            WriteObjectLedgerStoreId(writer, objectStore);
        }

        WriteVarintField(writer, 7, value.DrainAllRevision);
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, int field, string text)
    {
        var length = Encoding.UTF8.GetByteCount(text);
        if (length == 0)
        {
            return;
        }

        WriteTag(writer, field, WireLengthDelimited);
        WriteVarint(writer, (ulong)length);
        var span = writer.GetSpan(length);
        var written = Encoding.UTF8.GetBytes(text, span);
        Debug.Assert(written == length, "UTF-8 byte count matched.");
        writer.Advance(written);
    }

    /// <summary>Writes the canonical encoding of <c>NodeRegistry</c>: fields 1 to 7 in order, each omitted at its default.</summary>
    private static void WriteRegistry(IBufferWriter<byte> writer, RawRegistry registry)
    {
        var entry = new ArrayBufferWriter<byte>();
        foreach (var node in registry.Nodes)
        {
            entry.ResetWrittenCount();
            WriteEntry(entry, node);
            WriteBytes(writer, 1, entry.WrittenSpan, always: true);
        }

        WriteVarintField(writer, 2, registry.Revision);
        WriteVarintField(writer, 3, unchecked((ulong)registry.ArchiveStore));
        WriteBytes(writer, 4, registry.ArchiveStoreId);
        WriteBytes(writer, 5, registry.ArchiveStorePrefix);
        WriteBytes(writer, 6, registry.ObjectLedgerStoreId);
        WriteVarintField(writer, 7, registry.DrainAllRevision);
    }

    /// <summary>
    /// Writes the canonical encoding of <c>NodeEntry</c>: fields 1 to 7 in order, each omitted at its default but the
    /// ordinal, written whenever present.
    /// </summary>
    private static void WriteEntry(IBufferWriter<byte> writer, RawEntry entry)
    {
        WriteBytes(writer, 1, entry.Node);
        WriteBytes(writer, 2, entry.SiloEndpoint);
        WriteVarintField(writer, 3, unchecked((ulong)entry.Generation));
        if (entry.Ordinal is { } ordinal)
        {
            WriteTag(writer, 4, WireVarint);
            WriteVarint(writer, unchecked((ulong)ordinal));
        }

        WriteVarintField(writer, 5, entry.RetiredRevision);
        WriteBytes(writer, 6, entry.FailureDomain);
        if (entry.Diskless)
        {
            WriteVarintField(writer, 7, 1);
        }
    }

    private static void WriteVarintField(IBufferWriter<byte> writer, int field, ulong value)
    {
        if (value != 0)
        {
            WriteTag(writer, field, WireVarint);
            WriteVarint(writer, value);
        }
    }

    private static void WriteBytes(IBufferWriter<byte> writer, int field, ReadOnlySpan<byte> value, bool always = false)
    {
        if (always || value.Length != 0)
        {
            WriteTag(writer, field, WireLengthDelimited);
            WriteVarint(writer, (ulong)value.Length);
            writer.Write(value);
        }
    }

    private static void WriteTag(IBufferWriter<byte> writer, int field, int wireType) => WriteVarint(writer, (ulong)((field << 3) | wireType));

    private static void WriteVarint(IBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(10);
        var length = 0;
        while (value >= 0x80)
        {
            span[length++] = unchecked((byte)(value | 0x80));
            value >>= 7;
        }

        span[length++] = (byte)value;
        writer.Advance(length);
    }

    /// <summary>
    /// Parses a <c>NodeRegistry</c> leniently, as Protocol Buffers does: fields in any order, the last of a repeated scalar,
    /// and unknown fields skipped. False when the bytes are not a Protocol Buffers message of that type.
    /// </summary>
    private static bool TryReadRegistry(ReadOnlySpan<byte> body, out RawRegistry registry)
    {
        registry = new RawRegistry();
        var position = 0;
        while (position < body.Length)
        {
            if (!TryReadTag(body, ref position, out var field, out var wireType))
            {
                return false;
            }

            switch (field, wireType)
            {
                case (1, WireLengthDelimited):
                    if (!TryReadLengthDelimited(body, ref position, out var entryBytes) || !TryReadEntry(entryBytes, out var entry))
                    {
                        return false;
                    }

                    registry.Nodes.Add(entry);
                    break;
                case (2, WireVarint):
                    if (!TryReadVarint(body, ref position, out var revision))
                    {
                        return false;
                    }

                    registry.Revision = revision;
                    break;
                case (3, WireVarint):
                    if (!TryReadVarint(body, ref position, out var archiveStore))
                    {
                        return false;
                    }

                    registry.ArchiveStore = unchecked((int)archiveStore);
                    break;
                case (4, WireLengthDelimited):
                    if (!TryReadLengthDelimited(body, ref position, out var id))
                    {
                        return false;
                    }

                    registry.ArchiveStoreId = id.ToArray();
                    break;
                case (5, WireLengthDelimited):
                    if (!TryReadLengthDelimited(body, ref position, out var prefix))
                    {
                        return false;
                    }

                    registry.ArchiveStorePrefix = prefix.ToArray();
                    break;
                case (6, WireLengthDelimited):
                    if (!TryReadLengthDelimited(body, ref position, out var objectStoreId))
                    {
                        return false;
                    }

                    registry.ObjectLedgerStoreId = objectStoreId.ToArray();
                    break;
                case (7, WireVarint):
                    if (!TryReadVarint(body, ref position, out var drainAllRevision))
                    {
                        return false;
                    }

                    registry.DrainAllRevision = drainAllRevision;
                    break;
                case (1 or 2 or 3 or 4 or 5 or 6 or 7, _):
                    return false;
                default:
                    if (!TrySkip(body, ref position, wireType))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static bool TryReadEntry(ReadOnlySpan<byte> bytes, out RawEntry entry)
    {
        entry = new RawEntry();
        var position = 0;
        while (position < bytes.Length)
        {
            if (!TryReadTag(bytes, ref position, out var field, out var wireType))
            {
                return false;
            }

            ReadOnlySpan<byte> value;
            ulong number;
            switch (field, wireType)
            {
                case (1, WireLengthDelimited):
                    if (!TryReadLengthDelimited(bytes, ref position, out value))
                    {
                        return false;
                    }

                    entry.Node = value.ToArray();
                    break;
                case (2, WireLengthDelimited):
                    if (!TryReadLengthDelimited(bytes, ref position, out value))
                    {
                        return false;
                    }

                    entry.SiloEndpoint = value.ToArray();
                    break;
                case (3, WireVarint):
                    if (!TryReadVarint(bytes, ref position, out number))
                    {
                        return false;
                    }

                    entry.Generation = unchecked((int)number);
                    break;
                case (4, WireVarint):
                    if (!TryReadVarint(bytes, ref position, out number))
                    {
                        return false;
                    }

                    entry.Ordinal = unchecked((int)number);
                    break;
                case (5, WireVarint):
                    if (!TryReadVarint(bytes, ref position, out number))
                    {
                        return false;
                    }

                    entry.RetiredRevision = number;
                    break;
                case (6, WireLengthDelimited):
                    if (!TryReadLengthDelimited(bytes, ref position, out value))
                    {
                        return false;
                    }

                    entry.FailureDomain = value.ToArray();
                    break;
                case (7, WireVarint):
                    if (!TryReadVarint(bytes, ref position, out number) || number > 1)
                    {
                        return false;
                    }

                    entry.Diskless = number == 1;
                    break;
                case (1 or 2 or 3 or 4 or 5 or 6 or 7, _):
                    return false;
                default:
                    if (!TrySkip(bytes, ref position, wireType))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static bool TryReadTag(ReadOnlySpan<byte> bytes, ref int position, out int field, out int wireType)
    {
        field = wireType = 0;
        if (!TryReadVarint(bytes, ref position, out var tag) || tag >> 3 is 0 or > int.MaxValue)
        {
            return false;
        }

        field = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> bytes, ref int position, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (position >= bytes.Length)
            {
                return false;
            }

            var next = bytes[position++];
            if (shift == 63 && next > 1)
            {
                return false;
            }

            value |= (ulong)(next & 0x7F) << shift;
            if (next < 0x80)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadLengthDelimited(ReadOnlySpan<byte> bytes, scoped ref int position, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryReadVarint(bytes, ref position, out var length) || length > (ulong)(bytes.Length - position))
        {
            return false;
        }

        value = bytes.Slice(position, (int)length);
        position += (int)length;
        return true;
    }

    private static bool TrySkip(ReadOnlySpan<byte> bytes, ref int position, int wireType)
    {
        switch (wireType)
        {
            case WireVarint:
                return TryReadVarint(bytes, ref position, out _);
            case WireFixed64 or WireFixed32:
                var length = wireType == WireFixed64 ? 8 : 4;
                if (bytes.Length - position < length)
                {
                    return false;
                }

                position += length;
                return true;
            case WireLengthDelimited:
                return TryReadLengthDelimited(bytes, ref position, out _);
            default:
                return false;
        }
    }

    /// <summary>A <c>NodeRegistry</c> message as parsed, before its values are checked.</summary>
    private sealed class RawRegistry
    {
        public ulong Revision { get; set; }

        public List<RawEntry> Nodes { get; } = [];

        public int ArchiveStore { get; set; }

        public byte[] ArchiveStoreId { get; set; } = [];

        public byte[] ArchiveStorePrefix { get; set; } = [];

        public byte[] ObjectLedgerStoreId { get; set; } = [];

        public ulong DrainAllRevision { get; set; }
    }

    /// <summary>A <c>NodeEntry</c> message as parsed, before its values are checked.</summary>
    private sealed class RawEntry
    {
        public ulong RetiredRevision { get; set; }

        public byte[] Node { get; set; } = [];

        public byte[] SiloEndpoint { get; set; } = [];

        public int Generation { get; set; }

        public int? Ordinal { get; set; }

        public byte[] FailureDomain { get; set; } = [];

        public bool Diskless { get; set; }
    }
}

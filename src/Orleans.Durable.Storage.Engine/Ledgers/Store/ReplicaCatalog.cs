using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Buffers;

using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// One live ledger replica of a store: its handle, the window it was created with, the WAL file holding its create
/// record, and its ledger ID.
/// </summary>
/// <remarks>
/// The window bounds the replica's unwritten addresses: a reopened replica rebuilds its retained contiguous tail from it,
/// and refuses an operation of another window (LR-I6).
/// </remarks>
internal sealed record CatalogReplica(uint Handle, int Window, ulong FirstFile, LedgerId Ledger);

/// <summary>
/// A catalog checkpoint: the catalog the create and drop records below <see cref="Cut"/> leave (ledgerstore.md §7.2).
/// </summary>
internal sealed record CatalogCheckpoint(WalPosition Cut, ReplicaCatalog Catalog);

/// <summary>
/// What decoding a catalog checkpoint gives (conventions.md §15): the checkpoint, or the
/// <see cref="InvalidDataException"/> naming the first rule its bytes break, with the rule's reason code
/// (ledgerstore.md §7.2).
/// </summary>
internal readonly union CatalogDecoding(CatalogCheckpoint, InvalidDataException);

/// <summary>
/// A LedgerStore's catalog: the next handle, the live replicas, and the tombstones, the fold in WAL order of the WAL's
/// create and drop records over the newest valid catalog checkpoint (LS-I1, LS-I2).
/// </summary>
/// <remarks>
/// <para>
/// The catalog is derived. Catalog checkpoints persist it, each the named file <c>{file:D20}-{offset:D20}.clsc</c> in
/// <c>wal/</c>, named by its cut and created whole. The <c>CLSC</c> v1 format (ledgerstore.md §7.2) is a 16-byte
/// header, the magic <c>CLSC</c>, the version, the body length, and the CRC-32C of the body, each a little-endian 32-bit
/// integer after the magic, followed by the body:
/// </para>
/// <code>
/// cut               u64 file, u64 offset
/// next handle       u32
/// replica count     u32, then per replica in ascending handle order:
///   handle u32, window u32, first file u64, ledger ID u64
/// tombstone count   u32, then each tombstone's ledger ID u64, ascending
/// </code>
/// <para>
/// A tombstone is a dropped ledger, sealed and dropped on the node for its life: the catalog never discards one, and
/// keeps them in an immutable set which a drop extends, so a capture copies none (ledgerstore.md §8.8).
/// </para>
/// <para>
/// Decoding accepts only the canonical encoding. Every rejection is an <see cref="InvalidDataException"/> carrying the
/// reason code of the shared catalog vectors (<see cref="DecodeRejection"/>), and so is every contradiction between a
/// create or drop record and the catalog it applies to.
/// </para>
/// </remarks>
internal sealed class ReplicaCatalog
{
    internal const int BodyLengthOffset = 8;
    internal const int BodyCrcOffset = 12;
    internal const int HeaderLength = 16;

    private const uint Version = 1;
    private const int ReplicaLength = 2 * sizeof(uint) + 2 * sizeof(ulong);

    private readonly ImmutableDictionary<LedgerId, CatalogReplica> _byLedger;

    internal ReplicaCatalog(uint nextHandle, CatalogReplica[] replicas, ImmutableSortedSet<LedgerId> tombstones)
        : this(nextHandle, replicas, replicas.ToImmutableDictionary(static replica => replica.Ledger), tombstones)
    {
    }

    private ReplicaCatalog(
        uint nextHandle, CatalogReplica[] replicas, ImmutableDictionary<LedgerId, CatalogReplica> byLedger, ImmutableSortedSet<LedgerId> tombstones)
    {
        NextHandle = nextHandle;
        Replicas = replicas;
        _byLedger = byLedger;
        Tombstones = tombstones;
    }

    /// <summary>
    /// Gets the catalog of a store which holds no record: no replica, no tombstone, and handles from 0.
    /// </summary>
    public static ReplicaCatalog Empty { get; } = new(0, [], []);

    /// <summary>
    /// Gets the handle the next created replica takes.
    /// </summary>
    public uint NextHandle { get; }

    /// <summary>
    /// Gets a value indicating whether the catalog is the empty one: no create or drop record shaped it.
    /// </summary>
    public bool IsEmpty => NextHandle == 0 && Tombstones.IsEmpty;

    /// <summary>
    /// Gets the live replicas in ascending handle order.
    /// </summary>
    public IReadOnlyList<CatalogReplica> Replicas { get; }

    /// <summary>
    /// Gets the dropped ledgers, ascending: each is sealed and dropped on the node for its life (LS-I2).
    /// </summary>
    public ImmutableSortedSet<LedgerId> Tombstones { get; }

    private static ReadOnlySpan<byte> Magic => "CLSC"u8;

    /// <summary>
    /// Returns the live replica of <paramref name="ledger"/>, or <see langword="null"/>.
    /// </summary>
    public CatalogReplica? Find(LedgerId ledger) => _byLedger.GetValueOrDefault(ledger);

    /// <summary>
    /// Returns whether the catalog holds <paramref name="ledger"/>'s tombstone: the node dropped it (LS-I2).
    /// </summary>
    public bool IsDropped(LedgerId ledger) => Tombstones.Contains(ledger);

    /// <summary>
    /// Applies a create record holding <paramref name="pairs"/>, in file <paramref name="file"/>.
    /// </summary>
    /// <param name="pairs">The record's pairs.</param>
    /// <param name="file">The WAL file holding the record, each created replica's first file.</param>
    /// <param name="catalog">The catalog the record leaves, or <see langword="null"/> when it disagrees.</param>
    /// <returns>
    /// <see langword="null"/>, or the rejection naming how the record disagrees with the catalog (ledgerstore.md §7.3):
    /// its handles do not run consecutively from the next handle, or it names a ledger twice, one the catalog lists, or
    /// one it dropped.
    /// </returns>
    public InvalidDataException? Create(IReadOnlyList<CatalogPair> pairs, ulong file, out ReplicaCatalog? catalog)
    {
        catalog = null;
        var replicas = new CatalogReplica[Replicas.Count + pairs.Count];
        var index = 0;
        foreach (var replica in Replicas)
        {
            replicas[index++] = replica;
        }

        var byLedger = _byLedger.ToBuilder();
        var next = NextHandle;
        foreach (var pair in pairs)
        {
            if (pair.Handle != next)
            {
                return Rejection("create-handle-mismatch");
            }

            if (IsDropped(pair.Ledger))
            {
                return Rejection("create-ledger-dropped");
            }

            var created = new CatalogReplica(pair.Handle, (int)pair.Window, file, pair.Ledger);
            if (!byLedger.TryAdd(pair.Ledger, created))
            {
                return Rejection("create-ledger-listed");
            }

            replicas[index++] = created;
            next++;
        }

        catalog = new ReplicaCatalog(next, replicas, byLedger.ToImmutable(), Tombstones);
        return null;
    }

    /// <summary>
    /// Applies a drop record of <paramref name="ledger"/>, which leaves the catalog without the ledger's replica, if it
    /// lists one, and with its tombstone (LS-I2).
    /// </summary>
    /// <param name="ledger">The dropped ledger.</param>
    /// <param name="catalog">The catalog the record leaves, or <see langword="null"/> when it disagrees.</param>
    /// <returns>
    /// <see langword="null"/>, or the rejection of a drop of a ledger the catalog already dropped (ledgerstore.md §7.3).
    /// </returns>
    public InvalidDataException? Drop(LedgerId ledger, out ReplicaCatalog? catalog)
    {
        catalog = null;
        if (IsDropped(ledger))
        {
            return Rejection("drop-ledger-dropped");
        }

        var replicas = _byLedger.ContainsKey(ledger) ? [.. Replicas.Where(replica => replica.Ledger != ledger)] : (CatalogReplica[])Replicas;
        catalog = new ReplicaCatalog(NextHandle, replicas, _byLedger.Remove(ledger), Tombstones.Add(ledger));
        return null;
    }

    /// <summary>
    /// Applies a create or drop record, checking it against this catalog (ledgerstore.md §7.3). A seal record changes
    /// no catalog: it seals a live replica.
    /// </summary>
    /// <param name="record">The create or drop record.</param>
    /// <param name="file">The WAL file holding it, a created replica's first file.</param>
    /// <param name="catalog">The catalog the record leaves, or <see langword="null"/> when it disagrees.</param>
    /// <returns><see langword="null"/>, or the rejection naming how the record disagrees with the catalog.</returns>
    public InvalidDataException? Apply(StoreRecord record, ulong file, out ReplicaCatalog? catalog) => record switch
    {
        CreateRecord create => Create(create.Pairs, file, out catalog),
        DropRecord drop => Drop(drop.Ledger, out catalog),
        _ => throw new ArgumentException("A store record is a create or a drop.", nameof(record)),
    };

    /// <summary>
    /// Returns whether this catalog follows <paramref name="earlier"/>, the catalog of an older checkpoint: it assigned
    /// at least its handles, keeps each of its tombstones, and of each replica it lists, either dropped the ledger or
    /// lists the same replica (LS-I2, ledgerstore.md §8.7 step 8).
    /// </summary>
    public bool Follows(ReplicaCatalog earlier) =>
        ReferenceEquals(this, earlier)
        || NextHandle >= earlier.NextHandle
            && (ReferenceEquals(Tombstones, earlier.Tombstones) || Tombstones.IsSupersetOf(earlier.Tombstones))
            && earlier.Replicas.All(replica => IsDropped(replica.Ledger) || Find(replica.Ledger) == replica);

    /// <summary>
    /// Encodes this catalog as the catalog checkpoint of <paramref name="cut"/> into <paramref name="writer"/>, writing
    /// each byte once (§7.2).
    /// </summary>
    /// <returns>The checkpoint's length.</returns>
    public int Encode(WalPosition cut, ArcBufferWriter writer)
    {
        var bodyLength = checked(2 * sizeof(ulong) + sizeof(uint) + sizeof(uint) + Replicas.Count * ReplicaLength + sizeof(uint) + Tombstones.Count * sizeof(ulong));
        Span<byte> header = stackalloc byte[HeaderLength];
        writer.Write(header);
        using var body = new BodyWriter(writer);
        var fields = body.Reserve(24);
        BinaryPrimitives.WriteUInt64LittleEndian(fields, cut.File);
        BinaryPrimitives.WriteUInt64LittleEndian(fields[8..], cut.Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[16..], NextHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[20..], (uint)Replicas.Count);
        foreach (var replica in Replicas)
        {
            var span = body.Reserve(ReplicaLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span, replica.Handle);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)replica.Window);
            BinaryPrimitives.WriteUInt64LittleEndian(span[8..], replica.FirstFile);
            BinaryPrimitives.WriteUInt64LittleEndian(span[16..], replica.Ledger.Value);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(body.Reserve(sizeof(uint)), (uint)Tombstones.Count);
        foreach (var tombstone in Tombstones)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(body.Reserve(sizeof(ulong)), tombstone.Value);
        }

        var crc = body.Complete();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[BodyLengthOffset..], (uint)bodyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[BodyCrcOffset..], crc);
        writer.WriteAt(0, header);
        return HeaderLength + bodyLength;
    }

    /// <summary>
    /// Encodes this catalog as the catalog checkpoint of <paramref name="cut"/>.
    /// </summary>
    public byte[] Encode(WalPosition cut)
    {
        using var writer = new ArcBufferWriter();
        var length = Encode(cut, writer);
        using var bytes = writer.ConsumeSlice(length);
        return bytes.ToArray();
    }

    /// <summary>
    /// Decodes a catalog checkpoint: the checkpoint, or the rejection naming the first rule of ledgerstore.md §7.2 its
    /// bytes break, in the order that section lists them.
    /// </summary>
    /// <param name="bytes">The checkpoint's bytes.</param>
    /// <param name="named">The cut the checkpoint's name gives, which its body must restate, or <see langword="null"/>.</param>
    public static CatalogDecoding Decode(ReadOnlySpan<byte> bytes, WalPosition? named = null)
    {
        if (bytes.Length < HeaderLength)
        {
            return Reader.Truncated;
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            return Rejection("bad-magic");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != Version)
        {
            return Rejection("bad-version");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[BodyLengthOffset..]) != (uint)(bytes.Length - HeaderLength))
        {
            return Rejection("length-mismatch");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[BodyCrcOffset..]) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes[HeaderLength..]))
        {
            return Rejection("crc-mismatch");
        }

        var reader = new Reader(bytes[HeaderLength..]);
        if (!reader.TryReadUInt64(out var cutFile) || !reader.TryReadUInt64(out var cutOffset))
        {
            return Reader.Truncated;
        }

        var cut = new WalPosition(cutFile, cutOffset);
        if (cut.File == 0)
        {
            return Rejection("zero-cut-file");
        }

        if (named is { } name && name != cut)
        {
            return Rejection("cut-mismatch");
        }

        if (!reader.TryReadUInt32(out var nextHandle))
        {
            return Reader.Truncated;
        }

        if (nextHandle > LedgerRecords.StoreHandle)
        {
            return Rejection("next-handle-out-of-range");
        }

        if (reader.CheckCount(out var replicaCount) is { } countRejection)
        {
            return countRejection;
        }

        var replicas = new CatalogReplica[replicaCount];
        var ledgers = new HashSet<ulong>();
        for (var index = 0; index < replicas.Length; index++)
        {
            if (!reader.TryReadUInt32(out var handle) || !reader.TryReadUInt32(out var window) || !reader.TryReadUInt64(out var firstFile) || !reader.TryReadUInt64(out var ledger))
            {
                return Reader.Truncated;
            }

            if (index > 0 && handle <= replicas[index - 1].Handle)
            {
                return Rejection("handle-order");
            }

            if (handle >= nextHandle)
            {
                return Rejection("handle-not-below-next");
            }

            if (window is 0 or > int.MaxValue)
            {
                return Rejection("window-out-of-range");
            }

            if (firstFile == 0)
            {
                return Rejection("zero-first-file");
            }

            // A create at or above the cut is not reflected, so no listed replica's first file lies beyond the cut's.
            if (firstFile > cut.File)
            {
                return Rejection("first-file-above-cut");
            }

            if (ledger == 0)
            {
                return Rejection("zero-ledger");
            }

            if (!ledgers.Add(ledger))
            {
                return Rejection("duplicate-ledger");
            }

            replicas[index] = new CatalogReplica(handle, (int)window, firstFile, new LedgerId(ledger));
        }

        if (reader.CheckCount(out var tombstoneCount) is { } tombstoneCountRejection)
        {
            return tombstoneCountRejection;
        }

        var tombstones = ImmutableSortedSet.CreateBuilder<LedgerId>();
        var previous = 0UL;
        for (var index = 0; index < tombstoneCount; index++)
        {
            if (!reader.TryReadUInt64(out var ledger))
            {
                return Reader.Truncated;
            }

            if (ledger == 0)
            {
                return Rejection("zero-ledger");
            }

            if (ledger <= previous)
            {
                return Rejection("tombstone-order");
            }

            if (ledgers.Contains(ledger))
            {
                return Rejection("tombstone-live");
            }

            _ = tombstones.Add(new LedgerId(ledger));
            previous = ledger;
        }

        if (!reader.IsComplete)
        {
            return Rejection("trailing-bytes");
        }

        return new CatalogCheckpoint(cut, new ReplicaCatalog(nextHandle, replicas, tombstones.ToImmutable()));
    }

    /// <summary>
    /// Creates the rejection of a catalog checkpoint which breaks the rule <paramref name="reason"/> names (§7.2), or of
    /// a create or drop record which disagrees with the catalog it applies to (§7.3).
    /// </summary>
    internal static InvalidDataException Rejection(string reason) => DecodeRejection.Create(reason, reason switch
    {
        "truncated" => "The LedgerStore catalog checkpoint ends inside its header or a field.",
        "bad-magic" => "The LedgerStore catalog checkpoint has an invalid magic value.",
        "bad-version" => "The LedgerStore catalog checkpoint has an unsupported version.",
        "length-mismatch" => "The LedgerStore catalog checkpoint's body length does not match its header.",
        "crc-mismatch" => "The LedgerStore catalog checkpoint fails its CRC-32C.",
        "zero-cut-file" => "The LedgerStore catalog checkpoint's cut lies in file 0, which no WAL holds.",
        "cut-mismatch" => "The LedgerStore catalog checkpoint's cut differs from the cut its name gives.",
        "next-handle-out-of-range" => "The LedgerStore catalog checkpoint's next handle exceeds the handle space.",
        "count-overflow" => "The LedgerStore catalog checkpoint holds a count above its remaining bytes.",
        "handle-order" => "The LedgerStore catalog checkpoint's replicas are not in ascending handle order.",
        "handle-not-below-next" => "The LedgerStore catalog checkpoint holds a handle at or above its next handle.",
        "window-out-of-range" => "The LedgerStore catalog checkpoint holds a replica whose window is 0 or above 2^31 - 1.",
        "zero-first-file" => "The LedgerStore catalog checkpoint holds a replica whose first file is 0.",
        "first-file-above-cut" => "The LedgerStore catalog checkpoint holds a replica whose first file lies above its cut's file.",
        "zero-ledger" => "The LedgerStore catalog checkpoint holds a replica or a tombstone of ledger 0.",
        "duplicate-ledger" => "The LedgerStore catalog checkpoint lists a ledger twice.",
        "tombstone-order" => "The LedgerStore catalog checkpoint's tombstones are not in ascending order.",
        "tombstone-live" => "The LedgerStore catalog checkpoint holds a tombstone of a ledger it lists.",
        "trailing-bytes" => "The LedgerStore catalog checkpoint has trailing bytes.",
        "create-handle-mismatch" => "A create record's handles do not run consecutively from the catalog's next handle.",
        "create-ledger-dropped" => "A create record names a ledger the catalog dropped.",
        "create-ledger-listed" => "A create record names a ledger the catalog lists or names it twice.",
        "drop-ledger-dropped" => "A drop record names a ledger the catalog already dropped.",
        _ => throw new UnreachableException(),
    });

    /// <summary>
    /// Buffers a checkpoint body's fields in a pooled chunk, appending each full chunk to the writer and folding it into
    /// the body's CRC-32C, so the body is written once and never held whole.
    /// </summary>
    private sealed class BodyWriter(ArcBufferWriter writer) : IDisposable
    {
        private const int ChunkLength = 4096;
        private readonly Crc32 _crc = new(Crc32ParameterSet.Crc32C);
        private byte[]? _chunk = ArrayPool<byte>.Shared.Rent(ChunkLength);
        private int _length;

        public Span<byte> Reserve(int length)
        {
            if (_length + length > ChunkLength)
            {
                Flush();
            }

            var span = _chunk.AsSpan(_length, length);
            _length += length;
            return span;
        }

        /// <summary>
        /// Appends the buffered fields and returns the body's CRC-32C.
        /// </summary>
        public uint Complete()
        {
            Flush();
            return _crc.GetCurrentHashAsUInt32();
        }

        public void Dispose()
        {
            if (_chunk is { } chunk)
            {
                _chunk = null;
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        private void Flush()
        {
            var bytes = _chunk.AsSpan(0, _length);
            writer.Write(bytes);
            _crc.Append(bytes);
            _length = 0;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _bytes = bytes;

        public readonly bool IsComplete => _bytes.IsEmpty;

        /// <summary>
        /// Gets the rejection of a checkpoint which ends inside a field.
        /// </summary>
        public static InvalidDataException Truncated => Rejection("truncated");

        public bool TryReadUInt32(out uint value)
        {
            value = _bytes.Length >= sizeof(uint) ? BinaryPrimitives.ReadUInt32LittleEndian(_bytes) : 0;
            return Take(sizeof(uint));
        }

        public bool TryReadUInt64(out ulong value)
        {
            value = _bytes.Length >= sizeof(ulong) ? BinaryPrimitives.ReadUInt64LittleEndian(_bytes) : 0;
            return Take(sizeof(ulong));
        }

        /// <summary>
        /// Reads a replica or tombstone count, which may not exceed the remaining bytes, returning the rejection naming
        /// the rule it breaks, or <see langword="null"/>.
        /// </summary>
        public InvalidDataException? CheckCount(out int count)
        {
            count = 0;
            if (!TryReadUInt32(out var value))
            {
                return Truncated;
            }

            if (value > (uint)_bytes.Length)
            {
                return Rejection("count-overflow");
            }

            count = (int)value;
            return null;
        }

        private bool Take(int length)
        {
            if (_bytes.Length < length)
            {
                return false;
            }

            _bytes = _bytes[length..];
            return true;
        }
    }
}

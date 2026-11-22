using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;


namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// What a WAL record is (ledgerstore.md §7.3).
/// </summary>
internal enum RecordClass
{
    /// <summary>A slot record of an assigned handle.</summary>
    Slot,

    /// <summary>A record keyed by the store handle: a create, seal, or drop record, or a damaged one.</summary>
    Store,

    /// <summary>
    /// Damage which preserved the page CRC: a record shorter than its key, a slot record with an empty value, or a slot
    /// record of a handle at or above the next handle.
    /// </summary>
    Damaged,
}

/// <summary>
/// The keys of a LedgerStore's WAL records and their classification (ledgerstore.md §7.3): each record is the 8-byte
/// key <c>(handle &lt;&lt; 40) | slot</c>, little-endian, followed by its body. A slot record's body is the slot's
/// non-empty DATA value; a store record has the store handle, its kind in the slot field, and a create, seal, or drop
/// body.
/// </summary>
internal static class LedgerRecords
{
    /// <summary>
    /// The length of a WAL record's key, which precedes its body.
    /// </summary>
    internal const int KeyLength = sizeof(ulong);

    /// <summary>
    /// The number of bits of a record key which hold the slot; the handle takes the remaining 24.
    /// </summary>
    internal const int SlotBits = 40;

    internal const long SlotLimit = 1L << SlotBits;

    /// <summary>
    /// The store handle, 2<sup>24</sup> − 1, which keys create, seal, and drop records; replica handles lie below it, so it is
    /// also the number of handles a store can assign.
    /// </summary>
    internal const uint StoreHandle = (1u << 24) - 1;

    /// <summary>
    /// Returns the key of a slot record.
    /// </summary>
    internal static ulong Key(uint handle, long slot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(handle, StoreHandle);
        if ((ulong)slot >= SlotLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "A LedgerStore slot lies below 2^40.");
        }

        return ((ulong)handle << SlotBits) | (ulong)slot;
    }

    /// <summary>
    /// Returns a record's key, which precedes its body as the write request's header, so a value is never copied to
    /// prepend it.
    /// </summary>
    internal static ArcBuffer RecordKey(ulong key)
    {
        Span<byte> bytes = stackalloc byte[KeyLength];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, key);
        using var writer = new ArcBufferWriter(KeyLength);
        writer.Write(bytes);
        return writer.ConsumeSlice(KeyLength);
    }

    /// <summary>
    /// Checks that a record read for a DATA entry holds a non-empty value.
    /// </summary>
    internal static bool IsDataRecord(ArcBuffer record) => record.Length > KeyLength;

    /// <summary>
    /// Classifies a record against the catalog's next handle, parsing a slot record into its handle and slot.
    /// </summary>
    internal static RecordClass Classify(ArcBuffer payload, uint nextHandle, out uint handle, out long slot)
    {
        handle = 0;
        slot = 0;
        if (payload.Length < KeyLength)
        {
            return RecordClass.Damaged;
        }

        var key = ReadKey(payload);
        handle = (uint)(key >> SlotBits);
        slot = (long)(key & (SlotLimit - 1));
        if (handle == StoreHandle)
        {
            return RecordClass.Store;
        }

        // A slot record holds DATA, which is never empty (ledgerstore.md §7.3).
        return handle < nextHandle && payload.Length > KeyLength ? RecordClass.Slot : RecordClass.Damaged;
    }

    /// <summary>
    /// Returns the kind a store record's key names in its slot field, which <see cref="DecodeStore"/> checks.
    /// </summary>
    internal static ulong StoreKindOf(ArcBuffer payload) => ReadKey(payload) & (SlotLimit - 1);

    /// <summary>
    /// Decodes a record keyed by the store handle, one <see cref="Classify"/> calls <see cref="RecordClass.Store"/>, into a
    /// create, seal, or drop record, reading its body from the record's own buffer (ledgerstore.md §7.3).
    /// </summary>
    /// <param name="payload">The record: its key, then its body.</param>
    /// <returns>
    /// The record, or the rejection naming the first rule it breaks, with the rule's reason code (conventions.md §15),
    /// which makes it a damaged store record.
    /// </returns>
    /// <exception cref="ArgumentException">The record is not keyed by the store handle.</exception>
    internal static StoreRecordDecoding DecodeStore(ArcBuffer payload)
    {
        if (payload.Length < KeyLength || !StoreRecord.IsStoreKey(ReadKey(payload)))
        {
            throw new ArgumentException("The record is not keyed by the store handle.", nameof(payload));
        }

        return StoreRecord.Decode(ReadKey(payload), payload.AsReadOnlySequence().Slice(KeyLength));
    }

    /// <summary>
    /// Creates the rejection of a store record which breaks the rule <paramref name="reason"/> names.
    /// </summary>
    internal static InvalidDataException Rejection(string reason) => DecodeRejection.Create(reason, reason switch
    {
        "store-kind-unknown" => "The store record's kind is not create, drop, or seal.",
        "create-body-length" => "The create record's body is empty or not a whole number of 16-byte pairs.",
        "drop-body-length" => "The drop record's body is not 8 bytes.",
        "seal-body-length" => "The seal record's body is not 12 bytes.",
        "zero-ledger" => "The store record names ledger 0.",
        "handle-out-of-range" => "The store record names a handle at or above the store handle.",
        "window-out-of-range" => "The create record names a window of 0 or above int.MaxValue.",
        _ => throw new UnreachableException(),
    });

    private static ulong ReadKey(ArcBuffer payload)
    {
        Span<byte> bytes = stackalloc byte[KeyLength];
        _ = payload.UnsafeSlice(0, KeyLength).CopyTo(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}

/// <summary>
/// The kind of a store record, which its key's slot field holds under the store handle (ledgerstore.md §7.3).
/// </summary>
internal enum StoreRecordKind
{
    /// <summary>A create: one batch of creations.</summary>
    Create = 1,

    /// <summary>A drop of one ledger, which leaves its tombstone.</summary>
    Drop = 2,

    /// <summary>A seal of one live replica (LS-I11).</summary>
    Seal = 3,
}

/// <summary>
/// One 16-byte pair of a create record's body: a ledger ID, the handle its replica takes, and the window its replica
/// records, which bounds its unwritten addresses after a restart (LR-I6).
/// </summary>
internal readonly record struct CatalogPair(LedgerId Ledger, uint Handle, uint Window);

/// <summary>
/// A create, seal, or drop record of a LedgerStore's WAL, keyed by the store handle (ledgerstore.md §7.3).
/// </summary>
internal abstract record StoreRecord
{
    internal const int PairLength = sizeof(ulong) + sizeof(uint) + sizeof(uint);

    /// <summary>
    /// Gets the record's kind.
    /// </summary>
    public abstract StoreRecordKind Kind { get; }

    /// <summary>
    /// Gets the record's key: the store handle with the record's kind in the slot field.
    /// </summary>
    public ulong Key => KeyOf(Kind);

    /// <summary>
    /// Returns whether <paramref name="key"/> keys a store record: its handle is the store handle.
    /// </summary>
    public static bool IsStoreKey(ulong key) => key >> LedgerRecords.SlotBits == LedgerRecords.StoreHandle;

    public static ulong KeyOf(StoreRecordKind kind) => ((ulong)LedgerRecords.StoreHandle << LedgerRecords.SlotBits) | (ulong)kind;

    /// <summary>
    /// Gets the length of the record's body, which follows its key.
    /// </summary>
    public abstract int BodyLength { get; }

    /// <summary>
    /// Encodes the record's body, which follows its key, into <paramref name="writer"/>: the record's request's writer,
    /// so a create record of any number of creations crosses as pages (conventions.md §1).
    /// </summary>
    public abstract void Encode(ArcBufferWriter writer);

    /// <summary>
    /// Decodes a store record's body under its key (ledgerstore.md §7.3).
    /// </summary>
    /// <param name="key">The record's key, which <see cref="IsStoreKey"/> accepts.</param>
    /// <param name="body">The bytes after the key.</param>
    /// <returns>The record, or the rejection naming the first rule it breaks.</returns>
    public static StoreRecordDecoding Decode(ulong key, ReadOnlySequence<byte> body) => (key & (LedgerRecords.SlotLimit - 1)) switch
    {
        (ulong)StoreRecordKind.Create => CreateRecord.Decode(body),
        (ulong)StoreRecordKind.Drop => DropRecord.Decode(body),
        (ulong)StoreRecordKind.Seal => SealRecord.Decode(body),
        _ => LedgerRecords.Rejection("store-kind-unknown"),
    };

    /// <summary>
    /// Reads a little-endian 64-bit field from a body whose length was checked.
    /// </summary>
    private protected static ulong ReadUInt64(ref SequenceReader<byte> reader)
    {
        var read = reader.TryReadLittleEndian(out long value);
        Debug.Assert(read, "The body's length was checked.");
        return unchecked((ulong)value);
    }

    /// <summary>
    /// Reads a little-endian 32-bit field from a body whose length was checked.
    /// </summary>
    private protected static uint ReadUInt32(ref SequenceReader<byte> reader)
    {
        var read = reader.TryReadLittleEndian(out int value);
        Debug.Assert(read, "The body's length was checked.");
        return unchecked((uint)value);
    }
}

/// <summary>
/// A create record: one batch of creations, whose handles run consecutively from the catalog's next handle
/// (ledgerstore.md §7.3, §8.5).
/// </summary>
internal sealed record CreateRecord : StoreRecord
{
    public CreateRecord(IReadOnlyList<CatalogPair> pairs)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pairs.Count);
        Pairs = [.. pairs];
    }

    public override StoreRecordKind Kind => StoreRecordKind.Create;

    /// <summary>
    /// Gets the record's pairs, one per created replica.
    /// </summary>
    public CatalogPair[] Pairs { get; }

    public override int BodyLength => checked(Pairs.Length * PairLength);

    public override void Encode(ArcBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> bytes = stackalloc byte[PairLength];
        foreach (var pair in Pairs)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, pair.Ledger.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[sizeof(ulong)..], pair.Handle);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(sizeof(ulong) + sizeof(uint))..], pair.Window);
            writer.Write(bytes);
        }
    }

    public bool Equals(CreateRecord? other) => other is not null && Pairs.AsSpan().SequenceEqual(other.Pairs);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pair in Pairs)
        {
            hash.Add(pair);
        }

        return hash.ToHashCode();
    }

    internal static StoreRecordDecoding Decode(ReadOnlySequence<byte> body)
    {
        if (body.IsEmpty || body.Length % PairLength != 0)
        {
            return LedgerRecords.Rejection("create-body-length");
        }

        var pairs = new CatalogPair[body.Length / PairLength];
        var reader = new SequenceReader<byte>(body);
        for (var index = 0; index < pairs.Length; index++)
        {
            var ledger = ReadUInt64(ref reader);
            var handle = ReadUInt32(ref reader);
            var window = ReadUInt32(ref reader);
            if (ledger == 0)
            {
                return LedgerRecords.Rejection("zero-ledger");
            }

            if (handle >= LedgerRecords.StoreHandle)
            {
                return LedgerRecords.Rejection("handle-out-of-range");
            }

            if (window is 0 or > int.MaxValue)
            {
                return LedgerRecords.Rejection("window-out-of-range");
            }

            pairs[index] = new CatalogPair(new LedgerId(ledger), handle, window);
        }

        return new CreateRecord(pairs);
    }
}

/// <summary>
/// A drop record: it removes its ledger's replica, if live, and records the ledger's tombstone (ledgerstore.md §7.3,
/// §8.6).
/// </summary>
internal sealed record DropRecord(LedgerId Ledger) : StoreRecord
{
    internal const int EncodedBodyLength = sizeof(ulong);

    public override StoreRecordKind Kind => StoreRecordKind.Drop;

    public override int BodyLength => EncodedBodyLength;

    public override void Encode(ArcBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> body = stackalloc byte[EncodedBodyLength];
        BinaryPrimitives.WriteUInt64LittleEndian(body, Ledger.Value);
        writer.Write(body);
    }

    internal static StoreRecordDecoding Decode(ReadOnlySequence<byte> body)
    {
        if (body.Length != EncodedBodyLength)
        {
            return LedgerRecords.Rejection("drop-body-length");
        }

        var reader = new SequenceReader<byte>(body);
        var ledger = ReadUInt64(ref reader);
        if (ledger == 0)
        {
            return LedgerRecords.Rejection("zero-ledger");
        }

        return new DropRecord(new LedgerId(ledger));
    }
}

/// <summary>
/// A seal record: it seals the live replica its ledger and handle name (ledgerstore.md §7.3, §8.3; LS-I11).
/// </summary>
internal sealed record SealRecord(LedgerId Ledger, uint Handle) : StoreRecord
{
    internal const int EncodedBodyLength = sizeof(ulong) + sizeof(uint);

    public override StoreRecordKind Kind => StoreRecordKind.Seal;

    public override int BodyLength => EncodedBodyLength;

    public override void Encode(ArcBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> body = stackalloc byte[EncodedBodyLength];
        BinaryPrimitives.WriteUInt64LittleEndian(body, Ledger.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(body[sizeof(ulong)..], Handle);
        writer.Write(body);
    }

    internal static StoreRecordDecoding Decode(ReadOnlySequence<byte> body)
    {
        if (body.Length != EncodedBodyLength)
        {
            return LedgerRecords.Rejection("seal-body-length");
        }

        var reader = new SequenceReader<byte>(body);
        var ledger = ReadUInt64(ref reader);
        var handle = ReadUInt32(ref reader);
        if (ledger == 0)
        {
            return LedgerRecords.Rejection("zero-ledger");
        }

        if (handle >= LedgerRecords.StoreHandle)
        {
            return LedgerRecords.Rejection("handle-out-of-range");
        }

        return new SealRecord(new LedgerId(ledger), handle);
    }
}

/// <summary>
/// What decoding a store record gives (conventions.md §15): the create, seal, or drop record, or the
/// <see cref="InvalidDataException"/> naming the first rule it breaks, with the rule's reason code (ledgerstore.md §7.3).
/// </summary>
internal readonly union StoreRecordDecoding(StoreRecord, InvalidDataException);

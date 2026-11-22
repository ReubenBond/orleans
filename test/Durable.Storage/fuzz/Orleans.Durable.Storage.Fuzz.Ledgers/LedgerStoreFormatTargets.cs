using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Numerics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// The LedgerStore catalog checkpoint, <c>CLSC</c> version 1 (ledgerstore.md §7.2): a canonical format, rejected with
/// one of the shared catalog vectors' reasons or accepted as the catalog and cut which encode to exactly its bytes.
/// </summary>
public sealed class ReplicaCatalogTarget : FuzzTarget
{
    /// <summary>
    /// The reasons of <c>tests/shared-vectors/ledgerstore/catalog-rejections.json</c>.
    /// </summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("ledgerstore", "catalog-rejections.json"));

    public override string Name => "ledgerstore-catalog";

    public override int MaxLength => 1024;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "catalog.json", "catalog-rejections.json" }
            .SelectMany(file => vectors.Cases("ledgerstore", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var bound = MeasureAllocations(input.Length);
        var decoding = ReplicaCatalog.Decode(input);
        bound.Check("ReplicaCatalog.Decode");
        var expected = CatalogRule(input);
        if (decoding is InvalidDataException rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason is not null && Reasons.Value.Contains(reason), $"rejected a catalog checkpoint for '{reason}', which the catalog vectors do not name.");
            Require(reason == expected, $"rejected a catalog checkpoint for '{reason}' where the format's rules give '{expected ?? "valid"}'.");
            return FuzzOutcome.Rejected;
        }

        Require(expected is null, $"accepted a catalog checkpoint which breaks '{expected}'.");
        var checkpoint = Assert(decoding);
        Require(input.SequenceEqual(checkpoint.Catalog.Encode(checkpoint.Cut)), "accepted a catalog checkpoint which does not encode to its bytes.");
        return FuzzOutcome.Accepted;
    }

    private static CatalogCheckpoint Assert(CatalogDecoding decoding) =>
        decoding.Value as CatalogCheckpoint ?? throw new FuzzFindingException("ledgerstore-catalog: a decoding held neither a checkpoint nor a rejection.");

    /// <summary>
    /// Returns the first rule of ledgerstore.md §7.2 a catalog checkpoint read without a name breaks, by its reason code,
    /// in the order that section lists them, or <see langword="null"/>: a 16-byte header with the magic, version 1, the
    /// body's length and CRC-32C; then a cut outside file 0, a next handle within the handle space, the replicas, each
    /// above the last, below the next handle, of a window from 1 to <c>int.MaxValue</c>, a first file from 1 to the cut's
    /// file, and a non-zero ledger not listed before, and the tombstones, each non-zero, ascending, and not listed; every
    /// field whole, each count within the bytes left, and no byte after.
    /// </summary>
    private static string? CatalogRule(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
        {
            return "truncated";
        }

        if (!bytes[..4].SequenceEqual("CLSC"u8))
        {
            return "bad-magic";
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 1)
        {
            return "bad-version";
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != (uint)(bytes.Length - 16))
        {
            return "length-mismatch";
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes[16..]))
        {
            return "crc-mismatch";
        }

        var body = bytes[16..];
        if (body.Length < 20)
        {
            return "truncated";
        }

        var cutFile = BinaryPrimitives.ReadUInt64LittleEndian(body);
        var nextHandle = BinaryPrimitives.ReadUInt32LittleEndian(body[16..]);
        if (cutFile == 0)
        {
            return "zero-cut-file";
        }

        if (nextHandle > LedgerRecords.StoreHandle)
        {
            return "next-handle-out-of-range";
        }

        body = body[20..];
        if (Count(ref body, out var replicaCount) is { } replicaCountRule)
        {
            return replicaCountRule;
        }

        var ledgers = new HashSet<ulong>();
        var previousHandle = -1L;
        for (var index = 0; index < replicaCount; index++)
        {
            if (body.Length < 24)
            {
                return "truncated";
            }

            var handle = BinaryPrimitives.ReadUInt32LittleEndian(body);
            var window = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
            var firstFile = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
            var ledger = BinaryPrimitives.ReadUInt64LittleEndian(body[16..]);
            body = body[24..];
            if (ReplicaRule(handle, previousHandle, nextHandle, window, firstFile, cutFile, ledger, ledgers) is { } broken)
            {
                return broken;
            }

            previousHandle = handle;
        }

        if (Count(ref body, out var tombstoneCount) is { } tombstoneCountRule)
        {
            return tombstoneCountRule;
        }

        var previous = 0UL;
        for (var index = 0; index < tombstoneCount; index++)
        {
            if (body.Length < 8)
            {
                return "truncated";
            }

            var ledger = BinaryPrimitives.ReadUInt64LittleEndian(body);
            body = body[8..];
            if (TombstoneRule(ledger, previous, ledgers) is { } broken)
            {
                return broken;
            }

            previous = ledger;
        }

        return body.IsEmpty ? null : "trailing-bytes";

        static string? ReplicaRule(uint handle, long previousHandle, uint nextHandle, uint window, ulong firstFile, ulong cutFile, ulong ledger, HashSet<ulong> ledgers)
        {
            if (handle <= previousHandle)
            {
                return "handle-order";
            }

            if (handle >= nextHandle)
            {
                return "handle-not-below-next";
            }

            if (window is 0 or > int.MaxValue)
            {
                return "window-out-of-range";
            }

            if (firstFile == 0)
            {
                return "zero-first-file";
            }

            if (firstFile > cutFile)
            {
                return "first-file-above-cut";
            }

            if (ledger == 0)
            {
                return "zero-ledger";
            }

            return ledgers.Add(ledger) ? null : "duplicate-ledger";
        }

        static string? TombstoneRule(ulong ledger, ulong previous, HashSet<ulong> ledgers)
        {
            if (ledger == 0)
            {
                return "zero-ledger";
            }

            if (ledger <= previous)
            {
                return "tombstone-order";
            }

            return ledgers.Contains(ledger) ? "tombstone-live" : null;
        }

        static string? Count(ref ReadOnlySpan<byte> body, out long count)
        {
            count = 0;
            if (body.Length < 4)
            {
                return "truncated";
            }

            count = BinaryPrimitives.ReadUInt32LittleEndian(body);
            body = body[4..];
            return count > body.Length ? "count-overflow" : null;
        }
    }

    /// <summary>
    /// Writes the body's length and CRC-32C into the header.
    /// </summary>
    protected override byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (input.Length < ReplicaCatalog.HeaderLength)
        {
            return null;
        }

        var bytes = input.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ReplicaCatalog.BodyLengthOffset), (uint)(bytes.Length - ReplicaCatalog.HeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ReplicaCatalog.BodyCrcOffset), Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes.AsSpan(ReplicaCatalog.HeaderLength)));
        return bytes;
    }
}

/// <summary>
/// The LedgerStore WAL record (ledgerstore.md §7.3), classified against the catalog's next handle: damage, when
/// shorter than its key or a slot record of a handle the catalog never assigned; a store record, accepted as the create
/// or drop record which lays out exactly its bytes or rejected with one of the shared record vectors' reasons; or the
/// slot record's key and value which lay out exactly its bytes. The input is the next handle (<c>u32</c>, at most
/// 2<sup>24</sup> − 1) followed by the record.
/// </summary>
public sealed class StoreRecordTarget : FuzzTarget
{
    /// <summary>
    /// The store record reasons of <c>tests/shared-vectors/ledgerstore/record-rejections.json</c>: those of its vectors
    /// keyed by the store handle.
    /// </summary>
    private static readonly Lazy<HashSet<string>> StoreReasons = new(static () =>
    [
        .. GoldenVectors.Find().Cases("ledgerstore", "record-rejections.json")
            .Where(static vector => GoldenVectors.Hex(vector) is { Length: >= LedgerRecords.KeyLength } bytes
                && BinaryPrimitives.ReadUInt64LittleEndian(bytes) >> LedgerRecords.SlotBits == LedgerRecords.StoreHandle)
            .Select(static vector => GoldenVectors.Text(vector, "reason")),
    ]);

    public override string Name => "ledgerstore-record";

    public override int MaxLength => 256;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return SeedsOf(vectors);
    }

    private static IEnumerable<byte[]> SeedsOf(GoldenVectors vectors)
    {
        foreach (var file in new[] { "records.json", "record-rejections.json" })
        {
            foreach (var vector in vectors.Cases("ledgerstore", file))
            {
                var input = new byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32LittleEndian(input, vector.GetProperty("nextHandle").GetUInt32());
                yield return [.. input, .. GoldenVectors.Hex(vector)];
            }
        }
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        if (input.Length < sizeof(uint))
        {
            return FuzzOutcome.None;
        }

        var nextHandle = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(input), LedgerRecords.StoreHandle);
        var record = input[sizeof(uint)..];

        // A record is a slice of the page a scan read, so an empty one is an empty slice of a buffer.
        using var page = ArcBuffer.Wrap(input.ToArray());
        using var payload = page.Slice(sizeof(uint));
        var bound = MeasureAllocations(record.Length);
        var recordClass = LedgerRecords.Classify(payload, nextHandle, out var handle, out var slot);
        bound.Check("LedgerStore.Classify");
        switch (recordClass)
        {
            case RecordClass.Damaged:
                Require(
                    record.Length <= LedgerRecords.KeyLength || BinaryPrimitives.ReadUInt64LittleEndian(record) >> LedgerRecords.SlotBits >= nextHandle,
                    "rejected a record which holds its key, a non-empty value, and an assigned handle.");
                return FuzzOutcome.Rejected;
            case RecordClass.Store:
                Require(handle == LedgerRecords.StoreHandle, "classified a record of another handle as a store record.");
                var expected = StoreRule(BinaryPrimitives.ReadUInt64LittleEndian(record), record[LedgerRecords.KeyLength..]);
                var decoding = LedgerRecords.DecodeStore(payload);
                Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
                if (decoding.Value is InvalidDataException rejection)
                {
                    var reason = DecodeRejection.ReasonOf(rejection);
                    Require(reason is not null && StoreReasons.Value.Contains(reason), $"rejected a store record for '{reason}', which the record vectors do not name.");
                    Require(reason == expected, $"rejected a store record for '{reason}' where the format's rules give '{expected ?? "valid"}'.");
                    return FuzzOutcome.Rejected;
                }

                Require(expected is null, $"accepted a store record which breaks '{expected}'.");
                var store = (StoreRecord)decoding.Value;
                using (var key = LedgerRecords.RecordKey(store.Key))
                using (var writer = new ArcBufferWriter(store.BodyLength))
                {
                    store.Encode(writer);
                    using var body = writer.ConsumeSlice(writer.Length);
                    Require(record.SequenceEqual([.. key.ToArray(), .. body.ToArray()]), "decoded a store record which does not lay out its bytes.");
                }

                return FuzzOutcome.Accepted;
            case RecordClass.Slot:
                Require(handle < nextHandle, "accepted a record of a handle the catalog never assigned.");
                Require(LedgerRecords.IsDataRecord(payload), "accepted a slot record with an empty value, which no DATA is.");
                using (var key = LedgerRecords.RecordKey(LedgerRecords.Key(handle, slot)))
                {
                    Require(record.SequenceEqual([.. key.ToArray(), .. record[LedgerRecords.KeyLength..]]), "parsed a record whose key does not lay out its bytes.");
                }

                return FuzzOutcome.Accepted;
            default:
                throw new InvalidOperationException($"Unknown record class {recordClass}.");
        }
    }

    /// <summary>
    /// Returns the first rule of ledgerstore.md §7.3 a store record breaks, by its reason code, or <see langword="null"/>:
    /// a known kind; a body of whole 16-byte pairs, at least one, for a create, of 8 bytes for a drop, and of 12 for a
    /// seal; then, in order, each pair's or the body's non-zero ledger ID, a handle below the store handle, and a window
    /// from 1 to <c>int.MaxValue</c>.
    /// </summary>
    private static string? StoreRule(ulong key, ReadOnlySpan<byte> body)
    {
        const int PairLength = 16;
        switch (key & (LedgerRecords.SlotLimit - 1))
        {
            case 1:
                if (body.IsEmpty || body.Length % PairLength != 0)
                {
                    return "create-body-length";
                }

                for (; !body.IsEmpty; body = body[PairLength..])
                {
                    if (PairRule(body, window: true) is { } broken)
                    {
                        return broken;
                    }
                }

                return null;
            case 2:
                if (body.Length != 8)
                {
                    return "drop-body-length";
                }

                return BinaryPrimitives.ReadUInt64LittleEndian(body) == 0 ? "zero-ledger" : null;
            case 3:
                return body.Length != 12 ? "seal-body-length" : PairRule(body, window: false);
            default:
                return "store-kind-unknown";
        }

        static string? PairRule(ReadOnlySpan<byte> fields, bool window)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(fields) == 0)
            {
                return "zero-ledger";
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]) >= LedgerRecords.StoreHandle)
            {
                return "handle-out-of-range";
            }

            return window && BinaryPrimitives.ReadUInt32LittleEndian(fields[12..]) is 0 or > int.MaxValue ? "window-out-of-range" : null;
        }
    }
}

/// <summary>
/// A slot-index checkpoint leaf, <c>CLSI</c> version 1 (ledgerstore.md §7.4): rejected with the reason of the first rule
/// it breaks, one of the shared leaf vectors' reasons, or accepted. The decoder validates structurally and accepts
/// alternate encodings of the same entries, so the oracle for an accepted leaf is that the canonical encoder preserves
/// the decoded entries, and that random access and the full decoding agree.
/// </summary>
public sealed class SlotLeafTarget : FuzzTarget
{
    /// <summary>
    /// The reasons of <c>tests/shared-vectors/ledgerstore/leaf-rejections.json</c>.
    /// </summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("ledgerstore", "leaf-rejections.json"));

    public override string Name => "ledgerstore-leaf";

    public override int MaxLength => 16 * 1024;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "leaves.json", "leaf-rejections.json" }
            .SelectMany(file => vectors.Cases("ledgerstore", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var bytes = input.ToArray();
        var entries = new SlotEntry[SlotLeafCodec.Slots];
        var bound = MeasureAllocations(bytes.Length);
        var decoding = FrozenLeaf.Decode(bytes);
        if (decoding is FrozenLeaf decoded)
        {
            decoded.DecodeAll(entries);
        }

        bound.Check("FrozenLeaf.Decode");
        var expected = LeafRule(bytes);
        if (decoding is InvalidDataException rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason is not null && Reasons.Value.Contains(reason), $"rejected a leaf for '{reason}', which the leaf vectors do not name.");
            Require(reason == expected, $"rejected a leaf for '{reason}' where the format's rules give '{expected ?? "valid"}'.");
            return FuzzOutcome.Rejected;
        }

        Require(expected is null, $"accepted a leaf which breaks '{expected}'.");
        var leaf = Assert(decoding);
        Require(entries.Count(static entry => entry.HasRecord) == leaf.Count, "counts its present entries wrongly.");
        for (var index = 0; index < SlotLeafCodec.Slots; index++)
        {
            Require(leaf.Get(index) == entries[index], string.Create(CultureInfo.InvariantCulture, $"decodes entry {index} differently by random access."));
        }

        var canonical = new SlotEntry[SlotLeafCodec.Slots];
        Assert(FrozenLeaf.Decode(SlotLeafCodec.Encode(entries))).DecodeAll(canonical);
        Require(entries.AsSpan().SequenceEqual(canonical), "accepted entries which the canonical encoding does not preserve.");
        return FuzzOutcome.Accepted;
    }

    private static FrozenLeaf Assert(SlotLeafDecoding decoding) =>
        decoding.Value as FrozenLeaf ?? throw new FuzzFindingException($"ledgerstore-leaf: a decoding held no leaf: {(decoding.Value as Exception)?.Message}");

    /// <summary>
    /// Returns the first rule of ledgerstore.md §7.4 a leaf breaks, by its reason code, in the order that section lists
    /// them, or <see langword="null"/>: a 24-byte header with no reserved flag, widths of at most 1, 8, and 63 bits, and
    /// 1 to 256 files; the file table and presence bitmap whole; files and bases strictly ascending; a bitmap neither
    /// empty nor full; no more exceptions than present slots; exactly the computed length; each exception, in turn, above
    /// the last, for a present slot, and over a zero residual; then each present entry, in slot order, naming a file in
    /// the table and a page within 32 bits; and zero padding after each packed array.
    /// </summary>
    private static string? LeafRule(ReadOnlySpan<byte> bytes)
    {
        const int Header = 24;
        const int FileEntry = 16;
        const int Bitmap = 32;
        const int Exception = 9;
        if (bytes.Length < Header)
        {
            return "truncated";
        }

        var flags = bytes[0];
        int stateWidth = bytes[1];
        int fileIndexWidth = bytes[2];
        int residualWidth = bytes[3];
        int fileCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int exceptionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var intercept = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        var slope = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
        if ((flags & ~1) != 0)
        {
            return "reserved-flags";
        }

        if (stateWidth > 1 || fileIndexWidth > 8 || residualWidth > 63)
        {
            return "width-out-of-range";
        }

        if (fileCount is 0 or > 256)
        {
            return "file-count-out-of-range";
        }

        var allPresent = flags == 1;
        var tableEnd = Header + FileEntry * fileCount;
        if (bytes.Length < tableEnd + (allPresent ? 0 : Bitmap))
        {
            return "truncated";
        }

        var files = new ulong[fileCount];
        var bases = new ulong[fileCount];
        for (var g = 0; g < fileCount; g++)
        {
            files[g] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(Header + FileEntry * g)..]);
            bases[g] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(Header + FileEntry * g + 8)..]);
            if (g > 0 && !(files[g] > files[g - 1] && bases[g] > bases[g - 1]))
            {
                return "file-table-order";
            }
        }

        // The present slots in slot order, and each one's rank among them.
        var slots = new List<int>();
        var ranks = new int[256];
        for (var index = 0; index < 256; index++)
        {
            ranks[index] = -1;
            if (allPresent || ((bytes[tableEnd + index / 8] >> (index % 8)) & 1) != 0)
            {
                ranks[index] = slots.Count;
                slots.Add(index);
            }
        }

        if (!allPresent && slots.Count is 0 or 256)
        {
            return "bitmap-empty-or-full";
        }

        var n = slots.Count;
        if (exceptionCount > n)
        {
            return "too-many-exceptions";
        }

        var statesStart = tableEnd + (allPresent ? 0 : Bitmap);
        var fileIndexesStart = statesStart + (n * stateWidth + 7) / 8;
        var residualsStart = fileIndexesStart + (n * fileIndexWidth + 7) / 8;
        var exceptionsStart = residualsStart + (n * residualWidth + 7) / 8;
        if (bytes.Length != exceptionsStart + Exception * exceptionCount)
        {
            return "length-mismatch";
        }

        var residuals = bytes[residualsStart..exceptionsStart];
        var exceptions = new Dictionary<int, ulong>();
        var previous = -1;
        for (var k = 0; k < exceptionCount; k++)
        {
            var exception = bytes.Slice(exceptionsStart + Exception * k, Exception);
            int index = exception[0];
            if (index <= previous)
            {
                return "exception-order";
            }

            if (ranks[index] < 0)
            {
                return "exception-absent-slot";
            }

            if (SlotLeafCodec.Unpack(residuals, ranks[index], residualWidth) != 0)
            {
                return "exception-residual";
            }

            exceptions[index] = BinaryPrimitives.ReadUInt64LittleEndian(exception[1..]);
            previous = index;
        }

        for (var j = 0; j < n; j++)
        {
            var fileIndex = SlotLeafCodec.Unpack(bytes[fileIndexesStart..residualsStart], j, fileIndexWidth);
            if (fileIndex >= (ulong)fileCount)
            {
                return "file-index-out-of-range";
            }

            Int128 virtualPage = exceptions.TryGetValue(slots[j], out var stored)
                ? stored
                : intercept + SlotLeafCodec.Predict(slots[j], slope) + SlotLeafCodec.Unpack(residuals, j, residualWidth);
            var page = virtualPage - bases[(int)fileIndex];
            if (page < 0 || page > uint.MaxValue)
            {
                return "page-out-of-range";
            }
        }

        var padded = PaddingIsZero(bytes[statesStart..fileIndexesStart], n * stateWidth)
            && PaddingIsZero(bytes[fileIndexesStart..residualsStart], n * fileIndexWidth)
            && PaddingIsZero(residuals, n * residualWidth);
        return padded ? null : "nonzero-padding";

        static bool PaddingIsZero(ReadOnlySpan<byte> packed, int bits) => bits % 8 == 0 || packed[^1] >> (bits % 8) == 0;
    }
}

/// <summary>
/// One root slot of a slot-index checkpoint, <c>CLSI</c> version 1 (ledgerstore.md §7.4): unusable, with the reason of
/// the first rule it breaks, one of the shared root vectors' reasons, or the root which writes exactly its bytes. The
/// input is the slot, zero-filled to the smallest slot size a store uses which holds it.
/// </summary>
public sealed class CheckpointRootTarget : FuzzTarget
{
    /// <summary>
    /// The reasons of <c>tests/shared-vectors/ledgerstore/root-rejections.json</c>.
    /// </summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("ledgerstore", "root-rejections.json"));

    public override string Name => "ledgerstore-root";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "roots.json", "root-rejections.json" }
            .SelectMany(file => vectors.Cases("ledgerstore", file))
            .Select(static vector => GoldenVectors.Expand(GoldenVectors.Hex(vector), vector.GetProperty("slotSize").GetInt32()));
    }

    /// <summary>
    /// Returns the slot an input stands for: its bytes, zero-filled to the smallest slot size
    /// (<c>S = max(4096, P)</c> for a page size P) which holds them, or its first 64 KiB.
    /// </summary>
    private static byte[] ToSlot(ReadOnlySpan<byte> input)
    {
        var pageSize = (int)Math.Clamp(BitOperations.RoundUpToPowerOf2((uint)Math.Max(input.Length, 1)), WalOptions.MinimumPageSize, WalOptions.MaximumPageSize);
        var slot = new byte[Math.Max(CheckpointRoot.MinimumSlotSize, pageSize)];
        input[..Math.Min(input.Length, slot.Length)].CopyTo(slot);
        return slot;
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var slot = ToSlot(input);
        var bound = MeasureAllocations(slot.Length);
        var decoding = CheckpointRoot.Decode(slot);
        bound.Check("CheckpointRoot.Decode");
        var expected = RootRule(slot);
        if (decoding is InvalidDataException rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason is not null && Reasons.Value.Contains(reason), $"found a slot unusable for '{reason}', which the root vectors do not name.");
            Require(reason == expected, $"found a slot unusable for '{reason}' where the format's rules give '{expected ?? "usable"}'.");
            return FuzzOutcome.Rejected;
        }

        Require(expected is null, $"found a slot usable which breaks '{expected}'.");
        var root = decoding.Value as CheckpointRoot ?? throw new FuzzFindingException("ledgerstore-root: a decoding held neither a root nor a rejection.");
        var encoded = new byte[slot.Length];
        root.Write(encoded);
        Require(slot.AsSpan().SequenceEqual(encoded), "accepted a root slot which its root does not write.");
        return FuzzOutcome.Accepted;
    }

    /// <summary>
    /// Writes the CRC-32C of bytes 4 to S − 1.
    /// </summary>
    protected override byte[]? Seal(ReadOnlySpan<byte> input)
    {
        var slot = ToSlot(input);
        BinaryPrimitives.WriteUInt32LittleEndian(slot, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, slot.AsSpan(CheckpointRoot.MagicOffset)));
        return slot;
    }

    /// <summary>
    /// Returns the first rule of ledgerstore.md §7.4 a root slot of at least 4,096 bytes breaks, by its reason code, in
    /// the order that section lists them, or <see langword="null"/>: the CRC-32C of bytes 4 to S − 1, the magic, version
    /// 1, and zero bytes from 80; a root reference which is all zeros when its generation is 0; a height of at most 7,
    /// and 0 exactly when the reference is null; a non-zero generation, which with the reference's lies below
    /// 2<sup>63</sup>; and only flag bits 0 and 1, with the corruption bound's fields zero unless bit 0 is set.
    /// </summary>
    private static string? RootRule(ReadOnlySpan<byte> slot)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(slot) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, slot[4..]))
        {
            return "crc-mismatch";
        }

        if (!slot[4..8].SequenceEqual("CLSI"u8))
        {
            return "bad-magic";
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(slot[8..]) != 1)
        {
            return "bad-version";
        }

        if (slot[80..].ContainsAnyExcept((byte)0))
        {
            return "nonzero-padding";
        }

        var reference = slot.Slice(56, 24);
        var referenceGeneration = BinaryPrimitives.ReadUInt64LittleEndian(reference);
        if (referenceGeneration == 0 && reference.ContainsAnyExcept((byte)0))
        {
            return "null-reference-fields";
        }

        var height = BinaryPrimitives.ReadUInt32LittleEndian(slot[12..]);
        if (height > 7)
        {
            return "height-out-of-range";
        }

        if (height == 0 != (referenceGeneration == 0))
        {
            return "height-root-mismatch";
        }

        var generation = BinaryPrimitives.ReadUInt64LittleEndian(slot[16..]);
        if (generation == 0)
        {
            return "generation-zero";
        }

        if (generation >= 1UL << 63 || referenceGeneration >= 1UL << 63)
        {
            return "generation-out-of-range";
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(slot[52..]);
        if ((flags & ~3u) != 0)
        {
            return "unknown-flags";
        }

        return (flags & 1) == 0 && slot[40..52].ContainsAnyExcept((byte)0) ? "corruption-end-without-flag" : null;
    }
}

using System.Buffers.Binary;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the LedgerStore's WAL record (ledgerstore.md §7.3): the 8-byte little-endian key <c>(handle &lt;&lt; 40) |
/// slot</c> followed by the body. A slot record's handle lies below the store handle, 2^24 - 1, and its body is the
/// non-empty DATA value; a store record's key holds the store handle and its kind, and its body is a create record's
/// pairs, each with its replica's window, a drop record's ledger ID, or a seal record's ledger ID and handle (LS-I11).
/// A record shorter than its key, a slot record with an empty value, a slot record of a handle at or above the catalog's
/// next handle, or a store record which breaks a rule of its format, is damage.
/// </summary>
public sealed class LedgerStoreRecordTests : IDisposable
{
    /// <summary>
    /// Every reason a store record is damaged for.
    /// </summary>
    internal static readonly string[] StoreReasons =
    [
        "store-kind-unknown",
        "create-body-length",
        "drop-body-length",
        "seal-body-length",
        "zero-ledger",
        "handle-out-of-range",
        "window-out-of-range",
    ];

    private static readonly Gen<uint> Handles = Gen.Frequency((4, Gen.UInt[0, 16]), (1, Gen.UInt[0, LedgerRecords.StoreHandle - 1]));

    private static readonly Gen<long> Slots = Gen.Frequency((4, Gen.Long[0, 1_024]), (1, Gen.Long[0, LedgerRecords.SlotLimit - 1]));

    private static readonly Gen<LedgerId> Ledgers =
        Gen.Frequency((4, Gen.ULong[1, 64]), (1, Gen.ULong[1, ulong.MaxValue])).Select(static value => new LedgerId(value));

    private static readonly Gen<uint> Windows = Gen.Frequency((4, Gen.UInt[1, 64]), (1, Gen.UInt[1, int.MaxValue]));

    private static readonly Gen<StoreRecord> AnyStoreRecord = Gen.OneOf(
        Gen.Select(Gen.Select(Ledgers, Windows).Array[1, 8], Handles).Select(static (replicas, first) =>
            (StoreRecord)new CreateRecord(
            [
                .. replicas.Select((replica, index) =>
                    new CatalogPair(replica.Item1, Math.Min(first + (uint)index, LedgerRecords.StoreHandle - 1), replica.Item2)),
            ])),
        Ledgers.Select(static ledger => (StoreRecord)new DropRecord(ledger)),
        Gen.Select(Ledgers, Handles).Select(static (ledger, handle) => (StoreRecord)new SealRecord(ledger, handle)));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void ParsingInvertsTheSlotRecordLayoutForAnyHandleSlotAndValue() =>
        Gen.Select(Handles, Slots, Gen.Byte.Array[1, 40], Gen.UInt[1, 64]).Sample((handle, slot, value, spare) => _leaks.InScope(() =>
        {
            var key = LedgerRecords.Key(handle, slot);
            using var header = LedgerRecords.RecordKey(key);
            var bytes = (byte[])[.. header.ToArray(), .. value];
            Assert.Equal(LedgerRecords.KeyLength, header.Length);
            Assert.Equal(key, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            Assert.False(StoreRecord.IsStoreKey(key));

            using var record = ArcBuffer.Wrap(bytes);
            var nextHandle = (uint)Math.Min(LedgerRecords.StoreHandle, (ulong)handle + spare);
            Assert.Equal(RecordClass.Slot, LedgerRecords.Classify(record, nextHandle, out var parsedHandle, out var parsedSlot));
            Assert.Equal((handle, slot), (parsedHandle, parsedSlot));
            Assert.True(LedgerRecords.IsDataRecord(record));
            _ = Assert.Throws<ArgumentException>(() => LedgerRecords.DecodeStore(record));

            // The same bytes are damage to a catalog which never assigned the handle, and so is the record without its
            // value: no slot record is empty.
            Assert.Equal(RecordClass.Damaged, LedgerRecords.Classify(record, handle, out _, out _));
            using var empty = ArcBuffer.Wrap(header.ToArray());
            Assert.Equal(RecordClass.Damaged, LedgerRecords.Classify(empty, nextHandle, out _, out _));
            Assert.False(LedgerRecords.IsDataRecord(empty));
        }));

    [Fact]
    public void ParsingInvertsTheStoreRecordLayoutForAnyCreateDropOrSeal() =>
        Gen.Select(AnyStoreRecord, Gen.UInt[0, LedgerRecords.StoreHandle]).Sample((expected, nextHandle) => _leaks.InScope(() =>
        {
            using var header = LedgerRecords.RecordKey(expected.Key);
            var bytes = (byte[])[.. header.ToArray(), .. expected.Body()];
            Assert.Equal(0xFFFF_FF00_0000_0000ul | (ulong)expected.Kind, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            var bodyLength = expected switch
            {
                CreateRecord create => StoreRecord.PairLength * create.Pairs.Length,
                DropRecord => DropRecord.EncodedBodyLength,
                _ => SealRecord.EncodedBodyLength,
            };
            Assert.Equal(bodyLength, bytes.Length - LedgerRecords.KeyLength);

            // A store record classifies as one whatever the catalog's next handle, so replay meets it before any check.
            using var record = ArcBuffer.Wrap(bytes);
            Assert.Equal(RecordClass.Store, LedgerRecords.Classify(record, nextHandle, out _, out _));
            Assert.Equal((ulong)expected.Kind, LedgerRecords.StoreKindOf(record));
            Assert.Equal(expected, StoreRecordTestHelpers.Decode(record, out _));
        }));

    /// <summary>
    /// A create record of any number of creations encodes into its request's writer, and decodes from a record whose
    /// body spans pages, reading the body where it lies rather than from one array (conventions.md §1).
    /// </summary>
    [Fact]
    public void ACreateRecordWhoseBodySpansPagesEncodesAndDecodesInPlace()
    {
        var expected = new CreateRecord([.. Enumerable.Range(0, 300).Select(static index => new CatalogPair(new LedgerId((ulong)index + 1), (uint)index, 64))]);
        using var writer = new ArcBufferWriter();
        using (var key = LedgerRecords.RecordKey(expected.Key))
        {
            writer.Write(key.AsReadOnlySequence());
        }

        expected.Encode(writer);
        using var record = writer.ConsumeSlice(writer.Length);
        Assert.Equal(LedgerRecords.KeyLength + expected.BodyLength, record.Length);
        Assert.False(record.AsReadOnlySequence().IsSingleSegment);
        Assert.Equal(expected, Assert.IsType<CreateRecord>(LedgerRecords.DecodeStore(record).Value));
    }

    /// <summary>
    /// The parsing tests compare records by value, so a create record equals another only with the same pairs in the same
    /// order, and never equals null.
    /// </summary>
    [Fact]
    public void ACreateRecordEqualsOnlyOneWithTheSamePairs()
    {
        CatalogPair[] pairs = [new(new(1), 0, 4), new(new(2), 1, 4)];
        var record = new CreateRecord(pairs);
        Assert.Equal(new CreateRecord([.. pairs]), record);
        Assert.NotEqual(new CreateRecord([pairs[0]]), record);
        Assert.NotEqual(new CreateRecord([pairs[1], pairs[0]]), record);
        Assert.NotEqual(new CreateRecord([pairs[0], pairs[1] with { Window = 5 }]), record);
        Assert.False(record.Equals(null));
    }

    /// <summary>
    /// Any bytes classify as their layout says, and a store record either parses into a record which encodes back to
    /// exactly its bytes or is damaged for one of the store reasons; classifying and parsing never throw.
    /// </summary>
    [Fact]
    public void ArbitraryBytesAreEitherARecordOrDamage() =>
        Gen.Select(
                Gen.OneOf(
                    Gen.Byte.Array[0, 24],
                    Gen.Select(Gen.ULong[0, 7], Gen.Byte.Array[0, 40]).Select(static (kind, body) =>
                        (byte[])[.. BitConverter.GetBytes(StoreRecord.KeyOf((StoreRecordKind)kind)), .. body]),
                    Gen.Select(AnyStoreRecord, Gen.Int[0, 100], Gen.Byte[1, 255]).Select(static (record, position, flip) =>
                    {
                        byte[] bytes = [.. BitConverter.GetBytes(record.Key), .. record.Body()];
                        bytes[LedgerRecords.KeyLength + position % (bytes.Length - LedgerRecords.KeyLength)] ^= flip;
                        return bytes;
                    })),
                Gen.UInt[0, LedgerRecords.StoreHandle])
            .Sample((bytes, nextHandle) => _leaks.InScope(() =>
            {
                // A writer makes the empty record too, which wrapping an empty array cannot.
                using var writer = new ArcBufferWriter();
                writer.Write(bytes);
                using var record = writer.PeekSlice(writer.Length);
                var recordClass = LedgerRecords.Classify(record, nextHandle, out var handle, out var slot);
                var key = bytes.Length >= LedgerRecords.KeyLength ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0;
                Assert.Equal(Expected(bytes.Length, key, nextHandle), recordClass);
                Assert.Equal(bytes.Length > LedgerRecords.KeyLength, LedgerRecords.IsDataRecord(record));
                if (recordClass == RecordClass.Slot)
                {
                    Assert.Equal(key, LedgerRecords.Key(handle, slot));
                }

                if (recordClass != RecordClass.Store)
                {
                    _ = Assert.Throws<ArgumentException>(() => LedgerRecords.DecodeStore(record));
                }
                else if (StoreRecordTestHelpers.Decode(record, out var reason) is { } parsed)
                {
                    byte[] encoded = [.. BitConverter.GetBytes(parsed.Key), .. parsed.Body()];
                    Assert.Equal(bytes, encoded);
                }
                else
                {
                    Assert.Contains(reason, StoreReasons);
                }
            }));

    [Theory]
    [MemberData(nameof(StoreRejectionReasons))]
    public void EveryRuleBreakingStoreRecordIsDamagedForItsReason(string reason)
    {
        foreach (var bytes in RuleBreakingStoreRecords(reason))
        {
            using var record = ArcBuffer.Wrap(bytes);
            Assert.Null(StoreRecordTestHelpers.Decode(record, out var actual));
            Assert.Equal(reason, actual);
        }
    }

    /// <summary>
    /// The slot key space ends below the store handle: the last replica handle, 2^24 - 2, and the last slot, 2^40 - 1.
    /// The store handle's keys are store records, whose slot field holds the kind.
    /// </summary>
    [Fact]
    public void TheSlotKeySpaceEndsBelowTheStoreHandle()
    {
        Assert.Equal(0xFFFF_FEFF_FFFF_FFFFul, LedgerRecords.Key(LedgerRecords.StoreHandle - 1, LedgerRecords.SlotLimit - 1));
        Assert.Equal(0ul, LedgerRecords.Key(0, 0));
        Assert.Equal(1ul << 40, LedgerRecords.Key(1, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => LedgerRecords.Key(LedgerRecords.StoreHandle, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => LedgerRecords.Key(0, LedgerRecords.SlotLimit));
        Assert.Equal(0xFFFF_FF00_0000_0001ul, StoreRecord.KeyOf(StoreRecordKind.Create));
        Assert.Equal(0xFFFF_FF00_0000_0002ul, StoreRecord.KeyOf(StoreRecordKind.Drop));
        Assert.Equal(0xFFFF_FF00_0000_0003ul, StoreRecord.KeyOf(StoreRecordKind.Seal));

        // A record of the last replica handle parses only once the catalog has assigned every handle.
        using var last = ArcBuffer.Wrap((byte[])[.. BitConverter.GetBytes(LedgerRecords.Key(LedgerRecords.StoreHandle - 1, LedgerRecords.SlotLimit - 1)), 1]);
        Assert.NotEqual(RecordClass.Slot, LedgerRecords.Classify(last, LedgerRecords.StoreHandle - 1, out _, out _));
        Assert.Equal(RecordClass.Slot, LedgerRecords.Classify(last, LedgerRecords.StoreHandle, out var handle, out var slot));
        Assert.Equal((LedgerRecords.StoreHandle - 1, LedgerRecords.SlotLimit - 1), (handle, slot));

        // The largest key is a store record of an unknown kind.
        using var largest = ArcBuffer.Wrap(BitConverter.GetBytes(ulong.MaxValue));
        Assert.Equal(RecordClass.Store, LedgerRecords.Classify(largest, LedgerRecords.StoreHandle, out _, out _));
        Assert.Null(StoreRecordTestHelpers.Decode(largest, out var reason));
        Assert.Equal("store-kind-unknown", reason);
    }

    public static TheoryData<string> StoreRejectionReasons() => [.. StoreReasons];

    private static RecordClass Expected(int length, ulong key, uint nextHandle)
    {
        if (length < LedgerRecords.KeyLength)
        {
            return RecordClass.Damaged;
        }

        if (StoreRecord.IsStoreKey(key))
        {
            return RecordClass.Store;
        }

        // A slot record holds DATA, which is never empty.
        return length > LedgerRecords.KeyLength && key >> LedgerRecords.SlotBits < nextHandle ? RecordClass.Slot : RecordClass.Damaged;
    }

    private static byte[][] RuleBreakingStoreRecords(string reason) => reason switch
    {
        "store-kind-unknown" => [Store(0, Pair(7, 0)), Store(4, Pair(7, 0)), Store(LedgerRecords.SlotLimit - 1)],
        "create-body-length" => [Store(1), Store(1, [.. Pair(7, 0), 0]), Store(1, Pair(7, 0)[..15]), Store(1, Seal(7, 0))],
        "drop-body-length" => [Store(2, Pair(7, 6)), Store(2), Store(2, Ledger(7)[..7]), Store(2, [.. Ledger(7), 0])],
        "seal-body-length" => [Store(3), Store(3, Ledger(7)), Store(3, Seal(7, 0)[..11]), Store(3, [.. Seal(7, 0), 0]), Store(3, Pair(7, 0))],
        "zero-ledger" => [Store(1, [.. Pair(7, 0), .. Pair(0, 1)]), Store(2, Ledger(0)), Store(3, Seal(0, 1))],
        "handle-out-of-range" =>
        [
            Store(1, Pair(7, LedgerRecords.StoreHandle)),
            Store(1, Pair(7, uint.MaxValue)),
            Store(3, Seal(7, LedgerRecords.StoreHandle)),
            Store(3, Seal(7, uint.MaxValue)),
        ],
        "window-out-of-range" => [Store(1, Pair(7, 0, window: 0)), Store(1, [.. Pair(7, 0), .. Pair(8, 1, window: (uint)int.MaxValue + 1)])],
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown store record rejection reason."),
    };

    private static byte[] Store(long kind, params byte[] body) =>
        [.. BitConverter.GetBytes(((ulong)LedgerRecords.StoreHandle << LedgerRecords.SlotBits) | (ulong)kind), .. body];

    private static byte[] Ledger(ulong ledger)
    {
        var bytes = new byte[DropRecord.EncodedBodyLength];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, ledger);
        return bytes;
    }

    private static byte[] Pair(ulong ledger, uint handle, uint window = 4)
    {
        var bytes = new byte[StoreRecord.PairLength];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, ledger);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), handle);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), window);
        return bytes;
    }

    private static byte[] Seal(ulong ledger, uint handle)
    {
        var bytes = new byte[SealRecord.EncodedBodyLength];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, ledger);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), handle);
        return bytes;
    }
}

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Hashing;
using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the <c>CLSC</c> version 1 catalog checkpoint codec (ledgerstore.md §7.2): decoding inverts encoding for any
/// catalog and cut, decoding accepts only canonical encodings and rejects all other bytes as
/// <see cref="InvalidDataException"/> with a reason, every rule-breaking checkpoint is rejected for its rule, a
/// checkpoint's name is its cut, and the pure transitions fold create and drop records as replay checks them, a create
/// recording each replica's window (LR-I6) and a drop leaving its ledger's tombstone (LS-I1, LS-I2, §7.3).
/// </summary>
public sealed class ReplicaCatalogTests : IDisposable
{
    /// <summary>
    /// Every reason the decoder reports, in the order it checks them.
    /// </summary>
    internal static readonly string[] Reasons =
    [
        "truncated",
        "bad-magic",
        "bad-version",
        "length-mismatch",
        "crc-mismatch",
        "zero-cut-file",
        "cut-mismatch",
        "next-handle-out-of-range",
        "count-overflow",
        "handle-order",
        "handle-not-below-next",
        "window-out-of-range",
        "zero-first-file",
        "first-file-above-cut",
        "zero-ledger",
        "duplicate-ledger",
        "tombstone-order",
        "tombstone-live",
        "trailing-bytes",
    ];

    /// <summary>
    /// The cut the rule-breaking checkpoints are named by.
    /// </summary>
    internal static readonly WalPosition BaseCut = new(9, 12288);

    private static readonly Gen<LedgerId> Ledgers =
        Gen.Frequency((4, Gen.ULong[1, 64]), (1, Gen.ULong[1, ulong.MaxValue])).Select(static value => new LedgerId(value));

    private static readonly Gen<ulong> Files = Gen.Frequency((4, Gen.ULong[1, 1_000]), (1, Gen.ULong[1, ulong.MaxValue]));

    private static readonly Gen<uint> Handles = Gen.Frequency((4, Gen.UInt[0, 64]), (1, Gen.UInt[0, LedgerRecords.StoreHandle - 1]));

    private static readonly Gen<int> Windows = Gen.Frequency((4, Gen.Int[1, 64]), (1, Gen.Int[1, int.MaxValue]));

    /// <summary>
    /// Any valid checkpoint: replicas in ascending handle order below the next handle, with positive windows, non-zero
    /// first files at or below the cut's file, and distinct non-zero ledger IDs, and tombstones of other ledgers.
    /// </summary>
    private static readonly Gen<CatalogCheckpoint> AnyCheckpoint =
        Gen.Select(Files, Gen.ULong, Gen.Select(Handles, Windows, Files, Ledgers).Array[0, 12], Gen.UInt[0, 100], Ledgers.Array[0, 12])
            .Select(static (cutFile, offset, drawn, spare, dropped) =>
            {
                CatalogReplica[] replicas =
                [
                    .. drawn
                        .DistinctBy(static replica => replica.Item1)
                        .DistinctBy(static replica => replica.Item4)
                        .OrderBy(static replica => replica.Item1)
                        .Select(replica => new CatalogReplica(replica.Item1, replica.Item2, Math.Min(replica.Item3, cutFile), replica.Item4)),
                ];
                var next = Math.Min(LedgerRecords.StoreHandle, (replicas.Length == 0 ? 0 : replicas[^1].Handle + 1) + spare);
                var tombstones = dropped.Where(ledger => replicas.All(replica => replica.Ledger != ledger)).ToImmutableSortedSet();
                return new CatalogCheckpoint(new WalPosition(cutFile, offset), new ReplicaCatalog(next, replicas, tombstones));
            });

    /// <summary>
    /// The checkpoints a store reaches by batches of creations and drops, each record in a WAL file at or after the
    /// last: a creation of a listed or dropped ledger, and a drop of a dropped one, writes nothing; a drop of an unlisted
    /// ledger leaves its tombstone (ledgerstore.md §8.5, §8.6).
    /// </summary>
    private static readonly Gen<CatalogCheckpoint> Reachable =
        Gen.Select(Gen.Bool, Gen.ULong[1, 20].Array[1, 4], Gen.ULong[0, 2], Gen.UInt[1, 8]).Array[0, 30].Select(static operations =>
        {
            var catalog = ReplicaCatalog.Empty;
            var file = 1UL;
            foreach (var (drop, values, advance, window) in operations)
            {
                file += advance;
                if (drop)
                {
                    if (!catalog.IsDropped(new LedgerId(values[0])))
                    {
                        catalog = catalog.Dropped(new LedgerId(values[0]));
                    }

                    continue;
                }

                var created = values.Distinct().Select(static value => new LedgerId(value)).Where(ledger => catalog.Find(ledger) is null && !catalog.IsDropped(ledger)).ToArray();
                if (created.Length != 0)
                {
                    catalog = catalog.Created([.. created.Select((ledger, index) => new CatalogPair(ledger, catalog.NextHandle + (uint)index, window))], file);
                }
            }

            return new CatalogCheckpoint(new WalPosition(file, 4096), catalog);
        });

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void DecodingInvertsEncodingForAnyCatalog() =>
        Gen.OneOf(AnyCheckpoint, Reachable).Sample(static checkpoint =>
        {
            var bytes = checkpoint.Catalog.Encode(checkpoint.Cut);
            var decoded = ReplicaCatalog.Decode(bytes, checkpoint.Cut).Checkpoint();
            AssertEquivalent(checkpoint, decoded);
            Assert.Equal(bytes, decoded.Catalog.Encode(decoded.Cut));
        });

    /// <summary>
    /// Any bytes either decode to a checkpoint which encodes back to exactly those bytes, so only the canonical encoding
    /// is accepted, or are rejected as <see cref="InvalidDataException"/> with one of the decoder's reasons.
    /// </summary>
    [Fact]
    public void ArbitraryBytesAreEitherACanonicalCatalogOrRejectedWithAReason() =>
        Gen.OneOf(
                Gen.Byte.Array[0, 120],
                Gen.Select(AnyCheckpoint, Gen.Int[0, 1_000], Gen.Byte[1, 255]).Select(static (checkpoint, position, flip) =>
                {
                    var bytes = checkpoint.Catalog.Encode(checkpoint.Cut);
                    bytes[position % bytes.Length] ^= flip;
                    return bytes;
                }),
                Gen.Select(AnyCheckpoint, Gen.Int[0, 1_000], Gen.Byte[1, 255]).Select(static (checkpoint, position, flip) =>
                {
                    // A damaged body under a valid header reaches the body's rules.
                    var body = checkpoint.Catalog.Encode(checkpoint.Cut)[16..];
                    body[position % body.Length] ^= flip;
                    return WithHeader(body);
                }),
                Gen.Select(AnyCheckpoint, Gen.Int[0, 300], Gen.Byte.Array[0, 24]).Select(static (checkpoint, cut, extra) =>
                {
                    var body = checkpoint.Catalog.Encode(checkpoint.Cut)[16..];
                    return WithHeader([.. body.AsSpan(0, Math.Min(cut, body.Length)), .. extra]);
                }))
            .Sample(static bytes =>
            {
                try
                {
                    var decoded = ReplicaCatalog.Decode(bytes).Checkpoint();
                    Assert.Equal(bytes, decoded.Catalog.Encode(decoded.Cut));
                }
                catch (InvalidDataException exception)
                {
                    Assert.Contains(Assert.IsType<string>(exception.Data[DecodeRejection.ReasonKey]), Reasons);
                }
            });

    [Theory]
    [MemberData(nameof(RuleBreakingReasons))]
    public void EveryRuleBreakingCatalogIsRejectedForItsReason(string reason)
    {
        _ = ReplicaCatalog.Decode(ValidRejectionBase(), BaseCut).Checkpoint();
        foreach (var (bytes, cut) in RuleBreaking(reason))
        {
            var exception = Assert.Throws<InvalidDataException>(() => ReplicaCatalog.Decode(bytes, cut).Checkpoint());
            Assert.Equal(reason, exception.Data[DecodeRejection.ReasonKey]);
        }
    }

    [Fact]
    public void BoundaryCatalogsRoundTrip()
    {
        var boundaries = new[]
        {
            new CatalogCheckpoint(new WalPosition(1, 0), ReplicaCatalog.Empty),
            new CatalogCheckpoint(
                new WalPosition(ulong.MaxValue, ulong.MaxValue),
                new ReplicaCatalog(
                    LedgerRecords.StoreHandle,
                    [new CatalogReplica(0, 1, 1, new(1)), new CatalogReplica(LedgerRecords.StoreHandle - 1, int.MaxValue, ulong.MaxValue, new(ulong.MaxValue))],
                    [new(2), new(ulong.MaxValue - 1)])),
            new CatalogCheckpoint(new WalPosition(2, 4096), new ReplicaCatalog(3, [new CatalogReplica(1, 4, 1, new(1)), new CatalogReplica(2, 64, 2, new(ulong.MaxValue - 1))], [])),
            new CatalogCheckpoint(new WalPosition(2, 4096), new ReplicaCatalog(0, [], [new(1), new(ulong.MaxValue)])),

            // 1,000 replicas and 1,000 tombstones span several of the encoder's 4 KiB chunks.
            new CatalogCheckpoint(
                new WalPosition(2, 4096),
                new ReplicaCatalog(
                    1_000,
                    [.. Enumerable.Range(0, 1_000).Select(static index => new CatalogReplica((uint)index, index + 1, 1, new((ulong)index + 1)))],
                    [.. Enumerable.Range(1_001, 1_000).Select(static index => new LedgerId((ulong)index))])),
        };
        foreach (var checkpoint in boundaries)
        {
            AssertEquivalent(checkpoint, ReplicaCatalog.Decode(checkpoint.Catalog.Encode(checkpoint.Cut), checkpoint.Cut).Checkpoint());
        }
    }

    /// <summary>
    /// A checkpoint's name is its cut, as 20-digit decimal file and offset, so names sort by cut; no other name parses.
    /// </summary>
    [Fact]
    public void ACheckpointsNameIsItsCut()
    {
        Gen.Select(Gen.ULong, Gen.ULong).Sample(static (file, offset) =>
        {
            var cut = new WalPosition(file, offset);
            Assert.True(CatalogCheckpoints.TryParseFileName(CatalogCheckpoints.FileName(cut), out var parsed));
            Assert.Equal(cut, parsed);
        });

        Assert.Equal("00000000000000000009-00000000000000012288.clsc", CatalogCheckpoints.FileName(BaseCut));
        var ordered = new[] { new WalPosition(2, 5), new WalPosition(10, 1), new WalPosition(2, 40_000), new WalPosition(1, ulong.MaxValue) }
            .Select(CatalogCheckpoints.FileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        WalPosition[] expected = [new(1, ulong.MaxValue), new(2, 5), new(2, 40_000), new(10, 1)];
        Assert.Equal(expected, ordered.Select(static name => CatalogCheckpoints.TryParseFileName(name, out var cut) ? cut : default));
        foreach (var name in new[]
        {
            "9-12288.clsc",
            "00000000000000000009-00000000000000012288.clsi",
            "00000000000000000009_00000000000000012288.clsc",
            "0000000000000000000a-00000000000000012288.clsc",
            "+0000000000000000009-00000000000000012288.clsc",
            "99999999999999999999-00000000000000012288.clsc",
        })
        {
            Assert.False(CatalogCheckpoints.TryParseFileName(name, out _), name);
        }
    }

    /// <summary>
    /// LS-I1, LS-I2, LR-I6, §7.3: a create record's pairs take consecutive handles from the next handle, list each
    /// ledger with its pair's window and the record's file as its first file, and keep every other replica; a drop removes exactly its own replica, if
    /// listed, keeps the next handle, and leaves its ledger's tombstone. Replay rejects a create whose handles do not run
    /// from the next handle or which names a listed or dropped ledger, and a drop of a dropped ledger. Each catalog
    /// follows every earlier one.
    /// </summary>
    [Fact]
    public void TransitionsFoldCreateAndDropRecordsAsReplayChecksThem() =>
        Gen.Select(Gen.OneOf(AnyCheckpoint, Reachable), Ledgers.Array[1, 4], Files, Gen.UInt[1, 64]).Sample(static (checkpoint, ledgers, file, window) =>
        {
            var catalog = checkpoint.Catalog;
            var target = ledgers[0];
            if (catalog.IsDropped(target))
            {
                Assert.Equal("drop-ledger-dropped", DecodeRejection.ReasonOf(catalog.Drop(target, out var notDropped)!));
                Assert.Null(notDropped);
                Assert.Equal("create-ledger-dropped", DecodeRejection.ReasonOf(catalog.Create([new CatalogPair(target, catalog.NextHandle, window)], file, out var notCreated)!));
                Assert.Null(notCreated);
            }
            else
            {
                var dropped = catalog.Dropped(target);
                Assert.Equal(catalog.NextHandle, dropped.NextHandle);
                Assert.Equal(catalog.Replicas.Where(replica => replica.Ledger != target), dropped.Replicas);
                Assert.Null(dropped.Find(target));
                Assert.Equal(catalog.Tombstones.Add(target), dropped.Tombstones);
                Assert.Equal(dropped, catalog.Applied(new DropRecord(target), file), CatalogComparer.Instance);
                Assert.True(dropped.Follows(catalog));
                Assert.False(catalog.Follows(dropped));
            }

            var fresh = ledgers.Distinct().Where(ledger => catalog.Find(ledger) is null && !catalog.IsDropped(ledger)).ToArray();
            if (fresh.Length == 0 || catalog.NextHandle + (uint)fresh.Length > LedgerRecords.StoreHandle)
            {
                return;
            }

            CatalogPair[] pairs = [.. fresh.Select((ledger, index) => new CatalogPair(ledger, catalog.NextHandle + (uint)index, window + (uint)index))];
            var created = catalog.Created(pairs, file);
            Assert.Equal(catalog.NextHandle + (uint)pairs.Length, created.NextHandle);
            CatalogReplica[] expected = [.. catalog.Replicas, .. pairs.Select(pair => new CatalogReplica(pair.Handle, (int)pair.Window, file, pair.Ledger))];
            Assert.Equal(expected, created.Replicas);
            Assert.Equal(catalog.Tombstones, created.Tombstones);
            Assert.Equal(created, catalog.Applied(new CreateRecord(pairs), file), CatalogComparer.Instance);
            Assert.True(created.Follows(catalog));

            Assert.Equal("create-handle-mismatch", DecodeRejection.ReasonOf(catalog.Create([.. pairs.Select(pair => pair with { Handle = pair.Handle + 1 })], file, out _)!));
            Assert.Equal("create-ledger-listed", DecodeRejection.ReasonOf(created.Create([new CatalogPair(pairs[0].Ledger, created.NextHandle, window)], file, out _)!));
        });

    /// <summary>
    /// LS-I2, §8.7 step 8: a catalog follows an earlier one only when every clause holds, each checked alone here: it
    /// assigned at least the earlier one's handles, keeps each of its tombstones, and of each replica it lists either
    /// dropped the ledger or lists the same replica, with the same handle, window, and first file. A copy follows, and so
    /// do later creations, drops, and handles.
    /// </summary>
    [Fact]
    public void FollowsChecksEachClauseAlone()
    {
        CatalogReplica first = new(0, 4, 1, new(1));
        CatalogReplica second = new(1, 4, 2, new(2));
        var earlier = new ReplicaCatalog(2, [first, second], [new LedgerId(9)]);

        Assert.True(earlier.Follows(earlier));
        Assert.True(new ReplicaCatalog(2, [first, second], [new LedgerId(9)]).Follows(earlier));
        Assert.True(new ReplicaCatalog(5, [first, second], [new LedgerId(9)]).Follows(earlier));
        Assert.True(earlier.Dropped(new(2)).Follows(earlier));
        Assert.True(earlier.Created([new CatalogPair(new(3), 2, 4)], 4).Follows(earlier));

        Assert.False(new ReplicaCatalog(1, [first, second], [new LedgerId(9)]).Follows(earlier), "fewer handles");
        Assert.False(new ReplicaCatalog(2, [first, second], []).Follows(earlier), "a lost tombstone");
        Assert.False(new ReplicaCatalog(2, [first], [new LedgerId(9)]).Follows(earlier), "a replica neither listed nor dropped");
        Assert.False(new ReplicaCatalog(2, [first, second with { Handle = 0 }], [new LedgerId(9)]).Follows(earlier), "a replica under another handle");
        Assert.False(new ReplicaCatalog(2, [first, second with { FirstFile = 3 }], [new LedgerId(9)]).Follows(earlier), "a replica with another first file");
        Assert.False(new ReplicaCatalog(2, [first, second with { Window = 5 }], [new LedgerId(9)]).Follows(earlier), "a replica with another window");
    }

    /// <summary>
    /// Pins the byte layout by hand: the header's magic, version, and body length, then the cut, the next handle, the
    /// replica count, each replica's handle, window, first file, and ledger ID, the tombstone count, and each tombstone's
    /// ledger ID, ascending, little-endian; the header's CRC-32C covers the body.
    /// </summary>
    [Fact]
    public void TheCatalogLayoutIsPinned()
    {
        var catalog = ReplicaCatalog.Empty
            .Created([new CatalogPair(new(7), 0, 4), new CatalogPair(new(5), 1, 4)], 1)
            .Created([new CatalogPair(new(2), 2, 16)], 3)
            .Dropped(new(5))
            .Dropped(new(4));
        Assert.Equal(new CatalogReplica(0, 4, 1, new(7)), catalog.Find(new(7)));
        Assert.Equal(new CatalogReplica(2, 16, 3, new(2)), catalog.Find(new(2)));
        var bytes = catalog.Encode(new WalPosition(3, 0x1000));
        Assert.Equal("434c5343" + "01000000" + "5c000000", Convert.ToHexStringLower(bytes.AsSpan(0, 12)));
        Assert.Equal(Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes.AsSpan(16)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        Assert.Equal(
            "0300000000000000" + "0010000000000000" // cut: file 3, offset 4096
            + "03000000" + "02000000" // next handle 3, two replicas
            + "00000000" + "04000000" + "0100000000000000" + "0700000000000000" // handle 0, window 4, first file 1, ledger 7
            + "02000000" + "10000000" + "0300000000000000" + "0200000000000000" // handle 2, window 16, first file 3, ledger 2
            + "02000000" + "0400000000000000" + "0500000000000000", // two tombstones: ledgers 4, never held, and 5
            Convert.ToHexStringLower(bytes.AsSpan(16)));
    }

    public static TheoryData<string> RuleBreakingReasons() => [.. Reasons];

    /// <summary>
    /// Returns the checkpoint the rule-breaking ones start from: cut (9, 12288), next handle 5, replicas at handles 1, 3,
    /// and 4 of ledgers 2, 9, and 5, each of window 4, and the tombstones of ledgers 6 and 11, whose replicas held
    /// handles 0 and 2.
    /// </summary>
    internal static byte[] ValidRejectionBase() => WithHeader(Base());

    /// <summary>
    /// Returns checkpoints which break exactly the rule <paramref name="reason"/> names, each with the cut its name
    /// gives, the first of them the shared rejection vector's.
    /// </summary>
    internal static (byte[] Bytes, WalPosition Cut)[] RuleBreaking(string reason)
    {
        var valid = ValidRejectionBase();
        return reason switch
        {
            "truncated" => [Named(WithHeader(Base()[..^1])), Named(valid[..15]), Named(WithHeader(Body(BaseCut.File, 5, 3, [], [])[..23]))],
            "bad-magic" => [Named(With(valid, static bytes => bytes[0] = (byte)'X'))],
            "bad-version" => [Named(With(valid, static bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2)))],
            "length-mismatch" => [Named(With(valid, static bytes => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) + 1)))],
            "crc-mismatch" => [Named(With(valid, static bytes => bytes[^1] ^= 1))],
            "zero-cut-file" => [(WithHeader(Base(cutFile: 0)), new WalPosition(0, BaseCut.Offset))],
            "cut-mismatch" => [(valid, new WalPosition(BaseCut.File, BaseCut.Offset + 4096)), (valid, new WalPosition(BaseCut.File + 1, BaseCut.Offset))],
            "next-handle-out-of-range" => [Named(WithHeader(Base(nextHandle: LedgerRecords.StoreHandle + 1)))],
            "count-overflow" => [Named(WithHeader(Base(count: uint.MaxValue))), Named(WithHeader(Base(tombstoneCount: uint.MaxValue)))],
            "handle-order" =>
            [
                Named(WithHeader(Base(replicas: [(1, 3, 2), (1, 4, 9), (4, 9, 5)]))),
                Named(WithHeader(Base(replicas: [(3, 3, 2), (1, 4, 9), (4, 9, 5)]))),
            ],
            "handle-not-below-next" => [Named(WithHeader(Base(replicas: [(1, 3, 2), (3, 4, 9), (5, 9, 5)])))],
            "window-out-of-range" => [Named(WithHeader(Base(windows: [4, 0, 4]))), Named(WithHeader(Base(windows: [4, 4, (uint)int.MaxValue + 1])))],
            "zero-first-file" => [Named(WithHeader(Base(replicas: [(1, 3, 2), (3, 0, 9), (4, 9, 5)])))],
            "first-file-above-cut" => [Named(WithHeader(Base(replicas: [(1, 3, 2), (3, 4, 9), (4, 10, 5)])))],
            "zero-ledger" => [Named(WithHeader(Base(replicas: [(1, 3, 2), (3, 4, 0), (4, 9, 5)]))), Named(WithHeader(Base(tombstones: [0, 11])))],
            "duplicate-ledger" => [Named(WithHeader(Base(replicas: [(1, 3, 2), (3, 4, 9), (4, 9, 2)])))],
            "tombstone-order" => [Named(WithHeader(Base(tombstones: [11, 6]))), Named(WithHeader(Base(tombstones: [6, 6])))],
            "tombstone-live" => [Named(WithHeader(Base(tombstones: [6, 9]))), Named(WithHeader(Base(tombstones: [2])))],
            "trailing-bytes" => [Named(WithHeader([.. Base(), 0]))],
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown catalog rejection reason."),
        };

        static (byte[] Bytes, WalPosition Cut) Named(byte[] bytes) => (bytes, BaseCut);
    }

    /// <summary>
    /// Writes the base checkpoint's body with the named fields replaced, whatever rules that breaks.
    /// </summary>
    internal static byte[] Base(
        ulong? cutFile = null,
        uint nextHandle = 5,
        uint? count = null,
        (uint Handle, ulong FirstFile, ulong Ledger)[]? replicas = null,
        ulong[]? tombstones = null,
        uint? tombstoneCount = null,
        uint[]? windows = null)
    {
        replicas ??= [(1, 3, 2), (3, 4, 9), (4, 9, 5)];
        tombstones ??= [6, 11];
        return Body(cutFile ?? BaseCut.File, nextHandle, count ?? (uint)replicas.Length, replicas, tombstones, tombstoneCount, windows);
    }

    /// <summary>
    /// Writes a checkpoint body field by field, whatever rules it breaks, with the base cut's offset; each replica's window
    /// is 4 unless <paramref name="windows"/> gives it.
    /// </summary>
    internal static byte[] Body(
        ulong cutFile,
        uint nextHandle,
        uint count,
        (uint Handle, ulong FirstFile, ulong Ledger)[] replicas,
        ulong[] tombstones,
        uint? tombstoneCount = null,
        uint[]? windows = null)
    {
        const int ReplicaLength = 24;
        var body = new byte[8 + 8 + 4 + 4 + ReplicaLength * replicas.Length + 4 + 8 * tombstones.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(body, cutFile);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), BaseCut.Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), nextHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), count);
        for (var index = 0; index < replicas.Length; index++)
        {
            var replica = body.AsSpan(24 + ReplicaLength * index);
            BinaryPrimitives.WriteUInt32LittleEndian(replica, replicas[index].Handle);
            BinaryPrimitives.WriteUInt32LittleEndian(replica[4..], windows?[index] ?? 4);
            BinaryPrimitives.WriteUInt64LittleEndian(replica[8..], replicas[index].FirstFile);
            BinaryPrimitives.WriteUInt64LittleEndian(replica[16..], replicas[index].Ledger);
        }

        var tail = body.AsSpan(24 + ReplicaLength * replicas.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(tail, tombstoneCount ?? (uint)tombstones.Length);
        for (var index = 0; index < tombstones.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(tail[(4 + 8 * index)..], tombstones[index]);
        }

        return body;
    }

    /// <summary>
    /// Prefixes a body with a valid header: the magic, version 1, the body's length, and its CRC-32C.
    /// </summary>
    internal static byte[] WithHeader(ReadOnlySpan<byte> body)
    {
        var bytes = new byte[16 + body.Length];
        "CLSC"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body));
        body.CopyTo(bytes.AsSpan(16));
        return bytes;
    }

    private static byte[] With(byte[] valid, Action<byte[]> mutate)
    {
        var bytes = (byte[])valid.Clone();
        mutate(bytes);
        return bytes;
    }

    private static void AssertEquivalent(CatalogCheckpoint expected, CatalogCheckpoint actual)
    {
        Assert.Equal(expected.Cut, actual.Cut);
        Assert.Equal(expected.Catalog, actual.Catalog, CatalogComparer.Instance);
    }

    /// <summary>
    /// Compares catalogs by their next handle, replicas, and tombstones.
    /// </summary>
    private sealed class CatalogComparer : IEqualityComparer<ReplicaCatalog>
    {
        internal static CatalogComparer Instance { get; } = new();

        public bool Equals(ReplicaCatalog? x, ReplicaCatalog? y) =>
            x is not null && y is not null && x.NextHandle == y.NextHandle && x.Replicas.SequenceEqual(y.Replicas) && x.Tombstones.SequenceEqual(y.Tombstones);

        public int GetHashCode(ReplicaCatalog obj) => HashCode.Combine(obj.NextHandle, obj.Replicas.Count);
    }
}

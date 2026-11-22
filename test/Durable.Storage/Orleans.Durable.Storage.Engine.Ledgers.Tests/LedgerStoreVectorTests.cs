using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the shared LedgerStore vectors under <c>tests/shared-vectors/ledgerstore</c> (ledgerstore.md §7.1–§7.4):
/// the node file, the <c>CLSC</c> catalog checkpoint, the slot and store WAL records, slot-index leaves, generation files,
/// root slots, and the trees of unusable roots.
/// </summary>
public sealed class LedgerStoreVectorTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const string Area = "ledgerstore";

    /// <summary>
    /// The leaf's rejection reasons, in check order (ledgerstore.md §7.4).
    /// </summary>
    internal static readonly string[] LeafReasons =
    [
        "truncated",
        "reserved-flags",
        "width-out-of-range",
        "file-count-out-of-range",
        "file-table-order",
        "bitmap-empty-or-full",
        "too-many-exceptions",
        "length-mismatch",
        "exception-order",
        "exception-absent-slot",
        "exception-residual",
        "file-index-out-of-range",
        "page-out-of-range",
        "nonzero-padding",
    ];

    /// <summary>
    /// The root slot's rejection reasons, in check order (ledgerstore.md §7.4).
    /// </summary>
    private static readonly string[] RootReasons =
    [
        "truncated",
        "crc-mismatch",
        "bad-magic",
        "bad-version",
        "nonzero-padding",
        "null-reference-fields",
        "height-out-of-range",
        "height-root-mismatch",
        "generation-zero",
        "generation-out-of-range",
        "unknown-flags",
        "corruption-end-without-flag",
    ];

    /// <summary>
    /// The tree's rejection reasons, in check order (ledgerstore.md §7.4).
    /// </summary>
    private static readonly string[] TreeReasons =
    [
        "node-length",
        "leaf-beyond-address-space",
        "leaf-length",
        "generation-after-root",
        "generation-missing",
        "outside-file",
        "overlap",
        "crc-mismatch",
        "null-reference-fields",
        "node-empty",
    ];

    /// <summary>
    /// The node file's rejection reasons, in check order (ledgerstore.md §7.1).
    /// </summary>
    private static readonly string[] NodeFileReasons = ["digits", "empty-uuid"];

    /// <summary>
    /// §7.1, LS-I6: each node file vector's identity encodes to its exact bytes, and the bytes decode to the identity.
    /// </summary>
    [Fact]
    public void NodeFileVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "node-files.json"))
        {
            var node = new NodeId(Guid.ParseExact(Text(vector, "identity"), "N"));
            AssertBytes(vector, NodeFile.Encode(node));
            Assert.Equal(node, NodeFile.Decode(Hex(vector)).Node());
        }
    }

    /// <summary>
    /// §7.1, LS-I6: each node file rejection vector is rejected for its reason, and the vectors cover every reason.
    /// </summary>
    [Fact]
    public void NodeFileRejectionVectorsAreRejectedForTheirReason()
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in Cases(Area, "node-file-rejections.json"))
        {
            var reason = Text(vector, "reason");
            var rejection = Assert.IsType<InvalidDataException>(NodeFile.Decode(Hex(vector)).Value);
            Assert.Equal(reason, DecodeRejection.ReasonOf(rejection));
            _ = reasons.Add(reason);
        }

        Assert.Equal(NodeFileReasons.Order(StringComparer.Ordinal), reasons.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The store classifies every node file rejection as damage: reading the node file fails with
    /// <see cref="LedgerReplicaCorruptException"/>, whose inner exception is the rejection with the vector's reason, so the
    /// store does not open (ledgerstore.md §7.1, conventions.md §15).
    /// </summary>
    [Fact]
    public async Task NodeFileRejectionsAreDamageCarryingTheRejection()
    {
        foreach (var vector in Cases(Area, "node-file-rejections.json"))
        {
            await using var directory = await DirectoryWithAsync(NodeFile.Name, Hex(vector));
            var damage = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await NodeFile.ReadAsync(directory, "root", TestContext.Current.CancellationToken));
            var rejection = Assert.IsType<InvalidDataException>(damage.InnerException);
            Assert.Equal(Text(vector, "reason"), DecodeRejection.ReasonOf(rejection));
        }
    }

    /// <summary>
    /// The store classifies every catalog checkpoint rejection as a checkpoint it never loads (LS-I3): reading one under
    /// its file name answers the rejection with the vector's reason, and the open's search for valid checkpoints passes
    /// over it (ledgerstore.md §7.2, conventions.md §15).
    /// </summary>
    [Fact]
    public async Task CatalogRejectionsAreCheckpointsTheStoreNeverLoads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var vector in Cases(Area, "catalog-rejections.json"))
        {
            var cut = NamedCut(vector);
            await using var directory = await DirectoryWithAsync(CatalogCheckpoints.FileName(cut), Hex(vector));
            var rejection = Assert.IsType<InvalidDataException>((await CatalogCheckpoints.ReadAsync(directory, cut, cancellationToken)).Value);
            Assert.Equal(Text(vector, "reason"), DecodeRejection.ReasonOf(rejection));
            Assert.Empty(await CatalogCheckpoints.ReadNewestValidAsync(directory, [cut], cancellationToken));
        }
    }

    [Fact]
    public void CatalogVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "catalog.json"))
        {
            var replicas = vector.GetProperty("replicas").EnumerateArray()
                .Select(static replica => new CatalogReplica(
                    replica.GetProperty("handle").GetUInt32(),
                    replica.GetProperty("window").GetInt32(),
                    UInt64(replica, "firstFile"),
                    new LedgerId(UInt64(replica, "ledger"))))
                .ToArray();
            var tombstones = vector.GetProperty("tombstones").EnumerateArray().Select(static tombstone => new LedgerId(ulong.Parse(tombstone.GetString()!, CultureInfo.InvariantCulture))).ToArray();
            var cut = Position(vector.GetProperty("cut"));
            var catalog = new ReplicaCatalog(vector.GetProperty("nextHandle").GetUInt32(), replicas, [.. tombstones]);
            AssertBytes(vector, catalog.Encode(cut));
            Assert.Equal(Text(vector, "fileName"), CatalogCheckpoints.FileName(cut));

            var decoded = ReplicaCatalog.Decode(Hex(vector), cut).Checkpoint();
            Assert.Equal(cut, decoded.Cut);
            Assert.Equal(catalog.NextHandle, decoded.Catalog.NextHandle);
            Assert.Equal(replicas, decoded.Catalog.Replicas);
            Assert.Equal(tombstones, decoded.Catalog.Tombstones);
        }
    }

    /// <summary>
    /// Each rejection vector is rejected for its reason when read under its file name, whose cut its body must restate,
    /// and the vectors cover every reason; the first vector of each reason is the first rule-breaking checkpoint of that
    /// reason, and a later one breaks it and the rule after it.
    /// </summary>
    [Fact]
    public void CatalogRejectionVectorsAreRejectedForTheirReason()
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(ReplicaCatalogTests.ValidRejectionBase(), Hex(Cases(Area, "catalog.json").Single(static vector => Name(vector) == "replicas-and-tombstones")));
        foreach (var vector in Cases(Area, "catalog-rejections.json"))
        {
            var reason = Text(vector, "reason");
            var exception = Record.Exception(() => ReplicaCatalog.Decode(Hex(vector), NamedCut(vector)).Checkpoint());
            AssertRejected(vector, exception);
            if (reasons.Add(reason))
            {
                var (bytes, cut) = ReplicaCatalogTests.RuleBreaking(reason)[0];
                Assert.Equal(bytes, Hex(vector));
                Assert.Equal(cut, NamedCut(vector));
            }
        }

        Assert.Equal(ReplicaCatalogTests.Reasons.Order(StringComparer.Ordinal), reasons.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Renders each catalog vector as the decoder reads it (ledgerstore.md §7.2): the header, the cut and the file name
    /// it gives, the next handle, the live replicas with their windows, and the tombstones. The vectors pin the bytes; the snapshot is their reviewed
    /// reading.
    /// </summary>
    [Fact]
    public Task CatalogVectorsDecodeAsTheFormatDescribesThem()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "catalog.json"))
        {
            if (text.Length > 0)
            {
                _ = text.AppendLine();
            }

            var bytes = Hex(vector);
            var (cut, catalog) = ReplicaCatalog.Decode(bytes).Checkpoint();
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"{Name(vector)}: {bytes.Length} bytes");
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"  header: {Encoding.ASCII.GetString(bytes, 0, 4)} version {BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4))}, a {BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8))}-byte body, CRC-32C 0x{BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)):x8}");
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"  cut: file {cut.File}, offset {cut.Offset}, named {CatalogCheckpoints.FileName(cut)}");
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"  next handle {catalog.NextHandle}");
            _ = text.AppendLine(catalog.Replicas.Count == 0 ? "  no live replica" : "  live replicas, in handle order:");
            foreach (var replica in catalog.Replicas)
            {
                _ = text.AppendLine(CultureInfo.InvariantCulture, $"    handle {replica.Handle}: ledger {replica.Ledger.Value}; window {replica.Window}; first file {replica.FirstFile}");
            }

            _ = text.AppendLine(catalog.Tombstones.Count == 0
                ? "  no tombstone"
                : string.Create(CultureInfo.InvariantCulture, $"  tombstones, ascending: {string.Join(", ", catalog.Tombstones)}"));
        }

        return Verify(text.ToString());
    }

    /// <summary>
    /// Renders each catalog rejection vector with the reason and message its decoding under its file name reports
    /// (ledgerstore.md §7.2). A store reports a rejected checkpoint at or above the retention start as corrupt, with
    /// this exception as the cause.
    /// </summary>
    [Fact]
    public Task CatalogRejectionsReportTheirReasonAndMessage()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "catalog-rejections.json"))
        {
            var exception = Assert.IsType<InvalidDataException>(Record.Exception(() => ReplicaCatalog.Decode(Hex(vector), NamedCut(vector)).Checkpoint()));
            _ = text.AppendLine(Name(vector));
            _ = text.AppendLine(CultureInfo.InvariantCulture, $"  {exception.Data[DecodeRejection.ReasonKey]}: {exception.Message}");
        }

        return Verify(text.ToString());
    }

    /// <summary>
    /// A slot record is its key followed by its DATA value, and a store record its key followed by a create's pairs, a
    /// drop's ledger, or a seal's ledger and handle (ledgerstore.md §7.3, LS-I11): the store writes the key as the
    /// write request's header, and parsing the bytes yields the handle and slot, or the create, drop, or seal record.
    /// </summary>
    [Fact]
    public void RecordVectorsMatchTheRecordLayoutInBothDirections()
    {
        foreach (var vector in Cases(Area, "records.json"))
        {
            using var record = ArcBuffer.Wrap(Hex(vector));
            var nextHandle = vector.GetProperty("nextHandle").GetUInt32();
            if (vector.TryGetProperty("kind", out var kindName))
            {
                StoreRecord expected = kindName.GetString() switch
                {
                    "create" => new CreateRecord(
                    [
                        .. vector.GetProperty("pairs").EnumerateArray().Select(static pair =>
                            new CatalogPair(new LedgerId(UInt64(pair, "ledger")), pair.GetProperty("handle").GetUInt32(), pair.GetProperty("window").GetUInt32())),
                    ]),
                    "drop" => new DropRecord(new LedgerId(UInt64(vector, "ledger"))),
                    "seal" => new SealRecord(new LedgerId(UInt64(vector, "ledger")), vector.GetProperty("handle").GetUInt32()),
                    var other => throw new InvalidDataException($"'{Name(vector)}' has the unknown store record kind '{other}'."),
                };
                using (var key = LedgerRecords.RecordKey(expected.Key))
                {
                    AssertBytes(vector, [.. key.ToArray(), .. expected.Body()]);
                }

                Assert.Equal(RecordClass.Store, LedgerRecords.Classify(record, nextHandle, out _, out _));
                Assert.Equal((ulong)expected.Kind, LedgerRecords.StoreKindOf(record));
                var parsed = StoreRecordTestHelpers.Decode(record, out var reason);
                Assert.True(parsed is not null, $"'{Name(vector)}' was damaged: {reason}.");
                Assert.Equal(expected, parsed);
                continue;
            }

            var handle = vector.GetProperty("handle").GetUInt32();
            var slot = Int64(vector, "slot");
            var value = Hex(vector, "valueHex");
            using (var key = LedgerRecords.RecordKey(LedgerRecords.Key(handle, slot)))
            {
                AssertBytes(vector, [.. key.ToArray(), .. value]);
            }

            Assert.True(LedgerRecords.Classify(record, nextHandle, out var parsedHandle, out var parsedSlot) == RecordClass.Slot, $"'{Name(vector)}' was rejected.");
            Assert.Equal((handle, slot), (parsedHandle, parsedSlot));
            Assert.True(LedgerRecords.IsDataRecord(record), $"'{Name(vector)}' does not hold DATA.");
        }
    }

    /// <summary>
    /// Each record rejection vector is damage for its reason: a record shorter than its key, a slot record with an empty
    /// value, or a slot record of an unassigned handle classifies as damaged, and a store record which breaks a rule of
    /// its format fails to parse for that rule. The vectors cover every reason.
    /// </summary>
    [Fact]
    public void RecordRejectionVectorsAreDamage()
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in Cases(Area, "record-rejections.json"))
        {
            var reason = Text(vector, "reason");
            using var record = ArcBuffer.Wrap(Hex(vector));
            var recordClass = LedgerRecords.Classify(record, vector.GetProperty("nextHandle").GetUInt32(), out _, out _);
            if (reason is "record-truncated" or "empty-slot-record" or "handle-not-allocated")
            {
                Assert.True(recordClass == RecordClass.Damaged, $"'{Name(vector)}' was accepted.");
            }
            else
            {
                Assert.Equal(RecordClass.Store, recordClass);
                Assert.True(StoreRecordTestHelpers.Decode(record, out var actual) is null, $"'{Name(vector)}' was accepted.");
                Assert.True(reason == actual, $"'{Name(vector)}' was damaged as {actual}, not {reason}.");
            }

            _ = reasons.Add(reason);
        }

        string[] expected = ["empty-slot-record", "handle-not-allocated", "record-truncated", .. LedgerStoreRecordTests.StoreReasons];
        Assert.Equal(expected.Order(StringComparer.Ordinal), reasons.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LeafVectorsMatchTheCanonicalEncoderInBothDirections()
    {
        foreach (var vector in Cases(Area, "leaves.json"))
        {
            var entries = Entries(vector.GetProperty("entries"));
            AssertBytes(vector, SlotLeafCodec.Encode(entries));

            var decoded = new SlotEntry[SlotLeafCodec.Slots];
            FrozenLeaf.Decode(Hex(vector)).Leaf().DecodeAll(decoded);
            Assert.Equal(entries, decoded);
        }
    }

    /// <summary>
    /// §7.4: each leaf rejection vector is rejected for its reason, and the vectors cover every reason.
    /// </summary>
    [Fact]
    public void LeafRejectionVectorsAreRejectedForTheirReason()
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in Cases(Area, "leaf-rejections.json"))
        {
            var reason = Text(vector, "reason");
            var rejection = Assert.IsType<InvalidDataException>(FrozenLeaf.Decode(Hex(vector)).Value);
            Assert.True(reason == DecodeRejection.ReasonOf(rejection), $"'{Name(vector)}' was rejected as {DecodeRejection.ReasonOf(rejection)}, not {reason}.");
            _ = reasons.Add(reason);
        }

        Assert.Equal(LeafReasons.Order(StringComparer.Ordinal), reasons.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Writes each generation through <see cref="SlotCheckpoints"/> and compares the generation file and root with the
    /// vector, then loads the vector's files and compares the references a recovery finds.
    /// </summary>
    [Fact]
    public async Task GenerationVectorsMatchTheCheckpointWriterInBothDirections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var vector in Cases(Area, "generations.json"))
        {
            var generation = UInt64(vector, "generation");
            var fileName = SlotCheckpoints.FileName(generation);
            var replayStart = new WalPosition(UInt64(vector.GetProperty("replayStart"), "file"), UInt64(vector.GetProperty("replayStart"), "offset"));
            var leaves = vector.GetProperty("leaves").EnumerateArray().ToArray();

            var storage = new SimulatedWalStorage();
            await storage.CreateDirectoryAsync("slots", cancellationToken);
            await using (var directory = await storage.OpenDirectoryAsync("slots", cancellationToken))
            {
                await using var checkpoints = SlotCheckpoints.Create(directory);
                var written = await checkpoints.WriteAsync(
                    replayStart,
                    null,
                    isSealed: false,
                    [.. leaves.Select(static leaf => new CheckpointLeaf(leaf.GetProperty("leaf").GetInt64(), SlotLeafCodec.Encode(Entries(leaf.GetProperty("entries")))))],
                    cancellationToken);
                Assert.Equal(leaves.ToDictionary(static leaf => leaf.GetProperty("leaf").GetInt64(), static leaf => Reference(leaf.GetProperty("reference"))), written);
                Assert.Equal(generation, checkpoints.Generation);
            }

            var file = storage.ReadDurableFile("slots", fileName)!;
            Assert.Equal(vector.GetProperty("fileLength").GetInt32(), file.Length);
            AssertBytes(vector, Trim(file), "fileHex");
            AssertBytes(vector, Trim(storage.ReadDurableFile("slots", SlotCheckpoints.RootName)!), "rootFileHex");

            var replay = new SimulatedWalStorage();
            await replay.CreateDirectoryAsync("slots", cancellationToken);
            await using (var directory = await replay.OpenDirectoryAsync("slots", cancellationToken))
            {
                await CreateFileAsync(directory, fileName, Expand(Hex(vector, "fileHex"), file.Length), cancellationToken);
                await CreateFileAsync(directory, SlotCheckpoints.RootName, Expand(Hex(vector, "rootFileHex"), 2 * 4096), cancellationToken);
            }

            await using (var directory = await replay.OpenDirectoryAsync("slots", cancellationToken))
            {
                var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
                await using (checkpoints)
                {
                    Assert.NotNull(loaded);
                    Assert.Equal(generation, loaded.Generation);
                    Assert.Equal(replayStart, loaded.ReplayStart);
                    Assert.False(loaded.Sealed);
                    foreach (var leaf in leaves)
                    {
                        var reference = loaded.Leaves[leaf.GetProperty("leaf").GetInt64()];
                        var decoded = new SlotEntry[SlotLeafCodec.Slots];
                        FrozenLeaf.Decode(await checkpoints.ReadLeafBytesAsync(reference, cancellationToken)).Leaf().DecodeAll(decoded);
                        Assert.Equal(Entries(leaf.GetProperty("entries")), decoded);
                    }

                    Assert.Equal(leaves.Length, loaded.Leaves.Count);
                }
            }

            var nodes = vector.GetProperty("nodes").EnumerateArray().ToArray();
            foreach (var node in nodes)
            {
                var reference = Reference(node.GetProperty("reference"));
                Assert.Equal(SlotCheckpoints.NodeSize, (int)reference.Length);
                var bytes = file.AsSpan((int)reference.Offset, (int)reference.Length);
                var children = node.GetProperty("children").EnumerateArray().ToDictionary(static child => child.GetProperty("index").GetInt32(), static child => Reference(child.GetProperty("reference")));
                for (var index = 0; index < SlotLeafCodec.Slots; index++)
                {
                    Assert.Null(CheckpointRef.Decode(bytes[(index * CheckpointRef.Size)..], out var child));
                    Assert.Equal(children.GetValueOrDefault(index), child);
                }
            }
        }
    }

    [Fact]
    public void RootVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "roots.json"))
        {
            var slotSize = vector.GetProperty("slotSize").GetInt32();
            var root = Root(vector);
            var slot = new byte[slotSize];
            root.Write(slot);
            AssertBytes(vector, Trim(slot));
            Assert.Equal(root, CheckpointRoot.Decode(Expand(Hex(vector), slotSize)).Root());
        }
    }

    /// <summary>
    /// §7.4: each root rejection vector is unusable for its reason, and the vectors cover every reason but
    /// <c>truncated</c>: a vector is a whole slot of at least 4,096 bytes, so none is shorter than its fields
    /// (<see cref="SlotCheckpointFormatTests.ASlotShorterThanItsFieldsIsTruncated"/>).
    /// </summary>
    [Fact]
    public void RootRejectionVectorsAreUnusableForTheirReason()
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in Cases(Area, "root-rejections.json"))
        {
            var reason = Text(vector, "reason");
            var rejection = Assert.IsType<InvalidDataException>(CheckpointRoot.Decode(Expand(Hex(vector), vector.GetProperty("slotSize").GetInt32())).Value);
            Assert.True(reason == DecodeRejection.ReasonOf(rejection), $"'{Name(vector)}' was unusable as {DecodeRejection.ReasonOf(rejection)}, not {reason}.");
            _ = reasons.Add(reason);
        }

        Assert.Equal(RootReasons.Where(static reason => reason != "truncated").Order(StringComparer.Ordinal), reasons.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The store classifies every root rejection as a slot it never loads (LS-I3): beside a usable root of generation 1,
    /// the rejected slot is no candidate, so loading reads no tree of it and loads generation 1, and the slot's generation
    /// does not count toward the next one (LS-I9). Only a generation at or above 2<sup>63</sup> is damage, which requests
    /// a checkpoint at once (ledgerstore.md §7.4, §8.8, conventions.md §15).
    /// </summary>
    [Fact]
    public async Task RootRejectionsAreSlotsTheStoreNeverLoads()
    {
        const int SlotSize = CheckpointRoot.MinimumSlotSize;
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var vector in Cases(Area, "root-rejections.json"))
        {
            Assert.Equal(SlotSize, vector.GetProperty("slotSize").GetInt32());
            var slots = new byte[2 * SlotSize];
            Expand(Hex(vector), SlotSize).CopyTo(slots, 0);
            new CheckpointRoot(1, 0, new WalPosition(1, 0), null, Sealed: false, default).Write(slots.AsSpan(SlotSize));
            await using var directory = await DirectoryWithAsync(SlotCheckpoints.RootName, slots);
            var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
            await using (checkpoints)
            {
                Assert.True(loaded?.Generation == 1, string.Create(CultureInfo.InvariantCulture, $"'{Name(vector)}' left generation {loaded?.Generation} loaded."));
                Assert.Empty(checkpoints.TreeRejections);
                Assert.Equal(Text(vector, "reason") == "generation-out-of-range", checkpoints.FoundDamage);
                _ = await checkpoints.WriteAsync(new WalPosition(1, 0), null, isSealed: true, [], cancellationToken);
                Assert.Equal(2UL, checkpoints.Generation);
            }
        }
    }

    /// <summary>
    /// §7.4: the store classifies every tree rejection vector as an unusable root (LS-I3): loading its files finds
    /// damage and no usable root, and rejects the one candidate root's tree for the vector's reason. The vectors cover
    /// every reason, and each vector's generation file is the root reference's, the layout the
    /// <c>ledgerstore-generation</c> fuzz target reads.
    /// </summary>
    [Fact]
    public async Task TreeRejectionVectorsAreUnusableRootsForTheirReason()
    {
        const int SlotSize = CheckpointRoot.MinimumSlotSize;
        var cancellationToken = TestContext.Current.CancellationToken;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in Cases(Area, "tree-rejections.json"))
        {
            var reason = Text(vector, "reason");
            var generation = UInt64(vector, "generation");
            var rootFile = Expand(Hex(vector, "rootFileHex"), 2 * SlotSize);
            Assert.Equal(generation, CheckpointRoot.Decode(rootFile.AsSpan(0, SlotSize)).Root().Root.Generation);

            var storage = new SimulatedWalStorage();
            await storage.CreateDirectoryAsync("slots", cancellationToken);
            await using (var directory = await storage.OpenDirectoryAsync("slots", cancellationToken))
            {
                await CreateFileAsync(directory, SlotCheckpoints.FileName(generation), Expand(Hex(vector, "fileHex"), vector.GetProperty("fileLength").GetInt32()), cancellationToken);
                await CreateFileAsync(directory, SlotCheckpoints.RootName, rootFile, cancellationToken);
            }

            await using (var directory = await storage.OpenDirectoryAsync("slots", cancellationToken))
            {
                var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
                await using (checkpoints)
                {
                    Assert.True(loaded is null, $"'{Name(vector)}' loaded a checkpoint.");
                    Assert.True(checkpoints.FoundDamage, $"'{Name(vector)}' found no damage.");
                    var rejection = Assert.Single(checkpoints.TreeRejections);
                    Assert.True(reason == DecodeRejection.ReasonOf(rejection), $"'{Name(vector)}' was unusable as {DecodeRejection.ReasonOf(rejection)}, not {reason}.");
                }
            }

            _ = reasons.Add(reason);
        }

        Assert.Equal(TreeReasons.Order(StringComparer.Ordinal), reasons.Order(StringComparer.Ordinal));
    }

    private static WalPosition Position(JsonElement element) => new(UInt64(element, "file"), UInt64(element, "offset"));

    private static WalPosition NamedCut(JsonElement vector) =>
        CatalogCheckpoints.TryParseFileName(Text(vector, "fileName"), out var cut) ? cut : throw new InvalidDataException($"'{Name(vector)}' has no catalog checkpoint name.");

    private static SlotKind Kind(string name) => name switch
    {
        "data" => SlotKind.Data,
        "faulty" => SlotKind.Faulty,
        _ => throw new InvalidDataException($"Unknown slot state '{name}'."),
    };

    /// <summary>
    /// Expands runs of entries: entry <c>first + k</c> of a run has the run's state, file <c>file + k * fileStep</c>,
    /// and page <c>page + k * pageStep</c>.
    /// </summary>
    private static SlotEntry[] Entries(JsonElement runs)
    {
        var entries = new SlotEntry[SlotLeafCodec.Slots];
        foreach (var run in runs.EnumerateArray())
        {
            var first = run.GetProperty("first").GetInt32();
            var count = run.TryGetProperty("count", out var value) ? value.GetInt32() : 1;
            var fileStep = run.TryGetProperty("fileStep", out var step) ? step.GetInt64() : 0;
            var pageStep = run.TryGetProperty("pageStep", out step) ? step.GetInt64() : 0;
            for (var k = 0; k < count; k++)
            {
                entries[first + k] = new SlotEntry(
                    Kind(Text(run, "state")),
                    new WalPage(UInt64(run, "file") + (ulong)(k * fileStep), (uint)(run.GetProperty("page").GetUInt32() + k * pageStep)));
            }
        }

        return entries;
    }

    private static CheckpointRef Reference(JsonElement element) =>
        new(UInt64(element, "generation"), UInt64(element, "offset"), element.GetProperty("length").GetUInt32(), uint.Parse(Text(element, "crc"), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static CheckpointRoot Root(JsonElement vector)
    {
        var replayStart = vector.GetProperty("replayStart");
        var corruptionEnd = vector.GetProperty("corruptionEnd");
        return new CheckpointRoot(
            UInt64(vector, "generation"),
            vector.GetProperty("height").GetInt32(),
            new WalPosition(UInt64(replayStart, "file"), UInt64(replayStart, "offset")),
            corruptionEnd.ValueKind == JsonValueKind.Null ? null : new WalPage(UInt64(corruptionEnd, "file"), corruptionEnd.GetProperty("page").GetUInt32()),
            vector.GetProperty("sealed").GetBoolean(),
            Reference(vector.GetProperty("root")));
    }

    private static async Task CreateFileAsync(WalDirectory directory, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(bytes);
        using var contents = writer.ConsumeSlice(bytes.Length);
        await (await directory.CreateNamedFileAsync(name, contents, cancellationToken)).DisposeAsync();
    }

    private static byte[] Expand(byte[] prefix, int length)
    {
        var bytes = new byte[length];
        prefix.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Trim(ReadOnlySpan<byte> bytes) => bytes[..(bytes.LastIndexOfAnyExcept((byte)0) + 1)].ToArray();

    /// <summary>
    /// Returns a directory of a fresh simulated storage which holds one named file, <paramref name="name"/>, holding
    /// <paramref name="contents"/>.
    /// </summary>
    private static async Task<WalDirectory> DirectoryWithAsync(string name, byte[] contents)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync("wal", cancellationToken);
        var directory = await storage.OpenDirectoryAsync("wal", cancellationToken);
        using var writer = new ArcBufferWriter();
        writer.Write(contents);
        using var buffer = writer.ConsumeSlice(contents.Length);
        await (await directory.CreateNamedFileAsync(name, buffer, cancellationToken)).DisposeAsync();
        return directory;
    }
}

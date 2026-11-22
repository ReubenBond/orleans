using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Simulation;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// A slot-index checkpoint as recovery loads it (ledgerstore.md §7.4, §8.8): a root slot and two generation files
/// holding its interior nodes and leaves. The input is the root slot's first 80 bytes, its fields; then, when the input
/// continues, the length of file A (<c>u32</c>), file A, and file B, the rest. File A has the root reference's
/// generation (or the root's own when the reference is null), and file B the generation below it (or 2 when A's is 1),
/// so a tree can reference objects across generations.
/// </summary>
/// <remarks>
/// The loader validates structurally, so a round trip through the writer would not see a leaf it dropped or misplaced.
/// The oracle walks the input's bytes under the format's rules, independently of the loader: the root slot's fields;
/// every node 6,144 bytes, within its file, CRC-valid, with all-zero null references and a non-null child; every leaf
/// reference of a length some leaf has, within its file, and numbered within the slot space; and every object in its root's
/// generation or an older one, below 2<sup>63</sup>, and disjoint from the tree's other objects. The loader must load exactly the walk's
/// leaves, or find no usable root exactly when the walk finds a rule broken, rejecting a usable root's tree for the reason
/// of the first rule the walk finds it breaks, a reason <c>test/Durable.Storage/shared-vectors/ledgerstore/tree-rejections.json</c>
/// names, and the byte ranges it reads while loading must be pairwise disjoint. Each
/// leaf then reads as its bytes or fails its CRC, and the writer preserves what was loaded.
/// </remarks>
public sealed class CheckpointGenerationTarget : FuzzTarget
{
    private const int SlotSize = CheckpointRoot.MinimumSlotSize;
    private const int FilesOffset = CheckpointRoot.FieldsLength + sizeof(uint);
    private const string Directory = "slots";
    private const long MaximumLeaf = long.MaxValue >> SlotLeafCodec.SlotBits;
    private static readonly byte[] Magic = "CLSI"u8.ToArray();

    /// <summary>
    /// The reasons of <c>test/Durable.Storage/shared-vectors/ledgerstore/tree-rejections.json</c>.
    /// </summary>
    private static readonly Lazy<HashSet<string>> TreeReasons = new(static () => GoldenVectors.Find().Reasons("ledgerstore", "tree-rejections.json"));

    public override string Name => "ledgerstore-generation";

    public override int MaxLength => 32 * 1024;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return SeedsOf(vectors);
    }

    private static IEnumerable<byte[]> SeedsOf(GoldenVectors vectors)
    {
        foreach (var vector in vectors.Cases("ledgerstore", "generations.json"))
        {
            var root = GoldenVectors.Expand(GoldenVectors.Hex(vector, "rootFileHex"), CheckpointRoot.FieldsLength)[..CheckpointRoot.FieldsLength];
            var file = GoldenVectors.Expand(GoldenVectors.Hex(vector, "fileHex"), vector.GetProperty("fileLength").GetInt32());
            var length = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)file.Length);

            // The generation alone, and with a copy of it as file B, so changing a reference's generation reaches B.
            yield return [.. root, .. length, .. file];
            yield return [.. root, .. length, .. file, .. file];
        }

        // Trees which break a rule, each with its generation file as file A, the root reference's generation.
        foreach (var vector in vectors.Cases("ledgerstore", "tree-rejections.json"))
        {
            var root = GoldenVectors.Expand(GoldenVectors.Hex(vector, "rootFileHex"), CheckpointRoot.FieldsLength)[..CheckpointRoot.FieldsLength];
            var file = GoldenVectors.Expand(GoldenVectors.Hex(vector, "fileHex"), vector.GetProperty("fileLength").GetInt32());
            var length = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)file.Length);
            yield return [.. root, .. length, .. file];
        }

        // Roots alone, whose references name a generation file which does not exist.
        foreach (var vector in vectors.Cases("ledgerstore", "roots.json").Where(static vector => vector.GetProperty("slotSize").GetInt32() == SlotSize))
        {
            yield return GoldenVectors.Expand(GoldenVectors.Hex(vector), CheckpointRoot.FieldsLength)[..CheckpointRoot.FieldsLength];
        }
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var (slot, files) = Layout(input);
        var storage = new SimulatedWalStorage();
        Completed(storage.CreateDirectoryAsync(Directory, CancellationToken.None));
        var directory = Completed(storage.OpenDirectoryAsync(Directory, CancellationToken.None));
        try
        {
            Create(directory, SlotCheckpoints.RootName, [.. slot, .. new byte[SlotSize]]);
            foreach (var (generation, bytes) in files)
            {
                Create(directory, SlotCheckpoints.FileName(generation), bytes);
            }

            var expected = Walk(slot, files);

            // Loading reads each object at most once and never reads bytes another object owns, so the byte ranges it
            // reads are pairwise disjoint (ledgerstore.md §8.8).
            var reads = new List<(string Name, long Start, long End)>();
            var overlapping = false;
            storage.NamedReadFault = (_, name, offset, length) =>
            {
                overlapping |= reads.Exists(read => read.Name == name && read.Start < offset + length && offset < read.End);
                reads.Add((name, offset, offset + length));
                return null;
            };

            var bound = MeasureAllocations(2L * SlotSize + expected.ObjectBytes);
            var (checkpoints, loaded) = Completed(SlotCheckpoints.OpenAsync(directory, CancellationToken.None));
            bound.Check("SlotCheckpoints.OpenAsync");
            storage.NamedReadFault = null;
            try
            {
                Require(!overlapping, "read overlapping byte ranges of the checkpoint while loading it.");
                string?[] treeReasons = [.. checkpoints.TreeRejections.Select(static rejection => DecodeRejection.ReasonOf(rejection))];
                Require(
                    treeReasons.All(static reason => reason is not null && TreeReasons.Value.Contains(reason)),
                    $"rejected trees for [{string.Join(", ", treeReasons)}], which the tree vectors do not all name.");
                string?[] treeRules = expected.TreeRule is { } treeRule ? [treeRule] : [];
                Require(
                    treeReasons.SequenceEqual(treeRules),
                    $"rejected trees for [{string.Join(", ", treeReasons)}] where the format's rules give [{string.Join(", ", treeRules)}].");
                Require(
                    checkpoints.FoundDamage == expected.Damage,
                    expected.Damage ? "found no damage where the format's rules find some." : "found damage where the format's rules find none.");
                if (expected is not { Root: { } root, Leaves: { } leaves })
                {
                    Require(loaded is null, $"loaded a checkpoint whose root or tree breaks a rule: {expected.Violation}.");
                    return FuzzOutcome.Rejected;
                }

                Require(loaded is not null, "found no usable root where the format's rules accept the root and its tree.");
                Require(
                    loaded.Generation == root.Generation && loaded.ReplayStart == root.ReplayStart && loaded.CorruptionEnd == root.CorruptionEnd
                    && loaded.Sealed == root.Sealed,
                    "loaded a checkpoint whose fields differ from its root's.");
                Require(
                    loaded.Leaves.Count == leaves.Count
                    && loaded.Leaves.All(leaf => leaves.TryGetValue(leaf.Key, out var reference) && reference == leaf.Value),
                    "loaded leaves other than the ones its tree references.");

                RequireWriterPreserves(loaded, ReadLeaves(checkpoints, loaded, files));
                return FuzzOutcome.Accepted;
            }
            finally
            {
                Completed(checkpoints.DisposeAsync());
            }
        }
        finally
        {
            Completed(directory.DisposeAsync());
        }
    }

    /// <summary>
    /// Recomputes, bottom-up, the CRC-32C of every object the root's tree references within the generation files, then
    /// the root slot's CRC-32C, so the fuzzer reaches the interior nodes and leaves behind them.
    /// </summary>
    protected override byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (input.Length < CheckpointRoot.FieldsLength)
        {
            return null;
        }

        var bytes = input.ToArray();
        var (slot, files) = Layout(input);
        var starts = new Dictionary<ulong, int>();
        var start = FilesOffset;
        foreach (var (generation, file) in files)
        {
            starts[generation] = start;
            start += file.Length;
        }

        var sealedReferences = new HashSet<int>();
        void SealReference(int position, int level)
        {
            var reference = bytes.AsSpan(position, CheckpointRef.Size);
            var referencedGeneration = BinaryPrimitives.ReadUInt64LittleEndian(reference[CheckpointRef.GenerationOffset..]);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(reference[CheckpointRef.OffsetOffset..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(reference[CheckpointRef.LengthOffset..]);
            if (!files.TryGetValue(referencedGeneration, out var file)
                || offset > (ulong)file.Length
                || length > (ulong)file.Length - offset
                || !sealedReferences.Add(position))
            {
                return;
            }

            var target = starts[referencedGeneration] + (int)offset;
            if (level > 0 && length == SlotCheckpoints.NodeSize)
            {
                for (var child = 0; child < SlotLeafCodec.Slots; child++)
                {
                    SealReference(target + child * CheckpointRef.Size, level - 1);
                }
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + CheckpointRef.CrcOffset), Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes.AsSpan(target, (int)length)));
        }

        var height = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(CheckpointRoot.HeightOffset)), CheckpointRoot.MaximumHeight);
        SealReference(CheckpointRoot.RootOffset, height);
        bytes.AsSpan(0, CheckpointRoot.FieldsLength).CopyTo(slot);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, slot.AsSpan(CheckpointRoot.MagicOffset)));
        return bytes;
    }

    /// <summary>
    /// Returns the root slot an input stands for and its generation files by generation.
    /// </summary>
    private static (byte[] Slot, Dictionary<ulong, byte[]> Files) Layout(ReadOnlySpan<byte> input)
    {
        var slot = new byte[SlotSize];
        input[..Math.Min(input.Length, CheckpointRoot.FieldsLength)].CopyTo(slot);
        var files = new Dictionary<ulong, byte[]>();
        if (input.Length >= FilesOffset)
        {
            var rest = input[FilesOffset..];
            var lengthA = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(input[CheckpointRoot.FieldsLength..]), (uint)rest.Length);
            var referenced = BinaryPrimitives.ReadUInt64LittleEndian(slot.AsSpan(CheckpointRoot.RootOffset + CheckpointRef.GenerationOffset));
            var generationA = referenced != 0 ? referenced : Math.Max(1, BinaryPrimitives.ReadUInt64LittleEndian(slot.AsSpan(CheckpointRoot.GenerationOffset)));
            files[generationA] = rest[..lengthA].ToArray();
            files[generationA > 1 ? generationA - 1 : 2] = rest[lengthA..].ToArray();
        }

        return (slot, files);
    }

    /// <summary>
    /// Applies the format's rules to a root slot and its generation files (ledgerstore.md §7.4, §8.8).
    /// </summary>
    private static Expectation Walk(byte[] slot, Dictionary<ulong, byte[]> files)
    {
        var outOfRange = files.Keys.Any(static generation => generation >= CheckpointRoot.GenerationLimit);
        var (root, violation) = ReadRoot(slot);
        if (root is null)
        {
            return new Expectation(null, null, Damage: true, violation, TreeRule: null, ObjectBytes: 0);
        }

        var walk = new TreeWalk(files, root.Generation);
        var treeRule = root.Root.IsNull ? null : walk.Node(root.Root, root.Height, 0);
        return treeRule is null
            ? new Expectation(root, walk.Leaves, outOfRange, null, TreeRule: null, walk.ObjectBytes)
            : new Expectation(null, null, Damage: true, $"its tree breaks '{treeRule}'", treeRule, walk.ObjectBytes);
    }

    /// <summary>
    /// Reads a root slot's fields under the rules for a usable root, returning the rule it breaks when it is unusable.
    /// </summary>
    private static (CheckpointRoot? Root, string? Violation) ReadRoot(ReadOnlySpan<byte> slot)
    {
        if (RootViolation(slot) is { } violation)
        {
            return (null, violation);
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(slot[CheckpointRoot.FlagsOffset..]);
        WalPage? corruptionEnd = (flags & CheckpointRoot.CorruptionEndFlag) != 0
            ? new WalPage(
                BinaryPrimitives.ReadUInt64LittleEndian(slot[CheckpointRoot.CorruptionFileOffset..]),
                BinaryPrimitives.ReadUInt32LittleEndian(slot[CheckpointRoot.CorruptionPageOffset..]))
            : null;
        var replayStart = new WalPosition(
            BinaryPrimitives.ReadUInt64LittleEndian(slot[CheckpointRoot.ReplayStartOffset..]),
            BinaryPrimitives.ReadUInt64LittleEndian(slot[(CheckpointRoot.ReplayStartOffset + sizeof(ulong))..]));
        return (
            new CheckpointRoot(
                BinaryPrimitives.ReadUInt64LittleEndian(slot[CheckpointRoot.GenerationOffset..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(slot[CheckpointRoot.HeightOffset..]),
                replayStart,
                corruptionEnd,
                (flags & CheckpointRoot.SealedFlag) != 0,
                ReadReference(slot.Slice(CheckpointRoot.RootOffset, CheckpointRef.Size))),
            null);
    }

    private static string? RootViolation(ReadOnlySpan<byte> slot)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(slot) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, slot[CheckpointRoot.MagicOffset..]))
        {
            return "the root fails its CRC";
        }

        if (!slot[CheckpointRoot.MagicOffset..CheckpointRoot.VersionOffset].SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(slot[CheckpointRoot.VersionOffset..]) != 1)
        {
            return "the root is not CLSI version 1";
        }

        var height = BinaryPrimitives.ReadUInt32LittleEndian(slot[CheckpointRoot.HeightOffset..]);
        var referenceBytes = slot.Slice(CheckpointRoot.RootOffset, CheckpointRef.Size);
        var reference = ReadReference(referenceBytes);
        if (height > CheckpointRoot.MaximumHeight)
        {
            return "the root's height is above 7";
        }

        if (reference.Generation == 0 && referenceBytes.ContainsAnyExcept((byte)0))
        {
            return "the root's null reference has nonzero fields";
        }

        if (height == 0 != (reference.Generation == 0))
        {
            return "the root's height disagrees with its reference";
        }

        var generation = BinaryPrimitives.ReadUInt64LittleEndian(slot[CheckpointRoot.GenerationOffset..]);
        if (generation == 0 || generation >= CheckpointRoot.GenerationLimit || reference.Generation >= CheckpointRoot.GenerationLimit)
        {
            return "the root names a generation of zero, or at or above 2^63";
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(slot[CheckpointRoot.FlagsOffset..]);
        if ((flags & ~(CheckpointRoot.CorruptionEndFlag | CheckpointRoot.SealedFlag)) != 0)
        {
            return "the root has unknown flags";
        }

        return (flags & CheckpointRoot.CorruptionEndFlag) == 0 && slot[CheckpointRoot.CorruptionFileOffset..CheckpointRoot.FlagsOffset].ContainsAnyExcept((byte)0)
            ? "the root has a corruption end without its flag"
            : null;
    }

    private static CheckpointRef ReadReference(ReadOnlySpan<byte> bytes) =>
        new(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[CheckpointRef.GenerationOffset..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[CheckpointRef.OffsetOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[CheckpointRef.LengthOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[CheckpointRef.CrcOffset..]));

    private static void Create(WalDirectory directory, string name, byte[] contents)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(contents);
        var buffer = writer.ConsumeSlice(contents.Length);
        try
        {
            Completed(Completed(directory.CreateNamedFileAsync(name, buffer, CancellationToken.None)).DisposeAsync());
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Returns the result of an operation over <see cref="SimulatedWalStorage"/>, which completes synchronously.
    /// </summary>
    private static T Completed<T>(ValueTask<T> operation)
    {
        if (operation.IsCompletedSuccessfully)
        {
            return operation.Result;
        }

        var task = operation.AsTask();
        return task.IsCompleted
            ? task.GetAwaiter().GetResult()
            : throw new InvalidOperationException("A simulated storage operation did not complete synchronously.");
    }

    private static void Completed(ValueTask operation)
    {
        if (operation.IsCompletedSuccessfully)
        {
            return;
        }

        var task = operation.AsTask();
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException("A simulated storage operation did not complete synchronously.");
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Reads each loaded leaf: its bytes in its file when they match its CRC, and otherwise a failure, after which
    /// recovery recomputes the leaf from the WAL (LS-I3).
    /// </summary>
    private List<CheckpointLeaf> ReadLeaves(SlotCheckpoints checkpoints, LoadedCheckpoint loaded, Dictionary<ulong, byte[]> files)
    {
        var leaves = new List<CheckpointLeaf>();
        foreach (var (leaf, reference) in loaded.Leaves.OrderBy(static leaf => leaf.Key))
        {
            var expected = files[reference.Generation].AsSpan((int)reference.Offset, (int)reference.Length);
            var bound = MeasureAllocations(reference.Length);
            var bytes = new byte[reference.Length];
            var unreadable = Completed(checkpoints.ReadObjectAsync(reference, bytes, CancellationToken.None));
            bound.Check("SlotCheckpoints.ReadObjectAsync");
            var matches = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, expected) == reference.Crc;
            if (unreadable is { } rejection)
            {
                Require(!matches, string.Create(CultureInfo.InvariantCulture, $"failed to read leaf {leaf}, whose bytes match its CRC."));
                Require(
                    DecodeRejection.ReasonOf(rejection) == "crc-mismatch",
                    string.Create(CultureInfo.InvariantCulture, $"failed to read leaf {leaf} for '{DecodeRejection.ReasonOf(rejection)}', not its CRC."));
                continue;
            }

            Require(matches, string.Create(CultureInfo.InvariantCulture, $"read leaf {leaf}, whose bytes fail its CRC."));
            Require(bytes.AsSpan().SequenceEqual(expected), string.Create(CultureInfo.InvariantCulture, $"read leaf {leaf} as bytes other than its reference names."));
            leaves.Add(new CheckpointLeaf(leaf, bytes));
        }

        return leaves;
    }

    /// <summary>
    /// Writes the readable leaves as a new checkpoint at the loaded replay start and corruption bound, loads it, and
    /// requires the same fields and leaves.
    /// </summary>
    private void RequireWriterPreserves(LoadedCheckpoint loaded, List<CheckpointLeaf> leaves)
    {
        var storage = new SimulatedWalStorage();
        Completed(storage.CreateDirectoryAsync(Directory, CancellationToken.None));
        var directory = Completed(storage.OpenDirectoryAsync(Directory, CancellationToken.None));
        try
        {
            var writer = SlotCheckpoints.Create(directory);
            try
            {
                _ = Completed(writer.WriteAsync(loaded.ReplayStart, loaded.CorruptionEnd, loaded.Sealed, leaves, CancellationToken.None));
            }
            finally
            {
                Completed(writer.DisposeAsync());
            }

            var (checkpoints, reloaded) = Completed(SlotCheckpoints.OpenAsync(directory, CancellationToken.None));
            try
            {
                Require(
                    reloaded is not null && reloaded.ReplayStart == loaded.ReplayStart && reloaded.CorruptionEnd == loaded.CorruptionEnd
                    && reloaded.Sealed == loaded.Sealed,
                    "loaded fields which the writer does not preserve.");
                Require(
                    reloaded.Leaves.Keys.Order().SequenceEqual(leaves.Select(static leaf => leaf.Leaf)),
                    "loaded leaves which the writer does not preserve.");
                foreach (var leaf in leaves)
                {
                    var bytes = new byte[reloaded.Leaves[leaf.Leaf].Length];
                    Require(
                        Completed(checkpoints.ReadObjectAsync(reloaded.Leaves[leaf.Leaf], bytes, CancellationToken.None)) is null
                        && bytes.AsSpan().SequenceEqual(leaf.Bytes),
                        "loaded a leaf whose bytes the writer does not preserve.");
                }
            }
            finally
            {
                Completed(checkpoints.DisposeAsync());
            }
        }
        finally
        {
            Completed(directory.DisposeAsync());
        }
    }

    /// <summary>
    /// What the format's rules give for an input: the usable root and its tree's leaves, or the rule the root or its tree
    /// breaks, with the reason code of the tree's; whether loading finds damage; and the bytes of the objects loading
    /// reaches.
    /// </summary>
    private sealed record Expectation(CheckpointRoot? Root, Dictionary<long, CheckpointRef>? Leaves, bool Damage, string? Violation, string? TreeRule, long ObjectBytes);

    /// <summary>
    /// Walks a tree depth-first in child order, as loading does, placing each object it reaches and stopping at the first
    /// rule broken, which it names by its reason code (ledgerstore.md §7.4).
    /// </summary>
    private sealed class TreeWalk(Dictionary<ulong, byte[]> files, ulong rootGeneration)
    {
        private readonly List<(ulong Generation, ulong Start, ulong End)> _placed = [];

        public Dictionary<long, CheckpointRef> Leaves { get; } = [];

        public long ObjectBytes { get; private set; }

        /// <summary>
        /// Walks the interior node <paramref name="reference"/> names at <paramref name="level"/>, returning the rule it
        /// or a node or leaf beneath it breaks.
        /// </summary>
        public string? Node(CheckpointRef reference, int level, long prefix)
        {
            if (reference.Length != SlotCheckpoints.NodeSize)
            {
                return "node-length";
            }

            if (Place(reference) is { } misplaced)
            {
                return misplaced;
            }

            var node = files[reference.Generation].AsSpan((int)reference.Offset, SlotCheckpoints.NodeSize);
            if (Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, node) != reference.Crc)
            {
                return "crc-mismatch";
            }

            var children = new List<(int Index, CheckpointRef Child)>();
            for (var index = 0; index < SlotLeafCodec.Slots; index++)
            {
                var bytes = node.Slice(index * CheckpointRef.Size, CheckpointRef.Size);
                var child = ReadReference(bytes);
                if (child.Generation != 0)
                {
                    children.Add((index, child));
                }
                else if (bytes.ContainsAnyExcept((byte)0))
                {
                    return "null-reference-fields";
                }
            }

            if (children.Count == 0)
            {
                return "node-empty";
            }

            foreach (var (index, child) in children)
            {
                var path = (prefix << SlotLeafCodec.SlotBits) | (long)index;
                var problem = level > 1 ? Node(child, level - 1, path) : Leaf(child, path);
                if (problem is not null)
                {
                    return problem;
                }
            }

            return null;
        }

        private string? Leaf(CheckpointRef reference, long leaf)
        {
            if (leaf > MaximumLeaf)
            {
                return "leaf-beyond-address-space";
            }

            if (reference.Length < SlotLeafCodec.MinimumLength || reference.Length > SlotLeafCodec.MaximumLength)
            {
                return "leaf-length";
            }

            if (Place(reference) is { } misplaced)
            {
                return misplaced;
            }

            Leaves.Add(leaf, reference);
            return null;
        }

        /// <summary>
        /// Places an object in its file, returning the rule its placement breaks.
        /// </summary>
        private string? Place(CheckpointRef reference)
        {
            // A usable root's generation lies below 2^63, so an object in it or an older one does too.
            if (reference.Generation > rootGeneration)
            {
                return "generation-after-root";
            }

            if (!files.TryGetValue(reference.Generation, out var file))
            {
                return "generation-missing";
            }

            if (reference.Offset > (ulong)file.Length || reference.Length > (ulong)file.Length - reference.Offset)
            {
                return "outside-file";
            }

            var end = reference.Offset + reference.Length;
            if (_placed.Exists(other => other.Generation == reference.Generation && other.Start < end && reference.Offset < other.End))
            {
                return "overlap";
            }

            _placed.Add((reference.Generation, reference.Offset, end));
            ObjectBytes += reference.Length;
            return null;
        }
    }
}

using System.Globalization;
using System.IO.Hashing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Loads slot-index checkpoints whose files a crash, an operator, damage, or a failed write left inconsistent
/// (ledgerstore.md §6.2, §7.4, §8.8): loading either uses a root or reports the checkpoint unusable, so recovery falls
/// back to the older root or to the WAL, and their content never fails the store (LS-I3); a failed read or write is
/// reported, which stops the process (conventions.md §14), and the restarted process loads what the device holds.
/// </summary>
public sealed class SlotCheckpointLoadTests : IDisposable
{
    private const string SlotsPath = "slots";

    /// <summary>
    /// The root slot size <c>S</c> over <see cref="SimulatedWalStorage"/>, whose minimum page size is below 4096.
    /// </summary>
    private const int RootSlotSize = CheckpointRoot.MinimumSlotSize;

    private const int NodeSize = SlotCheckpoints.NodeSize;

    /// <summary>
    /// The checkpoint directory on a <see cref="RecordingFileSystem"/>, whose held writes fail once their bytes reach the
    /// device.
    /// </summary>
    private static readonly string RecordedSlotsPath = Path.Combine(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-checkpoints")), SlotsPath);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AMissingGenerationFileMakesTheRootsReferencingItUnusable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = await TwoGenerationsAsync();
        await using var directory = await storage.OpenDirectoryAsync(SlotsPath, cancellationToken);
        await directory.DeleteNamedFileAsync(SlotCheckpoints.FileName(2), cancellationToken);
        var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
        await using (checkpoints)
        {
            Assert.True(checkpoints.FoundDamage);
            Assert.Equal(["generation-missing"], Reasons(checkpoints));
            Assert.NotNull(loaded);
            Assert.Equal(1ul, loaded.Generation);
            Assert.Equal(new WalPosition(1, 0), loaded.ReplayStart);
            Assert.Equal([0L], loaded.Leaves.Keys);
        }

        await directory.DeleteNamedFileAsync(SlotCheckpoints.FileName(1), cancellationToken);
        (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
        await using (checkpoints)
        {
            Assert.True(checkpoints.FoundDamage);
            Assert.Equal(["generation-missing", "generation-missing"], Reasons(checkpoints));
            Assert.Null(loaded);
        }
    }

    /// <summary>
    /// Conventions.md §14: an I/O error reading an object of a tree is no damage (LS-I3). It is reported, which stops the
    /// process, and the load fails; the restarted process reads the object and loads the newest root.
    /// </summary>
    [Fact]
    public async Task AnUnreadableObjectFailsTheLoadAndTheRestartedProcessLoadsTheNewestRoot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = await TwoGenerationsAsync();
        var reported = FatalErrors.ReportedCount;
        storage.NamedReadFault = (_, name, _, _) => name == SlotCheckpoints.FileName(2) ? new IOException("The device failed the read.") : null;
        await using (var directory = await storage.OpenDirectoryAsync(SlotsPath, cancellationToken))
        {
            _ = await Assert.ThrowsAsync<IOException>(async () => await SlotCheckpoints.OpenAsync(directory, cancellationToken));
        }

        _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleReadScatter, SlotsPath);
        var restarted = storage.Crash(operatingSystem: false);
        await using var reopened = await restarted.OpenDirectoryAsync(SlotsPath, cancellationToken);
        var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(reopened, cancellationToken);
        await using (checkpoints)
        {
            Assert.False(checkpoints.FoundDamage);
            Assert.Equal(2ul, loaded?.Generation);
        }
    }

    /// <summary>
    /// Generations lie below 2<sup>63</sup> (ledgerstore.md §7.4). A root slot or reference at or above the limit is
    /// unusable, and a generation file at or above it is deleted; neither counts toward the next generation.
    /// </summary>
    [Theory]
    [InlineData("root")]
    [InlineData("reference")]
    [InlineData("file")]
    public async Task AGenerationAtOrAboveTheLimitIsDamage(string where)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new SimulatedWalStorage();
        await using var directory = await CreateSlotsAsync(storage, cancellationToken);
        await using (var writer = SlotCheckpoints.Create(directory))
        {
            _ = await writer.WriteAsync(new WalPosition(1, 0), null, isSealed: false, [new CheckpointLeaf(0, Leaf(1))], cancellationToken);
        }

        var stray = SlotCheckpoints.FileName(CheckpointRoot.GenerationLimit);
        if (where == "file")
        {
            await CreateFileAsync(directory, stray, Leaf(2), cancellationToken);
        }
        else
        {
            var root = storage.ReadDurableFile(SlotsPath, SlotCheckpoints.RootName)!;
            var reference = where == "reference" ? new CheckpointRef(CheckpointRoot.GenerationLimit, 0, NodeSize, 0) : default;
            new CheckpointRoot(where == "root" ? CheckpointRoot.GenerationLimit : 2, reference.IsNull ? 0 : 1, new WalPosition(1, 0), null, Sealed: false, reference)
                .Write(root.AsSpan(RootSlotSize, RootSlotSize));
            await directory.DeleteNamedFileAsync(SlotCheckpoints.RootName, cancellationToken);
            await CreateFileAsync(directory, SlotCheckpoints.RootName, root, cancellationToken);
        }

        var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
        await using (checkpoints)
        {
            Assert.True(checkpoints.FoundDamage);
            Assert.Equal(1ul, loaded?.Generation);
            Assert.DoesNotContain(stray, await directory.ListNamedFilesAsync(SlotCheckpoints.Extension, cancellationToken));
            _ = await checkpoints.WriteAsync(new WalPosition(1, 4096), null, isSealed: false, [new CheckpointLeaf(0, Leaf(3))], cancellationToken);
            Assert.Equal(2ul, checkpoints.Generation);
        }
    }

    /// <summary>
    /// Generations lie below 2<sup>63</sup>, so the last is 2<sup>63</sup> − 1 (LS-I9). A checkpoint written at the last
    /// generation loads again like any other, and no checkpoint follows it: the next write fails before it writes
    /// anything.
    /// </summary>
    [Fact]
    public async Task TheLastGenerationLoadsAndNoCheckpointFollowsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new SimulatedWalStorage();
        var slots = new byte[2 * RootSlotSize];
        new CheckpointRoot(CheckpointRoot.GenerationLimit - 2, 0, new WalPosition(1, 0), null, Sealed: false, default).Write(slots.AsSpan(0, RootSlotSize));
        await using var directory = await CreateSlotsAsync(storage, cancellationToken);
        await CreateFileAsync(directory, SlotCheckpoints.RootName, slots, cancellationToken);

        var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
        await using (checkpoints)
        {
            Assert.Equal(CheckpointRoot.GenerationLimit - 2, loaded?.Generation);
            Assert.False(checkpoints.Exhausted);
            _ = await checkpoints.WriteAsync(new WalPosition(1, 4096), null, isSealed: false, [new CheckpointLeaf(0, Leaf(1))], cancellationToken);
            Assert.Equal(CheckpointRoot.GenerationLimit - 1, checkpoints.Generation);
            Assert.True(checkpoints.Exhausted);

            // Even a checkpoint which would change nothing fails: exhaustion is checked first.
            _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await checkpoints.WriteAsync(new WalPosition(1, 4096), null, isSealed: false, [], cancellationToken));
        }

        (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
        await using (checkpoints)
        {
            Assert.False(checkpoints.FoundDamage);
            Assert.NotNull(loaded);
            Assert.Equal(CheckpointRoot.GenerationLimit - 1, loaded.Generation);
            Assert.Equal(new WalPosition(1, 4096), loaded.ReplayStart);
            Assert.Equal([0L], loaded.Leaves.Keys);
            Assert.True(checkpoints.Exhausted);

            var root = storage.ReadDurableFile(SlotsPath, SlotCheckpoints.RootName);
            var files = storage.ListNamedFiles(SlotsPath);
            _ = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await checkpoints.WriteAsync(new WalPosition(1, 8192), null, isSealed: false, [new CheckpointLeaf(0, Leaf(2))], cancellationToken));
            Assert.Equal(root, storage.ReadDurableFile(SlotsPath, SlotCheckpoints.RootName));
            Assert.Equal(files, storage.ListNamedFiles(SlotsPath));
            Assert.Equal(CheckpointRoot.GenerationLimit - 1, checkpoints.Generation);
            _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await checkpoints.WriteAsync(new WalPosition(1, 4096), null, isSealed: false, [], cancellationToken));
        }
    }

    /// <summary>
    /// Only the root slots count toward the next generation: a generation file no loaded root references is a leftover,
    /// deleted on open whatever its generation, so a stray file named the last generation does not exhaust the
    /// generations (LS-I9).
    /// </summary>
    [Fact]
    public async Task AStrayFileAtTheLastGenerationIsDeletedAndDoesNotExhaust()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new SimulatedWalStorage();
        var slots = new byte[2 * RootSlotSize];
        new CheckpointRoot(1, 0, new WalPosition(1, 0), null, Sealed: false, default).Write(slots.AsSpan(0, RootSlotSize));
        var stray = SlotCheckpoints.FileName(CheckpointRoot.GenerationLimit - 1);
        await using var directory = await CreateSlotsAsync(storage, cancellationToken);
        await CreateFileAsync(directory, SlotCheckpoints.RootName, slots, cancellationToken);
        await CreateFileAsync(directory, stray, Leaf(1), cancellationToken);

        var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
        await using (checkpoints)
        {
            Assert.Equal(1UL, loaded?.Generation);
            Assert.False(checkpoints.Exhausted);
            Assert.DoesNotContain(stray, storage.ListNamedFiles(SlotsPath));
            _ = await checkpoints.WriteAsync(new WalPosition(1, 4096), null, isSealed: false, [new CheckpointLeaf(0, Leaf(2))], cancellationToken);
            Assert.Equal(2UL, checkpoints.Generation);
        }
    }

    /// <summary>
    /// A generation is taken once its root is durable. A write of the last generation which fails is reported, which
    /// stops the process (conventions.md §14), and leaves the generations unexhausted: the restarted process keeps the
    /// root in force, deletes the file the failed write left, so no root slot or file names the generation, and takes it
    /// (LS-I9).
    /// </summary>
    [Fact]
    public async Task AFailedWriteOfTheLastGenerationIsTakenByTheRestartedProcess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new SimulatedWalStorage();
        var slots = new byte[2 * RootSlotSize];
        new CheckpointRoot(CheckpointRoot.GenerationLimit - 2, 0, new WalPosition(1, 0), null, Sealed: false, default).Write(slots.AsSpan(0, RootSlotSize));
        new CheckpointRoot(CheckpointRoot.GenerationLimit - 3, 0, new WalPosition(1, 0), null, Sealed: false, default).Write(slots.AsSpan(RootSlotSize, RootSlotSize));
        var last = SlotCheckpoints.FileName(CheckpointRoot.GenerationLimit - 1);
        await using (var directory = await CreateSlotsAsync(storage, cancellationToken))
        {
            await CreateFileAsync(directory, SlotCheckpoints.RootName, slots, cancellationToken);
            var (checkpoints, _) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
            await using (checkpoints)
            {
                var reported = FatalErrors.ReportedCount;
                storage.NamedWriteFault = (_, name, _, _) => name == SlotCheckpoints.RootName ? new IOException("injected root failure") : null;
                _ = await Assert.ThrowsAsync<IOException>(
                    async () => await checkpoints.WriteAsync(new WalPosition(1, 4096), null, isSealed: false, [new CheckpointLeaf(0, Leaf(1))], cancellationToken));
                _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleWriteGather, SlotsPath);
                Assert.False(checkpoints.Exhausted);
                Assert.Equal(CheckpointRoot.GenerationLimit - 2, checkpoints.Generation);
                Assert.Contains(last, storage.ListNamedFiles(SlotsPath));
            }
        }

        storage = storage.Crash(operatingSystem: false);
        await using var reopened = await storage.OpenDirectoryAsync(SlotsPath, cancellationToken);
        var (restarted, loaded) = await SlotCheckpoints.OpenAsync(reopened, cancellationToken);
        await using (restarted)
        {
            Assert.False(restarted.FoundDamage);
            Assert.Equal(CheckpointRoot.GenerationLimit - 2, loaded?.Generation);
            Assert.False(restarted.Exhausted);
            Assert.DoesNotContain(last, storage.ListNamedFiles(SlotsPath));
            _ = await restarted.WriteAsync(new WalPosition(1, 4096), null, isSealed: false, [new CheckpointLeaf(0, Leaf(1))], cancellationToken);
            Assert.Equal(CheckpointRoot.GenerationLimit - 1, restarted.Generation);
            Assert.True(restarted.Exhausted);
        }

        (restarted, loaded) = await SlotCheckpoints.OpenAsync(reopened, cancellationToken);
        await using (restarted)
        {
            Assert.False(restarted.FoundDamage);
            Assert.Equal(CheckpointRoot.GenerationLimit - 1, loaded?.Generation);
            Assert.True(restarted.Exhausted);
        }
    }

    /// <summary>
    /// A root write which fails once its bytes reach the device, as a device error reports one, is reported, which stops
    /// the process (conventions.md §14), and leaves a usable root slot naming a generation no write took. The restarted
    /// process loads it, so its next write takes the generation after it (LS-I9).
    /// </summary>
    [Fact]
    public async Task AFailedWriteWhoseRootReachedTheDeviceLoadsOnReopen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fileSystem = new RecordingFileSystem(Path.GetDirectoryName(RecordedSlotsPath)!, writeThrough: true);
        await using (var directory = await CreateSlotsAsync(fileSystem.Storage, RecordedSlotsPath, cancellationToken))
        {
            await using var failed = SlotCheckpoints.Create(directory);
            await FailGenerationTwoOnceItsRootReachesTheDeviceAsync(fileSystem, failed, cancellationToken);
        }

        fileSystem = fileSystem.Restart();
        await using var reopened = await fileSystem.Storage.OpenDirectoryAsync(RecordedSlotsPath, cancellationToken);
        var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(reopened, cancellationToken);
        await using (checkpoints)
        {
            Assert.False(checkpoints.FoundDamage);
            Assert.Equal(2UL, loaded?.Generation);
            Assert.Equal(new WalPosition(1, 4096), loaded!.ReplayStart);
            Assert.Equal([0L, 1L], loaded.Leaves.Keys.Order());
            _ = await checkpoints.WriteAsync(new WalPosition(1, 8192), null, isSealed: false, [new CheckpointLeaf(2, Leaf(3))], cancellationToken);
            Assert.Equal(3UL, checkpoints.Generation);
        }
    }

    /// <summary>
    /// LS-I11: a root records whether the replica's seal record precedes its replay start, so a write whose only change
    /// is the seal writes a root-only generation, a repeat writes nothing, and a reopen loads the seal with the leaves.
    /// </summary>
    [Fact]
    public async Task ASealAloneWritesARootWhichAReopenLoads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new SimulatedWalStorage();
        await using (var directory = await CreateSlotsAsync(storage, cancellationToken))
        {
            await using var checkpoints = SlotCheckpoints.Create(directory);
            _ = await checkpoints.WriteAsync(new WalPosition(1, 0), null, isSealed: false, [new CheckpointLeaf(0, Leaf(1))], cancellationToken);
            Assert.Empty(Assert.IsType<Dictionary<long, CheckpointRef>>(await checkpoints.WriteAsync(new WalPosition(1, 0), null, isSealed: true, [], cancellationToken)));
            Assert.Equal(2UL, checkpoints.Generation);
            Assert.Null(await checkpoints.WriteAsync(new WalPosition(1, 0), null, isSealed: true, [], cancellationToken));
            Assert.Equal(2UL, checkpoints.Generation);
        }

        await using var reopened = await storage.OpenDirectoryAsync(SlotsPath, cancellationToken);
        var (reloaded, loaded) = await SlotCheckpoints.OpenAsync(reopened, cancellationToken);
        await using (reloaded)
        {
            Assert.False(reloaded.FoundDamage);
            Assert.NotNull(loaded);
            Assert.Equal(2UL, loaded.Generation);
            Assert.True(loaded.Sealed);
            Assert.Equal([0L], loaded.Leaves.Keys);
        }
    }

    /// <summary>
    /// A tree references only its root's generation and older ones, as the writer places them, so a root whose tree names
    /// a later generation is damage and its file does not outlive the load.
    /// </summary>
    [Fact]
    public async Task AnObjectInAGenerationAfterItsRootsIsDamage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var file = new byte[SlotLeafCodec.MaximumLength + NodeSize];
        Leaf(1).CopyTo(file, 0);
        var leaf = new CheckpointRef(2, 0, (uint)Leaf(1).Length, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, Leaf(1)));
        leaf.Write(file.AsSpan(SlotLeafCodec.MaximumLength));
        var node = new CheckpointRef(2, (ulong)SlotLeafCodec.MaximumLength, NodeSize, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, file.AsSpan(SlotLeafCodec.MaximumLength, NodeSize)));
        var slots = new byte[2 * RootSlotSize];
        new CheckpointRoot(1, 1, new WalPosition(1, 0), null, Sealed: false, node).Write(slots.AsSpan(0, RootSlotSize));
        var storage = new SimulatedWalStorage();
        await using var directory = await CreateSlotsAsync(storage, cancellationToken);
        await CreateFileAsync(directory, SlotCheckpoints.FileName(2), file, cancellationToken);
        await CreateFileAsync(directory, SlotCheckpoints.RootName, slots, cancellationToken);

        var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
        await using (checkpoints)
        {
            Assert.True(checkpoints.FoundDamage);
            Assert.Equal(["generation-after-root"], Reasons(checkpoints));
            Assert.Null(loaded);
            Assert.DoesNotContain(SlotCheckpoints.FileName(2), storage.ListNamedFiles(SlotsPath));
        }
    }

    /// <summary>
    /// The objects a tree references occupy disjoint byte ranges of their files, so loading reads each node at most once.
    /// Shared or overlapping nodes would otherwise load a subtree once per reference, exponentially often in the tree's
    /// height, and overlapping leaves would load bytes another object owns.
    /// </summary>
    [Theory]
    [InlineData("disjoint")]
    [InlineData("shared node")]
    [InlineData("overlapping nodes")]
    [InlineData("overlapping leaves")]
    public async Task TheObjectsOfATreeOccupyDisjointRanges(string layout)
    {
        // Leaves A and B, level-1 node N naming them, 24 zero bytes, then the root node. N shifted by 24 bytes is a node
        // naming B alone, overlapping N.
        var a = Leaf(1);
        var b = Leaf(2);
        var node = a.Length + b.Length;
        var rootNode = node + NodeSize + CheckpointRef.Size;
        var file = new byte[rootNode + NodeSize];
        a.CopyTo(file, 0);
        b.CopyTo(file, a.Length);
        var leafB = layout == "overlapping leaves"
            ? Reference(file, 1, a.Length - 1)
            : Reference(file, a.Length, b.Length);
        Reference(file, 0, a.Length).Write(file.AsSpan(node));
        leafB.Write(file.AsSpan(node + CheckpointRef.Size));
        CheckpointRef[] children = layout switch
        {
            "shared node" => [Reference(file, node, NodeSize), Reference(file, node, NodeSize)],
            "overlapping nodes" => [Reference(file, node, NodeSize), Reference(file, node + CheckpointRef.Size, NodeSize)],
            _ => [Reference(file, node, NodeSize)],
        };
        for (var index = 0; index < children.Length; index++)
        {
            children[index].Write(file.AsSpan(rootNode + index * CheckpointRef.Size));
        }

        var (damage, leaves, reads, reasons) = await LoadAsync(file, height: 2, Reference(file, rootNode, NodeSize));
        long[]? expected = layout == "disjoint" ? [0L, 1L] : null;
        Assert.Equal(layout != "disjoint", damage);
        Assert.Equal(expected, leaves);
        Assert.Equal(layout == "disjoint" ? [] : ["overlap"], reasons);

        // Loading checks a node's range before it reads the node, so it never reads a node twice or bytes another node owns.
        Assert.All(
            reads.SelectMany((read, index) => reads.Skip(index + 1).Select(other => (read, other))),
            static pair => Assert.False(
                pair.read.Offset < pair.other.Offset + pair.other.Length && pair.other.Offset < pair.read.Offset + pair.read.Length,
                string.Create(CultureInfo.InvariantCulture, $"Loading read [{pair.read.Offset}, +{pair.read.Length}) and [{pair.other.Offset}, +{pair.other.Length}), which overlap.")));
    }

    /// <summary>
    /// Loading checks a reference's length before it reads or places the object: an interior node is 6,144 bytes, and a
    /// leaf is as long as some leaf can be, so no length field sizes a read beyond the objects the format defines, and
    /// every object occupies a nonempty range.
    /// </summary>
    [Theory]
    [InlineData("long node")]
    [InlineData("long leaf")]
    [InlineData("short leaf")]
    [InlineData("empty leaf")]
    public async Task AReferenceOfALengthNoObjectHasIsNotRead(string kind)
    {
        var length = kind switch
        {
            "long node" => NodeSize + CheckpointRef.Size,
            "long leaf" => SlotLeafCodec.MaximumLength + 1,
            "short leaf" => SlotLeafCodec.MinimumLength - 1,
            _ => 0,
        };

        var node = Math.Max(length, SlotLeafCodec.MaximumLength);
        var file = new byte[node + NodeSize];
        Reference(file, 0, length).Write(file.AsSpan(node));
        var root = kind == "long node" ? Reference(file, 0, length) : Reference(file, node, NodeSize);

        var (damage, leaves, reads, reasons) = await LoadAsync(file, height: 1, root);
        Assert.True(damage);
        Assert.Null(leaves);
        Assert.Equal([kind == "long node" ? "node-length" : "leaf-length"], reasons);
        Assert.DoesNotContain(reads, read => read.Length == length);
    }

    /// <summary>
    /// A tree whose object breaks one rule of ledgerstore.md §7.4 makes its root unusable, for that rule's reason
    /// (LS-I3): a leaf outside its file, a node which fails its CRC, a node with a null reference whose fields are not
    /// zero or with no child, and a leaf numbered beyond the slot space, at 2<sup>55</sup>.
    /// </summary>
    [Theory]
    [InlineData("outside-file")]
    [InlineData("crc-mismatch")]
    [InlineData("null-reference-fields")]
    [InlineData("node-empty")]
    [InlineData("leaf-beyond-address-space")]
    public async Task ATreeBreakingOneRuleIsUnusableForItsReason(string reason)
    {
        // Leaf A, then the interior nodes, bottom-up: one for a tree of height 1, or seven for one of height 7.
        var a = Leaf(1);
        var height = reason == "leaf-beyond-address-space" ? CheckpointRoot.MaximumHeight : 1;
        var file = new byte[a.Length + height * NodeSize];
        a.CopyTo(file, 0);
        var child = reason == "outside-file" ? new CheckpointRef(1, (ulong)file.Length, (uint)a.Length, 0) : Reference(file, 0, a.Length);
        for (var level = 1; level <= height; level++)
        {
            var node = a.Length + (level - 1) * NodeSize;
            if (reason != "node-empty")
            {
                // The top node of the tallest tree names its child at index 128, so its leaf's number is 128 << 48 = 2^55.
                child.Write(file.AsSpan(node + (level == CheckpointRoot.MaximumHeight ? 128 : 0) * CheckpointRef.Size));
            }

            if (reason == "null-reference-fields")
            {
                new CheckpointRef(0, 5, 0, 0).Write(file.AsSpan(node + CheckpointRef.Size));
            }

            child = Reference(file, node, NodeSize);
        }

        var root = reason == "crc-mismatch" ? child with { Crc = child.Crc ^ 1 } : child;
        var (damage, leaves, _, reasons) = await LoadAsync(file, height, root);
        Assert.True(damage);
        Assert.Null(leaves);
        Assert.Equal([reason], reasons);
    }

    /// <summary>
    /// Loads a tree of <paramref name="height"/> from one generation file through a root naming <paramref name="root"/>.
    /// </summary>
    /// <returns>
    /// Whether loading found damage, the leaves it loaded, every read of the generation file, and the reason it rejected
    /// each tree it passed over.
    /// </returns>
    private static async Task<(bool Damage, long[]? Leaves, List<(long Offset, int Length)> Reads, string?[] Reasons)> LoadAsync(byte[] file, int height, CheckpointRef root)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slots = new byte[2 * RootSlotSize];
        new CheckpointRoot(1, height, new WalPosition(1, 0), null, Sealed: false, root).Write(slots.AsSpan(0, RootSlotSize));
        var storage = new SimulatedWalStorage();
        var reads = new List<(long Offset, int Length)>();
        storage.NamedReadFault = (_, name, offset, length) =>
        {
            if (name == SlotCheckpoints.FileName(1))
            {
                reads.Add((offset, length));
            }

            return null;
        };
        await using var directory = await CreateSlotsAsync(storage, cancellationToken);
        await CreateFileAsync(directory, SlotCheckpoints.FileName(1), file, cancellationToken);
        await CreateFileAsync(directory, SlotCheckpoints.RootName, slots, cancellationToken);
        var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
        await using (checkpoints)
        {
            return (checkpoints.FoundDamage, loaded?.Leaves.Keys.Order().ToArray(), reads, Reasons(checkpoints));
        }
    }

    /// <summary>
    /// Returns the reason of each tree rejection loading met, newest root first.
    /// </summary>
    private static string?[] Reasons(SlotCheckpoints checkpoints) =>
        [.. checkpoints.TreeRejections.Select(static rejection => DecodeRejection.ReasonOf(rejection))];

    /// <summary>
    /// Writes generation 1 holding leaf 0 and generation 2 adding leaf 1.
    /// </summary>
    private static async Task<SimulatedWalStorage> TwoGenerationsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new SimulatedWalStorage();
        await using var directory = await CreateSlotsAsync(storage, cancellationToken);
        await using var checkpoints = SlotCheckpoints.Create(directory);
        _ = await checkpoints.WriteAsync(new WalPosition(1, 0), null, isSealed: false, [new CheckpointLeaf(0, Leaf(1))], cancellationToken);
        _ = await checkpoints.WriteAsync(new WalPosition(1, 4096), null, isSealed: false, [new CheckpointLeaf(1, Leaf(2))], cancellationToken);
        return storage;
    }

    /// <summary>
    /// Returns the reference of generation 1 to <paramref name="length"/> bytes of <paramref name="file"/> at
    /// <paramref name="offset"/>, with their CRC-32C.
    /// </summary>
    private static CheckpointRef Reference(byte[] file, int offset, int length) =>
        new(1, (ulong)offset, (uint)length, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, file.AsSpan(offset, length)));

    /// <summary>
    /// Writes generation 1, holding leaf 0, then fails generation 2, which adds leaf 1, once its root's bytes reach the
    /// device: the failure is reported, which stops the process there (conventions.md §14), generation 2's file and the
    /// root slot it wrote remain, and generation 1 stays in force.
    /// </summary>
    private static async Task FailGenerationTwoOnceItsRootReachesTheDeviceAsync(
        RecordingFileSystem fileSystem, SlotCheckpoints checkpoints, CancellationToken cancellationToken)
    {
        _ = await checkpoints.WriteAsync(new WalPosition(1, 0), null, isSealed: false, [new CheckpointLeaf(0, Leaf(1))], cancellationToken);
        fileSystem.HoldWrites = static path => path.EndsWith("/" + SlotCheckpoints.RootName, StringComparison.Ordinal);
        var failing = checkpoints.WriteAsync(new WalPosition(1, 4096), null, isSealed: false, [new CheckpointLeaf(1, Leaf(2))], cancellationToken).AsTask();
        while (fileSystem.HeldWrites.Count == 0)
        {
            Assert.False(failing.IsCompleted, "The write completed without writing its root.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, cancellationToken);
        }

        fileSystem.HoldWrites = null;
        var reported = FatalErrors.ReportedCount;
        fileSystem.HeldWrites[0].Fail();
        _ = await Assert.ThrowsAsync<IOException>(() => failing);
        var failure = Assert.Single(FatalErrors.TakeMine(reported));
        Assert.EndsWith(SlotCheckpoints.RootName, failure.Path, StringComparison.Ordinal);
        Assert.True(fileSystem.Crashed, "The failed root write did not stop the process.");
        Assert.Equal(1UL, checkpoints.Generation);
    }

    private static async Task CreateFileAsync(WalDirectory directory, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(bytes);
        using var contents = writer.ConsumeSlice(bytes.Length);
        await (await directory.CreateNamedFileAsync(name, contents, cancellationToken)).DisposeAsync();
    }

    /// <summary>
    /// Returns a leaf whose slot 0 holds DATA on page <paramref name="page"/> of WAL file 1.
    /// </summary>
    private static byte[] Leaf(uint page)
    {
        var entries = new SlotEntry[SlotLeafCodec.Slots];
        entries[0] = new SlotEntry(SlotKind.Data, new WalPage(1, page));
        return SlotLeafCodec.Encode(entries);
    }

    private static ValueTask<WalDirectory> CreateSlotsAsync(WalStorage storage, CancellationToken cancellationToken) =>
        CreateSlotsAsync(storage, SlotsPath, cancellationToken);

    private static async ValueTask<WalDirectory> CreateSlotsAsync(WalStorage storage, string path, CancellationToken cancellationToken)
    {
        await storage.CreateDirectoryAsync(path, cancellationToken);
        return await storage.OpenDirectoryAsync(path, cancellationToken);
    }
}

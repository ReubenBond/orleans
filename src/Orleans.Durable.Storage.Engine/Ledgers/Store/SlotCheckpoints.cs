using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Buffers;

using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// What decoding a root slot gives (conventions.md §15): the root, or the <see cref="InvalidDataException"/> naming the
/// first rule the slot breaks, with the rule's reason code (ledgerstore.md §7.4).
/// </summary>
internal readonly union CheckpointRootDecoding(CheckpointRoot, InvalidDataException);

/// <summary>
/// A reference to one checkpoint object: its file generation, byte range, and CRC-32C. Generation 0 is null, and a
/// null reference is all zeros.
/// </summary>
internal readonly record struct CheckpointRef(ulong Generation, ulong Offset, uint Length, uint Crc)
{
    public const int Size = 24;
    internal const int GenerationOffset = 0;
    internal const int OffsetOffset = 8;
    internal const int LengthOffset = 16;
    internal const int CrcOffset = 20;

    public bool IsNull => Generation == 0;

    /// <summary>
    /// Decodes a reference, returning the rejection of a null reference whose other fields are not all zero
    /// (<c>null-reference-fields</c>, ledgerstore.md §7.4), or <see langword="null"/> with the reference.
    /// </summary>
    public static InvalidDataException? Decode(ReadOnlySpan<byte> bytes, out CheckpointRef reference)
    {
        reference = new CheckpointRef(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[GenerationOffset..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[OffsetOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[LengthOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[CrcOffset..]));
        if (reference.IsNull && reference != default)
        {
            reference = default;
            return DecodeRejection.Create("null-reference-fields", "A null checkpoint reference has nonzero fields.");
        }

        return null;
    }

    public void Write(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[GenerationOffset..], Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[OffsetOffset..], Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[LengthOffset..], Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[CrcOffset..], Crc);
    }
}

/// <summary>
/// One root slot of <c>slots.root</c>: flag bit 0 records <see cref="CorruptionEnd"/>, and flag bit 1 that the replica's
/// seal record precedes the replay start (ledgerstore.md §7.4, LS-I11).
/// </summary>
internal sealed record CheckpointRoot(ulong Generation, int Height, WalPosition ReplayStart, WalPage? CorruptionEnd, bool Sealed, CheckpointRef Root)
{
    internal const uint CorruptionEndFlag = 1;
    internal const uint SealedFlag = 2;

    public const int MaximumHeight = 7;

    /// <summary>
    /// The bound every generation a root or reference names lies below (ledgerstore.md §7.4). Generations start at 1
    /// and each checkpoint takes the next, so no store's own checkpoints reach the last, 2<sup>63</sup> − 1, after which
    /// none follows (LS-I9).
    /// </summary>
    public const ulong GenerationLimit = 1UL << 63;

    internal const int MinimumSlotSize = 4096;
    internal const int MagicOffset = 4;
    internal const int VersionOffset = 8;
    internal const int HeightOffset = 12;
    internal const int GenerationOffset = 16;
    internal const int ReplayStartOffset = 24;
    internal const int CorruptionFileOffset = 40;
    internal const int CorruptionPageOffset = 48;
    internal const int FlagsOffset = 52;
    internal const int RootOffset = 56;
    internal const int FieldsLength = 80;

    private const uint Version = 1;

    private static ReadOnlySpan<byte> Magic => "CLSI"u8;

    public static int SlotSize(WalDirectory directory) => Math.Max(MinimumSlotSize, directory.MinimumPageSize);

    /// <summary>
    /// Decodes a root slot: the root, or the rejection naming the first rule of ledgerstore.md §7.4 the slot breaks, in
    /// the order that section lists them. A slot which decodes is usable.
    /// </summary>
    /// <remarks>The slot's header rules come first, then its root reference's, then its fields'.</remarks>
    public static CheckpointRootDecoding Decode(ReadOnlySpan<byte> slot)
    {
        if (CheckHeader(slot) is { } headerRejection)
        {
            return headerRejection;
        }

        if (CheckpointRef.Decode(slot[RootOffset..], out var reference) is { } referenceRejection)
        {
            return referenceRejection;
        }

        var height = BinaryPrimitives.ReadUInt32LittleEndian(slot[HeightOffset..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(slot[FlagsOffset..]);
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(slot[GenerationOffset..]);
        var corruptionFile = BinaryPrimitives.ReadUInt64LittleEndian(slot[CorruptionFileOffset..]);
        var corruptionPage = BinaryPrimitives.ReadUInt32LittleEndian(slot[CorruptionPageOffset..]);
        if (CheckFields(height, reference, generation, flags, corruptionFile != 0 || corruptionPage != 0) is { } fieldRejection)
        {
            return fieldRejection;
        }

        return new CheckpointRoot(
            generation,
            (int)height,
            new WalPosition(
                BinaryPrimitives.ReadUInt64LittleEndian(slot[ReplayStartOffset..]),
                BinaryPrimitives.ReadUInt64LittleEndian(slot[(ReplayStartOffset + sizeof(ulong))..])),
            (flags & CorruptionEndFlag) != 0 ? new WalPage(corruptionFile, corruptionPage) : null,
            (flags & SealedFlag) != 0,
            reference);
    }

    private static InvalidDataException? CheckHeader(ReadOnlySpan<byte> slot)
    {
        if (slot.Length < FieldsLength)
        {
            return Rejection("truncated");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(slot) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, slot[MagicOffset..]))
        {
            return Rejection("crc-mismatch");
        }

        if (!slot[MagicOffset..VersionOffset].SequenceEqual(Magic))
        {
            return Rejection("bad-magic");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(slot[VersionOffset..]) != Version)
        {
            return Rejection("bad-version");
        }

        if (slot[FieldsLength..].ContainsAnyExcept((byte)0))
        {
            return Rejection("nonzero-padding");
        }

        return null;
    }

    private static InvalidDataException? CheckFields(uint height, CheckpointRef root, ulong generation, uint flags, bool hasCorruptionEnd)
    {
        if (height > MaximumHeight)
        {
            return Rejection("height-out-of-range");
        }

        if (height == 0 != root.IsNull)
        {
            return Rejection("height-root-mismatch");
        }

        if (generation == 0)
        {
            return Rejection("generation-zero");
        }

        if (generation >= GenerationLimit || root.Generation >= GenerationLimit)
        {
            return Rejection("generation-out-of-range");
        }

        if ((flags & ~(CorruptionEndFlag | SealedFlag)) != 0)
        {
            return Rejection("unknown-flags");
        }

        if ((flags & CorruptionEndFlag) == 0 && hasCorruptionEnd)
        {
            return Rejection("corruption-end-without-flag");
        }

        return null;
    }

    private static InvalidDataException Rejection(string reason) => DecodeRejection.Create(reason, reason switch
    {
        "truncated" => "The checkpoint root slot is shorter than its fields.",
        "crc-mismatch" => "The checkpoint root slot fails its CRC-32C.",
        "bad-magic" => "The checkpoint root slot has an invalid magic value.",
        "bad-version" => "The checkpoint root slot has an unsupported version.",
        "nonzero-padding" => "The checkpoint root slot has nonzero bytes after its fields.",
        "height-out-of-range" => "The checkpoint root's height is above 7.",
        "height-root-mismatch" => "The checkpoint root's height is 0 with a root reference, or above 0 with a null one.",
        "generation-zero" => "The checkpoint root's generation is 0.",
        "generation-out-of-range" => "The checkpoint root or its root reference names a generation at or above 2^63.",
        "unknown-flags" => "The checkpoint root has flag bits the format does not define.",
        "corruption-end-without-flag" => "The checkpoint root has a corruption bound without its flag.",
        _ => throw new UnreachableException(),
    });

    public void Write(Span<byte> slot)
    {
        slot.Clear();
        Magic.CopyTo(slot[MagicOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[VersionOffset..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[HeightOffset..], (uint)Height);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[GenerationOffset..], Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[ReplayStartOffset..], ReplayStart.File);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[(ReplayStartOffset + sizeof(ulong))..], ReplayStart.Offset);
        var flags = Sealed ? SealedFlag : 0;
        if (CorruptionEnd is { } end)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(slot[CorruptionFileOffset..], end.File);
            BinaryPrimitives.WriteUInt32LittleEndian(slot[CorruptionPageOffset..], end.Index);
            flags |= CorruptionEndFlag;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(slot[FlagsOffset..], flags);

        Root.Write(slot[RootOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(slot, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, slot[MagicOffset..]));
    }
}

/// <summary>
/// The checkpoint a recovery loaded: its root's fields and a reference to each leaf, whose contents load on demand. The
/// leaves reflect exactly the WAL records below the replay start, and <see cref="Sealed"/> whether the replica's seal
/// record lies below it.
/// </summary>
internal sealed record LoadedCheckpoint(ulong Generation, WalPosition ReplayStart, WalPage? CorruptionEnd, bool Sealed, Dictionary<long, CheckpointRef> Leaves);

/// <summary>
/// The tree a root names, as loading reads it: its interior nodes by level and prefix, and a reference to each leaf by
/// leaf number, whose contents load on demand.
/// </summary>
internal sealed record CheckpointTree(Dictionary<(int Level, long Prefix), CheckpointRef[]> Nodes, Dictionary<long, CheckpointRef> Leaves);

/// <summary>
/// A leaf to write in a checkpoint: its encoding, or <see langword="null"/> when the leaf no longer exists.
/// </summary>
internal readonly record struct CheckpointLeaf(long Leaf, byte[]? Bytes);

/// <summary>
/// Owns the checkpoint files of one replica directory: the two-slot root, the immutable generation files, the persisted
/// interior nodes of the root in force, reads of leaves on demand, and reclamation (LS-I8, LS-I9; ledgerstore.md §7.4, §8.8).
/// </summary>
/// <remarks>
/// Its owner serializes calls to <see cref="WriteAsync"/>, one generation at a time; <see cref="ReadObjectAsync"/> may
/// run concurrently with them. The persisted shadow changes only after a root is durable, so a failed generation
/// leaves the root in force untouched.
/// </remarks>
internal sealed class SlotCheckpoints : IAsyncDisposable
{
    internal const string RootName = "slots.root";
    internal const string Extension = ".clsi";
    internal const int NodeSize = SlotLeafCodec.Slots * CheckpointRef.Size;

    private readonly WalDirectory _directory;
    private readonly int _slotSize;

    // Open generation files and the generations which may still be read: those either retained root references.
    private readonly Lock _filesLock = new();
    private readonly Dictionary<ulong, Task<WalFile>> _handles = [];
    private HashSet<ulong>? _readable;

    private Dictionary<(int Level, long Prefix), CheckpointRef[]> _nodes = [];
    private HashSet<ulong> _currentFiles = [];
    private HashSet<ulong> _previousFiles = [];
    private WalFile? _rootFile;
    private int _inForceSlot = -1;
    private int _height;
    private CheckpointRef _root;
    private WalPosition _replayStart;
    private WalPage? _corruptionEnd;
    private bool _sealed;

    // The highest generation a usable root slot names, whether or not its tree loads, or 0 when none does; each checkpoint
    // takes the next one once its root is durable, so no generation is taken while a usable root slot or file names it
    // (LS-I9). It lies below 2^63, the bound loading enforces. A write which failed stops the process (conventions.md §14),
    // so no later write in this process takes the generation it may have left; the next open loads its root slot when it is
    // usable, and otherwise deletes its file.
    private ulong _lastGeneration;

    private SlotCheckpoints(WalDirectory directory, HashSet<ulong>? readable)
    {
        _directory = directory;
        _slotSize = CheckpointRoot.SlotSize(directory);
        _readable = readable;
    }

    /// <summary>
    /// Gets the generation of the root in force, or 0 when none is.
    /// </summary>
    public ulong Generation { get; private set; }

    /// <summary>
    /// Gets the number of generation files the root in force references.
    /// </summary>
    public int FileCount => _currentFiles.Count;

    /// <summary>
    /// Gets a value indicating whether the generations are exhausted: a usable root slot names 2<sup>63</sup> − 1, the
    /// last generation below the bound, so no checkpoint can follow (ledgerstore.md §8.8). Only a damaged or crafted root
    /// slot, or a write which took the last generation, makes it so, and it persists while a usable root slot names the
    /// last generation, whether or not its tree loads: until an operator deletes the checkpoints.
    /// </summary>
    public bool Exhausted => Volatile.Read(ref _lastGeneration) == CheckpointRoot.GenerationLimit - 1;

    /// <summary>
    /// Gets a value indicating whether opening found a root it could not use where a newer one should have been: no
    /// usable root in an existing root file, or a newer CRC-valid root whose objects are invalid.
    /// </summary>
    public bool FoundDamage { get; private set; }

    /// <summary>
    /// Gets the rejection of each candidate root's tree which loading passed over, newest first: the first rule of
    /// ledgerstore.md §7.4 an object of the tree breaks, which made its root unusable (LS-I3).
    /// </summary>
    public IReadOnlyList<InvalidDataException> TreeRejections { get; private set; } = [];

    /// <summary>
    /// Returns whether a directory holds a root or any generation file.
    /// </summary>
    public static async ValueTask<bool> ExistAsync(WalDirectory directory, CancellationToken cancellationToken) =>
        (await directory.ListNamedFilesAsync(RootName, cancellationToken).ConfigureAwait(false)).Contains(RootName, StringComparer.Ordinal)
        || (await directory.ListNamedFilesAsync(Extension, cancellationToken).ConfigureAwait(false)).Count != 0;

    /// <summary>
    /// Starts the checkpoints of a directory which holds none.
    /// </summary>
    public static SlotCheckpoints Create(WalDirectory directory) => new(directory, readable: []);

    /// <summary>
    /// Loads the newest usable root and its interior nodes, deleting the generation files it does not reference. Leaves
    /// are not read: each is checked only to lie within its file, disjoint from the tree's other objects, and to have a
    /// length some leaf has.
    /// </summary>
    /// <remarks>
    /// Checkpoints are derived (LS-I3), so their content never fails the load: a root slot which does not decode is no
    /// candidate, and a root whose tree breaks a rule of ledgerstore.md §7.4 is unusable, its rejection recorded in
    /// <see cref="TreeRejections"/>, and loading moves to the older root or returns no checkpoint, setting
    /// <see cref="FoundDamage"/>. A failed read propagates, as do the failures of listing the
    /// directory, reading its root file, and deleting the generation files the loaded root does not reference, and
    /// cancellation; a failed local storage operation stops the process (conventions.md §14).
    /// </remarks>
    /// <returns>The checkpoints and the loaded checkpoint, or <see langword="null"/> when no root is usable.</returns>
    public static async ValueTask<(SlotCheckpoints Checkpoints, LoadedCheckpoint? Loaded)> OpenAsync(WalDirectory directory, CancellationToken cancellationToken)
    {
        var checkpoints = new SlotCheckpoints(directory, readable: null);
        try
        {
            var loaded = await checkpoints.LoadAsync(cancellationToken).ConfigureAwait(false);
            return (checkpoints, loaded);
        }
        catch
        {
            await checkpoints.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Writes one generation: the given leaves, the interior nodes above them, and a root naming
    /// <paramref name="replayStart"/>. Completes when the root is durable and the root in force has moved. With no
    /// leaves, a generation holds only a root, written when the replay start, the corruption bound, or the seal has
    /// changed. <paramref name="isSealed"/> records that the replica's seal record lies below the replay start (LS-I11).
    /// Files the new root no longer needs stay until <see cref="ReclaimAsync"/>. Once its generation's file is published,
    /// the write runs to its outcome whatever the caller's cancellation: nothing in this process removes what a write which
    /// stopped part way leaves, and a failure stops the process (ledgerstore.md §8.8, conventions.md §14).
    /// </summary>
    /// <returns>
    /// The reference of each leaf written, or <see langword="null"/> when nothing was written.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The generations are exhausted (<see cref="Exhausted"/>): nothing is written, and no checkpoint follows until an
    /// operator deletes the checkpoints.
    /// </exception>
    public async ValueTask<Dictionary<long, CheckpointRef>?> WriteAsync(
        WalPosition replayStart, WalPage? corruptionEnd, bool isSealed, IReadOnlyList<CheckpointLeaf> leaves, CancellationToken cancellationToken)
    {
        if (Exhausted)
        {
            throw GenerationsExhausted();
        }

        var generation = checked(_lastGeneration + 1);
        Debug.Assert(generation < CheckpointRoot.GenerationLimit, "Loading counts only generations below 2^63, and the writer stops at the last.");
        var slot = _inForceSlot == 0 ? 1 : 0;
        if (leaves.Count == 0 && _inForceSlot >= 0 && replayStart == _replayStart && corruptionEnd == _corruptionEnd && isSealed == _sealed)
        {
            return null;
        }

        var nodes = new Dictionary<(int Level, long Prefix), CheckpointRef[]>();
        CheckpointRef[] Node(int level, long prefix)
        {
            if (!nodes.TryGetValue((level, prefix), out var children))
            {
                children = _nodes.TryGetValue((level, prefix), out var persisted) ? (CheckpointRef[])persisted.Clone() : new CheckpointRef[SlotLeafCodec.Slots];
                nodes.Add((level, prefix), children);
            }

            return children;
        }

        var height = _height;
        foreach (var leaf in leaves.Where(static leaf => leaf.Bytes is not null))
        {
            height = Math.Max(height, HeightFor(leaf.Leaf));
        }

        using var contents = ArcBufferWriter.CreateAligned();
        ulong offset = 0;
        CheckpointRef Append(ReadOnlySpan<byte> bytes)
        {
            // Each object follows the last, so the objects of a generation occupy disjoint ranges of its file.
            Debug.Assert(offset == (ulong)contents.Length, "The generation file's objects are written back to back.");
            contents.Write(bytes);
            var reference = new CheckpointRef(generation, offset, (uint)bytes.Length, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes));
            offset += (ulong)bytes.Length;
            return reference;
        }

        // A taller tree places the old root as child 0 of the first new level; rewriting the old root replaces it.
        if (_height > 0 && height > _height)
        {
            Node(_height + 1, 0)[0] = _root;
            for (var level = _height + 2; level <= height; level++)
            {
                _ = Node(level, 0);
            }
        }

        var written = new Dictionary<long, CheckpointRef>();
        foreach (var leaf in leaves)
        {
            if (leaf.Bytes is null && leaf.Leaf >> (SlotLeafCodec.SlotBits * height) != 0)
            {
                continue;
            }

            var reference = leaf.Bytes is null ? default : Append(leaf.Bytes);
            Node(1, leaf.Leaf >> SlotLeafCodec.SlotBits)[leaf.Leaf & (SlotLeafCodec.Slots - 1)] = reference;
            if (!reference.IsNull)
            {
                written[leaf.Leaf] = reference;
            }
        }

        var root = _root;
        var node = new byte[NodeSize];
        for (var level = 1; level <= height; level++)
        {
            foreach (var key in nodes.Keys.Where(key => key.Level == level).Order().ToArray())
            {
                var children = nodes[key];
                CheckpointRef reference = default;
                if (children.Any(child => !child.IsNull))
                {
                    for (var index = 0; index < children.Length; index++)
                    {
                        children[index].Write(node.AsSpan(index * CheckpointRef.Size));
                    }

                    reference = Append(node);
                }

                if (level == height)
                {
                    root = reference;
                }
                else
                {
                    Node(level + 1, key.Prefix >> SlotLeafCodec.SlotBits)[key.Prefix & (SlotLeafCodec.Slots - 1)] = reference;
                }
            }
        }

        var resultHeight = root.IsNull ? 0 : height;
        if (contents.Length != 0)
        {
            var padded = (int)(((long)contents.Length + _slotSize - 1) / _slotSize * _slotSize);
            contents.Write(new byte[padded - contents.Length]);
            using var buffer = contents.ConsumeSlice(contents.Length);
            lock (_filesLock)
            {
                _ = _readable!.Add(generation);
            }

            await (await _directory.CreateNamedFileAsync(FileName(generation), buffer, cancellationToken).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }

        await WriteRootAsync(slot, new CheckpointRoot(generation, resultHeight, replayStart, corruptionEnd, isSealed, root), CancellationToken.None).ConfigureAwait(false);

        // The root is durable: the generation is taken, and the shadow is installed.
        Volatile.Write(ref _lastGeneration, generation);
        var previousFiles = _currentFiles;
        foreach (var (key, children) in nodes)
        {
            if (children.Any(child => !child.IsNull) && key.Level <= resultHeight)
            {
                _nodes[key] = children;
            }
            else
            {
                _ = _nodes.Remove(key);
            }
        }

        _root = root;
        _height = resultHeight;
        _inForceSlot = slot;
        _replayStart = replayStart;
        _corruptionEnd = corruptionEnd;
        _sealed = isSealed;
        Generation = generation;
        _currentFiles = ReferencedFiles();
        _previousFiles = previousFiles;
        return written;
    }

    /// <summary>
    /// Deletes every generation file which neither the root in force nor the previous root references. The owner calls
    /// it after pointing unloaded leaves at the objects the last generation copied them to.
    /// </summary>
    public ValueTask ReclaimAsync(CancellationToken cancellationToken) =>
        DeleteFilesAsync([.. _currentFiles, .. _previousFiles], cancellationToken);

    /// <summary>
    /// Returns the leaves to copy forward so the oldest generation file stops being referenced: every leaf in it, and
    /// one leaf beneath each interior node in it.
    /// </summary>
    public List<long> LeavesToCompact()
    {
        var oldest = _currentFiles.Where(file => file != Generation).DefaultIfEmpty().Min();
        var leaves = new List<long>();
        if (oldest == 0)
        {
            return leaves;
        }

        foreach (var ((level, prefix), children) in _nodes)
        {
            for (var index = 0; index < children.Length; index++)
            {
                if (children[index].Generation != oldest)
                {
                    continue;
                }

                var path = (prefix << SlotLeafCodec.SlotBits) | (long)index;
                if (level == 1)
                {
                    leaves.Add(path);
                }
                else if (FirstLeaf(level - 1, path) is { } leaf)
                {
                    leaves.Add(leaf);
                }
            }
        }

        return leaves;
    }

    public async ValueTask DisposeAsync()
    {
        if (_rootFile is not null)
        {
            await _rootFile.DisposeAsync().ConfigureAwait(false);
            _rootFile = null;
        }

        Task<WalFile>[] handles;
        lock (_filesLock)
        {
            handles = [.. _handles.Values];
            _handles.Clear();
            _readable = [];
        }

        await DisposeHandlesAsync(handles).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the failure of a checkpoint once the generations are exhausted (<see cref="Exhausted"/>).
    /// </summary>
    internal static InvalidDataException GenerationsExhausted() =>
        new("The checkpoint generations are exhausted: a usable root slot names 2^63 - 1, so no checkpoint can "
            + "follow until an operator deletes the replica's slots.root and .clsi files (ledgerstore.md §8.8).");

    internal static string FileName(ulong generation) =>
        string.Create(CultureInfo.InvariantCulture, $"{generation:D20}{Extension}");

    internal static int HeightFor(long leaf)
    {
        var height = 1;
        while (height < CheckpointRoot.MaximumHeight && leaf >> (SlotLeafCodec.SlotBits * height) != 0)
        {
            height++;
        }

        return height;
    }

    private static bool TryParseFileName(string name, out ulong generation)
    {
        generation = 0;
        return name.Length == 20 + Extension.Length
            && name.EndsWith(Extension, StringComparison.Ordinal)
            && ulong.TryParse(name.AsSpan(0, 20), NumberStyles.None, CultureInfo.InvariantCulture, out generation);
    }

    private static async ValueTask DisposeHandlesAsync(IEnumerable<Task<WalFile>> handles)
    {
        foreach (var handle in handles)
        {
            // A handle which failed to open holds nothing to release; its opener received the failure.
            await ((Task)handle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (handle.IsCompletedSuccessfully)
            {
                await (await handle.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private long? FirstLeaf(int level, long prefix)
    {
        if (!_nodes.TryGetValue((level, prefix), out var children))
        {
            return null;
        }

        for (var index = 0; index < children.Length; index++)
        {
            if (!children[index].IsNull)
            {
                var path = (prefix << SlotLeafCodec.SlotBits) | (long)index;
                return level == 1 ? path : FirstLeaf(level - 1, path);
            }
        }

        return null;
    }

    private HashSet<ulong> ReferencedFiles()
    {
        var files = new HashSet<ulong>();
        if (!_root.IsNull)
        {
            _ = files.Add(_root.Generation);
        }

        foreach (var children in _nodes.Values)
        {
            foreach (var child in children)
            {
                if (!child.IsNull)
                {
                    _ = files.Add(child.Generation);
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Deletes every generation file not in <paramref name="retained"/>, first ending reads of it.
    /// </summary>
    private async ValueTask DeleteFilesAsync(HashSet<ulong> retained, CancellationToken cancellationToken)
    {
        List<Task<WalFile>> closing = [];
        lock (_filesLock)
        {
            _readable = [.. retained];
            foreach (var generation in _handles.Keys.Where(generation => !retained.Contains(generation)).ToArray())
            {
                closing.Add(_handles[generation]);
                _ = _handles.Remove(generation);
            }
        }

        await DisposeHandlesAsync(closing).ConfigureAwait(false);
        foreach (var name in await _directory.ListNamedFilesAsync(Extension, cancellationToken).ConfigureAwait(false))
        {
            if (TryParseFileName(name, out var generation) && !retained.Contains(generation))
            {
                await _directory.DeleteNamedFileAsync(name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask WriteRootAsync(int slot, CheckpointRoot root, CancellationToken cancellationToken)
    {
        if (_rootFile is null)
        {
            await _directory.DeleteNamedFileAsync(RootName, cancellationToken).ConfigureAwait(false);
            using var zeros = ArcBufferWriter.CreateAligned();
            zeros.Write(new byte[2 * _slotSize]);
            using var empty = zeros.ConsumeSlice(2 * _slotSize);
            _rootFile = await _directory.CreateNamedFileAsync(RootName, empty, cancellationToken).ConfigureAwait(false);
        }

        var bytes = new byte[_slotSize];
        root.Write(bytes);
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(bytes);
        using var buffer = writer.ConsumeSlice(_slotSize);
        await _rootFile.WriteAsync((long)slot * _slotSize, new[] { buffer }).ConfigureAwait(false);
    }

    private async ValueTask<LoadedCheckpoint?> LoadAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<(CheckpointRoot Root, int Slot)>();

        // A root or generation file at or above the generation limit is damage (ledgerstore.md §7.4): it neither loads
        // nor counts toward the highest generation, and its file is deleted below. A generation file counts toward the
        // highest generation through the root slots alone: a tree references only its root's generation and older ones,
        // and loading deletes every file the loaded root does not reference, so no file outlives the load with a
        // generation above every root slot's.
        var outOfRange = false;
        var rootExists = (await _directory.ListNamedFilesAsync(RootName, cancellationToken).ConfigureAwait(false)).Contains(RootName, StringComparer.Ordinal);
        _rootFile = rootExists ? await _directory.OpenNamedFileAsync(RootName, cancellationToken).ConfigureAwait(false) : null;
        if (_rootFile is not null)
        {
            if (_rootFile.Length == 2L * _slotSize)
            {
                using var bytes = await _rootFile.ReadAsync(0, 2 * _slotSize, cancellationToken).ConfigureAwait(false);
                var array = bytes.ToArray();
                for (var slot = 0; slot < 2; slot++)
                {
                    var decoding = CheckpointRoot.Decode(array.AsSpan(slot * _slotSize, _slotSize));
                    Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
                    var candidate = decoding switch
                    {
                        CheckpointRoot root => root,

                        // LS-I3: a root slot which breaks a rule is unusable, and recovery falls back to the other slot.
                        InvalidDataException rejection => NotLoaded(rejection),
                        null => throw new UnreachableException(),
                    };
                    if (candidate is not null)
                    {
                        candidates.Add((candidate, slot));
                    }
                }
            }
            else
            {
                await _rootFile.DisposeAsync().ConfigureAwait(false);
                _rootFile = null;
            }
        }

        var highest = candidates.Select(candidate => candidate.Root.Generation).DefaultIfEmpty().Max();
        foreach (var name in await _directory.ListNamedFilesAsync(Extension, cancellationToken).ConfigureAwait(false))
        {
            outOfRange |= TryParseFileName(name, out var generation) && generation >= CheckpointRoot.GenerationLimit;
        }

        Volatile.Write(ref _lastGeneration, highest);
        LoadedCheckpoint? loaded = null;
        var rejections = new List<InvalidDataException>();
        foreach (var (root, slot) in candidates.OrderByDescending(candidate => candidate.Root.Generation))
        {
            var tree = new CheckpointTree([], []);
            if (await LoadTreeAsync(root, tree, cancellationToken).ConfigureAwait(false) is { } rejection)
            {
                // LS-I3: a checkpoint is derived, so a tree which breaks a rule makes its root unusable, and recovery falls
                // back to the older root or to the WAL. A failed read of an object stops the process (conventions.md §14).
                rejections.Add(rejection);
                continue;
            }

            _nodes = tree.Nodes;
            _root = root.Root;
            _height = root.Height;
            _inForceSlot = slot;
            _replayStart = root.ReplayStart;
            _corruptionEnd = root.CorruptionEnd;
            _sealed = root.Sealed;
            Generation = root.Generation;
            loaded = new LoadedCheckpoint(root.Generation, root.ReplayStart, root.CorruptionEnd, root.Sealed, tree.Leaves);
            break;
        }

        TreeRejections = rejections;
        FoundDamage = rejections.Count != 0 || outOfRange || rootExists && loaded is null;
        _currentFiles = ReferencedFiles();
        await DeleteFilesAsync(_currentFiles, cancellationToken).ConfigureAwait(false);
        return loaded;

        // A root slot at or above the generation limit is damage which counts toward FoundDamage (ledgerstore.md §7.4).
        CheckpointRoot? NotLoaded(InvalidDataException rejection)
        {
            outOfRange |= DecodeRejection.ReasonOf(rejection) == "generation-out-of-range";
            return null;
        }
    }

    /// <summary>
    /// Loads the tree <paramref name="root"/> names into <paramref name="tree"/>, its interior nodes and a reference to
    /// each leaf, returning <see langword="null"/>, or the rejection naming the first rule of ledgerstore.md §7.4 an
    /// object of the tree breaks, met depth-first in child order. Leaves are not read: each is checked only to lie within
    /// its file, disjoint from the tree's other objects, and to have a length some leaf has.
    /// </summary>
    private async ValueTask<InvalidDataException?> LoadTreeAsync(CheckpointRoot root, CheckpointTree tree, CancellationToken cancellationToken)
    {
        if (root.Root.IsNull)
        {
            return null;
        }

        return await LoadNodeAsync(root.Root, root.Generation, root.Height, 0, tree, [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads an interior node and those beneath it into <paramref name="tree"/>, returning the rejection naming the first
    /// rule the node or an object beneath it breaks, or <see langword="null"/>. The objects a tree references occupy
    /// disjoint byte ranges of their files, in its root's <paramref name="generation"/> or older ones (ledgerstore.md
    /// §7.4), so loading reads each node at most once: <paramref name="visited"/> holds the range of every object the tree
    /// has referenced.
    /// </summary>
    private async ValueTask<InvalidDataException?> LoadNodeAsync(
        CheckpointRef reference,
        ulong generation,
        int level,
        long prefix,
        CheckpointTree tree,
        SortedSet<(ulong Generation, ulong Start, ulong End)> visited,
        CancellationToken cancellationToken)
    {
        if (reference.Length != NodeSize)
        {
            return ObjectRejection("node-length", reference);
        }

        if (await PlaceAsync(reference, generation, visited, cancellationToken).ConfigureAwait(false) is { } misplaced)
        {
            return misplaced;
        }

        var bytes = new byte[NodeSize];
        if (await ReadObjectAsync(reference, bytes, cancellationToken).ConfigureAwait(false) is { } unreadable)
        {
            return unreadable;
        }

        var children = new CheckpointRef[SlotLeafCodec.Slots];
        var any = false;
        for (var index = 0; index < children.Length; index++)
        {
            if (CheckpointRef.Decode(bytes.AsSpan(index * CheckpointRef.Size), out children[index]) is { } childRejection)
            {
                return childRejection;
            }

            any |= !children[index].IsNull;
        }

        if (!any)
        {
            return ObjectRejection("node-empty", reference);
        }

        tree.Nodes.Add((level, prefix), children);
        for (var index = 0; index < children.Length; index++)
        {
            var child = children[index];
            if (child.IsNull)
            {
                continue;
            }

            var path = (prefix << SlotLeafCodec.SlotBits) | (long)index;
            var rejection = level > 1
                ? await LoadNodeAsync(child, generation, level - 1, path, tree, visited, cancellationToken).ConfigureAwait(false)
                : await PlaceLeafAsync(child, generation, path, tree, visited, cancellationToken).ConfigureAwait(false);
            if (rejection is not null)
            {
                return rejection;
            }
        }

        return null;
    }

    /// <summary>
    /// Places leaf <paramref name="leaf"/> of a tree without reading it, returning the rejection naming the first rule its
    /// reference breaks, or <see langword="null"/>.
    /// </summary>
    private async ValueTask<InvalidDataException?> PlaceLeafAsync(
        CheckpointRef reference,
        ulong generation,
        long leaf,
        CheckpointTree tree,
        SortedSet<(ulong Generation, ulong Start, ulong End)> visited,
        CancellationToken cancellationToken)
    {
        if (leaf > long.MaxValue >> SlotLeafCodec.SlotBits)
        {
            return ObjectRejection("leaf-beyond-address-space", reference);
        }

        if (reference.Length < SlotLeafCodec.MinimumLength || reference.Length > SlotLeafCodec.MaximumLength)
        {
            return ObjectRejection("leaf-length", reference);
        }

        if (await PlaceAsync(reference, generation, visited, cancellationToken).ConfigureAwait(false) is { } misplaced)
        {
            return misplaced;
        }

        tree.Leaves.Add(leaf, reference);
        return null;
    }

    /// <summary>
    /// Places an object of a tree in its file, returning the rejection naming the first rule its placement breaks, or
    /// <see langword="null"/>: it lies in its root's <paramref name="generation"/> or an older one, as the writer places
    /// it, so no generation file a loaded tree references has a generation above every root slot's; its file exists; it
    /// lies within the file; and its range is disjoint from every other object the tree references.
    /// </summary>
    private async ValueTask<InvalidDataException?> PlaceAsync(
        CheckpointRef reference, ulong generation, SortedSet<(ulong Generation, ulong Start, ulong End)> visited, CancellationToken cancellationToken)
    {
        if (reference.Generation > generation)
        {
            return ObjectRejection("generation-after-root", reference);
        }

        Debug.Assert(reference.Generation < CheckpointRoot.GenerationLimit, "A usable root's generation lies below 2^63, and so do its tree's.");
        WalFile file;
        try
        {
            file = await OpenGenerationAsync(reference.Generation, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return ObjectRejection("generation-missing", reference);
        }

        if (reference.Offset > (ulong)file.Length || reference.Length > (ulong)file.Length - reference.Offset)
        {
            return ObjectRejection("outside-file", reference);
        }

        var range = (reference.Generation, Start: reference.Offset, End: reference.Offset + reference.Length);
        var before = visited.GetViewBetween((range.Generation, 0, 0), range).Max;
        var after = visited.GetViewBetween(range, (range.Generation, ulong.MaxValue, ulong.MaxValue)).Min;
        if (before.Generation == range.Generation && before.End > range.Start
            || after.Generation == range.Generation && after.Start < range.End)
        {
            return ObjectRejection("overlap", reference);
        }

        _ = visited.Add(range);
        return null;
    }

    /// <summary>
    /// Reads an object, which loading placed or a checkpoint wrote, into <paramref name="bytes"/>, its length, and checks
    /// its CRC, returning the rejection of bytes which fail it, <c>crc-mismatch</c> (ledgerstore.md §7.4), or
    /// <see langword="null"/>. A leaf whose object is unusable, or whose file is gone, is
    /// recomputed from the WAL (LS-I3).
    /// </summary>
    /// <exception cref="FileNotFoundException">After loading, the object's file is missing or no longer retained.</exception>
    /// <exception cref="IOException">Reading the object's file failed, which stops the process (conventions.md §14).</exception>
    internal async ValueTask<InvalidDataException?> ReadObjectAsync(CheckpointRef reference, byte[] bytes, CancellationToken cancellationToken)
    {
        Debug.Assert(bytes.Length == reference.Length, "The buffer holds the object.");
        Debug.Assert(reference.Length <= SlotLeafCodec.MaximumLength || reference.Length == NodeSize, "Loading bounds every object's length, so it fits an int.");
        var file = await OpenGenerationAsync(reference.Generation, cancellationToken).ConfigureAwait(false);
        Debug.Assert(
            reference.Offset <= (ulong)file.Length && reference.Length <= (ulong)file.Length - reference.Offset,
            "Loading places every object it reads or keeps within its file, and a checkpoint writes each within its own.");
        using (var read = await file.ReadAsync((long)reference.Offset, bytes.Length, cancellationToken).ConfigureAwait(false))
        {
            // A read is shorter only at the end of the file, which the object lies within.
            var copied = read.CopyTo(bytes);
            Debug.Assert(copied == bytes.Length, "A read within a file returns every byte requested.");
        }

        if (Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes) != reference.Crc)
        {
            return ObjectRejection("crc-mismatch", reference);
        }

        return null;
    }

    /// <summary>
    /// Creates the rejection of the object <paramref name="reference"/> names, or of the tree which references it, for
    /// the rule <paramref name="reason"/> names (ledgerstore.md §7.4).
    /// </summary>
    private static InvalidDataException ObjectRejection(string reason, CheckpointRef reference) => DecodeRejection.Create(reason, reason switch
    {
        "node-length" => "A checkpoint interior node's reference is not 6,144 bytes long.",
        "leaf-beyond-address-space" => "A checkpoint leaf lies beyond the slot address space.",
        "leaf-length" => "A checkpoint leaf reference has a length no leaf has.",
        "generation-after-root" => "A checkpoint object lies in a generation after its root's.",
        "generation-missing" => string.Create(CultureInfo.InvariantCulture, $"Checkpoint generation {reference.Generation} is missing."),
        "outside-file" => "A checkpoint object lies outside its file.",
        "overlap" => "A checkpoint object overlaps another object of its tree.",
        "crc-mismatch" => "A checkpoint object fails its CRC-32C.",
        "node-empty" => "A checkpoint interior node has no children.",
        _ => throw new UnreachableException(),
    });

    /// <summary>
    /// Returns an open handle to a generation file, opening it once.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file is missing, or after loading no longer retained.</exception>
    private async ValueTask<WalFile> OpenGenerationAsync(ulong generation, CancellationToken cancellationToken)
    {
        Task<WalFile> handle;
        lock (_filesLock)
        {
            if (_readable is not null && !_readable.Contains(generation))
            {
                throw new FileNotFoundException($"Checkpoint generation {generation} is no longer retained.", FileName(generation));
            }

            if (!_handles.TryGetValue(generation, out handle!))
            {
                // Callers share the open, so no caller's cancellation ends it.
                handle = OpenFileAsync(_directory, FileName(generation));
                _handles.Add(generation, handle);
            }
        }

        try
        {
            return await handle.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (handle.IsFaulted)
        {
            lock (_filesLock)
            {
                if (_handles.TryGetValue(generation, out var current) && ReferenceEquals(current, handle))
                {
                    _ = _handles.Remove(generation);
                }
            }

            throw;
        }

        // A directory may fail an open before returning its task; the shared task carries that failure as well.
        static async Task<WalFile> OpenFileAsync(WalDirectory directory, string name) =>
            await directory.OpenNamedFileAsync(name, CancellationToken.None).ConfigureAwait(false);
    }
}

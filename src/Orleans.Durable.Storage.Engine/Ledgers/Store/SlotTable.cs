using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// The in-memory slot table: 256-slot leaves keyed by leaf number, each mutable, frozen into its encoding, or unloaded
/// and known only by its checkpoint reference; the set of leaves changed since they were last captured for a
/// checkpoint; and, for each, the WAL positions of the first and last records published to it since (LS-I8;
/// ledgerstore.md §8.8, §8.9).
/// </summary>
/// <remarks>
/// The table is not synchronized: its owner serializes every call. A frozen leaf is immutable, so the owner may decode
/// a reference to one without holding its lock. An unloaded leaf is unchanged since its checkpoint, so its contents
/// are those of its reference; the owner loads it before reading or mutating any of its slots.
/// </remarks>
internal sealed class SlotTable(int settleDistance)
{
    private const int LeafMask = SlotLeafCodec.Slots - 1;

    // Clearing the highest slot first probes this many leaves below it before searching every leaf.
    private const int HighestProbeLeaves = 64;

    private readonly Dictionary<long, SlotLeaf> _leaves = [];
    private readonly Dictionary<long, CheckpointRef> _unloaded = [];
    private readonly HashSet<long> _mutable = [];
    private readonly Dictionary<long, (WalPosition First, WalPosition Last)> _written = [];
    private HashSet<long> _dirty = [];
    private long _version;

    /// <summary>
    /// Gets the highest slot holding a record, or -1 when none does. While
    /// <see cref="UnresolvedHighestLeaf"/> is set, it is an upper bound: the last slot of that leaf.
    /// </summary>
    public long Highest { get; private set; } = -1;

    /// <summary>
    /// Gets the unloaded leaf which must be loaded to resolve <see cref="Highest"/>, or <see langword="null"/> when it
    /// is exact.
    /// </summary>
    public long? UnresolvedHighestLeaf { get; private set; }

    /// <summary>
    /// Gets the number of faulty entries in the resident leaves: faults a read found, which a checkpoint keeps, count once
    /// their leaf loads.
    /// </summary>
    public int FaultyCount { get; private set; }

    /// <summary>
    /// Gets the number of leaves resident in memory.
    /// </summary>
    public int LoadedLeafCount => _leaves.Count;

    /// <summary>
    /// Gets the number of leaves known only by their checkpoint reference.
    /// </summary>
    public int UnloadedLeafCount => _unloaded.Count;

    /// <summary>
    /// Gets the leaf number below which every leaf lies wholly below the slots an in-flight mutation can reach:
    /// mutations in flight at once span fewer than <c>settleDistance</c> slots below the highest.
    /// </summary>
    public long SettledBelow => Math.Max(0, (Highest - settleDistance + 1) >> SlotLeafCodec.SlotBits);

    public static long LeafOf(long slot) => slot >> SlotLeafCodec.SlotBits;

    public bool IsLoaded(long number) => !_unloaded.ContainsKey(number);

    public bool TryGetUnloaded(long number, out CheckpointRef reference) => _unloaded.TryGetValue(number, out reference);

    /// <summary>
    /// Gets a slot's entry; its leaf must be loaded.
    /// </summary>
    public SlotEntry Get(long slot)
    {
        var number = LeafOf(slot);
        if (_leaves.TryGetValue(number, out var leaf))
        {
            return leaf.Get((int)(slot & LeafMask));
        }

        return _unloaded.ContainsKey(number)
            ? throw new InvalidOperationException("The slot's leaf is not loaded.")
            : default;
    }

    /// <summary>
    /// Sets a slot's entry, thawing its leaf, and marks the leaf dirty; a Missing entry removes the slot. The slot's
    /// leaf must be loaded. <paramref name="position"/> is the WAL position of the record establishing the entry, or
    /// <see langword="null"/> for an entry no record establishes, such as a fault a read found.
    /// </summary>
    /// <returns><see langword="true"/> when <see cref="SettledBelow"/> advanced.</returns>
    public bool Set(long slot, SlotEntry entry, WalPosition? position)
    {
        var number = LeafOf(slot);
        if (_unloaded.ContainsKey(number))
        {
            throw new InvalidOperationException("The slot's leaf is not loaded.");
        }

        if (!_leaves.TryGetValue(number, out var leaf))
        {
            if (!entry.HasRecord)
            {
                return false;
            }

            leaf = new SlotLeaf();
            _leaves.Add(number, leaf);
        }

        var settled = SettledBelow;
        var index = (int)(slot & LeafMask);
        FaultyCount += Faulty(entry) - Faulty(leaf.Get(index));
        leaf.Set(index, entry);
        leaf.Version = ++_version;
        _ = _dirty.Add(number);
        if (position is { } at)
        {
            AddWritten(number, (at, at));
        }

        if (leaf.Count == 0)
        {
            _ = _leaves.Remove(number);
            _ = _mutable.Remove(number);
        }
        else
        {
            _ = _mutable.Add(number);
        }

        if (CountsTowardsHighest(entry))
        {
            if (slot > Highest)
            {
                Highest = slot;
                UnresolvedHighestLeaf = null;
            }
        }
        else if (slot == Highest && UnresolvedHighestLeaf is null)
        {
            ResolveHighest(number);
        }

        return SettledBelow > settled;
    }

    /// <summary>
    /// Installs a loaded checkpoint's leaves, unloaded.
    /// </summary>
    public void Load(Dictionary<long, CheckpointRef> leaves)
    {
        foreach (var (number, reference) in leaves)
        {
            _unloaded.Add(number, reference);
        }

        if (_unloaded.Count != 0)
        {
            ResolveHighest(_unloaded.Keys.Max());
        }
    }

    /// <summary>
    /// Installs a loaded leaf's encoding, if the leaf is still unloaded. Its contents are those of its checkpoint.
    /// </summary>
    public void InstallLoaded(long number, FrozenLeaf frozen)
    {
        if (!_unloaded.Remove(number))
        {
            return;
        }

        var leaf = new SlotLeaf(frozen) { Version = ++_version };
        _leaves.Add(number, leaf);
        Span<SlotEntry> entries = stackalloc SlotEntry[SlotLeafCodec.Slots];
        leaf.DecodeAll(entries);
        foreach (var entry in entries)
        {
            FaultyCount += Faulty(entry);
        }

        ContinueResolving(number);
    }

    /// <summary>
    /// Installs entries recomputed for an unloaded leaf whose checkpoint is unusable, marking the leaf dirty so the next
    /// checkpoint replaces it.
    /// </summary>
    public void InstallRebuilt(long number, SlotEntry[] entries)
    {
        if (!_unloaded.Remove(number))
        {
            return;
        }

        _ = _dirty.Add(number);
        var leaf = new SlotLeaf(entries) { Version = ++_version };
        foreach (var entry in entries)
        {
            FaultyCount += Faulty(entry);
        }

        if (leaf.Count != 0)
        {
            _leaves.Add(number, leaf);
            _ = _mutable.Add(number);
            if (UnresolvedHighestLeaf is null && HighestIn(number, leaf) is { } slot && slot > Highest)
            {
                Highest = slot;
            }
        }

        ContinueResolving(number);
    }

    /// <summary>
    /// Points unloaded leaves at the references a checkpoint copied them to.
    /// </summary>
    public void Relocate(Dictionary<long, CheckpointRef> written)
    {
        foreach (var (number, reference) in written)
        {
            if (_unloaded.ContainsKey(number))
            {
                _unloaded[number] = reference;
            }
        }
    }

    /// <summary>
    /// Chooses a checkpoint's cut and captures, at one instant, every dirty leaf and every leaf to copy forward whose
    /// records all lie below it; the others are deferred and stay dirty.
    /// </summary>
    /// <remarks>
    /// The cut starts at <paramref name="frontier"/>, below which every record is published. A leaf whose last record
    /// since its capture lies at or above the cut is deferred, and the cut moves down to that leaf's first such record,
    /// so replaying from the cut restores what its previous checkpoint lacks. Each pass over the leaves written since
    /// their capture defers those reaching the cut, and passes repeat while the cut moves, so no leaf left reaches the
    /// final cut. Only leaves written above the frontier start a deferral, so this normally takes one or two passes. The
    /// checkpoint therefore reflects exactly the records below the cut.
    /// </remarks>
    public CheckpointCapture TakeCheckpoint(WalPosition frontier, IEnumerable<long> compact)
    {
        var candidates = _dirty;
        _dirty = [];
        candidates.UnionWith(compact);
        var cut = frontier;
        var deferred = new HashSet<long>();
        WalPosition bound;
        do
        {
            bound = cut;
            foreach (var (number, written) in _written)
            {
                if (written.Last >= bound && deferred.Add(number) && written.First < cut)
                {
                    cut = written.First;
                }
            }
        }
        while (cut < bound);

        _dirty.UnionWith(deferred);

        var leaves = new List<CapturedLeaf>(candidates.Count - deferred.Count);
        var ranges = new Dictionary<long, (WalPosition First, WalPosition Last)>();
        foreach (var number in candidates)
        {
            if (deferred.Contains(number))
            {
                continue;
            }

            if (_written.Remove(number, out var written))
            {
                ranges.Add(number, written);
            }

            leaves.Add(Capture(number));
        }

        return new CheckpointCapture(cut, leaves, ranges, deferred.Count);
    }

    /// <summary>
    /// Returns a failed checkpoint's leaves to the dirty set with the records they reflected.
    /// </summary>
    public void RestoreCheckpoint(CheckpointCapture capture)
    {
        foreach (var leaf in capture.Leaves)
        {
            _ = _dirty.Add(leaf.Number);
        }

        foreach (var (number, taken) in capture.Ranges)
        {
            AddWritten(number, taken);
        }
    }

    /// <summary>
    /// Widens a leaf's range of records written since its capture.
    /// </summary>
    private void AddWritten(long number, (WalPosition First, WalPosition Last) range)
    {
        if (_written.TryGetValue(number, out var written))
        {
            range = (Min(range.First, written.First), Max(range.Last, written.Last));
        }

        _written[number] = range;

        static WalPosition Min(WalPosition left, WalPosition right) => left < right ? left : right;

        static WalPosition Max(WalPosition left, WalPosition right) => left > right ? left : right;
    }

    /// <summary>
    /// Returns the mutable leaves below <see cref="SettledBelow"/>.
    /// </summary>
    public List<long> FreezeCandidates()
    {
        var below = SettledBelow;
        return [.. _mutable.Where(number => number < below)];
    }

    /// <summary>
    /// Copies a mutable leaf to encode, with its version.
    /// </summary>
    public bool TryCopyMutable(long number, out SlotEntry[] entries, out long version)
    {
        if (!_leaves.TryGetValue(number, out var leaf) || leaf.Frozen is not null)
        {
            _ = _mutable.Remove(number);
            entries = [];
            version = 0;
            return false;
        }

        entries = leaf.Copy();
        version = leaf.Version;
        return true;
    }

    /// <summary>
    /// Installs an encoding of a leaf only if the leaf has not changed since it was copied at <paramref name="version"/>.
    /// </summary>
    public void InstallFrozen(long number, long version, FrozenLeaf frozen)
    {
        if (_leaves.TryGetValue(number, out var leaf) && leaf.Version == version)
        {
            leaf.Freeze(frozen);
            _ = _mutable.Remove(number);
        }
    }

    /// <summary>
    /// Freezes every settled mutable leaf in place; for a single-threaded owner, such as recovery.
    /// </summary>
    public void FreezeSettled()
    {
        foreach (var number in FreezeCandidates())
        {
            if (TryCopyMutable(number, out var entries, out var version))
            {
                InstallFrozen(number, version, FrozenLeaf.Encode(entries));
            }
        }
    }

    /// <summary>
    /// Captures a leaf: its encoding when frozen, a copy of its entries when mutable, its reference when unloaded, or
    /// none of these when the leaf no longer exists.
    /// </summary>
    private CapturedLeaf Capture(long number)
    {
        if (_unloaded.TryGetValue(number, out var reference))
        {
            return new CapturedLeaf(number, null, null, reference);
        }

        if (!_leaves.TryGetValue(number, out var leaf))
        {
            return new CapturedLeaf(number, null, null, null);
        }

        return leaf.Frozen is { } frozen
            ? new CapturedLeaf(number, frozen.Bytes, null, null)
            : new CapturedLeaf(number, null, leaf.Copy(), null);
    }

    /// <summary>
    /// A slot counts towards the highest when it holds a record: DATA, or a faulty entry naming the page of a record. A
    /// faulty entry without a page may hold a lost record, as an absent slot may while a damaged range is known.
    /// </summary>
    private static int Faulty(SlotEntry entry) => entry.Kind == SlotKind.Faulty ? 1 : 0;

    private static bool CountsTowardsHighest(SlotEntry entry) =>
        entry.Kind == SlotKind.Data || entry.Kind == SlotKind.Faulty && entry.Page != default;

    private static long? HighestIn(long number, SlotLeaf leaf)
    {
        Span<SlotEntry> entries = stackalloc SlotEntry[SlotLeafCodec.Slots];
        leaf.DecodeAll(entries);
        for (var index = entries.Length - 1; index >= 0; index--)
        {
            if (CountsTowardsHighest(entries[index]))
            {
                return (number << SlotLeafCodec.SlotBits) | (long)index;
            }
        }

        return null;
    }

    private void ContinueResolving(long number)
    {
        if (UnresolvedHighestLeaf == number)
        {
            ResolveHighest(number);
        }
    }

    /// <summary>
    /// Sets <see cref="Highest"/> to the highest qualifying slot at or below leaf <paramref name="fromLeaf"/>, or to an
    /// upper bound naming the first unloaded leaf the search meets. Leaves are usually dense below the highest, so a
    /// short probe downwards finds it before a search of every leaf.
    /// </summary>
    private void ResolveHighest(long fromLeaf)
    {
        var limit = fromLeaf;
        for (var probe = 0; probe < HighestProbeLeaves && limit >= 0; probe++, limit--)
        {
            if (TryResolveAt(limit))
            {
                return;
            }
        }

        while (limit >= 0)
        {
            var bound = limit;
            var below = Math.Max(
                _leaves.Keys.Where(number => number <= bound).DefaultIfEmpty(-1).Max(),
                _unloaded.Keys.Where(number => number <= bound).DefaultIfEmpty(-1).Max());
            if (below < 0)
            {
                break;
            }

            if (TryResolveAt(below))
            {
                return;
            }

            limit = below - 1;
        }

        Highest = -1;
        UnresolvedHighestLeaf = null;
    }

    private bool TryResolveAt(long number)
    {
        if (_unloaded.ContainsKey(number))
        {
            Highest = (number << SlotLeafCodec.SlotBits) | LeafMask;
            UnresolvedHighestLeaf = number;
            return true;
        }

        if (_leaves.TryGetValue(number, out var leaf) && HighestIn(number, leaf) is { } slot)
        {
            Highest = slot;
            UnresolvedHighestLeaf = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// One leaf captured for a checkpoint: its encoding, a copy of its entries, its checkpoint reference, or none of
    /// these when it no longer exists.
    /// </summary>
    internal readonly record struct CapturedLeaf(long Number, byte[]? Encoded, SlotEntry[]? Entries, CheckpointRef? Reference);

    /// <summary>
    /// A checkpoint's cut and the leaves captured at it, with the record positions they reflect and the number of
    /// leaves deferred.
    /// </summary>
    internal sealed record CheckpointCapture(
        WalPosition Cut, List<CapturedLeaf> Leaves, Dictionary<long, (WalPosition First, WalPosition Last)> Ranges, int Deferred);

    /// <summary>
    /// 256 consecutive slots: mutable entries, or a frozen encoding which a mutation thaws.
    /// </summary>
    private sealed class SlotLeaf
    {
        private SlotEntry[]? _entries;

        public SlotLeaf() => _entries = new SlotEntry[SlotLeafCodec.Slots];

        public SlotLeaf(SlotEntry[] entries)
        {
            _entries = entries;
            Count = entries.Count(static entry => entry.HasRecord);
        }

        public SlotLeaf(FrozenLeaf frozen)
        {
            Frozen = frozen;
            Count = frozen.Count;
        }

        public FrozenLeaf? Frozen { get; private set; }

        /// <summary>
        /// Gets the number of entries.
        /// </summary>
        public int Count { get; private set; }

        public long Version { get; set; }

        public SlotEntry Get(int index) => _entries is { } entries ? entries[index] : Frozen!.Get(index);

        public void DecodeAll(Span<SlotEntry> entries)
        {
            if (_entries is { } mutable)
            {
                mutable.CopyTo(entries);
            }
            else
            {
                Frozen!.DecodeAll(entries);
            }
        }

        public SlotEntry[] Copy()
        {
            var copy = new SlotEntry[SlotLeafCodec.Slots];
            DecodeAll(copy);
            return copy;
        }

        public void Set(int index, SlotEntry entry)
        {
            if (_entries is null)
            {
                _entries = Copy();
                Frozen = null;
            }

            Count += (entry.HasRecord ? 1 : 0) - (_entries[index].HasRecord ? 1 : 0);
            _entries[index] = entry;
        }

        public void Freeze(FrozenLeaf frozen)
        {
            Frozen = frozen;
            _entries = null;
        }
    }
}

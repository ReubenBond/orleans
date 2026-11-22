using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>Cached location and frame CRC-32C of one <c>DATA</c> slot inside an extent.</summary>
internal readonly record struct SlotLocation(int BatchOffset, int BatchLength, uint FrameCrc32c);

/// <summary>Cached byte span of one <c>CLOE</c> append inside an extent.</summary>
internal readonly record struct AppendSpan(Slot FirstSlot, int Count, long StartOffset, long TrailerOffset);

/// <summary>Per-extent index metadata cached from writer appends, trailer walks, and range scans.</summary>
internal sealed class ExtentIndex(Slot firstSlot, ObjectName name)
{
    public Slot FirstSlot { get; } = firstSlot;
    public ObjectName Name { get; } = name;
    public long WalkedUpToOffset { get; set; }
    public Slot? RollNextSlot { get; set; }
    public bool IsFinalized { get; set; }
    public Task<bool>? ActiveWalk { get; set; }
    public List<AppendSpan> Appends { get; } = [];
    public Dictionary<long, SlotLocation> Slots { get; } = [];
}

/// <summary>
/// Thread-safe, bounded cached extent slot index for one <see cref="AppendObjectLedgerStorage"/> instance
/// (object-ledgers.md §8.4). Guards all mutations under one internal lock, never held across awaits.
/// </summary>
internal sealed class AppendExtentIndex
{
    /// <summary>
    /// Soft cap on total cached per-slot <see cref="SlotLocation"/> entries across all extents of one ledger.
    /// <see cref="ExtentIndex.Appends"/> stays intact so evicted slots reload on demand in one append-span read.
    /// </summary>
    internal const int MaxCachedSlots = 16_384;

    private readonly Lock _lock = new();
    private readonly List<ExtentIndex> _extents = [];
    private int _cachedSlotCount;

    /// <summary>Returns the cached extent for <paramref name="firstSlot"/>, creating and inserting it in slot order if absent.</summary>
    public ExtentIndex GetOrAddExtent(Slot firstSlot, ObjectName name)
    {
        lock (_lock)
        {
            return GetOrAddExtentLocked(firstSlot, name);
        }
    }

    /// <summary>Returns the extent's current <see cref="ExtentIndex.WalkedUpToOffset"/> and <see cref="ExtentIndex.IsFinalized"/>.</summary>
    public (long WalkedUpToOffset, bool IsFinalized) GetWalkState(ExtentIndex extent)
    {
        lock (_lock)
        {
            return (extent.WalkedUpToOffset, extent.IsFinalized);
        }
    }

    /// <summary>
    /// Joins an in-flight trailer walk for <paramref name="extent"/> or claims ownership of starting one,
    /// coalescing concurrent cold readers onto a single walk per extent without invoking callbacks under the lock.
    /// </summary>
    public (Task<bool> WalkTask, TaskCompletionSource<bool>? Owner, bool JoinedExisting) TryJoinOrClaimWalk(ExtentIndex extent)
    {
        lock (_lock)
        {
            if (extent.IsFinalized)
            {
                return (Task.FromResult(false), null, false);
            }

            if (extent.ActiveWalk is { } active)
            {
                return (active, null, true);
            }

            var owner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            extent.ActiveWalk = owner.Task;
            return (owner.Task, owner, false);
        }
    }

    /// <summary>Clears the active walk reference on <paramref name="extent"/> once <paramref name="walkTask"/> finishes.</summary>
    public void ClearActiveWalk(ExtentIndex extent, Task<bool> walkTask)
    {
        lock (_lock)
        {
            if (ReferenceEquals(extent.ActiveWalk, walkTask))
            {
                extent.ActiveWalk = null;
            }
        }
    }

    /// <summary>Attempts to resolve <paramref name="slot"/> directly from the cached per-slot map.</summary>
    public bool TryGetCachedSlot(Slot slot, out ObjectName? extentName, out SlotLocation location)
    {
        lock (_lock)
        {
            var extent = FindCachedExtentLocked(slot);
            if (extent is not null && extent.Slots.TryGetValue(slot.Value, out location))
            {
                extentName = extent.Name;
                return true;
            }
        }

        extentName = null;
        location = default;
        return false;
    }

    /// <summary>Attempts to find a cached <see cref="AppendSpan"/> covering <paramref name="slot"/> across all extents.</summary>
    public bool TryGetCachedAppendSpan(Slot slot, out ExtentIndex? extent, out AppendSpan span)
    {
        lock (_lock)
        {
            var candidate = FindCachedExtentLocked(slot);
            if (candidate is not null && TryFindAppendSpanLocked(candidate, slot, out span))
            {
                extent = candidate;
                return true;
            }
        }

        extent = null;
        span = default;
        return false;
    }

    /// <summary>Attempts to find a cached <see cref="AppendSpan"/> covering <paramref name="slot"/> inside <paramref name="extent"/>.</summary>
    public bool TryGetCachedAppendSpan(ExtentIndex extent, Slot slot, out AppendSpan span)
    {
        lock (_lock)
        {
            return TryFindAppendSpanLocked(extent, slot, out span);
        }
    }

    /// <summary>
    /// Attempts to return the end byte offset of the cached <see cref="AppendSpan"/> containing
    /// <paramref name="lastSlot"/> inside <paramref name="extent"/> so a sequential scan can clamp its ranged read.
    /// </summary>
    public bool TryGetCachedSpanEndOffset(ExtentIndex extent, Slot lastSlot, out long endOffset)
    {
        lock (_lock)
        {
            if (TryFindAppendSpanLocked(extent, lastSlot, out var span))
            {
                endOffset = span.TrailerOffset + ObjectLedgerExtentFormat.TrailerLength;
                return true;
            }
        }

        endOffset = 0;
        return false;
    }

    /// <summary>Records a newly appended batch's span and slot locations from the writer.</summary>
    public void RecordAppendedBatch(
        long extentFirstSlot,
        ObjectName extentName,
        Slot firstSlot,
        int count,
        long startOffset,
        long newOffset,
        SlotLocation[] locations)
    {
        lock (_lock)
        {
            var extent = GetOrAddExtentLocked(new Slot(extentFirstSlot), extentName);
            if (startOffset >= extent.WalkedUpToOffset)
            {
                extent.Appends.Add(new AppendSpan(firstSlot, count, startOffset, newOffset - ObjectLedgerExtentFormat.TrailerLength));
                extent.WalkedUpToOffset = newOffset;
            }

            for (var i = 0; i < locations.Length; i++)
            {
                PutSlotLocked(extent, firstSlot.Value + i, locations[i]);
            }
        }
    }

    /// <summary>
    /// Records that an extent ended with a <c>ROLL</c> frame naming <paramref name="nextSlot"/>.
    /// Does not insert a <c>ROLL</c>-only append into <see cref="ExtentIndex.Appends"/> so it never collides with
    /// the successor extent's <see cref="ExtentIndex.FirstSlot"/>.
    /// </summary>
    public void RecordRolledExtent(long extentFirstSlot, ObjectName extentName, Slot nextSlot, long endOffset)
    {
        lock (_lock)
        {
            var extent = GetOrAddExtentLocked(new Slot(extentFirstSlot), extentName);
            if (endOffset > extent.WalkedUpToOffset)
            {
                extent.WalkedUpToOffset = endOffset;
            }

            extent.RollNextSlot = nextSlot;
            extent.IsFinalized = true;
        }
    }

    /// <summary>Records appends discovered by a trailer walk or sequential range scan (without populating per-slot entries).</summary>
    public void RecordWalkedAppends(
        ExtentIndex extent,
        List<AppendSpan> newlyWalked,
        long walkedUpToOffset,
        Slot? rollNext)
    {
        lock (_lock)
        {
            if (walkedUpToOffset > extent.WalkedUpToOffset)
            {
                foreach (var span in newlyWalked)
                {
                    if (span.StartOffset >= extent.WalkedUpToOffset)
                    {
                        extent.Appends.Add(span);
                        extent.WalkedUpToOffset = span.TrailerOffset + ObjectLedgerExtentFormat.TrailerLength;
                    }
                }

                if (walkedUpToOffset > extent.WalkedUpToOffset)
                {
                    extent.WalkedUpToOffset = walkedUpToOffset;
                }
            }

            if (rollNext is not null)
            {
                extent.RollNextSlot = rollNext;
                extent.IsFinalized = true;
            }
        }
    }

    /// <summary>Records per-slot locations indexed from one <see cref="AppendSpan"/> (and optional trailing <c>ROLL</c>).</summary>
    public void RecordIndexedSlots(
        ExtentIndex extent,
        List<(long Slot, SlotLocation Location)> parsed,
        Slot? rollNext)
    {
        lock (_lock)
        {
            foreach (var (slot, location) in parsed)
            {
                PutSlotLocked(extent, slot, location);
            }

            if (rollNext is not null)
            {
                extent.RollNextSlot = rollNext;
                extent.IsFinalized = true;
            }
        }
    }

    /// <summary>Records one slot location loaded directly by a single-frame point read.</summary>
    public void RecordSingleSlot(ExtentIndex extent, Slot slot, SlotLocation location)
    {
        lock (_lock)
        {
            PutSlotLocked(extent, slot.Value, location);
        }
    }

    private ExtentIndex GetOrAddExtentLocked(Slot firstSlot, ObjectName name)
    {
        var lo = 0;
        var hi = _extents.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var cmp = _extents[mid].FirstSlot.CompareTo(firstSlot);
            if (cmp == 0)
            {
                return _extents[mid];
            }

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var created = new ExtentIndex(firstSlot, name);
        _extents.Insert(lo, created);
        return created;
    }

    private ExtentIndex? FindCachedExtentLocked(Slot slot)
    {
        var lo = 0;
        var hi = _extents.Count - 1;
        ExtentIndex? match = null;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_extents[mid].FirstSlot <= slot)
            {
                match = _extents[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return match;
    }

    private static bool TryFindAppendSpanLocked(ExtentIndex extent, Slot slot, out AppendSpan span)
    {
        if (extent.RollNextSlot is { } roll && slot >= roll)
        {
            span = default;
            return false;
        }

        var appends = extent.Appends;
        var lo = 0;
        var hi = appends.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var candidate = appends[mid];
            if (slot < candidate.FirstSlot)
            {
                hi = mid - 1;
            }
            else if (slot.Value >= candidate.FirstSlot.Value + candidate.Count)
            {
                lo = mid + 1;
            }
            else
            {
                span = candidate;
                return true;
            }
        }

        span = default;
        return false;
    }

    private void PutSlotLocked(ExtentIndex extent, long slot, SlotLocation location)
    {
        if (!extent.Slots.ContainsKey(slot))
        {
            if (_cachedSlotCount >= MaxCachedSlots)
            {
                EvictSlotsLocked(keep: extent);
            }

            _cachedSlotCount += 1;
        }

        extent.Slots[slot] = location;
    }

    private void EvictSlotsLocked(ExtentIndex keep)
    {
        // Prefer evicting older extents' slot maps first; if only the active extent holds entries, clear it.
        foreach (var candidate in _extents)
        {
            if (!ReferenceEquals(candidate, keep) && candidate.Slots.Count > 0)
            {
                _cachedSlotCount -= candidate.Slots.Count;
                candidate.Slots.Clear();
                if (_cachedSlotCount < MaxCachedSlots)
                {
                    return;
                }
            }
        }

        if (_cachedSlotCount >= MaxCachedSlots && keep.Slots.Count > 0)
        {
            _cachedSlotCount -= keep.Slots.Count;
            keep.Slots.Clear();
        }
    }
}

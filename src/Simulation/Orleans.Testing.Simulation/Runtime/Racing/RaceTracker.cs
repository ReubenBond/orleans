using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>Scheduler-owned vector-clock race detector.</summary>
/// <remarks>
/// <para>
/// Happens-before is the transitive closure of program order within an operation, the edge from an operation to each
/// operation it registers, and the release-to-acquire edges of controlled synchronization: an exclusive lock's exit (or
/// the end of an operation which still holds it) to its next enter, every signal of an object to each later wait on it,
/// and a resource signal to the wait it resolves. Two accesses to one location race when at least one is a write and
/// neither happens before the other. Every recorded lock is exclusive and every release of it publishes an edge, so two
/// accesses under a common lock are ordered, unless two operations held the lock at once as one flow (see
/// <see cref="EnterExclusive"/>), in which case they race; the locks an access holds only label reports. A release
/// takes effect when the flow's hold ends (see <see cref="ExitExclusive"/>), so a concurrent holder never acquires the
/// other's accesses through an inner exit.
/// </para>
/// <para>
/// Clock components are keyed by clock slot rather than by operation. An operation takes its slot at its first event:
/// it continues a finished slot whose last event its clock already covers, whichever slot that is (an await
/// continuation may cover the awaited operation's last event, or its own fork's), and otherwise starts a new slot. So
/// every event of a slot happens before every later event of it, the events of a slot are totally ordered, an access is
/// kept as its slot and epoch, and an earlier access happens before a later one exactly when the later access's clock
/// covers the earlier one's epoch. Slots are inferred from clock coverage rather than taken from an identity another
/// layer supplies, such as <see cref="Threading.SimulationSynchronizationFlow"/>'s logical strand, so the detector's
/// correctness depends on no other layer's hint.
/// </para>
/// <para>
/// A location keeps its last write and the reads since it which are mutually unordered: a read drops every kept read
/// its clock covers. That loses no race: if a read R1 happens before a read R2 and a later write W does not follow R1,
/// it does not follow R2 either, and W cannot precede R2, which ran first. A race with the last write names the write;
/// otherwise it names the racing read which ran first.
/// </para>
/// <para>
/// An operation's state is dropped when it ends. A finished slot stays live while a live location keeps one of its
/// accesses. A clock entry decides nothing once its slot has ended, or has finished and the entry is below every epoch
/// the slot still keeps: it covers no kept access, and below the slot's last epoch it cannot continue the slot. Copies
/// keep only deciding entries, and a clock merged into is pruned once more slots have changed since its last prune than
/// half its size. A location of an object lives as long as the object: a sweep, due once the locations of objects have
/// doubled since the last, releases the accesses of collected objects as an operation completes or an object location
/// is added. Lock and signal clocks live as long as their synchronization object. So the live slots
/// are the running operations' plus those of mutually unordered accesses kept on live locations. A copy holds only
/// entries of live slots; each slot change stops at most one entry of a clock deciding, so a clock just merged into
/// holds at most twice as many entries as there are live slots, and between merges it grows only by its own slot.
/// </para>
/// <para>
/// No race verdict, race report, or scheduling decision depends on slot identity or GC timing. Verdicts are exact
/// happens-before; reports name operations, call sites, and locks numbered in first-use order; and a collected object
/// is never accessed again. GC timing changes only memory use and slot numbering.
/// </para>
/// </remarks>
internal sealed class RaceTracker
{
    // The fewest locations of objects for which completion sweeps for collected objects; after a sweep, the next waits
    // until the surviving objects' locations have doubled, so sweeping costs amortized O(1) per location.
    private const int DefaultMinimumSweepThreshold = 64;

    private readonly ConditionalWeakTable<object, TrackedObject> _objects = new();
    private readonly List<TrackedObject> _trackedObjects = [];
    private readonly Dictionary<RaceMemoryLocation, LocationState> _staticLocations = [];
    private readonly Dictionary<string, string> _collectionMembers = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<object, LockState> _locks = new();
    private readonly ConditionalWeakTable<object, Clock> _signals = new();
    private readonly Dictionary<long, OperationState> _operations = [];
    private readonly Dictionary<long, SlotState> _slots = [];
    private readonly int _minimumSweepThreshold;
    private long _nextObjectId;
    private long _nextLockId;
    private long _nextSlotId;
    private long _nextAccessSequence;

    // Counts the slot changes which can stop a clock entry deciding anything: a slot finishing while it keeps accesses,
    // a slot ending, and a finished slot's oldest kept access being released.
    private long _slotChanges;
    private int _sweepThreshold;
    private int _locations;
    private int _objectLocations;
    private int _retainedAccesses;
    private int _peakSlots;
    private int _sweeps;
    private int _largestClock;

    public RaceTracker(int minimumSweepThreshold = DefaultMinimumSweepThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumSweepThreshold);
        _minimumSweepThreshold = _sweepThreshold = minimumSweepThreshold;
    }

    public RaceReport? FirstRace { get; private set; }

    /// <summary>Gets the size of the tracker's retained state.</summary>
    public RaceTrackerStatistics Statistics =>
        new(_operations.Count, _slots.Count, _peakSlots, _nextSlotId, _largestClock, _locations, _retainedAccesses, _sweeps);

    /// <summary>Registers <paramref name="operation"/>, ordered after its <paramref name="parent"/>'s events so far.</summary>
    public void RegisterOperation(SimulationOperation operation, SimulationOperation? parent)
    {
        Clock clock;
        if (parent is null)
        {
            clock = new Clock(_slotChanges);
        }
        else
        {
            bool registered = _operations.TryGetValue(parent.Id.Value, out OperationState? parentState);
            Debug.Assert(registered, "A parent is the running operation, which is registered.");
            if (parentState is null)
            {
                clock = new Clock(_slotChanges);
            }
            else
            {
                Tick(parentState);
                clock = CopyDeciding(parentState.Clock);
            }
        }

        Debug.Assert(!_operations.ContainsKey(operation.Id.Value), "An operation is registered once.");
        _operations.Add(operation.Id.Value, new OperationState(clock));
    }

    /// <summary>
    /// Drops <paramref name="operation"/>'s state. The caller guarantees the operation records no further event: its
    /// body has returned or unwound, or never started. A lock it still holds stays held by its flow, and publishes the
    /// operation's clock to the next acquisition.
    /// </summary>
    public void CompleteOperation(SimulationOperation operation)
    {
        bool registered = _operations.Remove(operation.Id.Value, out OperationState? state);
        Debug.Assert(registered, "An operation's race state is completed once, after it was registered.");
        if (state is null)
        {
            return;
        }

        foreach ((LockState held, _) in state.Held)
        {
            held.Holders.Remove(state);
            Publish(held, state, state.Clock.Epochs[state.SlotId]);
        }

        state.Held.Clear();
        if (state.SlotId != 0)
        {
            SlotState slot = _slots[state.SlotId];
            slot.FinalEpoch = state.Clock.Epochs[state.SlotId];
            if (slot.Oldest is null)
            {
                EndSlot(state.SlotId);
            }
            else
            {
                _slotChanges++;
            }
        }

        SweepIfDue();
    }

    /// <summary>Resolves the tracked state of a logical location, or <see langword="null"/> for an untracked access.</summary>
    public LocationState? ResolveLocation(
        RaceMemoryLocationKind? kind,
        object? target,
        string member,
        long? elementIndex)
    {
        if (kind is null)
        {
            return null;
        }

        if (kind == RaceMemoryLocationKind.StaticField && target is null)
        {
            return GetOrAddLocation(_staticLocations, new RaceMemoryLocation(kind.Value, 0, member));
        }

        if (target is null)
        {
            return null;
        }

        if (!_objects.TryGetValue(target, out TrackedObject? tracked))
        {
            tracked = new TrackedObject(++_nextObjectId, target);
            _objects.Add(target, tracked);
            _trackedObjects.Add(tracked);
        }

        string locationMember = kind == RaceMemoryLocationKind.Collection ? CollectionMember(member) : member;
        int before = tracked.Locations.Count;
        LocationState location = GetOrAddLocation(
            tracked.Locations,
            new RaceMemoryLocation(kind.Value, tracked.Id, locationMember, elementIndex));
        _objectLocations += tracked.Locations.Count - before;
        SweepIfDue();

        // The sweep must not find the object being resolved collected, which would orphan the access about to be kept.
        GC.KeepAlive(target);
        return location;
    }

    /// <summary>
    /// Records a read or write of <paramref name="location"/>, and returns the earlier access it races with, if any. The
    /// first race in the run becomes <see cref="FirstRace"/>.
    /// </summary>
    public RacingAccess? RecordAccess(
        SimulationOperation operation,
        RaceAccessKind kind,
        LocationState location,
        RaceSourceLocation source,
        IReadOnlyList<RaceSchedulingPoint> trace)
    {
        Debug.Assert(kind is RaceAccessKind.Read or RaceAccessKind.Write, "Only reads and writes have a location.");
        OperationState state = StateOf(operation);
        long epoch = Tick(state);

        // Name the race before the location's accesses are updated, since the update reuses their records.
        TrackedAccess? raced = location.LastWrite is { } write && !HappensBefore(write, state.Clock)
            ? write
            : kind == RaceAccessKind.Write ? FirstRacingRead(location, state.Clock) : null;
        RacingAccess? racing = null;
        if (raced is not null)
        {
            racing = new RacingAccess(raced.OperationId, raced.Kind, raced.Source);
            FirstRace ??= new RaceReport
            {
                FirstAccess = CreatePublicAccess(raced, location),
                SecondAccess = new RaceAccessRecord(
                    operation.Id,
                    kind,
                    location.Location,
                    source,
                    Labels(state.HeldIds)),
                ScheduleTrace = [.. trace],
            };
        }

        TrackedAccess access = kind == RaceAccessKind.Read
            ? KeepRead(location, state)
            : KeepWrite(location);
        access.OperationId = operation.Id;
        access.SlotId = state.SlotId;
        access.Epoch = epoch;
        access.Sequence = ++_nextAccessSequence;
        access.Kind = kind;
        access.Source = source;
        access.HeldIds = state.HeldIds;
        Retain(access);
        return racing;
    }

    public bool HasHeldSynchronization(SimulationOperation operation) => StateOf(operation).Held.Count != 0;

    /// <summary>
    /// Records an entry of an exclusive lock, which one flow (<see cref="Threading.SimulationSynchronizationFlow"/>'s
    /// logical strand) holds at a time. Every entry acquires the clock the lock's releases have published, so every
    /// access under the lock happens before every later access under it. An operation which enters a lock another
    /// running operation of its flow holds, which happens only where an operation runs as another's flow, acquires those
    /// releases but not the other holder's accesses: the two hold the lock at once, and their accesses are unordered by
    /// it. Shared holds, such as a reader lock, publish and consume edges through <see cref="SignalSynchronization"/>
    /// and <see cref="WaitSynchronization"/> instead.
    /// </summary>
    public void EnterExclusive(SimulationOperation operation, object synchronization)
    {
        OperationState state = StateOf(operation);
        LockState lockState = LockOf(synchronization);

        // Covering the last release's own event covers its whole clock, apart from entries which decide nothing.
        if (lockState.Release is { } release &&
            !Covers(state.Clock, lockState.ReleaseSlotId, lockState.ReleaseEpoch))
        {
            MergeDeciding(state.Clock, release);
        }

        // Only an entry no running operation's hold precedes stands for the release clock: a concurrent holder's later
        // exit may join its own clock to it, which the entering operation does not cover.
        bool acquires = lockState.Holders.Count == 0 && lockState.Release is not null;

        int held = state.IndexOfHeld(lockState);
        if (held < 0)
        {
            state.Held.Add((lockState, 1));
            state.RefreshHeldIds();
            lockState.Holders.Add(state);
        }
        else
        {
            state.Held[held] = (lockState, state.Held[held].Recursion + 1);
        }

        lockState.Recursion++;
        long epoch = Tick(state);
        if (acquires)
        {
            // The acquiring event's clock covers the release clock, so an exit which covers it may replace that clock.
            lockState.ReleaseSlotId = state.SlotId;
            lockState.ReleaseEpoch = epoch;
        }
    }

    /// <summary>
    /// Records an exit of an exclusive lock by the flow holding it, which publishes the exiting operation's clock: by the
    /// operation which entered it, or by another operation of its flow, after the entering one has ended or while it
    /// still holds the lock. While a running operation still holds the lock, an exit (an inner exit of a recursive hold,
    /// or one holder's of two) publishes to the next acquisition only once the flow's hold ends, so a concurrent holder
    /// acquires only the releases from before the hold began.
    /// </summary>
    public void ExitExclusive(SimulationOperation operation, object synchronization)
    {
        OperationState state = StateOf(operation);
        LockState lockState = LockOf(synchronization);
        if (lockState.Recursion == 0)
        {
            // The shims reject an exit of a lock the flow does not hold before it reaches here.
            return;
        }

        long epoch = Tick(state);
        lockState.Recursion--;
        OperationState? holder = state.IndexOfHeld(lockState) >= 0
            ? state
            : lockState.Holders.Count != 0 ? lockState.Holders[0] : null;
        if (holder is not null)
        {
            int held = holder.IndexOfHeld(lockState);
            int recursion = holder.Held[held].Recursion - 1;
            if (recursion == 0)
            {
                holder.Held.RemoveAt(held);
                holder.RefreshHeldIds();
                lockState.Holders.Remove(holder);
            }
            else
            {
                holder.Held[held] = (lockState, recursion);
            }
        }

        Publish(lockState, state, epoch);
    }

    public void SignalSynchronization(SimulationOperation operation, object synchronization)
    {
        OperationState state = StateOf(operation);
        Tick(state);
        if (_signals.TryGetValue(synchronization, out Clock? aggregate))
        {
            MergeDeciding(aggregate, state.Clock);
        }
        else
        {
            _signals.Add(synchronization, CopyDeciding(state.Clock));
        }
    }

    public void WaitSynchronization(SimulationOperation operation, object synchronization)
    {
        OperationState state = StateOf(operation);
        if (_signals.TryGetValue(synchronization, out Clock? release))
        {
            MergeDeciding(state.Clock, release);
        }

        Tick(state);
    }

    public Clock CaptureRelease(SimulationOperation operation)
    {
        OperationState state = StateOf(operation);
        Tick(state);
        return CopyDeciding(state.Clock);
    }

    public void ConsumeRelease(SimulationOperation operation, Clock release)
    {
        OperationState state = StateOf(operation);
        MergeDeciding(state.Clock, release);
        Tick(state);
    }

    /// <summary>Gets the number of components in <paramref name="operation"/>'s clock.</summary>
    internal int ClockSize(SimulationOperation operation) => StateOf(operation).Clock.Epochs.Count;

    /// <summary>
    /// Checks the tracker's bookkeeping: the live slots are exactly those running operations own and those kept
    /// accesses name, each slot keeps its accesses in epoch order, and the counts match the accesses locations keep.
    /// </summary>
    /// <returns>A description of each violation found.</returns>
    internal List<string> CheckInvariants()
    {
        var violations = new List<string>();
        var expected = new Dictionary<long, List<TrackedAccess>>();
        foreach (OperationState state in _operations.Values)
        {
            if (state.SlotId != 0 && !expected.TryAdd(state.SlotId, []))
            {
                violations.Add($"Two running operations own slot {state.SlotId}.");
            }
        }

        int locations = 0;
        foreach (LocationState location in AllLocations())
        {
            locations++;
            for (int index = 0; index < location.ReaderCount; index++)
            {
                AddKept(location.Readers[index]);
            }

            if (location.LastWrite is { } write)
            {
                AddKept(write);
            }
        }

        if (locations != _locations)
        {
            violations.Add($"{_locations} locations are counted but {locations} are live.");
        }

        int retained = 0;
        foreach ((long slotId, List<TrackedAccess> kept) in expected)
        {
            retained += kept.Count;
            if (!_slots.TryGetValue(slotId, out SlotState? slot))
            {
                violations.Add($"Slot {slotId} is in use but has ended.");
                continue;
            }

            var chain = new List<TrackedAccess>();
            for (TrackedAccess? access = slot.Oldest; access is not null; access = access.Newer)
            {
                chain.Add(access);
            }

            if (chain.Count != kept.Count || chain.Except(kept).Any() ||
                chain.Zip(chain.Skip(1)).Any(static pair => pair.First.Epoch >= pair.Second.Epoch))
            {
                violations.Add($"Slot {slotId} keeps {chain.Count} accesses, not the {kept.Count} its locations keep, in epoch order.");
            }
        }

        foreach (long slotId in _slots.Keys.Where(slotId => !expected.ContainsKey(slotId)))
        {
            violations.Add($"Slot {slotId} is live but neither owned nor kept.");
        }

        if (retained != _retainedAccesses)
        {
            violations.Add($"{_retainedAccesses} accesses are counted as kept but locations keep {retained}.");
        }

        return violations;

        void AddKept(TrackedAccess access)
        {
            if (!expected.TryGetValue(access.SlotId, out List<TrackedAccess>? kept))
            {
                kept = [];
                expected.Add(access.SlotId, kept);
            }

            kept.Add(access);
        }
    }

    // Publishes the clock of the event at epoch in state's slot. While a running operation holds the lock, it is held
    // back as pending; once none does, it and anything pending become the release clock the next acquisition takes.
    private void Publish(LockState lockState, OperationState state, long epoch)
    {
        if (lockState.Holders.Count != 0)
        {
            PublishInto(
                ref lockState.Pending,
                ref lockState.PendingSlotId,
                ref lockState.PendingEpoch,
                !lockState.HasPending,
                state,
                epoch);
            lockState.HasPending = true;
            return;
        }

        PublishInto(
            ref lockState.Release,
            ref lockState.ReleaseSlotId,
            ref lockState.ReleaseEpoch,
            lockState.Release is null,
            state,
            epoch);
        if (lockState.HasPending)
        {
            lockState.HasPending = false;
            if (!Covers(state.Clock, lockState.PendingSlotId, lockState.PendingEpoch))
            {
                MergeDeciding(lockState.Release!, lockState.Pending!);
                lockState.ReleaseSlotId = 0;
            }
        }
    }

    // A clock which covers the previous publication's event replaces its clock, in place; otherwise the two are joined,
    // and no single event's coverage implies the joined clock.
    private void PublishInto(
        ref Clock? clock,
        ref long publishedSlotId,
        ref long publishedEpoch,
        bool isEmpty,
        OperationState state,
        long epoch)
    {
        bool coversPrevious = isEmpty || Covers(state.Clock, publishedSlotId, publishedEpoch);
        if (clock is null || coversPrevious)
        {
            clock = CopyDeciding(state.Clock, clock);
        }
        else
        {
            MergeDeciding(clock, state.Clock);
        }

        publishedSlotId = coversPrevious ? state.SlotId : 0;
        publishedEpoch = epoch;
    }

    private static bool HappensBefore(TrackedAccess previous, Clock current) =>
        Covers(current, previous.SlotId, previous.Epoch);

    private static bool Covers(Clock clock, long slotId, long epoch) =>
        slotId != 0 && clock.Epochs.TryGetValue(slotId, out long covered) && covered >= epoch;

    // A race with several reads names the one which ran first, which no slot numbering or collection can change.
    private static TrackedAccess? FirstRacingRead(LocationState location, Clock clock)
    {
        TrackedAccess? first = null;
        for (int index = 0; index < location.ReaderCount; index++)
        {
            TrackedAccess read = location.Readers[index];
            if (!HappensBefore(read, clock) && (first is null || read.Sequence < first.Sequence))
            {
                first = read;
            }
        }

        return first;
    }

    // Drops the kept reads the reader's clock covers, and returns a record for the new read: the reader's own earlier
    // read, which its clock always covers, or else a read record the location released, is reused.
    private TrackedAccess KeepRead(LocationState location, OperationState state)
    {
        TrackedAccess? own = null;
        TrackedAccess[] readers = location.Readers;
        int kept = 0;
        for (int index = 0; index < location.ReaderCount; index++)
        {
            TrackedAccess read = readers[index];
            if (!HappensBefore(read, state.Clock))
            {
                readers[kept++] = read;
                continue;
            }

            Release(read);
            if (read.SlotId == state.SlotId)
            {
                own = read;
            }
            else
            {
                location.SpareRead ??= read;
            }
        }

        Array.Clear(readers, kept, location.ReaderCount - kept);
        location.ReaderCount = kept;
        if (kept == readers.Length)
        {
            Array.Resize(ref location.Readers, Math.Max(2, 2 * kept));
        }

        TrackedAccess? spare = location.SpareRead;
        location.SpareRead = null;
        TrackedAccess access = own ?? spare ?? new TrackedAccess(location);
        location.Readers[location.ReaderCount++] = access;
        return access;
    }

    // Releases every kept access and returns a record for the new write, reusing the previous write's.
    private TrackedAccess KeepWrite(LocationState location)
    {
        TrackedAccess? previous = location.LastWrite;
        ReleaseAll(location);
        return location.LastWrite = previous ?? new TrackedAccess(location);
    }

    private void ReleaseAll(LocationState location)
    {
        for (int index = 0; index < location.ReaderCount; index++)
        {
            Release(location.Readers[index]);
        }

        if (location.ReaderCount != 0)
        {
            location.SpareRead ??= location.Readers[0];
        }

        Array.Clear(location.Readers, 0, location.ReaderCount);
        location.ReaderCount = 0;
        if (location.LastWrite is { } write)
        {
            Release(write);
            location.LastWrite = null;
        }
    }

    // Appends a newly kept access to its slot, whose accesses are kept in epoch order because only the slot's owner
    // records them.
    private void Retain(TrackedAccess access)
    {
        SlotState slot = _slots[access.SlotId];
        Debug.Assert(slot.IsOwned, "Only a slot's owner records an access in it.");
        Debug.Assert(slot.Newest is null || slot.Newest.Epoch < access.Epoch, "A slot's accesses are kept in epoch order.");
        access.Older = slot.Newest;
        access.Newer = null;
        if (slot.Newest is null)
        {
            slot.Oldest = access;
        }
        else
        {
            slot.Newest.Newer = access;
        }

        slot.Newest = access;
        _retainedAccesses++;
    }

    private void Release(TrackedAccess access)
    {
        SlotState slot = _slots[access.SlotId];
        bool wasOldest = slot.Oldest == access;
        if (access.Older is null)
        {
            slot.Oldest = access.Newer;
        }
        else
        {
            access.Older.Newer = access.Newer;
        }

        if (access.Newer is null)
        {
            slot.Newest = access.Older;
        }
        else
        {
            access.Newer.Older = access.Older;
        }

        access.Older = access.Newer = null;
        _retainedAccesses--;
        if (slot.IsOwned)
        {
            return;
        }

        if (slot.Oldest is null)
        {
            EndSlot(access.SlotId);
        }
        else if (wasOldest)
        {
            _slotChanges++;
        }
    }

    private void EndSlot(long slotId)
    {
        _slots.Remove(slotId);
        _slotChanges++;
    }

    // Sweeps once the locations of objects reach the threshold, both as an operation completes and as an object location
    // is added, so a long-running operation which allocates is swept too. The trigger counts locations, never collections.
    private void SweepIfDue()
    {
        if (_objectLocations >= _sweepThreshold)
        {
            SweepCollectedObjects();
        }
    }

    // Releases the accesses kept on the locations of collected objects. Which objects have been collected depends on
    // GC timing, which this changes only memory use and slot numbering for: a collected object is never accessed again.
    private void SweepCollectedObjects()
    {
        int live = 0;
        bool released = false;
        for (int index = 0; index < _trackedObjects.Count; index++)
        {
            TrackedObject tracked = _trackedObjects[index];
            if (tracked.Target.TryGetTarget(out _))
            {
                _trackedObjects[live++] = tracked;
                continue;
            }

            foreach (LocationState location in tracked.Locations.Values)
            {
                ReleaseAll(location);
            }

            _locations -= tracked.Locations.Count;
            _objectLocations -= tracked.Locations.Count;
            released = true;
        }

        if (released)
        {
            _sweeps++;
        }

        _trackedObjects.RemoveRange(live, _trackedObjects.Count - live);
        _sweepThreshold = Math.Max(_minimumSweepThreshold, 2 * _objectLocations);
    }

    /// <summary>Records the operation's next event in its slot, taking a slot at its first event.</summary>
    /// <returns>The event's epoch.</returns>
    private long Tick(OperationState state)
    {
        if (state.SlotId == 0)
        {
            state.SlotId = ContinueOrStartSlot(state.Clock);
        }

        ref long epoch = ref CollectionsMarshal.GetValueRefOrAddDefault(state.Clock.Epochs, state.SlotId, out _);
        epoch++;
        _largestClock = Math.Max(_largestClock, state.Clock.Epochs.Count);
        return epoch;
    }

    // Continues a finished slot whose last event the clock covers. Only a slot's owner advances its epoch, so no clock
    // holds more than the final epoch: equality is coverage. The lowest such slot is taken so that, absent collection,
    // slot numbering is reproducible; no output depends on it.
    private long ContinueOrStartSlot(Clock clock)
    {
        long continued = 0;
        SlotState? continuedSlot = null;
        foreach ((long slotId, long epoch) in clock.Epochs)
        {
            if ((continued == 0 || slotId < continued) &&
                _slots.TryGetValue(slotId, out SlotState? slot) &&
                slot.FinalEpoch == epoch)
            {
                continued = slotId;
                continuedSlot = slot;
            }
        }

        if (continuedSlot is not null)
        {
            continuedSlot.FinalEpoch = null;
            return continued;
        }

        long started = ++_nextSlotId;
        _slots.Add(started, new SlotState());
        _peakSlots = Math.Max(_peakSlots, _slots.Count);
        return started;
    }

    // An owned slot's entry may yet cover its final epoch, so it is kept. A finished slot's entry decides something only
    // if it covers one of the slot's kept accesses; an ended slot's decides nothing.
    private bool IsDeciding(long slotId, long epoch) =>
        _slots.TryGetValue(slotId, out SlotState? slot) && (slot.IsOwned || epoch >= slot.Oldest!.Epoch);

    /// <summary>Copies the entries of <paramref name="source"/> which decide something, into <paramref name="target"/> if given.</summary>
    private Clock CopyDeciding(Clock source, Clock? target = null)
    {
        if (target is null)
        {
            target = new Clock(_slotChanges);
        }
        else
        {
            target.Epochs.Clear();
            target.PrunedAt = _slotChanges;
        }

        foreach ((long slotId, long epoch) in source.Epochs)
        {
            if (IsDeciding(slotId, epoch))
            {
                target.Epochs.Add(slotId, epoch);
            }
        }

        return target;
    }

    /// <summary>
    /// Merges the entries of <paramref name="source"/> which decide something into <paramref name="target"/>, and prunes
    /// <paramref name="target"/>'s entries which decide nothing once more slots have changed since its last prune than
    /// half its size, which bounds both the entries kept and the cost of pruning per merge.
    /// </summary>
    private void MergeDeciding(Clock target, Clock source)
    {
        foreach ((long slotId, long epoch) in source.Epochs)
        {
            ref long current = ref CollectionsMarshal.GetValueRefOrNullRef(target.Epochs, slotId);
            if (Unsafe.IsNullRef(ref current))
            {
                if (IsDeciding(slotId, epoch))
                {
                    target.Epochs.Add(slotId, epoch);
                }
            }
            else if (current < epoch && IsDeciding(slotId, epoch))
            {
                current = epoch;
            }
        }

        if (_slotChanges - target.PrunedAt > target.Epochs.Count / 2)
        {
            // Removing the current key while enumerating a Dictionary is supported on .NET Core 3.0 and later.
            foreach ((long slotId, long epoch) in target.Epochs)
            {
                if (!IsDeciding(slotId, epoch))
                {
                    target.Epochs.Remove(slotId);
                }
            }

            target.PrunedAt = _slotChanges;
        }

        _largestClock = Math.Max(_largestClock, target.Epochs.Count);
    }

    private LocationState GetOrAddLocation(Dictionary<RaceMemoryLocation, LocationState> locations, RaceMemoryLocation location)
    {
        ref LocationState? state = ref CollectionsMarshal.GetValueRefOrAddDefault(locations, location, out bool exists);
        if (!exists)
        {
            state = new LocationState(location);
            _locations++;
        }

        return state!;
    }

    private string CollectionMember(string member)
    {
        if (!_collectionMembers.TryGetValue(member, out string? trimmed))
        {
            int separator = member.IndexOf("::", StringComparison.Ordinal);
            trimmed = separator >= 0 ? member[..separator] : member;
            _collectionMembers.Add(member, trimmed);
        }

        return trimmed;
    }

    private IEnumerable<LocationState> AllLocations() =>
        _staticLocations.Values.Concat(_trackedObjects.SelectMany(static tracked => tracked.Locations.Values));

    private static RaceAccessRecord CreatePublicAccess(TrackedAccess access, LocationState location) =>
        new(access.OperationId, access.Kind, location.Location, access.Source, Labels(access.HeldIds));

    private static System.Collections.Immutable.ImmutableArray<string> Labels(long[] heldIds) =>
        [.. heldIds.Select(static id => $"sync#{id}")];

    private OperationState StateOf(SimulationOperation operation) =>
        _operations.TryGetValue(operation.Id.Value, out OperationState? state)
            ? state
            : throw new SimulationSchedulerException($"Race state for {operation.Id} was not registered.");

    private LockState LockOf(object synchronization)
    {
        if (!_locks.TryGetValue(synchronization, out LockState? state))
        {
            state = new LockState(++_nextLockId);
            _locks.Add(synchronization, state);
        }

        return state;
    }

    /// <summary>A vector clock: the latest epoch it covers in each clock slot.</summary>
    internal sealed class Clock(long prunedAt)
    {
        public Dictionary<long, long> Epochs { get; } = [];

        // The slot-change count when the clock last dropped its entries which decide nothing.
        public long PrunedAt = prunedAt;
    }

    /// <summary>The tracked accesses of one logical location.</summary>
    internal sealed class LocationState(RaceMemoryLocation location)
    {
        private string? _description;

        public RaceMemoryLocation Location { get; } = location;

        /// <summary>Gets the location's description, as race scheduling points record it.</summary>
        public string Description => _description ??= Location.ToString();

        public TrackedAccess? LastWrite;

        // The mutually unordered reads since the last write, in no particular order.
        public TrackedAccess[] Readers = [];

        public int ReaderCount;

        // A read record the location has released, which no kept access or report refers to, for its next read.
        public TrackedAccess? SpareRead;
    }

    /// <summary>One kept access. A location reuses the record of an access it drops for the access which replaces it.</summary>
    internal sealed class TrackedAccess(LocationState location)
    {
        public LocationState Location { get; } = location;

        public SimulationOperationId OperationId;
        public long SlotId;
        public long Epoch;

        // The order the access ran in, among all accesses.
        public long Sequence;
        public RaceAccessKind Kind;
        public RaceSourceLocation Source;
        public long[] HeldIds = [];

        // The slot's adjacent kept accesses, in epoch order.
        public TrackedAccess? Older;
        public TrackedAccess? Newer;
    }

    /// <summary>The earlier access a recorded access races with.</summary>
    internal readonly record struct RacingAccess(
        SimulationOperationId OperationId,
        RaceAccessKind Kind,
        RaceSourceLocation Source);

    private sealed class SlotState
    {
        // The epoch of the slot's last event once its owner has ended, or null while an operation owns it.
        public long? FinalEpoch;

        // The slot's kept accesses, oldest first.
        public TrackedAccess? Oldest;
        public TrackedAccess? Newest;

        public bool IsOwned => FinalEpoch is null;
    }

    private sealed class OperationState(Clock clock)
    {
        public Clock Clock { get; } = clock;

        // The operation's clock slot, or zero before its first event.
        public long SlotId;

        // The locks the operation holds, with the times it has entered each without exiting.
        public List<(LockState Lock, int Recursion)> Held { get; } = [];

        // The held locks' ids in ascending order, shared by the accesses recorded while they are held.
        public long[] HeldIds = [];

        public void RefreshHeldIds()
        {
            if (Held.Count == 0)
            {
                HeldIds = [];
                return;
            }

            var ids = new long[Held.Count];
            for (int index = 0; index < ids.Length; index++)
            {
                ids[index] = Held[index].Lock.Id;
            }

            Array.Sort(ids);
            HeldIds = ids;
        }

        public int IndexOfHeld(LockState lockState)
        {
            for (int index = 0; index < Held.Count; index++)
            {
                if (Held[index].Lock == lockState)
                {
                    return index;
                }
            }

            return -1;
        }
    }

    private sealed class LockState(long id)
    {
        public long Id { get; } = id;

        // The operations which have entered the lock without exiting it, and the times its flow has entered it without
        // exiting; the lock is free while that is zero.
        public List<OperationState> Holders { get; } = [];
        public int Recursion;

        // The clock the last release published, and the event that published it while one did; zero otherwise.
        public Clock? Release;
        public long ReleaseSlotId;
        public long ReleaseEpoch;

        // The clock exits published while a running operation still held the lock, likewise, held back until none does;
        // the clock object is kept for reuse while nothing is pending.
        public Clock? Pending;
        public long PendingSlotId;
        public long PendingEpoch;
        public bool HasPending;
    }

    private sealed class TrackedObject(long id, object target)
    {
        public long Id { get; } = id;

        public WeakReference<object> Target { get; } = new(target);

        public Dictionary<RaceMemoryLocation, LocationState> Locations { get; } = [];
    }
}

/// <summary>The size of a <see cref="RaceTracker"/>'s retained state.</summary>
/// <param name="Operations">The operations whose state is retained: those which have not ended.</param>
/// <param name="Slots">The live clock slots: those owned by an operation or naming an access a location keeps.</param>
/// <param name="PeakSlots">The most clock slots live at once.</param>
/// <param name="SlotsStarted">The clock slots started: operations which continued no finished slot.</param>
/// <param name="LargestClock">The most components any clock has held.</param>
/// <param name="Locations">The tracked locations: every static field and the locations of live objects.</param>
/// <param name="RetainedAccesses">The accesses locations keep: their last writes and unordered reads.</param>
/// <param name="Sweeps">The sweeps which released the locations of collected objects.</param>
internal readonly record struct RaceTrackerStatistics(
    int Operations,
    int Slots,
    int PeakSlots,
    long SlotsStarted,
    int LargestClock,
    int Locations,
    int RetainedAccesses,
    int Sweeps);

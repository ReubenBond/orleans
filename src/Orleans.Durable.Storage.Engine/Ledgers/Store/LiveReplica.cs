using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Threading;
using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// The path a ledger's recovery took.
/// </summary>
internal enum LedgerStoreReplayPath
{
    /// <summary>The ledger was created by this store instance.</summary>
    Created,

    /// <summary>The ledger loaded a checkpoint and replayed the WAL after its replay start.</summary>
    Checkpoint,

    /// <summary>No checkpoint was usable, so the ledger replayed the WAL from the start of its first file.</summary>
    FullScan,
}

/// <summary>
/// What a ledger's recovery did: its path, the checkpoint generation it loaded, and the records it replayed.
/// </summary>
internal sealed record LedgerStoreReplay(LedgerStoreReplayPath Path, ulong Generation, long ReplayedRecords);

/// <summary>
/// One live ledger of a <see cref="LedgerStore"/>: its slot table, its slot-index checkpoints (LS-I8, LS-I9), its
/// seal and retained contiguous tail (LS-I11, LR-I6), and the slot rules over its slots (LS-I7; ledgerstore.md §8.1,
/// §8.3).
/// </summary>
/// <remarks>
/// <para>
/// A slot's in-flight claim on this replica is held from its condition check until its record's durable outcome is
/// published, so log order is each slot's mutation order and recovery applies records in log order with the last
/// record for a slot winning. The same per-slot serialization means no two records for one slot start on one page, so
/// the slot table names each slot's latest record by its page, while distinct replicas and distinct slots proceed
/// concurrently.
/// </para>
/// <para>
/// The store's maintenance freezes leaves no append can still reach and writes a checkpoint of every leaf changed since
/// the previous one. Recovery loads the newest usable checkpoint's interior nodes, replays the ledger's records after
/// its replay start, and leaves every other leaf unloaded until an operation needs one of its slots. A leaf whose
/// checkpoint object is unusable is recomputed from the ledger's WAL files and written again by the next checkpoint.
/// </para>
/// <para>
/// A damaged WAL range found while replaying cannot say which slots it held, so every slot whose latest record
/// precedes it, and every absent slot, is faulty until a later record re-establishes it. A damaged page found by a
/// read faults only the slot read. Faulty slots report <see cref="LedgerReplicaCorruptException"/> and are never reported
/// as missing; a re-established slot reads normally.
/// </para>
/// <para>
/// The seal is one seal record in the WAL (LS-I11). A writer write checks the seal and registers in one step under the
/// table lock, and the seal marks the replica in one step under the same lock, then waits for the registered writer
/// writes to publish their outcomes before it submits its record. Every writer write which checked the seal before the
/// mark is therefore durable before the seal answers, and every later one answers <see cref="ReplicaWriteOutcome.Sealed"/> once
/// the seal record is durable (LR-I8, conventions.md §13). Recovery writes, reads, and contiguous-tail reads do not
/// take part (ledgerstore.md §6.5).
/// </para>
/// <para>
/// The replica retains the greatest contiguous tail any operation carried, in memory: from 0 when created, and from
/// <c>max(0, h + 1 − K)</c> after the store opens, for its highest slot <c>h</c> and the window <c>K</c> its create
/// record holds. An operation of another window is refused before it has any effect (LR-I6).
/// </para>
/// <para>
/// Every operation is admitted before it runs; a drop closes the ledger's admission gate, which stops admitting and
/// waits for the admitted operations (conventions.md §11), so none runs after the drop. Every later one waits for the
/// drop's outcome and answers from it (conventions.md §13): <c>Dropped</c> once the drop record is durable and
/// published (LS-I2), or the drop's failure.
/// </para>
/// <para>
/// A seal or a drop which ends without its record, and a durable record whose publication throws, leave the replica
/// apart from the WAL: each stops the store, reporting the defect, before any operation waiting on it answers. An
/// operation which meets a failure a storage seam or the WAL reported stops the store before it leaves its admission.
/// The stopped store answers nothing (conventions.md §14).
/// </para>
/// </remarks>
internal sealed class LiveReplica
{
    /// <summary>
    /// The number of slots below the tail whose leaves stay mutable and load with recovery (at most five 256-slot
    /// leaves): four leaves keep appends and recovery reads near the tail in mutable memory without repeatedly
    /// freezing and thawing leaves (ledgerstore.md §8.7, §8.9, §9).
    /// </summary>
    internal const int TailSlotSpan = 1024;

    private readonly LedgerStore _store;
    private readonly LedgerStoreCheckpointOptions _checkpointOptions;
    [SuppressMessage("Usage", "CA2213", Justification = "Closing closes the gate rather than disposing it, so every waiter acquires it in turn, observes the close, and releases it.")]
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private readonly AdmissionGate _admission = new();
    private readonly Lock _lock = new();
    private readonly SlotTable _table = new(TailSlotSpan);

    // LS-I7: slots with a mutation in flight on this replica, guarded by _lock. A null value means no waiter;
    // a contending mutation on the same slot allocates a TaskCompletionSource and awaits it outside _lock.
    private readonly Dictionary<long, TaskCompletionSource?> _inFlightSlots = [];
    private readonly Dictionary<long, Task> _loads = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<DropResolution> _drop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ulong _recoveryGeneration;

    // LS-I11: the replica's seal, null while unsealed; set once, under the table lock.
    private SealState? _seal;

    // The writer writes which checked the seal and have not yet published their outcome; a marked seal waits for them.
    private int _writersInFlight;
    private TaskCompletionSource? _writersDrained;

    // LR-I6: the greatest contiguous tail any operation carried; guarded by the table lock.
    private long _retained;

    // LR-I10: RAM-only lease expiry in monotonic timestamp ticks relative to the store's boot timestamp; guarded by _lock.
    private long _grantedLeaseUntilTicks;

    // A replica's checkpoint directory comes with its first checkpoint (ledgerstore.md §8.8), so a replica created by
    // this store instance holds none until then. Set once, under the checkpoint gate.
    private WalDirectory? _directory;
    private SlotCheckpoints? _checkpoints;
    private WalPage? _corruptionEnd;

    // Whether an operation answered LedgerReplicaCorruptException since the store opened (LR-I2), guarded by the table
    // lock; in memory only, since the damage it found persists, and an operation which meets it again records it again.
    private bool _corruptionFound;

    private WalPosition _replayStart;
    private WalPosition _lastPublished;
    private bool _replayWakeSent;
    private bool _checkpointRequested;

    // The failure which records that the replica's checkpoint generations are exhausted, recorded once (§8.8).
    private InvalidDataException? _exhaustion;
    private long _checkpointsWritten;
    private long _rebuiltLeaves;
    private long _leavesDeferred;
    private bool _closed;
    private bool _released;
    private LedgerStoreReplayPath _recoveryPath;
    private long _replayed;

    internal LiveReplica(LedgerStore store, CatalogReplica entry, WalDirectory? directory, SlotCheckpoints? checkpoints, LoadedCheckpoint? loaded)
    {
        _store = store;
        Entry = entry;
        _directory = directory;
        _checkpoints = checkpoints;
        _checkpointOptions = store.CheckpointOptions;
        _replayStart = Start;
        if (loaded is not null)
        {
            _table.Load(loaded.Leaves);
            _corruptionEnd = loaded.CorruptionEnd;
            _replayStart = Max(loaded.ReplayStart, Start);
            if (loaded.Sealed)
            {
                _seal = SealState.FromCheckpoint(loaded);
            }
        }

        // A root which could not be used is recomputed at once rather than at the next interval.
        _checkpointRequested = checkpoints?.FoundDamage ?? false;
        if (checkpoints?.Exhausted ?? false)
        {
            RecordExhaustion();
        }

        _recoveryPath = loaded is null ? LedgerStoreReplayPath.FullScan : LedgerStoreReplayPath.Checkpoint;
        _recoveryGeneration = loaded?.Generation ?? 0;
    }

    private enum State
    {
        Missing,
        Data,
        Faulty,
    }

    internal CatalogReplica Entry { get; }

    internal LedgerId Ledger => Entry.Ledger;

    internal uint Handle => Entry.Handle;

    /// <summary>
    /// Gets the WAL position before which the ledger has no record: the start of its first file.
    /// </summary>
    internal WalPosition Start => new(Entry.FirstFile, 0);

    internal LedgerStoreReplay Recovery => new(_recoveryPath, _recoveryGeneration, Interlocked.Read(ref _replayed));

    /// <summary>
    /// Gets a value indicating whether the replica's seal record is durable (LS-I11).
    /// </summary>
    internal bool IsSealed
    {
        get
        {
            lock (_lock)
            {
                return _seal is { Durable.IsCompletedSuccessfully: true };
            }
        }
    }

    /// <summary>
    /// Gets the replica's retained contiguous tail (LR-I6).
    /// </summary>
    internal Slot RetainedContiguousTail
    {
        get
        {
            lock (_lock)
            {
                return new Slot(_retained);
            }
        }
    }

    /// <summary>
    /// Reports the published state of the slots below <paramref name="count"/>, those which hold DATA and those which are
    /// faulty, without reading their records: what the replica's recovery established (LS-I5). Deterministic simulation
    /// logs it when a node restarts, so trace validation checks that the restart kept every answer
    /// (test/Durable.Storage/docs/spec/verification.md §5.1). It reads each unloaded leaf into a scratch copy rather than loading it, so the
    /// slot table, its resident leaves, and the replica's counters stay as a run which never asks would leave them.
    /// </summary>
    internal async ValueTask<(IReadOnlyList<long> Data, IReadOnlyList<long> Faulty)> SlotStatesAsync(long count, CancellationToken cancellationToken)
    {
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            // A dropped replica holds no slot (LS-I2).
            return ([], []);
        }

        try
        {
            var data = new List<long>();
            var faulty = new List<long>();
            for (var first = 0L; first < count; first += SlotLeafCodec.Slots)
            {
                var number = SlotTable.LeafOf(first);
                var scratch = await ReadUnloadedLeafAsync(number, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    // A leaf an operation loaded meanwhile may have changed since its checkpoint, so it answers from the
                    // table.
                    var entries = _table.IsLoaded(number) ? null : scratch;
                    for (var slot = first; slot < Math.Min(count, first + SlotLeafCodec.Slots); slot++)
                    {
                        switch (StateLocked(entries?[slot - first] ?? _table.Get(slot)))
                        {
                            case State.Data:
                                data.Add(slot);
                                break;
                            case State.Faulty:
                                faulty.Add(slot);
                                break;
                            case State.Missing:
                                break;
                            default:
                                throw new InvalidOperationException("A slot has no state.");
                        }
                    }
                }
            }

            return (data, faulty);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Returns the WAL page which holds <paramref name="slot"/>'s latest record, or <see langword="null"/> when the slot
    /// holds none, without reading the record. Deterministic simulation damages that page so a replica answers the slot
    /// corrupt by construction (test/Durable.Storage/docs/spec/verification.md §5.1).
    /// </summary>
    internal async ValueTask<WalPage?> RecordPageAsync(long slot, CancellationToken cancellationToken)
    {
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            // A dropped replica holds no slot (LS-I2).
            return null;
        }

        try
        {
            await EnsureLoadedAsync(SlotTable.LeafOf(slot), cancellationToken).ConfigureAwait(false);
            _ = Observe(slot, out var entry);
            return entry.HasRecord ? entry.Page : null;
        }
        finally
        {
            Exit();
        }
    }

    internal long CheckpointsWritten => Interlocked.Read(ref _checkpointsWritten);

    /// <summary>
    /// Gets the failure which records that the replica's checkpoint generations are exhausted, recorded once, or
    /// <see langword="null"/> while they are not (ledgerstore.md §8.8).
    /// </summary>
    internal InvalidDataException? Exhaustion => Volatile.Read(ref _exhaustion);

    /// <summary>
    /// Gets a value indicating whether the replica's checkpoint generations are exhausted (ledgerstore.md §8.8): it
    /// records <see cref="Exhaustion"/>, maintenance schedules no checkpoint, and <see cref="CheckpointAsync"/> fails,
    /// until an operator deletes the replica's checkpoints.
    /// </summary>
    internal bool CheckpointsExhausted => Volatile.Read(ref _checkpoints)?.Exhausted ?? false;

    internal long RebuiltLeaves => Interlocked.Read(ref _rebuiltLeaves);

    internal long LeavesDeferred => Interlocked.Read(ref _leavesDeferred);

    internal (int Loaded, int Unloaded) LeafCounts
    {
        get
        {
            lock (_lock)
            {
                return (_table.LoadedLeafCount, _table.UnloadedLeafCount);
            }
        }
    }

    internal ulong CheckpointGeneration => Volatile.Read(ref _checkpoints)?.Generation ?? 0;

    internal int CheckpointFileCount => Volatile.Read(ref _checkpoints)?.FileCount ?? 0;

    /// <summary>
    /// Gets a value indicating whether the replica holds a checkpoint directory, which its first checkpoint creates.
    /// </summary>
    internal bool HasCheckpointDirectory => Volatile.Read(ref _checkpoints) is not null;

    /// <summary>
    /// Gets the WAL position recovery replays this ledger's records from: its checkpoint's cut, or its start.
    /// </summary>
    internal WalPosition ReplayStart
    {
        get
        {
            lock (_lock)
            {
                return _replayStart;
            }
        }
    }

    /// <summary>
    /// Gets the position of the latest record published to the ledger. A mutation is acknowledged when its record is
    /// published, just before the WAL's durable frontier passes it, so a checkpoint reflects every acknowledged
    /// mutation once the frontier is above this position.
    /// </summary>
    internal WalPosition LastPublished
    {
        get
        {
            lock (_lock)
            {
                return _lastPublished;
            }
        }
    }

    /// <summary>
    /// Gets or sets the timestamp from which maintenance measures the ledger's checkpoint interval, or
    /// <see langword="null"/> before maintenance first considers the ledger; maintenance alone uses it.
    /// </summary>
    internal long? LastCheckpointTimestamp { get; set; }

    /// <summary>
    /// Applies a writer write or a recovery write to a slot (ledgerstore.md §8.1): a writer write on a sealed replica
    /// answers <see cref="ReplicaWriteOutcome.Sealed"/> once the seal is durable; otherwise a slot which holds DATA
    /// answers <see cref="ReplicaWriteOutcome.Written"/> without a record, a writer write on a faulty slot fails as
    /// corrupt, and every other write appends one record and answers once it is durable and published. A write which
    /// meets the replica's drop answers <see cref="ReplicaWriteOutcome.Dropped"/> once the drop is published.
    /// </summary>
    internal async ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, int windowSize, CancellationToken cancellationToken)
    {
        var key = LedgerRecords.Key(Handle, slot.Value);
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReplicaWriteOutcome.Dropped.Instance;
        }

        SlotRequest? request = null;
        var accepted = false;
        var writer = false;
        try
        {
            SealState? seal = null;
            lock (_lock)
            {
                AdmitLocked(contiguousTail, windowSize);
                if (!recovery)
                {
                    // LS-I11: the seal check and the registration are one step against the seal's mark.
                    seal = _seal;
                    if (seal is null)
                    {
                        _writersInFlight++;
                        writer = true;

                        // LR-I10: extend RAM-only lease when admitting an unsealed writer write (zero WAL writes).
                        var leaseUntil = _store.ExtendLeaseTicks();
                        if (leaseUntil > _grantedLeaseUntilTicks)
                        {
                            _grantedLeaseUntilTicks = leaseUntil;
                        }
                    }
                }
            }

            if (seal is not null)
            {
                // LR-I8, conventions.md §13: Sealed only once the seal record is durable.
                var sealedProof = await seal.Durable.WaitAsync(cancellationToken).ConfigureAwait(false);
                return sealedProof.ToSealedOutcome();
            }

            await AcquireSlotAsync(slot.Value, cancellationToken).ConfigureAwait(false);
            try
            {
                _store.ThrowIfUnavailable();

                // The leaf stays loaded from here until the mutation is published.
                await EnsureLoadedAsync(SlotTable.LeafOf(slot.Value), cancellationToken).ConfigureAwait(false);
                var state = Observe(slot.Value, out _);
                if (state == State.Data)
                {
                    return writer ? _store.WrittenWithLease : ReplicaWriteOutcome.Written.Instance;
                }

                if (state == State.Faulty && !recovery)
                {
                    throw RecordCorruption(slot);
                }

                request = new SlotRequest(this, slot.Value, key, writer, payload);
                await _store.Wal.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                accepted = true;
            }
            finally
            {
                if (!accepted)
                {
                    request?.Abandon();
                    ReleaseSlot(slot.Value);
                }
            }
        }
        catch (Exception exception)
        {
            _store.StopIfReported(exception);
            throw;
        }
        finally
        {
            // An accepted mutation stays admitted, and a writer write registered, until its durable outcome is published.
            if (!accepted)
            {
                if (writer)
                {
                    ExitWriter();
                }

                Exit();
            }
        }

        var published = await request.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return published.ToWrittenOutcome(writer ? _store.WrittenWithLease : ReplicaWriteOutcome.Written.Instance);
    }

    /// <summary>
    /// Reads a slot's published state: an owned batch, <see cref="ReplicaRead.Missing"/>, or, once the replica's drop is
    /// published, <see cref="ReplicaWriteOutcome.Dropped"/>.
    /// </summary>
    internal async ValueTask<ReplicaRead> ReadAsync(
        Slot slot, Slot contiguousTail, int windowSize, CancellationToken cancellationToken)
    {
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReplicaWriteOutcome.Dropped.Instance;
        }

        try
        {
            lock (_lock)
            {
                AdmitLocked(contiguousTail, windowSize);
            }

            await EnsureLoadedAsync(SlotTable.LeafOf(slot.Value), cancellationToken).ConfigureAwait(false);
            var state = Observe(slot.Value, out var entry);
            if (state == State.Missing)
            {
                return ReplicaRead.Missing.Instance;
            }

            if (state == State.Faulty)
            {
                throw RecordCorruption(slot);
            }

            var read = await _store.Wal.ReadAsync(entry.Page, LedgerRecords.Key(Handle, slot.Value), cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out ArcBuffer record))
            {
                // "any slot whose point read returns WalCorruption" is faulty.
                MarkFaulty(slot.Value, entry.Page);
                throw RecordCorruption(slot);
            }

            using (record)
            {
                if (!LedgerRecords.IsDataRecord(record))
                {
                    MarkFaulty(slot.Value, entry.Page);
                    throw RecordCorruption(slot);
                }

                return record.Slice(LedgerRecords.KeyLength);
            }
        }
        catch (Exception exception)
        {
            _store.StopIfReported(exception);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Reads up to <paramref name="maxSlots"/> consecutive published slots starting at <paramref name="from"/> below
    /// <paramref name="below"/> whose length-prefixed payloads fit within <paramref name="maxBytes"/>, stopping at the
    /// first missing, faulty, or oversized slot after <paramref name="from"/>.
    /// </summary>
    internal async ValueTask<ReplicaRangeRead> ReadRangeAsync(
        Slot from, Slot below, Slot contiguousTail, int windowSize, int maxSlots, int maxBytes, CancellationToken cancellationToken)
    {
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReplicaWriteOutcome.Dropped.Instance;
        }

        ArcBufferWriter? writer = null;
        try
        {
            lock (_lock)
            {
                AdmitLocked(contiguousTail, windowSize);
            }

            if (from == below)
            {
                return ReplicaRangeBatch.Empty(from);
            }

            var end = Math.Min(below.Value, from.Value + maxSlots);
            var loadedLeaf = -1L;
            var count = 0;
            for (var slot = from.Value; slot < end; slot++)
            {
                var leaf = SlotTable.LeafOf(slot);
                if (leaf != loadedLeaf)
                {
                    await EnsureLoadedAsync(leaf, cancellationToken).ConfigureAwait(false);
                    loadedLeaf = leaf;
                }

                var state = Observe(slot, out var entry);
                if (state == State.Missing)
                {
                    break;
                }

                if (state == State.Faulty)
                {
                    if (count == 0)
                    {
                        throw RecordCorruption(new Slot(slot));
                    }

                    break;
                }

                var read = await _store.Wal.ReadAsync(entry.Page, LedgerRecords.Key(Handle, slot), cancellationToken).ConfigureAwait(false);
                if (!read.TryGetValue(out ArcBuffer record))
                {
                    MarkFaulty(slot, entry.Page);
                    if (count == 0)
                    {
                        throw RecordCorruption(new Slot(slot));
                    }

                    break;
                }

                using (record)
                {
                    if (!LedgerRecords.IsDataRecord(record))
                    {
                        MarkFaulty(slot, entry.Page);
                        if (count == 0)
                        {
                            throw RecordCorruption(new Slot(slot));
                        }

                        break;
                    }

                    var batchLength = record.Length - LedgerRecords.KeyLength;
                    var framedLength = checked(ReplicaRangeBatch.FrameHeaderLength + batchLength);
                    if (count == 0 && framedLength > maxBytes)
                    {
                        return ReplicaRangeBatch.TooLarge(from, batchLength);
                    }

                    if (count > 0 && writer!.Length + framedLength > maxBytes)
                    {
                        break;
                    }

                    writer ??= new ArcBufferWriter(Math.Min(maxBytes, Math.Max(framedLength, ArcBufferWriter.MinimumPageSize)));
                    using var payload = record.UnsafeSlice(LedgerRecords.KeyLength, batchLength);
                    ReplicaRangeBatch.WriteFrame(writer, payload);
                    count++;
                }
            }

            return count == 0
                ? ReplicaRangeBatch.Empty(from)
                : new ReplicaRangeBatch(from, count, writer!.ConsumeSlice(writer.Length));
        }
        catch (Exception exception)
        {
            _store.StopIfReported(exception);
            throw;
        }
        finally
        {
            writer?.Dispose();
            Exit();
        }
    }

    /// <summary>
    /// Reports the retained contiguous tail and the seal, sealing the replica first when <paramref name="seal"/> is set
    /// (ledgerstore.md §8.3). The seal is reported only once its record is durable: a read which meets a seal in flight
    /// waits for it (conventions.md §13).
    /// </summary>
    /// <remarks>
    /// A seal marks the replica, waits for the writer writes registered before the mark, then appends its record; once
    /// marked it runs to its record's outcome whatever the caller's cancellation, since later writer writes wait on it.
    /// The procedure holds the first sealer's admission, so a drop waits for it.
    /// </remarks>
    internal async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, int windowSize, CancellationToken cancellationToken)
    {
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReplicaWriteOutcome.Dropped.Instance;
        }

        var transferred = false;
        try
        {
            SealState? state;
            Task? drained = null;
            lock (_lock)
            {
                AdmitLocked(contiguousTail, windowSize);
                state = _seal;
                if (state is null && seal)
                {
                    // LS-I11: the mark is one step against the writer writes' seal checks.
                    state = _seal = new SealState();
                    if (_writersInFlight == 0)
                    {
                        drained = Task.CompletedTask;
                    }
                    else
                    {
                        _writersDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        drained = _writersDrained.Task;
                    }
                }

                if (state is null)
                {
                    return new LedgerReplicaTail(new Slot(_retained), IsSealed: false);
                }
            }

            if (drained is not null)
            {
                transferred = true;
                _ = RunSealAsync(state, drained);
            }

            var sealedProof = await state.Durable.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                var leaseRemaining = seal ? _store.ComputeLeaseRemaining(_grantedLeaseUntilTicks) : TimeSpan.Zero;
                return sealedProof.ToTail(new Slot(_retained), leaseRemaining);
            }
        }
        catch (Exception exception)
        {
            _store.StopIfReported(exception);
            throw;
        }
        finally
        {
            if (!transferred)
            {
                Exit();
            }
        }
    }

    /// <summary>
    /// Extends the replica's RAM-only lease if unsealed, or awaits durable seal publication and reports
    /// <see cref="ReplicaHeartbeatStatus.Sealed"/>; reports <see cref="ReplicaHeartbeatStatus.Dropped"/> if dropped
    /// (LR-I8, LR-I10, conventions.md §13). Writes nothing to the WAL.
    /// </summary>
    internal async ValueTask<ReplicaHeartbeatStatus> HeartbeatAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReplicaHeartbeatStatus.Dropped;
        }

        try
        {
            SealState? seal;
            lock (_lock)
            {
                seal = _seal;
                if (seal is null)
                {
                    var leaseUntil = _store.ExtendLeaseTicks();
                    if (leaseUntil > _grantedLeaseUntilTicks)
                    {
                        _grantedLeaseUntilTicks = leaseUntil;
                    }

                    return ReplicaHeartbeatStatus.Granted;
                }
            }

            // LR-I8, conventions.md §13: Sealed only once the seal record is durable.
            var sealedProof = await seal.Durable.WaitAsync(cancellationToken).ConfigureAwait(false);
            sealedProof.EnsureDurable();
            return ReplicaHeartbeatStatus.Sealed;
        }
        catch (Exception exception)
        {
            _store.StopIfReported(exception);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Writes a checkpoint of every leaf changed since the previous one, waiting for one already running.
    /// </summary>
    internal async ValueTask CheckpointAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            // A dropped replica has nothing to checkpoint (LS-I2).
            return;
        }

        try
        {
            await CheckpointOnceAsync(failWhenExhausted: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _store.StopIfReported(exception);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Admits an operation, refusing it once the store is stopped or disposed or the ledger is dropped. An operation which
    /// meets the admission a drop closed waits for the drop's outcome and answers from it (conventions.md §13):
    /// <see langword="false"/>, dropped, only once the drop record is durable and published, since a crash before then
    /// keeps the replica (LS-I2), and otherwise the drop's failure, which stopped the store, or
    /// <see cref="ObjectDisposedException"/> once the store is disposed.
    /// </summary>
    /// <returns>Whether the operation was admitted; <see langword="false"/> once the ledger's drop is published.</returns>
    internal ValueTask<bool> TryEnterAsync(CancellationToken cancellationToken)
    {
        if (!_admission.TryEnterUnscoped())
        {
            return RefuseAsync(cancellationToken);
        }

        try
        {
            _store.ThrowIfUnavailable();
        }
        catch
        {
            _admission.Exit();
            throw;
        }

        return ValueTask.FromResult(true);
    }

    internal void Exit() => _admission.Exit();

    /// <summary>
    /// Claims <paramref name="slot"/> on this replica (LS-I7), waiting asynchronously if another mutation for the same
    /// slot is already in flight. An uncontended claim allocates nothing.
    /// </summary>
    private ValueTask AcquireSlotAsync(long slot, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        Task wait;
        lock (_lock)
        {
            if (_inFlightSlots.TryAdd(slot, null))
            {
                return ValueTask.CompletedTask;
            }

            wait = GetOrCreateSlotWaiterLocked(slot);
        }

        return WaitAndAcquireSlotAsync(slot, wait, cancellationToken);
    }

    private async ValueTask WaitAndAcquireSlotAsync(long slot, Task wait, CancellationToken cancellationToken)
    {
        while (true)
        {
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_lock)
            {
                if (_inFlightSlots.TryAdd(slot, null))
                {
                    return;
                }

                wait = GetOrCreateSlotWaiterLocked(slot);
            }
        }
    }

    private Task GetOrCreateSlotWaiterLocked(long slot)
    {
        var waiter = _inFlightSlots[slot];
        if (waiter is null)
        {
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightSlots[slot] = waiter;
        }

        return waiter.Task;
    }

    private void ReleaseSlot(long slot)
    {
        TaskCompletionSource? waiter;
        lock (_lock)
        {
            var removed = _inFlightSlots.Remove(slot, out waiter);
            Debug.Assert(removed, "LS-I7: a slot claim is released once by its owner.");
        }

        _ = waiter?.TrySetResult();
    }

    /// <summary>
    /// Releases the in-flight claim for <paramref name="slot"/> and, when <paramref name="writer"/> is set, unregisters
    /// the writer write under one table lock acquisition (LS-I7, LS-I11).
    /// </summary>
    private void CompleteSlot(long slot, bool writer)
    {
        TaskCompletionSource? slotWaiter;
        TaskCompletionSource? writersDrained = null;
        lock (_lock)
        {
            var removed = _inFlightSlots.Remove(slot, out slotWaiter);
            Debug.Assert(removed, "LS-I7: a slot claim is released once by its owner.");
            if (writer)
            {
                Debug.Assert(_writersInFlight > 0, "LS-I11: a writer write unregisters once.");
                if (--_writersInFlight == 0 && _writersDrained is { } drained)
                {
                    _writersDrained = null;
                    writersDrained = drained;
                }
            }
        }

        _ = slotWaiter?.TrySetResult();
        _ = writersDrained?.TrySetResult();
    }

    /// <summary>
    /// Unregisters a writer write once its outcome is published, or once it ends without a record; the last one a marked
    /// seal waits for lets the seal append its record.
    /// </summary>
    private void ExitWriter()
    {
        lock (_lock)
        {
            Debug.Assert(_writersInFlight > 0, "LS-I11: a writer write unregisters once.");
            if (--_writersInFlight == 0 && _writersDrained is { } drained)
            {
                _writersDrained = null;
                _ = drained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Admits an operation of the replica window <paramref name="windowSize"/>, refusing one whose window differs from the
    /// one the replica's create record holds, then retains <paramref name="contiguousTail"/> (LR-I6). The caller holds
    /// the table lock.
    /// </summary>
    private void AdmitLocked(Slot contiguousTail, int windowSize)
    {
        LedgerReplicaOperations.ValidateReplicaWindow(Entry.Window, windowSize);
        _retained = Math.Max(_retained, contiguousTail.Value);
    }

    /// <summary>
    /// Appends the marked seal's record once every writer write registered before the mark has published its outcome,
    /// then releases the admission it holds (ledgerstore.md §8.3). A seal which fails completes the writer writes which
    /// met the mark, and every later seal, with its failure, since they wait on it; unless the store's disposal released
    /// the WAL first, the failure stopped the store and the process before then (conventions.md §14).
    /// </summary>
    private async Task RunSealAsync(SealState seal, Task drained)
    {
        try
        {
            await drained.ConfigureAwait(false);
            var published = await _store.WriteStoreRecordAsync(new SealRecord(Ledger, Handle), durable => PublishSeal(seal, durable)).ConfigureAwait(false);
            published.EnsureDurable();
        }
        catch (ObjectDisposedException exception) when (_store.IsDisposed)
        {
            // Disposal waits for no seal, so it may release the WAL before the record reaches it: nothing was written,
            // and the writer writes which met the mark fail with the disposal.
            seal.Fail(exception);
        }
#pragma warning disable CA1031 // A seal left without an outcome would leave writer writes waiting, so its failure completes them.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // A seal which ends without its record leaves the replica marked sealed apart from its WAL, so the store stops,
            // reporting the failure as a defect unless a seam or the WAL reported it, before the writer writes which met
            // the mark answer (ledgerstore.md §6.4).
            _store.Stop("LedgerStoreSeal", exception);
            seal.Fail(exception);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Publishes the durable seal record: the replica answers sealed from here (LS-I11, conventions.md §13).
    /// </summary>
    private DurableSeal PublishSeal(SealState seal, WalDurableRecord record)
    {
        record.EnsureDurable();
        lock (_lock)
        {
            _lastPublished = Max(_lastPublished, record.Position);
            return seal.Publish(record);
        }
    }

    /// <summary>
    /// Completes the drop which closed admission once its drop record is durable and published, allowing waiters to
    /// answer <see cref="ReplicaWriteOutcome.Dropped"/> (LS-I2, conventions.md §13).
    /// </summary>
    internal void CompleteDrop(LedgerStore.DurableDrop dropped)
    {
        dropped.EnsureDurable();
        _ = _drop.TrySetResult(DropResolution.FromDropped(dropped));
    }

    /// <summary>
    /// Completes the drop which closed admission with its failure when the drop ended without a durable drop record, so
    /// waiters answer the failure rather than <see cref="ReplicaWriteOutcome.Dropped"/> (LS-I2, conventions.md §13).
    /// </summary>
    internal void FailDrop(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        _ = _drop.TrySetResult(DropResolution.FromFailure(failure));
    }

    private async ValueTask<bool> RefuseAsync(CancellationToken cancellationToken)
    {
        var resolution = await _drop.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        resolution.ThrowIfFailedOrInvalid();
        _store.ThrowIfUnavailable();
        Debug.Assert(_store.Catalog.IsDropped(Ledger), "Conventions §13, LS-I2: a replica answers dropped only once the catalog holds its tombstone.");
        return false;
    }

    /// <summary>
    /// Stops admitting operations and waits for the admitted ones and for leaf loads, so nothing touches the ledger's
    /// slots or WAL records afterwards; it does no file I/O. Repeating it is harmless.
    /// </summary>
    internal async ValueTask StopAsync()
    {
        await _admission.CloseAsync().ConfigureAwait(false);
        await StopLoadsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a running checkpoint, closes the checkpoints, and releases the checkpoint files and their directory; the
    /// ledger then serves nothing.
    /// </summary>
    internal async ValueTask RetireAsync()
    {
        _ = await CloseCheckpointsAsync().ConfigureAwait(false);
        await ReleaseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the checkpoint files and their directory; the ledger then serves nothing.
    /// </summary>
    internal async ValueTask ReleaseAsync()
    {
        lock (_lock)
        {
            if (_released)
            {
                return;
            }

            _released = true;
        }

        try
        {
            if (_checkpoints is { } checkpoints)
            {
                await checkpoints.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (_directory is { } directory)
            {
                await directory.DisposeAsync().ConfigureAwait(false);
            }

            _stopping.Dispose();
        }
    }

    /// <summary>
    /// Stops starting loads and waits for those running, which observe the cancellation; their outcome no longer
    /// matters.
    /// </summary>
    internal async ValueTask StopLoadsAsync()
    {
        lock (_lock)
        {
            if (_released)
            {
                return;
            }
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        Task[] loads;
        lock (_lock)
        {
            loads = [.. _loads.Values];
        }

        await Task.WhenAll(loads).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Waits for a running checkpoint and closes the gate, writing a final checkpoint first when
    /// <paramref name="final"/> is set, so recovery replays an empty suffix for this replica.
    /// </summary>
    /// <returns>Whether it wrote a final checkpoint.</returns>
    internal async ValueTask<bool> CloseCheckpointsAsync(bool final = false)
    {
        await _checkpointGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (final && !_closed && !CheckpointsExhausted)
            {
                await WriteCheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        finally
        {
            _closed = true;
            _ = _checkpointGate.Release();
        }
    }

    /// <summary>
    /// Replays one recovered slot record of this ledger: loads its leaf, then publishes it.
    /// </summary>
    internal async ValueTask ReplayAsync(long slot, WalRecord record, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(SlotTable.LeafOf(slot), cancellationToken).ConfigureAwait(false);
        if (PublishCore(slot, record.Page, record.Position))
        {
            lock (_lock)
            {
                _table.FreezeSettled();
            }
        }

        _ = Interlocked.Increment(ref _replayed);
    }

    /// <summary>
    /// Replays the replica's recovered seal record, at or above its replay start (LS-I11).
    /// </summary>
    internal void ReplaySeal(WalRecord record)
    {
        lock (_lock)
        {
            // One seal record per replica: a sealed replica appends no other.
            _seal ??= SealState.FromWalRecord(record);
            _lastPublished = Max(_lastPublished, record.Position);
        }

        _store.RecordPublished(record.Page);
        _ = Interlocked.Increment(ref _replayed);
    }

    /// <summary>
    /// Completes recovery: freezes settled leaves, loads the leaves near the tail, and rebuilds the retained contiguous
    /// tail from the highest slot and the window the replica's create record holds (LR-I6).
    /// </summary>
    internal async ValueTask FinishRecoveryAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _table.FreezeSettled();
        }

        await LoadTailAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            // LR-I6: a slot h holds DATA only once a writer knew a contiguous tail of at least h + 1 − K.
            _retained = Math.Max(0, _table.Highest + 1 - Entry.Window);
        }
    }

    internal void MarkCreated() => _recoveryPath = LedgerStoreReplayPath.Created;

    /// <summary>
    /// Gets whether the replica found damage (LS-I5, LR-I2): its recovery met a damaged WAL range which faults its slots,
    /// its loaded slot table holds a fault a read found, which a checkpoint keeps across restarts, or an operation
    /// answered <see cref="LedgerReplicaCorruptException"/> since the store opened.
    /// </summary>
    internal bool IsCorrupt
    {
        get
        {
            lock (_lock)
            {
                return _corruptionEnd is not null || _table.FaultyCount > 0 || _corruptionFound;
            }
        }
    }

    /// <summary>
    /// Records that a damaged WAL range ends at <paramref name="page"/>: every slot whose latest record precedes it,
    /// and every absent slot, is faulty.
    /// </summary>
    internal void RaiseCorruptionEnd(WalPage page)
    {
        lock (_lock)
        {
            if (_corruptionEnd is not { } end || page > end)
            {
                _corruptionEnd = page;
            }
        }
    }

    /// <summary>
    /// Returns whether a checkpoint is due before the interval: the replay distance reached its bound, or a damaged
    /// checkpoint is being recomputed. None is due once the generations are exhausted.
    /// </summary>
    internal bool CheckpointDue()
    {
        if (CheckpointsExhausted)
        {
            return false;
        }

        lock (_lock)
        {
            return _checkpointRequested || ReplayDistanceLocked() >= _checkpointOptions.CheckpointReplayBytes;
        }
    }

    /// <summary>
    /// Writes the checkpoint maintenance found due, unless the generations are exhausted or the replica is closed. A
    /// failure propagates and ends maintenance: a failed local storage operation stops the process (conventions.md §14).
    /// A drop which stopped the replica's leaf loads while the checkpoint ran ends it, writing nothing, as a closed
    /// replica writes no checkpoint.
    /// </summary>
    internal async ValueTask MaintainCheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckpointOnceAsync(failWhenExhausted: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException && _stopping.IsCancellationRequested)
        {
            // A lifecycle race with the replica's drop, not a failure: the drop retires the replica's checkpoints.
        }
    }

    /// <summary>
    /// Encodes each mutable leaf no append can still reach, installing its encoding only if the leaf has not changed.
    /// </summary>
    internal void FreezeSettledLeaves()
    {
        List<long> candidates;
        lock (_lock)
        {
            candidates = _table.FreezeCandidates();
        }

        foreach (var number in candidates)
        {
            SlotEntry[] entries;
            long version;
            lock (_lock)
            {
                if (!_table.TryCopyMutable(number, out entries, out version))
                {
                    continue;
                }
            }

            var frozen = FrozenLeaf.Encode(entries);
            lock (_lock)
            {
                _table.InstallFrozen(number, version, frozen);
            }
        }
    }

    /// <summary>
    /// Publishes a live durable DATA record gated by its post-sync <see cref="WalDurableRecord"/> proof; this is the
    /// slot's linearization point (LS-I7, conventions.md §13).
    /// </summary>
    private DurableSlotWrite PublishDurableSlot(long slot, WalDurableRecord record)
    {
        record.EnsureDurable();
        _ = PublishCore(slot, record.Page, record.Position);
        return DurableSlotWrite.FromWal(record);
    }

    /// <summary>
    /// Publishes a durable DATA record into the slot table. Live writes enter through <see cref="PublishDurableSlot"/>
    /// with a <see cref="WalDurableRecord"/>; recovery enters through <see cref="ReplayAsync"/> from the finalized WAL.
    /// </summary>
    /// <returns><see langword="true"/> when the table's settled boundary advanced.</returns>
    private bool PublishCore(long slot, WalPage page, WalPosition position)
    {
        lock (_lock)
        {
            var settled = _table.Set(slot, new SlotEntry(SlotKind.Data, page), position);
            _lastPublished = Max(_lastPublished, position);
            _store.RecordPublished(page);
            var replayDue = !_replayWakeSent && ReplayDistanceLocked() >= _checkpointOptions.CheckpointReplayBytes;
            _replayWakeSent |= replayDue;
            if (settled || replayDue)
            {
                _store.Wake();
            }

            return settled;
        }
    }

    private static WalPosition Max(WalPosition left, WalPosition right) => left > right ? left : right;

    /// <summary>
    /// Records that the replica found damage, which it reports as corrupt (LR-I2), and returns the exception naming
    /// <paramref name="slot"/>.
    /// </summary>
    private LedgerReplicaCorruptException RecordCorruption(Slot slot)
    {
        lock (_lock)
        {
            _corruptionFound = true;
        }

        return new(slot, "The LedgerStore cannot establish the slot's state: a WAL range which may hold its latest record is damaged.");
    }

    /// <summary>
    /// A known damaged range faults every absent slot and every slot whose latest record precedes the range's end; a
    /// faulty entry is faulty until a later record replaces it.
    /// </summary>
    private static bool IsFaulty(SlotEntry entry, WalPage? corruptionEnd) =>
        entry.Kind == SlotKind.Faulty || corruptionEnd is { } end && (!entry.HasRecord || entry.Page < end);

    private State Observe(long slot, out SlotEntry entry)
    {
        lock (_lock)
        {
            entry = _table.Get(slot);
            return StateLocked(entry);
        }
    }

    /// <summary>
    /// Returns the published state of a slot whose entry is <paramref name="entry"/>. The caller holds the table lock.
    /// </summary>
    private State StateLocked(SlotEntry entry)
    {
        if (IsFaulty(entry, _corruptionEnd))
        {
            return State.Faulty;
        }

        return entry.Kind == SlotKind.Data ? State.Data : State.Missing;
    }

    /// <summary>
    /// Returns the WAL bytes from this ledger's replay start through the end of the latest page published to any
    /// ledger: the WAL a recovery replays for it.
    /// </summary>
    private long ReplayDistanceLocked() => _store.DistanceFrom(_replayStart);

    /// <summary>
    /// Records that a slot's latest record, on <paramref name="page"/>, cannot be read; the faulty entry is
    /// checkpointed, so the fault survives restarts until a later record re-establishes the slot.
    /// </summary>
    private void MarkFaulty(long slot, WalPage page)
    {
        lock (_lock)
        {
            var entry = _table.Get(slot);
            if (entry.Page == page && entry.Kind == SlotKind.Data)
            {
                _ = _table.Set(slot, new SlotEntry(SlotKind.Faulty, page), position: null);
            }
        }
    }

    /// <summary>
    /// Records, once, that the replica's checkpoint generations are exhausted (ledgerstore.md §8.8).
    /// </summary>
    private void RecordExhaustion() => _ = Interlocked.CompareExchange(ref _exhaustion, SlotCheckpoints.GenerationsExhausted(), null);

    /// <summary>
    /// Writes a checkpoint under the gate. Exhaustion is checked again under the gate, since a checkpoint which held it
    /// may have taken the last generation and recorded that: maintenance then skips the replica, and an explicit
    /// checkpoint fails (§8.8).
    /// </summary>
    private async ValueTask CheckpointOnceAsync(bool failWhenExhausted, CancellationToken cancellationToken)
    {
        await _checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CheckpointsExhausted)
            {
                if (failWhenExhausted)
                {
                    throw SlotCheckpoints.GenerationsExhausted();
                }

                return;
            }

            // A closed ledger was dropped or its store disposed; neither writes another checkpoint.
            if (!_closed)
            {
                await WriteCheckpointAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _checkpointGate.Release();
        }
    }

    /// <summary>
    /// Writes one checkpoint generation (LS-I8, LS-I9; ledgerstore.md §8.8); the caller holds the checkpoint gate.
    /// </summary>
    private async ValueTask WriteCheckpointAsync(CancellationToken cancellationToken)
    {
        // Copying the oldest file's live leaves forward lets reclamation delete it, bounding the files a root names.
        var compact = _checkpoints is { } existing && existing.FileCount > _checkpointOptions.MaxCheckpointFiles ? existing.LeavesToCompact() : [];
        SlotTable.CheckpointCapture capture;
        WalPage? corruptionEnd;
        bool sealedBelowCut;
        bool requested;
        lock (_lock)
        {
            // Every record below the frontier is published, and none is published while the lock is held, so the
            // capture reflects exactly the records below its cut.
            capture = _table.TakeCheckpoint(_store.Wal.DurableFrontier, compact);
            corruptionEnd = _corruptionEnd;

            // LS-I11: a seal record below the cut is published, since the WAL completes a record before its frontier passes it.
            sealedBelowCut = _seal?.Precedes(capture.Cut) ?? false;
            requested = _checkpointRequested;
            _checkpointRequested = false;
            _leavesDeferred += capture.Deferred;
        }

        Dictionary<long, CheckpointRef>? written;
        SlotCheckpoints checkpoints;
        try
        {
            // A stopped store writes no checkpoint: a durable record whose publication failed stopped it before the
            // frontier passed the record, so no capture's cut passes a record the table lacks (conventions.md §14).
            _store.ThrowIfStopped();
            var leaves = await EncodeLeavesAsync(capture.Leaves, cancellationToken).ConfigureAwait(false);
            checkpoints = _checkpoints ?? await CreateCheckpointsAsync(cancellationToken).ConfigureAwait(false);
            written = await checkpoints.WriteAsync(capture.Cut, corruptionEnd, sealedBelowCut, leaves, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_lock)
            {
                _table.RestoreCheckpoint(capture);
                _checkpointRequested |= requested;
            }

            throw;
        }
        finally
        {
            lock (_lock)
            {
                _replayWakeSent = false;
            }
        }

        // The root is durable: unloaded leaves move to their copies before the files they left are deleted.
        if (written is not null)
        {
            _ = Interlocked.Increment(ref _checkpointsWritten);
            lock (_lock)
            {
                _replayStart = Max(capture.Cut, Start);
                _table.Relocate(written);
            }

            // A checkpoint which took the last generation is the replica's last until an operator intervenes.
            if (checkpoints.Exhausted)
            {
                RecordExhaustion();
            }
        }

        await checkpoints.ReclaimAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the replica's checkpoint directory, one level beneath <c>slots/</c>, whose creation flushes <c>slots/</c>
    /// alone, and starts its checkpoints there; the caller holds the checkpoint gate (ledgerstore.md §8.8 step 3).
    /// </summary>
    private async ValueTask<SlotCheckpoints> CreateCheckpointsAsync(CancellationToken cancellationToken)
    {
        var directory = await _store.CreateReplicaDirectoryAsync(Handle, cancellationToken).ConfigureAwait(false);
        var checkpoints = SlotCheckpoints.Create(directory);
        _directory = directory;
        Volatile.Write(ref _checkpoints, checkpoints);
        return checkpoints;
    }

    /// <summary>
    /// Encodes captured leaves outside the lock, copying unloaded leaves from their checkpoint objects, which are
    /// validated on the way. An unloaded leaf whose object is unusable is recomputed and left for the next checkpoint:
    /// its records all precede its checkpoint's cut, so its previous reference stays correct to recover from.
    /// </summary>
    private async ValueTask<List<CheckpointLeaf>> EncodeLeavesAsync(List<SlotTable.CapturedLeaf> captured, CancellationToken cancellationToken)
    {
        var leaves = new List<CheckpointLeaf>(captured.Count);
        foreach (var leaf in captured.OrderBy(static leaf => leaf.Number))
        {
            var bytes = leaf.Encoded;
            if (leaf.Entries is { } entries)
            {
                bytes = entries.AsSpan().ContainsAnyExcept(default(SlotEntry)) ? SlotLeafCodec.Encode(entries) : null;
            }
            else if (leaf.Reference is { } reference)
            {
                bytes = (await TryReadLeafAsync(reference, cancellationToken).ConfigureAwait(false))?.Bytes;
                if (bytes is null)
                {
                    await EnsureLoadedAsync(leaf.Number, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            leaves.Add(new CheckpointLeaf(leaf.Number, bytes));
        }

        return leaves;
    }

    /// <summary>
    /// Reads and decodes an unloaded leaf's checkpoint object, returning <see langword="null"/> when the object or the
    /// leaf it holds is unusable, or its file has been reclaimed.
    /// </summary>
    private async ValueTask<FrozenLeaf?> TryReadLeafAsync(CheckpointRef reference, CancellationToken cancellationToken)
    {
        var bytes = new byte[reference.Length];
        InvalidDataException? unreadable;
        try
        {
            // Only a loaded checkpoint leaves a leaf unloaded, so the replica holds its checkpoints.
            unreadable = await _checkpoints!.ReadObjectAsync(reference, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or ObjectDisposedException)
        {
            // LS-I3: a checkpoint object whose file is gone, or whose file a later checkpoint closed while the read ran, is
            // recomputed from the WAL. A failed read stops the process (conventions.md §14) and propagates.
            return null;
        }

        // LS-I3: a checkpoint object is derived, so one whose bytes or leaf break a rule of ledgerstore.md §7.4 is
        // recomputed from the WAL.
        if (unreadable is not null)
        {
            return null;
        }

        var decoding = FrozenLeaf.Decode(bytes);
        Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
        return decoding switch
        {
            FrozenLeaf leaf => leaf,
            InvalidDataException => null,
            null => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Ensures a leaf is resident, loading it from its checkpoint object, or recomputing it from the WAL when that
    /// object is unusable. Concurrent callers share one load, which runs to completion unless the ledger stops.
    /// </summary>
    private async ValueTask EnsureLoadedAsync(long number, CancellationToken cancellationToken)
    {
        Task load;
        lock (_lock)
        {
            if (_table.IsLoaded(number))
            {
                return;
            }

            // A stopping ledger starts no load, so StopLoadsAsync waits for every load there will be.
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            if (!_loads.TryGetValue(number, out load!))
            {
                // The load runs on the thread pool, whatever scheduler the caller runs on.
                load = Task.Run(() => LoadLeafAsync(number), CancellationToken.None);
                _loads.Add(number, load);
            }
        }

        await load.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadLeafAsync(long number)
    {
        try
        {
            var cancellationToken = _stopping.Token;
            while (true)
            {
                CheckpointRef reference;
                lock (_lock)
                {
                    if (!_table.TryGetUnloaded(number, out reference))
                    {
                        return;
                    }
                }

                if (await TryReadLeafAsync(reference, cancellationToken).ConfigureAwait(false) is { } frozen)
                {
                    lock (_lock)
                    {
                        _table.InstallLoaded(number, frozen);
                    }

                    return;
                }

                // A checkpoint which copied the leaf forward may have reclaimed the file read.
                lock (_lock)
                {
                    if (_table.TryGetUnloaded(number, out var current) && current != reference)
                    {
                        continue;
                    }
                }

                var entries = await RebuildLeafAsync(number, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _table.InstallRebuilt(number, entries);
                    _checkpointRequested = true;
                }

                _store.Wake();
                _ = Interlocked.Increment(ref _rebuiltLeaves);
                return;
            }
        }
        finally
        {
            lock (_lock)
            {
                _ = _loads.Remove(number);
            }
        }
    }

    /// <summary>
    /// Reads an unloaded leaf's entries into a scratch copy the caller owns, from its checkpoint object, or recomputed from
    /// the WAL when that object is unusable, as <see cref="LoadLeafAsync"/> would; it installs nothing, requests no
    /// checkpoint, and counts no rebuilt leaf. Returns <see langword="null"/> when the leaf is loaded.
    /// </summary>
    private async ValueTask<SlotEntry[]?> ReadUnloadedLeafAsync(long number, CancellationToken cancellationToken)
    {
        while (true)
        {
            CheckpointRef reference;
            lock (_lock)
            {
                if (!_table.TryGetUnloaded(number, out reference))
                {
                    return null;
                }
            }

            if (await TryReadLeafAsync(reference, cancellationToken).ConfigureAwait(false) is { } frozen)
            {
                var entries = new SlotEntry[SlotLeafCodec.Slots];
                frozen.DecodeAll(entries);
                return entries;
            }

            // A checkpoint which copied the leaf forward may have reclaimed the file read, and a load may have loaded it.
            lock (_lock)
            {
                if (!_table.TryGetUnloaded(number, out var current))
                {
                    return null;
                }

                if (current != reference)
                {
                    continue;
                }
            }

            return await RebuildLeafAsync(number, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Recomputes an unloaded leaf from the WAL files from the ledger's first file: each slot's latest record wins,
    /// and a slot whose latest record precedes a damaged range, or which has none after one, is faulty.
    /// </summary>
    /// <remarks>
    /// An unloaded leaf has no mutation in flight, because a mutation loads its leaf first, so the scan up to the
    /// durable frontier holds every record of the leaf. LS-I4: "No record of a ledger replica precedes its first
    /// file", and every file from the first file is retained while the ledger is live.
    /// </remarks>
    private async ValueTask<SlotEntry[]> RebuildLeafAsync(long number, CancellationToken cancellationToken)
    {
        var first = number << SlotLeafCodec.SlotBits;
        var entries = new SlotEntry[SlotLeafCodec.Slots];
        var damageSeen = new int[SlotLeafCodec.Slots];
        var damage = 0;
        await foreach (var entry in _store.Wal.ReadAllAsync(Start, cancellationToken).ConfigureAwait(false))
        {
            if (entry.TryGetValue(out WalRecord record))
            {
                switch (LedgerRecords.Classify(record.Payload, _store.Catalog.NextHandle, out var handle, out var slot))
                {
                    case RecordClass.Damaged:
                        damage++;
                        break;
                    case RecordClass.Slot when handle == Handle && slot >= first && slot - first < SlotLeafCodec.Slots:
                        entries[slot - first] = new SlotEntry(SlotKind.Data, record.Page);
                        damageSeen[slot - first] = damage;
                        break;
                    case RecordClass.Slot:
                    case RecordClass.Store:
                    default:
                        // Another replica's slot record, or a create, seal, or drop record.
                        break;
                }
            }
            else if (entry.TryGetValue(out WalCorruption _))
            {
                damage++;
            }
        }

        // A faulty slot without a record keeps no page, so it does not count towards the highest slot.
        for (var index = 0; index < entries.Length; index++)
        {
            if (damageSeen[index] < damage)
            {
                entries[index] = new SlotEntry(SlotKind.Faulty, entries[index].Page);
            }
        }

        return entries;
    }

    /// <summary>
    /// Resolves the highest slot and loads the leaves covering the tail span below the tail and the tail slot, so
    /// appends and recovery reads near the tail read no checkpoint. Every other leaf stays unloaded.
    /// </summary>
    private async ValueTask LoadTailAsync(CancellationToken cancellationToken)
    {
        await ResolveHighestAsync(cancellationToken).ConfigureAwait(false);
        long highest;
        lock (_lock)
        {
            highest = _table.Highest;
        }

        var tail = checked(highest + 1);
        for (var leaf = SlotTable.LeafOf(Math.Max(0, tail - TailSlotSpan)); leaf <= SlotTable.LeafOf(tail); leaf++)
        {
            await EnsureLoadedAsync(leaf, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads leaves downwards until the highest slot is exact.
    /// </summary>
    private async ValueTask ResolveHighestAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            long? leaf;
            lock (_lock)
            {
                leaf = _table.UnresolvedHighestLeaf;
            }

            if (leaf is not { } number)
            {
                return;
            }

            await EnsureLoadedAsync(number, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Zero-allocation proof that a slot's DATA record is durable in the WAL and published to the slot table (LS-I7,
    /// conventions.md §13).
    /// </summary>
    internal readonly struct DurableSlotWrite
    {
        private readonly bool _durable;

        private DurableSlotWrite(bool durable) => _durable = durable;

        internal bool IsValid => _durable;

        internal static DurableSlotWrite FromWal(WalDurableRecord record)
        {
            record.EnsureDurable();
            return new DurableSlotWrite(durable: true);
        }

        internal void EnsureDurable()
        {
            if (!_durable)
            {
                throw Uninitialized();
            }
        }

        internal ReplicaWriteOutcome ToWrittenOutcome(ReplicaWriteOutcome.Written written)
        {
            EnsureDurable();
            return written;
        }

        private static InvalidOperationException Uninitialized() =>
            new("A default DurableSlotWrite does not witness a durable slot write (conventions.md §13).");
    }

    /// <summary>
    /// Zero-allocation proof that the replica's seal is durable in the WAL or recovered from durable state (LS-I11,
    /// LR-I8, conventions.md §13).
    /// </summary>
    internal readonly struct DurableSeal
    {
        private readonly bool _durable;

        private DurableSeal(bool durable) => _durable = durable;

        internal bool IsValid => _durable;

        internal static DurableSeal FromWal(WalDurableRecord record)
        {
            record.EnsureDurable();
            return new DurableSeal(durable: true);
        }

        internal static DurableSeal FromWalRecord(WalRecord record)
        {
            ArgumentOutOfRangeException.ThrowIfZero(record.Position.File);
            return new DurableSeal(durable: true);
        }

        internal static DurableSeal FromCheckpoint(LoadedCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (!checkpoint.Sealed)
            {
                throw new InvalidOperationException("An unsealed checkpoint does not witness a durable seal (LS-I11).");
            }

            return new DurableSeal(durable: true);
        }

        internal void EnsureDurable()
        {
            if (!_durable)
            {
                throw Uninitialized();
            }
        }

        internal ReplicaWriteOutcome ToSealedOutcome()
        {
            EnsureDurable();
            return ReplicaWriteOutcome.Sealed.Instance;
        }

        internal LedgerReplicaTail ToTail(Slot retained, TimeSpan leaseRemaining = default)
        {
            EnsureDurable();
            return new LedgerReplicaTail(retained, IsSealed: true, LeaseRemaining: leaseRemaining);
        }

        private static InvalidOperationException Uninitialized() =>
            new("A default DurableSeal does not witness a durable replica seal (conventions.md §13, LS-I11).");
    }

    /// <summary>
    /// Zero-allocation outcome of a drop that closed admission: either a verified <see cref="LedgerStore.DurableDrop"/>
    /// proof or the failure captured without faulting a task that may have no waiter (LS-I2, conventions.md §13).
    /// </summary>
    private readonly struct DropResolution
    {
        private readonly LedgerStore.DurableDrop _dropped;
        private readonly ExceptionDispatchInfo? _failure;

        private DropResolution(LedgerStore.DurableDrop dropped, ExceptionDispatchInfo? failure)
        {
            _dropped = dropped;
            _failure = failure;
        }

        public static DropResolution FromDropped(LedgerStore.DurableDrop dropped)
        {
            dropped.EnsureDurable();
            return new DropResolution(dropped, failure: null);
        }

        public static DropResolution FromFailure(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            return new DropResolution(default, ExceptionDispatchInfo.Capture(failure));
        }

        public void ThrowIfFailedOrInvalid()
        {
            _failure?.Throw();
            _dropped.EnsureDurable();
        }
    }

    /// <summary>
    /// The replica's seal: marked when a seal starts, and durable once its record is (LS-I11).
    /// </summary>
    private sealed class SealState
    {
        private readonly TaskCompletionSource<DurableSeal> _durable = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The seal record's position once durable; null for a seal a loaded checkpoint recorded below its cut.
        private WalPosition? _position;

        /// <summary>
        /// Gets the task which completes with a <see cref="DurableSeal"/> once the seal record is durable and published,
        /// or faults with the failure which stopped it.
        /// </summary>
        public Task<DurableSeal> Durable => _durable.Task;

        /// <summary>
        /// Returns a durable seal recovered from a loaded checkpoint below its cut.
        /// </summary>
        public static SealState FromCheckpoint(LoadedCheckpoint checkpoint)
        {
            var proof = DurableSeal.FromCheckpoint(checkpoint);
            var seal = new SealState { _position = null };
            seal._durable.SetResult(proof);
            return seal;
        }

        /// <summary>
        /// Returns a durable seal recovered from a finalized WAL record at <paramref name="record"/>'s position.
        /// </summary>
        public static SealState FromWalRecord(WalRecord record)
        {
            var proof = DurableSeal.FromWalRecord(record);
            var seal = new SealState { _position = record.Position };
            seal._durable.SetResult(proof);
            return seal;
        }

        public DurableSeal Publish(WalDurableRecord record)
        {
            var proof = DurableSeal.FromWal(record);
            _position = record.Position;
            _ = _durable.TrySetResult(proof);
            return proof;
        }

        public void Fail(Exception exception) => _ = _durable.TrySetException(exception);

        /// <summary>
        /// Returns whether the seal record precedes <paramref name="cut"/>; the caller holds the table lock.
        /// </summary>
        public bool Precedes(WalPosition cut) =>
            _durable.Task.IsCompletedSuccessfully && (_position is not { } position || position < cut);
    }

    /// <summary>
    /// One DATA record, which holds the slot's in-flight claim, and stays admitted, until its durable outcome is
    /// published.
    /// </summary>
    private sealed class SlotRequest : WalWriteRequest
    {
        private readonly LiveReplica _ledger;
        private readonly long _slot;
        private readonly bool _writer;
        private readonly TaskCompletionSource<DurableSlotWrite> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlotRequest(LiveReplica ledger, long slot, ulong key, bool writer, ArcBuffer value)
        {
            _ledger = ledger;
            _slot = slot;
            _writer = writer;
            Header = LedgerRecords.RecordKey(key);
            Payload = value;
        }

        public Task<DurableSlotWrite> Completion => _completion.Task;

        public void Abandon() => Header.Dispose();

        /// <summary>
        /// Publishes the durable record, unless the store has stopped, which publishes and answers nothing more; the next
        /// incarnation recovers the record from the WAL (conventions.md §14).
        /// </summary>
        protected internal override void OnDurable(WalDurableRecord record)
        {
            var store = _ledger._store;
            try
            {
                record.EnsureDurable();
                if (store.Stopped is { } stopped)
                {
                    _ = _completion.TrySetException(stopped);
                    return;
                }

                if (store.SlotPublicationFault?.Invoke(_ledger.Ledger, _slot) is { } fault)
                {
                    throw fault;
                }

                var proof = _ledger.PublishDurableSlot(_slot, record);
                _ = _completion.TrySetResult(proof);
            }
            catch (Exception exception)
            {
                // A publication which throws leaves the table behind the WAL. The store stops, reporting it, before the
                // caller sees the failure and before the slot claim and the admission are released, so no waiting
                // operation runs against the table; the WAL then finds it reported (conventions.md §14).
                store.Stop("LedgerStoreSlotPublication", exception);
                _ = _completion.TrySetException(exception);
                throw;
            }
            finally
            {
                Complete();
            }
        }

        /// <summary>
        /// Fails the record with the failure which stopped the WAL, which the WAL reported (conventions.md §14): the store
        /// stops with it before the caller sees it and before the slot claim and the admission are released.
        /// </summary>
        protected internal override void OnFailed(Exception error)
        {
            _ledger._store.StopOnWalFailure(error);
            Complete();
            _ = _completion.TrySetException(error);
        }

        private void Complete()
        {
            Header.Dispose();
            _ledger.CompleteSlot(_slot, _writer);
            _ledger.Exit();
        }
    }
}

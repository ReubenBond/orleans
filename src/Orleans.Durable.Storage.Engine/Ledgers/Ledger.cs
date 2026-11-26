using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// One ledger as a process uses it: its storage (<see cref="ILedgerStorage"/>), the quorum over its replicas or its one
/// copy in an object store; its known contiguous tail, which every operation carries; and the sequencer lifetime of its
/// one <see cref="LedgerWriter"/>: reads, the seal check, recovery, the recorded tail, and its writer's writes
/// (ledger-protocol.md §2.1, §2.7). A process holds one per ledger it uses; the ledger's primary appends through the writer
/// it creates (<see cref="CreateWriter(TimeProvider)"/>).
/// </summary>
/// <remarks>
/// <para>
/// The known contiguous tail only rises, from the writer's evidence, its storage's answers, recovery's <c>c</c>, and the
/// ledger map's recorded <c>T</c>, never from a candidate end (LP-I11). The storage keeps none, so this is the process's
/// one copy, which it carries to the storage as its acknowledged prefix (ledger-protocol.md §2.7).
/// </para>
/// <para>
/// A ledger has one writer (LP-I1), and every recovery in its process, and every seal check there which finds the ledger
/// sealed, stops that writer's lifetime first (LP-I14), so the process appends nothing to a ledger it is sealing.
/// </para>
/// </remarks>
internal sealed class Ledger : IAsyncDisposable
{
    private const long LeaseSurrendered = long.MinValue;

    private readonly ILedgerStorage _storage;
    private readonly LedgerLocation _location;
    private readonly TimeProvider _timeProvider;
    private readonly LedgerLeaseOptions _leaseOptions;
    private readonly long _leaseEpochBase;
    private readonly long _guardBandTicks;
    private readonly Lock _tailCheckLock = new();
    private Sequencer? _writerLifetime;
    private TaskCompletionSource<LedgerTailRead>? _runningTailCheck;
    private TaskCompletionSource<LedgerTailRead>? _queuedTailCheck;

    // 1 once a recovery ran in this process, or a seal check here found the ledger sealed: a lifetime adopted afterwards
    // is stopped at once (LP-I14).
    private int _sealedHere;

    // 1 once the ledger adopted the lifetime of a writer which serves Ledgers' proposals: every proposal it answers
    // Durable completed its slot first, which raised the known contiguous tail past it (ledgers.md §2.5, LG-I8).
    private int _served;
    private long _knownContiguousTail;

    // Monotonic primary lease expiry in timestamp ticks relative to _leaseEpochBase: 0 while no lease is held, > 0 while
    // a quorum lease has been granted to this primary, and LeaseSurrendered once permanently surrendered (LP-I18, LG-I14).
    private long _localLeaseUntil;

    // Monotonic elapsed ticks relative to _leaseEpochBase of the most recent successful lease grant or renewal.
    private long _lastLeaseExtendTicks;

    // The greatest known contiguous tail a writer write or an explicit contiguous-tail write carried to the storage
    // (ledger-protocol.md §8.2): the writer writes the tail explicitly only once its completions outrun it.
    private long _carriedContiguousTail;

    // The ledger map's recorded T once observed, and -1 before: every known contiguous tail lies at or below it (LP-I11).
    private long _recordedTail = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="Ledger"/> class over <paramref name="storage"/>, which it owns and
    /// disposes, for the ledger a map lists at <paramref name="location"/>: its sends assert they reach only the storage a
    /// map lists (LG-I7). Construction performs no I/O.
    /// </summary>
    /// <param name="id">The ledger's ID.</param>
    /// <param name="storage">The ledger's storage, the one <paramref name="location"/> names.</param>
    /// <param name="location">The location the ledger's map entry records: its replicas' nodes, or the object store.</param>
    /// <param name="timeProvider">The monotonic clock for primary lease tracking, or <see cref="TimeProvider.System"/>.</param>
    /// <param name="leaseOptions">The primary lease configuration, or <see cref="LedgerLeaseOptions.Disabled"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="location"/> holds no case.</exception>
    internal Ledger(
        LedgerId id,
        ILedgerStorage storage,
        LedgerLocation location,
        TimeProvider? timeProvider = null,
        LedgerLeaseOptions? leaseOptions = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (location.Value is null)
        {
            throw new ArgumentException("A ledger has a location.", nameof(location));
        }

        Id = id;
        _storage = storage;
        _location = location;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaseOptions = (leaseOptions ?? LedgerLeaseOptions.Disabled).Clone();
        if (_leaseOptions.IsEnabled)
        {
            _leaseOptions.Validate();
            _leaseEpochBase = _timeProvider.GetTimestamp();
            _guardBandTicks = LedgerLeaseOptions.CeilingTimestampTicks(_leaseOptions.EffectiveGuardBand, _timeProvider.TimestampFrequency);
        }
    }

    /// <summary>
    /// Gets the ledger's ID.
    /// </summary>
    public LedgerId Id { get; }

    /// <summary>
    /// Gets the location the ledger's map entry records: its replicas' nodes, or the object store.
    /// </summary>
    internal LedgerLocation Location => _location;

    /// <summary>
    /// Gets the ledger's window <c>K</c>, which its writer's sequencer and its storage share.
    /// </summary>
    public int WindowSize => _storage.WindowSize;

    /// <summary>
    /// Gets the ledger's known contiguous tail: every slot below it is decided (LP-I11).
    /// </summary>
    internal Slot KnownContiguousTail => new(Interlocked.Read(ref _knownContiguousTail));

    /// <summary>
    /// Gets a value indicating whether this process holds a valid, unsurrendered primary lease on this ledger
    /// (LP-I18, LG-I14): <c>now + guardBand &lt; localLeaseUntil</c>.
    /// </summary>
    internal bool HasValidLease
    {
        get
        {
            if (!_leaseOptions.IsEnabled || Volatile.Read(ref _served) == 0 || Volatile.Read(ref _sealedHere) != 0)
            {
                return false;
            }

            var until = Volatile.Read(ref _localLeaseUntil);
            if (until <= 0)
            {
                return false;
            }

            var elapsed = Math.Max(0L, _timeProvider.GetTimestamp() - _leaseEpochBase);
            return until - elapsed > _guardBandTicks;
        }
    }

    /// <summary>
    /// Gets a value indicating whether this ledger's primary lease has been permanently surrendered.
    /// </summary>
    internal bool IsLeaseSurrendered => Volatile.Read(ref _localLeaseUntil) == LeaseSurrendered;

    /// <summary>
    /// Returns whether this primary ledger has acquired a lease and has been idle for at least
    /// <paramref name="renewalIntervalTicks"/> without a lease extension, so a batched peer heartbeat should renew it.
    /// </summary>
    internal bool NeedsHeartbeat(long renewalIntervalTicks)
    {
        if (!_leaseOptions.IsEnabled || Volatile.Read(ref _served) == 0 || Volatile.Read(ref _sealedHere) != 0)
        {
            return false;
        }

        var until = Volatile.Read(ref _localLeaseUntil);
        if (until == LeaseSurrendered)
        {
            return false;
        }

        // Only heartbeat ledgers whose replicas already exist (a write or earlier grant occurred), so brand-new empty
        // ledgers do not send Absent heartbeats before their first write (LR-I7, LR-I10).
        if (until == 0 && KnownContiguousTail.Value == 0)
        {
            return false;
        }

        var elapsed = Math.Max(0L, _timeProvider.GetTimestamp() - _leaseEpochBase);
        var last = Volatile.Read(ref _lastLeaseExtendTicks);
        return elapsed - last >= renewalIntervalTicks;
    }

    /// <summary>
    /// Permanently surrenders this ledger's primary lease (LP-I18, OR-I24): no later 0-RTT local read can succeed and no
    /// concurrent or late write or heartbeat reply can re-extend it.
    /// </summary>
    internal void SurrenderLease() => _ = Interlocked.Exchange(ref _localLeaseUntil, LeaseSurrendered);

    /// <summary>
    /// Extends this ledger's primary lease from a quorum grant of <paramref name="grant"/> whose request began at
    /// <paramref name="sendTimestamp"/>, scaling down by <see cref="LedgerLeaseOptions.ClockDriftFactor"/> (LP-I18).
    /// Has no effect once the lease is surrendered, the ledger is sealed, or this process does not serve its writer.
    /// </summary>
    internal void TryExtendLease(long sendTimestamp, TimeSpan grant)
    {
        if (!_leaseOptions.IsEnabled || grant <= TimeSpan.Zero || Volatile.Read(ref _served) == 0 || Volatile.Read(ref _sealedHere) != 0)
        {
            return;
        }

        var scaledTicks = LedgerLeaseOptions.FloorScaledTimestampTicks(grant, _leaseOptions.ClockDriftFactor, _timeProvider.TimestampFrequency);
        if (scaledTicks <= 0)
        {
            return;
        }

        var sendElapsed = Math.Max(0L, sendTimestamp - _leaseEpochBase);
        var sum = (UInt128)sendElapsed + (UInt128)scaledTicks;
        var candidate = sum > (UInt128)long.MaxValue ? long.MaxValue : (long)sum;
        if (candidate <= 0)
        {
            return;
        }

        var current = Volatile.Read(ref _localLeaseUntil);
        while (current != LeaseSurrendered && candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref _localLeaseUntil, candidate, current);
            if (observed == current)
            {
                var nowElapsed = Math.Max(0L, _timeProvider.GetTimestamp() - _leaseEpochBase);
                var prev = Volatile.Read(ref _lastLeaseExtendTicks);
                while (nowElapsed > prev)
                {
                    var seen = Interlocked.CompareExchange(ref _lastLeaseExtendTicks, nowElapsed, prev);
                    if (seen == prev)
                    {
                        break;
                    }

                    prev = seen;
                }

                return;
            }

            current = observed;
        }
    }

    /// <summary>
    /// Stops this ledger's writer and permanently surrenders its lease upon observing a durable seal or drop from a replica
    /// (for example from a batched peer heartbeat; LP-I14, LP-I18).
    /// </summary>
    internal void OnObservedSeal() => StopWriter();

    /// <summary>
    /// Creates the ledger's one writer, with a new sequencer lifetime of the ledger's window <c>K</c> under a fresh
    /// instance ID (LP-I15), which this <see cref="Ledger"/> holds so that its recoveries and seal checks can stop it
    /// (LP-I14). Performs no I/O. The writer appends (ledger-protocol.md §2.3); Ledgers' proposals need a writer created
    /// through the reader's lease of the ledger (ledgers.md §2.4).
    /// </summary>
    /// <param name="timeProvider">Times the delays between the writer's repeated writes.</param>
    /// <returns>The writer, which its caller disposes; it never disposes this <see cref="Ledger"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The ledger already has a writer: one writer gives each slot one batch (LP-I1), so a ledger has one for its life.
    /// </exception>
    public LedgerWriter CreateWriter(TimeProvider timeProvider) => CreateWriter(new Sequencer(WindowSize), timeProvider, serving: null);

    /// <summary>
    /// Adopts <paramref name="lifetime"/> as the lifetime of the ledger's one writer and creates the writer over it. A
    /// lifetime adopted after a recovery in this process, or a seal check there which found the ledger sealed, is stopped
    /// with <see cref="SequencerStopReason.Sealing"/> at once (LP-I14).
    /// </summary>
    /// <param name="lifetime">A lifetime of the ledger's window, which no other <see cref="Ledger"/> adopted.</param>
    /// <param name="timeProvider">Times the delays between the writer's repeated writes.</param>
    /// <param name="serving">What Ledgers' proposals need, or <see langword="null"/> for an append path alone.</param>
    /// <exception cref="ArgumentException">The lifetime's window is not the ledger's.</exception>
    /// <exception cref="InvalidOperationException">The ledger already has a writer (LP-I1).</exception>
    internal LedgerWriter CreateWriter(Sequencer lifetime, TimeProvider timeProvider, LedgerWriter.Serving? serving)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (lifetime.WindowSize != WindowSize)
        {
            throw new ArgumentException("The writer's lifetime has the ledger's window K (LP-I2).", nameof(lifetime));
        }

        if (Interlocked.CompareExchange(ref _writerLifetime, lifetime, null) is not null)
        {
            throw new InvalidOperationException(
                "The ledger already has a writer: one writer gives each slot one batch (LP-I1), so a ledger has one for its life.");
        }

        // LP-I14: the adoption and StopWriter each publish their write with a full fence before reading the other's, so
        // a lifetime adopted while a recovery or a sealed seal check runs is stopped by one of them.
        if (Volatile.Read(ref _sealedHere) != 0)
        {
            lifetime.Stop(SequencerStopReason.Sealing);
        }

        var writer = new LedgerWriter(this, lifetime, timeProvider, serving);
        if (serving is not null)
        {
            Volatile.Write(ref _served, 1);
        }

        return writer;
    }
    /// <summary>
    /// Reads one slot, reporting whether it holds a batch, carrying the known contiguous tail.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The batch, which the caller owns and disposes; <see cref="ReplicaRead.Missing"/>; or, when it cannot decide,
    /// <see cref="ReplicaWriteOutcome.Dropped"/> or <see cref="ReplicaWriteOutcome.Backpressure"/> when a replica
    /// answered it (LP-I5, ledger-protocol.md §6.4).
    /// </returns>
    /// <remarks>
    /// Below the known contiguous tail the slot is decided: one replica's batch answers it, and a read which finds none
    /// fails rather than answering <see cref="ReplicaRead.Missing"/> (LP-I10); it asks replicas by ledger-protocol.md
    /// §8.3's policy. At or above it, the read reports <see cref="ReplicaRead.Missing"/> once <c>Q</c> replicas do;
    /// neither result decides the slot, and a batch there asserts only that a write was attempted (ledger-protocol.md
    /// §8.3).
    /// </remarks>
    public ValueTask<ReplicaRead> ReadAsync(Slot slot, CancellationToken cancellationToken = default) =>
        _storage.ReadAsync(slot, KnownContiguousTail, cancellationToken);

    /// <summary>
    /// Reads bytes <c>[offset, offset + length)</c> of the batch at one slot, a byte-range read, or reports that it holds
    /// none, as <see cref="ReadAsync(Slot, CancellationToken)"/> does; its storage forwards the range where it can.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="offset">The first byte of the range; at least zero.</param>
    /// <param name="length">The number of bytes in the range; positive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The range, which the caller owns and disposes; <see cref="ReplicaRead.OutsideBatch"/> when the range extends past the
    /// batch; or the read's other answer.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative or <paramref name="length"/> is not positive.
    /// </exception>
    public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, CancellationToken cancellationToken = default) =>
        _storage.ReadAsync(slot, offset, length, KnownContiguousTail, cancellationToken);

    /// <summary>
    /// Reads the consecutive slots <c>[from, below)</c> in slot order through its storage (ledger-protocol.md §2.7,
    /// object-ledgers.md §8.4), carrying the known contiguous tail.
    /// </summary>
    /// <param name="from">The first slot.</param>
    /// <param name="below">The slot after the last; at least <paramref name="from"/>.</param>
    /// <param name="window">An optional shared in-flight read semaphore.</param>
    /// <param name="cancellationToken">Stops the enumeration.</param>
    public IAsyncEnumerable<ReplicaRead> ReadRangeAsync(
        Slot from,
        Slot below,
        SemaphoreSlim? window = null,
        CancellationToken cancellationToken = default) =>
        _storage.ReadRangeAsync(from, below, KnownContiguousTail, window, cancellationToken);

    internal IAsyncEnumerable<ReplicaRead> ReadRangePointWiseAsync(
        Slot from,
        Slot below,
        long maxReadAheadBytes,
        SemaphoreSlim? window,
        Action<long>? onReadIssued,
        CancellationToken cancellationToken) =>
        LedgerRangeReads.ReadPointWiseAsync(_storage, from, below, KnownContiguousTail, maxReadAheadBytes, window, onReadIssued, cancellationToken);

    internal bool SupportsReadSlots => _storage.SupportsReadSlots;

    internal ValueTask<ReplicaSlotsRead> ReadSlotsAsync(ReadOnlyMemory<Slot> slots, CancellationToken cancellationToken = default) =>
        _storage.ReadSlotsAsync(slots, KnownContiguousTail, cancellationToken);

    /// <summary>
    /// Checks the seal through its storage, over <c>Q</c> replicas for a ledger on nodes, carrying the known contiguous
    /// tail, and raises it to the tail the check answers (LP-I9, LP-I11, LL-I5). When it finds the ledger sealed, it stops its writer's lifetime with
    /// <see cref="SequencerStopReason.Sealing"/> before it answers (LP-I14).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A <see cref="LedgerTail"/> holding the ledger's known contiguous tail after the call, and whether a replica answered
    /// sealed or dropped (LP-I5); its <see cref="LedgerTail.IsSealed"/> false proves no recovery of the ledger completed
    /// before the call began, and its <see cref="LedgerTail.ServedHere"/> that this ledger held the writer which serves
    /// Ledgers' proposals before the call began, so the tail covers every proposal that writer answered durable. Or, when the check cannot decide, <see cref="ReplicaWriteOutcome.Backpressure"/> when a
    /// replica answered it, which asserts nothing (ledger-protocol.md §6.4).
    /// </returns>
    public ValueTask<LedgerTailRead> ReadTailAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _served) != 0 && HasValidLease)
        {
            // LP-I18, LG-I14: valid primary lease guarantees no successor ledger has committed in LedgerMap, so
            // KnownContiguousTail covers every proposal answered durable here with zero replica RPCs.
            return new ValueTask<LedgerTailRead>(new LedgerTail(KnownContiguousTail, IsSealed: false, ServedHere: true));
        }

        return ReadTailQuorumAsync(cancellationToken);
    }

    /// <summary>
    /// Checks the seal through its storage, bypassing any local primary lease, with singleflight coalescing so
    /// concurrent callers share at most one in-flight and one queued check begun after their arrival (LP-I9,
    /// ledgers.md §8.4).
    /// </summary>
    internal ValueTask<LedgerTailRead> ReadTailQuorumAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<LedgerTailRead> check;
        var start = false;
        lock (_tailCheckLock)
        {
            if (_runningTailCheck is null)
            {
                check = _runningTailCheck = NewTailCheck();
                start = true;
            }
            else
            {
                check = _queuedTailCheck ??= NewTailCheck();
            }
        }

        if (start)
        {
            // The coalesced check serves every caller, so it runs in none's execution context (conventions.md §12).
            using (ExecutionContext.SuppressFlow())
            {
                _ = RunTailChecksAsync(check);
            }
        }

        return check.Task.IsCompletedSuccessfully
            ? new ValueTask<LedgerTailRead>(check.Task.Result)
            : new ValueTask<LedgerTailRead>(check.Task.WaitAsync(cancellationToken));
    }

    private static TaskCompletionSource<LedgerTailRead> NewTailCheck() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunTailChecksAsync(TaskCompletionSource<LedgerTailRead> check)
    {
        while (true)
        {
            try
            {
                var servedHere = Volatile.Read(ref _served) != 0;
                var answer = await _storage.CheckAsync(KnownContiguousTail, CancellationToken.None).ConfigureAwait(false);
                LedgerTailRead resolved = answer switch
                {
                    LedgerTail tail => HandleTailAnswer(tail, servedHere),
                    ReplicaWriteOutcome.Backpressure => answer,
                    null => throw new UnreachableException("conventions.md §15: a seal check answers a union which holds a case."),
                };
                _ = check.TrySetResult(resolved);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = check.TrySetException(exception);
                _ = check.Task.Exception;
            }

            lock (_tailCheckLock)
            {
                _runningTailCheck = _queuedTailCheck;
                _queuedTailCheck = null;
                if (_runningTailCheck is null)
                {
                    return;
                }

                check = _runningTailCheck;
            }
        }

        LedgerTail HandleTailAnswer(LedgerTail tail, bool servedHere)
        {
            if (tail.IsSealed)
            {
                StopWriter();
            }

            return tail with { ContiguousTail = RaiseKnownContiguousTail(tail.ContiguousTail), ServedHere = servedHere };
        }
    }

    /// <summary>
    /// Learns a contiguous tail through its storage without a seal check, from the replica on the caller's node for a
    /// ledger on nodes, carrying the known contiguous tail, and raises it to the tail the storage answers (LP-I11,
    /// ledger-protocol.md §8.2): a replica's retained contiguous tail is at most the ledger's (LR-I6).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ledger's known contiguous tail after the call.</returns>
    public async ValueTask<Slot> LearnTailAsync(CancellationToken cancellationToken = default) =>
        RaiseKnownContiguousTail(await _storage.LearnTailAsync(KnownContiguousTail, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Seals the ledger through its storage and recovers its candidate end, BookKeeper's ledger recovery for a ledger on
    /// nodes, from the known contiguous tail, which it raises to the recovery's <c>c</c> (LP-I6, LP-I7, LP-I11, LL-I3,
    /// ledger-protocol.md §8.3). It first stops
    /// its writer's lifetime with <see cref="SequencerStopReason.Sealing"/>, before its first seal request (LP-I14).
    /// </summary>
    /// <param name="cancellationToken">Stops the recovery; the seals and recovery writes it sent may still land.</param>
    /// <returns>
    /// <see cref="RecoveryOutcome.Recovered"/> with the candidate end: every slot below it holds a batch on <c>Q</c>
    /// replicas, and no writer write at it ever reaches <c>Q</c> written answers. It is a candidate until the ledger map's
    /// conditional write records it as the ledger's <c>T</c>, so the ledger never takes it as a known contiguous tail;
    /// concurrent recoveries may return different ends. <see cref="ReplicaWriteOutcome.Dropped"/> when a replica answered
    /// a seal, a read, or a recovery write <c>Dropped</c>: the ledger has left the map, so no close can apply (LP-I5).
    /// </returns>
    /// <remarks>
    /// The recovery's failures are its storage's (ledger-protocol.md §2.2 for <see cref="LedgerQuorum.RecoverAsync"/>), and
    /// a recovery which disposal stops fails with <see cref="ObjectDisposedException"/>.
    /// </remarks>
    public async ValueTask<RecoveryOutcome> RecoverAsync(CancellationToken cancellationToken = default)
    {
        StopWriter();
        var outcome = await _storage.RecoverAsync(KnownContiguousTail, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            LedgerRecovery recovered => Recovered(recovered),
            ReplicaWriteOutcome.Dropped dropped => dropped,
            null => throw new UnreachableException(),
        };

        RecoveryOutcome Recovered(LedgerRecovery recovered)
        {
            // LP-I11: c is a contiguous tail; the end is only a candidate until the ledger map records it.
            _ = RaiseKnownContiguousTail(recovered.ContiguousTail);
            return new RecoveryOutcome.Recovered(recovered.End, recovered.LeaseRemaining);
        }
    }

    /// <summary>
    /// Learns the ledger map's recorded <c>T</c> for this ledger and raises the known contiguous tail to it (LP-I11).
    /// Synchronous; performs no I/O.
    /// </summary>
    /// <param name="recordedTail">
    /// The <c>T</c> the ledger map recorded when it closed this ledger, and never another candidate end: a losing
    /// recovery's end may lie above or below it.
    /// </param>
    /// <remarks>
    /// Every slot below the recorded <c>T</c> holds a batch on <c>Q</c> replicas (LP-I7), and the map records one
    /// candidate end for the ledger's life, so <c>T</c> is the ledger's contiguous tail. Every later operation carries
    /// it.
    /// </remarks>
    public void ObserveRecordedTail(Slot recordedTail)
    {
        Debug.Assert(
            Interlocked.Read(ref _knownContiguousTail) <= recordedTail.Value,
            "The known contiguous tail is at most the ledger's recorded T.");
        var earlier = Interlocked.CompareExchange(ref _recordedTail, recordedTail.Value, -1);
        Debug.Assert(earlier == -1 || earlier == recordedTail.Value, "The ledger map records one T for the ledger's life.");
        _ = RaiseKnownContiguousTail(recordedTail);
    }

    /// <summary>
    /// Suppresses read-repair recovery writes on the preferred replica (used by <see cref="LedgerReader"/> once a
    /// ledger is recorded <c>Closed(T)</c> in the map).
    /// </summary>
    internal void SuppressReadRepair() => _storage.SuppressReadRepair();

    /// <summary>
    /// Disposes the ledger and its storage, which ends every call it has in flight by cancelling it (ledger-protocol.md
    /// §6.3).
    /// </summary>
    /// <returns>A task which completes once the storage is disposed.</returns>
    public ValueTask DisposeAsync() => _storage.DisposeAsync();

    /// <summary>
    /// Stops the ledger's reads, seal checks, and recoveries, which fail with <see cref="ObjectDisposedException"/>, while
    /// its writes go on (<see cref="ILedgerStorage.StopReadsAsync"/>).
    /// </summary>
    /// <returns>A task which completes once the cancellation's callbacks ran.</returns>
    public Task StopReadsAsync() => _storage.StopReadsAsync();

    /// <summary>
    /// Writes <paramref name="payload"/> at <paramref name="slot"/> as the writer, carrying the known contiguous tail
    /// with its storage's answers and failures (<see cref="ILedgerStorage.WriteAsync"/>; LP-I4 for the quorum).
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// The ledger was disposed before the write began, or its disposal ended the write's replica calls; the write asserts
    /// nothing, since it may have landed, and its writer stops its lifetime, leaving the recovered <c>T</c> to decide the
    /// slot (LP-I13).
    /// </exception>
    internal ValueTask<LedgerWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        CancellationToken cancellationToken = default)
    {
        var carried = KnownContiguousTail;
        _ = RaiseCarriedContiguousTail(carried);
        if (!_leaseOptions.IsEnabled || Volatile.Read(ref _served) == 0 || Volatile.Read(ref _localLeaseUntil) == LeaseSurrendered)
        {
            return _storage.WriteAsync(slot, payload, carried, cancellationToken);
        }

        // LP-I18: record sendTimestamp before dispatching the write so the primary's lease expiry is bounded by
        // sendTimestamp + floor(minGrant / F), never by receive time.
        var sendTimestamp = _timeProvider.GetTimestamp();
        return WriteAndExtendLeaseAsync(slot, payload, carried, sendTimestamp, cancellationToken);
    }

    private async ValueTask<LedgerWriteOutcome> WriteAndExtendLeaseAsync(
        Slot slot,
        ArcBuffer payload,
        Slot carried,
        long sendTimestamp,
        CancellationToken cancellationToken)
    {
        var outcome = await _storage.WriteAsync(slot, payload, carried, cancellationToken).ConfigureAwait(false);
        if (outcome.Value is ReplicaWriteOutcome.Written { LeaseGrant: var grant } && grant > TimeSpan.Zero)
        {
            TryExtendLease(sendTimestamp, grant);
        }
        else if (outcome.Value is ReplicaWriteOutcome.Sealed)
        {
            SurrenderLease();
        }

        return outcome;
    }

    /// <summary>
    /// Writes the known contiguous tail explicitly to its storage (<see cref="ILedgerStorage.WriteContiguousTail"/>), BookKeeper's
    /// explicit last add confirmed, when it exceeds every tail the writer's writes and earlier explicit writes carried, so
    /// readers in other processes can read the ledger's last batches from one replica (LP-I10, LP-I11, ledger-protocol.md
    /// §8.2). Its writer calls it once it has been idle for <see cref="LedgerWriter.ContiguousTailInterval"/>. Starts what
    /// it sends and returns; performs no I/O of its own.
    /// </summary>
    /// <returns>Whether it wrote the tail: <see langword="false"/> when a write or an earlier explicit write carried it.</returns>
    internal bool WriteContiguousTail()
    {
        // A closed ledger's readers learn its recorded T from the map, so only an open ledger's tail is written. The tail is
        // read first: ObserveRecordedTail records T before it raises the tail, so a tail T raised shows T recorded.
        var tail = KnownContiguousTail;
        if (Interlocked.Read(ref _recordedTail) >= 0)
        {
            return false;
        }
        if (!RaiseCarriedContiguousTail(tail))
        {
            return false;
        }

        _storage.WriteContiguousTail(tail);
        return true;
    }

    /// <summary>
    /// Raises the known contiguous tail to <paramref name="contiguousTail"/>, a contiguous tail of the ledger (LP-I11); it
    /// never decreases.
    /// </summary>
    /// <returns>The known contiguous tail afterwards: the greater of <paramref name="contiguousTail"/> and the earlier one.</returns>
    internal Slot RaiseKnownContiguousTail(Slot contiguousTail)
    {
        var value = contiguousTail.Value;
        AssertAtMostRecordedTail(value);
        var current = Interlocked.Read(ref _knownContiguousTail);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref _knownContiguousTail, value, current);
            if (observed == current)
            {
                return contiguousTail;
            }

            current = observed;
        }

        return new Slot(current);
    }

    /// <summary>
    /// Raises the greatest tail carried to the storage to <paramref name="contiguousTail"/>, returning whether it rose.
    /// </summary>
    private bool RaiseCarriedContiguousTail(Slot contiguousTail)
    {
        var value = contiguousTail.Value;
        var current = Interlocked.Read(ref _carriedContiguousTail);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref _carriedContiguousTail, value, current);
            if (observed == current)
            {
                return true;
            }

            current = observed;
        }

        return false;
    }

    /// <summary>
    /// Stops the lifetime of the ledger's writer, if it has one, with <see cref="SequencerStopReason.Sealing"/>: a stop
    /// cannot fail and is idempotent, so it runs even while the writer is being disposed (LP-I13, LP-I14).
    /// </summary>
    private void StopWriter()
    {
        SurrenderLease();
        _ = Interlocked.Exchange(ref _sealedHere, 1);
        Volatile.Read(ref _writerLifetime)?.Stop(SequencerStopReason.Sealing);
    }

    /// <summary>
    /// Asserts the sender's side of LG-I7 for a send which acts from <paramref name="map"/> (ledgers.md §8.5): the map
    /// lists the ledger at the location its sends reach, its replicas' nodes or the object store, which a ledger keeps for
    /// its life; or has revoked it, for a sender which acted from an earlier map that listed it and whose view has since
    /// advanced past the ledger's removal.
    /// </summary>
    [Conditional("DEBUG")]
    internal void AssertSendsFrom(LedgerMapValue map) =>
        Debug.Assert(
            map.Get(Id) is { } listed ? listed.Location.Equals(_location) : map.NoLongerLists(Id),
            "A ledger's sends reach its storage only from a map which lists the ledger there (LG-I7).");

    /// <summary>
    /// Asserts that a contiguous tail the ledger learns or carries lies at or below its recorded <c>T</c> once it is
    /// observed (LP-I11).
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertAtMostRecordedTail(long contiguousTail)
    {
        var recorded = Interlocked.Read(ref _recordedTail);
        Debug.Assert(recorded < 0 || contiguousTail <= recorded, "A known contiguous tail is at most the ledger's recorded T.");
    }
}

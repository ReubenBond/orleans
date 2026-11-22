using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The system the fold oracle drives over an object-store ledger (object-ledgers.md §2): the primary's process, its
/// <see cref="Ledger"/> and its <see cref="LedgerWriter"/>, and other processes' <see cref="Ledger"/>s, which check and
/// recover the ledger, each over its own <see cref="ObjectLedgerStorage"/> of one object store; and the sweeper, which
/// reclaims the ledger once the map revoked it. It keeps no recording, so <see cref="LedgerStorageSpec"/> judges its
/// answers alone.
/// </summary>
/// <remarks>
/// An object store which fails a call transiently, with an <see cref="IOException"/> or a <see cref="TimeoutException"/>, as
/// a fault-injecting or simulated store does, leaves a read, a check,
/// or a sweep to its caller, which repeats it, as the reader's callers and the composition do; the writer and the
/// recovery repeat their own. An append whose outcome is unknown loses the writer's create, before or after it lands,
/// with a failure no repetition changes, so the writer stops its lifetime (LP-I13).
/// </remarks>
internal sealed class ObjectLedgerSystem : ILedgerConformanceSystem, IConformanceEvents
{
    private const int Repeats = 1_000;

    private static readonly LedgerId Id = new(7);

    /// <summary>The map's next ledger ID, above the system's one ledger.</summary>
    private static readonly LedgerId NextLedgerId = new(8);

    private readonly LosingObjectStore _store;
    private readonly ObjectLedgerStore _ledgers;
    private readonly ObjectLedgerSweeper _sweeper;
    private readonly int _window;
    private readonly IAsyncDisposable? _owned;
    private readonly List<long[]> _appends = [];
    private readonly Ledger _primary;
    private readonly LedgerWriter _writer;
    private long _end = -1;
    private long _ticks;

#if DURABLE_STORAGE_SIMULATION_TESTS
    private static TimeProvider SystemClock => Orleans.Durable.Storage.Simulation.SimulationEnvironment.Clock;

    private static CancellationToken Cancellation => CancellationToken.None;
#else
    private static TimeProvider SystemClock => TimeProvider.System;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
#endif

    private ObjectLedgerSystem(LosingObjectStore store, ObjectLedgerStore ledgers, int window, IAsyncDisposable? owned)
    {
        _store = store;
        _window = window;
        _owned = owned;
        _ledgers = ledgers;
        _sweeper = new ObjectLedgerSweeper(_ledgers);
        _primary = Open();
        _writer = _primary.CreateWriter(SystemClock);
    }

    /// <summary>Gets the next tick of a logical clock, which orders the appends' intervals, as the virtual clock may not.</summary>
    private long Now => Interlocked.Increment(ref _ticks);

    /// <summary>
    /// Returns a per-slot system over <paramref name="store"/>, whose ledgers have the window <paramref name="window"/>,
    /// which disposes <paramref name="owned"/> last.
    /// </summary>
    internal static ObjectLedgerSystem Over(IObjectStore store, int window, IAsyncDisposable? owned = null)
    {
        var losing = new LosingObjectStore(store);
        // Disable the local cache so conformance reads always exercise the IObjectStore boundary under faults,
        // latency yields, and the acknowledge-before-landing race mutant (cache equivalence is tested in ObjectLedgerCacheTests).
        var ledgers = ObjectLedgerStore.Over(losing, new ObjectLedgerStoreOptions { CacheBytes = 0 }, SystemClock);
        return new(losing, ledgers, window, owned);
    }

    /// <summary>
    /// Returns an append-layout (<c>CLOE</c>) system over <paramref name="store"/>, whose ledgers have the window
    /// <paramref name="window"/>, which disposes <paramref name="owned"/> last.
    /// </summary>
    internal static ObjectLedgerSystem OverAppendable<TStore>(
        TStore store,
        int window,
        ObjectLedgerStoreOptions? options = null,
        IAsyncDisposable? owned = null)
        where TStore : IObjectStore, IAppendableObjectStore
    {
        var losing = new LosingAppendableObjectStore<TStore>(store);
        var effective = options ?? new ObjectLedgerStoreOptions
        {
            Layout = ObjectLedgerLayout.Append,
            ExtentTargetBytes = 256,
            ExtentMaxAppends = 3,
            CacheBytes = 0,
        };
        var ledgers = ObjectLedgerStore.OverAppendable(losing, effective, SystemClock);
        return new(losing, ledgers, window, owned);
    }

    /// <summary>Gets the object store the system's ledgers lie in, beneath the store which loses the writer's creates.</summary>
    internal IObjectStore Inner => _store.Inner;

    /// <summary>Returns the store's events, such as its injected faults, when it records any.</summary>
    public IReadOnlyList<string> TakeEvents() => _store.Inner is IConformanceEvents events ? events.TakeEvents() : [];

    public Task<LedgerOutcome> AppendAsync(LedgerAppend request) => AppendAsync(request.Value, twice: false);

    public async Task<LedgerOutcome> AcquireAsync(LedgerAcquire request)
    {
        var cancellationToken = Cancellation;
        switch (request.Use)
        {
            case AcquireUse.AppendTwice:
                return await AppendAsync(request.Value, twice: true);
            case AcquireUse.AppendForeign:
                {
                    using var payload = ArcBuffer.Wrap(LedgerStorageSpec.Payload(request.Value));
                    try
                    {
                        _ = await _writer.AppendAsync(new Slot(1_000), payload, cancellationToken);
                        return new LedgerOutcome(LedgerOutcomeKind.AppendedTwice, 1_000);
                    }
                    catch (ArgumentException)
                    {
                        return new LedgerOutcome(LedgerOutcomeKind.Refused);
                    }
                }

            case AcquireUse.Cancelled:
                {
                    using var cancelled = new CancellationTokenSource();
                    await cancelled.CancelAsync();
                    try
                    {
                        return await _writer.AcquireAsync(cancelled.Token) is { } slot
                            ? new LedgerOutcome(LedgerOutcomeKind.Appended, slot.Value)
                            : new LedgerOutcome(LedgerOutcomeKind.Unresolved, -1);
                    }
                    catch (OperationCanceledException)
                    {
                        return new LedgerOutcome(LedgerOutcomeKind.Cancelled);
                    }
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public async Task<LedgerOutcome> AppendUnansweredAsync(LedgerAppendUnanswered request)
    {
        _store.Lose(request.Landed, lost: true);
        try
        {
            return await AppendAsync(request.Value, twice: false);
        }
        finally
        {
            _store.Lose(request.Landed, lost: false);
        }
    }

    public Task<LedgerOutcome> ReadAsync(LedgerSlotRead request) =>
        ReadAsync((ledger, cancellationToken) => ledger.ReadAsync(new Slot(request.Slot), cancellationToken), refusesRange: false);

    public Task<LedgerOutcome> ReadRangeAsync(LedgerRangeRead request) =>
        ReadAsync((ledger, cancellationToken) => ledger.ReadAsync(new Slot(request.Slot), request.Offset, request.Length, cancellationToken), refusesRange: true);

    /// <summary>
    /// Reads every slot from the scan's first below the primary's known contiguous tail when it begins, in slot order, as
    /// a drain does, through <see cref="Ledger.ReadRangeAsync(Slot, Slot, SemaphoreSlim?, CancellationToken)"/>: each
    /// answers its batch, or <c>Dropped</c> once the ledger's storage is reclaimed (LL-I6, LL-I9, object-ledgers.md §8.4).
    /// </summary>
    public async Task<LedgerOutcome> ScanAsync(LedgerScan request)
    {
        var below = _primary.KnownContiguousTail.Value;
        List<byte[]?> batches = [];
        var next = request.From;
        while (next < below)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await foreach (var read in _primary.ReadRangeAsync(new Slot(next), new Slot(below), cancellationToken: Cancellation))
                    {
                        if (read.TryGetValue(out ArcBuffer payload))
                        {
                            using (payload)
                            {
                                batches.Add(payload.ToArray());
                            }
                        }
                        else if (read.Case == ReplicaReadCase.Dropped)
                        {
                            batches.Add(null);
                        }
                        else
                        {
                            // LL-I6: a read below the bound never answers Missing.
                            return new LedgerOutcome(LedgerOutcomeKind.Missing, next);
                        }

                        next++;
                    }

                    break;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException && attempt < Repeats)
                {
                }
            }
        }

        return new LedgerOutcome(LedgerOutcomeKind.Scanned, below, Batches: batches);
    }

    /// <summary>
    /// Checks the seal through the primary's <see cref="Ledger"/>, or through a fresh <see cref="Ledger"/> of another
    /// process, which knows no tail, as a reader's check does.
    /// </summary>
    public async Task<LedgerOutcome> CheckAsync(LedgerCheck request)
    {
        var ledger = request.Elsewhere ? Open() : _primary;
        var stoppedBefore = _writer.Sequencer.IsStopped;
        var start = Now;
        try
        {
            var tail = await RepeatAsync(token => ledger.ReadTailAsync(token));
            var end = Now;
            return new LedgerOutcome(
                LedgerOutcomeKind.Tail,
                tail.Tail.ContiguousTail.Value,
                tail.Tail.IsSealed,
                LifetimeStopped: Stopped(request.Elsewhere, stoppedBefore),
                OverlappedAppend: request.Elsewhere && OverlappedAnyAppend(start, end));
        }
        finally
        {
            if (request.Elsewhere)
            {
                await ledger.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Learns a contiguous tail through the primary's <see cref="Ledger"/>, or through a fresh <see cref="Ledger"/> of
    /// another process, which knows no tail.
    /// </summary>
    public async Task<LedgerOutcome> LearnAsync(LedgerLearn request)
    {
        var ledger = request.Elsewhere ? Open() : _primary;
        try
        {
            var tail = await ledger.LearnTailAsync(Cancellation);
            return new LedgerOutcome(LedgerOutcomeKind.Learned, tail.Value, LifetimeStopped: _writer.Sequencer.IsStopped);
        }
        finally
        {
            if (request.Elsewhere)
            {
                await ledger.DisposeAsync();
            }
        }
    }

    public async Task<LedgerOutcome> RecoverAsync(LedgerRecover request)
    {
        var ledger = request.Elsewhere ? Open() : _primary;
        var stoppedBefore = _writer.Sequencer.IsStopped;
        var start = Now;
        try
        {
            var outcome = (await ledger.RecoverAsync(Cancellation)).Value;
            var end = Now;
            var overlapped = request.Elsewhere && OverlappedAnyAppend(start, end);
            switch (outcome)
            {
                case RecoveryOutcome.Recovered { End: var recovered }:
                    _ = Interlocked.CompareExchange(ref _end, recovered.Value, -1);
                    return new LedgerOutcome(LedgerOutcomeKind.End, recovered.Value, LifetimeStopped: Stopped(request.Elsewhere, stoppedBefore), OverlappedAppend: overlapped);
                case ReplicaWriteOutcome.Dropped:
                    // LL-I9: a recovery which meets the reclamation answers Dropped.
                    return new LedgerOutcome(LedgerOutcomeKind.Dropped, LifetimeStopped: Stopped(request.Elsewhere, stoppedBefore), OverlappedAppend: overlapped);
                case null:
                default:
                    throw new UnreachableException();
            }
        }
        finally
        {
            if (request.Elsewhere)
            {
                await ledger.DisposeAsync();
            }
        }
    }

    public LedgerOutcome Observe()
    {
        _primary.ObserveRecordedTail(new Slot(Interlocked.Read(ref _end)));
        return new LedgerOutcome(LedgerOutcomeKind.Observed);
    }

    /// <summary>
    /// Reclaims the ledger's storage once the map revoked the closed ledger: its first durable step, the drop marker, or
    /// the sweeper's whole reclamation (LL-I9, OL-I4).
    /// </summary>
    public async Task<LedgerOutcome> DropAsync(LedgerDrop request)
    {
        if (request.Every)
        {
            _ = await RepeatAsync(token => _sweeper.SweepAsync(static id => id == Id, NextLedgerId, token));
        }
        else
        {
            _ = await RepeatAsync(token => _store.CreateAsync(ObjectLedgerNames.Dropped(Id), default, crc32c: null, token));
        }

        return new LedgerOutcome(LedgerOutcomeKind.Reclaimed);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await _primary.DisposeAsync();
        if (_owned is not null)
        {
            await _owned.DisposeAsync();
        }
    }

    /// <summary>
    /// Returns whether the writer's lifetime is stopped as a check or a recovery reports it: another process's never
    /// changes it, so it reports the lifetime as it stood before the call, which a concurrent append that met the seal the
    /// call placed may have stopped since; the primary's reports it after the call.
    /// </summary>
    private bool Stopped(bool elsewhere, bool before) => elsewhere ? before : _writer.Sequencer.IsStopped;

    private bool StoppedBySeal => _writer.Sequencer.Stopped is { IsCompletedSuccessfully: true, Result: SequencerStopReason.Sealing };

    private bool OverlappedAnyAppend(long start, long end)
    {
        lock (_appends)
        {
            return _appends.Exists(other => other[0] < end && other[1] > start);
        }
    }

    /// <summary>Repeats a call which failed transiently, as the reader's callers and the composition's sweeps do.</summary>
    private static async Task<T> RepeatAsync<T>(Func<CancellationToken, ValueTask<T>> call)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call(Cancellation);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException && attempt < Repeats)
            {
            }
        }
    }

    private async Task<LedgerOutcome> ReadAsync(Func<Ledger, CancellationToken, ValueTask<ReplicaRead>> readAsync, bool refusesRange)
    {
        var read = await RepeatAsync(token => readAsync(_primary, token));
        if (refusesRange && read.TryGetValue(out ReplicaRead.OutsideBatch? _))
        {
            return new LedgerOutcome(LedgerOutcomeKind.RangeRefused);
        }

        byte[]? bytes = null;
        if (read.TryGetValue(out ArcBuffer payload))
        {
            using (payload)
            {
                bytes = payload.ToArray();
            }
        }

        var kind = read.Case switch
        {
            ReplicaReadCase.Batch => LedgerOutcomeKind.Batch,
            ReplicaReadCase.Missing => LedgerOutcomeKind.Missing,
            ReplicaReadCase.Dropped => LedgerOutcomeKind.Dropped,
            ReplicaReadCase.Backpressure => throw new InvalidOperationException("An object-store ledger answered Backpressure."),
            ReplicaReadCase.OutsideBatch => throw new InvalidOperationException("A whole read answered a range past its batch."),
            _ => throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The read answered {read.Case}.")),
        };
        return new LedgerOutcome(kind, Bytes: bytes);
    }

    private async Task<LedgerOutcome> AppendAsync(byte value, bool twice)
    {
        var start = Now;
        long[] interval = [start, long.MaxValue];
        lock (_appends)
        {
            _appends.Add(interval);
        }

        AppendOutcome outcome;
        using (var payload = ArcBuffer.Wrap(LedgerStorageSpec.Payload(value)))
        {
            if (!twice)
            {
                outcome = await _writer.AppendAsync(payload, Cancellation);
            }
            else if (await _writer.AcquireAsync(Cancellation) is { } acquired)
            {
                outcome = await _writer.AppendAsync(acquired, payload, Cancellation);
                try
                {
                    _ = await _writer.AppendAsync(acquired, payload, Cancellation);
                    return new LedgerOutcome(LedgerOutcomeKind.AppendedTwice, acquired.Value);
                }
                catch (ArgumentException)
                {
                    // ledger-protocol.md §2.3: the slot was already appended.
                }
            }
            else
            {
                outcome = new AppendOutcome(new AppendOutcome.Unresolved(Slot: null));
            }
        }

        var end = Now;
        bool overlapped;
        lock (_appends)
        {
            interval[1] = end;
            overlapped = _appends.Exists(other => !ReferenceEquals(other, interval) && other[0] < end && other[1] > start);
        }

        var (kind, slot) = outcome.Value switch
        {
            AppendOutcome.Appended appended => (LedgerOutcomeKind.Appended, appended.Slot.Value),
            AppendOutcome.Sealed sealedAt => (LedgerOutcomeKind.Sealed, sealedAt.Slot.Value),
            AppendOutcome.Unresolved unresolved => (LedgerOutcomeKind.Unresolved, unresolved.Slot?.Value ?? -1),
            _ => throw new UnreachableException(),
        };
        return new LedgerOutcome(kind, slot, LifetimeStopped: _writer.Sequencer.IsStopped, OverlappedAppend: overlapped, StoppedBySeal: StoppedBySeal);
    }

    /// <summary>Opens a process's <see cref="Ledger"/> over its own storage of the ledger.</summary>
    private Ledger Open() => new(Id, _ledgers.Open(Id, _window), LedgerLocation.ObjectStore.Instance);

    /// <summary>
    /// An object store over another which loses the writer's creates, its batches, on request: before they land, or the
    /// answer of one which landed, each with a failure no repetition changes, as a transport which cannot decode an answer
    /// does.
    /// </summary>
    private class LosingObjectStore(IObjectStore inner) : IObjectStore
    {
        private bool _answersLost;
        private bool _requestsLost;

        public IObjectStore Inner => inner;

        protected bool AnswersLost => Volatile.Read(ref _answersLost);

        protected bool RequestsLost => Volatile.Read(ref _requestsLost);

        public void Lose(bool landed, bool lost)
        {
            if (landed)
            {
                Volatile.Write(ref _answersLost, lost);
            }
            else
            {
                Volatile.Write(ref _requestsLost, lost);
            }
        }

        public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            var batch = content.Length > 0 && ObjectLedgerNames.ParseSlot(Id, name) is not null;
            if (batch && RequestsLost)
            {
                throw new InvalidDataException("The create was lost before it landed, and its answer could not be decoded.");
            }

            var outcome = await inner.CreateAsync(name, content, crc32c, cancellationToken);

            // The create landed when the slot holds a batch, the writer's own: an Exists may answer its own attempt.
            return batch && AnswersLost && await inner.GetPropertiesAsync(name, cancellationToken) is { Length: > 0 }
                ? throw new InvalidDataException("The store's answer to the create could not be decoded.")
                : outcome;
        }

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }

    private sealed class LosingAppendableObjectStore<TStore>(TStore inner) : LosingObjectStore(inner), IAppendableObjectStore
        where TStore : IObjectStore, IAppendableObjectStore
    {
        public ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            if (ObjectLedgerNames.ParseExtent(Id, name) is not null && RequestsLost)
            {
                return ValueTask.FromException<ObjectCreateOutcome>(
                    new InvalidDataException("The extent create was lost before it landed, and its answer could not be decoded."));
            }

            return inner.CreateAppendableAsync(name, cancellationToken);
        }

        public async ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            var isExtentAppend = ObjectLedgerNames.ParseExtent(Id, name) is not null && content.Length > 0;
            if (isExtentAppend && RequestsLost)
            {
                throw new InvalidDataException("The append was lost before it landed, and its answer could not be decoded.");
            }

            var outcome = await inner.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);

            // The append landed when the store answered Appended, or LengthMismatch after a retried attempt already wrote it.
            if (isExtentAppend && AnswersLost)
            {
                var landed = outcome is ObjectAppendOutcome.Appended ||
                    outcome is ObjectAppendOutcome.LengthMismatch &&
                    await inner.GetPropertiesAsync(name, cancellationToken) is { } props &&
                    props.Length >= expectedLength + content.Length;
                if (landed)
                {
                    throw new InvalidDataException("The store's answer to the append could not be decoded.");
                }
            }

            return outcome;
        }

        public ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.SealAsync(name, cancellationToken);

        public ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken);
    }
}

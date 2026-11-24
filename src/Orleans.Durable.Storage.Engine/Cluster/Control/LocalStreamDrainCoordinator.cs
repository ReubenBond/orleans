using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// Coordinates closed-ledger draining of registered ledger-local streams behind <see cref="ILedgerLocalDrainBarrier"/>
/// (archive.md §2.3, orleans.md §5.6, LG-I15, LG-I16).
/// </summary>
/// <remarks>
/// <para>
/// Owns a dedicated <see cref="ByteBudget"/> independent of LSM and archive-log <c>DrainMemoryBytes</c>, coalesces
/// concurrent drain calls per closed ledger, caches completed ledgers until they leave <see cref="LedgerMapView"/>,
/// skips scans in O(1) when <see cref="ILocalKeyPresenceLookup"/> proves every registered stream absent, and otherwise
/// streams raw <see cref="BatchView"/> batches once across all registered streams with deadlock-free chunked flushes.
/// </para>
/// </remarks>
public sealed class LocalStreamDrainCoordinator : ILedgerLocalDrainBarrier
{
    private readonly LedgerMapView _map;
    private readonly LedgerReader _reader;
    private readonly ImmutableArray<ILocalStreamProcessor> _processors;
    private readonly ILocalKeyPresenceLookup? _presenceLookup;
    private readonly ByteBudget _budget;
    private readonly Lock _sync = new();
    private readonly HashSet<LedgerId> _completed = [];
    private readonly Dictionary<LedgerId, SharedDrain> _inFlight = [];
    private ulong _prunedAt;
    private long _presenceFastPathHits;
    private long _sequentialScansStarted;
    private long _chunkedFlushes;

    /// <summary>Initializes a new instance of the <see cref="LocalStreamDrainCoordinator"/> class.</summary>
    /// <param name="map">The process's ledger map view, used to prune completed ledgers and confirm drops.</param>
    /// <param name="reader">The ledger reader used to stream raw <see cref="BatchView"/> batches.</param>
    /// <param name="processors">The registered ledger-local stream processors (distinct stream names).</param>
    /// <param name="memoryBudgetBytes">The dedicated fold memory budget in bytes (at least 1).</param>
    /// <param name="presenceLookup">Optional O(1) local key-index presence lookup for zero-read skips.</param>
    /// <exception cref="ArgumentException">Two processors register the same stream name.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="memoryBudgetBytes"/> is less than 1.</exception>
    public LocalStreamDrainCoordinator(
        LedgerMapView map,
        LedgerReader reader,
        IEnumerable<ILocalStreamProcessor> processors,
        long memoryBudgetBytes = 32L << 20,
        ILocalKeyPresenceLookup? presenceLookup = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(processors);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryBudgetBytes, 1, nameof(memoryBudgetBytes));

        var list = processors.ToList();
        var seen = new HashSet<LocalStreamName>();
        foreach (var processor in list)
        {
            ArgumentNullException.ThrowIfNull(processor);
            if (!seen.Add(processor.Stream))
            {
                throw new ArgumentException($"Duplicate local stream processor registered for '{processor.Stream}'.", nameof(processors));
            }
        }

        list.Sort(static (a, b) => a.Stream.CompareTo(b.Stream));
        _map = map;
        _reader = reader;
        _processors = [.. list];
        _presenceLookup = presenceLookup;
        _budget = new ByteBudget(memoryBudgetBytes);
    }

    /// <summary>Gets the registered stream names in canonical ordinal order.</summary>
    public ImmutableArray<LocalStreamName> RegisteredStreams => [.. _processors.Select(static p => p.Stream)];

    /// <summary>Gets the number of available bytes in the coordinator's dedicated memory budget; for tests.</summary>
    internal long AvailableMemoryBytes => _budget.Available;

    /// <summary>Gets the number of closed ledgers proven absent via <see cref="ILocalKeyPresenceLookup"/> without scanning; for tests.</summary>
    internal long PresenceFastPathHits => Interlocked.Read(ref _presenceFastPathHits);

    /// <summary>Gets the number of sequential raw <see cref="BatchView"/> scans started; for tests.</summary>
    internal long SequentialScansStarted => Interlocked.Read(ref _sequentialScansStarted);

    /// <summary>Gets the number of mid-ledger chunked flushes triggered by <see cref="ByteBudget"/> pressure; for tests.</summary>
    internal long ChunkedFlushes => Interlocked.Read(ref _chunkedFlushes);

    /// <summary>
    /// Gets a value indicating whether the coordinator marks a ledger complete in its cache before committing its final
    /// fold (LG-I16 race mutant for tests).
    /// </summary>
    internal bool MarkCompleteBeforeCommitMutant { get; init; }

    /// <summary>
    /// Gets a value indicating whether the coordinator silently skips unregistered local streams instead of failing
    /// closed with <see cref="ArchiveCorruptException"/> (LG-I15 mutant for tests).
    /// </summary>
    internal bool SkipUnknownStreamMutant { get; init; }

    /// <summary>Optional test hook invoked after each chunked or final fold flush for trace export.</summary>
    internal Action<LedgerId, bool>? OnFlushCommitted { get; init; }

    /// <summary>Optional test hook invoked when the coordinator completes draining a ledger.</summary>
    internal Action<LedgerId, LocalDrainBarrierOutcome>? OnBarrierCompleted { get; init; }

    /// <inheritdoc />
    public void ValidateLocalAppendKey(LedgerMetadata ledger, ReadOnlySpan<byte> rowKey)
    {
        if (LedgerLocalKey.Validate(rowKey, ledger.Range, ledger.Id, out var streamSpan) is { } error)
        {
            throw new ArchiveCorruptException(
                string.Create(CultureInfo.InvariantCulture, $"Ledger {ledger.Id} contains a malformed RowKind.LocalAppend key: {error.Message}"),
                error);
        }

        if (!TryGetProcessorIndex(streamSpan, out _))
        {
            if (SkipUnknownStreamMutant)
            {
                return;
            }

            throw new ArchiveCorruptException(
                string.Create(CultureInfo.InvariantCulture, $"Ledger {ledger.Id} contains a RowKind.LocalAppend row for unregistered stream '{Encoding.ASCII.GetString(streamSpan)}' (LG-I15)."));
        }
    }

    /// <inheritdoc />
    public ValueTask<LocalDrainBarrierOutcome> EnsureLocalStreamsDrainedAsync(
        LedgerMetadata ledger,
        Slot tail,
        CancellationToken cancellationToken)
    {
        if (tail == Slot.Zero || _processors.IsEmpty)
        {
            return ValueTask.FromResult<LocalDrainBarrierOutcome>(LocalDrainBarrierOutcome.Drained.Instance);
        }

        SharedDrain shared;
        bool isLeader;
        lock (_sync)
        {
            PruneLocked(_map.Current);
            if (_completed.Contains(ledger.Id))
            {
                return ValueTask.FromResult<LocalDrainBarrierOutcome>(LocalDrainBarrierOutcome.Drained.Instance);
            }

            if (_inFlight.TryGetValue(ledger.Id, out var existing))
            {
                existing.AddWaiter();
                shared = existing;
                isLeader = false;
            }
            else
            {
                shared = new SharedDrain();
                _inFlight[ledger.Id] = shared;
                isLeader = true;
            }
        }

        if (isLeader)
        {
            shared.Start(RunDrainAsync(ledger, tail, shared));
        }

        return new ValueTask<LocalDrainBarrierOutcome>(JoinAsync(shared, cancellationToken));
    }

    private static async Task<LocalDrainBarrierOutcome> JoinAsync(SharedDrain shared, CancellationToken cancellationToken)
    {
        try
        {
            return await shared.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            shared.RemoveWaiter();
        }
    }

    private async Task<LocalDrainBarrierOutcome> RunDrainAsync(LedgerMetadata ledger, Slot tail, SharedDrain shared)
    {
        var ct = shared.Token;
        try
        {
            // Fast path: O(1) local key-index presence probe over caught-up RocksDB index (0 disk reads when absent).
            if (_presenceLookup is not null && ledger.Location.Value is LedgerLocation.Nodes)
            {
                var allAbsent = true;
                for (var i = 0; i < _processors.Length; i++)
                {
                    var localKey = LedgerLocalKey.Encode(ledger.Id, ledger.Range, _processors[i].Stream);
                    var presence = await _presenceLookup.TryHasKeyEntryAsync(ledger, localKey, tail, ct).ConfigureAwait(false);
                    if (presence.Value is not LocalKeyPresenceRead.Absent)
                    {
                        allAbsent = false;
                        break;
                    }
                }

                if (allAbsent)
                {
                    _ = Interlocked.Increment(ref _presenceFastPathHits);
                    MarkCompleted(ledger.Id);
                    OnBarrierCompleted?.Invoke(ledger.Id, LocalDrainBarrierOutcome.Drained.Instance);
                    return LocalDrainBarrierOutcome.Drained.Instance;
                }
            }

            // Single-pass raw BatchView sequential scan across all registered streams.
            _ = Interlocked.Increment(ref _sequentialScansStarted);
            var folds = new ILocalStreamFold[_processors.Length];
            ByteBudget.Lease? lease = null;
            try
            {
                for (var i = 0; i < _processors.Length; i++)
                {
                    folds[i] = _processors[i].BeginClosedLedgerFold(ledger, tail);
                }

                var segmentOrdinals = new Dictionary<ulong, int>();
                HashSet<ulong>? skippedOrdinals = SkipUnknownStreamMutant ? [] : null;
                for (var slot = Slot.Zero; slot < tail; slot += 1)
                {
                    var read = await _reader.ReadAsync(ledger, slot, ct).ConfigureAwait(false);
                    switch (read.Value)
                    {
                        case LedgerRead.Data data:
                            using (data)
                            {
                                ProcessBatch(ledger, slot, data, folds, segmentOrdinals, skippedOrdinals);
                            }

                            var totalMemory = SumMemoryBytes(folds);
                            var held = lease?.Bytes ?? 0;
                            if (totalMemory > held)
                            {
                                var delta = totalMemory - held;
                                if (lease is null)
                                {
                                    if (totalMemory <= _budget.Capacity)
                                    {
                                        lease = await _budget.AcquireAsync(totalMemory, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        // Single batch exceeded total budget: flush immediately while holding zero lease bytes.
                                        await FlushChunkAsync(ledger.Id, folds, ct).ConfigureAwait(false);
                                    }
                                }
                                else if (!lease.TryGrow(delta))
                                {
                                    // Deadlock-free chunked flush: commit, clear, and release the lease before continuing.
                                    await FlushChunkAsync(ledger.Id, folds, ct).ConfigureAwait(false);
                                    lease.Dispose();
                                    lease = null;
                                }
                            }
                            else if (totalMemory < held && lease is not null)
                            {
                                if (totalMemory <= 0)
                                {
                                    lease.Dispose();
                                    lease = null;
                                }
                                else
                                {
                                    lease.Shrink(held - totalMemory);
                                }
                            }

                            break;
                        case ReplicaWriteOutcome.Dropped:
                            if (await ConfirmDroppedAsync(ledger.Id, ct).ConfigureAwait(false))
                            {
                                OnBarrierCompleted?.Invoke(ledger.Id, LocalDrainBarrierOutcome.Dropped.Instance);
                                return LocalDrainBarrierOutcome.Dropped.Instance;
                            }

                            // The ledger is still listed; treat as backpressure so caller retries cleanly.
                            OnBarrierCompleted?.Invoke(ledger.Id, LocalDrainBarrierOutcome.Backpressure.Instance);
                            return LocalDrainBarrierOutcome.Backpressure.Instance;
                        case ReplicaWriteOutcome.Backpressure:
                            OnBarrierCompleted?.Invoke(ledger.Id, LocalDrainBarrierOutcome.Backpressure.Instance);
                            return LocalDrainBarrierOutcome.Backpressure.Instance;
                        case null:
                        default:
                            throw new UnreachableException("conventions.md §15: a ledger read answers a union which holds a case.");
                    }
                }

                if (MarkCompleteBeforeCommitMutant)
                {
                    MarkCompleted(ledger.Id);
                }

                await CommitAllAsync(ledger.Id, folds, isChunked: false, ct).ConfigureAwait(false);
                if (!MarkCompleteBeforeCommitMutant)
                {
                    MarkCompleted(ledger.Id);
                }

                OnBarrierCompleted?.Invoke(ledger.Id, LocalDrainBarrierOutcome.Drained.Instance);
                return LocalDrainBarrierOutcome.Drained.Instance;
            }
            finally
            {
                lease?.Dispose();
                for (var i = 0; i < folds.Length; i++)
                {
                    folds[i]?.Dispose();
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_inFlight.TryGetValue(ledger.Id, out var current) && ReferenceEquals(current, shared))
                {
                    _ = _inFlight.Remove(ledger.Id);
                }
            }

            shared.Complete();
        }
    }

    private void ProcessBatch(
        LedgerMetadata ledger,
        Slot slot,
        LedgerRead.Data data,
        ILocalStreamFold[] folds,
        Dictionary<ulong, int> segmentOrdinals,
        HashSet<ulong>? skippedOrdinals)
    {
        var view = data.View;
        if (view.IsSegmentStart)
        {
            segmentOrdinals.Clear();
            skippedOrdinals?.Clear();
        }

        foreach (var row in view)
        {
            if (row.Kind != RowKind.LocalAppend)
            {
                if (!row.IsDefining && segmentOrdinals.ContainsKey(row.Ordinal))
                {
                    throw new ArchiveCorruptException(
                        string.Create(CultureInfo.InvariantCulture, $"Ledger {ledger.Id} slot {slot} row {row.Index} ({row.Kind}) references a LedgerLocalKey ordinal {row.Ordinal} (LG-I15)."));
                }

                continue;
            }

            int processorIndex;
            if (row.IsDefining)
            {
                if (LedgerLocalKey.Validate(row.Key, ledger.Range, ledger.Id, out var streamSpan) is { } error)
                {
                    throw new ArchiveCorruptException(
                        string.Create(CultureInfo.InvariantCulture, $"Ledger {ledger.Id} slot {slot} row {row.Index} has a malformed RowKind.LocalAppend key: {error.Message}"),
                        error);
                }

                if (!TryGetProcessorIndex(streamSpan, out processorIndex))
                {
                    if (SkipUnknownStreamMutant)
                    {
                        _ = skippedOrdinals!.Add(row.Ordinal);
                        continue;
                    }

                    throw new ArchiveCorruptException(
                        string.Create(CultureInfo.InvariantCulture, $"Ledger {ledger.Id} slot {slot} row {row.Index} names unregistered local stream '{Encoding.ASCII.GetString(streamSpan)}' (LG-I15)."));
                }

                segmentOrdinals[row.Ordinal] = processorIndex;
            }
            else if (!segmentOrdinals.TryGetValue(row.Ordinal, out processorIndex))
            {
                if (SkipUnknownStreamMutant && skippedOrdinals!.Contains(row.Ordinal))
                {
                    continue;
                }

                throw new ArchiveCorruptException(
                    string.Create(CultureInfo.InvariantCulture, $"Ledger {ledger.Id} slot {slot} row {row.Index} (LocalAppend) references ordinal {row.Ordinal} not defined as a LocalAppend key in this segment (LG-I15)."));
            }

            folds[processorIndex].Add(slot, row.Index, row.Body);
        }
    }

    private async Task FlushChunkAsync(LedgerId ledger, ILocalStreamFold[] folds, CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _chunkedFlushes);
        await CommitAllAsync(ledger, folds, isChunked: true, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < folds.Length; i++)
        {
            folds[i].Clear();
        }
    }

    private async Task CommitAllAsync(LedgerId ledger, ILocalStreamFold[] folds, bool isChunked, CancellationToken cancellationToken)
    {
        for (var i = 0; i < folds.Length; i++)
        {
            await folds[i].CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        OnFlushCommitted?.Invoke(ledger, isChunked);
    }

    private async ValueTask<bool> ConfirmDroppedAsync(LedgerId ledger, CancellationToken cancellationToken)
    {
        if (_map.Current.Get(ledger) is null)
        {
            return true;
        }

        var refreshed = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
        return refreshed.Get(ledger) is null;
    }

    private static long SumMemoryBytes(ILocalStreamFold[] folds)
    {
        long total = 0;
        for (var i = 0; i < folds.Length; i++)
        {
            var bytes = folds[i].MemoryBytes;
            if (bytes > 0)
            {
                total = checked(total + bytes);
            }
        }

        return total;
    }

    private bool TryGetProcessorIndex(ReadOnlySpan<byte> streamNameBytes, out int index)
    {
        for (var i = 0; i < _processors.Length; i++)
        {
            if (_processors[i].Stream.Span.SequenceEqual(streamNameBytes))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private void MarkCompleted(LedgerId ledger)
    {
        lock (_sync)
        {
            _ = _completed.Add(ledger);
        }
    }

    private void PruneLocked(LedgerMapValue map)
    {
        if (map.Epoch <= _prunedAt)
        {
            return;
        }

        _prunedAt = map.Epoch;
        if (_completed.Count == 0)
        {
            return;
        }

        foreach (var id in _completed.ToArray())
        {
            if (id < map.NextLedgerId && map.Get(id) is null)
            {
                _ = _completed.Remove(id);
            }
        }
    }

    /// <summary>Tracks one shared in-flight closed-ledger drain with reference-counted cancellation across joined callers.</summary>
    private sealed class SharedDrain
    {
        private readonly Lock _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private int _waiters = 1;
        private bool _completed;

        public CancellationToken Token => _cts.Token;

        public Task<LocalDrainBarrierOutcome> Task { get; private set; } = null!;

        public void Start(Task<LocalDrainBarrierOutcome> task) => Task = task;

        public void AddWaiter()
        {
            lock (_lock)
            {
                _waiters++;
            }
        }

        public void RemoveWaiter()
        {
            var cancel = false;
            var dispose = false;
            lock (_lock)
            {
                if (--_waiters == 0)
                {
                    if (_completed)
                    {
                        dispose = true;
                    }
                    else
                    {
                        cancel = true;
                    }
                }
            }

            if (cancel)
            {
                _cts.Cancel();
            }
            else if (dispose)
            {
                _cts.Dispose();
            }
        }

        public void Complete()
        {
            var dispose = false;
            lock (_lock)
            {
                _completed = true;
                if (_waiters == 0)
                {
                    dispose = true;
                }
            }

            if (dispose)
            {
                _cts.Dispose();
            }
        }
    }
}

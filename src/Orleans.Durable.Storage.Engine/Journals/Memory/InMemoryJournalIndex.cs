using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals.Memory;

/// <summary>
/// An in-memory <see cref="IJournalIndex"/> for unit tests and deterministic Clockwork simulations.
/// </summary>
/// <remarks>
/// Faithfully models WAL-less RocksDB with atomic multi-column-family flush: <see cref="ApplyBatchAsync"/> and
/// <see cref="DropLedgersAsync"/> mutate only the active memtable overlay, <see cref="FlushAsync"/> atomically
/// promotes the overlay into durable state, and <see cref="CloseAsync"/> discards unflushed memtable state so closing
/// without flushing simulates a crash.
/// </remarks>
public sealed class InMemoryJournalIndex : IJournalIndex
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(LedgerId Ledger, JournalKeyHash KeyHash), SortedDictionary<(Slot Slot, int Index), (Slot DefiningSlot, ulong Ordinal)>> _durableKeyIndex = [];
    private readonly Dictionary<LedgerId, Dictionary<JournalKeyHash, JournalIndexTombstone>> _durableTombstones = [];
    private readonly SortedDictionary<LedgerId, Slot> _durableWatermarks = [];

    private Dictionary<(LedgerId Ledger, JournalKeyHash KeyHash), SortedDictionary<(Slot Slot, int Index), (Slot DefiningSlot, ulong Ordinal)>> _liveKeyIndex = [];
    private Dictionary<LedgerId, Dictionary<JournalKeyHash, JournalIndexTombstone>> _liveTombstones = [];
    private SortedDictionary<LedgerId, Slot> _liveWatermarks = [];
    private bool _open;
    private bool _corrupt;
    private TaskCompletionSource? _paused;

    /// <summary>Gets the number of completed atomic flushes.</summary>
    public int Flushes { get => Volatile.Read(ref field); private set; }

    /// <summary>Holds every later <see cref="FlushAsync"/> until <see cref="ResumeFlushes"/> or cancellation.</summary>
    public void PauseFlushes()
    {
        lock (_lock)
        {
            _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Releases flushes held by <see cref="PauseFlushes"/>.</summary>
    public void ResumeFlushes()
    {
        TaskCompletionSource? paused;
        lock (_lock)
        {
            paused = _paused;
            _paused = null;
        }

        _ = paused?.TrySetResult();
    }

    /// <summary>Makes every later operation report <see cref="JournalIndexCorruptException"/> until reset.</summary>
    public void Corrupt()
    {
        lock (_lock)
        {
            _corrupt = true;
        }
    }

    /// <summary>Returns a snapshot of the durable per-ledger watermarks.</summary>
    public IReadOnlyDictionary<LedgerId, Slot> GetDurableWatermarks()
    {
        lock (_lock)
        {
            return new SortedDictionary<LedgerId, Slot>(_durableWatermarks);
        }
    }

    /// <inheritdoc/>
    public ValueTask<SortedDictionary<LedgerId, Slot>> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_corrupt)
            {
                return ValueTask.FromException<SortedDictionary<LedgerId, Slot>>(Corruption());
            }

            if (_open)
            {
                return ValueTask.FromException<SortedDictionary<LedgerId, Slot>>(
                    new JournalIndexLockHeldException("The in-memory journal index is already open."));
            }

            // Discard any stale memtable state and initialize live view from the last atomic flush's durable state.
            CloneInto(_durableKeyIndex, _durableTombstones, _durableWatermarks, out _liveKeyIndex, out _liveTombstones, out _liveWatermarks);
            _open = true;
            return ValueTask.FromResult(new SortedDictionary<LedgerId, Slot>(_durableWatermarks));
        }
    }

    /// <inheritdoc/>
    public ValueTask ApplyBatchAsync(in JournalIndexBatch batch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ledger = batch.Ledger;
        var nextWatermark = batch.NextWatermark;
        var keyWrites = batch.KeyWrites.Span;
        var tombstoneWrites = batch.TombstoneWrites.Span;
        lock (_lock)
        {
            if (_corrupt)
            {
                return ValueTask.FromException(Corruption());
            }

            EnsureOpen();
            for (var index = 0; index < keyWrites.Length; index++)
            {
                var entry = keyWrites[index];
                var key = (ledger, entry.KeyHash);
                if (!_liveKeyIndex.TryGetValue(key, out var entries))
                {
                    entries = [];
                    _liveKeyIndex.Add(key, entries);
                }

                entries[(entry.Slot, entry.Index)] = (entry.DefiningSlot, entry.Ordinal);
            }

            for (var index = 0; index < tombstoneWrites.Length; index++)
            {
                var op = tombstoneWrites[index];
                if (op.IsDelete)
                {
                    if (_liveTombstones.TryGetValue(ledger, out var ledgerTombstones))
                    {
                        _ = ledgerTombstones.Remove(op.KeyHash);
                        if (ledgerTombstones.Count == 0)
                        {
                            _ = _liveTombstones.Remove(ledger);
                        }
                    }
                }
                else
                {
                    if (!_liveTombstones.TryGetValue(ledger, out var ledgerTombstones))
                    {
                        ledgerTombstones = [];
                        _liveTombstones.Add(ledger, ledgerTombstones);
                    }

                    // Copy raw key bytes so pooled buffers returned by the caller cannot alias stored state.
                    var rawKeyCopy = op.RawKey.ToArray();
                    ledgerTombstones[op.KeyHash] = new JournalIndexTombstone(
                        op.KeyHash,
                        op.DeletionTicks,
                        op.DeleteSlot,
                        op.DeleteIndex,
                        rawKeyCopy);
                }
            }

            _liveWatermarks[ledger] = nextWatermark;
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask DropLedgersAsync(IReadOnlyList<LedgerId> ledgers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledgers);
        cancellationToken.ThrowIfCancellationRequested();
        if (ledgers.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_lock)
        {
            if (_corrupt)
            {
                return ValueTask.FromException(Corruption());
            }

            EnsureOpen();
            var dropSet = ledgers.Count == 1 ? null : new HashSet<LedgerId>(ledgers);
            var single = ledgers[0];
            List<(LedgerId, JournalKeyHash)>? keysToRemove = null;
            foreach (var key in _liveKeyIndex.Keys)
            {
                var matches = dropSet is null ? key.Ledger == single : dropSet.Contains(key.Ledger);
                if (matches)
                {
                    (keysToRemove ??= []).Add(key);
                }
            }

            if (keysToRemove is not null)
            {
                for (var index = 0; index < keysToRemove.Count; index++)
                {
                    _ = _liveKeyIndex.Remove(keysToRemove[index]);
                }
            }

            for (var index = 0; index < ledgers.Count; index++)
            {
                var ledger = ledgers[index];
                _ = _liveTombstones.Remove(ledger);
                _ = _liveWatermarks.Remove(ledger);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask<List<JournalIndexKeyEntry>> GetKeyEntriesAsync(
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot startSlot,
        int startIndex,
        Slot exclusiveEndSlot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_corrupt)
            {
                return ValueTask.FromException<List<JournalIndexKeyEntry>>(Corruption());
            }

            EnsureOpen();
            var results = new List<JournalIndexKeyEntry>();
            if (startSlot >= exclusiveEndSlot
                || !_liveKeyIndex.TryGetValue((ledger, keyHash), out var entries))
            {
                return ValueTask.FromResult(results);
            }

            foreach (var ((slot, idx), (definingSlot, ordinal)) in entries)
            {
                if (slot < startSlot || slot == startSlot && idx < startIndex)
                {
                    continue;
                }

                if (slot >= exclusiveEndSlot)
                {
                    break;
                }

                results.Add(new JournalIndexKeyEntry(slot, idx, definingSlot, ordinal));
            }

            return ValueTask.FromResult(results);
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> HasKeyEntryAsync(
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot startSlot,
        int startIndex,
        Slot exclusiveEndSlot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_corrupt)
            {
                return ValueTask.FromException<bool>(Corruption());
            }

            EnsureOpen();
            if (startSlot >= exclusiveEndSlot
                || !_liveKeyIndex.TryGetValue((ledger, keyHash), out var entries))
            {
                return ValueTask.FromResult(false);
            }

            foreach (var (slot, idx) in entries.Keys)
            {
                if (slot < startSlot || slot == startSlot && idx < startIndex)
                {
                    continue;
                }

                return ValueTask.FromResult(slot < exclusiveEndSlot);
            }

            return ValueTask.FromResult(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask<List<JournalIndexTombstone>> ScanTombstonesAsync(
        LedgerId ledger,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_corrupt)
            {
                return ValueTask.FromException<List<JournalIndexTombstone>>(Corruption());
            }

            EnsureOpen();
            if (!_liveTombstones.TryGetValue(ledger, out var ledgerTombstones) || ledgerTombstones.Count == 0)
            {
                return ValueTask.FromResult(new List<JournalIndexTombstone>());
            }

            var results = new List<JournalIndexTombstone>(ledgerTombstones.Count);
            foreach (var tombstone in ledgerTombstones.Values)
            {
                results.Add(tombstone);
            }

            return ValueTask.FromResult(results);
        }
    }

    /// <inheritdoc/>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        Task? paused;
        lock (_lock)
        {
            paused = _paused?.Task;
        }

        if (paused is not null)
        {
            await paused.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_corrupt)
            {
                throw Corruption();
            }

            EnsureOpen();
            // Atomically commit live key_index, tombstones, and watermarks to durable state.
            CloneInto(_liveKeyIndex, _liveTombstones, _liveWatermarks, out var durableKeys, out var durableTombstones, out var durableWatermarks);
            _durableKeyIndex.Clear();
            foreach (var (key, value) in durableKeys)
            {
                _durableKeyIndex.Add(key, value);
            }

            _durableTombstones.Clear();
            foreach (var (key, value) in durableTombstones)
            {
                _durableTombstones.Add(key, value);
            }

            _durableWatermarks.Clear();
            foreach (var (key, value) in durableWatermarks)
            {
                _durableWatermarks.Add(key, value);
            }

            Flushes++;
        }
    }

    /// <inheritdoc/>
    public ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _durableKeyIndex.Clear();
            _durableTombstones.Clear();
            _durableWatermarks.Clear();
            _liveKeyIndex.Clear();
            _liveTombstones.Clear();
            _liveWatermarks.Clear();
            _corrupt = false;
            _open = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_corrupt)
            {
                return ValueTask.FromException(Corruption());
            }

            EnsureOpen();
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask CloseAsync()
    {
        lock (_lock)
        {
            _liveKeyIndex.Clear();
            _liveTombstones.Clear();
            _liveWatermarks.Clear();
            _open = false;
            return ValueTask.CompletedTask;
        }
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(!_open, this);

    private static JournalIndexCorruptException Corruption() =>
        new("The in-memory journal index was marked corrupt.");

    private static void CloneInto(
        Dictionary<(LedgerId Ledger, JournalKeyHash KeyHash), SortedDictionary<(Slot Slot, int Index), (Slot DefiningSlot, ulong Ordinal)>> sourceKeys,
        Dictionary<LedgerId, Dictionary<JournalKeyHash, JournalIndexTombstone>> sourceTombstones,
        SortedDictionary<LedgerId, Slot> sourceWatermarks,
        out Dictionary<(LedgerId Ledger, JournalKeyHash KeyHash), SortedDictionary<(Slot Slot, int Index), (Slot DefiningSlot, ulong Ordinal)>> destKeys,
        out Dictionary<LedgerId, Dictionary<JournalKeyHash, JournalIndexTombstone>> destTombstones,
        out SortedDictionary<LedgerId, Slot> destWatermarks)
    {
        destKeys = [with(sourceKeys.Count)];
        foreach (var (key, entries) in sourceKeys)
        {
            destKeys.Add(key, [with(entries)]);
        }

        destTombstones = [with(sourceTombstones.Count)];
        foreach (var (ledger, tombstones) in sourceTombstones)
        {
            destTombstones.Add(ledger, [with(tombstones)]);
        }

        destWatermarks = [with(sourceWatermarks)];
    }
}

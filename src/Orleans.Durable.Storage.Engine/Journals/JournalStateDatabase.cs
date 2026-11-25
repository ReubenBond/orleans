using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// One node's derived journal index over <see cref="IJournalIndex"/> (journals.md §8.3): per-live-ledger key indexes
/// and tombstone expiry indexes, used by both <see cref="JournalStore"/> and <see cref="ArchiveReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// For every live ledger the node replicates, <see cref="Applier"/> plugs into <see cref="LedgerApplyLoop"/>, writing
/// each batch's key-index entries, tombstone updates, and ledger watermark (<c>slot + 1</c>) atomically to
/// <see cref="IJournalIndex"/> and dropping the ledger's ranges and watermark once the map drops it. For a closed live
/// predecessor the node does not replicate, one shared <see cref="LedgerReader"/> scan builds its index to <c>T</c>,
/// kept until the ledger drops. Every lookup waits until the ledger's index has caught up to its requested decided slot
/// before answering (JS-I9).
/// </para>
/// <para>
/// Writes land directly in the index's memtables without a separate index WAL; periodic and clean-shutdown flushes
/// invoke an atomic multi-column-family flush so key-index entries, tombstones, and watermarks advance together
/// durably. On crash, any unflushed suffix is replayed from local <c>LedgerStore</c> starting at each ledger's
/// durable watermark.
/// </para>
/// </remarks>
public sealed class JournalStateDatabase : IAsyncDisposable
{
    private const int PredecessorMaxConcurrency = 8;
    private const int PredecessorCommitMaxRows = 1_024;
    private const int PredecessorCommitMaxBytes = 1024 * 1024;

    private static readonly TimeSpan PredecessorBaseDelay = LedgerApplyLoop.LearnInterval;
    private static readonly TimeSpan PredecessorMaximumDelay = TimeSpan.FromSeconds(1);

    private readonly IJournalIndex _index;
    private readonly LedgerReader _reader;
    private readonly JournalIndexOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly NodeId _node;
    private readonly AdmissionGate _gate = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly SemaphoreSlim _predecessorWindow = new(PredecessorMaxConcurrency, PredecessorMaxConcurrency);
    private readonly Lock _lock = new();
    private readonly Dictionary<LedgerId, Slot> _appliedWatermarks;
    private readonly Dictionary<LedgerId, Slot> _durableWatermarks;
    private readonly Dictionary<LedgerId, PriorityQueue<CatchUpWaiter, long>> _waiters = [];
    private readonly Dictionary<LedgerId, PredecessorScan> _predecessorScans = [];
    private readonly HashSet<LedgerId> _nonReplicated = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _watching;
    private readonly Task _flushing;
    private readonly Task _checking;
    private LedgerApplyLoop? _applyLoop;
    private Exception? _backgroundFault;
    private JournalIndexCorruptException? _corruptException;
    private long _writeVersion;
    private bool _dirty;
    private int _predecessorScansStarted;

    private JournalStateDatabase(
        IJournalIndex index,
        LedgerReader reader,
        JournalIndexOptions options,
        TimeProvider timeProvider,
        NodeId node,
        LedgerMapValue initialMap,
        SortedDictionary<LedgerId, Slot> initialWatermarks)
    {
        _index = index;
        _reader = reader;
        _options = options;
        _timeProvider = timeProvider;
        _node = node;
        _appliedWatermarks = [with(initialWatermarks)];
        _durableWatermarks = [with(initialWatermarks)];

        Applier = new LedgerApplier
        {
            NextSlotAsync = NextSlotAsync,
            ApplyAsync = ApplyAsync,
            RemoveAsync = RemoveAsync,
        };
        KeyIndex = new ArchiveKeyIndex(LookupAsync);

        foreach (var id in initialWatermarks.Keys)
        {
            if (initialMap.Get(id) is not { } listed || !listed.IsOnNode(_node))
            {
                _ = _nonReplicated.Add(id);
            }
        }

        // Background loops serve no caller, so they run in none's execution context (conventions.md §12).
        using (ExecutionContext.SuppressFlow())
        {
            _watching = Task.Run(() => WatchMapAsync(initialMap), CancellationToken.None);
            _flushing = options.FlushInterval != Timeout.InfiniteTimeSpan
                ? Task.Run(FlushLoopAsync, CancellationToken.None)
                : Task.CompletedTask;
            _checking = options.QuickCheckInterval != Timeout.InfiniteTimeSpan
                ? Task.Run(CheckLoopAsync, CancellationToken.None)
                : Task.CompletedTask;
        }
    }

    /// <summary>Gets the <see cref="LedgerApplier"/> to pass to this node's <see cref="LedgerApplyLoop"/>.</summary>
    public LedgerApplier Applier { get; }

    /// <summary>Gets the <see cref="ArchiveKeyIndex"/> to pass to this node's <see cref="ArchiveReader"/>.</summary>
    public ArchiveKeyIndex KeyIndex { get; }

    /// <summary>Gets a value indicating whether the underlying index has been marked corrupt.</summary>
    public bool IsCorrupt
    {
        get
        {
            lock (_lock)
            {
                return _corruptException is not null;
            }
        }
    }

    /// <summary>Gets the underlying journal index; for tests.</summary>
    internal IJournalIndex Index => _index;

    /// <summary>Gets how many shared scans for non-replicated closed predecessors this database started; for tests.</summary>
    internal int PredecessorScansStarted => Volatile.Read(ref _predecessorScansStarted);

    /// <summary>
    /// Gets or sets a value indicating whether lookups skip waiting for catch-up (JS-I9): the mutant that counts an
    /// index before it has caught up to the requested decided end, which tests set to verify they catch it.
    /// </summary>
    internal bool SkipCatchUpMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether indexed entries shift their recorded ordinal by one, creating stale
    /// ordinal entries so tests can verify <c>unknown-ordinal</c> rejection.
    /// </summary>
    internal bool StaleOrdinalMutant { get; set; }

    /// <summary>
    /// Opens a <see cref="JournalStateDatabase"/> over <paramref name="index"/>, resetting a corrupt index and cleaning
    /// up any persisted ledger watermarks the map already no longer lists.
    /// </summary>
    /// <param name="index">The node's derived journal index implementation.</param>
    /// <param name="reader">The node's ledger reader, whose local node is the node.</param>
    /// <param name="options">Optional index flush and integrity-check options.</param>
    /// <param name="timeProvider">Clock for periodic flush, integrity check, and predecessor-scan backoff; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>The opened <see cref="JournalStateDatabase"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="reader"/> names no local node.</exception>
    public static async ValueTask<JournalStateDatabase> OpenAsync(
        IJournalIndex index,
        LedgerReader reader,
        JournalIndexOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(reader);
        var node = reader.LocalNode ?? throw new ArgumentException("A node's JournalStateDatabase requires a LedgerReader naming its local node.", nameof(reader));
        var effectiveOptions = options ?? new JournalIndexOptions();
        effectiveOptions.Validate();
        var clock = timeProvider ?? TimeProvider.System;

        SortedDictionary<LedgerId, Slot> watermarks;
        try
        {
            watermarks = await index.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JournalIndexCorruptException)
        {
            // A corrupt derived journal index is discarded and rebuilt from the live ledgers (journals.md §8.3).
            await index.ResetAsync(cancellationToken).ConfigureAwait(false);
            watermarks = [];
        }

        try
        {
            var map = reader.Map.Current;
            var dropped = new List<LedgerId>();
            foreach (var id in watermarks.Keys)
            {
                if (map.NoLongerLists(id))
                {
                    dropped.Add(id);
                }
            }

            if (dropped.Count > 0)
            {
                await index.DropLedgersAsync(dropped, cancellationToken).ConfigureAwait(false);
                await index.FlushAsync(cancellationToken).ConfigureAwait(false);
                foreach (var id in dropped)
                {
                    _ = watermarks.Remove(id);
                }
            }

            return new JournalStateDatabase(index, reader, effectiveOptions, clock, node, map, watermarks);
        }
        catch
        {
            await index.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Attaches the node's <see cref="LedgerApplyLoop"/> so catch-up waits wake an open ledger's delivery at once and
    /// observe loop completion or faults instead of waiting indefinitely.
    /// </summary>
    /// <param name="applyLoop">The apply loop driving <see cref="Applier"/>.</param>
    /// <exception cref="InvalidOperationException">A <see cref="LedgerApplyLoop"/> is already attached.</exception>
    public void Attach(LedgerApplyLoop applyLoop)
    {
        ArgumentNullException.ThrowIfNull(applyLoop);
        using var admission = _gate.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);
        LedgerId[]? waiting;
        lock (_lock)
        {
            if (_applyLoop is not null)
            {
                throw new InvalidOperationException("A LedgerApplyLoop is already attached to this JournalStateDatabase.");
            }

            _applyLoop = applyLoop;
            waiting = _waiters.Count > 0 ? [.. _waiters.Keys] : null;
        }

        if (waiting is not null)
        {
            foreach (var ledger in waiting)
            {
                applyLoop.Wake(ledger);
            }
        }

        _ = applyLoop.Completion.ContinueWith(
            static (completed, state) =>
            {
                if (state is JournalStateDatabase db)
                {
                    Exception failure = completed.IsFaulted
                        ? completed.Exception?.InnerException ?? (Exception?)completed.Exception ?? new InvalidOperationException("The attached LedgerApplyLoop faulted.")
                        : new ObjectDisposedException(nameof(LedgerApplyLoop), "The attached LedgerApplyLoop stopped before catch-up completed.");
                    db.OnBackgroundFault(failure);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Gets the applied watermark (next slot to apply) for <paramref name="ledger"/>.</summary>
    public Slot Watermark(LedgerId ledger)
    {
        lock (_lock)
        {
            return _appliedWatermarks.TryGetValue(ledger, out var slot) ? slot : Slot.Zero;
        }
    }

    /// <summary>Gets the durable watermark for <paramref name="ledger"/>.</summary>
    public Slot DurableWatermark(LedgerId ledger)
    {
        lock (_lock)
        {
            return _durableWatermarks.TryGetValue(ledger, out var slot) ? slot : Slot.Zero;
        }
    }

    /// <summary>Returns whether <paramref name="ledger"/> currently has an active watermark entry in this index.</summary>
    internal bool HasLedger(LedgerId ledger)
    {
        lock (_lock)
        {
            return _appliedWatermarks.ContainsKey(ledger);
        }
    }

    /// <summary>
    /// Atomically flushes every applied change in the index's memtables across all column families to durable storage.
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        using var admission = _gate.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);
        ThrowIfCorrupt();

        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCorrupt();
            Dictionary<LedgerId, Slot> snapshot;
            long versionBefore;
            lock (_lock)
            {
                snapshot = [with(_appliedWatermarks)];
                versionBefore = _writeVersion;
            }

            try
            {
                await _index.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (JournalIndexCorruptException corrupt)
            {
                MarkCorrupt(corrupt);
                throw;
            }

            lock (_lock)
            {
                _durableWatermarks.Clear();
                foreach (var (id, slot) in snapshot)
                {
                    _durableWatermarks[id] = slot;
                }

                if (_writeVersion == versionBefore)
                {
                    _dirty = false;
                }
            }
        }
        finally
        {
            _ = _flushLock.Release();
        }
    }

    /// <summary>
    /// Collects cold candidate keys from the caught-up tombstone indexes of <paramref name="openLedgers"/> and their
    /// distinct overlapping live closed predecessors (journals.md §8.6, JS-I7, JS-I9): scans each open ledger and each
    /// distinct overlapping closed predecessor once, skipping keys already warm or seen in a newer ledger and
    /// suppressing same-ledger and cross-ledger resurrections via the key index.
    /// </summary>
    internal async ValueTask CollectExpiredTombstonesAsync(
        IReadOnlyList<(LedgerMetadata Ledger, Slot Below)> openLedgers,
        LedgerMapValue map,
        long cutoffTicks,
        Func<JournalRowKey, bool> isWarm,
        List<JournalRowKey> coldCandidates,
        CancellationToken cancellationToken)
    {
        using var admission = _gate.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);
        ThrowIfCorrupt();
        if (openLedgers.Count == 0)
        {
            return;
        }

        // Deduplicate closed predecessors across all open ledgers served on this node so shared parents are scanned once.
        var closedPredecessors = CollectDistinctClosedPredecessors(openLedgers, map);

        // Wait for catch-up on every indexed open ledger and closed predecessor before scanning tombstones (JS-I9).
        var indexedOpen = new List<(LedgerMetadata Ledger, Slot Below)>(openLedgers.Count);
        for (var index = 0; index < openLedgers.Count; index++)
        {
            var open = openLedgers[index];
            if (open.Below == Slot.Zero || !open.Ledger.IsOnNode(_node))
            {
                continue;
            }

            if (SkipCatchUpMutant || await WaitForReplicatedCatchUpAsync(open.Ledger, open.Below, cancellationToken).ConfigureAwait(false))
            {
                indexedOpen.Add(open);
            }
        }

        var indexedClosed = new List<(LedgerMetadata Ledger, Slot End)>(closedPredecessors.Count);
        for (var index = 0; index < closedPredecessors.Count; index++)
        {
            var closed = closedPredecessors[index];
            if (closed.Status.Value is not LedgerStatus.Closed closedStatus || closedStatus.T == Slot.Zero)
            {
                continue;
            }

            var ready = SkipCatchUpMutant
                || (closed.IsOnNode(_node)
                    ? await WaitForReplicatedCatchUpAsync(closed, closedStatus.T, cancellationToken).ConfigureAwait(false)
                    : await WaitForPredecessorScanAsync(closed, closedStatus.T, cancellationToken).ConfigureAwait(false));
            if (ready)
            {
                indexedClosed.Add((closed, closedStatus.T));
            }
        }

        var seenKeys = new HashSet<JournalRowKey>();
        for (var index = 0; index < indexedOpen.Count; index++)
        {
            var (ledger, below) = indexedOpen[index];
            await CollectOpenLedgerTombstonesAsync(ledger, below, cutoffTicks, isWarm, seenKeys, coldCandidates, cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < indexedClosed.Count; index++)
        {
            var (closed, end) = indexedClosed[index];
            await CollectClosedPredecessorTombstonesAsync(
                closed,
                end,
                openLedgers,
                indexedOpen,
                indexedClosed,
                newerClosedCount: index,
                cutoffTicks,
                isWarm,
                seenKeys,
                coldCandidates,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops background loops and any in-flight predecessor scans, wakes pending catch-up waiters, drains admitted
    /// callers, and closes the underlying journal index without flushing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task drained;
        PredecessorScan[] scans;
        lock (_lock)
        {
            if (_gate.IsClosed)
            {
                return;
            }

            drained = _gate.CloseAsync();
            scans = [.. _predecessorScans.Values];
            _predecessorScans.Clear();
        }

        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            SignalAllWaiters();

            foreach (var scan in scans)
            {
                await scan.StopAsync(dropped: false).ConfigureAwait(false);
            }

            await AwaitBackgroundTaskAsync(_watching).ConfigureAwait(false);
            await AwaitBackgroundTaskAsync(_flushing).ConfigureAwait(false);
            await AwaitBackgroundTaskAsync(_checking).ConfigureAwait(false);

            await drained.ConfigureAwait(false);
        }
        finally
        {
            _predecessorWindow.Dispose();
            _flushLock.Dispose();
            _writes.Dispose();
            _stop.Dispose();
            await _index.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task AwaitBackgroundTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    private static bool IsTransient(Exception failure) =>
        failure is AggregateException { InnerExceptions: var errors } && errors.All(static error => LedgerReplicaFailures.IsTransient(error, CancellationToken.None));

    private void ThrowIfCorrupt()
    {
        lock (_lock)
        {
            if (_corruptException is { } corrupt)
            {
                throw new JournalIndexCorruptException(corrupt.Message, corrupt);
            }
        }
    }

    private void MarkCorrupt(JournalIndexCorruptException corrupt)
    {
        lock (_lock)
        {
            _corruptException ??= corrupt;
        }

        OnBackgroundFault(corrupt);
    }

    private ValueTask<Slot> NextSlotAsync(LedgerId ledger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var admission = _gate.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);
        ThrowIfCorrupt();
        return ValueTask.FromResult(Watermark(ledger));
    }

    private ValueTask ApplyAsync(LedgerId ledger, LedgerScanItem item, CancellationToken cancellationToken)
    {
        var admission = _gate.TryEnter();
        if (!admission.Entered)
        {
            throw new ObjectDisposedException(nameof(JournalStateDatabase));
        }

        if (!item.Read.TryGetValue(out LedgerRead.Data data))
        {
            admission.Dispose();
            throw new UnreachableException("LedgerApplyLoop only delivers Data items to ApplyAsync.");
        }

        PendingBatch batch;
        try
        {
            batch = MaterializeBatch(ledger, data.Slot, data, item);
        }
        catch
        {
            admission.Dispose();
            throw;
        }

        return CommitAndReleaseAsync(ledger, data.Slot, batch, admission, cancellationToken);

        async ValueTask CommitAndReleaseAsync(LedgerId id, Slot slot, PendingBatch pending, AdmissionGate.Admission token, CancellationToken ct)
        {
            using (token)
            using (pending)
            {
                await CommitBatchAsync(id, slot, pending, signalWaiters: true, ct).ConfigureAwait(false);
            }
        }
    }

    private static void GrowArray<T>(ref T[] array, int used, int needed)
    {
        var grown = ArrayPool<T>.Shared.Rent(Math.Max(array.Length * 2, needed));
        array.AsSpan(0, used).CopyTo(grown);
        ArrayPool<T>.Shared.Return(array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        array = grown;
    }

    /// <summary>
    /// Materializes a scanned batch's key-index writes and tombstone updates synchronously into pooled arrays before
    /// awaiting the write lock, because <see cref="LedgerScanItem.Rows"/> is a <see langword="ref"/> struct that cannot
    /// cross an <see langword="await"/>.
    /// </summary>
    private PendingBatch MaterializeBatch(LedgerId ledger, Slot slot, in LedgerRead.Data data, LedgerScanItem item)
    {
        var capacity = BatchView.OfVerified(data.Batch).Count;
        if (capacity == 0)
        {
            return default;
        }

        var keyWrites = ArrayPool<JournalIndexKeyWrite>.Shared.Rent(capacity);
        JournalIndexTombstoneWrite[]? tombstoneWrites = null;
        byte[]? rawKeyBuffer = null;
        Dictionary<JournalKeyHash, int>? tombstonePositions = null;
        var keyCount = 0;
        var tombstoneCount = 0;
        var rawKeyUsed = 0;
        try
        {
            Span<byte> tombstoneBytes = stackalloc byte[JournalEntry.TombstoneLength];
            var shift = StaleOrdinalMutant ? 1UL : 0UL;
            foreach (var row in item.Rows)
            {
                if (!RowKinds.IsArchiveKind(row.Kind))
                {
                    // LG-I15: RowKind.Message rows carry no archive key and never enter key_index or tombstones.
                    continue;
                }

                Debug.Assert(row.Tag.Ledger == ledger && row.Tag.Slot == slot, "Resolved row matches its ledger and slot.");
                if (keyCount >= keyWrites.Length)
                {
                    GrowArray(ref keyWrites, keyCount, keyCount + 1);
                }

                var keyHash = JournalKeyHash.Compute(row.Key);
                keyWrites[keyCount++] = new JournalIndexKeyWrite(keyHash, row.Tag.Slot, row.Tag.Index, row.DefiningSlot, row.Ordinal + shift);

                if (row.Key.Length is <= 0 or > KeyRange.MaxKeyLength)
                {
                    continue;
                }

                if (row.Kind == RowKind.Delete)
                {
                    var isTombstone = false;
                    long deletionTicks = 0;
                    if (row.Body.Length == JournalEntry.TombstoneLength)
                    {
                        row.Body.CopyTo(tombstoneBytes);
                        var ticks = BinaryPrimitives.ReadInt64LittleEndian(tombstoneBytes);
                        if (ticks is >= 0 and <= JournalEntry.MaxTicks)
                        {
                            isTombstone = true;
                            deletionTicks = ticks;
                        }
                    }

                    tombstonePositions ??= [];
                    JournalIndexTombstoneWrite op;
                    if (isTombstone)
                    {
                        var neededRaw = rawKeyUsed + row.Key.Length;
                        if (rawKeyBuffer is null)
                        {
                            rawKeyBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, neededRaw));
                        }
                        else if (neededRaw > rawKeyBuffer.Length)
                        {
                            GrowArray(ref rawKeyBuffer, rawKeyUsed, neededRaw);
                        }

                        var rawOffset = rawKeyUsed;
                        row.Key.CopyTo(rawKeyBuffer.AsSpan(rawOffset, row.Key.Length));
                        rawKeyUsed = neededRaw;
                        op = JournalIndexTombstoneWrite.Put(
                            keyHash,
                            deletionTicks,
                            row.Tag.Slot,
                            row.Tag.Index,
                            rawKeyBuffer.AsMemory(rawOffset, row.Key.Length));
                    }
                    else
                    {
                        op = JournalIndexTombstoneWrite.Delete(keyHash);
                    }

                    if (tombstonePositions.TryGetValue(keyHash, out var existingIndex))
                    {
                        tombstoneWrites![existingIndex] = op;
                    }
                    else
                    {
                        if (tombstoneWrites is null)
                        {
                            tombstoneWrites = ArrayPool<JournalIndexTombstoneWrite>.Shared.Rent(4);
                        }
                        else if (tombstoneCount >= tombstoneWrites.Length)
                        {
                            GrowArray(ref tombstoneWrites, tombstoneCount, tombstoneCount + 1);
                        }

                        tombstonePositions[keyHash] = tombstoneCount;
                        tombstoneWrites[tombstoneCount++] = op;
                    }
                }
                else if (tombstonePositions is not null && tombstonePositions.TryGetValue(keyHash, out var existingIndex))
                {
                    // A later non-Delete row in the same batch supersedes an earlier Delete; delete any prior tombstone.
                    tombstoneWrites![existingIndex] = JournalIndexTombstoneWrite.Delete(keyHash);
                }
            }

            if (keyCount == 0)
            {
                ArrayPool<JournalIndexKeyWrite>.Shared.Return(keyWrites);
                if (tombstoneWrites is not null)
                {
                    ArrayPool<JournalIndexTombstoneWrite>.Shared.Return(tombstoneWrites, clearArray: true);
                }

                if (rawKeyBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rawKeyBuffer);
                }

                return default;
            }

            if (rawKeyBuffer is not null && tombstoneWrites is not null)
            {
                RebindRawKeySlices(tombstoneWrites.AsSpan(0, tombstoneCount), rawKeyBuffer);
            }

            return new PendingBatch(keyWrites, keyCount, tombstoneWrites, tombstoneCount, rawKeyBuffer, rawKeyUsed);
        }
        catch
        {
            ArrayPool<JournalIndexKeyWrite>.Shared.Return(keyWrites);
            if (tombstoneWrites is not null)
            {
                ArrayPool<JournalIndexTombstoneWrite>.Shared.Return(tombstoneWrites, clearArray: true);
            }

            if (rawKeyBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(rawKeyBuffer);
            }

            throw;
        }
    }

    private static void RebindRawKeySlices(Span<JournalIndexTombstoneWrite> writes, byte[] currentRawKeyBuffer)
    {
        for (var index = 0; index < writes.Length; index++)
        {
            var write = writes[index];
            if (write.IsDelete)
            {
                continue;
            }

            if (MemoryMarshal.TryGetArray(write.RawKey, out var segment)
                && !ReferenceEquals(segment.Array, currentRawKeyBuffer))
            {
                writes[index] = JournalIndexTombstoneWrite.Put(
                    write.KeyHash,
                    write.DeletionTicks,
                    write.DeleteSlot,
                    write.DeleteIndex,
                    currentRawKeyBuffer.AsMemory(segment.Offset, segment.Count));
            }
        }
    }

    private async ValueTask CommitBatchAsync(LedgerId ledger, Slot slot, PendingBatch entries, bool signalWaiters, CancellationToken cancellationToken)
    {
        ThrowIfCorrupt();
        var nextWatermark = slot + 1;
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCorrupt();
            if (Watermark(ledger) >= nextWatermark)
            {
                return;
            }

            var batch = new JournalIndexBatch(
                ledger,
                nextWatermark,
                entries.KeyWrites is { } keys ? keys.AsMemory(0, entries.KeyCount) : ReadOnlyMemory<JournalIndexKeyWrite>.Empty,
                entries.TombstoneWrites is { } tombs ? tombs.AsMemory(0, entries.TombstoneCount) : ReadOnlyMemory<JournalIndexTombstoneWrite>.Empty);

            try
            {
                await _index.ApplyBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (JournalIndexCorruptException corrupt)
            {
                MarkCorrupt(corrupt);
                throw;
            }

            lock (_lock)
            {
                _appliedWatermarks[ledger] = nextWatermark;
                _writeVersion++;
                _dirty = true;
            }
        }
        finally
        {
            _ = _writes.Release();
        }

        if (signalWaiters)
        {
            SignalLedger(ledger, nextWatermark.Value);
        }
    }

    private async ValueTask CommitBatchesAsync(LedgerId ledger, List<(Slot Slot, PendingBatch Batch)> batches, CancellationToken cancellationToken)
    {
        if (batches.Count == 0)
        {
            return;
        }

        ThrowIfCorrupt();
        var lastWatermark = batches[^1].Slot + 1;
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCorrupt();
            var currentWatermark = Watermark(ledger);
            if (currentWatermark >= lastWatermark)
            {
                return;
            }

            var totalKeys = 0;
            var totalTombs = 0;
            for (var index = 0; index < batches.Count; index++)
            {
                var (slot, batch) = batches[index];
                if (slot < currentWatermark)
                {
                    continue;
                }

                totalKeys += batch.KeyCount;
                totalTombs += batch.TombstoneCount;
            }

            JournalIndexKeyWrite[]? mergedKeys = totalKeys > 0 ? ArrayPool<JournalIndexKeyWrite>.Shared.Rent(totalKeys) : null;
            JournalIndexTombstoneWrite[]? mergedTombs = totalTombs > 0 ? ArrayPool<JournalIndexTombstoneWrite>.Shared.Rent(totalTombs) : null;
            try
            {
                var keyOffset = 0;
                var tombOffset = 0;
                for (var index = 0; index < batches.Count; index++)
                {
                    var (slot, batch) = batches[index];
                    if (slot < currentWatermark)
                    {
                        continue;
                    }

                    if (batch.KeyWrites is { } keys && batch.KeyCount > 0)
                    {
                        keys.AsSpan(0, batch.KeyCount).CopyTo(mergedKeys.AsSpan(keyOffset, batch.KeyCount));
                        keyOffset += batch.KeyCount;
                    }

                    if (batch.TombstoneWrites is { } tombs && batch.TombstoneCount > 0)
                    {
                        tombs.AsSpan(0, batch.TombstoneCount).CopyTo(mergedTombs.AsSpan(tombOffset, batch.TombstoneCount));
                        tombOffset += batch.TombstoneCount;
                    }
                }

                var combined = new JournalIndexBatch(
                    ledger,
                    lastWatermark,
                    mergedKeys is not null ? mergedKeys.AsMemory(0, keyOffset) : ReadOnlyMemory<JournalIndexKeyWrite>.Empty,
                    mergedTombs is not null ? mergedTombs.AsMemory(0, tombOffset) : ReadOnlyMemory<JournalIndexTombstoneWrite>.Empty);

                try
                {
                    await _index.ApplyBatchAsync(combined, cancellationToken).ConfigureAwait(false);
                }
                catch (JournalIndexCorruptException corrupt)
                {
                    MarkCorrupt(corrupt);
                    throw;
                }

                lock (_lock)
                {
                    _appliedWatermarks[ledger] = lastWatermark;
                    _writeVersion++;
                    _dirty = true;
                }
            }
            finally
            {
                if (mergedKeys is not null)
                {
                    ArrayPool<JournalIndexKeyWrite>.Shared.Return(mergedKeys);
                }

                if (mergedTombs is not null)
                {
                    ArrayPool<JournalIndexTombstoneWrite>.Shared.Return(mergedTombs, clearArray: true);
                }
            }
        }
        finally
        {
            _ = _writes.Release();
        }
    }

    private async ValueTask RemoveAsync(LedgerId ledger, CancellationToken cancellationToken)
    {
        using var admission = _gate.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);
        await RemoveLedgersCoreAsync([ledger], cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RemoveLedgersCoreAsync(LedgerId[] ledgers, CancellationToken cancellationToken)
    {
        if (ledgers.Length == 0)
        {
            return;
        }

        ThrowIfCorrupt();
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCorrupt();
            try
            {
                await _index.DropLedgersAsync(ledgers, cancellationToken).ConfigureAwait(false);
            }
            catch (JournalIndexCorruptException corrupt)
            {
                MarkCorrupt(corrupt);
                throw;
            }

            lock (_lock)
            {
                for (var index = 0; index < ledgers.Length; index++)
                {
                    _ = _appliedWatermarks.Remove(ledgers[index]);
                }

                _writeVersion++;
                _dirty = true;
            }
        }
        finally
        {
            _ = _writes.Release();
        }

        for (var index = 0; index < ledgers.Length; index++)
        {
            SignalDropped(ledgers[index]);
        }
    }

    private async ValueTask<KeyIndexRead> LookupAsync(LedgerMetadata ledger, Slot below, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        using var admission = _gate.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);
        ThrowIfCorrupt();
        if (below == Slot.Zero)
        {
            return new KeyIndexRead.Indexed([]);
        }

        if (ledger.IsOnNode(_node))
        {
            if (!SkipCatchUpMutant)
            {
                var caughtUp = await WaitForReplicatedCatchUpAsync(ledger, below, cancellationToken).ConfigureAwait(false);
                if (!caughtUp)
                {
                    return KeyIndexRead.Dropped.Instance;
                }
            }

            return await ScanEntriesAsync(ledger.Id, below, key, cancellationToken).ConfigureAwait(false);
        }

        if (ledger.Status.Value is not LedgerStatus.Closed closed)
        {
            return KeyIndexRead.Unindexed.Instance;
        }

        if (!SkipCatchUpMutant)
        {
            var caughtUp = await WaitForPredecessorScanAsync(ledger, closed.T, cancellationToken).ConfigureAwait(false);
            if (!caughtUp)
            {
                return KeyIndexRead.Dropped.Instance;
            }
        }

        return await ScanEntriesAsync(ledger.Id, below, key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> WaitForReplicatedCatchUpAsync(LedgerMetadata ledger, Slot below, CancellationToken cancellationToken)
    {
        if (Watermark(ledger.Id) >= below)
        {
            return true;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var woken = false;
        while (Watermark(ledger.Id) < below)
        {
            ObjectDisposedException.ThrowIf(_gate.IsClosed, this);
            if (_reader.Map.Current.NoLongerLists(ledger.Id))
            {
                return false;
            }

            CatchUpWaiter waiter;
            LedgerApplyLoop? loop;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_gate.IsClosed, this);
                if (_backgroundFault is { } fault)
                {
                    ExceptionDispatchInfo.Throw(fault);
                }

                if ((_appliedWatermarks.TryGetValue(ledger.Id, out var current) ? current : Slot.Zero) >= below)
                {
                    return true;
                }

                if (_reader.Map.Current.NoLongerLists(ledger.Id))
                {
                    return false;
                }

                if (!_waiters.TryGetValue(ledger.Id, out var queue))
                {
                    queue = new PriorityQueue<CatchUpWaiter, long>();
                    _waiters[ledger.Id] = queue;
                }

                waiter = new CatchUpWaiter();
                queue.Enqueue(waiter, below.Value);
                loop = _applyLoop;
            }

            if (!woken && ledger.Status.Value is LedgerStatus.Open)
            {
                woken = true;
                loop?.Wake(ledger.Id);
            }

            try
            {
                await waiter.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancelWaiter(ledger.Id, waiter);
                if (_stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new ObjectDisposedException(nameof(JournalStateDatabase));
                }

                throw;
            }
        }

        return true;
    }

    private async ValueTask<bool> WaitForPredecessorScanAsync(LedgerMetadata ledger, Slot end, CancellationToken cancellationToken)
    {
        if (Watermark(ledger.Id) >= end)
        {
            return !_reader.Map.Current.NoLongerLists(ledger.Id);
        }

        if (_reader.Map.Current.NoLongerLists(ledger.Id))
        {
            return false;
        }

        PredecessorScan scan;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_gate.IsClosed, this);
            if ((_appliedWatermarks.TryGetValue(ledger.Id, out var current) ? current : Slot.Zero) >= end)
            {
                return !_reader.Map.Current.NoLongerLists(ledger.Id);
            }

            if (_reader.Map.Current.NoLongerLists(ledger.Id))
            {
                return false;
            }

            if (!_predecessorScans.TryGetValue(ledger.Id, out scan!))
            {
                _ = _nonReplicated.Add(ledger.Id);
                scan = new PredecessorScan(this, ledger, end);
                _predecessorScans[ledger.Id] = scan;
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            return await scan.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(JournalStateDatabase));
        }
    }

    private async ValueTask<KeyIndexRead> ScanEntriesAsync(LedgerId ledger, Slot below, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        var keyHash = JournalKeyHash.Compute(key.Span);
        List<JournalIndexKeyEntry> entries;
        try
        {
            entries = await _index.GetKeyEntriesAsync(ledger, keyHash, Slot.Zero, 0, below, cancellationToken).ConfigureAwait(false);
        }
        catch (JournalIndexCorruptException corrupt)
        {
            MarkCorrupt(corrupt);
            throw;
        }

        if (!SkipCatchUpMutant && (Watermark(ledger) < below || _reader.Map.Current.NoLongerLists(ledger)))
        {
            return KeyIndexRead.Dropped.Instance;
        }

        if (entries.Count == 0)
        {
            return new KeyIndexRead.Indexed([]);
        }

        var builder = ImmutableArray.CreateBuilder<KeyIndexEntry>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            builder.Add(new KeyIndexEntry(new Tag(ledger, entry.Slot, entry.Index), entry.DefiningSlot, entry.Ordinal));
        }

        return new KeyIndexRead.Indexed(builder.MoveToImmutable());
    }

    private async Task FlushLoopAsync()
    {
        var token = _stop.Token;
        try
        {
            while (true)
            {
                await Task.Delay(_options.FlushInterval, _timeProvider, token).ConfigureAwait(false);
                bool shouldFlush;
                lock (_lock)
                {
                    shouldFlush = _dirty && _corruptException is null;
                }

                if (!shouldFlush)
                {
                    continue;
                }

                try
                {
                    await FlushAsync(token).ConfigureAwait(false);
                }
                catch (JournalIndexCorruptException)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception fault) when (fault is not OutOfMemoryException)
        {
            OnBackgroundFault(fault);
        }
    }

    private async Task CheckLoopAsync()
    {
        var token = _stop.Token;
        try
        {
            while (true)
            {
                await Task.Delay(_options.QuickCheckInterval, _timeProvider, token).ConfigureAwait(false);
                if (IsCorrupt)
                {
                    return;
                }

                try
                {
                    await _index.CheckAsync(token).ConfigureAwait(false);
                }
                catch (JournalIndexCorruptException corrupt)
                {
                    MarkCorrupt(corrupt);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception fault) when (fault is not OutOfMemoryException)
        {
            OnBackgroundFault(fault);
        }
    }

    private async Task WatchMapAsync(LedgerMapValue view)
    {
        var token = _stop.Token;
        try
        {
            var current = _reader.Map.Current;
            if (current.Epoch != view.Epoch)
            {
                view = current;
            }

            await ReconcileNonReplicatedAsync(view, token).ConfigureAwait(false);
            SignalDroppedWaiters(view);

            while (true)
            {
                view = await _reader.Map.WaitForChangeAsync(view.Epoch, token).ConfigureAwait(false);
                await ReconcileNonReplicatedAsync(view, token).ConfigureAwait(false);
                SignalDroppedWaiters(view);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception fault) when (fault is not OutOfMemoryException)
        {
            OnBackgroundFault(fault);
        }
    }

    private async Task ReconcileNonReplicatedAsync(LedgerMapValue view, CancellationToken cancellationToken)
    {
        List<LedgerId>? replicated = null;
        List<(LedgerId Id, PredecessorScan? Scan)>? dropped = null;
        lock (_lock)
        {
            foreach (var id in _nonReplicated)
            {
                if (view.Get(id) is { } listed)
                {
                    if (listed.IsOnNode(_node))
                    {
                        (replicated ??= []).Add(id);
                    }

                    continue;
                }

                if (!view.NoLongerLists(id))
                {
                    continue;
                }

                _ = _predecessorScans.Remove(id, out var scan);
                (dropped ??= []).Add((id, scan));
            }

            if (replicated is not null)
            {
                foreach (var id in replicated)
                {
                    _ = _nonReplicated.Remove(id);
                }
            }

            if (dropped is not null)
            {
                foreach (var (id, _) in dropped)
                {
                    _ = _nonReplicated.Remove(id);
                }
            }
        }

        if (dropped is null)
        {
            return;
        }

        var droppedIds = new LedgerId[dropped.Count];
        for (var index = 0; index < dropped.Count; index++)
        {
            var (id, scan) = dropped[index];
            droppedIds[index] = id;
            if (scan is not null)
            {
                await scan.StopAsync(dropped: true).ConfigureAwait(false);
            }
        }

        await RemoveLedgersCoreAsync(droppedIds, cancellationToken).ConfigureAwait(false);
    }

    private void SignalDroppedWaiters(LedgerMapValue view)
    {
        List<LedgerId>? droppedIds = null;
        List<TaskCompletionSource>? toSignal = null;
        lock (_lock)
        {
            foreach (var (id, queue) in _waiters)
            {
                if (!view.NoLongerLists(id))
                {
                    continue;
                }

                (droppedIds ??= []).Add(id);
                while (queue.TryDequeue(out var waiter, out _))
                {
                    (toSignal ??= []).Add(waiter.Completion);
                }
            }

            if (droppedIds is not null)
            {
                foreach (var id in droppedIds)
                {
                    _ = _waiters.Remove(id);
                }
            }
        }

        if (toSignal is not null)
        {
            foreach (var tcs in toSignal)
            {
                _ = tcs.TrySetResult();
            }
        }
    }

    private void SignalLedger(LedgerId ledger, long nextWatermark)
    {
        List<TaskCompletionSource>? toSignal = null;
        lock (_lock)
        {
            if (_waiters.TryGetValue(ledger, out var queue))
            {
                while (queue.TryPeek(out _, out var target) && target <= nextWatermark)
                {
                    var waiter = queue.Dequeue();
                    (toSignal ??= []).Add(waiter.Completion);
                }

                if (queue.Count == 0)
                {
                    _ = _waiters.Remove(ledger);
                }
            }
        }

        if (toSignal is not null)
        {
            foreach (var tcs in toSignal)
            {
                _ = tcs.TrySetResult();
            }
        }
    }

    private void SignalDropped(LedgerId ledger)
    {
        List<TaskCompletionSource>? toSignal = null;
        lock (_lock)
        {
            if (_waiters.Remove(ledger, out var queue))
            {
                while (queue.TryDequeue(out var waiter, out _))
                {
                    (toSignal ??= []).Add(waiter.Completion);
                }
            }
        }

        if (toSignal is not null)
        {
            foreach (var tcs in toSignal)
            {
                _ = tcs.TrySetResult();
            }
        }
    }

    private void CancelWaiter(LedgerId ledger, CatchUpWaiter waiter)
    {
        lock (_lock)
        {
            if (_waiters.TryGetValue(ledger, out var queue))
            {
                _ = queue.Remove(waiter, out _, out _);
                if (queue.Count == 0)
                {
                    _ = _waiters.Remove(ledger);
                }
            }
        }
    }

    private void SignalAllWaiters()
    {
        List<TaskCompletionSource>? toSignal = null;
        lock (_lock)
        {
            foreach (var queue in _waiters.Values)
            {
                while (queue.TryDequeue(out var waiter, out _))
                {
                    (toSignal ??= []).Add(waiter.Completion);
                }
            }

            _waiters.Clear();
        }

        if (toSignal is not null)
        {
            foreach (var tcs in toSignal)
            {
                _ = tcs.TrySetResult();
            }
        }
    }

    private void OnBackgroundFault(Exception fault)
    {
        List<TaskCompletionSource>? toSignal = null;
        lock (_lock)
        {
            _backgroundFault ??= fault;
            foreach (var queue in _waiters.Values)
            {
                while (queue.TryDequeue(out var waiter, out _))
                {
                    (toSignal ??= []).Add(waiter.Completion);
                }
            }

            _waiters.Clear();
        }

        if (toSignal is not null)
        {
            foreach (var tcs in toSignal)
            {
                _ = tcs.TrySetResult();
            }
        }
    }

    private void CompletePredecessorScan(LedgerId ledger, PredecessorScan scan)
    {
        lock (_lock)
        {
            if (_predecessorScans.TryGetValue(ledger, out var current) && ReferenceEquals(current, scan))
            {
                _ = _predecessorScans.Remove(ledger);
            }
        }
    }

    private sealed class CatchUpWaiter
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static List<LedgerMetadata> CollectDistinctClosedPredecessors(
        IReadOnlyList<(LedgerMetadata Ledger, Slot Below)> openLedgers,
        LedgerMapValue map)
    {
        HashSet<LedgerId>? seen = null;
        List<LedgerMetadata>? result = null;
        for (var ledgerIndex = 0; ledgerIndex < map.Ledgers.Length; ledgerIndex++)
        {
            var candidate = map.Ledgers[ledgerIndex];
            if (candidate.Status.Value is not LedgerStatus.Closed)
            {
                continue;
            }

            for (var index = 0; index < openLedgers.Count; index++)
            {
                if (candidate.Range.Overlaps(openLedgers[index].Ledger.Range))
                {
                    seen ??= [];
                    if (seen.Add(candidate.Id))
                    {
                        (result ??= []).Add(candidate);
                    }

                    break;
                }
            }
        }

        if (result is null)
        {
            return [];
        }

        result.Sort(static (left, right) => right.Id.Value.CompareTo(left.Id.Value));
        return result;
    }

    private async ValueTask<List<JournalIndexTombstone>> ScanLedgerTombstonesAsync(LedgerId ledger, CancellationToken cancellationToken)
    {
        try
        {
            return await _index.ScanTombstonesAsync(ledger, cancellationToken).ConfigureAwait(false);
        }
        catch (JournalIndexCorruptException corrupt)
        {
            MarkCorrupt(corrupt);
            throw;
        }
    }

    private async ValueTask<bool> HasKeyIndexEntryAsync(
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot startSlot,
        int startIndex,
        Slot below,
        CancellationToken cancellationToken)
    {
        if (startSlot >= below)
        {
            return false;
        }

        try
        {
            return await _index.HasKeyEntryAsync(ledger, keyHash, startSlot, startIndex, below, cancellationToken).ConfigureAwait(false);
        }
        catch (JournalIndexCorruptException corrupt)
        {
            MarkCorrupt(corrupt);
            throw;
        }
    }

    private ValueTask<bool> HasNewerEntryInLedgerAsync(
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot deleteSlot,
        int deleteIndex,
        Slot below,
        CancellationToken cancellationToken)
    {
        Slot nextSlot;
        int nextIndex;
        if (deleteIndex < int.MaxValue)
        {
            nextSlot = deleteSlot;
            nextIndex = deleteIndex + 1;
        }
        else if (deleteSlot.Value < long.MaxValue)
        {
            nextSlot = new Slot(deleteSlot.Value + 1);
            nextIndex = 0;
        }
        else
        {
            return ValueTask.FromResult(false);
        }

        return HasKeyIndexEntryAsync(ledger, keyHash, nextSlot, nextIndex, below, cancellationToken);
    }

    private async ValueTask CollectOpenLedgerTombstonesAsync(
        LedgerMetadata ledger,
        Slot below,
        long cutoffTicks,
        Func<JournalRowKey, bool> isWarm,
        HashSet<JournalRowKey> seenKeys,
        List<JournalRowKey> coldCandidates,
        CancellationToken cancellationToken)
    {
        var tombstones = await ScanLedgerTombstonesAsync(ledger.Id, cancellationToken).ConfigureAwait(false);
        if (!SkipCatchUpMutant && (Watermark(ledger.Id) < below || _reader.Map.Current.NoLongerLists(ledger.Id)))
        {
            return;
        }

        for (var index = 0; index < tombstones.Count; index++)
        {
            var tombstone = tombstones[index];
            if (tombstone.DeletionTicks > cutoffTicks || tombstone.DeleteSlot >= below)
            {
                continue;
            }

            var rawKey = tombstone.RawKey.Span;
            if (rawKey.Length > KeyRange.MaxKeyLength
                || !ledger.Range.Contains(rawKey)
                || JournalRowKey.TryFromRowKeyBytes(rawKey) is not { } rowKey)
            {
                continue;
            }

            if (!seenKeys.Add(rowKey) || isWarm(rowKey))
            {
                continue;
            }

            if (await HasNewerEntryInLedgerAsync(ledger.Id, tombstone.KeyHash, tombstone.DeleteSlot, tombstone.DeleteIndex, below, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            coldCandidates.Add(rowKey);
        }
    }

    private async ValueTask CollectClosedPredecessorTombstonesAsync(
        LedgerMetadata closed,
        Slot end,
        IReadOnlyList<(LedgerMetadata Ledger, Slot Below)> allOpenLedgers,
        List<(LedgerMetadata Ledger, Slot Below)> indexedOpen,
        List<(LedgerMetadata Ledger, Slot End)> indexedClosed,
        int newerClosedCount,
        long cutoffTicks,
        Func<JournalRowKey, bool> isWarm,
        HashSet<JournalRowKey> seenKeys,
        List<JournalRowKey> coldCandidates,
        CancellationToken cancellationToken)
    {
        var tombstones = await ScanLedgerTombstonesAsync(closed.Id, cancellationToken).ConfigureAwait(false);
        if (!SkipCatchUpMutant && (Watermark(closed.Id) < end || _reader.Map.Current.NoLongerLists(closed.Id)))
        {
            return;
        }

        for (var index = 0; index < tombstones.Count; index++)
        {
            var tombstone = tombstones[index];
            if (tombstone.DeletionTicks > cutoffTicks || tombstone.DeleteSlot >= end)
            {
                continue;
            }

            var rawKey = tombstone.RawKey;
            var rawKeySpan = rawKey.Span;
            if (rawKeySpan.Length > KeyRange.MaxKeyLength
                || !IsOwnedByAny(allOpenLedgers, rawKeySpan)
                || JournalRowKey.TryFromRowKeyBytes(rawKeySpan) is not { } rowKey)
            {
                continue;
            }

            if (!seenKeys.Add(rowKey) || isWarm(rowKey))
            {
                continue;
            }

            if (await HasNewerEntryInLedgerAsync(closed.Id, tombstone.KeyHash, tombstone.DeleteSlot, tombstone.DeleteIndex, end, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (await HasCrossLedgerResurrectionAsync(rawKey, tombstone.KeyHash, indexedOpen, indexedClosed, newerClosedCount, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            coldCandidates.Add(rowKey);
        }
    }

    private async ValueTask<bool> HasCrossLedgerResurrectionAsync(
        ReadOnlyMemory<byte> rawKey,
        JournalKeyHash keyHash,
        List<(LedgerMetadata Ledger, Slot Below)> indexedOpen,
        List<(LedgerMetadata Ledger, Slot End)> indexedClosed,
        int newerClosedCount,
        CancellationToken cancellationToken)
    {
        var rawKeySpan = rawKey.Span;
        LedgerMetadata? matchingOpen = null;
        Slot matchingOpenBelow = Slot.Zero;
        for (var index = 0; index < indexedOpen.Count; index++)
        {
            if (indexedOpen[index].Ledger.Range.Contains(rawKeySpan))
            {
                matchingOpen = indexedOpen[index].Ledger;
                matchingOpenBelow = indexedOpen[index].Below;
                break;
            }
        }

        List<(LedgerId Id, Slot End)>? matchingNewerClosed = null;
        for (var index = 0; index < newerClosedCount; index++)
        {
            if (indexedClosed[index].Ledger.Range.Contains(rawKeySpan))
            {
                (matchingNewerClosed ??= []).Add((indexedClosed[index].Ledger.Id, indexedClosed[index].End));
            }
        }

        if (matchingOpen is { } open
            && await HasKeyIndexEntryAsync(open.Id, keyHash, Slot.Zero, 0, matchingOpenBelow, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (matchingNewerClosed is not null)
        {
            for (var index = 0; index < matchingNewerClosed.Count; index++)
            {
                var (id, newerEnd) = matchingNewerClosed[index];
                if (await HasKeyIndexEntryAsync(id, keyHash, Slot.Zero, 0, newerEnd, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsOwnedByAny(IReadOnlyList<(LedgerMetadata Ledger, Slot Below)> openLedgers, ReadOnlySpan<byte> rawKey)
    {
        for (var index = 0; index < openLedgers.Count; index++)
        {
            if (openLedgers[index].Ledger.Range.Contains(rawKey))
            {
                return true;
            }
        }

        return false;
    }

    private readonly struct PendingBatch(
        JournalIndexKeyWrite[]? keyWrites,
        int keyCount,
        JournalIndexTombstoneWrite[]? tombstoneWrites,
        int tombstoneCount,
        byte[]? rawKeyBuffer,
        int rawKeyBytes) : IDisposable
    {
        public JournalIndexKeyWrite[]? KeyWrites { get; } = keyWrites;

        public int KeyCount { get; } = keyCount;

        public JournalIndexTombstoneWrite[]? TombstoneWrites { get; } = tombstoneWrites;

        public int TombstoneCount { get; } = tombstoneCount;

        public byte[]? RawKeyBuffer { get; } = rawKeyBuffer;

        public int EstimatedBytes => KeyCount * 68 + TombstoneCount * 60 + rawKeyBytes;

        public void Dispose()
        {
            if (KeyWrites is not null)
            {
                ArrayPool<JournalIndexKeyWrite>.Shared.Return(KeyWrites);
            }

            if (TombstoneWrites is not null)
            {
                ArrayPool<JournalIndexTombstoneWrite>.Shared.Return(TombstoneWrites, clearArray: true);
            }

            if (RawKeyBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(RawKeyBuffer);
            }
        }
    }

    /// <summary>
    /// One shared scan of a closed predecessor ledger this node does not replicate (journals.md §8.3), running from its
    /// current watermark (slot 0 on first touch) through <c>T</c>, shared by every lookup that needs it.
    /// </summary>
    private sealed class PredecessorScan
    {
        private readonly JournalStateDatabase _owner;
        private readonly LedgerMetadata _ledger;
        private readonly Slot _end;
        private readonly CancellationTokenSource _stop = new();
        private readonly Lock _stopLock = new();
        private readonly Task<bool> _run;
        private bool _runCompleted;
        private bool _stopping;
        private int _stoppedForDrop;

        public PredecessorScan(JournalStateDatabase owner, LedgerMetadata ledger, Slot end)
        {
            _owner = owner;
            _ledger = ledger;
            _end = end;
            _ = Interlocked.Increment(ref owner._predecessorScansStarted);
            using (ExecutionContext.SuppressFlow())
            {
                _run = Task.Run(RunAsync, CancellationToken.None);
            }
        }

        public Task<bool> Completion => _run;

        public async Task StopAsync(bool dropped)
        {
            if (dropped)
            {
                Volatile.Write(ref _stoppedForDrop, 1);
            }

            var ownStop = false;
            lock (_stopLock)
            {
                if (!_runCompleted && !_stopping)
                {
                    _stopping = true;
                    ownStop = true;
                }
            }

            if (ownStop)
            {
                try
                {
                    await _stop.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        _ = await _run.ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                    }
                }
                finally
                {
                    _stop.Dispose();
                }
            }
            else
            {
                try
                {
                    _ = await _run.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                }
            }
        }

        private async Task<bool> RunAsync()
        {
            var token = _stop.Token;
            try
            {
                var next = _owner.Watermark(_ledger.Id);
                using var view = new LedgerSegmentView(_ledger.Id);
                var pending = new List<(Slot Slot, PendingBatch Batch)>();
                var pendingRows = 0;
                var pendingBytes = 0;
                var attempt = 0;
                try
                {
                    while (next < _end)
                    {
                        var (passNext, passAttempt, passRows, passBytes, dropped, backpressured) =
                            await ScanAttemptAsync(view, pending, next, attempt, pendingRows, pendingBytes, token).ConfigureAwait(false);
                        next = passNext;
                        attempt = passAttempt;
                        pendingRows = passRows;
                        pendingBytes = passBytes;

                        if (pending.Count > 0)
                        {
                            if (dropped)
                            {
                                DisposePending(pending);
                            }
                            else
                            {
                                await FlushPendingAsync(pending, token).ConfigureAwait(false);
                            }

                            pendingRows = 0;
                            pendingBytes = 0;
                        }

                        if (dropped)
                        {
                            return false;
                        }

                        if (backpressured)
                        {
                            await Task.Delay(Backoff.Delay(attempt++, PredecessorBaseDelay, PredecessorMaximumDelay, Random.Shared.NextDouble()), _owner._timeProvider, token).ConfigureAwait(false);
                        }
                    }

                    return true;
                }
                finally
                {
                    DisposePending(pending);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (Volatile.Read(ref _stoppedForDrop) != 0)
                {
                    return false;
                }

                throw;
            }
            finally
            {
                _owner.CompletePredecessorScan(_ledger.Id, this);
                var disposeStop = false;
                lock (_stopLock)
                {
                    _runCompleted = true;
                    disposeStop = !_stopping;
                }

                if (disposeStop)
                {
                    _stop.Dispose();
                }
            }
        }

        private async Task<(Slot Next, int Attempt, int PendingRows, int PendingBytes, bool Dropped, bool Backpressured)> ScanAttemptAsync(
            LedgerSegmentView view,
            List<(Slot Slot, PendingBatch Batch)> pending,
            Slot next,
            int attempt,
            int pendingRows,
            int pendingBytes,
            CancellationToken token)
        {
            var dropped = false;
            var backpressured = false;
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                await using var enumerator = _owner._reader.ScanAsync(_ledger, next, _end, view, _owner._predecessorWindow, scanCts.Token).GetAsyncEnumerator(scanCts.Token);
                while (true)
                {
                    var moveNext = enumerator.MoveNextAsync();
                    if (!moveNext.IsCompleted && pending.Count > 0)
                    {
                        try
                        {
                            await FlushPendingAsync(pending, token).ConfigureAwait(false);
                        }
                        catch
                        {
                            await scanCts.CancelAsync().ConfigureAwait(false);
                            try
                            {
                                if (await moveNext.ConfigureAwait(false)
                                    && enumerator.Current.Read.TryGetValue(out LedgerRead.Data leaked))
                                {
                                    leaked.Dispose();
                                }
                            }
                            catch (Exception secondary) when (secondary is not OutOfMemoryException)
                            {
                            }

                            throw;
                        }

                        pendingRows = 0;
                        pendingBytes = 0;
                    }

                    if (!await moveNext.ConfigureAwait(false))
                    {
                        break;
                    }

                    var item = enumerator.Current;
                    if (item.Read.TryGetValue(out LedgerRead.Data data))
                    {
                        Slot slot;
                        PendingBatch batch;
                        using (data)
                        {
                            slot = data.Slot;
                            batch = _owner.MaterializeBatch(_ledger.Id, slot, data, item);
                        }

                        pending.Add((slot, batch));
                        pendingRows += batch.KeyCount;
                        pendingBytes += batch.EstimatedBytes;
                        next = slot + 1;
                        attempt = 0;

                        if (pendingRows >= PredecessorCommitMaxRows || pendingBytes >= PredecessorCommitMaxBytes)
                        {
                            await FlushPendingAsync(pending, token).ConfigureAwait(false);
                            pendingRows = 0;
                            pendingBytes = 0;
                        }
                    }
                    else if (item.Read.TryGetValue(out ReplicaWriteOutcome.Dropped? _))
                    {
                        dropped = true;
                        break;
                    }
                    else if (item.Read.TryGetValue(out ReplicaWriteOutcome.Backpressure? _))
                    {
                        backpressured = true;
                        break;
                    }
                    else
                    {
                        throw new UnreachableException("A scan's item holds a batch, Dropped, or Backpressure.");
                    }
                }
            }
            catch (Exception failure) when (IsTransient(failure) && !token.IsCancellationRequested)
            {
                backpressured = true;
            }

            return (next, attempt, pendingRows, pendingBytes, dropped, backpressured);
        }

        private async ValueTask FlushPendingAsync(List<(Slot Slot, PendingBatch Batch)> buffered, CancellationToken ct)
        {
            try
            {
                await _owner.CommitBatchesAsync(_ledger.Id, buffered, ct).ConfigureAwait(false);
            }
            finally
            {
                DisposePending(buffered);
            }
        }

        private static void DisposePending(List<(Slot Slot, PendingBatch Batch)> buffered)
        {
            for (var index = 0; index < buffered.Count; index++)
            {
                buffered[index].Batch.Dispose();
            }

            buffered.Clear();
        }
    }
}

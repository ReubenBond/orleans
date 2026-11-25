using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Jobs;
using Orleans.Durable.Messaging;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Threading;
using Orleans.Runtime;

namespace Orleans.Durable.Storage;

/// <summary>
/// Data-plane <see cref="IMessageHandler"/> for <c>orleans.wakeup</c> (orleans.md §5.15): validates wakeup
/// message payloads before admission and maintains one isolated <see cref="WakeupLedgerSession"/> per open ledger
/// primary.
/// </summary>
internal sealed partial class WakeupMessageHandler : IMessageHandler
{
    /// <summary>The canonical <see cref="MessageChannel"/> for grain wakeups (<c>orleans.wakeup</c>).</summary>
    public static readonly MessageChannel WakeupChannel = MessageChannel.Parse(WakeupMessageCodec.ChannelName);

    private static readonly string[] WellKnownJobNames =
    [
        MessagingPump.JobName,
        DurableJobQueue.WakeupJobName,
    ];

    private readonly WakeupOptions _options;
    private readonly IGrainWakeupDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly AdmissionGate _dispatchGate = new();
    private readonly ConcurrentDictionary<LedgerId, WakeupLedgerSession> _sessions = new();
    private readonly ConcurrentDictionary<LedgerId, byte> _ledgersNeedingRoll = new();
    private int _stopped;

    /// <summary>Initializes a new instance of the <see cref="WakeupMessageHandler"/> class.</summary>
    public WakeupMessageHandler(
        WakeupOptions options,
        IGrainWakeupDispatcher dispatcher,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _options = options;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public MessageChannel Channel => WakeupChannel;

    /// <inheritdoc />
    public InvalidDataException? Validate(ReadOnlySpan<byte> payload) =>
        WakeupMessageCodec.Decode(payload, TryInternJobName, out _);

    /// <inheritdoc />
    public IMessageSession OpenLedger(LedgerId ledger)
    {
        var session = new WakeupLedgerSession(this, ledger);
        _sessions[ledger] = session;
        return session;
    }

    /// <summary>
    /// Collects the ledgers whose open wakeup sessions exceeded <see cref="WakeupOptions.MaxWakeupBytesPerLedger"/>
    /// and should be proactively rolled (orleans.md §5.15).
    /// </summary>
    public ImmutableArray<LedgerId> CollectLedgersNeedingRoll()
    {
        if (_ledgersNeedingRoll.IsEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<LedgerId>();
        foreach (var kvp in _ledgersNeedingRoll)
        {
            if (_ledgersNeedingRoll.TryRemove(kvp.Key, out _))
            {
                builder.Add(kvp.Key);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Cancels all active wakeup timer loops and awaits every in-flight dispatch to drain cleanly during silo stop
    /// (called inside <c>NodeRuntime.StopJournalsAsync</c> before <c>Journals.DisposeAsync</c>; orleans.md §5.15).
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
        _ledgersNeedingRoll.Clear();
        await _dispatchGate.CloseAsync().ConfigureAwait(false);
    }

    internal static string? TryInternJobName(ReadOnlySpan<byte> utf8)
    {
        foreach (var known in WellKnownJobNames)
        {
            if (utf8.Length == known.Length && EqualsAscii(utf8, known))
            {
                return known;
            }
        }

        return null;
    }

    private static bool EqualsAscii(ReadOnlySpan<byte> bytes, string ascii)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    private void MarkNeedsRoll(LedgerId ledger) => _ledgersNeedingRoll[ledger] = 1;

    private void RemoveSession(LedgerId ledger, WakeupLedgerSession session)
    {
        _ = _sessions.TryRemove(new KeyValuePair<LedgerId, WakeupLedgerSession>(ledger, session));
        _ = _ledgersNeedingRoll.TryRemove(ledger, out _);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ephemeral wakeup dispatch for ({Target}, {JobName}) on ledger {Ledger} failed; retrying after backoff.")]
    private static partial void LogDispatchFailed(ILogger logger, GrainId target, string jobName, LedgerId ledger, Exception exception);

    /// <summary>
    /// Per-open-ledger primary wakeup session (orleans.md §5.15): indexed binary min-heap with zero dead entries,
    /// session-monotonic generation counters, safe <c>InFlight</c> clearing/updating, and a <see cref="TimeProvider"/>
    /// timer loop bounded by <see cref="WakeupOptions.MaxConcurrentWakeupDispatches"/>.
    /// </summary>
    internal sealed class WakeupLedgerSession : IMessageSession
    {
        private const long EntryOverheadBytes = 128;

        private readonly WakeupMessageHandler _owner;
        private readonly LedgerId _ledger;
        private readonly Lock _lock = new();
        private readonly Dictionary<(GrainId Target, string JobName), WakeupEntry> _entries = [];
        private readonly List<WakeupEntry> _heap = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly ITimer _timer;
        private ulong _nextGeneration;
        private long _estimatedBytes;
        private long _scheduledDueTicks = long.MaxValue;
        private int _inFlightDispatches;
        private bool _rollRequested;
        private bool _disposed;

        internal WakeupLedgerSession(WakeupMessageHandler owner, LedgerId ledger)
        {
            _owner = owner;
            _ledger = ledger;
            _timer = owner._timeProvider.CreateTimer(static state => ((WakeupLedgerSession)state!).OnTimerTick(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Gets the current count of tracked wakeup entries; for tests.</summary>
        internal int EntryCount
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>Gets the current count of heap entries (must never exceed <see cref="EntryCount"/>); for tests.</summary>
        internal int HeapCount
        {
            get
            {
                lock (_lock)
                {
                    return _heap.Count;
                }
            }
        }

        /// <inheritdoc />
        public void OnBatchDurable(Slot slot, ReadOnlySpan<MessageRecord> records)
        {
            // Decode and intern outside _lock so the critical section only updates the map and heap.
            var decoded = new WakeupRecord[records.Length];
            for (var i = 0; i < records.Length; i++)
            {
                var rejection = WakeupMessageCodec.Decode(records[i].Payload.Span, TryInternJobName, out decoded[i]);
                if (rejection is not null)
                {
                    throw new InvalidDataException(
                        string.Create(CultureInfo.InvariantCulture, $"Invalid wakeup message at ledger {_ledger} slot {slot}: {rejection.Message}"),
                        rejection);
                }
            }

            var triggerRoll = false;
            var dispatchNow = false;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                for (var i = 0; i < decoded.Length; i++)
                {
                    ref readonly var rec = ref decoded[i];
                    var key = (rec.Target, rec.JobName);
                    if (rec.DueTimeUtcTicks is { } dueTicks)
                    {
                        var gen = ++_nextGeneration;
                        if (_entries.TryGetValue(key, out var existing))
                        {
                            existing.DueTimeUtcTicks = dueTicks;
                            existing.Generation = gen;
                            existing.Cleared = false;
                            existing.RetryCount = 0;
                            if (!existing.InFlight)
                            {
                                HeapInsertOrUpdateLocked(existing);
                            }
                        }
                        else
                        {
                            var entry = new WakeupEntry(rec.Target, rec.JobName, dueTicks, gen, EstimateEntryBytes(rec.Target, rec.JobName));
                            _entries.Add(key, entry);
                            _estimatedBytes += entry.EstimatedBytes;
                            HeapInsertOrUpdateLocked(entry);
                        }
                    }
                    else if (_entries.TryGetValue(key, out var existing))
                    {
                        existing.Generation = ++_nextGeneration;
                        if (existing.InFlight)
                        {
                            // Keep in _entries until dispatch finishes so a subsequent SetWakeup(t2) advances Generation safely.
                            existing.Cleared = true;
                        }
                        else
                        {
                            RemoveEntryLocked(existing);
                        }
                    }
                }

                if (!_rollRequested && _estimatedBytes > _owner._options.MaxWakeupBytesPerLedger)
                {
                    _rollRequested = true;
                    triggerRoll = true;
                }

                dispatchNow = ArmTimerOrNeedImmediateDispatchLocked();
            }

            if (triggerRoll)
            {
                _owner.MarkNeedsRoll(_ledger);
            }

            if (dispatchNow)
            {
                DispatchDue();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _scheduledDueTicks = long.MaxValue;
                foreach (var entry in _entries.Values)
                {
                    entry.HeapIndex = -1;
                }

                _entries.Clear();
                _heap.Clear();
                _estimatedBytes = 0;
            }

            _timer.Dispose();
            _cts.Cancel();
            _cts.Dispose();
            _owner.RemoveSession(_ledger, this);
        }

        private void OnTimerTick() => DispatchDue();

        private void DispatchDue()
        {
            while (true)
            {
                WakeupEntry? entry;
                ulong dispatchedGen;
                long dispatchedDue;
                lock (_lock)
                {
                    if (_disposed || _inFlightDispatches >= _owner._options.MaxConcurrentWakeupDispatches || _heap.Count == 0)
                    {
                        _ = ArmTimerOrNeedImmediateDispatchLocked();
                        return;
                    }

                    var nowTicks = _owner._timeProvider.GetUtcNow().UtcTicks;
                    var min = _heap[0];
                    if (min.DueTimeUtcTicks > nowTicks)
                    {
                        _ = ArmTimerOrNeedImmediateDispatchLocked();
                        return;
                    }

                    HeapRemoveAtLocked(0);
                    min.InFlight = true;
                    _inFlightDispatches++;
                    entry = min;
                    dispatchedGen = min.Generation;
                    dispatchedDue = min.DueTimeUtcTicks;
                }

                var gate = _owner._dispatchGate.TryEnter();
                if (!gate.Entered)
                {
                    FinishDispatch(entry, dispatchedGen, DurableJobRunResult.Completed, failed: false, exception: null);
                    return;
                }

                // Dispatch in no caller's ExecutionContext (conventions.md §12).
                var suppress = !ExecutionContext.IsFlowSuppressed();
                var flow = suppress ? ExecutionContext.SuppressFlow() : default;
                try
                {
                    _ = DispatchOneAsync(entry, dispatchedGen, dispatchedDue, gate);
                }
                finally
                {
                    if (suppress)
                    {
                        flow.Undo();
                    }
                }
            }
        }

        private async Task DispatchOneAsync(WakeupEntry entry, ulong dispatchedGen, long dispatchedDueTicks, AdmissionGate.Admission gate)
        {
            try
            {
                var job = new DurableJob
                {
                    Id = string.Create(CultureInfo.InvariantCulture, $"wakeup:{_ledger.Value}:{dispatchedGen}"),
                    Name = entry.JobName,
                    DueTime = new DateTimeOffset(dispatchedDueTicks, TimeSpan.Zero),
                    TargetGrainId = entry.Target,
                    ShardId = string.Create(CultureInfo.InvariantCulture, $"ledger:{_ledger.Value}"),
                };
                var context = new JobRunContext(job, Guid.NewGuid().ToString("N"), entry.RetryCount + 1, isEphemeralWakeup: true);

                DurableJobRunResult result;
                try
                {
                    result = await _owner._dispatcher.DispatchAsync(entry.Target, context, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    FinishDispatch(entry, dispatchedGen, DurableJobRunResult.Completed, failed: false, exception: null);
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FinishDispatch(entry, dispatchedGen, DurableJobRunResult.Failed(ex), failed: true, exception: ex);
                    return;
                }

                FinishDispatch(entry, dispatchedGen, result, failed: result.IsFailed, exception: result.Exception);
            }
            finally
            {
                gate.Dispose();
            }
        }

        private void FinishDispatch(WakeupEntry entry, ulong dispatchedGen, DurableJobRunResult result, bool failed, Exception? exception)
        {
            if (failed && exception is not null)
            {
                LogDispatchFailed(_owner._logger, entry.Target, entry.JobName, _ledger, exception);
            }

            var dispatchMore = false;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _inFlightDispatches--;
                entry.InFlight = false;

                if (entry.Generation != dispatchedGen)
                {
                    // A newer SetWakeup arrived while this dispatch was in flight.
                    if (entry.Cleared)
                    {
                        RemoveEntryLocked(entry);
                    }
                    else
                    {
                        HeapInsertOrUpdateLocked(entry);
                    }
                }
                else if (entry.Cleared)
                {
                    RemoveEntryLocked(entry);
                }
                else if (failed)
                {
                    entry.RetryCount++;
                    var delay = ComputeBackoffDelay(entry.RetryCount);
                    entry.DueTimeUtcTicks = AddClampedTicks(_owner._timeProvider.GetUtcNow().UtcTicks, delay.Ticks);
                    HeapInsertOrUpdateLocked(entry);
                }
                else if (result.IsRescheduleRequested && result.RescheduleTime is { } rescheduleAt)
                {
                    entry.RetryCount = 0;
                    entry.DueTimeUtcTicks = rescheduleAt.UtcTicks;
                    HeapInsertOrUpdateLocked(entry);
                }
                else if (result.IsInProgress && result.PollAfterDelay is { } pollAfter)
                {
                    entry.RetryCount = 0;
                    entry.DueTimeUtcTicks = AddClampedTicks(_owner._timeProvider.GetUtcNow().UtcTicks, pollAfter.Ticks);
                    HeapInsertOrUpdateLocked(entry);
                }
                else
                {
                    // Completed cleanly and no newer SetWakeup arrived: remove from map.
                    RemoveEntryLocked(entry);
                }

                dispatchMore = ArmTimerOrNeedImmediateDispatchLocked();
            }

            if (dispatchMore)
            {
                DispatchDue();
            }
        }

        private bool ArmTimerOrNeedImmediateDispatchLocked()
        {
            if (_disposed || _heap.Count == 0 || _inFlightDispatches >= _owner._options.MaxConcurrentWakeupDispatches)
            {
                if (_scheduledDueTicks != long.MaxValue && !_disposed)
                {
                    _scheduledDueTicks = long.MaxValue;
                    _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }

                return false;
            }

            var minDue = _heap[0].DueTimeUtcTicks;
            var nowTicks = _owner._timeProvider.GetUtcNow().UtcTicks;
            if (minDue <= nowTicks)
            {
                _scheduledDueTicks = long.MaxValue;
                _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return true;
            }

            if (minDue != _scheduledDueTicks)
            {
                _scheduledDueTicks = minDue;
                var delayTicks = Math.Min(minDue - nowTicks, TimeSpan.FromDays(1).Ticks);
                _ = _timer.Change(TimeSpan.FromTicks(Math.Max(1, delayTicks)), Timeout.InfiniteTimeSpan);
            }

            return false;
        }

        private void RemoveEntryLocked(WakeupEntry entry)
        {
            if (entry.HeapIndex >= 0)
            {
                HeapRemoveAtLocked(entry.HeapIndex);
            }

            if (_entries.Remove((entry.Target, entry.JobName)))
            {
                _estimatedBytes -= entry.EstimatedBytes;
                Debug.Assert(_estimatedBytes >= 0, "Estimated wakeup bytes never go negative.");
            }
        }

        private void HeapInsertOrUpdateLocked(WakeupEntry entry)
        {
            if (entry.HeapIndex >= 0)
            {
                var idx = entry.HeapIndex;
                SiftUpLocked(idx);
                SiftDownLocked(entry.HeapIndex);
            }
            else
            {
                entry.HeapIndex = _heap.Count;
                _heap.Add(entry);
                SiftUpLocked(entry.HeapIndex);
            }
        }

        private void HeapRemoveAtLocked(int index)
        {
            var lastIdx = _heap.Count - 1;
            var removed = _heap[index];
            removed.HeapIndex = -1;
            if (index == lastIdx)
            {
                _heap.RemoveAt(lastIdx);
                return;
            }

            var moved = _heap[lastIdx];
            _heap.RemoveAt(lastIdx);
            _heap[index] = moved;
            moved.HeapIndex = index;
            SiftUpLocked(index);
            SiftDownLocked(moved.HeapIndex);
        }

        private void SiftUpLocked(int index)
        {
            var item = _heap[index];
            while (index > 0)
            {
                var parentIdx = (index - 1) >> 1;
                var parent = _heap[parentIdx];
                if (item.DueTimeUtcTicks >= parent.DueTimeUtcTicks)
                {
                    break;
                }

                _heap[index] = parent;
                parent.HeapIndex = index;
                index = parentIdx;
            }

            _heap[index] = item;
            item.HeapIndex = index;
        }

        private void SiftDownLocked(int index)
        {
            var count = _heap.Count;
            var item = _heap[index];
            while (true)
            {
                var left = (index << 1) + 1;
                if (left >= count)
                {
                    break;
                }

                var smallest = left;
                var right = left + 1;
                if (right < count && _heap[right].DueTimeUtcTicks < _heap[left].DueTimeUtcTicks)
                {
                    smallest = right;
                }

                var child = _heap[smallest];
                if (item.DueTimeUtcTicks <= child.DueTimeUtcTicks)
                {
                    break;
                }

                _heap[index] = child;
                child.HeapIndex = index;
                index = smallest;
            }

            _heap[index] = item;
            item.HeapIndex = index;
        }

        private TimeSpan ComputeBackoffDelay(int retryCount)
        {
            var initialTicks = _owner._options.InitialRetryDelay.Ticks;
            var maxTicks = _owner._options.MaxRetryDelay.Ticks;
            var shift = Math.Min(retryCount - 1, 30);
            var factor = 1L << shift;
            var delayTicks = initialTicks > maxTicks / factor ? maxTicks : initialTicks * factor;
            return TimeSpan.FromTicks(Math.Min(delayTicks, maxTicks));
        }

        private static long AddClampedTicks(long baseTicks, long deltaTicks)
        {
            var max = DateTimeOffset.MaxValue.UtcTicks;
            return baseTicks > max - deltaTicks ? max : baseTicks + deltaTicks;
        }

        private static long EstimateEntryBytes(GrainId target, string jobName) =>
            EntryOverheadBytes + target.Type.Value.AsSpan().Length + target.Key.Value.Length + jobName.Length * sizeof(char);

        private sealed class WakeupEntry(GrainId target, string jobName, long dueTimeUtcTicks, ulong generation, long estimatedBytes)
        {
            public GrainId Target { get; } = target;
            public string JobName { get; } = jobName;
            public long EstimatedBytes { get; } = estimatedBytes;
            public long DueTimeUtcTicks { get; set; } = dueTimeUtcTicks;
            public ulong Generation { get; set; } = generation;
            public int HeapIndex { get; set; } = -1;
            public int RetryCount { get; set; }
            public bool InFlight { get; set; }
            public bool Cleared { get; set; }
        }
    }
}

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
/// Data-plane <see cref="ILocalStreamHandler"/> for <c>orleans.wakeup</c> (orleans.md §5.15): validates companion
/// wakeup payloads before admission and maintains one isolated <see cref="WakeupLedgerSession"/> per open ledger
/// primary.
/// </summary>
internal sealed partial class WakeupStreamHandler : ILocalStreamHandler
{
    /// <summary>The canonical <see cref="LocalStreamName"/> for grain wakeups (<c>orleans.wakeup</c>).</summary>
    public static readonly LocalStreamName WakeupStream = LocalStreamName.Parse(WakeupCompanionCodec.StreamName);

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

    /// <summary>Initializes a new instance of the <see cref="WakeupStreamHandler"/> class.</summary>
    public WakeupStreamHandler(
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
    public LocalStreamName Stream => WakeupStream;

    /// <inheritdoc />
    public InvalidDataException? Validate(ReadOnlySpan<byte> payload) =>
        WakeupCompanionCodec.Decode(payload, out _);

    /// <inheritdoc />
    public ILocalStreamSession OpenLedger(LedgerId ledger)
    {
        var session = new WakeupLedgerSession(this, ledger, _options, _dispatcher, _timeProvider, _logger);
        if (Volatile.Read(ref _stopped) != 0)
        {
            session.Dispose();
            return session;
        }

        _sessions[ledger] = session;
        if (Volatile.Read(ref _stopped) != 0 && _sessions.TryRemove(new KeyValuePair<LedgerId, WakeupLedgerSession>(ledger, session)))
        {
            session.Dispose();
        }

        return session;
    }

    /// <summary>
    /// Returns and clears the open ledgers whose live wakeup heap exceeded
    /// <see cref="WakeupOptions.MaxWakeupBytesPerLedger"/> and therefore need a routine ledger roll.
    /// </summary>
    public ImmutableArray<LedgerId> CollectLedgersNeedingRoll()
    {
        if (_ledgersNeedingRoll.IsEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<LedgerId>();
        foreach (var ledger in _ledgersNeedingRoll.Keys)
        {
            if (_ledgersNeedingRoll.TryRemove(ledger, out _))
            {
                builder.Add(ledger);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Stops all active wakeup sessions' timer loops and awaits all in-flight dispatches cleanly before
    /// <see cref="JournalStore.DisposeAsync"/> tears down admissions. Tolerates trailing <c>OnBatchDurable</c> and
    /// <c>Dispose</c> calls.
    /// </summary>
    public async Task StopAsync()
    {
        Volatile.Write(ref _stopped, 1);
        var timerTasks = new List<Task>();
        foreach (var (ledger, session) in _sessions)
        {
            if (_sessions.TryRemove(new KeyValuePair<LedgerId, WakeupLedgerSession>(ledger, session)))
            {
                timerTasks.Add(session.StopAndDisposeAsync());
            }
        }

        await _dispatchGate.CloseAsync().ConfigureAwait(false);
        if (timerTasks.Count > 0)
        {
            await Task.WhenAll(timerTasks).ConfigureAwait(false);
        }
    }

    internal bool TryEnterDispatch() => _dispatchGate.TryEnterUnscoped();

    internal void ExitDispatch() => _dispatchGate.Exit();

    internal void FlagLedgerNeedingRoll(LedgerId ledger) => _ledgersNeedingRoll.TryAdd(ledger, 0);

    internal void UnregisterSession(LedgerId ledger, WakeupLedgerSession session)
    {
        _ = _sessions.TryRemove(new KeyValuePair<LedgerId, WakeupLedgerSession>(ledger, session));
        _ = _ledgersNeedingRoll.TryRemove(ledger, out _);
    }

    internal static string InternJobName(string jobName)
    {
        foreach (var known in WellKnownJobNames)
        {
            if (string.Equals(jobName, known, StringComparison.Ordinal))
            {
                return known;
            }
        }

        return jobName;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ephemeral wakeup dispatch for grain {Target} job {JobName} on ledger {Ledger} failed on attempt {Attempt}; retrying in {Delay}.")]
    internal static partial void LogWakeupDispatchRetry(ILogger logger, GrainId target, string jobName, LedgerId ledger, int attempt, TimeSpan delay, Exception? exception);
}

/// <summary>
/// Per-open-ledger wakeup scheduler backed by an indexed binary min-heap (zero dead entries) and a session-monotonic
/// <c>ulong</c> generation counter to eliminate ABA races between mid-dispatch updates/clears and completions
/// (orleans.md §5.15).
/// </summary>
internal sealed class WakeupLedgerSession : ILocalStreamSession
{
    private const long BaseEntryOverheadBytes = 128;

    private readonly WakeupStreamHandler _handler;
    private readonly LedgerId _ledger;
    private readonly WakeupOptions _options;
    private readonly IGrainWakeupDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<(GrainId Target, string JobName), Entry> _entries = [];
    private readonly List<Entry> _heap = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _dispatchSlots;
    private TaskCompletionSource _wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _timerLoop;
    private ulong _nextGeneration;
    private long _memoryBytes;
    private bool _rollFlagged;
    private bool _disposed;

    internal WakeupLedgerSession(
        WakeupStreamHandler handler,
        LedgerId ledger,
        WakeupOptions options,
        IGrainWakeupDispatcher dispatcher,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _handler = handler;
        _ledger = ledger;
        _options = options;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _logger = logger;
        _dispatchSlots = new SemaphoreSlim(options.MaxConcurrentWakeupDispatches, options.MaxConcurrentWakeupDispatches);
    }

    /// <summary>Gets the current live memory in bytes tracked by this session; for tests.</summary>
    internal long MemoryBytes
    {
        get
        {
            lock (_lock)
            {
                return _memoryBytes;
            }
        }
    }

    /// <summary>Gets the number of tracked entries (queued or in-flight); for tests.</summary>
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

    /// <summary>Gets the number of queued entries in the min-heap; for tests.</summary>
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
    public void OnBatchDurable(Slot slot, ReadOnlySpan<LocalAppendRecord> records)
    {
        if (records.IsEmpty)
        {
            return;
        }

        // Decode and intern well-known job names outside the per-ledger lock.
        var decoded = new WakeupRecord[records.Length];
        for (var i = 0; i < records.Length; i++)
        {
            if (WakeupCompanionCodec.Decode(records[i].Payload.Span, out var rec) is { } error)
            {
                throw error;
            }

            decoded[i] = rec with { JobName = WakeupStreamHandler.InternJobName(rec.JobName) };
        }

        TaskCompletionSource? toSignal = null;
        var flagRoll = false;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var prevHeadTicks = _heap.Count > 0 ? _heap[0].DueTimeUtcTicks!.Value : long.MaxValue;
            for (var i = 0; i < decoded.Length; i++)
            {
                var rec = decoded[i];
                var key = (rec.Target, rec.JobName);
                if (rec.DueTimeUtcTicks is { } dueTicks)
                {
                    if (_entries.TryGetValue(key, out var entry))
                    {
                        entry.Generation = ++_nextGeneration;
                        entry.DueTimeUtcTicks = dueTicks;
                        entry.Attempt = 0;
                        if (!entry.InFlight)
                        {
                            if (entry.HeapIndex >= 0)
                            {
                                HeapUpdate(entry.HeapIndex);
                            }
                            else
                            {
                                HeapPush(entry);
                            }
                        }
                    }
                    else
                    {
                        var weight = EstimateEntryBytes(rec.Target, rec.JobName);
                        entry = new Entry(rec.Target, rec.JobName, weight)
                        {
                            DueTimeUtcTicks = dueTicks,
                            Generation = ++_nextGeneration,
                        };
                        _entries.Add(key, entry);
                        _memoryBytes += weight;
                        HeapPush(entry);
                    }
                }
                else if (_entries.TryGetValue(key, out var existing))
                {
                    if (existing.InFlight)
                    {
                        // Keep entry in _entries until dispatch finishes to prevent ABA collisions and duplicate dispatch.
                        existing.DueTimeUtcTicks = null;
                        existing.Generation = ++_nextGeneration;
                        if (existing.HeapIndex >= 0)
                        {
                            HeapRemoveAt(existing.HeapIndex);
                        }
                    }
                    else
                    {
                        if (existing.HeapIndex >= 0)
                        {
                            HeapRemoveAt(existing.HeapIndex);
                        }

                        _ = _entries.Remove(key);
                        _memoryBytes -= existing.ByteWeight;
                    }
                }
            }

            if (_memoryBytes > _options.MaxWakeupBytesPerLedger && !_rollFlagged)
            {
                _rollFlagged = true;
                flagRoll = true;
            }

            if (_heap.Count > 0)
            {
                if (_timerLoop is null)
                {
                    using (ExecutionContext.SuppressFlow())
                    {
                        _timerLoop = Task.Run(RunTimerLoopAsync, CancellationToken.None);
                    }
                }
                else if (_heap[0].DueTimeUtcTicks!.Value < prevHeadTicks)
                {
                    toSignal = SwapSignalLocked();
                }
            }
        }

        if (flagRoll)
        {
            _handler.FlagLedgerNeedingRoll(_ledger);
        }

        _ = toSignal?.TrySetResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ = BeginDispose();
    }

    internal Task StopAndDisposeAsync()
    {
        var loop = BeginDispose();
        return loop ?? Task.CompletedTask;
    }

    private Task? BeginDispose()
    {
        TaskCompletionSource? toSignal;
        Task? loop;
        lock (_lock)
        {
            if (_disposed)
            {
                return _timerLoop;
            }

            _disposed = true;
            _heap.Clear();
            _entries.Clear();
            _memoryBytes = 0;
            toSignal = SwapSignalLocked();
            loop = _timerLoop;
        }

        _handler.UnregisterSession(_ledger, this);
        _cts.Cancel();
        _ = toSignal.TrySetResult();
        if (loop is null)
        {
            _cts.Dispose();
            _dispatchSlots.Dispose();
        }

        return loop;
    }

    private async Task RunTimerLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Entry? dueEntry = null;
                ulong dispatchedGeneration = 0;
                int attempt = 0;
                long dueTimeTicks = 0;
                Task? waitSignal = null;
                TimeSpan? delay = null;

                lock (_lock)
                {
                    if (_disposed)
                    {
                        break;
                    }

                    if (_heap.Count == 0)
                    {
                        waitSignal = _wakeSignal.Task;
                    }
                    else
                    {
                        var head = _heap[0];
                        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
                        var headTicks = head.DueTimeUtcTicks!.Value;
                        if (headTicks <= nowTicks)
                        {
                            HeapRemoveAt(0);
                            head.InFlight = true;
                            dueEntry = head;
                            dispatchedGeneration = head.Generation;
                            attempt = head.Attempt;
                            dueTimeTicks = headTicks;
                        }
                        else
                        {
                            delay = TimeSpan.FromTicks(headTicks - nowTicks);
                            waitSignal = _wakeSignal.Task;
                        }
                    }
                }

                if (dueEntry is not null)
                {
                    await _dispatchSlots.WaitAsync(token).ConfigureAwait(false);
                    if (!_handler.TryEnterDispatch())
                    {
                        _ = _dispatchSlots.Release();
                        break;
                    }

                    _ = DispatchEntryAsync(dueEntry, dispatchedGeneration, attempt, dueTimeTicks, token);
                    continue;
                }

                if (delay is { } waitDelay)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var delayTask = Task.Delay(waitDelay, _timeProvider, delayCts.Token);
                    _ = await Task.WhenAny(waitSignal!, delayTask).ConfigureAwait(false);
                    delayCts.Cancel();
                }
                else
                {
                    await waitSignal!.WaitAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _cts.Dispose();
            _dispatchSlots.Dispose();
        }
    }

    private async Task DispatchEntryAsync(Entry entry, ulong dispatchedGeneration, int attempt, long dueTimeTicks, CancellationToken token)
    {
        try
        {
            var job = new DurableJob
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"wakeup:{_ledger.Value}:{dispatchedGeneration}"),
                Name = entry.JobName,
                DueTime = new DateTimeOffset(dueTimeTicks, TimeSpan.Zero),
                TargetGrainId = entry.Target,
                ShardId = string.Create(CultureInfo.InvariantCulture, $"ledger:{_ledger.Value}"),
            };
            var context = new JobRunContext(job, Guid.NewGuid().ToString("N"), attempt + 1, isEphemeralWakeup: true);

            DurableJobRunResult? result = null;
            Exception? failure = null;
            try
            {
                result = await _dispatcher.DispatchAsync(entry.Target, context, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }

            TaskCompletionSource? toSignal = null;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                entry.InFlight = false;
                var key = (entry.Target, entry.JobName);

                if (entry.DueTimeUtcTicks is null)
                {
                    // Cleared while in flight: remove from _entries and never resurrect.
                    if (_entries.Remove(key))
                    {
                        _memoryBytes -= entry.ByteWeight;
                    }

                    return;
                }

                if (entry.Generation != dispatchedGeneration)
                {
                    // Newer SetWakeup arrived mid-dispatch: enqueue at its latest due time.
                    var prevHead = _heap.Count > 0 ? _heap[0].DueTimeUtcTicks!.Value : long.MaxValue;
                    HeapPush(entry);
                    if (_heap[0].DueTimeUtcTicks!.Value < prevHead || _heap.Count == 1)
                    {
                        toSignal = SwapSignalLocked();
                    }
                }
                else if (failure is null && result is { Status: DurableJobRunStatus.Completed })
                {
                    if (_entries.Remove(key))
                    {
                        _memoryBytes -= entry.ByteWeight;
                    }
                }
                else if (failure is null && result is { IsRescheduleRequested: true })
                {
                    entry.DueTimeUtcTicks = result.RescheduleTime.Value.UtcTicks;
                    entry.Attempt = 0;
                    var prevHead = _heap.Count > 0 ? _heap[0].DueTimeUtcTicks!.Value : long.MaxValue;
                    HeapPush(entry);
                    if (_heap[0].DueTimeUtcTicks!.Value < prevHead || _heap.Count == 1)
                    {
                        toSignal = SwapSignalLocked();
                    }
                }
                else if (failure is null && result is { IsInProgress: true })
                {
                    entry.DueTimeUtcTicks = _timeProvider.GetUtcNow().UtcTicks + result.PollAfterDelay.Value.Ticks;
                    var prevHead = _heap.Count > 0 ? _heap[0].DueTimeUtcTicks!.Value : long.MaxValue;
                    HeapPush(entry);
                    if (_heap[0].DueTimeUtcTicks!.Value < prevHead || _heap.Count == 1)
                    {
                        toSignal = SwapSignalLocked();
                    }
                }
                else
                {
                    entry.Attempt++;
                    var retryDelay = ComputeBackoff(entry.Attempt);
                    WakeupStreamHandler.LogWakeupDispatchRetry(_logger, entry.Target, entry.JobName, _ledger, entry.Attempt, retryDelay, failure ?? result?.Exception);
                    entry.DueTimeUtcTicks = _timeProvider.GetUtcNow().UtcTicks + retryDelay.Ticks;
                    var prevHead = _heap.Count > 0 ? _heap[0].DueTimeUtcTicks!.Value : long.MaxValue;
                    HeapPush(entry);
                    if (_heap[0].DueTimeUtcTicks!.Value < prevHead || _heap.Count == 1)
                    {
                        toSignal = SwapSignalLocked();
                    }
                }
            }

            _ = toSignal?.TrySetResult();
        }
        finally
        {
            try
            {
                _ = _dispatchSlots.Release();
            }
            catch (ObjectDisposedException)
            {
            }

            _handler.ExitDispatch();
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var shift = Math.Min(attempt - 1, 30);
        var scaled = _options.InitialRetryDelay.Ticks <= _options.MaxRetryDelay.Ticks >> shift
            ? _options.InitialRetryDelay.Ticks << shift
            : _options.MaxRetryDelay.Ticks;
        return TimeSpan.FromTicks(Math.Min(scaled, _options.MaxRetryDelay.Ticks));
    }

    private TaskCompletionSource SwapSignalLocked()
    {
        var current = _wakeSignal;
        _wakeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return current;
    }

    private static long EstimateEntryBytes(GrainId target, string jobName) =>
        BaseEntryOverheadBytes + target.Type.Value.AsSpan().Length + target.Key.AsSpan().Length + jobName.Length * 2L;

    // Indexed binary min-heap operations (all called under _lock, zero dead entries).
    private void HeapPush(Entry entry)
    {
        Debug.Assert(entry.HeapIndex == -1, "An entry already in the heap cannot be pushed twice.");
        var index = _heap.Count;
        entry.HeapIndex = index;
        _heap.Add(entry);
        _ = SiftUp(index);
    }

    private void HeapUpdate(int index)
    {
        if (!SiftUp(index))
        {
            SiftDown(index);
        }
    }

    private void HeapRemoveAt(int index)
    {
        var lastIndex = _heap.Count - 1;
        var removed = _heap[index];
        removed.HeapIndex = -1;
        if (index == lastIndex)
        {
            _heap.RemoveAt(lastIndex);
            return;
        }

        var last = _heap[lastIndex];
        _heap.RemoveAt(lastIndex);
        _heap[index] = last;
        last.HeapIndex = index;
        HeapUpdate(index);
    }

    private bool SiftUp(int index)
    {
        var moved = false;
        while (index > 0)
        {
            var parent = (index - 1) >> 1;
            if (_heap[index].DueTimeUtcTicks!.Value >= _heap[parent].DueTimeUtcTicks!.Value)
            {
                break;
            }

            Swap(index, parent);
            index = parent;
            moved = true;
        }

        return moved;
    }

    private void SiftDown(int index)
    {
        var count = _heap.Count;
        while (true)
        {
            var left = (index << 1) + 1;
            if (left >= count)
            {
                break;
            }

            var smallest = left;
            var right = left + 1;
            if (right < count && _heap[right].DueTimeUtcTicks!.Value < _heap[left].DueTimeUtcTicks!.Value)
            {
                smallest = right;
            }

            if (_heap[index].DueTimeUtcTicks!.Value <= _heap[smallest].DueTimeUtcTicks!.Value)
            {
                break;
            }

            Swap(index, smallest);
            index = smallest;
        }
    }

    private void Swap(int i, int j)
    {
        (_heap[i], _heap[j]) = (_heap[j], _heap[i]);
        _heap[i].HeapIndex = i;
        _heap[j].HeapIndex = j;
    }

    private sealed class Entry(GrainId target, string jobName, long byteWeight)
    {
        public GrainId Target { get; } = target;

        public string JobName { get; } = jobName;

        public long ByteWeight { get; } = byteWeight;

        public long? DueTimeUtcTicks;

        public ulong Generation;

        public int HeapIndex = -1;

        public int Attempt;

        public bool InFlight;
    }
}

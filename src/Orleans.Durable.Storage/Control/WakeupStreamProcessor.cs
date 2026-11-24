using System.Buffers;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Jobs;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// Control-plane <see cref="ILocalStreamProcessor"/> for <c>orleans.wakeup</c> (orleans.md §5.15, LG-I16): folds
/// closed-ledger wakeup companion records with last-write-wins semantics per <c>(Target, JobName)</c> and durably
/// schedules surviving entries through <see cref="ILocalDurableJobManager.ScheduleJobAsync"/> under a node-level
/// concurrency cap and deterministic overdue jitter.
/// </summary>
internal sealed class WakeupStreamProcessor : ILocalStreamProcessor, IDisposable
{
    private const long BaseEntryOverheadBytes = 96;

    private readonly ILocalDurableJobManager _jobManager;
    private readonly WakeupOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _scheduleConcurrency;

    /// <summary>Initializes a new instance of the <see cref="WakeupStreamProcessor"/> class.</summary>
    public WakeupStreamProcessor(
        ILocalDurableJobManager jobManager,
        WakeupOptions options,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _ = logger;
        options.Validate();
        _jobManager = jobManager;
        _options = options;
        _timeProvider = timeProvider;
        _scheduleConcurrency = new SemaphoreSlim(options.MaxConcurrentWakeupSchedules, options.MaxConcurrentWakeupSchedules);
    }

    /// <inheritdoc />
    public LocalStreamName Stream => WakeupStreamHandler.WakeupStream;

    /// <inheritdoc />
    public ILocalStreamFold BeginClosedLedgerFold(LedgerMetadata ledger, Slot tail) =>
        new WakeupFold(this, ledger.Id);

    /// <inheritdoc />
    public void Dispose() => _scheduleConcurrency.Dispose();

    /// <summary>
    /// Computes the effective due time ticks for a surviving wakeup: preserves future due times verbatim, and spreads
    /// overdue due times deterministically over <c>[now, now + OverdueJitterWindow)</c> using the target grain's uniform
    /// hash code.
    /// </summary>
    internal static long ComputeEffectiveDueTicks(long dueTicks, long nowTicks, GrainId target, string jobName, TimeSpan jitterWindow)
    {
        if (dueTicks >= nowTicks || jitterWindow <= TimeSpan.Zero)
        {
            return dueTicks < nowTicks ? nowTicks : dueTicks;
        }

        // Deterministic FNV-1a-style mix of GrainId.GetUniformHashCode() and jobName characters so all drainers agree.
        unchecked
        {
            var hash = target.GetUniformHashCode() ^ 2166136261u;
            foreach (var c in jobName)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            var offsetTicks = (long)(hash % (ulong)jitterWindow.Ticks);
            return Math.Min(DateTimeOffset.MaxValue.UtcTicks, nowTicks + offsetTicks);
        }
    }

    private sealed class WakeupFold(WakeupStreamProcessor owner, LedgerId ledger) : ILocalStreamFold
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<(GrainId Target, string JobName), long> _entries = [];
        private long _memoryBytes;

        /// <inheritdoc />
        public long MemoryBytes
        {
            get
            {
                lock (_sync)
                {
                    return _memoryBytes;
                }
            }
        }

        /// <inheritdoc />
        public void Add(Slot slot, int rowIndex, ReadOnlySequence<byte> payload)
        {
            if (WakeupCompanionCodec.Decode(payload, out var record) is { } error)
            {
                throw new ArchiveCorruptException(
                    string.Create(CultureInfo.InvariantCulture, $"Ledger {ledger} slot {slot} row {rowIndex} contains a malformed '{WakeupCompanionCodec.StreamName}' payload: {error.Message}"),
                    error);
            }

            var key = (record.Target, WakeupStreamHandler.InternJobName(record.JobName));
            var weight = EstimateEntryBytes(key.Target, key.Item2);
            lock (_sync)
            {
                if (record.DueTimeUtcTicks is { } dueTicks)
                {
                    if (!_entries.ContainsKey(key))
                    {
                        _memoryBytes += weight;
                    }

                    _entries[key] = dueTicks;
                }
                else if (_entries.Remove(key))
                {
                    _memoryBytes -= weight;
                }
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            lock (_sync)
            {
                _entries.Clear();
                _memoryBytes = 0;
            }
        }

        /// <inheritdoc />
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            KeyValuePair<(GrainId Target, string JobName), long>[] snapshot;
            lock (_sync)
            {
                if (_entries.Count == 0)
                {
                    return;
                }

                snapshot = [.. _entries];
            }

            var tasks = new Task[snapshot.Length];
            for (var i = 0; i < snapshot.Length; i++)
            {
                var entry = snapshot[i];
                tasks[i] = ScheduleOneAsync(entry.Key, entry.Value, cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Dispose() => Clear();

        private async Task ScheduleOneAsync((GrainId Target, string JobName) key, long dueTicks, CancellationToken cancellationToken)
        {
            await owner._scheduleConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var nowTicks = owner._timeProvider.GetUtcNow().UtcTicks;
                var effectiveTicks = ComputeEffectiveDueTicks(dueTicks, nowTicks, key.Target, key.JobName, owner._options.OverdueJitterWindow);
                var request = new ScheduleJobRequest
                {
                    Target = key.Target,
                    JobName = key.JobName,
                    DueTime = new DateTimeOffset(effectiveTicks, TimeSpan.Zero),
                };
                _ = await owner._jobManager.ScheduleJobAsync(request, cancellationToken).ConfigureAwait(false);

                // Remove on success so retries skip already-scheduled entries and reclaim memory immediately.
                var weight = EstimateEntryBytes(key.Target, key.JobName);
                lock (_sync)
                {
                    if (_entries.TryGetValue(key, out var current) && current == dueTicks && _entries.Remove(key))
                    {
                        _memoryBytes -= weight;
                    }
                }
            }
            finally
            {
                _ = owner._scheduleConcurrency.Release();
            }
        }

        private static long EstimateEntryBytes(GrainId target, string jobName) =>
            BaseEntryOverheadBytes + target.Type.Value.AsSpan().Length + target.Key.AsSpan().Length + jobName.Length * 2L;
    }
}

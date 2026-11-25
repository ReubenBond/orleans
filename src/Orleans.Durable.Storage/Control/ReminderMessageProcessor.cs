using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Jobs;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// Control-plane <see cref="IMessageProcessor"/> for <c>orleans.reminder</c> (orleans.md §5.15): folds each segment's
/// reminder records by <c>(GrainId Target, string JobName)</c> during <c>DrainScan</c> and publishes surviving entries
/// as deterministic, time-bucketed sealed <c>JobShard</c>s (1 atomic blob per non-empty bucket; 0 writes when 0
/// entries survive in the segment).
/// </summary>
internal sealed class ReminderMessageProcessor : IMessageProcessor, IDisposable
{
    private const long EntryOverheadBytes = 128;

    private readonly ISealedJobShardPublisher _publisher;
    private readonly ReminderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _publishGate;

    /// <summary>Initializes a new instance of the <see cref="ReminderMessageProcessor"/> class.</summary>
    public ReminderMessageProcessor(
        ISealedJobShardPublisher publisher,
        ReminderOptions options,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _publisher = publisher;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger.Instance;
        _publishGate = new SemaphoreSlim(options.MaxConcurrentReminderPublishes, options.MaxConcurrentReminderPublishes);
    }

    /// <inheritdoc />
    public MessageChannel Channel => ReminderMessageHandler.ReminderChannel;

    /// <inheritdoc />
    public IMessageFold BeginSegmentFold(LedgerMetadata ledger, LedgerSegmentId segment) =>
        new MessageFold(this, ledger, segment);

    /// <inheritdoc />
    public void Dispose() => _publishGate.Dispose();

    /// <summary>
    /// Computes deterministic overdue jitter in <c>[0, MaxOverdueJitter]</c> from <paramref name="target"/> and
    /// <paramref name="jobName"/> so racing drainers produce identical due times and spread post-crash load.
    /// </summary>
    internal TimeSpan ComputeOverdueJitter(GrainId target, string jobName)
    {
        var maxTicks = _options.OverdueJitterWindow.Ticks;
        if (maxTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        var hash = new XxHash64();
        hash.Append(target.Type.Value.AsSpan());
        hash.Append([(byte)'/']);
        hash.Append(target.Key.Value.Span);
        hash.Append([(byte)'|']);
        var maxUtf8 = Encoding.UTF8.GetMaxByteCount(jobName.Length);
        Span<byte> nameBuf = maxUtf8 <= 512 ? stackalloc byte[maxUtf8] : new byte[maxUtf8];
        var written = Encoding.UTF8.GetBytes(jobName, nameBuf);
        hash.Append(nameBuf[..written]);
        var value = hash.GetCurrentHashAsUInt64();
        return TimeSpan.FromTicks((long)(value % (ulong)(maxTicks + 1)));
    }

    internal static string DeterministicShardId(LedgerId ledger, Slot segmentStart, long bucketUtcTicks) =>
        string.Create(CultureInfo.InvariantCulture, $"drain-{ledger.Value:D20}-{segmentStart.Value:D20}-{bucketUtcTicks:D19}");

    private sealed class MessageFold(ReminderMessageProcessor owner, LedgerMetadata ledger, LedgerSegmentId segment) : IMessageFold
    {
        private readonly Dictionary<(GrainId Target, string JobName), long> _entries = [];
        private readonly HashSet<long> _publishedBuckets = [];
        private long _memoryBytes;

        public long MemoryBytes => _memoryBytes;

        public void Add(Slot slot, int rowIndex, ReadOnlySequence<byte> payload)
        {
            var rejection = ReminderMessageCodec.Decode(payload, ReminderMessageHandler.TryInternJobName, out var record);
            if (rejection is not null)
            {
                throw new ArchiveCorruptException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Corrupt orleans.reminder message in ledger {ledger.Id} segment {segment.Start} slot {slot} row {rowIndex}: {rejection.Message}"),
                    rejection);
            }

            var key = (record.Target, record.JobName);
            if (record.DueTimeUtcTicks is { } dueTicks)
            {
                if (_entries.TryAdd(key, dueTicks))
                {
                    _memoryBytes += EstimateEntryBytes(record.Target, record.JobName);
                }
                else
                {
                    _entries[key] = dueTicks;
                }
            }
            else if (_entries.Remove(key))
            {
                _memoryBytes -= EstimateEntryBytes(record.Target, record.JobName);
                if (_memoryBytes < 0)
                {
                    _memoryBytes = 0;
                }
            }
        }

        public void Clear()
        {
            _entries.Clear();
            _publishedBuckets.Clear();
            _memoryBytes = 0;
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            var now = owner._timeProvider.GetUtcNow();
            var shardDurationTicks = owner._publisher.ShardDuration.Ticks;
            if (shardDurationTicks <= 0)
            {
                throw new InvalidOperationException($"ISealedJobShardPublisher.ShardDuration must be positive, got {owner._publisher.ShardDuration}.");
            }

            var buckets = new SortedDictionary<long, List<(string TargetText, GrainId Target, string JobName, DateTimeOffset DueTime)>>();
            foreach (var ((target, jobName), dueTicks) in _entries)
            {
                DateTimeOffset effectiveDue;
                if (dueTicks > now.UtcTicks)
                {
                    effectiveDue = new DateTimeOffset(dueTicks, TimeSpan.Zero);
                }
                else
                {
                    var jitter = owner.ComputeOverdueJitter(target, jobName);
                    var clampedTicks = Math.Min(DateTimeOffset.MaxValue.UtcTicks, now.UtcTicks + jitter.Ticks);
                    effectiveDue = new DateTimeOffset(clampedTicks, TimeSpan.Zero);
                }

                var bucketTicks = effectiveDue.UtcTicks / shardDurationTicks * shardDurationTicks;
                if (!buckets.TryGetValue(bucketTicks, out var list))
                {
                    list = [];
                    buckets[bucketTicks] = list;
                }

                list.Add((target.ToString(), target, jobName, effectiveDue));
            }

            foreach (var (bucketTicks, items) in buckets)
            {
                if (_publishedBuckets.Contains(bucketTicks))
                {
                    continue;
                }

                items.Sort(static (left, right) =>
                {
                    var cmp = StringComparer.Ordinal.Compare(left.TargetText, right.TargetText);
                    return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(left.JobName, right.JobName);
                });

                var requests = new ScheduleJobRequest[items.Count];
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    requests[i] = new ScheduleJobRequest
                    {
                        Target = item.Target,
                        JobName = item.JobName,
                        DueTime = item.DueTime,
                    };
                }

                var bucketStart = new DateTimeOffset(bucketTicks, TimeSpan.Zero);
                var bucketEndTicks = bucketTicks > DateTimeOffset.MaxValue.UtcTicks - shardDurationTicks
                    ? DateTimeOffset.MaxValue.UtcTicks
                    : bucketTicks + shardDurationTicks;
                var bucketEnd = new DateTimeOffset(bucketEndTicks, TimeSpan.Zero);
                var deterministicId = DeterministicShardId(ledger.Id, segment.Start, bucketTicks);

                await owner._publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _ = await owner._publisher.PublishSealedShardAsync(
                        deterministicId,
                        bucketStart,
                        bucketEnd,
                        requests,
                        cancellationToken).ConfigureAwait(false);
                    _ = _publishedBuckets.Add(bucketTicks);
                    foreach (var item in items)
                    {
                        if (_entries.Remove((item.Target, item.JobName)))
                        {
                            _memoryBytes -= EstimateEntryBytes(item.Target, item.JobName);
                        }
                    }
                }
                finally
                {
                    _ = owner._publishGate.Release();
                }
            }

            if (_entries.Count == 0)
            {
                _memoryBytes = 0;
            }
        }

        public void Dispose() => Clear();

        private static long EstimateEntryBytes(GrainId target, string jobName) =>
            EntryOverheadBytes + target.Type.Value.AsSpan().Length + target.Key.Value.Length + jobName.Length * sizeof(char);
    }
}

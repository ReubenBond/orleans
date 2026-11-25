using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Durable.Storage.Bench;

/// <summary>
/// Benchmark journaled grain for S1 (raw write), S2 (large journals / hot-cold mix / cold read), and S6 (failover).
/// </summary>
public interface IBenchJournalGrain : IGrainWithStringKey
{
    Task WriteAsync(byte[] payload);

    Task<long> IncrementAndWriteAsync(byte[] payload);

    Task<int> ReadLengthAsync();

    Task<long> ReadVersionAsync();

    Task DeactivateNowAsync();
}

[JournalPlacement]
internal sealed class BenchJournalGrain(
    IDurableStateManager state,
    [FromKeyedServices("v")] IDurableValue<byte[]> value,
    [FromKeyedServices("ver")] IDurableValue<long> version) : Grain, IBenchJournalGrain
{
    public async Task WriteAsync(byte[] payload)
    {
        value.Value = payload;
        await state.WriteStateAsync();
    }

    public async Task<long> IncrementAndWriteAsync(byte[] payload)
    {
        var next = version.Value + 1;
        version.Value = next;
        value.Value = payload;
        await state.WriteStateAsync();
        return next;
    }

    public Task<int> ReadLengthAsync() => Task.FromResult(value.Value?.Length ?? 0);

    public Task<long> ReadVersionAsync() => Task.FromResult(version.Value);

    public Task DeactivateNowAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Benchmark grain combining journaled state with grain-local <see cref="IDurableJobQueue"/> for S3, S4a, S4b, and S6.
/// </summary>
public interface IBenchJobGrain : IGrainWithStringKey
{
    Task ScheduleWithStateAsync(string jobId, string runTag, int delayMs);

    Task<bool> CancelWithStateAsync(string jobId);

    Task<int> GetPendingCountAsync();

    Task<long> GetSequenceAsync();

    Task DeactivateNowAsync();
}

[JournalPlacement]
internal sealed class BenchJobGrain(
    IDurableStateManager state,
    [FromKeyedServices("seq")] IDurableValue<long> sequence,
    IDurableJobQueue jobQueue,
    BenchJobCounters counters,
    TimeProvider timeProvider) : Grain, IBenchJobGrain, IDurableJobQueueHandler
{
    public async Task ScheduleWithStateAsync(string jobId, string runTag, int delayMs)
    {
        sequence.Value = sequence.Value + 1;
        var dueTime = timeProvider.GetUtcNow().AddMilliseconds(delayMs);
        _ = jobQueue.Schedule(new GrainJobRequest
        {
            JobId = jobId,
            JobName = "bench-job",
            DueTime = dueTime,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["run"] = runTag,
                ["dueTicks"] = dueTime.UtcTicks.ToString(CultureInfo.InvariantCulture),
            },
        });
        await state.WriteStateAsync();
    }

    public async Task<bool> CancelWithStateAsync(string jobId)
    {
        sequence.Value = sequence.Value + 1;
        var cancelled = jobQueue.Cancel(jobId);
        await state.WriteStateAsync();
        return cancelled;
    }

    public Task<int> GetPendingCountAsync() => Task.FromResult(jobQueue.Count);

    public Task<long> GetSequenceAsync() => Task.FromResult(sequence.Value);

    public Task DeactivateNowAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var runTag = "unknown";
        var dueTicks = context.Job.DueTime.UtcTicks;

        if (context.Job.Metadata is { } metadata)
        {
            if (metadata.TryGetValue("run", out var tag) && !string.IsNullOrEmpty(tag))
            {
                runTag = tag;
            }

            if (metadata.TryGetValue("dueTicks", out var ticksStr)
                && long.TryParse(ticksStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTicks))
            {
                dueTicks = parsedTicks;
            }
        }

        var lagMs = (nowUtc.UtcTicks - dueTicks) / (double)TimeSpan.TicksPerMillisecond;
        counters.RecordExecution(runTag, lagMs, context.IsEphemeralReminder);
        return ValueTask.FromResult(DurableJobRunResult.Completed);
    }
}

/// <summary>
/// Per-silo singleton tracking job executions and fire-delay samples without activating job grains.
/// </summary>
public sealed class BenchJobCounters
{
    private const int MaxLagSamplesPerRun = 200_000;

    private long _totalExecuted;
    private long _totalEphemeral;
    private long _totalBackingShard;
    private readonly ConcurrentDictionary<string, RunCounters> _byRun = new(StringComparer.Ordinal);

    public void RecordExecution(string runTag, double lagMs, bool isEphemeralReminder)
    {
        Interlocked.Increment(ref _totalExecuted);
        if (isEphemeralReminder)
        {
            Interlocked.Increment(ref _totalEphemeral);
        }
        else
        {
            Interlocked.Increment(ref _totalBackingShard);
        }

        var run = _byRun.GetOrAdd(runTag, static _ => new RunCounters());
        run.Record(lagMs, isEphemeralReminder);
    }

    public JobCounterSnapshot Snapshot(string? runTag = null)
    {
        if (string.IsNullOrEmpty(runTag))
        {
            var runCounts = new Dictionary<string, long>(_byRun.Count, StringComparer.Ordinal);
            foreach (var (tag, stats) in _byRun)
            {
                runCounts[tag] = stats.Executed;
            }

            return new JobCounterSnapshot(
                TotalExecuted: Interlocked.Read(ref _totalExecuted),
                TotalEphemeralReminder: Interlocked.Read(ref _totalEphemeral),
                TotalBackingShard: Interlocked.Read(ref _totalBackingShard),
                RunTag: null,
                RunExecuted: 0,
                RunEphemeralReminder: 0,
                RunBackingShard: 0,
                RunLagSamplesMs: [],
                CountsByRun: runCounts);
        }

        if (_byRun.TryGetValue(runTag, out var runStats))
        {
            var (executed, ephemeral, backing, samples) = runStats.Snapshot();
            return new JobCounterSnapshot(
                TotalExecuted: Interlocked.Read(ref _totalExecuted),
                TotalEphemeralReminder: Interlocked.Read(ref _totalEphemeral),
                TotalBackingShard: Interlocked.Read(ref _totalBackingShard),
                RunTag: runTag,
                RunExecuted: executed,
                RunEphemeralReminder: ephemeral,
                RunBackingShard: backing,
                RunLagSamplesMs: samples,
                CountsByRun: []);
        }

        return new JobCounterSnapshot(
            TotalExecuted: Interlocked.Read(ref _totalExecuted),
            TotalEphemeralReminder: Interlocked.Read(ref _totalEphemeral),
            TotalBackingShard: Interlocked.Read(ref _totalBackingShard),
            RunTag: runTag,
            RunExecuted: 0,
            RunEphemeralReminder: 0,
            RunBackingShard: 0,
            RunLagSamplesMs: [],
            CountsByRun: []);
    }

    private sealed class RunCounters
    {
        private long _executed;
        private long _ephemeral;
        private long _backing;
        private readonly Lock _sampleLock = new();
        private readonly List<double> _lagSamples = [];

        public long Executed => Interlocked.Read(ref _executed);

        public void Record(double lagMs, bool isEphemeralReminder)
        {
            var n = Interlocked.Increment(ref _executed);
            if (isEphemeralReminder)
            {
                Interlocked.Increment(ref _ephemeral);
            }
            else
            {
                Interlocked.Increment(ref _backing);
            }

            lock (_sampleLock)
            {
                if (_lagSamples.Count < MaxLagSamplesPerRun)
                {
                    _lagSamples.Add(lagMs);
                }
                else
                {
                    var idx = Random.Shared.NextInt64(n);
                    if (idx < MaxLagSamplesPerRun)
                    {
                        _lagSamples[(int)idx] = lagMs;
                    }
                }
            }
        }

        public (long Executed, long Ephemeral, long Backing, double[] Samples) Snapshot()
        {
            double[] samples;
            lock (_sampleLock)
            {
                samples = [.. _lagSamples];
            }

            return (
                Interlocked.Read(ref _executed),
                Interlocked.Read(ref _ephemeral),
                Interlocked.Read(ref _backing),
                samples);
        }
    }
}

[GenerateSerializer]
[Immutable]
public sealed record JobCounterSnapshot(
    [property: Id(0)] long TotalExecuted,
    [property: Id(1)] long TotalEphemeralReminder,
    [property: Id(2)] long TotalBackingShard,
    [property: Id(3)] string? RunTag,
    [property: Id(4)] long RunExecuted,
    [property: Id(5)] long RunEphemeralReminder,
    [property: Id(6)] long RunBackingShard,
    [property: Id(7)] double[] RunLagSamplesMs,
    [property: Id(8)] Dictionary<string, long> CountsByRun);

/// <summary>
/// Fallback placement director for <see cref="JournalPlacement"/> used in <see cref="BenchMode.Baseline"/> so the exact
/// same <c>[JournalPlacement]</c> grain classes run identically without Durable Storage registered.
/// </summary>
internal sealed class FallbackJournalPlacementDirector : IPlacementDirector
{
    public Task<SiloAddress> OnAddActivation(
        PlacementStrategy strategy,
        PlacementTarget target,
        IPlacementContext context)
    {
        var compatible = context.GetCompatibleSilos(target);
        if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatible) is { } hint)
        {
            return Task.FromResult(hint);
        }

        return Task.FromResult(compatible[Random.Shared.Next(compatible.Length)]);
    }
}

/// <summary>
/// Source-generated JSON metadata for <c>UseJsonJournalFormat</c> (zero reflection).
/// </summary>
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(Guid))]
internal sealed partial class BenchJsonContext : JsonSerializerContext;

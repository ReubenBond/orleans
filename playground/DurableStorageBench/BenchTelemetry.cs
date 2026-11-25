using System.Diagnostics;
using Orleans.Concurrency;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Durable.Storage.Bench;

[GenerateSerializer]
[Immutable]
[SuppressReferenceTracking]
public sealed class SiloKeyPlacement : PlacementStrategy
{
    public static SiloKeyPlacement Instance { get; } = new();
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class SiloKeyPlacementAttribute() : PlacementAttribute(SiloKeyPlacement.Instance);

internal sealed class SiloKeyPlacementDirector : IPlacementDirector
{
    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var compatible = context.GetCompatibleSilos(target);
        var key = target.GrainIdentity.Key.ToString();
        if (!string.IsNullOrEmpty(key))
        {
            try
            {
                var targetSilo = SiloAddress.FromParsableString(key);
                if (Array.IndexOf(compatible, targetSilo) >= 0)
                {
                    return Task.FromResult(targetSilo);
                }
            }
            catch
            {
                // Fall back to first compatible silo.
            }
        }

        return Task.FromResult(compatible[0]);
    }
}

[GenerateSerializer]
[Immutable]
public sealed record SiloMemorySnapshot(
    [property: Id(0)] string SiloAddress,
    [property: Id(1)] string SiloName,
    [property: Id(2)] long WorkingSetBytes,
    [property: Id(3)] long GcTotalMemoryBytes,
    [property: Id(4)] long HeapSizeBytes,
    [property: Id(5)] long TotalCommittedBytes);

[GenerateSerializer]
[Immutable]
public sealed record SiloTelemetrySnapshot(
    [property: Id(0)] string SiloAddress,
    [property: Id(1)] string SiloName,
    [property: Id(2)] BlobCounterSnapshot BlobCounters,
    [property: Id(3)] JobCounterSnapshot JobCounters,
    [property: Id(4)] SiloMemorySnapshot Memory);

[GenerateSerializer]
[Immutable]
public sealed record SiloSamplesResponse(
    [property: Id(0)] string SiloAddress,
    [property: Id(1)] string SiloName,
    [property: Id(2)] List<SiloTelemetrySample> Samples,
    [property: Id(3)] List<BenchLifecycleEvent> Events);

/// <summary>
/// One lightweight telemetry grain per active silo, placed directly on that silo via <see cref="SiloKeyPlacement"/>,
/// enabling cluster-wide blob, job, memory, and 1-second runtime telemetry aggregation without activating benchmark grains.
/// </summary>
public interface IBenchSiloTelemetryGrain : IGrainWithStringKey
{
    [AlwaysInterleave]
    Task<SiloTelemetrySnapshot> GetTelemetryAsync(string? runTag = null);

    [AlwaysInterleave]
    Task<SiloSamplesResponse> GetSamplesAsync(DateTimeOffset? sinceUtc = null);
}

[SiloKeyPlacement]
internal sealed class BenchSiloTelemetryGrain(
    ILocalSiloDetails localSilo,
    BlobRequestCounterPolicy blobPolicy,
    BenchJobCounters jobCounters,
    SiloTelemetrySampler sampler,
    BenchEventLogStore eventLog) : Grain, IBenchSiloTelemetryGrain
{
    public Task<SiloTelemetrySnapshot> GetTelemetryAsync(string? runTag = null)
    {
        using var proc = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        var mem = new SiloMemorySnapshot(
            SiloAddress: localSilo.SiloAddress.ToParsableString(),
            SiloName: localSilo.Name,
            WorkingSetBytes: proc.WorkingSet64,
            GcTotalMemoryBytes: GC.GetTotalMemory(forceFullCollection: false),
            HeapSizeBytes: gcInfo.HeapSizeBytes,
            TotalCommittedBytes: gcInfo.TotalCommittedBytes);

        return Task.FromResult(new SiloTelemetrySnapshot(
            SiloAddress: localSilo.SiloAddress.ToParsableString(),
            SiloName: localSilo.Name,
            BlobCounters: blobPolicy.Snapshot(),
            JobCounters: jobCounters.Snapshot(runTag),
            Memory: mem));
    }

    public Task<SiloSamplesResponse> GetSamplesAsync(DateTimeOffset? sinceUtc = null) =>
        Task.FromResult(new SiloSamplesResponse(
            SiloAddress: localSilo.SiloAddress.ToParsableString(),
            SiloName: localSilo.Name,
            Samples: sampler.GetSamples(sinceUtc),
            Events: eventLog.GetEvents(sinceUtc)));
}

public sealed record ClusterTelemetrySnapshot(
    BlobCounterSnapshot AggregateBlobs,
    Dictionary<string, BlobCounterSnapshot> BlobsBySilo,
    long TotalJobExecuted,
    long RunJobExecuted,
    long RunEphemeralReminderExecutions,
    long RunBackingShardExecutions,
    List<double> RunLagSamplesMs,
    Dictionary<string, long> CountsByRun,
    List<SiloMemorySnapshot> MemoryBySilo,
    List<string> UnreachableSilos);

public sealed record ClusterBlobDiff(
    BlobCounterSnapshot Total,
    Dictionary<string, BlobCounterSnapshot> BySilo,
    List<string> UnreachableSilos);

/// <summary>
/// Aggregates blob counters, job execution counters, memory, and 1-second runtime telemetry across all active silos
/// with bounded per-silo timeouts so a dead/crashed silo never blocks the coordinator.
/// </summary>
public sealed class ClusterTelemetryAggregator(
    IClusterMembershipService membership,
    ILocalSiloDetails localSilo,
    IGrainFactory grainFactory,
    BlobRequestCounterPolicy localBlobPolicy,
    BenchJobCounters localJobCounters,
    SiloTelemetrySampler localSampler,
    BenchEventLogStore localEventLog,
    ILogger<ClusterTelemetryAggregator> logger)
{
    private static readonly TimeSpan PerSiloTimeout = TimeSpan.FromSeconds(2);

    public IReadOnlyList<SiloAddress> GetActiveSilos()
    {
        var list = membership.CurrentSnapshot.Members.Values
            .Where(static m => m.Status == SiloStatus.Active)
            .Select(static m => m.SiloAddress)
            .OrderBy(static s => s.ToParsableString(), StringComparer.Ordinal)
            .ToList();

        if (!list.Contains(localSilo.SiloAddress))
        {
            list.Add(localSilo.SiloAddress);
        }

        return list;
    }

    public async Task<ClusterTelemetrySnapshot> SnapshotAsync(string? runTag = null, CancellationToken cancellationToken = default)
    {
        var silos = GetActiveSilos();
        var tasks = silos.Select(s => QuerySiloTelemetryAsync(s, runTag, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var blobsBySilo = new Dictionary<string, BlobCounterSnapshot>(StringComparer.Ordinal);
        var memoryBySilo = new List<SiloMemorySnapshot>();
        var unreachable = new List<string>();
        var lagSamples = new List<double>();
        var countsByRun = new Dictionary<string, long>(StringComparer.Ordinal);
        long totalExec = 0, runExec = 0, runEph = 0, runBacking = 0;

        foreach (var (siloAddr, snapshot) in results)
        {
            if (snapshot is null)
            {
                unreachable.Add(siloAddr);
                continue;
            }

            blobsBySilo[siloAddr] = snapshot.BlobCounters;
            memoryBySilo.Add(snapshot.Memory);
            totalExec += snapshot.JobCounters.TotalExecuted;
            runExec += snapshot.JobCounters.RunExecuted;
            runEph += snapshot.JobCounters.RunEphemeralReminder;
            runBacking += snapshot.JobCounters.RunBackingShard;
            if (snapshot.JobCounters.RunLagSamplesMs.Length > 0)
            {
                lagSamples.AddRange(snapshot.JobCounters.RunLagSamplesMs);
            }

            foreach (var (tag, count) in snapshot.JobCounters.CountsByRun)
            {
                countsByRun[tag] = countsByRun.GetValueOrDefault(tag) + count;
            }
        }

        return new ClusterTelemetrySnapshot(
            AggregateBlobs: BlobCounterSnapshot.Merge(blobsBySilo.Values),
            BlobsBySilo: blobsBySilo,
            TotalJobExecuted: totalExec,
            RunJobExecuted: runExec,
            RunEphemeralReminderExecutions: runEph,
            RunBackingShardExecutions: runBacking,
            RunLagSamplesMs: lagSamples,
            CountsByRun: countsByRun,
            MemoryBySilo: memoryBySilo,
            UnreachableSilos: unreachable);
    }

    public async Task<(List<SiloTelemetrySummary> Summaries, List<BenchLifecycleEvent> Events, List<string> UnreachableSilos)> CollectSiloTelemetryAsync(
        DateTimeOffset sinceUtc,
        bool includeSeries,
        CancellationToken cancellationToken = default)
    {
        var silos = GetActiveSilos();
        var tasks = silos.Select(s => QuerySiloSamplesAsync(s, sinceUtc, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var summaries = new List<SiloTelemetrySummary>();
        var events = new List<BenchLifecycleEvent>();
        var unreachable = new List<string>();

        foreach (var (siloAddr, response) in results)
        {
            if (response is null)
            {
                unreachable.Add(siloAddr);
                continue;
            }

            summaries.Add(SiloTelemetrySampler.Summarize(
                response.SiloAddress,
                response.SiloName,
                response.Samples,
                includeSeries));

            if (response.Events.Count > 0)
            {
                events.AddRange(response.Events);
            }
        }

        events.Sort(static (a, b) => a.UtcTimestamp.CompareTo(b.UtcTimestamp));
        return (summaries, events, unreachable);
    }

    public static ClusterBlobDiff DiffBlobs(ClusterTelemetrySnapshot before, ClusterTelemetrySnapshot after)
    {
        var bySilo = new Dictionary<string, BlobCounterSnapshot>(StringComparer.Ordinal);
        foreach (var (silo, afterSnap) in after.BlobsBySilo)
        {
            var beforeSnap = before.BlobsBySilo.GetValueOrDefault(silo) ?? BlobCounterSnapshot.Empty;
            bySilo[silo] = BlobCounterSnapshot.Diff(beforeSnap, afterSnap);
        }

        return new ClusterBlobDiff(
            Total: BlobCounterSnapshot.Merge(bySilo.Values),
            BySilo: bySilo,
            UnreachableSilos: after.UnreachableSilos);
    }

    private async Task<(string SiloAddress, SiloTelemetrySnapshot? Snapshot)> QuerySiloTelemetryAsync(
        SiloAddress silo,
        string? runTag,
        CancellationToken cancellationToken)
    {
        var addr = silo.ToParsableString();
        if (silo.Equals(localSilo.SiloAddress))
        {
            using var proc = Process.GetCurrentProcess();
            var gcInfo = GC.GetGCMemoryInfo();
            var mem = new SiloMemorySnapshot(
                addr,
                localSilo.Name,
                proc.WorkingSet64,
                GC.GetTotalMemory(false),
                gcInfo.HeapSizeBytes,
                gcInfo.TotalCommittedBytes);
            return (addr, new SiloTelemetrySnapshot(addr, localSilo.Name, localBlobPolicy.Snapshot(), localJobCounters.Snapshot(runTag), mem));
        }

        try
        {
            var grain = grainFactory.GetGrain<IBenchSiloTelemetryGrain>(addr);
            var result = await grain.GetTelemetryAsync(runTag).WaitAsync(PerSiloTimeout, cancellationToken).ConfigureAwait(false);
            return (addr, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Telemetry query to silo {Silo} timed out or failed.", addr);
            return (addr, null);
        }
    }

    private async Task<(string SiloAddress, SiloSamplesResponse? Response)> QuerySiloSamplesAsync(
        SiloAddress silo,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var addr = silo.ToParsableString();
        if (silo.Equals(localSilo.SiloAddress))
        {
            return (addr, new SiloSamplesResponse(addr, localSilo.Name, localSampler.GetSamples(sinceUtc), localEventLog.GetEvents(sinceUtc)));
        }

        try
        {
            var grain = grainFactory.GetGrain<IBenchSiloTelemetryGrain>(addr);
            var result = await grain.GetSamplesAsync(sinceUtc).WaitAsync(PerSiloTimeout, cancellationToken).ConfigureAwait(false);
            return (addr, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Samples query to silo {Silo} timed out or failed.", addr);
            return (addr, null);
        }
    }
}

public sealed record LatencyStats(
    long Count,
    double MinMs,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double P999Ms,
    double MaxMs)
{
    public static LatencyStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public static LatencyStats FromSamples(List<double> samples)
    {
        if (samples.Count == 0)
        {
            return Empty;
        }

        samples.Sort();
        double sum = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            sum += samples[i];
        }

        return new LatencyStats(
            Count: samples.Count,
            MinMs: Math.Round(samples[0], 3),
            MeanMs: Math.Round(sum / samples.Count, 3),
            P50Ms: Math.Round(Pick(samples, 0.50), 3),
            P95Ms: Math.Round(Pick(samples, 0.95), 3),
            P99Ms: Math.Round(Pick(samples, 0.99), 3),
            P999Ms: Math.Round(Pick(samples, 0.999), 3),
            MaxMs: Math.Round(samples[^1], 3));
    }

    private static double Pick(List<double> sorted, double percentile)
    {
        var idx = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}

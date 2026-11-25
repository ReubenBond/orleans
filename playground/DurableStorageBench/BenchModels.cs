using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;

namespace Orleans.Durable.Storage.Bench;

public sealed class BenchRunRequest
{
    public string Scenario { get; set; } = "s1-raw-write";

    public int Grains { get; set; } = 1000;

    public int Concurrency { get; set; } = 64;

    public int DurationSeconds { get; set; } = 30;

    public int WarmupSeconds { get; set; } = 5;

    public int PayloadBytes { get; set; } = 1024;

    public int DelayMs { get; set; } = 1500;

    public bool DeactivateAfterSchedule { get; set; } = true;

    public bool RollAfterSchedule { get; set; }

    public bool RollAndDrain { get; set; }

    public int? ColdSampleGrains { get; set; }

    public double ReadRatio { get; set; } = 0.5;

    public double ZipfExponent { get; set; } = 1.0;

    public int WaitTimeoutSeconds { get; set; } = 120;

    public int JobCount { get; set; } = 200;

    public int VerifyTimeoutSeconds { get; set; } = 120;

    public int RetryBackoffMs { get; set; } = 100;

    public int CallTimeoutMs { get; set; } = 5000;

    public bool Distributed { get; set; }

    public bool LocalPrimaryOnly { get; set; }

    public bool IsolateWorkersByPrimary { get; set; }

    public List<string>? DriverSilos { get; set; }

    public List<string>? ExcludeSilos { get; set; }

    public string? RunTag { get; set; }
}

public sealed class LocalBenchRequest
{
    public string Scenario { get; set; } = "s1-raw-write";

    public string RunTag { get; set; } = string.Empty;

    public int DriverIndex { get; set; }

    public int Grains { get; set; } = 1000;

    public int Concurrency { get; set; } = 32;

    public int DurationSeconds { get; set; } = 30;

    public int WarmupSeconds { get; set; } = 5;

    public int PayloadBytes { get; set; } = 1024;

    public int DelayMs { get; set; } = 1500;

    public bool DeactivateAfterSchedule { get; set; } = true;

    public double ReadRatio { get; set; } = 0.5;

    public double ZipfExponent { get; set; } = 1.0;

    public int ColdSampleGrains { get; set; }

    public bool LocalPrimaryOnly { get; set; } = true;

    public bool IsolateWorkersByPrimary { get; set; }

    public int RetryBackoffMs { get; set; } = 100;

    public int CallTimeoutMs { get; set; } = 5000;

    public int VerifyTimeoutSeconds { get; set; } = 120;

    public double ExpectedKeyspaceFraction { get; set; }

    public int LocalLedgerCount { get; set; }

    public int MaxLatencySamples { get; set; } = 50_000;
}

public sealed record LocalBenchResponse(
    string SiloAddress,
    string SiloName,
    int? Ordinal,
    double KeyspaceFraction,
    int LocalLedgerCount,
    int LocalGrains,
    int LocalConcurrency,
    long Ops,
    long Errors,
    double ElapsedMs,
    double OpsPerSec,
    long ReadOps,
    long WriteOps,
    LatencyStats LatencyMs,
    double[] LatencySamplesMs,
    LatencyStats? SecondaryLatencyMs,
    double[]? SecondaryLatencySamplesMs,
    List<FailoverSecondBucket>? TimeSeries,
    Dictionary<string, List<FailoverSecondBucket>>? PerInitialPrimaryTimeSeries,
    Dictionary<string, long>? ErrorsByType,
    int TouchedGrains,
    int VerifiedGrains,
    int FailedVerifyGrains,
    int LostWriteCount,
    List<LostWriteDetail>? LostWriteDetails,
    SiloMemorySnapshot Memory);

public sealed record PerSiloBenchSummary(
    string SiloAddress,
    string SiloName,
    int? Ordinal,
    double KeyspaceFraction,
    int LocalLedgerCount,
    int LocalGrains,
    int LocalConcurrency,
    long Ops,
    long Errors,
    double ElapsedMs,
    double OpsPerSec,
    LatencyStats LatencyMs,
    LatencyStats? SecondaryLatencyMs);

public sealed record KeyspaceOwnership(
    string PrimaryNode,
    string? SiloAddress,
    int? Ordinal,
    int LedgerCount,
    double KeyspaceFraction);

public sealed class RollAndDrainRequest
{
    public int TimeoutSeconds { get; set; } = 180;
}

public sealed record RollAndDrainResult(
    bool Skipped,
    string? Reason,
    List<ulong> RolledLedgerIds,
    double ElapsedMs,
    int OpenLedgersAfter,
    int ClosedLedgersAfter,
    BlobCounterSnapshot BlobDelta,
    Dictionary<string, BlobCounterSnapshot> PerSiloBlobDelta,
    List<string> UnreachableSilos);

public sealed record BenchRunResponse(
    string Scenario,
    string RunTag,
    string Mode,
    string WalStorageType,
    string CoordinatorSilo,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double TotalElapsedMs,
    object Result,
    BlobCounterSnapshot BlobDelta,
    Dictionary<string, BlobCounterSnapshot> PerSiloBlobDelta,
    List<SiloTelemetrySummary> SiloTelemetry,
    List<BenchLifecycleEvent> Events,
    List<string> UnreachableSilos,
    string? UploadedResultUri);

public sealed record S1RawWriteResult(
    int Grains,
    int Concurrency,
    int PayloadBytes,
    int WarmupSeconds,
    int DurationSeconds,
    long Ops,
    long Errors,
    double ElapsedMs,
    double OpsPerSec,
    LatencyStats LatencyMs,
    BlobCounterSnapshot MeasuredBlobDelta,
    List<SiloMemorySnapshot> MemoryBySilo,
    bool Distributed = false,
    double? ClusterOpsPerSec = null,
    List<PerSiloBenchSummary>? PerSilo = null);

public sealed record S2PopulatePhaseResult(
    int Grains,
    int Concurrency,
    int PayloadBytes,
    long Ops,
    long Errors,
    double ElapsedMs,
    double OpsPerSec,
    LatencyStats LatencyMs,
    BlobCounterSnapshot BlobDelta,
    List<SiloMemorySnapshot> MemoryBySilo,
    List<PerSiloBenchSummary>? PerSilo = null);

public sealed record S2MixPhaseResult(
    int DurationSeconds,
    double ReadRatio,
    double ZipfExponent,
    long TotalOps,
    long ReadOps,
    long WriteOps,
    long Errors,
    double ElapsedMs,
    double OpsPerSec,
    LatencyStats ReadLatencyMs,
    LatencyStats WriteLatencyMs,
    LatencyStats OverallLatencyMs,
    BlobCounterSnapshot BlobDelta,
    List<PerSiloBenchSummary>? PerSilo = null);

public sealed record S2ColdReadPhaseResult(
    int SampleGrains,
    long Ops,
    long Errors,
    double ElapsedMs,
    double OpsPerSec,
    LatencyStats LatencyMs,
    BlobCounterSnapshot BlobDelta,
    List<PerSiloBenchSummary>? PerSilo = null);

public sealed record S2LargeJournalsResult(
    S2PopulatePhaseResult Populate,
    S2MixPhaseResult Mix,
    RollAndDrainResult? RollAndDrain,
    S2ColdReadPhaseResult? ColdRead,
    bool Distributed = false);

public sealed record S3JobsEarlyCancelResult(
    int Grains,
    int Concurrency,
    int WarmupSeconds,
    int DurationSeconds,
    long Cycles,
    long Errors,
    double ElapsedMs,
    double CyclesPerSec,
    LatencyStats ScheduleLatencyMs,
    LatencyStats CancelLatencyMs,
    BlobCounterSnapshot MeasuredBlobDelta,
    RollAndDrainResult? PostRunRollAndDrain,
    bool Distributed = false,
    double? ClusterCyclesPerSec = null,
    List<PerSiloBenchSummary>? PerSilo = null);

public sealed record S4aJobsOpenFireResult(
    int RequestedJobs,
    long ScheduledJobs,
    long ScheduleErrors,
    bool DeactivatedAfterSchedule,
    int DelayMs,
    long ExecutedJobs,
    long EphemeralReminderExecutions,
    long BackingShardExecutions,
    bool AllExecuted,
    double TotalWaitMs,
    LatencyStats ScheduleLatencyMs,
    LatencyStats DeliveryLagMs,
    bool Distributed = false,
    List<PerSiloBenchSummary>? PerSilo = null);

public sealed record S4bJobsSealedDrainResult(
    int RequestedJobs,
    long ScheduledJobs,
    long ScheduleErrors,
    bool DeactivatedAfterSchedule,
    int DelayMs,
    long ExecutedJobs,
    long EphemeralReminderExecutions,
    long BackingShardExecutions,
    bool AllExecuted,
    double TotalWaitMs,
    LatencyStats ScheduleLatencyMs,
    RollAndDrainResult? Drain,
    long SealedShardBlobPuts,
    LatencyStats DeliveryLagMs,
    bool Distributed = false,
    List<PerSiloBenchSummary>? PerSilo = null);

public sealed record FailoverSecondBucket(
    int Second,
    DateTimeOffset UtcTimestamp,
    long Ops,
    long Errors,
    double ErrorRatePct,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs);

public sealed record LostWriteDetail(
    string GrainId,
    long ExpectedAtLeast,
    long Actual);

public sealed record FailoverJobVerification(
    int RequestedJobs,
    int AckedJobs,
    int ScheduleErrors,
    long ExecutedJobs,
    long EphemeralReminderExecutions,
    long BackingShardExecutions,
    long LostJobCount,
    long SealedShardBlobPuts,
    LatencyStats DeliveryLagMs);

public sealed record S6FailoverResult(
    int Grains,
    int Concurrency,
    int DurationSeconds,
    int PayloadBytes,
    long TotalOps,
    long TotalErrors,
    double OverallErrorRatePct,
    double ElapsedMs,
    double OverallOpsPerSec,
    LatencyStats OverallLatencyMs,
    Dictionary<string, long> ErrorsByType,
    List<FailoverSecondBucket> TimeSeries,
    int TouchedGrains,
    int VerifiedGrains,
    int FailedVerifyGrains,
    int LostWriteCount,
    List<LostWriteDetail> LostWriteDetails,
    FailoverJobVerification? Jobs,
    bool Distributed = false,
    List<PerSiloBenchSummary>? PerSilo = null,
    bool IsolateWorkersByPrimary = false,
    Dictionary<string, List<FailoverSecondBucket>>? PerInitialPrimaryTimeSeries = null);

/// <summary>
/// Uploads benchmark JSON reports to an optional standard Azure Blob Storage account using an isolated
/// <see cref="BlobServiceClient"/> (never attached to <see cref="BlobRequestCounterPolicy"/>).
/// </summary>
public sealed class BenchResultsUploader(
    BlobContainerClient? container,
    ILogger<BenchResultsUploader> logger)
{
    public bool IsEnabled => container is not null;

    public async Task<string?> TryUploadAsync(string scenario, string runTag, string mode, object report, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        if (container is null)
        {
            return null;
        }

        try
        {
            var safeTag = runTag.Replace(':', '-').Replace('/', '-');
            var blobName = $"{DateTimeOffset.UtcNow:yyyyMMdd}/{mode.ToLowerInvariant()}-{scenario}-{safeTag}.json";
            var blob = container.GetBlobClient(blobName);
            var json = JsonSerializer.Serialize(report, jsonOptions);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            _ = await blob.UploadAsync(stream, overwrite: true, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Uploaded benchmark report to {Uri}", blob.Uri);
            return blob.Uri.ToString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to upload benchmark report for {RunTag} to results blob storage.", runTag);
            return null;
        }
    }
}

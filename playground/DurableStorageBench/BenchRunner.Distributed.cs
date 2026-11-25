using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using Orleans.Durable.Journaling;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Bench;

public sealed partial class BenchRunner
{
    private readonly ConcurrentDictionary<string, string[]> _cachedLocalKeys = new(StringComparer.Ordinal);

    private sealed record SiloAllocation(
        SiloAddress Silo,
        int DriverIndex,
        int? Ordinal,
        double KeyspaceFraction,
        int LocalLedgerCount,
        int LocalGrains,
        int LocalConcurrency);

    public async Task<LocalBenchResponse> RunLocalAsync(LocalBenchRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var scenario = (req.Scenario ?? string.Empty).Trim().ToLowerInvariant();
        var runTag = string.IsNullOrWhiteSpace(req.RunTag)
            ? $"local-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : req.RunTag.Trim();

        var localGrains = Math.Max(1, req.Grains);
        var localConcurrency = Math.Max(1, req.Concurrency);
        var maxSamples = Math.Clamp(req.MaxLatencySamples, 1_000, MaxLatencyReservoir);
        var useLocalPrimary = req.LocalPrimaryOnly && runtimeInfo.Mode == BenchMode.Clog;

        var (keyspaceFraction, localLedgerCount) = await GetLocalKeyspaceInfoAsync(ct).ConfigureAwait(false);
        if (keyspaceFraction <= 0 && req.ExpectedKeyspaceFraction > 0)
        {
            keyspaceFraction = req.ExpectedKeyspaceFraction;
            localLedgerCount = req.LocalLedgerCount;
        }

        return scenario switch
        {
            "s1-raw-write" => await ExecuteLocalS1Async(req, runTag, localGrains, localConcurrency, maxSamples, useLocalPrimary, keyspaceFraction, localLedgerCount, ct).ConfigureAwait(false),
            "s2-populate" => await ExecuteLocalS2PopulateAsync(req, runTag, localGrains, localConcurrency, maxSamples, useLocalPrimary, keyspaceFraction, localLedgerCount, ct).ConfigureAwait(false),
            "s2-mix" => await ExecuteLocalS2MixAsync(req, runTag, localGrains, localConcurrency, maxSamples, useLocalPrimary, keyspaceFraction, localLedgerCount, ct).ConfigureAwait(false),
            "s2-deactivate" => await ExecuteLocalS2DeactivateAsync(req, runTag, localGrains, localConcurrency, useLocalPrimary, keyspaceFraction, localLedgerCount, ct).ConfigureAwait(false),
            "s2-cold-read" => await ExecuteLocalS2ColdReadAsync(req, runTag, localGrains, localConcurrency, maxSamples, useLocalPrimary, keyspaceFraction, localLedgerCount, ct).ConfigureAwait(false),
            "s3-jobs-early-cancel" => await ExecuteLocalS3Async(req, runTag, localGrains, localConcurrency, maxSamples, useLocalPrimary, keyspaceFraction, localLedgerCount, ct).ConfigureAwait(false),
            "s4a-schedule" => await ExecuteLocalS4ScheduleAsync(req, runTag, "s4a", localGrains, localConcurrency, maxSamples, useLocalPrimary, keyspaceFraction, localLedgerCount, ct).ConfigureAwait(false),
            "s4b-schedule" => await ExecuteLocalS4ScheduleAsync(req, runTag, "s4b", localGrains, localConcurrency, maxSamples, useLocalPrimary, keyspaceFraction, localLedgerCount, ct).ConfigureAwait(false),
            "s6-failover-load" => await ExecuteLocalS6LoadAsync(req, runTag, localGrains, localConcurrency, maxSamples, keyspaceFraction, localLedgerCount, ct).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown local benchmark scenario '{req.Scenario}'.", nameof(req)),
        };
    }

    private async Task<S1RawWriteResult> RunS1RawWriteDistributedAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var allocations = await PlanSiloAllocationsAsync(req, excludeFailoverTarget: false, ct).ConfigureAwait(false);
        var localPrimaryOnly = runtimeInfo.Mode == BenchMode.Clog;

        var before = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var wallSw = Stopwatch.StartNew();

        var responses = await Task.WhenAll(allocations.Select(alloc => DispatchLocalAsync(
            alloc,
            new LocalBenchRequest
            {
                Scenario = "s1-raw-write",
                RunTag = runTag,
                DriverIndex = alloc.DriverIndex,
                Grains = alloc.LocalGrains,
                Concurrency = alloc.LocalConcurrency,
                DurationSeconds = Math.Max(1, req.DurationSeconds),
                WarmupSeconds = Math.Max(0, req.WarmupSeconds),
                PayloadBytes = Math.Max(1, req.PayloadBytes),
                LocalPrimaryOnly = localPrimaryOnly,
                ExpectedKeyspaceFraction = alloc.KeyspaceFraction,
                LocalLedgerCount = alloc.LocalLedgerCount,
            },
            ct))).ConfigureAwait(false);

        wallSw.Stop();
        var after = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var blobDiff = ClusterTelemetryAggregator.DiffBlobs(before, after);

        var totalOps = responses.Sum(static r => r.Ops);
        var totalErrors = responses.Sum(static r => r.Errors);
        var maxElapsedMs = responses.Max(static r => r.ElapsedMs);
        var clusterOpsPerSec = maxElapsedMs > 0 ? Math.Round(totalOps / (maxElapsedMs / 1000.0), 2) : 0;
        var mergedSamples = MergeSamples(responses.Select(static r => r.LatencySamplesMs));

        return new S1RawWriteResult(
            Grains: allocations.Sum(static a => a.LocalGrains),
            Concurrency: allocations.Sum(static a => a.LocalConcurrency),
            PayloadBytes: Math.Max(1, req.PayloadBytes),
            WarmupSeconds: req.WarmupSeconds,
            DurationSeconds: req.DurationSeconds,
            Ops: totalOps,
            Errors: totalErrors,
            ElapsedMs: Math.Round(maxElapsedMs, 2),
            OpsPerSec: clusterOpsPerSec,
            LatencyMs: LatencyStats.FromSamples(mergedSamples),
            MeasuredBlobDelta: blobDiff.Total,
            MemoryBySilo: responses.Select(static r => r.Memory).ToList(),
            Distributed: true,
            ClusterOpsPerSec: clusterOpsPerSec,
            PerSilo: ToPerSiloSummaries(responses));
    }

    private async Task<S2LargeJournalsResult> RunS2LargeJournalsDistributedAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var allocations = await PlanSiloAllocationsAsync(req, excludeFailoverTarget: false, ct).ConfigureAwait(false);
        var localPrimaryOnly = runtimeInfo.Mode == BenchMode.Clog;
        var payloadBytes = Math.Max(1, req.PayloadBytes);
        var readRatio = Math.Clamp(req.ReadRatio, 0.0, 1.0);

        // Phase A: Populate
        var popBefore = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var popWallSw = Stopwatch.StartNew();
        var popResponses = await Task.WhenAll(allocations.Select(alloc => DispatchLocalAsync(
            alloc,
            new LocalBenchRequest
            {
                Scenario = "s2-populate",
                RunTag = runTag,
                DriverIndex = alloc.DriverIndex,
                Grains = alloc.LocalGrains,
                Concurrency = alloc.LocalConcurrency,
                PayloadBytes = payloadBytes,
                LocalPrimaryOnly = localPrimaryOnly,
                ExpectedKeyspaceFraction = alloc.KeyspaceFraction,
                LocalLedgerCount = alloc.LocalLedgerCount,
            },
            ct))).ConfigureAwait(false);
        popWallSw.Stop();
        var popAfter = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var popBlobDiff = ClusterTelemetryAggregator.DiffBlobs(popBefore, popAfter);

        var popOps = popResponses.Sum(static r => r.Ops);
        var popErrors = popResponses.Sum(static r => r.Errors);
        var popElapsedMs = popWallSw.Elapsed.TotalMilliseconds;
        var populateResult = new S2PopulatePhaseResult(
            Grains: allocations.Sum(static a => a.LocalGrains),
            Concurrency: allocations.Sum(static a => a.LocalConcurrency),
            PayloadBytes: payloadBytes,
            Ops: popOps,
            Errors: popErrors,
            ElapsedMs: Math.Round(popElapsedMs, 2),
            OpsPerSec: popElapsedMs > 0 ? Math.Round(popOps / (popElapsedMs / 1000.0), 2) : 0,
            LatencyMs: LatencyStats.FromSamples(MergeSamples(popResponses.Select(static r => r.LatencySamplesMs))),
            BlobDelta: popBlobDiff.Total,
            MemoryBySilo: popResponses.Select(static r => r.Memory).ToList(),
            PerSilo: ToPerSiloSummaries(popResponses));

        // Phase B: Hot/Cold Zipf mix
        var mixBefore = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var mixWallSw = Stopwatch.StartNew();
        var mixResponses = await Task.WhenAll(allocations.Select(alloc => DispatchLocalAsync(
            alloc,
            new LocalBenchRequest
            {
                Scenario = "s2-mix",
                RunTag = runTag,
                DriverIndex = alloc.DriverIndex,
                Grains = alloc.LocalGrains,
                Concurrency = alloc.LocalConcurrency,
                DurationSeconds = Math.Max(1, req.DurationSeconds),
                PayloadBytes = payloadBytes,
                ReadRatio = readRatio,
                ZipfExponent = req.ZipfExponent,
                LocalPrimaryOnly = localPrimaryOnly,
                ExpectedKeyspaceFraction = alloc.KeyspaceFraction,
                LocalLedgerCount = alloc.LocalLedgerCount,
            },
            ct))).ConfigureAwait(false);
        mixWallSw.Stop();
        var mixAfter = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var mixBlobDiff = ClusterTelemetryAggregator.DiffBlobs(mixBefore, mixAfter);

        var readOps = mixResponses.Sum(static r => r.ReadOps);
        var writeOps = mixResponses.Sum(static r => r.WriteOps);
        var totalMixOps = readOps + writeOps;
        var mixErrors = mixResponses.Sum(static r => r.Errors);
        var mixMaxElapsedMs = mixResponses.Max(static r => r.ElapsedMs);
        var readSamples = MergeSamples(mixResponses.Select(static r => r.LatencySamplesMs));
        var writeSamples = MergeSamples(mixResponses.Select(static r => r.SecondaryLatencySamplesMs ?? []));
        var allMixSamples = new List<double>(readSamples.Count + writeSamples.Count);
        allMixSamples.AddRange(readSamples);
        allMixSamples.AddRange(writeSamples);

        var mixResult = new S2MixPhaseResult(
            DurationSeconds: req.DurationSeconds,
            ReadRatio: readRatio,
            ZipfExponent: req.ZipfExponent,
            TotalOps: totalMixOps,
            ReadOps: readOps,
            WriteOps: writeOps,
            Errors: mixErrors,
            ElapsedMs: Math.Round(mixMaxElapsedMs, 2),
            OpsPerSec: mixMaxElapsedMs > 0 ? Math.Round(totalMixOps / (mixMaxElapsedMs / 1000.0), 2) : 0,
            ReadLatencyMs: LatencyStats.FromSamples(readSamples),
            WriteLatencyMs: LatencyStats.FromSamples(writeSamples),
            OverallLatencyMs: LatencyStats.FromSamples(allMixSamples),
            BlobDelta: mixBlobDiff.Total,
            PerSilo: ToPerSiloSummaries(mixResponses));

        // Phase C: Optional Cold Read after deactivate + optional rollAndDrain
        var totalGrains = allocations.Sum(static a => a.LocalGrains);
        var coldCount = Math.Min(totalGrains, req.ColdSampleGrains ?? Math.Min(2000, totalGrains));
        RollAndDrainResult? rollResult = null;
        S2ColdReadPhaseResult? coldResult = null;

        if (coldCount > 0)
        {
            var coldAllocations = AllocateProportional(allocations, coldCount);
            await Task.WhenAll(allocations.Select((alloc, i) => DispatchLocalAsync(
                alloc,
                new LocalBenchRequest
                {
                    Scenario = "s2-deactivate",
                    RunTag = runTag,
                    DriverIndex = alloc.DriverIndex,
                    Grains = alloc.LocalGrains,
                    Concurrency = alloc.LocalConcurrency,
                    ColdSampleGrains = Math.Min(alloc.LocalGrains, coldAllocations[i]),
                    LocalPrimaryOnly = localPrimaryOnly,
                    ExpectedKeyspaceFraction = alloc.KeyspaceFraction,
                    LocalLedgerCount = alloc.LocalLedgerCount,
                },
                ct))).ConfigureAwait(false);

            await Task.Delay(500, ct).ConfigureAwait(false);

            if (req.RollAndDrain && runtimeInfo.Mode == BenchMode.Clog)
            {
                rollResult = await RollAndDrainAsync(TimeSpan.FromSeconds(Math.Max(30, req.WaitTimeoutSeconds)), ct).ConfigureAwait(false);
            }

            var coldBefore = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
            var coldWallSw = Stopwatch.StartNew();
            var coldResponses = await Task.WhenAll(allocations.Select((alloc, i) => DispatchLocalAsync(
                alloc,
                new LocalBenchRequest
                {
                    Scenario = "s2-cold-read",
                    RunTag = runTag,
                    DriverIndex = alloc.DriverIndex,
                    Grains = alloc.LocalGrains,
                    Concurrency = alloc.LocalConcurrency,
                    PayloadBytes = payloadBytes,
                    ColdSampleGrains = Math.Min(alloc.LocalGrains, coldAllocations[i]),
                    LocalPrimaryOnly = localPrimaryOnly,
                    ExpectedKeyspaceFraction = alloc.KeyspaceFraction,
                    LocalLedgerCount = alloc.LocalLedgerCount,
                },
                ct))).ConfigureAwait(false);
            coldWallSw.Stop();
            var coldAfter = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
            var coldBlobDiff = ClusterTelemetryAggregator.DiffBlobs(coldBefore, coldAfter);

            var coldOps = coldResponses.Sum(static r => r.Ops);
            var coldErrors = coldResponses.Sum(static r => r.Errors);
            var coldElapsedMs = coldWallSw.Elapsed.TotalMilliseconds;

            coldResult = new S2ColdReadPhaseResult(
                SampleGrains: coldAllocations.Sum(),
                Ops: coldOps,
                Errors: coldErrors,
                ElapsedMs: Math.Round(coldElapsedMs, 2),
                OpsPerSec: coldElapsedMs > 0 ? Math.Round(coldOps / (coldElapsedMs / 1000.0), 2) : 0,
                LatencyMs: LatencyStats.FromSamples(MergeSamples(coldResponses.Select(static r => r.LatencySamplesMs))),
                BlobDelta: coldBlobDiff.Total,
                PerSilo: ToPerSiloSummaries(coldResponses));
        }

        return new S2LargeJournalsResult(populateResult, mixResult, rollResult, coldResult, Distributed: true);
    }

    private async Task<S3JobsEarlyCancelResult> RunS3JobsEarlyCancelDistributedAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var allocations = await PlanSiloAllocationsAsync(req, excludeFailoverTarget: false, ct).ConfigureAwait(false);
        var localPrimaryOnly = runtimeInfo.Mode == BenchMode.Clog;

        var before = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var responses = await Task.WhenAll(allocations.Select(alloc => DispatchLocalAsync(
            alloc,
            new LocalBenchRequest
            {
                Scenario = "s3-jobs-early-cancel",
                RunTag = runTag,
                DriverIndex = alloc.DriverIndex,
                Grains = alloc.LocalGrains,
                Concurrency = alloc.LocalConcurrency,
                DurationSeconds = Math.Max(1, req.DurationSeconds),
                WarmupSeconds = Math.Max(0, req.WarmupSeconds),
                LocalPrimaryOnly = localPrimaryOnly,
                ExpectedKeyspaceFraction = alloc.KeyspaceFraction,
                LocalLedgerCount = alloc.LocalLedgerCount,
            },
            ct))).ConfigureAwait(false);
        var after = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var measuredBlobDiff = ClusterTelemetryAggregator.DiffBlobs(before, after);

        RollAndDrainResult? postRoll = null;
        if ((req.RollAfterSchedule || req.RollAndDrain) && runtimeInfo.Mode == BenchMode.Clog)
        {
            postRoll = await RollAndDrainAsync(TimeSpan.FromSeconds(Math.Max(30, req.WaitTimeoutSeconds)), ct).ConfigureAwait(false);
        }

        var totalCycles = responses.Sum(static r => r.Ops);
        var totalErrors = responses.Sum(static r => r.Errors);
        var maxElapsedMs = responses.Max(static r => r.ElapsedMs);
        var clusterCyclesPerSec = maxElapsedMs > 0 ? Math.Round(totalCycles / (maxElapsedMs / 1000.0), 2) : 0;

        return new S3JobsEarlyCancelResult(
            Grains: allocations.Sum(static a => a.LocalGrains),
            Concurrency: allocations.Sum(static a => a.LocalConcurrency),
            WarmupSeconds: req.WarmupSeconds,
            DurationSeconds: req.DurationSeconds,
            Cycles: totalCycles,
            Errors: totalErrors,
            ElapsedMs: Math.Round(maxElapsedMs, 2),
            CyclesPerSec: clusterCyclesPerSec,
            ScheduleLatencyMs: LatencyStats.FromSamples(MergeSamples(responses.Select(static r => r.LatencySamplesMs))),
            CancelLatencyMs: LatencyStats.FromSamples(MergeSamples(responses.Select(static r => r.SecondaryLatencySamplesMs ?? []))),
            MeasuredBlobDelta: measuredBlobDiff.Total,
            PostRunRollAndDrain: postRoll,
            Distributed: true,
            ClusterCyclesPerSec: clusterCyclesPerSec,
            PerSilo: ToPerSiloSummaries(responses));
    }

    private async Task<S4aJobsOpenFireResult> RunS4aJobsOpenFireDistributedAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var allocations = await PlanSiloAllocationsAsync(req, excludeFailoverTarget: false, ct).ConfigureAwait(false);
        var localPrimaryOnly = runtimeInfo.Mode == BenchMode.Clog;
        var delayMs = req.DelayMs > 0 ? req.DelayMs : 1500;
        var waitTimeout = TimeSpan.FromSeconds(Math.Max(15, req.WaitTimeoutSeconds));

        var responses = await Task.WhenAll(allocations.Select(alloc => DispatchLocalAsync(
            alloc,
            new LocalBenchRequest
            {
                Scenario = "s4a-schedule",
                RunTag = runTag,
                DriverIndex = alloc.DriverIndex,
                Grains = alloc.LocalGrains,
                Concurrency = alloc.LocalConcurrency,
                DelayMs = delayMs,
                DeactivateAfterSchedule = req.DeactivateAfterSchedule,
                LocalPrimaryOnly = localPrimaryOnly,
                ExpectedKeyspaceFraction = alloc.KeyspaceFraction,
                LocalLedgerCount = alloc.LocalLedgerCount,
            },
            ct))).ConfigureAwait(false);

        var scheduled = responses.Sum(static r => r.Ops);
        var scheduleErrors = responses.Sum(static r => r.Errors);
        var (finalSnap, waitElapsed) = await WaitForJobExecutionsAsync(runTag, scheduled, waitTimeout, ct).ConfigureAwait(false);

        return new S4aJobsOpenFireResult(
            RequestedJobs: allocations.Sum(static a => a.LocalGrains),
            ScheduledJobs: scheduled,
            ScheduleErrors: scheduleErrors,
            DeactivatedAfterSchedule: req.DeactivateAfterSchedule,
            DelayMs: delayMs,
            ExecutedJobs: finalSnap.RunJobExecuted,
            EphemeralReminderExecutions: finalSnap.RunEphemeralReminderExecutions,
            BackingShardExecutions: finalSnap.RunBackingShardExecutions,
            AllExecuted: finalSnap.RunJobExecuted >= scheduled,
            TotalWaitMs: Math.Round(waitElapsed.TotalMilliseconds, 2),
            ScheduleLatencyMs: LatencyStats.FromSamples(MergeSamples(responses.Select(static r => r.LatencySamplesMs))),
            DeliveryLagMs: LatencyStats.FromSamples(finalSnap.RunLagSamplesMs),
            Distributed: true,
            PerSilo: ToPerSiloSummaries(responses));
    }

    private async Task<S4bJobsSealedDrainResult> RunS4bJobsSealedDrainDistributedAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var allocations = await PlanSiloAllocationsAsync(req, excludeFailoverTarget: false, ct).ConfigureAwait(false);
        var localPrimaryOnly = runtimeInfo.Mode == BenchMode.Clog;
        var delayMs = req.DelayMs > 0 ? req.DelayMs : 10_000;
        var waitTimeout = TimeSpan.FromSeconds(Math.Max(30, req.WaitTimeoutSeconds));

        var responses = await Task.WhenAll(allocations.Select(alloc => DispatchLocalAsync(
            alloc,
            new LocalBenchRequest
            {
                Scenario = "s4b-schedule",
                RunTag = runTag,
                DriverIndex = alloc.DriverIndex,
                Grains = alloc.LocalGrains,
                Concurrency = alloc.LocalConcurrency,
                DelayMs = delayMs,
                DeactivateAfterSchedule = req.DeactivateAfterSchedule,
                LocalPrimaryOnly = localPrimaryOnly,
                ExpectedKeyspaceFraction = alloc.KeyspaceFraction,
                LocalLedgerCount = alloc.LocalLedgerCount,
            },
            ct))).ConfigureAwait(false);

        var scheduled = responses.Sum(static r => r.Ops);
        var scheduleErrors = responses.Sum(static r => r.Errors);

        RollAndDrainResult? drainResult = null;
        if (runtimeInfo.Mode == BenchMode.Clog)
        {
            drainResult = await RollAndDrainAsync(waitTimeout, ct).ConfigureAwait(false);
        }

        var (finalSnap, waitElapsed) = await WaitForJobExecutionsAsync(runTag, scheduled, waitTimeout, ct).ConfigureAwait(false);
        var sealedPuts = drainResult?.BlobDelta.GetCount("jobs", "PUT") ?? 0;

        return new S4bJobsSealedDrainResult(
            RequestedJobs: allocations.Sum(static a => a.LocalGrains),
            ScheduledJobs: scheduled,
            ScheduleErrors: scheduleErrors,
            DeactivatedAfterSchedule: req.DeactivateAfterSchedule,
            DelayMs: delayMs,
            ExecutedJobs: finalSnap.RunJobExecuted,
            EphemeralReminderExecutions: finalSnap.RunEphemeralReminderExecutions,
            BackingShardExecutions: finalSnap.RunBackingShardExecutions,
            AllExecuted: finalSnap.RunJobExecuted >= scheduled,
            TotalWaitMs: Math.Round(waitElapsed.TotalMilliseconds, 2),
            ScheduleLatencyMs: LatencyStats.FromSamples(MergeSamples(responses.Select(static r => r.LatencySamplesMs))),
            Drain: drainResult,
            SealedShardBlobPuts: sealedPuts,
            DeliveryLagMs: LatencyStats.FromSamples(finalSnap.RunLagSamplesMs),
            Distributed: true,
            PerSilo: ToPerSiloSummaries(responses));
    }

    private async Task<S6FailoverResult> RunS6FailoverDistributedAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var allocations = await PlanSiloAllocationsAsync(req, excludeFailoverTarget: true, ct).ConfigureAwait(false);
        var callTimeout = TimeSpan.FromMilliseconds(Math.Max(500, req.CallTimeoutMs));
        var verifyTimeout = TimeSpan.FromSeconds(Math.Max(15, req.VerifyTimeoutSeconds));
        var jobCount = Math.Max(0, req.JobCount);
        var jobDelayMs = req.DelayMs > 0 ? req.DelayMs : 10_000;

        var blobsBeforeJobs = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var ackedJobs = 0;
        var jobScheduleErrors = 0;

        // Schedule surviving-job batch across ALL 3 silos' key ranges before failover load begins.
        if (jobCount > 0)
        {
            var jobStats = await RunIndexedWorkAsync(
                jobCount,
                Math.Min(Math.Max(1, req.Concurrency), jobCount),
                async (idx, token) =>
                {
                    var grain = grainFactory.GetGrain<IBenchJobGrain>($"{runTag}:s6j:{idx}");
                    await grain.ScheduleWithStateAsync($"s6-{idx}", runTag, jobDelayMs).WaitAsync(callTimeout, token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
            ackedJobs = (int)jobStats.Ops;
            jobScheduleErrors = (int)jobStats.Errors;

            if (req.DeactivateAfterSchedule && ackedJobs > 0)
            {
                await RunIndexedWorkAsync(
                    jobCount,
                    Math.Min(Math.Max(1, req.Concurrency), jobCount),
                    async (idx, token) =>
                    {
                        var grain = grainFactory.GetGrain<IBenchJobGrain>($"{runTag}:s6j:{idx}");
                        await grain.DeactivateNowAsync().WaitAsync(callTimeout, token).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);
            }
        }

        // Fan out continuous load across surviving drivers (LocalPrimaryOnly = false so keys hit ALL 3 silos' ranges!).
        var responses = await Task.WhenAll(allocations.Select(alloc => DispatchLocalAsync(
            alloc,
            new LocalBenchRequest
            {
                Scenario = "s6-failover-load",
                RunTag = runTag,
                DriverIndex = alloc.DriverIndex,
                Grains = alloc.LocalGrains,
                Concurrency = alloc.LocalConcurrency,
                DurationSeconds = Math.Max(1, req.DurationSeconds),
                PayloadBytes = Math.Max(1, req.PayloadBytes),
                LocalPrimaryOnly = false,
                IsolateWorkersByPrimary = req.IsolateWorkersByPrimary,
                RetryBackoffMs = req.RetryBackoffMs,
                CallTimeoutMs = req.CallTimeoutMs,
                VerifyTimeoutSeconds = req.VerifyTimeoutSeconds,
                ExpectedKeyspaceFraction = alloc.KeyspaceFraction,
                LocalLedgerCount = alloc.LocalLedgerCount,
            },
            ct))).ConfigureAwait(false);

        var totalOps = responses.Sum(static r => r.Ops);
        var totalErrors = responses.Sum(static r => r.Errors);
        var totalAttempts = totalOps + totalErrors;
        var maxElapsedMs = responses.Max(static r => r.ElapsedMs);

        var mergedErrorsByType = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var r in responses)
        {
            if (r.ErrorsByType is null)
            {
                continue;
            }

            foreach (var (k, v) in r.ErrorsByType)
            {
                mergedErrorsByType[k] = mergedErrorsByType.GetValueOrDefault(k) + v;
            }
        }

        var mergedTimeSeries = MergeFailoverTimeSeries(responses.Select(static r => r.TimeSeries));
        Dictionary<string, List<FailoverSecondBucket>>? mergedPerPrimarySeries = null;
        if (responses.Any(static r => r.PerInitialPrimaryTimeSeries is { Count: > 0 }))
        {
            var allPrimaries = responses
                .Where(static r => r.PerInitialPrimaryTimeSeries is not null)
                .SelectMany(static r => r.PerInitialPrimaryTimeSeries!.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static k => k, StringComparer.Ordinal)
                .ToList();

            mergedPerPrimarySeries = new Dictionary<string, List<FailoverSecondBucket>>(allPrimaries.Count, StringComparer.Ordinal);
            foreach (var primary in allPrimaries)
            {
                mergedPerPrimarySeries[primary] = MergeFailoverTimeSeries(
                    responses.Select(r => r.PerInitialPrimaryTimeSeries is not null && r.PerInitialPrimaryTimeSeries.TryGetValue(primary, out var series) ? series : null));
            }
        }

        var lostDetails = responses
            .Where(static r => r.LostWriteDetails is not null)
            .SelectMany(static r => r.LostWriteDetails!)
            .ToList();

        FailoverJobVerification? jobVerification = null;
        if (jobCount > 0)
        {
            var (jobSnap, _) = await WaitForJobExecutionsAsync(runTag, ackedJobs, verifyTimeout, ct).ConfigureAwait(false);
            var jobBlobDiff = ClusterTelemetryAggregator.DiffBlobs(blobsBeforeJobs, jobSnap);
            jobVerification = new FailoverJobVerification(
                RequestedJobs: jobCount,
                AckedJobs: ackedJobs,
                ScheduleErrors: jobScheduleErrors,
                ExecutedJobs: jobSnap.RunJobExecuted,
                EphemeralReminderExecutions: jobSnap.RunEphemeralReminderExecutions,
                BackingShardExecutions: jobSnap.RunBackingShardExecutions,
                LostJobCount: Math.Max(0, ackedJobs - jobSnap.RunJobExecuted),
                SealedShardBlobPuts: jobBlobDiff.Total.GetCount("jobs", "PUT"),
                DeliveryLagMs: LatencyStats.FromSamples(jobSnap.RunLagSamplesMs));
        }

        return new S6FailoverResult(
            Grains: allocations.Sum(static a => a.LocalGrains),
            Concurrency: allocations.Sum(static a => a.LocalConcurrency),
            DurationSeconds: Math.Max(1, req.DurationSeconds),
            PayloadBytes: Math.Max(1, req.PayloadBytes),
            TotalOps: totalOps,
            TotalErrors: totalErrors,
            OverallErrorRatePct: totalAttempts > 0 ? Math.Round(100.0 * totalErrors / totalAttempts, 3) : 0,
            ElapsedMs: Math.Round(maxElapsedMs, 2),
            OverallOpsPerSec: maxElapsedMs > 0 ? Math.Round(totalOps / (maxElapsedMs / 1000.0), 2) : 0,
            OverallLatencyMs: LatencyStats.FromSamples(MergeSamples(responses.Select(static r => r.LatencySamplesMs))),
            ErrorsByType: mergedErrorsByType,
            TimeSeries: mergedTimeSeries,
            TouchedGrains: responses.Sum(static r => r.TouchedGrains),
            VerifiedGrains: responses.Sum(static r => r.VerifiedGrains),
            FailedVerifyGrains: responses.Sum(static r => r.FailedVerifyGrains),
            LostWriteCount: lostDetails.Count,
            LostWriteDetails: lostDetails,
            Jobs: jobVerification,
            Distributed: true,
            PerSilo: ToPerSiloSummaries(responses),
            IsolateWorkersByPrimary: mergedPerPrimarySeries is not null,
            PerInitialPrimaryTimeSeries: mergedPerPrimarySeries);
    }

    private async Task<LocalBenchResponse> ExecuteLocalS1Async(
        LocalBenchRequest req,
        string runTag,
        int localGrains,
        int localConcurrency,
        int maxSamples,
        bool useLocalPrimary,
        double keyspaceFraction,
        int localLedgerCount,
        CancellationToken ct)
    {
        var keys = await GenerateGrainKeysAsync<IBenchJournalGrain>(runTag, "s1", localGrains, req.DriverIndex, useLocalPrimary, ct).ConfigureAwait(false);
        var payload = CreatePayload(Math.Max(1, req.PayloadBytes));
        var warmup = TimeSpan.FromSeconds(Math.Max(0, req.WarmupSeconds));
        var duration = TimeSpan.FromSeconds(Math.Max(1, req.DurationSeconds));

        if (warmup > TimeSpan.Zero)
        {
            await RunWorkersForDurationAsync(
                localConcurrency,
                warmup,
                async (workerId, iter, token) =>
                {
                    var key = keys[(int)((iter * localConcurrency + workerId) % keys.Length)];
                    var grain = grainFactory.GetGrain<IBenchJournalGrain>(key);
                    await grain.WriteAsync(payload).ConfigureAwait(false);
                },
                recordStats: false,
                ct).ConfigureAwait(false);
        }

        var (ops, errors, elapsed, latencies) = await RunWorkersForDurationAsync(
            localConcurrency,
            duration,
            async (workerId, iter, token) =>
            {
                var key = keys[Random.Shared.Next(keys.Length)];
                var grain = grainFactory.GetGrain<IBenchJournalGrain>(key);
                await grain.WriteAsync(payload).ConfigureAwait(false);
            },
            recordStats: true,
            ct).ConfigureAwait(false);

        return BuildLocalResponse(
            keyspaceFraction,
            localLedgerCount,
            localGrains,
            localConcurrency,
            ops,
            errors,
            elapsed,
            readOps: 0,
            writeOps: ops,
            latencies,
            secondaryLatencies: null,
            maxSamples);
    }

    private async Task<LocalBenchResponse> ExecuteLocalS2PopulateAsync(
        LocalBenchRequest req,
        string runTag,
        int localGrains,
        int localConcurrency,
        int maxSamples,
        bool useLocalPrimary,
        double keyspaceFraction,
        int localLedgerCount,
        CancellationToken ct)
    {
        var keys = await GenerateGrainKeysAsync<IBenchJournalGrain>(runTag, "s2", localGrains, req.DriverIndex, useLocalPrimary, ct).ConfigureAwait(false);
        CacheLocalKeys(runTag, keys);
        var payload = CreatePayload(Math.Max(1, req.PayloadBytes));

        var stats = await RunIndexedWorkAsync(
            keys.Length,
            localConcurrency,
            async (idx, token) =>
            {
                var grain = grainFactory.GetGrain<IBenchJournalGrain>(keys[idx]);
                await grain.WriteAsync(payload).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return BuildLocalResponse(
            keyspaceFraction,
            localLedgerCount,
            keys.Length,
            localConcurrency,
            stats.Ops,
            stats.Errors,
            stats.Elapsed,
            readOps: 0,
            writeOps: stats.Ops,
            stats.Latencies,
            secondaryLatencies: null,
            maxSamples);
    }

    private async Task<LocalBenchResponse> ExecuteLocalS2MixAsync(
        LocalBenchRequest req,
        string runTag,
        int localGrains,
        int localConcurrency,
        int maxSamples,
        bool useLocalPrimary,
        double keyspaceFraction,
        int localLedgerCount,
        CancellationToken ct)
    {
        var keys = _cachedLocalKeys.TryGetValue(runTag, out var cached)
            ? cached
            : await GenerateGrainKeysAsync<IBenchJournalGrain>(runTag, "s2", localGrains, req.DriverIndex, useLocalPrimary, ct).ConfigureAwait(false);

        var payload = CreatePayload(Math.Max(1, req.PayloadBytes));
        var readRatio = Math.Clamp(req.ReadRatio, 0.0, 1.0);
        var zipf = req.ZipfExponent > 0 ? new ZipfSampler(keys.Length, req.ZipfExponent) : null;
        var duration = TimeSpan.FromSeconds(Math.Max(1, req.DurationSeconds));

        long readOps = 0, writeOps = 0, errors = 0;
        var readReservoir = new LatencyReservoir(maxSamples);
        var writeReservoir = new LatencyReservoir(maxSamples);
        var sw = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, localConcurrency).Select(_ => Task.Run(async () =>
        {
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                var idx = zipf is not null ? zipf.Next() : Random.Shared.Next(keys.Length);
                var isRead = Random.Shared.NextDouble() < readRatio;
                var grain = grainFactory.GetGrain<IBenchJournalGrain>(keys[idx]);
                var opSw = Stopwatch.StartNew();
                try
                {
                    if (isRead)
                    {
                        _ = await grain.ReadLengthAsync().ConfigureAwait(false);
                        opSw.Stop();
                        Interlocked.Increment(ref readOps);
                        readReservoir.Add(opSw.Elapsed.TotalMilliseconds);
                    }
                    else
                    {
                        await grain.WriteAsync(payload).ConfigureAwait(false);
                        opSw.Stop();
                        Interlocked.Increment(ref writeOps);
                        writeReservoir.Add(opSw.Elapsed.TotalMilliseconds);
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        sw.Stop();

        return BuildLocalResponse(
            keyspaceFraction,
            localLedgerCount,
            keys.Length,
            localConcurrency,
            ops: readOps + writeOps,
            errors,
            sw.Elapsed,
            readOps,
            writeOps,
            readReservoir.Snapshot(),
            writeReservoir.Snapshot(),
            maxSamples);
    }

    private async Task<LocalBenchResponse> ExecuteLocalS2DeactivateAsync(
        LocalBenchRequest req,
        string runTag,
        int localGrains,
        int localConcurrency,
        bool useLocalPrimary,
        double keyspaceFraction,
        int localLedgerCount,
        CancellationToken ct)
    {
        var keys = _cachedLocalKeys.TryGetValue(runTag, out var cached)
            ? cached
            : await GenerateGrainKeysAsync<IBenchJournalGrain>(runTag, "s2", localGrains, req.DriverIndex, useLocalPrimary, ct).ConfigureAwait(false);

        var sampleCount = Math.Min(keys.Length, Math.Max(0, req.ColdSampleGrains));
        var stats = await RunIndexedWorkAsync(
            sampleCount,
            localConcurrency,
            async (idx, token) =>
            {
                var grain = grainFactory.GetGrain<IBenchJournalGrain>(keys[idx]);
                await grain.DeactivateNowAsync().ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return BuildLocalResponse(
            keyspaceFraction,
            localLedgerCount,
            sampleCount,
            localConcurrency,
            stats.Ops,
            stats.Errors,
            stats.Elapsed,
            readOps: 0,
            writeOps: 0,
            latencies: [],
            secondaryLatencies: null,
            maxSamples: 0);
    }

    private async Task<LocalBenchResponse> ExecuteLocalS2ColdReadAsync(
        LocalBenchRequest req,
        string runTag,
        int localGrains,
        int localConcurrency,
        int maxSamples,
        bool useLocalPrimary,
        double keyspaceFraction,
        int localLedgerCount,
        CancellationToken ct)
    {
        var keys = _cachedLocalKeys.TryGetValue(runTag, out var cached)
            ? cached
            : await GenerateGrainKeysAsync<IBenchJournalGrain>(runTag, "s2", localGrains, req.DriverIndex, useLocalPrimary, ct).ConfigureAwait(false);

        var expectedPayloadLen = Math.Max(1, req.PayloadBytes);
        var sampleCount = Math.Min(keys.Length, Math.Max(0, req.ColdSampleGrains));

        var stats = await RunIndexedWorkAsync(
            sampleCount,
            localConcurrency,
            async (idx, token) =>
            {
                var grain = grainFactory.GetGrain<IBenchJournalGrain>(keys[idx]);
                var len = await grain.ReadLengthAsync().ConfigureAwait(false);
                if (len != expectedPayloadLen)
                {
                    throw new InvalidOperationException($"Cold read mismatch for '{keys[idx]}': expected {expectedPayloadLen} bytes, got {len}.");
                }
            },
            ct).ConfigureAwait(false);

        _ = _cachedLocalKeys.TryRemove(runTag, out _);

        return BuildLocalResponse(
            keyspaceFraction,
            localLedgerCount,
            sampleCount,
            localConcurrency,
            stats.Ops,
            stats.Errors,
            stats.Elapsed,
            readOps: stats.Ops,
            writeOps: 0,
            stats.Latencies,
            secondaryLatencies: null,
            maxSamples);
    }

    private async Task<LocalBenchResponse> ExecuteLocalS3Async(
        LocalBenchRequest req,
        string runTag,
        int localGrains,
        int localConcurrency,
        int maxSamples,
        bool useLocalPrimary,
        double keyspaceFraction,
        int localLedgerCount,
        CancellationToken ct)
    {
        var keys = await GenerateGrainKeysAsync<IBenchJobGrain>(runTag, "s3", localGrains, req.DriverIndex, useLocalPrimary, ct).ConfigureAwait(false);
        var warmup = TimeSpan.FromSeconds(Math.Max(0, req.WarmupSeconds));
        var duration = TimeSpan.FromSeconds(Math.Max(1, req.DurationSeconds));
        const int oneHourMs = 3_600_000;

        if (warmup > TimeSpan.Zero)
        {
            await RunWorkersForDurationAsync(
                localConcurrency,
                warmup,
                async (workerId, iter, token) =>
                {
                    var key = keys[(int)((iter * localConcurrency + workerId) % keys.Length)];
                    var grain = grainFactory.GetGrain<IBenchJobGrain>(key);
                    var jobId = $"warm-{req.DriverIndex}-{workerId}-{iter}";
                    await grain.ScheduleWithStateAsync(jobId, runTag, oneHourMs).ConfigureAwait(false);
                    _ = await grain.CancelWithStateAsync(jobId).ConfigureAwait(false);
                },
                recordStats: false,
                ct).ConfigureAwait(false);
        }

        var schedReservoir = new LatencyReservoir(maxSamples);
        var cancelReservoir = new LatencyReservoir(maxSamples);
        long cycles = 0, errors = 0;
        var sw = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, localConcurrency).Select(workerId => Task.Run(async () =>
        {
            long iter = 0;
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                iter++;
                var key = keys[(int)((workerId + iter * localConcurrency) % keys.Length)];
                var grain = grainFactory.GetGrain<IBenchJobGrain>(key);
                var jobId = $"job-{req.DriverIndex}-{workerId}-{iter}";

                try
                {
                    var sSw = Stopwatch.StartNew();
                    await grain.ScheduleWithStateAsync(jobId, runTag, oneHourMs).ConfigureAwait(false);
                    sSw.Stop();
                    schedReservoir.Add(sSw.Elapsed.TotalMilliseconds);

                    var cSw = Stopwatch.StartNew();
                    var cancelled = await grain.CancelWithStateAsync(jobId).ConfigureAwait(false);
                    cSw.Stop();
                    if (!cancelled)
                    {
                        Interlocked.Increment(ref errors);
                        continue;
                    }

                    cancelReservoir.Add(cSw.Elapsed.TotalMilliseconds);
                    Interlocked.Increment(ref cycles);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        sw.Stop();

        return BuildLocalResponse(
            keyspaceFraction,
            localLedgerCount,
            keys.Length,
            localConcurrency,
            cycles,
            errors,
            sw.Elapsed,
            readOps: 0,
            writeOps: cycles,
            schedReservoir.Snapshot(),
            cancelReservoir.Snapshot(),
            maxSamples);
    }

    private async Task<LocalBenchResponse> ExecuteLocalS4ScheduleAsync(
        LocalBenchRequest req,
        string runTag,
        string scope,
        int localGrains,
        int localConcurrency,
        int maxSamples,
        bool useLocalPrimary,
        double keyspaceFraction,
        int localLedgerCount,
        CancellationToken ct)
    {
        var keys = await GenerateGrainKeysAsync<IBenchJobGrain>(runTag, scope, localGrains, req.DriverIndex, useLocalPrimary, ct).ConfigureAwait(false);
        var delayMs = req.DelayMs > 0 ? req.DelayMs : 1500;

        var schedStats = await RunIndexedWorkAsync(
            keys.Length,
            localConcurrency,
            async (idx, token) =>
            {
                var grain = grainFactory.GetGrain<IBenchJobGrain>(keys[idx]);
                await grain.ScheduleWithStateAsync($"j-{req.DriverIndex}-{idx}", runTag, delayMs).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        if (req.DeactivateAfterSchedule)
        {
            await RunIndexedWorkAsync(
                keys.Length,
                localConcurrency,
                async (idx, token) =>
                {
                    var grain = grainFactory.GetGrain<IBenchJobGrain>(keys[idx]);
                    await grain.DeactivateNowAsync().ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }

        return BuildLocalResponse(
            keyspaceFraction,
            localLedgerCount,
            keys.Length,
            localConcurrency,
            schedStats.Ops,
            schedStats.Errors,
            schedStats.Elapsed,
            readOps: 0,
            writeOps: schedStats.Ops,
            schedStats.Latencies,
            secondaryLatencies: null,
            maxSamples);
    }

    private sealed record FailoverLoadWindowResult(
        int GrainCount,
        long TotalOps,
        long TotalErrors,
        TimeSpan Elapsed,
        List<double> Samples,
        Dictionary<string, long> ErrorsByType,
        List<FailoverSecondBucket> TimeSeries,
        bool UsedIsolatedPools,
        Dictionary<string, List<FailoverSecondBucket>>? PerInitialPrimaryTimeSeries,
        int TouchedGrains,
        int VerifiedGrains,
        int FailedVerifyGrains,
        List<LostWriteDetail> LostWriteDetails,
        DateTimeOffset VerifyDeadline);

    private sealed record PrimaryWorkerPool(
        string PrimaryLabel,
        int WorkerCount,
        int[] KeyIndices,
        ConcurrentDictionary<int, SecondBucketAccumulator> Buckets);

    private async Task<FailoverLoadWindowResult> RunFailoverLoadWindowAsync(
        string runTag,
        int driverIndex,
        int grains,
        int concurrency,
        int durationSeconds,
        byte[] payload,
        TimeSpan retryBackoff,
        TimeSpan callTimeout,
        TimeSpan verifyTimeout,
        int maxSamples,
        bool isolateWorkersByPrimary,
        CancellationToken ct)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds);
        string[] keys;
        List<PrimaryWorkerPool>? isolatedPools = null;

        if (isolateWorkersByPrimary && runtimeInfo.Mode == BenchMode.Clog)
        {
            (keys, isolatedPools) = await PartitionKeysByInitialPrimaryAsync(
                runTag,
                driverIndex,
                grains,
                concurrency,
                durationSeconds,
                ct).ConfigureAwait(false);
        }
        else
        {
            keys = new string[grains];
            for (var i = 0; i < grains; i++)
            {
                keys[i] = driverIndex >= 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{runTag}:s6:d{driverIndex}:{i}")
                    : string.Create(CultureInfo.InvariantCulture, $"{runTag}:s6:{i}");
            }
        }

        var lastAcked = new long[keys.Length];
        var buckets = new ConcurrentDictionary<int, SecondBucketAccumulator>();
        for (var s = 0; s < durationSeconds; s++)
        {
            buckets[s] = new SecondBucketAccumulator();
        }

        var errorsByType = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var overallReservoir = new LatencyReservoir(maxSamples);
        long totalOps = 0;
        long totalErrors = 0;

        var loadStartUtc = DateTimeOffset.UtcNow;
        var loadSw = Stopwatch.StartNew();

        Task SpawnWorker(int[]? poolKeyIndices, ConcurrentDictionary<int, SecondBucketAccumulator>? poolBuckets) => Task.Run(async () =>
        {
            while (loadSw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                var idx = poolKeyIndices is not null
                    ? poolKeyIndices[Random.Shared.Next(poolKeyIndices.Length)]
                    : Random.Shared.Next(keys.Length);
                var grain = grainFactory.GetGrain<IBenchJournalGrain>(keys[idx]);
                var opSw = Stopwatch.StartNew();
                try
                {
                    var returnedVersion = await grain.IncrementAndWriteAsync(payload)
                        .WaitAsync(callTimeout, ct)
                        .ConfigureAwait(false);
                    opSw.Stop();

                    AtomicMax(ref lastAcked[idx], returnedVersion);
                    var sec = Math.Max(0, (int)loadSw.Elapsed.TotalSeconds);
                    var ms = opSw.Elapsed.TotalMilliseconds;
                    buckets.GetOrAdd(sec, static _ => new SecondBucketAccumulator()).RecordSuccess(ms);
                    poolBuckets?.GetOrAdd(sec, static _ => new SecondBucketAccumulator()).RecordSuccess(ms);
                    overallReservoir.Add(ms);
                    Interlocked.Increment(ref totalOps);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var sec = Math.Max(0, (int)loadSw.Elapsed.TotalSeconds);
                    buckets.GetOrAdd(sec, static _ => new SecondBucketAccumulator()).RecordError();
                    poolBuckets?.GetOrAdd(sec, static _ => new SecondBucketAccumulator()).RecordError();
                    Interlocked.Increment(ref totalErrors);

                    var errKey = ClassifyException(ex);
                    errorsByType.AddOrUpdate(errKey, 1, static (_, c) => c + 1);

                    try
                    {
                        await Task.Delay(retryBackoff, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, ct);

        Task[] workers;
        if (isolatedPools is { Count: > 0 })
        {
            var list = new List<Task>(concurrency);
            foreach (var pool in isolatedPools)
            {
                for (var w = 0; w < pool.WorkerCount; w++)
                {
                    list.Add(SpawnWorker(pool.KeyIndices, pool.Buckets));
                }
            }

            workers = [.. list];
        }
        else
        {
            workers = Enumerable.Range(0, concurrency).Select(_ => SpawnWorker(null, null)).ToArray();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        loadSw.Stop();

        var maxSecond = Math.Max(durationSeconds - 1, buckets.Keys.DefaultIfEmpty(0).Max());
        var timeSeries = BuildTimeSeries(buckets, maxSecond, loadStartUtc);

        Dictionary<string, List<FailoverSecondBucket>>? perPrimarySeries = null;
        if (isolatedPools is { Count: > 0 })
        {
            perPrimarySeries = new Dictionary<string, List<FailoverSecondBucket>>(isolatedPools.Count, StringComparer.Ordinal);
            foreach (var pool in isolatedPools)
            {
                perPrimarySeries[pool.PrimaryLabel] = BuildTimeSeries(pool.Buckets, maxSecond, loadStartUtc);
            }
        }

        // Verify durable acknowledged writes across all touched grains (with retries during post-failover settle).
        var touchedIndices = new List<int>();
        for (var i = 0; i < keys.Length; i++)
        {
            if (Volatile.Read(ref lastAcked[i]) > 0)
            {
                touchedIndices.Add(i);
            }
        }

        var verifyDeadline = DateTimeOffset.UtcNow + verifyTimeout;
        var verifiedGrains = 0;
        var failedVerifyGrains = 0;
        var lostWrites = new ConcurrentBag<LostWriteDetail>();

        await Parallel.ForEachAsync(
            touchedIndices,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(concurrency, 64), CancellationToken = ct },
            async (idx, token) =>
            {
                var grainId = keys[idx];
                var expected = Volatile.Read(ref lastAcked[idx]);
                var grain = grainFactory.GetGrain<IBenchJournalGrain>(grainId);

                while (true)
                {
                    try
                    {
                        var actual = await grain.ReadVersionAsync().WaitAsync(callTimeout, token).ConfigureAwait(false);
                        Interlocked.Increment(ref verifiedGrains);
                        if (actual < expected)
                        {
                            lostWrites.Add(new LostWriteDetail(grainId, expected, actual));
                        }

                        return;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        if (DateTimeOffset.UtcNow >= verifyDeadline)
                        {
                            Interlocked.Increment(ref failedVerifyGrains);
                            return;
                        }

                        await Task.Delay(250, token).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);

        return new FailoverLoadWindowResult(
            GrainCount: keys.Length,
            TotalOps: totalOps,
            TotalErrors: totalErrors,
            Elapsed: loadSw.Elapsed,
            Samples: overallReservoir.Snapshot(),
            ErrorsByType: new Dictionary<string, long>(errorsByType, StringComparer.Ordinal),
            TimeSeries: timeSeries,
            UsedIsolatedPools: perPrimarySeries is not null,
            PerInitialPrimaryTimeSeries: perPrimarySeries,
            TouchedGrains: touchedIndices.Count,
            VerifiedGrains: verifiedGrains,
            FailedVerifyGrains: failedVerifyGrains,
            LostWriteDetails: [.. lostWrites],
            VerifyDeadline: verifyDeadline);
    }

    private async Task<(string[] Keys, List<PrimaryWorkerPool> Pools)> PartitionKeysByInitialPrimaryAsync(
        string runTag,
        int driverIndex,
        int grains,
        int concurrency,
        int durationSeconds,
        CancellationToken ct)
    {
        var routing = services.GetRequiredService<LocalRouting>();
        var current = await routing.ReadyAsync(ct).ConfigureAwait(false);
        var map = await current.View.ReadAsync(ct).ConfigureAwait(false);
        var reg = services.GetService<NodeRegistryResolver>()?.Current;
        var membership = services.GetService<IClusterMembershipService>();
        var members = membership?.CurrentSnapshot.Members;

        var primaryNodes = map.Ledgers
            .Where(static l => l.IsOpen)
            .Select(static l => l.Primary.Node)
            .Distinct()
            .Select(nodeId =>
            {
                var entry = reg?.Find(nodeId);
                string label;
                if (entry is not null && members is not null && members.TryGetValue(entry.Silo, out var member) && !string.IsNullOrWhiteSpace(member.Name))
                {
                    label = member.Name;
                }
                else if (entry?.Ordinal is { } ord)
                {
                    label = string.Create(CultureInfo.InvariantCulture, $"silo-{ord}");
                }
                else if (nodeId == current.Node)
                {
                    label = localSilo.Name;
                }
                else if (entry is not null)
                {
                    label = entry.Silo.ToParsableString();
                }
                else
                {
                    label = nodeId.ToString();
                }

                return (Node: nodeId, Ordinal: entry?.Ordinal ?? int.MaxValue, Label: label);
            })
            .OrderBy(static x => x.Ordinal)
            .ThenBy(static x => x.Label, StringComparer.Ordinal)
            .ToList();

        if (primaryNodes.Count == 0)
        {
            throw new InvalidOperationException("No open primary ledgers found in LedgerMapView for isolated s6-failover worker pools.");
        }

        var poolCount = primaryNodes.Count;
        var targetGrains = new int[poolCount];
        var baseGrains = grains / poolCount;
        var remGrains = grains % poolCount;
        for (var i = 0; i < poolCount; i++)
        {
            targetGrains[i] = Math.Max(1, baseGrains + (i < remGrains ? 1 : 0));
        }

        var nodeToPoolIdx = new Dictionary<NodeId, int>(poolCount);
        var poolKeyLists = new List<int>[poolCount];
        for (var i = 0; i < poolCount; i++)
        {
            nodeToPoolIdx[primaryNodes[i].Node] = i;
            poolKeyLists[i] = new List<int>(targetGrains[i]);
        }

        var totalTargetGrains = targetGrains.Sum();
        var keys = new string[totalTargetGrains];
        var grainType = grainFactory.GetGrain<IBenchJournalGrain>("__probe__").GetGrainId().Type;
        var assignedTotal = 0;
        long seq = 0;
        var maxAttempts = Math.Max(200_000L, totalTargetGrains * 128L);

        var sw = Stopwatch.StartNew();
        while (assignedTotal < totalTargetGrains && seq < maxAttempts)
        {
            var candidate = driverIndex >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"{runTag}:s6:d{driverIndex}:{seq}")
                : string.Create(CultureInfo.InvariantCulture, $"{runTag}:s6:{seq}");
            seq++;
            var grainId = GrainId.Create(grainType, IdSpan.Create(candidate));
            var rowKey = JournalRowKey.Encode(JournalId.FromGrainId(grainId).Value);
            if (map.FindOpen(rowKey.Span) is { } open
                && nodeToPoolIdx.TryGetValue(open.Primary.Node, out var poolIdx)
                && poolKeyLists[poolIdx].Count < targetGrains[poolIdx])
            {
                keys[assignedTotal] = candidate;
                poolKeyLists[poolIdx].Add(assignedTotal);
                assignedTotal++;
            }
        }

        sw.Stop();
        if (assignedTotal < totalTargetGrains)
        {
            throw new InvalidOperationException(
                $"Partitioned only {assignedTotal}/{totalTargetGrains} s6-failover keys across {poolCount} primaries after {seq} probes.");
        }

        var baseWorkers = concurrency / poolCount;
        var extraWorkers = concurrency % poolCount;
        var pools = new List<PrimaryWorkerPool>(poolCount);
        for (var i = 0; i < poolCount; i++)
        {
            var workerCount = baseWorkers + (i < extraWorkers ? 1 : 0);
            if (workerCount <= 0)
            {
                continue;
            }

            var poolBuckets = new ConcurrentDictionary<int, SecondBucketAccumulator>();
            for (var s = 0; s < durationSeconds; s++)
            {
                poolBuckets[s] = new SecondBucketAccumulator();
            }

            pools.Add(new PrimaryWorkerPool(
                PrimaryLabel: primaryNodes[i].Label,
                WorkerCount: workerCount,
                KeyIndices: [.. poolKeyLists[i]],
                Buckets: poolBuckets));
        }

        logger.LogInformation(
            "Partitioned {Grains} s6-failover keys into {PoolCount} primary-isolated worker pools in {ElapsedMs:F1} ms ({Probes} probes): {Pools}",
            totalTargetGrains,
            pools.Count,
            sw.Elapsed.TotalMilliseconds,
            seq,
            string.Join(", ", pools.Select(static p => $"{p.PrimaryLabel}={p.KeyIndices.Length}grains/{p.WorkerCount}workers")));

        return (keys, pools);
    }

    private static List<FailoverSecondBucket> BuildTimeSeries(
        ConcurrentDictionary<int, SecondBucketAccumulator> buckets,
        int maxSecond,
        DateTimeOffset loadStartUtc)
    {
        var series = new List<FailoverSecondBucket>(maxSecond + 1);
        for (var s = 0; s <= maxSecond; s++)
        {
            var acc = buckets.GetValueOrDefault(s);
            series.Add(acc is not null
                ? acc.Build(s, loadStartUtc.AddSeconds(s))
                : new FailoverSecondBucket(s, loadStartUtc.AddSeconds(s), 0, 0, 0, 0, 0, 0, 0));
        }

        return series;
    }

    private async Task<LocalBenchResponse> ExecuteLocalS6LoadAsync(
        LocalBenchRequest req,
        string runTag,
        int localGrains,
        int localConcurrency,
        int maxSamples,
        double keyspaceFraction,
        int localLedgerCount,
        CancellationToken ct)
    {
        var durationSeconds = Math.Max(1, req.DurationSeconds);
        var payload = CreatePayload(Math.Max(1, req.PayloadBytes));
        var retryBackoff = TimeSpan.FromMilliseconds(Math.Max(10, req.RetryBackoffMs));
        var callTimeout = TimeSpan.FromMilliseconds(Math.Max(500, req.CallTimeoutMs));
        var verifyTimeout = TimeSpan.FromSeconds(Math.Max(15, req.VerifyTimeoutSeconds));

        var loadWindow = await RunFailoverLoadWindowAsync(
            runTag,
            req.DriverIndex,
            localGrains,
            localConcurrency,
            durationSeconds,
            payload,
            retryBackoff,
            callTimeout,
            verifyTimeout,
            maxSamples,
            req.IsolateWorkersByPrimary,
            ct).ConfigureAwait(false);

        return new LocalBenchResponse(
            SiloAddress: localSilo.SiloAddress.ToParsableString(),
            SiloName: localSilo.Name,
            Ordinal: runtimeInfo.Ordinal,
            KeyspaceFraction: Math.Round(keyspaceFraction, 6),
            LocalLedgerCount: localLedgerCount,
            LocalGrains: loadWindow.GrainCount,
            LocalConcurrency: localConcurrency,
            Ops: loadWindow.TotalOps,
            Errors: loadWindow.TotalErrors,
            ElapsedMs: Math.Round(loadWindow.Elapsed.TotalMilliseconds, 2),
            OpsPerSec: loadWindow.Elapsed.TotalSeconds > 0 ? Math.Round(loadWindow.TotalOps / loadWindow.Elapsed.TotalSeconds, 2) : 0,
            ReadOps: 0,
            WriteOps: loadWindow.TotalOps,
            LatencyMs: LatencyStats.FromSamples([.. loadWindow.Samples]),
            LatencySamplesMs: Downsample(loadWindow.Samples, maxSamples),
            SecondaryLatencyMs: null,
            SecondaryLatencySamplesMs: null,
            TimeSeries: loadWindow.TimeSeries,
            PerInitialPrimaryTimeSeries: loadWindow.PerInitialPrimaryTimeSeries,
            ErrorsByType: loadWindow.ErrorsByType,
            TouchedGrains: loadWindow.TouchedGrains,
            VerifiedGrains: loadWindow.VerifiedGrains,
            FailedVerifyGrains: loadWindow.FailedVerifyGrains,
            LostWriteCount: loadWindow.LostWriteDetails.Count,
            LostWriteDetails: loadWindow.LostWriteDetails,
            Memory: CaptureLocalMemory());
    }

    private async Task<List<SiloAllocation>> PlanSiloAllocationsAsync(
        BenchRunRequest req,
        bool excludeFailoverTarget,
        CancellationToken ct)
    {
        var activeSilos = telemetry.GetActiveSilos().ToList();
        if (activeSilos.Count == 0)
        {
            throw new InvalidOperationException("No active silos found in cluster membership.");
        }

        // Optional driver selection (primarily for s6-failover).
        if (req.DriverSilos is { Count: > 0 })
        {
            var matched = activeSilos.Where(s => MatchesAnyFilter(s, req.DriverSilos)).ToList();
            if (matched.Count == 0)
            {
                throw new ArgumentException($"DriverSilos matched none of the active silos: {string.Join(", ", activeSilos.Select(static s => s.ToParsableString()))}");
            }

            activeSilos = matched;
        }
        else if (req.ExcludeSilos is { Count: > 0 })
        {
            var remaining = activeSilos.Where(s => !MatchesAnyFilter(s, req.ExcludeSilos)).ToList();
            if (remaining.Count > 0)
            {
                activeSilos = remaining;
            }
        }
        else if (excludeFailoverTarget && activeSilos.Count > 1)
        {
            // Keep all silos except the last (highest ordinal/address, e.g., silo-2) so drivers survive failover.
            var ordered = await OrderSilosByOrdinalAsync(activeSilos, ct).ConfigureAwait(false);
            activeSilos = ordered.Take(ordered.Count - 1).ToList();
        }

        var shares = new List<(SiloAddress Silo, int? Ordinal, double Share, int LedgerCount)>(activeSilos.Count);

        if (runtimeInfo.Mode == BenchMode.Clog && !excludeFailoverTarget)
        {
            var routing = services.GetRequiredService<LocalRouting>();
            var current = await routing.ReadyAsync(ct).ConfigureAwait(false);
            var map = await current.View.ReadAsync(ct).ConfigureAwait(false);
            var reg = services.GetService<NodeRegistryResolver>()?.Current;

            var nodeBySilo = new Dictionary<SiloAddress, (NodeId Node, int? Ordinal)>();
            nodeBySilo[localSilo.SiloAddress] = (current.Node, runtimeInfo.Ordinal);
            if (reg is not null)
            {
                foreach (var entry in reg.Nodes)
                {
                    if (!entry.IsRetired)
                    {
                        nodeBySilo[entry.Silo] = (entry.Node, entry.Ordinal);
                    }
                }
            }

            foreach (var silo in activeSilos)
            {
                var found = nodeBySilo.TryGetValue(silo, out var info);
                if (!found && reg is not null)
                {
                    var byEp = reg.Nodes.FirstOrDefault(e => !e.IsRetired && e.Silo.Endpoint.Equals(silo.Endpoint));
                    if (byEp is not null)
                    {
                        info = (byEp.Node, byEp.Ordinal);
                        found = true;
                    }
                }

                double fraction = 0;
                int count = 0;
                if (found)
                {
                    foreach (var ledger in map.Ledgers)
                    {
                        if (ledger.IsOpen && ledger.Primary.Node == info.Node)
                        {
                            count++;
                            fraction += Math.Pow(2.0, -ledger.Range.Depth);
                        }
                    }
                }

                shares.Add((silo, found ? info.Ordinal : null, fraction, count));
            }

            // Drop silos with 0 open ledgers only if other silos own keyspace.
            var totalShare = shares.Sum(static s => s.Share);
            if (totalShare > 0)
            {
                shares = shares.Where(static s => s.Share > 0).ToList();
                totalShare = shares.Sum(static s => s.Share);
                shares = shares.Select(s => (s.Silo, s.Ordinal, s.Share / totalShare, s.LedgerCount)).ToList();
            }
            else
            {
                var equal = 1.0 / shares.Count;
                shares = shares.Select(s => (s.Silo, s.Ordinal, equal, s.LedgerCount)).ToList();
            }
        }
        else
        {
            var equal = 1.0 / activeSilos.Count;
            for (var i = 0; i < activeSilos.Count; i++)
            {
                shares.Add((activeSilos[i], null, equal, 0));
            }
        }

        // Order deterministically by ordinal then silo address.
        shares = shares
            .OrderBy(static s => s.Ordinal ?? int.MaxValue)
            .ThenBy(static s => s.Silo.ToParsableString(), StringComparer.Ordinal)
            .ToList();

        var totalGrains = Math.Max(shares.Count, Math.Max(1, req.Grains));
        var totalConcurrency = Math.Max(shares.Count, Math.Max(1, req.Concurrency));

        var grainCounts = DistributeByFraction(shares.Select(static s => s.Share).ToList(), totalGrains);
        var concCounts = DistributeByFraction(shares.Select(static s => s.Share).ToList(), totalConcurrency);

        var allocations = new List<SiloAllocation>(shares.Count);
        for (var i = 0; i < shares.Count; i++)
        {
            allocations.Add(new SiloAllocation(
                Silo: shares[i].Silo,
                DriverIndex: i,
                Ordinal: shares[i].Ordinal,
                KeyspaceFraction: shares[i].Share,
                LocalLedgerCount: shares[i].LedgerCount,
                LocalGrains: grainCounts[i],
                LocalConcurrency: concCounts[i]));
        }

        logger.LogInformation(
            "Planned distributed run across {Count} silos: {Plan}",
            allocations.Count,
            string.Join(", ", allocations.Select(a => $"{a.Silo.Endpoint.Address}(ord={a.Ordinal},share={a.KeyspaceFraction:P1},ledgers={a.LocalLedgerCount},grains={a.LocalGrains},conc={a.LocalConcurrency})")));

        return allocations;
    }

    private async Task<List<SiloAddress>> OrderSilosByOrdinalAsync(List<SiloAddress> silos, CancellationToken ct)
    {
        if (runtimeInfo.Mode != BenchMode.Clog)
        {
            return silos.OrderBy(static s => s.ToParsableString(), StringComparer.Ordinal).ToList();
        }

        var reg = services.GetService<NodeRegistryResolver>()?.Current;
        if (reg is null)
        {
            return silos.OrderBy(static s => s.ToParsableString(), StringComparer.Ordinal).ToList();
        }

        await Task.CompletedTask;
        return silos
            .OrderBy(s => reg.Nodes.FirstOrDefault(e => !e.IsRetired && e.Silo.Equals(s))?.Ordinal ?? int.MaxValue)
            .ThenBy(static s => s.ToParsableString(), StringComparer.Ordinal)
            .ToList();
    }

    private async Task<LocalBenchResponse> DispatchLocalAsync(
        SiloAllocation alloc,
        LocalBenchRequest localReq,
        CancellationToken ct)
    {
        if (alloc.Silo.Equals(localSilo.SiloAddress))
        {
            return await RunLocalAsync(localReq, ct).ConfigureAwait(false);
        }

        var client = httpClientFactory.CreateClient("bench-peer");
        var url = string.Create(CultureInfo.InvariantCulture, $"http://{alloc.Silo.Endpoint.Address}:{runtimeInfo.HttpPort}/bench/run-local");
        using var httpResponse = await client.PostAsJsonAsync(url, localReq, ReportJsonOptions, ct).ConfigureAwait(false);
        if (!httpResponse.IsSuccessStatusCode)
        {
            var errBody = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Peer silo {alloc.Silo} ({url}) returned {(int)httpResponse.StatusCode}: {errBody}");
        }

        var result = await httpResponse.Content.ReadFromJsonAsync<LocalBenchResponse>(ReportJsonOptions, ct).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"Peer silo {alloc.Silo} returned an empty response.");
    }

    private async Task<(double KeyspaceFraction, int LocalLedgerCount)> GetLocalKeyspaceInfoAsync(CancellationToken ct)
    {
        if (runtimeInfo.Mode != BenchMode.Clog)
        {
            var count = Math.Max(1, telemetry.GetActiveSilos().Count);
            return (1.0 / count, 0);
        }

        var routing = services.GetService<LocalRouting>();
        if (routing is null)
        {
            return (0, 0);
        }

        var current = await routing.ReadyAsync(ct).ConfigureAwait(false);
        var map = await current.View.ReadAsync(ct).ConfigureAwait(false);
        double fraction = 0;
        var ledgers = 0;
        foreach (var ledger in map.Ledgers)
        {
            if (ledger.IsOpen && ledger.Primary.Node == current.Node)
            {
                ledgers++;
                fraction += Math.Pow(2.0, -ledger.Range.Depth);
            }
        }

        return (fraction, ledgers);
    }

    private async Task<string[]> GenerateGrainKeysAsync<TGrain>(
        string runTag,
        string scope,
        int count,
        int driverIndex,
        bool localPrimaryOnly,
        CancellationToken ct) where TGrain : IGrainWithStringKey
    {
        var keys = new string[count];
        var ordinalTag = runtimeInfo.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? $"d{driverIndex}";

        if (!localPrimaryOnly || runtimeInfo.Mode != BenchMode.Clog)
        {
            for (var i = 0; i < count; i++)
            {
                keys[i] = string.Create(CultureInfo.InvariantCulture, $"{runTag}:{scope}:d{driverIndex}:{i}");
            }

            return keys;
        }

        var routing = services.GetRequiredService<LocalRouting>();
        var current = await routing.ReadyAsync(ct).ConfigureAwait(false);
        var map = await current.View.ReadAsync(ct).ConfigureAwait(false);
        var localNode = current.Node;

        var ownedLedgers = map.Ledgers.Count(l => l.IsOpen && l.Primary.Node == localNode);
        if (ownedLedgers == 0)
        {
            throw new InvalidOperationException(
                $"Local silo {localSilo.SiloAddress} (node {localNode}) owns 0 open primary ledgers; cannot generate local-primary keys.");
        }

        var sw = Stopwatch.StartNew();
        var grainType = grainFactory.GetGrain<TGrain>("__probe__").GetGrainId().Type;
        var found = 0;
        long seq = 0;
        var maxAttempts = Math.Max(200_000L, count * 128L);

        while (found < count && seq < maxAttempts)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{runTag}:local:{scope}:{ordinalTag}:{seq}");
            seq++;
            var grainId = GrainId.Create(grainType, IdSpan.Create(candidate));
            var rowKey = JournalRowKey.Encode(JournalId.FromGrainId(grainId).Value);
            if (map.FindOpen(rowKey.Span) is { } open && open.Primary.Node == localNode)
            {
                keys[found++] = candidate;
            }
        }

        sw.Stop();
        if (found < count)
        {
            throw new InvalidOperationException(
                $"Generated only {found}/{count} local-primary keys for node {localNode} after {seq} probes.");
        }

        logger.LogInformation(
            "Generated {Count} local-primary {GrainType} keys for node {Node} ({OwnedLedgers} ledgers) in {ElapsedMs:F1} ms ({Probes} probes)",
            count, typeof(TGrain).Name, localNode, ownedLedgers, sw.Elapsed.TotalMilliseconds, seq);

        return keys;
    }

    private void CacheLocalKeys(string runTag, string[] keys)
    {
        if (_cachedLocalKeys.Count >= 4)
        {
            _cachedLocalKeys.Clear();
        }

        _cachedLocalKeys[runTag] = keys;
    }

    private LocalBenchResponse BuildLocalResponse(
        double keyspaceFraction,
        int localLedgerCount,
        int localGrains,
        int localConcurrency,
        long ops,
        long errors,
        TimeSpan elapsed,
        long readOps,
        long writeOps,
        List<double> latencies,
        List<double>? secondaryLatencies,
        int maxSamples)
    {
        var primaryCopy = new List<double>(latencies);
        List<double>? secondaryCopy = secondaryLatencies is not null ? new List<double>(secondaryLatencies) : null;

        return new LocalBenchResponse(
            SiloAddress: localSilo.SiloAddress.ToParsableString(),
            SiloName: localSilo.Name,
            Ordinal: runtimeInfo.Ordinal,
            KeyspaceFraction: Math.Round(keyspaceFraction, 6),
            LocalLedgerCount: localLedgerCount,
            LocalGrains: localGrains,
            LocalConcurrency: localConcurrency,
            Ops: ops,
            Errors: errors,
            ElapsedMs: Math.Round(elapsed.TotalMilliseconds, 2),
            OpsPerSec: elapsed.TotalSeconds > 0 ? Math.Round(ops / elapsed.TotalSeconds, 2) : 0,
            ReadOps: readOps,
            WriteOps: writeOps,
            LatencyMs: LatencyStats.FromSamples(primaryCopy),
            LatencySamplesMs: maxSamples > 0 ? Downsample(latencies, maxSamples) : [],
            SecondaryLatencyMs: secondaryCopy is not null ? LatencyStats.FromSamples(secondaryCopy) : null,
            SecondaryLatencySamplesMs: secondaryLatencies is not null && maxSamples > 0 ? Downsample(secondaryLatencies, maxSamples) : null,
            TimeSeries: null,
            PerInitialPrimaryTimeSeries: null,
            ErrorsByType: null,
            TouchedGrains: 0,
            VerifiedGrains: 0,
            FailedVerifyGrains: 0,
            LostWriteCount: 0,
            LostWriteDetails: null,
            Memory: CaptureLocalMemory());
    }

    private SiloMemorySnapshot CaptureLocalMemory()
    {
        using var proc = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        return new SiloMemorySnapshot(
            SiloAddress: localSilo.SiloAddress.ToParsableString(),
            SiloName: localSilo.Name,
            WorkingSetBytes: proc.WorkingSet64,
            GcTotalMemoryBytes: GC.GetTotalMemory(false),
            HeapSizeBytes: gcInfo.HeapSizeBytes,
            TotalCommittedBytes: gcInfo.TotalCommittedBytes);
    }

    private static List<PerSiloBenchSummary> ToPerSiloSummaries(LocalBenchResponse[] responses) =>
        responses.Select(static r => new PerSiloBenchSummary(
            SiloAddress: r.SiloAddress,
            SiloName: r.SiloName,
            Ordinal: r.Ordinal,
            KeyspaceFraction: r.KeyspaceFraction,
            LocalLedgerCount: r.LocalLedgerCount,
            LocalGrains: r.LocalGrains,
            LocalConcurrency: r.LocalConcurrency,
            Ops: r.Ops,
            Errors: r.Errors,
            ElapsedMs: r.ElapsedMs,
            OpsPerSec: r.OpsPerSec,
            LatencyMs: r.LatencyMs,
            SecondaryLatencyMs: r.SecondaryLatencyMs)).ToList();

    private static List<double> MergeSamples(IEnumerable<double[]> arrays)
    {
        var list = new List<double>();
        foreach (var arr in arrays)
        {
            if (arr.Length > 0)
            {
                list.AddRange(arr);
            }
        }

        return list;
    }

    private static double[] Downsample(List<double> samples, int maxCount)
    {
        if (samples.Count <= maxCount)
        {
            return [.. samples];
        }

        var result = new double[maxCount];
        var step = (double)samples.Count / maxCount;
        for (var i = 0; i < maxCount; i++)
        {
            result[i] = samples[Math.Min(samples.Count - 1, (int)(i * step))];
        }

        return result;
    }

    private static int[] DistributeByFraction(List<double> fractions, int total)
    {
        var result = new int[fractions.Count];
        if (fractions.Count == 0)
        {
            return result;
        }

        var assigned = 0;
        var maxIdx = 0;
        for (var i = 0; i < fractions.Count; i++)
        {
            result[i] = Math.Max(1, (int)Math.Round(total * fractions[i]));
            assigned += result[i];
            if (fractions[i] > fractions[maxIdx])
            {
                maxIdx = i;
            }
        }

        var diff = total - assigned;
        if (diff != 0 && result[maxIdx] + diff >= 1)
        {
            result[maxIdx] += diff;
        }

        return result;
    }

    private static int[] AllocateProportional(List<SiloAllocation> allocations, int total) =>
        DistributeByFraction(allocations.Select(static a => a.KeyspaceFraction).ToList(), total);

    private static bool MatchesAnyFilter(SiloAddress silo, List<string> filters)
    {
        var full = silo.ToParsableString();
        var ip = silo.Endpoint.Address.ToString();
        var ep = silo.Endpoint.ToString();
        foreach (var f in filters)
        {
            if (string.IsNullOrWhiteSpace(f))
            {
                continue;
            }

            var trimmed = f.Trim();
            if (string.Equals(full, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ip, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ep, trimmed, StringComparison.OrdinalIgnoreCase)
                || full.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<FailoverSecondBucket> MergeFailoverTimeSeries(IEnumerable<List<FailoverSecondBucket>?> seriesList)
    {
        var nonNull = seriesList.Where(static s => s is { Count: > 0 }).Select(static s => s!).ToList();
        if (nonNull.Count == 0)
        {
            return [];
        }

        var maxSeconds = nonNull.Max(static s => s.Count);
        var merged = new List<FailoverSecondBucket>(maxSeconds);
        for (var s = 0; s < maxSeconds; s++)
        {
            long ops = 0, errors = 0;
            double maxMs = 0;
            double wP50 = 0, wP95 = 0, wP99 = 0;
            DateTimeOffset ts = DateTimeOffset.UtcNow;
            var hasTs = false;

            foreach (var series in nonNull)
            {
                if (s >= series.Count)
                {
                    continue;
                }

                var b = series[s];
                if (!hasTs)
                {
                    ts = b.UtcTimestamp;
                    hasTs = true;
                }

                ops += b.Ops;
                errors += b.Errors;
                wP50 += b.P50Ms * b.Ops;
                wP95 += b.P95Ms * b.Ops;
                wP99 += b.P99Ms * b.Ops;
                if (b.MaxMs > maxMs)
                {
                    maxMs = b.MaxMs;
                }
            }

            var total = ops + errors;
            var errPct = total > 0 ? Math.Round(100.0 * errors / total, 2) : 0;
            merged.Add(new FailoverSecondBucket(
                Second: s,
                UtcTimestamp: ts,
                Ops: ops,
                Errors: errors,
                ErrorRatePct: errPct,
                P50Ms: ops > 0 ? Math.Round(wP50 / ops, 3) : 0,
                P95Ms: ops > 0 ? Math.Round(wP95 / ops, 3) : 0,
                P99Ms: ops > 0 ? Math.Round(wP99 / ops, 3) : 0,
                MaxMs: Math.Round(maxMs, 3)));
        }

        return merged;
    }
}

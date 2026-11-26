using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Orleans.Durable.Jobs;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Hosting;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Bench;

public sealed partial class BenchRunner(
    BenchRuntimeInfo runtimeInfo,
    IGrainFactory grainFactory,
    ILocalSiloDetails localSilo,
    ClusterTelemetryAggregator telemetry,
    BenchResultsUploader resultsUploader,
    IHttpClientFactory httpClientFactory,
    IServiceProvider services,
    ILogger<BenchRunner> logger)
{
    private const int MaxLatencyReservoir = 200_000;

    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        var clusterSnap = await telemetry.SnapshotAsync(runTag: null, cancellationToken).ConfigureAwait(false);
        var activeSilos = telemetry.GetActiveSilos().Select(static s => s.ToParsableString()).ToList();

        object? durableStatusObj = null;
        var openLedgers = new List<object>();
        var closedLedgers = new List<object>();
        var keyspaceByPrimary = new Dictionary<string, KeyspaceOwnership>(StringComparer.Ordinal);

        if (runtimeInfo.Mode == BenchMode.Clog)
        {
            var dsStatus = services.GetService<DurableStorageStatus>();
            var routing = services.GetService<LocalRouting>();
            var registryResolver = services.GetService<NodeRegistryResolver>();

            if (dsStatus is not null)
            {
                var nodeStatus = dsStatus.NodeStatus;
                durableStatusObj = new
                {
                    journalsState = dsStatus.Journals.ToString(),
                    nodeId = nodeStatus?.Node.ToString(),
                    observedAt = nodeStatus?.ObservedAt,
                    resources = nodeStatus?.Resources is { } res
                        ? new
                        {
                            res.MeasuredAt,
                            res.DiskUsedBytes,
                            res.DiskFreeBytes,
                            res.DiskBudgetBytes,
                            res.UnderPressure,
                            res.RetainedWalFiles,
                            res.LiveReplicas,
                        }
                        : null,
                    primaries = nodeStatus?.Primaries.Select(static p => new
                    {
                        ledgerId = p.Ledger.Value,
                        appliedBelow = p.AppliedBelow?.Value,
                        contiguousTail = p.ContiguousTail.Value,
                        watermark = p.Watermark.Value,
                        undrainedBytes = p.Undrained.Bytes,
                        throttledByCount = p.ThrottledBy.Length,
                    }).ToList(),
                };
            }

            if (routing?.Current is { } current)
            {
                try
                {
                    var map = await current.View.ReadAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    var reg = registryResolver?.Current;
                    var nodeAgg = new Dictionary<NodeId, (int Count, double Fraction)>();

                    foreach (var ledger in map.Ledgers)
                    {
                        var fraction = Math.Pow(2.0, -ledger.Range.Depth);
                        var info = new
                        {
                            id = ledger.Id.Value,
                            primary = ledger.Primary.Node.ToString(),
                            depth = ledger.Range.Depth,
                            keyspaceFraction = Math.Round(fraction, 6),
                            isOpen = ledger.IsOpen,
                        };
                        if (ledger.IsOpen)
                        {
                            openLedgers.Add(info);
                            var cur = nodeAgg.GetValueOrDefault(ledger.Primary.Node);
                            nodeAgg[ledger.Primary.Node] = (cur.Count + 1, cur.Fraction + fraction);
                        }
                        else
                        {
                            closedLedgers.Add(info);
                        }
                    }

                    foreach (var (nodeId, agg) in nodeAgg.OrderBy(static kv => kv.Key.ToString(), StringComparer.Ordinal))
                    {
                        var entry = reg?.Find(nodeId);
                        var key = nodeId.ToString();
                        keyspaceByPrimary[key] = new KeyspaceOwnership(
                            PrimaryNode: key,
                            SiloAddress: entry?.Silo.ToParsableString(),
                            Ordinal: entry?.Ordinal,
                            LedgerCount: agg.Count,
                            KeyspaceFraction: Math.Round(agg.Fraction, 6));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Could not read ledger map for /status.");
                }
            }
        }

        using var proc = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();

        return new
        {
            mode = runtimeInfo.Mode.ToString(),
            siloAddress = localSilo.SiloAddress.ToParsableString(),
            siloName = localSilo.Name,
            walStorageType = runtimeInfo.WalStorageType,
            orleansUseIoUring = runtimeInfo.OrleansUseIoUring,
            clusterId = runtimeInfo.ClusterId,
            serviceId = runtimeInfo.ServiceId,
            ordinal = runtimeInfo.Ordinal,
            resolvedVoters = runtimeInfo.ResolvedVoters,
            activeSilos,
            sealedJobShardPublisherRegistered = services.GetService<ISealedJobShardPublisher>() is not null,
            resultsUploadEnabled = resultsUploader.IsEnabled,
            durableStorageStatus = durableStatusObj,
            openLedgerCount = openLedgers.Count,
            openLedgers,
            closedLedgerCount = closedLedgers.Count,
            closedLedgers,
            keyspaceByPrimary,
            blobCounters = new
            {
                cluster = clusterSnap.AggregateBlobs,
                bySilo = clusterSnap.BlobsBySilo,
            },
            jobCounters = new
            {
                totalExecuted = clusterSnap.TotalJobExecuted,
                countsByRun = clusterSnap.CountsByRun,
            },
            memory = new
            {
                localWorkingSetBytes = proc.WorkingSet64,
                localGcTotalMemoryBytes = GC.GetTotalMemory(false),
                localHeapSizeBytes = gcInfo.HeapSizeBytes,
                bySilo = clusterSnap.MemoryBySilo,
            },
            unreachableSilos = clusterSnap.UnreachableSilos,
        };
    }

    public async Task<RollAndDrainResult> RollAndDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (runtimeInfo.Mode != BenchMode.Clog)
        {
            return new RollAndDrainResult(
                Skipped: true,
                Reason: "Baseline mode has no Durable Storage ledgers to roll or drain.",
                RolledLedgerIds: [],
                ElapsedMs: 0,
                OpenLedgersAfter: 0,
                ClosedLedgersAfter: 0,
                BlobDelta: BlobCounterSnapshot.Empty,
                PerSiloBlobDelta: [],
                UnreachableSilos: []);
        }

        var routing = services.GetRequiredService<LocalRouting>();
        var currentRouting = await routing.ReadyAsync(cancellationToken).ConfigureAwait(false);
        var beforeBlobs = await telemetry.SnapshotAsync(runTag: null, cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        var map = await currentRouting.View.ReadAsync(cancellationToken).ConfigureAwait(false);
        var targetIds = map.Ledgers
            .Where(static l => l.IsOpen)
            .Select(static l => l.Id)
            .ToList();

        if (targetIds.Count == 0)
        {
            return new RollAndDrainResult(
                Skipped: false,
                Reason: "No open ledgers found in map.",
                RolledLedgerIds: [],
                ElapsedMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                OpenLedgersAfter: 0,
                ClosedLedgersAfter: map.Ledgers.Count(static l => !l.IsOpen),
                BlobDelta: BlobCounterSnapshot.Empty,
                PerSiloBlobDelta: [],
                UnreachableSilos: []);
        }

        var controller = grainFactory.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key);
        await Task.WhenAll(targetIds.Select(id => controller.RequestRollAsync(id))).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + timeout;
        var lastReissue = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            map = await currentRouting.View.ReadAsync(cancellationToken).ConfigureAwait(false);

            var remaining = targetIds.Where(id => map.Get(id) is not null).ToList();
            if (remaining.Count == 0)
            {
                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var remainingDesc = string.Join(", ", remaining.Select(id => $"{id.Value}({(map.Get(id)?.IsOpen == true ? "open" : "closed")})"));
                throw new TimeoutException(
                    $"Timed out after {timeout.TotalSeconds:F0}s waiting for rolled ledgers to drain & drop: {remainingDesc}");
            }

            // Re-request roll for any target ledger still open (controller may have moved or coalesced).
            if (lastReissue.Elapsed >= TimeSpan.FromSeconds(2))
            {
                var stillOpen = remaining.Where(id => map.Get(id)?.IsOpen == true).ToList();
                if (stillOpen.Count > 0)
                {
                    await Task.WhenAll(stillOpen.Select(id => controller.RequestRollAsync(id))).ConfigureAwait(false);
                }

                lastReissue.Restart();
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        sw.Stop();
        var afterBlobs = await telemetry.SnapshotAsync(runTag: null, cancellationToken).ConfigureAwait(false);
        var blobDiff = ClusterTelemetryAggregator.DiffBlobs(beforeBlobs, afterBlobs);

        return new RollAndDrainResult(
            Skipped: false,
            Reason: null,
            RolledLedgerIds: targetIds.Select(static id => id.Value).ToList(),
            ElapsedMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            OpenLedgersAfter: map.Ledgers.Count(static l => l.IsOpen),
            ClosedLedgersAfter: map.Ledgers.Count(static l => !l.IsOpen),
            BlobDelta: blobDiff.Total,
            PerSiloBlobDelta: blobDiff.BySilo,
            UnreachableSilos: blobDiff.UnreachableSilos);
    }

    public async Task<BenchRunResponse> RunAsync(BenchRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scenario = (request.Scenario ?? string.Empty).Trim().ToLowerInvariant();
        var runTag = string.IsNullOrWhiteSpace(request.RunTag)
            ? $"{scenario}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
            : request.RunTag.Trim();

        var startedAtUtc = DateTimeOffset.UtcNow;
        var totalSw = Stopwatch.StartNew();
        var beforeAll = await telemetry.SnapshotAsync(runTag: null, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Starting benchmark scenario={Scenario} runTag={RunTag} mode={Mode} distributed={Distributed} localPrimaryOnly={LocalPrimaryOnly} grains={Grains} concurrency={Concurrency} duration={Duration}s",
            scenario, runTag, runtimeInfo.Mode, request.Distributed, request.LocalPrimaryOnly, request.Grains, request.Concurrency, request.DurationSeconds);

        object scenarioResult = scenario switch
        {
            "s1-raw-write" => request.Distributed
                ? await RunS1RawWriteDistributedAsync(request, runTag, cancellationToken).ConfigureAwait(false)
                : await RunS1RawWriteAsync(request, runTag, cancellationToken).ConfigureAwait(false),
            "s2-large-journals" => request.Distributed
                ? await RunS2LargeJournalsDistributedAsync(request, runTag, cancellationToken).ConfigureAwait(false)
                : await RunS2LargeJournalsAsync(request, runTag, cancellationToken).ConfigureAwait(false),
            "s3-jobs-early-cancel" => request.Distributed
                ? await RunS3JobsEarlyCancelDistributedAsync(request, runTag, cancellationToken).ConfigureAwait(false)
                : await RunS3JobsEarlyCancelAsync(request, runTag, cancellationToken).ConfigureAwait(false),
            "s4a-jobs-open-fire" => request.Distributed
                ? await RunS4aJobsOpenFireDistributedAsync(request, runTag, cancellationToken).ConfigureAwait(false)
                : await RunS4aJobsOpenFireAsync(request, runTag, cancellationToken).ConfigureAwait(false),
            "s4b-jobs-sealed-drain" => request.Distributed
                ? await RunS4bJobsSealedDrainDistributedAsync(request, runTag, cancellationToken).ConfigureAwait(false)
                : await RunS4bJobsSealedDrainAsync(request, runTag, cancellationToken).ConfigureAwait(false),
            "s6-failover" => request.Distributed
                ? await RunS6FailoverDistributedAsync(request, runTag, cancellationToken).ConfigureAwait(false)
                : await RunS6FailoverAsync(request, runTag, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException(
                $"Unknown scenario '{request.Scenario}'. Supported: s1-raw-write, s2-large-journals, s3-jobs-early-cancel, s4a-jobs-open-fire, s4b-jobs-sealed-drain, s6-failover.",
                nameof(request)),
        };

        var afterAll = await telemetry.SnapshotAsync(runTag: null, cancellationToken).ConfigureAwait(false);
        var overallBlobDiff = ClusterTelemetryAggregator.DiffBlobs(beforeAll, afterAll);

        var includeSeries = scenario == "s6-failover";
        var (siloTelemetry, lifecycleEvents, telemetryUnreachable) = await telemetry.CollectSiloTelemetryAsync(
            startedAtUtc.AddSeconds(-1),
            includeSeries,
            cancellationToken).ConfigureAwait(false);

        var unreachable = overallBlobDiff.UnreachableSilos
            .Union(telemetryUnreachable, StringComparer.Ordinal)
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        totalSw.Stop();
        var completedAtUtc = DateTimeOffset.UtcNow;

        var response = new BenchRunResponse(
            Scenario: scenario,
            RunTag: runTag,
            Mode: runtimeInfo.Mode.ToString(),
            WalStorageType: runtimeInfo.WalStorageType,
            CoordinatorSilo: localSilo.SiloAddress.ToParsableString(),
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: completedAtUtc,
            TotalElapsedMs: Math.Round(totalSw.Elapsed.TotalMilliseconds, 2),
            Result: scenarioResult,
            BlobDelta: overallBlobDiff.Total,
            PerSiloBlobDelta: overallBlobDiff.BySilo,
            SiloTelemetry: siloTelemetry,
            Events: lifecycleEvents,
            UnreachableSilos: unreachable,
            UploadedResultUri: null);

        if (resultsUploader.IsEnabled)
        {
            var uploadedUri = await resultsUploader.TryUploadAsync(
                scenario,
                runTag,
                runtimeInfo.Mode.ToString(),
                response,
                ReportJsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (uploadedUri is not null)
            {
                response = response with { UploadedResultUri = uploadedUri };
            }
        }

        logger.LogInformation(
            "Completed benchmark scenario={Scenario} runTag={RunTag} elapsedMs={ElapsedMs:F1} blobTotal={BlobTotal}",
            scenario, runTag, response.TotalElapsedMs, response.BlobDelta.Total);

        return response;
    }

    private async Task<S1RawWriteResult> RunS1RawWriteAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var grains = Math.Max(1, req.Grains);
        var concurrency = Math.Max(1, req.Concurrency);
        var payload = CreatePayload(Math.Max(1, req.PayloadBytes));
        var warmup = TimeSpan.FromSeconds(Math.Max(0, req.WarmupSeconds));
        var duration = TimeSpan.FromSeconds(Math.Max(1, req.DurationSeconds));
        var keys = await GenerateGrainKeysAsync<IBenchJournalGrain>(runTag, "s1", grains, driverIndex: 0, req.LocalPrimaryOnly, ct).ConfigureAwait(false);

        if (warmup > TimeSpan.Zero)
        {
            await RunWorkersForDurationAsync(
                concurrency,
                warmup,
                async (workerId, iter, token) =>
                {
                    var keyIdx = (int)((iter * concurrency + workerId) % keys.Length);
                    var grain = grainFactory.GetGrain<IBenchJournalGrain>(keys[keyIdx]);
                    await grain.WriteAsync(payload).ConfigureAwait(false);
                },
                recordStats: false,
                ct).ConfigureAwait(false);
        }

        var measuredBefore = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var (ops, errors, elapsed, latencies) = await RunWorkersForDurationAsync(
            concurrency,
            duration,
            async (workerId, iter, token) =>
            {
                var keyIdx = Random.Shared.Next(keys.Length);
                var grain = grainFactory.GetGrain<IBenchJournalGrain>(keys[keyIdx]);
                await grain.WriteAsync(payload).ConfigureAwait(false);
            },
            recordStats: true,
            ct).ConfigureAwait(false);

        var snap = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var measuredBlobDiff = ClusterTelemetryAggregator.DiffBlobs(measuredBefore, snap);

        return new S1RawWriteResult(
            Grains: grains,
            Concurrency: concurrency,
            PayloadBytes: payload.Length,
            WarmupSeconds: req.WarmupSeconds,
            DurationSeconds: req.DurationSeconds,
            Ops: ops,
            Errors: errors,
            ElapsedMs: Math.Round(elapsed.TotalMilliseconds, 2),
            OpsPerSec: elapsed.TotalSeconds > 0 ? Math.Round(ops / elapsed.TotalSeconds, 2) : 0,
            LatencyMs: LatencyStats.FromSamples(latencies),
            MeasuredBlobDelta: measuredBlobDiff.Total,
            MemoryBySilo: snap.MemoryBySilo);
    }

    private async Task<S2LargeJournalsResult> RunS2LargeJournalsAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var grains = Math.Max(1, req.Grains);
        var concurrency = Math.Max(1, req.Concurrency);
        var payload = CreatePayload(Math.Max(1, req.PayloadBytes));

        // Phase A: Populate
        var popBefore = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var popStats = await RunIndexedWorkAsync(
            grains,
            concurrency,
            async (idx, token) =>
            {
                var grain = grainFactory.GetGrain<IBenchJournalGrain>($"{runTag}:s2:{idx}");
                await grain.WriteAsync(payload).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        var popAfter = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var popBlobDiff = ClusterTelemetryAggregator.DiffBlobs(popBefore, popAfter);

        var populateResult = new S2PopulatePhaseResult(
            Grains: grains,
            Concurrency: concurrency,
            PayloadBytes: payload.Length,
            Ops: popStats.Ops,
            Errors: popStats.Errors,
            ElapsedMs: Math.Round(popStats.Elapsed.TotalMilliseconds, 2),
            OpsPerSec: popStats.Elapsed.TotalSeconds > 0 ? Math.Round(popStats.Ops / popStats.Elapsed.TotalSeconds, 2) : 0,
            LatencyMs: LatencyStats.FromSamples(popStats.Latencies),
            BlobDelta: popBlobDiff.Total,
            MemoryBySilo: popAfter.MemoryBySilo);

        // Phase B: Hot/Cold Zipfian or uniform mix
        var readRatio = Math.Clamp(req.ReadRatio, 0.0, 1.0);
        var zipf = req.ZipfExponent > 0 ? new ZipfSampler(grains, req.ZipfExponent) : null;
        var mixDuration = TimeSpan.FromSeconds(Math.Max(1, req.DurationSeconds));
        var mixBefore = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);

        long readOps = 0, writeOps = 0, mixErrors = 0;
        var readReservoir = new LatencyReservoir(MaxLatencyReservoir);
        var writeReservoir = new LatencyReservoir(MaxLatencyReservoir);
        var mixSw = Stopwatch.StartNew();

        var mixWorkers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            while (mixSw.Elapsed < mixDuration && !ct.IsCancellationRequested)
            {
                var idx = zipf is not null ? zipf.Next() : Random.Shared.Next(grains);
                var isRead = Random.Shared.NextDouble() < readRatio;
                var grain = grainFactory.GetGrain<IBenchJournalGrain>($"{runTag}:s2:{idx}");
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
                    Interlocked.Increment(ref mixErrors);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(mixWorkers).ConfigureAwait(false);
        mixSw.Stop();

        var mixAfter = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var mixBlobDiff = ClusterTelemetryAggregator.DiffBlobs(mixBefore, mixAfter);
        var readSamples = readReservoir.Snapshot();
        var writeSamples = writeReservoir.Snapshot();
        var allMixSamples = new List<double>(readSamples.Count + writeSamples.Count);
        allMixSamples.AddRange(readSamples);
        allMixSamples.AddRange(writeSamples);
        var totalMixOps = readOps + writeOps;

        var mixResult = new S2MixPhaseResult(
            DurationSeconds: req.DurationSeconds,
            ReadRatio: readRatio,
            ZipfExponent: req.ZipfExponent,
            TotalOps: totalMixOps,
            ReadOps: readOps,
            WriteOps: writeOps,
            Errors: mixErrors,
            ElapsedMs: Math.Round(mixSw.Elapsed.TotalMilliseconds, 2),
            OpsPerSec: mixSw.Elapsed.TotalSeconds > 0 ? Math.Round(totalMixOps / mixSw.Elapsed.TotalSeconds, 2) : 0,
            ReadLatencyMs: LatencyStats.FromSamples(readSamples),
            WriteLatencyMs: LatencyStats.FromSamples(writeSamples),
            OverallLatencyMs: LatencyStats.FromSamples(allMixSamples),
            BlobDelta: mixBlobDiff.Total);

        // Phase C: Optional Cold Read after deactivate + optional rollAndDrain
        var coldCount = Math.Min(grains, req.ColdSampleGrains ?? Math.Min(2000, grains));
        RollAndDrainResult? rollResult = null;
        S2ColdReadPhaseResult? coldResult = null;

        if (coldCount > 0)
        {
            await RunIndexedWorkAsync(
                coldCount,
                concurrency,
                async (idx, token) =>
                {
                    var grain = grainFactory.GetGrain<IBenchJournalGrain>($"{runTag}:s2:{idx}");
                    await grain.DeactivateNowAsync().ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            await Task.Delay(500, ct).ConfigureAwait(false);

            if (req.RollAndDrain && runtimeInfo.Mode == BenchMode.Clog)
            {
                rollResult = await RollAndDrainAsync(TimeSpan.FromSeconds(Math.Max(30, req.WaitTimeoutSeconds)), ct).ConfigureAwait(false);
            }

            var coldBefore = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
            var coldStats = await RunIndexedWorkAsync(
                coldCount,
                concurrency,
                async (idx, token) =>
                {
                    var grain = grainFactory.GetGrain<IBenchJournalGrain>($"{runTag}:s2:{idx}");
                    var len = await grain.ReadLengthAsync().ConfigureAwait(false);
                    if (len != payload.Length)
                    {
                        throw new InvalidOperationException($"Cold read mismatch for grain {idx}: expected {payload.Length} bytes, got {len}.");
                    }
                },
                ct).ConfigureAwait(false);
            var coldAfter = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
            var coldBlobDiff = ClusterTelemetryAggregator.DiffBlobs(coldBefore, coldAfter);

            coldResult = new S2ColdReadPhaseResult(
                SampleGrains: coldCount,
                Ops: coldStats.Ops,
                Errors: coldStats.Errors,
                ElapsedMs: Math.Round(coldStats.Elapsed.TotalMilliseconds, 2),
                OpsPerSec: coldStats.Elapsed.TotalSeconds > 0 ? Math.Round(coldStats.Ops / coldStats.Elapsed.TotalSeconds, 2) : 0,
                LatencyMs: LatencyStats.FromSamples(coldStats.Latencies),
                BlobDelta: coldBlobDiff.Total);
        }

        return new S2LargeJournalsResult(populateResult, mixResult, rollResult, coldResult);
    }

    private async Task<S3JobsEarlyCancelResult> RunS3JobsEarlyCancelAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var grains = Math.Max(1, req.Grains);
        var concurrency = Math.Max(1, req.Concurrency);
        var warmup = TimeSpan.FromSeconds(Math.Max(0, req.WarmupSeconds));
        var duration = TimeSpan.FromSeconds(Math.Max(1, req.DurationSeconds));
        const int oneHourMs = 3_600_000;

        if (warmup > TimeSpan.Zero)
        {
            await RunWorkersForDurationAsync(
                concurrency,
                warmup,
                async (workerId, iter, token) =>
                {
                    var idx = (int)((iter * concurrency + workerId) % grains);
                    var grain = grainFactory.GetGrain<IBenchJobGrain>($"{runTag}:s3:{idx}");
                    var jobId = $"warm-{workerId}-{iter}";
                    await grain.ScheduleWithStateAsync(jobId, runTag, oneHourMs).ConfigureAwait(false);
                    _ = await grain.CancelWithStateAsync(jobId).ConfigureAwait(false);
                },
                recordStats: false,
                ct).ConfigureAwait(false);
        }

        var measuredBefore = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var schedReservoir = new LatencyReservoir(MaxLatencyReservoir);
        var cancelReservoir = new LatencyReservoir(MaxLatencyReservoir);
        long cycles = 0, errors = 0;
        var sw = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, concurrency).Select(workerId => Task.Run(async () =>
        {
            long iter = 0;
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                iter++;
                var idx = (int)((workerId + iter * concurrency) % grains);
                var grain = grainFactory.GetGrain<IBenchJobGrain>($"{runTag}:s3:{idx}");
                var jobId = $"job-{workerId}-{iter}";

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

        var measuredAfter = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);
        var measuredBlobDiff = ClusterTelemetryAggregator.DiffBlobs(measuredBefore, measuredAfter);

        RollAndDrainResult? postRoll = null;
        if ((req.RollAfterSchedule || req.RollAndDrain) && runtimeInfo.Mode == BenchMode.Clog)
        {
            postRoll = await RollAndDrainAsync(TimeSpan.FromSeconds(Math.Max(30, req.WaitTimeoutSeconds)), ct).ConfigureAwait(false);
        }

        return new S3JobsEarlyCancelResult(
            Grains: grains,
            Concurrency: concurrency,
            WarmupSeconds: req.WarmupSeconds,
            DurationSeconds: req.DurationSeconds,
            Cycles: cycles,
            Errors: errors,
            ElapsedMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            CyclesPerSec: sw.Elapsed.TotalSeconds > 0 ? Math.Round(cycles / sw.Elapsed.TotalSeconds, 2) : 0,
            ScheduleLatencyMs: LatencyStats.FromSamples(schedReservoir.Snapshot()),
            CancelLatencyMs: LatencyStats.FromSamples(cancelReservoir.Snapshot()),
            MeasuredBlobDelta: measuredBlobDiff.Total,
            PostRunRollAndDrain: postRoll);
    }

    private async Task<S4aJobsOpenFireResult> RunS4aJobsOpenFireAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var grains = Math.Max(1, req.Grains);
        var concurrency = Math.Max(1, req.Concurrency);
        var delayMs = req.DelayMs > 0 ? req.DelayMs : 1500;
        var waitTimeout = TimeSpan.FromSeconds(Math.Max(15, req.WaitTimeoutSeconds));

        var schedStats = await RunIndexedWorkAsync(
            grains,
            concurrency,
            async (idx, token) =>
            {
                var grain = grainFactory.GetGrain<IBenchJobGrain>($"{runTag}:s4a:{idx}");
                await grain.ScheduleWithStateAsync($"j-{idx}", runTag, delayMs).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        if (req.DeactivateAfterSchedule)
        {
            await RunIndexedWorkAsync(
                grains,
                concurrency,
                async (idx, token) =>
                {
                    var grain = grainFactory.GetGrain<IBenchJobGrain>($"{runTag}:s4a:{idx}");
                    await grain.DeactivateNowAsync().ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }

        var (finalSnap, waitElapsed) = await WaitForJobExecutionsAsync(runTag, schedStats.Ops, waitTimeout, ct).ConfigureAwait(false);

        return new S4aJobsOpenFireResult(
            RequestedJobs: grains,
            ScheduledJobs: schedStats.Ops,
            ScheduleErrors: schedStats.Errors,
            DeactivatedAfterSchedule: req.DeactivateAfterSchedule,
            DelayMs: delayMs,
            ExecutedJobs: finalSnap.RunJobExecuted,
            EphemeralReminderExecutions: finalSnap.RunEphemeralReminderExecutions,
            BackingShardExecutions: finalSnap.RunBackingShardExecutions,
            AllExecuted: finalSnap.RunJobExecuted >= schedStats.Ops,
            TotalWaitMs: Math.Round(waitElapsed.TotalMilliseconds, 2),
            ScheduleLatencyMs: LatencyStats.FromSamples(schedStats.Latencies),
            DeliveryLagMs: LatencyStats.FromSamples(finalSnap.RunLagSamplesMs));
    }

    private async Task<S4bJobsSealedDrainResult> RunS4bJobsSealedDrainAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var grains = Math.Max(1, req.Grains);
        var concurrency = Math.Max(1, req.Concurrency);
        var delayMs = req.DelayMs > 0 ? req.DelayMs : 10_000;
        var waitTimeout = TimeSpan.FromSeconds(Math.Max(30, req.WaitTimeoutSeconds));

        var schedStats = await RunIndexedWorkAsync(
            grains,
            concurrency,
            async (idx, token) =>
            {
                var grain = grainFactory.GetGrain<IBenchJobGrain>($"{runTag}:s4b:{idx}");
                await grain.ScheduleWithStateAsync($"j-{idx}", runTag, delayMs).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        if (req.DeactivateAfterSchedule)
        {
            await RunIndexedWorkAsync(
                grains,
                concurrency,
                async (idx, token) =>
                {
                    var grain = grainFactory.GetGrain<IBenchJobGrain>($"{runTag}:s4b:{idx}");
                    await grain.DeactivateNowAsync().ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }

        RollAndDrainResult? drainResult = null;
        if (runtimeInfo.Mode == BenchMode.Clog)
        {
            drainResult = await RollAndDrainAsync(waitTimeout, ct).ConfigureAwait(false);
        }

        var (finalSnap, waitElapsed) = await WaitForJobExecutionsAsync(runTag, schedStats.Ops, waitTimeout, ct).ConfigureAwait(false);
        var sealedPuts = drainResult?.BlobDelta.GetCount("jobs", "PUT") ?? 0;

        return new S4bJobsSealedDrainResult(
            RequestedJobs: grains,
            ScheduledJobs: schedStats.Ops,
            ScheduleErrors: schedStats.Errors,
            DeactivatedAfterSchedule: req.DeactivateAfterSchedule,
            DelayMs: delayMs,
            ExecutedJobs: finalSnap.RunJobExecuted,
            EphemeralReminderExecutions: finalSnap.RunEphemeralReminderExecutions,
            BackingShardExecutions: finalSnap.RunBackingShardExecutions,
            AllExecuted: finalSnap.RunJobExecuted >= schedStats.Ops,
            TotalWaitMs: Math.Round(waitElapsed.TotalMilliseconds, 2),
            ScheduleLatencyMs: LatencyStats.FromSamples(schedStats.Latencies),
            Drain: drainResult,
            SealedShardBlobPuts: sealedPuts,
            DeliveryLagMs: LatencyStats.FromSamples(finalSnap.RunLagSamplesMs));
    }

    private async Task<S6FailoverResult> RunS6FailoverAsync(BenchRunRequest req, string runTag, CancellationToken ct)
    {
        var grains = Math.Max(1, req.Grains);
        var concurrency = Math.Max(1, req.Concurrency);
        var durationSeconds = Math.Max(1, req.DurationSeconds);
        var payload = CreatePayload(Math.Max(1, req.PayloadBytes));
        var retryBackoff = TimeSpan.FromMilliseconds(Math.Max(10, req.RetryBackoffMs));
        var callTimeout = TimeSpan.FromMilliseconds(Math.Max(500, req.CallTimeoutMs));
        var verifyTimeout = TimeSpan.FromSeconds(Math.Max(15, req.VerifyTimeoutSeconds));

        var blobsBeforeJobs = await telemetry.SnapshotAsync(runTag: null, ct).ConfigureAwait(false);

        // Optional surviving-job batch scheduled right before the failover load window.
        var jobCount = Math.Max(0, req.JobCount);
        var jobDelayMs = req.DelayMs > 0 ? req.DelayMs : 10_000;
        var ackedJobs = 0;
        var jobScheduleErrors = 0;

        if (jobCount > 0)
        {
            var jobStats = await RunIndexedWorkAsync(
                jobCount,
                Math.Min(concurrency, jobCount),
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
                    Math.Min(concurrency, jobCount),
                    async (idx, token) =>
                    {
                        var grain = grainFactory.GetGrain<IBenchJobGrain>($"{runTag}:s6j:{idx}");
                        await grain.DeactivateNowAsync().WaitAsync(callTimeout, token).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);
            }
        }

        var loadWindow = await RunFailoverLoadWindowAsync(
            runTag,
            driverIndex: -1,
            grains,
            concurrency,
            durationSeconds,
            payload,
            retryBackoff,
            callTimeout,
            verifyTimeout,
            maxSamples: MaxLatencyReservoir,
            isolateWorkersByPrimary: req.IsolateWorkersByPrimary,
            ct).ConfigureAwait(false);

        // Verify surviving delayed jobs executed across live silos.
        FailoverJobVerification? jobVerification = null;
        if (jobCount > 0)
        {
            var remainingWait = loadWindow.VerifyDeadline - DateTimeOffset.UtcNow;
            if (remainingWait < TimeSpan.FromSeconds(5))
            {
                remainingWait = TimeSpan.FromSeconds(5);
            }

            var (jobSnap, _) = await WaitForJobExecutionsAsync(runTag, ackedJobs, remainingWait, ct).ConfigureAwait(false);
            var jobBlobDiff = ClusterTelemetryAggregator.DiffBlobs(blobsBeforeJobs, jobSnap);
            var sealedPuts = jobBlobDiff.Total.GetCount("jobs", "PUT");

            jobVerification = new FailoverJobVerification(
                RequestedJobs: jobCount,
                AckedJobs: ackedJobs,
                ScheduleErrors: jobScheduleErrors,
                ExecutedJobs: jobSnap.RunJobExecuted,
                EphemeralReminderExecutions: jobSnap.RunEphemeralReminderExecutions,
                BackingShardExecutions: jobSnap.RunBackingShardExecutions,
                LostJobCount: Math.Max(0, ackedJobs - jobSnap.RunJobExecuted),
                SealedShardBlobPuts: sealedPuts,
                DeliveryLagMs: LatencyStats.FromSamples(jobSnap.RunLagSamplesMs));
        }

        var totalAttempts = loadWindow.TotalOps + loadWindow.TotalErrors;

        return new S6FailoverResult(
            Grains: loadWindow.GrainCount,
            Concurrency: concurrency,
            DurationSeconds: durationSeconds,
            PayloadBytes: payload.Length,
            TotalOps: loadWindow.TotalOps,
            TotalErrors: loadWindow.TotalErrors,
            OverallErrorRatePct: totalAttempts > 0 ? Math.Round(100.0 * loadWindow.TotalErrors / totalAttempts, 3) : 0,
            ElapsedMs: Math.Round(loadWindow.Elapsed.TotalMilliseconds, 2),
            OverallOpsPerSec: loadWindow.Elapsed.TotalSeconds > 0 ? Math.Round(loadWindow.TotalOps / loadWindow.Elapsed.TotalSeconds, 2) : 0,
            OverallLatencyMs: LatencyStats.FromSamples(loadWindow.Samples),
            ErrorsByType: loadWindow.ErrorsByType,
            TimeSeries: loadWindow.TimeSeries,
            TouchedGrains: loadWindow.TouchedGrains,
            VerifiedGrains: loadWindow.VerifiedGrains,
            FailedVerifyGrains: loadWindow.FailedVerifyGrains,
            LostWriteCount: loadWindow.LostWriteDetails.Count,
            LostWriteDetails: loadWindow.LostWriteDetails,
            Jobs: jobVerification,
            IsolateWorkersByPrimary: loadWindow.UsedIsolatedPools,
            PerInitialPrimaryTimeSeries: loadWindow.PerInitialPrimaryTimeSeries);
    }

    private async Task<(ClusterTelemetrySnapshot Snapshot, TimeSpan Elapsed)> WaitForJobExecutionsAsync(
        string runTag,
        long targetCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        ClusterTelemetrySnapshot snap;
        while (true)
        {
            snap = await telemetry.SnapshotAsync(runTag, cancellationToken).ConfigureAwait(false);
            if (snap.RunJobExecuted >= targetCount || sw.Elapsed >= timeout)
            {
                sw.Stop();
                return (snap, sw.Elapsed);
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(long Ops, long Errors, TimeSpan Elapsed, List<double> Latencies)> RunWorkersForDurationAsync(
        int concurrency,
        TimeSpan duration,
        Func<int, long, CancellationToken, Task> action,
        bool recordStats,
        CancellationToken ct)
    {
        var reservoir = recordStats ? new LatencyReservoir(MaxLatencyReservoir) : null;
        long ops = 0, errors = 0;
        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, concurrency).Select(workerId => Task.Run(async () =>
        {
            long iter = 0;
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                iter++;
                var opSw = recordStats ? Stopwatch.StartNew() : null;
                try
                {
                    await action(workerId, iter, ct).ConfigureAwait(false);
                    if (opSw is not null)
                    {
                        opSw.Stop();
                        reservoir!.Add(opSw.Elapsed.TotalMilliseconds);
                    }

                    Interlocked.Increment(ref ops);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        return (ops, errors, sw.Elapsed, reservoir?.Snapshot() ?? []);
    }

    private static async Task<(long Ops, long Errors, TimeSpan Elapsed, List<double> Latencies)> RunIndexedWorkAsync(
        int count,
        int concurrency,
        Func<int, CancellationToken, Task> action,
        CancellationToken ct)
    {
        var reservoir = new LatencyReservoir(MaxLatencyReservoir);
        long ops = 0, errors = 0;
        var next = -1;
        var sw = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, Math.Min(concurrency, count)).Select(_ => Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var idx = Interlocked.Increment(ref next);
                if (idx >= count)
                {
                    break;
                }

                var opSw = Stopwatch.StartNew();
                try
                {
                    await action(idx, ct).ConfigureAwait(false);
                    opSw.Stop();
                    reservoir.Add(opSw.Elapsed.TotalMilliseconds);
                    Interlocked.Increment(ref ops);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        sw.Stop();
        return (ops, errors, sw.Elapsed, reservoir.Snapshot());
    }

    private static byte[] CreatePayload(int bytes)
    {
        var buffer = new byte[bytes];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)((i * 31 + 17) & 0xFF);
        }

        return buffer;
    }

    private static void AtomicMax(ref long location, long value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static string ClassifyException(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
        {
            ex = agg.InnerExceptions[0];
        }

        var typeName = ex.GetType().Name;
        var firstLine = (ex.Message ?? string.Empty).Split(['\r', '\n'], 2)[0].Trim();
        if (firstLine.Length > 64)
        {
            firstLine = firstLine[..64];
        }

        return string.IsNullOrEmpty(firstLine)
            ? typeName
            : string.Create(CultureInfo.InvariantCulture, $"{typeName}: {firstLine}");
    }

    private sealed class LatencyReservoir(int maxCapacity)
    {
        private readonly Lock _lock = new();
        private readonly List<double> _samples = new(Math.Min(maxCapacity, 4096));
        private long _seen;

        public void Add(double ms)
        {
            lock (_lock)
            {
                _seen++;
                if (_samples.Count < maxCapacity)
                {
                    _samples.Add(ms);
                }
                else
                {
                    var idx = Random.Shared.NextInt64(_seen);
                    if (idx < maxCapacity)
                    {
                        _samples[(int)idx] = ms;
                    }
                }
            }
        }

        public List<double> Snapshot()
        {
            lock (_lock)
            {
                return [.. _samples];
            }
        }
    }

    private sealed class SecondBucketAccumulator
    {
        private const int MaxSamplesPerSecond = 8192;
        private readonly Lock _lock = new();
        private readonly List<double> _latencies = new(128);
        private long _ops;
        private long _errors;

        public void RecordSuccess(double ms)
        {
            lock (_lock)
            {
                _ops++;
                if (_latencies.Count < MaxSamplesPerSecond)
                {
                    _latencies.Add(ms);
                }
                else
                {
                    var idx = Random.Shared.NextInt64(_ops);
                    if (idx < MaxSamplesPerSecond)
                    {
                        _latencies[(int)idx] = ms;
                    }
                }
            }
        }

        public void RecordError()
        {
            lock (_lock)
            {
                _errors++;
            }
        }

        public FailoverSecondBucket Build(int second, DateTimeOffset utcTimestamp)
        {
            long ops, errors;
            List<double> samples;
            lock (_lock)
            {
                ops = _ops;
                errors = _errors;
                samples = [.. _latencies];
            }

            var total = ops + errors;
            var errorRatePct = total > 0 ? Math.Round(100.0 * errors / total, 2) : 0;
            if (samples.Count == 0)
            {
                return new FailoverSecondBucket(second, utcTimestamp, ops, errors, errorRatePct, 0, 0, 0, 0);
            }

            samples.Sort();
            return new FailoverSecondBucket(
                Second: second,
                UtcTimestamp: utcTimestamp,
                Ops: ops,
                Errors: errors,
                ErrorRatePct: errorRatePct,
                P50Ms: Math.Round(Pick(samples, 0.50), 3),
                P95Ms: Math.Round(Pick(samples, 0.95), 3),
                P99Ms: Math.Round(Pick(samples, 0.99), 3),
                MaxMs: Math.Round(samples[^1], 3));
        }

        private static double Pick(List<double> sorted, double p)
        {
            var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
        }
    }

    private sealed class ZipfSampler
    {
        private readonly double[] _cdf;

        public ZipfSampler(int n, double exponent)
        {
            _cdf = new double[n];
            double sum = 0;
            for (var i = 1; i <= n; i++)
            {
                sum += 1.0 / Math.Pow(i, exponent);
                _cdf[i - 1] = sum;
            }

            for (var i = 0; i < n; i++)
            {
                _cdf[i] /= sum;
            }

            _cdf[n - 1] = 1.0;
        }

        public int Next()
        {
            var u = Random.Shared.NextDouble();
            var idx = Array.BinarySearch(_cdf, u);
            if (idx < 0)
            {
                idx = ~idx;
            }

            return Math.Clamp(idx, 0, _cdf.Length - 1);
        }
    }
}

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.FileSystem.Archive;
using Orleans.Durable.Storage.FileSystem.MetaStore;
using Orleans.Durable.Storage.Hosting;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Quiet-machine one-silo-per-process Server-GC Orleans benchmark runner (<c>--orleans-bench</c> coordinator
/// and <c>--orleans-silo</c> child silo process) over real loopback TCP sockets.
/// </summary>
internal static class OrleansMultiProcessBenchmarks
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] AllScenarios =
    [
        "replica-latency",
        "replica-fanout",
        "replica-burst",
        "host-cores",
        "probe-saturation",
        "forwarded-journals",
    ];

    internal static async Task<int> RunCoordinatorAsync(string[] args)
    {
        var scenarios = new List<string>();
        var runs = 3;
        var smoke = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--runs":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for option '--runs'.");
                    }

                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out runs) || runs <= 0)
                    {
                        throw new ArgumentException($"Option '--runs' requires a positive integer, got '{args[i]}'.");
                    }

                    break;
                case "--smoke":
                    smoke = true;
                    runs = 1;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown --orleans-bench option '{arg}'.");
                    }

                    if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        scenarios.AddRange(AllScenarios);
                    }
                    else
                    {
                        scenarios.AddRange(arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }

                    break;
            }
        }

        if (scenarios.Count == 0)
        {
            scenarios.AddRange(AllScenarios);
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"# Orleans Multi-Process Server-GC Benchmarks (runs={runs}, smoke={smoke}, hostCores={Environment.ProcessorCount})"));
        Console.WriteLine();

        foreach (var scenario in scenarios.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            switch (scenario.ToLowerInvariant())
            {
                case "replica-latency":
                    await RunReplicaLatencyAsync(runs, smoke).ConfigureAwait(false);
                    break;
                case "replica-fanout":
                    await RunReplicaFanoutAsync(runs, smoke).ConfigureAwait(false);
                    break;
                case "replica-burst":
                    await RunReplicaBurstAsync(runs, smoke).ConfigureAwait(false);
                    break;
                case "host-cores":
                    await RunHostCoresAsync(runs, smoke).ConfigureAwait(false);
                    break;
                case "probe-saturation":
                    await RunProbeSaturationAsync(runs, smoke).ConfigureAwait(false);
                    break;
                case "forwarded-journals":
                    await RunForwardedJournalsAsync(runs, smoke).ConfigureAwait(false);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown scenario '{scenario}'. Available: {string.Join(", ", AllScenarios)}");
                    return 1;
            }
        }

        return 0;
    }

    // -------------------------------------------------------------------------
    // Scenario 1: replica-latency
    // -------------------------------------------------------------------------
    private static async Task RunReplicaLatencyAsync(int runs, bool smoke)
    {
        Console.WriteLine("## Scenario 1: replica-latency (2 silos, Server GC, loopback TCP)");
        (int Size, int Count)[] cases = smoke
            ? [(1024, 50), (65536, 50), (1048576, 20)]
            : [(1024, 1000), (65536, 1000), (1048576, 200)];

        var results = new Dictionary<(string Path, int Size), List<(double WriteP50, double WriteP99, double ReadP50, double ReadP99)>>();

        for (var run = 0; run < runs; run++)
        {
            await using var cluster = await MultiProcessCluster.StartAsync(siloCount: 2, controlTransitions: 0).ConfigureAwait(false);
            var s0 = cluster.Silos[0];
            var s1 = cluster.Silos[1];
            AssertServerGc(cluster);

            foreach (var (size, count) in cases)
            {
                var localRes = await s0.CallAsync<LatencyReply>(new SiloCommand
                {
                    Command = "run-replica-latency",
                    TargetNodeId = s0.NodeId,
                    PayloadBytes = size,
                    Count = count,
                    LedgerStart = (ulong)(size * 10 + 1),
                }).ConfigureAwait(false);
                AddSample(results, ("Local", size), (localRes.WriteP50Us, localRes.WriteP99Us, localRes.ReadP50Us, localRes.ReadP99Us));

                var remoteRes = await s0.CallAsync<LatencyReply>(new SiloCommand
                {
                    Command = "run-replica-latency",
                    TargetNodeId = s1.NodeId,
                    PayloadBytes = size,
                    Count = count,
                    LedgerStart = (ulong)(size * 10 + 2),
                }).ConfigureAwait(false);
                AddSample(results, ("Remote", size), (remoteRes.WriteP50Us, remoteRes.WriteP99Us, remoteRes.ReadP50Us, remoteRes.ReadP99Us));
            }
        }

        Console.WriteLine("| Path | Payload | Calls | Write p50 (us) | Write p99 (us) | Read p50 (us) | Read p99 (us) |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var (size, count) in cases)
        {
            foreach (var path in new[] { "Local", "Remote" })
            {
                var samples = results[(path, size)];
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {path} | {BenchmarkHelpers.FormatSize(size)} | {count} | {BenchmarkHelpers.FormatMeanRange(samples.Select(s => s.WriteP50))} | {BenchmarkHelpers.FormatMeanRange(samples.Select(s => s.WriteP99))} | {BenchmarkHelpers.FormatMeanRange(samples.Select(s => s.ReadP50))} | {BenchmarkHelpers.FormatMeanRange(samples.Select(s => s.ReadP99))} |"));
            }
        }

        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Scenario 2: replica-fanout
    // -------------------------------------------------------------------------
    private static async Task RunReplicaFanoutAsync(int runs, bool smoke)
    {
        Console.WriteLine("## Scenario 2: replica-fanout (3 silos x 16 ledgers, N=3 Q=2 drained to all 3, Server GC, loopback TCP)");
        int[] sizes = smoke ? [65536, 1048576] : [1024, 4096, 65536, 614400, 1048576];
        var durationSec = smoke ? 2.0 : 6.0;

        Console.WriteLine("| Payload | Ingress MiB/s | Write p50 (ms) | Write p99 (ms) | Backpressure | Total B/B | SOH B/B | LOH B/B | POH B/B | Gen0/1/2 (/s) | Gen2 Causes | GC Pause % | CPU s/GiB | WS Peak (MiB) | Host Bytes Peak (MiB) |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|");

        foreach (var size in sizes)
        {
            var runMetrics = new List<(
                double IngressMiBps,
                double P50Ms,
                double P99Ms,
                long BackpressureCount,
                double TotalBB,
                double SohBB,
                double LohBB,
                double PohBB,
                double Gen0ps,
                double Gen1ps,
                double Gen2ps,
                string Gen2Causes,
                double GcPausePct,
                double CpuSecPerGiB,
                double WsPeakMiB,
                double HostBytesPeakMiB)>();

            for (var run = 0; run < runs; run++)
            {
                await using var cluster = await MultiProcessCluster.StartAsync(siloCount: 3, controlTransitions: 0).ConfigureAwait(false);
                AssertServerGc(cluster);
                var allNodeIds = cluster.Silos.Select(s => s.NodeId).ToArray();

                // Warmup 1s across all 3 silos
                var warmTasks = cluster.Silos.Select((s, idx) => s.CallAsync<FanoutReply>(new SiloCommand
                {
                    Command = "run-replica-fanout",
                    TargetNodeIds = allNodeIds,
                    PayloadBytes = size,
                    Ledgers = 16,
                    DurationSec = smoke ? 0.5 : 1.0,
                    LedgerStart = (ulong)(1000 + idx * 100),
                })).ToArray();
                _ = await Task.WhenAll(warmTasks).ConfigureAwait(false);

                _ = await Task.WhenAll(cluster.Silos.Select(s => s.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }))).ConfigureAwait(false);

                var fanoutTasks = cluster.Silos.Select((s, idx) => s.CallAsync<FanoutReply>(new SiloCommand
                {
                    Command = "run-replica-fanout",
                    TargetNodeIds = allNodeIds,
                    PayloadBytes = size,
                    Ledgers = 16,
                    DurationSec = durationSec,
                    LedgerStart = (ulong)(5000 + idx * 100),
                    IncludeLatencies = true,
                })).ToArray();
                var fanoutReplies = await Task.WhenAll(fanoutTasks).ConfigureAwait(false);

                var totalIngressBytes = fanoutReplies.Sum(r => r.TotalBytes);
                var backpressureCount = fanoutReplies.Sum(r => r.BackpressureCount);
                var probeReplies = await Task.WhenAll(cluster.Silos.Select((s, idx) => s.CallAsync<ProbeReply>(new SiloCommand
                {
                    Command = "stop-probe",
                    TotalBytes = fanoutReplies[idx].TotalBytes,
                }))).ConfigureAwait(false);

                var avgElapsed = probeReplies.Average(p => p.ElapsedSec);
                var ingressMiBps = totalIngressBytes / (1024.0 * 1024.0) / Math.Max(avgElapsed, 1e-9);
                var mergedLatencies = fanoutReplies.SelectMany(r => r.LatenciesUs ?? []).ToArray();
                Array.Sort(mergedLatencies);
                var p50Ms = BenchmarkHelpers.Percentile(mergedLatencies, 0.50) / 1000.0;
                var p99Ms = BenchmarkHelpers.Percentile(mergedLatencies, 0.99) / 1000.0;
                var sumAlloc = probeReplies.Sum(p => p.AllocatedBytes);
                var sumSoh = probeReplies.Sum(p => p.SohBytes);
                var sumLoh = probeReplies.Sum(p => p.LohBytes);
                var sumPoh = probeReplies.Sum(p => p.PohBytes);
                var denom = Math.Max(totalIngressBytes, 1L);
                var avgGen0 = probeReplies.Average(p => p.Gen0 / Math.Max(p.ElapsedSec, 1e-9));
                var avgGen1 = probeReplies.Average(p => p.Gen1 / Math.Max(p.ElapsedSec, 1e-9));
                var avgGen2 = probeReplies.Average(p => p.Gen2 / Math.Max(p.ElapsedSec, 1e-9));
                var causes = BenchmarkHelpers.MergeGen2Causes(probeReplies.Select(p => p.Gen2Causes));

                var maxGcPause = probeReplies.Max(p => p.GcPausePercent);
                var totalCpuSec = probeReplies.Sum(p => p.CpuMs) / 1000.0;
                var totalGiB = totalIngressBytes / (1024.0 * 1024.0 * 1024.0);
                var cpuPerGiB = totalGiB > 0 ? totalCpuSec / totalGiB : 0;
                var maxWs = probeReplies.Max(p => p.PeakWorkingSetMiB);
                var maxHostBytes = probeReplies.Max(p => p.PeakReplicaHostBytes) / (1024.0 * 1024.0);

                runMetrics.Add((
                    ingressMiBps,
                    p50Ms,
                    p99Ms,
                    backpressureCount,
                    (double)sumAlloc / denom,
                    (double)sumSoh / denom,
                    (double)sumLoh / denom,
                    (double)sumPoh / denom,
                    avgGen0,
                    avgGen1,
                    avgGen2,
                    causes,
                    maxGcPause,
                    cpuPerGiB,
                    maxWs,
                    maxHostBytes));
            }

            var allCauses = BenchmarkHelpers.MergeGen2Causes(runMetrics.Select(m => m.Gen2Causes));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {BenchmarkHelpers.FormatSize(size)} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.IngressMiBps))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.P50Ms))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.P99Ms))} | {runMetrics.Sum(m => m.BackpressureCount)} | {runMetrics.Average(m => m.TotalBB):F2} | {runMetrics.Average(m => m.SohBB):F2} | {runMetrics.Average(m => m.LohBB):F2} | {runMetrics.Average(m => m.PohBB):F2} | {runMetrics.Average(m => m.Gen0ps):F1} / {runMetrics.Average(m => m.Gen1ps):F1} / {runMetrics.Average(m => m.Gen2ps):F2} | {allCauses} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.GcPausePct))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.CpuSecPerGiB))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.WsPeakMiB))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.HostBytesPeakMiB))} |"));
        }

        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Scenario 3: replica-burst
    // -------------------------------------------------------------------------
    private static async Task RunReplicaBurstAsync(int runs, bool smoke)
    {
        Console.WriteLine("## Scenario 3: replica-burst (3 caller silos x 32 writers x 1 MiB saturating 1 callee silo, 10s, Server GC)");
        var warmupSec = smoke ? 0.5 : 1.0;
        var durationSec = smoke ? 3.0 : 10.0;
        var runMetrics = new List<(double MiBps, long BackpressureCount, double WsPeakMiB, double HostBytesPeakMiB, double AllocBB, double LohBB, double Gen0ps, double Gen1ps, double Gen2ps, string Gen2Causes, double GcPausePct, double CpuSecPerGiB)>();

        for (var run = 0; run < runs; run++)
        {
            await using var cluster = await MultiProcessCluster.StartAsync(siloCount: 4, controlTransitions: 0).ConfigureAwait(false);
            AssertServerGc(cluster);
            var callee = cluster.Silos[0];
            var callers = cluster.Silos.Skip(1).ToArray();

            var warmTasks = callers.Select((c, idx) => c.CallAsync<BurstReply>(new SiloCommand
            {
                Command = "run-replica-burst",
                TargetNodeId = callee.NodeId,
                PayloadBytes = 1048576,
                Writers = 32,
                DurationSec = warmupSec,
                LedgerStart = (ulong)(15000 + idx * 100),
            })).ToArray();
            _ = await Task.WhenAll(warmTasks).ConfigureAwait(false);

            _ = await callee.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }).ConfigureAwait(false);
            var burstTasks = callers.Select((c, idx) => c.CallAsync<BurstReply>(new SiloCommand
            {
                Command = "run-replica-burst",
                TargetNodeId = callee.NodeId,
                PayloadBytes = 1048576,
                Writers = 32,
                DurationSec = durationSec,
                LedgerStart = (ulong)(20000 + idx * 100),
            })).ToArray();
            var burstReplies = await Task.WhenAll(burstTasks).ConfigureAwait(false);
            var totalBytes = burstReplies.Sum(r => r.TotalBytes);
            var backpressureCount = burstReplies.Sum(r => r.BackpressureCount);

            var calleeProbe = await callee.CallAsync<ProbeReply>(new SiloCommand
            {
                Command = "stop-probe",
                TotalBytes = totalBytes,
            }).ConfigureAwait(false);

            var mibps = totalBytes / (1024.0 * 1024.0) / Math.Max(calleeProbe.ElapsedSec, 1e-9);
            var denom = Math.Max(totalBytes, 1L);
            var totalGiB = totalBytes / (1024.0 * 1024.0 * 1024.0);
            var cpuPerGiB = totalGiB > 0 ? calleeProbe.CpuMs / 1000.0 / totalGiB : 0;
            runMetrics.Add((
                mibps,
                backpressureCount,
                calleeProbe.PeakWorkingSetMiB,
                calleeProbe.PeakReplicaHostBytes / (1024.0 * 1024.0),
                (double)calleeProbe.AllocatedBytes / denom,
                (double)calleeProbe.LohBytes / denom,
                calleeProbe.Gen0 / Math.Max(calleeProbe.ElapsedSec, 1e-9),
                calleeProbe.Gen1 / Math.Max(calleeProbe.ElapsedSec, 1e-9),
                calleeProbe.Gen2 / Math.Max(calleeProbe.ElapsedSec, 1e-9),
                string.IsNullOrWhiteSpace(calleeProbe.Gen2Causes) ? "none" : calleeProbe.Gen2Causes,
                calleeProbe.GcPausePercent,
                cpuPerGiB));
        }

        var allBurstCauses = BenchmarkHelpers.MergeGen2Causes(runMetrics.Select(m => m.Gen2Causes));
        Console.WriteLine("| Callee MiB/s | Backpressure | WS Peak (MiB) | Host Peak (MiB / 128) | Alloc B/B | LOH B/B | Gen0/1/2 (/s) | Gen2 Causes | GC Pause % | CPU s/GiB |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---|---|---:|---:|");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.MiBps))} | {runMetrics.Sum(m => m.BackpressureCount)} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.WsPeakMiB))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.HostBytesPeakMiB))} | {runMetrics.Average(m => m.AllocBB):F2} | {runMetrics.Average(m => m.LohBB):F2} | {runMetrics.Average(m => m.Gen0ps):F1} / {runMetrics.Average(m => m.Gen1ps):F1} / {runMetrics.Average(m => m.Gen2ps):F2} | {allBurstCauses} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.GcPausePct))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.CpuSecPerGiB))} |"));
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Scenario 4: host-cores
    // -------------------------------------------------------------------------
    private static async Task RunHostCoresAsync(int runs, bool smoke)
    {
        Console.WriteLine("## Scenario 4: host-cores (3 unconstrained caller silos -> 1 callee pinned via DOTNET_PROCESSOR_COUNT)");
        int[] coreCounts = smoke ? [2, 8] : [2, 4, 8, 16];
        var warmupSec = smoke ? 0.5 : 1.0;
        var durationSec = smoke ? 2.5 : 8.0;

        Console.WriteLine("| Callee Cores (verified) | Callee MiB/s | Backpressure | Host Peak (MiB) | WS Peak (MiB) | GC Pause % | CPU s/GiB |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var cores in coreCounts)
        {
            var runMetrics = new List<(int VerifiedCores, double MiBps, long BackpressureCount, double HostPeakMiB, double WsPeakMiB, double GcPausePct, double CpuSecPerGiB)>();
            for (var run = 0; run < runs; run++)
            {
                await using var cluster = await MultiProcessCluster.StartAsync(
                    siloCount: 4,
                    controlTransitions: 0,
                    calleeProcessorCount: cores).ConfigureAwait(false);
                AssertServerGc(cluster);
                var callee = cluster.Silos[0];
                if (callee.ProcessorCount != cores)
                {
                    throw new InvalidOperationException($"Expected callee ProcessorCount={cores}, got {callee.ProcessorCount}.");
                }

                var callers = cluster.Silos.Skip(1).ToArray();
                var warmTasks = callers.Select((c, idx) => c.CallAsync<BurstReply>(new SiloCommand
                {
                    Command = "run-replica-burst",
                    TargetNodeId = callee.NodeId,
                    PayloadBytes = 1048576,
                    Writers = 32,
                    DurationSec = warmupSec,
                    LedgerStart = (ulong)(25000 + cores * 1000 + idx * 100),
                })).ToArray();
                _ = await Task.WhenAll(warmTasks).ConfigureAwait(false);

                _ = await callee.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }).ConfigureAwait(false);
                var burstTasks = callers.Select((c, idx) => c.CallAsync<BurstReply>(new SiloCommand
                {
                    Command = "run-replica-burst",
                    TargetNodeId = callee.NodeId,
                    PayloadBytes = 1048576,
                    Writers = 32,
                    DurationSec = durationSec,
                    LedgerStart = (ulong)(30000 + cores * 1000 + idx * 100),
                })).ToArray();
                var burstReplies = await Task.WhenAll(burstTasks).ConfigureAwait(false);
                var totalBytes = burstReplies.Sum(r => r.TotalBytes);
                var backpressureCount = burstReplies.Sum(r => r.BackpressureCount);

                var calleeProbe = await callee.CallAsync<ProbeReply>(new SiloCommand
                {
                    Command = "stop-probe",
                    TotalBytes = totalBytes,
                }).ConfigureAwait(false);

                var mibps = totalBytes / (1024.0 * 1024.0) / Math.Max(calleeProbe.ElapsedSec, 1e-9);
                var totalGiB = totalBytes / (1024.0 * 1024.0 * 1024.0);
                var cpuPerGiB = totalGiB > 0 ? calleeProbe.CpuMs / 1000.0 / totalGiB : 0;
                runMetrics.Add((
                    callee.ProcessorCount,
                    mibps,
                    backpressureCount,
                    calleeProbe.PeakReplicaHostBytes / (1024.0 * 1024.0),
                    calleeProbe.PeakWorkingSetMiB,
                    calleeProbe.GcPausePercent,
                    cpuPerGiB));
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {runMetrics[0].VerifiedCores} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.MiBps))} | {runMetrics.Sum(m => m.BackpressureCount)} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.HostPeakMiB))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.WsPeakMiB))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.GcPausePct))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.CpuSecPerGiB))} |"));
        }

        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Scenario 5: probe-saturation
    // -------------------------------------------------------------------------
    private static async Task RunProbeSaturationAsync(int runs, bool smoke)
    {
        Console.WriteLine("## Scenario 5: probe-saturation (real SiloHealthMonitor ping RTT + false suspicions under 20s 1 MiB saturation; ProbeTimeout=1000ms, NumProbedSilos=3, IAmAlive=5s)");
        var warmupSec = smoke ? 0.5 : 1.0;
        var durationSec = smoke ? 5.0 : 20.0;
        var runMetrics = new List<(
            double CalleeMiBps,
            int CalleePingSamples,
            double CalleePingP50Ms,
            double CalleePingP99Ms,
            double CalleePingMaxMs,
            int AllPingSamples,
            double AllPingP50Ms,
            double AllPingP99Ms,
            double AllPingMaxMs,
            long PingsSent,
            long PingsReceived,
            long MissedPings,
            int SuspectCount,
            int NonActiveTransitions,
            double CalleeGcPausePct)>();

        for (var run = 0; run < runs; run++)
        {
            await using var cluster = await MultiProcessCluster.StartAsync(
                siloCount: 4,
                controlTransitions: 0,
                probeTimeoutMs: 1000).ConfigureAwait(false);
            AssertServerGc(cluster);
            var callee = cluster.Silos[0];
            var callers = cluster.Silos.Skip(1).ToArray();

            var warmTasks = callers.Select((c, idx) => c.CallAsync<BurstReply>(new SiloCommand
            {
                Command = "run-replica-burst",
                TargetNodeId = callee.NodeId,
                PayloadBytes = 1048576,
                Writers = 32,
                DurationSec = warmupSec,
                LedgerStart = (ulong)(35000 + idx * 100),
            })).ToArray();
            _ = await Task.WhenAll(warmTasks).ConfigureAwait(false);

            _ = await Task.WhenAll(cluster.Silos.Select(s => s.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }))).ConfigureAwait(false);

            var burstTasks = callers.Select((c, idx) => c.CallAsync<BurstReply>(new SiloCommand
            {
                Command = "run-replica-burst",
                TargetNodeId = callee.NodeId,
                PayloadBytes = 1048576,
                Writers = 32,
                DurationSec = durationSec,
                LedgerStart = (ulong)(40000 + idx * 100),
            })).ToArray();
            var burstReplies = await Task.WhenAll(burstTasks).ConfigureAwait(false);
            var totalBytes = burstReplies.Sum(r => r.TotalBytes);

            var probes = await Task.WhenAll(cluster.Silos.Select((s, idx) => s.CallAsync<ProbeReply>(new SiloCommand
            {
                Command = "stop-probe",
                TotalBytes = idx == 0 ? totalBytes : burstReplies[idx - 1].TotalBytes,
            }))).ConfigureAwait(false);

            var calleeProbe = probes[0];
            var allSamples = probes.SelectMany(p => p.PingSamples ?? []).ToArray();
            var allRtts = allSamples.Select(s => s.RttMs).OrderBy(x => x).ToArray();
            var calleeRtts = allSamples.Where(s => s.TargetPort == callee.SiloPort).Select(s => s.RttMs).OrderBy(x => x).ToArray();
            var pingsSent = probes.Sum(p => p.PingsSentCount);
            var pingsReceived = probes.Sum(p => p.PingsReplyReceivedCount);
            var missed = probes.Sum(p => p.PingMissedCount);
            var suspects = probes.Max(p => p.SuspectCount);
            var nonActive = probes.Max(p => p.NonActiveTransitions);

            if (pingsSent <= 0 || pingsReceived <= 0)
            {
                throw new InvalidOperationException($"Probe meter listener captured zero pings (sent={pingsSent}, received={pingsReceived}).");
            }

            if (calleeRtts.Length == 0)
            {
                throw new InvalidOperationException($"No callee-targeted membership pings captured for port {callee.SiloPort} (allSamples={allRtts.Length}).");
            }

            if (missed != 0 || suspects != 0 || nonActive != 0)
            {
                throw new InvalidOperationException($"Probe saturation failed liveness check: missed={missed}, suspects={suspects}, nonActive={nonActive}.");
            }

            var mibps = totalBytes / (1024.0 * 1024.0) / Math.Max(calleeProbe.ElapsedSec, 1e-9);
            runMetrics.Add((
                mibps,
                calleeRtts.Length,
                BenchmarkHelpers.Percentile(calleeRtts, 0.50),
                BenchmarkHelpers.Percentile(calleeRtts, 0.99),
                calleeRtts[^1],
                allRtts.Length,
                BenchmarkHelpers.Percentile(allRtts, 0.50),
                BenchmarkHelpers.Percentile(allRtts, 0.99),
                allRtts.Length > 0 ? allRtts[^1] : 0,
                pingsSent,
                pingsReceived,
                missed,
                suspects,
                nonActive,
                calleeProbe.GcPausePercent));
        }

        Console.WriteLine("| Callee MiB/s | Callee Pings | Callee RTT p50 (ms) | Callee RTT p99 (ms) | Callee RTT max (ms) | Cluster Pings | Cluster RTT p50 (ms) | Cluster RTT p99 (ms) | Cluster RTT max (ms) | Pings Sent / Recv | Missed Pings | False Suspicions | Non-Active Transitions | Callee GC Pause % |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.CalleeMiBps))} | {runMetrics.Sum(m => m.CalleePingSamples)} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.CalleePingP50Ms))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.CalleePingP99Ms))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.CalleePingMaxMs))} | {runMetrics.Sum(m => m.AllPingSamples)} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.AllPingP50Ms))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.AllPingP99Ms))} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.AllPingMaxMs))} | {runMetrics.Sum(m => m.PingsSent)} / {runMetrics.Sum(m => m.PingsReceived)} | {runMetrics.Sum(m => m.MissedPings)} | {runMetrics.Sum(m => m.SuspectCount)} | {runMetrics.Sum(m => m.NonActiveTransitions)} | {BenchmarkHelpers.FormatMeanRange(runMetrics.Select(m => m.CalleeGcPausePct))} |"));
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Scenario 6: forwarded-journals
    // -------------------------------------------------------------------------
    private static async Task RunForwardedJournalsAsync(int runs, bool smoke)
    {
        Console.WriteLine("## Scenario 6: forwarded-journals (3 silos, Server GC, loopback TCP via JournalRouter)");
        var write1kCount = smoke ? 30 : 300;
        var write1mCount = smoke ? 10 : 60;
        var readDurationSec = smoke ? 2.0 : 8.0;
        var seedMiB = smoke ? 16 : 64;
        var expectedSeedBytes = (long)seedMiB * 1024 * 1024;

        var w1kRuns = new List<(double OpsPerSec, double MiBps, double P50Ms, double P99Ms, double PrimaryGcPause, double PrimaryLohBB)>();
        var w1mRuns = new List<(double OpsPerSec, double MiBps, double P50Ms, double P99Ms, double PrimaryGcPause, double PrimaryLohBB)>();
        var r1Runs = new List<(double MiBps, int ReadCount, double P50Ms, double P99Ms, double PeakFwdMiB, double PrimaryGcPause, double PrimaryLohBB)>();
        var r4Runs = new List<(double MiBps, int ReadCount, double P50Ms, double P99Ms, double PeakFwdMiB, double PrimaryGcPause, double PrimaryLohBB)>();
        var conc1kRuns = new List<(double WriteP50Ms, double WriteP99Ms, double ReadP50Ms, double ReadP99Ms, int ConcurrentReads, double PeakFwdMiB)>();
        var conc1mRuns = new List<(double WriteP50Ms, double WriteP99Ms, double ReadP50Ms, double ReadP99Ms, int ConcurrentReads, double PeakFwdMiB)>();

        for (var run = 0; run < runs; run++)
        {
            await using var cluster = await MultiProcessCluster.StartAsync(siloCount: 3, controlTransitions: 1).ConfigureAwait(false);
            AssertServerGc(cluster);

            // Wait until the cluster controller bootstraps and balances open ledgers across all 3 silos.
            var largeKey = "fwd-read-large";
            var ownerInfo = await cluster.Silos[0].CallAsync<OwnerInfoReply>(new SiloCommand
            {
                Command = "wait-ledger-ready",
                JournalKey = largeKey,
                Count = 3,
            }).ConfigureAwait(false);

            var primary = cluster.Silos.First(s => string.Equals(s.NodeId, ownerInfo.PrimaryNodeId, StringComparison.Ordinal));
            var forwarder = cluster.Silos.First(s => !string.Equals(s.NodeId, ownerInfo.PrimaryNodeId, StringComparison.Ordinal));

            // Ensure the forwarder's local routing view has also caught up to the settled balanced map.
            var forwarderView = await forwarder.CallAsync<OwnerInfoReply>(new SiloCommand
            {
                Command = "wait-ledger-ready",
                JournalKey = largeKey,
                Count = 3,
            }).ConfigureAwait(false);
            if (!string.Equals(forwarderView.PrimaryNodeId, primary.NodeId, StringComparison.Ordinal))
            {
                primary = cluster.Silos.First(s => string.Equals(s.NodeId, forwarderView.PrimaryNodeId, StringComparison.Ordinal));
                forwarder = cluster.Silos.First(s => !string.Equals(s.NodeId, forwarderView.PrimaryNodeId, StringComparison.Ordinal));
            }

            // 6a. Forwarded 1 KiB writes (warmup strictly before start-probe; all keys routed to remote primary)
            _ = await forwarder.CallAsync<AckReply>(new SiloCommand
            {
                Command = "warmup-forwarded-writes",
                JournalKey = "fwd-w1k",
                TargetNodeId = primary.NodeId,
                PayloadBytes = 1024,
                Count = 5,
            }).ConfigureAwait(false);
            _ = await primary.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }).ConfigureAwait(false);
            var w1k = await forwarder.CallAsync<ForwardedWriteReply>(new SiloCommand
            {
                Command = "run-forwarded-writes",
                JournalKey = "fwd-w1k",
                TargetNodeId = primary.NodeId,
                PayloadBytes = 1024,
                Count = write1kCount,
            }).ConfigureAwait(false);
            var p1k = await primary.CallAsync<ProbeReply>(new SiloCommand { Command = "stop-probe", TotalBytes = w1k.TotalBytes }).ConfigureAwait(false);
            w1kRuns.Add((w1k.OpsPerSecond, w1k.MiBPerSecond, w1k.P50Us / 1000.0, w1k.P99Us / 1000.0, p1k.GcPausePercent, (double)p1k.LohBytes / Math.Max(w1k.TotalBytes, 1)));

            // 6b. Forwarded 1 MiB writes (warmup strictly before start-probe; all keys routed to remote primary)
            _ = await forwarder.CallAsync<AckReply>(new SiloCommand
            {
                Command = "warmup-forwarded-writes",
                JournalKey = "fwd-w1m",
                TargetNodeId = primary.NodeId,
                PayloadBytes = 1048576,
                Count = 5,
            }).ConfigureAwait(false);
            _ = await primary.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }).ConfigureAwait(false);
            var w1m = await forwarder.CallAsync<ForwardedWriteReply>(new SiloCommand
            {
                Command = "run-forwarded-writes",
                JournalKey = "fwd-w1m",
                TargetNodeId = primary.NodeId,
                PayloadBytes = 1048576,
                Count = write1mCount,
            }).ConfigureAwait(false);
            var p1m = await primary.CallAsync<ProbeReply>(new SiloCommand { Command = "stop-probe", TotalBytes = w1m.TotalBytes }).ConfigureAwait(false);
            w1mRuns.Add((w1m.OpsPerSecond, w1m.MiBPerSecond, w1m.P50Us / 1000.0, w1m.P99Us / 1000.0, p1m.GcPausePercent, (double)p1m.LohBytes / Math.Max(w1m.TotalBytes, 1)));

            // Seed large journal (64 MiB = 64 x 1 MiB entries; 16 MiB in smoke) and re-verify its exact primary & non-primary forwarder.
            _ = await primary.CallAsync<AckReply>(new SiloCommand
            {
                Command = "seed-forwarded-journal",
                JournalKey = largeKey,
                Count = seedMiB,
                PayloadBytes = 1048576,
            }).ConfigureAwait(false);

            var largeOwner = await cluster.Silos[0].CallAsync<OwnerInfoReply>(new SiloCommand
            {
                Command = "wait-ledger-ready",
                JournalKey = largeKey,
                Count = 3,
            }).ConfigureAwait(false);
            primary = cluster.Silos.First(s => string.Equals(s.NodeId, largeOwner.PrimaryNodeId, StringComparison.Ordinal));
            forwarder = cluster.Silos.First(s => !string.Equals(s.NodeId, largeOwner.PrimaryNodeId, StringComparison.Ordinal));

            // Warmup forwarded read path before probing.
            _ = await forwarder.CallAsync<AckReply>(new SiloCommand
            {
                Command = "warmup-forwarded-reads",
                JournalKey = largeKey,
                TotalBytes = expectedSeedBytes,
            }).ConfigureAwait(false);

            // 6c. Forwarded 64 MiB reads: 1 reader (duration-based)
            _ = await primary.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }).ConfigureAwait(false);
            var r1 = await forwarder.CallAsync<ForwardedReadReply>(new SiloCommand
            {
                Command = "run-forwarded-reads",
                JournalKey = largeKey,
                DurationSec = readDurationSec,
                Writers = 1,
                TotalBytes = expectedSeedBytes,
            }).ConfigureAwait(false);
            var pr1 = await primary.CallAsync<ProbeReply>(new SiloCommand { Command = "stop-probe", TotalBytes = r1.TotalBytes }).ConfigureAwait(false);
            r1Runs.Add((r1.MiBPerSecond, r1.ReadCount, r1.P50Us / 1000.0, r1.P99Us / 1000.0, pr1.PeakForwardedReadBytes / (1024.0 * 1024.0), pr1.GcPausePercent, (double)pr1.LohBytes / Math.Max(r1.TotalBytes, 1)));

            // 6d. Forwarded 64 MiB reads: 4 concurrent readers (duration-based)
            _ = await primary.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }).ConfigureAwait(false);
            var r4 = await forwarder.CallAsync<ForwardedReadReply>(new SiloCommand
            {
                Command = "run-forwarded-reads",
                JournalKey = largeKey,
                DurationSec = readDurationSec,
                Writers = 4,
                TotalBytes = expectedSeedBytes,
            }).ConfigureAwait(false);
            var pr4 = await primary.CallAsync<ProbeReply>(new SiloCommand { Command = "stop-probe", TotalBytes = r4.TotalBytes }).ConfigureAwait(false);
            r4Runs.Add((r4.MiBPerSecond, r4.ReadCount, r4.P50Us / 1000.0, r4.P99Us / 1000.0, pr4.PeakForwardedReadBytes / (1024.0 * 1024.0), pr4.GcPausePercent, (double)pr4.LohBytes / Math.Max(r4.TotalBytes, 1)));

            // 6e. Concurrent forwarded 1 KiB writes strictly overlapping 4 active 64 MiB forwarded reads
            var concDurationSec = smoke ? 1.5 : 4.0;
            _ = await primary.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }).ConfigureAwait(false);
            var conc1k = await forwarder.CallAsync<ForwardedConcurrentReply>(new SiloCommand
            {
                Command = "run-forwarded-concurrent",
                JournalKey = largeKey,
                WriteKeyPrefix = "fwd-conc-1k",
                TargetNodeId = primary.NodeId,
                PayloadBytes = 1024,
                DurationSec = concDurationSec,
                Writers = 4,
                TotalBytes = expectedSeedBytes,
            }).ConfigureAwait(false);
            var prC1k = await primary.CallAsync<ProbeReply>(new SiloCommand { Command = "stop-probe", TotalBytes = conc1k.ReadTotalBytes + conc1k.WriteTotalBytes }).ConfigureAwait(false);
            if (prC1k.PeakForwardedReadBytes <= 0 || conc1k.ConcurrentReadCount <= 0)
            {
                throw new InvalidOperationException($"Forwarded 1 KiB concurrent run lacked verified overlap: peakFwdBytes={prC1k.PeakForwardedReadBytes}, concurrentReads={conc1k.ConcurrentReadCount}.");
            }

            conc1kRuns.Add((conc1k.WriteP50Us / 1000.0, conc1k.WriteP99Us / 1000.0, conc1k.ReadP50Us / 1000.0, conc1k.ReadP99Us / 1000.0, conc1k.ConcurrentReadCount, prC1k.PeakForwardedReadBytes / (1024.0 * 1024.0)));

            // 6f. Concurrent forwarded 1 MiB writes strictly overlapping 4 active 64 MiB forwarded reads
            _ = await primary.CallAsync<AckReply>(new SiloCommand { Command = "start-probe" }).ConfigureAwait(false);
            var conc1m = await forwarder.CallAsync<ForwardedConcurrentReply>(new SiloCommand
            {
                Command = "run-forwarded-concurrent",
                JournalKey = largeKey,
                WriteKeyPrefix = "fwd-conc-1m",
                TargetNodeId = primary.NodeId,
                PayloadBytes = 1048576,
                DurationSec = concDurationSec,
                Writers = 4,
                TotalBytes = expectedSeedBytes,
            }).ConfigureAwait(false);
            var prC1m = await primary.CallAsync<ProbeReply>(new SiloCommand { Command = "stop-probe", TotalBytes = conc1m.ReadTotalBytes + conc1m.WriteTotalBytes }).ConfigureAwait(false);
            if (prC1m.PeakForwardedReadBytes <= 0 || conc1m.ConcurrentReadCount <= 0)
            {
                throw new InvalidOperationException($"Forwarded 1 MiB concurrent run lacked verified overlap: peakFwdBytes={prC1m.PeakForwardedReadBytes}, concurrentReads={conc1m.ConcurrentReadCount}.");
            }

            conc1mRuns.Add((conc1m.WriteP50Us / 1000.0, conc1m.WriteP99Us / 1000.0, conc1m.ReadP50Us / 1000.0, conc1m.ReadP99Us / 1000.0, conc1m.ConcurrentReadCount, prC1m.PeakForwardedReadBytes / (1024.0 * 1024.0)));
        }

        Console.WriteLine("### Forwarded Writes");
        Console.WriteLine("| Workload | Ops/s | MiB/s | p50 (ms) | p99 (ms) | Primary LOH B/B | Primary GC Pause % |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| Forwarded write 1 KiB | {BenchmarkHelpers.FormatMeanRange(w1kRuns.Select(r => r.OpsPerSec))} | {BenchmarkHelpers.FormatMeanRange(w1kRuns.Select(r => r.MiBps))} | {BenchmarkHelpers.FormatMeanRange(w1kRuns.Select(r => r.P50Ms))} | {BenchmarkHelpers.FormatMeanRange(w1kRuns.Select(r => r.P99Ms))} | {w1kRuns.Average(r => r.PrimaryLohBB):F2} | {BenchmarkHelpers.FormatMeanRange(w1kRuns.Select(r => r.PrimaryGcPause))} |"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| Forwarded write 1 MiB | {BenchmarkHelpers.FormatMeanRange(w1mRuns.Select(r => r.OpsPerSec))} | {BenchmarkHelpers.FormatMeanRange(w1mRuns.Select(r => r.MiBps))} | {BenchmarkHelpers.FormatMeanRange(w1mRuns.Select(r => r.P50Ms))} | {BenchmarkHelpers.FormatMeanRange(w1mRuns.Select(r => r.P99Ms))} | {w1mRuns.Average(r => r.PrimaryLohBB):F2} | {BenchmarkHelpers.FormatMeanRange(w1mRuns.Select(r => r.PrimaryGcPause))} |"));
        Console.WriteLine();

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"### Forwarded Reads ({seedMiB} MiB Journal, {readDurationSec:F0}s window)"));
        Console.WriteLine("| Workload | Reads | MiB/s | p50 (ms) | p99 (ms) | Primary FwdRead Peak (MiB / 256) | Primary LOH B/B | Primary GC Pause % |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| 1 reader ({seedMiB} MiB) | {r1Runs.Sum(r => r.ReadCount)} | {BenchmarkHelpers.FormatMeanRange(r1Runs.Select(r => r.MiBps))} | {BenchmarkHelpers.FormatMeanRange(r1Runs.Select(r => r.P50Ms))} | {BenchmarkHelpers.FormatMeanRange(r1Runs.Select(r => r.P99Ms))} | {BenchmarkHelpers.FormatMeanRange(r1Runs.Select(r => r.PeakFwdMiB))} | {r1Runs.Average(r => r.PrimaryLohBB):F2} | {BenchmarkHelpers.FormatMeanRange(r1Runs.Select(r => r.PrimaryGcPause))} |"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| 4 concurrent readers ({seedMiB} MiB) | {r4Runs.Sum(r => r.ReadCount)} | {BenchmarkHelpers.FormatMeanRange(r4Runs.Select(r => r.MiBps))} | {BenchmarkHelpers.FormatMeanRange(r4Runs.Select(r => r.P50Ms))} | {BenchmarkHelpers.FormatMeanRange(r4Runs.Select(r => r.P99Ms))} | {BenchmarkHelpers.FormatMeanRange(r4Runs.Select(r => r.PeakFwdMiB))} | {r4Runs.Average(r => r.PrimaryLohBB):F2} | {BenchmarkHelpers.FormatMeanRange(r4Runs.Select(r => r.PrimaryGcPause))} |"));
        Console.WriteLine();

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"### Concurrent Forwarded Writes Beside Active 4x {seedMiB} MiB Forwarded Reads"));
        Console.WriteLine("| Workload | Write p50 (ms) | Write p99 (ms) | Concurrent Read p50 (ms) | Concurrent Read p99 (ms) | Overlapping Reads | Primary FwdRead Peak (MiB) |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| 1 KiB writes + 4x {seedMiB} MiB reads | {BenchmarkHelpers.FormatMeanRange(conc1kRuns.Select(r => r.WriteP50Ms))} | {BenchmarkHelpers.FormatMeanRange(conc1kRuns.Select(r => r.WriteP99Ms))} | {BenchmarkHelpers.FormatMeanRange(conc1kRuns.Select(r => r.ReadP50Ms))} | {BenchmarkHelpers.FormatMeanRange(conc1kRuns.Select(r => r.ReadP99Ms))} | {conc1kRuns.Sum(r => r.ConcurrentReads)} | {BenchmarkHelpers.FormatMeanRange(conc1kRuns.Select(r => r.PeakFwdMiB))} |"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| 1 MiB writes + 4x {seedMiB} MiB reads | {BenchmarkHelpers.FormatMeanRange(conc1mRuns.Select(r => r.WriteP50Ms))} | {BenchmarkHelpers.FormatMeanRange(conc1mRuns.Select(r => r.WriteP99Ms))} | {BenchmarkHelpers.FormatMeanRange(conc1mRuns.Select(r => r.ReadP50Ms))} | {BenchmarkHelpers.FormatMeanRange(conc1mRuns.Select(r => r.ReadP99Ms))} | {conc1mRuns.Sum(r => r.ConcurrentReads)} | {BenchmarkHelpers.FormatMeanRange(conc1mRuns.Select(r => r.PeakFwdMiB))} |"));
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Child Silo Process Entrypoint (--orleans-silo)
    // -------------------------------------------------------------------------
    internal static async Task<int> RunSiloProcessAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Missing --orleans-silo config path.");
            return 1;
        }

        var configJson = await File.ReadAllTextAsync(args[0]).ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<SiloLaunchConfig>(configJson, JsonOptions)
            ?? throw new InvalidOperationException("Invalid silo config JSON.");

        using var pingCapture = new MembershipPingCapture();
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _ = builder.Logging.AddFilter("Orleans.Runtime.MembershipService.SiloHealthMonitor", LogLevel.Trace);
        _ = builder.Logging.AddProvider(pingCapture);

        _ = Directory.CreateDirectory(config.SiloRoot);
        var metaStore = new FileSystemMetaStore(config.MetaStoreDir, TimeProvider.System);
        var objectStore = new FileSystemObjectStore(config.ObjectStoreDir, TimeProvider.System);
        _ = builder.Services.AddSingleton<IMetaStore>(metaStore);
        _ = builder.Services.AddSingleton<IObjectStore>(objectStore);

        _ = builder.UseOrleans(silo =>
        {
            _ = silo.Configure<ClusterOptions>(o =>
            {
                o.ClusterId = config.OrleansClusterId;
                o.ServiceId = "clog-orleans-bench";
            });
            _ = silo.Configure<SiloOptions>(o => o.SiloName = config.SiloName);
            _ = silo.UseDevelopmentClustering(new IPEndPoint(IPAddress.Loopback, config.PrimarySiloPort));
            _ = silo.ConfigureEndpoints(IPAddress.Loopback, config.SiloPort, config.GatewayPort);
            if (config.ProbeTimeoutMs > 0)
            {
                _ = silo.Configure<ClusterMembershipOptions>(o =>
                {
                    o.NumProbedSilos = 3;
                    o.ProbeTimeout = TimeSpan.FromMilliseconds(config.ProbeTimeoutMs);
                    o.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(5);
                });
            }

            _ = silo.AddDurableJournalStorage(opts => opts.Configure(siloOpts =>
            {
                siloOpts.Node = new DurableStorageOptions
                {
                    Cluster = config.StorageClusterId,
                    Root = config.SiloRoot,
                    WalFileSize = 1L << 30,
                    PreallocateWalFiles = false,
                    ArchiveStore = ArchiveStoreKind.Lsm,
                    ArchiveStorePrefix = "orleansbench",
                };
                siloOpts.Control.MaxConcurrentTransitions = config.ControlTransitions;
                if (config.ControlTransitions > 0)
                {
                    siloOpts.Control.LedgersPerNode = 1;
                    siloOpts.Control.SplitBytesPerSecond = 1L << 40;
                }
            }));
        });

        using var host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);

        try
        {
            var localNode = host.Services.GetRequiredService<LocalNode>();
            var registered = await localNode.Registered.WaitAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            var myNodeId = registered.Entry.Node;
            var resolver = host.Services.GetRequiredService<NodeRegistryResolver>();
            var transport = host.Services.GetRequiredService<OrleansLedgerReplicaTransport>();
            var replicaHost = host.Services.GetRequiredService<LedgerReplicaHost>();
            var forwardedReads = host.Services.GetRequiredService<ForwardedReads>();
            var router = host.Services.GetRequiredService<JournalRouter>();
            var routing = host.Services.GetRequiredService<LocalRouting>();
            var membershipTable = host.Services.GetService<IMembershipTable>();
            var membershipService = host.Services.GetService<IClusterMembershipService>();

            var nonActiveTransitions = 0;
            using var membershipWatchCts = new CancellationTokenSource();
            var membershipWatchTask = membershipService is null
                ? Task.CompletedTask
                : Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var snap in membershipService.MembershipUpdates.WithCancellation(membershipWatchCts.Token).ConfigureAwait(false))
                        {
                            foreach (var member in snap.Members.Values)
                            {
                                if (member.Status is not SiloStatus.Active and not SiloStatus.Joining and not SiloStatus.Created)
                                {
                                    _ = Interlocked.Increment(ref nonActiveTransitions);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });

            using var meterListener = new MeterListener();
            long pingsSentCounter = 0;
            long pingsReplyReceivedCounter = 0;
            long pingMissedCounter = 0;
            meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Microsoft.Orleans"
                    && instrument.Name is "orleans-messaging-pings-sent"
                        or "orleans-messaging-pings-reply-received"
                        or "orleans-messaging-pings-reply-missed")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            meterListener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
            {
                switch (instrument.Name)
                {
                    case "orleans-messaging-pings-sent":
                        _ = Interlocked.Add(ref pingsSentCounter, measurement);
                        break;
                    case "orleans-messaging-pings-reply-received":
                        _ = Interlocked.Add(ref pingsReplyReceivedCounter, measurement);
                        break;
                    case "orleans-messaging-pings-reply-missed":
                        _ = Interlocked.Add(ref pingMissedCounter, measurement);
                        break;
                    default:
                        break;
                }
            });
            meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                switch (instrument.Name)
                {
                    case "orleans-messaging-pings-sent":
                        _ = Interlocked.Add(ref pingsSentCounter, measurement);
                        break;
                    case "orleans-messaging-pings-reply-received":
                        _ = Interlocked.Add(ref pingsReplyReceivedCounter, measurement);
                        break;
                    case "orleans-messaging-pings-reply-missed":
                        _ = Interlocked.Add(ref pingMissedCounter, measurement);
                        break;
                    default:
                        break;
                }
            });
            meterListener.Start();

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, config.ControlPort).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            // Send initial hello
            var hello = new SiloHello
            {
                SiloName = config.SiloName,
                NodeId = myNodeId.Value.ToString("N"),
                IsServerGc = GCSettings.IsServerGC,
                ProcessorCount = Environment.ProcessorCount,
                Pid = Environment.ProcessId,
                SiloPort = config.SiloPort,
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(hello, JsonOptions)).ConfigureAwait(false);

            BenchmarkProbe? activeProbe = null;
            using var samplerCts = new CancellationTokenSource();
            var samplerToken = samplerCts.Token;
            var samplingActive = false;
            long peakReplicaHostBytes = 0;
            long peakForwardedReadBytes = 0;
            var suspectLock = new Lock();
            var baselineSuspectVotes = new HashSet<string>(StringComparer.Ordinal);
            var observedSuspectVotes = new HashSet<string>(StringComparer.Ordinal);
            var peakSamplerTask = Task.Run(async () =>
            {
                var tick = 0;
                while (!samplerToken.IsCancellationRequested)
                {
                    if (Volatile.Read(ref samplingActive))
                    {
                        BenchmarkHelpers.UpdateMax(ref peakReplicaHostBytes, replicaHost.Bytes);
                        BenchmarkHelpers.UpdateMax(ref peakForwardedReadBytes, forwardedReads.ReservedBytes);
                        tick++;
                        if (tick % 10 == 0)
                        {
                            Volatile.Read(ref activeProbe)?.SampleInstant();
                        }

                        if (membershipTable is not null && tick % 50 == 0)
                        {
                            try
                            {
                                var all = await membershipTable.ReadAllAsync().ConfigureAwait(false);
                                RecordSuspectVotes(all, suspectLock, baselineSuspectVotes, observedSuspectVotes);
                            }
                            catch
                            {
                                // Best-effort periodic transient suspicion sampling; final read happens in stop-probe.
                            }
                        }
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, samplerToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
            long probeStartSent = 0;
            long probeStartReceived = 0;
            long probeStartMissed = 0;
            int probeStartNonActive = 0;
            var keyVersions = new ConcurrentDictionary<string, JournalVersion>(StringComparer.Ordinal);

            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    var cmd = JsonSerializer.Deserialize<SiloCommand>(line, JsonOptions)!;
                    string responseJson;
                    try
                    {
                        switch (cmd.Command)
                        {
                            case "wait-resolved":
                                {
                                    var deadline = TimeProvider.System.GetUtcNow().AddSeconds(45);
                                    var targetNodes = (cmd.TargetNodeIds ?? []).Select(id => new NodeId(Guid.Parse(id))).ToArray();
                                    while (TimeProvider.System.GetUtcNow() < deadline)
                                    {
                                        if (targetNodes.All(n => resolver.Resolve(n) is not null))
                                        {
                                            break;
                                        }

                                        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System).ConfigureAwait(false);
                                    }

                                    if (!targetNodes.All(n => resolver.Resolve(n) is not null))
                                    {
                                        throw new TimeoutException("Timed out waiting for NodeRegistryResolver to resolve all cluster nodes.");
                                    }

                                    responseJson = JsonSerializer.Serialize(new AckReply { Ok = true }, JsonOptions);
                                    break;
                                }

                            case "wait-ledger-ready":
                                {
                                    var deadline = TimeProvider.System.GetUtcNow().AddSeconds(45);
                                    var rowKey = JournalRowKey.Encode(cmd.JournalKey ?? "bench");
                                    var minDistinctPrimaries = Math.Max(1, cmd.Count);
                                    NodeId? primaryNode = null;
                                    while (TimeProvider.System.GetUtcNow() < deadline)
                                    {
                                        var currentRouting = routing.Current;
                                        if (currentRouting is not null)
                                        {
                                            var map = await currentRouting.View.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                                            var openLedgers = map.Ledgers.Where(static l => l.Status.Value is LedgerStatus.Open).ToArray();
                                            var distinctPrimaries = openLedgers.Select(static l => l.Primary.Node).Distinct().Count();
                                            if (distinctPrimaries >= minDistinctPrimaries
                                                && map.FindOpen(rowKey.Span) is { } entry)
                                            {
                                                primaryNode = entry.Primary.Node;
                                                break;
                                            }
                                        }

                                        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System).ConfigureAwait(false);
                                    }

                                    if (primaryNode is null)
                                    {
                                        throw new TimeoutException("Timed out waiting for open ledger primary assignment.");
                                    }

                                    responseJson = JsonSerializer.Serialize(new OwnerInfoReply { PrimaryNodeId = primaryNode.Value.Value.ToString("N") }, JsonOptions);
                                    break;
                                }

                            case "start-probe":
                                {
                                    Volatile.Write(ref samplingActive, false);
                                    activeProbe?.Dispose();
                                    activeProbe = null;
                                    pingCapture.Reset();
                                    _ = Interlocked.Exchange(ref peakReplicaHostBytes, replicaHost.Bytes);
                                    _ = Interlocked.Exchange(ref peakForwardedReadBytes, forwardedReads.ReservedBytes);
                                    probeStartSent = Volatile.Read(ref pingsSentCounter);
                                    probeStartReceived = Volatile.Read(ref pingsReplyReceivedCounter);
                                    probeStartMissed = Volatile.Read(ref pingMissedCounter);
                                    probeStartNonActive = Volatile.Read(ref nonActiveTransitions);
                                    lock (suspectLock)
                                    {
                                        baselineSuspectVotes.Clear();
                                        observedSuspectVotes.Clear();
                                    }

                                    if (membershipTable is not null)
                                    {
                                        var baseline = await membershipTable.ReadAllAsync().ConfigureAwait(false);
                                        lock (suspectLock)
                                        {
                                            foreach (var vote in EnumerateSuspectVotes(baseline))
                                            {
                                                _ = baselineSuspectVotes.Add(vote);
                                            }
                                        }
                                    }

                                    activeProbe = BenchmarkProbe.Start();
                                    Volatile.Write(ref samplingActive, true);
                                    responseJson = JsonSerializer.Serialize(new AckReply { Ok = true }, JsonOptions);
                                    break;
                                }

                            case "stop-probe":
                                {
                                    Volatile.Write(ref samplingActive, false);
                                    BenchmarkHelpers.UpdateMax(ref peakReplicaHostBytes, replicaHost.Bytes);
                                    BenchmarkHelpers.UpdateMax(ref peakForwardedReadBytes, forwardedReads.ReservedBytes);

                                    if (activeProbe is null)
                                    {
                                        throw new InvalidOperationException("stop-probe called without an active probe.");
                                    }

                                    var summary = activeProbe.Stop(cmd.TotalBytes);
                                    activeProbe.Dispose();
                                    activeProbe = null;

                                    if (membershipTable is not null)
                                    {
                                        var all = await membershipTable.ReadAllAsync().ConfigureAwait(false);
                                        RecordSuspectVotes(all, suspectLock, baselineSuspectVotes, observedSuspectVotes);
                                    }

                                    int suspectCount;
                                    lock (suspectLock)
                                    {
                                        suspectCount = observedSuspectVotes.Count;
                                    }

                                    var pingSamples = pingCapture.Snapshot();
                                    var reply = new ProbeReply
                                    {
                                        ElapsedSec = summary.Elapsed.TotalSeconds,
                                        AllocatedBytes = summary.AllocatedBytes,
                                        SohBytes = summary.SampledSohBytes,
                                        LohBytes = summary.SampledLohBytes,
                                        PohBytes = summary.SampledPohBytes,
                                        Gen0 = summary.Gen0Collections,
                                        Gen1 = summary.Gen1Collections,
                                        Gen2 = summary.Gen2Collections,
                                        Gen2Causes = summary.FormatGen2Causes(),
                                        GcPausePercent = summary.GcPausePercent,
                                        CpuMs = summary.CpuTime.TotalMilliseconds,
                                        PeakWorkingSetMiB = summary.PeakWorkingSetMiB,
                                        PeakReplicaHostBytes = Volatile.Read(ref peakReplicaHostBytes),
                                        PeakForwardedReadBytes = Volatile.Read(ref peakForwardedReadBytes),
                                        PingSamples = pingSamples,
                                        PingsSentCount = Volatile.Read(ref pingsSentCounter) - probeStartSent,
                                        PingsReplyReceivedCount = Volatile.Read(ref pingsReplyReceivedCounter) - probeStartReceived,
                                        PingMissedCount = Volatile.Read(ref pingMissedCounter) - probeStartMissed,
                                        SuspectCount = suspectCount,
                                        NonActiveTransitions = Volatile.Read(ref nonActiveTransitions) - probeStartNonActive,
                                    };
                                    responseJson = JsonSerializer.Serialize(reply, JsonOptions);
                                    break;
                                }

                            case "run-replica-latency":
                                {
                                    var target = new NodeId(Guid.Parse(cmd.TargetNodeId!));
                                    var ledgerId = new LedgerId(cmd.LedgerStart);
                                    await using var replica = transport.Open(target, ledgerId, new LedgerReplicaOptions { WindowSize = 64 });
                                    var raw = new byte[cmd.PayloadBytes];
                                    new Random(42 ^ cmd.PayloadBytes).NextBytes(raw);
                                    using var bw = new ArcBufferWriter(cmd.PayloadBytes);
                                    bw.Write(raw);
                                    using var payload = bw.PeekSlice(bw.Length);

                                    // Warmup 10 writes/reads
                                    for (long s = 0; s < 10; s++)
                                    {
                                        var w = await replica.WriteAsync(new Slot(s), payload, Slot.Zero, recovery: false).ConfigureAwait(false);
                                        if (w.Value is not ReplicaWriteOutcome.Written)
                                        {
                                            throw new InvalidOperationException($"Warmup write failed: {w.Value}");
                                        }

                                        var r = await replica.ReadAsync(new Slot(s), Slot.Zero).ConfigureAwait(false);
                                        if (r.TryGetValue(out ArcBuffer warmData))
                                        {
                                            warmData.Dispose();
                                        }
                                        else
                                        {
                                            throw new InvalidOperationException($"Warmup read failed: {r}");
                                        }
                                    }

                                    var writeUs = new double[cmd.Count];
                                    var readUs = new double[cmd.Count];
                                    for (var i = 0; i < cmd.Count; i++)
                                    {
                                        var slot = new Slot(10L + i);
                                        var t0 = Stopwatch.GetTimestamp();
                                        var w = await replica.WriteAsync(slot, payload, Slot.Zero, recovery: false).ConfigureAwait(false);
                                        writeUs[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
                                        if (w.Value is not ReplicaWriteOutcome.Written)
                                        {
                                            throw new InvalidOperationException($"Replica write failed: {w.Value}");
                                        }

                                        var t1 = Stopwatch.GetTimestamp();
                                        var r = await replica.ReadAsync(slot, Slot.Zero).ConfigureAwait(false);
                                        readUs[i] = Stopwatch.GetElapsedTime(t1).TotalMicroseconds;
                                        if (r.TryGetValue(out ArcBuffer readData))
                                        {
                                            readData.Dispose();
                                        }
                                        else
                                        {
                                            throw new InvalidOperationException($"Replica read failed: {r}");
                                        }
                                    }

                                    Array.Sort(writeUs);
                                    Array.Sort(readUs);
                                    responseJson = JsonSerializer.Serialize(new LatencyReply
                                    {
                                        WriteP50Us = BenchmarkHelpers.Percentile(writeUs, 0.50),
                                        WriteP99Us = BenchmarkHelpers.Percentile(writeUs, 0.99),
                                        ReadP50Us = BenchmarkHelpers.Percentile(readUs, 0.50),
                                        ReadP99Us = BenchmarkHelpers.Percentile(readUs, 0.99),
                                    }, JsonOptions);
                                    break;
                                }

                            case "run-replica-fanout":
                                {
                                    var targets = (cmd.TargetNodeIds ?? []).Select(id => new NodeId(Guid.Parse(id))).ToArray();
                                    if (targets.Length != 3)
                                    {
                                        throw new InvalidOperationException($"run-replica-fanout requires exactly 3 targets, got {targets.Length}.");
                                    }

                                    var ledgers = cmd.Ledgers;
                                    var raw = new byte[cmd.PayloadBytes];
                                    new Random(1337 ^ cmd.PayloadBytes).NextBytes(raw);

                                    var stopTick = Stopwatch.GetTimestamp() + (long)(cmd.DurationSec * Stopwatch.Frequency);
                                    var samplesPerLedger = new List<double>[ledgers];
                                    for (var l = 0; l < ledgers; l++)
                                    {
                                        List<double> list = [];
                                        _ = list.EnsureCapacity(4096);
                                        samplesPerLedger[l] = list;
                                    }

                                    long totalBytes = 0;
                                    long backpressureCount = 0;

                                    var tasks = new Task[ledgers];
                                    for (var l = 0; l < ledgers; l++)
                                    {
                                        var ledgerIdx = l;
                                        var ledgerId = new LedgerId(cmd.LedgerStart + (ulong)l);
                                        tasks[l] = Task.Run(async () =>
                                        {
                                            // Each ledger owns its own ArcBuffer so only its N-1 remote sends share a sender copy,
                                            // matching LedgerWriter and OrleansReplicaTransportMeasurements.FanOutAsync.
                                            using var bw = new ArcBufferWriter(cmd.PayloadBytes);
                                            bw.Write(raw);
                                            using var payload = bw.ConsumeSlice(cmd.PayloadBytes);
                                            var replicas = targets.Select(t => transport.Open(t, ledgerId, new LedgerReplicaOptions { WindowSize = 64 })).ToArray();
                                            var pending = new List<Task<ReplicaWriteOutcome>>(3);
                                            var ledgerSamples = samplesPerLedger[ledgerIdx];
                                            try
                                            {
                                                long slot = 0;
                                                while (Stopwatch.GetTimestamp() < stopTick)
                                                {
                                                    var curSlot = new Slot(slot++);
                                                    var t0 = Stopwatch.GetTimestamp();
                                                    // N=3 fan-out with shared ArcBuffer payload, wait for Q=2 acks (then drain 3rd before next slot).
                                                    pending.Clear();
                                                    pending.Add(replicas[0].WriteAsync(curSlot, payload, Slot.Zero, recovery: false).AsTask());
                                                    pending.Add(replicas[1].WriteAsync(curSlot, payload, Slot.Zero, recovery: false).AsTask());
                                                    pending.Add(replicas[2].WriteAsync(curSlot, payload, Slot.Zero, recovery: false).AsTask());
                                                    var acks = 0;
                                                    while (acks < 2 && pending.Count > 0)
                                                    {
                                                        var done = await Task.WhenAny(pending).ConfigureAwait(false);
                                                        _ = pending.Remove(done);
                                                        var outcome = await done.ConfigureAwait(false);
                                                        if (outcome.Value is ReplicaWriteOutcome.Written)
                                                        {
                                                            acks++;
                                                        }
                                                        else if (outcome.Value is ReplicaWriteOutcome.Backpressure)
                                                        {
                                                            _ = Interlocked.Increment(ref backpressureCount);
                                                        }
                                                        else
                                                        {
                                                            throw new InvalidOperationException($"Unexpected replica fanout write outcome: {outcome.Value}");
                                                        }
                                                    }

                                                    var quorumUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;

                                                    // Drain remaining 3rd write before advancing slot or reusing resources.
                                                    if (pending.Count > 0)
                                                    {
                                                        var remaining = await Task.WhenAll(pending).ConfigureAwait(false);
                                                        pending.Clear();
                                                        foreach (var rem in remaining)
                                                        {
                                                            if (rem.Value is ReplicaWriteOutcome.Written)
                                                            {
                                                                acks++;
                                                            }
                                                            else if (rem.Value is ReplicaWriteOutcome.Backpressure)
                                                            {
                                                                _ = Interlocked.Increment(ref backpressureCount);
                                                            }
                                                            else
                                                            {
                                                                throw new InvalidOperationException($"Unexpected trailing replica fanout write outcome: {rem.Value}");
                                                            }
                                                        }
                                                    }

                                                    if (acks < 2)
                                                    {
                                                        throw new InvalidOperationException($"Replica fanout failed to reach quorum Q=2 (acks={acks}, backpressure={Volatile.Read(ref backpressureCount)}).");
                                                    }

                                                    ledgerSamples.Add(quorumUs);
                                                    _ = Interlocked.Add(ref totalBytes, cmd.PayloadBytes);
                                                }
                                            }
                                            finally
                                            {
                                                if (pending.Count > 0)
                                                {
                                                    try
                                                    {
                                                        _ = await Task.WhenAll(pending).ConfigureAwait(false);
                                                    }
                                                    catch
                                                    {
                                                        // Ensure in-flight writes observe completion before disposing payload/replicas.
                                                    }

                                                    pending.Clear();
                                                }

                                                foreach (var r in replicas)
                                                {
                                                    await r.DisposeAsync().ConfigureAwait(false);
                                                }
                                            }
                                        });
                                    }

                                    await Task.WhenAll(tasks).ConfigureAwait(false);
                                    var totalSamples = samplesPerLedger.Sum(l => l.Count);
                                    var merged = new double[totalSamples];
                                    var copyOffset = 0;
                                    foreach (var list in samplesPerLedger)
                                    {
                                        list.CopyTo(merged, copyOffset);
                                        copyOffset += list.Count;
                                    }

                                    Array.Sort(merged);
                                    responseJson = JsonSerializer.Serialize(new FanoutReply
                                    {
                                        TotalBytes = Volatile.Read(ref totalBytes),
                                        BackpressureCount = Volatile.Read(ref backpressureCount),
                                        WriteP50Us = BenchmarkHelpers.Percentile(merged, 0.50),
                                        WriteP99Us = BenchmarkHelpers.Percentile(merged, 0.99),
                                        LatenciesUs = cmd.IncludeLatencies ? merged : null,
                                    }, JsonOptions);
                                    break;
                                }

                            case "run-replica-burst":
                                {
                                    var target = new NodeId(Guid.Parse(cmd.TargetNodeId!));
                                    var raw = new byte[cmd.PayloadBytes];
                                    new Random(999 ^ cmd.PayloadBytes).NextBytes(raw);

                                    var stopTick = Stopwatch.GetTimestamp() + (long)(cmd.DurationSec * Stopwatch.Frequency);
                                    long totalBytes = 0;
                                    long backpressureCount = 0;
                                    var tasks = new Task[cmd.Writers];
                                    for (var w = 0; w < cmd.Writers; w++)
                                    {
                                        var ledgerId = new LedgerId(cmd.LedgerStart + (ulong)w);
                                        tasks[w] = Task.Run(async () =>
                                        {
                                            // Each writer owns its own ArcBuffer so no two writers share a sender copy.
                                            using var bw = new ArcBufferWriter(cmd.PayloadBytes);
                                            bw.Write(raw);
                                            using var payload = bw.ConsumeSlice(cmd.PayloadBytes);
                                            await using var replica = transport.Open(target, ledgerId, new LedgerReplicaOptions { WindowSize = 64 });
                                            long slot = 0;
                                            while (Stopwatch.GetTimestamp() < stopTick)
                                            {
                                                var outcome = await replica.WriteAsync(new Slot(slot++), payload, Slot.Zero, recovery: false).ConfigureAwait(false);
                                                if (outcome.Value is ReplicaWriteOutcome.Written)
                                                {
                                                    _ = Interlocked.Add(ref totalBytes, cmd.PayloadBytes);
                                                }
                                                else if (outcome.Value is ReplicaWriteOutcome.Backpressure)
                                                {
                                                    _ = Interlocked.Increment(ref backpressureCount);
                                                    slot--;
                                                    await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System).ConfigureAwait(false);
                                                }
                                                else
                                                {
                                                    throw new InvalidOperationException($"Unexpected replica burst write outcome: {outcome.Value}");
                                                }
                                            }
                                        });
                                    }

                                    await Task.WhenAll(tasks).ConfigureAwait(false);
                                    responseJson = JsonSerializer.Serialize(new BurstReply
                                    {
                                        TotalBytes = Volatile.Read(ref totalBytes),
                                        BackpressureCount = Volatile.Read(ref backpressureCount),
                                    }, JsonOptions);
                                    break;
                                }

                            case "seed-forwarded-journal":
                                {
                                    var key = cmd.JournalKey!;
                                    var rowKey = JournalRowKey.Encode(key);
                                    var raw = new byte[cmd.PayloadBytes];
                                    new Random(777).NextBytes(raw);
                                    using var bw = new ArcBufferWriter(cmd.PayloadBytes);
                                    bw.Write(raw);
                                    using var payload = bw.PeekSlice(bw.Length);
                                    var version = keyVersions.GetValueOrDefault(key, JournalVersion.Origin);
                                    for (var i = 0; i < cmd.Count; i++)
                                    {
                                        using var record = i == 0
                                            ? JournalEntryRecord.Replace(rowKey, version, payload)
                                            : JournalEntryRecord.Append(rowKey, version, payload);
                                        var outcome = await router.WriteAsync(key, rowKey, record, CancellationToken.None).ConfigureAwait(false);
                                        if (outcome.Value is JournalWriteOutcome.Accepted accepted)
                                        {
                                            version = accepted.Version;
                                        }
                                        else if (outcome.Value is JournalWriteOutcome.Rejected rejected)
                                        {
                                            version = rejected.CurrentVersion;
                                            i--;
                                        }
                                        else
                                        {
                                            throw new InvalidOperationException($"Seed write {i} failed: {outcome.Value}");
                                        }
                                    }

                                    keyVersions[key] = version;
                                    responseJson = JsonSerializer.Serialize(new AckReply { Ok = true }, JsonOptions);
                                    break;
                                }

                            case "warmup-forwarded-writes":
                                {
                                    var keyPrefix = cmd.JournalKey!;
                                    var targetPrimary = new NodeId(Guid.Parse(cmd.TargetNodeId!));
                                    var warmKeys = SelectRemoteKeys(routing, myNodeId, targetPrimary, $"{keyPrefix}-warm", cmd.Count);
                                    var raw = new byte[cmd.PayloadBytes];
                                    new Random(555 ^ cmd.PayloadBytes).NextBytes(raw);
                                    using var bw = new ArcBufferWriter(cmd.PayloadBytes);
                                    bw.Write(raw);
                                    using var payload = bw.PeekSlice(bw.Length);

                                    for (var w = 0; w < warmKeys.Length; w++)
                                    {
                                        var (warmKey, warmRowKey) = warmKeys[w];
                                        VerifyNotPrimary(routing, myNodeId, warmRowKey, warmKey);
                                        using var warmRec = JournalEntryRecord.Replace(warmRowKey, JournalVersion.Origin, payload);
                                        var warmOut = await router.WriteAsync(warmKey, warmRowKey, warmRec, CancellationToken.None).ConfigureAwait(false);
                                        if (warmOut.Value is not JournalWriteOutcome.Accepted warmAcc)
                                        {
                                            throw new InvalidOperationException($"Forwarded warmup write {w} failed: {warmOut.Value}");
                                        }

                                        keyVersions[warmKey] = warmAcc.Version;
                                    }

                                    responseJson = JsonSerializer.Serialize(new AckReply { Ok = true }, JsonOptions);
                                    break;
                                }

                            case "run-forwarded-writes":
                                {
                                    var keyPrefix = cmd.JournalKey!;
                                    var targetPrimary = new NodeId(Guid.Parse(cmd.TargetNodeId!));
                                    var remoteKeys = SelectRemoteKeys(routing, myNodeId, targetPrimary, keyPrefix, 16);
                                    var raw = new byte[cmd.PayloadBytes];
                                    new Random(555 ^ cmd.PayloadBytes).NextBytes(raw);
                                    using var bw = new ArcBufferWriter(cmd.PayloadBytes);
                                    bw.Write(raw);
                                    using var payload = bw.PeekSlice(bw.Length);

                                    var latenciesUs = new double[cmd.Count];
                                    var tStart = Stopwatch.GetTimestamp();
                                    for (var i = 0; i < cmd.Count; i++)
                                    {
                                        var (key, rowKey) = remoteKeys[i % remoteKeys.Length];
                                        VerifyNotPrimary(routing, myNodeId, rowKey, key);
                                        var expected = keyVersions.GetValueOrDefault(key, JournalVersion.Origin);
                                        var t0 = Stopwatch.GetTimestamp();
                                        using var record = JournalEntryRecord.Replace(rowKey, expected, payload);
                                        var outcome = await router.WriteAsync(key, rowKey, record, CancellationToken.None).ConfigureAwait(false);
                                        if (outcome.Value is JournalWriteOutcome.Rejected rejected)
                                        {
                                            using var retry = JournalEntryRecord.Replace(rowKey, rejected.CurrentVersion, payload);
                                            outcome = await router.WriteAsync(key, rowKey, retry, CancellationToken.None).ConfigureAwait(false);
                                        }

                                        latenciesUs[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
                                        if (outcome.Value is JournalWriteOutcome.Accepted accepted)
                                        {
                                            keyVersions[key] = accepted.Version;
                                        }
                                        else
                                        {
                                            throw new InvalidOperationException($"Forwarded write {i} failed: {outcome.Value}");
                                        }
                                    }

                                    var elapsedSec = Stopwatch.GetElapsedTime(tStart).TotalSeconds;
                                    var totalBytes = (long)cmd.Count * cmd.PayloadBytes;
                                    Array.Sort(latenciesUs);
                                    responseJson = JsonSerializer.Serialize(new ForwardedWriteReply
                                    {
                                        TotalBytes = totalBytes,
                                        OpsPerSecond = cmd.Count / Math.Max(elapsedSec, 1e-9),
                                        MiBPerSecond = totalBytes / (1024.0 * 1024.0) / Math.Max(elapsedSec, 1e-9),
                                        P50Us = BenchmarkHelpers.Percentile(latenciesUs, 0.50),
                                        P99Us = BenchmarkHelpers.Percentile(latenciesUs, 0.99),
                                    }, JsonOptions);
                                    break;
                                }

                            case "warmup-forwarded-reads":
                                {
                                    var key = cmd.JournalKey!;
                                    var rowKey = JournalRowKey.Encode(key);
                                    VerifyNotPrimary(routing, myNodeId, rowKey, key);
                                    var read = await router.ReadAsync(key, rowKey, CancellationToken.None).ConfigureAwait(false);
                                    using (read.Content)
                                    {
                                        if (read.Content.Length != cmd.TotalBytes)
                                        {
                                            throw new InvalidOperationException($"Warmup forwarded read length mismatch: expected {cmd.TotalBytes}, got {read.Content.Length}.");
                                        }
                                    }

                                    responseJson = JsonSerializer.Serialize(new AckReply { Ok = true }, JsonOptions);
                                    break;
                                }

                            case "run-forwarded-reads":
                                {
                                    var key = cmd.JournalKey!;
                                    var rowKey = JournalRowKey.Encode(key);
                                    VerifyNotPrimary(routing, myNodeId, rowKey, key);
                                    var concurrency = Math.Max(1, cmd.Writers);
                                    var expectedBytes = cmd.TotalBytes;
                                    var stopTick = Stopwatch.GetTimestamp() + (long)(cmd.DurationSec * Stopwatch.Frequency);
                                    var latenciesUs = new ConcurrentBag<double>();
                                    long totalBytes = 0;

                                    var tStart = Stopwatch.GetTimestamp();
                                    var tasks = new Task[concurrency];
                                    for (var w = 0; w < concurrency; w++)
                                    {
                                        tasks[w] = Task.Run(async () =>
                                        {
                                            while (Stopwatch.GetTimestamp() < stopTick)
                                            {
                                                var t0 = Stopwatch.GetTimestamp();
                                                var read = await router.ReadAsync(key, rowKey, CancellationToken.None).ConfigureAwait(false);
                                                using (read.Content)
                                                {
                                                    if (read.Content.Length != expectedBytes)
                                                    {
                                                        throw new InvalidOperationException($"Forwarded read length mismatch: expected {expectedBytes}, got {read.Content.Length}.");
                                                    }

                                                    latenciesUs.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
                                                    _ = Interlocked.Add(ref totalBytes, read.Content.Length);
                                                }
                                            }
                                        });
                                    }

                                    await Task.WhenAll(tasks).ConfigureAwait(false);
                                    var elapsedSec = Stopwatch.GetElapsedTime(tStart).TotalSeconds;
                                    var sorted = latenciesUs.ToArray();
                                    Array.Sort(sorted);
                                    var bytes = Volatile.Read(ref totalBytes);
                                    responseJson = JsonSerializer.Serialize(new ForwardedReadReply
                                    {
                                        TotalBytes = bytes,
                                        ReadCount = sorted.Length,
                                        MiBPerSecond = bytes / (1024.0 * 1024.0) / Math.Max(elapsedSec, 1e-9),
                                        P50Us = BenchmarkHelpers.Percentile(sorted, 0.50),
                                        P99Us = BenchmarkHelpers.Percentile(sorted, 0.99),
                                    }, JsonOptions);
                                    break;
                                }

                            case "run-forwarded-concurrent":
                                {
                                    var readKey = cmd.JournalKey!;
                                    var readRowKey = JournalRowKey.Encode(readKey);
                                    VerifyNotPrimary(routing, myNodeId, readRowKey, readKey);
                                    var writePrefix = cmd.WriteKeyPrefix!;
                                    var targetPrimary = new NodeId(Guid.Parse(cmd.TargetNodeId!));
                                    var writeKeys = SelectRemoteKeys(routing, myNodeId, targetPrimary, writePrefix, 16);
                                    var expectedBytes = cmd.TotalBytes;
                                    var concurrency = Math.Max(1, cmd.Writers);

                                    var raw = new byte[cmd.PayloadBytes];
                                    new Random(888 ^ cmd.PayloadBytes).NextBytes(raw);
                                    using var bw = new ArcBufferWriter(cmd.PayloadBytes);
                                    bw.Write(raw);
                                    using var payload = bw.PeekSlice(bw.Length);

                                    using var readersCts = new CancellationTokenSource();
                                    var activeReaders = 0;
                                    var allActiveTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                    var writesRunning = false;
                                    var readLatenciesDuringWrites = new ConcurrentBag<double>();
                                    long readTotalBytes = 0;

                                    var readerTasks = new Task[concurrency];
                                    for (var w = 0; w < concurrency; w++)
                                    {
                                        readerTasks[w] = Task.Run(async () =>
                                        {
                                            while (!readersCts.IsCancellationRequested)
                                            {
                                                var nowActive = Interlocked.Increment(ref activeReaders);
                                                if (nowActive == concurrency)
                                                {
                                                    _ = allActiveTcs.TrySetResult();
                                                }

                                                try
                                                {
                                                    var t0 = Stopwatch.GetTimestamp();
                                                    var read = await router.ReadAsync(readKey, readRowKey, readersCts.Token).ConfigureAwait(false);
                                                    using (read.Content)
                                                    {
                                                        if (read.Content.Length != expectedBytes)
                                                        {
                                                            throw new InvalidOperationException($"Concurrent forwarded read length mismatch: expected {expectedBytes}, got {read.Content.Length}.");
                                                        }

                                                        var elapsedUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
                                                        _ = Interlocked.Add(ref readTotalBytes, read.Content.Length);
                                                        if (Volatile.Read(ref writesRunning))
                                                        {
                                                            readLatenciesDuringWrites.Add(elapsedUs);
                                                        }
                                                    }
                                                }
                                                catch (OperationCanceledException) when (readersCts.IsCancellationRequested)
                                                {
                                                    break;
                                                }
                                                finally
                                                {
                                                    _ = Interlocked.Decrement(ref activeReaders);
                                                }
                                            }
                                        });
                                    }

                                    var writeLatenciesList = new List<double>(4096);
                                    try
                                    {
                                        // Wait until all 4 readers are actively inside router.ReadAsync simultaneously.
                                        await allActiveTcs.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                                        Volatile.Write(ref writesRunning, true);

                                        var stopTick = Stopwatch.GetTimestamp() + (long)(cmd.DurationSec * Stopwatch.Frequency);
                                        var i = 0;
                                        while (Stopwatch.GetTimestamp() < stopTick || writeLatenciesList.Count < 10)
                                        {
                                            var (key, rowKey) = writeKeys[i % writeKeys.Length];
                                            i++;
                                            VerifyNotPrimary(routing, myNodeId, rowKey, key);
                                            var expected = keyVersions.GetValueOrDefault(key, JournalVersion.Origin);
                                            var t0 = Stopwatch.GetTimestamp();
                                            using var record = JournalEntryRecord.Replace(rowKey, expected, payload);
                                            var outcome = await router.WriteAsync(key, rowKey, record, CancellationToken.None).ConfigureAwait(false);
                                            if (outcome.Value is JournalWriteOutcome.Rejected rejected)
                                            {
                                                using var retry = JournalEntryRecord.Replace(rowKey, rejected.CurrentVersion, payload);
                                                outcome = await router.WriteAsync(key, rowKey, retry, CancellationToken.None).ConfigureAwait(false);
                                            }

                                            writeLatenciesList.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
                                            if (outcome.Value is JournalWriteOutcome.Accepted accepted)
                                            {
                                                keyVersions[key] = accepted.Version;
                                            }
                                            else
                                            {
                                                throw new InvalidOperationException($"Concurrent forwarded write {i} failed: {outcome.Value}");
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        Volatile.Write(ref writesRunning, false);
                                        await readersCts.CancelAsync().ConfigureAwait(false);
                                        await Task.WhenAll(readerTasks).ConfigureAwait(false);
                                    }

                                    var writeLatenciesUs = writeLatenciesList.ToArray();
                                    Array.Sort(writeLatenciesUs);
                                    var sortedReads = readLatenciesDuringWrites.ToArray();
                                    Array.Sort(sortedReads);
                                    responseJson = JsonSerializer.Serialize(new ForwardedConcurrentReply
                                    {
                                        WriteTotalBytes = (long)writeLatenciesUs.Length * cmd.PayloadBytes,
                                        ReadTotalBytes = Volatile.Read(ref readTotalBytes),
                                        WriteP50Us = BenchmarkHelpers.Percentile(writeLatenciesUs, 0.50),
                                        WriteP99Us = BenchmarkHelpers.Percentile(writeLatenciesUs, 0.99),
                                        ReadP50Us = BenchmarkHelpers.Percentile(sortedReads, 0.50),
                                        ReadP99Us = BenchmarkHelpers.Percentile(sortedReads, 0.99),
                                        ConcurrentReadCount = sortedReads.Length,
                                    }, JsonOptions);
                                    break;
                                }

                            case "shutdown":
                                await writer.WriteLineAsync(JsonSerializer.Serialize(new AckReply { Ok = true }, JsonOptions)).ConfigureAwait(false);
                                return 0;

                            default:
                                throw new InvalidOperationException($"Unknown command '{cmd.Command}'.");
                        }
                    }
                    catch (Exception ex) when (!FatalErrorHandler.IsReported(ex))
                    {
                        responseJson = JsonSerializer.Serialize(new ErrorReply { Error = ex.ToString() }, JsonOptions);
                    }

                    await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
                }
            }
            finally
            {
                Volatile.Write(ref samplingActive, false);
                activeProbe?.Dispose();
                activeProbe = null;
                await samplerCts.CancelAsync().ConfigureAwait(false);
                await peakSamplerTask.ConfigureAwait(false);
                await membershipWatchCts.CancelAsync().ConfigureAwait(false);
                await membershipWatchTask.ConfigureAwait(false);
            }

            return 0;
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await host.StopAsync(stopCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best effort stop on shutdown.
            }
        }
    }

    private static (string Key, JournalRowKey RowKey)[] SelectRemoteKeys(
        LocalRouting routing,
        NodeId myNodeId,
        NodeId targetPrimary,
        string prefix,
        int count)
    {
        if (targetPrimary == myNodeId)
        {
            throw new InvalidOperationException($"Target primary {targetPrimary.Value:N} matches local forwarder node {myNodeId.Value:N}.");
        }

        var map = routing.Current?.View.Current
            ?? throw new InvalidOperationException($"Routing table unavailable while selecting remote keys for '{prefix}'.");
        var selected = new (string Key, JournalRowKey RowKey)[count];
        var found = 0;
        for (var candidate = 0; found < count && candidate < count * 512; candidate++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{prefix}-{candidate}");
            var rowKey = JournalRowKey.Encode(key);
            if (map.FindOpen(rowKey.Span) is { } entry && entry.Primary.Node == targetPrimary)
            {
                selected[found++] = (key, rowKey);
            }
        }

        if (found < count)
        {
            throw new InvalidOperationException($"Could only find {found}/{count} remote keys routing to primary {targetPrimary.Value:N}.");
        }

        return selected;
    }

    private static void VerifyNotPrimary(LocalRouting routing, NodeId myNodeId, JournalRowKey rowKey, string key)
    {
        var map = routing.Current?.View.Current
            ?? throw new InvalidOperationException($"Routing table unavailable while forwarding key '{key}'.");
        var entry = map.FindOpen(rowKey.Span)
            ?? throw new InvalidOperationException($"No open ledger found for forwarded key '{key}'.");
        if (entry.Primary.Node == myNodeId)
        {
            throw new InvalidOperationException($"Key '{key}' routed locally to node {myNodeId.Value:N} instead of forwarding.");
        }
    }

    private static IEnumerable<string> EnumerateSuspectVotes(MembershipTableData data)
    {
        foreach (var (entry, _) in data.Members)
        {
            if (entry.SuspectTimes is not { Count: > 0 })
            {
                continue;
            }

            foreach (var vote in entry.SuspectTimes)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"{entry.SiloAddress}|{vote.Item1}|{vote.Item2.Ticks}");
            }
        }
    }

    private static void RecordSuspectVotes(
        MembershipTableData data,
        Lock suspectLock,
        HashSet<string> baselineVotes,
        HashSet<string> observedVotes)
    {
        lock (suspectLock)
        {
            foreach (var vote in EnumerateSuspectVotes(data))
            {
                if (!baselineVotes.Contains(vote))
                {
                    _ = observedVotes.Add(vote);
                }
            }
        }
    }

    private static void AssertServerGc(MultiProcessCluster cluster)
    {
        foreach (var silo in cluster.Silos)
        {
            if (!silo.IsServerGc)
            {
                throw new InvalidOperationException($"Silo {silo.SiloName} (PID {silo.Pid}) is not running Server GC!");
            }
        }
    }

    private static void AddSample<TKey, TVal>(Dictionary<TKey, List<TVal>> map, TKey key, TVal val)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(val);
    }

    // -------------------------------------------------------------------------
    // Real SiloHealthMonitor Membership Ping RTT Capture (with Target Silo Port)
    // -------------------------------------------------------------------------
    private sealed class MembershipPingCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<PingSample> _samples = new();

        public ILogger CreateLogger(string categoryName) =>
            string.Equals(categoryName, "Orleans.Runtime.MembershipService.SiloHealthMonitor", StringComparison.Ordinal)
                ? new PingLogger(_samples)
                : NullLogger.Instance;

        public void Reset()
        {
            while (_samples.TryDequeue(out _))
            {
            }
        }

        public PingSample[] Snapshot() => [.. _samples];

        public void Dispose()
        {
        }

        private sealed class PingLogger(ConcurrentQueue<PingSample> samples) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Trace;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel != LogLevel.Trace)
                {
                    return;
                }

                if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                {
                    double? rttMs = null;
                    var targetPort = 0;
                    for (var i = 0; i < pairs.Count; i++)
                    {
                        if (string.Equals(pairs[i].Key, "RoundTripTime", StringComparison.Ordinal) && pairs[i].Value is TimeSpan rtt)
                        {
                            rttMs = rtt.TotalMilliseconds;
                        }
                        else if (string.Equals(pairs[i].Key, "Silo", StringComparison.Ordinal) && pairs[i].Value is SiloAddress siloAddr)
                        {
                            targetPort = siloAddr.Endpoint.Port;
                        }
                    }

                    if (rttMs is { } ms)
                    {
                        samples.Enqueue(new PingSample { TargetPort = targetPort, RttMs = ms });
                    }
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Multi-Process Cluster Manager (guarantees child-process cleanup)
    // -------------------------------------------------------------------------
    private sealed class MultiProcessCluster : IAsyncDisposable
    {
        private readonly string _rootDir;
        private readonly TcpListener _listener;

        private MultiProcessCluster(string rootDir, TcpListener listener, List<SiloHandle> silos)
        {
            _rootDir = rootDir;
            _listener = listener;
            Silos = silos;
        }

        public IReadOnlyList<SiloHandle> Silos { get; }

        public static async Task<MultiProcessCluster> StartAsync(
            int siloCount,
            int controlTransitions,
            int? calleeProcessorCount = null,
            int probeTimeoutMs = 0)
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "clog-orleans-bench-" + Guid.NewGuid().ToString("N"));
            var metaDir = Path.Combine(rootDir, "meta");
            var objDir = Path.Combine(rootDir, "objects");
            _ = Directory.CreateDirectory(metaDir);
            _ = Directory.CreateDirectory(objDir);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var controlPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            var storageClusterId = Guid.NewGuid();
            var orleansClusterId = "bench-" + Guid.NewGuid().ToString("N")[..8];
            var basePort = ReserveFreePortBlock(siloCount * 2);
            var primarySiloPort = basePort;

            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine current executable path.");
            var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;

            var handles = new List<SiloHandle>(siloCount);
            var cluster = new MultiProcessCluster(rootDir, listener, handles);

            try
            {
                for (var i = 0; i < siloCount; i++)
                {
                    var siloName = $"silo-{i}";
                    var cfgPath = Path.Combine(rootDir, $"{siloName}.json");
                    var cfg = new SiloLaunchConfig
                    {
                        SiloName = siloName,
                        StorageClusterId = storageClusterId,
                        OrleansClusterId = orleansClusterId,
                        MetaStoreDir = metaDir,
                        ObjectStoreDir = objDir,
                        SiloRoot = Path.Combine(rootDir, siloName),
                        ControlPort = controlPort,
                        PrimarySiloPort = primarySiloPort,
                        SiloPort = basePort + i * 2,
                        GatewayPort = basePort + i * 2 + 1,
                        ControlTransitions = controlTransitions,
                        ProbeTimeoutMs = probeTimeoutMs,
                    };
                    await File.WriteAllTextAsync(cfgPath, JsonSerializer.Serialize(cfg, JsonOptions)).ConfigureAwait(false);

                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    };
                    if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                        || exePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
                    {
                        psi.ArgumentList.Add(entryAssembly!);
                    }

                    psi.ArgumentList.Add("--orleans-silo");
                    psi.ArgumentList.Add(cfgPath);
                    psi.Environment["DOTNET_gcServer"] = "1";
                    if (i == 0 && calleeProcessorCount is { } procCount)
                    {
                        psi.Environment["DOTNET_PROCESSOR_COUNT"] = procCount.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        _ = psi.Environment.Remove("DOTNET_PROCESSOR_COUNT");
                    }

                    var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start silo {siloName}.");
                    TcpClient? tcp = null;
                    try
                    {
                        var stderrBuf = new StringBuilder();
                        proc.ErrorDataReceived += (_, e) =>
                        {
                            if (e.Data is not null)
                            {
                                lock (stderrBuf)
                                {
                                    _ = stderrBuf.AppendLine(e.Data);
                                }
                            }
                        };
                        proc.BeginErrorReadLine();
                        proc.OutputDataReceived += (_, _) => { };
                        proc.BeginOutputReadLine();

                        using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        try
                        {
                            tcp = await listener.AcceptTcpClientAsync(acceptCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            string err;
                            lock (stderrBuf)
                            {
                                err = stderrBuf.ToString();
                            }

                            throw new InvalidOperationException($"Silo {siloName} failed to connect to coordinator. Stderr:\n{err}", ex);
                        }

                        var handle = await SiloHandle.CreateAsync(proc, tcp, stderrBuf, acceptCts.Token).ConfigureAwait(false);
                        handles.Add(handle);
                    }
                    catch
                    {
                        tcp?.Dispose();
                        try
                        {
                            if (!proc.HasExited)
                            {
                                proc.Kill(entireProcessTree: true);
                                _ = proc.WaitForExit(3000);
                            }
                        }
                        catch
                        {
                            // Best-effort cleanup of unattached child silo process.
                        }

                        proc.Dispose();
                        throw;
                    }
                }

                // Wait for every silo to resolve every cluster node.
                var allNodeIds = handles.Select(h => h.NodeId).ToArray();
                _ = await Task.WhenAll(handles.Select(h => h.CallAsync<AckReply>(new SiloCommand
                {
                    Command = "wait-resolved",
                    TargetNodeIds = allNodeIds,
                }))).ConfigureAwait(false);

                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static int ReserveFreePortBlock(int count)
        {
            // Find an unused port range using ephemeral listeners, then release.
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var startPort = Random.Shared.Next(20000, 55000);
                var sockets = new List<TcpListener>(count);
                var ok = true;
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var l = new TcpListener(IPAddress.Loopback, startPort + i);
                        l.Start();
                        sockets.Add(l);
                    }
                }
                catch
                {
                    ok = false;
                }
                finally
                {
                    foreach (var l in sockets)
                    {
                        l.Stop();
                    }
                }

                if (ok)
                {
                    return startPort;
                }
            }

            throw new InvalidOperationException("Could not reserve free loopback ports for silos.");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var silo in Silos)
            {
                await silo.ShutdownAndDisposeAsync().ConfigureAwait(false);
            }

            _listener.Stop();
            try
            {
                if (Directory.Exists(_rootDir))
                {
                    Directory.Delete(_rootDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class SiloHandle
    {
        private readonly Process _process;
        private readonly TcpClient _tcp;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _rpcLock = new(1, 1);
        private readonly StringBuilder _stderr;

        private SiloHandle(Process process, TcpClient tcp, StreamReader reader, StreamWriter writer, StringBuilder stderr, SiloHello hello)
        {
            _process = process;
            _tcp = tcp;
            _reader = reader;
            _writer = writer;
            _stderr = stderr;
            SiloName = hello.SiloName;
            NodeId = hello.NodeId;
            IsServerGc = hello.IsServerGc;
            ProcessorCount = hello.ProcessorCount;
            Pid = hello.Pid;
            SiloPort = hello.SiloPort;
        }

        public string SiloName { get; }
        public string NodeId { get; }
        public bool IsServerGc { get; }
        public int ProcessorCount { get; }
        public int Pid { get; }
        public int SiloPort { get; }

        public static async Task<SiloHandle> CreateAsync(Process process, TcpClient tcp, StringBuilder stderr, CancellationToken cancellationToken = default)
        {
            var stream = tcp.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var helloLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Silo disconnected before sending hello.");
            var hello = JsonSerializer.Deserialize<SiloHello>(helloLine, JsonOptions)
                ?? throw new InvalidOperationException("Invalid silo hello JSON.");
            return new SiloHandle(process, tcp, reader, writer, stderr, hello);
        }

        public async Task<TReply> CallAsync<TReply>(SiloCommand command, CancellationToken cancellationToken = default)
        {
            await _rpcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    string err;
                    lock (_stderr)
                    {
                        err = _stderr.ToString();
                    }

                    throw new InvalidOperationException($"Silo {SiloName} (PID {Pid}) closed connection during '{command.Command}'. Stderr:\n{err}");
                }

                var errCheck = JsonSerializer.Deserialize<ErrorReply>(line, JsonOptions);
                if (!string.IsNullOrEmpty(errCheck?.Error))
                {
                    throw new InvalidOperationException($"Silo {SiloName} failed '{command.Command}': {errCheck.Error}");
                }

                return JsonSerializer.Deserialize<TReply>(line, JsonOptions)
                    ?? throw new InvalidOperationException($"Failed to deserialize reply for '{command.Command}'.");
            }
            finally
            {
                _ = _rpcLock.Release();
            }
        }

        public async ValueTask ShutdownAndDisposeAsync()
        {
            Task? shutdownCall = null;
            try
            {
                if (!_process.HasExited)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try
                    {
                        shutdownCall = CallAsync<AckReply>(new SiloCommand { Command = "shutdown" }, cts.Token);
                        await shutdownCall.ConfigureAwait(false);
                        await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Force-kill below if graceful shutdown did not complete in time.
                    }
                }
            }
            finally
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                        _ = _process.WaitForExit(3000);
                    }
                }
                catch
                {
                    // Process already exited.
                }

                _writer.Dispose();
                _reader.Dispose();
                _tcp.Dispose();

                if (shutdownCall is not null && !shutdownCall.IsCompleted)
                {
                    try
                    {
                        await shutdownCall.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Wait for CallAsync to release _rpcLock after socket teardown before disposing it.
                    }
                }

                _rpcLock.Dispose();
                _process.Dispose();
            }
        }
    }

    // -------------------------------------------------------------------------
    // JSON Protocol DTOs
    // -------------------------------------------------------------------------
    private sealed class SiloLaunchConfig
    {
        public string SiloName { get; set; } = "";
        public Guid StorageClusterId { get; set; }
        public string OrleansClusterId { get; set; } = "";
        public string MetaStoreDir { get; set; } = "";
        public string ObjectStoreDir { get; set; } = "";
        public string SiloRoot { get; set; } = "";
        public int ControlPort { get; set; }
        public int PrimarySiloPort { get; set; }
        public int SiloPort { get; set; }
        public int GatewayPort { get; set; }
        public int ControlTransitions { get; set; }
        public int ProbeTimeoutMs { get; set; }
    }

    private sealed class SiloHello
    {
        public string SiloName { get; set; } = "";
        public string NodeId { get; set; } = "";
        public bool IsServerGc { get; set; }
        public int ProcessorCount { get; set; }
        public int Pid { get; set; }
        public int SiloPort { get; set; }
    }

    private sealed class SiloCommand
    {
        public string Command { get; set; } = "";
        public string? TargetNodeId { get; set; }
        public string[]? TargetNodeIds { get; set; }
        public string? JournalKey { get; set; }
        public string? WriteKeyPrefix { get; set; }
        public int PayloadBytes { get; set; }
        public int Count { get; set; }
        public int Ledgers { get; set; }
        public int Writers { get; set; }
        public double DurationSec { get; set; }
        public ulong LedgerStart { get; set; }
        public long TotalBytes { get; set; }
        public bool IncludeLatencies { get; set; }
    }

    private sealed class AckReply
    {
        public bool Ok { get; set; }
    }

    private sealed class ErrorReply
    {
        public string? Error { get; set; }
    }

    private sealed class OwnerInfoReply
    {
        public string PrimaryNodeId { get; set; } = "";
    }

    private sealed class LatencyReply
    {
        public double WriteP50Us { get; set; }
        public double WriteP99Us { get; set; }
        public double ReadP50Us { get; set; }
        public double ReadP99Us { get; set; }
    }

    private sealed class FanoutReply
    {
        public long TotalBytes { get; set; }
        public long BackpressureCount { get; set; }
        public double WriteP50Us { get; set; }
        public double WriteP99Us { get; set; }
        public double[]? LatenciesUs { get; set; }
    }

    private sealed class BurstReply
    {
        public long TotalBytes { get; set; }
        public long BackpressureCount { get; set; }
    }

    private sealed class ForwardedWriteReply
    {
        public long TotalBytes { get; set; }
        public double OpsPerSecond { get; set; }
        public double MiBPerSecond { get; set; }
        public double P50Us { get; set; }
        public double P99Us { get; set; }
    }

    private sealed class ForwardedReadReply
    {
        public long TotalBytes { get; set; }
        public int ReadCount { get; set; }
        public double MiBPerSecond { get; set; }
        public double P50Us { get; set; }
        public double P99Us { get; set; }
    }

    private sealed class ForwardedConcurrentReply
    {
        public long WriteTotalBytes { get; set; }
        public long ReadTotalBytes { get; set; }
        public double WriteP50Us { get; set; }
        public double WriteP99Us { get; set; }
        public double ReadP50Us { get; set; }
        public double ReadP99Us { get; set; }
        public int ConcurrentReadCount { get; set; }
    }

    private sealed class PingSample
    {
        public int TargetPort { get; set; }
        public double RttMs { get; set; }
    }

    private sealed class ProbeReply
    {
        public double ElapsedSec { get; set; }
        public long AllocatedBytes { get; set; }
        public long SohBytes { get; set; }
        public long LohBytes { get; set; }
        public long PohBytes { get; set; }
        public int Gen0 { get; set; }
        public int Gen1 { get; set; }
        public int Gen2 { get; set; }
        public string Gen2Causes { get; set; } = "";
        public double GcPausePercent { get; set; }
        public double CpuMs { get; set; }
        public double PeakWorkingSetMiB { get; set; }
        public long PeakReplicaHostBytes { get; set; }
        public long PeakForwardedReadBytes { get; set; }
        public PingSample[]? PingSamples { get; set; }
        public long PingsSentCount { get; set; }
        public long PingsReplyReceivedCount { get; set; }
        public long PingMissedCount { get; set; }
        public int SuspectCount { get; set; }
        public int NonActiveTransitions { get; set; }
    }
}

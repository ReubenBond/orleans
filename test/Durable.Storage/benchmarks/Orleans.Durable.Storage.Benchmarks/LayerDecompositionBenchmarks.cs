using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Portable;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Direct single-node, single-ledger and multi-ledger Clog layer-decomposition benchmark harness
/// (<c>--layer-bench</c>), isolating per-layer latency, throughput, allocation, GC pause, and CPU overhead
/// from L0 (raw WAL) through L1 (<see cref="LedgerStore"/>), L2 (quorum <see cref="LedgerWriter"/>),
/// and L3 (<see cref="JournalStore"/>) in both closed-loop and coordinated-omission-free open-loop modes.
/// </summary>
internal static class LayerDecompositionBenchmarks
{
    private static readonly NodeId[] ReplicaNodes =
    [
        JournalWriteBenchmarks.MakeNodeId(11),
        JournalWriteBenchmarks.MakeNodeId(12),
        JournalWriteBenchmarks.MakeNodeId(13),
    ];
    private static readonly NodeId PrimaryNode = ReplicaNodes[0];
    private static readonly Guid BenchmarkCluster = Guid.Parse("22222222-3333-4444-5555-666666666666");

    private static readonly int[] DefaultClosedPayloads = [128, 1024, 4096, 65536, 1048576];
    private static readonly int[] DefaultClosedConcurrencies = [1, 16, 64];
    private static readonly int[] DefaultOpenPayloads = [4096, 65536];
    private static readonly double[] DefaultOpenFractions = [0.10, 0.50, 0.75, 0.90];

    private sealed record LayerSpec(string Name, LayerKind Kind, int LedgerCount, bool RealWal, bool SingleHotJournal);

    private enum LayerKind
    {
        Wal,
        LedgerStore,
        QuorumLedger,
        JournalStore,
    }

    private sealed record ClosedLoopResult(
        string Layer,
        int PayloadBytes,
        int Concurrency,
        long Ops,
        double OpsPerSecond,
        double MiBPerSecond,
        double P50Us,
        double P95Us,
        double P99Us,
        double P999Us,
        double AllocBytesPerOp,
        double LohBytesPerOp,
        double GcPausePercent,
        double CpuUsPerOp,
        double CpuSecondsPerGiB,
        double PeakWorkingSetMiB);

    private sealed record OpenLoopResult(
        string Layer,
        int PayloadBytes,
        double TargetFraction,
        double TargetOpsPerSecond,
        double AchievedOpsPerSecond,
        long CompletedOps,
        long CappedDrops,
        double ResponseP50Us,
        double ResponseP95Us,
        double ResponseP99Us,
        double ResponseP999Us,
        double ServiceP50Us,
        double ServiceP99Us,
        double GcPausePercent,
        double CpuUsPerOp);

    private static readonly LayerSpec[] AllLayers =
    [
        new("L0-Wal", LayerKind.Wal, LedgerCount: 1, RealWal: true, SingleHotJournal: false),
        new("L1-Store-1L", LayerKind.LedgerStore, LedgerCount: 1, RealWal: true, SingleHotJournal: false),
        new("L1-Store-16L", LayerKind.LedgerStore, LedgerCount: 16, RealWal: true, SingleHotJournal: false),
        new("L1-Store-64L", LayerKind.LedgerStore, LedgerCount: 64, RealWal: true, SingleHotJournal: false),
        new("L2-QuorumMem-1L", LayerKind.QuorumLedger, LedgerCount: 1, RealWal: false, SingleHotJournal: false),
        new("L2-QuorumMem-16L", LayerKind.QuorumLedger, LedgerCount: 16, RealWal: false, SingleHotJournal: false),
        new("L2-QuorumWal-1L", LayerKind.QuorumLedger, LedgerCount: 1, RealWal: true, SingleHotJournal: false),
        new("L2-QuorumWal-16L", LayerKind.QuorumLedger, LedgerCount: 16, RealWal: true, SingleHotJournal: false),
        new("L3-JournalMem-1L-Hot", LayerKind.JournalStore, LedgerCount: 1, RealWal: false, SingleHotJournal: true),
        new("L3-JournalMem-1L-Many", LayerKind.JournalStore, LedgerCount: 1, RealWal: false, SingleHotJournal: false),
        new("L3-JournalMem-16L-Many", LayerKind.JournalStore, LedgerCount: 16, RealWal: false, SingleHotJournal: false),
        new("L3-JournalWal-1L-Hot", LayerKind.JournalStore, LedgerCount: 1, RealWal: true, SingleHotJournal: true),
        new("L3-JournalWal-1L-Many", LayerKind.JournalStore, LedgerCount: 1, RealWal: true, SingleHotJournal: false),
        new("L3-JournalWal-16L-Many", LayerKind.JournalStore, LedgerCount: 16, RealWal: true, SingleHotJournal: false),
    ];

    internal static async Task<int> RunAsync(string[] args)
    {
        var mode = "all";
        var layerFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closedPayloads = DefaultClosedPayloads;
        var closedConcurrencies = DefaultClosedConcurrencies;
        var openPayloads = DefaultOpenPayloads;
        var openFractions = DefaultOpenFractions;
        var closedDuration = TimeSpan.FromSeconds(4);
        var openDuration = TimeSpan.FromSeconds(5);
        var warmupDuration = TimeSpan.FromSeconds(1);
        var requireServerGc = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--mode":
                    mode = RequireOptionValue(args, ref i, arg);
                    break;
                case "--layers":
                    foreach (var part in RequireOptionValue(args, ref i, arg).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        _ = layerFilter.Add(part);
                    }

                    break;
                case "--payloads":
                    closedPayloads = ParsePositiveIntList(RequireOptionValue(args, ref i, arg), arg);
                    break;
                case "--open-payloads":
                    openPayloads = ParsePositiveIntList(RequireOptionValue(args, ref i, arg), arg);
                    break;
                case "--concurrencies":
                    closedConcurrencies = ParsePositiveIntList(RequireOptionValue(args, ref i, arg), arg);
                    break;
                case "--seconds":
                    closedDuration = TimeSpan.FromSeconds(ParsePositiveDouble(RequireOptionValue(args, ref i, arg), arg));
                    break;
                case "--open-seconds":
                    openDuration = TimeSpan.FromSeconds(ParsePositiveDouble(RequireOptionValue(args, ref i, arg), arg));
                    break;
                case "--warmup-seconds":
                    warmupDuration = TimeSpan.FromSeconds(ParsePositiveDouble(RequireOptionValue(args, ref i, arg), arg));
                    break;
                case "--require-server-gc":
                    requireServerGc = true;
                    break;
                case "--smoke":
                    closedPayloads = [4096];
                    closedConcurrencies = [1, 16];
                    openPayloads = [4096];
                    openFractions = [0.25, 0.75];
                    closedDuration = TimeSpan.FromSeconds(1);
                    openDuration = TimeSpan.FromSeconds(1.5);
                    warmupDuration = TimeSpan.FromMilliseconds(300);
                    break;
                default:
                    throw new ArgumentException($"Unknown --layer-bench option '{arg}'.");
            }
        }

        if (mode is not ("closed" or "open" or "all"))
        {
            throw new ArgumentException($"Invalid --mode '{mode}'. Expected 'closed', 'open', or 'all'.");
        }

        if (requireServerGc && !GCSettings.IsServerGC)
        {
            Console.Error.WriteLine("ERROR: --require-server-gc specified but GCSettings.IsServerGC is false. Launch with DOTNET_gcServer=1.");
            return 2;
        }

        var selectedLayers = layerFilter.Count == 0
            ? AllLayers
            : [.. AllLayers.Where(l => layerFilter.Any(f => l.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))];

        if (selectedLayers.Length == 0)
        {
            Console.Error.WriteLine($"No layers matched filter. Available: {string.Join(", ", AllLayers.Select(l => l.Name))}");
            return 1;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"# Clog Layer Decomposition Benchmarks (gc={(GCSettings.IsServerGC ? "Server" : "Workstation")}, cores={Environment.ProcessorCount}, mode={mode})"));
        Console.WriteLine();

        var closedResults = new List<ClosedLoopResult>();
        var openResults = new List<OpenLoopResult>();
        var saturationByLayerPayload = new Dictionary<(string Layer, int PayloadBytes), double>();

        if (mode is "closed" or "all")
        {
            Console.WriteLine("## Closed-Loop Results");
            Console.WriteLine("| Layer | Payload | Concurrency | Ops/s | MiB/s | p50 (us) | p95 (us) | p99 (us) | p99.9 (us) | Alloc B/op | LOH B/op | GC Pause % | CPU us/op | CPU s/GiB | WS Peak (MiB) |");
            Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

            foreach (var layer in selectedLayers)
            {
                foreach (var payload in closedPayloads)
                {
                    await using (var runner = await LayerRunner.CreateAsync(layer, payload).ConfigureAwait(false))
                    {
                        var bestOpsPerSec = 0.0;
                        foreach (var concurrency in closedConcurrencies)
                        {
                            var result = await RunClosedLoopAsync(runner, layer.Name, payload, concurrency, warmupDuration, closedDuration).ConfigureAwait(false);
                            closedResults.Add(result);
                            if (result.OpsPerSecond > bestOpsPerSec)
                            {
                                bestOpsPerSec = result.OpsPerSecond;
                            }

                            Console.WriteLine(string.Create(
                                CultureInfo.InvariantCulture,
                                $"| {result.Layer} | {BenchmarkHelpers.FormatSize(result.PayloadBytes)} | {result.Concurrency} | {result.OpsPerSecond:F1} | {result.MiBPerSecond:F1} | {result.P50Us:F1} | {result.P95Us:F1} | {result.P99Us:F1} | {result.P999Us:F1} | {result.AllocBytesPerOp:F0} | {result.LohBytesPerOp:F0} | {result.GcPausePercent:F2}% | {result.CpuUsPerOp:F1} | {result.CpuSecondsPerGiB:F2} | {result.PeakWorkingSetMiB:F1} |"));
                        }

                        saturationByLayerPayload[(layer.Name, payload)] = bestOpsPerSec;
                    }

                    ReclaimBetweenRuns();
                }
            }

            Console.WriteLine();
        }

        if (mode is "open" or "all")
        {
            Console.WriteLine("## Open-Loop Results (Coordinated-Omission-Free Poisson Arrivals)");
            Console.WriteLine("| Layer | Payload | Load % | Target ops/s | Achieved ops/s | Completed | Drops | Status | Resp p50 (us) | Resp p95 (us) | Resp p99 (us) | Resp p99.9 (us) | Svc p50 (us) | Svc p99 (us) | GC Pause % | CPU us/op |");
            Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|");

            foreach (var layer in selectedLayers)
            {
                foreach (var payload in openPayloads)
                {
                    await RunOpenLoopSuiteForLayerAsync(
                        layer,
                        payload,
                        openFractions,
                        warmupDuration,
                        openDuration,
                        saturationByLayerPayload,
                        openResults).ConfigureAwait(false);
                }
            }

            Console.WriteLine();
        }

        if (closedResults.Count > 0)
        {
            PrintLayerOverheadSummary(closedResults);
        }

        return 0;
    }

    private static void PrintLayerOverheadSummary(List<ClosedLoopResult> closedResults)
    {
        Console.WriteLine("## Layer-by-Layer Overhead Summary (Incremental Deltas vs L0-Wal, Single-Ledger & Multi-Ledger)");
        foreach (var payload in closedResults.Select(r => r.PayloadBytes).Distinct().Order())
        {
            var l0C1 = closedResults.FirstOrDefault(r => r.Layer == "L0-Wal" && r.PayloadBytes == payload && r.Concurrency == 1);
            var maxC = closedResults.Where(r => r.PayloadBytes == payload).Max(r => r.Concurrency);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"### Payload {BenchmarkHelpers.FormatSize(payload)}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| Layer | c=1 p50 (us) | Delta c=1 p50 (us) | c={maxC} ops/s | c={maxC} MiB/s | c={maxC} p99 (us) | c={maxC} Alloc B/op | c={maxC} CPU us/op |"));
            Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var layerName in closedResults.Where(r => r.PayloadBytes == payload).Select(r => r.Layer).Distinct())
            {
                var c1 = closedResults.FirstOrDefault(r => r.Layer == layerName && r.PayloadBytes == payload && r.Concurrency == 1);
                var cMax = closedResults.FirstOrDefault(r => r.Layer == layerName && r.PayloadBytes == payload && r.Concurrency == maxC);
                var c1P50 = c1?.P50Us ?? 0;
                var deltaP50 = l0C1 is not null && c1 is not null ? c1.P50Us - l0C1.P50Us : 0;
                var cMaxOps = cMax?.OpsPerSecond ?? 0;
                var cMaxMiB = cMax?.MiBPerSecond ?? 0;
                var cMaxP99 = cMax?.P99Us ?? 0;
                var cMaxAlloc = cMax?.AllocBytesPerOp ?? 0;
                var cMaxCpu = cMax?.CpuUsPerOp ?? 0;

                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {layerName} | {c1P50:F1} | {deltaP50:+0.0;-0.0;0.0} | {cMaxOps:F1} | {cMaxMiB:F1} | {cMaxP99:F1} | {cMaxAlloc:F0} | {cMaxCpu:F1} |"));
            }

            Console.WriteLine();
        }
    }

    private static async Task RunOpenLoopSuiteForLayerAsync(
        LayerSpec layer,
        int payload,
        double[] openFractions,
        TimeSpan warmupDuration,
        TimeSpan openDuration,
        Dictionary<(string, int), double> saturationByLayerPayload,
        List<OpenLoopResult> openResults)
    {
        var runner = await LayerRunner.CreateAsync(layer, payload).ConfigureAwait(false);
        await using (runner.ConfigureAwait(false))
        {
            if (!saturationByLayerPayload.TryGetValue((layer.Name, payload), out var satOpsPerSec) || satOpsPerSec <= 0)
            {
                // Establish saturation rate via max(c=16, c=64) so layers peaking at c=16 are not underestimated.
                var probe16 = await RunClosedLoopAsync(runner, layer.Name, payload, concurrency: 16, warmupDuration, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var probe64 = await RunClosedLoopAsync(runner, layer.Name, payload, concurrency: 64, warmupDuration, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                satOpsPerSec = Math.Max(probe16.OpsPerSecond, probe64.OpsPerSecond);
                saturationByLayerPayload[(layer.Name, payload)] = satOpsPerSec;
            }

            foreach (var fraction in openFractions)
            {
                var targetRate = Math.Max(1.0, satOpsPerSec * fraction);
                var result = await RunOpenLoopAsync(runner, layer.Name, payload, fraction, targetRate, warmupDuration, openDuration).ConfigureAwait(false);
                openResults.Add(result);
                var status = result.CappedDrops > 0 ? "OVERLOADED" : "ok";

                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {result.Layer} | {BenchmarkHelpers.FormatSize(result.PayloadBytes)} | {result.TargetFraction * 100:F0}% | {result.TargetOpsPerSecond:F1} | {result.AchievedOpsPerSecond:F1} | {result.CompletedOps} | {result.CappedDrops} | {status} | {result.ResponseP50Us:F1} | {result.ResponseP95Us:F1} | {result.ResponseP99Us:F1} | {result.ResponseP999Us:F1} | {result.ServiceP50Us:F1} | {result.ServiceP99Us:F1} | {result.GcPausePercent:F2}% | {result.CpuUsPerOp:F1} |"));
            }
        }

        ReclaimBetweenRuns();
    }

    private static void ReclaimBetweenRuns()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static async Task<ClosedLoopResult> RunClosedLoopAsync(
        LayerRunner runner,
        string layerName,
        int payloadBytes,
        int concurrency,
        TimeSpan warmupDuration,
        TimeSpan measureDuration)
    {
        // Warmup phase
        using (var warmupCts = new CancellationTokenSource(warmupDuration))
        {
            var warmupWorkers = new Task[concurrency];
            for (var w = 0; w < concurrency; w++)
            {
                var workerId = w;
                warmupWorkers[w] = Task.Run(async () =>
                {
                    while (!warmupCts.IsCancellationRequested)
                    {
                        await runner.WriteOneAsync(workerId).ConfigureAwait(false);
                    }
                });
            }

            await Task.WhenAll(warmupWorkers).ConfigureAwait(false);
        }

        // Pre-size per-worker latency lists to avoid mid-run LOH array growth.
        var samplesPerWorker = new List<double>[concurrency];
        for (var w = 0; w < concurrency; w++)
        {
            List<double> list = [];
            _ = list.EnsureCapacity(8192);
            samplesPerWorker[w] = list;
        }

        var stopTimestamp = Stopwatch.GetTimestamp() + (long)(measureDuration.TotalSeconds * Stopwatch.Frequency);
        using var probe = BenchmarkProbe.Start();
        using var watchCts = new CancellationTokenSource();
        var watchTask = probe.WatchAsync(watchCts.Token);
        try
        {
            var workers = new Task[concurrency];
            for (var w = 0; w < concurrency; w++)
            {
                var workerId = w;
                var workerSamples = samplesPerWorker[w];
                workers[w] = Task.Run(async () =>
                {
                    while (Stopwatch.GetTimestamp() < stopTimestamp)
                    {
                        var t0 = Stopwatch.GetTimestamp();
                        await runner.WriteOneAsync(workerId).ConfigureAwait(false);
                        var elapsedUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
                        workerSamples.Add(elapsedUs);
                    }
                });
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            await watchCts.CancelAsync().ConfigureAwait(false);
            await watchTask.ConfigureAwait(false);
        }

        long totalOps = 0;
        foreach (var list in samplesPerWorker)
        {
            totalOps += list.Count;
        }

        var totalBytes = totalOps * payloadBytes;
        var summary = probe.Stop(totalBytes);
        var allSamples = new double[totalOps];
        var offset = 0;
        foreach (var list in samplesPerWorker)
        {
            list.CopyTo(allSamples, offset);
            offset += list.Count;
        }

        Array.Sort(allSamples);
        var elapsedSec = summary.Elapsed.TotalSeconds;
        var opsPerSec = totalOps / Math.Max(elapsedSec, 1e-9);
        var mibPerSec = totalBytes / (1024.0 * 1024.0) / Math.Max(elapsedSec, 1e-9);
        var allocPerOp = totalOps > 0 ? (double)summary.AllocatedBytes / totalOps : 0;
        var lohPerOp = totalOps > 0 ? (double)summary.SampledLohBytes / totalOps : 0;
        var cpuUsPerOp = totalOps > 0 ? summary.CpuTime.TotalMicroseconds / totalOps : 0;

        return new ClosedLoopResult(
            layerName,
            payloadBytes,
            concurrency,
            totalOps,
            opsPerSec,
            mibPerSec,
            BenchmarkHelpers.Percentile(allSamples, 0.50),
            BenchmarkHelpers.Percentile(allSamples, 0.95),
            BenchmarkHelpers.Percentile(allSamples, 0.99),
            BenchmarkHelpers.Percentile(allSamples, 0.999),
            allocPerOp,
            lohPerOp,
            summary.GcPausePercent,
            cpuUsPerOp,
            summary.CpuSecondsPerGiB,
            summary.PeakWorkingSetMiB);
    }

    private sealed class OpenLoopContext
    {
        private const int Shards = 32;
        private readonly Lock[] _locks = new Lock[Shards];
        private readonly List<double>[] _respShards = new List<double>[Shards];
        private readonly List<double>[] _svcShards = new List<double>[Shards];
        private readonly LayerRunner _runner;
        private readonly CountdownEvent _allDone;
        private readonly double _ticksPerSecond;
        private int _inFlight;
        private long _completedOps;
        private Exception? _firstError;

        public OpenLoopContext(LayerRunner runner, CountdownEvent allDone, double ticksPerSecond, int expectedTotalOps)
        {
            _runner = runner;
            _allDone = allDone;
            _ticksPerSecond = ticksPerSecond;
            var perShard = Math.Clamp(expectedTotalOps / Shards + 256, 256, 65536);
            for (var i = 0; i < Shards; i++)
            {
                _locks[i] = new Lock();
                List<double> resp = [];
                _ = resp.EnsureCapacity(perShard);
                List<double> svc = [];
                _ = svc.EnsureCapacity(perShard);
                _respShards[i] = resp;
                _svcShards[i] = svc;
            }
        }

        public Exception? FirstError => Volatile.Read(ref _firstError);
        public long CompletedOps => Volatile.Read(ref _completedOps);

        public bool TryEnterInFlight(int maxInFlight)
        {
            if (Interlocked.Increment(ref _inFlight) > maxInFlight)
            {
                _ = Interlocked.Decrement(ref _inFlight);
                return false;
            }

            return true;
        }

        public async Task ExecuteAsync(int workerSlot, long scheduledTick, long dispatchTick)
        {
            try
            {
                await _runner.WriteOneAsync(workerSlot).ConfigureAwait(false);
                var doneTick = Stopwatch.GetTimestamp();
                var responseUs = (doneTick - scheduledTick) * 1_000_000.0 / _ticksPerSecond;
                var serviceUs = (doneTick - dispatchTick) * 1_000_000.0 / _ticksPerSecond;
                var shard = workerSlot & (Shards - 1);
                lock (_locks[shard])
                {
                    _respShards[shard].Add(responseUs);
                    _svcShards[shard].Add(serviceUs);
                }

                _ = Interlocked.Increment(ref _completedOps);
            }
            catch (Exception ex)
            {
                _ = Interlocked.CompareExchange(ref _firstError, ex, null);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _inFlight);
                _ = _allDone.Signal();
            }
        }

        public (double[] ResponseUs, double[] ServiceUs) CollectSorted()
        {
            var total = 0;
            for (var i = 0; i < Shards; i++)
            {
                total += _respShards[i].Count;
            }

            var resp = new double[total];
            var svc = new double[total];
            var offset = 0;
            for (var i = 0; i < Shards; i++)
            {
                _respShards[i].CopyTo(resp, offset);
                _svcShards[i].CopyTo(svc, offset);
                offset += _respShards[i].Count;
            }

            Array.Sort(resp);
            Array.Sort(svc);
            return (resp, svc);
        }
    }

    private static async Task<OpenLoopResult> RunOpenLoopAsync(
        LayerRunner runner,
        string layerName,
        int payloadBytes,
        double targetFraction,
        double targetOpsPerSecond,
        TimeSpan warmupDuration,
        TimeSpan measureDuration)
    {
        const int MaxInFlight = 4096;

        // Short closed-loop warmup to heat caches and JIT before open-loop pacing.
        using (var warmCts = new CancellationTokenSource(warmupDuration))
        {
            var warmTasks = new Task[8];
            for (var i = 0; i < warmTasks.Length; i++)
            {
                var workerId = i;
                warmTasks[i] = Task.Run(async () =>
                {
                    while (!warmCts.IsCancellationRequested)
                    {
                        await runner.WriteOneAsync(workerId).ConfigureAwait(false);
                    }
                });
            }

            await Task.WhenAll(warmTasks).ConfigureAwait(false);
        }

        long cappedDrops = 0;
        var startTick = Stopwatch.GetTimestamp();
        var endTick = startTick + (long)(measureDuration.TotalSeconds * Stopwatch.Frequency);
        var ticksPerSecond = (double)Stopwatch.Frequency;
        var rng = new Random(0x5EED_CAFE ^ payloadBytes ^ (int)(targetFraction * 1000));
        var expectedOps = (int)Math.Min(int.MaxValue / 4, Math.Ceiling(targetOpsPerSecond * measureDuration.TotalSeconds * 1.1));

        using var allDone = new CountdownEvent(1);
        var ctx = new OpenLoopContext(runner, allDone, ticksPerSecond, expectedOps);
        using var probe = BenchmarkProbe.Start();
        using var watchCts = new CancellationTokenSource();
        var watchTask = probe.WatchAsync(watchCts.Token);

        // Dedicated pacing thread avoids ThreadPool starvation skewing scheduled timestamps.
        var pacerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pacerThread = new Thread(() =>
        {
            try
            {
                var nextScheduledTick = (double)startTick;
                var opIndex = 0;
                while (ctx.FirstError is null)
                {
                    // Exponential inter-arrival (Poisson process, fixed seed).
                    var u = Math.Max(1e-12, 1.0 - rng.NextDouble());
                    var interArrivalSec = -Math.Log(u) / targetOpsPerSecond;
                    nextScheduledTick += interArrivalSec * ticksPerSecond;
                    var scheduledTick = (long)nextScheduledTick;
                    if (scheduledTick >= endTick)
                    {
                        break;
                    }

                    // Wait until scheduled arrival time.
                    var spin = default(SpinWait);
                    while (ctx.FirstError is null)
                    {
                        var now = Stopwatch.GetTimestamp();
                        var remainingTicks = scheduledTick - now;
                        if (remainingTicks <= 0)
                        {
                            break;
                        }

                        var remainingMs = remainingTicks * 1000.0 / ticksPerSecond;
                        if (remainingMs > 2.0)
                        {
                            _ = Thread.Yield();
                        }
                        else
                        {
                            spin.SpinOnce(sleep1Threshold: -1);
                        }
                    }

                    if (ctx.FirstError is not null)
                    {
                        break;
                    }

                    if (!ctx.TryEnterInFlight(MaxInFlight))
                    {
                        _ = Interlocked.Increment(ref cappedDrops);
                        continue;
                    }

                    var workerSlot = opIndex++;
                    allDone.AddCount();
                    _ = ThreadPool.UnsafeQueueUserWorkItem(
                        static state =>
                        {
                            var dispatchTick = Stopwatch.GetTimestamp();
                            _ = state.Ctx.ExecuteAsync(state.Slot, state.ScheduledTick, dispatchTick);
                        },
                        (Ctx: ctx, Slot: workerSlot, ScheduledTick: scheduledTick),
                        preferLocal: false);
                }

                pacerDone.SetResult();
            }
            catch (Exception ex)
            {
                pacerDone.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "OpenLoopPacer",
        };

        pacerThread.Start();
        Exception? pacerException = null;
        try
        {
            await pacerDone.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            pacerException = ex;
        }
        finally
        {
            _ = allDone.Signal();
            await Task.Run(allDone.Wait).ConfigureAwait(false);
            await watchCts.CancelAsync().ConfigureAwait(false);
            await watchTask.ConfigureAwait(false);
        }

        if (pacerException is not null)
        {
            ExceptionDispatchInfo.Throw(pacerException);
        }

        if (ctx.FirstError is { } workerException)
        {
            ExceptionDispatchInfo.Throw(workerException);
        }

        var completed = ctx.CompletedOps;
        var totalBytes = completed * payloadBytes;
        var summary = probe.Stop(totalBytes);
        var (respArray, svcArray) = ctx.CollectSorted();
        var achievedRate = completed / Math.Max(summary.Elapsed.TotalSeconds, 1e-9);
        var cpuUsPerOp = completed > 0 ? summary.CpuTime.TotalMicroseconds / completed : 0;

        return new OpenLoopResult(
            layerName,
            payloadBytes,
            targetFraction,
            targetOpsPerSecond,
            achievedRate,
            completed,
            Volatile.Read(ref cappedDrops),
            BenchmarkHelpers.Percentile(respArray, 0.50),
            BenchmarkHelpers.Percentile(respArray, 0.95),
            BenchmarkHelpers.Percentile(respArray, 0.99),
            BenchmarkHelpers.Percentile(respArray, 0.999),
            BenchmarkHelpers.Percentile(svcArray, 0.50),
            BenchmarkHelpers.Percentile(svcArray, 0.99),
            summary.GcPausePercent,
            cpuUsPerOp);
    }

    private static string RequireOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for option '{option}'.");
        }

        return args[++index];
    }

    private static int[] ParsePositiveIntList(string value, string option)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException($"Option '{option}' requires at least one positive integer.");
        }

        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                throw new ArgumentException($"Option '{option}' received invalid positive integer '{parts[i]}'.");
            }

            result[i] = parsed;
        }

        return result;
    }

    private static double ParsePositiveDouble(string value, string option)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0 || !double.IsFinite(parsed))
        {
            throw new ArgumentException($"Option '{option}' received invalid positive number '{value}'.");
        }

        return parsed;
    }

    private sealed class LayerRunner : IAsyncDisposable
    {
        private readonly LayerSpec _spec;
        private readonly int _payloadBytes;
        private readonly string _rootDir;
        private readonly byte[] _rawPayload;
        private readonly ArcBuffer _arcPayload;

        // L0: Raw WAL
        private WriteAheadLog? _wal;

        // L1: Single-node LedgerStore
        private LedgerStore? _ledgerStore;
        private ILedgerReplica[]? _storeReplicas;
        private long[]? _storeNextSlots;

        // L2 & L3: Quorum Ledger + JournalStore
        private LocalQuorumReplicaTransport? _walTransport;
        private NullDiskLedgerReplicaTransport? _memTransport;
        private LedgerMapCommitter? _committer;
        private LedgerReader? _reader;
        private LedgerWriterRegistry? _writers;
        private LedgerWriter[]? _ledgerWriters;
        private SemaphoreSlim[]? _ledgerBatchLocks;
        private byte[][][]? _ledgerKeys;
        private RowVersion[][]? _ledgerKeyVersions;
        private JournalStore? _journalStore;
        private InMemoryArchiveStore? _journalArchive;
        private JournalRowKey[]? _journalKeys;
        private JournalVersion[]? _journalVersions;
        private SemaphoreSlim[]? _journalKeyLocks;
        private long _roundRobin;

        private LayerRunner(LayerSpec spec, int payloadBytes, string rootDir)
        {
            _spec = spec;
            _payloadBytes = payloadBytes;
            _rootDir = rootDir;
            _rawPayload = new byte[payloadBytes];
            new Random(0x1C06_0001 ^ payloadBytes).NextBytes(_rawPayload);
            using var writer = new ArcBufferWriter(payloadBytes);
            writer.Write(_rawPayload);
            _arcPayload = writer.PeekSlice(writer.Length);
        }

        internal static async Task<LayerRunner> CreateAsync(LayerSpec spec, int payloadBytes)
        {
            var rootDir = Path.Combine(
                Path.GetTempPath(),
                "clog-layer-bench-" + spec.Name + "-" + Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(rootDir);
            var runner = new LayerRunner(spec, payloadBytes, rootDir);
            try
            {
                await runner.InitializeAsync().ConfigureAwait(false);
                return runner;
            }
            catch
            {
                await runner.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task InitializeAsync()
        {
            const long WalFileSize = 256L * 1024 * 1024;

            switch (_spec.Kind)
            {
                case LayerKind.Wal:
                    {
                        var walDir = Path.Combine(_rootDir, "wal");
                        _ = Directory.CreateDirectory(walDir);
                        var options = new WalOptions
                        {
                            TimeProvider = TimeProvider.System,
                            FileSize = WalFileSize,
                            PreallocateFiles = false,
                            PageBufferBudgetBytes = 64L * 1024 * 1024,
                        };
                        _wal = await WriteAheadLog.CreateAsync(new PortableWalStorage(), walDir, options).ConfigureAwait(false);
                        break;
                    }

                case LayerKind.LedgerStore:
                    {
                        var storeDir = Path.Combine(_rootDir, "store");
                        _ = Directory.CreateDirectory(storeDir);
                        var storeOptions = new LedgerStoreOptions
                        {
                            DirectoryPath = storeDir,
                            FileSize = WalFileSize,
                            PreallocateFiles = false,
                            PageBufferBudgetBytes = 64L * 1024 * 1024,
                        };
                        _ledgerStore = await LedgerStore.OpenAsync(
                            new PortableWalStorage(),
                            storeOptions,
                            BenchmarkCluster,
                            create: true,
                            new LedgerStoreCheckpointOptions(),
                            TimeProvider.System).ConfigureAwait(false);
                        _storeReplicas = new ILedgerReplica[_spec.LedgerCount];
                        _storeNextSlots = new long[_spec.LedgerCount];
                        var window = new LedgerReplicaOptions { WindowSize = 64 };
                        for (var i = 0; i < _spec.LedgerCount; i++)
                        {
                            var ledgerId = new LedgerId((ulong)(i + 1));
                            _ = await _ledgerStore.CreateLiveAsync(ledgerId, window.WindowSize, CancellationToken.None).ConfigureAwait(false);
                            _storeReplicas[i] = _ledgerStore.OpenReplica(ledgerId, window);
                        }

                        break;
                    }

                case LayerKind.QuorumLedger:
                case LayerKind.JournalStore:
                    {
                        var register = new InMemoryConditionalRegister();
                        var mapView = await LedgerMapView.OpenAsync(register, CancellationToken.None).ConfigureAwait(false);
                        _committer = new LedgerMapCommitter(register, int.MaxValue);

                        ILedgerReplicaTransport transport;
                        if (_spec.RealWal)
                        {
                            _walTransport = await LocalQuorumReplicaTransport.OpenAsync(
                                _rootDir,
                                ReplicaNodes,
                                walFileSize: WalFileSize).ConfigureAwait(false);
                            transport = _walTransport;
                        }
                        else
                        {
                            _memTransport = new NullDiskLedgerReplicaTransport();
                            transport = _memTransport;
                        }

                        _reader = new LedgerReader(mapView, transport, TimeProvider.System, PrimaryNode);

                        // Allow 1 MiB user payloads plus batch/journal framing.
                        var maxBatchLength = Math.Max(1 << 21, _payloadBytes + (1 << 16));
                        var writerOptions = new LedgerWriterOptions(
                            maxSegmentBytes: 1L << 30,
                            maxSegmentKeys: int.MaxValue,
                            maxSegmentAge: TimeSpan.FromHours(1),
                            maxProposalBytes: Math.Max(64L << 20, (long)maxBatchLength * 32));
                        _writers = new LedgerWriterRegistry(
                            _reader,
                            maxBatchLength,
                            writerOptions,
                            TimeProvider.System,
                            NullLoggerFactory.Instance);
                        var executor = new TransitionExecutor(_writers, NullLogger<TransitionExecutor>.Instance);
                        var location = new LedgerLocation.Nodes([.. ReplicaNodes]);

                        // Bootstrap ledger 1 over KeyRange.Root, then split until we have _spec.LedgerCount open ledgers.
                        var bootChoice = new TransitionChoice(PrimaryNode, location, 64, 0, mapView.Current.Epoch);
                        var boot = await executor.ExecuteAsync(new Transition.Bootstrap(bootChoice), _committer.CommitAsync, CancellationToken.None).ConfigureAwait(false);
                        if (boot.Value is not LedgerMapCommitOutcome.Applied)
                        {
                            throw new InvalidOperationException($"Bootstrap failed: {boot.Value}");
                        }

                        _ = await mapView.ReadAsync(CancellationToken.None).ConfigureAwait(false);

                        while (mapView.Current.Ledgers.Count(static l => l.Status.Value is LedgerStatus.Open) < _spec.LedgerCount)
                        {
                            var openLedgers = mapView.Current.Ledgers.Where(static l => l.Status.Value is LedgerStatus.Open).ToArray();
                            foreach (var open in openLedgers)
                            {
                                if (mapView.Current.Ledgers.Count(static l => l.Status.Value is LedgerStatus.Open) >= _spec.LedgerCount)
                                {
                                    break;
                                }

                                var splitChoice = new TransitionChoice(PrimaryNode, location, 64, 0, mapView.Current.Epoch);
                                var split = await executor.ExecuteAsync(new Transition.Split(open.Id, splitChoice), _committer.CommitAsync, CancellationToken.None).ConfigureAwait(false);
                                if (split.Value is not LedgerMapCommitOutcome.Applied)
                                {
                                    throw new InvalidOperationException($"Split failed: {split.Value}");
                                }

                                _ = await mapView.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                            }
                        }

                        await _writers.ReconciledAsync(mapView.Current.Epoch, CancellationToken.None).ConfigureAwait(false);

                        var finalOpen = mapView.Current.Ledgers.Where(static l => l.Status.Value is LedgerStatus.Open).ToArray();
                        if (finalOpen.Length != _spec.LedgerCount)
                        {
                            throw new InvalidOperationException($"Expected {_spec.LedgerCount} open ledgers, got {finalOpen.Length}.");
                        }

                        var writerList = new LedgerWriter[_spec.LedgerCount];
                        for (var i = 0; i < _spec.LedgerCount; i++)
                        {
                            writerList[i] = _writers.TryGet(finalOpen[i].Id)
                                ?? throw new InvalidOperationException($"Writer for {finalOpen[i].Id} not registered.");
                        }

                        _ledgerWriters = writerList;

                        if (_spec.Kind == LayerKind.QuorumLedger)
                        {
                            _ledgerBatchLocks = new SemaphoreSlim[_spec.LedgerCount];
                            _ledgerKeys = new byte[_spec.LedgerCount][][];
                            _ledgerKeyVersions = new RowVersion[_spec.LedgerCount][];
                            const int KeysPerLedger = 64;
                            for (var l = 0; l < _spec.LedgerCount; l++)
                            {
                                _ledgerBatchLocks[l] = new SemaphoreSlim(1, 1);
                                _ledgerKeys[l] = GenerateKeysInRange(finalOpen[l].Range, KeysPerLedger);
                                _ledgerKeyVersions[l] = new RowVersion[KeysPerLedger];
                                for (var k = 0; k < KeysPerLedger; k++)
                                {
                                    _ledgerKeyVersions[l][k] = default;
                                }
                            }
                        }
                        else
                        {
                            var journalOptions = new JournalStoreOptions
                            {
                                MaxBodyBytes = Math.Max(1 << 20, _payloadBytes),
                                WarmStateBytes = 256L << 20,
                            };
                            _journalArchive = new InMemoryArchiveStore();
                            _journalStore = new JournalStore(_writers, _journalArchive, journalOptions, TimeProvider.System);

                            if (_spec.SingleHotJournal)
                            {
                                _journalKeys = [JournalRowKey.Encode("layer-hot-0")];
                                _journalVersions = [JournalVersion.Origin];
                                _journalKeyLocks = new SemaphoreSlim[1];
                                _journalKeyLocks[0] = new SemaphoreSlim(1, 1);
                            }
                            else
                            {
                                var journalsPerLedger = _spec.LedgerCount > 1 ? 8 : 64;
                                var totalKeys = _spec.LedgerCount * journalsPerLedger;
                                _journalKeys = new JournalRowKey[totalKeys];
                                _journalVersions = new JournalVersion[totalKeys];
                                _journalKeyLocks = new SemaphoreSlim[totalKeys];
                                var perLedger = new JournalRowKey[_spec.LedgerCount][];
                                for (var l = 0; l < _spec.LedgerCount; l++)
                                {
                                    perLedger[l] = GenerateJournalKeysInRange(finalOpen[l].Range, journalsPerLedger, l);
                                }

                                // Stripe keys across ledgers so consecutive worker IDs spread evenly across all open ledgers.
                                var cursor = 0;
                                for (var j = 0; j < journalsPerLedger; j++)
                                {
                                    for (var l = 0; l < _spec.LedgerCount; l++)
                                    {
                                        _journalKeys[cursor] = perLedger[l][j];
                                        _journalVersions[cursor] = JournalVersion.Origin;
                                        _journalKeyLocks[cursor] = new SemaphoreSlim(1, 1);
                                        cursor++;
                                    }
                                }
                            }

                            // Pre-warm every journal key once so measurement captures steady-state warm writes, not first-touch scans.
                            var warmTasks = new Task[_journalKeys.Length];
                            for (var k = 0; k < _journalKeys.Length; k++)
                            {
                                var idx = k;
                                warmTasks[k] = Task.Run(async () => await WriteOneAsync(idx).ConfigureAwait(false));
                            }

                            await Task.WhenAll(warmTasks).ConfigureAwait(false);
                        }

                        break;
                    }

                default:
                    throw new InvalidOperationException($"Unknown layer kind: {_spec.Kind}");
            }
        }

        internal async ValueTask WriteOneAsync(int workerId)
        {
            switch (_spec.Kind)
            {
                case LayerKind.Wal:
                    _ = await _wal!.AppendAsync(_arcPayload).ConfigureAwait(false);
                    break;

                case LayerKind.LedgerStore:
                    {
                        var ledgerIdx = (int)((ulong)Interlocked.Increment(ref _roundRobin) % (ulong)_spec.LedgerCount);
                        var slot = Interlocked.Increment(ref _storeNextSlots![ledgerIdx]) - 1;
                        var outcome = await _storeReplicas![ledgerIdx]
                            .WriteAsync(new Slot(slot), _arcPayload, Slot.Zero, recovery: false)
                            .ConfigureAwait(false);
                        if (outcome.Value is not ReplicaWriteOutcome.Written)
                        {
                            throw new InvalidOperationException($"LedgerStore write failed: {outcome.Value}");
                        }

                        break;
                    }

                case LayerKind.QuorumLedger:
                    {
                        var seq = (ulong)Interlocked.Increment(ref _roundRobin);
                        var ledgerIdx = (int)(seq % (ulong)_spec.LedgerCount);
                        var keyIdx = (int)(seq / (ulong)_spec.LedgerCount % 64UL);
                        var writer = _ledgerWriters![ledgerIdx];
                        var batchLock = _ledgerBatchLocks![ledgerIdx];
                        var key = _ledgerKeys![ledgerIdx][keyIdx];
                        var maxLen = (int)(LedgerBatch.MaxHeaderLength(1) + LedgerBatch.MaxRowLength(key.Length, 0, RowToken.MaxLength, _payloadBytes) + 64);

                        LedgerBatch batch;
                        await batchLock.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            batch = await writer.CreateBatchAsync(rowCount: 1, maxLength: maxLen).ConfigureAwait(false);
                            try
                            {
                                var versionBefore = _ledgerKeyVersions![ledgerIdx][keyIdx];
                                var nextVersion = batch.BeginRow(
                                    key,
                                    RowKind.Replace,
                                    versionBefore,
                                    default,
                                    _payloadBytes,
                                    _rawPayload);
                                _ledgerKeyVersions[ledgerIdx][keyIdx] = nextVersion;
                            }
                            catch
                            {
                                batch.Dispose();
                                throw;
                            }
                        }
                        finally
                        {
                            _ = batchLock.Release();
                        }

                        using (batch)
                        {
                            var outcome = await writer.ProposeAsync(batch).ConfigureAwait(false);
                            if (!outcome.TryGetValue(out ProposalOutcome.Durable _))
                            {
                                throw new InvalidOperationException($"LedgerWriter proposal failed: {outcome}");
                            }
                        }

                        break;
                    }

                case LayerKind.JournalStore:
                    {
                        var keyIdx = _spec.SingleHotJournal
                            ? 0
                            : Math.Abs(workerId) % _journalKeys!.Length;
                        var key = _journalKeys![keyIdx];
                        var keyLock = _journalKeyLocks![keyIdx];

                        // Serialize expected-version handoff per journal key while allowing full concurrency across keys.
                        await keyLock.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var expected = _journalVersions![keyIdx];
                            using var record = _journalStore!.EncodeReplace(key, expected, _arcPayload);
                            var outcome = await _journalStore.WriteAsync(record).ConfigureAwait(false);
                            if (outcome.Value is not JournalWriteOutcome.Accepted accepted)
                            {
                                throw new InvalidOperationException($"JournalStore write failed under key lock: {outcome.Value}");
                            }

                            _journalVersions[keyIdx] = accepted.Version;
                        }
                        finally
                        {
                            _ = keyLock.Release();
                        }

                        break;
                    }

                default:
                    throw new InvalidOperationException($"Unknown layer kind: {_spec.Kind}");
            }
        }

        private static byte[][] GenerateKeysInRange(KeyRange range, int count)
        {
            var keys = new byte[count][];
            var found = 0;
            var candidate = 0;
            while (found < count)
            {
                var rk = JournalRowKey.Encode(string.Create(CultureInfo.InvariantCulture, $"l2-k-{candidate++}"));
                if (range.Contains(rk.Span))
                {
                    keys[found++] = rk.Span.ToArray();
                }
            }

            return keys;
        }

        private static JournalRowKey[] GenerateJournalKeysInRange(KeyRange range, int count, int ledgerSeed)
        {
            var keys = new JournalRowKey[count];
            var found = 0;
            var candidate = 0;
            while (found < count)
            {
                var rk = JournalRowKey.Encode(string.Create(CultureInfo.InvariantCulture, $"l3-{ledgerSeed}-k-{candidate++}"));
                if (range.Contains(rk.Span))
                {
                    keys[found++] = rk;
                }
            }

            return keys;
        }

        public async ValueTask DisposeAsync()
        {
            if (_journalStore is not null)
            {
                await _journalStore.DisposeAsync().ConfigureAwait(false);
            }

            _journalArchive?.Dispose();

            if (_writers is not null)
            {
                await _writers.DisposeAsync().ConfigureAwait(false);
            }

            if (_reader is not null)
            {
                await _reader.DisposeAsync().ConfigureAwait(false);
            }

            if (_committer is not null)
            {
                await _committer.DisposeAsync().ConfigureAwait(false);
            }

            if (_walTransport is not null)
            {
                await _walTransport.DisposeAsync().ConfigureAwait(false);
            }

            _memTransport?.Dispose();

            if (_storeReplicas is not null)
            {
                foreach (var r in _storeReplicas)
                {
                    if (r is not null)
                    {
                        await r.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            if (_ledgerStore is not null)
            {
                await _ledgerStore.DisposeAsync().ConfigureAwait(false);
            }

            if (_wal is not null)
            {
                await _wal.DisposeAsync().ConfigureAwait(false);
            }

            if (_ledgerBatchLocks is not null)
            {
                foreach (var l in _ledgerBatchLocks)
                {
                    l?.Dispose();
                }
            }

            if (_journalKeyLocks is not null)
            {
                foreach (var l in _journalKeyLocks)
                {
                    l?.Dispose();
                }
            }

            _arcPayload.Dispose();

            try
            {
                if (Directory.Exists(_rootDir))
                {
                    Directory.Delete(_rootDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp directory cleanup.
            }
        }
    }
}

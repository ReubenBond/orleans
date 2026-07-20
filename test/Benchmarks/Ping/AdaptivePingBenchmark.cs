using System.Net;
using BenchmarkGrainInterfaces.Ping;
using BenchmarkGrains.Ping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
#if ORLEANS_PROFILING
using System.Diagnostics;
using Orleans.Runtime;
#endif

namespace Benchmarks.Ping;

/// <summary>
/// Benchmark that runs indefinitely and uses hill climbing to tune concurrency
/// for maximum throughput. Useful for finding optimal concurrency levels and
/// for long-running performance testing.
/// </summary>
public class AdaptivePingBenchmark : IDisposable
{
    public enum BenchmarkMode
    {
        /// <summary>Client runs inside the silo process (lowest latency)</summary>
        HostedClient,
        /// <summary>External client connects to silo(s)</summary>
        ExternalClient,
        /// <summary>Calls go from one silo to another (tests cross-silo performance)</summary>
        SiloToSilo
    }

    private readonly List<IHost> _hosts = new();
    private readonly IHost _clientHost;
    private readonly IClusterClient _client;
    private readonly BenchmarkMode _mode;
    private readonly int _numSilos;
    private readonly CancellationTokenSource _cts = new();
    private const int DefaultRequestsPerBlock = 100;
    private const int DefaultInitialStepSize = 50;
    private const int DefaultMaxStableRounds = 5;
    private const double DefaultMinimumRelativeImprovement = 0.005;
    private static readonly TimeSpan DefaultMeasurementInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromMilliseconds(250);
#if ORLEANS_PROFILING
    private static readonly RpcProbeTracing ProbeTracing = new();
#endif

    public string Description { get; }
    public int BestConcurrency { get; private set; }
    public double BestThroughput { get; private set; }

    public AdaptivePingBenchmark(BenchmarkMode mode = BenchmarkMode.HostedClient, int numSilos = 1)
    {
        _mode = mode;
        _numSilos = numSilos;

        // Determine configuration based on mode
        bool startClient = mode == BenchmarkMode.ExternalClient;
        bool grainsOnSecondariesOnly = mode == BenchmarkMode.SiloToSilo;

        if (mode == BenchmarkMode.SiloToSilo && numSilos < 2)
        {
            numSilos = 2;
            _numSilos = 2;
        }

        Description = mode switch
        {
            BenchmarkMode.HostedClient => "Hosted Client",
            BenchmarkMode.ExternalClient when numSilos == 1 => "Client to Silo",
            BenchmarkMode.ExternalClient => $"Client to {numSilos} Silos",
            BenchmarkMode.SiloToSilo => "Silo to Silo",
            _ => mode.ToString()
        };

        // Start silos
        for (int i = 0; i < numSilos; i++)
        {
            var primary = i == 0 ? null : new IPEndPoint(IPAddress.Loopback, 11111);
            var hostBuilder = new HostBuilder().UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: 11111 + i,
                    gatewayPort: 30000 + i,
                    primarySiloEndpoint: primary);
#if ORLEANS_PROFILING
                siloBuilder.AddActivityPropagation();
#endif

                // For SiloToSilo mode: remove grains from primary silo to force cross-silo calls
                if (i == 0 && grainsOnSecondariesOnly)
                {
                    siloBuilder.Configure<GrainTypeOptions>(options => options.Classes.Remove(typeof(PingGrain)));
                }
            });

            var host = hostBuilder.Build();
            host.StartAsync().GetAwaiter().GetResult();
            _hosts.Add(host);
        }

        // Wait for cluster to stabilize in multi-silo mode
        if (numSilos > 1)
        {
            Thread.Sleep(4000);
        }

        // Start external client if needed
        if (startClient)
        {
            var hostBuilder = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
            {
#if ORLEANS_PROFILING
                clientBuilder.AddActivityPropagation();
#endif
                if (numSilos == 1)
                {
                    clientBuilder.UseLocalhostClustering();
                }
                else
                {
                    var gateways = Enumerable.Range(30000, numSilos)
                        .Select(i => new IPEndPoint(IPAddress.Loopback, i))
                        .ToArray();
                    clientBuilder.UseStaticClustering(gateways);
                }
            });

            _clientHost = hostBuilder.Build();
            _clientHost.StartAsync().GetAwaiter().GetResult();
            _client = _clientHost.Services.GetRequiredService<IClusterClient>();

            // Warm up the client connection
            var grain = _client.GetGrain<IPingGrain>(0);
            grain.Run().AsTask().GetAwaiter().GetResult();
        }

        // Wire up Ctrl+C to cancel
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutdown requested...");
            _cts.Cancel();
        };
#if ORLEANS_PROFILING
        RpcCallTrace.WriteBenchmarkPhase(1, 1);
#endif
    }

    /// <summary>
    /// Gets the grain factory based on the current mode.
    /// </summary>
    private IGrainFactory GetGrainFactory()
    {
        return _mode == BenchmarkMode.ExternalClient
            ? _client
            : _hosts[0].Services.GetRequiredService<IGrainFactory>();
    }

    /// <summary>
    /// Runs the adaptive benchmark, tuning concurrency via hill climbing.
    /// Terminates after maxStableRounds without a statistically significant improvement (default 5), or runs forever if 0.
    /// </summary>
    public async Task RunAsync(
        int initialConcurrency = 100,
        int minConcurrency = 1,
        int maxConcurrency = 2000,
        TimeSpan? warmupDuration = null,
        TimeSpan? measurementInterval = null,
        int maxStableRounds = DefaultMaxStableRounds,
        int initialStepSize = DefaultInitialStepSize,
        TimeSpan? sampleInterval = null,
        double minimumRelativeImprovement = DefaultMinimumRelativeImprovement)
    {
        var grainFactory = GetGrainFactory();

        Console.WriteLine($"=== Adaptive Ping Benchmark: {Description} ===");
        Console.WriteLine();

        var loadGenerator = new AdaptiveConcurrencyLoadGenerator<IPingGrain>(
            issueRequest: g => g.Run(),
            getStateForWorker: workerId => grainFactory.GetGrain<IPingGrain>(workerId),
            requestsPerBlock: DefaultRequestsPerBlock,
            warmupDuration: warmupDuration ?? TimeSpan.FromSeconds(5),
            measurementInterval: measurementInterval ?? DefaultMeasurementInterval,
            minConcurrency: minConcurrency,
            maxConcurrency: maxConcurrency,
            initialConcurrency: initialConcurrency,
            maxStableRounds: maxStableRounds,
            initialStepSize: initialStepSize,
            sampleInterval: sampleInterval ?? DefaultSampleInterval,
            minimumRelativeImprovement: minimumRelativeImprovement);

        try
        {
            await loadGenerator.RunForeverAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on Ctrl+C
        }

        BestConcurrency = loadGenerator.BestConcurrency;
        BestThroughput = loadGenerator.BestThroughput;

        Console.WriteLine($"\nFinal best: {BestConcurrency} concurrency @ {BestThroughput:N0}/s");
    }

    public async Task RunFixedAsync(int concurrency, TimeSpan warmupDuration, TimeSpan duration)
    {
        var grainFactory = GetGrainFactory();
        var loadGenerator = new AdaptiveConcurrencyLoadGenerator<IPingGrain>(
            issueRequest: g => g.Run(),
            getStateForWorker: workerId => grainFactory.GetGrain<IPingGrain>(workerId),
            requestsPerBlock: DefaultRequestsPerBlock,
            warmupDuration: warmupDuration,
            measurementInterval: duration,
            minConcurrency: concurrency,
            maxConcurrency: concurrency,
            initialConcurrency: concurrency,
            maxStableRounds: 0,
            initialStepSize: 1,
            sampleInterval: DefaultSampleInterval,
            minimumRelativeImprovement: DefaultMinimumRelativeImprovement);

        await loadGenerator.RunFixedAsync(_cts.Token);
        BestConcurrency = loadGenerator.BestConcurrency;
        BestThroughput = loadGenerator.BestThroughput;

        Console.WriteLine($"\nFixed result: {BestConcurrency} concurrency @ {BestThroughput:N0}/s");
    }

    public async Task RunFixedAsync(
        int concurrency,
        TimeSpan warmupDuration,
        TimeSpan measurementDuration,
        int iterations,
        int traceProbes = 0)
    {
        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        var grainFactory = GetGrainFactory();
#if ORLEANS_PROFILING
        const bool recordLatency = true;
#else
        const bool recordLatency = false;
#endif
        var loadGenerator = new FixedConcurrencyLoadGenerator<IPingGrain>(
            concurrency,
            issueRequest: static grain => grain.Run(),
            getStateForWorker: workerId => grainFactory.GetGrain<IPingGrain>(workerId),
            recordLatency: recordLatency);

        Console.WriteLine($"=== Fixed Ping Benchmark: {Description} ===");
        Console.WriteLine($"Process: {Environment.ProcessId}");
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Concurrency: {concurrency}");
        Console.WriteLine($"Warmup: {warmupDuration.TotalSeconds:F1}s");
        Console.WriteLine($"Measurement: {iterations} x {measurementDuration.TotalSeconds:F1}s");
        Console.WriteLine();

#if ORLEANS_PROFILING
        RpcCallTrace.WriteBenchmarkPhase(2, 1);
#endif
        await loadGenerator.WarmupAsync(warmupDuration);
#if ORLEANS_PROFILING
        RpcCallTrace.WriteBenchmarkPhase(3, 1);
#endif
        Console.WriteLine("Warmup complete");

        var results = new FixedConcurrencyLoadResult[iterations];
        for (var i = 0; i < results.Length; i++)
        {
#if ORLEANS_PROFILING
            RpcCallTrace.WriteBenchmarkPhase(4, 1);
            results[i] = await loadGenerator.RunAsync(
                measurementDuration,
                traceProbes > 0 ? () => RunTraceProbesAsync(grainFactory, concurrency, traceProbes) : null);
            RpcCallTrace.WriteBenchmarkPhase(5, 1);
#else
            results[i] = await loadGenerator.RunAsync(measurementDuration);
#endif
            var result = results[i];
            Console.WriteLine(
                $"Iteration {i + 1}: {result.Throughput:N0}/s, {result.Completed:N0} calls, " +
                $"{result.AllocatedBytesPerOperation:N1} B/op, " +
                $"GC {result.Gen0Collections}/{result.Gen1Collections}/{result.Gen2Collections}"
#if ORLEANS_PROFILING
                + ", " +
                $"latency mean/p50/p90/p99/p99.9/max " +
                $"{result.Latency.MeanMicroseconds:F2}/{result.Latency.GetPercentileMicroseconds(50):F2}/" +
                $"{result.Latency.GetPercentileMicroseconds(90):F2}/{result.Latency.GetPercentileMicroseconds(99):F2}/" +
                $"{result.Latency.GetPercentileMicroseconds(99.9):F2}/{result.Latency.MaxMicroseconds:F2} us"
#endif
                );
        }

        var mean = results.Average(static result => result.Throughput);
        var variance = results.Length > 1
            ? results.Sum(result => Math.Pow(result.Throughput - mean, 2)) / (results.Length - 1)
            : 0;
        var allocatedBytesPerOperation = results.Average(static result => result.AllocatedBytesPerOperation);
        Console.WriteLine();
        Console.WriteLine($"Mean: {mean:N0}/s");
        Console.WriteLine($"StdDev: {Math.Sqrt(variance):N0}/s ({Math.Sqrt(variance) / mean:P2})");
        Console.WriteLine($"Allocated: {allocatedBytesPerOperation:N1} B/op");
    }

    public async Task ShutdownAsync()
    {
#if ORLEANS_PROFILING
        RpcCallTrace.WriteBenchmarkPhase(6, 1);
#endif
        if (_clientHost != null)
        {
            await _clientHost.StopAsync();
            if (_clientHost is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                _clientHost.Dispose();
        }

        _hosts.Reverse();
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            if (host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                host.Dispose();
        }
    }

#if ORLEANS_PROFILING
    internal static async Task RunTraceProbesAsync(IGrainFactory grainFactory, int concurrency, int traceProbes)
    {
        for (var i = 0; i < traceProbes; i++)
        {
            var grainId = Random.Shared.Next(Math.Max(1, concurrency));
            var grain = grainFactory.GetGrain<IPingGrain>(grainId);
            using var probe = ProbeTracing.Begin();
            var previousMarker = RequestContext.Get(RpcCallTrace.ExactTraceMarker);
            RequestContext.Set(RpcCallTrace.ExactTraceMarker, true);
            Console.WriteLine($"Trace probe {i + 1}: {probe.TraceId}");

            var start = Stopwatch.GetTimestamp();
            try
            {
                await grain.Run();
                Console.WriteLine($"Trace probe {i + 1} latency: {Stopwatch.GetElapsedTime(start).TotalMicroseconds:F2} us");
            }
            finally
            {
                if (previousMarker is null)
                {
                    RequestContext.Remove(RpcCallTrace.ExactTraceMarker);
                }
                else
                {
                    RequestContext.Set(RpcCallTrace.ExactTraceMarker, previousMarker);
                }
            }
        }
    }

    internal sealed class RpcProbeTracing : IDisposable
    {
        private readonly ActivityListener _listener;
        private ActivityTraceId _selectedTraceId;

        public RpcProbeTracing()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name.StartsWith("Microsoft.Orleans.", StringComparison.Ordinal),
                Sample = Sample,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.None,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Probe Begin()
        {
            var activity = new Activity("Orleans.RpcProbe").SetIdFormat(ActivityIdFormat.W3C).Start();
            _selectedTraceId = activity.TraceId;
            return new(this, activity);
        }

        public void Dispose() => _listener.Dispose();

        private ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options) =>
            options.Parent.TraceId == _selectedTraceId || RequestContext.Get(RpcCallTrace.ExactTraceMarker) is true
                ? ActivitySamplingResult.PropagationData
                : ActivitySamplingResult.None;

        public sealed class Probe(RpcProbeTracing owner, Activity activity) : IDisposable
        {
            public ActivityTraceId TraceId => activity.TraceId;

            public void Dispose()
            {
                owner._selectedTraceId = default;
                activity.Stop();
            }
        }
    }
#endif

    public void Dispose()
    {
        _cts.Dispose();
        (_client as IDisposable)?.Dispose();
        _hosts.ForEach(h => h.Dispose());
    }

    /// <summary>
    /// Runs all adaptive ping benchmark scenarios and prints a summary.
    /// </summary>
    public static async Task RunAllScenariosAsync(int maxStableRounds = DefaultMaxStableRounds)
    {
        var results = new List<(string Description, int BestConcurrency, double BestThroughput)>();

        var scenarios = new (BenchmarkMode Mode, int NumSilos)[]
        {
            (BenchmarkMode.HostedClient, 1),
            (BenchmarkMode.ExternalClient, 1),
            (BenchmarkMode.ExternalClient, 2),
            (BenchmarkMode.SiloToSilo, 2),
        };

        foreach (var (mode, numSilos) in scenarios)
        {
            var benchmark = new AdaptivePingBenchmark(mode, numSilos);
            try
            {
                await benchmark.RunAsync(maxStableRounds: maxStableRounds);
                results.Add((benchmark.Description, benchmark.BestConcurrency, benchmark.BestThroughput));
            }
            finally
            {
                await benchmark.ShutdownAsync();
                benchmark.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 82));
            Console.WriteLine();

            GC.Collect();
            await Task.Delay(1000); // Brief pause between scenarios
        }

        // Print summary in GitHub-flavored markdown table format
        Console.WriteLine();
        Console.WriteLine("## Adaptive Ping Benchmark Results");
        Console.WriteLine();
        Console.WriteLine("| Scenario | Best Concurrency | Best Throughput |");
        Console.WriteLine("|----------|------------------|-----------------|");

        foreach (var (description, bestConcurrency, bestThroughput) in results)
        {
            Console.WriteLine($"| {description} | {bestConcurrency} | {bestThroughput:N0}/s |");
        }

        Console.WriteLine();
    }
}

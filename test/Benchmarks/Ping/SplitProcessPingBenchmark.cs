#if ORLEANS_PROFILING
using System.IO.Pipes;
using System.Net;
using BenchmarkGrainInterfaces.Ping;
using BenchmarkGrains.Ping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Benchmarks.Ping;

internal static class SplitProcessPingBenchmark
{
    internal static async Task RunTargetAsync(string sessionId, string readyFile)
    {
        using var tracing = new AdaptivePingBenchmark.RpcProbeTracing();
        using var host = new HostBuilder()
            .UseOrleans((_, siloBuilder) =>
            {
                siloBuilder.UseLocalhostClustering(siloPort: 11111, gatewayPort: 30000);
                siloBuilder.AddActivityPropagation();
            })
            .Build();

        RpcCallTrace.WriteBenchmarkPhase(1, 2);
        await host.StartAsync();
        Console.WriteLine($"Role: target; PID: {Environment.ProcessId}; Silo: 127.0.0.1:11111; Gateway: 127.0.0.1:30000");

        var readyDirectory = Path.GetDirectoryName(readyFile);
        if (!string.IsNullOrEmpty(readyDirectory))
        {
            Directory.CreateDirectory(readyDirectory);
        }

        var temporaryReadyFile = $"{readyFile}.{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temporaryReadyFile, $"ready {Environment.ProcessId}");
        File.Move(temporaryReadyFile, readyFile, overwrite: true);
        Console.WriteLine($"Ready: {readyFile}");

        await using var pipe = new NamedPipeServerStream(
            GetPipeName(sessionId),
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync();
        _ = pipe.ReadByte();

        RpcCallTrace.WriteBenchmarkPhase(6, 2);
        await host.StopAsync();
    }

    internal static async Task RunDriverAsync(
        string sessionId,
        int concurrency,
        TimeSpan warmupDuration,
        TimeSpan measurementDuration,
        int iterations,
        int traceProbes)
    {
        await using var controlPipe = new NamedPipeClientStream(
            ".",
            GetPipeName(sessionId),
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await controlPipe.ConnectAsync(30_000);

        using var host = new HostBuilder()
            .UseOrleans((_, siloBuilder) =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: 11112,
                    gatewayPort: 30001,
                    primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, 11111));
                siloBuilder.Configure<GrainTypeOptions>(options => options.Classes.Remove(typeof(PingGrain)));
                siloBuilder.AddActivityPropagation();
            })
            .Build();

        try
        {
            RpcCallTrace.WriteBenchmarkPhase(1, 1);
            await host.StartAsync();
            Console.WriteLine($"Role: driver; PID: {Environment.ProcessId}; Silo: 127.0.0.1:11112; Gateway: 127.0.0.1:30001");

            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            await WaitForReadyAsync(grainFactory);
            var loadGenerator = new FixedConcurrencyLoadGenerator<IPingGrain>(
                concurrency,
                issueRequest: static grain => grain.Run(),
                getStateForWorker: workerId => grainFactory.GetGrain<IPingGrain>(workerId),
                recordLatency: true);

            RpcCallTrace.WriteBenchmarkPhase(2, 1);
            await loadGenerator.WarmupAsync(warmupDuration);
            RpcCallTrace.WriteBenchmarkPhase(3, 1);

            for (var i = 0; i < iterations; i++)
            {
                RpcCallTrace.WriteBenchmarkPhase(4, 1);
                var result = await loadGenerator.RunAsync(
                    measurementDuration,
                    traceProbes > 0
                        ? () => AdaptivePingBenchmark.RunTraceProbesAsync(grainFactory, concurrency, traceProbes)
                        : null);
                RpcCallTrace.WriteBenchmarkPhase(5, 1);

                Console.WriteLine(
                    $"Iteration {i + 1}: {result.Throughput:N0}/s, {result.Completed:N0} calls, " +
                    $"{result.AllocatedBytesPerOperation:N1} B/op, latency mean/p50/p90/p99/p99.9/max " +
                    $"{result.Latency.MeanMicroseconds:F2}/{result.Latency.GetPercentileMicroseconds(50):F2}/" +
                    $"{result.Latency.GetPercentileMicroseconds(90):F2}/{result.Latency.GetPercentileMicroseconds(99):F2}/" +
                    $"{result.Latency.GetPercentileMicroseconds(99.9):F2}/{result.Latency.MaxMicroseconds:F2} us");
            }
        }
        finally
        {
            RpcCallTrace.WriteBenchmarkPhase(6, 1);
            await host.StopAsync();
            await controlPipe.WriteAsync(new byte[] { 1 });
        }
    }

    private static async Task WaitForReadyAsync(IGrainFactory grainFactory)
    {
        var grain = grainFactory.GetGrain<IPingGrain>(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Exception lastError = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await grain.Run();
                return;
            }
            catch (Exception exception) when (!timeout.IsCancellationRequested)
            {
                lastError = exception;
                await Task.Delay(100, timeout.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        throw new TimeoutException("The target silo did not accept a warmup call within 30 seconds.", lastError);
    }

    private static string GetPipeName(string sessionId) => $"orleans-rpc-profile-{sessionId}";
}
#endif

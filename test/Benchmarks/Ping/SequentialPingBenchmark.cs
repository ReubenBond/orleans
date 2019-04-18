using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkGrainInterfaces.Ping;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Benchmarks.Ping
{
    [MemoryDiagnoser]
    [EtwProfiler(performExtraBenchmarksRun: false)]
    public class SequentialPingBenchmark : IDisposable 
    {
        private readonly ISiloHost host;
        private readonly IPingGrain grain;
        private readonly IClusterClient client;

        public SequentialPingBenchmark()
        {
            this.host = new SiloHostBuilder()
                .UseLocalhostClustering()
                .Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev")
                .Build();
            this.host.StartAsync().GetAwaiter().GetResult();

            this.client = new ClientBuilder().UseLocalhostClustering().Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev").Build();
            this.client.Connect().GetAwaiter().GetResult();
            var grainFactory = this.client;

            this.grain = grainFactory.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
            this.grain.Run().GetAwaiter().GetResult();
        }
        
        [Benchmark]
        public Task Ping() => grain.Run();

        public async Task PingForever()
        {
            while (true)
            {
                await grain.Run();
            }
        }

        public Task PingConcurrent() => this.Run(
            runs: 3,
            grainFactory: this.client,
            blocksPerWorker: 10);

        public Task PingConcurrentHostedClient() => this.Run(
            runs: 3,
            grainFactory: (IGrainFactory)this.host.Services.GetService(typeof(IGrainFactory)),
            blocksPerWorker: 30);

        private async Task Run(int runs, IGrainFactory grainFactory, int blocksPerWorker)
        {
            var loadGenerator = new ConcurrentLoadGenerator<IPingGrain>(
                maxConcurrency: 250,
                blocksPerWorker: blocksPerWorker,
                requestsPerBlock: 500,
                issueRequest: g => g.Run(),
                getStateForWorker: workerId => grainFactory.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode()));
            await loadGenerator.Warmup();
            while (runs-- > 0) await loadGenerator.Run();
        }

        public async Task PingPongForever()
        {
            var other = this.client.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
            while (true)
            {
                await grain.PingPongInterleave(other, 100);
            }
        }

        public async Task PingPongForeverSaturate()
        {
            var num = Environment.ProcessorCount * Environment.ProcessorCount * 2;
            var grains = Enumerable.Range(0, num).Select(n => this.client.GetGrain<IPingGrain>(n)).ToArray();
            var others = Enumerable.Range(num, num*2).Select(n => this.client.GetGrain<IPingGrain>(n)).ToArray();
            var tasks = new List<Task>(num);
            while (true)
            {
                tasks.Clear();
                for (var i = 0; i < num; i++)
                {
                    tasks.Add(grains[i].PingPongInterleave(others[i], 100));
                }

                await Task.WhenAll(tasks);
            }
        }

        [GlobalCleanup]
        public void Dispose()
        {
            (this.client as IDisposable)?.Dispose(); 
            this.host.Dispose();
        }
    }
}
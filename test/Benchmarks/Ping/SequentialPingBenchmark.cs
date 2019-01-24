using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkGrainInterfaces.Ping;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Benchmarks.Ping
{
    [MemoryDiagnoser]
    public class SequentialPingBenchmark : IDisposable 
    {
        private readonly ISiloHost host;
        private readonly IPingGrain grain;
        private readonly IEchoGrain echoGrain;
        private readonly INewEchoGrain newEchoGrain;
        private readonly IClusterClient client;

        public SequentialPingBenchmark()
        {
            this.host = new SiloHostBuilder().UseLocalhostClustering()/*.ConfigureLogging(l => l.AddConsole())*/.Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev").Build();
            this.host.StartAsync().GetAwaiter().GetResult();

            this.client = new ClientBuilder().UseLocalhostClustering()/*.ConfigureLogging(l => l.AddConsole())*/.Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev").Build();
            this.client.Connect().GetAwaiter().GetResult();

            this.grain = this.client.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
            this.grain.Run().GetAwaiter().GetResult();

            Console.WriteLine("started");
            this.echoGrain = this.client.GetGrain<IEchoGrain>(Guid.NewGuid().GetHashCode());
            this.echoGrain.Echo(0).GetAwaiter().GetResult();
            this.newEchoGrain = this.client.GetGrain<INewEchoGrain>(Guid.NewGuid().GetHashCode());
            this.newEchoGrain.Echo(0).GetAwaiter().GetResult();
            Console.WriteLine("Init complete");
        }

        //[Benchmark]
        // public Task Ping() => grain.Run();

        [Benchmark]
        public ValueTask<int> Echo() => this.echoGrain.Echo(7);

        [Benchmark]
        public ValueTask<int> NewEcho() => this.newEchoGrain.Echo(7);

        public async Task PingForever()
        {
            while (true)
            {
                await echoGrain.Echo(21);
                await this.newEchoGrain.Echo(21);
            }
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
            this.client.Dispose(); 
            this.host.Dispose();
        }
    }
}
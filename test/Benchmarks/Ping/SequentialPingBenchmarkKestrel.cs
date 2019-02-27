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
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Benchmarks.Ping
{
    [MemoryDiagnoser]
    [EtwProfiler]
    public class KestrelSequentialPingBenchmark : IDisposable 
    {
        private readonly IHost host;
       // private readonly IHost two;
        private readonly IPingGrain grain;
        private readonly IClusterClient client;

        public KestrelSequentialPingBenchmark()
        {
            Console.WriteLine("starting");
            var primary = new IPEndPoint(IPAddress.Loopback, 60666);
            this.host = CreateSilo(primary, 0).GetAwaiter().GetResult();
            Console.WriteLine("silo started");
            //this.two = CreateSilo(primary, 1).GetAwaiter().GetResult();

            this.client = new ClientBuilder()
                .UseLocalhostClustering(gatewayPort: 60777)
                .Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev")
                .Build();
            this.client.Connect(ex => Task.FromResult(true)).GetAwaiter().GetResult();
            Console.WriteLine("client started");
            this.grain = this.client.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());


            /*this.client = two.Services.GetRequiredService<IClusterClient>();
            int siloPort;
            do
            {
                this.grain = this.client.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
                siloPort = this.grain.GetSiloPort().GetAwaiter().GetResult();
            } while (siloPort != 60666);*/
        }

        private static async Task<IHost> CreateSilo(IPEndPoint primary, int siloNum)
        {
            var host = new HostBuilder()
                /*.ConfigureWebHost(builder =>
                {
                    builder.UseKestrel(options =>
                    {
                        options.Listen(
                            new IPEndPoint(IPAddress.Any, 60666 + siloNum),
                            listenOptions =>
                            {
                                listenOptions.UseOrleansSiloConnectionHandler();
                            });
                        options.Listen(
                            new IPEndPoint(IPAddress.Any, 60777 + siloNum),
                            listenOptions =>
                            {
                                listenOptions.UseOrleansGatewayConnectionHandler();
                            });
                    })
                    .UseStartup<Startup>();
                })*/
                .UseOrleans(builder =>
                {
                    builder
                    .Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev")
                    .UseDevelopmentClustering((DevelopmentClusterMembershipOptions options) => { options.PrimarySiloEndpoint = primary; })
                    .Configure<EndpointOptions>(options =>
                    {
                        options.AdvertisedIPAddress = IPAddress.Loopback;
                        options.SiloPort = 60666 + siloNum;
                        options.GatewayPort = 60777 + siloNum;
                        options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, 60666 + siloNum);
                        options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, 60777 + siloNum);
                    })///.Configure<SchedulingOptions>(options => options.MaxActiveThreads = 2)
                    .EnableDirectClient();
                })
                //.ConfigureLogging(logging => logging.AddConsole())
                .UseConsoleLifetime().Build();

            await host.StartAsync();
            return host;
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

        public async Task PingForeverSaturate()
        {
            var num = Environment.ProcessorCount * Environment.ProcessorCount * 2;
            var grains = Enumerable.Range(0, num).Select(n => this.client.GetGrain<IPingGrain>(n)).ToArray();
            var tasks = new Task[num];
            while (true)
            {
                for (var i = 0; i < num; i++)
                {
                    tasks[i] = grains[i].Run();
                }

                await Task.WhenAll(tasks);
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
            //this.client.Dispose();
            this.host.Dispose();
            //this.two.Dispose();
        }

/*        public class Startup
        {
            // This method gets called by the runtime. Use this method to add services to the container.
            // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
            public void ConfigureServices(IServiceCollection services)
            {
            }

            // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
            public void Configure(IApplicationBuilder app, Microsoft.AspNetCore.Hosting.IHostingEnvironment env)
            {
                if (env.IsDevelopment())
                {
                    app.UseDeveloperExceptionPage();
                }

                app.Run(async (context) =>
                {
                    await context.Response.WriteAsync("Hello World!");
                });
            }
        }*/
    }
}
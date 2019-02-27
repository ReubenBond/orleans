using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Messaging;
using TestGrainContracts;

namespace KestrelTestSilo
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var primary = new IPEndPoint(IPAddress.Loopback, 60666);
            var one = await CreateSilo(primary, 0);
           // var two = await CreateSilo(primary, 1);
           // var three = await CreateSilo(primary, 2);

            //await Task.Delay(TimeSpan.FromMinutes(2));
            await RunClient();
            /*var client = two.Services.GetRequiredService<IClusterClient>();
            var grain = client.GetGrain<IMyHappyLittleKestrelGrain>("blah");
            while (true)
            {
                await grain.SayHelloKestrel("tob");
                await Task.Delay(1000);
            }*/
        }

        private static async Task<IHost> CreateSilo(IPEndPoint primary, int siloNum)
        {
            var host = new HostBuilder()
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
                    });
                })
                .ConfigureLogging(logging => logging.AddConsole())
                .UseConsoleLifetime()
                .Build();

            await host.StartAsync();
            return host;
        }

        static async Task RunClient()
        {
            var client = new ClientBuilder()
                .UseLocalhostClustering(gatewayPort: 60777)
                .Build();
            await client.Connect(ex => Task.FromResult(true));

            var grain = client.GetGrain<IMyHappyLittleKestrelGrain>(11);

            while (true)
            {
                await grain.Ping();
            }
        }
    }

    public class MyKestrelGrain : Grain, IMyHappyLittleKestrelGrain
    {
        private readonly ILogger<MyKestrelGrain> log;

        public MyKestrelGrain(ILogger<MyKestrelGrain> log) => this.log = log;

        public Task<string> HelloChain(int id)
        {
            var grain = this.GrainFactory.GetGrain<IMyHappyLittleKestrelGrain>(id);
            if (id <= 0) return grain.SayHelloKestrel("mememe");
            return grain.HelloChain(id - 1);
        }

        public Task Ping() => Task.CompletedTask;

        public Task<string> SayHelloKestrel(string name)
        {
            //throw new InvalidOperationException("no no " + name);
           // this.log.LogInformation($"Received a happy little message from {name} just now :)");
           return Task.FromResult($"Hello from Orleans on Kestrel, {name}!!!");
        }
    }
}

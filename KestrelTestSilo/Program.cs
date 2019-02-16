using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
            var host = new HostBuilder()
                .ConfigureWebHost(builder =>
                {
                    builder.UseKestrel(options =>
                    {
                        options.Listen(
                            new IPEndPoint(IPAddress.Any, 60666),
                            listenOptions =>
                            {
                                listenOptions.UseOrleansSiloConnectionHandler();
                            });
                        options.Listen(
                            new IPEndPoint(IPAddress.Any, 60777),
                            listenOptions =>
                            {
                                listenOptions.UseOrleansGatewayConnectionHandler();
                            });
                    })
                    .UseStartup<Startup>();
                })
                .UseOrleans(builder =>
                {
                    builder
                    .Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev")
                    .UseDevelopmentClustering((DevelopmentClusterMembershipOptions options) => { options.PrimarySiloEndpoint = new IPEndPoint(IPAddress.Loopback, 60666); })
                    .Configure<EndpointOptions>(options =>
                    {
                        options.AdvertisedIPAddress = IPAddress.Loopback;
                        options.SiloPort = 60666;
                        options.GatewayPort = 60777;
                        options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, 20666);
                        options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, 20777);
                    });
                })
                .ConfigureLogging(logging => logging.AddConsole())
                .UseConsoleLifetime().Build();
            
            await host.RunAsync();
        }

        public class Startup
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
        }
    }

    public class MyKestrelGrain : Grain, IMyHappyLittleKestrelGrain
    {
        private readonly ILogger<MyKestrelGrain> log;

        public MyKestrelGrain(ILogger<MyKestrelGrain> log) => this.log = log;

        public Task<string> SayHelloKestrel(string name)
        {
            this.log.LogInformation($"Received a happy little message from {name} just now :)");
            return Task.FromResult($"Hello from Orleans on Kestrel, {name}!!!");
        }
    }
}

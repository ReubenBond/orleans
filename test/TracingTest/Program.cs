using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Hosting;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenTracing;
using Jaeger.Samplers;
using Jaeger;
using OpenTracing.Util;
using Orleans.Configuration;
using Orleans.Threading;

namespace TracingTest
{
    public sealed class NetCoreThreadPoolExecutor : IExecutor
    {
        public void QueueWorkItem(Action<object> callback, object state = null)
        {
            ThreadPool.UnsafeQueueUserWorkItem(callback, state, preferLocal: true);
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .UseOrleans((hostContext, siloBuilder) =>
                {
                    siloBuilder.ConfigureServices((ctx, services) =>
                    {
                        services.AddSingleton<TraceGrainCallFilter>();
                    });

                    siloBuilder.AddIncomingGrainCallFilter<TraceGrainCallFilter>();
                    siloBuilder.AddOutgoingGrainCallFilter<TraceGrainCallFilter>();

                    if (!int.TryParse(hostContext.Configuration["port"], out var port)) port = 10_000;

                    IPEndPoint primary = null;
                    if (port == 10_000)
                    {
                        primary = new IPEndPoint(IPAddress.Loopback, 10_000);
                    }

                    siloBuilder.UseLocalhostClustering(port, 10_000 + port, primary);

                    siloBuilder.Configure<SiloMessagingOptions>(options => options.PropagateActivityId = true);
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IHostedService, LoadDriverHostedService>();

                    services.AddSingleton<ITracer>(sp =>
                    {
                        var serviceName = typeof(Program).Namespace;
                        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                        var sampler = new ConstSampler(sample: true);

                        var tracer = new Tracer.Builder(serviceName)
                        .WithLoggerFactory(loggerFactory)
                        .WithSampler(sampler)
                        .Build();

                        GlobalTracer.Register(tracer);

                        return tracer;
                    });

                    services.AddOpenTracing();
                    //services.AddSingleton<IExecutor, NetCoreThreadPoolExecutor>();
                })
                .Build();

            host.Run();
        }
    }

    public class LoadDriverHostedService : IHostedService
    {
        private CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ILogger<LoadDriverHostedService> log;
        private readonly IGrainFactory grainFactory;
        private readonly DiagnosticListener diagnosticListener;
        private Task runTask;

        public LoadDriverHostedService(ILogger<LoadDriverHostedService> log, IGrainFactory grainFactory)
        {
            this.log = log;
            this.grainFactory = grainFactory;
            this.diagnosticListener = new DiagnosticListener("LoadDriver");
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            runTask = Run();
            return Task.CompletedTask;
        }

        private async Task Run()
        {
            var counter = 0;
            this.log.LogInformation("Starting load driver");

            await Task.Delay(TimeSpan.FromSeconds(5));
            var grain = this.grainFactory.GetGrain<IPingGrain>(Guid.NewGuid());
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5));
                var activity = new Activity("Ping").AddTag("v", "1");

                try
                {
                    diagnosticListener.StartActivity(activity, null);
                    await grain.Ping(5);
                    if (++counter >= 500) break;
                }
                catch (Exception exception)
                {
                    this.log.LogError(exception, "Ping call failed");
                }
                finally
                {
                    diagnosticListener.StopActivity(activity, null);
                }
            }

            this.log.LogInformation("Stopped load driver");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            this.cancellation.Cancel();
            if (runTask is Task task) await task;
        }
    }

    public interface IPingGrain : IGrainWithGuidKey
    {
        Task Ping(int n);
    }

    public class PingGrain : Grain, IPingGrain
    {
        private IPingGrain nextGrain;
        public async Task Ping(int n)
        {
            if (n > 0)
            {
                nextGrain ??= this.GrainFactory.GetGrain<IPingGrain>(Guid.NewGuid());
                await nextGrain.Ping(n - 1);
            }
        }
    }
}

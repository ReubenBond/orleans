using Orleans.Configuration;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Host.UseOrleans((ctx, orleans) =>
{
    orleans.AddDistributedGrainDirectory();

    orleans.AddActivationRebalancer();

    // aggressive settings our demo
    orleans.Configure<ActivationRebalancerOptions>(o =>
    {
        o.SessionCyclePeriod = TimeSpan.FromSeconds(2);
        o.RebalancerDueTime = TimeSpan.FromSeconds(5);
        o.CycleNumberWeight = 1;
        o.SiloNumberWeight = 0;
    });

    if (ctx.HostingEnvironment.IsDevelopment())
    {
        orleans.UseLocalhostClustering();
    }
    else
    {
        orleans.UseRedisClustering(options => options.ConfigurationOptions = ConfigurationOptions.Parse("redis:6379"));    
    }

    orleans.UseDashboard(o =>
    {
        o.HostSelf = true;
        o.Port = 8888;
    });
});

builder.Services.AddGrpc();
builder.Services.AddHostedService<WorkerService>();
var app = builder.Build();
app.MapGrpcService<ChaosService>();
await app.StartAsync();
await app.WaitForShutdownAsync();

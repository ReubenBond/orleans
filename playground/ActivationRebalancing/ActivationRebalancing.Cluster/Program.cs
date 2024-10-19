using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Runtime.Placement;

#nullable enable

#pragma warning disable ORLEANSEXP002
var builder = Host.CreateApplicationBuilder(args);

builder.AddKeyedRedisClient("orleans-redis");
builder.UseOrleans(builder => builder
    .Configure<ActivationRebalancerOptions>(o =>
    {
        o.RebalancerDueTime = TimeSpan.FromSeconds(5);
        o.SessionCyclePeriod = TimeSpan.FromSeconds(5);
        // uncomment these below, if you want higher migration rate
        //o.CycleNumberWeight = 1;
        //o.SiloNumberWeight = 0; 
    })
    .AddActivationRebalancer());
    var app = builder.Build();
#pragma warning restore ORLEANSEXP002

await app.StartAsync();

var grainFactory = app.Services.GetRequiredService<IGrainFactory>();
var mgmtGrain = grainFactory.GetGrain<IManagementGrain>(0);

Dictionary<SiloAddress, SiloStatus> silos;
do {
    silos = await mgmtGrain.GetHosts(onlyActive: true);
    if (silos.Count >= 4)
    {
        break;
    }

    await Task.Delay(1000);
} while (true);
var addresses = silos.Select(x => x.Key).ToArray();

var tasks = new List<Task>();
RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[0]);
for (var i = 0; i < 300; i++)
{
    tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
}

RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[1]);
for (var i = 0; i < 30; i++)
{
    tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
}

RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[2]);
for (var i = 0; i < 410; i++)
{
    tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
}

RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[3]);
for (var i = 0; i < 120; i++)
{
    tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
}

var clusterMembership = app.Services.GetRequiredService<IClusterMembershipService>();
var localHostAddress = app.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;

bool IsFirstSilo() => clusterMembership.CurrentSnapshot.Members.Where(m => m.Value.Status == SiloStatus.Active).OrderBy(m => m.Key).First().Key.Equals(localHostAddress);

var shutdownCancellation = app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

var sessionCount = 0;
while (true)
{
    if (IsFirstSilo())
    {
        if (sessionCount == 25)
        {
            RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[0]);
            for (var i = 0; i < 50; i++)
            {
                tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
            }

            RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[1]);
            for (var i = 0; i < 50; i++)
            {
                tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
            }
        }

        if (sessionCount == 35)
        {
            RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[1]);
            for (var i = 0; i < 50; i++)
            {
                tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
            }

            RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[2]);
            for (var i = 0; i < 50; i++)
            {
                tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
            }
        }

        if (sessionCount == 45)
        {
            RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[2]);
            for (var i = 0; i < 50; i++)
            {
                tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
            }

            RequestContext.Set(IPlacementDirector.PlacementHintKey, addresses[3]);
            for (var i = 0; i < 50; i++)
            {
                tasks.Add(grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
            }
        }
    }

    await Task.Delay(5000, shutdownCancellation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // session duration
    if (shutdownCancellation.IsCancellationRequested)
    {
        break;
    }

    sessionCount++;

    if (sessionCount > 55)
    {
        sessionCount = 0;
    }
}

await app.WaitForShutdownAsync();

public interface IRebalancingTestGrain : IGrainWithGuidKey
{
    Task Ping();
}

public class RebalancingTestGrain : Grain, IRebalancingTestGrain
{
    public Task Ping() => Task.CompletedTask;
}
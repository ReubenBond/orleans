using BenchmarkGrainInterfaces.Ping;
using DashboardToy.Frontend.Data;
using Microsoft.AspNetCore.Mvc;
using Orleans.Configuration;
using Orleans.Placement.Rebalancing;

var builder = WebApplication.CreateBuilder(args);
builder.AddKeyedRedisClient("orleans-redis");
#pragma warning disable ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
builder.UseOrleans(orleans =>
{
    orleans.AddActiveRebalancing<HardLimitRule>();
    orleans.Configure<ActiveRebalancingOptions>(o =>
    {
        o.MinRebalancingPeriod = TimeSpan.FromSeconds(5);
        o.MaxRebalancingPeriod = TimeSpan.FromSeconds(15);
        o.RecoveryPeriod = TimeSpan.FromSeconds(2);
    });
});
#pragma warning restore ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Add services to the container.
builder.Services.AddSingleton<ClusterDiagnosticsService>();

var app = builder.Build();

var clusterDiagnosticsService = app.Services.GetRequiredService<ClusterDiagnosticsService>();
app.MapGet("/data.json", ([FromServices] ClusterDiagnosticsService clusterDiagnosticsService) => clusterDiagnosticsService.GetGrainCallFrequencies());

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

await app.StartAsync();
var loadGrain = app.Services.GetRequiredService<IGrainFactory>().GetGrain<IFanOutGrain>(0);
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
while (!lifetime.ApplicationStopping.IsCancellationRequested)
{
    await Task.Delay(5_000);
    await loadGrain.Ping();
    await Task.Delay(20_000);
}

await app.WaitForShutdownAsync();

public interface IFanOutGrain : IGrainWithIntegerKey
{
    public ValueTask Ping();
}

public class FanOutGrain : Grain, IFanOutGrain
{
    public const int FanOutFactor = 4;
    public const int MaxLevel = 3;
    private readonly List<IFanOutGrain> _children;

    public FanOutGrain()
    {
        var id = this.GetPrimaryKeyLong();

        var level = id == 0 ? 0 : (int)Math.Log(id, FanOutFactor);
        var numChildren = level < MaxLevel ? FanOutFactor : 0;
        _children = new List<IFanOutGrain>(numChildren);
        var childBase = (id + 1) * FanOutFactor;
        for (var i = 1; i <= numChildren; i++)
        {
            var child = GrainFactory.GetGrain<IFanOutGrain>(childBase + i);
            _children.Add(child);
        }
    }

    public async ValueTask Ping()
    {
        var tasks = new List<ValueTask>(_children.Count);
        foreach (var child in _children)
        {
            tasks.Add(child.Ping());
        }

        // Wait for the tasks to complete.
        foreach (var task in tasks)
        {
            await task;
        }
    }
}

internal sealed class HardLimitRule : IImbalanceToleranceRule
{
    public bool IsSatisfiedBy(uint imbalance) => imbalance <= 10;
}

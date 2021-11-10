using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Microsoft.AspNetCore.Builder;
using VotingData;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans((ctx, builder) =>
{
    if (ctx.HostingEnvironment.IsDevelopment())
    {
        builder.UseLocalhostClustering();
        builder.AddMemoryGrainStorage("votes");
    }
    else
    {
        // In Kubernetes, we use environment variables and the pod manifest
        builder.UseKubernetesHosting();

        // Use Redis for clustering & persistence
        var redisAddress = $"{Environment.GetEnvironmentVariable("REDIS")}:6379";
        builder.UseRedisClustering(options => options.ConnectionString = redisAddress);
        builder.AddRedisGrainStorage("votes", options => options.ConnectionString = redisAddress);
    }

    builder.UseDashboard(options =>
    {
        options.Port = 8888;
    })
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(PollGrain).Assembly));
});

builder.Services.AddControllersWithViews();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseEndpoints(endpoints =>
{
    endpoints.MapDefaultControllerRoute();
});

await app.RunAsync();

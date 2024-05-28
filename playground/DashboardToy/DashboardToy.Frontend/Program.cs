using BenchmarkGrainInterfaces.Ping;
using DashboardToy.Frontend.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.AddKeyedRedisClient("orleans-redis");
#pragma warning disable ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
builder.UseOrleans(orleans => orleans.AddActiveRebalancing());
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
var loadGrain = app.Services.GetRequiredService<IGrainFactory>().GetGrain<ITreeGrain>(0, "0");
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
while (!lifetime.ApplicationStopping.IsCancellationRequested)
{
    await Task.Delay(5_000);
    await loadGrain.Ping();
    await Task.Delay(20_000);
}

await app.WaitForShutdownAsync();

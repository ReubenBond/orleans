using BenchmarkGrainInterfaces.Ping;
using DashboardToy.Frontend.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

var builder = WebApplication.CreateBuilder(args);
builder.AddKeyedRedisClient("orleans-redis");
builder.UseOrleans(orleans => orleans.AddActiveRebalancing());

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<ClusterDiagnosticsService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

await app.StartAsync();
var loadGrain = app.Services.GetRequiredService<IGrainFactory>().GetGrain<ILoadGrain>(Guid.Empty);
await loadGrain.Generate(1000, 20);
await app.WaitForShutdownAsync();

using GPSTracker;
using GPSTracker.Common;
using Orleans.TestingHost.Logging;
using System.Diagnostics;
using System.Net;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging(logging => logging.ClearProviders().AddFile($"silo-{Stopwatch.GetTimestamp()}.log").SetMinimumLevel(LogLevel.Information));
builder.Host.UseOrleans((ctx, siloBuilder) => {

    // In order to support multiple hosts forming a cluster, they must listen on different ports.
    // Use the --InstanceId X option to launch subsequent hosts.
    var instanceId = ctx.Configuration.GetValue<int>("InstanceId");
    siloBuilder.UseLocalhostClustering(
        siloPort: 11111 + instanceId,
        gatewayPort: 30000 + instanceId,
        primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, 11111));

});
builder.WebHost.ConfigureKestrel((ctx, kestrelOptions) =>
{
    // To avoid port conflicts, each Web server must listen on a different port.
    var instanceId = ctx.Configuration.GetValue<int>("InstanceId");
    kestrelOptions.ListenLocalhost(5001 + instanceId);
});
builder.Services.AddHostedService<HubListUpdater>();
builder.Services.AddSignalR().AddJsonProtocol();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
app.UseStaticFiles();
app.UseDefaultFiles();
app.UseRouting();

app.UseAuthorization();

app.MapHub<LocationHub>("/locationHub");

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program.Main");
AppDomain.CurrentDomain.FirstChanceException += CurrentDomain_FirstChanceException;
AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

await app.RunAsync();

void CurrentDomain_FirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) => logger.LogError(e.Exception, "First chance exception!");

void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) => logger.LogError(e.ExceptionObject as Exception, "Unhandled exception!");

void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) => logger.LogError(e.Exception, "Unobserved Task exception!");

using Azure.Monitor.OpenTelemetry.Exporter;
using GPSTracker;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Net;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetryMetrics(metrics =>
{
    metrics
        .AddPrometheusExporter()
        .AddMeter("Microsoft.Orleans")
        .AddMeter("System.Runtime");

    // Uncomment this to export metrics to Azure Monitor
    //metrics.AddAzureMonitorMetricExporter(config => config.ConnectionString = Environment.GetEnvironmentVariable("AZMONCS"))
});

builder.Services.AddOpenTelemetryTracing(tracing =>
{
    // Set a service name
    tracing.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName: "GPSTracker", serviceVersion: "1.0"));

    tracing.AddAspNetCoreInstrumentation();
    tracing.AddSource("Microsoft.Orleans.Runtime");
    tracing.AddSource("Microsoft.Orleans.Application");

    tracing.AddZipkinExporter(zipkin =>
    {
        zipkin.Endpoint = new Uri("http://localhost:9411/api/v2/spans");
    });
});

builder.Host.UseOrleans((ctx, siloBuilder) => {

    // In order to support multiple hosts forming a cluster, they must listen on different ports.
    // Use the --InstanceId X option to launch subsequent hosts.
    var instanceId = ctx.Configuration.GetValue<int>("InstanceId");
    siloBuilder.UseLocalhostClustering(
        siloPort: 11111 + instanceId,
        gatewayPort: 30000 + instanceId,
        primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, 11111));

    siloBuilder.AddActivityPropagation();

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
app.MapPrometheusScrapingEndpoint();

await app.RunAsync();

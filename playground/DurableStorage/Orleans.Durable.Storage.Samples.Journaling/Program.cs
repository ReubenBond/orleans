using System.Net;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Orleans;
using Orleans.Configuration;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.AzureStorage.MetaStore;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Samples.Journaling;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);
var sample = builder.Configuration.GetSection("Sample");

// The AppHost passes Azurite's development account. A deployment reaches its storage account with Microsoft Entra ID:
// new TableServiceClient(uri, new DefaultAzureCredential()), and likewise for blobs, never with an account key.
var storage = builder.Configuration.GetConnectionString("storage")
    ?? throw new InvalidOperationException("The sample reads its storage account from ConnectionStrings:storage, which the AppHost sets.");
var tables = new TableServiceClient(storage);
var registers = tables.GetTableClient(sample["MetaStoreTable"]);
var objects = new BlobServiceClient(storage).GetBlobContainerClient(sample["ObjectContainer"]);

// Each run of the sample starts on an empty Azurite. A deployment provisions the table and the container with the rest of
// its infrastructure, and grants the silos' identity data-plane roles on them alone.
_ = await registers.CreateIfNotExistsAsync();
_ = await objects.CreateIfNotExistsAsync();

// The cluster's registers and the LSM's objects, which every silo shares; the node's own disk is DurableStorage:Node:Root.
builder.Services.AddSingleton<IMetaStore>(new AzureTableMetaStore(registers));
builder.Services.AddSingleton<IObjectStore>(new AzureBlobObjectStore(objects));

// The silos share one machine, each advertising the loopback address and ports of its own; a deployment advertises each
// pod's own IP address.
builder.UseOrleans(silo => silo
    .Configure<ClusterOptions>(options =>
    {
        options.ClusterId = sample["ClusterId"] ?? "durable-storage-sample";
        options.ServiceId = sample["ServiceId"] ?? "durable-storage-sample";
    })
    .Configure<SiloOptions>(options => options.SiloName = sample["SiloName"] ?? options.SiloName)
    .UseAzureStorageClustering(options => options.TableServiceClient = tables)
    .ConfigureEndpoints(IPAddress.Loopback, sample.GetValue<int>("SiloPort"), sample.GetValue<int>("GatewayPort"))
    .AddSampleJournaling(builder.Configuration));

// Durable Storage's meters (Orleans.Durable.Storage, Orleans.Durable.Storage.Engine.Ledgers, Orleans.Durable.Storage.Engine.Journals, Orleans.Durable.Storage.Engine.Wal, Orleans.Durable.Storage.Engine.Archive.Lsm) and the logs reach
// the Aspire dashboard over OTLP, whose endpoint the AppHost sets.
builder.Logging.AddOpenTelemetry(static logging => logging.IncludeFormattedMessage = true);
var telemetry = builder.Services.AddOpenTelemetry()
    .WithMetrics(static metrics => metrics.AddMeter("Orleans.Durable.Storage*").AddMeter("Microsoft.Orleans"));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    _ = telemetry.UseOtlpExporter();
}

var app = builder.Build();
app.MapGet("/", static () => "GET /grains/{id} reads a grain; PUT /grains/{id}/{key} with a body sets an entry; DELETE /grains/{id}/{key} removes one.");
app.MapGet("/grains/{id}", static (string id, IGrainFactory grains) => grains.GetGrain<IKeyValueGrain>(id).ReadAsync());
app.MapPut("/grains/{id}/{key}", static async (string id, string key, HttpRequest request, IGrainFactory grains) =>
{
    using var body = new StreamReader(request.Body);
    await grains.GetGrain<IKeyValueGrain>(id).SetAsync(key, await body.ReadToEndAsync(request.HttpContext.RequestAborted));
    return Results.NoContent();
});
app.MapDelete("/grains/{id}/{key}", static async (string id, string key, IGrainFactory grains) =>
    await grains.GetGrain<IKeyValueGrain>(id).RemoveAsync(key) ? Results.NoContent() : Results.NotFound());
await app.RunAsync();

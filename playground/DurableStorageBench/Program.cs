using System.Globalization;
using System.Net;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Orleans.Configuration;
using Orleans.Durable.Journaling;
using Orleans.Durable.Journaling.Json;
using Orleans.Durable.Storage;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Bench;
using Orleans.Durable.Storage.CasPaxos.Hosting;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.IoUring;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.RocksDb;
using Orleans.Providers;
using Orleans.Runtime;

var builder = WebApplication.CreateBuilder(args);

var benchOptions = new BenchOptions();
builder.Configuration.GetSection("Bench").Bind(benchOptions);

var mode = benchOptions.ParsedMode;
var siloName = benchOptions.SiloName
    ?? Environment.GetEnvironmentVariable("POD_NAME")
    ?? Environment.GetEnvironmentVariable("HOSTNAME")
    ?? Environment.MachineName;

var casPaxosBenchOptions = new CasPaxosBenchOptions();
builder.Configuration.GetSection("CasPaxosBench").Bind(casPaxosBenchOptions);

var durableNodeOrdinal = builder.Configuration.GetValue<int?>("DurableStorage:Node:Ordinal");
var inferredOrdinal = BenchBootstrapHelpers.InferOrdinal(casPaxosBenchOptions.Ordinal, durableNodeOrdinal, siloName);

List<ResolvedVoter>? resolvedVoters = null;
if (mode == BenchMode.Clog)
{
    resolvedVoters = await BenchBootstrapHelpers.ResolveVotersAsync(
        casPaxosBenchOptions,
        benchOptions.SiloPort,
        CancellationToken.None);
}

var advertisedIp = await BenchBootstrapHelpers.ResolveAdvertisedIpAsync(
    benchOptions,
    resolvedVoters,
    inferredOrdinal,
    CancellationToken.None);

if (inferredOrdinal is null && resolvedVoters is not null)
{
    var localEp = new IPEndPoint(advertisedIp, benchOptions.SiloPort);
    var matched = resolvedVoters.FirstOrDefault(v => v.Endpoint.Equals(localEp));
    if (matched is not null)
    {
        inferredOrdinal = matched.Index;
    }
}

var orleansUseIoUringEnv = Environment.GetEnvironmentVariable("ORLEANS_USE_IO_URING");
var orleansUseIoUring = OperatingSystem.IsLinux()
    && (string.Equals(orleansUseIoUringEnv, "1", StringComparison.Ordinal)
        || (AppContext.TryGetSwitch("Orleans.Connections.Transport.Sockets.UseIoUring", out var ioUringSwitch) && ioUringSwitch));

Console.WriteLine(
    $"[BenchStartup] Mode={mode} SiloName={siloName} Ordinal={inferredOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
    $"AdvertisedIP={advertisedIp} SiloPort={benchOptions.SiloPort} GatewayPort={benchOptions.GatewayPort} HttpPort={benchOptions.HttpPort} " +
    $"ClusterId={benchOptions.ClusterId} ORLEANS_USE_IO_URING={orleansUseIoUringEnv ?? "<unset>"} (active={orleansUseIoUring})");

// Configure Kestrel HTTP port if ASPNETCORE_URLS was not set externally.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{benchOptions.HttpPort}");
}

// Correlated storage lifecycle event log store + ILoggerProvider (Information+ for DurableStorage & CasPaxos).
var eventLogStore = new BenchEventLogStore(siloName);
builder.Logging.AddProvider(new BenchEventLogProvider(eventLogStore));

// Configure Azure Blob and Azure Table clients (DefaultAzureCredential when endpoints are set, connection string fallback).
TokenCredential? credential = null;
TokenCredential GetCredential() => credential ??= new DefaultAzureCredential();

var storage = benchOptions.Storage;
var blobCounterPolicy = new BlobRequestCounterPolicy(
    storage.LsmContainer,
    storage.JobsContainer,
    storage.BaselineJournalsContainer,
    eventLogStore);

var blobClientOptions = AzureArchiveLogStore.ConfigureClientOptions(new BlobClientOptions());
blobClientOptions.AddPolicy(blobCounterPolicy, HttpPipelinePosition.PerRetry);

BlobServiceClient blobServiceClient;
if (!string.IsNullOrWhiteSpace(storage.BlobEndpoint))
{
    blobServiceClient = new BlobServiceClient(new Uri(storage.BlobEndpoint), GetCredential(), blobClientOptions);
}
else if (!string.IsNullOrWhiteSpace(storage.ConnectionString))
{
    blobServiceClient = new BlobServiceClient(storage.ConnectionString, blobClientOptions);
}
else
{
    throw new InvalidOperationException(
        "Configure either Bench:Storage:BlobEndpoint (Managed Identity / DefaultAzureCredential) or Bench:Storage:ConnectionString.");
}

TableServiceClient tableServiceClient;
if (!string.IsNullOrWhiteSpace(storage.TableEndpoint))
{
    tableServiceClient = new TableServiceClient(new Uri(storage.TableEndpoint), GetCredential());
}
else if (!string.IsNullOrWhiteSpace(storage.ConnectionString))
{
    tableServiceClient = new TableServiceClient(storage.ConnectionString);
}
else
{
    throw new InvalidOperationException(
        "Configure either Bench:Storage:TableEndpoint (Managed Identity / DefaultAzureCredential) or Bench:Storage:ConnectionString.");
}

// Optional isolated BlobContainerClient for uploading benchmark JSON reports (never counted by BlobRequestCounterPolicy).
BlobContainerClient? resultsContainerClient = null;
if (!string.IsNullOrWhiteSpace(storage.ResultsBlobEndpoint))
{
    var resultsSvc = new BlobServiceClient(new Uri(storage.ResultsBlobEndpoint), GetCredential());
    resultsContainerClient = resultsSvc.GetBlobContainerClient(storage.ResultsContainer);
}
else if (!string.IsNullOrWhiteSpace(storage.ResultsConnectionString))
{
    var resultsSvc = new BlobServiceClient(storage.ResultsConnectionString);
    resultsContainerClient = resultsSvc.GetBlobContainerClient(storage.ResultsContainer);
}

// Best-effort container creation on startup when enabled (ignored if data-plane-only RBAC already provisioned them).
if (storage.CreateContainersIfNotExists)
{
    var containersToEnsure = new List<BlobContainerClient>
    {
        blobServiceClient.GetBlobContainerClient(storage.JobsContainer),
    };
    if (mode == BenchMode.Clog)
    {
        containersToEnsure.Add(blobServiceClient.GetBlobContainerClient(storage.LsmContainer));
    }
    else
    {
        containersToEnsure.Add(blobServiceClient.GetBlobContainerClient(storage.BaselineJournalsContainer));
    }

    if (resultsContainerClient is not null)
    {
        containersToEnsure.Add(resultsContainerClient);
    }

    foreach (var container in containersToEnsure)
    {
        try
        {
            _ = await container.CreateIfNotExistsAsync();
        }
        catch (RequestFailedException ex) when (ex.Status is 403 or 409)
        {
            Console.WriteLine($"[BenchStartup] Container '{container.Name}' CreateIfNotExists returned {ex.Status}; assuming pre-provisioned.");
        }
    }
}

// Select WAL storage for Clog mode: IoUringWalStorage on Linux 6.1+ with PortableWalStorage fallback,
// wrapped in ObservingWalStorage to log correlated WAL file creation/deletion/checkpoint events.
WalStorage? walStorage = null;
var walStorageType = "None";
if (mode == BenchMode.Clog)
{
    WalStorage rawWal;
    if (OperatingSystem.IsLinux())
    {
        try
        {
            rawWal = new IoUringWalStorage();
            walStorageType = nameof(IoUringWalStorage);
            Console.WriteLine("[BenchStartup] WAL storage active: IoUringWalStorage (Linux io_uring O_DIRECT).");
        }
        catch (PlatformNotSupportedException ex)
        {
            rawWal = new PortableWalStorage();
            walStorageType = nameof(PortableWalStorage);
            Console.WriteLine($"[BenchStartup] IoUringWalStorage unavailable ({ex.Message}); falling back to PortableWalStorage.");
        }
    }
    else
    {
        rawWal = new PortableWalStorage();
        walStorageType = nameof(PortableWalStorage);
        Console.WriteLine("[BenchStartup] WAL storage active: PortableWalStorage (non-Linux host).");
    }

    walStorage = new ObservingWalStorage(rawWal, eventLogStore);
}

var runtimeInfo = new BenchRuntimeInfo(
    Mode: mode,
    ClusterId: benchOptions.ClusterId,
    ServiceId: benchOptions.ServiceId,
    SiloName: siloName,
    HttpPort: benchOptions.HttpPort,
    WalStorageType: walStorageType,
    OrleansUseIoUring: orleansUseIoUring,
    LsmContainer: storage.LsmContainer,
    JobsContainer: storage.JobsContainer,
    BaselineJournalsContainer: storage.BaselineJournalsContainer,
    Ordinal: inferredOrdinal,
    ResolvedVoters: resolvedVoters?.Select(v => $"{v.Id}={v.Endpoint}").ToList() ?? []);

builder.Services.AddSingleton(runtimeInfo);
builder.Services.AddSingleton(eventLogStore);
builder.Services.AddSingleton(blobCounterPolicy);
builder.Services.AddSingleton<BenchJobCounters>();
builder.Services.AddSingleton<SiloTelemetrySampler>();
builder.Services.AddHostedService(static sp => sp.GetRequiredService<SiloTelemetrySampler>());
builder.Services.AddSingleton<ClusterTelemetryAggregator>();
builder.Services.AddSingleton(sp => new BenchResultsUploader(resultsContainerClient, sp.GetRequiredService<ILogger<BenchResultsUploader>>()));
builder.Services.AddHttpClient("bench-peer", static client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<BenchRunner>();

var clusteringTableName = BenchBootstrapHelpers.SanitizeAzureTableName(storage.ClusteringTableName, benchOptions.ClusterId);

builder.UseOrleans(silo =>
{
    silo.Configure<ClusterOptions>(options =>
        {
            options.ClusterId = benchOptions.ClusterId;
            options.ServiceId = benchOptions.ServiceId;
        })
        .Configure<SiloOptions>(options => options.SiloName = siloName)
        .Configure<ClusterMembershipOptions>(options =>
        {
            builder.Configuration.GetSection("Bench:Membership").Bind(options);
            Console.WriteLine(
                $"[BenchStartup] ClusterMembershipOptions: ProbeTimeout={options.ProbeTimeout} MinProbeTimeout={options.MinProbeTimeout} " +
                $"MaxProbeTimeout={options.MaxProbeTimeout} NumMissedProbesLimit={options.NumMissedProbesLimit} " +
                $"NumVotesForDeathDeclaration={options.NumVotesForDeathDeclaration} TableRefreshTimeout={options.TableRefreshTimeout} " +
                $"IAmAliveTablePublishTimeout={options.IAmAliveTablePublishTimeout}");
        })
        .ConfigureEndpoints(advertisedIp, benchOptions.SiloPort, benchOptions.GatewayPort)
        .UseAzureStorageClustering(options =>
        {
            options.TableServiceClient = tableServiceClient;
            options.TableName = clusteringTableName;
        })
        .AddPlacementDirector<SiloKeyPlacement, SiloKeyPlacementDirector>()
        .AddJournaling()
        .UseJsonJournalFormat(BenchJsonContext.Default);

    // Durable Jobs backing shard store (shared by both Clog and Baseline modes).
    silo.AddAzureBlobJournalStorage("jobs", options =>
    {
        options.BlobServiceClient = blobServiceClient;
        options.ContainerName = storage.JobsContainer;
    });

    silo.UseJournaledDurableJobs(options =>
    {
        options.ActiveProviderName = "jobs";
        options.ConcurrencySlowStartEnabled = false;
        options.ShardCheckInterval = TimeSpan.FromSeconds(15);
        builder.Configuration.GetSection("Bench:DurableJobs").Bind(options);
    });

    if (mode == BenchMode.Clog)
    {
        var ordinal = inferredOrdinal ?? throw new InvalidOperationException(
            "Could not determine silo ordinal for Clog/CasPaxos mode. Set POD_NAME (e.g. silo-0), CasPaxosBench:Ordinal, or DurableStorage:Node:Ordinal.");
        var proposerId = casPaxosBenchOptions.ProposerId ?? (ordinal + 1);
        var consensusDir = Path.GetFullPath(casPaxosBenchOptions.ConsensusDir);
        var proposerDb = Path.Combine(consensusDir, $"proposer-{ordinal}.db");

        var nodeRoot = builder.Configuration["DurableStorage:Node:Root"];
        if (string.IsNullOrWhiteSpace(nodeRoot))
        {
            nodeRoot = "/mnt/nvme/clog";
        }

        nodeRoot = Path.GetFullPath(nodeRoot);
        Directory.CreateDirectory(nodeRoot);
        Directory.CreateDirectory(consensusDir);

        Console.WriteLine(
            $"[BenchStartup] CasPaxos ProposerId={proposerId} ProposerDb={proposerDb} NodeRoot={nodeRoot} Voters=[{string.Join(", ", resolvedVoters!.Select(v => $"{v.Id}@{v.Endpoint}"))}]");

        silo.Services.AddSingleton<WalStorage>(walStorage!);
        silo.Services.AddSingleton<IObjectStore>(new AzureBlobObjectStore(blobServiceClient.GetBlobContainerClient(storage.LsmContainer)));

        silo.AddCasPaxosMetaStore(options => options.Configure(cas =>
        {
            // Optional tuning overrides from CasPaxos section.
            builder.Configuration.GetSection("CasPaxos").Bind(cas);
            cas.Voters.Clear();
            foreach (var v in resolvedVoters!)
            {
                cas.Voters.Add(new CasPaxosVoterOptions
                {
                    Id = v.Id,
                    Endpoint = v.Endpoint.ToString(),
                    Database = v.DatabasePath,
                });
            }

            cas.ProposerId = proposerId;
            cas.ProposerDatabase = proposerDb;
        }));

        silo.AddDurableJournalStorage(options => options.Configure(ds =>
        {
            // Optimal NVMe defaults for 3x Standard_L8s_v3 (8 vCPU, 64 GiB RAM, 1.92 TB local NVMe per node).
            // All settings can still be overridden via DurableStorage:* configuration/env vars.
            ds.Node.Cluster = BenchBootstrapHelpers.DeterministicClusterGuid(benchOptions.ClusterId);
            ds.Node.Root = nodeRoot;
            ds.Node.Ordinal = ordinal;
            ds.Node.ArchiveStore = ArchiveStoreKind.Lsm;
            ds.Node.ArchiveStorePrefix = BenchBootstrapHelpers.SanitizeArchivePrefix(benchOptions.ClusterId);

            ds.Node.WalFileSize = 1L << 30;                 // 1 GiB preallocated WAL files
            ds.Node.PreallocateWalFiles = true;
            ds.Node.WalRetentionBytes = 64L << 30;          // 64 GiB retention (many un-drained WAL files)
            ds.Node.DiskBudgetBytes = 512L << 30;           // 512 GiB disk budget
            ds.Node.MaxSegmentBytes = 128L << 20;           // 128 MiB segments
            ds.Node.MaxSegmentAge = TimeSpan.FromHours(1);
            ds.Node.MaxProposalBytes = 128L << 20;          // 128 MiB proposal window
            ds.Node.Lsm.DrainMemoryBytes = 512L << 20;      // 512 MiB drain memory
            ds.Node.Lsm.BlockCacheBytes = 512L << 20;       // 512 MiB LSM block cache budget
            ds.Node.MaxReplicaCalls = 256;
            ds.Node.MaxReplicaCallBytes = 256L << 20;
            ds.Node.MaxNodeCalls = 128;
            ds.Node.MaxNodeCallBytes = 128L << 20;
            ds.Node.MaxForwardCallBytes = 128L << 20;

            ds.Control.Replicas = resolvedVoters!.Count;
            ds.Control.LedgersPerNode = 21; // 63 open ledgers across 3 nodes (~32.8% / 32.8% / 34.4% dyadic keyspace balance)
            ds.Control.SequencerWindow = 64;
            ds.Control.MaxConcurrentDrains = 8;

            builder.Configuration.GetSection("DurableStorage").Bind(ds);
            if (ds.Node.Cluster == Guid.Empty)
            {
                ds.Node.Cluster = BenchBootstrapHelpers.DeterministicClusterGuid(benchOptions.ClusterId);
            }

            if (string.IsNullOrWhiteSpace(ds.Node.Root))
            {
                ds.Node.Root = nodeRoot;
            }

            ds.Node.Ordinal ??= ordinal;
            if (ds.Node.ArchiveStore == default)
            {
                ds.Node.ArchiveStore = ArchiveStoreKind.Lsm;
            }

            Console.WriteLine(
                $"[BenchStartup] DurableStorageOptions: WalFileSize={ds.Node.WalFileSize / (1024 * 1024)}MiB Prealloc={ds.Node.PreallocateWalFiles} " +
                $"WalRetention={ds.Node.WalRetentionBytes / (1024L * 1024 * 1024)}GiB MaxSegment={ds.Node.MaxSegmentBytes / (1024 * 1024)}MiB " +
                $"MaxConcurrentHistoryReads={ds.Node.Journals.MaxConcurrentHistoryReads} LedgersPerNode={ds.Control.LedgersPerNode} " +
                $"MaxConcurrentTransitions={ds.Control.MaxConcurrentTransitions} MaxConcurrentHandOffs={ds.Control.MaxConcurrentHandOffs} " +
                $"SequencerWindow={ds.Control.SequencerWindow} MaxConcurrentDrains={ds.Control.MaxConcurrentDrains}");
        }));

        var rocksDbWriteBufferBytes = Math.Max(16L << 20, benchOptions.RocksDbWriteBufferBytes);
        Console.WriteLine($"[BenchStartup] RocksDB derived index write buffer budget: {rocksDbWriteBufferBytes / (1024 * 1024)} MiB (FlushInterval=Infinite, size-based flushing).");
        silo.AddDurableJournalIndex(root => new RocksDbJournalIndex(Path.Combine(root, "index"), rocksDbWriteBufferBytes));
    }
    else
    {
        silo.AddAzureBlobJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, options =>
        {
            options.BlobServiceClient = blobServiceClient;
            options.ContainerName = storage.BaselineJournalsContainer;
        });

        silo.AddPlacementDirector<JournalPlacement, FallbackJournalPlacementDirector>();
    }
});

var app = builder.Build();

app.MapGet("/healthz", static () => Results.Ok(new { status = "ok" }));

app.MapGet("/status", static async (BenchRunner runner, CancellationToken ct) =>
    Results.Ok(await runner.GetStatusAsync(ct)));

app.MapGet("/metrics/blob", static async (
    BlobRequestCounterPolicy localPolicy,
    ILocalSiloDetails localSilo,
    ClusterTelemetryAggregator aggregator,
    CancellationToken ct) =>
{
    var cluster = await aggregator.SnapshotAsync(runTag: null, ct);
    return Results.Ok(new
    {
        silo = localSilo.SiloAddress.ToParsableString(),
        local = localPolicy.Snapshot(),
        cluster = cluster.AggregateBlobs,
        bySilo = cluster.BlobsBySilo,
        unreachableSilos = cluster.UnreachableSilos,
    });
});

app.MapGet("/telemetry", static (
    string? sinceUtc,
    ILocalSiloDetails localSilo,
    SiloTelemetrySampler sampler,
    BenchEventLogStore eventLog) =>
{
    DateTimeOffset? since = null;
    if (!string.IsNullOrWhiteSpace(sinceUtc)
        && DateTimeOffset.TryParse(sinceUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
    {
        since = parsed;
    }

    var samples = sampler.GetSamples(since);
    var events = eventLog.GetEvents(since);
    return Results.Ok(new
    {
        siloAddress = localSilo.SiloAddress.ToParsableString(),
        siloName = localSilo.Name,
        sampleCount = samples.Count,
        samples,
        eventCount = events.Count,
        events,
    });
});

app.MapPost("/bench/roll-and-drain", static async (
    HttpRequest httpRequest,
    BenchRunner runner,
    CancellationToken ct) =>
{
    RollAndDrainRequest? req = null;
    if (httpRequest.ContentLength is > 0)
    {
        req = await httpRequest.ReadFromJsonAsync<RollAndDrainRequest>(ct);
    }

    var timeout = TimeSpan.FromSeconds(Math.Max(10, req?.TimeoutSeconds ?? 180));
    var result = await runner.RollAndDrainAsync(timeout, ct);
    return Results.Ok(result);
});

app.MapPost("/bench/run", static async (
    BenchRunRequest request,
    BenchRunner runner,
    CancellationToken ct) =>
{
    try
    {
        var result = await runner.RunAsync(request, ct);
        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/bench/run-local", static async (
    LocalBenchRequest request,
    BenchRunner runner,
    CancellationToken ct) =>
{
    try
    {
        var result = await runner.RunLocalAsync(request, ct);
        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

await app.RunAsync();

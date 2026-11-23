using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Durable.Journaling;
using Orleans.Durable.Journaling.Json;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Interop.Tests;

/// <summary>
/// A plain Orleans deployment's journaling: Orleans' own <c>AzureBlobJournalStorage</c>, registered through
/// <c>AddAzureBlobJournalStorage</c> with Orleans' defaults but the journal format, never started as a silo.
/// </summary>
/// <remarks>
/// Over a container the deployment is given, its provider takes that container through a container factory, its public
/// extension point, so no silo lifecycle creates it; over a service client, it keeps Orleans' default factory, and
/// <see cref="InitializeAsync"/> runs the initialisation a silo's lifecycle runs at <c>RuntimeInitialize</c>, which creates
/// the container if absent. Each read is a fresh storage instance, as a new activation's.
/// </remarks>
internal sealed class PlainOrleans : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private PlainOrleans(ServiceProvider services)
    {
        _services = services;
        Provider = services.GetRequiredService<IJournalStorageProvider>();
        Format = services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey;
    }

    /// <summary>Gets Orleans' journal storage provider.</summary>
    public IJournalStorageProvider Provider { get; }

    /// <summary>Gets the catalog the provider keeps, which lists journals by their write-ahead blobs.</summary>
    public IJournalStorageCatalog Catalog => (IJournalStorageCatalog)Provider;

    /// <summary>Gets the deployment's journal format key, which Clog's store records as its <c>format</c>.</summary>
    public string Format { get; }

    /// <summary>Gets the deployment's services, for its state managers.</summary>
    public IServiceProvider Services => _services;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Returns a deployment whose provider reads <paramref name="container"/>, in <paramref name="format"/>; under the store
    /// prefix <paramref name="prefix"/>, when given, by naming each journal's blobs <c>{prefix}/wal/{id}</c> and
    /// <c>{prefix}/checkpoints/{id}/…</c> through the provider's own naming options, as a deployment reading a cluster's
    /// journals under its prefix configures it (orleans.md §5.13).
    /// </summary>
    public static PlainOrleans Over(BlobContainerClient container, string? format = null, string? prefix = null) =>
        With(format, silo => silo.AddAzureBlobJournalStorage(options =>
        {
            options.ContainerName = container.Name;
            options.BuildContainerFactory = (_, _) => new OneContainer(container);
            if (prefix is not null)
            {
                options.GetWalBlobName = journal => $"{prefix}/wal/{journal.Value}";
                options.GetCheckpointBlobName = (journal, snapshot) => $"{prefix}/checkpoints/{journal.Value}/{snapshot}";
            }
        }));

    /// <summary>Returns a deployment over <paramref name="service"/> with Orleans' default container factory.</summary>
    public static PlainOrleans OverService(BlobServiceClient service, string container) =>
        With(null, silo => silo.AddAzureBlobJournalStorage(options =>
        {
            options.ContainerName = container;
            options.BlobServiceClient = service;
        }));

    /// <summary>Runs the initialisation the silo's lifecycle runs for the provider, whose default factory creates its container.</summary>
    public async Task InitializeAsync()
    {
        var lifecycle = new SiloLifecycle();
        ((ILifecycleParticipant<ISiloLifecycle>)Provider).Participate(lifecycle);
        await lifecycle.StartAsync(Token);
    }

    /// <summary>Reads the journal <paramref name="id"/> through a fresh storage instance, as a new activation does.</summary>
    public async Task<OrleansRead> ReadAsync(string id)
    {
        var storage = Provider.CreateStorage(new JournalId(id));
        var collector = new Collector();
        await storage.ReadAsync(collector, Token);
        return new OrleansRead(collector.Content, collector.Completed ?? throw new InvalidOperationException("Orleans' read did not complete."), await storage.GetMetadataAsync(Token));
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    /// <summary>
    /// Returns a deployment whose journal storage <paramref name="configure"/> registers, in <paramref name="format"/>, or
    /// Orleans' default format when null; its scopes' durable states register with the manager their
    /// <see cref="ManagerHolder"/> holds.
    /// </summary>
    public static PlainOrleans With(string? format, Action<ISiloBuilder> configure)
    {
        var services = new ServiceCollection();
        _ = services.AddScoped<ManagerHolder>();
        _ = services.AddScoped<IJournaledStateManager>(static provider => provider.GetRequiredService<ManagerHolder>().Manager);
        _ = services.AddOrleans(configure);
        _ = services.Configure<JsonJournalOptions>(static options => options.AddTypeInfoResolver(DurableStatesJsonContext.Default));
        if (format is not null)
        {
            _ = services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = format);
        }

        return new PlainOrleans(services.BuildServiceProvider());
    }

    /// <summary>Orleans' container factory, its public extension point, handing it one container.</summary>
    private sealed class OneContainer(BlobContainerClient container) : IBlobContainerFactory
    {
        public BlobContainerClient GetBlobContainerClient(JournalId journalId) => container;

        public Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A silo lifecycle which runs its observers' starts in stage order, as a starting silo does.</summary>
    private sealed class SiloLifecycle : ISiloLifecycle
    {
        private readonly List<(int Stage, ILifecycleObserver Observer)> _observers = [];

        public int HighestCompletedStage { get; private set; }

        public int LowestStoppedStage { get; private set; }

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            _observers.Add((stage, observer));
            return new Unsubscriber();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var (stage, observer) in _observers.OrderBy(static entry => entry.Stage))
            {
                await observer.OnStart(cancellationToken);
                HighestCompletedStage = stage;
            }
        }

        private sealed class Unsubscriber : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

/// <summary>
/// The JSON metadata of the durable states' payloads, and of Orleans' state directory, its identifiers, which Orleans' JSON
/// format takes from a source-generated context.
/// </summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(DateTime))]
internal sealed partial class DurableStatesJsonContext : JsonSerializerContext;

/// <summary>Holds the standalone state manager a scope's durable states register with.</summary>
internal sealed class ManagerHolder
{
    public IJournaledStateManager Manager { get; set; } = null!;
}

/// <summary>What plain Orleans read: the journal's content, the metadata its read completed with, and its metadata, null when absent.</summary>
internal sealed record OrleansRead(byte[] Content, IJournalMetadata Completed, IJournalMetadata? Metadata);

/// <summary>Collects a read's bytes, which Orleans hands over in chunks, and the metadata it completes with.</summary>
internal sealed class Collector : IJournalStorageConsumer
{
    private readonly System.Buffers.ArrayBufferWriter<byte> _content = new();

    public byte[] Content => _content.WrittenSpan.ToArray();

    public IJournalMetadata? Completed { get; private set; }

    public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
    {
        Assert.Null(Completed);
        var length = buffer.Length;
        if (length > 0)
        {
            buffer.Read(_content.GetSpan(length)[..length]);
            _content.Advance(length);
        }

        if (buffer.IsCompleted)
        {
            Completed = metadata;
        }
    }
}

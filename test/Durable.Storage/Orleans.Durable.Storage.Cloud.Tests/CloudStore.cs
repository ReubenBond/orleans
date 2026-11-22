using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.GoogleCloud.Archive;
using Orleans.Durable.Storage.S3.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// One test's object store over a service's endpoint, under a prefix no other test uses, with the transport its client's
/// requests pass through.
/// </summary>
/// <remarks>
/// Disposing the store checks that the client disposed every download it received.
/// </remarks>
internal sealed class CloudStore : IAsyncDisposable
{
    private readonly IDisposable? _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudStore"/> class over <paramref name="emulator"/>'s kind of
    /// service at <paramref name="endpoint"/>, under <paramref name="prefix"/> or a fresh one.
    /// </summary>
    internal CloudStore(Emulator emulator, Uri endpoint, string? prefix = null)
    {
        Prefix = prefix ?? $"t{Guid.NewGuid():N}/";
        Transport = new TransportDouble();
        switch (emulator)
        {
            case Emulator.Azurite:
                var azure = new AzureBlobObjectStore(CloudEmulators.AzureContainer(endpoint, Transport), Prefix);
                Store = azure;
                Appendable = azure;
                break;
            case Emulator.LocalStack:
                var s3 = CloudClients.S3Client(endpoint, Transport);
                _client = s3;
                Store = new S3ObjectStore(s3, CloudEmulators.ContainerName, Prefix);
                break;
            case Emulator.FakeGcsServer or Emulator.GcsTestbench:
                var storage = CloudClients.StorageClient(endpoint, Transport);
                _client = storage;
                Store = new GoogleCloudStorageObjectStore(storage, CloudEmulators.ContainerName, Prefix);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(emulator));
        }
    }

    /// <summary>
    /// Gets the store.
    /// </summary>
    internal IObjectStore Store { get; }

    /// <summary>
    /// Gets the appendable view of the store's objects, for a service which has appendable objects: Azure's append blobs.
    /// </summary>
    internal IAppendableObjectStore? Appendable { get; }

    /// <summary>
    /// Gets the transport beneath the store's client.
    /// </summary>
    internal TransportDouble Transport { get; }

    /// <summary>
    /// Gets the prefix of the store's objects.
    /// </summary>
    internal string Prefix { get; }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        var open = Transport.OpenResponses;
        _client?.Dispose();
        Transport.Dispose();
        Assert.Equal(0, open);
        return ValueTask.CompletedTask;
    }
}

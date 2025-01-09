using Azure;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs.Models;
using System.Runtime.CompilerServices;
using Azure.Storage.Sas;
using Orleans.Serialization.Buffers;
using Orleans.Runtime;
using Azure.Storage.Blobs;
using Azure.Storage;
using Azure.Core;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Microsoft.Extensions.Logging;

namespace Orleans.Journaling;

public interface IStateMachineStorageProvider
{
    IStateMachineStorage Create(IGrainContext grainContext);
}

public class AzureAppendBlobStateMachineStorageOptions
{
    /// <summary>
    /// Container name where state machine state is stored.
    /// </summary>
    public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;
    public const string DEFAULT_CONTAINER_NAME = "state";

    public Func<GrainId, string> GetBlobName { get; set; } = DefaultGetBlobName;

    private static readonly Func<GrainId, string> DefaultGetBlobName = static (GrainId grainId) => $"{grainId}.bin";

    /// <summary>
    /// Options to be used when configuring the blob storage client, or <see langword="null"/> to use the default options.
    /// </summary>
    public BlobClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// The optional delegate used to create a <see cref="BlobServiceClient"/> instance.
    /// </summary>
    internal Func<CancellationToken, Task<BlobServiceClient>>? CreateClient { get; private set; }

    /// <summary>
    /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
    public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

    /// <summary>
    /// A function for building container factory instances.
    /// </summary>
    public Func<IServiceProvider, AzureAppendBlobStateMachineStorageOptions, IBlobContainerFactory> BuildContainerFactory { get; set; }
        = static (provider, options) => new DefaultBlobContainerFactory(options);

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using a connection string.
    /// </summary>
    public void ConfigureBlobServiceClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(connectionString, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI.
    /// </summary>
    public void ConfigureBlobServiceClient(Uri serviceUri)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(serviceUri, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using the provided callback.
    /// </summary>
    public void ConfigureBlobServiceClient(Func<CancellationToken, Task<BlobServiceClient>> createClientCallback)
    {
        CreateClient = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="TokenCredential"/>.
    /// </summary>
    public void ConfigureBlobServiceClient(Uri serviceUri, TokenCredential tokenCredential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(tokenCredential);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(serviceUri, tokenCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="AzureSasCredential"/>.
    /// </summary>
    public void ConfigureBlobServiceClient(Uri serviceUri, AzureSasCredential azureSasCredential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(azureSasCredential);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(serviceUri, azureSasCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="StorageSharedKeyCredential"/>.
    /// </summary>
    public void ConfigureBlobServiceClient(Uri serviceUri, StorageSharedKeyCredential sharedKeyCredential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(sharedKeyCredential);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(serviceUri, sharedKeyCredential, ClientOptions));
    }
}

/// <summary>
/// A factory for building container clients for blob storage using GrainId
/// </summary>
public interface IBlobContainerFactory
{
    /// <summary>
    /// Gets the container which should be used for the specified grain.
    /// </summary>
    /// <param name="grainId">The grain id</param>
    /// <returns>A configured blob client</returns>
    public BlobContainerClient GetBlobContainerClient(GrainId grainId);

    /// <summary>
    /// Initialize any required dependencies using the provided client and options.
    /// </summary>
    /// <param name="client">The connected blob client</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken);
}

/// <summary>
/// A default blob container factory that uses the default container name.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DefaultBlobContainerFactory"/> class.
/// </remarks>
/// <param name="options">The blob storage options</param>
internal class DefaultBlobContainerFactory(AzureAppendBlobStateMachineStorageOptions options) : IBlobContainerFactory
{
    private BlobContainerClient _defaultContainer = null!;

    /// <inheritdoc/>
    public BlobContainerClient GetBlobContainerClient(GrainId grainId) => _defaultContainer;

    /// <inheritdoc/>
    public async Task InitializeAsync(BlobServiceClient client, CancellationToken cancellationToken)
    {
        _defaultContainer = client.GetBlobContainerClient(options.ContainerName);
        await _defaultContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }
}

public static class AzureBlobStorageHostingExtensions
{
    public static ISiloBuilder AddAzureAppendBlobStateMachineStorage(this ISiloBuilder builder, Action<OptionsBuilder<AzureAppendBlobStateMachineStorageOptions>> configure)
    {
        builder.AddStateMachineStorage();

        var services = builder.Services;

        var options = builder.Services.AddOptions<AzureAppendBlobStateMachineStorageOptions>();
        configure?.Invoke(options);

        if (services.Any(service => service.ServiceType.Equals(typeof(AzureAppendBlobStateMachineStorageProvider))))
        {
            return builder;
        }

        builder.Services.AddSingleton<AzureAppendBlobStateMachineStorageProvider>();
        builder.Services.AddFromExisting<IStateMachineStorageProvider, AzureAppendBlobStateMachineStorageProvider>();
        builder.Services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureAppendBlobStateMachineStorageProvider>();
        return builder;
    }
}

public sealed class AzureAppendBlobStateMachineStorageProvider(
    IOptions<AzureAppendBlobStateMachineStorageOptions> options,
    IServiceProvider serviceProvider,
    ILogger<AzureAppendBlobLogStorage> logger) : IStateMachineStorageProvider, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IBlobContainerFactory _containerFactory = options.Value.BuildContainerFactory(serviceProvider, options.Value);
    private readonly AzureAppendBlobStateMachineStorageOptions _options = options.Value;

    private async Task Initialize(CancellationToken cancellationToken)
    {
        var client = await _options.CreateClient!(cancellationToken);
        await _containerFactory.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public IStateMachineStorage Create(IGrainContext grainContext)
    {
        var container = _containerFactory.GetBlobContainerClient(grainContext.GrainId);
        var blobName = _options.GetBlobName(grainContext.GrainId);
        var blobClient = container.GetAppendBlobClient(blobName);
        return new AzureAppendBlobLogStorage(blobClient, logger);
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureAppendBlobStateMachineStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
    }
}

public sealed class AzureAppendBlobLogStorage : IStateMachineStorage
{
    private static readonly AppendBlobCreateOptions CreateOptions = new() { Conditions = new() { IfNoneMatch = ETag.All } };
    private readonly AppendBlobClient _client;
    private readonly ILogger<AzureAppendBlobLogStorage> _logger;
    private readonly LogExtentBuilder.ReadOnlyStream _stream;
    private readonly AppendBlobAppendBlockOptions _appendOptions;
    private bool _exists;
    private int _numBlocks;

    public bool IsCompactionRequested => _numBlocks > 10;

    public AzureAppendBlobLogStorage(AppendBlobClient client, ILogger<AzureAppendBlobLogStorage> logger)
    {
        _client = client;
        _logger = logger;
        _stream = new();

        // For the first request, if we have not performed a read yet, we want to guard against clobbering an existing blob.
        _appendOptions = new AppendBlobAppendBlockOptions() { Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All } };
    }

    public async ValueTask AppendAsync(LogExtentBuilder value, CancellationToken cancellationToken)
    {
        if (!_exists)
        {
            var response = await _client.CreateAsync(CreateOptions, cancellationToken);
            _appendOptions.Conditions.IfNoneMatch = default;
            _appendOptions.Conditions.IfMatch = response.Value.ETag;
            _exists = true;
        }

        _stream.SetBuilder(value);
        var result = await _client.AppendBlockAsync(_stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var length = value.Length;
            _logger.LogDebug("Appended {Length} bytes to blob \"{ContainerName}/{BlobName}\"", length, _client.BlobContainerName, _client.Name);
        }

        _stream.Reset();
        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.ETag;
        _numBlocks = result.Value.BlobCommittedBlockCount;
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        var conditions = new BlobRequestConditions { IfMatch = _appendOptions.Conditions.IfMatch };
        await _client.DeleteAsync(conditions: conditions, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Expect no blob to have been created when we append to it.
        _appendOptions.Conditions.IfNoneMatch = ETag.All;
        _appendOptions.Conditions.IfMatch = default;
        _numBlocks = 0;
    }

    public async IAsyncEnumerable<LogExtent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Response<BlobDownloadStreamingResult> result;
        try
        {
            // If the blob was not newly created, then download the blob.
            result = await _client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            _exists = false;
            yield break;
        }

        // If the blob has a size of zero, check for a snapshot and restore the blob from the snapshot if one exists.
        if (result.Value.Details.ContentLength == 0)
        {
            if (result.Value.Details.Metadata.TryGetValue("snapshot", out var snapshot) && snapshot is { Length: > 0 })
            {
                result = await CopyFromSnapshotAsync(result.Value.Details.ETag, snapshot, cancellationToken).ConfigureAwait(false);
            }
        }

        _numBlocks = result.Value.Details.BlobCommittedBlockCount;
        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.Details.ETag;
        _exists = true;

        // Read everything into a single log segment. We could change this to read in chunks,
        // yielding when the stream does not return synchronously, if we wanted to support larger state machines.
        var rawStream = result.Value.Content;
        using var buffer = new ArcBufferWriter();
        while (true)
        {
            var mem = buffer.GetMemory();
            var bytesRead = await rawStream.ReadAsync(mem, cancellationToken);
            if (bytesRead == 0)
            {
                if (buffer.Length > 0)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        var length = buffer.Length;
                        _logger.LogDebug("Read {Length} bytes from blob \"{ContainerName}/{BlobName}\"", length, _client.BlobContainerName, _client.Name);
                    }

                    yield return new LogExtent(buffer.ConsumeSlice(buffer.Length));
                }

                yield break;
            }

            buffer.AdvanceWriter(bytesRead);
        }
    }

    private async Task<Response<BlobDownloadStreamingResult>> CopyFromSnapshotAsync(ETag eTag, string snapshotDetail, CancellationToken cancellationToken)
    {
        // Read snapshot and append it to the blob. 
        var snapshot = _client.WithSnapshot(snapshotDetail);
        var uri = snapshot.GenerateSasUri(permissions: BlobSasPermissions.Read, expiresOn: DateTimeOffset.UtcNow.AddHours(1));
        var copyResult = await _client.SyncCopyFromUriAsync(
            uri,
            new BlobCopyFromUriOptions { DestinationConditions = new BlobRequestConditions { IfNoneMatch = eTag } },
            cancellationToken).ConfigureAwait(false);
        if (copyResult.Value.CopyStatus is not CopyStatus.Success)
        {
            throw new InvalidOperationException($"Copy did not complete successfully. Status: {copyResult.Value.CopyStatus}");
        }

        var result = await _client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _exists = true;
        return result;
    }

    public async ValueTask ReplaceAsync(LogExtentBuilder value, CancellationToken cancellationToken)
    {
        // Create a snapshot of the blob for recovery purposes.
        var blobSnapshot = await _client.CreateSnapshotAsync(conditions: _appendOptions.Conditions, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Open the blob for writing, overwriting existing contents.
        var createOptions = new AppendBlobCreateOptions()
        {
            Conditions = _appendOptions.Conditions,
            Metadata = new Dictionary<string, string> { ["snapshot"] = blobSnapshot.Value.Snapshot },
        };
        var createResult = await _client.CreateAsync(createOptions, cancellationToken).ConfigureAwait(false);
        _appendOptions.Conditions.IfMatch = createResult.Value.ETag;
        _appendOptions.Conditions.IfNoneMatch = default;

        // Write the state machine snapshot.
        _stream.SetBuilder(value);
        var result = await _client.AppendBlockAsync(_stream, _appendOptions, cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var length = value.Length;
            _logger.LogDebug("Replaced blob \"{ContainerName}/{BlobName}\", writing {Length} bytes", _client.BlobContainerName, _client.Name, length);
        }

        _stream.Reset();
        _appendOptions.Conditions.IfNoneMatch = default;
        _appendOptions.Conditions.IfMatch = result.Value.ETag;
        _numBlocks = result.Value.BlobCommittedBlockCount;

        // Delete the blob snapshot.
        await _client.WithSnapshot(blobSnapshot.Value.Snapshot).DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

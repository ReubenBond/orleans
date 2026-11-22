using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Archive.Logs;

namespace Orleans.Durable.Storage.AzureStorage.Archive;

/// <summary>
/// Registers archive logs over one Azure Blob container (orleans.md §5.13, archive-logs.md §3): the
/// <see cref="IArchiveLogContainer"/>, <see cref="AzureArchiveLogContainer"/>, which opens the keys' logs and Clog's
/// objects under the cluster's store prefix, both over one client of the container, which is how both lie in one
/// container, as <c>LogArchiveStore</c>'s construction contract asks. The composition opens them with its store prefix and
/// its journal format, so neither is a setting of its own here. The client's options are
/// <see cref="AzureArchiveLogStore.ConfigureClientOptions"/>'s, at the service version the composition pins, beside the
/// SDK's retries, the one retry layer of each request.
/// </summary>
public static class AzureArchiveLogServiceCollectionExtensions
{
    /// <summary>
    /// Registers archive logs over the container at <paramref name="container"/>, authenticated with
    /// <paramref name="credential"/>.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="container">The container's URI.</param>
    /// <param name="credential">The credential the container's client authenticates with.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddAzureArchiveLogs(this IServiceCollection services, Uri container, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(credential);
        return services.AddAzureArchiveLogs(options => new BlobContainerClient(container, credential, options));
    }

    /// <summary>
    /// Registers archive logs over the container client <paramref name="createContainer"/> builds with the options it is
    /// given, which it must keep, and its own authentication: a shared access signature or a shared key, for example.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="createContainer">Builds the container's client with the options it is given.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddAzureArchiveLogs(this IServiceCollection services, Func<BlobClientOptions, BlobContainerClient> createContainer)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(createContainer);
        return services.AddSingleton<IArchiveLogContainer>(new AzureArchiveLogContainer(() => createContainer(AzureArchiveLogStore.ConfigureClientOptions(new BlobClientOptions(AzureBlobRequests.PinnedServiceVersion)))));
    }
}

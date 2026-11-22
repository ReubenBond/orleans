using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Durable.Journaling;
using Orleans.Runtime;

namespace Orleans.Durable.Jobs;

internal sealed record DurableJobsJournalProvider(
    string Name,
    IJournalStorageProvider Storage,
    IJournalStorageCatalog Catalog,
    IJournaledStateManagerFactory Factory);

internal sealed class DurableJobsJournalProviders
{
    public DurableJobsJournalProviders(IServiceProvider services, IOptions<DurableJobsOptions> options)
    {
        var value = options.Value;
        var names = GetProviderNames(value);
        Providers = names.Select(name => Resolve(services, name)).ToArray();
    }

    internal DurableJobsJournalProviders(params DurableJobsJournalProvider[] providers)
    {
        ArgumentOutOfRangeException.ThrowIfZero(providers.Length);
        Providers = providers.ToArray();
    }

    internal DurableJobsJournalProvider[] Providers { get; }

    internal DurableJobsJournalProvider WriteProvider => Providers[0];

    internal DurableJobsJournalProvider GetProvider(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Array.Find(Providers, provider => string.Equals(provider.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Journal provider '{name}' is not selected for Durable Jobs.", nameof(name));
    }

    internal static List<string> GetProviderNames(DurableJobsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ActiveProviderName))
        {
            throw new OrleansConfigurationException("DurableJobsOptions.ActiveProviderName must be non-empty.");
        }

        var result = new List<string> { options.ActiveProviderName };
        var names = new HashSet<string>(StringComparer.Ordinal) { options.ActiveProviderName };
        foreach (var name in options.DrainingProviderNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new OrleansConfigurationException("DurableJobsOptions.DrainingProviderNames must contain non-empty provider names.");
            }

            if (!names.Add(name))
            {
                throw new OrleansConfigurationException($"Durable Jobs journal provider '{name}' is selected more than once.");
            }

            result.Add(name);
        }

        return result;
    }

    private static DurableJobsJournalProvider Resolve(IServiceProvider services, string name)
    {
        // Durable Jobs discovers shards through the provider's catalog and claims them through conditional metadata
        // updates. Registrations bind a catalog only for providers which implement one, so its absence identifies a
        // provider which cannot hold shards, without constructing that provider.
        if (services.GetService<IServiceProviderIsKeyedService>() is { } registrations)
        {
            if (!registrations.IsKeyedService(typeof(IJournalStorageProvider), name))
            {
                throw new OrleansConfigurationException(
                    $"Durable Jobs journal provider '{name}' is not registered. Register a journal storage provider which supports " +
                    $"catalog listing and journal metadata under this name, for example AddAzureBlobJournalStorage(\"{name}\", ...), " +
                    $"or select a registered provider with {nameof(DurableJobsOptions)}.{nameof(DurableJobsOptions.ActiveProviderName)}.");
            }

            if (!registrations.IsKeyedService(typeof(IJournalStorageCatalog), name))
            {
                throw new OrleansConfigurationException(
                    $"Durable Jobs journal provider '{name}' does not implement {nameof(IJournalStorageCatalog)}. Durable Jobs discovers " +
                    "shards by listing the provider's journal catalog and claims them through conditional journal metadata updates, " +
                    "so its provider must support catalog listing and journal metadata. Register such a provider under another name, " +
                    "for example AddAzureBlobJournalStorage(\"jobs\", ...), and select it with " +
                    $"{nameof(DurableJobsOptions)}.{nameof(DurableJobsOptions.ActiveProviderName)} = \"jobs\".");
            }
        }

        try
        {
            return new(
                name,
                services.GetRequiredKeyedService<IJournalStorageProvider>(name),
                services.GetRequiredKeyedService<IJournalStorageCatalog>(name),
                services.GetRequiredKeyedService<IJournaledStateManagerFactory>(name));
        }
        catch (Exception exception)
        {
            throw new OrleansConfigurationException(
                $"Durable Jobs journal provider '{name}' requires registered storage, catalog, and state-manager factory services.",
                exception);
        }
    }
}

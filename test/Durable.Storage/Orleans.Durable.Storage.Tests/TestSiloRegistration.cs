using Orleans.Durable.Storage.Hosting;
using Orleans.Hosting;
using Orleans.Providers;

namespace Orleans.Durable.Storage.Tests;

/// <summary>Registers Clog in a test silo with the options a test built in code.</summary>
internal static class TestSiloRegistration
{
    /// <summary>
    /// Registers the silo's Clog node with <paramref name="options"/>' sections as the silo's options, under the provider
    /// named <paramref name="providerName"/>, or Orleans' default.
    /// </summary>
    public static ISiloBuilder AddDurableJournalStorageWith(this ISiloBuilder builder, DurableStorageSiloOptions options, string? providerName = null) =>
        builder.AddDurableJournalStorage(providerName ?? ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configure => configure.Configure(target =>
        {
            target.Node = options.Node;
            target.Control = options.Control;
        }));
}

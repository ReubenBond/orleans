using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Hosting;
using Orleans.Hosting;
using Orleans.Durable.Journaling.Json;

namespace Orleans.Durable.Storage.Samples.Journaling;

/// <summary>The sample silo's durable storage configuration, which its smoke test shares.</summary>
public static class SampleSilo
{
    /// <summary>The configuration section which holds the silo's durable storage options, with its <c>Node</c> and <c>Control</c> subsections.</summary>
    public const string Section = "DurableStorage";

    /// <summary>
    /// Registers durable storage as the silo's journal storage, its options bound from the configuration's <see cref="Section"/>,
    /// and the JSON journal format's metadata for the sample's grain. The silo's services supply the MetaStore and the LSM's
    /// object store.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <param name="configuration">The silo's configuration.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddSampleJournaling(this ISiloBuilder silo, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(configuration);
        _ = silo.AddDurableJournalStorage(configuration.GetSection(Section));
        _ = silo.Services.Configure<JsonJournalOptions>(static options => options.AddTypeInfoResolver(KeyValueJsonContext.Default));
        return silo;
    }
}

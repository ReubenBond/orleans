#nullable enable
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Orleans.Configuration.Internal;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime.Hosting;

/// <summary>
/// Extension methods for configuring the distributed grain directory.
/// </summary>
public static class DistributedGrainDirectorySiloBuilderExtensions
{
    /// <summary>
    /// Configures the silo to use <see cref="MigratingGrainDirectory"/> during a rolling upgrade from
    /// the DHT-based <see cref="LocalGrainDirectory"/> to the Virtual Synchrony-based <see cref="DistributedGrainDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method should be used during the migration period when the cluster contains a mix of OLD silos
    /// (using <see cref="LocalGrainDirectory"/>) and NEW silos (using <see cref="DistributedGrainDirectory"/>).
    /// The <see cref="MigratingGrainDirectory"/> ensures consistency by forwarding requests to the DHT
    /// when the DHT owner is an OLD silo.
    /// </para>
    /// <para>
    /// <b>Rolling upgrade procedure:</b>
    /// <list type="number">
    /// <item>Deploy new silos with <see cref="UseMigratingGrainDirectory"/> enabled</item>
    /// <item>Gradually replace old silos with new silos</item>
    /// <item>Monitor logs for "Grain directory migration complete" message</item>
    /// <item>Once all silos are upgraded, switch to <see cref="UseDistributedGrainDirectory"/> and redeploy</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Consistency guarantees:</b>
    /// <list type="bullet">
    /// <item>No duplicate grain activations during migration</item>
    /// <item>DHT remains authoritative while OLD silos exist</item>
    /// <item>Seamless transition to <see cref="DistributedGrainDirectory"/> when all silos are NEW</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder for method chaining.</returns>
    [Experimental("ORLEANSEXP003")]
    public static ISiloBuilder UseMigratingGrainDirectory(this ISiloBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Register the MigratingGrainDirectory with proper dependency injection
            // The IGrainDirectory parameter is fulfilled by DistributedGrainDirectory (registered in DefaultSiloServices)
            services.AddSingleton<MigratingGrainDirectory>(sp => new MigratingGrainDirectory(
                sp.GetRequiredService<DistributedGrainDirectory>(),
                sp.GetRequiredService<ILocalGrainDirectory>(),
                sp.GetRequiredService<DirectoryMembershipService>(),
                sp.GetRequiredService<ILogger<MigratingGrainDirectory>>()));
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp => sp.GetRequiredService<MigratingGrainDirectory>());

            // Replace LocalGrainDirectoryPartition with DelegatingGrainDirectoryPartition
            // This ensures DHT-based lookups from OLD silos work correctly
            services.RemoveAll<ILocalGrainDirectoryPartition>();
            services.AddSingleton<DelegatingGrainDirectoryPartition>();
            services.AddSingleton<ILocalGrainDirectoryPartition>(sp => sp.GetRequiredService<DelegatingGrainDirectoryPartition>());

            // Advertise the DistributedGrainDirectory capability via the cluster manifest
            // This allows DirectoryMembershipService to filter membership for DistributedGrainDirectory coordination
            services.AddSingleton<ISiloPropertiesProvider, DistributedGrainDirectoryCapabilityProvider>();
        });

        return builder;
    }

    /// <summary>
    /// Configures the silo to use <see cref="DistributedGrainDirectory"/> for grain location.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Important:</b> This method should only be used after all silos in the cluster have been upgraded
    /// using <see cref="UseMigratingGrainDirectory"/>. Using this method in a mixed cluster (with OLD silos
    /// still present) may result in duplicate grain activations.
    /// </para>
    /// <para>
    /// <b>Post-migration deployment:</b>
    /// <list type="number">
    /// <item>Ensure all silos are running with <see cref="UseMigratingGrainDirectory"/></item>
    /// <item>Verify logs show "Grain directory migration complete"</item>
    /// <item>Update configuration to use <see cref="UseDistributedGrainDirectory"/></item>
    /// <item>Perform a rolling restart of all silos</item>
    /// </list>
    /// </para>
    /// <para>
    /// This method configures the silo to:
    /// <list type="bullet">
    /// <item>Advertise the <see cref="GrainDirectoryCapability.Distributed"/> capability via the cluster manifest</item>
    /// <item>Use <see cref="DelegatingGrainDirectoryPartition"/> to maintain DHT compatibility during the final transition</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder for method chaining.</returns>
    [Experimental("ORLEANSEXP003")]
    public static ISiloBuilder UseDistributedGrainDirectory(this ISiloBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // NOTE: DirectoryMembershipService and DistributedGrainDirectory are registered in DefaultSiloServices
            // on ALL silos, so that IGrainDirectoryClient is available for recovery queries during rolling upgrades.
            // Here we only configure the migration-specific behavior:
            // 1. Replace the partition storage to delegate to DistributedGrainDirectory
            // 2. Advertise the capability so DirectoryMembershipService can identify upgraded silos

            // Replace LocalGrainDirectoryPartition with DelegatingGrainDirectoryPartition
            // This ensures DHT-based lookups work correctly while replicating to DistributedGrainDirectory
            services.RemoveAll<ILocalGrainDirectoryPartition>();
            services.AddSingleton<DelegatingGrainDirectoryPartition>();
            services.AddSingleton<ILocalGrainDirectoryPartition>(sp => sp.GetRequiredService<DelegatingGrainDirectoryPartition>());

            // Advertise the DistributedGrainDirectory capability via the cluster manifest (GrainManifest.Properties)
            // This allows DirectoryMembershipService to filter membership for DistributedGrainDirectory coordination
            services.AddSingleton<ISiloPropertiesProvider, DistributedGrainDirectoryCapabilityProvider>();
        });

        return builder;
    }
}

/// <summary>
/// Provides the <see cref="GrainDirectoryCapability.Distributed"/> capability as a silo property.
/// This property is included in the <see cref="GrainManifest.Properties"/> and propagated via the cluster manifest.
/// </summary>
internal sealed class DistributedGrainDirectoryCapabilityProvider : ISiloPropertiesProvider
{
    /// <inheritdoc />
    public void Populate(Dictionary<string, string> properties)
    {
        properties[GrainDirectoryCapability.MetadataKey] = GrainDirectoryCapability.Distributed;
    }
}

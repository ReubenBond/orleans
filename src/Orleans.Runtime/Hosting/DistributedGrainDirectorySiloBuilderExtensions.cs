#nullable enable
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.MembershipService.SiloMetadata;

namespace Orleans.Runtime.Hosting;

/// <summary>
/// Extension methods for configuring the distributed grain directory.
/// </summary>
public static class DistributedGrainDirectorySiloBuilderExtensions
{
    /// <summary>
    /// Configures the silo to use <see cref="DistributedGrainDirectory"/> for grain location during
    /// rolling upgrades from the default DHT-based <see cref="LocalGrainDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// During rolling upgrades, this method configures the silo to:
    /// <list type="bullet">
    /// <item>Advertise the <see cref="GrainDirectoryCapability.Distributed"/> capability via silo metadata</item>
    /// <item>Use <see cref="DelegatingGrainDirectoryPartition"/> which stores grain registrations locally
    /// (for DHT compatibility with OLD silos) and asynchronously replicates to <see cref="DistributedGrainDirectory"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Important:</b> During the migration period, NEW silos continue to use the DHT-based directory
    /// for grain registration/lookup to maintain compatibility with OLD silos. The <see cref="DelegatingGrainDirectoryPartition"/>
    /// ensures that:
    /// <list type="bullet">
    /// <item>DHT lookups from OLD silos work correctly (grains stored locally)</item>
    /// <item>Registrations are replicated to <see cref="DistributedGrainDirectory"/> for eventual consistency</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Rolling upgrade procedure:</b>
    /// <list type="number">
    /// <item>Deploy new silos with <see cref="UseDistributedGrainDirectory"/> enabled</item>
    /// <item>New silos participate in the DHT ring with <see cref="DelegatingGrainDirectoryPartition"/></item>
    /// <item>Old silos continue using <see cref="LocalGrainDirectory"/> and forward requests via DHT</item>
    /// <item>Gradually replace old silos with new silos</item>
    /// <item>Once all silos are upgraded, the DHT and <see cref="DistributedGrainDirectory"/> are fully synchronized</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder for method chaining.</returns>
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

            // Advertise the DistributedGrainDirectory capability via silo metadata
            // This allows DirectoryMembershipService to filter membership for DistributedGrainDirectory coordination
            services.AddOptionsWithValidateOnStart<SiloMetadata>()
                .Configure(m => m.AddMetadata(new Dictionary<string, string>
                {
                    [GrainDirectoryCapability.MetadataKey] = GrainDirectoryCapability.Distributed
                }));

            // Ensure silo metadata infrastructure is registered
            services.TryAddSingleton<SiloMetadataSystemTarget>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, SiloMetadataSystemTarget>();
            services.TryAddSingleton<SiloMetadataCache>();
            services.TryAddSingleton<ISiloMetadataCache>(sp => sp.GetRequiredService<SiloMetadataCache>());
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, SiloMetadataCache>();
            services.TryAddSingleton<ISiloMetadataClient, SiloMetadataClient>();
        });

        return builder;
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Orleans.ApplicationParts;
using Orleans.Runtime;

namespace Orleans.Metadata
{

    public class GrainMetadataProvider
    {
        private readonly ImmutableDictionary<GrainType, GrainMetadata> grainTypeMetadata;

        public GrainMetadataProvider(IEnumerable<IGrainMetadataProvider> grainMetadataProviders, IApplicationPartManager applicationPartManager, GrainTypeProvider grainTypeProvider)
        {
            var grainClassFeature = applicationPartManager.CreateAndPopulateFeature<GrainClassFeature>();
            var metadataBuilder = ImmutableDictionary.CreateBuilder<GrainType, GrainMetadata>();
            foreach (var grainClassMetadata in grainClassFeature.Classes)
            {
                var grainClass = grainClassMetadata.ClassType;
                var grainType = grainTypeProvider.GetGrainType(grainClass);
                var properties = new Dictionary<string, string>();
                foreach (var provider in grainMetadataProviders)
                {
                    provider.Populate(grainType, properties);
                }

                var grainMetadata = new GrainMetadata(properties.ToImmutableDictionary());
                if (metadataBuilder.ContainsKey(grainType))
                {
                    throw new InvalidOperationException($"An entry with the key {grainType} is already present."
                        + $"\nExisting: {metadataBuilder[grainType].ToDetailedString()}\nTrying to add: {grainMetadata.ToDetailedString()}");
                }

                metadataBuilder.Add(grainType, grainMetadata);
            }
        }
    }

    // For grain implementations (classes)

    // For metadata associated with 'GrainType' for local or remote grains.
    // Info such as placement policy.
    public static class GrainTypePropertiesExtensions
    {
        // for example...
        public static string GetMultiClusterRegistrationStrategy(this GrainMetadata properties) => properties.Values["sys.multicluster.registration.policy"] ?? "default";
        public static string GetPlacementPolicy(this GrainMetadata properties) => properties.Values["sys.placement.policy"] ?? "random";
    }

    // Information about CLR grain interface (communication) types on local or remote silos.
    public class GrainInterfaceManager
    {
        public cGrainInterfaceMetadata GetProperties(string interfaceTypeName) => null;

        public string GetInterfaceVersion(cGrainInterfaceMetadata properties) => properties.Values["version"] ?? "random";
    }
}

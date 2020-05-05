using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Orleans.ApplicationParts;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Creates a <see cref="SiloManifest"/> for this silo.
    /// </summary>
    internal class SiloManifestProvider
    {
        public SiloManifestProvider(
            IEnumerable<IGrainPropertiesProvider> grainPropertiesProviders,
            IEnumerable<IGrainInterfacePropertiesProvider> grainInterfacePropertiesProviders,
            IApplicationPartManager applicationPartManager,
            GrainTypeResolver typeProvider,
            GrainInterfaceIdResolver interfaceIdProvider)
        {
            var (grainProperties, grainTypes) = CreateGrainManifest(grainPropertiesProviders, applicationPartManager, typeProvider);
            var interfaces = CreateInterfaceManifest(grainInterfacePropertiesProviders, applicationPartManager, interfaceIdProvider);
            this.SiloManifest = new SiloManifest(grainProperties, interfaces);
            this.GrainTypeMap = new GrainClassMap(grainTypes);
        }

        public SiloManifest SiloManifest { get; }

        public GrainClassMap GrainTypeMap { get; }

        private static ImmutableDictionary<GrainInterfaceId, GrainInterfaceProperties> CreateInterfaceManifest(
            IEnumerable<IGrainInterfacePropertiesProvider> propertyProviders,
            IApplicationPartManager applicationPartManager,
            GrainInterfaceIdResolver grainInterfaceIdProvider)
        {
            var feature = applicationPartManager.CreateAndPopulateFeature<GrainInterfaceFeature>();
            var builder = ImmutableDictionary.CreateBuilder<GrainInterfaceId, GrainInterfaceProperties>();
            foreach (var value in feature.Interfaces)
            {
                var interfaceId = grainInterfaceIdProvider.GetGrainInterfaceId(value.InterfaceType);
                var properties = new Dictionary<string, string>();
                foreach (var provider in propertyProviders)
                {
                    provider.Populate(value.InterfaceType, interfaceId, properties);
                }

                var result = new GrainInterfaceProperties(properties.ToImmutableDictionary());
                if (builder.ContainsKey(interfaceId))
                {
                    throw new InvalidOperationException($"An entry with the key {interfaceId} is already present."
                        + $"\nExisting: {builder[interfaceId].ToDetailedString()}\nTrying to add: {result.ToDetailedString()}"
                        + "\nConsider using the [GrainInterfaceId(\"name\")] attribute to give these interfaces unique names.");
                }

                builder.Add(interfaceId, result);
            }

            return builder.ToImmutable();
        }

        private static (ImmutableDictionary<GrainType, GrainProperties>, ImmutableDictionary<GrainType, Type>) CreateGrainManifest(
            IEnumerable<IGrainPropertiesProvider> grainMetadataProviders,
            IApplicationPartManager applicationPartManager,
            GrainTypeResolver grainTypeProvider)
        {
            var feature = applicationPartManager.CreateAndPopulateFeature<GrainClassFeature>();
            var propertiesMap = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
            var typeMap = ImmutableDictionary.CreateBuilder<GrainType, Type>();
            foreach (var value in feature.Classes)
            {
                var grainClass = value.ClassType;
                var grainType = grainTypeProvider.GetGrainType(grainClass);
                var properties = new Dictionary<string, string>();
                foreach (var provider in grainMetadataProviders)
                {
                    provider.Populate(grainClass, grainType, properties);
                }

                var result = new GrainProperties(properties.ToImmutableDictionary());
                if (propertiesMap.ContainsKey(grainType))
                {
                    throw new InvalidOperationException($"An entry with the key {grainType} is already present."
                        + $"\nExisting: {propertiesMap[grainType].ToDetailedString()}\nTrying to add: {result.ToDetailedString()}"
                        + "\nConsider using the [GrainType(\"name\")] attribute to give these classes unique names.");
                }

                propertiesMap.Add(grainType, result);
                typeMap.Add(grainType, grainClass);
            }

            return (propertiesMap.ToImmutable(), typeMap.ToImmutable());
        }
    }
}

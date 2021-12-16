using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Legact.CodeGeneration;
using Orleans.Legacy.Metadata;
using Orleans.Serialization.Configuration;

namespace Orleans
{
    public class GrainMappingFeatureProvider : IApplicationFeatureProvider<GrainInterfaceFeature>, IApplicationFeatureProvider<GrainClassFeature>
    {
        private TypeManifestOptions _typeManifest;

        public GrainMappingFeatureProvider(IOptions<TypeManifestOptions> typeManifestOptions)
        {
            _typeManifest = typeManifestOptions.Value;
        }

        public void PopulateFeature(IEnumerable<IApplicationPart> parts, GrainInterfaceFeature feature)
        {
            foreach (var grainInterface in _typeManifest.Interfaces)
            {
                var metadata = new GrainInterfaceMetadata(grainInterface, GetGeneratedClassName(grainInterface), GrainInterfaceUtils.GetGrainInterfaceId(grainInterface));
                feature.Interfaces.Add(metadata);
            }

            static string GetGeneratedClassName(Type type) => $"OrleansCodeGen{TypeUtils.GetSuitableClassName(type)}Reference";
        }

        public void PopulateFeature(IEnumerable<IApplicationPart> parts, GrainClassFeature feature)
        {
            foreach (var grainClass in _typeManifest.InterfaceImplementations)
            {
                var metadata = new GrainClassMetadata(grainClass);
                feature.Classes.Add(metadata);
            }
        }
    }
}

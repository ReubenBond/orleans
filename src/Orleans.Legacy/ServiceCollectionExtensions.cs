using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.GrainReferences;
using Orleans.Legact.CodeGeneration;
using Orleans.Legacy;
using Orleans.Legacy.Metadata;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;
using Orleans.Legacy.Runtime;

namespace Orleans
{
    public static class ServiceCollectionExtensions
    {
        private static readonly ServiceDescriptor ServicesAddedDescriptor = new(typeof(MarkerService), typeof(MarkerService), ServiceLifetime.Transient);
        public static IServiceCollection AddOrleansLegacySuppport(this IServiceCollection services)
        {
            if (services.Contains(ServicesAddedDescriptor)) return services;

            services.Add(ServicesAddedDescriptor);

            // Ensure that dependencies are added.
            services.AddLogging();

            services.AddSingleton<SerializationManager>();
            services.AddSingleton<ITypeResolver, CachedTypeResolver>();
            services.AddSingleton<IFieldUtils, FieldUtils>();
            services.AddSingleton<BinaryFormatterSerializer>();
            services.AddSingleton<IKeyedSerializer, BinaryFormatterISerializableSerializer>();
            services.AddSingleton<IKeyedSerializer, ILBasedSerializer>();

            services.AddSingleton<GrainTypeManager>();
            services.AddSingleton<GrainReferenceMapper>();

            // Application Parts
            services.AddSingleton(CreateApplicationPartManager);

            return services;
        }

        private class MarkerService { }

        private static IApplicationPartManager CreateApplicationPartManager(IServiceProvider services)
        {
            return new ApplicationPartManager()
                .AddApplicationPart(new AssemblyPart(typeof(ServiceCollectionExtensions).Assembly) { IsFrameworkAssembly = true })
                .AddFeatureProvider(new BuiltInTypesSerializationFeaturePopulator())
                .AddFeatureProvider(new KnownTypesSerializationFeaturePopulator())
                .AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainInterfaceFeature>())
                .AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainClassFeature>())
                .AddFeatureProvider(new AssemblyAttributeFeatureProvider<SerializerFeature>())
                .AddFeatureProvider(new GrainMappingFeatureProvider(services.GetRequiredService<IOptions<TypeManifestOptions>>()))
                .ConfigureDefaults();
        }
    }
}

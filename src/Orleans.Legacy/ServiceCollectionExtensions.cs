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

            // Application Parts
            services.AddSingleton<IApplicationPartManager>(CreateApplicationPartManager);

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

    internal class GrainReferenceMapper
    {
        private readonly GrainReferenceActivator _grainReferenceActivator;
        private readonly GrainTypeManager _grainTypeManager;
        private readonly Orleans.Runtime.GrainTypeResolver _grainTypeResolver;
        private readonly Metadata.GrainTypeResolver _newGrainTypeResolver;
        private readonly ITypeResolver _typeResolver;
        private readonly GrainInterfaceTypeResolver _interfaceTypeResolver;

        public GrainReferenceMapper(
            IApplicationPartManager appParts,
            GrainReferenceActivator grainReferenceActivator,
            GrainTypeManager grainTypeManager,
            Orleans.Runtime.GrainTypeResolver grainTypeResolver,
            Orleans.Metadata.GrainTypeResolver newGrainTypeResolver,
            ITypeResolver typeResolver,
            GrainInterfaceTypeResolver interfaceTypeResolver)
        {
            _grainReferenceActivator = grainReferenceActivator;
            _grainTypeManager = grainTypeManager;
            _grainTypeResolver = grainTypeResolver;
            _newGrainTypeResolver = newGrainTypeResolver;
            _typeResolver = typeResolver;
            _interfaceTypeResolver = interfaceTypeResolver;
        }

        public Orleans.Runtime.GrainReference ConvertGrainReference(GrainReference grainReference, Type interfaceType)
        {
            var id = grainReference.GrainId;
            if (!_grainTypeManager.TryGetTypeInfo(id.TypeCode, out var grainClass, out _, grainReference.GenericArguments))
            {
                throw new InvalidOperationException($"Unable to find grain type information for the provided grain reference {grainReference}");
            }

            var grainClassType = _typeResolver.ResolveType(grainClass);
            var newGrainType = _newGrainTypeResolver.GetGrainType(grainClassType);
            var newGrainKey = LegacyGrainIdHelper.ConvertKeyToNewIdFormat(id);
            var newGrainId = Orleans.Runtime.GrainId.Create(newGrainType, newGrainKey);
            var grainInterfaceType = _interfaceTypeResolver.GetGrainInterfaceType(interfaceType);
            return _grainReferenceActivator.CreateReference(newGrainId, grainInterfaceType);
        }

        public GrainReference GetGrainReference(Type grainInterfaceType, long primaryKey, string keyExtension = null, string grainClassNamePrefix = null)
        {
            var implementation = this.GetGrainClassData(grainInterfaceType, grainClassNamePrefix);
            var grainId = GrainId.GetGrainId(implementation.GetTypeCode(grainInterfaceType), primaryKey, keyExtension);
            return MakeGrainReferenceFromType(grainInterfaceType, grainId);
        }

        public GrainReference GetGrainReference(Type grainInterfaceType, Guid primaryKey, string keyExtension = null, string grainClassNamePrefix = null)
        {
            var implementation = this.GetGrainClassData(grainInterfaceType, grainClassNamePrefix);
            var grainId = GrainId.GetGrainId(implementation.GetTypeCode(grainInterfaceType), primaryKey, keyExtension);
            return MakeGrainReferenceFromType(grainInterfaceType, grainId);
        }

        public GrainReference GetGrainReference(Type grainInterfaceType, string primaryKey, string grainClassNamePrefix = null)
        {
            var implementation = this.GetGrainClassData(grainInterfaceType, grainClassNamePrefix);
            var grainId = GrainId.GetGrainId(implementation.GetTypeCode(grainInterfaceType), primaryKey);
            return MakeGrainReferenceFromType(grainInterfaceType, grainId);
        }

        internal GrainReference MakeGrainReferenceFromType(Type interfaceType, GrainId grainId)
        {
            return GrainReference.FromGrainId(
                grainId,
                interfaceType.IsGenericType ? TypeUtils.GenericTypeArgsString(interfaceType.UnderlyingSystemType.FullName) : null);
        }

        private GrainClassData GetGrainClassData(Type interfaceType, string grainClassNamePrefix)
        {
            if (!GrainInterfaceUtils.IsGrainType(interfaceType))
            {
                throw new ArgumentException("Cannot fabricate grain-reference for non-grain type: " + interfaceType.FullName);
            }

            GrainClassData implementation;
            if (!_grainTypeResolver.TryGetGrainClassData(interfaceType, out implementation, grainClassNamePrefix))
            {
                var loadedAssemblies = _grainTypeResolver.GetLoadedGrainAssemblies();
                var assembliesString = string.IsNullOrEmpty(loadedAssemblies)
                    ? string.Empty
                    : " Loaded grain assemblies: " + loadedAssemblies;
                var grainClassPrefixString = string.IsNullOrEmpty(grainClassNamePrefix)
                    ? string.Empty
                    : ", grainClassNamePrefix: " + grainClassNamePrefix;
                throw new ArgumentException(
                    $"Cannot find an implementation class for grain interface: {interfaceType}{grainClassPrefixString}. " +
                    "Make sure the grain assembly was correctly deployed and loaded in the silo." + assembliesString);
            }

            return implementation;
        }
    }
}

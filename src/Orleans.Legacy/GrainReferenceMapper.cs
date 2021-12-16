using System;
using Orleans.GrainReferences;
using Orleans.Legact.CodeGeneration;
using Orleans.Legacy;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans
{
    internal class GrainReferenceMapper
    {
        private readonly GrainReferenceActivator _grainReferenceActivator;
        private readonly GrainTypeManager _grainTypeManager;
        private readonly GrainTypeResolver _grainTypeResolver;
        private readonly ITypeResolver _typeResolver;
        private readonly GrainInterfaceTypeResolver _interfaceTypeResolver;

        public GrainReferenceMapper(
            GrainReferenceActivator grainReferenceActivator,
            GrainTypeManager grainTypeManager,
            GrainTypeResolver grainTypeResolver,
            ITypeResolver typeResolver,
            GrainInterfaceTypeResolver interfaceTypeResolver)
        {
            _grainReferenceActivator = grainReferenceActivator;
            _grainTypeManager = grainTypeManager;
            _grainTypeResolver = grainTypeResolver;
            _typeResolver = typeResolver;
            _interfaceTypeResolver = interfaceTypeResolver;
        }

        public GrainReference ConvertGrainReference(LegacyGrainReference grainReference, Type interfaceType)
        {
            var id = grainReference.GrainId;
            if (!_grainTypeManager.TryGetTypeInfo(id.TypeCode, out var grainClass, out _, grainReference.GenericArguments))
            {
                throw new InvalidOperationException($"Unable to find grain type information for the provided grain reference {grainReference}");
            }

            var grainClassType = _typeResolver.ResolveType(grainClass);
            var newGrainType = _grainTypeResolver.GetGrainType(grainClassType);
            var newGrainKey = LegacyGrainIdHelper.ConvertKeyToNewIdFormat(id);
            var newGrainId = GrainId.Create(newGrainType, newGrainKey);
            var grainInterfaceType = _interfaceTypeResolver.GetGrainInterfaceType(interfaceType);
            return _grainReferenceActivator.CreateReference(newGrainId, grainInterfaceType);
        }

        public LegacyGrainReference ConvertGrainReference(GrainReference grainReference, Type grainClass)
        {
            // Compute the type code from the grainClass
            // Get generic arguments (if any)
            // Compute N0 & N1 values from key
            var id = grainReference.GrainId;
            (ulong n0, ulong n1, string keyExt) = LegacyGrainIdHelper.ExtractKeyComponents(id.Key);

            var hasKeyExt = !string.IsNullOrEmpty(keyExt);
            var isSystemTarget = typeof(SystemTarget).IsAssignableFrom(grainClass);
            var isObserver = typeof(IGrainObserver).IsAssignableFrom(grainClass) && !isSystemTarget && !typeof(Grain).IsAssignableFrom(grainClass);

            // Determine what 'category' of grain this is.
            UniqueKey.Category category;
            if (isSystemTarget)
            {
                if (hasKeyExt) category = UniqueKey.Category.KeyExtSystemTarget;
                else category = UniqueKey.Category.SystemTarget;
            }
            else if (isObserver)
            {
                category = UniqueKey.Category.Client;
            }
            else
            {
                if (hasKeyExt) category = UniqueKey.Category.KeyExtGrain;
                else category = UniqueKey.Category.Grain;
            }

            var baseTypeCode = LegacyGrainIdHelper.GetTypeCode(grainClass);

            long grainTypeCode;
            if (grainClass.IsConstructedGenericType)
            {
                // Adjust the type code by including 3 bytes of generic type information.
                var hash = Utils.CalculateIdHash(TypeUtils.GetGenericTypeArgs(grainClass.GetGenericArguments(), t => true));
                var genericTypeArgsData = ((long)(hash & 0x00FFFFFF)) << 32;
                grainTypeCode = genericTypeArgsData + baseTypeCode;
            }
            else
            {
                grainTypeCode = baseTypeCode;
            }

            var typeCodeData = ((ulong)category << 56) + ((ulong)grainTypeCode & 0x00FFFFFFFFFFFFFF);
            var grainId = LegacyGrainId.GetGrainId(UniqueKey.NewKey(n0, n1, typeCodeData, keyExt)); 
            var result = LegacyGrainReference.FromGrainId(
                grainId,
                grainClass.IsGenericType ? TypeUtils.GenericTypeArgsString(grainClass.UnderlyingSystemType.FullName) : null);
            return result;
        }

        public LegacyGrainReference GetGrainReference(Type grainInterfaceType, long primaryKey, string keyExtension = null, string grainClassNamePrefix = null)
        {
            var implementation = this.GetGrainClassData(grainInterfaceType, grainClassNamePrefix);
            var grainId = LegacyGrainId.GetGrainId(implementation.GetTypeCode(grainInterfaceType), primaryKey, keyExtension);
            return MakeGrainReferenceFromType(grainInterfaceType, grainId);
        }

        public LegacyGrainReference GetGrainReference(Type grainInterfaceType, Guid primaryKey, string keyExtension = null, string grainClassNamePrefix = null)
        {
            var implementation = this.GetGrainClassData(grainInterfaceType, grainClassNamePrefix);
            var grainId = LegacyGrainId.GetGrainId(implementation.GetTypeCode(grainInterfaceType), primaryKey, keyExtension);
            return MakeGrainReferenceFromType(grainInterfaceType, grainId);
        }

        public LegacyGrainReference GetGrainReference(Type grainInterfaceType, string primaryKey, string grainClassNamePrefix = null)
        {
            var implementation = this.GetGrainClassData(grainInterfaceType, grainClassNamePrefix);
            var grainId = LegacyGrainId.GetGrainId(implementation.GetTypeCode(grainInterfaceType), primaryKey);
            return MakeGrainReferenceFromType(grainInterfaceType, grainId);
        }

        internal LegacyGrainReference MakeGrainReferenceFromType(Type interfaceType, LegacyGrainId grainId)
        {
            return LegacyGrainReference.FromGrainId(
                grainId,
                interfaceType.IsGenericType ? TypeUtils.GenericTypeArgsString(interfaceType.UnderlyingSystemType.FullName) : null);
        }

        public LegacyGrainReference GetGrainObserverReference(Guid clientId, Guid observerId)
        {
            var grainId = LegacyGrainId.NewClientId(clientId);
            return LegacyGrainReference.NewObserverGrainReference(grainId, LegacyGuidId.GetGuidId(observerId));
        }

        private GrainClassData GetGrainClassData(Type interfaceType, string grainClassNamePrefix)
        {
            if (!GrainInterfaceUtils.IsGrainType(interfaceType))
            {
                throw new ArgumentException("Cannot fabricate grain-reference for non-grain type: " + interfaceType.FullName);
            }

            GrainClassData implementation;
            var grainInterfaceMap = _grainTypeManager.GetTypeCodeMap();
            var grainTypeResolver = grainInterfaceMap.GetGrainTypeResolver();
            if (!grainTypeResolver.TryGetGrainClassData(interfaceType, out implementation, grainClassNamePrefix))
            {
                var loadedAssemblies = grainTypeResolver.GetLoadedGrainAssemblies();
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

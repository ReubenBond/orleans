using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.GrainDirectory;
using Orleans.Utilities;

namespace Orleans.Runtime
{
    /// <summary>
    /// Internal data structure that holds a grain interfaces to grain classes map.
    /// </summary>
    [Serializable]
    internal class GrainInterfaceMap
    {
        private readonly Dictionary<string, GrainInterfaceData> typeToInterfaceData;
        private readonly Dictionary<int, GrainInterfaceData> table;

#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable CS0169 // Remove unused private members
        // Unused. Retained for serialization compatibility.
        private readonly HashSet<int> unordered;
#pragma warning restore CS0169 // Remove unused private members
#pragma warning restore IDE0051 // Remove unused private members

        private readonly Dictionary<int, GrainClassData> implementationIndex;
        private readonly Dictionary<int, PlacementStrategy> placementStrategiesIndex;

        [NonSerialized] // Client shouldn't need this
        private readonly Dictionary<string, string> primaryImplementations;

		private readonly PlacementStrategy defaultPlacementStrategy;

        internal IEnumerable<GrainClassData> SupportedGrainClassData
        {
            get { return implementationIndex.Values; }
        }

        internal IEnumerable<GrainInterfaceData> SupportedInterfaces
        {
            get { return table.Values; }
        }

        public GrainInterfaceMap(PlacementStrategy defaultPlacementStrategy)
        {
            table = new Dictionary<int, GrainInterfaceData>();
            typeToInterfaceData = new Dictionary<string, GrainInterfaceData>();
            primaryImplementations = new Dictionary<string, string>();
            implementationIndex = new Dictionary<int, GrainClassData>();
            placementStrategiesIndex = new Dictionary<int, PlacementStrategy>();
            this.defaultPlacementStrategy = defaultPlacementStrategy;
        }

        internal void AddMap(GrainInterfaceMap map)
        {
            foreach (var kvp in map.typeToInterfaceData)
            {
                if (!typeToInterfaceData.ContainsKey(kvp.Key))
                {
                    typeToInterfaceData.Add(kvp.Key, kvp.Value);
                }
            }

            foreach (var kvp in map.table)
            {
                if (!table.ContainsKey(kvp.Key))
                {
                    table.Add(kvp.Key, kvp.Value);
                }
            }

            foreach (var kvp in map.implementationIndex)
            {
                if (!implementationIndex.ContainsKey(kvp.Key))
                {
                    implementationIndex.Add(kvp.Key, kvp.Value);
                }
            }

            foreach (var kvp in map.placementStrategiesIndex)
            {
                if (!placementStrategiesIndex.ContainsKey(kvp.Key))
                {
                    placementStrategiesIndex.Add(kvp.Key, kvp.Value);
                }
            }
        }

        internal void AddEntry(Type iface, Type grain, PlacementStrategy placement, bool primaryImplementation)
        {
            lock (this)
            {
                var grainName = TypeUtils.GetFullName(grain);
                var isGenericGrainClass = grain.ContainsGenericParameters;
                var grainTypeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grain);

                var grainInterfaceData = GetOrAddGrainInterfaceData(iface, isGenericGrainClass);

                var implementation = new GrainClassData(grainTypeCode, grainName, isGenericGrainClass);
                if (!implementationIndex.ContainsKey(grainTypeCode))
                    implementationIndex.Add(grainTypeCode, implementation);
                if (!placementStrategiesIndex.ContainsKey(grainTypeCode))
                    placementStrategiesIndex.Add(grainTypeCode, placement);

                grainInterfaceData.AddImplementation(implementation, primaryImplementation);
                if (primaryImplementation)
                {
                    primaryImplementations[grainInterfaceData.GrainInterface] = grainName;
                }
                else
                {
                    if (!primaryImplementations.ContainsKey(grainInterfaceData.GrainInterface))
                        primaryImplementations.Add(grainInterfaceData.GrainInterface, grainName);
                }
            }
        }

        private GrainInterfaceData GetOrAddGrainInterfaceData(Type iface, bool isGenericGrainClass)
        {
            var interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(iface);
            var version = GrainInterfaceUtils.GetGrainInterfaceVersion(iface);

            // If already exist
            GrainInterfaceData grainInterfaceData;
            if (table.TryGetValue(interfaceId, out grainInterfaceData))
                return grainInterfaceData;

            // If not create new entry
            var interfaceName = TypeUtils.GetRawClassName(TypeUtils.GetFullName(iface));
            grainInterfaceData = new GrainInterfaceData(interfaceId, version, iface, interfaceName);
            table[interfaceId] = grainInterfaceData;

            // Add entry to mapping iface string -> data
            var interfaceTypeKey = GetTypeKey(iface, isGenericGrainClass);
            typeToInterfaceData[interfaceTypeKey] = grainInterfaceData;

            // If we are adding a concrete implementation of a generic interface
            // add also the latter to the map: GrainReference and InvokeMethodRequest 
            // always use the id of the generic one
            if (iface.IsConstructedGenericType)
                GetOrAddGrainInterfaceData(iface.GetGenericTypeDefinition(), true);

            return grainInterfaceData;
        }

        internal Dictionary<string, string> GetPrimaryImplementations()
        {
            lock (this)
            {
                return new Dictionary<string, string>(primaryImplementations);
            }
        }

        internal bool TryGetPrimaryImplementation(string grainInterface, out string grainClass)
        {
            lock (this)
            {
                return primaryImplementations.TryGetValue(grainInterface, out grainClass);
            }
        }

        internal bool TryGetServiceInterface(int interfaceId, out Type iface)
        {
            lock (this)
            {
                iface = null;

                if (!table.ContainsKey(interfaceId))
                    return false;

                var interfaceData = table[interfaceId];
                iface = interfaceData.Interface;
                return true;
            }
        }

        internal ushort GetInterfaceVersion(int ifaceId)
        {
            return table[ifaceId].InterfaceVersion;
        }

        internal bool TryGetTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, string genericArguments = null)
        {
            lock (this)
            {
                grainClass = null;
                placement = this.defaultPlacementStrategy;
                if (!implementationIndex.ContainsKey(typeCode))
                    return false;

                var implementation = implementationIndex[typeCode];
                grainClass = implementation.GetClassName(genericArguments);
                placement = placementStrategiesIndex[typeCode];
                return true;
            }
        }

        internal static string GetTypeKey(Type interfaceType, bool isGenericGrainClass)
        {
            if (isGenericGrainClass && interfaceType.IsGenericType)
            {
                return interfaceType.GetGenericTypeDefinition().AssemblyQualifiedName;
            }
            else 
            {
                return TypeUtils.GetTemplatedName(
                               TypeUtils.GetFullName(interfaceType),
                               interfaceType,
                               interfaceType.GetGenericArguments(),
                               t => false);
            }
        }

        public IGrainTypeResolver GetGrainTypeResolver()
        {
            return new GrainTypeResolver(this.typeToInterfaceData);
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Legact.CodeGeneration;
using Orleans.Legacy;
using Orleans.Legacy.Metadata;
using Orleans.Metadata;
using Orleans.Serialization;
using Orleans.Runtime;
using Orleans.Utilities;

namespace Orleans.Legacy.Runtime
{
    internal class GrainTypeManager
    {
        private Dictionary<SiloAddress, GrainInterfaceMap> grainInterfaceMapsBySilo;
        private Dictionary<int, List<SiloAddress>> supportedSilosByTypeCode;
        private readonly ILogger logger;
        private readonly GrainInterfaceMap grainInterfaceMap;
        private readonly Dictionary<string, GrainTypeData> grainTypes;
        private readonly SerializationManager serializationManager;
		private readonly PlacementStrategy defaultPlacementStrategy;
        private Dictionary<int, Dictionary<ushort, List<SiloAddress>>> supportedSilosByInterface;

        internal IReadOnlyDictionary<SiloAddress, GrainInterfaceMap> GrainInterfaceMapsBySilo => this.grainInterfaceMapsBySilo;

        public IEnumerable<KeyValuePair<string, GrainTypeData>> GrainClassTypeData => this.grainTypes;

        public GrainInterfaceMap ClusterGrainInterfaceMap { get; private set; }

        public ILegacyGrainTypeResolver GrainTypeResolver { get; private set; }

        public GrainTypeManager(
            ILocalSiloDetails siloDetails,
            IApplicationPartManager applicationPartManager,
            PlacementStrategy defaultPlacementStrategy,
            SerializationManager serializationManager,
            ILogger<GrainTypeManager> logger,
            IOptions<GrainClassOptions> grainClassOptions)
        {
            var localTestMode = siloDetails.SiloAddress.Endpoint.Address.Equals(IPAddress.Loopback);
            this.logger = logger;
            this.defaultPlacementStrategy = defaultPlacementStrategy;
            this.serializationManager = serializationManager;
            grainInterfaceMap = new GrainInterfaceMap(localTestMode, this.defaultPlacementStrategy);
            ClusterGrainInterfaceMap = grainInterfaceMap;
            GrainTypeResolver = grainInterfaceMap.GetGrainTypeResolver();
            grainInterfaceMapsBySilo = new Dictionary<SiloAddress, GrainInterfaceMap>();

            var grainClassFeature = applicationPartManager.CreateAndPopulateFeature<GrainClassFeature>();
            this.grainTypes = CreateGrainTypeMap(grainClassFeature, grainClassOptions.Value);

            this.InitializeInterfaceMap();
        }

        public void Start()
        {
            LogGrainTypesFound(this.logger, this.grainTypes);
            this.serializationManager.LogRegisteredTypes();
        }

        internal bool TryGetPrimaryImplementation(string grainInterface, out string grainClass)
        {
            return grainInterfaceMap.TryGetPrimaryImplementation(grainInterface, out grainClass);
        }

        internal GrainTypeData this[string className]
        {
            get
            {
                string msg;

                lock (this)
                {
                    string grainType;

                    if (grainInterfaceMap.TryGetPrimaryImplementation(className, out grainType))
                        return grainTypes[grainType];
                    if (grainTypes.TryGetValue(className, out var data))
                        return data;

                    if (TypeUtils.IsGenericClass(className))
                    {
                        var templateName = TypeUtils.GetRawClassName(className);
                        if (grainInterfaceMap.TryGetPrimaryImplementation(templateName, out grainType))
                            templateName = grainType;

                        if (grainTypes.TryGetValue(templateName, out var template))
                        {
                            // Found the generic template class
                            try
                            {
                                // Instantiate the specific type from generic template
                                var genericGrainTypeData = (GenericGrainTypeData)template;
                                Type[] typeArgs = TypeUtils.GenericTypeArgsFromClassName(className);
                                var concreteTypeData = genericGrainTypeData.MakeGenericType(typeArgs);

                                // Add to lookup tables for next time
                                var grainClassName = concreteTypeData.GrainClass;
                                grainTypes.Add(grainClassName, concreteTypeData);

                                return concreteTypeData;
                            }
                            catch (Exception ex)
                            {
                                msg = "Cannot instantiate generic class " + className;
                                logger.Error(ErrorCode.Runtime_Error_100092, msg, ex);
                                throw new KeyNotFoundException(msg, ex);
                            }
                        }
                    }
                }

                msg = "Cannot find GrainTypeData for class " + className;
                logger.Error(ErrorCode.Runtime_Error_100093, msg);
                throw new TypeLoadException(msg);
            }
        }

        internal bool TryGetTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, string genericArguments = null)
        {
            return ClusterGrainInterfaceMap.TryGetTypeInfo(typeCode, out grainClass, out placement, genericArguments);
        }
        
        internal void GetTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, string genericArguments = null)
        {
            if (!ClusterGrainInterfaceMap.TryGetTypeInfo(typeCode, out grainClass, out placement, genericArguments))
                throw new OrleansException(string.Format("Unexpected: Cannot find an implementation class for grain interface {0}", typeCode));
        }

        internal void SetInterfaceMapsBySilo(Dictionary<SiloAddress, GrainInterfaceMap> value)
        {
            grainInterfaceMapsBySilo = value;
            RebuildFullGrainInterfaceMap();
        }

        internal IReadOnlyList<SiloAddress> GetSupportedSilos(int typeCode)
        {
            return supportedSilosByTypeCode[typeCode];
        }

        internal IReadOnlyDictionary<ushort, IReadOnlyList<SiloAddress>> GetSupportedSilos(int typeCode, int ifaceId, IReadOnlyList<ushort> versions)
        {
            var result = new Dictionary<ushort, IReadOnlyList<SiloAddress>>();
            foreach (var version in versions)
            {
                var silosWithTypeCode = supportedSilosByTypeCode[typeCode];
                // We need to sort this so the list of silos returned will
                // be the same accross all silos in the cluster
                var silosWithCorrectVersion = supportedSilosByInterface[ifaceId][version]
                    .Intersect(silosWithTypeCode)
                    .OrderBy(addr => addr)
                    .ToList();
                result[version] = silosWithCorrectVersion;
            }
            
            return result;
        }

        internal IReadOnlyList<ushort> GetAvailableVersions(int ifaceId)
        {
            return supportedSilosByInterface[ifaceId].Keys.ToList();
        }

        internal ushort GetLocalSupportedVersion(int ifaceId)
        {
            return grainInterfaceMap.GetInterfaceVersion(ifaceId);
        }

        private void InitializeInterfaceMap()
        {
            foreach (GrainTypeData grainType in grainTypes.Values)
                AddToGrainInterfaceToClassMap(grainType.Type, grainType.RemoteInterfaceTypes, grainType.IsStatelessWorker);
        }

        private void AddToGrainInterfaceToClassMap(Type grainClass, IEnumerable<Type> grainInterfaces, bool isUnordered)
        {
            var placement = GrainTypeData.GetPlacementStrategy(grainClass, this.defaultPlacementStrategy);
            var directory = GrainTypeData.GetGrainDirectory(grainClass);

            foreach (var iface in grainInterfaces)
            {
                var isPrimaryImplementor = IsPrimaryImplementor(grainClass, iface);
                grainInterfaceMap.AddEntry(iface, grainClass, placement, directory, isPrimaryImplementor);
            }

            if (isUnordered)
                grainInterfaceMap.AddToUnorderedList(grainClass);
        }


        private static bool IsPrimaryImplementor(Type grainClass, Type iface)
        {
            // If the class name exactly matches the interface name, it is considered the primary (default)
            // implementation of the interface, e.g. IFooGrain -> FooGrain
            return (iface.Name.Substring(1) == grainClass.Name);
        }

        public bool TryGetData(string name, out GrainTypeData result)
        {
            return grainTypes.TryGetValue(name, out result);
        }

        internal GrainInterfaceMap GetTypeCodeMap()
        {
            // the map is immutable at this point
            return grainInterfaceMap;
        }

        /// <summary>
        /// Returns a list of all graintypes in the system.
        /// </summary>
        /// <returns></returns>
        internal string[] GetGrainTypeList()
        {
            return grainTypes.Keys.ToArray();
        }

        private void RebuildFullGrainInterfaceMap()
        {
            var newClusterGrainInterfaceMap = new GrainInterfaceMap(false, this.defaultPlacementStrategy);
            var newSupportedSilosByTypeCode = new Dictionary<int, List<SiloAddress>>();
            var newSupportedSilosByInterface = new Dictionary<int, Dictionary<ushort, List<SiloAddress>>>();
            foreach (var kvp in grainInterfaceMapsBySilo)
            {
                newClusterGrainInterfaceMap.AddMap(kvp.Value);

                foreach (var supportedInterface in kvp.Value.SupportedInterfaces)
                {
                    var ifaceId = supportedInterface.InterfaceId;
                    var version = supportedInterface.InterfaceVersion;

                    var supportedSilosByVersion = newSupportedSilosByInterface.GetValueOrAddNew(ifaceId);
                    var supportedSilosForVersion = supportedSilosByVersion.GetValueOrAddNew(version);
                    supportedSilosForVersion.Add(kvp.Key);
                }

                foreach (var grainClassData in kvp.Value.SupportedGrainClassData)
                {
                    var grainType = grainClassData.GrainTypeCode;

                    var supportedSilos = newSupportedSilosByTypeCode.GetValueOrAddNew(grainType);
                    supportedSilos.Add(kvp.Key);
                }
            }
            foreach (var silos in newSupportedSilosByTypeCode.Values)
            {
                // We need to sort this so the list of silos returned will
                // be the same accross all silos in the cluster
                silos.Sort(); 
            }
            ClusterGrainInterfaceMap = newClusterGrainInterfaceMap;
            GrainTypeResolver = ClusterGrainInterfaceMap.GetGrainTypeResolver();
            supportedSilosByTypeCode = newSupportedSilosByTypeCode;
            supportedSilosByInterface = newSupportedSilosByInterface;
        }

        private static Dictionary<string, GrainTypeData> CreateGrainTypeMap(GrainClassFeature grainClassFeature, GrainClassOptions grainClassOptions)
        {
            var result = new Dictionary<string, GrainTypeData>();

            var excluded = grainClassOptions.ExcludedGrainTypes;
            foreach (var grainClassMetadata in grainClassFeature.Classes)
            {
                var grainType = grainClassMetadata.ClassType;
                var className = TypeUtils.GetFullName(grainType);

                if (excluded != null && excluded.Contains(className)) continue;

                var typeData = grainType.IsGenericTypeDefinition ?
                    new GenericGrainTypeData(grainType) :
                    new GrainTypeData(grainType);
                result[className] = typeData;
            }

            return result;
        }

        internal static void LogGrainTypesFound(ILogger logger, IDictionary<string, GrainTypeData> grainTypeData)
        {
            var sb = new StringBuilder();
            sb.AppendLine(String.Format("Loaded grain type summary for {0} types: ", grainTypeData.Count));

            foreach (var grainType in grainTypeData.Values.OrderBy(gtd => gtd.Type.FullName))
            {
                // Skip system targets and Orleans grains
                var assemblyName = grainType.Type.Assembly.FullName.Split(',')[0];
                if (!typeof(ISystemTarget).IsAssignableFrom(grainType.Type))
                {
                    int grainClassTypeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grainType.Type);
                    sb.AppendFormat("Grain class {0}.{1} [{2} (0x{3})] from {4}.dll implementing interfaces: ",
                        grainType.Type.Namespace,
                        TypeUtils.GetTemplatedName(grainType.Type),
                        grainClassTypeCode,
                        grainClassTypeCode.ToString("X"),
                        assemblyName);
                    var first = true;

                    foreach (var iface in grainType.RemoteInterfaceTypes)
                    {
                        if (!first)
                            sb.Append(", ");

                        sb.Append(TypeUtils.GetTemplatedName(iface));

                        if (GrainInterfaceUtils.IsGrainType(iface))
                        {
                            int ifaceTypeCode = GrainInterfaceUtils.GetGrainInterfaceId(iface);
                            sb.AppendFormat(" [{0} (0x{1})]", ifaceTypeCode, ifaceTypeCode.ToString("X"));
                        }
                        first = false;
                    }
                    sb.AppendLine();
                }
            }

            var report = sb.ToString();
            logger.Info(ErrorCode.Loader_GrainTypeFullList, report);
        }
    }
}

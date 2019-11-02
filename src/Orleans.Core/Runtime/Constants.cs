using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal class Constants
    {
        // This needs to be first, as GrainId static initializers reference it. Otherwise, GrainId actually see a uninitialized (ie Zero) value for that "constant"!
        public static readonly TimeSpan INFINITE_TIMESPAN = TimeSpan.FromMilliseconds(-1);

        // We assume that clock skew between silos and between clients and silos is always less than 1 second
        public static readonly TimeSpan MAXIMUM_CLOCK_SKEW = TimeSpan.FromSeconds(1);

        public const string DATA_CONNECTION_STRING_NAME = "DataConnectionString";
        public const string ADO_INVARIANT_NAME = "AdoInvariant";
        public const string DATA_CONNECTION_FOR_REMINDERS_STRING_NAME = "DataConnectionStringForReminders";
        public const string ADO_INVARIANT_FOR_REMINDERS_NAME = "AdoInvariantForReminders";

        public const string ORLEANS_CLUSTERING_AZURESTORAGE = "Orleans.Clustering.AzureStorage";
        public const string ORLEANS_REMINDERS_AZURESTORAGE = "Orleans.Reminders.AzureStorage";

        public const string ORLEANS_CLUSTERING_ADONET = "Orleans.Clustering.AdoNet";
        public const string ORLEANS_REMINDERS_ADONET = "Orleans.Reminders.AdoNet";

        public const string INVARIANT_NAME_SQL_SERVER = "System.Data.SqlClient";

        public const string ORLEANS_CLUSTERING_ZOOKEEPER = "Orleans.Clustering.ZooKeeper";
        public const string TroubleshootingHelpLink = "https://aka.ms/orleans-troubleshooting";

        public static readonly GrainType DirectoryServiceId = GrainType.CreateForSystemTarget("dir");
        public static readonly GrainType DirectoryCacheValidatorId = GrainType.CreateForSystemTarget("dir-cache");
        public static readonly GrainType SiloControlId = GrainType.CreateForSystemTarget("silo-control");
        public static readonly GrainType ClientObserverRegistrarId = GrainType.CreateForSystemTarget("client-observer-registrar");
        public static readonly GrainType CatalogId = GrainType.CreateForSystemTarget("catalog");
        public static readonly GrainType MembershipOracleId = GrainType.CreateForSystemTarget("membership-oracle");
        public static readonly GrainType TypeManagerId = GrainType.CreateForSystemTarget("type-manager");
        public static readonly GrainType FallbackSystemTargetId = GrainType.CreateForSystemTarget("fallback");
        public static readonly GrainType LifecycleSchedulingSystemTargetId = GrainType.CreateForSystemTarget("lifecycle");
        public static readonly GrainType DeploymentLoadPublisherSystemTargetId = GrainType.CreateForSystemTarget("cluster-load");
        public static readonly GrainType MultiClusterOracleId = GrainType.CreateForSystemTarget("multicluster-oracle");
        public static readonly GrainType ClusterDirectoryServiceId = GrainType.CreateForSystemTarget("gsi-directory");
        public static readonly GrainType TestHooksSystemTargetId = GrainType.CreateForSystemTarget("test-hooks");
        public static readonly GrainType GsiProtocolGateway = GrainType.CreateForSystemTarget("es-gw");
        public static readonly GrainType SystemMembershipTableId = GrainType.CreateForSystemTarget("dev-membership");

        public static readonly SpanId SiloDirectConnectionId = SpanId.Create("01111111-1111-1111-1111-111111111111");

        public static readonly GrainType StreamPullingAgentManagerType = GrainType.CreateForSystemTarget("stream-agent-mgr");
        public const int PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE = 254;
        public static readonly GrainType StreamPullingAgentType = GrainType.CreateForSystemTarget("stream-agent");
        public const int PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE = 255;

        internal const long ReminderTableGrainId = 12345;

        /// <summary>
        /// Minimum period for registering a reminder ... we want to enforce a lower bound
        /// </summary>
        public static readonly TimeSpan MinReminderPeriod = TimeSpan.FromMinutes(1); // increase this period, reminders are supposed to be less frequent ... we use 2 seconds just to reduce the running time of the unit tests
        /// <summary>
        /// Refresh local reminder list to reflect the global reminder table every 'REFRESH_REMINDER_LIST' period
        /// </summary>
        public static readonly TimeSpan RefreshReminderList = TimeSpan.FromMinutes(5);

        public const int LARGE_OBJECT_HEAP_THRESHOLD = 85000;

        public const int DEFAULT_LOGGER_BULK_MESSAGE_LIMIT = 5;

        public static readonly TimeSpan DEFAULT_CLIENT_DROP_TIMEOUT = TimeSpan.FromMinutes(1);

        private static readonly Dictionary<GrainType, string> singletonSystemTargetNames = new Dictionary<GrainType, string>
        {
            {DirectoryServiceId, "DirectoryService"},
            {DirectoryCacheValidatorId, "DirectoryCacheValidator"},
            {SiloControlId,"SiloControl"},
            {ClientObserverRegistrarId,"ClientObserverRegistrar"},
            {CatalogId,"Catalog"},
            {MembershipOracleId,"MembershipOracle"},
            {MultiClusterOracleId,"MultiClusterOracle"},
            {TypeManagerId,"TypeManagerId"},
            {GsiProtocolGateway,"ProtocolGateway"},
            {FallbackSystemTargetId, "FallbackSystemTarget"},
            {DeploymentLoadPublisherSystemTargetId, "DeploymentLoadPublisherSystemTarget"},
        };

        private static readonly Dictionary<int, string> nonSingletonSystemTargetNames = new Dictionary<int, string>
        {
            {PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE, "PullingAgentSystemTarget"},
            {PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE, "PullingAgentsManagerSystemTarget"},
        };

        public static ushort DefaultInterfaceVersion = 1;

        public static string SystemTargetName(GrainId id)
        {
            string name;
            if (singletonSystemTargetNames.TryGetValue(id, out name)) return name;
            if (nonSingletonSystemTargetNames.TryGetValue(id.TypeCode, out name)) return name;
            return String.Empty;
        }

        public static bool IsSingletonSystemTarget(GrainId id)
        {
            return singletonSystemTargetNames.ContainsKey(id);
        }
    }
}
 

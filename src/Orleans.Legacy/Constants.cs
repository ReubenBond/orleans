using System;
using System.Collections.Generic;

namespace Orleans.Legacy.Runtime
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

        public static readonly LegacyGrainId DirectoryServiceId = LegacyGrainId.GetSystemTargetGrainId(10);
        public static readonly LegacyGrainId DirectoryCacheValidatorId = LegacyGrainId.GetSystemTargetGrainId(11);
        public static readonly LegacyGrainId SiloControlId = LegacyGrainId.GetSystemTargetGrainId(12);
        public static readonly LegacyGrainId ClientObserverRegistrarId = LegacyGrainId.GetSystemTargetGrainId(13);
        public static readonly LegacyGrainId CatalogId = LegacyGrainId.GetSystemTargetGrainId(14);
        public static readonly LegacyGrainId MembershipOracleId = LegacyGrainId.GetSystemTargetGrainId(15);
        public static readonly LegacyGrainId TypeManagerId = LegacyGrainId.GetSystemTargetGrainId(17);
        public static readonly LegacyGrainId FallbackSystemTargetId = LegacyGrainId.GetSystemTargetGrainId(19);
        public static readonly LegacyGrainId LifecycleSchedulingSystemTargetId = LegacyGrainId.GetSystemTargetGrainId(20);
        public static readonly LegacyGrainId DeploymentLoadPublisherSystemTargetId = LegacyGrainId.GetSystemTargetGrainId(22);
        public static readonly LegacyGrainId MultiClusterOracleId = LegacyGrainId.GetSystemTargetGrainId(23);
        public static readonly LegacyGrainId ClusterDirectoryServiceId = LegacyGrainId.GetSystemTargetGrainId(24);
        public static readonly LegacyGrainId StreamProviderManagerAgentSystemTargetId = LegacyGrainId.GetSystemTargetGrainId(25);
        public static readonly LegacyGrainId TestHooksSystemTargetId = LegacyGrainId.GetSystemTargetGrainId(26);
        public static readonly LegacyGrainId ProtocolGatewayId = LegacyGrainId.GetSystemTargetGrainId(27);
        public static readonly LegacyGrainId TransactionAgentSystemTargetId = LegacyGrainId.GetSystemTargetGrainId(28);
        public static readonly LegacyGrainId SystemMembershipTableId = LegacyGrainId.GetSystemTargetGrainId(29);
        public static readonly LegacyGrainId SiloDirectConnectionId = LegacyGrainId.GetSystemGrainId(new Guid("01111111-1111-1111-1111-111111111111"));

        public const int PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE = 254;
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

        private static readonly Dictionary<LegacyGrainId, string> singletonSystemTargetNames = new Dictionary<LegacyGrainId, string>
        {
            {DirectoryServiceId, "DirectoryService"},
            {DirectoryCacheValidatorId, "DirectoryCacheValidator"},
            {SiloControlId,"SiloControl"},
            {ClientObserverRegistrarId,"ClientObserverRegistrar"},
            {CatalogId,"Catalog"},
            {MembershipOracleId,"MembershipOracle"},
            {TypeManagerId,"TypeManager"},
            {FallbackSystemTargetId, "Fallback"},
            {LifecycleSchedulingSystemTargetId, "LifecycleScheduling"},
            {DeploymentLoadPublisherSystemTargetId, "DeploymentLoadPublisher"},
            {MultiClusterOracleId,"MultiClusterOracle"},
            {ClusterDirectoryServiceId,"ClusterDirectoryService"},
            {StreamProviderManagerAgentSystemTargetId,"StreamProviderManagerAgent"},
            {TestHooksSystemTargetId,"TestHooks"},
            {ProtocolGatewayId,"ProtocolGateway"},
            {TransactionAgentSystemTargetId,"TransactionAgent"},
            {SystemMembershipTableId,"SystemMembershipTable"},
        };

        private static readonly Dictionary<int, string> nonSingletonSystemTargetNames = new Dictionary<int, string>
        {
            {PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE, "PullingAgentSystemTarget"},
            {PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE, "PullingAgentsManagerSystemTarget"},
        };

        public static ushort DefaultInterfaceVersion = 1;

        public static string SystemTargetName(LegacyGrainId id)
        {
            string name;
            if (singletonSystemTargetNames.TryGetValue(id, out name)) return name;
            if (nonSingletonSystemTargetNames.TryGetValue(id.TypeCode, out name)) return name;
            return String.Empty;
        }

        public static bool IsSingletonSystemTarget(LegacyGrainId id)
        {
            return singletonSystemTargetNames.ContainsKey(id);
        }
    }
}
 


using System;

namespace Orleans
{
    /// <summary>
    /// Lifecycle stages of an orlean service.  Cluster Client, or Silo
    /// </summary>
    public static class ServiceLifecycleStage
    {
        // ::FIRST::
        // Before lifecycle starts, OrleansTaskScheduler is started

        // ::LAST::
        // After lifecycle stops, OrleansTaskScheduler is stopped

        /// <summary>
        /// First valid stage in service's lifecycle
        /// </summary>
        public const int First = int.MinValue;

        // ::START::
        // Silo sets SystemStatus = Starting
        // Add AppDomain.CurrentDomain.ProcessExit hook (depending on ProcessExitHandlingOptions)
        // Configure .NET ThreadPool & .NET ServicePointManager (depending on PerformanceTuningOptions)
        // HostedClient starts message pump
        // InsiderRuntimeClient initializes internal data structures & starts callback expiry timer
        // InsideRuntimeClient calls GrainTypeManager.Start, which just logs registered types & returns
        // ClusterMembership begins processing membership updates
        // SiloStatusListenerManager begins processing membership updates

        // ::STOP::
        // MembershipAgent stops updating IAmAlive & stops processing membership updates
        // SiloStatusListenerManager stops processing membership updates
        // Silo stops IncomingMessageAgents
        // Silo stops Watchdog
        // Silo stops MessageCenter
        // Silo stops SiloStatisticsManager
        // Silo sets SystemStatus = Terminated
        // Silo disposes DI container  ########################################################################################## THIS IS BAD! DO THIS IN HOST ONLY #############################################
        // Silo signals termination

        /// <summary>
        /// Initialize runtime
        /// </summary>
        public const int RuntimeInitialize = 2_000;

        // ::START::
        // MembershipTableManager initializes table, performs first read, & begins periodic table refreshes
        // LocalGrainDirectory starts processing membership updates
        // LocalGrainDirectory starts GlobalSingleInstance maintainer
        // LocalGrainDirectory starts Directory cache maintainer
        // Silo starts MessageCenter
        // Silo starts IncomingMessageAgents (ping, system, application)
        // Silo initializes ImplicitStreamSubscriberTable
        // Silo creates/registers SystemTargets
        // Silo subscribes various ISiloStatusListeners to the ISiloStatusOracle
        // Silo starts Catalog's activation collector timer (Catalog.Start)

        // ::STOP::
        // LocalGrainDirectory stops processing membership updates
        // MembershipTableManager stops periodically refreshing the membership table
        // Silo blocks application messages in MessageCenter
        // Silo stops application turns in Scheduler

        /// <summary>
        /// Start runtime services
        /// </summary>
        public const int RuntimeServices = 4_000;

        // ::START::
        // Silo creates & registers GrainServices & calls Init() for each
        // Silo initializes type management with IVersionStore
        // Silo calls IMultiClusterOracle.Start
        // Silo calls SiloStatisticsManager.Start
        // Silo calls DeploymentLoadPublisher.Start
        // Silo starts Watchdog

        // ::STOP::
        // HostedClient calls IClusterClient.Close which should just set some internal state to disposed
        // TypeManager stops processing membership updates

        /// <summary>
        /// Start runtime services
        /// </summary>
        public const int RuntimeGrainServices = 8_000;

        // ::START::
        // MembershipAgent transitions the silo status to Joining in the membership table, causing a table refresh

        /// <summary>
        /// Transition into the Joining state in membership.
        /// After this stage:
        /// <list type="bullet">
        ///   <item>
        ///     <description>Other silos are able to see that this silo is joining the cluster.</description>
        ///   </item>
        ///   <item>
        ///     <description>Grain placement can be made via the grain directory on other silos only.</description>
        ///   </item>
        /// </list>
        /// </summary>
        public const int BecomeJoining = 9_000;

        // ::START::
        // *Legacy* storage providers start
        // TODO: ensure type manager is refreshed here?
        // 

        /// <summary>
        /// Initialize runtime storage
        /// </summary>
        public const int RuntimeStorageServices = 12_000;

        // ::START::
        // MembershipAgent transitions the silo status to Active in the membership table, causing a table refresh
        // LocalGrainDirectory waits for the table refresh to propagate to it
        // Silo starts the gateway ########################################################################################## Is this too early? #############################################

        // NOTE: Gateway should open ports but not accept connections

        // ::STOP::
        // LocalGrainDirectory stops ActivationCacheMaintainer, GsiActivationMaintainer
        // LocalGrainDirectory performs handoff (only if graceful cancellation)
        // LocalGrainDirectory clears local directory & cache data structures
        // MembershipAgent transitions silo status to Stopping / ShuttingDown (depending on cancellation token)
        // Silo deactivates all activations (if graceful)
        // Silo waits 2 seconds in hopes taht all messages will have been forwarded by then
        // Silo makes MessageCenter stop accepting client messages

        /// <summary>
        /// Transition into the Active state in membership.
        ///
        /// Before this stage:
        /// <list type="bullet">
        ///   <item>
        ///     <description>Services which are required for grain activation must be available.</description>
        ///   </item>
        /// </list>
        /// 
        /// After this stage:
        /// <list type="bullet">
        ///   <item>
        ///     <description>Grain placement can be made via the grain directory on this silos as well as other silos.</description>
        ///   </item>
        ///   <item>
        ///     <description>Grains can be activated on this silo.</description>
        ///   </item>
        /// </list>
        /// </summary>
        public const int BecomeActive = 10_000;

        public const int AcceptGrains = 12_000;

        // ::START::
        // Non-legacy storage providers start (by default)
        // Grain-based grain storage starts
        // HostedClient starts
        // Grain-based Reminders are enabled
        // Grain-based Versioning is enabled

        // NOTE:
        // Gateway starts accepting connections

        // ::STOP::
        // Grain-based reminders are disabled

        /// <summary>
        /// Start application layer services.
        /// </summary>
        public const int ApplicationServices = 14_000;


        // ::START::
        // (User-defined) IStartupTasks start (by default)

        /// <summary>
        /// User-defined startup tasks run at this stage.
        /// </summary>
        public const int UserStartupTasks = 19_000;

        // ::START::
        // Transaction agent statistics start
        // Membership table cleanup agent starts
        // Persistent stream provider starts (by default) ########################################################################################## Is this too LATE?? #############################################
        // Silo starts IReminderService  ########################################################################################## Is this too LATE?? (should be one stage earlier?) #############################################
        // Silo starts GrainServices (GrainService.Start)
        // ClusterHealthMonitor begins monitoring other silos

        // ::STOP::
        // ClusterHealthMonitor stops monitoring other silos
        // Membership table cleanup agent stops
        // Silo stops IReminderService ########################################################################################## Is this too early? Grains are still running #############################################
        // Silo stops GrainServices (GrainService.Stop)

        /// <summary>
        /// Service is active.
        /// </summary>
        public const int Active = 20_000;

        /// <summary>
        /// Last valid stage in service's lifecycle
        /// </summary>
        public const int Last = int.MaxValue;
    }
}

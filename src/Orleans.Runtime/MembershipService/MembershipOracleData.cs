using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using System.Text;
using Orleans.Configuration;
using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.Serialization;
using Microsoft.Extensions.Options;

namespace Orleans.Runtime.MembershipService
{
    internal sealed class MembershipTableSnapshot
    {
        public MembershipTableSnapshot(
            MembershipVersion version,
            ImmutableDictionary<SiloAddress, MembershipEntry> entries)
        {
            this.Version = version;
            this.Entries = entries;

            var statuses = ImmutableDictionary.CreateBuilder<SiloAddress, SiloStatus>();
            var activeStatuses = ImmutableDictionary.CreateBuilder<SiloAddress, SiloStatus>();
            var names = ImmutableDictionary.CreateBuilder<SiloAddress, string>();
            foreach (var item in entries)
            {
                var entry = item.Value;
                statuses.Add(item.Key, entry.Status);
                if (entry.Status == SiloStatus.Active)
                {
                    activeStatuses.Add(item.Key, entry.Status);
                }

                names.Add(item.Key, entry.SiloName);
            }

            this.localTableCopy = statuses.ToImmutable();
            this.localTableCopyOnlyActive = activeStatuses.ToImmutable();
            this.localNamesTableCopy = names.ToImmutable();
        }

        public static MembershipTableSnapshot Create(MembershipTableData table)
        {
            var entries = ImmutableDictionary.CreateBuilder<SiloAddress, MembershipEntry>();
            foreach (var item in table.Members)
            {
                var entry = item.Item1;
                entries.Add(entry.SiloAddress, entry);
            }

            var version = new MembershipVersion(table.Version.Version);
            return new MembershipTableSnapshot(version, entries.ToImmutable());
        }

        public MembershipVersion Version { get; }

        public ImmutableDictionary<SiloAddress, MembershipEntry> Entries { get; }

        /// <summary>
        /// A cached copy of a local table, including current silo, for fast access.
        /// </summary>
        public ImmutableDictionary<SiloAddress, SiloStatus> localTableCopy { get; }

        /// <summary>
        /// A cached copy of a local table, for fast access, including only active nodes and current silo (if active).
        /// </summary>
        public ImmutableDictionary<SiloAddress, SiloStatus> localTableCopyOnlyActive { get; }

        /// <summary>
        /// A copy of a map from SiloAddress to Silo Name for fast access.
        /// </summary>
        public ImmutableDictionary<SiloAddress, string> localNamesTableCopy { get; }
    }

    internal class MembershipTableManager
    {
        private readonly IMembershipTable membershipTable;
        private readonly ILocalSiloDetails localSiloDetails;

        public MembershipTableManager(IMembershipTable membershipTable, ILocalSiloDetails localSiloDetails)
        {
            this.membershipTable = membershipTable;
            this.localSiloDetails = localSiloDetails;
        }

        public MembershipTableSnapshot CurrentTable { get; }
        public ChangeFeedEntry<MembershipTableSnapshot> TableUpdates { get; }

        public Task UpdateIAmAlive(MembershipEntry entry) => throw new NotImplementedException();
    }

    internal interface IFatalErrorHandler
    {
        void OnFatalException(object sender, string context, Exception exception);
    }

    internal class FatalErrorHandler : IFatalErrorHandler
    {
        private readonly ILogger<FatalErrorHandler> log;

        public FatalErrorHandler(ILogger<FatalErrorHandler> log)
        {
            this.log = log;
        }

        public void OnFatalException(object sender, string context, Exception exception)
        {
            var msg = $"FATAL EXCEPTION from {sender?.ToString() ?? "null"}. Context: {context}. Exception: {LogFormatter.PrintException(exception)}.\nCurrent stack: {Environment.StackTrace}";
            this.log.LogError((int)ErrorCode.Logger_ProcessCrashing, msg);


            // TODO: Should we initiate shutdown instead? might be worth having two methods, one for failfast and one for shutdown? Hard to reason about shutdown in these cases...
            // Can also signal shutdown using IApplicationLifetime, perhaps.


            Environment.FailFast(msg);
        }
    }

    internal interface IMembershipService
    {
        /// <summary>
        /// A snapshot of the current cluster membership.
        /// </summary>
        ClusterMembershipSnapshot CurrentMembership { get; }

        /// <summary>
        /// Updates to the current cluster membership.
        /// </summary>
        ChangeFeedEntry<ClusterMembershipSnapshot> MembershipUpdates { get; }
    }

    internal class MembershipService : IMembershipService, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        private readonly MembershipTableManager tableManager;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ILocalSiloDetails localSilo;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly ILogger<MembershipService> log;
        private readonly ChangeFeedSource<ClusterMembershipSnapshot> updates;
        private Task processUpdatesTask;

        public MembershipService(
            MembershipTableManager tableManager,
            ILocalSiloDetails localSilo,
            IFatalErrorHandler fatalErrorHandler,
            ILogger<MembershipService> log)
        {
            this.tableManager = tableManager;
            this.localSilo = localSilo;
            this.fatalErrorHandler = fatalErrorHandler;
            this.log = log;
            this.CurrentMembership = this.Create(tableManager.CurrentTable);
            this.updates = new ChangeFeedSource<ClusterMembershipSnapshot>(this.CurrentMembership);
        }

        public ClusterMembershipSnapshot CurrentMembership { get; private set; }

        public ChangeFeedEntry<ClusterMembershipSnapshot> MembershipUpdates => this.updates.Current;

        private async Task ProcessUpdates()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();
            var current = this.tableManager.TableUpdates;

            this.log.LogInformation($"Starting {nameof(MembershipService)}");
            try
            {
                while (!this.cancellation.IsCancellationRequested)
                {
                    var next = current.NextAsync();

                    // Handle graceful termination.
                    var task = await Task.WhenAny(next, cancellationTask);
                    if (ReferenceEquals(task, cancellationTask)) break;

                    current = next.GetAwaiter().GetResult();

                    if (!current.HasValue)
                    {
                        this.log.LogWarning("Received a membership update with no data");
                        continue;
                    }

                    var snapshot = this.Create(current.Value);
                    this.CurrentMembership = snapshot;
                    this.updates.Publish(snapshot);
                }
            }
            catch (Exception exception)
            {
                // Any exception here is fatal
                this.log.LogError("Error processing membership updates: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessUpdates), exception);
            }
            finally
            {
                this.log.LogInformation($"Shutting down {nameof(MembershipService)}");
            }
        }

        private ClusterMembershipSnapshot Create(MembershipTableSnapshot table) => ClusterMembershipSnapshot.Create(this.localSilo.SiloAddress, table);

        public void Participate(ISiloLifecycle lifecycle) => lifecycle.Subscribe(ServiceLifecycleStage.RuntimeInitialize, this);

        public Task OnStart(CancellationToken ct)
        {
            this.processUpdatesTask = this.ProcessUpdates();
            return Task.CompletedTask;
        }

        public Task OnStop(CancellationToken ct)
        {
            this.cancellation.Cancel(throwOnFirstException: false);
            return this.processUpdatesTask ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Responsible for updating membership table with details about the local silo.
    /// </summary>
    internal class MembershipAgent : ILifecycleParticipant<ISiloLifecycle>
    {
        // Subscribe to membership table updates, listen for death declarations about this silo, kill local silo when declared dead (if not shutting down).
        // Subscribe to silo lifecycle, reflect changes in membership table.
        // Periodically update IAmAlive row in table.

        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly MembershipTableManager tableManager;
        private readonly ILocalSiloDetails localSilo;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly ClusterMembershipOptions clusterMembershipOptions;
        private readonly ILogger<MembershipAgent> log;
        private readonly int updateLivenessPeriodMilliseconds;
        private SiloStatus expectedStatus;

        public MembershipAgent(
            MembershipTableManager tableManager,
            ILocalSiloDetails localSilo,
            IFatalErrorHandler fatalErrorHandler,
            IOptions<ClusterMembershipOptions> options,
            ILogger<MembershipAgent> log)
        {
            this.tableManager = tableManager;
            this.localSilo = localSilo;
            this.fatalErrorHandler = fatalErrorHandler;
            this.clusterMembershipOptions = options.Value;
            this.updateLivenessPeriodMilliseconds = (int)this.clusterMembershipOptions.IAmAliveTablePublishTimeout.TotalMilliseconds;
            this.log = log;
        }
        
        private async Task ProcessUpdates()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();
            var current = this.tableManager.TableUpdates;

            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting to process membership updates");
            try
            {
                while (!this.cancellation.IsCancellationRequested)
                {
                    var next = current.NextAsync();

                    // Handle graceful termination.
                    var task = await Task.WhenAny(next, cancellationTask);
                    if (this.expectedStatus.IsTerminating() || ReferenceEquals(task, cancellationTask)) break;

                    current = next.GetAwaiter().GetResult();

                    if (!current.HasValue)
                    {
                        this.log.LogWarning("Received a membership update with no data");
                        continue;
                    }

                    var snapshot = current.Value;
                    if (!snapshot.Entries.TryGetValue(this.localSilo.SiloAddress, out var entry))
                    {
                        throw new OrleansMissingMembershipEntryException();
                    }

                    // Check to see if this silo has been declared dead.
                    if (entry.Status == SiloStatus.Dead && !this.expectedStatus.IsTerminating())
                    {
                        var message = $"{OrleansSiloDeclaredDeadException.BaseMessage} Membership record: {entry.ToFullString()}";
                        this.log.LogError((int)ErrorCode.MembershipKillMyselfLocally, message);
                        throw new OrleansSiloDeclaredDeadException(message);
                    }
                }
            }
            catch (OrleansSiloDeclaredDeadException exception)
            {
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessUpdates), exception);
            }
            catch (Exception exception)
            {
                this.log.LogError("Error processing membership updates: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessUpdates), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping membership update processor");
            }
        }

        private async Task UpdateLiveness()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();

            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting periodic membership liveness timestamp updates");
            try
            {
                var delayMilliseconds = this.updateLivenessPeriodMilliseconds;
                while (!this.cancellation.IsCancellationRequested)
                {
                    var next = Task.Delay(delayMilliseconds);

                    // Handle graceful termination.
                    var task = await Task.WhenAny(next, cancellationTask);
                    if (this.expectedStatus.IsTerminating() || ReferenceEquals(task, cancellationTask)) break;

                    var snapshot = this.tableManager.CurrentTable;

                    if (!snapshot.Entries.TryGetValue(this.localSilo.SiloAddress, out var entry))
                    {
                        throw new OrleansMissingMembershipEntryException();
                    }

                    try
                    {
                        var stopwatch = ValueStopwatch.StartNew();
                        await this.tableManager.UpdateIAmAlive(entry);
                        stopwatch.Stop();
                        if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace("Updating liveness for entry {Entry} took {Elapsed}", entry, stopwatch.Elapsed);
                        delayMilliseconds = Math.Max(this.updateLivenessPeriodMilliseconds - (int)stopwatch.Elapsed.TotalMilliseconds, 0);
                    }
                    catch (Exception exception)
                    {
                        this.log.LogError(
                            (int)ErrorCode.MembershipUpdateIAmAliveFailure,
                            "Failed to update table entry for this silo, will retry shortly: {Exception}",
                            exception);

                        // Retry quickly
                        delayMilliseconds = 1_000;
                    }
                }
            }
            catch (Exception exception)
            {
                this.log.LogError("Error updating liveness timestamp: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(UpdateLiveness), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping periodic membership liveness timestamp updates");
            }
        }

        private async Task CleanupDefunctEntries()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();

            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting periodic membership liveness timestamp updates");
            try
            {
                var delayMilliseconds = this.updateLivenessPeriodMilliseconds;
                while (!this.cancellation.IsCancellationRequested)
                {
                    var next = Task.Delay(delayMilliseconds);

                    // Handle graceful termination.
                    var task = await Task.WhenAny(next, cancellationTask);
                    if (this.expectedStatus.IsTerminating() || ReferenceEquals(task, cancellationTask)) break;

                    var snapshot = this.tableManager.CurrentTable;

                    if (!snapshot.Entries.TryGetValue(this.localSilo.SiloAddress, out var entry))
                    {
                        throw new OrleansMissingMembershipEntryException();
                    }

                    try
                    {
                        var stopwatch = ValueStopwatch.StartNew();
                        await this.tableManager.UpdateIAmAlive(entry);
                        stopwatch.Stop();
                        if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace("Updating liveness for entry {Entry} took {Elapsed}", entry, stopwatch.Elapsed);
                        delayMilliseconds = Math.Max(this.updateLivenessPeriodMilliseconds - (int)stopwatch.Elapsed.TotalMilliseconds, 0);
                    }
                    catch (Exception exception)
                    {
                        this.log.LogError(
                            (int)ErrorCode.MembershipUpdateIAmAliveFailure,
                            "Failed to update table entry for this silo, will retry shortly: {Exception}",
                            exception);

                        // Retry quickly
                        delayMilliseconds = 1_000;
                    }
                }
            }
            catch (Exception exception)
            {
                this.log.LogError("Error updating liveness timestamp: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(UpdateLiveness), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping periodic membership liveness timestamp updates");
            }
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            var becomeActiveTasks = new List<Task>();

            lifecycle.Subscribe(nameof(MembershipAgent), ServiceLifecycleStage.BecomeActive, OnBecomeActiveStart, OnBecomeActiveStop);

            Task OnBecomeActiveStart(CancellationToken ct)
            {
                becomeActiveTasks.Add(this.ProcessUpdates());
                becomeActiveTasks.Add(this.UpdateLiveness());
                return Task.CompletedTask;
            }

            Task OnBecomeActiveStop(CancellationToken ct)
            {
                this.cancellation.Cancel(throwOnFirstException: false);
                return Task.WhenAll(becomeActiveTasks);
            }
        }
    }

    public class OrleansMissingMembershipEntryException : OrleansException
    {
        public OrleansMissingMembershipEntryException() : base("Membership table does not contain information an entry for this silo.") { }

        public OrleansMissingMembershipEntryException(string message) : base(message) { }

        public OrleansMissingMembershipEntryException(string message, Exception innerException) : base(message, innerException) { }

        public OrleansMissingMembershipEntryException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    public class OrleansSiloDeclaredDeadException : OrleansException
    {
        public const string BaseMessage = "This silo has been declared dead.";

        public OrleansSiloDeclaredDeadException() : base(BaseMessage) { }

        public OrleansSiloDeclaredDeadException(string message) : base(message) { }

        public OrleansSiloDeclaredDeadException(string message, Exception innerException) : base(message, innerException) { }

        public OrleansSiloDeclaredDeadException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Responsible for ensuring that this silo monitors other silos in the cluster.
    /// </summary>
    public class ClusterHealthMonitor
    {
        // Subscribe to membership table, listen for new silos, manage collection of SiloHealthMonitor for each silo which should be monitored.
        // Monitor local SiloHealthMonitors & reflect actions in membership table.
    }

    /// <summary>
    /// Responsible for monitoring an individual remote silo.
    /// </summary>
    public class SiloHealthMonitor
    {
        // Periodically ping an individual silo to track silo health.
        // Expose decisions: add/remove self as suspector (i.e, vote), declare dead.
    }

    internal class MembershipOracleData
    {
        private readonly Dictionary<SiloAddress, MembershipEntry> localTable;  // all silos not including current silo
        private Dictionary<SiloAddress, SiloStatus> localTableCopy;            // a cached copy of a local table, including current silo, for fast access
        private Dictionary<SiloAddress, SiloStatus> localTableCopyOnlyActive;  // a cached copy of a local table, for fast access, including only active nodes and current silo (if active)
        private Dictionary<SiloAddress, string> localNamesTableCopy;           // a cached copy of a map from SiloAddress to Silo Name, not including current silo, for fast access
        private List<SiloAddress> localMultiClusterGatewaysCopy;               // a cached copy of the silos that are designated gateways

        private readonly List<ISiloStatusListener> statusListeners;
        private readonly ILogger logger;
        
        private IntValueStatistic clusterSizeStatistic;
        private StringValueStatistic clusterStatistic;

        internal readonly DateTime SiloStartTime;
        internal readonly SiloAddress MyAddress;
        internal readonly int MyProxyPort;
        internal readonly string MyHostname;
        internal SiloStatus CurrentStatus { get; private set; } // current status of this silo.
        internal string SiloName { get; } // name of this silo.

        private readonly bool multiClusterActive; // set by configuration if multicluster is active
        private readonly int maxMultiClusterGateways; // set by configuration

        private UpdateFaultCombo myFaultAndUpdateZones;
        
        internal MembershipOracleData(ILocalSiloDetails siloDetails, ILogger log, MultiClusterOptions multiClusterOptions)
        {
            logger = log;
            localTable = new Dictionary<SiloAddress, MembershipEntry>();  
            localTableCopy = new Dictionary<SiloAddress, SiloStatus>();       
            localTableCopyOnlyActive = new Dictionary<SiloAddress, SiloStatus>();
            localNamesTableCopy = new Dictionary<SiloAddress, string>();  
            localMultiClusterGatewaysCopy = new List<SiloAddress>();
            statusListeners = new List<ISiloStatusListener>();
            
            SiloStartTime = DateTime.UtcNow;
            MyAddress = siloDetails.SiloAddress;
            MyHostname = siloDetails.DnsHostName;
            MyProxyPort = siloDetails.GatewayAddress?.Endpoint?.Port ?? 0;
            SiloName = siloDetails.Name;
            this.multiClusterActive = multiClusterOptions.HasMultiClusterNetwork;
            this.maxMultiClusterGateways = multiClusterOptions.MaxMultiClusterGateways;
            CurrentStatus = SiloStatus.Created;
            clusterSizeStatistic = IntValueStatistic.FindOrCreate(StatisticNames.MEMBERSHIP_ACTIVE_CLUSTER_SIZE, () => localTableCopyOnlyActive.Count);
            clusterStatistic = StringValueStatistic.FindOrCreate(StatisticNames.MEMBERSHIP_ACTIVE_CLUSTER,
                    () => 
                        {
                            List<string> list = localTableCopyOnlyActive.Keys.Select(addr => addr.ToLongString()).ToList();
                            list.Sort();
                            return Utils.EnumerableToString(list);
                        });
        }

        // ONLY access localTableCopy and not the localTable, to prevent races, as this method may be called outside the turn.
        internal SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress)
        {
            var statuses = localTableCopy;
            var status = SiloStatus.None;
            if (siloAddress.Equals(MyAddress))
            {
                status = CurrentStatus;
            }
            else
            {
                if (!statuses.TryGetValue(siloAddress, out status))
                {
                    if (CurrentStatus == SiloStatus.Active)
                        if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Runtime_Error_100209, "-The given siloAddress {0} is not registered in this MembershipOracle.", siloAddress.ToLongString());

                    status = SiloStatus.None;
                }
            }

            if (status == SiloStatus.None)
            {
                foreach (var entry in statuses)
                {
                    if (entry.Key.IsSuccessorOf(siloAddress))
                    {
                        status = SiloStatus.Dead;
                        break;
                    }
                }
            }

            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("-GetApproximateSiloStatus returned {0} for silo: {1}", status, siloAddress.ToLongString());
            return status;
        }

        // ONLY access localTableCopy or localTableCopyOnlyActive and not the localTable, to prevent races, as this method may be called outside the turn.
        internal Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            Dictionary<SiloAddress, SiloStatus> dict = onlyActive ? localTableCopyOnlyActive : localTableCopy;
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("-GetApproximateSiloStatuses returned {0} silos: {1}", dict.Count, Utils.DictionaryToString(dict));
            return dict;
        }

        internal List<SiloAddress> GetApproximateMultiClusterGateways()
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("-GetApproximateMultiClusterGateways returned {0} silos: {1}", localMultiClusterGatewaysCopy.Count, string.Join(",", localMultiClusterGatewaysCopy));
            return localMultiClusterGatewaysCopy;
        }

        internal bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
        {
            if (siloAddress.Equals(MyAddress))
            {
                siloName = SiloName;
                return true;
            }
            return localNamesTableCopy.TryGetValue(siloAddress, out siloName);
        }

        internal bool SubscribeToSiloStatusEvents(ISiloStatusListener observer)
        {
            lock (statusListeners)
            {
                if (statusListeners.Contains(observer))
                    return false;
                
                statusListeners.Add(observer);
                return true;
            }
        }

        internal bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer)
        {
            lock (statusListeners)
            {
                return statusListeners.Contains(observer) && statusListeners.Remove(observer);
            }
        }

        internal void UpdateMyStatusLocal(SiloStatus status)
        {
            if (CurrentStatus == status) return;

            // make copies
            var tmpLocalTableCopy = GetSiloStatuses(st => true, true); // all the silos including me.
            var tmpLocalTableCopyOnlyActive = GetSiloStatuses(st => st == SiloStatus.Active, true);    // only active silos including me.
            var tmpLocalTableNamesCopy = localTable.ToDictionary(pair => pair.Key, pair => pair.Value.SiloName);   // all the silos excluding me.

            CurrentStatus = status;

            tmpLocalTableCopy[MyAddress] = status;

            if (status == SiloStatus.Active)
            {
                tmpLocalTableCopyOnlyActive[MyAddress] = status;
            }
            else if (tmpLocalTableCopyOnlyActive.ContainsKey(MyAddress))
            {
                tmpLocalTableCopyOnlyActive.Remove(MyAddress);
            }
            localTableCopy = tmpLocalTableCopy;
            localTableCopyOnlyActive = tmpLocalTableCopyOnlyActive;
            localNamesTableCopy = tmpLocalTableNamesCopy;

            if (this.multiClusterActive)
                localMultiClusterGatewaysCopy = DetermineMultiClusterGateways();

            NotifyLocalSubscribers(MyAddress, CurrentStatus);
        }

        private SiloStatus GetSiloStatus(SiloAddress siloAddress)
        {
            if (siloAddress.Equals(MyAddress))
                return CurrentStatus;
            
            MembershipEntry data;
            return !localTable.TryGetValue(siloAddress, out data) ? SiloStatus.None : data.Status;
        }

        internal MembershipEntry GetSiloEntry(SiloAddress siloAddress)
        {
            return localTable[siloAddress];
        }

        internal Dictionary<SiloAddress, SiloStatus> GetSiloStatuses(Func<SiloStatus, bool> filter, bool includeMyself)
        {
            Dictionary<SiloAddress, SiloStatus> dict = localTable.Where(
                pair => filter(pair.Value.Status)).ToDictionary(pair => pair.Key, pair => pair.Value.Status);

            if (includeMyself && filter(CurrentStatus)) // add myself
                dict.Add(MyAddress, CurrentStatus);
            
            return dict;
        }

        internal MembershipEntry CreateNewMembershipEntry(SiloStatus myStatus)
        {
            return CreateNewMembershipEntry(SiloName, MyAddress, MyProxyPort, MyHostname, myStatus, SiloStartTime);
        }

        private static MembershipEntry CreateNewMembershipEntry(string siloName, SiloAddress myAddress, int proxyPort, string myHostname, SiloStatus myStatus, DateTime startTime)
        {
            var assy = Assembly.GetEntryAssembly() ?? typeof(MembershipOracleData).Assembly;
            var roleName = assy.GetName().Name;

            var entry = new MembershipEntry
            {
                SiloAddress = myAddress,

                HostName = myHostname,
                SiloName = siloName,

                Status = myStatus,
                ProxyPort = proxyPort,

                RoleName = roleName,
                
                SuspectTimes = new List<Tuple<SiloAddress, DateTime>>(),
                StartTime = startTime,
                IAmAliveTime = DateTime.UtcNow
            };
            return entry;
        }

        internal void UpdateMyFaultAndUpdateZone(MembershipEntry entry)
        {
            this.myFaultAndUpdateZones = new UpdateFaultCombo(entry.UpdateZone, entry.FaultZone);

            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug($"-Updated my FaultZone={entry.FaultZone} UpdateZone={entry.UpdateZone}");

            if (this.multiClusterActive)
                localMultiClusterGatewaysCopy = DetermineMultiClusterGateways();
        }

        internal bool TryUpdateStatusAndNotify(MembershipEntry entry)
        {
            if (!TryUpdateStatus(entry)) return false;

            localTableCopy = GetSiloStatuses(status => true, true); // all the silos including me.
            localTableCopyOnlyActive = GetSiloStatuses(status => status == SiloStatus.Active, true);    // only active silos including me.
            localNamesTableCopy = localTable.ToDictionary(pair => pair.Key, pair => pair.Value.SiloName);   // all the silos excluding me.

            if (this.multiClusterActive)
                localMultiClusterGatewaysCopy = DetermineMultiClusterGateways();

            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("-Updated my local view of {0} status. It is now {1}.", entry.SiloAddress.ToLongString(), GetSiloStatus(entry.SiloAddress));

            NotifyLocalSubscribers(entry.SiloAddress, entry.Status);
            return true;
        }

        // return true if the status changed
        private bool TryUpdateStatus(MembershipEntry updatedSilo)
        {
            MembershipEntry currSiloData = null;
            if (!localTable.TryGetValue(updatedSilo.SiloAddress, out currSiloData))
            {
                // an optimization - if I learn about dead silo and I never knew about him before, I don't care, can just ignore him.
                if (updatedSilo.Status == SiloStatus.Dead) return false;

                localTable.Add(updatedSilo.SiloAddress, updatedSilo);
                return true;
            }

            if (currSiloData.Status == updatedSilo.Status) return false;

            currSiloData.Update(updatedSilo);
            return true;
        }

        private void NotifyLocalSubscribers(SiloAddress siloAddress, SiloStatus newStatus)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("-NotifyLocalSubscribers about {0} status {1}", siloAddress.ToLongString(), newStatus);
            
            List<ISiloStatusListener> copy;
            lock (statusListeners)
            {
                copy = statusListeners.ToList();
            }

            foreach (ISiloStatusListener listener in copy)
            {
                try
                {
                    listener.SiloStatusChangeNotification(siloAddress, newStatus);
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.MembershipLocalSubscriberException,
                        String.Format("Local ISiloStatusListener {0} has thrown an exception when was notified about SiloStatusChangeNotification about silo {1} new status {2}",
                        listener.GetType().FullName, siloAddress.ToLongString(), newStatus), exc);
                }
            }
        }

        // deterministic function for designating the silos that should act as multi-cluster gateways
        private List<SiloAddress> DetermineMultiClusterGateways()
        {
            // function should never be called if we are not in a multicluster
            if (! this.multiClusterActive)
                throw new OrleansException("internal error: should not call this function without multicluster network");

            List<SiloAddress> result;

            // take all the active silos if their count does not exceed the desired number of gateways
            if (localTableCopyOnlyActive.Count <= this.maxMultiClusterGateways)
            {
                result = localTableCopyOnlyActive.Keys.ToList();
            }
            else
            {
                result = MembershipHelper.DeterministicBalancedChoice<SiloAddress, UpdateFaultCombo>(
                    localTableCopyOnlyActive.Keys,
                    this.maxMultiClusterGateways,
                   (SiloAddress a) => a.Equals(MyAddress) ? this.myFaultAndUpdateZones : new UpdateFaultCombo(localTable[a]),
                   logger);
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var gateways = string.Join(", ", result.Select(silo => silo.ToString()));
                logger.Debug($"-DetermineMultiClusterGateways {gateways}");
            }

            return result;
        }

        internal struct UpdateFaultCombo : IComparable
        {
            public readonly int UpdateZone;
            public readonly int FaultZone;

            public UpdateFaultCombo(int updateZone, int faultZone)
            {
                UpdateZone = updateZone;
                FaultZone = faultZone;
            }

            public UpdateFaultCombo(MembershipEntry e)
            {
                UpdateZone = e.UpdateZone;
                FaultZone = e.FaultZone;
            }

            public int CompareTo(object x)
            {
                var other = (UpdateFaultCombo)x;
                int comp = UpdateZone.CompareTo(other.UpdateZone);
                if (comp != 0) return comp;
                return FaultZone.CompareTo(other.FaultZone);
            }

            public override string ToString()
            {
                return $"({UpdateZone},{FaultZone})";
            }
        }

        public override string ToString()
        {
            return string.Format("CurrentSiloStatus = {0}, {1} silos: {2}.",
                CurrentStatus,
                localTableCopy.Count,
                Utils.EnumerableToString(localTableCopy, pair => 
                    String.Format("SiloAddress={0} Status={1}", pair.Key.ToLongString(), pair.Value)));
        }
    }

    internal static class MembershipHelper
    {
        // pick a specified number of elements from a set of candidates
        // - in a balanced way (try to pick evenly from groups)
        // - in a deterministic way (using sorting order on candidates and keys)
        internal static List<T> DeterministicBalancedChoice<T, K>(IEnumerable<T> candidates, int count, Func<T, K> group, ILogger logger = null)
            where T : IComparable where K : IComparable
        {
            // organize candidates by groups
            var groups = new Dictionary<K, List<T>>();
            var keys = new List<K>();
            int numcandidates = 0;
            foreach (var c in candidates)
            {
                var key = group(c);
                List<T> list;
                if (!groups.TryGetValue(key, out list))
                {
                    groups[key] = list = new List<T>();
                    keys.Add(key);
                }
                list.Add(c);
                numcandidates++;
            }

            if (numcandidates < count)
                throw new ArgumentException("not enough candidates");

            // sort the keys and the groups to guarantee deterministic result
            keys.Sort();
            foreach (var kvp in groups)
                kvp.Value.Sort();

            // for debugging, trace all the gateway candidates
            if (logger != null && logger.IsEnabled(LogLevel.Trace))
            {
                var b = new StringBuilder();
                foreach (var k in keys)
                {
                    b.Append(k);
                    b.Append(':');
                    foreach (var s in groups[k])
                    {
                        b.Append(' ');
                        b.Append(s);
                    }
                }
                logger.Trace($"-DeterministicBalancedChoice candidates {b}");
            }

            // pick round-robin from groups
            var result = new List<T>();
            for (int i = 0; result.Count < count; i++)
            {
                var list = groups[keys[i % keys.Count]];
                var col = i / keys.Count;
                if (col < list.Count)
                    result.Add(list[col]);
            }
            return result;
        }
    }
}

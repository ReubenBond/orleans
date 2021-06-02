using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using AsyncEx = Nito.AsyncEx;

namespace Orleans.MetadataStore
{
    public struct ClusterMembersUpdate
    {
        public ClusterMembersUpdate(SiloAddress[] members)
        {
            Members = members;
        }

        public SiloAddress[] Members { get; }
    }

    internal interface IInternalConfigurationManager
    {
        ConfigProposer Proposer { get; }
        ConfigAcceptor Acceptor { get; }
    }

    public interface ILocalConfiguration
    {
        ClusterConfiguration CommittedConfiguration { get; }
        void OnCommittedConfiguration(ClusterConfiguration state);
    }

    /// <summary>
    /// The Configuration Manager is responsible for coordinating configuration (cluster membership) changes
    /// across the cluster.
    /// </summary>
    /// <remarks>
    /// From a high level, cluster configuration is stored in a special-purpose shared register. The linearizability
    /// properties of this register are used to ensure that safety requirements of the system are not violated.
    /// In particular, at no point in time and under no situation 
    /// </remarks>
    public class ConfigurationManager : IInternalConfigurationManager, ILocalConfiguration
    {
        private delegate (bool ShouldUpdate, ClusterMembersUpdate Update) ConfigurationUpdater(ClusterConfiguration existingConfiguration, SiloAddress input);
        private readonly MetadataStoreOptions _options;

        private readonly ConfigurationUpdater _addFunction;
        private readonly ConfigurationUpdater _removeFunction;
        private readonly AsyncEx.AsyncLock _updateLock = new();
        private readonly object _committedStateLock = new();
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly ILogger<ConfigurationManager> _log;
        private readonly IAcceptorRouter<ClusterConfiguration> _remoteManagerMediator;

        public ConfigurationManager(
            ILoggerFactory loggerFactory,
            IAcceptorRouter<ClusterConfiguration> remoteManagerMediator,
            IOptions<MetadataStoreOptions> options,
            ILocalSiloDetails localSiloDetails)
        {
            _localSiloDetails = localSiloDetails;
            _log = loggerFactory.CreateLogger<ConfigurationManager>();
            _options = options.Value;
            _addFunction = AddServer;
            _removeFunction = RemoveServer;

            Acceptor = new(this);
            Proposer = new(
                this,
                localSiloDetails.SiloAddress,
                log: loggerFactory.CreateLogger("MetadataStore.ConfigProposer"),
                remoteManagerMediator);
            _remoteManagerMediator = remoteManagerMediator;
        }

        public void OnCommittedConfiguration(ClusterConfiguration state)
        {
            lock (_committedStateLock)
            {
                if (CommittedConfiguration is null || CommittedConfiguration.Version < state.Version)
                {
                    CommittedConfiguration = state;
                }
            }
        }

        public ConfigProposer Proposer { get; }

        public ConfigAcceptor Acceptor { get; }

        /// <summary>
        /// Returns the most recently known-committed configuration.
        /// </summary>
        public ClusterConfiguration CommittedConfiguration { get; private set; }

        public void ForceLocalConfiguration(ClusterConfiguration configuration)
        {
            lock (_committedStateLock)
            {
                if (CommittedConfiguration is null || CommittedConfiguration.Version < configuration.Version)
                {
                    CommittedConfiguration = configuration;
                }
            }

            Acceptor.ForceState(configuration);
        }

        public Task<UpdateResult<ClusterConfiguration>> TryAddServer(SiloAddress address) => ModifyConfiguration(_addFunction, address);

        public Task<UpdateResult<ClusterConfiguration>> TryRemoveServer(SiloAddress address) => ModifyConfiguration(_removeFunction, address);

        public async Task<ReadResult<ClusterConfiguration>> TryRead(CancellationToken cancellationToken = default)
        {
            var (status, value) = await Proposer.TryRead(cancellationToken);
            return new ReadResult<ClusterConfiguration>(status == ReplicationStatus.Success, value);
        }

        private async Task<UpdateResult<ClusterConfiguration>> ModifyConfiguration(ConfigurationUpdater changeFunc, SiloAddress input)
        {
            // Update the configuration using two consensus rounds, first reading/committing the existing configuration,
            // then modifying it to add or remove a single server and committing the new value.
            // 
            // Note that performing the update using a single consensus round could break the invariant that configuration
            // grows or shrinks by at most one node at a time. For example, consider a scenario in which a commit was only
            // accepted on one acceptor in a set before the proposer faulted. In that case, the configuration may be seen
            // by the hypothetical single read-modify-write consensus round before the majority of acceptors are using
            // that configuration. The effect is that the majority may see a configuration change which changes by two
            // or more nodes simultaneously.
            var cancellationToken = CancellationToken.None;
            using (await _updateLock.LockAsync())
            {
                // Read the currently committed configuration, potentially committing a partially-committed configuration in the process.
                var (status, committedValue) = await Proposer.TryRead(cancellationToken);
                if (status != ReplicationStatus.Success)
                {
                    return new UpdateResult<ClusterConfiguration>(false, committedValue);
                }

                // Modify the replica set.
                var (shouldUpdate, update) = changeFunc(committedValue, input);
                if (!shouldUpdate)
                {
                    // The new address was already in the committed configuration, so no additional work needs to be done.
                    return new UpdateResult<ClusterConfiguration>(true, committedValue);
                }

                // Assemble the new configuration.
                var committedStamp = committedValue?.Stamp ?? default;
                Proposer.Ballot = Proposer.Ballot.AdvanceTo(committedStamp);
                var newStamp = Proposer.Ballot.Successor();

                var quorum = update.Members.Length / 2 + 1;
                var updatedConfig = new ClusterConfiguration(
                    stamp: newStamp,
                    version: (committedValue?.Version ?? MembershipVersion.Zero).Next(),
                    members: update.Members,
                    acceptQuorum: quorum,
                    prepareQuorum: quorum);

                // Attempt to commit the new configuration.
                (status, committedValue) = await Proposer.TryUpdate(updatedConfig, cancellationToken);
                var success = status == ReplicationStatus.Success;
                if (success)
                {
                    OnCommittedConfiguration(committedValue);

                    // Gossip the committed value to the servers which are included in it, but do not wait for gossiping to complete.
                    GossipSuccessfulCommit(committedValue).Ignore();
                }

                return new UpdateResult<ClusterConfiguration>(success, committedValue);
            }
        }

        private (bool ShouldUpdate, ClusterMembersUpdate Update) AddServer(ClusterConfiguration existingConfiguration, SiloAddress nodeToAdd)
        {
            var existingNodes = existingConfiguration?.Members;

            // Add the new node to the list of nodes, being sure not to add a duplicate.
            var newNodes = new SiloAddress[(existingNodes?.Length ?? 0) + 1];
            if (existingNodes != null)
            {
                for (var i = 0; i < existingNodes.Length; i++)
                {
                    // If the configuration already contains the specified node, return the already-confirmed configuration.
                    if (existingNodes[i].Equals(nodeToAdd))
                    {
                        return (false, new ClusterMembersUpdate(existingNodes));
                    }

                    newNodes[i] = existingNodes[i];
                }
            }

            // Add the new node at the end.
            newNodes[^1] = nodeToAdd;
            return (true, new ClusterMembersUpdate(newNodes));
        }

        private (bool ShouldUpdate, ClusterMembersUpdate Update) RemoveServer(ClusterConfiguration existingConfiguration, SiloAddress nodeToRemove)
        {
            var existingNodes = existingConfiguration?.Members;
            if (existingNodes == null || existingNodes.Length == 0)
            {
                return (false, new ClusterMembersUpdate(existingNodes));
            }

            // Remove the node from the list of nodes.
            var newNodes = new List<SiloAddress>(existingNodes);
            var removed = newNodes.Remove(nodeToRemove);

            // If no nodes changed, return a reference to the original configuration.
            if (!removed)
            {
                return (false, new ClusterMembersUpdate(existingNodes));
            }

            return (true, new ClusterMembersUpdate(newNodes.ToArray()));
        }

        private async Task GossipSuccessfulCommit(ClusterConfiguration value)
        {
            if (value.Members is not { Length: > 0})
            {
                return;
            }

            try
            {
                var tasks = new List<Task>(value.Members.Length);
                foreach (var server in value.Members)
                {
                    if (server.Equals(_localSiloDetails.SiloAddress))
                    {
                        continue;
                    }

                    var task = _remoteManagerMediator.Committed(server, value).AsTask();
                    tasks.Add(task);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.LogError(exception, "Error gossiping committed value to servers");
            }
        }
    }
}

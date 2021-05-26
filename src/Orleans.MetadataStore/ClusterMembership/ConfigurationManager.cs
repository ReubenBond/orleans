using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.MetadataStore.Storage;
using Orleans.Runtime;
using AsyncEx = Nito.AsyncEx;

namespace Orleans.MetadataStore
{
    [GenerateSerializer]
    public struct ClusterMembersUpdate
    {
        public ClusterMembersUpdate(SiloAddress[] members)
        {
            Members = members;
        }

        [Id(0)]
        public SiloAddress[] Members { get; }
    }

    public delegate (bool ShouldUpdate, ClusterMembersUpdate Update) ConfigurationUpdater<T>(ClusterMembers existingConfiguration, T input);

    /// <summary>
    /// The Configuration Manager is responsible for coordinating configuration (cluster membership) changes
    /// across the cluster.
    /// </summary>
    /// <remarks>
    /// From a high level, cluster configuration is stored in a special-purpose shared register. The linearizability
    /// properties of this register are used to ensure that safety requirements of the system are not violated.
    /// In particular, at no point in time and under no situation 
    /// </remarks>
    public class ConfigurationManager
    {
        private readonly IStoreReferenceFactory _referenceFactory;
        private readonly IServiceProvider _serviceProvider;
        private readonly MetadataStoreOptions _options;

        private readonly ChangeFunction<ClusterMembers, ClusterMembers> _updateFunction = (current, updated) => (current?.Version ?? 0) == updated.Version - 1 ? updated : current;
        private readonly ConfigurationUpdater<SiloAddress> _addFunction;
        private readonly ConfigurationUpdater<SiloAddress> _removeFunction;
        private readonly ChangeFunction<object, ClusterMembers> _readFunction = (current, updated) => current;
        private readonly AsyncEx.AsyncLock _updateLock = new AsyncEx.AsyncLock();
        private readonly ConfigProposer _proposer;
        private readonly Acceptor<ClusterMembers> _acceptor;
        private readonly ILogger<ConfigurationManager> _log;

        public ConfigurationManager(
            ILoggerFactory loggerFactory,
            IStoreReferenceFactory referenceFactory,
            IOptions<MetadataStoreOptions> options,
            IServiceProvider serviceProvider)
        {
            _referenceFactory = referenceFactory;
            _serviceProvider = serviceProvider;
            _log = loggerFactory.CreateLogger<ConfigurationManager>();
            _options = options.Value;
            _addFunction = AddServer;
            _removeFunction = RemoveServer;

            _acceptor = new ConfigAcceptor<ClusterMembers>(
                key: ClusterConfigurationKey,
                store: store,
                getParentBallot: _getAcceptorParentBallot,
                onUpdateState: OnUpdateConfiguration,
                log: loggerFactory.CreateLogger("MetadataStore.ConfigAcceptor")
            );

            // The config proposer always uses the configuration which it's proposing.
            _proposer = new ConfigProposer(
                getConfiguration: () => AcceptedConfiguration,
                log: loggerFactory.CreateLogger("MetadataStore.ConfigProposer")
            );
        }

        private ValueTask OnUpdateConfiguration(RegisterState<ClusterMembers> state)
        {
            //using (await this.updateLock.LockAsync())
            {
                AcceptedConfiguration = ExpandedClusterMembers.Create(state.Value, _options, _referenceFactory);
                //this.ProposedConfiguration = null;
            }
            return default;
        }

        /// <summary>
        /// Returns the most recently accepted configuration. Note that this configuration may not be committed.
        /// </summary>
        public ClusterMembers AcceptedConfiguration { get; private set; }

        /// <summary>
        /// Returns the most recently proposed configuration.
        /// </summary>
        public ClusterMembers ProposedConfiguration { get; private set; }

        /// <summary>
        /// Returns the active configuration.
        /// </summary>
        public ClusterMembers ActiveConfiguration => ProposedConfiguration ?? AcceptedConfiguration;

        public Task ForceLocalConfiguration(ClusterMembers configuration) => _acceptor.ForceState(configuration);

        public Task<UpdateResult<ClusterMembers>> TryAddServer(SiloAddress address) => ModifyConfiguration(_addFunction, address);

        public Task<UpdateResult<ClusterMembers>> TryRemoveServer(SiloAddress address) => ModifyConfiguration(_removeFunction, address);

        public Task<UpdateResult<ClusterMembers>> TryUpdate<T>(ConfigurationUpdater<T> func, T state) => ModifyConfiguration(func, state);

        public async Task<ReadResult<ClusterMembers>> TryRead(CancellationToken cancellationToken = default)
        {
            var result = await _proposer.TryUpdate(null, _readFunction, cancellationToken);
            return new ReadResult<ClusterMembers>(result.Status == ReplicationStatus.Success, result.Value);
        }

        private async Task<UpdateResult<ClusterMembers>> ModifyConfiguration<T>(ConfigurationUpdater<T> changeFunc, T input)
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
            var cancellation = CancellationToken.None;
            using (await _updateLock.LockAsync())
            {
                // Read the currently committed configuration, potentially committing a partially-committed configuration in the process.
                var (status, committedValue) = await _proposer.TryUpdate(null, _readFunction, cancellation);
                var committedConfig = committedValue;
                if (status != ReplicationStatus.Success)
                {
                    return new UpdateResult<ClusterMembers>(false, committedConfig);
                }

                // Modify the replica set.
                var (shouldUpdate, update) = changeFunc(committedConfig, input);
                if (!shouldUpdate)
                {
                    // The new address was already in the committed configuration, so no additional work needs to be done.
                    return new UpdateResult<ClusterMembers>(true, committedConfig);
                }

                // Assemble the new configuration.
                var committedStamp = committedConfig?.Stamp ?? default;
                _proposer.Ballot = _proposer.Ballot.AdvanceTo(committedStamp);
                var newStamp = _proposer.Ballot.Successor();

                var quorum = update.Members.Length / 2 + 1;
                var updatedConfig = new ClusterMembers(
                    stamp: newStamp,
                    version: (committedValue?.Version ?? 0) + 1,
                    members: update.Members,
                    acceptQuorum: quorum,
                    prepareQuorum: quorum);
                var success = false;

                try
                {
                    ProposedConfiguration = ExpandedClusterMembers.Create(updatedConfig, _options, _referenceFactory);

                    // Attempt to commit the new configuration.
                    (status, committedValue) = await _proposer.TryUpdate(updatedConfig, _updateFunction, cancellation);
                    success = status == ReplicationStatus.Success;

                    return new UpdateResult<ClusterMembers>(success, committedValue);
                }
                finally
                {
                    if (success)
                    {
                        AcceptedConfiguration = ProposedConfiguration;
                    }
                }
            }
        }

        private (bool ShouldUpdate, ClusterMembersUpdate Update) AddServer(ClusterMembers existingConfiguration, SiloAddress nodeToAdd)
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

        private (bool ShouldUpdate, ClusterMembersUpdate Update) RemoveServer(ClusterMembers existingConfiguration, SiloAddress nodeToRemove)
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
    }
}

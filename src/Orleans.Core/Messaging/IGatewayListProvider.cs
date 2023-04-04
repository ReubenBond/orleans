using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Utilities;

namespace Orleans.Messaging
{
    /// <summary>
    /// Interface that provides Orleans gateways information.
    /// </summary>
    public interface IGatewayListProvider
    {
        /// <summary>
        /// Initializes the provider, will be called before all other methods.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task InitializeGatewayListProvider();

        /// <summary>
        /// Returns the list of gateways (silos) that can be used by a client to connect to Orleans cluster.
        /// The Uri is in the form of: "gwy.tcp://IP:port/Generation". See Utils.ToGatewayUri and Utils.ToSiloAddress for more details about Uri format.
        /// </summary>
        /// <returns>The list of gateway endpoints.</returns>
        Task<IList<Uri>> GetGateways();

        /// <summary>
        /// Gets the period of time between refreshes.
        /// </summary>
        TimeSpan MaxStaleness { get; }

        /// <summary>
        /// Gets a value indicating whether this IGatewayListProvider ever refreshes its returned information, or always returns the same gateway list.
        /// </summary>
        [Obsolete("This attribute is no longer used and all providers are considered updatable")]
        bool IsUpdatable { get; }
    }

    public sealed class GatewayMembershipService : IGatewayMembershipService, ILifecycleParticipant<IClusterClientLifecycle>
    {
        private readonly AsyncEnumerable<GatewayMembershipSnapshot> _updates;
        private readonly IMembershipTable _membershipTable;
        private readonly PeriodicTimer _timer;
        private readonly ILogger _logger;
        private readonly IOptions<GatewayOptions> _gatewayOptions;
        private Task _updateTask;
        private GatewayMembershipSnapshot _snapshot;

        public GatewayMembershipService(
            IOptions<GatewayOptions> gatewayOptions,
            IMembershipTable membershipTable,
            ILogger<GatewayMembershipService> logger)
        {
            _logger = logger;
            _gatewayOptions = gatewayOptions;
            _membershipTable = membershipTable;
            _timer = new PeriodicTimer(_gatewayOptions.Value.GatewayListRefreshPeriod);
            _snapshot = new GatewayMembershipSnapshot(ImmutableArray<GatewayMember>.Empty, MembershipVersion.MinValue);
            _updates = new AsyncEnumerable<GatewayMembershipSnapshot>(
                (previous, proposed) => proposed.Version == MembershipVersion.MinValue || proposed.Version > previous.Version,
                _snapshot,
                update => Interlocked.Exchange(ref _snapshot, update));
        }

        /// <inheritdoc/>
        public GatewayMembershipSnapshot CurrentSnapshot => _snapshot;

        /// <inheritdoc/>
        public IAsyncEnumerable<GatewayMembershipSnapshot> GetMembershipUpdatesAsync(CancellationToken cancellationToken = default) => _updates;

        /// <inheritdoc/>
        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
        {
            if (minimumVersion != default && minimumVersion != MembershipVersion.MinValue && _snapshot.Version >= minimumVersion)
            {
                return default;
            }

            return RefreshAsync(minimumVersion);

            async ValueTask RefreshAsync(MembershipVersion minimumVersion)
            {
                var didRefresh = false;
                do
                {
                    if (!didRefresh || _snapshot.Version < minimumVersion)
                    {
                        await RefreshInternal();
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(10));
                } while (_snapshot.Version < minimumVersion);
            }
        }

        private async Task PollForUpdates()
        {
            await Task.Yield();
            do
            {
                try
                {
                    await RefreshInternal();
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error refreshing gateways");
                }
            } while (await _timer.WaitForNextTickAsync());
        }

        private async Task RefreshInternal()
        {
            var table = await _membershipTable.ReadAll();
            if (table.Version.Version > _snapshot.Version.Value)
            {
                var members = new List<GatewayMember>();
                foreach (var member in table.Members)
                {
                    members.Add(new GatewayMember(member.Item1.SiloAddress, member.Item1.Endpoints.ToImmutableArray()));
                }

                var newSnapshot = new GatewayMembershipSnapshot(members.ToImmutableArray(), new MembershipVersion(table.Version.Version));
                _updates.TryPublish(newSnapshot);
            }
        }

        void ILifecycleParticipant<IClusterClientLifecycle>.Participate(IClusterClientLifecycle observer)
        {
            async Task OnRuntimeInitializeStart(CancellationToken startCancellation)
            {
                await RefreshInternal();

                StartPollingForUpdates();
                void StartPollingForUpdates()
                {
                    using var _ = new ExecutionContextSuppressor();
                    _updateTask = Task.Run(PollForUpdates);
                }
            }

            async Task OnRuntimeInitializeStop(CancellationToken ct)
            {
                _timer.Dispose();
                if (_updateTask is { } task)
                {
                    await Task.WhenAny(ct.WhenCancelled(), task);
                }
            }

            observer.Subscribe(
                nameof(GatewayMembershipService),
                ServiceLifecycleStage.RuntimeInitialize,
                OnRuntimeInitializeStart,
                OnRuntimeInitializeStop);
        }
    }

    /// <summary>
    /// Functionality for querying gateway membership.
    /// </summary>
    public interface IGatewayMembershipService
    {
        /// <summary>
        /// Gets the current membership snapshot.
        /// </summary>
        GatewayMembershipSnapshot CurrentSnapshot { get; }

        /// <summary>
        /// Gets a stream of membership snapshot updates.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A stream of updates to gateway membership.</returns>
        IAsyncEnumerable<GatewayMembershipSnapshot> GetMembershipUpdatesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Refreshes membership if it is not at or above the specified minimum version.
        /// </summary>
        /// <param name="minimumVersion">The minimum version.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
        ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Describes the gateways in a deployment.
    /// </summary>
    [GenerateSerializer, Immutable]
    public sealed class GatewayMembershipSnapshot
    {
        public GatewayMembershipSnapshot(ImmutableArray<GatewayMember> entries, MembershipVersion version)
        {
            Gateways = entries;
            Version = version;
        }

        /// <summary>
        /// Gets the gateways.
        /// </summary>
        [Id(0)]
        public ImmutableArray<GatewayMember> Gateways { get; }

        /// <summary>
        /// Gets the version of the gateway table.
        /// </summary>
        [Id(1)]
        public MembershipVersion Version { get; }
    }

    /// <summary>
    /// Describes a gateway.
    /// </summary>
    [GenerateSerializer, Immutable]
    public sealed class GatewayMember
    {
        public GatewayMember(SiloAddress siloAddress, ImmutableArray<EndpointInfo> endpoints)
        {
            SiloAddress = siloAddress;
            Endpoints = endpoints;
        }

        /// <summary>
        /// Gets the identity of this gateway.
        /// </summary>
        [Id(0)]
        public SiloAddress SiloAddress { get; }

        /// <summary>
        /// Gets the endpoint information for this gateway.
        /// </summary>
        [Id(1)]
        public ImmutableArray<EndpointInfo> Endpoints { get; }
    }   
}

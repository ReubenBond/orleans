using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Utilities;

namespace Orleans.Messaging
{
    internal sealed class GatewayMembershipService : IGatewayMembershipService, ILifecycleParticipant<IClusterClientLifecycle>, IDisposable
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
            while (await _timer.WaitForNextTickAsync())
            {
                try
                {
                    await RefreshInternal();
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error refreshing gateways");
                }
            }
        }

        private async Task RefreshInternal()
        {
            var table = await _membershipTable.ReadAll();
            if (table.Version.Version > _snapshot.Version.Value)
            {
                var members = new List<GatewayMember>();
                foreach (var member in table.Members)
                {
                    // Ignore gateways which aren't currently active.
                    if (member.Item1.Status is not SiloStatus.Active)
                    {
                        continue;
                    }

                    members.Add(new GatewayMember(member.Item1.SiloAddress, member.Item1.Endpoints.ToImmutableArray()));
                }

                var newSnapshot = new GatewayMembershipSnapshot(members, new MembershipVersion(table.Version.Version));
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

        public void Dispose()
        {
            _timer.Dispose();
            _updates.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.Messaging
{
    /// <summary>
    /// Implementation of <see cref="IGatewayMembershipProvider"/> which relies on an <see cref="IMembershipTable"/> to provide gateway membership.
    /// </summary>
    public sealed class GatewayMembershipProvider : IGatewayMembershipProvider
    {
        private readonly object _lock = new();
        private readonly ILogger<GatewayMembershipProvider> _logger;
        private readonly IMembershipTable _membershipTable;
        private Task _initTask;
        private GatewayMembershipSnapshot _snapshot = new (ImmutableArray<GatewayMember>.Empty, MembershipVersion.MinValue);

        public GatewayMembershipProvider(ILogger<GatewayMembershipProvider> logger, IMembershipTable membershipTable)
        {
            _logger = logger;
            _membershipTable = membershipTable;
        }

        public async ValueTask<GatewayMembershipSnapshot> GetGatewaysAsync(CancellationToken cancellationToken)
        {
            // Ensure that the membership table has been initialized.
            var didRefresh = await EnsureInitialized();

            if (!didRefresh)
            {
                await RefreshTableInternal();
            }

            return _snapshot;
        }

        private async ValueTask<bool> EnsureInitialized()
        {
            var didRefresh = false;
            if (_snapshot.Version == MembershipVersion.MinValue)
            {
                Task initTask;
                lock (_lock)
                {
                    if (_initTask is null)
                    {
                        _initTask = InitializeMembershipTable();
                        didRefresh = true;
                    }

                    initTask = _initTask;
                }

                await initTask;
            }

            return didRefresh;
        }

        private async Task InitializeMembershipTable()
        {
            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Initializing gateway membership provider");
                }

                await _membershipTable.InitializeMembershipTable(tryInitTableVersion: true);
                await RefreshTableInternal();

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Initialized gateway membership provider");
                }

                // Prevent future callers from re-running the initialization logic.
                _initTask = Task.CompletedTask;
            }
            catch (Exception exception)
            {
                // To ensure a retry happens, we need to set the init task to null.
                _initTask = null;

                _logger.LogError(exception, "Error initializing gateway membership provider");
            }
        }

        private async ValueTask RefreshTableInternal()
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Refreshing gateway membership table");
            }

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

                    // Ignore hosts which do not specify a gateway port.
                    if (member.Item1.ProxyPort == 0)
                    {
                        continue;
                    }

                    // Ignore hosts which do not have a gateway endpoint.
                    if (!member.Item1.Endpoints.Any(static ep => string.Equals(ep.Name, ClientOutboundConnectionFactory.DefaultConnectorName, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    members.Add(new GatewayMember(member.Item1.SiloAddress, member.Item1.Endpoints.ToImmutableArray()));
                }

                var newSnapshot = new GatewayMembershipSnapshot(members, new MembershipVersion(table.Version.Version));
                _snapshot = newSnapshot;
            }
            
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Refreshed gateway membership table");
            }
        }
    }
}

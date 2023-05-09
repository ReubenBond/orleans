using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    /// <summary>
    /// <see cref="IGatewayListProvider"/> implementation which returns a static list, configured via <see cref="StaticGatewayMembershipProviderOptions"/>.
    /// </summary>
    public class StaticGatewayMembershipProvider : IGatewayMembershipProvider, IDisposable
    {
        private readonly GatewayMembershipSnapshot _snapshot;
        private readonly IDisposable _optionsMonitor;

        /// <summary>
        /// Initializes a new instance of the <see cref="StaticGatewayMembershipProvider"/> class.
        /// </summary>
        /// <param name="options">The specific options.</param>
        public StaticGatewayMembershipProvider(IOptionsMonitor<StaticGatewayMembershipProviderOptions> options)
        {
            _snapshot = new GatewayMembershipSnapshot(options.CurrentValue.Gateways, default(MembershipVersion).Successor());
            _optionsMonitor = options.OnChange((options, name) =>
            {
                if (!string.Equals(name, Options.DefaultName, StringComparison.Ordinal))
                {
                    return;
                }

                var newVersion = _snapshot.Version.Successor();
            });
        }

        /// <inheritdoc />
        public ValueTask<GatewayMembershipSnapshot> GetGatewaysAsync(CancellationToken cancellationToken) => new(_snapshot);

        /// <inheritdoc />
        public void Dispose() => _optionsMonitor.Dispose();
    }
}

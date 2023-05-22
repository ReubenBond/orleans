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
    public class StaticGatewayMembershipProvider : IGatewayMembershipProvider
    {
        private readonly IOptionsMonitor<StaticGatewayMembershipProviderOptions> _options;
        private MembershipVersion _version;

        /// <summary>
        /// Initializes a new instance of the <see cref="StaticGatewayMembershipProvider"/> class.
        /// </summary>
        /// <param name="options">The specific options.</param>
        public StaticGatewayMembershipProvider(IOptionsMonitor<StaticGatewayMembershipProviderOptions> options)
        {
            _options = options;
        }

        /// <inheritdoc />
        public ValueTask<GatewayMembershipSnapshot> GetGatewaysAsync(CancellationToken cancellationToken) => new(GetSnapshot());

        private GatewayMembershipSnapshot GetSnapshot()
        {
            var version = _version = _version.Successor();
            return new GatewayMembershipSnapshot(_options.CurrentValue.Gateways, version);
        }
    }
}

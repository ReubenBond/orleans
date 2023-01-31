using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Messaging
{
    /// <summary>
    /// <see cref="IGatewayListProvider"/> implementation which returns a static list, configured via <see cref="StaticGatewayMembershipProviderOptions"/>.
    /// </summary>
    public sealed class StaticGatewayMembershipProvider : IGatewayMembershipProvider, IDisposable
    {
        private readonly IOptionsMonitor<StaticGatewayMembershipProviderOptions> _options;
        private readonly IDisposable _optionsChangeHandler;
        private GatewayMembershipSnapshot _snapshot;

        /// <summary>
        /// Initializes a new instance of the <see cref="StaticGatewayMembershipProvider"/> class.
        /// </summary>
        /// <param name="options">The specific options.</param>
        public StaticGatewayMembershipProvider(IOptionsMonitor<StaticGatewayMembershipProviderOptions> options)
        {
            _options = options;
            _optionsChangeHandler = _options.OnChange((options, name) => _snapshot = CreateSnapshot(options));
            _snapshot = CreateSnapshot(_options.CurrentValue);

            GatewayMembershipSnapshot CreateSnapshot(StaticGatewayMembershipProviderOptions options)
            {
                var version = _snapshot switch { null => default, { } snapshot => snapshot.Version };
                return new GatewayMembershipSnapshot(options.Gateways, version.Successor());
            }
        }

        /// <inheritdoc />
        public ValueTask<GatewayMembershipSnapshot> GetGatewaysAsync(CancellationToken cancellationToken) => new(_snapshot);

        /// <inheritdoc />
        public void Dispose() => _optionsChangeHandler.Dispose();
    }
}

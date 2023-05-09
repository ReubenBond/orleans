using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    /// <summary>
    /// Interface that provides Orleans gateways information.
    /// </summary>
    [Obsolete("Use IGatewayMembershipService instead")]
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

#pragma warning disable CS0618 // Type or member is obsolete
    internal sealed class GatewayMembershipServiceGatewayListProvider : IGatewayListProvider
#pragma warning restore CS0618 // Type or member is obsolete
    {
        private readonly GatewayOptions _gatewayOptions;
        private readonly IGatewayMembershipService _gatewayMembershipService;
        public GatewayMembershipServiceGatewayListProvider(
            IOptions<GatewayOptions> gatewayOptions,
            IGatewayMembershipService gatewayMembershipService)
        {
            _gatewayOptions = gatewayOptions.Value;
            _gatewayMembershipService = gatewayMembershipService;
        }

        public TimeSpan MaxStaleness => _gatewayOptions.GatewayListRefreshPeriod;
        public bool IsUpdatable => true;

        public Task<IList<Uri>> GetGateways() => Task.FromResult<IList<Uri>>(_gatewayMembershipService.CurrentSnapshot.Gateways.Keys.Select(static key => key.ToGatewayUri()).ToList());
        public Task InitializeGatewayListProvider() => _gatewayMembershipService.Refresh().AsTask();
    }
}

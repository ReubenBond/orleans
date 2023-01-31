using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class ZooKeeperHostingExtensions
    {
        /// <summary>
        /// Configures the silo to use ZooKeeper for cluster membership.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>.
        /// </returns>
        public static ISiloBuilder UseZooKeeperClustering(
            this ISiloBuilder builder,
            Action<ZooKeeperClusteringOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
                });
        }

        /// <summary>
        /// Configures the silo to use ZooKeeper for cluster membership.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>.
        /// </returns>
        public static ISiloBuilder UseZooKeeperClustering(
            this ISiloBuilder builder,
            Action<OptionsBuilder<ZooKeeperClusteringOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<ZooKeeperClusteringOptions>());
                    services.AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
                });
        }

        /// <summary>
        /// Configure the client to use ZooKeeper for clustering.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="IClientBuilder"/>.
        /// </returns>
        public static IClientBuilder UseZooKeeperClustering(
            this IClientBuilder builder,
            Action<ZooKeeperClusteringOptions> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.AddSingleton<IGatewayMembershipProvider, GatewayMembershipProvider>();
                    services.AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
                });
        }

        /// <summary>
        /// Configure the client to use ZooKeeper for clustering.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="IClientBuilder"/>.
        /// </returns>
        public static IClientBuilder UseZooKeeperClustering(
            this IClientBuilder builder,
            Action<OptionsBuilder<ZooKeeperClusteringOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<ZooKeeperClusteringOptions>());
                    services.AddSingleton<IGatewayMembershipProvider, GatewayMembershipProvider>();
                    services.AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
                });
        }
    }
}

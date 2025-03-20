// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Orleans;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Clustering.Redis;
using StackExchange.Redis;

namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// Hosting extensions for the Redis clustering provider.
    /// </summary>
    public static class RedisClusteringISiloBuilderExtensions
    {
        /// <summary>
        /// Configures Redis as the clustering provider.
        /// </summary>
        public static ISiloBuilder UseRedisClustering(this ISiloBuilder builder, Action<RedisClusteringOptions> configuration)
        {
            return builder.ConfigureServices(services =>
            {
                if (configuration != null)
                {
                    services.Configure(configuration);
                }

                services.AddRedisClustering();
            });
        }

        /// <summary>
        /// Configures Redis as the clustering provider.
        /// </summary>
        public static ISiloBuilder UseRedisClustering(this ISiloBuilder builder, string redisConnectionString)
        {
            return builder.ConfigureServices(services => services
                .Configure<RedisClusteringOptions>(options =>
                {
                    options.ConfigurationOptions = ConfigurationOptions.Parse(redisConnectionString);
                })
                .AddRedisClustering());
        }

        internal static IServiceCollection AddRedisClustering(this IServiceCollection services)
        {
            services.AddSingleton<RedisMembershipTable>();
            services.AddSingleton<IConfigurationValidator, RedisClusteringOptionsValidator>();
            services.AddSingleton<IMembershipTable>(sp => sp.GetRequiredService<RedisMembershipTable>());
            return services;
        }
    }
}

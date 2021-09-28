using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans.Hosting
{
    /// <summary>
    /// Internal wrapper of <see cref="IHostBuilder"/> that scopes all configuration extensions related to Orleans servers.
    /// </summary>
    internal class SiloBuilder : ISiloBuilder
    {
        private readonly IHostBuilder hostBuilder;

        /// <inheritdoc />
        public IDictionary<object, object> Properties => this.hostBuilder.Properties;

        public SiloBuilder(IHostBuilder hostBuilder)
        {
            this.hostBuilder = hostBuilder;
            this.ConfigureApplicationParts(parts => parts.ConfigureDefaults());
            this.ConfigureDefaults();
            hostBuilder.ConfigureServices((ctx, services) => services.AddHostedService<SiloHostedService>());
        }

        /// <inheritdoc />
        public ISiloBuilder ConfigureServices(Action<Microsoft.Extensions.Hosting.HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.hostBuilder.ConfigureServices(configureDelegate);
            return this;
        }
    }
}
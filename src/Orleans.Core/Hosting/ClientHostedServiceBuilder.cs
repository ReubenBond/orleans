using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans.Hosting
{
    /// <summary>
    /// Internal wrapper type of <see cref="IHostBuilder"/> that scope all configuration extensions related to orleans.
    /// </summary>
    internal class ClientHostedServiceBuilder : IClientBuilder
    {
        private readonly IHostBuilder hostBuilder;
        private readonly List<Action<HostBuilderContext, IClientBuilder>> configureSiloDelegates = new List<Action<HostBuilderContext, IClientBuilder>>();
        private readonly List<Action<HostBuilderContext, IServiceCollection>> configureServicesDelegates = new List<Action<HostBuilderContext, IServiceCollection>>();

        /// <inheritdoc />
        public IDictionary<object, object> Properties => this.hostBuilder.Properties;

        public ClientHostedServiceBuilder(IHostBuilder hostBuilder)
        {
            this.hostBuilder = hostBuilder;
        }

        public void Build(HostBuilderContext context, IServiceCollection serviceCollection)
        {
            foreach (var configurationDelegate in this.configureSiloDelegates)
            {
                configurationDelegate(context, this);
            }

            serviceCollection.AddHostedService<ClientHostedService>();
            this.ConfigureDefaults();
            this.ConfigureApplicationParts(parts => parts.ConfigureDefaults());

            foreach (var configurationDelegate in this.configureServicesDelegates)
            {
                configurationDelegate(context, serviceCollection);
            }
        }

        public IClientBuilder ConfigureSilo(Action<HostBuilderContext, IClientBuilder> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.configureSiloDelegates.Add(configureDelegate);
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.configureServicesDelegates.Add(configureDelegate);
            return this;
        }
    }
}

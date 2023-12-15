using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Messaging;

namespace Orleans.Hosting
{
    /// <summary>
    /// Builder for configuring an Orleans server.
    /// </summary>
    internal class SiloBuilder : ISiloBuilder
    {
        public SiloBuilder(IServiceCollection services)
        {
            Services = services;
            Transports = new SiloTransportCollection(services);
            DefaultSiloServices.AddDefaultServices(this);
        }

        /// <inheritdoc/>
        public IServiceCollection Services { get; }

        /// <inheritdoc/>
        public ISiloTransportCollection Transports { get; }
    }
}
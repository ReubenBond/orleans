using Microsoft.Extensions.DependencyInjection;
using Orleans.Connections.Transport;

namespace Orleans.Hosting
{
    /// <summary>
    /// Builder for configuring an Orleans client.
    /// </summary>
    public class ClientBuilder : IClientBuilder
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClientBuilder"/> class.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        public ClientBuilder(IServiceCollection services)
        {
            Services = services;
            Transports = new ClientTransportCollection(services);
            this.AddDefaultServices();
        }

        /// <inheritdoc/>
        public IServiceCollection Services { get; }

        /// <inheritdoc/>
        public IClientTransportCollection Transports { get; }
    }
}
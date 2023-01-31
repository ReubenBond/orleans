using Microsoft.Extensions.DependencyInjection;
using Orleans.Connections.Transport;

namespace Orleans.Hosting
{
    /// <summary>
    /// Builder for configuring an Orleans client.
    /// </summary>
    public interface IClientBuilder
    {
        /// <summary>
        /// Gets the services collection.
        /// </summary>
        IServiceCollection Services { get; }

        /// <summary>
        /// Gets the transport collection.
        /// </summary>
        IClientTransportCollection Transports { get; }
    }
}
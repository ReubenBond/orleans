using Microsoft.Extensions.DependencyInjection;

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
            DefaultSiloServices.AddDefaultServices(this);
        }

        /// <inheritdoc/>
        public IServiceCollection Services { get; }
    }
}
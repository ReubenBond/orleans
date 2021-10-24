using Orleans.Runtime;

namespace Orleans
{
    public static class SystemServiceGrainFactoryExtensions
    {
        /// <summary>
        /// Gets a system service implementing the specified interface, with the specified host address.
        /// </summary>
        public static TService GetService<TService>(this IGrainFactory grainFactory, SiloAddress hostAddress) where TService : ISystemService
        {
            var key = IdSpan.Create(hostAddress.ToParsableString());
            return grainFactory.GetGrain<TService>(key);
        }
    }
}
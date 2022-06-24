using Orleans.Runtime;

namespace Orleans
{
    public static class SystemServiceGrainFactoryExtensions
    {
        public static TService GetService<TService>(this IGrainFactory grainFactory, SiloAddress hostAddress) where TService : ISystemService
        {
            var key = IdSpan.Create(hostAddress.ToParsableString());
            return grainFactory.GetGrain<TService>(key);
        }
    }
}
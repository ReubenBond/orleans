using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// <see cref="IGrainLocatorResolver"/> for resolving client grain locations.
    /// </summary>
    internal class ClientGrainLocatorResolver : IGrainLocatorResolver
    {
        private readonly IServiceProvider _services;
        private ClientGrainLocator _grainLocator;

        public ClientGrainLocatorResolver(IServiceProvider services) => _services = services;
        public bool TryResolveGrainLocator(GrainType grainType, out IGrainLocator result)
        {
            if (grainType.IsClient())
            {
                result = _grainLocator ??= _services.GetRequiredService<ClientGrainLocator>();
                return true;
            }

            result = null;
            return false;
        }
    }
}

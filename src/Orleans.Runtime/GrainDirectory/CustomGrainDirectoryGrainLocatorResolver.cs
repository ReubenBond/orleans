using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// <see cref="IGrainLocatorResolver"/> for grains with a custom grain directory implementation.
    /// </summary>
    internal class CustomGrainDirectoryGrainLocatorResolver : IGrainLocatorResolver
    {
        private readonly GrainDirectoryResolver _grainDirectoryResolver;
        private readonly CachedGrainLocator _cachedGrainLocator;

        public CustomGrainDirectoryGrainLocatorResolver(
            GrainDirectoryResolver grainDirectoryResolver,
            CachedGrainLocator cachedGrainLocator)
        {
            _grainDirectoryResolver = grainDirectoryResolver;
            _cachedGrainLocator = cachedGrainLocator;
        }

        public bool TryResolveGrainLocator(GrainType grainType, out IGrainLocator result)
        {
            if (_grainDirectoryResolver.HasNonDefaultDirectory(grainType))
            {
                result = _cachedGrainLocator;
                return true;
            }

            result = null;
            return false;
        }
    }
}

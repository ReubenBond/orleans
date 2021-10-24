using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    internal class GrainLocatorResolver
    {
        private readonly ConcurrentDictionary<GrainType, IGrainLocator> _resolvedLocators = new(GrainType.Comparer.Instance);
        private readonly IGrainLocatorResolver[] _grainLocatorResolvers;
        private readonly Func<GrainType, IGrainLocator> _getLocatorInternal;
        private readonly DhtGrainLocator _dhtGrainLocator;

        public GrainLocatorResolver(
            DhtGrainLocator dhtGrainLocator,
            IEnumerable<IGrainLocatorResolver> grainLocatorResolvers)
        {
            _grainLocatorResolvers = grainLocatorResolvers.ToArray();
            _getLocatorInternal = GetGrainLocatorInternal;
            _dhtGrainLocator = dhtGrainLocator;
        }

        public IGrainLocator GetGrainLocator(GrainType grainType) => _resolvedLocators.GetOrAdd(grainType, _getLocatorInternal);

        private IGrainLocator GetGrainLocatorInternal(GrainType grainType)
        {
            IGrainLocator result = null;
            foreach (var resolver in _grainLocatorResolvers)
            {
                if (resolver.TryResolveGrainLocator(grainType, out result))
                {
                    break;
                }
            }

            if (result is null)
            {
                result = _dhtGrainLocator;
            }

            return result;
        }
    }
}

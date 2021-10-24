using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Metadata;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Implementation of <see cref="IGrainLocator"/> that parses a <see cref="SiloAddress"/> from the grain key.
    /// </summary>
    internal class SystemServiceGrainLocator : IGrainLocator
    {
        private readonly ClusterMembershipService _clusterMembershipService;
        private readonly LRU<GrainId, GrainAddress> _cache = new(128_000, TimeSpan.FromHours(1));

        public SystemServiceGrainLocator(ClusterMembershipService clusterMembershipService) => _clusterMembershipService = clusterMembershipService;

        public ValueTask<GrainAddress> Lookup(GrainId grainId) => new(GetAddress(grainId));

        public Task<GrainAddress> Register(GrainAddress address) => throw new InvalidOperationException($"Cannot register system service explicitly");

        public Task Unregister(GrainAddress address, UnregistrationCause cause) => throw new InvalidOperationException($"Cannot unregister system service explicitly");

        public void CachePlacementDecision(GrainAddress address) => _cache.Add(address.GrainId, address);

        public void InvalidateCache(GrainId grainId) => _cache.RemoveKey(grainId);

        public void InvalidateCache(GrainAddress address) => InvalidateCache(address.GrainId);

        public bool TryLookupInCache(GrainId grainId, out GrainAddress address)
        {
            address = GetAddress(grainId);
            return true;
        }

        public GrainAddress GetAddress(in GrainId grainId)
        {
            if (!_cache.TryGetValue(grainId, out var address))
            {
                address = new GrainAddress()
                {
                    SiloAddress = GetSiloAddress(grainId),
                    GrainId = grainId,
                    ActivationId = ActivationId.GetDeterministic(grainId),
                    MembershipVersion = _clusterMembershipService.CurrentSnapshot.Version,
                };

                _cache.Add(grainId, address);
            }

            return address;
        }

        public static SiloAddress GetSiloAddress(in GrainId grainId)
        {
            if (!SiloAddress.TryParse(grainId.Key.Value.Span, out var siloAddress))
            {
                ThrowNotSystemServiceGrainId(grainId);
                return null;
            }

            return siloAddress;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNotSystemServiceGrainId(GrainId grainId) => throw new InvalidOperationException($"{grainId} is not a valid system service id");
    }

    /// <summary>
    /// <see cref="IGrainLocatorResolver"/> for grains with a custom grain directory implementation.
    /// </summary>
    internal class SystemServiceGrainLocatorResolver : IGrainLocatorResolver
    {
        private readonly SystemServiceGrainLocator _grainLocator;
        private readonly GrainPropertiesResolver _grainPropertiesResolver;

        public SystemServiceGrainLocatorResolver(
            SystemServiceGrainLocator grainLocator,
            GrainPropertiesResolver grainPropertiesResolver)
        {
            _grainLocator = grainLocator;
            _grainPropertiesResolver = grainPropertiesResolver;
        }

        public bool TryResolveGrainLocator(GrainType grainType, out IGrainLocator result)
        {
            var grainProperties = _grainPropertiesResolver.GetGrainProperties(grainType);
            if (grainProperties.Properties.TryGetValue(SystemServicePlacement.SystemServiceGrainPropertyName, out var value)
                && string.Equals(value, SystemServicePlacement.SystemServiceGrainPropertyValue, StringComparison.OrdinalIgnoreCase))
            {
                result = _grainLocator;
                return true;
            }

            result = null;
            return false;
        }
    }
}

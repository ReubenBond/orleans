using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory
{
    internal interface ILocalGrainDirectory : IDhtGrainDirectory
    {
        /// <summary>
        /// Starts the local portion of the directory service.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops the local portion of the directory service.
        /// </summary>
        Task StopAsync();

        RemoteGrainDirectory RemoteGrainDirectory { get; }
        RemoteGrainDirectory CacheValidator { get; }

        /// <summary>
        /// Fetches locally known directory information for a grain.
        /// If there is no local information, either in the cache or in this node's directory partition,
        /// then this method will return false and leave the list empty.
        /// </summary>
        /// <param name="grain">The ID of the grain to look up.</param>
        /// <param name="addresses">An output parameter that receives the list of locally-known activations of the grain.</param>
        /// <returns>True if remote addresses are complete within freshness constraint</returns>
        bool LocalLookup(GrainId grain, out AddressAndTag addresses);

        /// <summary>
        /// Invalidates cache entry for the given activation address.
        /// This method is intended to be called whenever a directory client tries to access 
        /// an activation returned from the previous directory lookup and gets a reject from the target silo 
        /// notifying him that the activation does not exist.
        /// </summary>
        /// <param name="activation">The address of the activation that needs to be invalidated in the directory cache for the given grain.</param>
        void InvalidateCacheEntry(GrainAddress activation);

        /// <summary>
        /// Invalidates cache entry for the given grain.
        /// </summary>
        void InvalidateCacheEntry(GrainId grainId);

        /// <summary>
        /// Adds or updates a cache entry for the given activation address.
        /// This method is intended to be called whenever a placement decision is made.
        /// </summary>
        void AddOrUpdateCacheEntry(GrainId grainId, SiloAddress siloAddress);

        /// <summary>
        /// For testing purposes only.
        /// Returns the silo that this silo thinks is the primary owner of directory information for
        /// the provided grain ID.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        SiloAddress? GetPrimaryForGrain(GrainId grain);

        /// <summary>
        /// Returns the directory information held in a local directory partition for the provided grain ID.
        /// The result will be null if no information is held.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        AddressAndTag GetLocalDirectoryData(GrainId grain);

        /// <summary>
        /// For testing and troubleshooting purposes only.
        /// Returns the directory information held in a local directory cache for the provided grain ID.
        /// The result will be null if no information is held.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        GrainAddress? GetLocalCacheData(GrainId grain);

        /// <summary>
        /// Attempts to find the specified grain in the directory cache.
        /// </summary>
        bool TryCachedLookup(GrainId grainId, [NotNullWhen(true)] out GrainAddress? address);

        /// <summary>
        /// Sets the callback to <see cref="Catalog"/> which is called when a silo is removed from membership.
        /// </summary>
        /// <param name="catalogOnSiloRemoved">The callback.</param>
        void SetSiloRemovedCatalogCallback(Action<SiloAddress, SiloStatus> catalogOnSiloRemoved);
    }
}

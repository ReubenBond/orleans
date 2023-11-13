using System.Threading.Tasks;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    /// <summary>
    /// Internal interface for exposing the cluster manifest.
    /// </summary>
    [Alias("Orleans.Runtime.IClusterManifestSystemTarget")]
    internal interface IClusterManifestSystemTarget : ISystemTarget
    {
        /// <summary>
        /// Gets the current cluster manifest.
        /// </summary>
        /// <returns>The current cluster manifest.</returns>
        [Alias("GetClusterManifest")]
        ValueTask<ClusterManifest> GetClusterManifest();
    }
}

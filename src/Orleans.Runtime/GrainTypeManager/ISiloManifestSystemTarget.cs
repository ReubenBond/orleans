using System.Threading.Tasks;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    [Alias("Orleans.Runtime.ISiloManifestSystemTarget")]
    internal interface ISiloManifestSystemTarget : ISystemTarget
    {
        [Alias("GetSiloManifest")]
        ValueTask<GrainManifest> GetSiloManifest();
    }
}
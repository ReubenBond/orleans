using System.Collections.Immutable;
using System.Threading.Tasks;

namespace Orleans.Runtime.GrainDirectory
{
    [Alias("Orleans.Runtime.GrainDirectory.IRemoteClientDirectory")]
    internal interface IRemoteClientDirectory : ISystemTarget
    {
        [Alias("OnUpdateClientRoutes")]
        Task OnUpdateClientRoutes(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> update);
        [Alias("GetClientRoutes")]
        Task<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>> GetClientRoutes(ImmutableDictionary<SiloAddress, long> knownRoutes);
    }
}

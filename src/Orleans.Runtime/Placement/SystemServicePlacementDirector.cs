using System.Threading.Tasks;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime.Placement;

internal class SystemServicePlacementDirector : IPlacementDirector
{
    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        => Task.FromResult(SystemServiceGrainLocator.GetSiloAddress(target.GrainIdentity));
}
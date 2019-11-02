using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    [Serializable]
    public sealed class SystemTargetPlacementDirector : IPlacementDirector, IActivationSelector
    {
        public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            throw new NotSupportedException("System targets have deterministic placement and are fixed lifetimes");
        }

        public Task<PlacementResult> OnSelectActivation(PlacementStrategy strategy, GrainId target, IPlacementRuntime context)
        {
            var siloAddress = SystemTarget.GetSiloAddress(target);
            return PlacementResult.IdentifySelection(ActivationAddress.GetAddress(siloAddress, target, ))
        }

        public bool TrySelectActivationSynchronously(PlacementStrategy strategy, GrainId target, IPlacementRuntime context, out PlacementResult placementResult)
        {
            throw new NotImplementedException();
        }
    }
}

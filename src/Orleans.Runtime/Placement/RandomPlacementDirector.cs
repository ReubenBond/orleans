using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal class RandomPlacementDirector : IPlacementDirector
    {
        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var compatibleSilos = context.GetCompatibleSilos(target);

            // If a valid placement hint was specified, use it.
            if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatibleSilos) is { } placementHint)
            {
                return Task.FromResult(placementHint);
            }

            return Task.FromResult(compatibleSilos[Random.Shared.Next(compatibleSilos.Length)]);
        }
    }

    internal class FixedPlacementDirector : IPlacementDirector
    {
        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var targetSilo = FixedPlacement.ParseSiloAddress(target.GrainIdentity);
            var allSilos = context.GetCompatibleSilos(target);
            var found = false;
            foreach (var silo in allSilos)
            {
                if (silo.Equals(targetSilo))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                ThrowSiloUnavailable(target, targetSilo);
            }

            return Task.FromResult(targetSilo);

            [MethodImpl(MethodImplOptions.NoInlining)]
            static void ThrowSiloUnavailable(PlacementTarget target, SiloAddress targetSilo) => throw new SiloUnavailableException($"The silo {targetSilo} for grain {target.GrainIdentity} is not available");
        }
    }
}

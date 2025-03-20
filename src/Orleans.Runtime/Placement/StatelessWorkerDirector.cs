// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime.Placement;

internal class StatelessWorkerDirector : IPlacementDirector
{
    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var compatibleSilos = context.GetCompatibleSilos(target);

        // If the current silo is not shutting down, place locally if we are compatible
        if (!context.LocalSiloStatus.IsTerminating())
        {
            foreach (var silo in compatibleSilos)
            {
                if (silo.Equals(context.LocalSilo))
                {
                    return Task.FromResult(context.LocalSilo);
                }
            }
        }

        // otherwise, place somewhere else
        return Task.FromResult(compatibleSilos[Random.Shared.Next(compatibleSilos.Length)]);
    }
}

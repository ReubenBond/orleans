// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Runtime.Placement;

namespace UnitTests.Grains;

public class VersionAwarePlacementDirector : IPlacementDirector
{
    private readonly Random random = new Random();

    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        SiloAddress[] silos;
        if (target.InterfaceVersion == 0)
        {
            silos = context.GetCompatibleSilos(target);
        }
        else
        {
            var silosByVersion = context.GetCompatibleSilosWithVersions(target);
            var maxSiloCount = 0;
            ushort version = 0;
            foreach (var kvp in silosByVersion)
            {
                if (kvp.Value.Length > maxSiloCount)
                {
                    version = kvp.Key;
                    maxSiloCount = kvp.Value.Length;
                }
            }
            silos = silosByVersion[version];
        }

        return Task.FromResult(silos[random.Next(silos.Length)]);
    }
}

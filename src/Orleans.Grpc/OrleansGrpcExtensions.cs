using System.Linq;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.gRPC;

namespace Microsoft.Hosting;

public static class OrleansGrpcExtensions
{
    public static ISiloBuilder AddGrpcGrains(this ISiloBuilder siloBuilder)
    {
        return siloBuilder;
    }
}

internal sealed class GprcGrainMetadata : IConfigureGrainTypeComponents
{
    private readonly GrainClassMap _grainClassMap;

    public GprcGrainMetadata(GrainClassMap grainClassMap)
    {
        /*
        var grainClasses = grainTypeOptions.Value.Classes;

        foreach (var grainClass in grainClasses)
        {
            if (!typeof(IGrpcGrain).IsAssignableFrom(grainClass))
            {
                continue;
            }

            // 
        }
        */

        _grainClassMap = grainClassMap;
    }

    public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
    {
        if (!_grainClassMap.TryGetGrainClass(grainType, out var grainClass))
        {
            return;
        }


    }

}

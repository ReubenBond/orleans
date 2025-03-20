// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace Benchmarks.MapReduce;

public class MapReduceBenchmarkConfig : ManualConfig
{
    public MapReduceBenchmarkConfig()
    {
        AddJob(new Job
        {
            Run = {
                LaunchCount = 1,
                IterationCount = 2,
                WarmupCount = 0
            }
        });
    }
}

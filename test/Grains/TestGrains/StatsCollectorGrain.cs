// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Placement;

namespace UnitTests.Stats
{
    [PreferLocalPlacement]
    public class StatsCollectorGrain : Grain, IStatsCollectorGrain
    {
        private long numStatsCalls;

        public Task ReportStatsCalled()
        {
            numStatsCalls++;
            return Task.CompletedTask;
        }
        
        public Task<long> GetReportStatsCallCount()
        {
            return Task.FromResult(numStatsCalls);
        }
    }
}
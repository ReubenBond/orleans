// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.Stats;

public interface IStatsCollectorGrain : IGrainWithIntegerKey
{
    Task ReportStatsCalled();
    
    Task<long> GetReportStatsCallCount();
}
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public static class StreamBatchingTestConst
{
    public const string ProviderName = "StreamBatchingTest";
    public const string BatchingNameSpace = "batching";
    public const string NonBatchingNameSpace = "nonbatching";
}

[GenerateSerializer]
public class ConsumptionReport
{
    [Id(0)]
    public int Consumed { get; set; }

    [Id(1)]
    public int MaxBatchSize { get; set; }
}

public interface IStreamBatchingTestConsumerGrain : IGrainWithGuidKey
{
    Task<ConsumptionReport> GetConsumptionReport();
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace BenchmarkGrainInterfaces.GrainStorage;

[GenerateSerializer]
public class Report
{
    [Id(1)]
    public bool Success { get; set; }

    [Id(2)]
    public TimeSpan Elapsed { get; set; }
}

public interface IPersistentGrain : IGrainWithGuidKey
{
    Task Init(int payloadSize);
    Task<Report> TrySet(int index);
}

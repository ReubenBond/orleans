// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces;

public interface IStatelessWorkerScalingGrain : IGrainWithIntegerKey
{
    Task Wait();

    [AlwaysInterleave]
    Task Release();

    [AlwaysInterleave]
    Task<int> GetActivationCount();

    [AlwaysInterleave]
    Task<int> GetWaitingCount();
}

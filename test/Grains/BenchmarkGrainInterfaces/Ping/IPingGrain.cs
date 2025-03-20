// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Concurrency;

namespace BenchmarkGrainInterfaces.Ping;

public interface IPingGrain : IGrainWithIntegerKey
{
    ValueTask Run();

    [AlwaysInterleave]
    ValueTask PingPongInterleave(IPingGrain other, int count);
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using DistributedTests.GrainInterfaces;

namespace DistributedTests.Grains;

public class PingGrain : Grain, IPingGrain
{
    public ValueTask Ping() => default;
}

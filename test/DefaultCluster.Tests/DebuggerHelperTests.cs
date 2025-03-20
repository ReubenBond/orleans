// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General;

public class DebuggerHelperTests : HostedTestClusterEnsureDefaultStarted
{
    public DebuggerHelperTests(DefaultClusterFixture fixture) : base(fixture)
    {
    }

    [Fact, TestCategory("BVT")]
    public async Task DebuggerHelper_GetGrainInstance()
    {
        var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid()).Cast<IDebuggerHelperTestGrain>();
        await grain.OrleansDebuggerHelper_GetGrainInstance_Test();
    }
}

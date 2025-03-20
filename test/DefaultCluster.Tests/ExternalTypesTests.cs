// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General;

public class ExternalTypesTests : HostedTestClusterEnsureDefaultStarted
{
    public ExternalTypesTests(DefaultClusterFixture fixture) : base(fixture)
    {
    }

    [Fact, TestCategory("BVT"), TestCategory("Serialization")]
    public async Task ExternalTypesTest_GrainWithEnumExternalTypeParam()
    {
        var grainWithEnumTypeParam = this.GrainFactory.GetGrain<IExternalTypeGrain>(0);
        await grainWithEnumTypeParam.GetEnumModel();
    }
}

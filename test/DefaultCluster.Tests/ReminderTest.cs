// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests;

public class ReminderTest : HostedTestClusterEnsureDefaultStarted
{
    public ReminderTest(DefaultClusterFixture fixture) : base(fixture)
    {
    }

    [Fact, TestCategory("BVT"), TestCategory("Reminders")]
    public async Task SimpleGrainGetGrain()
    {
        IReminderTestGrain grain = this.GrainFactory.GetGrain<IReminderTestGrain>(GetRandomGrainId());
        bool notExists = await grain.IsReminderExists("not exists");
        Assert.False(notExists);

        await grain.AddReminder("dummy");
        Assert.True(await grain.IsReminderExists("dummy"));

        await grain.RemoveReminder("dummy");
        Assert.False(await grain.IsReminderExists("dummy"));
    }
}
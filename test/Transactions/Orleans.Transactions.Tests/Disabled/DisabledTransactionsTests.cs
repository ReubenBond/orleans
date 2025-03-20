// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;
using TestExtensions;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class DisabledTransactionsTests : DisabledTransactionsTestRunnerxUnit, IClassFixture<DefaultClusterFixture>
    {
        public DisabledTransactionsTests(DefaultClusterFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}

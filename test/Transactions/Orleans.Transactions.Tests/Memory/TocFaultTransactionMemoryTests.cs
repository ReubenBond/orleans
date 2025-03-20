// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Transactions.TestKit.xUnit;
using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public class TocFaultTransactionMemoryTests : TocFaultTransactionTestRunnerxUnit, IClassFixture<MemoryTransactionsFixture>
{
    public TocFaultTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
        : base(fixture.GrainFactory, output)
    {
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionAttributionTest : TransactionAttributionTestRunner, IClassFixture<MemoryTransactionsFixture>
{
    public TransactionAttributionTest(MemoryTransactionsFixture fixture, ITestOutputHelper output)
        : base(fixture.GrainFactory, output)
    {
    }
}

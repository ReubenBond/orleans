// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.AzureStorage.Tests;

[TestCategory("AzureStorage"), TestCategory("Transactions-dev")]
public class ConsistencyFaultInjectionTests: ConsistencyTransactionTestRunnerxUnit, IClassFixture<RandomFaultInjectedTestFixture>
{
    public ConsistencyFaultInjectionTests(RandomFaultInjectedTestFixture fixture, ITestOutputHelper output)
        : base(fixture.GrainFactory, output)
    { }

    protected override bool StorageAdaptorHasLimitedCommitSpace => true;
    protected override bool StorageErrorInjectionActive => true;
}

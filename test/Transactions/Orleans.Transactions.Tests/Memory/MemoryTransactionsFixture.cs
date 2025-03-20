// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Orleans.Transactions.TestKit;
using TestExtensions;

namespace Orleans.Transactions.Tests;

public class MemoryTransactionsFixture : BaseTestClusterFixture
{
    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
    }

    public class SiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .ConfigureServices(services => services.AddKeyedSingleton<IRemoteCommitService, RemoteCommitService>(TransactionTestConstants.RemoteCommitService))
                .AddMemoryGrainStorage(TransactionTestConstants.TransactionStore)
                .UseTransactions();
        }
    }
}

public class SkewedClockMemoryTransactionsFixture : MemoryTransactionsFixture
{
    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<SkewedClockConfigurator>();
        base.ConfigureTestCluster(builder);
    }
}

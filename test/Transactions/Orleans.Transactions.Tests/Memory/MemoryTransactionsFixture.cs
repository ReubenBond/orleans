using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.Hosting;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Microsoft.Extensions.Hosting;

namespace Orleans.Transactions.Tests
{
    public class MemoryTransactionsFixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(IHostBuilder hostBuilder, ISiloBuilder siloBuilder)
            {
                hostBuilder.ConfigureTracingForTransactionTests();
                siloBuilder
                    .ConfigureServices(services => services.AddSingletonNamedService<IRemoteCommitService, RemoteCommitService>(TransactionTestConstants.RemoteCommitService))
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
}

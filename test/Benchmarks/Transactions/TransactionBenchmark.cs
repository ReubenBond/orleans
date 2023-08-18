using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans.Hosting;
using Orleans.TestingHost;
using BenchmarkGrainInterfaces.Transaction;
using TestExtensions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Transactions;
using Orleans.Configuration;
using Orleans.Runtime;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Text;
using Benchmarks.Ping;

namespace Benchmarks.Transactions
{
    public class TransactionBenchmark : IDisposable
    {
        private TestCluster host;
        private readonly int runs;
        private readonly int transactionsPerRun;
        private readonly int concurrent;

        public TransactionBenchmark(int runs, int transactionsPerRun, int concurrent)
        {
            this.runs = runs;
            this.transactionsPerRun = transactionsPerRun;
            this.concurrent = concurrent;
        }

        public void MemorySetup()
        {
            var builder = new TestClusterBuilder(1);
            builder.AddSiloBuilderConfigurator<SiloMemoryStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public void MemoryThrottledSetup()
        {
            var builder = new TestClusterBuilder(4);
            builder.AddSiloBuilderConfigurator<SiloMemoryStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionThrottlingConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public void AzureSetup()
        {
            var builder = new TestClusterBuilder(4);
            builder.AddSiloBuilderConfigurator<SiloAzureStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public void AzureThrottledSetup()
        {
            var builder = new TestClusterBuilder(4);
            builder.AddSiloBuilderConfigurator<SiloAzureStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionThrottlingConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public class SiloMemoryStorageConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault();
            }
        }

        public class SiloAzureStorageConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureTableTransactionalStateStorageAsDefault(options =>
                {
                    options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                });
            }
        }

        public class SiloTransactionThrottlingConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Configure<TransactionRateLoadSheddingOptions>(options =>
                {
                    options.Enabled = true;
                    options.Limit = 50;
                });
            }
        }

        public Task RunAsync() => Run2(runs, ((InProcessSiloHandle)host.Primary).SiloHost.Services.GetRequiredService<IGrainFactory>(), 10);

        private async Task Run(int runs, IGrainFactory grainFactory, int blocksPerWorker)
        {
            var loadGenerator = new ConcurrentLoadGenerator<ITransactionGrain>(
                maxConcurrency: 250,
                blocksPerWorker: blocksPerWorker,
                requestsPerBlock: 500,
                issueRequest: g => g.Run(),
                getStateForWorker: workerId => grainFactory.GetGrain<ITransactionGrain>(workerId));
            await loadGenerator.Warmup();
            while (runs-- > 0) await loadGenerator.Run();
        }
        private async Task Run2(int runs, IGrainFactory grainFactory, int blocksPerWorker)
        {
            var transactionClient = ((InProcessSiloHandle)host.Primary).SiloHost.Services.GetRequiredService<ITransactionClient>();
            var loadGenerator = new ConcurrentLoadGenerator<(ITransactionGrain A, ITransactionGrain B)>(
                maxConcurrency: 250,
                blocksPerWorker: blocksPerWorker,
                requestsPerBlock: 500,
                issueRequest: g => new(transactionClient.RunTransaction(TransactionOption.Create, async () =>
                {
                    var a = g.A.Run();
                    var b = g.B.Run();
                    await a;
                    await b;
                    return true;
                })),
                getStateForWorker: workerId => (grainFactory.GetGrain<ITransactionGrain>(workerId * 2), grainFactory.GetGrain<ITransactionGrain>(workerId * 2 + 1)));
            await loadGenerator.Warmup();
            while (runs-- > 0) await loadGenerator.Run();
        }

        private async Task FullRunAsync()
        {
            int runners = Math.Max(1,(int)Math.Sqrt(concurrent));
            int transactionsPerRunner = Math.Max(1, this.transactionsPerRun / runners);
            Report[] reports = await Task.WhenAll(Enumerable.Range(0, runners).Select(i => RunAsync(i, transactionsPerRunner, runners)));
            Report finalReport = new Report();
            foreach (Report report in reports)
            {
                finalReport.Succeeded += report.Succeeded;
                finalReport.Failed += report.Failed;
                finalReport.Throttled += report.Throttled;
                finalReport.Elapsed = TimeSpan.FromMilliseconds(Math.Max(finalReport.Elapsed.TotalMilliseconds, report.Elapsed.TotalMilliseconds));
            }
            Console.WriteLine($"{finalReport.Succeeded} transactions in {finalReport.Elapsed.TotalMilliseconds}ms.");
            Console.WriteLine($"{(int)(finalReport.Succeeded * 1000 / finalReport.Elapsed.TotalMilliseconds)} transactions per second.");
            Console.WriteLine($"{finalReport.Failed} transactions failed.");
            Console.WriteLine($"{finalReport.Throttled} transactions were throttled.");
        }

        public async Task<Report> RunAsync(int run, int transactiosPerRun, int concurrentPerRun)
        {
            ILoadGrain load = this.host.Client.GetGrain<ILoadGrain>(Guid.NewGuid());
            await load.Generate(run, transactiosPerRun, concurrentPerRun);
            Report report = null;
            while (report == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                report = await load.TryGetReport();
            }
            return report;
        }

        public void Teardown()
        {
            host.StopAllSilos();
        }

        public void Dispose()
        {
            host?.Dispose();
        }

        public sealed class SiloTransactionConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseTransactions();
            }
        }
    }
}
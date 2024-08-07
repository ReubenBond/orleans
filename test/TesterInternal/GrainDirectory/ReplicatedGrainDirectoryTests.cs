using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.GrainDirectory;

internal interface IMyDirectoryTestGrain : IGrainWithIntegerKey
{
    ValueTask Ping();
}

internal class MyDirectoryTestGrain : Grain, IMyDirectoryTestGrain
{
    public ValueTask Ping() => default;
}

public sealed class ReplicatedGrainDirectoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DynamicClusterTest()
    {
        var testClusterBuilder = new TestClusterBuilder(1);
        testClusterBuilder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        var testCluster = testClusterBuilder.Build();
        await testCluster.DeployAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var reconfigurationTimer = CoarseStopwatch.StartNew();
        var upperLimit = 5;
        var lowerLimit = 1;
        var target = upperLimit;
        var clusterOperation = Task.CompletedTask;
        var idBase = 0L;
        const int CallsPerIteration = 100;
        try
        {
            var loadTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(5));
                        await Parallel.ForAsync(0, CallsPerIteration, (i, ct) => testCluster.GrainFactory.GetGrain<IMyDirectoryTestGrain>(idBase + i).Ping());

                        idBase += CallsPerIteration;

                    }
                    catch (Exception ex)
                    {
                        output.WriteLine($"Ignoring load exception: {ex}");
                    }
                }
            });

            var chaosTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        if (reconfigurationTimer.Elapsed > TimeSpan.FromSeconds(2))
                        {
                            reconfigurationTimer.Restart();
                            await clusterOperation;

                            // Check integrity
                            foreach (var silo in testCluster.Silos)
                            {
                                var address = silo.SiloAddress;
                                var replica = ((IInternalGrainFactory)testCluster.GrainFactory).GetSystemTarget<IGrainDirectoryReplicaTestHooks>(Constants.DirectoryReplicaType, address);
                                await replica.CheckIntegrityAsync();
                            }

                            clusterOperation = Task.Run(async () =>
                            {
                                var currentCount = testCluster.Silos.Count;

                                if (currentCount > target)
                                {
                                    // Stop or kill a random silo, but not the primary (since that hosts cluster membership)
                                    var victim = testCluster.SecondarySilos[Random.Shared.Next(testCluster.SecondarySilos.Count)];
                                    if (currentCount % 2 == 0)
                                    {
                                        await testCluster.StopSiloAsync(victim);
                                    }
                                    else
                                    {
                                        await testCluster.KillSiloAsync(victim);
                                    }
                                }
                                else if (currentCount < target)
                                {
                                    await testCluster.StartAdditionalSiloAsync();
                                }

                                if (currentCount <= lowerLimit)
                                {
                                    target = upperLimit;
                                }
                                else if (currentCount >= upperLimit)
                                {
                                    target = lowerLimit;
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex.GetType().Name.Contains("DebugAssertException"))
                        {
                            throw;
                        }

                        output.WriteLine($"Ignoring chaos exception: {ex}");
                    }
                }
            });

            await Task.WhenAll(loadTask, chaosTask);
        }
        finally
        {
            await testCluster.StopAllSilosAsync();
            await testCluster.DisposeAsync();
        }
    }

    private class SiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.ConfigureLogging(l => l.AddFilter("Orleans.Runtime.GrainDirectory.GrainDirectoryReplica", LogLevel.Trace));
            siloBuilder.ConfigureLogging(l => l.AddFilter("Orleans.Runtime.GrainDirectory.ReplicatedGrainDirectory", LogLevel.Trace));
            siloBuilder.Services.AddSingleton<IFatalErrorHandler, FakeFatalErrorHandler>();
        }
    }

    private class FakeFatalErrorHandler : IFatalErrorHandler
    {
        bool IFatalErrorHandler.IsUnexpected(Exception exception) => false;
        void IFatalErrorHandler.OnFatalException(object sender, string context, Exception exception)
        {
            // no-op
        }
    }
}

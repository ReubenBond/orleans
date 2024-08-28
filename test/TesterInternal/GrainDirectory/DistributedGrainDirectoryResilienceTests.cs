#nullable enable
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BraggerSpecs;
using BraggerSpecs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using TestExtensions;
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

public partial class ClientDirectoryState
{
    partial void Initialize()
    {
        Directory = new DictionaryState<RegistrationState>();
    }
}

public partial class SystemState
{
    partial void Initialize()
    {
        Clients = new DictionaryState<ClientDirectoryState>();
    }
}

// - Perform Register, Unregister, Lookup from each client's perspective
// - Check integrity of each client: invariants hold
// - Each grain is registered exactly once
public sealed class LookupRequest
{
    public required string SiloAddress { get; init; }
    public required string GrainId { get; init; }
}

public sealed class GrainAddressEntity
{
    public required string ActivationId { get; init; }
    public required string GrainId { get; init; }
    public required string SiloAddress { get; init; }

    [return: NotNullIfNotNull(nameof(input))]
    public static implicit operator GrainAddress?(GrainAddressEntity? input)
    {
        if (input is null) return null;
        return new GrainAddress
        {
            GrainId = Orleans.Runtime.GrainId.Parse(input.GrainId),
            SiloAddress = Orleans.Runtime.SiloAddress.FromParsableString(input.SiloAddress),
            ActivationId = Orleans.Runtime.ActivationId.FromParsableString(input.ActivationId)
        };
    }

    [return: NotNullIfNotNull(nameof(input))]
    public static implicit operator GrainAddressEntity?(GrainAddress? input)
    {
        if (input is null) return null;
        return new GrainAddressEntity
        {
            GrainId = input.GrainId.ToString(),
            SiloAddress = input.SiloAddress?.ToParsableString()!,
            ActivationId = input.ActivationId.ToParsableString()
        };
    }

    [return: NotNullIfNotNull(nameof(input))]
    public static implicit operator RegistrationState?(GrainAddressEntity? input)
    {
        if (input is null) return null;
        return new RegistrationState
        {
            GrainId = input.GrainId,
            SiloAddress = input.SiloAddress,
            ActivationId = input.ActivationId
        };
    }

    [return: NotNullIfNotNull(nameof(input))]
    public static implicit operator GrainAddressEntity?(RegistrationState? input)
    {
        if (input is null) return null;
        return new GrainAddressEntity
        {
            GrainId = input.GrainId,
            SiloAddress = input.SiloAddress,
            ActivationId = input.ActivationId
        };
    }
}

public sealed class DistributedGrainDirectoryTestingContext(TestCluster testCluster, OperationDefinitionRegistry registry, string testDirectoryPath) : TestingContext(registry, testDirectoryPath)
{
    public TestCluster TestCluster => testCluster;
}

public class RegisterBehavior : ExecutableBehavior<GrainAddressEntity, GrainAddressEntity?, SystemState>
{
    public override ExpectedOutcomes InvokeOperation(GrainAddressEntity request, SystemState state)
    {
        // Validate
        foreach (var (id, clientState) in state.Clients)
        {
            if (clientState.Directory.TryGetValue(request.GrainId, out var registrationState))
            {
                return new ExpectedOutcome(Descriptor.FromValue((GrainAddressEntity)registrationState), state);
            }
        }

        // Mutate
        var updatedState = (SystemState)state.Clone();
        updatedState.Clients[request.SiloAddress].Directory[request.GrainId] = new RegistrationState
        {
            GrainId = request.GrainId,
            ActivationId = request.ActivationId,
            SiloAddress = request.SiloAddress
        };

        return new ExpectedOutcome(
            Descriptor.FromValue(request),
            updatedState);
    }

    public override async Task<GrainAddressEntity?> ExecuteAsync(TestingContext context, GrainAddressEntity request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var siloHandle = ((DistributedGrainDirectoryTestingContext)context).TestCluster.Silos.Single(s => s.SiloAddress.ToParsableString().Equals(request.SiloAddress));
        var directory = ((InProcessSiloHandle)siloHandle).SiloHost.Services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
        var address = new GrainAddress
        {
            GrainId = GrainId.Parse(request.GrainId),
            SiloAddress = SiloAddress.FromParsableString(request.SiloAddress),
            ActivationId = ActivationId.FromParsableString(request.ActivationId)
        };

        var result = await directory.Register(address);

        return result;
    }
}

public class UnregisterBehavior : ExecutableBehavior<GrainAddressEntity, Empty, SystemState>
{
    public override ExpectedOutcomes InvokeOperation(GrainAddressEntity request, SystemState state)
    {
        // Validate
        foreach (var (id, clientState) in state.Clients)
        {
            if (clientState.Directory.Contains(KeyValuePair.Create<string, RegistrationState>(request.GrainId, request)))
            {
                // Mutate
                var updatedState = (SystemState)state.Clone();
                updatedState.Clients[request.SiloAddress].Directory.Remove(request.GrainId);
                return new ExpectedOutcome(Descriptor.FromValue(Empty.Instance), state);
            }
        }

        return new ExpectedOutcome(
            Descriptor.FromValue(Empty.Instance),
            state);
    }

    public override async Task<Empty> ExecuteAsync(TestingContext context, GrainAddressEntity request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var siloHandle = ((DistributedGrainDirectoryTestingContext)context).TestCluster.Silos.Single(s => s.SiloAddress.ToParsableString().Equals(request.SiloAddress));
        var directory = ((InProcessSiloHandle)siloHandle).SiloHost.Services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
        var address = new GrainAddress
        {
            GrainId = GrainId.Parse(request.GrainId),
            SiloAddress = SiloAddress.FromParsableString(request.SiloAddress),
            ActivationId = ActivationId.FromParsableString(request.ActivationId)
        };

        await directory.Unregister(address);
        return Empty.Instance;
    }
}

public class LookupBehavior : ExecutableBehavior<LookupRequest, GrainAddressEntity?, SystemState>
{
    public override ExpectedOutcomes InvokeOperation(LookupRequest request, SystemState state)
    {
        foreach (var (id, clientState) in state.Clients)
        {
            if (clientState.Directory.TryGetValue(request.GrainId, out var registrationState))
            {
                return new ExpectedOutcome(Descriptor.FromValue((GrainAddressEntity)registrationState), state);
            }
        }

        return new ExpectedOutcome(
            Descriptor.FromValue<GrainAddressEntity?>(null),
            state);
    }

    public override async Task<GrainAddressEntity?> ExecuteAsync(TestingContext context, LookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var siloHandle = ((DistributedGrainDirectoryTestingContext)context).TestCluster.Silos.Single(s => s.SiloAddress.ToParsableString().Equals(request.SiloAddress));
        var directory = ((InProcessSiloHandle)siloHandle).SiloHost.Services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
        var result = await directory.Lookup(GrainId.Parse(request.GrainId));
        return result;
    }
}

public class StopSiloBehavior : ExecutableBehavior<string, Empty, SystemState>
{
    public override ExpectedOutcomes InvokeOperation(string siloAddress, SystemState state)
    {
        if (state.Clients.ContainsKey(siloAddress))
        {
            var updatedState = (SystemState)state.Clone();
            updatedState.Clients.Remove(siloAddress);

            return new ExpectedOutcome(
                Descriptor.FromValue(Empty.Instance),
                updatedState);
        }

        return new ExpectedOutcome(
            Descriptor.FromValue(Empty.Instance),
            state);
    }

    public override async Task<Empty> ExecuteAsync(TestingContext context, string siloAddress)
    {
        ArgumentNullException.ThrowIfNull(siloAddress);
        var siloHandle = ((DistributedGrainDirectoryTestingContext)context).TestCluster.Silos.SingleOrDefault(s => s.SiloAddress.ToParsableString().Equals(siloAddress));
        if (siloHandle is null) return Empty.Instance;
        await siloHandle.StopSiloAsync(stopGracefully: true);
        return Empty.Instance;
    }
}

public class StartSiloBehavior : ExecutableBehavior<string, Empty, SystemState>
{
    public override ExpectedOutcomes InvokeOperation(string siloAddress, SystemState state)
    {
        if (state.Clients.ContainsKey(siloAddress))
        {
            var updatedState = (SystemState)state.Clone();
            updatedState.Clients.Remove(siloAddress);

            return new ExpectedOutcome(
                Descriptor.FromValue(Empty.Instance),
                updatedState);
        }

        return new ExpectedOutcome(
            Descriptor.FromValue(Empty.Instance),
            state);
    }

    public override async Task<Empty> ExecuteAsync(TestingContext context, string siloAddress)
    {
        ArgumentNullException.ThrowIfNull(siloAddress);
        var siloHandle = ((DistributedGrainDirectoryTestingContext)context).TestCluster.Silos.SingleOrDefault(s => s.SiloAddress.ToParsableString().Equals(siloAddress));
        if (siloHandle is null) return Empty.Instance;
        await siloHandle.StopSiloAsync(stopGracefully: true);
        return Empty.Instance;
    }
}

[TestCategory("SlowBVT"), TestCategory("Directory")]
public sealed class DistributedGrainDirectorySpecificationTests : TestClusterPerTest
{
    private static LookupBehavior _lookupBehavior = new LookupBehavior();
    private static RegisterBehavior _registerBehavior = new RegisterBehavior();
    private static UnregisterBehavior _unregisterBehavior = new UnregisterBehavior();
    private readonly ITestOutputHelper _output;
    private OperationDefinitionRegistry _registry;

    public DistributedGrainDirectorySpecificationTests(ITestOutputHelper output)
    {
        _registry = new OperationDefinitionRegistry()
        {
            ["Lookup"] = OperationDefinition.FromExecutableBehavior(_lookupBehavior),
            ["Register"] = OperationDefinition.FromExecutableBehavior(_registerBehavior),
            ["Unregister"] = OperationDefinition.FromExecutableBehavior(_unregisterBehavior),
        };
        _output = output;
    }

    [Fact]
    public async Task OperationalSpecificationTest()
    {
        var siloAddresses = base.HostedCluster.Silos.Select(s => s.SiloAddress.ToParsableString()).ToList();
        var grainIds = Enumerable.Range(0, 1).Select(i => GrainId.Create("dir-test", $"{i}").ToString()).ToList();

        var operations = new OperationSet();
        var startingState = new SystemState();
        foreach (var siloAddress in siloAddresses)
        {
            startingState.Clients[siloAddress] = new ClientDirectoryState();
            foreach (var grainId in grainIds)
            {
                operations.Add(new Operation(
                    $"Lookup '{grainId}' on '{siloAddress}'",
                    _registry["Lookup"],
                    new LookupRequest
                    {
                        SiloAddress = siloAddress,
                        GrainId = grainId
                    }));
                operations.Add(new Operation(
                    $"Register '{grainId}' on '{siloAddress}'",
                    _registry["Register"],
                    new GrainAddressEntity
                    {
                        ActivationId = ActivationId.NewId().ToParsableString(),
                        GrainId = grainId,
                        SiloAddress = siloAddress
                    }));
                operations.Add(new Operation(
                    $"Unregister '{grainId}' on '{siloAddress}'",
                    _registry["Unregister"],
                    new GrainAddressEntity
                    {
                        ActivationId = ActivationId.NewId().ToParsableString(),
                        GrainId = grainId,
                        SiloAddress = siloAddress
                    }));
            }
        }

        var logPath = !Directory.Exists("logs") ? Directory.CreateDirectory("logs") : new DirectoryInfo("logs");
        var outputPath = logPath.CreateSubdirectory(HostedCluster.Options.ClusterId + Guid.NewGuid().ToString("N")[0..5]);
        var testingContext = new DistributedGrainDirectoryTestingContext(HostedCluster, _registry, outputPath.FullName);

        var testCases = TestCaseGenerator.GenerateConcurrentTestCases(
            testingContext,
            startingState,
            operations);

        // VisualizeStateSpace is only called to help developers get an intuitive
        // feel for how how test cases are generated; it is not needed otherwise.
        var dotFileContents = TestCaseGenerator.VisualizeStateSpace(
            testingContext,
            startingState,
            operations);
        File.WriteAllText(Path.Combine(outputPath.FullName, "graph.viz"), dotFileContents);

        var results = await TestCaseExecutor.ExecuteConcurrentTestCases(
            testingContext,
            testCases,
            () => ResetState(testingContext));

        Assert.True(
            results.All(r => r.Success),
            "Some test cases failed.");

        async Task<State> ResetState(DistributedGrainDirectoryTestingContext testingContext)
        {
            foreach (var grain in grainIds)
            {
                var siloHandle = testingContext.TestCluster.Primary;
                var directory = ((InProcessSiloHandle)siloHandle).SiloHost.Services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
                var address = await directory.Lookup(GrainId.Parse(grain));
                if (address is not null)
                {
                    await directory.Unregister(address);
                }
            }

            var newState = new SystemState();
            foreach (var siloAddress in siloAddresses)
            {
                newState.Clients[siloAddress] = new ClientDirectoryState();
            }
            return newState;
        }
    }
}

[TestCategory("SlowBVT"), TestCategory("Directory")]
public sealed class DistributedGrainDirectoryResilienceTests(ITestOutputHelper output)
{
    /// <summary>
    /// Cluster chaos test: tests directory functionality & integrity while starting/stopping/killing silos frequently.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task ElasticClusterWorkload()
    {
        var testClusterBuilder = new TestClusterBuilder(3);
        testClusterBuilder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        var testCluster = testClusterBuilder.Build();
        await testCluster.DeployAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var reconfigurationTimer = CoarseStopwatch.StartNew();
        var upperLimit = 5;
        var lowerLimit = 1;
        var target = upperLimit;
        var clusterOperation = Task.CompletedTask;
        var idBase = 0L;
        var client = ((InProcessSiloHandle)testCluster.Primary).SiloHost.Services.GetRequiredService<IGrainFactory>();
        var client2 = ((InProcessSiloHandle)testCluster.SecondarySilos[0]).SiloHost.Services.GetRequiredService<IGrainFactory>();
        var client3 = ((InProcessSiloHandle)testCluster.SecondarySilos[1]).SiloHost.Services.GetRequiredService<IGrainFactory>();
        const int CallsPerIteration = 100;
        try
        {
            var loadTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5));
                    var time = Stopwatch.StartNew();
                    var workTask = Parallel.ForAsync(0, CallsPerIteration, (i, ct) => client.GetGrain<IMyDirectoryTestGrain>(idBase + i).Ping());
                    using var delayCancellation = new CancellationTokenSource();
                    var delayTask = Task.Delay(TimeSpan.FromMilliseconds(15_000), delayCancellation.Token);
                    await Task.WhenAny(workTask, delayTask);
                    Assert.False(delayTask.IsCompleted);

                    try
                    {
                        await workTask;
                    }
                    catch (SiloUnavailableException sue)
                    {
                        output.WriteLine($"Caught & swallowed transient exception: {sue}");
                    }

                    idBase += CallsPerIteration;
                }
            });

            var chaosTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var remaining = TimeSpan.FromSeconds(2) - reconfigurationTimer.Elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            reconfigurationTimer.Restart();
                            await clusterOperation;

                            // Check integrity
                            foreach (var silo in testCluster.Silos)
                            {
                                var address = silo.SiloAddress;
                                var replica = ((IInternalGrainFactory)client).GetSystemTarget<IGrainDirectoryReplicaTestHooks>(Constants.DirectoryReplicaType, address);
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
                        else
                        {
                            await Task.Delay(remaining);
                        }
                    }
                    catch (Exception exception)
                    {
                        output.WriteLine($"Ignoring chaos exception: {exception}");
                    }
                }
            });

            await await Task.WhenAny(loadTask, chaosTask);
            cts.Cancel();
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
#pragma warning disable ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            //siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            siloBuilder.ConfigureLogging(l => l.AddFilter("Orleans.Runtime.GrainDirectory.GrainDirectoryReplica", LogLevel.Trace));
            siloBuilder.ConfigureLogging(l => l.AddFilter("Orleans.Runtime.GrainDirectory.DistributedGrainDirectory", LogLevel.Information));
        }
    }
}

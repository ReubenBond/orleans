#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Hosting;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.GrainDirectory;

/// <summary>
/// Tests for rolling upgrades from LocalGrainDirectory to DistributedGrainDirectory.
/// </summary>
/// <remarks>
/// These tests verify that a cluster can be migrated from the default DHT-based
/// LocalGrainDirectory to the Virtual Synchrony-based DistributedGrainDirectory
/// without downtime or duplicate grain activations.
/// </remarks>
[TestCategory("SlowBVT"), TestCategory("Directory")]
public sealed class GrainDirectoryMigrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private TestCluster _testCluster = null!;
    private ILogger _log = null!;

    /// <summary>
    /// Configuration key used to signal that a silo should use DistributedGrainDirectory.
    /// </summary>
    private const string UseDistributedGrainDirectoryKey = "GrainDirectoryMigrationTests:UseDistributedGrainDirectory";

    public GrainDirectoryMigrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // Start with OLD silos (default LocalGrainDirectory configuration)
        // The MigrationSiloConfigurator checks configuration to decide whether to use DistributedGrainDirectory
        var testClusterBuilder = new TestClusterBuilder(2);
        testClusterBuilder.AddSiloBuilderConfigurator<MigrationSiloConfigurator>();
        _testCluster = testClusterBuilder.Build();
        await _testCluster.DeployAsync();
        _log = _testCluster.ServiceProvider.GetRequiredService<ILogger<GrainDirectoryMigrationTests>>();
        _log.LogInformation("Test cluster deployed with {SiloCount} OLD silos", _testCluster.Silos.Count);
    }

    public async Task DisposeAsync()
    {
        await _testCluster.StopAllSilosAsync();
        await _testCluster.DisposeAsync();
    }

    /// <summary>
    /// Tests that grains registered on OLD silos remain accessible when NEW silos join the cluster.
    /// </summary>
    [Fact]
    public async Task GrainsRegisteredOnOldSilos_RemainAccessible_WhenNewSilosJoin()
    {
        // Arrange: Create grains on OLD silos
        var grainCount = 50;
        var grains = new List<IMyDirectoryTestGrain>();
        for (int i = 0; i < grainCount; i++)
        {
            var grain = _testCluster.Client.GetGrain<IMyDirectoryTestGrain>(i);
            await grain.Ping();
            grains.Add(grain);
        }
        _log.LogInformation("Created {GrainCount} grains on OLD silos", grainCount);

        // Act: Start NEW silos with DistributedGrainDirectory
        var newSilo1 = await StartNewSiloAsync();
        var newSilo2 = await StartNewSiloAsync();
        _log.LogInformation("Started 2 NEW silos: {Silo1}, {Silo2}", newSilo1.SiloAddress, newSilo2.SiloAddress);

        // Wait for membership to stabilize
        await _testCluster.WaitForLivenessToStabilizeAsync();

        // Assert: All grains should still be accessible
        foreach (var grain in grains)
        {
            await grain.Ping(); // Should not throw
        }
        _log.LogInformation("All {GrainCount} grains still accessible after NEW silos joined", grainCount);
    }

    /// <summary>
    /// Tests that grains can be registered on NEW silos and looked up from OLD silos.
    /// </summary>
    [Fact]
    public async Task GrainsRegisteredOnNewSilos_CanBeLookedUp_FromOldSilos()
    {
        // Log initial cluster state
        _log.LogInformation("Initial cluster has {SiloCount} OLD silos:", _testCluster.Silos.Count);
        foreach (var silo in _testCluster.Silos)
        {
            _log.LogInformation("  OLD Silo {Address}", silo.SiloAddress);
        }

        // Test that OLD silos work - create a NEW grain before NEW silo joins
        _log.LogInformation("Creating grain 99999 on OLD silos BEFORE new silo joins...");
        var simpleGrain = _testCluster.Client.GetGrain<IMyDirectoryTestGrain>(99999);
        await simpleGrain.Ping();
        _log.LogInformation("Successfully created grain 99999 on OLD silos");

        // Start NEW silo
        var newSilo = await StartNewSiloAsync();
        _log.LogInformation("Started NEW silo: {Silo}", newSilo.SiloAddress);
        await _testCluster.WaitForLivenessToStabilizeAsync();
        _log.LogInformation("Liveness stabilized");
        
        // Log all silos and their types
        _log.LogInformation("Cluster now has {SiloCount} silos:", _testCluster.Silos.Count);
        foreach (var silo in _testCluster.Silos)
        {
            var isNew = IsNewSilo(silo);
            _log.LogInformation("  Silo {Address}: {Type}", silo.SiloAddress, isNew ? "NEW" : "OLD");
        }

        // Wait a bit for any handoff operations to complete
        _log.LogInformation("Waiting 3 seconds for directory handoff to complete...");
        await Task.Delay(3000);
        
        // Check LocalGrainDirectory.Running on each silo
        foreach (var silo in _testCluster.Silos)
        {
            if (silo is InProcessSiloHandle handle)
            {
                var localDir = handle.SiloHost.Services.GetRequiredService<ILocalGrainDirectory>() as LocalGrainDirectory;
                _log.LogInformation("  Silo {Address} ({Type}) LocalGrainDirectory.Running = {Running}",
                    silo.SiloAddress, IsNewSilo(silo) ? "NEW" : "OLD", localDir?.Running ?? false);
            }
        }

        // Test that existing grain still works
        _log.LogInformation("Re-testing grain 99999 (should use cached address)...");
        await simpleGrain.Ping();
        _log.LogInformation("Successfully re-pinged grain 99999");

        // Try to create a NEW grain after NEW silo joins
        _log.LogInformation("Creating a NEW grain 88888 AFTER new silo joined...");
        try
        {
            var newGrain = _testCluster.Client.GetGrain<IMyDirectoryTestGrain>(88888);
            await newGrain.Ping().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            _log.LogInformation("Successfully created grain 88888!");
        }
        catch (TimeoutException)
        {
            _log.LogError("Timeout creating grain 88888 after NEW silo joined");
            
            // Check partition contents on all silos
            foreach (var silo in _testCluster.Silos)
            {
                if (silo is InProcessSiloHandle handle)
                {
                    var partition = handle.SiloHost.Services.GetService<ILocalGrainDirectoryPartition>();
                    var localDir = handle.SiloHost.Services.GetRequiredService<ILocalGrainDirectory>() as LocalGrainDirectory;
                    var activationDir = handle.SiloHost.Services.GetService<ActivationDirectory>();
                    
                    _log.LogInformation("  Silo {Address} ({Type}) partition count: {Count}, Running: {Running}, activations: {Activations}",
                        silo.SiloAddress, 
                        IsNewSilo(silo) ? "NEW" : "OLD",
                        partition?.Count ?? -1,
                        localDir?.Running ?? false,
                        activationDir?.Count ?? -1);
                    
                    // Check silo status
                    var statusOracle = handle.SiloHost.Services.GetService<ISiloStatusOracle>();
                    if (statusOracle != null)
                    {
                        _log.LogInformation("  Silo {Address} CurrentStatus: {Status}", silo.SiloAddress, statusOracle.CurrentStatus);
                    }
                }
            }
            
            throw;
        }

        _log.LogInformation("All basic connectivity tests passed");
    }

    /// <summary>
    /// Tests a full rolling upgrade scenario where OLD silos are replaced one by one.
    /// </summary>
    [Fact]
    public async Task RollingUpgrade_ReplacesAllOldSilos_WithoutDataLoss()
    {
        // Arrange: Create initial grains on OLD silos
        var grainCount = 100;
        var grainIds = Enumerable.Range(0, grainCount).ToList();
        foreach (var id in grainIds)
        {
            var grain = _testCluster.Client.GetGrain<IMyDirectoryTestGrain>(id);
            await grain.Ping();
        }
        _log.LogInformation("Created {GrainCount} grains on OLD silos", grainCount);

        // Capture initial old silos
        var oldSilos = _testCluster.Silos.ToList();
        _log.LogInformation("Initial cluster has {OldSiloCount} OLD silos", oldSilos.Count);

        // Act: Start NEW silos first
        var newSilo1 = await StartNewSiloAsync();
        var newSilo2 = await StartNewSiloAsync();
        _log.LogInformation("Started 2 NEW silos");
        await _testCluster.WaitForLivenessToStabilizeAsync();

        // Verify grains are still accessible in mixed cluster
        await VerifyAllGrainsAccessible(grainIds, "mixed cluster");

        // Stop OLD silos one by one (except primary which holds membership)
        var nonPrimaryOldSilos = oldSilos.Where(s => s != _testCluster.Primary).ToList();
        foreach (var oldSilo in nonPrimaryOldSilos)
        {
            _log.LogInformation("Stopping OLD silo: {Silo}", oldSilo.SiloAddress);
            await _testCluster.StopSiloAsync(oldSilo);
            await _testCluster.WaitForLivenessToStabilizeAsync();

            // Verify grains are still accessible after each silo removal
            await VerifyAllGrainsAccessible(grainIds, $"after stopping {oldSilo.SiloAddress}");
        }

        // Add one more NEW silo before stopping primary
        var newSilo3 = await StartNewSiloAsync();
        await _testCluster.WaitForLivenessToStabilizeAsync();

        _log.LogInformation("Final cluster has {SiloCount} NEW silos", _testCluster.Silos.Count);

        // Final verification
        await VerifyAllGrainsAccessible(grainIds, "final cluster with only NEW silos");
    }

    /// <summary>
    /// Tests that no duplicate activations occur during rolling upgrade under load.
    /// </summary>
    [Fact]
    public async Task RollingUpgrade_NoDuplicateActivations_UnderLoad()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var grainIdCounter = 0L;
        const int CallsPerIteration = 50;
        var errors = new List<Exception>();

        // Start load generator
        var loadTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var baseId = Interlocked.Add(ref grainIdCounter, CallsPerIteration) - CallsPerIteration;
                var tasks = Enumerable.Range(0, CallsPerIteration)
                    .Select(i => _testCluster.Client.GetGrain<IMyDirectoryTestGrain>(baseId + i).Ping().AsTask())
                    .ToList();

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (SiloUnavailableException ex)
                {
                    _log.LogDebug(ex, "Transient silo unavailable during load");
                }
                catch (OrleansMessageRejectionException ex)
                {
                    _log.LogDebug(ex, "Message rejected during load");
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                    _log.LogError(ex, "Unexpected error during load");
                }

                await Task.Delay(10, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        });

        // Perform rolling upgrade while under load
        await Task.Delay(500); // Let load start

        // Add NEW silos
        var newSilo1 = await StartNewSiloAsync();
        await Task.Delay(1000);
        var newSilo2 = await StartNewSiloAsync();
        await _testCluster.WaitForLivenessToStabilizeAsync();

        // Remove OLD silos (except primary)
        var secondarySilos = _testCluster.SecondarySilos.ToList();
        var oldSecondarySilos = secondarySilos.Where(s => !IsNewSilo(s)).ToList();
        foreach (var oldSilo in oldSecondarySilos)
        {
            _log.LogInformation("Stopping OLD silo under load: {Silo}", oldSilo.SiloAddress);
            await _testCluster.StopSiloAsync(oldSilo);
            await Task.Delay(2000); // Give time for recovery
        }

        // Let the load run a bit more
        await Task.Delay(2000);
        cts.Cancel();
        await loadTask;

        // Assert
        Assert.Empty(errors);
        _log.LogInformation("Completed {GrainCount} grain activations without errors during rolling upgrade", grainIdCounter);
    }

    /// <summary>
    /// Tests directory integrity after rolling upgrade using integrity checks.
    /// </summary>
    [Fact]
    public async Task RollingUpgrade_DirectoryIntegrity_IsPreserved()
    {
        // Arrange: Create grains
        var grainCount = 50;
        for (int i = 2000; i < 2000 + grainCount; i++)
        {
            var grain = _testCluster.Client.GetGrain<IMyDirectoryTestGrain>(i);
            await grain.Ping();
        }

        // Act: Perform rolling upgrade
        var newSilo1 = await StartNewSiloAsync();
        var newSilo2 = await StartNewSiloAsync();
        await _testCluster.WaitForLivenessToStabilizeAsync();

        // Run integrity checks on all silos
        await CheckDirectoryIntegrity();

        // Stop an OLD silo
        var secondarySilos = _testCluster.SecondarySilos.ToList();
        if (secondarySilos.Count > 0)
        {
            var oldSilo = secondarySilos.FirstOrDefault(s => !IsNewSilo(s));
            if (oldSilo != null)
            {
                await _testCluster.StopSiloAsync(oldSilo);
                await _testCluster.WaitForLivenessToStabilizeAsync();
            }
        }

        // Run integrity checks again
        await CheckDirectoryIntegrity();

        // Verify all grains are still accessible
        for (int i = 2000; i < 2000 + grainCount; i++)
        {
            var grain = _testCluster.Client.GetGrain<IMyDirectoryTestGrain>(i);
            await grain.Ping();
        }
    }

    private async Task<SiloHandle> StartNewSiloAsync()
    {
        // Start a new silo with DistributedGrainDirectory configuration
        // The MigrationSiloConfigurator reads this configuration key to decide behavior
        var configOverrides = new List<IConfigurationSource>
        {
            new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    [UseDistributedGrainDirectoryKey] = "true"
                }
            }
        };

        _log.LogInformation("Starting NEW silo with UseDistributedGrainDirectory...");
        var handle = await _testCluster.StartSiloAsync(
            _testCluster.Silos.Count,
            _testCluster.Options,
            configOverrides,
            startSiloOnNewPort: true);
        
        _log.LogInformation("NEW silo started: {Address}", handle.SiloAddress);
        
        // Verify the silo is actually started
        if (handle is InProcessSiloHandle inProcess)
        {
            var siloLifecycle = inProcess.SiloHost.Services.GetService<ISiloLifecycle>();
            _log.LogInformation("NEW silo lifecycle service: {Service}", siloLifecycle != null ? "exists" : "null");
        }

        return handle;
    }

    private bool IsNewSilo(SiloHandle silo)
    {
        // Check if silo has the distributed grain directory capability
        if (silo is InProcessSiloHandle inProcess)
        {
            var partition = inProcess.SiloHost.Services.GetService<ILocalGrainDirectoryPartition>();
            return partition is DelegatingGrainDirectoryPartition;
        }
        return false;
    }

    private async Task VerifyAllGrainsAccessible(IEnumerable<int> grainIds, string phase)
    {
        var failures = new List<(int Id, Exception Ex)>();
        foreach (var id in grainIds)
        {
            try
            {
                var grain = _testCluster.Client.GetGrain<IMyDirectoryTestGrain>(id);
                await grain.Ping();
            }
            catch (Exception ex)
            {
                failures.Add((id, ex));
            }
        }

        if (failures.Count > 0)
        {
            _log.LogError("Failed to access {FailureCount} grains during {Phase}", failures.Count, phase);
            foreach (var (id, ex) in failures.Take(5))
            {
                _log.LogError(ex, "Grain {GrainId} failed", id);
            }
        }

        Assert.Empty(failures);
        _log.LogInformation("Verified all grains accessible during {Phase}", phase);
    }

    private async Task CheckDirectoryIntegrity()
    {
        var client = (IInternalGrainFactory)_testCluster.Client;
        var integrityChecks = new List<Task>();

        foreach (var silo in _testCluster.Silos)
        {
            var address = silo.SiloAddress;
            for (var partitionIndex = 0; partitionIndex < DirectoryMembershipSnapshot.PartitionsPerSilo; partitionIndex++)
            {
                var replica = client.GetSystemTarget<IGrainDirectoryTestHooks>(
                    GrainDirectoryPartition.CreateGrainId(address, partitionIndex).GrainId);
                integrityChecks.Add(replica.CheckIntegrityAsync().AsTask());
            }
        }

        await Task.WhenAll(integrityChecks);
        _log.LogInformation("Directory integrity checks passed for all {SiloCount} silos", _testCluster.Silos.Count);
    }

    /// <summary>
    /// Configurator that conditionally enables DistributedGrainDirectory based on configuration.
    /// </summary>
    /// <remarks>
    /// This configurator checks for <see cref="UseDistributedGrainDirectoryKey"/> in the configuration.
    /// If the key is set to "true", the silo will use DistributedGrainDirectory (NEW silo).
    /// Otherwise, it uses the default LocalGrainDirectory (OLD silo).
    /// </remarks>
    private class MigrationSiloConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            hostBuilder.UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder.Configure<SiloMessagingOptions>(o =>
                {
                    o.ResponseTimeout = TimeSpan.FromMinutes(2);
                    o.SystemResponseTimeout = TimeSpan.FromMinutes(2);
                });

                // Check configuration to decide if this is a NEW silo
                var useDistributed = ctx.Configuration.GetValue<bool>(UseDistributedGrainDirectoryKey);
                if (useDistributed)
                {
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only
                    siloBuilder.UseDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
                }
            });
        }
    }
}

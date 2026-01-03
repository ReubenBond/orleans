#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// A grain directory implementation that facilitates rolling upgrades from the DHT-based
/// <see cref="LocalGrainDirectory"/> to the Virtual Synchrony-based <see cref="DistributedGrainDirectory"/>.
/// </summary>
/// <remarks>
/// <para>
/// During a rolling upgrade, the cluster contains a mix of OLD silos (using <see cref="LocalGrainDirectory"/>)
/// and NEW silos (using <see cref="DistributedGrainDirectory"/>). This class ensures consistency by:
/// </para>
/// <list type="bullet">
/// <item>Forwarding requests to the DHT when the DHT owner is an OLD silo</item>
/// <item>Using <see cref="DistributedGrainDirectory"/> when all relevant silos are NEW</item>
/// </list>
/// <para>
/// <b>Usage:</b>
/// <list type="number">
/// <item>Deploy new silos with <c>UseMigratingGrainDirectory()</c> enabled</item>
/// <item>Gradually replace old silos with new silos</item>
/// <item>Once all silos are upgraded, switch to <c>UseDistributedGrainDirectory()</c> and redeploy</item>
/// </list>
/// </para>
/// </remarks>
internal sealed partial class MigratingGrainDirectory : IGrainDirectory, ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private readonly IGrainDirectory _distributedDirectory;
    private readonly ILocalGrainDirectory _dhtDirectory;
    private readonly DirectoryMembershipService _membershipService;
    private readonly ILogger<MigratingGrainDirectory> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly AsyncEnumerable<bool> _migrationCompleteUpdates;
    
    private Task? _monitorTask;
    private bool _migrationComplete;
    private bool _loggedMigrationComplete;

    public MigratingGrainDirectory(
        IGrainDirectory distributedDirectory,
        ILocalGrainDirectory dhtDirectory,
        DirectoryMembershipService membershipService,
        ILogger<MigratingGrainDirectory> logger)
    {
        _distributedDirectory = distributedDirectory ?? throw new ArgumentNullException(nameof(distributedDirectory));
        _dhtDirectory = dhtDirectory ?? throw new ArgumentNullException(nameof(dhtDirectory));
        _membershipService = membershipService ?? throw new ArgumentNullException(nameof(membershipService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        _migrationCompleteUpdates = new AsyncEnumerable<bool>(
            initialValue: false,
            updateValidator: (previous, proposed) => proposed != previous,
            onPublished: value => _migrationComplete = value);
    }

    /// <summary>
    /// Gets a value indicating whether the migration is complete (all silos are NEW).
    /// </summary>
    public bool IsMigrationComplete => _migrationComplete;

    /// <inheritdoc />
    public async Task<GrainAddress?> Register(GrainAddress address)
    {
        if (ShouldUseDht(address.GrainId, out var dhtOwner))
        {
            LogDebugForwardingToDht("Register", address.GrainId, dhtOwner!);
            var result = await _dhtDirectory.RegisterAsync(address, hopCount: 0);
            return result.Address;
        }

        return await _distributedDirectory.Register(address);
    }

    /// <inheritdoc />
    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress)
    {
        if (ShouldUseDht(address.GrainId, out var dhtOwner))
        {
            LogDebugForwardingToDht("Register", address.GrainId, dhtOwner!);
            var result = await _dhtDirectory.RegisterAsync(address, previousAddress, hopCount: 0);
            return result.Address;
        }

        return await _distributedDirectory.Register(address, previousAddress);
    }

    /// <inheritdoc />
    public async Task<GrainAddress?> Lookup(GrainId grainId)
    {
        if (ShouldUseDht(grainId, out var dhtOwner))
        {
            LogDebugForwardingToDht("Lookup", grainId, dhtOwner!);
            var result = await _dhtDirectory.LookupAsync(grainId, hopCount: 0);
            return result.Address;
        }

        return await _distributedDirectory.Lookup(grainId);
    }

    /// <inheritdoc />
    public async Task Unregister(GrainAddress address)
    {
        if (ShouldUseDht(address.GrainId, out var dhtOwner))
        {
            LogDebugForwardingToDht("Unregister", address.GrainId, dhtOwner!);
            await _dhtDirectory.UnregisterAsync(address, UnregistrationCause.Force, hopCount: 0);
            return;
        }

        await _distributedDirectory.Unregister(address);
    }

    /// <inheritdoc />
    public Task UnregisterSilos(List<SiloAddress> siloAddresses)
    {
        // Both directories handle silo removal through their own mechanisms
        return _distributedDirectory.UnregisterSilos(siloAddresses);
    }

    /// <summary>
    /// Determines whether the request should be forwarded to the DHT directory.
    /// </summary>
    /// <remarks>
    /// During a mixed cluster (OLD + NEW silos), the DHT remains authoritative.
    /// We forward to the DHT if the DHT owner is an OLD silo (not in the filtered membership).
    /// </remarks>
    private bool ShouldUseDht(GrainId grainId, out SiloAddress? dhtOwner)
    {
        // Calculate who owns this grain in the DHT (which includes ALL silos)
        dhtOwner = _dhtDirectory.GetPrimaryForGrain(grainId);
        if (dhtOwner is null)
        {
            // No DHT owner (e.g., we're the only silo and shutting down)
            return false;
        }

        var view = _membershipService.CurrentView;
        
        // If there are no members in the filtered view, fall back to DHT
        if (view.Members.Length == 0)
        {
            return true;
        }

        // Check if the DHT owner is in the filtered membership (i.e., is a NEW silo)
        // If not, it's an OLD silo and we should use the DHT
        return !view.Members.Contains(dhtOwner);
    }

    /// <summary>
    /// Monitors membership changes and detects when migration is complete.
    /// </summary>
    private async Task MonitorMigrationStatus()
    {
        try
        {
            await foreach (var view in _membershipService.ViewUpdates.WithCancellation(_shutdownCts.Token))
            {
                var allActiveSilos = view.ClusterMembershipSnapshot.Members.Values
                    .Where(m => m.Status == SiloStatus.Active)
                    .Select(m => m.SiloAddress)
                    .ToList();

                // Migration is complete when filtered members == all active members
                // This means all silos have the DistributedGrainDirectory capability
                var isComplete = view.Members.Length == allActiveSilos.Count && view.Members.Length > 0;

                if (isComplete && !_loggedMigrationComplete)
                {
                    _loggedMigrationComplete = true;
                    LogInfoMigrationComplete(view.Members.Length);
                }
                else if (!isComplete && _loggedMigrationComplete)
                {
                    // Migration status changed back (e.g., an OLD silo joined)
                    _loggedMigrationComplete = false;
                    var oldSiloCount = allActiveSilos.Count - view.Members.Length;
                    LogWarningMigrationIncomplete(oldSiloCount, view.Members.Length);
                }

                _migrationCompleteUpdates.Publish(isComplete);
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            LogErrorMonitoringMigration(ex);
        }
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(MigratingGrainDirectory),
            ServiceLifecycleStage.RuntimeServices,
            OnStart,
            OnStop);

        Task OnStart(CancellationToken cancellationToken)
        {
            _monitorTask = Task.Run(MonitorMigrationStatus, cancellationToken);
            return Task.CompletedTask;
        }

        async Task OnStop(CancellationToken cancellationToken)
        {
            _shutdownCts.Cancel();
            if (_monitorTask is not null)
            {
                await _monitorTask.SuppressThrowing();
            }
        }
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        _migrationCompleteUpdates.Dispose();
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "MigratingGrainDirectory: Forwarding {Operation} for grain {GrainId} to DHT owner {DhtOwner} (OLD silo)"
    )]
    private partial void LogDebugForwardingToDht(string operation, GrainId grainId, SiloAddress dhtOwner);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Grain directory migration complete: All {SiloCount} silos now support DistributedGrainDirectory. " +
                  "You can now switch from UseMigratingGrainDirectory() to UseDistributedGrainDirectory() and redeploy."
    )]
    private partial void LogInfoMigrationComplete(int siloCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Grain directory migration incomplete: {OldSiloCount} OLD silo(s) detected, {NewSiloCount} NEW silo(s) active. " +
                  "Requests will continue to be forwarded to DHT for grains owned by OLD silos."
    )]
    private partial void LogWarningMigrationIncomplete(int oldSiloCount, int newSiloCount);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error monitoring grain directory migration status"
    )]
    private partial void LogErrorMonitoringMigration(Exception exception);
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class DirectoryMembershipService : IAsyncDisposable
{
    private readonly IInternalGrainFactory _grainFactory;
    private readonly IClusterManifestProvider _clusterManifestProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _runTask;
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates;

    public DirectoryMembershipSnapshot CurrentView { get; private set; } = DirectoryMembershipSnapshot.Default;

    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => _viewUpdates;

    public ClusterMembershipService ClusterMembershipService { get; }

    /// <summary>
    /// Gets all active silos in the cluster, regardless of their grain directory capability.
    /// Used for recovery operations that need to query all silos for their activations.
    /// </summary>
    public ImmutableArray<SiloAddress> AllActiveMembers => CurrentView.ClusterMembershipSnapshot.Members.Values
        .Where(m => m.Status == SiloStatus.Active)
        .Select(m => m.SiloAddress)
        .ToImmutableArray();

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken)
    {
        _ = ClusterMembershipService.Refresh(version, cancellationToken);
        if (CurrentView.Version <= version)
        {
            await foreach (var view in _viewUpdates.WithCancellation(cancellationToken))
            {
                if (view.Version >= version)
                {
                    break;
                }
            }
        }

        return CurrentView;
    }

    public DirectoryMembershipService(
        ClusterMembershipService clusterMembershipService,
        IInternalGrainFactory grainFactory,
        ILogger<DirectoryMembershipService> logger,
        IClusterManifestProvider clusterManifestProvider)
    {
        _viewUpdates = new(
            DirectoryMembershipSnapshot.Default,
            (previous, proposed) => proposed.Version >= previous.Version,
            update => CurrentView = update);
        ClusterMembershipService = clusterMembershipService;
        _grainFactory = grainFactory;
        _logger = logger;
        _clusterManifestProvider = clusterManifestProvider;
        using var _ = new ExecutionContextSuppressor();
        _runTask = Task.Run(ProcessMembershipUpdates);
    }

    private async Task ProcessMembershipUpdates()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                try
                {
                    await foreach (var update in ClusterMembershipService.MembershipUpdates.WithCancellation(_shutdownCts.Token))
                    {
                        var view = new DirectoryMembershipSnapshot(update, _grainFactory, HasDistributedGrainDirectoryCapability);
                        LogDebugMembershipUpdate(view.Version, view.Members.Length, update.Members.Count(m => m.Value.Status == SiloStatus.Active));
                        _viewUpdates.Publish(view);
                    }
                }
                catch (Exception exception)
                {
                    if (!_shutdownCts.IsCancellationRequested)
                    {
                        LogErrorProcessingMembershipUpdates(exception);
                    }
                }
            }
        }
        finally
        {
            _viewUpdates.Dispose();
        }
    }

    /// <summary>
    /// Checks if a silo has the distributed grain directory capability by looking at the cluster manifest.
    /// The capability is advertised via GrainManifest.Properties when UseDistributedGrainDirectory is called.
    /// </summary>
    /// <remarks>
    /// During rolling upgrades, we have three scenarios:
    /// 1. All OLD silos (none have capability) - don't filter, include all silos
    /// 2. Mixed cluster (some have capability) - only include silos with capability
    /// 3. All NEW silos (all have capability) - include all silos
    /// 
    /// This approach ensures that the DistributedGrainDirectory membership is correct during migration.
    /// </remarks>
    private bool HasDistributedGrainDirectoryCapability(SiloAddress siloAddress)
    {
        var manifest = _clusterManifestProvider.Current;
        
        // Check if ANY silo in the cluster has the distributed grain directory capability
        bool anyHasCapability = false;
        foreach (var siloManifest in manifest.Silos.Values)
        {
            if (siloManifest.Properties.TryGetValue(GrainDirectoryCapability.MetadataKey, out var cap)
                && cap == GrainDirectoryCapability.Distributed)
            {
                anyHasCapability = true;
                break;
            }
        }

        if (!anyHasCapability)
        {
            // No silos have the capability - this is an all-OLD-silos cluster.
            // Don't filter; include all silos (matching previous behavior when SiloMetadataCache was null).
            return true;
        }

        // Mixed or all-NEW cluster - filter based on capability
        if (manifest.Silos.TryGetValue(siloAddress, out var grainManifest))
        {
            return grainManifest.Properties.TryGetValue(GrainDirectoryCapability.MetadataKey, out var capability)
                && capability == GrainDirectoryCapability.Distributed;
        }

        // Silo not yet in the cluster manifest - it's either new or we haven't fetched its manifest yet.
        // Treat as not having the capability for safety during rolling upgrades.
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        await _runTask.SuppressThrowing();
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Directory membership updated to version {Version} with {FilteredMemberCount} filtered members out of {TotalActiveMemberCount} total active members."
    )]
    private partial void LogDebugMembershipUpdate(MembershipVersion version, int filteredMemberCount, int totalActiveMemberCount);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing membership updates."
    )]
    private partial void LogErrorProcessingMembershipUpdates(Exception exception);
}

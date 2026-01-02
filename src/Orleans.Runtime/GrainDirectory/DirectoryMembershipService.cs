using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
    /// <summary>
    /// Threshold after which we log a warning about waiting for manifest.
    /// This is for diagnostics only - we will continue waiting indefinitely
    /// because all silos must have identical membership views.
    /// </summary>
    private static readonly TimeSpan ManifestWaitWarningThreshold = TimeSpan.FromSeconds(5);

    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly IClusterManifestProvider _clusterManifestProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _runTask;
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates;

    public DirectoryMembershipSnapshot CurrentView { get; private set; } = DirectoryMembershipSnapshot.Default;

    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => _viewUpdates;

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
        _ = _clusterMembershipService.Refresh(version, cancellationToken);
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
        IClusterMembershipService clusterMembershipService,
        IInternalGrainFactory grainFactory,
        ILogger<DirectoryMembershipService> logger,
        IClusterManifestProvider clusterManifestProvider)
    {
        _viewUpdates = new(
            DirectoryMembershipSnapshot.Default,
            (previous, proposed) => proposed.Version >= previous.Version,
            update => CurrentView = update);
        _clusterMembershipService = clusterMembershipService;
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
            var cancellationToken = _shutdownCts.Token;
            var membershipEnumerator = _clusterMembershipService.MembershipUpdates.GetAsyncEnumerator(cancellationToken);
            var manifestEnumerator = _clusterManifestProvider.Updates.GetAsyncEnumerator(cancellationToken);

            try
            {
                // Start both enumerators
                var membershipMoveNext = membershipEnumerator.MoveNextAsync().AsTask();
                var manifestMoveNext = manifestEnumerator.MoveNextAsync().AsTask();

                ClusterMembershipSnapshot? currentMembership = null;
                ClusterManifest currentManifest = _clusterManifestProvider.Current;
                
                // Track when we started waiting for manifest completeness (for warning logs only)
                Stopwatch? manifestWaitStopwatch = null;
                bool warnedAboutDelay = false;

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Wait for either a membership update or a manifest update
                    var completedTask = await Task.WhenAny(membershipMoveNext, manifestMoveNext);

                    if (completedTask == membershipMoveNext)
                    {
                        if (!await membershipMoveNext)
                        {
                            // Membership stream ended
                            break;
                        }

                        currentMembership = membershipEnumerator.Current;
                        membershipMoveNext = membershipEnumerator.MoveNextAsync().AsTask();
                        
                        // Reset wait tracking on new membership (silos may have changed)
                        manifestWaitStopwatch = null;
                        warnedAboutDelay = false;
                    }
                    else // completedTask == manifestMoveNext
                    {
                        if (!await manifestMoveNext)
                        {
                            // Manifest stream ended
                            break;
                        }

                        currentManifest = manifestEnumerator.Current;
                        manifestMoveNext = manifestEnumerator.MoveNextAsync().AsTask();
                    }

                    // If we have a membership snapshot, check if manifest is complete
                    if (currentMembership is not null)
                    {
                        // Check if all active silos are in the manifest
                        var activeSilos = currentMembership.Members.Values
                            .Where(m => m.Status == SiloStatus.Active)
                            .Select(m => m.SiloAddress)
                            .ToList();

                        var missingSilos = activeSilos.Where(s => !currentManifest.Silos.ContainsKey(s)).ToList();
                        var allSilosInManifest = missingSilos.Count == 0;

                        if (allSilosInManifest)
                        {
                            // Manifest is complete, publish the view
                            PublishView(currentMembership, currentManifest);
                            manifestWaitStopwatch = null;
                            warnedAboutDelay = false;
                        }
                        else
                        {
                            // Manifest is incomplete - we MUST wait for it to complete.
                            // All silos must have identical membership views for consistency.
                            // Track how long we've been waiting for diagnostic purposes only.
                            manifestWaitStopwatch ??= Stopwatch.StartNew();
                            var elapsed = manifestWaitStopwatch.Elapsed;

                            if (elapsed >= ManifestWaitWarningThreshold && !warnedAboutDelay)
                            {
                                // Log a warning that we're still waiting (diagnostics only)
                                LogWarningWaitingForManifest(currentMembership.Version, missingSilos.Count, elapsed, missingSilos);
                                warnedAboutDelay = true;
                            }
                            else if (!warnedAboutDelay)
                            {
                                // Debug log - waiting for manifest
                                LogDebugWaitingForManifest(currentMembership.Version, missingSilos.Count);
                            }
                        }
                    }
                }
            }
            finally
            {
                await membershipEnumerator.DisposeAsync();
                await manifestEnumerator.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            // Expected during shutdown
        }
        catch (Exception exception)
        {
            if (!_shutdownCts.IsCancellationRequested)
            {
                LogErrorProcessingMembershipUpdates(exception);
            }
        }
        finally
        {
            _viewUpdates.Dispose();
        }
    }

    private void PublishView(ClusterMembershipSnapshot membership, ClusterManifest manifest)
    {
        // Pre-compute whether any silo has the distributed capability (for performance)
        var anyHasCapability = HasAnyDistributedGrainDirectoryCapability(manifest);
        
        var view = new DirectoryMembershipSnapshot(
            membership,
            _grainFactory,
            siloAddress => HasDistributedGrainDirectoryCapability(siloAddress, manifest, anyHasCapability));

        var activeCount = membership.Members.Count(m => m.Value.Status == SiloStatus.Active);
        LogDebugMembershipUpdate(view.Version, view.Members.Length, activeCount);
        _viewUpdates.Publish(view);
    }

    /// <summary>
    /// Checks if any silo in the cluster has the distributed grain directory capability.
    /// </summary>
    private static bool HasAnyDistributedGrainDirectoryCapability(ClusterManifest manifest)
    {
        foreach (var siloManifest in manifest.Silos.Values)
        {
            if (siloManifest.Properties.TryGetValue(GrainDirectoryCapability.MetadataKey, out var cap)
                && cap == GrainDirectoryCapability.Distributed)
            {
                return true;
            }
        }
        return false;
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
    /// <param name="siloAddress">The silo to check.</param>
    /// <param name="manifest">The cluster manifest.</param>
    /// <param name="anyHasCapability">Pre-computed flag indicating if any silo has the capability.</param>
    private static bool HasDistributedGrainDirectoryCapability(SiloAddress siloAddress, ClusterManifest manifest, bool anyHasCapability)
    {
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
        Level = LogLevel.Debug,
        Message = "Waiting for cluster manifest to include {MissingSiloCount} active silos before publishing directory membership version {Version}."
    )]
    private partial void LogDebugWaitingForManifest(MembershipVersion version, int missingSiloCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Still waiting for cluster manifest to include {MissingSiloCount} active silos after {Elapsed}. Directory membership version {Version} is delayed until manifest is complete. Missing silos: {MissingSilos}. This may indicate a silo startup issue or network problem."
    )]
    private partial void LogWarningWaitingForManifest(MembershipVersion version, int missingSiloCount, TimeSpan elapsed, List<SiloAddress> missingSilos);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing membership updates."
    )]
    private partial void LogErrorProcessingMembershipUpdates(Exception exception);
}

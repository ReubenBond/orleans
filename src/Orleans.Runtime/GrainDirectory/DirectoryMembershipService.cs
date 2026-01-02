using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class DirectoryMembershipService : IAsyncDisposable
{
    private readonly IInternalGrainFactory _grainFactory;
    private readonly ISiloMetadataCache? _siloMetadataCache;
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
        ISiloMetadataCache? siloMetadataCache = null)
    {
        _viewUpdates = new(
            DirectoryMembershipSnapshot.Default,
            (previous, proposed) => proposed.Version >= previous.Version,
            update => CurrentView = update);
        ClusterMembershipService = clusterMembershipService;
        _grainFactory = grainFactory;
        _logger = logger;
        _siloMetadataCache = siloMetadataCache;
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
    /// Checks if a silo has the distributed grain directory capability.
    /// If no metadata cache is configured, all silos are considered capable (no filtering).
    /// </summary>
    private bool HasDistributedGrainDirectoryCapability(SiloAddress siloAddress)
    {
        if (_siloMetadataCache is null)
        {
            // No filtering configured - include all silos
            return true;
        }

        var metadata = _siloMetadataCache.GetSiloMetadata(siloAddress);
        return metadata.Metadata.TryGetValue(GrainDirectoryCapability.MetadataKey, out var capability)
            && capability == GrainDirectoryCapability.Distributed;
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class GrainDirectoryReplica(
    ILocalSiloDetails localSiloDetails,
    ClusterMembershipService clusterMembershipService,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    IInternalGrainFactory grainFactory)
    : SystemTarget(Constants.DirectoryReplicaType, localSiloDetails.SiloAddress, loggerFactory), IGrainDirectoryReplica, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly Dictionary<GrainId, GrainAddress> _directory = [];
    private readonly ClusterMembershipService _clusterMembershipService = clusterMembershipService;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IInternalGrainFactory _grainFactory = grainFactory;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SiloAddress _id = localSiloDetails.SiloAddress;
    private readonly ILogger<GrainDirectoryReplica> _logger = loggerFactory.CreateLogger<GrainDirectoryReplica>();
    private readonly TaskCompletionSource _shutdownTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates = new(
        DirectoryMembershipSnapshot.Default,
        (previous, proposed) => proposed.Version >= previous.Version,
        _ => { });

    // Ranges which cannot be served yet, eg because the replica is currently transferring them from a previous owner.
    private readonly List<(RingRange Range, MembershipVersion Version, Task Completion)> _pendingRanges = [];

    // Ranges which were previously at least partially owned by this replica, but which are pending transfer to a new replica.  
    private readonly List<PartitionSnapshotState> _partitionSnapshots = [];

    // The current directory membership snapshot.
    private DirectoryMembershipSnapshot _view = DirectoryMembershipSnapshot.Default;

    private Task? _runTask;
    private DirectoryMembershipSnapshot? _finalView;

    public DirectoryMembershipSnapshot CurrentView => _view;

    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => _viewUpdates;

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version = default)
    {
        _ = _clusterMembershipService.Refresh(version);
        await foreach (var view in _viewUpdates)
        {
            if (view.Version >= version)
            {
                return view;
            }
        }

        return _view;
    }

    async ValueTask<GrainDirectoryPartitionSnapshot?> IGrainDirectoryReplica.GetPartitionSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRange range)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("GetPartitionSnapshotAsync('{Version}', '{RangeVersion}', '{Range}')", version, rangeVersion, range);
        }

        // Wait for the range to be un-wedged.
        await RefreshViewAsync(version);
        await WaitForRange(range, rangeVersion);

        List<GrainAddress> partitionAddresses = [];
        List<RingRange> snapshotRanges = [];
        foreach (var partitionSnapshot in _partitionSnapshots)
        {
            if (partitionSnapshot.DirectoryMembershipVersion != rangeVersion)
            {
                continue;
            }

            // Only include addresses which are in the requested range.
            foreach (var address in partitionSnapshot.GrainAddresses)
            {
                if (range.Contains(address.GrainId))
                {
                    partitionAddresses.Add(address);
                }
            }

            snapshotRanges.AddRange(partitionSnapshot.Range.Intersections(range));
        }

        if (snapshotRanges.Count <= 0)
        {
            _logger.LogWarning("Received a request for a snapshot which this replica does not have, version '{Version}', range version '{RangeVersion}', range '{Range}'.", version, rangeVersion, range);
            return null;
        }

        var rangeSnapshot = new GrainDirectoryPartitionSnapshot(rangeVersion, partitionAddresses, snapshotRanges);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Transferring '{Count}' entries in range '{Range}' from version '{Version}' snapshot.", partitionAddresses.Count, range, rangeVersion);
        }

        return rangeSnapshot;
    }

    ValueTask IGrainDirectoryReplica.AcknowledgeSnapshotTransferAsync(SiloAddress owner, MembershipVersion rangeVersion)
    {
        RemoveSnapshotTransferPartner(owner, rangeVersion);
        return ValueTask.CompletedTask;
    }

    private void RemoveSnapshotTransferPartner(SiloAddress owner, MembershipVersion? rangeVersion)
    {
        for (var i = 0; i < _partitionSnapshots.Count; ++i)
        {
            var partitionSnapshot = _partitionSnapshots[i];
            if (rangeVersion.HasValue && partitionSnapshot.DirectoryMembershipVersion != rangeVersion.Value)
            {
                continue;
            }

            partitionSnapshot.TransferPartners.Remove(owner);
            if (partitionSnapshot.TransferPartners.Count == 0)
            {
                _partitionSnapshots.RemoveAt(i);

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Removing version '{Version}' snapshot. Current snapshots: [{CurrentSnapshots}].", partitionSnapshot.DirectoryMembershipVersion, string.Join(", ", _partitionSnapshots.Select(s => s.DirectoryMembershipVersion)));
                }

                // Trigger shutdown completion if the final snapshot has been transferred.
                if (_finalView is { } finalView && !_partitionSnapshots.Any(snapshot => finalView.Version == snapshot.DirectoryMembershipVersion))
                {
                    _shutdownTcs.TrySetResult();
                }

                break;
            }
        }
    }

    [Conditional("DEBUG")]
    private void AssertOwnership(GrainId grainId)
    {
        var view = _view;
        Debug.Assert(view.TryGetOwnerIndex(grainId, out var index));
        Debug.Assert(_id.Equals(view.Members[index]));
    }

    private ValueTask WaitForRange(GrainId grainId, MembershipVersion version) => WaitForRange(RingRange.FromPoint(grainId.GetUniformHashCode()), version);

    private async ValueTask WaitForRange(RingRange range, MembershipVersion version)
    {
        if (_view.Version < version)
        {
            await RefreshViewAsync(version);
        }

        while (TryGetOverlappingWedge(range, version, out var completion))
        {
            await completion;
        }

        bool TryGetOverlappingWedge(RingRange range, MembershipVersion version, [NotNullWhen(true)] out Task? completion)
        {
            foreach (var wedge in _pendingRanges)
            {
                if (wedge.Version <= version && wedge.Range.Overlaps(range))
                {
                    completion = wedge.Completion;
                    return true;
                }
            }

            completion = null;
            return false;
        }
    }

    public IGrainDirectoryReplica GetReplica(SiloAddress address) => _grainFactory.GetSystemTarget<IGrainDirectoryReplica>(Constants.DirectoryReplicaType, address);

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(nameof(GrainDirectoryReplica), ServiceLifecycleStage.RuntimeInitialize, OnRuntimeInitializeStart, OnRuntimeInitializeStop);

        // Transition into 'ShuttingDown'/'Stopping' stage, removing ourselves from directory membership, but allow some time for hand-off before transitioning to 'Dead'.
        observer.Subscribe(nameof(GrainDirectoryReplica), ServiceLifecycleStage.BecomeActive - 1, _ => Task.CompletedTask, OnShuttingDown);
    }

    private async Task OnShuttingDown(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _partitionSnapshots.Count > 0)
        {
            await _shutdownTcs.Task.WaitAsync(token).SuppressThrowing();
        }
    }

    private Task OnRuntimeInitializeStart(CancellationToken cancellationToken)
    {
        var catalog = _serviceProvider.GetRequiredService<Catalog>();
        catalog.RegisterSystemTarget(this);

        using var _ = new ExecutionContextSuppressor();
        WorkItemGroup.QueueAction(() => _runTask = ProcessMembershipUpdates());

        return Task.CompletedTask;
    }

    async Task OnRuntimeInitializeStop(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel();
        if (_runTask is { } task)
        {
            // Try to wait for hand-off to complete.
            await this.RunOrQueueTask(async () => await task.WaitAsync(cancellationToken).SuppressThrowing());
        }
    }

    private async Task ProcessMembershipUpdates()
    {
        // Ensure all child tasks are completed before exiting, tracking them here.
        List<Task> tasks = [];

        try
        {
            var previousUpdate = ClusterMembershipSnapshot.Default;
            while (!_shutdownCts.IsCancellationRequested)
            {
                try
                {
                    await foreach (var update in _clusterMembershipService.MembershipUpdates.WithCancellation(_shutdownCts.Token))
                    {
                        var changes = update.CreateUpdate(previousUpdate);
                        
                        foreach (var change in changes.Changes)
                        {
                            if (change.Status == SiloStatus.Dead)
                            {
                                OnSiloRemovedFromCluster(change);
                            }
                        }

                        var current = new DirectoryMembershipSnapshot(update);
                        if (!current.Contains(_id) && _view.Contains(_id))
                        {
                            // Record how much of the ring we own when we lose ownership.
                            // This allow us to wait for graceful hand-off.
                            _finalView = _view;
                        }

                        // It is important that this method is synchronous, to ensure that updates are atomic.
                        _logger.LogInformation("Updating view from '{PreviousVersion}' to '{Version}'.", previousUpdate.Version, update.Version);
                        ProcessMembershipUpdate(tasks, current);
                        tasks.RemoveAll(task => task.IsCompleted);

                        _logger.LogInformation("Updated view from '{PreviousVersion}' to '{Version}'.", previousUpdate.Version, update.Version);
                        _viewUpdates.Publish(current);
                        previousUpdate = update;
                    }
                }
                catch (Exception exception)
                {
                    if (!_shutdownCts.IsCancellationRequested)
                    {
                        _logger.LogError(exception, "Error processing membership updates.");
                    }
                }
            }

            await Task.WhenAll(tasks).SuppressThrowing();
        }
        finally
        {
            _viewUpdates.Dispose();
        }
    }

    private void OnSiloRemovedFromCluster(ClusterMember change)
    {
        var toRemove = new List<GrainAddress>();
        foreach (var entry in _directory)
        {
            if (change.SiloAddress.Equals(entry.Value.SiloAddress))
            {
                toRemove.Add(entry.Value);
            }
        }

        if (toRemove.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Deleting '{Count}' entries located on now-defunct silo '{SiloAddress}'.", toRemove.Count, change.SiloAddress);
            }

            foreach (var grainAddress in toRemove)
            {
                UnregisterCore(grainAddress);
            }
        }

        RemoveSnapshotTransferPartner(change.SiloAddress, rangeVersion: null);
    }

    private void ProcessMembershipUpdate(List<Task> tasks, DirectoryMembershipSnapshot current)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Observed membership version '{Version}'.", current.Version);
        }

        var previous = _view;
        _view = current;

        var previousRange = previous.GetRingRange(_id);
        var currentRange = current.GetRingRange(_id);

        if (!previousRange.Equals(currentRange))
        {
            // Snapshot & remove everything not in the current range.
            // The new owner will have the opportunity to retrieve the snapshot as they take ownership.
            foreach (var removedRange in currentRange.GetRemovals(previousRange))
            {
                List<GrainAddress>? removedAddresses = [];
                HashSet<SiloAddress> transferPartners = [];
                for (var i = 0; i < current.Ranges.Length; ++i)
                {
                    if (current.Ranges[i].Overlaps(removedRange))
                    {
                        var owner = current.Members[i];
                        Debug.Assert(!_id.Equals(owner));
                        transferPartners.Add(owner);
                        continue;
                    }
                }

                RemoveRange(removedRange, removedAddresses);
                if (transferPartners.Count > 0)
                {
                    _partitionSnapshots.Add(new PartitionSnapshotState(previous.Version, removedAddresses, transferPartners, removedRange));
                }
            }
        }

        foreach (var addedRange in currentRange.GetAdditions(previousRange))
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Accepting ownership of range '{Range}' (current: '{Current}', previous: '{Previous}').", addedRange, currentRange, previousRange);
            }

            // Wedge this range and transfer state from the previous owner.
            // If the predecessor becomes unavailable or membership advances quickly, we will declare data loss and un-wedge the range.
            tasks.Add(TransferOwnershipAsync(previous, current.Version, addedRange));
        }
    }

    private async Task TransferOwnershipAsync(DirectoryMembershipSnapshot previous, MembershipVersion currentVersion, RingRange addedRange)
    {
        // Yield back to the caller immediately after wedging the range.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRanges.Add((addedRange, currentVersion, tcs.Task));
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);

        var recovered = false;
        var stopwatch = CoarseStopwatch.StartNew();
        try
        {
            var tasks = new List<Task<bool>>();
            bool success;

            // The view change is contiguous if the new version is exactly one greater than the previous version.
            // If not, we have missed some updates, so we must declare a potential data loss event.
            var isContiguous = currentVersion.Value == previous.Version.Value + 1;
            if (isContiguous)
            {

                for (var i = 0; i < previous.Ranges.Length; i++)
                {
                    var previousRange = previous.Ranges[i];
                    if (!previousRange.Overlaps(addedRange))
                    {
                        continue;
                    }

                    var previousOwner = previous.Members[i];
                    var previousVersion = previous.Version;
                    tasks.Add(TransferRangeFromPreviousOwner(currentVersion, addedRange, previousOwner, previousVersion));
                }

                await Task.WhenAll(tasks).WaitAsync(_shutdownCts.Token).SuppressThrowing();
                if (_shutdownCts.IsCancellationRequested)
                {
                    return;
                }

                success = tasks.All(t => t.Result);
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Non-contiguous view change detected: '{PreviousVersion}' to '{CurrentVersion}'. Bumping data loss version for range '{Range}'.",
                        previous.Version,
                        currentVersion,
                        addedRange);
                }

                success = false;
            }

            if (!success)
            {
                _logger.LogInformation("Recovering activations from range '{Range}' at version '{Version}'.", addedRange, currentVersion);
                await RecoverPartitionRange(currentVersion, addedRange);
                recovered = true;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Completed transferring entries for range '{Range}'.", addedRange);
            }
        }
        finally
        {
            tcs.SetResult();

            // Un-wedge the range whether it was successfully transferred or not.
            // If it was not successfully transferred, data loss will have been declared.
            _pendingRanges.Remove((addedRange, currentVersion, tcs.Task));

            _logger.LogInformation("Transfer of range '{Range}' at version '{Version}' took {Elapsed}ms.{Recovered}", addedRange, currentVersion, stopwatch.ElapsedMilliseconds, recovered ? " Recovered" : "");
        }
    }

    private async Task<bool> TransferRangeFromPreviousOwner(MembershipVersion currentVersion, RingRange addedRange, SiloAddress previousOwner, MembershipVersion previousVersion)
    {
        try
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Requesting entries for range '{Range}' from '{PreviousOwner}' at version '{PreviousVersion}'.", addedRange, previousOwner, previousVersion);
            }

            var replica = GetReplica(previousOwner);

            // Alternatively, the previous owner could push the snapshot. The pull-based approach is used here because it is simpler.
            var snapshot = await replica.GetPartitionSnapshotAsync(currentVersion, previousVersion, addedRange).AsTask().WaitAsync(_shutdownCts.Token);

            if (snapshot is null)
            {
                _logger.LogWarning("Expected a valid snapshot from previous owner '{PreviousOwner}' for part of range '{Range}', but found none.", previousOwner, addedRange);
                return false;
            }

            // The acknowledgement step lets the previous owner know that the snapshot has been received so that it can proceed.
            // This does not need to be awaited, since it is only a notification, but we should 
            replica.AcknowledgeSnapshotTransferAsync(_id, previousVersion).AsTask().Ignore();

            // Wait for previous versions to be un-wedged before proceeding.
            await WaitForRange(addedRange, previousVersion);

            // Incorporate the values into the grain directory.
            foreach (var entry in snapshot.GrainAddresses)
            {
                AssertOwnership(entry.GrainId);
                _directory[entry.GrainId] = entry;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Transferred '{Count}' entries for range '{Range}' from '{PreviousOwner}'.", snapshot.GrainAddresses.Count, addedRange, previousOwner);
            }

            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error transferring ownership of range {Range}.", addedRange);
            return false;
        }
    }

    private async Task RecoverPartitionRange(MembershipVersion viewNumber, RingRange addedRange)
    {
        var tasks = new List<Task<List<GrainAddress>>>();

        // Membership is guaranteed to be newer than the current view.
        var clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
        Debug.Assert(clusterMembershipSnapshot.Version >= viewNumber);
        var members = clusterMembershipSnapshot.Members.Values.ToList();
        foreach (var member in members)
        {
            if (member.Status is not SiloStatus.Active or SiloStatus.Joining)
            {
                continue;
            }

            tasks.Add(GetRegisteredActivations(addedRange, member.SiloAddress));
        }

        await Task.WhenAll(tasks).WaitAsync(_shutdownCts.Token).SuppressThrowing();
        if (_shutdownCts.IsCancellationRequested)
        {
            return;
        }

        for (var i = 0; i < tasks.Count; ++i)
        {
            var activations = await tasks[i];
            foreach (var entry in activations)
            {
                AssertOwnership(entry.GrainId);
                _directory[entry.GrainId] = entry;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Recovered '{Count}' entries from silo '{SiloAddress}' for range '{Range}' at version '{Version}'.", activations.Count, members[i], addedRange, viewNumber);
            }
        }

        async Task<List<GrainAddress>> GetRegisteredActivations(RingRange range, SiloAddress siloAddress)
        {
            var clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
            while (!_shutdownCts.IsCancellationRequested)
            {
                if (clusterMembershipSnapshot.GetSiloStatus(siloAddress) is not SiloStatus.Active or SiloStatus.Joining)
                {
                    // The silo is no longer active, so there are no activations to recover.
                    return [];
                }

                try
                {
                    var catalog = _grainFactory.GetSystemTarget<ICatalog>(Constants.CatalogType, siloAddress);
                    var result = await catalog.GetRegisteredActivations(range);
                    return result.Value;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting activations in range '{Range}' from silo '{SiloAddress}'.", range, siloAddress);
                    await _clusterMembershipService.Refresh(default);
                    if (_clusterMembershipService.CurrentSnapshot.Version == clusterMembershipSnapshot.Version)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }

                    clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
                }
            }

            return [];
        }
    }

    private void RemoveRange(RingRange range, List<GrainAddress> removedAddresses)
    {
        // Collect all addresses that are not in the owned range.
        foreach (var entry in _directory)
        {
            if (range.Contains(entry.Key))
            {
                removedAddresses.Add(entry.Value);
            }
        }

        // Remove these addresses from the partition.
        if (removedAddresses is not null)
        {
            foreach (var address in removedAddresses)
            {
                _directory.Remove(address.GrainId);
            }
        }
    }

    private sealed record class PartitionSnapshotState(
        MembershipVersion DirectoryMembershipVersion,
        List<GrainAddress> GrainAddresses,
        HashSet<SiloAddress> TransferPartners,
        RingRange Range);
}

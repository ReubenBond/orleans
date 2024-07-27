using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed class GrainDirectoryReplica(
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

    // The most recent directory membership version when a data loss event occurred.
    private MembershipVersion _dataLossVersion = default;

    private Task? _runTask;
    private DirectoryMembershipSnapshot? _finalView;

    public DirectoryMembershipSnapshot CurrentView => _view;

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

    async ValueTask<DirectoryResult<GrainAddress>> IGrainDirectoryReplica.RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration) 
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("RegisterAsync('{Version}', '{Address}', '{ExistingAddress}')", version, address, currentRegistration);
        }

        // Ensure that the current membership version is new enough.
        await WaitForRange(address.GrainId, version);
        if (!IsExpectedView(version))
        {
            return new DirectoryResult<GrainAddress>(null!, _view.Version);
        }

        AssertOwnership(address.GrainId);
        return new DirectoryResult<GrainAddress>(RegisterCore(address, currentRegistration), _view.Version);
    }

    async ValueTask<DirectoryResult<List<GrainAddress>>> IGrainDirectoryReplica.RegisterAsync(MembershipVersion version, List<GrainAddress> addresses) 
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("RegisterAsync('{Version}', '{AddressCount}')", version, addresses.Count);
        }

        var results = new List<GrainAddress>(addresses.Count);
        foreach (var address in addresses)
        {
            // Ensure we can serve the request.
            await WaitForRange(address.GrainId, version);
            if (!IsExpectedView(version))
            {
                return new DirectoryResult<List<GrainAddress>>(null!, _view.Version);
            }

            AssertOwnership(address.GrainId);
            results.Add(RegisterCore(address, null));
        }

        return new DirectoryResult<List<GrainAddress>>(results, _view.Version);
    }

    async ValueTask<DirectoryResult<GrainAddress?>> IGrainDirectoryReplica.LookupAsync(MembershipVersion version, GrainId grainId)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("LookupAsync('{Version}', '{GrainId}')", version, grainId);
        }

        // Ensure we can serve the request.
        await WaitForRange(grainId, version);
        if (!IsExpectedView(version))
        {
            return new DirectoryResult<GrainAddress?>(null, _view.Version);
        }

        AssertOwnership(grainId);
        return new DirectoryResult<GrainAddress?>(LookupCore(grainId), _view.Version);
    }

    async ValueTask<DirectoryResult<List<GrainAddress?>>> IGrainDirectoryReplica.LookupAsync(MembershipVersion version, List<GrainId> grainIds)
    {
        ArgumentNullException.ThrowIfNull(grainIds);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("LookupAsync('{Version}', '{GrainIdCount}')", version, grainIds.Count);
        }

        var results = new List<GrainAddress?>(grainIds.Count);
        foreach (var grainId in grainIds)
        {
            await WaitForRange(grainId, version);
            if (!IsExpectedView(version))
            {
                return new DirectoryResult<List<GrainAddress?>>(null!, _view.Version);
            }

            AssertOwnership(grainId);
            results.Add(LookupCore(grainId));
        }

        return new DirectoryResult<List<GrainAddress?>>(results, _view.Version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryReplica.UnregisterAsync(MembershipVersion version, GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("UnregisterAsync('{Version}', '{Address}')", version, address);
        }

        await WaitForRange(address.GrainId, version);
        if (!IsExpectedView(version))
        {
            return new DirectoryResult<bool>(false, _view.Version);
        }

        AssertOwnership(address.GrainId);
        return new DirectoryResult<bool>(UnregisterCore(address), _view.Version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryReplica.UnregisterAsync(MembershipVersion version, List<GrainAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("UnregisterAsync('{Version}', '{AddressCount}')", version, addresses.Count);
        }

        var result = true;
        foreach (var address in addresses)
        {
            // Ensure we can serve the request.
            await WaitForRange(address.GrainId, version);
            if (!IsExpectedView(version))
            {
                return new DirectoryResult<bool>(false, _view.Version);
            }

            AssertOwnership(address.GrainId);
            result &= UnregisterCore(address);
        }

        return new DirectoryResult<bool>(result, _view.Version);
    }

    async ValueTask<DirectoryResult<GrainDirectoryPartitionSnapshot>> IGrainDirectoryReplica.GetPartitionSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRange range)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("GetPartitionSnapshotAsync('{Version}', '{RangeVersion}', '{Range}')", version, rangeVersion, range);
        }

        // Wait for the range to be un-wedged.
        await RefreshViewAsync(version);
        await WaitForRange(range, rangeVersion);

        foreach (var partitionSnapshot in _partitionSnapshots)
        {
            if (partitionSnapshot.DirectoryMembershipVersion != rangeVersion)
            {
                continue;
            }

            // Only include addresses which are in the requested range.
            List<GrainAddress> partitionAddresses = [];
            foreach (var address in partitionSnapshot.GrainAddresses)
            {
                if (range.Contains(address.GrainId))
                {
                    partitionAddresses.Add(address);
                }
            }

            var rangeSnapshot = new GrainDirectoryPartitionSnapshot(partitionSnapshot.DirectoryMembershipVersion, partitionAddresses, partitionSnapshot.DataLossVersion);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Transferring '{Count}' entries in range '{Range}' from version '{Version}' snapshot.", partitionAddresses.Count, range, rangeVersion);
            }

            return new DirectoryResult<GrainDirectoryPartitionSnapshot>(rangeSnapshot, rangeVersion);
        }

        _logger.LogWarning("Received a request for a snapshot which this replica does not have, version '{Version}', range version '{RangeVersion}', range '{Range}'.", version, rangeVersion, range);
        return new DirectoryResult<GrainDirectoryPartitionSnapshot>(null!, _view.Version);
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
                if (_finalView is { } finalView && finalView.Version == partitionSnapshot.DirectoryMembershipVersion)
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
        Debug.Assert(_view.TryGetOwner(grainId, out var owner));
        Debug.Assert(_id.Equals(owner));
    }

    private bool UnregisterCore(GrainAddress address)
    {
        if (_directory.TryGetValue(address.GrainId, out var existing) && existing.Equals(address))
        {
            return _directory.Remove(address.GrainId);
        }

        return false;
    }

    private GrainAddress? LookupCore(GrainId grainId)
    {
        if (_directory.TryGetValue(grainId, out var existing))
        {
            return existing;
        }

        return null;
    }

    private GrainAddress RegisterCore(GrainAddress newAddress, GrainAddress? existingAddress)
    {
        ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(_directory, newAddress.GrainId, out _);

        // Optimization: if silo is dead, allow the entry to be overwritten.
        if (existing is null || existing.Equals(existingAddress) || IsSiloDead(existing))
        {
            existing = newAddress;
        }

        return existing;

        bool IsSiloDead(GrainAddress existing) => _clusterMembershipService.CurrentSnapshot.GetSiloStatus(existing.SiloAddress) == SiloStatus.Dead;
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

    private bool IsExpectedView(MembershipVersion version) => version == _view.Version;

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
            List<GrainAddress>? removedAddresses = [];
            HashSet<SiloAddress> transferPartners = [];
            foreach (var removedRange in currentRange.GetRemovals(previousRange))
            {
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
            }

            if (transferPartners.Count > 0)
            {
                _partitionSnapshots.Add(new PartitionSnapshotState(previous.Version, removedAddresses, _dataLossVersion, transferPartners));
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
            tasks.Add(TransferOwnershipAsync(previous, current.Version, addedRange, _shutdownCts.Token));
        }
    }

    private async Task TransferOwnershipAsync(DirectoryMembershipSnapshot previous, MembershipVersion currentVersion, RingRange addedRange, CancellationToken cancellationToken)
    {
        // The view change is contiguous if the new version is exactly one greater than the previous version.
        // If not, we have missed some updates, so we must declare a potential data loss event.
        var isContiguous = currentVersion.Value == previous.Version.Value + 1;
        if (!isContiguous)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Non-contiguous view change detected: '{PreviousVersion}' to '{CurrentVersion}'. Bumping data loss version for range '{Range}'.",
                    previous.Version,
                    currentVersion,
                    addedRange);
            }

            BumpDataLossVersion(currentVersion, "non-contiguous view numbers");
            return;
        }

        // Yield back to the caller immediately after wedging the range.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRanges.Add((addedRange, currentVersion, tcs.Task));
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);

        try
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
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Requesting entries for range '{Range}' from '{PreviousOwner}' at version '{PreviousVersion}'.", addedRange, previousOwner, previousVersion);
                }

                var replica = GetReplica(previousOwner);

                // Alternatively, the previous owner could push the snapshot. The pull-based approach is used here because it is simpler.
                var snapshotResult = await replica.GetPartitionSnapshotAsync(currentVersion, previousVersion, addedRange).AsTask().WaitAsync(cancellationToken);

                // The acknowledgement step lets the previous owner know that the snapshot has been received so that it can proceed.
                await replica.AcknowledgeSnapshotTransferAsync(_id, previousVersion).SuppressThrowing();

                // Wait for previous versions to be un-wedged before proceeding.
                await WaitForRange(addedRange, previousVersion);

                // Check that the version has not changed since the call was issued, and that the remote replica validated the version.
                if (!snapshotResult.TryGetResult(previousVersion, out var snapshot))
                {
                    _logger.LogWarning("Expected a valid snapshot from previous owner '{PreviousOwner}' for part of range '{Range}', but found none.", previousOwner, addedRange);
                    BumpDataLossVersion(currentVersion, "result version mismatch");
                    return;
                }

                if (snapshot is null)
                {
                    _logger.LogWarning("Expected a valid snapshot from previous owner '{PreviousOwner}' for part of range '{Range}', but found none.", previousOwner, addedRange);
                    BumpDataLossVersion(currentVersion, "snapshot null");
                    return;
                }

                BumpDataLossVersion(snapshot.DataLossVersion, "inherited");

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
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Completed transferring entries for range '{Range}'.", addedRange);
            }
        }
        catch (Exception exception)
        {
            BumpDataLossVersion(currentVersion, "exception");
            _logger.LogError(exception, "Error transferring ownership of range {Range}.", addedRange);
        }
        finally
        {
            tcs.SetResult();

            // Un-wedge the range whether it was successfully transferred or not.
            // If it was not successfully transferred, data loss will have been declared.
            _pendingRanges.Remove((addedRange, currentVersion, tcs.Task));
        }

        void BumpDataLossVersion(MembershipVersion version, string reason)
        {
            // TODO: Consider finer-grain tracking of data loss version.
            if (_dataLossVersion < version)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Bumping data loss version to '{Version}' due to '{Reason}'.", version, reason);
                }

                _dataLossVersion = version;
            }
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
        MembershipVersion DataLossVersion,
        HashSet<SiloAddress> TransferPartners);
}

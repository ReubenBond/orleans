using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
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
    : SystemTarget(Constants.DirectoryReplicaType, localSiloDetails.SiloAddress, loggerFactory), IGrainDirectoryReplica, IGrainDirectoryReplicaTestHooks, ILifecycleParticipant<ISiloLifecycle>
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

    // Ranges which cannot be served currently, eg because the replica is currently transferring them from a previous owner.
    // Requests in these ranges must wait for the range to become available.
    private readonly HashSet<RingRangeLock> _rangeLocks = [];

    // Ranges which were previously at least partially owned by this replica, but which are pending transfer to a new replica.  
    private readonly List<PartitionSnapshotState> _partitionSnapshots = [];

    // The current directory membership snapshot.
    private DirectoryMembershipSnapshot _view = DirectoryMembershipSnapshot.Default;

    private Task? _runTask;

    public DirectoryMembershipSnapshot View => _view;

    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => _viewUpdates;

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken)
    {
        var stopwatch = ValueStopwatch.StartNew();
        _ = _clusterMembershipService.Refresh(version, cancellationToken);
        if (_view.Version <= version)
        {
            await foreach (var view in _viewUpdates.WithCancellation(cancellationToken))
            {
                if (view.Version >= version)
                {
                    break;
                }
            }

            if (_logger.IsEnabled(LogLevel.Information) && stopwatch.Elapsed.TotalMilliseconds > 50)
            {
                _logger.LogInformation("Refreshed view to version '{Version}' in {Elapsed}ms.", version, stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        return _view;
    }

    async ValueTask<GrainDirectoryPartitionSnapshot?> IGrainDirectoryReplica.GetPartitionSnapshotAsync(MembershipVersion version, RingRange range)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("GetPartitionSnapshotAsync('{RangeVersion}', '{Range}')", version, range);
        }

        // Wait for the range to be unlocked.
        await RefreshViewAsync(version, CancellationToken.None);
        var stopwatch = CoarseStopwatch.StartNew();
        await WaitForRange(range, version, CancellationToken.None);

        if (stopwatch.Elapsed.TotalMilliseconds > 500)
        {
            _logger.LogInformation("Waited for range '{Range}' at version '{Version}' for {Elapsed}ms.", range, version, stopwatch.ElapsedMilliseconds);
        }

        List<GrainAddress> partitionAddresses = [];
        var foundPartition = false;
        foreach (var partitionSnapshot in _partitionSnapshots)
        {
            if (partitionSnapshot.DirectoryMembershipVersion != version)
            {
                continue;
            }

            if (!partitionSnapshot.Range.Intersects(range))
            {
                continue;
            }

            foundPartition = true;

            // Only include addresses which are in the requested range.
            foreach (var address in partitionSnapshot.GrainAddresses)
            {
                if (range.Contains(address.GrainId))
                {
                    partitionAddresses.Add(address);
                }
            }
        }

        if (foundPartition)
        {
            var rangeSnapshot = new GrainDirectoryPartitionSnapshot(version, partitionAddresses);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Transferring '{Count}' entries in range '{Range}' from version '{Version}' snapshot.", partitionAddresses.Count, range, version);
            }

            return rangeSnapshot;
        }

        Debug.Fail($"Received a request for a snapshot which this replica does not have, version '{version}', range '{range}'.");
        return null;
    }

    ValueTask<bool> IGrainDirectoryReplica.AcknowledgeSnapshotTransferAsync(SiloAddress owner, MembershipVersion rangeVersion)
    {
        RemoveSnapshotTransferPartner(owner, rangeVersion);
        return new (true);
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

            var partners = partitionSnapshot.TransferPartners;
            if (partners.Remove(owner) && partners.Count == 0)
            {
                _partitionSnapshots.RemoveAt(i);
                --i;

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Removing version '{Version}' snapshot. Current snapshots: [{CurrentSnapshots}].", partitionSnapshot.DirectoryMembershipVersion, string.Join(", ", _partitionSnapshots.Select(s => s.DirectoryMembershipVersion)));
                }

                // If shutdown has been requested and there are no more pending snapshots, signal completion.
                if (_shutdownCts.IsCancellationRequested && _partitionSnapshots.Count == 0)
                {
                    _shutdownTcs.TrySetResult();
                }
            }
        }
    }

    [Conditional("DEBUG")]
    private void DebugAssertOwnership(GrainId grainId) => DebugAssertOwnership(_view, grainId);

    [Conditional("DEBUG")]
    private void DebugAssertOwnership(DirectoryMembershipSnapshot view, GrainId grainId)
    {
        if (!view.TryGetOwner(grainId, out var owner))
        {
            Debug.Fail($"Could not find owner for grain grain '{grainId}' in view '{view}'.");
        }

        if (!_id.Equals(owner))
        {
            Debug.Fail($"'{_id}' expected to be the owner of grain '{grainId}', but the owner is '{owner}'.");
        }
    }

    private ValueTask WaitForRange(GrainId grainId, MembershipVersion version, CancellationToken cancellationToken) => WaitForRange(RingRange.FromPoint(grainId.GetUniformHashCode()), version, cancellationToken);

    private async ValueTask WaitForRange(RingRange range, MembershipVersion version, CancellationToken cancellationToken)
    {
        if (_view.Version < version)
        {
            await RefreshViewAsync(version, cancellationToken);
        }

        while (TryGetOverlappingRangeLock(range, version, out var completion))
        {
            await completion.WaitAsync(cancellationToken);
        }

        bool TryGetOverlappingRangeLock(RingRange range, MembershipVersion version, [NotNullWhen(true)] out Task? completion)
        {
            foreach (var rangeLock in _rangeLocks)
            {
                if (rangeLock.Version <= version && range.Intersects(rangeLock.Range))
                {
                    completion = rangeLock.ReleaseTask;
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

    private async Task OnRuntimeInitializeStop(CancellationToken cancellationToken)
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
        try
        {
            // Ensure all child tasks are completed before exiting, tracking them here.
            List<Task> tasks = [];
            var previousUpdate = ClusterMembershipSnapshot.Default;
            while (!_shutdownCts.IsCancellationRequested)
            {
                try
                {
                    var previousRanges = _view.GetRanges(_id);
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

                        // It is important that this method is synchronous, to ensure that updates are atomic.
                        var currentRanges = current.GetRanges(_id);
                        var deltaSize = currentRanges.SizePercent - previousRanges.SizePercent;
                        var meanSizePercent = current.Members.Length > 0 ? 100.0 / current.Members.Length : 0f;
                        var deviationFromMean = Math.Abs(meanSizePercent - currentRanges.SizePercent);
                        _logger.LogInformation("Updating view from '{PreviousVersion}' to '{Version}'. Now responsible for '{Range}' (Δ {DeltaPercent:0.00}%. {DeviationFromMean:0.00}% from ideal share).", previousUpdate.Version, update.Version, currentRanges, deltaSize, deviationFromMean);
                        ProcessMembershipUpdate(tasks, current);
                        tasks.RemoveAll(task => task.IsCompleted);

                        _logger.LogInformation("Updated view from '{PreviousVersion}' to '{Version}'.", previousUpdate.Version, update.Version);
                        _viewUpdates.Publish(current);
                        previousUpdate = update;
                        previousRanges = currentRanges;
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
#if false
                _logger.LogInformation("Deleting '{GrainAddress}' located on now-defunct silo '{SiloAddress}'.", grainAddress, change.SiloAddress);
#endif
                DeregisterCore(grainAddress);
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

        var previousRanges = previous.GetRanges(_id);
        var currentRanges = current.GetRanges(_id);

        var removedRanges = previousRanges.Difference(currentRanges);
        var addedRanges = currentRanges.Difference(previousRanges);

        Debug.Assert(currentRanges.Size == previousRanges.Size + addedRanges.Size - removedRanges.Size);
        Debug.Assert(!removedRanges.Intersects(addedRanges));
        Debug.Assert(!removedRanges.Intersects(currentRanges));
        Debug.Assert(removedRanges.IsEmpty || removedRanges.Intersects(previousRanges));
        Debug.Assert(!addedRanges.Intersects(removedRanges));
        Debug.Assert(addedRanges.IsEmpty || addedRanges.Intersects(currentRanges));
        Debug.Assert(!addedRanges.Intersects(previousRanges));

        if (!removedRanges.IsEmpty)
        {
            tasks.Add(ReleaseRangesAsync(previous, current, removedRanges));
        }

        if (!addedRanges.IsEmpty)
        {
            tasks.Add(AcquireRangesAsync(previous, current, addedRanges));
        }
    }

    private async Task ReleaseRangesAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRangeCollection removedRanges)
    {
        // Lock all removed ranges at the current version.
        var tasks = new List<Task>(removedRanges.Ranges.Length);
        foreach (var range in removedRanges)
        {
            tasks.Add(SnapshotRange(previous, current, range));
        }

        await Task.WhenAll(tasks);

        async Task SnapshotRange(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRange removedRange)
        {
            using var rangeLock = RingRangeLock.Create(this, removedRange, current.Version);

            // Snapshot & remove everything not in the current range.
            // The new owner will have the opportunity to retrieve the snapshot as they take ownership.
            List<GrainAddress> removedAddresses = [];
            HashSet<SiloAddress> transferPartners = [];

            // Wait for the range being removed to become valid.
            await WaitForRange(removedRange, previous.Version, CancellationToken.None);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Relinquishing ownership of range '{Range}'.", removedRange);
            }

            foreach (var (newRange, newOwnerIndex) in current.RangeOwners)
            {
                if (newRange.Intersects(removedRange))
                {
                    var newOwner = current.Members[newOwnerIndex];
                    Debug.Assert(!_id.Equals(newOwner));
                    transferPartners.Add(newOwner);
                }
            }

            // Collect all addresses that are not in the owned range.
            foreach (var entry in _directory)
            {
                if (removedRange.Contains(entry.Key))
                {
                    removedAddresses.Add(entry.Value);
                }
            }

            // Remove these addresses from the partition.
            foreach (var address in removedAddresses)
            {
                if (transferPartners.Count > 0)
                {
                    _logger.LogTrace("Evicting entry '{Address}' to snapshot.", address);
                }

                _directory.Remove(address.GrainId);
            }

            if (transferPartners.Count > 0)
            {
                _partitionSnapshots.Add(new PartitionSnapshotState(current.Version, removedAddresses, transferPartners, rangeLock.Range));
            }
            else
            {
                _logger.LogDebug("Dropping snapshot of range '{Range}' at version '{Version}' since there are no transfer partners.", removedRange, rangeLock.Version);
            }
        }
    }

    private async Task AcquireRangesAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRangeCollection addedRanges)
    {
        var stopwatch = CoarseStopwatch.StartNew();

        // The view change is contiguous if the new version is exactly one greater than the previous version.
        // If not, we have missed some updates, so we must declare a potential data loss event.
        var isContiguous = current.Version.Value == previous.Version.Value + 1;
        var rangeLocks = new List<RingRangeLock>();
        if (isContiguous)
        {
            // Transfer subranges from previous owners.
            var tasks = new List<Task>();

            // For each range, find the member(s) which previously owned that range and request their snapshots.
            // Note that it's possible for one added range to overlap with ranges owned by multiple former members,
            // eg if multiple members begin shutting down simultaneously.
            foreach (var addedRange in addedRanges.Ranges)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Accepting ownership of range '{Range}'.", addedRange);
                }

                foreach (var (previousRange, previousOwnerIndex) in previous.RangeOwners)
                {
                    //foreach (var intersection in previousRange.Intersections(addedRange))
                    if (previousRange.Intersects(addedRange))
                    {
                        var previousOwner = previous.Members[previousOwnerIndex];
                        Debug.Assert(!_id.Equals(previousOwner));
                        
                        // If the transfer is successful, the range lock will be released.
                        // If not, the recovery process will need to release the lock once it has completed.
                        var rangeLock = RingRangeLock.Create(this, addedRange, current.Version);
                        rangeLocks.Add(rangeLock);
                        tasks.Add(TransferSnapshotAsync(previous, current, rangeLock, previousOwner));
                    }
                }
            }

            // Note: there should be no 'await' points before this point in the method.
            // An await before this point would result in ranges not being locked synchronously.
            await Task.WhenAll(tasks).WaitAsync(_shutdownCts.Token).SuppressThrowing();
            if (_shutdownCts.IsCancellationRequested)
            {
                return;
            }
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Non-contiguous view change detected: '{PreviousVersion}' to '{CurrentVersion}'. Performing recovery.",
                    previous.Version,
                    current.Version);
            }

            // Lock all ranges and wait for recovery.
            foreach (var addedRange in addedRanges.Ranges)
            {
                rangeLocks.Add(RingRangeLock.Create(this, addedRange, current.Version));
            }
        }

        var recovered = false;

        // If any locks are still held, perform recovery.
        if (rangeLocks.Any(rangeLock => !rangeLock.ReleaseTask.IsCompletedSuccessfully))
        {
            var remainingRanges = new HashSet<RingRange>();
            foreach (var rangeLock in rangeLocks)
            {
                if (rangeLock.ReleaseTask.IsCompletedSuccessfully)
                {
                    continue;
                }

                remainingRanges.Add(rangeLock.Range);
            }

            // Wait for previous versions to be unlocked before proceeding.
            foreach (var range in remainingRanges)
            {
                await WaitForRange(range, previous.Version, CancellationToken.None);
            }

            await RecoverPartitionRange(current, RingRangeCollection.Create(remainingRanges));

            // Release remaining locks.
            foreach (var rangeLock in rangeLocks)
            {
                rangeLock.Release();
            }

            recovered = true;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Completed transferring entries for range '{Range}' at version '{Version}' took {Elapsed}ms.{Recovered}", addedRanges, current.Version, stopwatch.ElapsedMilliseconds, recovered ? " Recovered" : "");
        }
    }

    private async Task TransferSnapshotAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRangeLock rangeLock, SiloAddress previousOwner)
    {
        var addedRange = rangeLock.Range;
        try
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Requesting entries for range '{Range}' from '{PreviousOwner}' at version '{Version}'.", addedRange, previousOwner, current.Version);
            }

            var replica = GetReplica(previousOwner);

            // Alternatively, the previous owner could push the snapshot. The pull-based approach is used here because it is simpler.
            var snapshot = await replica.GetPartitionSnapshotAsync(current.Version, addedRange).AsTask().WaitAsync(_shutdownCts.Token);

            if (snapshot is null)
            {
                _logger.LogWarning("Expected a valid snapshot from previous owner '{PreviousOwner}' for part of range '{Range}', but found none.", previousOwner, addedRange);
                return;
            }

            // The acknowledgement step lets the previous owner know that the snapshot has been received so that it can proceed.
            InvokeOnClusterMember(
                previousOwner,
                async () => await replica.AcknowledgeSnapshotTransferAsync(_id, current.Version),
                false,
                nameof(IGrainDirectoryReplica.AcknowledgeSnapshotTransferAsync)).Ignore();

            // Wait for previous versions to be unlocked before proceeding.
            await WaitForRange(addedRange, previous.Version, CancellationToken.None);

            // Incorporate the values into the grain directory.
            foreach (var entry in snapshot.GrainAddresses)
            {
                DebugAssertOwnership(current, entry.GrainId);
                
                _logger.LogTrace("Received '{Entry}' via snapshot from '{PreviousOwner}' for version '{Version}'.", entry, previousOwner, current.Version);
                _directory[entry.GrainId] = entry;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Transferred '{Count}' entries for range '{Range}' from '{PreviousOwner}'.", snapshot.GrainAddresses.Count, addedRange, previousOwner);
            }

            // Release the range lock.
            rangeLock.Release();
        }
        catch (Exception exception)
        {
            if (exception is SiloUnavailableException)
            {
                _logger.LogWarning("Remote host became unavailable while transferring ownership of range '{Range}'. Recovery will be performed.", addedRange);
            }
            else
            {
                _logger.LogWarning(exception, "Error transferring ownership of range '{Range}'. Recovery will be performed.", addedRange);
            }
        }
    }

    private async Task RecoverPartitionRange(DirectoryMembershipSnapshot current, RingRangeCollection addedRanges)
    {
        _logger.LogInformation("Recovering activations from ranges '{Range}' at version '{Version}'.", addedRanges, current.Version);

        await foreach (var activations in GetRegisteredActivations(current, addedRanges, isValidation: false))
        {
            foreach (var entry in activations)
            {
                DebugAssertOwnership(current, entry.GrainId);
                _logger.LogTrace("Recovered '{Entry}' for version '{Version}'.", entry, current.Version);
                _directory[entry.GrainId] = entry;
            }
        }

        _logger.LogInformation("Completed recovering activations from ranges '{Range}' at version '{Version}'.", addedRanges, current.Version);
    }

    private async IAsyncEnumerable<List<GrainAddress>> GetRegisteredActivations(DirectoryMembershipSnapshot current, RingRangeCollection ranges, bool isValidation)
    {
        // Membership is guaranteed to be at least as recent as the current view.
        var clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
        Debug.Assert(clusterMembershipSnapshot.Version >= current.Version);

        var tasks = new List<Task<List<GrainAddress>>>();
        foreach (var member in clusterMembershipSnapshot.Members.Values)
        {
            if (member.Status is not (SiloStatus.Active or SiloStatus.Joining or SiloStatus.ShuttingDown))
            {
                continue;
            }

            tasks.Add(GetRegisteredActivationsFromClusterMember(current.Version, ranges, member.SiloAddress, isValidation));
        }

        await Task.WhenAll(tasks).WaitAsync(_shutdownCts.Token).SuppressThrowing();
        if (_shutdownCts.IsCancellationRequested)
        {
            yield break;
        }

        foreach (var task in tasks)
        {
            yield return await task;
        }

        async Task<List<GrainAddress>> GetRegisteredActivationsFromClusterMember(MembershipVersion version, RingRangeCollection ranges, SiloAddress siloAddress, bool isValidation)
        {
            var stopwatch = ValueStopwatch.StartNew();
            var client = _grainFactory.GetSystemTarget<IGrainDirectoryReplicaClient>(Constants.DirectoryReplicaClientType, siloAddress);
            var result = await InvokeOnClusterMember(
                siloAddress,
                async () => await client.GetRegisteredActivations(version, ranges, isValidation),
                new Immutable<List<GrainAddress>>([]),
                nameof(GetRegisteredActivations));

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Recovered '{Count}' entries from silo '{SiloAddress}' for ranges '{Range}' at version '{Version}' in {ElapsedMilliseconds}ms.", result.Value.Count, siloAddress, ranges, version, stopwatch.Elapsed.TotalMilliseconds);
            }

            return result.Value;
        }
    }

    private async Task<T> InvokeOnClusterMember<T>(SiloAddress siloAddress, Func<Task<T>> func, T defaultValue, string operationName)
    {
        var clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
        while (!_shutdownCts.IsCancellationRequested)
        {
            if (clusterMembershipSnapshot.GetSiloStatus(siloAddress) is not (SiloStatus.Active or SiloStatus.Joining or SiloStatus.ShuttingDown))
            {
                break;
            }

            try
            {
                return await func();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invoking operation '{Operation}' on silo '{SiloAddress}'.", operationName, siloAddress);
                await _clusterMembershipService.Refresh(default, CancellationToken.None);
                if (_clusterMembershipService.CurrentSnapshot.Version == clusterMembershipSnapshot.Version)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }

                clusterMembershipSnapshot = _clusterMembershipService.CurrentSnapshot;
            }
        }

        return defaultValue;
    }

    async ValueTask IGrainDirectoryReplicaTestHooks.CheckIntegrityAsync()
    {
        var current = _view;
        await WaitForRange(RingRange.Full, current.Version, CancellationToken.None);
        _logger.LogInformation("Performing integrity check on directory at version '{Version}'.", current.Version);
        using var fullRangeLock = RingRangeLock.Create(this, RingRange.Full, current.Version);
        foreach (var entry in _directory)
        {
            DebugAssertOwnership(entry.Key);
        }

        int missing = 0;
        int mismatched = 0;
        var total = 0;
        await foreach (var activationList in GetRegisteredActivations(current, current.GetRanges(_id), isValidation: true))
        {
            total += activationList.Count;
            foreach (var entry in activationList)
            {
                DebugAssertOwnership(entry.GrainId);
                if (_directory.TryGetValue(entry.GrainId, out var existingEntry))
                {
                    if (!existingEntry.Equals(entry))
                    {
                        ++mismatched;
                        _logger.LogError("Integrity violation: Recovered entry '{RecoveredRecord}' does not match existing entry '{LocalRecord}'.", entry, existingEntry);
                        Debug.Fail($"Integrity violation: Recovered entry '{entry}' does not match existing entry '{existingEntry}'.");
                    }
                }
                else
                {
                    ++missing;
                    _logger.LogError("Integrity violation: Recovered entry '{RecoveredRecord}' not found in directory.", entry);
                    Debug.Fail($"Integrity violation: Recovered entry '{entry}' not found in directory.");
                }
            }
        }

        _logger.LogInformation("Directory integrity check analyzed '{TotalRecordCount}' records, '{MissingRecordCount}' were missing, and '{MismatchedRecordCount}' mismatched.", total, missing, mismatched);
        return;
    }

    private sealed record class PartitionSnapshotState(
        MembershipVersion DirectoryMembershipVersion,
        List<GrainAddress> GrainAddresses,
        HashSet<SiloAddress> TransferPartners,
        RingRange Range);

    private sealed class RingRangeLock : IDisposable
    {
        private readonly GrainDirectoryReplica _source;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private RingRangeLock(
            GrainDirectoryReplica source,
            RingRange range,
            MembershipVersion version)
        {
            Range = range;
            Version = version;
            _source = source;
        }

        public RingRange Range { get; }
        public MembershipVersion Version { get; }
        public Task ReleaseTask => _completion.Task;

        public static RingRangeLock Create(GrainDirectoryReplica source,
            RingRange range,
            MembershipVersion version)
        {
            var result = new RingRangeLock(source, range, version);
            source._rangeLocks.Add(result);
            return result;
        }

        public void Release() => Dispose();
        public void Dispose()
        {
            _completion.TrySetResult();
            _source._rangeLocks.Remove(this);
        }
    }
}

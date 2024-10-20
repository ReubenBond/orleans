using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// Represents a single contiguous partition of the distributed grain directory.
/// </summary>
/// <param name="partitionIndex">The index of this partition on this silo. Each silo hosts a fixed number of dynamically sized partitions.</param>
internal sealed partial class GrainDirectoryReplica(
    int partitionIndex,
    DistributedGrainDirectory owner,
    ILocalSiloDetails localSiloDetails,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    IInternalGrainFactory grainFactory)
    : SystemTarget(CreateGrainId(localSiloDetails.SiloAddress, partitionIndex), localSiloDetails.SiloAddress, loggerFactory), IGrainDirectoryPartition, IGrainDirectoryTestHooks
{
    internal static SystemTargetGrainId CreateGrainId(SiloAddress siloAddress, int partitionIndex) => SystemTargetGrainId.Create(Constants.GrainDirectoryPartition, siloAddress, partitionIndex.ToString(CultureInfo.InvariantCulture));
    private readonly Dictionary<GrainId, GrainAddress> _directory = [];
    private readonly int _partitionIndex = partitionIndex;
    private readonly DistributedGrainDirectory _owner = owner;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IInternalGrainFactory _grainFactory = grainFactory;
    private readonly SiloAddress _id = localSiloDetails.SiloAddress;
    private readonly ILogger<GrainDirectoryReplica> _logger = loggerFactory.CreateLogger<GrainDirectoryReplica>();
    private readonly List<SnapshotRequest> _snapshotRequests = [];
    private readonly List<(SiloAddress SiloAddress, CancellationTokenSource Cancellation, Task Completion)> _pendingClusterMemberOperations = [];
    private readonly HashSet<(SiloAddress SiloAddress, int PartitionIndex, MembershipVersion Version, Task Completion)> _inFlightSnapshots = [];
    private readonly TaskCompletionSource _shutdownInitiated = new (TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates = new(
        DirectoryMembershipSnapshot.Default,
        (previous, proposed) => proposed.Version >= previous.Version,
        _ => { });

    // Ranges which cannot be served currently, eg because the replica is currently transferring them from a previous owner.
    // Requests in these ranges must wait for the range to become available.
    private readonly List<(RingRange Range, MembershipVersion Version, TaskCompletionSource Completion)> _rangeLocks = [];

    // Tracked for diagnostic purposes only.
    private readonly List<Task> _viewChangeTasks = [];

    private CancellationToken ShutdownToken => _owner.OnStoppedToken;

    private RingRange _currentRange;

    // The current directory membership snapshot.
    public DirectoryMembershipSnapshot CurrentView { get; private set; } = DirectoryMembershipSnapshot.Default;

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken)
    {
        _ = _owner.RefreshViewAsync(version, cancellationToken);
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

    [Conditional("DEBUG")]
    private void DebugAssertOwnership(GrainId grainId) => DebugAssertOwnership(CurrentView, grainId);

    [Conditional("DEBUG")]
    private void DebugAssertOwnership(DirectoryMembershipSnapshot view, GrainId grainId)
    {
        if (!view.TryGetOwner(grainId, out var owner, out var partitionReference))
        {
            Debug.Fail($"Could not find owner for grain grain '{grainId}' in view '{view}'.");
        }

        if (!_id.Equals(owner))
        {
            Debug.Fail($"'{_id}' expected to be the owner of grain '{grainId}', but the owner is '{owner}'.");
        }

        if (!GrainId.Equals(partitionReference.GetGrainId()))
        {
            Debug.Fail($"'{GrainId}' expected to be the owner of grain '{grainId}', but the owner is '{partitionReference.GetGrainId()}'.");
        }
    }

    private bool IsOwner(DirectoryMembershipSnapshot view, GrainId grainId) => view.TryGetOwner(grainId, out _, out var partitionReference) && GrainId.Equals(partitionReference.GetGrainId());

    private ValueTask WaitForRange(GrainId grainId, MembershipVersion version) => WaitForRange(RingRange.FromPoint(grainId.GetUniformHashCode()), version);

    private ValueTask WaitForRange(RingRange range, MembershipVersion version)
    {
        GrainRuntime.CheckRuntimeContext(this);
        Task? completion = null;
        if (CurrentView.Version < version || TryGetIntersectingLock(range, version, out completion))
        {
            return WaitForRangeCore(range, version, completion);
        }

        return ValueTask.CompletedTask;

        bool TryGetIntersectingLock(RingRange range, MembershipVersion version, [NotNullWhen(true)] out Task? completion)
        {
            foreach (var rangeLock in _rangeLocks)
            {
                if (rangeLock.Version <= version && range.Intersects(rangeLock.Range))
                {
                    completion = rangeLock.Completion.Task;
                    return true;
                }
            }

            completion = null;
            return false;
        }

        async ValueTask WaitForRangeCore(RingRange range, MembershipVersion version, Task? task)
        {
            if (task is not null)
            {
                await task;
            }

            if (CurrentView.Version < version)
            {
                await RefreshViewAsync(version, ShutdownToken);
            }

            while (TryGetIntersectingLock(range, version, out var completion))
            {
                await completion.WaitAsync(ShutdownToken);
            }
        }
    }

    public IGrainDirectoryPartition GetReplicaReference(SiloAddress address, int partitionIndex) => _grainFactory.GetSystemTarget<IGrainDirectoryPartition>(CreateGrainId(address, partitionIndex).GrainId);

    internal async Task OnShuttingDown(CancellationToken token)
    {
        await this.RunOrQueueTask(async () =>
        {
            await _shutdownInitiated.Task.WaitAsync(token);
           
            var duration = ValueStopwatch.StartNew();
            _logger.LogInformation("Shutting down grain directory partition {PartitionId}.", _partitionIndex);  
            var pendingTasks = _inFlightSnapshots.Select(op => op.Completion).ToList();
            await Task.WhenAll(pendingTasks).WaitAsync(token).SuppressThrowing();

            if (token.IsCancellationRequested || duration.Elapsed > TimeSpan.FromSeconds(1))
            {
                //var state = string.Join(", ", _partitionSnapshots);
                var cluster = _owner.ClusterMembershipSnapshot.ToString();
                _logger.LogWarning("Shutting down grain directory partition {PartitionId} took {Elapsed}. CancellationRequested? '{CancellationRequested}'. Membership: {Cluster}", _partitionIndex, duration.Elapsed, token.IsCancellationRequested, cluster);
            }
            else
            {
                _logger.LogInformation("Shutting down grain directory partition {PartitionId} took {Elapsed}.", _partitionIndex, duration.Elapsed);
            }
        });
    }

    internal Task OnSiloRemovedFromClusterAsync(ClusterMember change) =>
        this.QueueAction(
            static state => state.Self.OnSiloRemovedFromCluster(state.Change),
            (Self: this, Change: change),
            nameof(OnSiloRemovedFromCluster));

    private void OnSiloRemovedFromCluster(ClusterMember change)
    {
        GrainRuntime.CheckRuntimeContext(this);
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
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Deleting '{Count}' entries located on now-defunct silo '{SiloAddress}'.", toRemove.Count, change.SiloAddress);
            }

            foreach (var grainAddress in toRemove)
            {
#if false
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Deleting '{GrainAddress}' located on now-defunct silo '{SiloAddress}'.", grainAddress, change.SiloAddress);
                }
#endif
                DeregisterCore(grainAddress);
            }
        }

        foreach (var pendingOperation in _pendingClusterMemberOperations)
        {
            if (pendingOperation.SiloAddress.Equals(change.SiloAddress))
            {
                pendingOperation.Cancellation.Cancel();
            }
        }

        foreach (var snapshotRequest in _snapshotRequests)
        {
            if (snapshotRequest.PreviousOwner.Equals(change.SiloAddress))
            {
                snapshotRequest.Completion.TrySetException(new SiloUnavailableException($"The snapshot creator '{change.SiloAddress}' is no longer available."));
            }
        }

        _snapshotRequests.RemoveAll(s => s.Completion.Task.IsCompleted);
    }

    internal Task ProcessMembershipUpdateAsync(DirectoryMembershipSnapshot current) =>
        this.QueueAction(
            static state => state.Self.ProcessMembershipUpdate(state.Current),
            (Self: this, Current: current),
            nameof(ProcessMembershipUpdate));

    private void ProcessMembershipUpdate(DirectoryMembershipSnapshot current)
    {
        GrainRuntime.CheckRuntimeContext(this);

        _viewChangeTasks.RemoveAll(task => task.IsCompleted);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Observed membership version '{Version}'.", current.Version);
        }

        var previous = CurrentView;
        CurrentView = current;

        var previousRange = previous.GetRange(_id, _partitionIndex);
        _currentRange = current.GetRange(_id, _partitionIndex);

        // It is important that this method is synchronous, to ensure that updates are atomic.
        var removedRange = previousRange.Difference(_currentRange).SingleOrDefault();
        var addedRange = _currentRange.Difference(previousRange).SingleOrDefault();

#if DEBUG
        Debug.Assert(addedRange.IsEmpty ^ removedRange.IsEmpty || addedRange.IsEmpty && removedRange.IsEmpty); // Either the range grew or it shrank, but not both.
        Debug.Assert(previousRange.Difference(_currentRange).Count() < 2);
        Debug.Assert(_currentRange.Difference(previousRange).Count() < 2);
        Debug.Assert(_currentRange.Size == previousRange.Size + addedRange.Size - removedRange.Size);
        Debug.Assert(!removedRange.Intersects(addedRange));
        Debug.Assert(!removedRange.Intersects(_currentRange));
        Debug.Assert(removedRange.IsEmpty || removedRange.Intersects(previousRange));
        Debug.Assert(!addedRange.Intersects(removedRange));
        Debug.Assert(addedRange.IsEmpty || addedRange.Intersects(_currentRange));
        Debug.Assert(!addedRange.Intersects(previousRange));
        Debug.Assert(previousRange.IsEmpty || _currentRange.IsEmpty || previousRange.Start == _currentRange.Start);
#endif

        if (!removedRange.IsEmpty)
        {
            _viewChangeTasks.Add(ReleaseRangeAsync(previous, current, removedRange));
        }

        if (!addedRange.IsEmpty)
        {
            _viewChangeTasks.Add(AcquireRangeAsync(previous, current, addedRange));
        }

        _viewUpdates.Publish(current);
        if (current.ClusterMembershipSnapshot.GetSiloStatus(_id).IsTerminating())
        {
            _shutdownInitiated.TrySetResult();
        }
    }

    private async Task ReleaseRangeAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRange removedRange)
    {
        GrainRuntime.CheckRuntimeContext(this);
        var (tcs, sw) = LockRange(removedRange, current.Version);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Relinquishing ownership of range '{Range}' version '{Version}'.", removedRange, previous.Version);
        }

        try
        {
            // Snapshot & remove everything not in the current range.
            // The new owner will have the opportunity to retrieve the snapshot as they take ownership.
            List<GrainAddress> removedAddresses = [];
            HashSet<(SiloAddress SiloAddress, int PartitionIndex)> transferPartners = [];

            // Wait for the range being removed to become valid.
            await WaitForRange(removedRange, previous.Version);

            GrainRuntime.CheckRuntimeContext(this);

            foreach (var (range, ownerIndex, partitionIndex) in current.RangeOwners)
            {
                if (range.Intersects(removedRange))
                {
                    var owner = current.Members[ownerIndex];
                    Debug.Assert(!_id.Equals(owner));
                    transferPartners.Add((owner, partitionIndex));
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
                if (_logger.IsEnabled(LogLevel.Trace) && transferPartners.Count > 0)
                {
                    _logger.LogTrace("Evicting entry '{Address}' to snapshot.", address);
                }

                _directory.Remove(address.GrainId);
            }

            // TODO: avoid scanning removedAddresses multiple times!!!!!
            // TODO: avoid scanning removedAddresses multiple times!!!!!
            // TODO: avoid scanning removedAddresses multiple times!!!!!
            // TODO: avoid scanning removedAddresses multiple times!!!!!
            // TODO: avoid scanning removedAddresses multiple times!!!!!

            if (transferPartners.Count > 0)
            {
                foreach (var transferPartner in transferPartners)
                {
                    var replica = GetReplicaReference(transferPartner.SiloAddress, transferPartner.PartitionIndex);
                    var range = current.GetRange(transferPartner.SiloAddress, transferPartner.PartitionIndex);

                    // Only include addresses which are in the requested range.
                    List<GrainAddress> partitionAddresses = [];
                    foreach (var address in removedAddresses)
                    {
                        if (range.Contains(address.GrainId))
                        {
                            partitionAddresses.Add(address);
                        }
                    }

                    // Note that we transfer snapshots even if they are empty, since an empty snapshot tells the recipient that the range is empty.
                    // Otherwise the would-be recipient would need to perform recovery to be sure that they had a consistent view.
                    var snapshot = new GrainDirectoryPartitionSnapshot(previous.Version, _id, _partitionIndex, partitionAddresses);
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var inFlightSnapshot = (transferPartner.SiloAddress, transferPartner.PartitionIndex, current.Version, tcs.Task);
                    _inFlightSnapshots.Add(inFlightSnapshot);

                    // Do not wait for the transfer to complete here.
                    Transfer().Ignore();
                    async Task Transfer()
                    {
                        try
                        {
                            await InvokeOnClusterMember(transferPartner.SiloAddress, async cancellationToken =>
                            {
                                if (_logger.IsEnabled(LogLevel.Debug))
                                {
                                    _logger.LogDebug("Transferring '{Count}' entries in range '{Range}' from version '{Version}' snapshot.", snapshot.GrainAddresses.Count, range, previous.Version);
                                }

                                await replica.InstallSnapshotAsync(current.Version, snapshot).AsTask().WaitAsync(cancellationToken);
                                return true;
                            },
                            false,
                            nameof(IGrainDirectoryPartition.InstallSnapshotAsync),
                            CancellationToken.None);
                        }
                        finally
                        {
                            tcs.TrySetResult();
                            _inFlightSnapshots.Remove(inFlightSnapshot);
                        }
                    }
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Dropping snapshot for range '{Range}' version '{Version}' since there are no transfer partners.", removedRange, previous.Version);
                }
            }
        }
        finally
        {
            UnlockRange(removedRange, current.Version, tcs, sw.Elapsed, "release");
        }
    }

    private async Task AcquireRangeAsync(DirectoryMembershipSnapshot previous, DirectoryMembershipSnapshot current, RingRange addedRange)
    {
        GrainRuntime.CheckRuntimeContext(this);

        // Suspend the range and transfer state from the previous owners.
        // If the predecessor becomes unavailable or membership advances quickly, we will declare data loss and unlock the range.
        var (tcs, sw) = LockRange(addedRange, current.Version);

        try
        {
            CoarseStopwatch stopwatch = default;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Acquiring range '{Range}' version '{Version}'.", addedRange, current.Version);
                stopwatch = CoarseStopwatch.StartNew();
            }

            // The view change is contiguous if the new version is exactly one greater than the previous version.
            // If not, we have missed some updates, so we must declare a potential data loss event.
            var isContiguous = current.Version.Value == previous.Version.Value + 1;
            bool success;
            if (isContiguous)
            {
                // Transfer subranges from previous owners.
                var tasks = new List<Task<bool>>();
                foreach (var previousOwner in previous.Members)
                {
                    var previousOwnerRanges = previous.GetMemberRangesByPartition(previousOwner);
                    for (var partitionIndex = 0; partitionIndex < previousOwnerRanges.Length; partitionIndex++)
                    {
                        var previousOwnerRange = previousOwnerRanges[partitionIndex];
                        if (previousOwnerRange.Intersects(addedRange))
                        {
                            tasks.Add(AcquireSnapshotAsync(current, addedRange, previousOwner, partitionIndex, previous.Version));
                        }
                    }
                }

                // Note: there should be no 'await' points before this point.
                // An await before this point would result in ranges not being locked synchronously.
                await Task.WhenAll(tasks).WaitAsync(ShutdownToken).SuppressThrowing();
                if (ShutdownToken.IsCancellationRequested)
                {
                    return;
                }

                success = tasks.All(t => t.Result);
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Non-contiguous view change detected: '{PreviousVersion}' to '{CurrentVersion}'. Performing recovery.",
                        previous.Version,
                        current.Version);
                }

                success = false;
            }

            var recovered = false;
            if (!success)
            {
                // Wait for previous versions to be unlocked before proceeding.
                await WaitForRange(addedRange, previous.Version);

                await RecoverPartitionRange(current, addedRange);
                recovered = true;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Completed transferring entries for range '{Range}' at version '{Version}' took {Elapsed}ms.{Recovered}", addedRange, current.Version, stopwatch.ElapsedMilliseconds, recovered ? " Recovered" : "");
            }
        }
        finally
        {
            UnlockRange(addedRange, current.Version, tcs, sw.Elapsed, "acquire");
        }
    }

    private (TaskCompletionSource Lock, ValueStopwatch Stopwatch) LockRange(RingRange range, MembershipVersion version)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rangeLocks.Add((range, version, tcs));
        return (tcs, ValueStopwatch.StartNew());
    }

    private void UnlockRange(RingRange range, MembershipVersion version, TaskCompletionSource tcs, TimeSpan heldDuration, string operationName)
    {
        DirectoryInstruments.RangeLockHeldDuration.Record((long)heldDuration.TotalMilliseconds);
        if (ShutdownToken.IsCancellationRequested)
        {
            // If the replica is stopped, the range is never unlocked and the task is cancelled instead.
            tcs.SetCanceled(ShutdownToken);
        }
        else
        {
            tcs.SetResult();
            _rangeLocks.Remove((range, version, tcs));
        }
    }

    async ValueTask<bool> IGrainDirectoryPartition.RequestSnapshotAsync(MembershipVersion version, SiloAddress siloAddress, int partitionIndex, RingRange range)
    {
        await WaitForRange(range, version);
        var result = _inFlightSnapshots.Any(s => s.SiloAddress.Equals(siloAddress) && s.Version == version && s.PartitionIndex == partitionIndex);
        if (!result)
        {
            _logger.LogWarning("Requested snapshot for range '{Range}' version '{Version}' from '{SiloAddress}' was not found.", range, version, siloAddress);
        }

        return result;
    }

    async ValueTask IGrainDirectoryPartition.InstallSnapshotAsync(MembershipVersion version, GrainDirectoryPartitionSnapshot snapshot)
    {
        // Wait for the wait for the specified version to be seen and acknowledged by this replica.
        if (CurrentView.Version < version)
        {
            await RefreshViewAsync(version, ShutdownToken);
        }

        // Find the snapshot request for the given previous replica and version.
        var index = _snapshotRequests.FindIndex(r => r.PartitionIndex == snapshot.PartitionIndex && r.PreviousVersion == snapshot.DirectoryMembershipVersion && r.PreviousOwner == snapshot.SiloAddress);
        if (index < 0)
        {
            // If the request was not found, it may have been completed or abandoned already.
            return;
        }

        // Complete and remove the request.
        var request = _snapshotRequests[index];
        request.Completion.TrySetResult(snapshot);
        _snapshotRequests.RemoveAt(index);
    }

    private async Task<bool> AcquireSnapshotAsync(DirectoryMembershipSnapshot current, RingRange addedRange, SiloAddress previousOwner, int partitionIndex, MembershipVersion previousVersion)
    {
        try
        {
            var stopwatch = ValueStopwatch.StartNew();
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Requesting entries for ranges '{Range}' from '{PreviousOwner}' at version '{PreviousVersion}'.", addedRange, previousOwner, previousVersion);
            }

            var replica = GetReplicaReference(previousOwner, partitionIndex);

            var request = new SnapshotRequest(previousVersion, previousOwner, partitionIndex);
            _snapshotRequests.Add(request);

            /*
            // Notify the previous owner that a snapshot is being requested.
            // This is to expedite membership update and snapshot transfer at the previous owner.
            RequestAsync().Ignore();
            async Task RequestAsync()
            {
                try
                {
                    var result = await InvokeOnClusterMember(
                        previousOwner,
                        async cancellationToken =>
                        {
                            var requestTask = replica.RequestSnapshotAsync(current.Version, _id, _partitionIndex, addedRange).AsTask().WaitAsync(cancellationToken);
                            var completionTask = await Task.WhenAny(requestTask, request.Completion.Task);
                            if (completionTask == requestTask)
                            {
                                return await requestTask;
                            }

                            return true;
                        },
                        true,
                        nameof(IGrainDirectoryPartition.RequestSnapshotAsync),
                        CancellationToken.None);

                    if (!result)
                    {
                        request.Completion.TrySetException(new OperationCanceledException("The previous owner rejected the snapshot request."));
                    }
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetException(exception);
                }
            }
            */

            // Wait for the snapshot to be received or abandoned.
            var snapshot = await request.Completion.Task.WaitAsync(ShutdownToken);

            if (snapshot is null)
            {
                _logger.LogWarning("Expected a valid snapshot from previous owner '{PreviousOwner}' for part of ranges '{Range}', but found none.", previousOwner, addedRange);
                return false;
            }

            // Wait for previous versions to be unlocked before proceeding.
            await WaitForRange(addedRange, previousVersion);

            // Incorporate the values into the grain directory.
            foreach (var entry in snapshot.GrainAddresses)
            {
                DebugAssertOwnership(current, entry.GrainId);

                _logger.LogTrace("Received '{Entry}' via snapshot from '{PreviousOwner}' for version '{Version}'.", entry, previousOwner, previousVersion);
                _directory[entry.GrainId] = entry;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Transferred '{Count}' entries for range '{Range}' from '{PreviousOwner}'.", snapshot.GrainAddresses.Count, addedRange, previousOwner);
            }

            DirectoryInstruments.SnapshotTransferCount.Add(1);
            DirectoryInstruments.SnapshotTransferDuration.Record((long)stopwatch.Elapsed.TotalMilliseconds);

            return true;
        }
        catch (Exception exception)
        {
            if (exception is SiloUnavailableException)
            {
                _logger.LogWarning("Remote host '{SiloAddress}' became unavailable while transferring ownership of range '{Range}'. Recovery will be performed.", previousOwner, addedRange);
            }
            else
            {
                _logger.LogWarning(exception, "Error transferring ownership of range '{Range}'. Recovery will be performed.", addedRange);
            }

            return false;
        }
    }

    private async Task RecoverPartitionRange(DirectoryMembershipSnapshot current, RingRange addedRange)
    {
        var stopwatch = ValueStopwatch.StartNew();
        GrainRuntime.CheckRuntimeContext(this);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Recovering activations from range '{Range}' at version '{Version}'.", addedRange, current.Version);
        }

        await foreach (var activations in GetRegisteredActivations(current, addedRange, isValidation: false))
        {
            GrainRuntime.CheckRuntimeContext(this);
            foreach (var entry in activations)
            {
                DebugAssertOwnership(current, entry.GrainId);
                _logger.LogTrace("Recovered '{Entry}' for version '{Version}'.", entry, current.Version);
                _directory[entry.GrainId] = entry;
            }
        }

        DirectoryInstruments.RangeRecoveryCount.Add(1);
        DirectoryInstruments.RangeRecoveryDuration.Record((long)stopwatch.Elapsed.TotalMilliseconds);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Completed recovering activations from range '{Range}' at version '{Version}' took '{Elapsed}'.", addedRange, current.Version, stopwatch.Elapsed);
        }
    }

    private async IAsyncEnumerable<List<GrainAddress>> GetRegisteredActivations(DirectoryMembershipSnapshot current, RingRange range, bool isValidation)
    {
        // Membership is guaranteed to be at least as recent as the current view.
        var clusterMembershipSnapshot = _owner.ClusterMembershipSnapshot;
        Debug.Assert(clusterMembershipSnapshot.Version >= current.Version);

        var tasks = new List<Task<List<GrainAddress>>>();
        foreach (var member in clusterMembershipSnapshot.Members.Values)
        {
            if (member.Status is not (SiloStatus.Active or SiloStatus.Joining or SiloStatus.ShuttingDown))
            {
                continue;
            }

            tasks.Add(GetRegisteredActivationsFromClusterMember(current.Version, range, member.SiloAddress, isValidation));
        }

        await Task.WhenAll(tasks).WaitAsync(ShutdownToken).SuppressThrowing();
        if (ShutdownToken.IsCancellationRequested)
        {
            yield break;
        }

        foreach (var task in tasks)
        {
            yield return await task;
        }

        async Task<List<GrainAddress>> GetRegisteredActivationsFromClusterMember(MembershipVersion version, RingRange range, SiloAddress siloAddress, bool isValidation)
        {
            var stopwatch = ValueStopwatch.StartNew();
            var client = _grainFactory.GetSystemTarget<IGrainDirectoryClient>(Constants.GrainDirectory, siloAddress);
            var result = await InvokeOnClusterMember(
                siloAddress,
                async ct => await client.GetRegisteredActivations(version, range, isValidation).AsTask().WaitAsync(ct),
                new Immutable<List<GrainAddress>>([]),
                nameof(GetRegisteredActivations),
                CancellationToken.None);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Recovered '{Count}' entries from silo '{SiloAddress}' for ranges '{Range}' at version '{Version}' in {ElapsedMilliseconds}ms.", result.Value.Count, siloAddress, range, version, stopwatch.Elapsed.TotalMilliseconds);
            }

            return result.Value;
        }
    }

    private async Task<T> InvokeOnClusterMember<T>(SiloAddress siloAddress, Func<CancellationToken, Task<T>> func, T defaultValue, string operationName, CancellationToken cancellationToken)
    {
        GrainRuntime.CheckRuntimeContext(this);
        var clusterMembershipSnapshot = _owner.ClusterMembershipSnapshot;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
        var pendingOperation = (siloAddress, cts, tcs.Task);
        _pendingClusterMemberOperations.Add(pendingOperation);

        try
        {
            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();
                var status = clusterMembershipSnapshot.GetSiloStatus(siloAddress);
                if (status is not (SiloStatus.Active or SiloStatus.Joining or SiloStatus.ShuttingDown))
                {
                    throw new SiloUnavailableException($"Silo '{siloAddress}' is not available.");
                }

                try
                {
                    return await func(cts.Token);
                }
                catch (Exception ex)
                {
                    if (ex is not OrleansMessageRejectionException)
                    {
                        _logger.LogError(ex, "Error invoking operation '{Operation}' on silo '{SiloAddress}'.", operationName, siloAddress);
                    }

                    await _owner.RefreshViewAsync(default, cts.Token);
                    if (_owner.ClusterMembershipSnapshot.Version == clusterMembershipSnapshot.Version)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
                    }

                    clusterMembershipSnapshot = _owner.ClusterMembershipSnapshot;
                }
            }
        }
        catch (Exception exception)
        {
            tcs.TrySetException(exception);
        }
        finally
        {
            tcs.TrySetCanceled(CancellationToken.None);
            _pendingClusterMemberOperations.Remove(pendingOperation);
        }

        cts.Token.ThrowIfCancellationRequested();
        return defaultValue;
    }

    async ValueTask IGrainDirectoryTestHooks.CheckIntegrityAsync()
    {
        GrainRuntime.CheckRuntimeContext(this);
        var current = CurrentView;
        var range = _currentRange;
        Debug.Assert(range.Equals(current.GetRange(_id, _partitionIndex)));

        await WaitForRange(RingRange.Full, current.Version);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rangeLocks.Add((RingRange.Full, current.Version, tcs));
        try
        {
            foreach (var entry in _directory)
            {
                if (!range.Contains(entry.Key))
                {
                    Debug.Fail($"Invariant violated. This host is not the owner of grain '{entry.Key}'.");
                }

                DebugAssertOwnership(current, entry.Key);
            }

            var missing = 0;
            var mismatched = 0;
            var total = 0;
            await foreach (var activationList in GetRegisteredActivations(current, range, isValidation: true))
            {
                total += activationList.Count;
                foreach (var entry in activationList)
                {
                    if (!IsOwner(current, entry.GrainId))
                    {
                        // The view has been refreshed since the request for registered activations was made.
                        if (current.Version <= current.Version)
                        {
                            Debug.Fail("Invariant violated. This host was sent a registration which it should not have been.");
                        }

                        continue;
                    }

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
        }
        finally
        {
            if (ShutdownToken.IsCancellationRequested)
            {
                tcs.SetCanceled(ShutdownToken);
            }
            else
            {
                tcs.SetResult();
            }

            _rangeLocks.Remove((RingRange.Full, current.Version, tcs));
        }
    }

    private sealed class SnapshotRequest(MembershipVersion previousVersion, SiloAddress previousOwner, int partitionIndex)
    {
        public MembershipVersion PreviousVersion { get; } = previousVersion;
        public SiloAddress PreviousOwner { get; } = previousOwner;
        public int PartitionIndex { get; } = partitionIndex;
        public TaskCompletionSource<GrainDirectoryPartitionSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

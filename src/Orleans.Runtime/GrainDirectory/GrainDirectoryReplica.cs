using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed class ReplicatedGrainDirectory(GrainDirectoryReplica localReplica, ILogger<ReplicatedGrainDirectory> logger) : IGrainDirectory
{
    public async Task<GrainAddress?> Lookup(GrainId grainId) => await InvokeAsync(
        grainId,
        static (replica, version, grainId) => replica.LookupAsync(version, grainId),
        grainId);

    public async Task<GrainAddress?> Register(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (replica, version, address) => replica.RegisterAsync(version, address, null),
        address);

    public async Task Unregister(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (replica, version, address) => replica.UnregisterAsync(version, address),
        address);

    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress) => await InvokeAsync(
        address.GrainId,
        static (replica, version, state) => replica.RegisterAsync(version, state.Address, state.PreviousAddress),
        (Address: address, PreviousAddress: previousAddress));

    public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;

    private async Task<TResult> InvokeAsync<TState, TResult>(
        GrainId grainId,
        Func<IGrainDirectoryReplica, MembershipVersion, TState, ValueTask<DirectoryResult<TResult>>> func,
        TState state,
        [CallerArgumentExpression(nameof(func))] string operation = "")
    {
        DirectoryResult<TResult> invokeResult;
        var view = localReplica.CurrentView;
        while (true)
        {
            if (!view.TryGetOwner(grainId, out var owner))
            {
                if (view.Members.Length == 0 && view.Version.Value > 0)
                {
                    return default!;
                }

                view = await localReplica.RefreshViewAsync(new(view.Version.Value + 1));
                continue;
            }

            logger.LogInformation("Invoking '{Operation}' on '{Owner}' for grain '{GrainId}'.", operation, owner, grainId);
            var replica = localReplica.GetReplica(owner);
            invokeResult = await func(replica, view.Version, state);

            if (invokeResult.TryGetResult(view.Version, out var result))
            {
                logger.LogInformation("Invoked '{Operation}' on '{Owner}' for grain '{GrainId}' and received result '{Result}'.", operation, owner, grainId, result);
                return result;
            }
            else
            {
                // Sync with the remote replica.
                view = await localReplica.RefreshViewAsync(invokeResult.Version);
            }
        }
    }
}

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
    private readonly List<(RingRange Range, MembershipVersion Version, Task Completion)> _wedgedRanges = [];

    // Ranges which were previously at least partially owned by this replica, but which are pending transfer to a new replica.  
    private readonly List<RangeSnapshotState> _partitionSnapshots = [];

    // The current directory membership snapshot.
    private DirectoryMembershipSnapshot _view = DirectoryMembershipSnapshot.Default;

    // The most recent directory membership version when a data loss event occurred.
    private MembershipVersion _dataLossVersion = default;

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

        throw new UnreachableException();
    }

    async ValueTask<DirectoryResult<GrainAddress>> IGrainDirectoryReplica.RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration) 
    {
        ArgumentNullException.ThrowIfNull(address);
        _logger.LogInformation("RegisterAsync('{Version}', '{Address}', '{ExistingAddress}')", version, address, currentRegistration);

        // Ensure that the current membership version is new enough.
        if (!await EnsureValidViewAsync(address.GrainId, version))
        {
            return new DirectoryResult<GrainAddress>(null!, _view.Version);
        }

        AssertOwnership(address.GrainId);
        return new DirectoryResult<GrainAddress>(RegisterCore(address, currentRegistration), _view.Version);
    }

    async ValueTask<DirectoryResult<List<GrainAddress>>> IGrainDirectoryReplica.RegisterAsync(MembershipVersion version, List<GrainAddress> addresses) 
    {
        ArgumentNullException.ThrowIfNull(addresses);
        _logger.LogInformation("RegisterAsync('{Version}', '{AddressCount}')", version, addresses.Count);

        // Ensure that the current membership version is new enough.
        if (version != _view.Version)
        {
            await RefreshViewAsync(version);
        }

        var results = new List<GrainAddress>(addresses.Count);
        foreach (var address in addresses)
        {
            // Ensure we can serve the request.
            if (!await EnsureValidViewAsync(address.GrainId, version))
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
        _logger.LogInformation("LookupAsync('{Version}', '{GrainId}')", version, grainId);

        // Ensure we can serve the request.
        if (!await EnsureValidViewAsync(grainId, version))
        {
            return new DirectoryResult<GrainAddress?>(null, _view.Version);
        }

        AssertOwnership(grainId);
        return new DirectoryResult<GrainAddress?>(LookupCore(grainId), _view.Version);
    }

    async ValueTask<DirectoryResult<List<GrainAddress?>>> IGrainDirectoryReplica.LookupAsync(MembershipVersion version, List<GrainId> grainIds)
    {
        ArgumentNullException.ThrowIfNull(grainIds);
        _logger.LogInformation("LookupAsync('{Version}', '{GrainIdCount}')", version, grainIds.Count);

        // Ensure that the current membership version is new enough.
        if (version != _view.Version)
        {
            await RefreshViewAsync(version);
        }

        var results = new List<GrainAddress?>(grainIds.Count);
        foreach (var grainId in grainIds)
        {
            if (!await EnsureValidViewAsync(grainId, version))
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
        _logger.LogInformation("UnregisterAsync('{Version}', '{Address}')", version, address);
        if (!await EnsureValidViewAsync(address.GrainId, version))
        {
            return new DirectoryResult<bool>(false, _view.Version);
        }

        AssertOwnership(address.GrainId);
        return new DirectoryResult<bool>(UnregisterAsyncCore(address), _view.Version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryReplica.UnregisterAsync(MembershipVersion version, List<GrainAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        _logger.LogInformation("UnregisterAsync('{Version}', '{AddressCount}')", version, addresses.Count);

        // Ensure that the current membership version is new enough.
        if (version != _view.Version)
        {
            await RefreshViewAsync(version);
        }

        var result = true;
        foreach (var address in addresses)
        {
            // Ensure we can serve the request.
            if (!await EnsureValidViewAsync(address.GrainId, version))
            {
                return new DirectoryResult<bool>(false, _view.Version);
            }

            AssertOwnership(address.GrainId);
            result &= UnregisterAsyncCore(address);
        }

        return new DirectoryResult<bool>(result, _view.Version);
    }

    async ValueTask<DirectoryResult<GrainDirectoryPartitionSnapshot>> IGrainDirectoryReplica.GetPartitionSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRange range)
    {
        _logger.LogInformation("GetPartitionSnapshotAsync('{Version}', '{RangeVersion}', '{Range}')", version, rangeVersion, range);

        // Ensure that the current membership version is new enough.
        if (version > _view.Version)
        {
            await RefreshViewAsync(version);
        }

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
            return new DirectoryResult<GrainDirectoryPartitionSnapshot>(rangeSnapshot, _view.Version);
        }

        return new DirectoryResult<GrainDirectoryPartitionSnapshot>(null!, _view.Version);
    }

    ValueTask IGrainDirectoryReplica.AcknowledgeSnapshotTransferAsync(MembershipVersion rangeVersion, RingRange range)
    {
        for (var i = 0; i < _partitionSnapshots.Count; ++i)
        {
            var partitionSnapshot = _partitionSnapshots[i];
            if (partitionSnapshot.DirectoryMembershipVersion != rangeVersion)
            {
                continue;
            }

            if (--partitionSnapshot.ExpectedAcknowledgements <= 0)
            {
                _partitionSnapshots.RemoveAt(i);

                // Trigger shutdown completion if the final snapshot has been transferred.
                if (_finalView is { } finalView && finalView.Version == rangeVersion)
                {
                    _shutdownTcs.TrySetResult();
                }

                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    [Conditional("DEBUG")]
    private void AssertOwnership(GrainId grainId)
    {
        Debug.Assert(_view.TryGetOwner(grainId, out var owner));
        Debug.Assert(_id.Equals(owner));
    }

    private bool UnregisterAsyncCore(GrainAddress address)
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
    }

    private bool IsSiloDead(GrainAddress existing) => _clusterMembershipService.CurrentSnapshot.GetSiloStatus(existing.SiloAddress) == SiloStatus.Dead;

    private ValueTask<bool> EnsureValidViewAsync(GrainId grainId, MembershipVersion version)
    {
        Task? completion;
        if (_view.Version < version)
        {
            completion = _clusterMembershipService.Refresh(version).AsTask();
        }
        else
        {
            TryGetWedge(grainId, version, out completion);
        }

        if (completion is not null)
        {
            return WaitForActivationCore(grainId, version, completion);
        }

        return new(IsValid(grainId, version));

        async ValueTask<bool> WaitForActivationCore(GrainId grainId, MembershipVersion version, Task initialCompletion)
        {
            var completion = initialCompletion;

            do
            {
                await completion;
            } while (TryGetWedge(grainId, version, out completion));

            return IsValid(grainId, version);
        }

        bool IsValid(GrainId grainId, MembershipVersion version) => version == _view.Version;// && _view.TryGetOwner(grainId, out var owner) && _id.Equals(owner);
    }

    private bool TryGetWedge(GrainId grainId, MembershipVersion version, [NotNullWhen(true)] out Task? completion)
    {
        foreach (var wedge in _wedgedRanges)
        {
            if (wedge.Version == version && wedge.Range.Contains(grainId))
            {
                completion = wedge.Completion;
                return true;
            }
        }

        completion = null;
        return false;
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
            await _shutdownTcs.Task.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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
            await this.RunOrQueueTask(async () => await task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing));
        }
    }

    private async Task ProcessMembershipUpdates()
    {
        // For debugging purposes, we track background tasks started by this process.
        List<Task> tasks = [];

        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                try
                {
                    await foreach (var update in _clusterMembershipService.MembershipUpdates.WithCancellation(_shutdownCts.Token))
                    {
                        var current = new DirectoryMembershipSnapshot(update);
                        if (!current.Contains(_id) && _view.Contains(_id))
                        {
                            // Record how much of the ring we own when we lose ownership.
                            // This allow us to wait for graceful hand-off.
                            _finalView = _view;
                        }

                        // It is important that this method is synchronous, to ensure that updates are atomic.
                        ProcessMembershipUpdate(tasks, current);

                        tasks.RemoveAll(t => t.IsCompleted);
                        _viewUpdates.Publish(current);
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

            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            _viewUpdates.Dispose();
        }
    }

    private void ProcessMembershipUpdate(List<Task> tasks, DirectoryMembershipSnapshot current)
    {
        _logger.LogInformation("Observed membership version '{Version}'.", current.Version);
        var previous = _view;
        _view = current;

        var previousRange = previous.GetRingRange(_id);
        var currentRange = current.GetRingRange(_id);

        if (!previousRange.Equals(currentRange))
        {
            // Snapshot & remove everything not in the current range.
            // The new owner will have the opportunity to retrieve the snapshot as they take ownership.
            List<GrainAddress>? removedAddresses = [];
            var expectedAcks = 0;
            foreach (var removedRange in currentRange.GetRemovals(previousRange))
            {
                expectedAcks += current.Ranges.Count(r => r.Overlaps(removedRange));
                RemoveRange(removedRange, removedAddresses);
            }

            if (expectedAcks > 0)
            {
                _partitionSnapshots.Add(new RangeSnapshotState(previous.Version, removedAddresses, _dataLossVersion) { ExpectedAcknowledgements = expectedAcks });
            }
        }

        foreach (var addedRange in currentRange.GetAdditions(previousRange))
        {
            _logger.LogInformation("Accepting ownership of range '{Range}' (current: '{Current}', previous: '{Previous}').", addedRange, currentRange, previousRange);

            if (addedRange.SizePercent > 101f / _view.Ranges.Length)
            {
                Console.WriteLine("1) what");
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
            _logger.LogInformation(
                "Non-contiguous view change detected: '{PreviousVersion}' to '{CurrentVersion}'. Bumping data loss version for range '{Range}'.",
                previous.Version,
                currentVersion,
                addedRange);
            BumpDataLossVersion(currentVersion);
            return;
        }

        // Yield back to the caller immediately after wedging the range.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wedgedRanges.Add((addedRange, currentVersion, tcs.Task));
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

                // If the view changes while this is running, abandon the transfer, declaring data loss.
                if (_view.Version != currentVersion)
                {
                    BumpDataLossVersion(currentVersion);
                    return;
                }

                var previousOwner = previous.Members[i];
                var previousVersion = previous.Version;
                _logger.LogInformation("Requesting entries for range '{Range}' from '{PreviousOwner}' at version '{PreviousVersion}'.", addedRange, previousOwner, previousVersion);
                var replica = GetReplica(previousOwner);
                var snapshotResult = await replica.GetPartitionSnapshotAsync(currentVersion, previousVersion, addedRange).AsTask().WaitAsync(cancellationToken);
                await replica.AcknowledgeSnapshotTransferAsync(previousVersion, addedRange).AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                // Check that the version has not changed since the call was issued, and that the remote replica validated the version.
                if (_view.Version != currentVersion || !snapshotResult.TryGetResult(currentVersion, out var snapshot))
                {
                    BumpDataLossVersion(currentVersion);
                    return;
                }

                if (snapshot is null)
                {
                    BumpDataLossVersion(currentVersion);
                    return;
                }

                BumpDataLossVersion(snapshot.DataLossVersion);

                // Incorporate the values into the grain directory.
                foreach (var entry in snapshot.GrainAddresses)
                {
                    AssertOwnership(entry.GrainId);
                    _directory[entry.GrainId] = entry;
                }

                _logger.LogInformation("Transferred {Count} entries for range '{Range}' from '{PreviousOwner}'.", snapshot.GrainAddresses.Count, addedRange, previousOwner);
            }

            _logger.LogInformation("Completed transferring entries for range '{Range}'.", addedRange);
        }
        catch (Exception exception)
        {
            BumpDataLossVersion(currentVersion);
            _logger.LogError(exception, "Error transferring ownership of range {Range}.", addedRange);
        }
        finally
        {
            tcs.SetResult();

            // Un-wedge the range whether it was successfully transferred or not.
            // If it was not successfully transferred, data loss will have been declared.
            _wedgedRanges.Remove((addedRange, currentVersion, tcs.Task));
        }

        void BumpDataLossVersion(MembershipVersion version)
        {
            // TODO: Consider finer-grain tracking of data loss version.
            if (_dataLossVersion < version)
            {
                _logger.LogInformation("Bumping data loss version to '{Version}'.", version);
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

    private sealed class RangeSnapshotState(
        MembershipVersion directoryMembershipVersion,
        List<GrainAddress> grainAddresses,
        MembershipVersion dataLossVersion)
    {
        public MembershipVersion DirectoryMembershipVersion { get; } = directoryMembershipVersion;

        public List<GrainAddress> GrainAddresses { get; } = grainAddresses;

        public MembershipVersion DataLossVersion { get; } = dataLossVersion;

        public int ExpectedAcknowledgements { get; set; }
    }
}

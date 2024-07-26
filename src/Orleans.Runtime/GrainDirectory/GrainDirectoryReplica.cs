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
using Orleans.GrainDirectory;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed class ReplicatedGrainDirectory(GrainDirectoryReplica localReplica) : IGrainDirectory
{
    public async Task<GrainAddress?> Lookup(GrainId grainId) => await InvokeAsync(grainId, (replica, grainId) => replica.LookupAsync(localReplica.CurrentView.Version, grainId), grainId);

    public async Task<GrainAddress?> Register(GrainAddress address) => await InvokeAsync(address.GrainId, (replica, address) => replica.RegisterAsync(localReplica.CurrentView.Version, address, null), address);

    public async Task Unregister(GrainAddress address) => await InvokeAsync(address.GrainId, (replica, address) => replica.UnregisterAsync(localReplica.CurrentView.Version, address), address);

    public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;

    private async Task<TResult> InvokeAsync<TState, TResult>(GrainId grainId, Func<IGrainDirectoryReplica, TState, ValueTask<DirectoryResult<TResult>>> func, TState state)
    {
        DirectoryResult<TResult> invokeResult;
        var view = localReplica.CurrentView;
        while (true)
        {
            SiloAddress? owner;
            if (!view.TryGetOwner(grainId, out owner))
            {
                view = await localReplica.RefreshView(new (view.Version.Value + 1));
                continue;
            }

            invokeResult = await func(localReplica.GetReplica(owner), state);

            if (invokeResult.TryGetResult(view.Version, out var result))
            {
                return result;
            }
            else
            {
                // Sync with the remote replica.
                view = await localReplica.RefreshView(invokeResult.Version);
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
    : SystemTarget(Constants.DirectoryReplicaType, localSiloDetails.SiloAddress, loggerFactory), IGrainDirectoryReplica, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
{
    private readonly Dictionary<GrainId, GrainAddress> _directory = [];
    private readonly ClusterMembershipService _clusterMembershipService = clusterMembershipService;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IInternalGrainFactory _grainFactory = grainFactory;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SiloAddress _id = localSiloDetails.SiloAddress;
    private readonly ILogger<GrainDirectoryReplica> _logger = loggerFactory.CreateLogger<GrainDirectoryReplica>();
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates = new(
        DirectoryMembershipSnapshot.Default,
        (previous, proposed) => proposed.Version > previous.Version,
        _ => { });

    // Ranges which cannot be served yet, eg because the replica is currently transferring them from a previous owner.
    private readonly List<(RingRange Range, MembershipVersion Version, Task Completion)> _wedgedRanges = [];

    // Ranges which were previously owned by this replica, but which are pending transfer to a new replica.
    private readonly List<GrainDirectoryPartitionSnapshot> _partitionSnapshots = [];

    // The current directory membership snapshot.
    private DirectoryMembershipSnapshot _view = DirectoryMembershipSnapshot.Default;

    // The most recent directory membership version when a data loss event occurred.
    private MembershipVersion _dataLossVersion = default;

    private Task? _runTask;

    public DirectoryMembershipSnapshot CurrentView => _view;
    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => _viewUpdates;

    public async ValueTask<DirectoryMembershipSnapshot> RefreshView(MembershipVersion version = default)
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
        // Ensure that the current membership version is new enough.
        if (!await EnsureValidViewAsync(address.GrainId, version))
        {
            return new DirectoryResult<GrainAddress>(null!, _view.Version);
        }

        AssertOwnership(GrainId);
        return new DirectoryResult<GrainAddress>(RegisterCore(address, currentRegistration), _view.Version);
    }

    async ValueTask<DirectoryResult<List<GrainAddress>>> IGrainDirectoryReplica.RegisterAsync(MembershipVersion version, List<GrainAddress> addresses) 
    {
        // Ensure that the current membership version is new enough.
        if (version != _view.Version)
        {
            await RefreshMembershipAsync(version);
        }

        var results = new List<GrainAddress>(addresses.Count);
        foreach (var address in addresses)
        {
            // Ensure we can serve the request.
            if (!await EnsureValidViewAsync(address.GrainId, version))
            {
                return new DirectoryResult<List<GrainAddress>>(null!, _view.Version);
            }

            AssertOwnership(GrainId);
            results.Add(RegisterCore(address, null));
        }

        return new DirectoryResult<List<GrainAddress>>(results, _view.Version);
    }

    async ValueTask<DirectoryResult<GrainAddress?>> IGrainDirectoryReplica.LookupAsync(MembershipVersion version, GrainId grainId)
    {
        // Ensure we can serve the request.
        if (!await EnsureValidViewAsync(grainId, version))
        {
            return new DirectoryResult<GrainAddress?>(null, _view.Version);
        }

        AssertOwnership(GrainId);
        return new DirectoryResult<GrainAddress?>(LookupCore(grainId), _view.Version);
    }

    async ValueTask<DirectoryResult<List<GrainAddress?>>> IGrainDirectoryReplica.LookupAsync(MembershipVersion version, List<GrainId> grainIds)
    {
        // Ensure that the current membership version is new enough.
        if (version != _view.Version)
        {
            await RefreshMembershipAsync(version);
        }

        var results = new List<GrainAddress?>(grainIds.Count);
        foreach (var grainId in grainIds)
        {
            if (!await EnsureValidViewAsync(grainId, version))
            {
                return new DirectoryResult<List<GrainAddress?>>(null!, _view.Version);
            }

            AssertOwnership(GrainId);
            results.Add(LookupCore(grainId));
        }

        return new DirectoryResult<List<GrainAddress?>>(results, _view.Version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryReplica.UnregisterAsync(MembershipVersion version, GrainAddress address)
    {
        if (!await EnsureValidViewAsync(address.GrainId, version))
        {
            return new DirectoryResult<bool>(false, _view.Version);
        }

        AssertOwnership(address.GrainId);
        return new DirectoryResult<bool>(UnregisterAsyncCore(address), _view.Version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryReplica.UnregisterAsync(MembershipVersion version, List<GrainAddress> addresses)
    {
        // Ensure that the current membership version is new enough.
        if (version != _view.Version)
        {
            await RefreshMembershipAsync(version);
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

    async ValueTask<DirectoryResult<GrainDirectoryPartitionSnapshot>> IGrainDirectoryReplica.GetPartitionSnapshotAsync(MembershipVersion version, RingRange range)
    {
        // Ensure that the current membership version is new enough.
        if (version != _view.Version)
        {
            await RefreshMembershipAsync(version);

            if (version != _view.Version)
            {
                return new DirectoryResult<GrainDirectoryPartitionSnapshot>(null!, _view.Version);
            }
        }

        List<GrainAddress> addresses = [];
        MembershipVersion dataLossVersion = default;
        foreach (var partitionSnapshot in _partitionSnapshots)
        {
            if (partitionSnapshot.DirectoryMembershipVersion != version || !partitionSnapshot.Range.Overlaps(range))
            {
                continue;
            }

            foreach (var entry in partitionSnapshot.GrainAddresses)
            {
                if (range.Contains(entry.GrainId))
                {
                    addresses.Add(entry);
                }
            }

            if (partitionSnapshot.DataLossVersion > dataLossVersion)
            {
                dataLossVersion = partitionSnapshot.DataLossVersion;
            }
        }

        var snapshot = new GrainDirectoryPartitionSnapshot(version, addresses, dataLossVersion, range);
        return new DirectoryResult<GrainDirectoryPartitionSnapshot>(snapshot!, _view.Version);
    }

    private async Task RefreshMembershipAsync(MembershipVersion version)
    {
        var first = true;
        while (version > _view.Version)
        {
            await _clusterMembershipService.Refresh(version);

            if (first)
            {
                // TODO: use a signal mechanism instead
                await Task.Delay(TimeSpan.FromMilliseconds(10));
                first = false;
            }
        }
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
            completion = RefreshMembershipAsync(version);
        }
        else
        {
            TryGetWedge(grainId, version, out completion);
        }

        if (completion is not null)
        {
            return WaitForActivationCore(grainId, version, completion);
        }

        return new(_view.Version == version);

        async ValueTask<bool> WaitForActivationCore(GrainId grainId, MembershipVersion version, Task initialCompletion)
        {
            var completion = initialCompletion;

            do
            {
                await completion;
            } while (TryGetWedge(grainId, version, out completion));

            return _view.Version == version;
        }
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

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer) => observer.Subscribe(ServiceLifecycleStage.RuntimeInitialize, this);

    Task ILifecycleObserver.OnStart(CancellationToken cancellationToken)
    {
        var catalog = _serviceProvider.GetRequiredService<Catalog>();
        catalog.RegisterSystemTarget(this);

        using var _ = new ExecutionContextSuppressor();
        WorkItemGroup.QueueAction(() => _runTask = ProcessMembershipUpdates());

        return Task.CompletedTask;
    }

    async Task ILifecycleObserver.OnStop(CancellationToken cancellationToken)
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

        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                await foreach (var update in _clusterMembershipService.MembershipUpdates.WithCancellation(_shutdownCts.Token))
                {
                    _logger.LogInformation("Observed membership version '{Version}'.", update.Version);
                    var previous = _view;
                    var current = new DirectoryMembershipSnapshot(update);
                    _view = current;

                    // The view change is contiguous if the new version is exactly one greater than the previous version.
                    // If not, we have missed some updates, so we must declare a potential data loss event.
                    var isContiguous = current.Version.Value == previous.Version.Value + 1;

                    var previousRange = previous.GetRingRange(_id);
                    var currentRange = current.GetRingRange(_id);
                    foreach (var removedRange in currentRange.GetRemovals(previousRange))
                    {
                        _logger.LogInformation("Snapshotting and removing range '{Range}'.", removedRange);

                        // Snapshot & remove the range.
                        // The new owner will have the opportunity to retrieve the snapshot as they take ownership.
                        var rangeSnapshot = RemoveRange(removedRange);
                        _partitionSnapshots.Add(new GrainDirectoryPartitionSnapshot(previous.Version, rangeSnapshot, _dataLossVersion, removedRange));
                    }

                    if (!isContiguous)
                    {
                        BumpDataLossVersion(current.Version);
                    }
                    else
                    {
                        foreach (var addedRange in currentRange.GetAdditions(previousRange))
                        {
                            _logger.LogInformation("Accepting ownership of range '{Range}'.", addedRange);
                            // Wedge this range and transfer state from the previous owner.
                            // If the predecessor becomes unavailable or membership advances quickly, we will declare data loss and un-wedge the range.
                            tasks.Add(TransferOwnershipAsync(previous, current.Version, addedRange, _shutdownCts.Token));
                        }
                    }

                    tasks.RemoveAll(t => t.IsCompleted);
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

    private async Task TransferOwnershipAsync(DirectoryMembershipSnapshot membershipSnapshot, MembershipVersion version, RingRange range, CancellationToken cancellationToken)
    {
        // Yield back to the caller immediately after wedging the range.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wedgedRanges.Add((range, version, tcs.Task));
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);

        try
        {
            for (var i = 0; i < membershipSnapshot.Ranges.Length; i++)
            {
                // If the view changes while this is running, abandon the transfer, declaring data loss.
                if (_view.Version != version)
                {
                    BumpDataLossVersion(version);
                    return;
                }

                var currentRange = membershipSnapshot.Ranges[i];
                if (!currentRange.Overlaps(range))
                {
                    continue;
                }

                var owner = membershipSnapshot.Members[i];

                _logger.LogInformation("Requesting entries for range '{Range}' from '{PreviousOwner}'.", range, owner);
                var snapshotResult = await GetReplica(owner).GetPartitionSnapshotAsync(version, range).AsTask().WaitAsync(cancellationToken);

                // Check that the version has not changed since the call was issued, and that the remote replica validated the version.
                if (_view.Version != version || !snapshotResult.TryGetResult(version, out var snapshot))
                {
                    BumpDataLossVersion(version);
                    return;
                }

                BumpDataLossVersion(snapshot.DataLossVersion);

                // Incorporate the values into the grain directory.
                foreach (var entry in snapshot.GrainAddresses)
                {
                    AssertOwnership(entry.GrainId);
                    _directory[entry.GrainId] = entry;
                }

                _logger.LogInformation("Transferred {Count} entries for range '{Range}' from '{PreviousOwner}'.", snapshot.GrainAddresses.Count, range, owner);
            }

            _logger.LogInformation("Completed transferring entries for range '{Range}'.", range);
        }
        catch (Exception exception)
        {
            BumpDataLossVersion(version);
            _logger.LogError(exception, "Error transferring ownership of range {Range}.", range);
        }
        finally
        {
            tcs.SetResult();

            // Un-wedge the range whether it was successfully transferred or not.
            // If it was not successfully transferred, data loss will have been declared.
            _wedgedRanges.Remove((range, version, tcs.Task));
        }
    }

    private void BumpDataLossVersion(MembershipVersion version)
    {
        // TODO: Consider finer-grain tracking of data loss version.
        if (_dataLossVersion < version)
        {
            _logger.LogInformation("Bumping data loss version to '{Version}'.", version);
            _dataLossVersion = version;
        }
    }

    private List<GrainAddress> RemoveRange(RingRange removedRange)
    {
        List<GrainAddress> addresses = [];

        // Collect all addresses that are not in the owned range.
        foreach (var entry in _directory)
        {
            if (!removedRange.Contains(entry.Key))
            {
                addresses.Add(entry.Value);
            }
        }

        // Remove these addresses from the partition.
        foreach (var address in addresses)
        {
            _directory.Remove(address.GrainId);
        }

        return addresses;
    }
}

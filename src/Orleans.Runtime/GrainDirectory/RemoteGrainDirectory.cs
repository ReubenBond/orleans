using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Runtime.Scheduler;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed class RemoteGrainDirectory : SystemTarget, IRemoteGrainDirectory
{
    private readonly LocalGrainDirectory router;
    private readonly GrainDirectoryPartition partition;
    private readonly ILogger logger;

    internal RemoteGrainDirectory(LocalGrainDirectory r, GrainType grainType, ILoggerFactory loggerFactory)
        : base(grainType, r.MyAddress, loggerFactory)
    {
        router = r;
        partition = r.DirectoryPartition;
        logger = loggerFactory.CreateLogger($"{typeof(RemoteGrainDirectory).FullName}.CacheValidator");
    }

    public Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress? previousAddress, int hopCount)
    {
        DirectoryInstruments.RegistrationsSingleActRemoteReceived.Add(1);

        return router.RegisterAsync(address, previousAddress, hopCount);
    }

    public Task RegisterMany(List<GrainAddress> addresses)
    {
        if (addresses == null || addresses.Count == 0)
        {
            throw new ArgumentException("Addresses cannot be an empty list or null");
        }

        // validate that this request arrived correctly
        //logger.Assert(ErrorCode.Runtime_Error_100140, silo.Matches(router.MyAddress), "destination address != my address");

        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("RegisterMany Count={Count}", addresses.Count);


        return Task.WhenAll(addresses.Select(addr => router.RegisterAsync(addr, previousAddress: null, 1)));
    }

    public Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount)
    {
        return router.UnregisterAsync(address, cause, hopCount);
    }

    public Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount)
    {
        return router.UnregisterManyAsync(addresses, cause, hopCount);
    }

    public Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount)
    {
        return router.LookupAsync(grainId, hopCount);
    }

    public Task<List<AddressAndTag>> LookUpMany(List<(GrainId GrainId, int Version)> grainAndETagList)
    {
        DirectoryInstruments.ValidationsCacheReceived.Add(1);
        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("LookUpMany for {Count} entries", grainAndETagList.Count);

        var result = new List<AddressAndTag>();

        foreach (var tuple in grainAndETagList)
        {
            int curGen = partition.GetGrainETag(tuple.GrainId);
            if (curGen == tuple.Version || curGen == GrainInfo.NO_ETAG)
            {
                // the grain entry either does not exist in the local partition (curGen = -1) or has not been updated

                result.Add(new(GrainAddress.GetAddress(null, tuple.GrainId, default), curGen));
            }
            else
            {
                // the grain entry has been updated -- fetch and return its current version
                var lookupResult = partition.LookUpActivation(tuple.GrainId);
                // validate that the entry is still in the directory (i.e., it was not removed concurrently)
                if (lookupResult.Address != null)
                {
                    result.Add(lookupResult);
                }
                else
                {
                    result.Add(new(GrainAddress.GetAddress(null, tuple.GrainId, default), GrainInfo.NO_ETAG));
                }
            }
        }

        return Task.FromResult(result);
    }

    public Task AcceptSplitPartition(List<GrainAddress> singleActivations)
    {
        //router.HandoffManager.AcceptExistingRegistrations(singleActivations);
        return Task.CompletedTask;
    }

    public Task<AddressAndTag> RegisterAsync(GrainAddress address, int hopCount = 0) => router.RegisterAsync(address, hopCount);
}

    // SCRATCH CODE FOR CLIENT
    /*
    private async ValueTask<DirectoryResult<bool>> DUMMY_UnregisterAsync(MembershipVersion version, List<GrainAddress> addresses)
    {
        // Ensure that the current membership version is new enough.
        if (version != _view.Version)
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

            return new DirectoryResult<bool>(false, _view.Version);
        }

        // Perform the actions locally, forwarding requests which are 
        Dictionary<SiloAddress, List<GrainAddress>>? forwardedRequests = null;
        foreach (var address in addresses)
        {
            if (!_view.TryGetOwner(address.GrainId, out var owner) || !owner.Equals(_id))
            {
                return new DirectoryResult<bool>(false, _view.Version);
            }

            if (owner.Equals(_id))
            {
                return new DirectoryResult<bool>(UnregisterAsyncCore(address), _view.Version);
            }
            else
            {
                void AddToForwardingList(SiloAddress owner, GrainAddress address)
                {
                    forwardedRequests ??= [];
                    ref var toForward = ref CollectionsMarshal.GetValueRefOrAddDefault(forwardedRequests, owner, out _);
                    toForward ??= [];
                    toForward.Add(address); 
                }

                AddToForwardingList(owner, address);
            }
        }

        // Forward any requests which need to be forwarded.
        if (forwardedRequests is not null)
        {
            var tasks = new List<Task<DirectoryResult<bool>>>(forwardedRequests.Count);
            foreach (var (silo, list) in forwardedRequests)
            {
                var replica = GetReplicaReference(silo);
                tasks.Add(replica.UnregisterAsync(version, list).AsTask());
            }

            await Task.WhenAll(tasks);

            foreach (var task in tasks)
            {
                var result = await task;
                if (result.Version != version)
                {
                    return result;
                }
            }
        }

        return new DirectoryResult<bool>(true, _view.Version);
    }
    */

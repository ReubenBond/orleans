using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Runtime.GrainDirectory;

#nullable enable
namespace Orleans.Runtime;

/// <summary>
/// Per-silo system interface for managing the distributed, partitioned grain-silo-activation directory.
/// </summary>
internal interface IRemoteGrainDirectory : ISystemTarget, IDhtGrainDirectory
{        
    /// <summary>
    /// Records a bunch of new grain activations.
    /// This method should be called only remotely during handoff.
    /// </summary>
    /// <param name="addresses">The addresses of the grains to register</param>
    /// <returns></returns>
    Task RegisterMany(List<GrainAddress> addresses);

    /// <summary>
    /// Fetch the updated information on the given list of grains.
    /// This method should be called only remotely to refresh directory caches.
    /// </summary>
    /// <param name="grainAndETagList">list of grains and generation (version) numbers. The latter denote the versions of 
    /// the lists of activations currently held by the invoker of this method.</param>
    /// <returns>list of tuples holding a grain, generation number of the list of activations, and the list of activations. 
    /// If the generation number of the invoker matches the number of the destination, the list is null. If the destination does not
    /// hold the information on the grain, generation counter -1 is returned (and the list of activations is null)</returns>
    Task<List<AddressAndTag>> LookUpMany(List<(GrainId GrainId, int Version)> grainAndETagList);

    /// <summary>
    /// Registers activations from a split partition with this directory.
    /// </summary>
    /// <param name="singleActivations">The single-activation registrations from the split partition.</param>
    /// <returns></returns>
    Task AcceptSplitPartition(List<GrainAddress> singleActivations);
}

internal interface IGrainDirectoryReplica : ISystemTarget
{        
    ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration);
    ValueTask<DirectoryResult<List<GrainAddress>>> RegisterAsync(MembershipVersion version, List<GrainAddress> addresses);

    ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(MembershipVersion version, GrainId grainId);
    ValueTask<DirectoryResult<List<GrainAddress?>>> LookupAsync(MembershipVersion version, List<GrainId> grainIds);

    ValueTask<DirectoryResult<bool>> UnregisterAsync(MembershipVersion version, GrainAddress address);
    ValueTask<DirectoryResult<bool>> UnregisterAsync(MembershipVersion version, List<GrainAddress> addresses);

    //ValueTask<DirectoryResult<bool>> AcceptPartition(MembershipVersion version, GrainDirectoryPartition addresses);
    ValueTask<DirectoryResult<GrainDirectoryPartitionSnapshot>> GetPartitionSnapshotAsync(MembershipVersion version, RingRange range);
}

[GenerateSerializer]
[Alias("DirectoryResult`1")]
public readonly struct DirectoryResult<T>(T result, MembershipVersion version)
{
    [Id(0)]
    private readonly T _result = result;

    [Id(1)]
    public readonly MembershipVersion Version = version;

    public bool TryGetResult(MembershipVersion version, [NotNullWhen(true)] out T? result)
    {
        if (Version != version)
        {
            result = default;
            return false;
        }

        result = _result!;
        return true;
    }
}

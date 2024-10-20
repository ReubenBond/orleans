using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

[Alias("IGrainDirectoryReplica")]
internal interface IGrainDirectoryPartition : ISystemTarget
{
    [Alias("RegisterAsync")]
    ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration);

    [Alias("LookupAsync")]
    ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(MembershipVersion version, GrainId grainId);

    [Alias("DeregisterAsync")]
    ValueTask<DirectoryResult<bool>> DeregisterAsync(MembershipVersion version, GrainAddress address);

    [Alias("RequestSnapshotAsync")]
    ValueTask<bool> RequestSnapshotAsync(MembershipVersion version, SiloAddress siloAddress, int partitionIndex, RingRange range);

    // Called to transfer a snapshot to the new owner of the range.
    [Alias("InstallSnapshotAsync")]
    ValueTask InstallSnapshotAsync(MembershipVersion version, GrainDirectoryPartitionSnapshot snapshot);
}

[Alias("IGrainDirectoryReplicaClient")]
internal interface IGrainDirectoryClient : ISystemTarget
{
    [Alias("GetRegisteredActivations")]
    ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(MembershipVersion membershipVersion, RingRange range, bool isValidation);
}

[Alias("IGrainDirectoryReplicaTestHooks")]
internal interface IGrainDirectoryTestHooks : ISystemTarget
{
    [Alias("CheckIntegrityAsync")]
    ValueTask CheckIntegrityAsync();
}

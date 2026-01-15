using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

[Alias("IGrainDirectoryPartition")]
internal interface IGrainDirectoryPartition : ISystemTarget
{
    [Alias("RegisterAsync")]
    ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration, CancellationToken cancellationToken);

    [Alias("LookupAsync")]
    ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(MembershipVersion version, GrainId grainId, CancellationToken cancellationToken);

    [Alias("DeregisterAsync")]
    ValueTask<DirectoryResult<bool>> DeregisterAsync(MembershipVersion version, GrainAddress address, CancellationToken cancellationToken);

    [Alias("GetSnapshotAsync")]
    ValueTask<GrainDirectoryPartitionSnapshot?> GetSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRange range, CancellationToken cancellationToken);

    [Alias("AcknowledgeSnapshotTransferAsync")]
    ValueTask<bool> AcknowledgeSnapshotTransferAsync(SiloAddress silo, int partitionIndex, MembershipVersion version, CancellationToken cancellationToken);
}

[Alias("IGrainDirectoryClient")]
internal interface IGrainDirectoryClient : ISystemTarget
{
    [Alias("GetRegisteredActivations")]
    ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(MembershipVersion membershipVersion, RingRange range, bool isValidation, CancellationToken cancellationToken);

    [Alias("RecoverRegisteredActivations")]
    ValueTask<Immutable<List<GrainAddress>>> RecoverRegisteredActivations(MembershipVersion membershipVersion, RingRange range, SiloAddress siloAddress, int partitionId, CancellationToken cancellationToken);
}

[Alias("IGrainDirectoryTestHooks")]
internal interface IGrainDirectoryTestHooks : ISystemTarget
{
    [Alias("CheckIntegrityAsync")]
    ValueTask CheckIntegrityAsync(CancellationToken cancellationToken);
}

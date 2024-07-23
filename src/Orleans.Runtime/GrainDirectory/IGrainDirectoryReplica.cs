using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime.GrainDirectory;

#nullable enable
namespace Orleans.Runtime;

internal interface IGrainDirectoryReplica : ISystemTarget
{        
    ValueTask<DirectoryResult<GrainAddress>> RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration);
    ValueTask<DirectoryResult<GrainAddress?>> LookupAsync(MembershipVersion version, GrainId grainId);
    ValueTask<DirectoryResult<bool>> DeregisterAsync(MembershipVersion version, GrainAddress address);

    ValueTask<DirectoryResult<List<GrainAddress>>> RegisterAsync(MembershipVersion version, [Immutable] List<GrainAddress> addresses);
    ValueTask<DirectoryResult<List<GrainAddress?>>> LookupAsync(MembershipVersion version, [Immutable] List<GrainId> grainIds);
    ValueTask<DirectoryResult<bool>> DeregisterAsync(MembershipVersion version, [Immutable] List<GrainAddress> addresses);

    ValueTask<GrainDirectoryPartitionSnapshot?> GetSnapshotAsync(MembershipVersion version, MembershipVersion rangeVersion, RingRangeCollection ranges);
    ValueTask<bool> AcknowledgeSnapshotTransferAsync(SiloAddress owner, MembershipVersion version);

    ValueTask<BulkDirectoryResponse> ApplyBulk(BulkDirectoryRequest request);
}

[GenerateSerializer, Immutable, Alias("dir.BulkRequest")]
internal sealed class BulkDirectoryRequest
{
    [Id(0)]
    public MembershipVersion Version { get; set; }

    [Id(1)]
    public List<GrainId>? Lookups { get; set; }

    [Id(2)]
    public List<(GrainAddress Address, GrainAddress? CurrentAddress)>? Registrations {get; set; }

    [Id(3)]
    public List<GrainAddress>? Deregistrations {get; set; }
}

[GenerateSerializer, Immutable, Alias("dir.BulkResponse")]
internal sealed class BulkDirectoryResponse
{
    [Id(0)]
    public MembershipVersion Version { get; set; }

    [Id(1)]
    public List<GrainAddress?>? Lookups { get; set; }

    [Id(2)]
    public List<GrainAddress?>? Registrations { get; set; }

    [Id(3)]
    public List<bool?>? Deregistrations { get; set; }
}

internal interface IGrainDirectoryReplicaClient : ISystemTarget
{
    ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(MembershipVersion membershipVersion, RingRangeCollection ranges, bool isValidation);
}

internal interface IGrainDirectoryReplicaTestHooks : ISystemTarget
{
    ValueTask CheckIntegrityAsync();
}

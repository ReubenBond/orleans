using System.Collections.Generic;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

[GenerateSerializer, Alias(nameof(GrainDirectoryPartitionSnapshot)), Immutable]
internal sealed class GrainDirectoryPartitionSnapshot(
    MembershipVersion directoryMembershipVersion,
    SiloAddress siloAddress,
    int partitionIndex,
    List<GrainAddress> grainAddresses)
{
    [Id(0)]
    public MembershipVersion DirectoryMembershipVersion { get; } = directoryMembershipVersion;

    [Id(1)]
    public List<GrainAddress> GrainAddresses { get; } = grainAddresses;

    // The address of the replica that created this snapshot.
    [Id(2)]
    public SiloAddress SiloAddress { get; } = siloAddress;

    // The index of the replica that created this snapshot.
    [Id(3)]
    public int PartitionIndex { get; } = partitionIndex;
}

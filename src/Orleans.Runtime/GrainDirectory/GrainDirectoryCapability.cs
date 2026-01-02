#nullable enable
namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// Grain directory capabilities that a silo can advertise via silo metadata.
/// Used to coordinate rolling upgrades from LocalGrainDirectory to DistributedGrainDirectory.
/// </summary>
public static class GrainDirectoryCapability
{
    /// <summary>
    /// Metadata key for grain directory capability.
    /// </summary>
    public const string MetadataKey = "Orleans.GrainDirectory";

    /// <summary>
    /// Silo supports DistributedGrainDirectory.
    /// When all silos in the cluster advertise this capability, the cluster uses DistributedGrainDirectory exclusively.
    /// During mixed-cluster scenarios (some silos with this capability, some without), 
    /// DistributedGrainDirectory operates with filtered membership containing only capable silos.
    /// </summary>
    public const string Distributed = "Distributed";
}

using Orleans.Runtime.Utilities;

namespace Orleans.Runtime
{
    internal interface IClusterMembership
    {
        ClusterMembershipSnapshot CurrentSnapshot { get; }

        IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates { get; }
    }
}

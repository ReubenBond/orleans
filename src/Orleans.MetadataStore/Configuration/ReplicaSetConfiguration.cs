using System;
using System.Linq;
using Orleans.Runtime;

namespace Orleans.MetadataStore
{
    [Immutable]
    [Serializable]
    [GenerateSerializer]
    public class ReplicaSetConfiguration : IVersioned
    {
        public ReplicaSetConfiguration(
            Ballot stamp,
            long version,
            SiloAddress[] members,
            int acceptQuorum,
            int prepareQuorum)
        {
            Stamp = stamp;
            Version = version;
            Members = members;
            AcceptQuorum = acceptQuorum;
            PrepareQuorum = prepareQuorum;
        }

        /// <summary>
        /// The addresses of all members.
        /// </summary>
        [Id(0)]
        public SiloAddress[] Members { get; }

        /// <summary>
        /// The quorum size for Accept operations.
        /// </summary>
        [Id(1)]
        public int AcceptQuorum { get; }

        /// <summary>
        /// The quorum size for Prepare operations.
        /// </summary>
        [Id(2)]
        public int PrepareQuorum { get; }

        /// <summary>
        /// The unique ballot number of this configuration.
        /// </summary>
        [Id(3)]
        public Ballot Stamp { get; }

        /// <summary>
        /// The monotonically increasing version number of this configuration.
        /// </summary>
        [Id(4)]
        public long Version { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            var nodes = Members == null ? "[]" : $"[{string.Join(", ", Members.Select(_ => _.ToString()))}]";
            return $"{nameof(Stamp)}: {Stamp}, {nameof(Version)}: {Version}, {nameof(Members)}: {nodes}, {nameof(AcceptQuorum)}: {AcceptQuorum}, {nameof(PrepareQuorum)}: {PrepareQuorum}";
        }
    }
}

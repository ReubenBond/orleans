using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Orleans.Runtime;

namespace Orleans.MetadataStore
{
    [Immutable]
    [Serializable]
    [GenerateSerializer]
    public class ClusterConfiguration : IEquatable<ClusterConfiguration>
    {
        public ClusterConfiguration(
            Ballot stamp,
            MembershipVersion version,
            SiloAddress[] members)
        {
            Stamp = stamp;
            Version = version;
            Members = members ?? throw new ArgumentNullException(nameof(members));
            Array.Sort(Members);
        }

        /// <summary>
        /// The addresses of all members.
        /// </summary>
        [Id(0)]
        public SiloAddress[] Members { get; }

        /// <summary>
        /// The unique ballot number of this configuration.
        /// </summary>
        [Id(1)]
        public Ballot Stamp { get; }

        /// <summary>
        /// The version of this membership.
        /// </summary>
        [Id(2)]
        public MembershipVersion Version { get; }

        public bool Equals([AllowNull] ClusterConfiguration other)
        {
            if (other is null)
            {
                return false;
            }

            if (other.Stamp != Stamp)
            {
                return false;
            }

            if (other.Version != Version)
            {
                return false;
            }

            if (ReferenceEquals(other.Members, Members))
            {
                return true;
            }

            if (other.Members is null ^ Members is null)
            {
                return false;
            }

            if (other.Members.Length != Members.Length)
            {
                return false;
            }

            for (var i = 0; i < Members.Length; i++)
            {
                if (Members[i] != other.Members[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is ClusterConfiguration cfg && Equals(cfg);
        }

        public override int GetHashCode() => HashCode.Combine(Stamp, Version, Members);

        /// <inheritdoc />
        public override string ToString()
        {
            var nodes = Members == null ? "[]" : $"[{string.Join(", ", Members.Select(_ => _.ToString()))}]";
            return $"{nameof(Stamp)}: {Stamp}, {nameof(Version)}: {Version}, {nameof(Members)}: {nodes}";
        }
    }
}

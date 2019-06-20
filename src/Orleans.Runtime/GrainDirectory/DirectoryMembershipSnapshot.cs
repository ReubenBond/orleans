using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// A snapshot of cluster membership from the perspective of the local grain directory.
    /// </summary>
    internal class DirectoryMembershipSnapshot
    {
        private static readonly Func<SiloAddress, string> PrintSiloAddressForStatistics = (SiloAddress addr) => $"{addr.ToLongString()}/{addr.GetConsistentHashCode():X}";
        private static readonly Comparison<SiloAddress> RingComparer = CompareSiloAddressesForRing;
        private readonly ILogger log;
        private readonly ImmutableList<SiloAddress> ring;
        private readonly SiloAddress siloAddress;

        public DirectoryMembershipSnapshot(
            ILogger log,
            SiloAddress siloAddress,
            ClusterMembershipSnapshot clusterMembership)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.siloAddress = siloAddress ?? throw new ArgumentNullException(nameof(siloAddress));
            this.ClusterMembership = clusterMembership ?? throw new ArgumentNullException(nameof(clusterMembership));

            var activeMembers = ImmutableList.CreateBuilder<SiloAddress>();
            
            foreach (var member in clusterMembership.Members)
            {
                if (member.Value.Status == SiloStatus.Active)
                {
                    var silo = member.Value.SiloAddress;
                    activeMembers.Add(silo);
                }
            }

            activeMembers.Sort(RingComparer);
            this.ring = activeMembers.ToImmutable();
        }

        internal static int RingSizeStatistic(DirectoryMembershipSnapshot snapshot) => snapshot.ring.Count;

        internal static string RingDetailsStatistic(DirectoryMembershipSnapshot snapshot) => Utils.EnumerableToString(snapshot.ring, PrintSiloAddressForStatistics);

        internal static string RingPredecessorStatistic(DirectoryMembershipSnapshot snapshot) => Utils.EnumerableToString(snapshot.FindPredecessors(snapshot.siloAddress, 1), PrintSiloAddressForStatistics);

        internal static string RingSuccessorStatistic(DirectoryMembershipSnapshot snapshot) => Utils.EnumerableToString(snapshot.FindSuccessors(snapshot.siloAddress, 1), PrintSiloAddressForStatistics);

        private static int CompareSiloAddressesForRing(SiloAddress left, SiloAddress right)
        {
            var leftHash = left.GetConsistentHashCode();
            var rightHash = right.GetConsistentHashCode();
            return leftHash.CompareTo(rightHash);
        }

        /// <summary>
        /// The monotonically increasing membership version associated with this snapshot.
        /// </summary>
        public ClusterMembershipSnapshot ClusterMembership { get; }

        /// <summary>
        /// Returns the <see cref="SiloAddress"/> which owns the directory partition of the provided grain.
        /// </summary>
        [Pure]
        internal SiloAddress CalculateGrainDirectoryPartition(GrainId grainId)
        {
            // give a special treatment for special grains
            if (grainId.IsSystemTarget)
            {
                if (log.IsEnabled(LogLevel.Trace))
                {
                    log.LogTrace(
                        "Silo {LocalSilo} looked for a system target {SystemTarget}, returned {ResultSilo}",
                        this.siloAddress,
                        grainId,
                        this.siloAddress);
                }

                // every silo owns its system targets
                return this.siloAddress;
            }

            if (this.ring.Count == 0) return null;

            SiloAddress siloAddress = null;
            int hash = unchecked((int)grainId.GetUniformHashCode());

            // need to implement a binary search, but for now simply traverse the list of silos sorted by their hashes
            for (var index = this.ring.Count - 1; index >= 0; --index)
            {
                var item = this.ring[index];
                if (item.GetConsistentHashCode() <= hash)
                {
                    siloAddress = item;
                    break;
                }
            }

            if (siloAddress == null)
            {
                // If not found in the traversal, last silo will do (we are on a ring).
                // We checked above to make sure that the list isn't empty, so this should always be safe.
                siloAddress = this.ring[this.ring.Count - 1];
            }

            if (log.IsEnabled(LogLevel.Trace))
            {
                log.LogTrace(
                    "Silo {LocalSilo} calculated directory partition owner silo {Silo} for grain {Grain}: {GrainHash} --> {SiloHash}",
                    this.siloAddress,
                    siloAddress,
                    grainId,
                    hash,
                    siloAddress?.GetConsistentHashCode());
            }

            return siloAddress;
        }

        [Pure]
        public List<SiloAddress> FindPredecessors(SiloAddress silo, int count)
        {
            int index = this.ring.FindIndex(elem => elem.Equals(silo));
            var result = new List<SiloAddress>();
            if (index == -1)
            {
                log.Warn(ErrorCode.Runtime_Error_100201, "Got request to find predecessors of silo " + silo + ", which is not in the list of members");
                return result;
            }

            int numMembers = this.ring.Count;
            for (int i = index - 1; ((i + numMembers) % numMembers) != index && result.Count < count; i--)
            {
                result.Add(this.ring[(i + numMembers) % numMembers]);
            }

            return result;
        }

        [Pure]
        public List<SiloAddress> FindSuccessors(SiloAddress silo, int count)
        {
            var result = new List<SiloAddress>();
            int index = this.ring.FindIndex(elem => elem.Equals(silo));
            if (index == -1)
            {
                log.Warn(ErrorCode.Runtime_Error_100203, "Got request to find successors of silo " + silo + ", which is not in the list of members");
                return result;
            }

            int numMembers = this.ring.Count;
            for (int i = index + 1; i % numMembers != index && result.Count < count; i++)
            {
                result.Add(this.ring[i % numMembers]);
            }

            return result;
        }

        public string ToDetailedString()
        {
            var sb = new StringBuilder();
            sb.AppendFormat(
                "Silo address is {0}, silo consistent hash is {1:X}.",
                this.siloAddress,
                this.siloAddress.GetConsistentHashCode()).AppendLine();
            sb.AppendLine("Ring is:");
            foreach (var silo in (this).ring)
            {
                sb.AppendFormat("    Silo {0}, consistent hash is {1:X}", silo, silo.GetConsistentHashCode()).AppendLine();
            }

            sb.Append("My predecessors: " + this.FindPredecessors(this.siloAddress, 1).ToStrings(addr => $"{addr}/{addr.GetConsistentHashCode():X}---", " -- "));
            sb.AppendLine();
            sb.Append("My successors: " + this.FindSuccessors(this.siloAddress, 1).ToStrings(addr => $"{addr}/{addr.GetConsistentHashCode():X}---", " -- "));
            return sb.ToString();
        }
    }
}

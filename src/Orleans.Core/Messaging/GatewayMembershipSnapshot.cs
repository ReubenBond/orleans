using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    /// <summary>
    /// Describes the gateways in a deployment.
    /// </summary>
    [GenerateSerializer, Immutable]
    public sealed class GatewayMembershipSnapshot
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GatewayMembershipSnapshot"/> class.
        /// </summary>
        /// <param name="entries">The gateway entries.</param>
        /// <param name="version">The snapshot version.</param>
        public GatewayMembershipSnapshot(IEnumerable<GatewayMember> entries, MembershipVersion version)
        {
            Gateways = entries.ToImmutableDictionary(static entry => entry.SiloAddress, static entry => entry);
            Version = version;
        }

        /// <summary>
        /// Gets the gateways.
        /// </summary>
        [Id(0)]
        public ImmutableDictionary<SiloAddress, GatewayMember> Gateways { get; }

        /// <summary>
        /// Gets the version of the gateway table.
        /// </summary>
        [Id(1)]
        public MembershipVersion Version { get; }

        /// <inheritdoc/>
        public override string ToString() => $"GatewayMembershipSnapshot(Version={Version}, Gateways=[{string.Join(", ", Gateways.Select(g => $"[{g}]"))}])";
    }
}

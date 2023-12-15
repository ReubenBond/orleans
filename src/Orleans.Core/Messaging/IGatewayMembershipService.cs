using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    /// <summary>
    /// Functionality for querying gateway membership.
    /// </summary>
    public interface IGatewayMembershipService
    {
        /// <summary>
        /// Gets the current membership snapshot.
        /// </summary>
        GatewayMembershipSnapshot CurrentSnapshot { get; }

        /// <summary>
        /// Gets a stream of membership snapshot updates.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A stream of updates to gateway membership.</returns>
        IAsyncEnumerable<GatewayMembershipSnapshot> GetMembershipUpdatesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Refreshes membership if it is not at or above the specified minimum version.
        /// </summary>
        /// <param name="minimumVersion">The minimum version.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
        ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default);
    }
}

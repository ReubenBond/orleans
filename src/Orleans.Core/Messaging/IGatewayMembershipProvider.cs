using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Messaging
{
    /// <summary>
    /// Provides information about the current gateway membership.
    /// </summary>
    public interface IGatewayMembershipProvider
    {
        /// <summary>
        /// Gets the current membership snapshot.
        /// </summary>
        /// <param name="cancellationToken">A token which can be used to cancel the operation.</param>
        /// <returns>The membership snapshot.</returns>
        public ValueTask<GatewayMembershipSnapshot> GetGatewaysAsync(CancellationToken cancellationToken);
    }
}

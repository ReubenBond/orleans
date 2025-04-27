using System.Collections.Generic;
using System.Threading;
using Orleans.Membership;


namespace Orleans.Runtime;

internal interface IGatewayMembershipGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Gets updates to gateway membership, starting with a full snapshot of the current membership.
    /// </summary>
    /// <param name="cancellationToken">A token used to signal cancellation.</param>
    /// <returns>A stream of updates to gateway membership, starting with a full snapshot of the current membership.</returns>
    IAsyncEnumerable<GatewayMembershipUpdate> GetGatewayMembershipUpdatesAsync(CancellationToken cancellationToken);
}

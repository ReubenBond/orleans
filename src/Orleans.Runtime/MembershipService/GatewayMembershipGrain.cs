using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Membership;

namespace Orleans.Runtime.MembershipService;
internal sealed class GatewayMembershipGrain(MembershipTableManager membershipTableManager) : IGatewayMembershipGrain
{
    public async IAsyncEnumerable<GatewayMembershipUpdate> GetGatewayMembershipUpdatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var previousSnapshot = membershipTableManager.MembershipTableSnapshot;
        yield return previousSnapshot.CreateGatewayMembershipUpdate();
        await foreach (var snapshot in membershipTableManager.MembershipTableUpdates.WithCancellation(cancellationToken))
        {
            if (snapshot.Version <= previousSnapshot.Version)
            {
                continue;
            }

            yield return snapshot.CreateGatewayMembershipUpdate(previousSnapshot);
            previousSnapshot = snapshot;
        }
    }
}

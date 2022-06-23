#nullable enable

using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService.Standalone;

internal interface IAcceptor<TValue>
{
    ValueTask<(bool Success, ConfigurationBallot Ballot, TValue? Value)> Prepare(ConfigurationBallot ballot);

    ValueTask<(bool Success, ConfigurationBallot Ballot)> Accept(ConfigurationBallot ballot, TValue value, AcceptOptions options);
}

#nullable enable

using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService.Standalone;

internal partial class Acceptor<TValue, TVersionedValue> : IAcceptor<TValue, TVersionedValue> where TVersionedValue : IVersionedValue<TValue, TVersionedValue>
{
    private struct AcceptorState
    {
        public ConfigurationBallot PromisedBallot { get; set; }
        public ConfigurationBallot AcceptedBallot { get; set; }
        public TVersionedValue? Value { get; set; }
    }

    private AcceptorState _state;

    private ConfigurationBallot MaxBallot => _state.PromisedBallot > _state.AcceptedBallot ? _state.PromisedBallot : _state.AcceptedBallot;

    public ValueTask<(bool Success, ConfigurationBallot Ballot, TVersionedValue? Value)> Prepare(ConfigurationBallot ballot)
    {
        var maxBallot = MaxBallot;
        if (ballot < maxBallot)
        {
            return new((false, maxBallot, _state.Value));
        }

        _state.PromisedBallot = ballot;
        return new((true, ballot, _state.Value));
    }

    public ValueTask<(bool Success, ConfigurationBallot Ballot)> Accept(ConfigurationBallot ballot, TVersionedValue value, AcceptOptions options)
    {
        var maxBallot = MaxBallot;
        if (ballot < _state.PromisedBallot)
        {
            return new((false, maxBallot));
        }

        if (ballot < _state.AcceptedBallot)
        {
            return new((false, maxBallot));
        }

        // Additional optimization for fast rounds: allow multiple proposers to propose identical values.
        // This reduces needless conflicts in the anticipated scenario where multiple proposers move in lock-step.
        if (ballot == _state.AcceptedBallot)
        {
            // Allow only equivalent values to be accepted under this optimization.
            // That means that eitehr both values are null, or both values are non-null and are equal.
            var acceptedValue = _state.Value;
            var valuesAreNull = acceptedValue is null && value is null;
            var valuesAreEqual = acceptedValue is not null && value is not null && acceptedValue.Equals(value);

            if (!valuesAreNull && !valuesAreEqual)
            {
                // The values differ.
                return new((false, maxBallot));
            }
        }

        // To enable multiple successive fast-rounds as well as the distinguished proposer optimization, the proposer
        // may elect to also prepare the next round immediately.
        if (options.PrepareNextAccept)
        {
            _state.PromisedBallot = ballot.Successor(ballot.Proposer);
        }

        _state.AcceptedBallot = ballot;
        _state.Value = value;
        return new((true, ballot));
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService.Standalone;

/// <summary>
/// Represents a Paxos ballot, sometimes known as a round number.
/// </summary>
internal readonly struct ConfigurationBallot : IComparable<ConfigurationBallot>
{
    /// <summary>
    /// The round number.
    /// </summary>
    public readonly int Round;

    /// <summary>
    /// The unique identifier of the proposer.
    /// </summary>
    public readonly Guid Proposer;

    public ConfigurationBallot(int round, Guid id)
    {
        Round = round;
        Proposer = id;
    }

    public ConfigurationBallot FastRoundSuccessor() => new(Round + 1, Guid.Empty);

    public ConfigurationBallot Successor(Guid id) => new(Round + 1, id);

    public ConfigurationBallot AdvancePast(ConfigurationBallot other) => new(Math.Max(Round, other.Round), Proposer);

    public static ConfigurationBallot Zero => default;

    public bool IsZero => Equals(Zero);

    public bool IsFastRoundBallot => Proposer == Guid.Empty;

    public bool IsClassicRoundBallot => !IsFastRoundBallot;

    /// <inheritdoc />
    public override string ToString() => $"{nameof(ConfigurationBallot)}({Round}.{Proposer:N})";

    public bool Equals(ConfigurationBallot other) => Round == other.Round && Proposer == other.Proposer;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ConfigurationBallot ballot && Equals(ballot);

    /// <inheritdoc />
    public int CompareTo(ConfigurationBallot other)
    {
        var roundComparison = Round - other.Round;
        if (roundComparison != 0)
        {
            return roundComparison;
        }

        return Proposer.CompareTo(other.Proposer);
    }

    public static bool operator ==(ConfigurationBallot left, ConfigurationBallot right) => left.Equals(right);

    public static bool operator !=(ConfigurationBallot left, ConfigurationBallot right) => !left.Equals(right);

    public static bool operator <(ConfigurationBallot left, ConfigurationBallot right) => left.CompareTo(right) < 0;

    public static bool operator >(ConfigurationBallot left, ConfigurationBallot right) => left.CompareTo(right) > 0;

    public static bool operator <=(ConfigurationBallot left, ConfigurationBallot right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ConfigurationBallot left, ConfigurationBallot right) => left.CompareTo(right) >= 0;

    public override int GetHashCode() => HashCode.Combine(Round, Proposer);
}

internal enum OperationStatus
{
    Failed,
    NotApplicable,
    Success
}

internal interface IOperation<TValue>
{
    (OperationStatus Status, TValue Result) Apply(TValue current);
}

internal delegate (OperationStatus Status, TValue Result) Apply<TInput, TValue>(TValue existing, TInput? input);

internal class Operation<TInput, TValue> : IOperation<TValue>
{
    public Operation(Apply<TInput, TValue> apply, TInput? input, string operationName)
    {
        Input = input;
        Apply = apply;
        Name = operationName;
    }

    public TInput? Input { get; set; }
    public Apply<TInput, TValue> Apply { get; init; }
    public string Name { get; set; }

    (OperationStatus Status, TValue Result) IOperation<TValue>.Apply(TValue current) => Apply(current, Input);

    public override string ToString() => Name;
}

internal interface IProposer<TValue>
{
    ValueTask<(OperationStatus Status, TValue? Result)> Commit(IOperation<TValue> operation, CancellationToken cancellation);
}

internal interface ILearner<TValue>
{
    ValueTask OnCommitted(TValue value/*, Ballot? ballot -- for sharing fast commit ballots */);
}


// Used to provide list of hosts for use while bootstrapping a cluster and connecting to an existing cluster.
internal interface IBootstrapHostListProvider
{
    HostList Current { get; }

    IAsyncEnumerable<HostList> Changes();
}

[GenerateSerializer]
public record HostList
{
    [Id(0)]
    public long Version { get; init; }

    [Id(1)]
    public EndPoint[] Hosts { get; init; } = Array.Empty<EndPoint>();
}

internal interface IAcceptorRouter<TValue>
{
    ValueTask<(bool Success, ConfigurationBallot Ballot, TValue? Value)> Prepare(EndPoint member, ConfigurationBallot ballot);

    ValueTask<(bool Success, ConfigurationBallot Ballot)> Accept(EndPoint member, ConfigurationBallot ballot, TValue value, AcceptOptions options);
}

internal class ConfigurationProposer : IProposer<ClusterConfiguration>
{
    private readonly IAcceptorRouter<ClusterConfiguration> _acceptors;
    private readonly ISeedHostProvider _memberListProvider;

    public ConfigurationProposer(Guid proposerId, IAcceptorRouter<ClusterConfiguration> acceptors, ISeedHostProvider memberListProvider)
    {
        _acceptors = acceptors;
        _memberListProvider = memberListProvider;
        _nextBallot = new(0, proposerId);
        _members = memberListProvider.Current;
    }

    private readonly Guid _proposerId;
    private EndPoint[] _members;
    private ConfigurationBallot? _preparedBallot;
    private ConfigurationBallot _nextBallot;
    private ClusterConfiguration? _cachedValue;
    private bool _enableDistinguishedLeaderCommit;
    private bool _enableFastCommit;
    private bool _preferFastCommit;

    public void InstallConfiguration(ClusterConfiguration configuration)
    {
    }

    public async ValueTask<(OperationStatus Status, ClusterConfiguration? Result)> Commit(IOperation<ClusterConfiguration> operation, CancellationToken cancellation)
    {
        while (!_preparedBallot.HasValue || _preparedBallot.Value == _nextBallot)
        {
            if (cancellation.IsCancellationRequested)
            {
                return (OperationStatus.Failed, default);
            }

            _nextBallot = ChooseNextBallot();
            var (success, maxConflict) = await Prepare(_nextBallot, cancellation);

            if (success)
            {
                _preparedBallot = _nextBallot;
                break;
            }

            if (maxConflict.HasValue && maxConflict.Value > _nextBallot)
            {
                _nextBallot = _nextBallot.AdvancePast(maxConflict.Value);
            }
        }

        return default;
    }

    private ValueTask<(bool Success, ConfigurationBallot? maxConflict)> Prepare(ConfigurationBallot attemptBallot, CancellationToken cancellation)
    {
        return default;
    }

    private ConfigurationBallot ChooseNextBallot()
    {
        var nextProposerId = _preferFastCommit switch
        {
            _ when _preferFastCommit => Guid.Empty,
            _ => _proposerId
        };

        return _nextBallot.Successor(nextProposerId);
    }

    public bool HasQuorum(ConfigurationBallot ballot, int responses)
    {
        if (ballot.IsFastRoundBallot)
        {
            // >= 3n/4
            return 4 * responses >= 3 * _acceptors.Count;
        }
        else
        {
            // >= n/2 + 1 (i.e., > n/2)
            return 2 * responses > _acceptors.Count;
        }
    }

    /// <summary>
    /// Returns true if a quorum is possible.
    /// </summary>
    /// <remarks>
    /// A quorum of either success or failure responses is not currently possible, so retry indefinitely.
    /// Typical cases where this could happen are:
    /// <list type="unordered">
    ///   <item>4 acceptors nodes with 2 success + 2 failures</item>
    ///   <item>3 acceptors and a Fast Paxos round with 2 success + 1 failure</item>
    /// </list>
    /// </remarks>
    public bool IsQuorumPossible(ConfigurationBallot ballot, int successResponses, int failureResponses)
    {
        // Assuming all of the remaining votes go one direction or the other, can a quorum be achieved?
        var remaining = _members.Count - (successResponses + failureResponses);
        return HasQuorum(ballot, successResponses + remaining) || HasQuorum(ballot, failureResponses + remaining);
    }
}

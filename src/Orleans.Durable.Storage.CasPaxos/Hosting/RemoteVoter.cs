using Orleans.Consensus.CasPaxos.Messages;
using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Protocol;
using Orleans.Consensus.CasPaxos.Server;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// Another silo's voter as the proposer's <see cref="IKeyedAcceptor"/>: each operation addresses the voter's system
/// target at the voter's current silo address, and returns the voter's answer only when the reply names that voter.
/// </summary>
/// <remarks>
/// <see cref="ReplicatedRegisterService"/> runs over it unchanged: a voter which cannot be addressed, a failed call, or a
/// reply from another voter fails the operation, which the register service counts as that voter's missing reply. Its
/// deadlines, cancellation, late replies, and unknown write outcomes keep their meanings, since the register service
/// cancels each phase's calls and consumes no reply after its phase.
/// </remarks>
/// <param name="id">The voter's identity.</param>
/// <param name="target">Returns the voter's system target at its current silo address, or throws <see cref="CasPaxosVoterUnavailableException"/>.</param>
internal sealed class RemoteVoter(string id, Func<ICasPaxosVoter> target) : IKeyedAcceptor
{
    /// <summary>Gets the voter's identity.</summary>
    public string Id { get; } = id;

    /// <inheritdoc/>
    public async ValueTask<AcceptorReadResult> ReadAsync(string key, AcceptedDigest? known = null, CancellationToken cancellationToken = default)
    {
        var reply = await target().ReadAsync(Id, key, known?.AcceptedBallot, known?.ValueHash, cancellationToken).ConfigureAwait(false);
        Validate(reply.Voter);
        return reply.Kind switch
        {
            VoterReadKind.Missing => new AcceptorMissing(reply.PromisedBallot),
            VoterReadKind.Unchanged => new AcceptorUnchanged(),
            VoterReadKind.Accepted when reply.Record is { } record => record.ToState(),
            _ => throw new InvalidDataException($"Voter '{Id}' answered a read with an invalid outcome."),
        };
    }

    /// <inheritdoc/>
    public async ValueTask<AcceptorPrepareResult<ByteString?>> PrepareAsync(string key, PrepareRequest request, CancellationToken cancellationToken = default)
    {
        var reply = await target().PrepareAsync(Id, key, request.Ballot, cancellationToken).ConfigureAwait(false);
        Validate(reply.Voter);
        return new(reply.AcceptedBallot, reply.AcceptedValue, reply.ConflictingBallot);
    }

    /// <inheritdoc/>
    public async ValueTask<AcceptorAcceptResult> AcceptAsync(string key, AcceptRequest<ByteString> request, CancellationToken cancellationToken = default)
    {
        var reply = await target().AcceptAsync(Id, key, request.Ballot, request.Value, request.NextBallotToPrepare, cancellationToken).ConfigureAwait(false);
        Validate(reply.Voter);
        return new(reply.IsAccepted, reply.Ballot);
    }

    /// <inheritdoc/>
    public async ValueTask<Ballot> SetFloorAsync(Ballot targetFloor, CancellationToken cancellationToken = default)
    {
        var reply = await target().SetFloorAsync(Id, targetFloor, cancellationToken).ConfigureAwait(false);
        Validate(reply.Voter);
        return reply.Floor;
    }

    /// <inheritdoc/>
    public async ValueTask<KeyPage> ListKeysAsync(string prefix = "", string? afterKey = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var reply = await target().ListKeysAsync(Id, prefix, afterKey, limit, cancellationToken).ConfigureAwait(false);
        Validate(reply.Voter);
        return new([.. reply.Entries.Select(static record => record.ToState())], reply.ContinuationKey, reply.FloorBallot);
    }

    /// <summary>
    /// Asks the voter to admit <paramref name="incarnation"/> of <paramref name="candidate"/>, validating
    /// <paramref name="bootstrapDigest"/> when supplied and returning the voter's reply.
    /// </summary>
    public async ValueTask<VoterAdmitReply> AdmitAsync(string candidate, Guid incarnation, ByteString? bootstrapDigest, CancellationToken cancellationToken)
    {
        var reply = await target().AdmitAsync(Id, candidate, incarnation, bootstrapDigest, cancellationToken).ConfigureAwait(false);
        Validate(reply.Voter);
        return reply;
    }

    private void Validate(string answered)
    {
        if (!string.Equals(answered, Id, StringComparison.Ordinal))
        {
            throw new CasPaxosVoterUnavailableException($"Voter '{answered}' answered a request addressed to voter '{Id}'; its reply does not count.");
        }
    }
}

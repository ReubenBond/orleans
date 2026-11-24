using Orleans.Consensus.CasPaxos.Messages;
using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Server;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// A voter's answers to <see cref="ICasPaxosVoter"/> requests, which its system target serves over Orleans RPC. It holds
/// no protocol state: each request runs the <see cref="LocalVoter"/>'s own transition and answers once its transaction
/// commits, naming the voter which answered.
/// </summary>
/// <remarks>
/// A request names the voter it is for; the service refuses one for another voter before it touches state, so a request
/// which reached the wrong silo changes nothing. A caller's cancellation reaches the acceptor, which honors it until its
/// writer selects the request.
/// </remarks>
/// <param name="local">The voter the silo hosts.</param>
internal sealed class CasPaxosVoterService(LocalVoter local) : ICasPaxosVoter
{
    /// <inheritdoc/>
    public async ValueTask<VoterReadReply> ReadAsync(string voter, string key, Ballot? knownBallot, ByteString? knownHash, CancellationToken cancellationToken)
    {
        ThrowIfNotFor(voter);
        AcceptedDigest? known = knownBallot is { } ballot && knownHash is { } hash ? new(ballot, hash) : null;
        var result = await local.ReadAsync(key, known, cancellationToken);
        return result switch
        {
            KeyedAcceptorState state => new(local.Id, VoterReadKind.Accepted, VoterAcceptedRecord.From(state), state.PromisedBallot),
            AcceptorUnchanged => new(local.Id, VoterReadKind.Unchanged, null),
            AcceptorMissing missing => new(local.Id, VoterReadKind.Missing, null, missing.PromisedBallot),
            _ => new(local.Id, VoterReadKind.Missing, null),
        };
    }

    /// <inheritdoc/>
    public async ValueTask<VoterPrepareReply> PrepareAsync(string voter, string key, Ballot ballot, CancellationToken cancellationToken)
    {
        ThrowIfNotFor(voter);
        var result = await local.PrepareAsync(key, new PrepareRequest(ballot), cancellationToken);
        return new(local.Id, result.AcceptedBallot, result.AcceptedValue, result.ConflictingBallot);
    }

    /// <inheritdoc/>
    public async ValueTask<VoterAcceptReply> AcceptAsync(string voter, string key, Ballot ballot, ByteString value, Ballot nextBallotToPrepare, CancellationToken cancellationToken)
    {
        ThrowIfNotFor(voter);
        var result = await local.AcceptAsync(key, new AcceptRequest<ByteString>(ballot, value, nextBallotToPrepare), cancellationToken);
        return new(local.Id, result.IsAccepted, result.GetBallot());
    }

    /// <inheritdoc/>
    public async ValueTask<VoterSetFloorReply> SetFloorAsync(string voter, Ballot targetFloor, CancellationToken cancellationToken)
    {
        ThrowIfNotFor(voter);
        var floor = await local.SetFloorAsync(targetFloor, cancellationToken);
        return new(local.Id, floor);
    }

    /// <inheritdoc/>
    public async ValueTask<VoterKeyPageReply> ListKeysAsync(string voter, string prefix, string? afterKey, int limit, CancellationToken cancellationToken)
    {
        ThrowIfNotFor(voter);
        var page = await local.ListKeysAsync(prefix, afterKey, limit, cancellationToken);
        return new(local.Id, [.. page.Entries.Select(VoterAcceptedRecord.From)], page.ContinuationKey, page.FloorBallot);
    }

    /// <inheritdoc/>
    public async ValueTask<VoterAdmitReply> AdmitAsync(string voter, string candidate, Guid incarnation, ByteString? bootstrapDigest, CancellationToken cancellationToken)
    {
        ThrowIfNotFor(voter);
        try
        {
            return new(local.Id, await local.AdmitAsync(candidate, incarnation, bootstrapDigest, cancellationToken), VoterAdmitStatus.Admitted);
        }
        catch (InvalidDataException)
        {
            return new(local.Id, Guid.Empty, VoterAdmitStatus.BootstrapDigestMismatch);
        }
    }

    private void ThrowIfNotFor(string addressed)
    {
        if (!string.Equals(addressed, local.Id, StringComparison.Ordinal))
        {
            throw new CasPaxosVoterUnavailableException($"This silo serves voter '{local.Id}', not '{addressed}'.");
        }
    }
}

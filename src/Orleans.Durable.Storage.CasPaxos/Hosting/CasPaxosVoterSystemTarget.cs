using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// The system target through which a voter silo serves its voter over Orleans RPC (<see cref="ICasPaxosVoter"/>): each
/// call runs on the target's own scheduler and forwards to the voter's <see cref="CasPaxosVoterService"/>.
/// </summary>
internal sealed class CasPaxosVoterSystemTarget : SystemTarget, ICasPaxosVoter
{
    /// <summary>The system target's grain type, one per voter silo.</summary>
    public static readonly GrainType VoterType = SystemTargetGrainId.CreateGrainType("durable-storage.caspaxos.voter");

    private readonly CasPaxosVoterService _service;

    public CasPaxosVoterSystemTarget(CasPaxosVoterService service, SystemTargetShared shared)
        : base(VoterType, shared)
    {
        _service = service;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    /// <inheritdoc/>
    public ValueTask<VoterReadReply> ReadAsync(string voter, string key, Ballot? knownBallot, ByteString? knownHash, CancellationToken cancellationToken) =>
        _service.ReadAsync(voter, key, knownBallot, knownHash, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<VoterPrepareReply> PrepareAsync(string voter, string key, Ballot ballot, CancellationToken cancellationToken) =>
        _service.PrepareAsync(voter, key, ballot, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<VoterAcceptReply> AcceptAsync(string voter, string key, Ballot ballot, ByteString value, Ballot nextBallotToPrepare, CancellationToken cancellationToken) =>
        _service.AcceptAsync(voter, key, ballot, value, nextBallotToPrepare, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<VoterKeyPageReply> ListKeysAsync(string voter, string prefix, string? afterKey, int limit, CancellationToken cancellationToken) =>
        _service.ListKeysAsync(voter, prefix, afterKey, limit, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<VoterAdmitReply> AdmitAsync(string voter, string candidate, Guid incarnation, CancellationToken cancellationToken) =>
        _service.AdmitAsync(voter, candidate, incarnation, cancellationToken);
}

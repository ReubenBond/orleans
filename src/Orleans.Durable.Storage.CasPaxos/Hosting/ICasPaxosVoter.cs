using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Server;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// A consensus voter's acceptor operations over Orleans RPC: the system target which each voter silo hosts, addressed at
/// the voter's current silo address.
/// </summary>
/// <remarks>
/// Each request names the voter it is for, which the target serves only when it is that voter, and each reply names
/// the voter which answered, so a caller counts a reply only from the voter it addressed. Every answer is the local
/// acceptor's durable answer: the target answers after its transaction commits.
/// </remarks>
[Alias("Orleans.Durable.Storage.CasPaxos.ICasPaxosVoter")]
internal interface ICasPaxosVoter : ISystemTarget
{
    /// <summary>Reads the voter's accepted state of <paramref name="key"/>, as <see cref="IKeyedAcceptor.ReadAsync"/> does.</summary>
    [Alias("Read")]
    ValueTask<VoterReadReply> ReadAsync(string voter, string key, Ballot? knownBallot, ByteString? knownHash, CancellationToken cancellationToken);

    /// <summary>Prepares <paramref name="ballot"/> for <paramref name="key"/>, as <see cref="IKeyedAcceptor.PrepareAsync"/> does.</summary>
    [Alias("Prepare")]
    ValueTask<VoterPrepareReply> PrepareAsync(string voter, string key, Ballot ballot, CancellationToken cancellationToken);

    /// <summary>Accepts <paramref name="value"/> at <paramref name="ballot"/> for <paramref name="key"/>, as <see cref="IKeyedAcceptor.AcceptAsync"/> does.</summary>
    [Alias("Accept")]
    ValueTask<VoterAcceptReply> AcceptAsync(string voter, string key, Ballot ballot, ByteString value, Ballot nextBallotToPrepare, CancellationToken cancellationToken);

    /// <summary>Lists the voter's accepted records, as <see cref="IKeyedAcceptor.ListKeysAsync"/> does.</summary>
    [Alias("ListKeys")]
    ValueTask<VoterKeyPageReply> ListKeysAsync(string voter, string prefix, string? afterKey, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Asks <paramref name="voter"/> to admit <paramref name="incarnation"/> of the voter <paramref name="candidate"/>
    /// (<see cref="Consensus.CasPaxos.Sqlite.SqliteVoterRegistry.AdmitAsync"/>), answering with the incarnation it admits.
    /// </summary>
    [Alias("Admit")]
    ValueTask<VoterAdmitReply> AdmitAsync(string voter, string candidate, Guid incarnation, CancellationToken cancellationToken);
}

/// <summary>The kinds of a voter's read answer (<see cref="AcceptorReadResult"/>).</summary>
internal enum VoterReadKind : byte
{
    /// <summary>The voter holds no accepted value for the key (<see cref="AcceptorMissing"/>).</summary>
    Missing,

    /// <summary>The voter's accepted state matches the caller's digest (<see cref="AcceptorUnchanged"/>).</summary>
    Unchanged,

    /// <summary>The voter's accepted state (<see cref="KeyedAcceptorState"/>).</summary>
    Accepted,
}

/// <summary>A voter's accepted record of a key, as <see cref="KeyedAcceptorState"/> holds it.</summary>
[GenerateSerializer]
[Immutable]
internal sealed record VoterAcceptedRecord(
    [property: Id(0)] string Key,
    [property: Id(1)] Ballot PromisedBallot,
    [property: Id(2)] Ballot AcceptedBallot,
    [property: Id(3)] ByteString AcceptedValue)
{
    public static VoterAcceptedRecord From(KeyedAcceptorState state) => new(state.Key, state.PromisedBallot, state.AcceptedBallot, state.AcceptedValue);

    public KeyedAcceptorState ToState() => new(Key, PromisedBallot, AcceptedBallot, AcceptedValue);
}

/// <summary>A voter's read answer; <see cref="Record"/> is present for <see cref="VoterReadKind.Accepted"/> alone.</summary>
[GenerateSerializer]
[Immutable]
internal sealed record VoterReadReply([property: Id(0)] string Voter, [property: Id(1)] VoterReadKind Kind, [property: Id(2)] VoterAcceptedRecord? Record);

/// <summary>A voter's prepare answer, as <c>AcceptorPrepareResult&lt;ByteString?&gt;</c> holds it.</summary>
[GenerateSerializer]
[Immutable]
internal sealed record VoterPrepareReply(
    [property: Id(0)] string Voter,
    [property: Id(1)] Ballot AcceptedBallot,
    [property: Id(2)] ByteString? AcceptedValue,
    [property: Id(3)] Ballot ConflictingBallot);

/// <summary>A voter's accept answer, as <c>AcceptorAcceptResult</c> holds it.</summary>
[GenerateSerializer]
[Immutable]
internal sealed record VoterAcceptReply([property: Id(0)] string Voter, [property: Id(1)] bool IsAccepted, [property: Id(2)] Ballot Ballot);

/// <summary>A voter's inventory page, as <see cref="KeyPage"/> holds it.</summary>
[GenerateSerializer]
[Immutable]
internal sealed record VoterKeyPageReply(
    [property: Id(0)] string Voter,
    [property: Id(1)] IReadOnlyList<VoterAcceptedRecord> Entries,
    [property: Id(2)] string? ContinuationKey);

/// <summary>A voter's admission answer: the incarnation of the candidate it admits.</summary>
[GenerateSerializer]
[Immutable]
internal sealed record VoterAdmitReply([property: Id(0)] string Voter, [property: Id(1)] Guid Admitted);

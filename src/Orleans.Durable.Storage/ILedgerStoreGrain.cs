using Orleans.Concurrency;

namespace Orleans.Durable.Storage;

/// <summary>
/// A node's replica endpoint (orleans.md §5.12, §7.2): one call per <c>ILedgerReplica</c> operation on a replica of the
/// node's LedgerStore, keyed by the node identity, so the node crosses as the grain's key. Placed by
/// <see cref="RegisteredNodePlacement"/> only on the silo the registry names for the node while membership shows it
/// active (OR-I14). Calls interleave, and their store work runs off the activation's scheduler.
/// </summary>
internal interface ILedgerStoreGrain : IGrainWithGuidKey
{
    /// <summary>Writes a slot of the request's replica: a writer write, or a recovery write.</summary>
    [AlwaysInterleave]
    Task<RemoteReplicaWriteOutcome> WriteAsync(LedgerReplicaWriteRequest request, CancellationToken cancellationToken);

    /// <summary>Reads a slot of the request's replica, whole or a range of it.</summary>
    [AlwaysInterleave]
    Task<RemoteReplicaRead> ReadAsync(LedgerReplicaReadRequest request, CancellationToken cancellationToken);

    /// <summary>Reads a contiguous batch of slots from the request's replica in one RPC.</summary>
    [AlwaysInterleave]
    Task<RemoteReplicaRangeRead> ReadRangeAsync(LedgerReplicaReadRangeRequest request, CancellationToken cancellationToken);

    /// <summary>Reads the request's replica's contiguous tail and seal, sealing it first when the request seals.</summary>
    [AlwaysInterleave]
    Task<RemoteReplicaTailRead> ReadTailAsync(LedgerReplicaTailRequest request, CancellationToken cancellationToken);

    /// <summary>Extends the RAM-only read lease for each listed unsealed live ledger on this node (LR-I10).</summary>
    [AlwaysInterleave]
    Task<RemoteReplicaHeartbeatRead> HeartbeatAsync(LedgerReplicaHeartbeatRequest request, CancellationToken cancellationToken);
}

/// <summary>The replica protocol's version, which every replica call carries and the callee checks first (OR-I15).</summary>
internal static class LedgerReplicaProtocol
{
    /// <summary>
    /// The version this silo speaks. It changes whenever a call gains a field which a callee must not ignore, or a field's
    /// meaning or encoding changes, since Orleans' version-tolerant serializer drops a field its recipient does not know.
    /// Version 2 carries payloads and DATA in Orleans' <c>ArcBuffer</c>, whose empty field a version-1 silo cannot decode.
    /// Version 3 adds L2 quorum read-lease grants, remaining lease durations on sealed tails, and batched peer heartbeats.
    /// Version 4 adds batched multi-slot <c>ReadRangeAsync</c>.
    /// </summary>
    public const int Version = 4;
}

/// <summary>
/// A replica write as it crosses to the node (OR-I15): the protocol version, the ledger and the replica's window, the slot,
/// the caller's known contiguous tail, the recovery flag, and the payload in an Orleans buffer (OR-I18). It is not
/// <see cref="ImmutableAttribute"/>, so Orleans' proxy pins a copy of the payload as each call is made, which the
/// message holds; the sender then releases its own copy once its calls end, and the callee releases the payload it
/// receives on every path.
/// </summary>
[GenerateSerializer]
internal readonly struct LedgerReplicaWriteRequest
{
    /// <summary>Gets or initializes the replica protocol's version.</summary>
    [Id(0)]
    public int Version { get; init; }

    /// <summary>Gets or initializes the ledger's ID.</summary>
    [Id(1)]
    public ulong Ledger { get; init; }

    /// <summary>Gets or initializes the replica's window <c>K</c>.</summary>
    [Id(2)]
    public int WindowSize { get; init; }

    /// <summary>Gets or initializes the slot.</summary>
    [Id(3)]
    public long Slot { get; init; }

    /// <summary>Gets or initializes the caller's known contiguous tail.</summary>
    [Id(4)]
    public long ContiguousTail { get; init; }

    /// <summary>Gets or initializes a value indicating whether the write is a recovery write.</summary>
    [Id(5)]
    public bool Recovery { get; init; }

    /// <summary>Gets or initializes the payload; a request which carries none is refused as an empty batch.</summary>
    [Id(6)]
    public OrleansBuffer Payload { get; init; }
}

/// <summary>
/// A replica read as it crosses to the node (OR-I15): the protocol version, the ledger and the replica's window, the slot,
/// the caller's known contiguous tail, a ranged read's offset and positive length, which a whole read leaves at zero, and
/// the longest reply the caller reserved, beyond which the host answers <see cref="RemoteReplicaRead.TooLarge"/>.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerReplicaReadRequest
{
    /// <summary>Gets or initializes the replica protocol's version.</summary>
    [Id(0)]
    public int Version { get; init; }

    /// <summary>Gets or initializes the ledger's ID.</summary>
    [Id(1)]
    public ulong Ledger { get; init; }

    /// <summary>Gets or initializes the replica's window <c>K</c>.</summary>
    [Id(2)]
    public int WindowSize { get; init; }

    /// <summary>Gets or initializes the slot.</summary>
    [Id(3)]
    public long Slot { get; init; }

    /// <summary>Gets or initializes the caller's known contiguous tail.</summary>
    [Id(4)]
    public long ContiguousTail { get; init; }

    /// <summary>Gets or initializes a ranged read's offset into the value.</summary>
    [Id(5)]
    public int Offset { get; init; }

    /// <summary>Gets or initializes a ranged read's length, positive; zero for a whole read.</summary>
    [Id(6)]
    public int Length { get; init; }

    /// <summary>Gets or initializes the longest reply the caller reserved: a ranged read's length, or a whole read's reservation.</summary>
    [Id(7)]
    public int MaxReplyBytes { get; init; }

    /// <summary>Gets a value indicating whether the read is of a range of the value rather than the whole of it.</summary>
    public bool IsRange => Length > 0;
}

/// <summary>
/// A batched multi-slot replica read as it crosses to the node (OR-I15): reads up to <see cref="MaxSlots"/> consecutive
/// slots starting at <see cref="From"/> below <see cref="Below"/> whose packed length-prefixed frames fit within
/// <see cref="MaxBytes"/>.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerReplicaReadRangeRequest
{
    /// <summary>Gets or initializes the replica protocol's version.</summary>
    [Id(0)]
    public int Version { get; init; }

    /// <summary>Gets or initializes the ledger's ID.</summary>
    [Id(1)]
    public ulong Ledger { get; init; }

    /// <summary>Gets or initializes the replica's window <c>K</c>.</summary>
    [Id(2)]
    public int WindowSize { get; init; }

    /// <summary>Gets or initializes the first slot to read.</summary>
    [Id(3)]
    public long From { get; init; }

    /// <summary>Gets or initializes the exclusive upper slot bound.</summary>
    [Id(4)]
    public long Below { get; init; }

    /// <summary>Gets or initializes the caller's known contiguous tail.</summary>
    [Id(5)]
    public long ContiguousTail { get; init; }

    /// <summary>Gets or initializes the maximum number of consecutive slots to return.</summary>
    [Id(6)]
    public int MaxSlots { get; init; }

    /// <summary>Gets or initializes the maximum packed reply bytes reserved by the caller.</summary>
    [Id(7)]
    public int MaxBytes { get; init; }
}

/// <summary>
/// A contiguous-tail read or a seal as it crosses to the node (OR-I15): the protocol version, the ledger and the replica's
/// window, the caller's known contiguous tail, and whether it seals.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerReplicaTailRequest
{
    /// <summary>Gets or initializes the replica protocol's version.</summary>
    [Id(0)]
    public int Version { get; init; }

    /// <summary>Gets or initializes the ledger's ID.</summary>
    [Id(1)]
    public ulong Ledger { get; init; }

    /// <summary>Gets or initializes the replica's window <c>K</c>.</summary>
    [Id(2)]
    public int WindowSize { get; init; }

    /// <summary>Gets or initializes the caller's known contiguous tail.</summary>
    [Id(3)]
    public long ContiguousTail { get; init; }

    /// <summary>Gets or initializes a value indicating whether the read seals the replica first.</summary>
    [Id(4)]
    public bool Seal { get; init; }
}

/// <summary>
/// A batched per-node read-lease heartbeat as it crosses to the node (OR-I15, LR-I10): the protocol version and the
/// non-empty list of ledger IDs whose RAM-only leases the caller asks to extend.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerReplicaHeartbeatRequest
{
    /// <summary>Gets or initializes the replica protocol's version.</summary>
    [Id(0)]
    public int Version { get; init; }

    /// <summary>Gets or initializes the non-empty list of ledger IDs to heartbeat.</summary>
    [Id(1)]
    public ulong[] Ledgers { get; init; }
}

using System.Globalization;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

#pragma warning disable CA1852 // Accordant generates a virtual clone factory for state types.

/// <summary>
/// The abstract state of a node's replicas under test, one per ledger the spec names, or of the composition's
/// one ledger (ledger-replicas.md §2.1, §2.3).
/// </summary>
[State]
internal partial class LedgerReplicaState
{
    /// <summary>
    /// Gets or sets each ledger's state, by index: ledger 0 is the replica under test's, and ledger 1 another ledger's
    /// on the same node (<see cref="LedgerReplicaSpec.LedgerCount"/>).
    /// </summary>
    public List<LedgerReplicaReplicaState> Ledgers { get; set; } = [new(), new()];
}

/// <summary>
/// The abstract state of one ledger: its DATA, seal, and retained contiguous tail, whether its replica exists, and
/// whether its node dropped it, which leaves the ledger's tombstone (ledger-replicas.md §2.1, §2.3).
/// </summary>
/// <remarks>
/// For a replica, <see cref="Data"/> holds every address it holds DATA at, and <see cref="Sealed"/> its durable seal.
/// For the composition, <see cref="Data"/> holds every address whose DATA <c>Q</c> replicas hold,
/// <see cref="Attempted"/> every other address a writer write may have left DATA at on fewer replicas, and
/// <see cref="Sealed"/> that a seal or recovery succeeded, while <see cref="MaySeal"/> records that one was attempted;
/// <see cref="Exists"/> records that an operation certainly created the replica on some node.
/// </remarks>
[State]
internal partial class LedgerReplicaReplicaState
{
    /// <summary>
    /// Gets or sets the addresses which hold DATA, ascending; each holds its single writer's
    /// <see cref="LedgerReplicaSpec.Payload"/>.
    /// </summary>
    public List<long> Data { get; set; } = [];

    /// <summary>
    /// Gets or sets the composition's addresses whose DATA fewer than <c>Q</c> replicas may hold, ascending: a read there
    /// may answer DATA or missing, and a recovery may find it or end there (ledger-protocol.md §8.3).
    /// </summary>
    public List<long> Attempted { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the node holds the replica: its first write, recovery write, or seal
    /// created it (LR-I7). For the composition, an operation certainly created the replica on some node: one succeeded,
    /// a failed one landed, or a listing named the ledger.
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the replica is sealed (LR-I8), or the composition's seal or recovery
    /// succeeded (LP-I6, LP-I7).
    /// </summary>
    public bool Sealed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the composition attempted a seal or recovery, so some of its replicas may
    /// be sealed.
    /// </summary>
    public bool MaySeal { get; set; }

    /// <summary>
    /// Gets or sets the retained contiguous tail: the greatest an operation carried, or after a restart its rebuild
    /// (LR-I6).
    /// </summary>
    public long Retained { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the node dropped the ledger and holds its tombstone, permanently (LR-I3,
    /// LP-I5).
    /// </summary>
    public bool Dropped { get; set; }

    /// <summary>
    /// Gets or sets, under ambiguity, the greatest contiguous tail a replica's failed call carried: the call may have
    /// reached the replica, which then retained it, so a report may hold any tail up to it (LR-I6, LR-I9).
    /// </summary>
    public long MayRetain { get; set; }
}

#pragma warning restore CA1852

/// <summary>
/// The outcome kinds of <see cref="LedgerReplicaOutcome"/>.
/// </summary>
internal enum LedgerReplicaOutcomeKind
{
    /// <summary>A write reports <see cref="ReplicaWriteOutcome.Written"/>.</summary>
    Written,

    /// <summary>A write reports <see cref="ReplicaWriteOutcome.Sealed"/>.</summary>
    Sealed,

    /// <summary>A read reports <see cref="ReplicaRead.Missing"/>.</summary>
    Missing,

    /// <summary>A read reports a batch.</summary>
    Data,

    /// <summary>A byte-range read reports <see cref="ReplicaRead.OutsideBatch"/> with the batch's length.</summary>
    OutsideBatch,

    /// <summary>A contiguous-tail read reports a <see cref="LedgerReplicaTail"/>.</summary>
    Tail,

    /// <summary>A recovery reports its candidate end.</summary>
    End,

    /// <summary>A drop or restart returns.</summary>
    Done,

    /// <summary>The node lists the ledgers of <see cref="LedgerReplicaOutcome.Listing"/>.</summary>
    Listed,

    /// <summary>The replica rejects an argument with <see cref="ArgumentException"/>.</summary>
    InvalidArgument,

    /// <summary>The replica answers <c>Dropped</c>: its node dropped the ledger (LR-I3).</summary>
    Dropped,

    /// <summary>The replica answers <c>Backpressure</c>, before the operation took effect (outcomes.md §1).</summary>
    Backpressure,

    /// <summary>A transport or replica failure leaves the outcome unknown (ledger-replicas.md §6.4).</summary>
    Unavailable,

    /// <summary>Any other failure, which the contract never allows here.</summary>
    Unexpected,
}

/// <summary>
/// The observed outcome of one <see cref="ILedgerReplica"/> call, recovery, or node action.
/// </summary>
/// <param name="Kind">The outcome's kind.</param>
/// <param name="Position">A contiguous-tail read's contiguous tail, a recovery's candidate end, or the length of a batch a byte-range read's range extends past.</param>
/// <param name="IsSealed">Whether a contiguous-tail read reports a seal.</param>
/// <param name="Bytes">A read's DATA.</param>
/// <param name="Failure">A failure's description.</param>
/// <param name="Listing">
/// The ledgers a listing names, one bit per ledger index, with <see cref="UnknownLedger"/> for a ledger the spec does
/// not name.
/// </param>
internal sealed record LedgerReplicaOutcome(
    LedgerReplicaOutcomeKind Kind, long Position = 0, bool IsSealed = false, byte[]? Bytes = null, string? Failure = null, int Listing = 0)
{
    /// <summary>
    /// The bit of <see cref="Listing"/> for a listed ledger the spec does not name.
    /// </summary>
    internal const int UnknownLedger = 1 << 30;

    /// <summary>
    /// Gets the calls which overlapped this one in real time, as the harness observed them.
    /// </summary>
    public LedgerReplicaOverlap Overlap { get; init; } = LedgerReplicaOverlap.None;

    public override string ToString() => Kind switch
    {
        LedgerReplicaOutcomeKind.Data => Invariant($"Data[{Convert.ToHexString(Bytes!)}]"),
        LedgerReplicaOutcomeKind.Tail => IsSealed ? Invariant($"Tail({Position}, sealed)") : Invariant($"Tail({Position})"),
        LedgerReplicaOutcomeKind.End => Invariant($"End({Position})"),
        LedgerReplicaOutcomeKind.OutsideBatch => Invariant($"OutsideBatch({Position})"),
        LedgerReplicaOutcomeKind.Listed => Invariant($"Listed[{LedgerReplicaSpec.DescribeListing(Listing)}]"),
        LedgerReplicaOutcomeKind.Unavailable or LedgerReplicaOutcomeKind.Unexpected => Invariant($"{Kind}({Failure})"),
        LedgerReplicaOutcomeKind.Written or LedgerReplicaOutcomeKind.Sealed or LedgerReplicaOutcomeKind.Missing or LedgerReplicaOutcomeKind.Done
            or LedgerReplicaOutcomeKind.InvalidArgument or LedgerReplicaOutcomeKind.Dropped or LedgerReplicaOutcomeKind.Backpressure => Kind.ToString(),
        _ => Kind.ToString(),
    };

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);
}

/// <summary>
/// The calls which overlapped a call in real time, as the harness observed them. A replica retains a call's contiguous
/// tail, and creates the replica a write or seal names, when the call reaches it, and the composition answers a writer
/// write from its replicas' answers as they arrive; so these effects show in an overlapping call's outcome although the
/// spec orders the calls one at a time (LP-I4, LR-I6, LR-I7).
/// </summary>
/// <param name="Requests">The requests of the overlapping calls.</param>
internal sealed record LedgerReplicaOverlap(IReadOnlyList<object> Requests)
{
    /// <summary>
    /// Gets the overlap of a call which overlapped none.
    /// </summary>
    internal static LedgerReplicaOverlap None { get; } = new([]);

    /// <summary>
    /// Returns the overlap which also holds <paramref name="request"/>.
    /// </summary>
    internal LedgerReplicaOverlap With(object request) => new([.. Requests, request]);

    /// <summary>
    /// Returns the greatest contiguous tail an overlapping call on ledger <paramref name="ledger"/> carried.
    /// </summary>
    internal long ContiguousTail(int ledger) =>
        Requests.Where(request => LedgerReplicaSpec.LedgerOf(request) == ledger).Select(LedgerReplicaSpec.ContiguousTailOf).DefaultIfEmpty().Max();

    /// <summary>
    /// Returns whether a seal or recovery of ledger <paramref name="ledger"/>, or its removal, whose dropped answers the
    /// composition counts as sealed ones (LP-I5), overlapped the call.
    /// </summary>
    internal bool Seals(int ledger) =>
        Requests.Any(request => LedgerReplicaSpec.LedgerOf(request) == ledger && (LedgerReplicaSpec.IsSealing(request) || request is LedgerReplicaRemove));

    /// <summary>
    /// Returns whether a write, recovery write, seal, or recovery, which creates ledger <paramref name="ledger"/>'s replica,
    /// overlapped the call; a write of an invalid address creates nothing. A recovery seals the composition's replicas,
    /// creating each it reaches.
    /// </summary>
    internal bool Creates(int ledger) =>
        Requests.Any(request => LedgerReplicaSpec.LedgerOf(request) == ledger
            && request is LedgerReplicaWrite { Address: >= 0 and < long.MaxValue } or LedgerReplicaReadTail { Seal: true } or LedgerReplicaRecover);
}

/// <summary>
/// A request on one ledger's replica, by the ledger's index in <see cref="LedgerReplicaState.Ledgers"/>.
/// </summary>
internal interface ILedgerRequest
{
    /// <summary>
    /// Gets the index of the ledger the request names.
    /// </summary>
    int Ledger { get; }
}

/// <summary>
/// A <c>WriteAsync</c> of the address's DATA, <see cref="LedgerReplicaSpec.Payload"/>, carrying a known contiguous tail: a
/// writer write, or with <paramref name="Recovery"/> a recovery write.
/// </summary>
internal sealed record LedgerReplicaWrite(long Address, int Ledger = 0, long ContiguousTail = 0, bool Recovery = false) : ILedgerRequest
{
    public override string ToString() =>
        LedgerReplicaSpec.Describe(Ledger, $"{(Recovery ? "RecoveryWrite" : "Write")}({Address}{LedgerReplicaSpec.DescribeTail(ContiguousTail)})");
}

/// <summary>A <c>WriteAsync</c> of an empty payload, which no DATA is (ledger-replicas.md §3.2).</summary>
internal sealed record LedgerReplicaWriteEmpty(long Address, int Ledger = 0) : ILedgerRequest
{
    public override string ToString() => LedgerReplicaSpec.Describe(Ledger, $"WriteEmpty({Address})");
}

/// <summary>A whole-value <c>ReadAsync</c> carrying a known contiguous tail.</summary>
internal sealed record LedgerReplicaRead(long Address, int Ledger = 0, long ContiguousTail = 0) : ILedgerRequest
{
    public override string ToString() => LedgerReplicaSpec.Describe(Ledger, $"Read({Address}{LedgerReplicaSpec.DescribeTail(ContiguousTail)})");
}

/// <summary>A ranged <c>ReadAsync</c> of bytes <c>[Offset, Offset + Length)</c> carrying a known contiguous tail.</summary>
internal sealed record LedgerReplicaReadRange(long Address, int Offset, int Length, int Ledger = 0, long ContiguousTail = 0) : ILedgerRequest
{
    public override string ToString() =>
        LedgerReplicaSpec.Describe(Ledger, $"ReadRange({Address}, {Offset}, {Length}{LedgerReplicaSpec.DescribeTail(ContiguousTail)})");
}

/// <summary>
/// A <c>ReadTailAsync</c> with the caller's known contiguous tail: a contiguous-tail read, or with
/// <paramref name="Seal"/> a seal.
/// </summary>
internal sealed record LedgerReplicaReadTail(long ContiguousTail, bool Seal = false, int Ledger = 0) : ILedgerRequest
{
    public override string ToString() => LedgerReplicaSpec.Describe(Ledger, $"{(Seal ? "Seal" : "ReadTail")}({ContiguousTail})");
}

/// <summary>
/// The composition's <c>RecoverAsync</c> with the caller's known contiguous tail (ledger-protocol.md §8.3).
/// </summary>
internal sealed record LedgerReplicaRecover(long ContiguousTail)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Recover({ContiguousTail})");
}

/// <summary>
/// The node's process, or its operating system, crashes, and the node reopens its replicas: every durable answer
/// survives, and each retained contiguous tail is rebuilt (LR-I6).
/// </summary>
internal sealed record LedgerReplicaRestart(bool OperatingSystem)
{
    public override string ToString() => OperatingSystem ? "the operating system crashes and the store restarts" : "the store crashes and restarts";
}

/// <summary>
/// The node drops the ledger through its <c>ILedgerStore.DropReplicaAsync</c>, removing its replica, if held, and
/// leaving its tombstone (LR-I3).
/// </summary>
internal sealed record LedgerReplicaDropReplica(int Ledger = 0) : ILedgerRequest
{
    public override string ToString() => LedgerReplicaSpec.Describe(Ledger, $"DropReplica");
}

/// <summary>
/// The node lists its replicas through its <c>ILedgerStore.ListReplicasAsync</c>; the outcome is the ledgers it lists
/// (ledger-replicas.md §2.3). For the composition, the outcome is the ledgers any of its nodes lists.
/// </summary>
internal sealed record LedgerReplicaListReplicas
{
    internal static LedgerReplicaListReplicas Instance { get; } = new();

    public override string ToString() => "ListReplicas";
}

/// <summary>
/// The ledger leaves the map, and every node drops it (LR-I3, LP-I5).
/// </summary>
internal sealed record LedgerReplicaRemove
{
    internal static LedgerReplicaRemove Instance { get; } = new();

    public override string ToString() => "Remove";
}

/// <summary>
/// The role of a replica under the spec (ledger-replicas.md §2.1).
/// </summary>
internal enum LedgerReplicaRole
{
    /// <summary>
    /// A ledger replica: it meets §8.1 for every request, with no caller precondition.
    /// </summary>
    Replica,

    /// <summary>
    /// The composition, a Ledger over a ledger's replicas behind a replica-shaped adapter: its caller keeps the requirements
    /// of §3.2, and it recovers.
    /// </summary>
    Composition,
}

/// <summary>
/// What a spec of <see cref="ILedgerReplica"/> allows beside the contract's outcomes.
/// </summary>
internal sealed record LedgerReplicaSpecOptions
{
    /// <summary>
    /// Gets the replica's window <c>K</c>.
    /// </summary>
    public required int WindowSize { get; init; }

    /// <summary>
    /// Gets the replica's role, which decides the caller's preconditions and the operations it has.
    /// </summary>
    public required LedgerReplicaRole Role { get; init; }

    /// <summary>
    /// Gets a value indicating whether an operation may fail as unavailable: a failed mutation may still take effect,
    /// at most once, possibly later, as a transport or replica failure leaves it (ledger-replicas.md §6.1, §6.4). A
    /// replica reached over a transport fails so when no answer comes back from its node (LR-I9); the call may have
    /// reached the replica, retaining its contiguous tail, or not.
    /// </summary>
    public bool Ambiguous { get; init; }

    /// <summary>
    /// Gets a value indicating whether an operation may answer <c>Backpressure</c> (outcomes.md §1): a replica's comes
    /// before it retains or writes anything, so it takes no effect; the composition's asserts nothing, so a mutation it
    /// answers may still take effect, as an unavailable one may, and its recovery repeats rather than answers it.
    /// </summary>
    public bool Backpressure { get; init; }

    /// <summary>
    /// Gets the composition's replica count <c>N</c>.
    /// </summary>
    public int ReplicaCount { get; init; } = 1;

    /// <summary>
    /// Gets the composition's quorum <c>Q</c>.
    /// </summary>
    public int Quorum { get; init; } = 1;
}

/// <summary>
/// The executable spec of <see cref="ILedgerReplica"/> (ledger-replicas.md §2.1, §5, §8): create-once DATA registers (LR-I1),
/// durable answers (LR-I4), the positionless seal a writer write meets and a recovery write passes (LR-I8), the
/// retained contiguous tail and its rebuild after a restart (LR-I6), the node's tombstones (LR-I3, LP-I5, LR-I7), and
/// for the composition its writes at <c>Q</c> (LP-I4), seal and seal check (LP-I6, LP-I9), and recovery
/// (LP-I7). Every provider and the composition meet it.
/// </summary>
/// <remarks>
/// <para>
/// A replica's node holds <see cref="LedgerCount"/> ledgers, each with its own replica, so a drop, creation, or listing
/// which reaches the wrong ledger shows. Each address has one writer, so its DATA is <see cref="Payload"/>. A replica's
/// first write, recovery write, or seal creates it unless the node dropped the ledger; a read or contiguous-tail read of
/// one the node neither holds nor dropped answers as an empty one, creates nothing, and retains nothing; the node lists
/// exactly the replicas it holds; and once the node drops a ledger, every operation on it refuses, for good, while its
/// other ledgers serve on. A replica meets §8.1 for every request, since the ledger's contiguous tail and window bound
/// what its composition sends, not what a lagging replica holds.
/// </para>
/// <para>
/// The composition, a Ledger over a ledger's replicas, serves one ledger's caller, which passes known contiguous tails no
/// greater than the ledger's and keeps the window discipline (§3.2), which <see cref="IsCallerAllowed"/> checks. Its
/// writes report <c>Written</c> at <c>Q</c> replicas; a writer write which meets a seal or a dropped replica reports
/// <c>Sealed</c> and may have left its DATA on fewer. A recovery ends at a position no writer write reaches <c>Q</c>
/// replicas at, with every lower position it read on <c>Q</c> replicas, and seals the ledger.
/// </para>
/// <para>
/// A replica retains a call's contiguous tail, and creates the replica a write or seal names, when the call reaches it,
/// before its outcome, and the composition answers a writer write from its replicas' answers as they arrive. So a
/// contiguous-tail report may hold a greater tail which a call overlapping it in real time carried, a listing may name a
/// replica which an overlapping call created before a drop refused it, a read of a replica the node did not hold may find
/// the replica an overlapping write or seal created, empty, and leave its contiguous tail retained there, and a writer
/// write which a seal or recovery overlaps may answer <c>Sealed</c> after leaving DATA its recovery finds; the outcome's
/// <see cref="LedgerReplicaOutcome.Overlap"/>, which the harness observes, bounds each. A call which overlaps none is checked
/// exactly.
/// </para>
/// <para>
/// Under <see cref="LedgerReplicaSpecOptions.Ambiguous"/>, a failed operation's effect is unknown: a failed write or seal
/// is a pending step function which may take effect at any later point, at most once, or never (§6.1), unless the
/// replica is refused by then (LR-I7). A replica reached over a transport fails so when no answer comes back from its
/// node (LR-I9): the call may have reached it, creating it with a landed write or seal and retaining its contiguous tail,
/// so a later report may hold any tail up to the greatest a failed call carried. A replica's node drops and lists through its
/// <c>ILedgerStore</c>; the composition's ledger leaves the map in one action, which drops it on every node,
/// and its listing is the union of its nodes', which holds the ledger once an operation created it and until it
/// leaves the map, and may hold it after a failed one, from the first listing which names it.
/// </para>
/// </remarks>
internal static class LedgerReplicaSpec
{
    /// <summary>
    /// The number of ledgers a replica's node holds under test.
    /// </summary>
    internal const int LedgerCount = 2;

    /// <summary>
    /// Returns the DATA the single writer of <paramref name="address"/> writes: two to four bytes which identify it.
    /// </summary>
    internal static byte[] Payload(long address) =>
        [.. Enumerable.Range(0, 2 + (int)(address % 3)).Select(index => unchecked((byte)(address * 37 + index * 11 + 1)))];

    /// <summary>
    /// Returns the index of the ledger <paramref name="request"/> names; a request which names none is the replica under
    /// test's, ledger 0.
    /// </summary>
    internal static int LedgerOf(object request) => request is ILedgerRequest ledgerRequest ? ledgerRequest.Ledger : 0;

    /// <summary>
    /// Returns the known contiguous tail <paramref name="request"/> carries, or 0 when it carries none.
    /// </summary>
    internal static long ContiguousTailOf(object request) => request switch
    {
        LedgerReplicaWrite write => write.ContiguousTail,
        LedgerReplicaRead read => read.ContiguousTail,
        LedgerReplicaReadRange read => read.ContiguousTail,
        LedgerReplicaReadTail read => read.ContiguousTail,
        LedgerReplicaRecover recover => recover.ContiguousTail,
        _ => 0,
    };

    /// <summary>
    /// Returns whether <paramref name="request"/> seals: a sealing contiguous-tail read or a recovery.
    /// </summary>
    internal static bool IsSealing(object request) => request is LedgerReplicaReadTail { Seal: true } or LedgerReplicaRecover;

    /// <summary>
    /// Describes a request on ledger <paramref name="ledger"/>: ledger 0's plainly, and another's with its name.
    /// </summary>
    internal static string Describe(int ledger, FormattableString call) =>
        ledger == 0 ? Invariant(call) : string.Concat(Name(ledger), ".", Invariant(call));

    /// <summary>
    /// Describes a request's known contiguous tail, which is omitted when it is 0.
    /// </summary>
    internal static string DescribeTail(long contiguousTail) => contiguousTail == 0 ? string.Empty : Invariant($", tail {contiguousTail}");

    /// <summary>
    /// Describes the ledgers of a listing's <see cref="LedgerReplicaOutcome.Listing"/>.
    /// </summary>
    internal static string DescribeListing(int listing) =>
        string.Join(
            ", ",
            Enumerable.Range(0, LedgerCount).Where(ledger => (listing & (1 << ledger)) != 0).Select(Name)
                .Concat((listing & LedgerReplicaOutcome.UnknownLedger) != 0 ? ["?"] : []));

    /// <summary>
    /// Returns the first address which does not hold DATA: for the composition, the first not on <c>Q</c> replicas, the
    /// ledger's contiguous tail.
    /// </summary>
    internal static long ContiguousTail(LedgerReplicaReplicaState replica)
    {
        var address = 0L;
        while (Holds(replica, address))
        {
            address++;
        }

        return address;
    }

    /// <summary>
    /// Returns whether the caller may issue <paramref name="request"/> in <paramref name="state"/>. A replica's caller
    /// may issue any request (ledger-replicas.md §2.1). The composition's caller keeps §3.2: every contiguous tail it
    /// passes is at most the ledger's, and a write keeps the window discipline, its address below the known contiguous
    /// tail it carries, the greater of its own and the composition's retained one, plus the window (LP-I11). A refused
    /// replica refuses every request, so any is allowed.
    /// </summary>
    internal static bool IsCallerAllowed(object request, LedgerReplicaState state, LedgerReplicaSpecOptions options)
    {
        // A Ledger, the composition, seals and sends recovery writes only in its recovery (ledger-protocol.md §2.1).
        if (options.Role == LedgerReplicaRole.Composition && request is LedgerReplicaWrite { Recovery: true } or LedgerReplicaReadTail { Seal: true })
        {
            return false;
        }

        var replica = state.Ledgers[LedgerOf(request)];
        if (options.Role == LedgerReplicaRole.Replica || replica.Dropped)
        {
            return true;
        }

        var tail = ContiguousTail(replica);
        return request switch
        {
            LedgerReplicaWrite write => write.ContiguousTail <= tail
                && (!IsValid(write.Address) || write.Address < Math.Max(replica.Retained, write.ContiguousTail) + options.WindowSize),
            LedgerReplicaRead read => read.ContiguousTail <= tail,
            LedgerReplicaReadRange read => read.ContiguousTail <= tail,
            LedgerReplicaReadTail read => read.ContiguousTail <= tail,
            LedgerReplicaRecover recover => recover.ContiguousTail <= tail,
            _ => true,
        };
    }

    /// <summary>
    /// Creates the spec. Only a replica's node restarts, drops, and lists one replica; only the composition recovers and
    /// leaves the map.
    /// </summary>
    internal static Spec<LedgerReplicaState> Create(LedgerReplicaSpecOptions options)
    {
        var spec = new Spec<LedgerReplicaState>();
        var failures = new Failures(options);
        var composition = options.Role == LedgerReplicaRole.Composition;

        // LR-I1, LR-I4, LR-I8: the first write decides the register, a writer write which meets the seal stores nothing,
        // and a write reports what occupies the address after it.
        spec.Operation<LedgerReplicaWrite, LedgerReplicaOutcome>("Write", (request, state) =>
            Write(request, state, composition, failures));

        // ledger-replicas.md §3.2: an empty payload is never DATA, and no provider stores it.
        spec.Operation<LedgerReplicaWriteEmpty, LedgerReplicaOutcome>("WriteEmpty", static (request, state) => Failures.Invalid(state.Ledgers[request.Ledger]));

        // LR-I2, LR-I4: a read classifies the address as missing or DATA from durable state, and creates nothing
        // (LR-I7); it retains its contiguous tail on a replica which exists (LR-I6).
        spec.Operation<LedgerReplicaRead, LedgerReplicaOutcome>("Read", (request, state) =>
            Read(request.Ledger, request.Address, request.ContiguousTail, state, options, failures, static payload => IsData(payload)));

        // ledger-replicas.md §2.1: a byte-range read returns the range of DATA, and the DATA's length for a range past it.
        spec.Operation<LedgerReplicaReadRange, LedgerReplicaOutcome>("ReadRange", (request, state) =>
            Read(
                request.Ledger,
                request.Address,
                request.ContiguousTail,
                state,
                options,
                failures,
                payload => request.Offset + request.Length <= payload.Length
                    ? IsData(payload[request.Offset..(request.Offset + request.Length)])
                    : IsOutsideBatch(payload.Length)));

        // LR-I6, LR-I8, LP-I6, LP-I9: a contiguous-tail read reports the retained contiguous tail after it and the
        // seal; a seal seals first, creating the replica.
        spec.Operation<LedgerReplicaReadTail, LedgerReplicaOutcome>("ReadTail", (request, state) =>
            ReadTail(request, state, composition, failures));

        if (composition)
        {
            // LP-I7: a recovery seals the replicas and ends at a position N − Q + 1 sealed replicas lack, after copying
            // every lower position it read to Q replicas; it reads from the contiguous tail c it retains.
            spec.Operation<LedgerReplicaRecover, LedgerReplicaOutcome>("Recover", (request, state) =>
                Recover(request, state, options, failures));

            // LR-I3, LP-I5: once the ledger leaves the map and every node drops it, the composition refuses every
            // operation but a writer write and a contiguous-tail read, which count a dropped answer as a sealed one.
            spec.Operation<LedgerReplicaRemove, LedgerReplicaOutcome>("Remove", static (_, _) =>
                Is(LedgerReplicaOutcomeKind.Done).ThenState<LedgerReplicaState>(static next => next.Ledgers[0].Dropped = true));

            // ledger-replicas.md §2.3: some node lists the ledger once an operation created it and until it leaves the map,
            // and none lists it before an operation reached a node. A failed write, seal, or recovery may have reached
            // some, which a listing which names the ledger shows; it stays listed until the ledger leaves the map.
            spec.Operation<LedgerReplicaListReplicas, LedgerReplicaOutcome>("ListReplicas", (_, state) =>
            {
                var replica = state.Ledgers[0];
                if (IsListed(replica))
                {
                    return IsListing(1).SameState();
                }

                var absent = IsAbsent(replica) ? 1 : 0;
                var mayHaveCreated = !replica.Dropped && (replica.Attempted.Count > 0 || replica.MaySeal);
                return mayHaveCreated
                    ? Expect.OneOf(IsListing(0, absent).SameState(), IsListing(1).ThenState<LedgerReplicaState>(static next => next.Ledgers[0].Exists = true))
                    : IsListing(0, absent).SameState();
            });
        }
        else
        {
            // LR-I6: a restart keeps every durable answer and rebuilds each retained contiguous tail from the replica's
            // highest DATA and its window.
            spec.Operation<LedgerReplicaRestart, LedgerReplicaOutcome>("Restart", (_, _) =>
                Is(LedgerReplicaOutcomeKind.Done).ThenState<LedgerReplicaState>(next =>
                {
                    foreach (var replica in next.Ledgers.Where(static replica => replica.Exists && !replica.Dropped))
                    {
                        replica.Retained = Math.Max(0, (replica.Data.Count == 0 ? -1 : replica.Data[^1]) + 1 - options.WindowSize);
                    }
                }));

            // LR-I3: a drop leaves the ledger's tombstone, whether or not the node held its replica; a repeat changes
            // nothing, and the node's other ledgers are untouched.
            spec.Operation<LedgerReplicaDropReplica, LedgerReplicaOutcome>(
                "DropReplica", static (request, _) => Is(LedgerReplicaOutcomeKind.Done).ThenState<LedgerReplicaState>(next => next.Ledgers[request.Ledger].Dropped = true));

            // ledger-replicas.md §2.3: the node lists exactly the replicas it holds, which a drop removes.
            spec.Operation<LedgerReplicaListReplicas, LedgerReplicaOutcome>("ListReplicas", static (_, state) =>
                IsListing(Ledgers(state, IsListed), Ledgers(state, IsAbsent)).SameState());
        }

        return spec;
    }

    private static ExpectedOutcomes Write(LedgerReplicaWrite request, LedgerReplicaState state, bool composition, Failures failures)
    {
        var (ledger, address, recovery) = (request.Ledger, request.Address, request.Recovery);
        var replica = state.Ledgers[ledger];
        if (!IsValid(address))
        {
            return Failures.Invalid(replica);
        }

        var retained = Math.Max(replica.Retained, request.ContiguousTail);
        var pending = Pending.Writing(ledger, address, recovery);
        if (replica.Dropped)
        {
            // LP-I5: the composition counts a dropped answer to a writer write as a sealed one.
            if (composition && !recovery)
            {
                return Expect.OneOf([At(Is(LedgerReplicaOutcomeKind.Sealed), ledger, Retain(retained)), .. failures.Of(ledger, replica, pending, Retain(retained))]);
            }

            return failures.Refused(ledger, composition ? Retain(retained) : null);
        }

        void Written(LedgerReplicaReplicaState next) => AddData(Create(next, retained), address);

        void Refused(LedgerReplicaReplicaState next)
        {
            Create(next, retained);
            if (composition)
            {
                // A replica which the seal had not yet reached may take the write.
                AddAttempted(next, address);
            }
        }

        // The model records no certain creation for a failed composition write: it may have created the replica on some
        // nodes, and left its DATA on fewer than Q replicas; its pending step creates the replica if it lands (LR-I7).
        void Failed(LedgerReplicaReplicaState next)
        {
            Retain(retained)(next);
            AddAttempted(next, address);
        }

        // A writer write meets a seal on a replica, or, on the composition, a seal any replica it reaches holds. The
        // composition answers from its replicas' answers as they arrive, so a seal or recovery which overlaps a writer
        // write may seal some replicas after the write left its DATA on others, which that recovery may find (LP-I4).
        ExpectedOutcome[] outcomes;
        if (recovery || !replica.Sealed && !(composition && replica.MaySeal))
        {
            outcomes = composition && !recovery
                ? [At(Is(LedgerReplicaOutcomeKind.Written), ledger, Written), At(MeetsAnOverlappingSeal(ledger), ledger, Refused)]
                : [At(Is(LedgerReplicaOutcomeKind.Written), ledger, Written)];
        }
        else if (replica.Sealed)
        {
            outcomes = [At(Is(LedgerReplicaOutcomeKind.Sealed), ledger, Refused)];
        }
        else
        {
            outcomes = [At(Is(LedgerReplicaOutcomeKind.Written), ledger, Written), At(Is(LedgerReplicaOutcomeKind.Sealed), ledger, Refused)];
        }

        return Expect.OneOf([.. outcomes, .. failures.Of(ledger, replica, pending, Failed, carried: retained)]);
    }

    private static ExpectedOutcomes Read(
        int ledger,
        long address,
        long contiguousTail,
        LedgerReplicaState state,
        LedgerReplicaSpecOptions options,
        Failures failures,
        Func<byte[], ExpectedOutcomeBuilder<LedgerReplicaOutcome>> data)
    {
        var composition = options.Role == LedgerReplicaRole.Composition;
        var replica = state.Ledgers[ledger];
        if (!IsValid(address))
        {
            return Failures.Invalid(replica);
        }

        var known = Math.Max(replica.Retained, contiguousTail);
        var retain = Retain(known);
        if (replica.Dropped)
        {
            return failures.Refused(ledger, composition ? retain : null);
        }

        if (!replica.Exists && !composition)
        {
            // LR-I7: a creating write or seal publishes the replica before it takes effect, so a read which overlaps one may
            // find the created, empty replica, which retains the read's contiguous tail (LR-I6). A failed read may still
            // reach the node once a later call created the replica, and retain its tail there (LR-I9).
            return Expect.OneOf(
            [
                Is(LedgerReplicaOutcomeKind.Missing).SameState(),
                At(IsMissingOnAnOverlappingCreation(ledger), ledger, next => Create(next, known)),
                .. failures.Of(ledger, replica, pending: null, immediate: null, carried: known),
            ]);
        }

        // The composition answers missing at or above its known contiguous tail once Q replicas do, so DATA fewer than Q
        // replicas hold answers either way, and so does DATA Q hold when the N − Q others are as many (2Q ≤ N).
        var eitherWay = replica.Attempted.Contains(address)
            || composition && Holds(replica, address) && address >= known && 2 * options.Quorum <= options.ReplicaCount;
        ExpectedOutcomeBuilder<LedgerReplicaOutcome>[] answers;
        if (eitherWay)
        {
            answers = [data(Payload(address)), Is(LedgerReplicaOutcomeKind.Missing)];
        }
        else
        {
            answers = Holds(replica, address) ? [data(Payload(address))] : [Is(LedgerReplicaOutcomeKind.Missing)];
        }

        return Expect.OneOf([.. answers.Select(answer => At(answer, ledger, retain)), .. failures.Of(ledger, replica, pending: null, retain, carried: known)]);
    }

    private static ExpectedOutcomes ReadTail(LedgerReplicaReadTail request, LedgerReplicaState state, bool composition, Failures failures)
    {
        var ledger = request.Ledger;
        var replica = state.Ledgers[ledger];
        var retained = Math.Max(replica.Retained, request.ContiguousTail);
        var retain = Retain(retained);
        if (replica.Dropped)
        {
            // LP-I5: the composition counts a dropped answer as a sealed one, which carries no contiguous tail.
            return composition
                ? Expect.OneOf([At(IsReport(ledger, retained, isSealed: true), ledger, retain), .. failures.Of(ledger, replica, pending: null, retain)])
                : failures.Refused(ledger, immediate: null);
        }

        if (request.Seal)
        {
            void Sealed(LedgerReplicaReplicaState next)
            {
                Create(next, retained).Sealed = true;
                next.MaySeal = true;
            }

            return Expect.OneOf(
            [
                At(IsReport(ledger, retained, isSealed: true, replica.MayRetain), ledger, Sealed),
                .. failures.Of(ledger, replica, Pending.Seal(ledger), MaySeal(retained), carried: retained),
            ]);
        }

        if (!replica.Exists && !composition)
        {
            // LR-I7: a contiguous-tail read which overlaps a creating write or seal may find the created replica, empty and
            // unsealed, which retains its argument (LR-I6). A failed one may still reach the node once a later call created
            // the replica, and retain its argument there (LR-I9).
            return Expect.OneOf(
            [
                IsTail(request.ContiguousTail, isSealed: false).SameState(),
                At(IsReportOnAnOverlappingCreation(ledger, retained, replica.MayRetain), ledger, next => Create(next, retained)),
                .. failures.Of(ledger, replica, pending: null, immediate: null, carried: retained),
            ]);
        }

        // The composition's seal check reports any seal its Q answers hold, so an attempted seal may show.
        ExpectedOutcome[] reports;
        var bound = replica.MayRetain;
        if (replica.Sealed)
        {
            reports = [At(IsReport(ledger, retained, isSealed: true, bound), ledger, retain)];
        }
        else if (composition && replica.MaySeal)
        {
            reports = [At(IsReport(ledger, retained, isSealed: true, bound), ledger, retain), At(IsReport(ledger, retained, isSealed: false, bound), ledger, retain)];
        }
        else
        {
            reports = [At(IsReport(ledger, retained, isSealed: false, bound), ledger, retain)];
        }

        return Expect.OneOf([.. reports, .. failures.Of(ledger, replica, pending: null, retain, carried: retained)]);
    }

    private static ExpectedOutcomes Recover(LedgerReplicaRecover request, LedgerReplicaState state, LedgerReplicaSpecOptions options, Failures failures)
    {
        var replica = state.Ledgers[0];
        var retained = Math.Max(replica.Retained, request.ContiguousTail);
        if (replica.Dropped)
        {
            return failures.Refused(0, Retain(retained));
        }

        // Each position from c is found when some replica holds it and ends the recovery when N − Q + 1 sealed replicas
        // lack it, which no position Q replicas hold can; every writer write lies below c + K (LP-I11).
        List<ExpectedOutcome> ends = [];
        for (var end = retained; end <= retained + 2L * options.WindowSize; end++)
        {
            if (!Holds(replica, end))
            {
                var recovered = end;
                ends.Add(At(IsEnd(recovered), 0, next =>
                {
                    Create(next, retained);
                    for (var address = retained; address < recovered; address++)
                    {
                        AddData(next, address);
                    }

                    next.Sealed = true;
                    next.MaySeal = true;
                }));
            }

            // The recovery reads past a position only once some replica reports DATA there.
            if (!Holds(replica, end) && !replica.Attempted.Contains(end))
            {
                break;
            }
        }

        // outcomes.md §1: a recovery repeats a backpressure answer rather than giving one.
        return Expect.OneOf([.. ends, .. failures.Of(0, replica, Pending.Seal(0), MaySeal(retained), backpressure: false)]);
    }

    /// <summary>
    /// Records a composition's failed seal or recovery, which may have sealed some replicas. The model records no certain
    /// creation: it may have created the replica on some nodes, and its pending step creates and seals the replica if it
    /// lands (LR-I7).
    /// </summary>
    private static Action<LedgerReplicaReplicaState> MaySeal(long retained) => next =>
    {
        Retain(retained)(next);
        next.MaySeal = true;
    };

    /// <summary>
    /// Returns whether the replica holds DATA at <paramref name="address"/>, or, for the composition, <c>Q</c> replicas do.
    /// </summary>
    private static bool Holds(LedgerReplicaReplicaState replica, long address) => replica.Data.BinarySearch(address) >= 0;

    private static bool IsListed(LedgerReplicaReplicaState replica) => replica.Exists && !replica.Dropped;

    private static string Name(int ledger) => Invariant($"S{ledger + 1}");

    private static bool IsValid(long address) => address is >= 0 and < long.MaxValue;

    private static Action<LedgerReplicaReplicaState> Retain(long retained) => replica => replica.Retained = Math.Max(replica.Retained, retained);

    /// <summary>
    /// Records that a mutation created the replica, or found it, retaining <paramref name="retained"/> (LR-I6, LR-I7).
    /// </summary>
    private static LedgerReplicaReplicaState Create(LedgerReplicaReplicaState replica, long retained)
    {
        replica.Exists = true;
        replica.Retained = Math.Max(replica.Retained, retained);
        return replica;
    }

    private static void AddData(LedgerReplicaReplicaState replica, long address)
    {
        var index = replica.Data.BinarySearch(address);
        if (index < 0)
        {
            replica.Data.Insert(~index, address);
        }

        _ = replica.Attempted.Remove(address);
    }

    private static void AddAttempted(LedgerReplicaReplicaState replica, long address)
    {
        var index = replica.Attempted.BinarySearch(address);
        if (index < 0 && !Holds(replica, address))
        {
            replica.Attempted.Insert(~index, address);
        }
    }

    /// <summary>
    /// Returns <paramref name="outcome"/> with <paramref name="effect"/> applied to ledger <paramref name="ledger"/>.
    /// </summary>
    private static ExpectedOutcome At(ExpectedOutcomeBuilder<LedgerReplicaOutcome> outcome, int ledger, Action<LedgerReplicaReplicaState> effect) =>
        outcome.ThenState<LedgerReplicaState>(next => effect(next.Ledgers[ledger]));

    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> Is(LedgerReplicaOutcomeKind kind) =>
        Expect.That<LedgerReplicaOutcome>(outcome => outcome.Kind == kind, Invariant($"Expected {kind}."));

    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> IsOutsideBatch(int length) =>
        Expect.That<LedgerReplicaOutcome>(
            outcome => outcome.Kind == LedgerReplicaOutcomeKind.OutsideBatch && outcome.Position == length,
            Invariant($"Expected OutsideBatch({length})."));

    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> IsData(byte[] bytes) =>
        Expect.That<LedgerReplicaOutcome>(
            outcome => outcome.Kind == LedgerReplicaOutcomeKind.Data && outcome.Bytes.AsSpan().SequenceEqual(bytes),
            Invariant($"Expected Data[{Convert.ToHexString(bytes)}]."));

    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> IsTail(long position, bool isSealed) =>
        Expect.That<LedgerReplicaOutcome>(
            outcome => outcome.Kind == LedgerReplicaOutcomeKind.Tail && outcome.Position == position && outcome.IsSealed == isSealed,
            Invariant($"Expected {new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, position, isSealed)}."));

    /// <summary>
    /// Expects a report of the retained contiguous tail <paramref name="retained"/>, or of a greater one an overlapping
    /// call on ledger <paramref name="ledger"/> carried, or, under ambiguity, one up to <paramref name="bound"/>, which a
    /// failed call may have left, since a replica retains a call's contiguous tail when the call reaches it (LR-I6).
    /// </summary>
    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> IsReport(int ledger, long retained, bool isSealed, long bound = 0) =>
        Expect.That<LedgerReplicaOutcome>(
            outcome => Reports(outcome, ledger, retained, isSealed, bound),
            Invariant($"Expected {new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, retained, isSealed)}, or a greater contiguous tail an overlapping or failed call carried."));

    private static bool Reports(LedgerReplicaOutcome outcome, int ledger, long retained, bool isSealed, long bound = 0) =>
        outcome.Kind == LedgerReplicaOutcomeKind.Tail
        && outcome.IsSealed == isSealed
        && outcome.Position >= retained
        && outcome.Position <= Math.Max(Math.Max(retained, bound), outcome.Overlap.ContiguousTail(ledger));

    /// <summary>
    /// Expects a read of a replica the node did not hold to answer <c>Missing</c> from the replica an overlapping write or
    /// seal created before taking effect (LR-I7).
    /// </summary>
    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> IsMissingOnAnOverlappingCreation(int ledger) =>
        Expect.That<LedgerReplicaOutcome>(
            outcome => outcome.Kind == LedgerReplicaOutcomeKind.Missing && outcome.Overlap.Creates(ledger),
            "Expected Missing from the empty replica an overlapping write or seal created.");

    /// <summary>
    /// Expects a contiguous-tail read of a replica the node did not hold to report, unsealed, the retained contiguous tail
    /// of the replica an overlapping write or seal created before taking effect (LR-I6, LR-I7).
    /// </summary>
    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> IsReportOnAnOverlappingCreation(int ledger, long retained, long bound) =>
        Expect.That<LedgerReplicaOutcome>(
            outcome => outcome.Overlap.Creates(ledger) && Reports(outcome, ledger, retained, isSealed: false, bound),
            Invariant($"Expected {new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Tail, retained)} from the replica an overlapping write or seal created."));

    /// <summary>
    /// Expects a writer write of the composition to answer <c>Sealed</c> when a seal or recovery overlapped it.
    /// </summary>
    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> MeetsAnOverlappingSeal(int ledger) =>
        Expect.That<LedgerReplicaOutcome>(
            outcome => outcome.Kind == LedgerReplicaOutcomeKind.Sealed && outcome.Overlap.Seals(ledger),
            "Expected Written, or Sealed when a seal, recovery, or removal overlapped the write.");

    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> IsEnd(long position) =>
        Expect.That<LedgerReplicaOutcome>(
            outcome => outcome.Kind == LedgerReplicaOutcomeKind.End && outcome.Position == position,
            Invariant($"Expected {new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.End, position)}."));

    /// <summary>
    /// Expects a listing of the ledgers <paramref name="listed"/>, which may also name a ledger of
    /// <paramref name="absent"/> which an overlapping write, recovery write, seal, or recovery creates (LR-I7).
    /// </summary>
    private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> IsListing(int listed, int absent = 0) =>
        Expect.That<LedgerReplicaOutcome>(
            outcome => outcome.Kind == LedgerReplicaOutcomeKind.Listed
                && (outcome.Listing & listed) == listed
                && (outcome.Listing & ~Created(outcome.Overlap, absent)) == listed,
            Invariant($"Expected {new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Listed, Listing: listed)}, or with a ledger an overlapping call created."));

    /// <summary>
    /// Returns the ledgers of <paramref name="absent"/> which a call of <paramref name="overlap"/> creates.
    /// </summary>
    private static int Created(LedgerReplicaOverlap overlap, int absent) =>
        Enumerable.Range(0, LedgerCount).Where(ledger => (absent & (1 << ledger)) != 0 && overlap.Creates(ledger)).Sum(static ledger => 1 << ledger);

    /// <summary>
    /// Returns the bits of the ledgers which <paramref name="select"/> selects.
    /// </summary>
    private static int Ledgers(LedgerReplicaState state, Func<LedgerReplicaReplicaState, bool> select) =>
        Enumerable.Range(0, LedgerCount).Where(ledger => select(state.Ledgers[ledger])).Sum(static ledger => 1 << ledger);

    private static bool IsAbsent(LedgerReplicaReplicaState replica) => !replica.Exists && !replica.Dropped;

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// The failures a replica may report beside an operation's outcome under <see cref="LedgerReplicaSpecOptions"/>.
    /// </summary>
    private sealed class Failures(LedgerReplicaSpecOptions options)
    {
        /// <summary>
        /// Returns the outcomes of a request with an invalid argument: the replica rejects it (ledger-replicas.md §2.1, §3.2), and
        /// a refused replica may instead refuse it (LR-I3, LR-I7), since the contract orders neither check first.
        /// </summary>
        internal static ExpectedOutcomes Invalid(LedgerReplicaReplicaState replica) =>
            replica.Dropped
                ? Expect.OneOf(Is(LedgerReplicaOutcomeKind.InvalidArgument).SameState(), Is(LedgerReplicaOutcomeKind.Dropped).SameState())
                : Is(LedgerReplicaOutcomeKind.InvalidArgument).SameState();

        /// <summary>
        /// Returns the outcomes of an operation on a refused replica, which applies <paramref name="immediate"/>: it
        /// refuses, or under ambiguity fails (LP-I5).
        /// </summary>
        internal ExpectedOutcomes Refused(int ledger, Action<LedgerReplicaReplicaState>? immediate) =>
            Expect.OneOf([Apply(Is(LedgerReplicaOutcomeKind.Dropped), ledger, immediate).Build(), .. Of(ledger, new LedgerReplicaReplicaState { Dropped = true }, pending: null, immediate)]);

        /// <summary>
        /// Returns the failures an operation on <paramref name="replica"/> may report: a replica's backpressure takes no
        /// effect, while the composition's, unless the operation repeats it (<paramref name="backpressure"/> unset), and an
        /// unavailable failure apply <paramref name="immediate"/> and leave a mutation <paramref name="pending"/>. A
        /// replica's unavailable failure applies nothing certain: its call may have reached the replica, which then
        /// retained <paramref name="carried"/>, the contiguous tail it carried, and its mutation may land later.
        /// </summary>
        internal IEnumerable<ExpectedOutcome> Of(
            int ledger,
            LedgerReplicaReplicaState replica,
            IStepFunction? pending,
            Action<LedgerReplicaReplicaState>? immediate,
            bool backpressure = true,
            long carried = 0)
        {
            if (options.Backpressure && backpressure)
            {
                if (options.Role == LedgerReplicaRole.Composition)
                {
                    // outcomes.md §1: the composition's backpressure asserts nothing; a write may have left DATA on fewer
                    // than Q replicas, which a later recovery may copy (LP-I12).
                    var answered = Apply(Is(LedgerReplicaOutcomeKind.Backpressure), ledger, immediate);
                    yield return pending is null ? answered : answered.Triggers(pending);
                }
                else
                {
                    yield return Is(LedgerReplicaOutcomeKind.Backpressure).SameState();
                }
            }

            if (!options.Ambiguous)
            {
                yield break;
            }

            // LP-I5: a Dropped answer comes only from Refused, once the ledger left the map: an operation which a removal
            // overlaps is ordered after it, and a writer write and a seal check count a dropped answer as a sealed one.
            var failure = options.Role == LedgerReplicaRole.Composition
                ? Apply(Is(LedgerReplicaOutcomeKind.Unavailable), ledger, immediate)
                : Apply(Is(LedgerReplicaOutcomeKind.Unavailable), ledger, next => next.MayRetain = Math.Max(next.MayRetain, carried));
            yield return pending is null ? failure : failure.Triggers(pending);
        }

        private static ExpectedOutcomeBuilder<LedgerReplicaOutcome> Apply(
            ExpectedOutcomeBuilder<LedgerReplicaOutcome> outcome, int ledger, Action<LedgerReplicaReplicaState>? immediate) =>
            immediate is null ? outcome.SameState() : outcome.ThenState<LedgerReplicaState>(next => immediate(next.Ledgers[ledger]));
    }

    /// <summary>
    /// A failed mutation of one ledger, which may still take effect once at any later point (ledger-replicas.md §6.1).
    /// </summary>
    private sealed class Pending(string id, int ledger, Action<LedgerReplicaReplicaState> effect) : BaseStepFunction
    {
        public override string StepFunctionId => id;

        /// <summary>
        /// A write which lands: on a replica, unless a writer write meets its seal; on the composition, on <c>Q</c>
        /// replicas.
        /// </summary>
        internal static Pending Writing(int ledger, long address, bool recovery) =>
            new(Describe(ledger, $"Write({address})"), ledger, replica =>
            {
                if (recovery || !replica.Sealed)
                {
                    AddData(replica, address);
                }
            });

        /// <summary>
        /// A composition's seal or recovery which reaches <c>N − Q + 1</c> replicas.
        /// </summary>
        internal static Pending Seal(int ledger) =>
            new(Describe(ledger, $"Seal"), ledger, static replica =>
            {
                replica.Sealed = true;
                replica.MaySeal = true;
            });

        protected override IList<StepResult> ApplyInternal(IState state)
        {
            var next = (LedgerReplicaState)((LedgerReplicaState)state).Clone();
            var replica = next.Ledgers[ledger];
            if (!replica.Dropped)
            {
                replica.Exists = true;
                effect(replica);
            }

            return [new StepResult { State = next }];
        }
    }
}

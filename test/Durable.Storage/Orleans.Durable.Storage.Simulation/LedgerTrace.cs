using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// The operation a ledger event names (docs/spec/verification.md §5.1).
/// </summary>
public enum LedgerOperation
{
    /// <summary>
    /// A writer write: <see cref="ILedgerReplica.WriteAsync"/> with <c>recovery</c> unset.
    /// </summary>
    WriterWrite,

    /// <summary>
    /// A recovery write: <see cref="ILedgerReplica.WriteAsync"/> with <c>recovery</c> set.
    /// </summary>
    RecoveryWrite,

    /// <summary>
    /// A read of one address.
    /// </summary>
    Read,

    /// <summary>
    /// A contiguous-tail read which does not seal: <see cref="ILedgerReplica.ReadTailAsync"/> with <c>seal</c> unset.
    /// </summary>
    TailRead,

    /// <summary>
    /// A seal: <see cref="ILedgerReplica.ReadTailAsync"/> with <c>seal</c> set.
    /// </summary>
    Seal,
}

/// <summary>
/// The kind of a ledger event (docs/spec/verification.md §5.1).
/// </summary>
public enum LedgerEventKind
{
    /// <summary>
    /// A replica's answer to one request, logged on the node which served it.
    /// </summary>
    Served,

    /// <summary>
    /// A client's request to one replica, logged when the client sends it.
    /// </summary>
    Called,

    /// <summary>
    /// The answer or failure a client received for one request.
    /// </summary>
    Answered,

    /// <summary>
    /// The writer acquires an address: it sends the address's write, carrying its completed prefix. A
    /// <see cref="LedgerWriter"/>'s acquisition carries no tail the trace sees; its writes carry it to each replica.
    /// </summary>
    Acquired,

    /// <summary>
    /// The writer completes an address's write with the quorum's outcome. A <see cref="LedgerWriter"/> logs only
    /// <see cref="ReplicaAnswers.Written"/>, when it calls the slot's completion.
    /// </summary>
    Appended,

    /// <summary>
    /// The writer's slot at an address completed: every lower slot completed (LP-I3).
    /// </summary>
    Completed,

    /// <summary>
    /// The writer stopped its sequencer lifetime, with the reason the answer names. A stop is idempotent, so a repeated
    /// request logs again; the first ended the lifetime.
    /// </summary>
    Stopped,

    /// <summary>
    /// A recovery executor begins a recovery.
    /// </summary>
    RecoveryBegan,

    /// <summary>
    /// A recovery returns its candidate end, with its <c>c</c> when the trace knows it: a primary's recovery starts from
    /// a known contiguous tail its other operations may raise meanwhile.
    /// </summary>
    RecoveryEnded,

    /// <summary>
    /// A recovery fails.
    /// </summary>
    RecoveryFailed,

    /// <summary>
    /// The ledger map closes the ledger at its recorded end <c>T</c>, a recovery's candidate end (LG-I9): the conditional
    /// write of the recovery the client names, or <c>map</c> when the trace observes no recovery's.
    /// </summary>
    Closed,

    /// <summary>
    /// A recovery's conditional close of the ledger map lost: the map had recorded another recovery's <c>T</c>, which the
    /// recovery adopts (LG-I9).
    /// </summary>
    Superseded,

    /// <summary>
    /// A client's <see cref="Ledger"/> learns the map's recorded <c>T</c> (<see cref="Ledger.ObserveRecordedTail"/>), with
    /// no I/O; its later requests carry it (LP-I11).
    /// </summary>
    RecordedTailObserved,

    /// <summary>
    /// A client begins a seal check.
    /// </summary>
    TailReadBegan,

    /// <summary>
    /// A seal check answers, with the known contiguous tail of the client's <see cref="Ledger"/> after the check.
    /// </summary>
    TailReadAnswered,

    /// <summary>
    /// A seal check fails.
    /// </summary>
    TailReadFailed,

    /// <summary>
    /// A node's reconciliation dropped its replica of a ledger.
    /// </summary>
    Dropped,

    /// <summary>
    /// A node restarted from what its disk kept.
    /// </summary>
    Restarted,

    /// <summary>
    /// Media damage reached a node's WAL: a later read of a record the damage reaches, or the node's next recovery, finds
    /// the slot faulty (LS-I5). Damage reaches whichever ledgers' records the page holds, so the event names the node and
    /// no ledger.
    /// </summary>
    Damaged,

    /// <summary>
    /// A recovery handled one replica's answer, in the order it handles them.
    /// </summary>
    Handled,
}

/// <summary>
/// One event of a simulated ledger's protocol, as <see cref="LedgerTrace"/> logs it.
/// </summary>
/// <param name="Sequence">The event's position in the trace.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Client">The client the event concerns: the writer, a recovery executor, or a reader.</param>
/// <param name="Ledger">The ledger.</param>
public sealed record LedgerEvent(long Sequence, LedgerEventKind Kind, string Client, LedgerId Ledger)
{
    /// <summary>
    /// Gets the node of a replica event, a drop, or a restart.
    /// </summary>
    public string? Node { get; init; }

    /// <summary>
    /// Gets the node's process, counted from its first start, of an answer it served or of its restart.
    /// </summary>
    public int Incarnation { get; init; }

    /// <summary>
    /// Gets the identity which pairs a client's request with its answer, or a client step's begin with its end.
    /// </summary>
    public long? Call { get; init; }

    /// <summary>
    /// Gets the operation of a replica event.
    /// </summary>
    public LedgerOperation? Operation { get; init; }

    /// <summary>
    /// Gets the address of a write, read, or append.
    /// </summary>
    public long? Address { get; init; }

    /// <summary>
    /// Gets the contiguous tail a request or step carried; for a recovery's end, its <c>c</c>.
    /// </summary>
    public long? ContiguousTail { get; init; }

    /// <summary>
    /// Gets a value indicating whether a read was sent after the recovery handled its replica's seal answer (LP-I7).
    /// </summary>
    public bool Marked { get; init; }

    /// <summary>
    /// Gets the answer: a write's or read's result, <c>Tail</c> for a contiguous-tail read, or the failure's type.
    /// </summary>
    public string? Answer { get; init; }

    /// <summary>
    /// Gets the contiguous tail and seal a contiguous-tail read reported.
    /// </summary>
    public LedgerReplicaTail? Tail { get; init; }

    /// <summary>
    /// Gets a recovery's candidate end.
    /// </summary>
    public long? End { get; init; }

    /// <summary>
    /// Gets what a restarted node's recovery established for the ledger's replica.
    /// </summary>
    public RecoveredReplica? Recovered { get; init; }

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        LedgerEventKind.Served => Invariant($"served #{Call} {Node}#{Incarnation} s{Ledger} {Client} {Request()} -> {Answer}{Reported()}"),
        LedgerEventKind.Called => Invariant($"call #{Call} {Client} -> {Node} s{Ledger} {Request()}{(Marked ? " marked" : string.Empty)}"),
        LedgerEventKind.Answered => Invariant($"answer #{Call} {Node} -> {Client} s{Ledger} {Operation} -> {Answer}{Reported()}"),
        LedgerEventKind.Handled => Invariant($"handled {Client} <- {Node} s{Ledger} {Operation}{(Address is { } at ? Invariant($" {at}") : string.Empty)}{(Marked ? " marked" : string.Empty)} -> {Answer}{Reported()}"),
        LedgerEventKind.Acquired => Invariant($"{Client} appends s{Ledger} {Address}{Carried(" tail ")}"),
        LedgerEventKind.Appended => Invariant($"{Client} appended s{Ledger} {Address} -> {Answer}"),
        LedgerEventKind.Completed => Invariant($"{Client} completed s{Ledger} {Address}"),
        LedgerEventKind.Stopped => Invariant($"{Client} stopped s{Ledger}: {Answer}"),
        LedgerEventKind.RecoveryBegan => Invariant($"{Client} recovery #{Call} of s{Ledger} begins{Carried(" with tail ")}"),
        LedgerEventKind.RecoveryEnded => Invariant($"{Client} recovery #{Call} of s{Ledger} ends{Carried(": c ")}, end {End}"),
        LedgerEventKind.RecoveryFailed => Invariant($"{Client} recovery #{Call} of s{Ledger} fails: {Answer}"),
        LedgerEventKind.Closed => Invariant($"{Client} closes s{Ledger} at {End}"),
        LedgerEventKind.Superseded => Invariant($"{Client} finds s{Ledger} closed"),
        LedgerEventKind.RecordedTailObserved => Invariant($"{Client} learns s{Ledger}'s recorded T {End}"),
        LedgerEventKind.TailReadBegan => Invariant($"{Client} seal check #{Call} of s{Ledger} begins{Carried(" with tail ")}"),
        LedgerEventKind.TailReadAnswered => Invariant($"{Client} seal check #{Call} of s{Ledger} ->{Reported()}"),
        LedgerEventKind.TailReadFailed => Invariant($"{Client} seal check #{Call} of s{Ledger} fails: {Answer}"),
        LedgerEventKind.Dropped => Invariant($"drop {Node} s{Ledger}"),
        LedgerEventKind.Restarted => Invariant($"restart {Node}#{Incarnation} s{Ledger} {Recovered}"),
        LedgerEventKind.Damaged => Invariant($"damage {Node}#{Incarnation}"),
        _ => Kind.ToString(),
    };

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private string Request() =>
        Invariant($"{Operation}{(Address is { } address ? Invariant($" {address}") : string.Empty)} tail {ContiguousTail}");

    /// <summary>
    /// Renders the tail a client step carried, after <paramref name="label"/>; a step whose tail the trace does not see
    /// renders none.
    /// </summary>
    private string Carried(string label) =>
        ContiguousTail is { } tail ? Invariant($"{label}{tail}") : string.Empty;

    private string Reported() =>
        Tail is { } tail ? Invariant($" tail {tail.ContiguousTail} sealed {tail.IsSealed}") : string.Empty;
}

/// <summary>
/// What a restarted node's recovery established for one ledger's replica: whether the node holds it, whether its seal is
/// durable, and the slots which hold DATA and those which are faulty (LS-I5), below the highest address any client had
/// sent a request for.
/// </summary>
/// <param name="Held">Whether the node holds the replica.</param>
/// <param name="Sealed">Whether the replica's seal is durable.</param>
/// <param name="Data">The slots which hold DATA.</param>
/// <param name="Faulty">The faulty slots.</param>
public sealed record RecoveredReplica(bool Held, bool Sealed, IReadOnlyList<long> Data, IReadOnlyList<long> Faulty)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        if (!Held)
        {
            return "not held";
        }

        var seal = Sealed ? " sealed" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"held{seal} data [{string.Join(", ", Data)}] faulty [{string.Join(", ", Faulty)}]");
    }
}

/// <summary>
/// Logs a simulated ledger's protocol, as the ledger model names its steps, and checks the run against the
/// guarantees the log shows after every step (docs/spec/verification.md §5.1, §7).
/// </summary>
/// <remarks>
/// <para>
/// The log has four sources. Each node logs every answer its running store gives a replica request
/// (<see cref="LedgerEventKind.Served"/>): the call it answers, the operation, the client, the address, the contiguous
/// tail the request carried, its recovery or seal flag, and the answer with the contiguous tail and seal a
/// contiguous-tail read reports. A replica which <see cref="Traced(ILedgerReplica, string, string, LedgerId)"/> wraps around
/// each replica a client's <see cref="Ledger"/> uses logs each request it sends and the answer or failure it receives
/// (<see cref="LedgerEventKind.Called"/>, <see cref="LedgerEventKind.Answered"/>), so a recovery's repetitions show as
/// resends; it marks a read sent after the client's recovery handled that replica's seal answer, the rule
/// <see cref="Ledger.RecoverAsync(CancellationToken)"/> counts a missing answer by (LP-I7). A
/// recovery logs each replica answer it handles, in the order it handles them, through the observer it is given
/// (<see cref="LedgerEventKind.Handled"/>). The workloads log the client steps: the writer's acquisitions and
/// completions, each recovery's begin and its end with its <c>c</c>, each seal check, the map's close at the recorded
/// <c>T</c> by a recovery's conditional write and a rival recovery's lost close, each client's learning of <c>T</c>, and
/// the nodes' drops and restarts; each node logs the media damage it suffers as it happens. A <see cref="LedgerWriter"/>'s steps come from its
/// <see cref="Sequencer"/>, whose observer <see cref="TraceWriter(LedgerWriter, string, LedgerId)"/> installs: it acquires a
/// slot, the writer calls the slot's completion only once the slot's write reached <c>Q</c>
/// <see cref="ReplicaAnswers.Written"/> answers, so the writer saw the address written, and the completion returns once
/// every lower slot completed (LP-I3); its stops are logged too. Every event is
/// also recorded in the run's trace, prefixed <c>ledger</c>, so the history a failed run writes to the results directory
/// holds the log.
/// </para>
/// <para>
/// The checks follow ledger-replicas.md's quorum guarantees, and <see cref="LedgerTraceExport"/> renders the same log for the
/// ledger model's TLC trace specification. LR-I8: no replica answers a writer write <see cref="ReplicaAnswers.Written"/> after it answered
/// sealed: a seal, a sealed contiguous-tail read, a writer write answered <see cref="ReplicaAnswers.Sealed"/>, or a dropped
/// answer. LR-I6: a contiguous tail a replica reports is at most the writer's completed prefix, the lowest address the
/// writer has not seen written, or, once the map closes the ledger, its recorded <c>T</c>, which a <see cref="Ledger"/>
/// carries once it learns it (LP-I11); and never below a tail the replica reported, or an operation it answered carried, before
/// the read arrived, within one process of its node. LP-I7: a recovery's end <c>T</c> lies in
/// <c>[c, c + 2K]</c>, and the writer never sees <c>T</c> written, before, during, or after the recovery: the
/// <c>N − Q + 1</c> sealed replicas whose answers decided <c>T</c> missing never answer a writer write at <c>T</c>
/// written, so no such write reaches <c>Q</c>. Since the writer's completed prefix lies at or below every address it has
/// not seen written, every end then lies above every address the writer saw written with every lower one, and an end
/// at or below an address the writer saw written lies at a lower address the writer has not. LP-I8: no slot at or above
/// a recovery's candidate end completes, before or after the recovery. LP-I9: a seal check begun after a recovery of its
/// ledger completed reports sealed. LG-I9: the map closes a ledger only at a candidate end of a recovery which
/// completed.
/// </para>
/// <para>
/// A node logs answers in the order its scheduler lane completes them, which is not always the order the replica
/// serialized them: a writer write which meets a marked seal answers <see cref="ReplicaAnswers.Sealed"/> once the seal
/// record is durable, as the seal itself does, so the log may show the writer's <see cref="ReplicaAnswers.Sealed"/> answer
/// before the seal's answer, and a read may return a write's DATA before the write's answer is logged. The C# checks
/// count either as the replica's seal answer, and the trace export orders each such answer after the seal or write
/// which published its state.
/// </para>
/// </remarks>
public sealed class LedgerTrace
{
    /// <summary>
    /// The answer a node logs for a contiguous-tail read, beside the tail and seal it reports.
    /// </summary>
    internal const string TailAnswer = "Tail";

    /// <summary>
    /// The answer a replica gives once its node dropped the ledger (LR-I3).
    /// </summary>
    internal const string DroppedAnswer = nameof(ReplicaReadCase.Dropped);

    /// <summary>
    /// The answer a recovery handles for an operation it stopped once it no longer needed the answer.
    /// </summary>
    internal const string StoppedAnswer = "Stopped";

    private readonly SimulationEnvironment _environment;
    private readonly List<LedgerEvent> _events = [];
    private readonly HashSet<(string Node, LedgerId Ledger)> _created = [];
    private readonly HashSet<(string Node, LedgerId Ledger)> _sealed = [];
    private readonly Dictionary<(string Node, LedgerId Ledger, int Incarnation), long> _floors = [];
    private readonly Dictionary<LedgerId, HashSet<long>> _written = [];
    private readonly Dictionary<LedgerId, long> _prefixes = [];
    private readonly Dictionary<LedgerId, List<long>> _ends = [];
    private readonly Dictionary<LedgerId, long> _completed = [];
    private readonly Dictionary<LedgerId, long> _closed = [];
    private readonly HashSet<(string Client, LedgerId Ledger)> _writers = [];
    private readonly HashSet<LedgerId> _recovered = [];
    private readonly HashSet<(string Client, string Node, LedgerId Ledger)> _sealsHandled = [];
    private readonly Dictionary<(string Client, string Node, LedgerId Ledger, long Address), List<bool>> _readMarks = [];
    private readonly Dictionary<LedgerId, long> _highest = [];
    private readonly Dictionary<Ledger, IReadOnlyList<string>> _ledgerNodes = [];
    private long _calls;
    private long? _callHandOff;
    private string? _writtenAfterSeal;
    private string? _tailViolation;
    private string? _endViolation;
    private string? _completionViolation;
    private string? _sealCheckViolation;
    private string? _openCheckViolation;
    private string? _closeViolation;

    internal LedgerTrace(SimulationEnvironment environment)
    {
        _environment = environment;
        environment.Invariants.Add("ledger: no replica answers a writer write Written after it answered sealed (LR-I8)", () => _writtenAfterSeal);
        environment.Invariants.Add(
            "ledger: a replica's contiguous tail is at most the writer's completed prefix, or the recorded T once the map closes the ledger, and never decreases within a process (LR-I6)",
            () => _tailViolation);
        environment.Invariants.Add("ledger: a recovery's end lies in [c, c + 2K] and the writer never sees it written (LP-I7)", () => _endViolation);
        environment.Invariants.Add("ledger: no slot at or above a recovery's candidate end completes (LP-I8)", () => _completionViolation);
        environment.Invariants.Add("ledger: a seal check begun after a recovery completed reports sealed (LP-I9)", () => _sealCheckViolation);
        environment.Invariants.Add("ledger: a writer's seal check which finds its ledger sealed stops its lifetime (ledgers §8.3)", () => _openCheckViolation);
        environment.Invariants.Add("ledger: the map closes a ledger only at a candidate end of a completed recovery (LG-I9)", () => _closeViolation);
    }

    /// <summary>
    /// Gets the events logged so far.
    /// </summary>
    public IReadOnlyList<LedgerEvent> Events => _events;

    /// <summary>
    /// Returns a replica which logs each request a client sends to <paramref name="replica"/> and the answer it receives,
    /// and which disposes <paramref name="replica"/>.
    /// </summary>
    /// <param name="replica">The replica the client's quorum uses.</param>
    /// <param name="client">The client.</param>
    /// <param name="node">The replica's node.</param>
    /// <param name="ledger">The ledger.</param>
    /// <returns>The wrapper.</returns>
    public ILedgerReplica Traced(ILedgerReplica replica, string client, string node, LedgerId ledger)
    {
        ArgumentNullException.ThrowIfNull(replica);
        return new TracedLedgerReplica(this, replica, client, node, ledger);
    }

    /// <summary>
    /// Logs <paramref name="client"/>'s steps as the writer of <paramref name="ledger"/> through
    /// <paramref name="writer"/>'s <see cref="Sequencer"/>: each slot it grants, each completion the writer calls, which it
    /// calls only once the slot's write reached <c>Q</c> <see cref="ReplicaAnswers.Written"/> answers, each completion which
    /// returns, each write which met a seal, and each stop. The writer's <see cref="Ledger"/> stops the lifetime before a
    /// recovery's first seal and once a seal check finds the ledger sealed (LP-I14).
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="client">The writer's client.</param>
    /// <param name="ledger">The ledger.</param>
    internal void TraceWriter(LedgerWriter writer, string client, LedgerId ledger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _ = _writers.Add((client, ledger));
        writer.Sequencer.Observer = step =>
        {
            switch (step.Kind)
            {
                case SequencerStepKind.Acquired:
                    Acquired(client, ledger, step.Slot!.Value.Value);
                    break;
                case SequencerStepKind.Completing:
                    Appended(client, ledger, step.Slot!.Value.Value, ReplicaAnswers.Written);
                    break;
                case SequencerStepKind.Sealed:
                    Appended(client, ledger, step.Slot!.Value.Value, ReplicaAnswers.Sealed);
                    break;
                case SequencerStepKind.Completed:
                    Completed(client, ledger, step.Slot!.Value.Value);
                    break;
                case SequencerStepKind.Stopped:
                    Stopped(client, ledger, step.Reason!.Value);
                    break;
                default:
                    throw new InvalidOperationException("A sequencer step has no kind.");
            }
        };
    }

    /// <summary>
    /// Writes <paramref name="slot"/> through <paramref name="replica"/>'s writer write, as one of several writers
    /// which each write their own addresses do, logging the acquisition with the contiguous tail the write carries, the
    /// ledger's known contiguous tail, and the outcome.
    /// </summary>
    /// <param name="replica">The writer's <see cref="Ledger"/>.</param>
    /// <param name="client">The writer.</param>
    /// <param name="ledger">The ledger.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="payload">The value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    internal async ValueTask<LedgerWriteOutcome> WriteAsync(
        Ledger replica,
        string client,
        LedgerId ledger,
        Slot slot,
        ArcBuffer payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        _ = Record(new LedgerEvent(0, LedgerEventKind.Acquired, client, ledger) { Address = slot.Value, ContiguousTail = replica.KnownContiguousTail.Value });
        LedgerWriteOutcome result;
        try
        {
            result = await replica.WriteAsync(slot, payload, cancellationToken);
        }
        catch (Exception failure)
        {
            _ = Record(new LedgerEvent(0, LedgerEventKind.Appended, client, ledger) { Address = slot.Value, Answer = Describe(failure) });
            throw;
        }

        _ = Record(new LedgerEvent(0, LedgerEventKind.Appended, client, ledger) { Address = slot.Value, Answer = result.Case.ToString() });
        if (result.Value is ReplicaWriteOutcome.Written)
        {
            Written(ledger, slot.Value);
        }

        return result;
    }

    /// <summary>
    /// Recovers <paramref name="ledger"/> through <paramref name="replica"/>, a recovery executor's
    /// <see cref="Ledger"/> which serves this recovery alone while it runs, logging its begin with the known contiguous
    /// tail it starts from and its end with its <c>c</c>: the recovery raises the known contiguous tail to <c>c</c> and
    /// nothing else raises it meanwhile, so the value after the recovery is <c>c</c> exactly (LP-I11).
    /// </summary>
    /// <param name="replica">The recovery executor's <see cref="Ledger"/>.</param>
    /// <param name="client">The recovery executor.</param>
    /// <param name="ledger">The ledger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recovery's outcome.</returns>
    internal async ValueTask<RecoveryOutcome> RecoverAsync(
        Ledger replica,
        string client,
        LedgerId ledger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        var (recovery, outcome) = await RecoverAsync(
            replica,
            client,
            ledger,
            replica.KnownContiguousTail.Value,
            () => replica.RecoverAsync(cancellationToken));
        if (outcome.Value is RecoveryOutcome.Recovered recovered)
        {
            var c = replica.KnownContiguousTail.Value;
            RecoveryEnded(client, ledger, recovery, c, startFloor: c, startCeiling: c, recovered.End.Value, replica.WindowSize);
        }

        return outcome;
    }

    /// <summary>
    /// Recovers <paramref name="ledger"/> through the primary's <paramref name="replica"/>, as a primary which helps does,
    /// logging the recovery's begin and its end; the <see cref="Ledger"/> stops its writer's lifetime before the
    /// recovery's first seal (LP-I14).
    /// </summary>
    /// <param name="replica">The primary's <see cref="Ledger"/>, whose writer's appends may run during the recovery.</param>
    /// <param name="client">The primary.</param>
    /// <param name="ledger">The ledger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recovery's outcome.</returns>
    /// <remarks>
    /// The recovery starts from the greater of the <see cref="Ledger"/>'s known contiguous tail and the retained tails its
    /// seals report, and raises the known contiguous tail to that <c>c</c> (LP-I11), so the value before the recovery
    /// bounds <c>c</c> from below. The primary's other operations during the recovery may raise the value past <c>c</c>,
    /// to at most the recorded <c>T</c> once it learns it, so the value after the recovery bounds <c>c</c> from above. The
    /// trace checks the end against both bounds, and logs no exact <c>c</c>.
    /// </remarks>
    internal async ValueTask<RecoveryOutcome> RecoverAsPrimaryAsync(
        Ledger replica,
        string client,
        LedgerId ledger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        var startFloor = replica.KnownContiguousTail.Value;
        var (recovery, outcome) = await RecoverAsync(
            replica,
            client,
            ledger,
            contiguousTail: null,
            () => replica.RecoverAsync(cancellationToken));
        if (outcome.Value is RecoveryOutcome.Recovered recovered)
        {
            RecoveryEnded(client, ledger, recovery, c: null, startFloor, startCeiling: replica.KnownContiguousTail.Value, recovered.End.Value, replica.WindowSize);
        }

        return outcome;
    }

    /// <summary>
    /// Logs that the ledger map closed <paramref name="ledger"/> at <paramref name="end"/>, its recorded <c>T</c>, by
    /// <paramref name="recovery"/>'s conditional write, and bounds every contiguous tail a replica reports from then on
    /// by it (LR-I6, LP-I11).
    /// </summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="recovery">The recovery executor whose conditional write closed the ledger at its candidate end.</param>
    /// <param name="end">The recorded <c>T</c>: a candidate end of a recovery of the ledger which completed (LG-I9).</param>
    public void Closed(LedgerId ledger, string recovery, long end)
    {
        _ = Record(new LedgerEvent(0, LedgerEventKind.Closed, recovery, ledger) { End = end });
        if (!Ends(ledger).Contains(end))
        {
            _closeViolation ??= Invariant($"the map closed ledger {ledger} at {end}, which no completed recovery ended at");
        }

        if (_closed.TryGetValue(ledger, out var recorded) && recorded != end)
        {
            _closeViolation ??= Invariant($"the map closed ledger {ledger} at {end} after closing it at {recorded}");
        }

        _closed[ledger] = end;
    }

    /// <summary>
    /// Logs that <paramref name="recovery"/>'s conditional close of <paramref name="ledger"/> lost: the map had recorded
    /// another recovery's <c>T</c>, which <paramref name="recovery"/> adopts (LG-I9).
    /// </summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="recovery">The recovery executor whose recovery completed.</param>
    public void Superseded(LedgerId ledger, string recovery)
    {
        _ = Record(new LedgerEvent(0, LedgerEventKind.Superseded, recovery, ledger));
        if (!_closed.ContainsKey(ledger))
        {
            _closeViolation ??= Invariant($"{recovery}'s close of ledger {ledger} lost while the map held it open");
        }
    }

    /// <summary>
    /// Passes <paramref name="ledger"/>'s recorded <c>T</c> to <paramref name="client"/>'s <paramref name="replica"/>, as a
    /// process's map view learns it (<see cref="Ledger.ObserveRecordedTail"/>), and logs it; the map records one
    /// <c>T</c> for the ledger's life (LG-I9).
    /// </summary>
    /// <param name="replica">The client's <see cref="Ledger"/>.</param>
    /// <param name="client">The client.</param>
    /// <param name="ledger">The ledger.</param>
    /// <param name="recordedTail">The <c>T</c> the map recorded.</param>
    internal void ObserveRecordedTail(Ledger replica, string client, LedgerId ledger, Slot recordedTail)
    {
        ArgumentNullException.ThrowIfNull(replica);
        _ = Record(new LedgerEvent(0, LedgerEventKind.RecordedTailObserved, client, ledger) { End = recordedTail.Value });
        if (!_closed.TryGetValue(ledger, out var recorded) || recorded != recordedTail.Value)
        {
            _closeViolation ??= Invariant($"{client} learned ledger {ledger}'s recorded T {recordedTail.Value}, which the map did not record");
        }

        replica.ObserveRecordedTail(recordedTail);
    }

    /// <summary>
    /// Logs that the ledger map closed <paramref name="ledger"/> at <paramref name="end"/>, its recorded <c>T</c>, in a run
    /// whose executors recover inside <c>TransitionExecutor</c>, so the trace observes none of their recoveries. The
    /// recorded <c>T</c> is a candidate end, so the trace holds it to a candidate end's checks from then on: no slot at or
    /// above it completed or completes (LP-I8), and the writer never sees it written (LP-I7). It bounds every contiguous
    /// tail a replica reports by <c>T</c> (LR-I6, LP-I11), and checks that the map records one <c>T</c>.
    /// </summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="end">The recorded <c>T</c>.</param>
    public void ClosedByExecutor(LedgerId ledger, long end)
    {
        _ = Record(new LedgerEvent(0, LedgerEventKind.Closed, "map", ledger) { End = end });
        if (_closed.TryGetValue(ledger, out var recorded) && recorded != end)
        {
            _closeViolation ??= Invariant($"the map closed ledger {ledger} at {end} after closing it at {recorded}");
        }

        if (_completed.GetValueOrDefault(ledger, -1) >= end)
        {
            _completionViolation ??= Invariant($"ledger {ledger}'s slot {_completed[ledger]} completed, at or above its recorded T {end}");
        }

        if (WrittenAddresses(ledger).Contains(end))
        {
            _endViolation ??= Invariant($"the writer saw ledger {ledger} address {end} written, its recorded T");
        }

        Ends(ledger).Add(end);
        _closed[ledger] = end;
    }

    /// <summary>
    /// Logs each ledger <paramref name="value"/>, a value the register applied over <paramref name="previous"/>, lists
    /// closed which <paramref name="previous"/> listed open, as <see cref="ClosedByExecutor"/> does, and checks that its
    /// recorded <c>T</c> follows a recovery which completed its seal coverage (LG-I9): at least <c>N − Q + 1</c> of the
    /// ledger's replicas answered sealed before the write applied, so no writer write reaches <c>Q</c> written answers
    /// after it.
    /// </summary>
    /// <param name="previous">The value before.</param>
    /// <param name="value">The value the register applied.</param>
    /// <param name="addressOf">The address of a node's simulated store.</param>
    public void RecordCloses(LedgerMapValue previous, LedgerMapValue value, Func<NodeId, string> addressOf)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(addressOf);
        foreach (var ledger in value.Ledgers)
        {
            if (ledger.Status.Value is not LedgerStatus.Closed closed || previous.Get(ledger.Id) is not { IsOpen: true })
            {
                continue;
            }

            ClosedByExecutor(ledger.Id, closed.T.Value);
            if (ledger.Location.Value is not LedgerLocation.Nodes onNodes)
            {
                continue;
            }

            var sealedReplicas = onNodes.Replicas.Count(node => _sealed.Contains((addressOf(node), ledger.Id)));
            var coverage = onNodes.Replicas.Length - onNodes.Quorum + 1;
            if (sealedReplicas < coverage)
            {
                _closeViolation ??= Invariant($"the map closed ledger {ledger.Id} at {closed.T.Value} when {sealedReplicas} of its replicas had answered sealed, below the seal coverage {coverage}");
            }
        }
    }

    /// <summary>
    /// Checks <paramref name="ledger"/>'s seal through <paramref name="replica"/>, logging its begin, with the known
    /// contiguous tail the check carries, and its answer.
    /// </summary>
    /// <param name="replica">The client's <see cref="Ledger"/>.</param>
    /// <param name="client">The client.</param>
    /// <param name="ledger">The ledger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The <see cref="Ledger"/>'s answer.</returns>
    internal ValueTask<LedgerTailRead> CheckSealAsync(
        Ledger replica,
        string client,
        LedgerId ledger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        return CheckSealAsync(client, ledger, replica.KnownContiguousTail.Value, () => replica.ReadTailAsync(cancellationToken));
    }

    /// <summary>
    /// Checks <paramref name="ledger"/>'s seal through the primary's <paramref name="replica"/>, as a primary's open check
    /// does, logging its begin and its answer, which carries the known contiguous tail after the check; the primary's
    /// appends may raise the tail while the check runs, so the begin logs no carried tail.
    /// </summary>
    /// <param name="replica">The primary's <see cref="Ledger"/>.</param>
    /// <param name="client">The primary.</param>
    /// <param name="ledger">The ledger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The <see cref="Ledger"/>'s answer.</returns>
    internal ValueTask<LedgerTailRead> CheckSealAsPrimaryAsync(
        Ledger replica,
        string client,
        LedgerId ledger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        return CheckSealAsync(client, ledger, carried: null, () => replica.ReadTailAsync(cancellationToken));
    }

    /// <summary>
    /// Returns the end below which every slot of <paramref name="ledger"/> is decided as far as the trace knows now: its
    /// recorded <c>T</c> once the map closes it, otherwise one past its writer's highest completed slot, or 0.
    /// </summary>
    internal long DecidedEnd(LedgerId ledger) =>
        _closed.TryGetValue(ledger, out var end) ? end : _completed.TryGetValue(ledger, out var completed) ? completed + 1 : 0;

    /// <summary>
    /// Returns the highest contiguous tail <paramref name="node"/>'s running process reported, or retained from an
    /// operation it answered, for its replica of <paramref name="ledger"/>; <see langword="null"/> while no answer
    /// shows that the replica exists, since a replica the node does not hold retains nothing (LR-I6, LR-I7).
    /// </summary>
    internal long? Floor(LedgerStoreNode node, LedgerId ledger) =>
        _created.Contains((node.Address, ledger)) && _floors.TryGetValue((node.Address, ledger, node.Incarnation), out var floor) ? floor : null;

    /// <summary>
    /// Logs the answer <paramref name="node"/>'s running store gave request <paramref name="call"/>, before the answer
    /// leaves the node.
    /// </summary>
    internal void Served(
        string client,
        LedgerStoreNode node,
        LedgerId ledger,
        LedgerOperation operation,
        long? address,
        Slot contiguousTail,
        string answer,
        LedgerReplicaTail? tail,
        long? floor,
        long? call = null)
    {
        _ = Record(new LedgerEvent(0, LedgerEventKind.Served, client, ledger)
        {
            Node = node.Address,
            Incarnation = node.Incarnation,
            Call = call,
            Operation = operation,
            Address = address,
            ContiguousTail = contiguousTail.Value,
            Answer = answer,
            Tail = tail,
        });

        var replica = (node.Address, ledger);
        if (operation == LedgerOperation.WriterWrite && answer == nameof(ReplicaAnswers.Written) && _sealed.Contains(replica))
        {
            _writtenAfterSeal ??= Invariant($"{node.Address}#{node.Incarnation} answered {client}'s writer write of ledger {ledger} address {address} Written after it answered sealed");
        }

        if (answer is nameof(ReplicaAnswers.Sealed) or DroppedAnswer || tail is { IsSealed: true })
        {
            _ = _sealed.Add(replica);
        }

        if (tail is { } reported)
        {
            CheckReportedTail(client, node, ledger, reported.ContiguousTail.Value, floor);
        }

        if (answer is nameof(ReplicaAnswers.Written) or nameof(ReplicaAnswers.Sealed) || operation == LedgerOperation.Seal && tail is not null)
        {
            // A write which answers, or a seal, created the replica unless it existed (LR-I7).
            _ = _created.Add(replica);
        }

        if (_created.Contains(replica) && IsAnswer(answer))
        {
            var key = (node.Address, ledger, node.Incarnation);
            _floors[key] = Math.Max(_floors.GetValueOrDefault(key), Math.Max(contiguousTail.Value, tail?.ContiguousTail.Value ?? 0));
        }
    }

    /// <summary>
    /// Logs a client's request to a replica and returns the identity which pairs it with its answer, which the replica
    /// the request goes to takes (<see cref="TakeCall"/>) so that the node logs it with each answer it serves.
    /// </summary>
    internal long Called(string client, string node, LedgerId ledger, LedgerOperation operation, long? address, Slot contiguousTail, bool marked)
    {
        // A traced replica hands its request's identity to the replica it calls synchronously, so no other request is
        // sent in between.
        Debug.Assert(_callHandOff is null, "The replica took the previous request's identity.");
        if (address is { } at)
        {
            _highest[ledger] = Math.Max(_highest.GetValueOrDefault(ledger), at);
            if (operation == LedgerOperation.Read)
            {
                var key = (client, node, ledger, at);
                if (!_readMarks.TryGetValue(key, out var marks))
                {
                    _readMarks[key] = marks = [];
                }

                marks.Add(marked);
            }
        }

        var call = Record(new LedgerEvent(0, LedgerEventKind.Called, client, ledger)
        {
            Node = node,
            Call = ++_calls,
            Operation = operation,
            Address = address,
            ContiguousTail = contiguousTail.Value,
            Marked = marked,
        }).Call!.Value;
        _callHandOff = call;
        return call;
    }

    /// <summary>
    /// Returns the identity of the request a traced replica is handing to its replica, which the replica takes
    /// when it is called, or <see langword="null"/> for a request no traced replica sent.
    /// </summary>
    internal long? TakeCall()
    {
        var call = _callHandOff;
        _callHandOff = null;
        return call;
    }

    /// <summary>
    /// Logs a replica answer a recovery handled, in the order it handles them, and from a seal's answer marks the reads
    /// the recovery sends that replica from then on (LP-I7).
    /// </summary>
    internal void Handled(string client, string node, LedgerId ledger, RecoveryStep step)
    {
        var operation = step.Kind switch
        {
            RecoveryStepKind.Seal => LedgerOperation.Seal,
            RecoveryStepKind.Read => LedgerOperation.Read,
            RecoveryStepKind.RecoveryWrite => LedgerOperation.RecoveryWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step.Kind, "A recovery step has no kind."),
        };
        _ = Record(new LedgerEvent(0, LedgerEventKind.Handled, client, ledger)
        {
            Node = node,
            Operation = operation,
            Address = step.Address,
            Marked = step.AfterSeal,
            Answer = HandledAnswer(step),
            Tail = step.Tail,
        });
        if (step is { Kind: RecoveryStepKind.Read, Answer: not null })
        {
            // LP-I7: the recovery and its traced replica mark a read at one instant, so a read it handled was sent with the
            // mark it reports.
            var sent = _readMarks.TryGetValue((client, node, ledger, step.Address!.Value), out var marks) && marks.Remove(step.AfterSeal);
            Debug.Assert(sent, "A recovery handled a read whose mark no request it sent carried.");
        }

        if (step is { Kind: RecoveryStepKind.Seal, Answer: RecoveryAnswer.Tail })
        {
            _ = _sealsHandled.Add((client, node, ledger));
        }
    }

    /// <summary>
    /// Withdraws the mark a read carried which failed or was stopped, whose answer no recovery handles as a read's, so the
    /// marks the recovery's answers match are those of reads still in flight (LP-I7).
    /// </summary>
    internal void ReadFailed(string client, string node, LedgerId ledger, long address, bool marked)
    {
        if (_readMarks.TryGetValue((client, node, ledger, address), out var marks))
        {
            _ = marks.Remove(marked);
        }
    }

    /// <summary>
    /// Returns whether <paramref name="client"/>'s recovery handled <paramref name="node"/>'s seal answer, so that a read
    /// it sends that replica follows the seal (LP-I7).
    /// </summary>
    internal bool IsSealHandled(string client, string node, LedgerId ledger) => _sealsHandled.Contains((client, node, ledger));

    /// <summary>
    /// Logs the answer or failure a client received for request <paramref name="call"/>.
    /// </summary>
    internal void Answered(long call, string client, string node, LedgerId ledger, LedgerOperation operation, string answer, LedgerReplicaTail? tail = null) =>
        _ = Record(new LedgerEvent(0, LedgerEventKind.Answered, client, ledger) { Node = node, Call = call, Operation = operation, Answer = answer, Tail = tail });

    /// <summary>
    /// Logs that <paramref name="node"/> dropped its replica of <paramref name="ledger"/>.
    /// </summary>
    internal void Dropped(LedgerStoreNode node, LedgerId ledger) =>
        _ = Record(new LedgerEvent(0, LedgerEventKind.Dropped, node.Address, ledger) { Node = node.Address, Incarnation = node.Incarnation });

    /// <summary>
    /// Logs that media damage reached <paramref name="node"/>'s WAL, as it happens: the trace allows a slot of the node to
    /// turn faulty only after this (docs/spec/verification.md §5.1).
    /// </summary>
    internal void Damaged(LedgerStoreNode node) =>
        _ = Record(new LedgerEvent(0, LedgerEventKind.Damaged, node.Address, default) { Node = node.Address, Incarnation = node.Incarnation });

    /// <summary>
    /// Registers the node of each replica of <paramref name="replica"/>, a <see cref="Ledger"/> whose recoveries log each
    /// answer they handle.
    /// </summary>
    internal void Register(Ledger replica, IReadOnlyList<string> nodes) => _ledgerNodes[replica] = nodes;

    private static readonly AsyncLocal<Action<RecoveryStep>?> CurrentRecovery = new();

    /// <summary>
    /// Gets the observer every traced quorum is built with: it forwards each step to the observer of the recovery the
    /// current flow runs through <see cref="RecoverAsync(Ledger, string, LedgerId, CancellationToken)"/>, so concurrent
    /// recoveries through one <see cref="Ledger"/> each log their own steps (docs/spec/verification.md §5.1).
    /// </summary>
    internal static Action<RecoveryStep> RecoveryObserver { get; } = static step => CurrentRecovery.Value?.Invoke(step);

    /// <summary>
    /// Gets or sets a value indicating whether a node's restart logs what its recovery established for each ledger a
    /// client has sent requests for, which trace validation checks (docs/spec/verification.md §5.1). Reading it reads each
    /// unloaded slot-table leaf it covers into a scratch copy before the node serves, so the node loads the leaves an
    /// untraced run's would, but the reads can change the schedule; only a run whose trace is exported sets it.
    /// </summary>
    public bool LogsRecoveredReplicas { get; set; }

    /// <summary>
    /// Logs that <paramref name="node"/> restarted, before the node serves: with what its recovery established for each
    /// ledger a client has sent requests for when <see cref="LogsRecoveredReplicas"/> is set.
    /// </summary>
    internal async ValueTask RestartedAsync(LedgerStoreNode node, LedgerStore store)
    {
        if (!LogsRecoveredReplicas)
        {
            _ = Record(new LedgerEvent(0, LedgerEventKind.Restarted, node.Address, default) { Node = node.Address, Incarnation = node.Incarnation });
            return;
        }

        foreach (var (ledger, highest) in _highest.OrderBy(static entry => entry.Key.Value).ToList())
        {
            RecoveredReplica recovered;
            if (store.FindLive(ledger) is { } live)
            {
                var (data, faulty) = await live.SlotStatesAsync(highest + 1, CancellationToken.None);
                recovered = new RecoveredReplica(Held: true, live.IsSealed, data, faulty);
            }
            else
            {
                recovered = new RecoveredReplica(Held: false, Sealed: false, [], []);
            }

            _ = Record(new LedgerEvent(0, LedgerEventKind.Restarted, node.Address, ledger)
            {
                Node = node.Address,
                Incarnation = node.Incarnation,
                Recovered = recovered,
            });
        }
    }

    /// <summary>
    /// Names a failure in the log: its type.
    /// </summary>
    internal static string Describe(Exception failure) => failure.GetType().Name;

    private static string HandledAnswer(RecoveryStep step)
    {
        if (step.Stopped)
        {
            return StoppedAnswer;
        }

        if (step.Failure is { } failure)
        {
            return Describe(failure);
        }

        return step.Answer switch
        {
            RecoveryAnswer.Tail => TailAnswer,
            RecoveryAnswer.Batch => nameof(ReplicaReadCase.Batch),
            RecoveryAnswer.Missing => nameof(ReplicaReadCase.Missing),
            RecoveryAnswer.Written => nameof(ReplicaAnswers.Written),
            RecoveryAnswer.Dropped => DroppedAnswer,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step.Answer, "A recovery step has no answer."),
        };
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// Returns whether a logged answer is one the replica gave after admitting the request's contiguous tail, rather than
    /// a failure.
    /// </summary>
    private static bool IsAnswer(string answer) =>
        answer is nameof(ReplicaAnswers.Written) or nameof(ReplicaAnswers.Sealed) or nameof(ReplicaReadCase.Batch) or nameof(ReplicaReadCase.Missing) or TailAnswer;

    private void CheckReportedTail(string client, LedgerStoreNode node, LedgerId ledger, long reported, long? floor)
    {
        var prefix = _prefixes.GetValueOrDefault(ledger);
        if (_closed.TryGetValue(ledger, out var recorded))
        {
            // LP-I11: once the map records T, a Ledger which learns it carries it.
            if (reported > Math.Max(prefix, recorded))
            {
                _tailViolation ??= Invariant($"{node.Address}#{node.Incarnation} reported contiguous tail {reported} of ledger {ledger} to {client}, above its recorded T {recorded}");
            }
        }
        else if (reported > prefix)
        {
            _tailViolation ??= Invariant($"{node.Address}#{node.Incarnation} reported contiguous tail {reported} of ledger {ledger} to {client}, above the writer's completed prefix {prefix}");
        }

        if (floor is { } held && reported < held)
        {
            _tailViolation ??= Invariant($"{node.Address}#{node.Incarnation} reported contiguous tail {reported} of ledger {ledger} to {client}, below the {held} it held when the read arrived");
        }
    }

    /// <summary>
    /// Logs a recovery's end and checks it: it lies in <c>[c, c + 2K]</c> for the recovery's start <c>c</c>, which lies in
    /// <c>[startFloor, startCeiling]</c>, and <paramref name="c"/> when the trace knows it exactly; the writer never sees
    /// it written (LP-I7); and no completed slot lies at or above it (LP-I8).
    /// </summary>
    private void RecoveryEnded(string client, LedgerId ledger, long recovery, long? c, long startFloor, long startCeiling, long end, int window)
    {
        _ = Record(new LedgerEvent(0, LedgerEventKind.RecoveryEnded, client, ledger) { Call = recovery, ContiguousTail = c, End = end });
        if (end < startFloor || end > startCeiling + 2L * window)
        {
            _endViolation ??= Invariant($"{client}'s recovery #{recovery} of ledger {ledger} ended at {end}, outside [c, c + 2K] for c in [{startFloor}, {startCeiling}]");
        }

        if (WrittenAddresses(ledger).Contains(end))
        {
            _endViolation ??= Invariant($"{client}'s recovery #{recovery} of ledger {ledger} ended at {end}, which the writer saw written");
        }

        if (_completed.TryGetValue(ledger, out var completed) && completed >= end)
        {
            _completionViolation ??= Invariant($"{client}'s recovery #{recovery} of ledger {ledger} ended at {end}, at or below slot {completed}, which completed");
        }

        Ends(ledger).Add(end);
        _ = _recovered.Add(ledger);
    }

    /// <summary>
    /// Logs a recovery's begin, with the known contiguous tail it starts from when the trace knows it, runs it with the
    /// observer which logs each answer it handles when <paramref name="replica"/>'s nodes are registered, and logs its
    /// failure.
    /// </summary>
    /// <returns>The recovery's identity and its outcome.</returns>
    private async ValueTask<(long Recovery, RecoveryOutcome Outcome)> RecoverAsync(
        Ledger replica,
        string client,
        LedgerId ledger,
        long? contiguousTail,
        Func<ValueTask<RecoveryOutcome>> recover)
    {
        // A recovery holds only its own seal answers, so a read is marked once this recovery handled its replica's.
        _ = _sealsHandled.RemoveWhere(handled => handled.Client == client && handled.Ledger == ledger);
        foreach (var key in _readMarks.Keys.Where(key => key.Client == client && key.Ledger == ledger).ToList())
        {
            _ = _readMarks.Remove(key);
        }

        var recovery = Record(new LedgerEvent(0, LedgerEventKind.RecoveryBegan, client, ledger) { Call = ++_calls, ContiguousTail = contiguousTail }).Call!.Value;
        Action<RecoveryStep>? observer = _ledgerNodes.TryGetValue(replica, out var nodes)
            ? step => Handled(client, nodes[step.Replica], ledger, step)
            : null;
        RecoveryOutcome outcome;
        try
        {
            // A traced quorum's observer forwards to this recovery's, which the recovery's own flow carries.
            CurrentRecovery.Value = observer;
            outcome = await recover();
        }
        catch (Exception failure)
        {
            _ = Record(new LedgerEvent(0, LedgerEventKind.RecoveryFailed, client, ledger) { Call = recovery, Answer = Describe(failure) });
            throw;
        }

        if (outcome.Value is ReplicaWriteOutcome.Dropped)
        {
            _ = Record(new LedgerEvent(0, LedgerEventKind.RecoveryFailed, client, ledger) { Call = recovery, Answer = DroppedAnswer });
        }

        return (recovery, outcome);
    }

    /// <summary>
    /// Logs a seal check's begin, with the contiguous tail it carries when the trace knows it, and its answer, and checks
    /// that a writer's check which finds its ledger sealed stopped the writer's lifetime before it answered
    /// (ledgers.md §8.3).
    /// </summary>
    private async ValueTask<LedgerTailRead> CheckSealAsync(string client, LedgerId ledger, long? carried, Func<ValueTask<LedgerTailRead>> check)
    {
        var call = Record(new LedgerEvent(0, LedgerEventKind.TailReadBegan, client, ledger) { Call = ++_calls, ContiguousTail = carried }).Call!.Value;
        var begun = _events.Count;
        var afterRecovery = _recovered.Contains(ledger);
        LedgerTailRead answer;
        try
        {
            answer = await check();
        }
        catch (Exception failure)
        {
            _ = Record(new LedgerEvent(0, LedgerEventKind.TailReadFailed, client, ledger) { Call = call, Answer = Describe(failure) });
            throw;
        }

        if (answer.Value is not LedgerTail tail)
        {
            _ = Record(new LedgerEvent(0, LedgerEventKind.TailReadFailed, client, ledger) { Call = call, Answer = nameof(ReplicaWriteOutcome.Backpressure) });
            return answer;
        }

        if (tail.IsSealed
            && _writers.Contains((client, ledger))
            && !_events.Skip(begun).Any(entry => entry is { Kind: LedgerEventKind.Stopped, Answer: nameof(SequencerStopReason.Sealing) } && entry.Client == client && entry.Ledger == ledger))
        {
            _openCheckViolation ??= Invariant($"{client}'s seal check #{call} of ledger {ledger} found it sealed and did not stop the writer's lifetime");
        }

        TailReadAnswered(client, ledger, call, afterRecovery, new LedgerReplicaTail(tail.ContiguousTail, tail.IsSealed));
        return answer;
    }

    /// <summary>
    /// Logs a seal check's answer, and checks that a check begun after a recovery of its ledger completed reports sealed
    /// (LP-I9).
    /// </summary>
    private void TailReadAnswered(string client, LedgerId ledger, long check, bool afterRecovery, LedgerReplicaTail tail)
    {
        _ = Record(new LedgerEvent(0, LedgerEventKind.TailReadAnswered, client, ledger) { Call = check, Tail = tail });
        if (afterRecovery && !tail.IsSealed)
        {
            _sealCheckViolation ??= Invariant($"{client}'s seal check #{check} of ledger {ledger}, begun after a recovery completed, reported tail {tail.ContiguousTail} unsealed");
        }
    }

    /// <summary>
    /// Logs that <paramref name="client"/>, a <see cref="LedgerWriter"/>, acquired the slot at <paramref name="address"/>.
    /// </summary>
    internal void Acquired(string client, LedgerId ledger, long address) =>
        _ = Record(new LedgerEvent(0, LedgerEventKind.Acquired, client, ledger) { Address = address });

    /// <summary>
    /// Logs that the write of <paramref name="address"/> by <paramref name="client"/>, a <see cref="LedgerWriter"/>, was
    /// decided, as an <see cref="LedgerEventKind.Appended"/> event with its <paramref name="answer"/>:
    /// <see cref="ReplicaAnswers.Written"/> once it reached <c>Q</c> such answers, when the writer calls the slot's completion
    /// (ledger-protocol.md §8.1), or <see cref="ReplicaAnswers.Sealed"/> once it met a seal or a drop (LP-I8).
    /// </summary>
    internal void Appended(string client, LedgerId ledger, long address, ReplicaWriteOutcome answer)
    {
        _ = Record(new LedgerEvent(0, LedgerEventKind.Appended, client, ledger) { Address = address, Answer = answer.Case.ToString() });
        if (answer.Value is ReplicaWriteOutcome.Written)
        {
            Written(ledger, address);
        }
    }

    /// <summary>
    /// Logs that <paramref name="client"/>'s slot at <paramref name="address"/> completed, and checks that it lies below
    /// every recovery's candidate end (LP-I8).
    /// </summary>
    internal void Completed(string client, LedgerId ledger, long address)
    {
        _ = Record(new LedgerEvent(0, LedgerEventKind.Completed, client, ledger) { Address = address });
        _completed[ledger] = Math.Max(_completed.GetValueOrDefault(ledger, -1), address);
        foreach (var end in Ends(ledger).Where(candidate => candidate <= address))
        {
            _completionViolation ??= Invariant($"{client}'s slot {address} of ledger {ledger} completed, at or above a recovery's candidate end {end}");
        }
    }

    /// <summary>
    /// Logs that <paramref name="client"/> stopped its sequencer lifetime for <paramref name="reason"/>. A stop is
    /// idempotent, so a lifetime which has stopped logs a later request again; its first stop is the one which ended it.
    /// </summary>
    internal void Stopped(string client, LedgerId ledger, SequencerStopReason reason) =>
        _ = Record(new LedgerEvent(0, LedgerEventKind.Stopped, client, ledger) { Answer = reason.ToString() });

    private void Written(LedgerId ledger, long address)
    {
        var written = WrittenAddresses(ledger);
        _ = written.Add(address);
        var prefix = _prefixes.GetValueOrDefault(ledger);
        while (written.Contains(prefix))
        {
            prefix++;
        }

        _prefixes[ledger] = prefix;
        if (Ends(ledger).Contains(address))
        {
            _endViolation ??= Invariant($"the writer saw ledger {ledger} address {address} written, a recovery's end");
        }
    }

    private HashSet<long> WrittenAddresses(LedgerId ledger)
    {
        if (!_written.TryGetValue(ledger, out var written))
        {
            written = [];
            _written.Add(ledger, written);
        }

        return written;
    }

    private List<long> Ends(LedgerId ledger)
    {
        if (!_ends.TryGetValue(ledger, out var ends))
        {
            ends = [];
            _ends.Add(ledger, ends);
        }

        return ends;
    }

    private LedgerEvent Record(LedgerEvent entry)
    {
        var recorded = entry with { Sequence = _events.Count };
        _events.Add(recorded);
        _environment.Trace.Record("ledger " + recorded);
        return recorded;
    }

    /// <summary>
    /// A client's handle over one replica, logging each request and its answer.
    /// </summary>
    private sealed class TracedLedgerReplica(LedgerTrace trace, ILedgerReplica inner, string client, string node, LedgerId ledger) : ILedgerReplica
    {
        public async ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            var operation = recovery ? LedgerOperation.RecoveryWrite : LedgerOperation.WriterWrite;
            var call = trace.Called(client, node, ledger, operation, slot.Value, contiguousTail, marked: false);
            ReplicaWriteOutcome result;
            try
            {
                result = await HandOff(() => inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken));
            }
            catch (Exception failure)
            {
                trace.Answered(call, client, node, ledger, operation, Describe(failure));
                throw;
            }

            trace.Answered(call, client, node, ledger, operation, result.Case.ToString());
            return result;
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            ReadAsync(slot, contiguousTail, token => inner.ReadAsync(slot, contiguousTail, token), cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            ReadAsync(slot, contiguousTail, token => inner.ReadAsync(slot, offset, length, contiguousTail, token), cancellationToken);

        public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
        {
            var operation = seal ? LedgerOperation.Seal : LedgerOperation.TailRead;
            var call = trace.Called(client, node, ledger, operation, address: null, contiguousTail, marked: false);
            ReplicaTailRead answer;
            try
            {
                answer = await HandOff(() => inner.ReadTailAsync(contiguousTail, seal, cancellationToken));
            }
            catch (Exception failure)
            {
                trace.Answered(call, client, node, ledger, operation, Describe(failure));
                throw;
            }

            trace.Answered(call, client, node, ledger, operation, answer.Name, answer.TryGetValue(out LedgerReplicaTail tail) ? tail : null);
            return answer;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private async ValueTask<ReplicaRead> ReadAsync(
            Slot slot, Slot contiguousTail, Func<CancellationToken, ValueTask<ReplicaRead>> read, CancellationToken cancellationToken)
        {
            // LP-I7: a read is marked once the client's recovery handled the replica's seal answer.
            var marked = trace.IsSealHandled(client, node, ledger);
            var call = trace.Called(client, node, ledger, LedgerOperation.Read, slot.Value, contiguousTail, marked);
            ReplicaRead result;
            try
            {
                result = await HandOff(() => read(cancellationToken));
            }
            catch (Exception failure)
            {
                trace.ReadFailed(client, node, ledger, slot.Value, marked);
                trace.Answered(call, client, node, ledger, LedgerOperation.Read, Describe(failure));
                throw;
            }

            trace.Answered(call, client, node, ledger, LedgerOperation.Read, result.Case.ToString());
            return result;
        }

        /// <summary>
        /// Starts the request on the replica, which takes the request's identity as it starts, and withdraws the
        /// identity from any other replica.
        /// </summary>
        private ValueTask<T> HandOff<T>(Func<ValueTask<T>> start)
        {
            try
            {
                return start();
            }
            finally
            {
                _ = trace.TakeCall();
            }
        }
    }
}

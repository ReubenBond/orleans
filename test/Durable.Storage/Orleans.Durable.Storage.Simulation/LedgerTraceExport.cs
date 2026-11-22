using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Renders one ledger's <see cref="LedgerTrace"/> events as the Quint module <c>LedgerTraceData</c>, which
/// <c>test/Durable.Storage/specs/ledger-protocol/LedgerProtocolTrace.qnt</c> validates against the ledger model (test/Durable.Storage/docs/spec/verification.md §5.1).
/// </summary>
/// <remarks>
/// <para>
/// Each event becomes a record of the kind LedgerProtocolTrace's header describes. The renderer derives across events
/// only what no single event shows: which delivery of a request reached its client (<c>deliver</c>), whether a delivery
/// leaves the request in flight for a later delivery of the same request (<c>keep</c>), which check a contiguous-tail read
/// belongs to, which of the writer's contiguous-tail reads outside a check starts an explicit contiguous-tail write, which
/// requests in flight to a node reached its disk before the node crashed, without an answer the trace
/// logged: the writer writes and recovery writes whose DATA, and the seals whose seal, the restarted node recovered, or an
/// answer the crashed process gave showed, without any answer of the node having shown them before, the tail a primary's
/// recovery starts from, which its seals carry, the reads a recovery executor sends outside its recoveries, which are a reader's, the prefix the writer
/// acknowledged by the map's close, and the requests which carry the recorded <c>T</c> their client learned, each of
/// whose deliveries a replica's <c>retain</c> precedes. Media damage names a node and no ledger, so it renders in every
/// ledger's trace.
/// </para>
/// <para>
/// A recovery executor reads the map before it seals, so one which begins after the close read the map before it: the
/// renderer places its begin just before the close.
/// </para>
/// <para>
/// A node logs its answers in the order its scheduler lane completes them, while a seal and a write publish their
/// records before their answers are logged. Within one process of the node, the renderer therefore orders a seal before
/// the first answer which reports the replica sealed, and a write which answered Written before the first read which
/// returned its DATA (§5.1). Recovery reads above the highest position which matters, past every written position and
/// every recovered end, answer missing and decide nothing, so the renderer leaves them out and bounds the model's
/// positions.
/// </para>
/// </remarks>
public static class LedgerTraceExport
{
    /// <summary>
    /// The name of the rendered module.
    /// </summary>
    public const string ModuleName = "LedgerTraceData";

    private const string Writer = "w";
    private const string CorruptAnswer = nameof(LedgerReplicaCorruptException);

    /// <summary>
    /// Renders <paramref name="ledger"/>'s events as the module <see cref="ModuleName"/>.
    /// </summary>
    /// <param name="events">The trace's events, in order.</param>
    /// <param name="ledger">The ledger.</param>
    /// <param name="nodes">Every node which holds a replica of the ledger.</param>
    /// <param name="quorum">The quorum <c>Q</c>.</param>
    /// <param name="window">The ledger's window <c>K</c>.</param>
    /// <returns>The module's text.</returns>
    public static string Render(IReadOnlyList<LedgerEvent> events, LedgerId ledger, IReadOnlyList<string> nodes, int quorum, int window)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(nodes);
        var trace = Translate(events, ledger, nodes);
        var text = new StringBuilder();
        _ = text.AppendLine("// -*- mode: Bluespec; -*-");
        _ = text.Append("module ").Append(ModuleName).AppendLine(" {");
        _ = text.AppendLine("  import LedgerProtocolTrace(");
        _ = text.Append("    TraceReplicas = ").Append(Set(Enumerable.Range(0, nodes.Count).Select(static id => Number(id)))).AppendLine(",");
        _ = text.Append("    TraceQuorum = ").Append(Number(quorum)).AppendLine(",");
        _ = text.Append("    TraceWindow = ").Append(Number(window)).AppendLine(",");
        _ = text.Append("    TraceSlots = ").Append(Number(trace.PositionCount)).AppendLine(",");
        _ = text.Append("    TraceRecoverers = ").Append(Set(Enumerable.Range(0, trace.RecoveryClients.Count).Select(static id => Number(id)))).AppendLine(",");
        _ = text.Append("    TraceReaders = ").Append(Set(Enumerable.Range(0, trace.CheckIds.Count).Select(static id => Number(id)))).AppendLine(",");
        _ = text.Append("    TraceFetchers = ").Append(Set(Enumerable.Range(0, trace.FetcherIds.Count).Select(static id => Number(id)))).AppendLine(",");
        _ = text.Append("    TraceDuplicates = ").Append(Number(trace.DuplicateBudget)).AppendLine(",");
        _ = text.Append("    TraceFaults = ").AppendLine(trace.HasCorruption ? "Set(\"corrupt\")" : "Set()");
        _ = text.AppendLine("  ).* from \"../../../specs/ledger-protocol/LedgerProtocolTrace\"");
        _ = text.AppendLine();
        _ = text.AppendLine("  run traceReplay = traceInit.expect(TraceInvariants)");
        foreach (var step in trace.Steps)
        {
            _ = text.Append("    .then(").Append(step).AppendLine(")");
        }

        _ = text.AppendLine("}");
        return text.ToString();
    }

    /// <summary>
    /// Translates <paramref name="ledger"/>'s events into the trace's records, rendered.
    /// </summary>
    internal static RenderedTrace Translate(IReadOnlyList<LedgerEvent> events, LedgerId ledger, IReadOnlyList<string>? nodes = null)
    {
        var ordered = Reorder([.. events.Where(entry => entry.Ledger == ledger || entry.Kind == LedgerEventKind.Damaged)]);
        var recoverers = ordered.Where(static entry => entry.Kind == LedgerEventKind.RecoveryBegan).Select(static entry => entry.Client).ToHashSet(StringComparer.Ordinal);
        var slots = Slots(ordered, recoverers);
        var beyond = ordered
            .Where(entry => entry.Kind == LedgerEventKind.Called && IsRecoveryRead(entry, recoverers) && entry.Address > slots)
            .Select(static entry => entry.Call!.Value)
            .ToHashSet();
        var input = ordered
            .Where(entry => entry.Call is not { } call || !beyond.Contains(call))
            .Where(entry => entry.Kind != LedgerEventKind.Handled || !IsRecoveryRead(entry, recoverers) || entry.Address <= slots)
            .ToList();
        return new Translation(input, recoverers, slots, nodes).ToTrace();
    }

    /// <summary>
    /// Moves an answer a node logged after another which depends on its record before that other, within one process of
    /// the node: a seal before the first answer which reports the replica sealed, and a write which answered Written
    /// before the first read which returned its DATA. Nothing moves across the node's restart, which ends the process
    /// whose completion order the move corrects, and a restart which recovers the replica sealed counts as its seal, which
    /// a seal that landed without an answer may have made.
    /// </summary>
    private static List<LedgerEvent> Reorder(List<LedgerEvent> input)
    {
        var output = new List<LedgerEvent>(input.Count);
        var remaining = new LinkedList<LedgerEvent>(input);
        var sealedNodes = new HashSet<string>(StringComparer.Ordinal);
        var published = new HashSet<(string Node, long Address)>();
        var writes = new HashSet<long>();
        while (remaining.First is { } first)
        {
            var entry = first.Value;
            remaining.RemoveFirst();
            if (entry.Kind == LedgerEventKind.Called && entry.Operation is LedgerOperation.WriterWrite or LedgerOperation.RecoveryWrite)
            {
                _ = writes.Add(entry.Call!.Value);
            }
            else if (entry.Kind == LedgerEventKind.Restarted && entry.Recovered is { } recovered)
            {
                // The restart shows the replica's seal, which a seal that landed without an answer may have made.
                _ = recovered is { Held: true, Sealed: true } ? sealedNodes.Add(entry.Node!) : sealedNodes.Remove(entry.Node!);
                _ = published.RemoveWhere(held => held.Node == entry.Node);
                published.UnionWith(recovered.Data.Select(address => (entry.Node!, address)));
            }
            else if (entry.Kind == LedgerEventKind.Served && entry.Node is { } node)
            {
                LedgerEvent? earlier = null;
                if (IsSeal(entry))
                {
                    _ = sealedNodes.Add(node);
                }
                else if (ReportsSealed(entry) && sealedNodes.Add(node))
                {
                    earlier = TakeLater(remaining, node, IsSeal);
                }
                else if (entry is { Operation: LedgerOperation.Read, Answer: nameof(ReplicaReadCase.Batch), Address: { } address } && published.Add((node, address)))
                {
                    earlier = TakeLater(remaining, node, later => IsPublished(later) && later.Address == address && writes.Contains(later.Call ?? 0));
                }

                if (IsPublished(entry))
                {
                    _ = published.Add((node, entry.Address!.Value));
                }

                if (earlier is not null)
                {
                    output.Add(earlier);
                }
            }

            output.Add(entry);
        }

        return output;
    }

    /// <summary>
    /// Removes and returns the first answer <paramref name="node"/> logged later in its current process which
    /// <paramref name="match"/> accepts.
    /// </summary>
    private static LedgerEvent? TakeLater(LinkedList<LedgerEvent> remaining, string node, Func<LedgerEvent, bool> match)
    {
        for (var later = remaining.First; later is not null; later = later.Next)
        {
            if (later.Value.Node != node)
            {
                continue;
            }

            if (later.Value.Kind == LedgerEventKind.Restarted)
            {
                return null;
            }

            if (later.Value.Kind == LedgerEventKind.Served && match(later.Value))
            {
                remaining.Remove(later);
                return later.Value;
            }
        }

        return null;
    }

    private static bool IsPublished(LedgerEvent entry) =>
        entry.Operation is LedgerOperation.WriterWrite or LedgerOperation.RecoveryWrite && entry.Answer == nameof(ReplicaAnswers.Written);

    private static bool IsSeal(LedgerEvent entry) =>
        entry.Operation == LedgerOperation.Seal && entry.Tail is { IsSealed: true };

    private static bool ReportsSealed(LedgerEvent entry) =>
        entry.Operation == LedgerOperation.WriterWrite && entry.Answer == nameof(ReplicaAnswers.Sealed)
        || entry.Operation == LedgerOperation.TailRead && entry.Tail is { IsSealed: true };

    /// <summary>
    /// Returns the positions the model needs: two past the highest position written, read as DATA or corrupt, read by a
    /// reader, or recovered as an end, so that the model's recovery reads every position which decides anything.
    /// </summary>
    private static long Slots(List<LedgerEvent> input, HashSet<string> recoverers)
    {
        var highest = 0L;
        foreach (var entry in input)
        {
            if (Relevant(entry, recoverers) is { } relevant)
            {
                highest = Math.Max(highest, relevant);
            }
        }

        return highest + 2;
    }

    private static long? Relevant(LedgerEvent entry, HashSet<string> recoverers)
    {
        if (entry.Kind == LedgerEventKind.Acquired)
        {
            return entry.Address;
        }

        if (entry.Kind == LedgerEventKind.RecoveryEnded)
        {
            return entry.End;
        }

        if (entry.Kind != LedgerEventKind.Served)
        {
            return null;
        }

        var written = entry.Operation is LedgerOperation.WriterWrite or LedgerOperation.RecoveryWrite;
        var found = entry.Operation == LedgerOperation.Read && (entry.Answer is nameof(ReplicaReadCase.Batch) or CorruptAnswer || !recoverers.Contains(entry.Client));
        return written || found ? entry.Address : null;
    }

    private static bool IsRecoveryRead(LedgerEvent entry, HashSet<string> recoverers) =>
        entry.Kind is LedgerEventKind.Called or LedgerEventKind.Served or LedgerEventKind.Answered or LedgerEventKind.Handled
        && entry.Operation == LedgerOperation.Read
        && recoverers.Contains(entry.Client);

    private static string Set(IEnumerable<string> members) => "Set(" + string.Join(", ", members) + ")";

    private static string List(IEnumerable<string> members) => "[" + string.Join(", ", members) + "]";

    private static string Str(string value) => "\"" + value.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A trace rendered as records, with the constants it fixes.
    /// </summary>
    internal sealed record RenderedTrace(
        IReadOnlyList<string> Steps,
        long PositionCount,
        IReadOnlyList<string> RecoveryClients,
        IReadOnlyList<string> CheckIds,
        IReadOnlyList<string> FetcherIds,
        int DuplicateBudget,
        bool HasCorruption);

    /// <summary>
    /// A request whose record reached its node's disk before the node crashed, without an answer the trace logged, which
    /// event <paramref name="Index"/> is the first to show: a writer write (<c>write</c>) or a recovery write (<c>copy</c>)
    /// whose DATA the event holds, or a seal (<c>seal</c>) whose seal it reports.
    /// </summary>
    private sealed record Landed(int Index, string Operation, string Client, bool FromFetcher, string Node, long Address, long Tail);

    /// <summary>
    /// One ledger's events, ordered and bounded, with what the renderer derives across them, rendered one event at a
    /// time.
    /// </summary>
    private sealed class Translation
    {
        private readonly List<LedgerEvent> _input;
        private readonly HashSet<string> _recoverers;
        private readonly long _slots;
        private readonly Dictionary<long, LedgerEvent> _calls;
        private readonly Dictionary<long, string> _checks = [];
        private readonly Dictionary<string, long> _checkTails = [];
        private readonly Dictionary<int, List<Landed>> _landed = [];
        private readonly HashSet<int> _delivered = [];
        private readonly HashSet<int> _kept = [];
        private readonly HashSet<Landed> _keptLanded = [];
        private readonly HashSet<long> _acquired = [];
        private readonly HashSet<long> _peeks = [];
        private readonly Dictionary<long, long> _recoveryTails = [];
        private readonly Dictionary<long, string> _learnedCalls = [];
        private readonly HashSet<string> _fetchers = [];
        private readonly HashSet<string> _learnedChecks = [];
        private readonly HashSet<string> _writerClients;
        private readonly HashSet<long> _appendedWritten = [];
        private readonly bool _sequenced;
        private long _acknowledged;
        private readonly HashSet<long> _tails = [];
        private readonly Dictionary<int, string> _repairs = [];
        private readonly Dictionary<string, int> _replicaIds = [];
        private readonly Dictionary<string, int> _recovererIds = [];
        private readonly Dictionary<string, int> _readerIds = [];
        private readonly Dictionary<string, int> _fetcherIds = [];

        public Translation(List<LedgerEvent> input, HashSet<string> recoverers, long slots, IReadOnlyList<string>? nodes)
        {
            _input = input;
            _recoverers = recoverers;
            _slots = slots;
            _calls = input.Where(static entry => entry.Kind == LedgerEventKind.Called).ToDictionary(static entry => entry.Call!.Value);
            _writerClients = [.. input
                .Where(static entry => entry.Kind is LedgerEventKind.Completed or LedgerEventKind.Stopped || entry is { Kind: LedgerEventKind.Acquired, ContiguousTail: null })
                .Select(static entry => entry.Client)];
            _sequenced = _writerClients.Count > 0;
            ClassifyClientRequests();
            FindRepairs();
            AssignChecks();
            FindLearners();
            FindLanded();
            FindDelivered();
            FindKept();
            AssignIds(nodes);
        }

        private void AssignIds(IReadOnlyList<string>? nodes)
        {
            if (nodes is not null)
            {
                for (var index = 0; index < nodes.Count; index++)
                {
                    _replicaIds[nodes[index]] = index;
                }
            }

            foreach (var node in _input.Select(static entry => entry.Node).Where(static node => node is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                _ = _replicaIds.TryAdd(node!, _replicaIds.Count);
            }

            foreach (var recoverer in _recoverers.Order(StringComparer.Ordinal))
            {
                _recovererIds[recoverer] = _recovererIds.Count;
            }

            foreach (var check in _checkTails.Keys.Order(StringComparer.Ordinal))
            {
                _readerIds[check] = _readerIds.Count;
            }

            foreach (var fetcher in _fetchers.Order(StringComparer.Ordinal))
            {
                _fetcherIds[fetcher] = _fetcherIds.Count;
            }
        }

        private int ReplicaId(string node) => _replicaIds[node];

        private int RecovererId(string recoverer) => _recovererIds[recoverer];

        private int ReaderId(string check) => _readerIds[check];

        private int FetcherIntId(string fetcher) => _fetcherIds[fetcher];

        private static string CheckId(long call) => Invariant($"check-{call}");

        /// <summary>
        /// Returns the model's fetcher which stands for <paramref name="client"/>'s view of the ledger map, once it learned
        /// the recorded <c>T</c>.
        /// </summary>
        private static string FetcherId(string client) => Invariant($"{client}/map");

        /// <summary>
        /// Returns the model's client of a recovery write <paramref name="client"/> sent: a recovery's, or a reader's read
        /// repair, which the model's fetcher of the reader's map view sends (ledger-protocol §8.3).
        /// </summary>
        private string CopyClient(string client) => _recoverers.Contains(client) ? client : FetcherId(client);

        private static string Call(string action, params string[] args) =>
            args.Length == 0 ? action : action + "(" + string.Join(", ", args) + ")";

        private static string Bool(bool value) => value ? "true" : "false";

        /// <summary>
        /// Returns the model's acquire of an address: the writer sends it carrying its completed prefix, the tail the
        /// acquisition, or the address's first write, carried.
        /// </summary>
        private static string Acquire(LedgerEvent entry) =>
            Call("stepAcquire", Number(entry.Address!.Value), Number(entry.ContiguousTail!.Value));

        private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

        /// <summary>
        /// Returns the model's begin of a recovery, from the known contiguous tail it starts from: the one its begin logged,
        /// or, for a primary's, the one its seals carry.
        /// </summary>
        private string RecoveryBegan(LedgerEvent entry) =>
            Call("stepRecover", Number(RecovererId(entry.Client)), Number(entry.ContiguousTail ?? _recoveryTails[entry.Call!.Value]));

        /// <summary>
        /// Raises the acknowledged prefix by an append which is not a <see cref="LedgerWriter"/>'s: its write reached
        /// <c>Q</c> <see cref="ReplicaAnswers.Written"/> answers, which acknowledges it to its caller, and the prefix is the
        /// addresses so acknowledged with every lower one (LP-I3). A LedgerWriter's acknowledgements are its completions.
        /// </summary>
        private void Acknowledge(LedgerEvent entry)
        {
            if (_sequenced || entry.Answer != nameof(ReplicaAnswers.Written))
            {
                return;
            }

            _ = _appendedWritten.Add(entry.Address!.Value);
            while (_appendedWritten.Contains(_acknowledged))
            {
                _acknowledged++;
            }
        }

        /// <summary>
        /// Finds what the client of a request or a step shows only across events: a recovery executor's reads sent outside
        /// its recoveries, which read decided positions as a reader's do (peeks); the tail a recovery which logs none
        /// starts from, which its seals carry; and the requests which carry the recorded <c>T</c> their client learned.
        /// </summary>
        private void ClassifyClientRequests()
        {
            var recovering = new HashSet<string>(StringComparer.Ordinal);
            var untailed = new Dictionary<string, long>(StringComparer.Ordinal);
            var learned = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var entry in _input)
            {
                if (entry.Kind == LedgerEventKind.RecoveryBegan)
                {
                    _ = recovering.Add(entry.Client);
                    if (entry.ContiguousTail is null)
                    {
                        untailed[entry.Client] = entry.Call!.Value;
                    }
                }
                else if (entry.Kind is LedgerEventKind.RecoveryEnded or LedgerEventKind.RecoveryFailed)
                {
                    _ = recovering.Remove(entry.Client);
                }
                else if (entry.Kind == LedgerEventKind.RecordedTailObserved)
                {
                    learned[entry.Client] = entry.End!.Value;
                    _ = _fetchers.Add(FetcherId(entry.Client));
                }
                else if (entry.Kind == LedgerEventKind.Called)
                {
                    var call = entry.Call!.Value;
                    if (entry.Operation == LedgerOperation.Seal && untailed.Remove(entry.Client, out var recovery))
                    {
                        _recoveryTails[recovery] = entry.ContiguousTail!.Value;
                    }

                    if (entry.Operation == LedgerOperation.Read && _recoverers.Contains(entry.Client) && !recovering.Contains(entry.Client))
                    {
                        _ = _peeks.Add(call);
                    }

                    if (entry.Operation == LedgerOperation.RecoveryWrite && !_recoverers.Contains(entry.Client))
                    {
                        _ = _fetchers.Add(FetcherId(entry.Client));
                    }

                    if (learned.TryGetValue(entry.Client, out var recorded) && entry.ContiguousTail == recorded)
                    {
                        _learnedCalls[call] = FetcherId(entry.Client);
                    }
                }
            }
        }

        public RenderedTrace ToTrace()
        {
            var steps = new List<string>();
            int? close = null;
            for (var index = 0; index < _input.Count; index++)
            {
                var entry = _input[index];
                if (entry.Kind == LedgerEventKind.RecoveryBegan && close is { } at)
                {
                    // An executor reads the map, finds the ledger open, and seals (ledgers.md §8.1): one which begins after
                    // the close read the map before it, and its seals, which change nothing until they land, wait in
                    // flight. Its own close then loses.
                    steps.Insert(at, RecoveryBegan(entry));
                    close = at + 1;
                    continue;
                }

                if (entry.Kind == LedgerEventKind.Closed)
                {
                    close = steps.Count;
                }

                if (entry is { Kind: LedgerEventKind.Served, Call: { } call } && _learnedCalls.TryGetValue(call, out var fetcher))
                {
                    // The replica retains the recorded T the request carries when it admits it (LR-I6).
                    steps.Add(Call("stepRetain", Number(FetcherIntId(fetcher)), Number(ReplicaId(entry.Node!))));
                }

                if (Step(index) is { } step)
                {
                    steps.Add(step);
                }
            }

            var requests = _input.Count(static entry => entry.Kind is LedgerEventKind.Called or LedgerEventKind.Served);
            var corrupt = _input.Any(entry =>
                entry.Kind == LedgerEventKind.Served && entry.Answer == CorruptAnswer
                || entry.Recovered is { } recovered && recovered.Faulty.Any(slot => slot < _slots));
            return new RenderedTrace(
                steps,
                _slots,
                [.. _recoverers.Order(StringComparer.Ordinal)],
                [.. _checkTails.Keys.Order(StringComparer.Ordinal)],
                [.. _fetchers.Order(StringComparer.Ordinal)],
                requests + 1,
                corrupt);
        }

        /// <summary>
        /// Returns the record event <paramref name="index"/> becomes, or <see langword="null"/> for an event the model
        /// takes no step for.
        /// </summary>
        private string? Step(int index)
        {
            var entry = _input[index];
            switch (entry.Kind)
            {
                case LedgerEventKind.Acquired when entry.ContiguousTail is null:
                    // A LedgerWriter's acquisition carries no tail the trace sees; its slot's first write, which carries
                    // the tail, is the model's acquire.
                    return null;
                case LedgerEventKind.Acquired:
                    _ = _acquired.Add(entry.Address!.Value);
                    return Acquire(entry);
                case LedgerEventKind.Appended:
                    Acknowledge(entry);
                    return Call("stepAppended", Number(entry.Address!.Value), Str(entry.Answer!));
                case LedgerEventKind.Called:
                    var (operation, client) = Role(entry);
                    if (operation == "write" && _acquired.Add(entry.Address!.Value))
                    {
                        return Acquire(entry);
                    }

                    if (operation == "write")
                    {
                        return Call(
                            "stepCallWrite",
                            Number(ReplicaId(entry.Node!)),
                            Number(entry.Address!.Value),
                            Number(entry.ContiguousTail ?? 0),
                            Bool(_learnedCalls.ContainsKey(entry.Call!.Value)));
                    }

                    if (operation == "tail")
                    {
                        // The writer's explicit contiguous-tail write sends one tail to every replica, which only a tail
                        // above every earlier one it carried starts: the model's WriteTail at its first request.
                        return _tails.Add(entry.ContiguousTail!.Value) ? Call("stepTail", Number(entry.ContiguousTail.Value)) : null;
                    }

                    return operation switch
                    {
                        "seal" => Call("stepCallSeal", Number(RecovererId(client)), Number(ReplicaId(entry.Node!))),
                        "read" => Call("stepCallRead", Number(RecovererId(client)), Number(ReplicaId(entry.Node!)), Number(entry.Address!.Value), Bool(entry.Marked)),
                        "copy" => Call(
                            "stepCallCopy",
                            Bool(!_recoverers.Contains(client)),
                            Number(_recoverers.Contains(client) ? RecovererId(client) : FetcherIntId(client)),
                            Number(ReplicaId(entry.Node!)),
                            Number(entry.Address!.Value),
                            Number(entry.ContiguousTail ?? 0)),
                        _ => null,
                    };
                case LedgerEventKind.Served:
                    return Served(index, entry);
                case LedgerEventKind.Answered:
                    return Answered(entry);
                case LedgerEventKind.Handled when entry.Answer == LedgerTrace.StoppedAnswer:
                    return null;
                case LedgerEventKind.Handled:
                    return Receive(RecoveryOperation(entry.Operation!.Value), entry.Client, entry.Node!, entry.Address ?? -1, entry.Marked, entry.Answer!, entry.Tail);
                case LedgerEventKind.RecoveryBegan:
                    return RecoveryBegan(entry);
                case LedgerEventKind.RecoveryEnded:
                    // A primary's recovery logs no c, which its other operations may pass meanwhile; its seal answers,
                    // which the trace binds, decide it.
                    return Call("stepRecovered", Number(RecovererId(entry.Client)), Number(entry.ContiguousTail ?? -1), Number(entry.End!.Value));
                case LedgerEventKind.TailReadBegan:
                    var check = CheckId(entry.Call!.Value);
                    return Call("stepCheck", Number(ReaderId(check)), Number(_checkTails.GetValueOrDefault(check)), Bool(_learnedChecks.Contains(check)));
                case LedgerEventKind.TailReadAnswered:
                    var answered = CheckId(entry.Call!.Value);
                    var isWriterCheck = _writerClients.Contains(entry.Client);
                    var writerId = isWriterCheck && _fetcherIds.TryGetValue(FetcherId(entry.Client), out var id) ? id : -1;
                    return Call(
                        "stepChecked",
                        Number(ReaderId(answered)),
                        Number(entry.Tail!.Value.ContiguousTail.Value),
                        Bool(entry.Tail.Value.IsSealed),
                        Bool(isWriterCheck),
                        Number(writerId));
                case LedgerEventKind.Restarted:
                    return Restarted(index, entry);
                case LedgerEventKind.Damaged:
                    // Damage to the node's media, which names no ledger, may reach this ledger's records.
                    return Call("stepDamage", Number(ReplicaId(entry.Node!)));
                case LedgerEventKind.Dropped:
                    throw new NotSupportedException("Trace validation does not map a drop of the traced ledger.");
                case LedgerEventKind.Completed:
                    // A LedgerWriter's slot completed, with every lower slot (LP-I3): its append is acknowledged.
                    _acknowledged = Math.Max(_acknowledged, entry.Address!.Value + 1);
                    return Call("stepCompleted", Number(entry.Address!.Value));
                case LedgerEventKind.Stopped:
                    return Call("stepStopped");
                case LedgerEventKind.Closed when _recoverers.Contains(entry.Client):
                    return Call("stepClose", Number(RecovererId(entry.Client)), Number(entry.End!.Value), Number(_acknowledged));
                case LedgerEventKind.Closed:
                    throw new NotSupportedException(Invariant($"Trace validation maps a close by a recovery the trace observes, not {entry}."));
                case LedgerEventKind.Superseded:
                    return Call("stepSuperseded", Number(RecovererId(entry.Client)));
                case LedgerEventKind.RecordedTailObserved:
                    return Call("stepLearn", Number(FetcherIntId(FetcherId(entry.Client))), Number(entry.End!.Value));
                case LedgerEventKind.RecoveryFailed:
                case LedgerEventKind.TailReadFailed:
                    return null;
                default:
                    throw new NotSupportedException(Invariant($"Trace validation does not map {entry}."));
            }
        }

        /// <summary>
        /// Returns the model's operation and client for a request, or an answer a node served: the writer's writes, a
        /// recovery's seals, reads, and recovery writes, a reader's read repairs, the recovery writes of its map view's
        /// fetcher, a reader's reads of positions (peeks), which the model checks
        /// against the replica's state where they are served, a check's contiguous-tail reads, the writer's explicit
        /// contiguous-tail writes, its contiguous-tail reads outside any check, and another client's learns, its
        /// contiguous-tail reads outside any check, which its map view's fetcher sends (ledger-protocol §8.2).
        /// </summary>
        private (string Operation, string Client) Role(LedgerEvent entry) => entry.Operation switch
        {
            LedgerOperation.WriterWrite => ("write", Writer),
            LedgerOperation.Seal => ("seal", entry.Client),
            LedgerOperation.Read => _recoverers.Contains(entry.Client) && !_peeks.Contains(entry.Call ?? -1) ? ("read", entry.Client) : ("peek", entry.Client),
            LedgerOperation.RecoveryWrite => ("copy", CopyClient(entry.Client)),
            LedgerOperation.TailRead when entry.Call is { } call && _checks.TryGetValue(call, out var check) => ("check", check),
            LedgerOperation.TailRead when _writerClients.Contains(entry.Client) => ("tail", Writer),
            LedgerOperation.TailRead => ("learn", FetcherId(entry.Client)),
            null => throw new NotSupportedException(Invariant($"{entry} names no operation.")),
            _ => throw new NotSupportedException(Invariant($"Trace validation does not map {entry}.")),
        };

        private string Served(int index, LedgerEvent entry)
        {
            var (operation, client) = Role(entry);
            var called = entry.Call is { } call ? _calls.GetValueOrDefault(call) : null;
            var source = called ?? entry;
            var (landed, copied, sealedBy) = LandedArgs(index);
            var replica = Number(ReplicaId(source.Node!));
            var pos = Number(source.Address ?? -1);
            var carriedTail = Number(source.ContiguousTail ?? 0);
            var marked = Bool(source.Marked);
            var answer = Str(entry.Answer!);
            var repTail = Number(entry.Tail?.ContiguousTail.Value ?? 0);
            var isSealed = Bool(entry.Tail?.IsSealed ?? false);
            var keep = Bool(_kept.Contains(index));
            var deliver = Bool(_delivered.Contains(index));
            return operation switch
            {
                "write" => Call("stepServeWrite", replica, pos, carriedTail, answer, keep, deliver, landed, copied, sealedBy),
                "seal" => Call("stepServeSeal", Number(RecovererId(client)), replica, answer, repTail, isSealed, keep, deliver, landed, copied, sealedBy),
                "read" => Call("stepServeRead", Number(RecovererId(client)), replica, pos, marked, answer, keep, deliver, landed, copied, sealedBy),
                "copy" => Call(
                    "stepServeCopy",
                    Bool(!_recoverers.Contains(client)),
                    Number(_recoverers.Contains(client) ? RecovererId(client) : FetcherIntId(client)),
                    replica,
                    pos,
                    carriedTail,
                    answer,
                    keep,
                    deliver,
                    landed,
                    copied,
                    sealedBy),
                "check" => Call("stepServeCheck", Number(ReaderId(client)), replica, answer, repTail, isSealed, deliver, landed, copied, sealedBy),
                "peek" => Call(
                    "stepServePeek",
                    replica,
                    pos,
                    carriedTail,
                    answer,
                    Number(_repairs.TryGetValue(index, out var fetcher) ? FetcherIntId(fetcher) : -1),
                    landed,
                    copied,
                    sealedBy),
                "tail" => Call("stepServeTail", replica, carriedTail, keep, landed, copied, sealedBy),
                "learn" => Call("stepServeLearn", Number(FetcherIntId(client)), replica, carriedTail, answer, repTail, isSealed, landed, copied, sealedBy),
                _ => throw new NotSupportedException(Invariant($"Trace validation does not map served {operation}.")),
            };
        }

        /// <summary>
        /// Finds each reader's read repair (ledger-protocol §8.3) at the answer which started it: the reader's read of the
        /// repaired replica, the latest before the repair, which answered <see cref="ReplicaReadCase.Missing"/> or corrupt.
        /// The model's fetcher reads below its tail and sends the repair in one step (<c>ReadBelowTail</c>), so the
        /// repair's own request is one already in flight.
        /// </summary>
        private void FindRepairs()
        {
            var lacking = new Dictionary<(string Client, string Node, long Address), int>();
            for (var index = 0; index < _input.Count; index++)
            {
                var entry = _input[index];
                if (entry is { Kind: LedgerEventKind.Served, Call: { } call } && _calls.TryGetValue(call, out var read)
                    && read.Operation == LedgerOperation.Read && Role(read).Operation == "peek"
                    && entry.Answer is nameof(ReplicaReadCase.Missing) or CorruptAnswer)
                {
                    lacking[(read.Client, read.Node!, read.Address!.Value)] = index;
                }
                else if (entry is { Kind: LedgerEventKind.Called, Operation: LedgerOperation.RecoveryWrite } && !_recoverers.Contains(entry.Client))
                {
                    var key = (entry.Client, entry.Node!, entry.Address!.Value);
                    if (!lacking.Remove(key, out var served))
                    {
                        throw new NotSupportedException(Invariant($"Trace validation maps a reader's repair only after its read of the repaired replica lacked the slot: {entry}."));
                    }

                    _repairs[served] = FetcherId(entry.Client);
                }
            }
        }

        /// <summary>
        /// Returns the record an answer which reached its client becomes: the writer receives its answers as they arrive,
        /// and a check's answers arrive for it to handle in any order. A recovery's answers count when it handles them,
        /// and a reader's reads are checked where they are served.
        /// </summary>
        private string? Answered(LedgerEvent entry)
        {
            var answer = entry.Answer!;
            if (answer.EndsWith("Exception", StringComparison.Ordinal) && answer is not CorruptAnswer)
            {
                return null;
            }

            var called = _calls[entry.Call!.Value];
            return Role(called) switch
            {
                ("write", _) => Receive("write", Writer, entry.Node!, called.Address!.Value, marked: false, answer, tail: null),
                ("check", var check) => Call("stepArrive", Number(ReaderId(check)), Number(ReplicaId(entry.Node!))),
                _ => null,
            };
        }

        private string Receive(string operation, string client, string node, long address, bool marked, string answer, LedgerReplicaTail? tail) =>
            operation switch
            {
                "write" => Call("stepReceiveWrite", Number(ReplicaId(node)), Number(address), Str(answer)),
                "seal" => Call("stepReceiveSeal", Number(RecovererId(client)), Number(ReplicaId(node)), Str(answer), Number(tail?.ContiguousTail.Value ?? 0)),
                "read" => Call("stepReceiveRead", Number(RecovererId(client)), Number(ReplicaId(node)), Number(address), Bool(marked), Str(answer)),
                "copy" => Call(
                    "stepReceiveCopy",
                    Bool(!_recoverers.Contains(client)),
                    Number(_recoverers.Contains(client) ? RecovererId(client) : FetcherIntId(client)),
                    Number(ReplicaId(node)),
                    Number(address),
                    Str(answer)),
                _ => throw new NotSupportedException(Invariant($"Trace validation does not map receive {operation}.")),
            };

        private static string RecoveryOperation(LedgerOperation operation) => operation switch
        {
            LedgerOperation.Seal => "seal",
            LedgerOperation.Read => "read",
            LedgerOperation.RecoveryWrite => "copy",
            LedgerOperation.WriterWrite or LedgerOperation.TailRead => throw new NotSupportedException(Invariant($"A recovery handles no {operation} answer.")),
            _ => throw new NotSupportedException(Invariant($"Trace validation does not map {operation}.")),
        };

        private string Restarted(int index, LedgerEvent entry)
        {
            var recovered = entry.Recovered ?? throw new NotSupportedException(Invariant($"{entry} records no recovered replica."));
            var (landed, copied, sealedBy) = LandedArgs(index);
            return Call(
                "stepRestart",
                Number(ReplicaId(entry.Node!)),
                Bool(recovered.Held),
                Bool(recovered.Sealed),
                Set(recovered.Data.Select(Number)),
                List(recovered.Faulty.Where(slot => slot < _slots).Order().Select(Number)),
                landed,
                copied,
                sealedBy);
        }

        /// <summary>
        /// Returns the arguments naming the requests that landed without an answer and event <paramref name="index"/>
        /// first shows: <c>landed</c>, the writer writes, <c>(p, t, keep)</c>; <c>copied</c>, the recovery writes,
        /// <c>(fromFetcher, c, p, t, keep)</c>; and <c>sealedBy</c>, the seals, <c>(c, keep)</c>, where
        /// <c>keep</c> leaves the request in flight for a later delivery.
        /// </summary>
        private (string Landed, string Copied, string SealedBy) LandedArgs(int index)
        {
            var landed = _landed.GetValueOrDefault(index) ?? [];
            string Keep(Landed request) => Bool(_keptLanded.Contains(request));
            return (
                List(landed.Where(static request => request.Operation == "write").Select(request => Invariant($"({request.Address}, {request.Tail}, {Keep(request)})"))),
                List(landed.Where(static request => request.Operation == "copy").Select(request => Invariant($"({Bool(request.FromFetcher)}, {(request.FromFetcher ? FetcherIntId(request.Client) : RecovererId(request.Client))}, {request.Address}, {request.Tail}, {Keep(request)})"))),
                List(landed.Where(static request => request.Operation == "seal").Select(request => Invariant($"({RecovererId(request.Client)}, {Keep(request)})"))));
        }

        /// <summary>
        /// Assigns each contiguous-tail read to the check its client was making when it sent it, and finds the tail each
        /// check carried.
        /// </summary>
        private void AssignChecks()
        {
            var open = new Dictionary<string, string>(StringComparer.Ordinal);
            var carried = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in _input)
            {
                if (entry.Kind == LedgerEventKind.TailReadBegan)
                {
                    var check = CheckId(entry.Call!.Value);
                    open[entry.Client] = check;
                    _checkTails[check] = 0;
                }
                else if (entry.Kind == LedgerEventKind.Called && entry.Operation == LedgerOperation.TailRead && open.TryGetValue(entry.Client, out var current))
                {
                    _checks[entry.Call!.Value] = current;
                    if (carried.Add(current))
                    {
                        _checkTails[current] = entry.ContiguousTail!.Value;
                        if (_learnedCalls.ContainsKey(entry.Call.Value))
                        {
                            _ = _learnedChecks.Add(current);
                        }
                    }
                }
                else if (entry.Kind is LedgerEventKind.TailReadAnswered or LedgerEventKind.TailReadFailed)
                {
                    _ = open.Remove(entry.Client);
                }
            }
        }

        /// <summary>
        /// Finds the clients which learn a contiguous tail from one replica without a seal check (ledger-protocol §8.2):
        /// a contiguous-tail read outside any check, by a client which is not the writer, whose map view the model's
        /// fetcher stands for.
        /// </summary>
        private void FindLearners()
        {
            foreach (var entry in _input)
            {
                if (entry is { Kind: LedgerEventKind.Called, Operation: LedgerOperation.TailRead } && Role(entry).Operation == "learn")
                {
                    _ = _fetchers.Add(FetcherId(entry.Client));
                }
            }
        }

        /// <summary>
        /// Finds the requests in flight to a node whose records reached its disk before the node crashed, without an answer
        /// the trace logged, and the event which first shows each (test/Durable.Storage/docs/spec/verification.md §5.1): the node's restart,
        /// which recovered DATA no answer of the node held or a seal none reported, or, in the process the crash ended, a
        /// read which returned such DATA or an answer which reported such a seal, which no answer of the request's own
        /// follows in that process. A writer write, a recovery write of the position, or a seal counts when no delivery of
        /// it was served before the event; a request sent before an earlier restart stays in flight, and the next process
        /// may take it.
        /// </summary>
        private void FindLanded()
        {
            var served = new HashSet<long>();
            var held = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            var sealedNodes = new HashSet<string>(StringComparer.Ordinal);
            var sent = new List<LedgerEvent>();
            var lastRestart = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < _input.Count; index++)
            {
                if (_input[index] is { Kind: LedgerEventKind.Restarted, Recovered: not null, Node: { } restarted })
                {
                    lastRestart[restarted] = index;
                }
            }

            HashSet<long> Held(string node)
            {
                if (!held.TryGetValue(node, out var positions))
                {
                    held[node] = positions = [];
                }

                return positions;
            }

            for (var index = 0; index < _input.Count; index++)
            {
                var entry = _input[index];
                if (entry.Kind == LedgerEventKind.Called && entry.Operation is LedgerOperation.WriterWrite or LedgerOperation.RecoveryWrite or LedgerOperation.Seal)
                {
                    sent.Add(entry);
                }
                else if (entry.Kind == LedgerEventKind.Served && entry.Call is { } call)
                {
                    var node = entry.Node!;
                    if (lastRestart.GetValueOrDefault(node, -1) > index)
                    {
                        // The export orders a request's answer before the answers its record explains within one process,
                        // so only a process which a crash ends answers from a record whose own answer it never logged.
                        var data = entry is { Operation: LedgerOperation.Read, Answer: nameof(ReplicaReadCase.Batch), Address: { } address } && !Held(node).Contains(address)
                            ? new HashSet<long> { address }
                            : [];
                        Land(index, node, data, seal: ReportsSealed(entry) && !sealedNodes.Contains(node));
                    }

                    _ = served.Add(call);
                    if (IsPublished(entry) || entry is { Operation: LedgerOperation.Read, Answer: nameof(ReplicaReadCase.Batch) })
                    {
                        _ = Held(node).Add(entry.Address!.Value);
                    }

                    if (IsSeal(entry) || ReportsSealed(entry))
                    {
                        _ = sealedNodes.Add(node);
                    }
                }
                else if (entry.Kind == LedgerEventKind.Restarted && entry.Recovered is { } recovered)
                {
                    var node = entry.Node!;
                    var known = Held(node);
                    var resealed = recovered is { Held: true, Sealed: true };
                    Land(index, node, [.. recovered.Data.Where(address => !known.Contains(address))], seal: resealed && !sealedNodes.Contains(node));
                    held[node] = [.. recovered.Data];
                    _ = resealed ? sealedNodes.Add(node) : sealedNodes.Remove(node);
                }
            }

            void Land(int index, string node, HashSet<long> data, bool seal)
            {
                var pending = sent.Where(called => called.Node == node && !served.Contains(called.Call!.Value)).ToList();
                List<Landed> landed =
                [
                    .. pending
                        .Where(called => called.Operation == LedgerOperation.WriterWrite && data.Contains(called.Address!.Value))
                        .GroupBy(static called => called.Address!.Value)
                        .Select(candidate => new Landed(index, "write", Writer, FromFetcher: false, node, candidate.Key, candidate.Last().ContiguousTail!.Value)),
                    .. pending
                        .Where(called => called.Operation == LedgerOperation.RecoveryWrite && data.Contains(called.Address!.Value))
                        .Select(called => new Landed(index, "copy", CopyClient(called.Client), FromFetcher: !_recoverers.Contains(called.Client), node, called.Address!.Value, called.ContiguousTail!.Value))
                        .Distinct(),
                    .. pending
                        .Where(called => seal && called.Operation == LedgerOperation.Seal)
                        .Select(called => new Landed(index, "seal", called.Client, FromFetcher: false, node, -1, 0))
                        .Distinct(),
                ];
                if (landed.Count > 0)
                {
                    _landed[index] = landed;
                }
            }
        }

        /// <summary>
        /// Finds the deliveries whose answers reached their clients: for each request, the first delivery whose answer is
        /// the one its client logged.
        /// </summary>
        private void FindDelivered()
        {
            var answers = _input.Where(static entry => entry.Kind == LedgerEventKind.Answered).ToDictionary(static entry => entry.Call!.Value);
            var reached = new HashSet<long>();
            for (var index = 0; index < _input.Count; index++)
            {
                var entry = _input[index];
                if (entry.Kind == LedgerEventKind.Served && entry.Call is { } call && !reached.Contains(call)
                    && answers.TryGetValue(call, out var answer) && answer.Answer == entry.Answer && answer.Tail == entry.Tail)
                {
                    _ = _delivered.Add(index);
                    _ = reached.Add(call);
                }
            }
        }

        /// <summary>
        /// Finds the deliveries which leave their request in flight, those followed by another delivery of the same
        /// request before the request is sent again: served events, and requests which landed without an answer.
        /// </summary>
        private void FindKept()
        {
            var last = new Dictionary<string, (int Index, Landed? Landed, bool Served)>(StringComparer.Ordinal);
            void Note(string identity, int index, Landed? request, bool served)
            {
                if (served && last.TryGetValue(identity, out var previous) && previous.Served)
                {
                    if (previous.Landed is { } kept)
                    {
                        _ = _keptLanded.Add(kept);
                    }
                    else
                    {
                        _ = _kept.Add(previous.Index);
                    }
                }

                last[identity] = (index, request, served);
            }

            for (var index = 0; index < _input.Count; index++)
            {
                // A request which landed is delivered just before the event which shows it.
                foreach (var request in _landed.GetValueOrDefault(index) ?? [])
                {
                    Note(Identity(request), index, request, served: true);
                }

                var entry = _input[index];
                if (entry.Kind is LedgerEventKind.Called or LedgerEventKind.Served && Identity(entry) is { } identity)
                {
                    Note(identity, index, request: null, entry.Kind == LedgerEventKind.Served);
                }
            }
        }

        /// <summary>
        /// Returns the identity of the model's request which landed without an answer, as <see cref="Identity(LedgerEvent)"/>
        /// returns it.
        /// </summary>
        private static string Identity(Landed request) => request.Operation switch
        {
            "write" or "copy" => Invariant($"{request.Operation}|{request.Client}|{request.Node}|{request.Address}|{request.Tail}"),
            _ => Invariant($"{request.Operation}|{request.Client}|{request.Node}"),
        };

        /// <summary>
        /// Returns the identity of the model's request a <see cref="LedgerEventKind.Called"/> or
        /// <see cref="LedgerEventKind.Served"/> event concerns, as the model's set of requests in flight holds it, or
        /// <see langword="null"/> for a request the model keeps no set of.
        /// </summary>
        private string? Identity(LedgerEvent entry)
        {
            var called = entry;
            if (entry.Kind != LedgerEventKind.Called)
            {
                called = entry.Call is { } call ? _calls.GetValueOrDefault(call) : null;
            }

            if (called is null)
            {
                return null;
            }

            var (operation, client) = Role(called);
            return operation switch
            {
                "write" or "copy" => Invariant($"{operation}|{client}|{called.Node}|{called.Address}|{called.ContiguousTail}"),
                "seal" => Invariant($"seal|{client}|{called.Node}"),
                "read" => Invariant($"read|{client}|{called.Node}|{called.Address}|{called.Marked}"),
                "tail" => Invariant($"tail|{called.Node}|{called.ContiguousTail}"),
                _ => null,
            };
        }
    }
}

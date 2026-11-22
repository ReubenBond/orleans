// Ported from Porcupine (https://github.com/anishathalye/porcupine, commit 97cd067), Copyright (c) Anish Athalye,
// under the MIT License in PORCUPINE-LICENSE.md.

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Checks histories for linearizability against a <see cref="LinearizabilityModel{TState, TInput, TOutput}"/> with
/// Porcupine's algorithm (test/Durable.Storage/docs/spec/verification.md §3.4).
/// </summary>
/// <remarks>
/// <para>
/// Porcupine "implements the algorithm described in Faster linearizability checking via P-compositionality [Horn and
/// Kroening, FORTE 2015], an optimization of the algorithm described in Testing for Linearizability [Lowe 2017]". The
/// search walks the history's invocations and responses in time order, tries to linearize each operation whose
/// invocation precedes the first pending response, and backtracks at a response none of whose candidates linearizes. A
/// cache of (linearized operations, state) pairs prunes every configuration already explored.
/// </para>
/// <para>
/// Refuting a partition can take exponential time where another partition is refuted at once, so Porcupine searches
/// partitions in parallel and stops at the first illegal one. This port searches them on the calling thread in turns of
/// <see cref="Quantum"/> steps each, round robin in partition order, and also stops at the first illegal one: the
/// work is bounded as Porcupine's is, and a check is deterministic, so it runs inside a deterministic simulation. A
/// verbose check, as Porcupine's, searches every partition to its end unless asked to stop at the first illegal one.
/// </para>
/// <para>
/// A cancelled check returns <see cref="LinearizabilityVerdict.Unknown"/> unless it has already found an illegal
/// partition; a timeout is a token from <c>new CancellationTokenSource(timeout, timeProvider)</c>.
/// </para>
/// </remarks>
internal static class LinearizabilityChecker
{
    /// <summary>
    /// The number of search steps a partition takes in each turn.
    /// </summary>
    internal const int Quantum = 1_024;

    /// <summary>
    /// Checks whether a history of operations is linearizable.
    /// </summary>
    /// <param name="model">The sequential specification.</param>
    /// <param name="history">The operations, in any order.</param>
    /// <param name="cancellationToken">Ends the search with <see cref="LinearizabilityVerdict.Unknown"/>.</param>
    /// <returns>The verdict.</returns>
    public static LinearizabilityVerdict Check<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryOperation<TInput, TOutput>> history,
        CancellationToken cancellationToken = default)
        where TInput : notnull =>
        Run(model, history, explain: false, stopAtIllegal: true, cancellationToken).Verdict;

    /// <summary>
    /// Checks whether a history of events is linearizable. Each event's position in the history is its timestamp.
    /// </summary>
    /// <param name="model">The sequential specification.</param>
    /// <param name="history">The invocations and responses in the order they happened.</param>
    /// <param name="cancellationToken">Ends the search with <see cref="LinearizabilityVerdict.Unknown"/>.</param>
    /// <returns>The verdict.</returns>
    public static LinearizabilityVerdict Check<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryEvent<TInput, TOutput>> history,
        CancellationToken cancellationToken = default)
        where TInput : notnull =>
        Check(model, ToOperations(history), cancellationToken);

    /// <summary>
    /// Checks a history of operations, recording the (partial) linearizations which explain the verdict and which
    /// <see cref="PorcupineVisualization"/> draws. Every partition is searched to its end, as Porcupine's verbose check
    /// does.
    /// </summary>
    /// <param name="model">The sequential specification.</param>
    /// <param name="history">The operations, in any order.</param>
    /// <param name="cancellationToken">Ends the search with <see cref="LinearizabilityVerdict.Unknown"/>.</param>
    /// <returns>The verdict, and each partition with its verdict and linearizations.</returns>
    public static LinearizabilityAnalysis<TState, TInput, TOutput> CheckVerbose<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryOperation<TInput, TOutput>> history,
        CancellationToken cancellationToken = default)
        where TInput : notnull =>
        CheckVerbose(model, history, stopAtFirstIllegal: false, cancellationToken);

    /// <summary>
    /// Checks a history of operations, recording the (partial) linearizations which explain the verdict and which
    /// <see cref="PorcupineVisualization"/> draws.
    /// </summary>
    /// <param name="model">The sequential specification.</param>
    /// <param name="history">The operations, in any order.</param>
    /// <param name="stopAtFirstIllegal">
    /// Whether the first illegal partition ends the search, leaving the partitions still being searched
    /// <see cref="LinearizabilityVerdict.Unknown"/>, so that explaining a failure costs no more than finding it.
    /// </param>
    /// <param name="cancellationToken">Ends the search with <see cref="LinearizabilityVerdict.Unknown"/>.</param>
    /// <returns>The verdict, and each partition with its verdict and linearizations.</returns>
    public static LinearizabilityAnalysis<TState, TInput, TOutput> CheckVerbose<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryOperation<TInput, TOutput>> history,
        bool stopAtFirstIllegal,
        CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        var (verdict, searches) = Run(model, history, explain: true, stopAtFirstIllegal, cancellationToken);
        return new LinearizabilityAnalysis<TState, TInput, TOutput>(
            model,
            verdict,
            searches.ConvertAll(static search => new LinearizabilityPartition<TInput, TOutput>(search.Verdict, search.Operations, search.PartialLinearizations())));
    }

    /// <summary>
    /// Checks a history of events, recording the (partial) linearizations which explain the verdict. Each event's
    /// position in the history is its timestamp.
    /// </summary>
    /// <param name="model">The sequential specification.</param>
    /// <param name="history">The invocations and responses in the order they happened.</param>
    /// <param name="cancellationToken">Ends the search with <see cref="LinearizabilityVerdict.Unknown"/>.</param>
    /// <returns>The verdict, and each partition with its verdict and linearizations.</returns>
    public static LinearizabilityAnalysis<TState, TInput, TOutput> CheckVerbose<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryEvent<TInput, TOutput>> history,
        CancellationToken cancellationToken = default)
        where TInput : notnull =>
        CheckVerbose(model, ToOperations(history), stopAtFirstIllegal: false, cancellationToken);

    /// <summary>
    /// Checks a history of events, recording the (partial) linearizations which explain the verdict. Each event's
    /// position in the history is its timestamp.
    /// </summary>
    /// <param name="model">The sequential specification.</param>
    /// <param name="history">The invocations and responses in the order they happened.</param>
    /// <param name="stopAtFirstIllegal">
    /// Whether the first illegal partition ends the search, leaving the partitions still being searched
    /// <see cref="LinearizabilityVerdict.Unknown"/>.
    /// </param>
    /// <param name="cancellationToken">Ends the search with <see cref="LinearizabilityVerdict.Unknown"/>.</param>
    /// <returns>The verdict, and each partition with its verdict and linearizations.</returns>
    public static LinearizabilityAnalysis<TState, TInput, TOutput> CheckVerbose<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryEvent<TInput, TOutput>> history,
        bool stopAtFirstIllegal,
        CancellationToken cancellationToken = default)
        where TInput : notnull =>
        CheckVerbose(model, ToOperations(history), stopAtFirstIllegal, cancellationToken);

    /// <summary>
    /// Searches every partition in turns until all have finished, the check is cancelled, or, when
    /// <paramref name="stopAtIllegal"/>, one is illegal.
    /// </summary>
    private static (LinearizabilityVerdict Verdict, List<Search<TState, TInput, TOutput>> Searches) Run<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryOperation<TInput, TOutput>> history,
        bool explain,
        bool stopAtIllegal,
        CancellationToken cancellationToken)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(model);
        var searches = Partition(model, history).ConvertAll(operations => new Search<TState, TInput, TOutput>(model, operations, explain));
        var active = new List<Search<TState, TInput, TOutput>>(searches);
        while (active.Count > 0)
        {
            for (var index = 0; index < active.Count;)
            {
                var search = active[index];
                if (!search.Advance(Quantum, cancellationToken))
                {
                    index++;
                    continue;
                }

                active.RemoveAt(index);
                if (stopAtIllegal && search.Verdict == LinearizabilityVerdict.Illegal)
                {
                    foreach (var unfinished in active)
                    {
                        unfinished.Stop(LinearizabilityVerdict.Unknown);
                    }

                    return (LinearizabilityVerdict.Illegal, searches);
                }
            }
        }

        if (searches.Exists(static search => search.Verdict == LinearizabilityVerdict.Illegal))
        {
            return (LinearizabilityVerdict.Illegal, searches);
        }

        return (searches.Exists(static search => search.Verdict == LinearizabilityVerdict.Unknown) ? LinearizabilityVerdict.Unknown : LinearizabilityVerdict.Ok, searches);
    }

    /// <summary>
    /// Converts events to operations whose timestamps are the events' positions. An operation's identifier is the order
    /// of its invocation, and a response's metadata replaces its invocation's.
    /// </summary>
    private static HistoryOperation<TInput, TOutput>[] ToOperations<TInput, TOutput>(IReadOnlyList<HistoryEvent<TInput, TOutput>> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var operations = new List<HistoryOperation<TInput, TOutput>>(history.Count / 2);
        var pending = new Dictionary<int, int>();
        for (var position = 0; position < history.Count; position++)
        {
            var historyEvent = history[position];
            if (historyEvent.Kind == HistoryEventKind.Call)
            {
                if (!pending.TryAdd(historyEvent.Id, operations.Count))
                {
                    throw new ArgumentException(Invariant($"Event {position} invokes operation {historyEvent.Id}, which is already pending."), nameof(history));
                }

                operations.Add(new HistoryOperation<TInput, TOutput>(historyEvent.ClientId, historyEvent.Input, position, default!, position, historyEvent.Metadata));
            }
            else if (pending.Remove(historyEvent.Id, out var index))
            {
                var call = operations[index];
                operations[index] = call with { Output = historyEvent.Output, Return = position, Metadata = historyEvent.Metadata ?? call.Metadata };
            }
            else
            {
                throw new ArgumentException(Invariant($"Event {position} answers operation {historyEvent.Id}, which is not pending."), nameof(history));
            }
        }

        if (pending.Count > 0)
        {
            throw new ArgumentException(
                Invariant($"{pending.Count} operations have no response; record an operation whose outcome is unknown with an output the model accepts in any state."),
                nameof(history));
        }

        return [.. operations];
    }

    /// <summary>
    /// Splits a history into the model's partitions, in the order of each partition's first operation, keeping history
    /// order within each.
    /// </summary>
    private static List<HistoryOperation<TInput, TOutput>[]> Partition<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryOperation<TInput, TOutput>> history)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(history);
        for (var index = 0; index < history.Count; index++)
        {
            if (history[index].Return < history[index].Call)
            {
                throw new ArgumentException(Invariant($"Operation {index} returns at {history[index].Return}, before its call at {history[index].Call}."), nameof(history));
            }
        }

        if (history.Count == 0)
        {
            return [];
        }

        if (model.PartitionComparer is not { } comparer)
        {
            return [[.. history]];
        }

        var indexes = new Dictionary<TInput, int>(comparer);
        var partitions = new List<List<HistoryOperation<TInput, TOutput>>>();
        foreach (var operation in history)
        {
            if (!indexes.TryGetValue(operation.Input, out var index))
            {
                index = partitions.Count;
                indexes.Add(operation.Input, index);
                partitions.Add([]);
            }

            partitions[index].Add(operation);
        }

        return partitions.ConvertAll(static partition => partition.ToArray());
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// One invocation or response, ordered by time with invocations before responses at equal times (closed intervals),
    /// then by operation for a deterministic order.
    /// </summary>
    private readonly record struct Entry(long Time, bool IsReturn, int Operation) : IComparable<Entry>
    {
        public int CompareTo(Entry other)
        {
            var byTime = Time.CompareTo(other.Time);
            if (byTime != 0)
            {
                return byTime;
            }

            var byKind = IsReturn.CompareTo(other.IsReturn);
            return byKind != 0 ? byKind : Operation.CompareTo(other.Operation);
        }
    }

    /// <summary>
    /// A configuration already explored: the set of linearized operations and the state they lead to.
    /// </summary>
    private readonly record struct CacheEntry<TState>(ulong[] Linearized, TState State, int Hash);

    private sealed class CacheComparer<TState>(IEqualityComparer<TState> stateComparer) : IEqualityComparer<CacheEntry<TState>>
    {
        public bool Equals(CacheEntry<TState> x, CacheEntry<TState> y) =>
            x.Hash == y.Hash
            && x.Linearized.AsSpan().SequenceEqual(y.Linearized)
            && stateComparer.Equals(x.State, y.State);

        public int GetHashCode(CacheEntry<TState> obj) => obj.Hash;
    }

    /// <summary>
    /// The search over one partition, as Porcupine's <c>checkSingle</c>, advanced a number of steps at a time.
    /// </summary>
    /// <remarks>
    /// The history is a doubly linked list over arrays: node 0 is the head, node <c>2n + 1</c> the tail, and nodes
    /// <c>1..2n</c> the entries in time order. Linearizing an operation lifts its invocation and response out of the list;
    /// backtracking restores them in reverse order, so the removed nodes' own links stay valid (dancing links).
    /// </remarks>
    private sealed class Search<TState, TInput, TOutput>
        where TInput : notnull
    {
        private readonly LinearizabilityModel<TState, TInput, TOutput> _model;
        private readonly int _tail;
        private readonly int[] _next;
        private readonly int[] _previous;

        /// <summary>For an invocation node, the node of its response; zero for a response.</summary>
        private readonly int[] _response;

        private readonly int[] _operationOf;

        /// <summary>The linearized operations, one bit each.</summary>
        private readonly ulong[] _linearized;

        /// <summary>The nodes of the linearized invocations in linearization order, and the state before each.</summary>
        private readonly int[] _stackNodes;

        private readonly TState[] _stackStates;

        /// <summary>For each operation, the longest linearizable prefix found which includes it, when explaining.</summary>
        private readonly int[]?[]? _longest;

        private HashSet<CacheEntry<TState>>? _cache;

        /// <summary>The Zobrist hash of <see cref="_linearized"/>.</summary>
        private ulong _linearizedHash;

        private int _depth;
        private TState _state;
        private int _node;

        public Search(LinearizabilityModel<TState, TInput, TOutput> model, HistoryOperation<TInput, TOutput>[] operations, bool explain)
        {
            _model = model;
            Operations = operations;
            var entries = new Entry[2 * operations.Length];
            for (var index = 0; index < operations.Length; index++)
            {
                entries[2 * index] = new Entry(operations[index].Call, IsReturn: false, index);
                entries[2 * index + 1] = new Entry(operations[index].Return, IsReturn: true, index);
            }

            Array.Sort(entries);
            _tail = entries.Length + 1;
            _next = new int[_tail + 1];
            _previous = new int[_tail + 1];
            _response = new int[_tail + 1];
            _operationOf = new int[_tail + 1];
            var responseNodes = new int[operations.Length];
            for (var node = 0; node <= _tail; node++)
            {
                _next[node] = node + 1;
                _previous[node] = node - 1;
            }

            for (var index = 0; index < entries.Length; index++)
            {
                _operationOf[index + 1] = entries[index].Operation;
                if (entries[index].IsReturn)
                {
                    responseNodes[entries[index].Operation] = index + 1;
                }
            }

            for (var index = 0; index < entries.Length; index++)
            {
                if (!entries[index].IsReturn)
                {
                    _response[index + 1] = responseNodes[entries[index].Operation];
                }
            }

            _linearized = new ulong[(operations.Length + 63) / 64];
            _stackNodes = new int[operations.Length];
            _stackStates = new TState[operations.Length];
            _longest = explain ? new int[operations.Length][] : null;
            _cache = [with(new CacheComparer<TState>(model.StateComparer))];
            _state = model.Init();
            _node = _next[0];
        }

        public HistoryOperation<TInput, TOutput>[] Operations { get; }

        /// <summary>Gets the partition's verdict, which is <see cref="LinearizabilityVerdict.Unknown"/> until it finishes.</summary>
        public LinearizabilityVerdict Verdict { get; private set; } = LinearizabilityVerdict.Unknown;

        /// <summary>
        /// Takes up to <paramref name="steps"/> steps of the search.
        /// </summary>
        /// <returns>Whether the search has finished with a verdict, which a cancellation makes unknown.</returns>
        public bool Advance(int steps, CancellationToken cancellationToken)
        {
            var cache = _cache ?? throw new InvalidOperationException("The search has finished.");
            for (var step = 0; step < steps; step++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Stop(LinearizabilityVerdict.Unknown);
                    return true;
                }

                if (_response[_node] != 0)
                {
                    var operation = _operationOf[_node];
                    var allowed = _model.Step(_state, Operations[operation].Input, Operations[operation].Output, out var next);

                    // As Porcupine's checkSingle, a cancellation during the step ends the search before it uses the step.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Stop(LinearizabilityVerdict.Unknown);
                        return true;
                    }

                    if (allowed)
                    {
                        Toggle(operation);
                        var probe = new CacheEntry<TState>(_linearized, next, HashCode.Combine(_linearizedHash, _model.StateComparer.GetHashCode(next!)));
                        if (!cache.Contains(probe))
                        {
                            _ = cache.Add(probe with { Linearized = (ulong[])_linearized.Clone() });
                            _stackNodes[_depth] = _node;
                            _stackStates[_depth] = _state;
                            _depth++;
                            _state = next;
                            Lift(_node);
                            if (_next[0] == _tail)
                            {
                                Stop(LinearizabilityVerdict.Ok);
                                return true;
                            }

                            _node = _next[0];
                        }
                        else
                        {
                            Toggle(operation);
                            _node = _next[_node];
                        }
                    }
                    else
                    {
                        _node = _next[_node];
                    }
                }
                else
                {
                    // A response: no pending invocation before it extends the linearization, so backtrack.
                    if (_depth == 0)
                    {
                        Stop(LinearizabilityVerdict.Illegal);
                        return true;
                    }

                    if (_longest is not null)
                    {
                        RecordLongest();
                    }

                    _depth--;
                    _node = _stackNodes[_depth];
                    _state = _stackStates[_depth];
                    _stackStates[_depth] = default!;
                    Toggle(_operationOf[_node]);
                    Unlift(_node);
                    _node = _next[_node];
                }
            }

            return false;
        }

        /// <summary>
        /// Ends the search with a verdict and releases its cache. A complete linearization is the longest prefix of every
        /// operation.
        /// </summary>
        public void Stop(LinearizabilityVerdict verdict)
        {
            Verdict = verdict;
            _cache = null;
            if (verdict == LinearizabilityVerdict.Ok && _longest is not null)
            {
                Array.Fill(_longest, Identifiers());
            }
        }

        /// <summary>
        /// Returns the distinct longest prefixes recorded, longest first and otherwise in the order of their operations.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<int>> PartialLinearizations()
        {
            if (_longest is null)
            {
                return [];
            }

            var distinct = new HashSet<int[]>(ReferenceEqualityComparer.Instance);
            var partials = new List<int[]>();
            foreach (var prefix in _longest)
            {
                if (prefix is not null && distinct.Add(prefix))
                {
                    partials.Add(prefix);
                }
            }

            return [.. partials.OrderByDescending(static prefix => prefix.Length)];
        }

        /// <summary>
        /// SplitMix64's finalizer, a bijection which spreads consecutive operation identifiers over 64 bits.
        /// </summary>
        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value += 0x9E3779B97F4A7C15;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
                return value ^ (value >> 31);
            }
        }

        /// <summary>
        /// Adds or removes an operation from the linearized set, updating the set's Zobrist hash.
        /// </summary>
        private void Toggle(int operation)
        {
            _linearized[operation >> 6] ^= 1UL << (operation & 63);
            _linearizedHash ^= Mix((ulong)operation);
        }

        private int[] Identifiers()
        {
            var identifiers = new int[_depth];
            for (var index = 0; index < _depth; index++)
            {
                identifiers[index] = _operationOf[_stackNodes[index]];
            }

            return identifiers;
        }

        /// <summary>
        /// Records the current prefix for each of its operations whose longest recorded prefix is shorter.
        /// </summary>
        private void RecordLongest()
        {
            int[]? prefix = null;
            for (var index = 0; index < _depth; index++)
            {
                var operation = _operationOf[_stackNodes[index]];
                if (_longest![operation] is not { } longest || _depth > longest.Length)
                {
                    prefix ??= Identifiers();
                    _longest[operation] = prefix;
                }
            }
        }

        private void Lift(int node)
        {
            _next[_previous[node]] = _next[node];
            _previous[_next[node]] = _previous[node];
            var response = _response[node];
            _next[_previous[response]] = _next[response];
            _previous[_next[response]] = _previous[response];
        }

        private void Unlift(int node)
        {
            var response = _response[node];
            _next[_previous[response]] = response;
            _previous[_next[response]] = response;
            _next[_previous[node]] = node;
            _previous[_next[node]] = node;
        }
    }
}

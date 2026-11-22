using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Renders a <see cref="LedgerMapTrace"/> as a Quint module whose <c>traceReplay</c> run applies the trace's actions to
/// <c>test/Durable.Storage/specs/ledgers/LedgerMap.qnt</c> in order and checks every invariant of the model after each, and the model's
/// state against the run's after each ledger-map write and each batch (test/Durable.Storage/docs/spec/verification.md §5.1). <c>quint test</c>
/// replays it: an action the model disables, a broken invariant, or a state the run did not reach fails the trace.
/// </summary>
/// <remarks>
/// <para>
/// The mapping. The model's ledger <c>i</c> is the ledger whose ID is <c>i</c>. A ledger-map write, which a committer's
/// group commit may make of several changes (ledgers.md §2.6), becomes the sequence of the model's conditional steps
/// its value shows against the value before it: each install, in the order of the IDs it opened, which is the order the
/// fold applied them, then each drop, in ID order. An install which opens one ledger over the range of a ledger it closes
/// is <c>takeover</c> (a replace, handoff, replica change, or roll); one which opens the two children of a ledger it
/// closes, the lower first, is <c>split</c>; and one which opens the parent of two siblings it closes is <c>merge</c>. A
/// write's value is then the model's map: the ledgers it lists, their ranges and status, and <c>NextLedgerId</c>, and,
/// where the trace holds writes, each closed ledger's recorded <c>T</c> is the model's tail.
/// </para>
/// <para>
/// A decided batch becomes the model's <c>write</c>, or <c>marker</c> for a batch of no rows, at the slot the model's
/// next slot must equal, with the row's form, defining or referencing, as the writer's dictionary made it. Each
/// ledger's batches are applied in slot order, no later than the first proposal answered durable at or above their slot,
/// and every batch below a ledger's <c>T</c> before the write which closes it: an atomic append lands at some instant
/// between its call and its answer, before the seal. A proposal answered durable at a slot no scan holds, as one at or
/// past its ledger's <c>T</c>, is the model's <c>write</c> where its answer arrived.
/// </para>
/// <para>
/// The abstractions. The bootstrap, the first write which lists a ledger, the root's, is the model's <c>init</c>, and
/// earlier writes list nothing. Node identities, replicas, primaries, quorums, and <c>ChosenAt</c> are not modeled, nor
/// the retirement floor (LG-I11), so a write which only raises the floor is no step. A range of depth <c>d</c> at most
/// <c>KeyBits</c> is the model's <c>(prefix, depth)</c>, its prefix's top <c>d</c> bits; a key is the model's key of its
/// first byte's top <c>KeyBits</c> bits, which identify each one-byte key a simulation writes, the smallest of a range.
/// A row's body, its JSN, and its token are not compared: the model computes each key's JSNs and tokens from its own
/// history. The trace holds no drain: a drop of a ledger with writes is preceded by the model's <c>drain</c> to its
/// <c>T</c>, as the Archive's completion of the ledger precedes it (LSM-I3); a trace without writes drops ledgers whose
/// model tail is zero. Trims and reads are not traced.
/// </para>
/// </remarks>
public static class LedgerMapTraceExport
{
    /// <summary>The model's invariants, which the replay checks after every step.</summary>
    public const string Invariants =
        "OneOwner, IdsDense, HistoryOrdered, AckedInHistory, DropsInLineageOrder, ReferencesResolve, TrimsAtSegmentStarts, TokensReplayPlan, ReadsSeeAcked";

    /// <summary>
    /// Renders <paramref name="trace"/> as the module <paramref name="module"/>, whose import path names the model from a
    /// directory two levels below the repository's root, as <c>artifacts/ledger-map-traces</c> is.
    /// </summary>
    /// <param name="trace">The trace.</param>
    /// <param name="module">The module's name.</param>
    /// <param name="source">The run the trace records, for the module's header.</param>
    /// <param name="keyBits">The model's <c>KeyBits</c>: the deepest range the run may open.</param>
    /// <param name="tracesWrites">Whether the trace holds every decided batch of its ledgers, so their <c>T</c> is the model's tail.</param>
    /// <returns>The module's text.</returns>
    /// <exception cref="InvalidOperationException">A write's value is no sequence of the model's steps.</exception>
    public static string Render(LedgerMapTrace trace, string module, string source, int keyBits, bool tracesWrites)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentOutOfRangeException.ThrowIfLessThan(keyBits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keyBits, 8);
        var translation = new Translation(trace, keyBits, tracesWrites);
        translation.Run();
        var text = new StringBuilder()
            .AppendLine(Invariant($"// A ledger map trace of {source}, generated; trace validation replays it against"))
            .AppendLine("// test/Durable.Storage/specs/ledgers/LedgerMap.qnt with quint test (test/Durable.Storage/docs/spec/verification.md §5.1). The mapping:")
            .AppendLine("// - The model's ledger i is the ledger whose ID is i; the bootstrap, the first value which lists a ledger, is init.")
            .AppendLine("// - A ledger-map write is the model's steps its value shows, a group commit several: takeover, split, and merge in")
            .AppendLine("//   the order of the IDs they open, the order the committer's fold applied them, then drop in ID order. mapIs then")
            .AppendLine(tracesWrites
                ? "//   checks the model's listed ledgers, ranges, status, next, and closed ledgers' tails against the value."
                : "//   checks the model's listed ledgers, ranges, status, and next against the value.")
            .AppendLine("// - A decided batch is write, or marker for one of no rows, at its slot, no later than its durable answer and before")
            .AppendLine("//   its ledger's close; wrote and marked check its slot, key, and row form. A durable answer at a slot no scan holds")
            .AppendLine("//   is a write where it arrived.")
            .AppendLine("// The abstractions:")
            .AppendLine("// - Nodes, replicas, primaries, ChosenAt, and the retirement floor (LG-I11) are not modeled: a floor raise is no step.")
            .AppendLine(Invariant($"// - A range of depth d <= {keyBits} is (its prefix's top d bits, d), and a key is its first byte's top {keyBits} bits."))
            .AppendLine("// - Bodies, versions, JSNs, and tokens are not compared: the model computes JSNs and tokens from its own history.")
            .AppendLine(tracesWrites
                ? "// - The run drains nothing: a drop of a ledger with batches follows drain to its T, the Archive's completion (LSM-I3)."
                : "// - The run writes no batches: its recorded T is no model tail, and a drop needs no drain.")
            .AppendLine("// - Trims and reads are not traced.")
            .AppendLine(Invariant($"module {module} {{"))
            .AppendLine(Invariant($"  import LedgerMap(KeyBits = {keyBits}, MaxLedgers = {translation.MaxLedgers}, MaxJsn = {translation.MaxJsn}, MaxMarkers = {translation.Markers}, Mutation = \"none\").* from \"../../specs/ledgers/LedgerMap\""))
            .AppendLine(Invariant($"  val Invariants = and {{ {Invariants} }}"))
            .AppendLine("  // The map lists exactly these ledgers, as (ID, prefix, depth, open); each (ID, T) has its recorded T as its tail.")
            .AppendLine("  def mapIs(listed: Set[(int, int, int, bool)], tails: Set[(int, int)], nextId: int): bool = and {")
            .AppendLine("    liveIds(s).map(i => (i, s.ledgers.get(i).prefix, s.ledgers.get(i).depth, s.ledgers.get(i).status == Open)) == listed,")
            .AppendLine("    tails.forall(((i, t)) => s.ledgers.get(i).tail == t),")
            .AppendLine("    s.next == nextId,")
            .AppendLine("  }")
            .AppendLine("  // Ledger i's last batch is at the slot, a row of key k which defines or references k in its segment.")
            .AppendLine("  def wrote(i: int, slot: int, k: int, defining: bool): bool =")
            .AppendLine("    and { s.len.get(i) == slot + 1, s.rows.get(i).exists(r => and { r.slot == slot, r.k == k, r.defining == defining }) }")
            .AppendLine("  // Ledger i's last batch is at the slot, a row of key k: a durable answer no scan shows.")
            .AppendLine("  def landed(i: int, slot: int, k: int): bool = and { s.len.get(i) == slot + 1, s.rows.get(i).exists(r => and { r.slot == slot, r.k == k }) }")
            .AppendLine("  // Ledger i's last batch is a marker at the slot.")
            .AppendLine("  def marked(i: int, slot: int): bool = and { s.len.get(i) == slot + 1, s.rows.get(i).forall(r => r.slot != slot) }")
            .AppendLine("  run traceReplay = init.expect(Invariants)");
        foreach (var step in translation.Steps)
        {
            _ = text.Append("    .then(").Append(step.Action).Append(").expect(")
                .Append(step.Expected is { } expected ? Invariant($"and {{ Invariants, {expected} }}") : "Invariants").Append(')');
            if (step.Comment is { } comment)
            {
                _ = text.Append(" // ").Append(comment);
            }

            _ = text.AppendLine();
        }

        return text.AppendLine("}").ToString();
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>One step of the replay: the model's action, what the state after it must satisfy, and a note for mutants.</summary>
    private sealed record Step(string Action, string? Expected = null, string? Comment = null);

    /// <summary>A ledger the model lists: its range, and whether it is open.</summary>
    private sealed class Listed(KeyRange range)
    {
        public KeyRange Range { get; } = range;

        public bool Open { get; set; } = true;
    }

    private sealed class Translation(LedgerMapTrace trace, int keyBits, bool tracesWrites)
    {
        private readonly SortedDictionary<ulong, Listed> _listed = [];
        private readonly Dictionary<ulong, long> _emitted = [];
        private readonly Dictionary<int, int> _writes = [];
        private bool _bootstrapped;

        public List<Step> Steps { get; } = [];

        public ulong MaxLedgers { get; private set; } = 1;

        public int MaxJsn => Math.Max(1, _writes.Values.DefaultIfEmpty(0).Max());

        public int Markers { get; private set; }

        public void Run()
        {
            var previous = LedgerMapValue.Empty;
            foreach (var entry in trace.Events)
            {
                if (entry.Value is { } value)
                {
                    Apply(previous, value);
                    previous = value;
                }
                else if (!_emitted.TryGetValue(entry.Ledger.Value, out var next) || next <= entry.Slot)
                {
                    Answered(entry);
                }
            }

            foreach (var (id, ledger) in _listed)
            {
                if (ledger.Open)
                {
                    Emit(id, long.MaxValue);
                }
            }
        }

        private void Apply(LedgerMapValue previous, LedgerMapValue value)
        {
            var from = previous.NextLedgerId.Value;
            var first = Steps.Count;
            if (!_bootstrapped)
            {
                if (value.Ledgers.IsEmpty)
                {
                    return;
                }

                if (from != 1 || value.Get(new LedgerId(1)) is not { } root || root.Range != KeyRange.Root)
                {
                    throw new InvalidOperationException($"Epoch {value.Epoch}, the first value which lists a ledger, does not list ledger 1 over the root, the model's init.");
                }

                _listed[1] = new Listed(KeyRange.Root);
                _bootstrapped = true;
                from = 2;
            }

            LedgerMetadata[] opened = [.. value.Ledgers.Where(ledger => ledger.Id.Value >= from).OrderBy(static ledger => ledger.Id.Value)];
            if (value.NextLedgerId.Value - from != (ulong)opened.Length)
            {
                throw new InvalidOperationException($"Epoch {value.Epoch} takes IDs {from} to {value.NextLedgerId.Value - 1} but lists {opened.Length} of them.");
            }

            for (var index = 0; index < opened.Length; index++)
            {
                var range = opened[index].Range;
                ulong[] closing = [.. _listed.Where(listed => listed.Value.Open && value.Get(new LedgerId(listed.Key))?.IsOpen != true).Select(static listed => listed.Key)];
                if (closing.FirstOrDefault(id => _listed[id].Range == range) is var replaced and > 0)
                {
                    Close(value, replaced);
                    Install(opened[index], Invariant($"takeover({replaced})"));
                }
                else if (index + 1 < opened.Length
                    && closing.FirstOrDefault(id => _listed[id].Range.Depth < KeyRange.MaxDepth && _listed[id].Range.Children == (range, opened[index + 1].Range)) is var split and > 0)
                {
                    Close(value, split);
                    Install(opened[index], Invariant($"split({split})"), opened[++index]);
                }
                else if (range.Depth < KeyRange.MaxDepth
                    && closing.FirstOrDefault(id => _listed[id].Range == range.Children.Lower) is var lower and > 0
                    && closing.FirstOrDefault(id => _listed[id].Range == range.Children.Upper) is var upper and > 0)
                {
                    Close(value, lower);
                    Close(value, upper);
                    Install(opened[index], Invariant($"merge({lower}, {upper})"));
                }
                else
                {
                    throw new InvalidOperationException($"Epoch {value.Epoch} opens s{opened[index].Id} over {range}, which no takeover, split, or merge of the ledgers it closes opens.");
                }
            }

            if (_listed.FirstOrDefault(listed => listed.Value.Open && value.Get(new LedgerId(listed.Key))?.IsOpen != true) is { Value: not null } orphan)
            {
                throw new InvalidOperationException($"Epoch {value.Epoch} closes s{orphan.Key} without opening a ledger over its range.");
            }

            foreach (var id in _listed.Keys.Where(id => value.Get(new LedgerId(id)) is null).ToArray())
            {
                if (tracesWrites && _emitted.GetValueOrDefault(id) is var tail and > 0)
                {
                    Steps.Add(new Step(Invariant($"drain({id}, {tail})")));
                }

                _ = _listed.Remove(id);
                Steps.Add(new Step(Invariant($"drop({id})")));
            }

            if (Steps.Count > first)
            {
                Steps[^1] = Steps[^1] with { Expected = MapIs(value) };
            }
        }

        /// <summary>Applies every batch of ledger <paramref name="id"/> below its recorded <c>T</c>, which the closing write records.</summary>
        private void Close(LedgerMapValue value, ulong id)
        {
            // A ledger closed and dropped in one write shows no T, so every batch the scans hold is below it.
            if (tracesWrites)
            {
                Emit(id, value.Get(new LedgerId(id))?.Status.Value is LedgerStatus.Closed closed ? closed.T.Value - 1 : long.MaxValue);
            }

            _listed[id].Open = false;
        }

        private void Install(LedgerMetadata first, string action, LedgerMetadata? second = null)
        {
            foreach (var opened in second is { } other ? [first, other] : new[] { first })
            {
                _listed[opened.Id.Value] = new Listed(opened.Range);
                MaxLedgers = Math.Max(MaxLedgers, opened.Id.Value);
                _ = Depth(opened.Range);
            }

            Steps.Add(new Step(action, Comment: Invariant($"installs {string.Join(", ", second is { } upper ? [first.Id.Value, upper.Id.Value] : new[] { first.Id.Value })}")));
        }

        /// <summary>A proposal answered durable: its ledger's batches through its slot, or, at a slot no scan holds, its write.</summary>
        private void Answered(LedgerMapTraceEvent answer)
        {
            var id = answer.Ledger.Value;
            if (trace.Batches.TryGetValue(answer.Ledger, out var batches) && batches.ContainsKey(answer.Slot))
            {
                Emit(id, answer.Slot);
                return;
            }

            var k = Key(answer.Key);
            _writes[k] = _writes.GetValueOrDefault(k) + 1;
            _emitted[id] = Math.Max(_emitted.GetValueOrDefault(id), answer.Slot + 1);
            Steps.Add(new Step(Invariant($"write({id}, {k}, false)"), Invariant($"landed({id}, {answer.Slot}, {k})"), Invariant($"durable at slot {answer.Slot}, which no scan holds")));
        }

        /// <summary>Applies ledger <paramref name="id"/>'s batches not yet applied, through slot <paramref name="last"/>.</summary>
        private void Emit(ulong id, long last)
        {
            if (!trace.Batches.TryGetValue(new LedgerId(id), out var batches))
            {
                return;
            }

            var next = _emitted.GetValueOrDefault(id);
            foreach (var (slot, batch) in batches)
            {
                if (slot < next)
                {
                    continue;
                }

                if (slot > last)
                {
                    break;
                }

                if (slot != next)
                {
                    throw new InvalidOperationException($"s{id}'s batches skip slot {next}.");
                }

                if (batch.Key is { } key)
                {
                    var k = Key(key);
                    _writes[k] = _writes.GetValueOrDefault(k) + 1;
                    var flagged = batch.StartsSegment && slot > 0;
                    Steps.Add(new Step(Invariant($"write({id}, {k}, {Bool(flagged)})"), Invariant($"wrote({id}, {slot}, {k}, {Bool(batch.Defining)})")));
                }
                else
                {
                    Markers++;
                    Steps.Add(new Step(Invariant($"marker({id})"), Invariant($"marked({id}, {slot})")));
                }

                next = slot + 1;
            }

            _emitted[id] = next;
        }

        private string MapIs(LedgerMapValue value)
        {
            var listed = value.Ledgers.OrderBy(static ledger => ledger.Id.Value)
                .Select(ledger => Invariant($"({ledger.Id.Value}, {Prefix(ledger.Range)}, {Depth(ledger.Range)}, {Bool(ledger.IsOpen)})"));
            var tails = tracesWrites
                ? value.Ledgers.OrderBy(static ledger => ledger.Id.Value)
                    .Select(static ledger => ledger.Status.Value is LedgerStatus.Closed closed ? Invariant($"({ledger.Id.Value}, {closed.T.Value})") : null)
                    .OfType<string>()
                : [];
            return Invariant($"mapIs(Set({string.Join(", ", listed)}), Set({string.Join(", ", tails)}), {value.NextLedgerId.Value})");
        }

        private int Depth(KeyRange range) => range.Depth <= keyBits
            ? range.Depth
            : throw new InvalidOperationException($"The range {range} lies deeper than the model's {keyBits} key bits.");

        private int Prefix(KeyRange range) => Depth(range) == 0 ? 0 : range.Prefix[0] >> (8 - range.Depth);

        private int Key(byte key) => key >> (8 - keyBits);

        private static string Bool(bool value) => value ? "true" : "false";
    }
}

using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// One step of a generated history: what kind of change to derive, the choices which pick its ledgers and nodes, how
/// stale a value to derive it from, and whether the batch it joins ends after it.
/// </summary>
internal readonly record struct LedgerMapStep(int Kind, int Choice, int Staleness, bool EndsBatch);

/// <summary>
/// A change derived from <see cref="LedgerMapWorld.History"/>[<paramref name="From"/>]: by a stale controller, whose
/// view predates later retirements, or a deliberate defect.
/// </summary>
internal readonly record struct LedgerMapDerived(LedgerMapChange Change, int From, bool Stale = false, bool Defective = false);

/// <summary>
/// The world the map's generated histories run in (ledgers.md §2.6, §3): the values the register held, and a node
/// registry whose retirements raise the floor. Each change is derived as an executor or controller derives it, from a
/// value the register held, in which its rule holds (ledgers.md §3), or is a deliberate defect; installs name only
/// nodes eligible at their <c>ChosenAt</c>.
/// </summary>
internal sealed class LedgerMapWorld
{
    /// <summary>The nodes of the registry.</summary>
    public const int NodeCount = 6;

    private readonly Dictionary<int, ulong> _retired = [];
    private int _sequencers;


    /// <summary>Gets the values the register held, from the absent register's.</summary>
    public List<LedgerMapValue> History { get; } = [LedgerMapValue.Empty];

    /// <summary>
    /// Gets each floor raise answered applied, as its revision and the index in <see cref="History"/> of the value the
    /// answer names: a composition opens a retired node's gate from that value (OR-I4).
    /// </summary>
    public List<(ulong Revision, int Value)> RaisesApplied { get; } = [];

    /// <summary>Gets the composition's node view revision.</summary>
    public ulong Revision { get; private set; }

    /// <summary>Gets the revision at which each retired node was retired.</summary>
    public IReadOnlyDictionary<int, ulong> Retired => _retired;

    /// <summary>Gets the node a number names.</summary>
    public static NodeId Node(int number) => new(new Guid(number, 0x1d, 0x4e, [0x80, 0, 0, 0, 0, 0, 0, (byte)(number & 0xff)]));

    /// <summary>Returns whether <paramref name="value"/> names <paramref name="node"/> as a replica or a primary.</summary>
    public static bool Names(LedgerMapValue value, NodeId node) =>
        value.Ledgers.Any(ledger => ledger.Replicas.Contains(node) || ledger.Primary.Node == node);

    /// <summary>
    /// Derives a change for <paramref name="step"/> from a value of <see cref="History"/>, and returns it with the index
    /// of that value, or <see langword="null"/> when the value offers no such change.
    /// </summary>
    public LedgerMapDerived? Derive(LedgerMapStep step)
    {
        var from = step.Staleness < 70 ? History.Count - 1 : step.Choice % History.Count;
        var value = History[from];
        LedgerMetadata[] open = [.. value.Ledgers.Where(static ledger => ledger.IsOpen)];
        LedgerMetadata[] closed = [.. value.Ledgers.Where(static ledger => !ledger.IsOpen)];
        var slot = new Slot(step.Choice % 1_000);
        switch (step.Kind)
        {
            case < 30:
                return open.Length == 0 ? Install([], [KeyRange.Root], Revision, step, from) : Transition(open, step, Revision, from);
            case < 45:
                LedgerMetadata[] droppable = [.. closed.Where(ledger => !value.Ledgers.Any(lower => lower.Id < ledger.Id && lower.Range.Overlaps(ledger.Range)))];
                return droppable.Length == 0 ? null : new LedgerMapDerived(new LedgerMapChange.Drop(droppable[step.Choice % droppable.Length].Id), from);
            case < 55:
                var node = 1 + step.Choice % NodeCount;
                if (_retired.ContainsKey(node) || NodeCount - _retired.Count <= 3)
                {
                    return null;
                }

                // A node retired at a new revision; the composition raises the floor once the retirement applies.
                _retired[node] = ++Revision;
                return new LedgerMapDerived(new LedgerMapChange.RaiseFloor(Revision), from);
            case < 60:
                return new LedgerMapDerived(new LedgerMapChange.RaiseFloor((ulong)step.Choice % (Revision + 1)), from);
            case < 70:
                // A stale controller: its view predates later retirements.
                return Revision == 0 || open.Length == 0 ? null : Transition(open, step, (ulong)step.Choice % Revision, from) is { } stale ? stale with { Stale = true } : null;
            case < 75:
                return Defect(value, open, closed, step, slot, from);
            default:
                return open.Length == 0 ? null : Transition(open, step, Revision, from);
        }
    }

    private LedgerMapDerived? Transition(LedgerMetadata[] open, LedgerMapStep step, ulong chosenAt, int from)
    {
        var target = open[step.Choice % open.Length];
        var tail = new Slot(step.Choice % 1_000);
        switch (step.Choice / 7 % 3)
        {
            case 1 when target.Range.Depth < LedgerMapReference.Bits:
                var (lower, upper) = target.Range.Children;
                return Install([new(target.Id, tail)], [lower, upper], chosenAt, step, from);
            case 2 when target.Range.Depth > 0 && Array.FindIndex(open, ledger => ledger.Range == target.Range.Sibling) is >= 0 and var sibling:
                return Install([new(target.Id, tail), new(open[sibling].Id, tail + 1)], [target.Range.Parent], chosenAt, step, from);
            default:
                return Install([new(target.Id, tail)], [target.Range], chosenAt, step, from);
        }
    }

    private LedgerMapDerived? Defect(LedgerMapValue value, LedgerMetadata[] open, LedgerMetadata[] closed, LedgerMapStep step, Slot slot, int from)
    {
        switch (step.Choice % 4)
        {
            case 0 when open.Length > 0 && open[0].Range.Depth < LedgerMapReference.Bits:
                // Closes a ledger and opens only one of its halves: a gap.
                return Install([new(open[0].Id, slot)], [open[0].Range.Children.Lower], Revision, step, from) is { } gap ? gap with { Defective = true } : null;
            case 1 when open.Length > 0:
                return new LedgerMapDerived(new LedgerMapChange.Drop(open[step.Choice % open.Length].Id), from, Defective: true);
            case 2:
                return new LedgerMapDerived(new LedgerMapChange.Drop(new LedgerId(value.NextLedgerId.Value + (ulong)(step.Choice % 3))), from, Defective: true);
            default:
                var blocked = closed.FirstOrDefault(ledger => value.Ledgers.Any(lower => lower.Id < ledger.Id && lower.Range.Overlaps(ledger.Range)));
                return blocked.Location.Value is null ? null : new LedgerMapDerived(new LedgerMapChange.Drop(blocked.Id), from, Defective: true);
        }
    }

    private LedgerMapDerived? Install(ImmutableArray<LedgerClosing> closes, KeyRange[] ranges, ulong chosenAt, LedgerMapStep step, int from)
    {
        // Nodes eligible in the view at ChosenAt: not retired at or before it (OR-I4's premise).
        int[] eligible = [.. Enumerable.Range(1, NodeCount).Where(node => !_retired.TryGetValue(node, out var retired) || retired > chosenAt)];
        var opens = ImmutableArray.CreateBuilder<LedgerOpening>(ranges.Length);
        foreach (var range in ranges)
        {
            var count = eligible.Length >= 3 && step.Choice % 2 == 0 ? 3 : 1;
            var first = step.Choice % eligible.Length;
            ImmutableArray<NodeId> replicas = [.. Enumerable.Range(0, count).Select(offset => Node(eligible[(first + offset) % eligible.Length]))];
            var sequencer = new SequencerInstanceId(new Guid(++_sequencers, 0x5e, 0x9c, [0x80, 0, 0, 0, 0, 0, 0, 1]));
            opens.Add(new LedgerOpening(range, replicas, 1 + step.Choice % 4, new PrimaryClaim(replicas[0], sequencer)));
        }

        return new LedgerMapDerived(new LedgerMapChange.Install(closes, opens.MoveToImmutable(), chosenAt), from);
    }
}

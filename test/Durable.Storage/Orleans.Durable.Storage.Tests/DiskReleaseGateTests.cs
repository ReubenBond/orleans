using System.Collections.Frozen;
using System.Collections.Immutable;
using CsCheck;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// OR-I4, the retirement gate, over ledger maps (orleans.md §5.8, verification.md §3.2): over generated histories of
/// retirements, installs chosen by stale controllers from any registry value since the first, each naming only nodes active
/// in its value (OR-I5), floor raises, and drops, applied by the map's commit rules, the gate agrees after every step with
/// an oracle derived from the history, and the gate checker holds: once the gate answered a node's disk releasable, no
/// later map value names the node. The oracle: a node is releasable exactly when no ledger of the map names it and it is
/// retired in every registry value at or above the map's floor, so no install the rules can still apply names it. Scripted
/// histories show what the checker catches: a gate which ignored the floor opens before a stale install names the node
/// again, and so does an install which names a node retired in its registry value; and a ledger whose primary is no replica
/// keeps its primary's gate closed.
/// </summary>
public sealed class DiskReleaseGateTests : IDisposable
{
    private const int NodeCount = 5;
    private const int Replicas = 3;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The gate agrees with the oracle, and the checker holds, after every step of generated histories; some histories open
    /// a gate and then try an install chosen before the retirement, which the floor supersedes.
    /// </summary>
    [Fact]
    public void AnOpenGateStaysOpenAcrossEveryHistoryTheRulesAdmit()
    {
        var steps = Gen.Select(Gen.Int[0, 11], Gen.Int[1, NodeCount], Gen.Int[0, 3], Gen.Int[0, 100], Gen.Bool)
            .Select(static (kind, node, back, pick, split) => new Step(kind switch { 0 => Kind.Retire, < 7 => Kind.Install, < 9 => Kind.Raise, _ => Kind.Drop }, node, back, pick, split))
            .Array[1, 60];
        var coverage = new Coverage();
        steps.Sample(
            history => _leaks.InScope(() =>
            {
                var run = new History(chooseActive: true);
                foreach (var step in history)
                {
                    run.Apply(step);
                    foreach (var node in run.Registry.Nodes.Keys)
                    {
                        run.AssertAgreesWithTheOracle(node);
                    }

                    Assert.Null(run.Checker.Check(run.Registry, run.Map));
                }

                coverage.Add(run);
            }),
            iter: 2000,
            seed: Environment.GetEnvironmentVariable("CLOG_TEST_SEED") is { Length: > 0 } seed ? seed : null);

        Assert.True(coverage.Opened > 0, "No history opened a retired node's gate.");
        Assert.True(coverage.StaleAfterOpening > 0, "No history tried an install chosen before a retirement after its gate opened.");
    }

    /// <summary>
    /// A stale controller's install, chosen while node 3 was active and naming it, reaches the map after node 3's gate
    /// opened: the map's floor, which the gate waited for, supersedes it, and node 3 stays out of every ledger. A gate which
    /// ignored the floor opens earlier, and the same install, applied under the lower floor, names node 3 again, which the
    /// checker reports.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGateWhichIgnoresTheFloorIsCaughtByAStaleInstall(bool ignoreTheFloor)
    {
        var run = new History(chooseActive: true, ignoreTheFloor ? static (node, registry, map) => NodeStanding.Of(node, registry, map).Value is NodeStanding.Retired { Ledgers.IsEmpty: true } : null);
        run.Install(chosenAt: 1, [Node(1), Node(2), Node(3)]);
        var root = run.Map.Ledgers.Single();
        run.Retire(Node(3));
        run.Install(chosenAt: 2, [Node(1), Node(2), Node(4)], closes: root.Id);
        run.Drop(root.Id);
        if (!ignoreTheFloor)
        {
            Assert.Null(run.Checker.Check(run.Registry, run.Map));
            Assert.False(run.Checker.Opened(Node(3)));
            run.RaiseFloor();
        }

        Assert.Null(run.Checker.Check(run.Registry, run.Map));
        Assert.True(run.Checker.Opened(Node(3)));

        var open = run.Map.Ledgers.Single(static ledger => ledger.IsOpen);
        var verdict = run.Install(chosenAt: 1, [Node(1), Node(2), Node(3)], closes: open.Id);

        if (ignoreTheFloor)
        {
            _ = Assert.IsType<LedgerMapFoldVerdict.Applies>(verdict.Value);
            Assert.Contains("OR-I4", run.Checker.Check(run.Registry, run.Map), StringComparison.Ordinal);
        }
        else
        {
            _ = Assert.IsType<LedgerMapFoldVerdict.RuleFails>(verdict.Value);
            Assert.Null(run.Checker.Check(run.Registry, run.Map));
        }
    }

    /// <summary>
    /// The gate relies on OR-I5: an install chosen after the retirement, at or above the floor, which nonetheless names the
    /// retired node, as a controller blind to the retirement would choose, applies and names it again after its gate
    /// opened, which the checker reports.
    /// </summary>
    [Fact]
    public void AnInstallWhichNamesANodeRetiredInItsRegistryValueIsCaught()
    {
        var run = new History(chooseActive: false);
        run.Install(chosenAt: 1, [Node(1), Node(2), Node(3)]);
        var root = run.Map.Ledgers.Single();
        run.Retire(Node(3));
        run.RaiseFloor();
        run.Install(chosenAt: 2, [Node(1), Node(2), Node(4)], closes: root.Id);
        run.Drop(root.Id);
        Assert.Null(run.Checker.Check(run.Registry, run.Map));
        Assert.True(run.Checker.Opened(Node(3)));

        var open = run.Map.Ledgers.Single(static ledger => ledger.IsOpen);
        _ = Assert.IsType<LedgerMapFoldVerdict.Applies>(run.Install(chosenAt: 2, [Node(1), Node(2), Node(3)], closes: open.Id).Value);

        Assert.Contains("OR-I4", run.Checker.Check(run.Registry, run.Map), StringComparison.Ordinal);
    }

    /// <summary>
    /// A ledger names its primary as well as its replicas: a retired node which is the primary of a ledger but none of its
    /// replicas, as the map's rules allow, keeps its gate closed until that ledger drops, as the oracle says.
    /// </summary>
    [Fact]
    public void APrimaryOutsideItsReplicasKeepsItsGateClosed()
    {
        var run = new History(chooseActive: true);
        run.Install(chosenAt: 1, [Node(1), Node(2), Node(4)], primary: Node(3));
        var root = run.Map.Ledgers.Single();
        run.Retire(Node(3));
        run.RaiseFloor();

        var standing = Assert.IsType<NodeStanding.Retired>(NodeStanding.Of(Node(3), run.Registry, run.Map).Value);
        Assert.True(standing.FloorCovers);
        Assert.Equal([root.Id], standing.Ledgers);
        Assert.False(standing.IsReleasable);
        run.AssertAgreesWithTheOracle(Node(3));

        run.Install(chosenAt: 2, [Node(1), Node(2), Node(4)], closes: root.Id);
        run.Drop(root.Id);
        Assert.True(NodeStanding.Of(Node(3), run.Registry, run.Map).IsReleasable);
        run.AssertAgreesWithTheOracle(Node(3));
    }

    private enum Kind
    {
        Retire,
        Install,
        Raise,
        Drop,
    }

    /// <summary>One generated step.</summary>
    private sealed record Step(Kind Kind, int Node, int Back, int Pick, bool Split);

    /// <summary>
    /// The gate checker of verification.md §3.2: it records the epoch of each map value from which the gate answered a node
    /// releasable, and reports a later value which names the node.
    /// </summary>
    private sealed class GateChecker(Func<NodeId, RegisteredNodes, LedgerMapValue, bool> releasable)
    {
        private readonly Dictionary<NodeId, ulong> _openedAt = [];

        public bool Opened(NodeId node) => _openedAt.ContainsKey(node);

        public string? Check(RegisteredNodes registry, LedgerMapValue map)
        {
            foreach (var (node, epoch) in _openedAt)
            {
                if (map.Ledgers.Where(ledger => ledger.Replicas.Contains(node) || ledger.Primary.Node == node).Select(static ledger => (LedgerId?)ledger.Id).FirstOrDefault() is { } naming)
                {
                    return $"OR-I4: the gate answered node {node} releasable at epoch {epoch}, and the map at epoch {map.Epoch} names it in ledger {naming}.";
                }
            }

            foreach (var node in registry.Nodes.Keys)
            {
                if (releasable(node, registry, map))
                {
                    _ = _openedAt.TryAdd(node, map.Epoch);
                }
            }

            return null;
        }
    }

    /// <summary>A history of the registry and the ledger map, each change applied by the map's commit rules.</summary>
    private sealed class History
    {
        private readonly List<RegisteredNodes> _registries;
        private readonly bool _chooseActive;

        public History(bool chooseActive, Func<NodeId, RegisteredNodes, LedgerMapValue, bool>? releasable = null)
        {
            _chooseActive = chooseActive;
            var nodes = Enumerable.Range(1, NodeCount).ToFrozenDictionary(static node => Node(node), static _ => new RegisteredNode(RetiredRevision: null, FailureDomain: null, Generation: 1, Ordinal: null));
            _registries = [RegisteredNodes.Empty, new RegisteredNodes(1, nodes)];
            Checker = new GateChecker(releasable ?? (static (node, registry, map) => NodeStanding.Of(node, registry, map).IsReleasable));
        }

        public GateChecker Checker { get; }

        public LedgerMapValue Map { get; private set; } = LedgerMapValue.Empty;

        public RegisteredNodes Registry => _registries[^1];

        public bool StaleAfterOpening { get; private set; }

        /// <summary>
        /// Asserts that the gate's answer for <paramref name="node"/> agrees with the oracle the history decides: the floor
        /// covers the retirement exactly when every registry value at or above the map's floor holds the node retired, and
        /// the node is releasable exactly when, besides, no ledger of the map names it.
        /// </summary>
        public void AssertAgreesWithTheOracle(NodeId node)
        {
            var floor = Map.RetirementFloor;
            var retiredFromTheFloor = _registries.Where(registry => registry.Revision >= floor).All(registry => registry.Find(node) is { IsRetired: true });
            LedgerId[] naming = [.. Map.Ledgers.Where(ledger => ledger.Primary.Node == node || ledger.Replicas.Contains(node)).Select(static ledger => ledger.Id)];
            switch (NodeStanding.Of(node, Registry, Map).Value)
            {
                case NodeStanding.Retired retired:
                    Assert.Equal((retiredFromTheFloor, retiredFromTheFloor && naming.Length == 0), (retired.FloorCovers, retired.IsReleasable));
                    Assert.Equal(naming, retired.Ledgers);
                    Assert.Equal((Registry.Find(node)!.RetiredRevision, Map.Epoch, floor), (retired.RetiredRevision, retired.Epoch, retired.Floor));
                    break;
                case NodeStanding.Active:
                    Assert.False(retiredFromTheFloor || Registry.Find(node)!.IsRetired);
                    break;
                case NodeStanding.NotRegistered:
                case null:
                default:
                    Assert.Fail($"The registry holds node {node}.");
                    break;
            }
        }

        public void Apply(Step step)
        {
            switch (step.Kind)
            {
                case Kind.Retire when Registry.Nodes.Count(static entry => !entry.Value.IsRetired) > Replicas:
                    Retire(Node(step.Node));
                    break;
                case Kind.Install:
                    InstallStep(step);
                    break;
                case Kind.Raise:
                    RaiseFloor();
                    break;
                case Kind.Drop when Map.Ledgers.Where(ledger => !ledger.IsOpen && LedgerMapRules.Decide(Map, new LedgerMapChange.Drop(ledger.Id), int.MaxValue).Value is LedgerMapFoldVerdict.Applies)
                    .Select(static ledger => (LedgerId?)ledger.Id).FirstOrDefault() is { } closed:
                    Drop(closed);
                    break;
                case Kind.Retire:
                case Kind.Drop:
                default:
                    break;
            }
        }

        /// <summary>Retires <paramref name="node"/> at the registry's next revision.</summary>
        public void Retire(NodeId node)
        {
            var revision = Registry.Revision + 1;
            _registries.Add(new RegisteredNodes(revision, Registry.Nodes.ToFrozenDictionary(static entry => entry.Key, entry => entry.Key == node && !entry.Value.IsRetired ? entry.Value with { RetiredRevision = revision } : entry.Value)));
        }

        /// <summary>Raises the map's floor to the registry's newest retirement, as the controller does.</summary>
        public void RaiseFloor() => _ = Commit(new LedgerMapChange.RaiseFloor(Registry.LatestRetiredRevision));

        public void Drop(LedgerId ledger) => _ = Commit(new LedgerMapChange.Drop(ledger));

        /// <summary>
        /// Installs, chosen at <paramref name="chosenAt"/> on <paramref name="replicas"/>, a bootstrap, or a replace or a
        /// split of <paramref name="closes"/>.
        /// </summary>
        public LedgerMapFoldVerdict Install(ulong chosenAt, NodeId[] replicas, LedgerId? closes = null, bool split = false, NodeId? primary = null)
        {
            if (closes is not { } closed)
            {
                return Commit(new LedgerMapChange.Install([], [Opening(KeyRange.Root, replicas, primary)], chosenAt));
            }

            var range = Map.Get(closed)!.Value.Range;
            ImmutableArray<LedgerOpening> opens = split ? [Opening(range.Children.Lower, replicas, primary), Opening(range.Children.Upper, replicas, primary)] : [Opening(range, replicas, primary)];
            return Commit(new LedgerMapChange.Install([new LedgerClosing(closed, Slot.Zero)], opens, chosenAt));
        }

        private static LedgerOpening Opening(KeyRange range, NodeId[] replicas, NodeId? primary) =>
            new(range, [.. replicas], 4, new PrimaryClaim(primary ?? replicas[0], SequencerInstanceId.New()));

        /// <summary>
        /// Installs a generated step's transition: chosen at a registry value <c>Back</c> revisions before the newest, on
        /// replicas active in it (or, for a controller blind to retirements, in the first), a bootstrap, or a replace or a
        /// split of an open ledger.
        /// </summary>
        private void InstallStep(Step step)
        {
            var chosenAt = (ulong)Math.Max(1, (int)Registry.Revision - step.Back);
            var candidates = (_chooseActive ? _registries[(int)chosenAt] : _registries[1]).Nodes.Where(static entry => !entry.Value.IsRetired).Select(static entry => entry.Key).Order(NodeOrder.Instance).ToArray();
            if (candidates.Length < Replicas)
            {
                return;
            }

            NodeId[] replicas = [.. Enumerable.Range(0, Replicas).Select(index => candidates[(step.Pick + index) % candidates.Length])];
            var open = Map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
            if (open.Length == 0)
            {
                _ = Install(chosenAt, replicas);
                return;
            }

            var target = open[step.Pick % open.Length];
            StaleAfterOpening |= replicas.Any(node => Checker.Opened(node) && chosenAt < Registry.Find(node)!.RetiredRevision);
            _ = Install(chosenAt, replicas, target.Id, split: step.Split && target.Range.Depth < 2);
        }

        private LedgerMapFoldVerdict Commit(LedgerMapChange change)
        {
            var (result, verdicts, applied) = LedgerMapRules.Fold(Map, [change], int.MaxValue);
            if (!ReferenceEquals(result, Map))
            {
                // The committer writes each value which applies a change at the next epoch.
                var written = new LedgerMapValue(Map.Epoch + 1, result.NextLedgerId, result.RetirementFloor, result.Ledgers);
                Assert.Null(LedgerMapRules.CheckInvariants(Map, written, applied));
                Map = written;
            }

            return verdicts[0];
        }
    }

    /// <summary>The histories' coverage across every case.</summary>
    private sealed class Coverage
    {
        private int _opened;
        private int _staleAfterOpening;

        public int Opened => Volatile.Read(ref _opened);

        public int StaleAfterOpening => Volatile.Read(ref _staleAfterOpening);

        public void Add(History run)
        {
            if (Enumerable.Range(1, NodeCount).Any(node => run.Checker.Opened(Node(node))))
            {
                _ = Interlocked.Increment(ref _opened);
            }

            if (run.StaleAfterOpening)
            {
                _ = Interlocked.Increment(ref _staleAfterOpening);
            }
        }
    }
}

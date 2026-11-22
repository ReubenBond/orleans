using System.Collections.Frozen;
using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures a pass of the controller's decisions (orleans.md §5.6) over a balanced cluster of 4,096 open ledgers on 100
/// live active nodes in <see cref="Domains"/> failure domains, node <c>n</c> in domain <c>n mod Domains</c>, every ledger
/// warm, over a manifest of 49,152 sections, twelve in each of 4,096 partitions, none overfull, so the pass sends nothing
/// and assigns no compaction: its time and allocations when nothing changed since the previous pass, and after a new
/// registry value, which makes the pass scan the map and the manifest's partitions. Ledger <c>i</c>'s replicas are the
/// three nodes from <c>3i mod 100</c> around the ring of nodes, which spreads the primaries and the replicas each to
/// within one; with three domains, the triples which wrap the ring share a domain, and the setup applies the transitions
/// passes send, dropping each closed ledger, until one sends none, so evening finds nodes above its bound with no
/// domain-safe roll and the pass scans their ledgers. The baseline is the pass after a change, which scans whatever the pass skips. The registry also holds
/// <see cref="RetiredNodes"/> retired identities, each retired at a revision of its own, with the map's floor covering
/// them all, as a cluster's lifetime of retirements leaves it (orleans.md §8.1).
/// </summary>
[MemoryDiagnoser]
public class ClusterControllerPassBenchmarks : IAsyncDisposable
{
    private const int Nodes = 100;
    private const int Depth = 12;
    private const int SectionsPerPartition = 12;

    private readonly ControlOptions _options = new() { LedgersPerNode = 40 };
    private NodeId[] _nodes = null!;
    private FrozenDictionary<NodeId, RegisteredNode> _registry = null!;
    private FrozenDictionary<NodeId, NodeLiveness> _liveness = null!;
    private LedgerMapView _view = null!;
    private LedgerMapCommitter _committer = null!;
    private ManifestCommitter _manifestCommitter = null!;
    private ClusterController _controller = null!;
    private ulong _revision;

    /// <summary>Gets or sets the retired identities the registry holds beside the live active nodes.</summary>
    [Params(0, 800)]
    public int RetiredNodes { get; set; }

    /// <summary>Gets or sets the failure domains of the nodes: ten of ten nodes each, or three of 34, 33, and 33.</summary>
    [Params(10, 3)]
    public int Domains { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _nodes = [.. Enumerable.Range(0, Nodes).Select(static node => new NodeId(new Guid(node + 1, 0, 0, new byte[8])))];
        var retired = Enumerable.Range(0, RetiredNodes).Select(static node => new NodeId(new Guid(node + 1, 1, 0, new byte[8])));
        _registry = _nodes.Select((node, index) => (node, new RegisteredNode(RetiredRevision: null, $"domain-{index % Domains}", Generation: 1, Ordinal: null)))
            .Concat(retired.Select((node, index) => (node, new RegisteredNode((ulong)index + 1, $"domain-{index % Domains}", Generation: 1, Ordinal: null))))
            .ToFrozenDictionary(static entry => entry.node, static entry => entry.Item2);
        _revision = (ulong)RetiredNodes;
        _liveness = _nodes.ToFrozenDictionary(static node => node, static _ => (NodeLiveness)NodeLiveness.Live.Instance);

        var ranges = Ranges(KeyRange.Root, Depth).ToArray();
        ImmutableArray<LedgerMetadata> ledgers = [.. ranges.Select((range, index) => new LedgerMetadata(
            new LedgerId((ulong)index + 1),
            range,
            [_nodes[3 * index % Nodes], _nodes[(3 * index + 1) % Nodes], _nodes[(3 * index + 2) % Nodes]],
            _options.SequencerWindow,
            new PrimaryClaim(_nodes[3 * index % Nodes], SequencerInstanceId.New()),
            LedgerStatus.Open.Instance))];
        var register = new InMemoryConditionalRegister();
        using (var encoded = new LedgerMapValue(1, new LedgerId((ulong)ranges.Length + 1), (ulong)RetiredNodes, ledgers).Encode())
        {
            _ = await register.WriteAsync(null, encoded.AsReadOnlySequence()).ConfigureAwait(false);
        }

        var objects = new InMemoryObjectStore();
        using (var encoded = ManifestFormat.Encode(Manifest(ranges)))
        {
            _ = await objects.ReplaceAsync(ObjectName.Manifest, null, encoded.Content, encoded.Crc32C).ConfigureAwait(false);
        }

        _view = await LedgerMapView.OpenAsync(register).ConfigureAwait(false);
        var manifest = new ManifestView(objects);
        _committer = new LedgerMapCommitter(register, int.MaxValue, _view);
        _manifestCommitter = new ManifestCommitter(objects, manifest, TimeProvider.System, TimeSpan.Zero);
        _controller = new ClusterController(_options, _view, _committer, new ClusterArchive.Lsm(manifest, _manifestCommitter, new ArchiveOptions()), TimeProvider.System, NullLogger.Instance);
        await _controller.ReadManifestAsync().ConfigureAwait(false);
        Observe();
        foreach (var node in _nodes)
        {
            var load = ledgers.Where(ledger => ledger.Primary.Node == node).ToImmutableDictionary(static ledger => ledger.Id, _ => _options.SplitBytesPerSecond / 2);
            _ = _controller.Report(NodeReport.Idle(node, generation: 1, sequence: 1) with { ProposedBytesPerSecond = load });
        }

        await SettleAsync().ConfigureAwait(false);
    }

    [Benchmark(Baseline = true)]
    public int PassAfterARegistryChange()
    {
        Observe();
        return _controller.RunPass().Transitions.Length;
    }

    [Benchmark]
    public int PassWithNothingChanged() => _controller.RunPass().Transitions.Length;

    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_committer is not null)
        {
            await _committer.DisposeAsync().ConfigureAwait(false);
            _committer = null!;
        }

        if (_manifestCommitter is not null)
        {
            await _manifestCommitter.DisposeAsync().ConfigureAwait(false);
            _manifestCommitter = null!;
        }
    }

    /// <summary>
    /// Applies every transition each pass sends, as their executors would, and drops each closed ledger, until a pass sends
    /// none.
    /// </summary>
    private async Task SettleAsync()
    {
        for (var pass = 0; pass < 100; pass++)
        {
            var sent = _controller.RunPass();
            if (!sent.Compactions.IsEmpty)
            {
                throw new InvalidOperationException("The pass assigned compactions over a manifest with no overfull partition.");
            }

            if (sent.Transitions.IsEmpty)
            {
                return;
            }

            foreach (var transition in sent.Transitions)
            {
                if (transition.Transition.Value is not Transition.Replace { Ledger: var ledger } || _view.Current.Get(ledger) is not { } replaced)
                {
                    throw new InvalidOperationException($"A pass sent {transition}, which replaces no ledger the map holds.");
                }
                var choice = transition.Transition.Choice;
                var opening = new LedgerOpening(replaced.Range, choice.Replicas, choice.K, new PrimaryClaim(choice.Executor, SequencerInstanceId.New()));
                var outcome = await _controller.CommitLedgerMapAsync(new LedgerMapChange.Install([new LedgerClosing(replaced.Id, Slot.Zero)], [opening], choice.ChosenAt)).ConfigureAwait(false);
                _ = _controller.TransitionCompleted(transition, TransitionReply.Of(outcome));

                // The closed ledger drains and drops, as in a settled cluster, so no pass reads its drain.
                if ((await _controller.CommitLedgerMapAsync(new LedgerMapChange.Drop(replaced.Id)).ConfigureAwait(false)).Value is not LedgerMapCommitOutcome.Applied)
                {
                    throw new InvalidOperationException($"The drop of s{replaced.Id} did not apply.");
                }
            }
        }

        throw new InvalidOperationException("The cluster does not settle: passes sent transitions for 100 passes.");
    }

    /// <summary>Returns a manifest of <see cref="SectionsPerPartition"/> sections in each of <paramref name="partitions"/>, each its own object.</summary>
    private static ManifestValue Manifest(KeyRange[] partitions)
    {
        const long length = 64L * 1024 * 1024;
        var tag = new Tag(new LedgerId(1), new Slot(1), 0);
        var sections = partitions.SelectMany((partition, index) => Enumerable.Range(0, SectionsPerPartition).Select(section =>
            new SectionRef(new ObjectName($"d/1/0/p{index}s{section}"), 0, length, partition, tag, tag, new ObjectRange(length, 1))));
        return new ManifestValue(1, new LedgerId(1), [], sections);
    }

    private static IEnumerable<KeyRange> Ranges(KeyRange range, int depth) =>
        depth == 0 ? [range] : Ranges(range.Children.Lower, depth - 1).Concat(Ranges(range.Children.Upper, depth - 1));

    private void Observe() => _controller.Observe(new RegisteredNodes(++_revision, _registry), _liveness);
}

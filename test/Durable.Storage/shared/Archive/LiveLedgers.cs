using System.Buffers;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// One node's process over in-memory replicas and a ledger map register (ledgers.md §2): its map view, reader, writer
/// registry, and transition executor, with the map's committer, so a test opens, writes, closes, and drops live ledgers
/// through the Ledgers layer's own paths. Every ledger has this node as its primary and first replica, and, when the
/// process starts with three replicas, two more nodes, so a batch is decided once two of them hold it.
/// </summary>
internal sealed class LiveLedgers : IAsyncDisposable
{
    private const int Window = 4;

    private readonly InMemoryLedgerReplicaTransport _transport;
    private readonly IConditionalRegister _register;
    private readonly LedgerWriterOptions _options;
    private readonly LedgerWriterRegistry _registry;
    private readonly TransitionExecutor _executor;
    private bool _restarted;

    private LiveLedgers(InMemoryLedgerReplicaTransport transport, IConditionalRegister register, LedgerMapView map, LedgerReader reader, LedgerMapCommitter committer, TimeProvider timeProvider, LedgerWriterOptions options, int replicas)
    {
        Replicas = [.. new[] { Node, new NodeId(new Guid("00000000-0000-0000-0000-00000000000b")), new NodeId(new Guid("00000000-0000-0000-0000-00000000000c")) }.Take(replicas)];
        _transport = transport;
        _register = register;
        _options = options;
        TimeProvider = timeProvider;
        Map = map;
        Reader = reader;
        Committer = committer;
        _registry = new LedgerWriterRegistry(reader, 1 << 25, options, timeProvider, NullLoggerFactory.Instance, new Random(17));
        _executor = new TransitionExecutor(_registry, NullLogger<TransitionExecutor>.Instance);
    }

    /// <summary>Gets the node, every ledger's replica and primary.</summary>
    public static NodeId Node { get; } = new(new Guid("00000000-0000-0000-0000-00000000000a"));

    /// <summary>Gets the nodes every ledger lists, this node first.</summary>
    public ImmutableArray<NodeId> Replicas { get; }

    /// <summary>Gets the process's map view.</summary>
    public LedgerMapView Map { get; }

    /// <summary>Gets the process's reader.</summary>
    public LedgerReader Reader { get; }

    /// <summary>Gets the map's committer.</summary>
    public LedgerMapCommitter Committer { get; }

    /// <summary>Gets the process's writer registry, which serves every open ledger's primary.</summary>
    public LedgerWriterRegistry Registry => _registry;

    /// <summary>Gets the transport every process reads through, whose replicas answer backpressure as a test says.</summary>
    public BackpressureTransport Transport { get; private init; } = null!;

    /// <summary>
    /// Gets the versions the process's rows store, each following its key's previous row's across segments and ledgers,
    /// with the empty token, the LSM's (JS-I10, HI-I4).
    /// </summary>
    public ArchiveChains Chains { get; private init; } = ArchiveChains.WithEmptyTokens();

    /// <summary>Gets the clock of every process's backoff.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Starts the process over <paramref name="register"/>, whose writers follow <paramref name="options"/>, or the
    /// defaults, and draw their segment ages from a seeded source, with one replica a ledger, or three, whose rows store
    /// <paramref name="chains"/>'s tokens, or the empty token, the LSM's.
    /// </summary>
    public static async Task<LiveLedgers> StartAsync(IConditionalRegister register, TimeProvider timeProvider, CancellationToken cancellationToken, LedgerWriterOptions? options = null, int replicas = 1, ArchiveChains? chains = null)
    {
        if (replicas is not (1 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(replicas), replicas, "Ledgers here have one replica or three.");
        }

        var transport = new InMemoryLedgerReplicaTransport();
        var shared = new BackpressureTransport(transport);
        var map = await LedgerMapView.OpenAsync(register, cancellationToken);
        return new LiveLedgers(transport, register, map, new LedgerReader(map, shared, timeProvider, Node), new LedgerMapCommitter(register, int.MaxValue), timeProvider, options ?? LedgerWriterOptions.Default, replicas)
        {
            Transport = shared,
            Chains = chains ?? ArchiveChains.WithEmptyTokens(),
        };
    }

    /// <summary>Submits a change to the map's committer, as the composition does.</summary>
    public Task<LedgerMapCommitOutcome> CommitAsync(LedgerMapChange change, CancellationToken cancellationToken) =>
        Committer.CommitAsync(change, cancellationToken);

    /// <summary>Opens the first ledger, over the whole keyspace.</summary>
    public Task BootstrapAsync(CancellationToken cancellationToken) => ExecuteAsync(new Transition.Bootstrap(Choice()), cancellationToken);

    /// <summary>Closes <paramref name="ledger"/> at its recovered tail and opens its successor over its range.</summary>
    public Task ReplaceAsync(LedgerId ledger, CancellationToken cancellationToken) => ExecuteAsync(new Transition.Replace(ledger, Choice()), cancellationToken);

    /// <summary>Closes <paramref name="ledger"/> at its recovered tail and opens its two children.</summary>
    public Task SplitAsync(LedgerId ledger, CancellationToken cancellationToken) => ExecuteAsync(new Transition.Split(ledger, Choice()), cancellationToken);

    /// <summary>Closes the open siblings <paramref name="left"/> and <paramref name="right"/> and opens their parent.</summary>
    public Task MergeAsync(LedgerId left, LedgerId right, CancellationToken cancellationToken) => ExecuteAsync(new Transition.Merge(left, right, Choice()), cancellationToken);

    /// <summary>
    /// Hands the open ledger <paramref name="ledger"/> to <paramref name="primary"/>, as another node's takeover does: its
    /// recovery in this process stops its writer's lifetime and seals it, and an install closes it at the recovered end
    /// and opens its successor over its range under a claim of <paramref name="primary"/>, its one replica; the view then
    /// reads the map. Returns the successor.
    /// </summary>
    public async Task<LedgerId> HandOffAsync(LedgerId ledger, NodeId primary, CancellationToken cancellationToken)
    {
        var listed = Ledger(ledger);
        Slot end;
        using (var lease = Reader.LeaseLedger(listed))
        {
            end = (await lease.Ledger.RecoverAsync(cancellationToken)).Value is RecoveryOutcome.Recovered recovered
                ? recovered.End
                : throw new InvalidOperationException($"Ledger {ledger}'s recovery found it dropped.");
        }

        var successor = Map.Current.NextLedgerId;
        var install = new LedgerMapChange.Install(
            [new LedgerClosing(ledger, end)],
            [new LedgerOpening(listed.Range, [primary], 1, new PrimaryClaim(primary, SequencerInstanceId.New()))],
            0);
        var outcome = await CommitAsync(install, cancellationToken);
        if (outcome.Value is not LedgerMapCommitOutcome.Applied)
        {
            throw new InvalidOperationException($"The handoff of ledger {ledger} answered {outcome.Value}.");
        }

        _ = await Map.ReadAsync(cancellationToken);
        return successor;
    }

    /// <summary>Returns the ledger the map view lists under <paramref name="ledger"/>.</summary>
    public LedgerMetadata Ledger(LedgerId ledger) =>
        Map.Current.Get(ledger) ?? throw new InvalidOperationException($"The map view does not list ledger {ledger}.");

    /// <summary>Returns the open ledger whose range contains <paramref name="key"/>.</summary>
    public LedgerMetadata OpenContaining(byte[] key) =>
        Map.Current.Find(key)?.Open ?? throw new InvalidOperationException("The map view lists no open ledger.");

    /// <summary>
    /// Proposes one batch of <paramref name="rows"/> through the primary of the open ledger <paramref name="ledger"/>.
    /// </summary>
    /// <returns>The rows' tags, once the batch is durable.</returns>
    public async Task<ImmutableArray<Tag>> ProposeAsync(LedgerId ledger, IReadOnlyList<LiveRow> rows, CancellationToken cancellationToken)
    {
        var writer = _registry.TryGet(ledger) ?? throw new InvalidOperationException($"The process serves no writer of ledger {ledger}.");
        long length = LedgerBatch.MaxHeaderLength(rows.Count);
        foreach (var row in rows)
        {
            length += LedgerBatch.MaxRowLength(row.Key.Length, 0, 0, row.Payload.Length);
        }

        ProposalOutcome outcome;
        var pending = Chains.Propose();
        using (var batch = await writer.CreateBatchAsync(rows.Count, (int)length, cancellationToken))
        {
            foreach (var row in rows)
            {
                _ = pending.AddRow(batch, row.Key, row.Kind, new ReadOnlySequence<byte>(row.Payload));
            }

            outcome = await writer.ProposeAsync(batch, cancellationToken);
        }

        pending.Settle(outcome.TryGetValue(out ProposalOutcome.Durable _));

        if (!outcome.TryGetValue(out ProposalOutcome.Durable durable))
        {
            throw new InvalidOperationException($"A proposal to ledger {ledger} answered {outcome}.");
        }

        return [.. Enumerable.Range(0, rows.Count).Select(index => new Tag(ledger, durable.Slot, index))];
    }

    /// <summary>Returns the contiguous tail the primary of the open ledger <paramref name="ledger"/> confirms.</summary>
    public async Task<Slot> ConfirmOpenAsync(LedgerId ledger, CancellationToken cancellationToken)
    {
        var writer = _registry.TryGet(ledger) ?? throw new InvalidOperationException($"The process serves no writer of ledger {ledger}.");
        return (await writer.ConfirmOpenAsync(cancellationToken)).Value is OpenCheckOutcome.Open open
            ? open.ContiguousTail
            : throw new InvalidOperationException($"Ledger {ledger}'s open check found it sealed.");
    }

    /// <summary>
    /// Proposes one batch of <paramref name="row"/> through the primary of the open ledger <paramref name="ledger"/>, its
    /// defining row storing <paramref name="token"/> and the empty version base, past the chains, as a writer whose store
    /// planned another token would.
    /// </summary>
    public async Task ProposeWithTokenAsync(LedgerId ledger, LiveRow row, RowToken token, CancellationToken cancellationToken)
    {
        var writer = _registry.TryGet(ledger) ?? throw new InvalidOperationException($"The process serves no writer of ledger {ledger}.");
        ProposalOutcome outcome;
        using (var batch = await writer.CreateBatchAsync(1, (int)(LedgerBatch.MaxHeaderLength(1) + LedgerBatch.MaxRowLength(row.Key.Length, 0, token.Length, row.Payload.Length)), cancellationToken))
        {
            _ = batch.AddRow(row.Key, row.Kind, default, token, new ReadOnlySequence<byte>(row.Payload));
            outcome = await writer.ProposeAsync(batch, cancellationToken);
        }

        if (!outcome.TryGetValue(out ProposalOutcome.Durable _))
        {
            throw new InvalidOperationException($"A proposal to ledger {ledger} answered {outcome}.");
        }
    }

    /// <summary>
    /// Ends the open segment of the open ledger <paramref name="ledger"/> with a marker batch, as a failure-triggered drain
    /// first does (ledgers.md §2.4), and returns the marker's slot once it is durable: the segment before it is closed.
    /// </summary>
    public async Task<Slot> EndSegmentAsync(LedgerId ledger, CancellationToken cancellationToken)
    {
        var writer = _registry.TryGet(ledger) ?? throw new InvalidOperationException($"The process serves no writer of ledger {ledger}.");
        var outcome = await writer.EndSegmentAsync(cancellationToken);
        return outcome.TryGetValue(out ProposalOutcome.Durable durable)
            ? durable.Slot
            : throw new InvalidOperationException($"A marker of ledger {ledger} answered {outcome}.");
    }

    /// <summary>
    /// Returns the slots below <paramref name="below"/> at which <paramref name="ledger"/>'s decided batches start
    /// segments, from a scan of the decided ledger through the process's reader, which resumes after backpressure; or
    /// <see langword="null"/> when a replica reports the ledger dropped. <paramref name="below"/> is at most a contiguous
    /// tail the process's reader learned, or the closed ledger's <c>T</c>.
    /// </summary>
    public async Task<ImmutableArray<Slot>?> DecidedStartsAsync(LedgerMetadata ledger, Slot below, CancellationToken cancellationToken)
    {
        var starts = ImmutableArray.CreateBuilder<Slot>();
        using var view = new LedgerSegmentView(ledger.Id);
        var next = Slot.Zero;
        for (var attempt = 0; attempt < 1000 && next < below; attempt++)
        {
            await foreach (var item in Reader.ScanAsync(ledger, next, below, view, cancellationToken: cancellationToken))
            {
                if (item.Read.TryGetValue(out LedgerRead.Data data))
                {
                    using (data)
                    {
                        if (data.View.IsSegmentStart)
                        {
                            starts.Add(data.Slot);
                        }

                        next = data.Slot + 1;
                    }
                }
                else if (item.Read.TryGetValue(out ReplicaWriteOutcome.Dropped? _))
                {
                    return null;
                }
            }
        }

        return next == below ? starts.ToImmutable() : throw new InvalidOperationException($"Backpressure kept a scan of ledger {ledger.Id} from reaching {below}.");
    }

    /// <summary>
    /// Opens another process's map view of the register, which lags this one's until it reads.
    /// </summary>
    public Task<LedgerMapView> OpenViewAsync(CancellationToken cancellationToken) => LedgerMapView.OpenAsync(_register, cancellationToken);

    /// <summary>
    /// Returns another process's reader over the shared replicas, through <paramref name="view"/>. The caller disposes it.
    /// </summary>
    public LedgerReader OpenReader(LedgerMapView view) => new(view, Transport, TimeProvider, localNode: null);

    /// <summary>
    /// Refreshes the process's map view and waits for its writer registry to serve the ledgers it lists under this node's
    /// claims, as after a transition whose outcome was unknown.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var view = await Map.ReadAsync(cancellationToken);
        await _registry.ReconciledAsync(view.Epoch, cancellationToken);
    }

    /// <summary>Returns the node's reconciliation over <paramref name="view"/>, which drops the replicas the view revokes.</summary>
    public ReplicaReconciler Reconciler(LedgerMapView view) => new(_transport.Node(Node), view);

    /// <summary>Returns each replica node's reconciliation over <paramref name="view"/>, which drops the replicas the view revokes.</summary>
    public IEnumerable<ReplicaReconciler> Reconcilers(LedgerMapView view) => Replicas.Select(node => new ReplicaReconciler(_transport.Node(node), view));

    /// <summary>Drops every node's replica of <paramref name="ledger"/>, as their reconciliations do once their views revoke it.</summary>
    public async ValueTask DropReplicaAsync(LedgerId ledger, CancellationToken cancellationToken)
    {
        foreach (var node in Replicas)
        {
            await _transport.Node(node).DropReplicaAsync(ledger, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_restarted)
        {
            return;
        }

        await _registry.DisposeAsync();
        await Reader.DisposeAsync();
        await Committer.DisposeAsync();
        _transport.Dispose();
    }

    /// <summary>
    /// Restarts the process, as a crash and a new process on the node do: disposes its registry, which ends its writers'
    /// lifetimes and leaves their undecided proposals unanswered, its reader, and its committer, and returns the next
    /// process over the same replicas and map register, with a view of its own, which serves no ledger until its executor
    /// takes one over. The next process owns the replicas; this one's disposal does nothing.
    /// </summary>
    public async Task<LiveLedgers> RestartAsync(CancellationToken cancellationToken)
    {
        _restarted = true;
        await _registry.DisposeAsync();
        await Reader.DisposeAsync();
        await Committer.DisposeAsync();
        var map = await LedgerMapView.OpenAsync(_register, cancellationToken);
        return new LiveLedgers(_transport, _register, map, new LedgerReader(map, Transport, TimeProvider, Node), new LedgerMapCommitter(_register, int.MaxValue), TimeProvider, _options, Replicas.Length)
        {
            Transport = Transport,
            Chains = Chains,
        };
    }

    private TransitionChoice Choice() => new(Node, Replicas, Window, 0, Map.Current.Epoch);

    private async Task ExecuteAsync(Transition transition, CancellationToken cancellationToken)
    {
        var outcome = await _executor.ExecuteAsync(transition, (change, token) => Committer.CommitAsync(change, token), cancellationToken);
        if (outcome.Value is not LedgerMapCommitOutcome.Applied)
        {
            throw new InvalidOperationException($"The transition {transition.Value} answered {outcome.Value}.");
        }
    }
}

/// <summary>A row a test proposes: its key, kind, and payload.</summary>
internal readonly record struct LiveRow(byte[] Key, RowKind Kind, byte[] Payload)
{
    /// <summary>Returns a row of <paramref name="key"/> and <paramref name="payload"/>, as UTF-8.</summary>
    public static LiveRow Of(string key, RowKind kind, string payload) =>
        new(System.Text.Encoding.UTF8.GetBytes(key), kind, System.Text.Encoding.UTF8.GetBytes(payload));
}

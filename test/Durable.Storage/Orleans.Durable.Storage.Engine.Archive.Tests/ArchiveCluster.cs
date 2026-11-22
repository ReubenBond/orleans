using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// One node's ledgers over memory replicas and a memory <c>ledger-map</c> register, its primary's process, and a memory
/// archive store: the primary appends rows, storing each key's token chain and versions as Journals will
/// (<see cref="ArchiveChains"/>), ends segments, executes transitions, drains closed segments into the store, and drops
/// ledgers, and each process reads histories through a <see cref="ArchiveReader"/> of its own. Its helpers take the
/// test's token.
/// </summary>
internal sealed class ArchiveCluster : IAsyncDisposable
{
    public const int Window = 4;

    private readonly List<ArchiveProcess> _processes = [];

    private ArchiveCluster()
    {
        Committer = new LedgerMapCommitter(Register, 1 << 20);
        Chains = new ArchiveChains(Store);
    }

    public static NodeId Node { get; } = new(new Guid("00000000-0000-0000-0000-000000000001"));

    public InMemoryLedgerReplicaTransport Transport { get; } = new();

    public InMemoryConditionalRegister Register { get; } = new();

    public LedgerMapCommitter Committer { get; }

    public InMemoryArchiveStore Store { get; } = new();

    /// <summary>Gets the token chains and versions the primary's rows store.</summary>
    public ArchiveChains Chains { get; }

    /// <summary>Gets the primary's process, which executes every transition.</summary>
    public ArchiveProcess Primary => _processes[0];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a cluster with its primary's process, whose map lists one open ledger over the whole keyspace.</summary>
    public static async Task<ArchiveCluster> StartAsync()
    {
        var cluster = new ArchiveCluster();
        _ = await cluster.StartProcessAsync(cluster.Store);
        await cluster.BootstrapAsync();
        return cluster;
    }

    /// <summary>Starts a cluster with its primary's process and no ledger.</summary>
    public static async Task<ArchiveCluster> StartUnbootstrappedAsync()
    {
        var cluster = new ArchiveCluster();
        _ = await cluster.StartProcessAsync(cluster.Store);
        return cluster;
    }

    /// <summary>Bootstraps the map from the primary's process.</summary>
    public async Task BootstrapAsync() =>
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await Primary.Executor!.ExecuteAsync(new Transition.Bootstrap(Choice(0)), Committer.CommitAsync, Token)).Value);

    /// <summary>
    /// Starts a process with its own map view, reader, and history reader over <paramref name="store"/>, its backoff on
    /// <paramref name="time"/>, whose replica reads pass through <paramref name="reads"/> when it is given, and wait for
    /// <paramref name="gate"/> before they reach the replica when it is given.
    /// </summary>
    public async Task<ArchiveProcess> StartProcessAsync(IArchiveStore store, TimeProvider? time = null, ReplicaReadHook? reads = null, ReplicaReadGate? gate = null)
    {
        var view = await LedgerMapView.OpenAsync(Register, Token);
        ILedgerReplicaTransport transport = reads is null && gate is null ? Transport : new HookedTransport(Transport, reads ?? NoHook, gate);
        var process = new ArchiveProcess(view, transport, store, time ?? TimeProvider.System, primary: _processes.Count == 0);
        _processes.Add(process);
        return process;
    }

    /// <summary>Returns the ledger the primary's view lists open over the range which contains <paramref name="key"/>.</summary>
    public LedgerMetadata OpenLedger(byte key) => Primary.View.Current.Find([key])!.Value.Open;

    /// <summary>Appends one row of <paramref name="key"/> through the primary, and returns its tag.</summary>
    public async Task<Tag> AppendAsync(byte key, RowKind kind, string payload)
    {
        var ledger = OpenLedger(key);
        var writer = Primary.Registry!.TryGet(ledger.Id) ?? throw new InvalidOperationException($"The primary serves no writer of {ledger.Id}.");
        using var bytes = ArcBuffer.Wrap(Encoding.UTF8.GetBytes(payload));
        using var row = new Row([key], kind, bytes);
        var outcome = await Chains.ProposeAsync(writer, [row], cancellationToken: Token);
        Assert.True(outcome.TryGetValue(out ProposalOutcome.Durable durable), $"The proposal was {outcome}.");
        return new Tag(ledger.Id, durable.Slot, 0);
    }

    /// <summary>Ends the segment of the open ledger over <paramref name="key"/>'s range, with a marker batch which starts the next.</summary>
    public async Task EndSegmentAsync(byte key)
    {
        var writer = Primary.Registry!.TryGet(OpenLedger(key).Id) ?? throw new InvalidOperationException("The primary serves no writer of the open ledger.");
        Assert.True((await writer.EndSegmentAsync(Token)).TryGetValue(out ProposalOutcome.Durable _));
    }

    /// <summary>Checks the open ledger's seal from the primary's process, which carries its tail to the replicas.</summary>
    public async Task CarryTailAsync(byte key) => _ = await Primary.Reader.ReadTailAsync(OpenLedger(key), Token);

    /// <summary>Replaces the open ledger over <paramref name="key"/>'s range, closing it, through the primary.</summary>
    public async Task<LedgerId> ReplaceLedgerAsync(byte key)
    {
        var ledger = OpenLedger(key);
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await Primary.Executor!.ExecuteAsync(
            new Transition.Replace(ledger.Id, Choice(Primary.View.Current.Epoch)),
            Committer.CommitAsync,
            Token)).Value);
        return ledger.Id;
    }

    /// <summary>
    /// Drains the segments of <paramref name="ledger"/> which end below <paramref name="below"/>, by default its recorded
    /// <c>T</c>, as its drain would: each whole closed segment's rows into the store, in order (archive.md §9).
    /// </summary>
    public async Task DrainAsync(LedgerId ledger, Slot? below = null)
    {
        var listed = Primary.View.Current.Get(ledger)!.Value;
        var end = below ?? Assert.IsType<LedgerStatus.Closed>(listed.Status.Value).T;
        foreach (var segment in await SegmentsAsync(listed, end))
        {
            try
            {
                KeyedRow.DrainByKey(segment, (key, rows) => Store.Drain(key, rows));
            }
            finally
            {
                KeyedRow.Release(segment);
            }
        }
    }

    /// <summary>
    /// Returns the rows of <paramref name="ledger"/>'s segments which are closed below <paramref name="end"/>, by segment, each
    /// row owned: a segment is closed once a later batch starts the next, or at a closed ledger's <c>T</c>.
    /// </summary>
    public async Task<List<KeyedRow[]>> SegmentsAsync(LedgerMetadata ledger, Slot end)
    {
        var segments = new List<KeyedRow[]>();
        var current = new List<KeyedRow>();
        using var bodies = new ArcBufferWriter();
        LedgerSegmentId? segment = null;
        using var view = new LedgerSegmentView(ledger.Id);
        try
        {
            await foreach (var item in Primary.Reader.ScanAsync(ledger, Slot.Zero, end, view, cancellationToken: Token))
            {
                Assert.True(item.Read.TryGetValue(out LedgerRead.Data data), $"A drain's scan read {item.Read}.");
                using (data)
                {
                    if (segment is { } open && open != view.Segment)
                    {
                        segments.Add([.. current]);
                        current.Clear();
                    }

                    segment = view.Segment;
                    foreach (var row in item.Rows)
                    {
                        current.Add(new KeyedRow(row.Key.ToArray(), ArchiveRow.Of(row, view.Segment, bodies)));
                    }
                }
            }

            if (ledger.Status.Value is LedgerStatus.Closed closed && closed.T == end)
            {
                segments.Add([.. current]);
                current.Clear();
            }

            return segments;
        }
        finally
        {
            KeyedRow.Release(current);
        }
    }

    /// <summary>Drops the closed <paramref name="ledger"/> from the map and from its replicas.</summary>
    public async Task DropAsync(LedgerId ledger)
    {
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await Committer.CommitAsync(new LedgerMapChange.Drop(ledger), Token)).Value);
        _ = await Primary.View.ReadAsync(Token);
        _ = await new ReplicaReconciler(Transport.Node(Node), Primary.View).ReconcileAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            await process.DisposeAsync();
        }

        await Committer.DisposeAsync();
        Transport.Dispose();
        Store.Dispose();
    }

    private static TransitionChoice Choice(ulong epoch) => new(Node, [Node], Window, 0, epoch);

    private static ReplicaRead? NoHook(LedgerId ledger, Slot slot) => null;
}

/// <summary>A process: its map view, reader, and history reader; the primary's also has its registry and executor.</summary>
internal sealed class ArchiveProcess : IAsyncDisposable
{
    public ArchiveProcess(LedgerMapView view, ILedgerReplicaTransport transport, IArchiveStore store, TimeProvider time, bool primary)
    {
        View = view;
        Reader = new LedgerReader(view, transport, TimeProvider.System, ArchiveCluster.Node);
        Archive = new ArchiveReader(Reader, store, time);
        if (primary)
        {
            Registry = new LedgerWriterRegistry(Reader, 1 << 25, LedgerWriterOptions.Default, TimeProvider.System, NullLoggerFactory.Instance);
            Executor = new TransitionExecutor(Registry, NullLogger<TransitionExecutor>.Instance);
        }
    }

    public LedgerMapView View { get; }

    public LedgerReader Reader { get; }

    public ArchiveReader Archive { get; }

    public LedgerWriterRegistry? Registry { get; }

    public TransitionExecutor? Executor { get; }

    public async ValueTask DisposeAsync()
    {
        if (Registry is not null)
        {
            await Registry.DisposeAsync();
        }

        await Reader.DisposeAsync();
    }
}

/// <summary>Answers a replica's read of a ledger's slot in its place, or null to let it through.</summary>
internal delegate ReplicaRead? ReplicaReadHook(LedgerId ledger, Slot slot);

/// <summary>Returns a task a replica's read of a ledger's slot waits for before it reaches the replica.</summary>
internal delegate Task ReplicaReadGate(LedgerId ledger, Slot slot);

/// <summary>
/// A transport whose replicas pass each read through a <see cref="ReplicaReadHook"/>, and through a
/// <see cref="ReplicaReadGate"/> when one is given.
/// </summary>
internal sealed class HookedTransport(ILedgerReplicaTransport inner, ReplicaReadHook reads, ReplicaReadGate? gate = null) : ILedgerReplicaTransport
{
    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => new Replica(inner.Open(node, ledger, options), ledger, reads, gate);

    public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
        inner.HeartbeatAsync(node, ledgers, cancellationToken);

    private sealed class Replica(ILedgerReplica inner, LedgerId ledger, ReplicaReadHook reads, ReplicaReadGate? gate) : ILedgerReplica
    {
        public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            gate is null ? Through(slot, contiguousTail, cancellationToken) : GatedAsync(gate(ledger, slot), slot, contiguousTail, cancellationToken);

        private async ValueTask<ReplicaRead> GatedAsync(Task open, Slot slot, Slot contiguousTail, CancellationToken cancellationToken)
        {
            await open.WaitAsync(cancellationToken);
            return await Through(slot, contiguousTail, cancellationToken);
        }

        private ValueTask<ReplicaRead> Through(Slot slot, Slot contiguousTail, CancellationToken cancellationToken) =>
            reads(ledger, slot) is { } answer ? ValueTask.FromResult(answer) : inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>Reads a history read's rows as their tags, kinds, payloads, and whether the prefix holds them.</summary>
internal static class ArchiveRows
{
    public static ImmutableArray<(Tag Tag, RowKind Kind, string Payload, bool Held)> Of(ReadOnlySpan<ArchiveRow> rows)
    {
        var builder = ImmutableArray.CreateBuilder<(Tag, RowKind, string, bool)>(rows.Length);
        foreach (var row in rows)
        {
            builder.Add((row.Tag, row.Kind, Encoding.UTF8.GetString(row.Body.ToArray()), row.IsHeld));
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Returns the content a read folds to, its prefix and then its rows not held, as UTF-8.</summary>
    public static string Content(ArchiveRead read) => Encoding.UTF8.GetString(ArchiveOracle.Fold(read.Prefix, read.Rows).Content);
}

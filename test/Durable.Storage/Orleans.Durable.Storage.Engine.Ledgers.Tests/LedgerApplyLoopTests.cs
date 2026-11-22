using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// A node's delivery of the decided batches of the ledgers it replicates (ledgers.md §8.5, LP-I10, LP-I11, LG-I13): its
/// applier receives, in slot order and as resolved rows, an open ledger's batches below the tail the node learns from its
/// own replica, and once the map closes the ledger the rest below <c>T</c>, read from other replicas where its own lacks
/// them; an applier which restarts mid-segment receives exactly the rows from its watermark on; and a ledger the map no
/// longer lists leaves the applier. The ledger is <see cref="LedgerSegmentScript"/>'s, whose writer recorded each row's
/// version as it gave it.
/// </summary>
public sealed class LedgerApplyLoopTests : IDisposable
{
    private static readonly TimeSpan Signal = TimeSpan.FromSeconds(10);
    private static readonly NodeId Node = LedgerSegmentScript.Nodes[0];
    private static readonly long End = LedgerSegmentScript.Script.Length;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Once the map lists the ledger <c>Closed(T)</c>, the applier receives every batch below <c>T</c>, each row resolved to
    /// the version its writer gave it, also the batches the node's own replica lacks, which the node reads from another.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AClosedLedgersApplierReceivesEveryBatchBelowTIncludingThoseItsReplicaLacks()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var (map, open, expected) = await LedgerSegmentScript.WriteAsync(transport, token);
        var closed = await LedgerSegmentScript.RecoverAsync(map, transport, open, token);
        await map.PublishAsync(new LedgerMapValue(2, new LedgerId(LedgerSegmentScript.Id.Value + 1), 0, [closed]), token);
        var lacking = new LackingTransport(transport, Node, lacks: [1, 5, 9]);
        await using var reader = new LedgerReader(map.View, lacking, new FakeTimeProvider(), Node);
        var applier = new RecordingApplier(Slot.Zero);

        await using (var loop = new LedgerApplyLoop(reader, applier.Callbacks, new FakeTimeProvider()))
        {
            await applier.Until(applied => applied.Next == End).WaitAsync(Signal, TimeProvider.System, token);
            Assert.False(loop.Completion.IsCompleted);
        }

        Assert.Equal(Enumerable.Range(0, (int)End).Select(static slot => (long)slot), applier.Slots);
        Assert.Equal(expected, applier.Rows);
        Assert.True(lacking.Lacked > 0, "The node's own replica was never asked for a slot it lacks.");
    }

    /// <summary>
    /// An open ledger's applier receives the batches below the tail the node learns from its own replica, which is below
    /// the end its recovery finds, since no write carries its own slot; once the map closes the ledger, it receives the
    /// rest below <c>T</c>. A delivery which stopped at the last tail it learned would leave the applier short of
    /// <c>T</c>, and this test would time out. The learned tail lies inside the last segment, and the delivery keeps its
    /// segment view across its scans, so it resumes there without rewinding: the node's replica is asked each slot once.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnOpenLedgersApplierReceivesTheBatchesBelowItsLearnedTailAndTheRestOnceItCloses()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var (map, open, expected) = await LedgerSegmentScript.WriteAsync(transport, token);
        var learned = await RetainedTailAsync(transport, token);
        Assert.Equal(End - 1, learned);
        var counting = new LackingTransport(transport, Node, lacks: []);
        await using var reader = new LedgerReader(map.View, counting, new FakeTimeProvider(), Node);
        var applier = new RecordingApplier(Slot.Zero);

        await using (var loop = new LedgerApplyLoop(reader, applier.Callbacks, new FakeTimeProvider()))
        {
            await applier.Until(applied => applied.Next == learned).WaitAsync(Signal, TimeProvider.System, token);
            Assert.Equal(Enumerable.Range(0, (int)learned).Select(static slot => (long)slot), applier.Slots);

            var closed = await LedgerSegmentScript.RecoverAsync(map, transport, open, token);
            Assert.Equal(new Slot(End), closed.Status.Value is LedgerStatus.Closed { T: var t } ? t : default);
            await map.PublishAsync(new LedgerMapValue(2, new LedgerId(LedgerSegmentScript.Id.Value + 1), 0, [closed]), token);
            await applier.Until(applied => applied.Next == End).WaitAsync(Signal, TimeProvider.System, token);
            Assert.False(loop.Completion.IsCompleted);
        }

        Assert.Equal(Enumerable.Range(0, (int)End).Select(static slot => (long)slot), applier.Slots);
        Assert.Equal(expected, applier.Rows);
        Assert.Equal(Enumerable.Repeat(1, (int)End), Enumerable.Range(0, (int)End).Select(slot => counting.ReadsOf(slot)));
    }

    /// <summary>
    /// An applier whose durable watermark lies anywhere in a segment, its start or its middle, receives exactly the batches
    /// and rows from its watermark on, each row resolved as a scan from the ledger's start resolves it (LG-I13).
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(12)]
    public async Task AnApplierWhichRestartsMidSegmentReceivesExactlyTheRowsFromItsWatermarkOn(long watermark)
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var (map, open, expected) = await LedgerSegmentScript.WriteAsync(transport, token);
        var closed = await LedgerSegmentScript.RecoverAsync(map, transport, open, token);
        await map.PublishAsync(new LedgerMapValue(2, new LedgerId(LedgerSegmentScript.Id.Value + 1), 0, [closed]), token);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), Node);
        var applier = new RecordingApplier(new Slot(watermark));

        await using (new LedgerApplyLoop(reader, applier.Callbacks, new FakeTimeProvider()))
        {
            await applier.Until(applied => applied.Next == End).WaitAsync(Signal, TimeProvider.System, token);
        }

        Assert.Equal(Enumerable.Range((int)watermark, (int)(End - watermark)).Select(static slot => (long)slot), applier.Slots);
        Assert.Equal(expected.Where(row => row.Slot >= watermark), applier.Rows);
    }

    /// <summary>
    /// A ledger the node's map view no longer lists, one the map revoked, leaves the applier: the loop delivers no more of
    /// it and tells the applier, which reads the rest from the archive store.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ALedgerTheViewNoLongerListsLeavesTheApplier()
    {
        var token = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var (map, open, _) = await LedgerSegmentScript.WriteAsync(transport, token);
        var closed = await LedgerSegmentScript.RecoverAsync(map, transport, open, token);
        var next = new LedgerId(LedgerSegmentScript.Id.Value + 1);
        await map.PublishAsync(new LedgerMapValue(2, next, 0, [closed]), token);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), Node);
        var applier = new RecordingApplier(Slot.Zero);

        await using var loop = new LedgerApplyLoop(reader, applier.Callbacks, new FakeTimeProvider());
        await applier.Until(applied => applied.Next == End).WaitAsync(Signal, TimeProvider.System, token);
        await map.PublishAsync(new LedgerMapValue(3, next, 0, []), token);

        await applier.Until(static applied => applied.Removed.Count > 0).WaitAsync(Signal, TimeProvider.System, token);
        Assert.Equal([LedgerSegmentScript.Id], applier.Removed);
        Assert.False(loop.Completion.IsCompleted);
    }

    /// <summary>Returns the contiguous tail the node's own replica retained from the writer's writes.</summary>
    private static async Task<long> RetainedTailAsync(InMemoryLedgerReplicaTransport transport, CancellationToken token)
    {
        await using var replica = transport.Open(Node, LedgerSegmentScript.Id, new LedgerReplicaOptions { WindowSize = LedgerSegmentScript.Window });
        var tail = await replica.ReadTailAsync(Slot.Zero, seal: false, token);
        return tail.TryGetValue(out LedgerReplicaTail answer) ? answer.ContiguousTail.Value : throw new InvalidOperationException(tail.ToString());
    }

    /// <summary>
    /// An applier which records each batch's slot and resolved rows, the ledgers it removed, and the slot it next needs,
    /// starting from a watermark.
    /// </summary>
    private sealed class RecordingApplier(Slot watermark)
    {
        private readonly Lock _lock = new();
        private readonly List<(Func<RecordingApplier, bool> Done, TaskCompletionSource Signal)> _waits = [];

        public long Next { get; private set; } = watermark.Value;

        public List<long> Slots { get; } = [];

        public List<ScriptedRow> Rows { get; } = [];

        public List<LedgerId> Removed { get; } = [];

        /// <summary>Completes once <paramref name="done"/> holds of the applier.</summary>
        public Task Until(Func<RecordingApplier, bool> done)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                if (done(this))
                {
                    return Task.CompletedTask;
                }

                _waits.Add((done, signal));
            }

            return signal.Task;
        }

        /// <summary>Gets the loop's callbacks into the recording.</summary>
        public LedgerApplier Callbacks => new()
        {
            NextSlotAsync = (_, _) => ValueTask.FromResult(watermark),
            ApplyAsync = ApplyAsync,
            RemoveAsync = RemoveAsync,
        };

        private ValueTask ApplyAsync(LedgerId ledger, LedgerScanItem item, CancellationToken cancellationToken)
        {
            Assert.Equal(LedgerSegmentScript.Id, ledger);
            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
            Changed(() =>
            {
                Assert.Equal(Next, data.Slot.Value);
                LedgerSegmentScript.Collect(item, data.Slot.Value, Rows);
                Slots.Add(data.Slot.Value);
                Next = data.Slot.Value + 1;
            });
            return ValueTask.CompletedTask;
        }

        private ValueTask RemoveAsync(LedgerId ledger, CancellationToken cancellationToken)
        {
            Changed(() => Removed.Add(ledger));
            return ValueTask.CompletedTask;
        }

        private void Changed(Action change)
        {
            List<TaskCompletionSource> done = [];
            lock (_lock)
            {
                change();
                foreach (var wait in _waits.ToList())
                {
                    if (wait.Done(this))
                    {
                        done.Add(wait.Signal);
                        _ = _waits.Remove(wait);
                    }
                }
            }

            done.ForEach(static signal => signal.SetResult());
        }
    }

    /// <summary>
    /// A transport whose replica on <paramref name="node"/> lacks the batches at <paramref name="lacks"/>, which it reports
    /// missing, as a replica which missed their writes does, and which counts the reads of each slot that replica serves.
    /// </summary>
    private sealed class LackingTransport(ILedgerReplicaTransport inner, NodeId node, long[] lacks) : ILedgerReplicaTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> _reads = new();
        private int _lacked;

        /// <summary>Gets how many reads of a slot it lacks the node's replica answered missing.</summary>
        public int Lacked => Volatile.Read(ref _lacked);

        /// <summary>Returns how many reads of <paramref name="slot"/> the node's replica served.</summary>
        public int ReadsOf(long slot) => _reads.GetValueOrDefault(slot);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId target, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(target, ledgers, cancellationToken);

        public ILedgerReplica Open(NodeId target, LedgerId ledger, LedgerReplicaOptions options)
        {
            var replica = inner.Open(target, ledger, options);
            if (target != node)
            {
                return replica;
            }

            return new DelegatingLedgerReplica
            {
                Write = replica.WriteAsync,
                Read = (slot, tail, token) =>
                {
                    _ = _reads.AddOrUpdate(slot.Value, 1, static (_, count) => count + 1);
                    if (!lacks.Contains(slot.Value))
                    {
                        return replica.ReadAsync(slot, tail, token);
                    }

                    _ = Interlocked.Increment(ref _lacked);
                    return ValueTask.FromResult<ReplicaRead>(ReplicaRead.Missing.Instance);
                },
                ReadTail = replica.ReadTailAsync,
                Disposal = replica.DisposeAsync,
            };
        }
    }
}

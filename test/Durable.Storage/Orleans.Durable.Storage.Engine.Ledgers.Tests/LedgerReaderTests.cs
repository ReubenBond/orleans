using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// The reader (ledgers.md §2.5, §8.4): an open ledger read to its contiguous tail and a closed one to the map's
/// <c>T</c>, the map re-read when a replica reports a seal or a drop, and the process's one <see cref="Ledger"/> of each
/// ledger, which the reader releases once the map no longer lists the ledger and its leases end.
/// </summary>
public sealed class LedgerReaderTests : IDisposable
{
    private const int Window = 4;
    private static readonly LedgerId Id = new(3);
    private static readonly NodeId[] Nodes = [LedgerMapSamples.Node(1), LedgerMapSamples.Node(2), LedgerMapSamples.Node(3)];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LG-I8: an open ledger is read to the contiguous tail the reader learned from a seal check, and a scan beyond the
    /// known tail is refused, since its slots would not be decided.
    /// </summary>
    [Fact]
    public async Task AnOpenLedgerIsScannedToTheContiguousTailItsReaderLearned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var open = await ProposeAsync(primary, map, ["a", "b", "c"], cancellationToken);

        await AssertRefusedAsync(() => ScanAsync(reader, open, Slot.Zero, new Slot(1), cancellationToken));
        var tail = (await reader.ReadTailAsync(open, cancellationToken)).Tail;
        Assert.False(tail.IsSealed);

        // The writer's last write carries the tail of the writes before it (ledger-protocol.md §8.2).
        Assert.Equal(new Slot(2), tail.ContiguousTail);
        Assert.Equal(["a", "b"], await ScanAsync(reader, open, Slot.Zero, tail.ContiguousTail, cancellationToken));
        await AssertRefusedAsync(() => ScanAsync(reader, open, Slot.Zero, new Slot(3), cancellationToken));
    }

    /// <summary>
    /// LG-I4, LG-I8: a closed ledger is read below the map's <c>T</c>, which the reader passes to its <see cref="Ledger"/>,
    /// and its tail is <c>(T, true)</c> from the map without I/O.
    /// </summary>
    [Fact]
    public async Task AClosedLedgerIsReadToTheMapsRecordedTail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var open = await ProposeAsync(primary, map, ["a", "b", "c"], cancellationToken);
        await using (var executor = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null))
        {
            using var lease = executor.LeaseLedger(open);
            Assert.Equal(new Slot(3), (await lease.Ledger.RecoverAsync(cancellationToken)).End);
        }

        var closed = Closed(open, new Slot(3));
        await using var noIo = new LedgerReader(map.View, new UnreachableTransport(), new FakeTimeProvider(), localNode: null);
        Assert.Equal(new LedgerTail(new Slot(3), IsSealed: true), (await noIo.ReadTailAsync(closed, cancellationToken)).Tail);

        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        Assert.Equal(["a", "b", "c"], await ScanAsync(reader, closed, Slot.Zero, new Slot(3), cancellationToken));
        await AssertRefusedAsync(() => ScanAsync(reader, closed, Slot.Zero, new Slot(4), cancellationToken));
    }

    /// <summary>
    /// LG-I8, ledgers.md §2.5: a read answers only a decided slot. Below a closed ledger's <c>T</c> it answers the batch;
    /// at <c>T</c>, where a deposed writer's batch may lie, and at or above an open ledger's known contiguous tail, it is
    /// refused with <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    [Fact]
    public async Task AReadAnswersOnlyADecidedSlot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var open = await ProposeAsync(primary, map, ["a", "b", "c"], cancellationToken);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var tail = (await reader.ReadTailAsync(open, cancellationToken)).Tail.ContiguousTail;
        Assert.Equal(new Slot(2), tail);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await reader.ReadAsync(open, tail, cancellationToken));
        Assert.Equal("b", await ReadAsync(reader, open, new Slot(1), cancellationToken));

        // The ledger closes at T = 2, below the batch the writer wrote at slot 2.
        await using var closedReader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var closed = Closed(open, new Slot(2));
        Assert.Equal("b", await ReadAsync(closedReader, closed, new Slot(1), cancellationToken));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await closedReader.ReadAsync(closed, new Slot(2), cancellationToken));
    }

    /// <summary>
    /// ledgers.md §2.5: a scan whose bound lies below its start is refused when it is called, before it is enumerated.
    /// </summary>
    [Fact]
    public async Task AScanBelowItsStartIsRefusedAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var reader = new LedgerReader(map.View, new UnreachableTransport(), new FakeTimeProvider(), localNode: null);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => reader.ScanAsync(Open(new SequencerInstanceId(Guid.NewGuid())), new Slot(2), new Slot(1), cancellationToken));
    }

    /// <summary>
    /// ledgers.md §2.5: the reader's disposal waits for a scan's lease to end, and then every replica the reader opened
    /// is disposed.
    /// </summary>
    [Fact]
    public async Task DisposingTheReaderDuringAScanDisposesEveryReplicaOnceTheScanEnds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var inner = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, inner, new FakeTimeProvider(), localNode: null);
        var closed = Closed(await ProposeAsync(primary, map, ["a", "b", "c"], cancellationToken), new Slot(3));
        var transport = new CountingTransport(inner);
        var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);

        Task disposal;
        await using (var scan = reader.ScanAsync(closed, Slot.Zero, new Slot(3), cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            Assert.True(await scan.MoveNextAsync());
            Assert.True(scan.Current.Read.TryGetValue(out LedgerRead.Data first));
            first.Dispose();
            disposal = reader.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => ScanAsync(reader, closed, Slot.Zero, new Slot(3), cancellationToken));
        }

        await disposal;
        Assert.Equal(Nodes.Length, transport.Opened);
        Assert.Equal(transport.Opened, transport.Disposed);
    }

    /// <summary>
    /// LG-I8, ledgers.md §8.4: a reader whose seal check finds an open ledger sealed re-reads the map, and once the map
    /// lists the ledger closed its <see cref="Ledger"/> learns <c>T</c>, so it scans to <c>T</c>.
    /// </summary>
    [Fact]
    public async Task ASealedAnswerMakesTheReaderReReadTheMap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var open = await ProposeAsync(primary, map, ["a", "b", "c"], cancellationToken);
        using (var lease = primary.LeaseLedger(open))
        {
            Assert.Equal(new Slot(3), (await lease.Ledger.RecoverAsync(cancellationToken)).End);
        }

        map.Write(Map(epoch: 2, Closed(open, new Slot(3))));
        var reads = map.Reads;

        Assert.True((await reader.ReadTailAsync(open, cancellationToken)).Tail.IsSealed);

        Assert.Equal(reads + 1, map.Reads);
        Assert.False(map.View.Current.Get(Id)!.Value.IsOpen);
        Assert.Equal(["a", "b", "c"], await ScanAsync(reader, open, Slot.Zero, new Slot(3), cancellationToken));
    }

    /// <summary>
    /// LG-I8, ledgers.md §8.4: a read which meets a dropped replica answers <see cref="ReplicaWriteOutcome.Dropped"/> once the
    /// reader re-read the map, and a scan ends with it; the reader releases the ledger's <see cref="Ledger"/> once the
    /// map no longer lists the ledger and its last lease ends.
    /// </summary>
    [Fact]
    public async Task ADroppedAnswerMakesTheReaderReReadTheMapAndReleaseTheLedger()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var open = await ProposeAsync(primary, map, ["a"], cancellationToken);
        var closed = Closed(open, new Slot(1));
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var held = reader.LeaseLedger(closed);
        var ledger = held.Ledger;
        foreach (var node in Nodes)
        {
            await transport.Node(node).DropReplicaAsync(Id, cancellationToken);
        }

        map.Write(Map(epoch: 3));
        var reads = map.Reads;

        Assert.True((await reader.ReadAsync(closed, Slot.Zero, cancellationToken)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));
        Assert.Equal(reads + 1, map.Reads);
        await using (var scan = reader.ScanAsync(closed, Slot.Zero, new Slot(1), cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            Assert.True(await scan.MoveNextAsync());
            Assert.True(scan.Current.Read.TryGetValue(out ReplicaWriteOutcome.Dropped? _));
            Assert.False(await scan.MoveNextAsync());
        }

        // The scan's lease came from a new Ledger, since the reader released the listed one; the held lease keeps it.
        _ = await ledger.ReadTailAsync(cancellationToken);
        held.Dispose();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await ledger.ReadTailAsync(cancellationToken));
    }

    /// <summary>
    /// ledgers.md §2.5: the reader holds one <see cref="Ledger"/> per ledger, which every lease of it shares.
    /// </summary>
    [Fact]
    public async Task EveryLeaseOfALedgerSharesItsLedger()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var ledger = Open(new SequencerInstanceId(Guid.NewGuid()));

        using var first = reader.LeaseLedger(ledger);
        using var second = reader.LeaseLedger(ledger);

        Assert.Same(first.Ledger, second.Ledger);
        Assert.Equal(Window, first.Ledger.WindowSize);
    }

    /// <summary>
    /// ledger-protocol.md §2.7: the reader builds a ledger's storage from its map entry's location: the quorum over the
    /// replicas a ledger on nodes names, and the process's object-ledger store for a ledger in the object store, which a
    /// reader without one refuses (object-ledgers.md §2.1).
    /// </summary>
    [Fact]
    public async Task ALedgerIsOpenedOverTheStorageItsLocationNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var inObjectStore = new LedgerMetadata(Id, KeyRange.Root, LedgerLocation.ObjectStore.Instance, Window, new PrimaryClaim(Nodes[0], SequencerInstanceId.New()), LedgerStatus.Open.Instance);

        _ = Assert.Throws<NotSupportedException>(() => reader.LeaseLedger(inObjectStore));
        using var lease = reader.LeaseLedger(Open(SequencerInstanceId.New()));
        _ = Assert.IsType<LedgerTail>((await lease.Ledger.ReadTailAsync(cancellationToken)).Value);

        await using var objectReader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null)
        {
            Objects = ObjectLedgerStore.Over(new InMemoryObjectStore(), new ObjectLedgerStoreOptions(), TimeProvider.System),
        };
        using var objectLease = objectReader.LeaseLedger(inObjectStore);
        Assert.Equal(new LedgerTail(Slot.Zero, IsSealed: false), (await objectLease.Ledger.ReadTailAsync(cancellationToken)).Value);
    }

    /// <summary>
    /// ledgers.md §2.5: a read which backpressure keeps from deciding answers
    /// <see cref="ReplicaWriteOutcome.Backpressure"/>, which asserts nothing, and a scan ends with it, so its caller
    /// resumes from that slot.
    /// </summary>
    [Fact]
    public async Task ABackpressuredReadAnswersBackpressureAndEndsAScan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var reader = new LedgerReader(map.View, new BackpressuredTransport(), new FakeTimeProvider(), localNode: null);
        var closed = Closed(Open(new SequencerInstanceId(Guid.NewGuid())), new Slot(2));

        Assert.True((await reader.ReadAsync(closed, Slot.Zero, cancellationToken)).TryGetValue(out ReplicaWriteOutcome.Backpressure? _));
        await using var scan = reader.ScanAsync(closed, Slot.Zero, new Slot(2), cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await scan.MoveNextAsync());
        Assert.True(scan.Current.Read.TryGetValue(out ReplicaWriteOutcome.Backpressure? _));
        Assert.False(await scan.MoveNextAsync());
    }

    /// <summary>
    /// ledgers.md §2.5, archive.md §8.1: a scan which shares a window of reads in flight frees each place it took, those of
    /// the reads it read ahead and no longer needs among them, whether backpressure ended it with reads still in flight or
    /// its caller stopped enumerating it, so the scans beside it, and its own resumption, find every place free.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AScanWhichEndsWithReadsInFlightFreesTheirPlacesInItsWindow(bool abandoned)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var reader = new LedgerReader(map.View, new BackpressuredTransport(), new FakeTimeProvider(), localNode: null);
        var closed = Closed(Open(new SequencerInstanceId(Guid.NewGuid())), new Slot(4 * Window));
        using var window = new SemaphoreSlim(Window);
        using var view = new LedgerSegmentView(closed.Id);

        await using (var scan = reader.ScanAsync(closed, Slot.Zero, new Slot(4 * Window), view, window, cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            Assert.True(await scan.MoveNextAsync());
            Assert.True(scan.Current.Read.TryGetValue(out ReplicaWriteOutcome.Backpressure? _));
            if (!abandoned)
            {
                Assert.False(await scan.MoveNextAsync());
            }
        }

        Assert.Equal(Window, window.CurrentCount);
    }

    /// <summary>
    /// LG-I8, ledgers.md §2.5: a seal check through the reader of the process which serves the ledger's writer, which
    /// shares the process's <see cref="Ledger"/>, reports <see cref="LedgerTail.ServedHere"/> and a tail which covers every
    /// proposal the writer answered durable; another process's reports neither.
    /// </summary>
    [Fact]
    public async Task ATailCheckWhereTheWriterIsServedCoversEveryDurableProposal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        await using var other = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var open = await ProposeAsync(primary, map, ["a", "b", "c"], cancellationToken);

        // The other process's check comes first: the primary's carries its tail to the replicas, which then answer it.
        Assert.Equal(new LedgerTail(new Slot(2), IsSealed: false, ServedHere: false), (await other.ReadTailAsync(open, cancellationToken)).Tail);
        Assert.Equal(new LedgerTail(new Slot(3), IsSealed: false, ServedHere: true), (await primary.ReadTailAsync(open, cancellationToken)).Tail);
    }

    /// <summary>
    /// ledgers.md §2.5: a caller which learned an open ledger's tail through the reader, and reads below it after the
    /// reader's view revoked the ledger, meets a fresh <see cref="Ledger"/> whose known tail is 0; the read and the scan
    /// answer <see cref="ReplicaWriteOutcome.Dropped"/>, once the reader re-read the map, rather than refusing the slot.
    /// </summary>
    [Fact]
    public async Task AReadPastAFreshLedgersTailOfARevokedLedgerAnswersDropped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var open = await ProposeAsync(primary, map, ["a", "b", "c"], cancellationToken);
        await using var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        Assert.Equal(new Slot(2), (await reader.ReadTailAsync(open, cancellationToken)).Tail.ContiguousTail);

        await map.PublishAsync(Map(epoch: 3), cancellationToken);
        var reads = map.Reads;

        Assert.True((await reader.ReadAsync(open, new Slot(1), cancellationToken)).TryGetValue(out ReplicaWriteOutcome.Dropped? _));
        await using (var scan = reader.ScanAsync(open, Slot.Zero, new Slot(2), cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            Assert.True(await scan.MoveNextAsync());
            Assert.True(scan.Current.Read.TryGetValue(out ReplicaWriteOutcome.Dropped? _));
            Assert.False(await scan.MoveNextAsync());
        }

        Assert.Equal(reads + 2, map.Reads);
    }

    /// <summary>
    /// LP-I10, ledger-protocol.md §8.4, orleans.md §5.7: a read of a decided slot copies its batch from one replica. A
    /// reader on a node which holds a replica of the ledger, here the map's last, reads every decided slot from it and
    /// reaches no other node; one on a node which holds none reaches one replica per read, rotating over the replicas.
    /// Each batch crosses the transport once. The test reports each node's read calls and bytes.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADecidedReadCopiesItsBatchFromOneReplica(bool replicaOnItsNode)
    {
        const int Reads = 9;
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        string[] payloads = ["a", "b", "c"];
        var closed = Closed(await ProposeAsync(primary, map, payloads, cancellationToken), new Slot(payloads.Length));
        var metered = new ReadMeteringTransport(transport);
        var node = replicaOnItsNode ? Nodes[2] : LedgerMapSamples.Node(9);
        await using var reader = new LedgerReader(map.View, metered, new FakeTimeProvider(), node);

        var copied = 0L;
        for (var read = 0; read < Reads; read++)
        {
            var slot = new Slot(read % payloads.Length);
            Assert.True((await reader.ReadAsync(closed, slot, cancellationToken)).TryGetValue(out LedgerRead.Data data));
            using (data)
            {
                Assert.Equal(payloads[slot.Value], Payload(data));
                copied += data.Batch.Length;
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(
            "; ",
            Nodes.Select(other => string.Create(CultureInfo.InvariantCulture, $"{(other == node ? "local" : "remote")} node: {metered.Calls(other)} reads, {metered.Bytes(other)} bytes"))));
        Assert.Equal(Reads, Nodes.Sum(metered.Calls));
        Assert.Equal(copied, Nodes.Sum(metered.Bytes));
        if (replicaOnItsNode)
        {
            Assert.Equal(Reads, metered.Calls(node));
            Assert.Equal(0, Nodes.Where(other => other != node).Sum(metered.Calls));
        }
        else
        {
            Assert.All(Nodes, other => Assert.Equal(Reads / Nodes.Length, metered.Calls(other)));
        }
    }

    private static LedgerMetadata Open(SequencerInstanceId sequencer) =>
        new(Id, KeyRange.Root, [.. Nodes], Window, new PrimaryClaim(Nodes[0], sequencer), LedgerStatus.Open.Instance);

    private static LedgerMetadata Closed(LedgerMetadata ledger, Slot end) => ledger.Close(end);

    private static LedgerMapValue Map(ulong epoch, params LedgerMetadata[] ledgers) => new(epoch, new LedgerId(Id.Value + 1), 0, [.. ledgers]);

    /// <summary>
    /// Opens the ledger in the map, creates its writer through <paramref name="process"/>'s <see cref="Ledger"/>, and
    /// proposes one batch per payload, each durable.
    /// </summary>
    private static async Task<LedgerMetadata> ProposeAsync(LedgerReader process, TestLedgerMap map, string[] payloads, CancellationToken cancellationToken)
    {
        var lifetime = new Sequencer(Window);
        var open = Open(lifetime.InstanceId);
        var lease = process.LeaseLedger(open);
        var writer = lease.CreateWriter(lifetime, map.View, WriterTestData.Host(), new FakeTimeProvider());
        await using (writer)
        {
            await map.PublishAsync(Map(epoch: 1, open), cancellationToken);
            for (var index = 0; index < payloads.Length; index++)
            {
                using var payload = WriterTestData.CreatePayload(System.Text.Encoding.UTF8.GetBytes(payloads[index]));
                using var row = new Row([0x10], RowKind.Append, payload);
                Assert.Equal(new ProposalOutcome(new ProposalOutcome.Durable(new Slot(index))), await writer.ProposeAsync([row], cancellationToken));
            }

            lease.Dispose();
            return open;
        }
    }

    /// <summary>Scans the slots and returns each batch's one payload as text, releasing each batch.</summary>
    private static async Task<string[]> ScanAsync(LedgerReader reader, LedgerMetadata ledger, Slot from, Slot below, CancellationToken cancellationToken)
    {
        var payloads = new List<string>();
        await foreach (var item in reader.ScanAsync(ledger, from, below, cancellationToken))
        {
            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
            using (data)
            {
                payloads.Add(Payload(data));
            }
        }

        return [.. payloads];
    }

    /// <summary>Reads one slot and returns its batch's one payload as text, releasing the batch.</summary>
    private static async Task<string> ReadAsync(LedgerReader reader, LedgerMetadata ledger, Slot slot, CancellationToken cancellationToken)
    {
        Assert.True((await reader.ReadAsync(ledger, slot, cancellationToken)).TryGetValue(out LedgerRead.Data data));
        using (data)
        {
            return Payload(data);
        }
    }

    private static string Payload(LedgerRead.Data data)
    {
        foreach (var row in data.View)
        {
            return System.Text.Encoding.UTF8.GetString(row.Body.ToArray());
        }

        throw new Xunit.Sdk.XunitException("A batch holds a row.");
    }

    /// <summary>
    /// ledgers.md §2.5: the reader's disposal stops the reads of every ledger it holds, a leased one among them, so a
    /// seal check or a read of a decided slot whose replicas answer only once cancelled, its caller's token one which
    /// is never cancelled, fails with <see cref="ObjectDisposedException"/> at once, and the disposal completes; also
    /// when the reader's node holds a replica, which a decided read asks first (ledger-protocol.md §8.3).
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposalStopsALeasedReadHeldAtItsReplicasAndCompletes(bool decided, bool local)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var open = await ProposeAsync(primary, map, ["a", "b"], cancellationToken);
        var held = new HeldReadsTransport();
        var reader = new LedgerReader(map.View, held, new FakeTimeProvider(), localNode: local ? Nodes[1] : null);
        Task read = decided
            ? reader.ReadAsync(Closed(open, new Slot(2)), Slot.Zero, CancellationToken.None).AsTask()
            : reader.ReadTailAsync(open, CancellationToken.None).AsTask();
        await held.Reached.WaitAsync(cancellationToken);

        await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, cancellationToken);

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => read);
    }

    /// <summary>
    /// ledgers.md §2.5: a read past the known contiguous tail re-reads the map before it answers; when the reader's
    /// disposal cancels that re-read, no map read begun after the call says whether the ledger was revoked, so the read
    /// answers <see cref="ObjectDisposedException"/>, never that the slot is undecided.
    /// </summary>
    [Fact]
    public async Task AReadPastTheTailWhoseMapReReadTheDisposalCancelsAnswersDisposed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var primary = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        var open = await ProposeAsync(primary, map, ["a"], cancellationToken);
        var reader = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        map.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = reader.ReadAsync(open, new Slot(5), CancellationToken.None).AsTask();
        Assert.False(read.IsCompleted);

        await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, cancellationToken);

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => read);
    }

    private static async Task AssertRefusedAsync(Func<Task> scan) => _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(scan);

    /// <summary>A transport whose every replica answers reads with backpressure.</summary>
    private sealed class BackpressuredTransport : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            new DelegatingLedgerReplica { Read = static (_, _, _) => ValueTask.FromResult(new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance)) };

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ReplicaHeartbeatRead(ReplicaWriteOutcome.Backpressure.Instance));
    }

    /// <summary>A transport which counts the replicas it opens and their disposals.</summary>
    private sealed class CountingTransport(ILedgerReplicaTransport inner) : ILedgerReplicaTransport
    {
        private int _opened;
        private int _disposed;

        public int Opened => Volatile.Read(ref _opened);

        public int Disposed => Volatile.Read(ref _disposed);

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options)
        {
            var replica = inner.Open(node, ledger, options);
            _ = Interlocked.Increment(ref _opened);
            return new DelegatingLedgerReplica
            {
                Write = replica.WriteAsync,
                Read = replica.ReadAsync,
                ReadTail = replica.ReadTailAsync,
                Disposal = async () =>
                {
                    await replica.DisposeAsync();
                    _ = Interlocked.Increment(ref _disposed);
                },
            };
        }

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(node, ledgers, cancellationToken);
    }

    /// <summary>A transport which meters each node's replica reads: their calls and the bytes of the batches they return.</summary>
    private sealed class ReadMeteringTransport(ILedgerReplicaTransport inner) : ILedgerReplicaTransport
    {
        private readonly ConcurrentDictionary<NodeId, int> _calls = new();
        private readonly ConcurrentDictionary<NodeId, long> _bytes = new();

        public int Calls(NodeId node) => _calls.GetValueOrDefault(node);

        public long Bytes(NodeId node) => _bytes.GetValueOrDefault(node);

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options)
        {
            var replica = inner.Open(node, ledger, options);
            return new DelegatingLedgerReplica
            {
                Write = replica.WriteAsync,
                Read = async (slot, tail, cancellationToken) =>
                {
                    _ = _calls.AddOrUpdate(node, 1, static (_, calls) => calls + 1);
                    var read = await replica.ReadAsync(slot, tail, cancellationToken);
                    if (read.TryGetValue(out ArcBuffer batch))
                    {
                        _ = _bytes.AddOrUpdate(node, batch.Length, (_, bytes) => bytes + batch.Length);
                    }

                    return read;
                },
                ReadTail = replica.ReadTailAsync,
                Disposal = replica.DisposeAsync,
            };
        }

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(node, ledgers, cancellationToken);
    }

    /// <summary>A transport whose every replica holds its reads and seal checks until their token is cancelled.</summary>
    private sealed class HeldReadsTransport : ILedgerReplicaTransport
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task which completes once a read or a seal check reached a replica.</summary>
        public Task Reached => _reached.Task;

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => new DelegatingLedgerReplica
        {
            Read = async (_, _, cancellationToken) =>
            {
                await HoldAsync(cancellationToken);
                return ReplicaAnswers.Missing;
            },
            ReadTail = async (_, _, cancellationToken) =>
            {
                await HoldAsync(cancellationToken);
                return new LedgerReplicaTail(Slot.Zero, IsSealed: false);
            },
        };

        public async ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default)
        {
            await HoldAsync(cancellationToken);
            return new ReplicaHeartbeatRead(new ReplicaHeartbeatReply(TimeSpan.Zero, new ReplicaHeartbeatStatus[ledgers.Length]));
        }

        private async Task HoldAsync(CancellationToken cancellationToken)
        {
            _ = _reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
        }
    }

    /// <summary>A transport whose replicas a test must not reach.</summary>
    private sealed class UnreachableTransport : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            throw new Xunit.Sdk.XunitException("The reader opened a replica.");

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("The reader sent a heartbeat.");
    }
}

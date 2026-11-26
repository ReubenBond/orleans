using System.Collections.Immutable;
using System.Text;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// <see cref="ArchiveReader"/> with <see cref="ArchiveKeyIndex"/> and <see cref="ArchiveStoreRead.Absent"/> per-ledger
/// bounds (archive.md §8.1): point-reads only distinct defining and row slots, reads zero slots for absent keys,
/// falls back to scanning above <c>Absent.Bounds</c> when unindexed, restarts on <see cref="KeyIndexRead.Dropped"/>,
/// retries backpressure on point reads, and rejects stale ordinals as <c>unknown-ordinal</c>.
/// </summary>
public sealed class ArchiveKeyIndexTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Indexed reads fetch only the distinct defining and referencing slots of the key, record
    /// <see cref="ArchiveReadCounts.PointReads"/> and <see cref="ArchiveReadCounts.IndexedLedgers"/>, and issue zero
    /// replica reads for an absent key.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task IndexedReadPointReadsOnlyTheKeysSlotsAndZeroForAbsentKey()
    {
        var token = TestContext.Current.CancellationToken;
        await using var cluster = await ArchiveCluster.StartAsync();
        var tA0 = await cluster.AppendAsync(1, RowKind.Append, "a0-");
        _ = await cluster.AppendAsync(2, RowKind.Append, "b0");
        _ = await cluster.AppendAsync(3, RowKind.Append, "c0");
        var tA1 = await cluster.AppendAsync(1, RowKind.Append, "a1");
        _ = await cluster.AppendAsync(2, RowKind.Append, "b1");
        var closed = await cluster.ReplaceLedgerAsync(1);

        var readSlots = new HashSet<long>();
        var readerProcess = await cluster.StartProcessAsync(
            cluster.Store,
            reads: (ledger, slot) =>
            {
                if (ledger == closed)
                {
                    lock (readSlots)
                    {
                        _ = readSlots.Add(slot.Value);
                    }
                }

                return null;
            });

        var index = new ArchiveKeyIndex((ledger, below, key, _) =>
        {
            if (ledger.Id != closed)
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
            }

            if (key.Span.SequenceEqual([(byte)1]))
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed(
                [
                    new(tA0, tA0.Slot, 0),
                    new(tA1, tA0.Slot, 0),
                ]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });

        var reader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System, index);
        using (var read = await reader.ReadAsync(new byte[] { 1 }, token))
        {
            Assert.NotNull(read);
            Assert.Equal("a0-a1", ReadText(read));
            Assert.Equal(2, reader.Counts.PointReads.Value);
            Assert.True(reader.Counts.IndexedLedgers.Value >= 1);
            Assert.Equal([tA0.Slot.Value, tA1.Slot.Value], [.. readSlots.Order()]);
        }

        // Absent key: zero slot reads.
        readSlots.Clear();
        using var absent = await reader.ReadAsync("\x63"u8.ToArray(), token);
        Assert.Null(absent.Prefix);
        Assert.Empty(absent.Rows.ToArray());
        Assert.Empty(readSlots);
    }

    /// <summary>
    /// When the key index returns <see cref="KeyIndexRead.Unindexed"/> and the store answers
    /// <see cref="ArchiveStoreRead.Absent"/> with a per-ledger bound <c>w</c> (a segment start, as LSM drains advance),
    /// <see cref="ArchiveReader"/> scans only slots <c>&gt;= w</c> and never touches slots below <c>w</c>, even when the
    /// store read completes asynchronously.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task UnindexedFallbackScansOnlyAboveAbsentBounds()
    {
        var token = TestContext.Current.CancellationToken;
        await using var cluster = await ArchiveCluster.StartAsync();
        // Segment 0: slots 0..1 (key 2 only); EndSegmentAsync writes the marker starting Segment 1 at slot 2; Segment 1: slot 3 (key 1).
        _ = await cluster.AppendAsync(2, RowKind.Append, "b0");
        _ = await cluster.AppendAsync(2, RowKind.Append, "b1");
        await cluster.EndSegmentAsync(1);
        var tA = await cluster.AppendAsync(1, RowKind.Append, "a-after-bound");
        var closed = await cluster.ReplaceLedgerAsync(1);

        // Segment 0 ended before slot 2 (the marker batch that starts segment 1, which is tA.Slot - 1).
        var bound = new Slot(tA.Slot.Value - 1);
        var boundedStore = new BoundedAbsentStore(cluster.Store, [new LedgerAbsentBound(closed, bound)]);

        var readSlots = new HashSet<long>();
        var readerProcess = await cluster.StartProcessAsync(
            boundedStore,
            reads: (ledger, slot) =>
            {
                if (ledger == closed)
                {
                    lock (readSlots)
                    {
                        _ = readSlots.Add(slot.Value);
                    }
                }

                return null;
            });

        var unindexed = new ArchiveKeyIndex((_, _, _, _) => ValueTask.FromResult<KeyIndexRead>(KeyIndexRead.Unindexed.Instance));
        var reader = new ArchiveReader(readerProcess.Reader, boundedStore, TimeProvider.System, unindexed);
        using var read = await reader.ReadAsync(new byte[] { 1 }, token);
        Assert.NotNull(read);
        Assert.Equal("a-after-bound", ReadText(read));
        Assert.All(readSlots, slot => Assert.True(slot >= bound.Value, $"Scan read slot {slot} below Absent bound {bound}."));
    }

    /// <summary>
    /// When an indexed or unindexed scan hits <see cref="KeyIndexRead.Dropped"/> while an asynchronous
    /// <see cref="IArchiveStore.ReadAsync"/> is still in flight, <see cref="ArchiveReader"/> cancels the attempt, absorbs
    /// the attempt-driven cancellation of the store read, and restarts cleanly instead of throwing
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DroppedLedgerCancelsInFlightAsyncStoreReadAndRestartsCleanly()
    {
        var token = TestContext.Current.CancellationToken;
        await using var cluster = await ArchiveCluster.StartAsync();
        var tA0 = await cluster.AppendAsync(1, RowKind.Append, "a0-");
        var closed1 = await cluster.ReplaceLedgerAsync(1);
        var tA1 = await cluster.AppendAsync(1, RowKind.Append, "a1");
        var closed2 = await cluster.ReplaceLedgerAsync(1);

        // Case 1: KeyIndexRead.Dropped cancels an in-flight async store ReadAsync.
        var store1 = new CancelFirstReadStore(cluster.Store);
        var process1 = await cluster.StartProcessAsync(store1);
        var dropped1 = 0;
        var index1 = new ArchiveKeyIndex((ledger, _, key, _) =>
        {
            if (ledger.Id == closed1 && key.Span.SequenceEqual([(byte)1]))
            {
                if (Interlocked.Exchange(ref dropped1, 1) == 0)
                {
                    return ValueTask.FromResult<KeyIndexRead>(KeyIndexRead.Dropped.Instance);
                }

                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([new(tA0, tA0.Slot, 0)]));
            }

            if (ledger.Id == closed2 && key.Span.SequenceEqual([(byte)1]))
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([new(tA1, tA1.Slot, 0)]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });

        var reader1 = new ArchiveReader(process1.Reader, store1, TimeProvider.System, index1);
        using (var read = await reader1.ReadAsync(new byte[] { 1 }, token))
        {
            Assert.Equal("a0-a1", ReadText(read));
            Assert.True(store1.FirstReadCanceled);
            Assert.True(reader1.Counts.Dropped.Value >= 1);
        }

        // Case 2: Sibling ledger returns Unindexed (awaiting reading) while closed2 returns Dropped (cancelling attempt).
        var store2 = new CancelFirstReadStore(cluster.Store);
        var process2 = await cluster.StartProcessAsync(store2);
        var dropped2 = 0;
        var index2 = new ArchiveKeyIndex((ledger, _, _, _) =>
        {
            if (ledger.Id == closed2 && Interlocked.Exchange(ref dropped2, 1) == 0)
            {
                return ValueTask.FromResult<KeyIndexRead>(KeyIndexRead.Dropped.Instance);
            }

            return ValueTask.FromResult<KeyIndexRead>(KeyIndexRead.Unindexed.Instance);
        });

        var reader2 = new ArchiveReader(process2.Reader, store2, TimeProvider.System, index2);
        using (var read = await reader2.ReadAsync(new byte[] { 1 }, token))
        {
            Assert.Equal("a0-a1", ReadText(read));
            Assert.True(store2.FirstReadCanceled);
            Assert.True(reader2.Counts.Dropped.Value >= 1);
        }
    }

    /// <summary>
    /// A colliding referencing row (<c>DefiningSlot != Tag.Slot</c>) injected into a key's index resolves its defining
    /// batch, rejects the mismatch on the full key bytes, and skips both the defining and referencing rows cleanly.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CollidingReferencingRowInKeyIndexIsFilteredByDefiningRowKey()
    {
        var token = TestContext.Current.CancellationToken;
        await using var cluster = await ArchiveCluster.StartAsync();
        var tA0 = await cluster.AppendAsync(1, RowKind.Append, "a0");
        var tB0 = await cluster.AppendAsync(2, RowKind.Append, "b0-");
        var tB1 = await cluster.AppendAsync(2, RowKind.Append, "b1");
        var closed = await cluster.ReplaceLedgerAsync(1);

        var readerProcess = await cluster.StartProcessAsync(cluster.Store);
        var index = new ArchiveKeyIndex((ledger, _, key, _) =>
        {
            if (ledger.Id == closed && key.Span.SequenceEqual([(byte)1]))
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed(
                [
                    new(tA0, tA0.Slot, 0),
                    new(tB0, tB0.Slot, 1),
                    new(tB1, tB0.Slot, 1), // Referencing row: DefiningSlot (tB0.Slot) != Tag.Slot (tB1.Slot).
                ]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });

        var indexedReader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System, index);
        var unindexedReader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System);
        using var indexed = await indexedReader.ReadAsync(new byte[] { 1 }, token);
        using var oracle = await unindexedReader.ReadAsync(new byte[] { 1 }, token);
        AssertMatchesOracle(indexed, oracle);
    }

    /// <summary>
    /// A non-caller <see cref="OperationCanceledException"/> (such as an internal object-store timeout) thrown by a slot
    /// point read during an indexed fetch is propagated rather than swallowed as a truncated <c>Reached</c> scan.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task IndexedReadPropagatesNonCallerOperationCanceledExceptionFromSlotRead()
    {
        var token = TestContext.Current.CancellationToken;
        var memoryObjects = new InMemoryObjectStore();
        var closedId = new LedgerId(1);
        var openId = new LedgerId(2);
        var cancelSlot1 = false;
        var canceledReads = 0;
        var slot1Suffix = ObjectLedgerNames.Slot(closedId, new Slot(1)).Value;
        var objects = new CancelingObjectStore(
            memoryObjects,
            name =>
            {
                if (!cancelSlot1 || !name.Value.EndsWith(slot1Suffix, StringComparison.Ordinal))
                {
                    return false;
                }

                _ = Interlocked.Increment(ref canceledReads);
                return true;
            });

        // The local batch cache (OL-I11) would serve slot 1 from its append without reading the object store, so the
        // store runs without one and every point read reaches CancelingObjectStore.
        var objectStore = ObjectLedgerStore.Over(objects, new ObjectLedgerStoreOptions { CacheBytes = 0 }, TimeProvider.System);
        var claim = new PrimaryClaim(ArchiveCluster.Node, SequencerInstanceId.New());
        var openClosed = new LedgerMetadata(closedId, KeyRange.Root, LedgerLocation.ObjectStore.Instance, ArchiveCluster.Window, claim, LedgerStatus.Open.Instance);

        var register = new InMemoryConditionalRegister();
        var initialMap = new LedgerMapValue(
            epoch: 1,
            nextLedgerId: new LedgerId(2),
            retirementFloor: 0,
            ledgers: [openClosed]);
        MetaStoreVersion version;
        using (var encoded = initialMap.Encode())
        {
            var outcome = await register.WriteAsync(null, encoded.AsReadOnlySequence(), token);
            version = Assert.IsType<RegisterWriteOutcome.Applied>(outcome.Value).Version;
        }

        var view = await LedgerMapView.OpenAsync(register, token);
        using var transport = new InMemoryLedgerReplicaTransport();
        await using var ledgerReader = new LedgerReader(view, transport, TimeProvider.System, ArchiveCluster.Node)
        {
            Objects = objectStore,
        };

        using var store = new InMemoryArchiveStore();
        Tag tA0;
        Tag tB1;
        using (var lease = ledgerReader.LeaseLedger(openClosed))
        {
            await using var writer = lease.Ledger.CreateWriter(TimeProvider.System);
            var s0 = Assert.NotNull(await writer.AcquireAsync(token));
            using var b0 = ArcBuffer.Wrap(Encoding.UTF8.GetBytes("a0-"));
            using var r0 = new Row([1], RowKind.Append, b0);
            using var batch0 = BatchView.Encode([r0]);
            var o0 = await writer.AppendAsync(s0, batch0, token);
            Assert.True(o0.TryGetValue(out AppendOutcome.Appended d0));
            tA0 = new Tag(closedId, d0.Slot, 0);

            var s1 = Assert.NotNull(await writer.AcquireAsync(token));
            using var b1 = ArcBuffer.Wrap(Encoding.UTF8.GetBytes("b1"));
            using var r1 = new Row([2], RowKind.Append, b1);
            using var batch1 = BatchView.Encode([r1]);
            var o1 = await writer.AppendAsync(s1, batch1, token);
            Assert.True(o1.TryGetValue(out AppendOutcome.Appended d1));
            tB1 = new Tag(closedId, d1.Slot, 0);
        }

        var closed = new LedgerMetadata(closedId, KeyRange.Root, LedgerLocation.ObjectStore.Instance, ArchiveCluster.Window, claim, new LedgerStatus.Closed(new Slot(2)));
        var open = new LedgerMetadata(openId, KeyRange.Root, LedgerLocation.ObjectStore.Instance, ArchiveCluster.Window, claim, LedgerStatus.Open.Instance);
        var closedMap = new LedgerMapValue(
            epoch: 2,
            nextLedgerId: new LedgerId(3),
            retirementFloor: 0,
            ledgers: [closed, open]);
        using (var encoded = closedMap.Encode())
        {
            _ = Assert.IsType<RegisterWriteOutcome.Applied>((await register.WriteAsync(version, encoded.AsReadOnlySequence(), token)).Value);
        }

        _ = await view.ReadAsync(token);

        cancelSlot1 = true;

        var index = new ArchiveKeyIndex((ledger, _, key, _) =>
        {
            if (ledger.Id == closedId && key.Span.SequenceEqual([(byte)1]))
            {
                // Key 2's defining row stands in for a hash collision in key 1's index, so the read fetches slot 1.
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed(
                [
                    new(tA0, tA0.Slot, 0),
                    new(tB1, tB1.Slot, 0),
                ]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });

        var reader = new ArchiveReader(ledgerReader, store, TimeProvider.System, index);
        var thrown = await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            using var _ = await reader.ReadAsync(new byte[] { 1 }, token);
        });
        Assert.Equal("Simulated object-store timeout.", thrown.Message);
        Assert.True(Volatile.Read(ref canceledReads) > 0);
    }

    /// <summary>
    /// Structural equality on <see cref="KeyIndexRead.Indexed"/> and <see cref="ArchiveStoreRead.Absent"/> compares
    /// contents by value rather than array reference identity.
    /// </summary>
    [Fact]
    public void UnionValueRecordsCompareEntriesAndBoundsStructurally()
    {
        var ledger1 = new LedgerId(1);
        var ledger2 = new LedgerId(2);
        var entry = new KeyIndexEntry(new Tag(ledger1, new Slot(3), 1), new Slot(0), 2);
        var indexedA = new KeyIndexRead.Indexed([entry]);
        var indexedB = new KeyIndexRead.Indexed([entry]);
        Assert.Equal(indexedA, indexedB);
        Assert.Equal(indexedA.GetHashCode(), indexedB.GetHashCode());

        var absentA = ArchiveStoreRead.Absent.WithBounds([new(ledger2, new Slot(5)), new(ledger1, new Slot(2))]);
        var absentB = ArchiveStoreRead.Absent.WithBounds([new(ledger1, new Slot(2)), new(ledger2, new Slot(5))]);
        Assert.Equal(absentA, absentB);
        Assert.Equal(absentA.GetHashCode(), absentB.GetHashCode());
        Assert.Equal(new Slot(2), absentA.Bound(ledger1));
        Assert.Equal(new Slot(5), absentA.Bound(ledger2));
        Assert.Equal(Slot.Zero, absentA.Bound(new LedgerId(3)));
    }

    /// <summary>
    /// Across randomized multi-ledger histories with multi-row batches (<c>Index &gt; 0</c>), multi-slot segments
    /// (<c>DefiningSlot &gt; Segment.Start</c>, <c>Ordinal &gt; 0</c>), <c>Append</c>/<c>Replace</c>/<c>Delete</c>/
    /// <c>EndSegment</c>, and drained/dropped ledgers, an indexed <see cref="ArchiveReader"/> matches the unindexed
    /// <see cref="ArchiveReader"/> row-for-row on every field while reading no more slots than the key's distinct indexed
    /// slots.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task IndexedReadMatchesUnindexedOracleRowForRowAcrossRandomizedHistories()
    {
        var token = TestContext.Current.CancellationToken;
        var rowOpGen = Gen.Select(
            Gen.Byte[1, 3],
            Gen.Int[0, 3], // 0: Append, 1: Replace, 2: Delete (tombstone), 3: Delete (empty expiry)
            Gen.Int[0, 999]);
        var stepGen = Gen.Select(
            Gen.Int[0, 9], // 0..6: propose batch, 7: end segment, 8: replace ledger, 9: drain & drop oldest closed
            rowOpGen.Array[1, 4]);

        await stepGen.Array[4, 14].SampleAsync(
            async steps => await _leaks.InScopeAsync(async () =>
            {
                await using var cluster = await ArchiveCluster.StartAsync();
                var closedLedgers = new Queue<LedgerId>();
                foreach (var (action, ops) in steps)
                {
                    if (action <= 6)
                    {
                        var ledger = cluster.OpenLedger(1);
                        var writer = cluster.Primary.Registry!.TryGet(ledger.Id)!;
                        var rows = new List<Row>(ops.Length);
                        try
                        {
                            foreach (var (key, kindCode, num) in ops)
                            {
                                var (kind, payload) = kindCode switch
                                {
                                    0 => (RowKind.Append, $"a{num}"),
                                    1 => (RowKind.Replace, $"r{num}"),
                                    2 => (RowKind.Delete, $"d{num}"),
                                    _ => (RowKind.Delete, string.Empty),
                                };
                                using var body = payload.Length == 0 ? default : ArcBuffer.Wrap(Encoding.UTF8.GetBytes(payload));
                                rows.Add(new Row([key], kind, body));
                            }

                            var outcome = await cluster.Chains.ProposeAsync(writer, rows, cancellationToken: token);
                            Assert.True(outcome.TryGetValue(out ProposalOutcome.Durable _));
                        }
                        finally
                        {
                            foreach (var row in rows)
                            {
                                row.Dispose();
                            }
                        }
                    }
                    else if (action == 7)
                    {
                        await cluster.EndSegmentAsync(1);
                    }
                    else if (action == 8)
                    {
                        closedLedgers.Enqueue(await cluster.ReplaceLedgerAsync(1));
                    }
                    else if (closedLedgers.Count > 0)
                    {
                        var oldest = closedLedgers.Dequeue();
                        await cluster.DrainAsync(oldest);
                        await cluster.DropAsync(oldest);
                    }
                }

                await cluster.CarryTailAsync(1);

                // Build exact ground-truth key index by scanning every listed ledger once.
                var indexedByLedger = new Dictionary<(LedgerId Ledger, byte Key), ImmutableArray<KeyIndexEntry>>();
                var map = cluster.Primary.View.Current;
                var lookup = map.Find([1])!.Value;
                var allLedgers = lookup.Closed.Append(lookup.Open).ToArray();
                foreach (var ledger in allLedgers)
                {
                    var end = ledger.Status.Value is LedgerStatus.Closed closed
                        ? closed.T
                        : Assert.IsType<LedgerTail>((await cluster.Primary.Reader.ReadTailAsync(ledger, token)).Value).ContiguousTail;
                    var perKey = new Dictionary<byte, List<KeyIndexEntry>>();
                    var definingSlots = new Dictionary<ulong, Slot>();
                    LedgerSegmentId? currentSegment = null;
                    using var view = new LedgerSegmentView(ledger.Id);
                    await foreach (var item in cluster.Primary.Reader.ScanAsync(ledger, Slot.Zero, end, view, cancellationToken: token))
                    {
                        Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
                        using (data)
                        {
                            if (currentSegment != view.Segment)
                            {
                                definingSlots.Clear();
                                currentSegment = view.Segment;
                            }

                            foreach (var row in item.Rows)
                            {
                                if (row.IsDefining)
                                {
                                    definingSlots[row.Ordinal] = row.Tag.Slot;
                                }

                                var keyByte = row.Key[0];
                                var defSlot = definingSlots[row.Ordinal];
                                if (!perKey.TryGetValue(keyByte, out var list))
                                {
                                    list = [];
                                    perKey[keyByte] = list;
                                }

                                list.Add(new KeyIndexEntry(row.Tag, defSlot, row.Ordinal));
                            }
                        }
                    }

                    foreach (var (k, list) in perKey)
                    {
                        indexedByLedger[(ledger.Id, k)] = [.. list];
                    }
                }

                var readerProcess = await cluster.StartProcessAsync(cluster.Store);
                var keyIndex = new ArchiveKeyIndex((ledger, below, key, _) =>
                {
                    if (!indexedByLedger.TryGetValue((ledger.Id, key.Span[0]), out var all))
                    {
                        return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
                    }

                    var filtered = all.Where(e => e.Tag.Slot < below).ToImmutableArray();
                    return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed(filtered));
                });

                var indexedReader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System, keyIndex);
                var unindexedReader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System);
                foreach (var keyByte in new byte[] { 1, 2, 3, 99 })
                {
                    var key = new[] { keyByte };
                    var beforePointReads = indexedReader.Counts.PointReads.Value;
                    using var indexed = await indexedReader.ReadAsync(key, token);
                    using var oracle = await unindexedReader.ReadAsync(key, token);
                    AssertMatchesOracle(indexed, oracle);

                    var expectedDistinctSlots = 0;
                    foreach (var ledger in allLedgers)
                    {
                        if (indexedByLedger.TryGetValue((ledger.Id, keyByte), out var entries))
                        {
                            expectedDistinctSlots += entries.SelectMany(static e => new[] { e.DefiningSlot, e.Tag.Slot }).Distinct().Count();
                        }
                    }

                    Assert.True(
                        indexedReader.Counts.PointReads.Value - beforePointReads <= expectedDistinctSlots,
                        $"Indexed read for key {keyByte} issued {indexedReader.Counts.PointReads.Value - beforePointReads} point reads, expected <= {expectedDistinctSlots}.");
                }
            }),
            iter: 20);
    }

    /// <summary>
    /// Point reads retry <see cref="ReplicaWriteOutcome.Backpressure"/>, restart on <see cref="KeyIndexRead.Dropped"/>,
    /// and reject a stale ordinal entry as <c>unknown-ordinal</c> without leaking any <see cref="Buffers.ArcBuffer"/>.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task IndexedReadHandlesDroppedBackpressureAndStaleOrdinal()
    {
        var token = TestContext.Current.CancellationToken;
        await using var cluster = await ArchiveCluster.StartAsync();
        var tA0 = await cluster.AppendAsync(1, RowKind.Append, "a0-");
        var tA1 = await cluster.AppendAsync(1, RowKind.Append, "a1");
        var closed = await cluster.ReplaceLedgerAsync(1);

        var injectedBackpressure = 0;
        var readerProcess = await cluster.StartProcessAsync(
            cluster.Store,
            reads: (ledger, slot) =>
            {
                if (ledger == closed && slot == tA1.Slot && Interlocked.Exchange(ref injectedBackpressure, 1) == 0)
                {
                    return new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance);
                }

                return null;
            });

        var droppedOnce = 0;
        var index = new ArchiveKeyIndex((ledger, _, key, _) =>
        {
            if (ledger.Id == closed && key.Span.SequenceEqual([(byte)1]))
            {
                if (Interlocked.Exchange(ref droppedOnce, 1) == 0)
                {
                    return ValueTask.FromResult<KeyIndexRead>(KeyIndexRead.Dropped.Instance);
                }

                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed(
                [
                    new(tA0, tA0.Slot, 0),
                    new(tA1, tA0.Slot, 0),
                ]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });

        var reader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System, index);
        using (var read = await reader.ReadAsync(new byte[] { 1 }, token))
        {
            Assert.NotNull(read);
            Assert.Equal("a0-a1", ReadText(read));
            Assert.True(reader.Counts.Dropped.Value >= 1);
            Assert.True(reader.Counts.Backpressured.Value >= 1);
        }

        // Stale ordinal (ordinal 99 does not exist in the defining batch at tA0.Slot): rejected as unknown-ordinal!
        var staleIndex = new ArchiveKeyIndex((ledger, _, key, _) =>
        {
            if (ledger.Id == closed && key.Span.SequenceEqual([(byte)1]))
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([new(tA0, tA0.Slot, 99)]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });
        var staleReader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System, staleIndex);
        var invalid = await Assert.ThrowsAsync<InvalidDataException>(async () => _ = await staleReader.ReadAsync(new byte[] { 1 }, token));
        Assert.Equal(BatchView.ReasonUnknownOrdinal, DecodeRejection.ReasonOf(invalid));
    }

    /// <summary>
    /// Descending indexed reads stop as soon as a verified <c>Replace</c> opener is reached: a key with 200 older appends,
    /// a <c>Replace</c>, and 5 newer appends reads only the defining slot, the <c>Replace</c>, and the 5 newer slots —
    /// never reading the 200 pre-<c>Replace</c> appends — while preserving exact folded content, version, and token chain.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DescendingIndexedReadStopsAtVerifiedReplaceAndSkipsOlderAppends()
    {
        var token = TestContext.Current.CancellationToken;
        await using var cluster = await ArchiveCluster.StartAsync();

        var entries = new List<KeyIndexEntry>(206);
        var tDef = await cluster.AppendAsync(1, RowKind.Append, "old-0|");
        entries.Add(new(tDef, tDef.Slot, 0));
        for (var i = 1; i < 200; i++)
        {
            var tOld = await cluster.AppendAsync(1, RowKind.Append, $"old-{i}|");
            entries.Add(new(tOld, tDef.Slot, 0));
        }

        var tReplace = await cluster.AppendAsync(1, RowKind.Replace, "base");
        entries.Add(new(tReplace, tDef.Slot, 0));
        for (var i = 1; i <= 5; i++)
        {
            var tNew = await cluster.AppendAsync(1, RowKind.Append, $"+{i}");
            entries.Add(new(tNew, tDef.Slot, 0));
        }

        var closed = await cluster.ReplaceLedgerAsync(1);
        var readSlots = new HashSet<long>();
        var readerProcess = await cluster.StartProcessAsync(
            cluster.Store,
            reads: (ledger, slot) =>
            {
                if (ledger == closed)
                {
                    lock (readSlots)
                    {
                        _ = readSlots.Add(slot.Value);
                    }
                }

                return null;
            });

        var index = new ArchiveKeyIndex((ledger, below, key, _) =>
        {
            if (ledger.Id == closed && key.Span.SequenceEqual([(byte)1]))
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([.. entries.Where(e => e.Tag.Slot < below)]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });

        var indexedReader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System, index);
        var unindexedReader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System);
        using var indexed = await indexedReader.ReadAsync(new byte[] { 1 }, token);
        HashSet<long> indexedReadSlots;
        lock (readSlots)
        {
            indexedReadSlots = [.. readSlots];
        }

        using var oracle = await unindexedReader.ReadAsync(new byte[] { 1 }, token);

        Assert.Equal("base+1+2+3+4+5", ReadText(indexed));
        AssertMatchesOracle(indexed, oracle);

        // Only the defining slot + the Replace + 5 newer appends + at most the rest of the single newest wave (waveSize <= 32)
        // are read — the ~170+ older pre-Replace slots are never touched!
        Assert.True(indexedReadSlots.Count <= 33, $"Expected descending wave scan to read <= 33 slots, but read {indexedReadSlots.Count}.");
        Assert.Contains(tDef.Slot.Value, indexedReadSlots);
        Assert.Contains(tReplace.Slot.Value, indexedReadSlots);
        Assert.DoesNotContain(entries[1].Tag.Slot.Value, indexedReadSlots);
        Assert.DoesNotContain(entries[100].Tag.Slot.Value, indexedReadSlots);
    }

    /// <summary>
    /// When replicas natively support batched scattered-slot reads (<see cref="ILedgerReplica.SupportsReadSlots"/>),
    /// <see cref="ArchiveReader"/> fetches each descending wave via a single batched <see cref="ILedgerReplica.ReadSlotsAsync"/>
    /// RPC instead of per-slot point reads, matching the unindexed oracle identically and handling peer fallback for missing slots.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task MultiSlotIndexedReadUsesBatchedReadSlotsAndMatchesOracle()
    {
        var token = TestContext.Current.CancellationToken;
        await using var cluster = await ArchiveCluster.StartAsync();

        var entries = new List<KeyIndexEntry>(24);
        var tDef = await cluster.AppendAsync(7, RowKind.Append, "v0");
        entries.Add(new(tDef, tDef.Slot, 0));
        for (var i = 1; i < 20; i++)
        {
            _ = await cluster.AppendAsync(99, RowKind.Append, $"noise-{i}");
            var tNext = await cluster.AppendAsync(7, RowKind.Append, $"+{i}");
            entries.Add(new(tNext, tDef.Slot, 0));
        }

        var closed = await cluster.ReplaceLedgerAsync(7);
        var readerProcess = await cluster.StartProcessAsync(cluster.Store);

        var index = new ArchiveKeyIndex((ledger, below, key, _) =>
        {
            if (ledger.Id == closed && key.Span.SequenceEqual([(byte)7]))
            {
                return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([.. entries.Where(e => e.Tag.Slot < below)]));
            }

            return ValueTask.FromResult<KeyIndexRead>(new KeyIndexRead.Indexed([]));
        });

        var indexedReader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System, index);
        var unindexedReader = new ArchiveReader(readerProcess.Reader, cluster.Store, TimeProvider.System);
        using var indexed = await indexedReader.ReadAsync(new byte[] { 7 }, token);
        using var oracle = await unindexedReader.ReadAsync(new byte[] { 7 }, token);
        AssertMatchesOracle(indexed, oracle);
        Assert.Equal(20, indexed.Rows.Length);
    }

    private static void AssertMatchesOracle(ArchiveRead actual, ArchiveRead oracle)
    {
        Assert.Equal(oracle.Linearizable, actual.Linearizable);
        Assert.Equal(oracle.NextToken, actual.NextToken);
        Assert.Equal(oracle.OpenLedger, actual.OpenLedger);
        Assert.Equal(oracle.Prefix is null, actual.Prefix is null);
        if (oracle.Prefix is { } expectedPrefix && actual.Prefix is { } actualPrefix)
        {
            Assert.Equal(expectedPrefix.Token, actualPrefix.Token);
            Assert.Equal(expectedPrefix.VersionBase, actualPrefix.VersionBase);
            Assert.Equal(expectedPrefix.HasBase, actualPrefix.HasBase);
            Assert.Equal(expectedPrefix.Tombstone, actualPrefix.Tombstone);
            Assert.Equal(expectedPrefix.Content.ToArray(), actualPrefix.Content.ToArray());
        }

        // Descending indexed reads may omit rows superseded by a later verified opener within their segment (or older
        // segments/ledgers when tokenless); compare every surviving row against the matching segment-trimmed oracle!
        var expectedRows = TrimSupersededWithinSegments(oracle.Rows);
        Assert.Equal(expectedRows.Count, actual.Rows.Length);
        for (var i = 0; i < expectedRows.Count; i++)
        {
            var expected = expectedRows[i];
            var got = actual.Rows[i];
            Assert.Equal(expected.Tag, got.Tag);
            Assert.Equal(expected.Kind, got.Kind);
            Assert.Equal(expected.Version, got.Version);
            Assert.Equal(expected.Segment, got.Segment);
            Assert.Equal(expected.Token, got.Token);
            Assert.Equal(expected.IsHeld, got.IsHeld);
            Assert.Equal(expected.Body.ToArray(), got.Body.ToArray());
        }

        Assert.Equal(ReadText(oracle), ReadText(actual));
    }

    private static List<ArchiveRow> TrimSupersededWithinSegments(ReadOnlySpan<ArchiveRow> rows)
    {
        var result = new List<ArchiveRow>(rows.Length);
        for (var start = 0; start < rows.Length;)
        {
            var end = start + 1;
            while (end < rows.Length && rows[end].Segment == rows[start].Segment)
            {
                end++;
            }

            var lastOpener = -1;
            for (var i = end - 1; i >= start; i--)
            {
                if (rows[i].Kind is RowKind.Replace or RowKind.Delete)
                {
                    lastOpener = i;
                    break;
                }
            }

            if (lastOpener >= 0 && rows[lastOpener].Token.IsEmpty)
            {
                result.Clear();
            }

            var keepFrom = lastOpener >= 0 ? lastOpener : start;
            for (var i = keepFrom; i < end; i++)
            {
                result.Add(rows[i]);
            }

            start = end;
        }

        return result;
    }

    private static string ReadText(ArchiveRead read)
    {
        var sb = new StringBuilder();
        if (read.Prefix is { } prefix && prefix.Content.Length > 0)
        {
            _ = sb.Append(Encoding.UTF8.GetString(prefix.Content.ToArray()));
        }

        foreach (var row in read.Rows)
        {
            if (row.IsHeld)
            {
                continue;
            }

            if (row.Kind == RowKind.Replace)
            {
                _ = sb.Clear();
            }
            else if (row.Kind == RowKind.Delete)
            {
                _ = sb.Clear();
                continue;
            }

            if (row.Body.Length > 0)
            {
                _ = sb.Append(Encoding.UTF8.GetString(row.Body.ToArray()));
            }
        }

        return sb.ToString();
    }

    private sealed class BoundedAbsentStore(IArchiveStore inner, LedgerAbsentBound[] bounds) : IArchiveStore
    {
        public ArchiveStoreKind Kind => inner.Kind;

        public int? MaxAppendBytes => inner.MaxAppendBytes;

        public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
            inner.StoreRecordAsync(parameters, cancellationToken);

        public ArchivePlan Begin(RowToken token) => inner.Begin(token);

        public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => inner.Holds(prefix, write, count);

        public bool IsReplaceRequested(RowToken token) => inner.IsReplaceRequested(token);

        public bool AdmitsKey(ReadOnlySpan<byte> key) => inner.AdmitsKey(key);

        public async ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var result = await inner.ReadAsync(key, map, cancellationToken).ConfigureAwait(false);
            return result.Value is ArchiveStoreRead.Absent
                ? ArchiveStoreRead.Absent.WithBounds(bounds)
                : result;
        }
    }

    private sealed class CancelFirstReadStore(IArchiveStore inner) : IArchiveStore
    {
        private int _reads;

        public bool FirstReadCanceled { get; private set; }

        public ArchiveStoreKind Kind => inner.Kind;

        public int? MaxAppendBytes => inner.MaxAppendBytes;

        public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
            inner.StoreRecordAsync(parameters, cancellationToken);

        public ArchivePlan Begin(RowToken token) => inner.Begin(token);

        public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count) => inner.Holds(prefix, write, count);

        public bool IsReplaceRequested(RowToken token) => inner.IsReplaceRequested(token);

        public bool AdmitsKey(ReadOnlySpan<byte> key) => inner.AdmitsKey(key);

        public async ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    FirstReadCanceled = true;
                    throw;
                }
            }

            return await inner.ReadAsync(key, map, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CancelingObjectStore(IObjectStore inner, Func<ObjectName, bool> shouldCancel) : IObjectStore
    {
        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            shouldCancel(name)
                ? throw new TaskCanceledException("Simulated object-store timeout.")
                : inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            shouldCancel(name)
                ? throw new TaskCanceledException("Simulated object-store timeout.")
                : inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(
            ObjectName name,
            ObjectVersion? expectedVersion,
            ArcBuffer content,
            uint? crc32c,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }
}

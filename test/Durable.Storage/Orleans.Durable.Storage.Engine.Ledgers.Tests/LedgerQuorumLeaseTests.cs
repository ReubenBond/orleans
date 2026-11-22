using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Ledgers.Tests.Transitions;
using Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Covers the L2 ledger-replica quorum lease (system-model.md §2, §4; ledger-replicas.md §2.1, §4, LR-I10;
/// ledger-protocol.md §8.4, LP-I17; ledgers.md §2.5, §2.6, §8.1, §8.4, LG-I10):
/// 0-RTT local reads on leased primaries, guard-band/expiry/seal/surrender fallback, <c>LeaseDuration = 0</c>
/// clock-free disabling, fallback singleflight coalescing, RAM-only <c>bootLeaseHorizon</c> with zero WAL writes,
/// batched peer <c>HeartbeatAsync</c> renewal, and <see cref="TransitionExecutor"/> lease-expiry waits vs 0 ms
/// cooperative handoff.
/// </summary>
public sealed class LedgerQuorumLeaseTests : IDisposable
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 4 };
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GuardBand = TimeSpan.FromMilliseconds(500);
    private const double ClockDriftFactor = 1.25;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LP-I17: while the primary holds a valid quorum lease (<c>now + guardBand &lt; localLeaseUntil</c>),
    /// <see cref="Ledger.ReadTailAsync"/> answers in 0 RTTs with zero replica RPCs; once time advances into the
    /// guard band or past expiry, it falls back to a quorum tail read. A non-owner (<c>ServedHere == false</c>)
    /// never uses a lease and always issues quorum tail reads.
    /// </summary>
    [Fact]
    public async Task PrimaryServesZeroRttLocalReadsWhileLeasedAndFallsBackAtGuardBandOrNonOwner()
    {
        var clock = new FakeTimeProvider();
        using var backing = new InMemoryLedgerReplicaTransport(clock, LeaseDuration);
        var transport = new CountingTransport(backing) { SuppressHeartbeats = true };
        var register = new InMemoryConditionalRegister();
        var map = await LedgerMapView.OpenAsync(register, Token);
        var leaseOptions = new LedgerLeaseOptions
        {
            LeaseDuration = LeaseDuration,
            ClockDriftFactor = ClockDriftFactor,
            LeaseGuardBand = GuardBand,
        };

        var nodes = TransitionCluster.Nodes;
        await using var ownerReader = new LedgerReader(map, transport, clock, nodes[0]) { LeaseOptions = leaseOptions };
        await using var otherReader = new LedgerReader(map, transport, clock, nodes[1]) { LeaseOptions = leaseOptions };
        await using var registry = new LedgerWriterRegistry(ownerReader, 1 << 20, LedgerWriterOptions.Default, clock, NullLoggerFactory.Instance, new Random(1));
        var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);
        await using var committer = new LedgerMapCommitter(register, 1 << 20);

        Assert.Equal(TransitionCluster.Applied(1), await executor.ExecuteAsync(new Transition.Bootstrap(TransitionCluster.Choice(nodes[0], 0)), committer.CommitAsync, Token));
        _ = await otherReader.Map.ReadAsync(Token);
        var metadata = map.Current.Ledgers.Single(static l => l.IsOpen);
        var writer = registry.TryGet(metadata.Id)!;

        // First write grants a quorum lease of D = 3s -> localLeaseUntil = t0 + 3s / 1.25 = t0 + 2.4s.
        Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(writer, 0x11, Token));
        Assert.Equal(0, transport.ReadTailCalls);

        // Owner reads tail in 0 RTTs while now + 500ms < 2400ms (up to 1899ms).
        using var ownerLease = ownerReader.LeaseLedger(metadata);
        var tail1 = Assert.IsType<LedgerTail>((await ownerLease.Ledger.ReadTailAsync(Token)).Value);
        Assert.Equal(new LedgerTail(new Slot(1), IsSealed: false, ServedHere: true), tail1);
        Assert.Equal(0, transport.ReadTailCalls);

        clock.Advance(TimeSpan.FromMilliseconds(1_899));
        // Snapshot after Advance because LedgerWriter's deferred WriteContiguousTail timer fires during Advance.
        var callsBeforeTail2 = transport.ReadTailCalls;
        var tail2 = Assert.IsType<LedgerTail>((await ownerLease.Ledger.ReadTailAsync(Token)).Value);
        Assert.Equal(new LedgerTail(new Slot(1), IsSealed: false, ServedHere: true), tail2);
        Assert.Equal(callsBeforeTail2, transport.ReadTailCalls);

        // Non-owner NEVER uses the local lease even while the primary's lease is valid.
        using var otherLease = otherReader.LeaseLedger(metadata);
        var nonOwnerTail = Assert.IsType<LedgerTail>((await otherLease.Ledger.ReadTailAsync(Token)).Value);
        Assert.Equal(new LedgerTail(new Slot(1), IsSealed: false, ServedHere: false), nonOwnerTail);
        Assert.True(transport.ReadTailCalls > 0);
        var callsAfterNonOwner = transport.ReadTailCalls;

        // Owner still 0-RTT at t = 1899ms.
        _ = await ownerLease.Ledger.ReadTailAsync(Token);
        Assert.Equal(callsAfterNonOwner, transport.ReadTailCalls);

        // Advance 1ms into the guard band (1900ms + 500ms >= 2400ms): owner MUST fall back to quorum ReadTailAsync.
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var fallbackTail = Assert.IsType<LedgerTail>((await ownerLease.Ledger.ReadTailAsync(Token)).Value);
        Assert.Equal(new LedgerTail(new Slot(1), IsSealed: false, ServedHere: true), fallbackTail);
        Assert.True(transport.ReadTailCalls > callsAfterNonOwner);
    }

    /// <summary>
    /// system-model.md §2, LP-I17: <c>LeaseDuration = TimeSpan.Zero</c> disables leases completely and reduces cleanly
    /// to clock-free quorum tail checks on every <see cref="Ledger.ReadTailAsync"/>.
    /// </summary>
    [Fact]
    public async Task ZeroLeaseDurationDisablesLeasesAndAlwaysChecksQuorum()
    {
        var clock = new FakeTimeProvider();
        using var backing = new InMemoryLedgerReplicaTransport(clock, TimeSpan.Zero);
        var transport = new CountingTransport(backing);
        var register = new InMemoryConditionalRegister();
        var map = await LedgerMapView.OpenAsync(register, Token);
        var nodes = TransitionCluster.Nodes;

        await using var reader = new LedgerReader(map, transport, clock, nodes[0])
        {
            LeaseOptions = new LedgerLeaseOptions { LeaseDuration = TimeSpan.Zero },
        };
        await using var registry = new LedgerWriterRegistry(reader, 1 << 20, LedgerWriterOptions.Default, clock, NullLoggerFactory.Instance, new Random(2));
        var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);
        await using var committer = new LedgerMapCommitter(register, 1 << 20);

        Assert.Equal(TransitionCluster.Applied(1), await executor.ExecuteAsync(new Transition.Bootstrap(TransitionCluster.Choice(nodes[0], 0)), committer.CommitAsync, Token));
        var metadata = map.Current.Ledgers.Single(static l => l.IsOpen);
        var writer = registry.TryGet(metadata.Id)!;
        Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(writer, 0x22, Token));

        using var lease = reader.LeaseLedger(metadata);
        Assert.Equal(0, transport.ReadTailCalls);
        _ = await lease.Ledger.ReadTailAsync(Token);
        Assert.True(transport.ReadTailCalls > 0);
        var afterFirst = transport.ReadTailCalls;
        _ = await lease.Ledger.ReadTailAsync(Token);
        Assert.True(transport.ReadTailCalls > afterFirst);
        Assert.Equal(0, transport.HeartbeatCalls);
    }

    /// <summary>
    /// OR-I24, LP-I17: <see cref="LedgerWriter.PrepareReleaseAsync"/> permanently surrenders the ledger's local lease
    /// before answering <c>Released</c>, and late write replies arriving after surrender cannot re-extend it; any
    /// subsequent <see cref="Ledger.ReadTailAsync"/> immediately falls back to quorum checking.
    /// </summary>
    [Fact]
    public async Task PrepareReleasePermanentlySurrendersLeaseAndBlocksLateReExtension()
    {
        var clock = new FakeTimeProvider();
        using var backing = new InMemoryLedgerReplicaTransport(clock, LeaseDuration);
        var transport = new CountingTransport(backing);
        var register = new InMemoryConditionalRegister();
        var map = await LedgerMapView.OpenAsync(register, Token);
        var nodes = TransitionCluster.Nodes;

        await using var reader = new LedgerReader(map, transport, clock, nodes[0])
        {
            LeaseOptions = new LedgerLeaseOptions { LeaseDuration = LeaseDuration, ClockDriftFactor = ClockDriftFactor, LeaseGuardBand = GuardBand },
        };
        await using var registry = new LedgerWriterRegistry(reader, 1 << 20, LedgerWriterOptions.Default, clock, NullLoggerFactory.Instance, new Random(3));
        var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);
        await using var committer = new LedgerMapCommitter(register, 1 << 20);

        Assert.Equal(TransitionCluster.Applied(1), await executor.ExecuteAsync(new Transition.Bootstrap(TransitionCluster.Choice(nodes[0], 0)), committer.CommitAsync, Token));
        var metadata = map.Current.Ledgers.Single(static l => l.IsOpen);
        var writer = registry.TryGet(metadata.Id)!;
        Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(writer, 0x33, Token));

        using var lease = reader.LeaseLedger(metadata);
        // Confirm lease is currently active (0 RTT).
        _ = await lease.Ledger.ReadTailAsync(Token);
        Assert.Equal(0, transport.ReadTailCalls);

        // Surrender via PrepareRelease.
        var released = await writer.PrepareReleaseAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(new Slot(1), Assert.IsType<LedgerPrepareReleaseOutcome.Released>(released.Value).ContiguousTail);

        // Even a synthetic late lease grant cannot resurrect a permanently surrendered lease.
        lease.Ledger.TryExtendLease(clock.GetTimestamp(), LeaseDuration);

        // ReadTailAsync immediately falls back to quorum RPCs instead of serving 0-RTT.
        _ = await lease.Ledger.ReadTailAsync(Token);
        Assert.True(transport.ReadTailCalls > 0);
    }

    /// <summary>
    /// ledgers.md §8.4: concurrent fallback <see cref="Ledger.ReadTailAsync"/> calls (when leases are disabled or
    /// expired) singleflight-coalesce into at most one in-flight quorum check plus one queued check for callers
    /// arriving mid-flight, preventing replica storms.
    /// </summary>
    [Fact]
    public async Task FallbackReadTailCoalescesConcurrentCallersIntoSingleflightChecks()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource<ReplicaTailRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource<ReplicaTailRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        await using var replica = new DelegatingLedgerReplica
        {
            ReadTail = (_, _, _) =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    _ = firstEntered.TrySetResult();
                    return new ValueTask<ReplicaTailRead>(firstRelease.Task);
                }

                return new ValueTask<ReplicaTailRead>(secondRelease.Task);
            },
        };

        await using var ledger = WriterTestData.CreateLog(replica, windowSize: 4);

        // First wave: caller 1 starts the in-flight check.
        var first = ledger.ReadTailAsync(Token).AsTask();
        await firstEntered.Task.WaitAsync(Token);
        Assert.Equal(1, Volatile.Read(ref calls));

        // Second wave: 6 callers arrive while the first check is already in flight.
        // Because their linearizability point is after the first check started, they coalesce onto ONE queued check.
        var wave2 = Enumerable.Range(0, 6).Select(_ => ledger.ReadTailAsync(Token).AsTask()).ToArray();
        Assert.Equal(1, Volatile.Read(ref calls));

        _ = firstRelease.TrySetResult(new LedgerReplicaTail(new Slot(1), IsSealed: false));
        Assert.Equal(new LedgerTail(new Slot(1), IsSealed: false), Assert.IsType<LedgerTail>((await first).Value));

        // Release the single queued check; all 6 callers receive its result with only 2 total replica RPCs.
        _ = secondRelease.TrySetResult(new LedgerReplicaTail(new Slot(2), IsSealed: false));
        var wave2Results = await Task.WhenAll(wave2);
        Assert.All(wave2Results, static tail => Assert.Equal(new LedgerTail(new Slot(2), IsSealed: false), Assert.IsType<LedgerTail>(tail.Value)));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    /// <summary>
    /// LR-I10, LS-I13: <see cref="LedgerStore"/> latches a RAM-only <c>bootLeaseHorizon = bootTime + D</c> on open
    /// with zero WAL writes; <c>ReadTailAsync(seal: true)</c> after restart reports remaining <c>bootLeaseHorizon</c>
    /// (decaying to zero after <c>D</c>), <c>seal: false</c> always reports zero, non-recovery writes grant <c>D</c>
    /// while recovery writes grant zero, batched <c>HeartbeatReplicasAsync</c> renews live ledgers in RAM with zero
    /// WAL writes and respects sealed/dropped/absent replicas, and <c>LeaseDuration = 0</c> disables leases completely.
    /// </summary>
    [Fact]
    public async Task LedgerStoreBootLeaseHorizonAndBatchedHeartbeatsWriteZeroWalRecords()
    {
        var clock = new FakeTimeProvider();
        var root = LedgerStoreReplicaTests.Simulated();
        var storeOptions = root.Options with { LeaseDuration = LeaseDuration };
        var liveA = new LedgerId(10);
        var liveB = new LedgerId(11);
        var sealedLedger = new LedgerId(12);
        var droppedLedger = new LedgerId(13);
        var absentLedger = new LedgerId(14);

        await using (var store = await root.OpenAsync(create: true, options: storeOptions, timeProvider: clock))
        {
            await using var a = store.OpenReplica(liveA, Window);
            await using var b = store.OpenReplica(liveB, Window);
            await using var s = store.OpenReplica(sealedLedger, Window);
            await using var d = store.OpenReplica(droppedLedger, Window);

            // Non-recovery write returns LeaseGrant = 3s; recovery write returns 0.
            var writeOutcome = Assert.IsType<ReplicaWriteOutcome.Written>((await LedgerStoreReplicaTests.WriteAsync(a, 0, [1])).Value);
            Assert.Equal(LeaseDuration, writeOutcome.LeaseGrant);
            _ = await LedgerStoreReplicaTests.WriteAsync(b, 0, [2]);
            _ = await LedgerStoreReplicaTests.WriteAsync(s, 0, [3]);
            _ = await LedgerStoreReplicaTests.WriteAsync(d, 0, [4]);
            _ = await s.SealAsync(Token);
            var recoveryOutcome = Assert.IsType<ReplicaWriteOutcome.Written>((await LedgerStoreReplicaTests.RecoveryWriteAsync(s, 1, [5])).Value);
            Assert.Equal(TimeSpan.Zero, recoveryOutcome.LeaseGrant);
            await store.DropReplicaAsync(droppedLedger, Token);

            // Advance 2s and issue a batched heartbeat across all 5 ledgers: zero WAL writes!
            clock.Advance(TimeSpan.FromSeconds(2));
            var walWritesBefore = root.Storage.IssuedWriteCount;
            var hb = await store.HeartbeatReplicasAsync(new[] { liveA, liveB, sealedLedger, droppedLedger, absentLedger }, Token);
            Assert.Equal(walWritesBefore, root.Storage.IssuedWriteCount);
            Assert.Equal(LeaseDuration, hb.LeaseGrant);
            Assert.Equal(
                [
                    ReplicaHeartbeatStatus.Granted,
                    ReplicaHeartbeatStatus.Granted,
                    ReplicaHeartbeatStatus.Sealed,
                    ReplicaHeartbeatStatus.Dropped,
                    ReplicaHeartbeatStatus.Absent,
                ],
                hb.Outcomes.ToArray());
            Assert.DoesNotContain(absentLedger, await store.ListReplicasAsync(Token));

            // Sealing liveA 1s after the heartbeat reports 2s remaining (from RAM grantedLeaseUntil),
            // whereas seal: false always reports 0.
            clock.Advance(TimeSpan.FromSeconds(1));
            var unsealedTail = Assert.IsType<LedgerReplicaTail>((await a.ReadTailAsync(Token)).Value);
            Assert.Equal(TimeSpan.Zero, unsealedTail.LeaseRemaining);
            var sealedTail = Assert.IsType<LedgerReplicaTail>((await a.SealAsync(Token)).Value);
            Assert.True(sealedTail.IsSealed);
            Assert.Equal(TimeSpan.FromSeconds(2), sealedTail.LeaseRemaining);
        }

        root.Crash(operatingSystem: true);

        // Reopen after crash: bootLeaseHorizon = bootTime + 3s protects liveB even with RAM lost, with zero WAL writes!
        clock.Advance(TimeSpan.FromSeconds(10));
        await using (var reopened = await root.OpenAsync(create: false, options: storeOptions, timeProvider: clock))
        {
            await using var b = reopened.OpenReplica(liveB, Window);
            clock.Advance(TimeSpan.FromSeconds(1));
            var sealAt1s = Assert.IsType<LedgerReplicaTail>((await b.SealAsync(Token)).Value);
            Assert.True(sealAt1s.IsSealed);
            Assert.Equal(TimeSpan.FromSeconds(2), sealAt1s.LeaseRemaining);

            // After full LeaseDuration has elapsed since boot, bootLeaseHorizon decays to 0.
            clock.Advance(TimeSpan.FromSeconds(2));
            var sealAt3s = Assert.IsType<LedgerReplicaTail>((await b.SealAsync(Token)).Value);
            Assert.Equal(TimeSpan.Zero, sealAt3s.LeaseRemaining);
        }

        // LeaseDuration = 0 disables bootLeaseHorizon and write grants completely.
        await using (var disabled = await root.OpenAsync(create: false, options: root.Options with { LeaseDuration = TimeSpan.Zero }, timeProvider: clock))
        {
            await using var fresh = disabled.OpenReplica(new LedgerId(99), Window);
            var written = Assert.IsType<ReplicaWriteOutcome.Written>((await LedgerStoreReplicaTests.WriteAsync(fresh, 0, [9])).Value);
            Assert.Equal(TimeSpan.Zero, written.LeaseGrant);
            var sealedTail = Assert.IsType<LedgerReplicaTail>((await fresh.SealAsync(Token)).Value);
            Assert.Equal(TimeSpan.Zero, sealedTail.LeaseRemaining);
        }
    }

    /// <summary>
    /// ledgers.md §8.4, LP-I17: <see cref="LedgerWriterRegistry"/> batches idle lease renewals per peer node once an
    /// open primary ledger has been idle for more than <c>D / 3</c>, covering multiple due ledgers in ONE
    /// <see cref="ILedgerReplicaTransport.HeartbeatAsync"/> RPC per peer node so 0-RTT local reads remain active.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WriterRegistryBatchesIdleHeartbeatsPerPeerNodeAcrossMultipleLedgers()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ObservableFakeTimeProvider();
        using var backing = new InMemoryLedgerReplicaTransport(clock, LeaseDuration);
        var transport = new CountingTransport(backing);
        var register = new InMemoryConditionalRegister();
        var map = await LedgerMapView.OpenAsync(register, token);
        var nodes = TransitionCluster.Nodes;
        var leaseOptions = new LedgerLeaseOptions
        {
            LeaseDuration = LeaseDuration,
            ClockDriftFactor = ClockDriftFactor,
            LeaseGuardBand = GuardBand,
        };

        await using var reader = new LedgerReader(map, transport, clock, nodes[0]) { LeaseOptions = leaseOptions };
        await using var registry = new LedgerWriterRegistry(reader, 1 << 20, LedgerWriterOptions.Default, clock, NullLoggerFactory.Instance, new Random(4));
        var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);
        await using var committer = new LedgerMapCommitter(register, 1 << 20);

        // Bootstrap and split the root into 2 open ledgers both owned by nodes[0].
        Assert.Equal(TransitionCluster.Applied(1), await executor.ExecuteAsync(new Transition.Bootstrap(TransitionCluster.Choice(nodes[0], 0)), committer.CommitAsync, token));
        var root = map.Current.Ledgers.Single(static l => l.IsOpen);
        var splitTask = executor.ExecuteAsync(new Transition.Split(root.Id, TransitionCluster.Choice(nodes[0], 1)), committer.CommitAsync, token);
        // Advance clock past bootLeaseHorizon so the split finishes cleanly.
        while (!splitTask.IsCompleted)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, token);
        }

        Assert.Equal(TransitionCluster.Applied(2), await splitTask);

        var openLedgers = map.Current.Ledgers.Where(static l => l.IsOpen).ToArray();
        Assert.Equal(2, openLedgers.Length);

        // Write once to both ledgers so both have live replicas and initial leases.
        foreach (var meta in openLedgers)
        {
            var writer = registry.TryGet(meta.Id)!;
            var key = meta.Range.Contains([0x01]) ? (byte)0x01 : (byte)0x81;
            Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(writer, key, token));
        }

        // Advance past D / 3 (1s) in steps so the background heartbeat loop wakes and sends ONE batched RPC per peer node
        // covering BOTH open ledgers (batch size 2).
        transport.ResetHeartbeatBatches();
        var deadline = TimeProvider.System.GetTimestamp();
        while (!transport.HasHeartbeatBatchOfSize(2))
        {
            Assert.True(TimeProvider.System.GetElapsedTime(deadline) < TimeSpan.FromSeconds(15), "Timed out waiting for batched idle heartbeat.");
            clock.Advance(TimeSpan.FromMilliseconds(400));
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, token);
        }

        // Both ledgers now hold renewed leases and still serve 0-RTT local reads.
        var tailCallsBefore = transport.ReadTailCalls;
        foreach (var meta in openLedgers)
        {
            using var lease = reader.LeaseLedger(meta);
            var tail = Assert.IsType<LedgerTail>((await lease.Ledger.ReadTailAsync(token)).Value);
            Assert.False(tail.IsSealed);
            Assert.True(tail.ServedHere);
        }

        Assert.Equal(tailCallsBefore, transport.ReadTailCalls);
    }

    /// <summary>
    /// LG-I10, OR-I24: unplanned <see cref="Transition.Replace"/> (<see cref="PredecessorReleaseHint.TakenOver"/>),
    /// <see cref="Transition.Split"/>, and <see cref="Transition.Merge"/> wait for the sealing quorum's
    /// <c>max(LeaseRemaining)</c> before invoking <c>commit</c>, whereas planned cooperative
    /// <see cref="Transition.Replace"/> (<see cref="PredecessorReleaseHint.Cooperative"/>) skips the wait (0 ms).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TransitionExecutorWaitsForLeaseExpiryOnUnplannedReplaceSplitMergeAndSkipsOnCooperativeReplace()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ObservableFakeTimeProvider();
        using var backing = new InMemoryLedgerReplicaTransport(clock, LeaseDuration);
        var register = new InMemoryConditionalRegister();
        var mapA = await LedgerMapView.OpenAsync(register, token);
        var mapB = await LedgerMapView.OpenAsync(register, token);
        var nodes = TransitionCluster.Nodes;
        var leaseOptions = new LedgerLeaseOptions
        {
            LeaseDuration = LeaseDuration,
            ClockDriftFactor = ClockDriftFactor,
            LeaseGuardBand = GuardBand,
        };

        await using var readerA = new LedgerReader(mapA, backing, clock, nodes[0]) { LeaseOptions = leaseOptions };
        await using var readerB = new LedgerReader(mapB, backing, clock, nodes[1]) { LeaseOptions = leaseOptions };
        await using var registryA = new LedgerWriterRegistry(readerA, 1 << 20, LedgerWriterOptions.Default, clock, NullLoggerFactory.Instance, new Random(5));
        await using var registryB = new LedgerWriterRegistry(readerB, 1 << 20, LedgerWriterOptions.Default, clock, NullLoggerFactory.Instance, new Random(6));
        var executorA = new TransitionExecutor(registryA, NullLogger<TransitionExecutor>.Instance);
        var executorB = new TransitionExecutor(registryB, NullLogger<TransitionExecutor>.Instance);
        await using var committer = new LedgerMapCommitter(register, 1 << 20);

        // Bootstrap L1 on A and write so L1 has an active 3s lease on replicas.
        Assert.Equal(TransitionCluster.Applied(1), await executorA.ExecuteAsync(new Transition.Bootstrap(TransitionCluster.Choice(nodes[0], 0)), committer.CommitAsync, token));
        var l1 = mapA.Current.Ledgers.Single(static l => l.IsOpen);
        Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(registryA.TryGet(l1.Id)!, 0x10, token));

        // 1. Unplanned Replace (TakenOver) from A to B: commit MUST NOT be invoked until 3s elapses on clock!
        var timersBeforeReplace = clock.CreatedTimers;
        var commitCalled = 0;
        var unplannedReplace = executorB.ExecuteAsync(
            new Transition.Replace(l1.Id, TransitionCluster.Choice(nodes[1], 1), PredecessorReleaseHint.TakenOver),
            (change, ct) =>
            {
                _ = Interlocked.Increment(ref commitCalled);
                return committer.CommitAsync(change, ct);
            },
            token);

        await TransitionCluster.WaitUntilAsync(() => clock.CreatedTimers > timersBeforeReplace, "unplanned Replace enters lease-expiry sleep");
        clock.Advance(LeaseDuration - TimeSpan.FromMilliseconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, token);
        Assert.Equal(0, Volatile.Read(ref commitCalled));
        Assert.False(unplannedReplace.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TransitionCluster.Applied(2), await unplannedReplace);
        Assert.Equal(1, Volatile.Read(ref commitCalled));

        // 2. Cooperative Replace from B back to A after PrepareRelease: 0 ms wait (no clock advance needed!).
        var l2 = mapB.Current.Ledgers.Single(static l => l.IsOpen);
        Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(registryB.TryGet(l2.Id)!, 0x10, token));
        _ = Assert.IsType<LedgerPrepareReleaseOutcome.Released>((await registryB.TryGet(l2.Id)!.PrepareReleaseAsync(TimeSpan.FromSeconds(5), token)).Value);

        var cooperativeStart = clock.GetTimestamp();
        Assert.Equal(
            TransitionCluster.Applied(3),
            await executorA.ExecuteAsync(
                new Transition.Replace(l2.Id, TransitionCluster.Choice(nodes[0], 2), PredecessorReleaseHint.Cooperative),
                committer.CommitAsync,
                token));
        Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(cooperativeStart));

        // 3. Remote Split executed by B on A's open ledger: MUST wait for lease expiry before commit.
        var l3 = mapA.Current.Ledgers.Single(static l => l.IsOpen);
        Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(registryA.TryGet(l3.Id)!, 0x10, token));
        var timersBeforeSplit = clock.CreatedTimers;
        var splitCommitCalled = 0;
        var splitTask = executorB.ExecuteAsync(
            new Transition.Split(l3.Id, TransitionCluster.Choice(nodes[1], 3)),
            (change, ct) =>
            {
                _ = Interlocked.Increment(ref splitCommitCalled);
                return committer.CommitAsync(change, ct);
            },
            token);

        await TransitionCluster.WaitUntilAsync(() => splitTask.IsCompleted || clock.CreatedTimers > timersBeforeSplit, "Split enters lease-expiry sleep");
        clock.Advance(LeaseDuration - TimeSpan.FromMilliseconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, token);
        Assert.Equal(0, Volatile.Read(ref splitCommitCalled));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TransitionCluster.Applied(4), await splitTask);
        Assert.Equal(1, Volatile.Read(ref splitCommitCalled));

        // 4. Remote Merge executed by A on B's two open children: MUST wait for max lease remaining across both.
        var children = mapB.Current.Ledgers.Where(static l => l.IsOpen).OrderBy(static l => l.Id.Value).ToArray();
        Assert.Equal(2, children.Length);
        Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(registryB.TryGet(children[0].Id)!, 0x01, token));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(registryB.TryGet(children[1].Id)!, 0x81, token));
        // children[0] has 2.5s remaining, children[1] has 3s remaining -> Merge must wait the full 3s!
        var timersBeforeMerge = clock.CreatedTimers;
        var mergeCommitCalled = 0;
        var mergeTask = executorA.ExecuteAsync(
            new Transition.Merge(children[0].Id, children[1].Id, TransitionCluster.Choice(nodes[0], 4)),
            (change, ct) =>
            {
                _ = Interlocked.Increment(ref mergeCommitCalled);
                return committer.CommitAsync(change, ct);
            },
            token);

        await TransitionCluster.WaitUntilAsync(() => mergeTask.IsCompleted || clock.CreatedTimers > timersBeforeMerge, "Merge enters lease-expiry sleep");
        clock.Advance(LeaseDuration - TimeSpan.FromMilliseconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, token);
        Assert.Equal(0, Volatile.Read(ref mergeCommitCalled));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TransitionCluster.Applied(5), await mergeTask);
        Assert.Equal(1, Volatile.Read(ref mergeCommitCalled));

        // 5. Same-process Split by A on A's own ledger: StopAdmittingAsync surrenders the sole primary lease in-process -> 0 ms wait.
        var lMerged = mapA.Current.Ledgers.Single(static l => l.IsOpen);
        Assert.Equal(TransitionCluster.DurableAt(0), await TransitionCluster.ProposeAsync(registryA.TryGet(lMerged.Id)!, 0x22, token));
        var localSplitStart = clock.GetTimestamp();
        Assert.Equal(
            TransitionCluster.Applied(6),
            await executorA.ExecuteAsync(
                new Transition.Split(lMerged.Id, TransitionCluster.Choice(nodes[0], 5)),
                committer.CommitAsync,
                token));
        Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(localSplitStart));
    }

    private sealed class ObservableFakeTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        private int _createdTimers;

        public int CreatedTimers => Volatile.Read(ref _createdTimers);

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override long GetTimestamp() => _inner.GetTimestamp();

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _ = Interlocked.Increment(ref _createdTimers);
            return _inner.CreateTimer(callback, state, dueTime, period);
        }

        public void Advance(TimeSpan delta) => _inner.Advance(delta);
    }

    private sealed class CountingTransport(ILedgerReplicaTransport inner) : ILedgerReplicaTransport
    {
        private readonly Lock _lock = new();
        private readonly List<int> _heartbeatBatchSizes = [];
        private int _readTailCalls;
        private int _heartbeatCalls;

        public int ReadTailCalls => Volatile.Read(ref _readTailCalls);

        public int HeartbeatCalls => Volatile.Read(ref _heartbeatCalls);

        public bool SuppressHeartbeats { get; init; }

        public void ResetHeartbeatBatches()
        {
            lock (_lock)
            {
                _heartbeatBatchSizes.Clear();
            }
        }

        public bool HasHeartbeatBatchOfSize(int size)
        {
            lock (_lock)
            {
                return _heartbeatBatchSizes.Contains(size);
            }
        }

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            new CountingReplica(inner.Open(node, ledger, options), this);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _heartbeatCalls);
            lock (_lock)
            {
                _heartbeatBatchSizes.Add(ledgers.Length);
            }

            return SuppressHeartbeats
                ? ValueTask.FromResult<ReplicaHeartbeatRead>(ReplicaWriteOutcome.Backpressure.Instance)
                : inner.HeartbeatAsync(node, ledgers, cancellationToken);
        }

        private sealed class CountingReplica(ILedgerReplica inner, CountingTransport parent) : ILedgerReplica
        {
            public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
                inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, contiguousTail, cancellationToken);

            public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
            {
                _ = Interlocked.Increment(ref parent._readTailCalls);
                return inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}

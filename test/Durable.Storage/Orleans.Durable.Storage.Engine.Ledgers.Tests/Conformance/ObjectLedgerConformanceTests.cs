using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Conformance;

/// <summary>
/// The fold oracle over object-store ledgers (object-ledgers.md §2, §10): a primary's <see cref="Ledger"/> and
/// <see cref="LedgerWriter"/> over <see cref="ObjectLedgerStorage"/>, with checks and recoveries in its process and in
/// others and the sweeper's reclamation, conform to the ledger storage contract's spec, which judges answers alone
/// (<see cref="LedgerStorageSpec"/>, LL-I1 to LL-I9). The memory store answers every call; the fault-injecting store
/// fails calls before and after they land, and holds writes, the writer's batches, a recovery's SEALs, and the
/// sweeper's deletes, which land during later calls, so a SEAL or a batch lands after the sweep which reclaimed the
/// ledger (object-ledgers.md §1, OL-I9).
/// </summary>
public sealed class ObjectLedgerConformanceTests : IDisposable
{
    private const int Window = 2;
    private const string Land = "Land";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact(Timeout = 120_000)]
    public async Task EverySequenceToASmallDepthConforms()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.Over(new InMemoryObjectStore(), Window));
        var spec = LedgerStorageSpec.Create();
        var inputs = new Microsoft.Accordant.InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LedgerOutcome>(operation).With(request, request.ToString()));

        Add(LedgerStorageSpec.Append, new LedgerAppend(1));
        Add(LedgerStorageSpec.Append, new LedgerAppend(2));
        Add(LedgerStorageSpec.AppendUnanswered, new LedgerAppendUnanswered(3));
        Add(LedgerStorageSpec.AppendUnanswered, new LedgerAppendUnanswered(5, Landed: false));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(0));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(1));
        Add(LedgerStorageSpec.Check, new LedgerCheck());
        Add(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true));
        Add(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: false));
        Add(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true));
        Add(LedgerStorageSpec.Observe, LedgerObserve.Instance);
        Add(LedgerStorageSpec.Drop, new LedgerDrop(Every: false));
        Add(LedgerStorageSpec.Drop, new LedgerDrop(Every: true));

        var sequences = spec.GenerateTests(new LedgerState(), inputs, kit.GenerationOptions(maxDepth: 5));
        Assert.NotEmpty(sequences);
        await kit.RunAsync(sequences);
    }

    [Fact(Timeout = 120_000)]
    public async Task RandomSequencesConform()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.Over(new InMemoryObjectStore(), Window));
        await kit.SampleAsync(kit.Sequences(30), iterations: 300);
    }

    /// <summary>
    /// Random sequences conform over a store which fails calls and holds writes: the writer and the recovery repeat their
    /// own calls, settling each create by its object (OL-I2), and a held SEAL or batch which lands after a sweep is read
    /// as the drop (OL-I7, OL-I9).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RandomSequencesConformOverAFaultyStore()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static seed => ObjectLedgerSystem.Over(new FaultInjectingObjectStore<InMemoryObjectStore>(new InMemoryObjectStore(), seed, faultRate: 0.1), Window));
        await kit.SampleAsync(kit.Sequences(30), iterations: 200);
    }

    /// <summary>
    /// Appends and reads run in concurrent groups, so each append's create is in flight with up to <c>K</c> lower slots'.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ConcurrentAppendsAndReadsConform()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.Over(new InMemoryObjectStore(), Window));
        var appendsAndReads = Gen.Frequency(
            (3, LedgerConformanceOperations.Values.Select(static value => new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(value)))),
            (1, LedgerConformanceOperations.Slots.Select(static slot => new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(slot)))));
        var rounds = Gen.Frequency(
            (3, LedgerConformanceOperations.Calls.Select(static call => new ConformanceRound([call]))),
            (2, appendsAndReads.Array[2, 4].Select(static calls => new ConformanceRound(calls))));
        await kit.SampleAsync(rounds.Array[1, 20].Select(static rounds => new ConformanceCase(rounds)), iterations: 300);
    }

    /// <summary>
    /// The spec fails a storage which never finds the drop marker, as a read which answers an absent object without
    /// reading it does: once the sweep reclaimed the ledger, its reads answer <c>Missing</c> or corruption, never
    /// <c>Dropped</c> (OL-I7, LL-I9).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSpecCatchesAStorageBlindToTheDropMarker()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.Over(new DropBlindObjectStore(), Window));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => kit.SampleAsync(kit.Sequences(30), iterations: 300));
        Assert.Contains("The spec does not allow round", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Concurrent groups of appends, reads, scans, and another process's checks and recoveries, and drops, conform, each
    /// group judged by every order of its calls: a recovery's fence races the writer's creates, and a drop races reads. An
    /// unanswered append, which loses every concurrent write's answer, and the primary's own check or recovery, which stops
    /// the lifetime its concurrent appends report, run alone.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ConcurrentGroupsWithFencesAndDropsConform()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.Over(new InMemoryObjectStore(), Window));
        var concurrent = Gen.Frequency(
            (4, LedgerConformanceOperations.Values.Select(static value => new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(value)))),
            (2, LedgerConformanceOperations.Slots.Select(static slot => new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(slot)))),
            (1, LedgerConformanceOperations.RangeReads.Select(static request => new ConformanceCall(LedgerStorageSpec.ReadRange, request))),
            (1, LedgerConformanceOperations.Slots.Select(static from => new ConformanceCall(LedgerStorageSpec.Scan, new LedgerScan(from)))),
            (1, Gen.Const(new ConformanceCall(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true)))),
            (2, Gen.Const(new ConformanceCall(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)))),
            (1, Gen.Bool.Select(static every => new ConformanceCall(LedgerStorageSpec.Drop, new LedgerDrop(every)))));
        var rounds = Gen.Frequency(
            (3, LedgerConformanceOperations.Calls.Select(static call => new ConformanceRound([call]))),
            (2, concurrent.Array[2, 3].Select(static calls => new ConformanceRound(calls))));
        await kit.SampleAsync(rounds.Array[1, 15].Select(static rounds => new ConformanceCase(rounds)), iterations: 300);
    }

    /// <summary>
    /// LP-I3, LP-I13: when pipelined appends overlap a concurrent recovery's fence, one slot may write its batch before
    /// the fence reaches it while a sibling slot meets the seal and stops the lifetime, so the written slot answers
    /// <c>Unresolved(slot)</c> with the lifetime stopped. The spec accepts both slot orderings (lower sealed / higher
    /// completion-stopped, and lower completion-stopped / higher sealed) and still rejects an unsealed open-ledger
    /// append returning <c>Unresolved(slot)</c> without a seal stop.
    /// </summary>
    [Fact]
    public void SpecAllowsOverlappingCompletionStopDuringSealAndRejectsUnsealedUnresolved()
    {
        var spec = LedgerStorageSpec.Create();
        var append = spec.GetOperation<LedgerAppend, LedgerOutcome>(LedgerStorageSpec.Append);
        var recover = spec.GetOperation<LedgerRecover, LedgerOutcome>(LedgerStorageSpec.Recover);

        // Case A: lower slot met the seal first (Stopped = true), higher overlapping slot wrote before the fence and stopped at completion.
        var afterLowerSealed = new LedgerState { Next = 3, Completed = 2, Values = [5, 6, 5], Partial = [2], Sealed = true, Stopped = true, End = 2 };
        var higherUnresolved = new LedgerOutcome(LedgerOutcomeKind.Unresolved, Value: 3, LifetimeStopped: true, OverlappedAppend: true, StoppedBySeal: true);
        Assert.True(spec.Allows(append, new LedgerAppend(2), higherUnresolved, new Microsoft.Accordant.StateProfile(afterLowerSealed)).Item1);

        // Case B: lower overlapping slot wrote before the fence and stopped at completion after a higher slot met the seal.
        var open = new LedgerState { Next = 2, Completed = 2, Values = [5, 6] };
        var lowerUnresolved = new LedgerOutcome(LedgerOutcomeKind.Unresolved, Value: 2, LifetimeStopped: true, OverlappedAppend: true, StoppedBySeal: true);
        var (lowerAllowed, _, lowerNext) = spec.Allows(append, new LedgerAppend(5), lowerUnresolved, new Microsoft.Accordant.StateProfile(open));
        Assert.True(lowerAllowed);
        var afterLowerUnresolved = (LedgerState)Assert.Single(lowerNext.StatesAndStepFunctions).State;
        Assert.True(afterLowerUnresolved.Stopped);
        Assert.Equal(3, afterLowerUnresolved.Next);
        Assert.Equal(2, afterLowerUnresolved.Completed);

        var recovered = new LedgerOutcome(LedgerOutcomeKind.End, Value: 3, LifetimeStopped: false, OverlappedAppend: true);
        var (recoverAllowed, _, recoverNext) = spec.Allows(recover, new LedgerRecover(Elsewhere: true), recovered, lowerNext);
        Assert.True(recoverAllowed);

        var higherSealed = new LedgerOutcome(LedgerOutcomeKind.Sealed, Value: 3, LifetimeStopped: true, OverlappedAppend: true, StoppedBySeal: true);
        Assert.True(spec.Allows(append, new LedgerAppend(2), higherSealed, recoverNext).Item1);

        // Spurious Unresolved(2) without StoppedBySeal on an open ledger is still rejected.
        var spurious = new LedgerOutcome(LedgerOutcomeKind.Unresolved, Value: 2, LifetimeStopped: true, OverlappedAppend: true, StoppedBySeal: false);
        Assert.False(spec.Allows(append, new LedgerAppend(5), spurious, new Microsoft.Accordant.StateProfile(open)).Item1);
    }

    /// <summary>
    /// object-ledgers.md §1, OL-I7, OL-I9: the writer's batches and the recovery's SEALs are each held at their first create
    /// and land after the sweep reclaimed the ledger, re-creating decided batches and placing SEALs on the fence: a read
    /// below the end returns its batch, others answer the drop, a recovery answers the drop, and the next sweep deletes
    /// the re-created batches.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task WritesHeldPastTheSweepLandAndReadAsTheDrop()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.Over(new HoldingFirstCreatesStore(), Window))
            .Action(Land, Gen.Const(0), static (system, _) => ((HoldingFirstCreatesStore)system.Inner).LandAsync(), weight: 0);
        ConformanceRound Call(string operation, object request) => new([new ConformanceCall(operation, request)]);

        await kit.RunAsync(new ConformanceCase(
        [
            Call(LedgerStorageSpec.Append, new LedgerAppend(1)),
            Call(LedgerStorageSpec.Append, new LedgerAppend(2)),
            Call(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)),
            Call(LedgerStorageSpec.Observe, LedgerObserve.Instance),
            Call(LedgerStorageSpec.Drop, new LedgerDrop(Every: true)),
            Call(Land, 0),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(0)),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(2)),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(5)),
            Call(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)),
            Call(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true)),
            Call(LedgerStorageSpec.Append, new LedgerAppend(3)),
            Call(LedgerStorageSpec.Drop, new LedgerDrop(Every: true)),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(0)),
        ]));
    }

    /// <summary>
    /// object-ledgers.md §7.4, §8, OL-I7, OL-I9, OL-I10: delayed extent creates and appends held past a sweep either
    /// meet the fence extent's block-blob <c>SEAL</c> tombstone (<c>Exists</c> / <c>NotAppendable</c>) or re-create a
    /// deleted earlier extent; reads below the end return their batch or <c>Dropped</c>, reads at or past the end and
    /// recoveries answer <c>Dropped</c>, and the next sweep deletes the re-created extent.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AppendLayoutWritesHeldPastTheSweepLandAndReadAsTheDrop()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.OverAppendable(new HoldingFirstAppendableStore(), Window))
            .Action(Land, Gen.Const(0), static (system, _) => ((HoldingFirstAppendableStore)system.Inner).LandAsync(), weight: 0);
        ConformanceRound Call(string operation, object request) => new([new ConformanceCall(operation, request)]);

        await kit.RunAsync(new ConformanceCase(
        [
            Call(LedgerStorageSpec.Append, new LedgerAppend(1)),
            Call(LedgerStorageSpec.Append, new LedgerAppend(2)),
            Call(LedgerStorageSpec.Append, new LedgerAppend(3)),
            Call(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)),
            Call(LedgerStorageSpec.Observe, LedgerObserve.Instance),
            Call(LedgerStorageSpec.Drop, new LedgerDrop(Every: true)),
            Call(Land, 0),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(0)),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(3)),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(5)),
            Call(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)),
            Call(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true)),
            Call(LedgerStorageSpec.Append, new LedgerAppend(4)),
            Call(LedgerStorageSpec.Drop, new LedgerDrop(Every: true)),
            Call(LedgerStorageSpec.Read, new LedgerSlotRead(0)),
        ]));
    }

    /// <summary>
    /// object-ledgers.md §2.1, §8: every small-depth sequence over the append (<c>CLOE</c>) layout conforms to
    /// <see cref="LedgerStorageSpec"/>.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AppendLayoutEverySequenceToASmallDepthConforms()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.OverAppendable(new InMemoryObjectStore(), Window));
        var spec = LedgerStorageSpec.Create();
        var inputs = new Microsoft.Accordant.InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LedgerOutcome>(operation).With(request, request.ToString()));

        Add(LedgerStorageSpec.Append, new LedgerAppend(1));
        Add(LedgerStorageSpec.Append, new LedgerAppend(2));
        Add(LedgerStorageSpec.AppendUnanswered, new LedgerAppendUnanswered(3));
        Add(LedgerStorageSpec.AppendUnanswered, new LedgerAppendUnanswered(5, Landed: false));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(0));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(1));
        Add(LedgerStorageSpec.Check, new LedgerCheck());
        Add(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true));
        Add(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: false));
        Add(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true));
        Add(LedgerStorageSpec.Observe, LedgerObserve.Instance);
        Add(LedgerStorageSpec.Drop, new LedgerDrop(Every: false));
        Add(LedgerStorageSpec.Drop, new LedgerDrop(Every: true));

        var sequences = spec.GenerateTests(new LedgerState(), inputs, kit.GenerationOptions(maxDepth: 5));
        Assert.NotEmpty(sequences);
        await kit.RunAsync(sequences);
    }

    [Fact(Timeout = 120_000)]
    public async Task AppendLayoutRandomSequencesConform()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.OverAppendable(new InMemoryObjectStore(), Window));
        await kit.SampleAsync(kit.Sequences(30), iterations: 300);
    }

    [Fact(Timeout = 120_000)]
    public async Task AppendLayoutRandomSequencesConformOverAFaultyStore()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static seed => ObjectLedgerSystem.OverAppendable(new FaultInjectingObjectStore<InMemoryObjectStore>(new InMemoryObjectStore(), seed, faultRate: 0.1), Window));
        await kit.SampleAsync(kit.Sequences(30), iterations: 200);
    }

    [Fact(Timeout = 120_000)]
    public async Task AppendLayoutConcurrentAppendsAndReadsConform()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.OverAppendable(new InMemoryObjectStore(), Window));
        var appendsAndReads = Gen.Frequency(
            (3, LedgerConformanceOperations.Values.Select(static value => new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(value)))),
            (1, LedgerConformanceOperations.Slots.Select(static slot => new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(slot)))));
        var rounds = Gen.Frequency(
            (3, LedgerConformanceOperations.Calls.Select(static call => new ConformanceRound([call]))),
            (2, appendsAndReads.Array[2, 4].Select(static calls => new ConformanceRound(calls))));
        await kit.SampleAsync(rounds.Array[1, 20].Select(static rounds => new ConformanceCase(rounds)), iterations: 300);
    }

    [Fact(Timeout = 120_000)]
    public async Task AppendLayoutConcurrentGroupsWithFencesAndDropsConform()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.OverAppendable(new InMemoryObjectStore(), Window));
        var concurrent = Gen.Frequency(
            (4, LedgerConformanceOperations.Values.Select(static value => new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(value)))),
            (2, LedgerConformanceOperations.Slots.Select(static slot => new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(slot)))),
            (1, LedgerConformanceOperations.RangeReads.Select(static request => new ConformanceCall(LedgerStorageSpec.ReadRange, request))),
            (1, LedgerConformanceOperations.Slots.Select(static from => new ConformanceCall(LedgerStorageSpec.Scan, new LedgerScan(from)))),
            (1, Gen.Const(new ConformanceCall(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true)))),
            (2, Gen.Const(new ConformanceCall(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)))),
            (1, Gen.Bool.Select(static every => new ConformanceCall(LedgerStorageSpec.Drop, new LedgerDrop(every)))));
        var rounds = Gen.Frequency(
            (3, LedgerConformanceOperations.Calls.Select(static call => new ConformanceRound([call]))),
            (2, concurrent.Array[2, 3].Select(static calls => new ConformanceRound(calls))));
        await kit.SampleAsync(rounds.Array[1, 15].Select(static rounds => new ConformanceCase(rounds)), iterations: 300);
    }

    [Fact(Timeout = 120_000)]
    public async Task AppendLayoutTheSpecCatchesAStorageBlindToTheDropMarker()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static _ => ObjectLedgerSystem.OverAppendable(new DropBlindObjectStore(), Window));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => kit.SampleAsync(kit.Sequences(30), iterations: 300));
        Assert.Contains("The spec does not allow round", failure.Message, StringComparison.Ordinal);
    }

    private ConformanceKit<LedgerState, ObjectLedgerSystem> Kit(CancellationToken cancellationToken, Func<int, ObjectLedgerSystem> create) =>
        new ConformanceKit<LedgerState, ObjectLedgerSystem>(
                LedgerStorageSpec.Create(),
                static () => new LedgerState(),
                seed => Task.FromResult(create(seed)),
                LedgerStorageSpec.IsCallerAllowed,
                cancellationToken,
                _leaks.InScopeAsync)
            .WithLedgerOperations();

    /// <summary>
    /// Holds each slot's first create, failing it as a lost request does, and lands every held create when asked, the
    /// writer's batches and the recovery's SEALs among them.
    /// </summary>
    private sealed class HoldingFirstCreatesStore : Objects.ForwardingObjectStore
    {
        private readonly HashSet<ObjectName> _seen = [];
        private readonly List<(ObjectName Name, byte[] Content)> _held = [];

        public async Task LandAsync()
        {
            foreach (var (name, content) in _held)
            {
                using var buffer = ArcBuffer.Wrap(content);
                _ = await Inner.CreateAsync(name, content.Length == 0 ? default : buffer, crc32c: null, TestContext.Current.CancellationToken);
            }

            _held.Clear();
        }

        public override ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            if (ObjectLedgerNames.ParseSlot(new LedgerId(7), name) is not null && _seen.Add(name))
            {
                _held.Add((name, content.ToArray()));
                return ValueTask.FromException<ObjectCreateOutcome>(new IOException("The create's answer was lost; the create is held."));
            }

            return base.CreateAsync(name, content, crc32c, cancellationToken);
        }
    }

    /// <summary>
    /// Holds each extent's first <c>CreateAppendableAsync</c> and first <c>AppendAsync</c>, failing them as lost
    /// requests, and replays them when asked (after a sweep).
    /// </summary>
    private sealed class HoldingFirstAppendableStore : Objects.ForwardingObjectStore
    {
        private readonly HashSet<ObjectName> _seenCreates = [];
        private readonly HashSet<ObjectName> _seenAppends = [];
        private readonly List<(ObjectName Name, long? ExpectedLength, byte[]? Content)> _held = [];

        public async Task LandAsync()
        {
            foreach (var (name, expectedLength, content) in _held)
            {
                if (expectedLength is not { } offset || content is null)
                {
                    _ = await Inner.CreateAppendableAsync(name, TestContext.Current.CancellationToken);
                }
                else
                {
                    using var buffer = ArcBuffer.Wrap(content);
                    _ = await Inner.AppendAsync(name, offset, buffer, crc32c: null, TestContext.Current.CancellationToken);
                }
            }

            _held.Clear();
        }

        public override ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            if (ObjectLedgerNames.ParseExtent(new LedgerId(7), name) is not null && _seenCreates.Add(name))
            {
                _held.Add((name, null, null));
                return ValueTask.FromException<ObjectCreateOutcome>(new IOException("The appendable create's answer was lost; the create is held."));
            }

            return base.CreateAppendableAsync(name, cancellationToken);
        }

        public override ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            if (ObjectLedgerNames.ParseExtent(new LedgerId(7), name) is not null && _seenAppends.Add(name))
            {
                _held.Add((name, expectedLength, content.ToArray()));
                return ValueTask.FromException<ObjectAppendOutcome>(new IOException("The append's answer was lost; the append is held."));
            }

            return base.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);
        }
    }

    /// <summary>A memory store whose properties reads never find a ledger's <c>dropped</c> marker.</summary>
    private sealed class DropBlindObjectStore : Objects.ForwardingObjectStore
    {
        public override ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            name.Value.EndsWith("/dropped", StringComparison.Ordinal) ? ValueTask.FromResult<ObjectProperties?>(null) : base.GetPropertiesAsync(name, cancellationToken);
    }
}

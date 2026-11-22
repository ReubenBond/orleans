using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Objects;

/// <summary>
/// Metamorphic equivalence across all four <see cref="ILedgerStorage"/> configurations (object-ledgers.md §10,
/// verification.md §13): quorum <c>N = 1, Q = 1</c>, quorum <c>N = 3, Q = 2</c>, object-store per-slot
/// (<see cref="ObjectLedgerStorage"/>), and object-store append (<see cref="AppendObjectLedgerStorage"/>). Identical
/// scripted and CsCheck-generated workloads drive all four and assert exact answer equivalence: append outcomes,
/// recovered end <c>T</c>, slot reads, byte-range reads, <c>ReadRangeAsync</c>, open/sealed checks, and post-drop
/// <c>Dropped</c> answers.
/// </summary>
public sealed class ObjectLedgerMetamorphicTests : IDisposable
{
    private const string SeedVariable = "CLOG_TEST_SEED";
    private static readonly LedgerId Id = new(76);
    private static readonly LedgerId NextId = new(77);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Randomized workloads mixing single and pipelined appends, slot reads, byte-range reads, ranged scans, open and
    /// sealed checks, recovery from primary and other processes, observing recorded <c>T</c>, post-seal writes, and
    /// drop/sweep answer identically across Quorum (N=1, Q=1), Quorum (N=3, Q=2), Per-slot, and Append layouts.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RandomizedWorkloadsAnswerEquivalentlyAcrossAllFourConfigurations()
    {
        var payloadGen = Gen.Select(Gen.Byte, Gen.Int[1, 96], static (seed, len) => MakePayload(seed, len));
        var slotChoiceGen = Gen.Frequency(
            (3, Gen.Int[0, 31].Select(static index => SlotChoice.FromCommitted(index))),
            (2, Gen.Int[-2, 2].Select(static delta => SlotChoice.NearTail(delta))),
            (1, Gen.Int[0, 3].Select(static slot => SlotChoice.At(slot))));

        var workloadGen = Gen.Int[1, 4].SelectMany(window =>
        {
            var maxPipelined = Math.Max(2, window);
            var stepGen = Gen.Frequency(
                (4, payloadGen.Select(static p => (WorkloadStep)new AppendStep(p))),
                (2, payloadGen.Array[2, maxPipelined].Select(static ps => (WorkloadStep)new PipelinedAppendStep(ps))),
                (2, Gen.Select(slotChoiceGen, Gen.Bool, Gen.Bool, static (slot, elsewhere, observeTail) => (WorkloadStep)new ReadSlotStep(slot, elsewhere, observeTail))),
                (2, Gen.Select(slotChoiceGen, Gen.Int[0, 100], Gen.Int[1, 48], Gen.Bool, Gen.Bool, static (slot, offset, len, elsewhere, observeTail) => (WorkloadStep)new ReadBytesStep(slot, offset, len, elsewhere, observeTail))),
                (2, Gen.Select(slotChoiceGen, Gen.Int[0, 8], Gen.Bool, Gen.Bool, static (from, count, elsewhere, observeTail) => (WorkloadStep)new ReadRangeStep(from, count, elsewhere, observeTail))),
                (2, Gen.Bool.Select(static elsewhere => (WorkloadStep)new CheckStep(elsewhere))),
                (1, Gen.Bool.Select(static elsewhere => (WorkloadStep)new RecoverStep(elsewhere))),
                (1, Gen.Const<WorkloadStep>(new DropStep())));

            return stepGen.Array[4, 24].Select(steps => (Window: window, Steps: steps));
        });

        var replaySeed = Environment.GetEnvironmentVariable(SeedVariable) is { Length: > 0 } seed ? seed : null;
        var cancellationToken = TestContext.Current.CancellationToken;
        await workloadGen.SampleAsync(async workload =>
        {
            await _leaks.InScopeAsync(() => RunWorkloadAsync(workload.Window, workload.Steps, cancellationToken));
        }, iter: 120, seed: replaySeed);
    }

    /// <summary>
    /// Empty-ledger edge case: open check, recovery at <c>T = 0</c>, stale writer append, post-seal check, missing reads,
    /// empty range scan, and post-drop <c>Dropped</c> answers match across all four configurations.
    /// </summary>
    [Fact]
    public async Task EmptyLedgerRecoveryAndDropAnswerEquivalently()
    {
        WorkloadStep[] steps =
        [
            new CheckStep(Elsewhere: false),
            new CheckStep(Elsewhere: true),
            new ReadSlotStep(SlotChoice.At(0), Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 4, Elsewhere: true, ObserveRecordedTail: true),
            new RecoverStep(Elsewhere: true),
            new AppendStep(MakePayload(1, 16)),
            new CheckStep(Elsewhere: true),
            new ReadSlotStep(SlotChoice.At(0), Elsewhere: false, ObserveRecordedTail: true),
            new ReadBytesStep(SlotChoice.At(0), 0, 4, Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 2, Elsewhere: true, ObserveRecordedTail: true),
            new DropStep(),
            new ReadSlotStep(SlotChoice.At(0), Elsewhere: true, ObserveRecordedTail: true),
            new ReadBytesStep(SlotChoice.At(0), 0, 4, Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 2, Elsewhere: true, ObserveRecordedTail: true),
            new RecoverStep(Elsewhere: true),
        ];

        await RunWorkloadAsync(window: 2, steps, Token);
    }

    /// <summary>
    /// Scripted multi-extent edge case: pipelined writes across <c>K = 4</c> triggering multiple append-layout extent
    /// rotations, boundary byte-range reads (start, middle, last byte, outside batch), below-bound cold reads observing
    /// recorded <c>T</c>, cross-extent <c>ReadRangeAsync</c>, takeover recovery, post-seal stale writes, and drop/sweep
    /// reclamation match across all four configurations.
    /// </summary>
    [Fact]
    public async Task PipelinedWritesMultiExtentReadsAndRecoveryAnswerEquivalently()
    {
        WorkloadStep[] steps =
        [
            new AppendStep(MakePayload(1, 40)),
            new PipelinedAppendStep([MakePayload(2, 64), MakePayload(3, 80), MakePayload(4, 48), MakePayload(5, 72)]),
            new PipelinedAppendStep([MakePayload(6, 96), MakePayload(7, 32)]),
            new CheckStep(Elsewhere: true),
            new ReadSlotStep(SlotChoice.At(0), Elsewhere: true, ObserveRecordedTail: true),
            new ReadSlotStep(SlotChoice.At(3), Elsewhere: false, ObserveRecordedTail: true),
            new ReadSlotStep(SlotChoice.At(6), Elsewhere: true, ObserveRecordedTail: true),
            new ReadSlotStep(SlotChoice.At(7), Elsewhere: true, ObserveRecordedTail: true),
            new ReadBytesStep(SlotChoice.At(0), 0, 1, Elsewhere: true, ObserveRecordedTail: true),
            new ReadBytesStep(SlotChoice.At(2), 17, 25, Elsewhere: true, ObserveRecordedTail: true),
            new ReadBytesStep(SlotChoice.At(6), 31, 1, Elsewhere: false, ObserveRecordedTail: true),
            new ReadBytesStep(SlotChoice.At(6), 32, 1, Elsewhere: true, ObserveRecordedTail: true),
            new ReadBytesStep(SlotChoice.At(6), 20, 20, Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 7, Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(2), Count: 3, Elsewhere: false, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(5), Count: 5, Elsewhere: true, ObserveRecordedTail: true),
            new RecoverStep(Elsewhere: true),
            new AppendStep(MakePayload(99, 24)),
            new PipelinedAppendStep([MakePayload(100, 16), MakePayload(101, 16)]),
            new CheckStep(Elsewhere: false),
            new CheckStep(Elsewhere: true),
            new ReadSlotStep(SlotChoice.At(0), Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 7, Elsewhere: true, ObserveRecordedTail: true),
            new DropStep(),
            new ReadSlotStep(SlotChoice.At(0), Elsewhere: true, ObserveRecordedTail: true),
            new ReadSlotStep(SlotChoice.At(6), Elsewhere: true, ObserveRecordedTail: true),
            new ReadBytesStep(SlotChoice.At(2), 0, 8, Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 7, Elsewhere: true, ObserveRecordedTail: true),
            new ReadSlotStep(SlotChoice.At(0), Elsewhere: true, ObserveRecordedTail: false),
            new ReadRangeStep(SlotChoice.At(0), Count: 7, Elsewhere: true, ObserveRecordedTail: false),
            new RecoverStep(Elsewhere: true),
        ];

        await RunWorkloadAsync(window: 4, steps, Token);
    }

    /// <summary>
    /// Torn pipelined window (LP-I3, LL-I4, OL-I6): slot <c>0</c> commits, slot <c>2</c> is submitted with slot
    /// <c>1</c> left unwritten, and a taker calls <c>RecoverAsync</c>. All four configurations close the gap at
    /// <c>T = 1</c>, reject a subsequent write to slot <c>1</c> as <c>Sealed</c>, and answer identically on reads of
    /// slots <c>0</c> and <c>1</c> and range <c>[0, 2)</c>.
    /// </summary>
    [Fact]
    public async Task TornPipelinedWindowRecoveryClosesGapEquivalentlyAcrossAllFourConfigurations()
    {
        var token = Token;
        var payload0 = MakePayload(11, 32);
        var payload1Late = MakePayload(12, 24);
        var payload2 = MakePayload(13, 40);

        await _leaks.InScopeAsync(async () =>
        {
            var harnesses = CreateHarnesses(window: 3);
            try
            {
                var results = new TornWindowResult[harnesses.Length];
                for (var h = 0; h < harnesses.Length; h++)
                {
                    results[h] = await harnesses[h].RunTornWindowAsync(payload0, payload1Late, payload2, token);
                }

                var baseline = results[0];
                Assert.Equal(1L, baseline.RecoveredEnd);
                Assert.True(baseline.LateGapWriteSealed);
                Assert.Equal(new ReadAnswer(ReadAnswerKind.Batch, payload0), baseline.Slot0Read);
                Assert.Equal(new ReadAnswer(ReadAnswerKind.Missing), baseline.Slot1Read);
                Assert.Equal(
                    new ReadRangeAnswer([new ReadAnswer(ReadAnswerKind.Batch, payload0), new ReadAnswer(ReadAnswerKind.Missing)]),
                    baseline.Range0To2);

                for (var h = 1; h < harnesses.Length; h++)
                {
                    Assert.Equal(baseline, results[h]);
                }
            }
            finally
            {
                await DisposeAllAsync(harnesses);
            }
        });
    }

    /// <summary>
    /// Concurrent takeover racing an in-flight pipelined writer: across all four configurations, recovery returns
    /// <c>Recovered(T)</c> within the submitted prefix, every acknowledged append lies strictly below <c>T</c>, every
    /// slot below <c>T</c> reads back its exact submitted payload on a cold handle and via <c>ReadRangeAsync</c>, slot
    /// <c>T</c> is absent, and writes after takeover are rejected.
    /// </summary>
    [Fact]
    public async Task RecoveryRacingInFlightWriterPreservesContractAcrossAllFourConfigurations()
    {
        var token = Token;
        const int burst = 12;
        var payloads = Enumerable.Range(0, burst).Select(static i => MakePayload((byte)(i + 1), 32 + i * 7)).ToArray();

        await _leaks.InScopeAsync(async () =>
        {
            var harnesses = CreateHarnesses(window: 4);
            try
            {
                foreach (var harness in harnesses)
                {
                    await harness.VerifyRacingRecoveryAsync(payloads, token);
                }
            }
            finally
            {
                await DisposeAllAsync(harnesses);
            }
        });
    }

    /// <summary>
    /// Quorum <c>N = 3, Q = 2</c> with one replica transiently failing writes and reads (then rejoining behind the
    /// quorum with missing slots) answers identically to <c>N = 1, Q = 1</c>, per-slot, and append object ledgers.
    /// </summary>
    [Fact]
    public async Task QuorumWithLaggingReplicaAnswersEquivalentlyToAllOtherConfigurations()
    {
        var token = Token;
        var lagSwitch = new ReplicaLagSwitch { IsLagging = true };

        WorkloadStep[] phase1 =
        [
            new AppendStep(MakePayload(21, 48)),
            new PipelinedAppendStep([MakePayload(22, 64), MakePayload(23, 32)]),
            new ReadSlotStep(SlotChoice.At(0), Elsewhere: true, ObserveRecordedTail: true),
            new ReadSlotStep(SlotChoice.At(2), Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 3, Elsewhere: true, ObserveRecordedTail: true),
            new CheckStep(Elsewhere: true),
        ];

        // Bring the lagging replica back online (still missing slots 0..2), append more, read across both old and new
        // slots, recover, and drop.
        WorkloadStep[] phase2 =
        [
            new AppendStep(MakePayload(24, 56)),
            new ReadSlotStep(SlotChoice.At(1), Elsewhere: true, ObserveRecordedTail: true),
            new ReadBytesStep(SlotChoice.At(2), 4, 12, Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 4, Elsewhere: true, ObserveRecordedTail: true),
            new RecoverStep(Elsewhere: true),
            new AppendStep(MakePayload(25, 16)),
            new CheckStep(Elsewhere: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 4, Elsewhere: true, ObserveRecordedTail: true),
            new DropStep(),
            new ReadSlotStep(SlotChoice.At(0), Elsewhere: true, ObserveRecordedTail: true),
            new ReadRangeStep(SlotChoice.At(0), Count: 4, Elsewhere: true, ObserveRecordedTail: true),
            new RecoverStep(Elsewhere: true),
        ];

        await _leaks.InScopeAsync(async () =>
        {
            ConfigurationHarness[] harnesses =
            [
                ConfigurationHarness.Quorum("Quorum(N=1,Q=1)", replicas: 1, quorum: 1, window: 3),
                ConfigurationHarness.Quorum("Quorum(N=3,Q=2,lagging)", replicas: 3, quorum: 2, window: 3, laggingReplicaIndex: 2, lagSwitch),
                ConfigurationHarness.ObjectStore("ObjectStore(PerSlot)", ObjectLedgerLayout.PerSlot, window: 3),
                ConfigurationHarness.ObjectStore("ObjectStore(Append)", ObjectLedgerLayout.Append, window: 3),
            ];

            try
            {
                var state = new WorkloadState();
                await ExecuteStepsAsync(harnesses, phase1, state, token);
                lagSwitch.IsLagging = false;
                await ExecuteStepsAsync(harnesses, phase2, state, token);
            }
            finally
            {
                await DisposeAllAsync(harnesses);
            }
        });
    }

    private static ConfigurationHarness[] CreateHarnesses(int window) =>
    [
        ConfigurationHarness.Quorum("Quorum(N=1,Q=1)", replicas: 1, quorum: 1, window),
        ConfigurationHarness.Quorum("Quorum(N=3,Q=2)", replicas: 3, quorum: 2, window),
        ConfigurationHarness.ObjectStore("ObjectStore(PerSlot)", ObjectLedgerLayout.PerSlot, window),
        ConfigurationHarness.ObjectStore("ObjectStore(Append)", ObjectLedgerLayout.Append, window),
    ];

    private static async Task RunWorkloadAsync(int window, WorkloadStep[] steps, CancellationToken cancellationToken)
    {
        var harnesses = CreateHarnesses(window);
        try
        {
            await ExecuteStepsAsync(harnesses, steps, new WorkloadState(), cancellationToken);
        }
        finally
        {
            await DisposeAllAsync(harnesses);
        }
    }

    private static async Task ExecuteStepsAsync(
        ConfigurationHarness[] harnesses,
        WorkloadStep[] steps,
        WorkloadState state,
        CancellationToken cancellationToken)
    {
        for (var stepIndex = 0; stepIndex < steps.Length; stepIndex++)
        {
            var step = steps[stepIndex];
            if (step is DropStep && (!state.Closed || state.Dropped))
            {
                // Reclamation runs only after a recovery has closed the ledger (LG-I6, LL-I9).
                continue;
            }

            var resolved = ResolveStep(step, state.CommittedPayloads.Count);
            var answers = new StepAnswer[harnesses.Length];
            for (var h = 0; h < harnesses.Length; h++)
            {
                answers[h] = await harnesses[h].ExecuteAsync(resolved, cancellationToken);
            }

            var baseline = answers[0];
            for (var h = 1; h < harnesses.Length; h++)
            {
                AssertEquivalent(stepIndex, resolved, harnesses[0].Name, baseline, harnesses[h].Name, answers[h], state);
            }

            switch (resolved)
            {
                case ResolvedAppendStep append when baseline is AppendAnswer { Kind: AppendAnswerKind.Appended }:
                    state.CommittedPayloads.Add(append.Payload);
                    break;
                case ResolvedAppendStep when baseline is AppendAnswer { Kind: AppendAnswerKind.Sealed or AppendAnswerKind.Stopped }:
                    state.WriterStopped = true;
                    break;
                case ResolvedPipelinedAppendStep pipelined when baseline is PipelinedAppendAnswer pipelinedAnswer:
                    for (var i = 0; i < pipelinedAnswer.Items.Length; i++)
                    {
                        if (pipelinedAnswer.Items[i].Kind == AppendAnswerKind.Appended)
                        {
                            state.CommittedPayloads.Add(pipelined.Payloads[i]);
                        }
                        else
                        {
                            state.WriterStopped = true;
                        }
                    }

                    break;
                case ResolvedCheckStep { Elsewhere: false } when baseline is CheckAnswer { IsSealed: true }:
                    // LP-I14: a sealing tail read on the primary ledger stops its writer before answering.
                    state.WriterStopped = true;
                    break;
                case ResolvedRecoverStep recover when baseline is RecoverAnswer { Dropped: false, End: var end }:
                    state.Closed = true;
                    if (!recover.Elsewhere)
                    {
                        // LP-I14: RecoverAsync on the primary ledger stops its writer before its first seal request.
                        state.WriterStopped = true;
                    }

                    for (var h = 0; h < harnesses.Length; h++)
                    {
                        harnesses[h].ObserveRecordedTail(new Slot(end));
                    }

                    break;
                case ResolvedRecoverStep { Elsewhere: false }:
                    state.WriterStopped = true;
                    break;
                case ResolvedDropStep:
                    state.Dropped = true;
                    break;
                default:
                    break;
            }
        }
    }

    private static ResolvedStep ResolveStep(WorkloadStep step, int committedCount) => step switch
    {
        AppendStep append => new ResolvedAppendStep(append.Payload),
        PipelinedAppendStep pipelined => new ResolvedPipelinedAppendStep(pipelined.Payloads),
        ReadSlotStep read => new ResolvedReadSlotStep(read.Slot.Resolve(committedCount), read.Elsewhere, read.ObserveRecordedTail),
        ReadBytesStep readBytes => new ResolvedReadBytesStep(readBytes.Slot.Resolve(committedCount), readBytes.Offset, readBytes.Length, readBytes.Elsewhere, readBytes.ObserveRecordedTail),
        ReadRangeStep readRange => ResolveRange(readRange, committedCount),
        CheckStep check => new ResolvedCheckStep(check.Elsewhere),
        RecoverStep recover => new ResolvedRecoverStep(recover.Elsewhere),
        DropStep => new ResolvedDropStep(),
        _ => throw new UnreachableException(),
    };

    private static ResolvedReadRangeStep ResolveRange(ReadRangeStep step, int committedCount)
    {
        var from = step.From.Resolve(committedCount);
        return new ResolvedReadRangeStep(from, from + step.Count, step.Elsewhere, step.ObserveRecordedTail);
    }

    private static void AssertEquivalent(
        int stepIndex,
        ResolvedStep step,
        string expectedName,
        StepAnswer expected,
        string actualName,
        StepAnswer actual,
        WorkloadState state)
    {
        if (expected is CheckAnswer expectedCheck && actual is CheckAnswer actualCheck)
        {
            Assert.Equal(expectedCheck.IsSealed, actualCheck.IsSealed);

            // LL-I5: a check's reported contiguous tail lies in [0, completedSlots]; exact lag differs between quorum
            // replicas (carried contiguous tail) and object stores (newest slot object / recorded T).
            Assert.InRange(expectedCheck.Tail, 0L, state.CommittedPayloads.Count);
            Assert.InRange(actualCheck.Tail, 0L, state.CommittedPayloads.Count);
            return;
        }

        if (expected is PipelinedAppendAnswer expectedPipe && actual is PipelinedAppendAnswer actualPipe)
        {
            Assert.Equal(expectedPipe.Items.Length, actualPipe.Items.Length);
            if (state.Closed)
            {
                AssertClosedPipelinedAnswer(stepIndex, step, expectedName, expectedPipe, state.WriterStopped);
                AssertClosedPipelinedAnswer(stepIndex, step, actualName, actualPipe, state.WriterStopped);
                return;
            }

            Assert.True(
                expectedPipe.Equals(actualPipe),
                $"Step {stepIndex} ({step}): {expectedName} answered {expectedPipe}, while {actualName} answered {actualPipe}.");
            return;
        }

        if (state.Dropped
            && expected is ReadAnswer expectedRead
            && actual is ReadAnswer actualRead
            && IsValidPostDropBelowBoundRead(step, state.CommittedPayloads, expectedRead)
            && IsValidPostDropBelowBoundRead(step, state.CommittedPayloads, actualRead))
        {
            // LL-I9 / OL-I7: once reclamation completes, a retained handle reading below the recorded bound T may answer
            // either Dropped or the exact decided batch's answer (bytes or OutsideBatch(exactLength)).
            return;
        }

        if (state.Dropped
            && step is ResolvedReadRangeStep { Elsewhere: false, From: var from, Below: var below }
            && expected is ReadRangeAnswer expectedRange
            && actual is ReadRangeAnswer actualRange
            && IsValidPostDropRetainedRange(from, below, state.CommittedPayloads, expectedRange)
            && IsValidPostDropRetainedRange(from, below, state.CommittedPayloads, actualRange))
        {
            // LL-I9 / OL-I7: on a retained handle after reclamation, ReadRangeAsync may serve cached decided batches
            // below T or stop at Dropped.
            return;
        }

        Assert.True(
            expected.Equals(actual),
            $"Step {stepIndex} ({step}): {expectedName} answered {expected}, while {actualName} answered {actual}.");
    }

    private static void AssertClosedPipelinedAnswer(
        int stepIndex,
        ResolvedStep step,
        string name,
        PipelinedAppendAnswer answer,
        bool writerAlreadyStopped)
    {
        if (writerAlreadyStopped)
        {
            Assert.True(
                answer.Items.All(static item => item.Kind == AppendAnswerKind.Stopped),
                $"Step {stepIndex} ({step}): {name} had already stopped its writer, expected all Stopped, got {answer}.");
            return;
        }

        Assert.True(
            answer.Items.All(static item => item.Kind is AppendAnswerKind.Sealed or AppendAnswerKind.Stopped),
            $"Step {stepIndex} ({step}): {name} answered unexpected pipelined outcome after seal: {answer}.");
        Assert.True(
            answer.Items.Any(static item => item.Kind == AppendAnswerKind.Sealed),
            $"Step {stepIndex} ({step}): {name} answered no Sealed item on a sealed ledger: {answer}.");
    }

    private static bool IsValidPostDropBelowBoundRead(ResolvedStep step, List<byte[]> committedPayloads, ReadAnswer answer)
    {
        if (answer.Kind == ReadAnswerKind.Dropped)
        {
            return true;
        }

        switch (step)
        {
            case ResolvedReadSlotStep { Elsewhere: false, Slot: var slot } when slot >= 0 && slot < committedPayloads.Count:
                return answer.Kind == ReadAnswerKind.Batch && answer.Bytes.AsSpan().SequenceEqual(committedPayloads[(int)slot]);

            case ResolvedReadBytesStep { Elsewhere: false, Slot: var slot, Offset: var offset, Length: var length } when slot >= 0 && slot < committedPayloads.Count:
                var payload = committedPayloads[(int)slot];
                if (offset > payload.Length || payload.Length - offset < length)
                {
                    return answer.Kind == ReadAnswerKind.OutsideBatch && answer.OutsideLength == payload.Length;
                }

                return answer.Kind == ReadAnswerKind.Batch && answer.Bytes.AsSpan().SequenceEqual(payload.AsSpan(offset, length));

            default:
                return false;
        }
    }

    private static bool IsValidPostDropRetainedRange(long from, long below, List<byte[]> committedPayloads, ReadRangeAnswer answer)
    {
        if (from >= below)
        {
            return answer.Items.Length == 0;
        }

        if (answer.Items.Length == 0 || answer.Items.Length > below - from)
        {
            return false;
        }

        var maxItems = Math.Max(0, (int)Math.Min(below, committedPayloads.Count) - (int)Math.Min(from, committedPayloads.Count));
        for (var i = 0; i < answer.Items.Length; i++)
        {
            var slot = from + i;
            var item = answer.Items[i];
            if (item.Kind == ReadAnswerKind.Dropped)
            {
                return i == answer.Items.Length - 1;
            }

            if (slot < 0 || slot >= committedPayloads.Count || item.Kind != ReadAnswerKind.Batch || !item.Bytes.AsSpan().SequenceEqual(committedPayloads[(int)slot]))
            {
                return false;
            }
        }

        return below <= committedPayloads.Count && answer.Items.Length == maxItems;
    }

    private static async Task DisposeAllAsync(ConfigurationHarness[] harnesses)
    {
        List<Exception>? failures = null;
        foreach (var harness in harnesses)
        {
            try
            {
                await harness.DisposeAsync();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is { Count: 1 })
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException("One or more metamorphic configuration harnesses failed to dispose.", failures);
        }
    }

    private static byte[] MakePayload(byte seed, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = unchecked((byte)(seed * 29 + i * 13 + 3));
        }

        return bytes;
    }

    private sealed class WorkloadState
    {
        public List<byte[]> CommittedPayloads { get; } = [];
        public bool Closed { get; set; }
        public bool Dropped { get; set; }
        public bool WriterStopped { get; set; }
    }

    private enum SlotChoiceKind
    {
        Absolute,
        FromCommitted,
        NearTail,
    }

    private readonly record struct SlotChoice(SlotChoiceKind Kind, int Offset)
    {
        public static SlotChoice At(int slot) => new(SlotChoiceKind.Absolute, slot);
        public static SlotChoice FromCommitted(int index) => new(SlotChoiceKind.FromCommitted, index);
        public static SlotChoice NearTail(int delta) => new(SlotChoiceKind.NearTail, delta);

        public long Resolve(int committedCount) => Kind switch
        {
            SlotChoiceKind.Absolute => Math.Max(0, Offset),
            SlotChoiceKind.FromCommitted => committedCount == 0 ? 0 : Offset % committedCount,
            SlotChoiceKind.NearTail => Math.Max(0, committedCount + Offset),
            _ => throw new UnreachableException(),
        };
    }

    private abstract record WorkloadStep;

    private sealed record AppendStep(byte[] Payload) : WorkloadStep;

    private sealed record PipelinedAppendStep(byte[][] Payloads) : WorkloadStep;

    private sealed record ReadSlotStep(SlotChoice Slot, bool Elsewhere, bool ObserveRecordedTail) : WorkloadStep;

    private sealed record ReadBytesStep(SlotChoice Slot, int Offset, int Length, bool Elsewhere, bool ObserveRecordedTail) : WorkloadStep;

    private sealed record ReadRangeStep(SlotChoice From, int Count, bool Elsewhere, bool ObserveRecordedTail) : WorkloadStep;

    private sealed record CheckStep(bool Elsewhere) : WorkloadStep;

    private sealed record RecoverStep(bool Elsewhere) : WorkloadStep;

    private sealed record DropStep : WorkloadStep;

    private abstract record ResolvedStep;

    private sealed record ResolvedAppendStep(byte[] Payload) : ResolvedStep;

    private sealed record ResolvedPipelinedAppendStep(byte[][] Payloads) : ResolvedStep;

    private sealed record ResolvedReadSlotStep(long Slot, bool Elsewhere, bool ObserveRecordedTail) : ResolvedStep;

    private sealed record ResolvedReadBytesStep(long Slot, int Offset, int Length, bool Elsewhere, bool ObserveRecordedTail) : ResolvedStep;

    private sealed record ResolvedReadRangeStep(long From, long Below, bool Elsewhere, bool ObserveRecordedTail) : ResolvedStep;

    private sealed record ResolvedCheckStep(bool Elsewhere) : ResolvedStep;

    private sealed record ResolvedRecoverStep(bool Elsewhere) : ResolvedStep;

    private sealed record ResolvedDropStep : ResolvedStep;

    private enum AppendAnswerKind
    {
        Appended,
        Sealed,
        Unresolved,
        Stopped,
    }

    private enum ReadAnswerKind
    {
        Batch,
        Missing,
        OutsideBatch,
        Dropped,
    }

    private abstract record StepAnswer;

    private sealed record AppendAnswer(AppendAnswerKind Kind, long Slot = -1) : StepAnswer;

    private sealed record PipelinedAppendAnswer(AppendAnswer[] Items) : StepAnswer
    {
        public bool Equals(PipelinedAppendAnswer? other) =>
            other is not null && Items.SequenceEqual(other.Items);

        public override int GetHashCode() => Items.Length;

        protected override bool PrintMembers(StringBuilder builder)
        {
            _ = builder.Append("Items = [");
            for (var i = 0; i < Items.Length; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append(", ");
                }

                _ = builder.Append(Items[i]);
            }

            _ = builder.Append(']');
            return true;
        }
    }

    private sealed record ReadAnswer(ReadAnswerKind Kind, byte[]? Bytes = null, int OutsideLength = -1) : StepAnswer
    {
        public bool Equals(ReadAnswer? other) =>
            other is not null
            && Kind == other.Kind
            && OutsideLength == other.OutsideLength
            && (Bytes is null ? other.Bytes is null : other.Bytes is not null && Bytes.AsSpan().SequenceEqual(other.Bytes));

        public override int GetHashCode() => HashCode.Combine(Kind, Bytes?.Length ?? -1, OutsideLength);

        protected override bool PrintMembers(StringBuilder builder)
        {
            _ = builder.Append("Kind = ").Append(Kind);
            if (Bytes is not null)
            {
                _ = builder.Append(", Bytes(").Append(Bytes.Length).Append(") = ").Append(Convert.ToHexString(Bytes.AsSpan(0, Math.Min(16, Bytes.Length))));
                if (Bytes.Length > 16)
                {
                    _ = builder.Append("...");
                }
            }

            if (OutsideLength >= 0)
            {
                _ = builder.Append(", OutsideLength = ").Append(OutsideLength.ToString(CultureInfo.InvariantCulture));
            }

            return true;
        }
    }

    private sealed record ReadRangeAnswer(ReadAnswer[] Items) : StepAnswer
    {
        public bool Equals(ReadRangeAnswer? other) =>
            other is not null && Items.SequenceEqual(other.Items);

        public override int GetHashCode() => Items.Length;

        protected override bool PrintMembers(StringBuilder builder)
        {
            _ = builder.Append("Items = [");
            for (var i = 0; i < Items.Length; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append(", ");
                }

                _ = builder.Append(Items[i]);
            }

            _ = builder.Append(']');
            return true;
        }
    }

    private sealed record CheckAnswer(bool IsSealed, long Tail) : StepAnswer;

    private sealed record RecoverAnswer(bool Dropped, long End = -1) : StepAnswer;

    private sealed record DropAnswer : StepAnswer;

    private sealed record TornWindowResult(
        long RecoveredEnd,
        bool LateGapWriteSealed,
        ReadAnswer Slot0Read,
        ReadAnswer Slot1Read,
        ReadRangeAnswer Range0To2);

    private sealed class ReplicaLagSwitch
    {
        private int _lagging;

        public bool IsLagging
        {
            get => Volatile.Read(ref _lagging) != 0;
            set => Volatile.Write(ref _lagging, value ? 1 : 0);
        }
    }

    private sealed class LaggingLedgerReplica(ILedgerReplica inner, ReplicaLagSwitch lagSwitch) : ILedgerReplica
    {
        public ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot,
            ArcBuffer payload,
            Slot contiguousTail,
            bool recovery,
            CancellationToken cancellationToken = default) =>
            lagSwitch.IsLagging
                ? ValueTask.FromException<ReplicaWriteOutcome>(new IOException("Simulated lagging replica unavailable for write."))
                : inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot,
            Slot contiguousTail,
            CancellationToken cancellationToken = default) =>
            lagSwitch.IsLagging
                ? ValueTask.FromException<ReplicaRead>(new IOException("Simulated lagging replica unavailable for read."))
                : inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot,
            int offset,
            int length,
            Slot contiguousTail,
            CancellationToken cancellationToken = default) =>
            lagSwitch.IsLagging
                ? ValueTask.FromException<ReplicaRead>(new IOException("Simulated lagging replica unavailable for ranged read."))
                : inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(
            Slot contiguousTail,
            bool seal,
            CancellationToken cancellationToken = default) =>
            lagSwitch.IsLagging
                ? ValueTask.FromException<ReplicaTailRead>(new IOException("Simulated lagging replica unavailable for tail read."))
                : inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class ConfigurationHarness : IAsyncDisposable
    {
        private readonly Func<ILedgerStorage> _openStorage;
        private readonly LedgerLocation _location;
        private readonly Func<CancellationToken, Task> _drop;
        private readonly IDisposable? _owned;
        private readonly Ledger _primary;
        private readonly LedgerWriter _writer;
        private readonly int _window;
        private Slot? _recordedTail;

        private ConfigurationHarness(
            string name,
            int window,
            Func<ILedgerStorage> openStorage,
            LedgerLocation location,
            Func<CancellationToken, Task> drop,
            TimeProvider time,
            IDisposable? owned = null)
        {
            Name = name;
            _window = window;
            _openStorage = openStorage;
            _location = location;
            _drop = drop;
            _owned = owned;
            _primary = OpenLedger();
            _writer = _primary.CreateWriter(time);
        }

        public string Name { get; }

        public static ConfigurationHarness Quorum(
            string name,
            int replicas,
            int quorum,
            int window,
            int laggingReplicaIndex = -1,
            ReplicaLagSwitch? lagSwitch = null)
        {
            var time = new FakeTimeProvider();
            var transport = new InMemoryLedgerReplicaTransport();
            var nodes = Enumerable.Range(0, replicas)
                .Select(static i => new NodeId(new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)))
                .ToArray();
            var location = new LedgerLocation.Nodes([.. nodes]);
            var options = new LedgerReplicaOptions { WindowSize = window };

            ILedgerStorage OpenStorage()
            {
                var replicaHandles = new ILedgerReplica[nodes.Length];
                for (var i = 0; i < nodes.Length; i++)
                {
                    ILedgerReplica replica = transport.Open(nodes[i], Id, options);
                    if (i == laggingReplicaIndex && lagSwitch is not null)
                    {
                        replica = new LaggingLedgerReplica(replica, lagSwitch);
                    }

                    replicaHandles[i] = replica;
                }

                return new LedgerQuorum(replicaHandles, quorum, options, time, preferredReplica: 0);
            }

            async Task DropAsync(CancellationToken ct)
            {
                foreach (var node in nodes)
                {
                    await transport.Node(node).DropReplicaAsync(Id, ct);
                }
            }

            return new ConfigurationHarness(name, window, OpenStorage, location, DropAsync, time, transport);
        }

        public static ConfigurationHarness ObjectStore(string name, ObjectLedgerLayout layout, int window)
        {
            var time = new FakeTimeProvider();
            var objectStore = new InMemoryObjectStore();
            var ledgers = layout == ObjectLedgerLayout.Append
                ? ObjectLedgerStore.OverAppendable(
                    objectStore,
                    new ObjectLedgerStoreOptions
                    {
                        Layout = ObjectLedgerLayout.Append,
                        ExtentTargetBytes = 128,
                        ExtentMaxAppends = 2,
                    },
                    time)
                : ObjectLedgerStore.Over(
                    objectStore,
                    new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.PerSlot },
                    time);
            var sweeper = new ObjectLedgerSweeper(ledgers);

            ILedgerStorage OpenStorage() => ledgers.Open(Id, window);
            async Task DropAsync(CancellationToken ct) => _ = await sweeper.SweepAsync(static id => id == Id, NextId, ct);

            return new ConfigurationHarness(name, window, OpenStorage, LedgerLocation.ObjectStore.Instance, DropAsync, time);
        }

        public void ObserveRecordedTail(Slot recordedTail)
        {
            _recordedTail = recordedTail;
            _primary.ObserveRecordedTail(recordedTail);
        }

        public async Task<StepAnswer> ExecuteAsync(ResolvedStep step, CancellationToken cancellationToken) => step switch
        {
            ResolvedAppendStep append => await ExecuteAppendAsync(append.Payload, cancellationToken),
            ResolvedPipelinedAppendStep pipelined => await ExecutePipelinedAppendAsync(pipelined.Payloads, cancellationToken),
            ResolvedReadSlotStep read => await WithLedgerAsync(read.Elsewhere, read.ObserveRecordedTail, async ledger => NormalizeRead(await ledger.ReadAsync(new Slot(read.Slot), cancellationToken))),
            ResolvedReadBytesStep readBytes => await WithLedgerAsync(readBytes.Elsewhere, readBytes.ObserveRecordedTail, async ledger => NormalizeRead(await ledger.ReadAsync(new Slot(readBytes.Slot), readBytes.Offset, readBytes.Length, cancellationToken))),
            ResolvedReadRangeStep readRange => await WithLedgerAsync(readRange.Elsewhere, readRange.ObserveRecordedTail, ledger => ReadRangeAnswerAsync(ledger, new Slot(readRange.From), new Slot(readRange.Below), cancellationToken)),
            ResolvedCheckStep check => await WithLedgerAsync(check.Elsewhere, observeRecordedTail: false, ledger => ReadTailAnswerAsync(ledger, cancellationToken)),
            ResolvedRecoverStep recover => await WithLedgerAsync(recover.Elsewhere, observeRecordedTail: false, ledger => RecoverAnswerAsync(ledger, cancellationToken)),
            ResolvedDropStep => await ExecuteDropAsync(cancellationToken),
            _ => throw new UnreachableException(),
        };

        public async Task<TornWindowResult> RunTornWindowAsync(
            byte[] payload0,
            byte[] payload1Late,
            byte[] payload2,
            CancellationToken cancellationToken)
        {
            await using var writerStorage = _openStorage();
            using (var buffer0 = ArcBuffer.Wrap(payload0))
            {
                var outcome0 = await writerStorage.WriteAsync(Slot.Zero, buffer0, Slot.Zero, cancellationToken);
                Assert.IsType<ReplicaWriteOutcome.Written>(outcome0.Value);
            }

            // Submit slot 2 while leaving slot 1 unwritten (a torn K=3 pipelined window). On quorum and per-slot
            // storages slot 2 lands immediately; on append storage it queues waiting for slot 1 until slot 1 arrives
            // and the flush meets the seal.
            using var slot2Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var slot2Task = WriteSlotRawAsync(writerStorage, new Slot(2), payload2, new Slot(1), slot2Cts.Token);
            var slot2Joined = false;
            try
            {
                await using var taker = OpenLedger();
                var recovery = await taker.RecoverAsync(cancellationToken);
                var recovered = Assert.IsType<RecoveryOutcome.Recovered>(recovery.Value);
                taker.ObserveRecordedTail(recovered.End);

                bool lateGapWriteSealed;
                using (var buffer1 = ArcBuffer.Wrap(payload1Late))
                {
                    var lateOutcome = await writerStorage.WriteAsync(new Slot(1), buffer1, new Slot(1), cancellationToken);
                    lateGapWriteSealed = lateOutcome is ReplicaWriteOutcome.Sealed;
                }

                // Wait for the torn slot-2 write to settle (Written on quorum/per-slot, Sealed on append).
                slot2Joined = true;
                var slot2Outcome = await slot2Task;
                Assert.True(
                    slot2Outcome is ReplicaWriteOutcome.Written or ReplicaWriteOutcome.Sealed,
                    $"{Name}: unexpected torn slot-2 outcome {slot2Outcome}.");

                var slot0Read = NormalizeRead(await taker.ReadAsync(Slot.Zero, cancellationToken));
                var slot1Read = NormalizeRead(await taker.ReadAsync(new Slot(1), cancellationToken));
                var range = await ReadRangeAnswerAsync(taker, Slot.Zero, new Slot(2), cancellationToken);

                return new TornWindowResult(recovered.End.Value, lateGapWriteSealed, slot0Read, slot1Read, range);
            }
            finally
            {
                if (!slot2Joined)
                {
                    await slot2Cts.CancelAsync();
                    try
                    {
                        _ = await slot2Task;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        public async Task VerifyRacingRecoveryAsync(byte[][] payloads, CancellationToken cancellationToken)
        {
            var results = new List<(Slot Slot, AppendAnswer Answer)>(payloads.Length);
            var writerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var writerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var writerToken = writerCts.Token;

            var writerTask = Task.Run(async () =>
            {
                var pending = new List<(Slot Slot, Task<AppendAnswer> Task)>(payloads.Length);
                try
                {
                    for (var i = 0; i < payloads.Length; i++)
                    {
                        var acquired = await _writer.AcquireAsync(writerToken);
                        if (acquired is not { } slot)
                        {
                            break;
                        }

                        pending.Add((slot, AppendSlotAsync(slot, payloads[i], writerToken)));
                        if (i == 1)
                        {
                            _ = writerGate.TrySetResult();
                        }

                        await Task.Yield();
                    }
                }
                finally
                {
                    _ = writerGate.TrySetResult();
                    Exception? pendingFault = null;
                    foreach (var (slot, task) in pending)
                    {
                        try
                        {
                            results.Add((slot, await task));
                        }
                        catch (OperationCanceledException) when (writerToken.IsCancellationRequested)
                        {
                        }
                        catch (Exception ex)
                        {
                            pendingFault ??= ex;
                        }
                    }

                    if (pendingFault is not null)
                    {
                        ExceptionDispatchInfo.Capture(pendingFault).Throw();
                    }
                }
            }, writerToken);

            var writerJoined = false;
            RecoveryOutcome.Recovered recovered;
            try
            {
                await writerGate.Task.WaitAsync(cancellationToken);
                await using var taker = OpenLedger();
                var recovery = await taker.RecoverAsync(cancellationToken);
                recovered = Assert.IsType<RecoveryOutcome.Recovered>(recovery.Value);
                writerJoined = true;
                await writerTask;
            }
            finally
            {
                if (!writerJoined)
                {
                    await writerCts.CancelAsync();
                    try
                    {
                        await writerTask;
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            var end = recovered.End.Value;
            Assert.InRange(end, 0L, payloads.Length);
            foreach (var (slot, answer) in results)
            {
                if (answer.Kind == AppendAnswerKind.Appended)
                {
                    Assert.True(slot.Value < end, $"{Name}: slot {slot.Value} answered Appended but recovery ended at {end}.");
                }
            }

            // Cold reader verifies every slot below T and absence at T.
            await using var reader = OpenLedger();
            reader.ObserveRecordedTail(recovered.End);
            for (var s = 0L; s < end; s++)
            {
                var read = NormalizeRead(await reader.ReadAsync(new Slot(s), cancellationToken));
                Assert.Equal(new ReadAnswer(ReadAnswerKind.Batch, payloads[(int)s]), read);
            }

            var expectedRange = new ReadRangeAnswer(
                [.. Enumerable.Range(0, (int)end).Select(i => new ReadAnswer(ReadAnswerKind.Batch, payloads[i]))]);
            var actualRange = await ReadRangeAnswerAsync(reader, Slot.Zero, recovered.End, cancellationToken);
            Assert.Equal(expectedRange, actualRange);

            var atEnd = NormalizeRead(await reader.ReadAsync(recovered.End, cancellationToken));
            Assert.Equal(new ReadAnswer(ReadAnswerKind.Missing), atEnd);

            var afterSeal = await ExecuteAppendAsync(MakePayload(250, 16), cancellationToken);
            Assert.True(
                afterSeal.Kind is AppendAnswerKind.Sealed or AppendAnswerKind.Stopped,
                $"{Name}: expected Sealed/Stopped after takeover, got {afterSeal}.");
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
            await _primary.DisposeAsync();
            _owned?.Dispose();
        }

        private Ledger OpenLedger() => new(Id, _openStorage(), _location);

        private async Task<T> WithLedgerAsync<T>(bool elsewhere, bool observeRecordedTail, Func<Ledger, Task<T>> action)
        {
            if (!elsewhere)
            {
                return await action(_primary);
            }

            await using var ledger = OpenLedger();
            if (observeRecordedTail && _recordedTail is { } tail)
            {
                ledger.ObserveRecordedTail(tail);
            }

            return await action(ledger);
        }

        private async Task<DropAnswer> ExecuteDropAsync(CancellationToken cancellationToken)
        {
            await _drop(cancellationToken);
            return new DropAnswer();
        }

        private async Task<AppendAnswer> ExecuteAppendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            var slot = await _writer.AcquireAsync(cancellationToken);
            if (slot is not { } acquired)
            {
                return new AppendAnswer(AppendAnswerKind.Stopped);
            }

            return await AppendSlotAsync(acquired, payload, cancellationToken);
        }

        private async Task<PipelinedAppendAnswer> ExecutePipelinedAppendAsync(byte[][] payloads, CancellationToken cancellationToken)
        {
            var count = Math.Min(payloads.Length, _window);
            var tasks = new Task<AppendAnswer>[count];
            for (var i = 0; i < count; i++)
            {
                var acquired = await _writer.AcquireAsync(cancellationToken);
                tasks[i] = acquired is { } slot
                    ? AppendSlotAsync(slot, payloads[i], cancellationToken)
                    : Task.FromResult(new AppendAnswer(AppendAnswerKind.Stopped));
            }

            var items = await Task.WhenAll(tasks);
            return new PipelinedAppendAnswer(items);
        }

        private async Task<AppendAnswer> AppendSlotAsync(Slot slot, byte[] payload, CancellationToken cancellationToken)
        {
            using var buffer = ArcBuffer.Wrap(payload);
            var outcome = await _writer.AppendAsync(slot, buffer, cancellationToken);
            return NormalizeAppend(slot, outcome);
        }

        private static async Task<LedgerWriteOutcome> WriteSlotRawAsync(
            ILedgerStorage storage,
            Slot slot,
            byte[] payload,
            Slot acknowledged,
            CancellationToken cancellationToken)
        {
            using var buffer = ArcBuffer.Wrap(payload);
            return await storage.WriteAsync(slot, buffer, acknowledged, cancellationToken);
        }

        private static async Task<ReadRangeAnswer> ReadRangeAnswerAsync(
            Ledger ledger,
            Slot from,
            Slot below,
            CancellationToken cancellationToken)
        {
            List<ReadAnswer> items = [];
            await foreach (var read in ledger.ReadRangeAsync(from, below, cancellationToken: cancellationToken))
            {
                items.Add(NormalizeRead(read));
            }

            return new ReadRangeAnswer([.. items]);
        }

        private static async Task<CheckAnswer> ReadTailAnswerAsync(Ledger ledger, CancellationToken cancellationToken)
        {
            var outcome = await ledger.ReadTailAsync(cancellationToken);
            return outcome switch
            {
                LedgerTail tail => new CheckAnswer(tail.IsSealed, tail.ContiguousTail.Value),
                ReplicaWriteOutcome.Backpressure => throw new InvalidOperationException("Unexpected Backpressure in metamorphic check."),
                null => throw new UnreachableException(),
            };
        }

        private static async Task<RecoverAnswer> RecoverAnswerAsync(Ledger ledger, CancellationToken cancellationToken)
        {
            var outcome = await ledger.RecoverAsync(cancellationToken);
            return outcome switch
            {
                RecoveryOutcome.Recovered recovered => new RecoverAnswer(Dropped: false, recovered.End.Value),
                ReplicaWriteOutcome.Dropped => new RecoverAnswer(Dropped: true),
                null => throw new UnreachableException(),
            };
        }

        private static AppendAnswer NormalizeAppend(Slot slot, AppendOutcome outcome) => outcome switch
        {
            AppendOutcome.Appended => new AppendAnswer(AppendAnswerKind.Appended, slot.Value),
            AppendOutcome.Sealed => new AppendAnswer(AppendAnswerKind.Sealed),
            AppendOutcome.Unresolved => new AppendAnswer(AppendAnswerKind.Unresolved),
            _ => throw new UnreachableException(),
        };

        private static ReadAnswer NormalizeRead(ReplicaRead read) => read switch
        {
            ArcBuffer batch => ReadBatch(batch),
            ReplicaRead.Missing => new ReadAnswer(ReadAnswerKind.Missing),
            ReplicaRead.OutsideBatch outside => new ReadAnswer(ReadAnswerKind.OutsideBatch, OutsideLength: outside.Length),
            ReplicaWriteOutcome.Dropped => new ReadAnswer(ReadAnswerKind.Dropped),
            ReplicaWriteOutcome.Backpressure => throw new InvalidOperationException("Unexpected Backpressure in metamorphic read."),
            null => throw new UnreachableException(),
        };

        private static ReadAnswer ReadBatch(ArcBuffer batch)
        {
            using (batch)
            {
                return new ReadAnswer(ReadAnswerKind.Batch, batch.ToArray());
            }
        }
    }
}

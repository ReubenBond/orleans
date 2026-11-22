using System.Globalization;
using Microsoft.Accordant;

namespace Orleans.Durable.Storage.Testing;

#pragma warning disable CA1852 // Accordant generates a virtual clone factory for state types.

/// <summary>
/// The abstract state of one ledger as its storage's answers reveal it through the primary's <c>Ledger</c> and
/// <c>LedgerWriter</c>, and through other processes' <c>Ledger</c>s (ledger-protocol.md §2.7, LL-I1 to LL-I9).
/// </summary>
[State]
internal partial class LedgerState
{
    /// <summary>
    /// Gets or sets the next slot the writer's lifetime grants.
    /// </summary>
    public long Next { get; set; }

    /// <summary>
    /// Gets or sets the writer's completed prefix: every slot below it answered <c>Written</c> (LL-I2, LP-I3).
    /// </summary>
    public long Completed { get; set; }

    /// <summary>
    /// Gets or sets the value of each slot the lifetime granted, by slot: the value of the batch an append wrote or
    /// attempted to write there, or <c>-1</c> for an append whose write never landed. A value at or past the completed
    /// prefix is the batch of an append which did not complete: either an unanswered append whose write landed, or a
    /// sealed append listed in <see cref="Partial"/> whose write may have landed on an unsealed minority replica.
    /// </summary>
    public List<int> Values { get; set; } = [];

    /// <summary>
    /// Gets or sets the slots whose append met a seal after sending its write: the batch in <see cref="Values"/> may
    /// have landed on up to <c>Q − 1</c> unsealed minority replicas or on none, so a read there may answer either that
    /// batch or <c>Missing</c> (ledger-protocol.md §6.2, §8.3).
    /// </summary>
    public List<long> Partial { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a recovery of the ledger completed, in any process.
    /// </summary>
    public bool Sealed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the writer's lifetime is stopped (LP-I13, LP-I14).
    /// </summary>
    public bool Stopped { get; set; }

    /// <summary>
    /// Gets or sets the candidate end <c>T</c> of the ledger's first completed recovery, which the ledger map records, or
    /// <c>-1</c> before one.
    /// </summary>
    public long End { get; set; } = -1;

    /// <summary>
    /// Gets or sets a value indicating whether the primary's process learned the ledger map's recorded <c>T</c>.
    /// </summary>
    public bool Recorded { get; set; }

    /// <summary>
    /// Gets or sets the greatest tail a check or a scan reported, which every later candidate end is at least (LL-I7).
    /// </summary>
    public long Reported { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the reclamation of the ledger's storage began: its first durable step
    /// was taken (LL-I9).
    /// </summary>
    public bool Reclaiming { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the reclamation of the ledger's storage completed (LL-I9).
    /// </summary>
    public bool Reclaimed { get; set; }
}

#pragma warning restore CA1852

/// <summary>
/// An append of <see cref="LedgerStorageSpec.Payload"/> of <paramref name="Value"/> through the primary's writer.
/// </summary>
internal sealed record LedgerAppend(byte Value)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Append({Value})");
}

/// <summary>
/// An append of <see cref="LedgerStorageSpec.Payload"/> of <paramref name="Value"/> through the primary's writer whose
/// outcome is unknown (ledger-protocol.md §2.4, LL-I8): its write landed while every answer to it was lost when
/// <paramref name="Landed"/>, and otherwise failed without landing.
/// </summary>
internal sealed record LedgerAppendUnanswered(byte Value, bool Landed = true)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"AppendUnanswered({Value}{(Landed ? string.Empty : ", unlanded")})");
}

/// <summary>How an acquisition through the primary's writer uses its slot (ledger-protocol.md §2.3).</summary>
internal enum AcquireUse
{
    /// <summary>Appends at the acquired slot, and then again at it, which the writer refuses.</summary>
    AppendTwice,

    /// <summary>Appends at a slot the writer never acquired, which it refuses.</summary>
    AppendForeign,

    /// <summary>Acquires with a cancelled token, which acquires nothing.</summary>
    Cancelled,
}

/// <summary>
/// An acquisition of a slot through the primary's writer, which <paramref name="Use"/> uses, appending
/// <see cref="LedgerStorageSpec.Payload"/> of <paramref name="Value"/>.
/// </summary>
internal sealed record LedgerAcquire(byte Value, AcquireUse Use)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Acquire({Value}, {Use})");
}

/// <summary>
/// A read of <paramref name="Slot"/> through the primary's <c>Ledger</c>, carrying its bound.
/// </summary>
internal sealed record LedgerSlotRead(long Slot)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Read({Slot})");
}

/// <summary>
/// A read of bytes <c>[Offset, Offset + Length)</c> of the batch at <paramref name="Slot"/> through the primary's
/// <c>Ledger</c>.
/// </summary>
internal sealed record LedgerRangeRead(long Slot, int Offset, int Length)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"ReadRange({Slot}, {Offset}, {Length})");
}

/// <summary>
/// A scan through the primary's <c>Ledger</c> of every slot from <paramref name="From"/> below its bound when the scan
/// begins, as a drain or a reader's scan reads them (ledgers.md §2.5).
/// </summary>
internal sealed record LedgerScan(long From)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Scan({From})");
}

/// <summary>
/// A seal check: the primary's open check, through its <c>Ledger</c> (ledgers.md §8.3), or, when
/// <paramref name="Elsewhere"/>, a reader's check through a <c>Ledger</c> of another process, which knows no tail.
/// </summary>
internal sealed record LedgerCheck(bool Elsewhere = false)
{
    public override string ToString() => Elsewhere ? "CheckElsewhere" : "Check";
}

/// <summary>
/// A contiguous-tail learn without a seal check (ledger-protocol.md §8.2): the primary's, through its <c>Ledger</c>, or,
/// when <paramref name="Elsewhere"/>, a reader's through a <c>Ledger</c> of another process on a replica's node, which
/// knows no tail.
/// </summary>
internal sealed record LedgerLearn(bool Elsewhere = false)
{
    public override string ToString() => Elsewhere ? "LearnElsewhere" : "Learn";
}

/// <summary>
/// A recovery: through the primary's writer, or, when <paramref name="Elsewhere"/>, through a <c>Ledger</c> of another
/// process, as a transition's executor recovers.
/// </summary>
internal sealed record LedgerRecover(bool Elsewhere)
{
    public override string ToString() => Elsewhere ? "RecoverElsewhere" : "Recover";
}

/// <summary>
/// The primary's process learns the ledger map's recorded <c>T</c>, the end of the ledger's first completed recovery.
/// </summary>
internal sealed record LedgerObserve
{
    public static LedgerObserve Instance { get; } = new();

    public override string ToString() => "Observe";
}

/// <summary>
/// The reclamation of the ledger's storage once the map revoked the closed ledger (LL-I9): its first durable step, or,
/// when <paramref name="Every"/>, every step, after which no copy of the ledger remains.
/// </summary>
internal sealed record LedgerDrop(bool Every)
{
    public override string ToString() => Every ? "DropEvery" : "DropOne";
}

/// <summary>
/// The kinds of <see cref="LedgerOutcome"/>.
/// </summary>
internal enum LedgerOutcomeKind
{
    /// <summary>An append's slot completed.</summary>
    Appended,

    /// <summary>An append's write met a seal.</summary>
    Sealed,

    /// <summary>An append acquired no slot, or its write or completion failed once the write began.</summary>
    Unresolved,

    /// <summary>A read found a batch.</summary>
    Batch,

    /// <summary>A read found no batch.</summary>
    Missing,

    /// <summary>A read, a scan's read, or a recovery found the ledger's storage reclaimed.</summary>
    Dropped,

    /// <summary>A byte-range read named a range past its slot's batch, which the read answered <c>OutsideBatch</c>.</summary>
    RangeRefused,

    /// <summary>A scan's batches.</summary>
    Scanned,

    /// <summary>A check's answer.</summary>
    Tail,

    /// <summary>A learn's contiguous tail.</summary>
    Learned,

    /// <summary>A recovery's candidate end.</summary>
    End,

    /// <summary>The recorded <c>T</c> was learned.</summary>
    Observed,

    /// <summary>The ledger's storage was reclaimed, in part or whole.</summary>
    Reclaimed,

    /// <summary>An append at a slot the writer did not acquire, or already appended at, was refused.</summary>
    Refused,

    /// <summary>A cancelled acquisition acquired nothing.</summary>
    Cancelled,

    /// <summary>A second append at a slot the writer had already appended at was accepted.</summary>
    AppendedTwice,
}

/// <summary>
/// What a call answered, and what a recording of an implementation's requests shows of it when the harness keeps one.
/// </summary>
/// <param name="Kind">The answer's kind.</param>
/// <param name="Value">
/// An append's slot (<c>-1</c> for one which acquired none), a check's tail, a recovery's end, or the bound a scan read
/// below.
/// </param>
/// <param name="IsSealed">Whether a check found the ledger sealed.</param>
/// <param name="Bytes">The batch, or the range of it, a read found.</param>
/// <param name="LifetimeStopped">Whether the writer's lifetime is stopped after the call.</param>
/// <param name="OverlappedAppend">Whether another append overlapped the call in real time, as the harness observed.</param>
/// <param name="StoppedBySeal">Whether the writer's lifetime stopped for a seal.</param>
/// <param name="Batches">
/// A scan's answers, by slot from its first: each slot's batch, or <see langword="null"/> where the read found the
/// ledger's storage reclaimed.
/// </param>
/// <param name="Recording">What the recording of the call's requests shows, when the harness keeps one.</param>
internal sealed record LedgerOutcome(
    LedgerOutcomeKind Kind,
    long Value = 0,
    bool IsSealed = false,
    byte[]? Bytes = null,
    bool LifetimeStopped = false,
    bool OverlappedAppend = false,
    bool StoppedBySeal = false,
    IReadOnlyList<byte[]?>? Batches = null,
    LedgerRecording? Recording = null)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Kind}({Value}{(IsSealed ? ", sealed" : string.Empty)}){(Batches is { } batches ? string.Create(CultureInfo.InvariantCulture, $" of {batches.Count}") : string.Empty)}{(LifetimeStopped ? ", lifetime stopped" : string.Empty)}{(Recording is { } recording ? " " + recording : string.Empty)}");
}

/// <summary>
/// What the expectations a recording adds to the contract's require of an outcome the contract allows.
/// </summary>
/// <param name="Holds">Whether the outcome's recording shows what the rule requires.</param>
/// <param name="Rule">The rule, as the spec reports it.</param>
internal sealed record LedgerJudgment(Func<LedgerOutcome, bool> Holds, string Rule)
{
    /// <summary>Gets the judgment of a harness which keeps no recording.</summary>
    public static LedgerJudgment None { get; } = new(static _ => true, string.Empty);
}

/// <summary>
/// The executable spec of the ledger storage contract (ledger-protocol.md §2.7, LL-I1 to LL-I9) as one ledger's
/// processes use it through the shared front: the primary's <c>Ledger</c> and <c>LedgerWriter</c>, and other processes'
/// <c>Ledger</c>s, which check and recover the ledger. It judges answers alone, so it holds for every implementation of
/// <c>ILedgerStorage</c>; an implementation's oracle adds what a recording of its requests must show
/// (<see cref="LedgerQuorumOracle"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every append completes in slot order while the ledger is open (LP-I3, LL-I2); one whose write meets a completed
/// recovery's seal reports it and stops the lifetime (LL-I4, LP-I8); one whose outcome is unknown reports its slot
/// unresolved and stops the lifetime, its batch landed or not (LL-I8, LP-I13); and once the lifetime is stopped an
/// append acquires nothing. A read returns only the batch an append wrote at its slot (LL-I1): below the front's bound
/// that batch, never <c>Missing</c> (LL-I6), and at or above it that batch or <c>Missing</c>, which decides nothing. A
/// check reports sealed exactly when a recovery completed (LL-I4, LL-I5), with a tail at most every candidate end
/// (LL-I7). A recovery returns a candidate end at or above the completed prefix and every tail reported before it, below
/// which every slot holds a batch (LL-I2, LL-I3, LL-I7); the writer's process stops its lifetime first (LP-I14).
/// </para>
/// <para>
/// Once the map has revoked the closed ledger, its storage is reclaimed (LL-I9): from the first durable step every
/// operation answers <c>Dropped</c> or as it would have before, and once the reclamation completes, <c>Dropped</c> or a
/// batch below <c>T</c>; no write is ever <c>Written</c>.
/// </para>
/// </remarks>
internal static class LedgerStorageSpec
{
    /// <summary>The name of the append operation.</summary>
    internal const string Append = "Append";

    /// <summary>The name of the append whose outcome is unknown.</summary>
    internal const string AppendUnanswered = "AppendUnanswered";

    /// <summary>The name of the read operation.</summary>
    internal const string Read = "Read";

    /// <summary>The name of the byte-range read operation.</summary>
    internal const string ReadRange = "ReadRange";

    /// <summary>The name of the scan operation.</summary>
    internal const string Scan = "Scan";

    /// <summary>The name of the seal check.</summary>
    internal const string Check = "Check";

    /// <summary>The name of the contiguous-tail learn.</summary>
    internal const string Learn = "Learn";

    /// <summary>The name of the recovery operation.</summary>
    internal const string Recover = "Recover";

    /// <summary>The name of the operation which learns the recorded <c>T</c>.</summary>
    internal const string Observe = "Observe";

    /// <summary>The name of the acquisition operation.</summary>
    internal const string Acquire = "Acquire";

    /// <summary>The name of the reclamation of the ledger's storage.</summary>
    internal const string Drop = "Drop";

    /// <summary>
    /// Returns the batch an append of <paramref name="value"/> writes: <paramref name="value"/> repeated, from 1 to 64
    /// times.
    /// </summary>
    internal static byte[] Payload(byte value) => [.. Enumerable.Repeat(value, 1 + value * 37 % 64)];

    /// <summary>
    /// Creates the spec, whose every expectation also requires what <paramref name="recording"/> judges of the call's
    /// recording, when given.
    /// </summary>
    internal static Spec<LedgerState> Create(Func<object, LedgerState, LedgerJudgment>? recording = null)
    {
        var judge = recording ?? (static (_, _) => LedgerJudgment.None);
        var spec = new Spec<LedgerState>();
        spec.Operation<LedgerAppend, LedgerOutcome>(Append, (request, state) => AppendOutcome(request, state, judge(request, state)));
        spec.Operation<LedgerAppendUnanswered, LedgerOutcome>(AppendUnanswered, (request, state) => AppendUnansweredOutcome(request, state, judge(request, state)));
        spec.Operation<LedgerSlotRead, LedgerOutcome>(Read, (request, state) => ReadOutcome(request, state, judge(request, state)));
        spec.Operation<LedgerRangeRead, LedgerOutcome>(ReadRange, (request, state) => RangeReadOutcome(request, state, judge(request, state)));
        spec.Operation<LedgerScan, LedgerOutcome>(Scan, (request, state) => ScanOutcome(request, state, judge(request, state)));
        spec.Operation<LedgerCheck, LedgerOutcome>(Check, (request, state) => CheckOutcome(request, state, judge(request, state)));
        spec.Operation<LedgerLearn, LedgerOutcome>(Learn, (request, state) => LearnOutcome(request, state, judge(request, state)));
        spec.Operation<LedgerRecover, LedgerOutcome>(Recover, (request, state) => RecoverOutcome(request, state, judge(request, state)));
        spec.Operation<LedgerObserve, LedgerOutcome>(Observe, (request, state) => ObserveOutcome(state, judge(request, state)));
        spec.Operation<LedgerAcquire, LedgerOutcome>(Acquire, (request, state) => AcquireOutcome(request, state, judge(request, state)));
        spec.Operation<LedgerDrop, LedgerOutcome>(Drop, (request, state) => DropOutcome(request, judge(request, state)));
        return spec;
    }

    /// <summary>
    /// Returns whether the caller may issue <paramref name="request"/> in <paramref name="state"/>: the map records a
    /// <c>T</c> only once a recovery completed, and revokes the ledger, which reclaims its storage, only once it closed
    /// (LG-I9, LL-I9).
    /// </summary>
    internal static bool IsCallerAllowed(object request, LedgerState state) =>
        request is not (LedgerObserve or LedgerDrop) || state.End >= 0;

    /// <summary>
    /// Returns the front's bound in the primary's process, below which every slot is decided: its completed prefix, or
    /// the recorded <c>T</c> once the process learned it.
    /// </summary>
    internal static long Bound(LedgerState state) => state.Recorded ? Math.Max(state.Completed, state.End) : state.Completed;

    /// <summary>
    /// Returns the greatest tail any process's bound may reach (LL-I7): the recorded <c>T</c> once a recovery completed,
    /// and before one, the end of the batches written from slot zero, the greatest candidate end a recovery may return.
    /// </summary>
    internal static long Ceiling(LedgerState state) => state.End >= 0 ? state.End : WrittenEnd(state);

    /// <summary>
    /// Returns the end of the batches written from slot zero: every slot below the completed prefix holds its batch, and
    /// past it an unresolved append whose write landed or a sealed append whose write may have landed on an unsealed
    /// minority replica can still be found by recovery, while a slot whose write never landed holds none.
    /// </summary>
    internal static long WrittenEnd(LedgerState state)
    {
        var end = state.Completed;
        while (end < state.Values.Count && state.Values[(int)end] >= 0)
        {
            end++;
        }

        return end;
    }

    /// <summary>Returns the batch an append wrote at <paramref name="slot"/>, or <see langword="null"/> when none did.</summary>
    internal static byte[]? Written(LedgerState state, long slot) =>
        slot >= 0 && slot < state.Values.Count && state.Values[(int)slot] is var value and >= 0 ? Payload((byte)value) : null;

    /// <summary>
    /// Returns whether <paramref name="slot"/>'s append met a seal after sending its write, so its batch may reside on
    /// an unsealed minority replica or on none (ledger-protocol.md §6.2, §8.3).
    /// </summary>
    internal static bool IsPartial(LedgerState state, long slot) => state.Partial.Contains(slot);

    private static ExpectedOutcomeBuilder<LedgerOutcome> Judged(Func<LedgerOutcome, bool> holds, string rule, LedgerJudgment judgment) =>
        Expect.That<LedgerOutcome>(outcome => holds(outcome) && judgment.Holds(outcome), rule + judgment.Rule);

    private static ExpectedOutcomes AppendOutcome(LedgerAppend request, LedgerState state, LedgerJudgment judgment)
    {
        var slot = state.Next;
        if (state.Stopped)
        {
            // An append which overlapped another may have acquired its slot before that one's write met the seal and
            // stopped the lifetime, and then either meets the seal too or writes its batch before the fence reaches its
            // slot and stops when CompleteSlotAsync sees the stopped lifetime (LP-I3, LP-I13).
            return Expect.OneOf(
                Judged(
                    static outcome => outcome is { Kind: LedgerOutcomeKind.Unresolved, Value: -1 },
                    "LP-I13: once the lifetime is stopped, an append acquires no slot and writes nothing.",
                    judgment).SameState(),
                MeetsTheSeal(slot, request.Value, overlapping: true, judgment),
                CompletionStopped(slot, request.Value, requireSealStop: false, judgment));
        }

        if (state.Sealed)
        {
            return MeetsTheSeal(slot, request.Value, overlapping: false, judgment);
        }

        return Expect.OneOf(
            Judged(
                    outcome => outcome.Kind == LedgerOutcomeKind.Appended && outcome.Value == slot,
                    string.Create(CultureInfo.InvariantCulture, $"LP-I3, LL-I2: an append to the open ledger completes at the next slot, {slot}."),
                    judgment)
                .ThenState<LedgerState>(next =>
                {
                    next.Values.Add(request.Value);
                    next.Next++;
                    next.Completed++;
                }),
            CompletionStopped(slot, request.Value, requireSealStop: true, judgment));
    }

    /// <summary>
    /// ledger-protocol.md §2.3: an append names a slot its writer acquired and has not appended, so a second append at a
    /// slot, or one at a slot never acquired, is refused and writes nothing; a cancelled acquisition acquires nothing. An
    /// acquisition and its first append behave as one append does.
    /// </summary>
    private static ExpectedOutcomes AcquireOutcome(LedgerAcquire request, LedgerState state, LedgerJudgment judgment) =>
        request.Use switch
        {
            AcquireUse.AppendTwice => AppendOutcome(new LedgerAppend(request.Value), state, judgment),
            AcquireUse.AppendForeign => Judged(
                static outcome => outcome.Kind == LedgerOutcomeKind.Refused,
                "An append at a slot its writer never acquired is refused and writes nothing.",
                judgment).SameState(),
            AcquireUse.Cancelled => Judged(
                static outcome => outcome.Kind == LedgerOutcomeKind.Cancelled,
                "A cancelled acquisition acquires nothing and writes nothing.",
                judgment).SameState(),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

    private static ExpectedOutcomes AppendUnansweredOutcome(LedgerAppendUnanswered request, LedgerState state, LedgerJudgment judgment)
    {
        if (state.Stopped || state.Sealed && request.Landed)
        {
            // A write which lands and meets the seal answers it; once the lifetime is stopped nothing is acquired.
            return AppendOutcome(new LedgerAppend(request.Value), state, judgment);
        }

        var unresolved = Unresolved(request, state.Next, judgment);
        return state.Sealed
            ? Expect.OneOf(MeetsTheSeal(state.Next, value: -1, overlapping: false, judgment), unresolved)
            : unresolved;
    }

    private static ExpectedOutcome Unresolved(LedgerAppendUnanswered request, long slot, LedgerJudgment judgment) =>
        Judged(
                outcome => outcome is { Kind: LedgerOutcomeKind.Unresolved, LifetimeStopped: true } && outcome.Value == slot,
                string.Create(CultureInfo.InvariantCulture, $"LL-I8: an append whose outcome is unknown reports Unresolved({slot}), since a recovery may still find its batch, and stops the lifetime (LP-I13)."),
                judgment)
            .ThenState<LedgerState>(next =>
            {
                next.Values.Add(request.Landed ? request.Value : -1);
                next.Next++;
                next.Stopped = true;
            });

    private static ExpectedOutcome MeetsTheSeal(long slot, int value, bool overlapping, LedgerJudgment judgment) =>
        Judged(
                outcome => outcome.Kind == LedgerOutcomeKind.Sealed
                    && outcome.Value == slot
                    && outcome.LifetimeStopped
                    && (!overlapping || outcome.OverlappedAppend),
                string.Create(CultureInfo.InvariantCulture, $"LL-I4, LP-I8: an append at {slot} meets the completed recovery's seal, or the reclamation which counts as one, reports Sealed({slot}), and stops the lifetime."),
                judgment)
            .ThenState<LedgerState>(next =>
            {
                next.Values.Add(value);
                if (value >= 0)
                {
                    next.Partial.Add(slot);
                }

                next.Next++;
                next.Stopped = true;
            });

    private static ExpectedOutcome CompletionStopped(long slot, int value, bool requireSealStop, LedgerJudgment judgment) =>
        Judged(
                outcome => outcome is { Kind: LedgerOutcomeKind.Unresolved, LifetimeStopped: true, OverlappedAppend: true }
                    && outcome.Value == slot
                    && (!requireSealStop || outcome.StoppedBySeal),
                string.Create(CultureInfo.InvariantCulture, $"LP-I3, LP-I13: an overlapping append whose batch landed at {slot} stops at completion once a concurrent slot meets a seal, reporting Unresolved({slot}) and leaving the lifetime stopped."),
                judgment)
            .ThenState<LedgerState>(next =>
            {
                next.Values.Add(value);
                next.Next++;
                next.Stopped = true;
            });

    private static bool LifetimeMatches(LedgerOutcome outcome, bool elsewhere, bool expectedStopped) =>
        outcome.LifetimeStopped == expectedStopped
        || elsewhere && outcome.OverlappedAppend && !outcome.LifetimeStopped;

    /// <summary>
    /// Returns whether a read of <paramref name="slot"/> may answer <paramref name="outcome"/>: the slot's batch, of which
    /// <paramref name="matches"/> judges the bytes; <c>Missing</c> at or above the bound or where no batch was written; and
    /// <c>Dropped</c> once the reclamation began, before which a revoked ledger has no reclaimed storage.
    /// </summary>
    private static bool ReadAnswers(LedgerOutcome outcome, LedgerState state, long slot, Func<byte[], bool> matches) =>
        outcome is { Kind: LedgerOutcomeKind.Batch, Bytes: { } bytes }
            ? matches(bytes) && (!state.Reclaimed || slot < state.End)
            : outcome.Kind == LedgerOutcomeKind.Missing
                ? !state.Reclaimed && (slot >= Bound(state) || Written(state, slot) is null)
                : outcome.Kind == LedgerOutcomeKind.Dropped && state.Reclaiming;

    private static ExpectedOutcome ReadOutcome(LedgerSlotRead request, LedgerState state, LedgerJudgment judgment)
    {
        var written = Written(state, request.Slot);
        return Judged(
            outcome => ReadAnswers(outcome, state, request.Slot, bytes => written is not null && bytes.AsSpan().SequenceEqual(written)),
            string.Create(CultureInfo.InvariantCulture, $"LL-I1, LL-I6: a read of slot {request.Slot} returns the batch written there, never Missing below the bound {Bound(state)}; LL-I9: Dropped only once the reclamation began."),
            judgment).SameState();
    }

    private static ExpectedOutcome RangeReadOutcome(LedgerRangeRead request, LedgerState state, LedgerJudgment judgment)
    {
        var written = Written(state, request.Slot);
        var fits = written is not null && (long)request.Offset + request.Length <= written.Length;
        return Judged(
            outcome => outcome.Kind == LedgerOutcomeKind.RangeRefused
                ? written is not null && !fits
                : ReadAnswers(outcome, state, request.Slot, bytes => fits && bytes.AsSpan().SequenceEqual(written.AsSpan(request.Offset, request.Length))),
            string.Create(CultureInfo.InvariantCulture, $"A byte-range read of slot {request.Slot} returns bytes [{request.Offset}, {(long)request.Offset + request.Length}) of the batch written there, refuses a range past it, and otherwise answers as the slot's read does (LL-I1, LL-I6, LL-I9)."),
            judgment).SameState();
    }

    private static ExpectedOutcome ScanOutcome(LedgerScan request, LedgerState state, LedgerJudgment judgment)
    {
        var bound = Bound(state);
        var ceiling = Ceiling(state);
        return Judged(
                outcome => outcome.Kind == LedgerOutcomeKind.Scanned
                    && outcome.Value >= bound
                    && outcome.Value <= ceiling
                    && outcome.Batches is { } batches
                    && batches.Count == Math.Max(0, outcome.Value - request.From)
                    && batches.Select((batch, index) => batch is null
                            ? state.Reclaiming
                            : Written(state, request.From + index) is { } written && batch.AsSpan().SequenceEqual(written))
                        .All(static holds => holds),
                string.Create(CultureInfo.InvariantCulture, $"LL-I6, LL-I7: a scan from {request.From} reads below a bound from {bound} to {ceiling}, each slot's batch written there, never Missing; LL-I9: Dropped only once the reclamation began."),
                judgment)
            .ThenState<LedgerState>(static (outcome, next) => next.Reported = Math.Max(next.Reported, outcome.Value), static () => new LedgerOutcome(LedgerOutcomeKind.Scanned, Batches: []));
    }

    private static ExpectedOutcome CheckOutcome(LedgerCheck request, LedgerState state, LedgerJudgment judgment)
    {
        var floor = request.Elsewhere ? 0 : Bound(state);
        var ceiling = Ceiling(state);
        var stopped = request.Elsewhere ? state.Stopped : state.Stopped || state.Sealed;
        return Judged(
                outcome => outcome.Kind == LedgerOutcomeKind.Tail
                    && outcome.IsSealed == state.Sealed
                    && outcome.Value >= floor
                    && outcome.Value <= ceiling
                    && LifetimeMatches(outcome, request.Elsewhere, stopped),
                string.Create(CultureInfo.InvariantCulture, $"LL-I4, LL-I5: the check reports sealed {state.Sealed}, the reclamation counting as a seal; LL-I7: its tail lies from {floor} to {ceiling}; LP-I14: the primary's check leaves the lifetime stopped once it finds the ledger sealed, and another process's leaves it as it was."),
                judgment)
            .ThenState<LedgerState>(
                (outcome, next) =>
                {
                    next.Reported = Math.Max(next.Reported, outcome.Value);
                    next.Stopped |= !request.Elsewhere && next.Sealed;
                },
                static () => new LedgerOutcome(LedgerOutcomeKind.Tail));
    }

    /// <summary>
    /// LP-I11, LL-I7: a learn answers a contiguous tail at least what its process knew and at most every candidate end,
    /// and changes nothing else: it neither seals nor stops the lifetime.
    /// </summary>
    private static ExpectedOutcome LearnOutcome(LedgerLearn request, LedgerState state, LedgerJudgment judgment)
    {
        var floor = request.Elsewhere ? 0 : Bound(state);
        var ceiling = Ceiling(state);
        return Judged(
                outcome => outcome.Kind == LedgerOutcomeKind.Learned
                    && outcome.Value >= floor
                    && outcome.Value <= ceiling
                    && outcome.LifetimeStopped == state.Stopped,
                string.Create(CultureInfo.InvariantCulture, $"LP-I11, LL-I7: the learn answers a tail from {floor} to {ceiling}, and leaves the lifetime as it was."),
                judgment)
            .ThenState<LedgerState>(static (outcome, next) => next.Reported = Math.Max(next.Reported, outcome.Value), static () => new LedgerOutcome(LedgerOutcomeKind.Learned));
    }

    private static ExpectedOutcomes RecoverOutcome(LedgerRecover request, LedgerState state, LedgerJudgment judgment)
    {
        var lowest = Math.Max(state.Completed, state.Reported);
        var highest = WrittenEnd(state);
        var stopped = !request.Elsewhere || state.Stopped;
        var recovered = Judged(
                outcome => outcome.Kind == LedgerOutcomeKind.End
                    && outcome.Value >= lowest
                    && outcome.Value <= highest
                    && LifetimeMatches(outcome, request.Elsewhere, stopped)
                    && !state.Reclaimed,
                string.Create(CultureInfo.InvariantCulture, $"LL-I2, LL-I3, LL-I7: the recovery returns a candidate end from {lowest}, the completed prefix and every tail reported, to {highest}, below which every slot holds its batch; LP-I14: the writer's recovery stops the lifetime."),
                judgment)
            .ThenState<LedgerState>(
                (outcome, next) =>
                {
                    next.Sealed = true;
                    next.End = next.End >= 0 ? next.End : outcome.Value;
                    next.Stopped |= !request.Elsewhere;
                },
                static () => new LedgerOutcome(LedgerOutcomeKind.End));
        if (!state.Reclaiming)
        {
            return recovered;
        }

        var dropped = Judged(
                outcome => outcome.Kind == LedgerOutcomeKind.Dropped && LifetimeMatches(outcome, request.Elsewhere, stopped),
                "LL-I9: a recovery which meets the reclamation answers Dropped.",
                judgment)
            .ThenState<LedgerState>(next => next.Stopped |= !request.Elsewhere);
        return Expect.OneOf(recovered, dropped);
    }

    private static ExpectedOutcome ObserveOutcome(LedgerState state, LedgerJudgment judgment) =>
        Judged(
                static outcome => outcome.Kind == LedgerOutcomeKind.Observed,
                string.Create(CultureInfo.InvariantCulture, $"The primary's process learns the recorded T, {state.End}, which its front carries as its acknowledged prefix from then on."),
                judgment)
            .ThenState<LedgerState>(static next => next.Recorded = true);

    private static ExpectedOutcome DropOutcome(LedgerDrop request, LedgerJudgment judgment) =>
        Judged(
                static outcome => outcome.Kind == LedgerOutcomeKind.Reclaimed,
                "LL-I9: the map revoked the closed ledger, so its storage is reclaimed.",
                judgment)
            .ThenState<LedgerState>(next =>
            {
                next.Reclaiming = true;
                next.Reclaimed |= request.Every;
            });
}

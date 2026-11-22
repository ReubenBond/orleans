using System.Globalization;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// What a recording of a <c>LedgerQuorum</c>'s replica requests shows of one call (ledger-protocol.md §8.2, §8.3).
/// </summary>
/// <param name="MaxCarried">
/// The highest contiguous tail any replica request the call sent carried, or <c>-1</c> when it sent none.
/// </param>
/// <param name="KnownBefore">The known contiguous tail of the call's <c>Ledger</c> before the call.</param>
/// <param name="KnownAfter">The known contiguous tail of the call's <c>Ledger</c> after the call.</param>
/// <param name="C">
/// A recovery's <c>c</c>: the greatest of its known contiguous tail when it began and the tails of the first
/// <c>N − Q + 1</c> seal answers, in the order the recovery handled them. That order may differ from the one the recording
/// shows: the recording stamps an answer before the layers above it deliver it, <c>Task.WhenAny</c> picks among answers
/// already complete in its set's order, and a transport may reorder them too.
/// </param>
/// <param name="StoppedFirst">Whether the writer's lifetime was stopped before the call's first seal request.</param>
/// <param name="AskedOtherNodes">Whether the call sent a request to a replica on another node than its process's.</param>
internal sealed record LedgerRecording(
    long MaxCarried = -1,
    long KnownBefore = 0,
    long KnownAfter = 0,
    long C = 0,
    bool StoppedFirst = false,
    bool AskedOtherNodes = false)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"carried {MaxCarried}, known {KnownBefore} -> {KnownAfter}, c {C}{(StoppedFirst ? ", stopped first" : string.Empty)}{(AskedOtherNodes ? ", asked other nodes" : string.Empty)}");
}

/// <summary>
/// The quorum's recording oracle (ledger-protocol.md §8.2, §8.3): what a recording of a <c>LedgerQuorum</c>'s replica
/// requests must show of each answer <see cref="LedgerStorageSpec"/> allows, and the answers the quorum gives where the
/// contract leaves a choice.
/// </summary>
/// <remarks>
/// Every tail a replica request carries is at most the completed prefix while the ledger is open, and at most the recorded
/// <c>T</c>, which may exceed the completed prefix, once the process learned it (LP-I11); a call which acquires nothing
/// sends nothing. A recovery raises its process's known contiguous tail to at most the greater of its prior value and
/// its <c>c</c>, never from its end, and <c>c</c> is at most the end; the writer's process stops its lifetime before the
/// recovery's first seal request (LP-I6, LP-I11, LP-I14). The primary's check reports exactly its known contiguous tail,
/// and another process's at most the primary's bound (LP-I9); learning the recorded <c>T</c> raises the known contiguous
/// tail to it. A learn asks only its own node's replica: the primary's reports exactly its known contiguous tail, and
/// another process's at most the primary's bound (LP-I11, LR-I6). A read at or above the bound reports the batch some replica holds, or <c>Missing</c> at <c>Q</c> answers
/// (ledger-protocol.md §8.3), until the reclamation begins.
/// </remarks>
internal static class LedgerQuorumOracle
{
    /// <summary>
    /// Returns what the recording of a call of <paramref name="request"/> in <paramref name="state"/> must show.
    /// </summary>
    internal static LedgerJudgment Judge(object request, LedgerState state)
    {
        var bound = LedgerStorageSpec.Bound(state);
        var carried = string.Create(CultureInfo.InvariantCulture, $" LP-I11: no replica request carries a tail above {bound}.");
        bool Carries(LedgerOutcome outcome) => outcome.Recording is { } recording && recording.MaxCarried <= bound;
        bool SendsNothing(LedgerOutcome outcome) => outcome.Recording is { MaxCarried: -1 };
        return request switch
        {
            LedgerAppend or LedgerAppendUnanswered or LedgerAcquire => new(
                outcome => outcome.Kind is LedgerOutcomeKind.Refused or LedgerOutcomeKind.Cancelled || outcome is { Kind: LedgerOutcomeKind.Unresolved, Value: -1 }
                    ? SendsNothing(outcome)
                    : Carries(outcome),
                carried + " A call which acquires no slot sends nothing."),
            LedgerSlotRead read => new(
                outcome => Carries(outcome) && FindsWhatWasWritten(outcome, state, read.Slot),
                carried + string.Create(CultureInfo.InvariantCulture, $" §8.3: before the reclamation the quorum's read of slot {read.Slot} reports the batch written there, Missing where none was, and either where a sealed append may have landed on an unsealed minority.")),
            LedgerRangeRead read => new(
                outcome => Carries(outcome) && (outcome.Kind == LedgerOutcomeKind.RangeRefused || FindsWhatWasWritten(outcome, state, read.Slot)),
                carried + string.Create(CultureInfo.InvariantCulture, $" §8.3: before the reclamation the quorum's read of slot {read.Slot} reports the batch written there, Missing where none was, and either where a sealed append may have landed on an unsealed minority.")),
            LedgerScan => new(Carries, carried),
            LedgerCheck { Elsewhere: false } => new(
                outcome => Carries(outcome) && outcome.Value == bound,
                carried + string.Create(CultureInfo.InvariantCulture, $" LP-I9: the primary's check reports its known contiguous tail, {bound}.")),
            LedgerCheck => new(
                outcome => Carries(outcome) && outcome.Value <= bound,
                carried + string.Create(CultureInfo.InvariantCulture, $" LP-I9, LR-I6: another process's check learns a tail at most the primary's, {bound}.")),
            LedgerLearn { Elsewhere: false } => new(
                outcome => Carries(outcome) && outcome.Recording is { AskedOtherNodes: false } && outcome.Value == bound,
                carried + string.Create(CultureInfo.InvariantCulture, $" §8.2: the learn asks only its node's replica; LP-I11, LR-I6: the primary's learn reports its known contiguous tail, {bound}.")),
            LedgerLearn => new(
                outcome => Carries(outcome) && outcome.Recording is { AskedOtherNodes: false } && outcome.Value <= bound,
                carried + string.Create(CultureInfo.InvariantCulture, $" §8.2: the learn asks only its node's replica; LP-I11, LR-I6: another process's learn reports a tail at most the primary's, {bound}.")),
            LedgerRecover recover => new(
                outcome => Carries(outcome)
                    && outcome.Recording is { } recording
                    && (recover.Elsewhere || recording.StoppedFirst)
                    && (outcome.Kind != LedgerOutcomeKind.End
                        || recording.KnownBefore <= recording.KnownAfter
                            && recording.KnownAfter <= Math.Max(recording.KnownBefore, recording.C)
                            && recording.C <= outcome.Value),
                carried + " LP-I6, LP-I11: the recovery raises its known contiguous tail to at most the greater of its prior value and c, which is at most its end, never from the end; LP-I14: the writer's recovery stops the lifetime before its first seal."),
            LedgerObserve => new(
                outcome => outcome.Recording is { } recording && recording.KnownAfter == state.End,
                string.Create(CultureInfo.InvariantCulture, $" LP-I11: learning the recorded T {state.End} raises the known contiguous tail to it.")),
            LedgerDrop => LedgerJudgment.None,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    private static bool FindsWhatWasWritten(LedgerOutcome outcome, LedgerState state, long slot) =>
        state.Reclaiming
        || LedgerStorageSpec.IsPartial(state, slot)
        || outcome.Kind == (LedgerStorageSpec.Written(state, slot) is null ? LedgerOutcomeKind.Missing : LedgerOutcomeKind.Batch);
}

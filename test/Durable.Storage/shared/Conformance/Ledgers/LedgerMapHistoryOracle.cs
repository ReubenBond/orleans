using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// One submission to a ledger map committer: the change, the committer it was submitted to, named as the register names
/// that committer's writes, the register's epoch when it was submitted, its answer, an outcome or the exception which
/// left it unknown, and when the answer arrived, as the number of writes the register had seen dispatched by then and,
/// when recorded, as the number of writes which had landed by then.
/// </summary>
internal sealed record LedgerMapSubmission(
    LedgerMapChange Change,
    int Committer,
    ulong SubmittedAt,
    LedgerMapCommitOutcome? Outcome,
    Exception? Failure = null,
    long AnsweredAt = long.MaxValue,
    ulong? LandedAt = null)
{
    /// <summary>Gets the outcome's case, or <see langword="null"/> when the submission failed.</summary>
    public object? Value => Outcome?.Value;
}

/// <summary>
/// The committer's conformance oracle (ledgers.md §2.6, §10): it keeps the register's values, one per epoch, with the
/// dispatch order and the writer of the write which produced each, and judges each answer against the values the
/// register held since the change's submission, by <see cref="LedgerMapReference"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Applied(e)</c> holds the goal at <c>e</c>; <c>Superseded(e)</c> finds the goal absent and the rule failed at
/// <c>e</c>, and the goal at no later epoch; <c>Rejected(r)</c> is a defect, for <c>r</c> or a rule before it in its
/// chain, at some epoch since the submission; and <c>Refused</c> is an install too large for the register's value limit
/// at some epoch since the submission, or at its own place in a batch the refusing committer wrote since: the fold
/// decides each change once, against the result of the batch's changes submitted before it, and a later change of the
/// batch, such as a floor raise past the install's <c>ChosenAt</c> or a drop, can leave the written value one where the
/// install's rule fails or it fits. Each epoch an answer names is one the register held at or after the submission,
/// and, where the answer records the writes landed when it arrived, one which had landed by then (conventions.md §13),
/// so an answer from a value from before it, or from a write which never landed or landed later, fails. The fold
/// decides a rejection against the result of the batch's earlier changes, a value later than any the register held
/// before the write, and closes, drops, and <c>NextLedgerId</c> only advance; so the rule it names may come later in
/// its chain than the one the register shows: a drop's never installed, then not closed, then blocked by its lineage,
/// and an install's closing a ledger never installed, then leaving a gap.
/// </para>
/// <para>
/// A negative answer, <c>Superseded</c>, <c>Rejected</c>, or <c>Refused</c>, is revoked when a value produced after the
/// submission, by a write the answering committer dispatched before the answer arrived, holds the change's goal: a write
/// in flight or held when the committer answered has applied the change. A write dispatched after the answer, such as a
/// resubmission's, may apply it, and so may another committer's, of which the answering committer knows nothing. An
/// exception leaves the outcome unknown and asserts nothing.
/// </para>
/// </remarks>
internal static class LedgerMapHistoryOracle
{
    /// <summary>Returns a description of each answer the register's history contradicts.</summary>
    /// <param name="history">The register's values: the absent register's, then each applied write's, in epoch order.</param>
    /// <param name="dispatchedAt">
    /// For each value of <paramref name="history"/>, the number of writes dispatched before the write which produced it;
    /// −1 for the absent register's.
    /// </param>
    /// <param name="writtenBy">
    /// For each value of <paramref name="history"/>, the committer whose write produced it, as
    /// <see cref="LedgerMapSubmission.Committer"/> names it; −1 for the absent register's or another writer's.
    /// </param>
    /// <param name="submissions">The submissions and their answers, in the order each committer received them.</param>
    /// <param name="maxValueLength">The register's value limit, past which an install is refused, when the caller checks refusals against it.</param>
    public static IReadOnlyList<string> Violations(IReadOnlyList<LedgerMapValue> history, IReadOnlyList<long> dispatchedAt, IReadOnlyList<int> writtenBy, IEnumerable<LedgerMapSubmission> submissions, int? maxValueLength = null)
    {
        Assert.Equal(history.Count, dispatchedAt.Count);
        Assert.Equal(history.Count, writtenBy.Count);
        var violations = new List<string>();
        var all = submissions.ToList();
        foreach (var submission in all)
        {
            var change = submission.Change;
            string Kind(ulong epoch) => LedgerMapReference.Decide(history[(int)epoch], change, int.MaxValue).Kind;
            var since = Enumerable.Range((int)submission.SubmittedAt, Math.Max(0, history.Count - (int)submission.SubmittedAt)).Select(static epoch => (ulong)epoch).ToArray();
            var revoked = since.Any(epoch => epoch > submission.SubmittedAt && writtenBy[(int)epoch] == submission.Committer && dispatchedAt[(int)epoch] < submission.AnsweredAt && Kind(epoch) == "goal");
            if (submission.LandedAt is { } landed && DecidedEpoch(submission.Outcome?.Value) is { } decided && decided > landed)
            {
                violations.Add($"{change.Value} was answered from epoch {decided} when {landed} writes had landed: an answer left before its value was durable.");
            }

            switch (submission.Outcome?.Value)
            {
                case null:
                    if (submission.Failure is null)
                    {
                        violations.Add($"{change.Value} has neither an outcome nor an exception.");
                    }

                    break;
                case LedgerMapCommitOutcome.Applied applied when !since.Contains(applied.Epoch):
                    violations.Add($"{change.Value} was applied at epoch {applied.Epoch}, which the register did not hold since its submission at {submission.SubmittedAt}.");
                    break;
                case LedgerMapCommitOutcome.Applied applied when Kind(applied.Epoch) != "goal":
                    violations.Add($"{change.Value} was applied at epoch {applied.Epoch}, whose value lacks its goal.");
                    break;
                case LedgerMapCommitOutcome.Superseded superseded when !since.Contains(superseded.Epoch):
                    violations.Add($"{change.Value} was superseded at epoch {superseded.Epoch}, which the register did not hold since its submission at {submission.SubmittedAt}.");
                    break;
                case LedgerMapCommitOutcome.Superseded superseded when Kind(superseded.Epoch) != "rule-fails":
                    violations.Add($"{change.Value} was superseded at epoch {superseded.Epoch}, against which it {Kind(superseded.Epoch)}.");
                    break;
                case LedgerMapCommitOutcome.Superseded superseded when revoked || since.Any(epoch => epoch > superseded.Epoch && Kind(epoch) == "goal"):
                    violations.Add($"{change.Value} was superseded at epoch {superseded.Epoch}, but applied later.");
                    break;
                case LedgerMapCommitOutcome.Rejected rejected when !since.Any(epoch => LedgerMapReference.Decide(history[(int)epoch], change, int.MaxValue) is { Kind: "defect", Reason: { } shown } && Rank(shown) <= Rank(rejected.Reason)):
                    violations.Add($"{change.Value} was rejected for {rejected.Reason}, but no value since its submission shows it or a rule before it in its chain.");
                    break;
                case LedgerMapCommitOutcome.Rejected rejected when revoked:
                    violations.Add($"{change.Value} was rejected for {rejected.Reason}, but a write dispatched before the answer applied it.");
                    break;
                case LedgerMapCommitOutcome.Refused when change.Value is not LedgerMapChange.Install:
                    violations.Add($"{change.Value} was refused, but it is no install.");
                    break;
                case LedgerMapCommitOutcome.Refused when maxValueLength is { } limit
                    && !since.Any(epoch => LedgerMapReference.Decide(history[(int)epoch], change, limit).Kind == "too-large")
                    && !TooLargeWithinAWrite(history, writtenBy, all, submission, limit):
                    violations.Add($"{change.Value} was refused, but neither a value since its submission nor its own place in a batch its committer wrote since leaves it too large for {maxValueLength} bytes.");
                    break;
                case LedgerMapCommitOutcome.Refused when revoked:
                    violations.Add($"{change.Value} was refused, but a write dispatched before the answer applied it.");
                    break;
                default:
                    break;
            }
        }

        return violations;
    }

    /// <summary>Returns the epoch an answer names, the deciding value's for <c>Applied</c> and <c>Superseded</c>, or <see langword="null"/>.</summary>
    private static ulong? DecidedEpoch(object? outcome) => outcome switch
    {
        LedgerMapCommitOutcome.Applied applied => applied.Epoch,
        LedgerMapCommitOutcome.Superseded superseded => superseded.Epoch,
        _ => null,
    };

    /// <summary>
    /// Returns whether a write the refusing committer landed after <paramref name="refused"/>'s submission leaves its
    /// change too large at its own place in the write's batch, where the committer decided it: over the write's basis,
    /// the value before it, which a conditional write lands only over, folded in submission order through the changes
    /// it carried which were submitted before <paramref name="refused"/>, those of the committer's submissions whose
    /// goal it alone shows.
    /// </summary>
    private static bool TooLargeWithinAWrite(
        IReadOnlyList<LedgerMapValue> history, IReadOnlyList<int> writtenBy, IReadOnlyList<LedgerMapSubmission> submissions, LedgerMapSubmission refused, int limit)
    {
        for (var epoch = (int)refused.SubmittedAt + 1; epoch < history.Count; epoch++)
        {
            if (writtenBy[epoch] != refused.Committer)
            {
                continue;
            }

            var (basis, written) = (history[epoch - 1], history[epoch]);
            var value = basis;
            foreach (var carried in submissions)
            {
                if (ReferenceEquals(carried, refused))
                {
                    if (LedgerMapReference.Decide(value, refused.Change, limit).Kind == "too-large")
                    {
                        return true;
                    }

                    break;
                }

                if (carried.Committer == refused.Committer
                    && LedgerMapReference.Decide(basis, carried.Change, int.MaxValue).Kind != "goal"
                    && LedgerMapReference.Decide(written, carried.Change, int.MaxValue).Kind == "goal"
                    && LedgerMapReference.Decide(value, carried.Change, int.MaxValue) is { Kind: "applies", Result: { } next })
                {
                    value = next;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a rejection's place in its change's chain of rules, which a map passes through as it advances: a drop's
    /// ledger is never installed, then open, then blocked by its lineage; an install's closed ledger is never installed,
    /// then its opens leave a gap.
    /// </summary>
    private static int Rank(LedgerMapRejection reason) => reason switch
    {
        LedgerMapRejection.DropNeverInstalled or LedgerMapRejection.InstallClosesNeverInstalled => 0,
        LedgerMapRejection.DropNotClosed or LedgerMapRejection.OpensLeaveGap => 1,
        LedgerMapRejection.DropBlockedByLineage => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "An unknown rejection."),
    };
}

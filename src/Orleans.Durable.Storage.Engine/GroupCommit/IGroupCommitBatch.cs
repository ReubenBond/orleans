namespace Orleans.Durable.Storage.Engine.GroupCommit;

/// <summary>
/// One batch's decision, which <see cref="IGroupCommitRules{TValue, TVersion, TChange, TOutcome, TReply}.Fold"/> makes:
/// the value its write holds, whether that write carries each change, and each change's answer from the value which
/// decides it. Its members are pure, and address the batch's changes by their position in it.
/// </summary>
/// <typeparam name="TValue">The decoded value.</typeparam>
/// <typeparam name="TVersion">The store's version of the value.</typeparam>
/// <typeparam name="TOutcome">A change's answer.</typeparam>
public interface IGroupCommitBatch<TValue, TVersion, TOutcome>
    where TValue : class
    where TVersion : class
{
    /// <summary>
    /// Gets the value the batch's write holds, at the version after the basis's, or <see langword="null"/> when the batch
    /// writes nothing. A write which carries no change is a fence (<see cref="IGroupCommitRules{TValue, TVersion, TChange, TOutcome, TReply}.Fold"/>).
    /// </summary>
    TValue? Written { get; }

    /// <summary>Returns whether the batch's write carries, and so applies, the change at <paramref name="index"/>.</summary>
    bool Carries(int index);

    /// <summary>
    /// Answers the change at <paramref name="index"/> from <paramref name="value"/>, which decides it: the value the batch's
    /// write produced, or a value from after every submission of the batch, over which it writes nothing.
    /// </summary>
    GroupCommitAnswer<TOutcome> Settle(int index, TValue value);

    /// <summary>
    /// Answers the change at <paramref name="index"/>, which a write whose outcome is unknown carried, from
    /// <paramref name="read"/>, the value of the read which ends the batch: applied, when it holds the change's goal.
    /// </summary>
    /// <returns>The applied answer, or <see langword="null"/> when <paramref name="read"/> lacks the goal, which leaves the
    /// change's outcome unknown, since the write may still land.</returns>
    GroupCommitAnswer<TOutcome>? Applied(int index, TValue read);

    /// <summary>
    /// Answers the change at <paramref name="index"/>, which a write whose outcome is unknown did not carry, from
    /// <paramref name="read"/>, the value of the read which ends the batch.
    /// </summary>
    /// <param name="index">The change's position in the batch.</param>
    /// <param name="read">The value read.</param>
    /// <param name="heldWriteMayLand">Whether the held write may still land over <paramref name="read"/>.</param>
    /// <returns>The answer <paramref name="read"/> decides and no later value revokes; or <see langword="null"/> when the
    /// read leaves the change undecided, which fails it with the write's exception.</returns>
    GroupCommitAnswer<TOutcome>? AfterUnknownOutcome(int index, TValue read, bool heldWriteMayLand);

    /// <summary>
    /// Returns the invariant which <see cref="Written"/> breaks when written over <paramref name="before"/>, as a message
    /// naming the write and the invariant; or <see langword="null"/>. The committer writes no such value, and fails the
    /// batch as a defect.
    /// </summary>
    string? Violation(TValue before);

    /// <summary>
    /// Observes the batch's write, which applied: the store holds <see cref="Written"/> at <paramref name="version"/>. It
    /// never throws; the committer calls it once the answers are decided, before it releases them.
    /// </summary>
    void OnApplied(TVersion version);
}

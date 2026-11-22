namespace Orleans.Durable.Storage.Engine.GroupCommit;

/// <summary>
/// A change's answer from a group commit's rules (conventions.md §15): its outcome, or the exception which answers it.
/// </summary>
/// <typeparam name="TOutcome">The change's outcome.</typeparam>
public readonly union GroupCommitAnswer<TOutcome>(GroupCommitAnswer<TOutcome>.Answered, GroupCommitAnswer<TOutcome>.Failed)
{
    /// <summary>The change is answered <paramref name="Outcome"/>.</summary>
    /// <param name="Outcome">The outcome.</param>
    public sealed record Answered(TOutcome Outcome);

    /// <summary>The change is answered with <paramref name="Failure"/>.</summary>
    /// <param name="Failure">The exception which answers it.</param>
    public sealed record Failed(Exception Failure);
}

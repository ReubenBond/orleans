namespace Orleans.Durable.Storage.Engine.GroupCommit;

/// <summary>
/// The outcome of a group-committed change is unknown: the write which carried it may have applied, or may still apply,
/// and submitting the same change again decides it (conventions.md §6). It is an <see cref="IOException"/>, and a
/// submitter treats it as an unknown outcome, as it treats the store's own failures: it submits the change again.
/// </summary>
public sealed class GroupCommitOutcomeUnknownException : IOException
{
    /// <summary>Initializes a new instance of the <see cref="GroupCommitOutcomeUnknownException"/> class.</summary>
    public GroupCommitOutcomeUnknownException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GroupCommitOutcomeUnknownException"/> class.</summary>
    /// <param name="message">The message.</param>
    public GroupCommitOutcomeUnknownException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GroupCommitOutcomeUnknownException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">What left the outcome unknown.</param>
    public GroupCommitOutcomeUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

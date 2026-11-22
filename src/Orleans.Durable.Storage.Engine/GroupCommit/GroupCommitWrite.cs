namespace Orleans.Durable.Storage.Engine.GroupCommit;

/// <summary>
/// The definite outcome of a conditional write of a group-committed value (conventions.md §15): <see cref="Applied"/>
/// with the new version; <see cref="Mismatch"/>, after which the write never lands; or <see cref="Refused"/>, which wrote
/// nothing and fails the batch. A write whose outcome is unknown throws instead.
/// </summary>
/// <typeparam name="TVersion">The value's version.</typeparam>
public readonly union GroupCommitWrite<TVersion>(GroupCommitWrite<TVersion>.Applied, GroupCommitWrite<TVersion>.Mismatch, GroupCommitWrite<TVersion>.Refused)
    where TVersion : class
{
    /// <summary>The write applied: the store holds its value, durably, at <paramref name="Version"/>.</summary>
    /// <param name="Version">The version after the write.</param>
    public sealed record Applied(TVersion Version);

    /// <summary>The store held another version; an earlier attempt of the write may have applied.</summary>
    public sealed record Mismatch;

    /// <summary>
    /// The store refused the write and wrote nothing, as an object store refuses content which changed after its checksum
    /// was taken: the batch fails with <paramref name="Failure"/>, and no read follows.
    /// </summary>
    /// <param name="Failure">The store's refusal, which answers every change only this attempt carried.</param>
    public sealed record Refused(Exception Failure);
}

namespace Orleans.Durable.Storage.Engine.GroupCommit;

/// <summary>
/// A read of a group-committed value (conventions.md §15): the value and its version, or the corruption of a value the
/// codec rejects, which every later read meets too. A read which fails otherwise throws.
/// </summary>
/// <typeparam name="TValue">The decoded value.</typeparam>
/// <typeparam name="TVersion">Its version; <see langword="null"/> while the value is absent.</typeparam>
public readonly union GroupCommitRead<TValue, TVersion>(GroupCommitRead<TValue, TVersion>.Found, GroupCommitRead<TValue, TVersion>.Corrupt)
    where TValue : class
    where TVersion : class
{
    /// <summary>The value read, the empty value while it is absent, and its version.</summary>
    /// <param name="Current">The value.</param>
    /// <param name="Version">Its version, <see langword="null"/> while absent.</param>
    public sealed record Found(TValue Current, TVersion? Version);

    /// <summary>The stored bytes break the codec's rules.</summary>
    /// <param name="Failure">The exception which reports the corruption to submitters: the rejection, or the layer's own
    /// corruption exception around it.</param>
    public sealed record Corrupt(Exception Failure);
}
